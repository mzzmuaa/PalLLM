using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers the single-number operator happiness score. The contract these
/// tests pin:
///
/// 1. A fully-healthy runtime scores 100 / "Excellent" with no reasons.
/// 2. Each degradation signal subtracts a deterministic amount; the
///    top-3 reasons are ordered by penalty size, then alphabetically so
///    repeated polls don't flap.
/// 3. The score is clamped to [0, 100] so a pathological combination of
///    signals can't produce a negative number.
/// 4. Inference-side penalties only apply when inference is configured —
///    a fallback-only operator never sees a lower score just because
///    they chose not to enable live inference.
/// </summary>
public sealed class OperatorHealthScorerTests
{
    private static RuntimeHealth HealthyBaseline(
        bool adapterReady = true,
        bool bridgeEnabled = true,
        string status = "Ready.",
        bool inferenceConfigured = false,
        string inferenceCircuitState = "Closed",
        long inferenceSuccessCount = 0,
        long inferenceFailureCount = 0,
        long rateLimitedCount = 0) => new()
    {
        AdapterName = "Palworld (UE4SS bridge)",
        AdapterReady = adapterReady,
        BridgeEnabled = bridgeEnabled,
        Status = status,
        InferenceConfigured = inferenceConfigured,
        InferenceModel = "placeholder",
        InferenceActiveModel = "placeholder",
        VisionEnabled = false,
        TtsEnabled = false,
        AutomationEnabled = false,
        InferenceCircuitState = inferenceCircuitState,
        InferenceSuccessCount = inferenceSuccessCount,
        InferenceFailureCount = inferenceFailureCount,
        RateLimitedCount = rateLimitedCount,
    };

    [Test]
    public void HealthyBaseline_ScoresExcellent()
    {
        OperatorHealthScore s = OperatorHealthScorer.Score(HealthyBaseline());

        Assert.That(s.Score, Is.EqualTo(100));
        Assert.That(s.Grade, Is.EqualTo("Excellent"));
        Assert.That(s.TopReasons, Is.Empty);
        Assert.That(s.Summary, Is.Not.Empty);
    }

    [Test]
    public void AdapterNotReady_DeductsAndShowsReason()
    {
        RuntimeHealth h = HealthyBaseline(adapterReady: false);

        OperatorHealthScore s = OperatorHealthScorer.Score(h);

        Assert.That(s.Score, Is.EqualTo(80));
        Assert.That(s.Grade, Is.EqualTo("Good"));
        Assert.That(s.TopReasons[0], Does.Contain("adapter is not ready"));
    }

    [Test]
    public void InferenceCircuitOpen_OnlyDeductsWhenInferenceConfigured()
    {
        // When inference isn't configured, the circuit state shouldn't count.
        RuntimeHealth fallbackOnly = HealthyBaseline(inferenceCircuitState: "Open");
        OperatorHealthScore noInference = OperatorHealthScorer.Score(fallbackOnly);
        Assert.That(noInference.Score, Is.EqualTo(100),
            "Fallback-only operators shouldn't be penalised for inference state.");

        // When inference IS configured and breaker is open, -15.
        RuntimeHealth withInference = HealthyBaseline(inferenceConfigured: true, inferenceCircuitState: "Open");
        OperatorHealthScore open = OperatorHealthScorer.Score(withInference);
        Assert.That(open.Score, Is.EqualTo(85));
        Assert.That(open.TopReasons[0], Does.Contain("circuit breaker is OPEN"));
    }

    [Test]
    public void HighInferenceFailureRate_ShowsInReasons()
    {
        RuntimeHealth h = HealthyBaseline(
            inferenceConfigured: true,
            inferenceSuccessCount: 5,
            inferenceFailureCount: 5); // 50% failure, 10 total

        OperatorHealthScore s = OperatorHealthScorer.Score(h);

        Assert.That(s.Score, Is.EqualTo(85),
            "50% failure on 10 samples is above the 25% threshold — expect -15.");
        Assert.That(s.TopReasons[0], Does.Contain("failure rate is high"));
    }

    [Test]
    public void LowSampleCount_DoesNotPenaliseFailureRate()
    {
        // 3 failures in 3 attempts (100% failure) but only 3 samples total —
        // too few to call a trend. Score should not move on failure-rate.
        RuntimeHealth h = HealthyBaseline(
            inferenceConfigured: true,
            inferenceSuccessCount: 0,
            inferenceFailureCount: 3);

        OperatorHealthScore s = OperatorHealthScorer.Score(h);

        Assert.That(s.Score, Is.EqualTo(100),
            "Small sample sizes should not penalise the operator for noise.");
    }

    [Test]
    public void TopReasonsOrderedByPenalty()
    {
        RuntimeHealth h = HealthyBaseline(
            adapterReady: false,        // -20
            bridgeEnabled: false,       // -10
            rateLimitedCount: 3);       // -5

        OperatorHealthScore s = OperatorHealthScorer.Score(h);

        Assert.That(s.Score, Is.EqualTo(65));
        Assert.That(s.Grade, Is.EqualTo("Degraded"));
        Assert.That(s.TopReasons, Has.Count.EqualTo(3));
        // Highest penalty first.
        Assert.That(s.TopReasons[0], Does.Contain("adapter is not ready"));
        Assert.That(s.TopReasons[1], Does.Contain("Bridge is disabled"));
        Assert.That(s.TopReasons[2], Does.Contain("Rate limiter engaged"));
    }

    [Test]
    public void ScoreClampsToZero()
    {
        // Stack every deduction the scorer knows about.
        RuntimeHealth h = HealthyBaseline(
            adapterReady: false,           // -20
            bridgeEnabled: false,          // -10
            status: "",                    // -5
            inferenceConfigured: true,
            inferenceCircuitState: "Open", // -15
            inferenceSuccessCount: 10,
            inferenceFailureCount: 90,     // -15 (>=25%)
            rateLimitedCount: 100);         // -5

        OperatorHealthScore s = OperatorHealthScorer.Score(h);

        Assert.That(s.Score, Is.GreaterThanOrEqualTo(0));
        Assert.That(s.Score, Is.LessThanOrEqualTo(100));
        Assert.That(s.Grade, Is.EqualTo("Critical").Or.EqualTo("Degraded"));
    }

    [Test]
    public void Grade_BoundariesMatchDocumentedRanges()
    {
        // Only the adapter-not-ready signal -> -20 -> 80 -> Good
        Assert.That(OperatorHealthScorer.Score(HealthyBaseline(adapterReady: false)).Grade,
            Is.EqualTo("Good"));

        // Adapter + bridge + rate-limited + HalfOpen circuit = 60 -> Degraded
        OperatorHealthScore mid = OperatorHealthScorer.Score(HealthyBaseline(
            adapterReady: false,
            bridgeEnabled: false,
            inferenceConfigured: true,
            inferenceCircuitState: "HalfOpen",
            rateLimitedCount: 1));
        Assert.That(mid.Grade, Is.EqualTo("Degraded"));

        // Healthy baseline -> Excellent
        Assert.That(OperatorHealthScorer.Score(HealthyBaseline()).Grade, Is.EqualTo("Excellent"));
    }
}
