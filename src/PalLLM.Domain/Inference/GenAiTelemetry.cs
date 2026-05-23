using System.Diagnostics;
using System.Net;

using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

internal readonly record struct GenAiOperationContext(
    string OperationName,
    string ProviderName,
    string RequestModel,
    string? ResponseModel,
    string? ServerAddress,
    int? ServerPort,
    string? OutputType,
    int? MaxTokens,
    double? Temperature,
    double? TopP,
    double? PresencePenalty,
    string? ConversationId);

internal static class GenAiTelemetry
{
    internal const string OperationChat = "chat";
    internal const string OperationGenerateContent = "generate_content";
    internal const string OutputTypeText = "text";
    internal const string OutputTypeJson = "json";
    internal const string ErrorTypeCancelled = "cancelled";
    internal const string ErrorTypeInvalidResponse = "invalid_response";

    private const string OperationNameTag = "gen_ai.operation.name";
    private const string ProviderNameTag = "gen_ai.provider.name";
    private const string RequestModelTag = "gen_ai.request.model";
    private const string ResponseModelTag = "gen_ai.response.model";
    private const string ResponseIdTag = "gen_ai.response.id";
    private const string ResponseFinishReasonsTag = "gen_ai.response.finish_reasons";
    private const string InputTokensTag = "gen_ai.usage.input_tokens";
    private const string OutputTokensTag = "gen_ai.usage.output_tokens";
    private const string ReasoningOutputTokensTag = "gen_ai.usage.reasoning.output_tokens";
    private const string OutputTypeTag = "gen_ai.output.type";
    private const string MaxTokensTag = "gen_ai.request.max_tokens";
    private const string TemperatureTag = "gen_ai.request.temperature";
    private const string TopPTag = "gen_ai.request.top_p";
    private const string PresencePenaltyTag = "gen_ai.request.presence_penalty";
    private const string ConversationIdTag = "gen_ai.conversation.id";
    private const string TokenTypeTag = "gen_ai.token.type";
    private const string ErrorTypeTag = "error.type";
    private const string ServerAddressTag = "server.address";
    private const string ServerPortTag = "server.port";

    internal static GenAiOperationContext CreateContext(
        string operationName,
        string baseUrl,
        string requestModel,
        string? outputType,
        int? maxTokens = null,
        double? temperature = null,
        double? topP = null,
        double? presencePenalty = null,
        string? conversationId = null)
    {
        Uri? endpoint = TryParseBaseUrl(baseUrl);
        string? serverAddress = endpoint?.Host;
        int? serverPort = endpoint is not null && !endpoint.IsDefaultPort ? endpoint.Port : null;

        return new GenAiOperationContext(
            OperationName: operationName,
            ProviderName: ResolveProviderName(endpoint, baseUrl),
            RequestModel: requestModel,
            ResponseModel: null,
            ServerAddress: string.IsNullOrWhiteSpace(serverAddress) ? null : serverAddress,
            ServerPort: serverPort,
            OutputType: outputType,
            MaxTokens: maxTokens,
            Temperature: temperature,
            TopP: topP,
            PresencePenalty: presencePenalty,
            ConversationId: conversationId);
    }

    internal static string GetProviderName(string baseUrl) =>
        ResolveProviderName(TryParseBaseUrl(baseUrl), baseUrl);

