using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

internal sealed class PresentationCuePlanner
{
    public PresentationCuePlan Build(
        FallbackBehaviorContext context,
        FallbackBehaviorDecision anchorDecision,
        string source,
        bool usedFallback,
        string? assistantMessage,
        int actionPriority)
    {
        StrategyProfile profile = StrategyProfile.Lookup(anchorDecision.StrategyId);
        AudioCuePlan audio = ApplyAudioOverlays(profile.BuildAudio(), context, anchorDecision);
        VisualCuePlan visual = ApplyVisualOverlays(profile.BuildVisual(), context, anchorDecision);
        DeliverySurfacePlan surface = BuildSurface(
            context,
            anchorDecision,
            source,
            usedFallback,
            assistantMessage,
            audio,
            visual,
            actionPriority);

        return new PresentationCuePlan
        {
            Source = source,
            StrategyId = anchorDecision.StrategyId,
            Phase = anchorDecision.Phase.ToString(),
            Summary = BuildSummary(profile.Summary, audio, visual, usedFallback),
            Audio = audio,
            Visual = visual,
            Surface = surface,
        };
    }

    private static AudioCuePlan ApplyAudioOverlays(
        AudioCuePlan baseAudio,
        FallbackBehaviorContext context,
        FallbackBehaviorDecision anchorDecision)
    {
        List<string> layers = [.. baseAudio.Layers];
        string subtitleStyle = baseAudio.SubtitleStyle;
        string musicMode = baseAudio.MusicMode;
        string stinger = baseAudio.Stinger;
        string mixProfile = baseAudio.MixProfile;
        int priority = baseAudio.Priority;
        int cooldownMs = baseAudio.CooldownMs;

        switch (anchorDecision.Phase)
        {
            case FallbackPacingPhase.Relax:
                layers.Add("phase-relax");
                musicMode = AppendFacet(musicMode, "warm-breathing-pad");
                break;
            case FallbackPacingPhase.BuildUp:
                layers.Add("phase-build-up");
                musicMode = AppendFacet(musicMode, "anticipation-rise");
                stinger = AppendFacet(stinger, "briefing-pulse");
                priority = Math.Max(priority, 78);
                break;
            case FallbackPacingPhase.Peak:
                layers.Add("phase-peak");
                musicMode = AppendFacet(musicMode, "adrenaline-surge");
                stinger = AppendFacet(stinger, "hard-alert");
                mixProfile = AppendFacet(mixProfile, "threat-ducking");
                priority = Math.Max(priority, 90);
                break;
            case FallbackPacingPhase.Recover:
                layers.Add("phase-recover");
                musicMode = AppendFacet(musicMode, "recovery-exhale-bed");
                stinger = AppendFacet(stinger, "reset-breath");
                cooldownMs += 500;
                break;
        }

        if (context.HasWeatherRisk)
        {
            layers.Add("weather-muffle-warning");
            mixProfile = AppendFacet(mixProfile, "weather-muffle");
        }

        if (context.IsNight)
        {
            layers.Add("night-low-register-landmarks");
            subtitleStyle = AppendFacet(subtitleStyle, "night-contrast");
        }

        if (context.BaseThreat)
        {
            layers.Add("perimeter-sweep-bed");
            mixProfile = AppendFacet(mixProfile, "perimeter-alert");
            priority = Math.Max(priority, 82);
        }

        if (anchorDecision.StrategyId == "base-network" && context.KnownBaseCount >= 2)
        {
            layers.Add("known-base-topology");
            mixProfile = AppendFacet(mixProfile, "network-clarity");
        }

        if (context.IsLowMorale)
        {
            layers.Add("steadying-rally-underlay");
            musicMode = AppendFacet(musicMode, "morale-floor");
        }

        if (context.HasNemesisMemory)
        {
            layers.Add("memory-echo-filter");
            stinger = AppendFacet(stinger, "nemesis-echo");
        }

        return new AudioCuePlan
        {
            BehaviorId = baseAudio.BehaviorId,
            Delivery = baseAudio.Delivery,
            VoicePrint = baseAudio.VoicePrint,
            SubtitleStyle = subtitleStyle,
            MusicMode = musicMode,
            Stinger = stinger,
            MixProfile = mixProfile,
            Spatialization = baseAudio.Spatialization,
            Priority = Math.Clamp(priority, 0, 100),
            CooldownMs = cooldownMs,
            Layers = Deduplicate(layers),
        };
    }

    private static VisualCuePlan ApplyVisualOverlays(
        VisualCuePlan baseVisual,
        FallbackBehaviorContext context,
        FallbackBehaviorDecision anchorDecision)
    {
        List<string> layers = [.. baseVisual.Layers];
        string hudAccent = baseVisual.HudAccent;
        string worldMarker = baseVisual.WorldMarker;
        string screenTreatment = baseVisual.ScreenTreatment;
        string cameraTreatment = baseVisual.CameraTreatment;
        string lightCue = baseVisual.LightCue;
        int priority = baseVisual.Priority;
        int holdMs = baseVisual.HoldMs;

        switch (anchorDecision.Phase)
        {
            case FallbackPacingPhase.Relax:
                layers.Add("phase-relax");
                hudAccent = AppendFacet(hudAccent, "warm-rest-glow");
                break;
            case FallbackPacingPhase.BuildUp:
                layers.Add("phase-build-up");
                hudAccent = AppendFacet(hudAccent, "anticipation-chevron");
                screenTreatment = AppendFacet(screenTreatment, "forward-bloom");
                priority = Math.Max(priority, 78);
                break;
            case FallbackPacingPhase.Peak:
                layers.Add("phase-peak");
                hudAccent = AppendFacet(hudAccent, "high-contrast-threat");
                screenTreatment = AppendFacet(screenTreatment, "impact-clarity");
                cameraTreatment = AppendFacet(cameraTreatment, "tight-jolt");
                lightCue = AppendFacet(lightCue, "alarm-spike");
                priority = Math.Max(priority, 90);
                holdMs = Math.Min(holdMs + 300, 4_000);
                break;
            case FallbackPacingPhase.Recover:
                layers.Add("phase-recover");
                hudAccent = AppendFacet(hudAccent, "cool-reset");
                screenTreatment = AppendFacet(screenTreatment, "soft-clear");
                break;
        }

        if (context.HasWeatherRisk)
        {
            layers.Add("weather-visibility-haze");
            screenTreatment = AppendFacet(screenTreatment, "weather-haze");
            lightCue = AppendFacet(lightCue, "storm-shelter-rim");
        }

        if (context.IsNight)
        {
            layers.Add("night-landmark-contrast");
            worldMarker = AppendFacet(worldMarker, "night-landmark");
            screenTreatment = AppendFacet(screenTreatment, "night-contrast");
        }

        if (context.BaseThreat)
        {
            layers.Add("breach-ring-overlay");
            hudAccent = AppendFacet(hudAccent, "defense-front");
            worldMarker = AppendFacet(worldMarker, "breach-front");
        }

        if (anchorDecision.StrategyId == "base-network" && context.KnownBaseCount >= 2)
        {
            layers.Add("supply-network-threads");
            worldMarker = AppendFacet(worldMarker, "known-base-links");
        }

        if (context.IsLowMorale)
        {
            layers.Add("warm-cluster-aura");
            lightCue = AppendFacet(lightCue, "confidence-warmth");
        }

        if (context.HasNemesisMemory)
        {
            layers.Add("rival-accent-echo");
            cameraTreatment = AppendFacet(cameraTreatment, "memory-punch-in");
        }

        return new VisualCuePlan
        {
            BehaviorId = baseVisual.BehaviorId,
            PortraitExpression = baseVisual.PortraitExpression,
            BodyPose = baseVisual.BodyPose,
            HudAccent = hudAccent,
            WorldMarker = worldMarker,
            ScreenTreatment = screenTreatment,
            CameraTreatment = cameraTreatment,
            LightCue = lightCue,
            Emote = baseVisual.Emote,
            Priority = Math.Clamp(priority, 0, 100),
            HoldMs = holdMs,
            Layers = Deduplicate(layers),
        };
    }

