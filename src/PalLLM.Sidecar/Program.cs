using System.IO;
using System.Net.Http.Headers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Packs;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar;

var builder = WebApplication.CreateBuilder(args);
bool isOpenApiBuild = builder.Environment.IsEnvironment("OpenApiBuild");

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
    PalLlmJsonOptions.AddSourceGeneration(options.SerializerOptions);
});

builder.Services.AddProblemDetails(options =>
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
builder.Services.AddMalformedRequestExceptionHandler();

builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});
builder.Services.Configure<GzipCompressionProviderOptions>(options =>
{
    options.Level = CompressionLevel.Fastest;
});

builder.Services.AddOptions<PalLlmOptions>()
    .Bind(builder.Configuration.GetSection("PalLLM"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<PalLlmOptions>, PalLlmOptionsValidator>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<PalLlmOptions>>().Value);

HttpSurfaceOptions httpOptions = builder.Configuration.GetSection("PalLLM:Http").Get<HttpSurfaceOptions>() ?? new();

builder.Services.AddOutputCache(options =>
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

builder.Services.AddRateLimiter(options =>
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

builder.Services.AddRequestTimeouts(options =>
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

// HttpClient for the availability probe uses a shorter timeout than chat -
// a probe that takes 60s would pin the tier-upgrade worker for that long if
// the endpoint is hung. 10s keeps it snappy without being so tight that a
// slow-but-alive endpoint gets a false negative.
builder.Services.AddHttpClient<IModelAvailabilityProbe, HttpModelAvailabilityProbe>((sp, client) =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
    .UseSocketsHttpHandler((handler, _) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

// Process-wide domain metrics. Owns thread-safe counters for fallback
// strategy usage, model-tier transitions, and chat-latency histogram
// buckets. Injected into PalLlmRuntime + ModelTierOrchestrator so those
// record at their respective call sites. Rendered as labeled Prometheus
// counters + histogram by PrometheusExporter.
builder.Services.AddSingleton<PalLlmMetrics>();
builder.Services.AddSingleton<InferencePerformanceTracker>();

builder.Services.AddSingleton<ModelTierOrchestrator>(sp => new ModelTierOrchestrator(
    sp.GetRequiredService<PalLlmOptions>(),
    sp.GetRequiredService<IModelAvailabilityProbe>(),
    sp.GetRequiredService<PalLlmMetrics>()));
builder.Services.AddSingleton<ModelCollaborationPlanner>();
builder.Services.AddSingleton<ModelCollaborationDecisionPlanner>();
// Local-first mesh role registry: reads PalLLM:ModelRoles[] and reports
// coverage across Edge / Worker / Judge / Media / Validator slots for the
// /api/roles endpoint, the pal_model_roles MCP tool, and the dashboard
// role panel. Metadata-only today; future passes may layer role-aware
// routing on top without changing the operator shape.
builder.Services.AddSingleton<ModelRoleRegistry>();
// Qwen Duo Mesh planner: deterministic router that turns a
// (task, risk, hardware, live role coverage) tuple into one of the ten
// cooperation patterns. Pure C#, no inference call, no external I/O.
builder.Services.AddSingleton<DuoOrchestratorPlanner>();

// Hard-code promotion ledger: records per-task-class observations so
// operators + AI callers can see which patterns are stable enough to
// promote from "AI-assisted workflow" into "deterministic product
// logic". In-memory bounded deque; see PromotionLedger for the
// conservative promotion criterion.
builder.Services.AddSingleton<PromotionLedger>();

// HttpJsonInferenceClient's ctor takes (HttpClient, PalLlmOptions,
// ModelTierOrchestrator?). The DI container picks the 3-arg ctor because
// both PalLlmOptions and ModelTierOrchestrator are registered as
// singletons above, so tier orchestration is wired automatically without
// an explicit factory.
builder.Services.AddHttpClient<IInferenceClient, HttpJsonInferenceClient>((sp, client) =>
{
    PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Inference.TimeoutSeconds));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
    .UseSocketsHttpHandler((handler, _) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

builder.Services.AddHttpClient<IVisionClient, HttpVisionClient>((sp, client) =>
{
    PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Vision.TimeoutSeconds));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
})
    .UseSocketsHttpHandler((handler, _) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

builder.Services.AddHttpClient<ITtsClient, HttpTtsClient>((sp, client) =>
{
    PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Tts.TimeoutSeconds));
})
    .UseSocketsHttpHandler((handler, _) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

builder.Services.AddHttpClient<IAudioTranscriptionClient, HttpAudioTranscriptionClient>((sp, client) =>
{
    PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Asr.TimeoutSeconds));
})
    .UseSocketsHttpHandler((handler, _) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

// Opt-in upstream MCP discovery should reuse pooled handlers just like the
// hot inference / vision / TTS clients do. The MCP SDK transport receives a
// factory-created HttpClient so periodic upstream probing does not churn
// sockets or create one-off handler graphs every refresh tick.
builder.Services.AddHttpClient(PalLLM.Sidecar.Mcp.McpUpstreamClientPool.HttpClientName, client =>
{
    client.Timeout = Timeout.InfiniteTimeSpan;
})
    .UseSocketsHttpHandler((handler, _) =>
    {
        handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
        handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
    })
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

builder.Services.AddSingleton<PalLlmRuntime>(sp => new PalLlmRuntime(
    sp.GetRequiredService<PalLlmOptions>(),
    sp.GetRequiredService<IInferenceClient>(),
    sp.GetRequiredService<IVisionClient>(),
    sp.GetRequiredService<ITtsClient>(),
    sp.GetRequiredService<PalLlmMetrics>(),
    sp.GetRequiredService<InferencePerformanceTracker>(),
    sp.GetRequiredService<IAudioTranscriptionClient>()));
if (!isOpenApiBuild)
{
    builder.Services.AddHostedService<BridgeInboxWorker>();
    builder.Services.AddHostedService<InferenceWarmupWorker>();
    builder.Services.AddHostedService<ScreenshotWatcher>();
    builder.Services.AddHostedService<SessionAutosaveWorker>();
    builder.Services.AddHostedService<ModelTierUpgradeWorker>();
    // Conservative watchdog: archives orphan envelopes + writes audit
    // evidence + logs when operator-health drops below the unhealthy
    // floor. Never restarts the sidecar or resets the circuit breaker.
    builder.Services.AddHostedService<SelfHealingWorker>();
    // Auto-feeder: turns PalLlmMetrics fallback-strategy delta increments
    // into PromotionLedger observations on a cadence. Opt-in (default on)
    // via PalLlmOptions.PromotionFeeder; pure observer pattern.
    builder.Services.AddHostedService<PromotionLedgerFeeder>();
}

// PalLLM-as-MCP-client: discovery-only pool of external MCP servers.
// Configure `PalLLM:McpClient:UpstreamServers[]` with `{ Id, Url, BearerToken?, Enabled }`
// entries to have the sidecar probe each on startup and every
// DiscoveryIntervalSeconds (default 300s). The pool caches the
// discovered tools/resources/prompts for readers; v1 is read-only -
// PalLLM does NOT automatically proxy tool calls to upstreams.
builder.Services.AddSingleton<PalLLM.Sidecar.Mcp.McpUpstreamClientPool>();
if (!isOpenApiBuild)
{
    builder.Services.AddHostedService<McpUpstreamDiscoveryWorker>();
}

// Model Context Protocol server over Streamable HTTP. Exposes PalLLM's
// runtime as a full MCP surface - all three primitives:
//   * Tools (model-controlled actions)  -> PalLlmMcpTools
//   * Resources (passive context data)  -> PalLlmMcpResources
//   * Prompts (user-controlled templates) -> PalLlmMcpPrompts
// to any MCP-aware agent (Claude Desktop, VS Code, Cursor, ChatGPT,
// custom clients). Stateless mode - each JSON-RPC call is independent,
// so a fresh MCP client connects without resumption state. Attribute-
// based registration means every new [McpServerTool]/[McpServerResource]/
// [McpServerPrompt] method is picked up automatically; no central
// registration list to keep in sync.
builder.Services.AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.Stateless = true;
    })
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

builder.Services.AddHealthChecks()
    .AddCheck<LivenessHealthCheck>("liveness", tags: ["live"])
    .AddCheck<ReadinessHealthCheck>("readiness", tags: ["ready"])
    .AddCheck<InferencePerformanceReadinessHealthCheck>("inference_recent_window", tags: ["ready"]);

// .NET 10 native OpenAPI 3.1 document generation. The route registrations
// below are the source of truth; the document is regenerated on each request
// to GET /openapi/v1.json so it can never drift from the actual routes.
builder.Services.AddOpenApi(options =>
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
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    string serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "pal-llm-sidecar";
    string? serviceVersion = typeof(PalLlmRuntime).Assembly.GetName().Version?.ToString();
    static bool ShouldObserveHttpRequest(HttpContext context) =>
        !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        && !context.Request.Path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);

    builder.Services.AddOpenTelemetry()
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

    builder.Services.AddSingleton<PalLlmOperationalTelemetry>();
}

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    _ = app.Services.GetRequiredService<PalLlmOperationalTelemetry>();
}

