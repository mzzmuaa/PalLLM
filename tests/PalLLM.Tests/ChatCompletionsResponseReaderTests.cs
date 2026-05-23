using System.Net.Http.Headers;
using System.Text;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

// Pass 298 - direct unit tests for the OpenAI-compatible chat-completions
// response parser. Every upstream chat reply from Ollama, llama.cpp, vLLM,
// LM Studio, transformers serve, TensorRT-LLM, OpenVINO, and Foundry Local
// runs through this parser. It sits one layer above HttpContentReadLimiter
// (Pass 224): the limiter caps bytes, this reader extracts structured
// response data from the bounded JSON.
//
// Until this pass the reader was only covered indirectly via
// `InferenceClient` integration paths. The 14+ parsing branches (no
// choices, no message, unsupported content shape, string vs array text,
// modern `tool_calls` vs legacy `function_call`, audio, usage with/without
// the various detail sub-objects, finish_reasons, logprobs) had no direct
// fast-feedback coverage. A regression that silently dropped a finish
// reason or a token-usage field would have shipped through every
// existing test green.
public sealed class ChatCompletionsResponseReaderTests
{
    private const int LargeCap = 1024 * 1024;
    private const string ResponseLabel = "Test chat response";

    // ---------- Happy path: text content ----------

    [Test]
    public async Task ReadAsync_StringContent_ExtractsText()
    {
        const string body = """
        {
          "choices": [{
            "message": { "content": "hello pal" },
            "finish_reason": "stop"
          }],
          "model": "test-model",
          "id": "test-id"
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("hello pal"));
        Assert.That(result.ResponseModel, Is.EqualTo("test-model"));
        Assert.That(result.ResponseId, Is.EqualTo("test-id"));
        Assert.That(result.FinishReasons, Is.EqualTo(new[] { "stop" }));
        Assert.That(result.ErrorMessage, Is.Empty);
    }

    [Test]
    public async Task ReadAsync_ArrayContentWithTextParts_ConcatenatesParts()
    {
        // OpenAI multimodal-style content array: parts can be strings or
        // {type:"text", text:"..."} objects. The reader must concatenate
        // the text-bearing parts and ignore non-text parts (images, etc).
        const string body = """
        {
          "choices": [{
            "message": {
              "content": [
                { "type": "text", "text": "hello " },
                "world",
                { "type": "image", "image_url": "ignored" },
                { "type": "text", "text": "!" }
              ]
            }
          }]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("hello world!"));
    }

    [Test]
    public async Task ReadAsync_ContentPartWithContentField_ExtractsAsAlternateTextKey()
    {
        // Some providers use `content` instead of `text` inside content
        // parts. The reader must accept both.
        const string body = """
        {
          "choices": [{
            "message": {
              "content": [
                { "type": "text", "content": "alt-field works" }
              ]
            }
          }]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("alt-field works"));
    }

    // ---------- Tool calls ----------

    [Test]
    public async Task ReadAsync_ToolCalls_ExtractedAsRawJson()
    {
        const string body = """
        {
          "choices": [{
            "message": {
              "content": null,
              "tool_calls": [
                {"id":"call_1","type":"function","function":{"name":"f","arguments":"{}"}}
              ]
            }
          }]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.ToolCallsJson, Does.Contain("call_1"));
        Assert.That(result.ToolCallsJson, Does.Contain("function"));
    }

    [Test]
    public async Task ReadAsync_LegacyFunctionCall_StillAccepted()
    {
        // Pre-tools OpenAI API used a single `function_call` object. Some
        // long-running deployments and Foundry Local still emit this shape.
        const string body = """
        {
          "choices": [{
            "message": {
              "content": null,
              "function_call": {"name":"legacy_fn","arguments":"{}"}
            }
          }]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.ToolCallsJson, Does.Contain("legacy_fn"));
    }

    [Test]
    public async Task ReadAsync_EmptyToolCallsArray_DoesNotCountAsToolCalls()
    {
        // `tool_calls: []` is the same as no tool calls. The reader must
        // not treat it as a successful tool-call response on its own.
        const string body = """
        {
          "choices": [{
            "message": {
              "content": "fallback text",
              "tool_calls": []
            }
          }]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("fallback text"));
        Assert.That(result.ToolCallsJson, Is.Empty);
    }

