using System.Security.Cryptography;
using System.Text.Json;

namespace PalLLM.Sidecar;

internal static class ConditionalHttp
{
    private static readonly JsonSerializerOptions FingerprintJsonOptions = CreateFingerprintJsonOptions();

    private static JsonSerializerOptions CreateFingerprintJsonOptions()
    {
        JsonSerializerOptions options = PalLlmJsonOptions.Create(static serializerOptions =>
        {
            serializerOptions.PropertyNamingPolicy = null;
        });

        return options;
    }

    public static string CreateStrongEtag<T>(T payload)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, FingerprintJsonOptions);
        byte[] hash = SHA256.HashData(json);
        return $"\"{Convert.ToHexString(hash)}\"";
    }

    public static void ApplyPrivateCaching(HttpContext context, string etag, TimeSpan? maxAge)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);

        context.Response.Headers.ETag = etag;

        if (maxAge.HasValue)
        {
            context.Response.Headers.CacheControl = $"private, max-age={(int)Math.Max(0, Math.Floor(maxAge.Value.TotalSeconds))}";
        }
        else
        {
            context.Response.Headers.CacheControl = "private, no-cache, must-revalidate";
        }
    }

    public static bool RequestMatchesEtag(HttpRequest request, string currentEtag)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentEtag);

        string header = request.Headers.IfNoneMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        foreach (string rawCandidate in header.Split(','))
        {
            string candidate = rawCandidate.Trim();
            if (candidate.Length == 0)
            {
                continue;
            }

            if (candidate == "*")
            {
                return true;
            }

            if (string.Equals(candidate, currentEtag, StringComparison.Ordinal)
                || string.Equals(candidate, $"W/{currentEtag}", StringComparison.Ordinal)
                || string.Equals($"W/{candidate}", currentEtag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
