using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Covers <c>POST /api/promotion/record</c> and
/// <c>GET /api/promotion/summary</c>. Pin:
///
/// 1. Fresh sidecar → empty summary (no tasks, zero candidates).
/// 2. Recording an observation with a valid outcome returns 200 + the
///    stored observation with a 12-char id and normalised outcome.
/// 3. Missing or invalid fields return 400 ProblemDetails with stable
///    public messages instead of raw exception text.
/// 4. After recording, summary reflects the observation.
/// </summary>
public sealed class PromotionEndpointTests
{
    [Test]
    public async Task GetSummary_FreshSidecar_ReturnsEmpty()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/promotion/summary");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.GetProperty("Tasks").GetArrayLength(), Is.EqualTo(0));
        Assert.That(root.GetProperty("PromotionCandidateCount").GetInt32(), Is.EqualTo(0));
    }

    [Test]
    public async Task PostRecord_ValidOutcome_ReturnsStoredObservation()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/record")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"ImplementDraft\",\"PatternId\":\"duo-branch-tournament\",\"Outcome\":\"success\",\"Note\":\"Worker draft accepted\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.GetProperty("Id").GetString(), Has.Length.EqualTo(12));
        Assert.That(root.GetProperty("TaskClass").GetString(), Is.EqualTo("ImplementDraft"));
        Assert.That(root.GetProperty("PatternId").GetString(), Is.EqualTo("duo-branch-tournament"));
        Assert.That(root.GetProperty("Outcome").GetString(), Is.EqualTo("success"));
    }

    [Test]
    public async Task PostRecord_InvalidOutcome_ReturnsProblemDetails400()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/record")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"task\",\"PatternId\":\"pattern\",\"Outcome\":\"maybe\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;

        Assert.That(root.GetProperty("title").GetString(), Is.EqualTo("Invalid promotion outcome"));
        Assert.That(root.GetProperty("detail").GetString(), Does.Contain("success"));
        Assert.That(root.GetProperty("detail").GetString(), Does.Not.Contain("not recognised"));
    }

    [Test]
    public async Task PostRecord_MissingTaskClass_ReturnsStableProblemDetails400()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/record")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"   \",\"PatternId\":\"pattern\",\"Outcome\":\"success\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;

        Assert.That(root.GetProperty("title").GetString(), Is.EqualTo("Missing task class"));
        Assert.That(root.GetProperty("detail").GetString(), Does.Contain("TaskClass"));
        Assert.That(root.GetProperty("detail").GetString(), Does.Not.Contain("Parameter"));
    }

    [Test]
    public async Task GetSummary_AfterRecording_ReflectsObservation()
    {
        await using var fixture = new SidecarTestFixture();

        // Record one observation.
        using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/api/promotion/record")
        {
            Content = new StringContent(
                "{\"TaskClass\":\"Audit\",\"PatternId\":\"dense-appeal\",\"Outcome\":\"success\"}",
                Encoding.UTF8,
                "application/json"),
        };
        using HttpResponseMessage postResponse = await fixture.Client.SendAsync(postRequest);
        Assert.That(postResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        // Summary should show exactly that task with 1 observation.
        using HttpResponseMessage getResponse = await fixture.Client.GetAsync("/api/promotion/summary");
        string body = await getResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement tasks = doc.RootElement.GetProperty("Tasks");

        Assert.That(tasks.GetArrayLength(), Is.EqualTo(1));
        JsonElement only = tasks[0];
        Assert.That(only.GetProperty("TaskClass").GetString(), Is.EqualTo("Audit"));
        Assert.That(only.GetProperty("TotalObservations").GetInt32(), Is.EqualTo(1));
        Assert.That(only.GetProperty("SuccessCount").GetInt32(), Is.EqualTo(1));
        Assert.That(only.GetProperty("IsPromotionCandidate").GetBoolean(), Is.False,
            "One observation must never be enough to promote.");
    }
}
