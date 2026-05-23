using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar.Mcp;

/// <summary>
/// Model Context Protocol (MCP) prompt surface for PalLLM.
///
/// <para>Prompts are user-controlled templates - Claude Desktop surfaces
/// them as slash commands and command-palette entries. A prompt is a
/// pre-authored message list that the user can invoke with arguments,
/// and the host injects the result into the conversation.</para>
///
/// <para>PalLLM's prompts exist to get a new MCP user productive in one
/// click: pick "Companion chat", fill in the character name, and Claude
/// Desktop drops a fully-contextualised chat scaffold into the
/// conversation with the current world state and character profile
/// pre-loaded - no manual copy-paste from <c>pal_world_snapshot</c>.</para>
///
/// <para>Role conventions: MCP prompts only support <c>user</c> and
/// <c>assistant</c> roles (no <c>system</c>). PalLLM's prompts emit
/// context-setting as an <c>assistant</c> message - the host client
/// sees a model "opening turn" that frames the scene, followed by a
/// <c>user</c> message carrying the actual question. This matches the
/// MCP spec wire format exactly with no silent role coercion.</para>
/// </summary>
[McpServerPromptType]
public static class PalLlmMcpPrompts
{
    [McpServerPrompt(Name = "palllm_companion_chat")]
    [Description(
        "Open a conversation with a Pal companion. Injects the current world snapshot and the "
        + "character profile (if a characterId is supplied) as system context, then seeds the "
        + "user turn with whatever the player wants to say. Use this as a one-click way to chat "
        + "with a companion through Claude Desktop / VS Code with full game context already "
        + "loaded.")]
    public static IEnumerable<ChatMessage> CompanionChat(
        PalLlmRuntime runtime,
        [Description("What the player wants to say to the companion. Required.")]
        string userMessage,
        [Description("Optional numeric character id to target. Leave blank for the default companion.")]
        string? characterId = null)
    {
        GameWorldSnapshot snapshot = runtime.Adapter.Snapshot;
        GameCharacterSnapshot? character = null;
        if (!string.IsNullOrWhiteSpace(characterId)
            && int.TryParse(characterId, out int parsed))
        {
            character = snapshot.Characters.FirstOrDefault(c => c.Id == parsed);
        }

        StringBuilder systemBuilder = new();
        systemBuilder.AppendLine("You are a Pal companion in Palworld. Stay in character.");

        string scene = SnapshotVisionFallback.Compose(snapshot);
        if (!string.IsNullOrEmpty(scene))
        {
            systemBuilder.AppendLine();
            systemBuilder.AppendLine($"Scene: {scene}");
        }

        if (character is not null)
        {
            systemBuilder.AppendLine();
            systemBuilder.AppendLine(
                $"You are {character.DisplayName} (species: {character.Species}). Role: {character.Role}.");
            if (character.Traits is { Count: > 0 })
            {
                systemBuilder.AppendLine($"Traits: {string.Join(", ", character.Traits)}.");
            }
        }

        return new[]
        {
            new ChatMessage(ChatRole.Assistant, systemBuilder.ToString().TrimEnd()),
            new ChatMessage(ChatRole.User, userMessage),
        };
    }

