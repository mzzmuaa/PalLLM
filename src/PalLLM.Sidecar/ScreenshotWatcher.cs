using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Background service that polls <c>Bridge/Screenshots</c> on an interval and feeds
/// each new image through the vision world-state extractor. Complementary to the
/// UE4SS event stream: whenever a hook is missed or unavailable, a screenshot is
/// a second, independent sensor that keeps the snapshot fresh. Off by default
/// because vision costs a model call; flip on via
/// <see cref="VisionOptions.EnableScreenshotWatcher"/>.
/// </summary>
public sealed class ScreenshotWatcher : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly PalLlmRuntime _runtime;
    private readonly ILogger<ScreenshotWatcher> _logger;

    public ScreenshotWatcher(PalLlmOptions options, PalLlmRuntime runtime, ILogger<ScreenshotWatcher> logger)
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
                int removed = _runtime.PrunePendingScreenshots();
                if (removed > 0)
                {
                    _logger.LogInformation(
                        "PalLLM screenshot watcher pruned {RemovedCount} stale pending screenshot(s).",
                        removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PalLLM screenshot watcher prune pass failed.");
            }

            if (!_options.Vision.EnableScreenshotWatcher || !_options.Vision.Enabled)
            {
                // Nothing to do right now; re-check after the standard delay so toggling
                // the option at runtime still takes effect on the next cycle.
                try
                {
                    await Task.Delay(_options.Vision.ScreenshotPollIntervalMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                ScreenshotIngestResult result = await _runtime.ProcessScreenshotsAsync(
                    stoppingToken,
                    _options.Vision.MaxScreenshotsPerPoll);
                if (result.ProcessedCount > 0 || result.FailedCount > 0)
                {
                    _logger.LogInformation(
                        "PalLLM screenshot watcher processed {ProcessedCount} screenshot(s) with {FailedCount} failure(s).",
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
                _logger.LogError(ex, "PalLLM screenshot watcher tick failed.");
            }

            try
            {
                await Task.Delay(_options.Vision.ScreenshotPollIntervalMs, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
