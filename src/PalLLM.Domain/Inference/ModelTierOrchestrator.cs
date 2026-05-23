using System.Diagnostics;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

/// <summary>
/// Tracks which configured model tier is currently active and serves the
/// winning model tag to every chat request. Thread-safe, lock-free on the
/// read path so inference calls pay a single volatile read.
///
/// <para>When <see cref="InferenceOptions.ModelTiers"/> is empty, the
/// orchestrator always returns the static <see cref="InferenceOptions.Model"/>
/// and <see cref="RefreshAsync"/> is a no-op — behaviour is fully backwards
/// compatible with pre-tier configs.</para>
///
/// <para>When tiers are configured, the orchestrator eagerly seeds the
/// active model with the first tier in list order so the very first
/// request works before any probe has run. A background worker then calls
/// <see cref="RefreshAsync"/> on a cadence, and whenever the winner
/// changes the orchestrator emits a <c>pal.model_tier.transition</c>
/// event on the <see cref="PalLlmTelemetry.Source"/> ActivitySource so
/// operators can see the graduation in their tracing backend.</para>
/// </summary>
public sealed class ModelTierOrchestrator
{
    private readonly PalLlmOptions _options;
    private readonly IModelAvailabilityProbe _probe;

    // Volatile so the read path in GetActiveModel() doesn't need a lock —
    // the write path is behind _refreshLock so the pair is always consistent.
    private volatile string _activeModel;
    private volatile string? _activeTierId;
    private volatile string[] _lastSeenAvailableModels = Array.Empty<string>();

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly PalLlmMetrics _metrics;

    public ModelTierOrchestrator(PalLlmOptions options, IModelAvailabilityProbe probe)
        : this(options, probe, metrics: null)
    {
    }

    public ModelTierOrchestrator(PalLlmOptions options, IModelAvailabilityProbe probe, PalLlmMetrics? metrics)
    {
        _options = options;
        _probe = probe;
        _metrics = metrics ?? new PalLlmMetrics();

        // Eager seed: pick the highest-priority tier in list order so the
        // first request works before any probe has run. If no tiers are
        // configured, fall back to the static model.
        (string initialModel, string? initialTier) = ResolveInitialSeed(options);
        _activeModel = initialModel;
        _activeTierId = initialTier;
    }

    /// <summary>Current model tag that inference requests should use. Lock-free read.</summary>
    public string GetActiveModel() => _activeModel;

    /// <summary>Current tier id, or null when no tier is active (static-model mode).</summary>
    public string? GetActiveTierId() => _activeTierId;

    /// <summary>Snapshot of the last probe result. Empty when no probe has run yet.</summary>
    public IReadOnlyList<string> GetLastSeenAvailableModels() => _lastSeenAvailableModels;

