using Microsoft.AspNetCore.OutputCaching;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar.Mcp;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Maps /api/inference/* routes: performance snapshot, active
//            model and tier, collaboration plan, circuit-breaker state.
//            Read-only introspection of the inference lane.
//   surface: PalLlmInferenceRoutes.MapInference(IEndpointRouteBuilder).
//   gate:    tests/PalLLM.Tests/SidecarEndpointTests.cs (inference routes)
//            + tests/PalLLM.Tests/InferenceClientTests.cs.
//   adr:     ADR 0001 (deterministic-first reply pipeline).
//   docs:    docs/API.md (/api/inference/*), docs/MODEL_COLLABORATION.md.
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmInferenceRoutes
{
    internal static void MapPalLlmInferenceRoutes(this RouteGroupBuilder api, HttpSurfaceOptions httpOptions)
    {
        api.MapGet("/inference/performance", IResult (HttpContext context, PalLlmRuntime runtime) =>
        {
            InferencePerformanceSnapshot snapshot = runtime.GetInferencePerformanceSnapshot();
            string etag = ConditionalHttp.CreateStrongEtag(
                InferencePerformanceEtagPayload.From(snapshot),
                PalLlmJsonSerializerContext.Default.InferencePerformanceEtagPayload);

            ConditionalHttp.ApplyPrivateCaching(context, etag, maxAge: null);
            if (ConditionalHttp.RequestMatchesEtag(context.Request, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return TypedResults.Ok(snapshot);
        })
            .WithName("GetInferencePerformance")
            .WithTags("Inspection")
            .WithSummary("Get a recent per-model live inference summary with latency-budget assessment and token trends for the active GenAI lanes.")
            .Produces<InferencePerformanceSnapshot>(StatusCodes.Status200OK);

        api.MapGet("/inference/collaboration", (
            int? vramGb,
            int? ramGb,
            int? unifiedMemoryGb,
            bool? cpuOnly,
            bool? preferParallel,
            ModelCollaborationPlanner planner) =>
            TypedResults.Ok(planner.GetSnapshot(new ModelHardwareHints(
                VramGb: vramGb,
                RamGb: ramGb,
                UnifiedMemoryGb: unifiedMemoryGb,
                CpuOnly: cpuOnly ?? false,
                PreferParallel: preferParallel ?? true))))
            .WithName("GetInferenceCollaborationPlan")
            .WithTags("Inspection")
            .WithSummary("Get a hardware-aware collaboration plan for the configured local model lanes.")
            .Produces<ModelCollaborationSnapshot>(StatusCodes.Status200OK);

        api.MapPost("/inference/collaboration/plan", (ModelCollaborationDecisionRequest request, ModelCollaborationDecisionPlanner planner) =>
            TypedResults.Ok(planner.Plan(request)))
            .ValidatePalRequest<ModelCollaborationDecisionRequest>()
            .WithName("PlanInferenceCollaborationTask")
            .WithTags("Inspection")
            .WithSummary("Plan the exact dense-plus-MoE operating strategy for a concrete task and hardware profile.")
            .Produces<ModelCollaborationDecision>(StatusCodes.Status200OK)
            .ProducesValidationProblem();

        api.MapPost("/inference/warmup", async (PalLlmRuntime runtime, CancellationToken cancellationToken) =>
                TypedResults.Ok(await runtime.WarmInferenceAsync("manual_api", force: false, cancellationToken)))
            .WithName("WarmInferenceModel")
            .WithTags("Inspection")
            .WithSummary("Prime the currently active inference model with a bounded low-token warmup request.")
            .Produces<InferenceWarmupSnapshot>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithRequestTimeout("chat-timeout");

        RouteHandlerBuilder upstreamMcpEndpoint = api.MapGet("/mcp/upstream", IResult (HttpContext context, McpUpstreamClientPool pool) =>
        {
            UpstreamSnapshot[] snapshots = pool.GetSnapshots().Values
                .OrderBy(s => s.Id, StringComparer.Ordinal)
                .ToArray();
            string etag = ConditionalHttp.CreateStrongEtag(
                snapshots,
                PalLlmJsonSerializerContext.Default.UpstreamSnapshotArray);
            TimeSpan? maxAge = httpOptions.UpstreamSnapshotCacheSeconds > 0
                ? TimeSpan.FromSeconds(httpOptions.UpstreamSnapshotCacheSeconds)
                : null;

            ConditionalHttp.ApplyPrivateCaching(context, etag, maxAge);
            if (ConditionalHttp.RequestMatchesEtag(context.Request, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return TypedResults.Ok(snapshots);
        })
            .WithName("ListUpstreamMcpSnapshots")
            .WithTags("Mcp")
            .WithSummary("List the discovered snapshots of configured upstream MCP servers.");

        if (httpOptions.UpstreamSnapshotCacheSeconds > 0)
        {
            upstreamMcpEndpoint.CacheOutput("upstream-mcp");
        }
    }
}
