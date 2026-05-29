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
