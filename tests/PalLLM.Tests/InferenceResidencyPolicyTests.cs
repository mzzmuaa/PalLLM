using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

// Pass 301 - direct unit tests for the inference-residency provider
// detection + hint policy. This is the helper that decides whether the
// runtime sends `ttl=N` (LM Studio chat-completions extension) when
// keeping a model resident between turns. Until this pass it was only
// covered indirectly via the model-tier orchestrator path. The enum
// branches, the `Auto` detection (host substring vs port-based), the
// malformed-URL fallback, the negative-TTL clamp, and the
// `DescribeHint` formatting had no direct fast-feedback coverage. A
// regression that, say, started emitting `ttl=` to a generic
// OpenAI-compatible endpoint would silently break warmup residency
// and shipping latency.
//
// Pass 346: Ollama-specific test cases removed alongside the
// InferenceResidencyProvider.Ollama enum value. llama-server (the new
// bundled default) keeps models resident for the lifetime of the
// server process and doesn't need an active residency hint, so the
// only provider still emitting hints is LM Studio.
public sealed class InferenceResidencyPolicyTests
{
    // ---------- Explicit provider selection ----------

    [Test]
    public void Resolve_ExplicitLmStudio_ReturnsLmStudioProviderId()
    {
        var inference = new InferenceOptions
        {
            ResidencyProvider = InferenceResidencyProvider.LmStudio,
            ResidencyTtlSeconds = 900,
        };

        var residency = InferenceResidencyPolicy.Resolve(inference);

        Assert.That(residency.ProviderId, Is.EqualTo("lmstudio"));
        Assert.That(residency.TtlSeconds, Is.EqualTo(900));
        Assert.That(residency.SupportsChatCompletionsTtl, Is.True);
        Assert.That(residency.SupportsNativeWarmupKeepAlive, Is.False);
    }

    [Test]
    public void Resolve_ExplicitDisabled_ReturnsNone()
    {
        var inference = new InferenceOptions
        {
            ResidencyProvider = InferenceResidencyProvider.Disabled,
            ResidencyTtlSeconds = 1200,
            BaseUrl = "http://lmstudio.local:1234/v1/", // explicit Disabled wins even at an auto-detectable host
        };

        var residency = InferenceResidencyPolicy.Resolve(inference);

        Assert.That(residency.ProviderId, Is.EqualTo("none"));
        Assert.That(residency.SupportsChatCompletionsTtl, Is.False);
        Assert.That(residency.SupportsNativeWarmupKeepAlive, Is.False);
    }

    // ---------- Auto detection ----------

    // Pass 346: Resolve_Auto_DetectsOllamaFromUrl deleted alongside the
    // Ollama provider removal. Endpoints that used to look like Ollama
    // (port 11434 or host containing "ollama") now fall through to
    // "none" — see Resolve_Auto_UnknownProvider_ReturnsNone below for
    // the explicit non-detection contract.

    [TestCase("http://localhost:1234/v1/")]                // LM Studio default port
    [TestCase("http://lmstudio.local/v1/")]
    [TestCase("https://LMStudio-LAN.example/v1/")]         // case-insensitive
    public void Resolve_Auto_DetectsLmStudioFromUrl(string baseUrl)
    {
        var inference = new InferenceOptions
        {
            ResidencyProvider = InferenceResidencyProvider.Auto,
            BaseUrl = baseUrl,
            ResidencyTtlSeconds = 60,
        };

        var residency = InferenceResidencyPolicy.Resolve(inference);

        Assert.That(residency.ProviderId, Is.EqualTo("lmstudio"),
            $"Expected LM Studio detection from '{baseUrl}'.");
    }

