using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

public sealed class InferenceExecutionPlannerTests
{
    [Test]
    public void Plan_WhenFastReactiveTask_UsesCompactPalworldPromptBudget()
    {
        var options = new PalLlmOptions();
        var planner = new InferenceExecutionPlanner(options);
        PalTaskProfile taskProfile = PalTaskRouter.Resolve("bark_perimeter", "Anything moving near the treeline?", PalTaskPriority.Low);

        InferenceExecutionProfile profile = planner.Plan(
            taskProfile,
            new ChatRequest
            {
                TaskTag = "bark_perimeter",
                UserMessage = "Anything moving near the treeline?",
                Priority = PalTaskPriority.Low,
            },
            "unsloth/Qwen3.6-35B-A3B-GGUF");

        Assert.That(profile.ProfileId, Is.EqualTo("fast-reactive"));
        Assert.That(profile.AllowLiveVisionAugmentation, Is.False);
        Assert.That(profile.ChatVisionMaxTokens, Is.Zero);
        Assert.That(profile.EnableThinking, Is.False);
        Assert.That(profile.PromptBudget.MaxPromptChars, Is.EqualTo(2200));
        Assert.That(profile.PromptBudget.MaxKnownBases, Is.EqualTo(2));
        Assert.That(profile.PromptBudget.MaxNearbyHostiles, Is.EqualTo(2));
        Assert.That(profile.PromptBudget.MaxRecentEvents, Is.EqualTo(3));
        Assert.That(profile.PromptBudget.MaxMemorySnippets, Is.EqualTo(2));
    }

    [Test]
    public void Plan_WhenDenseBaseStrategyTask_UsesLargerPromptBudgetAndPreservesThinking()
    {
        var options = new PalLlmOptions();
        var planner = new InferenceExecutionPlanner(options);
        PalTaskProfile taskProfile = PalTaskRouter.Resolve("base_strategy", "Plan the safest expansion path for our base network.", PalTaskPriority.High);

        InferenceExecutionProfile profile = planner.Plan(
            taskProfile,
            new ChatRequest
            {
                TaskTag = "base_strategy",
                UserMessage = "Plan the safest expansion path for our base network.",
                Priority = PalTaskPriority.High,
            },
            "unsloth/Qwen3.6-27B-GGUF");

        Assert.That(profile.ProfileId, Is.EqualTo("dense-deliberate"));
        Assert.That(profile.AllowLiveVisionAugmentation, Is.True);
        Assert.That(profile.ChatVisionMaxTokens, Is.EqualTo(96));
        Assert.That(profile.EnableThinking, Is.True);
        Assert.That(profile.PreserveThinking, Is.True);
        Assert.That(profile.PromptBudget.MaxPromptChars, Is.EqualTo(8000));
        Assert.That(profile.PromptBudget.MaxKnownBases, Is.EqualTo(5));
        Assert.That(profile.PromptBudget.MaxNearbyHostiles, Is.EqualTo(5));
        Assert.That(profile.PromptBudget.MaxRecentEvents, Is.EqualTo(6));
        Assert.That(profile.PromptBudget.MaxMemorySnippets, Is.EqualTo(5));
    }
}
