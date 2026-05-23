using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 33 / D2 — deterministic advisor that inspects the current
/// <see cref="HardwareProfile"/> + <see cref="PalLlmOptions"/> and
/// recommends a graceful-degradation posture for boxes that cannot
/// comfortably run the full inference / vision / TTS pipeline.
///
/// <para>Covers the "my laptop has no GPU, can I still play?" case:
/// deterministic director + small Edge model stay available, vision
/// + TTS are recommended off, and the active model lane is nudged
/// toward the smallest available tier. All recommendations are
/// advisory — the advisor never mutates options itself. Pairs with
/// the Pass-25 <see cref="HardwareProfiler"/>: HardwareProfiler
/// reports what's on the box, this advisor reports what to DO about
/// it.</para>
///
/// <para>Pure function. No side effects. Safe on hot paths.</para>
/// </summary>
public static class GracefulDegradationAdvisor
{
    /// <summary>
    /// Compute degradation advisory for the given box + options.
    /// </summary>
    public static DegradationAdvisory Recommend(HardwareProfile profile, PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);

        var recommendations = new List<DegradationHint>();
        DegradationPosture posture;
        string headline;

        bool cpuOnly = !profile.GpuLikelyPresent;
        bool constrained = string.Equals(profile.EffectiveTier, nameof(DuoHardwareTier.Constrained), StringComparison.Ordinal);
        bool generous = string.Equals(profile.EffectiveTier, nameof(DuoHardwareTier.Generous), StringComparison.Ordinal);

        if (cpuOnly && constrained)
        {
            posture = DegradationPosture.CpuOnlyConstrained;
            headline = $"CPU-only / {profile.PhysicalRamGigabytes} GB RAM — run PalLLM in deterministic-first mode. Vision and TTS should stay off.";
            recommendations.Add(new DegradationHint(
                "inference-small-model",
                options.Inference.Enabled ? "review" : "leave-off",
                "Pin PalLLM:Inference:Model to a 1B-class model (e.g. `gemma3:1b` or `qwen3:0.6b`). Larger models will be unusably slow without GPU."));
            recommendations.Add(new DegradationHint(
                "vision-off",
                options.Vision.Enabled ? "disable" : "leave-off",
                "Vision describe is CPU-intensive. Recommend PalLLM:Vision:Enabled=false until GPU hardware is available."));
            recommendations.Add(new DegradationHint(
                "tts-off",
                options.Tts.Enabled ? "disable" : "leave-off",
                "TTS synthesis off — the deterministic director still emits text presentation cues, just no audio."));
            recommendations.Add(new DegradationHint(
                "keep-fallback-on",
                "keep",
                "Deterministic fallback director remains the primary reply path — zero network, zero GPU, always available."));
        }
        else if (cpuOnly)
        {
            posture = DegradationPosture.CpuOnlyCapable;
            headline = $"CPU-only but {profile.PhysicalRamGigabytes} GB RAM — small-to-medium models can work, just slowly.";
            recommendations.Add(new DegradationHint(
                "inference-small-model",
                options.Inference.Enabled ? "review" : "opt-in",
                "Prefer 1-4B-class models. 7B class works with 32 GB RAM but expect 10-30 s replies."));
            recommendations.Add(new DegradationHint(
                "vision-only-on-demand",
                options.Vision.Enabled ? "review" : "leave-off",
                "Vision describe is fine on-demand via /api/vision/describe. Don't enable the screenshot watcher for auto-processing."));
            recommendations.Add(new DegradationHint(
                "tts-optional",
                "review",
                "Local Piper / Coqui CPU TTS is fine if enabled. Keep an eye on p95 latency."));
        }
        else if (constrained)
        {
            posture = DegradationPosture.GpuEntry;
            headline = $"Entry-level GPU + {profile.PhysicalRamGigabytes} GB RAM — fast lane OK, skip the Judge role.";
            recommendations.Add(new DegradationHint(
                "worker-only",
                "keep",
                "Bind only the Worker role. Judge-dependent patterns fall back to validators-only."));
            recommendations.Add(new DegradationHint(
                "thinking-off",
                "review",
                "Leave thinking-mode off for the fast-reactive profile; a tight VRAM budget spikes on think tokens."));
        }
        else if (generous)
        {
            posture = DegradationPosture.NoDegradation;
            headline = $"Multi-GPU or workstation-class box — full mesh is available. No degradation recommended.";
            recommendations.Add(new DegradationHint(
                "bind-worker-plus-judge",
                "opt-in",
                "Both Worker and Judge fit simultaneously — bind both to unlock full Duo patterns."));
            recommendations.Add(new DegradationHint(
                "enable-vision",
                "opt-in",
                "Vision describe + screenshot watcher are reasonable here."));
        }
        else
        {
            posture = DegradationPosture.Standard;
            headline = $"Single-GPU studio-class box — deliberate role binding recommended.";
            recommendations.Add(new DegradationHint(
                "worker-plus-selective-judge",
                "opt-in",
                "Worker + Edge fit; Judge runs serialised (not co-resident). Bind Judge only for audit-heavy workflows."));
            recommendations.Add(new DegradationHint(
                "vision-selective",
                "opt-in",
                "Vision describe is fine; consider disabling the screenshot watcher unless you need passive world-state extraction."));
        }

        return new DegradationAdvisory(
            Posture: posture.ToString(),
            Headline: headline,
            Recommendations: recommendations,
            CapturedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Classification the advisor returns.
    /// </summary>
    public enum DegradationPosture
    {
        /// <summary>CPU-only + low RAM — use deterministic-first posture.</summary>
        CpuOnlyConstrained,
        /// <summary>CPU-only but enough RAM for small-to-medium models.</summary>
        CpuOnlyCapable,
        /// <summary>Entry-level GPU, Worker-only recommended.</summary>
        GpuEntry,
        /// <summary>Single-GPU studio-class, deliberate role choices.</summary>
        Standard,
        /// <summary>Multi-GPU or workstation-class, no degradation needed.</summary>
        NoDegradation,
    }
}

/// <summary>
/// Structured advisory returned by <see cref="GracefulDegradationAdvisor.Recommend"/>.
/// </summary>
/// <param name="Posture">Machine-friendly posture classification (enum stringified).</param>
/// <param name="Headline">One-sentence plain-English summary.</param>
/// <param name="Recommendations">Ordered list of specific recommendations.</param>
/// <param name="CapturedAtUtc">When the advisory was captured (UTC).</param>
public sealed record DegradationAdvisory(
    string Posture,
    string Headline,
    IReadOnlyList<DegradationHint> Recommendations,
    DateTimeOffset CapturedAtUtc);

/// <summary>
/// One recommendation inside a <see cref="DegradationAdvisory"/>.
/// </summary>
/// <param name="Id">Short kebab-case id.</param>
/// <param name="Action">Action verb: "keep" / "disable" / "review" / "opt-in" / "leave-off".</param>
/// <param name="Detail">Plain-English detail explaining why.</param>
public sealed record DegradationHint(
    string Id,
    string Action,
    string Detail);
