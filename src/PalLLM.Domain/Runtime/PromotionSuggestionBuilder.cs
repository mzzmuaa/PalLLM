using System.Globalization;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Turns the <see cref="PromotionLedger"/>'s
/// <see cref="PromotionTaskSummary.IsPromotionCandidate"/> signal into
/// an actionable <see cref="PromotionSuggestion"/> — the concrete
/// "here's what to change" step the 2035 doc predicted would become
/// normal once AI-assisted patterns proved themselves stable.
///
/// <para>Deterministic: no inference call, no external I/O. Given a
/// <see cref="PromotionSummary"/>, emits one suggestion per candidate
/// pattern with:</para>
/// <list type="bullet">
///   <item><b>TargetFile</b> — concrete repo-relative path the change
///         would land in (e.g.
///         <c>src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs</c>
///         for <c>fallback-director</c> task-class candidates, or a
///         generic file pointer for custom classes).</item>
///   <item><b>SuggestedChange</b> — one-sentence human-readable recipe.</item>
///   <item><b>EvidenceSummary</b> — per-observation counts and success
///         rate rolled up into a single line so operators can read the
///         suggestion without cross-referencing the ledger.</item>
///   <item><b>RollbackPath</b> — how to revert the hard-code if the
///         deterministic rule misfires.</item>
///   <item><b>Provenance</b> — a <see cref="ProofPacket"/> tagged with
///         subsystem <c>promotion-suggester</c> so the suggestion itself
///         has an audit record.</item>
/// </list>
///
/// <para>Task classes the builder recognises get tailored suggestions
/// (fallback-director, duo-branch-tournament, etc.). Unknown task
/// classes get a generic "promote this pattern to a deterministic rule"
/// suggestion with neutral target guidance so the builder never fails
/// to produce output.</para>
/// </summary>
public static class PromotionSuggestionBuilder
{
    /// <summary>Build suggestions for every promotion candidate in the summary.</summary>
    public static PromotionSuggestionSet Build(PromotionSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        List<PromotionSuggestion> suggestions = new();
        foreach (PromotionTaskSummary task in summary.Tasks)
        {
            if (!task.IsPromotionCandidate) { continue; }
            suggestions.Add(BuildForTask(task, summary.CapturedAtUtc));
        }

        return new PromotionSuggestionSet(
            CapturedAtUtc: summary.CapturedAtUtc,
            CandidateCount: summary.PromotionCandidateCount,
            Suggestions: suggestions);
    }

    /// <summary>Build a single suggestion for an already-known candidate.</summary>
    public static PromotionSuggestion BuildForTask(PromotionTaskSummary task, DateTimeOffset capturedAt)
    {
        ArgumentNullException.ThrowIfNull(task);

        string evidenceSummary = BuildEvidenceSummary(task);
        (string targetFile, string suggestedChange, string rollbackPath) = ResolveTarget(task);

        // Every suggestion carries a proof packet so the suggestion
        // itself has provenance + a stable id downstream evidence stores
        // can dedupe on.
        ProofPacket packet = ProofPacketBuilder.Build(
            subsystem: "promotion-suggester",
            decision: $"promote-pattern={task.MostCommonPatternId ?? "unspecified"}",
            primaryReason: suggestedChange,
            evidenceLines:
            [
                $"TaskClass={task.TaskClass}",
                $"TotalObservations={task.TotalObservations.ToString(CultureInfo.InvariantCulture)}",
                $"SuccessRate={task.SuccessRate.ToString("F3", CultureInfo.InvariantCulture)}",
                $"SuccessCount={task.SuccessCount.ToString(CultureInfo.InvariantCulture)}",
                $"DisagreementBlockCount={task.DisagreementBlockCount.ToString(CultureInfo.InvariantCulture)}",
                $"ValidatorFailCount={task.ValidatorFailCount.ToString(CultureInfo.InvariantCulture)}",
                $"HumanOverrideCount={task.HumanOverrideCount.ToString(CultureInfo.InvariantCulture)}",
                task.MostCommonPatternId is null ? "MostCommonPatternId=(none)" : $"MostCommonPatternId={task.MostCommonPatternId}",
            ],
            rollbackPath: rollbackPath,
            confidence: "high",
            humanReviewRequired: true,
            capturedAtUtc: capturedAt);

        return new PromotionSuggestion(
            TaskClass: task.TaskClass,
            PatternId: task.MostCommonPatternId ?? "(unspecified)",
            TargetFile: targetFile,
            SuggestedChange: suggestedChange,
            EvidenceSummary: evidenceSummary,
            RollbackPath: rollbackPath,
            Provenance: packet);
    }

