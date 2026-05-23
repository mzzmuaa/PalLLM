using System.Net;
using Microsoft.Extensions.Primitives;
using PalLLM.Domain.Configuration;

namespace PalLLM.Sidecar.Mcp;

/// <summary>
/// Validates browser-originated requests to the local Streamable HTTP MCP
/// endpoint. The MCP transport spec requires origin checks to mitigate DNS
/// rebinding attacks against localhost-bound servers.
/// </summary>
internal static class McpOriginPolicy
{
    private const string RejectionDetail =
        "MCP browser requests must originate from a loopback origin or an origin explicitly listed in PalLLM:Auth:McpAllowedOrigins[].";

    public static bool TryValidate(HttpRequest request, AuthOptions auth, out string? rejectionDetail)
    {
        rejectionDetail = null;

        if (!request.Headers.TryGetValue("Origin", out StringValues origins) || origins.Count == 0)
        {
            return true;
        }

        if (origins.Count != 1)
        {
            rejectionDetail = RejectionDetail;
            return false;
        }

        string originValue = origins[0] ?? string.Empty;
        if (string.IsNullOrWhiteSpace(originValue) ||
            string.Equals(originValue, "null", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(originValue, UriKind.Absolute, out Uri? origin) ||
            !IsHttpOrigin(origin))
        {
            rejectionDetail = RejectionDetail;
            return false;
        }

        if (IsLoopbackOrigin(origin) || IsExplicitlyAllowed(origin, auth.McpAllowedOrigins))
        {
            return true;
        }

        rejectionDetail = RejectionDetail;
        return false;
    }

    internal static bool IsExplicitlyAllowed(Uri origin, IReadOnlyList<string>? allowedOrigins)
    {
        if (allowedOrigins is null || allowedOrigins.Count == 0)
        {
            return false;
        }

        string candidate = NormalizeOrigin(origin);
        foreach (string raw in allowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(raw) ||
                !Uri.TryCreate(raw, UriKind.Absolute, out Uri? allowedOrigin) ||
                !IsHttpOrigin(allowedOrigin))
            {
                continue;
            }

            if (string.Equals(candidate, NormalizeOrigin(allowedOrigin), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsLoopbackOrigin(Uri origin)
    {
        if (origin.IsLoopback)
        {
            return true;
        }

        return IPAddress.TryParse(origin.IdnHost, out IPAddress? address) &&
            IPAddress.IsLoopback(address);
    }

    internal static string NormalizeOrigin(Uri origin)
    {
        string host = origin.IdnHost.ToLowerInvariant();
        string scheme = origin.Scheme.ToLowerInvariant();
        bool includePort = !origin.IsDefaultPort;
        return includePort
            ? $"{scheme}://{host}:{origin.Port}"
            : $"{scheme}://{host}";
    }

    private static bool IsHttpOrigin(Uri origin) =>
        string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
