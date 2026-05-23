using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

/// <summary>
/// Deterministic, no-dependency hardware profiler (Pass 25 / D1).
/// Inspects OS-reported cues — CPU core count, physical RAM, OS
/// identifier, GPU markers under <c>NVIDIA_VISIBLE_DEVICES</c> /
/// <c>CUDA_VISIBLE_DEVICES</c> / ROCm visibility hints / driver-marker
/// fallbacks, bounded Linux procfs probes, and Windows display-adapter
/// registry strings - and derives a
/// <see cref="DuoHardwareTier"/> recommendation plus a short
/// <see cref="HardwareProfile"/> that operators and AI agents can
/// inspect to decide which role bindings are realistic.
///
/// <para>Never launches tools, never calls the GPU directly, never
/// talks to the network. On unknown environments it falls back to
/// <see cref="DuoHardwareTier.Standard"/> and sets
/// <see cref="HardwareProfile.DetectionConfidence"/> to
/// <c>"low"</c>. Safe to call during host startup.</para>
///
/// <para>An operator can always pin the tier via
/// <c>PalLLM:Hardware:ForceTier</c> — the override surfaces on the
/// profile as <see cref="HardwareProfile.OverrideApplied"/>, so the
/// /api/describe self-description can show both the detected and the
/// forced tier.</para>
/// </summary>
public static class HardwareProfiler
{
    private const string GpuArchitectureHintEnvVar = "PALLLM_GPU_ARCHITECTURE";
    private const int LinuxNvidiaInfoMaxBytes = 8_192;
    private const int LinuxMemInfoMaxBytes = 8_192;
    private const int WindowsGpuRegistryMaxRootKeys = 64;
    private const int WindowsGpuRegistryMaxAdapterKeys = 16;
    private const string WindowsVideoRegistryPath = @"SYSTEM\CurrentControlSet\Control\Video";

    private static readonly string[] WindowsGpuRegistryValueNames =
    [
        "HardwareInformation.AdapterString",
        "HardwareInformation.ChipType",
        "DriverDesc",
        "Device Description",
    ];

    // Cache slot — hardware posture does not change during a process lifetime
    // (cores, RAM, GPU driver presence are all boot-stable). Capturing is cheap
    // but the OS calls + driver-marker file probes are ~5-20ms together; caching
    // removes that from every /api/hardware and every advisor call.
    // Keyed by forceTier so the override path also caches correctly. Thread-safe
    // by volatile-read + atomic swap — a brief double-compute on cold start is
    // harmless (the result is identical).
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromMinutes(5);
    private static volatile HardwareProfileCacheEntry? _cached;

