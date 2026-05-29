using System.Globalization;
using System.Text;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Packs;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Prompt-building helpers: resolves speaker name, formats area
//            ranges, stitches memory + relationship hints + personality
//            packs + world snapshot into the prompt that Chat.Inference
//            sends downstream. Pure formatting; no model calls.
//   surface: PalLlmRuntime.ResolveSpeakerName,
//            PalLlmRuntime.FormatAreaRange,
//            PalLlmRuntime.BuildPromptInputs (all private).
//   gate:    tests/PalLLM.Tests/PalLlmRuntimeChatTests.cs +
//            tests/PalLLM.Tests/PromptBuilderTests.cs.
//   adr:     ADR 0001 (deterministic-first reply pipeline).
//   docs:    docs/DATAFLOW.md (Chat hot path), docs/PROMPT_CARDS.md
//            (deterministic-fallback strategies).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    private static string ResolveSpeakerName(
        GameCharacterSnapshot? character,
        ChatRequest request,
        string fallback) =>
        character?.DisplayName
            ?? (string.IsNullOrWhiteSpace(request.CharacterName) ? fallback : request.CharacterName);

    private static string FormatAreaRange(float? areaRange) =>
        areaRange.HasValue
            ? $" (area range {areaRange.Value.ToString("0.##", CultureInfo.InvariantCulture)})"
            : string.Empty;

    private static string TrimToLength(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (maxChars <= 0 || trimmed.Length <= maxChars)
        {
            return maxChars <= 0 ? string.Empty : trimmed;
        }

        if (maxChars <= 3)
        {
            return trimmed[..maxChars];
        }

        return trimmed[..(maxChars - 3)] + "...";
    }

    private static string? TrimAssistantMessage(string? message, out bool trimmed)
    {
        trimmed = false;
        if (message is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        string normalized = message.Trim();
        trimmed = normalized.Length > AssistantMessageHardCapChars;
        return TrimToLength(normalized, AssistantMessageHardCapChars);
    }

    private static string AppendStatusNotice(string statusMessage, string notice)
    {
        if (string.IsNullOrWhiteSpace(statusMessage))
        {
            return notice;
        }

        return statusMessage.EndsWith(".", StringComparison.Ordinal)
            ? $"{statusMessage} {notice}"
            : $"{statusMessage}. {notice}";
    }

    private static string FormatKnownBase(GameBaseSnapshot baseInfo) =>
        baseInfo.AreaRange.HasValue
            ? $"{baseInfo.BaseId} (range {baseInfo.AreaRange.Value.ToString("0.##", CultureInfo.InvariantCulture)})"
            : baseInfo.BaseId;

    private static string FormatLatestProduction(ProductionStatusSnapshot status)
    {
        string baseLabel = string.IsNullOrWhiteSpace(status.BaseId) ? "the base" : status.BaseId;
        string state = string.IsNullOrWhiteSpace(status.Status) ? "completed" : status.Status;
        string station = string.IsNullOrWhiteSpace(status.Station) ? string.Empty : $" at {status.Station}";
        string item = status.Quantity > 0 && !string.IsNullOrWhiteSpace(status.Item)
            ? $": {status.Quantity}x {status.Item}"
            : string.Empty;
        return $"{state}{station} in {baseLabel}{item}";
    }

    private static string FormatLatestTravel(TravelStatusSnapshot status)
    {
        string origin = string.IsNullOrWhiteSpace(status.Origin) ? "unknown" : status.Origin;
        string destination = string.IsNullOrWhiteSpace(status.Destination) ? "unknown" : status.Destination;
        string mode = string.IsNullOrWhiteSpace(status.Mode) ? "on_foot" : status.Mode;
        string waypoint = string.IsNullOrWhiteSpace(status.Waypoint) ? string.Empty : $" via {status.Waypoint}";
        return $"{origin} -> {destination}{waypoint} ({mode})";
    }

    private static string BuildSystemPrompt(
        GameWorldSnapshot snapshot,
        GameCharacterSnapshot? character,
        NarrativeCharacterProfile? lore,
        IReadOnlyList<ConversationMemoryMatch> memoryMatches,
        CharacterRelationship? relationship,
        string taskTag,
        bool preferTaskFocus,
        string visualContext,
        PromptContextBudget promptBudget,
        string visualContextSource = "vision_model")
    {
        var builder = new StringBuilder(1_024);
        builder.AppendLine("You are PalLLM, a local-first Palworld roleplay and companion layer.");
        builder.AppendLine("Stay grounded in the game world, avoid inventing unsupported mechanics, and keep replies practical.");
        if (preferTaskFocus)
        {
            // Deflanderization directive (arxiv 2510.13586): keep the character voice but
            // refuse to let performative shtick crowd out the actual player ask.
            builder.AppendLine("Stay in character, but resolve the player's ask first - do not lean on roleplay at the expense of the concrete task.");
        }

        AppendStableCharacterContext(builder, character, promptBudget);
        AppendLoreContext(builder, lore, promptBudget);

        builder.AppendLine();
        builder.AppendLine("Turn context:");
        builder.AppendLine($"Task tag: {taskTag}");
        builder.AppendLine($"World loaded: {snapshot.IsWorldLoaded}; World: {snapshot.WorldName}");

        string boundedVisualContext = TrimToLength(visualContext, promptBudget.MaxVisualContextChars);
        if (!string.IsNullOrWhiteSpace(boundedVisualContext))
        {
            // Vision augmentation. Source label tracks whether the description
            // came from the configured multimodal model or from the deterministic
            // snapshot fallback - prompt-level transparency so the model can
            // weight the context appropriately.
            string sourceLabel = visualContextSource switch
            {
                "snapshot_fallback" => "from snapshot fallback",
                _ => "from vision model",
            };
            builder.AppendLine($"Visual context ({sourceLabel}): {boundedVisualContext}");
        }

        AppendWorldContext(builder, snapshot, promptBudget);
        AppendCharacterStateContext(builder, character);
        AppendRelationshipContext(builder, relationship);
        AppendMemoryContext(builder, memoryMatches, promptBudget);

        string prompt = builder.ToString().Trim();
        int effectivePromptCap = Math.Clamp(promptBudget.MaxPromptChars, 1_024, PromptHardCapChars);
        if (prompt.Length <= effectivePromptCap)
        {
            return prompt;
        }

        // Keep the cache-stable header (role + identity + authored lore) and
        // the memory tail, drop the middle. The tail is what recent mutations
        // most affect, and the header carries the persona. Better than
        // summarising to one line.
        int headerBudget = effectivePromptCap / 3;
        int tailBudget = effectivePromptCap - headerBudget - 3;
        return prompt[..headerBudget] + "..." + prompt[^tailBudget..];
    }

    private static void AppendRelationshipContext(StringBuilder builder, CharacterRelationship? relationship)
    {
        if (relationship is null)
        {
            return;
        }

        string moodLabel = relationship.Mood switch
        {
            RelationshipMood.Hostile => "resentful and guarded",
            RelationshipMood.Cold => "wary and short with you",
            RelationshipMood.Neutral => "polite but not especially close",
            RelationshipMood.Warm => "friendly and glad to help",
            RelationshipMood.Attached => "deeply loyal and affectionate",
            _ => "neutral",
        };

        builder.AppendLine();
        builder.AppendLine(
            $"Relationship: {relationship.CharacterName} is {moodLabel} (affinity {relationship.Affinity}, {relationship.InteractionCount} exchanges). " +
            $"Let that colour tone and phrasing without stealing focus from the task.");
    }

    private static void AppendWorldContext(StringBuilder builder, GameWorldSnapshot snapshot, PromptContextBudget promptBudget)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.TimeOfDay) ||
            !string.IsNullOrWhiteSpace(snapshot.Weather) ||
            !string.IsNullOrWhiteSpace(snapshot.Biome))
        {
            builder.AppendLine($"Scene: time={snapshot.TimeOfDay}; weather={snapshot.Weather}; biome={snapshot.Biome}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CurrentObjective))
        {
            builder.AppendLine($"Objective: {snapshot.CurrentObjective}");
        }

        if (snapshot.ActiveBaseIds.Count > 0)
        {
            builder.AppendLine($"Active bases: {string.Join(", ", snapshot.ActiveBaseIds.Take(promptBudget.MaxKnownBases))}");
        }

        if (snapshot.KnownBases.Count > 0)
        {
            string knownBases = string.Join(
                ", ",
                snapshot.KnownBases
                    .Take(promptBudget.MaxKnownBases)
                    .Select(FormatKnownBase));
            builder.AppendLine($"Known bases: {knownBases}");
        }

        if (snapshot.LastProduction is not null)
        {
            builder.AppendLine($"Latest production: {FormatLatestProduction(snapshot.LastProduction)}");
        }

        if (snapshot.LastTravel is not null)
        {
            builder.AppendLine($"Latest travel: {FormatLatestTravel(snapshot.LastTravel)}");
        }

        if (snapshot.NearbyHostiles.Count > 0)
        {
            builder.AppendLine($"Nearby hostiles: {string.Join(", ", snapshot.NearbyHostiles.Take(promptBudget.MaxNearbyHostiles))}");
        }

        if (snapshot.NearbyResources.Count > 0)
        {
            builder.AppendLine($"Nearby resources: {string.Join(", ", snapshot.NearbyResources.Take(promptBudget.MaxNearbyResources))}");
        }

        if (snapshot.RecentEvents.Count > 0)
        {
            builder.AppendLine($"Recent world events: {string.Join(", ", snapshot.RecentEvents.Take(promptBudget.MaxRecentEvents))}");
        }
    }

    private static void AppendStableCharacterContext(StringBuilder builder, GameCharacterSnapshot? character, PromptContextBudget promptBudget)
    {
        if (character is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"Active character: {character.DisplayName} ({character.Species})");
        if (character.Traits.Count > 0)
        {
            builder.AppendLine($"Traits: {string.Join(", ", character.Traits.Take(promptBudget.MaxCharacterTraits))}");
        }

        if (character.Skills.Count > 0)
        {
            builder.AppendLine($"Skills: {string.Join(", ", character.Skills.Take(promptBudget.MaxCharacterSkills).Select(skill => $"{skill.Key} {skill.Value}"))}");
        }
    }

    private static void AppendCharacterStateContext(StringBuilder builder, GameCharacterSnapshot? character)
    {
        if (character is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"Character state: alive={character.IsAlive}; playerFaction={character.IsPlayerFaction}; incapacitated={character.IsIncapacitated}");
        if (!string.IsNullOrWhiteSpace(character.Role) || !string.IsNullOrWhiteSpace(character.CurrentTask))
        {
            builder.AppendLine($"Role: {character.Role}; Current task: {character.CurrentTask}");
        }
    }

    private static void AppendLoreContext(StringBuilder builder, NarrativeCharacterProfile? lore, PromptContextBudget promptBudget)
    {
        if (lore is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Narrative pack context:");
        builder.AppendLine($"Role: {lore.Role}");
        builder.AppendLine($"Personality: {lore.Personality}");
        builder.AppendLine($"Backstory: {lore.Backstory}");
        if (lore.Traits.Count > 0)
        {
            builder.AppendLine($"Authored traits: {string.Join(", ", lore.Traits.Take(promptBudget.MaxLoreTraits))}");
        }
    }

    private static void AppendMemoryContext(StringBuilder builder, IReadOnlyList<ConversationMemoryMatch> memoryMatches, PromptContextBudget promptBudget)
    {
        if (memoryMatches.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Relevant memory snippets:");
        foreach (ConversationMemoryMatch match in memoryMatches.Take(promptBudget.MaxMemorySnippets))
        {
            builder.AppendLine($"- {match.Entry.Content}");
        }
    }
}
