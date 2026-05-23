namespace PalLLM.Domain.Inference;

public sealed class ModelCollaborationDecisionPlanner
{
    private readonly ModelCollaborationPlanner _planner;

    public ModelCollaborationDecisionPlanner(ModelCollaborationPlanner planner)
    {
        _planner = planner;
    }

    public ModelCollaborationDecision Plan(ModelCollaborationDecisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ModelHardwareHints hints = new(
            VramGb: request.VramGb,
            RamGb: request.RamGb,
            UnifiedMemoryGb: request.UnifiedMemoryGb,
            CpuOnly: request.CpuOnly,
            PreferParallel: request.PreferParallel);
        ModelCollaborationSnapshot snapshot = _planner.GetSnapshot(hints);

        ModelCollaborationModelDescriptor fastLane = snapshot.ConfiguredModels
            .FirstOrDefault(model => string.Equals(model.OperatingStyle, "fast-iterative", StringComparison.Ordinal))
            ?? snapshot.ConfiguredModels.First();
        ModelCollaborationModelDescriptor deliberateLane = snapshot.ConfiguredModels
            .FirstOrDefault(model => string.Equals(model.OperatingStyle, "deliberate", StringComparison.Ordinal))
            ?? snapshot.ConfiguredModels.First();

        string normalizedRisk = NormalizeRiskLevel(request.RiskLevel, request);
        string policyId = SelectPolicyId(request, normalizedRisk);
        ModelTaskRoutingPolicy policy = snapshot.RoutingPolicies
            .FirstOrDefault(candidate => string.Equals(candidate.Id, policyId, StringComparison.Ordinal))
            ?? snapshot.RoutingPolicies.First();

        string recipeId = SelectRecipeId(request, normalizedRisk, snapshot.Hardware);
        ModelCollaborationRecipe recipe = snapshot.Recipes
            .FirstOrDefault(candidate => string.Equals(candidate.Id, recipeId, StringComparison.Ordinal))
            ?? snapshot.Recipes.First();

        ModelHardwareTierPlaybook playbook = snapshot.HardwarePlaybook
            .FirstOrDefault(candidate => string.Equals(candidate.TierId, snapshot.Hardware.ClassId, StringComparison.Ordinal))
            ?? snapshot.HardwarePlaybook.First();

        bool lowRisk = string.Equals(normalizedRisk, "low", StringComparison.Ordinal);
        bool highRisk = string.Equals(normalizedRisk, "high", StringComparison.Ordinal);
        bool visualWork = request.FrontendOrVisual || request.NeedsVision || request.AssetOrMedia;
        bool humanReviewRequired = request.ReleaseGate
            || request.HeroAsset
            || policy.RequiresHumanReview
            || (request.AssetOrMedia && request.HeroAsset);

        string runMode = DetermineRunMode(request, snapshot.Hardware, recipe);
        ModelLaneBooleanHints thinkingMode = BuildThinkingMode(request, lowRisk, highRisk);
        ModelLaneBooleanHints preserveThinking = BuildPreserveThinkingMode(request, lowRisk, highRisk);
        ModelLaneTextHints contextBudget = BuildContextBudget(request, snapshot.Hardware, visualWork);
        ModelLaneTextHints quantRecommendation = new(
            FastLane: playbook.FastLaneQuantHint,
            DeliberateLane: playbook.DeliberateLaneQuantHint);
        string fastLaneRole = BuildFastLaneRole(request, lowRisk, highRisk, visualWork);
        string deliberateLaneRole = BuildDeliberateLaneRole(request, lowRisk, highRisk, visualWork);
        string[] validators = BuildValidators(request, lowRisk, highRisk, visualWork);
        string[] promotionCriteria = BuildPromotionCriteria(snapshot.QualificationSuite, request, lowRisk, highRisk, visualWork);
        string fallback = BuildFallback(snapshot.Hardware, fastLane, deliberateLane);
        string why = BuildWhy(request, snapshot.Hardware, policy, recipe, lowRisk, highRisk, visualWork);

        return new ModelCollaborationDecision(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            HardwareClassId: snapshot.Hardware.ClassId,
            SelectedPolicyId: policy.Id,
            SelectedRecipeId: recipe.Id,
            Strategy: policy.PreferredFlow,
            Why: why,
            FastLaneModel: fastLane.ModelId,
            FastLaneRole: fastLaneRole,
            DeliberateLaneModel: deliberateLane.ModelId,
            DeliberateLaneRole: deliberateLaneRole,
            RunMode: runMode,
            ThinkingMode: thinkingMode,
            PreserveThinking: preserveThinking,
            ContextBudget: contextBudget,
            QuantRecommendation: quantRecommendation,
            Validators: validators,
            HumanReviewRequired: humanReviewRequired,
            PromotionCriteria: promotionCriteria,
            Fallback: fallback,
            Steps: policy.Steps);
    }

