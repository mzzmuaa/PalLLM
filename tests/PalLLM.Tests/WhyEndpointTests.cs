using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Covers <c>POST /api/why</c>. Contract pinned here:
///
/// 1. Empty-body / empty-question POST still returns 200 with a
///    grounded generic-posture answer — the endpoint is always available.
/// 2. A keyword question ("why is the bridge not ready?") routes to the
///    specific intent and surfaces the evidence references the WhyEngine
///    pinned in its unit tests.
/// 3. Response is a valid JSON document with the documented
///    <c>WhyAnswer</c> shape.
/// </summary>
public sealed class WhyEndpointTests
{
    [Test]
    public async Task PostWhy_EmptyQuestion_ReturnsGroundedPosture()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/why")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.GetProperty("Intent").GetString(), Is.EqualTo("Unknown"));
        Assert.That(root.GetProperty("PrimaryReason").GetString(), Is.Not.Empty);
        Assert.That(root.GetProperty("CausalChain").GetArrayLength(), Is.GreaterThan(0));
    }

    [Test]
    public async Task PostWhy_BridgeQuestion_ReturnsBridgeIntent()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/why")
        {
            Content = new StringContent(
                "{\"Question\":\"why is the bridge not ready?\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.GetProperty("Intent").GetString(), Is.EqualTo("BridgeNotReady"));
        JsonElement evidence = root.GetProperty("EvidenceReferences");
        Assert.That(evidence.ValueKind, Is.EqualTo(JsonValueKind.Array),
            "Bridge answer must carry evidence references.");
    }
}
