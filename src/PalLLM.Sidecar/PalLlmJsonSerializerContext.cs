using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar.Mcp;

namespace PalLLM.Sidecar;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RuntimeHealth))]
[JsonSerializable(typeof(BridgeLoopProofSnapshot))]
[JsonSerializable(typeof(ChatIngressSnapshot))]
[JsonSerializable(typeof(OutboxReplyTraceSnapshot))]
[JsonSerializable(typeof(ReplyDeliverySnapshot))]
[JsonSerializable(typeof(BridgeActionFeedbackSnapshot))]
[JsonSerializable(typeof(SpeechPlaybackSnapshot))]
[JsonSerializable(typeof(NativeReadinessSnapshot))]
[JsonSerializable(typeof(HudBindRecommendationSnapshot))]
[JsonSerializable(typeof(InferenceWarmupSnapshot))]
[JsonSerializable(typeof(BridgeBootPayload))]
[JsonSerializable(typeof(BridgeBootCompatSignal))]
[JsonSerializable(typeof(BridgeBootCompatSignal[]))]
[JsonSerializable(typeof(DashboardSnapshot))]
[JsonSerializable(typeof(InferencePerformanceSnapshot))]
[JsonSerializable(typeof(InferencePerformanceAssessmentSnapshot))]
[JsonSerializable(typeof(InferencePerformanceLaneSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<InferencePerformanceLaneSnapshot>))]
[JsonSerializable(typeof(InferencePerformanceHealthLaneSignal))]
[JsonSerializable(typeof(InferencePerformanceHealthLaneSignal[]))]
[JsonSerializable(typeof(RuntimeWorldState))]
[JsonSerializable(typeof(CharacterRelationship[]))]
[JsonSerializable(typeof(PackSummary[]))]
[JsonSerializable(typeof(PalLLM.Domain.Packs.SpeciesPersonalityResolution))]
[JsonSerializable(typeof(AdapterLogEntry[]))]
[JsonSerializable(typeof(OutboxListing[]))]
[JsonSerializable(typeof(ModelTierOptions[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int[]))]
[JsonSerializable(typeof(FeatureDescriptor[]))]
[JsonSerializable(typeof(SelfDescription))]
[JsonSerializable(typeof(QuickstartGuide))]
[JsonSerializable(typeof(AirGapReport))]
[JsonSerializable(typeof(WhyRequest))]
[JsonSerializable(typeof(WhyAnswer))]
[JsonSerializable(typeof(ModelRoleCoverage))]
[JsonSerializable(typeof(DuoPlanRequest))]
[JsonSerializable(typeof(DuoPlan))]
[JsonSerializable(typeof(DisagreementCheckRequest))]
[JsonSerializable(typeof(DisagreementAnalysis))]
[JsonSerializable(typeof(ProofPacketRequest))]
[JsonSerializable(typeof(ProofPacket))]
[JsonSerializable(typeof(PromotionRecordRequest))]
[JsonSerializable(typeof(PromotionObservation))]
[JsonSerializable(typeof(PromotionSummary))]
[JsonSerializable(typeof(PromotionSuggestionSet))]
[JsonSerializable(typeof(PromotionApplyPreviewRequest))]
[JsonSerializable(typeof(PromotionApplyPreview))]
[JsonSerializable(typeof(PromotionApplyRequest))]
[JsonSerializable(typeof(PromotionApplyResult))]
[JsonSerializable(typeof(HardwareProfile))]
[JsonSerializable(typeof(PrivacyPosture))]
[JsonSerializable(typeof(DirectivePlanRequest))]
[JsonSerializable(typeof(DirectivePlan))]
[JsonSerializable(typeof(DegradationAdvisory))]
[JsonSerializable(typeof(ResourceBudgetPosture))]
[JsonSerializable(typeof(NarrationCue))]
[JsonSerializable(typeof(MoodWeather))]
[JsonSerializable(typeof(LifetimeRelationshipAggregate))]
[JsonSerializable(typeof(LifetimeRelationshipSummary))]
[JsonSerializable(typeof(PartyChatRequest))]
[JsonSerializable(typeof(PartyChatResponse))]
[JsonSerializable(typeof(ChatPlanRequest))]
[JsonSerializable(typeof(ChatPlanAdvice))]
[JsonSerializable(typeof(ChatDispatchDecision))]
[JsonSerializable(typeof(GameWorldSnapshot))]
[JsonSerializable(typeof(GameCharacterSnapshot[]))]
[JsonSerializable(typeof(UpstreamSnapshot[]))]
[JsonSerializable(typeof(ReleaseReadinessSnapshot))]
[JsonSerializable(typeof(ReleaseSmokeEvidenceSnapshot))]
[JsonSerializable(typeof(ReleaseNativeProofEvidenceSnapshot))]
[JsonSerializable(typeof(ReleaseNativeProofStatusTransition))]
[JsonSerializable(typeof(ReleaseProofBundleEvidenceSnapshot))]
[JsonSerializable(typeof(ReleaseSupportBundleEvidenceSnapshot))]
[JsonSerializable(typeof(ReleasePackageVerificationEvidenceSnapshot))]
[JsonSerializable(typeof(ReleaseArtifactIntegrityEvidenceSnapshot))]
[JsonSerializable(typeof(ReleaseFullAuditEvidenceSnapshot))]
[JsonSerializable(typeof(BridgeProofSnapshot))]
[JsonSerializable(typeof(BridgeProofLaneSnapshot))]
[JsonSerializable(typeof(IReadOnlyList<BridgeProofLaneSnapshot>))]
[JsonSerializable(typeof(ChatResponse))]
[JsonSerializable(typeof(PresentationCuePlan))]
[JsonSerializable(typeof(SpeechArtifact))]
[JsonSerializable(typeof(AudioTranscribeRequest))]
[JsonSerializable(typeof(AudioTranscribeResponse))]
[JsonSerializable(typeof(AudioTranscriptionConfidenceReceipt))]
[JsonSerializable(typeof(AudioTranscriptionTimingReceipt))]
[JsonSerializable(typeof(AudioTranscriptionQualityReceipt))]
[JsonSerializable(typeof(AudioTurnEndpointingInput))]
[JsonSerializable(typeof(AudioTurnEndpointingReceipt))]
[JsonSerializable(typeof(ActionIntent))]
[JsonSerializable(typeof(ReleaseRuntimeSurfaceSummary))]
[JsonSerializable(typeof(ReleaseFeatureCatalogSummary))]
[JsonSerializable(typeof(ReleasePublicationSummary))]
[JsonSerializable(typeof(ReleaseSurfaceDescriptor[]))]
[JsonSerializable(typeof(ReleaseAuditDescriptor[]))]
[JsonSerializable(typeof(ReleaseDocumentDescriptor[]))]
[JsonSerializable(typeof(DashboardEtagPayload))]
[JsonSerializable(typeof(InferencePerformanceEtagPayload))]
[JsonSerializable(typeof(BridgeProofEtagPayload))]
[JsonSerializable(typeof(ReleaseReadinessEtagPayload))]
[JsonSerializable(typeof(HealthReportPayload))]
[JsonSerializable(typeof(HealthReportEntryPayload))]
[JsonSerializable(typeof(Dictionary<string, HealthReportEntryPayload>))]
[JsonSerializable(typeof(HealthReportDataValue))]
[JsonSerializable(typeof(Dictionary<string, HealthReportDataValue>))]
[JsonSerializable(typeof(ClearOutboxResponse))]
[JsonSerializable(typeof(SelfHealingStatusMarker))]
[JsonSerializable(typeof(ChatStreamStartedPayload))]
[JsonSerializable(typeof(ChatStreamPhasePayload))]
[JsonSerializable(typeof(ChatStreamFinalPrepPayload))]
[JsonSerializable(typeof(ChatStreamTokenPayload))]
[JsonSerializable(typeof(ChatStreamErrorPayload))]
[JsonSerializable(typeof(McpChatToolResultPayload))]
[JsonSerializable(typeof(McpStatusPayload))]
[JsonSerializable(typeof(McpMemoryRecallItem[]))]
[JsonSerializable(typeof(McpRecentBridgeEventsPayload))]
[JsonSerializable(typeof(McpActiveModelTierPayload))]
[JsonSerializable(typeof(McpCharacterProfileErrorPayload))]
[JsonSerializable(typeof(McpCharacterProfileNotFoundPayload))]
[JsonSerializable(typeof(FallbackStrategyCount))]
[JsonSerializable(typeof(IReadOnlyList<FallbackStrategyCount>))]
[JsonSerializable(typeof(ModelTierTransitionCount))]
[JsonSerializable(typeof(IReadOnlyList<ModelTierTransitionCount>))]
[JsonSerializable(typeof(ChatLatencyHistogram))]
[JsonSerializable(typeof(LatencyHistogramBucket))]
[JsonSerializable(typeof(IReadOnlyList<LatencyHistogramBucket>))]
[JsonSerializable(typeof(ModelCollaborationSnapshot))]
[JsonSerializable(typeof(ModelHardwareProfile))]
[JsonSerializable(typeof(ModelCollaborationModelDescriptor))]
[JsonSerializable(typeof(ModelCollaborationModelDescriptor[]))]
[JsonSerializable(typeof(ModelCapabilityProfile))]
[JsonSerializable(typeof(ModelSpeculationProfile))]
[JsonSerializable(typeof(ModelServingProfile))]
[JsonSerializable(typeof(ModelAuthorityProfile))]
[JsonSerializable(typeof(ModelCollaborationRecipe))]
[JsonSerializable(typeof(ModelCollaborationRecipe[]))]
[JsonSerializable(typeof(ModelCollaborationStage))]
[JsonSerializable(typeof(ModelCollaborationStage[]))]
[JsonSerializable(typeof(ModelTaskRoutingPolicy))]
[JsonSerializable(typeof(ModelTaskRoutingPolicy[]))]
[JsonSerializable(typeof(ModelQualificationSuite))]
[JsonSerializable(typeof(ModelQualificationCheck))]
[JsonSerializable(typeof(ModelQualificationCheck[]))]
[JsonSerializable(typeof(ModelHardwareTierPlaybook))]
[JsonSerializable(typeof(ModelHardwareTierPlaybook[]))]
[JsonSerializable(typeof(ModelCollaborationIdea))]
[JsonSerializable(typeof(ModelCollaborationIdea[]))]
[JsonSerializable(typeof(ModelCollaborationDecisionRequest))]
[JsonSerializable(typeof(ModelCollaborationDecision))]
[JsonSerializable(typeof(ModelLaneBooleanHints))]
[JsonSerializable(typeof(ModelLaneTextHints))]
internal partial class PalLlmJsonSerializerContext : JsonSerializerContext;

internal static class PalLlmJsonOptions
{
    public static void AddSourceGeneration(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.TypeInfoResolverChain.Contains(PalLlmJsonSerializerContext.Default))
        {
            options.TypeInfoResolverChain.Insert(0, PalLlmJsonSerializerContext.Default);
        }

        if (!options.TypeInfoResolverChain.Any(resolver => resolver is DefaultJsonTypeInfoResolver))
        {
            options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        }
    }

    public static JsonSerializerOptions Create(Action<JsonSerializerOptions>? configure = null)
    {
        JsonSerializerOptions options = new();
        AddSourceGeneration(options);
        configure?.Invoke(options);
        return options;
    }
}

