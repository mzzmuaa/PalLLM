using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Maps the current fallback strategy to a safe, advisory <see cref="ActionIntent"/>.
///
/// Safe means: the action is reversible or easily rejected by the game side, the
/// intent carries no internal game-state mutation, and every suggested type has
/// to be on the operator's allowlist before PalLLM emits it. The runtime never
/// acts on its own intents — it only recommends. This keeps Phase 6 automation
/// non-destructive by construction.
/// </summary>
internal static class ActionIntentPlanner
{
    /// <summary>
    /// Returns an intent when the strategy has a clean safe-action mapping AND the
    /// operator has opted the type into <see cref="AutomationOptions.AllowedActions"/>.
    /// Returns null otherwise — strategies that are pure tactical commentary (e.g.
    /// hero-moment, nemesis-counterplay, morale-rally) do not produce intents.
    /// </summary>
    public static ActionIntent? Plan(
        FallbackBehaviorContext context,
        FallbackBehaviorDecision decision,
        AutomationOptions automation)
    {
        if (!automation.Enabled || automation.AllowedActions.Count == 0)
        {
            return null;
        }

        (string type, Dictionary<string, string> args, int priority, string justification)? candidate =
            decision.StrategyId switch
            {
                "retreat-and-rally" => (
                    "recall_pals",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = "combat_retreat",
                        ["anchor"] = context.PrimaryBaseLabel,
                        ["pal_group"] = "party",
                        ["status_change"] = "regroup_called",
                        ["mode"] = "defensive_regroup",
                    },
                    90,
                    "Peak phase with low morale or outnumbered — pulling pals back reduces exposure."),

                "perimeter-lockdown" => (
                    "recall_pals",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = "base_defense",
                        ["anchor"] = context.PrimaryBaseLabel,
                        ["pal_group"] = "party",
                        ["status_change"] = "recall_defense_line",
                        ["mode"] = "base_lockdown",
                    },
                    85,
                    "Hostiles at the base perimeter — bring pals inside the defensive line."),

                "safe-travel" => (
                    "waypoint_suggest",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = "safe_route",
                        ["bias"] = "anchor_to_anchor",
                        ["origin"] = context.InBase ? context.PrimaryBaseLabel : "current_position",
                        ["destination"] = context.HasObjective ? context.CurrentObjectiveLabel : context.SecondaryBaseLabel,
                        ["waypoint"] = context.InBase ? context.SecondaryBaseLabel : context.PrimaryBaseLabel,
                        ["mode"] = "guided_route",
                    },
                    60,
                    "Route advice asked — suggest an anchor-to-anchor path."),

                "harvest-window" => (
                    "waypoint_suggest",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = "resource_gather",
                        ["resource"] = ExtractResourceLabel(context),
                        ["origin"] = context.InBase ? context.PrimaryBaseLabel : "current_position",
                        ["destination"] = ExtractResourceLabel(context),
                        ["waypoint"] = context.PrimaryBaseLabel,
                        ["mode"] = "gather_loop",
                    },
                    55,
                    "Harvest window open — suggest the nearest safe resource pocket."),

                "objective-push" => (
                    "waypoint_suggest",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = "objective",
                        ["origin"] = context.InBase ? context.PrimaryBaseLabel : "current_position",
                        ["destination"] = context.CurrentObjectiveLabel,
                        ["waypoint"] = context.FocusThreat,
                        ["mode"] = "staged_push",
                    },
                    70,
                    $"Buildup around {context.CurrentObjectiveLabel} — suggest the staged approach."),

                "base-network" => (
                    "request_craft_queue",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["reason"] = "specialization",
                        ["primary_base"] = context.PrimaryBaseLabel,
                        ["secondary_base"] = context.SecondaryBaseLabel,
                        ["station"] = "logistics_planner",
                        ["item"] = "specialization_queue",
                        ["quantity"] = "1",
                        ["status"] = "requested",
                    },
                    65,
                    "Multi-base logistics — suggest specialization across bases."),

                _ => null,
            };

        if (candidate is null)
        {
            return null;
        }

        (string type, Dictionary<string, string> args, int priority, string justification) value = candidate.Value;

        // Operator allowlist is the final gate — even an internally-wired mapping
        // stays dormant until the operator explicitly permits the action type.
        if (!automation.AllowedActions.Any(a => string.Equals(a, value.type, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        return new ActionIntent
        {
            Type = value.type,
            Arguments = value.args,
            Priority = value.priority,
            Justification = value.justification,
            SourceStrategy = decision.StrategyId,
        };
    }

    private static string ExtractResourceLabel(FallbackBehaviorContext context)
    {
        string resource = context.ResourceHint.Trim();
        if (string.IsNullOrWhiteSpace(resource))
        {
            return "nearest_resource";
        }

        return resource.StartsWith("near ", StringComparison.OrdinalIgnoreCase)
            ? resource["near ".Length..].Trim()
            : resource;
    }
}
