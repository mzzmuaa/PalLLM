using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Covers <c>POST /api/promotion/apply/preview</c>. Pin:
///
/// 1. Missing/empty TaskClass → 400 ProblemDetails (caller error).
/// 2. Unknown TaskClass → 404 (ledger has no observations for it).
/// 3. Known TaskClass but not yet a candidate → 409 with the ledger's
///    own "not ready" recommendation text.
/// 4. Candidate TaskClass → 200 with the full
///    <c>PromotionApplyPreview</c> shape.
/// </summary>
public sealed class PromotionApplyPreviewEndpointTests
{
    [Test]
    public async Task PostApplyPreview_MissingTaskClass_Returns400()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/apply/preview")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
    }

    [Test]
    public async Task PostApplyPreview_UnknownTaskClass_Returns404()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/apply/preview")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"does-not-exist\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task PostApplyPreview_RecordedButNotCandidate_Returns409()
    {
        await using var fixture = new SidecarTestFixture();

        // Single observation — below the 20-observation minimum, so not a candidate.
        using var recordReq = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/record")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"fallback-director\",\"PatternId\":\"test\",\"Outcome\":\"success\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage recordResp = await fixture.Client.SendAsync(recordReq);
        Assert.That(recordResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var previewReq = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/apply/preview")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"fallback-director\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage previewResp = await fixture.Client.SendAsync(previewReq);
        Assert.That(previewResp.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
    }

    [Test]
    public async Task PostApplyPreview_CandidateTaskClass_ReturnsPreview()
    {
        await using var fixture = new SidecarTestFixture();

        // Record 20 successful observations so the task passes the stability gate.
        for (int i = 0; i < 20; i++)
        {
            using var recordReq = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/record")
            {
                Content = new StringContent(
                    "{\"TaskClass\":\"fallback-director\",\"PatternId\":\"stealth-withdraw\",\"Outcome\":\"success\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
            using HttpResponseMessage recordResp = await fixture.Client.SendAsync(recordReq);
            Assert.That(recordResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        }

        using var previewReq = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/apply/preview")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"fallback-director\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage previewResp = await fixture.Client.SendAsync(previewReq);
        Assert.That(previewResp.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await previewResp.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.GetProperty("TaskClass").GetString(), Is.EqualTo("fallback-director"));
        Assert.That(root.GetProperty("PatternId").GetString(), Is.EqualTo("stealth-withdraw"));
        Assert.That(root.GetProperty("DiffPreview").GetString(), Does.Contain("FallbackBehaviorEngine"));
        Assert.That(root.GetProperty("RollbackCommand").GetString(), Does.StartWith("git checkout"));
        Assert.That(root.GetProperty("SafetyWarnings").GetArrayLength(), Is.GreaterThan(0));
    }
}
