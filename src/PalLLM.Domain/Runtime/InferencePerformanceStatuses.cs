namespace PalLLM.Domain.Runtime;

public static class InferencePerformanceStatuses
{
    public const string Healthy = "healthy";
    public const string Degraded = "degraded";
    public const string Critical = "critical";
    public const string InsufficientData = "insufficient_data";
    public const string NoData = "no_data";

    public static readonly string[] All =
    [
        Healthy,
        Degraded,
        Critical,
        InsufficientData,
        NoData,
    ];

    public static bool IsAlerting(string? status) =>
        string.Equals(status, Degraded, StringComparison.Ordinal)
        || string.Equals(status, Critical, StringComparison.Ordinal);
}
