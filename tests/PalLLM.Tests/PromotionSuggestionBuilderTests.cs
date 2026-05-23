using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="PromotionSuggestionBuilder"/>. Contract pinned here:
///
/// 1. An empty summary → empty suggestion set with zero candidates.
/// 2. Non-candidate tasks are skipped — a task class that's still
///    collecting evidence never produces a suggestion.
/// 3. Recognised task classes get tailored target files and changes
///    (fallback-director → FallbackBehaviorEngine.cs; duo patterns →
///    DuoOrchestratorPlanner.cs; live-inference → InferenceClient.cs;
///    disagreement → DisagreementDetector.cs).
/// 4. Unknown task classes get a generic suggestion that still carries
///    a concrete rollback path so the builder never fails.
/// 5. Every suggestion carries a full ProofPacket with subsystem
///    'promotion-suggester' and HumanReviewRequired=true so the
///    suggestion itself has audit provenance.
/// 6. Multiple candidates in one summary → one suggestion each, in the
///    same order the ledger surfaced them.
/// </summary>
public sealed class PromotionSuggestionBuilderTests
{
    private static PromotionTaskSummary BuildTask(
        string taskClass,
        string? patternId = "pattern",
        bool candidate = true,
        int total = 25,
        int success = 25,
        int disagreement = 0,
        int validatorFail = 0,
        int humanOverride = 0)
    {
        double rate = total == 0 ? 0.0 : (double)success / total;
        return new PromotionTaskSummary(
            TaskClass: taskClass,
            TotalObservations: total,
            SuccessCount: success,
            DisagreementBlockCount: disagreement,
            ValidatorFailCount: validatorFail,
            HumanOverrideCount: humanOverride,
            SuccessRate: rate,
            MostCommonPatternId: patternId,
            IsPromotionCandidate: candidate,
            Recommendation: candidate ? "Stable" : "Not ready");
    }

