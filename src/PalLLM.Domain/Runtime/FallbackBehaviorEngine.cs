using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Packs;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Deterministic-fallback director. 19 hand-authored Try_* strategies
//            that always return a coherent reply when inference is disabled,
//            broken, rate-limited, or thermal-throttled. Hard rule: POST
//            /api/chat MUST never 500 because inference is off; this file
//            owns the guarantee.
//   surface: FallbackBehaviorEngine; one Try_<Name> method per strategy.
//   gate:    Drift_Fallback_strategy_count - the count of Try_* methods must
//            match docs/ROADMAP.md and PROJECT_NUMBERS.json.fallbackStrategies.
//   adr:     0001-deterministic-first-reply-pipeline.md (load-bearing).
//   docs:    docs/PROMPT_CARDS.md (player-facing card per strategy),
//            docs/FALLBACK_AI_RESEARCH.md (design rationale + research),
//            docs/COOKBOOK.md (recipe to add a new strategy).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

public sealed class FallbackBehaviorEngine
{
    private readonly FallbackOptions _options;

    public FallbackBehaviorEngine(PalLlmOptions options)
    {
        _options = options.Fallback;
    }

    internal FallbackBehaviorContext Analyze(
        ChatRequest request,
        PalTaskProfile taskProfile,
        GameWorldSnapshot snapshot,
        GameCharacterSnapshot? character,
        NarrativeCharacterProfile? lore,
        IReadOnlyList<ConversationMemoryMatch> memoryMatches,
        IReadOnlyList<ConversationMemoryEntry> recentEntries) =>
        FallbackBehaviorContext.Create(
            request,
            taskProfile,
            snapshot,
            character,
            lore,
            memoryMatches,
            recentEntries);

    internal FallbackBehaviorDecision Generate(
        FallbackBehaviorContext context)
    {
        List<FallbackBehaviorDecision> candidates =
        [
            TryHeroMoment(context),
            TryEmergencyTriage(context),
            TryRetreatAndRally(context),
            TryStealthShadow(context),
            TryNemesisCounterplay(context),
            TryBuddyOverwatch(context),
            TryPerimeterLockdown(context),
            TryBaseNetwork(context),
            TrySafeTravel(context),
            TryCaptureWindow(context),
            TryObjectivePush(context),
            TryCraftingDiscipline(context),
            TryHarvestWindow(context),
            TryWeatherShelter(context),
            TryExplorationSweep(context),
            TryMoraleRally(context),
            TryRecoverWindow(context),
            TryAmbientCamp(context),
            CreateGeneralDirector(context),
        ];

        List<FallbackBehaviorDecision> applicable = candidates
            .Where(candidate => candidate.IsApplicable)
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.StrategyId, StringComparer.Ordinal)
            .ToList();

        if (context.WantsCreativeInference &&
            context.Phase == FallbackPacingPhase.Relax &&
            !context.BaseThreat &&
            context.HostileCount == 0)
        {
            FallbackBehaviorDecision? tonalAnchor = applicable.FirstOrDefault(candidate =>
                candidate.StrategyId == "ambient-camp"
                || (candidate.StrategyId == "safe-travel" && context.WantsTravel)
                || (candidate.StrategyId == "exploration-sweep" && context.WantsExplore));

            if (tonalAnchor is not null)
            {
                return tonalAnchor;
            }
        }

        foreach (FallbackBehaviorDecision candidate in applicable)
        {
            if (!WasUsedRecently(candidate.StrategyId, context.RecentEntries))
            {
                return candidate;
            }
        }

