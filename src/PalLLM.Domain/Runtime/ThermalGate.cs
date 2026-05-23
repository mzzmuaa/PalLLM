using System.Diagnostics;
using System.Globalization;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Opt-in GPU-temperature gate. Short-circuits live inference
//            calls when the host's primary GPU is hot enough to be
//            actively throttling - keeps the deterministic fallback
//            replies flowing under thermal pressure without piling up
//            on an already-throttled card.
//   surface: ThermalGate (TryProbe / IsThrottling / Reset). Probe runs
//            via nvidia-smi or the OS perf counters; opt-in via
//            PalLLM:Inference:ThermalGateEnabled.
//   gate:    None directly; behaviour pinned by ThermalGateTests.
//   adr:     0001-deterministic-first-reply-pipeline.md (the fallback
//            never failing is the load-bearing invariant; thermal
//            pressure must not break it).
//   docs:    docs/TUNING.md (ThermalGate threshold knob),
//            docs/RUNBOOK.md ("chat went deterministic under load"),
//            docs/HOT_PATH.md (thermal-gate is on the chat hot path).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Opt-in GPU-temperature gate that short-circuits live inference calls when
/// the host's primary GPU is hot enough to be actively throttling. Keeps the
/// deterministic fallback director's player-visible replies flowing under
/// thermal pressure without running the big model on top of a throttled
/// device - the latter would just slow every chat by the same amount as the
/// throttling itself.
///
/// <para>Sampling is best-effort: when no sensor is reachable the gate is
/// permissive (returns <see cref="ThermalGateDecision.Allow"/>) so the feature
/// is always safe to leave enabled. Reads are cached for
/// <see cref="CacheTtl"/> so a burst of chat requests does not spawn a new
/// <c>nvidia-smi</c> per turn.</para>
///
/// <para>The gate is game-agnostic and publishable: it lives in
/// <c>PalLLM.Domain</c>, depends only on the BCL, and is harvestable by other
/// local-first LLM companion runtimes exactly like
/// <c>SemanticEmbedder</c> / <c>ResponseCleanup</c> are. See
/// <c>docs/CORE_LIBRARY.md</c>.</para>
/// </summary>
public sealed class ThermalGate
{
    private const int NvidiaSmiTimeoutMs = 750;
    private const int NvidiaSmiDrainTimeoutMs = 250;
    private const int NvidiaSmiStdoutMaxChars = 4 * 1024;

    /// <summary>
    /// Test override: when this environment variable is set to a parseable
    /// <see cref="double"/>, the gate returns that value as the current
    /// temperature regardless of installed sensors. Lets unit tests exercise
    /// warn/reject/allow code paths deterministically, and lets operators
    /// simulate a hot GPU on a machine without NVML installed.
    /// </summary>
    public const string TestOverrideEnvVar = "PALLLM_FAKE_GPU_TEMP_C";

    private readonly object _gate = new();
    private readonly Func<ThermalSample> _sampler;
    private readonly Func<DateTimeOffset> _now;
    private ThermalSample? _cached;

    public ThermalGate()
        : this(DefaultSampler, () => DateTimeOffset.UtcNow)
    {
    }

