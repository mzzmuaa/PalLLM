using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Background worker that converts <see cref="PalLlmMetrics"/> snapshot
/// deltas into automatic <see cref="PromotionLedger"/> observations.
/// Closes the Pass 10 loop: the ledger was introduced as a passive
/// manually-fed surface; this worker makes it self-populating so the
/// dashboard's Promotion panel reflects actual live behaviour without
/// the operator running a single POST.
///
/// <para>Observer-only. Reads the metrics snapshot, diffs it against
/// the prior tick, and writes one ledger observation per increment.
/// Does not mutate the metrics, does not touch ChatAsync, does not
/// affect the deterministic always-available layer.</para>
///
/// <para>Opt-in but default ON — behaviour is purely additive, bounded
/// (<see cref="PromotionFeederOptions.MaxObservationsPerStrategyPerTick"/>),
/// and cheap (one snapshot read + a handful of lock-free increments per
/// tick).</para>
///
/// <para>Every fallback-director fire is recorded as an
/// <c>OutcomeSuccess</c> observation. From the system's perspective the
/// director never throws, never produces unsafe output, and never fails
/// to respond — every fire IS a successful bypass that kept the
/// player-visible chat turn alive. The ledger then reports which
/// strategies have been stable long enough to promote from "deterministic
/// fallback policy" into "hard-coded deterministic behaviour" (the 2035
/// doc's explicit prediction).</para>
/// </summary>
public sealed class PromotionLedgerFeeder : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly PalLlmMetrics _metrics;
    private readonly PalLlmRuntime _runtime;
    private readonly PromotionLedger _ledger;
    private readonly ILogger<PromotionLedgerFeeder> _logger;

    // Per-surface prior-tick counts so we can compute deltas without
    // re-recording already-seen increments.
    private readonly Dictionary<string, long> _priorStrategyCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _priorTierTransitionCounts = new(StringComparer.Ordinal);
    private long _priorInferenceSuccessCount;
    private long _priorInferenceFailureCount;
    private long _priorRateLimitedCount;

    public PromotionLedgerFeeder(
        PalLlmOptions options,
        PalLlmMetrics metrics,
        PalLlmRuntime runtime,
        PromotionLedger ledger,
        ILogger<PromotionLedgerFeeder> logger)
    {
        _options = options;
        _metrics = metrics;
        _runtime = runtime;
        _ledger = ledger;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        PromotionFeederOptions opt = _options.PromotionFeeder;
        if (!opt.Enabled)
        {
            return;
        }

        int seconds = Math.Max(5, opt.CheckIntervalSeconds);
        TimeSpan interval = TimeSpan.FromSeconds(seconds);

        // Seed the prior counts on startup so we never treat the very
        // first read as an avalanche of fresh observations.
        SeedBaseline();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                Tick();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Promotion-ledger feeder tick failed; deferring to next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Single tick. Exposed so tests can drive the feeder deterministically
    /// without spinning up a full hosted-service loop. Returns the number
    /// of observations it emitted this tick so tests can assert on it.
    /// </summary>
    public int Tick()
    {
        PromotionFeederOptions opt = _options.PromotionFeeder;
        if (!opt.Enabled) { return 0; }

        int cap = Math.Max(1, opt.MaxObservationsPerStrategyPerTick);
        PalLlmMetricsSnapshot snapshot = _metrics.Snapshot();
        RuntimeHealth health = _runtime.GetHealth();
        int emitted = 0;

        // ---- Fallback-director strategy deltas -----------------------
        string fallbackTaskClass = Resolve(opt.FallbackTaskClass, "fallback-director");
        foreach (FallbackStrategyCount entry in snapshot.FallbackStrategyCounts)
        {
            long prior = _priorStrategyCounts.TryGetValue(entry.StrategyId, out long p) ? p : 0L;
            long delta = entry.Count - prior;
            _priorStrategyCounts[entry.StrategyId] = entry.Count;
            if (delta <= 0) { continue; }

            long recordCount = Math.Min(delta, cap);
            for (long i = 0; i < recordCount; i++)
            {
                _ledger.Record(
                    taskClass: fallbackTaskClass,
                    patternId: entry.StrategyId,
                    outcome: PromotionLedger.OutcomeSuccess,
                    note: "auto-feeder");
                emitted++;
            }
        }

        // ---- Live-inference success / failure ------------------------
        if (opt.TrackLiveInference)
        {
            string liveTaskClass = Resolve(opt.LiveInferenceTaskClass, "live-inference");
            string activeModel = string.IsNullOrWhiteSpace(health.InferenceActiveModel)
                ? (string.IsNullOrWhiteSpace(health.InferenceModel) ? "inference" : health.InferenceModel)
                : health.InferenceActiveModel;

            long successDelta = health.InferenceSuccessCount - _priorInferenceSuccessCount;
            _priorInferenceSuccessCount = health.InferenceSuccessCount;
            if (successDelta > 0)
            {
                long recordCount = Math.Min(successDelta, cap);
                for (long i = 0; i < recordCount; i++)
                {
                    _ledger.Record(
                        taskClass: liveTaskClass,
                        patternId: activeModel,
                        outcome: PromotionLedger.OutcomeSuccess,
                        note: "auto-feeder:inference-success");
                    emitted++;
                }
            }

            long failureDelta = health.InferenceFailureCount - _priorInferenceFailureCount;
            _priorInferenceFailureCount = health.InferenceFailureCount;
            if (failureDelta > 0)
            {
                long recordCount = Math.Min(failureDelta, cap);
                for (long i = 0; i < recordCount; i++)
                {
                    _ledger.Record(
                        taskClass: liveTaskClass,
                        patternId: activeModel,
                        outcome: PromotionLedger.OutcomeValidatorFail,
                        note: "auto-feeder:inference-failure");
                    emitted++;
                }
            }
        }

        // ---- Rate-limiter engagement (limiter itself is working) -----
        if (opt.TrackRateLimiter)
        {
            string rateTaskClass = Resolve(opt.RateLimiterTaskClass, "rate-limiter");
            long rateDelta = health.RateLimitedCount - _priorRateLimitedCount;
            _priorRateLimitedCount = health.RateLimitedCount;
            if (rateDelta > 0)
            {
                long recordCount = Math.Min(rateDelta, cap);
                for (long i = 0; i < recordCount; i++)
                {
                    _ledger.Record(
                        taskClass: rateTaskClass,
                        patternId: "sliding-window",
                        outcome: PromotionLedger.OutcomeSuccess,
                        note: "auto-feeder:rate-limit-engaged");
                    emitted++;
                }
            }
        }

        // ---- Model tier transitions (small → large, large → small) ---
        if (opt.TrackTierTransitions)
        {
            string tierTaskClass = Resolve(opt.TierTransitionTaskClass, "model-tier-transition");
            foreach (ModelTierTransitionCount entry in snapshot.ModelTierTransitionCounts)
            {
                string key = $"{entry.From ?? "<none>"}->{entry.To ?? "<none>"}";
                long prior = _priorTierTransitionCounts.TryGetValue(key, out long p) ? p : 0L;
                long delta = entry.Count - prior;
                _priorTierTransitionCounts[key] = entry.Count;
                if (delta <= 0) { continue; }

                long recordCount = Math.Min(delta, cap);
                for (long i = 0; i < recordCount; i++)
                {
                    _ledger.Record(
                        taskClass: tierTaskClass,
                        patternId: key,
                        outcome: PromotionLedger.OutcomeSuccess,
                        note: "auto-feeder:tier-transition");
                    emitted++;
                }
            }
        }

        return emitted;
    }

    private static string Resolve(string? candidate, string fallback) =>
        string.IsNullOrWhiteSpace(candidate) ? fallback : candidate.Trim();

    private void SeedBaseline()
    {
        PalLlmMetricsSnapshot snapshot = _metrics.Snapshot();
        foreach (FallbackStrategyCount entry in snapshot.FallbackStrategyCounts)
        {
            _priorStrategyCounts[entry.StrategyId] = entry.Count;
        }
        foreach (ModelTierTransitionCount entry in snapshot.ModelTierTransitionCounts)
        {
            string key = $"{entry.From ?? "<none>"}->{entry.To ?? "<none>"}";
            _priorTierTransitionCounts[key] = entry.Count;
        }

        RuntimeHealth health = _runtime.GetHealth();
        _priorInferenceSuccessCount = health.InferenceSuccessCount;
        _priorInferenceFailureCount = health.InferenceFailureCount;
        _priorRateLimitedCount = health.RateLimitedCount;
    }
}
