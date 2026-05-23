namespace PalLLM.Sidecar;

internal static class ReleaseEvidenceFreshness
{
    private const int DefaultFreshnessWindowHours = 24;

    public static ReleaseEvidenceFreshnessSnapshot Evaluate(
        DateTimeOffset? capturedAtUtc,
        int freshnessWindowHours)
    {
        int normalizedWindowHours = freshnessWindowHours > 0
            ? freshnessWindowHours
            : DefaultFreshnessWindowHours;

        if (!capturedAtUtc.HasValue)
        {
            return new ReleaseEvidenceFreshnessSnapshot(
                "unknown",
                null,
                normalizedWindowHours);
        }

        DateTimeOffset freshUntilUtc = capturedAtUtc.Value
            .ToUniversalTime()
            .AddHours(normalizedWindowHours);
        string freshnessStatus = DateTimeOffset.UtcNow <= freshUntilUtc
            ? "fresh"
            : "stale";

        return new ReleaseEvidenceFreshnessSnapshot(
            freshnessStatus,
            freshUntilUtc,
            normalizedWindowHours);
    }
}

internal readonly record struct ReleaseEvidenceFreshnessSnapshot(
    string Status,
    DateTimeOffset? FreshUntilUtc,
    int FreshnessWindowHours);