internal sealed record DashboardEtagPayload(
    RuntimeHealth Health,
    RuntimeWorldState World,
    InferencePerformanceEtagPayload InferencePerformance,
    IReadOnlyList<CharacterRelationship> Relationships,
    IReadOnlyList<AdapterLogEntry> Logs,
    IReadOnlyList<OutboxListing> Outbox)
{
    public static DashboardEtagPayload From(DashboardSnapshot dashboard)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        return new(
            dashboard.Health,
            dashboard.World,
            InferencePerformanceEtagPayload.From(dashboard.InferencePerformance),
            dashboard.Relationships,
            dashboard.Logs,
            dashboard.Outbox);
    }
}

internal sealed record InferencePerformanceEtagPayload(
    int WindowMinutes,
    int RetainedOperationLimit,
    int SampleCount,
    int SuccessCount,
    int FailureCount,
    long AverageLatencyMs,
    long P95LatencyMs,
    long TotalPromptTokens,
    long TotalCompletionTokens,
    long TotalTokens,
    long TotalCachedPromptTokens,
    long TotalPromptAudioTokens,
    long TotalCompletionReasoningTokens,
    long TotalCompletionAudioTokens,
    long TotalAcceptedPredictionTokens,
    long TotalRejectedPredictionTokens,
    DateTimeOffset? LastOperationAtUtc,
    string Summary,
    IReadOnlyList<InferencePerformanceLaneSnapshot> Lanes)
{
    public static InferencePerformanceEtagPayload From(InferencePerformanceSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(
            snapshot.WindowMinutes,
            snapshot.RetainedOperationLimit,
            snapshot.SampleCount,
            snapshot.SuccessCount,
            snapshot.FailureCount,
            snapshot.AverageLatencyMs,
            snapshot.P95LatencyMs,
            snapshot.TotalPromptTokens,
            snapshot.TotalCompletionTokens,
            snapshot.TotalTokens,
            snapshot.TotalCachedPromptTokens,
            snapshot.TotalPromptAudioTokens,
            snapshot.TotalCompletionReasoningTokens,
            snapshot.TotalCompletionAudioTokens,
            snapshot.TotalAcceptedPredictionTokens,
            snapshot.TotalRejectedPredictionTokens,
            snapshot.LastOperationAtUtc,
            snapshot.Summary,
            snapshot.Lanes);
    }
}

