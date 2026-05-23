using System.Text;
using System.Text.RegularExpressions;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Packs;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    The shared input bag for every Try_<Name> fallback strategy.
//            Captures the player's current message, recent memory window,
//            world snapshot, character / pal cues, plus signal scores
//            (urgency, mood, intent kind). Each strategy reads this context
//            and decides whether to fire.
//   surface: FallbackBehaviorContext (record); FallbackBehaviorContextBuilder
//            (constructs one from a chat request + runtime state).
//   gate:    None directly; behaviour pinned by FallbackBehaviorEngineTests
//            and the per-strategy tests.
//   adr:     0001-deterministic-first-reply-pipeline.md (the strategy
//            pattern is the load-bearing part of this ADR).
//   docs:    docs/FALLBACK_AI_RESEARCH.md (research backing the 19
//            strategies), docs/PROMPT_CARDS.md (player-facing scenario
//            cards), docs/COOKBOOK.md ("add a new fallback strategy").
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

internal sealed class FallbackBehaviorContext
{
    private const float DefaultHealth = 0.7f;
    private const float DefaultStamina = 0.7f;
    private const float DefaultHunger = 0.6f;
    private const float DefaultMorale = 0.55f;

    // Word-boundary regex matching. Substring matching produced false positives on common
    // English words (e.g. "raid" inside "afraid", "again" inside "against", "move" inside
    // "remove", "plan" inside "plant"). Phrases are still matched because \b anchors at
    // the start and end of the phrase.
    private static readonly Regex StealthPattern = BuildWordPattern(
        "stealth", "sneak", "quiet", "silent", "hide", "avoid detection");

    private static readonly Regex TravelPattern = BuildWordPattern(
        "travel", "route", "path", "journey", "move", "cross", "climb", "escort", "get there");

    private static readonly Regex ExplorePattern = BuildWordPattern(
        "explore", "search", "sweep", "look for", "scout", "check ahead");

    private static readonly Regex FightPattern = BuildWordPattern(
        "fight", "attack", "combat", "push", "defend", "raid", "enemy", "enemies", "hostile", "hostiles");

    private static readonly Regex BuildPattern = BuildWordPattern(
        "build", "repair", "craft", "upgrade", "prepare camp", "fortify", "prepare this camp");

    private static readonly Regex HarvestPattern = BuildWordPattern(
        "gather", "harvest", "mine", "chop", "farm", "collect", "resource", "resources");

    private static readonly Regex LogisticsPattern = BuildWordPattern(
        "bases", "between bases", "between our bases", "split work", "split jobs", "split tasks",
        "production base", "which base", "outpost", "supply line", "logistics", "transfer",
        "move supplies", "specialize", "specialise", "breeding base", "mining base",
        "smelting base", "factory base");

    private static readonly Regex CapturePattern = BuildWordPattern(
        "capture", "catch", "tame", "sphere", "spheres");

    private static readonly Regex RescuePattern = BuildWordPattern(
        "save", "rescue", "cover me", "buy me time");

    private static readonly Regex PlanningPattern = BuildWordPattern(
        "what now", "plan", "next move", "what should", "how should");

    private static readonly Regex CreativePattern = BuildWordPattern(
        "story", "stories", "roleplay", "dialogue", "backstory", "personality",
        "narrate", "write", "invent", "improvise");

    private static readonly Regex ReasoningPattern = BuildWordPattern(
        "why", "explain", "compare", "analysis", "analyze", "pros and cons", "reason");

    private static readonly Regex CampSignalPattern = BuildWordPattern(
        "camp", "base", "bases", "perimeter", "workbench", "forge");

    private static readonly Regex NightWindowPattern = BuildWordPattern(
        "night", "prepare", "camp");

    private static readonly Regex ReactiveTravelPattern = BuildWordPattern(
        "move", "go");

    private static readonly Regex CampRestPattern = BuildWordPattern(
        "camp", "rest", "night");

    private static readonly Regex WeatherRiskPattern = BuildWordPattern(
        "storm", "rain", "fog", "snow", "sand", "ash");

    private static readonly Regex NightPhrasePattern = BuildWordPattern(
        "night", "evening", "dusk");

    private static readonly Regex ThreatEventPattern = BuildWordPattern(
        "raid", "attack", "ambush", "breach", "combat");

    private static readonly Regex LossMemoryPattern = BuildWordPattern(
        "downed", "wiped", "lost", "killed", "overextended");

