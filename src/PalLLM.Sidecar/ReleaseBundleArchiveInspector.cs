using System.IO.Compression;
using System.Text.Json;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleaseBundleArchiveInspector
{
    public static ReleaseBundleArchiveInspection InspectProofBundle(
        ReleaseProofBundleEvidenceSnapshot snapshot,
        int maxManifestBytes) =>
        Inspect(
            archivePath: snapshot.ArchivePath,
            manifestEntryName: "proof-bundle.json",
            includedFiles: snapshot.IncludedFiles,
            maxManifestBytes: maxManifestBytes,
            readManifest: stream => JsonSerializer.Deserialize(
                stream,
                PalLlmJsonSerializerContext.Default.ReleaseProofBundleEvidenceSnapshot),
            matchesRecordedManifest: archived =>
                archived is not null &&
                string.Equals(archived.Status, snapshot.Status, StringComparison.Ordinal) &&
                archived.CapturedAtUtc == snapshot.CapturedAtUtc &&
                string.Equals(archived.ReleasePublicationStatus, snapshot.ReleasePublicationStatus, StringComparison.Ordinal) &&
                string.Equals(archived.BridgeProofStatus, snapshot.BridgeProofStatus, StringComparison.Ordinal) &&
                string.Equals(archived.SmokeEvidenceStatus, snapshot.SmokeEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.NativeProofEvidenceStatus, snapshot.NativeProofEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.InferencePerformanceStatus, snapshot.InferencePerformanceStatus, StringComparison.Ordinal) &&
                archived.InferencePerformanceSampleCount == snapshot.InferencePerformanceSampleCount &&
                archived.InferencePerformanceLaneCount == snapshot.InferencePerformanceLaneCount &&
                archived.InferencePerformanceAlertingLaneCount == snapshot.InferencePerformanceAlertingLaneCount &&
                archived.InferencePerformanceLatestReceiptLaneCount == snapshot.InferencePerformanceLatestReceiptLaneCount &&
                archived.InferencePerformanceTokenReceiptLaneCount == snapshot.InferencePerformanceTokenReceiptLaneCount &&
                archived.InferencePerformanceFinishReasonReceiptLaneCount == snapshot.InferencePerformanceFinishReasonReceiptLaneCount &&
                archived.InferencePerformanceUpstreamRequestIdReceiptLaneCount == snapshot.InferencePerformanceUpstreamRequestIdReceiptLaneCount &&
                archived.InferencePerformanceUpstreamProcessingReceiptLaneCount == snapshot.InferencePerformanceUpstreamProcessingReceiptLaneCount &&
                archived.InferencePerformancePhaseTimingReceiptLaneCount == snapshot.InferencePerformancePhaseTimingReceiptLaneCount &&
                archived.InferencePerformanceUsageDetailReceiptLaneCount == snapshot.InferencePerformanceUsageDetailReceiptLaneCount &&
                archived.InferencePerformanceTotalTokens == snapshot.InferencePerformanceTotalTokens &&
                archived.InferencePerformanceCachedPromptTokens == snapshot.InferencePerformanceCachedPromptTokens &&
                archived.InferencePerformanceCompletionReasoningTokens == snapshot.InferencePerformanceCompletionReasoningTokens &&
                archived.TtsEnabled == snapshot.TtsEnabled &&
                archived.TtsCallCount == snapshot.TtsCallCount &&
                archived.TtsFailureCount == snapshot.TtsFailureCount &&
                archived.TtsSuccessEvidenceCount == snapshot.TtsSuccessEvidenceCount &&
                archived.AsrEnabled == snapshot.AsrEnabled &&
                archived.AsrCallCount == snapshot.AsrCallCount &&
                archived.AsrFailureCount == snapshot.AsrFailureCount &&
                archived.AsrSuccessEvidenceCount == snapshot.AsrSuccessEvidenceCount &&
                archived.AsrEndpointingReceiptCount == snapshot.AsrEndpointingReceiptCount &&
                archived.AsrBargeInCount == snapshot.AsrBargeInCount &&
                archived.AsrEndpointingReviewCount == snapshot.AsrEndpointingReviewCount &&
                archived.AsrConfidenceReceiptCount == snapshot.AsrConfidenceReceiptCount &&
                archived.AsrConfidenceReviewCount == snapshot.AsrConfidenceReviewCount &&
                archived.AsrTimingReceiptCount == snapshot.AsrTimingReceiptCount &&
                archived.AsrTimingReviewCount == snapshot.AsrTimingReviewCount &&
                archived.AsrQualityReceiptCount == snapshot.AsrQualityReceiptCount &&
                archived.AsrQualityReviewCount == snapshot.AsrQualityReviewCount &&
                archived.AsrUpstreamRequestIdReceiptCount == snapshot.AsrUpstreamRequestIdReceiptCount &&
                archived.AsrUpstreamProcessingReceiptCount == snapshot.AsrUpstreamProcessingReceiptCount &&
                archived.AsrUpstreamPhaseTimingReceiptCount == snapshot.AsrUpstreamPhaseTimingReceiptCount &&
                string.Equals(archived.NativeHudConfigSource, snapshot.NativeHudConfigSource, StringComparison.Ordinal) &&
                string.Equals(archived.NativeHudConfigPath, snapshot.NativeHudConfigPath, StringComparison.Ordinal) &&
                archived.PrivacyRedactionApplied == snapshot.PrivacyRedactionApplied &&
                archived.PrivacyRedactionCheckedFileCount == snapshot.PrivacyRedactionCheckedFileCount &&
                archived.PrivacyRedactionRedactedFileCount == snapshot.PrivacyRedactionRedactedFileCount &&
                SameStrings(archived.PrivacyRedactionRuleHits, snapshot.PrivacyRedactionRuleHits) &&
                archived.PublicationScanPassed == snapshot.PublicationScanPassed &&
                archived.PublicationScanCheckedFileCount == snapshot.PublicationScanCheckedFileCount &&
                SameStrings(archived.PublicationScanViolations, snapshot.PublicationScanViolations) &&
                SameStrings(archived.IncludedFiles, snapshot.IncludedFiles) &&
                SameStrings(archived.MissingOptionalFiles, snapshot.MissingOptionalFiles) &&
                SameStrings(archived.CurrentBlockers, snapshot.CurrentBlockers) &&
                SameStrings(archived.ReadyEvidence, snapshot.ReadyEvidence));

    public static ReleaseBundleArchiveInspection InspectSupportBundle(
        ReleaseSupportBundleEvidenceSnapshot snapshot,
        int maxManifestBytes) =>
        Inspect(
            archivePath: snapshot.ArchivePath,
            manifestEntryName: "support-bundle.json",
            includedFiles: snapshot.IncludedFiles,
            maxManifestBytes: maxManifestBytes,
            readManifest: stream => JsonSerializer.Deserialize(
                stream,
                PalLlmJsonSerializerContext.Default.ReleaseSupportBundleEvidenceSnapshot),
            matchesRecordedManifest: archived =>
                archived is not null &&
                string.Equals(archived.Status, snapshot.Status, StringComparison.Ordinal) &&
                archived.CapturedAtUtc == snapshot.CapturedAtUtc &&
                string.Equals(archived.RuntimeRoot, snapshot.RuntimeRoot, StringComparison.Ordinal) &&
                string.Equals(archived.LaunchEvidenceStatus, snapshot.LaunchEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.SmokeEvidenceStatus, snapshot.SmokeEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.NativeProofEvidenceStatus, snapshot.NativeProofEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.ProofBundleEvidenceStatus, snapshot.ProofBundleEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.PackageVerificationEvidenceStatus, snapshot.PackageVerificationEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.FullAuditEvidenceStatus, snapshot.FullAuditEvidenceStatus, StringComparison.Ordinal) &&
                string.Equals(archived.NativeHudConfigPath, snapshot.NativeHudConfigPath, StringComparison.Ordinal) &&
                archived.PrivacyRedactionApplied == snapshot.PrivacyRedactionApplied &&
                archived.PrivacyRedactionCheckedFileCount == snapshot.PrivacyRedactionCheckedFileCount &&
                archived.PrivacyRedactionRedactedFileCount == snapshot.PrivacyRedactionRedactedFileCount &&
                SameStrings(archived.PrivacyRedactionRuleHits, snapshot.PrivacyRedactionRuleHits) &&
                archived.PublicationScanPassed == snapshot.PublicationScanPassed &&
                archived.PublicationScanCheckedFileCount == snapshot.PublicationScanCheckedFileCount &&
                SameStrings(archived.PublicationScanViolations, snapshot.PublicationScanViolations) &&
                SameStrings(archived.IncludedFiles, snapshot.IncludedFiles) &&
                SameStrings(archived.MissingOptionalFiles, snapshot.MissingOptionalFiles) &&
                SameStrings(archived.CurrentBlockers, snapshot.CurrentBlockers) &&
                SameStrings(archived.ReadyEvidence, snapshot.ReadyEvidence));

    private static ReleaseBundleArchiveInspection Inspect<TManifest>(
        string archivePath,
        string manifestEntryName,
        IEnumerable<string>? includedFiles,
        int maxManifestBytes,
        Func<Stream, TManifest?> readManifest,
        Func<TManifest?, bool> matchesRecordedManifest)
    {
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return ReleaseBundleArchiveInspection.Invalid("The latest portable bundle archive path is missing.");
        }

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            HashSet<string> archiveEntries = new(StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (!IsSafeArchiveEntryName(entry.FullName))
                {
                    return ReleaseBundleArchiveInspection.Invalid(
                        "The latest portable bundle archive contains unsafe entry names.");
                }

                if (string.IsNullOrWhiteSpace(entry.Name))
                {
                    continue;
                }

                string normalizedEntryName = NormalizeEntryName(entry.FullName);
                if (!archiveEntries.Add(normalizedEntryName))
                {
                    return ReleaseBundleArchiveInspection.Invalid(
                        "The latest portable bundle archive contains duplicate normalized file entries.");
                }
            }

            string normalizedManifestName = NormalizeEntryName(manifestEntryName);
            ZipArchiveEntry? manifestEntry = archive.GetEntry(normalizedManifestName);
            if (manifestEntry is null)
            {
                return ReleaseBundleArchiveInspection.Invalid(
                    $"The latest portable bundle archive is missing '{normalizedManifestName}'.");
            }

            int effectiveMaxManifestBytes = Math.Max(1_024, maxManifestBytes);
            if (manifestEntry.Length > effectiveMaxManifestBytes)
            {
                return ReleaseBundleArchiveInspection.Invalid(
                    $"The archived portable bundle manifest exceeds the configured size limit of {effectiveMaxManifestBytes} bytes.");
            }

            using Stream manifestStream = manifestEntry.Open();
            TManifest? archivedManifest = readManifest(manifestStream);
            if (!matchesRecordedManifest(archivedManifest))
            {
                return ReleaseBundleArchiveInspection.Invalid(
                    $"The latest portable bundle archive contains a stale or mismatched '{normalizedManifestName}'.");
            }

            string[] requiredEntries = BuildRequiredEntryNames(includedFiles, manifestEntryName);
            if (requiredEntries.Any(entry => !IsSafeArchiveEntryName(entry)))
            {
                return ReleaseBundleArchiveInspection.Invalid(
                    "The latest portable bundle manifest lists unsafe entry names.");
            }

            string[] missingEntries = requiredEntries
                .Select(NormalizeEntryName)
                .Where(entry => !archiveEntries.Contains(entry))
                .ToArray();
            if (missingEntries.Length > 0)
            {
                return ReleaseBundleArchiveInspection.Invalid(
                    "The latest portable bundle archive is missing manifest-listed entries: " +
                    string.Join(", ", missingEntries) +
                    ".");
            }

            return ReleaseBundleArchiveInspection.Valid();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return ReleaseBundleArchiveInspection.Invalid(
                "The latest portable bundle archive could not be read as a safe zip archive.");
        }
    }

    private static string[] BuildRequiredEntryNames(
        IEnumerable<string>? includedFiles,
        string manifestEntryName) =>
        (includedFiles ?? Array.Empty<string>())
            .Append(manifestEntryName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string NormalizeEntryName(string entryName) =>
        entryName.Replace('\\', '/').Trim();

    // Pass 217: bumped from `private` to `internal` so the test project (which
    // has `InternalsVisibleTo PalLLM.Tests`) can pin every rejection branch
    // directly with fast unit tests instead of going through full
    // /api/release/readiness integration fixtures. The semantic surface is
    // unchanged for any caller outside this assembly.
    internal static bool IsSafeArchiveEntryName(string entryName)
    {
        string normalized = NormalizeEntryName(entryName);
        if (normalized.Length == 0 ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (char ch in normalized)
        {
            if (char.IsControl(ch))
            {
                return false;
            }
        }

        string[] segments = normalized.Split('/');
        return segments.All(segment =>
            segment.Length > 0 &&
            !string.Equals(segment, ".", StringComparison.Ordinal) &&
            !string.Equals(segment, "..", StringComparison.Ordinal));
    }

    private static bool SameStrings(
        IEnumerable<string>? left,
        IEnumerable<string>? right) =>
        (left ?? Array.Empty<string>()).SequenceEqual(right ?? Array.Empty<string>(), StringComparer.Ordinal);
}

internal readonly record struct ReleaseBundleArchiveInspection(bool Succeeded, string Summary)
{
    public static ReleaseBundleArchiveInspection Valid() => new(true, string.Empty);

    public static ReleaseBundleArchiveInspection Invalid(string summary) => new(false, summary);
}
