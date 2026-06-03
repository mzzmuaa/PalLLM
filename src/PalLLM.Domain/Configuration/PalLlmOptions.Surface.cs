// PalLlmOptions (partial): MCP-client / upstream / auth / HTTP-surface / automation options.
// Nested option classes bound from the "PalLLM" appsettings section; see PalLlmOptions.cs for the root aggregator + AGENT-CARD.
using System.Text.Json.Serialization;

namespace PalLLM.Domain.Configuration;


/// <summary>
/// PalLLM-as-MCP-client configuration. Off by default (empty
/// <see cref="UpstreamServers"/> list). When populated, the sidecar
/// connects to each configured external MCP server at startup and
/// periodically re-probes to discover its tools, resources, and
/// prompts. Operators can inspect the discovered surface via
/// <c>GET /api/mcp/upstream</c> or the <c>pal_list_upstream_mcp</c>
/// MCP tool.
///
/// <para>V1 is <b>discovery-only and read-only</b>: PalLLM does not
/// automatically proxy tool calls to discovered upstreams. This keeps
/// the security model simple — an operator explicitly configures
/// upstream URLs and auth, and the runtime only fetches catalog
/// metadata. Future revisions can layer selective invocation on top
/// once the security model is designed.</para>
/// </summary>
public sealed class McpClientOptions
{
    /// <summary>Ordered list of external MCP servers the sidecar should probe.</summary>
    public List<McpUpstreamServer> UpstreamServers { get; set; } = new();

    /// <summary>
    /// Periodic re-discovery cadence. Each tick re-probes every enabled
    /// upstream server so newly-added tools on the remote side become
    /// visible without restarting the sidecar.
    /// </summary>
    public int DiscoveryIntervalSeconds { get; set; } = 300;

    /// <summary>
    /// HTTP timeout for each upstream probe. Short by design so a
    /// hung server can't stall the discovery worker.
    /// </summary>
    public int DiscoveryTimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Hard cap on the number of tool names cached per upstream server.
    /// Keeps one overly chatty server from ballooning the cached snapshot
    /// or the `/api/mcp/upstream` response body.
    /// </summary>
    public int MaxToolsPerServer { get; set; } = 128;

    /// <summary>
    /// Hard cap on the number of resource URIs cached per upstream server.
    /// </summary>
    public int MaxResourcesPerServer { get; set; } = 128;

    /// <summary>
    /// Hard cap on the number of prompt names cached per upstream server.
    /// </summary>
    public int MaxPromptsPerServer { get; set; } = 64;

    /// <summary>
    /// Hard cap on the length of any cached upstream tool name, resource
    /// URI, or prompt name. Oversized values are trimmed after whitespace
    /// and control-character normalization so snapshots stay log-safe and
    /// memory-bounded.
    /// </summary>
    public int MaxMetadataEntryLength { get; set; } = 256;
}

/// <summary>
/// A single external MCP server the sidecar should discover.
/// </summary>
public sealed class McpUpstreamServer
{
    /// <summary>Human-readable id used in logs, status endpoints, and tool output.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Streamable HTTP endpoint URL (e.g. <c>http://localhost:3001/mcp</c>).</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Optional bearer token forwarded as <c>Authorization: Bearer</c>.</summary>
    public string? BearerToken { get; set; }

