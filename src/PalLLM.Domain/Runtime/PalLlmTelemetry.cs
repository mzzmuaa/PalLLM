using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Central <see cref="ActivitySource"/> for PalLLM runtime spans.
///
/// This type has no dependency on any OpenTelemetry package so the Domain
/// project stays portable. When an <see cref="ActivityListener"/> is
/// registered (either by the Sidecar's OpenTelemetry wiring in Program.cs
/// when <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set, or by a test that wants
/// to assert on emitted spans), <c>ActivitySource.StartActivity</c>
/// returns a live <see cref="Activity"/> that callers should dispose to
/// close the span. When no listener is present, StartActivity returns null
/// and the cost is a single branch — safe to leave on in production.
/// </summary>
public static class PalLlmTelemetry
{
    /// <summary>
    /// Activity source name for PalLLM runtime spans.
    /// </summary>
    public const string SourceName = "PalLLM.Runtime";

    /// <summary>
    /// Meter name for PalLLM runtime metrics.
    /// </summary>
    public const string MeterName = "PalLLM.Runtime";

    /// <summary>
    /// OpenTelemetry GenAI client duration histogram name.
    /// </summary>
    public const string GenAiClientOperationDurationMetricName = "gen_ai.client.operation.duration";

    /// <summary>
    /// OpenTelemetry GenAI client token-usage histogram name.
    /// </summary>
    public const string GenAiClientTokenUsageMetricName = "gen_ai.client.token.usage";

    /// <summary>
    /// Custom gauge that exports the active recent-window readiness status as a
    /// one-hot state vector over the bounded PalLLM status catalog.
    /// </summary>
    public const string InferenceRecentWindowStatusMetricName = "palllm.inference.recent_window.status";

    /// <summary>
    /// Recent-window live-operation count gauge.
    /// </summary>
    public const string InferenceRecentWindowSampleCountMetricName = "palllm.inference.recent_window.sample_count";

    /// <summary>
    /// Recent-window success-ratio gauge.
    /// </summary>
    public const string InferenceRecentWindowSuccessRatioMetricName = "palllm.inference.recent_window.success_ratio";

    /// <summary>
    /// Recent-window latency-target-hit ratio gauge.
    /// </summary>
    public const string InferenceRecentWindowTargetHitRatioMetricName = "palllm.inference.recent_window.target_hit_ratio";

    /// <summary>
    /// Recent-window latency-ceiling-hit ratio gauge.
    /// </summary>
    public const string InferenceRecentWindowCeilingHitRatioMetricName = "palllm.inference.recent_window.ceiling_hit_ratio";

    /// <summary>
    /// Per-lane one-hot readiness state gauge.
    /// </summary>
    public const string InferenceLaneStatusMetricName = "palllm.inference.lane.status";

    /// <summary>
    /// Per-lane recent-window sample-count gauge.
    /// </summary>
    public const string InferenceLaneSampleCountMetricName = "palllm.inference.lane.sample_count";

    /// <summary>
    /// Recommended explicit bucket boundaries for the GenAI client duration
    /// histogram from the OpenTelemetry GenAI semantic conventions.
    /// </summary>
    public static readonly double[] GenAiClientOperationDurationBoundaries =
    [
        0.01, 0.02, 0.04, 0.08, 0.16, 0.32, 0.64,
        1.28, 2.56, 5.12, 10.24, 20.48, 40.96, 81.92,
    ];

    /// <summary>
    /// Recommended explicit bucket boundaries for the GenAI client token-usage
    /// histogram from the OpenTelemetry GenAI semantic conventions.
    /// </summary>
    public static readonly double[] GenAiClientTokenUsageBoundaries =
    [
        1, 4, 16, 64, 256, 1024, 4096,
        16384, 65536, 262144, 1048576, 4194304, 16777216, 67108864,
    ];

    /// <summary>
    /// Shared activity source for runtime spans.
    /// </summary>
    public static ActivitySource Source { get; } = new(
        SourceName,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    /// <summary>
    /// Shared meter for runtime metrics.
    /// </summary>
    public static Meter Meter { get; } = new(
        MeterName,
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0");

    internal static Histogram<double> GenAiClientOperationDuration { get; } =
        Meter.CreateHistogram<double>(
            GenAiClientOperationDurationMetricName,
            unit: "s",
            description: "Duration of outgoing GenAI client operations.");

    internal static Histogram<long> GenAiClientTokenUsage { get; } =
        Meter.CreateHistogram<long>(
            GenAiClientTokenUsageMetricName,
            unit: "{token}",
            description: "Input and output tokens reported by outgoing GenAI client operations.");
}
