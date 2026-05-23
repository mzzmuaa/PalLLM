using System.Globalization;
using System.Text;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Inference;

/// <summary>
/// Deterministic scene-description fallback that composes a terse summary
/// from the live <see cref="GameWorldSnapshot"/> when the configured vision
/// model is disabled or unreachable.
///
/// <para>Used by <c>PalLlmRuntime.ChatAsync</c> when a chat request carries
/// an image but the vision pipeline cannot produce a description - instead
/// of dropping visual context entirely, the runtime splices this snapshot-
/// derived summary into the system prompt. The player still gets
/// situationally-aware replies even without a multimodal model running.</para>
///
/// <para>This is the vision counterpart to the 19 deterministic chat
/// strategies in <c>FallbackBehaviorEngine</c>: every "feasible item" has a
/// high-quality hard-coded fallback so companions never feel broken when a
/// model is unavailable.</para>
/// </summary>
public static class SnapshotVisionFallback
{
    /// <summary>
    /// Compose a 1-3 sentence scene description. Returns an empty string
    /// when the snapshot has no meaningful data - callers then know there
    /// is no visual context to splice and can skip augmentation entirely.
    /// </summary>
    public static string Compose(GameWorldSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsWorldLoaded)
        {
            return string.Empty;
        }

        var sentences = new List<string>();

        string locationClause = ComposeLocationClause(snapshot);
        if (!string.IsNullOrEmpty(locationClause))
        {
            sentences.Add(locationClause);
        }

        string companyClause = ComposeCompanyClause(snapshot);
        if (!string.IsNullOrEmpty(companyClause))
        {
            sentences.Add(companyClause);
        }

        string threatClause = ComposeThreatClause(snapshot);
        if (!string.IsNullOrEmpty(threatClause))
        {
            sentences.Add(threatClause);
        }

        string objectiveClause = ComposeObjectiveClause(snapshot);
        if (!string.IsNullOrEmpty(objectiveClause))
        {
            sentences.Add(objectiveClause);
        }

        return string.Join(" ", sentences).Trim();
    }

    private static string ComposeLocationClause(GameWorldSnapshot snapshot)
    {
        // "Night at base Kindling Hollow in the Tropical Zone with clear weather."
        var parts = new List<string>();

        string timeOfDay = TitleCaseOrEmpty(snapshot.TimeOfDay);
        if (!string.IsNullOrEmpty(timeOfDay))
        {
            parts.Add(timeOfDay);
        }

        bool inBase = snapshot.IsInBase == true;
        string? baseLabel = null;
        if (inBase)
        {
            // Prefer an active-id match from KnownBases if present; fall back to the
            // first active id directly. BaseId is typically a short human-friendly
            // handle ("forest-camp", "main-base") - safe to surface as-is.
            baseLabel = snapshot.KnownBases
                .FirstOrDefault(b => snapshot.ActiveBaseIds.Contains(b.BaseId))?.BaseId
                ?? snapshot.ActiveBaseIds.FirstOrDefault();
        }

        string locationPhrase = inBase
            ? (string.IsNullOrWhiteSpace(baseLabel) ? "at the base" : $"at base {baseLabel}")
            : "in the wild";

        string biome = TitleCaseOrEmpty(snapshot.Biome);
        if (!string.IsNullOrEmpty(biome))
        {
            locationPhrase += $" in the {biome} biome";
        }

        string weather = snapshot.Weather?.Trim().ToLowerInvariant() ?? string.Empty;
        if (!string.IsNullOrEmpty(weather))
        {
            locationPhrase += $" with {weather} weather";
        }

        if (parts.Count > 0)
        {
            // "Night at base Kindling Hollow"
            return $"{parts[0]} {locationPhrase}.";
        }

        return $"{FirstLetterUpper(locationPhrase)}.";
    }

    private static string ComposeCompanyClause(GameWorldSnapshot snapshot)
    {
        List<string> palNames = snapshot.Characters
            .Where(c => !string.IsNullOrWhiteSpace(c.DisplayName))
            .Select(c => c.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int friendlyCount = snapshot.NearbyFriendlies.Count;
        int total = Math.Max(palNames.Count, friendlyCount);
        if (total == 0)
        {
            return "Alone.";
        }

        if (palNames.Count == 0)
        {
            // Only friendly counts, no named pals.
            string noun = friendlyCount == 1 ? "ally" : "allies";
            return $"{friendlyCount} {noun} nearby.";
        }

        if (palNames.Count == 1 && friendlyCount <= 1)
        {
            return $"{palNames[0]} is nearby.";
        }

        if (palNames.Count <= 2)
        {
            string joined = string.Join(" and ", palNames);
            int extra = Math.Max(0, friendlyCount - palNames.Count);
            return extra > 0
                ? $"{joined} and {extra} other{(extra == 1 ? string.Empty : "s")} nearby."
                : $"{joined} nearby.";
        }

        // Three or more: name the first two, summarise the rest.
        string firstTwo = $"{palNames[0]} and {palNames[1]}";
        int remainder = palNames.Count - 2 + Math.Max(0, friendlyCount - palNames.Count);
        return remainder > 0
            ? $"{firstTwo} plus {remainder} other{(remainder == 1 ? string.Empty : "s")} nearby."
            : $"{firstTwo} nearby.";
    }

    private static string ComposeThreatClause(GameWorldSnapshot snapshot)
    {
        int hostileCount = snapshot.NearbyHostiles.Count;
        if (hostileCount > 0)
        {
            string label = hostileCount == 1
                ? snapshot.NearbyHostiles[0]
                : $"{hostileCount} hostiles";
            return hostileCount == 1
                ? $"{label} threat detected."
                : $"{label} threatening the area.";
        }

        // When no hostiles but ThreatLevel is elevated, still surface it.
        if (snapshot.ThreatLevel is float threat && threat >= 0.5f)
        {
            return threat >= 0.8f
                ? "High threat level in the area."
                : "Elevated threat level in the area.";
        }

        float? alert = snapshot.AlertLevel;
        if (alert is float a && a >= 0.5f)
        {
            return "On alert.";
        }

        return "No hostiles.";
    }

    private static string ComposeObjectiveClause(GameWorldSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.CurrentObjective))
        {
            return string.Empty;
        }

        string objective = snapshot.CurrentObjective.Trim();
        // Avoid duplicating any trailing period.
        if (!objective.EndsWith('.') && !objective.EndsWith('!') && !objective.EndsWith('?'))
        {
            objective += ".";
        }
        return $"Current objective: {objective}";
    }

    private static string TitleCaseOrEmpty(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Use invariant culture because Palworld snapshots use English
        // tokens; avoiding culture-dependent capitalisation keeps output
        // stable across machines.
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());
    }

    private static string FirstLetterUpper(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        return char.ToUpperInvariant(value[0]) + value[1..];
    }
}
