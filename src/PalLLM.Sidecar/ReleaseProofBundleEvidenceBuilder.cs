using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleaseProofBundleEvidenceBuilder
{
    public static ReleaseProofBundleEvidenceSnapshot ReadLatest(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int freshnessWindowHours = options.ReleaseEvidenceFreshnessHours;
        string artifactPath = options.LatestProofBundleEvidencePath;
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(artifactPath))
        {
            return new ReleaseProofBundleEvidenceSnapshot
            {
                Status = "missing",
                Summary = "No Palworld release proof bundle has been exported yet. Run scripts/export-release-proof-bundle.ps1 after capturing smoke and native proof.",
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
                ArchivePath = options.LatestProofBundleArchivePath,
            };
        }

        try
        {
            ArtifactJsonFileReader.ArtifactJsonReadResult<ReleaseProofBundleEvidenceSnapshot> readResult =
                ArtifactJsonFileReader.TryRead(
                    artifactPath,
                    PalLlmJsonSerializerContext.Default.ReleaseProofBundleEvidenceSnapshot,
                    maxBytes);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                return new ReleaseProofBundleEvidenceSnapshot
                {
                    Status = "invalid",
                    Summary = ArtifactJsonFileReader.BuildFailureMessage(
                        "The latest proof bundle artifact",
                        readResult.FailureCode ?? ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                        maxBytes),
                    FreshnessStatus = "unknown",
                    FreshnessWindowHours = freshnessWindowHours,
                    ArtifactPath = artifactPath,
                    ArchivePath = options.LatestProofBundleArchivePath,
                };
            }

            ReleaseProofBundleEvidenceSnapshot normalized = Normalize(
                readResult.Value,
                artifactPath,
                options.LatestProofBundleArchivePath,
                freshnessWindowHours);
            if (string.IsNullOrWhiteSpace(normalized.ArchivePath) || !File.Exists(normalized.ArchivePath))
            {
                return InvalidFrom(
                    normalized,
                    $"The latest proof bundle manifest exists at '{artifactPath}', but the matching archive '{normalized.ArchivePath}' is missing.");
            }

            ReleaseBundleArchiveInspection archiveInspection =
                ReleaseBundleArchiveInspector.InspectProofBundle(normalized, maxBytes);
            if (!archiveInspection.Succeeded)
            {
                return InvalidFrom(normalized, archiveInspection.Summary);
            }

            return normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseProofBundleEvidenceSnapshot
            {
                Status = "invalid",
                Summary = ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest proof bundle artifact",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                    maxBytes),
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
                ArchivePath = options.LatestProofBundleArchivePath,
            };
        }
    }

    private static ReleaseProofBundleEvidenceSnapshot Normalize(
        ReleaseProofBundleEvidenceSnapshot snapshot,
        string artifactPath,
        string defaultArchivePath,
        int freshnessWindowHours)
    {
        ReleaseEvidenceFreshnessSnapshot freshness = ReleaseEvidenceFreshness.Evaluate(
            snapshot.CapturedAtUtc,
            freshnessWindowHours);
        string status = string.IsNullOrWhiteSpace(snapshot.Status)
            ? "recorded"
            : snapshot.Status.Trim();
        string summary = string.IsNullOrWhiteSpace(snapshot.Summary)
            ? "Palworld release proof bundle is available."
            : snapshot.Summary.Trim();
        if (string.Equals(freshness.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            summary = $"{summary} Refresh it before trusting this release candidate.";
        }

        string[] includedFiles = (snapshot.IncludedFiles ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] missingOptionalFiles = (snapshot.MissingOptionalFiles ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] currentBlockers = (snapshot.CurrentBlockers ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] readyEvidence = (snapshot.ReadyEvidence ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] publicationScanViolations = (snapshot.PublicationScanViolations ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] privacyRedactionRuleHits = (snapshot.PrivacyRedactionRuleHits ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ReleaseProofBundleEvidenceSnapshot
        {
            Status = status,
            Summary = summary,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            FreshnessStatus = freshness.Status,
            FreshUntilUtc = freshness.FreshUntilUtc,
            FreshnessWindowHours = freshness.FreshnessWindowHours,
            ArtifactPath = string.IsNullOrWhiteSpace(snapshot.ArtifactPath)
                ? artifactPath
                : snapshot.ArtifactPath.Trim(),
            HistoryArtifactPath = snapshot.HistoryArtifactPath?.Trim() ?? string.Empty,
            ArchivePath = string.IsNullOrWhiteSpace(snapshot.ArchivePath)
                ? defaultArchivePath
                : snapshot.ArchivePath.Trim(),
            HistoryArchivePath = snapshot.HistoryArchivePath?.Trim() ?? string.Empty,
            BaseUrl = snapshot.BaseUrl?.Trim() ?? string.Empty,
            ReleasePublicationStatus = snapshot.ReleasePublicationStatus?.Trim() ?? string.Empty,
            BridgeProofStatus = snapshot.BridgeProofStatus?.Trim() ?? string.Empty,
            SmokeEvidenceStatus = snapshot.SmokeEvidenceStatus?.Trim() ?? string.Empty,
            NativeProofEvidenceStatus = snapshot.NativeProofEvidenceStatus?.Trim() ?? string.Empty,
            InferencePerformanceStatus = snapshot.InferencePerformanceStatus?.Trim() ?? string.Empty,
            InferencePerformanceSampleCount = Math.Max(0, snapshot.InferencePerformanceSampleCount),
            InferencePerformanceLaneCount = Math.Max(0, snapshot.InferencePerformanceLaneCount),
            InferencePerformanceAlertingLaneCount = Math.Max(0, snapshot.InferencePerformanceAlertingLaneCount),
            InferencePerformanceLatestReceiptLaneCount = Math.Max(0, snapshot.InferencePerformanceLatestReceiptLaneCount),
            InferencePerformanceTokenReceiptLaneCount = Math.Max(0, snapshot.InferencePerformanceTokenReceiptLaneCount),
            InferencePerformanceFinishReasonReceiptLaneCount = Math.Max(0, snapshot.InferencePerformanceFinishReasonReceiptLaneCount),
            InferencePerformanceUpstreamRequestIdReceiptLaneCount = Math.Max(0, snapshot.InferencePerformanceUpstreamRequestIdReceiptLaneCount),
            InferencePerformanceUpstreamProcessingReceiptLaneCount = Math.Max(0, snapshot.InferencePerformanceUpstreamProcessingReceiptLaneCount),
            InferencePerformancePhaseTimingReceiptLaneCount = Math.Max(0, snapshot.InferencePerformancePhaseTimingReceiptLaneCount),
            InferencePerformanceUsageDetailReceiptLaneCount = Math.Max(0, snapshot.InferencePerformanceUsageDetailReceiptLaneCount),
            InferencePerformanceTotalTokens = Math.Max(0, snapshot.InferencePerformanceTotalTokens),
            InferencePerformanceCachedPromptTokens = Math.Max(0, snapshot.InferencePerformanceCachedPromptTokens),
            InferencePerformanceCompletionReasoningTokens = Math.Max(0, snapshot.InferencePerformanceCompletionReasoningTokens),
            TtsEnabled = snapshot.TtsEnabled,
            TtsCallCount = Math.Max(0, snapshot.TtsCallCount),
            TtsFailureCount = Math.Max(0, snapshot.TtsFailureCount),
            TtsSuccessEvidenceCount = Math.Max(0, snapshot.TtsSuccessEvidenceCount),
            AsrEnabled = snapshot.AsrEnabled,
            AsrCallCount = Math.Max(0, snapshot.AsrCallCount),
            AsrFailureCount = Math.Max(0, snapshot.AsrFailureCount),
            AsrSuccessEvidenceCount = Math.Max(0, snapshot.AsrSuccessEvidenceCount),
            AsrEndpointingReceiptCount = Math.Max(0, snapshot.AsrEndpointingReceiptCount),
            AsrBargeInCount = Math.Max(0, snapshot.AsrBargeInCount),
            AsrEndpointingReviewCount = Math.Max(0, snapshot.AsrEndpointingReviewCount),
            AsrConfidenceReceiptCount = Math.Max(0, snapshot.AsrConfidenceReceiptCount),
            AsrConfidenceReviewCount = Math.Max(0, snapshot.AsrConfidenceReviewCount),
            AsrTimingReceiptCount = Math.Max(0, snapshot.AsrTimingReceiptCount),
            AsrTimingReviewCount = Math.Max(0, snapshot.AsrTimingReviewCount),
            AsrQualityReceiptCount = Math.Max(0, snapshot.AsrQualityReceiptCount),
            AsrQualityReviewCount = Math.Max(0, snapshot.AsrQualityReviewCount),
            AsrUpstreamRequestIdReceiptCount = Math.Max(0, snapshot.AsrUpstreamRequestIdReceiptCount),
            AsrUpstreamProcessingReceiptCount = Math.Max(0, snapshot.AsrUpstreamProcessingReceiptCount),
            AsrUpstreamPhaseTimingReceiptCount = Math.Max(0, snapshot.AsrUpstreamPhaseTimingReceiptCount),
            NativeHudConfigSource = snapshot.NativeHudConfigSource?.Trim() ?? string.Empty,
            NativeHudConfigPath = snapshot.NativeHudConfigPath?.Trim() ?? string.Empty,
            PrivacyRedactionApplied = snapshot.PrivacyRedactionApplied,
            PrivacyRedactionCheckedFileCount = snapshot.PrivacyRedactionCheckedFileCount,
            PrivacyRedactionRedactedFileCount = snapshot.PrivacyRedactionRedactedFileCount,
            PrivacyRedactionRuleHits = privacyRedactionRuleHits,
            PublicationScanPassed = snapshot.PublicationScanPassed,
            PublicationScanCheckedFileCount = snapshot.PublicationScanCheckedFileCount,
            PublicationScanViolations = publicationScanViolations,
            IncludedFiles = includedFiles,
            MissingOptionalFiles = missingOptionalFiles,
            CurrentBlockers = currentBlockers,
            ReadyEvidence = readyEvidence,
        };
    }

    private static ReleaseProofBundleEvidenceSnapshot InvalidFrom(
        ReleaseProofBundleEvidenceSnapshot snapshot,
        string summary) =>
        new()
        {
            Status = "invalid",
            Summary = summary,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            FreshnessStatus = snapshot.FreshnessStatus,
            FreshUntilUtc = snapshot.FreshUntilUtc,
            FreshnessWindowHours = snapshot.FreshnessWindowHours,
            ArtifactPath = snapshot.ArtifactPath,
            HistoryArtifactPath = snapshot.HistoryArtifactPath,
            ArchivePath = snapshot.ArchivePath,
            HistoryArchivePath = snapshot.HistoryArchivePath,
            BaseUrl = snapshot.BaseUrl,
            ReleasePublicationStatus = snapshot.ReleasePublicationStatus,
            BridgeProofStatus = snapshot.BridgeProofStatus,
            SmokeEvidenceStatus = snapshot.SmokeEvidenceStatus,
            NativeProofEvidenceStatus = snapshot.NativeProofEvidenceStatus,
            InferencePerformanceStatus = snapshot.InferencePerformanceStatus,
            InferencePerformanceSampleCount = snapshot.InferencePerformanceSampleCount,
            InferencePerformanceLaneCount = snapshot.InferencePerformanceLaneCount,
            InferencePerformanceAlertingLaneCount = snapshot.InferencePerformanceAlertingLaneCount,
            InferencePerformanceLatestReceiptLaneCount = snapshot.InferencePerformanceLatestReceiptLaneCount,
            InferencePerformanceTokenReceiptLaneCount = snapshot.InferencePerformanceTokenReceiptLaneCount,
            InferencePerformanceFinishReasonReceiptLaneCount = snapshot.InferencePerformanceFinishReasonReceiptLaneCount,
            InferencePerformanceUpstreamRequestIdReceiptLaneCount = snapshot.InferencePerformanceUpstreamRequestIdReceiptLaneCount,
            InferencePerformanceUpstreamProcessingReceiptLaneCount = snapshot.InferencePerformanceUpstreamProcessingReceiptLaneCount,
            InferencePerformancePhaseTimingReceiptLaneCount = snapshot.InferencePerformancePhaseTimingReceiptLaneCount,
            InferencePerformanceUsageDetailReceiptLaneCount = snapshot.InferencePerformanceUsageDetailReceiptLaneCount,
            InferencePerformanceTotalTokens = snapshot.InferencePerformanceTotalTokens,
            InferencePerformanceCachedPromptTokens = snapshot.InferencePerformanceCachedPromptTokens,
            InferencePerformanceCompletionReasoningTokens = snapshot.InferencePerformanceCompletionReasoningTokens,
            TtsEnabled = snapshot.TtsEnabled,
            TtsCallCount = snapshot.TtsCallCount,
            TtsFailureCount = snapshot.TtsFailureCount,
            TtsSuccessEvidenceCount = snapshot.TtsSuccessEvidenceCount,
            AsrEnabled = snapshot.AsrEnabled,
            AsrCallCount = snapshot.AsrCallCount,
            AsrFailureCount = snapshot.AsrFailureCount,
            AsrSuccessEvidenceCount = snapshot.AsrSuccessEvidenceCount,
            AsrEndpointingReceiptCount = snapshot.AsrEndpointingReceiptCount,
            AsrBargeInCount = snapshot.AsrBargeInCount,
            AsrEndpointingReviewCount = snapshot.AsrEndpointingReviewCount,
            AsrConfidenceReceiptCount = snapshot.AsrConfidenceReceiptCount,
            AsrConfidenceReviewCount = snapshot.AsrConfidenceReviewCount,
            AsrTimingReceiptCount = snapshot.AsrTimingReceiptCount,
            AsrTimingReviewCount = snapshot.AsrTimingReviewCount,
            AsrQualityReceiptCount = snapshot.AsrQualityReceiptCount,
            AsrQualityReviewCount = snapshot.AsrQualityReviewCount,
            AsrUpstreamRequestIdReceiptCount = snapshot.AsrUpstreamRequestIdReceiptCount,
            AsrUpstreamProcessingReceiptCount = snapshot.AsrUpstreamProcessingReceiptCount,
            AsrUpstreamPhaseTimingReceiptCount = snapshot.AsrUpstreamPhaseTimingReceiptCount,
            NativeHudConfigSource = snapshot.NativeHudConfigSource,
            NativeHudConfigPath = snapshot.NativeHudConfigPath,
            PrivacyRedactionApplied = snapshot.PrivacyRedactionApplied,
            PrivacyRedactionCheckedFileCount = snapshot.PrivacyRedactionCheckedFileCount,
            PrivacyRedactionRedactedFileCount = snapshot.PrivacyRedactionRedactedFileCount,
            PrivacyRedactionRuleHits = snapshot.PrivacyRedactionRuleHits,
            PublicationScanPassed = snapshot.PublicationScanPassed,
            PublicationScanCheckedFileCount = snapshot.PublicationScanCheckedFileCount,
            PublicationScanViolations = snapshot.PublicationScanViolations,
            IncludedFiles = snapshot.IncludedFiles,
            MissingOptionalFiles = snapshot.MissingOptionalFiles,
            CurrentBlockers = snapshot.CurrentBlockers,
            ReadyEvidence = snapshot.ReadyEvidence,
        };
}
