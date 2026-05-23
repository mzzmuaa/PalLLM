namespace PalLLM.Domain.Runtime;

public sealed class FallbackBehaviorDecision
{
    public static FallbackBehaviorDecision NotApplicable { get; } = new(
        "not-applicable",
        FallbackPacingPhase.Relax,
        string.Empty,
        int.MinValue,
        [],
        isApplicable: false);

    public FallbackBehaviorDecision(
        string strategyId,
        FallbackPacingPhase phase,
        string message,
        int priority,
        IReadOnlyList<string> signals,
        bool isApplicable)
    {
        StrategyId = strategyId;
        Phase = phase;
        Message = message;
        Priority = priority;
        Signals = signals;
        IsApplicable = isApplicable;
    }

    public string StrategyId { get; }

    public FallbackPacingPhase Phase { get; }

    public string Message { get; }

    public int Priority { get; }

    public IReadOnlyList<string> Signals { get; }

    public bool IsApplicable { get; }
}

public enum FallbackPacingPhase
{
    Relax,
    BuildUp,
    Peak,
    Recover,
}

internal enum FallbackMemoryTheme
{
    None,
    Loss,
    Ambush,
    Rival,
}
