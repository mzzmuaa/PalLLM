// InferenceClient wire contracts: the OpenAI-compatible chat-completions
// request/response DTOs + InferencePrompt/InferenceResult. Split out of
// InferenceClient.cs (same namespace) so the client logic reads cleanly.
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Portable;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

internal sealed class InferenceChatCompletionsRequestBody
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    public float Temperature { get; init; }

    [JsonIgnore]
    public int TokenBudget { get; init; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("max_completion_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxCompletionTokens { get; init; }

    [JsonPropertyName("messages")]
    public InferenceChatMessage[] Messages { get; init; } = [];

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseFormat { get; init; }

    [JsonPropertyName("structured_outputs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? StructuredOutputs { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? TopP { get; init; }

    [JsonPropertyName("presence_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? PresencePenalty { get; init; }

    [JsonPropertyName("frequency_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? FrequencyPenalty { get; init; }

    [JsonPropertyName("top_k")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopK { get; init; }

    [JsonPropertyName("min_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? MinP { get; init; }

    [JsonPropertyName("repetition_penalty")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? RepetitionPenalty { get; init; }

    [JsonPropertyName("reasoning_effort")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasoningEffort { get; init; }

    [JsonPropertyName("thinking_token_budget")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ThinkingTokenBudget { get; init; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; init; }

    [JsonPropertyName("priority")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Priority { get; init; }

    [JsonPropertyName("service_tier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; init; }

    [JsonPropertyName("prompt_cache_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheKey { get; init; }

    [JsonPropertyName("prompt_cache_retention")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptCacheRetention { get; init; }

    [JsonPropertyName("verbosity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Verbosity { get; init; }

    [JsonPropertyName("safety_identifier")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SafetyIdentifier { get; init; }

    [JsonPropertyName("store")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Store { get; init; }

    [JsonPropertyName("metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Metadata { get; init; }

    [JsonPropertyName("cache_prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LlamaCppCachePrompt { get; init; }

    [JsonPropertyName("id_slot")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LlamaCppSlotId { get; init; }

    [JsonPropertyName("n_cache_reuse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LlamaCppCacheReuseTokens { get; init; }

    [JsonPropertyName("parallel_tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ParallelToolCalls { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ToolChoice { get; init; }

    [JsonPropertyName("prediction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Prediction { get; init; }

    [JsonPropertyName("modalities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Modalities { get; init; }

    [JsonPropertyName("audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Audio { get; init; }

    [JsonPropertyName("mm_processor_kwargs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MultimodalProcessorOptions? MmProcessorKwargs { get; init; }

    [JsonPropertyName("logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Logprobs { get; init; }

    [JsonPropertyName("top_logprobs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TopLogprobs { get; init; }

    [JsonPropertyName("stop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Stop { get; init; }

    [JsonPropertyName("chat_template_kwargs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InferenceChatTemplateKwargs? ChatTemplateKwargs { get; init; }

    [JsonPropertyName("enable_thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableThinking { get; init; }

    [JsonPropertyName("preserve_thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PreserveThinking { get; init; }

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Ttl { get; init; }

    [JsonPropertyName("cache_salt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CacheSalt { get; init; }
}

internal sealed class InferenceChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public JsonElement Content { get; init; }
}

internal sealed class InferenceChatTemplateKwargs
{
    [JsonPropertyName("enable_thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? EnableThinking { get; init; }

    [JsonPropertyName("preserve_thinking")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PreserveThinking { get; init; }
}

// Pass 346: the dedicated Ollama warmup-request DTO was removed. The
// runtime warms every engine through the generic OpenAI-compatible
// chat-completions path now.

public sealed class InferencePrompt
{
    public string SystemPrompt { get; init; } = string.Empty;

    public string UserPrompt { get; init; } = string.Empty;

    public float Temperature { get; init; } = 0.7f;

    public int MaxTokens { get; init; } = 256;

    public float? TopP { get; init; }

    public float? PresencePenalty { get; init; }

    public float? FrequencyPenalty { get; init; }

    public int? TopK { get; init; }

    public float? MinP { get; init; }

    public float? RepetitionPenalty { get; init; }

    public bool? EnableThinking { get; init; }

    public bool? PreserveThinking { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>
    /// Optional vLLM-compatible <c>thinking_token_budget</c> cap forwarded only
    /// for route-specific reasoning-model canaries. Leave empty for normal
    /// companion chat; set positive values only after the exact vLLM server was
    /// launched with a reasoning parser and accepted the request shape.
    /// </summary>
    public int? ThinkingTokenBudget { get; init; }

    public string? TokenBudgetField { get; init; }

    public int? Seed { get; init; }

    public int? RequestPriority { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>service_tier</c> hint forwarded only for
    /// endpoint-proven routing canaries. Ordinary local companion chat omits it.
    /// </summary>
    public string? ServiceTier { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>prompt_cache_key</c> hint forwarded only
    /// for route-specific cache-routing canaries. Leave empty for ordinary
    /// local companion chat and for strict endpoints that reject hosted-only
    /// fields.
    /// </summary>
    public string? PromptCacheKey { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>prompt_cache_retention</c> hint forwarded
    /// only for route-specific long-prefix cache canaries. Allowed values are
    /// normalized by <see cref="InferencePromptCacheRetentions"/>.
    /// </summary>
    public string? PromptCacheRetention { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>verbosity</c> hint forwarded only for
    /// route-specific concise or expanded-output canaries. Leave empty for
    /// ordinary local companion chat so strict endpoints never see the field.
    /// </summary>
    public string? Verbosity { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>safety_identifier</c> value forwarded only
    /// for hosted lanes that need a pseudonymous safety correlation id. Keep it
    /// stable and non-secret; never pass player names, emails, paths, or raw
    /// save identifiers.
    /// </summary>
    public string? SafetyIdentifier { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>store</c> switch forwarded only for
    /// hosted retention-posture canaries. Leave empty for normal companion
    /// chat; prefer <c>false</c> over <c>true</c> unless an operator is
    /// deliberately running an eval/distillation lane outside gameplay.
    /// </summary>
    public bool? StoreCompletions { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>metadata</c> labels forwarded only for
    /// route-owned proof canaries. Values are bounded, trimmed, and merged
    /// over <c>PalLLM:Inference:RequestMetadata</c>; never pass player names,
    /// save paths, prompts, secrets, or raw game-state text.
    /// </summary>
    public IReadOnlyDictionary<string, string>? RequestMetadata { get; init; }

    /// <summary>
    /// Optional bounded ASCII correlation id sent as the configured outbound
    /// HTTP request-id header when <c>PalLLM:Inference:ClientRequestIdHeader</c>
    /// is set. Normal companion chat passes the already-generated PalLLM chat
    /// request id; the header itself remains omitted unless explicitly enabled.
    /// </summary>
    public string? ClientRequestId { get; init; }

    /// <summary>
    /// Optional llama.cpp <c>cache_prompt</c> toggle forwarded only for
    /// endpoint-proven prompt-cache canaries. Leave empty for ordinary local
    /// companion chat and for strict non-llama OpenAI-compatible endpoints.
    /// </summary>
    public bool? LlamaCppCachePrompt { get; init; }

    /// <summary>
    /// Optional llama.cpp <c>id_slot</c> selector for route-owned warm-slot
    /// canaries. Values below -1 are suppressed before serialization.
    /// </summary>
    public int? LlamaCppSlotId { get; init; }

    /// <summary>
    /// Optional llama.cpp <c>n_cache_reuse</c> floor for measured stable-prefix
    /// canaries. Negative values are suppressed before serialization.
    /// </summary>
    public int? LlamaCppCacheReuseTokens { get; init; }

    public bool? ParallelToolCalls { get; init; }

    public IReadOnlyList<string>? StopSequences { get; init; }

    /// <summary>
    /// Optional raw chat-completions user <c>content</c> value forwarded
    /// verbatim for route-specific multimodal input canaries. Use this for
    /// OpenAI/vLLM-style content-part arrays such as <c>text</c>,
    /// <c>image_url</c>, <c>video_url</c>, <c>input_audio</c>, or
    /// <c>audio_url</c>. Leave empty for normal companion chat so the hot path
    /// remains a plain text message.
    /// </summary>
    public JsonElement? UserContent { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>tools</c> array forwarded verbatim for
    /// strict route-specific tool-call canaries. Leave empty for normal
    /// companion chat so unsupported local endpoints never see the field.
    /// </summary>
    public JsonElement? Tools { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>tool_choice</c> value forwarded verbatim
    /// with <see cref="Tools"/> for route-specific action/directive proof
    /// lanes. Supports string modes such as <c>"none"</c>, <c>"auto"</c>, or
    /// <c>"required"</c>, and named function-choice objects.
    /// </summary>
    public JsonElement? ToolChoice { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>prediction</c> payload forwarded verbatim
    /// for route-specific predicted-output canaries. Leave empty for normal
    /// companion chat so unsupported local endpoints never see the field.
    /// </summary>
    public JsonElement? Prediction { get; init; }

    /// <summary>
    /// Optional vLLM-compatible <c>structured_outputs</c> payload forwarded
    /// verbatim for endpoint-specific choice, regex, grammar, JSON, or
    /// structural-tag canaries. Leave empty for normal companion chat and prefer
    /// <see cref="ResponseFormat"/> when a portable OpenAI-compatible schema is
    /// enough.
    /// </summary>
    public JsonElement? StructuredOutputs { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>modalities</c> output list forwarded only
    /// for route-specific audio-output canaries. Leave empty for normal
    /// companion chat so strict local endpoints never see the field.
    /// </summary>
    public IReadOnlyList<string>? Modalities { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>audio</c> response-parameter object
    /// forwarded with <see cref="Modalities"/> for audio-output canaries.
    /// Use this only after the exact endpoint proves it accepts the shape.
    /// </summary>
    public JsonElement? Audio { get; init; }

    /// <summary>
    /// Optional vLLM-compatible <c>mm_processor_kwargs</c> payload for
    /// route-owned multimodal canaries. Overrides configured
    /// <c>PalLLM:Inference:MultimodalProcessor</c> when supplied.
    /// </summary>
    public MultimodalProcessorOptions? MultimodalProcessor { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>logprobs</c> request switch forwarded only
    /// for route-specific confidence/proof canaries. Leave empty for normal
    /// companion chat so unsupported local endpoints never see the field.
    /// </summary>
    public bool? Logprobs { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>top_logprobs</c> count for route-specific
    /// confidence/proof canaries. Values outside the documented 0-20 range are
    /// suppressed before serialization.
    /// </summary>
    public int? TopLogprobs { get; init; }

    /// <summary>
    /// Optional OpenAI-compatible <c>response_format</c> payload forwarded
    /// verbatim for route-specific structured-output canaries. Leave empty for
    /// normal companion chat so strict local endpoints that reject unknown fields
    /// keep working.
    /// </summary>
    public JsonElement? ResponseFormat { get; init; }
}

public sealed class InferenceResult
{
    private InferenceResult(
        bool isConfigured,
        bool success,
        string? content,
        string statusMessage,
        TokenUsage usage,
        string providerName,
        string requestModel,
        string? responseModel,
        string? responseId,
        string? systemFingerprint,
        string? toolCallsJson,
        string? logprobsJson,
        string? audioJson,
        IReadOnlyList<string>? finishReasons,
        string? upstreamRequestId,
        double? upstreamProcessingMs,
        double? upstreamQueueMs,
        double? upstreamTimeToFirstTokenMs,
        double? upstreamPrefillMs,
        double? upstreamDecodeMs,
        long latencyMs,
        string? errorType)
    {
        IsConfigured = isConfigured;
        Success = success;
        Content = content;
        StatusMessage = statusMessage;
        Usage = usage;
        ProviderName = providerName;
        RequestModel = requestModel;
        ResponseModel = responseModel ?? string.Empty;
        ResponseId = responseId ?? string.Empty;
        SystemFingerprint = systemFingerprint ?? string.Empty;
        ToolCallsJson = toolCallsJson ?? string.Empty;
        LogprobsJson = logprobsJson ?? string.Empty;
        AudioJson = audioJson ?? string.Empty;
        FinishReasons = NormalizeFinishReasons(finishReasons);
        UpstreamRequestId = HttpResponseReceiptExtractor.NormalizeIdentifier(upstreamRequestId);
        UpstreamProcessingMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamProcessingMs);
        UpstreamQueueMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamQueueMs);
        UpstreamTimeToFirstTokenMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamTimeToFirstTokenMs);
        UpstreamPrefillMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamPrefillMs);
        UpstreamDecodeMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamDecodeMs);
        LatencyMs = Math.Max(0, latencyMs);
        ErrorType = errorType ?? string.Empty;
    }

    public bool IsConfigured { get; }

    public bool Success { get; }

    public string? Content { get; }

    public string StatusMessage { get; }

    /// Token usage reported by the endpoint's `usage` field. When the endpoint does
    /// not return a usage block (some servers omit it for streaming paths), this
    /// falls back to <see cref="TokenUsage.Empty"/>.
    public TokenUsage Usage { get; }

    public string ProviderName { get; }

    public string RequestModel { get; }

    public string ResponseModel { get; }

    /// <summary>
    /// Optional completion identifier returned by OpenAI-compatible
    /// chat-completions endpoints. Empty when the upstream omits it.
    /// </summary>
    public string ResponseId { get; }

    /// <summary>
    /// Optional backend configuration fingerprint returned by OpenAI-compatible
    /// chat-completions endpoints. Empty when the upstream omits it.
    /// </summary>
    public string SystemFingerprint { get; }

    /// <summary>
    /// Raw assistant tool-call payload returned by OpenAI-compatible
    /// chat-completions endpoints. Empty when the response did not include
    /// <c>tool_calls</c> or a legacy <c>function_call</c> object.
    /// </summary>
    public string ToolCallsJson { get; }

    /// <summary>
    /// Raw choice-level <c>logprobs</c> payload returned by OpenAI-compatible
    /// chat-completions endpoints. Empty when the response omitted the receipt.
    /// </summary>
    public string LogprobsJson { get; }

    /// <summary>
    /// Raw assistant <c>audio</c> payload returned by OpenAI-compatible
    /// chat-completions endpoints. Empty when the response omitted audio.
    /// </summary>
    public string AudioJson { get; }

    /// <summary>
    /// Choice-level stop reasons returned by OpenAI-compatible chat-completions
    /// endpoints. Empty when the upstream omits <c>finish_reason</c>.
    /// </summary>
    public IReadOnlyList<string> FinishReasons { get; }

    /// <summary>
    /// Optional request/correlation identifier returned by the upstream HTTP
    /// endpoint, usually from <c>x-request-id</c>. Empty when omitted.
    /// </summary>
    public string UpstreamRequestId { get; }

    /// <summary>
    /// Optional upstream processing duration reported by response headers such
    /// as <c>openai-processing-ms</c> or <c>Server-Timing</c>. Null when omitted.
    /// </summary>
    public double? UpstreamProcessingMs { get; }

    /// <summary>
    /// Optional upstream queue duration reported by response headers such as
    /// <c>Server-Timing: queue;dur=...</c>. Null when omitted.
    /// </summary>
    public double? UpstreamQueueMs { get; }

    /// <summary>
    /// Optional upstream time-to-first-token duration reported by response
    /// headers such as <c>Server-Timing: ttft;dur=...</c>. Null when omitted.
    /// </summary>
    public double? UpstreamTimeToFirstTokenMs { get; }

    /// <summary>
    /// Optional upstream prefill duration reported by response headers such as
    /// <c>Server-Timing: prefill;dur=...</c>. Null when omitted.
    /// </summary>
    public double? UpstreamPrefillMs { get; }

    /// <summary>
    /// Optional upstream decode duration reported by response headers such as
    /// <c>Server-Timing: decode;dur=...</c>. Null when omitted.
    /// </summary>
    public double? UpstreamDecodeMs { get; }

    public long LatencyMs { get; }

    public string ErrorType { get; }

    public static InferenceResult Disabled(
        string statusMessage,
        string providerName = "",
        string requestModel = "") =>
        new(
            isConfigured: false,
            success: false,
            content: null,
            statusMessage,
            TokenUsage.Empty,
            providerName,
            requestModel,
            responseModel: null,
            responseId: null,
            systemFingerprint: null,
            toolCallsJson: null,
            logprobsJson: null,
            audioJson: null,
            finishReasons: null,
            upstreamRequestId: null,
            upstreamProcessingMs: null,
            upstreamQueueMs: null,
            upstreamTimeToFirstTokenMs: null,
            upstreamPrefillMs: null,
            upstreamDecodeMs: null,
            latencyMs: 0,
            errorType: null);

    public static InferenceResult Failed(
        string statusMessage,
        string providerName = "",
        string requestModel = "",
        string? responseModel = null,
        long latencyMs = 0,
        string? errorType = null,
        string? upstreamRequestId = null,
        double? upstreamProcessingMs = null,
        double? upstreamQueueMs = null,
        double? upstreamTimeToFirstTokenMs = null,
        double? upstreamPrefillMs = null,
        double? upstreamDecodeMs = null) =>
        new(
            isConfigured: true,
            success: false,
            content: null,
            statusMessage,
            TokenUsage.Empty,
            providerName,
            requestModel,
            responseModel,
            responseId: null,
            systemFingerprint: null,
            toolCallsJson: null,
            logprobsJson: null,
            audioJson: null,
            finishReasons: null,
            upstreamRequestId,
            upstreamProcessingMs,
            upstreamQueueMs,
            upstreamTimeToFirstTokenMs,
            upstreamPrefillMs,
            upstreamDecodeMs,
            latencyMs,
            errorType);

    public static InferenceResult Succeeded(
        string content,
        TokenUsage usage = default,
        string providerName = "",
        string requestModel = "",
        string? responseModel = null,
        long latencyMs = 0,
        string? systemFingerprint = null,
        string? toolCallsJson = null,
        string? logprobsJson = null,
        string? audioJson = null,
        string? responseId = null,
        IReadOnlyList<string>? finishReasons = null,
        string? upstreamRequestId = null,
        double? upstreamProcessingMs = null,
        double? upstreamQueueMs = null,
        double? upstreamTimeToFirstTokenMs = null,
        double? upstreamPrefillMs = null,
        double? upstreamDecodeMs = null) =>
        new(
            isConfigured: true,
            success: true,
            content,
            "Inference completed.",
            usage.Equals(default) ? TokenUsage.Empty : usage,
            providerName,
            requestModel,
            responseModel,
            responseId,
            systemFingerprint,
            toolCallsJson,
            logprobsJson,
            audioJson,
            finishReasons,
            upstreamRequestId,
            upstreamProcessingMs,
            upstreamQueueMs,
            upstreamTimeToFirstTokenMs,
            upstreamPrefillMs,
            upstreamDecodeMs,
            latencyMs,
            errorType: null);

    private static string[] NormalizeFinishReasons(IReadOnlyList<string>? finishReasons)
    {
        if (finishReasons is null || finishReasons.Count == 0)
        {
            return [];
        }

        List<string> normalized = [];
        foreach (string reason in finishReasons)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            normalized.Add(reason.Trim());
        }

        return normalized.Count == 0 ? [] : normalized.ToArray();
    }
}

public readonly record struct TokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens,
    int CachedPromptTokens = 0,
    int PromptAudioTokens = 0,
    int CompletionReasoningTokens = 0,
    int CompletionAudioTokens = 0,
    int AcceptedPredictionTokens = 0,
    int RejectedPredictionTokens = 0)
{
    public static TokenUsage Empty { get; } = new(0, 0, 0);
}
