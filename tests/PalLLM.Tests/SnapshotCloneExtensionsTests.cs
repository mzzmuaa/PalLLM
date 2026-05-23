using PalLLM.Domain.Integration;

namespace PalLLM.Tests;

// Pass 303 - direct unit tests for the world-snapshot deep-clone +
// base-discovery merge helpers. These are the surfaces that produce
// the immutable snapshots the fallback engine, prompt builder, vision
// orchestrator, and dashboard all consume. A regression that
// accidentally shared a reference between original and clone, or that
// stopped capping recent events at 12, or that broke the
// case-insensitive base-id match, would silently corrupt downstream
// state (the fallback engine would see the wrong base history; the
// dashboard would show stale entries; concurrent workers would observe
// a mid-edit snapshot).
//
// Until this pass the helpers were only covered indirectly via
// bridge-event integration paths. The deep-copy guarantees, null-input
// resilience, base-discovery merge semantics, and recent-event capping
// + dedup had no direct fast-feedback coverage.
public sealed class SnapshotCloneExtensionsTests
{
    // ---------- CloneDeep ----------

    [Test]
    public void CloneDeep_ReturnsNewInstance_WithSameScalarValues()
    {
        var original = new GameWorldSnapshot
        {
            Source = "bridge",
            WorldName = "Palpagos",
            IsWorldLoaded = true,
            CurrentTick = 12345,
            ThreatLevel = 0.42f,
            PlayerHealthFraction = 0.83f,
            CurrentObjective = "find shards",
        };

        var clone = original.CloneDeep();

        Assert.That(clone, Is.Not.SameAs(original));
        Assert.That(clone.Source, Is.EqualTo("bridge"));
        Assert.That(clone.WorldName, Is.EqualTo("Palpagos"));
        Assert.That(clone.IsWorldLoaded, Is.True);
        Assert.That(clone.CurrentTick, Is.EqualTo(12345));
        Assert.That(clone.ThreatLevel, Is.EqualTo(0.42f));
        Assert.That(clone.PlayerHealthFraction, Is.EqualTo(0.83f));
        Assert.That(clone.CurrentObjective, Is.EqualTo("find shards"));
    }

    [Test]
    public void CloneDeep_NestedLists_AreIndependent()
    {
        var original = new GameWorldSnapshot
        {
            ActiveBaseIds = ["A", "B"],
            NearbyHostiles = ["wolf"],
            RecentEvents = ["raid", "discovered:X"],
        };

        var clone = original.CloneDeep();

        // Mutating the clone's lists must not affect the original.
        ((List<string>)clone.ActiveBaseIds!).Add("C");
        ((List<string>)clone.NearbyHostiles!).Clear();

        Assert.That(original.ActiveBaseIds, Is.EqualTo(new[] { "A", "B" }));
        Assert.That(original.NearbyHostiles, Is.EqualTo(new[] { "wolf" }));
        Assert.That(clone.ActiveBaseIds, Contains.Item("C"));
        Assert.That(clone.NearbyHostiles, Is.Empty);
    }

    [Test]
    public void CloneDeep_KnownBases_DeepCopiedNotShared()
    {
        var original = new GameWorldSnapshot
        {
            KnownBases =
            [
                new GameBaseSnapshot { BaseId = "outpost-1", AreaRange = 50f, Source = "bridge" },
                new GameBaseSnapshot { BaseId = "outpost-2", AreaRange = 75f, Source = "manual" },
            ],
        };

        var clone = original.CloneDeep();

        Assert.That(clone.KnownBases, Has.Count.EqualTo(2));
        Assert.That(clone.KnownBases[0], Is.Not.SameAs(original.KnownBases[0]),
            "Each GameBaseSnapshot must be cloned, not aliased.");
        Assert.That(clone.KnownBases[0].BaseId, Is.EqualTo("outpost-1"));
        Assert.That(clone.KnownBases[0].AreaRange, Is.EqualTo(50f));
    }

    [Test]
    public void CloneDeep_NullableNestedObjects_PreservedAsNull()
    {
        var original = new GameWorldSnapshot
        {
            LastTravel = null,
            LastProduction = null,
        };

        var clone = original.CloneDeep();

        Assert.That(clone.LastTravel, Is.Null);
        Assert.That(clone.LastProduction, Is.Null);
    }

