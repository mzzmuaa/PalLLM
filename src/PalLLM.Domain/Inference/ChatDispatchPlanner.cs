using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

/// <summary>
/// Deterministic planner that turns a
/// (<see cref="DuoCooperationPattern"/>, <see cref="ModelRoleCoverage"/>)
/// pair into a concrete <see cref="ChatDispatchDecision"/> describing
/// the role chain that would execute for a chat turn.
///
/// <para>Pass 22 makes this decision observable on every
/// <see cref="Integration.ChatResponse"/> as
/// <c>DispatchedRoleChain</c>, and inside every
/// <c>/api/chat/plan</c> response as <c>Dispatch</c>. Actual
/// multi-role HTTP plumbing stays a future pass — today the runtime
/// still dispatches through the single-lane inference client — but
/// surfacing the decision lets operators and AI agents see the
/// concrete execution plan instead of just the abstract pattern.</para>
///
/// <para>Pure function. No inference calls. No mutable state.
/// Given identical inputs it always returns identical output; the
/// only data it inspects is the planner output and the current role
/// coverage snapshot.</para>
/// </summary>
public static class ChatDispatchPlanner
{
    /// <summary>
    /// Compute the executable dispatch decision for a chat turn.
    /// </summary>
    public static ChatDispatchDecision Decide(DuoCooperationPattern pattern, ModelRoleCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        ModelRoleSlot? workerSlot = coverage.Slots.FirstOrDefault(s => s.Role == nameof(ModelRole.Worker));
        ModelRoleSlot? judgeSlot = coverage.Slots.FirstOrDefault(s => s.Role == nameof(ModelRole.Judge));
        bool workerActive = workerSlot?.IsActive ?? false;
        bool judgeActive = judgeSlot?.IsActive ?? false;

        string? workerBinding = workerActive ? ActiveBindingId(workerSlot) : null;
        string? judgeBinding = judgeActive ? ActiveBindingId(judgeSlot) : null;

        // 1. Deterministic-only: no model inference planned.
        if (pattern == DuoCooperationPattern.DeterministicOnly || (!workerActive && !judgeActive))
        {
            return new ChatDispatchDecision(
                Mode: "deterministic-only",
                PatternName: pattern.ToString(),
                Roles: Array.Empty<string>(),
                PrimaryRole: null,
                PrimaryBindingId: null,
                ReviewerRole: null,
                ReviewerBindingId: null,
                RequiresInference: false,
                Reason: "No Worker or Judge bound — deterministic director answers every chat.",
                Notes: "Add a Worker binding under PalLLM:ModelRoles[] to enable Worker-only dispatch.");
        }

        // 2. Single-role fallback (Worker only).
        if (workerActive && !judgeActive)
        {
            return new ChatDispatchDecision(
                Mode: "single-role",
                PatternName: DuoCooperationPattern.SingleRoleFallback.ToString(),
                Roles: new[] { "Worker" },
                PrimaryRole: "Worker",
                PrimaryBindingId: workerBinding,
                ReviewerRole: null,
                ReviewerBindingId: null,
                RequiresInference: true,
                Reason: $"Only Worker ('{workerBinding}') is bound — Worker handles the task end-to-end.",
                Notes: "Add a Judge binding under PalLLM:ModelRoles[] to unlock audit / review patterns.");
        }

        // 3. Single-role fallback (Judge only).
        if (!workerActive && judgeActive)
        {
            return new ChatDispatchDecision(
                Mode: "single-role",
                PatternName: DuoCooperationPattern.SingleRoleFallback.ToString(),
                Roles: new[] { "Judge" },
                PrimaryRole: "Judge",
                PrimaryBindingId: judgeBinding,
                ReviewerRole: null,
                ReviewerBindingId: null,
                RequiresInference: true,
                Reason: $"Only Judge ('{judgeBinding}') is bound — Judge handles the task end-to-end.",
                Notes: "Add a Worker binding under PalLLM:ModelRoles[] to unlock fan-out / scout patterns.");
        }

        // 4. Both bound — pick the role chain from the pattern.
        return pattern switch
        {
            DuoCooperationPattern.ScoutThenJudge => Duo("duo-sequential",
                new[] { "Worker", "Judge" }, workerBinding, judgeBinding,
                "Worker scouts; Judge reviews before the user sees the reply."),

            DuoCooperationPattern.ArchitectThenImplementerThenAuditor => Duo("duo-sequential",
                new[] { "Judge", "Worker", "Judge" }, workerBinding, judgeBinding,
                "Judge drafts the architecture; Worker implements; Judge audits."),

            DuoCooperationPattern.FanOutThenSynthesis => Duo("duo-fanout",
                new[] { "Worker", "Worker", "Worker", "Judge" }, workerBinding, judgeBinding,
                "Worker produces multiple candidates in parallel; Judge synthesises."),

            DuoCooperationPattern.ParallelDisagreement => Duo("duo-parallel",
                new[] { "Worker", "Judge" }, workerBinding, judgeBinding,
                "Worker and Judge run in parallel; disagreement detector adjudicates."),

            DuoCooperationPattern.BranchTournament => Duo("duo-tournament",
                new[] { "Worker", "Worker", "Worker", "Judge" }, workerBinding, judgeBinding,
                "Worker generates competing branches; Judge picks the winner."),

            DuoCooperationPattern.SequentialSwap => Duo("duo-sequential",
                new[] { "Worker", "Judge", "Worker" }, workerBinding, judgeBinding,
                "Worker drafts; Judge refines; Worker finalises — swap context between roles."),

            DuoCooperationPattern.WorkerLiveJudgeBackground => Duo("duo-background",
                new[] { "Worker", "Judge" }, workerBinding, judgeBinding,
                "Worker replies to the player live; Judge processes long-context synthesis off the hot path."),

            DuoCooperationPattern.DraftThenFinalize => Duo("duo-sequential",
                new[] { "Worker", "Judge" }, workerBinding, judgeBinding,
                "Worker drafts; Judge finalises for quality gates (media prompting, structured output)."),

            DuoCooperationPattern.DuoWatchdog => Duo("duo-watchdog",
                new[] { "Worker", "Judge" }, workerBinding, judgeBinding,
                "Worker executes; Judge watches for drift / policy violations as a safety sentinel."),

            DuoCooperationPattern.DenseAppealCourt => Duo("duo-appeal",
                new[] { "Judge", "Worker", "Judge" }, workerBinding, judgeBinding,
                "Judge drafts the ruling; Worker drafts the counter; Judge adjudicates."),

            _ => Duo("duo-sequential",
                new[] { "Worker", "Judge" }, workerBinding, judgeBinding,
                $"Pattern {pattern} defaults to sequential Worker→Judge."),
        };

        static ChatDispatchDecision Duo(string mode, string[] roles, string? worker, string? judge, string reason)
            => new(
                Mode: mode,
                PatternName: mode == "duo-sequential" ? "DuoSequential" : mode,
                Roles: roles,
                PrimaryRole: roles.Length > 0 ? roles[0] : null,
                PrimaryBindingId: roles.Length > 0 && roles[0] == "Worker" ? worker : judge,
                ReviewerRole: roles.Length > 1 ? roles[^1] : null,
                ReviewerBindingId: roles.Length > 1 && roles[^1] == "Judge" ? judge : worker,
                RequiresInference: true,
                Reason: reason,
                Notes: null);
    }