    private static string SelectPolicyId(ModelCollaborationDecisionRequest request, string normalizedRisk)
    {
        if (string.Equals(normalizedRisk, "high", StringComparison.Ordinal))
        {
            return "high-risk-deliberate-bookends";
        }

        if (request.ToolHeavy)
        {
            return "tool-heavy-guarded";
        }

        if (request.FrontendOrVisual || request.NeedsVision || request.AssetOrMedia)
        {
            return "frontend-visual-loop";
        }

        if (request.LargeContext || LooksLargeContext(request))
        {
            return "context-compiler-then-dense-reasoning";
        }

        return string.Equals(normalizedRisk, "low", StringComparison.Ordinal)
            ? "low-risk-fast-lane"
            : "medium-risk-fast-implement-dense-review";
    }

    private static string SelectRecipeId(
        ModelCollaborationDecisionRequest request,
        string normalizedRisk,
        ModelHardwareProfile hardware)
    {
        if (string.Equals(normalizedRisk, "high", StringComparison.Ordinal))
        {
            return "dense-plan-fast-execute-dense-audit";
        }

        if (LooksLikeWatchdogWork(request))
        {
            return "watchdog-and-repair";
        }

        if (hardware.CanKeepTwoSpecialistsWarm && LooksLikeCrossfireWork(request))
        {
            return "parallel-crossfire";
        }

        return "fast-draft-dense-judge";
    }

    private static string DetermineRunMode(
        ModelCollaborationDecisionRequest request,
        ModelHardwareProfile hardware,
        ModelCollaborationRecipe recipe)
    {
        if (hardware.CpuOnly)
        {
            return string.Equals(recipe.Id, "watchdog-and-repair", StringComparison.Ordinal)
                ? "sequential"
                : "one_model_only";
        }

        if (string.Equals(recipe.Mode, "parallel", StringComparison.Ordinal) && hardware.CanKeepTwoSpecialistsWarm)
        {
            return "parallel";
        }

        if (request.ToolHeavy && hardware.CanKeepTwoSpecialistsWarm && hardware.PreferParallel)
        {
            return "parallel";
        }

        return hardware.PreferSequentialBatonPassing
            ? "sequential"
            : recipe.Mode;
    }

    private static ModelLaneBooleanHints BuildThinkingMode(
        ModelCollaborationDecisionRequest request,
        bool lowRisk,
        bool highRisk)
    {
        bool fastThinking = request.ToolHeavy
            || request.LargeContext
            || request.FrontendOrVisual
            || request.AssetOrMedia
            || request.NeedsVision
            || !lowRisk;
        bool deliberateThinking = highRisk
            || !lowRisk
            || request.LargeContext
            || request.AssetOrMedia
            || request.ReleaseGate;

        return new(FastLane: fastThinking, DeliberateLane: deliberateThinking);
    }

    private static ModelLaneBooleanHints BuildPreserveThinkingMode(
        ModelCollaborationDecisionRequest request,
        bool lowRisk,
        bool highRisk)
    {
        bool deliberatePreserve = highRisk
            || request.LargeContext
            || request.ToolHeavy
            || request.AssetOrMedia
            || request.ReleaseGate
            || !lowRisk;

        return new(FastLane: false, DeliberateLane: deliberatePreserve);
    }

    private static ModelLaneTextHints BuildContextBudget(
        ModelCollaborationDecisionRequest request,
        ModelHardwareProfile hardware,
        bool visualWork)
    {
        return hardware.ClassId switch
        {
            "cpu-only" => new(
                FastLane: "4K-16K interactive context",
                DeliberateLane: "4K-16K batch-only context"),
            "edge" => new(
                FastLane: visualWork ? "16K-32K with screenshot or browser evidence" : "8K-16K default, 32K when justified",
                DeliberateLane: request.LargeContext ? "32K selected-context review windows" : "16K-32K review windows"),
            "hybrid-offload" => new(
                FastLane: visualWork ? "16K-32K with distilled visual evidence" : "8K-32K operational context",
                DeliberateLane: request.LargeContext ? "32K-64K retrieval-first" : "16K-32K, 64K only when proven necessary"),
            "prosumer" => new(
                FastLane: request.LargeContext ? "32K-64K evidence compiler lane" : "16K-32K fast lane",
                DeliberateLane: request.LargeContext ? "64K-128K selected-context reasoning" : "32K-64K final review lane"),
            _ => new(
                FastLane: request.LargeContext ? "32K-64K per worker with retrieval first" : "16K-32K per worker",
                DeliberateLane: request.LargeContext ? "64K-262K retrieval-first dense reasoning" : "64K-128K dense review"),
        };
    }

