using System.Globalization;
using System.Text;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Takes a Pass-12 <see cref="PromotionSuggestion"/> and produces a
/// <see cref="PromotionApplyPreview"/> — the final step in the 2035
/// hard-code promotion loop. The builder turns the suggestion's
/// one-sentence recipe into a concrete, editor-ready change template
/// the operator can paste directly into the target file.
///
/// <para>Deterministic, no file reads, no inference call. The builder
/// does NOT produce a real unified diff against the live repo — that
/// would require reading every potential target file and risk drifting
/// with real code. Instead it generates a descriptive before-context
/// + after-code template so the operator sees exactly what the change
/// looks like, then hand-applies it with full awareness of the
/// surrounding code.</para>
///
/// <para>The preview always includes:</para>
/// <list type="bullet">
///   <item><c>DiffPreview</c> — multi-line string with file path,
///         before-context anchor, and after-code fenced in Markdown so
///         every UI (dashboard, terminal, MCP client) renders it the
///         same way.</item>
///   <item><c>SafetyWarnings</c> — up to three short sentences listing
///         things the operator must check before committing (e.g. "this
///         change assumes hook signature X").</item>
///   <item><c>RollbackCommand</c> — single-line shell command the
///         operator can run to back out the change.</item>
///   <item><c>Provenance</c> — a <see cref="ProofPacket"/> tagged
///         <c>subsystem=promotion-apply-preview</c> so the preview
///         itself has audit evidence.</item>
/// </list>
///
/// <para>For unknown task classes the builder still returns a usable
/// generic template rather than failing — consistent with every other
/// deterministic surface in PalLLM.</para>
/// </summary>
public static class PromotionApplyPreviewBuilder
{
    public static PromotionApplyPreview Build(PromotionSuggestion suggestion, DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        DateTimeOffset captured = capturedAtUtc ?? DateTimeOffset.UtcNow;

        string normalized = (suggestion.TaskClass ?? string.Empty).Trim().ToLowerInvariant();
        (string diffPreview, string[] safetyWarnings, string rollbackCommand) = normalized switch
        {
            "fallback-director" => BuildFallbackDirectorPreview(suggestion),
            "duo-branch-tournament" or "duo-scout-judge" or "duo-architect-implementer-auditor" or "duo-fan-out-synthesis" or "duo-parallel-disagreement" =>
                BuildDuoPlannerPreview(suggestion, normalized),
            "live-inference" => BuildLiveInferencePreview(suggestion),
            "duo-disagreement-detector" => BuildDisagreementDetectorPreview(suggestion),
            "rate-limiter" => BuildRateLimiterPreview(suggestion),
            "model-tier-transition" => BuildTierTransitionPreview(suggestion),
            _ => BuildGenericPreview(suggestion),
        };

        ProofPacket provenance = ProofPacketBuilder.Build(
            subsystem: "promotion-apply-preview",
            decision: $"apply-preview={suggestion.PatternId}",
            primaryReason: suggestion.SuggestedChange,
            evidenceLines:
            [
                $"TaskClass={suggestion.TaskClass}",
                $"PatternId={suggestion.PatternId}",
                $"TargetFile={suggestion.TargetFile}",
                $"OriginalSuggestionId={suggestion.Provenance.Id}",
            ],
            rollbackPath: rollbackCommand,
            confidence: "high",
            humanReviewRequired: true,
            capturedAtUtc: captured);

        return new PromotionApplyPreview(
            TaskClass: suggestion.TaskClass ?? string.Empty,
            PatternId: suggestion.PatternId,
            TargetFile: suggestion.TargetFile,
            DiffPreview: diffPreview,
            SafetyWarnings: safetyWarnings,
            RollbackCommand: rollbackCommand,
            Provenance: provenance);
    }

    // -- Per-task-class templates -------------------------------------

