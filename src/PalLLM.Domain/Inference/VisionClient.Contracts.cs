// VisionClient wire contracts: the vision describe/world-state request +
// the OpenAI-compatible multimodal chat-completions DTOs + VisionResult.
// Split out of VisionClient.cs (same namespace).
using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

public sealed class VisionRequest
{
    public string ImageBase64 { get; init; } = string.Empty;

    public string? ImageMimeType { get; init; }

    public string SystemPrompt { get; init; } = string.Empty;

    public string UserPrompt { get; init; } = string.Empty;

    public int? MaxTokens { get; init; }

    public float? Temperature { get; init; }

    /// <summary>
    /// Optional OpenAI-style <c>response_format</c> value forwarded verbatim to
    /// the chat-completions body. When set to a <c>json_schema</c> wrapper,
    /// OpenAI-compatible endpoints that support structured outputs (including
    /// llama.cpp) constrain the model's output to the
    /// supplied schema. Endpoints that do not recognise the field
    /// silently ignore it; the orchestrator's graceful-fail JSON parser
    /// still handles either case.
    /// </summary>
    public JsonElement? ResponseFormat { get; init; }
}

internal sealed class VisionChatCompletionsRequestBody
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("temperature")]
    public float Temperature { get; init; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("messages")]
    public VisionChatMessage[] Messages { get; init; } = [];

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? ResponseFormat { get; init; }

    [JsonPropertyName("mm_processor_kwargs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MultimodalProcessorOptions? MmProcessorKwargs { get; init; }
}

internal sealed class VisionChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public JsonElement Content { get; init; }
}

internal sealed class VisionContentPart
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VisionImageUrl? ImageUrl { get; init; }

    [JsonPropertyName("uuid")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Uuid { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }
}

internal sealed class VisionImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;
}

public sealed class VisionResult
{
    private VisionResult(
        bool isConfigured,
        bool success,
        string? content,
        string statusMessage,
        TokenUsage usage,
        string providerName,
        string requestModel,
        string? responseModel,
        long latencyMs,
        IReadOnlyList<string>? finishReasons,
        string? upstreamRequestId,
        double? upstreamProcessingMs,
        double? upstreamQueueMs,
        double? upstreamTimeToFirstTokenMs,
        double? upstreamPrefillMs,
        double? upstreamDecodeMs,
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
        LatencyMs = Math.Max(0, latencyMs);
        FinishReasons = NormalizeFinishReasons(finishReasons);
        UpstreamRequestId = HttpResponseReceiptExtractor.NormalizeIdentifier(upstreamRequestId);
        UpstreamProcessingMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamProcessingMs);
        UpstreamQueueMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamQueueMs);
        UpstreamTimeToFirstTokenMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamTimeToFirstTokenMs);
        UpstreamPrefillMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamPrefillMs);
        UpstreamDecodeMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamDecodeMs);
        ErrorType = errorType ?? string.Empty;
    }

    public bool IsConfigured { get; }

    public bool Success { get; }

    public string? Content { get; }

    public string StatusMessage { get; }

    public TokenUsage Usage { get; }

    public string ProviderName { get; }

    public string RequestModel { get; }

    public string ResponseModel { get; }

    public long LatencyMs { get; }

    public IReadOnlyList<string> FinishReasons { get; }

    public string UpstreamRequestId { get; }

    public double? UpstreamProcessingMs { get; }

    public double? UpstreamQueueMs { get; }

    public double? UpstreamTimeToFirstTokenMs { get; }

    public double? UpstreamPrefillMs { get; }

    public double? UpstreamDecodeMs { get; }

    public string ErrorType { get; }

    public static VisionResult Disabled(
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
            latencyMs: 0,
            finishReasons: null,
            upstreamRequestId: null,
            upstreamProcessingMs: null,
            upstreamQueueMs: null,
            upstreamTimeToFirstTokenMs: null,
            upstreamPrefillMs: null,
            upstreamDecodeMs: null,
            errorType: null);

    public static VisionResult Failed(
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
            latencyMs,
            finishReasons: null,
            upstreamRequestId,
            upstreamProcessingMs,
            upstreamQueueMs,
            upstreamTimeToFirstTokenMs,
            upstreamPrefillMs,
            upstreamDecodeMs,
            errorType);

    public static VisionResult Succeeded(
        string content,
        TokenUsage usage = default,
        string providerName = "",
        string requestModel = "",
        string? responseModel = null,
        long latencyMs = 0,
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
            "Vision describe completed.",
            usage.Equals(default) ? TokenUsage.Empty : usage,
            providerName,
            requestModel,
            responseModel,
            latencyMs,
            finishReasons,
            upstreamRequestId,
            upstreamProcessingMs,
            upstreamQueueMs,
            upstreamTimeToFirstTokenMs,
            upstreamPrefillMs,
            upstreamDecodeMs,
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
