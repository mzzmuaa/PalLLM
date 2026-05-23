using System.Net;

namespace PalLLM.Domain.Configuration;

/// <summary>
/// Pass 354: production-safety guard that runs at sidecar startup
/// to catch the "operator forgot to enable auth before binding to a
/// non-loopback interface" footgun. The default
/// <c>PalLLM:Auth:ApiKey = null</c> serves the localhost-only
/// posture the docs describe -- but ASP.NET Core does not refuse to
/// bind public interfaces under that posture. An operator who
/// changes <c>ASPNETCORE_URLS</c> to <c>http://0.0.0.0:5088</c>
/// with no auth gets a sidecar exposed to LAN-or-worse with no
/// startup warning.
///
/// <para>This guard inspects the configured bind URLs alongside the
/// resolved <see cref="AuthOptions.ApiKey"/> and returns a verdict
/// the startup path acts on:</para>
///
/// <list type="bullet">
///   <item><description><b>Fail</b> -- any non-loopback bind URL is
///     present and ApiKey is null/empty. Startup must abort with the
///     remediation hint baked in so the operator sees both the
///     problem and the fix in a single log line.</description></item>
///   <item><description><b>Warn</b> -- every bind URL is loopback
///     (<c>localhost</c> / <c>127.0.0.1</c> / <c>::1</c>) but ApiKey
///     is null/empty. This is the "fine for dev, surprising in
///     prod" posture -- safe to keep running, but log loudly.
///     </description></item>
///   <item><description><b>Pass</b> -- auth is enabled, OR the bind
///     surface is empty (no URLs configured; Kestrel defaults to
///     loopback). No log line needed.</description></item>
/// </list>
///
/// <para>The class is intentionally pure-logic so it can be
/// exhaustively unit-tested without spinning up an
/// <c>IWebHostBuilder</c>. <see cref="StartupAuthGuard.Inspect"/>
/// is the single entry point.</para>
/// </summary>
public static class StartupAuthGuard
{
    public enum Verdict
    {
        Pass = 0,
        Warn = 1,
        Fail = 2,
    }

    public readonly record struct Result(
        Verdict Action,
        string Reason,
        string RemediationHint,
        IReadOnlyList<string> OffendingUrls);

    /// <summary>
    /// Classify each bind URL + the resolved API key and return the
    /// strictest applicable action. Inputs are intentionally simple
    /// strings so the caller can pass <c>WebApplication.Urls</c>
    /// (which is a string collection) directly without conversion.
    /// </summary>
    public static Result Inspect(IEnumerable<string>? bindUrls, string? apiKey)
    {
        bool authConfigured = !string.IsNullOrWhiteSpace(apiKey);
        List<string> urls = (bindUrls ?? Array.Empty<string>())
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .ToList();

        if (urls.Count == 0)
        {
            // No URLs configured -> Kestrel will fall back to the
            // ASP.NET Core defaults, which bind loopback only. Treat
            // as the same equivalence class as "all loopback".
            return authConfigured
                ? new Result(Verdict.Pass, "Authentication enabled; no explicit bind URLs (Kestrel default loopback).", string.Empty, Array.Empty<string>())
                : new Result(Verdict.Warn, "Authentication is OFF and no bind URLs are configured. Kestrel defaults to loopback so this is safe locally; set `PalLLM:Auth:ApiKey` before exposing the port beyond localhost.", "Set `PalLLM:Auth:ApiKey` (or env `PalLLM__Auth__ApiKey`) to a non-empty value. See docs/SECURITY.md.", Array.Empty<string>());
        }

        List<string> nonLoopbackBinds = urls.Where(u => !IsLoopbackBind(u)).ToList();

        if (authConfigured)
        {
            // Auth is on. Non-loopback binds are fine; loopback binds
            // are fine. Nothing to flag.
            return new Result(Verdict.Pass, "Authentication enabled; bind surface accepted as-is.", string.Empty, Array.Empty<string>());
        }

        if (nonLoopbackBinds.Count == 0)
        {
            // All-loopback with no auth -- the documented dev-mode
            // posture. Warn loudly so an operator who later flips
            // ASPNETCORE_URLS to 0.0.0.0 sees the prior warning in
            // their logs and knows to set the key first.
            return new Result(
                Verdict.Warn,
                $"Authentication is OFF and the sidecar is bound only to loopback ({string.Join(", ", urls)}). This is the documented localhost-only posture; safe for development, NOT safe if you later expose the port beyond localhost without first setting `PalLLM:Auth:ApiKey`.",
                "Before binding to a LAN or public interface (e.g. setting `ASPNETCORE_URLS=http://0.0.0.0:5088`), set `PalLLM:Auth:ApiKey` to a non-empty value. The Pass 354 startup guard will refuse to boot the sidecar otherwise.",
                Array.Empty<string>());
        }

        // The blocking case: non-loopback bind + no auth. Refuse to
        // boot so an operator who forgot to set the key never ends
        // up with an unauthenticated network endpoint.
        return new Result(
            Verdict.Fail,
            $"Refusing to start: PalLLM is bound to a non-loopback interface ({string.Join(", ", nonLoopbackBinds)}) AND authentication is DISABLED (`PalLLM:Auth:ApiKey` is null/empty). This would expose every `/api/*` route, the dashboard, and every MCP tool to anything that can reach the bind interface.",
            "Either (a) set `PalLLM:Auth:ApiKey` (env: `PalLLM__Auth__ApiKey`) to a non-empty bearer-token value, or (b) restrict the bind by setting `ASPNETCORE_URLS=http://127.0.0.1:5088` (or `[::1]:5088`). See docs/SECURITY.md `## API authentication`.",
            nonLoopbackBinds);
    }

    /// <summary>
    /// True when the URL's host resolves to a loopback identifier.
    /// Treats <c>localhost</c>, <c>127.0.0.0/8</c>, and <c>::1</c>
    /// as loopback. Wildcard binds (<c>0.0.0.0</c>, <c>::</c>,
    /// <c>*</c>, <c>+</c>) and any hostname / non-loopback IP are
    /// non-loopback.
    /// </summary>
    public static bool IsLoopbackBind(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // Kestrel accepts wildcard binds via `*` and `+`. The Uri
        // parser can't represent those, so handle them explicitly.
        // Both forms expose every interface => never loopback.
        if (url.Contains("://*", StringComparison.Ordinal) ||
            url.Contains("://+", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            // Fallback: treat unparseable URLs as non-loopback so we
            // fail safe (cause a warning/fail rather than masking
            // a malformed config as loopback-only).
            return false;
        }

        string host = parsed.Host;
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        // Common loopback aliases first; Uri.Host normalizes case for
        // ASCII but not all edge cases, so OrdinalIgnoreCase is the
        // safe comparison.
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 0.0.0.0 / :: are "all interfaces" — explicitly non-loopback
        // even though IPAddress.IsLoopback wouldn't catch them.
        if (host == "0.0.0.0" || host == "::" || host == "[::]")
        {
            return false;
        }

        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? ip))
        {
            return IPAddress.IsLoopback(ip);
        }

        // Hostname that isn't `localhost` -- assume reachable from
        // somewhere, so non-loopback.
        return false;
    }
}
