using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Pins <c>POST /api/chat/plan</c>. Contract:
///
/// 1. Empty body returns a valid advisory plan (defaults applied).
/// 2. An architecture-sounding user message infers
///    <c>ArchitecturePlan</c> as the task kind.
/// 3. A high-risk phrase always routes to <c>HighRisk</c> + the
///    planner's <c>ParallelDisagreement</c> pattern (when roles allow).
/// 4. Explicit TaskTag overrides message-keyword inference.
/// </summary>
public sealed class ChatPlanEndpointTests
{
    private static async Task<JsonDocument> PostAsync(SidecarTestFixture fixture, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/plan")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }

    [Test]
    public async Task PostChatPlan_EmptyBody_ReturnsDefaultsApplied()
    {
        await using var fixture = new SidecarTestFixture();

        using JsonDocument doc = await PostAsync(fixture, "{}");

        Assert.That(doc.RootElement.GetProperty("InferredTaskKind").GetString(),
            Is.EqualTo("ImplementDraft"));
        Assert.That(doc.RootElement.GetProperty("Risk").GetString(), Is.EqualTo("Low"));
        Assert.That(doc.RootElement.GetProperty("Hardware").GetString(), Is.EqualTo("Standard"));
        Assert.That(doc.RootElement.TryGetProperty("Plan", out _), Is.True);
    }

    [Test]
    public async Task PostChatPlan_ArchitectureMessage_InfersArchitecturePlan()
    {
        await using var fixture = new SidecarTestFixture();

        using JsonDocument doc = await PostAsync(fixture,
            "{\"UserMessage\":\"please draft an architecture for our new asset foundry\"}");

        Assert.That(doc.RootElement.GetProperty("InferredTaskKind").GetString(),
            Is.EqualTo("ArchitecturePlan"));
    }

    [Test]
    public async Task PostChatPlan_HighRiskPhrase_InfersHighRisk()
    {
        await using var fixture = new SidecarTestFixture();

        using JsonDocument doc = await PostAsync(fixture,
            "{\"UserMessage\":\"please delete every production record\"}");

        Assert.That(doc.RootElement.GetProperty("InferredTaskKind").GetString(),
            Is.EqualTo("HighRisk"));
    }

    [Test]
    public async Task PostChatPlan_ExplicitTaskTag_OverridesInference()
    {
        await using var fixture = new SidecarTestFixture();

        using JsonDocument doc = await PostAsync(fixture,
            "{\"UserMessage\":\"short message\",\"TaskTag\":\"MediaPrompting\"}");

        Assert.That(doc.RootElement.GetProperty("InferredTaskKind").GetString(),
            Is.EqualTo("MediaPrompting"));
    }

    [Test]
    public async Task PostChatPlan_IncludesFullPlanShape()
    {
        await using var fixture = new SidecarTestFixture();

        using JsonDocument doc = await PostAsync(fixture,
            "{\"UserMessage\":\"audit the payment flow\"}");

        JsonElement plan = doc.RootElement.GetProperty("Plan");
        Assert.That(plan.TryGetProperty("Pattern", out _), Is.True);
        Assert.That(plan.TryGetProperty("Steps", out _), Is.True);
        Assert.That(plan.TryGetProperty("Escalation", out _), Is.True);
        Assert.That(plan.TryGetProperty("RiskLevel", out _), Is.True);
    }
}
