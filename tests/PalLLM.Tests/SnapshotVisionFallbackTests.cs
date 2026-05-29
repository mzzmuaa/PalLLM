using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;

namespace PalLLM.Tests;

public sealed class SnapshotVisionFallbackTests
{
    [Test]
    public void Compose_WhenSnapshotIsNull_ReturnsEmpty()
    {
        Assert.That(SnapshotVisionFallback.Compose(null), Is.Empty);
    }

    [Test]
    public void Compose_WhenWorldNotLoaded_ReturnsEmpty()
    {
        // Pre-load or post-teardown â€” emitting a fake scene description
        // would mislead the model. Better to splice nothing.
        var snapshot = new GameWorldSnapshot { IsWorldLoaded = false };
        Assert.That(SnapshotVisionFallback.Compose(snapshot), Is.Empty);
    }

    [Test]
    public void Compose_WhenAtBaseWithTimeAndBiome_ProducesFullLocationClause()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            TimeOfDay = "night",
            Biome = "tropical",
            Weather = "clear",
            IsInBase = true,
            ActiveBaseIds = ["kindling-hollow"],
            KnownBases = [new GameBaseSnapshot { BaseId = "kindling-hollow" }],
        };

        string description = SnapshotVisionFallback.Compose(snapshot);

        Assert.That(description, Does.Contain("Night"));
        Assert.That(description, Does.Contain("kindling-hollow"));
        Assert.That(description, Does.Contain("Tropical"));
        Assert.That(description, Does.Contain("clear weather"));
    }

    [Test]
    public void Compose_WhenInTheWild_SaysInTheWild()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = false,
            TimeOfDay = "morning",
            Biome = "desert",
        };

        string description = SnapshotVisionFallback.Compose(snapshot);

        Assert.That(description, Does.Contain("in the wild"));
        Assert.That(description, Does.Contain("Morning"));
        Assert.That(description, Does.Contain("Desert"));
    }

    [Test]
    public void Compose_WhenOneNamedPalPresent_NamesIt()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot { Id = 1, DisplayName = "CampGuardian", Species = "CampGuardian" },
            ],
        };

        string description = SnapshotVisionFallback.Compose(snapshot);

        Assert.That(description, Does.Contain("CampGuardian is nearby"));
    }

    [Test]
    public void Compose_WhenTwoNamedPalsPresent_NamesBothJoined()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot { Id = 1, DisplayName = "CampGuardian" },
                new GameCharacterSnapshot { Id = 2, DisplayName = "TrailGuard" },
            ],
        };

        string description = SnapshotVisionFallback.Compose(snapshot);

        Assert.That(description, Does.Contain("CampGuardian and TrailGuard nearby"));
    }

    [Test]
    public void Compose_WhenManyPalsPresent_SummarisesRest()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot { Id = 1, DisplayName = "CampGuardian" },
                new GameCharacterSnapshot { Id = 2, DisplayName = "TrailGuard" },
                new GameCharacterSnapshot { Id = 3, DisplayName = "Tanzee" },
                new GameCharacterSnapshot { Id = 4, DisplayName = "Foxsparks" },
            ],
        };

        string description = SnapshotVisionFallback.Compose(snapshot);

        // Names the first two, summarises the rest with a count.
        Assert.That(description, Does.Contain("CampGuardian and TrailGuard plus 2 others"));
    }

    [Test]
    public void Compose_WhenNoCompanions_SaysAlone()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = false,
        };

        string description = SnapshotVisionFallback.Compose(snapshot);
        Assert.That(description, Does.Contain("Alone."));
    }

    [Test]
    public void Compose_WhenHostilesPresent_ReportsThreat()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = ["Syndicate Thug", "Syndicate Mage", "Bushi"],
        };

        string description = SnapshotVisionFallback.Compose(snapshot);
        Assert.That(description, Does.Contain("3 hostiles"));
    }

    [Test]
    public void Compose_WhenSingleHostile_UsesItsName()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = ["Syndicate Thug"],
        };

        string description = SnapshotVisionFallback.Compose(snapshot);
        Assert.That(description, Does.Contain("Syndicate Thug"));
    }

    [Test]
    public void Compose_WhenNoHostilesButHighThreatLevel_SurfacesThreat()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            ThreatLevel = 0.9f,
        };

        string description = SnapshotVisionFallback.Compose(snapshot);
        Assert.That(description, Does.Contain("High threat"));
    }

    [Test]
    public void Compose_WhenObjectiveSet_IncludesObjectiveClause()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            CurrentObjective = "Build the forest outpost",
        };

        string description = SnapshotVisionFallback.Compose(snapshot);
        Assert.That(description, Does.Contain("Current objective"));
        Assert.That(description, Does.Contain("Build the forest outpost"));
    }

    [Test]
    public void Compose_WhenObjectiveAlreadyPunctuated_DoesNotDoublePeriod()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            CurrentObjective = "Reach the dock!",
        };

        string description = SnapshotVisionFallback.Compose(snapshot);
        Assert.That(description, Does.Contain("Reach the dock!"));
        Assert.That(description, Does.Not.Contain("dock!."));
    }

    [Test]
    public void Compose_RichSnapshot_ProducesMultiSentenceDescription()
    {
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            TimeOfDay = "dusk",
            Biome = "tropical",
            Weather = "rainy",
            IsInBase = true,
            ActiveBaseIds = ["main-camp"],
            KnownBases = [new GameBaseSnapshot { BaseId = "main-camp" }],
            Characters =
            [
                new GameCharacterSnapshot { Id = 1, DisplayName = "CampGuardian" },
                new GameCharacterSnapshot { Id = 2, DisplayName = "TrailGuard" },
            ],
            NearbyHostiles = ["Syndicate Thug"],
            CurrentObjective = "Secure the perimeter",
        };

        string description = SnapshotVisionFallback.Compose(snapshot);

        // Should be multi-sentence, all clauses present.
        Assert.That(description, Does.Contain("Dusk"));
        Assert.That(description, Does.Contain("main-camp"));
        Assert.That(description, Does.Contain("rainy"));
        Assert.That(description, Does.Contain("CampGuardian and TrailGuard"));
        Assert.That(description, Does.Contain("Syndicate Thug"));
        Assert.That(description, Does.Contain("Secure the perimeter"));

        // Expect at least 3 sentences.
        int periods = description.Count(c => c == '.');
        Assert.That(periods, Is.GreaterThanOrEqualTo(3),
            $"Rich snapshot should compose to multiple sentences. Got: '{description}'");
    }

    [Test]
    public void Compose_IsStableAcrossInvocations()
    {
        // Deterministic by design â€” two identical snapshots must produce
        // identical strings. Turns the fallback into a reliable dev aid
        // for reproducing chat behaviour.
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            TimeOfDay = "noon",
            Biome = "tundra",
            Weather = "snowy",
            IsInBase = false,
            Characters =
            [
                new GameCharacterSnapshot { Id = 1, DisplayName = "Lifmunk" },
            ],
            CurrentObjective = "Find shelter",
        };

        string first = SnapshotVisionFallback.Compose(snapshot);
        string second = SnapshotVisionFallback.Compose(snapshot);
        Assert.That(first, Is.EqualTo(second));
    }
}