    /// <summary>Per-server enable switch so operators can disable one server
    /// without removing it from the config.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class AuthOptions
{
    /// <summary>
    /// Optional bearer-token key. When set to a non-empty string, every
    /// request under <c>/api/*</c> must carry an
    /// <c>Authorization: Bearer &lt;key&gt;</c> header whose value matches
    /// exactly (constant-time <c>CryptographicOperations.FixedTimeEquals</c>
    /// byte comparison). When null or empty (default) the
    /// sidecar serves <c>/api/*</c> unauthenticated — the right posture for
    /// localhost-only deployments where the port is only reachable from the
    /// machine owner.
    ///
    /// <para>Operational routes (<c>/metrics</c>, <c>/health/live</c>,
    /// <c>/health/ready</c>, <c>/openapi/v1.json</c>, and the static
    /// dashboard) stay open by default so monitoring and the public
    /// contract are reachable without a credential. Flip
    /// <see cref="ProtectMetrics"/> or <see cref="ProtectHealth"/> on when
    /// exposing the sidecar to an untrusted network where even those
    /// surfaces should require a credential.</para>
    ///
    /// <para>Supply the key via any standard ASP.NET Core configuration
    /// source: <c>appsettings.json</c>, the
    /// <c>PalLLM__Auth__ApiKey</c> environment variable (works from inside
    /// the Docker container), or a secrets manager.</para>
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Exact browser origins allowed to call the Streamable HTTP MCP endpoint
    /// when an <c>Origin</c> header is present. Entries must be full origins
    /// such as <c>https://ops.example.com</c> or
    /// <c>http://127.0.0.1:3000</c> - hostnames alone are not enough.
    ///
    /// <para>Loopback origins (<c>localhost</c>, <c>127.0.0.1</c>,
    /// <c>::1</c>) are always allowed even when this list is empty so local
    /// dashboards and desktop clients keep working by default. Requests with
    /// no <c>Origin</c> header are also allowed because most desktop MCP
    /// clients are not browsers and therefore do not send one.</para>
    /// </summary>
    public List<string> McpAllowedOrigins { get; set; } = [];

    /// <summary>
    /// When true and <see cref="ApiKey"/> is set, <c>/metrics</c> also
    /// requires the bearer credential. Default false so a local Prometheus
    /// scrape keeps working without extra config.
    /// </summary>
    public bool ProtectMetrics { get; set; }

    /// <summary>
    /// When true and <see cref="ApiKey"/> is set, <c>/health/live</c> and
    /// <c>/health/ready</c> also require the bearer credential. Default
    /// false so container orchestrators and external health pollers keep
    /// working without extra config.
    /// </summary>
    public bool ProtectHealth { get; set; }
}

public sealed class HttpSurfaceOptions
{
    /// <summary>
    /// Cache duration for the generated OpenAPI JSON/YAML endpoints. The route
    /// surface is effectively static for a running PalLLM process, so caching the
    /// generated document avoids repeating the document-generation pipeline on
    /// every request while still staying fresh after a restart. Set to 0 to disable.
    /// </summary>
    public int OpenApiCacheMinutes { get; set; } = 10;

    /// <summary>
    /// Client + server cache TTL for the static feature catalog exposed on
    /// <c>GET /api/features</c>. The catalog only changes when the process
    /// starts a new build, so a longer TTL cuts repeated downloads and enables
    /// cheap browser revalidation with ETags.
    /// </summary>
    public int FeatureCatalogCacheMinutes { get; set; } = 60;

    /// <summary>
    /// Client + server cache TTL for <c>GET /api/describe</c>. Keep short because
    /// the self-description surface is read-heavy but still includes current
    /// health and configuration posture, so callers benefit from fewer repeated
    /// rebuilds without carrying a long stale window.
    /// </summary>
    public int SelfDescriptionCacheSeconds { get; set; } = 15;

    /// <summary>
    /// Client + server cache TTL for the discovered upstream MCP snapshot on
    /// <c>GET /api/mcp/upstream</c>. Keep short because the background worker
    /// refreshes these snapshots at runtime.
    /// </summary>
    public int UpstreamSnapshotCacheSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum bytes PalLLM will read from local JSON artifacts surfaced
    /// through inspection endpoints such as <c>GET /api/release/readiness</c>
    /// and <c>GET /api/self-healing/status</c>. Keeps release/readiness and
    /// watchdog readers bounded even if a local artifact is bloated,
    /// truncated, or tampered with.
    /// </summary>
    public int LocalArtifactMaxBytes { get; set; } = 65_536;

