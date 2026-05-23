using PalLLM.Domain.Configuration;

namespace PalLLM.Tests;

/// <summary>
/// Pass 354 — exhaustive coverage for the startup auth-posture
/// guard. The contract is intentionally simple but load-bearing:
/// any non-loopback bind URL combined with a null/empty
/// <c>PalLLM:Auth:ApiKey</c> must produce a Fail verdict so the
/// sidecar refuses to boot. The previous shipping default
/// (`ApiKey = null`) was "safe for localhost-only" by documentation
/// but unenforced by code — an operator who flipped
/// `ASPNETCORE_URLS` to <c>0.0.0.0</c> got an unauthenticated
/// network endpoint with no startup warning.
/// </summary>
public sealed class StartupAuthGuardTests
{
    // ---------- Loopback variants must Pass or Warn ----------

    [TestCase("http://127.0.0.1:5088")]
    [TestCase("http://localhost:5088")]
    [TestCase("http://[::1]:5088")]
    [TestCase("https://127.0.0.1:5443")]
    [TestCase("http://127.0.0.5:5088")]   // 127.0.0.0/8 is the full loopback range
    public void Inspect_LoopbackBind_WithNullKey_Warns(string url)
    {
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(new[] { url }, apiKey: null);

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Warn),
            $"Loopback bind '{url}' + null ApiKey must Warn (dev-mode posture; safe to boot but log loudly).");
        Assert.That(result.OffendingUrls, Is.Empty,
            "Warn verdict must not list offending URLs (there isn't a footgun to call out).");
        Assert.That(result.RemediationHint, Does.Contain("PalLLM:Auth:ApiKey").Or.Contain("PalLLM__Auth__ApiKey"),
            "Warn remediation must name the config key the operator should set before exposing the port.");
    }

    [TestCase("http://127.0.0.1:5088")]
    [TestCase("http://localhost:5088")]
    [TestCase("http://[::1]:5088")]
    public void Inspect_LoopbackBind_WithKey_Passes(string url)
    {
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(new[] { url }, apiKey: "some-bearer-token");

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Pass),
            $"Loopback bind '{url}' + non-empty ApiKey must Pass.");
    }

    // ---------- Non-loopback variants without auth must Fail ----------

    [TestCase("http://0.0.0.0:5088")]
    [TestCase("http://[::]:5088")]
    [TestCase("http://192.168.1.10:5088")]
    [TestCase("http://10.0.0.5:5088")]
    [TestCase("http://172.16.5.5:5088")]
    [TestCase("https://palllm.example.com:5088")]
    [TestCase("http://*:5088")]
    [TestCase("http://+:5088")]
    public void Inspect_NonLoopbackBind_WithNullKey_Fails(string url)
    {
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(new[] { url }, apiKey: null);

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Fail),
            $"Non-loopback bind '{url}' + null ApiKey MUST Fail. Without this guard, the sidecar would expose every /api/* route and the dashboard to anything that can reach the bind interface.");
        Assert.That(result.OffendingUrls, Has.Member(url).Or.Some.Contains(url),
            "Fail verdict must list the offending URL(s) so the operator sees exactly which bind is the problem.");
        Assert.That(result.RemediationHint, Does.Contain("ASPNETCORE_URLS").Or.Contain("ApiKey"),
            "Fail remediation must offer the two corrective paths: set the key OR restrict the bind.");
    }

    [TestCase("http://0.0.0.0:5088")]
    [TestCase("http://192.168.1.10:5088")]
    [TestCase("https://palllm.example.com:5088")]
    [TestCase("http://*:5088")]
    public void Inspect_NonLoopbackBind_WithKey_Passes(string url)
    {
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(new[] { url }, apiKey: "deployed-bearer-token");

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Pass),
            $"Non-loopback bind '{url}' + non-empty ApiKey is the intended LAN/public posture — Pass.");
    }

    // ---------- Empty / whitespace ApiKey is treated as null ----------

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\t\n")]
    public void Inspect_NonLoopbackBind_WithWhitespaceKey_Fails(string apiKey)
    {
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(
            new[] { "http://0.0.0.0:5088" },
            apiKey);

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Fail),
            "Whitespace-only ApiKey must be treated as null/empty — the guard cannot let an operator paste-by-accident a blank into the config and call it 'authenticated'.");
    }

    // ---------- Mixed loopback + non-loopback bind ----------

    [Test]
    public void Inspect_MixedLoopbackAndNonLoopbackBind_WithoutKey_Fails()
    {
        // Operators sometimes bind both 127.0.0.1 AND 0.0.0.0 for
        // testing. As long as ONE non-loopback URL is present and
        // ApiKey is null, the guard must Fail — the exposure surface
        // is identical to the all-public case.
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(
            new[] { "http://127.0.0.1:5088", "http://0.0.0.0:5088" },
            apiKey: null);

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Fail));
        Assert.That(result.OffendingUrls, Has.Some.Contains("0.0.0.0"),
            "Mixed-bind Fail must call out the non-loopback URL specifically.");
        Assert.That(result.OffendingUrls, Has.None.Contains("127.0.0.1"),
            "The loopback URL is not the offender — only the non-loopback one belongs in OffendingUrls.");
    }

    // ---------- Empty / null URL collection ----------

    [Test]
    public void Inspect_NullBindUrls_WithoutKey_Warns()
    {
        // Kestrel falls back to its loopback default when no URLs
        // are configured. Same equivalence class as "loopback only".
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(bindUrls: null, apiKey: null);

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Warn));
    }

    [Test]
    public void Inspect_EmptyBindUrls_WithKey_Passes()
    {
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(
            Array.Empty<string>(),
            apiKey: "bearer");

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Pass));
    }

    // ---------- IsLoopbackBind helper directly ----------

    [TestCase("http://127.0.0.1:5088", true)]
    [TestCase("http://localhost:5088", true)]
    [TestCase("http://[::1]:5088", true)]
    [TestCase("https://127.0.0.5:5088", true)]
    [TestCase("http://0.0.0.0:5088", false)]
    [TestCase("http://[::]:5088", false)]
    [TestCase("http://192.168.1.1:5088", false)]
    [TestCase("https://palllm.example.com", false)]
    [TestCase("http://*:5088", false)]
    [TestCase("http://+:5088", false)]
    [TestCase("not a url", false)]
    [TestCase("", false)]
    public void IsLoopbackBind_Classifies(string url, bool expected)
    {
        Assert.That(StartupAuthGuard.IsLoopbackBind(url), Is.EqualTo(expected),
            $"IsLoopbackBind('{url}') misclassified.");
    }

    // ---------- Whitespace / null URL entries are skipped ----------

    [Test]
    public void Inspect_WhitespaceBindEntries_Ignored()
    {
        // Operators occasionally end up with empty strings in their
        // URL list (config-merge artifacts). The guard must skip
        // them, not treat them as bind targets.
        StartupAuthGuard.Result result = StartupAuthGuard.Inspect(
            new[] { "", "   ", "http://127.0.0.1:5088" },
            apiKey: null);

        Assert.That(result.Action, Is.EqualTo(StartupAuthGuard.Verdict.Warn),
            "Whitespace-only URL entries must be skipped, leaving only the loopback bind for the verdict.");
    }
}
