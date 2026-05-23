using NUnit.Framework;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Pass 25 — regression coverage for <see cref="HardwareProfiler"/>.
/// The profiler must:
/// <list type="bullet">
///   <item>Always return a non-null <see cref="HardwareProfile"/>.</item>
///   <item>Report a plausible core count and OS identifier.</item>
///   <item>Honour the <c>forceTier</c> override and expose the override flag.</item>
///   <item>Default to a valid <see cref="DuoHardwareTier"/> enum name on both tiers.</item>
/// </list>
/// </summary>
[TestFixture]
public class HardwareProfilerTests
{
    [Test]
    public void Capture_ReturnsPlausibleProfile()
    {
        HardwareProfile profile = HardwareProfiler.Capture();

        Assert.That(profile, Is.Not.Null);
        Assert.That(profile.LogicalCoreCount, Is.GreaterThan(0));
        Assert.That(new[] { "windows", "linux", "macos", "unknown" }, Does.Contain(profile.OperatingSystem));
        Assert.That(Enum.TryParse<DuoHardwareTier>(profile.DetectedTier, out _), Is.True,
            "DetectedTier must always be a valid DuoHardwareTier enum name.");
        Assert.That(Enum.TryParse<DuoHardwareTier>(profile.EffectiveTier, out _), Is.True,
            "EffectiveTier must always be a valid DuoHardwareTier enum name.");
        Assert.That(profile.Recommendation, Is.Not.Empty);
        Assert.That(profile.CapturedAtUtc, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
    }

    [Test]
    public void Capture_WithValidForceTier_OverridesDetectedTier()
    {
        HardwareProfile constrained = HardwareProfiler.Capture(forceTier: "Constrained");
        HardwareProfile standard = HardwareProfiler.Capture(forceTier: "Standard");
        HardwareProfile generous = HardwareProfiler.Capture(forceTier: "Generous");

        Assert.That(constrained.EffectiveTier, Is.EqualTo("Constrained"));
        Assert.That(standard.EffectiveTier, Is.EqualTo("Standard"));
        Assert.That(generous.EffectiveTier, Is.EqualTo("Generous"));
        Assert.That(constrained.OverrideApplied, Is.True);
        Assert.That(standard.OverrideApplied, Is.True);
        Assert.That(generous.OverrideApplied, Is.True);
    }

    [Test]
    public void Capture_WithCaseInsensitiveForceTier_StillOverrides()
    {
        HardwareProfile profile = HardwareProfiler.Capture(forceTier: "generous");

        Assert.That(profile.EffectiveTier, Is.EqualTo("Generous"));
        Assert.That(profile.OverrideApplied, Is.True);
    }

    [Test]
    public void Capture_WithUnparsableForceTier_UsesDetectedTier()
    {
        HardwareProfile profile = HardwareProfiler.Capture(forceTier: "Quantum");

        Assert.That(profile.EffectiveTier, Is.EqualTo(profile.DetectedTier),
            "Unparsable forceTier must silently fall back to detection.");
        Assert.That(profile.OverrideApplied, Is.False);
    }

    [Test]
    public void Capture_WithEmptyForceTier_UsesDetectedTier()
    {
        HardwareProfile profile = HardwareProfiler.Capture(forceTier: "");

        Assert.That(profile.OverrideApplied, Is.False);
        Assert.That(profile.EffectiveTier, Is.EqualTo(profile.DetectedTier));
    }

    [Test]
    public void CaptureCached_ReturnsSameInstanceWithinTtl()
    {
        HardwareProfiler.InvalidateCache();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        HardwareProfile a = HardwareProfiler.CaptureCached(forceTier: null, cacheTtl: ttl);
        HardwareProfile b = HardwareProfiler.CaptureCached(forceTier: null, cacheTtl: ttl);

        Assert.That(b.CapturedAtUtc, Is.EqualTo(a.CapturedAtUtc),
            "Within the TTL the cache must hand back the same snapshot instance.");
    }

    [Test]
    public void CaptureCached_RespectsForceTierAsCacheKey()
    {
        HardwareProfiler.InvalidateCache();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        HardwareProfile a = HardwareProfiler.CaptureCached(forceTier: "Constrained", cacheTtl: ttl);
        HardwareProfile b = HardwareProfiler.CaptureCached(forceTier: "Generous", cacheTtl: ttl);

        Assert.That(a.EffectiveTier, Is.EqualTo("Constrained"));
        Assert.That(b.EffectiveTier, Is.EqualTo("Generous"),
            "Different forceTier must bypass the cache and recompute.");
    }

    [Test]
    public void InvalidateCache_ForcesRecomputeOnNextCaptureCached()
    {
        TimeSpan ttl = TimeSpan.FromMinutes(5);
        HardwareProfile first = HardwareProfiler.CaptureCached(forceTier: null, cacheTtl: ttl);

        HardwareProfiler.InvalidateCache();
        // Ensure timestamps would differ if recomputation fires.
        Thread.Sleep(2);
        HardwareProfile second = HardwareProfiler.CaptureCached(forceTier: null, cacheTtl: ttl);

        Assert.That(second.CapturedAtUtc, Is.GreaterThan(first.CapturedAtUtc),
            "After InvalidateCache, CaptureCached must produce a freshly-timestamped profile.");
    }

    // -------------------------------------------------------------------
    // Pass 54 — GPU architecture + FP4 tensor core detection
    // -------------------------------------------------------------------

    [Test]
    public void Capture_AlwaysReportsArchitectureFields()
    {
        HardwareProfile profile = HardwareProfiler.Capture();

        Assert.Multiple(() =>
        {
            Assert.That(profile.GpuArchitecture, Is.Not.Null.And.Not.Empty,
                "GpuArchitecture must always be populated (use 'none' / 'unknown' as the floor).");
            Assert.That(profile.GpuArchitectureDetail, Is.Not.Null.And.Not.Empty);
            Assert.That(profile.RecommendedQuantization, Is.Not.Null.And.Not.Empty);
            Assert.That(new[] { "nvfp4", "mxfp4", "fp8", "q4_k_m" }, Does.Contain(profile.RecommendedQuantization),
                "RecommendedQuantization must be one of the documented values.");
        });
    }

    [Test]
    public void Capture_BlackwellHint_ReportsFp4Capable()
    {
        HardwareProfiler.InvalidateCache();
        string? saved = Environment.GetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE");
        string? cudaSaved = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        try
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", "blackwell");
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "0");
            HardwareProfile profile = HardwareProfiler.Capture();

            Assert.That(profile.GpuLikelyPresent, Is.True);
            Assert.That(profile.GpuArchitecture, Is.EqualTo("blackwell"));
            Assert.That(profile.Fp4TensorCoresLikely, Is.True);
            Assert.That(profile.RecommendedQuantization, Is.EqualTo("nvfp4"));
            Assert.That(profile.Recommendation, Does.Contain("NVFP4"),
                "Recommendation should mention NVFP4 when Blackwell is detected.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", saved);
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", cudaSaved);
            HardwareProfiler.InvalidateCache();
        }
    }

    [Test]
    public void Capture_HopperHint_RecommendsFp8NotNvfp4()
    {
        HardwareProfiler.InvalidateCache();
        string? saved = Environment.GetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE");
        string? cudaSaved = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        try
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", "hopper");
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "0");
            HardwareProfile profile = HardwareProfiler.Capture();

            Assert.That(profile.GpuLikelyPresent, Is.True);
            Assert.That(profile.GpuArchitecture, Is.EqualTo("hopper"));
            Assert.That(profile.Fp4TensorCoresLikely, Is.False,
                "Hopper has FP8 tensor cores but no FP4.");
            Assert.That(profile.RecommendedQuantization, Is.EqualTo("fp8"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", saved);
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", cudaSaved);
            HardwareProfiler.InvalidateCache();
        }
    }

    [Test]
    public void Capture_AmpereHint_RecommendsQ4kmNotNvfp4()
    {
        HardwareProfiler.InvalidateCache();
        string? saved = Environment.GetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE");
        string? cudaSaved = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        try
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", "ampere");
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", "0");
            HardwareProfile profile = HardwareProfiler.Capture();

            Assert.That(profile.GpuLikelyPresent, Is.True);
            Assert.That(profile.GpuArchitecture, Is.EqualTo("ampere"));
            Assert.That(profile.Fp4TensorCoresLikely, Is.False);
            Assert.That(profile.RecommendedQuantization, Is.EqualTo("q4_k_m"),
                "Pre-Hopper GPUs lack FP8/FP4 tensor cores; Q4_K_M is the right default.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", saved);
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", cudaSaved);
            HardwareProfiler.InvalidateCache();
        }
    }

    [TestCase("NVIDIA GeForce RTX 5090", "blackwell")]
    [TestCase("NVIDIA L4", "ada")]
    [TestCase("NVIDIA A10", "ampere")]
    [TestCase("AMD Radeon RX 7900 XTX", "rdna")]
    [TestCase("AMD Instinct MI350X", "cdna4")]
    public void NormalizeArchitectureHint_MapsRegistryStyleGpuNames(string modelName, string expectedArchitecture)
    {
        Assert.That(HardwareProfiler.NormalizeArchitectureHint(modelName), Is.EqualTo(expectedArchitecture));
    }

    [TestCase("MemTotal:       67108864 kB", 68_719_476_736L)]
    [TestCase("MemTotal: 64 GiB", 68_719_476_736L)]
    public void TryParseLinuxMemTotalBytes_ParsesMemInfoUnits(string memInfo, long expectedBytes)
    {
        Assert.That(HardwareProfiler.TryParseLinuxMemTotalBytes(memInfo, out long bytes), Is.True);
        Assert.That(bytes, Is.EqualTo(expectedBytes));
    }

    [Test]
    public void TryParseLinuxMemTotalBytes_RejectsMissingMemTotal()
    {
        Assert.That(HardwareProfiler.TryParseLinuxMemTotalBytes("SwapTotal: 1048576 kB", out long bytes), Is.False);
        Assert.That(bytes, Is.EqualTo(0));
    }

    [TestCase("mi300", "cdna3")]
    [TestCase("gfx942", "cdna3")]
    [TestCase("mi350", "cdna4")]
    [TestCase("gfx950", "cdna4")]
    public void Capture_AmdInstinctHints_RecommendMxfp4(string hint, string expectedArchitecture)
    {
        HardwareProfiler.InvalidateCache();
        string? hintSaved = Environment.GetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE");
        string? rocrSaved = Environment.GetEnvironmentVariable("ROCR_VISIBLE_DEVICES");
        try
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", hint);
            Environment.SetEnvironmentVariable("ROCR_VISIBLE_DEVICES", "0");

            HardwareProfile profile = HardwareProfiler.Capture();

            Assert.That(profile.GpuLikelyPresent, Is.True);
            Assert.That(profile.GpuArchitecture, Is.EqualTo(expectedArchitecture));
            Assert.That(profile.Fp4TensorCoresLikely, Is.False,
                "AMD Instinct hinting should not claim NVIDIA tensor-core semantics.");
            Assert.That(profile.RecommendedQuantization, Is.EqualTo("mxfp4"));
            Assert.That(profile.Recommendation, Does.Contain("MXFP4"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", hintSaved);
            Environment.SetEnvironmentVariable("ROCR_VISIBLE_DEVICES", rocrSaved);
            HardwareProfiler.InvalidateCache();
        }
    }

    [Test]
    public void Capture_NoGpu_AlwaysRecommendsCpuFriendlyQuant()
    {
        // When no GPU is present we always recommend Q4_K_M (llama.cpp). NVFP4
        // is meaningless without tensor cores; FP8 is meaningless without
        // hardware support.
        HardwareProfiler.InvalidateCache();
        string? archSaved = Environment.GetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE");
        string? cudaSaved = Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES");
        string? nvSaved   = Environment.GetEnvironmentVariable("NVIDIA_VISIBLE_DEVICES");
        string? hipSaved  = Environment.GetEnvironmentVariable("HIP_VISIBLE_DEVICES");
        string? rocrSaved = Environment.GetEnvironmentVariable("ROCR_VISIBLE_DEVICES");
        try
        {
            // Wipe every signal that would make DetectGpuPresence return true.
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", null);
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", null);
            Environment.SetEnvironmentVariable("NVIDIA_VISIBLE_DEVICES", null);
            Environment.SetEnvironmentVariable("HIP_VISIBLE_DEVICES", null);
            Environment.SetEnvironmentVariable("ROCR_VISIBLE_DEVICES", null);
            HardwareProfile profile = HardwareProfiler.Capture();

            // We can only assert the no-GPU branch when DetectGpuPresence
            // actually returns false (some CI agents have driver markers
            // installed even without a real GPU). When it does return false,
            // the contract is strict.
            if (!profile.GpuLikelyPresent)
            {
                Assert.That(profile.GpuArchitecture, Is.EqualTo("none"));
                Assert.That(profile.Fp4TensorCoresLikely, Is.False);
                Assert.That(profile.RecommendedQuantization, Is.EqualTo("q4_k_m"));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", archSaved);
            Environment.SetEnvironmentVariable("CUDA_VISIBLE_DEVICES", cudaSaved);
            Environment.SetEnvironmentVariable("NVIDIA_VISIBLE_DEVICES", nvSaved);
            Environment.SetEnvironmentVariable("HIP_VISIBLE_DEVICES", hipSaved);
            Environment.SetEnvironmentVariable("ROCR_VISIBLE_DEVICES", rocrSaved);
            HardwareProfiler.InvalidateCache();
        }
    }
}
