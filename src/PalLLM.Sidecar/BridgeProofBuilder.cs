using System.Globalization;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Builds the machine-readable native-readiness + live-loop proof
//            snapshot served at GET /api/bridge/proof. The single payload
//            smoke tooling, dashboards, and release-readiness checks all
//            consume to answer "did the loop actually execute end-to-end?"
//   surface: BridgeProofBuilder.Create(runtime) -> BridgeProofSnapshot.
//   gate:    Drift_Api_route_count + Drift_OpenApi_snapshot (the route
//            registration is in Program.cs); no count specifically pins
//            this builder, but its output shape is part of the OpenAPI
//            contract.
//   adr:     None directly; the one-way bridge invariant lives in
//            docs/adr/0003-one-way-bridge.md.
//   docs:    docs/API.md (GET /api/bridge/proof), docs/OPERATIONS.md,
//            docs/RUNBOOK.md ("how do I know the loop is alive?").
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class BridgeProofBuilder
{
    public static BridgeProofSnapshot Create(PalLlmRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        RuntimeHealth health = runtime.GetHealth();
        RuntimeWorldState world = runtime.GetWorldState();
        BridgeActivitySnapshot bridge = world.Bridge;
        NativeReadinessSnapshot nativeReadiness = health.NativeReadiness;
        BridgeLoopProofSnapshot loopProof = health.BridgeLoop;

        List<string> readyEvidence = BuildReadyEvidence(nativeReadiness, loopProof);
        List<string> currentBlockers = BuildCurrentBlockers(nativeReadiness, loopProof);
        string status = BuildStatus(nativeReadiness, loopProof);
        IReadOnlyList<BridgeProofLaneSnapshot> proofLanes =
            BuildProofLanes(nativeReadiness, loopProof);

        return new BridgeProofSnapshot
        {
            GeneratedAtUtc = ResolveGeneratedAtUtc(health, bridge, loopProof),
            Status = status,
            Summary = BuildSummary(status, nativeReadiness, loopProof),
            RecommendedNextStep = BuildRecommendedNextStep(status, nativeReadiness),
            ActiveRequestId = loopProof.ActiveRequestId,
            LastBridgeEventType = health.LastBridgeEventType,
            LastBridgeEventAtUtc = health.LastBridgeEventAtUtc,
            LiveDeliveryProven = loopProof.LoopClosed,
            NativeHudBindReady = nativeReadiness.HudBindReady,
            NativeReadiness = nativeReadiness,
            LoopProof = loopProof,
            LastBridgeBoot = bridge.LastBridgeBoot,
            LastUiProbe = bridge.LastUiProbe,
            UiProbeDiagnostics = bridge.UiProbeDiagnostics,
            ProofLanes = proofLanes,
            ReadyEvidence = readyEvidence,
            CurrentBlockers = currentBlockers,
        };
    }

    private static DateTimeOffset ResolveGeneratedAtUtc(
        RuntimeHealth health,
        BridgeActivitySnapshot bridge,
        BridgeLoopProofSnapshot loopProof) =>
        health.LastBridgeEventAtUtc
        ?? bridge.UiProbeDiagnostics?.LastDumpAtUtc
        ?? bridge.LastUiProbe?.CapturedAtUtc
        ?? loopProof.LastReplyDelivery?.CapturedAtUtc
        ?? loopProof.LastActionFeedback?.CapturedAtUtc
        ?? loopProof.LastSpeechPlayback?.CapturedAtUtc
        ?? loopProof.LastOutboxReply?.WrittenAtUtc
        ?? loopProof.LastIngress?.CapturedAtUtc
        ?? DateTimeOffset.UtcNow;

    private static List<string> BuildReadyEvidence(
        NativeReadinessSnapshot nativeReadiness,
        BridgeLoopProofSnapshot loopProof)
    {
        List<string> readyEvidence = [];

        if (nativeReadiness.BridgeBootSeen)
        {
            readyEvidence.Add($"bridge_boot heartbeat seen from version '{nativeReadiness.BridgeVersion}' with status '{nativeReadiness.BridgeStatus}'.");
        }

        if (nativeReadiness.HasPalGameStateCompat)
        {
            readyEvidence.Add("PalGameStateInGame compatibility is present.");
        }

        if (nativeReadiness.HasUserWidgetCompat)
        {
            readyEvidence.Add("UserWidget compatibility is present for native HUD discovery.");
        }

        if (nativeReadiness.UiProbeEnabled)
        {
            readyEvidence.Add("ui_probe is enabled in the UE4SS bridge.");
        }

        if (nativeReadiness.HasUiProbeCandidates)
        {
            string candidate = string.IsNullOrWhiteSpace(nativeReadiness.TopUiProbeCandidate)
                ? "captured"
                : $"captured; top candidate '{nativeReadiness.TopUiProbeCandidate}'";
            readyEvidence.Add($"Ranked widget candidates have been {candidate}.");
        }

        if (!string.IsNullOrWhiteSpace(nativeReadiness.HudBindRecommendation.RecommendedTarget))
        {
            readyEvidence.Add($"HUD bind shortlist recommends '{nativeReadiness.HudBindRecommendation.RecommendedTarget}' next.");
        }

        if (!string.IsNullOrWhiteSpace(nativeReadiness.NativeHudConfigPath))
        {
            if (string.Equals(nativeReadiness.NativeHudConfigSource, "inline_defaults", StringComparison.OrdinalIgnoreCase))
            {
                readyEvidence.Add($"Native HUD is currently using inline defaults; preferred override path is '{nativeReadiness.NativeHudConfigPath}'.");
            }
            else if (!string.Equals(nativeReadiness.NativeHudConfigSource, "override_error", StringComparison.OrdinalIgnoreCase))
            {
                readyEvidence.Add($"Native HUD override source '{nativeReadiness.NativeHudConfigSource}' loaded from '{nativeReadiness.NativeHudConfigPath}'.");
            }
        }

        if (nativeReadiness.ActionExecutorEnabled)
        {
            readyEvidence.Add("Guarded action executor is enabled for bridge feedback.");
        }

        if (nativeReadiness.NativeHudEnabled)
        {
            readyEvidence.Add("native_hud_render_enabled is true.");
        }

        if (nativeReadiness.NativeHudTargetsConfigured)
        {
            readyEvidence.Add("native_hud_widget_targets contains at least one configured target.");
        }

        if (nativeReadiness.ProductionSamplerReady)
        {
            readyEvidence.Add("Production sampler is enabled and PalBaseCampManager compatibility is present.");
        }

        if (nativeReadiness.WaypointMarkerReady)
        {
            readyEvidence.Add("Native waypoint marker hint is enabled and PalMapManager compatibility is present.");
        }

        if (loopProof.RequestSeen)
        {
            readyEvidence.Add($"Tracked chat ingress exists for request '{ResolveTrackedRequestId(loopProof)}'.");
        }

        if (loopProof.OutboxReplyWritten)
        {
            readyEvidence.Add("The sidecar wrote a reply envelope into Bridge/Outbox.");
        }

        if (loopProof.VisibleDeliveryConfirmed)
        {
            string surface = string.IsNullOrWhiteSpace(loopProof.LastReplyDelivery?.Surface)
                ? "unknown-surface"
                : loopProof.LastReplyDelivery.Surface;
            readyEvidence.Add($"UE4SS reported a rendered reply via '{surface}'.");
        }

        if (loopProof.ActionPlanned)
        {
            readyEvidence.Add("The tracked reply included a guarded action plan.");
        }

        if (loopProof.ActionFeedbackObserved)
        {
            string eventType = string.IsNullOrWhiteSpace(loopProof.LastActionFeedback?.EventType)
                ? "bridge action"
                : loopProof.LastActionFeedback.EventType;
            readyEvidence.Add($"Matching '{eventType}' feedback returned from the bridge.");
        }

        if (loopProof.SpeechPlaybackStarted)
        {
            string mode = string.IsNullOrWhiteSpace(loopProof.LastSpeechPlayback?.PlaybackMode)
                ? "unknown-mode"
                : loopProof.LastSpeechPlayback.PlaybackMode;
            string artifact = FormatSpeechArtifactSize(loopProof.LastSpeechPlayback);
            string launch = FormatSpeechPlaybackLaunchReceipt(loopProof.LastSpeechPlayback);
            string timing = FormatSpeechPlaybackTimingReceipt(loopProof);
            readyEvidence.Add($"Matching speech playback started via '{mode}'{artifact}{launch}{timing}.");
        }

        if (loopProof.LoopClosed)
        {
            readyEvidence.Add("The tracked request, delivery, and feedback loop is closed.");
        }

        return readyEvidence;
    }

    private static List<string> BuildCurrentBlockers(
        NativeReadinessSnapshot nativeReadiness,
        BridgeLoopProofSnapshot loopProof)
    {
        List<string> blockers = [];

        if (!nativeReadiness.BridgeBootSeen)
        {
            blockers.Add("No bridge_boot heartbeat has been observed yet from a live Palworld session.");
            return blockers;
        }

        if (!nativeReadiness.HasUserWidgetCompat)
        {
            blockers.Add("UserWidget compatibility was not detected on the current Palworld build.");
        }

        if (!nativeReadiness.UiProbeEnabled)
        {
            blockers.Add("ui_probe is disabled in the UE4SS bridge.");
        }
        else if (!nativeReadiness.HasUiProbeCandidates)
        {
            blockers.Add("ui_probe has not captured any ranked widget candidates yet.");
        }

        if (!nativeReadiness.ActionExecutorEnabled)
        {
            blockers.Add("Guarded action executor is disabled, so round-trip feedback proof is incomplete.");
        }

        if (!nativeReadiness.NativeHudEnabled)
        {
            blockers.Add("native_hud_render_enabled is false.");
        }

        if (!nativeReadiness.NativeHudTargetsConfigured)
        {
            blockers.Add("native_hud_widget_targets is empty.");
        }
        else if (string.Equals(
            nativeReadiness.HudBindRecommendation.Status,
            "configured_targets_need_review",
            StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("native_hud_widget_targets does not currently include the top ranked ui_probe candidate.");
        }
        else if (string.Equals(
            nativeReadiness.HudBindRecommendation.Status,
            "configured_targets_unreported",
            StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("native_hud_widget_targets count is reported, but the bridge heartbeat did not include exact target names.");
        }

        if (string.Equals(nativeReadiness.NativeHudConfigSource, "override_error", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(nativeReadiness.NativeHudConfigPath))
            {
                blockers.Add("Native HUD override loading failed, so the bridge fell back to inline defaults.");
            }
            else
            {
                blockers.Add($"Native HUD override loading failed for '{nativeReadiness.NativeHudConfigPath}', so the bridge fell back to inline defaults.");
            }
        }

        if (loopProof.RequestSeen && !loopProof.OutboxReplyWritten)
        {
            blockers.Add("A tracked request exists, but no matching outbox reply has been written yet.");
        }

        if (loopProof.OutboxReplyWritten && !loopProof.VisibleDeliveryConfirmed)
        {
            blockers.Add("A reply was written, but the game has not reported matching rendered delivery yet.");
        }

        if (loopProof.LoopClosed
            && nativeReadiness.HudBindReady
            && !IsNativeHudDeliverySurface(loopProof.LastReplyDelivery?.Surface))
        {
            string surface = FormatDeliverySurface(loopProof.LastReplyDelivery);
            blockers.Add($"The tracked request closed through '{surface}', but release native-proof requires reply_delivery surface=native_hud.");
        }

        if (loopProof.ActionPlanned && !loopProof.ActionFeedbackObserved)
        {
            blockers.Add("A guarded action was planned, but matching bridge feedback has not arrived yet.");
        }

        if (loopProof.SpeechPlaybackExpected && !loopProof.SpeechPlaybackObserved)
        {
            blockers.Add("A speech artifact was written, but the game has not reported a matching playback attempt yet.");
        }
        else if (loopProof.SpeechPlaybackExpected && !loopProof.SpeechPlaybackStarted)
        {
            string reason = string.IsNullOrWhiteSpace(loopProof.LastSpeechPlayback?.Reason)
                ? "no reason supplied"
                : loopProof.LastSpeechPlayback.Reason;
            string failureCode = FormatSpeechPlaybackFailureCode(loopProof.LastSpeechPlayback);
            blockers.Add($"A speech playback attempt was reported but did not start{failureCode}: {reason}.");
        }

        if (string.Equals(loopProof.Status, "delivery_unmatched", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("A delivery event arrived without matching the current tracked request.");
        }

        if (string.Equals(loopProof.Status, "delivery_suppressed", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("The latest delivery event reported suppressed rendering instead of a visible reply.");
        }

        if (string.Equals(loopProof.Status, "feedback_unmatched", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("A feedback event arrived without matching the current tracked request.");
        }

        if (string.Equals(loopProof.Status, "speech_playback_unmatched", StringComparison.OrdinalIgnoreCase))
        {
            blockers.Add("A speech playback event arrived without matching the current tracked request.");
        }

        return blockers;
    }

    private static IReadOnlyList<BridgeProofLaneSnapshot> BuildProofLanes(
        NativeReadinessSnapshot nativeReadiness,
        BridgeLoopProofSnapshot loopProof)
    {
        bool uiProbeCaptureReady =
            nativeReadiness.UiProbeEnabled && nativeReadiness.HasUiProbeCandidates;
        bool actionLanePassed =
            !loopProof.ActionPlanned || loopProof.ActionFeedbackObserved;
        bool speechPlaybackLanePassed =
            !loopProof.SpeechPlaybackExpected || loopProof.SpeechPlaybackStarted;
        bool nativeAudioMixerLaneRequired =
            IsNativeAudioMixerLaneRequired(loopProof.LastSpeechPlayback);
        bool nativeAudioMixerLanePassed =
            !nativeAudioMixerLaneRequired || IsNativeAudioMixerPlaybackStarted(loopProof.LastSpeechPlayback);
        bool nativeHudDeliveryLaneRequired =
            nativeReadiness.HudBindReady && loopProof.VisibleDeliveryConfirmed;
        bool nativeHudDeliveryLanePassed =
            IsNativeHudDeliverySurface(loopProof.LastReplyDelivery?.Surface);

        return
        [
            BuildLane(
                "bridge_boot",
                required: true,
                passed: nativeReadiness.BridgeBootSeen,
                passedSummary: BuildBridgeBootLaneSummary(nativeReadiness),
                failedSummary: "No bridge_boot heartbeat has been observed from a live Palworld session.",
                nextAction: "Launch Palworld with UE4SS and wait for bridge_boot."),

            BuildLane(
                "user_widget_compat",
                required: true,
                passed: nativeReadiness.HasUserWidgetCompat,
                passedSummary: "UserWidget compatibility is present for native HUD discovery.",
                failedSummary: "UserWidget compatibility is not present in the latest bridge heartbeat.",
                nextAction: "Validate UserWidget compatibility on the current Palworld build before binding native HUD."),

            BuildLane(
                "ui_probe_capture",
                required: true,
                passed: uiProbeCaptureReady,
                passedSummary: BuildUiProbeLaneSummary(nativeReadiness),
                failedSummary: nativeReadiness.UiProbeEnabled
                    ? "ui_probe is enabled, but no ranked widget candidates have been captured yet."
                    : "ui_probe is disabled in the UE4SS bridge.",
                nextAction: nativeReadiness.UiProbeEnabled
                    ? "Trigger ui_probe during representative gameplay and review /api/bridge/ui-probe."
                    : "Enable ui_probe in the bridge config, then restart or reload Palworld."),

            BuildLane(
                "native_hud_bind",
                required: false,
                passed: nativeReadiness.HudBindReady,
                passedSummary: BuildNativeHudBindLaneSummary(nativeReadiness),
                failedSummary: BuildNativeHudBindMissingSummary(nativeReadiness),
                nextAction: BuildNativeHudBindNextAction(nativeReadiness)),

            BuildLane(
                "chat_ingress",
                required: true,
                passed: loopProof.RequestSeen,
                passedSummary: $"Tracked chat ingress exists for request '{ResolveTrackedRequestId(loopProof)}'.",
                failedSummary: "No tracked live chat request has entered the sidecar yet.",
                nextAction: "Send one live in-game chat turn while the bridge is running."),

            BuildLane(
                "outbox_reply",
                required: true,
                passed: loopProof.OutboxReplyWritten,
                passedSummary: "A matching sidecar reply envelope was written to Bridge/Outbox.",
                failedSummary: loopProof.RequestSeen
                    ? "A tracked request exists, but no matching outbox reply has been written yet."
                    : "No outbox reply can be matched until a tracked chat request exists.",
                nextAction: loopProof.RequestSeen
                    ? "Inspect sidecar chat logs and Bridge/Outbox for the tracked request id."
                    : "Run a live chat turn first, then recheck /api/bridge/proof."),

            BuildLane(
                "visible_delivery",
                required: true,
                passed: loopProof.VisibleDeliveryConfirmed,
                passedSummary: BuildVisibleDeliveryLaneSummary(loopProof),
                failedSummary: loopProof.OutboxReplyWritten
                    ? "A reply was written, but the game has not reported matching rendered delivery yet."
                    : "Visible delivery cannot be confirmed before a matching outbox reply exists.",
                nextAction: loopProof.OutboxReplyWritten
                    ? "Inspect the UE4SS renderer and outbox consumer for the tracked request id."
                    : "Wait for the sidecar reply envelope before checking native rendering."),

            BuildLane(
                "native_hud_delivery",
                required: nativeHudDeliveryLaneRequired,
                passed: nativeHudDeliveryLanePassed,
                passedSummary: BuildNativeHudDeliveryLaneSummary(loopProof),
                failedSummary: BuildNativeHudDeliveryMissingSummary(loopProof),
                nextAction: BuildNativeHudDeliveryNextAction(loopProof)),

            BuildLane(
                "action_feedback",
                required: loopProof.ActionPlanned,
                passed: actionLanePassed,
                passedSummary: loopProof.ActionPlanned
                    ? "A planned guarded action returned matching bridge feedback."
                    : "No guarded action was planned for the tracked request.",
                failedSummary: "A guarded action was planned, but matching bridge feedback has not arrived yet.",
                nextAction: "Exercise or acknowledge the planned guarded action so the bridge emits matching feedback."),

            BuildLane(
                "speech_playback",
                required: loopProof.SpeechPlaybackExpected,
                passed: speechPlaybackLanePassed,
                passedSummary: BuildSpeechPlaybackLaneSummary(loopProof),
                failedSummary: BuildSpeechPlaybackMissingSummary(loopProof),
                nextAction: BuildSpeechPlaybackNextAction(loopProof)),

            BuildLane(
                "native_audio_mixer",
                required: nativeAudioMixerLaneRequired,
                passed: nativeAudioMixerLanePassed,
                passedSummary: BuildNativeAudioMixerLaneSummary(loopProof),
                failedSummary: BuildNativeAudioMixerMissingSummary(loopProof),
                nextAction: BuildNativeAudioMixerNextAction(loopProof)),
        ];
    }

    private static BridgeProofLaneSnapshot BuildLane(
        string name,
        bool required,
        bool passed,
        string passedSummary,
        string failedSummary,
        string nextAction)
    {
        string status = passed
            ? "PASS"
            : required
                ? "FAIL"
                : "WARN";

        return new BridgeProofLaneSnapshot
        {
            Name = name,
            Required = required,
            Status = status,
            Summary = passed ? passedSummary : failedSummary,
            NextAction = passed ? string.Empty : nextAction,
        };
    }

    private static string BuildBridgeBootLaneSummary(NativeReadinessSnapshot nativeReadiness)
    {
        string version = string.IsNullOrWhiteSpace(nativeReadiness.BridgeVersion)
            ? "unknown"
            : nativeReadiness.BridgeVersion;
        string status = string.IsNullOrWhiteSpace(nativeReadiness.BridgeStatus)
            ? "unknown"
            : nativeReadiness.BridgeStatus;

        return $"bridge_boot heartbeat seen from version '{version}' with status '{status}'.";
    }

    private static string BuildUiProbeLaneSummary(NativeReadinessSnapshot nativeReadiness)
    {
        if (string.IsNullOrWhiteSpace(nativeReadiness.TopUiProbeCandidate))
        {
            return "Ranked widget candidates have been captured.";
        }

        return $"Ranked widget candidates captured; top candidate '{nativeReadiness.TopUiProbeCandidate}'.";
    }

    private static string BuildNativeHudBindLaneSummary(NativeReadinessSnapshot nativeReadiness)
    {
        if (!string.IsNullOrWhiteSpace(nativeReadiness.HudBindRecommendation.RecommendedTarget))
        {
            return $"Native HUD bind is ready for '{nativeReadiness.HudBindRecommendation.RecommendedTarget}'.";
        }

        return "Native HUD bind is ready.";
    }

    private static string BuildNativeHudBindMissingSummary(NativeReadinessSnapshot nativeReadiness)
    {
        if (!string.IsNullOrWhiteSpace(nativeReadiness.HudBindRecommendation.RecommendedTarget))
        {
            return $"Native HUD bind is not ready; recommended target is '{nativeReadiness.HudBindRecommendation.RecommendedTarget}'.";
        }

        return "Native HUD bind is not ready.";
    }

    private static string BuildNativeHudBindNextAction(NativeReadinessSnapshot nativeReadiness)
    {
        if (!string.IsNullOrWhiteSpace(nativeReadiness.HudBindRecommendation.RecommendedTarget))
        {
            return "Run scripts/apply-hud-bind-recommendation.ps1, then restart Palworld and rerun scripts/run-native-proof.ps1.";
        }

        return "Capture ui_probe evidence, populate native_hud_widget_targets, then rerun scripts/run-native-proof.ps1.";
    }

    private static string BuildVisibleDeliveryLaneSummary(BridgeLoopProofSnapshot loopProof)
    {
        string surface = FormatDeliverySurface(loopProof.LastReplyDelivery);

        return $"UE4SS reported matching rendered delivery via '{surface}'.";
    }

    private static string BuildNativeHudDeliveryLaneSummary(BridgeLoopProofSnapshot loopProof)
    {
        string surface = FormatDeliverySurface(loopProof.LastReplyDelivery);
        return $"UE4SS reported matching rendered delivery through the native HUD surface '{surface}'.";
    }

    private static string BuildNativeHudDeliveryMissingSummary(BridgeLoopProofSnapshot loopProof)
    {
        if (!loopProof.VisibleDeliveryConfirmed)
        {
            return "No matching rendered reply has reached the native HUD path yet.";
        }

        string surface = FormatDeliverySurface(loopProof.LastReplyDelivery);
        return $"The latest matching reply rendered through '{surface}', not native_hud.";
    }

    private static string BuildNativeHudDeliveryNextAction(BridgeLoopProofSnapshot loopProof)
    {
        if (!loopProof.OutboxReplyWritten)
        {
            return "Run a live chat turn and wait for the sidecar to write a matching outbox reply.";
        }

        if (!loopProof.VisibleDeliveryConfirmed)
        {
            return "Inspect the UE4SS renderer and wait for a matching reply_delivery event.";
        }

        return "Inspect native_hud_render_enabled, native_hud_widget_targets, and UE4SS HUD bind logs; fallback rendering is visible but not release-native proof.";
    }

    private static string FormatDeliverySurface(ReplyDeliverySnapshot? delivery) =>
        string.IsNullOrWhiteSpace(delivery?.Surface)
            ? "unknown-surface"
            : delivery.Surface.Trim();

    private static string BuildSpeechPlaybackLaneSummary(BridgeLoopProofSnapshot loopProof)
    {
        if (!loopProof.SpeechPlaybackExpected)
        {
            return "No speech artifact was written for the tracked request.";
        }

        string mode = string.IsNullOrWhiteSpace(loopProof.LastSpeechPlayback?.PlaybackMode)
            ? "unknown-mode"
            : loopProof.LastSpeechPlayback.PlaybackMode;
        string hint = string.IsNullOrWhiteSpace(loopProof.LastSpeechPlayback?.PlaybackHint)
            ? string.Empty
            : $" with hint '{loopProof.LastSpeechPlayback.PlaybackHint}'";
        string artifact = FormatSpeechArtifactSize(loopProof.LastSpeechPlayback);
        string format = FormatSpeechAudioFormatReceipt(loopProof.LastSpeechPlayback);
        string launch = FormatSpeechPlaybackLaunchReceipt(loopProof.LastSpeechPlayback);
        string timing = FormatSpeechPlaybackTimingReceipt(loopProof);
        string supersession = FormatSpeechSupersessionReceipt(loopProof.LastSpeechPlayback);

        return $"UE4SS reported matching speech playback via '{mode}'{hint}{artifact}{format}{launch}{timing}{supersession}.";
    }

    private static string FormatSpeechArtifactSize(SpeechPlaybackSnapshot? playback)
    {
        long bytes = playback?.ArtifactBytes ?? 0L;
        return bytes > 0
            ? $" for a {bytes:N0}-byte artifact"
            : string.Empty;
    }

    private static string FormatSpeechPlaybackLaunchReceipt(SpeechPlaybackSnapshot? playback)
    {
        int attempts = playback?.AttemptCount ?? 0;
        int elapsedMs = playback?.ElapsedMs ?? 0;

        if (attempts <= 0 && elapsedMs <= 0)
        {
            return string.Empty;
        }

        if (attempts > 0 && elapsedMs > 0)
        {
            return attempts == 1
                ? $" after 1 launch attempt in {elapsedMs:N0} ms"
                : $" after {attempts:N0} launch attempts in {elapsedMs:N0} ms";
        }

        if (attempts > 0)
        {
            return attempts == 1
                ? " after 1 launch attempt"
                : $" after {attempts:N0} launch attempts";
        }

        return $" in {elapsedMs:N0} ms";
    }

    private static string FormatSpeechPlaybackTimingReceipt(BridgeLoopProofSnapshot loopProof)
    {
        List<string> parts = new(3);
        if (loopProof.SpeechPlaybackIngressLagMs > 0)
        {
            parts.Add($"request-to-receipt {loopProof.SpeechPlaybackIngressLagMs:N0} ms");
        }

        if (loopProof.SpeechPlaybackOutboxLagMs > 0)
        {
            parts.Add($"outbox-to-receipt {loopProof.SpeechPlaybackOutboxLagMs:N0} ms");
        }

        if (loopProof.SpeechPlaybackDeliveryLagMs > 0)
        {
            parts.Add($"delivery-to-receipt {loopProof.SpeechPlaybackDeliveryLagMs:N0} ms");
        }

        return parts.Count == 0
            ? string.Empty
            : $" [speech timing: {string.Join("; ", parts)}]";
    }

    private static string FormatSpeechSupersessionReceipt(SpeechPlaybackSnapshot? playback)
    {
        if (playback is null)
        {
            return string.Empty;
        }

        List<string> parts = new(4);
        if (playback.PlaybackSequence > 0)
        {
            parts.Add($"speech sequence {playback.PlaybackSequence:N0}");
        }

        if (!string.IsNullOrWhiteSpace(playback.SupersededRequestId))
        {
            string age = playback.SupersededSpeechAgeMs > 0
                ? $" after {playback.SupersededSpeechAgeMs:N0} ms"
                : string.Empty;
            string buffer = playback.SupersededSpeechBufferedMs > 0
                ? $", prior buffer ~{playback.SupersededSpeechBufferedMs:N0} ms"
                : string.Empty;
            string remaining = playback.SupersededSpeechRemainingMs > 0
                ? $", ~{playback.SupersededSpeechRemainingMs:N0} ms estimated remaining"
                : string.Empty;
            parts.Add($"superseded previous speech request{age}{buffer}{remaining}");
        }

        string cancellation = FormatSpeechCancellationMode(playback.CancellationMode);
        if (!string.IsNullOrWhiteSpace(cancellation))
        {
            parts.Add(cancellation);
        }

        return parts.Count == 0
            ? string.Empty
            : $" [{string.Join("; ", parts)}]";
    }

    private static string FormatSpeechCancellationMode(string cancellationMode)
    {
        if (string.IsNullOrWhiteSpace(cancellationMode))
        {
            return string.Empty;
        }

        return cancellationMode.Trim().ToLowerInvariant() switch
        {
            "none" => string.Empty,
            "desktop_helper_uncontrolled" => "desktop helper cannot hard-cancel prior speech",
            "native_mixer_pending" => "native mixer cancellation binding still pending",
            _ => $"cancellation mode {cancellationMode.Trim()}",
        };
    }

    private static string FormatSpeechAudioFormatReceipt(SpeechPlaybackSnapshot? playback)
    {
        if (playback is null)
        {
            return string.Empty;
        }

        string format = FormatSpeechAudioFormat(playback);
        if (string.IsNullOrWhiteSpace(format))
        {
            return string.Empty;
        }

        return $" ({format})";
    }

    private static string FormatSpeechAudioFormat(SpeechPlaybackSnapshot playback)
    {
        List<string> parts = new(15);
        string encoding = FormatSpeechAudioEncoding(playback.AudioEncoding);
        if (!string.IsNullOrWhiteSpace(encoding))
        {
            parts.Add(encoding);
        }

        string sampleFormat = FormatSpeechSampleFormat(playback.SampleFormat);
        if (!string.IsNullOrWhiteSpace(sampleFormat))
        {
            parts.Add(sampleFormat);
        }

        string byteOrder = FormatSpeechByteOrder(playback.ByteOrder);
        if (!string.IsNullOrWhiteSpace(byteOrder))
        {
            parts.Add(byteOrder);
        }

        string mixerConversion = FormatSpeechMixerConversionHint(playback.MixerConversionHint);
        if (!string.IsNullOrWhiteSpace(mixerConversion))
        {
            parts.Add(mixerConversion);
        }

        if (playback.SampleRateHz > 0)
        {
            parts.Add($"{playback.SampleRateHz:N0} Hz");
        }

        if (playback.ChannelCount > 0)
        {
            parts.Add(playback.ChannelCount == 1
                ? "mono"
                : playback.ChannelCount == 2
                    ? "stereo"
                    : $"{playback.ChannelCount:N0} channels");
        }

        if (playback.ChannelMask > 0)
        {
            parts.Add($"channel mask 0x{playback.ChannelMask.ToString("X", CultureInfo.InvariantCulture)}");
        }

        if (playback.BitsPerSample > 0)
        {
            parts.Add($"{playback.BitsPerSample:N0}-bit");
        }

        if (playback.ValidBitsPerSample > 0 && playback.ValidBitsPerSample != playback.BitsPerSample)
        {
            parts.Add($"{playback.ValidBitsPerSample:N0} valid bits");
        }

        if (playback.DurationMs > 0)
        {
            parts.Add($"{playback.DurationMs:N0} ms");
        }

        if (playback.ByteRate > 0)
        {
            parts.Add($"{playback.ByteRate:N0} B/s");
        }

        if (playback.BlockAlignBytes > 0)
        {
            parts.Add($"{playback.BlockAlignBytes:N0} B blocks");
        }

        if (playback.AudioDataBytes > 0)
        {
            parts.Add($"{playback.AudioDataBytes:N0} audio bytes");
        }

        if (playback.FrameCount > 0)
        {
            parts.Add($"{playback.FrameCount:N0} sample frames");
        }

        if (playback.MixerQuantumMs > 0
            && playback.MixerQuantumFrames > 0
            && playback.MixerQueueDepthEstimate > 0)
        {
            string buffered = playback.MixerBufferedMs > 0
                ? $", ~{playback.MixerBufferedMs:N0} ms buffered"
                : string.Empty;
            parts.Add($"{playback.MixerQueueDepthEstimate:N0} x {playback.MixerQuantumMs:N0} ms mixer quanta ({playback.MixerQuantumFrames:N0} frames each{buffered})");
        }

        if (playback.MixerTailFrames > 0)
        {
            string tailMs = playback.MixerTailMs > 0
                ? $" (~{playback.MixerTailMs:N0} ms)"
                : string.Empty;
            parts.Add($"{playback.MixerTailFrames:N0} mixer tail frames{tailMs}");
        }

        if (playback.BlockRemainderBytes > 0)
        {
            parts.Add($"{playback.BlockRemainderBytes:N0} partial-frame bytes");
        }

        return string.Join(", ", parts);
    }

    private static string FormatSpeechAudioEncoding(string audioEncoding)
    {
        if (string.IsNullOrWhiteSpace(audioEncoding))
        {
            return string.Empty;
        }

        return audioEncoding.Trim().ToLowerInvariant() switch
        {
            "pcm" => "PCM",
            "extensible_pcm" => "WAVE_FORMAT_EXTENSIBLE PCM",
            "raw_pcm" => "raw PCM",
            "l16_pcm" => "L16 PCM",
            "ieee_float" => "IEEE float",
            "extensible_ieee_float" => "WAVE_FORMAT_EXTENSIBLE IEEE float",
            "alaw" => "A-law",
            "mulaw" => "mu-law",
            "extensible" => "WAVE_FORMAT_EXTENSIBLE",
            string value when value.StartsWith("format_tag_", StringComparison.OrdinalIgnoreCase) => $"WAV format tag {value["format_tag_".Length..]}",
            string value => value.Replace('_', ' '),
        };
    }

    private static string FormatSpeechSampleFormat(string sampleFormat)
    {
        if (string.IsNullOrWhiteSpace(sampleFormat))
        {
            return string.Empty;
        }

        return sampleFormat.Trim().ToLowerInvariant() switch
        {
            "signed_integer" => "signed integer samples",
            "unsigned_integer" => "unsigned integer samples",
            "float" => "floating-point samples",
            "companded" => "companded samples",
            string value => $"{value.Replace('_', ' ')} samples",
        };
    }

    private static string FormatSpeechByteOrder(string byteOrder)
    {
        if (string.IsNullOrWhiteSpace(byteOrder))
        {
            return string.Empty;
        }

        return byteOrder.Trim().ToLowerInvariant() switch
        {
            "little_endian" => "little-endian",
            "big_endian" => "big-endian",
            string value => value.Replace('_', '-'),
        };
    }

    private static string FormatSpeechMixerConversionHint(string mixerConversionHint)
    {
        if (string.IsNullOrWhiteSpace(mixerConversionHint))
        {
            return string.Empty;
        }

        return mixerConversionHint.Trim().ToLowerInvariant() switch
        {
            "already_float32" => "native mixer float32-ready",
            "format_verified" => "native mixer format verified",
            "integer_to_float32" => "native mixer integer-to-float32",
            "byte_swap_integer_to_float32" => "native mixer byte-swap + integer-to-float32",
            "float_width_to_float32" => "native mixer float-width conversion",
            "byte_swap_float_width_to_float32" => "native mixer byte-swap + float-width conversion",
            "byte_swap" => "native mixer byte-swap",
            "decode_to_float32" => "native mixer decode-to-float32",
            "channel_layout_map" => "native mixer channel-layout map",
            string value => $"native mixer {value.Replace('_', ' ')}",
        };
    }

    private static string BuildSpeechPlaybackMissingSummary(BridgeLoopProofSnapshot loopProof)
    {
        if (!loopProof.SpeechPlaybackExpected)
        {
            return "No speech artifact was written for the tracked request.";
        }

        if (!loopProof.SpeechPlaybackObserved)
        {
            return "A speech artifact was written, but no matching speech playback attempt has been reported yet.";
        }

        string reason = string.IsNullOrWhiteSpace(loopProof.LastSpeechPlayback?.Reason)
            ? "no reason supplied"
            : loopProof.LastSpeechPlayback.Reason;
        string launch = FormatSpeechPlaybackLaunchReceipt(loopProof.LastSpeechPlayback);
        string format = FormatSpeechAudioFormatReceipt(loopProof.LastSpeechPlayback);
        string failureCode = FormatSpeechPlaybackFailureCode(loopProof.LastSpeechPlayback);
        string timing = FormatSpeechPlaybackTimingReceipt(loopProof);
        string supersession = FormatSpeechSupersessionReceipt(loopProof.LastSpeechPlayback);
        return $"A matching speech playback attempt was reported but did not start{format}{launch}{failureCode}{timing}{supersession}: {reason}.";
    }

    private static string BuildSpeechPlaybackNextAction(BridgeLoopProofSnapshot loopProof)
    {
        if (!loopProof.SpeechPlaybackExpected)
        {
            return string.Empty;
        }

        if (!loopProof.SpeechPlaybackObserved)
        {
            return "Wait for the UE4SS speech helper to emit speech_playback, or inspect why the outbox consumer skipped speech.";
        }

        string failureCode = loopProof.LastSpeechPlayback?.FailureCode ?? string.Empty;
        if (string.Equals(failureCode, "raw_pcm_block_alignment_invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Regenerate the raw_pcm artifact with complete sample frames, or request a containerized format before rerunning the live turn.";
        }

        if (string.Equals(failureCode, "raw_pcm_native_mixer_required", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.LastSpeechPlayback?.PlaybackMode, "raw_pcm", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.LastSpeechPlayback?.PlaybackHint, "raw_pcm", StringComparison.OrdinalIgnoreCase))
        {
            return "Use a containerized local audio format for helper playback, or bind a native PCM mixer before promoting raw_pcm speech.";
        }

        if (string.Equals(failureCode, "unsupported_format", StringComparison.OrdinalIgnoreCase))
        {
            return "Request wav, mp3, m4a, aac, wma, ogg, opus, or flac for helper playback, then rerun the live turn.";
        }

        if (string.Equals(failureCode, "duplicate_within_dedupe_window", StringComparison.OrdinalIgnoreCase))
        {
            return "Wait for the speech dedupe window to expire or send a new chat-linked speech artifact.";
        }

        if (string.Equals(failureCode, "launch_failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Check the local PowerShell/media helper launch policy and rerun with the same supported audio container.";
        }

        if (string.Equals(failureCode, "missing_speech_path", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureCode, "speech_file_missing", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureCode, "speech_file_unreadable", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureCode, "speech_file_empty", StringComparison.OrdinalIgnoreCase)
            || string.Equals(failureCode, "wave_header_invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Regenerate the TTS artifact and verify the sidecar wrote a non-empty supported audio file before playback.";
        }

        return "Use a supported local audio container or playback hint, then rerun the live turn.";
    }

    private static bool IsNativeAudioMixerLaneRequired(SpeechPlaybackSnapshot? playback)
    {
        return IsRawPcmSpeechPlayback(playback) || IsNativeAudioMixerPlaybackStarted(playback);
    }

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

    private static bool IsNativeAudioMixerPlaybackStarted(SpeechPlaybackSnapshot? playback)
    {
        if (playback is null || !playback.Started)
        {
            return false;
        }

        return IsNativeAudioMixerMode(playback.PlaybackMode)
            || IsNativeAudioMixerMode(playback.PlaybackHint);
    }

    private static bool IsNativeAudioMixerMode(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "native_mixer" => true,
            "native_audio_mixer" => true,
            "raw_pcm_native_mixer" => true,
            _ => false,
        };
    }

    private static bool IsRawPcmSpeechPlayback(SpeechPlaybackSnapshot? playback)
    {
        if (playback is null)
        {
            return false;
        }

        return IsRawPcmAudioEncoding(playback.AudioEncoding)
            || IsRawPcmToken(playback.PlaybackMode)
            || IsRawPcmToken(playback.PlaybackHint)
            || IsRawPcmToken(playback.MimeType)
            || IsRawPcmToken(playback.FileExtension)
            || IsRawPcmToken(playback.FailureCode);
    }

    private static bool IsRawPcmAudioEncoding(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized is "raw_pcm" or "l16_pcm" or "l24_pcm" or "l32_pcm";
    }

    private static bool IsRawPcmToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim().ToLowerInvariant();
        return normalized is "raw_pcm" or ".pcm" or "raw_pcm_native_mixer_required"
            || normalized.StartsWith("audio/pcm", StringComparison.Ordinal)
            || normalized.StartsWith("audio/l16", StringComparison.Ordinal)
            || normalized.Contains("raw_pcm", StringComparison.Ordinal);
    }

    private static string BuildNativeAudioMixerLaneSummary(BridgeLoopProofSnapshot loopProof)
    {
        SpeechPlaybackSnapshot? playback = loopProof.LastSpeechPlayback;
        if (!loopProof.SpeechPlaybackExpected)
        {
            return "No speech artifact was written for the tracked request, so native audio mixer proof is not required.";
        }

        if (playback is null)
        {
            return "No speech playback receipt has required native audio mixer proof yet.";
        }

        if (IsNativeAudioMixerPlaybackStarted(playback))
        {
            string format = FormatSpeechAudioFormatReceipt(playback);
            string timing = FormatSpeechPlaybackTimingReceipt(loopProof);
            return $"Native audio mixer reported raw PCM playback started{format}{timing}.";
        }

        string mode = string.IsNullOrWhiteSpace(playback.PlaybackMode)
            ? "helper"
            : playback.PlaybackMode;
        return $"Speech playback used '{mode}', so raw PCM native mixer binding was not required.";
    }

    private static string BuildNativeAudioMixerMissingSummary(BridgeLoopProofSnapshot loopProof)
    {
        SpeechPlaybackSnapshot? playback = loopProof.LastSpeechPlayback;
        if (playback is null)
        {
            return "No speech playback receipt has arrived for native audio mixer proof.";
        }

        string format = FormatSpeechAudioFormatReceipt(playback);
        string timing = FormatSpeechPlaybackTimingReceipt(loopProof);
        if (string.Equals(playback.FailureCode, "raw_pcm_block_alignment_invalid", StringComparison.OrdinalIgnoreCase))
        {
            return $"Raw PCM reached native-audio proof with incomplete sample frames{format}{timing}; fix block alignment before mixer promotion.";
        }

        if (string.Equals(playback.FailureCode, "native_audio_mixer_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return $"Raw PCM reached the native audio mixer callback path{format}{timing}, but the configured callback was unavailable.";
        }

        if (string.Equals(playback.FailureCode, "native_audio_mixer_failed", StringComparison.OrdinalIgnoreCase))
        {
            return $"Raw PCM reached the native audio mixer callback path{format}{timing}, but the callback failed before reporting playback started.";
        }

        if (string.Equals(playback.FailureCode, "native_audio_mixer_rejected", StringComparison.OrdinalIgnoreCase))
        {
            return $"Raw PCM reached the native audio mixer callback path{format}{timing}, but the callback rejected the buffer instead of starting playback.";
        }

        return $"Raw PCM reached the proof-only native audio lane{format}{timing}, but no native mixer binding started playback.";
    }

    private static string BuildNativeAudioMixerNextAction(BridgeLoopProofSnapshot loopProof)
    {
        SpeechPlaybackSnapshot? playback = loopProof.LastSpeechPlayback;
        if (playback is null)
        {
            return "Capture a speech_playback receipt for the tracked request, then recheck native audio mixer proof.";
        }

        if (string.Equals(playback.FailureCode, "raw_pcm_block_alignment_invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "Regenerate the raw_pcm artifact with complete sample frames, or request a containerized audio format before rerunning native audio proof.";
        }

        if (string.Equals(playback.FailureCode, "native_audio_mixer_unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return "Expose the configured PalLLM_NativeAudioMixer_PlayRawPcm callback, or turn native_audio_mixer_enabled back off until the binding exists.";
        }

        if (string.Equals(playback.FailureCode, "native_audio_mixer_failed", StringComparison.OrdinalIgnoreCase))
        {
            return "Fix the native audio mixer callback failure and rerun the raw_pcm proof turn.";
        }

        if (string.Equals(playback.FailureCode, "native_audio_mixer_rejected", StringComparison.OrdinalIgnoreCase))
        {
            return "Update the native audio mixer callback to accept the raw PCM buffer before it emits a started native_mixer receipt.";
        }

        return "Bind a native PCM mixer that emits a started native_mixer speech_playback receipt, or request wav, mp3, m4a, aac, wma, ogg, opus, or flac for helper playback.";
    }

    private static string FormatSpeechPlaybackFailureCode(SpeechPlaybackSnapshot? playback)
    {
        string code = playback?.FailureCode ?? string.Empty;
        return string.IsNullOrWhiteSpace(code)
            ? string.Empty
            : $" [{code}]";
    }

    private static string BuildStatus(
        NativeReadinessSnapshot nativeReadiness,
        BridgeLoopProofSnapshot loopProof)
    {
        if (string.Equals(loopProof.Status, "delivery_unmatched", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.Status, "delivery_suppressed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.Status, "feedback_unmatched", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.Status, "speech_playback_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.Status, "speech_playback_unmatched", StringComparison.OrdinalIgnoreCase))
        {
            return loopProof.Status;
        }

        if (string.Equals(loopProof.Status, "closed", StringComparison.OrdinalIgnoreCase))
        {
            if (!nativeReadiness.HudBindReady)
            {
                return "delivery_proven_pending_hud_bind";
            }

            return IsNativeHudDeliverySurface(loopProof.LastReplyDelivery?.Surface)
                ? "delivery_proven"
                : "delivery_proven_pending_native_hud_surface";
        }

        if (string.Equals(loopProof.Status, "awaiting_action_feedback", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.Status, "awaiting_delivery", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.Status, "awaiting_reply", StringComparison.OrdinalIgnoreCase)
            || string.Equals(loopProof.Status, "awaiting_speech_playback", StringComparison.OrdinalIgnoreCase))
        {
            return loopProof.Status;
        }

        if (!nativeReadiness.BridgeBootSeen)
        {
            return "awaiting_bridge_boot";
        }

        if (!nativeReadiness.UiProbeEnabled)
        {
            return "ui_probe_disabled";
        }

        if (!nativeReadiness.HasUserWidgetCompat)
        {
            return "missing_userwidget_compat";
        }

        if (!nativeReadiness.HasUiProbeCandidates)
        {
            return "awaiting_ui_probe_capture";
        }

        if (!nativeReadiness.HudBindReady)
        {
            return "ready_for_hud_bind";
        }

        return "ready_for_live_turn";
    }

    private static string BuildSummary(
        string status,
        NativeReadinessSnapshot nativeReadiness,
        BridgeLoopProofSnapshot loopProof) =>
        status switch
        {
            "awaiting_bridge_boot" => "The sidecar is running, but no live Palworld bridge heartbeat has been observed yet.",
            "ui_probe_disabled" => "The bridge is alive, but ui_probe is disabled, so PalLLM cannot rank native HUD targets yet.",
            "missing_userwidget_compat" => "The bridge heartbeat is present, but the current Palworld build has not reported UserWidget compatibility for native HUD discovery.",
            "awaiting_ui_probe_capture" => "The bridge is alive and ui_probe is enabled, but no ranked HUD candidates have been captured yet.",
            "ready_for_hud_bind" when !string.IsNullOrWhiteSpace(nativeReadiness.HudBindRecommendation.RecommendedTarget) =>
                $"Bridge boot plus widget evidence are present; next bind target is '{nativeReadiness.HudBindRecommendation.RecommendedTarget}', but ship HUD config is not ready yet.",
            "ready_for_hud_bind" => "Bridge boot plus widget evidence are present, but the ship HUD bind is not configured yet.",
            "ready_for_live_turn" => "Native HUD prerequisites are configured; PalLLM is ready for a live in-game turn proof.",
            "awaiting_reply" => "A live request is tracked and PalLLM is waiting for the sidecar to write the matching reply envelope.",
            "awaiting_delivery" => "The sidecar wrote the reply envelope, but the game has not reported matching rendered delivery yet.",
            "awaiting_speech_playback" => "Visible delivery is confirmed, but PalLLM is still waiting for the game-side speech playback receipt.",
            "awaiting_action_feedback" => "Visible delivery is confirmed, but PalLLM is still waiting for matching guarded-action feedback.",
            "delivery_proven" => "PalLLM has end-to-end delivery proof and the native HUD bind prerequisites are configured.",
            "delivery_proven_pending_hud_bind" => "PalLLM has end-to-end delivery proof, but the native HUD bind is still not configured for ship use.",
            "delivery_proven_pending_native_hud_surface" => "PalLLM has end-to-end delivery proof, but the latest rendered reply used a fallback surface instead of native_hud.",
            "delivery_unmatched" => "A delivery event arrived, but it did not match the current tracked request.",
            "delivery_suppressed" => "The latest delivery event reported suppressed rendering instead of a visible in-game reply.",
            "feedback_unmatched" => "A feedback event arrived, but it did not match the current tracked request.",
            "speech_playback_failed" => "The game reported a matching speech playback attempt, but local playback did not start.",
            "speech_playback_unmatched" => "A speech playback event arrived, but it did not match the current tracked request.",
            _ when loopProof.LoopClosed && nativeReadiness.HudBindReady =>
                "PalLLM has live loop proof and native HUD prerequisites configured.",
            _ => $"Bridge proof status is '{status}'.",
        };

    private static string BuildRecommendedNextStep(string status, NativeReadinessSnapshot nativeReadiness) =>
        status switch
        {
            "awaiting_bridge_boot" => "Launch Palworld with UE4SS and wait for a bridge_boot heartbeat before validating any native seam.",
            "ui_probe_disabled" => "Enable ui_probe in the UE4SS bridge and capture a widget dump during representative gameplay.",
            "missing_userwidget_compat" => "Validate UserWidget compatibility on the current Palworld build before attempting native HUD binding.",
            "awaiting_ui_probe_capture" => "Trigger ui_probe during a representative Palworld scene and review GET /api/bridge/ui-probe for ranked candidates.",
            "ready_for_hud_bind" when !string.IsNullOrWhiteSpace(nativeReadiness.HudBindRecommendation.RecommendedTarget) =>
                $"Populate native_hud_widget_targets with '{nativeReadiness.HudBindRecommendation.RecommendedTarget}' first, then turn native_hud_render_enabled on for the next live smoke pass.",
            "ready_for_hud_bind" => "Populate native_hud_widget_targets from the confirmed ui_probe shortlist and turn native_hud_render_enabled on for the next live smoke pass.",
            "ready_for_live_turn" => "Run a live Palworld chat turn and verify the request closes through outbox, delivery, and feedback.",
            "awaiting_reply" => "Wait for the sidecar to finish the tracked turn or inspect adapter logs for why the reply envelope was not written.",
            "awaiting_delivery" => "Inspect the UE4SS renderer and Bridge/Outbox consumer for why the reply was not reported as rendered.",
            "awaiting_speech_playback" => "Inspect the UE4SS speech helper and wait for a matching speech_playback event for the tracked request.",
            "awaiting_action_feedback" => "Exercise or acknowledge the planned guarded action so matching bridge feedback closes the loop.",
            "delivery_proven" => "Capture and archive a live Palworld smoke artifact so this proof snapshot becomes part of the release evidence.",
            "delivery_proven_pending_hud_bind" => "Promote the confirmed widget seam into ship configuration, then rerun a live turn through the native HUD path.",
            "delivery_proven_pending_native_hud_surface" => "Fix the native HUD bind until reply_delivery reports surface=native_hud, then rerun scripts/run-native-proof.ps1.",
            "delivery_unmatched" => "Compare request ids between the outbox reply and the reply_delivery event to fix the bridge correlation path.",
            "delivery_suppressed" => "Inspect the current surface strategy and suppression note, then rerun the turn until the game reports visible rendering.",
            "feedback_unmatched" => "Compare request ids between the planned action and the returned feedback event to repair the bridge trace path.",
            "speech_playback_failed" => "Review the speech playback reason and local audio helper support, then rerun the turn with a supported TTS response format.",
            "speech_playback_unmatched" => "Compare request ids between the speech artifact and speech_playback event to repair the bridge trace path.",
            _ => "Inspect the latest bridge boot, ui_probe evidence, and loop-proof details before promoting any new native surface.",
        };

    private static string ResolveTrackedRequestId(BridgeLoopProofSnapshot loopProof)
    {
        if (!string.IsNullOrWhiteSpace(loopProof.ActiveRequestId))
        {
            return loopProof.ActiveRequestId;
        }

        if (!string.IsNullOrWhiteSpace(loopProof.LastIngress?.RequestId))
        {
            return loopProof.LastIngress.RequestId;
        }

        if (!string.IsNullOrWhiteSpace(loopProof.LastOutboxReply?.RequestId))
        {
            return loopProof.LastOutboxReply.RequestId;
        }

        return "unknown";
    }
}
