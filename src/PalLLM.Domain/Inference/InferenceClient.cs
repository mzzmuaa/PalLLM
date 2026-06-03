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

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    The HTTP client that talks to whichever OpenAI-compatible
//            inference engine the operator wired (local engine / vLLM /
//            llama.cpp / TGI / SGLang / direct OpenAI). Owns timeout,
//            circuit-breaker integration, response-bytes cap, structured
//            response-path classification (live / fallback / circuit-open).
//   surface: InferenceClient.ChatAsync (the call site of every model
//            request); ChatStreamAsync for streaming tokens.
//   gate:    None directly; behaviour pinned by InferenceClientTests
//            and the circuit-breaker tests.
//   adr:     0002-portable-adapter-seam.md (the HTTP surface PalLLM
//            consumes; operator can swap engines without touching domain).
//   docs:    docs/QUANTIZATION.md (which engine fits which quant),
//            docs/BLACKWELL_RECIPES.md (vLLM + NVFP4 path),
//            docs/HOT_PATH.md (timeout + budget),
//            docs/RUNBOOK.md ("inference returns deterministic replies
//            even with inference enabled").
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Inference;

public interface IInferenceClient
{
    Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken);
}

public interface IInferenceLaneMetadata
{
    string GetActiveModelId();

    string? GetActiveTierId();

    IReadOnlyList<string> GetLastSeenAvailableModels();
}

public sealed record InferenceWarmupTransportResult(
    InferenceResult Result,
    string Transport,
    bool ResidencyHintApplied,
    string ResidencyProvider);

public sealed class HttpJsonInferenceClient : IInferenceClient, IInferenceLaneMetadata
{
    private const string ChatCompletionsPath = "chat/completions";
    private const string ResponseLabel = "Inference response";
    private const float DefaultTopP = 0.8f;
    private const float DefaultPresencePenalty = 1.5f;
    private const int MaxRequestHintIdentifierLength = 128;
    private const int MaxClientRequestIdLength = 512;

    // Hosts whose API surface expects an extra enable_thinking request-body flag.
    // Keeping this as a private const array lets operators who point PalLLM at
    // such a host keep working without surfacing the brand in prompts or docs.
    private static readonly string[] ThinkingToggleHostMarkers = ["dashscope", "aliyuncs.com"];

    private readonly HttpClient _httpClient;
    private readonly PalLlmOptions _options;
    private readonly InferenceCircuitBreaker _circuitBreaker;
    private readonly ModelTierOrchestrator? _tierOrchestrator;
    private readonly ThermalGate? _thermalGate;

    public HttpJsonInferenceClient(
        HttpClient httpClient,
        PalLlmOptions options,
        ModelTierOrchestrator? tierOrchestrator = null,
        ThermalGate? thermalGate = null)
    {
        _httpClient = httpClient;
        _options = options;
        _tierOrchestrator = tierOrchestrator;
        _circuitBreaker = new InferenceCircuitBreaker
        {
            FailureThreshold = Math.Max(0, options.Inference.CircuitBreakerFailureThreshold),
            Cooldown = TimeSpan.FromSeconds(Math.Max(1, options.Inference.CircuitBreakerCooldownSeconds)),
        };

        // Thermal gate is opt-in. When disabled we don't even construct one
        // so we never pay the sensor read or spawn nvidia-smi.
        if (options.Inference.ThermalGate.Enabled)
        {
            _thermalGate = thermalGate ?? new ThermalGate
            {
                RejectAboveC = options.Inference.ThermalGate.RejectAboveC,
                WarnAboveC = options.Inference.ThermalGate.WarnAboveC,
                CacheTtl = TimeSpan.FromSeconds(Math.Max(1, options.Inference.ThermalGate.CacheTtlSeconds)),
            };
        }
    }

    /// Exposed so the runtime can surface breaker state in RuntimeHealth.
    public InferenceCircuitBreaker CircuitBreaker => _circuitBreaker;

