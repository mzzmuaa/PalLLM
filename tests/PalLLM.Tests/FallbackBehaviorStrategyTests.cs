using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Direct coverage for deterministic fallback strategies that the broader
/// runtime tests don't exercise end-to-end. Each test crafts a minimal
/// <see cref="FallbackBehaviorContext"/> (a keyword in the chat message, a
/// world-state signal, or a low-stat character) so exactly one strategy is
/// applicable, then asserts <c>Generate</c> routes to it with a non-empty
/// reply. Pins the headline "there is always a working deterministic reply"
/// contract against regressions in the strategy guards or priority dispatch.
/// </summary>
public sealed class FallbackBehaviorStrategyTests
{
    private static readonly FallbackBehaviorEngine Engine = new(new PalLlmOptions());

    private static FallbackBehaviorDecision Decide(
        string userMessage,
        GameWorldSnapshot? snapshot = null,
        GameCharacterSnapshot? character = null)
    {
        FallbackBehaviorContext context = Engine.Analyze(
            new ChatRequest { UserMessage = userMessage },
            new PalTaskProfile(),
            snapshot ?? new GameWorldSnapshot(),
            character,
            lore: null,
            memoryMatches: [],
            recentEntries: []);
        return Engine.Generate(context);
    }

    [Test]
    public void CaptureKeyword_RoutesToCaptureWindow()
    {
        FallbackBehaviorDecision decision = Decide("I want to catch and tame that pal");

        Assert.That(decision.StrategyId, Is.EqualTo("capture-window"));
        Assert.That(decision.IsApplicable, Is.True);
        Assert.That(decision.Message, Is.Not.Empty);
    }

    [Test]
    public void ExploreKeyword_RoutesToExplorationSweep()
    {
        FallbackBehaviorDecision decision = Decide("let's scout ahead and sweep the ridge");

        Assert.That(decision.StrategyId, Is.EqualTo("exploration-sweep"));
        Assert.That(decision.Message, Is.Not.Empty);
    }

    [Test]
    public void StormWeather_RoutesToWeatherShelter()
    {
        FallbackBehaviorDecision decision = Decide(
            "thinking out loud here",
            new GameWorldSnapshot { Weather = "storm" });

        Assert.That(decision.StrategyId, Is.EqualTo("weather-shelter"));
        Assert.That(decision.Message, Is.Not.Empty);
    }

    [Test]
    public void LowMoraleCharacter_RoutesToMoraleRally()
    {
        // Morale <= 0.35 trips IsLowMorale; morale does not shift the pacing
        // phase, so with no other signal the morale-rally director is the
        // single applicable strategy above the general director.
        FallbackBehaviorDecision decision = Decide(
            "not sure we can keep this up",
            character: new GameCharacterSnapshot { Morale = 0.1f });

        Assert.That(decision.StrategyId, Is.EqualTo("morale-rally"));
        Assert.That(decision.Message, Is.Not.Empty);
    }
}
