using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

public readonly record struct ResolvedInferenceResidency(
    string ProviderId,
    int TtlSeconds,
    bool SupportsChatCompletionsTtl,
    bool SupportsNativeWarmupKeepAlive);

internal static class InferenceResidencyPolicy
{
    public static ResolvedInferenceResidency Resolve(InferenceOptions inference)
    {
        ArgumentNullException.ThrowIfNull(inference);

        int ttlSeconds = Math.Max(0, inference.ResidencyTtlSeconds);
        InferenceResidencyProvider provider = inference.ResidencyProvider switch
        {
            InferenceResidencyProvider.Auto => DetectProvider(inference.BaseUrl),
            _ => inference.ResidencyProvider,
        };

        return provider switch
        {
            InferenceResidencyProvider.LmStudio => new(
                ProviderId: "lmstudio",
                TtlSeconds: ttlSeconds,
                SupportsChatCompletionsTtl: true,
                SupportsNativeWarmupKeepAlive: false),
            _ => new(
                ProviderId: "none",
                TtlSeconds: ttlSeconds,
                SupportsChatCompletionsTtl: false,
                SupportsNativeWarmupKeepAlive: false),
        };
    }

    public static string DescribeHint(ResolvedInferenceResidency residency) =>
        residency.ProviderId switch
        {
            "lmstudio" when residency.TtlSeconds > 0 => $"ttl={residency.TtlSeconds}",
            _ => string.Empty,
        };

    // Pass 346: Ollama detection branch removed (port 11434 / host-substring
    // match no longer routes to InferenceResidencyProvider.Ollama because
    // the enum value itself was deleted). llama-server (PalLLM's bundled
    // default) doesn't need active residency hints — it keeps the loaded
    // model resident for the lifetime of the server process. LM Studio
    // detection still routes to its per-request `ttl` lane.
    private static InferenceResidencyProvider DetectProvider(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? uri))
        {
            return InferenceResidencyProvider.Disabled;
        }

        string host = uri.Host ?? string.Empty;
        if (host.Contains("lmstudio", StringComparison.OrdinalIgnoreCase)
            || uri.Port == 1234)
        {
            return InferenceResidencyProvider.LmStudio;
        }

        return InferenceResidencyProvider.Disabled;
    }
}