    /// Exposed so dashboards and metrics can surface gate state when the gate
    /// is enabled. <c>null</c> when the gate is configured off.
    public ThermalGate? ThermalGate => _thermalGate;

    public string GetActiveModelId() => _tierOrchestrator?.GetActiveModel() ?? _options.Inference.Model;

    public string? GetActiveTierId() => _tierOrchestrator?.GetActiveTierId();

    public IReadOnlyList<string> GetLastSeenAvailableModels() =>
        _tierOrchestrator?.GetLastSeenAvailableModels() ?? Array.Empty<string>();

    public async Task<InferenceWarmupTransportResult> WarmAsync(
        InferencePrompt prompt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        InferenceOptions inference = _options.Inference;
        ResolvedInferenceResidency residency = InferenceResidencyPolicy.Resolve(inference);

        // Pass 346: warmup now goes through the generic OpenAI-compatible
        // chat-completions path for every supported engine (llama-server is
        // PalLLM's bundled default; LM Studio can additionally carry the
        // per-request `ttl` field surfaced by ResidencyHintApplied below).
        // llama.cpp keeps the loaded model resident for the lifetime of the
        // server process, so no per-request keep-alive hint is needed.
        InferenceResult genericResult = await CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
        return new InferenceWarmupTransportResult(
            Result: genericResult,
            Transport: "chat_completions",
            ResidencyHintApplied: residency.SupportsChatCompletionsTtl && residency.TtlSeconds > 0,
            ResidencyProvider: residency.ProviderId);
    }