    [McpServerPrompt(Name = "palllm_threat_analysis")]
    [Description(
        "Ask the model to analyse the player's current tactical situation using the live world "
        + "snapshot - nearby hostiles, threat / alert levels, player vitals, and active base "
        + "defences. Returns a system message framing the task plus a user turn asking for a "
        + "concise recommendation. Good for a quick 'what should I do right now?' check.")]
    public static IEnumerable<ChatMessage> ThreatAnalysis(PalLlmRuntime runtime)
    {
        GameWorldSnapshot snapshot = runtime.Adapter.Snapshot;

        StringBuilder sb = new();
        sb.AppendLine("You are a tactical advisor for a Palworld player.");
        sb.AppendLine("Analyse the current situation and recommend the safest high-utility action.");
        sb.AppendLine();
        sb.AppendLine($"Scene: {SnapshotVisionFallback.Compose(snapshot)}");
        sb.AppendLine($"Threat level: {snapshot.ThreatLevel?.ToString("0.00") ?? "unknown"}");
        sb.AppendLine($"Alert level: {snapshot.AlertLevel?.ToString("0.00") ?? "unknown"}");
        sb.AppendLine($"Player health: {snapshot.PlayerHealthFraction?.ToString("0%") ?? "unknown"}");
        sb.AppendLine($"Player stamina: {snapshot.PlayerStaminaFraction?.ToString("0%") ?? "unknown"}");
        sb.AppendLine($"Player hunger: {snapshot.PlayerHungerFraction?.ToString("0%") ?? "unknown"}");
        if (snapshot.NearbyHostiles.Count > 0)
        {
            sb.AppendLine($"Nearby hostiles: {string.Join(", ", snapshot.NearbyHostiles)}");
        }
        if (snapshot.NearbyFriendlies.Count > 0)
        {
            sb.AppendLine($"Nearby friendlies: {string.Join(", ", snapshot.NearbyFriendlies)}");
        }

        return new[]
        {
            new ChatMessage(ChatRole.Assistant, sb.ToString().TrimEnd()),
            new ChatMessage(ChatRole.User,
                "Give me a 3-sentence recommendation for my immediate next move."),
        };
    }

    [McpServerPrompt(Name = "palllm_base_status")]
    [Description(
        "Summarise every base PalLLM knows about - active vs dormant, recent production, and "
        + "whether the player is currently on-site. Ask the model to flag any bases that need "
        + "attention (idle production, undefended, or far from current location).")]
    public static IEnumerable<ChatMessage> BaseStatus(PalLlmRuntime runtime)
    {
        GameWorldSnapshot snapshot = runtime.Adapter.Snapshot;

        StringBuilder sb = new();
        sb.AppendLine("You are PalLLM's base-network strategist.");
        sb.AppendLine("Review the known base inventory and flag any base that needs attention.");
        sb.AppendLine();
        sb.AppendLine($"Player at base: {snapshot.IsInBase?.ToString() ?? "unknown"}");
        sb.AppendLine($"Active base ids: {(snapshot.ActiveBaseIds.Count == 0 ? "(none)" : string.Join(", ", snapshot.ActiveBaseIds))}");
        sb.AppendLine($"Known bases: {snapshot.KnownBases.Count}");
        foreach (GameBaseSnapshot baseEntry in snapshot.KnownBases)
        {
            sb.AppendLine(
                $"  - {baseEntry.BaseId} | range: {baseEntry.AreaRange?.ToString("0.0") ?? "?"} | last seen: {baseEntry.LastSeenUtc:O}");
        }

        return new[]
        {
            new ChatMessage(ChatRole.Assistant, sb.ToString().TrimEnd()),
            new ChatMessage(ChatRole.User,
                "Which bases should I prioritise and why? Keep the answer under five sentences."),
        };
    }