    private static string BuildSummary(
        string summary,
        AudioCuePlan audio,
        VisualCuePlan visual,
        bool usedFallback)
    {
        string mode = usedFallback
            ? "The fallback path can drive these cues directly."
            : "These cues can support the live reply without spending another LLM turn.";
        return $"{summary} Audio={audio.BehaviorId}. Visual={visual.BehaviorId}. {mode}";
    }

    private static DeliverySurfacePlan BuildSurface(
        FallbackBehaviorContext context,
        FallbackBehaviorDecision anchorDecision,
        string source,
        bool usedFallback,
        string? assistantMessage,
        AudioCuePlan audio,
        VisualCuePlan visual,
        int actionPriority)
    {
        string familyId = ClassifyStrategyFamily(anchorDecision.StrategyId);
        string layoutMode = BuildLayoutMode(familyId);
        int priority = Math.Max(Math.Max(audio.Priority, visual.Priority), Math.Clamp(actionPriority, 0, 100));
        int holdMs = visual.HoldMs <= 0 ? 2_500 : visual.HoldMs;
        int messageLength = assistantMessage?.Length ?? 0;
        int messageBonusMs = Math.Min(2_800, Math.Max(0, messageLength - 64) * 20);
        int primaryDurationMs = (int)Math.Max(4_500, Math.Min(9_000, holdMs + messageBonusMs));
        if (priority >= 90)
        {
            primaryDurationMs = Math.Max(primaryDurationMs, 6_500);
        }

        int followupDurationMs = (int)Math.Max(3_500, Math.Min(6_000, primaryDurationMs - 1_000));
        int cardBudget = BuildCardBudget(familyId, priority, messageLength);
        int primaryCueTokenCount = BuildPrimaryCueTokenCount(familyId, priority, messageLength);
        int primaryFocusTokenCount = BuildPrimaryFocusTokenCount(familyId, messageLength);
        int primaryStatusTokenCount = BuildPrimaryStatusTokenCount(familyId, priority, messageLength);
        int primaryStageTokenCount = BuildPrimaryStageTokenCount(familyId, cardBudget);
        int primaryAtmosphereTokenCount = BuildPrimaryAtmosphereTokenCount(familyId, cardBudget, priority);

        return new DeliverySurfacePlan
        {
            FamilyId = familyId,
            LayoutMode = layoutMode,
            PathBadge = BuildPathBadge(source, usedFallback),
            FamilyBadge = BuildFamilyBadge(familyId),
            PhaseBadge = BuildPhaseBadge(anchorDecision.Phase),
            PrimaryTitle = BuildPrimaryTitle(familyId),
            CueTitle = BuildCueTitle(familyId),
            ReadoutTitle = BuildReadoutTitle(familyId),
            SupportTitle = BuildSupportTitle(familyId),
            ActionPreviewTitle = BuildActionPreviewTitle(familyId),
            ActionFeedbackTitle = BuildActionFeedbackTitle(familyId),
            HeaderTokens = BuildHeaderTokens(source, usedFallback, familyId, anchorDecision.Phase),
            CueTokens = BuildCueTokens(audio, visual),
            StageTokens = BuildStageTokens(visual),
            AtmosphereTokens = BuildAtmosphereTokens(audio),
            FocusTokens = BuildFocusTokens(context, familyId),
            StatusTokens = BuildStatusTokens(context, familyId),
            FooterTokens = BuildFooterTokens(anchorDecision.StrategyId, audio, visual),
            FollowupOrder = BuildFollowupOrder(familyId, actionPriority),
            CardBudget = cardBudget,
            PrimaryCueTokenCount = primaryCueTokenCount,
            PrimaryFocusTokenCount = primaryFocusTokenCount,
            PrimaryStatusTokenCount = primaryStatusTokenCount,
            PrimaryStageTokenCount = primaryStageTokenCount,
            PrimaryAtmosphereTokenCount = primaryAtmosphereTokenCount,
            WidthChars = BuildWidthChars(layoutMode),
            MaxBodyLines = BuildMaxBodyLines(layoutMode),
            PrimaryDurationMs = primaryDurationMs,
            FollowupDurationMs = followupDurationMs,
        };
    }

    private static IReadOnlyList<string> BuildHeaderTokens(
        string source,
        bool usedFallback,
        string familyId,
        FallbackPacingPhase phase) =>
        Deduplicate(
        [
            BuildPathBadge(source, usedFallback),
            BuildFamilyBadge(familyId),
            BuildPhaseBadge(phase),
        ]);

    private static IReadOnlyList<string> BuildCueTokens(AudioCuePlan audio, VisualCuePlan visual) =>
        Deduplicate(
        [
            BuildDisplayToken("Subtitle", audio.SubtitleStyle),
            BuildDisplayToken("HUD", visual.HudAccent),
            BuildDisplayToken("Screen", visual.ScreenTreatment),
        ]);