    private static readonly Regex AmbushMemoryPattern = BuildWordPattern(
        "ambush", "trap", "flanked", "surprised");

    // Deliberately omits the bare word "again" - it substring-collides with "against"
    // and rarely adds signal in isolation. Rivalry cues are carried by the specific words.
    private static readonly Regex RivalMemoryPattern = BuildWordPattern(
        "rival", "nemesis", "revenge");

    private static readonly Regex RecentPeakPattern = BuildWordPattern(
        "raid", "ambush", "under attack", "wiped", "barely held");

    // Pass 299: bumped from `private` to `internal` so the test project (which
    // has `InternalsVisibleTo PalLLM.Tests`) can construct synthetic instances
    // for testing pure advisors like `ActionIntentPlanner` without spinning up
    // the full `Build(...)` factory and its game-state inputs. Production
    // callers outside this assembly still cannot instantiate the type — the
    // semantic surface (factory-only construction from real game state) is
    // unchanged for any non-test consumer.
    internal FallbackBehaviorContext()
    {
    }

    public ChatRequest Request { get; init; } = new();

    public PalTaskProfile TaskProfile { get; init; } = new();

    public GameWorldSnapshot Snapshot { get; init; } = new();

    public GameCharacterSnapshot? Character { get; init; }

    public NarrativeCharacterProfile? Lore { get; init; }

    public IReadOnlyList<ConversationMemoryMatch> MemoryMatches { get; init; } = [];

    public IReadOnlyList<ConversationMemoryEntry> RecentEntries { get; init; } = [];

    public FallbackPacingPhase Phase { get; init; }

    public int Seed { get; init; }

    public int HostileCount { get; init; }

    public int AllyCount { get; init; }

    public float Threat { get; init; }

    public float Health { get; init; }

    public float Stamina { get; init; }

    public float Hunger { get; init; }

    public float Morale { get; init; }

    public bool IsLowMorale => Morale <= 0.35f;

    public bool InBase { get; init; }

    public bool Outnumbered { get; init; }

    public bool WantsStealth { get; init; }

    public bool WantsTravel { get; init; }

    public bool WantsExplore { get; init; }

    public bool WantsFight { get; init; }

    public bool WantsBuild { get; init; }

    public bool WantsHarvest { get; init; }

    public bool WantsBaseLogistics { get; init; }

    public bool WantsCapture { get; init; }

    public bool IsPlanningAsk { get; init; }

    public bool WantsCreativeInference { get; init; }

    public bool WantsDetailedReasoning { get; init; }

    public bool IsRoutineDeterministicCandidate { get; init; }

    public bool BaseThreat { get; init; }

    public int KnownBaseCount { get; init; }

    public string PrimaryBaseLabel { get; init; } = "the main base";

    public string SecondaryBaseLabel { get; init; } = "the support base";

    public bool HasObjective { get; init; }

    public string CurrentObjectiveLabel { get; init; } = "the next objective";

    public bool HasRecentTravelObservation { get; init; }

    public string RecentTravelRouteLabel { get; init; } = string.Empty;

    public string RecentTravelAnchorLabel { get; init; } = "the last good anchor";

    public string RecentTravelModeLabel { get; init; } = "on foot";

    public bool HasRecentProductionObservation { get; init; }

    public string RecentProductionBaseLabel { get; init; } = "the main base";

    public string RecentProductionStationLabel { get; init; } = string.Empty;

    public string RecentProductionItemLabel { get; init; } = string.Empty;

    public string RecentProductionStatusLabel { get; init; } = "completed";

    public bool IsNight { get; init; }

    public bool HasWeatherRisk { get; init; }

    public bool IsCampLike { get; init; }

    public bool CanTriggerHeroMoment { get; init; }

    public string FocusThreat { get; init; } = "the nearest threat";

    public string ResourceHint { get; init; } = string.Empty;

    public bool HasNemesisMemory { get; init; }

    public FallbackMemoryTheme NemesisTheme { get; init; }

