using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Sidecar;

/// <summary>
/// Ties the Pass-8 <see cref="DuoOrchestratorPlanner"/> together with
/// the Pass-16 <see cref="ChatTaskKindInferer"/>: given a raw chat
/// request (user message + optional task tag) plus the operator's risk
/// + hardware preference, returns the Duo cooperation plan that would
/// be used for that chat — without running any inference.
///
/// <para>Pure advisory. Does NOT mutate runtime state, does NOT touch
/// the inference pipeline, does NOT require a live Worker or Judge
/// binding. Operators can call this before deciding to enable a
/// specific role; AI agents can call it to answer "how would my next
/// chat flow through the mesh?" in one round-trip.</para>
/// </summary>
internal static class ChatPlanAdvisor
{
    public static ChatPlanAdvice Advise(ChatPlanRequest request, DuoOrchestratorPlanner planner, ModelRoleRegistry? registry = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(planner);

        DuoTaskKind kind = ChatTaskKindInferer.Infer(request.UserMessage, request.TaskTag);
        DuoRiskLevel risk = ParseRisk(request.Risk);
        DuoHardwareTier hardware = ParseHardware(request.Hardware);

        DuoPlan plan = planner.Plan(new DuoPlanRequest
        {
            Kind = kind,
            Risk = risk,
            Hardware = hardware,
        });

        // Pass 22 — compute the executable role chain for the given
        // pattern under the current role bindings so the caller sees
        // both "what pattern was chosen" and "what role chain would
        // actually execute right now". When no registry is supplied
        // (older caller), the dispatch decision falls back to a
        // bindings-free "deterministic-only" result.
        ModelRoleCoverage coverage = registry is null
            ? new ModelRoleCoverage(
                Slots: Array.Empty<ModelRoleSlot>(),
                ActiveBindings: 0,
                TotalBindings: 0,
                CriticalGaps: Array.Empty<string>(),
                PairingPattern: "no-registry")
            : registry.GetCoverage();
        ChatDispatchDecision dispatch = ChatDispatchPlanner.Decide(plan.Pattern, coverage);

        return new ChatPlanAdvice(
            InferredTaskKind: kind.ToString(),
            Risk: risk.ToString(),
            Hardware: hardware.ToString(),
            Plan: plan,
            Dispatch: dispatch);
    }

    private static DuoRiskLevel ParseRisk(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out DuoRiskLevel parsed)
            ? parsed
            : DuoRiskLevel.Low;

    private static DuoHardwareTier ParseHardware(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out DuoHardwareTier parsed)
            ? parsed
            : DuoHardwareTier.Standard;
}

public sealed class ChatPlanRequest
{
    public string? UserMessage { get; init; }
    public string? TaskTag { get; init; }
    public string? Risk { get; init; }
    public string? Hardware { get; init; }
}

public sealed record ChatPlanAdvice(
    string InferredTaskKind,
    string Risk,
    string Hardware,
    DuoPlan Plan,
    ChatDispatchDecision Dispatch);
