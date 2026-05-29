using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.Routing;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmHealthRoutes
{
    internal static void MapPalLlmHealthRoutes(
        this IEndpointRouteBuilder app,
        RouteGroupBuilder api,
        HttpSurfaceOptions httpOptions)
    {
        api.MapGet("/health", (PalLlmRuntime runtime) => TypedResults.Ok(runtime.GetHealth()))
            .WithName("GetRuntimeHealth")
            .WithTags("Inspection")
            .WithSummary("Get the current runtime health snapshot.")
            .Produces<RuntimeHealth>(StatusCodes.Status200OK);

        api.MapGet("/dashboard", IResult (HttpContext context, PalLlmRuntime runtime) =>
        {
            DashboardSnapshot dashboard = runtime.GetDashboardSnapshot();
            string etag = ConditionalHttp.CreateStrongEtag(
                DashboardEtagPayload.From(dashboard),
                PalLlmJsonSerializerContext.Default.DashboardEtagPayload);

            ConditionalHttp.ApplyPrivateCaching(context, etag, maxAge: null);
            if (ConditionalHttp.RequestMatchesEtag(context.Request, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            context.Response.Headers.Append("Server-Timing", $"dashboard;dur={dashboard.ServerLatencyMs}");
            return TypedResults.Ok(dashboard);
        })
            .WithName("GetDashboardSnapshot")
            .WithTags("Inspection")
            .WithSummary("Get the aggregated dashboard snapshot used by the field console.")
            .Produces<DashboardSnapshot>(StatusCodes.Status200OK);

        // Prometheus scrape target. Lives under /metrics (no /api prefix) to
        // match the convention Prometheus, Grafana Agent, and OTel collectors expect.
        app.MapGet("/metrics", (PalLlmRuntime runtime) =>
            Results.Text(
                PrometheusExporter.Render(
                    runtime.GetHealth(),
                    runtime.GetInferencePerformanceSnapshot()),
                "text/plain; version=0.0.4"));

        // Standard K8s / cloud health endpoints. /health/live is liveness;
        // /health/ready is readiness. Both return status plus per-check JSON.
        app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live"),
            ResponseWriter = PalLlmHealthResponseWriter.WriteJsonAsync,
        });

        app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("ready"),
            ResponseWriter = PalLlmHealthResponseWriter.WriteJsonAsync,
        });

        RouteHandlerBuilder featureCatalogEndpoint = api.MapGet("/features", IResult (HttpContext context, PalLlmRuntime runtime) =>
        {
            FeatureDescriptor[] features = runtime.GetFeatures().ToArray();
            string etag = ConditionalHttp.CreateStrongEtag(
                features,
                PalLlmJsonSerializerContext.Default.FeatureDescriptorArray);
            TimeSpan? maxAge = httpOptions.FeatureCatalogCacheMinutes > 0
                ? TimeSpan.FromMinutes(httpOptions.FeatureCatalogCacheMinutes)
                : null;

            ConditionalHttp.ApplyPrivateCaching(context, etag, maxAge);
            if (ConditionalHttp.RequestMatchesEtag(context.Request, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return TypedResults.Ok(features);
        })
            .WithName("GetFeatureCatalog")
            .WithTags("Inspection")
            .WithSummary("List the shipped feature catalog entries.");

        if (httpOptions.FeatureCatalogCacheMinutes > 0)
        {
            featureCatalogEndpoint.CacheOutput("feature-catalog");
        }

        // AI-friendly one-shot self-description. MCP clients and custom LLM
        // callers can fetch this on connect to learn the running sidecar,
        // current capabilities, and next endpoint/tool choices in one round-trip.
        RouteHandlerBuilder selfDescriptionEndpoint = api.MapGet("/describe", IResult (
            HttpContext context,
            PalLlmRuntime runtime,
            PalLlmOptions options,
            EndpointDataSource endpointDataSource) =>
        {
            SelfDescription description = SelfDescriptionBuilder.Build(runtime, options, endpointDataSource);
            string etag = ConditionalHttp.CreateStrongEtag(
                description,
                PalLlmJsonSerializerContext.Default.SelfDescription);
            TimeSpan? maxAge = httpOptions.SelfDescriptionCacheSeconds > 0
                ? TimeSpan.FromSeconds(httpOptions.SelfDescriptionCacheSeconds)
                : null;

            ConditionalHttp.ApplyPrivateCaching(context, etag, maxAge);
            if (ConditionalHttp.RequestMatchesEtag(context.Request, etag))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
            }

            return TypedResults.Ok(description);
        })
            .WithName("GetSelfDescription")
            .WithTags("Inspection")
            .WithSummary("One-shot self-description manifest for AI / MCP consumers.");

        if (httpOptions.SelfDescriptionCacheSeconds > 0)
        {
            selfDescriptionEndpoint.CacheOutput("self-description");
        }

        // Dynamic "what should I do next?" guidance derived from live health,
        // role, and options state. Keep it uncached so it reflects current setup.
        api.MapGet("/quickstart", IResult (
            HttpContext context,
            PalLlmRuntime runtime,
            PalLlmOptions options,
            ModelRoleRegistry roleRegistry) =>
        {
            QuickstartGuide guide = QuickstartGuideBuilder.Build(runtime, options, roleRegistry);
            return TypedResults.Ok(guide);
        })
            .WithName("GetQuickstartGuide")
            .WithTags("Inspection")
            .WithSummary("Live state-aware next-step guidance for humans + AI.");
    }
}