    public static FallbackBehaviorContext Create(
        ChatRequest request,
        PalTaskProfile taskProfile,
        GameWorldSnapshot snapshot,
        GameCharacterSnapshot? character,
        NarrativeCharacterProfile? lore,
        IReadOnlyList<ConversationMemoryMatch> memoryMatches,
        IReadOnlyList<ConversationMemoryEntry> recentEntries)
    {
        string userLower = request.UserMessage.Trim().ToLowerInvariant();

        float health = FirstDefined(
            character?.HealthFraction,
            snapshot.PlayerHealthFraction,
            TryReadNeed(character?.Needs, "health"),
            DefaultHealth);
        float stamina = FirstDefined(
            character?.StaminaFraction,
            snapshot.PlayerStaminaFraction,
            TryReadNeed(character?.Needs, "stamina"),
            DefaultStamina);
        float hunger = FirstDefined(
            character?.HungerFraction,
            snapshot.PlayerHungerFraction,
            TryReadNeed(character?.Needs, "hunger"),
            DefaultHunger);
        float morale = FirstDefined(
            character?.Morale,
            CalculateMoraleBaseline(snapshot, character),
            DefaultMorale);

        int hostileCount = Math.Max(snapshot.NearbyHostiles.Count, character?.NearbyEnemyCount ?? 0);
        int allyCount = Math.Max(snapshot.NearbyFriendlies.Count, character?.NearbyAllyCount ?? 0);

        bool inBase = snapshot.IsInBase
            ?? (snapshot.ActiveBaseIds.Count > 0 || Matches(userLower, CampSignalPattern));

        List<string> knownBaseLabels = CollectKnownBaseLabels(snapshot);
        int knownBaseCount = knownBaseLabels.Count;
        string primaryBaseLabel = knownBaseCount > 0 ? knownBaseLabels[0] : "the main base";
        string secondaryBaseLabel = knownBaseCount > 1 ? knownBaseLabels[1] : "the support base";
        string travelOriginLabel = NormalizeSurfaceLabel(snapshot.LastTravel?.Origin);
        string travelDestinationLabel = NormalizeSurfaceLabel(snapshot.LastTravel?.Destination);
        string travelWaypointLabel = NormalizeSurfaceLabel(snapshot.LastTravel?.Waypoint);
        bool hasNamedTravelRoute =
            IsNamedTravelLabel(travelOriginLabel) ||
            IsNamedTravelLabel(travelDestinationLabel) ||
            IsNamedTravelLabel(travelWaypointLabel);
        bool hasRecentTravelObservation =
            snapshot.LastTravel is not null &&
            (!string.IsNullOrWhiteSpace(travelOriginLabel) || !string.IsNullOrWhiteSpace(travelDestinationLabel));
        string recentTravelRouteLabel = BuildRecentTravelRouteLabel(
            travelOriginLabel,
            travelDestinationLabel,
            travelWaypointLabel,
            hasNamedTravelRoute);
        string recentTravelAnchorLabel =
            hasNamedTravelRoute && !string.IsNullOrWhiteSpace(travelDestinationLabel)
                ? travelDestinationLabel
                : "the last good anchor";
        string recentTravelModeLabel = HumanizeIdentifier(snapshot.LastTravel?.Mode, "on foot");
        string recentProductionBaseLabel = NormalizeSurfaceLabel(snapshot.LastProduction?.BaseId, primaryBaseLabel);
        string recentProductionStationLabel = HumanizeIdentifier(snapshot.LastProduction?.Station);
        string recentProductionItemLabel = HumanizeIdentifier(snapshot.LastProduction?.Item);
        string recentProductionStatusLabel = HumanizeIdentifier(snapshot.LastProduction?.Status, "completed");
        bool hasRecentProductionObservation =
            snapshot.LastProduction is not null &&
            (!string.IsNullOrWhiteSpace(recentProductionBaseLabel) || !string.IsNullOrWhiteSpace(recentProductionItemLabel));

        bool wantsStealth = Matches(userLower, StealthPattern);
        bool wantsTravel = Matches(userLower, TravelPattern);
        bool wantsExplore = Matches(userLower, ExplorePattern);
        bool wantsFight = Matches(userLower, FightPattern);
        bool wantsBuild = Matches(userLower, BuildPattern);
        bool wantsHarvest = Matches(userLower, HarvestPattern);
        bool wantsBaseLogistics = knownBaseCount >= 2 && Matches(userLower, LogisticsPattern);
        bool wantsCapture = Matches(userLower, CapturePattern);
        bool wantsRescue = Matches(userLower, RescuePattern) || Matches(userLower, HelpCallPattern);
        bool isPlanningAsk = Matches(userLower, PlanningPattern);
        bool wantsCreativeInference = taskProfile.Kind == PalTaskKind.PackAuthoring
            || Matches(userLower, CreativePattern);
        bool wantsDetailedReasoning = Matches(userLower, ReasoningPattern);

        float threat = Math.Clamp(
            Math.Max(
                FirstDefined(snapshot.ThreatLevel, snapshot.AlertLevel, 0f),
                Math.Max(
                    hostileCount > 0 ? Math.Min(1f, hostileCount / 5f) : 0f,
                    character?.RecentDamageFraction ?? 0f)),
            0f,
            1f);

        bool hasRecentPeak = recentEntries.Any(entry =>
            entry.Tags.Any(tag => string.Equals(tag, "fallback-phase:peak", StringComparison.OrdinalIgnoreCase))
            || Matches(entry.Content.ToLowerInvariant(), RecentPeakPattern));

        FallbackPacingPhase phase = DeterminePhase(
            threat,
            health,
            stamina,
            hunger,
            wantsFight,
            hostileCount,
            character,
            snapshot,
            hasRecentPeak);

        bool outnumbered = hostileCount > Math.Max(1, allyCount + 1);
        bool hasWeatherRisk = Matches(
            $"{snapshot.Weather} {snapshot.Biome}".ToLowerInvariant(),
            WeatherRiskPattern);
        bool isNight = Matches(DeriveTimeOfDay(snapshot).ToLowerInvariant(), NightPhrasePattern);
        bool baseThreat = inBase
            && (hostileCount > 0 || threat >= 0.55f || ContainsThreatEvent(snapshot, character));

        FallbackMemoryTheme memoryTheme = DetermineMemoryTheme(memoryMatches);
        bool heroOnCooldown = recentEntries.Any(entry =>
            entry.Tags.Any(tag => string.Equals(tag, "fallback:hero-moment", StringComparison.OrdinalIgnoreCase)));

        bool reactiveTravel = taskProfile.Kind == PalTaskKind.ReactiveBark
            && Matches(userLower, ReactiveTravelPattern);
        bool buildAtNight = inBase && Matches(userLower, NightWindowPattern);

        bool canTriggerHeroMoment =
            !heroOnCooldown &&
            (wantsRescue || health <= 0.25f) &&
            (health <= 0.4f || outnumbered || character?.IsIncapacitated == true) &&
            (wantsFight || hostileCount > 0 || baseThreat);

        return new FallbackBehaviorContext
        {
            Request = request,
            TaskProfile = taskProfile,
            Snapshot = snapshot,
            Character = character,
            Lore = lore,
            MemoryMatches = memoryMatches,
            RecentEntries = recentEntries,
            Phase = phase,
            Seed = FallbackHash.Seed(request, character, snapshot),
            HostileCount = hostileCount,
            AllyCount = allyCount,
            Threat = threat,
            Health = health,
            Stamina = stamina,
            Hunger = hunger,
            Morale = morale,
            InBase = inBase,
            Outnumbered = outnumbered,
            WantsStealth = wantsStealth,
            WantsTravel = wantsTravel || reactiveTravel,
            WantsExplore = wantsExplore,
            WantsFight = wantsFight || threat >= 0.45f,
            WantsBuild = wantsBuild || buildAtNight,
            WantsHarvest = wantsHarvest,
            WantsBaseLogistics = wantsBaseLogistics,
            WantsCapture = wantsCapture,
            IsPlanningAsk = isPlanningAsk,
            WantsCreativeInference = wantsCreativeInference,
            WantsDetailedReasoning = wantsDetailedReasoning,
            IsRoutineDeterministicCandidate =
                wantsStealth ||
                wantsTravel ||
                wantsExplore ||
                wantsBuild ||
                wantsHarvest ||
                wantsBaseLogistics ||
                wantsCapture ||
                baseThreat ||
                phase is FallbackPacingPhase.Relax or FallbackPacingPhase.Recover,
            BaseThreat = baseThreat,
            KnownBaseCount = knownBaseCount,
            PrimaryBaseLabel = primaryBaseLabel,
            SecondaryBaseLabel = secondaryBaseLabel,
            HasObjective = !string.IsNullOrWhiteSpace(snapshot.CurrentObjective),
            CurrentObjectiveLabel = string.IsNullOrWhiteSpace(snapshot.CurrentObjective)
                ? "the next objective"
                : snapshot.CurrentObjective.Trim(),
            HasRecentTravelObservation = hasRecentTravelObservation,
            RecentTravelRouteLabel = recentTravelRouteLabel,
            RecentTravelAnchorLabel = recentTravelAnchorLabel,
            RecentTravelModeLabel = recentTravelModeLabel,
            HasRecentProductionObservation = hasRecentProductionObservation,
            RecentProductionBaseLabel = recentProductionBaseLabel,
            RecentProductionStationLabel = recentProductionStationLabel,
            RecentProductionItemLabel = recentProductionItemLabel,
            RecentProductionStatusLabel = recentProductionStatusLabel,
            IsNight = isNight,
            HasWeatherRisk = hasWeatherRisk,
            IsCampLike = inBase
                || taskProfile.Kind == PalTaskKind.ReactiveBark
                || Matches(userLower, CampRestPattern),
            CanTriggerHeroMoment = canTriggerHeroMoment,
            FocusThreat = ResolveFocusThreat(snapshot, hostileCount),
            ResourceHint = ResolveResourceHint(snapshot),
            HasNemesisMemory = memoryTheme != FallbackMemoryTheme.None,
            NemesisTheme = memoryTheme,
        };
    }

