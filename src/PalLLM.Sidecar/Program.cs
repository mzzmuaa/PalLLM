using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.OutputCaching;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar;

var builder = WebApplication.CreateBuilder(args);
bool isOpenApiBuild = builder.Environment.IsEnvironment("OpenApiBuild");
HttpSurfaceOptions httpOptions = builder.Configuration.GetSection("PalLLM:Http").Get<HttpSurfaceOptions>() ?? new();

builder.Services
    .AddPalLlmCore(builder.Configuration, httpOptions)
    .AddPalLlmInference(isOpenApiBuild)
    .AddPalLlmMcp(isOpenApiBuild)
    .AddPalLlmHealthAndOpenApi();
bool observabilityEnabled = builder.Services.AddPalLlmObservability();

var app = builder.Build();

if (observabilityEnabled)
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
// The `app.Urls` access is wrapped because some tooling hosts can invoke
// Main() without an IServerAddressesFeature. In that path the guard is
// skipped because there is no real bind address to inspect.
{
    PalLlmOptions guardOptions = app.Services.GetRequiredService<PalLlmOptions>();
    IReadOnlyList<string>? bindUrls = null;
    try
    {
        bindUrls = app.Urls.ToList();
    }
    catch (InvalidOperationException)
    {
        // IServerAddressesFeature not present. The guard has nothing to
        // inspect and the process won't serve traffic — skip silently.
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
app.MapPalLlmFieldConsoleStaticAssets(staticAssetsManifestPath);

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

app.MapPalLlmHealthRoutes(api, httpOptions);

api.MapPalLlmInspectionRoutes();

api.MapPalLlmPartyChatRoute();

api.MapPalLlmPlanningRoutes();

api.MapPalLlmProofPacketRoute();

api.MapPalLlmPromotionRoutes();

api.MapPalLlmReleaseProofRoutes(httpOptions);

api.MapPalLlmInferenceRoutes(httpOptions);

api.MapPalLlmContentWorldRoutes();

api.MapPalLlmBridgeRoutes();

api.MapPalLlmChatTurnRoutes();

api.MapPalLlmWhyRoute();

api.MapPalLlmMemoryRelationshipRoutes();

api.MapPalLlmVisionRoutes();

api.MapPalLlmSessionRoutes();

api.MapPalLlmAudioRoutes();

app.Run();

static IResult BuildRequestBodyPayloadTooLargeResult(long maxBytes) =>
    Results.Problem(
        statusCode: StatusCodes.Status413PayloadTooLarge,
        title: "Payload Too Large",
        detail: $"PalLLM API and MCP request bodies must be {maxBytes} bytes or smaller.");

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