    private static string BuildEvidenceSummary(PromotionTaskSummary task)
    {
        string rate = (task.SuccessRate * 100.0).ToString("F1", CultureInfo.InvariantCulture);
        string parts = string.Format(
            CultureInfo.InvariantCulture,
            "{0} observations over task '{1}'; success {2}/{3} ({4}% rate)",
            task.TotalObservations,
            task.TaskClass,
            task.SuccessCount,
            task.TotalObservations,
            rate);

        if (task.MostCommonPatternId is not null)
        {
            parts += $"; most-common pattern '{task.MostCommonPatternId}'";
        }
        return parts + ".";
    }

    private static (string TargetFile, string SuggestedChange, string RollbackPath) ResolveTarget(PromotionTaskSummary task)
    {
        string normalized = (task.TaskClass ?? string.Empty).Trim().ToLowerInvariant();
        string pattern = task.MostCommonPatternId ?? "(unspecified)";

        return normalized switch
        {
            "fallback-director" => (
                "src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs",
                $"Promote fallback strategy '{pattern}' from discretionary routing to a high-priority deterministic branch: bump its priority floor, add a dedicated unit test covering the context that triggers it, and document the trigger in FALLBACK_AI_RESEARCH.md so future Claude sessions don't accidentally demote it.",
                "Revert the priority change, remove the dedicated test, and delete the research-doc entry. The ledger will resume observing the strategy's natural priority."),

            "duo-branch-tournament" or "duo-scout-judge" or "duo-architect-implementer-auditor" or "duo-fan-out-synthesis" or "duo-parallel-disagreement" => (
                "src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs",
                $"Promote the '{normalized}' cooperation pattern from planner recommendation to a pinned default for its task kind. Update SelectPattern() to bias toward this pattern for the relevant DuoTaskKind, add a regression test that asserts the pin, and record the task-kind in docs/MODEL_COLLABORATION.md.",
                "Remove the pin in SelectPattern() and the accompanying test. The planner will resume selecting the pattern probabilistically based on task + risk + hardware."),

            "live-inference" => (
                "src/PalLLM.Domain/Inference/InferenceClient.cs",
                $"The '{pattern}' pattern succeeded at promotion rate. Consider tightening the circuit breaker's failure-rate window so the breaker trips faster on regressions (current regressions would blur the ledger's signal).",
                "Restore the previous circuit-breaker tuning parameters; the breaker's cool-down logic is unaffected."),

            "duo-disagreement-detector" => (
                "src/PalLLM.Domain/Runtime/DisagreementDetector.cs",
                $"The pattern '{pattern}' has run to stable agreement rates. Consider tightening the detector's 'agree' threshold (currently 0.85) to 0.87 so near-miss paraphrases no longer auto-promote without a review gate.",
                "Restore the 0.85 threshold. No data loss — the detector is stateless."),

            _ => (
                $"(operator-assigned target for task class '{task.TaskClass}')",
                $"Promote the '{pattern}' pattern into a deterministic rule for the '{task.TaskClass}' task class. Typical implementation: author a named policy method that reproduces the observed behaviour, add a regression test that pins the new behaviour, and remove the AI-assisted routing that used to handle this class.",
                $"Delete the named policy method, restore the AI-assisted routing entry, and re-run the ledger observation window. Candidates must stay green for {PromotionLedger.MinObservationsForPromotion} observations again before re-promotion."),
        };
    }
}

public sealed record PromotionSuggestionSet(
    DateTimeOffset CapturedAtUtc,
    int CandidateCount,
    IReadOnlyList<PromotionSuggestion> Suggestions);

public sealed record PromotionSuggestion(
    string TaskClass,
    string PatternId,
    string TargetFile,
    string SuggestedChange,
    string EvidenceSummary,
    string RollbackPath,
    ProofPacket Provenance);
