using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

// Pass 302 - direct unit tests for the upstream-failure status-string
// builder used by every outbound HTTP client (chat, vision, TTS, MCP
// upstream client pool). The builder produces the human-readable summary
// that surfaces in the operator dashboard, the proof packet, and the
// ResponsePath diagnostic when an upstream call fails. Until this pass
// it was only covered indirectly via integration paths. Any of the
// per-status branches could be silently re-mapped to a generic message
// — observable to an operator as "I can't tell why my inference
// endpoint failed."
public sealed class TransportFailureStatusBuilderTests
{
    private const string Surface = "Inference";

    // ---------- HttpStatus: known specific codes ----------

    [TestCase(400, "rejected the request (HTTP 400)")]
    [TestCase(401, "rejected authentication (HTTP 401)")]
    [TestCase(403, "refused the request (HTTP 403)")]
    [TestCase(404, "was not found (HTTP 404)")]
    [TestCase(408, "timed out while handling the request (HTTP 408)")]
    [TestCase(413, "rejected the request as too large (HTTP 413)")]
    [TestCase(415, "rejected the request media type (HTTP 415)")]
    [TestCase(422, "rejected the request payload (HTTP 422)")]
    [TestCase(429, "rate-limited the request (HTTP 429)")]
    public void HttpStatus_KnownClientError_ReturnsDistinctMessage(int statusCode, string expectedSnippet)
    {
        string message = TransportFailureStatusBuilder.HttpStatus(Surface, statusCode);

        Assert.That(message, Does.StartWith(Surface));
        Assert.That(message, Does.Contain(expectedSnippet));
    }

    // ---------- HttpStatus: 5xx range ----------

    [TestCase(500)]
    [TestCase(502)]
    [TestCase(503)]
    [TestCase(504)]
    [TestCase(599)]
    public void HttpStatus_ServerError_5xxRange_ReturnsGenericServerFailureMessage(int statusCode)
    {
        string message = TransportFailureStatusBuilder.HttpStatus(Surface, statusCode);

        Assert.That(message, Does.Contain($"failed while handling the request (HTTP {statusCode})"));
    }

    // ---------- HttpStatus: out-of-range / unknown ----------

    [TestCase(100)]
    [TestCase(200)]                       // unusual to see 2xx as a failure but should be neutral
    [TestCase(301)]
    [TestCase(305)]
    [TestCase(418)]                       // I'm a teapot — known but not in allowlist
    [TestCase(425)]                       // too early — known but not in allowlist
    [TestCase(600)]                       // out of 5xx range
    [TestCase(999)]
    public void HttpStatus_UnknownStatus_FallsBackToGenericMessage(int statusCode)
    {
        string message = TransportFailureStatusBuilder.HttpStatus(Surface, statusCode);

        Assert.That(message, Is.EqualTo($"{Surface} endpoint returned HTTP {statusCode}."));
    }

    // ---------- Surface label forwarding ----------

    [TestCase("Inference")]
    [TestCase("Vision")]
    [TestCase("TTS")]
    [TestCase("MCP upstream")]
    [TestCase("")]                        // operator passing empty string — should not throw
    public void HttpStatus_SurfaceLabel_IsForwardedIntoMessage(string surface)
    {
        string message = TransportFailureStatusBuilder.HttpStatus(surface, 500);

        Assert.That(message, Does.StartWith($"{surface} endpoint"),
            $"Surface label '{surface}' must appear at the start of the message.");
    }

    // ---------- Timeout / Unreachable / MalformedJson ----------

    [Test]
    public void Timeout_ReturnsSurfaceQualifiedMessage()
    {
        string message = TransportFailureStatusBuilder.Timeout(Surface);

        Assert.That(message, Is.EqualTo("Inference endpoint timed out before completing the response."));
    }

    [Test]
    public void Unreachable_ReturnsSurfaceQualifiedMessage()
    {
        string message = TransportFailureStatusBuilder.Unreachable(Surface);

        Assert.That(message, Is.EqualTo("Inference endpoint is unreachable."));
    }

    [Test]
    public void MalformedJson_ReturnsSurfaceQualifiedMessage()
    {
        string message = TransportFailureStatusBuilder.MalformedJson(Surface);

        Assert.That(message, Is.EqualTo("Inference endpoint returned malformed JSON."));
    }

    [Test]
    public void Timeout_AcceptsAnySurfaceLabel()
    {
        Assert.That(TransportFailureStatusBuilder.Timeout("Vision"),
            Does.StartWith("Vision endpoint"));
        Assert.That(TransportFailureStatusBuilder.Unreachable("TTS"),
            Does.StartWith("TTS endpoint"));
        Assert.That(TransportFailureStatusBuilder.MalformedJson("MCP upstream"),
            Does.StartWith("MCP upstream endpoint"));
    }

    // ---------- Messages are distinguishable ----------

    [Test]
    public void EachStatusCodeProducesDistinctMessage()
    {
        // Operators triage on the message — a regression that mapped two
        // different client errors to the same string would lose diagnostic
        // signal. This test ensures the 9 specific codes each produce
        // distinct text.
        int[] specificCodes = [400, 401, 403, 404, 408, 413, 415, 422, 429];
        var messages = specificCodes
            .Select(code => TransportFailureStatusBuilder.HttpStatus(Surface, code))
            .ToList();

        Assert.That(messages.Distinct().Count(), Is.EqualTo(specificCodes.Length),
            "Each known client-error code must produce a distinct human-readable message.");
    }
}