    private static string BuildFastLaneRole(
        ModelCollaborationDecisionRequest request,
        bool lowRisk,
        bool highRisk,
        bool visualWork)
    {
        if (request.ToolHeavy)
        {
            return "Fast tool draft lane and low-risk execution worker";
        }

        if (visualWork)
        {
            return request.AssetOrMedia
                ? "Player-facing presentation draft and screenshot-preview lane"
                : "HUD draft and screenshot-loop worker";
        }

        if (request.LargeContext || LooksLargeContext(request))
        {
            return "Context compiler, scout, and evidence-pack builder";
        }

        if (highRisk)
        {
            return "Approved-plan implementer and narrow repair worker";
        }

        return lowRisk
            ? "Fast patch scout and narrow implementation lane"
            : "Candidate generator and implementation worker";
    }

    private static string BuildDeliberateLaneRole(
        ModelCollaborationDecisionRequest request,
        bool lowRisk,
        bool highRisk,
        bool visualWork)
    {
        if (highRisk)
        {
            return "Contract author, final reviewer, and release gate";
        }

        if (request.AssetOrMedia)
        {
            return "Player-facing presentation judge and release gate";
        }

        if (request.LargeContext || LooksLargeContext(request))
        {
            return "Selected-context reasoner, synthesis judge, and final decision lane";
        }

        if (visualWork)
        {
            return "HUD reviewer, accessibility judge, and final synthesizer";
        }

        return lowRisk
            ? "Escalation reviewer only when validators fail"
            : "Architect, reviewer, and final synthesizer";
    }

    private static string[] BuildValidators(
        ModelCollaborationDecisionRequest request,
        bool lowRisk,
        bool highRisk,
        bool visualWork)
    {
        List<string> validators =
        [
            "Diff-scope check against the approved file set",
        ];

        validators.Add(lowRisk
            ? "Targeted tests for the changed behavior"
            : "Targeted tests plus lint and typecheck");

        if (request.ToolHeavy)
        {
            validators.Add("Tool schema validation and path allowlist firewall");
        }

        if (highRisk)
        {
            validators.Add("Security and contract validation for the touched surface");
        }

        if (visualWork)
        {
            validators.Add(request.AssetOrMedia
                ? "Player-facing screenshot review with deterministic file checks"
                : "HUD or screenshot verification with accessibility review");
        }

        if (request.LargeContext || LooksLargeContext(request))
        {
            validators.Add("Evidence-ledger review before dense sign-off");
        }

        if (request.ReleaseGate || request.HeroAsset)
        {
            validators.Add("Manual release or promotion checklist");
        }

        return validators.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] BuildPromotionCriteria(
        ModelQualificationSuite suite,
        ModelCollaborationDecisionRequest request,
        bool lowRisk,
        bool highRisk,
        bool visualWork)
    {
        List<string> ids =
        [
            "repeated-run-stability",
        ];

        if (request.ToolHeavy)
        {
            ids.Add("exact-json-tool-call");
            ids.Add("nested-tool-call-object");
        }

        if (!lowRisk)
        {
            ids.Add("small-patch-generation");
            ids.Add("diff-format-compliance");
            ids.Add("no-unrelated-file-edit");
        }

        if (request.LargeContext || LooksLargeContext(request))
        {
            ids.Add("long-context-file-retrieval");
            ids.Add("prompt-injection-resistance");
        }

        if (visualWork)
        {
            ids.Add("browser-or-visual-task");
        }

        if (highRisk)
        {
            ids.Add("test-failure-diagnosis");
        }

        return ids
            .Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                ModelQualificationCheck? check = suite.Checks.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal));
                return check is null
                    ? id
                    : $"{check.Id}: {check.MinimumEvidence}";
            })
            .ToArray();
    }

    private static string BuildFallback(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastLane,
        ModelCollaborationModelDescriptor deliberateLane)
    {
        string batonNote = hardware.PreferSequentialBatonPassing
            ? "Batch dense review windows to avoid constant model swapping."
            : "Keep both lanes hot when the machine can support it.";

        return $"If {deliberateLane.ModelId} is unavailable, keep {fastLane.ModelId} scout-only behind deterministic validators and human review. " +
               $"If {fastLane.ModelId} is unavailable, let {deliberateLane.ModelId} do a smaller single-branch pass and skip fan-out. {batonNote}";
    }

    private static string BuildWhy(
        ModelCollaborationDecisionRequest request,
        ModelHardwareProfile hardware,
        ModelTaskRoutingPolicy policy,
        ModelCollaborationRecipe recipe,
        bool lowRisk,
        bool highRisk,
        bool visualWork)
    {
        string riskClause = highRisk
            ? "The task touches a high-risk boundary, so the dense lane needs to set and close the contract."
            : lowRisk
                ? "The task is narrow enough for a fast first pass as long as validators fire immediately."
                : "The task benefits from fast implementation speed but still needs dense review before trust.";

        string workloadClause = request.ToolHeavy
            ? "Tool execution is guarded, so tool calls stay proposals until the deliberate lane and firewall approve them."
            : request.LargeContext || LooksLargeContext(request)
                ? "The fast lane is acting as a context compiler so the dense lane reasons over evidence instead of transcript noise."
                : visualWork
                    ? "Palworld HUD or screenshot work needs visual verification instead of text-only optimism."
                    : $"This maps cleanly onto the `{policy.Id}` policy and `{recipe.Id}` recipe.";

        string hardwareClause = hardware.PreferSequentialBatonPassing
            ? "This machine still prefers baton passing over constant dual residency."
            : "This machine can support hotter dual-lane operation when the task earns it.";

        return $"{riskClause} {workloadClause} {hardwareClause}";
    }

    private static string NormalizeRiskLevel(string? riskLevel, ModelCollaborationDecisionRequest request)
    {
        string normalized = Normalize(riskLevel);
        if (normalized is "low" or "medium" or "high")
        {
            return normalized;
        }

        if (request.ReleaseGate || request.HeroAsset || LooksSensitive(request))
        {
            return "high";
        }

        if (request.ToolHeavy || request.FrontendOrVisual || request.LargeContext || request.AssetOrMedia || request.NeedsVision)
        {
            return "medium";
        }

        return "low";
    }

    private static bool LooksSensitive(ModelCollaborationDecisionRequest request)
    {
        string haystack = BuildTaskHaystack(request);
        return ContainsAny(
            haystack,
            "auth",
            "security",
            "secret",
            "credential",
            "migration",
            "schema",
            "database",
            "persistence",
            "delete",
            "publish",
            "release",
            "legal",
            "license",
            "privacy",
            "bridge",
            "hud",
            "widget",
            "waypoint",
            "subtitle",
            "audio");
    }

    private static bool LooksLargeContext(ModelCollaborationDecisionRequest request)
    {
        string haystack = BuildTaskHaystack(request);
        return ContainsAny(
            haystack,
            "whole repo",
            "whole codebase",
            "whole project",
            "repo audit",
            "codebase audit",
            "large repo",
            "long context",
            "docs sync",
            "trace triage",
            "bridge audit",
            "compat audit",
            "compare branches");
    }

    private static bool LooksLikeWatchdogWork(ModelCollaborationDecisionRequest request)
    {
        string haystack = BuildTaskHaystack(request);
        return ContainsAny(
            haystack,
            "watchdog",
            "monitor",
            "self-heal",
            "self heal",
            "repair loop",
            "shadow",
            "drift patrol",
            "nightly audit");
    }

    private static bool LooksLikeCrossfireWork(ModelCollaborationDecisionRequest request)
    {
        string haystack = BuildTaskHaystack(request);
        return ContainsAny(
            haystack,
            "compare",
            "tournament",
            "benchmark",
            "choose",
            "rank",
            "disagreement",
            "root cause",
            "root-cause",
            "audit");
    }

    private static string BuildTaskHaystack(ModelCollaborationDecisionRequest request) =>
        Normalize($"{request.Task} {request.TaskClass}");

    private static bool ContainsAny(string haystack, params string[] needles) =>
        needles.Any(needle => haystack.Contains(needle, StringComparison.Ordinal));

    private static string Normalize(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}

