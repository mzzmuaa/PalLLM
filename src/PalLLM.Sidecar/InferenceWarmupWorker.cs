using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Runs a bounded inference warmup on startup and optionally keeps the active
/// model resident on a configurable cadence. The request is deliberately tiny:
/// enough to trigger model load and cache priming without doing meaningful work.
/// </summary>
public sealed class InferenceWarmupWorker : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly PalLlmRuntime _runtime;
    private readonly ILogger<InferenceWarmupWorker> _logger;

    public InferenceWarmupWorker(
        PalLlmOptions options,
        PalLlmRuntime runtime,
        ILogger<InferenceWarmupWorker> logger)
    {
        _options = options;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Inference.Enabled || !_options.Inference.EnableWarmup)
        {
            return;
        }

        await TryWarmAsync("startup", stoppingToken).ConfigureAwait(false);

        if (_options.Inference.WarmupIntervalSeconds <= 0)
        {
            return;
        }

        int seconds = Math.Max(5, _options.Inference.WarmupIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await TryWarmAsync("periodic_keepalive", stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryWarmAsync(string reason, CancellationToken stoppingToken)
    {
        try
        {
            await _runtime.WarmInferenceAsync(reason, force: false, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Inference warmup pass '{Reason}' failed.", reason);
        }
    }
}
