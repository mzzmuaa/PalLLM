using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="PromotionApplyPreviewBuilder"/>. Contract pinned:
///
/// 1. Every preview carries a non-empty DiffPreview / SafetyWarnings
///    (1-3 items) / RollbackCommand / Provenance regardless of task
///    class — the builder never fails.
/// 2. Recognised task classes produce previews that mention their
///    specific target file (fallback-director → FallbackBehaviorEngine;
///    duo patterns → DuoOrchestratorPlanner; live-inference →
///    InferenceClient via PalLlmOptions; disagreement → DisagreementDetector).
/// 3. Unknown task classes fall through to a generic preview with
///    operator-assigned target wording.
/// 4. Every preview's Provenance is a ProofPacket tagged subsystem
///    'promotion-apply-preview' with HumanReviewRequired=true — hard-code
///    changes are never auto-applied.
/// 5. RollbackCommand is a single-line git command so it can be copied
///    verbatim into a terminal.
/// </summary>
public sealed class PromotionApplyPreviewBuilderTests
{
    private static PromotionSuggestion BuildSuggestion(string taskClass, string patternId = "test-pattern", string? targetFile = null)
    {
        var proofProvenance = ProofPacketBuilder.Build(
            subsystem: "promotion-suggester",
            decision: $"promote-pattern={patternId}",
            primaryReason: "stable pattern observed",
            evidenceLines: new[] { $"TaskClass={taskClass}", $"Pattern={patternId}" },
            rollbackPath: "(none)",
            confidence: "high",
            humanReviewRequired: true);

        return new PromotionSuggestion(
            TaskClass: taskClass,
            PatternId: patternId,
            TargetFile: targetFile ?? $"src/test/{taskClass}.cs",
            SuggestedChange: "Promote this pattern into a deterministic rule.",
            EvidenceSummary: "25 observations over the task; success rate 100%.",
            RollbackPath: "Revert the promotion.",
            Provenance: proofProvenance);
    }

    [Test]
    public void Build_FallbackDirector_ProducesEngineTargetPreview()
    {
        PromotionSuggestion s = BuildSuggestion("fallback-director", patternId: "stealth-withdraw",
            targetFile: "src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.DiffPreview, Does.Contain("FallbackBehaviorEngine.cs"));
        Assert.That(preview.DiffPreview, Does.Contain("stealth-withdraw"));
        Assert.That(preview.DiffPreview, Does.Contain("TryStealthWithdraw"),
            "PascalCase helper must convert kebab-case strategy ids to method names.");
        Assert.That(preview.RollbackCommand, Does.StartWith("git checkout HEAD"));
    }

    [Test]
    public void Build_DuoBranchTournament_PinsPatternInPlanner()
    {
        PromotionSuggestion s = BuildSuggestion("duo-branch-tournament",
            targetFile: "src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.DiffPreview, Does.Contain("DuoOrchestratorPlanner"));
        Assert.That(preview.DiffPreview, Does.Contain("SelectPattern"));
        Assert.That(preview.DiffPreview, Does.Contain("DuoCooperationPattern."),
            "Duo previews must reference the cooperation-pattern enum so the pinned value is explicit.");
    }

    [Test]
    public void Build_LiveInference_SuggestsTighteningCircuitBreaker()
    {
        PromotionSuggestion s = BuildSuggestion("live-inference", patternId: "qwen3.6:35b-a3b",
            targetFile: "src/PalLLM.Domain/Inference/InferenceClient.cs");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.DiffPreview, Does.Contain("CircuitBreaker"));
        Assert.That(preview.DiffPreview, Does.Contain("qwen3.6:35b-a3b"));
    }

    [Test]
    public void Build_DisagreementDetector_TightensAgreeThreshold()
    {
        PromotionSuggestion s = BuildSuggestion("duo-disagreement-detector",
            targetFile: "src/PalLLM.Domain/Runtime/DisagreementDetector.cs");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.DiffPreview, Does.Contain("0.85"));
        Assert.That(preview.DiffPreview, Does.Contain("0.87"));
        Assert.That(preview.DiffPreview, Does.Contain("DisagreementDetector.cs"));
    }

    [Test]
    public void Build_RateLimiterCandidate_ProducesOptionsTweak()
    {
        PromotionSuggestion s = BuildSuggestion("rate-limiter", patternId: "sliding-window");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.DiffPreview, Does.Contain("MaxCharacterRequestsPerMinute"));
        Assert.That(preview.SafetyWarnings, Is.Not.Empty);
    }

    [Test]
    public void Build_TierTransitionCandidate_SuggestsDefaultTierList()
    {
        PromotionSuggestion s = BuildSuggestion("model-tier-transition", patternId: "small->large");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.DiffPreview, Does.Contain("ModelTiers"));
        Assert.That(preview.DiffPreview, Does.Contain("small->large"));
    }

    [Test]
    public void Build_UnknownTaskClass_FallsThroughToGenericTemplate()
    {
        PromotionSuggestion s = BuildSuggestion("my-custom-surface");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.DiffPreview, Does.Contain("Generic promotion template"));
        Assert.That(preview.DiffPreview, Does.Contain("my-custom-surface"));
        Assert.That(preview.RollbackCommand, Does.Contain("git"));
    }

    [Test]
    public void Build_EveryPreviewCarriesProvenanceAndHumanReview()
    {
        foreach (string taskClass in new[]
        {
            "fallback-director",
            "duo-branch-tournament",
            "live-inference",
            "duo-disagreement-detector",
            "rate-limiter",
            "model-tier-transition",
            "unknown",
        })
        {
            PromotionSuggestion s = BuildSuggestion(taskClass);
            PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

            Assert.That(preview.Provenance, Is.Not.Null, $"{taskClass} preview missing provenance.");
            Assert.That(preview.Provenance.Subsystem, Is.EqualTo("promotion-apply-preview"));
            Assert.That(preview.Provenance.HumanReviewRequired, Is.True,
                $"{taskClass} preview must mark HumanReviewRequired=true.");
            Assert.That(preview.DiffPreview, Is.Not.Empty);
            Assert.That(preview.SafetyWarnings, Is.Not.Empty);
            Assert.That(preview.RollbackCommand, Is.Not.Empty);
        }
    }

    [Test]
    public void Build_SafetyWarnings_CapAtThreeItems()
    {
        PromotionSuggestion s = BuildSuggestion("fallback-director", patternId: "stealth-withdraw");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.SafetyWarnings.Count, Is.LessThanOrEqualTo(3),
            "SafetyWarnings is capped at 3 entries so the preview stays readable.");
    }

    [Test]
    public void Build_RollbackCommand_IsSingleLineShellCommand()
    {
        PromotionSuggestion s = BuildSuggestion("fallback-director", patternId: "stealth-withdraw");

        PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(s);

        Assert.That(preview.RollbackCommand, Does.Not.Contain("\n"),
            "RollbackCommand must be a single line so operators can copy-paste it into a terminal.");
    }
}
