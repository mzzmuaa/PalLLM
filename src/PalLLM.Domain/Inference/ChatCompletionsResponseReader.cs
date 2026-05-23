using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PalLLM.Domain.Inference;

internal static class ChatCompletionsResponseReader
{
    public static async Task<ChatCompletionsReadResult> ReadAsync(
        HttpContent httpContent,
        int maxBytes,
        string responseLabel,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await HttpContentReadLimiter.ParseJsonDocumentAsync(
                httpContent,
                maxBytes,
                responseLabel,
                cancellationToken)
            .ConfigureAwait(false);

        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("choices", out JsonElement choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return ChatCompletionsReadResult.Failed("returned no choices.");
        }

        JsonElement choice = choices[0];
        if (!choice.TryGetProperty("message", out JsonElement message))
        {
            return ChatCompletionsReadResult.Failed("returned an unexpected payload.");
        }

        string messageContent = string.Empty;
        bool hasTextContent = message.TryGetProperty("content", out JsonElement contentElement) &&
            TryExtractTextContent(contentElement, out messageContent);
        bool hasToolCalls = TryExtractToolCalls(message, out string toolCallsJson);
        string audioJson = ParseFirstMessageAudio(choices);
        if (!hasTextContent && !hasToolCalls && string.IsNullOrEmpty(audioJson))
        {
            return ChatCompletionsReadResult.Failed("returned an unsupported message content shape.");
        }

        return ChatCompletionsReadResult.Succeeded(
            hasTextContent ? messageContent : string.Empty,
            ParseUsage(root),
            ParseString(root, "model"),
            ParseString(root, "id"),
            ParseString(root, "system_fingerprint"),
            ParseFinishReasons(choices),
            toolCallsJson,
            ParseChoiceLogprobs(choice),
            audioJson);
    }

    private static TokenUsage ParseUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return TokenUsage.Empty;
        }

        int prompt = ParseNonNegativeInt(usage, "prompt_tokens");
        int completion = ParseNonNegativeInt(usage, "completion_tokens");
        int total = ParseNonNegativeInt(usage, "total_tokens");
        if (total == 0 && (prompt > 0 || completion > 0))
        {
            total = prompt + completion;
        }

        int cachedPromptTokens = ParseUsageDetailInt(usage, "prompt_tokens_details", "cached_tokens");
        int promptAudioTokens = ParseUsageDetailInt(usage, "prompt_tokens_details", "audio_tokens");
        int completionReasoningTokens = ParseUsageDetailInt(usage, "completion_tokens_details", "reasoning_tokens");
        int completionAudioTokens = ParseUsageDetailInt(usage, "completion_tokens_details", "audio_tokens");
        int acceptedPredictionTokens = ParseUsageDetailInt(
            usage,
            "completion_tokens_details",
            "accepted_prediction_tokens");
        int rejectedPredictionTokens = ParseUsageDetailInt(
            usage,
            "completion_tokens_details",
            "rejected_prediction_tokens");

        return new TokenUsage(
            prompt,
            completion,
            total,
            cachedPromptTokens,
            promptAudioTokens,
            completionReasoningTokens,
            completionAudioTokens,
            acceptedPredictionTokens,
            rejectedPredictionTokens);
    }

    private static int ParseUsageDetailInt(
        JsonElement usage,
        string objectPropertyName,
        string valuePropertyName)
    {
        if (!usage.TryGetProperty(objectPropertyName, out JsonElement details) ||
            details.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        return ParseNonNegativeInt(details, valuePropertyName);
    }

    private static int ParseNonNegativeInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return 0;
        }

        int parsed = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(
                value.GetString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int number) => number,
            _ => 0,
        };

        return Math.Max(0, parsed);
    }

    private static string ParseString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string[] ParseFinishReasons(JsonElement choices)
    {
        List<string> finishReasons = [];
        foreach (JsonElement choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("finish_reason", out JsonElement finishReason) &&
                finishReason.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(finishReason.GetString()))
            {
                finishReasons.Add(finishReason.GetString()!);
            }
        }

        return finishReasons.Count == 0 ? [] : finishReasons.ToArray();
    }

    private static bool TryExtractTextContent(JsonElement contentElement, out string content)
    {
        switch (contentElement.ValueKind)
        {
            case JsonValueKind.String:
                content = contentElement.GetString() ?? string.Empty;
                return true;

            case JsonValueKind.Array:
                var builder = new StringBuilder();
                bool matchedPart = false;

                foreach (JsonElement part in contentElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(part.GetString());
                        matchedPart = true;
                        continue;
                    }

                    if (part.ValueKind != JsonValueKind.Object ||
                        !TryExtractTextPart(part, out string partText))
                    {
                        continue;
                    }

                    builder.Append(partText);
                    matchedPart = true;
                }

                content = builder.ToString();
                return matchedPart;

            default:
                content = string.Empty;
                return false;
        }
    }

    private static bool TryExtractToolCalls(JsonElement message, out string toolCallsJson)
    {
        if (message.TryGetProperty("tool_calls", out JsonElement toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array &&
            toolCalls.GetArrayLength() > 0)
        {
            toolCallsJson = toolCalls.GetRawText();
            return true;
        }

        if (message.TryGetProperty("function_call", out JsonElement legacyFunctionCall) &&
            legacyFunctionCall.ValueKind == JsonValueKind.Object)
        {
            toolCallsJson = legacyFunctionCall.GetRawText();
            return true;
        }

        toolCallsJson = string.Empty;
        return false;
    }

    private static string ParseChoiceLogprobs(JsonElement choice)
    {
        if (choice.TryGetProperty("logprobs", out JsonElement logprobs) &&
            logprobs.ValueKind == JsonValueKind.Object)
        {
            return logprobs.GetRawText();
        }

        return string.Empty;
    }

    private static string ParseFirstMessageAudio(JsonElement choices)
    {
        foreach (JsonElement choice in choices.EnumerateArray())
        {
            if (choice.TryGetProperty("message", out JsonElement message) &&
                message.TryGetProperty("audio", out JsonElement audio) &&
                audio.ValueKind == JsonValueKind.Object)
            {
                return audio.GetRawText();
            }
        }

        return string.Empty;
    }

    private static bool TryExtractTextPart(JsonElement part, out string text)
    {
        if (part.TryGetProperty("text", out JsonElement textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            text = textElement.GetString() ?? string.Empty;
            return true;
        }

        if (part.TryGetProperty("content", out JsonElement contentElement) &&
            contentElement.ValueKind == JsonValueKind.String)
        {
            text = contentElement.GetString() ?? string.Empty;
            return true;
        }

        text = string.Empty;
        return false;
    }
}

internal readonly record struct ChatCompletionsReadResult(
    bool Success,
    string Content,
    TokenUsage Usage,
    string ResponseModel,
    string ResponseId,
    string SystemFingerprint,
    string[] FinishReasons,
    string ToolCallsJson,
    string LogprobsJson,
    string AudioJson,
    string ErrorMessage)
{
    public static ChatCompletionsReadResult Succeeded(
        string content,
        TokenUsage usage,
        string responseModel,
        string responseId,
        string systemFingerprint,
        string[] finishReasons,
        string toolCallsJson = "",
        string logprobsJson = "",
        string audioJson = "") =>
        new(true, content, usage, responseModel, responseId, systemFingerprint, finishReasons, toolCallsJson, logprobsJson, audioJson, string.Empty);

    public static ChatCompletionsReadResult Failed(string errorMessage) =>
        new(false, string.Empty, TokenUsage.Empty, string.Empty, string.Empty, string.Empty, [], string.Empty, string.Empty, string.Empty, errorMessage);
}
