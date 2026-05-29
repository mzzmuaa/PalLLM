using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Maps /api/state/* routes: lifetime relationship aggregate,
//            personality pack list, session snapshot, opt-in posture
//            report. Read-only views into persistent state.
//   surface: PalLlmStateRoutes.MapState(IEndpointRouteBuilder).
//   gate:    tests/PalLLM.Tests/SidecarEndpointTests.cs (state routes) +
//            tests/PalLLM.Tests/LifetimeRelationshipAggregatorTests.cs.
//   adr:     None directly.
//   docs:    docs/API.md (/api/state/*).
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmStateRoutes
{
    internal static void MapPalLlmMemoryRelationshipRoutes(this RouteGroupBuilder api)
    {
        api.MapPost("/memory/recall", (MemoryRecallRequest request, PalLlmRuntime runtime) =>
        {
            var results = runtime.RecallMemory(request).Select(match => new
            {
                match.Score,
                match.Entry.CharacterId,
                match.Entry.CharacterName,
                match.Entry.SpeakerRole,
                match.Entry.Content,
                match.Entry.Tags,
                match.Entry.CreatedAtUtc,
                match.Entry.Importance,
            });

            return TypedResults.Ok(results);
        })
            .ValidatePalRequest<MemoryRecallRequest>()
            .WithName("RecallMemory")
            .WithTags("Memory")
            .WithSummary("Recall scored memory matches for a query.");

        api.MapGet("/relationships", (PalLlmRuntime runtime) =>
            TypedResults.Ok(runtime.GetRelationships().ToArray()))
            .WithName("ListRelationships")
            .WithTags("Relationships")
            .WithSummary("List every tracked per-character relationship.");

        api.MapGet("/relationships/{characterId:int}", (int characterId, PalLlmRuntime runtime) =>
        {
            CharacterRelationship? relationship = runtime.GetRelationship(characterId);
            return relationship is null
                ? Results.NotFound()
                : Results.Ok(relationship);
        })
            .WithName("GetRelationshipByCharacterId")
            .WithTags("Relationships")
            .WithSummary("Get the tracked relationship for a single character id.")
            .Produces<CharacterRelationship>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    internal static void MapPalLlmSessionRoutes(this RouteGroupBuilder api)
    {
        api.MapPost("/session/save", (PalLlmRuntime runtime) =>
            TypedResults.Ok(runtime.SaveSession()))
            .WithName("SaveSession")
            .WithTags("Session")
            .WithSummary("Persist the current session state to disk.");

        api.MapPost("/session/reload", (PalLlmRuntime runtime) =>
            TypedResults.Ok(runtime.LoadSession()))
            .WithName("ReloadSession")
            .WithTags("Session")
            .WithSummary("Reload the session state from disk.");
    }
}