public sealed record ModelCollaborationDecisionRequest(
    string Task,
    string? TaskClass = null,
    string? RiskLevel = null,
    bool ToolHeavy = false,
    bool FrontendOrVisual = false,
    bool LargeContext = false,
    bool AssetOrMedia = false,
    bool NeedsVision = false,
    bool ReleaseGate = false,
    bool HeroAsset = false,
    double? VramGb = null,
    double? RamGb = null,
    double? UnifiedMemoryGb = null,
    bool CpuOnly = false,
    bool PreferParallel = true,
    string? AvailableQuants = null,
    string? ContextBudget = null);

public sealed record ModelCollaborationDecision(
    DateTimeOffset GeneratedAtUtc,
    string HardwareClassId,
    string SelectedPolicyId,
    string SelectedRecipeId,
    string Strategy,
    string Why,
    string FastLaneModel,
    string FastLaneRole,
    string DeliberateLaneModel,
    string DeliberateLaneRole,
    string RunMode,
    ModelLaneBooleanHints ThinkingMode,
    ModelLaneBooleanHints PreserveThinking,
    ModelLaneTextHints ContextBudget,
    ModelLaneTextHints QuantRecommendation,
    string[] Validators,
    bool HumanReviewRequired,
    string[] PromotionCriteria,
    string Fallback,
    string[] Steps);

public sealed record ModelLaneBooleanHints(bool FastLane, bool DeliberateLane);

public sealed record ModelLaneTextHints(string FastLane, string DeliberateLane);
