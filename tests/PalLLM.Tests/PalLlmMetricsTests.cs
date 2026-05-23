using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

public sealed class PalLlmMetricsTests
{
    [Test]
    public void Snapshot_EmptyState_ReturnsEmptyCollectionsAndZeroHistogram()
    {
        var metrics = new PalLlmMetrics();

        PalLlmMetricsSnapshot snapshot = metrics.Snapshot();

        Assert.That(snapshot.FallbackStrategyCounts, Is.Empty);
        Assert.That(snapshot.ModelTierTransitionCounts, Is.Empty);
        Assert.That(snapshot.ChatLatency.Count, Is.Zero);
        Assert.That(snapshot.ChatLatency.SumSeconds, Is.EqualTo(0.0));
        Assert.That(snapshot.ChatLatency.Buckets,
            Has.Count.EqualTo(PalLlmMetrics.ChatLatencyBucketUpperBounds.Length));
        Assert.That(snapshot.ChatLatency.Buckets.Select(b => b.CumulativeCount),
            Is.All.Zero, "Every bucket must start at zero.");
    }

    [Test]
    public void RecordFallbackStrategy_CountsAccumulatePerStrategy()
    {
        var metrics = new PalLlmMetrics();

        metrics.RecordFallbackStrategy("stealth-withdraw");
        metrics.RecordFallbackStrategy("stealth-withdraw");
        metrics.RecordFallbackStrategy("crafting-discipline");

        PalLlmMetricsSnapshot snapshot = metrics.Snapshot();

        FallbackStrategyCount stealth = snapshot.FallbackStrategyCounts
            .First(e => e.StrategyId == "stealth-withdraw");
        FallbackStrategyCount crafting = snapshot.FallbackStrategyCounts
            .First(e => e.StrategyId == "crafting-discipline");

        Assert.That(stealth.Count, Is.EqualTo(2));
        Assert.That(crafting.Count, Is.EqualTo(1));
    }

    [Test]
    public void RecordFallbackStrategy_EmptyOrWhitespaceId_IsIgnored()
    {
        // Defensive: upstream callers passing "" shouldn't create a
        // phantom empty-string series — Prometheus allows it but it's
        // noise. Silent-ignore keeps the snapshot clean.
        var metrics = new PalLlmMetrics();
        metrics.RecordFallbackStrategy("");
        metrics.RecordFallbackStrategy("   ");
        metrics.RecordFallbackStrategy(null!);

        Assert.That(metrics.Snapshot().FallbackStrategyCounts, Is.Empty);
    }

    [Test]
    public void RecordTierTransition_UsesNoneSentinelForEmptyFromTierId()
    {
        // The very first transition at startup has no previous tier —
        // the orchestrator passes "<none>" as the from id. Verify that
        // survives the recorder's null/blank-defensive path as well.
        var metrics = new PalLlmMetrics();

        metrics.RecordTierTransition(string.Empty, "small");
        metrics.RecordTierTransition("small", "large");

        PalLlmMetricsSnapshot snapshot = metrics.Snapshot();
        Assert.That(snapshot.ModelTierTransitionCounts, Has.Count.EqualTo(2));
        Assert.That(snapshot.ModelTierTransitionCounts,
            Has.Some.Matches<ModelTierTransitionCount>(e => e.From == "<none>" && e.To == "small"));
        Assert.That(snapshot.ModelTierTransitionCounts,
            Has.Some.Matches<ModelTierTransitionCount>(e => e.From == "small" && e.To == "large"));
    }

    [Test]
    public void RecordChatLatency_IncrementsCumulativeBucketsCorrectly()
    {
        var metrics = new PalLlmMetrics();

        // 3ms (fallback-only territory), 80ms (sub-100ms inference),
        // 450ms (mid-range), 3.5s (large model).
        metrics.RecordChatLatency(TimeSpan.FromMilliseconds(3));
        metrics.RecordChatLatency(TimeSpan.FromMilliseconds(80));
        metrics.RecordChatLatency(TimeSpan.FromMilliseconds(450));
        metrics.RecordChatLatency(TimeSpan.FromMilliseconds(3500));

        PalLlmMetricsSnapshot snapshot = metrics.Snapshot();
        ChatLatencyHistogram histogram = snapshot.ChatLatency;

        Assert.That(histogram.Count, Is.EqualTo(4));
        Assert.That(histogram.SumSeconds,
            Is.EqualTo(3.5 + 0.45 + 0.08 + 0.003).Within(0.01),
            "Sum must approximate total observed seconds.");

        // Cumulative-bucket semantics: bucket at le=0.005 counts only
        // observations ≤ 5ms (just the 3ms one); le=0.1 counts everything
        // ≤ 100ms (3ms + 80ms); le=1.0 counts everything ≤ 1s (3ms + 80ms
        // + 450ms); le=5.0 counts everything (all four).
        LatencyHistogramBucket b_5ms = histogram.Buckets.First(b => b.UpperBoundSeconds == 0.005);
        LatencyHistogramBucket b_100ms = histogram.Buckets.First(b => b.UpperBoundSeconds == 0.1);
        LatencyHistogramBucket b_1s = histogram.Buckets.First(b => b.UpperBoundSeconds == 1.0);
        LatencyHistogramBucket b_5s = histogram.Buckets.First(b => b.UpperBoundSeconds == 5.0);

        Assert.That(b_5ms.CumulativeCount, Is.EqualTo(1));
        Assert.That(b_100ms.CumulativeCount, Is.EqualTo(2));
        Assert.That(b_1s.CumulativeCount, Is.EqualTo(3));
        Assert.That(b_5s.CumulativeCount, Is.EqualTo(4));
    }

