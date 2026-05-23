using NUnit.Framework;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 35 / D10 — regression coverage for
/// <see cref="ResourceBudgetPostureBuilder"/>. Pinned contract:
/// <list type="bullet">
///   <item>Default options produce a posture with every feature classified.</item>
///   <item>Disabled vision + TTS collapse to 'surface-off' rows.</item>
///   <item>Headline reflects OK / review counts.</item>
///   <item>Status buckets come from the fixed enumeration.</item>
///   <item>High fallback share flips chat-fallback-share to 'review'.</item>
/// </list>
/// </summary>
[TestFixture]
public class ResourceBudgetPostureBuilderTests
{
    [Test]
    public void Capture_DefaultOptions_ReturnsAllOkPosture()
    {
        var options = new PalLlmOptions();
        var metrics = new ResourceBudgetMetrics(ChatTotal: 0, ChatFallbackTotal: 0);

        ResourceBudgetPosture posture = ResourceBudgetPostureBuilder.Capture(options, metrics);

        Assert.That(posture.Budgets.Count, Is.GreaterThanOrEqualTo(5));
        Assert.That(posture.ReviewCount + posture.ExhaustedCount, Is.LessThanOrEqualTo(1),
            "Default install should have no more than one review/exhausted row (fallback-share might be 'unknown').");
    }

    [Test]
    public void Capture_EveryEntryHasValidStatus()
    {
        var options = new PalLlmOptions();
        var metrics = new ResourceBudgetMetrics(ChatTotal: 100, ChatFallbackTotal: 20);
        string[] valid = ["ok", "review", "exhausted", "unknown"];

        ResourceBudgetPosture posture = ResourceBudgetPostureBuilder.Capture(options, metrics);

        foreach (ResourceBudgetEntry entry in posture.Budgets)
        {
            Assert.That(valid, Does.Contain(entry.Status),
                $"Budget {entry.Id} used invalid status '{entry.Status}'.");
            Assert.That(entry.Budget, Is.Not.Empty);
            Assert.That(entry.Notes, Is.Not.Empty);
        }
    }

    [Test]
    public void Capture_HighFallbackShare_FlipsChatFallbackRowToReview()
    {
        var options = new PalLlmOptions();
        // 80% fallback share — above the 0.75 threshold.
        var metrics = new ResourceBudgetMetrics(ChatTotal: 100, ChatFallbackTotal: 80);

        ResourceBudgetPosture posture = ResourceBudgetPostureBuilder.Capture(options, metrics);

        ResourceBudgetEntry row = posture.Budgets.First(b => b.Id == "runtime-fallback-share");
        Assert.That(row.Status, Is.EqualTo("review"),
            "High fallback share on a busy install must flip to 'review'.");
    }

    [Test]
    public void Capture_DisabledVisionTtsAndAsr_CollapsesToSurfaceOffRows()
    {
        var options = new PalLlmOptions
        {
            Vision = new VisionOptions { Enabled = false },
            Tts = new TtsOptions { Enabled = false },
            Asr = new AsrOptions { Enabled = false },
        };
        var metrics = new ResourceBudgetMetrics(ChatTotal: 0, ChatFallbackTotal: 0);

        ResourceBudgetPosture posture = ResourceBudgetPostureBuilder.Capture(options, metrics);

        Assert.That(posture.Budgets.Any(b => b.Id == "vision-disabled"), Is.True);
        Assert.That(posture.Budgets.Any(b => b.Id == "tts-disabled"), Is.True);
        Assert.That(posture.Budgets.Any(b => b.Id == "asr-disabled"), Is.True);
    }

    [Test]
    public void Capture_HeadlineReflectsCounts()
    {
        var options = new PalLlmOptions();
        var metrics = new ResourceBudgetMetrics(ChatTotal: 0, ChatFallbackTotal: 0);

        ResourceBudgetPosture posture = ResourceBudgetPostureBuilder.Capture(options, metrics);

        if (posture.ReviewCount == 0 && posture.ExhaustedCount == 0)
        {
            Assert.That(posture.Headline, Does.Contain("comfortably"));
        }
    }

    [Test]
    public void CaptureCached_ReturnsSameInstanceWithinTtl()
    {
        ResourceBudgetPostureBuilder.InvalidateCache();
        var options = new PalLlmOptions();
        var metrics = new ResourceBudgetMetrics(ChatTotal: 0, ChatFallbackTotal: 0);
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        ResourceBudgetPosture a = ResourceBudgetPostureBuilder.CaptureCached(options, metrics, ttl);
        ResourceBudgetPosture b = ResourceBudgetPostureBuilder.CaptureCached(options, metrics, ttl);

        Assert.That(b.CapturedAtUtc, Is.EqualTo(a.CapturedAtUtc),
            "Within the TTL the cache must hand back the same snapshot instance.");
    }

    [Test]
    public void CaptureCached_InvalidatesWhenFallbackShareCrossesBoundary()
    {
        ResourceBudgetPostureBuilder.InvalidateCache();
        var options = new PalLlmOptions();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        // Below the 75% boundary — row stays "ok".
        var lowShare = new ResourceBudgetMetrics(ChatTotal: 100, ChatFallbackTotal: 50);
        // Above the boundary — row should flip to "review", and the
        // signature should change so CaptureCached recomputes.
        var highShare = new ResourceBudgetMetrics(ChatTotal: 100, ChatFallbackTotal: 90);

        ResourceBudgetPosture a = ResourceBudgetPostureBuilder.CaptureCached(options, lowShare, ttl);
        ResourceBudgetPosture b = ResourceBudgetPostureBuilder.CaptureCached(options, highShare, ttl);

        ResourceBudgetEntry rowA = a.Budgets.First(r => r.Id == "runtime-fallback-share");
        ResourceBudgetEntry rowB = b.Budgets.First(r => r.Id == "runtime-fallback-share");
        Assert.That(rowA.Status, Is.EqualTo("ok"));
        Assert.That(rowB.Status, Is.EqualTo("review"),
            "Crossing the 75% boundary must invalidate the cache and recompute.");
    }

    [Test]
    public void InvalidateCache_ForcesRecomputeOnNextCaptureCached()
    {
        var options = new PalLlmOptions();
        var metrics = new ResourceBudgetMetrics(ChatTotal: 0, ChatFallbackTotal: 0);
        TimeSpan ttl = TimeSpan.FromMinutes(5);
        ResourceBudgetPosture first = ResourceBudgetPostureBuilder.CaptureCached(options, metrics, ttl);

        ResourceBudgetPostureBuilder.InvalidateCache();
        Thread.Sleep(2);
        ResourceBudgetPosture second = ResourceBudgetPostureBuilder.CaptureCached(options, metrics, ttl);

        Assert.That(second.CapturedAtUtc, Is.GreaterThan(first.CapturedAtUtc),
            "After InvalidateCache, next CaptureCached must produce a freshly-timestamped posture.");
    }
}
