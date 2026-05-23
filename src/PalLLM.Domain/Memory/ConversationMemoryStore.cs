using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PalLLM.Domain.Portable;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Bounded conversation memory store. Records recent player /
//            companion exchanges per character with a hard cap (2000
//            entries, 4 KiB content per entry) so a long-running sidecar
//            never grows unbounded.
//            Surfaces the recent window into the chat hot path so replies
//            stay coherent across turns. Recall combines deterministic
//            embeddings, recency, importance, character affinity, and a tiny
//            exact-token rerank signal so named events survive hash-bucket ties.
//   surface: ConversationMemoryStore (Append / GetRecent / Snapshot /
//            Clear). Persisted via Session.EnableAutosave on a timer.
//   gate:    None directly; behaviour pinned by ConversationMemoryStore
//            tests in tests/PalLLM.Tests.
//   adr:     None directly; bounded-store invariant enforced inline.
//   docs:    docs/HARVEST.md (memory-store harvest recipe),
//            docs/ARCHITECTURE.md ("Memory" section),
//            docs/TUNING.md (Session.MaxPersistedBytes knob).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Memory;

public sealed class ConversationMemoryStore
{
    private const int MaxEntries = 2_000;
    public const int MaxContentChars = 4 * 1024;

    // Retrieval uses a weighted sum of semantic similarity, recency, and importance.
    // PalLLM adds a deterministic character-affinity boost so replies stay rooted in
    // the addressee's history.
    private const float SemanticWeight = 0.55f;
    private const float RecencyWeight = 0.25f;
    private const float ImportanceWeight = 0.20f;
    private const float LexicalRerankWeight = 0.10f;
    private const float CharacterBoost = 0.15f;
    private const double RecencyHalfLifeHours = 12.0;
    private const int MaxQueryLexicalTokens = 32;
    private const int MinLexicalTokenLength = 3;

    private readonly object _gate = new();
    private readonly List<ConversationMemoryEntry> _entries = [];
    private long _mutationVersion;

    /// Monotonically-increasing version number that bumps on every write
    /// (Remember, Import). Used by session autosave to skip disk writes when
    /// nothing has changed since the last flush.
    public long MutationVersion => Interlocked.Read(ref _mutationVersion);

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public ConversationMemoryEntry Remember(
        int? characterId,
        string characterName,
        string speakerRole,
        string content,
        params string[] tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        string[] normalizedTags = tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string trimmedContent = TrimContent(content);
        var entry = new ConversationMemoryEntry
        {
            CharacterId = characterId,
            CharacterName = string.IsNullOrWhiteSpace(characterName) ? "Unknown" : characterName,
            SpeakerRole = string.IsNullOrWhiteSpace(speakerRole) ? "system" : speakerRole,
            Content = trimmedContent,
            Tags = normalizedTags,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Embedding = SemanticEmbedder.FallbackEmbed(trimmedContent),
            Importance = MemoryImportance.Derive(trimmedContent, speakerRole, normalizedTags),
        };

        lock (_gate)
        {
            _entries.Add(entry);
            int overage = _entries.Count - MaxEntries;
            if (overage > 0)
            {
                _entries.RemoveRange(0, overage);
            }
        }

        Interlocked.Increment(ref _mutationVersion);
        return entry;
    }