internal sealed record BridgeProofEtagPayload(
    string Status,
    string Summary,
    string RecommendedNextStep,
    string ActiveRequestId,
    string LastBridgeEventType,
    DateTimeOffset? LastBridgeEventAtUtc,
    bool LiveDeliveryProven,
    bool NativeHudBindReady,
    NativeReadinessSnapshot NativeReadiness,
    BridgeLoopProofSnapshot LoopProof,
    BridgeBootPayload? LastBridgeBoot,
    UiProbeSnapshot? LastUiProbe,
    UiProbeDiagnosticsSnapshot? UiProbeDiagnostics,
    IReadOnlyList<BridgeProofLaneSnapshot> ProofLanes,
    IReadOnlyList<string> ReadyEvidence,
    IReadOnlyList<string> CurrentBlockers)
{
    public static BridgeProofEtagPayload From(BridgeProofSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(
            snapshot.Status,
            snapshot.Summary,
            snapshot.RecommendedNextStep,
            snapshot.ActiveRequestId,
            snapshot.LastBridgeEventType,
            snapshot.LastBridgeEventAtUtc,
            snapshot.LiveDeliveryProven,
            snapshot.NativeHudBindReady,
            snapshot.NativeReadiness,
            snapshot.LoopProof,
            snapshot.LastBridgeBoot,
            snapshot.LastUiProbe,
            snapshot.UiProbeDiagnostics,
            snapshot.ProofLanes,
            snapshot.ReadyEvidence,
            snapshot.CurrentBlockers);
    }
}

