using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Regression coverage for Pass 185's malformed-request handler. The
/// load-bearing invariant: a POST with malformed JSON or empty body
/// must surface as 400 Bad Request with a clean ProblemDetails body —
/// never as a confusing 500 (the prior pre-Pass-185 behavior).
///
/// <para>
/// Each adverse-input case is declared as an inline <c>[TestCase]</c>
/// rather than via <c>[TestCaseSource]</c> so each endpoint × case
/// pair contributes one regex-countable declaration to
/// <c>Drift_Test_count_docs</c>. Adding a new POST endpoint to the
/// runtime means adding three <c>[TestCase]</c> rows here (one per
/// adverse-input shape).
/// </para>
/// </summary>
public sealed class MalformedRequestExceptionHandlerTests
{
    [TestCase("/api/chat")]
    [TestCase("/api/chat/plan")]
    [TestCase("/api/why")]
    [TestCase("/api/promotion/apply")]
    [TestCase("/api/promotion/apply/preview")]
    [TestCase("/api/promotion/record")]
    [TestCase("/api/disagreement/check")]
    [TestCase("/api/proof/packet")]
    [TestCase("/api/duo/plan")]
    [TestCase("/api/directives/plan")]
    public async Task PostWithMalformedJson_Returns400ProblemDetails(string endpoint)
    {
        await using var fixture = new SidecarTestFixture();
        using HttpRequestMessage request = BuildPost(endpoint, "not valid json");

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            $"{endpoint} must return 400 (not 500) for malformed JSON. " +
            "The prior pre-Pass-185 behavior was 500 because BadHttpRequestException " +
            "was flowing past the exception handler.");

