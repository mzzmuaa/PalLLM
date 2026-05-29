using System.IO.Compression;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using PalLLM.Domain.Configuration;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    DI registration entry point for the PalLLM Domain runtime:
//            portable adapter, runtime, fallback engine, memory store,
//            relationship aggregator, narrative pack service, and all the
//            advisor/builder singletons. Called by
//            Program.cs.AddPalLlmCore().
//   surface: PalLlmCoreServiceCollectionExtensions.AddPalLlmCore(IServiceCollection,
//            IConfiguration).
//   gate:    tests/PalLLM.Tests/SidecarTestFixture.cs (fixture wiring) +
//            every fixture that boots the host.
//   adr:     ADR 0002 (portable adapter seam).
//   docs:    docs/ARCHITECTURE.md (DI lanes), docs/CODE_MAP.md (where
//            things live).
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmCoreServiceCollectionExtensions
{
    public static IServiceCollection AddPalLlmCore(
        this IServiceCollection services,
        IConfiguration configuration,
        HttpSurfaceOptions httpOptions)
    {
        services.Configure<JsonOptions>(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = null;
            PalLlmJsonOptions.AddSourceGeneration(options.SerializerOptions);
        });

        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

                if (string.IsNullOrWhiteSpace(context.ProblemDetails.Instance) &&
                    context.HttpContext.Request.Path.HasValue)
                {
                    context.ProblemDetails.Instance = context.HttpContext.Request.Path;
                }
            };
        });

        // Registered first so it sees malformed-input exceptions before the
        // default exception handler maps them to a confusing 500. See
        // MalformedRequestExceptionHandler for the narrow allow-list of
        // exception shapes it handles; everything else still flows through to
        // the default 500 ProblemDetails path.
        services.AddMalformedRequestExceptionHandler();

        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
        });
        services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });
        services.Configure<GzipCompressionProviderOptions>(options =>
        {
            options.Level = CompressionLevel.Fastest;
        });

        services.AddOptions<PalLlmOptions>()
            .Bind(configuration.GetSection("PalLLM"))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PalLlmOptions>, PalLlmOptionsValidator>();
        services.AddSingleton(sp => sp.GetRequiredService<IOptions<PalLlmOptions>>().Value);

        services.AddOutputCache(options =>
        {
            if (httpOptions.OpenApiCacheMinutes > 0)
            {
                options.AddPolicy("openapi-doc", policy => policy.Expire(TimeSpan.FromMinutes(httpOptions.OpenApiCacheMinutes)));
            }

            if (httpOptions.FeatureCatalogCacheMinutes > 0)
            {
                options.AddPolicy(
                    "feature-catalog",
                    policy => policy
                        .Expire(TimeSpan.FromMinutes(httpOptions.FeatureCatalogCacheMinutes))
                        .Tag("feature-catalog"));
            }

            if (httpOptions.SelfDescriptionCacheSeconds > 0)
            {
                options.AddPolicy(
                    "self-description",
                    policy => policy
                        .Expire(TimeSpan.FromSeconds(httpOptions.SelfDescriptionCacheSeconds))
                        .Tag("self-description"));
            }

            if (httpOptions.UpstreamSnapshotCacheSeconds > 0)
            {
                options.AddPolicy(
                    "upstream-mcp",
                    policy => policy
                        .Expire(TimeSpan.FromSeconds(httpOptions.UpstreamSnapshotCacheSeconds))
                        .Tag("upstream-mcp"));
            }
        });

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                await TypedResults.Problem(
                        statusCode: StatusCodes.Status429TooManyRequests,
                        title: "Too Many Requests",
                        detail: "PalLLM is deliberately shedding excess work on this lane to preserve low interactive latency for the local runtime.")
                    .ExecuteAsync(context.HttpContext);
            };
            options.AddConcurrencyLimiter("chat-heavy", limiter =>
            {
                limiter.PermitLimit = httpOptions.ChatConcurrentRequests;
                limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiter.QueueLimit = httpOptions.ChatQueueLimit;
            });
            options.AddConcurrencyLimiter("vision-heavy", limiter =>
            {
                limiter.PermitLimit = httpOptions.VisionConcurrentRequests;
                limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiter.QueueLimit = httpOptions.VisionQueueLimit;
            });
            options.AddConcurrencyLimiter("tts-heavy", limiter =>
            {
                limiter.PermitLimit = httpOptions.TtsConcurrentRequests;
                limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
                limiter.QueueLimit = httpOptions.TtsQueueLimit;
            });
        });

        services.AddRequestTimeouts(options =>
        {
            options.AddPolicy(
                "chat-timeout",
                BuildRequestTimeoutPolicy(
                    httpOptions.ChatRequestTimeoutSeconds,
                    "PalLLM stopped this chat request because it exceeded PalLLM:Http:ChatRequestTimeoutSeconds. Retry with a smaller prompt or tune the timeout after measuring the active model lane."));
            options.AddPolicy(
                "vision-timeout",
                BuildRequestTimeoutPolicy(
                    httpOptions.VisionRequestTimeoutSeconds,
                    "PalLLM stopped this vision request because it exceeded PalLLM:Http:VisionRequestTimeoutSeconds. Retry with smaller media or tune the timeout after measuring the vision lane."));
            options.AddPolicy(
                "tts-timeout",
                BuildRequestTimeoutPolicy(
                    httpOptions.TtsRequestTimeoutSeconds,
                    "PalLLM stopped this TTS request because it exceeded PalLLM:Http:TtsRequestTimeoutSeconds. Retry with shorter text or tune the timeout after measuring the speech lane."));
        });

        return services;
    }

    private static RequestTimeoutPolicy BuildRequestTimeoutPolicy(int timeoutSeconds, string detail) =>
        new()
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)),
            TimeoutStatusCode = StatusCodes.Status503ServiceUnavailable,
            WriteTimeoutResponse = context =>
                TypedResults.Problem(
                        statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "Request timed out.",
                        detail: detail)
                    .ExecuteAsync(context),
        };
}
