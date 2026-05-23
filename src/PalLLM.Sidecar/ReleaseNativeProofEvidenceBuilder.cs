using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class ReleaseNativeProofEvidenceBuilder
{
    public static ReleaseNativeProofEvidenceSnapshot ReadLatest(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int freshnessWindowHours = options.ReleaseEvidenceFreshnessHours;
        string artifactPath = options.LatestNativeProofEvidencePath;
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(artifactPath))
        {
            NativeProofDiagnosis diagnosis = BuildDiagnosis(
                "native_proof_missing",
                "No durable live Palworld native-proof artifact has been recorded.");

            return new ReleaseNativeProofEvidenceSnapshot
            {
                Status = "missing",
                Summary = "No live Palworld native-proof artifact has been recorded yet. Run scripts/run-native-proof.ps1 while Palworld is live to capture one.",
                DiagnosisCode = diagnosis.Code,
                DiagnosisSummary = diagnosis.Summary,
                DiagnosisAction = diagnosis.Action,
                DiagnosisCommand = diagnosis.Command,
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }

        try
        {
            ArtifactJsonFileReader.ArtifactJsonReadResult<ReleaseNativeProofEvidenceSnapshot> readResult =
                ArtifactJsonFileReader.TryRead(
                    artifactPath,
                    PalLlmJsonSerializerContext.Default.ReleaseNativeProofEvidenceSnapshot,
                    maxBytes);

            if (!readResult.Succeeded || readResult.Value is null)
            {
                NativeProofDiagnosis diagnosis = BuildDiagnosis(
                    "native_proof_artifact_invalid",
                    "The durable native-proof artifact could not be read safely.");

                return new ReleaseNativeProofEvidenceSnapshot
                {
                    Status = "invalid",
                    Summary = ArtifactJsonFileReader.BuildFailureMessage(
                        "The latest native proof artifact",
                        readResult.FailureCode ?? ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                        maxBytes),
                    DiagnosisCode = diagnosis.Code,
                    DiagnosisSummary = diagnosis.Summary,
                    DiagnosisAction = diagnosis.Action,
                    DiagnosisCommand = diagnosis.Command,
                    FreshnessStatus = "unknown",
                    FreshnessWindowHours = freshnessWindowHours,
                    ArtifactPath = artifactPath,
                };
            }

            return Normalize(readResult.Value, artifactPath, freshnessWindowHours);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            NativeProofDiagnosis diagnosis = BuildDiagnosis(
                "native_proof_artifact_invalid",
                "The durable native-proof artifact could not be read safely.");

            return new ReleaseNativeProofEvidenceSnapshot
            {
                Status = "invalid",
                Summary = ArtifactJsonFileReader.BuildFailureMessage(
                    "The latest native proof artifact",
                    ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                    maxBytes),
                DiagnosisCode = diagnosis.Code,
                DiagnosisSummary = diagnosis.Summary,
                DiagnosisAction = diagnosis.Action,
                DiagnosisCommand = diagnosis.Command,
                FreshnessStatus = "unknown",
                FreshnessWindowHours = freshnessWindowHours,
                ArtifactPath = artifactPath,
            };
        }
    }

    private static ReleaseNativeProofEvidenceSnapshot Normalize(
        ReleaseNativeProofEvidenceSnapshot snapshot,
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
            ? "Live native proof evidence is available."
            : snapshot.Summary.Trim();
        string freshnessStatus = freshness.Status;
        DateTimeOffset? freshUntilUtc = freshness.FreshUntilUtc;
        if (string.Equals(freshness.Status, "stale", StringComparison.OrdinalIgnoreCase))
        {
            summary = $"{summary} Refresh it before trusting this release candidate.";
        }

        string[] configuredHudTargets = (snapshot.ConfiguredHudTargets ?? Array.Empty<string>())
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] currentBlockers = (snapshot.CurrentBlockers ?? Array.Empty<string>())
            .Where(blocker => !string.IsNullOrWhiteSpace(blocker))
            .Select(blocker => blocker.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        string[] readyEvidence = (snapshot.ReadyEvidence ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        ReleaseNativeProofStatusTransition[] statusTransitions =
            NormalizeStatusTransitions(snapshot.StatusTransitions);
        NativeProofDiagnosis diagnosis = Diagnose(status, snapshot, currentBlockers);

        if (ClaimsProvenWithoutNativeHudDeliveryEvidence(status, snapshot))
        {
            status = "invalid";
            summary = "The latest native proof artifact claims proven status without matching native HUD delivery evidence; recapture it with scripts/run-native-proof.ps1.";
            freshnessStatus = "unknown";
            freshUntilUtc = null;
            currentBlockers = currentBlockers
                .Append("Native proof artifact claims proven status without matching native HUD delivery evidence.")
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            diagnosis = BuildDiagnosis(
                "native_proof_artifact_contradiction",
                "The artifact claims proven native delivery without matching native HUD evidence.");
        }

        return new ReleaseNativeProofEvidenceSnapshot
        {
            Status = status,
            Summary = summary,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            WatcherStartedAtUtc = snapshot.WatcherStartedAtUtc,
            WatcherFinishedAtUtc = snapshot.WatcherFinishedAtUtc,
            WatcherCompletionReason = snapshot.WatcherCompletionReason?.Trim() ?? string.Empty,
            TimeoutSeconds = Math.Max(0, snapshot.TimeoutSeconds),
            PollIntervalSeconds = Math.Max(0, snapshot.PollIntervalSeconds),
            PollCount = Math.Max(0, snapshot.PollCount),
            TimedOut = snapshot.TimedOut,
            DiagnosisCode = diagnosis.Code,
            DiagnosisSummary = diagnosis.Summary,
            DiagnosisAction = diagnosis.Action,
            DiagnosisCommand = diagnosis.Command,
            FreshnessStatus = freshnessStatus,
            FreshUntilUtc = freshUntilUtc,
            FreshnessWindowHours = freshness.FreshnessWindowHours,
            ArtifactPath = string.IsNullOrWhiteSpace(snapshot.ArtifactPath)
                ? artifactPath
                : snapshot.ArtifactPath.Trim(),
            HistoryArtifactPath = snapshot.HistoryArtifactPath?.Trim() ?? string.Empty,
            BaseUrl = snapshot.BaseUrl?.Trim() ?? string.Empty,
            BridgeProofStatus = snapshot.BridgeProofStatus?.Trim() ?? string.Empty,
            ActiveRequestId = snapshot.ActiveRequestId?.Trim() ?? string.Empty,
            LiveDeliveryProven = snapshot.LiveDeliveryProven,
            NativeHudBindReady = snapshot.NativeHudBindReady,
            RecommendedHudTarget = snapshot.RecommendedHudTarget?.Trim() ?? string.Empty,
            ConfiguredHudTargets = configuredHudTargets,
            NativeHudConfigSource = snapshot.NativeHudConfigSource?.Trim() ?? string.Empty,
            NativeHudConfigPath = snapshot.NativeHudConfigPath?.Trim() ?? string.Empty,
            DeliverySurface = snapshot.DeliverySurface?.Trim() ?? string.Empty,
            LoopStatus = snapshot.LoopStatus?.Trim() ?? string.Empty,
            VisibleDeliveryConfirmed = snapshot.VisibleDeliveryConfirmed,
            ActionFeedbackObserved = snapshot.ActionFeedbackObserved,
            AppliedHudRecommendation = snapshot.AppliedHudRecommendation,
            AppliedHudRecommendationPath = snapshot.AppliedHudRecommendationPath?.Trim() ?? string.Empty,
            RecommendedNextStep = snapshot.RecommendedNextStep?.Trim() ?? string.Empty,
            CurrentBlockers = currentBlockers,
            ReadyEvidence = readyEvidence,
            StatusTransitions = statusTransitions,
        };
    }

    private static NativeProofDiagnosis Diagnose(
        string status,
        ReleaseNativeProofEvidenceSnapshot snapshot,
        IReadOnlyList<string> currentBlockers)
    {
        string existingCode = NormalizeDiagnosisCode(snapshot.DiagnosisCode);
        string existingSummary = snapshot.DiagnosisSummary?.Trim() ?? string.Empty;

        if (string.Equals(status, "proven", StringComparison.OrdinalIgnoreCase))
        {
            return BuildDiagnosis(
                "native_hud_delivery_proven",
                "Live Palworld native HUD delivery has matching bridge proof.");
        }

        if (string.Equals(status, "invalid", StringComparison.OrdinalIgnoreCase))
        {
            return BuildDiagnosis(
                "native_proof_artifact_invalid",
                "The durable native-proof artifact is invalid or unsafe to trust.");
        }

        if (string.Equals(snapshot.WatcherCompletionReason, "palworld_process_missing", StringComparison.OrdinalIgnoreCase)
            || ContainsBlocker(currentBlockers, "Palworld process is not running"))
        {
            return BuildDiagnosis(
                "palworld_process_missing",
                "The local proof watcher started before a Palworld process was visible.");
        }

        if (IsAwaitingBridgeBoot(snapshot.BridgeProofStatus))
        {
            return BuildDiagnosis(
                "bridge_boot_missing",
                "The sidecar has not received a live bridge_boot event from the UE4SS bridge.");
        }

        if (IsUiProbeMissing(snapshot.BridgeProofStatus, currentBlockers))
        {
            return BuildDiagnosis(
                "ui_probe_missing",
                "The live bridge has not captured enough ui_probe evidence to recommend a HUD target.");
        }

        if (!snapshot.NativeHudBindReady || IsHudBindBlocker(currentBlockers))
        {
            return BuildDiagnosis(
                "native_hud_bind_not_ready",
                "Native HUD rendering is not ready; apply or review the recommended widget target before rerunning proof.");
        }

        if (snapshot.VisibleDeliveryConfirmed
            && !IsNativeHudDeliverySurface(snapshot.DeliverySurface))
        {
            return BuildDiagnosis(
                "native_hud_surface_mismatch",
                "The reply became visible through a fallback surface instead of native_hud.");
        }

        if (snapshot.TimedOut
            || string.Equals(status, "timed_out", StringComparison.OrdinalIgnoreCase)
            || string.Equals(snapshot.WatcherCompletionReason, "delivery_proven_timeout", StringComparison.OrdinalIgnoreCase))
        {
            return BuildDiagnosis(
                "delivery_proven_timeout",
                "The proof watcher timed out before bridge proof reached delivery_proven.");
        }

        if (IsAwaitingDelivery(snapshot.BridgeProofStatus, snapshot.LoopStatus))
        {
            return BuildDiagnosis(
                "awaiting_visible_delivery",
                "The sidecar has a tracked reply but has not received matching visible-delivery proof yet.");
        }

        if (currentBlockers.Count > 0)
        {
            return BuildDiagnosis(
                "bridge_proof_blocked",
                currentBlockers[0]);
        }

        if (!string.IsNullOrWhiteSpace(existingCode))
        {
            return BuildDiagnosis(
                existingCode,
                string.IsNullOrWhiteSpace(existingSummary)
                    ? "The native-proof artifact supplied a diagnosis code without a summary."
                    : existingSummary);
        }

        return BuildDiagnosis(
            "native_proof_incomplete",
            "Native proof has not reached a release-blocking or proven terminal state yet.");
    }

    private static NativeProofDiagnosis BuildDiagnosis(
        string code,
        string summary)
    {
        string normalizedCode = NormalizeDiagnosisCode(code);
        return new(
            normalizedCode,
            summary.Trim(),
            BuildDiagnosisAction(normalizedCode),
            BuildDiagnosisCommand(normalizedCode));
    }

    private static string BuildDiagnosisAction(string code) =>
        code switch
        {
            "native_hud_delivery_proven" =>
                "Archive the current live proof and continue with the release smoke and proof-bundle lanes.",
            "palworld_process_missing" =>
                "Start Palworld with the UE4SS bridge loaded, then rerun native proof.",
            "bridge_boot_missing" =>
                "Wait for a live bridge_boot heartbeat from the UE4SS bridge before validating native delivery.",
            "ui_probe_missing" =>
                "Capture ui_probe widget evidence during representative gameplay before binding the HUD target.",
            "native_hud_bind_not_ready" =>
                "Apply or review the recommended native HUD target, then rerun native proof.",
            "native_hud_surface_mismatch" =>
                "Fix the HUD bind until reply_delivery reports surface=native_hud.",
            "delivery_proven_timeout" =>
                "Rerun native proof with more time after confirming the sidecar, bridge, and HUD bind are active.",
            "awaiting_visible_delivery" =>
                "Inspect the UE4SS outbox consumer and renderer for the tracked request id.",
            "native_proof_artifact_invalid" or "native_proof_artifact_contradiction" =>
                "Recapture native proof from a live Palworld session instead of trusting this artifact.",
            "native_proof_missing" =>
                "Capture the first live Palworld native-proof artifact.",
            _ =>
                "Inspect the bridge proof lanes, fix the listed blocker, and rerun native proof.",
        };

    private static string BuildDiagnosisCommand(string code) =>
        code switch
        {
            "native_hud_delivery_proven" =>
                "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-sidecar-smoke.ps1",
            "native_hud_bind_not_ready" or "native_hud_surface_mismatch" =>
                "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -ApplyHudRecommendation",
            "delivery_proven_timeout" or "awaiting_visible_delivery" =>
                "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -TimeoutSeconds 300",
            _ =>
                "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1",
        };

    private static bool ContainsBlocker(
        IReadOnlyList<string> blockers,
        string value) =>
        blockers.Any(blocker => blocker.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsAwaitingBridgeBoot(string? status) =>
        string.IsNullOrWhiteSpace(status)
        || string.Equals(status.Trim(), "awaiting_bridge_boot", StringComparison.OrdinalIgnoreCase);

    private static bool IsUiProbeMissing(
        string? status,
        IReadOnlyList<string> blockers) =>
        string.Equals(status?.Trim(), "awaiting_ui_probe_capture", StringComparison.OrdinalIgnoreCase)
        || ContainsBlocker(blockers, "ui_probe");

    private static bool IsHudBindBlocker(IReadOnlyList<string> blockers) =>
        ContainsBlocker(blockers, "native_hud_render_enabled")
        || ContainsBlocker(blockers, "native_hud_widget_targets")
        || ContainsBlocker(blockers, "native HUD bind");

    private static bool IsAwaitingDelivery(
        string? bridgeProofStatus,
        string? loopStatus) =>
        ContainsToken(bridgeProofStatus, "awaiting_delivery")
        || ContainsToken(loopStatus, "awaiting_delivery");

    private static bool ContainsToken(string? value, string token) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDiagnosisCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Trim().ToLowerInvariant();
        char[] buffer = new char[normalized.Length];
        int index = 0;
        foreach (char ch in normalized)
        {
            buffer[index++] = char.IsLetterOrDigit(ch) ? ch : '_';
        }

        return new string(buffer, 0, index).Trim('_');
    }

    private static ReleaseNativeProofStatusTransition[] NormalizeStatusTransitions(
        IReadOnlyList<ReleaseNativeProofStatusTransition>? transitions) =>
        (transitions ?? Array.Empty<ReleaseNativeProofStatusTransition>())
            .Where(transition => transition is not null)
            .Where(transition => transition.ObservedAtUtc != default
                || !string.IsNullOrWhiteSpace(transition.BridgeProofStatus)
                || !string.IsNullOrWhiteSpace(transition.Summary))
            .Take(32)
            .Select(transition => new ReleaseNativeProofStatusTransition
            {
                ObservedAtUtc = transition.ObservedAtUtc,
                PollIndex = Math.Max(0, transition.PollIndex),
                BridgeProofStatus = transition.BridgeProofStatus?.Trim() ?? string.Empty,
                Summary = transition.Summary?.Trim() ?? string.Empty,
                ActiveRequestId = transition.ActiveRequestId?.Trim() ?? string.Empty,
                LoopStatus = transition.LoopStatus?.Trim() ?? string.Empty,
                LiveDeliveryProven = transition.LiveDeliveryProven,
                NativeHudBindReady = transition.NativeHudBindReady,
                VisibleDeliveryConfirmed = transition.VisibleDeliveryConfirmed,
                DeliverySurface = transition.DeliverySurface?.Trim() ?? string.Empty,
            })
            .ToArray();

    private static bool ClaimsProvenWithoutNativeHudDeliveryEvidence(
        string status,
        ReleaseNativeProofEvidenceSnapshot snapshot) =>
        string.Equals(status, "proven", StringComparison.OrdinalIgnoreCase)
        && (!snapshot.LiveDeliveryProven
            || !snapshot.NativeHudBindReady
            || !snapshot.VisibleDeliveryConfirmed
            || !string.Equals(snapshot.LoopStatus?.Trim(), "closed", StringComparison.OrdinalIgnoreCase)
            || !IsNativeHudDeliverySurface(snapshot.DeliverySurface)
            || !string.Equals(
                snapshot.BridgeProofStatus?.Trim(),
                "delivery_proven",
                StringComparison.OrdinalIgnoreCase));

    private static bool IsNativeHudDeliverySurface(string? surface)
    {
        if (string.IsNullOrWhiteSpace(surface))
        {
            return false;
        }

        string normalized = surface.Trim();
        return string.Equals(normalized, "native_hud", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("native_hud:", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record NativeProofDiagnosis(string Code, string Summary, string Action, string Command);
}
