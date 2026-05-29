using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmObservabilityServiceCollectionExtensions
{
    public static bool AddPalLlmObservability(this IServiceCollection services)
    {
        // Optional OpenTelemetry distributed observability (traces + metrics + logs).
        // Activated only when OTEL_EXPORTER_OTLP_ENDPOINT is set in the process
        // environment so the default localhost deployment carries zero OTel
        // overhead - no listeners get registered, PalLlmTelemetry.Source.StartActivity
        // returns null, and neither the ASP.NET Core / HttpClient instrumentation
        // nor the OpenTelemetry log provider install their bridges. The Domain
        // meter still exists, but without a reader/exporter the cost of recording
        // stays at the no-listener fast path. When the env var IS set (typically to
        // http://localhost:4317 for a local Tempo/Jaeger or a collector URL),
        // incoming HTTP requests, outgoing HttpClient calls, PalLLM runtime spans,
        // GenAI client histograms, AND ILogger log records all flow through OTLP.
        // Because OTel logs pick up the active Activity's trace_id/span_id
        // automatically, a log message emitted during a chat turn is linked to
        // its `pal.chat` span in the backend - click a span, see its logs.
        // Standard OTel env vars (OTEL_SERVICE_NAME, OTEL_RESOURCE_ATTRIBUTES,
        // OTEL_EXPORTER_OTLP_HEADERS, OTEL_EXPORTER_OTLP_PROTOCOL) are honoured.
        // See docs/OPERATIONS.md Sec. "Enabling distributed tracing".
        string? otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            return false;
        }

        string serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "pal-llm-sidecar";
        string? serviceVersion = typeof(PalLlmRuntime).Assembly.GetName().Version?.ToString();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceVersion: serviceVersion))
            .WithTracing(tracing => tracing
                .AddSource(PalLlmTelemetry.SourceName)
                .AddAspNetCoreInstrumentation(options =>
                {
                    // Health and metrics are scraped every few seconds; tracing
                    // them would drown the interesting chat/bridge spans.
                    options.Filter = ShouldObserveHttpRequest;
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(metrics => metrics
                .AddMeter(PalLlmTelemetry.MeterName)
                .AddView(
                    PalLlmTelemetry.GenAiClientOperationDurationMetricName,
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = PalLlmTelemetry.GenAiClientOperationDurationBoundaries,
                    })
                .AddView(
                    PalLlmTelemetry.GenAiClientTokenUsageMetricName,
                    new ExplicitBucketHistogramConfiguration
                    {
                        Boundaries = PalLlmTelemetry.GenAiClientTokenUsageBoundaries,
                    })
                .AddOtlpExporter())
            .WithLogging(logging => logging
                .AddOtlpExporter());

        services.AddSingleton<PalLlmOperationalTelemetry>();
        return true;
    }

    private static bool ShouldObserveHttpRequest(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
}