    private static IReadOnlyList<string> BuildStageTokens(VisualCuePlan visual) =>
        Deduplicate(
        [
            BuildDisplayToken("Marker", visual.WorldMarker),
            BuildDisplayToken("Portrait", visual.PortraitExpression),
            BuildDisplayToken("Pose", visual.BodyPose),
            BuildDisplayToken("Emote", visual.Emote),
            BuildDisplayToken("Camera", visual.CameraTreatment),
            BuildDisplayToken("Light", visual.LightCue),
        ]);

    private static IReadOnlyList<string> BuildAtmosphereTokens(AudioCuePlan audio) =>
        Deduplicate(
        [
            BuildDisplayToken("Delivery", audio.Delivery),
            BuildDisplayToken("Voice", audio.VoicePrint),
            BuildDisplayToken("Music", audio.MusicMode),
            BuildDisplayToken("Stinger", audio.Stinger),
        ]);

    private static IReadOnlyList<string> BuildFocusTokens(FallbackBehaviorContext context, string familyId)
    {
        string baseToken =
            context.KnownBaseCount >= 2
                ? $"Bases {NormalizeDisplayValue(context.PrimaryBaseLabel)} + {NormalizeDisplayValue(context.SecondaryBaseLabel)}"
                : context.KnownBaseCount == 1
                    ? BuildLiteralToken("Base", context.PrimaryBaseLabel)
                    : context.InBase
                        ? "Base On Site"
                        : string.Empty;

        string productionToken =
            context.HasRecentProductionObservation && !string.IsNullOrWhiteSpace(context.RecentProductionItemLabel)
                ? BuildLiteralToken("Queue", context.RecentProductionItemLabel)
                : string.Empty;

        return familyId switch
        {
            "travel" => Deduplicate(
            [
                !string.IsNullOrWhiteSpace(context.RecentTravelRouteLabel)
                    ? BuildLiteralToken("Route", context.RecentTravelRouteLabel)
                    : BuildLiteralToken("Anchor", context.RecentTravelAnchorLabel),
                BuildDisplayToken("Mode", context.RecentTravelModeLabel),
                context.HasObjective ? BuildLiteralToken("Objective", context.CurrentObjectiveLabel) : string.Empty,
            ]),
            "base" => Deduplicate(
            [
                baseToken,
                productionToken,
                !string.IsNullOrWhiteSpace(context.RecentProductionStationLabel)
                    ? BuildLiteralToken("Station", context.RecentProductionStationLabel)
                    : string.Empty,
            ]),
            "combat" => Deduplicate(
            [
                BuildLiteralToken("Threat", context.FocusThreat),
                context.BaseThreat ? "Perimeter Threat" : string.Empty,
                context.Outnumbered ? "Outnumbered" : string.Empty,
                context.HasObjective ? BuildLiteralToken("Objective", context.CurrentObjectiveLabel) : string.Empty,
            ]),
            "stealth" => Deduplicate(
            [
                BuildLiteralToken("Threat", context.FocusThreat),
                context.IsNight ? "Night Window" : string.Empty,
                context.HasObjective ? BuildLiteralToken("Objective", context.CurrentObjectiveLabel) : string.Empty,
            ]),
            "capture" => Deduplicate(
            [
                context.HasObjective
                    ? BuildLiteralToken("Objective", context.CurrentObjectiveLabel)
                    : BuildLiteralToken("Target", context.FocusThreat),
                context.HasRecentTravelObservation ? BuildLiteralToken("Anchor", context.RecentTravelAnchorLabel) : string.Empty,
                BuildDisplayToken("Mode", context.RecentTravelModeLabel),
            ]),
            "camp" => Deduplicate(
            [
                context.KnownBaseCount > 0
                    ? BuildLiteralToken("Camp", context.PrimaryBaseLabel)
                    : (context.InBase ? "Camp Window" : string.Empty),
                context.IsNight ? "Night Window" : string.Empty,
                BuildResourceToken(context.ResourceHint),
            ]),
            "recovery" => Deduplicate(
            [
                context.InBase && context.KnownBaseCount > 0
                    ? BuildLiteralToken("Base", context.PrimaryBaseLabel)
                    : string.Empty,
                context.HasRecentTravelObservation ? BuildLiteralToken("Anchor", context.RecentTravelAnchorLabel) : string.Empty,
                context.Outnumbered ? "Break Contact" : string.Empty,
            ]),
            _ => Deduplicate(
            [
                context.HasObjective ? BuildLiteralToken("Objective", context.CurrentObjectiveLabel) : string.Empty,
                baseToken,
                context.HasRecentTravelObservation ? BuildLiteralToken("Anchor", context.RecentTravelAnchorLabel) : string.Empty,
            ]),
        };
    }

    private static IReadOnlyList<string> BuildStatusTokens(FallbackBehaviorContext context, string familyId) =>
        familyId switch
        {
            "travel" => Deduplicate(
            [
                BuildPercentToken("Stamina", context.Stamina),
                BuildPercentToken("Health", context.Health),
                context.IsNight ? "Night" : string.Empty,
                context.HasWeatherRisk ? "Weather Risk" : string.Empty,
            ]),
            "combat" => Deduplicate(
            [
                BuildPercentToken("Threat", context.Threat),
                BuildCountToken("Hostiles", context.HostileCount),
                BuildPercentToken("Health", context.Health),
                BuildPercentToken("Morale", context.Morale),
            ]),
            "stealth" => Deduplicate(
            [
                BuildPercentToken("Threat", context.Threat),
                BuildCountToken("Hostiles", context.HostileCount),
                BuildPercentToken("Stamina", context.Stamina),
                context.IsNight ? "Night" : string.Empty,
            ]),
            "base" => Deduplicate(
            [
                BuildPercentToken("Morale", context.Morale),
                BuildPercentToken("Health", context.Health),
                context.IsNight ? "Night" : string.Empty,
                context.HasWeatherRisk ? "Weather Risk" : string.Empty,
            ]),
            "camp" => Deduplicate(
            [
                BuildPercentToken("Morale", context.Morale),
                BuildPercentToken("Health", context.Health),
                context.IsNight ? "Night" : string.Empty,
            ]),
            "recovery" => Deduplicate(
            [
                BuildPercentToken("Health", context.Health),
                BuildPercentToken("Stamina", context.Stamina),
                BuildPercentToken("Morale", context.Morale),
                BuildPercentToken("Threat", context.Threat),
            ]),
            "capture" => Deduplicate(
            [
                BuildPercentToken("Threat", context.Threat),
                BuildPercentToken("Stamina", context.Stamina),
                BuildPercentToken("Health", context.Health),
                BuildCountToken("Hostiles", context.HostileCount),
            ]),
            _ => Deduplicate(
            [
                BuildPercentToken("Health", context.Health),
                BuildPercentToken("Morale", context.Morale),
                BuildPercentToken("Threat", context.Threat),
            ]),
        };

