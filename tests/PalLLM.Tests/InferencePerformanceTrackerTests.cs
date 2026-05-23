using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

public sealed class InferencePerformanceTrackerTests
{
    [Test]
    public void GetSnapshot_WhenRecentWindowMeetsBudget_ReportsHealthyAssessment()
    {
        var tracker = new InferencePerformanceTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Record(new InferencePerformanceSample(
            "chat",
            "openai_compatible",
            "fast-q4",
            "fast-q4",
            true,
            null,
            850,
            20,
            10,
            now,
            FinishReasons: ["stop"],
            UpstreamRequestId: "req-upstream-001",
            UpstreamProcessingMs: 127.5,
            UpstreamQueueMs: 3.5,
            UpstreamTimeToFirstTokenMs: 45.25,
            UpstreamPrefillMs: 22.125,
            UpstreamDecodeMs: 61.75,
            CachedPromptTokens: 8,
            PromptAudioTokens: 2,
            CompletionReasoningTokens: 3,
            CompletionAudioTokens: 1,
            AcceptedPredictionTokens: 4,
            RejectedPredictionTokens: 5));
        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "fast-q4", "fast-q4", true, null, 1025, 21, 11, now.AddSeconds(-5)));
        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "fast-q4", "fast-q4", true, null, 1410, 19, 12, now.AddSeconds(-10)));

        InferencePerformanceSnapshot snapshot = tracker.GetSnapshot();

        Assert.That(snapshot.Assessment.Status, Is.EqualTo("healthy"));
        Assert.That(snapshot.Assessment.BudgetName, Is.EqualTo("interactive_chat"));
        Assert.That(snapshot.Assessment.TargetHitRatioPercent, Is.EqualTo(100));
        Assert.That(snapshot.Assessment.CeilingHitRatioPercent, Is.EqualTo(100));
        Assert.That(snapshot.Lanes, Has.Count.EqualTo(1));
        Assert.That(snapshot.Lanes[0].Assessment.Status, Is.EqualTo("healthy"));
        Assert.That(snapshot.Lanes[0].LastPromptTokens, Is.EqualTo(20));
        Assert.That(snapshot.Lanes[0].LastCompletionTokens, Is.EqualTo(10));
        Assert.That(snapshot.Lanes[0].LastTotalTokens, Is.EqualTo(30));
        Assert.That(snapshot.TotalCachedPromptTokens, Is.EqualTo(8));
        Assert.That(snapshot.TotalPromptAudioTokens, Is.EqualTo(2));
        Assert.That(snapshot.TotalCompletionReasoningTokens, Is.EqualTo(3));
        Assert.That(snapshot.TotalCompletionAudioTokens, Is.EqualTo(1));
        Assert.That(snapshot.TotalAcceptedPredictionTokens, Is.EqualTo(4));
        Assert.That(snapshot.TotalRejectedPredictionTokens, Is.EqualTo(5));
        Assert.That(snapshot.Lanes[0].LastCachedPromptTokens, Is.EqualTo(8));
        Assert.That(snapshot.Lanes[0].LastPromptAudioTokens, Is.EqualTo(2));
        Assert.That(snapshot.Lanes[0].LastCompletionReasoningTokens, Is.EqualTo(3));
        Assert.That(snapshot.Lanes[0].LastCompletionAudioTokens, Is.EqualTo(1));
        Assert.That(snapshot.Lanes[0].LastAcceptedPredictionTokens, Is.EqualTo(4));
        Assert.That(snapshot.Lanes[0].LastRejectedPredictionTokens, Is.EqualTo(5));
        Assert.That(snapshot.Lanes[0].TotalCachedPromptTokens, Is.EqualTo(8));
        Assert.That(snapshot.Lanes[0].TotalCompletionReasoningTokens, Is.EqualTo(3));
        Assert.That(snapshot.Lanes[0].LastFinishReasons, Is.EqualTo(new[] { "stop" }));
        Assert.That(snapshot.Lanes[0].LastUpstreamRequestId, Is.EqualTo("req-upstream-001"));
        Assert.That(snapshot.Lanes[0].LastUpstreamProcessingMs, Is.EqualTo(127.5));
        Assert.That(snapshot.Lanes[0].LastUpstreamQueueMs, Is.EqualTo(3.5));
        Assert.That(snapshot.Lanes[0].LastUpstreamTimeToFirstTokenMs, Is.EqualTo(45.25));
        Assert.That(snapshot.Lanes[0].LastUpstreamPrefillMs, Is.EqualTo(22.125));
        Assert.That(snapshot.Lanes[0].LastUpstreamDecodeMs, Is.EqualTo(61.75));
    }

    [Test]
    public void GetSnapshot_WhenWindowMissesTargetButStaysInsideCeiling_ReportsDegradedAssessment()
    {
        var tracker = new InferencePerformanceTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "slow-q4", "slow-q4", true, null, 3_200, 18, 9, now));
        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "slow-q4", "slow-q4", true, null, 4_100, 17, 8, now.AddSeconds(-5)));
        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "slow-q4", "slow-q4", true, null, 5_400, 20, 10, now.AddSeconds(-10)));

        InferencePerformanceSnapshot snapshot = tracker.GetSnapshot();

        Assert.That(snapshot.Assessment.Status, Is.EqualTo("degraded"));
        Assert.That(snapshot.Assessment.TargetHitRatioPercent, Is.EqualTo(0));
        Assert.That(snapshot.Assessment.CeilingHitRatioPercent, Is.EqualTo(100));
        Assert.That(snapshot.Lanes[0].Assessment.Status, Is.EqualTo("degraded"));
    }

    [Test]
    public void GetSnapshot_WhenWindowContainsFailuresAndVerySlowCalls_ReportsCriticalAssessment()
    {
        var tracker = new InferencePerformanceTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "worker-q4", "worker-q4", false, "timeout", 9_500, 0, 0, now));
        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "worker-q4", "worker-q4", true, null, 8_600, 16, 8, now.AddSeconds(-5)));
        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "worker-q4", "worker-q4", true, null, 7_900, 14, 6, now.AddSeconds(-10)));

        InferencePerformanceSnapshot snapshot = tracker.GetSnapshot();

        Assert.That(snapshot.Assessment.Status, Is.EqualTo("critical"));
        Assert.That(snapshot.Assessment.SuccessRatioPercent, Is.EqualTo(67));
        Assert.That(snapshot.Assessment.CeilingHitRatioPercent, Is.EqualTo(33));
        Assert.That(snapshot.Lanes[0].Assessment.Status, Is.EqualTo("critical"));
    }

    [Test]
    public void GetSnapshot_WhenRecentWindowMixesChatAndVision_UsesMixedBudgetSummary()
    {
        var tracker = new InferencePerformanceTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        tracker.Record(new InferencePerformanceSample("chat", "openai_compatible", "fast-q4", "fast-q4", true, null, 1_200, 10, 4, now));
        tracker.Record(new InferencePerformanceSample("generate_content", "openai_compatible", "vision-e2b", "vision-e2b", true, null, 2_200, 15, 6, now.AddSeconds(-5)));
        tracker.Record(new InferencePerformanceSample("generate_content", "openai_compatible", "vision-e2b", "vision-e2b", true, null, 2_350, 15, 6, now.AddSeconds(-10)));

        InferencePerformanceSnapshot snapshot = tracker.GetSnapshot();

        Assert.That(snapshot.Assessment.Status, Is.EqualTo("healthy"));
        Assert.That(snapshot.Assessment.BudgetName, Is.EqualTo("mixed_recent_window"));
        Assert.That(snapshot.Assessment.LatencyTargetMs, Is.Null);
        Assert.That(snapshot.Assessment.LatencyCeilingMs, Is.Null);
    }
}
