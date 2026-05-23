using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Liveness probe. Succeeds as long as the runtime object is constructed and the
/// process is answering requests — i.e. the sidecar is up, regardless of whether
/// downstream models are available. Scheduler tooling (K8s, Docker restarts)
/// reads this to decide whether to kill and restart the container.
/// </summary>
public sealed class LivenessHealthCheck : IHealthCheck
{
    private readonly PalLlmRuntime _runtime;

    public LivenessHealthCheck(PalLlmRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        RuntimeHealth health = _runtime.GetHealth();
        return Task.FromResult(HealthCheckResult.Healthy(
            "PalLLM sidecar process is alive.",
            new Dictionary<string, object>
            {
                ["status"] = health.Status,
                ["runtime_root"] = health.RuntimeRoot,
                ["adapter"] = health.AdapterName,
            }));
    }
}

/// <summary>
/// Readiness probe. Reports <c>Degraded</c> when the inference circuit breaker is
/// open or when the outbox has piled up past a warning threshold — the sidecar
/// can still serve fallback replies, but upstream isn't healthy. Reports
/// <c>Unhealthy</c> only when a genuinely broken invariant is detected (e.g. the
/// runtime directory doesn't exist after startup), which should never happen in
/// practice but is worth surfacing if it does.
/// </summary>
public sealed class ReadinessHealthCheck : IHealthCheck
{
    // Arbitrary soft thresholds; the point is surfacing drift, not gating traffic.
    private const int OutboxWarningThreshold = 75;
    private const int ScreenshotWarningThreshold = 40;

    private readonly PalLlmRuntime _runtime;

    public ReadinessHealthCheck(PalLlmRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        RuntimeHealth health = _runtime.GetHealth();

        var data = new Dictionary<string, object>
        {
            ["adapter_ready"] = health.AdapterReady,
            ["inference_configured"] = health.InferenceConfigured,
            ["inference_active_model"] = health.InferenceActiveModel,
            ["inference_active_tier"] = health.InferenceActiveTierId ?? string.Empty,
            ["inference_warmup_status"] = health.InferenceWarmup.Status,
            ["inference_circuit"] = health.InferenceCircuitState,
            ["outbox_pending"] = health.OutboxPendingCount,
            ["screenshot_pending"] = health.ScreenshotPendingCount,
            ["session_dirty"] = health.SessionDirty,
        };

        if (string.IsNullOrWhiteSpace(health.RuntimeRoot) || !Directory.Exists(health.RuntimeRoot))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Runtime root not configured — sidecar cannot persist or drain.", data: data));
        }

        if (string.Equals(health.InferenceCircuitState, "Open", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Inference circuit breaker is open — serving fallback replies only.", data: data));
        }

        if (health.OutboxPendingCount > OutboxWarningThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Outbox has {health.OutboxPendingCount} pending files — no consumer draining?", data: data));
        }

        if (health.ScreenshotPendingCount > ScreenshotWarningThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Screenshot queue has {health.ScreenshotPendingCount} files — watcher may be stuck.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy("PalLLM is ready for traffic.", data));
    }
}

/// <summary>
/// Recent-window inference readiness probe. Mirrors the same bounded
/// assessment behind <c>/api/inference/performance</c> into
/// <c>/health/ready</c> so lightweight automation can detect degraded or
/// critical live lanes without polling the full inspection payload.
/// </summary>
public sealed class InferencePerformanceReadinessHealthCheck : IHealthCheck
{
    private const int AlertingLanePreviewLimit = 6;

    private readonly PalLlmRuntime _runtime;

    public InferencePerformanceReadinessHealthCheck(PalLlmRuntime runtime)
    {
        _runtime = runtime;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        InferencePerformanceSnapshot snapshot = _runtime.GetInferencePerformanceSnapshot();
        InferencePerformanceAssessmentSnapshot assessment = snapshot.Assessment;
        int degradedLaneCount = snapshot.Lanes.Count(lane =>
            string.Equals(lane.Assessment.Status, InferencePerformanceStatuses.Degraded, StringComparison.Ordinal));
        int criticalLaneCount = snapshot.Lanes.Count(lane =>
            string.Equals(lane.Assessment.Status, InferencePerformanceStatuses.Critical, StringComparison.Ordinal));
        InferencePerformanceHealthLaneSignal[] alertingLanes = snapshot.Lanes
            .Where(lane => InferencePerformanceStatuses.IsAlerting(lane.Assessment.Status))
            .OrderByDescending(lane =>
                string.Equals(lane.Assessment.Status, InferencePerformanceStatuses.Critical, StringComparison.Ordinal))
            .ThenByDescending(lane => lane.SampleCount)
            .ThenBy(lane => lane.OperationName, StringComparer.Ordinal)
            .ThenBy(lane => lane.Model, StringComparer.Ordinal)
            .Take(AlertingLanePreviewLimit)
            .Select(lane => new InferencePerformanceHealthLaneSignal(
                lane.OperationName,
                lane.ProviderName,
                lane.Model,
                lane.Assessment.Status,
                lane.Assessment.BudgetName,
                lane.SampleCount,
                lane.Assessment.SuccessRatioPercent,
                lane.Assessment.TargetHitRatioPercent,
                lane.Assessment.CeilingHitRatioPercent,
                lane.LastObservedAtUtc,
                lane.LastErrorType))
            .ToArray();

        var data = new Dictionary<string, object>
        {
            ["window_minutes"] = snapshot.WindowMinutes,
            ["lane_count"] = snapshot.Lanes.Count,
            ["sample_count"] = snapshot.SampleCount,
            ["success_count"] = snapshot.SuccessCount,
            ["failure_count"] = snapshot.FailureCount,
            ["degraded_lane_count"] = degradedLaneCount,
            ["critical_lane_count"] = criticalLaneCount,
            ["assessment"] = assessment,
            ["alerting_lanes"] = alertingLanes,
        };

        if (snapshot.LastOperationAtUtc.HasValue)
        {
            data["last_operation_at_utc"] = snapshot.LastOperationAtUtc.Value;
        }

        if (string.Equals(assessment.Status, InferencePerformanceStatuses.Critical, StringComparison.Ordinal))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Recent live inference performance is outside the proven latency/reliability envelope.",
                data: data));
        }

        if (string.Equals(assessment.Status, InferencePerformanceStatuses.Degraded, StringComparison.Ordinal))
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                "Recent live inference performance is slipping outside the preferred target budget.",
                data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "Recent live inference performance is within the current readiness posture.",
            data));
    }
}

public sealed record InferencePerformanceHealthLaneSignal(
    string OperationName,
    string ProviderName,
    string Model,
    string Status,
    string BudgetName,
    int SampleCount,
    int SuccessRatioPercent,
    int TargetHitRatioPercent,
    int CeilingHitRatioPercent,
    DateTimeOffset? LastObservedAtUtc,
    string LastErrorType);

public static class PalLlmHealthResponseWriter
{
    public static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        context.Response.ContentType = "application/health+json; charset=utf-8";
        HealthReportPayload payload = HealthReportPayload.From(report);
        return JsonSerializer.SerializeAsync(
            context.Response.Body,
            payload,
            PalLlmJsonSerializerContext.Default.HealthReportPayload,
            context.RequestAborted);
    }
}
