using System.Collections.Concurrent;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Process-wide domain-level metrics. Owns thread-safe counters for
/// fallback-strategy usage, model-tier transitions, and chat latency —
/// things the existing gauge-based <see cref="RuntimeHealth"/> surface
/// cannot capture because they have per-event dimensionality (strategy id,
/// from→to tier pair, latency bucket).
///
/// <para>Deliberately has no dependency on any metrics SDK (System.Diagnostics.Metrics,
/// OpenTelemetry, prometheus-net, etc.) so <see cref="PalLLM.Domain"/> stays
/// free of transport concerns. Callers record via simple methods; the
/// snapshot is pulled into <see cref="RuntimeHealth"/> and rendered by
/// <see cref="PrometheusExporter"/>. If a future revision wants to emit via
/// an SDK instead, it can wrap this class without rewriting call sites.</para>
///
/// <para>Cardinality is bounded by construction: strategy ids come from a
/// fixed 19-entry enum, tier transitions are keyed by the configured tier
/// catalog, and the latency histogram uses a pre-declared fixed bucket
/// array. No per-character or per-request labels.</para>
/// </summary>
public sealed class PalLlmMetrics
{
    /// <summary>
    /// Cumulative Prometheus-style histogram bucket upper bounds in seconds.
    /// Bucket at index i records "count of observations with duration ≤ this bound".
    /// Covers the realistic range for PalLLM chat latency: fallback-only paths
    /// sit at sub-10ms, inference-backed paths span hundreds of ms to tens of
    /// seconds depending on model size.
    /// </summary>
    public static readonly double[] ChatLatencyBucketUpperBounds =
    [
        0.005, 0.010, 0.025, 0.050, 0.100, 0.250, 0.500,
        1.0, 2.5, 5.0, 10.0, 30.0, 60.0,
    ];

    private readonly ConcurrentDictionary<string, long> _fallbackStrategyCounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _tierTransitionCounts = new(StringComparer.Ordinal);
    private readonly long[] _latencyBucketCounts = new long[ChatLatencyBucketUpperBounds.Length];
    private long _latencyCountTotal;
    private long _latencySumMicroseconds;

    public void RecordFallbackStrategy(string strategyId)
    {
        if (string.IsNullOrWhiteSpace(strategyId))
        {
            return;
        }

        _fallbackStrategyCounts.AddOrUpdate(strategyId, 1, (_, existing) => existing + 1);
    }

    public void RecordTierTransition(string fromTierId, string toTierId)
    {
        // Null-safe: use "<none>" for the initial seed transition and any
        // unknown-id defensive case. Keeping the map string-keyed (not
        // (string,string)-tuple-keyed) avoids JSON serialisation headaches
        // when the snapshot flows out via RuntimeHealth.
        string from = string.IsNullOrWhiteSpace(fromTierId) ? "<none>" : fromTierId;
        string to = string.IsNullOrWhiteSpace(toTierId) ? "<none>" : toTierId;
        string key = $"{from}->{to}";
        _tierTransitionCounts.AddOrUpdate(key, 1, (_, existing) => existing + 1);
    }

    public void RecordChatLatency(TimeSpan duration)
    {
        double seconds = duration.TotalSeconds;
        if (seconds < 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
        {
            return;
        }

        // Cumulative-bucket semantics: increment every bucket whose upper
        // bound is ≥ the observation. Prometheus histogram format reads
        // these as ≤-counts, so a 0.2s observation increments every
        // bucket from 0.25s upward.
        for (int i = 0; i < ChatLatencyBucketUpperBounds.Length; i++)
        {
            if (seconds <= ChatLatencyBucketUpperBounds[i])
            {
                Interlocked.Increment(ref _latencyBucketCounts[i]);
            }
        }

        Interlocked.Increment(ref _latencyCountTotal);
        // Store sum in microseconds so we can accumulate integrally without
        // double-precision drift at high counts. 1 second = 1_000_000 μs.
        long microseconds = (long)Math.Min(seconds * 1_000_000, long.MaxValue);
        Interlocked.Add(ref _latencySumMicroseconds, microseconds);
    }

    /// <summary>
    /// Immutable point-in-time snapshot safe to hand to serialisers,
    /// <see cref="PrometheusExporter"/>, and tests. The underlying counters
    /// keep incrementing after this returns.
    /// </summary>
    public PalLlmMetricsSnapshot Snapshot()
    {
        FallbackStrategyCount[] strategyCounts = _fallbackStrategyCounts
            .Select(pair => new FallbackStrategyCount(pair.Key, pair.Value))
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.StrategyId, StringComparer.Ordinal)
            .ToArray();

        ModelTierTransitionCount[] tierCounts = _tierTransitionCounts
            .Select(pair =>
            {
                string[] parts = pair.Key.Split("->", 2);
                string from = parts.Length == 2 ? parts[0] : pair.Key;
                string to = parts.Length == 2 ? parts[1] : "<unknown>";
                return new ModelTierTransitionCount(from, to, pair.Value);
            })
            .OrderByDescending(entry => entry.Count)
            .ThenBy(entry => entry.From, StringComparer.Ordinal)
            .ThenBy(entry => entry.To, StringComparer.Ordinal)
            .ToArray();

        long countTotal = Interlocked.Read(ref _latencyCountTotal);
        double sumSeconds = Interlocked.Read(ref _latencySumMicroseconds) / 1_000_000.0;
        LatencyHistogramBucket[] buckets = ChatLatencyBucketUpperBounds
            .Select((bound, i) => new LatencyHistogramBucket(
                UpperBoundSeconds: bound,
                CumulativeCount: Interlocked.Read(ref _latencyBucketCounts[i])))
            .ToArray();
        ChatLatencyHistogram latency = new(
            Count: countTotal,
            SumSeconds: sumSeconds,
            Buckets: buckets);

        return new PalLlmMetricsSnapshot(
            FallbackStrategyCounts: strategyCounts,
            ModelTierTransitionCounts: tierCounts,
            ChatLatency: latency);
    }
}

/// <summary>Immutable view of <see cref="PalLlmMetrics"/> state.</summary>
public sealed record PalLlmMetricsSnapshot(
    IReadOnlyList<FallbackStrategyCount> FallbackStrategyCounts,
    IReadOnlyList<ModelTierTransitionCount> ModelTierTransitionCounts,
    ChatLatencyHistogram ChatLatency);