    internal static Activity? StartClientActivity(in GenAiOperationContext context)
    {
        string spanName = string.IsNullOrWhiteSpace(context.RequestModel)
            ? context.OperationName
            : $"{context.OperationName} {context.RequestModel}";
        Activity? activity = PalLlmTelemetry.Source.StartActivity(spanName, ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag(OperationNameTag, context.OperationName);
        activity.SetTag(ProviderNameTag, context.ProviderName);

        if (!string.IsNullOrWhiteSpace(context.RequestModel))
        {
            activity.SetTag(RequestModelTag, context.RequestModel);
        }

        if (!string.IsNullOrWhiteSpace(context.OutputType))
        {
            activity.SetTag(OutputTypeTag, context.OutputType);
        }

        if (context.MaxTokens.HasValue)
        {
            activity.SetTag(MaxTokensTag, context.MaxTokens.Value);
        }

        if (context.Temperature.HasValue)
        {
            activity.SetTag(TemperatureTag, context.Temperature.Value);
        }

        if (context.TopP.HasValue)
        {
            activity.SetTag(TopPTag, context.TopP.Value);
        }

        if (context.PresencePenalty.HasValue)
        {
            activity.SetTag(PresencePenaltyTag, context.PresencePenalty.Value);
        }

        if (!string.IsNullOrWhiteSpace(context.ConversationId))
        {
            activity.SetTag(ConversationIdTag, context.ConversationId);
        }

        if (!string.IsNullOrWhiteSpace(context.ServerAddress))
        {
            activity.SetTag(ServerAddressTag, context.ServerAddress);
        }

        if (context.ServerPort.HasValue)
        {
            activity.SetTag(ServerPortTag, context.ServerPort.Value);
        }

        return activity;
    }

    internal static void ApplyResponse(Activity? activity, ChatCompletionsReadResult response)
    {
        if (activity is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(response.ResponseModel))
        {
            activity.SetTag(ResponseModelTag, response.ResponseModel);
        }

        if (!string.IsNullOrWhiteSpace(response.ResponseId))
        {
            activity.SetTag(ResponseIdTag, response.ResponseId);
        }

        if (response.FinishReasons.Length > 0)
        {
            activity.SetTag(ResponseFinishReasonsTag, response.FinishReasons);
        }

        if (response.Usage.PromptTokens > 0)
        {
            activity.SetTag(InputTokensTag, response.Usage.PromptTokens);
        }

        if (response.Usage.CompletionTokens > 0)
        {
            activity.SetTag(OutputTokensTag, response.Usage.CompletionTokens);
        }

        if (response.Usage.CompletionReasoningTokens > 0)
        {
            activity.SetTag(ReasoningOutputTokensTag, response.Usage.CompletionReasoningTokens);
        }
    }

    internal static void MarkError(Activity? activity, string errorType)
    {
        if (activity is null || string.IsNullOrWhiteSpace(errorType))
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, errorType);
        activity.SetTag(ErrorTypeTag, errorType);
    }

    internal static void RecordOperationDuration(
        in GenAiOperationContext context,
        TimeSpan duration,
        string? errorType = null)
    {
        TagList tags = BuildCommonMetricTags(context, errorType);
        PalLlmTelemetry.GenAiClientOperationDuration.Record(duration.TotalSeconds, tags);
    }

    internal static void RecordTokenUsage(in GenAiOperationContext context, TokenUsage usage)
    {
        if (usage.PromptTokens > 0)
        {
            TagList inputTags = BuildCommonMetricTags(context);
            inputTags.Add(TokenTypeTag, "input");
            PalLlmTelemetry.GenAiClientTokenUsage.Record(usage.PromptTokens, inputTags);
        }

        if (usage.CompletionTokens > 0)
        {
            TagList outputTags = BuildCommonMetricTags(context);
            outputTags.Add(TokenTypeTag, "output");
            PalLlmTelemetry.GenAiClientTokenUsage.Record(usage.CompletionTokens, outputTags);
        }
    }

    private static TagList BuildCommonMetricTags(
        in GenAiOperationContext context,
        string? errorType = null)
    {
        TagList tags = default;
        tags.Add(OperationNameTag, context.OperationName);
        tags.Add(ProviderNameTag, context.ProviderName);

        if (!string.IsNullOrWhiteSpace(context.RequestModel))
        {
            tags.Add(RequestModelTag, context.RequestModel);
        }

        if (!string.IsNullOrWhiteSpace(context.ResponseModel))
        {
            tags.Add(ResponseModelTag, context.ResponseModel);
        }

        if (!string.IsNullOrWhiteSpace(context.ServerAddress))
        {
            tags.Add(ServerAddressTag, context.ServerAddress);
        }

        if (context.ServerPort.HasValue)
        {
            tags.Add(ServerPortTag, context.ServerPort.Value);
        }

        if (!string.IsNullOrWhiteSpace(errorType))
        {
            tags.Add(ErrorTypeTag, errorType);
        }

        return tags;
    }

    private static Uri? TryParseBaseUrl(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri) ? uri : null;

