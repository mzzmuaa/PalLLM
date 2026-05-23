namespace PalLLM.Domain.Memory;

/// <summary>
/// Deterministic memory-reflection trigger. An LLM-based reflection step would be
/// expensive and non-reproducible, so PalLLM reflects without calling the model:
/// when accumulated importance over the recent window crosses a threshold, the top
/// salient entries are consolidated into a single high-importance memory so
/// retrieval can surface them as one unit later.
/// </summary>
public sealed class ReflectionService
{
    // Threshold tuned for PalLLM-style session pacing: ~10 salient events worth of
    // accumulated importance in the recent window. Game sessions are shorter than
    // the long-horizon simulations this pattern was popularised for, so the bar is
    // set proportionally lower.
    private const float AccumulatedImportanceThreshold = 8.0f;
    private const int Window = 20;
    private const int MinEntries = 8;
    private const int MinEntriesBetweenReflections = 8;
    private const int MaxConsolidationBullets = 4;
    private const int ExcerptMaxChars = 160;
    private const float SalientEntryImportanceFloor = 0.6f;

    private readonly ConversationMemoryStore _memory;

    public ReflectionService(ConversationMemoryStore memory)
    {
        _memory = memory;
    }

    /// <summary>
    /// If the recent memory stream for <paramref name="characterId"/> has crossed the
    /// importance threshold and no reflection has been produced within the cooldown
    /// window, synthesises a new reflection entry and persists it to the memory store.
    /// Returns <c>null</c> when no reflection is warranted.
    /// </summary>
    public ConversationMemoryEntry? TryReflect(int? characterId, string characterName)
    {
        if (_memory.Count < MinEntries)
        {
            return null;
        }

        IReadOnlyList<ConversationMemoryEntry> cooldownWindow =
            _memory.GetRecent(MinEntriesBetweenReflections, characterId);
        if (cooldownWindow.Any(IsReflectionEntry))
        {
            return null;
        }

        float accumulated = _memory.AccumulatedImportance(Window, characterId);
        if (accumulated < AccumulatedImportanceThreshold)
        {
            return null;
        }

        IReadOnlyList<ConversationMemoryEntry> recent = _memory.GetRecent(Window, characterId);
        List<ConversationMemoryEntry> salient = recent
            .Where(entry => entry.Importance >= SalientEntryImportanceFloor && !IsReflectionEntry(entry))
            .OrderByDescending(entry => entry.Importance)
            .ThenByDescending(entry => entry.CreatedAtUtc)
            .Take(MaxConsolidationBullets)
            .ToList();

        if (salient.Count < 2)
        {
            return null;
        }

        string summary = BuildReflectionContent(salient);
        string[] themeTags = salient
            .SelectMany(entry => entry.Tags)
            .Where(tag => !string.Equals(tag, "assistant_reply", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(tag, "player_input", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .Select(tag => $"reflection-theme:{tag}")
            .ToArray();

        string[] tags = ["reflection", .. themeTags];
        string speakerName = string.IsNullOrWhiteSpace(characterName) ? "Memory" : characterName;
        return _memory.Remember(characterId, speakerName, "system", summary, tags);
    }

    private static bool IsReflectionEntry(ConversationMemoryEntry entry) =>
        entry.Tags.Any(tag => string.Equals(tag, "reflection", StringComparison.OrdinalIgnoreCase));

    private static string BuildReflectionContent(IReadOnlyList<ConversationMemoryEntry> salient)
    {
        IEnumerable<string> bullets = salient.Select(entry =>
        {
            string? tagHint = entry.Tags.FirstOrDefault(tag =>
                !string.Equals(tag, "assistant_reply", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(tag, "player_input", StringComparison.OrdinalIgnoreCase));
            string excerpt = Excerpt(entry.Content, ExcerptMaxChars);
            return string.IsNullOrWhiteSpace(tagHint)
                ? $"- {excerpt}"
                : $"- [{tagHint}] {excerpt}";
        });

        return "Reflection on recent salient moments:\n" + string.Join("\n", bullets);
    }

    private static string Excerpt(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
        {
            return content;
        }

        int cut = content.LastIndexOf(' ', Math.Min(maxChars, content.Length - 1));
        if (cut < maxChars / 2)
        {
            cut = maxChars;
        }

        return content[..cut].TrimEnd() + "…";
    }
}
