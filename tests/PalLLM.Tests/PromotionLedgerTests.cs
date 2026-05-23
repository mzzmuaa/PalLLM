using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="PromotionLedger"/>. Contract pinned here:
///
/// 1. Fresh ledger → empty snapshot with zero candidates.
/// 2. Observations accumulate per task class and cap at
///    <see cref="PromotionLedger.PerTaskWindow"/> (oldest-first eviction).
/// 3. Promotion criterion is exactly: ≥20 observations AND ≥95%
///    success rate AND no disagreement-block / human-override in the
///    last 10.
/// 4. Invalid outcome strings are rejected with ArgumentException so
///    the wire surface can't silently record garbage.
/// 5. Summary's most-common-pattern hint is deterministic (tie-broken
///    alphabetically) so dashboards don't flap.
/// 6. Success rate / recommendation strings are stable and carry the
///    right "not ready" phrasing for each failure mode.
/// </summary>
public sealed class PromotionLedgerTests
{
    [Test]
    public void Snapshot_FreshLedger_ReturnsEmpty()
    {
        var ledger = new PromotionLedger();

        PromotionSummary summary = ledger.Snapshot();

        Assert.That(summary.Tasks, Is.Empty);
        Assert.That(summary.PromotionCandidateCount, Is.EqualTo(0));
    }

    [Test]
    public void Record_ValidOutcome_ReturnsStoredObservation()
    {
        var ledger = new PromotionLedger();

        PromotionObservation stored = ledger.Record(
            taskClass: "ImplementDraft",
            patternId: "duo-branch-tournament",
            outcome: "success",
            note: "Worker draft accepted by Judge");

        Assert.That(stored.Id, Has.Length.EqualTo(12));
        Assert.That(stored.TaskClass, Is.EqualTo("ImplementDraft"));
        Assert.That(stored.PatternId, Is.EqualTo("duo-branch-tournament"));
        Assert.That(stored.Outcome, Is.EqualTo(PromotionLedger.OutcomeSuccess));
        Assert.That(stored.Note, Is.EqualTo("Worker draft accepted by Judge"));
    }

    [Test]
    public void Record_UnknownOutcome_Throws()
    {
        var ledger = new PromotionLedger();

        Assert.That(
            () => ledger.Record("task", "pattern", "maybe"),
            Throws.InstanceOf<ArgumentException>().With.Message.Contains("not recognised"));
    }

