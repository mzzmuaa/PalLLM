using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

public sealed class InferenceClientTests
{
    [Test]
    public async Task CompleteAsync_WhenUsingDefaultModelProfile_SendsConfiguredSampling()
    {
        // The shipped default Model string is an operator-overridable placeholder.
        // When the inference client detects a family the runtime has sampling
        // presets for, it fills in DefaultTopP and DefaultPresencePenalty unless
        // the operator has already supplied their own values in InferenceOptions.
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("model").GetString(), Is.EqualTo(options.Inference.Model));
        Assert.That(requestJson.RootElement.GetProperty("max_tokens").GetInt32(), Is.EqualTo(128));
        Assert.That(requestJson.RootElement.TryGetProperty("max_completion_tokens", out _), Is.False);
        Assert.That(requestJson.RootElement.GetProperty("top_p").GetSingle(), Is.EqualTo(0.8f));
        Assert.That(requestJson.RootElement.GetProperty("presence_penalty").GetSingle(), Is.EqualTo(1.5f));
        Assert.That(requestJson.RootElement.TryGetProperty("frequency_penalty", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("top_k", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("min_p", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("repetition_penalty", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("enable_thinking", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("reasoning_effort", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("thinking_token_budget", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("seed", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("priority", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("service_tier", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("prompt_cache_key", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("prompt_cache_retention", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("verbosity", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("safety_identifier", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("store", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("metadata", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("cache_prompt", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("id_slot", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("n_cache_reuse", out _), Is.False);
        Assert.That(handler.LastRequestHeaders.ContainsKey("X-Client-Request-Id"), Is.False);
        Assert.That(handler.LastRequestHeaders.ContainsKey("X-Request-Id"), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("parallel_tool_calls", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("stop", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("tools", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("tool_choice", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("prediction", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("modalities", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("audio", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("mm_processor_kwargs", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("logprobs", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("top_logprobs", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("response_format", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("structured_outputs", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("cache_salt", out _), Is.False);
        JsonElement userMessage = requestJson.RootElement.GetProperty("messages")[1];
        Assert.That(userMessage.GetProperty("content").ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(userMessage.GetProperty("content").GetString(), Is.EqualTo("user"));
        Assert.That(result.AudioJson, Is.Empty);
    }

    [Test]
    public async Task CompleteAsync_WhenOptionalRequestHintsConfigured_ForwardsValues()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.ReasoningEffort = " HIGH ";
        options.Inference.FrequencyPenalty = 0.65f;
        options.Inference.TopK = 40;
        options.Inference.MinP = 0.05f;
        options.Inference.RepetitionPenalty = 1.08f;
        options.Inference.ServiceTier = " SCALE ";
        options.Inference.PromptCacheKey = " pal-save-alpha ";
        options.Inference.PromptCacheRetention = " 24H ";
        options.Inference.Verbosity = " LOW ";
        options.Inference.SafetyIdentifier = " pal-profile-hash-001 ";
        options.Inference.StoreCompletions = false;
        options.Inference.RequestMetadata[" pal_surface "] = " release-proof ";
        options.Inference.RequestMetadata["pal_canary"] = " metadata ";
        options.Inference.ClientRequestIdHeader = " X-Client-Request-Id ";
        options.Inference.LlamaCppCachePrompt = true;
        options.Inference.LlamaCppSlotId = 2;
        options.Inference.LlamaCppCacheReuseTokens = 768;
        options.Inference.ThinkingTokenBudget = 256;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
            ClientRequestId = " pal-chat-001 ",
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("reasoning_effort").GetString(), Is.EqualTo("high"));
        Assert.That(requestJson.RootElement.GetProperty("thinking_token_budget").GetInt32(), Is.EqualTo(256));
        Assert.That(requestJson.RootElement.GetProperty("frequency_penalty").GetSingle(), Is.EqualTo(0.65f));
        Assert.That(requestJson.RootElement.GetProperty("top_k").GetInt32(), Is.EqualTo(40));
        Assert.That(requestJson.RootElement.GetProperty("min_p").GetSingle(), Is.EqualTo(0.05f));
        Assert.That(requestJson.RootElement.GetProperty("repetition_penalty").GetSingle(), Is.EqualTo(1.08f));
        Assert.That(requestJson.RootElement.GetProperty("service_tier").GetString(), Is.EqualTo("scale"));
        Assert.That(requestJson.RootElement.GetProperty("prompt_cache_key").GetString(), Is.EqualTo("pal-save-alpha"));
        Assert.That(requestJson.RootElement.GetProperty("prompt_cache_retention").GetString(), Is.EqualTo("24h"));
        Assert.That(requestJson.RootElement.GetProperty("verbosity").GetString(), Is.EqualTo("low"));
        Assert.That(requestJson.RootElement.GetProperty("safety_identifier").GetString(), Is.EqualTo("pal-profile-hash-001"));
        Assert.That(requestJson.RootElement.GetProperty("store").GetBoolean(), Is.False);
        JsonElement metadata = requestJson.RootElement.GetProperty("metadata");
        Assert.That(metadata.GetProperty("pal_surface").GetString(), Is.EqualTo("release-proof"));
        Assert.That(metadata.GetProperty("pal_canary").GetString(), Is.EqualTo("metadata"));
        Assert.That(requestJson.RootElement.GetProperty("cache_prompt").GetBoolean(), Is.True);
        Assert.That(requestJson.RootElement.GetProperty("id_slot").GetInt32(), Is.EqualTo(2));
        Assert.That(requestJson.RootElement.GetProperty("n_cache_reuse").GetInt32(), Is.EqualTo(768));
        Assert.That(handler.LastRequestHeaders["x-client-request-id"], Is.EqualTo("pal-chat-001"));
    }

    [Test]
    public async Task CompleteAsync_WhenPromptOverridesOptionalHints_UsesPromptValues()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.ReasoningEffort = "low";
        options.Inference.FrequencyPenalty = 1.25f;
        options.Inference.TopK = 64;
        options.Inference.MinP = 0.15f;
        options.Inference.RepetitionPenalty = 1.2f;
        options.Inference.ServiceTier = "flex";
        options.Inference.PromptCacheKey = "configured-cache-key";
        options.Inference.PromptCacheRetention = "in_memory";
        options.Inference.Verbosity = "medium";
        options.Inference.SafetyIdentifier = "configured-safety-id";
        options.Inference.StoreCompletions = true;
        options.Inference.RequestMetadata["pal_surface"] = "configured";
        options.Inference.RequestMetadata["shared"] = "configured";
        options.Inference.ClientRequestIdHeader = "X-Request-Id";
        options.Inference.LlamaCppCachePrompt = true;
        options.Inference.LlamaCppSlotId = 1;
        options.Inference.LlamaCppCacheReuseTokens = 384;
        options.Inference.ThinkingTokenBudget = 512;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
            ReasoningEffort = "xhigh",
            FrequencyPenalty = -0.25f,
            TopK = 20,
            MinP = 0.01f,
            RepetitionPenalty = 0.95f,
            ServiceTier = " DEFAULT ",
            PromptCacheKey = " prompt-cache-key ",
            PromptCacheRetention = " 24H ",
            Verbosity = " HIGH ",
            SafetyIdentifier = " prompt-safety-id ",
            StoreCompletions = false,
            RequestMetadata = new Dictionary<string, string>
            {
                [" pal_surface "] = " prompt ",
                ["pal_route"] = " chat ",
            },
            ClientRequestId = "prompt-client-request-id",
            LlamaCppCachePrompt = false,
            LlamaCppSlotId = 3,
            LlamaCppCacheReuseTokens = 0,
            ThinkingTokenBudget = 64,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("reasoning_effort").GetString(), Is.EqualTo("xhigh"));
        Assert.That(requestJson.RootElement.GetProperty("thinking_token_budget").GetInt32(), Is.EqualTo(64));
        Assert.That(requestJson.RootElement.GetProperty("frequency_penalty").GetSingle(), Is.EqualTo(-0.25f));
        Assert.That(requestJson.RootElement.GetProperty("top_k").GetInt32(), Is.EqualTo(20));
        Assert.That(requestJson.RootElement.GetProperty("min_p").GetSingle(), Is.EqualTo(0.01f));
        Assert.That(requestJson.RootElement.GetProperty("repetition_penalty").GetSingle(), Is.EqualTo(0.95f));
        Assert.That(requestJson.RootElement.GetProperty("service_tier").GetString(), Is.EqualTo("default"));
        Assert.That(requestJson.RootElement.GetProperty("prompt_cache_key").GetString(), Is.EqualTo("prompt-cache-key"));
        Assert.That(requestJson.RootElement.GetProperty("prompt_cache_retention").GetString(), Is.EqualTo("24h"));
        Assert.That(requestJson.RootElement.GetProperty("verbosity").GetString(), Is.EqualTo("high"));
        Assert.That(requestJson.RootElement.GetProperty("safety_identifier").GetString(), Is.EqualTo("prompt-safety-id"));
        Assert.That(requestJson.RootElement.GetProperty("store").GetBoolean(), Is.False);
        JsonElement metadata = requestJson.RootElement.GetProperty("metadata");
        Assert.That(metadata.GetProperty("pal_surface").GetString(), Is.EqualTo("prompt"));
        Assert.That(metadata.GetProperty("shared").GetString(), Is.EqualTo("configured"));
        Assert.That(metadata.GetProperty("pal_route").GetString(), Is.EqualTo("chat"));
        Assert.That(requestJson.RootElement.GetProperty("cache_prompt").GetBoolean(), Is.False);
        Assert.That(requestJson.RootElement.GetProperty("id_slot").GetInt32(), Is.EqualTo(3));
        Assert.That(requestJson.RootElement.GetProperty("n_cache_reuse").GetInt32(), Is.EqualTo(0));
        Assert.That(handler.LastRequestHeaders["x-request-id"], Is.EqualTo("prompt-client-request-id"));
    }

    [Test]
    public async Task CompleteAsync_WhenTokenBudgetFieldUsesMaxCompletionTokens_EmitsOnlyNewField()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.TokenBudgetField = " max_completion_tokens ";

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.TryGetProperty("max_tokens", out _), Is.False);
        Assert.That(requestJson.RootElement.GetProperty("max_completion_tokens").GetInt32(), Is.EqualTo(128));
    }

    [Test]
    public async Task CompleteAsync_WhenPromptOverridesTokenBudgetField_UsesPromptField()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.TokenBudgetField = InferenceTokenBudgetFields.MaxCompletionTokens;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 64,
            TokenBudgetField = InferenceTokenBudgetFields.MaxTokens,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("max_tokens").GetInt32(), Is.EqualTo(64));
        Assert.That(requestJson.RootElement.TryGetProperty("max_completion_tokens", out _), Is.False);
    }

    [Test]
    public async Task CompleteAsync_WhenSeedConfigured_ForwardsSeed()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.Seed = 4242;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("seed").GetInt32(), Is.EqualTo(4242));
    }

    [Test]
    public async Task CompleteAsync_WhenRequestPriorityConfigured_ForwardsPriority()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.RequestPriority = -10;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("priority").GetInt32(), Is.EqualTo(-10));
    }

    [Test]
    public async Task CompleteAsync_WhenPromptOverridesRequestPriority_UsesPromptPriority()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.RequestPriority = 50;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
            RequestPriority = 5,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("priority").GetInt32(), Is.EqualTo(5));
    }

    [Test]
    public async Task CompleteAsync_WhenParallelToolCallsConfigured_ForwardsValue()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.ParallelToolCalls = false;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("parallel_tool_calls").GetBoolean(), Is.False);
    }

    [Test]
    public async Task CompleteAsync_WhenPromptOverridesParallelToolCalls_UsesPromptValue()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.ParallelToolCalls = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
            ParallelToolCalls = false,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("parallel_tool_calls").GetBoolean(), Is.False);
    }

    [Test]
    public async Task CompleteAsync_WhenPromptOverridesSeed_UsesPromptSeed()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.Seed = 111;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
            Seed = 222,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("seed").GetInt32(), Is.EqualTo(222));
    }

    [Test]
    public async Task CompleteAsync_WhenStopSequencesConfigured_ForwardsTrimmedArrayAndPromptCanOverride()
    {
        using (var handler = new RecordingHandler())
        using (var httpClient = new HttpClient(handler))
        {
            var options = new PalLlmOptions();
            options.Inference.Enabled = true;
            options.Inference.StopSequences.Add("</pal-action>");
            options.Inference.StopSequences.Add(" END ");

            var client = new HttpJsonInferenceClient(httpClient, options);

            InferenceResult result = await client.CompleteAsync(new InferencePrompt
            {
                SystemPrompt = "system",
                UserPrompt = "user",
                Temperature = 0.7f,
                MaxTokens = 128,
            }, CancellationToken.None);

            using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

            Assert.That(result.Success, Is.True);
            JsonElement stop = requestJson.RootElement.GetProperty("stop");
            Assert.That(stop.EnumerateArray().Select(item => item.GetString()).ToArray(),
                Is.EqualTo(new[] { "</pal-action>", "END" }));
        }

        using (var handler = new RecordingHandler())
        using (var httpClient = new HttpClient(handler))
        {
            var options = new PalLlmOptions();
            options.Inference.Enabled = true;
            options.Inference.StopSequences.Add("configured");

            var client = new HttpJsonInferenceClient(httpClient, options);

            InferenceResult result = await client.CompleteAsync(new InferencePrompt
            {
                SystemPrompt = "system",
                UserPrompt = "user",
                Temperature = 0.7f,
                MaxTokens = 128,
                StopSequences = ["prompt-stop"],
            }, CancellationToken.None);

            using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

            Assert.That(result.Success, Is.True);
            JsonElement stop = requestJson.RootElement.GetProperty("stop");
            Assert.That(stop.EnumerateArray().Select(item => item.GetString()).ToArray(),
                Is.EqualTo(new[] { "prompt-stop" }));
        }

        using (var handler = new RecordingHandler())
        using (var httpClient = new HttpClient(handler))
        {
            var options = new PalLlmOptions();
            options.Inference.Enabled = true;
            options.Inference.StopSequences.Add("configured");

            var client = new HttpJsonInferenceClient(httpClient, options);

            InferenceResult result = await client.CompleteAsync(new InferencePrompt
            {
                SystemPrompt = "system",
                UserPrompt = "user",
                Temperature = 0.7f,
                MaxTokens = 128,
                StopSequences = [],
            }, CancellationToken.None);

            using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

            Assert.That(result.Success, Is.True);
            Assert.That(requestJson.RootElement.TryGetProperty("stop", out _), Is.False);
        }
    }

    [Test]
    public async Task CompleteAsync_WhenPromptCarriesResponseFormat_ForwardsJsonSchemaAndMarksJsonTelemetry()
    {
        using var telemetry = new GenAiTelemetryCapture();
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);
        using JsonDocument responseFormat = JsonDocument.Parse(
            """
            {
              "type": "json_schema",
              "json_schema": {
                "name": "pal_action",
                "strict": true,
                "schema": {
                  "type": "object",
                  "additionalProperties": false,
                  "properties": {
                    "intent": { "type": "string" }
                  },
                  "required": [ "intent" ]
                }
              }
            }
            """);
        using JsonDocument structuredOutputs = JsonDocument.Parse(
            """
            {
              "choice": [ "gather", "guard" ]
            }
            """);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "Return JSON.",
            UserPrompt = "capture intent",
            Temperature = 0.1f,
            MaxTokens = 64,
            ResponseFormat = responseFormat.RootElement.Clone(),
            StructuredOutputs = structuredOutputs.RootElement.Clone(),
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        JsonElement forwarded = requestJson.RootElement.GetProperty("response_format");
        Assert.That(forwarded.GetProperty("type").GetString(), Is.EqualTo("json_schema"));
        Assert.That(forwarded.GetProperty("json_schema").GetProperty("name").GetString(), Is.EqualTo("pal_action"));
        Assert.That(forwarded.GetProperty("json_schema").GetProperty("strict").GetBoolean(), Is.True);
        JsonElement structured = requestJson.RootElement.GetProperty("structured_outputs");
        Assert.That(
            structured.GetProperty("choice").EnumerateArray().Select(value => value.GetString()).ToArray(),
            Is.EqualTo(new[] { "gather", "guard" }));
        Assert.That(telemetry.Activities.Single().GetTagItem("gen_ai.output.type"), Is.EqualTo("json"));
    }

    [Test]
    public async Task CompleteAsync_WhenPromptCarriesPrediction_ForwardsPredictedOutput()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        using JsonDocument prediction = JsonDocument.Parse(
            """
            {
              "type": "content",
              "content": "Return only this stable proof scaffold unless the live state changed."
            }
            """);

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "Regenerate the proof scaffold.",
            UserPrompt = "No state change.",
            Temperature = 0.1f,
            MaxTokens = 64,
            Prediction = prediction.RootElement.Clone(),
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        JsonElement forwarded = requestJson.RootElement.GetProperty("prediction");
        Assert.That(forwarded.GetProperty("type").GetString(), Is.EqualTo("content"));
        Assert.That(forwarded.GetProperty("content").GetString(), Does.Contain("stable proof scaffold"));
    }

    [Test]
    public async Task CompleteAsync_WhenPromptCarriesUserContent_ForwardsMultimodalContentParts()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MultimodalProcessor.MinPixels = 28 * 28;
        options.Inference.MultimodalProcessor.MaxPixels = 1280 * 28 * 28;
        options.Inference.MultimodalProcessor.MaxSoftTokens = 280;
        options.Inference.MultimodalProcessor.Fps = 1;

        using JsonDocument userContent = JsonDocument.Parse(
            """
            [
              {
                "type": "text",
                "text": "Summarize this field recording and call out safety risks."
              },
              {
                "type": "input_audio",
                "input_audio": {
                  "data": "UklGRgAAAAA=",
                  "format": "wav"
                }
              },
              {
                "type": "video_url",
                "video_url": {
                  "url": "data:video/mp4;base64,AAAA"
                }
              }
            ]
            """);

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "Use the supplied multimodal evidence only.",
            UserPrompt = "fallback text",
            Temperature = 0.1f,
            MaxTokens = 64,
            UserContent = userContent.RootElement.Clone(),
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        JsonElement messages = requestJson.RootElement.GetProperty("messages");
        Assert.That(messages[0].GetProperty("content").GetString(), Is.EqualTo("Use the supplied multimodal evidence only."));
        JsonElement forwarded = messages[1].GetProperty("content");
        Assert.That(forwarded.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(forwarded[0].GetProperty("type").GetString(), Is.EqualTo("text"));
        Assert.That(forwarded[0].GetProperty("text").GetString(), Does.Contain("field recording"));
        Assert.That(forwarded[1].GetProperty("type").GetString(), Is.EqualTo("input_audio"));
        Assert.That(forwarded[1].GetProperty("input_audio").GetProperty("format").GetString(), Is.EqualTo("wav"));
        Assert.That(forwarded[1].GetProperty("uuid").GetString(),
            Is.EqualTo("palllm-audio-sha256-48db9ef8d4d6e57a94b2e64f88b144e6"),
            "Route-owned input_audio parts should carry stable ids for repeated multimodal proof replays.");
        Assert.That(forwarded[2].GetProperty("type").GetString(), Is.EqualTo("video_url"));
        Assert.That(forwarded[2].GetProperty("video_url").GetProperty("url").GetString(), Does.StartWith("data:video/mp4;base64,"));
        Assert.That(forwarded[2].GetProperty("uuid").GetString(),
            Is.EqualTo("palllm-video-sha256-f786f55fdd1733a0a89a114a03636429"),
            "Route-owned local video data URLs should carry stable ids without relying on remote fetches.");
        JsonElement processorKwargs = requestJson.RootElement.GetProperty("mm_processor_kwargs");
        Assert.That(processorKwargs.GetProperty("min_pixels").GetInt32(), Is.EqualTo(28 * 28));
        Assert.That(processorKwargs.GetProperty("max_pixels").GetInt32(), Is.EqualTo(1280 * 28 * 28));
        Assert.That(processorKwargs.GetProperty("max_soft_tokens").GetInt32(), Is.EqualTo(280));
        Assert.That(processorKwargs.GetProperty("fps").GetSingle(), Is.EqualTo(1.0f));

        using var strictHandler = new RecordingHandler();
        using var strictHttpClient = new HttpClient(strictHandler);
        var strictOptions = new PalLlmOptions();
        strictOptions.Inference.Enabled = true;
        strictOptions.Inference.UseMediaCacheIds = false;

        var strictClient = new HttpJsonInferenceClient(strictHttpClient, strictOptions);

        await strictClient.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "Use the supplied multimodal evidence only.",
            UserPrompt = "fallback text",
            Temperature = 0.1f,
            MaxTokens = 64,
            UserContent = userContent.RootElement.Clone(),
        }, CancellationToken.None);

        using JsonDocument strictRequestJson = JsonDocument.Parse(strictHandler.LastRequestBody);
        JsonElement strictForwarded = strictRequestJson.RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.That(strictForwarded[1].TryGetProperty("uuid", out _), Is.False,
            "Strict endpoints can opt out of vLLM-style media-cache ids for prompt-level UserContent.");
        Assert.That(strictForwarded[2].TryGetProperty("uuid", out _), Is.False,
            "Strict endpoints should receive the caller's content-part array without injected uuid fields.");
        Assert.That(strictRequestJson.RootElement.TryGetProperty("mm_processor_kwargs", out _), Is.False,
            "Strict endpoints should not see multimodal processor hints unless the operator explicitly configures them.");
    }

    [Test]
    public async Task CompleteAsync_WhenPromptRequestsLogprobs_ForwardsConfidenceRequest()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "Score the next proof token.",
            UserPrompt = "Return one safe word.",
            Temperature = 0.1f,
            MaxTokens = 4,
            TopLogprobs = 5,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("logprobs").GetBoolean(), Is.True);
        Assert.That(requestJson.RootElement.GetProperty("top_logprobs").GetInt32(), Is.EqualTo(5));
    }

    [Test]
    public async Task CompleteAsync_WhenPromptRequestsAudioOutput_ForwardsModalitiesAudioAndPreservesReceipt()
    {
        using var handler = new RecordingHandler(
            """
            {
              "id": "chatcmpl-audio-1",
              "model": "omni-local",
              "choices": [
                {
                  "finish_reason": "stop",
                  "message": {
                    "role": "assistant",
                    "content": "Meet at the base gate."
                  }
                },
                {
                  "finish_reason": "stop",
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "audio": {
                      "id": "audio_1",
                      "data": "UklGRgAAAAA=",
                      "expires_at": 1778486400
                    }
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 12,
                "completion_tokens": 4,
                "total_tokens": 16
              }
            }
            """);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        using JsonDocument audio = JsonDocument.Parse(
            """
            {
              "format": "wav",
              "voice": "pal_local"
            }
            """);

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "Return text plus audio for this canary.",
            UserPrompt = "Say where to meet.",
            Temperature = 0.1f,
            MaxTokens = 64,
            Modalities = [" text ", "audio", "audio", "video"],
            Audio = audio.RootElement.Clone(),
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("Meet at the base gate."));
        Assert.That(requestJson.RootElement.GetProperty("modalities").EnumerateArray().Select(item => item.GetString()).ToArray(),
            Is.EqualTo(new[] { "text", "audio" }));
        Assert.That(requestJson.RootElement.GetProperty("audio").GetProperty("format").GetString(), Is.EqualTo("wav"));
        Assert.That(requestJson.RootElement.GetProperty("audio").GetProperty("voice").GetString(), Is.EqualTo("pal_local"));

        using JsonDocument receipt = JsonDocument.Parse(result.AudioJson);
        Assert.That(receipt.RootElement.GetProperty("id").GetString(), Is.EqualTo("audio_1"));
        Assert.That(receipt.RootElement.GetProperty("data").GetString(), Is.EqualTo("UklGRgAAAAA="));
        Assert.That(result.Usage, Is.EqualTo(new TokenUsage(12, 4, 16)));
    }

    [Test]
    public async Task CompleteAsync_WhenPromptCarriesToolsAndToolChoice_ForwardsAndParsesToolCallReceipt()
    {
        using var handler = new RecordingHandler(
            """
            {
              "id": "chatcmpl-tool-1",
              "model": "worker-tool",
              "choices": [
                {
                  "finish_reason": "tool_calls",
                  "message": {
                    "role": "assistant",
                    "content": null,
                    "tool_calls": [
                      {
                        "id": "call_1",
                        "type": "function",
                        "function": {
                          "name": "pal_stage_action",
                          "arguments": "{\"intent\":\"waypoint_suggest\"}"
                        }
                      }
                    ]
                  }
                }
              ],
              "usage": {
                "prompt_tokens": 14,
                "completion_tokens": 6,
                "total_tokens": 20
              }
            }
            """);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        using JsonDocument tools = JsonDocument.Parse(
            """
            [
              {
                "type": "function",
                "function": {
                  "name": "pal_stage_action",
                  "description": "Stage one guarded action intent for PalLLM review.",
                  "parameters": {
                    "type": "object",
                    "additionalProperties": false,
                    "properties": {
                      "intent": { "type": "string" }
                    },
                    "required": [ "intent" ]
                  }
                }
              }
            ]
            """);
        using JsonDocument toolChoice = JsonDocument.Parse(
            """
            {
              "type": "function",
              "function": {
                "name": "pal_stage_action"
              }
            }
            """);

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "Return a tool call only.",
            UserPrompt = "Suggest a waypoint action.",
            Temperature = 0.1f,
            MaxTokens = 64,
            Tools = tools.RootElement.Clone(),
            ToolChoice = toolChoice.RootElement.Clone(),
            ParallelToolCalls = false,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.Empty);
        Assert.That(result.ToolCallsJson, Does.Contain("pal_stage_action"));
        Assert.That(result.ToolCallsJson, Does.Contain("waypoint_suggest"));
        Assert.That(result.Usage.TotalTokens, Is.EqualTo(20));
        Assert.That(requestJson.RootElement.GetProperty("parallel_tool_calls").GetBoolean(), Is.False);
        Assert.That(requestJson.RootElement.GetProperty("tools")[0].GetProperty("type").GetString(), Is.EqualTo("function"));
        Assert.That(requestJson.RootElement.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString(),
            Is.EqualTo("pal_stage_action"));
    }

    [Test]
    public void CircuitBreaker_OpensAfterFailureThresholdAndRecoversAfterCooldown()
    {
        var breaker = new InferenceCircuitBreaker
        {
            FailureThreshold = 3,
            Cooldown = TimeSpan.FromMilliseconds(50),
        };

        Assert.That(breaker.ShouldAllowCall(), Is.True, "Closed breaker permits calls.");

        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.That(breaker.State, Is.EqualTo(CircuitState.Closed),
            "Two failures below the threshold keep the breaker closed.");
        Assert.That(breaker.ShouldAllowCall(), Is.True);

        breaker.RecordFailure();
        Assert.That(breaker.State, Is.EqualTo(CircuitState.Open),
            "Crossing the threshold opens the breaker.");
        Assert.That(breaker.ShouldAllowCall(), Is.False,
            "Open breaker must short-circuit calls immediately.");

        Thread.Sleep(80);  // wait out the cooldown

        Assert.That(breaker.ShouldAllowCall(), Is.True,
            "After cooldown the breaker transitions to half-open and allows one trial.");
        Assert.That(breaker.State, Is.EqualTo(CircuitState.HalfOpen));

        breaker.RecordSuccess();
        Assert.That(breaker.State, Is.EqualTo(CircuitState.Closed),
            "A successful trial in half-open state closes the breaker.");
        Assert.That(breaker.ConsecutiveFailures, Is.Zero);
    }

    [Test]
    public void CircuitBreaker_HalfOpenFailure_ReopensImmediately()
    {
        var breaker = new InferenceCircuitBreaker
        {
            FailureThreshold = 2,
            Cooldown = TimeSpan.FromMilliseconds(30),
        };

        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.That(breaker.State, Is.EqualTo(CircuitState.Open));

        Thread.Sleep(50);
        Assert.That(breaker.State, Is.EqualTo(CircuitState.HalfOpen));

        // A failure during the trial re-opens the breaker, even if the failure
        // counter on its own wouldn't cross the threshold again.
        breaker.RecordFailure();
        Assert.That(breaker.State, Is.EqualTo(CircuitState.Open));
    }

    [Test]
    public async Task CompleteAsync_WhenHostMatchesThinkingToggleMarker_SendsThinkingSwitch()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
        options.Inference.Model = "qwen3.5-plus";
        options.Inference.EnableThinking = false;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("model").GetString(), Is.EqualTo("qwen3.5-plus"));
        Assert.That(requestJson.RootElement.GetProperty("enable_thinking").GetBoolean(), Is.False);
    }