// Pass 354: production-safety guard. Refuse to boot the sidecar when
// it would bind a non-loopback interface with auth disabled, and warn
// loudly when auth is disabled on a loopback bind. The default
// `PalLLM:Auth:ApiKey = null` posture is fine for localhost-only dev
// but a footgun the moment an operator sets `ASPNETCORE_URLS` to
// 0.0.0.0 / a LAN IP / a public hostname without first setting the
// key. See docs/SECURITY.md and src/PalLLM.Domain/Configuration/StartupAuthGuard.cs.
//
// The `app.Urls` access is wrapped because the dotnet-getdocument
// OpenAPI snapshot tool invokes Main() against a stub host without
// the IServerAddressesFeature, which throws. In that path the guard
// is skipped (the tool doesn't actually serve traffic).
{
    PalLlmOptions guardOptions = app.Services.GetRequiredService<PalLlmOptions>();
    IReadOnlyList<string>? bindUrls = null;
    try
    {
        bindUrls = app.Urls.ToList();
    }
    catch (InvalidOperationException)
    {
        // IServerAddressesFeature not present (e.g. dotnet-getdocument
        // build-time invocation). The guard has nothing to inspect and
        // the process won't serve traffic — skip silently.
    }
    if (bindUrls is not null)
    {
        StartupAuthGuard.Result authVerdict = StartupAuthGuard.Inspect(bindUrls, guardOptions.Auth.ApiKey);
        ILogger startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PalLLM.Startup");
        switch (authVerdict.Action)
        {
            case StartupAuthGuard.Verdict.Fail:
                startupLogger.LogCritical(
                    "PalLLM startup refused: {Reason} Remediation: {Remediation}",
                    authVerdict.Reason,
                    authVerdict.RemediationHint);
                throw new InvalidOperationException(
                    $"PalLLM refused to start: {authVerdict.Reason} {authVerdict.RemediationHint}");
            case StartupAuthGuard.Verdict.Warn:
                startupLogger.LogWarning(
                    "PalLLM startup auth posture: {Reason} {Remediation}",
                    authVerdict.Reason,
                    authVerdict.RemediationHint);
                break;
            // Pass: no log line needed.
        }
    }
}

app.UseResponseCompression();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.TryAdd("Content-Security-Policy", "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'; object-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'");
        headers.TryAdd("Permissions-Policy", "camera=(), geolocation=(), microphone=(), payment=(), usb=()");
        headers.TryAdd("Referrer-Policy", "no-referrer");
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("X-Frame-Options", "DENY");
        return Task.CompletedTask;
    });

    await next();
});
app.Use(async (context, next) =>
{
    if (!ShouldDisableCaching(context.Request.Path))
    {
        await next();
        return;
    }

    context.Response.OnStarting(() =>
    {
        context.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
        return Task.CompletedTask;
    });

    await next();
});
// Fail fast on oversized API/MCP JSON bodies before minimal-API model binding
// allocates for large text, base64 media, or JSON-RPC payloads. Kestrel enforces
// the feature for streamed bodies; the Content-Length check gives deterministic
// 413 ProblemDetails in the in-process test host and for honest clients.
app.Use(async (context, next) =>
{
    if (!ShouldLimitRequestBody(context.Request))
    {
        await next();
        return;
    }

    PalLlmOptions palOptions = context.RequestServices.GetRequiredService<PalLlmOptions>();
    long maxBytes = Math.Max(1_024, palOptions.Http.ApiRequestBodyMaxBytes);
    HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(context.Request, maxBytes);

    if (context.Request.ContentLength is long contentLength && contentLength > maxBytes)
    {
        await BuildRequestBodyPayloadTooLargeResult(maxBytes).ExecuteAsync(context);
        return;
    }

    await next();
});
// MCP Streamable HTTP origin validation. The current MCP transport spec
// requires servers to reject invalid browser origins to prevent DNS
// rebinding attacks against localhost-bound MCP endpoints. Non-browser
// desktop clients typically omit Origin entirely, which remains allowed.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
    {
        AuthOptions auth = context.RequestServices.GetRequiredService<PalLlmOptions>().Auth;
        if (!PalLLM.Sidecar.Mcp.McpOriginPolicy.TryValidate(context.Request, auth, out string? detail))
        {
            await TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Forbidden Origin",
                    detail: detail)
                .ExecuteAsync(context);
            return;
        }
    }

    await next();
});
// Optional bearer-token guard for /api/*. Only kicks in when
// PalLLM:Auth:ApiKey is set, so localhost-only deployments stay open and
// backwards compatible. Metrics, health, and openapi surfaces stay open by
// default so monitoring and contract discovery don't need a credential;
// flip PalLLM:Auth:ProtectMetrics / ProtectHealth when exposing the sidecar
// to an untrusted network. Static dashboard root is always open - it's
// inert HTML/CSS/JS with no data access of its own.
app.Use(async (context, next) =>
{
    static Task WriteUnauthorizedAsync(HttpContext httpContext, string detail)
    {
        httpContext.Response.Headers.WWWAuthenticate = "Bearer";
        return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Unauthorized",
                detail: detail)
            .ExecuteAsync(httpContext);
    }

    PalLlmOptions palOptions = context.RequestServices.GetRequiredService<PalLlmOptions>();
    string? configuredKey = palOptions.Auth.ApiKey;
    if (string.IsNullOrEmpty(configuredKey))
    {
        await next();
        return;
    }

    PathString path = context.Request.Path;
    bool requiresAuth =
        path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase)
        || (palOptions.Auth.ProtectMetrics && path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase))
        || (palOptions.Auth.ProtectHealth && path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase));
    if (!requiresAuth)
    {
        await next();
        return;
    }

    string? header = context.Request.Headers.Authorization;
    const string bearerPrefix = "Bearer ";
    if (string.IsNullOrEmpty(header) || !header.StartsWith(bearerPrefix, StringComparison.Ordinal))
    {
        await WriteUnauthorizedAsync(context, "Missing bearer credential.");
        return;
    }

    string presented = header[bearerPrefix.Length..].Trim();
    // Constant-time comparison to avoid timing-side-channel attacks where
    // an attacker could infer the correct key prefix by timing responses.
    if (!CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(presented),
            System.Text.Encoding.UTF8.GetBytes(configuredKey)))
    {
        await WriteUnauthorizedAsync(context, "Invalid bearer credential.");
        return;
    }

    await next();
});

