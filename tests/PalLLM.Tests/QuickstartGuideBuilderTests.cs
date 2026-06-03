using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="QuickstartGuideBuilder"/>, the state-aware
/// "what should I do next?" advisor behind <c>GET /api/quickstart</c>. The
/// opt-in feature branches (vision / TTS / auth / thermal-gate / warmup) are
/// option-driven, so they're deterministic regardless of host hardware or
/// bridge state — each test asserts both sides of those guards plus the
/// overall-status / headline rollup.
/// </summary>
public sealed class QuickstartGuideBuilderTests
{
    private static QuickstartGuide BuildWith(PalLlmOptions options)
    {
        var runtime = new PalLlmRuntime(options, new NoopInferenceClient());
        return QuickstartGuideBuilder.Build(runtime, options);
    }

    private sealed class NoopInferenceClient : IInferenceClient
    {
        public Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken) =>
            Task.FromResult(InferenceResult.Disabled("disabled"));
    }

    private static bool HasStep(QuickstartGuide guide, string labelSubstring) =>
        guide.Steps.Any(s => s.Label.Contains(labelSubstring, StringComparison.OrdinalIgnoreCase));

    [Test]
    public void DefaultPosture_SurfacesEveryOptInUpgradeStep()
    {
        QuickstartGuide guide = BuildWith(new PalLlmOptions());

        Assert.That(guide.Steps, Is.Not.Empty);
        Assert.That(guide.Headline, Is.Not.Empty);
        Assert.That(guide.OverallStatus, Is.AnyOf("ready", "needs-setup", "needs-attention"));
        Assert.That(guide.OperatorHealth, Is.Not.Null);

        // Every network feature ships off, so each one surfaces its opt-in step.
        Assert.That(HasStep(guide, "vision pipeline"), Is.True);
        Assert.That(HasStep(guide, "TTS"), Is.True);
        Assert.That(HasStep(guide, "API key"), Is.True);
        Assert.That(HasStep(guide, "thermal gate"), Is.True);
        // Every step carries a complete, non-empty card.
        Assert.That(guide.Steps.All(s =>
            !string.IsNullOrWhiteSpace(s.Priority) &&
            !string.IsNullOrWhiteSpace(s.Label) &&
            !string.IsNullOrWhiteSpace(s.Why) &&
            !string.IsNullOrWhiteSpace(s.Action) &&
            !string.IsNullOrWhiteSpace(s.Verify)), Is.True);
    }

    [Test]
    public void EnabledFeatures_DropTheirOptInSteps()
    {
        var options = new PalLlmOptions();
        options.Vision.Enabled = true;
        options.Tts.Enabled = true;
        options.Auth.ApiKey = "secret-token";
        options.Inference.ThermalGate.Enabled = true;

        QuickstartGuide guide = BuildWith(options);

        Assert.That(HasStep(guide, "vision pipeline"), Is.False);
        Assert.That(HasStep(guide, "TTS"), Is.False);
        Assert.That(HasStep(guide, "API key"), Is.False);
        Assert.That(HasStep(guide, "thermal gate"), Is.False);
    }

    [Test]
    public void InferenceEnabled_WarmupStepTracksTheWarmupFlag()
    {
        var withoutWarmup = new PalLlmOptions();
        withoutWarmup.Inference.Enabled = true;
        withoutWarmup.Inference.EnableWarmup = false;
        Assert.That(HasStep(BuildWith(withoutWarmup), "warmup"), Is.True,
            "Inference on + warmup off must surface the warmup nudge.");

        var withWarmup = new PalLlmOptions();
        withWarmup.Inference.Enabled = true;
        withWarmup.Inference.EnableWarmup = true;
        Assert.That(HasStep(BuildWith(withWarmup), "warmup"), Is.False,
            "Inference on + warmup on must not surface the warmup nudge.");
    }
}
