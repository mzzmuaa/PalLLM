using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Covers <c>POST /api/chat/stream</c>. Contract pinned here:
///
/// 1. Response content-type is <c>text/event-stream</c>.
/// 2. The stream emits at minimum a <c>started</c> event and a
///    <c>final</c> event; the final event carries the complete
///    <c>ChatResponse</c> JSON so a client that only cares about the
///    final answer can still consume this endpoint.
/// 3. The final ChatResponse matches what <c>/api/chat</c> would have
///    returned for the same request (deterministic fallback, since the
///    test fixture has inference disabled).
/// </summary>
public sealed class ChatStreamEndpointTests
{
    [Test]
    public async Task PostChatStream_EmitsStartedAndFinal()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = new StringContent(
                "{\"UserMessage\":\"hi there\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("event: started"),
            "Stream must open with a 'started' event so clients know the request id.");
        Assert.That(body, Does.Contain("event: final"),
            "Stream must close with a 'final' event carrying the ChatResponse.");

        // Parse the final event's data payload and sanity-check the shape.
        string finalBlock = ExtractEventBlock(body, "final");
        string dataJson = ExtractDataLine(finalBlock);
        using JsonDocument doc = JsonDocument.Parse(dataJson);
        JsonElement root = doc.RootElement;
        Assert.That(root.TryGetProperty("AssistantMessage", out JsonElement assistantMessage), Is.True,
            "Final event payload must include the same AssistantMessage shape as /api/chat.");
        Assert.That(assistantMessage.GetString(), Is.Not.Empty,
            "Assistant must always have a reply (deterministic fallback guarantee).");
        Assert.That(root.TryGetProperty("UsedFallback", out _), Is.True,
            "Final event payload must expose UsedFallback so streaming clients can detect fallback paths.");

        // Pass 23 per-channel events must precede the `final` event.
        // 'token' and 'presentation' are always present (the
        // deterministic fallback always produces a reply + cue plan).
        // 'speech' is only emitted when TTS is enabled, and 'action'
        // only when the fallback also surfaces an action intent — both
        // covered separately in
        // PostChatStream_TokenEventsReconstructFullReply and the
        // richer integration tests.
        Assert.That(body, Does.Contain("event: token"),
            "Pass 23 must emit incremental 'token' events so streaming clients can render words live.");
        Assert.That(body, Does.Contain("event: presentation"),
            "Pass 23 must emit a 'presentation' event with the visual cue plan.");
    }

    [Test]
    public async Task PostChatStream_TokenEventsReconstructFullReply()
    {
        await using var fixture = new SidecarTestFixture();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = new StringContent(
                "{\"UserMessage\":\"a slightly longer message so multiple tokens emit\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        string body = await response.Content.ReadAsStringAsync();

        // Sum every token payload and compare against the final
        // AssistantMessage — they must match exactly so streaming
        // clients don't see a different reply than sync callers.
        string[] blocks = body.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        foreach (string block in blocks)
        {
            if (!block.StartsWith("event: token", StringComparison.Ordinal))
            {
                continue;
            }
            string data = ExtractDataLine(block);
            using JsonDocument tokenDoc = JsonDocument.Parse(data);
            sb.Append(tokenDoc.RootElement.GetProperty("text").GetString());
        }

        string finalBlock = ExtractEventBlock(body, "final");
        using JsonDocument finalDoc = JsonDocument.Parse(ExtractDataLine(finalBlock));
        string assistantMessage = finalDoc.RootElement.GetProperty("AssistantMessage").GetString() ?? "";

        Assert.That(sb.ToString().TrimEnd(), Is.EqualTo(assistantMessage.TrimEnd()),
            "Concatenated token events must reconstruct the full AssistantMessage from the final event.");
    }

    [Test]
    public async Task PostChatStream_WhenInferenceThrows_EmitsGenericErrorWithoutInternalDetails()
    {
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Inference:Enabled"] = "true",
                ["PalLLM:Inference:Model"] = "throwing-model",
                ["PalLLM:Fallback:EnablePolicyBypass"] = "false",
            },
            services =>
            {
                services.RemoveAll<IInferenceClient>();
                services.AddSingleton<IInferenceClient>(new ThrowingInferenceClient("kaboom: private transport detail"));
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = new StringContent(
                "{\"UserMessage\":\"force an internal error\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("event: error"));
        Assert.That(body, Does.Not.Contain("kaboom"),
            "Internal exception text must not be echoed into SSE error payloads.");

        string errorBlock = ExtractEventBlock(body, "error");
        using JsonDocument errorDoc = JsonDocument.Parse(ExtractDataLine(errorBlock));
        JsonElement root = errorDoc.RootElement;

        Assert.That(root.GetProperty("message").GetString(),
            Is.EqualTo("Chat stream aborted before the final reply was ready."));
        Assert.That(root.GetProperty("retryable").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("internal_error"));
        Assert.That(root.TryGetProperty("detail", out _), Is.False);
    }

    [Test]
    public async Task PostChatStream_WhenChatTimeoutExpires_EmitsSanitizedTimeoutError()
    {
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Http:ChatRequestTimeoutSeconds"] = "1",
                ["PalLLM:Inference:Enabled"] = "true",
                ["PalLLM:Inference:Model"] = "hanging-model",
                ["PalLLM:Fallback:EnablePolicyBypass"] = "false",
            },
            services =>
            {
                services.RemoveAll<IInferenceClient>();
                services.AddSingleton<IInferenceClient>(new HangingInferenceClient());
            });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/stream")
        {
            Content = new StringContent(
                "{\"UserMessage\":\"force a stream timeout\"}",
                Encoding.UTF8,
                "application/json"),
        };

        using HttpResponseMessage response = await fixture.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "SSE responses flush before the final reply, so timeout is signaled as an error event.");
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/event-stream"));

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("event: started"));
        Assert.That(body, Does.Contain("event: error"));
        Assert.That(body, Does.Not.Contain("event: final"),
            "A timed-out stream must not emit a final ChatResponse.");

        string errorBlock = ExtractEventBlock(body, "error");
        using JsonDocument errorDoc = JsonDocument.Parse(ExtractDataLine(errorBlock));
        JsonElement root = errorDoc.RootElement;

        Assert.That(root.GetProperty("message").GetString(),
            Is.EqualTo("Chat stream exceeded its configured timeout before the final reply was ready."));
        Assert.That(root.GetProperty("retryable").GetBoolean(), Is.True);
        Assert.That(root.GetProperty("reason").GetString(), Is.EqualTo("request_timeout"));
        Assert.That(root.TryGetProperty("detail", out _), Is.False);
    }

    private static string ExtractEventBlock(string body, string eventName)
    {
        // SSE blocks are separated by \n\n. Find the block whose first line
        // reads `event: <name>`.
        string[] blocks = body.Split(new[] { "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string block in blocks)
        {
            if (block.StartsWith("event: " + eventName, StringComparison.Ordinal))
            {
                return block;
            }
        }
        throw new AssertionException($"No SSE block with event: {eventName}");
    }

    private static string ExtractDataLine(string block)
    {
        foreach (string line in block.Split('\n'))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                return line.Substring("data: ".Length);
            }
        }
        throw new AssertionException("SSE block did not contain a data: line.");
    }

    private sealed class ThrowingInferenceClient : IInferenceClient
    {
        private readonly string _message;

        public ThrowingInferenceClient(string message)
        {
            _message = message;
        }

        public Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(_message);
    }

    private sealed class HangingInferenceClient : IInferenceClient
    {
        public async Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return InferenceResult.Succeeded("late", new TokenUsage(), "test", "hanging-model", null, 30_000);
        }
    }
}
