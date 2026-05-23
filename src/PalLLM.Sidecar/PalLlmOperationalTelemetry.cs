using System.Diagnostics;
using System.Diagnostics.Metrics;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Sidecar-owned observable gauges for recent-window inference readiness.
/// Registered only when OTLP export is enabled so the default localhost
/// posture keeps the same near-zero observability overhead.
/// </summary>
public sealed class PalLlmOperationalTelemetry
{
    private static readonly long SnapshotCacheTtlTicks = Stopwatch.Frequency;

    private readonly PalLlmRuntime _runtime;
    private readonly object _snapshotGate = new();
    private readonly ObservableGauge<int> _recentWindowStatus;
    private readonly ObservableGauge<int> _recentWindowSampleCount;
    private readonly ObservableGauge<int> _recentWindowSuccessRatio;
    private readonly ObservableGauge<int> _recentWindowTargetHitRatio;
    private readonly ObservableGauge<int> _recentWindowCeilingHitRatio;
    private readonly ObservableGauge<int> _laneStatus;
    private readonly ObservableGauge<int> _laneSampleCount;
    private InferencePerformanceSnapshot? _cachedSnapshot;
    private long _nextSnapshotRefreshTick;

    public PalLlmOperationalTelemetry(PalLlmRuntime runtime)
    {
        _runtime = runtime;

        _recentWindowStatus = PalLlmTelemetry.Meter.CreateObservableGauge(
            PalLlmTelemetry.InferenceRecentWindowStatusMetricName,
            ObserveRecentWindowStatus,
            description: "Current recent-window inference readiness state. Exactly one status series is 1.");
        _recentWindowSampleCount = PalLlmTelemetry.Meter.CreateObservableGauge(
            PalLlmTelemetry.InferenceRecentWindowSampleCountMetricName,
            ObserveRecentWindowSampleCount,
            unit: "{sample}",
            description: "Recent-window live inference sample count.");
        _recentWindowSuccessRatio = PalLlmTelemetry.Meter.CreateObservableGauge(
            PalLlmTelemetry.InferenceRecentWindowSuccessRatioMetricName,
            ObserveRecentWindowSuccessRatio,
            unit: "%",
            description: "Recent-window live inference success ratio.");
        _recentWindowTargetHitRatio = PalLlmTelemetry.Meter.CreateObservableGauge(
            PalLlmTelemetry.InferenceRecentWindowTargetHitRatioMetricName,
            ObserveRecentWindowTargetHitRatio,
            unit: "%",
            description: "Recent-window live inference latency target-hit ratio.");
        _recentWindowCeilingHitRatio = PalLlmTelemetry.Meter.CreateObservableGauge(
            PalLlmTelemetry.InferenceRecentWindowCeilingHitRatioMetricName,
            ObserveRecentWindowCeilingHitRatio,
            unit: "%",
            description: "Recent-window live inference latency ceiling-hit ratio.");
        _laneStatus = PalLlmTelemetry.Meter.CreateObservableGauge(
            PalLlmTelemetry.InferenceLaneStatusMetricName,
            ObserveLaneStatus,
            description: "Current recent-window readiness state for each active inference lane. Exactly one status series per lane is 1.");
        _laneSampleCount = PalLlmTelemetry.Meter.CreateObservableGauge(
            PalLlmTelemetry.InferenceLaneSampleCountMetricName,
            ObserveLaneSampleCount,
            unit: "{sample}",
            description: "Recent-window sample count for each active inference lane.");
    }

    private InferencePerformanceSnapshot GetSnapshot()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_snapshotGate)
        {
            if (_cachedSnapshot is not null && now < _nextSnapshotRefreshTick)
            {
                return _cachedSnapshot;
            }

            _cachedSnapshot = _runtime.GetInferencePerformanceSnapshot();
            _nextSnapshotRefreshTick = now + SnapshotCacheTtlTicks;
            return _cachedSnapshot;
        }
    }

    private IEnumerable<Measurement<int>> ObserveRecentWindowStatus()
    {
        InferencePerformanceSnapshot snapshot = GetSnapshot();
        string budget = NormalizeBudget(snapshot.Assessment.BudgetName);
        foreach (string status in InferencePerformanceStatuses.All)
        {
            TagList tags = new()
            {
                { "status", status },
                { "budget", budget },
            };
            yield return new Measurement<int>(
                string.Equals(snapshot.Assessment.Status, status, StringComparison.Ordinal) ? 1 : 0,
                tags);
        }
    }

    private IEnumerable<Measurement<int>> ObserveRecentWindowSampleCount()
    {
        InferencePerformanceSnapshot snapshot = GetSnapshot();
        TagList tags = new()
        {
            { "budget", NormalizeBudget(snapshot.Assessment.BudgetName) },
        };
        yield return new Measurement<int>(snapshot.SampleCount, tags);
    }

    private IEnumerable<Measurement<int>> ObserveRecentWindowSuccessRatio()
    {
        InferencePerformanceSnapshot snapshot = GetSnapshot();
        TagList tags = new()
        {
            { "budget", NormalizeBudget(snapshot.Assessment.BudgetName) },
        };
        yield return new Measurement<int>(snapshot.Assessment.SuccessRatioPercent, tags);
    }

    private IEnumerable<Measurement<int>> ObserveRecentWindowTargetHitRatio()
    {
        InferencePerformanceSnapshot snapshot = GetSnapshot();
        TagList tags = new()
        {
            { "budget", NormalizeBudget(snapshot.Assessment.BudgetName) },
        };
        yield return new Measurement<int>(snapshot.Assessment.TargetHitRatioPercent, tags);
    }

    private IEnumerable<Measurement<int>> ObserveRecentWindowCeilingHitRatio()
    {
        InferencePerformanceSnapshot snapshot = GetSnapshot();
        TagList tags = new()
        {
            { "budget", NormalizeBudget(snapshot.Assessment.BudgetName) },
        };
        yield return new Measurement<int>(snapshot.Assessment.CeilingHitRatioPercent, tags);
    }

    private IEnumerable<Measurement<int>> ObserveLaneStatus()
    {
        InferencePerformanceSnapshot snapshot = GetSnapshot();
        foreach (InferencePerformanceLaneSnapshot lane in snapshot.Lanes)
        {
            string budget = NormalizeBudget(lane.Assessment.BudgetName);
            foreach (string status in InferencePerformanceStatuses.All)
            {
                TagList tags = CreateLaneTags(lane, budget);
                tags.Add("status", status);
                yield return new Measurement<int>(
                    string.Equals(lane.Assessment.Status, status, StringComparison.Ordinal) ? 1 : 0,
                    tags);
            }
        }
    }

    private IEnumerable<Measurement<int>> ObserveLaneSampleCount()
    {
        InferencePerformanceSnapshot snapshot = GetSnapshot();
        foreach (InferencePerformanceLaneSnapshot lane in snapshot.Lanes)
        {
            TagList tags = CreateLaneTags(lane, NormalizeBudget(lane.Assessment.BudgetName));
            yield return new Measurement<int>(lane.SampleCount, tags);
        }
    }

    private static TagList CreateLaneTags(InferencePerformanceLaneSnapshot lane, string budget) =>
        new()
        {
            { "operation", NormalizeLabel(lane.OperationName, "unknown_operation") },
            { "provider", NormalizeLabel(lane.ProviderName, "unknown_provider") },
            { "model", NormalizeLabel(lane.Model, "unknown_model") },
            { "budget", budget },
        };

    private static string NormalizeBudget(string budgetName) =>
        NormalizeLabel(budgetName, "recent_window");

    private static string NormalizeLabel(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