// OpenAPI document endpoint. Served at /openapi/v1.json for integrators and
// SDK generators. Kept outside the /api prefix because it describes the API
// rather than being part of it - same reasoning that puts /metrics and
// /health/* outside /api.
app.UseOutputCache();
app.UseRequestTimeouts();
app.UseRateLimiter();

// Endpoint-routed static assets follow the current ASP.NET Core guidance for
// build-produced assets and let the routing layer short-circuit CSS/JS/image
// requests before they flow through the rest of the endpoint pipeline.
//
// Single-file self-extracting bundles (PublishSingleFile=true) leave the
// staticwebassets endpoints manifest next to the EXE but never copy it into
// AppContext.BaseDirectory (the per-launch extraction temp dir), so
// MapStaticAssets()'s default lookup throws InvalidOperationException on
// startup. When the manifest is visible next to the running executable, hand
// the explicit path to the static-asset router; otherwise fall through to
// the default AppContext.BaseDirectory lookup that works for `dotnet run`,
// tests, and framework-dependent publishes.
string? staticAssetsManifestPath = null;
string? exeDirectory = Path.GetDirectoryName(Environment.ProcessPath);
if (!string.IsNullOrEmpty(exeDirectory))
{
    string candidateManifest = Path.Combine(
        exeDirectory,
        $"{app.Environment.ApplicationName}.staticwebassets.endpoints.json");
    if (File.Exists(candidateManifest))
    {
        staticAssetsManifestPath = candidateManifest;
    }
}
app.MapStaticAssets(staticAssetsManifestPath).ShortCircuit();
app.MapGet("/", IResult (IWebHostEnvironment environment) =>
{
    string? webRootPath = environment.WebRootPath;
    if (string.IsNullOrWhiteSpace(webRootPath))
    {
        return TypedResults.NotFound();
    }

    string indexPath = Path.Combine(webRootPath, "index.html");
    return File.Exists(indexPath)
        ? TypedResults.PhysicalFile(indexPath, "text/html; charset=utf-8")
        : TypedResults.NotFound();
})
    .ExcludeFromDescription();

var openApiJson = app.MapOpenApi();
var openApiYaml = app.MapOpenApi("/openapi/{documentName}.yaml");
if (httpOptions.OpenApiCacheMinutes > 0)
{
    openApiJson.CacheOutput("openapi-doc");
    openApiYaml.CacheOutput("openapi-doc");
}

// Model Context Protocol endpoint. Rooted at /mcp per the MCP spec's
// Streamable HTTP convention - clients POST JSON-RPC 2.0 messages here
// and the server streams back responses. The auth middleware above
// already protects /mcp/* when PalLLM:Auth:ApiKey is set, matching the
// protection model for /api/*. Claude Desktop, VS Code, Cursor, and
// other MCP hosts can be pointed at http://host:5088/mcp to reach this
// server. See docs/OPERATIONS.md Sec. "Exposing PalLLM via MCP" for the
// connection setup walk-through.
app.MapMcp("/mcp");

RouteGroupBuilder api = app.MapGroup("/api");

api.MapGet("/health", (PalLlmRuntime runtime) => TypedResults.Ok(runtime.GetHealth()))
    .WithName("GetRuntimeHealth")
    .WithTags("Inspection")
    .WithSummary("Get the current runtime health snapshot.")
    .Produces<RuntimeHealth>(StatusCodes.Status200OK);

