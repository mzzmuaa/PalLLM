using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 36 / C2 — deterministic advisor that decides whether the
/// current scene warrants a companion's world-narration quip ("boss
/// just showed up", "night's falling", "you look hurt"). Used by
/// the optional narrator service (future pass) and by any AI agent
/// that wants to ask "is anything narration-worthy happening right
/// now?" without running inference.
///
/// <para>Pure function over a <see cref="GameWorldSnapshot"/> plus
/// an optional <see cref="DateTimeOffset"/> for the last-narrated
/// timestamp. Returns a <see cref="NarrationCue"/> with a
/// <c>ShouldNarrate</c> decision, a trigger bucket
/// ("combat-start" / "threat-spike" / "night-fall" / "weather-change"
/// / "low-health" / "objective-update" / "no-trigger"), a suggested
/// prompt fragment the companion can expand on, and the rate-limit
/// budget hint so the caller never over-narrates.</para>
///
/// <para>Rate-limit: caller enforces. Advisor exposes
/// <see cref="NarrationCue.MinimumGapSeconds"/> so the caller can
/// drop cues that arrive inside the gap.</para>
/// </summary>
public static class WorldNarrationAdvisor
{
    /// <summary>
    /// Minimum gap between narration cues, in seconds.
    /// Keeps the companion from becoming chatty.
    /// </summary>
    public const int DefaultMinimumGapSeconds = 90;

    /// <summary>
    /// Decide whether the scene warrants a narration cue.
    /// </summary>
    /// <param name="snapshot">The current world snapshot.</param>
    /// <param name="lastNarrationUtc">When the caller last narrated, or null if never.</param>
    /// <param name="now">Optional fixed clock for tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static NarrationCue Advise(
        GameWorldSnapshot? snapshot,
        DateTimeOffset? lastNarrationUtc,
        DateTimeOffset? now = null)
    {
        if (snapshot is null || !snapshot.IsWorldLoaded)
        {
            return new NarrationCue(
                ShouldNarrate: false,
                Trigger: "world-not-loaded",
                PromptFragment: string.Empty,
                MinimumGapSeconds: DefaultMinimumGapSeconds,
                Reason: "World snapshot isn't loaded — nothing to narrate.");
        }

        DateTimeOffset evaluatedAt = now ?? DateTimeOffset.UtcNow;
        if (lastNarrationUtc is { } lastAt)
        {
            TimeSpan elapsed = evaluatedAt - lastAt;
            if (elapsed < TimeSpan.FromSeconds(DefaultMinimumGapSeconds))
            {
                int secondsLeft = DefaultMinimumGapSeconds - (int)elapsed.TotalSeconds;
                return new NarrationCue(
                    ShouldNarrate: false,
                    Trigger: "rate-limited",
                    PromptFragment: string.Empty,
                    MinimumGapSeconds: DefaultMinimumGapSeconds,
                    Reason: $"Rate-limited — last narration was {(int)elapsed.TotalSeconds}s ago, need {secondsLeft}s more.");
            }
        }

        // Trigger evaluation, most-specific first.
        // 1. Combat / threat spike.
        if (snapshot.NearbyHostiles.Count > 0)
        {
            string hostile = snapshot.NearbyHostiles[0];
            return new NarrationCue(
                ShouldNarrate: true,
                Trigger: "combat-start",
                PromptFragment: $"Hostile nearby: {hostile}. React to the immediate threat in one short line.",
                MinimumGapSeconds: DefaultMinimumGapSeconds,
                Reason: $"{snapshot.NearbyHostiles.Count} hostile(s) in range.");
        }
        if (snapshot.ThreatLevel is float threat && threat > 0.7f)
        {
            return new NarrationCue(
                ShouldNarrate: true,
                Trigger: "threat-spike",
                PromptFragment: $"Threat level is high ({threat:F1}). Warn the player in a short line.",
                MinimumGapSeconds: DefaultMinimumGapSeconds,
                Reason: $"ThreatLevel={threat:F2}.");
        }

        // 2. Player state (hurt / exhausted / hungry).
        if (snapshot.PlayerHealthFraction is float hp && hp < 0.3f)
        {
            return new NarrationCue(
                ShouldNarrate: true,
                Trigger: "low-health",
                PromptFragment: $"Player HP is low ({hp:P0}). Express concern in one short line.",
                MinimumGapSeconds: DefaultMinimumGapSeconds,
                Reason: $"PlayerHealthFraction={hp:F2}.");
        }

        // 3. Objective change.
        if (!string.IsNullOrWhiteSpace(snapshot.CurrentObjective))
        {
            return new NarrationCue(
                ShouldNarrate: true,
                Trigger: "objective-update",
                PromptFragment: $"Active objective: '{snapshot.CurrentObjective}'. Acknowledge in one short line.",
                MinimumGapSeconds: DefaultMinimumGapSeconds,
                Reason: "Objective text is non-empty.");
        }

        // 4. Ambient cues — weather + time-of-day. Deliberately softer
        //    so the companion doesn't narrate every day/night flip.
        string weather = (snapshot.Weather ?? string.Empty).ToLowerInvariant();
        if (weather.Contains("storm", StringComparison.Ordinal)
            || weather.Contains("thunder", StringComparison.Ordinal)
            || weather.Contains("blizzard", StringComparison.Ordinal))
        {
            return new NarrationCue(
                ShouldNarrate: true,
                Trigger: "weather-change",
                PromptFragment: $"Weather turned rough ({snapshot.Weather}). Mention it in one short line.",
                MinimumGapSeconds: DefaultMinimumGapSeconds,
                Reason: $"Weather='{snapshot.Weather}'.");
        }

        string timeOfDay = (snapshot.TimeOfDay ?? string.Empty).ToLowerInvariant();
        if (timeOfDay.Contains("night", StringComparison.Ordinal))
        {
            return new NarrationCue(
                ShouldNarrate: true,
                Trigger: "night-fall",
                PromptFragment: "It's night-time. Note the shift in mood in one short line.",
                MinimumGapSeconds: DefaultMinimumGapSeconds,
                Reason: $"TimeOfDay='{snapshot.TimeOfDay}'.");
        }

        return new NarrationCue(
            ShouldNarrate: false,
            Trigger: "no-trigger",
            PromptFragment: string.Empty,
            MinimumGapSeconds: DefaultMinimumGapSeconds,
            Reason: "No narration-worthy signals in the current snapshot.");
    }
}

/// <summary>
/// Advisory returned by <see cref="WorldNarrationAdvisor.Advise"/>.
/// </summary>
/// <param name="ShouldNarrate">True when the advisor recommends narrating.</param>
/// <param name="Trigger">Short kebab-case trigger bucket.</param>
/// <param name="PromptFragment">Prompt fragment the companion can expand into a one-line narration.</param>
/// <param name="MinimumGapSeconds">Minimum gap the caller should enforce between narration cues.</param>
/// <param name="Reason">Plain-English reason for the decision.</param>
public sealed record NarrationCue(
    bool ShouldNarrate,
    string Trigger,
    string PromptFragment,
    int MinimumGapSeconds,
    string Reason);
