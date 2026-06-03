// Contracts (partial): release-readiness, native-proof, and bridge-proof evidence snapshots.
// Part of the PalLLM.Domain.Integration wire contract; see Contracts.cs for the core game/bridge/chat shapes.
using System.Text.Json;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Integration;

public sealed class ReleaseReadinessSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public ReleaseRuntimeSurfaceSummary Runtime { get; init; } = new();

    public ReleaseFeatureCatalogSummary Features { get; init; } = new();

    public ReleasePublicationSummary Publication { get; init; } = new();

    public ReleaseSmokeEvidenceSnapshot SmokeEvidence { get; init; } = new();

    public ReleaseNativeProofEvidenceSnapshot NativeProofEvidence { get; init; } = new();

    public ReleaseProofBundleEvidenceSnapshot ProofBundleEvidence { get; init; } = new();

    public ReleaseSupportBundleEvidenceSnapshot SupportBundleEvidence { get; init; } = new();

    public ReleasePackageVerificationEvidenceSnapshot PackageVerificationEvidence { get; init; } = new();

    public ReleaseArtifactIntegrityEvidenceSnapshot ArtifactIntegrityEvidence { get; init; } = new();

    public ReleaseFullAuditEvidenceSnapshot FullAuditEvidence { get; init; } = new();

    public IReadOnlyList<ReleaseSurfaceDescriptor> Surfaces { get; init; } =
        Array.Empty<ReleaseSurfaceDescriptor>();

    public IReadOnlyList<ReleaseAuditDescriptor> Audits { get; init; } =
        Array.Empty<ReleaseAuditDescriptor>();

    public IReadOnlyList<ReleaseDocumentDescriptor> Documents { get; init; } =
        Array.Empty<ReleaseDocumentDescriptor>();
}

public sealed class ReleaseSmokeEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string BridgeLoopStatus { get; init; } = string.Empty;

    public bool LoopClosed { get; init; }

    public bool VisibleDeliveryConfirmed { get; init; }

    public bool ActionFeedbackObserved { get; init; }

    public bool NativeHudBindReady { get; init; }

    public string RecommendedHudTarget { get; init; } = string.Empty;

    public IReadOnlyList<string> ConfiguredHudTargets { get; init; } =
        Array.Empty<string>();

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public string DeliverySurface { get; init; } = string.Empty;

    public string ActionType { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }
}

public sealed class ReleaseNativeProofEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public DateTimeOffset? WatcherStartedAtUtc { get; init; }

    public DateTimeOffset? WatcherFinishedAtUtc { get; init; }

    public string WatcherCompletionReason { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; }

    public int PollIntervalSeconds { get; init; }

    public int PollCount { get; init; }

    public bool TimedOut { get; init; }

    public string DiagnosisCode { get; init; } = string.Empty;

    public string DiagnosisSummary { get; init; } = string.Empty;

    public string DiagnosisAction { get; init; } = string.Empty;

    public string DiagnosisCommand { get; init; } = string.Empty;

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public bool LiveDeliveryProven { get; init; }

    public bool NativeHudBindReady { get; init; }

    public string RecommendedHudTarget { get; init; } = string.Empty;

    public IReadOnlyList<string> ConfiguredHudTargets { get; init; } =
        Array.Empty<string>();

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public string DeliverySurface { get; init; } = string.Empty;

    public string LoopStatus { get; init; } = string.Empty;

    public bool VisibleDeliveryConfirmed { get; init; }

    public bool ActionFeedbackObserved { get; init; }

    public bool AppliedHudRecommendation { get; init; }

    public string AppliedHudRecommendationPath { get; init; } = string.Empty;

    public string RecommendedNextStep { get; init; } = string.Empty;

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<ReleaseNativeProofStatusTransition> StatusTransitions { get; init; } =
        Array.Empty<ReleaseNativeProofStatusTransition>();
}

public sealed class ReleaseNativeProofStatusTransition
{
    public DateTimeOffset ObservedAtUtc { get; init; }

    public int PollIndex { get; init; }

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public string LoopStatus { get; init; } = string.Empty;

