using NUnit.Framework;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 31 / C3 — regression coverage for
/// <see cref="DirectiveIntentTranslator"/>. Pinned contract:
/// <list type="bullet">
///   <item>Only emits directives whose action id is in the allowlist.</item>
///   <item>Cue matching is case-insensitive and substring-based.</item>
///   <item>Target pal name flows through to each emitted directive.</item>
///   <item>Empty utterance returns an empty plan with reason 'empty-utterance'.</item>
///   <item>Unallowed candidates land in RejectedCandidates with a diagnostic.</item>
/// </list>
/// </summary>
[TestFixture]
public class DirectiveIntentTranslatorTests
{
    private static readonly string[] AllActions =
    [
        "stop_current_task",
        "recall_pals",
        "help_in_combat",
        "gather_resources",
        "request_craft_queue",
        "mark_waypoint",
        "follow_player",
        "guard_position",
    ];

    [Test]
    public void Translate_EmptyUtterance_ReturnsEmptyPlanWithReason()
    {
        DirectivePlan plan = DirectiveIntentTranslator.Translate("", AllActions);

        Assert.That(plan.Directives, Is.Empty);
        Assert.That(plan.Reason, Is.EqualTo("empty-utterance"));
    }

    [Test]
    public void Translate_StopAndHelpFight_EmitsOrderedDirectives()
    {
        DirectivePlan plan = DirectiveIntentTranslator.Translate(
            "stop mining and help me fight!",
            AllActions);

        Assert.That(plan.Directives, Has.Count.EqualTo(2));
        Assert.That(plan.Directives[0].Action, Is.EqualTo("stop_current_task"));
        Assert.That(plan.Directives[1].Action, Is.EqualTo("help_in_combat"));
        Assert.That(plan.Directives[0].OrderIndex, Is.EqualTo(0));
        Assert.That(plan.Directives[1].OrderIndex, Is.EqualTo(1));
    }

    [Test]
    public void Translate_AddressedPalFlowsToEveryDirective()
    {
        DirectivePlan plan = DirectiveIntentTranslator.Translate(
            "follow me and guard this spot",
            AllActions,
            addressedPal: "Lamball");

        Assert.That(plan.Directives.Count, Is.GreaterThanOrEqualTo(1));
        foreach (PalDirective d in plan.Directives)
        {
            Assert.That(d.TargetPal, Is.EqualTo("Lamball"));
        }
    }

    [Test]
    public void Translate_WhenActionNotAllowlisted_GoesIntoRejectedCandidates()
    {
        // Allowlist includes everything except help_in_combat; the
        // translator should detect the cue but not emit the directive.
        string[] restricted = AllActions.Where(a => a != "help_in_combat").ToArray();

        DirectivePlan plan = DirectiveIntentTranslator.Translate(
            "help me fight please",
            restricted);

        Assert.That(plan.Directives.Any(d => d.Action == "help_in_combat"), Is.False,
            "Non-allowlisted actions must not be emitted.");
        Assert.That(plan.RejectedCandidates.Any(r => r.Contains("help_in_combat", StringComparison.Ordinal)), Is.True,
            "Rejected candidate must appear with a diagnostic.");
    }

    [Test]
    public void Translate_EmptyAllowlist_EmitsNothingButStillClassifies()
    {
        DirectivePlan plan = DirectiveIntentTranslator.Translate(
            "stop and follow me and gather wood",
            Array.Empty<string>());

        Assert.That(plan.Directives, Is.Empty);
        Assert.That(plan.RejectedCandidates.Count, Is.GreaterThanOrEqualTo(3),
            "All three cues must appear in rejected candidates when nothing is allowlisted.");
    }

    [Test]
    public void Translate_CueMatching_IsCaseInsensitive()
    {
        DirectivePlan plan = DirectiveIntentTranslator.Translate(
            "GATHER MORE WOOD",
            AllActions);

        Assert.That(plan.Directives.Any(d => d.Action == "gather_resources"), Is.True);
    }

    [Test]
    public void Translate_NoKnownCue_ReturnsEmptyPlanWithExplanation()
    {
        DirectivePlan plan = DirectiveIntentTranslator.Translate(
            "the weather is nice today",
            AllActions);

        Assert.That(plan.Directives, Is.Empty);
        Assert.That(plan.Reason, Does.Contain("No known cue"));
    }
}