    private static IReadOnlyList<string> BuildFooterTokens(
        string strategyId,
        AudioCuePlan audio,
        VisualCuePlan visual) =>
        Deduplicate(
        [
            HumanizeIdentifier(strategyId),
            BuildDisplayToken("Voice", audio.VoicePrint),
            BuildDisplayToken("Marker", visual.WorldMarker),
        ]);

    private static string BuildLayoutMode(string familyId) =>
        familyId switch
        {
            "stealth" => "stealth_whisper",
            "combat" => "combat_alert",
            "base" => "operations_panel",
            "travel" => "route_strip",
            "capture" => "capture_focus",
            "camp" => "camp_banner",
            "recovery" => "recovery_breath",
            _ => "guide_panel",
        };

    private static int BuildWidthChars(string layoutMode) =>
        layoutMode switch
        {
            "stealth_whisper" => 46,
            "combat_alert" => 50,
            "capture_focus" => 50,
            "operations_panel" => 58,
            "route_strip" => 56,
            "camp_banner" => 58,
            "recovery_breath" => 56,
            _ => 54,
        };

    private static int BuildMaxBodyLines(string layoutMode) =>
        layoutMode switch
        {
            "stealth_whisper" => 4,
            "operations_panel" => 4,
            "route_strip" => 4,
            "camp_banner" => 4,
            "recovery_breath" => 4,
            _ => 3,
        };

    private static int BuildCardBudget(string familyId, int priority, int messageLength)
    {
        int budget = priority >= 85 ? 3 : 2;

        if (messageLength >= 220)
        {
            budget = Math.Min(budget, 2);
        }

        if (familyId is "stealth" or "travel" or "recovery")
        {
            budget = Math.Min(budget, 2);
        }

        if (familyId is "camp" or "general" && priority < 45 && messageLength >= 170)
        {
            budget = 1;
        }

        return Math.Clamp(budget, 1, 3);
    }

    private static int BuildPrimaryCueTokenCount(string familyId, int priority, int messageLength)
    {
        int count = familyId switch
        {
            "travel" or "base" => 2,
            _ => 1,
        };

        if (priority >= 90 || messageLength >= 200)
        {
            count = Math.Min(count, 1);
        }

        return Math.Clamp(count, 0, 2);
    }

    private static int BuildPrimaryFocusTokenCount(string familyId, int messageLength)
    {
        int count = familyId switch
        {
            "travel" or "base" or "combat" or "capture" => 2,
            _ => 1,
        };

        if (messageLength >= 220)
        {
            count = Math.Min(count, 1);
        }

        return Math.Clamp(count, 0, 2);
    }

    private static int BuildPrimaryStatusTokenCount(string familyId, int priority, int messageLength)
    {
        int count = familyId switch
        {
            "combat" or "capture" or "recovery" => 2,
            "stealth" or "base" or "camp" => 1,
            "travel" when priority >= 90 => 1,
            _ => 0,
        };

        if (messageLength >= 220)
        {
            count = Math.Min(count, 1);
        }

        return Math.Clamp(count, 0, 2);
    }

    private static int BuildPrimaryStageTokenCount(string familyId, int cardBudget) =>
        familyId switch
        {
            "combat" or "capture" or "stealth" or "travel" => 1,
            "base" or "camp" or "recovery" when cardBudget <= 1 => 1,
            _ => 0,
        };

    private static int BuildPrimaryAtmosphereTokenCount(string familyId, int cardBudget, int priority) =>
        familyId switch
        {
            "combat" or "capture" or "stealth" or "travel" or "camp" or "recovery" => 1,
            "base" when cardBudget <= 1 || priority >= 75 => 1,
            _ => 0,
        };

    private static string ClassifyStrategyFamily(string strategyId)
    {
        string normalized = strategyId?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized switch
        {
            "stealth-shadow" => "stealth",
            "hero-moment" or "emergency-triage" or "retreat-and-rally" or "nemesis-counterplay" or "buddy-overwatch" => "combat",
            "perimeter-lockdown" or "base-network" or "crafting-discipline" => "base",
            "safe-travel" or "objective-push" or "exploration-sweep" or "weather-shelter" => "travel",
            "capture-window" => "capture",
            "ambient-camp" or "harvest-window" => "camp",
            "morale-rally" or "recover-window" => "recovery",
            _ => "general",
        };
    }

    private static string BuildPathBadge(string source, bool usedFallback)
    {
        if (usedFallback)
        {
            return "FALLBACK";
        }

        string normalized = source?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Contains("bypass", StringComparison.Ordinal))
        {
            return "FAST";
        }

        if (normalized.Contains("live", StringComparison.Ordinal))
        {
            return "LIVE";
        }