    /// <summary>
    /// Capture a <see cref="HardwareProfile"/> describing the current box.
    /// Always returns a fresh snapshot — for a cached variant that only recomputes
    /// every TTL window, use <see cref="CaptureCached(string?, TimeSpan?)"/>.
    /// </summary>
    /// <param name="forceTier">Optional explicit tier override (<c>PalLLM:Hardware:ForceTier</c>).</param>
    public static HardwareProfile Capture(string? forceTier = null)
    {
        int logicalCores = Math.Max(1, Environment.ProcessorCount);
        long totalRamBytes = GetTotalPhysicalMemory();
        int ramGigabytes = (int)Math.Round(totalRamBytes / 1024.0 / 1024.0 / 1024.0);

        string os = OperatingSystem.IsWindows() ? "windows"
                  : OperatingSystem.IsLinux() ? "linux"
                  : OperatingSystem.IsMacOS() ? "macos"
                  : "unknown";

        (bool gpuLikely, string gpuDetail) = DetectGpuPresence();
        (string gpuArch, bool fp4Likely, string archDetail) = DetectGpuArchitecture(gpuLikely);

        DuoHardwareTier detected = DeriveTier(logicalCores, ramGigabytes, gpuLikely);
        string detectionConfidence = gpuLikely || ramGigabytes >= 8 ? "high" : "medium";
        if (os == "unknown") { detectionConfidence = "low"; }

        DuoHardwareTier effective = detected;
        bool overrideApplied = false;
        if (!string.IsNullOrWhiteSpace(forceTier)
            && Enum.TryParse(forceTier.Trim(), ignoreCase: true, out DuoHardwareTier forced))
        {
            effective = forced;
            overrideApplied = true;
        }

        string recommendation = BuildRecommendation(effective, ramGigabytes, gpuLikely, fp4Likely, gpuArch);
        string recommendedQuant = BuildQuantizationRecommendation(gpuLikely, fp4Likely, gpuArch);

        return new HardwareProfile(
            OperatingSystem: os,
            LogicalCoreCount: logicalCores,
            PhysicalRamGigabytes: ramGigabytes,
            GpuLikelyPresent: gpuLikely,
            GpuDetectionDetail: gpuDetail,
            GpuArchitecture: gpuArch,
            GpuArchitectureDetail: archDetail,
            Fp4TensorCoresLikely: fp4Likely,
            RecommendedQuantization: recommendedQuant,
            DetectedTier: detected.ToString(),
            EffectiveTier: effective.ToString(),
            OverrideApplied: overrideApplied,
            DetectionConfidence: detectionConfidence,
            Recommendation: recommendation,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Memoised variant of <see cref="Capture"/> — recomputes at most once every
    /// <paramref name="cacheTtl"/> (default 5 minutes). Safe to call on hot paths
    /// (the endpoint handler for <c>GET /api/hardware</c>, the degradation
    /// advisor, any dashboard poll) without paying the OS-call cost repeatedly.
    /// The cached record itself is immutable — callers cannot mutate it.
    /// </summary>
    /// <param name="forceTier">Optional explicit tier override (<c>PalLLM:Hardware:ForceTier</c>). When this changes, the cache is invalidated.</param>
    /// <param name="cacheTtl">Optional TTL override. Defaults to 5 minutes.</param>
    public static HardwareProfile CaptureCached(string? forceTier = null, TimeSpan? cacheTtl = null)
    {
        TimeSpan ttl = cacheTtl ?? DefaultCacheTtl;
        HardwareProfileCacheEntry? snapshot = _cached;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot is not null
            && snapshot.ForceTier == forceTier
            && now - snapshot.CapturedAt < ttl)
        {
            return snapshot.Profile;
        }

        HardwareProfile fresh = Capture(forceTier);
        _cached = new HardwareProfileCacheEntry(fresh, forceTier, now);
        return fresh;
    }

    /// <summary>
    /// Invalidate the cache (used by tests or when the operator explicitly
    /// requests a re-probe). The next <see cref="CaptureCached"/> call will
    /// recompute from scratch.
    /// </summary>
    public static void InvalidateCache() => _cached = null;

    private sealed record HardwareProfileCacheEntry(
        HardwareProfile Profile,
        string? ForceTier,
        DateTimeOffset CapturedAt);

    private static long GetTotalPhysicalMemory()
    {
        if (OperatingSystem.IsWindows() && TryGetWindowsTotalPhysicalMemory(out long windowsBytes))
        {
            return windowsBytes;
        }

        if (OperatingSystem.IsLinux() && TryGetLinuxTotalPhysicalMemory(out long linuxBytes))
        {
            return linuxBytes;
        }

        // Last-resort fallback: GC.GetGCMemoryInfo is available on every
        // supported .NET version and reports the process-visible memory budget.
        // That is conservative under containers/job objects, which is safer
        // than over-promising model residency.
        try
        {
            var info = GC.GetGCMemoryInfo();
            if (info.TotalAvailableMemoryBytes > 0)
            {
                return info.TotalAvailableMemoryBytes;
            }
        }
        catch
        {
            // fall through
        }
        return 0L;
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetWindowsTotalPhysicalMemory(out long bytes)
    {
        bytes = 0;

        try
        {
            MemoryStatusEx status = new()
            {
                Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };

            if (GlobalMemoryStatusEx(ref status) && status.TotalPhysicalBytes > 0)
            {
                bytes = status.TotalPhysicalBytes > long.MaxValue
                    ? long.MaxValue
                    : (long)status.TotalPhysicalBytes;
                return true;
            }
        }
        catch
        {
            // Hardware capture must never block startup. Fall back below.
        }

        return false;
    }

    private static bool TryGetLinuxTotalPhysicalMemory(out long bytes)
    {
        bytes = 0;

        BoundedTextFileReader.TextReadResult readResult =
            BoundedTextFileReader.TryRead("/proc/meminfo", LinuxMemInfoMaxBytes);
        return readResult.Succeeded
            && TryParseLinuxMemTotalBytes(readResult.Text, out bytes);
    }

    internal static bool TryParseLinuxMemTotalBytes(string? memInfo, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(memInfo))
        {
            return false;
        }

        foreach (string rawLine in memInfo.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] parts = line["MemTotal:".Length..]
                .Trim()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0
                || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount)
                || amount <= 0)
            {
                return false;
            }

