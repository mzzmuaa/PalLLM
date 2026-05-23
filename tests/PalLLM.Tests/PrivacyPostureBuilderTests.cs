using NUnit.Framework;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 27 / E3 — regression coverage for the privacy posture builder.
/// Pinned contract:
/// <list type="bullet">
///   <item>Default install is fully local: zero "leaves-by-default" surfaces.</item>
///   <item>Enabling inference/vision/TTS flips the corresponding surface to "leaves-by-default".</item>
///   <item>Every surface entry has a valid Status from the three-value enumeration.</item>
///   <item>Headline reflects the counts accurately.</item>
/// </list>
/// </summary>
[TestFixture]
public class PrivacyPostureBuilderTests
{
    [Test]
    public void Capture_DefaultOptions_ShowsFullyLocalPosture()
    {
        var options = new PalLlmOptions
        {
            Inference = new InferenceOptions { Enabled = false },
            Vision = new VisionOptions { Enabled = false },
            Tts = new TtsOptions { Enabled = false },
        };

        PrivacyPosture posture = PrivacyPostureBuilder.Capture(options);

        Assert.That(posture.ActiveOutboundCount, Is.EqualTo(0),
            "Default install must have zero active outbound surfaces.");
        Assert.That(posture.NeverLeavesCount, Is.GreaterThan(5));
        Assert.That(posture.OptInAvailableCount, Is.GreaterThan(0));
        Assert.That(posture.Headline, Does.Contain("Fully local"));
    }

    [Test]
    public void Capture_WithLiveInferenceEnabled_ReportsOutboundSurface()
    {
        var options = new PalLlmOptions
        {
            Inference = new InferenceOptions { Enabled = true, BaseUrl = "http://127.0.0.1:11434/v1/" },
            Vision = new VisionOptions { Enabled = false },
            Tts = new TtsOptions { Enabled = false },
        };

        PrivacyPosture posture = PrivacyPostureBuilder.Capture(options);

        PrivacySurface inferenceSurface = posture.Surfaces.First(s => s.Id == "live-inference");
        Assert.That(inferenceSurface.Status, Is.EqualTo("leaves-by-default"));
        Assert.That(inferenceSurface.Description, Does.Contain("loopback"),
            "Description must classify the endpoint scope (loopback/private/public).");
        Assert.That(posture.ActiveOutboundCount, Is.GreaterThanOrEqualTo(1));
    }

    [Test]
    public void Capture_EverySurfaceHasValidStatus()
    {
        var options = new PalLlmOptions();
        string[] validStatuses = ["never-leaves", "only-with-opt-in", "leaves-by-default"];

        PrivacyPosture posture = PrivacyPostureBuilder.Capture(options);

        foreach (PrivacySurface s in posture.Surfaces)
        {
            Assert.That(validStatuses, Does.Contain(s.Status),
                $"Surface {s.Id} has invalid status '{s.Status}'.");
            Assert.That(s.Description, Is.Not.Empty,
                $"Surface {s.Id} must have a description.");
        }
    }

    [Test]
    public void Capture_CountsAddUpToSurfaceCount()
    {
        var options = new PalLlmOptions();

        PrivacyPosture posture = PrivacyPostureBuilder.Capture(options);

        int total = posture.NeverLeavesCount + posture.OptInAvailableCount + posture.ActiveOutboundCount;
        Assert.That(total, Is.EqualTo(posture.Surfaces.Count),
            "Three bucket counts must partition the full surface list.");
    }

    [Test]
    public void Capture_AlwaysIncludesCoreLocalSurfaces()
    {
        var options = new PalLlmOptions();

        PrivacyPosture posture = PrivacyPostureBuilder.Capture(options);
        var ids = posture.Surfaces.Select(s => s.Id).ToHashSet();

        // Pin the ship-critical "never leaves" guarantees — these must
        // always be in the posture so a future pass can't silently drop
        // them from the public inventory.
        Assert.That(ids, Does.Contain("conversation-memory"));
        Assert.That(ids, Does.Contain("dashboard"));
        Assert.That(ids, Does.Contain("health-probes"));
        Assert.That(ids, Does.Contain("proof-packets"));
        Assert.That(ids, Does.Contain("deterministic-fallback"));
        Assert.That(ids, Does.Contain("crash-reports"));
        Assert.That(ids, Does.Contain("update-check"));
        Assert.That(ids, Does.Contain("analytics"));
    }

    [Test]
    public void CaptureCached_ReturnsSameInstanceWithinTtl()
    {
        PrivacyPostureBuilder.InvalidateCache();
        var options = new PalLlmOptions();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        PrivacyPosture a = PrivacyPostureBuilder.CaptureCached(options, ttl);
        PrivacyPosture b = PrivacyPostureBuilder.CaptureCached(options, ttl);

        Assert.That(b.CapturedAtUtc, Is.EqualTo(a.CapturedAtUtc),
            "Within the TTL the cache must hand back the same snapshot instance.");
    }

    [Test]
    public void CaptureCached_InvalidatesWhenInferenceEnabledFlips()
    {
        PrivacyPostureBuilder.InvalidateCache();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        var optionsOff = new PalLlmOptions
        {
            Inference = new InferenceOptions { Enabled = false },
        };
        var optionsOn = new PalLlmOptions
        {
            Inference = new InferenceOptions { Enabled = true, BaseUrl = "http://127.0.0.1:11434/v1/" },
        };

        PrivacyPosture a = PrivacyPostureBuilder.CaptureCached(optionsOff, ttl);
        PrivacyPosture b = PrivacyPostureBuilder.CaptureCached(optionsOn, ttl);

        Assert.That(a.ActiveOutboundCount, Is.EqualTo(0),
            "Off-by-default install should have zero active outbound surfaces.");
        Assert.That(b.ActiveOutboundCount, Is.GreaterThanOrEqualTo(1),
            "Toggling Inference.Enabled must bypass the cache (signature changed) and recompute.");
    }

    [Test]
    public void InvalidateCache_ForcesRecomputeOnNextCaptureCached()
    {
        var options = new PalLlmOptions();
        TimeSpan ttl = TimeSpan.FromMinutes(5);
        PrivacyPosture first = PrivacyPostureBuilder.CaptureCached(options, ttl);

        PrivacyPostureBuilder.InvalidateCache();
        Thread.Sleep(2);
        PrivacyPosture second = PrivacyPostureBuilder.CaptureCached(options, ttl);

        Assert.That(second.CapturedAtUtc, Is.GreaterThan(first.CapturedAtUtc),
            "After InvalidateCache, CaptureCached must produce a freshly-timestamped posture.");
    }
}