        return normalized.Length > 0 ? "SIDECAR" : "PAL";
    }

    private static string BuildFamilyBadge(string familyId) =>
        familyId switch
        {
            "stealth" => "STEALTH",
            "combat" => "ALERT",
            "base" => "BASE",
            "travel" => "ROUTE",
            "capture" => "CAPTURE",
            "camp" => "CAMP",
            "recovery" => "RESET",
            _ => "GUIDE",
        };

    private static string BuildPhaseBadge(FallbackPacingPhase phase) =>
        phase switch
        {
            FallbackPacingPhase.Peak => "PEAK",
            FallbackPacingPhase.BuildUp => "BUILD",
            FallbackPacingPhase.Recover => "RECOVER",
            FallbackPacingPhase.Relax => "RELAX",
            _ => "FLOW",
        };

    private static string BuildPrimaryTitle(string familyId) =>
        familyId switch
        {
            "stealth" => "[[ STEALTH THREAD ]]",
            "combat" => "!! ALERT VECTOR !!",
            "base" => "== OPERATIONS PANEL ==",
            "travel" => "--> ROUTE THREAD -->",
            "capture" => "<> CAPTURE WINDOW <>",
            "camp" => "~~ CAMP WATCH ~~",
            "recovery" => "++ RESET WINDOW ++",
            _ => "[[ FIELD GUIDE ]]",
        };

    private static string BuildCueTitle(string familyId) =>
        familyId switch
        {
            "stealth" => "[Shadow Cues]",
            "combat" => "[Threat Cues]",
            "base" => "[Operations Cues]",
            "travel" => "[Route Cues]",
            "capture" => "[Capture Cues]",
            "camp" => "[Camp Cues]",
            "recovery" => "[Recovery Cues]",
            _ => "[Guide Cues]",
        };

    private static string BuildReadoutTitle(string familyId) =>
        familyId switch
        {
            "stealth" => "[Quiet Readout]",
            "combat" => "[Threat Readout]",
            "base" => "[Operations Readout]",
            "travel" => "[Route Readout]",
            "capture" => "[Capture Readout]",
            "camp" => "[Camp Readout]",
            "recovery" => "[Recovery Readout]",
            _ => "[Field Readout]",
        };

    private static string BuildSupportTitle(string familyId) =>
        familyId switch
        {
            "stealth" => "[Quiet Suggestion]",
            "combat" => "[Immediate Suggestion]",
            "base" => "[Task Suggestion]",
            "travel" => "[Route Suggestion]",
            "capture" => "[Capture Suggestion]",
            "camp" => "[Camp Suggestion]",
            "recovery" => "[Recovery Suggestion]",
            _ => "[Guide Suggestion]",
        };

    private static string BuildActionPreviewTitle(string familyId) =>
        familyId switch
        {
            "stealth" => "[Quiet Action]",
            "combat" => "[Immediate Action]",
            "base" => "[Operations Action]",
            "travel" => "[Route Action]",
            "capture" => "[Capture Action]",
            "camp" => "[Camp Action]",
            "recovery" => "[Recovery Action]",
            _ => "[Guide Action]",
        };

    private static string BuildActionFeedbackTitle(string familyId) =>
        familyId switch
        {
            "stealth" => "[Quiet Result]",
            "combat" => "[Immediate Result]",
            "base" => "[Operations Result]",
            "travel" => "[Route Result]",
            "capture" => "[Capture Result]",
            "camp" => "[Camp Result]",
            "recovery" => "[Recovery Result]",
            _ => "[Guide Result]",
        };

    private static IReadOnlyList<string> BuildFollowupOrder(string familyId, int actionPriority)
    {
        if (actionPriority >= 80)
        {
            return ["support", "readout", "cue"];
        }

        return familyId switch
        {
            "travel" or "base" or "recovery" => ["readout", "support", "cue"],
            "combat" or "capture" or "stealth" or "camp" => ["readout", "cue", "support"],
            _ => ["cue", "readout", "support"],
        };
    }

    private static string BuildDisplayToken(string label, string? value)
    {
        string humanized = HumanizeIdentifier(value);
        return string.IsNullOrWhiteSpace(humanized)
            ? string.Empty
            : $"{label} {humanized}";
    }

    private static string BuildLiteralToken(string label, string? value)
    {
        string normalized = NormalizeDisplayValue(value);
        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : $"{label} {normalized}";
    }

    private static string BuildPercentToken(string label, float value)
    {
        int percentage = (int)Math.Round(Math.Clamp(value, 0f, 1f) * 100f);
        return $"{label} {percentage}%";
    }

    private static string BuildCountToken(string label, int count) =>
        count > 0 ? $"{label} {count}" : string.Empty;

    private static string BuildResourceToken(string? resourceHint)
    {
        string normalized = NormalizeDisplayValue(resourceHint);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        normalized = normalized
            .Replace("near ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        return string.IsNullOrWhiteSpace(normalized)
            ? string.Empty
            : $"Resource {normalized}";
    }

    private static string HumanizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value
            .Trim()
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Replace('+', ' ');

        string[] parts = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int index = 0; index < parts.Length; index++)
        {
            string part = parts[index];
            if (part.Length == 0)
            {
                continue;
            }

            parts[index] = part.Length == 1
                ? char.ToUpperInvariant(part[0]).ToString()
                : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant();
        }

        return string.Join(' ', parts);
    }

    private static string NormalizeDisplayValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string[] parts = value
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return string.Join(' ', parts);
    }

    private static string AppendFacet(string current, string facet)
    {
        if (string.IsNullOrWhiteSpace(facet))
        {
            return current;
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            return facet;
        }

        return current.Contains(facet, StringComparison.OrdinalIgnoreCase)
            ? current
            : $"{current}+{facet}";
    }

    private static IReadOnlyList<string> Deduplicate(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// Canonical per-strategy cue attributes. One entry per strategy collapses what used to be 18
    /// separate switch expressions, so adding or tuning a strategy now only needs to touch this table.
    private sealed record StrategyProfile(
        string Summary,
        string AudioBehavior,
        string VisualBehavior,
        int Priority,
        int CooldownMs,
        int HoldMs,
        string Delivery,
        string VoicePrint,
        string SubtitleStyle,
        string MusicMode,
        string Stinger,
        string MixProfile,
        string Spatialization,
        string PortraitExpression,
        string BodyPose,
        string HudAccent,
        string WorldMarker,
        string ScreenTreatment,
        string CameraTreatment,
        string LightCue,
        string Emote,
        IReadOnlyList<string> AudioLayers,
        IReadOnlyList<string> VisualLayers)
    {
        public AudioCuePlan BuildAudio() =>
            new()
            {
                BehaviorId = AudioBehavior,
                Delivery = Delivery,
                VoicePrint = VoicePrint,
                SubtitleStyle = SubtitleStyle,
                MusicMode = MusicMode,
                Stinger = Stinger,
                MixProfile = MixProfile,
                Spatialization = Spatialization,
                Priority = Priority,
                CooldownMs = CooldownMs,
                Layers = AudioLayers,
            };

        public VisualCuePlan BuildVisual() =>
            new()
            {
                BehaviorId = VisualBehavior,
                PortraitExpression = PortraitExpression,
                BodyPose = BodyPose,
                HudAccent = HudAccent,
                WorldMarker = WorldMarker,
                ScreenTreatment = ScreenTreatment,
                CameraTreatment = CameraTreatment,
                LightCue = LightCue,
                Emote = Emote,
                Priority = Priority,
                HoldMs = HoldMs,
                Layers = VisualLayers,
            };

        public static StrategyProfile Lookup(string strategyId) =>
            Profiles.TryGetValue(strategyId, out StrategyProfile? profile) ? profile : Default;

        private static readonly StrategyProfile Default = new(
            Summary: "General-purpose deterministic cues that keep context readable across mood shifts.",
            AudioBehavior: "director-guidance-bed",
            VisualBehavior: "director-context-frame",
            Priority: 70,
            CooldownMs: 7_500,
            HoldMs: 2_500,
            Delivery: "support-callout",
            VoicePrint: "steady-guide",
            SubtitleStyle: "default-caption",
            MusicMode: "adaptive-bed",
            Stinger: "director-tick",
            MixProfile: "balanced-clarity",
            Spatialization: "front-center",
            PortraitExpression: "focused-neutral",
            BodyPose: "ready-stance",
            HudAccent: "context-ring",
            WorldMarker: "context-glyph",
            ScreenTreatment: "subtle-context-frame",
            CameraTreatment: "smart-shoulder",
            LightCue: "context-rim",
            Emote: "ready",
            AudioLayers: ["general", "fallback", "contextual"],
            VisualLayers: ["general", "context", "director"]);

        private static readonly IReadOnlyDictionary<string, StrategyProfile> Profiles = new Dictionary<string, StrategyProfile>(StringComparer.Ordinal)
        {
            ["hero-moment"] = new(
                Summary: "Rare rescue cues that keep support readable without stealing the scene.",
                AudioBehavior: "hero-rescue-stinger",
                VisualBehavior: "hero-focus-flash",
                Priority: 100,
                CooldownMs: 45_000,
                HoldMs: 2_200,
                Delivery: "support-callout",
                VoicePrint: "steady-protector",
                SubtitleStyle: "default-caption",
                MusicMode: "hero-surge",
                Stinger: "rescue-commit",
                MixProfile: "focus-ducking",
                Spatialization: "front-center",
                PortraitExpression: "determined",
                BodyPose: "intercept-lunge",
                HudAccent: "gold-guard-arc",
                WorldMarker: "rescue-intercept-pin",
                ScreenTreatment: "focus-flare",
                CameraTreatment: "shoulder-tighten",
                LightCue: "warm-rescue-rim",
                Emote: "protective-step",
                AudioLayers: ["rare_support", "rescue_window", "player-first"],
                VisualLayers: ["rare-heroic", "ally-commit", "readable-save"]),

            ["emergency-triage"] = new(
                Summary: "Hard triage cues that push attention onto the nearest solvable danger.",
                AudioBehavior: "triage-command-bark",
                VisualBehavior: "threat-edge-pulse",
                Priority: 96,
                CooldownMs: 7_500,
                HoldMs: 1_600,
                Delivery: "combat-command",
                VoicePrint: "clipped-directive",
                SubtitleStyle: "high-legibility",
                MusicMode: "combat-surge",
                Stinger: "threat-hard-stop",
                MixProfile: "threat-ducking",
                Spatialization: "front-center",
                PortraitExpression: "laser-focus",
                BodyPose: "cover-brace",
                HudAccent: "red-threat-border",
                WorldMarker: "priority-hostile-bracket",
                ScreenTreatment: "edge-pulse",
                CameraTreatment: "impact-jolt",
                LightCue: "alarm-red-rim",
                Emote: "hard-point",
                AudioLayers: ["closest-threat", "triage", "pressure"],
                VisualLayers: ["peak-threat", "solve-first", "readable-danger"]),

            ["retreat-and-rally"] = new(
                Summary: "Regroup cues that make shrinking the plan feel intentional instead of panicked.",
                AudioBehavior: "regroup-rally-shout",
                VisualBehavior: "fallback-arrow-stack",
                Priority: 91,
                CooldownMs: 8_500,
                HoldMs: 2_100,
                Delivery: "combat-command",
                VoicePrint: "grounded-rally",
                SubtitleStyle: "default-caption",
                MusicMode: "fallback-drum",
                Stinger: "regroup-hit",
                MixProfile: "balanced-clarity",
                Spatialization: "front-center",
                PortraitExpression: "focused-concern",
                BodyPose: "pull-back-sweep",
                HudAccent: "amber-regroup-arrows",
                WorldMarker: "rally-anchor-stack",
                ScreenTreatment: "retreat-vignette",
                CameraTreatment: "pullback-widen",
                LightCue: "ember-guide-rim",
                Emote: "beckon-back",
                AudioLayers: ["fallback", "spacing", "confidence-reset"],
                VisualLayers: ["regroup", "support-radius", "safe-anchor"]),

            ["stealth-shadow"] = new(
                Summary: "Quiet stealth-support cues that preserve trust and local awareness.",
                AudioBehavior: "stealth-whisper-callout",
                VisualBehavior: "stealth-silhouette-ping",
                Priority: 88,
                CooldownMs: 7_500,
                HoldMs: 2_400,
                Delivery: "precision-whisper",
                VoicePrint: "hushed-spotter",
                SubtitleStyle: "small-whisper-caption",
                MusicMode: "stealth-tension-pulse",
                Stinger: "hush-tick",
                MixProfile: "whisper-front",
                Spatialization: "nearby-left-right",
                PortraitExpression: "narrowed-eyes",
                BodyPose: "low-crouch-guide",
                HudAccent: "teal-silence-line",
                WorldMarker: "silhouette-ping",
                ScreenTreatment: "soft-shadow-vignette",
                CameraTreatment: "low-shoulder-follow",
                LightCue: "cool-silhouette-rim",
                Emote: "two-finger-hush",
                AudioLayers: ["stealth", "trust", "same-side"],
                VisualLayers: ["stealth-safe", "local-awareness", "minimal-noise"]),

            ["nemesis-counterplay"] = new(
                Summary: "Memory-echo cues that frame the moment as a rematch without overplaying it.",
                AudioBehavior: "nemesis-memory-taunt",
                VisualBehavior: "rival-accent-echo",
                Priority: 87,
                CooldownMs: 7_500,
                HoldMs: 2_000,
                Delivery: "support-callout",
                VoicePrint: "dry-memory-echo",
                SubtitleStyle: "default-caption",
                MusicMode: "rival-thread",
                Stinger: "echo-sting",
                MixProfile: "balanced-clarity",
                Spatialization: "front-center",
                PortraitExpression: "grim-recognition",
                BodyPose: "measured-lean",
                HudAccent: "crimson-memory-trace",
                WorldMarker: "repeat-threat-glyph",
                ScreenTreatment: "memory-echo-flash",
                CameraTreatment: "duel-punch-in",
                LightCue: "rival-rim",
                Emote: "point-and-track",
                AudioLayers: ["memory", "rivalry", "counterplay"],
                VisualLayers: ["rematch", "pattern-memory", "revenge-loop"]),

            ["buddy-overwatch"] = new(
                Summary: "Buddy-angle cues that keep help visible and player-first.",
                AudioBehavior: "overwatch-side-callout",
                VisualBehavior: "support-lane-arc",
                Priority: 84,
                CooldownMs: 7_500,
                HoldMs: 1_900,
                Delivery: "support-callout",
                VoicePrint: "steady-wingmate",
                SubtitleStyle: "support-caption",
                MusicMode: "support-lane-pulse",
                Stinger: "side-lane-hit",
                MixProfile: "balanced-clarity",
                Spatialization: "off-shoulder",
                PortraitExpression: "confident-support",
                BodyPose: "side-guard-stance",
                HudAccent: "cyan-side-arc",
                WorldMarker: "support-lane-chevron",
                ScreenTreatment: "support-focus",
                CameraTreatment: "shoulder-pair-follow",
                LightCue: "ally-rim",
                Emote: "cover-point",
                AudioLayers: ["buddy-ai", "support-angle", "player-visible"],
                VisualLayers: ["buddy-support", "same-room", "visible-help"]),

            ["perimeter-lockdown"] = new(
                Summary: "Base-defense cues that turn pressure into one readable defensive front.",
                AudioBehavior: "perimeter-sweep-alarm",
                VisualBehavior: "breach-scan-ring",
                Priority: 90,
                CooldownMs: 8_500,
                HoldMs: 2_300,
                Delivery: "combat-command",
                VoicePrint: "firm-sentry",
                SubtitleStyle: "default-caption",
                MusicMode: "base-alert-grid",
                Stinger: "breach-tone",
                MixProfile: "alarm-priority-ducking",
                Spatialization: "perimeter-ring",
                PortraitExpression: "alert-guard",
                BodyPose: "brace-and-direct",
                HudAccent: "orange-defense-ring",
                WorldMarker: "breach-scan-ring",
                ScreenTreatment: "base-alert-sweep",
                CameraTreatment: "stabilize-pan",
                LightCue: "perimeter-beacon",
                Emote: "hold-line",
                AudioLayers: ["base-defense", "breach", "frontline"],
                VisualLayers: ["base-threat", "protect-workers", "nearest-breach"]),

            ["base-network"] = new(
                Summary: "Base-network cues that make specialization and supply loops legible at a glance.",
                AudioBehavior: "base-logistics-call",
                VisualBehavior: "base-network-flow",
                Priority: 83,
                CooldownMs: 9_500,
                HoldMs: 3_200,
                Delivery: "logistics-briefing",
                VoicePrint: "quartermaster-guide",
                SubtitleStyle: "logistics-caption",
                MusicMode: "base-network-bed",
                Stinger: "supply-route-chime",
                MixProfile: "operations-clarity",
                Spatialization: "base-to-base-pan",
                PortraitExpression: "measured-command",
                BodyPose: "map-and-point",
                HudAccent: "amber-network-thread",
                WorldMarker: "base-link-threads",
                ScreenTreatment: "supply-route-clarity",
                CameraTreatment: "map-pan",
                LightCue: "hearth-to-outpost-rim",
                Emote: "split-assignments",
                AudioLayers: ["base-logistics", "specialization", "transfer-loop"],
                VisualLayers: ["base-network", "specialized-bases", "supply-thread"]),

            ["safe-travel"] = new(
                Summary: "Traversal cues that keep routes anchor-to-anchor and recoverable.",
                AudioBehavior: "route-guide-murmur",
                VisualBehavior: "breadcrumb-anchor-trail",
                Priority: 78,
                CooldownMs: 7_500,
                HoldMs: 3_000,
                Delivery: "route-guidance",
                VoicePrint: "quiet-guide",
                SubtitleStyle: "route-caption",
                MusicMode: "travel-breath",
                Stinger: "waypoint-tick",
                MixProfile: "open-world-bed",
                Spatialization: "path-ahead",
                PortraitExpression: "focused-calm",
                BodyPose: "forward-point",
                HudAccent: "green-route-thread",
                WorldMarker: "anchor-trail",
                ScreenTreatment: "path-clarity",
                CameraTreatment: "look-ahead-drift",
                LightCue: "route-lantern-rim",
                Emote: "lead-on",
                AudioLayers: ["travel", "terrain", "anchor-route"],
                VisualLayers: ["safe-route", "terrain-anchor", "return-lane"]),

            ["capture-window"] = new(
                Summary: "Capture-readiness cues that shift from damage to discipline when the window opens.",
                AudioBehavior: "capture-window-hush",
                VisualBehavior: "weakness-ring",
                Priority: 82,
                CooldownMs: 10_000,
                HoldMs: 2_600,
                Delivery: "precision-whisper",
                VoicePrint: "calm-handler",
                SubtitleStyle: "capture-caption",
                MusicMode: "low-hold-tension",
                Stinger: "window-open-tick",
                MixProfile: "precision-focus",
                Spatialization: "front-center",
                PortraitExpression: "controlled-focus",
                BodyPose: "hold-and-aim",
                HudAccent: "violet-capture-ring",
                WorldMarker: "capture-window-ring",
                ScreenTreatment: "target-isolate",
                CameraTreatment: "gentle-lock-on",
                LightCue: "containment-rim",
                Emote: "steady-hand",
                AudioLayers: ["capture", "discipline", "clean-attempt"],
                VisualLayers: ["weakness-read", "stop-stray-damage", "clean-catch"]),

            ["objective-push"] = new(
                Summary: "Buildup cues that make the next objective feel staged, not rushed.",
                AudioBehavior: "briefing-push-cadence",
                VisualBehavior: "objective-chevron-bloom",
                Priority: 80,
                CooldownMs: 7_500,
                HoldMs: 2_200,
                Delivery: "briefing-call",
                VoicePrint: "purposeful-lead",
                SubtitleStyle: "default-caption",
                MusicMode: "forward-anticipation",
                Stinger: "push-cue",
                MixProfile: "balanced-clarity",
                Spatialization: "front-center",
                PortraitExpression: "intent-focus",
                BodyPose: "ready-lean",
                HudAccent: "amber-chevron-bloom",
                WorldMarker: "objective-lane",
                ScreenTreatment: "forward-glow",
                CameraTreatment: "intent-tilt",
                LightCue: "staging-rim",
                Emote: "move-out",
                AudioLayers: ["objective", "buildup", "staging"],
                VisualLayers: ["objective-push", "stage-then-commit", "clear-entry"]),

            ["crafting-discipline"] = new(
                Summary: "Maintenance cues that emphasize one useful bottleneck fix at a time.",
                AudioBehavior: "workbench-checklist-chime",
                VisualBehavior: "maintenance-highlight",
                Priority: 74,
                CooldownMs: 9_000,
                HoldMs: 3_100,
                Delivery: "task-checklist",
                VoicePrint: "practical-planner",
                SubtitleStyle: "work-caption",
                MusicMode: "light-maintenance-bed",
                Stinger: "checklist-chime",
                MixProfile: "balanced-clarity",
                Spatialization: "front-center",
                PortraitExpression: "measured-focus",
                BodyPose: "tool-ready",
                HudAccent: "warm-workbench-glow",
                WorldMarker: "maintenance-outline",
                ScreenTreatment: "subtle-task-highlight",
                CameraTreatment: "steady-bench-frame",
                LightCue: "forge-warmth",
                Emote: "tool-check",
                AudioLayers: ["maintenance", "bottleneck", "parallel-chores"],
                VisualLayers: ["crafting", "maintenance", "keep-responsive"]),

            ["harvest-window"] = new(
                Summary: "Resource-loop cues that keep harvest runs short, banked, and recoverable.",
                AudioBehavior: "resource-loop-hum",
                VisualBehavior: "resource-shimmer-path",
                Priority: 73,
                CooldownMs: 8_500,
                HoldMs: 2_800,
                Delivery: "gathering-call",
                VoicePrint: "easygoing-harvester",
                SubtitleStyle: "default-caption",
                MusicMode: "resource-loop-bed",
                Stinger: "haul-tick",
                MixProfile: "balanced-clarity",
                Spatialization: "front-center",
                PortraitExpression: "light-focus",
                BodyPose: "collect-and-return",
                HudAccent: "green-resource-shimmer",
                WorldMarker: "bank-route-thread",
                ScreenTreatment: "resource-shimmer",
                CameraTreatment: "sweep-and-return",
                LightCue: "harvest-rim",
                Emote: "gather-signal",
                AudioLayers: ["gathering", "safe-loop", "bank-early"],
                VisualLayers: ["harvest", "return-lane", "support-range"]),

            ["weather-shelter"] = new(
                Summary: "Weather-risk cues that treat visibility and footing as enemies too.",
                AudioBehavior: "weather-muffle-warning",
                VisualBehavior: "shelter-desaturation",
                Priority: 79,
                CooldownMs: 11_000,
                HoldMs: 2_500,
                Delivery: "weather-warning",
                VoicePrint: "weathered-guide",
                SubtitleStyle: "weather-caption",
                MusicMode: "storm-muffle-bed",
                Stinger: "shelter-tone",
                MixProfile: "weather-occlusion",
                Spatialization: "wide-environment",
                PortraitExpression: "weather-wary",
                BodyPose: "tighten-up",
                HudAccent: "slate-shelter-band",
                WorldMarker: "shelter-marker",
                ScreenTreatment: "weather-desaturation",
                CameraTreatment: "shelter-seek-pan",
                LightCue: "storm-shelter-rim",
                Emote: "brace-weather",
                AudioLayers: ["weather", "visibility", "foothold"],
                VisualLayers: ["weather-risk", "visibility-tax", "anchor-to-anchor"]),

            ["exploration-sweep"] = new(
                Summary: "Search cues that stay honest to local information and landmarks.",
                AudioBehavior: "search-question-callout",
                VisualBehavior: "scan-cone",
                Priority: 76,
                CooldownMs: 7_500,
                HoldMs: 2_700,
                Delivery: "search-callout",
                VoicePrint: "curious-scout",
                SubtitleStyle: "default-caption",
                MusicMode: "search-suspense-bed",
                Stinger: "scan-tick",
                MixProfile: "balanced-clarity",
                Spatialization: "forward-cone",
                PortraitExpression: "curious-focus",
                BodyPose: "lean-and-check",
                HudAccent: "blue-search-cone",
                WorldMarker: "scan-cone",
                ScreenTreatment: "fog-of-war-clarity",
                CameraTreatment: "slow-scan",
                LightCue: "cool-search-rim",
                Emote: "look-here",
                AudioLayers: ["search", "local-sensing", "fairness"],
                VisualLayers: ["exploration", "local-information", "re-anchor"]),

            ["morale-rally"] = new(
                Summary: "Confidence-repair cues that make simpler jobs feel stabilizing.",
                AudioBehavior: "steadying-rally",
                VisualBehavior: "warm-cluster-aura",
                Priority: 77,
                CooldownMs: 9_000,
                HoldMs: 2_900,
                Delivery: "reassurance-call",
                VoicePrint: "steady-reassurer",
                SubtitleStyle: "support-caption",
                MusicMode: "confidence-floor",
                Stinger: "rally-lift",
                MixProfile: "balanced-clarity",
                Spatialization: "front-center",
                PortraitExpression: "supportive-focus",
                BodyPose: "gather-close",
                HudAccent: "warm-cluster-band",
                WorldMarker: "support-radius",
                ScreenTreatment: "confidence-warmth",
                CameraTreatment: "group-settle",
                LightCue: "hearth-rim",
                Emote: "steady-up",
                AudioLayers: ["morale", "confidence", "simple-jobs"],
                VisualLayers: ["morale", "support-bubble", "steady-team"]),

            ["recover-window"] = new(
                Summary: "Recovery cues that convert the lull into readiness instead of greed.",
                AudioBehavior: "exhale-reset",
                VisualBehavior: "cooldown-blue-ease",
                Priority: 72,
                CooldownMs: 7_500,
                HoldMs: 3_100,
                Delivery: "recovery-breath",
                VoicePrint: "calm-reset",
                SubtitleStyle: "default-caption",
                MusicMode: "recovery-breath-bed",
                Stinger: "reset-chime",
                MixProfile: "balanced-clarity",
                Spatialization: "front-center",
                PortraitExpression: "measured-relief",
                BodyPose: "reset-stance",
                HudAccent: "blue-reset-band",
                WorldMarker: "recovery-anchor",
                ScreenTreatment: "cool-blue-ease",
                CameraTreatment: "breathing-widen",
                LightCue: "cool-rim",
                Emote: "take-a-beat",
                AudioLayers: ["recover", "reset", "no-greed"],
                VisualLayers: ["recovery", "reload-heal", "reset-clean"]),

            ["ambient-camp"] = new(
                Summary: "Ambient-life cues that make downtime feel lived in while keeping a soft watch alive.",
                AudioBehavior: "camp-banter-bed",
                VisualBehavior: "idle-cozy-gestures",
                Priority: 68,
                CooldownMs: 12_000,
                HoldMs: 3_600,
                Delivery: "ambient-banter",
                VoicePrint: "cozy-companion",
                SubtitleStyle: "ambient-caption",
                MusicMode: "campfire-bed",
                Stinger: "soft-clink",
                MixProfile: "wide-dynamic",
                Spatialization: "camp-around-you",
                PortraitExpression: "soft-relief",
                BodyPose: "small-idle-chore",
                HudAccent: "ember-comfort-glow",
                WorldMarker: "lazy-perimeter-pips",
                ScreenTreatment: "campfire-soften",
                CameraTreatment: "gentle-handheld",
                LightCue: "ember-rim",
                Emote: "idle-comfort",
                AudioLayers: ["ambient", "camp-life", "parallel-behaviors"],
                VisualLayers: ["ambient-life", "downtime", "soft-watch"]),
        };
    }
}