    /// <summary>
    /// Probes the endpoint and recomputes the active tier. Returns the current
    /// active model after the probe so callers can log transitions without a
    /// second read. Safe to call concurrently — the internal lock serialises
    /// refreshes and the probe's HTTP call is idempotent so a duplicate in
    /// flight just wastes one request.
    /// </summary>
    public async Task<ModelTierRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelTierOptions> tiers = _options.Inference.ModelTiers;
        if (tiers.Count == 0)
        {
            return new ModelTierRefreshResult(
                ActiveModel: _activeModel,
                ActiveTierId: null,
                PreviousTierId: null,
                Changed: false);
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlySet<string> available = await _probe.GetAvailableModelsAsync(cancellationToken)
                .ConfigureAwait(false);

            string[] availableSnapshot = available.ToArray();
            Array.Sort(availableSnapshot, StringComparer.Ordinal);
            _lastSeenAvailableModels = availableSnapshot;

            ModelTierOptions? winner = SelectWinner(tiers, available);
            string newActiveModel;
            string? newActiveTierId;
            if (winner is not null)
            {
                newActiveModel = winner.Model;
                newActiveTierId = winner.Id;
            }
            else
            {
                // No configured tier is currently available. Keep whatever
                // we had before — either the previously graduated tier (so a
                // transient probe failure doesn't knock us back) or the
                // seeded first tier. Probe returning empty is common on
                // first boot before the endpoint is ready; staying put is
                // safer than thrashing.
                newActiveModel = _activeModel;
                newActiveTierId = _activeTierId;
            }

            bool changed = !string.Equals(newActiveTierId, _activeTierId, StringComparison.Ordinal);
            string? previousTierId = _activeTierId;

            _activeModel = newActiveModel;
            _activeTierId = newActiveTierId;

            if (changed)
            {
                EmitTransitionSpan(previousTierId, newActiveTierId, newActiveModel, availableSnapshot);
                _metrics.RecordTierTransition(previousTierId ?? "<none>", newActiveTierId ?? "<none>");
            }

            return new ModelTierRefreshResult(
                ActiveModel: newActiveModel,
                ActiveTierId: newActiveTierId,
                PreviousTierId: previousTierId,
                Changed: changed);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static (string model, string? tierId) ResolveInitialSeed(PalLlmOptions options)
    {
        IReadOnlyList<ModelTierOptions> tiers = options.Inference.ModelTiers;
        if (tiers.Count == 0)
        {
            return (options.Inference.Model, null);
        }

        // Seed with the LOWEST-priority tier in list order — that's typically
        // the "instant" tier (small model) which is most likely to be
        // available first. The probe will graduate us up as larger tiers
        // finish loading. If the config is mis-ordered, the probe will still
        // pick the correct winner on first refresh.
        ModelTierOptions? seed = tiers
            .Select((tier, index) => (tier, index))
            .Where(t => !string.IsNullOrWhiteSpace(t.tier.Model) && !string.IsNullOrWhiteSpace(t.tier.Id))
            .OrderBy(t => t.tier.Priority)
            .ThenBy(t => t.index)
            .Select(t => t.tier)
            .FirstOrDefault();

        return seed is not null
            ? (seed.Model, seed.Id)
            : (options.Inference.Model, null);
    }

    private static ModelTierOptions? SelectWinner(
        IReadOnlyList<ModelTierOptions> tiers,
        IReadOnlySet<string> available)
    {
        if (available.Count == 0)
        {
            return null;
        }

        return tiers
            .Select((tier, index) => (tier, index))
            .Where(t => !string.IsNullOrWhiteSpace(t.tier.Model)
                        && !string.IsNullOrWhiteSpace(t.tier.Id)
                        && available.Contains(t.tier.Model))
            .OrderByDescending(t => t.tier.Priority)
            .ThenBy(t => t.index)
            .Select(t => t.tier)
            .FirstOrDefault();
    }

    private static void EmitTransitionSpan(
        string? previousTierId,
        string? newTierId,
        string newModel,
        IReadOnlyList<string> availableSnapshot)
    {
        using Activity? activity = PalLlmTelemetry.Source.StartActivity(
            "pal.model_tier.transition",
            ActivityKind.Internal);
        activity?.SetTag("pal.model_tier.previous", previousTierId ?? "<none>");
        activity?.SetTag("pal.model_tier.current", newTierId ?? "<none>");
        activity?.SetTag("pal.model_tier.model", newModel);
        activity?.SetTag("pal.model_tier.available_count", availableSnapshot.Count);
    }
}

/// <summary>Outcome of a <see cref="ModelTierOrchestrator.RefreshAsync"/> call.</summary>
/// <param name="ActiveModel">The model tag inference requests will use after this refresh.</param>
/// <param name="ActiveTierId">The tier id backing the active model, or null when no tier is active.</param>
/// <param name="PreviousTierId">The tier id before this refresh.</param>
/// <param name="Changed">True when this refresh changed the active tier.</param>
public sealed record ModelTierRefreshResult(
    string ActiveModel,
    string? ActiveTierId,
    string? PreviousTierId,
    bool Changed);
