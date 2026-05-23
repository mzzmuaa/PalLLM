using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 38 / C10 — deterministic advisor that blends a character's
/// <see cref="CharacterRelationship"/> with the current
/// <see cref="GameWorldSnapshot"/> to produce a rollable-forecast
/// style "mood weather" pill the dashboard can render and the
/// companion's chat prompt can include.
///
/// <para>Inputs are whatever's already observable — no inference,
/// no side effects. Mood drifts in a bounded set of buckets
/// ("content" / "uneasy" / "agitated" / "affectionate" / "weary" /
/// "wary") based on affinity, recent tone, player health, threat
/// level, and time-of-day. A short plain-English "weather line"
/// accompanies each bucket so players + AI agents have a human
/// readout alongside the machine id.</para>
///
/// <para>Pure function: identical inputs always produce identical
/// output. Safe on hot paths.</para>
/// </summary>
public static class MoodWeatherAdvisor
{
    /// <summary>
    /// Derive the current mood weather for a character given their
    /// relationship record and the latest world snapshot.
    /// </summary>
    public static MoodWeather Forecast(CharacterRelationship relationship, GameWorldSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        int affinity = relationship.Affinity;
        string weather;
        string mood;
        string tone;

        // 1. Threat / combat takes priority — even a content companion
        //    gets wary in combat.
        if (snapshot is not null && snapshot.NearbyHostiles.Count > 0)
        {
            mood = "wary";
            weather = "storm-front";
            tone = "on edge";
        }
        else if (snapshot is not null && snapshot.PlayerHealthFraction is float hp && hp < 0.3f)
        {
            mood = "agitated";
            weather = "cloudburst";
            tone = "worried for you";
        }
        // 2. Affinity-driven base mood.
        else if (affinity <= -40)
        {
            mood = "uneasy";
            weather = "cold-front";
            tone = "distant";
        }
        else if (affinity >= 60)
        {
            mood = "affectionate";
            weather = "golden-hour";
            tone = "open";
        }
        else if (affinity >= 20)
        {
            mood = "content";
            weather = "clear-sky";
            tone = "warm";
        }
        else
        {
            mood = "content";
            weather = "partly-cloudy";
            tone = "neutral";
        }

        // 3. Recent tone nudges base mood BEFORE night-softening so
        //    nudge-then-soften stays monotonic (night always wins).
        switch (relationship.LastTone)
        {
            case InteractionTone.Harsh:
            case InteractionTone.Cool:
                if (mood == "content") { mood = "uneasy"; tone = "cooling"; }
                break;
            case InteractionTone.Warm:
            case InteractionTone.Affectionate:
                if (mood == "content") { mood = "affectionate"; tone = "opening"; }
                break;
        }

        // 4. Night-time softens mood down one notch.
        bool isNight = snapshot?.TimeOfDay is string tod
            && tod.Contains("night", StringComparison.OrdinalIgnoreCase);
        if (isNight && mood == "affectionate")
        {
            mood = "content";
            tone = "quiet";
        }
        else if (isNight && mood == "content")
        {
            mood = "weary";
            weather = "twilight";
            tone = "sleepy";
        }

        string summary = $"{relationship.CharacterName} is {mood} ({weather}, {tone}).";

        return new MoodWeather(
            CharacterId: relationship.CharacterId,
            CharacterName: relationship.CharacterName,
            Mood: mood,
            Weather: weather,
            Tone: tone,
            Summary: summary,
            Affinity: affinity,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// Result of <see cref="MoodWeatherAdvisor.Forecast"/>. Intended to
/// be rendered as a dashboard pill and injected into chat prompts as
/// one extra system-prompt line.
/// </summary>
/// <param name="CharacterId">Character id this forecast applies to.</param>
/// <param name="CharacterName">Character display name.</param>
/// <param name="Mood">Short mood bucket (content / uneasy / agitated / affectionate / weary / wary).</param>
/// <param name="Weather">Weather metaphor (clear-sky / partly-cloudy / cold-front / storm-front / cloudburst / golden-hour / twilight).</param>
/// <param name="Tone">One-word tone (warm / open / distant / worried-for-you / on-edge / sleepy / quiet / neutral / cooling / opening).</param>
/// <param name="Summary">Human-readable summary line.</param>
/// <param name="Affinity">Current affinity score at capture time.</param>
/// <param name="CapturedAtUtc">When the forecast was captured (UTC).</param>
public sealed record MoodWeather(
    int CharacterId,
    string CharacterName,
    string Mood,
    string Weather,
    string Tone,
    string Summary,
    int Affinity,
    DateTimeOffset CapturedAtUtc);
