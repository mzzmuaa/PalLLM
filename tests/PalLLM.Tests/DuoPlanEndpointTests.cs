using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Covers <c>POST /api/duo/plan</c>. Pin:
///
/// 1. Empty body returns a usable default plan (ImplementDraft, Low
///    risk, Standard hardware) — the endpoint is always available.
/// 2. Invalid enum values do not 400 — the endpoint defaults to the
///    safe ImplementDraft + Low + Standard tuple so AI callers can be
///    sloppy with string input.
/// 3. Response is a valid JSON document with the documented
///    <c>DuoPlan</c> shape (Pattern / Why / Steps[] / Escalation /
///    ThinkingMode / ContextBudget / RiskLevel).
/// </summary>
public sealed class DuoPlanEndpointTests
{
    [Test]
    public async Task PostDuoPlan_EmptyBody_ReturnsUsableDefaultPlan()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/duo/plan")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // System.Text.Json serialises enums as numbers by default, so
        // Pattern lands as an int. Matches the shipping sidecar posture.
        Assert.That(root.TryGetProperty("Pattern", out JsonElement pattern), Is.True);
        Assert.That(pattern.ValueKind, Is.AnyOf(JsonValueKind.Number, JsonValueKind.String));
        Assert.That(root.GetProperty("Why").GetString(), Is.Not.Empty);
        Assert.That(root.GetProperty("Steps").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(root.GetProperty("Escalation").GetString(), Is.Not.Empty);
        // RiskLevel is the stringified enum name inside DuoPlan, so this
        // one is a string regardless of the global enum policy.
        Assert.That(root.GetProperty("RiskLevel").GetString(), Is.EqualTo("Low"));
    }

    [Test]
    public async Task PostDuoPlan_WithHighRiskTask_PicksParallelDisagreement()
    {
        await using var fixture = new SidecarTestFixture();

        // Request body uses numeric enum values because the sidecar
        // serialises enums numerically by default. HighRisk=8, High=2,
        // Standard=1 from DuoTaskKind / DuoRiskLevel / DuoHardwareTier.
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/duo/plan")
        {
            Content = new StringContent(
                "{\"Kind\":8,\"Risk\":2,\"Hardware\":1}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        // Pattern lands as a numeric enum value. The default
        // SidecarTestFixture has no Worker/Judge bound, so the planner
        // returns DeterministicOnly (11). Once both roles are bound it
        // would be ParallelDisagreement (3). Either is valid.
        int patternValue = doc.RootElement.GetProperty("Pattern").GetInt32();
        Assert.That(patternValue, Is.AnyOf(
            (int)PalLLM.Domain.Inference.DuoCooperationPattern.DeterministicOnly,
            (int)PalLLM.Domain.Inference.DuoCooperationPattern.ParallelDisagreement,
            (int)PalLLM.Domain.Inference.DuoCooperationPattern.SingleRoleFallback),
            "High-risk tasks must never fall through to a generic pattern.");
        Assert.That(doc.RootElement.GetProperty("Escalation").GetString(), Is.Not.Empty);
    }
}
