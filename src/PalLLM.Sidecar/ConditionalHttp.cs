using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PalLLM.Sidecar;

internal static class ConditionalHttp
{
    public static string CreateStrongEtag<T>(T payload, JsonTypeInfo<T> jsonTypeInfo)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, jsonTypeInfo);
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

    public static bool RequestMatchesLastModified(HttpRequest request, DateTimeOffset currentLastModifiedUtc)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(request.Headers.IfNoneMatch.ToString()))
        {
            return false;
        }

        DateTimeOffset? ifModifiedSince = request.GetTypedHeaders().IfModifiedSince;
        if (!ifModifiedSince.HasValue)
        {
            return false;
        }

        DateTimeOffset normalizedCurrent = TruncateToWholeSeconds(currentLastModifiedUtc);
        DateTimeOffset normalizedCandidate = TruncateToWholeSeconds(ifModifiedSince.Value);
        return normalizedCandidate >= normalizedCurrent;
    }

    public static DateTimeOffset TruncateToWholeSeconds(DateTimeOffset value)
    {
        DateTimeOffset utc = value.ToUniversalTime();
        long ticks = utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
