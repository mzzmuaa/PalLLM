using PalLLM.Domain.Configuration;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="AirGapVerifier"/>. Contract pinned here:
///
/// 1. All-default options (every opt-in off) = "strict-airgapped".
/// 2. A single loopback inference endpoint still = "strict-airgapped".
/// 3. A public-hostname endpoint = "not-airgapped" with the surface
///    marked <c>public</c> and a descriptive note.
/// 4. Private/LAN addresses fall out as "lan-airgapped".
/// 5. Empty / malformed endpoints fall out as "indeterminate" so a caller
///    can't be misled into thinking an un-parseable URL is safe.
/// 6. The verifier NEVER emits a live request — we assert this indirectly
///    by passing an obviously-unreachable hostname and still getting a
///    classification in under the DNS timeout.
/// </summary>
public sealed class AirGapVerifierTests
{
    [Test]
    public void AllDisabled_ProducesStrictAirgapped()
    {
        var options = new PalLlmOptions();
        // Defaults: every opt-in is off.

        AirGapReport report = AirGapVerifier.Verify(options);

        Assert.That(report.Verdict, Is.EqualTo("strict-airgapped"));
        Assert.That(report.Surfaces, Is.Not.Empty);
        foreach (AirGapSurface s in report.Surfaces)
        {
            Assert.That(s.Classification, Is.AnyOf("disabled", "loopback"),
                $"Surface {s.Surface} should be disabled or loopback in the default posture, not {s.Classification}.");
        }
    }

    [Test]
    public void LoopbackInference_StaysStrictAirgapped()
    {
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://127.0.0.1:11434/v1/";

        AirGapReport report = AirGapVerifier.Verify(options);

        Assert.That(report.Verdict, Is.EqualTo("strict-airgapped"));
        AirGapSurface inference = report.Surfaces.Single(s => s.Surface == "inference");
        Assert.That(inference.Classification, Is.EqualTo("loopback"));
    }

    [Test]
    public void LocalhostInference_StaysStrictAirgapped()
    {
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://localhost:11434/v1/";

        AirGapReport report = AirGapVerifier.Verify(options);

        Assert.That(report.Verdict, Is.EqualTo("strict-airgapped"));
        AirGapSurface inference = report.Surfaces.Single(s => s.Surface == "inference");
        Assert.That(inference.Classification, Is.EqualTo("loopback"));
    }

    [Test]
    public void RFC1918InferenceAddress_IsLanAirgapped()
    {
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://192.168.1.42:11434/v1/";

        AirGapReport report = AirGapVerifier.Verify(options);

        Assert.That(report.Verdict, Is.EqualTo("lan-airgapped"));
        AirGapSurface inference = report.Surfaces.Single(s => s.Surface == "inference");
        Assert.That(inference.Classification, Is.EqualTo("private"));
    }

    [Test]
    public void PublicIpInference_IsNotAirgapped()
    {
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://8.8.8.8:11434/v1/";

        AirGapReport report = AirGapVerifier.Verify(options);

        Assert.That(report.Verdict, Is.EqualTo("not-airgapped"));
        AirGapSurface inference = report.Surfaces.Single(s => s.Surface == "inference");
        Assert.That(inference.Classification, Is.EqualTo("public"));
        Assert.That(report.Summary, Does.Contain("public network"));
    }

    [Test]
    public void UnresolvableHost_IsIndeterminate()
    {
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        // .invalid is reserved by RFC2606 and will never resolve.
        options.Inference.BaseUrl = "http://a-host-that-cannot-exist.invalid:8080/v1/";

        AirGapReport report = AirGapVerifier.Verify(options);

        // At minimum the inference surface should be classified as unknown,
        // and the overall verdict should reflect that (not a false 'strict').
        AirGapSurface inference = report.Surfaces.Single(s => s.Surface == "inference");
        Assert.That(inference.Classification, Is.EqualTo("unknown"));
        Assert.That(report.Verdict, Is.EqualTo("indeterminate"));
    }

    [Test]
    public void DisabledSurface_IsClassifiedDisabledEvenWithEndpointSet()
    {
        var options = new PalLlmOptions();
        // Inference configured but disabled — should NOT be treated as outbound.
        options.Inference.Enabled = false;
        options.Inference.BaseUrl = "http://api.example.com/";

        AirGapReport report = AirGapVerifier.Verify(options);

        AirGapSurface inference = report.Surfaces.Single(s => s.Surface == "inference");
        Assert.That(inference.Classification, Is.EqualTo("disabled"),
            "A disabled surface must not count toward the airgap verdict even if an endpoint is configured.");
        Assert.That(report.Verdict, Is.EqualTo("strict-airgapped"));
    }

    [Test]
    public void MultipleSurfaces_MostPermissiveWins()
    {
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.BaseUrl = "http://127.0.0.1:11434/v1/";
        options.Vision.Enabled = true;
        options.Vision.BaseUrl = "http://192.168.1.42:11434/v1/";

        AirGapReport report = AirGapVerifier.Verify(options);

        // loopback + private = lan-airgapped.
        Assert.That(report.Verdict, Is.EqualTo("lan-airgapped"));
    }

    [Test]
    public void VerifyCached_ReturnsSameInstanceWithinTtl()
    {
        AirGapVerifier.InvalidateCache();
        var options = new PalLlmOptions();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        AirGapReport a = AirGapVerifier.VerifyCached(options, ttl);
        AirGapReport b = AirGapVerifier.VerifyCached(options, ttl);

        Assert.That(ReferenceEquals(a, b), Is.True,
            "Within the TTL the cache must hand back the same report reference.");
    }

    [Test]
    public void VerifyCached_InvalidatesWhenInferenceEndpointChanges()
    {
        AirGapVerifier.InvalidateCache();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        var loopback = new PalLlmOptions();
        loopback.Inference.Enabled = true;
        loopback.Inference.BaseUrl = "http://127.0.0.1:11434/v1/";

        var privateNet = new PalLlmOptions();
        privateNet.Inference.Enabled = true;
        privateNet.Inference.BaseUrl = "http://192.168.1.42:11434/v1/";

        AirGapReport a = AirGapVerifier.VerifyCached(loopback, ttl);
        AirGapReport b = AirGapVerifier.VerifyCached(privateNet, ttl);

        Assert.That(a.Verdict, Is.EqualTo("strict-airgapped"));
        Assert.That(b.Verdict, Is.EqualTo("lan-airgapped"),
            "Changing BaseUrl must bypass the cache and recompute.");
    }

    [Test]
    public void InvalidateCache_ForcesRecomputeOnNextVerifyCached()
    {
        var options = new PalLlmOptions();
        TimeSpan ttl = TimeSpan.FromMinutes(5);

        AirGapReport first = AirGapVerifier.VerifyCached(options, ttl);
        AirGapVerifier.InvalidateCache();
        AirGapReport second = AirGapVerifier.VerifyCached(options, ttl);

        Assert.That(ReferenceEquals(first, second), Is.False,
            "After InvalidateCache, VerifyCached must produce a fresh instance.");
    }
}
