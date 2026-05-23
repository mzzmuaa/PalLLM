namespace PalLLM.Domain.Runtime;

/// <summary>
/// Last-resort safety net. The deterministic <c>FallbackBehaviorEngine</c>
/// is already the "inference failed" path for PalLLM; <c>EmergencyFallback</c>
/// is the "even the fallback engine threw" path.
///
/// <para>Real-world failure modes this protects against:</para>
/// <list type="bullet">
///   <item>A narrative pack with malformed data that trips a strategy's
///         null/range assumption after hot-reload.</item>
///   <item>A portable-adapter regression that surfaces a character with
///         unexpected state after an in-game patch.</item>
///   <item>An out-of-memory condition inside the director while the
///         sidecar is still otherwise healthy.</item>
/// </list>
///
/// <para>The emergency messages are short, neutral, and deliberately
/// non-specific about Palworld so they also make sense for any other
/// game-adapter re-harvest. They NEVER throw, do no IO, and do not touch
/// any shared state — a caller that hits this path has already broken
/// enough invariants that the safest thing is to hand the player a
/// plain-English acknowledgement and keep the session alive.</para>
///
/// <para>Every message is tagged with the deterministic
/// <see cref="FallbackBehaviorDecision.StrategyId"/> value
/// <c>"emergency-recovery"</c> so operators can count this tier in the
/// existing strategy metric without a new histogram.</para>
/// </summary>
public static class EmergencyFallback
{
    public const string StrategyId = "emergency-recovery";

    // Tiny rotation so two back-to-back emergencies don't look like a
    // stuck machine. Five entries is enough to avoid obvious repetition
    // in the rare case this tier fires more than once per session.
    private static readonly string[] Messages =
    [
        "I'm here. Give me a moment — something wobbled on my end, but I'm still with you.",
        "Hold on just a second. I hit a snag, but I'm not going anywhere.",
        "I stumbled for a beat. Still on your side — what should we tackle first?",
        "Sorry about that — had to steady myself. I'm back. Where were we?",
        "Still thinking. Something tripped me up, but I'm recovering — tell me what you need.",
    ];

    /// <summary>
    /// Deterministic rotation indexed by <paramref name="tick"/>. Callers
    /// that want variety pass <c>Environment.TickCount64</c> or a chat
    /// counter; tests pass a fixed value for reproducibility.
    /// </summary>
    public static FallbackBehaviorDecision Decide(long tick)
    {
        int index = (int)((uint)tick % (uint)Messages.Length);
        return new FallbackBehaviorDecision(
            strategyId: StrategyId,
            phase: FallbackPacingPhase.Relax,
            message: Messages[index],
            priority: int.MinValue + 1,
            signals: ["emergency"],
            isApplicable: true);
    }

    /// <summary>
    /// Overload that wraps a throwing call to the deterministic director.
    /// Use at call sites inside <c>PalLlmRuntime</c> so a broken strategy
    /// never crashes a chat turn.
    /// </summary>
    public static FallbackBehaviorDecision Guard(Func<FallbackBehaviorDecision> attempt, long tick)
    {
        if (attempt is null)
        {
            return Decide(tick);
        }

        try
        {
            FallbackBehaviorDecision result = attempt();
            // Defensive: a director that returns null or an empty message
            // shouldn't reach the player either.
            if (result is null || string.IsNullOrWhiteSpace(result.Message))
            {
                return Decide(tick);
            }
            return result;
        }
        catch
        {
            return Decide(tick);
        }
    }
}
