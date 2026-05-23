using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

/// <summary>
/// The ten cooperation patterns for the Qwen Duo Mesh (fast MoE worker +
/// dense judge). Each pattern captures how a Worker-class model and a
/// Judge-class model should combine for a particular task shape. The
/// planner returns one of these given a request + the operator's live
/// role bindings + hardware posture.
///
/// <para>Vocabulary mirrors the 2035 Qwen Duo architecture brief so the
/// patterns are comparable across games, apps, coding tools, and media
/// pipelines:</para>
/// </summary>
public enum DuoCooperationPattern
{
    /// <summary>Worker scouts / fans out ideas; Judge picks the
    /// winner and writes the contract.</summary>
    ScoutThenJudge = 0,

    /// <summary>Judge writes architecture + acceptance tests; Worker
    /// implements; Judge audits the final diff.</summary>
    ArchitectThenImplementerThenAuditor = 1,

    /// <summary>Worker generates many candidates in parallel; Judge
    /// synthesises one coherent answer.</summary>
    FanOutThenSynthesis = 2,

    /// <summary>Both answer independently; disagreement becomes a
    /// safety signal that blocks auto-promotion.</summary>
    ParallelDisagreement = 3,

    /// <summary>Worker implements 3 branches; Judge runs a tournament
    /// with validators to pick the winner.</summary>
    BranchTournament = 4,

    /// <summary>Low-VRAM tier: load Worker, work, unload, load Judge,
    /// review. Sequential swap so only one model is resident.</summary>
    SequentialSwap = 5,

    /// <summary>Worker handles nearline / live UX; Judge runs
    /// long-running background jobs (architecture, self-healing,
    /// proof packets).</summary>
    WorkerLiveJudgeBackground = 6,

    /// <summary>Worker drafts many prompt variants; Judge enforces
    /// style genome, continuity, and safety constraints.</summary>
    DraftThenFinalize = 7,

    /// <summary>Worker does the work; Judge samples the trace every
    /// N steps and flags drift / hallucination / unsafe action.</summary>
    DuoWatchdog = 8,

    /// <summary>Worker handles routine decisions; Judge is the appeal
    /// court for low-confidence / high-risk / validator-failing
    /// cases.</summary>
    DenseAppealCourt = 9,

    /// <summary>Only one of the two roles is bound; planner reports
    /// which tier that falls back to and what the caller should
    /// expect.</summary>
    SingleRoleFallback = 10,

    /// <summary>Neither Worker nor Judge is bound; caller should
    /// expect the deterministic fallback director plus a nudge to
    /// declare roles.</summary>
    DeterministicOnly = 11,
}

/// <summary>
/// The task shape the caller wants to accomplish. Used by the planner
/// to pick the right cooperation pattern. Derived from the 2035
/// architecture brief's "best division of labor" table so every common
/// game / app / coding / media task has a named shape.
/// </summary>
public enum DuoTaskKind
{
    /// <summary>Short intent routing, command classification. Wants
    /// low latency + strict JSON; usually Worker-only.</summary>
    CommandRouting = 0,

    /// <summary>Draft implementation, small patch, bounded tool
    /// call. Worker-first with optional Judge audit.</summary>
    ImplementDraft = 1,

    /// <summary>Write a spec, acceptance tests, intent contract.
    /// Judge-first.</summary>
    ArchitecturePlan = 2,

    /// <summary>Final audit of a diff, spec, or decision. Judge.</summary>
    Audit = 3,

    /// <summary>Generate many variants in parallel (prompts, branches,
    /// patch candidates). Worker fan-out.</summary>
    ParallelCandidates = 4,

    /// <summary>Synthesise many options into one coherent final
    /// output. Judge synthesis.</summary>
    FinalSynthesis = 5,

    /// <summary>Long-context recall / summarisation across a repo,
    /// doc corpus, or session history.</summary>
    LongContextSynthesis = 6,

