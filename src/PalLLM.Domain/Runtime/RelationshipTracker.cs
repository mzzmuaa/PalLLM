using System.Text.RegularExpressions;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Per-character affinity and mood tracker. A light sentiment heuristic over the
/// player's message updates the character's affinity score, which is then
/// surfaced in the prompt so the companion reads the room instead of restarting
/// every conversation from zero. Deterministic and local — no model call needed.
/// </summary>
public sealed class RelationshipTracker
{
    private const int AffinityMin = -100;
    private const int AffinityMax = 100;
    private const int MaxDeltaPerInteraction = 5;

    /// Soft cap on the number of tracked relationships. Long-running sessions with
    /// many transient or synthetic character ids would otherwise grow the
    /// dictionary forever. When the soft cap is exceeded, eviction prefers the
    /// oldest, least-interacted entries and preserves strong affinity.
    private const int MaxTrackedRelationships = 256;
    private const int EvictionTarget = 192;

    private static readonly Regex PositivePattern = new(
        @"\b(?:thanks|thank\s+you|please|sorry|trust|love|great|awesome|amazing|good\s+job|well\s+done|appreciate|proud|brave|loyal|friend|together|beside\s+me|with\s+me)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NegativePattern = new(
        @"\b(?:hate|stupid|useless|idiot|pathetic|worst|dumb|garbage|trash|shut\s+up|leave\s+me|abandon|betray)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Dictionary<int, CharacterRelationship> _relationships = [];
    private long _mutationVersion;

    /// <summary>Same dirty-tracking primitive as <c>ConversationMemoryStore</c> —
    /// bumps on every Record/Import so autosave can skip unchanged state.</summary>
    public long MutationVersion => Interlocked.Read(ref _mutationVersion);

    public CharacterRelationship RecordInteraction(
        int characterId,
        string characterName,
        string message,
        DateTimeOffset timestamp)
    {
        int delta = DeriveValenceDelta(message);

        lock (_gate)
        {
            if (!_relationships.TryGetValue(characterId, out CharacterRelationship? existing))
            {
                existing = new CharacterRelationship
                {
                    CharacterId = characterId,
                    CharacterName = characterName,
                    Affinity = 0,
                    InteractionCount = 0,
                    FirstInteractionUtc = timestamp,
                    LastInteractionUtc = timestamp,
                };
            }

            int affinity = Math.Clamp(existing.Affinity + delta, AffinityMin, AffinityMax);
            CharacterRelationship updated = existing with
            {
                CharacterName = string.IsNullOrWhiteSpace(characterName) ? existing.CharacterName : characterName,
                Affinity = affinity,
                Mood = DeriveMood(affinity),
                LastTone = DeriveTone(delta),
                LastInteractionUtc = timestamp,
                InteractionCount = existing.InteractionCount + 1,
            };

            _relationships[characterId] = updated;
            EnforceRetention(protectId: characterId);
            Interlocked.Increment(ref _mutationVersion);
            return updated;
        }
    }

    /// Soft-capped retention. When the dictionary grows past the cap, prune to the
    /// eviction target by dropping entries with the lowest retention score —
    /// recency + interaction count + |affinity|. Always preserves the entry that
    /// was just written so the caller never sees its own record disappear.
    private void EnforceRetention(int? protectId = null)
    {
        if (_relationships.Count <= MaxTrackedRelationships)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<(int Id, double Score)> scored = _relationships
            .Select(pair => (pair.Key, Score: RetentionScore(pair.Value, now)))
            .OrderBy(tuple => tuple.Score)
            .ToList();

        int evicted = 0;
        int needed = _relationships.Count - EvictionTarget;
        foreach ((int id, _) in scored)
        {
            if (evicted >= needed)
            {
                break;
            }
            if (protectId.HasValue && id == protectId.Value)
            {
                continue;
            }

            _relationships.Remove(id);
            evicted++;
        }
    }

    private static double RetentionScore(CharacterRelationship relationship, DateTimeOffset now)
    {
        double hoursSinceLastInteraction = Math.Max(0, (now - relationship.LastInteractionUtc).TotalHours);
        double recencyScore = 1.0 / (1.0 + hoursSinceLastInteraction);
        double affinityScore = Math.Abs(relationship.Affinity) / 100.0;
        double volumeScore = Math.Min(1.0, relationship.InteractionCount / 20.0);
        return (recencyScore * 0.5) + (affinityScore * 0.3) + (volumeScore * 0.2);
    }

    public CharacterRelationship? TryGet(int? characterId)
    {
        if (!characterId.HasValue)
        {
            return null;
        }

        lock (_gate)
        {
            return _relationships.TryGetValue(characterId.Value, out CharacterRelationship? value) ? value : null;
        }
    }

    public IReadOnlyList<CharacterRelationship> Snapshot()
    {
        lock (_gate)
        {
            return _relationships.Values
                .OrderByDescending(r => r.Affinity)
                .ToArray();
        }
    }

    /// Replaces the current relationship map with the supplied records. Used by session
    /// restore. Records with an invalid character id (zero) are skipped.
    public void Import(IEnumerable<CharacterRelationship> relationships)
    {
        ArgumentNullException.ThrowIfNull(relationships);
        lock (_gate)
        {
            _relationships.Clear();
            foreach (CharacterRelationship relationship in relationships)
            {
                if (relationship is null || relationship.CharacterId == 0)
                {
                    continue;
                }

                _relationships[relationship.CharacterId] = relationship;
            }
        }

        Interlocked.Increment(ref _mutationVersion);
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _relationships.Count;
            }
        }
    }

    internal static int DeriveValenceDelta(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return 0;
        }

        int positiveHits = PositivePattern.Matches(message).Count;
        int negativeHits = NegativePattern.Matches(message).Count;
        int raw = (positiveHits * 2) - (negativeHits * 3);
        return Math.Clamp(raw, -MaxDeltaPerInteraction, MaxDeltaPerInteraction);
    }

    private static RelationshipMood DeriveMood(int affinity) => affinity switch
    {
        <= -60 => RelationshipMood.Hostile,
        <= -20 => RelationshipMood.Cold,
        < 20 => RelationshipMood.Neutral,
        < 60 => RelationshipMood.Warm,
        _ => RelationshipMood.Attached,
    };

    private static InteractionTone DeriveTone(int delta) => delta switch
    {
        <= -3 => InteractionTone.Harsh,
        < 0 => InteractionTone.Cool,
        0 => InteractionTone.Neutral,
        < 3 => InteractionTone.Warm,
        _ => InteractionTone.Affectionate,
    };
}

public sealed record CharacterRelationship
{
    public int CharacterId { get; init; }

    public string CharacterName { get; init; } = string.Empty;

    /// Cumulative affinity in [-100, 100]. Negative values indicate resentment, positive values attachment.
    public int Affinity { get; init; }

    public RelationshipMood Mood { get; init; } = RelationshipMood.Neutral;

    public InteractionTone LastTone { get; init; } = InteractionTone.Neutral;

    public int InteractionCount { get; init; }

    public DateTimeOffset FirstInteractionUtc { get; init; }

    public DateTimeOffset LastInteractionUtc { get; init; }
}

public enum RelationshipMood
{
    Hostile,
    Cold,
    Neutral,
    Warm,
    Attached,
}

public enum InteractionTone
{
    Harsh,
    Cool,
    Neutral,
    Warm,
    Affectionate,
}
