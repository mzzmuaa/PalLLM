using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Writes <c>session.json</c> on a configurable interval so a crash never costs
/// more than one autosave window of conversation history. Skips quietly when
/// session persistence is disabled. The save path is serialized against a lock
/// inside <see cref="SessionPersistence"/>, so concurrent manual + worker saves
/// are safe.
/// </summary>
public sealed class SessionAutosaveWorker : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly PalLlmRuntime _runtime;
    private readonly ILogger<SessionAutosaveWorker> _logger;

    public SessionAutosaveWorker(PalLlmOptions options, PalLlmRuntime runtime, ILogger<SessionAutosaveWorker> logger)
    {
        _options = options;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            int seconds = Math.Max(5, _options.Session.AutosaveIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_options.Session.Enabled || !_options.Session.EnableAutosave)
            {
                continue;
            }

            try
            {
                // Dirty-aware save: skip disk I/O when nothing has mutated since
                // the previous flush. Turns the autosave loop into "no mutation
                // → no work" so a quiet session costs nothing.
                SessionPersistenceResult result = _runtime.SaveSessionIfDirty();
                if (!result.Success)
                {
                    _logger.LogWarning("Session autosave failed: {Status}", result.StatusMessage);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Session autosave threw unexpectedly.");
            }
        }

        // Final save on shutdown — only if there's unsaved state. Best-effort.
        if (_options.Session.Enabled && _options.Session.EnableAutosave && _runtime.SessionIsDirty)
        {
            try
            {
                _runtime.SaveSession();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Final session save on shutdown failed.");
            }
        }
    }
}
