using Microsoft.Extensions.Logging.Abstractions;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="PromotionLedgerFeeder"/>. Contract pinned here:
///
/// 1. With <c>Enabled = false</c>, the feeder ticks to zero observations
///    regardless of the metrics content — opt-out works.
/// 2. The feeder seeds its baseline on construction via the first tick,
///    so a sidecar that boots with already-populated metrics does NOT
///    retroactively record pre-existing strategy fires (that would
///    flood the ledger and misattribute history).
/// 3. Subsequent fallback-strategy increments produce exactly one ledger
///    observation per delta count up to the
///    <see cref="PromotionFeederOptions.MaxObservationsPerStrategyPerTick"/>
///    cap, so a burst is bounded but still advances the prior count so
///    we don't double-record it next tick.
/// 4. Every emitted observation is recorded against
///    <see cref="PromotionFeederOptions.FallbackTaskClass"/> with
///    outcome <c>success</c>.
/// </summary>
public sealed class PromotionLedgerFeederTests
{
    private sealed class StubRuntime
    {
        public long SuccessCount { get; set; }
        public long FailureCount { get; set; }
        public long RateLimitedCount { get; set; }
        public string ActiveModel { get; set; } = "qwen3.6:35b-a3b";
    }

    private static (PalLlmOptions Options, PalLlmMetrics Metrics, PromotionLedger Ledger, PromotionLedgerFeeder Feeder, StubRuntime Stub) BuildFeeder(
        Action<PromotionFeederOptions>? configure = null)
    {
        var options = new PalLlmOptions();
        configure?.Invoke(options.PromotionFeeder);
        var metrics = new PalLlmMetrics();
        var ledger = new PromotionLedger();
        var stub = new StubRuntime();

        // PalLlmRuntime is injected as the source of RuntimeHealth. Its
        // constructor needs a full IInferenceClient + IVisionClient etc.,
        // so for tests we use a NoOp inference client and mutate a
        // runtime wrapper that exposes the counters we care about. The
        // simplest working path is to construct a real PalLlmRuntime
        // against a disabled inference client and observe that the
        // health counters start at zero. Tests then drive the stub for
        // paths that need non-zero counters by re-seeding the feeder.
        using var httpClient = new HttpClient(new AlwaysFailHandler());
        IInferenceClient client = new HttpJsonInferenceClient(httpClient, options);
        var runtime = new PalLlmRuntime(options, client);

        var feeder = new PromotionLedgerFeeder(options, metrics, runtime, ledger, NullLogger<PromotionLedgerFeeder>.Instance);
        // Tick once to seed the baseline so later ticks only see deltas.
        _ = feeder.Tick();
        return (options, metrics, ledger, feeder, stub);
    }