    /// <summary>Tool execution under a gateway — risk-sensitive, may
    /// need the Judge's review of the plan.</summary>
    ToolExecution = 7,

    /// <summary>High-risk decision (security, payments, release
    /// gates, IP/provenance). Always takes the appeal court.</summary>
    HighRisk = 8,

    /// <summary>Asset / prompt / media draft → refinement loop. Worker
    /// drafts, Judge finalises.</summary>
    MediaPrompting = 9,
}

/// <summary>
/// Deterministic planner that turns a <see cref="DuoPlanRequest"/> into
/// a <see cref="DuoPlan"/> by mapping the task kind + risk + hardware
/// tier + active role bindings to one of the ten
/// <see cref="DuoCooperationPattern"/> choices.
///
/// <para>Pure C#, no inference call, no external I/O. Can be consulted
/// from the deterministic always-available layer and always produces a
/// usable plan — if no Worker or Judge is bound, returns the
/// <see cref="DuoCooperationPattern.SingleRoleFallback"/> or
/// <see cref="DuoCooperationPattern.DeterministicOnly"/> pattern with
/// clear operator guidance.</para>
/// </summary>
public sealed class DuoOrchestratorPlanner
{
    private readonly ModelRoleRegistry _registry;

    public DuoOrchestratorPlanner(ModelRoleRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public DuoPlan Plan(DuoPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ModelRoleCoverage coverage = _registry.GetCoverage();
        ModelRoleSlot workerSlot = coverage.Slots.First(s => s.Role == nameof(ModelRole.Worker));
        ModelRoleSlot judgeSlot = coverage.Slots.First(s => s.Role == nameof(ModelRole.Judge));
        bool workerActive = workerSlot.IsActive;
        bool judgeActive = judgeSlot.IsActive;

        // Neither role bound — deterministic-only fallback plan.
        if (!workerActive && !judgeActive)
        {
            return new DuoPlan(
                Pattern: DuoCooperationPattern.DeterministicOnly,
                Why: "No Worker or Judge role bound. Deterministic fallback director still answers every chat, but duo cooperation patterns require at least one role.",
                Steps: [new DuoPlanStep("deterministic-fallback", "Either", "PalLLM's deterministic director replies without model inference.")],
                Escalation: "Declare a Worker binding under PalLLM:ModelRoles[] (Qwen3.6-35B-A3B class) to unlock Worker-only patterns. Add a Judge binding (Qwen3.6-27B class) to unlock full-duo patterns.",
                ThinkingMode: new DuoThinkingMode(Worker: null, Judge: null),
                ContextBudget: new DuoContextBudget(Worker: null, Judge: null),
                RiskLevel: request.Risk.ToString());
        }

        // Only one role bound — pick the appropriate single-role fallback.
        if (workerActive && !judgeActive)
        {
            return new DuoPlan(
                Pattern: DuoCooperationPattern.SingleRoleFallback,
                Why: $"Only Worker is bound ('{ActiveBindingId(workerSlot)}'). Running in Worker-only mode; Judge-dependent patterns (audit, branch tournament, final synthesis) will degrade to validators-only review.",
                Steps:
                [
                    new DuoPlanStep("worker-execute", "Worker", "Worker handles the task end-to-end."),
                    new DuoPlanStep("validators", "Either", "Deterministic validators (schema, tests, policy) substitute for dense audit."),
                ],
                Escalation: "Add a Judge binding (Qwen3.6-27B class) to unlock audit / branch-tournament / final-synthesis patterns.",
                ThinkingMode: new DuoThinkingMode(Worker: WorkerThinkingFor(request.Kind), Judge: null),
                ContextBudget: new DuoContextBudget(Worker: WorkerContextFor(request.Kind, request.Hardware), Judge: null),
                RiskLevel: request.Risk.ToString());
        }
        if (!workerActive && judgeActive)
        {
            return new DuoPlan(
                Pattern: DuoCooperationPattern.SingleRoleFallback,
                Why: $"Only Judge is bound ('{ActiveBindingId(judgeSlot)}'). Running in Judge-only mode; Worker-dependent patterns (fan-out, parallel candidates) will be serialised.",
                Steps:
                [
                    new DuoPlanStep("judge-execute", "Judge", "Judge handles the task end-to-end — slower but coherent."),
                ],
                Escalation: "Add a Worker binding (Qwen3.6-35B-A3B class) to unlock fan-out / scout / draft patterns.",
                ThinkingMode: new DuoThinkingMode(Worker: null, Judge: JudgeThinkingFor(request.Kind)),
                ContextBudget: new DuoContextBudget(Worker: null, Judge: JudgeContextFor(request.Kind, request.Hardware)),
                RiskLevel: request.Risk.ToString());
        }

        // Both roles bound — pick the pattern by task kind, risk, and hardware.
        DuoCooperationPattern pattern = SelectPattern(request, coverage);
        (DuoPlanStep[] steps, string why) = BuildStepsAndWhy(pattern, request, workerSlot, judgeSlot);

        return new DuoPlan(
            Pattern: pattern,
            Why: why,
            Steps: steps,
            Escalation: request.Risk == DuoRiskLevel.High
                ? "High-risk task: require a human review gate before auto-promotion, regardless of pattern outcome."
                : "Validators substitute for human review on low/medium risk; escalate to human on validator disagreement.",
            ThinkingMode: new DuoThinkingMode(
                Worker: WorkerThinkingFor(request.Kind),
                Judge: JudgeThinkingFor(request.Kind)),
            ContextBudget: new DuoContextBudget(
                Worker: WorkerContextFor(request.Kind, request.Hardware),
                Judge: JudgeContextFor(request.Kind, request.Hardware)),
            RiskLevel: request.Risk.ToString());
    }

    // ---- Pattern selection rules ------------------------------------

    private static DuoCooperationPattern SelectPattern(DuoPlanRequest request, ModelRoleCoverage coverage)
    {
        // High-risk tasks always take the appeal court so disagreement
        // becomes a safety signal.
        if (request.Risk == DuoRiskLevel.High)
        {
            return DuoCooperationPattern.ParallelDisagreement;
        }

        // Constrained hardware: force sequential swap so both models
        // don't need to co-reside.
        if (request.Hardware == DuoHardwareTier.Constrained)
        {
            return DuoCooperationPattern.SequentialSwap;
        }

        // Task-kind-driven matrix. Same task shape → same pattern so
        // dashboards + tests don't flap.
        return request.Kind switch
        {
            DuoTaskKind.CommandRouting => DuoCooperationPattern.DenseAppealCourt,
            DuoTaskKind.ImplementDraft => DuoCooperationPattern.ArchitectThenImplementerThenAuditor,
            DuoTaskKind.ArchitecturePlan => DuoCooperationPattern.ArchitectThenImplementerThenAuditor,
            DuoTaskKind.Audit => DuoCooperationPattern.DenseAppealCourt,
            DuoTaskKind.ParallelCandidates => DuoCooperationPattern.BranchTournament,
            DuoTaskKind.FinalSynthesis => DuoCooperationPattern.FanOutThenSynthesis,
            DuoTaskKind.LongContextSynthesis => DuoCooperationPattern.WorkerLiveJudgeBackground,
            DuoTaskKind.ToolExecution => DuoCooperationPattern.DuoWatchdog,
            DuoTaskKind.HighRisk => DuoCooperationPattern.ParallelDisagreement,
            DuoTaskKind.MediaPrompting => DuoCooperationPattern.DraftThenFinalize,
            _ => DuoCooperationPattern.ScoutThenJudge,
        };
    }

    private static (DuoPlanStep[] Steps, string Why) BuildStepsAndWhy(
        DuoCooperationPattern pattern,
        DuoPlanRequest request,
        ModelRoleSlot worker,
        ModelRoleSlot judge)
    {
        string w = $"Worker ({ActiveBindingId(worker)})";
        string j = $"Judge ({ActiveBindingId(judge)})";

        return pattern switch
        {
            DuoCooperationPattern.ScoutThenJudge => (
                [
                    new DuoPlanStep("scout", "Worker", $"{w} fans out fast hypotheses and identifies relevant files/tools."),
                    new DuoPlanStep("judge", "Judge", $"{j} picks the winning hypothesis and writes the intent contract."),
                ],
                "Task kind needs breadth then selection — Worker scouts cheaply, Judge picks coherently."),

            DuoCooperationPattern.ArchitectThenImplementerThenAuditor => (
                [
                    new DuoPlanStep("architect", "Judge", $"{j} writes intent contract + acceptance tests."),
                    new DuoPlanStep("implement", "Worker", $"{w} implements the smallest safe strategy."),
                    new DuoPlanStep("audit", "Judge", $"{j} audits the final diff and writes a proof packet."),
                ],
                "Implementation task: dense planning up front, fast implementation in the middle, dense audit at the end."),

            DuoCooperationPattern.FanOutThenSynthesis => (
                [
                    new DuoPlanStep("fan-out", "Worker", $"{w} generates many candidate variants in parallel."),
                    new DuoPlanStep("synthesize", "Judge", $"{j} synthesises the best variants into one coherent final output."),
                ],
                "Synthesis task: Worker fans out cheaply, Judge merges coherently."),

            DuoCooperationPattern.ParallelDisagreement => (
                [
                    new DuoPlanStep("worker-answer", "Worker", $"{w} answers independently."),
                    new DuoPlanStep("judge-answer", "Judge", $"{j} answers independently."),
                    new DuoPlanStep("compare", "Either", "Compare outputs; disagreement blocks auto-promotion and escalates to validators + human review."),
                ],
                "High-risk task: disagreement between Worker and Judge is a first-class safety signal."),

            DuoCooperationPattern.BranchTournament => (
                [
                    new DuoPlanStep("intent", "Judge", $"{j} writes intent contract + acceptance tests."),
                    new DuoPlanStep("branches", "Worker", $"{w} implements 3 independent branches."),
                    new DuoPlanStep("validators", "Either", "Validators (tests/lint/typecheck/security) score each branch."),
                    new DuoPlanStep("pick-winner", "Judge", $"{j} picks the winning branch and writes the merge plan."),
                ],
                "Parallel-candidates task: branch tournament with validator-backed dense selection."),

            DuoCooperationPattern.SequentialSwap => (
                [
                    new DuoPlanStep("load-worker", "Worker", $"Load {w}; handle fast interactive work."),
                    new DuoPlanStep("unload-load-judge", "Judge", $"Unload Worker; load {j} for deep review."),
                    new DuoPlanStep("judge-review", "Judge", $"{j} runs architecture/audit/repair/proof-packet passes."),
                ],
                "Constrained-hardware task: only one model resident at a time; batch Judge reviews so swaps are rare."),

            DuoCooperationPattern.WorkerLiveJudgeBackground => (
                [
                    new DuoPlanStep("worker-live", "Worker", $"{w} handles nearline / live user interaction."),
                    new DuoPlanStep("judge-background", "Judge", $"{j} runs long-context synthesis as a background job."),
                ],
                "Long-context task: Worker keeps the UX responsive, Judge chews the long document in the background."),

            DuoCooperationPattern.DraftThenFinalize => (
                [
                    new DuoPlanStep("draft", "Worker", $"{w} drafts many prompt / style / shot candidates."),
                    new DuoPlanStep("finalize", "Judge", $"{j} enforces style genome, continuity, and safety constraints."),
                ],
                "Media-prompting task: fast drafts then dense continuity pass."),

            DuoCooperationPattern.DuoWatchdog => (
                [
                    new DuoPlanStep("worker-execute", "Worker", $"{w} executes tool-call workflow under a gateway."),
                    new DuoPlanStep("judge-sample", "Judge", $"{j} samples the trace every N steps and flags drift / bad tool call / unsafe action."),
                    new DuoPlanStep("repair", "Judge", $"{j} repairs on detection; resumes Worker or escalates."),
                ],
                "Tool-execution task: Worker drives, Judge supervises."),

            DuoCooperationPattern.DenseAppealCourt => (
                [
                    new DuoPlanStep("worker-default", "Worker", $"{w} handles the default case."),
                    new DuoPlanStep("escalate", "Judge", $"Route to {j} on low confidence, validator failure, or risky category."),
                ],
                "Routine task with escalation hook: Worker is fast default, Judge is the appeal court."),

            _ => (
                [new DuoPlanStep("unspecified", "Either", "Planner did not find a specific pattern; see fallback guidance.")],
                "Unmatched task shape — using ScoutThenJudge as a safe default."),
        };
    }

    // ---- Thinking + context heuristics ------------------------------

    private static bool WorkerThinkingFor(DuoTaskKind kind) => kind switch
    {
        DuoTaskKind.CommandRouting => false,
        DuoTaskKind.ImplementDraft => false,
        DuoTaskKind.ParallelCandidates => true,
        DuoTaskKind.ToolExecution => true,
        _ => false,
    };

    private static bool JudgeThinkingFor(DuoTaskKind kind) => kind switch
    {
        DuoTaskKind.CommandRouting => false,
        _ => true,
    };

    private static string WorkerContextFor(DuoTaskKind kind, DuoHardwareTier hw) => (kind, hw) switch
    {
        (DuoTaskKind.LongContextSynthesis, _) => "8K–64K (retrieval before long context)",
        (_, DuoHardwareTier.Constrained) => "4K–16K",
        (_, DuoHardwareTier.Standard) => "8K–32K",
        _ => "16K–64K",
    };

    private static string JudgeContextFor(DuoTaskKind kind, DuoHardwareTier hw) => (kind, hw) switch
    {
        (DuoTaskKind.LongContextSynthesis, _) => "64K–262K",
        (DuoTaskKind.Audit, _) => "32K–128K",
        (_, DuoHardwareTier.Constrained) => "8K–16K",
        (_, DuoHardwareTier.Standard) => "16K–64K",
        _ => "32K–128K",
    };

    private static string ActiveBindingId(ModelRoleSlot slot)
    {
        ModelRoleBinding? active = slot.Bindings.FirstOrDefault(b => b.Enabled);
        return active is null || string.IsNullOrWhiteSpace(active.Id) ? "unbound" : active.Id;
    }
}

public sealed class DuoPlanRequest
{
    public DuoTaskKind Kind { get; init; } = DuoTaskKind.ImplementDraft;
    public DuoRiskLevel Risk { get; init; } = DuoRiskLevel.Low;
    public DuoHardwareTier Hardware { get; init; } = DuoHardwareTier.Standard;
    public string? Note { get; init; }
}

public enum DuoRiskLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}

public enum DuoHardwareTier
{
    /// <summary>8–20GB VRAM / CPU-only laptops / single-model-resident.</summary>
    Constrained = 0,

    /// <summary>24–32GB VRAM / serious single-GPU local studio.</summary>
    Standard = 1,

    /// <summary>48GB+ VRAM / multi-GPU / both models simultaneously.</summary>
    Generous = 2,
}

public sealed record DuoPlan(
    DuoCooperationPattern Pattern,
    string Why,
    IReadOnlyList<DuoPlanStep> Steps,
    string Escalation,
    DuoThinkingMode ThinkingMode,
    DuoContextBudget ContextBudget,
    string RiskLevel);

public sealed record DuoPlanStep(string Name, string Actor, string Detail);

public sealed record DuoThinkingMode(bool? Worker, bool? Judge);

public sealed record DuoContextBudget(string? Worker, string? Judge);
