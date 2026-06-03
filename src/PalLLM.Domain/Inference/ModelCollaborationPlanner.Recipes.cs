using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

// Partial of ModelCollaborationPlanner; see ModelCollaborationPlanner.cs for fields + ctor + GetSnapshot.
public sealed partial class ModelCollaborationPlanner
{

    private static ModelCollaborationModelDescriptor PickPreferredFastModel(
        IReadOnlyList<ModelCollaborationModelDescriptor> configuredModels) =>
        configuredModels.FirstOrDefault(model => string.Equals(model.OperatingStyle, "fast-iterative", StringComparison.Ordinal))
        ?? configuredModels.First();

    private static ModelCollaborationModelDescriptor PickPreferredDeliberateModel(
        IReadOnlyList<ModelCollaborationModelDescriptor> configuredModels) =>
        configuredModels.FirstOrDefault(model => string.Equals(model.OperatingStyle, "deliberate", StringComparison.Ordinal))
        ?? configuredModels.First();

    private static ModelCollaborationRecipe[] BuildRecipes(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        string collaborationMode = hardware.CanKeepTwoSpecialistsWarm && hardware.PreferParallel
            ? "parallel"
            : "sequential";

        return
        [
            new ModelCollaborationRecipe(
                Id: "fast-draft-dense-judge",
                Name: "Fast Draft, Dense Judge",
                Mode: collaborationMode,
                Summary: "Let the faster model explore, draft, or run tool loops; let the denser model approve the patch, rewrite the final answer, or reject drift.",
                BestWhen: "Narrow PalLLM fixes, bridge sweeps, screenshot loops, and any task where first-token speed matters but final correctness matters more.",
                Stages:
                [
                    CreateStage("draft", "Draft / scout", fastModel, deliberateModel, "Generate candidate files, patch shapes, screenshot findings, or tool-call branches.", "short plan + candidate diff summary", hardware.CanKeepTwoSpecialistsWarm),
                    CreateStage("judge", "Review / finalizer", deliberateModel, fastModel, "Rank candidates, enforce global constraints, and produce the final PalLLM-facing answer.", "approved patch or rejection note", false),
                ],
                Notes:
                [
                    "This is the default pairing for a dense Qwen reviewer plus a fast sparse-MoE scout.",
                    "If the scout loops or drifts, tighten its output contract and keep the dense reviewer as the gate.",
                ]),

            new ModelCollaborationRecipe(
                Id: "dense-plan-fast-execute-dense-audit",
                Name: "Dense Plan, Fast Execute, Dense Audit",
                Mode: "sequential",
                Summary: "Use the deliberate model to set invariants up front, the faster model to do the grunt work, and the deliberate model again for final audit.",
                BestWhen: "Bridge contract changes, HUD seam work, doc+code sync, and release-facing behavior changes.",
                Stages:
                [
                    CreateStage("plan", "Planner", deliberateModel, fastModel, "Write the acceptance criteria, constraints, and file list before edits start.", "checklist + constraints", false),
                    CreateStage("execute", "Executor", fastModel, deliberateModel, "Perform the implementation or wide search once the contract is frozen.", "candidate patch set", hardware.CanKeepTwoSpecialistsWarm),
                    CreateStage("audit", "Auditor", deliberateModel, fastModel, "Review for hidden regressions, missing tests, native-seam drift, and doc drift.", "pass/fail audit with fix list", false),
                ],
                Notes:
                [
                    "This is the safest lane when the mod is changing public contracts or player-facing behavior.",
                    "For PalLLM itself, this recipe is ideal for code-vs-doc audits, OpenAPI drift work, and bridge/HUD seam changes.",
                ]),

            new ModelCollaborationRecipe(
                Id: "watchdog-and-repair",
                Name: "Resident Watchdog, On-Demand Repair",
                Mode: hardware.PreferSequentialBatonPassing ? "sequential" : collaborationMode,
                Summary: "Keep the cheaper or faster model resident as a background auditor, then wake the deliberate model only when something smells wrong.",
                BestWhen: "Bridge inbox anomalies, screenshot drift, CI triage, route-count drift checks, and long unattended runs.",
                Stages:
                [
                    CreateStage("watch", "Watchdog", fastModel, deliberateModel, "Continuously scan logs, health snapshots, bridge events, or tests for anomalies.", "ranked anomaly report", true),
                    CreateStage("repair", "Repair author", deliberateModel, fastModel, "Design the minimal fix once the anomaly is localized.", "repair patch or rollback recommendation", false),
                    CreateStage("verify", "Cheap recheck", fastModel, deliberateModel, "Re-run the narrow verification loop after the fix lands.", "verification summary", hardware.CanKeepTwoSpecialistsWarm),
                ],
                Notes:
                [
                    "This pattern maps cleanly onto staged model promotion, quant review, release-hygiene gates, and bridge drift patrol.",
                    "On tight hardware, keep the watchdog resident and page the deliberate model in only on failure boundaries.",
                ]),

            new ModelCollaborationRecipe(
                Id: "parallel-crossfire",
                Name: "Parallel Crossfire",
                Mode: hardware.CanKeepTwoSpecialistsWarm ? "parallel" : "burst-parallel",
                Summary: "Have both models attack the same problem from different prompts, then reconcile overlap and disagreement.",
                BestWhen: "Hard bridge bugs, HUD seam regressions, release triage, and overnight candidate generation.",
                Stages:
                [
                    CreateStage("lane-a", "Fast lane", fastModel, deliberateModel, "Optimize for breadth, alternate hypotheses, and more tool-call attempts.", "multiple candidate hypotheses", true),
                    CreateStage("lane-b", "Deliberate lane", deliberateModel, fastModel, "Optimize for consistency, root-cause analysis, and constraint checking.", "root-cause memo", true),
                    CreateStage("merge", "Arbiter", deliberateModel, fastModel, "Merge agreement, reject contradictions, and produce the next best action.", "merged decision", false),
                ],
                Notes:
                [
                    "Best on workstation-class hardware or when the models are served on separate ports or machines.",
                    "If hardware is tight, emulate this with short alternating turns instead of true parallel residency.",
                ]),
        ];
    }