    // Kept separate so "help" - which is a common polite verb - does not fire rescue intent
    // unless it appears alongside a rescue cue. The bare word is matched only inside
    // unambiguous phrases like "help me" / "help us" / "i need help".
    private static readonly Regex HelpCallPattern = BuildWordPattern(
        "help me", "help us", "need help", "i need help");

    private static string ResolveFocusThreat(GameWorldSnapshot snapshot, int hostileCount)
    {
        string? first = snapshot.NearbyHostiles.FirstOrDefault();
        if (!string.IsNullOrEmpty(first))
        {
            return first;
        }

        return hostileCount > 0 ? "the nearest threat" : "the first real problem";
    }

    private static string ResolveResourceHint(GameWorldSnapshot snapshot)
    {
        string? first = snapshot.NearbyResources.FirstOrDefault();
        return string.IsNullOrEmpty(first) ? string.Empty : $" near {first}";
    }

    private static bool ContainsThreatEvent(GameWorldSnapshot snapshot, GameCharacterSnapshot? character)
    {
        IEnumerable<string> events = snapshot.RecentEvents.Concat(character?.RecentEvents ?? []);
        return events.Any(item => Matches(item.ToLowerInvariant(), ThreatEventPattern));
    }

    private static FallbackMemoryTheme DetermineMemoryTheme(IReadOnlyList<ConversationMemoryMatch> memoryMatches)
    {
        foreach (ConversationMemoryMatch match in memoryMatches)
        {
            string text = match.Entry.Content.ToLowerInvariant();
            if (Matches(text, LossMemoryPattern))
            {
                return FallbackMemoryTheme.Loss;
            }

            if (Matches(text, AmbushMemoryPattern))
            {
                return FallbackMemoryTheme.Ambush;
            }

            if (Matches(text, RivalMemoryPattern))
            {
                return FallbackMemoryTheme.Rival;
            }
        }

        return FallbackMemoryTheme.None;
    }

