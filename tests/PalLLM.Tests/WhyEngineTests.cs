using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="WhyEngine"/>. Contract pinned here:
///
/// 1. Every call returns a <see cref="WhyAnswer"/> with non-empty
///    <see cref="WhyAnswer.PrimaryReason"/>, at least one causal-chain
///    entry, a stable <see cref="WhyAnswer.Intent"/> string, and a
///    confidence tag. The engine never throws.
/// 2. Keyword-based intent classification correctly routes common
///    questions ("fallback", "bridge", "circuit", "health", "rate limit",
///    "thermal", "bypass").
/// 3. A question that doesn't match any keyword falls through to a
///    generic posture explanation so the caller always gets grounded
///    evidence instead of a "didn't understand" error.
/// 4. The engine makes no outbound network call and is safe to invoke
///    from the deterministic always-available layer.
/// </summary>
public sealed class WhyEngineTests
{
    private static RuntimeHealth Health(
        bool adapterReady = true,
        bool bridgeEnabled = true,
        bool inferenceConfigured = false,
        string circuit = "Closed",
        long rateLimited = 0) => new()
    {
        AdapterName = "Palworld (UE4SS bridge)",
        AdapterReady = adapterReady,
        BridgeEnabled = bridgeEnabled,
        Status = "Ready.",
        InferenceConfigured = inferenceConfigured,
        InferenceModel = "placeholder",
        InferenceActiveModel = "placeholder",
        VisionEnabled = false,
        TtsEnabled = false,
        AutomationEnabled = false,
        InferenceCircuitState = circuit,
        RateLimitedCount = rateLimited,
    };

    private static PalLlmMetricsSnapshot EmptyMetrics() => new(
        FallbackStrategyCounts: Array.Empty<FallbackStrategyCount>(),
        ModelTierTransitionCounts: Array.Empty<ModelTierTransitionCount>(),
        ChatLatency: new ChatLatencyHistogram(
            Count: 0,
            SumSeconds: 0,
            Buckets: Array.Empty<LatencyHistogramBucket>()));

    private static PalLlmMetricsSnapshot MetricsWithStrategy(string strategyId, long count) => new(
        FallbackStrategyCounts: new[] { new FallbackStrategyCount(strategyId, count) },
        ModelTierTransitionCounts: Array.Empty<ModelTierTransitionCount>(),
        ChatLatency: new ChatLatencyHistogram(
            Count: 0,
            SumSeconds: 0,
            Buckets: Array.Empty<LatencyHistogramBucket>()));

    [Test]
    public void Answer_FallbackQuestion_ExplainsInferenceOff()
    {
        RuntimeHealth health = Health(inferenceConfigured: false);
        OperatorHealthScore score = OperatorHealthScorer.Score(health);
        PalLlmMetricsSnapshot metrics = MetricsWithStrategy("stealth-withdraw", 3);

        WhyAnswer answer = WhyEngine.Answer("why did my reply come from the fallback?", health, metrics, score);

        Assert.That(answer.Intent, Is.EqualTo("FallbackTriggered"));
        Assert.That(answer.PrimaryReason, Is.Not.Empty);
        Assert.That(answer.CausalChain, Is.Not.Empty);
        Assert.That(answer.CausalChain.Any(line => line.Contains("Live inference is off", StringComparison.Ordinal)),
            Is.True, "Chain must mention that inference is off.");
        Assert.That(answer.CausalChain.Any(line => line.Contains("stealth-withdraw", StringComparison.Ordinal)),
            Is.True, "Chain must reference the top fallback strategy by name.");
        Assert.That(answer.EvidenceReferences, Contains.Item("RuntimeHealth.InferenceConfigured=false"));
        Assert.That(answer.Confidence, Is.EqualTo("high"));
    }

    [Test]
    public void Answer_FallbackQuestion_WithOpenCircuit_ExplainsCircuit()
    {
        RuntimeHealth health = Health(inferenceConfigured: true, circuit: "Open");
        OperatorHealthScore score = OperatorHealthScorer.Score(health);
        PalLlmMetricsSnapshot metrics = MetricsWithStrategy("general-director", 5);

        WhyAnswer answer = WhyEngine.Answer("why fallback?", health, metrics, score);

        Assert.That(answer.CausalChain.Any(line => line.Contains("circuit breaker is OPEN", StringComparison.Ordinal)),
            Is.True);
        Assert.That(answer.EvidenceReferences, Contains.Item("RuntimeHealth.InferenceCircuitState=Open"));
    }