api.MapGet("/dashboard", IResult (HttpContext context, PalLlmRuntime runtime) =>
{
    DashboardSnapshot dashboard = runtime.GetDashboardSnapshot();
    string etag = ConditionalHttp.CreateStrongEtag(DashboardEtagPayload.From(dashboard));

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

// Prometheus scrape target. Lives under /metrics (no /api prefix) to match the
// convention Prometheus, Grafana Agent, and OTel collectors expect.
app.MapGet("/metrics", (PalLlmRuntime runtime) =>
    Results.Text(
        PrometheusExporter.Render(
            runtime.GetHealth(),
            runtime.GetInferencePerformanceSnapshot()),
        "text/plain; version=0.0.4"));

// Standard K8s / cloud health endpoints. /health/live for liveness, /health/ready
// for readiness. Both return aggregated status + per-check data as JSON.
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
    string etag = ConditionalHttp.CreateStrongEtag(features);
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

// AI-friendly one-shot self-description. An MCP client or custom LLM caller
// can GET this on connect and learn what the running sidecar is, what it can
// do right now, and which other endpoints/tools to reach for next — without
// scraping docs or multiple round-trips.
RouteHandlerBuilder selfDescriptionEndpoint = api.MapGet("/describe", IResult (
    HttpContext context,
    PalLlmRuntime runtime,
    PalLlmOptions options,
    EndpointDataSource endpointDataSource) =>
{
    SelfDescription description = SelfDescriptionBuilder.Build(runtime, options, endpointDataSource);
    string etag = ConditionalHttp.CreateStrongEtag(description);
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

// Dynamic "what should I do next?" guidance. Where /api/describe is the
// static manifest, /api/quickstart is state-aware: it reads the live
// RuntimeHealth + options and returns ordered critical/recommended/optional
// steps with label/why/action/verify for each. Both human operators and AI
// assistants can call this once to know exactly what to do next without
// scraping the dashboard or reading the opt-in matrix.
api.MapGet("/quickstart", IResult (
    HttpContext context,
    PalLlmRuntime runtime,
    PalLlmOptions options,
    ModelRoleRegistry roleRegistry) =>
{
    QuickstartGuide guide = QuickstartGuideBuilder.Build(runtime, options, roleRegistry);
    // No caching headers — the guide is derived from live health state and
    // is meant to be re-read any time the operator asks "am I done yet?".
    return TypedResults.Ok(guide);
})
    .WithName("GetQuickstartGuide")
    .WithTags("Inspection")
    .WithSummary("Live state-aware next-step guidance for humans + AI.");

// Self-healing watchdog status: the latest evidence artifact written by
// SelfHealingWorker, or a structured pending marker if the worker has not
// ticked yet. Used by the dashboard chip + the pal_self_healing_status MCP
// tool so every consumer sees the same payload contract.
api.MapGet("/self-healing/status", IResult (
    HttpContext context,
    PalLlmOptions options) =>
{
    using JsonDocument doc = SelfHealingStatusReader.Read(options);
    // Return the parsed JsonElement so the minimal-API pipeline serialises it
    // with the same PascalCase posture the rest of the sidecar uses.
    return TypedResults.Content(doc.RootElement.GetRawText(), "application/json");
})
    .WithName("GetSelfHealingStatus")
    .WithTags("Inspection")
    .WithSummary("Latest SelfHealingWorker evidence or a pending marker when the watchdog has not ticked yet.");

// Publication-facing air-gap posture check. Classifies every outbound surface
// (inference, vision, TTS, OTLP, MCP upstreams) as loopback/private/public/
// disabled without opening a TCP connection or emitting a single live
// request. Answers "will this sidecar make any network call off this
// machine under the current config?" in one shot.
api.MapGet("/airgap/verify", IResult (
    HttpContext context,
    PalLlmOptions options) =>
{
    AirGapReport report = AirGapVerifier.VerifyCached(options);
    return TypedResults.Ok(report);
})
    .WithName("GetAirGapReport")
    .WithTags("Inspection")
    .WithSummary("Classify every outbound surface so operators + AI can prove air-gap posture.");

// Local-first mesh role coverage. Reports which of the five roles
// (Edge / Worker / Judge / Media / Validator) the operator has bound to
// local endpoints, which are missing, and what a good pairing looks like
// for the current setup. Metadata-only today: binding a role does not
// automatically route inference traffic, but it makes the mesh
// architecture legible to operators and AI clients.
api.MapGet("/roles", IResult (ModelRoleRegistry registry) =>
{
    ModelRoleCoverage coverage = registry.GetCoverage();
    return TypedResults.Ok(coverage);
})
    .WithName("GetModelRoleCoverage")
    .WithTags("Inspection")
    .WithSummary("Coverage of the Edge / Worker / Judge / Media / Validator roles in the local-first AI mesh.");

// Pass 25 / D1 — machine-readable hardware posture. Inspects the OS,
// core count, RAM, and GPU markers and derives the recommended
// DuoHardwareTier. Operators can force a tier via
// PalLLM:Hardware:ForceTier; the override surfaces on the profile so
// /api/describe and the dashboard can show both the detected and the
// forced tier. Deterministic — no inference call, no subprocess, no
// network. Safe to call on hot paths.
api.MapGet("/hardware", IResult (PalLlmOptions options) =>
{
    HardwareProfile profile = HardwareProfiler.CaptureCached(options.Hardware.ForceTier);
    return TypedResults.Ok(profile);
})
    .WithName("GetHardwareProfile")
    .WithTags("Inspection")
    .WithSummary("Detected hardware posture: CPU cores, RAM, GPU-likelihood, and recommended DuoHardwareTier. Honours PalLLM:Hardware:ForceTier override.");

// Pass 33 / D2 — graceful-degradation advisory. Inspects the current
// HardwareProfile + PalLlmOptions and recommends a posture for boxes
// that cannot comfortably run the full inference / vision / TTS
// pipeline. Covers "my laptop has no GPU, can I still play?" —
// deterministic director + small Edge model stay available, vision
// + TTS are recommended off, and the active model lane is nudged
// toward the smallest available tier. Pure advisory — the endpoint
// never mutates options itself.
api.MapGet("/degradation/advisory", IResult (PalLlmOptions options) =>
{
    HardwareProfile profile = HardwareProfiler.CaptureCached(options.Hardware.ForceTier);
    DegradationAdvisory advisory = GracefulDegradationAdvisor.Recommend(profile, options);
    return TypedResults.Ok(advisory);
})
    .WithName("GetDegradationAdvisory")
    .WithTags("Inspection")
    .WithSummary("Advisory posture for the current hardware + options: CPU-only deterministic-first, entry-GPU worker-only, full-mesh no-degradation. Never mutates runtime state.");

// Pass 35 / D10 — resource budget posture. Enumerates every tracked
// runtime budget (inference rate, circuit breaker, vision queue, TTS
// caps, memory window, bridge retention, chat fallback share) and
// classifies each as ok / review / exhausted with a plain-English
// recommendation. Pure advisory — never mutates counters.
api.MapGet("/budgets", IResult (PalLlmOptions options, PalLlmMetrics metrics) =>
{
    ResourceBudgetMetrics derived = ResourceBudgetMetrics.FromSnapshot(metrics.Snapshot());
    ResourceBudgetPosture posture = ResourceBudgetPostureBuilder.CaptureCached(options, derived);
    return TypedResults.Ok(posture);
})
    .WithName("GetResourceBudgetPosture")
    .WithTags("Inspection")
    .WithSummary("Resource-budget posture per feature (inference rate, vision queue, TTS caps, memory window, bridge retention, fallback share) with ok / review / exhausted bucketing.");

// Pass 36 / C2 — world-narration advisor. Deterministic decision on
// whether the current scene warrants a companion's one-line quip.
// Triggers on combat-start, threat-spike, night-fall, weather-change,
// low-health, objective-update. Rate-limited by caller — the advisor
// returns the minimum gap it expects, so a narrator worker can drop
// cues that arrive too quickly. Pure function over the world snapshot.
api.MapGet("/narration/cue", IResult (PalLlmRuntime runtime) =>
{
    NarrationCue cue = WorldNarrationAdvisor.Advise(runtime.Adapter.Snapshot, lastNarrationUtc: null);
    return TypedResults.Ok(cue);
})
    .WithName("GetNarrationCue")
    .WithTags("Inspection")
    .WithSummary("Should the companion narrate right now? Deterministic decision from the current world snapshot. Never calls inference.");

// Pass 38 / C10 — mood weather forecast per character. Blends the
// RelationshipTracker's CharacterRelationship record with the current
// world snapshot (threat, player health, time-of-day) to produce a
// short mood/weather/tone triple the dashboard can render and the
// chat prompt can include. Deterministic — no inference call.
// Pass 40 / C8 — lifetime-relationship summary across every
// observed session. Reads the aggregate persisted under
// Runtime/LifetimeRelationships/latest.json (or returns an empty
// aggregate if the file doesn't exist yet) and emits a life-story
// summary per tracked character. Pure read-only inspection.
api.MapGet("/relationships/lifetime", IResult (PalLlmRuntime runtime, PalLlmOptions options) =>
{
    string saveRoot = string.IsNullOrWhiteSpace(options.PalSavedRoot)
        ? AppContext.BaseDirectory
        : options.PalSavedRoot!;
    string path = Path.Combine(saveRoot, "Runtime", "LifetimeRelationships", "latest.json");
    LifetimeRelationshipAggregate aggregate = LifetimeRelationshipAggregator.Empty();
    if (File.Exists(path))
    {
        BoundedJsonFileReader.JsonReadResult<LifetimeRelationshipAggregate> readResult =
            BoundedJsonFileReader.TryRead(
                path,
                options.Http.LocalArtifactMaxBytes,
                LifetimeRelationshipAggregator.Deserialize);
        if (readResult.Succeeded && readResult.Value is not null)
        {
            aggregate = readResult.Value;
        }
    }
    var summaries = aggregate.Characters
        .Select(LifetimeRelationshipAggregator.Summarise)
        .ToArray();
    return TypedResults.Ok(new
    {
        Aggregate = aggregate,
        Summaries = summaries,
    });
})
    .WithName("GetLifetimeRelationships")
    .WithTags("Relationships")
    .WithSummary("Cross-session lifetime summary for every tracked character. Reads the persisted aggregate under Runtime/LifetimeRelationships/latest.json with bounded local JSON ingress.");

api.MapGet("/characters/{characterId:int}/mood", IResult (int characterId, PalLlmRuntime runtime) =>
{
    CharacterRelationship? rel = runtime.GetRelationships()
        .FirstOrDefault(r => r.CharacterId == characterId);
    if (rel is null)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Character not tracked",
            detail: $"No relationship record for character id {characterId}. Chat at least once to create one.");
    }
    MoodWeather forecast = MoodWeatherAdvisor.Forecast(rel, runtime.Adapter.Snapshot);
    return Results.Ok(forecast);
})
    .WithName("GetCharacterMoodWeather")
    .WithTags("Relationships")
    .WithSummary("Deterministic mood-weather forecast for a tracked character, blended from relationship + world snapshot.");

// Pass 27 / E3 — machine-readable privacy posture. Enumerates every
// data-emitting surface PalLLM ships and classifies each as
// "never-leaves", "only-with-opt-in", or "leaves-by-default" so
// operators + AI agents can answer "what does this install actually
// transmit?" without running a packet capture. Pairs with
// /api/airgap/verify (network-scope view) to give a complete
// privacy picture. Deterministic — no inference call.
api.MapGet("/privacy/posture", IResult (PalLlmOptions options) =>
{
    PrivacyPosture posture = PrivacyPostureBuilder.CaptureCached(options);
    return TypedResults.Ok(posture);
})
    .WithName("GetPrivacyPosture")
    .WithTags("Inspection")
    .WithSummary("Enumerate every data-emitting surface and classify it as never-leaves / only-with-opt-in / leaves-by-default. Pairs with /api/airgap/verify.");

// Pass 31 / C3 — deterministic directive translator. Converts a
// natural-language player utterance into an ordered plan of
// allowlisted pal directives the UE4SS mod can forward to the native
// pal-AI controller. Never emits above AutomationOptions.AllowedActions
// — if nothing matches the allowlist, returns an empty plan with a
// plain-English reason. Deterministic — no inference call.
// Pass 34 / C1 — party chat. Fans out a single utterance across
// multiple character ids in order. Each per-character turn runs
// through the existing ChatAsync machinery (so the task-aware
// execution profile, Pass-8 planner, rate limiting, and deterministic
// fallback all apply per-turn). Threaded mode seeds each turn with a
// brief mention of earlier replies so a conversation forms; default
// off so each character replies independently.
api.MapPost("/chat/party", async (
    PartyChatRequest request,
    PalLlmRuntime runtime,
    CancellationToken cancellationToken) =>
{
    PartyChatRequest r = request ?? new PartyChatRequest();
    if (r.CharacterIds is null || r.CharacterIds.Count == 0)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Party chat requires at least one CharacterId",
            detail: "Send a non-empty CharacterIds array so the dispatcher knows which companions to fan out across.");
    }
    if (string.IsNullOrWhiteSpace(r.UserMessage))
    {
        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "UserMessage is required",
            detail: "Party chat must carry a non-empty UserMessage just like /api/chat.");
    }

    string partyId = "party-" + Guid.NewGuid().ToString("N")[..12];
    var turns = new List<PartyChatTurn>(r.CharacterIds.Count);
    var earlierSummary = new System.Text.StringBuilder();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    int fallbackCount = 0;

    for (int i = 0; i < r.CharacterIds.Count; i++)
    {
        int cid = r.CharacterIds[i];
        string? cname = (r.CharacterNames is not null && r.CharacterNames.Count > i)
            ? r.CharacterNames[i]
            : null;

        string perTurnMessage = r.UserMessage;
        if (r.Threaded && earlierSummary.Length > 0)
        {
            perTurnMessage = r.UserMessage
                + "\n\n[Party context — earlier replies this turn]\n"
                + earlierSummary;
        }

        var turnReq = new ChatRequest
        {
            CharacterId = cid,
            CharacterName = cname,
            TaskTag = r.TaskTag,
            UserMessage = perTurnMessage,
            Temperature = r.Temperature,
            RequestId = partyId + "-" + i,
        };
        ChatResponse turnResp = await runtime.ChatAsync(turnReq, cancellationToken).ConfigureAwait(false);

        turns.Add(new PartyChatTurn(
            OrderIndex: i,
            CharacterId: cid,
            CharacterName: string.IsNullOrWhiteSpace(cname) ? turnResp.CharacterName : cname,
            Response: turnResp));

        if (turnResp.UsedFallback) { fallbackCount++; }

        if (r.Threaded)
        {
            string who = string.IsNullOrWhiteSpace(turnResp.CharacterName) ? $"#{cid}" : turnResp.CharacterName;
            string reply = turnResp.AssistantMessage ?? string.Empty;
            if (reply.Length > 240) { reply = reply[..240] + "…"; }
            earlierSummary.Append(who).Append(": ").AppendLine(reply);
        }
    }

    sw.Stop();

    var response = new PartyChatResponse
    {
        PartyId = partyId,
        Turns = turns,
        Threaded = r.Threaded,
        TotalLatencyMs = sw.ElapsedMilliseconds,
        FallbackTurnCount = fallbackCount,
        CapturedAtUtc = DateTimeOffset.UtcNow,
    };
    return Results.Ok(response);
})
    .ValidatePalRequest<PartyChatRequest>()
    .RequireRateLimiting("chat-heavy")
    .WithName("PostChatParty")
    .WithTags("Conversation")
    .WithSummary("Pass 34 – fan a single utterance out across multiple character ids. Each per-character reply runs through the full ChatAsync pipeline.")
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .WithRequestTimeout("chat-timeout");

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