    public bool LiveDeliveryProven { get; init; }

    public bool NativeHudBindReady { get; init; }

    public bool VisibleDeliveryConfirmed { get; init; }

    public string DeliverySurface { get; init; } = string.Empty;
}

public sealed class ReleaseProofBundleEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string ArchivePath { get; init; } = string.Empty;

    public string HistoryArchivePath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string ReleasePublicationStatus { get; init; } = string.Empty;

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string SmokeEvidenceStatus { get; init; } = string.Empty;

    public string NativeProofEvidenceStatus { get; init; } = string.Empty;

    public string InferencePerformanceStatus { get; init; } = string.Empty;

    public int InferencePerformanceSampleCount { get; init; }

    public int InferencePerformanceLaneCount { get; init; }

    public int InferencePerformanceAlertingLaneCount { get; init; }

    public int InferencePerformanceLatestReceiptLaneCount { get; init; }

    public int InferencePerformanceTokenReceiptLaneCount { get; init; }

    public int InferencePerformanceFinishReasonReceiptLaneCount { get; init; }

    public int InferencePerformanceUpstreamRequestIdReceiptLaneCount { get; init; }

    public int InferencePerformanceUpstreamProcessingReceiptLaneCount { get; init; }

    public int InferencePerformancePhaseTimingReceiptLaneCount { get; init; }

    public int InferencePerformanceUsageDetailReceiptLaneCount { get; init; }

    public long InferencePerformanceTotalTokens { get; init; }

    public long InferencePerformanceCachedPromptTokens { get; init; }

    public long InferencePerformanceCompletionReasoningTokens { get; init; }

    public bool TtsEnabled { get; init; }

    public long TtsCallCount { get; init; }

    public long TtsFailureCount { get; init; }

    public long TtsSuccessEvidenceCount { get; init; }

    public bool AsrEnabled { get; init; }

    public long AsrCallCount { get; init; }

    public long AsrFailureCount { get; init; }

    public long AsrSuccessEvidenceCount { get; init; }

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

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public bool PrivacyRedactionApplied { get; init; }

    public int PrivacyRedactionCheckedFileCount { get; init; }

    public int PrivacyRedactionRedactedFileCount { get; init; }

    public IReadOnlyList<string> PrivacyRedactionRuleHits { get; init; } =
        Array.Empty<string>();

    public bool PublicationScanPassed { get; init; }

    public int PublicationScanCheckedFileCount { get; init; }

    public IReadOnlyList<string> PublicationScanViolations { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> IncludedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MissingOptionalFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseSupportBundleEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string ArchivePath { get; init; } = string.Empty;

    public string HistoryArchivePath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string RuntimeRoot { get; init; } = string.Empty;

    public string LaunchEvidenceStatus { get; init; } = string.Empty;

    public string SmokeEvidenceStatus { get; init; } = string.Empty;

    public string NativeProofEvidenceStatus { get; init; } = string.Empty;

    public string ProofBundleEvidenceStatus { get; init; } = string.Empty;

    public string PackageVerificationEvidenceStatus { get; init; } = string.Empty;

    public string FullAuditEvidenceStatus { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public bool PrivacyRedactionApplied { get; init; }

    public int PrivacyRedactionCheckedFileCount { get; init; }

    public int PrivacyRedactionRedactedFileCount { get; init; }

    public IReadOnlyList<string> PrivacyRedactionRuleHits { get; init; } =
        Array.Empty<string>();

    public bool PublicationScanPassed { get; init; }

    public int PublicationScanCheckedFileCount { get; init; }

    public IReadOnlyList<string> PublicationScanViolations { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> IncludedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MissingOptionalFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleasePackageVerificationEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string PackagePath { get; init; } = string.Empty;

    public string PackageKind { get; init; } = string.Empty;

    public string ReleaseName { get; init; } = string.Empty;

    public string ManifestRelativePath { get; init; } = string.Empty;

    public int ManifestSchemaVersion { get; init; }

    public string PackageSha256 { get; init; } = string.Empty;

    public bool VerifiedFromArchive { get; init; }

    public bool IncludesSidecarPublish { get; init; }

    public bool SelfContainedSidecar { get; init; }

    public bool RequiredFilesPresent { get; init; }

    public int CheckedFileCount { get; init; }

    public bool PublicationScanPassed { get; init; }

    public int PublicationScanCheckedFileCount { get; init; }

    public IReadOnlyList<string> MissingRequiredFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> UnexpectedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MismatchedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> PublicationScanViolations { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseArtifactIntegrityEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string PackagingRoot { get; init; } = string.Empty;

    public string ChecksumsJsonPath { get; init; } = string.Empty;

    public string Sha256SumsPath { get; init; } = string.Empty;

    public string Sha512SumsPath { get; init; } = string.Empty;

    public int ArtifactCount { get; init; }

    public bool ChecksumsJsonPresent { get; init; }

    public bool Sha256SumsPresent { get; init; }

    public bool Sha512SumsPresent { get; init; }

    public bool Sha512Skipped { get; init; }

    public bool DetachedSignaturePresent { get; init; }

    public IReadOnlyList<string> DetachedSignaturePaths { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseFullAuditEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string AuditRoot { get; init; } = string.Empty;

    public string ResultsPath { get; init; } = string.Empty;

    public string StepsDirectoryPath { get; init; } = string.Empty;

    public bool TestsEnabled { get; init; }

    public bool CoverageEnabled { get; init; }

    public bool SbomEnabled { get; init; }

    public bool PackagingEnabled { get; init; }

    public int TotalStepCount { get; init; }

    public int PassedStepCount { get; init; }

    public int FailedStepCount { get; init; }

    public IReadOnlyList<string> StepNames { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> FailedSteps { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class BridgeProofSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string RecommendedNextStep { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public string LastBridgeEventType { get; init; } = string.Empty;

    public DateTimeOffset? LastBridgeEventAtUtc { get; init; }

    public bool LiveDeliveryProven { get; init; }

    public bool NativeHudBindReady { get; init; }

    public NativeReadinessSnapshot NativeReadiness { get; init; } = new();

    public BridgeLoopProofSnapshot LoopProof { get; init; } = new();

    public BridgeBootPayload? LastBridgeBoot { get; init; }

    public UiProbeSnapshot? LastUiProbe { get; init; }

    public UiProbeDiagnosticsSnapshot? UiProbeDiagnostics { get; init; }

    public IReadOnlyList<BridgeProofLaneSnapshot> ProofLanes { get; init; } =
        Array.Empty<BridgeProofLaneSnapshot>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();
}

public sealed class BridgeProofLaneSnapshot
{
    public string Name { get; init; } = string.Empty;

    public bool Required { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string NextAction { get; init; } = string.Empty;
}

public sealed class ReleaseRuntimeSurfaceSummary
{
    public string AdapterName { get; init; } = string.Empty;

    public int ApiRouteCount { get; init; }

    public int ProtocolRouteCount { get; init; }

    public int FeaturedOperationalSurfaceCount { get; init; }

    public string DashboardPath { get; init; } = string.Empty;

    public string MetricsPath { get; init; } = string.Empty;

    public string OpenApiJsonPath { get; init; } = string.Empty;

    public string OpenApiYamlPath { get; init; } = string.Empty;

    public string McpPath { get; init; } = string.Empty;

    public IReadOnlyList<string> ConditionalReadPaths { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseFeatureCatalogSummary
{
    public int Total { get; init; }

    public int Ready { get; init; }

    public int Scaffolded { get; init; }

    public int Deferred { get; init; }

    public int Other { get; init; }
}

public sealed class ReleasePublicationSummary
{
    public string Status { get; init; } = string.Empty;

    public string NextRecommendedPass { get; init; } = string.Empty;

    public string NextRecommendedCommand { get; init; } = string.Empty;

    public IReadOnlyList<string> CurrentBlockers { get; init; } = Array.Empty<string>();
}

public sealed class ReleaseSurfaceDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Area { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;
}

public sealed class ReleaseAuditDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public string Purpose { get; init; } = string.Empty;
}

public sealed class ReleaseDocumentDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Purpose { get; init; } = string.Empty;
}