        await AssertCleanProblemDetailsAsync(response, endpoint);
    }

    [TestCase("/api/chat")]
    [TestCase("/api/chat/plan")]
    [TestCase("/api/why")]
    [TestCase("/api/promotion/apply")]
    [TestCase("/api/promotion/apply/preview")]
    [TestCase("/api/promotion/record")]
    [TestCase("/api/disagreement/check")]
    [TestCase("/api/proof/packet")]
    [TestCase("/api/duo/plan")]
    [TestCase("/api/directives/plan")]
    public async Task PostWithEmptyBody_Returns400ProblemDetails(string endpoint)
    {
        await using var fixture = new SidecarTestFixture();
        using HttpRequestMessage request = BuildPost(endpoint, string.Empty);

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            $"{endpoint} must return 400 (not 500) for an empty body.");

        await AssertCleanProblemDetailsAsync(response, endpoint);
    }

    [TestCase("/api/chat")]
    [TestCase("/api/chat/plan")]
    [TestCase("/api/why")]
    [TestCase("/api/promotion/apply")]
    [TestCase("/api/promotion/apply/preview")]
    [TestCase("/api/promotion/record")]
    [TestCase("/api/disagreement/check")]
    [TestCase("/api/proof/packet")]
    [TestCase("/api/duo/plan")]
    [TestCase("/api/directives/plan")]
    public async Task PostWithTruncatedJson_Returns400ProblemDetails(string endpoint)
    {
        await using var fixture = new SidecarTestFixture();
        // JSON missing its closing brace — a common mistake when a
        // client builds a request by string concatenation.
        using HttpRequestMessage request = BuildPost(endpoint, """{"x":1""");

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            $"{endpoint} must return 400 (not 500) for truncated JSON.");

        await AssertCleanProblemDetailsAsync(response, endpoint);
    }

    [TestCase("/api/chat")]
    [TestCase("/mcp")]
    public async Task PostWithBodyOverConfiguredApiCap_Returns413ProblemDetails(string endpoint)
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Http:ApiRequestBodyMaxBytes"] = "1024",
        });
        using HttpRequestMessage request = BuildPost(endpoint, $$"""{"padding":"{{new string('x', 2048)}}"}""");

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.RequestEntityTooLarge),
            $"{endpoint} must fail fast with 413 before JSON model binding allocates an oversized body.");

        await AssertCleanProblemDetailsAsync(response, endpoint, HttpStatusCode.RequestEntityTooLarge);
    }

    [Test]
    public async Task PostChatWithValidBody_Still_Returns200()
    {
        // Sanity check: the new exception handler must not interfere
        // with the happy path. A valid /api/chat request must still
        // round-trip cleanly via the deterministic fallback.
        await using var fixture = new SidecarTestFixture();
        using HttpRequestMessage request = BuildPost(
            "/api/chat",
            """{"userMessage":"hi","characterId":1}""");

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "Valid /api/chat request must still return 200 — the handler must only react to malformed input.");
    }

    // ---------------------------------------------------------------------
    // Pass 186 — Hard cap on user-supplied free-text fields.
    //
    // PalApiValidation.UserTextMaxLength = 16 KiB caps `UserMessage`
    // (chat) and `Query` (memory recall). A misbehaving client previously
    // could submit a 5 MB userMessage that produced a ~6 MB reply; the
    // hard cap closes that vector cleanly.
    // ---------------------------------------------------------------------

    [Test]
    public async Task PostChat_RejectsOversizedUserMessageWith400()
    {
        await using var fixture = new SidecarTestFixture();
        // 17 KB — one byte over the 16 KB cap.
        string huge = new('a', (16 * 1024) + 1);
        using HttpRequestMessage request = BuildPost(
            "/api/chat",
            JsonSerializer.Serialize(new { userMessage = huge, characterId = 1 }));

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "Chat request with >16 KB userMessage must surface as 400, not be silently accepted.");

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("UserMessage"),
            "ProblemDetails must name the offending field so the caller can diagnose.");
        Assert.That(body, Does.Contain("16384"),
            "ProblemDetails should communicate the cap so the caller knows the bound.");
    }

    [Test]
    public async Task PostMemoryRecall_RejectsOversizedQueryWith400()
    {
        await using var fixture = new SidecarTestFixture();
        string huge = new('q', (16 * 1024) + 1);
        using HttpRequestMessage request = BuildPost(
            "/api/memory/recall",
            JsonSerializer.Serialize(new { query = huge, characterId = 1 }));

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "Memory recall with >16 KB Query must surface as 400.");

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Query"),
            "ProblemDetails must name the offending field.");
    }

    [Test]
    public async Task PostChat_AcceptsMessageAtExactlyCapBoundary()
    {
        // 16 KB exactly — must still be accepted. Off-by-one would
        // silently break legitimate long-form chat use cases.
        await using var fixture = new SidecarTestFixture();
        string atCap = new('a', 16 * 1024);
        using HttpRequestMessage request = BuildPost(
            "/api/chat",
            JsonSerializer.Serialize(new { userMessage = atCap, characterId = 1 }));

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "16 KB exactly must be accepted — the cap is inclusive of the documented limit.");
    }

    // ---------------------------------------------------------------------
    // Pass 190 - The same user-text cap also applies to deterministic
    // planning/proof endpoints that echo, tokenize, classify, or hash caller
    // text. Empty/default requests remain accepted; oversized fields return a
    // clean validation 400 before any planner work starts.
    // ---------------------------------------------------------------------

    [TestCase("/api/chat/plan", "userMessage", "UserMessage")]
    [TestCase("/api/why", "question", "Question")]
    [TestCase("/api/directives/plan", "utterance", "Utterance")]
    [TestCase("/api/duo/plan", "note", "Note")]
    [TestCase("/api/disagreement/check", "workerOutput", "WorkerOutput")]
    [TestCase("/api/proof/packet", "decision", "Decision")]
    public async Task PostPlanningEndpoint_RejectsOversizedTextFieldWith400(
        string endpoint,
        string jsonField,
        string expectedErrorField)
    {
        await using var fixture = new SidecarTestFixture();
        string huge = new('x', (16 * 1024) + 1);
        using HttpRequestMessage request = BuildPost(
            endpoint,
            $$"""{"{{jsonField}}":"{{huge}}"}""");

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            $"{endpoint} must reject >16 KB caller text before planner/proof work starts.");

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain(expectedErrorField),
            "ProblemDetails must name the offending field.");
        Assert.That(body, Does.Contain("16384"),
            "ProblemDetails should communicate the shared text cap.");
    }

    [Test]
    public async Task PostProofPacket_RejectsTooManyEvidenceEntriesWith400()
    {
        await using var fixture = new SidecarTestFixture();
        string[] evidence = Enumerable.Range(0, 33)
            .Select(i => $"line-{i}")
            .ToArray();
        using HttpRequestMessage request = BuildPost(
            "/api/proof/packet",
            JsonSerializer.Serialize(new
            {
                subsystem = "test",
                decision = "record",
                evidence,
            }));

        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest),
            "Proof packets must keep evidence as a bounded summary rather than an unbounded transcript.");

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("Evidence"),
            "ProblemDetails must identify the bounded evidence list.");
        Assert.That(body, Does.Contain("32"),
            "ProblemDetails should communicate the evidence-entry cap.");
    }

    [Test]
    public async Task PostPlanningEndpoint_WithEmptyObject_StillReturns200()
    {
        string[] endpoints =
        [
            "/api/chat/plan",
            "/api/why",
            "/api/directives/plan",
            "/api/duo/plan",
            "/api/disagreement/check",
            "/api/proof/packet",
        ];

        await using var fixture = new SidecarTestFixture();
        foreach (string endpoint in endpoints)
        {
            using HttpRequestMessage request = BuildPost(endpoint, "{}");
            using HttpResponseMessage response = await fixture.Client.SendAsync(request);
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
                $"{endpoint} must keep its deterministic default-request contract.");
        }
    }

    private static HttpRequestMessage BuildPost(string endpoint, string body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        return request;
    }

    private static async Task AssertCleanProblemDetailsAsync(
        HttpResponseMessage response,
        string endpoint,
        HttpStatusCode expectedStatus = HttpStatusCode.BadRequest)
    {
        Assert.That(response.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("application/problem+json").Or.EqualTo("application/json"),
            $"{endpoint} {(int)expectedStatus} response must use application/problem+json (or fall back to application/json).");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.GetProperty("status").GetInt32(), Is.EqualTo((int)expectedStatus),
            $"{endpoint} ProblemDetails 'status' must equal {(int)expectedStatus}.");
        Assert.That(root.GetProperty("title").GetString(), Is.Not.Empty,
            $"{endpoint} ProblemDetails must include a non-empty title.");
        Assert.That(root.TryGetProperty("type", out JsonElement typeProp), Is.True,
            $"{endpoint} ProblemDetails must include the RFC 9110 type URI.");
        Assert.That(typeProp.GetString(), Does.Contain("rfc9110"),
            $"{endpoint} type URI should reference RFC 9110 so clients can map to a known status class.");

        // Hard rule: implementation details (stack traces, exception
        // type names, file paths) must never appear in /api/* error
        // bodies. A leaked stack trace would surface as long
        // CamelCase identifiers, " at " frames, or .cs file paths.
        Assert.That(body, Does.Not.Contain(" at PalLLM."),
            $"{endpoint} ProblemDetails must not leak a stack trace.");
        Assert.That(body, Does.Not.Contain("BadHttpRequestException"),
            $"{endpoint} ProblemDetails must not name internal exception types.");
        Assert.That(body, Does.Not.Contain(".cs:line"),
            $"{endpoint} ProblemDetails must not leak source file paths.");
    }
}
