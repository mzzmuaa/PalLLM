using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleaseSupportBundleEvidenceBuilder
{
    public static ReleaseSupportBundleEvidenceSnapshot ReadLatest(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int freshnessWindowHours = options.ReleaseEvidenceFreshnessHours;
        string artifactPath = options.LatestSupportBundleEvidencePath;
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(artifactPath))
        {
            return new ReleaseSupportBundleEvidenceSnapshot
            {
                Status = "missing",
                Summary = "No PalLLM support bundle has been captured yet. Run scripts/export-support-bundle.ps1 or support.bat after reproducing the latest issue or release flow.",
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
                ArchivePath = options.LatestSupportBundleArchivePath,
            };
        }

        try
        {
            ArtifactJsonFileReader.ArtifactJsonReadResult<ReleaseSupportBundleEvidenceSnapshot> readResult =
                ArtifactJsonFileReader.TryRead(
                    artifactPath,
                    PalLlmJsonSerializerContext.Default.ReleaseSupportBundleEvidenceSnapshot,
                    maxBytes);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                return new ReleaseSupportBundleEvidenceSnapshot
                {
                    Status = "invalid",
                    Summary = ArtifactJsonFileReader.BuildFailureMessage(
                        "The latest support-bundle artifact",
                        readResult.FailureCode ?? ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                        maxBytes),
                    FreshnessStatus = "unknown",
                    FreshnessWindowHours = freshnessWindowHours,
                    ArtifactPath = artifactPath,
                    ArchivePath = options.LatestSupportBundleArchivePath,
                };
            }

            ReleaseSupportBundleEvidenceSnapshot normalized = Normalize(
                readResult.Value,
                artifactPath,
                options.LatestSupportBundleArchivePath,
                freshnessWindowHours);

            if (string.IsNullOrWhiteSpace(normalized.ArchivePath) || !File.Exists(normalized.ArchivePath))
            {
                return InvalidFrom(
                    normalized,
                    $"The latest support-bundle artifact exists at '{artifactPath}', but the matching archive '{normalized.ArchivePath}' is missing.");
            }

            ReleaseBundleArchiveInspection archiveInspection =
                ReleaseBundleArchiveInspector.InspectSupportBundle(normalized, maxBytes);
            if (!archiveInspection.Succeeded)
            {
                return InvalidFrom(normalized, archiveInspection.Summary);
            }

            return normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseSupportBundleEvidenceSnapshot
            {
                Status = "invalid",
                Summary = ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest support-bundle artifact",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                    maxBytes),
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
                ArchivePath = options.LatestSupportBundleArchivePath,
            };
        }
    }

    private static ReleaseSupportBundleEvidenceSnapshot Normalize(
        ReleaseSupportBundleEvidenceSnapshot snapshot,
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
            ? "PalLLM support-bundle evidence is available."
            : snapshot.Summary.Trim();

        if (string.Equals(freshness.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            summary = $"{summary} Refresh it before handing this bundle to support or using it for release triage.";
        }

        return new ReleaseSupportBundleEvidenceSnapshot
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
            RuntimeRoot = snapshot.RuntimeRoot?.Trim() ?? string.Empty,
            LaunchEvidenceStatus = snapshot.LaunchEvidenceStatus?.Trim() ?? string.Empty,
            SmokeEvidenceStatus = snapshot.SmokeEvidenceStatus?.Trim() ?? string.Empty,
            NativeProofEvidenceStatus = snapshot.NativeProofEvidenceStatus?.Trim() ?? string.Empty,
            ProofBundleEvidenceStatus = snapshot.ProofBundleEvidenceStatus?.Trim() ?? string.Empty,
            PackageVerificationEvidenceStatus = snapshot.PackageVerificationEvidenceStatus?.Trim() ?? string.Empty,
            FullAuditEvidenceStatus = snapshot.FullAuditEvidenceStatus?.Trim() ?? string.Empty,
            NativeHudConfigPath = snapshot.NativeHudConfigPath?.Trim() ?? string.Empty,
            PrivacyRedactionApplied = snapshot.PrivacyRedactionApplied,
            PrivacyRedactionCheckedFileCount = snapshot.PrivacyRedactionCheckedFileCount,
            PrivacyRedactionRedactedFileCount = snapshot.PrivacyRedactionRedactedFileCount,
            PrivacyRedactionRuleHits = NormalizeStrings(snapshot.PrivacyRedactionRuleHits),
            PublicationScanPassed = snapshot.PublicationScanPassed,
            PublicationScanCheckedFileCount = snapshot.PublicationScanCheckedFileCount,
            PublicationScanViolations = NormalizeStrings(snapshot.PublicationScanViolations),
            IncludedFiles = NormalizeStrings(snapshot.IncludedFiles),
            MissingOptionalFiles = NormalizeStrings(snapshot.MissingOptionalFiles),
            CurrentBlockers = NormalizeStrings(snapshot.CurrentBlockers),
            ReadyEvidence = NormalizeStrings(snapshot.ReadyEvidence),
        };
    }

    private static ReleaseSupportBundleEvidenceSnapshot InvalidFrom(
        ReleaseSupportBundleEvidenceSnapshot snapshot,
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
            RuntimeRoot = snapshot.RuntimeRoot,
            LaunchEvidenceStatus = snapshot.LaunchEvidenceStatus,
            SmokeEvidenceStatus = snapshot.SmokeEvidenceStatus,
            NativeProofEvidenceStatus = snapshot.NativeProofEvidenceStatus,
            ProofBundleEvidenceStatus = snapshot.ProofBundleEvidenceStatus,
            PackageVerificationEvidenceStatus = snapshot.PackageVerificationEvidenceStatus,
            FullAuditEvidenceStatus = snapshot.FullAuditEvidenceStatus,
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

    private static string[] NormalizeStrings(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