internal sealed record ReleaseReadinessEtagPayload(
    ReleaseRuntimeSurfaceSummary Runtime,
    ReleaseFeatureCatalogSummary Features,
    ReleasePublicationSummary Publication,
    ReleaseSmokeEvidenceSnapshot SmokeEvidence,
    ReleaseNativeProofEvidenceSnapshot NativeProofEvidence,
    ReleaseProofBundleEvidenceSnapshot ProofBundleEvidence,
    ReleaseSupportBundleEvidenceSnapshot SupportBundleEvidence,
    ReleasePackageVerificationEvidenceSnapshot PackageVerificationEvidence,
    ReleaseArtifactIntegrityEvidenceSnapshot ArtifactIntegrityEvidence,
    ReleaseFullAuditEvidenceSnapshot FullAuditEvidence,
    IReadOnlyList<ReleaseSurfaceDescriptor> Surfaces,
    IReadOnlyList<ReleaseAuditDescriptor> Audits,
    IReadOnlyList<ReleaseDocumentDescriptor> Documents)
{
    public static ReleaseReadinessEtagPayload From(ReleaseReadinessSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new(
            snapshot.Runtime,
            snapshot.Features,
            snapshot.Publication,
            snapshot.SmokeEvidence,
            snapshot.NativeProofEvidence,
            snapshot.ProofBundleEvidence,
            snapshot.SupportBundleEvidence,
            snapshot.PackageVerificationEvidence,
            snapshot.ArtifactIntegrityEvidence,
            snapshot.FullAuditEvidence,
            snapshot.Surfaces,
            snapshot.Audits,
            snapshot.Documents);
    }
}

internal sealed class HealthReportPayload
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("totalDurationMs")]
    public double TotalDurationMs { get; init; }

    [JsonPropertyName("results")]
    public Dictionary<string, HealthReportEntryPayload> Results { get; init; } = new(StringComparer.Ordinal);

    public static HealthReportPayload From(HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new HealthReportPayload
        {
            Status = report.Status.ToString(),
            TotalDurationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            Results = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => HealthReportEntryPayload.From(entry.Value),
                StringComparer.Ordinal),
        };
    }
}

