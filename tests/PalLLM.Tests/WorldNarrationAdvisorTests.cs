using NUnit.Framework;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 36 / C2 — regression coverage for
/// <see cref="WorldNarrationAdvisor"/>. Pinned contract:
/// <list type="bullet">
///   <item>Null / unloaded snapshot returns ShouldNarrate=false with trigger='world-not-loaded'.</item>
///   <item>Hostile nearby triggers 'combat-start'.</item>
///   <item>High threat with no hostiles triggers 'threat-spike'.</item>
///   <item>Low health triggers 'low-health'.</item>
///   <item>Rate-limited return uses trigger='rate-limited' and includes remaining seconds in Reason.</item>
///   <item>Quiet snapshot returns 'no-trigger'.</item>
/// </list>
/// </summary>
[TestFixture]
public class WorldNarrationAdvisorTests
{
    [Test]
    public void Advise_NullSnapshot_ReturnsWorldNotLoaded()
    {
        NarrationCue cue = WorldNarrationAdvisor.Advise(null, lastNarrationUtc: null);
        Assert.That(cue.ShouldNarrate, Is.False);
        Assert.That(cue.Trigger, Is.EqualTo("world-not-loaded"));
    }

    [Test]
    public void Advise_UnloadedSnapshot_ReturnsWorldNotLoaded()
    {
        var snap = new GameWorldSnapshot { IsWorldLoaded = false };
        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarrationUtc: null);
        Assert.That(cue.Trigger, Is.EqualTo("world-not-loaded"));
    }

    [Test]
    public void Advise_HostilesNearby_FiresCombatStart()
    {
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = { "Frostallion" },
        };
        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarrationUtc: null);
        Assert.That(cue.ShouldNarrate, Is.True);
        Assert.That(cue.Trigger, Is.EqualTo("combat-start"));
        Assert.That(cue.PromptFragment, Does.Contain("Frostallion"));
    }

    [Test]
    public void Advise_HighThreatNoHostiles_FiresThreatSpike()
    {
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            ThreatLevel = 0.85f,
        };
        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarrationUtc: null);
        Assert.That(cue.Trigger, Is.EqualTo("threat-spike"));
        Assert.That(cue.ShouldNarrate, Is.True);
    }

    [Test]
    public void Advise_LowPlayerHealth_FiresLowHealth()
    {
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            PlayerHealthFraction = 0.15f,
        };
        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarrationUtc: null);
        Assert.That(cue.Trigger, Is.EqualTo("low-health"));
    }

    [Test]
    public void Advise_ObjectiveSet_FiresObjectiveUpdate()
    {
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            CurrentObjective = "Reach the Bamboo Grove",
        };
        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarrationUtc: null);
        Assert.That(cue.Trigger, Is.EqualTo("objective-update"));
        Assert.That(cue.PromptFragment, Does.Contain("Bamboo Grove"));
    }

    [Test]
    public void Advise_NightFall_FiresAmbientCue()
    {
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            TimeOfDay = "late-night",
        };
        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarrationUtc: null);
        Assert.That(cue.Trigger, Is.EqualTo("night-fall"));
    }

    [Test]
    public void Advise_RateLimited_WhenInsideGap()
    {
        var snap = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = { "Frostallion" },
        };
        DateTimeOffset now = new(2026, 4, 24, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset lastNarration = now.AddSeconds(-30); // inside default 90s gap

        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarration, now);

        Assert.That(cue.ShouldNarrate, Is.False);
        Assert.That(cue.Trigger, Is.EqualTo("rate-limited"));
        Assert.That(cue.Reason, Does.Contain("ago"));
    }

    [Test]
    public void Advise_QuietSnapshot_ReturnsNoTrigger()
    {
        var snap = new GameWorldSnapshot { IsWorldLoaded = true };
        NarrationCue cue = WorldNarrationAdvisor.Advise(snap, lastNarrationUtc: null);
        Assert.That(cue.Trigger, Is.EqualTo("no-trigger"));
        Assert.That(cue.ShouldNarrate, Is.False);
    }
}
