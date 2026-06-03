using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Covers the policy / recipe / run-mode selection branches of
/// <see cref="ModelCollaborationDecisionPlanner"/> behind
/// <c>POST /api/inference/collaboration/plan</c>. The selection is pure
/// request-driven logic, so each branch is reachable by varying one signal on
/// the request. (ModelTierTests already pins the high-risk + tool-heavy +
/// parallel path; these cover the remaining policy arms + the CPU-only run
/// mode.)
/// </summary>
public sealed class ModelCollaborationDecisionPlannerTests
{
    private static ModelCollaborationDecisionPlanner NewPlanner()
    {
        var options = new PalLlmOptions();
        return new ModelCollaborationDecisionPlanner(
            new ModelCollaborationPlanner(
                options,
                new ModelTierOrchestrator(options, new NullModelAvailabilityProbe())));
    }

    private static string PolicyFor(ModelCollaborationDecisionRequest request) =>
        NewPlanner().Plan(request).SelectedPolicyId;

    [Test]
    public void ToolHeavy_NonHighRisk_SelectsToolHeavyGuarded() =>
        Assert.That(
            PolicyFor(new ModelCollaborationDecisionRequest(
                Task: "Apply a batch of repo edits through tools", RiskLevel: "medium", ToolHeavy: true)),
            Is.EqualTo("tool-heavy-guarded"));

    [Test]
    public void VisionWork_SelectsFrontendVisualLoop() =>
        Assert.That(
            PolicyFor(new ModelCollaborationDecisionRequest(
                Task: "Describe the scene and lay out the HUD", RiskLevel: "medium", NeedsVision: true)),
            Is.EqualTo("frontend-visual-loop"));

    [Test]
    public void LargeContext_SelectsContextCompiler() =>
        Assert.That(
            PolicyFor(new ModelCollaborationDecisionRequest(
                Task: "Digest this input", RiskLevel: "medium", LargeContext: true)),
            Is.EqualTo("context-compiler-then-dense-reasoning"));

    [Test]
    public void LowRisk_Simple_SelectsLowRiskFastLane() =>
        Assert.That(
            PolicyFor(new ModelCollaborationDecisionRequest(Task: "Say hello", RiskLevel: "low")),
            Is.EqualTo("low-risk-fast-lane"));

    [Test]
    public void MediumRisk_Simple_SelectsMediumRiskDefault() =>
        Assert.That(
            PolicyFor(new ModelCollaborationDecisionRequest(Task: "Adjust one config value", RiskLevel: "medium")),
            Is.EqualTo("medium-risk-fast-implement-dense-review"));

    [Test]
    public void CpuOnly_ProducesCoherentSequentialDecision()
    {
        ModelCollaborationDecision decision = NewPlanner().Plan(new ModelCollaborationDecisionRequest(
            Task: "Plan a small change", RiskLevel: "medium", CpuOnly: true, PreferParallel: false));

        Assert.That(decision.RunMode, Is.Not.Empty);
        Assert.That(decision.SelectedRecipeId, Is.Not.Empty);
        Assert.That(decision.SelectedPolicyId, Is.Not.Empty);
        Assert.That(decision.FastLaneModel, Is.Not.Null);
        Assert.That(decision.DeliberateLaneModel, Is.Not.Null);
    }
}
