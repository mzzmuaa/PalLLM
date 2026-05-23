using System.Text.Json;
using PalLLM.Domain;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 40 / C8 — deterministic aggregator that rolls per-session
/// <see cref="CharacterRelationship"/> records into a lifetime view
/// that survives session resets. Each time a session is saved,
/// <see cref="Merge"/> extends the lifetime record (first-seen,
/// last-seen, session-count, peak/floor affinity, cumulative
/// mood-tally) for every tracked character. A <see cref="Summarise"/>
/// pass renders the aggregated data into a human + machine-readable
/// life-story record the dashboard can display.
///
/// <para>Pure function over in-memory records. Persistence is a
/// sibling concern: callers write the aggregate to
/// <c>{PalSavedRoot}/Runtime/LifetimeRelationships/latest.json</c>
/// after <see cref="Merge"/>. On load, call
/// <see cref="Deserialize(string)"/> to rebuild.</para>
/// </summary>
public static class LifetimeRelationshipAggregator
{
    /// <summary>
    /// Merge one session's relationship records into the lifetime
    /// aggregate. Returns a new aggregate — inputs are not mutated.
    /// </summary>
    public static LifetimeRelationshipAggregate Merge(
        LifetimeRelationshipAggregate prior,
        IReadOnlyList<CharacterRelationship> sessionRelationships,
        DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(prior);
        ArgumentNullException.ThrowIfNull(sessionRelationships);

        DateTimeOffset stamp = now ?? DateTimeOffset.UtcNow;
        var updated = new Dictionary<int, LifetimeRelationship>(prior.Characters.Count);
        foreach (LifetimeRelationship existing in prior.Characters)
        {
            updated[existing.CharacterId] = existing;
        }

        foreach (CharacterRelationship rel in sessionRelationships)
        {
            if (updated.TryGetValue(rel.CharacterId, out LifetimeRelationship? prev))
            {
                int sessionCount = prev.SessionCount + 1;
                int peak = Math.Max(prev.PeakAffinity, rel.Affinity);
                int floor = Math.Min(prev.FloorAffinity, rel.Affinity);
                long cumulative = prev.CumulativeAffinity + rel.Affinity;
                var moodTally = new Dictionary<string, int>(prev.MoodTally);
                string moodKey = rel.Mood.ToString();
                moodTally[moodKey] = moodTally.GetValueOrDefault(moodKey) + 1;

                updated[rel.CharacterId] = new LifetimeRelationship(
                    CharacterId: rel.CharacterId,
                    CharacterName: string.IsNullOrWhiteSpace(rel.CharacterName) ? prev.CharacterName : rel.CharacterName,
                    FirstSeenUtc: prev.FirstSeenUtc,
                    LastSeenUtc: stamp,
                    SessionCount: sessionCount,
                    CurrentAffinity: rel.Affinity,
                    PeakAffinity: peak,
                    FloorAffinity: floor,
                    CumulativeAffinity: cumulative,
                    MoodTally: moodTally);
            }
            else
            {
                var moodTally = new Dictionary<string, int>
                {
                    [rel.Mood.ToString()] = 1,
                };
                updated[rel.CharacterId] = new LifetimeRelationship(
                    CharacterId: rel.CharacterId,
                    CharacterName: rel.CharacterName,
                    FirstSeenUtc: stamp,
                    LastSeenUtc: stamp,
                    SessionCount: 1,
                    CurrentAffinity: rel.Affinity,
                    PeakAffinity: rel.Affinity,
                    FloorAffinity: rel.Affinity,
                    CumulativeAffinity: rel.Affinity,
                    MoodTally: moodTally);
            }
        }

        return new LifetimeRelationshipAggregate(
            CapturedAtUtc: stamp,
            Characters: updated.Values.OrderBy(c => c.CharacterId).ToArray());
    }

    /// <summary>
    /// Render a human + AI-readable life-story record for one
    /// character. Computes the average affinity across observed
    /// sessions and picks the dominant mood bucket.
    /// </summary>
    public static LifetimeRelationshipSummary Summarise(LifetimeRelationship record)
    {
        ArgumentNullException.ThrowIfNull(record);

        double average = record.SessionCount == 0
            ? 0.0
            : (double)record.CumulativeAffinity / record.SessionCount;
        string dominantMood = record.MoodTally.Count == 0
            ? "Neutral"
            : record.MoodTally.OrderByDescending(kv => kv.Value).First().Key;

        string lifeStory =
            $"{record.CharacterName}: known since {record.FirstSeenUtc:yyyy-MM-dd}, across {record.SessionCount} session(s); "
            + $"peak affinity {record.PeakAffinity}, floor {record.FloorAffinity}, "
            + $"average {average:F1}; dominant mood \"{dominantMood}\".";

        return new LifetimeRelationshipSummary(
            CharacterId: record.CharacterId,
            CharacterName: record.CharacterName,
            AverageAffinity: average,
            DominantMood: dominantMood,
            LifeStory: lifeStory,
            Source: record);
    }

    /// <summary>
    /// Deserialise a previously-written aggregate JSON back into memory.
    /// Returns an empty aggregate if the file doesn't exist or parses
    /// as null.
    /// </summary>
    public static LifetimeRelationshipAggregate Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty();
        }
        try
        {
            LifetimeRelationshipAggregate? parsed = JsonSerializer.Deserialize(
                json,
                SerializerJsonContext.LifetimeRelationshipAggregate);
            return parsed ?? Empty();
        }
        catch (JsonException)
        {
            return Empty();
        }
    }

    /// <summary>
    /// Deserialise a previously-written aggregate JSON stream back into memory.
    /// Returns an empty aggregate if the stream contains a JSON null payload.
    /// Throws <see cref="JsonException"/> for malformed JSON so bounded callers can
    /// surface a stable failure category.
    /// </summary>
    public static LifetimeRelationshipAggregate Deserialize(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        LifetimeRelationshipAggregate? parsed = JsonSerializer.Deserialize(
            stream,
            SerializerJsonContext.LifetimeRelationshipAggregate);
        return parsed ?? Empty();
    }

    /// <summary>Serialise an aggregate to canonical JSON.</summary>
    public static string Serialize(LifetimeRelationshipAggregate aggregate)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        return JsonSerializer.Serialize(aggregate, SerializerJsonContext.LifetimeRelationshipAggregate);
    }

    /// <summary>Empty aggregate factory.</summary>
    public static LifetimeRelationshipAggregate Empty()
        => new(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Characters: Array.Empty<LifetimeRelationship>());

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly PalLlmDomainJsonSerializerContext SerializerJsonContext = new(SerializerOptions);
}

/// <summary>
/// Aggregate across every character PalLLM has tracked.
/// </summary>
public sealed record LifetimeRelationshipAggregate(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<LifetimeRelationship> Characters);

/// <summary>
/// Lifetime record for a single character.
/// </summary>
public sealed record LifetimeRelationship(
    int CharacterId,
    string CharacterName,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    int SessionCount,
    int CurrentAffinity,
    int PeakAffinity,
    int FloorAffinity,
    long CumulativeAffinity,
    IReadOnlyDictionary<string, int> MoodTally);

/// <summary>
/// Human + AI-readable rendering of a single
/// <see cref="LifetimeRelationship"/>.
/// </summary>
public sealed record LifetimeRelationshipSummary(
    int CharacterId,
    string CharacterName,
    double AverageAffinity,
    string DominantMood,
    string LifeStory,
    LifetimeRelationship Source);