    public IReadOnlyList<ConversationMemoryMatch> Recall(string query, int? characterId, int limit)
    {
        int maxResults = Math.Max(1, limit);
        float[] queryEmbedding = string.IsNullOrWhiteSpace(query)
            ? []
            : SemanticEmbedder.FallbackEmbed(query);
        Span<uint> queryTokenHashes = stackalloc uint[MaxQueryLexicalTokens];
        int queryTokenCount = CollectLexicalTokenHashes(query.AsSpan(), queryTokenHashes);

        ConversationMemoryEntry[] snapshot = RentSnapshot(out int count);
        if (count == 0)
        {
            return [];
        }

        try
        {
            int targetCount = Math.Min(maxResults, count);
            var topEntries = new ConversationMemoryEntry[targetCount];
            var topScores = new float[targetCount];
            int selected = 0;
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Callers typically ask for a tiny result set (for example, the top 5
            // memory matches during ChatAsync), so keep only the current top-N
            // instead of sorting the entire bounded store on every recall.
            for (int i = 0; i < count; i++)
            {
                ConversationMemoryEntry entry = snapshot[i];
                float score = Score(entry, queryEmbedding, queryTokenHashes[..queryTokenCount], characterId, now);
                int insertIndex = FindInsertionIndex(score, entry.CreatedAtUtc, topScores, topEntries, selected);
                if (insertIndex >= targetCount)
                {
                    continue;
                }

                int copyCount = Math.Min(selected, targetCount - 1) - insertIndex;
                if (copyCount > 0)
                {
                    Array.Copy(topEntries, insertIndex, topEntries, insertIndex + 1, copyCount);
                    Array.Copy(topScores, insertIndex, topScores, insertIndex + 1, copyCount);
                }

                topEntries[insertIndex] = entry;
                topScores[insertIndex] = score;
                if (selected < targetCount)
                {
                    selected++;
                }
            }

            var matches = new ConversationMemoryMatch[selected];
            for (int i = 0; i < selected; i++)
            {
                matches[i] = new ConversationMemoryMatch(topEntries[i], topScores[i]);
            }

            return matches;
        }
        finally
        {
            ReturnSnapshot(snapshot);
        }
    }

    public IReadOnlyList<ConversationMemoryEntry> GetRecent(int limit, int? characterId = null)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return [];
            }

            int maxResults = Math.Min(Math.Max(1, limit), _entries.Count);
            var recent = new ConversationMemoryEntry[maxResults];
            int count = 0;
            Span<ConversationMemoryEntry> entries = CollectionsMarshal.AsSpan(_entries);

            // The store is capped at 2,000 entries and remains chronologically
            // ordered, so a reverse scan under the existing lock is cheaper than
            // snapshotting + re-sorting the full collection on every read.
            for (int i = entries.Length - 1; i >= 0 && count < maxResults; i--)
            {
                ConversationMemoryEntry entry = entries[i];
                if (characterId.HasValue &&
                    entry.CharacterId != characterId.Value &&
                    entry.CharacterId is not null)
                {
                    continue;
                }

                recent[count++] = entry;
            }

            if (count == 0)
            {
                return [];
            }

            if (count == recent.Length)
            {
                return recent;
            }