    // ---------- Audio ----------

    [Test]
    public async Task ReadAsync_AudioOutput_ExtractedAsRawJson()
    {
        const string body = """
        {
          "choices": [{
            "message": {
              "audio": {"data":"<base64>","transcript":"hello"}
            }
          }]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.AudioJson, Does.Contain("transcript"));
    }

    // ---------- Failure: missing structure ----------

    [Test]
    public async Task ReadAsync_NoChoicesArray_Fails()
    {
        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent("{}"), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("no choices"));
    }

    [Test]
    public async Task ReadAsync_EmptyChoicesArray_Fails()
    {
        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent("""{"choices": []}"""), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("no choices"));
    }

    [Test]
    public async Task ReadAsync_ChoicesNotArray_Fails()
    {
        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent("""{"choices": "not-an-array"}"""), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("no choices"));
    }

    [Test]
    public async Task ReadAsync_NoMessageInChoice_Fails()
    {
        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent("""{"choices": [{}]}"""), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("unexpected payload"));
    }

    [Test]
    public async Task ReadAsync_MessageWithNoTextOrToolsOrAudio_Fails()
    {
        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent("""{"choices": [{"message": {}}]}"""),
            LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("unsupported"));
    }

    // ---------- Usage parsing ----------

    [Test]
    public async Task ReadAsync_UsageFields_ParsedCorrectly()
    {
        const string body = """
        {
          "choices": [{"message": {"content": "x"}}],
          "usage": {
            "prompt_tokens": 100,
            "completion_tokens": 50,
            "total_tokens": 150,
            "prompt_tokens_details": {
              "cached_tokens": 80,
              "audio_tokens": 5
            },
            "completion_tokens_details": {
              "reasoning_tokens": 20,
              "audio_tokens": 3,
              "accepted_prediction_tokens": 7,
              "rejected_prediction_tokens": 2
            }
          }
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Usage.PromptTokens, Is.EqualTo(100));
        Assert.That(result.Usage.CompletionTokens, Is.EqualTo(50));
        Assert.That(result.Usage.TotalTokens, Is.EqualTo(150));
        Assert.That(result.Usage.CachedPromptTokens, Is.EqualTo(80));
        Assert.That(result.Usage.PromptAudioTokens, Is.EqualTo(5));
        Assert.That(result.Usage.CompletionReasoningTokens, Is.EqualTo(20));
        Assert.That(result.Usage.CompletionAudioTokens, Is.EqualTo(3));
        Assert.That(result.Usage.AcceptedPredictionTokens, Is.EqualTo(7));
        Assert.That(result.Usage.RejectedPredictionTokens, Is.EqualTo(2));
    }

    [Test]
    public async Task ReadAsync_UsageMissingTotal_AutoSumsFromPromptAndCompletion()
    {
        // Some providers omit `total_tokens` and only emit prompt + completion.
        // The reader fills it in so downstream metrics aren't zeroed.
        const string body = """
        {
          "choices": [{"message": {"content": "x"}}],
          "usage": {"prompt_tokens": 30, "completion_tokens": 70}
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Usage.TotalTokens, Is.EqualTo(100));
    }

    [Test]
    public async Task ReadAsync_UsageWithStringTokenCounts_Parsed()
    {
        // A misbehaving provider can emit token counts as strings. The
        // reader must accept that and still extract the numeric value.
        const string body = """
        {
          "choices": [{"message": {"content": "x"}}],
          "usage": {"prompt_tokens": "42", "completion_tokens": "8"}
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Usage.PromptTokens, Is.EqualTo(42));
        Assert.That(result.Usage.CompletionTokens, Is.EqualTo(8));
        Assert.That(result.Usage.TotalTokens, Is.EqualTo(50));
    }

