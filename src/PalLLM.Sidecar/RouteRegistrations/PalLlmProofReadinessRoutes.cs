using Microsoft.AspNetCore.Routing;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Maps /api/release/readiness + the proof-bundle export
//            endpoints. Surfaces release-evidence artifacts (smoke proof,
//            native-proof, full-audit, package verification) as one
//            machine-readable shape.
//   surface: PalLlmProofReadinessRoutes.MapProofReadiness(IEndpointRouteBuilder).
//   gate:    tests/PalLLM.Tests/ReleaseReadinessTests.cs +
//            tests/PalLLM.Tests/SidecarEndpointTests.cs (release routes).
//   adr:     None directly.
//   docs:    docs/RELEASE.md, docs/API.md (/api/release/readiness).
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmProofReadinessRoutes
{
    internal static void MapPalLlmProofPacketRoute(this RouteGroupBuilder api)
    {
        // Machine-readable provenance bundle for an automated PalLLM decision.
        // Every packet carries the subsystem, decision, primary reason,
        // evidence, rollback path, and confidence so operators and downstream
        // audit tooling can reconstruct the "why" of every automated action.
        api.MapPost("/proof/packet", IResult (ProofPacketRequest request) =>
        {
            ProofPacketRequest r = request ?? new ProofPacketRequest();
            ProofPacket packet = ProofPacketBuilder.Build(
                subsystem: string.IsNullOrWhiteSpace(r.Subsystem) ? "operator-submitted" : r.Subsystem,
                decision: string.IsNullOrWhiteSpace(r.Decision) ? "(unspecified)" : r.Decision,
                primaryReason: r.PrimaryReason ?? string.Empty,
                evidenceLines: r.Evidence ?? new List<string>(),
                rollbackPath: r.RollbackPath ?? "(no rollback path recorded)",
                confidence: r.Confidence ?? "medium",
                humanReviewRequired: r.HumanReviewRequired);
            return TypedResults.Ok(packet);
        })
            .ValidatePalRequest<ProofPacketRequest>()
            .WithName("PostProofPacket")
            .WithTags("Inspection")
            .WithSummary("Build a machine-readable proof packet for an automated decision (provenance bundle with rollback).");
    }

    internal static void MapPalLlmReleaseProofRoutes(
        this RouteGroupBuilder api,
        HttpSurfaceOptions httpOptions)
    {
        api.MapGet("/release/readiness", IResult (
            HttpContext context,
            PalLlmRuntime runtime,
            EndpointDataSource endpointDataSource,
            PalLlmOptions options) =>
        {
            ReleaseReadinessSnapshot snapshot = ReleaseReadinessBuilder.Create(runtime, endpointDataSource, options);
            string etag = ConditionalHttp.CreateStrongEtag(
                ReleaseReadinessEtagPayload.From(snapshot),
                PalLlmJsonSerializerContext.Default.ReleaseReadinessEtagPayload);
            TimeSpan? maxAge = httpOptions.FeatureCatalogCacheMinutes > 0
                ? TimeSpan.FromMinutes(httpOptions.FeatureCatalogCacheMinutes)
                : null;

            ConditionalHttp.ApplyPrivateCaching(context, etag, maxAge);
            if (ConditionalHttp.RequestMatchesEtag(context.Request, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return TypedResults.Ok(snapshot);
        })
            .WithName("GetReleaseReadiness")
            .WithTags("Inspection")
            .WithSummary("Get a machine-readable release/readiness snapshot with canonical audit commands, doc pointers, and publication blockers.")
            .Produces<ReleaseReadinessSnapshot>(StatusCodes.Status200OK);

        api.MapGet("/bridge/proof", IResult (
            HttpContext context,
            PalLlmRuntime runtime) =>
        {
            BridgeProofSnapshot snapshot = BridgeProofBuilder.Create(runtime);
            string etag = ConditionalHttp.CreateStrongEtag(
                BridgeProofEtagPayload.From(snapshot),
                PalLlmJsonSerializerContext.Default.BridgeProofEtagPayload);
            TimeSpan? maxAge = httpOptions.FeatureCatalogCacheMinutes > 0
                ? TimeSpan.FromMinutes(httpOptions.FeatureCatalogCacheMinutes)
                : null;

            ConditionalHttp.ApplyPrivateCaching(context, etag, maxAge);
            if (ConditionalHttp.RequestMatchesEtag(context.Request, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return TypedResults.Ok(snapshot);
        })
            .WithName("GetBridgeProof")
            .WithTags("Inspection")
            .WithSummary("Get a machine-readable Palworld bridge proof snapshot with native readiness, widget-seam evidence, and live request/delivery closure.")
            .Produces<BridgeProofSnapshot>(StatusCodes.Status200OK);
    }
}
