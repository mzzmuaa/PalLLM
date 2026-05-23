using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleaseSmokeEvidenceBuilder
{
    public static ReleaseSmokeEvidenceSnapshot ReadLatest(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int freshnessWindowHours = options.ReleaseEvidenceFreshnessHours;
        string artifactPath = options.LatestSmokeEvidencePath;
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(artifactPath))
        {
            return new ReleaseSmokeEvidenceSnapshot
            {
                Status = "missing",
                Summary = "No Palworld smoke artifact has been recorded yet. Run scripts/run-sidecar-smoke.ps1 against a live sidecar to capture one.",
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }

        try
        {
            ArtifactJsonFileReader.ArtifactJsonReadResult<ReleaseSmokeEvidenceSnapshot> readResult =
                ArtifactJsonFileReader.TryRead(
                    artifactPath,
                    PalLlmJsonSerializerContext.Default.ReleaseSmokeEvidenceSnapshot,
                    maxBytes);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                return new ReleaseSmokeEvidenceSnapshot
                {
                    Status = "invalid",
                    Summary = ArtifactJsonFileReader.BuildFailureMessage(
                        "The latest smoke artifact",
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
            return new ReleaseSmokeEvidenceSnapshot
            {
                Status = "invalid",
                Summary = ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest smoke artifact",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                    maxBytes),
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }
    }

    private static ReleaseSmokeEvidenceSnapshot Normalize(
        ReleaseSmokeEvidenceSnapshot snapshot,
        string artifactPath,
        int freshnessWindowHours)
    {
        ReleaseEvidenceFreshnessSnapshot freshness = ReleaseEvidenceFreshness.Evaluate(
            snapshot.CapturedAtUtc,
            freshnessWindowHours);
        string status = string.IsNullOrWhiteSpace(snapshot.Status)
            ? "recorded"
            : snapshot.Status.Trim();
        string summary = string.IsNullOrWhiteSpace(snapshot.Summary)
            ? "Palworld smoke evidence is available."
            : snapshot.Summary.Trim();
        if (string.Equals(freshness.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            summary = $"{summary} Refresh it before trusting this release candidate.";
        }

        string[] configuredHudTargets = (snapshot.ConfiguredHudTargets ?? Array.Empty<string>())
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ReleaseSmokeEvidenceSnapshot
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
            BaseUrl = snapshot.BaseUrl?.Trim() ?? string.Empty,
            RequestId = snapshot.RequestId?.Trim() ?? string.Empty,
            ResponsePath = snapshot.ResponsePath?.Trim() ?? string.Empty,
            BridgeProofStatus = snapshot.BridgeProofStatus?.Trim() ?? string.Empty,
            BridgeLoopStatus = snapshot.BridgeLoopStatus?.Trim() ?? string.Empty,
            LoopClosed = snapshot.LoopClosed,
            VisibleDeliveryConfirmed = snapshot.VisibleDeliveryConfirmed,
            ActionFeedbackObserved = snapshot.ActionFeedbackObserved,
            NativeHudBindReady = snapshot.NativeHudBindReady,
            RecommendedHudTarget = snapshot.RecommendedHudTarget?.Trim() ?? string.Empty,
            ConfiguredHudTargets = configuredHudTargets,
            NativeHudConfigSource = snapshot.NativeHudConfigSource?.Trim() ?? string.Empty,
            NativeHudConfigPath = snapshot.NativeHudConfigPath?.Trim() ?? string.Empty,
            DeliverySurface = snapshot.DeliverySurface?.Trim() ?? string.Empty,
            ActionType = snapshot.ActionType?.Trim() ?? string.Empty,
            UsedFallback = snapshot.UsedFallback,
        };
    }
}