    private static (string Diff, string[] Warnings, string Rollback) BuildFallbackDirectorPreview(PromotionSuggestion s)
    {
        string pattern = s.PatternId;
        StringBuilder sb = new();
        sb.AppendLine($"// File: {s.TargetFile}");
        sb.AppendLine();
        sb.AppendLine($"// 1. Bump priority floor for strategy '{pattern}'");
        sb.AppendLine($"// Find the Try{ToPascalCase(pattern)}() method (or its priority-setting line)");
        sb.AppendLine("// and raise the Priority constant so the strategy is considered before");
        sb.AppendLine("// lower-priority siblings in the director's selection loop.");
        sb.AppendLine();
        sb.AppendLine("// BEFORE:");
        sb.AppendLine("//     Priority = FallbackPriority.Normal,");
        sb.AppendLine("// AFTER:");
        sb.AppendLine("//     Priority = FallbackPriority.StablePromoted,   // Pass-14 promotion");
        sb.AppendLine();
        sb.AppendLine($"// 2. Add a regression test in tests/PalLLM.Tests/FallbackBehaviorEngineTests.cs:");
        sb.AppendLine("[Test]");
        sb.AppendLine($"public void {ToPascalCase(pattern)}_FiresWhenPromotedContextMatches()");
        sb.AppendLine("{");
        sb.AppendLine($"    // Observed {s.EvidenceSummary}");
        sb.AppendLine($"    // Pin the trigger context that caused '{pattern}' to stabilise.");
        sb.AppendLine("    var context = new FallbackBehaviorContext { /* ... */ };");
        sb.AppendLine("    var engine = new FallbackBehaviorEngine();");
        sb.AppendLine("    var decision = engine.Generate(context);");
        sb.AppendLine($"    Assert.That(decision.StrategyId, Is.EqualTo(\"{pattern}\"));");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// 3. Document the promotion in docs/FALLBACK_AI_RESEARCH.md under 'Promoted strategies'.");

        string[] warnings =
        [
            $"Confirm the exact Try{ToPascalCase(pattern)}() method exists — the strategy id may differ from the method name.",
            "If the director reads priority from a strategy-attribute instead of a constant, update the attribute instead.",
            "Add a CHANGELOG entry noting the promotion so future sessions don't accidentally demote the strategy.",
        ];

        string rollback = $"git checkout HEAD -- src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs tests/PalLLM.Tests/FallbackBehaviorEngineTests.cs docs/FALLBACK_AI_RESEARCH.md";
        return (sb.ToString().TrimEnd(), warnings, rollback);
    }

    private static (string Diff, string[] Warnings, string Rollback) BuildDuoPlannerPreview(PromotionSuggestion s, string pattern)
    {
        StringBuilder sb = new();
        sb.AppendLine($"// File: {s.TargetFile}");
        sb.AppendLine();
        sb.AppendLine($"// Pin the '{pattern}' cooperation pattern as the default for its task kind.");
        sb.AppendLine("// Find the DuoOrchestratorPlanner.SelectPattern() switch and pin the return:");
        sb.AppendLine();
        sb.AppendLine("// BEFORE:");
        sb.AppendLine("//     DuoTaskKind.XXX => DefaultPattern,");
        sb.AppendLine("// AFTER:");
        sb.AppendLine($"//     DuoTaskKind.XXX => DuoCooperationPattern.{ToPascalCase(pattern.Replace("duo-", ""))},   // Pass-14 promotion");
        sb.AppendLine();
        sb.AppendLine("// Add a regression test:");
        sb.AppendLine("[Test]");
        sb.AppendLine("public void SelectPattern_PinsPromotedPatternForTaskKind()");
        sb.AppendLine("{");
        sb.AppendLine($"    // Observed {s.EvidenceSummary}");
        sb.AppendLine("    var planner = new DuoOrchestratorPlanner(/* ... */);");
        sb.AppendLine("    var plan = planner.Plan(new DuoPlanRequest { Kind = DuoTaskKind.XXX });");
        sb.AppendLine($"    Assert.That(plan.Pattern, Is.EqualTo(DuoCooperationPattern.{ToPascalCase(pattern.Replace("duo-", ""))}));");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// Document the pin in docs/MODEL_COLLABORATION.md under 'Promoted patterns'.");

        string[] warnings =
        [
            "DuoTaskKind.XXX must be replaced with the specific task-kind that triggered the promotion — the planner currently picks by kind, not by pattern name.",
            "If risk = High the planner forces ParallelDisagreement — a pin at lower risk won't override that.",
            "Ensure DuoOrchestratorPlannerTests.cs still passes; a few tests assume the default pattern selection.",
        ];

        string rollback = "git checkout HEAD -- src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs tests/PalLLM.Tests/DuoOrchestratorPlannerTests.cs docs/MODEL_COLLABORATION.md";
        return (sb.ToString().TrimEnd(), warnings, rollback);
    }