    [Test]
    public void Answer_CircuitBreakerQuestion_DescribesCurrentState()
    {
        RuntimeHealth health = Health(inferenceConfigured: true, circuit: "HalfOpen");
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer("why is the circuit breaker half-open?", health, EmptyMetrics(), score);

        Assert.That(answer.Intent, Is.EqualTo("CircuitBreaker"));
        Assert.That(answer.PrimaryReason, Does.Contain("HALF-OPEN"));
    }

    [Test]
    public void Answer_BridgeQuestion_ExplainsAdapterNotReady()
    {
        RuntimeHealth health = Health(adapterReady: false);
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer("why isn't the bridge ready?", health, EmptyMetrics(), score);

        Assert.That(answer.Intent, Is.EqualTo("BridgeNotReady"));
        Assert.That(answer.CausalChain.Any(line => line.Contains("game adapter", StringComparison.OrdinalIgnoreCase)),
            Is.True);
        Assert.That(answer.EvidenceReferences, Contains.Item("RuntimeHealth.AdapterReady=false"));
    }

    [Test]
    public void Answer_HealthScoreQuestion_IncludesScoreAndReasons()
    {
        RuntimeHealth health = Health(adapterReady: false, bridgeEnabled: false);
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer("why is the health score so low?", health, EmptyMetrics(), score);

        Assert.That(answer.Intent, Is.EqualTo("LowHealthScore"));
        Assert.That(answer.PrimaryReason, Does.Contain(score.Score.ToString()));
        Assert.That(answer.CausalChain.Any(line => line.Contains("adapter is not ready", StringComparison.Ordinal)),
            Is.True);
    }

    [Test]
    public void Answer_RateLimitQuestion_SurfacesCount()
    {
        RuntimeHealth health = Health(rateLimited: 7);
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer("why was I rate limited?", health, EmptyMetrics(), score);

        Assert.That(answer.Intent, Is.EqualTo("RateLimited"));
        Assert.That(answer.PrimaryReason, Does.Contain("7"));
    }

    [Test]
    public void Answer_ThermalQuestion_ReturnsThermalExplanation()
    {
        RuntimeHealth health = Health();
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer("why did my request get thermal gated?", health, EmptyMetrics(), score);

        Assert.That(answer.Intent, Is.EqualTo("ThermalGate"));
        Assert.That(answer.PrimaryReason, Does.Contain("thermal"));
    }

    [Test]
    public void Answer_InferenceBypassQuestion_ExplainsBypassReasons()
    {
        RuntimeHealth health = Health(inferenceConfigured: true, rateLimited: 2);
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer("why is inference being bypassed?", health, EmptyMetrics(), score);

        Assert.That(answer.Intent, Is.EqualTo("InferenceBypassed"));
        Assert.That(answer.CausalChain.Any(line => line.Contains("rate limiter has engaged", StringComparison.Ordinal)),
            Is.True);
    }

    [Test]
    public void Answer_UnknownQuestion_FallsBackToGenericPosture()
    {
        RuntimeHealth health = Health();
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer("what flavour of ice cream do you like?", health, EmptyMetrics(), score);

        Assert.That(answer.Intent, Is.EqualTo("Unknown"));
        Assert.That(answer.CausalChain, Is.Not.Empty);
        Assert.That(answer.PrimaryReason, Does.Contain("grounded posture"));
    }

    [Test]
    public void Answer_EmptyQuestion_StillReturnsGrounded()
    {
        RuntimeHealth health = Health();
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        WhyAnswer answer = WhyEngine.Answer(null, health, EmptyMetrics(), score);

        Assert.That(answer, Is.Not.Null);
        Assert.That(answer.PrimaryReason, Is.Not.Empty);
        Assert.That(answer.CausalChain, Is.Not.Empty);
    }
}
