using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmPlanningRoutes
{
    internal static void MapPalLlmPlanningRoutes(this RouteGroupBuilder api)
    {
        // Pass 31 / C3 — deterministic directive translator. Converts a
        // natural-language player utterance into an ordered plan of
        // allowlisted pal directives the UE4SS mod can forward to the native
        // pal-AI controller. Never emits above AutomationOptions.AllowedActions
        // — if nothing matches the allowlist, returns an empty plan with a
        // plain-English reason. Deterministic — no inference call.
        api.MapPost("/directives/plan", IResult (DirectivePlanRequest request, PalLlmOptions options) =>
        {
            DirectivePlanRequest r = request ?? new DirectivePlanRequest();
            DirectivePlan plan = DirectiveIntentTranslator.Translate(
                r.Utterance,
                options.Automation.AllowedActions,
                r.AddressedPal);
            return TypedResults.Ok(plan);
        })
            .ValidatePalRequest<DirectivePlanRequest>()
            .WithName("PostDirectivePlan")
            .WithTags("Inspection")
            .WithSummary("Translate a player utterance into an ordered plan of allowlisted pal directives. Deterministic — no inference call. Never emits above AutomationOptions.AllowedActions.");

        // Qwen Duo Mesh planner. Deterministic — no inference call. Given a
        // task kind + risk + hardware tier (and the operator's live role
        // bindings), returns one of the ten cooperation patterns with
        // per-step role assignments, thinking-mode and context-budget
        // recommendations, and an escalation path. Pairs with /api/roles:
        // /api/roles tells you what's bound, /api/duo/plan tells you how to
        // use it.
        api.MapPost("/duo/plan", IResult (DuoPlanRequest request, DuoOrchestratorPlanner planner) =>
        {
            DuoPlan plan = planner.Plan(request ?? new DuoPlanRequest());
            return TypedResults.Ok(plan);
        })
            .ValidatePalRequest<DuoPlanRequest>()
            .WithName("PostDuoPlan")
            .WithTags("Inspection")
            .WithSummary("Recommend a Qwen Duo cooperation pattern for the given task / risk / hardware.");

        // Duo disagreement detector. Takes two outputs (typically the Worker
        // and Judge replies from a ParallelDisagreement cooperation) and
        // returns a structured similarity-score + verdict + safety-signal.
        // Deterministic — no inference call. Pairs with pal_duo_plan: use
        // ParallelDisagreement to know when to run it, use this endpoint to
        // actually evaluate the comparison.
        api.MapPost("/disagreement/check", IResult (DisagreementCheckRequest request) =>
        {
            DisagreementCheckRequest r = request ?? new DisagreementCheckRequest();
            DisagreementAnalysis analysis = DisagreementDetector.Compare(r.WorkerOutput, r.JudgeOutput);
            return TypedResults.Ok(analysis);
        })
            .ValidatePalRequest<DisagreementCheckRequest>()
            .WithName("PostDisagreementCheck")
            .WithTags("Inspection")
            .WithSummary("Compare two model outputs and emit a structured disagreement verdict + safety signal.");
    }

    internal static void MapPalLlmWhyRoute(this RouteGroupBuilder api)
    {
        // Local "why engine" — natural-language causal questions about the
        // PalLLM runtime's recent behaviour. Deterministic-first: no inference
        // call, so the endpoint is always available and ships the same
        // structured answer shape regardless of whether live inference is off,
        // broken, or thriving.
        api.MapPost("/why", IResult (
            WhyRequest request,
            PalLlmRuntime runtime,
            PalLlmMetrics metrics) =>
        {
            // Take ONE health snapshot and reuse it for both the WhyEngine call
            // and the operator-health score. Two GetHealth() invocations would
            // waste snapshot assembly and leave a small consistency window.
            RuntimeHealth health = runtime.GetHealth();
            WhyAnswer answer = WhyEngine.Answer(
                request?.Question,
                health,
                metrics.Snapshot(),
                OperatorHealthScorer.Score(health),
                runtime.Adapter.Snapshot);
            return TypedResults.Ok(answer);
        })
            .ValidatePalRequest<WhyRequest>()
            .WithName("PostWhyQuestion")
            .WithTags("Inspection")
            .WithSummary("Answer a natural-language causal question about the runtime's recent behaviour.");
    }
}
