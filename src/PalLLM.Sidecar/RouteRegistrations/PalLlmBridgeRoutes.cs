using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Maps the /api/bridge/* operational routes: bridge proof,
//            native-proof evidence, HUD-bind recommendations. Each route
//            is a thin shim that calls into PalLlmRuntime and serialises
//            through PalLlmDomainJsonSerializerContext.
//   surface: PalLlmBridgeRoutes.MapBridge(IEndpointRouteBuilder).
//   gate:    tests/PalLLM.Tests/SidecarEndpointTests.cs (bridge routes).
//   adr:     ADR 0003 (one-way advisory bridge).
//   docs:    docs/API.md (/api/bridge/*).
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmBridgeRoutes
{
    internal static void MapPalLlmBridgeRoutes(this RouteGroupBuilder api)
    {
        api.MapPost("/bridge/drain", (PalLlmRuntime runtime) => TypedResults.Ok(runtime.DrainInbox()))
            .WithName("DrainBridgeInbox")
            .WithTags("Bridge")
            .WithSummary("Drain the bridge inbox immediately.");

        api.MapGet("/bridge/outbox", (PalLlmRuntime runtime) =>
            TypedResults.Ok(runtime.GetOutboxListings().ToArray()))
            .WithName("ListBridgeOutbox")
            .WithTags("Bridge")
            .WithSummary("List the pending outbox envelopes waiting for a game-side consumer.");

        api.MapGet("/bridge/ui-probe", (PalLlmRuntime runtime) =>
            TypedResults.Ok(runtime.GetUiProbeDiagnostics()))
            .WithName("GetUiProbeDiagnostics")
            .WithTags("Bridge")
            .WithSummary("Get ranked UI widget probe diagnostics from bridge dumps.");

        api.MapPost("/bridge/outbox/clear", (PalLlmRuntime runtime) =>
            TypedResults.Ok(new ClearOutboxResponse(runtime.ClearOutbox())))
            .WithName("ClearBridgeOutbox")
            .WithTags("Bridge")
            .WithSummary("Clear every pending outbox envelope.");
    }
}