    [McpServerPrompt(Name = "palllm_model_collaboration_orchestrator")]
    [Description(
        "Creates a reusable Palworld-task orchestration scaffold from PalLLM's live model-collaboration snapshot. "
        + "Use this when you want the host model to decide whether the fast worker lane, the dense review lane, "
        + "or both should handle a PalLLM runtime, bridge, HUD, screenshot, docs-sync, or release-hardening task, "
        + "and to return a strict JSON operating plan.")]
    public static IEnumerable<ChatMessage> ModelCollaborationOrchestrator(
        ModelCollaborationPlanner planner,
        [Description("The task the duo should solve. Required.")]
        string task,
        [Description("Optional hardware note, e.g. 'RTX 4090 24GB + 128GB RAM' or 'dual 24GB GPUs'.")]
        string? hardware = null,
        [Description("Optional list of available quants or model variants on the machine.")]
        string? availableQuants = null,
        [Description("Optional context budget note, e.g. '35B at 32K, 27B at 64K'.")]
        string? contextBudget = null)
    {
        ModelCollaborationSnapshot snapshot = planner.GetSnapshot();
        ModelCollaborationModelDescriptor? fastLane = snapshot.ConfiguredModels
            .FirstOrDefault(model => string.Equals(model.OperatingStyle, "fast-iterative", StringComparison.Ordinal));
        ModelCollaborationModelDescriptor? deliberateLane = snapshot.ConfiguredModels
            .FirstOrDefault(model => string.Equals(model.OperatingStyle, "deliberate", StringComparison.Ordinal));

        StringBuilder sb = new();
        sb.AppendLine("You are the PalLLM model-collaboration orchestrator.");
        sb.AppendLine("Use the live collaboration snapshot below to choose the safest high-value operating plan for PalLLM's Palworld-mod work.");
        sb.AppendLine("Stay scoped to PalLLM runtime, bridge, HUD, screenshot, docs-sync, and release-hardening tasks.");
        sb.AppendLine();
        sb.AppendLine($"Current hardware class: {snapshot.Hardware.ClassId}");
        sb.AppendLine($"Hardware summary: {snapshot.Hardware.Summary}");
        if (fastLane is not null)
        {
            sb.AppendLine($"Fast lane: {fastLane.ModelId} ({fastLane.Architecture}, roles: {string.Join(", ", fastLane.PrimaryRoles)})");
        }

        if (deliberateLane is not null)
        {
            sb.AppendLine($"Deliberate lane: {deliberateLane.ModelId} ({deliberateLane.Architecture}, roles: {string.Join(", ", deliberateLane.PrimaryRoles)})");
        }

        if (snapshot.RoutingPolicies.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Default routing policies:");
            foreach (ModelTaskRoutingPolicy policy in snapshot.RoutingPolicies)
            {
                sb.AppendLine($"- {policy.Id}: {policy.PreferredFlow}");
            }
        }

        if (snapshot.QualificationSuite.Checks.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Promotion gates that matter before trusting a fresh quant:");
            foreach (ModelQualificationCheck check in snapshot.QualificationSuite.Checks.Take(5))
            {
                sb.AppendLine($"- {check.Id}: {check.MinimumEvidence}");
            }
        }

        if (snapshot.HardwarePlaybook.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Hardware playbook:");
            foreach (ModelHardwareTierPlaybook tier in snapshot.HardwarePlaybook)
            {
                sb.AppendLine($"- {tier.TierId}: {tier.RecommendedRunMode}; fast={tier.FastLaneQuantHint}; deliberate={tier.DeliberateLaneQuantHint}; ctx={tier.ContextGuidance}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("Return only JSON using this shape:");
        sb.AppendLine("{");
        sb.AppendLine("  \"strategy\": \"\",");
        sb.AppendLine("  \"why\": \"\",");
        sb.AppendLine("  \"35b_role\": \"\",");
        sb.AppendLine("  \"27b_role\": \"\",");
        sb.AppendLine("  \"run_mode\": \"parallel | sequential | one_model_only\",");
        sb.AppendLine("  \"thinking_mode\": { \"35b\": true, \"27b\": true },");
        sb.AppendLine("  \"preserve_thinking\": { \"35b\": false, \"27b\": true },");
        sb.AppendLine("  \"context_budget\": { \"35b\": \"\", \"27b\": \"\" },");
        sb.AppendLine("  \"quant_recommendation\": { \"35b\": \"\", \"27b\": \"\" },");
        sb.AppendLine("  \"validators\": [],");
        sb.AppendLine("  \"human_review_required\": false,");
        sb.AppendLine("  \"promotion_criteria\": [],");
        sb.AppendLine("  \"fallback\": \"\"");
        sb.AppendLine("}");

        StringBuilder user = new();
        user.AppendLine($"Task: {task}");
        if (!string.IsNullOrWhiteSpace(hardware))
        {
            user.AppendLine($"Hardware: {hardware}");
        }

        if (!string.IsNullOrWhiteSpace(availableQuants))
        {
            user.AppendLine($"Available quants: {availableQuants}");
        }

        if (!string.IsNullOrWhiteSpace(contextBudget))
        {
            user.AppendLine($"Context budget: {contextBudget}");
        }

        return new[]
        {
            new ChatMessage(ChatRole.Assistant, sb.ToString().TrimEnd()),
            new ChatMessage(ChatRole.User, user.ToString().TrimEnd()),
        };
    }
}
