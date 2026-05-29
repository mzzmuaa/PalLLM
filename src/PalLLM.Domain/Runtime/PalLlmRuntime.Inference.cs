using System.Diagnostics;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    public InferencePerformanceSnapshot GetInferencePerformanceSnapshot() =>
        _inferencePerformance.GetSnapshot();

    private string GetInferenceCircuitState() =>
        _inferenceClient is HttpJsonInferenceClient compat
            ? compat.CircuitBreaker.State.ToString()
            : "NotInstrumented";

    private int GetInferenceCircuitFailures() =>
        _inferenceClient is HttpJsonInferenceClient compat
            ? compat.CircuitBreaker.ConsecutiveFailures
            : 0;

    private string GetInferenceActiveModel() =>
        _inferenceClient is IInferenceLaneMetadata metadata
            ? metadata.GetActiveModelId()
            : _options.Inference.Model;

    private string? GetInferenceActiveTierId() =>
        _inferenceClient is IInferenceLaneMetadata metadata
            ? metadata.GetActiveTierId()
            : null;

    private IReadOnlyList<string> GetInferenceLastSeenAvailableModels() =>
        _inferenceClient is IInferenceLaneMetadata metadata
            ? metadata.GetLastSeenAvailableModels()
            : Array.Empty<string>();

    public InferenceWarmupSnapshot GetInferenceWarmupSnapshot()
    {
        lock (_inferenceWarmupGate)
        {
            return BuildInferenceWarmupSnapshot(_inferenceWarmup);
        }
    }

    private void RecordLiveInferenceSuccess(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        lock (_inferenceWarmupGate)
        {
            _inferenceWarmup = BuildInferenceWarmupSnapshot(
                _inferenceWarmup,
                lastLiveInferenceAtUtc: DateTimeOffset.UtcNow,
                lastLiveInferenceModel: modelId.Trim());
        }
    }

    private TimeSpan GetKeepaliveFreshnessWindow() =>
        _options.Inference.WarmupIntervalSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(5, _options.Inference.WarmupIntervalSeconds))
            : TimeSpan.Zero;

    public async Task<InferenceWarmupSnapshot> WarmInferenceAsync(
        string reason,
        bool force,
        CancellationToken cancellationToken)
    {
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "manual_api" : reason.Trim();

        if (!_options.Inference.EnableWarmup)
        {
            lock (_inferenceWarmupGate)
            {
                _inferenceWarmup = BuildInferenceWarmupSnapshot(
                    _inferenceWarmup,
                    status: "disabled",
                    lastReason: normalizedReason,
                    statusMessage: "Inference warmup is disabled by configuration.");
                return _inferenceWarmup;
            }
        }

        if (!_options.Inference.Enabled)
        {
            lock (_inferenceWarmupGate)
            {
                _inferenceWarmup = BuildInferenceWarmupSnapshot(
                    _inferenceWarmup,
                    status: "disabled",
                    lastReason: normalizedReason,
                    statusMessage: "Inference is disabled, so no warmup will run.");
                return _inferenceWarmup;
            }
        }

        await _inferenceWarmupSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_inferenceWarmupGate)
            {
                InferenceWarmupSnapshot current = BuildInferenceWarmupSnapshot(_inferenceWarmup);
                bool recentSuccess =
                    !force
                    && string.Equals(current.ActiveModel, current.LastWarmedModel, StringComparison.Ordinal)
                    && current.LastSuccessAtUtc.HasValue
                    && now - current.LastSuccessAtUtc.Value < InferenceWarmupMinimumGap;
                if (recentSuccess)
                {
                    _inferenceWarmup = BuildInferenceWarmupSnapshot(
                        current,
                        status: "ready",
                        lastReason: normalizedReason,
                        statusMessage:
                            $"Recent warmup for '{current.ActiveModel}' is still fresh; skipping duplicate request.");
                    return _inferenceWarmup;
                }

                TimeSpan keepaliveFreshnessWindow = GetKeepaliveFreshnessWindow();
                bool recentLiveInference =
                    !force
                    && keepaliveFreshnessWindow > TimeSpan.Zero
                    && string.Equals(normalizedReason, "periodic_keepalive", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(current.ActiveModel)
                    && string.Equals(current.ActiveModel, current.LastLiveInferenceModel, StringComparison.Ordinal)
                    && current.LastLiveInferenceAtUtc.HasValue
                    && now - current.LastLiveInferenceAtUtc.Value < keepaliveFreshnessWindow;
                if (recentLiveInference)
                {
                    TimeSpan age = now - current.LastLiveInferenceAtUtc!.Value;
                    _inferenceWarmup = BuildInferenceWarmupSnapshot(
                        current,
                        status: "ready",
                        lastReason: normalizedReason,
                        statusMessage:
                            $"Recent live inference for '{current.ActiveModel}' {Math.Max(0, age.TotalSeconds):0}s ago already kept the lane warm; skipping keepalive.");
                    return _inferenceWarmup;
                }

                _inferenceWarmup = BuildInferenceWarmupSnapshot(
                    current,
                    status: "warming",
                    lastReason: normalizedReason,
                    lastAttemptAtUtc: now,
                    attemptCount: current.AttemptCount + 1,
                    statusMessage: $"Warming active model '{current.ActiveModel}' for reason '{normalizedReason}'.");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            InferenceResult result;
            string warmupTransport = "chat_completions";
            bool usedResidencyHint = false;
            try
            {
                var warmupPrompt = new InferencePrompt
                {
                    SystemPrompt = "You are handling a model warmup request. Reply with OK only.",
                    UserPrompt = "OK",
                    Temperature = 0f,
                    MaxTokens = Math.Clamp(_options.Inference.WarmupMaxTokens, 1, 32),
                    TopP = 0.1f,
                    PresencePenalty = 0f,
                    EnableThinking = false,
                    PreserveThinking = false,
                };

                if (_inferenceClient is HttpJsonInferenceClient compat)
                {
                    InferenceWarmupTransportResult transportResult = await compat.WarmAsync(warmupPrompt, cancellationToken)
                        .ConfigureAwait(false);
                    result = transportResult.Result;
                    warmupTransport = transportResult.Transport;
                    usedResidencyHint = transportResult.ResidencyHintApplied;
                }
                else
                {
                    result = await _inferenceClient.CompleteAsync(warmupPrompt, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_inferenceWarmupGate)
                {
                    InferenceWarmupSnapshot current = BuildInferenceWarmupSnapshot(_inferenceWarmup);
                    _inferenceWarmup = BuildInferenceWarmupSnapshot(
                        current,
                        status: "idle",
                        lastReason: normalizedReason,
                        statusMessage: "Inference warmup was cancelled before completion.");
                    return _inferenceWarmup;
                }
            }

            stopwatch.Stop();
            lock (_inferenceWarmupGate)
            {
                InferenceWarmupSnapshot current = BuildInferenceWarmupSnapshot(_inferenceWarmup);
                _inferenceWarmup = result.Success
                    ? BuildInferenceWarmupSnapshot(
                        current,
                        status: "ready",
                        lastReason: normalizedReason,
                        lastWarmedModel: current.ActiveModel,
                        warmupTransport: warmupTransport,
                        lastWarmupUsedResidencyHint: usedResidencyHint,
                        lastSuccessAtUtc: DateTimeOffset.UtcNow,
                        successCount: current.SuccessCount + 1,
                        lastLatencyMs: Math.Max(0, stopwatch.ElapsedMilliseconds),
                        statusMessage: BuildWarmupStatusMessage(
                            current.ActiveModel,
                            warmupTransport,
                            usedResidencyHint,
                            success: true))
                    : BuildInferenceWarmupSnapshot(
                        current,
                        status: "failed",
                        lastReason: normalizedReason,
                        lastWarmedModel: current.ActiveModel,
                        warmupTransport: warmupTransport,
                        lastWarmupUsedResidencyHint: usedResidencyHint,
                        lastFailureAtUtc: DateTimeOffset.UtcNow,
                        failureCount: current.FailureCount + 1,
                        lastLatencyMs: Math.Max(0, stopwatch.ElapsedMilliseconds),
                        statusMessage: BuildWarmupStatusMessage(
                            current.ActiveModel,
                            warmupTransport,
                            usedResidencyHint,
                            success: false,
                            upstreamStatus: result.StatusMessage));
                return _inferenceWarmup;
            }
        }
        finally
        {
            _inferenceWarmupSemaphore.Release();
        }
    }

    private void RecordInferenceOperation(
        InferenceResult? result,
        string fallbackRequestModel)
    {
        if (result is null || !result.IsConfigured)
        {
            return;
        }

        string requestModel = string.IsNullOrWhiteSpace(result.RequestModel)
            ? fallbackRequestModel
            : result.RequestModel;
        string providerName = string.IsNullOrWhiteSpace(result.ProviderName)
            ? "openai_compatible"
            : result.ProviderName;

        _inferencePerformance.Record(new InferencePerformanceSample(
            GenAiTelemetry.OperationChat,
            providerName,
            requestModel,
            string.IsNullOrWhiteSpace(result.ResponseModel) ? null : result.ResponseModel,
            result.Success,
            string.IsNullOrWhiteSpace(result.ErrorType) ? null : result.ErrorType,
            result.LatencyMs,
            result.Usage.PromptTokens,
            result.Usage.CompletionTokens,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(result.SystemFingerprint) ? null : result.SystemFingerprint,
            string.IsNullOrWhiteSpace(result.ResponseId) ? null : result.ResponseId,
            result.FinishReasons,
            string.IsNullOrWhiteSpace(result.UpstreamRequestId) ? null : result.UpstreamRequestId,
            result.UpstreamProcessingMs,
            result.UpstreamQueueMs,
            result.UpstreamTimeToFirstTokenMs,
            result.UpstreamPrefillMs,
            result.UpstreamDecodeMs,
            result.Usage.CachedPromptTokens,
            result.Usage.PromptAudioTokens,
            result.Usage.CompletionReasoningTokens,
            result.Usage.CompletionAudioTokens,
            result.Usage.AcceptedPredictionTokens,
            result.Usage.RejectedPredictionTokens));
    }

    private InferenceWarmupSnapshot BuildInferenceWarmupSnapshot(
        InferenceWarmupSnapshot? current,
        string? status = null,
        string? lastReason = null,
        string? lastWarmedModel = null,
        string? warmupTransport = null,
        bool? lastWarmupUsedResidencyHint = null,
        string? statusMessage = null,
        DateTimeOffset? lastAttemptAtUtc = null,
        DateTimeOffset? lastSuccessAtUtc = null,
        DateTimeOffset? lastLiveInferenceAtUtc = null,
        string? lastLiveInferenceModel = null,
        DateTimeOffset? lastFailureAtUtc = null,
        long? attemptCount = null,
        long? successCount = null,
        long? failureCount = null,
        long? lastLatencyMs = null)
    {
        InferenceWarmupSnapshot baseline = current ?? new InferenceWarmupSnapshot();
        ResolvedInferenceResidency residency = InferenceResidencyPolicy.Resolve(_options.Inference);
        return new InferenceWarmupSnapshot
        {
            Enabled = _options.Inference.EnableWarmup,
            Status = status ?? baseline.Status,
            ActiveModel = GetInferenceActiveModel(),
            ActiveTierId = GetInferenceActiveTierId(),
            ResidencyProvider = residency.ProviderId,
            ResidencyTtlSeconds = residency.TtlSeconds,
            LastSeenAvailableModels = GetInferenceLastSeenAvailableModels().ToArray(),
            LastWarmedModel = lastWarmedModel ?? baseline.LastWarmedModel,
            LastReason = lastReason ?? baseline.LastReason,
            WarmupTransport = warmupTransport ?? baseline.WarmupTransport,
            LastWarmupUsedResidencyHint = lastWarmupUsedResidencyHint ?? baseline.LastWarmupUsedResidencyHint,
            StatusMessage = statusMessage ?? baseline.StatusMessage,
            LastAttemptAtUtc = lastAttemptAtUtc ?? baseline.LastAttemptAtUtc,
            LastSuccessAtUtc = lastSuccessAtUtc ?? baseline.LastSuccessAtUtc,
            LastLiveInferenceAtUtc = lastLiveInferenceAtUtc ?? baseline.LastLiveInferenceAtUtc,
            LastLiveInferenceModel = lastLiveInferenceModel ?? baseline.LastLiveInferenceModel,
            LastFailureAtUtc = lastFailureAtUtc ?? baseline.LastFailureAtUtc,
            AttemptCount = attemptCount ?? baseline.AttemptCount,
            SuccessCount = successCount ?? baseline.SuccessCount,
            FailureCount = failureCount ?? baseline.FailureCount,
            LastLatencyMs = lastLatencyMs ?? baseline.LastLatencyMs,
        };
    }

    private string BuildWarmupStatusMessage(
        string activeModel,
        string warmupTransport,
        bool usedResidencyHint,
        bool success,
        string? upstreamStatus = null)
    {
        string transport = string.IsNullOrWhiteSpace(warmupTransport) ? "chat_completions" : warmupTransport;
        string residencyHint = InferenceResidencyPolicy.DescribeHint(InferenceResidencyPolicy.Resolve(_options.Inference));

        if (success)
        {
            return usedResidencyHint && !string.IsNullOrWhiteSpace(residencyHint)
                ? $"Warmup completed for '{activeModel}' via {transport} using {residencyHint}."
                : $"Warmup completed for '{activeModel}' via {transport}.";
        }

        string detail = string.IsNullOrWhiteSpace(upstreamStatus)
            ? "Inference warmup failed."
            : upstreamStatus.Trim();

        return usedResidencyHint && !string.IsNullOrWhiteSpace(residencyHint)
            ? $"Warmup failed for '{activeModel}' via {transport} using {residencyHint}: {detail}"
            : $"Warmup failed for '{activeModel}' via {transport}: {detail}";
    }
}
