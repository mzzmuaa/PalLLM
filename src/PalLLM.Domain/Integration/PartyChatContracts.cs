namespace PalLLM.Domain.Integration;

/// <summary>
/// Pass 34 / C1 — request shape for <c>POST /api/chat/party</c>.
/// Fans out a single utterance across several character ids so a
/// whole party of companions can reply to the same player prompt in
/// sequence. Uses the existing <see cref="ChatRequest"/> machinery
/// per-character — so every reply still goes through the task-aware
/// execution profile, the Pass-8 planner, rate limiting, and
/// deterministic fallback.
///
/// <para>Deliberately NOT a contract change on <see cref="ChatRequest"/>:
/// the per-turn shape stays identical so single-character callers
/// see zero behaviour change. Party chat is an orchestration on top.</para>
/// </summary>
public sealed class PartyChatRequest
{
    /// <summary>Character ids to fan the utterance out to, in order.</summary>
    public List<int> CharacterIds { get; init; } = new();

    /// <summary>Optional parallel list of names (same length as <see cref="CharacterIds"/>). When omitted, names come from the adapter.</summary>
    public List<string>? CharacterNames { get; init; }

    /// <summary>Task tag applied to every per-character request.</summary>
    public string TaskTag { get; init; } = "player_chat";

    /// <summary>The player's message.</summary>
    public string UserMessage { get; init; } = string.Empty;

    /// <summary>When true, each reply is seeded with a brief mention of earlier characters' replies so a conversation thread forms. Default off — each character replies independently.</summary>
    public bool Threaded { get; init; } = false;

    /// <summary>Optional temperature passed to every turn. When omitted, per-character defaults apply.</summary>
    public float? Temperature { get; init; }
}

/// <summary>
/// One character's reply inside a <see cref="PartyChatResponse"/>.
/// </summary>
/// <param name="OrderIndex">Zero-based order in the fan-out.</param>
/// <param name="CharacterId">Character id this reply is from.</param>
/// <param name="CharacterName">Character name (from request or adapter).</param>
/// <param name="Response">Full per-turn ChatResponse for this character.</param>
public sealed record PartyChatTurn(
    int OrderIndex,
    int? CharacterId,
    string? CharacterName,
    ChatResponse Response);

/// <summary>
/// Full party-chat response: one entry per character id.
/// </summary>
public sealed class PartyChatResponse
{
    /// <summary>Correlation id for the full fan-out.</summary>
    public string PartyId { get; init; } = string.Empty;

    /// <summary>Per-character replies in request order.</summary>
    public IReadOnlyList<PartyChatTurn> Turns { get; init; } = Array.Empty<PartyChatTurn>();

    /// <summary>When true, each reply's system prompt was seeded with earlier replies.</summary>
    public bool Threaded { get; init; }

    /// <summary>Sum of per-turn LatencyMs values.</summary>
    public long TotalLatencyMs { get; init; }

    /// <summary>Count of turns that used the deterministic fallback path (useful to detect live-inference regressions at the party scale).</summary>
    public int FallbackTurnCount { get; init; }

    /// <summary>When the party-chat dispatch completed (UTC).</summary>
    public DateTimeOffset CapturedAtUtc { get; init; }
}