    private static string ResolveProviderName(Uri? endpoint, string baseUrl)
    {
        string host = endpoint?.Host ?? string.Empty;
        string path = endpoint?.AbsolutePath ?? string.Empty;
        bool localRuntimeAddress = IsLoopbackOrPrivateEndpoint(endpoint);

        if (host.EndsWith("openai.com", StringComparison.OrdinalIgnoreCase))
        {
            return "openai";
        }

        if (host.EndsWith("openai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return "azure.ai.openai";
        }

        if (host.EndsWith("services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("models.inference.ai.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return "azure.ai.inference";
        }

        if (host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            return "gcp.gemini";
        }

        if (host.Equals("aiplatform.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            return "gcp.vertex_ai";
        }

        if (host.EndsWith("anthropic.com", StringComparison.OrdinalIgnoreCase))
        {
            return "anthropic";
        }

        if (host.EndsWith("cohere.com", StringComparison.OrdinalIgnoreCase))
        {
            return "cohere";
        }

        if (host.EndsWith("groq.com", StringComparison.OrdinalIgnoreCase))
        {
            return "groq";
        }

        if (host.EndsWith("deepseek.com", StringComparison.OrdinalIgnoreCase))
        {
            return "deepseek";
        }

        if (host.EndsWith("mistral.ai", StringComparison.OrdinalIgnoreCase))
        {
            return "mistral_ai";
        }

        if (host.EndsWith("perplexity.ai", StringComparison.OrdinalIgnoreCase))
        {
            return "perplexity";
        }

        if (host.EndsWith("x.ai", StringComparison.OrdinalIgnoreCase))
        {
            return "x_ai";
        }

        if (baseUrl.Contains("googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            return "gcp.gen_ai";
        }

        // Pass 346: Ollama host detection removed. Port 11434 and the
        // "ollama" host substring no longer resolve to a recognised
        // provider — they fall through to "openai_compat" the same way
        // any other unknown OpenAI-compatible endpoint does.

        if (host.Contains("lmstudio", StringComparison.OrdinalIgnoreCase)
            || host.Contains("lm-studio", StringComparison.OrdinalIgnoreCase)
            || (localRuntimeAddress && endpoint?.Port == 1234))
        {
            return "lmstudio";
        }

        if (host.Contains("llamacpp", StringComparison.OrdinalIgnoreCase)
            || host.Contains("llama-cpp", StringComparison.OrdinalIgnoreCase)
            || (localRuntimeAddress && endpoint?.Port == 8080))
        {
            return "llama.cpp";
        }

        if (host.Contains("vllm", StringComparison.OrdinalIgnoreCase))
        {
            return "vllm";
        }

        if (host.Contains("sglang", StringComparison.OrdinalIgnoreCase))
        {
            return "sglang";
        }

        if (host.Contains("tensorrt", StringComparison.OrdinalIgnoreCase)
            || host.Contains("trtllm", StringComparison.OrdinalIgnoreCase)
            || host.Contains("trt-llm", StringComparison.OrdinalIgnoreCase))
        {
            return "tensorrt_llm";
        }

        if (host.Contains("openvino", StringComparison.OrdinalIgnoreCase)
            || (localRuntimeAddress && path.StartsWith("/v3", StringComparison.OrdinalIgnoreCase)))
        {
            return "openvino";
        }

        if (host.Contains("foundry", StringComparison.OrdinalIgnoreCase)
            || (localRuntimeAddress && path.StartsWith("/openai", StringComparison.OrdinalIgnoreCase)))
        {
            return "foundry_local";
        }

        if (host.Contains("transformers", StringComparison.OrdinalIgnoreCase))
        {
            return "transformers";
        }

        return "openai_compatible";
    }

    private static bool IsLoopbackOrPrivateEndpoint(Uri? endpoint)
    {
        if (endpoint is null)
        {
            return false;
        }

        if (endpoint.IsLoopback || endpoint.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!IPAddress.TryParse(endpoint.Host, out IPAddress? address))
        {
            return false;
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        byte[] bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork =>
                bytes[0] == 10
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168),
            System.Net.Sockets.AddressFamily.InterNetworkV6 =>
                (bytes[0] & 0xfe) == 0xfc,
            _ => false,
        };
    }
}
