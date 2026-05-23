using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleaseArtifactIntegrityEvidenceBuilder
{
    public static ReleaseArtifactIntegrityEvidenceSnapshot ReadLatest(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int freshnessWindowHours = options.ReleaseEvidenceFreshnessHours;
        string artifactPath = options.LatestArtifactIntegrityEvidencePath;
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(artifactPath))
        {
            return new ReleaseArtifactIntegrityEvidenceSnapshot
            {
                Status = "missing",
                Summary = "No PalLLM release artifact integrity evidence has been recorded yet. Run scripts/compute-release-checksums.ps1 after building or verifying a candidate package.",
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }

        try
        {
            ArtifactJsonFileReader.ArtifactJsonReadResult<ReleaseArtifactIntegrityEvidenceSnapshot> readResult =
                ArtifactJsonFileReader.TryRead(
                    artifactPath,
                    PalLlmJsonSerializerContext.Default.ReleaseArtifactIntegrityEvidenceSnapshot,
                    maxBytes);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                return new ReleaseArtifactIntegrityEvidenceSnapshot
                {
                    Status = "invalid",
                    Summary = ArtifactJsonFileReader.BuildFailureMessage(
                        "The latest artifact integrity evidence",
                        readResult.FailureCode ?? ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                        maxBytes),
                    FreshnessStatus = "unknown",
                    FreshnessWindowHours = freshnessWindowHours,
                    ArtifactPath = artifactPath,
                };
            }

            ReleaseArtifactIntegrityEvidenceSnapshot normalized = Normalize(
                readResult.Value,
                artifactPath,
                freshnessWindowHours);

            if (string.Equals(normalized.Status, "recorded", StringComparison.OrdinalIgnoreCase))
            {
                ReleaseArtifactIntegrityEvidenceSnapshot? invalid = ValidateReferencedFiles(normalized);
                if (invalid is not null)
                {
                    return invalid;
                }
            }

            return normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseArtifactIntegrityEvidenceSnapshot
            {
                Status = "invalid",
                Summary = ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest artifact integrity evidence",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                    maxBytes),
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }
    }

    private static ReleaseArtifactIntegrityEvidenceSnapshot Normalize(
        ReleaseArtifactIntegrityEvidenceSnapshot snapshot,
        string artifactPath,
        int freshnessWindowHours)
    {
        ReleaseEvidenceFreshnessSnapshot freshness = ReleaseEvidenceFreshness.Evaluate(
            snapshot.CapturedAtUtc,
            freshnessWindowHours);
        string[] detachedSignaturePaths = NormalizeStrings(snapshot.DetachedSignaturePaths);
        string[] currentBlockers = NormalizeStrings(snapshot.CurrentBlockers);
        string[] readyEvidence = NormalizeStrings(snapshot.ReadyEvidence);
        string status = string.IsNullOrWhiteSpace(snapshot.Status)
            ? currentBlockers.Length > 0 ? "invalid" : "recorded"
            : snapshot.Status.Trim();
        string summary = string.IsNullOrWhiteSpace(snapshot.Summary)
            ? string.Equals(status, "recorded", StringComparison.OrdinalIgnoreCase)
                ? "PalLLM release artifact checksum evidence is available."
                : "PalLLM release artifact checksum evidence is not valid."
            : snapshot.Summary.Trim();
        if (string.Equals(freshness.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            summary = $"{summary} Refresh it before trusting this release candidate.";
        }

        return new ReleaseArtifactIntegrityEvidenceSnapshot
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
            PackagingRoot = snapshot.PackagingRoot?.Trim() ?? string.Empty,
            ChecksumsJsonPath = snapshot.ChecksumsJsonPath?.Trim() ?? string.Empty,
            Sha256SumsPath = snapshot.Sha256SumsPath?.Trim() ?? string.Empty,
            Sha512SumsPath = snapshot.Sha512SumsPath?.Trim() ?? string.Empty,
            ArtifactCount = Math.Max(0, snapshot.ArtifactCount),
            ChecksumsJsonPresent = snapshot.ChecksumsJsonPresent,
            Sha256SumsPresent = snapshot.Sha256SumsPresent,
            Sha512SumsPresent = snapshot.Sha512SumsPresent,
            Sha512Skipped = snapshot.Sha512Skipped,
            DetachedSignaturePresent = snapshot.DetachedSignaturePresent || detachedSignaturePaths.Length > 0,
            DetachedSignaturePaths = detachedSignaturePaths,
            CurrentBlockers = currentBlockers,
            ReadyEvidence = readyEvidence,
        };
    }

    private static ReleaseArtifactIntegrityEvidenceSnapshot? ValidateReferencedFiles(
        ReleaseArtifactIntegrityEvidenceSnapshot snapshot)
    {
        if (snapshot.ArtifactCount <= 0)
        {
            return InvalidFrom(snapshot, "The latest artifact integrity evidence did not record any release artifacts.");
        }

        if (string.IsNullOrWhiteSpace(snapshot.PackagingRoot) || !Directory.Exists(snapshot.PackagingRoot))
        {
            return InvalidFrom(snapshot, "The latest artifact integrity evidence points at a missing packaging root.");
        }

        if (!snapshot.ChecksumsJsonPresent ||
            string.IsNullOrWhiteSpace(snapshot.ChecksumsJsonPath) ||
            !File.Exists(snapshot.ChecksumsJsonPath))
        {
            return InvalidFrom(snapshot, "The latest artifact integrity evidence is missing checksums.json.");
        }

        if (!snapshot.Sha256SumsPresent ||
            string.IsNullOrWhiteSpace(snapshot.Sha256SumsPath) ||
            !File.Exists(snapshot.Sha256SumsPath))
        {
            return InvalidFrom(snapshot, "The latest artifact integrity evidence is missing SHA256SUMS.");
        }

        if (!snapshot.Sha512Skipped &&
            (!snapshot.Sha512SumsPresent ||
             string.IsNullOrWhiteSpace(snapshot.Sha512SumsPath) ||
             !File.Exists(snapshot.Sha512SumsPath)))
        {
            return InvalidFrom(snapshot, "The latest artifact integrity evidence is missing SHA512SUMS.");
        }

        return null;
    }

    private static ReleaseArtifactIntegrityEvidenceSnapshot InvalidFrom(
        ReleaseArtifactIntegrityEvidenceSnapshot snapshot,
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
            PackagingRoot = snapshot.PackagingRoot,
            ChecksumsJsonPath = snapshot.ChecksumsJsonPath,
            Sha256SumsPath = snapshot.Sha256SumsPath,
            Sha512SumsPath = snapshot.Sha512SumsPath,
            ArtifactCount = snapshot.ArtifactCount,
            ChecksumsJsonPresent = snapshot.ChecksumsJsonPresent,
            Sha256SumsPresent = snapshot.Sha256SumsPresent,
            Sha512SumsPresent = snapshot.Sha512SumsPresent,
            Sha512Skipped = snapshot.Sha512Skipped,
            DetachedSignaturePresent = snapshot.DetachedSignaturePresent,
            DetachedSignaturePaths = snapshot.DetachedSignaturePaths,
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
