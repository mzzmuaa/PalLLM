using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleasePackageVerificationEvidenceBuilder
{
    public static ReleasePackageVerificationEvidenceSnapshot ReadLatest(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int freshnessWindowHours = options.ReleaseEvidenceFreshnessHours;
        string artifactPath = options.LatestPackageVerificationEvidencePath;
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(artifactPath))
        {
            return new ReleasePackageVerificationEvidenceSnapshot
            {
                Status = "missing",
                Summary = "No PalLLM release package verification artifact has been recorded yet. Run scripts/package-release.ps1 or scripts/verify-release-package.ps1 against a candidate package.",
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }

        try
        {
            ArtifactJsonFileReader.ArtifactJsonReadResult<ReleasePackageVerificationEvidenceSnapshot> readResult =
                ArtifactJsonFileReader.TryRead(
                    artifactPath,
                    PalLlmJsonSerializerContext.Default.ReleasePackageVerificationEvidenceSnapshot,
                    maxBytes);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                return new ReleasePackageVerificationEvidenceSnapshot
                {
                    Status = "invalid",
                    Summary = ArtifactJsonFileReader.BuildFailureMessage(
                        "The latest package verification artifact",
                        readResult.FailureCode ?? ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                        maxBytes),
                    FreshnessStatus = "unknown",
                    FreshnessWindowHours = freshnessWindowHours,
                    ArtifactPath = artifactPath,
                };
            }

            return Normalize(readResult.Value, artifactPath, freshnessWindowHours);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleasePackageVerificationEvidenceSnapshot
            {
                Status = "invalid",
                Summary = ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest package verification artifact",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                    maxBytes),
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }
    }

    private static ReleasePackageVerificationEvidenceSnapshot Normalize(
        ReleasePackageVerificationEvidenceSnapshot snapshot,
        string artifactPath,
        int freshnessWindowHours)
    {
        ReleaseEvidenceFreshnessSnapshot freshness = ReleaseEvidenceFreshness.Evaluate(
            snapshot.CapturedAtUtc,
            freshnessWindowHours);
        string status = string.IsNullOrWhiteSpace(snapshot.Status)
            ? "verified"
            : snapshot.Status.Trim();
        string summary = string.IsNullOrWhiteSpace(snapshot.Summary)
            ? "PalLLM release package verification evidence is available."
            : snapshot.Summary.Trim();
        if (string.Equals(freshness.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            summary = $"{summary} Refresh it before trusting this release candidate.";
        }

        string[] missingRequiredFiles = NormalizeStrings(snapshot.MissingRequiredFiles);
        string[] unexpectedFiles = NormalizeStrings(snapshot.UnexpectedFiles);
        string[] mismatchedFiles = NormalizeStrings(snapshot.MismatchedFiles);
        string[] publicationScanViolations = NormalizeStrings(snapshot.PublicationScanViolations);
        string[] currentBlockers = NormalizeStrings(snapshot.CurrentBlockers);
        string[] readyEvidence = NormalizeStrings(snapshot.ReadyEvidence);

        return new ReleasePackageVerificationEvidenceSnapshot
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
            PackagePath = snapshot.PackagePath?.Trim() ?? string.Empty,
            PackageKind = snapshot.PackageKind?.Trim() ?? string.Empty,
            ReleaseName = snapshot.ReleaseName?.Trim() ?? string.Empty,
            ManifestRelativePath = snapshot.ManifestRelativePath?.Trim() ?? string.Empty,
            ManifestSchemaVersion = Math.Max(0, snapshot.ManifestSchemaVersion),
            PackageSha256 = snapshot.PackageSha256?.Trim() ?? string.Empty,
            VerifiedFromArchive = snapshot.VerifiedFromArchive,
            IncludesSidecarPublish = snapshot.IncludesSidecarPublish,
            SelfContainedSidecar = snapshot.SelfContainedSidecar,
            RequiredFilesPresent = snapshot.RequiredFilesPresent,
            CheckedFileCount = Math.Max(0, snapshot.CheckedFileCount),
            PublicationScanPassed = snapshot.PublicationScanPassed,
            PublicationScanCheckedFileCount = Math.Max(0, snapshot.PublicationScanCheckedFileCount),
            MissingRequiredFiles = missingRequiredFiles,
            UnexpectedFiles = unexpectedFiles,
            MismatchedFiles = mismatchedFiles,
            PublicationScanViolations = publicationScanViolations,
            CurrentBlockers = currentBlockers,
            ReadyEvidence = readyEvidence,
        };
    }

    private static string[] NormalizeStrings(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
