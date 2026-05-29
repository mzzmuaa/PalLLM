using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Net.Http.Headers;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Maps the static asset surface: dashboard at /, /welcome.html,
//            /openapi/v1.{json,yaml}, source-generated conditional ETags +
//            last-modified validation. The Field Console UI lives behind
//            these routes.
//   surface: PalLlmStaticAssetRoutes.MapStaticAssets(IEndpointRouteBuilder).
//   gate:    tests/PalLLM.Tests/StaticAssetTests.cs +
//            tests/PalLLM.Tests/SidecarEndpointTests.cs (static routes).
//   adr:     None directly.
//   docs:    docs/API.md (operational routes), src/PalLLM.Sidecar/wwwroot/
//            (the dashboard sources).
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmStaticAssetRoutes
{
    private static readonly ConcurrentDictionary<string, FieldConsoleAssetFingerprint> AssetFingerprints =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void MapPalLlmFieldConsoleStaticAssets(
        this WebApplication app,
        string? staticAssetsManifestPath)
    {
        ArgumentNullException.ThrowIfNull(app);

        MapFieldConsoleAsset(app, "/app.js", "app.js", "text/javascript; charset=utf-8");
        MapFieldConsoleAsset(app, "/favicon.svg", "favicon.svg", "image/svg+xml");
        MapFieldConsoleAsset(app, "/index.html", "index.html", "text/html; charset=utf-8");
        MapFieldConsoleAsset(app, "/manifest.webmanifest", "manifest.webmanifest", "application/manifest+json; charset=utf-8");
        MapFieldConsoleAsset(app, "/styles.css", "styles.css", "text/css; charset=utf-8");
        MapFieldConsoleAsset(app, "/welcome.html", "welcome.html", "text/html; charset=utf-8");

        if (!FieldConsoleAssetExists(app.Environment, "index.html") ||
            !FieldConsoleAssetExists(app.Environment, "styles.css") ||
            !FieldConsoleAssetExists(app.Environment, "app.js"))
        {
            app.MapStaticAssets(staticAssetsManifestPath);
        }

        app.MapGet("/", IResult (IWebHostEnvironment environment, HttpContext context) =>
            ServeFieldConsoleAsset(context, environment, "index.html", "text/html; charset=utf-8"))
            .ExcludeFromDescription();
    }

    private static bool FieldConsoleAssetExists(IWebHostEnvironment environment, string fileName)
    {
        string? webRootPath = environment.WebRootPath;
        return !string.IsNullOrWhiteSpace(webRootPath) &&
            File.Exists(Path.Combine(webRootPath, fileName));
    }

    private static void MapFieldConsoleAsset(WebApplication app, string route, string fileName, string contentType)
    {
        app.MapMethods(route, ["GET", "HEAD"], (IWebHostEnvironment environment, HttpContext context) =>
            ServeFieldConsoleAsset(context, environment, fileName, contentType))
            .WithOrder(int.MinValue)
            .ExcludeFromDescription();
    }

    private static IResult ServeFieldConsoleAsset(
        HttpContext context,
        IWebHostEnvironment environment,
        string fileName,
        string contentType)
    {
        string? webRootPath = environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(webRootPath))
        {
            return TypedResults.NotFound();
        }

        string assetPath = Path.Combine(webRootPath, fileName);
        var assetInfo = new FileInfo(assetPath);
        if (!assetInfo.Exists)
        {
            return TypedResults.NotFound();
        }

        FieldConsoleAssetFingerprint fingerprint = GetFingerprint(assetInfo);
        ApplyAssetCacheHeaders(context, fingerprint);
        if (ConditionalHttp.RequestMatchesEtag(context.Request, fingerprint.Etag) ||
            ConditionalHttp.RequestMatchesLastModified(context.Request, fingerprint.LastModifiedUtc))
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        return TypedResults.PhysicalFile(
            assetInfo.FullName,
            contentType,
            lastModified: fingerprint.LastModifiedUtc,
            entityTag: EntityTagHeaderValue.Parse(fingerprint.Etag));
    }

    private static void ApplyAssetCacheHeaders(HttpContext context, FieldConsoleAssetFingerprint fingerprint)
    {
        context.Response.Headers.ETag = fingerprint.Etag;
        context.Response.Headers.LastModified = fingerprint.LastModifiedUtc.ToString("R", CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "public, no-cache, must-revalidate";
    }

    private static FieldConsoleAssetFingerprint GetFingerprint(FileInfo assetInfo)
    {
        string path = assetInfo.FullName;
        long length = assetInfo.Length;
        long lastWriteUtcTicks = assetInfo.LastWriteTimeUtc.Ticks;

        if (AssetFingerprints.TryGetValue(path, out FieldConsoleAssetFingerprint? cached) &&
            cached.Length == length &&
            cached.LastWriteUtcTicks == lastWriteUtcTicks)
        {
            return cached;
        }

        using FileStream stream = assetInfo.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        string etag = $"W/\"{Convert.ToHexString(SHA256.HashData(stream))}\"";
        DateTimeOffset lastModifiedUtc = ConditionalHttp.TruncateToWholeSeconds(
            new DateTimeOffset(assetInfo.LastWriteTimeUtc));
        var fingerprint = new FieldConsoleAssetFingerprint(
            etag,
            lastModifiedUtc,
            length,
            lastWriteUtcTicks);
        AssetFingerprints[path] = fingerprint;
        return fingerprint;
    }

    private sealed record FieldConsoleAssetFingerprint(
        string Etag,
        DateTimeOffset LastModifiedUtc,
        long Length,
        long LastWriteUtcTicks);
}
