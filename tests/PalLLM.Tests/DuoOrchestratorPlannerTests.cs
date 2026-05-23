using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="DuoOrchestratorPlanner"/>. Contract pinned here:
///
/// 1. With no Worker and no Judge bound, the planner returns the
///    <see cref="DuoCooperationPattern.DeterministicOnly"/> pattern
///    with a clear operator nudge and no thinking/context budgets.
/// 2. With only one of Worker/Judge bound, the planner returns
///    <see cref="DuoCooperationPattern.SingleRoleFallback"/> and
///    names the missing role.
/// 3. With both roles bound, task kind → pattern selection matches
///    the Qwen Duo architecture brief's "best division of labor"
///    (implement → architect/implementer/auditor; parallel-candidates
///    → branch tournament; media → draft-then-finalise; etc.).
/// 4. High-risk tasks always take <see cref="DuoCooperationPattern.ParallelDisagreement"/>.
/// 5. Constrained hardware forces
///    <see cref="DuoCooperationPattern.SequentialSwap"/> regardless of
///    task kind, because the operator's VRAM can't hold both models.
/// 6. Every plan carries non-empty steps + why + escalation so the
///    output is always actionable.
/// </summary>
public sealed class DuoOrchestratorPlannerTests
{
    private static DuoOrchestratorPlanner PlannerFor(params ModelRoleBinding[] bindings)
    {
        var options = new PalLlmOptions();
        foreach (var binding in bindings)
        {
            options.ModelRoles.Add(binding);
        }
        return new DuoOrchestratorPlanner(new ModelRoleRegistry(options));
    }

    private static ModelRoleBinding Worker(string id = "qwen-fast", bool enabled = true)
        => new() { Role = ModelRole.Worker, Id = id, ModelId = "qwen3.6:35b-a3b", Enabled = enabled };

    private static ModelRoleBinding Judge(string id = "qwen-dense", bool enabled = true)
        => new() { Role = ModelRole.Judge, Id = id, ModelId = "qwen3.6:27b", Enabled = enabled };

    [Test]
    public void Plan_NoRolesBound_ReturnsDeterministicOnlyWithNudge()
    {
        DuoOrchestratorPlanner planner = PlannerFor();

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.ImplementDraft });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.DeterministicOnly));
        Assert.That(plan.Escalation, Does.Contain("Declare a Worker binding"));
        Assert.That(plan.ThinkingMode.Worker, Is.Null);
        Assert.That(plan.ThinkingMode.Judge, Is.Null);
        Assert.That(plan.Steps, Is.Not.Empty);
    }

    [Test]
    public void Plan_WorkerOnly_ReturnsSingleRoleFallbackNamingMissing()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker());

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.Audit });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.SingleRoleFallback));
        Assert.That(plan.Why, Does.Contain("Worker-only"));
        Assert.That(plan.Escalation, Does.Contain("Judge binding"));
        Assert.That(plan.ThinkingMode.Worker, Is.Not.Null);
        Assert.That(plan.ThinkingMode.Judge, Is.Null);
    }

    [Test]
    public void Plan_JudgeOnly_ReturnsSingleRoleFallbackNamingMissing()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.ParallelCandidates });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.SingleRoleFallback));
        Assert.That(plan.Why, Does.Contain("Judge-only"));
        Assert.That(plan.Escalation, Does.Contain("Worker binding"));
        Assert.That(plan.ThinkingMode.Worker, Is.Null);
        Assert.That(plan.ThinkingMode.Judge, Is.Not.Null);
    }

    [Test]
    public void Plan_DisabledBinding_DoesNotCountAsActive()
    {
        DuoOrchestratorPlanner planner = PlannerFor(
            Worker("qwen-fast", enabled: false),
            Judge("qwen-dense", enabled: true));

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.ImplementDraft });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.SingleRoleFallback));
        Assert.That(plan.Why, Does.Contain("Judge-only"));
    }

    [Test]
    public void Plan_ImplementDraft_ReturnsArchitectImplementerAuditor()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest
        {
            Kind = DuoTaskKind.ImplementDraft,
            Risk = DuoRiskLevel.Low,
            Hardware = DuoHardwareTier.Standard,
        });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.ArchitectThenImplementerThenAuditor));
        // Must have exactly three steps: Judge → Worker → Judge.
        Assert.That(plan.Steps, Has.Count.EqualTo(3));
        Assert.That(plan.Steps[0].Actor, Is.EqualTo("Judge"));
        Assert.That(plan.Steps[1].Actor, Is.EqualTo("Worker"));
        Assert.That(plan.Steps[2].Actor, Is.EqualTo("Judge"));
    }

    [Test]
    public void Plan_ParallelCandidates_ReturnsBranchTournament()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.ParallelCandidates });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.BranchTournament));
        Assert.That(plan.Steps.Any(s => s.Name == "branches" && s.Actor == "Worker"), Is.True);
        Assert.That(plan.Steps.Any(s => s.Name == "pick-winner" && s.Actor == "Judge"), Is.True);
    }

    [Test]
    public void Plan_FinalSynthesis_ReturnsFanOutThenSynthesis()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.FinalSynthesis });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.FanOutThenSynthesis));
    }

    [Test]
    public void Plan_MediaPrompting_ReturnsDraftThenFinalize()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.MediaPrompting });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.DraftThenFinalize));
    }

    [Test]
    public void Plan_ToolExecution_ReturnsDuoWatchdog()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.ToolExecution });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.DuoWatchdog));
    }

    [Test]
    public void Plan_HighRisk_AlwaysReturnsParallelDisagreement()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        // Even a normally "fan-out" task becomes parallel-disagreement
        // when the caller says it's high risk.
        DuoPlan plan = planner.Plan(new DuoPlanRequest
        {
            Kind = DuoTaskKind.ParallelCandidates,
            Risk = DuoRiskLevel.High,
        });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.ParallelDisagreement));
        Assert.That(plan.Escalation, Does.Contain("human review gate"));
    }

    [Test]
    public void Plan_ConstrainedHardware_AlwaysReturnsSequentialSwap()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest
        {
            Kind = DuoTaskKind.ImplementDraft,
            Risk = DuoRiskLevel.Low,
            Hardware = DuoHardwareTier.Constrained,
        });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.SequentialSwap));
        Assert.That(plan.Steps.Any(s => s.Name == "load-worker"), Is.True);
        Assert.That(plan.Steps.Any(s => s.Name == "unload-load-judge"), Is.True);
    }

    [Test]
    public void Plan_LongContextSynthesis_AssignsJudgeTheLongContext()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.LongContextSynthesis });

        Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.WorkerLiveJudgeBackground));
        Assert.That(plan.ContextBudget.Judge, Does.Contain("262K"),
            "Long-context task should route the deep context budget to the Judge.");
    }

    [Test]
    public void Plan_EveryOutcomeCarriesNonEmptySteps()
    {
        DuoOrchestratorPlanner planner = PlannerFor(Worker(), Judge());

        foreach (DuoTaskKind kind in Enum.GetValues<DuoTaskKind>())
        {
            DuoPlan plan = planner.Plan(new DuoPlanRequest { Kind = kind });
            Assert.That(plan.Steps, Is.Not.Empty, $"Plan for {kind} must carry at least one step.");
            Assert.That(plan.Why, Is.Not.Empty);
            Assert.That(plan.Escalation, Is.Not.Empty);
        }
    }
}