            var trimmed = new ConversationMemoryEntry[count];
            Array.Copy(recent, trimmed, count);
            return trimmed;
        }
    }

    /// Full ordered export of the current memory stream. Used by session persistence
    /// to serialise state to disk; callers that only need filtered recalls should use
    /// <see cref="Recall"/> or <see cref="GetRecent"/> instead.
    public IReadOnlyList<ConversationMemoryEntry> Export()
    {
        lock (_gate)
        {
            // Insertion and import both preserve chronological ordering, so export can
            // return the current buffer directly without an extra sort pass.
            return _entries.ToArray();
        }
    }

    /// Replaces the in-memory stream with the supplied entries. Used by session
    /// restore; keeps the <see cref="MaxEntries"/> cap by trimming oldest first
    /// so the invariant is preserved after load.
    public void Import(IEnumerable<ConversationMemoryEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var ordered = entries
            .Where(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.Content))
            .Select(NormalizeImportedEntry)
            .OrderBy(entry => entry.CreatedAtUtc)
            .ToList();

        lock (_gate)
        {
            _entries.Clear();
            int overage = ordered.Count - MaxEntries;
            int startIndex = overage > 0 ? overage : 0;
            for (int i = startIndex; i < ordered.Count; i++)
            {
                _entries.Add(ordered[i]);
            }
        }

        Interlocked.Increment(ref _mutationVersion);
    }

    /// Sum of importance scores over the most recent <paramref name="window"/> entries,
    /// optionally filtered by character. Used by the reflection trigger to decide when
    /// accumulated salience warrants consolidation.
    public float AccumulatedImportance(int window, int? characterId = null)
    {
        lock (_gate)
        {
            if (_entries.Count == 0)
            {
                return 0f;
            }

            int maxResults = Math.Max(1, window);
            int included = 0;
            float total = 0f;
            Span<ConversationMemoryEntry> entries = CollectionsMarshal.AsSpan(_entries);

            for (int i = entries.Length - 1; i >= 0 && included < maxResults; i--)
            {
                ConversationMemoryEntry entry = entries[i];
                if (characterId.HasValue &&
                    entry.CharacterId != characterId.Value &&
                    entry.CharacterId is not null)
                {
                    continue;
                }

                total += entry.Importance;
                included++;
            }

            return total;
        }
    }

    private ConversationMemoryEntry[] RentSnapshot(out int count)
    {
        lock (_gate)
        {
            count = _entries.Count;
            if (count == 0)
            {
                return [];
            }

            // Keep the pooled snapshot lifetime fully inside Recall so the rented
            // array never escapes the method boundary.
            ConversationMemoryEntry[] snapshot = ArrayPool<ConversationMemoryEntry>.Shared.Rent(count);
            CollectionsMarshal.AsSpan(_entries).CopyTo(snapshot.AsSpan(0, count));
            return snapshot;
        }
    }

    private static void ReturnSnapshot(ConversationMemoryEntry[] snapshot)
    {
        if (snapshot.Length == 0)
        {
            return;
        }

        ArrayPool<ConversationMemoryEntry>.Shared.Return(snapshot, clearArray: true);
    }

    private static ConversationMemoryEntry NormalizeImportedEntry(ConversationMemoryEntry entry)
    {
        string boundedContent = TrimContent(entry.Content);
        if (string.Equals(boundedContent, entry.Content, StringComparison.Ordinal))
        {
            return entry;
        }

        return new ConversationMemoryEntry
        {
            Id = entry.Id,
            CharacterId = entry.CharacterId,
            CharacterName = entry.CharacterName,
            SpeakerRole = entry.SpeakerRole,
            Content = boundedContent,
            Tags = entry.Tags,
            CreatedAtUtc = entry.CreatedAtUtc,
            Embedding = SemanticEmbedder.FallbackEmbed(boundedContent),
            Importance = entry.Importance,
        };
    }

    private static string TrimContent(string content)
    {
        string trimmed = content.Trim();
        if (trimmed.Length <= MaxContentChars)
        {
            return trimmed;
        }

        return trimmed[..(MaxContentChars - 3)] + "...";
    }

    private static int FindInsertionIndex(
        float score,
        DateTimeOffset createdAtUtc,
        float[] topScores,
        ConversationMemoryEntry[] topEntries,
        int selected)
    {
        int index = 0;
        while (index < selected)
        {
            float existingScore = topScores[index];
            ConversationMemoryEntry existingEntry = topEntries[index];
            if (score > existingScore)
            {
                break;
            }

            if (score == existingScore && createdAtUtc > existingEntry.CreatedAtUtc)
            {
                break;
            }

            index++;
        }

        return index;
    }

    private static float Score(
        ConversationMemoryEntry entry,
        float[] queryEmbedding,
        ReadOnlySpan<uint> queryTokenHashes,
        int? characterId,
        DateTimeOffset now)
    {
        float semantic = queryEmbedding.Length == 0
            ? 0f
            : SemanticEmbedder.CosineSimilarity(queryEmbedding, entry.Embedding);
        float lexical = LexicalOverlap(queryTokenHashes, entry.Content);

        float characterBoost = characterId.HasValue && characterId == entry.CharacterId ? CharacterBoost : 0f;
        double hoursOld = Math.Max(0.0, (now - entry.CreatedAtUtc).TotalHours);
        float recency = (float)(1.0 / (1.0 + (hoursOld / RecencyHalfLifeHours)));
        float importance = entry.Importance;

        return (semantic * SemanticWeight)
            + (recency * RecencyWeight)
            + (importance * ImportanceWeight)
            + (lexical * LexicalRerankWeight)
            + characterBoost;
    }

    private static int CollectLexicalTokenHashes(ReadOnlySpan<char> text, Span<uint> destination)
    {
        if (text.IsEmpty || destination.IsEmpty)
        {
            return 0;
        }

        int count = 0;
        int tokenStart = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool atEnd = i == text.Length;
            bool isSeparator = atEnd || !char.IsLetterOrDigit(text[i]);
            if (!isSeparator)
            {
                if (tokenStart < 0)
                {
                    tokenStart = i;
                }

                continue;
            }

            if (tokenStart < 0)
            {
                continue;
            }

            int tokenLength = i - tokenStart;
            if (tokenLength >= MinLexicalTokenLength)
            {
                uint hash = StableTokenHash(text.Slice(tokenStart, tokenLength));
                if (!ContainsHash(destination[..count], hash))
                {
                    destination[count++] = hash;
                    if (count == destination.Length)
                    {
                        return count;
                    }
                }
            }

            tokenStart = -1;
        }

        return count;
    }

    private static float LexicalOverlap(ReadOnlySpan<uint> queryTokenHashes, string? content)
    {
        if (queryTokenHashes.IsEmpty || string.IsNullOrEmpty(content))
        {
            return 0f;
        }

        ReadOnlySpan<char> text = content.AsSpan();
        uint matchedMask = 0;
        uint fullMask = queryTokenHashes.Length == 32
            ? uint.MaxValue
            : (1u << queryTokenHashes.Length) - 1u;
        int tokenStart = -1;

        for (int i = 0; i <= text.Length; i++)
        {
            bool atEnd = i == text.Length;
            bool isSeparator = atEnd || !char.IsLetterOrDigit(text[i]);
            if (!isSeparator)
            {
                if (tokenStart < 0)
                {
                    tokenStart = i;
                }

                continue;
            }

            if (tokenStart < 0)
            {
                continue;
            }

            int tokenLength = i - tokenStart;
            if (tokenLength >= MinLexicalTokenLength)
            {
                uint hash = StableTokenHash(text.Slice(tokenStart, tokenLength));
                for (int hashIndex = 0; hashIndex < queryTokenHashes.Length; hashIndex++)
                {
                    if (queryTokenHashes[hashIndex] != hash)
                    {
                        continue;
                    }

                    matchedMask |= 1u << hashIndex;
                    if (matchedMask == fullMask)
                    {
                        return 1f;
                    }

                    break;
                }
            }

            tokenStart = -1;
        }

        return BitOperations.PopCount(matchedMask) / (float)queryTokenHashes.Length;
    }

    private static bool ContainsHash(ReadOnlySpan<uint> hashes, uint candidate)
    {
        for (int i = 0; i < hashes.Length; i++)
        {
            if (hashes[i] == candidate)
            {
                return true;
            }
        }

        return false;
    }

    private static uint StableTokenHash(ReadOnlySpan<char> token)
    {
        uint hash = 2166136261u;
        for (int i = 0; i < token.Length; i++)
        {
            hash ^= char.ToLowerInvariant(token[i]);
            hash *= 16777619u;
        }

        return hash;
    }
}