internal sealed class HealthReportEntryPayload
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("durationMs")]
    public double DurationMs { get; init; }

    [JsonPropertyName("data")]
    public Dictionary<string, HealthReportDataValue> Data { get; init; } = new(StringComparer.Ordinal);

    public static HealthReportEntryPayload From(HealthReportEntry entry) =>
        new()
        {
            Status = entry.Status.ToString(),
            Description = entry.Description,
            DurationMs = Math.Round(entry.Duration.TotalMilliseconds, 2),
            Data = entry.Data.ToDictionary(
                pair => pair.Key,
                pair => HealthReportDataValue.From(pair.Value),
                StringComparer.Ordinal),
        };
}

[JsonConverter(typeof(HealthReportDataValueJsonConverter))]
internal readonly record struct HealthReportDataValue(object? RawValue)
{
    public static HealthReportDataValue From(object? value) => new(value);
}

internal sealed class HealthReportDataValueJsonConverter : JsonConverter<HealthReportDataValue>
{
    public override HealthReportDataValue Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options) =>
        throw new NotSupportedException("Health report payloads are write-only.");

    public override void Write(
        Utf8JsonWriter writer,
        HealthReportDataValue value,
        JsonSerializerOptions options)
    {
        switch (value.RawValue)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool boolean:
                writer.WriteBooleanValue(boolean);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case float number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case DateTimeOffset timestamp:
                writer.WriteStringValue(timestamp);
                break;
            case InferencePerformanceAssessmentSnapshot assessment:
                JsonSerializer.Serialize(
                    writer,
                    assessment,
                    PalLlmJsonSerializerContext.Default.InferencePerformanceAssessmentSnapshot);
                break;
            case InferencePerformanceHealthLaneSignal[] lanes:
                JsonSerializer.Serialize(
                    writer,
                    lanes,
                    PalLlmJsonSerializerContext.Default.InferencePerformanceHealthLaneSignalArray);
                break;
            case IReadOnlyList<InferencePerformanceHealthLaneSignal> lanes:
                JsonSerializer.Serialize(
                    writer,
                    lanes.ToArray(),
                    PalLlmJsonSerializerContext.Default.InferencePerformanceHealthLaneSignalArray);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}

internal sealed record ClearOutboxResponse([property: JsonPropertyName("removed")] int Removed);

internal sealed record SelfHealingStatusMarker(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail);

internal sealed record ChatStreamStartedPayload(
    [property: JsonPropertyName("request_id")] string RequestId);

internal sealed record ChatStreamPhasePayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string Detail);

internal sealed record ChatStreamFinalPrepPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("task_kind")] string? TaskKind,
    [property: JsonPropertyName("cooperation_pattern")] string? CooperationPattern,
    [property: JsonPropertyName("dispatch_mode")] string? DispatchMode,
    [property: JsonPropertyName("role_chain")] IReadOnlyList<string> RoleChain);

internal sealed record ChatStreamTokenPayload(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("text")] string Text);

internal sealed record ChatStreamErrorPayload(
    [property: JsonPropertyName("request_id")] string RequestId,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("retryable")] bool Retryable,
    [property: JsonPropertyName("reason")] string Reason);

internal sealed record McpChatToolResultPayload(
    string RequestId,
    string CharacterName,
    string? AssistantMessage,
    string ResponsePath,
    bool UsedFallback,
    string? FallbackStrategy,
    bool InferenceAttempted,
    string TaskKind);

internal sealed record McpStatusPayload(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail);

internal sealed record McpMemoryRecallItem(
    string CharacterName,
    string SpeakerRole,
    string Content,
    int? CharacterId,
    DateTimeOffset CreatedAtUtc,
    float? Importance,
    float Similarity);

internal sealed record McpRecentBridgeEventsPayload(
    AdapterLogEntry[] RecentLogs,
    OutboxListing[] RecentOutbox);

internal sealed record McpActiveModelTierPayload(
    string ActiveModel,
    string? ActiveTierId,
    string[] LastSeenAvailableModels,
    ModelTierOptions[] ConfiguredTiers,
    InferenceWarmupSnapshot Warmup);

internal sealed record McpCharacterProfileErrorPayload(string Error);

internal sealed record McpCharacterProfileNotFoundPayload(string Error, int[] AvailableIds);
