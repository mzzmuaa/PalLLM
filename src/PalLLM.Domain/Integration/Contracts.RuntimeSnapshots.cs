// Contracts (partial): memory/feature/pack requests plus bridge-runtime, dashboard, and inference-performance snapshots.
// Part of the PalLLM.Domain.Integration wire contract; see Contracts.cs for the core game/bridge/chat shapes.
using System.Text.Json;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Integration;

public sealed class MemoryRecallRequest
{
    public int? CharacterId { get; init; }

    public string Query { get; init; } = string.Empty;

    public int Limit { get; init; } = 5;
}

public sealed class FeatureDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;
}

public sealed class PackSummary
{
    public string Name { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    /// Pack-root-relative manifest path (`/`-separated) for the loaded pack so
    /// public listings stay stable across machines without disclosing the
    /// operator's absolute local filesystem layout.
    public string FilePath { get; init; } = string.Empty;

    public int CharacterCount { get; init; }
}

public sealed class ChatIngressSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string CharacterName { get; init; } = string.Empty;

    public string TaskTag { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class OutboxReplyTraceSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string CharacterName { get; init; } = string.Empty;

    public string TaskTag { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public string FallbackStrategy { get; init; } = string.Empty;

    public string ActionType { get; init; } = string.Empty;

    public bool SpeechExpected { get; init; }

    public string SpeechDelivery { get; init; } = string.Empty;

    public string SpeechMimeType { get; init; } = string.Empty;

    public string SpeechPlaybackHint { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset WrittenAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ReplyDeliveryEventPayload
{
    public string RequestId { get; init; } = string.Empty;

    public string Speaker { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public string StrategyId { get; init; } = string.Empty;

    public string Phase { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public bool Rendered { get; init; }

    public string Surface { get; init; } = string.Empty;

    public string CardLabel { get; init; } = string.Empty;

    public int CardIndex { get; init; }

    public int CardCount { get; init; }

    public string Note { get; init; } = string.Empty;
}

public sealed class SpeechPlaybackEventPayload
{
    public string RequestId { get; init; } = string.Empty;

    public bool Started { get; init; }

    public long ArtifactBytes { get; init; }

    public int AttemptCount { get; init; }

    public int ElapsedMs { get; init; }

    public int PlaybackSequence { get; init; }

    public string SupersededRequestId { get; init; } = string.Empty;

    public int SupersededSpeechCount { get; init; }

    public int SupersededSpeechAgeMs { get; init; }

    public long SupersededSpeechBufferedMs { get; init; }

    public long SupersededSpeechRemainingMs { get; init; }

    public string CancellationMode { get; init; } = string.Empty;

    public int SampleRateHz { get; init; }

    public int ChannelCount { get; init; }

    public int BitsPerSample { get; init; }

    public int DurationMs { get; init; }

    public long ByteRate { get; init; }

    public int BlockAlignBytes { get; init; }

    public long AudioDataBytes { get; init; }

    public long FrameCount { get; init; }

    public int BlockRemainderBytes { get; init; }

    public int ValidBitsPerSample { get; init; }

    public long ChannelMask { get; init; }

    public string AudioEncoding { get; init; } = string.Empty;

    public string SampleFormat { get; init; } = string.Empty;

    public string ByteOrder { get; init; } = string.Empty;

    public string MixerConversionHint { get; init; } = string.Empty;

    public int MixerQuantumMs { get; init; }

    public int MixerQuantumFrames { get; init; }

    public long MixerQueueDepthEstimate { get; init; }

    public int MixerTailFrames { get; init; }

    public long MixerBufferedMs { get; init; }

    public int MixerTailMs { get; init; }

    public string PlaybackMode { get; init; } = string.Empty;

    public string PlaybackHint { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public string FileExtension { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string FailureCode { get; init; } = string.Empty;
}

public sealed class SpeechPlaybackSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public bool Started { get; init; }

    public long ArtifactBytes { get; init; }

    public int AttemptCount { get; init; }

    public int ElapsedMs { get; init; }

    public int PlaybackSequence { get; init; }

    public string SupersededRequestId { get; init; } = string.Empty;

    public int SupersededSpeechCount { get; init; }

    public int SupersededSpeechAgeMs { get; init; }

    public long SupersededSpeechBufferedMs { get; init; }

    public long SupersededSpeechRemainingMs { get; init; }

    public string CancellationMode { get; init; } = string.Empty;

    public int SampleRateHz { get; init; }

    public int ChannelCount { get; init; }

    public int BitsPerSample { get; init; }

    public int DurationMs { get; init; }

    public long ByteRate { get; init; }

    public int BlockAlignBytes { get; init; }

    public long AudioDataBytes { get; init; }

    public long FrameCount { get; init; }

    public int BlockRemainderBytes { get; init; }

    public int ValidBitsPerSample { get; init; }

    public long ChannelMask { get; init; }

    public string AudioEncoding { get; init; } = string.Empty;

    public string SampleFormat { get; init; } = string.Empty;

    public string ByteOrder { get; init; } = string.Empty;

    public string MixerConversionHint { get; init; } = string.Empty;

    public int MixerQuantumMs { get; init; }

    public int MixerQuantumFrames { get; init; }

    public long MixerQueueDepthEstimate { get; init; }

    public int MixerTailFrames { get; init; }

    public long MixerBufferedMs { get; init; }

    public int MixerTailMs { get; init; }

    public string PlaybackMode { get; init; } = string.Empty;

    public string PlaybackHint { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public string FileExtension { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string FailureCode { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ReplyDeliverySnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string Speaker { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public string StrategyId { get; init; } = string.Empty;

    public string Phase { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public bool Rendered { get; init; }

    public string Surface { get; init; } = string.Empty;

    public string CardLabel { get; init; } = string.Empty;

    public int CardIndex { get; init; }

    public int CardCount { get; init; }

    public string Note { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class BridgeActionFeedbackSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    public string SourceStrategy { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class BridgeLoopProofSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public bool RequestSeen { get; init; }

    public bool OutboxReplyWritten { get; init; }

    public bool VisibleDeliveryConfirmed { get; init; }

    public bool ActionPlanned { get; init; }

    public bool ActionFeedbackObserved { get; init; }

    public bool SpeechPlaybackExpected { get; init; }

    public bool SpeechPlaybackObserved { get; init; }

    public bool SpeechPlaybackStarted { get; init; }

    public int SpeechPlaybackIngressLagMs { get; init; }

    public int SpeechPlaybackOutboxLagMs { get; init; }

    public int SpeechPlaybackDeliveryLagMs { get; init; }

    public bool LoopClosed { get; init; }

    public ChatIngressSnapshot? LastIngress { get; init; }

    public OutboxReplyTraceSnapshot? LastOutboxReply { get; init; }

    public ReplyDeliverySnapshot? LastReplyDelivery { get; init; }

    public BridgeActionFeedbackSnapshot? LastActionFeedback { get; init; }

    public SpeechPlaybackSnapshot? LastSpeechPlayback { get; init; }
}

public sealed class BridgeActivitySnapshot
{
    public long EventCount { get; init; }

    public long BootCount { get; init; }

    public string LastEventType { get; init; } = string.Empty;

    public DateTimeOffset? LastEventAtUtc { get; init; }

    public string LastEventSource { get; init; } = string.Empty;

    public BridgeBootPayload? LastBridgeBoot { get; init; }

    public UiProbeSnapshot? LastUiProbe { get; init; }

    public UiProbeDiagnosticsSnapshot? UiProbeDiagnostics { get; init; }

    public BridgeLoopProofSnapshot LoopProof { get; init; } = new();
}

public sealed class InferenceWarmupSnapshot
{
    public bool Enabled { get; init; }

    public string Status { get; init; } = string.Empty;

    public string ActiveModel { get; init; } = string.Empty;

    public string? ActiveTierId { get; init; }

    public IReadOnlyList<string> LastSeenAvailableModels { get; init; } =
        Array.Empty<string>();

    public string LastWarmedModel { get; init; } = string.Empty;

    public string LastReason { get; init; } = string.Empty;

    public string WarmupTransport { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;

    public DateTimeOffset? LastAttemptAtUtc { get; init; }

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public DateTimeOffset? LastLiveInferenceAtUtc { get; init; }

    public string LastLiveInferenceModel { get; init; } = string.Empty;

    public DateTimeOffset? LastFailureAtUtc { get; init; }

    public long AttemptCount { get; init; }

    public long SuccessCount { get; init; }

    public long FailureCount { get; init; }

    public long LastLatencyMs { get; init; }
}

public sealed class NativeReadinessSnapshot
{
    public bool BridgeBootSeen { get; init; }

    public string BridgeVersion { get; init; } = string.Empty;

    public string BridgeStatus { get; init; } = string.Empty;

    public string CompatSummary { get; init; } = string.Empty;

    public IReadOnlyList<BridgeBootCompatSignal> CompatSignals { get; init; } =
        Array.Empty<BridgeBootCompatSignal>();

    public bool UiProbeEnabled { get; init; }

    public bool HasPalGameStateCompat { get; init; }

    public bool HasPalCharacterCompat { get; init; }

    public bool HasPalBaseCampManagerCompat { get; init; }

    public bool HasPalMapManagerCompat { get; init; }

    public bool HasUserWidgetCompat { get; init; }

    public bool HasUiProbeCandidates { get; init; }

    public string TopUiProbeCandidate { get; init; } = string.Empty;

    public IReadOnlyList<string> ConfiguredHudTargets { get; init; } =
        Array.Empty<string>();

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public bool ActionExecutorEnabled { get; init; }

    public bool NativeHudEnabled { get; init; }

    public bool NativeHudTargetsConfigured { get; init; }

    public bool HudSeamDiscovered { get; init; }

    public bool HudBindReady { get; init; }

    public bool ProductionSamplerEnabled { get; init; }

    public bool ProductionSamplerReady { get; init; }

    public bool WaypointNativeMarkerEnabled { get; init; }

    public bool WaypointMarkerReady { get; init; }

    public HudBindRecommendationSnapshot HudBindRecommendation { get; init; } = new();

    public IReadOnlyList<string> ReadyCapabilities { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MissingPrerequisites { get; init; } =
        Array.Empty<string>();
}

public sealed class RuntimeWorldState
{
    public GameWorldSnapshot Snapshot { get; init; } = new();

    public BridgeActivitySnapshot Bridge { get; init; } = new();
}

public sealed class DashboardSnapshot
{
    public RuntimeHealth Health { get; init; } = new();

    public RuntimeWorldState World { get; init; } = new();

    public InferencePerformanceSnapshot InferencePerformance { get; init; } = new();

    public IReadOnlyList<CharacterRelationship> Relationships { get; init; } =
        Array.Empty<CharacterRelationship>();

    public IReadOnlyList<AdapterLogEntry> Logs { get; init; } =
        Array.Empty<AdapterLogEntry>();

    public IReadOnlyList<OutboxListing> Outbox { get; init; } =
        Array.Empty<OutboxListing>();

    public DateTimeOffset RefreshedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public long ServerLatencyMs { get; init; }
}

public sealed class InferencePerformanceSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int WindowMinutes { get; init; }

    public int RetainedOperationLimit { get; init; }

    public int SampleCount { get; init; }

    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }

    public long AverageLatencyMs { get; init; }

    public long P95LatencyMs { get; init; }

    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public long TotalTokens { get; init; }

    public long TotalCachedPromptTokens { get; init; }

    public long TotalPromptAudioTokens { get; init; }

    public long TotalCompletionReasoningTokens { get; init; }

    public long TotalCompletionAudioTokens { get; init; }

    public long TotalAcceptedPredictionTokens { get; init; }

    public long TotalRejectedPredictionTokens { get; init; }

    public DateTimeOffset? LastOperationAtUtc { get; init; }

    public InferencePerformanceAssessmentSnapshot Assessment { get; init; } = new();

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<InferencePerformanceLaneSnapshot> Lanes { get; init; } =
        Array.Empty<InferencePerformanceLaneSnapshot>();
}

public sealed class InferencePerformanceAssessmentSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string BudgetName { get; init; } = string.Empty;

    public int MinimumSampleCount { get; init; }

    public int SuccessRatioPercent { get; init; }

    public int TargetHitCount { get; init; }

    public int CeilingHitCount { get; init; }

    public int TargetHitRatioPercent { get; init; }

    public int CeilingHitRatioPercent { get; init; }

    public int? LatencyTargetMs { get; init; }

    public int? LatencyCeilingMs { get; init; }

    public string Summary { get; init; } = string.Empty;
}

public sealed class InferencePerformanceLaneSnapshot
{
    public string OperationName { get; init; } = string.Empty;

    public string ProviderName { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string RequestModel { get; init; } = string.Empty;

    public string ResponseModel { get; init; } = string.Empty;

    public string LastResponseId { get; init; } = string.Empty;

    public string LastUpstreamRequestId { get; init; } = string.Empty;

    public double? LastUpstreamProcessingMs { get; init; }

    public double? LastUpstreamQueueMs { get; init; }

    public double? LastUpstreamTimeToFirstTokenMs { get; init; }

    public double? LastUpstreamPrefillMs { get; init; }

    public double? LastUpstreamDecodeMs { get; init; }

    public string LastSystemFingerprint { get; init; } = string.Empty;

    public IReadOnlyList<string> LastFinishReasons { get; init; } =
        Array.Empty<string>();

    public int SampleCount { get; init; }

    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }

    public long AverageLatencyMs { get; init; }

    public long P95LatencyMs { get; init; }

    public long LastLatencyMs { get; init; }

    public long LastPromptTokens { get; init; }

    public long LastCompletionTokens { get; init; }

    public long LastTotalTokens { get; init; }

    public long LastCachedPromptTokens { get; init; }

    public long LastPromptAudioTokens { get; init; }

    public long LastCompletionReasoningTokens { get; init; }

    public long LastCompletionAudioTokens { get; init; }

    public long LastAcceptedPredictionTokens { get; init; }

    public long LastRejectedPredictionTokens { get; init; }

    public long AveragePromptTokens { get; init; }

    public long AverageCompletionTokens { get; init; }

    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public long TotalTokens { get; init; }

    public long TotalCachedPromptTokens { get; init; }

    public long TotalPromptAudioTokens { get; init; }

    public long TotalCompletionReasoningTokens { get; init; }

    public long TotalCompletionAudioTokens { get; init; }

    public long TotalAcceptedPredictionTokens { get; init; }

    public long TotalRejectedPredictionTokens { get; init; }

    public DateTimeOffset? LastObservedAtUtc { get; init; }

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public DateTimeOffset? LastFailureAtUtc { get; init; }

    public string LastErrorType { get; init; } = string.Empty;

    public InferencePerformanceAssessmentSnapshot Assessment { get; init; } = new();
}

