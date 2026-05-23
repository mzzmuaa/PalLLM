namespace PalLLM.Domain.Runtime;

public enum PalTaskPriority
{
    High,
    Normal,
    Low,
}

public enum PalTaskKind
{
    General,
    InteractiveChat,
    ReactiveBark,
    BaseAdvisor,
    PackAuthoring,
}

public sealed class PalTaskProfile
{
    public PalTaskKind Kind { get; init; }

    public bool AllowFastLane { get; init; }

    public int DefaultMaxTokens { get; init; }
}

public static class PalTaskRouter
{
    public static PalTaskProfile Resolve(string tag, string userMessage, PalTaskPriority priority)
    {
        if (StartsWith(tag, "chat_") || Contains(tag, "player_chat"))
        {
            return new PalTaskProfile
            {
                Kind = PalTaskKind.InteractiveChat,
                DefaultMaxTokens = 220,
            };
        }

        if (StartsWith(tag, "bark_") || StartsWith(tag, "ambient_"))
        {
            return new PalTaskProfile
            {
                Kind = PalTaskKind.ReactiveBark,
                AllowFastLane = priority == PalTaskPriority.Low,
                DefaultMaxTokens = 90,
            };
        }

        if (StartsWith(tag, "base_") || Contains(userMessage, "base"))
        {
            return new PalTaskProfile
            {
                Kind = PalTaskKind.BaseAdvisor,
                DefaultMaxTokens = 320,
            };
        }

        if (StartsWith(tag, "pack_"))
        {
            return new PalTaskProfile
            {
                Kind = PalTaskKind.PackAuthoring,
                DefaultMaxTokens = 500,
            };
        }

        return new PalTaskProfile
        {
            Kind = PalTaskKind.General,
            AllowFastLane = priority == PalTaskPriority.Low,
            DefaultMaxTokens = priority switch
            {
                PalTaskPriority.High => 260,
                PalTaskPriority.Low => 120,
                _ => 180,
            },
        };
    }

    private static bool StartsWith(string? value, string prefix) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string? value, string fragment) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