            string unit = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "kb";
            try
            {
                bytes = unit switch
                {
                    "b" or "byte" or "bytes" => amount,
                    "kb" or "kib" => checked(amount * 1024L),
                    "mb" or "mib" => checked(amount * 1024L * 1024L),
                    "gb" or "gib" => checked(amount * 1024L * 1024L * 1024L),
                    _ => 0,
                };
            }
            catch (OverflowException)
            {
                bytes = long.MaxValue;
            }

            return bytes > 0;
        }

        return false;
    }

    private static (bool present, string detail) DetectGpuPresence()
    {
        // Env-var cues that are set by common ML tooling (CUDA, ROCm, NVIDIA
        // container toolkit, Docker with GPU passthrough).
        string[] gpuEnvVars =
        [
            "CUDA_VISIBLE_DEVICES",
            "NVIDIA_VISIBLE_DEVICES",
            "HIP_VISIBLE_DEVICES",
            "ROCR_VISIBLE_DEVICES",
        ];
        foreach (string name in gpuEnvVars)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && !value.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return (true, $"{name}={value}");
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // DirectX / NVIDIA installs drop a canonical DLL in System32. The
            // file existence is a good-enough signal for the tier decision
            // without loading the DLL (which would fail on AMD systems).
            string system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string[] markers =
            [
                Path.Combine(system32, "nvml.dll"),
                Path.Combine(system32, "nvapi64.dll"),
                Path.Combine(system32, "amdxc64.dll"),
                Path.Combine(system32, "amdihk64.dll"),
            ];
            foreach (string marker in markers)
            {
                if (File.Exists(marker))
                {
                    return (true, Path.GetFileName(marker));
                }
            }
        }

        if (OperatingSystem.IsLinux())
        {
            string[] markers =
            [
                "/dev/nvidia0",
                "/dev/nvidiactl",
                "/dev/kfd", // AMD ROCm
                "/sys/class/drm/card0",
            ];
            foreach (string marker in markers)
            {
                if (File.Exists(marker))
                {
                    return (true, marker);
                }
            }
        }

        return (false, "no-gpu-markers-detected");
    }

    /// <summary>
    /// Detect the GPU architecture so the recommendation engine can suggest the
    /// right quantization format. Blackwell (RTX 50 / B100 / B200 / GB200) has
    /// native FP4 tensor cores; recommending NVFP4 + vLLM / TensorRT-LLM there
    /// gives 2x throughput vs FP8 and 4x vs FP16 with near-FP16 accuracy. On
    /// pre-Blackwell hardware NVFP4 is software-emulated and offers no win, so
    /// the recommendation falls back to the right format for that architecture.
    ///
    /// <para>We deliberately don't shell out (no nvidia-smi, no subprocess);
    /// this keeps the profiler hot-path-safe and embeddable. Detection sources
    /// in priority order:</para>
    /// <list type="number">
    ///   <item>Operator-set <c>PALLLM_GPU_ARCHITECTURE</c> env var (explicit;
    ///         examples: <c>blackwell</c>, <c>mi300</c>, <c>mi350</c>).</item>
    ///   <item>Linux <c>/proc/driver/nvidia/gpus/&lt;id&gt;/information</c> file
    ///         which lists the NVIDIA GPU model name without a subprocess.
    ///         Reads stay bounded so the hot path never trusts an arbitrarily
    ///         large pseudo-file.</item>
    ///   <item>Windows display-adapter registry strings under
    ///         <c>HKLM\SYSTEM\CurrentControlSet\Control\Video</c>, bounded by
    ///         key count and sanitized before surfacing.</item>
    ///   <item>Returns <c>"unknown"</c> when nothing pins it down; operators can
    ///         still set the env var explicitly.</item>
    /// </list>
    /// </summary>
    private static (string arch, bool fp4Likely, string detail) DetectGpuArchitecture(bool gpuLikely)
    {
        if (!gpuLikely)
        {
            return ("none", false, "no-gpu-detected");
        }

        // 1. Explicit operator hint always wins.
        string? hint = Environment.GetEnvironmentVariable(GpuArchitectureHintEnvVar);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            string normalized = NormalizeArchitectureHint(hint);
            return (normalized, IsFp4Capable(normalized), $"env:{GpuArchitectureHintEnvVar}={normalized}");
        }

        // 2. Linux: read the GPU model from /proc without a subprocess.
        if (OperatingSystem.IsLinux())
        {
            string? procFailureDetail = null;

            try
            {
                const string gpusRoot = "/proc/driver/nvidia/gpus";
                if (Directory.Exists(gpusRoot))
                {
                    foreach (string gpuDir in Directory.EnumerateDirectories(gpusRoot))
                    {
                        string infoPath = Path.Combine(gpuDir, "information");
                        if (!File.Exists(infoPath)) continue;

                        BoundedTextFileReader.TextReadResult readResult =
                            BoundedTextFileReader.TryRead(infoPath, LinuxNvidiaInfoMaxBytes);
                        if (!readResult.Succeeded || string.IsNullOrWhiteSpace(readResult.Text))
                        {
                            procFailureDetail ??= readResult.FailureCode switch
                            {
                                BoundedTextFileReader.TextReadFailureCode.Oversized => "proc:information-oversized",
                                BoundedTextFileReader.TextReadFailureCode.Unreadable => "proc:information-unreadable",
                                _ => null,
                            };
                            continue;
                        }

                        string content = readResult.Text;
                        (string arch, bool fp4) = ClassifyNvidiaModel(content);
                        if (arch != "unknown")
                        {
                            string sample = ExtractModelLine(content);
                            return (arch, fp4, $"proc:{sample}");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(procFailureDetail))
                    {
                        return ("unknown", false, procFailureDetail);
                    }
                }
            }
            catch
            {
                // /proc reads are usually safe; if a sandbox blocks them we just
                // fall through to "unknown" rather than throwing.
            }
        }

        // 3. Windows: read display-adapter registry strings without loading
        //    vendor DLLs or launching a subprocess.
        if (OperatingSystem.IsWindows())
        {
            (string arch, bool fp4, string detail) = DetectWindowsGpuArchitecture();
            if (arch != "unknown")
            {
                return (arch, fp4, detail);
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                return ("unknown", false, detail);
            }
        }

        // 4. Default - we can't tell. Operators can still pin the path with
        //    PALLLM_GPU_ARCHITECTURE=blackwell / hopper / ada / ampere / mi300.
        return ("unknown", false, "architecture-not-determinable-without-hint");
    }

    [SupportedOSPlatform("windows")]
    private static (string arch, bool fp4, string detail) DetectWindowsGpuArchitecture()
    {
        try
        {
            using RegistryKey? videoRoot = Registry.LocalMachine.OpenSubKey(WindowsVideoRegistryPath);
            if (videoRoot is null)
            {
                return ("unknown", false, "registry:video-key-missing");
            }

            string? firstUnclassifiedAdapter = null;
            int rootKeysVisited = 0;

            foreach (string rootKeyName in videoRoot.GetSubKeyNames())
            {
                if (++rootKeysVisited > WindowsGpuRegistryMaxRootKeys)
                {
                    break;
                }

                using RegistryKey? rootKey = videoRoot.OpenSubKey(rootKeyName);
                if (rootKey is null)
                {
                    continue;
                }

                (string arch, bool fp4, string? adapterText) = ClassifyWindowsRegistryKey(rootKey);
                if (arch != "unknown")
                {
                    return (arch, fp4, $"registry:{SanitizeProbeDetail(adapterText)}");
                }
                firstUnclassifiedAdapter ??= adapterText;

                int adapterKeysVisited = 0;
                foreach (string adapterKeyName in rootKey.GetSubKeyNames())
                {
                    if (++adapterKeysVisited > WindowsGpuRegistryMaxAdapterKeys)
                    {
                        break;
                    }

                    using RegistryKey? adapterKey = rootKey.OpenSubKey(adapterKeyName);
                    if (adapterKey is null)
                    {
                        continue;
                    }

                    (arch, fp4, adapterText) = ClassifyWindowsRegistryKey(adapterKey);
                    if (arch != "unknown")
                    {
                        return (arch, fp4, $"registry:{SanitizeProbeDetail(adapterText)}");
                    }
                    firstUnclassifiedAdapter ??= adapterText;
                }
            }

            if (!string.IsNullOrWhiteSpace(firstUnclassifiedAdapter))
            {
                return ("unknown", false, $"registry:unclassified:{SanitizeProbeDetail(firstUnclassifiedAdapter)}");
            }

            return ("unknown", false, "registry:no-adapter-strings");
        }
        catch
        {
            return ("unknown", false, "registry:unreadable");
        }
    }

    [SupportedOSPlatform("windows")]
    private static (string arch, bool fp4, string? adapterText) ClassifyWindowsRegistryKey(RegistryKey key)
    {
        foreach (string valueName in WindowsGpuRegistryValueNames)
        {
            string? adapterText = ConvertRegistryValueToText(key.GetValue(valueName));
            if (string.IsNullOrWhiteSpace(adapterText))
            {
                continue;
            }

            (string arch, bool fp4) = ClassifyGpuModel(adapterText);
            if (arch != "unknown")
            {
                return (arch, fp4, adapterText);
            }

            return ("unknown", false, adapterText);
        }

        return ("unknown", false, null);
    }

    private static string? ConvertRegistryValueToText(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case string[] values:
                return string.Join(' ', values);
            case byte[] bytes:
                return DecodeRegistryBytes(bytes);
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    private static string? DecodeRegistryBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        string unicode = Encoding.Unicode.GetString(bytes).TrimEnd('\0', ' ', '\t', '\r', '\n');
        if (ContainsUsefulText(unicode))
        {
            return unicode;
        }

        string utf8 = Encoding.UTF8.GetString(bytes).TrimEnd('\0', ' ', '\t', '\r', '\n');
        return ContainsUsefulText(utf8) ? utf8 : null;
    }

    private static bool ContainsUsefulText(string text)
    {
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    private static string SanitizeProbeDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "adapter-string-empty";
        }

        StringBuilder builder = new(capacity: Math.Min(detail.Length, 96));
        bool previousWasWhitespace = false;
        foreach (char c in detail)
        {
            if (builder.Length >= 96)
            {
                break;
            }

            if (char.IsControl(c) || char.IsWhiteSpace(c))
            {
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                    previousWasWhitespace = true;
                }
                continue;
            }

            builder.Append(c);
            previousWasWhitespace = false;
        }

        string sanitized = builder.ToString().Trim();
        return sanitized.Length == 0 ? "adapter-string-empty" : sanitized;
    }

    /// <summary>True when the named architecture has hardware FP4 tensor cores.</summary>
    private static bool IsFp4Capable(string archLowercase) =>
        archLowercase is "blackwell" or "blackwell-ultra" or "rubin";

    internal static string NormalizeArchitectureHint(string hintRaw)
    {
        string normalized = hintRaw.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "unknown";
        }

        if (TryMapAmdArchitectureHint(normalized, out string amdArchitecture))
        {
            return amdArchitecture;
        }

        (string gpuArchitecture, _) = ClassifyGpuModel(normalized);
        if (gpuArchitecture != "unknown")
        {
            return gpuArchitecture;
        }

        return normalized switch
        {
            "turing" => "turing-or-older",
            "rdna3" or "rdna4" or "gfx1100" or "gfx1101" or "gfx1102" or "gfx1200" or "gfx1201" => "rdna",
            _ => normalized,
        };
    }

    private static (string arch, bool fp4) ClassifyGpuModel(string modelStringRaw)
    {
        string normalized = modelStringRaw.Trim().ToLowerInvariant();

        if (TryMapAmdArchitectureHint(normalized, out string amdArchitecture))
        {
            return (amdArchitecture, false);
        }

        (string nvidiaArchitecture, bool nvidiaFp4) = ClassifyNvidiaModel(normalized);
        if (nvidiaArchitecture != "unknown")
        {
            return (nvidiaArchitecture, nvidiaFp4);
        }

        if (normalized.Contains("radeon", StringComparison.Ordinal)
            && (normalized.Contains("rx 9", StringComparison.Ordinal)
                || normalized.Contains("rx 7", StringComparison.Ordinal)
                || normalized.Contains("rx9", StringComparison.Ordinal)
                || normalized.Contains("rx7", StringComparison.Ordinal)
                || normalized.Contains("w7900", StringComparison.Ordinal)
                || normalized.Contains("w7800", StringComparison.Ordinal)))
        {
            return ("rdna", false);
        }

        return ("unknown", false);
    }

    /// <summary>
    /// Match common NVIDIA GPU model strings to a coarse architecture name.
    /// Conservative — only firms up well-known model families. Anything
    /// ambiguous returns "unknown" so the recommendation stays honest.
    /// </summary>
    private static (string arch, bool fp4) ClassifyNvidiaModel(string modelStringRaw)
    {
        string s = modelStringRaw.ToLowerInvariant();

        // Blackwell consumer (RTX 50 series, ~2025) and datacenter (B100 /
        // B200 / GB200, ~2024-25). All have FP4 tensor cores.
        if (s.Contains("rtx 50") || s.Contains("rtx5") || s.Contains(" 5090")
            || s.Contains(" 5080") || s.Contains(" 5070") || s.Contains(" 5060")
            || s.Contains("b100") || s.Contains("b200") || s.Contains("gb200")
            || s.Contains("blackwell"))
        {
            return ("blackwell", true);
        }

        // Hopper (H100, H200, GH200). FP8 native, no FP4.
        if (s.Contains("h100") || s.Contains("h200") || s.Contains("gh200")
            || s.Contains("hopper"))
        {
            return ("hopper", false);
        }

        // Ada Lovelace (RTX 40 series, L40, L4). FP8 native, no FP4.
        if (s.Contains("rtx 40") || s.Contains("rtx4") || s.Contains(" 4090")
            || s.Contains(" 4080") || s.Contains(" 4070") || s.Contains(" 4060")
            || s.Contains("l40") || ContainsModelToken(s, "l4") || s.Contains("ada"))
        {
            return ("ada", false);
        }

        // Ampere (RTX 30 series, A100, A40, A10). No FP8 / FP4 hardware.
        if (s.Contains("rtx 30") || s.Contains("rtx3") || s.Contains(" 3090")
            || s.Contains(" 3080") || s.Contains(" 3070") || s.Contains(" 3060")
            || s.Contains("a100") || s.Contains("a40") || ContainsModelToken(s, "a10")
            || s.Contains("ampere"))
        {
            return ("ampere", false);
        }

        // Turing / Volta / older.
        if (s.Contains("rtx 20") || s.Contains("rtx2") || s.Contains("titan rtx")
            || s.Contains("turing") || s.Contains("volta") || s.Contains("v100"))
        {
            return ("turing-or-older", false);
        }

        return ("unknown", false);
    }

    private static bool ContainsModelToken(string text, string token)
    {
        int start = 0;
        while (start < text.Length)
        {
            int index = text.IndexOf(token, start, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            int before = index - 1;
            int after = index + token.Length;
            bool startsClean = before < 0 || !char.IsLetterOrDigit(text[before]);
            bool endsClean = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            if (startsClean && endsClean)
            {
                return true;
            }

            start = index + token.Length;
        }

        return false;
    }

    /// <summary>Pull a single short model-name line out of the raw /proc info file.</summary>
    private static string ExtractModelLine(string content)
    {
        foreach (string line in content.Split('\n'))
        {
            if (line.Contains("Model:", StringComparison.OrdinalIgnoreCase))
            {
                return line.Trim();
            }
        }
        return "model-line-not-found";
    }

    /// <summary>
    /// Pick a sensible default quantization format given the detected hardware.
    /// Operators always have the final word — this is just the suggestion the
    /// dashboard / quickstart surface uses.
    /// </summary>
    private static string BuildQuantizationRecommendation(bool gpuLikely, bool fp4Likely, string arch)
    {
        if (!gpuLikely)
        {
            // CPU-only — llama.cpp Q4_K_M is the right default. Q8 is wasteful
            // for the latency budget; FP4 is irrelevant without tensor cores.
            return "q4_k_m";
        }
        if (fp4Likely)
        {
            // Blackwell: NVFP4 via vLLM or TensorRT-LLM is 2x faster than FP8
            // at near-FP16 accuracy. See docs/QUANTIZATION.md for the full
            // matrix.
            return "nvfp4";
        }
        if (arch is "hopper" or "ada")
        {
            // Hopper / Ada: FP8 is the right native format. NVFP4 is not
            // accelerated here — would be software-emulated, no win.
            return "fp8";
        }
        if (arch is "cdna3" or "cdna4")
        {
            // AMD Instinct MI300 / MI350 class accelerators can run the
            // standards-based MXFP4 path on current ROCm-oriented stacks.
            return "mxfp4";
        }
        if (arch is "ampere" or "turing-or-older")
        {
            // Pre-Hopper: no FP8 / FP4. Q4_K_M (llama.cpp) or AWQ-INT4 (vLLM)
            // are the practical choices.
            return "q4_k_m";
        }
        // Unknown architecture — recommend the lowest-risk default.
        return "q4_k_m";
    }

    private static DuoHardwareTier DeriveTier(int cores, int ramGb, bool gpuLikely)
    {
        // Constrained: laptops / CPU-only / small-RAM systems.
        if (!gpuLikely && (cores < 8 || ramGb < 16))
        {
            return DuoHardwareTier.Constrained;
        }
        if (!gpuLikely)
        {
            // CPU-rich servers without a GPU still land in Constrained because
            // the Worker-class model will be unusably slow. Constrained is the
            // honest tier in that case.
            return DuoHardwareTier.Constrained;
        }

        // With GPU + serious core/RAM budget, consider Generous.
        if (cores >= 16 && ramGb >= 48)
        {
            return DuoHardwareTier.Generous;
        }

        return DuoHardwareTier.Standard;
    }

    private static string BuildRecommendation(DuoHardwareTier tier, int ramGb, bool gpuLikely, bool fp4Likely, string arch)
    {
        string lowPrecisionNote = fp4Likely
            ? " Blackwell FP4 tensor cores detected — vLLM or TensorRT-LLM with an NVFP4-quantized model gives ~2x throughput vs FP8 at near-FP16 accuracy. See docs/QUANTIZATION.md."
            : arch is "cdna3" or "cdna4"
                ? " AMD Instinct CDNA3/CDNA4 accelerator hint detected — current ROCm-oriented stacks expose an MXFP4 path. Validate it on your exact model family before promoting it to the default serving lane; see docs/QUANTIZATION.md."
            : string.Empty;

        return tier switch
        {
            DuoHardwareTier.Constrained when !gpuLikely =>
                $"CPU-only box with {ramGb} GB RAM. Keep live inference on small unsloth UD-* GGUFs (gemma-4-E4B / qwen3.6-mini-4B-A1B) via llama.cpp or run deterministic-only. Vision + TTS off by default.",
            DuoHardwareTier.Constrained =>
                $"Entry-level GPU + {ramGb} GB RAM. Stick with the fast lane (unsloth gemma-4-E4B-it-UD-Q4_K_XL via llama.cpp) and keep thinking-mode off. Judge role not recommended.{lowPrecisionNote}",
            DuoHardwareTier.Standard =>
                $"Single-GPU studio-class box with {ramGb} GB RAM. Worker + Edge bindings fit; Judge runs serialised (not co-resident). Enable vision + TTS selectively.{lowPrecisionNote}",
            DuoHardwareTier.Generous =>
                $"Multi-GPU or workstation-class box with {ramGb} GB RAM. Both Worker and Judge can be resident simultaneously; speculative decoding + full mesh patterns are worth trying.{lowPrecisionNote}",
            _ => "Unknown hardware posture — defaulting to Standard tier.",
        };
    }

    private static bool TryMapAmdArchitectureHint(string normalizedHint, out string architecture)
    {
        if (normalizedHint.Contains("mi350", StringComparison.Ordinal)
            || normalizedHint.Contains("mi355", StringComparison.Ordinal)
            || normalizedHint.Contains("gfx950", StringComparison.Ordinal)
            || normalizedHint.Contains("cdna4", StringComparison.Ordinal))
        {
            architecture = "cdna4";
            return true;
        }

        if (normalizedHint.Contains("mi300", StringComparison.Ordinal)
            || normalizedHint.Contains("mi325", StringComparison.Ordinal)
            || normalizedHint.Contains("gfx941", StringComparison.Ordinal)
            || normalizedHint.Contains("gfx942", StringComparison.Ordinal)
            || normalizedHint.Contains("cdna3", StringComparison.Ordinal))
        {
            architecture = "cdna3";
            return true;
        }

        architecture = string.Empty;
        return false;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }
}

