using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Covers the <c>GET /api/describe</c> self-description endpoint. The
/// contract this endpoint promises to AI / MCP clients is:
///
/// 1. Always returns 200 on success with a <c>SelfDescription</c> payload.
/// 2. Participates in the strong-ETag private-cache protocol so repeat
///    callers can cheap-revalidate (304).
/// 3. Exposes the feature counts, route count, and safety-tier language
///    callers use to decide which specific surface to reach for next.
/// </summary>
public sealed class SelfDescriptionEndpointTests
{
    [Test]
    public async Task GetDescribe_ReturnsSelfDescriptionWithCountsAndPosture()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/describe");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Sidecar is configured with PropertyNamingPolicy = null, so JSON
        // fields preserve the original PascalCase from the record.
        Assert.That(root.TryGetProperty("Identity", out JsonElement identity), Is.True);
        Assert.That(identity.GetProperty("Product").GetString(), Is.EqualTo("PalLLM"));
        Assert.That(identity.GetProperty("License").GetString(), Is.EqualTo("MIT"));
        Assert.That(identity.GetProperty("Redistributable").GetBoolean(), Is.True);

        Assert.That(root.TryGetProperty("Version", out JsonElement version), Is.True);
        Assert.That(version.GetProperty("McpProtocol").GetString(), Is.EqualTo("2025-06-18"));

        Assert.That(root.TryGetProperty("Surface", out JsonElement surface), Is.True);
        Assert.That(surface.GetProperty("ApiRouteCount").GetInt32(), Is.GreaterThan(0),
            "Route count must be computed from the live EndpointDataSource, not a constant.");
        Assert.That(surface.GetProperty("FeatureCount").GetInt32(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("FallbackStrategyCount").GetInt32(), Is.EqualTo(19));

        Assert.That(root.TryGetProperty("PostureGuarantees", out JsonElement posture), Is.True);
        Assert.That(posture.GetProperty("LocalFirst").GetString(), Is.Not.Empty);
        Assert.That(posture.GetProperty("DeterministicFallbackAlwaysAvailable").GetString(), Is.Not.Empty);

        Assert.That(root.TryGetProperty("CommonAsks", out JsonElement asks), Is.True);
        Assert.That(asks.GetArrayLength(), Is.GreaterThanOrEqualTo(5),
            "Self-description must advertise at least 5 common asks for AI callers to pick from.");

        Assert.That(root.TryGetProperty("SafetyNotes", out JsonElement safety), Is.True);
        Assert.That(safety.GetProperty("EmergencyFallbackTier").GetString(), Does.Contain("EmergencyFallback"),
            "Safety notes must mention the third-tier safety net so AI callers know chat never crashes.");
    }

    [Test]
    public async Task GetDescribe_ETagRevalidationReturns304()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/describe");
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string? etag = first.Headers.ETag?.Tag;
        Assert.That(etag, Is.Not.Null.And.Not.Empty, "GET /api/describe must emit a strong ETag.");

        using var ifNoneMatchRequest = new HttpRequestMessage(HttpMethod.Get, "/api/describe");
        ifNoneMatchRequest.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using HttpResponseMessage revalidated = await fixture.Client.SendAsync(ifNoneMatchRequest);

        Assert.That(revalidated.StatusCode, Is.EqualTo(HttpStatusCode.NotModified),
            "A matching If-None-Match must return 304 so AI callers can cheap-revalidate.");
    }

    [Test]
    public async Task GetDescribe_DefaultCacheControlUsesShortDedicatedTtl()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/describe");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string cacheControl = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.That(cacheControl, Does.Contain("private"));
        Assert.That(cacheControl, Does.Contain("max-age=15"),
            "Self-description should use its own short cache TTL instead of inheriting the feature-catalog window.");
    }
}
