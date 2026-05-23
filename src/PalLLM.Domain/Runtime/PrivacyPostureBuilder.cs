using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 27 / E3 — deterministic, machine-readable privacy posture.
/// Enumerates every data-emitting surface PalLLM ships and classifies
/// each as <c>"never-leaves"</c>, <c>"only-with-opt-in"</c>, or
/// <c>"leaves-by-default"</c>. Used by <c>/api/privacy/posture</c>,
/// <c>pal_privacy_posture</c> MCP tool, and the dashboard privacy chip
/// so operators + AI agents can prove what does and does not leave
/// the machine without running a packet capture.
///
/// <para>Pairs with <c>AirGapVerifier</c> in the Sidecar project: the air-gap verifier
/// classifies live endpoints by network scope (loopback / private /
/// public); the privacy-posture builder explains <i>what category of
/// data</i> each surface would transmit if enabled. Together they
/// answer "is this install actually air-gapped right now?" and "what
/// does turning X on mean for my privacy?"</para>
/// </summary>
public static class PrivacyPostureBuilder
{
    // Cache slot — privacy posture is composed from deterministic inputs
    // (a handful of option booleans + one env-var presence check), so we
    // can hand back the same snapshot across many /api/privacy/posture
    // hits without recomputing the 16-surface list. Keyed on a compact
    // signature string so any change to the relevant options invalidates
    // automatically. Short TTL so an operator flipping a config file
    // picks up quickly via process restart; within a single process the
    // signature handles it.
    // See docs/DESIGN_PRINCIPLES.md § 8 "Cache TTLs over recomputation
    // on hot paths" for the broader pattern.
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);
    private static volatile PrivacyPostureCacheEntry? _cached;

    /// <summary>
    /// Build the complete posture snapshot from the current options.
    /// Always recomputes — for a cached variant that only recomputes
    /// every <see cref="CaptureCached"/>-configured TTL (or on options
    /// signature change), use <see cref="CaptureCached"/>.
    /// </summary>
    public static PrivacyPosture Capture(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var surfaces = new List<PrivacySurface>
        {
            // --- Never leaves (ship-baseline guarantee) -----------------
            new(
                Id: "conversation-memory",
                Category: "memory",
                Status: "never-leaves",
                Description: "Chat history + embeddings live in ConversationMemoryStore, persisted to runtime-root/session.json only when Session.Enabled=true.",
                ControlledBy: "PalLLM:Session:Enabled (affects disk persistence, not network)",
                AffectedByOptIn: false),
            new(
                Id: "relationship-tracker",
                Category: "memory",
                Status: "never-leaves",
                Description: "Per-character affection/trust/stress records are in-process and persisted to disk only.",
                ControlledBy: null,
                AffectedByOptIn: false),
            new(
                Id: "dashboard",
                Category: "http",
                Status: "never-leaves",
                Description: "The Field Console dashboard is served from wwwroot/ on the sidecar's bound addresses; the shipped launchers default to localhost:5088.",
                ControlledBy: "ASPNETCORE_URLS / --urls",
                AffectedByOptIn: false),
            new(
                Id: "health-probes",
                Category: "http",
                Status: "never-leaves",
                Description: "/health/live, /health/ready, /metrics, and /openapi/* follow the sidecar's bound addresses; the shipped launchers default to localhost:5088.",
                ControlledBy: "ASPNETCORE_URLS / --urls",
                AffectedByOptIn: false),
            new(
                Id: "proof-packets",
                Category: "evidence",
                Status: "never-leaves",
                Description: "Proof packets, release readiness, support bundles, and launch evidence are local files under Runtime/ReleaseEvidence or Runtime/SupportEvidence.",
                ControlledBy: null,
                AffectedByOptIn: false),
            new(
                Id: "promotion-staging",
                Category: "evidence",
                Status: "never-leaves",
                Description: "Pass-24 apply verb writes template/rollback/packet triples to PalLLM:PromotionApply:StagingRoot only — never transmitted.",
                ControlledBy: "PalLLM:PromotionApply:AllowApply",
                AffectedByOptIn: false),
            new(
                Id: "deterministic-fallback",
                Category: "inference",
                Status: "never-leaves",
                Description: "FallbackBehaviorEngine replies are composed from local prompt templates and emit zero network traffic.",
                ControlledBy: null,
                AffectedByOptIn: false),

            // --- Only with opt-in ---------------------------------------
            new(
                Id: "live-inference",
                Category: "inference",
                Status: options.Inference.Enabled ? "leaves-by-default" : "only-with-opt-in",
                Description: $"HttpJsonInferenceClient POSTs chat completions to PalLLM:Inference:BaseUrl when enabled. Current endpoint: {Classify(options.Inference.BaseUrl)}.",
                ControlledBy: "PalLLM:Inference:Enabled + PalLLM:Inference:BaseUrl",
                AffectedByOptIn: true),
            new(
                Id: "vision-describe",
                Category: "vision",
                Status: options.Vision.Enabled ? "leaves-by-default" : "only-with-opt-in",
                Description: $"HttpVisionClient POSTs screenshots to PalLLM:Vision:BaseUrl when enabled. Current endpoint: {Classify(options.Vision.BaseUrl)}.",
                ControlledBy: "PalLLM:Vision:Enabled + PalLLM:Vision:BaseUrl",
                AffectedByOptIn: true),
            new(
                Id: "tts-synthesis",
                Category: "audio",
                Status: options.Tts.Enabled ? "leaves-by-default" : "only-with-opt-in",
                Description: $"HttpTtsClient POSTs text to PalLLM:Tts:BaseUrl when enabled. Current endpoint: {Classify(options.Tts.BaseUrl)}.",
                ControlledBy: "PalLLM:Tts:Enabled + PalLLM:Tts:BaseUrl",
                AffectedByOptIn: true),
            new(
                Id: "asr-transcription",
                Category: "audio",
                Status: options.Asr.Enabled ? "leaves-by-default" : "only-with-opt-in",
                Description: $"HttpAudioTranscriptionClient POSTs bounded audio clips to PalLLM:Asr:BaseUrl when enabled. Current endpoint: {Classify(options.Asr.BaseUrl)}.",
                ControlledBy: "PalLLM:Asr:Enabled + PalLLM:Asr:BaseUrl",
                AffectedByOptIn: true),
            new(
                Id: "otlp-telemetry",
                Category: "telemetry",
                Status: HasOtlpEndpoint() ? "leaves-by-default" : "only-with-opt-in",
                Description: "OpenTelemetry OTLP exporter sends traces + metrics + logs to OTEL_EXPORTER_OTLP_ENDPOINT when that env-var is set. Default off.",
                ControlledBy: "OTEL_EXPORTER_OTLP_ENDPOINT env-var",
                AffectedByOptIn: true),
            new(
                Id: "upstream-mcp",
                Category: "http",
                Status: "only-with-opt-in",
                Description: "UpstreamMcpClient connects to configured PalLLM:McpClient:UpstreamServers[] endpoints. Each entry is an explicit opt-in by URL.",
                ControlledBy: "PalLLM:McpClient:UpstreamServers[]",
                AffectedByOptIn: true),
            new(
                Id: "narrative-pack-loading",
                Category: "content",
                Status: "only-with-opt-in",
                Description: "Narrative packs are loaded from runtime-root/Packs on disk; sharing a pack to another machine requires the operator to hand-copy the files.",
                ControlledBy: "Hand-copy pack files into runtime-root/Packs",
                AffectedByOptIn: true),

            // --- Telemetry that never flows by default ------------------
            new(
                Id: "crash-reports",
                Category: "telemetry",
                Status: "never-leaves",
                Description: "PalLLM does not phone home on crashes. SelfHealingWorker writes local evidence under Runtime/SelfHealing/ only.",
                ControlledBy: null,
                AffectedByOptIn: false),
            new(
                Id: "update-check",
                Category: "network",
                Status: "never-leaves",
                Description: "PalLLM does not automatically check for updates. The release-readiness snapshot reads local evidence only.",
                ControlledBy: null,
                AffectedByOptIn: false),
            new(
                Id: "analytics",
                Category: "telemetry",
                Status: "never-leaves",
                Description: "No product analytics. PalLlmMetrics exposes Prometheus counters on the localhost /metrics surface only.",
                ControlledBy: null,
                AffectedByOptIn: false),
        };

        int neverCount = surfaces.Count(s => s.Status == "never-leaves");
        int optInCount = surfaces.Count(s => s.Status == "only-with-opt-in");
        int activeCount = surfaces.Count(s => s.Status == "leaves-by-default");

        string headline = activeCount == 0
            ? $"Fully local: {neverCount} surfaces never leave, {optInCount} require explicit opt-in, none currently active."
            : $"{activeCount} outbound surface(s) currently active. {neverCount} never leave. {optInCount} available behind opt-in.";

        return new PrivacyPosture(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Headline: headline,
            NeverLeavesCount: neverCount,
            OptInAvailableCount: optInCount,
            ActiveOutboundCount: activeCount,
            Surfaces: surfaces);
    }

    /// <summary>
    /// Memoised variant of <see cref="Capture"/> — recomputes at most
    /// once every <paramref name="cacheTtl"/> (default 30 seconds), or
    /// on options-signature change, whichever comes first. Safe to call
    /// on the /api/privacy/posture hot path without paying the list
    /// composition cost repeatedly. Applies the shared TTL-cache
    /// pattern documented in docs/DESIGN_PRINCIPLES.md § 8.
    /// </summary>
    public static PrivacyPosture CaptureCached(PalLlmOptions options, TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        TimeSpan ttl = cacheTtl ?? DefaultCacheTtl;
        string signature = ComputeSignature(options);
        PrivacyPostureCacheEntry? snapshot = _cached;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot is not null
            && snapshot.Signature == signature
            && now - snapshot.CapturedAt < ttl)
        {
            return snapshot.Posture;
        }

        PrivacyPosture fresh = Capture(options);
        _cached = new PrivacyPostureCacheEntry(fresh, signature, now);
        return fresh;
    }

    /// <summary>
    /// Invalidate the cache. The next <see cref="CaptureCached"/> call
    /// will recompute from scratch. Exposed for tests and for operators
    /// who explicitly request a re-probe.
    /// </summary>
    public static void InvalidateCache() => _cached = null;

    private static string ComputeSignature(PalLlmOptions options)
    {
        // Compact signature of the fields Capture() actually reads.
        // Changing any of these invalidates the cache automatically.
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
            HasOtlpEndpoint() ? "O+" : "O-");
    }

    private sealed record PrivacyPostureCacheEntry(
        PrivacyPosture Posture,
        string Signature,
        DateTimeOffset CapturedAt);

    private static bool HasOtlpEndpoint()
    {
        string? value = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string Classify(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) { return "not-set"; }
        if (endpoint.Contains("localhost", StringComparison.OrdinalIgnoreCase)
            || endpoint.Contains("127.0.0.1", StringComparison.Ordinal)
            || endpoint.Contains("::1", StringComparison.Ordinal))
        {
            return "loopback";
        }
        if (endpoint.Contains("10.", StringComparison.Ordinal)
            || endpoint.Contains("192.168.", StringComparison.Ordinal)
            || endpoint.Contains("172.16.", StringComparison.Ordinal))
        {
            return "private-lan";
        }
        if (endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return "public-internet";
        }
        return "unknown";
    }
}