    private static float CalculateMoraleBaseline(GameWorldSnapshot snapshot, GameCharacterSnapshot? character)
    {
        float morale = DefaultMorale;
        if (character?.IsIncapacitated == true)
        {
            morale -= 0.3f;
        }

        if ((snapshot.ThreatLevel ?? 0f) >= 0.6f || (snapshot.AlertLevel ?? 0f) >= 0.6f)
        {
            morale -= 0.15f;
        }

        return Math.Clamp(morale, 0f, 1f);
    }

    private static FallbackPacingPhase DeterminePhase(
        float threat,
        float health,
        float stamina,
        float hunger,
        bool wantsFight,
        int hostileCount,
        GameCharacterSnapshot? character,
        GameWorldSnapshot snapshot,
        bool hasRecentPeak)
    {
        bool incapacitated = character?.IsIncapacitated == true;
        bool undersiegeLowHealth = hostileCount >= 3 && health <= 0.55f;
        // Low vitals while hostiles are still present should not silently downgrade to
        // Recover - that would keep retreat-and-rally and emergency-triage from firing
        // during active combat. Escalate to Peak so combat-oriented strategies stay open.
        bool combatCrisis = hostileCount > 0 && health <= 0.45f;
        if (incapacitated || threat >= 0.75f || undersiegeLowHealth || combatCrisis || ContainsThreatEvent(snapshot, character))
        {
            return FallbackPacingPhase.Peak;
        }

        if (health <= 0.45f || stamina <= 0.3f || hunger <= 0.25f || hasRecentPeak)
        {
            return FallbackPacingPhase.Recover;
        }

        if (wantsFight || hostileCount > 0 || !string.IsNullOrWhiteSpace(snapshot.CurrentObjective))
        {
            return FallbackPacingPhase.BuildUp;
        }

        return FallbackPacingPhase.Relax;
    }