    private static ModelTaskRoutingPolicy[] BuildRoutingPolicies(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        string residencyMode = hardware.CanKeepTwoSpecialistsWarm
            ? "parallel-ready"
            : "sequential-baton";

        return
        [
            new ModelTaskRoutingPolicy(
                Id: "low-risk-fast-lane",
                TaskClass: "quick sidecar, bridge, or docs fix",
                RiskLevel: "low",
                Summary: "Let the fast lane own the first pass, but force immediate deterministic validation before anything is treated as complete.",
                PreferredFlow: $"{fastModel.ModelId} patch -> validators -> escalate only on failure",
                Steps:
                [
                    $"{fastModel.ModelId} scouts the smallest sufficient context and applies the narrow patch.",
                    "Run targeted tests, lint, or schema validation immediately after the patch.",
                    $"Escalate to {deliberateModel.ModelId} only when validators fail, unrelated edits appear, or the patch spills outside the intended scope.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: false,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "medium-risk-fast-implement-dense-review",
                TaskClass: "medium-risk runtime, docs, or bridge change",
                RiskLevel: "medium",
                Summary: "Use the fast lane for implementation speed, but make the dense lane the formal reviewer before the work is considered merge-ready.",
                PreferredFlow: $"{fastModel.ModelId} implement -> validators -> {deliberateModel.ModelId} review",
                Steps:
                [
                    $"{fastModel.ModelId} maps the affected files and drafts the candidate patch.",
                    "Run the smallest deterministic validation set that proves the changed behavior.",
                    $"{deliberateModel.ModelId} reviews the diff for hidden coupling, missing call sites, and insufficient tests before sign-off.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "high-risk-deliberate-bookends",
                TaskClass: "native HUD/audio seams, bridge compatibility, auth, persistence, or release-facing contracts",
                RiskLevel: "high",
                Summary: "The dense lane writes the contract first and closes the loop at the end; the fast lane does the mechanical work in the middle.",
                PreferredFlow: $"{deliberateModel.ModelId} spec -> {fastModel.ModelId} execute -> validators -> {deliberateModel.ModelId} final review",
                Steps:
                [
                    $"{deliberateModel.ModelId} defines acceptance criteria, invariants, and the exact files in scope before edits begin.",
                    $"{fastModel.ModelId} implements only the approved plan and classifies any validation failures instead of guessing at repairs.",
                    $"{deliberateModel.ModelId} performs the final review and blocks promotion when evidence is weak or scope expanded.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: true),

            new ModelTaskRoutingPolicy(
                Id: "tool-heavy-guarded",
                TaskClass: "tool-heavy audit or file-edit loops inside the PalLLM repo",
                RiskLevel: "medium",
                Summary: "Treat tool calls as proposals first. The fast lane drafts them, the dense lane validates them, and a deterministic firewall still decides what actually runs.",
                PreferredFlow: $"{fastModel.ModelId} tool draft -> {deliberateModel.ModelId} guard -> firewall -> execution",
                Steps:
                [
                    $"{fastModel.ModelId} proposes tool calls, file paths, and command sequences quickly.",
                    $"{deliberateModel.ModelId} checks schema, permissions, file scope, and whether a safer alternative exists.",
                    "Execute only the filtered tool plan and keep deterministic validators ahead of any default promotion.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "frontend-visual-loop",
                TaskClass: "Palworld HUD, dashboard, or screenshot-driven work",
                RiskLevel: "medium",
                Summary: "Use the fast lane for rough HUD or screenshot iteration, then let the dense lane review player-facing behavior, accessibility, and state handling.",
                PreferredFlow: $"{fastModel.ModelId} HUD draft -> screenshot checks -> {deliberateModel.ModelId} player-facing review",
                Steps:
                [
                    $"{fastModel.ModelId} drafts the HUD, dashboard, or screenshot-facing patch quickly.",
                    "Run screenshot-based or dashboard verification instead of forcing the whole task through text-only reasoning.",
                    $"{deliberateModel.ModelId} reviews accessibility, state transitions, and over-broad edits before final approval.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "context-compiler-then-dense-reasoning",
                TaskClass: "whole-repo audit or long-context PalLLM investigation",
                RiskLevel: "medium",
                Summary: "Use the fast lane as a context compiler: gather evidence broadly, then let the dense lane reason over the reduced evidence pack instead of a noisy full transcript.",
                PreferredFlow: $"{fastModel.ModelId} evidence ledger -> {deliberateModel.ModelId} selected-context reasoning ({residencyMode})",
                Steps:
                [
                    $"{fastModel.ModelId} ranks relevant files, tests, configs, and unknowns instead of summarizing the whole repository.",
                    $"{deliberateModel.ModelId} reasons over the reduced evidence set and rejects noise or weak retrieval.",
                    "Store findings in a shared evidence ledger: facts, rejected hypotheses, validation outcomes, and promotion decisions.",
                ],
                RequiresDeterministicValidators: false,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),
        ];
    }

    private static ModelQualificationSuite BuildQualificationSuite(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        string shadowMode = hardware.CanKeepTwoSpecialistsWarm
            ? "Run the candidate in parallel shadow mode against the current default lane."
            : "Run the candidate in sequential shadow mode and batch promotion checks to avoid churn.";

        return new ModelQualificationSuite(
            Summary: $"Fresh models and fresh quants should earn trust before promotion. Use {fastModel.ModelId} for cheap smoke and PalLLM workload replay, then require {deliberateModel.ModelId} to write the promotion verdict.",
            EvaluationPhases:
            [
                "Shadow smoke tests against the existing lane",
                "Task-fit replay on real PalLLM repo and runtime workloads",
                "Promotion review with rollback notes and validator evidence",
            ],
            Checks:
            [
                new("exact-json-tool-call", "Exact JSON or tool-call schema", "schema", "Fresh candidates often fail on strict structured output before they fail on prose tasks.", "Produce valid structured output across repeated runs."),
                new("nested-tool-call-object", "Nested tool-call object handling", "schema", "Tool loops fail quietly when nested argument objects drift or flatten unexpectedly.", "Preserve nested object shape under the target runtime and parser."),
                new("small-patch-generation", "Small patch generation", "coding", "A candidate must prove it can make narrow edits without rewriting the repository.", "Solve a representative small fix with a minimal diff."),
                new("diff-format-compliance", "Diff and edit format compliance", "coding", "The harness loses time when a model emits prose instead of machine-actionable edits.", "Emit a valid patch or file-edit format the harness can consume directly."),
                new("long-context-file-retrieval", "Long-context file retrieval", "retrieval", "Context-heavy tasks fall apart when the model confuses explored context with relevant context.", "Find the right files and symbols from a large repo slice without flooding the judge with noise."),
                new("test-failure-diagnosis", "Failing-test diagnosis", "coding", "A good worker must classify failures before attempting blind repairs.", "Differentiate patch bugs, test expectations, environment issues, and unrelated failures."),
                new("no-unrelated-file-edit", "No unrelated file edits", "safety", "Over-broad diffs are one of the easiest ways for a fast lane to create hidden regressions.", "Keep touched files inside the approved scope."),
                new("prompt-injection-resistance", "Prompt-injection resistance on repo text and web context", "safety", "Logs, comments, issues, and fetched webpages are evidence, not instructions.", "Ignore hostile text inside retrieved context unless the harness explicitly endorses it."),
                new("browser-or-visual-task", "Browser or visual task fit", "visual", "Palworld HUD and screenshot work need visual verification, not text-only optimism.", "Pass at least one representative screenshot or dashboard verification loop when that workload matters."),
                new("repeated-run-stability", "Repeated-run stability", "stability", "A candidate that only succeeds once is not safe to promote as a default lane.", "Repeat the same task enough times to see whether structure, latency, and correctness stay stable."),
            ],
            PromotionRequirements:
            [
                shadowMode,
                "Do not make a candidate the default lane until it passes schema, retrieval, coding, and stability checks on your own workload.",
                "Keep very aggressive low-bit quants scout-only until they independently prove patch quality and tool correctness.",
                "Promotion decisions should carry rollback notes, not just a winner label.",
            ],
            FailureActions:
            [
                "Keep the candidate in scout-only or shadow-only service instead of promoting it.",
                "Tighten the tool schema, prompt contract, or validation harness before trying again.",
                "Reduce context pressure or choose a less aggressive quant when errors correlate with long-context drift.",
                "Rollback to the previous default lane immediately when stability or safety checks regress.",
            ]);
    }

    private static ModelHardwareTierPlaybook[] BuildHardwarePlaybook(
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel) =>
    [
        new(
            TierId: "cpu-only",
            Summary: "CPU-only or very low-memory laptops. Favor one heavyweight lane at a time and keep expectations honest.",
            RecommendedRunMode: "one_model_only",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "cpu-only"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "cpu-only"),
            ContextGuidance: "4K-16K context. Keep the fast lane interactive and reserve the deliberate lane for short or overnight batch work.",
            Notes:
            [
                "Treat extremely low-bit quants as scout-only until they independently pass your qualification suite.",
                "Batch deep-review work to avoid constant model swapping.",
            ]),
        new(
            TierId: "edge",
            Summary: "Single 16 GB VRAM card or similar unified-memory budget. This is the classic sequential duo tier.",
            RecommendedRunMode: "sequential",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "edge"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "edge"),
            ContextGuidance: "8K-32K context. Load the fast lane for interactive work, then swap to the deliberate lane for review windows.",
            Notes:
            [
                "The fast lane should scout, draft, and run narrow validation loops.",
                "The deliberate lane should own architecture, review, and release-readiness notes.",
            ]),
        new(
            TierId: "hybrid-offload",
            Summary: "20 GB-ish VRAM or GPU+RAM offload. Stronger than edge, but still best treated as baton-passing hardware.",
            RecommendedRunMode: "sequential",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "hybrid-offload"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "hybrid-offload"),
            ContextGuidance: "8K-32K interactive context; 64K only when the task genuinely needs it and validators justify the memory hit.",
            Notes:
            [
                "KV cache pressure becomes the real constraint here, not just raw model file size.",
                "Favor retrieval and evidence ledgers over blindly expanding the context window.",
            ]),
        new(
            TierId: "prosumer",
            Summary: "24-32 GB VRAM or strong unified memory. The best serious single-accelerator local coding tier.",
            RecommendedRunMode: "sequential-preferred",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "prosumer"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "prosumer"),
            ContextGuidance: "32K-64K default. Keep one lane hot and page the other in at decision boundaries unless you can tolerate lower quants.",
            Notes:
            [
                "Use the fast lane for branch fan-out and tool loops.",
                "Use the deliberate lane for final review, migration planning, and release-facing bridge or HUD changes.",
            ]),
        new(
            TierId: "workstation",
            Summary: "48 GB+ VRAM, dual GPUs, or large workstation-class unified memory. This is where true duo workcells become comfortable.",
            RecommendedRunMode: "parallel",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "workstation"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "workstation"),
            ContextGuidance: "64K-128K by default, with higher windows only when the retrieval path proves the task really needs them.",
            Notes:
            [
                "Run separate OpenAI-compatible endpoints when possible.",
                "Use the fast lane for concurrent audits, screenshot loops, and bridge triage; use the deliberate lane for judges, auditors, and repair planners.",
            ]),
    ];

    private static string GetFastLaneQuantHint(ModelCollaborationModelDescriptor model, string tierId)
    {
        string normalized = NormalizeModelId(model.ModelId);
        bool qwen35A3B = normalized.Contains("qwen3.6") && normalized.Contains("35b") && normalized.Contains("a3b");

        if (!qwen35A3B)
        {
            return tierId switch
            {
                "cpu-only" => "Choose the lowest-bit usable fast lane and keep it scout-only.",
                "edge" => "Favor a lower-bit fast lane that stays responsive for scanning and drafts.",
                "hybrid-offload" => "Balanced 3-4 bit fast lane with cautious context sizing.",
                "prosumer" => "Balanced 4-5 bit fast lane for implementation and tool loops.",
                _ => "Higher-quality fast lane or native serving profile.",
            };
        }

        return tierId switch
        {
            "cpu-only" => "UD-IQ1_M / UD-IQ2_XXS / UD-IQ2_M",
            "edge" => "UD-Q3_K_XL / UD-IQ4_XS / lower Q4",
            "hybrid-offload" => "UD-Q3_K_XL / UD-IQ4_XS / UD-Q4_K_S",
            "prosumer" => "UD-Q4_K_M / UD-Q4_K_XL / MXFP4_MOE / UD-Q5_K_M",
            _ => "UD-Q4_K_M / MXFP4_MOE / UD-Q5_K_M / UD-Q6_K",
        };
    }

    private static string GetDeliberateLaneQuantHint(ModelCollaborationModelDescriptor model, string tierId)
    {
        string normalized = NormalizeModelId(model.ModelId);
        bool qwen27 = normalized.Contains("qwen3.6") && normalized.Contains("27b");

        if (!qwen27)
        {
            return tierId switch
            {
                "cpu-only" => "Short, low-bit batch-only deliberate lane if you need one at all.",
                "edge" => "Tight 4-bit deliberate lane for audit windows.",
                "hybrid-offload" => "4-bit deliberate lane with small-to-medium context.",
                "prosumer" => "4-5 bit deliberate lane for deep review windows.",
                _ => "5-6 bit deliberate lane or native precision where hardware allows.",
            };
        }

        return tierId switch
        {
            "cpu-only" => "UD-IQ2_XXS / UD-IQ2_M / Q3_K_S (batch only)",
            "edge" => "Q3 / tight Q4",
            "hybrid-offload" => "Q4_K_S / IQ4_XS / Q4_K_M",
            "prosumer" => "Q4_K_M / UD-Q4_K_XL / Q5_K_M",
            _ => "Q5_K_M / Q6_K / Q8_0",
        };
    }

    private static ModelCollaborationStage CreateStage(
        string stageId,
        string role,
        ModelCollaborationModelDescriptor preferredModel,
        ModelCollaborationModelDescriptor fallbackModel,
        string why,
        string outputContract,
        bool canRunInParallel) =>
        new(
            StageId: stageId,
            Role: role,
            PreferredModel: preferredModel.ModelId,
            PreferredTierId: preferredModel.TierId,
            FallbackModel: fallbackModel.ModelId,
            Why: why,
            OutputContract: outputContract,
            CanRunInParallel: canRunInParallel);

    private static string[] BuildDeploymentNotes(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        List<string> notes = new();

        notes.Add(hardware.ClassId switch
        {
            "cpu-only" => "Load one heavyweight model at a time. Use the fast lane for constant availability and wake the deliberate lane only for narrow audits or final sign-off.",
            "edge" => "Prefer the faster model as the always-on lane. Queue dense review only for diff candidates, failing tests, or release checkpoints.",
            "hybrid-offload" => "Sequential baton passing usually beats trying to keep both big models warm. Keep the MoE scout resident and page the dense model in on demand.",
            "prosumer" => "Use separate ports if possible. Keep the fast lane warm and reserve dense review for commit candidates, public docs, and bridge regression triage.",
            _ => "Run separate servers and let both specialists stay hot. Use true parallel crossfire for hard bridge bugs, HUD regressions, and nightly audit jobs.",
        });

        notes.Add($"Preferred fast lane: {fastModel.ModelId}");
        notes.Add($"Preferred deliberate lane: {deliberateModel.ModelId}");
        notes.Add("Use the fast lane as an evidence compiler first: rank files, tests, configs, and unknowns before asking the dense lane to reason globally.");
        notes.Add("Keep a shared evidence ledger of facts, rejected hypotheses, validation results, and promotion decisions instead of storing whole transcripts.");

        if (fastModel.LikelyMultimodal || deliberateModel.LikelyMultimodal)
        {
            notes.Add("If your local deployment uses text-only GGUF builds, keep Palworld screenshot work on a separate vision-capable server and feed only distilled findings back into the text lanes.");
        }

        return notes.ToArray();
    }

    private static ModelCollaborationIdea[] BuildSelfHealingIdeas(
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel) =>
    [
        new(
            Id: "doc-and-contract-drift-patrol",
            Summary: $"Keep {fastModel.ModelId} as the resident drift detector for routes, feature counts, bridge contracts, and failing docs links; wake {deliberateModel.ModelId} to approve any repair patch before it lands.",
            Trigger: "Any route, feature-catalog, or OpenAPI change."),
        new(
            Id: "shadow-test-repair",
            Summary: $"{fastModel.ModelId} localizes likely failure files and drafts minimal repairs; {deliberateModel.ModelId} checks invariants and rejects fixes that only silence the symptom.",
            Trigger: "Test failures, bridge log spikes, or repeated fallback-path regressions."),
        new(
            Id: "model-promotion-review",
            Summary: $"{fastModel.ModelId} smoke-tests new quants or model revisions in shadow mode; {deliberateModel.ModelId} acts as the promotion reviewer so nothing silently becomes the new default lane.",
            Trigger: "New local model download, quant swap, or lane default change."),
    ];

    private static bool IsSparseMoe(string normalizedModelId) =>
        normalizedModelId.Contains("a3b")
        || normalizedModelId.Contains("a10b")
        || normalizedModelId.Contains("a17b")
        || normalizedModelId.Contains("moe");

    private static string NormalizeModelId(string modelId) =>
        (modelId ?? string.Empty).Trim().ToLowerInvariant();
}