/// <summary>
/// Deterministic importance scorer. An LLM-based salience estimator would be
/// expensive and non-reproducible — PalLLM derives an equivalent [0, 1] value
/// from content, role, and tag cues so the reflection trigger stays local-first
/// and testable.
/// </summary>
internal static class MemoryImportance
{
    private const float Baseline = 0.5f;

    private static readonly Regex SalientEventPattern = new(
        @"\b(?:killed|died|downed|wiped|saved|rescued|discovered|first|unlocked|defeated|betrayed|broke|destroyed|lost|won|triumphed|captured|escaped|learned|realized|swore|promised)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] HighSalienceTagFragments =
    [
        "base_discovered", "raid", "combat_start", "pal_down", "hero-moment",
        "rescue", "reflection", "weather_change", "boss_encounter", "capture",
    ];

    public static float Derive(string content, string speakerRole, IReadOnlyList<string> tags)
    {
        float importance = Baseline;

        if (string.Equals(speakerRole, "system", StringComparison.OrdinalIgnoreCase))
        {
            importance += 0.10f;
        }

        if (tags.Any(t => string.Equals(t, "reflection", StringComparison.OrdinalIgnoreCase)))
        {
            importance += 0.30f;
        }

        foreach (string fragment in HighSalienceTagFragments)
        {
            if (tags.Any(t => t.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            {
                importance += 0.15f;
                break;
            }
        }

        if (!string.IsNullOrEmpty(content) && SalientEventPattern.IsMatch(content))
        {
            importance += 0.15f;
        }

        if (content.Length < 20)
        {
            importance -= 0.10f;
        }

        return Math.Clamp(importance, 0f, 1f);
    }
}

public sealed class ConversationMemoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public int? CharacterId { get; init; }

    public string CharacterName { get; init; } = string.Empty;

    public string SpeakerRole { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public DateTimeOffset CreatedAtUtc { get; init; }

    public float[] Embedding { get; init; } = [];

    /// Salience score in [0, 1] derived at insert time. Used as the importance term
    /// of the retrieval score alongside semantic similarity and recency decay.
    public float Importance { get; init; } = 0.5f;
}

public sealed class ConversationMemoryMatch
{
    public ConversationMemoryMatch(ConversationMemoryEntry entry, float score)
    {
        Entry = entry;
        Score = score;
    }

    public ConversationMemoryEntry Entry { get; }

    public float Score { get; }
}