// Promotion-ledger observations: operators + AI callers post one
// observation per task-class run (success / disagreement-block /
// validator-fail / human-override) and read back per-task summaries
// that flag which patterns are stable enough to hard-code into
// deterministic logic. Deterministic + in-memory + bounded per task.
api.MapPost("/promotion/record", IResult (
    PromotionRecordRequest request,
    PromotionLedger ledger) =>
{
    PromotionRecordRequest r = request ?? new PromotionRecordRequest();
    if (string.IsNullOrWhiteSpace(r.TaskClass))
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing task class",
            detail: "A non-empty TaskClass is required before an observation can be recorded.");
    }

    if (string.IsNullOrWhiteSpace(r.PatternId))
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing pattern id",
            detail: "A non-empty PatternId is required before an observation can be recorded.");
    }

    if (string.IsNullOrWhiteSpace(r.Outcome))
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing promotion outcome",
            detail: "A non-empty Outcome is required before an observation can be recorded.");
    }

    if (!PromotionLedger.TryNormalizeOutcome(r.Outcome, out _))
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Invalid promotion outcome",
            detail: $"Outcome must be one of: {string.Join(", ", PromotionLedger.AllowedOutcomeValues)}.");
    }

    PromotionObservation observation = ledger.Record(
        taskClass: r.TaskClass,
        patternId: r.PatternId,
        outcome: r.Outcome,
        note: r.Note);
    return TypedResults.Ok(observation);
})
    .WithName("PostPromotionObservation")
    .WithTags("Inspection")
    .WithSummary("Record one observation against the hard-code promotion ledger.");

api.MapGet("/promotion/summary", IResult (PromotionLedger ledger) =>
{
    PromotionSummary summary = ledger.Snapshot();
    return TypedResults.Ok(summary);
})
    .WithName("GetPromotionSummary")
    .WithTags("Inspection")
    .WithSummary("Per-task-class promotion summary; flags patterns stable enough to hard-code.");

