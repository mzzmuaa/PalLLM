using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

public readonly record struct InferencePerformanceSample(
    string OperationName,
    string ProviderName,
    string RequestModel,
    string? ResponseModel,
    bool Success,
    string? ErrorType,
    long DurationMs,
    int PromptTokens,
    int CompletionTokens,
    DateTimeOffset CompletedAtUtc,
    string? SystemFingerprint = null,
    string? ResponseId = null,
    IReadOnlyList<string>? FinishReasons = null,
    string? UpstreamRequestId = null,
    double? UpstreamProcessingMs = null,
    double? UpstreamQueueMs = null,
    double? UpstreamTimeToFirstTokenMs = null,
    double? UpstreamPrefillMs = null,
    double? UpstreamDecodeMs = null,
    int CachedPromptTokens = 0,
    int PromptAudioTokens = 0,
    int CompletionReasoningTokens = 0,
    int CompletionAudioTokens = 0,
    int AcceptedPredictionTokens = 0,
    int RejectedPredictionTokens = 0)
{
    public string EffectiveModel =>
        !string.IsNullOrWhiteSpace(ResponseModel) ? ResponseModel! : RequestModel;

    public long TotalTokens => Math.Max(0, PromptTokens) + Math.Max(0, CompletionTokens);

    public long TotalUsageDetailTokens =>
        Math.Max(0, CachedPromptTokens) +
        Math.Max(0, PromptAudioTokens) +
        Math.Max(0, CompletionReasoningTokens) +
        Math.Max(0, CompletionAudioTokens) +
        Math.Max(0, AcceptedPredictionTokens) +
        Math.Max(0, RejectedPredictionTokens);
}

public sealed class InferencePerformanceTracker
{
    private const int RetentionLimit = 512;
    private const int AssessmentMinimumSampleCount = 3;
    private const int HealthySuccessRatioPercent = 99;
    private const int DegradedSuccessRatioPercent = 95;
    private const int HealthyTargetHitRatioPercent = 90;
    private const int DegradedCeilingHitRatioPercent = 80;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly InferencePerformanceBudget ChatBudget = new("interactive_chat", 3_000, 8_000);
    private static readonly InferencePerformanceBudget VisionBudget = new("vision_extract", 2_500, 6_000);
    private readonly object _gate = new();
    private readonly Queue<InferencePerformanceSample> _samples = new(RetentionLimit);

    private readonly record struct InferenceLaneKey(
        string OperationName,
        string ProviderName,
        string Model);

    private readonly record struct InferencePerformanceBudget(
        string Name,
        int LatencyTargetMs,
        int LatencyCeilingMs);

    public void Record(InferencePerformanceSample sample)
    {
        if (string.IsNullOrWhiteSpace(sample.OperationName))
        {
            return;
        }

        InferencePerformanceSample normalized = sample with
        {
            ProviderName = string.IsNullOrWhiteSpace(sample.ProviderName)
                ? "openai_compatible"
                : sample.ProviderName.Trim(),
            RequestModel = string.IsNullOrWhiteSpace(sample.RequestModel)
                ? "unknown"
                : sample.RequestModel.Trim(),
            ResponseModel = string.IsNullOrWhiteSpace(sample.ResponseModel)
                ? null
                : sample.ResponseModel.Trim(),
            ErrorType = string.IsNullOrWhiteSpace(sample.ErrorType)
                ? null
                : sample.ErrorType.Trim(),
            DurationMs = Math.Max(0, sample.DurationMs),
            PromptTokens = Math.Max(0, sample.PromptTokens),
            CompletionTokens = Math.Max(0, sample.CompletionTokens),
            CompletedAtUtc = sample.CompletedAtUtc == default
                ? DateTimeOffset.UtcNow
                : sample.CompletedAtUtc,
            SystemFingerprint = string.IsNullOrWhiteSpace(sample.SystemFingerprint)
                ? null
                : sample.SystemFingerprint.Trim(),
            ResponseId = string.IsNullOrWhiteSpace(sample.ResponseId)
                ? null
                : sample.ResponseId.Trim(),
            FinishReasons = NormalizeFinishReasons(sample.FinishReasons),
            UpstreamRequestId = string.IsNullOrWhiteSpace(sample.UpstreamRequestId)
                ? null
                : HttpResponseReceiptExtractor.NormalizeIdentifier(sample.UpstreamRequestId),
            UpstreamProcessingMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(sample.UpstreamProcessingMs),
            UpstreamQueueMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(sample.UpstreamQueueMs),
            UpstreamTimeToFirstTokenMs =
                HttpResponseReceiptExtractor.NormalizeProcessingMs(sample.UpstreamTimeToFirstTokenMs),
            UpstreamPrefillMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(sample.UpstreamPrefillMs),
            UpstreamDecodeMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(sample.UpstreamDecodeMs),
            CachedPromptTokens = Math.Max(0, sample.CachedPromptTokens),
            PromptAudioTokens = Math.Max(0, sample.PromptAudioTokens),
            CompletionReasoningTokens = Math.Max(0, sample.CompletionReasoningTokens),
            CompletionAudioTokens = Math.Max(0, sample.CompletionAudioTokens),
            AcceptedPredictionTokens = Math.Max(0, sample.AcceptedPredictionTokens),
            RejectedPredictionTokens = Math.Max(0, sample.RejectedPredictionTokens),
        };

        lock (_gate)
        {
            _samples.Enqueue(normalized);
            while (_samples.Count > RetentionLimit)
            {
                _samples.Dequeue();
            }
        }
    }