    private static float? TryReadNeed(IReadOnlyDictionary<string, float>? needs, string key) =>
        needs is not null && needs.TryGetValue(key, out float value) ? value : null;

    private static float FirstDefined(float? value1, float? value2, float fallback) =>
        value1.HasValue ? Math.Clamp(value1.Value, 0f, 1f) :
        value2.HasValue ? Math.Clamp(value2.Value, 0f, 1f) :
        Math.Clamp(fallback, 0f, 1f);

    private static float FirstDefined(float? value1, float? value2, float? value3, float fallback) =>
        value1.HasValue ? Math.Clamp(value1.Value, 0f, 1f) :
        value2.HasValue ? Math.Clamp(value2.Value, 0f, 1f) :
        value3.HasValue ? Math.Clamp(value3.Value, 0f, 1f) :
        Math.Clamp(fallback, 0f, 1f);

    private static List<string> CollectKnownBaseLabels(GameWorldSnapshot snapshot)
    {
        List<string> labels = [];

        foreach (string activeBaseId in snapshot.ActiveBaseIds)
        {
            AddBaseLabel(labels, activeBaseId);
        }

        foreach (GameBaseSnapshot baseInfo in snapshot.KnownBases)
        {
            AddBaseLabel(labels, baseInfo.BaseId);
        }

        return labels;
    }

    private static void AddBaseLabel(List<string> labels, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return;
        }

        string cleaned = candidate.Trim();
        if (labels.Exists(label => string.Equals(label, cleaned, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        labels.Add(cleaned);
    }

    private static string NormalizeSurfaceLabel(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value.Trim();
    }

    private static string HumanizeIdentifier(string? value, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        string normalized = value
            .Trim()
            .Replace('_', ' ')
            .Replace('-', ' ');

        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static bool IsNamedTravelLabel(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "unknown", StringComparison.OrdinalIgnoreCase) &&
        !value.StartsWith("sector ", StringComparison.OrdinalIgnoreCase);

    private static string BuildRecentTravelRouteLabel(
        string origin,
        string destination,
        string waypoint,
        bool hasNamedTravelRoute)
    {
        if (!hasNamedTravelRoute)
        {
            return string.Empty;
        }

        string anchorOrigin = string.IsNullOrWhiteSpace(origin) ? "the last good anchor" : origin;
        string anchorDestination = string.IsNullOrWhiteSpace(destination) ? "the next stable landmark" : destination;
        string waypointClause = string.IsNullOrWhiteSpace(waypoint) ? string.Empty : $" via {waypoint}";
        return $"{anchorOrigin} -> {anchorDestination}{waypointClause}";
    }

    /// <summary>
    /// Word-boundary pattern over the supplied terms. Accepts either single words or phrases;
    /// <c>Regex.Escape</c> keeps punctuation literal and spaces match spaces. Event IDs that
    /// use underscores as separators are handled by <see cref="Matches"/> which normalises
    /// underscores to spaces before matching.
    /// </summary>
    private static Regex BuildWordPattern(params string[] terms)
    {
        string alternation = string.Join("|", terms.Select(Regex.Escape));
        return new Regex(
            $@"\b(?:{alternation})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
    }

    private static bool Matches(string text, Regex pattern)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // Event IDs and memory tags use '_' as a separator. Treat it as whitespace so the
        // \b boundaries match e.g. "base_raid" -> "base raid" and the keyword "raid" fires.
        string normalized = text.IndexOf('_') >= 0 ? text.Replace('_', ' ') : text;
        return pattern.IsMatch(normalized);
    }

    private static string DeriveTimeOfDay(GameWorldSnapshot snapshot)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.TimeOfDay))
        {
            return snapshot.TimeOfDay;
        }

        if (!snapshot.IsWorldLoaded || snapshot.TicksPerDay <= 0)
        {
            return string.Empty;
        }

        long tickOfDay = snapshot.CurrentTick % snapshot.TicksPerDay;
        if (tickOfDay < 0)
        {
            tickOfDay += snapshot.TicksPerDay;
        }

        double fraction = tickOfDay / (double)snapshot.TicksPerDay;
        return fraction switch
        {
            < 0.15 => "dawn",
            < 0.55 => "day",
            < 0.75 => "dusk",
            _ => "night",
        };
    }
}