    [Test]
    public async Task ReadAsync_UsageNegativeNumber_ClampedToZero()
    {
        // The reader's `Math.Max(0, parsed)` guard means a negative count
        // (which would be a provider bug) is clamped rather than
        // propagated into downstream metrics.
        const string body = """
        {
          "choices": [{"message": {"content": "x"}}],
          "usage": {"prompt_tokens": -5}
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Usage.PromptTokens, Is.EqualTo(0));
    }

    [Test]
    public async Task ReadAsync_UsageMissing_ReturnsEmpty()
    {
        const string body = """
        {"choices": [{"message": {"content": "x"}}]}
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Usage.PromptTokens, Is.EqualTo(0));
        Assert.That(result.Usage.TotalTokens, Is.EqualTo(0));
    }

    // ---------- Finish reasons ----------

    [Test]
    public async Task ReadAsync_MultipleChoices_ConcatenatesAllFinishReasons()
    {
        const string body = """
        {
          "choices": [
            {"message": {"content": "a"}, "finish_reason": "stop"},
            {"message": {"content": "b"}, "finish_reason": "length"}
          ]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.FinishReasons, Is.EqualTo(new[] { "stop", "length" }));
    }

    [Test]
    public async Task ReadAsync_MissingFinishReasons_ReturnsEmptyArray()
    {
        const string body = """
        {"choices": [{"message": {"content": "x"}}]}
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.FinishReasons, Is.Empty);
    }

    [Test]
    public async Task ReadAsync_BlankFinishReason_Skipped()
    {
        // A whitespace-only or empty `finish_reason` adds no information
        // and is filtered out (rather than poisoning metric labels).
        const string body = """
        {
          "choices": [
            {"message": {"content": "a"}, "finish_reason": "   "},
            {"message": {"content": "b"}, "finish_reason": "stop"}
          ]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.FinishReasons, Is.EqualTo(new[] { "stop" }));
    }

    // ---------- Logprobs + system_fingerprint ----------

    [Test]
    public async Task ReadAsync_Logprobs_ExtractedAsRawJson()
    {
        const string body = """
        {
          "choices": [{
            "message": {"content": "x"},
            "logprobs": {"content": [{"token":"x","logprob":-0.1}]}
          }]
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.LogprobsJson, Does.Contain("\"token\":\"x\""));
    }

    [Test]
    public async Task ReadAsync_SystemFingerprint_Extracted()
    {
        const string body = """
        {
          "choices": [{"message": {"content": "x"}}],
          "system_fingerprint": "fp_abcdef"
        }
        """;

        var result = await ChatCompletionsResponseReader.ReadAsync(
            NewJsonContent(body), LargeCap, ResponseLabel, CancellationToken.None);

        Assert.That(result.SystemFingerprint, Is.EqualTo("fp_abcdef"));
    }

    // ---------- Body-size cap inherited from HttpContentReadLimiter ----------

    [Test]
    public void ReadAsync_OversizedBody_ThrowsViaUnderlyingLimiter()
    {
        // The reader delegates to HttpContentReadLimiter.ParseJsonDocumentAsync
        // for the actual byte read. An over-cap declared length throws
        // InvalidDataException — the caller's responsibility to map that to
        // a ChatResponse with a diagnostic ResponsePath.
        string body = "{\"choices\":[" + new string('a', 8192) + "]}";

        Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            _ = await ChatCompletionsResponseReader.ReadAsync(
                NewJsonContent(body), maxBytes: 1024, ResponseLabel, CancellationToken.None);
        });
    }

    // ---------- Helpers ----------

    private static HttpContent NewJsonContent(string body)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        return content;
    }
}