        return applicable[0];
    }

    internal bool ShouldBypassInference(FallbackBehaviorContext context, out string reason)
    {
        reason = string.Empty;
        int wordCount = context.Request.UserMessage
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Length;
        bool conciseRoutinePrompt = wordCount <= 18;

        if (!_options.Enabled || !_options.EnablePolicyBypass)
        {
            return false;
        }

        if (context.WantsCreativeInference || context.WantsDetailedReasoning)
        {
            return false;
        }

        if (_options.PreferForReactiveBarks && context.TaskProfile.Kind == PalTaskKind.ReactiveBark)
        {
            reason = "reactive_bark";
            return true;
        }

        if (_options.PreferForRoutineTacticalTasks &&
            context.IsRoutineDeterministicCandidate &&
            (context.TaskProfile.AllowFastLane || context.IsPlanningAsk || context.BaseThreat || conciseRoutinePrompt))
        {
            reason = "routine_tactical";
            return true;
        }

        if (_options.PreferForRecoveryAndCampTasks &&
            context.IsCampLike &&
            context.Phase is FallbackPacingPhase.Relax or FallbackPacingPhase.Recover)
        {
            reason = "camp_or_recovery";
            return true;
        }

        return false;
    }

    private static bool WasUsedRecently(string strategyId, IReadOnlyList<ConversationMemoryEntry> recentEntries) =>
        recentEntries.Any(entry => entry.Tags.Any(tag =>
            string.Equals(tag, $"fallback:{strategyId}", StringComparison.OrdinalIgnoreCase)));

    private static FallbackBehaviorDecision TryHeroMoment(FallbackBehaviorContext context)
    {
        if (!context.CanTriggerHeroMoment)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "hero-moment",
            100,
            ["buddy_save", "rare_support", "player_first"],
            Choose(context, "hero-lead",
                "Hero beat only if we need it.",
                "Saving the special move for the right second.",
                "This is the kind of moment I spend carefully."),
            Choose(context, "hero-action",
                $"If {context.FocusThreat} commits on you, I break the angle and buy the two seconds that matter.",
                $"If {context.FocusThreat} overextends, I make the interruption and hand the finish back to you.",
                "If the nearest threat crashes in, I cut the momentum and give you space to recover."),
            Choose(context, "hero-contingency",
                "Otherwise I stay disciplined and do not turn this into a noisy escort routine.",
                "If the window never opens, I keep shadowing you instead of forcing the scene.",
                "If it stays stable, I save the move and keep support invisible."));
    }

    private static FallbackBehaviorDecision TryEmergencyTriage(FallbackBehaviorContext context)
    {
        if (context.Phase != FallbackPacingPhase.Peak)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "emergency-triage",
            95,
            ["peak", "triage", "closest_threat"],
            Choose(context, "triage-lead",
                "Peak pressure.",
                "Hot contact.",
                "This is the sharp end of the curve."),
            Choose(context, "triage-action",
                $"Pin {context.FocusThreat} first and keep hard cover between us and the rest.",
                $"Solve {context.FocusThreat} first and make the others walk through bad angles to reach us.",
                "Take the nearest danger off the table before we think about anything flashy."),
            Choose(context, "triage-contingency",
                "If the lane collapses, we break contact, heal, and reset instead of opening a second fight.",
                "If our cover gets dirty, we disengage and rebuild the line instead of gambling the whole encounter.",
                "If it keeps widening, we trade ground for survival and re-enter on our terms."));
    }

    private static FallbackBehaviorDecision TryRetreatAndRally(FallbackBehaviorContext context)
    {
        if (context.Phase != FallbackPacingPhase.Peak || (!context.Outnumbered && !context.IsLowMorale))
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "retreat-and-rally",
            92,
            ["confidence", "defensive_shift", "spacing"],
            Choose(context, "retreat-lead",
                "Confidence is slipping.",
                "This fight is leaning their way.",
                "We are not winning the exchange by pretending otherwise."),
            Choose(context, "retreat-action",
                "Collapse back toward the nearest ally or base anchor and make them step into our space.",
                "Shorten the line, keep everyone in support distance, and force them to come through a single readable approach.",
                "Regroup on the strongest nearby position instead of letting the fight smear across the whole area."),
            Choose(context, "retreat-contingency",
                "If they keep pressing, we trade distance for safety until the line reforms.",
                "If panic starts spreading, shrink the plan until every move is easy to execute cleanly.",
                "If the push stalls, turn the regroup into a clean reset instead of instantly re-peeking."));
    }

    private static FallbackBehaviorDecision TryStealthShadow(FallbackBehaviorContext context)
    {
        if (!context.WantsStealth || context.Phase == FallbackPacingPhase.Peak)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "stealth-shadow",
            90,
            ["stealth", "same_side", "trust"],
            Choose(context, "stealth-lead",
                "Quiet pass.",
                "Stealth rules now.",
                "No loud heroics here."),
            Choose(context, "stealth-action",
                "I stay on your side of the route, keep out of your firing lane, and call danger before I break cover.",
                "I mirror your line, stay in the same space, and refuse any shot that would give away your position.",
                "I shadow the route, keep the threat local, and let you choose the first move."),
            Choose(context, "stealth-contingency",
                "If something turns, we freeze first and only act if it is about to expose you.",
                "If the read goes bad, we reset to the last safe landmark instead of improvising noise.",
                "If a target starts to clock us, I warn first and intervene only on the clean rescue window."));
    }

    private static FallbackBehaviorDecision TryNemesisCounterplay(FallbackBehaviorContext context)
    {
        if (!context.HasNemesisMemory || (!context.WantsFight && context.HostileCount == 0))
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "nemesis-counterplay",
            89,
            ["memory", "revenge_loop", "adaptation"],
            Choose(context, "nemesis-lead",
                "We know this pattern.",
                "This is not our first dance with them.",
                "History says they punish lazy reads."),
            Choose(context, "nemesis-action",
                "Answer the move that hurt us last time instead of inventing a new mistake under pressure.",
                "Treat this like a rematch and deny the angle they won with before.",
                "Use what we learned, take the safer counter, and make them prove they have a second trick."),
            Choose(context, "nemesis-contingency",
                "If they change script, fall back to cover and reassess before committing again.",
                "If the old read stops fitting, we downgrade to local fundamentals instead of ego fighting.",
                "If the rivalry starts baiting us into greed, we reset the tempo and take the boring win."));
    }

    private static FallbackBehaviorDecision TryBuddyOverwatch(FallbackBehaviorContext context)
    {
        if (!context.WantsFight || context.Phase == FallbackPacingPhase.Peak || context.IsLowMorale)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "buddy-overwatch",
            88,
            ["buddy_ai", "support_angle", "player_visibility"],
            Choose(context, "overwatch-lead",
                "We do this as a buddy pair.",
                "Support angle, not solo heroics.",
                "I stay useful where you can actually feel it."),
            Choose(context, "overwatch-action",
                "I hold the close angle, stay in the same room as you, and pressure anything that commits too hard.",
                "I take the support lane, keep the front readable, and help you out of tight spots without stealing the whole encounter.",
                "I shadow your push, keep the flank honest, and make my interventions obvious and timely."),
            Choose(context, "overwatch-contingency",
                "If you push, I follow the move; if you stop, I cover instead of wandering off.",
                "If the fight splinters, I collapse back to your side rather than chase a side duel.",
                "If support would break your plan, I keep it verbal and let you finish the read."));
    }

    private static FallbackBehaviorDecision TryPerimeterLockdown(FallbackBehaviorContext context)
    {
        if (!context.BaseThreat)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "perimeter-lockdown",
            87,
            ["base_defense", "frontline", "protect_workers"],
            Choose(context, "perimeter-lead",
                "Perimeter first.",
                "Base-side problem.",
                "We defend the shape of the base before the pride of the squad."),
            Choose(context, "perimeter-action",
                "Set defense on one readable front, keep workers off the exposed edge, and collapse on the closest breach before chasing stragglers.",
                "Hold a clear frontline, keep the soft assets inside it, and focus the nearest entry instead of scattering across every alarm.",
                "Turn the base into a single problem at a time: nearest breach, nearest threat, nearest reset point."),
            Choose(context, "perimeter-contingency",
                "If the wall gets noisy everywhere, tighten around storage and beds instead of stretching thin.",
                "If they force multiple angles, protect the irreplaceable pieces and let the outer edge breathe.",
                "If the defense starts intermixing, pull back until friendly space is intact again."));
    }

    private static FallbackBehaviorDecision TryBaseNetwork(FallbackBehaviorContext context)
    {
        if (!context.WantsBaseLogistics || context.KnownBaseCount < 2)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "base-network",
            85,
            ["base-logistics", "specialization", "transfer-loop"],
            Choose(context, "base-network-lead",
                "Run the bases like a network, not clones.",
                "Multiple bases means specialization, not duplication.",
                "This is a logistics question more than a construction question."),
            Choose(context, "base-network-action",
                $"Let {context.PrimaryBaseLabel} carry the long-cycle jobs like smelting, ranching, breeding, and bulk storage while {context.SecondaryBaseLabel} stays lean for spheres, medicine, repairs, and fast redeploys.",
                $"Make {context.PrimaryBaseLabel} the deep-production hearth and keep {context.SecondaryBaseLabel} as the field-facing outpost so finished goods move on deliberate supply loops instead of both camps doing half the same jobs.",
                context.HasRecentProductionObservation
                    ? $"Keep {context.RecentProductionBaseLabel} on {DescribeRecentProductionLane(context)} while {context.SecondaryBaseLabel} handles restocks, crafting bursts, and anything we need raid-ready."
                    : $"Pick one heavy base and one quick-turn base: {context.PrimaryBaseLabel} handles the slow throughput while {context.SecondaryBaseLabel} handles restocks, crafting bursts, and anything we need raid-ready."),
            Choose(context, "base-network-contingency",
                "If one base starts tripping over raids or pathing, cut it back to essentials and let the other carry throughput until the lane is stable again.",
                "If transport turns messy, simplify the loop so raw materials stay where they are abundant and only finished goods travel.",
                "If every base starts doing every job, stop and reassign because duplicated half-lines waste Pals, power, and attention."));
    }

    private static FallbackBehaviorDecision TrySafeTravel(FallbackBehaviorContext context)
    {
        if (!context.WantsTravel)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "safe-travel",
            86,
            ["travel", "terrain", "anchors"],
            Choose(context, "travel-lead",
                "Short route, low risk.",
                "Travel brain, not bravado.",
                "The map can beat us if we let it."),
            Choose(context, "travel-action",
                "Use the gentlest line toward the objective, keep structures or high ground as anchors, and avoid terrain that can split or slow us.",
                $"Bias for the route with clean footing, obvious recovery space, and a reset line back to {context.RecentTravelAnchorLabel} if the path folds.",
                "Take the low-friction path, keep the return lane readable, and never let the terrain choose the fight for us."),
            Choose(context, "travel-contingency",
                "If the route turns ugly, loop and keep momentum rather than fighting the map.",
                "If footing or visibility goes bad, slow down before the terrain steals more time than any enemy would.",
                "If we get dragged off line, reset to the last good anchor and path again instead of brute forcing it."));
    }

    private static FallbackBehaviorDecision TryCaptureWindow(FallbackBehaviorContext context)
    {
        if (!context.WantsCapture)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "capture-window",
            85,
            ["capture", "discipline", "clean_attempt"],
            Choose(context, "capture-lead",
                "Capture discipline.",
                "We only get one clean catch.",
                "This is a setup problem before it is a throw problem."),
            Choose(context, "capture-action",
                "Trim the target down, keep a retreat lane open, and stop stray damage once the capture window opens.",
                "Set the attempt up by controlling the area first, then commit while the target is weak and the line stays clean.",
                "Make the catch from stability: soften the target, clear the noise, and stop panic shots at the wrong second."),
            Choose(context, "capture-contingency",
                "If the area gets crowded, clear the noise first and retry instead of wasting the attempt.",
                "If the target starts dragging us through bad terrain, reset and force a cleaner window.",
                "If the attempt gets messy, save resources and rebuild the setup rather than forcing it."));
    }

    private static FallbackBehaviorDecision TryObjectivePush(FallbackBehaviorContext context)
    {
        if (!context.HasObjective || (context.Phase != FallbackPacingPhase.BuildUp && !context.IsPlanningAsk))
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "objective-push",
            84,
            ["objective", "build_up", "pace_control"],
            Choose(context, "objective-lead",
                "Build pressure, do not rush it.",
                "We're in the wind-up.",
                "This is a setup beat before the push."),
            Choose(context, "objective-action",
                "Reposition, top off essentials, and hit the next objective with one clear angle instead of drifting into side problems.",
                $"Stay lined up for {context.CurrentObjectiveLabel}, keep the route readable, and push only once the team is actually ready.",
                "Use the lull to sort position and supplies, then commit to the next step without feeding extra chaos into it."),
            Choose(context, "objective-contingency",
                "If resistance spikes before we commit, delay a beat and make them reveal the real choke.",
                "If the first approach looks cursed, rotate cleanly instead of grinding against a bad door.",
                "If we lose the tempo, reset the staging and make the next entry intentional."));
    }

    private static FallbackBehaviorDecision TryCraftingDiscipline(FallbackBehaviorContext context)
    {
        if (!context.WantsBuild)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "crafting-discipline",
            83,
            ["maintenance", "single_bottleneck", "parallel_tasks"],
            Choose(context, "craft-lead",
                "Calm window, practical work.",
                "This is a maintenance beat.",
                "We build by removing bottlenecks, not by flailing at everything."),
            Choose(context, "craft-action",
                "Repair one bottleneck, queue one upgrade, and leave enough hands free to react if the world changes.",
                "Pick the highest-friction job, finish it cleanly, and pair it with one small side task instead of five half-projects.",
                "Handle the most practical build task first, let a second chore run in parallel, and keep the base responsive."),
            Choose(context, "craft-contingency",
                "If a threat signal appears, drop expansion first and keep the base functional.",
                "If this starts snowballing into chores, stop at the first useful win and bank the stability.",
                "If the calm breaks, preserve tools, materials, and exits before ambition."));
    }

    private static FallbackBehaviorDecision TryHarvestWindow(FallbackBehaviorContext context)
    {
        if (!context.WantsHarvest)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "harvest-window",
            82,
            ["gathering", "safe_loop", "resource_trip"],
            Choose(context, "harvest-lead",
                "Use the lull.",
                "Resource pass.",
                "Quick gain, low drama."),
            Choose(context, "harvest-action",
                $"Take the safest nearby yield{context.ResourceHint}, keep the return path obvious, and pair the job with a light watch instead of full tunnel vision.",
                "Run a short gather loop, bank resources early, and never let greed turn a supply trip into a rescue mission.",
                "Harvest from the safest pocket first, keep everyone inside support range, and move only once the return lane is still clean."),
            Choose(context, "harvest-contingency",
                "If hostiles drift in, bank what we have and reset the loop.",
                "If the route back stops being obvious, end the trip before the map taxes the haul away.",
                "If the quiet ends, turn the gather into a retreat instead of a stubborn stand."));
    }

    private static FallbackBehaviorDecision TryWeatherShelter(FallbackBehaviorContext context)
    {
        if (!context.HasWeatherRisk)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "weather-shelter",
            81,
            ["weather", "visibility", "foothold"],
            Choose(context, "weather-lead",
                "The environment is part of the fight.",
                "Weather tax is real.",
                "Bad visibility and bad footing count as enemies too."),
            Choose(context, "weather-action",
                "Respect the visibility and footing hit, move anchor to anchor, and avoid any route that would force a recovery in the open.",
                "Keep the march tight, favor stable ground, and make shelter or cover part of the route plan instead of an afterthought.",
                "Cut speed before the environment cuts it for us, and keep every move one rollback away from safety."),
            Choose(context, "weather-contingency",
                "If the storm keeps stacking risk, shelter and resume when the map is readable again.",
                "If the weather turns a small mistake into a wipe, we wait it out and keep our resources.",
                "If footing goes slippery or sight lines vanish, we downgrade the plan until it is safe again."));
    }

    private static FallbackBehaviorDecision TryExplorationSweep(FallbackBehaviorContext context)
    {
        if (!context.WantsExplore)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "exploration-sweep",
            80,
            ["local_sensing", "search", "fairness"],
            Choose(context, "explore-lead",
                "Local sweep only.",
                "No omniscient nonsense.",
                "We search what is actually around us, not what we wish we knew."),
            Choose(context, "explore-action",
                "Check what is actually near us, clear corners in pairs, and bias toward lines that keep our last safe space behind us.",
                "Search outward from the last reliable landmark, keep eyes on the likely approaches, and do not invent threats from the entire map.",
                "Treat the next few rooms as a methodical sweep: local information, simple calls, clean exits."),
            Choose(context, "explore-contingency",
                "If we lose the thread, return to the last known landmark and search outward.",
                "If the signals get muddy, stop expanding the search and rebuild certainty first.",
                "If something pulls us off plan, mark the last clean point and re-anchor there."));
    }

    private static FallbackBehaviorDecision TryMoraleRally(FallbackBehaviorContext context)
    {
        if (!context.IsLowMorale)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "morale-rally",
            79,
            ["morale", "confidence", "simple_jobs"],
            Choose(context, "morale-lead",
                "A steady unit fights better than a brave mess.",
                "Morale first.",
                "We need confidence to climb before the pace does."),
            Choose(context, "morale-action",
                "Cluster around the strongest position or ally, give the shaky hands simpler jobs, and let confidence rebuild before the next gamble.",
                "Keep everyone inside a readable support bubble, hand out obvious work, and let a few solid wins lift the room.",
                "Reduce the plan to clean fundamentals and let the team feel competent again before you ask for anything clever."),
            Choose(context, "morale-contingency",
                "If panic starts spreading, shrink the plan until everyone can execute it cleanly.",
                "If one bad beat rattles the group, answer with structure instead of louder orders.",
                "If confidence returns, step the pace back up gradually rather than all at once."));
    }

    private static FallbackBehaviorDecision TryRecoverWindow(FallbackBehaviorContext context)
    {
        if (context.Phase != FallbackPacingPhase.Recover)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "recover-window",
            78,
            ["recover", "reset", "no_greed"],
            Choose(context, "recover-lead",
                "Recovery window.",
                "We earned a breather.",
                "This is the valley after the peak."),
            Choose(context, "recover-action",
                "Heal, reload, eat, and sort inventory before we touch the next objective.",
                "Treat this like a reset beat: patch up, refill, and clean the formation before we accelerate again.",
                "Cash in the lull on recovery, not greed; the next push is better with full hands and clear heads."),
            Choose(context, "recover-contingency",
                "If the world stays quiet, take one more small prep step; if not, keep the reset short and move.",
                "If trouble returns early, leave with recovery complete enough to fight, not perfect enough to waste the opening.",
                "If the calm lasts, translate it into readiness instead of wandering."));
    }

    private static FallbackBehaviorDecision TryAmbientCamp(FallbackBehaviorContext context)
    {
        if (context.Phase != FallbackPacingPhase.Relax || !context.IsCampLike)
        {
            return FallbackBehaviorDecision.NotApplicable;
        }

        return BuildDecision(
            context,
            "ambient-camp",
            77,
            ["ambient", "camp_life", "parallel_behaviors"],
            Choose(context, "ambient-lead",
                "Camp can breathe for a minute.",
                "Soft watch.",
                "This is where everyday life keeps the whole machine stable."),
            Choose(context, "ambient-action",
                "Eat, mend, chat, and knock out one useful chore in parallel while keeping a lazy perimeter check running.",
                "Use the calm for overlapping little wins: food, repairs, sorting, and a low-effort watch on the edges.",
                "Let the camp feel lived in for a moment, but keep one eye on the perimeter and one hand free to pivot."),
            Choose(context, "ambient-contingency",
                "If the mood shifts, we snap from cozy to practical without drama.",
                "If a threat signal appears, the small comforts stop and the watch hardens immediately.",
                "If the quiet holds, let the downtime do real work instead of idling it away."));
    }

    private static FallbackBehaviorDecision CreateGeneralDirector(FallbackBehaviorContext context) =>
        BuildDecision(
            context,
            "general-director",
            1,
            [context.Phase.ToString().ToLowerInvariant(), "general", "fallback"],
            context.Phase switch
            {
                FallbackPacingPhase.Peak => "Pressure is high, so we keep the next move simple.",
                FallbackPacingPhase.Recover => "The pace is easing off, so we use it to reset cleanly.",
                FallbackPacingPhase.BuildUp => "This is the buildup, so we set the next beat on purpose.",
                _ => "We are in a calm pocket, so practical choices win."
            },
            Choose(context, "general-action",
                "Stay local, solve the clearest problem first, and keep enough structure that a sudden spike does not blindside us.",
                "Pick one concrete action, keep the rest in reserve, and make the situation more readable before making it bigger.",
                "Keep the plan grounded in what we can actually see, do the obvious useful thing, and leave room to adapt."),
            Choose(context, "general-contingency",
                "If the world changes faster than expected, fall back to cover, allies, and the last safe landmark.",
                "If the read breaks, reset to safety and build a new plan instead of doubling down on the old one.",
                "If new pressure appears, shorten the plan until it is robust again."));

    private static FallbackBehaviorDecision BuildDecision(
        FallbackBehaviorContext context,
        string strategyId,
        int priority,
        IReadOnlyList<string> signals,
        string lead,
        string action,
        string contingency)
    {
        // Lead/action/contingency plus an optional overlay - max four sentences by construction,
        // so no further truncation is needed.
        List<string> sentences = [lead, action, contingency];
        string overlay = BuildOverlaySentence(context, strategyId);
        if (!string.IsNullOrWhiteSpace(overlay))
        {
            sentences.Add(overlay);
        }

        string message = string.Join(
            " ",
            sentences
                .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
                .Select(EnsureSentence));

        return new FallbackBehaviorDecision(
            strategyId,
            context.Phase,
            message,
            priority,
            signals,
            isApplicable: true);
    }

    private static string BuildOverlaySentence(FallbackBehaviorContext context, string strategyId)
    {
        if (strategyId == "base-network")
        {
            if (context.HasRecentProductionObservation)
            {
                return $"Right now {context.RecentProductionBaseLabel} is carrying {DescribeRecentProductionLane(context)}, so keep that lane stable instead of bouncing it between bases.";
            }

            return context.HasObjective
                ? $"That keeps {context.CurrentObjectiveLabel} supplied while {context.PrimaryBaseLabel} and {context.SecondaryBaseLabel} stop competing for the same chores."
                : $"That lets {context.PrimaryBaseLabel} stay on deep production while {context.SecondaryBaseLabel} stays light enough to react fast.";
        }

        if (strategyId == "safe-travel" && context.HasRecentTravelObservation)
        {
            return !string.IsNullOrWhiteSpace(context.RecentTravelRouteLabel)
                ? $"Our latest clean movement was {context.RecentTravelRouteLabel}, so if the route degrades we reset to {context.RecentTravelAnchorLabel} and path again from there."
                : $"The last travel read was {context.RecentTravelModeLabel}, so if the path degrades we reset to {context.RecentTravelAnchorLabel} instead of brute forcing the terrain.";
        }

        if (context.HasNemesisMemory && strategyId is not "nemesis-counterplay" and not "ambient-camp")
        {
            return context.NemesisTheme switch
            {
                FallbackMemoryTheme.Loss => "We already paid for overextending here once, so discipline beats pride.",
                FallbackMemoryTheme.Ambush => "We remember the ambush pattern, so we stay harder to surprise this time.",
                FallbackMemoryTheme.Rival => "That old troublemaker already has history with us, so we answer patterns instead of taunts.",
                _ => "We have history here, so I am favoring the safer read over the flashy one."
            };
        }

        if (context.IsNight && strategyId is "safe-travel" or "exploration-sweep" or "ambient-camp")
        {
            return "Night means shorter loops, clearer landmarks, and no wandering farther than we can recover.";
        }

        if (context.HasObjective && strategyId is "safe-travel" or "objective-push")
        {
            return $"That keeps us lined up for {context.CurrentObjectiveLabel}.";
        }

        return string.Empty;
    }

    private static string DescribeRecentProductionLane(FallbackBehaviorContext context)
    {
        string status = string.IsNullOrWhiteSpace(context.RecentProductionStatusLabel)
            ? "running"
            : context.RecentProductionStatusLabel;
        string item = string.IsNullOrWhiteSpace(context.RecentProductionItemLabel)
            ? "the current queue"
            : context.RecentProductionItemLabel;
        string station = string.IsNullOrWhiteSpace(context.RecentProductionStationLabel)
            ? string.Empty
            : $" on {context.RecentProductionStationLabel}";
        return $"{status} {item}{station}";
    }

    private static string Choose(FallbackBehaviorContext context, string salt, params string[] options)
    {
        if (options.Length == 0)
        {
            return string.Empty;
        }

        int index = FallbackHash.PositiveModulo(
            FallbackHash.OfString($"{context.Seed}:{salt}"),
            options.Length);
        return options[index];
    }

    private static string EnsureSentence(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        return trimmed[^1] is '.' or '!' or '?'
            ? trimmed
            : trimmed + ".";
    }
}
