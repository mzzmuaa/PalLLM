using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers the opt-in ThermalGate behavior documented by the feature catalog
/// entry `thermal-gate` and referenced from the inference pipeline. These
/// tests pin the four user-facing contracts:
///
/// 1. Sensor unavailable -> Allow (gate is always safe to leave enabled).
/// 2. Temperature under WarnAboveC -> Allow ("GPU within thermal budget").
/// 3. WarnAboveC &lt;= Temperature &lt; RejectAboveC -> Warn (surface only,
///    does not block).
/// 4. Temperature &gt;= RejectAboveC -> Reject (route to fallback).
/// 5. Cached reads are reused within CacheTtl; expired cache resamples.
/// </summary>
public sealed class ThermalGateTests
{
    private static ThermalSample Sample(double? tempC, string source = "test") =>
        new(tempC, source, DateTimeOffset.UtcNow);

    [Test]
    public void Evaluate_NoSensor_ReturnsAllowWithUnavailableReason()
    {
        var clock = new TestClock();
        var gate = new ThermalGate(() => Sample(null, "unavailable"), () => clock.Now)
        {
            WarnAboveC = 78.0,
            RejectAboveC = 83.0,
        };

        ThermalGateResult result = gate.Evaluate();

        Assert.That(result.Decision, Is.EqualTo(ThermalGateDecision.Allow));
        Assert.That(result.Sample.TemperatureC, Is.Null);
        Assert.That(result.Sample.Source, Is.EqualTo("unavailable"));
        Assert.That(result.Reason, Does.Contain("sensor unavailable"));
    }

    [Test]
    public void Evaluate_UnderWarnThreshold_ReturnsAllow()
    {
        var clock = new TestClock();
        var gate = new ThermalGate(() => Sample(62.0), () => clock.Now)
        {
            WarnAboveC = 78.0,
            RejectAboveC = 83.0,
        };

        ThermalGateResult result = gate.Evaluate();

        Assert.That(result.Decision, Is.EqualTo(ThermalGateDecision.Allow));
        Assert.That(result.Sample.TemperatureC, Is.EqualTo(62.0));
    }

    [Test]
    public void Evaluate_BetweenWarnAndReject_ReturnsWarn()
    {
        var clock = new TestClock();
        var gate = new ThermalGate(() => Sample(80.0), () => clock.Now)
        {
            WarnAboveC = 78.0,
            RejectAboveC = 83.0,
        };

        ThermalGateResult result = gate.Evaluate();

        Assert.That(result.Decision, Is.EqualTo(ThermalGateDecision.Warn));
        Assert.That(result.Reason, Does.Contain("warn threshold"));
    }

    [Test]
    public void Evaluate_AtOrAboveRejectThreshold_ReturnsReject()
    {
        var clock = new TestClock();
        var gate = new ThermalGate(() => Sample(85.5), () => clock.Now)
        {
            WarnAboveC = 78.0,
            RejectAboveC = 83.0,
        };

        ThermalGateResult result = gate.Evaluate();

        Assert.That(result.Decision, Is.EqualTo(ThermalGateDecision.Reject));
        Assert.That(result.Reason, Does.Contain("reject threshold"));
    }

    [Test]
    public void Evaluate_WithinCacheTtl_DoesNotResample()
    {
        var clock = new TestClock();
        int sampleCalls = 0;
        var gate = new ThermalGate(
            () => { sampleCalls++; return Sample(60.0); },
            () => clock.Now)
        {
            CacheTtl = TimeSpan.FromSeconds(5),
        };

        _ = gate.Evaluate();
        _ = gate.Evaluate();
        clock.Advance(TimeSpan.FromSeconds(2));
        _ = gate.Evaluate();

        Assert.That(sampleCalls, Is.EqualTo(1),
            "Reads within the cache TTL must reuse the last sample.");
    }

    [Test]
    public void Evaluate_AfterCacheTtlExpires_Resamples()
    {
        var clock = new TestClock();
        int sampleCalls = 0;
        var gate = new ThermalGate(
            () => { sampleCalls++; return Sample(60.0); },
            () => clock.Now)
        {
            CacheTtl = TimeSpan.FromSeconds(5),
        };

        _ = gate.Evaluate();
        clock.Advance(TimeSpan.FromSeconds(6));
        _ = gate.Evaluate();

        Assert.That(sampleCalls, Is.EqualTo(2),
            "Reads after the cache TTL must refresh the sensor.");
    }

    [Test]
    public void Evaluate_SamplerThrows_FallsBackToUnavailable()
    {
        var clock = new TestClock();
        var gate = new ThermalGate(() => throw new InvalidOperationException("sensor crashed"), () => clock.Now);

        ThermalGateResult result = gate.Evaluate();

        Assert.That(result.Decision, Is.EqualTo(ThermalGateDecision.Allow),
            "A crashing sensor must not lock out live inference.");
        Assert.That(result.Sample.Source, Is.EqualTo("unavailable"));
    }

    [Test]
    public void InvalidateCache_ForcesResampleOnNextEvaluate()
    {
        var clock = new TestClock();
        int sampleCalls = 0;
        var gate = new ThermalGate(
            () => { sampleCalls++; return Sample(60.0); },
            () => clock.Now)
        {
            CacheTtl = TimeSpan.FromSeconds(60),
        };

        _ = gate.Evaluate();
        _ = gate.Evaluate();
        gate.InvalidateCache();
        _ = gate.Evaluate();

        Assert.That(sampleCalls, Is.EqualTo(2),
            "InvalidateCache must force the next Evaluate to re-read the sensor.");
    }

    [Test]
    public void DefaultSampler_HonorsEnvOverride()
    {
        string? original = Environment.GetEnvironmentVariable(ThermalGate.TestOverrideEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(ThermalGate.TestOverrideEnvVar, "91.5");

            ThermalSample s = ThermalGate.DefaultSampler();

            Assert.That(s.TemperatureC, Is.EqualTo(91.5));
            Assert.That(s.Source, Is.EqualTo("env_override"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ThermalGate.TestOverrideEnvVar, original);
        }
    }

    [Test]
    public void ParseHottestTemperature_IgnoresNoiseAndReturnsHighestValue()
    {
        double? parsed = ThermalGate.ParseHottestTemperature(
            """
            71
            not-a-number
             84.5
            79
            """);

        Assert.That(parsed, Is.EqualTo(84.5));
    }

    private sealed class TestClock
    {
        public DateTimeOffset Now { get; private set; } = new DateTimeOffset(2026, 4, 23, 0, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan t)
        {
            Now = Now + t;
        }
    }
}