/// <summary>
/// Machine-readable privacy posture snapshot.
/// </summary>
/// <param name="CapturedAtUtc">When the snapshot was taken (UTC).</param>
/// <param name="Headline">One-sentence plain-English summary.</param>
/// <param name="NeverLeavesCount">Count of surfaces that never emit network traffic.</param>
/// <param name="OptInAvailableCount">Count of surfaces that are opt-in-only and currently off.</param>
/// <param name="ActiveOutboundCount">Count of surfaces currently actively emitting traffic.</param>
/// <param name="Surfaces">Full per-surface detail list.</param>
public sealed record PrivacyPosture(
    DateTimeOffset CapturedAtUtc,
    string Headline,
    int NeverLeavesCount,
    int OptInAvailableCount,
    int ActiveOutboundCount,
    IReadOnlyList<PrivacySurface> Surfaces);

/// <summary>
/// One entry in the privacy posture report: a single data-emitting
/// surface + its current status and configuration controls.
/// </summary>
/// <param name="Id">Stable kebab-case identifier.</param>
/// <param name="Category">High-level bucket: "memory", "http", "inference", "vision", "audio", "telemetry", "network", "content", "evidence".</param>
/// <param name="Status">Current status: "never-leaves", "only-with-opt-in", or "leaves-by-default".</param>
/// <param name="Description">Plain-English description of what the surface does and where the data goes.</param>
/// <param name="ControlledBy">Configuration key that controls this surface, or null if not operator-configurable.</param>
/// <param name="AffectedByOptIn">True when toggling an operator option can change the Status.</param>
public sealed record PrivacySurface(
    string Id,
    string Category,
    string Status,
    string Description,
    string? ControlledBy,
    bool AffectedByOptIn);