// Actionable suggestions for every promotion candidate: concrete target
// files, one-sentence recipes, rollback paths, and a ProofPacket per
// suggestion so the recommendation itself has audit provenance.
api.MapGet("/promotion/suggestions", IResult (PromotionLedger ledger) =>
{
    PromotionSummary summary = ledger.Snapshot();
    PromotionSuggestionSet suggestions = PromotionSuggestionBuilder.Build(summary);
    return TypedResults.Ok(suggestions);
})
    .WithName("GetPromotionSuggestions")
    .WithTags("Inspection")
    .WithSummary("Hard-code suggestions for every promotion candidate: target file, suggested change, evidence summary, rollback path, ProofPacket.");

// Concrete, editor-ready change template for a specific candidate.
// Request body names a (TaskClass, PatternId) pair; the server looks
// up the matching candidate in the live ledger and returns a preview
// with file path, before-context anchors, after-code template,
// safety warnings, rollback command, and ProofPacket provenance.
// Deterministic — no file reads, no inference call.
api.MapPost("/promotion/apply/preview", IResult (
    PromotionApplyPreviewRequest request,
    PromotionLedger ledger) =>
{
    PromotionApplyPreviewRequest r = request ?? new PromotionApplyPreviewRequest();
    if (string.IsNullOrWhiteSpace(r.TaskClass))
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing task class",
            detail: "A non-empty TaskClass is required so the preview builder can find the matching candidate.");
    }

    PromotionSummary summary = ledger.Snapshot();
    PromotionTaskSummary? task = summary.Tasks.FirstOrDefault(t =>
        string.Equals(t.TaskClass, r.TaskClass, StringComparison.Ordinal));
    if (task is null)
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Task class not in ledger",
            detail: $"No observations recorded against task class '{r.TaskClass}'. Record at least one observation before requesting a preview.");
    }
    if (!task.IsPromotionCandidate)
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Task is not a promotion candidate",
            detail: task.Recommendation);
    }

    // If the caller supplied a specific PatternId, pin it; otherwise
    // use the task's most-common pattern as a reasonable default.
    string patternId = string.IsNullOrWhiteSpace(r.PatternId)
        ? (task.MostCommonPatternId ?? "(unspecified)")
        : r.PatternId.Trim();

    // Build the suggestion once and feed it into the preview builder.
    // Since the suggestion builder's ResolveTarget is keyed on task class,
    // we derive a synthesised task summary with the caller-pinned pattern
    // id so the downstream templates reference the right pattern.
    PromotionTaskSummary pinnedTask = task with { MostCommonPatternId = patternId };
    PromotionSuggestion suggestion = PromotionSuggestionBuilder.BuildForTask(pinnedTask, summary.CapturedAtUtc);
    PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(suggestion);
    return TypedResults.Ok(preview);
})
    .WithName("PostPromotionApplyPreview")
    .WithTags("Inspection")
    .WithSummary("Build an editor-ready change template for a specific promotion candidate.");

// Pass 24 — "apply" verb for the promotion pipeline. Persists the
// Pass-14 preview as a durable staging triple (template + rollback +
// provenance packet) under the configured staging root. NEVER
// mutates source code. Behind PalLLM:PromotionApply:AllowApply=false
// by default — flip only in environments where a human reviewer will
// cherry-pick the staged artifacts into real code. 403 when the flag
// is off; 404/409 for the same reasons as /apply/preview; 200 + the
// structured PromotionApplyResult otherwise.
api.MapPost("/promotion/apply", IResult (
    PromotionApplyRequest request,
    PromotionLedger ledger,
    PalLlmOptions options) =>
{
    PromotionApplyRequest r = request ?? new PromotionApplyRequest();
    if (string.IsNullOrWhiteSpace(r.TaskClass))
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Missing task class",
            detail: "A non-empty TaskClass is required so the apply verb can find the matching candidate.");
    }

    if (!options.PromotionApply.AllowApply)
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "Promotion apply is disabled",
            detail: "Set PalLLM:PromotionApply:AllowApply=true in configuration to allow the apply verb to persist staging artifacts. Apply never mutates source code in place — it only writes to the configured staging root.");
    }

    PromotionSummary applySummary = ledger.Snapshot();
    PromotionTaskSummary? applyTask = applySummary.Tasks.FirstOrDefault(t =>
        string.Equals(t.TaskClass, r.TaskClass, StringComparison.Ordinal));
    if (applyTask is null)
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "Task class not in ledger",
            detail: $"No observations recorded against task class '{r.TaskClass}'.");
    }
    if (!applyTask.IsPromotionCandidate)
    {
        return TypedResults.Problem(
            statusCode: StatusCodes.Status409Conflict,
            title: "Task is not a promotion candidate",
            detail: applyTask.Recommendation);
    }

    string applyPatternId = string.IsNullOrWhiteSpace(r.PatternId)
        ? (applyTask.MostCommonPatternId ?? "(unspecified)")
        : r.PatternId!.Trim();
    PromotionTaskSummary pinnedApply = applyTask with { MostCommonPatternId = applyPatternId };
    PromotionSuggestion applySuggestion = PromotionSuggestionBuilder.BuildForTask(pinnedApply, applySummary.CapturedAtUtc);
    PromotionApplyPreview applyPreview = PromotionApplyPreviewBuilder.Build(applySuggestion);

    PromotionApplyResult applyResult = PromotionApplier.Apply(applyPreview, options);
    return TypedResults.Ok(applyResult);
})
    .WithName("PostPromotionApply")
    .WithTags("Inspection")
    .WithSummary("Persist a promotion candidate as a staging-only template + rollback + provenance triple under the configured staging root. Never mutates source code. Gated by PalLLM:PromotionApply:AllowApply.");

