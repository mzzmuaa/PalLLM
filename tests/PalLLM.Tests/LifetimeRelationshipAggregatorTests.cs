using NUnit.Framework;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 40 / C8 — regression coverage for
/// <see cref="LifetimeRelationshipAggregator"/>. Pinned contract:
/// <list type="bullet">
///   <item>Merging an empty aggregate with new relationships creates fresh lifetime records.</item>
///   <item>Re-merging the same character bumps session count, preserves first-seen, updates last-seen, tracks peak/floor.</item>
///   <item>Summarise() picks the dominant mood bucket and renders a plain-English life story.</item>
///   <item>Serialize/Deserialize round-trip preserves every field.</item>
/// </list>
/// </summary>
[TestFixture]
public class LifetimeRelationshipAggregatorTests
{
    [Test]
    public void Merge_FirstSession_CreatesFreshLifetimeRecords()
    {
        LifetimeRelationshipAggregate prior = LifetimeRelationshipAggregator.Empty();
        var rels = new[]
        {
            new CharacterRelationship { CharacterId = 1, CharacterName = "Lamball", Affinity = 20, Mood = RelationshipMood.Warm },
            new CharacterRelationship { CharacterId = 2, CharacterName = "Cattiva", Affinity = -5, Mood = RelationshipMood.Neutral },
        };
        DateTimeOffset now = new(2026, 4, 24, 12, 0, 0, TimeSpan.Zero);

        LifetimeRelationshipAggregate result = LifetimeRelationshipAggregator.Merge(prior, rels, now);

        Assert.That(result.Characters, Has.Count.EqualTo(2));
        var lamball = result.Characters.Single(c => c.CharacterId == 1);
        Assert.That(lamball.SessionCount, Is.EqualTo(1));
        Assert.That(lamball.FirstSeenUtc, Is.EqualTo(now));
        Assert.That(lamball.LastSeenUtc, Is.EqualTo(now));
        Assert.That(lamball.PeakAffinity, Is.EqualTo(20));
        Assert.That(lamball.FloorAffinity, Is.EqualTo(20));
    }

    [Test]
    public void Merge_SecondSession_BumpsCountAndTracksPeakFloor()
    {
        LifetimeRelationshipAggregate first = LifetimeRelationshipAggregator.Merge(
            LifetimeRelationshipAggregator.Empty(),
            new[]
            {
                new CharacterRelationship { CharacterId = 1, CharacterName = "Lamball", Affinity = 20, Mood = RelationshipMood.Warm },
            },
            new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero));

        LifetimeRelationshipAggregate second = LifetimeRelationshipAggregator.Merge(
            first,
            new[]
            {
                new CharacterRelationship { CharacterId = 1, CharacterName = "Lamball", Affinity = 80, Mood = RelationshipMood.Warm },
            },
            new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero));

        LifetimeRelationship lamball = second.Characters.Single();
        Assert.That(lamball.SessionCount, Is.EqualTo(2));
        Assert.That(lamball.FirstSeenUtc, Is.EqualTo(new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero)),
            "FirstSeenUtc must be preserved across merges.");
        Assert.That(lamball.LastSeenUtc, Is.EqualTo(new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero)));
        Assert.That(lamball.PeakAffinity, Is.EqualTo(80));
        Assert.That(lamball.FloorAffinity, Is.EqualTo(20));
        Assert.That(lamball.CurrentAffinity, Is.EqualTo(80));
        Assert.That(lamball.CumulativeAffinity, Is.EqualTo(100L));
    }

    [Test]
    public void Summarise_PicksDominantMoodAndRendersLifeStory()
    {
        LifetimeRelationship record = new(
            CharacterId: 1,
            CharacterName: "Lamball",
            FirstSeenUtc: new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastSeenUtc: new DateTimeOffset(2026, 4, 24, 0, 0, 0, TimeSpan.Zero),
            SessionCount: 10,
            CurrentAffinity: 80,
            PeakAffinity: 90,
            FloorAffinity: 20,
            CumulativeAffinity: 650,
            MoodTally: new Dictionary<string, int>
            {
                ["Warm"] = 6,
                ["Neutral"] = 4,
            });

        LifetimeRelationshipSummary summary = LifetimeRelationshipAggregator.Summarise(record);

        Assert.That(summary.AverageAffinity, Is.EqualTo(65.0));
        Assert.That(summary.DominantMood, Is.EqualTo("Warm"));
        Assert.That(summary.LifeStory, Does.Contain("Lamball"));
        Assert.That(summary.LifeStory, Does.Contain("10 session(s)"));
    }

    [Test]
    public void SerializeDeserialize_RoundTrips()
    {
        LifetimeRelationshipAggregate original = LifetimeRelationshipAggregator.Merge(
            LifetimeRelationshipAggregator.Empty(),
            new[]
            {
                new CharacterRelationship { CharacterId = 1, CharacterName = "Lamball", Affinity = 42, Mood = RelationshipMood.Warm },
            });

        string json = LifetimeRelationshipAggregator.Serialize(original);
        LifetimeRelationshipAggregate roundTripped = LifetimeRelationshipAggregator.Deserialize(json);

        Assert.That(roundTripped.Characters, Has.Count.EqualTo(1));
        Assert.That(roundTripped.Characters[0].CharacterName, Is.EqualTo("Lamball"));
        Assert.That(roundTripped.Characters[0].CurrentAffinity, Is.EqualTo(42));
    }

    [Test]
    public void Deserialize_MalformedJson_ReturnsEmpty()
    {
        LifetimeRelationshipAggregate result = LifetimeRelationshipAggregator.Deserialize("{not-json");

        Assert.That(result.Characters, Is.Empty);
    }
}