    private sealed class AlwaysFailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Inference is disabled in the auto-feeder tests.");
    }

    [Test]
    public void Tick_Disabled_DoesNothing()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder(opt =>
        {
            opt.Enabled = false;
        });

        // Even with a burst of fallback fires, a disabled feeder records
        // zero observations.
        metrics.RecordFallbackStrategy("stealth-withdraw");
        metrics.RecordFallbackStrategy("stealth-withdraw");

        int emitted = feeder.Tick();

        Assert.That(emitted, Is.EqualTo(0));
        Assert.That(ledger.Snapshot().Tasks, Is.Empty);
    }

    [Test]
    public void Tick_NoDelta_ReturnsZero()
    {
        (_, _, _, PromotionLedgerFeeder feeder, _) = BuildFeeder();

        // No new fallback fires since the seeding tick.
        int emitted = feeder.Tick();

        Assert.That(emitted, Is.EqualTo(0));
    }

    [Test]
    public void Tick_NewStrategyFires_RecordsOneObservationPerIncrement()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder();

        metrics.RecordFallbackStrategy("stealth-withdraw");
        metrics.RecordFallbackStrategy("stealth-withdraw");
        metrics.RecordFallbackStrategy("general-director");

        int emitted = feeder.Tick();

        Assert.That(emitted, Is.EqualTo(3));
        PromotionSummary summary = ledger.Snapshot();
        Assert.That(summary.Tasks, Has.Count.EqualTo(1),
            "All observations should land on the fallback-director task class.");
        PromotionTaskSummary task = summary.Tasks[0];
        Assert.That(task.TaskClass, Is.EqualTo("fallback-director"));
        Assert.That(task.TotalObservations, Is.EqualTo(3));
        Assert.That(task.SuccessCount, Is.EqualTo(3));
    }

    [Test]
    public void Tick_BurstBeyondCap_AdvancesBaselineAnyway()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder(opt =>
        {
            opt.MaxObservationsPerStrategyPerTick = 5;
        });

        for (int i = 0; i < 12; i++) { metrics.RecordFallbackStrategy("stealth-withdraw"); }

        int firstTick = feeder.Tick();
        int secondTick = feeder.Tick();

        Assert.That(firstTick, Is.EqualTo(5), "First tick caps at MaxObservationsPerStrategyPerTick.");
        Assert.That(secondTick, Is.EqualTo(0),
            "Second tick should NOT replay the 12 fires — the burst must advance the prior count fully.");
        Assert.That(ledger.Snapshot().Tasks[0].TotalObservations, Is.EqualTo(5));
    }

    [Test]
    public void Tick_CustomTaskClass_LabelsObservationsAccordingly()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder(opt =>
        {
            opt.FallbackTaskClass = "custom-ledger-slot";
        });

        metrics.RecordFallbackStrategy("stealth-withdraw");

        _ = feeder.Tick();

        PromotionTaskSummary task = ledger.Snapshot().Tasks.Single();
        Assert.That(task.TaskClass, Is.EqualTo("custom-ledger-slot"));
    }

    [Test]
    public void Tick_AcrossMultipleStrategies_RecordsEach()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder();

        metrics.RecordFallbackStrategy("stealth-withdraw");
        metrics.RecordFallbackStrategy("general-director");
        metrics.RecordFallbackStrategy("stealth-withdraw");

        _ = feeder.Tick();

        PromotionSummary summary = ledger.Snapshot();
        PromotionTaskSummary fallback = summary.Tasks.Single();
        Assert.That(fallback.TotalObservations, Is.EqualTo(3));
        Assert.That(fallback.MostCommonPatternId, Is.EqualTo("stealth-withdraw"),
            "The most-common-pattern hint should point at the strategy with the highest count.");
    }

    // ---- Pass 13: full-surface extension ----------------------------

    [Test]
    public void Tick_TierTransition_RecordsObservationWithFromArrowToPattern()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder();

        metrics.RecordTierTransition("small", "large");
        metrics.RecordTierTransition("small", "large");

        _ = feeder.Tick();

        PromotionTaskSummary task = ledger.Snapshot().Tasks
            .Single(t => t.TaskClass == "model-tier-transition");

        Assert.That(task.TotalObservations, Is.EqualTo(2));
        Assert.That(task.MostCommonPatternId, Is.EqualTo("small->large"),
            "Tier transitions are keyed by a 'from->to' string so dashboards can distinguish graduations from demotions.");
    }

    [Test]
    public void Tick_TierTransitionsDisabled_RecordsNothing()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder(opt =>
        {
            opt.TrackTierTransitions = false;
        });

        metrics.RecordTierTransition("small", "large");

        _ = feeder.Tick();

        Assert.That(ledger.Snapshot().Tasks, Is.Empty,
            "TrackTierTransitions=false must skip tier-transition observations entirely.");
    }

    [Test]
    public void Tick_FreshRuntime_LiveInferenceAndRateLimitStayAtZero()
    {
        // With no inference / rate-limit events the feeder should emit
        // nothing against those surfaces — important because the
        // counters exist on every runtime and we don't want to flood
        // the ledger on startup.
        (_, _, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder();

        _ = feeder.Tick();
        _ = feeder.Tick();

        PromotionSummary summary = ledger.Snapshot();
        Assert.That(summary.Tasks.Any(t => t.TaskClass == "live-inference"), Is.False);
        Assert.That(summary.Tasks.Any(t => t.TaskClass == "rate-limiter"), Is.False);
    }

    [Test]
    public void Tick_AllRuntimeSignalsDisabled_EmitsZeroObservations()
    {
        // With every optional surface disabled only the fallback-strategy
        // path emits — and that only when metrics.RecordFallbackStrategy
        // has fired. Confirms the individual toggles really do opt out
        // without breaking the always-on fallback path.
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder(opt =>
        {
            opt.TrackLiveInference = false;
            opt.TrackRateLimiter = false;
            opt.TrackTierTransitions = false;
        });

        metrics.RecordTierTransition("small", "large");

        _ = feeder.Tick();

        Assert.That(ledger.Snapshot().Tasks, Is.Empty,
            "No fallback fires + every optional signal disabled = zero observations.");
    }

    [Test]
    public void Tick_ConfigurableTaskClassNames_FlowThroughToObservations()
    {
        (_, PalLlmMetrics metrics, PromotionLedger ledger, PromotionLedgerFeeder feeder, _) = BuildFeeder(opt =>
        {
            opt.FallbackTaskClass = "custom-fallback";
            opt.TierTransitionTaskClass = "custom-tier";
        });

        metrics.RecordFallbackStrategy("strategy-a");
        metrics.RecordTierTransition("small", "large");

        _ = feeder.Tick();

        PromotionSummary summary = ledger.Snapshot();
        Assert.That(summary.Tasks.Any(t => t.TaskClass == "custom-fallback"), Is.True);
        Assert.That(summary.Tasks.Any(t => t.TaskClass == "custom-tier"), Is.True);
    }
}
