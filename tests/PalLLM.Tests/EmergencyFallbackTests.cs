using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers the last-resort <see cref="EmergencyFallback"/> safety net.
/// These tests pin the user-facing contract of the third fallback tier:
///
/// 1. Every emergency message is non-empty and tagged with the
///    deterministic <c>emergency-recovery</c> strategy id so operators
///    can see this tier in the existing strategy metric.
/// 2. The rotation is deterministic for a given tick — same tick,
///    same message — so snapshot tests don't flake.
/// 3. <see cref="EmergencyFallback.Guard"/> returns the director's
///    decision untouched on the happy path.
/// 4. A throwing director, a director returning <c>null</c>, and a
///    director returning an empty-message decision all collapse to the
///    emergency rotation rather than crashing the caller.
/// </summary>
public sealed class EmergencyFallbackTests
{
    [Test]
    public void Decide_AllEntriesAreNonEmptyAndTaggedConsistently()
    {
        // Exhaustive over the rotation. Five entries today; adding a sixth
        // should still hit this test the same way.
        for (int tick = 0; tick < 1000; tick++)
        {
            FallbackBehaviorDecision d = EmergencyFallback.Decide(tick);

            Assert.That(d, Is.Not.Null);
            Assert.That(d.StrategyId, Is.EqualTo(EmergencyFallback.StrategyId));
            Assert.That(d.IsApplicable, Is.True,
                "Emergency decisions must always be applicable — they are the safety net.");
            Assert.That(d.Message, Is.Not.Null);
            Assert.That(d.Message.Trim(), Is.Not.Empty,
                "An empty message would defeat the whole point of the safety net.");
        }
    }

    [Test]
    public void Decide_IsDeterministicForSameTick()
    {
        FallbackBehaviorDecision a = EmergencyFallback.Decide(42);
        FallbackBehaviorDecision b = EmergencyFallback.Decide(42);

        Assert.That(b.Message, Is.EqualTo(a.Message));
        Assert.That(b.StrategyId, Is.EqualTo(a.StrategyId));
    }

    [Test]
    public void Decide_RotatesAcrossDifferentTicks()
    {
        // Five entries — sampling 10 distinct ticks must produce at least
        // two distinct messages, or the modulo is broken.
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int tick = 0; tick < 10; tick++)
        {
            seen.Add(EmergencyFallback.Decide(tick).Message);
        }

        Assert.That(seen, Has.Count.GreaterThanOrEqualTo(2),
            "Rotation should produce at least two distinct messages across 10 ticks.");
    }

    [Test]
    public void Guard_PassesThroughHappyPath()
    {
        var director = new FallbackBehaviorDecision(
            strategyId: "stealth-withdraw",
            phase: FallbackPacingPhase.Relax,
            message: "Let's slip back into cover.",
            priority: 10,
            signals: [],
            isApplicable: true);

        FallbackBehaviorDecision result = EmergencyFallback.Guard(() => director, tick: 0);

        Assert.That(result, Is.SameAs(director),
            "On the happy path, Guard must return the director's decision untouched.");
    }

    [Test]
    public void Guard_OnThrowingDirector_FallsBackToEmergency()
    {
        FallbackBehaviorDecision result = EmergencyFallback.Guard(
            () => throw new InvalidOperationException("pack data malformed"),
            tick: 0);

        Assert.That(result.StrategyId, Is.EqualTo(EmergencyFallback.StrategyId));
        Assert.That(result.Message.Trim(), Is.Not.Empty);
    }

    [Test]
    public void Guard_OnNullDirectorResult_FallsBackToEmergency()
    {
        FallbackBehaviorDecision result = EmergencyFallback.Guard(() => null!, tick: 7);

        Assert.That(result.StrategyId, Is.EqualTo(EmergencyFallback.StrategyId));
    }

    [Test]
    public void Guard_OnEmptyMessageDirectorResult_FallsBackToEmergency()
    {
        var brokenDecision = new FallbackBehaviorDecision(
            strategyId: "stealth-withdraw",
            phase: FallbackPacingPhase.Relax,
            message: "   ",
            priority: 10,
            signals: [],
            isApplicable: true);

        FallbackBehaviorDecision result = EmergencyFallback.Guard(() => brokenDecision, tick: 3);

        Assert.That(result.StrategyId, Is.EqualTo(EmergencyFallback.StrategyId),
            "A director that returns whitespace must not reach the player.");
    }

    [Test]
    public void Guard_OnNullAttempt_FallsBackToEmergency()
    {
        FallbackBehaviorDecision result = EmergencyFallback.Guard(attempt: null!, tick: 1);

        Assert.That(result.StrategyId, Is.EqualTo(EmergencyFallback.StrategyId));
    }
}
