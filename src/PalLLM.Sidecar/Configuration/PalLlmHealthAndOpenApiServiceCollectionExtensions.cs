using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmHealthAndOpenApiServiceCollectionExtensions
{
    public static IServiceCollection AddPalLlmHealthAndOpenApi(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<LivenessHealthCheck>("liveness", tags: ["live"])
            .AddCheck<ReadinessHealthCheck>("readiness", tags: ["ready"])
            .AddCheck<InferencePerformanceReadinessHealthCheck>("inference_recent_window", tags: ["ready"]);

        // .NET 10 native OpenAPI 3.1 document generation. The route
        // registrations in Program.cs are the source of truth; the document is
        // regenerated on each request to GET /openapi/v1.json so it can never
        // drift from the actual routes.
        services.AddOpenApi(options =>
        {
            options.CreateSchemaReferenceId = OpenApiSchemaReferenceIds.Create;
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (OpenApiSchemaReferenceIds.TryGetOverrideName(context.JsonTypeInfo.Type, out string? schemaId))
                {
                    schema.Title = schemaId;
                }

                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, context, _) =>
            {
                document.Info.Title = "PalLLM sidecar API";
                document.Info.Version = "v1";
                document.Info.Description =
                    "Local-first companion-runtime HTTP surface. Application routes live under /api; " +
                    "operational routes (/metrics, /health/*, /openapi/*) and the MCP transport " +
                    "endpoint (/mcp) are intentionally separate from the JSON API namespace. " +
                    "Bridge-specific world snapshot internals are published under neutral schema ids " +
                    "so external clients do not have to couple to the current game target.";
                return Task.CompletedTask;
            });
        });

        return services;
    }
}