RouteHandlerBuilder releaseReadinessEndpoint = api.MapGet("/release/readiness", IResult (
    HttpContext context,
    PalLlmRuntime runtime,
    EndpointDataSource endpointDataSource,
    PalLlmOptions options) =>
{
    ReleaseReadinessSnapshot snapshot = ReleaseReadinessBuilder.Create(runtime, endpointDataSource, options);
    string etag = ConditionalHttp.CreateStrongEtag(ReleaseReadinessEtagPayload.From(snapshot));
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

RouteHandlerBuilder bridgeProofEndpoint = api.MapGet("/bridge/proof", IResult (
    HttpContext context,
    PalLlmRuntime runtime) =>
{
    BridgeProofSnapshot snapshot = BridgeProofBuilder.Create(runtime);
    string etag = ConditionalHttp.CreateStrongEtag(BridgeProofEtagPayload.From(snapshot));
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

api.MapGet("/inference/performance", IResult (HttpContext context, PalLlmRuntime runtime) =>
{
    InferencePerformanceSnapshot snapshot = runtime.GetInferencePerformanceSnapshot();
    string etag = ConditionalHttp.CreateStrongEtag(InferencePerformanceEtagPayload.From(snapshot));

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

RouteHandlerBuilder upstreamMcpEndpoint = api.MapGet("/mcp/upstream", IResult (HttpContext context, PalLLM.Sidecar.Mcp.McpUpstreamClientPool pool) =>
{
    PalLLM.Sidecar.Mcp.UpstreamSnapshot[] snapshots = pool.GetSnapshots().Values
        .OrderBy(s => s.Id, StringComparer.Ordinal)
        .ToArray();
    string etag = ConditionalHttp.CreateStrongEtag(snapshots);
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

api.MapGet("/packs", (PalLlmRuntime runtime) => TypedResults.Ok(runtime.GetPacks().ToArray()))
    .WithName("ListNarrativePacks")
    .WithTags("Content")
    .WithSummary("List the narrative packs currently loaded by the runtime.");

// Pass 315 - per-species personality resolver. Operators map species
// (e.g. "Lamball") to a personality-pack id via
// PalLlmOptions:Packs:DefaultBySpecies; this route exposes the lookup
// so MCP clients and the bridge can ask "for this species, which pack
// would you use?" without re-implementing the priority logic. Pure
// deterministic lookup, no side effects.
api.MapGet("/packs/resolve", IResult (string? species, string? fallback, PalLlmOptions options) =>
{
    PalLLM.Domain.Packs.SpeciesPersonalityResolution resolution =
        PalLLM.Domain.Packs.SpeciesPersonalityResolver.Resolve(
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

api.MapPost("/chat", async (
    ChatRequest request,
    PalLlmRuntime runtime,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    // runtime.ChatAsync is designed to swallow inference / fallback / rate-limit
    // failures and return a ChatResponse with Success=false. Wrap it anyway so
    // an unexpected exception (OOM, JSON binding fault that escapes
    // ValidatePalRequest, domain-level ArgumentException) becomes a structured
    // Problem instead of an opaque ASP.NET 500. Parity with the streaming
    // sibling (/chat/stream) which already catches and emits a structured
    // error payload before closing the SSE stream.
    try
    {
        ChatResponse response = await runtime.ChatAsync(request, cancellationToken);
        return Results.Ok(response);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client disconnected. ASP.NET handles the response framing; rethrow
        // so the request-completed log line records the cancel rather than
        // a 500.
        throw;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Chat turn aborted before a reply could be returned.");
        return Results.Problem(
            title: "Chat turn failed.",
            detail: "The chat orchestration pipeline raised an unexpected internal error. Re-run the request; if the issue persists, check the sidecar log for the matching warning entry.",
            statusCode: StatusCodes.Status500InternalServerError);
    }
})
    .ValidatePalRequest<ChatRequest>()
    .RequireRateLimiting("chat-heavy")
    .WithName("PostChatTurn")
    .WithTags("Conversation")
    .WithSummary("Run a single chat turn through the PalLLM orchestration pipeline.")
    .Produces<ChatResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .ProducesProblem(StatusCodes.Status500InternalServerError)
    .WithRequestTimeout("chat-timeout");

// Streaming variant of /chat. Emits Server-Sent Events so a web UI or AI
// client sees progress phases (`started` -> `phase` -> ... -> `final`)
// instead of a silent wait before the final payload arrives. The final
// event always carries the complete ChatResponse JSON, so a client that
// only cares about the final answer can still consume this endpoint and
// ignore the intermediate phases. /api/chat stays unchanged for clients
// that prefer a single synchronous request.
api.MapPost("/chat/stream", async (
    ChatRequest request,
    PalLlmRuntime runtime,
    PalLlmOptions options,
    HttpContext context,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.Headers["Content-Type"] = "text/event-stream";
    context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
    context.Response.Headers["X-Accel-Buffering"] = "no"; // Caddy / nginx / reverse-proxy hint.

    string requestId = Guid.NewGuid().ToString("N")[..12];
    await ChatStreamWriter.EmitAsync(
        context.Response,
        "started",
        new ChatStreamStartedPayload(requestId),
        PalLlmJsonSerializerContext.Default.ChatStreamStartedPayload,
        cancellationToken);

    ChatResponse? response = null;
    int timeoutSeconds = Math.Max(1, options.Http.ChatRequestTimeoutSeconds);
    using var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    streamTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
    try
    {
        await ChatStreamWriter.EmitAsync(
            context.Response,
            "phase",
            new ChatStreamPhasePayload("ingress", "received request"),
            PalLlmJsonSerializerContext.Default.ChatStreamPhasePayload,
            cancellationToken);
        await ChatStreamWriter.EmitAsync(
            context.Response,
            "phase",
            new ChatStreamPhasePayload("orchestration", "routing through PalLlmRuntime"),
            PalLlmJsonSerializerContext.Default.ChatStreamPhasePayload,
            cancellationToken);

        response = await runtime.ChatAsync(request, streamTimeout.Token);

        // Pass 23: emit structured per-channel events BEFORE the final
        // synchronous payload so streaming clients can render reply
        // text, presentation cues, speech, and action intent as they
        // arrive instead of re-parsing the full ChatResponse. Clients
        // that only care about the final answer still receive the
        // complete ChatResponse as the trailing `final` event.
        await ChatStreamWriter.EmitAsync(
            context.Response,
            "phase",
            new ChatStreamFinalPrepPayload(
                "final-prep",
                response.UsedFallback
                ? $"fallback strategy '{response.FallbackStrategy}' produced the reply"
                : "live inference produced the reply",
                response.InferredTaskKind,
                response.CooperationPattern,
                response.DispatchMode,
                response.DispatchedRoleChain),
            PalLlmJsonSerializerContext.Default.ChatStreamFinalPrepPayload,
            cancellationToken);

        // Token-level event framing. Real upstream-token SSE requires
        // the inference provider to support it; Pass 23 emits word
        // chunks from the completed reply at a cadence fast enough to
        // feel incremental in the dashboard + any MCP-aware client.
        // When the provider itself streams (future pass), the
        // ChatResponse-side API can stay identical while the stream
        // path swaps the chunking source.
        string replyText = response.AssistantMessage ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(replyText))
        {
            string[] words = replyText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int emitted = 0;
            foreach (string word in words)
            {
                emitted++;
                await ChatStreamWriter.EmitAsync(
                    context.Response,
                    "token",
                    new ChatStreamTokenPayload(
                        emitted,
                        words.Length,
                        word + (emitted == words.Length ? string.Empty : " ")),
                    PalLlmJsonSerializerContext.Default.ChatStreamTokenPayload,
                    cancellationToken);
                // Tiny yield so the event arrives even if the consumer
                // is reading with no buffering; skipped on cancellation.
                if (emitted % 6 == 0 && !cancellationToken.IsCancellationRequested)
                {
                    await context.Response.Body.FlushAsync(cancellationToken);
                }
            }
        }

        if (response.Presentation is not null)
        {
            await ChatStreamWriter.EmitAsync(
                context.Response,
                "presentation",
                response.Presentation,
                PalLlmJsonSerializerContext.Default.PresentationCuePlan,
                cancellationToken);
        }
        if (response.Speech is not null)
        {
            await ChatStreamWriter.EmitAsync(
                context.Response,
                "speech",
                response.Speech,
                PalLlmJsonSerializerContext.Default.SpeechArtifact,
                cancellationToken);
        }
        if (response.Action is not null)
        {
            await ChatStreamWriter.EmitAsync(
                context.Response,
                "action",
                response.Action,
                PalLlmJsonSerializerContext.Default.ActionIntent,
                cancellationToken);
        }

        await ChatStreamWriter.EmitAsync(
            context.Response,
            "final",
            response,
            PalLlmJsonSerializerContext.Default.ChatResponse,
            cancellationToken);
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        // Client disconnected. Nothing more to emit.
    }
    catch (OperationCanceledException) when (streamTimeout.IsCancellationRequested)
    {
        logger.LogWarning(
            "Chat stream {RequestId} exceeded PalLLM:Http:ChatRequestTimeoutSeconds ({TimeoutSeconds}s) before the final reply was ready.",
            requestId,
            timeoutSeconds);
        await ChatStreamWriter.EmitAsync(
            context.Response,
            "error",
            new ChatStreamErrorPayload(
                requestId,
                "Chat stream exceeded its configured timeout before the final reply was ready.",
                true,
                "request_timeout"),
            PalLlmJsonSerializerContext.Default.ChatStreamErrorPayload,
            cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Chat stream {RequestId} aborted before the final reply was ready.", requestId);
        await ChatStreamWriter.EmitAsync(
            context.Response,
            "error",
            new ChatStreamErrorPayload(
                requestId,
                "Chat stream aborted before the final reply was ready.",
                true,
                "internal_error"),
            PalLlmJsonSerializerContext.Default.ChatStreamErrorPayload,
            cancellationToken);
    }
})
    .ValidatePalRequest<ChatRequest>()
    .RequireRateLimiting("chat-heavy")
    .WithName("PostChatTurnStreaming")
    .WithTags("Conversation")
    .WithSummary("Server-Sent-Events variant of /api/chat. Emits 'started' / 'phase' / 'final' (or 'error') events so clients see progress before the final answer lands.");

// Advisory: how would the Qwen Duo planner route THIS specific chat
// request? Infers the DuoTaskKind from the user message (keyword
// classifier), then calls the Pass-8 planner with the operator's
// risk + hardware preference. Deterministic, no inference call, no
// runtime mutation — pure "what pattern would be picked" forecast.
api.MapPost("/chat/plan", IResult (ChatPlanRequest request, DuoOrchestratorPlanner planner, ModelRoleRegistry registry) =>
{
    ChatPlanAdvice advice = ChatPlanAdvisor.Advise(request ?? new ChatPlanRequest(), planner, registry);
    return TypedResults.Ok(advice);
})
    .ValidatePalRequest<ChatPlanRequest>()
    .WithName("PostChatPlanAdvice")
    .WithTags("Inspection")
    .WithSummary("Return the Duo cooperation pattern the planner would pick for a specific chat request, plus the executable role chain that would dispatch today.");

// Local "why engine" — natural-language causal questions about the PalLLM
// runtime's recent behaviour. Deterministic-first: no inference call, so
// the endpoint is always available and ships the same structured answer
// shape regardless of whether live inference is off, broken, or thriving.
// Pattern comes from the 2026+ local-first AI mesh "why engine" trend:
// every complex local system should answer "why did X happen?" with a
// grounded causal chain, not a vague LLM guess.
api.MapPost("/why", IResult (
    WhyRequest request,
    PalLlmRuntime runtime,
    PalLlmMetrics metrics) =>
{
    // Take ONE health snapshot and reuse it for both the WhyEngine call and
    // the operator-health score. Two GetHealth() invocations would (a) waste
    // the snapshot-assembly cost and (b) leave a small consistency window
    // where WhyEngine's health argument and the score's input could disagree
    // (counters tick between the two reads).
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

api.MapPost("/vision/describe", async (
    VisionDescribeRequest request,
    PalLlmRuntime runtime,
    CancellationToken cancellationToken) =>
    TypedResults.Ok(await runtime.DescribeImageAsync(request, cancellationToken)))
    .ValidatePalRequest<VisionDescribeRequest>()
    .RequireRateLimiting("vision-heavy")
    .WithName("DescribeImage")
    .WithTags("Vision")
    .WithSummary("Generate a freeform scene description for an image.")
    .Produces<VisionDescribeResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .WithRequestTimeout("vision-timeout");

api.MapPost("/vision/world-state", async (
    VisionWorldStateRequest request,
    PalLlmRuntime runtime,
    CancellationToken cancellationToken) =>
    TypedResults.Ok(await runtime.ExtractWorldStateAsync(request, cancellationToken)))
    .ValidatePalRequest<VisionWorldStateRequest>()
    .RequireRateLimiting("vision-heavy")
    .WithName("ExtractVisionWorldState")
    .WithTags("Vision")
    .WithSummary("Extract structured world-state data from an image and optionally merge it into the live snapshot.")
    .Produces<VisionWorldStateResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .WithRequestTimeout("vision-timeout");

api.MapPost("/vision/screenshots/process", async (
    PalLlmRuntime runtime,
    CancellationToken cancellationToken) =>
    TypedResults.Ok(await runtime.ProcessScreenshotsAsync(cancellationToken)))
    .RequireRateLimiting("vision-heavy")
    .WithName("ProcessPendingScreenshots")
    .WithTags("Vision")
    .WithSummary("Process pending screenshots from the bridge screenshot inbox.")
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .WithRequestTimeout("vision-timeout");

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

api.MapPost("/tts/synthesize", async (
    TtsSynthesizeRequest request,
    PalLlmRuntime runtime,
    CancellationToken cancellationToken) =>
    TypedResults.Ok(await runtime.SynthesizeSpeechAsync(request, cancellationToken)))
    .ValidatePalRequest<TtsSynthesizeRequest>()
    .RequireRateLimiting("tts-heavy")
    .WithName("SynthesizeSpeech")
    .WithTags("Audio")
    .WithSummary("Synthesize speech audio for a text payload.")
    .Produces<TtsSynthesizeResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .WithRequestTimeout("tts-timeout");

api.MapPost("/audio/transcribe", async (
    AudioTranscribeRequest request,
    PalLlmRuntime runtime,
    CancellationToken cancellationToken) =>
{
    AudioTranscribeResponse result = await runtime.TranscribeAudioAsync(request, cancellationToken);
    return TypedResults.Ok(result);
})
    .ValidatePalRequest<AudioTranscribeRequest>()
    .RequireRateLimiting("tts-heavy")
    .WithName("TranscribeAudio")
    .WithTags("Audio")
    .WithSummary("Transcribe a bounded local audio payload through an opt-in OpenAI-compatible ASR endpoint.")
    .Produces<AudioTranscribeResponse>(StatusCodes.Status200OK)
    .ProducesValidationProblem()
    .ProducesProblem(StatusCodes.Status429TooManyRequests)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
    .WithRequestTimeout("tts-timeout");

app.Run();

static IResult BuildPackValidationPayloadTooLargeResult(int maxBytes) =>
    Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "Payload Too Large",
        detail: $"Pack validation payloads must be {maxBytes} bytes or smaller.");

static IResult BuildRequestBodyPayloadTooLargeResult(long maxBytes) =>
    Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "Payload Too Large",
        detail: $"PalLLM API and MCP request bodies must be {maxBytes} bytes or smaller.");

static RequestTimeoutPolicy BuildRequestTimeoutPolicy(int timeoutSeconds, string detail) =>
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

static bool ShouldDisableCaching(PathString path) =>
    (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        && !IsConditionallyCacheableApiPath(path))
    || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase)
    || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

static bool ShouldLimitRequestBody(HttpRequest request) =>
    (HttpMethods.IsPost(request.Method)
        || HttpMethods.IsPut(request.Method)
        || HttpMethods.IsPatch(request.Method))
    && (request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
        || request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase));

static bool IsConditionallyCacheableApiPath(PathString path) =>
    path.Equals("/api/dashboard", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/api/features", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/api/describe", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/api/bridge/proof", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/api/inference/performance", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/api/release/readiness", StringComparison.OrdinalIgnoreCase)
    || path.Equals("/api/mcp/upstream", StringComparison.OrdinalIgnoreCase);

public partial class Program
{
}
