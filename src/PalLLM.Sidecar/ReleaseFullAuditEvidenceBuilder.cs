using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleaseFullAuditEvidenceBuilder
{
    public static ReleaseFullAuditEvidenceSnapshot ReadLatest(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int freshnessWindowHours = options.ReleaseEvidenceFreshnessHours;
        string artifactPath = options.LatestFullAuditEvidencePath;
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(artifactPath))
        {
            return new ReleaseFullAuditEvidenceSnapshot
            {
                Status = "missing",
                Summary = "No durable PalLLM full-audit artifact has been recorded yet. Run scripts/run_full_audit.ps1 to capture build, test, drift, and package-verification truth.",
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }

        try
        {
            ArtifactJsonFileReader.ArtifactJsonReadResult<ReleaseFullAuditEvidenceSnapshot> readResult =
                ArtifactJsonFileReader.TryRead(
                    artifactPath,
                    PalLlmJsonSerializerContext.Default.ReleaseFullAuditEvidenceSnapshot,
                    maxBytes);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                return new ReleaseFullAuditEvidenceSnapshot
                {
                    Status = "invalid",
                    Summary = ArtifactJsonFileReader.BuildFailureMessage(
                        "The latest full-audit artifact",
                        readResult.FailureCode ?? ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                        maxBytes),
                    FreshnessStatus = "unknown",
                    FreshnessWindowHours = freshnessWindowHours,
                    ArtifactPath = artifactPath,
                };
            }

            ReleaseFullAuditEvidenceSnapshot normalized = Normalize(readResult.Value, artifactPath, freshnessWindowHours);

            if (string.IsNullOrWhiteSpace(normalized.AuditRoot) || !Directory.Exists(normalized.AuditRoot))
            {
                return InvalidFrom(normalized, $"The latest full-audit artifact exists at '{artifactPath}', but the audit root '{normalized.AuditRoot}' is missing.");
            }

            if (string.IsNullOrWhiteSpace(normalized.ResultsPath) || !File.Exists(normalized.ResultsPath))
            {
                return InvalidFrom(normalized, $"The latest full-audit artifact exists at '{artifactPath}', but the results report '{normalized.ResultsPath}' is missing.");
            }

            if (string.IsNullOrWhiteSpace(normalized.StepsDirectoryPath) || !Directory.Exists(normalized.StepsDirectoryPath))
            {
                return InvalidFrom(normalized, $"The latest full-audit artifact exists at '{artifactPath}', but the step-log directory '{normalized.StepsDirectoryPath}' is missing.");
            }

            if (normalized.TotalStepCount <= 0)
            {
                return InvalidFrom(normalized, $"The latest full-audit artifact at '{artifactPath}' does not describe any executed audit steps.");
            }

            return normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new ReleaseFullAuditEvidenceSnapshot
            {
                Status = "invalid",
                Summary = ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest full-audit artifact",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                    maxBytes),
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }
    }

    private static ReleaseFullAuditEvidenceSnapshot Normalize(
        ReleaseFullAuditEvidenceSnapshot snapshot,
        string artifactPath,
        int freshnessWindowHours)
    {
        ReleaseEvidenceFreshnessSnapshot freshness = ReleaseEvidenceFreshness.Evaluate(
            snapshot.CapturedAtUtc,
            freshnessWindowHours);

        string[] stepNames = NormalizeStrings(snapshot.StepNames);
        string[] failedSteps = NormalizeStrings(snapshot.FailedSteps);
        string[] currentBlockers = NormalizeStrings(snapshot.CurrentBlockers);
        string[] readyEvidence = NormalizeStrings(snapshot.ReadyEvidence);

        string status = string.IsNullOrWhiteSpace(snapshot.Status)
            ? (failedSteps.Length > 0 || currentBlockers.Length > 0 ? "failed" : "passed")
            : snapshot.Status.Trim();

        string summary = string.IsNullOrWhiteSpace(snapshot.Summary)
            ? string.Equals(status, "passed", StringComparison.OrdinalIgnoreCase)
                ? "PalLLM full-audit evidence is available."
                : "PalLLM full-audit evidence recorded failing gates."
            : snapshot.Summary.Trim();

        if (string.Equals(freshness.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            summary = $"{summary} Refresh it before trusting this release candidate.";
        }

        return new ReleaseFullAuditEvidenceSnapshot
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
            AuditRoot = snapshot.AuditRoot?.Trim() ?? string.Empty,
            ResultsPath = snapshot.ResultsPath?.Trim() ?? string.Empty,
            StepsDirectoryPath = snapshot.StepsDirectoryPath?.Trim() ?? string.Empty,
            TestsEnabled = snapshot.TestsEnabled,
            CoverageEnabled = snapshot.CoverageEnabled,
            SbomEnabled = snapshot.SbomEnabled,
            PackagingEnabled = snapshot.PackagingEnabled,
            TotalStepCount = Math.Max(0, snapshot.TotalStepCount),
            PassedStepCount = Math.Max(0, snapshot.PassedStepCount),
            FailedStepCount = Math.Max(0, snapshot.FailedStepCount),
            StepNames = stepNames,
            FailedSteps = failedSteps,
            CurrentBlockers = currentBlockers,
            ReadyEvidence = readyEvidence,
        };
    }

    private static ReleaseFullAuditEvidenceSnapshot InvalidFrom(
        ReleaseFullAuditEvidenceSnapshot snapshot,
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
            AuditRoot = snapshot.AuditRoot,
            ResultsPath = snapshot.ResultsPath,
            StepsDirectoryPath = snapshot.StepsDirectoryPath,
            TestsEnabled = snapshot.TestsEnabled,
            CoverageEnabled = snapshot.CoverageEnabled,
            SbomEnabled = snapshot.SbomEnabled,
            PackagingEnabled = snapshot.PackagingEnabled,
            TotalStepCount = snapshot.TotalStepCount,
            PassedStepCount = snapshot.PassedStepCount,
            FailedStepCount = snapshot.FailedStepCount,
            StepNames = snapshot.StepNames,
            FailedSteps = snapshot.FailedSteps,
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