    /// <summary>
    /// Maximum HTTP request-body bytes accepted on API and MCP JSON routes
    /// before model binding starts. Field-level validators still enforce
    /// tighter semantic caps, but this outer guard keeps oversized JSON bodies
    /// from allocating deeply before PalLLM can reject them.
    /// </summary>
    public int ApiRequestBodyMaxBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>
    /// Global concurrency cap for <c>POST /api/chat</c>. Local inference is the
    /// dominant latency + hardware cost in PalLLM, so a small gate keeps bursty
    /// callers from oversubscribing the local model runtime and wrecking tail
    /// latency for everyone else.
    /// </summary>
    public int ChatConcurrentRequests { get; set; } = 2;

    /// <summary>
    /// Queue depth behind the chat concurrency gate. Short by design: interactive
    /// callers should fail fast with a 429 rather than wait behind a long queue of
    /// already-expensive turns.
    /// </summary>
    public int ChatQueueLimit { get; set; } = 4;

    /// <summary>
    /// Outer ASP.NET Core request timeout for chat-class HTTP lanes, including
    /// <c>POST /api/chat</c>, <c>POST /api/chat/party</c>, and the manual
    /// inference warmup endpoint. This is deliberately wider than the default
    /// upstream inference timeout so the model client can still perform its
    /// configured single transient retry, while the HTTP lane remains bounded.
    /// </summary>
    public int ChatRequestTimeoutSeconds { get; set; } = 130;

    /// <summary>
    /// Global concurrency cap for vision endpoints (<c>/api/vision/*</c>). Vision
    /// work is usually more expensive than text fallback and often runs on the
    /// same local accelerator as chat, so it gets its own tighter lane.
    /// </summary>
    public int VisionConcurrentRequests { get; set; } = 1;

    public int VisionQueueLimit { get; set; } = 2;

    /// <summary>
    /// Outer ASP.NET Core request timeout for vision HTTP lanes. Upstream vision
    /// failures still degrade to structured vision responses; this guard catches
    /// hung local work that outlives the configured endpoint budget.
    /// </summary>
    public int VisionRequestTimeoutSeconds { get; set; } = 45;

    /// <summary>
    /// Global concurrency cap for TTS synthesis requests. TTS is optional and
    /// latency-sensitive, but usually cheaper than multimodal extraction, so the
    /// default lane is wider than vision and narrower than unconstrained parallelism.
    /// </summary>
    public int TtsConcurrentRequests { get; set; } = 2;

    public int TtsQueueLimit { get; set; } = 4;

    /// <summary>
    /// Outer ASP.NET Core request timeout for TTS synthesis. Keep close to the
    /// upstream TTS timeout so stale audio jobs do not linger behind interactive
    /// chat work.
    /// </summary>
    public int TtsRequestTimeoutSeconds { get; set; } = 45;
}

public sealed class AutomationOptions
{
    /// Hard kill switch for action-intent emission. When false (default), PalLLM
    /// never attaches an ActionIntent to a ChatResponse — companions stay purely
    /// advisory. Flipping on is explicit operator opt-in, and actions still pass
    /// through the <see cref="AllowedActions"/> allowlist before being emitted.
    public bool Enabled { get; set; }

    /// Allowlist of action types the runtime is permitted to suggest. Empty
    /// means no intent is ever emitted regardless of the Enabled flag — safer
    /// default than allow-all. Known safe types:
    /// <c>waypoint_suggest</c>, <c>recall_pals</c>, <c>request_craft_queue</c>.
    public List<string> AllowedActions { get; set; } = [];

    /// When true, the runtime appends the intent to the outbox envelope so a
    /// UE4SS Lua consumer can pick it up. When false, the intent is only visible
    /// on the ChatResponse — useful for dry-running automation logic without
    /// letting the game-side consumer act on it.
    public bool EmitToOutbox { get; set; } = true;
}