/// <summary>
/// Deterministic snapshot of the host's approximate hardware posture,
/// captured by <see cref="HardwareProfiler.Capture"/>.
/// </summary>
/// <param name="OperatingSystem">Short OS identifier: "windows", "linux", "macos", "unknown".</param>
/// <param name="LogicalCoreCount">Count reported by <see cref="Environment.ProcessorCount"/>.</param>
/// <param name="PhysicalRamGigabytes">Coarse estimate of physical RAM (GiB, rounded).</param>
/// <param name="GpuLikelyPresent">True when env-var cues or driver files indicate a usable GPU.</param>
/// <param name="GpuDetectionDetail">Short string describing which cue fired (env-var name / marker path / "no-gpu-markers-detected").</param>
/// <param name="GpuArchitecture">Coarse architecture classifier: "blackwell" / "hopper" / "ada" / "ampere" / "turing-or-older" / "cdna3" / "cdna4" / "rdna" / "none" / "unknown". Used by the recommendation engine to pick a quantization format.</param>
/// <param name="GpuArchitectureDetail">Provenance of the architecture decision (env-var name, /proc model line, or "architecture-not-determinable-without-hint").</param>
/// <param name="Fp4TensorCoresLikely">True when the detected architecture has hardware FP4 tensor cores (Blackwell and successors). When true, NVFP4 is the recommended quantization.</param>
/// <param name="RecommendedQuantization">Suggested quantization format: "nvfp4" / "mxfp4" / "fp8" / "q4_k_m". See docs/QUANTIZATION.md for the full matrix.</param>
/// <param name="DetectedTier">Tier derived purely from the detected signals.</param>
/// <param name="EffectiveTier">Tier actually in effect after applying any operator override.</param>
/// <param name="OverrideApplied">True when <see cref="EffectiveTier"/> came from a forced override rather than detection.</param>
/// <param name="DetectionConfidence">Confidence hint: "high" / "medium" / "low".</param>
/// <param name="Recommendation">One-sentence plain-English suggestion for operators.</param>
/// <param name="CapturedAtUtc">When the snapshot was taken, in UTC.</param>
public sealed record HardwareProfile(
    string OperatingSystem,
    int LogicalCoreCount,
    int PhysicalRamGigabytes,
    bool GpuLikelyPresent,
    string GpuDetectionDetail,
    string GpuArchitecture,
    string GpuArchitectureDetail,
    bool Fp4TensorCoresLikely,
    string RecommendedQuantization,
    string DetectedTier,
    string EffectiveTier,
    bool OverrideApplied,
    string DetectionConfidence,
    string Recommendation,
    DateTimeOffset CapturedAtUtc);