    public InferencePerformanceSnapshot GetSnapshot()
    {
        InferencePerformanceSample[] captured;
        lock (_gate)
        {
            captured = _samples.ToArray();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cutoff = now - Window;
        InferencePerformanceSample[] recent = captured
            .Where(sample => sample.CompletedAtUtc >= cutoff)
            .OrderByDescending(sample => sample.CompletedAtUtc)
            .ToArray();

        if (recent.Length == 0)
        {
            return new InferencePerformanceSnapshot
            {
                GeneratedAtUtc = now,
                WindowMinutes = (int)Window.TotalMinutes,
                RetainedOperationLimit = RetentionLimit,
                Assessment = BuildAssessment(recent),
                Summary = "No recent live inference or vision operations recorded in the current window.",
            };
        }

        InferencePerformanceLaneSnapshot[] lanes = recent
            .GroupBy(sample => new InferenceLaneKey(sample.OperationName, sample.ProviderName, sample.EffectiveModel))
            .Select(group => BuildLaneSnapshot(group))
            .OrderByDescending(lane => lane.SampleCount)
            .ThenByDescending(lane => lane.LastObservedAtUtc)
            .ThenBy(lane => lane.OperationName, StringComparer.Ordinal)
            .ThenBy(lane => lane.Model, StringComparer.Ordinal)
            .ToArray();

        int successCount = recent.Count(sample => sample.Success);
        int failureCount = recent.Length - successCount;
        long[] latencies = recent.Select(sample => sample.DurationMs).OrderBy(duration => duration).ToArray();
        long totalPromptTokens = recent.Sum(sample => Math.Max(0L, sample.PromptTokens));
        long totalCompletionTokens = recent.Sum(sample => Math.Max(0L, sample.CompletionTokens));
        long totalCachedPromptTokens = recent.Sum(sample => Math.Max(0L, sample.CachedPromptTokens));
        long totalPromptAudioTokens = recent.Sum(sample => Math.Max(0L, sample.PromptAudioTokens));
        long totalCompletionReasoningTokens = recent.Sum(sample => Math.Max(0L, sample.CompletionReasoningTokens));
        long totalCompletionAudioTokens = recent.Sum(sample => Math.Max(0L, sample.CompletionAudioTokens));
        long totalAcceptedPredictionTokens = recent.Sum(sample => Math.Max(0L, sample.AcceptedPredictionTokens));
        long totalRejectedPredictionTokens = recent.Sum(sample => Math.Max(0L, sample.RejectedPredictionTokens));

        return new InferencePerformanceSnapshot
        {
            GeneratedAtUtc = now,
            WindowMinutes = (int)Window.TotalMinutes,
            RetainedOperationLimit = RetentionLimit,
            SampleCount = recent.Length,
            SuccessCount = successCount,
            FailureCount = failureCount,
            AverageLatencyMs = RoundToLong(latencies.Average()),
            P95LatencyMs = CalculatePercentile(latencies, 0.95),
            TotalPromptTokens = totalPromptTokens,
            TotalCompletionTokens = totalCompletionTokens,
            TotalTokens = totalPromptTokens + totalCompletionTokens,
            TotalCachedPromptTokens = totalCachedPromptTokens,
            TotalPromptAudioTokens = totalPromptAudioTokens,
            TotalCompletionReasoningTokens = totalCompletionReasoningTokens,
            TotalCompletionAudioTokens = totalCompletionAudioTokens,
            TotalAcceptedPredictionTokens = totalAcceptedPredictionTokens,
            TotalRejectedPredictionTokens = totalRejectedPredictionTokens,
            LastOperationAtUtc = recent[0].CompletedAtUtc,
            Assessment = BuildAssessment(recent),
            Summary = BuildSummary(recent.Length, lanes.Length, successCount, failureCount, latencies),
            Lanes = lanes,
        };
    }

    private static InferencePerformanceLaneSnapshot BuildLaneSnapshot(
        IGrouping<InferenceLaneKey, InferencePerformanceSample> group)
    {
        InferencePerformanceSample[] samples = group
            .OrderByDescending(sample => sample.CompletedAtUtc)
            .ToArray();
        InferencePerformanceSample latest = samples[0];
        int successCount = samples.Count(sample => sample.Success);
        int failureCount = samples.Length - successCount;
        long[] latencies = samples.Select(sample => sample.DurationMs).OrderBy(duration => duration).ToArray();
        long totalPromptTokens = samples.Sum(sample => Math.Max(0L, sample.PromptTokens));
        long totalCompletionTokens = samples.Sum(sample => Math.Max(0L, sample.CompletionTokens));
        long totalCachedPromptTokens = samples.Sum(sample => Math.Max(0L, sample.CachedPromptTokens));
        long totalPromptAudioTokens = samples.Sum(sample => Math.Max(0L, sample.PromptAudioTokens));
        long totalCompletionReasoningTokens = samples.Sum(sample => Math.Max(0L, sample.CompletionReasoningTokens));
        long totalCompletionAudioTokens = samples.Sum(sample => Math.Max(0L, sample.CompletionAudioTokens));
        long totalAcceptedPredictionTokens = samples.Sum(sample => Math.Max(0L, sample.AcceptedPredictionTokens));
        long totalRejectedPredictionTokens = samples.Sum(sample => Math.Max(0L, sample.RejectedPredictionTokens));
        DateTimeOffset? lastSuccessAt = samples
            .Where(sample => sample.Success)
            .Select(sample => (DateTimeOffset?)sample.CompletedAtUtc)
            .FirstOrDefault();
        DateTimeOffset? lastFailureAt = samples
            .Where(sample => !sample.Success)
            .Select(sample => (DateTimeOffset?)sample.CompletedAtUtc)
            .FirstOrDefault();
        string lastErrorType = samples
            .Where(sample => !sample.Success)
            .Select(sample => sample.ErrorType)
            .FirstOrDefault(errorType => !string.IsNullOrWhiteSpace(errorType))
            ?? string.Empty;

        return new InferencePerformanceLaneSnapshot
        {
            OperationName = latest.OperationName,
            ProviderName = latest.ProviderName,
            Model = latest.EffectiveModel,
            RequestModel = latest.RequestModel,
            ResponseModel = latest.ResponseModel ?? string.Empty,
            LastResponseId = latest.ResponseId ?? string.Empty,
            LastUpstreamRequestId = latest.UpstreamRequestId ?? string.Empty,
            LastUpstreamProcessingMs = latest.UpstreamProcessingMs,
            LastUpstreamQueueMs = latest.UpstreamQueueMs,
            LastUpstreamTimeToFirstTokenMs = latest.UpstreamTimeToFirstTokenMs,
            LastUpstreamPrefillMs = latest.UpstreamPrefillMs,
            LastUpstreamDecodeMs = latest.UpstreamDecodeMs,
            LastSystemFingerprint = latest.SystemFingerprint ?? string.Empty,
            LastFinishReasons = latest.FinishReasons ?? Array.Empty<string>(),
            SampleCount = samples.Length,
            SuccessCount = successCount,
            FailureCount = failureCount,
            AverageLatencyMs = RoundToLong(latencies.Average()),
            P95LatencyMs = CalculatePercentile(latencies, 0.95),
            LastLatencyMs = latest.DurationMs,
            LastPromptTokens = Math.Max(0, latest.PromptTokens),
            LastCompletionTokens = Math.Max(0, latest.CompletionTokens),
            LastTotalTokens = latest.TotalTokens,
            LastCachedPromptTokens = Math.Max(0, latest.CachedPromptTokens),
            LastPromptAudioTokens = Math.Max(0, latest.PromptAudioTokens),
            LastCompletionReasoningTokens = Math.Max(0, latest.CompletionReasoningTokens),
            LastCompletionAudioTokens = Math.Max(0, latest.CompletionAudioTokens),
            LastAcceptedPredictionTokens = Math.Max(0, latest.AcceptedPredictionTokens),
            LastRejectedPredictionTokens = Math.Max(0, latest.RejectedPredictionTokens),
            AveragePromptTokens = samples.Length == 0
                ? 0
                : RoundToLong(samples.Average(sample => (double)Math.Max(0, sample.PromptTokens))),
            AverageCompletionTokens = samples.Length == 0
                ? 0
                : RoundToLong(samples.Average(sample => (double)Math.Max(0, sample.CompletionTokens))),
            TotalPromptTokens = totalPromptTokens,
            TotalCompletionTokens = totalCompletionTokens,
            TotalTokens = totalPromptTokens + totalCompletionTokens,
            TotalCachedPromptTokens = totalCachedPromptTokens,
            TotalPromptAudioTokens = totalPromptAudioTokens,
            TotalCompletionReasoningTokens = totalCompletionReasoningTokens,
            TotalCompletionAudioTokens = totalCompletionAudioTokens,
            TotalAcceptedPredictionTokens = totalAcceptedPredictionTokens,
            TotalRejectedPredictionTokens = totalRejectedPredictionTokens,
            LastObservedAtUtc = latest.CompletedAtUtc,
            LastSuccessAtUtc = lastSuccessAt,
            LastFailureAtUtc = lastFailureAt,
            LastErrorType = lastErrorType,
            Assessment = BuildAssessment(samples),
        };
    }

    private static InferencePerformanceAssessmentSnapshot BuildAssessment(
        IReadOnlyList<InferencePerformanceSample> samples)
    {
        if (samples.Count == 0)
        {
            return new InferencePerformanceAssessmentSnapshot
            {
                Status = "no_data",
                BudgetName = "recent_window",
                MinimumSampleCount = AssessmentMinimumSampleCount,
                Summary = "No recent live inference or vision operations recorded in the current window.",
            };
        }

        InferencePerformanceBudget[] budgets = samples
            .Select(sample => ResolveBudget(sample.OperationName))
            .ToArray();
        bool mixedBudget = budgets
            .Select(budget => budget.Name)
            .Distinct(StringComparer.Ordinal)
            .Count() > 1;
        InferencePerformanceBudget primaryBudget = budgets[0];

        int successCount = samples.Count(sample => sample.Success);
        int targetHitCount = 0;
        int ceilingHitCount = 0;
        for (int index = 0; index < samples.Count; index++)
        {
            InferencePerformanceSample sample = samples[index];
            InferencePerformanceBudget budget = budgets[index];
            if (!sample.Success)
            {
                continue;
            }

            if (sample.DurationMs <= budget.LatencyTargetMs)
            {
                targetHitCount++;
            }

            if (sample.DurationMs <= budget.LatencyCeilingMs)
            {
                ceilingHitCount++;
            }
        }

        int successRatioPercent = CalculatePercentage(successCount, samples.Count);
        int targetHitRatioPercent = CalculatePercentage(targetHitCount, samples.Count);
        int ceilingHitRatioPercent = CalculatePercentage(ceilingHitCount, samples.Count);
        bool meetsHealthyBudget =
            successRatioPercent >= HealthySuccessRatioPercent
            && targetHitRatioPercent >= HealthyTargetHitRatioPercent;
        bool meetsDegradedBudget =
            successRatioPercent >= DegradedSuccessRatioPercent
            && ceilingHitRatioPercent >= DegradedCeilingHitRatioPercent;

        string status = meetsHealthyBudget
            ? (samples.Count >= AssessmentMinimumSampleCount ? "healthy" : "insufficient_data")
            : (meetsDegradedBudget ? "degraded" : "critical");

        return new InferencePerformanceAssessmentSnapshot
        {
            Status = status,
            BudgetName = mixedBudget ? "mixed_recent_window" : primaryBudget.Name,
            MinimumSampleCount = AssessmentMinimumSampleCount,
            SuccessRatioPercent = successRatioPercent,
            TargetHitCount = targetHitCount,
            CeilingHitCount = ceilingHitCount,
            TargetHitRatioPercent = targetHitRatioPercent,
            CeilingHitRatioPercent = ceilingHitRatioPercent,
            LatencyTargetMs = mixedBudget ? null : primaryBudget.LatencyTargetMs,
            LatencyCeilingMs = mixedBudget ? null : primaryBudget.LatencyCeilingMs,
            Summary = BuildAssessmentSummary(
                samples.Count,
                status,
                primaryBudget,
                mixedBudget,
                successRatioPercent,
                targetHitRatioPercent,
                ceilingHitRatioPercent),
        };
    }

    private static string BuildSummary(
        int sampleCount,
        int laneCount,
        int successCount,
        int failureCount,
        IReadOnlyList<long> latencies)
    {
        long p95 = CalculatePercentile(latencies, 0.95);
        return failureCount > 0
            ? $"{sampleCount} recent operations across {laneCount} lanes; p95 {p95} ms; {successCount} succeeded and {failureCount} failed."
            : $"{sampleCount} recent operations across {laneCount} lanes; p95 {p95} ms; all recent calls succeeded.";
    }

    private static string BuildAssessmentSummary(
        int sampleCount,
        string status,
        InferencePerformanceBudget budget,
        bool mixedBudget,
        int successRatioPercent,
        int targetHitRatioPercent,
        int ceilingHitRatioPercent)
    {
        string targetLabel = mixedBudget
            ? "their lane target budgets"
            : $"the {budget.LatencyTargetMs} ms target";
        string ceilingLabel = mixedBudget
            ? "their lane ceiling budgets"
            : $"the {budget.LatencyCeilingMs} ms ceiling";

        return status switch
        {
            "no_data" =>
                "No recent live inference or vision operations recorded in the current window.",
            "insufficient_data" =>
                $"{successRatioPercent}% of {sampleCount} recent operations succeeded and {targetHitRatioPercent}% met {targetLabel}; collect {AssessmentMinimumSampleCount} samples before treating the window as proven.",
            "healthy" =>
                $"{successRatioPercent}% of {sampleCount} recent operations succeeded and {targetHitRatioPercent}% met {targetLabel}.",
            "degraded" =>
                $"{successRatioPercent}% of {sampleCount} recent operations succeeded, but only {targetHitRatioPercent}% met {targetLabel}; {ceilingHitRatioPercent}% still stayed inside {ceilingLabel}.",
            _ =>
                $"{successRatioPercent}% of {sampleCount} recent operations succeeded and only {ceilingHitRatioPercent}% stayed inside {ceilingLabel}.",
        };
    }

    private static long CalculatePercentile(
        IReadOnlyList<long> sortedValues,
        double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(sortedValues.Count * percentile) - 1;
        index = Math.Clamp(index, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    private static InferencePerformanceBudget ResolveBudget(string operationName) =>
        string.Equals(operationName, GenAiTelemetry.OperationGenerateContent, StringComparison.Ordinal)
            ? VisionBudget
            : ChatBudget;

    private static int CalculatePercentage(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            return 0;
        }

        double value = (double)numerator / denominator * 100d;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    private static long RoundToLong(double value) =>
        double.IsFinite(value)
            ? Convert.ToInt64(Math.Round(value, MidpointRounding.AwayFromZero))
            : 0;

    private static string[] NormalizeFinishReasons(IReadOnlyList<string>? finishReasons)
    {
        if (finishReasons is null || finishReasons.Count == 0)
        {
            return [];
        }

        List<string> normalized = [];
        foreach (string reason in finishReasons)
        {
            if (string.IsNullOrWhiteSpace(reason))
            {
                continue;
            }

            normalized.Add(reason.Trim());
        }

        return normalized.Count == 0 ? [] : normalized.ToArray();
    }
}