    [Test]
    public async Task CompleteAsync_WithThermalGateRejecting_ShortCircuitsToFallback()
    {
        // When the opt-in thermal gate reports Reject, the client must skip
        // the HTTP round-trip entirely and hand the caller a structured
        // failure tagged "thermal_gated" so the runtime can route to the
        // deterministic fallback director without paying the round-trip
        // latency on an already-throttled card.
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.ThermalGate.Enabled = true;
        options.Inference.ThermalGate.RejectAboveC = 83.0;
        options.Inference.ThermalGate.WarnAboveC = 78.0;

        var hotGate = new ThermalGate(
            () => new ThermalSample(90.0, "test", DateTimeOffset.UtcNow),
            () => DateTimeOffset.UtcNow);

        var client = new HttpJsonInferenceClient(httpClient, options, tierOrchestrator: null, thermalGate: hotGate);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 64,
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False, "Rejecting thermal gate must short-circuit the call.");
        Assert.That(result.ErrorType, Is.EqualTo("thermal_gated"));
        Assert.That(result.LatencyMs, Is.EqualTo(0),
            "Short-circuit path must not incur measurable network latency.");
        Assert.That(handler.LastRequestBody, Is.Empty, "No HTTP request must be sent when gated.");
    }

    [Test]
    public async Task CompleteAsync_WithThermalGateAllowing_LetsCallThrough()
    {
        // Temperature well under the warn threshold -> normal inference path.
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.ThermalGate.Enabled = true;
        options.Inference.ThermalGate.RejectAboveC = 83.0;
        options.Inference.ThermalGate.WarnAboveC = 78.0;

        var coolGate = new ThermalGate(
            () => new ThermalSample(55.0, "test", DateTimeOffset.UtcNow),
            () => DateTimeOffset.UtcNow);

        var client = new HttpJsonInferenceClient(httpClient, options, tierOrchestrator: null, thermalGate: coolGate);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 64,
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(handler.LastRequestBody, Is.Not.Empty,
            "An allowing thermal gate must let the normal inference round-trip happen.");
    }

    [Test]
    public async Task CompleteAsync_WithThermalGateDisabled_BehavesLikeNoGate()
    {
        // Default config: thermal gate off -> no sampling overhead, no gating.
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        // ThermalGate.Enabled left at its default false.

        var client = new HttpJsonInferenceClient(httpClient, options);

        Assert.That(client.ThermalGate, Is.Null, "Disabled gate must not be constructed.");

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 64,
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(handler.LastRequestBody, Is.Not.Empty);
    }

    [Test]
    public async Task CompleteAsync_WhenPromptOverridesSamplingAndThinking_UsesPromptValues()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
        options.Inference.EnableThinking = false;
        options.Inference.TopP = 0.8f;
        options.Inference.PresencePenalty = 1.5f;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.55f,
            MaxTokens = 192,
            TopP = 0.61f,
            PresencePenalty = 0.25f,
            EnableThinking = true,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("top_p").GetSingle(), Is.EqualTo(0.61f));
        Assert.That(requestJson.RootElement.GetProperty("presence_penalty").GetSingle(), Is.EqualTo(0.25f));
        Assert.That(requestJson.RootElement.GetProperty("enable_thinking").GetBoolean(), Is.True);
    }

    [Test]
    public async Task CompleteAsync_WhenQwenUsesGenericOpenAiHost_SendsChatTemplateThinkingControls()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://127.0.0.1:11434/v1/";
        options.Inference.Model = "Qwen/Qwen3.6-27B";
        options.Inference.PrefixCacheSalt = "pal-save-alpha";

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.55f,
            MaxTokens = 192,
            EnableThinking = false,
            PreserveThinking = true,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.TryGetProperty("enable_thinking", out _), Is.False);
        Assert.That(requestJson.RootElement.TryGetProperty("preserve_thinking", out _), Is.False);
        Assert.That(requestJson.RootElement.GetProperty("cache_salt").GetString(), Is.EqualTo("pal-save-alpha"));
        Assert.That(requestJson.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(), Is.False);
        Assert.That(requestJson.RootElement.GetProperty("chat_template_kwargs").GetProperty("preserve_thinking").GetBoolean(), Is.True);
    }

    [Test]
    public async Task CompleteAsync_WhenQwenUsesDashScopeHost_SendsRootThinkingControls()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "https://dashscope-intl.aliyuncs.com/compatible-mode/v1";
        options.Inference.Model = "Qwen/Qwen3.6-27B";

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.55f,
            MaxTokens = 192,
            EnableThinking = true,
            PreserveThinking = true,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("enable_thinking").GetBoolean(), Is.True);
        Assert.That(requestJson.RootElement.GetProperty("preserve_thinking").GetBoolean(), Is.True);
        Assert.That(requestJson.RootElement.TryGetProperty("chat_template_kwargs", out _), Is.False);
    }

    [Test]
    public async Task CompleteAsync_WhenLmStudioCompatConfigured_SendsTtlResidencyHint()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://127.0.0.1:1234/v1/";
        options.Inference.ResidencyTtlSeconds = 900;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.GetProperty("ttl").GetInt32(), Is.EqualTo(900));
    }

    [Test]
    public async Task CompleteAsync_WhenResidencyProviderDisabled_SuppressesLmStudioTtlHint()
    {
        using var handler = new RecordingHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://127.0.0.1:1234/v1/";
        options.Inference.ResidencyProvider = InferenceResidencyProvider.Disabled;
        options.Inference.ResidencyTtlSeconds = 900;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.7f,
            MaxTokens = 128,
        }, CancellationToken.None);

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);

        Assert.That(result.Success, Is.True);
        Assert.That(requestJson.RootElement.TryGetProperty("ttl", out _), Is.False);
    }

    // Pass 346: WarmAsync_WhenAutoDetectedOllama_UsesNativeChatPreloadWithKeepAlive
    // deleted alongside the Ollama back-compat path. The dedicated
    // /api/chat preload transport and Ollama keep_alive request body
    // no longer exist — every supported runtime now warms via the
    // generic OpenAI-compatible chat-completions path covered by
    // WarmAsync_WhenLmStudioResidency_AppliesTtl and the negative
    // WarmAsync_WhenResidencyDisabled_DoesNotApplyTtl tests above.

    [Test]
    public async Task CompleteAsync_TransientFailure_IsRetriedAndSucceeds()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("transient blip", Encoding.UTF8, "text/plain"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"second try\"}}]}",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MaxTransientRetries = 1;
        options.Inference.TransientRetryBackoffMs = 5;  // keep the test fast

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True,
            "Transient 5xx must be retried; the second attempt succeeds.");
        Assert.That(result.Content, Is.EqualTo("second try"));
        Assert.That(handler.CallCount, Is.EqualTo(2));
    }

    [Test]
    public async Task CompleteAsync_WhenEndpointReturnsContentPartArray_ConcatenatesTextParts()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hello\"},{\"type\":\"text\",\"text\":\" world\"}]}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("hello world"));
        Assert.That(result.Usage, Is.EqualTo(new TokenUsage(12, 3, 15)));
    }

    [Test]
    public async Task CompleteAsync_WhenUsageTokensAreNumericStrings_ParsesTokenUsage()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"usage ok\"}}],\"usage\":{\"prompt_tokens\":\"12\",\"completion_tokens\":\"3\",\"total_tokens\":\"15\"}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Usage, Is.EqualTo(new TokenUsage(12, 3, 15)));
    }

    [Test]
    public async Task CompleteAsync_WhenUsageTokensAreInvalidOrNegative_ClampsAndFallsBackTotal()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"usage ok\"}}],\"usage\":{\"prompt_tokens\":\"-4\",\"completion_tokens\":6,\"total_tokens\":\"unknown\"}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Usage, Is.EqualTo(new TokenUsage(0, 6, 6)));
    }

    [Test]
    public async Task CompleteAsync_WhenEndpointReturnsResponseReceipts_PreservesReplayReceipts()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"id\":\"chatcmpl-local-001\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"fingerprinted\"}}],\"model\":\"worker-q4\",\"system_fingerprint\":\"fp_local_replay_001\",\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":2,\"total_tokens\":9,\"prompt_tokens_details\":{\"cached_tokens\":4,\"audio_tokens\":1},\"completion_tokens_details\":{\"reasoning_tokens\":1,\"audio_tokens\":2,\"accepted_prediction_tokens\":3,\"rejected_prediction_tokens\":5}}}",
                Encoding.UTF8,
                "application/json"),
        };
        response.Headers.Add("x-request-id", "req_local_001");
        response.Headers.Add("openai-processing-ms", "127.5");
        response.Headers.Add("Server-Timing", "queue;dur=3.25, ttft;dur=41.5, prefill;dur=22.75, decode;dur=63.5");
        var handler = new ScriptedHandler(response);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.ResponseModel, Is.EqualTo("worker-q4"));
        Assert.That(result.ResponseId, Is.EqualTo("chatcmpl-local-001"));
        Assert.That(result.UpstreamRequestId, Is.EqualTo("req_local_001"));
        Assert.That(result.UpstreamProcessingMs, Is.EqualTo(127.5));
        Assert.That(result.UpstreamQueueMs, Is.EqualTo(3.25));
        Assert.That(result.UpstreamTimeToFirstTokenMs, Is.EqualTo(41.5));
        Assert.That(result.UpstreamPrefillMs, Is.EqualTo(22.75));
        Assert.That(result.UpstreamDecodeMs, Is.EqualTo(63.5));
        Assert.That(result.SystemFingerprint, Is.EqualTo("fp_local_replay_001"));
        Assert.That(result.FinishReasons, Is.EqualTo(new[] { "stop" }));
        Assert.That(result.Usage, Is.EqualTo(new TokenUsage(7, 2, 9, 4, 1, 1, 2, 3, 5)));
    }

    [Test]
    public async Task CompleteAsync_WhenEndpointFailsWithRequestIdHeader_PreservesUpstreamRequestId()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":\"busy\"}", Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("x-request-id", "req_rate_limited_001");
        response.Headers.Add("Server-Timing", "cache;dur=2, queue;dur=5.5, inference;dur=234.75;desc=\"model\", time_to_first_token;dur=50, request_prefill;dur=33, request_decode;dur=101");
        var handler = new ScriptedHandler(response);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MaxTransientRetries = 0;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("429"));
        Assert.That(result.UpstreamRequestId, Is.EqualTo("req_rate_limited_001"));
        Assert.That(result.UpstreamProcessingMs, Is.EqualTo(234.75));
        Assert.That(result.UpstreamQueueMs, Is.EqualTo(5.5));
        Assert.That(result.UpstreamTimeToFirstTokenMs, Is.EqualTo(50));
        Assert.That(result.UpstreamPrefillMs, Is.EqualTo(33));
        Assert.That(result.UpstreamDecodeMs, Is.EqualTo(101));
    }

    [Test]
    public void HttpResponseReceiptExtractor_WhenHeaderIsUnsafe_SuppressesUpstreamRequestId()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("x-request-id", "req-with-\t-tab");
        response.Headers.TryAddWithoutValidation("x-correlation-id", new string('a', 129));

        string receipt = HttpResponseReceiptExtractor.GetUpstreamRequestId(response);

        Assert.That(receipt, Is.Empty);
    }

    [Test]
    public void HttpResponseReceiptExtractor_WhenProcessingHeaderIsUnsafe_SuppressesUpstreamProcessingMs()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("openai-processing-ms", "-1");
        response.Headers.TryAddWithoutValidation("x-processing-ms", "not-a-number");
        response.Headers.TryAddWithoutValidation("x-queue-ms", "-2");
        response.Headers.TryAddWithoutValidation("x-ttft-ms", "not-a-number");
        response.Headers.TryAddWithoutValidation("x-prefill-ms", "Infinity");
        response.Headers.TryAddWithoutValidation("x-decode-ms", "86400001");
        response.Headers.TryAddWithoutValidation("Server-Timing", "cache;desc=\"hit\"");

        double? receipt = HttpResponseReceiptExtractor.GetUpstreamProcessingMs(response);
        UpstreamPhaseTimingReceipt phaseReceipt = HttpResponseReceiptExtractor.GetUpstreamPhaseTimings(response);

        Assert.That(receipt, Is.Null);
        Assert.That(phaseReceipt.QueueMs, Is.Null);
        Assert.That(phaseReceipt.TimeToFirstTokenMs, Is.Null);
        Assert.That(phaseReceipt.PrefillMs, Is.Null);
        Assert.That(phaseReceipt.DecodeMs, Is.Null);
    }

    [Test]
    public async Task CompleteAsync_WhenEndpointReturnsLogprobs_PreservesRawConfidenceReceipt()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "choices": [
                        {
                          "message": { "content": "safe" },
                          "logprobs": {
                            "content": [
                              {
                                "token": "safe",
                                "logprob": -0.02,
                                "bytes": [115,97,102,101],
                                "top_logprobs": [
                                  { "token": "safe", "logprob": -0.02, "bytes": [115,97,102,101] },
                                  { "token": "stay", "logprob": -4.2, "bytes": [115,116,97,121] }
                                ]
                              }
                            ]
                          }
                        }
                      ],
                      "usage": { "prompt_tokens": 5, "completion_tokens": 1, "total_tokens": 6 }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Logprobs = true,
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("safe"));
        using JsonDocument logprobs = JsonDocument.Parse(result.LogprobsJson);
        JsonElement tokenReceipt = logprobs.RootElement.GetProperty("content")[0];
        Assert.That(tokenReceipt.GetProperty("token").GetString(), Is.EqualTo("safe"));
        Assert.That(tokenReceipt.GetProperty("top_logprobs").GetArrayLength(), Is.EqualTo(2));
        Assert.That(result.Usage, Is.EqualTo(new TokenUsage(5, 1, 6)));
    }

    [Test]
    public async Task CompleteAsync_WhenDeclaredResponseExceedsCap_FailsBeforeReadingBody()
    {
        var trackingStream = new TrackingReadStream(new byte[] { 0x7B, 0x7D });
        var content = new StreamContent(trackingStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 2_048;

        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MaxResponseBytes = 1_024;
        options.Inference.MaxTransientRetries = 0;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("response_too_large"));
        Assert.That(result.StatusMessage, Is.EqualTo("Inference response exceeds the configured cap of 1024 bytes."));
        Assert.That(trackingStream.ReadStarted, Is.False,
            "Declared oversized inference payloads should fail from headers alone without opening the response body.");
    }

    [Test]
    public async Task CompleteAsync_WhenStreamingResponseCrossesCap_ReturnsFailed()
    {
        string oversizedJson = "{\"choices\":[{\"message\":{\"content\":\"" + new string('a', 2_048) + "\"}}]}";
        var trackingStream = new TrackingReadStream(Encoding.UTF8.GetBytes(oversizedJson));
        var content = new UnknownLengthReadContent(trackingStream, "application/json");

        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MaxResponseBytes = 1_024;
        options.Inference.MaxTransientRetries = 0;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("response_too_large"));
        Assert.That(result.StatusMessage, Is.EqualTo("Inference response exceeds the configured cap of 1024 bytes."));
        Assert.That(trackingStream.ReadStarted, Is.True,
            "When Content-Length is absent, the inference client should stream the body and stop once the configured cap is crossed.");
    }

    [Test]
    public async Task CompleteAsync_WhenErrorBodyExceedsCap_FailsBeforeReadingBody()
    {
        var trackingStream = new TrackingReadStream(new byte[] { 0x6F, 0x6B });
        var content = new StreamContent(trackingStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Headers.ContentLength = 2_048;

        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MaxResponseBytes = 1_024;
        options.Inference.MaxTransientRetries = 0;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("500"));
        Assert.That(result.StatusMessage, Does.Contain("HTTP 500"));
        Assert.That(result.StatusMessage, Does.Not.Contain("1024"));
        Assert.That(trackingStream.ReadStarted, Is.False,
            "Declared oversized error bodies should be rejected from headers alone without opening the response stream.");
    }

    [Test]
    public async Task CompleteAsync_WhenServerReturnsErrorBody_DoesNotEchoRawBody()
    {
        var trackingStream = new TrackingReadStream(Encoding.UTF8.GetBytes("upstream model down"));
        var content = new UnknownLengthReadContent(trackingStream, "text/plain");
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MaxTransientRetries = 0;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("502"));
        Assert.That(result.StatusMessage, Does.Contain("HTTP 502"));
        Assert.That(result.StatusMessage, Does.Not.Contain("upstream model down"));
        Assert.That(trackingStream.ReadStarted, Is.True,
            "Unknown-length error bodies should still be drained through the shared bounded text-reader path.");
    }

    [Test]
    public async Task CompleteAsync_EmitsGenAiSpanAndMetrics()
    {
        using var telemetry = new GenAiTelemetryCapture();
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"id\":\"chatcmpl-123\",\"model\":\"gpt-4.1-mini-2026\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Telemetry ok\"}}],\"usage\":{\"prompt_tokens\":12,\"completion_tokens\":3,\"total_tokens\":15,\"completion_tokens_details\":{\"reasoning_tokens\":2}}}",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "https://api.openai.com/v1/";
        options.Inference.Model = "gpt-4.1-mini";

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.45f,
            MaxTokens = 64,
            TopP = 0.91f,
            PresencePenalty = 0.25f,
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.FinishReasons, Is.EqualTo(new[] { "stop" }));

        Activity activity = telemetry.Activities.Single();
        Assert.That(activity.OperationName, Is.EqualTo("chat gpt-4.1-mini"));
        Assert.That(activity.Kind, Is.EqualTo(ActivityKind.Client));
        Assert.That(activity.GetTagItem("gen_ai.operation.name"), Is.EqualTo("chat"));
        Assert.That(activity.GetTagItem("gen_ai.provider.name"), Is.EqualTo("openai"));
        Assert.That(activity.GetTagItem("gen_ai.request.model"), Is.EqualTo("gpt-4.1-mini"));
        Assert.That(activity.GetTagItem("gen_ai.response.model"), Is.EqualTo("gpt-4.1-mini-2026"));
        Assert.That(activity.GetTagItem("gen_ai.response.id"), Is.EqualTo("chatcmpl-123"));
        Assert.That(activity.GetTagItem("gen_ai.output.type"), Is.EqualTo("text"));
        Assert.That(activity.GetTagItem("server.address"), Is.EqualTo("api.openai.com"));
        Assert.That(activity.GetTagItem("gen_ai.request.max_tokens"), Is.EqualTo(64));
        Assert.That(Convert.ToDouble(activity.GetTagItem("gen_ai.request.temperature")), Is.EqualTo(0.45d).Within(0.001d));
        Assert.That(Convert.ToDouble(activity.GetTagItem("gen_ai.request.top_p")), Is.EqualTo(0.91d).Within(0.001d));
        Assert.That(Convert.ToDouble(activity.GetTagItem("gen_ai.request.presence_penalty")), Is.EqualTo(0.25d).Within(0.001d));
        Assert.That(activity.GetTagItem("gen_ai.usage.input_tokens"), Is.EqualTo(12));
        Assert.That(activity.GetTagItem("gen_ai.usage.output_tokens"), Is.EqualTo(3));
        Assert.That(activity.GetTagItem("gen_ai.usage.reasoning.output_tokens"), Is.EqualTo(2));
        Assert.That(((IEnumerable<string>)activity.GetTagItem("gen_ai.response.finish_reasons")!).ToArray(),
            Is.EqualTo(new[] { "stop" }));

        TelemetryDoubleMeasurement duration = telemetry.DurationMeasurements.Single();
        Assert.That(duration.Value, Is.GreaterThan(0d));
        Assert.That(duration.Tag("gen_ai.operation.name"), Is.EqualTo("chat"));
        Assert.That(duration.Tag("gen_ai.provider.name"), Is.EqualTo("openai"));
        Assert.That(duration.Tag("gen_ai.request.model"), Is.EqualTo("gpt-4.1-mini"));
        Assert.That(duration.Tag("gen_ai.response.model"), Is.EqualTo("gpt-4.1-mini-2026"));

        Assert.That(
            telemetry.TokenMeasurements.Select(m => $"{m.Tag("gen_ai.token.type")}:{m.Value}"),
            Is.EquivalentTo(new[] { "input:12", "output:3" }));
        Assert.That(telemetry.TokenMeasurements.Select(m => m.Tag("gen_ai.operation.name")).Distinct(),
            Is.EquivalentTo(new[] { "chat" }));

        Assert.Multiple(() =>
        {
            // Pass 346: port 11434 / host substring "ollama" no longer
            // resolve to provider "ollama" — the Ollama detection
            // branch was removed alongside the rest of the back-compat
            // path. They now fall through to "openai_compatible".
            Assert.That(GenAiTelemetry.GetProviderName("http://localhost:11434/v1/"), Is.EqualTo("openai_compatible"),
                "Ex-Ollama port 11434 must fall through to openai_compatible after Pass 346.");
            Assert.That(GenAiTelemetry.GetProviderName("http://ollama-host.local/v1/"), Is.EqualTo("openai_compatible"),
                "Ex-Ollama host substring must fall through to openai_compatible after Pass 346.");
            Assert.That(GenAiTelemetry.GetProviderName("http://127.0.0.1:1234/v1/"), Is.EqualTo("lmstudio"));
            Assert.That(GenAiTelemetry.GetProviderName("http://localhost:8080/v1/"), Is.EqualTo("llama.cpp"));
            Assert.That(GenAiTelemetry.GetProviderName("http://192.168.1.20:8080/v1/"), Is.EqualTo("llama.cpp"));
            Assert.That(GenAiTelemetry.GetProviderName("http://vllm.local/v1/"), Is.EqualTo("vllm"));
            Assert.That(GenAiTelemetry.GetProviderName("http://openvino.local:8000/v3/"), Is.EqualTo("openvino"));
            Assert.That(GenAiTelemetry.GetProviderName("http://203.0.113.10:8080/v1/"), Is.EqualTo("openai_compatible"),
                "Default local-runtime ports should not classify arbitrary public hosts.");
            Assert.That(GenAiTelemetry.GetProviderName("http://localhost:8000/v1/"), Is.EqualTo("openai_compatible"),
                "Port 8000 alone is ambiguous across vLLM, OpenVINO, and other OpenAI-compatible servers.");
        });
    }

    [Test]
    public async Task CompleteAsync_DeterministicFailure_DoesNotRetry()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("bad model", Encoding.UTF8, "text/plain"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.MaxTransientRetries = 3;
        options.Inference.TransientRetryBackoffMs = 5;

        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt(), CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(handler.CallCount, Is.EqualTo(1),
            "4xx responses must not burn retry budget — the next attempt would return the same thing.");
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenTtsDisabled_ReturnsDisabledStatus()
    {
        // The default PalLlmOptions.Tts.Enabled is false, so SynthesizeAsync
        // must short-circuit with a disabled result instead of trying to hit
        // a network that isn't there. This is the path the sidecar takes
        // when an operator hasn't flipped on TTS — which is the default.
        using var handler = new RecordingTtsHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest
        {
            Text = "anything",
        }, CancellationToken.None);

        Assert.That(result.IsConfigured, Is.False);
        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("disabled"));
        Assert.That(handler.CallCount, Is.EqualTo(0), "No HTTP call should fire when TTS is disabled.");
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenTextMissing_ReturnsFailedWithoutHttpCall()
    {
        using var handler = new RecordingTtsHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest
        {
            Text = "   ",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsConfigured, Is.True);
        Assert.That(result.StatusMessage, Does.Contain("no text"));
        Assert.That(handler.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenTextExceedsCap_ReturnsFailedWithoutHttpCall()
    {
        using var handler = new RecordingTtsHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";
        options.Tts.MaxCharacters = 16;

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest
        {
            Text = new string('x', 17),
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("16"));
        Assert.That(handler.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenServerReturnsAudio_ParsesBytesAndMimeAndVoice()
    {
        byte[] canned = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x01, 0x02 };
        using var handler = new RecordingTtsHandler(canned, "audio/mpeg");
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";
        options.Tts.DefaultVoice = "calm-narrator";

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest
        {
            Text = "Hello there.",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.IsConfigured, Is.True);
        Assert.That(result.Audio, Is.EqualTo(canned));
        Assert.That(result.MimeType, Is.EqualTo("audio/mpeg"));
        Assert.That(result.Voice, Is.EqualTo("calm-narrator"));
        Assert.That(handler.CallCount, Is.EqualTo(1));

        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody);
        Assert.That(body.RootElement.GetProperty("text").GetString(), Is.EqualTo("Hello there."));
        Assert.That(body.RootElement.GetProperty("voice").GetString(), Is.EqualTo("calm-narrator"));
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenOpenAiSpeechFormatConfigured_SendsCompatibleBody()
    {
        byte[] canned = new byte[] { 0x52, 0x49, 0x46, 0x46 };
        using var handler = new RecordingTtsHandler(canned, "audio/wav");
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/audio/speech";
        options.Tts.RequestFormat = TtsRequestFormats.OpenAiSpeech;
        options.Tts.Model = "Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice";
        options.Tts.DefaultVoice = "vivian";
        options.Tts.ResponseFormat = "pcm";

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest
        {
            Text = "Hello there.",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(handler.CallCount, Is.EqualTo(1));

        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody);
        Assert.That(body.RootElement.GetProperty("model").GetString(), Is.EqualTo("Qwen/Qwen3-TTS-12Hz-0.6B-CustomVoice"));
        Assert.That(body.RootElement.GetProperty("input").GetString(), Is.EqualTo("Hello there."));
        Assert.That(body.RootElement.GetProperty("voice").GetString(), Is.EqualTo("vivian"));
        Assert.That(body.RootElement.GetProperty("response_format").GetString(), Is.EqualTo("pcm"));
        Assert.That(body.RootElement.TryGetProperty("text", out _), Is.False);
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenOpenAiSpeechModelEmpty_OmitsModelField()
    {
        using var handler = new RecordingTtsHandler(new byte[] { 0x00 }, "audio/wav");
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/audio/speech";
        options.Tts.RequestFormat = TtsRequestFormats.OpenAiSpeech;
        options.Tts.Model = "   ";
        options.Tts.DefaultVoice = "default";

        var client = new HttpTtsClient(httpClient, options);

        _ = await client.SynthesizeAsync(new TtsRequest { Text = "ok" }, CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody);
        Assert.That(body.RootElement.TryGetProperty("model", out _), Is.False,
            "Local vLLM-Omni speech endpoints can infer the served model from the server; omit blank model ids instead of serializing empty strings.");
        Assert.That(body.RootElement.GetProperty("response_format").GetString(), Is.EqualTo("wav"));
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenOpenAiSpeechResponseTypeIsMissingOrGeneric_InfersRequestedMime()
    {
        using (var handler = new RecordingTtsHandler(new byte[] { 0x46, 0x4C, 0x41, 0x43 }, null))
        using (var httpClient = new HttpClient(handler))
        {
            var options = new PalLlmOptions();
            options.Tts.Enabled = true;
            options.Tts.BaseUrl = "https://tts.example/v1/audio/speech";
            options.Tts.RequestFormat = TtsRequestFormats.OpenAiSpeech;
            options.Tts.ResponseFormat = "flac";

            var client = new HttpTtsClient(httpClient, options);

            TtsResult result = await client.SynthesizeAsync(new TtsRequest { Text = "hello" }, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(result.MimeType, Is.EqualTo("audio/flac"));
        }

        using (var handler = new RecordingTtsHandler(new byte[] { 0x00, 0x01 }, "application/octet-stream"))
        using (var httpClient = new HttpClient(handler))
        {
            var options = new PalLlmOptions();
            options.Tts.Enabled = true;
            options.Tts.BaseUrl = "https://tts.example/v1/audio/speech";
            options.Tts.RequestFormat = TtsRequestFormats.OpenAiSpeech;
            options.Tts.ResponseFormat = "pcm";

            var client = new HttpTtsClient(httpClient, options);

            TtsResult result = await client.SynthesizeAsync(new TtsRequest { Text = "hello" }, CancellationToken.None);

            Assert.That(result.Success, Is.True);
            Assert.That(result.MimeType, Is.EqualTo("audio/pcm"));
        }
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenDeclaredResponseExceedsCap_FailsBeforeReadingBody()
    {
        var trackingStream = new TrackingReadStream(new byte[] { 0x01, 0x02, 0x03 });
        var content = new StreamContent(trackingStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        content.Headers.ContentLength = 2_048;

        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";
        options.Tts.MaxResponseBytes = 1_024;

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest { Text = "hello" }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("1024"));
        Assert.That(trackingStream.ReadStarted, Is.False,
            "Declared oversized TTS payloads should fail from headers alone without opening the response body.");
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenStreamingResponseCrossesCap_ReturnsFailed()
    {
        var trackingStream = new TrackingReadStream(new byte[2_048]);
        var content = new UnknownLengthReadContent(trackingStream, "audio/wav");

        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";
        options.Tts.MaxResponseBytes = 1_024;

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest { Text = "hello" }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("1024"));
        Assert.That(trackingStream.ReadStarted, Is.True,
            "When Content-Length is absent, the client should stream the body and stop once the configured cap is crossed.");
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenApiKeyConfigured_SendsBearerAuthorization()
    {
        using var handler = new RecordingTtsHandler(new byte[] { 0x00 }, "audio/wav");
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";
        options.Tts.ApiKey = "secret-tts-key";

        var client = new HttpTtsClient(httpClient, options);

        _ = await client.SynthesizeAsync(new TtsRequest { Text = "ok" }, CancellationToken.None);

        Assert.That(handler.LastAuthorizationScheme, Is.EqualTo("Bearer"));
        Assert.That(handler.LastAuthorizationParameter, Is.EqualTo("secret-tts-key"));
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenServerReturnsError_ReturnsSanitizedStatus()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("upstream model down", Encoding.UTF8, "text/plain"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest { Text = "hello" }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsConfigured, Is.True);
        Assert.That(result.StatusMessage, Does.Contain("502"));
        Assert.That(result.StatusMessage, Does.Not.Contain("upstream model down"));
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenDeclaredErrorBodyExceedsCap_FailsWithoutReadingBody()
    {
        var trackingStream = new TrackingReadStream(Encoding.UTF8.GetBytes("down"));
        var content = new StreamContent(trackingStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Headers.ContentLength = 2_048;

        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";
        options.Tts.MaxResponseBytes = 1_024;

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest { Text = "hello" }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("502"));
        Assert.That(result.StatusMessage, Does.Not.Contain("1024"));
        Assert.That(trackingStream.ReadStarted, Is.False,
            "Declared oversized TTS error payloads should fail from headers alone without opening the response body.");
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenRequestThrows_SurfacesAsFailedResultWithoutBubbling()
    {
        using var handler = new ThrowingTtsHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";

        var client = new HttpTtsClient(httpClient, options);

        TtsResult result = await client.SynthesizeAsync(new TtsRequest { Text = "hello" }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("unreachable"));
        Assert.That(result.StatusMessage, Does.Not.Contain("simulated network failure"));
    }

    [Test]
    public async Task TtsSynthesizeAsync_WhenCallerCancels_PropagatesCancellation()
    {
        using var handler = new CancellingTtsHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Tts.Enabled = true;
        options.Tts.BaseUrl = "https://tts.example/v1/synthesize";

        var client = new HttpTtsClient(httpClient, options);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(
            async () => await client.SynthesizeAsync(new TtsRequest { Text = "hi" }, cts.Token),
            Throws.InstanceOf<OperationCanceledException>(),
            "Pre-cancelled token must propagate, not be swallowed into a Failed result.");
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenAsrDisabled_ReturnsDisabledWithoutHttpCall()
    {
        using var handler = new RecordingAsrHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 0x52, 0x49 }),
            },
            CancellationToken.None);

        Assert.That(result.IsConfigured, Is.False);
        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("disabled"));
        Assert.That(handler.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenEndpointReturnsText_SendsMultipartAndParsesTranscript()
    {
        using var handler = new RecordingAsrHandler("{\"text\":\" Meet at the ridge. \"}");
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";
        options.Asr.ApiKey = "secret-asr-key";
        options.Asr.Language = " en ";
        options.Asr.Prompt = " Palworld field command. ";
        options.Asr.ChunkingStrategy = " AUTO ";
        options.Asr.Seed = 4242;

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 0x52, 0x49, 0x46, 0x46 }),
                AudioMimeType = "audio/wav",
            },
            CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Transcript, Is.EqualTo("Meet at the ridge."));
        Assert.That(result.Model, Is.EqualTo("local-whisper-small"));
        Assert.That(result.AudioBytes, Is.EqualTo(4));
        Assert.That(handler.CallCount, Is.EqualTo(1));
        Assert.That(handler.LastRequestPath, Is.EqualTo("/v1/audio/transcriptions"));
        Assert.That(handler.LastContentType, Does.StartWith("multipart/form-data"));
        Assert.That(handler.LastRequestBody, Does.Contain("name=file"));
        Assert.That(handler.LastRequestBody, Does.Contain("audio.wav"));
        Assert.That(handler.LastRequestBody, Does.Contain("name=model"));
        Assert.That(handler.LastRequestBody, Does.Contain("local-whisper-small"));
        Assert.That(handler.LastRequestBody, Does.Contain("name=language"));
        Assert.That(handler.LastRequestBody, Does.Contain("en"));
        Assert.That(handler.LastRequestBody, Does.Contain("name=prompt"));
        Assert.That(handler.LastRequestBody, Does.Contain("Palworld field command."));
        Assert.That(handler.LastRequestBody, Does.Contain("name=chunking_strategy"));
        Assert.That(handler.LastRequestBody, Does.Contain("auto"));
        Assert.That(handler.LastRequestBody, Does.Contain("name=seed"));
        Assert.That(handler.LastRequestBody, Does.Contain("4242"));
        Assert.That(handler.LastRequestBody, Does.Contain("name=response_format"));
        Assert.That(handler.LastRequestBody, Does.Contain("json"));
        Assert.That(handler.LastAuthorizationScheme, Is.EqualTo("Bearer"));
        Assert.That(handler.LastAuthorizationParameter, Is.EqualTo("secret-asr-key"));
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenVerboseJsonConfigured_SendsVerboseJsonAndParsesTranscript()
    {
        const string responseJson = """
            {
              "text": "Meet at the ridge.",
              "language": "en",
              "duration": 1.2,
              "segments": [
                {
                  "id": 0,
                  "start": 0.0,
                  "end": 1.2,
                  "text": "Meet at the ridge.",
                  "avg_logprob": -0.42
                }
              ],
              "words": [
                { "word": "Meet", "start": 0.0, "end": 0.25 },
                { "word": "ridge", "start": 0.82, "end": 1.2 }
              ]
            }
            """;
        using var handler = new RecordingAsrHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";
        options.Asr.ResponseFormat = " VERBOSE_JSON ";
        options.Asr.TimestampGranularities.Add(" segment ");
        options.Asr.TimestampGranularities.Add("word");

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 0x52, 0x49, 0x46, 0x46 }),
            },
            CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Transcript, Is.EqualTo("Meet at the ridge."));
        Assert.That(handler.LastRequestBody, Does.Contain("name=response_format"));
        Assert.That(handler.LastRequestBody, Does.Contain("verbose_json"));
        Assert.That(handler.LastRequestBody, Does.Contain("timestamp_granularities[]"));
        Assert.That(result.Timing.VerboseJsonRequested, Is.True);
        Assert.That(result.Timing.VerboseJsonReturned, Is.True);
        Assert.That(result.Timing.SegmentTimestampsRequested, Is.True);
        Assert.That(result.Timing.WordTimestampsRequested, Is.True);
        Assert.That(result.Timing.SegmentTimestampsReturned, Is.True);
        Assert.That(result.Timing.WordTimestampsReturned, Is.True);
        Assert.That(result.Timing.Status, Is.EqualTo("ready"));
        Assert.That(result.Timing.Language, Is.EqualTo("en"));
        Assert.That(result.Timing.DurationSeconds, Is.EqualTo(1.2d));
        Assert.That(result.Timing.SegmentCount, Is.EqualTo(1));
        Assert.That(result.Timing.WordCount, Is.EqualTo(2));
        Assert.That(result.Timing.CoveredSegmentSeconds, Is.EqualTo(1.2d));
        Assert.That(result.Timing.SegmentCoverageRatio, Is.EqualTo(1.0d));
        Assert.That(result.Timing.ToString(), Does.Not.Contain("Meet at the ridge"));
        Assert.That(result.Confidence.LogprobsReturned, Is.False);
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenVerboseJsonTimingIsMissing_ReturnsReviewReceipt()
    {
        const string responseJson = """
            {
              "text": "Meet at the ridge.",
              "language": "en",
              "duration": 35.0,
              "segments": []
            }
            """;
        using var handler = new RecordingAsrHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";
        options.Asr.ResponseFormat = AsrResponseFormats.VerboseJson;
        options.Asr.TimestampGranularities.Add(AsrTimestampGranularities.Segment);
        options.Asr.MaxTurnDurationMs = 30_000;

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 0x52, 0x49, 0x46, 0x46 }),
            },
            CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Timing.Status, Is.EqualTo("review"));
        Assert.That(result.Timing.SegmentTimestampsRequested, Is.True);
        Assert.That(result.Timing.SegmentTimestampsReturned, Is.False);
        Assert.That(result.Timing.Flags, Contains.Item("segment_timestamps_missing"));
        Assert.That(result.Timing.Flags, Contains.Item("no_segments_returned"));
        Assert.That(result.Timing.Flags, Contains.Item("duration_over_turn_budget"));
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenVerboseJsonQualityMetadataReturned_ReducesContentFreeReceipt()
    {
        const string responseJson = """
            {
              "text": "Meet at the ridge.",
              "duration": 1.8,
              "segments": [
                {
                  "id": 0,
                  "start": 0.0,
                  "end": 0.7,
                  "text": "Meet",
                  "tokens": [100, 101],
                  "avg_logprob": -0.3,
                  "compression_ratio": 1.2,
                  "no_speech_prob": 0.02,
                  "temperature": 0.0
                },
                {
                  "id": 1,
                  "start": 0.7,
                  "end": 1.8,
                  "text": "at the ridge",
                  "tokens": [102, 103, 104],
                  "avg_logprob": -1.2,
                  "compression_ratio": 2.6,
                  "no_speech_prob": 1.1,
                  "temperature": 0.2
                }
              ]
            }
            """;
        using var handler = new RecordingAsrHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";
        options.Asr.ResponseFormat = AsrResponseFormats.VerboseJson;

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 0x52, 0x49, 0x46, 0x46 }),
            },
            CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Quality.VerboseJsonRequested, Is.True);
        Assert.That(result.Quality.QualityMetadataReturned, Is.True);
        Assert.That(result.Quality.Status, Is.EqualTo("review"));
        Assert.That(result.Quality.SegmentCount, Is.EqualTo(2));
        Assert.That(result.Quality.QualitySegmentCount, Is.EqualTo(2));
        Assert.That(result.Quality.AverageSegmentLogprob, Is.EqualTo(-0.75d));
        Assert.That(result.Quality.MinSegmentLogprob, Is.EqualTo(-1.2d));
        Assert.That(result.Quality.LowAverageLogprobSegmentCount, Is.EqualTo(1));
        Assert.That(result.Quality.LowAverageLogprobThreshold, Is.EqualTo(-1.0d));
        Assert.That(result.Quality.MaxCompressionRatio, Is.EqualTo(2.6d));
        Assert.That(result.Quality.HighCompressionRatioSegmentCount, Is.EqualTo(1));
        Assert.That(result.Quality.HighCompressionRatioThreshold, Is.EqualTo(2.4d));
        Assert.That(result.Quality.MaxNoSpeechProbability, Is.EqualTo(1.1d));
        Assert.That(result.Quality.NoSpeechProbabilitySegmentCount, Is.EqualTo(2));
        Assert.That(result.Quality.SilentSegmentCandidateCount, Is.EqualTo(1));
        Assert.That(result.Quality.TemperatureSegmentCount, Is.EqualTo(2));
        Assert.That(result.Quality.MaxTemperature, Is.EqualTo(0.2d));
        Assert.That(result.Quality.Flags, Contains.Item("avg_logprob_below_minus_one"));
        Assert.That(result.Quality.Flags, Contains.Item("compression_ratio_above_2_4"));
        Assert.That(result.Quality.Flags, Contains.Item("silent_segment_candidate"));
        Assert.That(result.Quality.ToString(), Does.Not.Contain("Meet"));
        Assert.That(result.Quality.ToString(), Does.Not.Contain("ridge"));
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenEndpointReturnsReceipts_PreservesUpstreamRequestAndTiming()
    {
        using var handler = new RecordingAsrHandler(
            "{\"text\":\"Meet at the ridge.\"}",
            responseHeaders:
            [
                ("x-request-id", "asr-req-001"),
                ("openai-processing-ms", "21.125"),
                ("Server-Timing", "asr;dur=22.5, queue;dur=3, ttft;dur=8.5, prefill;dur=5, decode;dur=9"),
            ]);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 0x52, 0x49, 0x46, 0x46 }),
            },
            CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.UpstreamRequestId, Is.EqualTo("asr-req-001"));
        Assert.That(result.UpstreamProcessingMs, Is.EqualTo(21.125));
        Assert.That(result.UpstreamQueueMs, Is.EqualTo(3));
        Assert.That(result.UpstreamTimeToFirstTokenMs, Is.EqualTo(8.5));
        Assert.That(result.UpstreamPrefillMs, Is.EqualTo(5));
        Assert.That(result.UpstreamDecodeMs, Is.EqualTo(9));
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenLogprobsRequested_ReturnsContentFreeConfidenceReceipt()
    {
        const string responseJson = """
            {
              "text": "Meet at the ridge.",
              "logprobs": [
                { "token": "Meet", "logprob": -0.2, "bytes": [77, 101, 101, 116] },
                { "token": "ridge", "logprob": -1.4, "bytes": [114, 105, 100, 103, 101] }
              ]
            }
            """;
        using var handler = new RecordingAsrHandler(responseJson);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";
        options.Asr.Temperature = 0.2f;
        options.Asr.RequestLogprobs = true;
        options.Asr.LowConfidenceLogprobThreshold = -1.0f;

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 0x52, 0x49, 0x46, 0x46 }),
            },
            CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Confidence.LogprobsRequested, Is.True);
        Assert.That(result.Confidence.LogprobsReturned, Is.True);
        Assert.That(result.Confidence.Status, Is.EqualTo("review"));
        Assert.That(result.Confidence.TokenCount, Is.EqualTo(2));
        Assert.That(result.Confidence.LowConfidenceTokenCount, Is.EqualTo(1));
        Assert.That(result.Confidence.AverageLogprob, Is.EqualTo(-0.8d));
        Assert.That(result.Confidence.MinLogprob, Is.EqualTo(-1.4d));
        Assert.That(handler.LastRequestBody, Does.Contain("name=temperature"));
        Assert.That(handler.LastRequestBody, Does.Contain("0.2"));
        Assert.That(handler.LastRequestBody, Does.Contain("include[]"));
        Assert.That(handler.LastRequestBody, Does.Contain("logprobs"));
        Assert.That(result.Confidence.ToString(), Does.Not.Contain("Meet"));
        Assert.That(result.Confidence.ToString(), Does.Not.Contain("ridge"));
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenAudioBase64Malformed_FailsBeforeHttpCall()
    {
        using var handler = new RecordingAsrHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest { AudioBase64 = "not base64!!!" },
            CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("valid base64"));
        Assert.That(handler.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task AudioTranscribeAsync_WhenServerReturnsError_ReturnsSanitizedStatus()
    {
        using var handler = new RecordingAsrHandler(
            responseBody: "model stack trace",
            statusCode: HttpStatusCode.BadGateway,
            contentType: "text/plain",
            responseHeaders:
            [
                ("x-correlation-id", "asr-502"),
                ("Server-Timing", "asr;dur=42, queue;dur=4"),
            ]);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Asr.Enabled = true;
        options.Asr.BaseUrl = "https://asr.example/v1/audio/transcriptions";
        options.Asr.Model = "local-whisper-small";

        var client = new HttpAudioTranscriptionClient(httpClient, options);

        AudioTranscriptionResult result = await client.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
            },
            CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.IsConfigured, Is.True);
        Assert.That(result.UpstreamRequestId, Is.EqualTo("asr-502"));
        Assert.That(result.UpstreamProcessingMs, Is.EqualTo(42));
        Assert.That(result.UpstreamQueueMs, Is.EqualTo(4));
        Assert.That(result.StatusMessage, Does.Contain("502"));
        Assert.That(result.StatusMessage, Does.Not.Contain("stack trace"));
    }

    [Test]
    public async Task VisionDescribeAsync_WhenEndpointReturnsContentPartArray_ConcatenatesTextParts()
    {
        using var handler = new RecordingHandler(
            "{\"choices\":[{\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Storm front \"},{\"type\":\"output_text\",\"text\":\"rolling over the ridge.\"}]}}]}");
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Vision.Enabled = true;

        var client = new HttpVisionClient(httpClient, options);

        VisionResult result = await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Content, Is.EqualTo("Storm front rolling over the ridge."));

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);
        JsonElement imagePart = requestJson.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")[0];
        Assert.That(imagePart.GetProperty("uuid").GetString(),
            Is.EqualTo("palllm-image-sha256-a84085bba2ff5bcd7f7590f9c8ce1d6e"),
            "Repeated identical screenshots should carry a stable media-cache id for vLLM-compatible endpoints.");
    }

    [Test]
    public async Task VisionDescribeAsync_WhenImageBase64Malformed_FailsBeforeHttpRequest()
    {
        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"should not be called\"}}]}",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Vision.Enabled = true;

        var client = new HttpVisionClient(httpClient, options);

        VisionResult result = await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "not base64!!!",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("invalid_base64"));
        Assert.That(result.StatusMessage, Does.Contain("valid base64"));
        Assert.That(handler.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task VisionDescribeAsync_WhenDeclaredResponseExceedsCap_FailsBeforeReadingBody()
    {
        var trackingStream = new TrackingReadStream(new byte[] { 0x7B, 0x7D });
        var content = new StreamContent(trackingStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 2_048;

        var handler = new ScriptedHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Vision.Enabled = true;
        options.Vision.MaxResponseBytes = 1_024;

        var client = new HttpVisionClient(httpClient, options);

        VisionResult result = await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("response_too_large"));
        Assert.That(result.StatusMessage, Is.EqualTo("Vision response exceeds the configured cap of 1024 bytes."));
        Assert.That(trackingStream.ReadStarted, Is.False,
            "Declared oversized vision payloads should fail from headers alone without opening the response body.");
    }

    [Test]
    public async Task VisionDescribeAsync_WhenServerReturnsError_ReturnsSanitizedStatus()
    {
        var response = new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("backend exploded", Encoding.UTF8, "text/plain"),
        };
        response.Headers.Add("x-correlation-id", "vision-upstream-502");
        response.Headers.Add("Server-Timing", "vision;dur=42.25, queue;dur=4, ttft;dur=19.5, prefill;dur=12.25, decode;dur=18.5");
        var handler = new ScriptedHandler(response);
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Vision.Enabled = true;

        var client = new HttpVisionClient(httpClient, options);

        VisionResult result = await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorType, Is.EqualTo("502"));
        Assert.That(result.UpstreamRequestId, Is.EqualTo("vision-upstream-502"));
        Assert.That(result.UpstreamProcessingMs, Is.EqualTo(42.25));
        Assert.That(result.UpstreamQueueMs, Is.EqualTo(4));
        Assert.That(result.UpstreamTimeToFirstTokenMs, Is.EqualTo(19.5));
        Assert.That(result.UpstreamPrefillMs, Is.EqualTo(12.25));
        Assert.That(result.UpstreamDecodeMs, Is.EqualTo(18.5));
        Assert.That(result.StatusMessage, Does.Contain("HTTP 502"));
        Assert.That(result.StatusMessage, Does.Not.Contain("backend exploded"));
    }

    [Test]
    public async Task VisionDescribeAsync_EmitsGenerateContentSpanAndMetrics()
    {
        using var telemetry = new GenAiTelemetryCapture();
        using var handler = new RecordingHandler(
            "{\"id\":\"vision-123\",\"model\":\"gpt-4.1-mini-vision-2026\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"Scout report ready.\"}}],\"usage\":{\"prompt_tokens\":19,\"completion_tokens\":7,\"total_tokens\":26}}");
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Vision.Enabled = true;
        options.Vision.BaseUrl = "https://api.openai.com/v1/";
        options.Vision.Model = "gpt-4.1-mini-vision";
        options.Vision.MultimodalProcessor.MaxSoftTokens = 560;
        options.Vision.MultimodalProcessor.Fps = 1;

        var client = new HttpVisionClient(httpClient, options);

        using JsonDocument responseFormat = JsonDocument.Parse("""{"type":"json_schema"}""");
        VisionResult result = await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
            MaxTokens = 96,
            Temperature = 0.1f,
            ResponseFormat = responseFormat.RootElement.Clone(),
        }, CancellationToken.None);

        Assert.That(result.Success, Is.True);
        Assert.That(result.FinishReasons, Is.EqualTo(new[] { "stop" }));

        using JsonDocument requestJson = JsonDocument.Parse(handler.LastRequestBody);
        JsonElement processorKwargs = requestJson.RootElement.GetProperty("mm_processor_kwargs");
        Assert.That(processorKwargs.GetProperty("max_soft_tokens").GetInt32(), Is.EqualTo(560));
        Assert.That(processorKwargs.GetProperty("fps").GetSingle(), Is.EqualTo(1.0f));

        Activity activity = telemetry.Activities.Single();
        Assert.That(activity.OperationName, Is.EqualTo("generate_content gpt-4.1-mini-vision"));
        Assert.That(activity.Kind, Is.EqualTo(ActivityKind.Client));
        Assert.That(activity.GetTagItem("gen_ai.operation.name"), Is.EqualTo("generate_content"));
        Assert.That(activity.GetTagItem("gen_ai.provider.name"), Is.EqualTo("openai"));
        Assert.That(activity.GetTagItem("gen_ai.request.model"), Is.EqualTo("gpt-4.1-mini-vision"));
        Assert.That(activity.GetTagItem("gen_ai.response.model"), Is.EqualTo("gpt-4.1-mini-vision-2026"));
        Assert.That(activity.GetTagItem("gen_ai.output.type"), Is.EqualTo("json"));
        Assert.That(activity.GetTagItem("gen_ai.request.max_tokens"), Is.EqualTo(96));
        Assert.That(Convert.ToDouble(activity.GetTagItem("gen_ai.request.temperature")), Is.EqualTo(0.1d).Within(0.001d));

        TelemetryDoubleMeasurement duration = telemetry.DurationMeasurements.Single();
        Assert.That(duration.Tag("gen_ai.operation.name"), Is.EqualTo("generate_content"));
        Assert.That(duration.Tag("gen_ai.response.model"), Is.EqualTo("gpt-4.1-mini-vision-2026"));

        Assert.That(
            telemetry.TokenMeasurements.Select(m => $"{m.Tag("gen_ai.token.type")}:{m.Value}"),
            Is.EquivalentTo(new[] { "input:19", "output:7" }));
    }

    private sealed class RecordingHandler : HttpMessageHandler, IDisposable
    {
        private readonly string _responseBody;

        public RecordingHandler(string? responseBody = null)
        {
            _responseBody = string.IsNullOrWhiteSpace(responseBody)
                ? "{\"choices\":[{\"message\":{\"content\":\"hello\"}}]}"
                : responseBody;
        }

        public string LastRequestBody { get; private set; } = string.Empty;

        public string LastRequestPath { get; private set; } = string.Empty;

        public Dictionary<string, string> LastRequestHeaders { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            LastRequestHeaders = request.Headers.ToDictionary(
                header => header.Key,
                header => string.Join(",", header.Value),
                StringComparer.OrdinalIgnoreCase);
            LastRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responseBody,
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public ScriptedHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            HttpResponseMessage next = _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"choices\":[{\"message\":{\"content\":\"default\"}}]}",
                        Encoding.UTF8,
                        "application/json"),
                };
            return Task.FromResult(next);
        }
    }

    private sealed class RecordingTtsHandler : HttpMessageHandler, IDisposable
    {
        private readonly byte[] _audio;
        private readonly string? _contentType;

        public RecordingTtsHandler() : this(new byte[] { 0x00 }, "audio/wav") { }

        public RecordingTtsHandler(byte[] audio, string? contentType)
        {
            _audio = audio;
            _contentType = contentType;
        }

        public int CallCount { get; private set; }

        public string LastRequestBody { get; private set; } = string.Empty;

        public string? LastAuthorizationScheme { get; private set; }

        public string? LastAuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            var content = new ByteArrayContent(_audio);
            if (!string.IsNullOrWhiteSpace(_contentType))
            {
                content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            };
        }
    }

    private sealed class RecordingAsrHandler : HttpMessageHandler, IDisposable
    {
        private readonly string _responseBody;
        private readonly HttpStatusCode _statusCode;
        private readonly string _contentType;
        private readonly (string Name, string Value)[] _responseHeaders;

        public RecordingAsrHandler(
            string responseBody = "{\"text\":\"transcribed\"}",
            HttpStatusCode statusCode = HttpStatusCode.OK,
            string contentType = "application/json",
            params (string Name, string Value)[] responseHeaders)
        {
            _responseBody = responseBody;
            _statusCode = statusCode;
            _contentType = contentType;
            _responseHeaders = responseHeaders;
        }

        public int CallCount { get; private set; }

        public string LastRequestPath { get; private set; } = string.Empty;

        public string LastRequestBody { get; private set; } = string.Empty;

        public string? LastContentType { get; private set; }

        public string? LastAuthorizationScheme { get; private set; }

        public string? LastAuthorizationParameter { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestPath = request.RequestUri?.AbsolutePath ?? string.Empty;
            LastContentType = request.Content?.Headers.ContentType?.MediaType;
            LastRequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody, Encoding.UTF8, _contentType),
            };
            foreach ((string name, string value) in _responseHeaders)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        }
    }

    private sealed class ThrowingTtsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("simulated network failure");
        }
    }

    private sealed class CancellingTtsHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The real HttpClient pipeline passes the caller's CancellationToken
            // down; emulate that by observing it and honoring cancellation.
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x00 }),
            });
        }
    }

    private sealed class TrackingReadStream : MemoryStream
    {
        public TrackingReadStream(byte[] buffer) : base(buffer, writable: false)
        {
        }

        public bool ReadStarted { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadStarted = true;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ReadStarted = true;
            return base.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted = true;
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class UnknownLengthReadContent : HttpContent
    {
        private readonly Stream _stream;

        public UnknownLengthReadContent(Stream stream, string contentType)
        {
            _stream = stream;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("Test content is only consumed through ReadAsStreamAsync.");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult(_stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private readonly record struct TelemetryDoubleMeasurement(
        double Value,
        IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string key) => Tags.TryGetValue(key, out object? value) ? value : null;
    }

    private readonly record struct TelemetryLongMeasurement(
        long Value,
        IReadOnlyDictionary<string, object?> Tags)
    {
        public object? Tag(string key) => Tags.TryGetValue(key, out object? value) ? value : null;
    }

    private sealed class GenAiTelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activityListener;
        private readonly MeterListener _meterListener;

        public GenAiTelemetryCapture()
        {
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => source.Name == PalLlmTelemetry.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => Activities.Add(activity),
            };
            ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new MeterListener();
            _meterListener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PalLlmTelemetry.MeterName &&
                    (instrument.Name == PalLlmTelemetry.GenAiClientOperationDurationMetricName ||
                     instrument.Name == PalLlmTelemetry.GenAiClientTokenUsageMetricName))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            {
                if (instrument.Name == PalLlmTelemetry.GenAiClientOperationDurationMetricName)
                {
                    DurationMeasurements.Add(new TelemetryDoubleMeasurement(measurement, CloneTags(tags)));
                }
            });
            _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (instrument.Name == PalLlmTelemetry.GenAiClientTokenUsageMetricName)
                {
                    TokenMeasurements.Add(new TelemetryLongMeasurement(measurement, CloneTags(tags)));
                }
            });
            _meterListener.Start();
        }

        public List<Activity> Activities { get; } = [];

        public List<TelemetryDoubleMeasurement> DurationMeasurements { get; } = [];

        public List<TelemetryLongMeasurement> TokenMeasurements { get; } = [];

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }

        private static Dictionary<string, object?> CloneTags(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            Dictionary<string, object?> clone = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                clone[tag.Key] = tag.Value;
            }

            return clone;
        }
    }
}
