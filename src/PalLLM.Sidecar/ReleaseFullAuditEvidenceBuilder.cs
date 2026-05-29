using System.Text;
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

            string? structuralError = ValidateStructure(normalized, maxBytes);
            if (!string.IsNullOrWhiteSpace(structuralError))
            {
                return InvalidFrom(normalized, structuralError);
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

    private static string? ValidateStructure(ReleaseFullAuditEvidenceSnapshot snapshot, int maxBytes)
    {
        string? pathError = ValidatePathUnderRoot(snapshot.AuditRoot, snapshot.ResultsPath, "results report");
        if (pathError is not null)
        {
            return pathError;
        }

        pathError = ValidatePathUnderRoot(snapshot.AuditRoot, snapshot.StepsDirectoryPath, "step-log directory");
        if (pathError is not null)
        {
            return pathError;
        }

        if (snapshot.PassedStepCount + snapshot.FailedStepCount != snapshot.TotalStepCount)
        {
            return "The latest full-audit artifact has contradictory pass/fail counts; rerun scripts/run_full_audit.ps1 so the durable evidence matches RESULTS.md.";
        }

        if (string.Equals(snapshot.Status, "passed", StringComparison.OrdinalIgnoreCase) &&
            (snapshot.FailedStepCount != 0 || snapshot.FailedSteps.Count > 0 || snapshot.PassedStepCount != snapshot.TotalStepCount))
        {
            return "The latest full-audit artifact claims PASS but also records failed or missing steps; rerun scripts/run_full_audit.ps1 before trusting this release candidate.";
        }

        if (string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase) &&
            snapshot.FailedStepCount == 0 &&
            snapshot.FailedSteps.Count == 0 &&
            snapshot.CurrentBlockers.Count == 0)
        {
            return "The latest full-audit artifact claims FAIL without naming a failed step or blocker; rerun scripts/run_full_audit.ps1 so the failure is actionable.";
        }

        if (snapshot.StepNames.Count > snapshot.TotalStepCount)
        {
            return "The latest full-audit artifact lists more step names than executed steps; rerun scripts/run_full_audit.ps1 so the step inventory is internally consistent.";
        }

        int logFileCount = Directory
            .EnumerateFiles(snapshot.StepsDirectoryPath, "*.log", SearchOption.TopDirectoryOnly)
            .Take(snapshot.TotalStepCount)
            .Count();
        if (logFileCount < snapshot.TotalStepCount)
        {
            return $"The latest full-audit artifact describes {snapshot.TotalStepCount} steps but only {logFileCount} step log(s) exist under '{snapshot.StepsDirectoryPath}'.";
        }

        string resultsText;
        try
        {
            FileInfo resultsInfo = new(snapshot.ResultsPath);
            int effectiveMaxBytes = Math.Max(1_024, maxBytes);
            if (resultsInfo.Length > effectiveMaxBytes)
            {
                return ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest full-audit RESULTS.md",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Oversized,
                    maxBytes);
            }

            using FileStream stream = File.OpenRead(snapshot.ResultsPath);
            byte[] buffer = new byte[checked((int)resultsInfo.Length)];
            stream.ReadExactly(buffer);
            resultsText = Encoding.UTF8.GetString(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ArtifactJsonFileReader.BuildFailureMessage(
                "The latest full-audit RESULTS.md",
                ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                maxBytes);
        }

        string expectedVerdict = string.Equals(snapshot.Status, "passed", StringComparison.OrdinalIgnoreCase)
            ? "- Overall: **PASS**"
            : string.Equals(snapshot.Status, "failed", StringComparison.OrdinalIgnoreCase)
                ? "- Overall: **FAIL**"
                : string.Empty;

        if (!string.IsNullOrEmpty(expectedVerdict) &&
            resultsText.IndexOf(expectedVerdict, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return $"The latest full-audit artifact status '{snapshot.Status}' does not match the verdict recorded in RESULTS.md.";
        }

        return null;
    }

    private static string? ValidatePathUnderRoot(string rootPath, string candidatePath, string label)
    {
        try
        {
            string fullRoot = Path.GetFullPath(rootPath);
            string fullCandidate = Path.GetFullPath(candidatePath);
            string rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
                ? fullRoot
                : fullRoot + Path.DirectorySeparatorChar;

            if (!fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(fullCandidate, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return $"The latest full-audit artifact points its {label} outside the recorded audit root.";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"The latest full-audit artifact has an invalid {label} path.";
        }

        return null;
    }

    private static string[] NormalizeStrings(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