    private static (string Diff, string[] Warnings, string Rollback) BuildLiveInferencePreview(PromotionSuggestion s)
    {
        StringBuilder sb = new();
        sb.AppendLine($"// File: {s.TargetFile}");
        sb.AppendLine();
        sb.AppendLine($"// Model '{s.PatternId}' has been stable across {s.EvidenceSummary}.");
        sb.AppendLine("// Tighten the inference circuit-breaker window so regressions surface faster:");
        sb.AppendLine();
        sb.AppendLine("// BEFORE:");
        sb.AppendLine("//     public int CircuitBreakerFailureThreshold { get; set; } = 5;");
        sb.AppendLine("//     public int CircuitBreakerCooldownSeconds { get; set; } = 30;");
        sb.AppendLine("// AFTER:");
        sb.AppendLine("//     public int CircuitBreakerFailureThreshold { get; set; } = 3;   // Pass-14 promotion");
        sb.AppendLine("//     public int CircuitBreakerCooldownSeconds { get; set; } = 45;   // Pass-14 promotion");
        sb.AppendLine();
        sb.AppendLine("// Note: these defaults live on InferenceOptions — override in appsettings.json");
        sb.AppendLine("// if your deployment wants a different window per endpoint.");

        string[] warnings =
        [
            "A tighter breaker means a noisier endpoint will trip faster — verify downstream fallback still covers the gap.",
            "Applies to every configured inference endpoint, not just the one that triggered the promotion.",
            "Record current behaviour in a CHANGELOG entry before shipping; the breaker's behaviour changes for every operator on upgrade.",
        ];

        string rollback = "git checkout HEAD -- src/PalLLM.Domain/Configuration/PalLlmOptions.cs";
        return (sb.ToString().TrimEnd(), warnings, rollback);
    }

    private static (string Diff, string[] Warnings, string Rollback) BuildDisagreementDetectorPreview(PromotionSuggestion s)
    {
        StringBuilder sb = new();
        sb.AppendLine($"// File: {s.TargetFile}");
        sb.AppendLine();
        sb.AppendLine($"// '{s.PatternId}' has run stably. Tighten the detector's 'agree' threshold so near-miss paraphrases route to review.");
        sb.AppendLine();
        sb.AppendLine("// BEFORE:");
        sb.AppendLine("//     >= 0.85 => (\"agree\", \"proceed\", ...)");
        sb.AppendLine("// AFTER:");
        sb.AppendLine("//     >= 0.87 => (\"agree\", \"proceed\", ...)   // Pass-14 promotion");
        sb.AppendLine();
        sb.AppendLine("// No data loss — the detector is stateless.");

        string[] warnings =
        [
            "Tightening the threshold will emit more 'review' verdicts; confirm downstream callers handle the signal.",
            "The existing DisagreementDetectorTests expect >=0.85 behaviour — one test will need its pinned threshold updated.",
            "Dashboard promotion candidates may re-enter the 'collecting' state until enough observations accumulate at the new threshold.",
        ];

        string rollback = "git checkout HEAD -- src/PalLLM.Domain/Runtime/DisagreementDetector.cs tests/PalLLM.Tests/DisagreementDetectorTests.cs";
        return (sb.ToString().TrimEnd(), warnings, rollback);
    }

    private static (string Diff, string[] Warnings, string Rollback) BuildRateLimiterPreview(PromotionSuggestion s)
    {
        StringBuilder sb = new();
        sb.AppendLine($"// File: src/PalLLM.Domain/Configuration/PalLlmOptions.cs");
        sb.AppendLine();
        sb.AppendLine($"// Rate-limiter engagement ({s.EvidenceSummary}) indicates a healthy rate of runaway calls.");
        sb.AppendLine("// Consider lowering the per-character cap so the limiter activates earlier:");
        sb.AppendLine();
        sb.AppendLine("// BEFORE:");
        sb.AppendLine("//     public int MaxCharacterRequestsPerMinute { get; set; } = 0; // off");
        sb.AppendLine("// AFTER:");
        sb.AppendLine("//     public int MaxCharacterRequestsPerMinute { get; set; } = 6; // Pass-14 promotion");
        sb.AppendLine();
        sb.AppendLine("// Note: 0 disables the limiter. Pick a cap that matches your chat cadence + latency budget.");

        string[] warnings =
        [
            "Lowering the cap will route more turns to the fallback director — ensure the director's phrasing is acceptable for your deployment.",
            "The change ships as a default; every operator upgrading picks it up unless they override in appsettings.json.",
            "Monitor palllm_rate_limited_total after rollout to make sure the new cap isn't too aggressive.",
        ];

        string rollback = "git checkout HEAD -- src/PalLLM.Domain/Configuration/PalLlmOptions.cs";
        return (sb.ToString().TrimEnd(), warnings, rollback);
    }

