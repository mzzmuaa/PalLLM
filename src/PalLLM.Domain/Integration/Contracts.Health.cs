// Contracts (partial): runtime-health surface plus latency/fallback/tier metric records.
// Part of the PalLLM.Domain.Integration wire contract; see Contracts.cs for the core game/bridge/chat shapes.
using System.Text.Json;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Integration;

public sealed class RuntimeHealth
{
    public string AdapterName { get; init; } = string.Empty;

    public bool AdapterReady { get; init; }

    public bool BridgeEnabled { get; init; }

    public bool InferenceConfigured { get; init; }

    public string InferenceModel { get; init; } = string.Empty;

    public string InferenceActiveModel { get; init; } = string.Empty;

    public string? InferenceActiveTierId { get; init; }

    public IReadOnlyList<string> InferenceLastSeenAvailableModels { get; init; } =
        Array.Empty<string>();

    public bool VisionEnabled { get; init; }

    public string VisionModel { get; init; } = string.Empty;

    public bool TtsEnabled { get; init; }

    public bool AsrEnabled { get; init; }

    public bool AutomationEnabled { get; init; }

    public string Status { get; init; } = string.Empty;

    public long InferenceSuccessCount { get; init; }

    public long InferenceFailureCount { get; init; }

    public long InferenceBypassCount { get; init; }

    public long FallbackReplyCount { get; init; }

    public int CharacterCount { get; init; }

    public int RememberedEntries { get; init; }

    public int LoadedPackCount { get; init; }

    public int KnownBaseCount { get; init; }

    public long BridgeEventCount { get; init; }

    public long BridgeBootCount { get; init; }

    public string LastBridgeEventType { get; init; } = string.Empty;

    public DateTimeOffset? LastBridgeEventAtUtc { get; init; }

    public string RuntimeRoot { get; init; } = string.Empty;

    public int TrackedRelationshipCount { get; init; }

    public int OutboxPendingCount { get; init; }

    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public long TotalInferenceTokens { get; init; }

    public long VisionCallCount { get; init; }

    public long VisionFailureCount { get; init; }

    public long TtsCallCount { get; init; }

    public long TtsSuccessCount { get; init; }

    public long TtsFailureCount { get; init; }

    public long AsrCallCount { get; init; }

    public long AsrSuccessCount { get; init; }

    public long AsrFailureCount { get; init; }

    public long AsrEndpointingReceiptCount { get; init; }

    public long AsrBargeInCount { get; init; }

    public long AsrEndpointingReviewCount { get; init; }

    public long AsrConfidenceReceiptCount { get; init; }

    public long AsrConfidenceReviewCount { get; init; }

    public long AsrTimingReceiptCount { get; init; }

    public long AsrTimingReviewCount { get; init; }

    public long AsrQualityReceiptCount { get; init; }

    public long AsrQualityReviewCount { get; init; }

    public long AsrUpstreamRequestIdReceiptCount { get; init; }

    public long AsrUpstreamProcessingReceiptCount { get; init; }

    public long AsrUpstreamPhaseTimingReceiptCount { get; init; }

    public int InboxPendingCount { get; init; }

    public int ScreenshotPendingCount { get; init; }

    public int ArchiveFileCount { get; init; }

    public int FailedFileCount { get; init; }

    public bool SessionDirty { get; init; }

    public DateTimeOffset? SessionLastSavedAtUtc { get; init; }

    public NativeReadinessSnapshot NativeReadiness { get; init; } = new();

    public BridgeLoopProofSnapshot BridgeLoop { get; init; } = new();

    public long RateLimitedCount { get; init; }

    public string InferenceCircuitState { get; init; } = string.Empty;

    public int InferenceCircuitFailures { get; init; }

    public InferenceWarmupSnapshot InferenceWarmup { get; init; } = new();

    /// <summary>Per-strategy usage counter from the deterministic fallback director.
    /// Rendered as labeled Prometheus counters (`palllm_fallback_strategy_total{strategy="..."}`).
    /// Empty dictionary when no fallback replies have been served yet.</summary>
    public IReadOnlyList<FallbackStrategyCount> FallbackStrategyCounts { get; init; } = [];

    /// <summary>Per-transition counter for model-tier graduations. Each entry records
    /// a directional transition (for example, `small -> large`) along with how many times that
    /// transition fired since startup. Rendered as labeled Prometheus counters
    /// (`palllm_model_tier_transition_total{from="...",to="..."}`).</summary>
    public IReadOnlyList<ModelTierTransitionCount> ModelTierTransitionCounts { get; init; } = [];

    /// <summary>Cumulative Prometheus-style histogram of end-to-end chat latency
    /// in seconds. Buckets cover the realistic range for PalLLM: sub-10ms for
    /// fallback-only paths, up to 60s for inference-backed paths on large models.</summary>
    public ChatLatencyHistogram ChatLatency { get; init; } = new(0, 0, []);

    /// <summary>Operator-actionable next-step hints derived from the current snapshot.
    /// Empty when the runtime is healthy. Each entry carries a stable <c>Code</c> for
    /// programmatic consumption (dashboards, <c>pal next</c> advisor, MCP tools), a
    /// human-readable <c>Message</c>, and an optional <c>Command</c> the operator can
    /// copy-paste to address it. This is the single source of "what should I do
    /// right now?" for both /api/health curl callers and the Field Console.</summary>
    public IReadOnlyList<HealthSuggestion> Suggestions { get; init; } = Array.Empty<HealthSuggestion>();
}

/// <summary>One actionable next-step hint surfaced in <see cref="RuntimeHealth.Suggestions"/>.
/// <para><see cref="Code"/> is a stable kebab-case identifier (e.g. <c>"no-packs-loaded"</c>,
/// <c>"inference-circuit-open"</c>, <c>"bridge-idle"</c>) so dashboards and CI tooling can
/// match on it without parsing the Message.</para>
/// <para><see cref="Message"/> is a single sentence explaining the situation in plain
/// English.</para>
/// <para><see cref="Command"/> is an optional copy-paste command (a <c>pal</c> verb,
/// a PowerShell one-liner, etc.) the operator can run to address it. Null when no
/// single-shot remediation exists.</para>
/// <para><see cref="Severity"/> is one of <c>"info"</c> / <c>"warn"</c> / <c>"urgent"</c>
/// and lets every consumer (dashboard, MCP client, pal next, pal doctor) render
/// matching visual treatment without duplicating a code-to-severity map. The builder
/// is the source of truth so a new hint code automatically lights up the right colour
/// across every surface without coordinated edits.</para></summary>
public sealed record HealthSuggestion(string Code, string Message, string? Command, string Severity);

/// <summary>Cumulative Prometheus-style histogram record. <see cref="SumSeconds"/>
/// + <see cref="Count"/> give average; each <see cref="LatencyHistogramBucket"/>
/// entry records "count of observations with duration &lt;= UpperBoundSeconds".</summary>
public sealed record ChatLatencyHistogram(
    long Count,
    double SumSeconds,
    IReadOnlyList<LatencyHistogramBucket> Buckets);

public sealed record LatencyHistogramBucket(double UpperBoundSeconds, long CumulativeCount);

/// <summary>Count of chat replies served by a single fallback strategy since startup.</summary>
public sealed record FallbackStrategyCount(string StrategyId, long Count);

/// <summary>Count of times the tier orchestrator graduated from one tier id to another.</summary>
public sealed record ModelTierTransitionCount(string From, string To, long Count);

