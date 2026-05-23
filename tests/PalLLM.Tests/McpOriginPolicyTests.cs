using Microsoft.AspNetCore.Http;
using PalLLM.Domain.Configuration;
using PalLLM.Sidecar.Mcp;

namespace PalLLM.Tests;

// Pass 295 - direct unit tests for the MCP origin policy that mitigates
// DNS-rebinding attacks against the localhost-bound Streamable HTTP MCP
// endpoint. The MCP transport spec requires origin checks on browser-
// initiated requests; without them, a malicious page on a non-loopback
// domain could DNS-rebind itself to 127.0.0.1 mid-session and post to
// the MCP server.
//
// Until this pass the policy was only covered indirectly via the
// end-to-end MCP endpoint fixture. The 6+ rejection branches (multiple
// Origin headers, "null" string, malformed URI, non-HTTP scheme,
// non-loopback origin not on the allowlist, allowlist entries with
// malformed shape) had no direct fast-feedback coverage. A regression
// that quietly accepted a non-loopback origin would have shipped through
// every existing test green.
public sealed class McpOriginPolicyTests
{
    // ---------- No Origin header ----------

    [Test]
    public void TryValidate_NoOriginHeader_AcceptsRequest()
    {
        // Non-browser MCP clients (Claude Desktop, VS Code, Cursor, the
        // local Streamable HTTP transport) do not send an Origin header. The
        // policy must allow these through; the rebinding-attack vector only
        // exists for browser-initiated cross-origin requests.
        HttpRequest request = NewRequest();
        var auth = new AuthOptions();

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.True);
        Assert.That(detail, Is.Null);
    }

    // ---------- Loopback origins (always allowed) ----------

    [TestCase("http://localhost")]
    [TestCase("http://localhost:5088")]
    [TestCase("http://127.0.0.1:5088")]
    [TestCase("http://[::1]:5088")]
    [TestCase("https://localhost:8443")]
    public void TryValidate_LoopbackOrigin_AcceptsRequest(string origin)
    {
        HttpRequest request = NewRequest(origin);
        var auth = new AuthOptions();

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.True, $"Loopback origin '{origin}' should be accepted.");
        Assert.That(detail, Is.Null);
    }

    // ---------- Malformed origins ----------

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("null")]                            // Chrome sandboxed-iframe origin
    [TestCase("NULL")]                            // case-insensitive
    [TestCase("not a uri")]
    [TestCase("/relative")]
    [TestCase("javascript:alert(1)")]             // dangerous non-HTTP scheme
    [TestCase("file:///etc/passwd")]              // dangerous non-HTTP scheme
    [TestCase("data:text/html,<x>")]              // data: URI
    [TestCase("chrome-extension://abcdef")]       // browser-extension scheme
    public void TryValidate_MalformedOrNonHttpOrigin_RejectsWithDetail(string origin)
    {
        HttpRequest request = NewRequest(origin);
        var auth = new AuthOptions();

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.False, $"Malformed/non-HTTP origin '{origin}' must be rejected.");
        Assert.That(detail, Is.Not.Null);
        Assert.That(detail, Does.Contain("loopback origin").Or.Contain("McpAllowedOrigins"));
    }

    // ---------- Multiple Origin headers (HTTP spec violation) ----------

    [Test]
    public void TryValidate_MultipleOriginHeaders_RejectsWithDetail()
    {
        // RFC 6454 says a request MUST NOT carry multiple Origin headers; a
        // request that does is either malformed or a downgrade-attack
        // attempt. The policy rejects rather than picking one.
        HttpRequest request = NewRequest("http://localhost");
        request.Headers.Append("Origin", "http://evil.example");
        var auth = new AuthOptions();

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.False);
        Assert.That(detail, Is.Not.Null);
    }

    // ---------- Non-loopback, not on allowlist ----------

    [TestCase("http://evil.example")]
    [TestCase("https://attacker.example.com:8443")]
    [TestCase("http://192.168.1.100")]              // private-range non-loopback
    [TestCase("http://10.0.0.5")]                   // private-range non-loopback
    [TestCase("http://169.254.169.254")]            // link-local (cloud-metadata)
    public void TryValidate_NonLoopbackOriginNotOnAllowlist_Rejects(string origin)
    {
        HttpRequest request = NewRequest(origin);
        var auth = new AuthOptions { McpAllowedOrigins = [] };

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.False, $"Non-loopback origin '{origin}' off the allowlist must be rejected.");
        Assert.That(detail, Does.Contain("McpAllowedOrigins"));
    }

    // ---------- Allowlist ----------

    [Test]
    public void TryValidate_NonLoopbackOriginOnAllowlist_Accepts()
    {
        HttpRequest request = NewRequest("https://example.com:8443");
        var auth = new AuthOptions { McpAllowedOrigins = ["https://example.com:8443"] };

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.True);
        Assert.That(detail, Is.Null);
    }

    [Test]
    public void TryValidate_AllowlistMatch_IsCaseInsensitiveOnHostAndScheme()
    {
        // RFC 6454 origin comparison is scheme + host + port. Host and
        // scheme are case-insensitive in DNS / URI specs; the normalizer
        // lowercases both. So `HTTPS://EXAMPLE.COM:8443` and
        // `https://example.com:8443` describe the same origin.
        HttpRequest request = NewRequest("HTTPS://EXAMPLE.COM:8443");
        var auth = new AuthOptions { McpAllowedOrigins = ["https://example.com:8443"] };

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.True);
        Assert.That(detail, Is.Null);
    }

    [Test]
    public void TryValidate_AllowlistPortMismatch_Rejects()
    {
        // Port is part of origin identity. example.com:8443 and
        // example.com:8444 are distinct origins.
        HttpRequest request = NewRequest("https://example.com:8444");
        var auth = new AuthOptions { McpAllowedOrigins = ["https://example.com:8443"] };

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.False);
        Assert.That(detail, Is.Not.Null);
    }

    [Test]
    public void TryValidate_AllowlistSchemeMismatch_Rejects()
    {
        // Scheme is part of origin identity. http://example.com:8443 and
        // https://example.com:8443 are distinct origins.
        HttpRequest request = NewRequest("http://example.com:8443");
        var auth = new AuthOptions { McpAllowedOrigins = ["https://example.com:8443"] };

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.False);
        Assert.That(detail, Is.Not.Null);
    }

    [Test]
    public void TryValidate_AllowlistWithMalformedEntries_SkipsBadEntriesAndStillMatches()
    {
        // Operators may misconfigure the allowlist. The policy must
        // silently skip malformed entries rather than throw or accept the
        // bad string as an origin.
        HttpRequest request = NewRequest("https://example.com:8443");
        var auth = new AuthOptions
        {
            McpAllowedOrigins = [
                string.Empty,
                "   ",
                "not-a-uri",
                "javascript:bad",
                "https://example.com:8443"  // the actual match
            ]
        };

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.True);
        Assert.That(detail, Is.Null);
    }

    [Test]
    public void TryValidate_AllowlistOfOnlyMalformedEntries_RejectsNonLoopbackOrigin()
    {
        HttpRequest request = NewRequest("https://example.com:8443");
        var auth = new AuthOptions
        {
            McpAllowedOrigins = [string.Empty, "not-a-uri", "javascript:bad"]
        };

        bool ok = McpOriginPolicy.TryValidate(request, auth, out string? detail);

        Assert.That(ok, Is.False);
        Assert.That(detail, Is.Not.Null);
    }

    // ---------- IsLoopbackOrigin helper ----------

    [TestCase("http://localhost", true)]
    [TestCase("http://127.0.0.1", true)]
    [TestCase("http://127.0.0.5", true)]                // any 127.0.0.0/8
    [TestCase("http://[::1]", true)]
    [TestCase("http://example.com", false)]
    [TestCase("http://192.168.1.1", false)]
    [TestCase("http://169.254.169.254", false)]         // link-local metadata
    public void IsLoopbackOrigin_ClassifiesCorrectly(string origin, bool expected)
    {
        Uri uri = new(origin);

        Assert.That(McpOriginPolicy.IsLoopbackOrigin(uri), Is.EqualTo(expected),
            $"Expected IsLoopbackOrigin('{origin}') = {expected}.");
    }

    // ---------- NormalizeOrigin helper ----------

    [TestCase("http://example.com", "http://example.com")]
    [TestCase("HTTP://EXAMPLE.COM", "http://example.com")]
    [TestCase("http://example.com:80", "http://example.com")]               // default port stripped
    [TestCase("https://example.com:443", "https://example.com")]            // default port stripped
    [TestCase("http://example.com:8080", "http://example.com:8080")]        // non-default kept
    [TestCase("https://example.com:8443", "https://example.com:8443")]      // non-default kept
    public void NormalizeOrigin_ProducesCanonicalForm(string input, string expected)
    {
        Uri uri = new(input);

        Assert.That(McpOriginPolicy.NormalizeOrigin(uri), Is.EqualTo(expected));
    }

    // ---------- Helpers ----------

    private static HttpRequest NewRequest(string? origin = null)
    {
        var context = new DefaultHttpContext();
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }
        return context.Request;
    }
}
