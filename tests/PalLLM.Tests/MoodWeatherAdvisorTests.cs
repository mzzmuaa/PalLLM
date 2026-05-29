using NUnit.Framework;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 38 / C10 — regression coverage for
/// <see cref="MoodWeatherAdvisor"/>. Pinned contract:
/// <list type="bullet">
///   <item>High affinity + calm scene → affectionate / golden-hour.</item>
///   <item>Low affinity → uneasy / cold-front.</item>
///   <item>Combat overrides affinity to 'wary'.</item>
///   <item>Low player HP overrides to 'agitated'.</item>
///   <item>Night-time softens affectionate → content.</item>
///   <item>Hostile last-tone downgrades content → uneasy.</item>
/// </list>
/// </summary>
[TestFixture]
public class MoodWeatherAdvisorTests
{
    [Test]
    public void Forecast_HighAffinity_CalmScene_IsAffectionate()
    {
        var rel = BuildRel(affinity: 75, RelationshipMood.Warm, InteractionTone.Warm);
        var snap = new GameWorldSnapshot { IsWorldLoaded = true, TimeOfDay = "day" };

        MoodWeather mood = MoodWeatherAdvisor.Forecast(rel, snap);

        Assert.That(mood.Mood, Is.EqualTo("affectionate"));
        Assert.That(mood.Weather, Is.EqualTo("golden-hour"));
    }

    [Test]
    public void Forecast_LowAffinity_IsUneasy()
    {
        var rel = BuildRel(affinity: -60, RelationshipMood.Cold, InteractionTone.Harsh);
        var snap = new GameWorldSnapshot { IsWorldLoaded = true };

        MoodWeather mood = MoodWeatherAdvisor.Forecast(rel, snap);

        Assert.That(mood.Mood, Is.EqualTo("uneasy"));
        Assert.That(mood.Weather, Is.EqualTo("cold-front"));
    }

    [Test]
    public void Forecast_CombatInProgress_OverridesToWary()
    {
        var rel = BuildRel(affinity: 80, RelationshipMood.Warm, InteractionTone.Warm);
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = { "Bandit" },
        };

        MoodWeather mood = MoodWeatherAdvisor.Forecast(rel, snap);

        Assert.That(mood.Mood, Is.EqualTo("wary"),
            "Combat always beats base mood.");
        Assert.That(mood.Weather, Is.EqualTo("storm-front"));
    }

    [Test]
    public void Forecast_LowPlayerHealth_OverridesToAgitated()
    {
        var rel = BuildRel(affinity: 70, RelationshipMood.Warm, InteractionTone.Warm);
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            PlayerHealthFraction = 0.15f,
        };

        MoodWeather mood = MoodWeatherAdvisor.Forecast(rel, snap);

        Assert.That(mood.Mood, Is.EqualTo("agitated"));
        Assert.That(mood.Weather, Is.EqualTo("cloudburst"));
    }

    [Test]
    public void Forecast_Night_SoftensAffectionateToContent()
    {
        var rel = BuildRel(affinity: 80, RelationshipMood.Warm, InteractionTone.Warm);
        var snap = new GameWorldSnapshot { IsWorldLoaded = true, TimeOfDay = "late-night" };

        MoodWeather mood = MoodWeatherAdvisor.Forecast(rel, snap);

        Assert.That(mood.Mood, Is.EqualTo("content"));
        Assert.That(mood.Tone, Is.EqualTo("quiet"));
    }

    [Test]
    public void Forecast_HostileLastTone_DowngradesContentToUneasy()
    {
        var rel = BuildRel(affinity: 25, RelationshipMood.Neutral, InteractionTone.Harsh);
        var snap = new GameWorldSnapshot { IsWorldLoaded = true, TimeOfDay = "day" };

        MoodWeather mood = MoodWeatherAdvisor.Forecast(rel, snap);

        Assert.That(mood.Mood, Is.EqualTo("uneasy"));
    }

    [Test]
    public void Forecast_NullSnapshot_StillProducesForecast()
    {
        var rel = BuildRel(affinity: 30, RelationshipMood.Warm, InteractionTone.Warm);

        MoodWeather mood = MoodWeatherAdvisor.Forecast(rel, snapshot: null);

        Assert.That(mood.CharacterName, Is.EqualTo("SpeciesAlpha"));
        Assert.That(mood.Summary, Does.Contain("SpeciesAlpha"));
    }

    private static CharacterRelationship BuildRel(int affinity, RelationshipMood mood, InteractionTone tone)
        => new()
        {
            CharacterId = 42,
            CharacterName = "SpeciesAlpha",
            Affinity = affinity,
            Mood = mood,
            LastTone = tone,
        };
}