    [Test]
    public void RecordChatLatency_NegativeOrInvalidDurations_AreIgnored()
    {
        // Defensive: a miscomputed Stopwatch.GetElapsedTime() shouldn't
        // corrupt the histogram. Negative / NaN / infinite durations
        // silently drop.
        var metrics = new PalLlmMetrics();

        metrics.RecordChatLatency(TimeSpan.FromMilliseconds(-5));
        metrics.RecordChatLatency(TimeSpan.FromTicks(long.MinValue));
        metrics.RecordChatLatency(TimeSpan.FromMilliseconds(10));

        Assert.That(metrics.Snapshot().ChatLatency.Count, Is.EqualTo(1),
            "Only the valid 10ms observation should count.");
    }

    [Test]
    public void PrometheusExporter_Render_IncludesLabeledFallbackCountersAndHistogram()
    {
        // End-to-end: record a few events via PalLlmMetrics, drop the
        // snapshot into a RuntimeHealth, render to Prometheus text,
        // spot-check the expected lines.
        var metrics = new PalLlmMetrics();
        metrics.RecordFallbackStrategy("stealth-withdraw");
        metrics.RecordFallbackStrategy("crafting-discipline");
        metrics.RecordFallbackStrategy("crafting-discipline");
        metrics.RecordTierTransition("<none>", "small");
        metrics.RecordTierTransition("small", "large");
        metrics.RecordChatLatency(TimeSpan.FromMilliseconds(42));

        PalLlmMetricsSnapshot snapshot = metrics.Snapshot();
        var health = new RuntimeHealth
        {
            FallbackStrategyCounts = snapshot.FallbackStrategyCounts,
            ModelTierTransitionCounts = snapshot.ModelTierTransitionCounts,
            ChatLatency = snapshot.ChatLatency,
        };

        string text = PrometheusExporter.Render(health);

        Assert.That(text, Does.Contain("palllm_fallback_strategy_total{strategy=\"stealth-withdraw\"} 1"));
        Assert.That(text, Does.Contain("palllm_fallback_strategy_total{strategy=\"crafting-discipline\"} 2"));
        Assert.That(text, Does.Contain("palllm_model_tier_transition_total{from=\"<none>\",to=\"small\"} 1"));
        Assert.That(text, Does.Contain("palllm_model_tier_transition_total{from=\"small\",to=\"large\"} 1"));

        // Histogram shape — cumulative bucket + _count + _sum + +Inf.
        Assert.That(text, Does.Contain("palllm_chat_duration_seconds_bucket{le=\"+Inf\"} 1"));
        Assert.That(text, Does.Contain("palllm_chat_duration_seconds_count 1"));
        Assert.That(text, Does.Contain("palllm_chat_duration_seconds_sum "));
    }

    [Test]
    public void PrometheusExporter_Render_WithNoMetricRecords_DoesNotEmitLabeledBlocks()
    {
        // Empty state: don't emit labeled-counter blocks at all. Keeps
        // the /metrics output tight on a fresh sidecar that hasn't
        // served any chat yet. Prometheus scrape parses cleanly either
        // way, but empty TYPE/HELP lines would be noise.
        var health = new RuntimeHealth();
        string text = PrometheusExporter.Render(health);

        Assert.That(text, Does.Not.Contain("palllm_fallback_strategy_total"));
        Assert.That(text, Does.Not.Contain("palllm_model_tier_transition_total"));
        Assert.That(text, Does.Not.Contain("palllm_chat_duration_seconds"));
    }
}