    [Test]
    public void CloneDeep_PopulatedNestedObjects_DeepCopied()
    {
        var original = new GameWorldSnapshot
        {
            LastTravel = new TravelStatusSnapshot
            {
                Origin = "Home",
                Destination = "Outpost",
                Mode = "walk",
            },
            LastProduction = new ProductionStatusSnapshot
            {
                BaseId = "outpost-1",
                Item = "iron_ore",
                Quantity = 7,
            },
        };

        var clone = original.CloneDeep();

        Assert.That(clone.LastTravel, Is.Not.Null);
        Assert.That(clone.LastTravel, Is.Not.SameAs(original.LastTravel));
        Assert.That(clone.LastTravel!.Origin, Is.EqualTo("Home"));
        Assert.That(clone.LastTravel.Destination, Is.EqualTo("Outpost"));

        Assert.That(clone.LastProduction, Is.Not.Null);
        Assert.That(clone.LastProduction, Is.Not.SameAs(original.LastProduction));
        Assert.That(clone.LastProduction!.BaseId, Is.EqualTo("outpost-1"));
        Assert.That(clone.LastProduction.Item, Is.EqualTo("iron_ore"));
        Assert.That(clone.LastProduction.Quantity, Is.EqualTo(7));
    }

    [Test]
    public void CloneDeep_Characters_DeepCopiedWithSkillsAndNeeds()
    {
        var original = new GameWorldSnapshot
        {
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 1,
                    DisplayName = "Pal-A",
                    Skills = new Dictionary<string, int> { ["mining"] = 5 },
                    Needs = new Dictionary<string, float> { ["sleep"] = 0.3f },
                    Loadout = ["pickaxe"],
                    Position = new Vector3Snapshot { X = 1, Y = 2, Z = 3 },
                },
            ],
        };

        var clone = original.CloneDeep();

        Assert.That(clone.Characters, Has.Count.EqualTo(1));
        var clonedChar = clone.Characters[0];
        Assert.That(clonedChar, Is.Not.SameAs(original.Characters[0]));
        Assert.That(clonedChar.Skills, Is.Not.SameAs(original.Characters[0].Skills));
        Assert.That(clonedChar.Skills["mining"], Is.EqualTo(5));
        Assert.That(clonedChar.Needs["sleep"], Is.EqualTo(0.3f));
        Assert.That(clonedChar.Loadout, Is.EqualTo(new[] { "pickaxe" }));
        Assert.That(clonedChar.Position!.X, Is.EqualTo(1));
    }

    [Test]
    public void CloneDeep_NullCharacterPosition_DefaultsToZeroVector()
    {
        // The clone substitutes a fresh Vector3 when the source position is null,
        // so downstream consumers can always dereference Position without a guard.
        var original = new GameWorldSnapshot
        {
            Characters = [new GameCharacterSnapshot { Id = 1, Position = null! }],
        };

        var clone = original.CloneDeep();

        Assert.That(clone.Characters[0].Position, Is.Not.Null);
        Assert.That(clone.Characters[0].Position!.X, Is.EqualTo(0));
        Assert.That(clone.Characters[0].Position.Y, Is.EqualTo(0));
        Assert.That(clone.Characters[0].Position.Z, Is.EqualTo(0));
    }

    [Test]
    public void CloneDeep_DictionaryWithWhitespaceKey_DropsTheBadEntry()
    {
        // CloneDictionary silently drops keys that are null/whitespace —
        // matching the runtime's "never propagate unusable keys" posture.
        var original = new GameWorldSnapshot
        {
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 1,
                    Skills = new Dictionary<string, int>
                    {
                        ["mining"] = 5,
                        ["   "] = 99,
                    },
                },
            ],
        };

        var clone = original.CloneDeep();

        Assert.That(clone.Characters[0].Skills, Has.Count.EqualTo(1));
        Assert.That(clone.Characters[0].Skills, Does.Not.ContainKey("   "));
    }

    [Test]
    public void CloneDeep_DictionaryComparison_IsCaseInsensitive()
    {
        // The cloned dictionary uses OrdinalIgnoreCase, so callers can
        // look up either casing without surprises.
        var original = new GameWorldSnapshot
        {
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 1,
                    Skills = new Dictionary<string, int> { ["Mining"] = 5 },
                },
            ],
        };

        var clone = original.CloneDeep();

        Assert.That(clone.Characters[0].Skills["mining"], Is.EqualTo(5));
        Assert.That(clone.Characters[0].Skills["MINING"], Is.EqualTo(5));
    }

    // ---------- WithBaseDiscovery: new base ----------

    [Test]
    public void WithBaseDiscovery_NewBaseId_AddsToActiveAndKnown()
    {
        var snapshot = new GameWorldSnapshot { Source = "bridge" };
        var when = new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);

        var next = snapshot.WithBaseDiscovery("outpost-1", areaRange: 100f, when, source: "bridge");

        Assert.That(next.ActiveBaseIds, Contains.Item("outpost-1"));
        Assert.That(next.KnownBases, Has.Count.EqualTo(1));
        Assert.That(next.KnownBases[0].BaseId, Is.EqualTo("outpost-1"));
        Assert.That(next.KnownBases[0].AreaRange, Is.EqualTo(100f));
        Assert.That(next.KnownBases[0].FirstSeenUtc, Is.EqualTo(when));
        Assert.That(next.KnownBases[0].LastSeenUtc, Is.EqualTo(when));
        Assert.That(next.KnownBases[0].Source, Is.EqualTo("bridge"));
        Assert.That(next.CapturedAtUtc, Is.EqualTo(when));
    }

    [Test]
    public void WithBaseDiscovery_EmptySource_DefaultsToBridge()
    {
        var snapshot = new GameWorldSnapshot();
        var when = DateTimeOffset.UtcNow;

        var next = snapshot.WithBaseDiscovery("outpost-1", areaRange: 75f, when, source: "");

        Assert.That(next.KnownBases[0].Source, Is.EqualTo("bridge"));
    }

    [Test]
    public void WithBaseDiscovery_AddsBaseDiscoveredEventToRecent()
    {
        var snapshot = new GameWorldSnapshot();

        var next = snapshot.WithBaseDiscovery("outpost-1", areaRange: 50f,
            DateTimeOffset.UtcNow, source: "bridge");

        Assert.That(next.RecentEvents, Has.Some.EqualTo("base_discovered:outpost-1"));
        Assert.That(next.RecentEvents[0], Is.EqualTo("base_discovered:outpost-1"),
            "New event must land at the front (index 0).");
    }

    // ---------- WithBaseDiscovery: existing base ----------

    [Test]
    public void WithBaseDiscovery_ExistingBase_UpdatesLastSeenButPreservesFirstSeen()
    {
        var first = new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var second = new DateTimeOffset(2026, 5, 21, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new GameWorldSnapshot
        {
            KnownBases =
            [
                new GameBaseSnapshot
                {
                    BaseId = "outpost-1",
                    AreaRange = 50f,
                    FirstSeenUtc = first,
                    LastSeenUtc = first,
                    Source = "original",
                },
            ],
            ActiveBaseIds = ["outpost-1"],
        };

        var next = snapshot.WithBaseDiscovery("outpost-1", areaRange: 80f, second, source: "bridge");

        Assert.That(next.KnownBases, Has.Count.EqualTo(1));
        Assert.That(next.KnownBases[0].BaseId, Is.EqualTo("outpost-1"));
        Assert.That(next.KnownBases[0].AreaRange, Is.EqualTo(80f), "New AreaRange overrides existing.");
        Assert.That(next.KnownBases[0].FirstSeenUtc, Is.EqualTo(first),
            "FirstSeenUtc must be preserved across re-discoveries.");
        Assert.That(next.KnownBases[0].LastSeenUtc, Is.EqualTo(second));
    }

    [Test]
    public void WithBaseDiscovery_ExistingBase_NullAreaRange_PreservesPriorValue()
    {
        var snapshot = new GameWorldSnapshot
        {
            KnownBases =
            [
                new GameBaseSnapshot
                {
                    BaseId = "outpost-1",
                    AreaRange = 50f,
                    Source = "bridge",
                },
            ],
        };

        var next = snapshot.WithBaseDiscovery("outpost-1", areaRange: null,
            DateTimeOffset.UtcNow, source: "bridge");

        Assert.That(next.KnownBases[0].AreaRange, Is.EqualTo(50f),
            "Null AreaRange must NOT erase the existing value.");
    }

    [Test]
    public void WithBaseDiscovery_BaseIdMatchingIsCaseInsensitive()
    {
        var snapshot = new GameWorldSnapshot
        {
            KnownBases = [new GameBaseSnapshot { BaseId = "OUTPOST-A" }],
            ActiveBaseIds = ["OUTPOST-A"],
        };

        var next = snapshot.WithBaseDiscovery("outpost-a", areaRange: 30f,
            DateTimeOffset.UtcNow, source: "bridge");

        // Same base under a different casing must NOT be duplicated.
        Assert.That(next.KnownBases, Has.Count.EqualTo(1));
        Assert.That(next.ActiveBaseIds, Has.Count.EqualTo(1));
    }

    // ---------- ActiveBaseIds dedup + whitespace filter ----------

    [Test]
    public void WithBaseDiscovery_ActiveBaseIds_DropsWhitespaceAndDuplicates()
    {
        var snapshot = new GameWorldSnapshot
        {
            ActiveBaseIds = ["outpost-1", "  ", "OUTPOST-1", "", "outpost-2"],
        };

        var next = snapshot.WithBaseDiscovery("outpost-3", areaRange: 50f,
            DateTimeOffset.UtcNow, source: "bridge");

        Assert.That(next.ActiveBaseIds, Has.Count.EqualTo(3),
            "Whitespace and case-duplicates must be filtered.");
        Assert.That(next.ActiveBaseIds, Contains.Item("outpost-3"));
    }

    // ---------- RecentEvents capping + dedup ----------

    [Test]
    public void WithBaseDiscovery_RecentEvents_CappedAt12()
    {
        // Seed with 12 unrelated events; adding a new one must push the
        // oldest off the back, keeping total at 12.
        List<string> seed = [];
        for (int i = 0; i < 12; i++)
        {
            seed.Add($"event-{i}");
        }

        var snapshot = new GameWorldSnapshot { RecentEvents = seed };

        var next = snapshot.WithBaseDiscovery("outpost-X", areaRange: 50f,
            DateTimeOffset.UtcNow, source: "bridge");

        Assert.That(next.RecentEvents, Has.Count.EqualTo(12));
        Assert.That(next.RecentEvents[0], Is.EqualTo("base_discovered:outpost-X"));
        Assert.That(next.RecentEvents, Does.Not.Contain("event-11"),
            "Oldest event must have been dropped to keep cap at 12.");
    }

    [Test]
    public void WithBaseDiscovery_DuplicateEvent_MovedToFront()
    {
        var snapshot = new GameWorldSnapshot
        {
            RecentEvents = ["base_discovered:outpost-1", "raid", "weather:rain"],
        };

        var next = snapshot.WithBaseDiscovery("outpost-1", areaRange: 50f,
            DateTimeOffset.UtcNow, source: "bridge");

        Assert.That(next.RecentEvents, Has.Count.EqualTo(3),
            "Existing duplicate must be removed before re-insertion (no count growth).");
        Assert.That(next.RecentEvents[0], Is.EqualTo("base_discovered:outpost-1"));
        Assert.That(next.RecentEvents.Count(e => e == "base_discovered:outpost-1"),
            Is.EqualTo(1), "Event must appear exactly once.");
    }

    [Test]
    public void WithBaseDiscovery_RecentEvents_WhitespaceEntriesDropped()
    {
        var snapshot = new GameWorldSnapshot
        {
            RecentEvents = ["raid", "  ", "weather:rain", ""],
        };

        var next = snapshot.WithBaseDiscovery("outpost-1", areaRange: 50f,
            DateTimeOffset.UtcNow, source: "bridge");

        Assert.That(next.RecentEvents, Does.Not.Contain("  "));
        Assert.That(next.RecentEvents, Does.Not.Contain(""));
        Assert.That(next.RecentEvents, Contains.Item("raid"));
        Assert.That(next.RecentEvents, Contains.Item("weather:rain"));
    }
}