    private static string? ActiveBindingId(ModelRoleSlot? slot)
    {
        if (slot is null) return null;
        ModelRoleBinding? active = slot.Bindings.FirstOrDefault(b => b.Enabled);
        return active is null || string.IsNullOrWhiteSpace(active.Id) ? null : active.Id;
    }
}

/// <summary>
/// Executable-routing decision the runtime would make for a chat turn,
/// given the current <see cref="ModelRoleRegistry"/> state and the
/// Pass-8 planner's chosen <see cref="DuoCooperationPattern"/>.
///
/// <para>Pass 22 makes this observable on every chat response. The
/// runtime still dispatches through the single-lane inference client
/// today, so <c>RequiresInference</c> is advisory: it describes what
/// the planner asked for, not what the runtime executed. Future
/// passes can flip the single-lane passthrough to actually invoke the
/// chain recorded here.</para>
/// </summary>
/// <param name="Mode">Short machine-friendly bucket — "deterministic-only", "single-role", or one of the "duo-*" modes.</param>
/// <param name="PatternName">Human-readable pattern name the decision is derived from.</param>
/// <param name="Roles">Ordered role chain the pattern would invoke (empty when RequiresInference=false).</param>
/// <param name="PrimaryRole">First role in the chain, or null when no inference is planned.</param>
/// <param name="PrimaryBindingId">Binding id of the primary role, or null when unbound.</param>
/// <param name="ReviewerRole">Last role in the chain, when it differs from PrimaryRole.</param>
/// <param name="ReviewerBindingId">Binding id of the reviewer role, or null when unbound.</param>
/// <param name="RequiresInference">True when the planner wants at least one inference call for this turn.</param>
/// <param name="Reason">One-sentence plain-English explanation of why this chain.</param>
/// <param name="Notes">Optional guidance for unlocking a richer chain (e.g. bind a Judge).</param>
public sealed record ChatDispatchDecision(
    string Mode,
    string PatternName,
    IReadOnlyList<string> Roles,
    string? PrimaryRole,
    string? PrimaryBindingId,
    string? ReviewerRole,
    string? ReviewerBindingId,
    bool RequiresInference,
    string Reason,
    string? Notes);
