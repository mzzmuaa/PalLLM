using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Packs;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmContentWorldRoutes
{
    internal static void MapPalLlmContentWorldRoutes(this RouteGroupBuilder api)
    {
        api.MapGet("/packs", (PalLlmRuntime runtime) => TypedResults.Ok(runtime.GetPacks().ToArray()))
            .WithName("ListNarrativePacks")
            .WithTags("Content")
            .WithSummary("List the narrative packs currently loaded by the runtime.");

        // Pass 315 - per-species personality resolver. Operators map species
        // to a personality-pack id via PalLlmOptions:Packs:DefaultBySpecies;
        // this exposes the lookup without asking callers to re-implement it.
        api.MapGet("/packs/resolve", IResult (string? species, string? fallback, PalLlmOptions options) =>
        {
            SpeciesPersonalityResolution resolution = SpeciesPersonalityResolver.Resolve(
                species,
                options.Packs.DefaultBySpecies,
                fallback);
            return TypedResults.Ok(resolution);
        })
            .WithName("ResolvePackForSpecies")
            .WithTags("Content")
            .WithSummary("Pick the personality-pack id that should apply to a character of the given species (operator-configured species map, then optional caller fallback).");

        api.MapGet("/logs", (PalLlmRuntime runtime) => TypedResults.Ok(runtime.GetLogs().ToArray()))
            .WithName("ListAdapterLogs")
            .WithTags("Inspection")
            .WithSummary("Get the recent adapter log tail.");

        api.MapGet("/world", (PalLlmRuntime runtime) => TypedResults.Ok(runtime.GetWorldState()))
            .WithName("GetWorldState")
            .WithTags("Inspection")
            .WithSummary("Get the current world snapshot plus bridge activity.");

        api.MapPost("/packs/reload", (PalLlmRuntime runtime) =>
        {
            runtime.ReloadPacks();
            return TypedResults.Accepted("/api/packs");
        })
            .WithName("ReloadNarrativePacks")
            .WithTags("Content")
            .WithSummary("Reload narrative packs from disk.")
            .Produces(StatusCodes.Status202Accepted);

        api.MapPost("/packs/validate", async (HttpRequest request) =>
        {
            const int MaxPackValidationBytes = NarrativePackValidator.MaxPackBytes;

            if (!request.HasJsonContentType())
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status415UnsupportedMediaType,
                    title: "Unsupported Media Type",
                    detail: "Pack validation accepts only application/json payloads.");
            }

            if (request.ContentLength is 0)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["body"] = ["Request body is required."],
                });
            }

            HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(request, MaxPackValidationBytes);
            if (request.ContentLength > MaxPackValidationBytes)
            {
                return BuildPackValidationPayloadTooLargeResult(MaxPackValidationBytes);
            }

            HttpRequestBodyReadLimiter.BoundedTextReadResult bodyRead = await HttpRequestBodyReadLimiter.ReadUtf8Async(
                request,
                MaxPackValidationBytes,
                request.HttpContext.RequestAborted);
            if (bodyRead.ExceededLimit)
            {
                return BuildPackValidationPayloadTooLargeResult(MaxPackValidationBytes);
            }

            string json = bodyRead.Text;
            if (string.IsNullOrWhiteSpace(json))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["body"] = ["Request body is required."],
                });
            }

            NarrativePackValidationResult result = NarrativePackValidator.Validate(json);
            return result.IsValid
                ? Results.Ok(result)
                : Results.BadRequest(result);
        })
            .WithName("ValidateNarrativePack")
            .WithTags("Content")
            .WithSummary("Validate a narrative pack payload without loading it.")
            .Accepts<string>("application/json")
            .Produces<NarrativePackValidationResult>(StatusCodes.Status200OK)
            .Produces<NarrativePackValidationResult>(StatusCodes.Status400BadRequest)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        api.MapPost("/snapshot", (GameWorldSnapshot snapshot, PalLlmRuntime runtime) =>
        {
            runtime.UpdateSnapshot(snapshot);
            return TypedResults.Accepted("/api/health");
        })
            .WithName("UpdateWorldSnapshot")
            .WithTags("Bridge")
            .WithSummary("Push a new game snapshot into the runtime.")
            .Produces(StatusCodes.Status202Accepted);
    }

    private static IResult BuildPackValidationPayloadTooLargeResult(int maxBytes) =>
        Results.Problem(
            statusCode: StatusCodes.Status413PayloadTooLarge,
            title: "Payload Too Large",
            detail: $"Pack validation payloads must be {maxBytes} bytes or smaller.");
}