    private static (string Diff, string[] Warnings, string Rollback) BuildTierTransitionPreview(PromotionSuggestion s)
    {
        StringBuilder sb = new();
        sb.AppendLine("// File: src/PalLLM.Domain/Inference/ModelTierOrchestrator.cs");
        sb.AppendLine();
        sb.AppendLine($"// Transition '{s.PatternId}' has been stable. Consider making it the default");
        sb.AppendLine("// graduation target so new deployments benefit without explicit tier config:");
        sb.AppendLine();
        sb.AppendLine("// BEFORE:");
        sb.AppendLine("//     public List<ModelTierOptions> ModelTiers { get; set; } = new();");
        sb.AppendLine("// AFTER:");
        sb.AppendLine("//     public List<ModelTierOptions> ModelTiers { get; set; } = new()");
        sb.AppendLine("//     {");
        sb.AppendLine("//         new() { Id = \"small\", Model = \"...\", Priority = 1 },   // Pass-14 promotion");
        sb.AppendLine("//         new() { Id = \"large\", Model = \"...\", Priority = 10 },  // Pass-14 promotion");
        sb.AppendLine("//     };");

        string[] warnings =
        [
            "Default tiers ship as config — every operator upgrading sees them unless they override in appsettings.json.",
            "Shipping a default model-tag embeds an opinion about a specific model; verify IP / licensing implications before shipping.",
            "Test against the model-tier orchestrator's unit tests; a few pin the default empty-tier behaviour.",
        ];

        string rollback = "git checkout HEAD -- src/PalLLM.Domain/Configuration/PalLlmOptions.cs";
        return (sb.ToString().TrimEnd(), warnings, rollback);
    }

    private static (string Diff, string[] Warnings, string Rollback) BuildGenericPreview(PromotionSuggestion s)
    {
        StringBuilder sb = new();
        sb.AppendLine($"// File: {s.TargetFile}");
        sb.AppendLine();
        sb.AppendLine($"// Generic promotion template for task class '{s.TaskClass}'.");
        sb.AppendLine($"// Pattern: {s.PatternId}");
        sb.AppendLine($"// Evidence: {s.EvidenceSummary}");
        sb.AppendLine();
        sb.AppendLine("// Recommended: author a named policy method capturing the observed behaviour,");
        sb.AppendLine("// add a regression test, and remove the AI-assisted routing that used to");
        sb.AppendLine("// handle this task class.");

        string[] warnings =
        [
            "Target file path is operator-assigned — no template is available for an unknown task class.",
            "The builder cannot generate a rollback command without knowing the target file.",
            "Manually record the promotion in CHANGELOG.md once the change lands.",
        ];

        string rollback = "git status   # review changes, then 'git checkout HEAD -- <your-target-file>'";
        return (sb.ToString().TrimEnd(), warnings, rollback);
    }

    private static string ToPascalCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) { return string.Empty; }
        string[] parts = input.Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        StringBuilder sb = new(input.Length);
        foreach (string part in parts)
        {
            if (part.Length == 0) { continue; }
            sb.Append(char.ToUpper(part[0], CultureInfo.InvariantCulture));
            if (part.Length > 1) { sb.Append(part.AsSpan(1)); }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Wire-level request shape for <c>POST /api/promotion/apply/preview</c>.
/// Callers name the task class + pattern id they want a preview for;
/// the server looks up the matching candidate in the live ledger and
/// builds the preview from the derived <see cref="PromotionSuggestion"/>.
/// </summary>
public sealed class PromotionApplyPreviewRequest
{
    public string? TaskClass { get; init; }
    public string? PatternId { get; init; }
}

public sealed record PromotionApplyPreview(
    string TaskClass,
    string PatternId,
    string TargetFile,
    string DiffPreview,
    IReadOnlyList<string> SafetyWarnings,
    string RollbackCommand,
    ProofPacket Provenance);