    public async Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken)
    {
        InferenceOptions inference = _options.Inference;
        string activeModel = _tierOrchestrator?.GetActiveModel() ?? inference.Model;
        string providerName = GenAiTelemetry.GetProviderName(inference.BaseUrl);
        if (!inference.Enabled ||
            string.IsNullOrWhiteSpace(inference.BaseUrl) ||
            string.IsNullOrWhiteSpace(activeModel))
        {
            return InferenceResult.Disabled(
                "Inference is disabled. Configure PalLLM:Inference to enable live model calls.",
                providerName,
                activeModel);
        }

        // Retry budget: the first attempt plus up to MaxTransientRetries follow-ups.
        int maxAttempts = 1 + Math.Max(0, inference.MaxTransientRetries);
        InferenceResult? lastResult = null;

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Circuit-breaker gate checked on every attempt so a concurrent failure
            // that opens the breaker mid-retry can still short-circuit us.
            if (inference.CircuitBreakerFailureThreshold > 0 && !_circuitBreaker.ShouldAllowCall())
            {
                return InferenceResult.Failed(
                    $"Inference circuit breaker open (cooling down {inference.CircuitBreakerCooldownSeconds}s). Routing to fallback.",
                    providerName,
                    activeModel,
                    latencyMs: 0,
                    errorType: "circuit_open");
            }

            // Thermal gate (opt-in). When the primary GPU is already throttling,
            // the round-trip latency cost of running the big model on top of a
            // throttled card is the same as just using the deterministic
            // fallback director — so prefer fallback so the player-visible
            // latency stays predictable under thermal pressure.
            if (_thermalGate is { } gate)
            {
                ThermalGateResult decision = gate.Evaluate();
                if (decision.Decision == ThermalGateDecision.Reject)
                {
                    return InferenceResult.Failed(
                        $"Inference gated by thermal policy: {decision.Reason}. Routing to fallback.",
                        providerName,
                        activeModel,
                        latencyMs: 0,
                        errorType: "thermal_gated");
                }
            }

            (InferenceResult result, bool transient) = await AttemptOnceAsync(prompt, inference, activeModel, cancellationToken)
                .ConfigureAwait(false);

            if (result.Success)
            {
                return result;
            }

            lastResult = result;
            // Only retry transient failures (network / timeout / 5xx). Deterministic
            // 4xx / parse errors will return the same thing — retrying wastes time.
            if (!transient || attempt == maxAttempts - 1)
            {
                break;
            }

            int backoff = ComputeBackoffMs(inference.TransientRetryBackoffMs, attempt);
            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return lastResult ?? result;
            }
        }

        return lastResult ?? InferenceResult.Failed(
            "Inference attempt never produced a result.",
            providerName,
            activeModel,
            errorType: "no_result");
    }

    private async Task<(InferenceResult Result, bool Transient)> AttemptOnceAsync(
        InferencePrompt prompt,
        InferenceOptions inference,
        string activeModel,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, inference.TimeoutSeconds));
        int maxResponseBytes = Math.Max(1_024, inference.MaxResponseBytes);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        CancellationToken effectiveCancellationToken = requestTimeout.Token;

        InferenceChatCompletionsRequestBody requestBody = BuildRequestBody(prompt, inference, activeModel);
        GenAiOperationContext telemetryContext = GenAiTelemetry.CreateContext(
            GenAiTelemetry.OperationChat,
            inference.BaseUrl,
            activeModel,
            requestBody.ResponseFormat.HasValue ? GenAiTelemetry.OutputTypeJson : GenAiTelemetry.OutputTypeText,
            maxTokens: requestBody.TokenBudget,
            temperature: requestBody.Temperature,
            topP: requestBody.TopP,
            presencePenalty: requestBody.PresencePenalty);
        using Activity? activity = GenAiTelemetry.StartClientActivity(telemetryContext);
        long startedAt = Stopwatch.GetTimestamp();
        string? errorType = null;

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(inference.BaseUrl));
        if (!string.IsNullOrWhiteSpace(inference.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", inference.ApiKey);
        }

        AddClientRequestIdHeader(request, inference.ClientRequestIdHeader, prompt.ClientRequestId);

        request.Content = JsonContent.Create(
            requestBody,
            PalLlmDomainJsonSerializerContext.Default.InferenceChatCompletionsRequestBody);

        HttpResponseMessage? response = null;
        string upstreamRequestId = string.Empty;
        double? upstreamProcessingMs = null;
        UpstreamPhaseTimingReceipt upstreamPhaseTimings = UpstreamPhaseTimingReceipt.Empty;
        try
        {
            response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            upstreamRequestId = HttpResponseReceiptExtractor.GetUpstreamRequestId(response);
            upstreamProcessingMs = HttpResponseReceiptExtractor.GetUpstreamProcessingMs(response);
            upstreamPhaseTimings = HttpResponseReceiptExtractor.GetUpstreamPhaseTimings(response);
            if (!response.IsSuccessStatusCode)
            {
                _circuitBreaker.RecordFailure();
                int statusCode = (int)response.StatusCode;
                bool transient = statusCode >= 500 || statusCode == 408 || statusCode == 429;
                _ = await ReadStatusBodyAsync(response.Content, maxResponseBytes, effectiveCancellationToken)
                    .ConfigureAwait(false);
                errorType = statusCode.ToString();
                GenAiTelemetry.MarkError(activity, errorType);
                return (InferenceResult.Failed(
                    TransportFailureStatusBuilder.HttpStatus("Inference", statusCode),
                    telemetryContext.ProviderName,
                    activeModel,
                    latencyMs: GetElapsedMilliseconds(startedAt),
                    errorType: errorType,
                    upstreamRequestId: upstreamRequestId,
                    upstreamProcessingMs: upstreamProcessingMs,
                    upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                    upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                    upstreamDecodeMs: upstreamPhaseTimings.DecodeMs), transient);
            }

            ChatCompletionsReadResult parsed = await ChatCompletionsResponseReader.ReadAsync(
                    response.Content,
                    maxResponseBytes,
                    ResponseLabel,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            if (!parsed.Success)
            {
                _circuitBreaker.RecordFailure();
                errorType = GenAiTelemetry.ErrorTypeInvalidResponse;
                GenAiTelemetry.MarkError(activity, errorType);
                return (InferenceResult.Failed(
                    $"Inference endpoint {parsed.ErrorMessage}",
                    telemetryContext.ProviderName,
                    activeModel,
                    responseModel: parsed.ResponseModel,
                    latencyMs: GetElapsedMilliseconds(startedAt),
                    errorType: errorType,
                    upstreamRequestId: upstreamRequestId,
                    upstreamProcessingMs: upstreamProcessingMs,
                    upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                    upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                    upstreamDecodeMs: upstreamPhaseTimings.DecodeMs), false);
            }

            telemetryContext = telemetryContext with
            {
                ResponseModel = string.IsNullOrWhiteSpace(parsed.ResponseModel) ? null : parsed.ResponseModel,
            };
            GenAiTelemetry.ApplyResponse(activity, parsed);
            GenAiTelemetry.RecordTokenUsage(telemetryContext, parsed.Usage);

            string cleaned = ResponseCleanup.StripReasoning(parsed.Content);
            _circuitBreaker.RecordSuccess();
            return (InferenceResult.Succeeded(
                cleaned,
                parsed.Usage,
                telemetryContext.ProviderName,
                activeModel,
                parsed.ResponseModel,
                GetElapsedMilliseconds(startedAt),
                responseId: parsed.ResponseId,
                systemFingerprint: parsed.SystemFingerprint,
                toolCallsJson: parsed.ToolCallsJson,
                logprobsJson: parsed.LogprobsJson,
                audioJson: parsed.AudioJson,
                finishReasons: parsed.FinishReasons,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs), false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errorType = GenAiTelemetry.ErrorTypeCancelled;
            GenAiTelemetry.MarkError(activity, errorType);
            throw;
        }
        catch (OperationCanceledException)
        {
            _circuitBreaker.RecordFailure();
            errorType = "timeout";
            GenAiTelemetry.MarkError(activity, errorType);
            return (InferenceResult.Failed(
                TransportFailureStatusBuilder.Timeout("Inference"),
                telemetryContext.ProviderName,
                activeModel,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs), true);
        }
        catch (HttpRequestException)
        {
            _circuitBreaker.RecordFailure();
            errorType = nameof(HttpRequestException);
            GenAiTelemetry.MarkError(activity, errorType);
            return (InferenceResult.Failed(
                TransportFailureStatusBuilder.Unreachable("Inference"),
                telemetryContext.ProviderName,
                activeModel,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType), true);
        }
        catch (InvalidDataException)
        {
            _circuitBreaker.RecordFailure();
            errorType = "response_too_large";
            GenAiTelemetry.MarkError(activity, errorType);
            return (InferenceResult.Failed(
                HttpContentReadLimiter.BuildExceededLimitMessage(ResponseLabel, maxResponseBytes),
                telemetryContext.ProviderName,
                activeModel,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs), false);
        }
        catch (JsonException)
        {
            // Malformed response body. Not transient — retrying against the same broken
            // endpoint will return the same garbage — but still a real upstream fault,
            // so feed it to the breaker.
            _circuitBreaker.RecordFailure();
            errorType = nameof(JsonException);
            GenAiTelemetry.MarkError(activity, errorType);
            return (InferenceResult.Failed(
                TransportFailureStatusBuilder.MalformedJson("Inference"),
                telemetryContext.ProviderName,
                activeModel,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs), false);
        }
        finally
        {
            GenAiTelemetry.RecordOperationDuration(
                telemetryContext,
                Stopwatch.GetElapsedTime(startedAt),
                errorType);
            response?.Dispose();
        }
    }

    private static async Task<string> ReadStatusBodyAsync(
        HttpContent content,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        int maxErrorBytes = Math.Max(1_024, Math.Min(maxResponseBytes, 8 * 1_024));
        HttpContentReadLimiter.BoundedTextReadResult readResult = await HttpContentReadLimiter.ReadTextAsync(
                content,
                maxErrorBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return readResult.ExceededLimit
            ? $"[response body exceeded {maxErrorBytes} bytes]"
            : readResult.Text;
    }

    // Pass 346: the dedicated Ollama-native warmup transport (WarmOllama,
    // its endpoint+body builders, and the warmup request DTO) was
    // removed alongside the rest of the Ollama back-compat path. The
    // runtime now warms every engine through the generic OpenAI-compatible
    // chat-completions path (llama-server, vLLM, LM Studio, etc.).
    // llama.cpp keeps the loaded model resident for the lifetime of the
    // server process, so no per-request keep-alive hint is needed;
    // LM Studio's per-request `ttl` is still carried via the generic
    // request body when configured.

    private static int ComputeBackoffMs(int baseMs, int attempt)
    {
        if (baseMs <= 0)
        {
            return 0;
        }

        // Exponential-ish with jitter: base * 2^attempt + random(0, base). Attempts
        // stay small so we don't need to cap beyond clamp to int.MaxValue.
        int scaled = baseMs * (1 << Math.Min(attempt, 5));
        int jitter = Random.Shared.Next(0, baseMs);
        return Math.Min(scaled + jitter, 30_000);
    }

    private static long GetElapsedMilliseconds(long startedAt) =>
        Math.Max(0, (long)Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, MidpointRounding.AwayFromZero));

    private static string BuildEndpoint(string baseUrl)
    {
        string normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalized), ChatCompletionsPath).ToString();
    }

    private static InferenceChatCompletionsRequestBody BuildRequestBody(
        InferencePrompt prompt,
        InferenceOptions inference,
        string activeModel)
    {
        bool useFamilySamplingPresets = HasFamilySamplingPresets(activeModel);
        bool usesTemplateThinkingControls = HasTemplateThinkingControls(activeModel);
        bool usesRootThinkingControls = UsesRootThinkingControls(inference.BaseUrl);
        ResolvedInferenceResidency residency = InferenceResidencyPolicy.Resolve(inference);

        float? topP = prompt.TopP ?? inference.TopP ?? (useFamilySamplingPresets ? DefaultTopP : (float?)null);
        float? presencePenalty = prompt.PresencePenalty ?? inference.PresencePenalty ?? (useFamilySamplingPresets ? DefaultPresencePenalty : (float?)null);
        float? frequencyPenalty = prompt.FrequencyPenalty ?? inference.FrequencyPenalty;
        int? topK = prompt.TopK ?? inference.TopK;
        float? minP = prompt.MinP ?? inference.MinP;
        float? repetitionPenalty = prompt.RepetitionPenalty ?? inference.RepetitionPenalty;
        string? reasoningEffort = InferenceReasoningEfforts.Normalize(prompt.ReasoningEffort ?? inference.ReasoningEffort);
        int? thinkingTokenBudget = NormalizeThinkingTokenBudget(prompt.ThinkingTokenBudget ?? inference.ThinkingTokenBudget);
        string tokenBudgetField = InferenceTokenBudgetFields.Normalize(prompt.TokenBudgetField ?? inference.TokenBudgetField);
        bool useMaxCompletionTokens = InferenceTokenBudgetFields.UsesMaxCompletionTokens(tokenBudgetField);
        int? seed = prompt.Seed ?? inference.Seed;
        int? requestPriority = prompt.RequestPriority ?? inference.RequestPriority;
        string? serviceTier = InferenceServiceTiers.Normalize(prompt.ServiceTier ?? inference.ServiceTier);
        string? promptCacheKey = NormalizePromptCacheKey(prompt.PromptCacheKey ?? inference.PromptCacheKey);
        string? promptCacheRetention = InferencePromptCacheRetentions.Normalize(
            prompt.PromptCacheRetention ?? inference.PromptCacheRetention);
        string? verbosity = InferenceVerbosities.Normalize(prompt.Verbosity ?? inference.Verbosity);
        string? safetyIdentifier = NormalizeRequestHintIdentifier(
            prompt.SafetyIdentifier ?? inference.SafetyIdentifier);
        bool? storeCompletions = prompt.StoreCompletions ?? inference.StoreCompletions;
        Dictionary<string, string>? requestMetadata = NormalizeRequestMetadata(
            inference.RequestMetadata,
            prompt.RequestMetadata);
        bool? llamaCppCachePrompt = prompt.LlamaCppCachePrompt ?? inference.LlamaCppCachePrompt;
        int? llamaCppSlotId = NormalizeLlamaCppSlotId(prompt.LlamaCppSlotId ?? inference.LlamaCppSlotId);
        int? llamaCppCacheReuseTokens = NormalizeLlamaCppCacheReuseTokens(
            prompt.LlamaCppCacheReuseTokens ?? inference.LlamaCppCacheReuseTokens);
        bool? parallelToolCalls = prompt.ParallelToolCalls ?? inference.ParallelToolCalls;
        string[]? stopSequences = NormalizeStopSequences(prompt.StopSequences ?? inference.StopSequences);
        JsonElement? tools = prompt.Tools;
        JsonElement? toolChoice = prompt.ToolChoice;
        JsonElement? structuredOutputs = prompt.StructuredOutputs;
        JsonElement? prediction = prompt.Prediction;
        string[]? modalities = NormalizeModalities(prompt.Modalities);
        JsonElement? audio = prompt.Audio;
        MultimodalProcessorOptions? multimodalProcessor = ResolveMultimodalProcessorOptions(
            prompt.MultimodalProcessor,
            prompt.UserContent.HasValue ? inference.MultimodalProcessor : null);
        bool? logprobs = prompt.Logprobs;
        int? topLogprobs = NormalizeTopLogprobs(prompt.TopLogprobs);
        if (topLogprobs.HasValue && logprobs is null)
        {
            logprobs = true;
        }

        if (logprobs != true)
        {
            topLogprobs = null;
        }

        bool? enableThinking = prompt.EnableThinking;
        bool? preserveThinking = prompt.PreserveThinking;
        InferenceChatTemplateKwargs? chatTemplateKwargs = null;
        bool? rootEnableThinking = null;
        bool? rootPreserveThinking = null;

        if (usesTemplateThinkingControls && !usesRootThinkingControls)
        {
            if (enableThinking.HasValue || preserveThinking.HasValue)
            {
                chatTemplateKwargs = new InferenceChatTemplateKwargs
                {
                    EnableThinking = enableThinking,
                    PreserveThinking = preserveThinking,
                };
            }
        }
        else if (ShouldSendThinkingToggle(inference.BaseUrl, enableThinking ?? inference.EnableThinking))
        {
            rootEnableThinking = (enableThinking ?? inference.EnableThinking)!.Value;
        }

        if (usesTemplateThinkingControls && usesRootThinkingControls && preserveThinking.HasValue)
        {
            rootPreserveThinking = preserveThinking.Value;
        }

        int? ttl = residency.SupportsChatCompletionsTtl && residency.TtlSeconds > 0
            ? residency.TtlSeconds
            : null;
        string? cacheSalt = string.IsNullOrWhiteSpace(inference.PrefixCacheSalt)
            ? null
            : inference.PrefixCacheSalt.Trim();

        JsonElement systemContent = JsonSerializer.SerializeToElement(
            prompt.SystemPrompt,
            PalLlmDomainJsonSerializerContext.Default.String);
        JsonElement userContent = BuildUserContent(prompt, inference);

        return new InferenceChatCompletionsRequestBody
        {
            Model = activeModel,
            Temperature = prompt.Temperature,
            TokenBudget = prompt.MaxTokens,
            MaxTokens = useMaxCompletionTokens
                ? null
                : prompt.MaxTokens,
            MaxCompletionTokens = useMaxCompletionTokens
                ? prompt.MaxTokens
                : null,
            Messages =
            [
                new InferenceChatMessage { Role = "system", Content = systemContent },
                new InferenceChatMessage { Role = "user", Content = userContent },
            ],
            ResponseFormat = prompt.ResponseFormat,
            TopP = topP,
            PresencePenalty = presencePenalty,
            FrequencyPenalty = frequencyPenalty,
            TopK = topK,
            MinP = minP,
            RepetitionPenalty = repetitionPenalty,
            ReasoningEffort = reasoningEffort,
            ThinkingTokenBudget = thinkingTokenBudget,
            Seed = seed,
            Priority = requestPriority,
            ServiceTier = serviceTier,
            PromptCacheKey = promptCacheKey,
            PromptCacheRetention = promptCacheRetention,
            Verbosity = verbosity,
            SafetyIdentifier = safetyIdentifier,
            Store = storeCompletions,
            Metadata = requestMetadata,
            LlamaCppCachePrompt = llamaCppCachePrompt,
            LlamaCppSlotId = llamaCppSlotId,
            LlamaCppCacheReuseTokens = llamaCppCacheReuseTokens,
            ParallelToolCalls = parallelToolCalls,
            Stop = stopSequences,
            Tools = tools,
            ToolChoice = toolChoice,
            StructuredOutputs = structuredOutputs,
            Prediction = prediction,
            Modalities = modalities,
            Audio = audio,
            MmProcessorKwargs = multimodalProcessor,
            Logprobs = logprobs == true ? true : null,
            TopLogprobs = topLogprobs,
            ChatTemplateKwargs = chatTemplateKwargs,
            EnableThinking = rootEnableThinking,
            PreserveThinking = rootPreserveThinking,
            Ttl = ttl,
            CacheSalt = cacheSalt,
        };
    }

    private static JsonElement BuildUserContent(InferencePrompt prompt, InferenceOptions inference)
    {
        if (!prompt.UserContent.HasValue)
        {
            return JsonSerializer.SerializeToElement(
                prompt.UserPrompt,
                PalLlmDomainJsonSerializerContext.Default.String);
        }

        return inference.UseMediaCacheIds
            ? MultimodalContentPartMediaCacheIds.AddStableIds(prompt.UserContent.Value)
            : prompt.UserContent.Value.Clone();
    }

    private static bool ShouldSendThinkingToggle(string baseUrl, bool? enableThinking)
    {
        if (!enableThinking.HasValue)
        {
            return false;
        }

        foreach (string marker in ThinkingToggleHostMarkers)
        {
            if (baseUrl.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsesRootThinkingControls(string baseUrl)
    {
        foreach (string marker in ThinkingToggleHostMarkers)
        {
            if (baseUrl.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // Family detection looks for a specific model-family tag in the model
    // string. The tag is a common identifier used only to decide which
    // default sampling knobs to apply when the operator did not supply their
    // own InferenceOptions.TopP / PresencePenalty values.
    private static bool HasFamilySamplingPresets(string model) =>
        !string.IsNullOrWhiteSpace(model) &&
        model.Contains("qwen", StringComparison.OrdinalIgnoreCase);

    private static bool HasTemplateThinkingControls(string model) => HasFamilySamplingPresets(model);

    private static string[]? NormalizeStopSequences(IReadOnlyList<string>? stopSequences)
    {
        if (stopSequences is null || stopSequences.Count == 0)
        {
            return null;
        }

        List<string> normalized = [];
        foreach (string stopSequence in stopSequences)
        {
            if (string.IsNullOrWhiteSpace(stopSequence))
            {
                continue;
            }

            string trimmed = stopSequence.Trim();
            normalized.Add(trimmed);
        }

        return normalized.Count == 0 ? null : normalized.ToArray();
    }

    private static string? NormalizePromptCacheKey(string? promptCacheKey)
    {
        return NormalizeRequestHintIdentifier(promptCacheKey);
    }

    private static int? NormalizeThinkingTokenBudget(int? thinkingTokenBudget) =>
        thinkingTokenBudget is > 0 ? thinkingTokenBudget : null;

    private static int? NormalizeLlamaCppSlotId(int? slotId)
    {
        if (!slotId.HasValue)
        {
            return null;
        }

        return slotId.Value >= -1 ? slotId.Value : null;
    }

    private static int? NormalizeLlamaCppCacheReuseTokens(int? cacheReuseTokens)
    {
        if (!cacheReuseTokens.HasValue)
        {
            return null;
        }

        return cacheReuseTokens.Value >= 0 ? cacheReuseTokens.Value : null;
    }

    private static string? NormalizeRequestHintIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= MaxRequestHintIdentifierLength ? trimmed : null;
    }

    private static Dictionary<string, string>? NormalizeRequestMetadata(
        IReadOnlyDictionary<string, string>? configuredMetadata,
        IReadOnlyDictionary<string, string>? promptMetadata)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        AddRequestMetadata(normalized, configuredMetadata);
        AddRequestMetadata(normalized, promptMetadata);
        return normalized.Count == 0 ? null : normalized;
    }

    private static void AddRequestMetadata(
        Dictionary<string, string> target,
        IReadOnlyDictionary<string, string>? source)
    {
        if (source is null || source.Count == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, string> pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            string key = pair.Key.Trim();
            string value = pair.Value.Trim();
            if (key.Length > InferenceRequestMetadataLimits.MaxKeyLength ||
                value.Length > InferenceRequestMetadataLimits.MaxValueLength)
            {
                continue;
            }

            if (!target.ContainsKey(key) &&
                target.Count >= InferenceRequestMetadataLimits.MaxEntries)
            {
                continue;
            }

            target[key] = value;
        }
    }

    private static void AddClientRequestIdHeader(
        HttpRequestMessage request,
        string? configuredHeader,
        string? clientRequestId)
    {
        string? headerName = InferenceClientRequestIdHeaders.Normalize(configuredHeader);
        string? normalizedRequestId = NormalizeClientRequestId(clientRequestId);
        if (string.IsNullOrEmpty(headerName) || string.IsNullOrEmpty(normalizedRequestId))
        {
            return;
        }

        request.Headers.TryAddWithoutValidation(headerName, normalizedRequestId);
    }

    private static string? NormalizeClientRequestId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        if (trimmed.Length > MaxClientRequestIdLength)
        {
            return null;
        }

        foreach (char ch in trimmed)
        {
            if (ch < 0x21 || ch > 0x7E)
            {
                return null;
            }
        }

        return trimmed;
    }

    private static string[]? NormalizeModalities(IReadOnlyList<string>? modalities)
    {
        if (modalities is null || modalities.Count == 0)
        {
            return null;
        }

        List<string> normalized = [];
        foreach (string modality in modalities)
        {
            if (string.IsNullOrWhiteSpace(modality))
            {
                continue;
            }

            string trimmed = modality.Trim().ToLowerInvariant();
            if (trimmed is not ("text" or "audio") ||
                normalized.Contains(trimmed, StringComparer.Ordinal))
            {
                continue;
            }

            normalized.Add(trimmed);
        }

        return normalized.Count == 0 ? null : normalized.ToArray();
    }

    private static int? NormalizeTopLogprobs(int? topLogprobs) =>
        topLogprobs is >= 0 and <= 20 ? topLogprobs : null;

    private static MultimodalProcessorOptions? ResolveMultimodalProcessorOptions(
        MultimodalProcessorOptions? promptOptions,
        MultimodalProcessorOptions? configuredOptions)
    {
        MultimodalProcessorOptions? candidate = promptOptions?.HasAny == true
            ? promptOptions
            : configuredOptions;
        return candidate?.HasAny == true ? candidate : null;
    }
}

