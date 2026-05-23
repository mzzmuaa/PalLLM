using NUnit.Framework;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Pass 22 — regression coverage for the deterministic
/// <see cref="ChatDispatchPlanner"/>. Every assertion pins a
/// specific (pattern, role bindings) → decision mapping so that a
/// future pass that flips the single-lane runtime to actually invoke
/// the chain can rely on these shapes.
/// </summary>
[TestFixture]
public class ChatDispatchPlannerTests
{
    [Test]
    public void Decide_WhenNoRolesBound_ReturnsDeterministicOnly()
    {
        ModelRoleCoverage coverage = new ModelRoleRegistry(new PalLlmOptions()).GetCoverage();

        ChatDispatchDecision decision = ChatDispatchPlanner.Decide(DuoCooperationPattern.ScoutThenJudge, coverage);

        Assert.That(decision.Mode, Is.EqualTo("deterministic-only"));
        Assert.That(decision.Roles, Is.Empty);
        Assert.That(decision.RequiresInference, Is.False);
        Assert.That(decision.PrimaryRole, Is.Null);
        Assert.That(decision.PrimaryBindingId, Is.Null);
    }

    [Test]
    public void Decide_WhenWorkerOnlyBound_ReturnsSingleRoleWorkerChain()
    {
        var options = new PalLlmOptions();
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Worker,
            Id = "qwen-fast",
            ModelId = "qwen3.6:35b-a3b",
            BaseUrl = "http://127.0.0.1:11434/v1/",
            Enabled = true,
        });
        ModelRoleCoverage coverage = new ModelRoleRegistry(options).GetCoverage();

        ChatDispatchDecision decision = ChatDispatchPlanner.Decide(DuoCooperationPattern.ScoutThenJudge, coverage);

        Assert.That(decision.Mode, Is.EqualTo("single-role"));
        Assert.That(decision.Roles, Has.Count.EqualTo(1));
        Assert.That(decision.Roles[0], Is.EqualTo("Worker"));
        Assert.That(decision.PrimaryRole, Is.EqualTo("Worker"));
        Assert.That(decision.PrimaryBindingId, Is.EqualTo("qwen-fast"));
        Assert.That(decision.ReviewerRole, Is.Null);
        Assert.That(decision.RequiresInference, Is.True);
    }

    [Test]
    public void Decide_WhenJudgeOnlyBound_ReturnsSingleRoleJudgeChain()
    {
        var options = new PalLlmOptions();
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Judge,
            Id = "qwen-dense",
            ModelId = "qwen3.6:27b",
            BaseUrl = "http://127.0.0.1:11434/v1/",
            Enabled = true,
        });
        ModelRoleCoverage coverage = new ModelRoleRegistry(options).GetCoverage();

        ChatDispatchDecision decision = ChatDispatchPlanner.Decide(DuoCooperationPattern.ScoutThenJudge, coverage);

        Assert.That(decision.Mode, Is.EqualTo("single-role"));
        Assert.That(decision.Roles[0], Is.EqualTo("Judge"));
        Assert.That(decision.PrimaryBindingId, Is.EqualTo("qwen-dense"));
    }

    [Test]
    public void Decide_WhenBothBound_ScoutThenJudge_ReturnsSequentialChain()
    {
        ModelRoleCoverage coverage = BothRolesBoundCoverage();

        ChatDispatchDecision decision = ChatDispatchPlanner.Decide(DuoCooperationPattern.ScoutThenJudge, coverage);

        Assert.That(decision.Mode, Is.EqualTo("duo-sequential"));
        Assert.That(decision.Roles, Is.EqualTo(new[] { "Worker", "Judge" }));
        Assert.That(decision.PrimaryRole, Is.EqualTo("Worker"));
        Assert.That(decision.ReviewerRole, Is.EqualTo("Judge"));
        Assert.That(decision.RequiresInference, Is.True);
    }

    [Test]
    public void Decide_WhenBothBound_FanOut_ReturnsFanOutChain()
    {
        ModelRoleCoverage coverage = BothRolesBoundCoverage();

        ChatDispatchDecision decision = ChatDispatchPlanner.Decide(DuoCooperationPattern.FanOutThenSynthesis, coverage);

        Assert.That(decision.Mode, Is.EqualTo("duo-fanout"));
        Assert.That(decision.Roles.Count, Is.EqualTo(4));
        Assert.That(decision.Roles[0], Is.EqualTo("Worker"));
        Assert.That(decision.Roles[^1], Is.EqualTo("Judge"));
    }

    [Test]
    public void Decide_WhenBothBound_ParallelDisagreement_ReturnsParallelChain()
    {
        ModelRoleCoverage coverage = BothRolesBoundCoverage();

        ChatDispatchDecision decision = ChatDispatchPlanner.Decide(DuoCooperationPattern.ParallelDisagreement, coverage);

        Assert.That(decision.Mode, Is.EqualTo("duo-parallel"));
        Assert.That(decision.Roles, Is.EqualTo(new[] { "Worker", "Judge" }));
    }

    [Test]
    public void Decide_WhenBothBound_BranchTournament_ReturnsTournamentChain()
    {
        ModelRoleCoverage coverage = BothRolesBoundCoverage();

        ChatDispatchDecision decision = ChatDispatchPlanner.Decide(DuoCooperationPattern.BranchTournament, coverage);

        Assert.That(decision.Mode, Is.EqualTo("duo-tournament"));
    }

    [Test]
    public void Decide_EveryCooperationPatternReturnsANonEmptyChainWhenBothRolesBound()
    {
        ModelRoleCoverage coverage = BothRolesBoundCoverage();

        // Exhaustive safety net: every enum value must return a
        // well-formed decision when both roles are bound. A future
        // pass that adds a new DuoCooperationPattern will fail here
        // until it adds a mapping in ChatDispatchPlanner.
        foreach (DuoCooperationPattern pattern in Enum.GetValues<DuoCooperationPattern>())
        {
            if (pattern == DuoCooperationPattern.DeterministicOnly || pattern == DuoCooperationPattern.SingleRoleFallback)
            {
                continue; // explicitly handled before role-coverage branching
            }

            ChatDispatchDecision decision = ChatDispatchPlanner.Decide(pattern, coverage);
            Assert.That(decision.Roles, Is.Not.Empty, $"Pattern {pattern} must produce a non-empty role chain.");
            Assert.That(decision.RequiresInference, Is.True, $"Pattern {pattern} must require inference when roles are bound.");
            Assert.That(decision.PrimaryRole, Is.Not.Null, $"Pattern {pattern} must name a primary role.");
        }
    }

    private static ModelRoleCoverage BothRolesBoundCoverage()
    {
        var options = new PalLlmOptions();
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Worker,
            Id = "qwen-fast",
            ModelId = "qwen3.6:35b-a3b",
            BaseUrl = "http://127.0.0.1:11434/v1/",
            Enabled = true,
        });
        options.ModelRoles.Add(new ModelRoleBinding
        {
            Role = ModelRole.Judge,
            Id = "qwen-dense",
            ModelId = "qwen3.6:27b",
            BaseUrl = "http://127.0.0.1:11434/v1/",
            Enabled = true,
        });
        return new ModelRoleRegistry(options).GetCoverage();
    }
}