    private static PromotionSummary BuildSummary(params PromotionTaskSummary[] tasks)
    {
        int candidates = 0;
        foreach (PromotionTaskSummary t in tasks) { if (t.IsPromotionCandidate) { candidates++; } }
        return new PromotionSummary(
            CapturedAtUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero),
            Tasks: tasks,
            PromotionCandidateCount: candidates);
    }

    [Test]
    public void Build_EmptySummary_ReturnsEmptySet()
    {
        PromotionSuggestionSet set = PromotionSuggestionBuilder.Build(BuildSummary());

        Assert.That(set.Suggestions, Is.Empty);
        Assert.That(set.CandidateCount, Is.EqualTo(0));
    }

    [Test]
    public void Build_SkipsNonCandidateTasks()
    {
        PromotionSummary summary = BuildSummary(
            BuildTask("fallback-director", candidate: false, total: 10, success: 10));

        PromotionSuggestionSet set = PromotionSuggestionBuilder.Build(summary);

        Assert.That(set.Suggestions, Is.Empty,
            "Tasks still collecting evidence must not produce suggestions.");
    }

    [Test]
    public void Build_FallbackDirectorCandidate_TargetsFallbackEngine()
    {
        PromotionSummary summary = BuildSummary(BuildTask("fallback-director", patternId: "stealth-withdraw"));

        PromotionSuggestion suggestion = PromotionSuggestionBuilder.Build(summary).Suggestions.Single();

        Assert.That(suggestion.TargetFile, Is.EqualTo("src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs"));
        Assert.That(suggestion.SuggestedChange, Does.Contain("stealth-withdraw"));
        Assert.That(suggestion.RollbackPath, Does.Contain("Revert"));
    }

    [Test]
    public void Build_DuoBranchTournamentCandidate_TargetsPlanner()
    {
        PromotionSummary summary = BuildSummary(BuildTask("duo-branch-tournament", patternId: "worker-three-branches"));

        PromotionSuggestion suggestion = PromotionSuggestionBuilder.Build(summary).Suggestions.Single();

        Assert.That(suggestion.TargetFile, Does.Contain("DuoOrchestratorPlanner.cs"));
        Assert.That(suggestion.SuggestedChange, Does.Contain("SelectPattern"));
    }

    [Test]
    public void Build_LiveInferenceCandidate_TargetsInferenceClient()
    {
        PromotionSummary summary = BuildSummary(BuildTask("live-inference", patternId: "normal-flow"));

        PromotionSuggestion suggestion = PromotionSuggestionBuilder.Build(summary).Suggestions.Single();

        Assert.That(suggestion.TargetFile, Does.Contain("InferenceClient.cs"));
        Assert.That(suggestion.SuggestedChange, Does.Contain("circuit breaker"));
    }

    [Test]
    public void Build_DisagreementDetectorCandidate_TargetsDetector()
    {
        PromotionSummary summary = BuildSummary(BuildTask("duo-disagreement-detector", patternId: "stable-agreement"));

        PromotionSuggestion suggestion = PromotionSuggestionBuilder.Build(summary).Suggestions.Single();

        Assert.That(suggestion.TargetFile, Does.Contain("DisagreementDetector.cs"));
        Assert.That(suggestion.SuggestedChange, Does.Contain("threshold"));
    }

    [Test]
    public void Build_UnknownTaskClass_ReturnsGenericSuggestion()
    {
        PromotionSummary summary = BuildSummary(BuildTask("my-custom-task", patternId: "my-pattern"));

        PromotionSuggestion suggestion = PromotionSuggestionBuilder.Build(summary).Suggestions.Single();

        Assert.That(suggestion.TargetFile, Does.Contain("operator-assigned"));
        Assert.That(suggestion.SuggestedChange, Does.Contain("my-pattern"));
        Assert.That(suggestion.SuggestedChange, Does.Contain("my-custom-task"));
        Assert.That(suggestion.RollbackPath, Does.Contain("Delete"));
    }

    [Test]
    public void Build_EveryCandidateCarriesFullProvenance()
    {
        PromotionSummary summary = BuildSummary(
            BuildTask("fallback-director", patternId: "stealth-withdraw"));

        PromotionSuggestion suggestion = PromotionSuggestionBuilder.Build(summary).Suggestions.Single();

        Assert.That(suggestion.Provenance, Is.Not.Null);
        Assert.That(suggestion.Provenance.Subsystem, Is.EqualTo("promotion-suggester"));
        Assert.That(suggestion.Provenance.HumanReviewRequired, Is.True,
            "Every hard-code suggestion requires a human review gate — never auto-promotion.");
        Assert.That(suggestion.Provenance.Confidence, Is.EqualTo("high"));
        Assert.That(suggestion.Provenance.Evidence, Is.Not.Empty);
        Assert.That(suggestion.Provenance.Id, Has.Length.EqualTo(12));
    }

    [Test]
    public void Build_EvidenceSummary_ContainsRateAndObservationCount()
    {
        PromotionSummary summary = BuildSummary(
            BuildTask("fallback-director", patternId: "stealth-withdraw", total: 40, success: 39));

        PromotionSuggestion suggestion = PromotionSuggestionBuilder.Build(summary).Suggestions.Single();

        Assert.That(suggestion.EvidenceSummary, Does.Contain("40 observations"));
        Assert.That(suggestion.EvidenceSummary, Does.Contain("39/40"));
        Assert.That(suggestion.EvidenceSummary, Does.Contain("97.5%"));
        Assert.That(suggestion.EvidenceSummary, Does.Contain("stealth-withdraw"));
    }

    [Test]
    public void Build_MultipleCandidates_AllSurfaced()
    {
        PromotionSummary summary = BuildSummary(
            BuildTask("fallback-director", patternId: "stealth-withdraw"),
            BuildTask("live-inference", patternId: "normal-flow"),
            BuildTask("non-candidate", patternId: "noise", candidate: false, total: 10, success: 10));

        PromotionSuggestionSet set = PromotionSuggestionBuilder.Build(summary);

        Assert.That(set.Suggestions, Has.Count.EqualTo(2),
            "Non-candidate tasks must be skipped; the two candidates must both produce suggestions.");
        Assert.That(set.Suggestions[0].TaskClass, Is.EqualTo("fallback-director"));
        Assert.That(set.Suggestions[1].TaskClass, Is.EqualTo("live-inference"));
    }

    [Test]
    public void BuildForTask_Directly_MatchesSummaryBuild()
    {
        PromotionTaskSummary task = BuildTask("fallback-director", patternId: "stealth-withdraw");
        DateTimeOffset now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        PromotionSuggestion direct = PromotionSuggestionBuilder.BuildForTask(task, now);
        PromotionSuggestion viaSummary = PromotionSuggestionBuilder.Build(BuildSummary(task)).Suggestions.Single();

        Assert.That(direct.TargetFile, Is.EqualTo(viaSummary.TargetFile));
        Assert.That(direct.SuggestedChange, Is.EqualTo(viaSummary.SuggestedChange));
        Assert.That(direct.RollbackPath, Is.EqualTo(viaSummary.RollbackPath));
    }
}
