using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Periodically probes the inference endpoint via
/// <see cref="ModelTierOrchestrator.RefreshAsync"/>. Lets the sidecar
/// graduate from the small "instant" tier to the large "quality" tier as
/// soon as the large model finishes downloading / warming — no manual
/// config edit or restart. Skips entirely when no model tiers are
/// configured or inference is disabled so the default localhost-only
/// deployment pays nothing.
/// </summary>
public sealed class ModelTierUpgradeWorker : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly ModelTierOrchestrator _orchestrator;
    private readonly PalLlmRuntime _runtime;
    private readonly ILogger<ModelTierUpgradeWorker> _logger;

    public ModelTierUpgradeWorker(
        PalLlmOptions options,
        ModelTierOrchestrator orchestrator,
        PalLlmRuntime runtime,
        ILogger<ModelTierUpgradeWorker> logger)
    {
        _options = options;
        _orchestrator = orchestrator;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Inference.Enabled)
        {
            // Inference is opted out — the chat lane runs against the deterministic
            // fallback director and there is no upstream endpoint to probe. Without
            // this guard the worker would fire HTTP probes against the configured
            // BaseUrl on every TierProbeIntervalSeconds tick even though the
            // operator explicitly disabled the live inference lane. That breaks
            // both the air-gap promise (probes leave the machine) and the "default
            // is silent" promise (every probe shows up in netstat).
            return;
        }

        if (_options.Inference.ModelTiers.Count == 0)
        {
            // No tiers configured → nothing to probe. This is the default for
            // backwards-compat configs that only set Inference.Model.
            return;
        }

        // Kick off an immediate probe on startup so the initial tier pick is
        // evidence-based instead of stuck on the seed default.
        await TryProbeOnceAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            int seconds = Math.Max(5, _options.Inference.TierProbeIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TryProbeOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryProbeOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            ModelTierRefreshResult result = await _orchestrator.RefreshAsync(stoppingToken).ConfigureAwait(false);
            if (result.Changed)
            {
                _logger.LogInformation(
                    "Model tier graduated {PreviousTier} -> {CurrentTier} ({Model}).",
                    result.PreviousTierId ?? "<none>",
                    result.ActiveTierId ?? "<none>",
                    result.ActiveModel);

                if (_options.Inference.EnableWarmup)
                {
                    await _runtime.WarmInferenceAsync("tier_change", force: false, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown — swallow.
        }
        catch (Exception ex)
        {
            // Probe failures are non-fatal. Log once per tick at Debug so a
            // long-running sidecar against a briefly-unreachable endpoint
            // doesn't spam Warning-level logs.
            _logger.LogDebug(ex, "Model tier probe attempt failed; keeping current active tier.");
        }
    }
}