    /// Test-only constructor so unit tests can inject a fake sampler + clock.
    public ThermalGate(Func<ThermalSample> sampler, Func<DateTimeOffset> now)
    {
        _sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    /// <summary>
    /// Temperature (deg C) at which a call is rejected. Default matches the
    /// conservative threshold for consumer NVIDIA cards: above this number,
    /// the card is already actively lowering its clocks, so burning an
    /// inference round-trip on it only hurts player latency.
    /// </summary>
    public double RejectAboveC { get; set; } = 83.0;

    /// <summary>
    /// Temperature (deg C) at which the gate reports a "warm" warning without
    /// rejecting. Used for dashboards / operational surfaces; the live chat
    /// path only gates on <see cref="RejectAboveC"/>.
    /// </summary>
    public double WarnAboveC { get; set; } = 78.0;

    /// <summary>
    /// How long a successful sensor read is trusted before resampling.
    /// Raising this reduces sensor-read overhead; lowering it makes the gate
    /// react faster to spikes. Default chosen to ride under the typical
    /// inference chat cadence without letting stale data mask a real stall.
    /// </summary>
    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Returns the decision plus the underlying reading. The <see cref="ThermalSample.Source"/>
    /// tells callers whether the reading was live (<c>nvidia-smi</c>,
    /// <c>env_override</c>) or the gate is currently running permissively
    /// because no sensor responded.
    /// </summary>
    public ThermalGateResult Evaluate()
    {
        ThermalSample sample = ReadCached();

        if (sample.TemperatureC is null)
        {
            return new ThermalGateResult(ThermalGateDecision.Allow, sample, Reason: "sensor unavailable - permissive");
        }

        double t = sample.TemperatureC.Value;
        if (t >= RejectAboveC)
        {
            return new ThermalGateResult(
                ThermalGateDecision.Reject,
                sample,
                Reason: $"GPU {t.ToString("F1", CultureInfo.InvariantCulture)} deg C >= reject threshold {RejectAboveC.ToString("F1", CultureInfo.InvariantCulture)} deg C");
        }

        if (t >= WarnAboveC)
        {
            return new ThermalGateResult(
                ThermalGateDecision.Warn,
                sample,
                Reason: $"GPU {t.ToString("F1", CultureInfo.InvariantCulture)} deg C >= warn threshold {WarnAboveC.ToString("F1", CultureInfo.InvariantCulture)} deg C");
        }

        return new ThermalGateResult(ThermalGateDecision.Allow, sample, Reason: "GPU within thermal budget");
    }

    /// <summary>
    /// Forces a fresh sensor read on the next <see cref="Evaluate"/> call.
    /// Primarily for tests and for operators who want to re-probe after an
    /// ambient change (new cooler fan profile, new side-panel mod, etc.).
    /// </summary>
    public void InvalidateCache()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private ThermalSample ReadCached()
    {
        DateTimeOffset now = _now();
        lock (_gate)
        {
            if (_cached is { } c && now - c.SampledAtUtc < CacheTtl)
            {
                return c;
            }
        }

        ThermalSample fresh = SafeSample(now);
        lock (_gate)
        {
            _cached = fresh;
        }

        return fresh;
    }

    private ThermalSample SafeSample(DateTimeOffset now)
    {
        try
        {
            ThermalSample sample = _sampler();
            // Carry the sampled-at we chose so our TTL clock agrees with the
            // injected `_now` even if the sampler set its own value.
            return sample with { SampledAtUtc = now };
        }
        catch
        {
            return new ThermalSample(TemperatureC: null, Source: "unavailable", SampledAtUtc: now);
        }
    }

    /// <summary>
    /// Default sampler: preferentially honors the <see cref="TestOverrideEnvVar"/>
    /// override; otherwise shells out to <c>nvidia-smi</c> if it's on PATH.
    /// No hard NuGet dependency on NVML or a vendor SDK is taken; the gate
    /// is best-effort on systems without the small-footprint CLI installed.
    /// </summary>
    public static ThermalSample DefaultSampler()
    {
        string? overrideValue = Environment.GetEnvironmentVariable(TestOverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(overrideValue) &&
            double.TryParse(overrideValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double overrideC))
        {
            return new ThermalSample(overrideC, "env_override", DateTimeOffset.UtcNow);
        }

        // `nvidia-smi` ships with any NVIDIA driver; no SDK needed. We pick the
        // hottest GPU on a multi-GPU rig so a hot second card still gates the
        // primary inference call - conservative is the right default.
        double? hottest = TryReadViaNvidiaSmi();
        if (hottest is not null)
        {
            return new ThermalSample(hottest.Value, "nvidia-smi", DateTimeOffset.UtcNow);
        }

        return new ThermalSample(TemperatureC: null, Source: "unavailable", SampledAtUtc: DateTimeOffset.UtcNow);
    }

    private static double? TryReadViaNvidiaSmi()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
            Task<ProcessTextReadLimiter.BoundedTextReadResult> stdoutTask =
                ProcessTextReadLimiter.ReadAsync(proc.StandardOutput, NvidiaSmiStdoutMaxChars);
            Task<ProcessTextReadLimiter.BoundedTextReadResult> stderrTask =
                ProcessTextReadLimiter.ReadAsync(proc.StandardError, maxChars: 0);

            // A slow `nvidia-smi` is itself a signal the GPU stack is struggling.
            // Stay permissive rather than burning more latency on the probe.
            bool exited = proc.WaitForExit(NvidiaSmiTimeoutMs);
            if (!exited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            }

            if (!TryWaitForDrain(stdoutTask, stderrTask, NvidiaSmiDrainTimeoutMs) ||
                !exited ||
                proc.ExitCode != 0)
            {
                return null;
            }

            ProcessTextReadLimiter.BoundedTextReadResult stdout = stdoutTask.GetAwaiter().GetResult();
            if (stdout.Truncated)
            {
                return null;
            }

            return ParseHottestTemperature(stdout.Text);
        }
        catch
        {
            return null;
        }
    }

    internal static double? ParseHottestTemperature(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return null;
        }

        double? hottest = null;
        foreach (string rawLine in stdout.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (double.TryParse(rawLine, NumberStyles.Float, CultureInfo.InvariantCulture, out double c))
            {
                hottest = hottest is null ? c : Math.Max(hottest.Value, c);
            }
        }

        return hottest;
    }

    private static bool TryWaitForDrain(Task stdoutTask, Task stderrTask, int timeoutMs)
    {
        try
        {
            return Task.WhenAll(stdoutTask, stderrTask).Wait(timeoutMs);
        }
        catch
        {
            return false;
        }
    }
}

public readonly record struct ThermalSample(double? TemperatureC, string Source, DateTimeOffset SampledAtUtc);

public enum ThermalGateDecision
{
    /// <summary>Call may proceed. Either under warn threshold, or no sensor reachable.</summary>
    Allow,

    /// <summary>Call is allowed but the GPU is warm enough to surface a dashboard warning.</summary>
    Warn,

    /// <summary>Call must be rejected; route straight to the deterministic fallback director.</summary>
    Reject,
}

public readonly record struct ThermalGateResult(ThermalGateDecision Decision, ThermalSample Sample, string Reason);