    [TestCase("http://localhost:8000/v1/")]                // vLLM
    [TestCase("http://127.0.0.1:8080/")]                   // llama.cpp default (no residency hint needed — process-resident)
    [TestCase("https://api.example.com/v1/")]              // generic OpenAI-compatible
    [TestCase("http://localhost:11434/v1/")]               // Pass 346: ex-Ollama port — must now fall through to none
    [TestCase("http://ollama-host.local/v1/")]             // Pass 346: ex-Ollama host substring — must now fall through to none
    public void Resolve_Auto_UnknownProvider_ReturnsNone(string baseUrl)
    {
        var inference = new InferenceOptions
        {
            ResidencyProvider = InferenceResidencyProvider.Auto,
            BaseUrl = baseUrl,
        };

        var residency = InferenceResidencyPolicy.Resolve(inference);

        Assert.That(residency.ProviderId, Is.EqualTo("none"),
            $"URL '{baseUrl}' should not match any residency provider.");
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not a url")]
    [TestCase("/relative/path")]
    public void Resolve_Auto_MalformedBaseUrl_ReturnsNone(string baseUrl)
    {
        var inference = new InferenceOptions
        {
            ResidencyProvider = InferenceResidencyProvider.Auto,
            BaseUrl = baseUrl,
        };

        var residency = InferenceResidencyPolicy.Resolve(inference);

        Assert.That(residency.ProviderId, Is.EqualTo("none"));
    }

    // ---------- TTL clamping ----------

    [Test]
    public void Resolve_NegativeTtl_ClampedToZero()
    {
        var inference = new InferenceOptions
        {
            ResidencyProvider = InferenceResidencyProvider.LmStudio,
            ResidencyTtlSeconds = -500,
        };

        var residency = InferenceResidencyPolicy.Resolve(inference);

        Assert.That(residency.TtlSeconds, Is.EqualTo(0),
            "Negative TTL must clamp to 0 so a misconfigured option cannot emit a nonsensical ttl hint.");
    }

    [Test]
    public void Resolve_ZeroTtl_StaysZero()
    {
        var inference = new InferenceOptions
        {
            ResidencyProvider = InferenceResidencyProvider.LmStudio,
            ResidencyTtlSeconds = 0,
        };

        var residency = InferenceResidencyPolicy.Resolve(inference);

        Assert.That(residency.TtlSeconds, Is.EqualTo(0));
    }

    // ---------- DescribeHint ----------
    //
    // Pass 346: DescribeHint_Ollama_* tests deleted alongside the Ollama
    // provider removal. The only provider id that produces a hint now
    // is "lmstudio" → `ttl=N`. Any other id falls through to empty.

    [TestCase(60, "ttl=60")]
    [TestCase(900, "ttl=900")]
    public void DescribeHint_LmStudio_PositiveTtl_EmitsTtl(int ttl, string expected)
    {
        var residency = new ResolvedInferenceResidency(
            "lmstudio", ttl, SupportsChatCompletionsTtl: true, SupportsNativeWarmupKeepAlive: false);

        Assert.That(InferenceResidencyPolicy.DescribeHint(residency), Is.EqualTo(expected));
    }

    [Test]
    public void DescribeHint_LmStudio_ZeroTtl_ReturnsEmpty()
    {
        var residency = new ResolvedInferenceResidency(
            "lmstudio", TtlSeconds: 0, SupportsChatCompletionsTtl: true, SupportsNativeWarmupKeepAlive: false);

        Assert.That(InferenceResidencyPolicy.DescribeHint(residency), Is.Empty);
    }

    [Test]
    public void DescribeHint_NoneProvider_ReturnsEmpty()
    {
        // Any non-lmstudio provider id returns empty regardless of TTL.
        var residency = new ResolvedInferenceResidency(
            "none", TtlSeconds: 600, SupportsChatCompletionsTtl: false, SupportsNativeWarmupKeepAlive: false);

        Assert.That(InferenceResidencyPolicy.DescribeHint(residency), Is.Empty);
    }

    [Test]
    public void DescribeHint_UnknownProvider_ReturnsEmpty()
    {
        // Pass 346: an explicit assertion that the formerly-Ollama
        // provider id "ollama" no longer round-trips through DescribeHint
        // — if a stale config or persisted record reintroduces the
        // string, the runtime must not emit a hint.
        var residency = new ResolvedInferenceResidency(
            "ollama", TtlSeconds: 600, SupportsChatCompletionsTtl: false, SupportsNativeWarmupKeepAlive: false);

        Assert.That(InferenceResidencyPolicy.DescribeHint(residency), Is.Empty);
    }

    // ---------- Null guard ----------

    [Test]
    public void Resolve_NullInferenceOptions_ThrowsArgumentNullException()
    {
        Assert.That(
            () => InferenceResidencyPolicy.Resolve(null!),
            Throws.ArgumentNullException);
    }
}
