using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

public sealed class BridgeInboxWorker : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly PalLlmRuntime _runtime;
    private readonly ILogger<BridgeInboxWorker> _logger;

    public BridgeInboxWorker(PalLlmOptions options, PalLlmRuntime runtime, ILogger<BridgeInboxWorker> logger)
    {
        _options = options;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                BridgeDrainResult result = _runtime.DrainInbox(_options.Bridge.MaxEventsPerPoll);
                if (result.ProcessedCount > 0 || result.FailedCount > 0)
                {
                    _logger.LogInformation(
                        "PalLLM bridge processed {ProcessedCount} event(s) with {FailedCount} failure(s).",
                        result.ProcessedCount,
                        result.FailedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PalLLM bridge inbox worker failed.");
            }

            try
            {
                await Task.Delay(_options.Bridge.PollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