    [Test]
    public void Record_MissingInputs_Throws()
    {
        var ledger = new PromotionLedger();

        Assert.That(() => ledger.Record("", "pattern", "success"), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => ledger.Record("task", "", "success"), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => ledger.Record("task", "pattern", ""), Throws.InstanceOf<ArgumentException>());
        Assert.That(() => ledger.Record(null!, "pattern", "success"), Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void Snapshot_BelowMinObservations_IsNotCandidate()
    {
        var ledger = new PromotionLedger();
        for (int i = 0; i < 5; i++)
        {
            ledger.Record("ImplementDraft", "duo-branch", "success");
        }

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary task = summary.Tasks.Single();

        Assert.That(task.TotalObservations, Is.EqualTo(5));
        Assert.That(task.IsPromotionCandidate, Is.False);
        Assert.That(task.Recommendation, Does.Contain("Not enough data"));
    }

    [Test]
    public void Snapshot_HighSuccessRateCleanRecent_IsCandidate()
    {
        var ledger = new PromotionLedger();
        // 20 total: 19 success + 1 validator-fail (success rate 95%).
        // The validator-fail is in the early window, so the last 10
        // are clean.
        ledger.Record("ImplementDraft", "duo-branch", "validator-fail");
        for (int i = 0; i < 19; i++)
        {
            ledger.Record("ImplementDraft", "duo-branch", "success");
        }

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary task = summary.Tasks.Single();

        Assert.That(task.TotalObservations, Is.EqualTo(20));
        Assert.That(task.SuccessRate, Is.EqualTo(0.95).Within(1e-9));
        Assert.That(task.IsPromotionCandidate, Is.True);
        Assert.That(task.Recommendation, Does.Contain("Stable"));
        Assert.That(summary.PromotionCandidateCount, Is.EqualTo(1));
    }

    [Test]
    public void Snapshot_RecentDisagreementBlock_PreventsPromotion()
    {
        var ledger = new PromotionLedger();
        // 19 successes then one disagreement-block in the last slot.
        for (int i = 0; i < 19; i++)
        {
            ledger.Record("HighRisk", "duo-parallel", "success");
        }
        ledger.Record("HighRisk", "duo-parallel", "disagreement-block");

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary task = summary.Tasks.Single();

        Assert.That(task.TotalObservations, Is.EqualTo(20));
        Assert.That(task.SuccessRate, Is.EqualTo(0.95).Within(1e-9));
        Assert.That(task.IsPromotionCandidate, Is.False,
            "A block in the recent window must block promotion even with 95% overall success.");
        Assert.That(task.Recommendation, Does.Contain("disagreement-block").Or.Contain("human-override"));
    }

    [Test]
    public void Snapshot_RecentHumanOverride_PreventsPromotion()
    {
        var ledger = new PromotionLedger();
        for (int i = 0; i < 19; i++)
        {
            ledger.Record("Audit", "dense-appeal", "success");
        }
        ledger.Record("Audit", "dense-appeal", "human-override");

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary task = summary.Tasks.Single();

        Assert.That(task.IsPromotionCandidate, Is.False);
    }

    [Test]
    public void Snapshot_LowSuccessRate_PreventsPromotion()
    {
        var ledger = new PromotionLedger();
        for (int i = 0; i < 15; i++)
        {
            ledger.Record("ToolExecution", "duo-watchdog", "success");
        }
        for (int i = 0; i < 10; i++)
        {
            ledger.Record("ToolExecution", "duo-watchdog", "validator-fail");
        }

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary task = summary.Tasks.Single();

        Assert.That(task.TotalObservations, Is.EqualTo(25));
        Assert.That(task.SuccessRate, Is.EqualTo(0.60).Within(1e-9));
        Assert.That(task.IsPromotionCandidate, Is.False);
        Assert.That(task.Recommendation, Does.Contain("below"));
    }

    [Test]
    public void Record_BeyondWindow_EvictsOldest()
    {
        var ledger = new PromotionLedger();
        int totalToInsert = PromotionLedger.PerTaskWindow + 25;
        for (int i = 0; i < totalToInsert; i++)
        {
            ledger.Record("ImplementDraft", "pattern", i < 25 ? "validator-fail" : "success");
        }

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary task = summary.Tasks.Single();

        Assert.That(task.TotalObservations, Is.EqualTo(PromotionLedger.PerTaskWindow),
            "Ledger should cap at PerTaskWindow and evict oldest.");
        // All 25 validator-fails were at the front, so after eviction
        // the window should be pure successes.
        Assert.That(task.SuccessRate, Is.EqualTo(1.0).Within(1e-9));
    }

    [Test]
    public void Snapshot_MostCommonPattern_DeterministicTieBreaking()
    {
        var ledger = new PromotionLedger();
        // Equal counts for two patterns → alphabetical tie-break.
        ledger.Record("task", "pattern-zebra", "success");
        ledger.Record("task", "pattern-alpha", "success");
        ledger.Record("task", "pattern-zebra", "success");
        ledger.Record("task", "pattern-alpha", "success");

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary task = summary.Tasks.Single();

        Assert.That(task.MostCommonPatternId, Is.EqualTo("pattern-alpha"),
            "Tie-break must be ordinal-alphabetical so dashboards don't flap.");
    }

    [Test]
    public void Snapshot_MultipleTaskClasses_SortedAlphabetically()
    {
        var ledger = new PromotionLedger();
        ledger.Record("ZLast", "p", "success");
        ledger.Record("AFirst", "p", "success");
        ledger.Record("MMiddle", "p", "success");

        PromotionSummary summary = ledger.Snapshot();

        Assert.That(summary.Tasks.Select(t => t.TaskClass),
            Is.EqualTo(new[] { "AFirst", "MMiddle", "ZLast" }));
    }
}
