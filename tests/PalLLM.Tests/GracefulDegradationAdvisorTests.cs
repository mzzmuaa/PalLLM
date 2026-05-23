using NUnit.Framework;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 33 / D2 — regression coverage for
/// <see cref="GracefulDegradationAdvisor"/>. Pinned contract:
/// <list type="bullet">
///   <item>CPU-only + Constrained tier → CpuOnlyConstrained posture with disable-vision / disable-tts hints.</item>
///   <item>CPU-only + higher RAM → CpuOnlyCapable with softer hints.</item>
///   <item>Generous tier → NoDegradation posture.</item>
///   <item>Every posture returns a non-empty Recommendations list.</item>
///   <item>Verb strings are from the fixed set keep/disable/review/opt-in/leave-off.</item>
/// </list>
/// </summary>
[TestFixture]
public class GracefulDegradationAdvisorTests
{
    private static readonly string[] AllowedActions =
        ["keep", "disable", "review", "opt-in", "leave-off"];

    [Test]
    public void Recommend_CpuOnlyConstrained_RecommendsDeterministicFirst()
    {
        var profile = Build(cores: 4, ramGb: 8, gpu: false, detectedTier: DuoHardwareTier.Constrained);
        var options = new PalLlmOptions
        {
            Inference = new InferenceOptions { Enabled = true },
            Vision = new VisionOptions { Enabled = true },
            Tts = new TtsOptions { Enabled = true },
        };

        DegradationAdvisory advisory = GracefulDegradationAdvisor.Recommend(profile, options);

        Assert.That(advisory.Posture, Is.EqualTo("CpuOnlyConstrained"));
        Assert.That(advisory.Recommendations.Any(r => r.Id == "vision-off" && r.Action == "disable"), Is.True,
            "CPU-only constrained must recommend disabling an enabled vision surface.");
        Assert.That(advisory.Recommendations.Any(r => r.Id == "tts-off" && r.Action == "disable"), Is.True,
            "CPU-only constrained must recommend disabling an enabled TTS surface.");
        Assert.That(advisory.Recommendations.Any(r => r.Id == "keep-fallback-on"), Is.True,
            "Deterministic fallback must be preserved in the recommendation set.");
    }

    [Test]
    public void Recommend_GenerousGpu_ReportsNoDegradation()
    {
        var profile = Build(cores: 24, ramGb: 64, gpu: true, detectedTier: DuoHardwareTier.Generous);
        var options = new PalLlmOptions();

        DegradationAdvisory advisory = GracefulDegradationAdvisor.Recommend(profile, options);

        Assert.That(advisory.Posture, Is.EqualTo("NoDegradation"));
        Assert.That(advisory.Headline, Does.Contain("Multi-GPU").Or.Contain("workstation"));
    }

    [Test]
    public void Recommend_EveryPosture_HasAtLeastOneRecommendation()
    {
        var options = new PalLlmOptions();
        var profiles = new[]
        {
            Build(4, 8, false, DuoHardwareTier.Constrained),
            Build(8, 32, false, DuoHardwareTier.Constrained),
            Build(8, 16, true, DuoHardwareTier.Constrained),
            Build(12, 32, true, DuoHardwareTier.Standard),
            Build(24, 64, true, DuoHardwareTier.Generous),
        };

        foreach (var profile in profiles)
        {
            DegradationAdvisory advisory = GracefulDegradationAdvisor.Recommend(profile, options);
            Assert.That(advisory.Recommendations, Is.Not.Empty,
                $"Profile {profile.EffectiveTier} / gpu={profile.GpuLikelyPresent} produced an empty recommendation list.");
            foreach (DegradationHint hint in advisory.Recommendations)
            {
                Assert.That(AllowedActions, Does.Contain(hint.Action),
                    $"Recommendation {hint.Id} uses a non-standard verb '{hint.Action}'.");
                Assert.That(hint.Detail, Is.Not.Empty,
                    $"Recommendation {hint.Id} must have a detail string.");
            }
        }
    }

    [Test]
    public void Recommend_CpuOnlyCapable_LeavesOptionsAloneWithoutDisablingFeatures()
    {
        var profile = Build(cores: 8, ramGb: 32, gpu: false, detectedTier: DuoHardwareTier.Standard);
        var options = new PalLlmOptions
        {
            Inference = new InferenceOptions { Enabled = true },
            Vision = new VisionOptions { Enabled = true },
        };

        DegradationAdvisory advisory = GracefulDegradationAdvisor.Recommend(profile, options);

        // No-gpu + not-Constrained => CpuOnlyCapable (even when detected tier says Standard,
        // the cpu-only branch intercepts before the standard branch).
        Assert.That(advisory.Posture, Is.EqualTo("CpuOnlyCapable"));
        Assert.That(advisory.Recommendations.Any(r => r.Action == "disable"), Is.False,
            "CpuOnlyCapable is the 'careful but fine' posture — no hard disables.");
    }

    private static HardwareProfile Build(int cores, int ramGb, bool gpu, DuoHardwareTier detectedTier) =>
        new(
            OperatingSystem: "linux",
            LogicalCoreCount: cores,
            PhysicalRamGigabytes: ramGb,
            GpuLikelyPresent: gpu,
            GpuDetectionDetail: gpu ? "test-marker" : "no-gpu-markers-detected",
            GpuArchitecture: gpu ? "unknown" : "none",
            GpuArchitectureDetail: "test-fixture",
            Fp4TensorCoresLikely: false,
            RecommendedQuantization: gpu ? "q4_k_m" : "q4_k_m",
            DetectedTier: detectedTier.ToString(),
            EffectiveTier: detectedTier.ToString(),
            OverrideApplied: false,
            DetectionConfidence: "high",
            Recommendation: "test",
            CapturedAtUtc: DateTimeOffset.UtcNow);
}
