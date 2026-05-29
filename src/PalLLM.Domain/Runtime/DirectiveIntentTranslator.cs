using System.Globalization;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 31 / C3 — deterministic translator that converts a natural-
/// language player chat (e.g. "hey helper, stop mining and help me
/// fight") into an ordered <see cref="PalDirective"/> array the UE4SS
/// mod can forward to the native pal-AI controller.
///
/// <para>Pure function. No inference call, no mutable state. The
/// translator only emits directives whose <see cref="PalDirective.Action"/>
/// appears in the caller-supplied allowlist — so the existing
/// <c>AutomationOptions.AllowedActions</c> safety gate stays in force
/// end-to-end. If nothing in the utterance maps to an allowed action,
/// returns an empty plan with <see cref="DirectivePlan.Reason"/>
/// explaining why.</para>
///
/// <para>Pattern mirrors the Pass-16 <see cref="Inference.ChatTaskKindInferer"/>:
/// keyword-based classifier, more-specific cues win over generic ones.
/// Unknown shape collapses to an empty plan (no guess work).</para>
/// </summary>
public static class DirectiveIntentTranslator
{
    /// <summary>
    /// Translate <paramref name="utterance"/> into an ordered plan of
    /// allowlisted directives.
    /// </summary>
    /// <param name="utterance">Raw player message.</param>
    /// <param name="allowedActions">Current <c>AutomationOptions.AllowedActions</c> snapshot — never emitted above this set.</param>
    /// <param name="addressedPal">Optional name of the pal the utterance addresses — used to populate <see cref="PalDirective.TargetPal"/>.</param>
    public static DirectivePlan Translate(
        string? utterance,
        IReadOnlyList<string>? allowedActions,
        string? addressedPal = null)
    {
        ArgumentNullException.ThrowIfNull(allowedActions);

        if (string.IsNullOrWhiteSpace(utterance))
        {
            return new DirectivePlan(
                Utterance: string.Empty,
                Directives: Array.Empty<PalDirective>(),
                RejectedCandidates: Array.Empty<string>(),
                Reason: "empty-utterance",
                CapturedAtUtc: DateTimeOffset.UtcNow);
        }

        string q = utterance.ToLowerInvariant();
        string? target = string.IsNullOrWhiteSpace(addressedPal) ? null : addressedPal.Trim();

        // Each candidate is an (action-id, priority, cue-matchers, detail).
        // Earlier candidates win when multiple fire, so the ordering
        // below is the priority: stop > help_fight > gather > follow.
        var candidates = new List<(string Action, string Detail, string[] Cues)>
        {
            ("stop_current_task",
             target is null ? "Halt whatever the pal is doing right now." : $"Tell {target} to halt its current task.",
             new[] { "stop ", "cease", "halt", "quit ", "stand down" }),
            ("recall_pals",
             target is null ? "Recall active pals back to the player." : $"Recall {target} back to the player.",
             new[] { "come back", "return to me", "recall", "back to me", "stand by me" }),
            ("help_in_combat",
             target is null ? "Have the pal assist the player in combat." : $"Have {target} assist the player in combat.",
             new[] { "help me fight", "help fight", "defend me", "attack ", "assist in combat", "cover me", "engage " }),
            ("gather_resources",
             target is null ? "Send the pal to gather nearby resources." : $"Send {target} to gather nearby resources.",
             new[] { "gather ", "collect ", "harvest", "mine ", "chop ", "forage" }),
            ("request_craft_queue",
             "Queue an item for crafting at the nearest workbench.",
             new[] { "craft ", "build ", "make me a", "forge " }),
            ("mark_waypoint",
             "Mark the current spot as a waypoint.",
             new[] { "mark this", "waypoint", "pin this", "remember this spot" }),
            ("follow_player",
             target is null ? "Have the pal follow the player." : $"Have {target} follow the player.",
             new[] { "follow me", "come with me", "stay with me", "tag along" }),
            ("guard_position",
             target is null ? "Have the pal guard the current position." : $"Have {target} guard the current position.",
             new[] { "guard this", "defend this", "stay here", "hold position", "hold here" }),
        };

        var emitted = new List<PalDirective>();
        var rejected = new List<string>();
        var allowSet = new HashSet<string>(allowedActions, StringComparer.OrdinalIgnoreCase);

        foreach ((string action, string detail, string[] cues) in candidates)
        {
            if (!cues.Any(c => q.Contains(c, StringComparison.Ordinal)))
            {
                continue;
            }
            if (!allowSet.Contains(action))
            {
                rejected.Add($"{action} — not in AutomationOptions.AllowedActions");
                continue;
            }
            emitted.Add(new PalDirective(
                Action: action,
                TargetPal: target,
                Detail: detail,
                OrderIndex: emitted.Count,
                CueMatched: cues.First(c => q.Contains(c, StringComparison.Ordinal)).Trim()));
        }

        string reason = emitted.Count > 0
            ? $"Emitted {emitted.Count} directive(s)."
            : rejected.Count > 0
                ? "Every cue that fired maps to an action the operator hasn't allowlisted."
                : "No known cue matched the utterance.";

        return new DirectivePlan(
            Utterance: utterance,
            Directives: emitted,
            RejectedCandidates: rejected,
            Reason: reason,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }
}

/// <summary>
/// One ordered directive produced by <see cref="DirectiveIntentTranslator"/>.
/// </summary>
/// <param name="Action">Action id — must match an entry in <c>AutomationOptions.AllowedActions</c>.</param>
/// <param name="TargetPal">Optional pal name the directive addresses.</param>
/// <param name="Detail">One-sentence plain-English explanation.</param>
/// <param name="OrderIndex">Zero-based order in the plan.</param>
/// <param name="CueMatched">Which cue phrase matched in the utterance.</param>
public sealed record PalDirective(
    string Action,
    string? TargetPal,
    string Detail,
    int OrderIndex,
    string CueMatched);

/// <summary>
/// Full output of <see cref="DirectiveIntentTranslator.Translate"/>.
/// </summary>
/// <param name="Utterance">Original player utterance.</param>
/// <param name="Directives">Ordered plan of allowlisted directives (may be empty).</param>
/// <param name="RejectedCandidates">Candidates that matched a cue but were blocked by the allowlist.</param>
/// <param name="Reason">Plain-English explanation of the outcome.</param>
/// <param name="CapturedAtUtc">When the plan was captured (UTC).</param>
public sealed record DirectivePlan(
    string Utterance,
    IReadOnlyList<PalDirective> Directives,
    IReadOnlyList<string> RejectedCandidates,
    string Reason,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// Wire-level request shape for <c>POST /api/directives/plan</c>.
/// </summary>
public sealed class DirectivePlanRequest
{
    /// <summary>Player utterance to translate.</summary>
    public string? Utterance { get; init; }
    /// <summary>Optional pal name the utterance addresses.</summary>
    public string? AddressedPal { get; init; }
}
