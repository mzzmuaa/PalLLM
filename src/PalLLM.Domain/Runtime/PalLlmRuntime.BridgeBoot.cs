using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    private void RememberBridgeBoot(BridgeBootPayload payload)
    {
        BridgeBootPayload normalized = NormalizeBridgeBootPayload(payload);
        lock (_bridgeGate)
        {
            _lastBridgeBoot = normalized;
        }
    }

    private static BridgeBootPayload NormalizeBridgeBootPayload(BridgeBootPayload payload)
    {
        BridgeBootCompatSignal[] compatSignals = NormalizeCompatSignals(payload.CompatSignals, payload.Compat);
        string compatSummary = string.IsNullOrWhiteSpace(payload.Compat)
            ? BuildCompatSummary(compatSignals)
            : payload.Compat.Trim();
        string[] nativeHudWidgetTargets = NormalizeHudTargetList(payload.NativeHudWidgetTargets);

        return new BridgeBootPayload
        {
            Version = payload.Version ?? string.Empty,
            Status = payload.Status ?? string.Empty,
            Compat = compatSummary,
            CompatSignals = compatSignals,
            UiProbeEnabled = payload.UiProbeEnabled,
            ActionExecutorEnabled = payload.ActionExecutorEnabled,
            NativeHudRenderEnabled = payload.NativeHudRenderEnabled,
            NativeHudWidgetTargetCount = Math.Max(
                Math.Max(0, payload.NativeHudWidgetTargetCount),
                nativeHudWidgetTargets.Length),
            NativeHudWidgetTargets = nativeHudWidgetTargets,
            NativeHudConfigSource = payload.NativeHudConfigSource?.Trim() ?? string.Empty,
            NativeHudConfigPath = payload.NativeHudConfigPath?.Trim() ?? string.Empty,
            ProductionSamplerEnabled = payload.ProductionSamplerEnabled,
            WaypointNativeMarkerEnabled = payload.WaypointNativeMarkerEnabled,
        };
    }

    private static BridgeBootCompatSignal[] NormalizeCompatSignals(
        IReadOnlyList<BridgeBootCompatSignal>? compatSignals,
        string compatSummary)
    {
        if (compatSignals is { Count: > 0 })
        {
            return compatSignals
                .Where(signal => !string.IsNullOrWhiteSpace(signal.Key))
                .Select(signal => new BridgeBootCompatSignal
                {
                    Key = signal.Key.Trim(),
                    Present = signal.Present,
                })
                .ToArray();
        }

        if (string.IsNullOrWhiteSpace(compatSummary))
        {
            return [];
        }

        List<BridgeBootCompatSignal> parsed = [];
        foreach (string part in compatSummary.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = part.IndexOf('=');
            string key = separatorIndex >= 0 ? part[..separatorIndex].Trim() : part.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string value = separatorIndex >= 0 ? part[(separatorIndex + 1)..].Trim() : "missing";
            parsed.Add(new BridgeBootCompatSignal
            {
                Key = key,
                Present = value.Equals("present", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("ready", StringComparison.OrdinalIgnoreCase),
            });
        }

        return parsed.ToArray();
    }

    private static string BuildCompatSummary(IReadOnlyList<BridgeBootCompatSignal> compatSignals) =>
        compatSignals.Count == 0
            ? string.Empty
            : string.Join(
                " | ",
                compatSignals.Select(signal => $"{signal.Key}={(signal.Present ? "present" : "missing")}"));

    private static BridgeBootPayload? CloneBridgeBootPayload(BridgeBootPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        string[] nativeHudWidgetTargets = NormalizeHudTargetList(payload.NativeHudWidgetTargets);

        return new BridgeBootPayload
        {
            Version = payload.Version ?? string.Empty,
            Status = payload.Status ?? string.Empty,
            Compat = payload.Compat ?? string.Empty,
            CompatSignals = NormalizeCompatSignals(payload.CompatSignals, payload.Compat ?? string.Empty),
            UiProbeEnabled = payload.UiProbeEnabled,
            ActionExecutorEnabled = payload.ActionExecutorEnabled,
            NativeHudRenderEnabled = payload.NativeHudRenderEnabled,
            NativeHudWidgetTargetCount = Math.Max(
                Math.Max(0, payload.NativeHudWidgetTargetCount),
                nativeHudWidgetTargets.Length),
            NativeHudWidgetTargets = nativeHudWidgetTargets,
            NativeHudConfigSource = payload.NativeHudConfigSource?.Trim() ?? string.Empty,
            NativeHudConfigPath = payload.NativeHudConfigPath?.Trim() ?? string.Empty,
            ProductionSamplerEnabled = payload.ProductionSamplerEnabled,
            WaypointNativeMarkerEnabled = payload.WaypointNativeMarkerEnabled,
        };
    }

    private static NativeReadinessSnapshot BuildNativeReadinessSnapshot(
        BridgeBootPayload? bridgeBoot,
        UiProbeDiagnosticsSnapshot? diagnostics)
    {
        BridgeBootPayload? normalizedBoot = bridgeBoot is null ? null : NormalizeBridgeBootPayload(bridgeBoot);
        IReadOnlyList<BridgeBootCompatSignal> compatSignals = normalizedBoot?.CompatSignals ?? Array.Empty<BridgeBootCompatSignal>();

        bool bridgeBootSeen = normalizedBoot is not null;
        bool uiProbeEnabled = normalizedBoot?.UiProbeEnabled ?? false;
        bool hasPalGameStateCompat = HasCompatSignal(compatSignals, "PalGameStateInGame");
        bool hasPalCharacterCompat = HasCompatSignal(compatSignals, "PalCharacter");
        bool hasPalBaseCampManagerCompat = HasCompatSignal(compatSignals, "PalBaseCampManager");
        bool hasPalMapManagerCompat = HasCompatSignal(compatSignals, "PalMapManager");
        bool hasUserWidgetCompat = HasCompatSignal(compatSignals, "UserWidget");
        bool hasUiProbeCandidates = diagnostics?.Candidates?.Count > 0;
        string[] configuredHudTargets = NormalizeHudTargetList(normalizedBoot?.NativeHudWidgetTargets);
        string nativeHudConfigSource = normalizedBoot?.NativeHudConfigSource ?? string.Empty;
        string nativeHudConfigPath = normalizedBoot?.NativeHudConfigPath ?? string.Empty;
        string topUiProbeCandidate = diagnostics?.Candidates?.FirstOrDefault() switch
        {
            UiProbeCandidateSummary candidate when !string.IsNullOrWhiteSpace(candidate.DisplayName) => candidate.DisplayName,
            UiProbeCandidateSummary candidate when !string.IsNullOrWhiteSpace(candidate.FullName) => candidate.FullName,
            _ => string.Empty,
        };

        bool nativeHudEnabled = normalizedBoot?.NativeHudRenderEnabled ?? false;
        int nativeHudTargetCount = Math.Max(
            configuredHudTargets.Length,
            normalizedBoot?.NativeHudWidgetTargetCount ?? 0);
        bool nativeHudTargetsConfigured = nativeHudTargetCount > 0;
        bool hudSeamDiscovered = bridgeBootSeen && hasUserWidgetCompat && hasUiProbeCandidates;
        bool hudBindReady = hudSeamDiscovered && nativeHudEnabled && nativeHudTargetsConfigured;
        bool productionSamplerEnabled = normalizedBoot?.ProductionSamplerEnabled ?? false;
        bool productionSamplerReady = bridgeBootSeen && hasPalBaseCampManagerCompat && productionSamplerEnabled;
        bool waypointNativeMarkerEnabled = normalizedBoot?.WaypointNativeMarkerEnabled ?? false;
        bool waypointMarkerReady = bridgeBootSeen && hasPalMapManagerCompat && waypointNativeMarkerEnabled;
        bool actionExecutorEnabled = normalizedBoot?.ActionExecutorEnabled ?? false;
        HudBindRecommendationSnapshot hudBindRecommendation = BuildHudBindRecommendation(
            bridgeBootSeen,
            uiProbeEnabled,
            hasUserWidgetCompat,
            nativeHudEnabled,
            nativeHudTargetCount,
            configuredHudTargets,
            diagnostics);

        List<string> readyCapabilities = [];
        List<string> missingPrerequisites = [];

        if (!bridgeBootSeen)
        {
            missingPrerequisites.Add("No bridge_boot heartbeat has been observed yet. Launch Palworld with the UE4SS bridge running.");
        }
        else
        {
            readyCapabilities.Add($"Bridge boot heartbeat received from version '{normalizedBoot!.Version}' with status '{normalizedBoot.Status}'.");

            if (hasPalGameStateCompat)
            {
                readyCapabilities.Add("PalGameStateInGame class is present.");
            }

            if (hasPalCharacterCompat)
            {
                readyCapabilities.Add("PalCharacter class is present.");
            }

            if (hasUserWidgetCompat)
            {
                readyCapabilities.Add("UserWidget class is present for native HUD targeting.");
            }
            else
            {
                missingPrerequisites.Add("UserWidget class was not detected on the current Palworld build.");
            }

            if (hasPalBaseCampManagerCompat)
            {
                readyCapabilities.Add("PalBaseCampManager is present for production sampling.");
            }
            else
            {
                missingPrerequisites.Add("PalBaseCampManager was not detected on the current Palworld build.");
            }

            if (hasPalMapManagerCompat)
            {
                readyCapabilities.Add("PalMapManager is present for waypoint marker hints.");
            }
            else
            {
                missingPrerequisites.Add("PalMapManager was not detected on the current Palworld build.");
            }

            if (uiProbeEnabled)
            {
                if (hasUiProbeCandidates)
                {
                    readyCapabilities.Add($"ui_probe has captured candidate widgets; top candidate: '{topUiProbeCandidate}'.");
                }
                else
                {
                    missingPrerequisites.Add("ui_probe is enabled but has not captured any ranked widget candidates yet.");
                }
            }
            else
            {
                missingPrerequisites.Add("ui_probe is disabled in the UE4SS bridge.");
            }

            if (nativeHudEnabled)
            {
                readyCapabilities.Add("native_hud_render_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("native_hud_render_enabled is false.");
            }

            if (nativeHudTargetsConfigured)
            {
                readyCapabilities.Add("native_hud_widget_targets has at least one configured target.");
            }
            else
            {
                missingPrerequisites.Add("native_hud_widget_targets is empty.");
            }

            if (productionSamplerEnabled)
            {
                readyCapabilities.Add("production_sampler_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("production_sampler_enabled is false.");
            }

            if (waypointNativeMarkerEnabled)
            {
                readyCapabilities.Add("waypoint_native_marker_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("waypoint_native_marker_enabled is false.");
            }

            if (actionExecutorEnabled)
            {
                readyCapabilities.Add("action_executor_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("action_executor_enabled is false.");
            }
        }

        if (string.Equals(nativeHudConfigSource, "override_error", StringComparison.OrdinalIgnoreCase))
        {
            string pathDetail = string.IsNullOrWhiteSpace(nativeHudConfigPath)
                ? "the configured override file"
                : $"'{nativeHudConfigPath}'";
            missingPrerequisites.Add($"Native HUD override loading failed for {pathDetail}; the bridge fell back to inline defaults.");
        }
        else if (!string.IsNullOrWhiteSpace(nativeHudConfigPath))
        {
            string configSummary = string.Equals(nativeHudConfigSource, "inline_defaults", StringComparison.OrdinalIgnoreCase)
                ? $"Native HUD is currently using inline defaults; preferred override path is '{nativeHudConfigPath}'."
                : $"Native HUD override loaded from '{nativeHudConfigPath}'.";
            readyCapabilities.Add(configSummary);
        }

        if (!string.IsNullOrWhiteSpace(hudBindRecommendation.RecommendedTarget))
        {
            readyCapabilities.Add($"HUD bind recommendation is '{hudBindRecommendation.RecommendedTarget}'.");
        }

        switch (hudBindRecommendation.Status)
        {
            case "configured_targets_need_review":
                missingPrerequisites.Add("Configured native_hud_widget_targets do not include the top ranked ui_probe candidate yet.");
                break;
            case "configured_targets_unreported":
                missingPrerequisites.Add("native_hud_widget_targets count is reported, but the bridge heartbeat did not include exact target names.");
                break;
            case "recommend_target" when !string.IsNullOrWhiteSpace(hudBindRecommendation.RecommendedTarget):
                missingPrerequisites.Add($"native_hud_widget_targets should start with '{hudBindRecommendation.RecommendedTarget}'.");
                break;
        }

        return new NativeReadinessSnapshot
        {
            BridgeBootSeen = bridgeBootSeen,
            BridgeVersion = normalizedBoot?.Version ?? string.Empty,
            BridgeStatus = normalizedBoot?.Status ?? string.Empty,
            CompatSummary = normalizedBoot?.Compat ?? string.Empty,
            CompatSignals = compatSignals.ToArray(),
            UiProbeEnabled = uiProbeEnabled,
            HasPalGameStateCompat = hasPalGameStateCompat,
            HasPalCharacterCompat = hasPalCharacterCompat,
            HasPalBaseCampManagerCompat = hasPalBaseCampManagerCompat,
            HasPalMapManagerCompat = hasPalMapManagerCompat,
            HasUserWidgetCompat = hasUserWidgetCompat,
            HasUiProbeCandidates = hasUiProbeCandidates,
            TopUiProbeCandidate = topUiProbeCandidate,
            ConfiguredHudTargets = configuredHudTargets,
            NativeHudConfigSource = nativeHudConfigSource,
            NativeHudConfigPath = nativeHudConfigPath,
            ActionExecutorEnabled = actionExecutorEnabled,
            NativeHudEnabled = nativeHudEnabled,
            NativeHudTargetsConfigured = nativeHudTargetsConfigured,
            HudSeamDiscovered = hudSeamDiscovered,
            HudBindReady = hudBindReady,
            ProductionSamplerEnabled = productionSamplerEnabled,
            ProductionSamplerReady = productionSamplerReady,
            WaypointNativeMarkerEnabled = waypointNativeMarkerEnabled,
            WaypointMarkerReady = waypointMarkerReady,
            HudBindRecommendation = hudBindRecommendation,
            ReadyCapabilities = readyCapabilities,
            MissingPrerequisites = missingPrerequisites,
        };
    }

    private static HudBindRecommendationSnapshot BuildHudBindRecommendation(
        bool bridgeBootSeen,
        bool uiProbeEnabled,
        bool hasUserWidgetCompat,
        bool nativeHudEnabled,
        int configuredTargetCount,
        IReadOnlyList<string> configuredTargets,
        UiProbeDiagnosticsSnapshot? diagnostics)
    {
        UiProbeCandidateSummary[] shortlist = (diagnostics?.Candidates ?? Array.Empty<UiProbeCandidateSummary>())
            .Take(3)
            .Select(CloneUiProbeCandidate)
            .ToArray();

        UiProbeCandidateSummary? topCandidate = shortlist.FirstOrDefault();
        string recommendedDisplayName = topCandidate?.DisplayName ?? string.Empty;
        string recommendedFullName = topCandidate?.FullName ?? string.Empty;
        string recommendedClassName = topCandidate?.ClassName ?? string.Empty;
        string recommendedTarget = TakeFirstNonBlank(
            recommendedFullName,
            recommendedClassName,
            recommendedDisplayName);
        string[] suggestedConfigTargets = shortlist
            .Select(candidate => candidate.FullName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (suggestedConfigTargets.Length == 0 && !string.IsNullOrWhiteSpace(recommendedTarget))
        {
            suggestedConfigTargets = [recommendedTarget];
        }

        bool configuredTargetsReported = configuredTargets.Count > 0 || configuredTargetCount == 0;
        bool configuredTargetMatchesRecommendation =
            !string.IsNullOrWhiteSpace(recommendedFullName)
            && configuredTargets.Any(target =>
                string.Equals(target, recommendedFullName, StringComparison.Ordinal));

        string status;
        string summary;
        List<string> suggestedNextSteps = [];

        if (!bridgeBootSeen)
        {
            status = "awaiting_bridge_boot";
            summary = "Launch Palworld with UE4SS so the bridge can report native HUD readiness.";
        }
        else if (!hasUserWidgetCompat)
        {
            status = "missing_userwidget_compat";
            summary = "UserWidget compatibility is missing on the current Palworld build, so native HUD binding should stay off.";
        }
        else if (!uiProbeEnabled)
        {
            status = "ui_probe_disabled";
            summary = "Enable ui_probe before choosing a native HUD widget target.";
        }
        else if (topCandidate is null)
        {
            status = "awaiting_ui_probe_capture";
            summary = "Capture a representative ui_probe dump before binding a Palworld HUD widget.";
        }
        else if (configuredTargetCount > 0 && !configuredTargetsReported)
        {
            status = "configured_targets_unreported";
            summary = "HUD targets are configured on the bridge, but the heartbeat did not report their exact names yet.";
            suggestedNextSteps.Add("Update the bridge boot heartbeat so it reports native_hud_widget_targets verbatim.");
        }
        else if (configuredTargetMatchesRecommendation && nativeHudEnabled)
        {
            status = "bind_ready";
            summary = "The top ranked ui_probe candidate is already configured and native HUD rendering is enabled.";
            suggestedNextSteps.Add("Run a live Palworld turn and verify reply_delivery reports surface=native_hud.");
        }
        else if (configuredTargetMatchesRecommendation)
        {
            status = "configured_target_ready";
            summary = "The top ranked ui_probe candidate is already configured; enable native_hud_render_enabled for the next smoke pass.";
            suggestedNextSteps.Add("Flip native_hud_render_enabled to true and rerun a live turn.");
        }
        else if (configuredTargets.Count > 0)
        {
            status = "configured_targets_need_review";
            summary = "Configured HUD targets do not currently include the top ranked ui_probe candidate.";
            suggestedNextSteps.Add("Move the recommended target to the front of native_hud_widget_targets before the next smoke pass.");
        }
        else
        {
            status = "recommend_target";
            summary = "A ranked Palworld HUD target is ready to copy into native_hud_widget_targets.";
            suggestedNextSteps.Add("Copy the recommended target into native_hud_widget_targets as the first entry.");
        }

        if (!string.IsNullOrWhiteSpace(recommendedFullName))
        {
            suggestedNextSteps.Add($"Prefer '{recommendedFullName}' as the first native_hud_widget_targets entry.");
        }

        return new HudBindRecommendationSnapshot
        {
            Status = status,
            Summary = summary,
            RecommendedTarget = recommendedTarget,
            RecommendedDisplayName = recommendedDisplayName,
            RecommendedFullName = recommendedFullName,
            RecommendedClassName = recommendedClassName,
            RecommendedScore = Math.Max(0, topCandidate?.Score ?? 0),
            ConfiguredTargetMatchesRecommendation = configuredTargetMatchesRecommendation,
            ConfiguredTargets = configuredTargets.ToArray(),
            SuggestedConfigTargets = suggestedConfigTargets,
            SuggestedNextSteps = suggestedNextSteps
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Rationale = (topCandidate?.Rationale ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
            Shortlist = shortlist,
        };
    }

    private static bool HasCompatSignal(
        IReadOnlyList<BridgeBootCompatSignal> compatSignals,
        string key) =>
        compatSignals.Any(signal =>
            signal.Present
            && string.Equals(signal.Key, key, StringComparison.OrdinalIgnoreCase));

    private static string[] NormalizeHudTargetList(IReadOnlyList<string>? targets) =>
        (targets ?? Array.Empty<string>())
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
