using System.Net;
using System.Net.Sockets;
using PalLLM.Domain.Configuration;

namespace PalLLM.Sidecar;

/// <summary>
/// Answers the question "will this sidecar make any outbound network call
/// off this machine under the current configuration?" for every enabled
/// surface. Useful for:
///
/// <list type="bullet">
///   <item>Operators who want a one-shot, machine-readable proof that the
///         runtime is truly local-only before shipping.</item>
///   <item>AI assistants asking the instance to self-verify its posture
///         before handing it sensitive input.</item>
///   <item>Auditors under a "no outbound traffic" policy requirement.</item>
/// </list>
///
/// <para>Every outbound destination is classified as:</para>
/// <list type="bullet">
///   <item><c>loopback</c> — 127.0.0.0/8, ::1, <c>localhost</c>. Counts as
///         strictly air-gapped.</item>
///   <item><c>private</c> — RFC1918 / RFC4193 ranges (LAN). Air-gapped in
///         the "no public network" sense but not in the "no outbound at all"
///         strict sense.</item>
///   <item><c>public</c> — anything else. NOT air-gapped.</item>
///   <item><c>disabled</c> — the surface is off; no outbound at all.</item>
///   <item><c>unknown</c> — host could not be resolved / parsed.</item>
/// </list>
///
/// <para>The verifier NEVER emits a live request. Classification is pure
/// host-string inspection + DNS resolution on the passed-in URL, so calling
/// this endpoint can itself be done inside an air-gapped environment.</para>
/// </summary>
public static class AirGapVerifier
{
    // Cache slot — fourth application of the TTL-cache pattern documented
    // in docs/DESIGN_PRINCIPLES.md § 8 (after HardwareProfiler,
    // PrivacyPostureBuilder, and ResourceBudgetPostureBuilder). Air-gap
    // verification is called on every /api/airgap/verify hit plus
    // referenced by /api/describe; classification is pure host-string
    // inspection over options fields which don't change mid-process, so
    // caching is safe and the same signature-based invalidation pattern
    // kicks the cache on any config flip.
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);
    private static volatile AirGapReportCacheEntry? _cached;

    /// <summary>
    /// Verify the current air-gap posture. Always recomputes — for a
    /// TTL-cached variant safe for hot dashboard polls, use
    /// <see cref="VerifyCached"/>.
    /// </summary>
    public static AirGapReport Verify(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<AirGapSurface> surfaces = new();

        // Live inference — only calls out when Enabled.
        surfaces.Add(ClassifySurface(
            surface: "inference",
            enabled: options.Inference.Enabled,
            endpoint: options.Inference.BaseUrl,
            note: options.Inference.Enabled
                ? $"Calls out to {options.Inference.BaseUrl} for chat completions."
                : "Live inference is off; deterministic fallback director replies locally."));

        // Vision — on-demand OCR / describe / world-state.
        surfaces.Add(ClassifySurface(
            surface: "vision",
            enabled: options.Vision.Enabled,
            endpoint: options.Vision.BaseUrl,
            note: options.Vision.Enabled
                ? $"Calls out to {options.Vision.BaseUrl} for vision analysis."
                : "Vision is off; snapshot-derived fallback describes scenes locally."));

        // TTS — synthesize speech.
        surfaces.Add(ClassifySurface(
            surface: "tts",
            enabled: options.Tts.Enabled,
            endpoint: options.Tts.BaseUrl,
            note: options.Tts.Enabled
                ? $"Calls out to {options.Tts.BaseUrl} for text-to-audio synthesis."
                : "TTS is off; no audio is synthesized."));

        // ASR — player/operator audio transcription proof lane.
        surfaces.Add(ClassifySurface(
            surface: "asr",
            enabled: options.Asr.Enabled,
            endpoint: options.Asr.BaseUrl,
            note: options.Asr.Enabled
                ? $"Calls out to {options.Asr.BaseUrl} for audio transcription."
                : "ASR is off; no audio is transcribed."));

        // OTLP telemetry export — only when the process env var is set.
        string? otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        bool otlpEnabled = !string.IsNullOrWhiteSpace(otlpEndpoint);
        surfaces.Add(ClassifySurface(
            surface: "otlp",
            enabled: otlpEnabled,
            endpoint: otlpEndpoint ?? string.Empty,
            note: otlpEnabled
                ? $"OpenTelemetry OTLP exporter configured for {otlpEndpoint}."
                : "OTLP exporter is off; traces and logs stay in-process."));

        // Upstream MCP servers (may be many).
        foreach (McpUpstreamServer upstream in options.McpClient.UpstreamServers)
        {
            surfaces.Add(ClassifySurface(
                surface: $"mcp-upstream:{(string.IsNullOrWhiteSpace(upstream.Id) ? upstream.Url : upstream.Id)}",
                enabled: upstream.Enabled,
                endpoint: upstream.Url,
                note: upstream.Enabled
                    ? $"MCP client discovers/proxies tools at {upstream.Url}."
                    : "This upstream MCP server is disabled."));
        }

        bool anyPublic = surfaces.Any(s => string.Equals(s.Classification, "public", StringComparison.OrdinalIgnoreCase));
        bool anyPrivate = surfaces.Any(s => string.Equals(s.Classification, "private", StringComparison.OrdinalIgnoreCase));
        bool anyUnknown = surfaces.Any(s => string.Equals(s.Classification, "unknown", StringComparison.OrdinalIgnoreCase));

        // Summary verdict + human one-liner.
        string verdict;
        string summary;
        if (anyPublic)
        {
            verdict = "not-airgapped";
            summary = "At least one enabled surface points at a public network host. This sidecar will make outbound requests off this machine.";
        }
        else if (anyPrivate)
        {
            verdict = "lan-airgapped";
            summary = "No enabled surface reaches the public internet. Outbound traffic stays within the local LAN.";
        }
        else if (anyUnknown)
        {
            verdict = "indeterminate";
            summary = "One or more enabled surfaces point at a hostname that could not be resolved or parsed. Review the findings before trusting an airgap claim.";
        }
        else
        {
            verdict = "strict-airgapped";
            summary = "Every enabled outbound surface is either disabled or bound to loopback. This sidecar makes no network calls off this machine.";
        }

        return new AirGapReport(verdict, summary, surfaces);
    }

    /// <summary>
    /// Memoised variant of <see cref="Verify"/> — recomputes at most
    /// once every <paramref name="cacheTtl"/> (default 30 seconds), or
    /// on signature change (any relevant option flips), whichever comes
    /// first. Safe to call on the hot <c>/api/airgap/verify</c> and
    /// <c>/api/describe</c> paths without paying the per-surface
    /// classification cost repeatedly. Fourth application of the
    /// pattern; see <c>docs/DESIGN_PRINCIPLES.md § 8</c>.
    /// </summary>
    public static AirGapReport VerifyCached(PalLlmOptions options, TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        TimeSpan ttl = cacheTtl ?? DefaultCacheTtl;
        string signature = ComputeSignature(options);
        AirGapReportCacheEntry? snapshot = _cached;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot is not null
            && snapshot.Signature == signature
            && now - snapshot.CapturedAt < ttl)
        {
            return snapshot.Report;
        }

        AirGapReport fresh = Verify(options);
        _cached = new AirGapReportCacheEntry(fresh, signature, now);
        return fresh;
    }

    /// <summary>
    /// Invalidate the cache. Next <see cref="VerifyCached"/> call
    /// recomputes from scratch. Exposed for tests and explicit refresh.
    /// </summary>
    public static void InvalidateCache() => _cached = null;

    private static string ComputeSignature(PalLlmOptions options)
    {
        // Mirror every field Verify() reads. Changing any of these
        // changes the resulting AirGapReport, so the cache must miss.
        string? otlpEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        return string.Concat(
            options.Inference.Enabled ? "I+" : "I-",
            options.Inference.BaseUrl ?? "",
            ";",
            options.Vision.Enabled ? "V+" : "V-",
            options.Vision.BaseUrl ?? "",
            ";",
            options.Tts.Enabled ? "T+" : "T-",
            options.Tts.BaseUrl ?? "",
            ";",
            options.Asr.Enabled ? "A+" : "A-",
            options.Asr.BaseUrl ?? "",
            ";",
            "O=", otlpEndpoint ?? "",
            ";",
            string.Join(",", options.McpClient.UpstreamServers
                .Select(u => $"{u.Id}|{u.Url}|{u.Enabled}")));
    }

    private sealed record AirGapReportCacheEntry(
        AirGapReport Report,
        string Signature,
        DateTimeOffset CapturedAt);

    private static AirGapSurface ClassifySurface(string surface, bool enabled, string endpoint, string note)
    {
        if (!enabled)
        {
            return new AirGapSurface(surface, "disabled", endpoint ?? string.Empty, Host: null, note);
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new AirGapSurface(surface, "unknown", string.Empty, Host: null, "Endpoint is empty but the surface is enabled.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri))
        {
            return new AirGapSurface(surface, "unknown", endpoint, Host: null, "Endpoint did not parse as an absolute URI.");
        }

        string host = uri.Host;
        string classification = ClassifyHost(host);
        return new AirGapSurface(surface, classification, endpoint, host, note);
    }

    /// <summary>
    /// Pure host-string + DNS classification. Resolves the hostname to IPs
    /// (best effort, short timeout) and classifies based on the reachable
    /// address class. Never opens a TCP connection or emits a request.
    /// </summary>
    private static string ClassifyHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) { return "unknown"; }

        // Easy string cases first.
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) { return "loopback"; }

        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return ClassifyAddress(literal);
        }

        // Hostname — try to resolve. Use a short timeout so we don't stall
        // the endpoint. If DNS fails, we can't prove safety -> "unknown".
        try
        {
            Task<IPAddress[]> resolve = Dns.GetHostAddressesAsync(host);
            if (!resolve.Wait(TimeSpan.FromMilliseconds(750)))
            {
                return "unknown";
            }

            IPAddress[] addresses = resolve.Result;
            if (addresses.Length == 0) { return "unknown"; }

            // If any resolved address is public, the whole host is public —
            // conservative classification.
            bool anyPublic = false;
            bool anyPrivate = false;
            bool anyLoopback = false;
            foreach (IPAddress addr in addresses)
            {
                switch (ClassifyAddress(addr))
                {
                    case "public": anyPublic = true; break;
                    case "private": anyPrivate = true; break;
                    case "loopback": anyLoopback = true; break;
                }
            }

            if (anyPublic) { return "public"; }
            if (anyPrivate) { return "private"; }
            if (anyLoopback) { return "loopback"; }
            return "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string ClassifyAddress(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) { return "loopback"; }

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = addr.GetAddressBytes();
            // RFC1918 private ranges.
            if (b[0] == 10) { return "private"; }
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) { return "private"; }
            if (b[0] == 192 && b[1] == 168) { return "private"; }
            // Link-local 169.254.0.0/16.
            if (b[0] == 169 && b[1] == 254) { return "private"; }
            return "public";
        }

        if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 unique local (fc00::/7) + link-local (fe80::/10).
            byte[] b = addr.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) { return "private"; }
            if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) { return "private"; }
            return "public";
        }

        return "unknown";
    }
}

public sealed record AirGapReport(
    string Verdict,
    string Summary,
    IReadOnlyList<AirGapSurface> Surfaces);

public sealed record AirGapSurface(
    string Surface,
    string Classification,
    string Endpoint,
    string? Host,
    string Note);
