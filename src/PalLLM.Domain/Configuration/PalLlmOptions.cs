// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Strongly-typed runtime configuration. Every operator-facing knob
//            (Bridge / Inference / Fallback / Tts / Asr / Vision / Session /
//            Automation / Auth / Http / McpClient) lives here as a nested
//            options class with compiled defaults. Bound from
//            appsettings.json's "PalLLM" section + PalLLM__Section__Key env
//            var overrides.
//   surface: PalLlmOptions (root); BridgeOptions / InferenceOptions /
//            FallbackOptions / TtsOptions / AsrOptions / VisionOptions / SessionOptions /
//            AutomationOptions / AuthOptions / HttpSurfaceOptions /
//            McpClientOptions (nested).
//   gate:    None directly; option validation lives in PalLlmOptionsValidator.
//   adr:     0006-opt-in-defaults.md (every privacy-sensitive opt-in is off
//            by default; the wizard NEVER flips defaults without explicit
//            consent).
//   docs:    docs/ENV_VARS.md (every knob with effects), docs/TUNING.md
//            (too-low / too-high guidance + per-knob test recipes),
//            scripts/pal-config-wizard.ps1 (interactive setup).
// ---------------------------------------------------------------------------

using System.Text.Json.Serialization;

namespace PalLLM.Domain.Configuration;

public sealed class PalLlmOptions
{
    public string PalSavedRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pal",
        "Saved");

    public string RuntimeFolderName { get; set; } = "PalLLM";

    /// <summary>
    /// Optional absolute path that overrides where <see cref="ModelsDir"/> resolves
    /// to. Empty (default) keeps the runtime-root-anchored layout
    /// (<c>%LOCALAPPDATA%\Pal\Saved\PalLLM\Models</c>). Set to an operator-curated
    /// model library (e.g. <c>D:\Models</c>) to share weights across PalLLM
    /// installs and other inference tooling without duplicating GB-class files.
    /// PalLLM never writes to this directory automatically — it's an
    /// informational hint for the connector scripts and for any future advisor
    /// that needs to enumerate locally-available GGUFs.
    /// </summary>
    public string ExternalModelsRoot { get; set; } = string.Empty;

    public BridgeOptions Bridge { get; set; } = new();

    public InferenceOptions Inference { get; set; } = new();

    /// <summary>
    /// Background self-healing watchdog settings. When enabled, a
    /// conservative worker sweeps for orphan outbox envelopes on a cadence
    /// and writes a durable audit artifact under
    /// <c>Runtime/SelfHealingEvidence/latest-self-healing.json</c> so
    /// operators can see exactly what was automated. Defaults to on with
    /// safe thresholds; flip <see cref="SelfHealingOptions.Enabled"/> to
    /// false to disable the worker entirely.
    /// </summary>
    public SelfHealingOptions SelfHealing { get; set; } = new();

    /// <summary>
    /// Auto-feeder that converts <c>PalLlmMetrics</c> deltas into
    /// <c>PromotionLedger</c> observations on a cadence. Opt-in but
    /// defaults ON — the feeder is pure observer (reads metric counters,
    /// writes to the bounded in-memory ledger) so leaving it on is safe.
    /// Disable by flipping <see cref="PromotionFeederOptions.Enabled"/>
    /// if an operator would rather feed the ledger manually.
    /// </summary>
    public PromotionFeederOptions PromotionFeeder { get; set; } = new();

    /// <summary>
    /// Promotion-apply flow config (Pass 24). Controls whether
    /// <c>POST /api/promotion/apply</c> is allowed to write durable
    /// staging artifacts (template + rollback marker + audit packet)
    /// for a candidate promotion. Default is <see cref="PromotionApplyOptions.AllowApply"/>=false —
    /// the promotion pipeline is observation-only out of the box. The
    /// apply verb never mutates source code; it writes to
    /// <see cref="PromotionApplyOptions.StagingRoot"/> so a human
    /// reviewer can cherry-pick the change and commit it. Rollback is
    /// deletion of the staging artifacts.
    /// </summary>
    public PromotionApplyOptions PromotionApply { get; set; } = new();

    /// <summary>
    /// Pass 25 / D1 — hardware profile override. When
    /// <see cref="HardwareOptions.ForceTier"/> names a valid
    /// <c>DuoHardwareTier</c> enum value, the /api/hardware surface
    /// reports that tier as the effective tier regardless of
    /// detection. Empty or unparsable values are ignored and the
    /// detected tier wins.
    /// </summary>
    public HardwareOptions Hardware { get; set; } = new();

    /// <summary>
    /// Declarative role bindings for the local-first AI mesh. Each entry
    /// maps one of the five <c>ModelRole</c> values (Edge / Worker / Judge
    /// / Media / Validator) to a named endpoint + model pair. Used by
    /// <c>ModelRoleRegistry</c> to compute coverage, by <c>/api/roles</c>
    /// to report the configured mesh, and by <c>/api/quickstart</c> to
    /// nudge operators toward a stronger pairing.
    ///
    /// <para>The list is metadata-only today: binding a role does not
    /// automatically route inference traffic to that endpoint. It records
    /// intent so the mesh is legible to operators, AI clients, and
    /// validators. Future passes can add role-aware routing on top
    /// without changing the operator-facing shape.</para>
    /// </summary>
    public List<ModelRoleBinding> ModelRoles { get; set; } = new();

    public FallbackOptions Fallback { get; set; } = new();

    public VisionOptions Vision { get; set; } = new();

    public SessionOptions Session { get; set; } = new();

    public TtsOptions Tts { get; set; } = new();

    public AsrOptions Asr { get; set; } = new();

    public AutomationOptions Automation { get; set; } = new();

    public HttpSurfaceOptions Http { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public McpClientOptions McpClient { get; set; } = new();

    /// <summary>Pack resolution policy — currently the per-species personality-pack
    /// default map, so operators can pin one pack per Palworld species without
    /// authoring one pack per character id. See <see cref="PacksOptions"/>.</summary>
    public PacksOptions Packs { get; set; } = new();

    public string RuntimeRoot => Path.Combine(PalSavedRoot, RuntimeFolderName);

    /// <summary>
    /// Resolves the directory PalLLM treats as the canonical model library.
    /// When <see cref="ExternalModelsRoot"/> is set, that absolute path wins;
    /// otherwise the legacy runtime-root-anchored <c>Models</c> subdirectory is
    /// used. PalLLM never writes to this directory automatically — it's read by
    /// connector scripts and by any future "where do local GGUFs live?" advisor.
    /// </summary>
    public string ModelsDir => string.IsNullOrWhiteSpace(ExternalModelsRoot)
        ? Path.Combine(RuntimeRoot, "Models")
        : ExternalModelsRoot.Trim();

    /// <summary>
    /// Resolves the directory PalLLM treats as the canonical diffusion-model
    /// library (Stable Diffusion / Flux / Hunyuan / etc. weights for the
    /// future portrait-variant + scene-narration lane described in
    /// <c>docs/FUTURE_2035.md</c> idea #15). Always a <c>Diffusion</c>
    /// subdirectory of <see cref="ModelsDir"/> so it automatically tracks any
    /// <see cref="ExternalModelsRoot"/> override the operator sets — no
    /// separate config knob to keep in sync. Like <see cref="ModelsDir"/>,
    /// PalLLM never writes to this directory automatically; the diffusion
    /// endpoint owns its own weights file lifecycle.
    /// </summary>
    public string DiffusionModelsDir => Path.Combine(ModelsDir, "Diffusion");

    public string PackDir => Path.Combine(RuntimeRoot, "Packs");

    public string TtsDir => Path.Combine(RuntimeRoot, "TTS");

    public string BridgeRoot => Path.Combine(RuntimeRoot, "Bridge");

    public string BridgeInboxDir => Path.Combine(BridgeRoot, "Inbox");

    public string BridgeArchiveDir => Path.Combine(BridgeRoot, "Archive");

    public string BridgeFailedDir => Path.Combine(BridgeRoot, "Failed");

    public string BridgeOutboxDir => Path.Combine(BridgeRoot, "Outbox");

    public string BridgeScreenshotsDir => Path.Combine(BridgeRoot, "Screenshots");

    public string BridgeDiagnosticsDir => Path.Combine(BridgeRoot, "Diagnostics");

    public string ReleaseEvidenceDir => Path.Combine(RuntimeRoot, "ReleaseEvidence");

    public string ReleaseEvidenceHistoryDir => Path.Combine(ReleaseEvidenceDir, "History");

    public string SupportEvidenceDir => Path.Combine(RuntimeRoot, "SupportEvidence");

    public string SupportEvidenceHistoryDir => Path.Combine(SupportEvidenceDir, "History");

    public string LatestSmokeEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-smoke.json");

    public string LatestNativeProofEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-native-proof.json");

    public string LatestProofBundleEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-proof-bundle.json");

    public string LatestProofBundleArchivePath => Path.Combine(ReleaseEvidenceDir, "latest-proof-bundle.zip");

    public string LatestPackageVerificationEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-package-verification.json");

    public string LatestArtifactIntegrityEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-artifact-integrity.json");

    public string LatestFullAuditEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-full-audit.json");

    public string LatestSupportBundleEvidencePath => Path.Combine(SupportEvidenceDir, "latest-support-bundle.json");

    public string LatestSupportBundleArchivePath => Path.Combine(SupportEvidenceDir, "latest-support-bundle.zip");

    /// <summary>
    /// Maximum age, in hours, before release proof artifacts should be treated as
    /// stale and refreshed before a candidate package is trusted.
    /// </summary>
    public int ReleaseEvidenceFreshnessHours { get; set; } = 24;

    public string SessionFilePath => Path.Combine(RuntimeRoot, "session.json");

    private int _directoriesEnsured;

    public void EnsureDirectories()
    {
        // First call creates every runtime-owned directory. Subsequent calls are
        // free — the hot paths (every chat, every bridge drain, every screenshot
        // tick) all call this, and the directories outlive the sidecar once
        // they exist. Write sites still handle DirectoryNotFoundException, so a
        // user who deletes a directory mid-run just sees the next write retry
        // via the write site's own error handling.
        //
        // ModelsDir is intentionally NOT created here — PalLLM talks to
        // HTTP-reachable inference endpoints, so there is no local weights file
        // to store. It remains on the IPathProvider surface for portable
        // adapter-library compatibility; a consumer that actually downloads
        // models can create the directory lazily at write time.
        if (Interlocked.CompareExchange(ref _directoriesEnsured, 1, 0) != 0)
        {
            return;
        }

        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(PackDir);
        Directory.CreateDirectory(TtsDir);
        Directory.CreateDirectory(BridgeInboxDir);
        Directory.CreateDirectory(BridgeArchiveDir);
        Directory.CreateDirectory(BridgeFailedDir);
        Directory.CreateDirectory(BridgeOutboxDir);
        Directory.CreateDirectory(BridgeScreenshotsDir);
        Directory.CreateDirectory(BridgeDiagnosticsDir);
        Directory.CreateDirectory(ReleaseEvidenceHistoryDir);
        Directory.CreateDirectory(SupportEvidenceHistoryDir);
    }

    /// <summary>Forces the next <see cref="EnsureDirectories"/> call to re-create
    /// all runtime directories. Tests use this so each fixture's tmp root gets a
    /// fresh layout; production code should not call it during normal operation.</summary>
    public void ResetDirectoryCache() => Interlocked.Exchange(ref _directoriesEnsured, 0);
}

/// <summary>
/// Pack resolution policy. Currently exposes a single map: Palworld species name
/// (case-insensitive) -> personality-pack id. Lets operators pin one pack per
/// species (e.g. all same-species companions share the same timid-helper voice) without having
/// to author one pack per character id. Consumed by
/// <c>SpeciesPersonalityResolver.Resolve</c>; missing / empty entries are silently
/// skipped and the resolver falls through to the caller's fallback chain.
/// </summary>
public sealed class PacksOptions
{
    /// <summary>
    /// Species -> packId default map. Both keys and values are trimmed and
    /// empty/whitespace entries are ignored at resolve-time. Keys are matched
    /// case-insensitively (so <c>"species-alpha"</c> and <c>"Species-Alpha"</c> work the
    /// same). Values reference pack ids under
    /// <c>runtime-root/Packs/personalities/&lt;id&gt;/</c>. Empty dictionary
    /// disables the species-default lane entirely (default).
    /// </summary>
    public Dictionary<string, string> DefaultBySpecies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BridgeOptions
{
    public bool Enabled { get; set; } = true;

    public int PollIntervalMs { get; set; } = 1_000;

    /// Upper bound on how many inbox events the background worker processes in one
    /// poll. Keeps long-running sessions responsive even if a producer dumps a large
    /// backlog into Bridge/Inbox; manual drains can still process the full queue.
    public int MaxEventsPerPoll { get; set; } = 32;

    /// Hard cap on a single Bridge/Inbox JSON envelope. Prevents a malformed or
    /// hostile local producer from forcing the bridge drain to deserialize
    /// arbitrarily large event files into memory on the hot filesystem ingest path.
    public int MaxInboxEventBytes { get; set; } = 65_536;

    public bool ArchiveProcessedEvents { get; set; } = true;

    /// Enables the reply return-channel. When true, every successful chat response is
    /// persisted as a JSON envelope in the Bridge/Outbox directory so UE4SS (or any
    /// other game-side consumer) can render the assistant message and presentation
    /// cues in-game without calling back into the sidecar.
    public bool OutboxEnabled { get; set; } = true;

    /// Retention cap for the outbox directory. Prevents unbounded growth when a
    /// game-side consumer isn't running. On write, the oldest files beyond this cap
    /// are deleted so the outbox never exceeds the configured size.
    public int OutboxMaxFiles { get; set; } = 100;

    /// Max age (hours) for outbox files. Files older than this are pruned on write.
    public int OutboxMaxAgeHours { get; set; } = 24;

    /// Retention cap for the archive directory. Bridge events and processed
    /// screenshots both archive here, so the cap is higher than the outbox's.
    public int ArchiveMaxFiles { get; set; } = 500;

    public int ArchiveMaxAgeHours { get; set; } = 72;

    /// Retention cap for the failed directory. Failures should be rare; keeping the
    /// last few hundred is enough for diagnostic pullback without letting a runaway
    /// producer pack the disk.
    public int FailedMaxFiles { get; set; } = 200;

    public int FailedMaxAgeHours { get; set; } = 168;

    /// Retention cap for widget-probe diagnostics. These dumps are useful for
    /// HUD discovery, but should stay bounded so repeated probe sessions do not
    /// silently accumulate under Bridge/Diagnostics forever.
    public int DiagnosticsMaxFiles { get; set; } = 128;

    public int DiagnosticsMaxAgeHours { get; set; } = 168;
}

public sealed class InferenceOptions
{
    public bool Enabled { get; set; }

    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1/";

    public string Model { get; set; } = "qwen3.6:35b-a3b";

    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional vLLM-compatible prefix-cache trust-domain salt. When set,
    /// PalLLM forwards it as <c>cache_salt</c> on chat-completions requests so
    /// a shared model server can reuse cache inside one operator-approved
    /// trust domain without reusing cached prefixes across unrelated domains.
    /// Leave empty for maximum OpenAI-compatible endpoint portability.
    /// </summary>
    public string? PrefixCacheSalt { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible prompt-cache routing key. Leave empty for
    /// maximum endpoint portability. When configured for a compatible hosted
    /// endpoint, PalLLM forwards it as <c>prompt_cache_key</c> so repeated
    /// PalLLM prompts can route toward warmer prefix-cache shards without
    /// exposing player or save identifiers directly.
    /// </summary>
    public string? PromptCacheKey { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible prompt-cache retention policy. Leave empty so
    /// the endpoint applies its own default; set to <c>in_memory</c> or
    /// <c>24h</c> only after the exact endpoint/model proves support.
    /// </summary>
    public string? PromptCacheRetention { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible verbosity hint. Leave empty for local-runtime
    /// portability. Set to <c>low</c> only after the endpoint proves it accepts
    /// the field and actually reduces generated tokens without harming parse or
    /// companion quality.
    /// </summary>
    public string? Verbosity { get; set; }

    /// <summary>
    /// Optional hosted-endpoint safety correlation id. This should be a stable,
    /// pseudonymous hash scoped to the PalLLM install/profile, never a player
    /// name, save path, account id, email, or secret. Omitted by default.
    /// </summary>
    public string? SafetyIdentifier { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible retention switch forwarded as <c>store</c>.
    /// Leave empty for local-runtime portability. Set to <c>false</c> only
    /// after the hosted endpoint proves it accepts the explicit no-store
    /// receipt; avoid <c>true</c> for normal Palworld companion turns.
    /// </summary>
    public bool? StoreCompletions { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible request metadata forwarded as
    /// <c>metadata</c>. Use only low-cardinality proof labels such as route
    /// family, build channel, or canary name; never include player identity,
    /// save paths, prompt text, secrets, or raw game state. Omitted by default.
    /// </summary>
    public Dictionary<string, string> RequestMetadata { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Optional outbound HTTP request-correlation header for compatible
    /// inference endpoints. Leave empty for maximum local-runtime portability.
    /// Set to <c>x-client-request-id</c> for hosted OpenAI-compatible support
    /// traces or <c>x-request-id</c> for vLLM servers launched with request-id
    /// header support; PalLLM forwards only the current bounded chat/proof
    /// request id, never prompt or save content.
    /// </summary>
    public string? ClientRequestIdHeader { get; set; }

    /// <summary>
    /// Optional llama.cpp prompt-cache toggle forwarded as <c>cache_prompt</c>.
    /// Leave null for broad endpoint portability and llama.cpp's server default;
    /// set only on a proven llama-server lane when measuring prefix reuse.
    /// </summary>
    public bool? LlamaCppCachePrompt { get; set; }

    /// <summary>
    /// Optional llama.cpp slot selector forwarded as <c>id_slot</c>. Leave null
    /// unless the target llama-server exposes slots and a replay proves that
    /// pinning the foreground companion lane to a warm slot lowers TTFT without
    /// starving background work.
    /// </summary>
    public int? LlamaCppSlotId { get; set; }

    /// <summary>
    /// Optional llama.cpp prompt-cache reuse floor forwarded as
    /// <c>n_cache_reuse</c>. Leave null unless a llama-server lane has measured
    /// the exact stable prefix length it should try to reuse.
    /// </summary>
    public int? LlamaCppCacheReuseTokens { get; set; }

    /// <summary>
    /// Adds stable content-hash <c>uuid</c> fields to prompt-level
    /// <c>InferencePrompt.UserContent</c> media parts that carry local base64
    /// image/video/audio data. This helps vLLM-compatible multimodal servers
    /// reuse media preprocessing across replay/proof turns while leaving normal
    /// text chat as a plain string message.
    /// </summary>
    public bool UseMediaCacheIds { get; set; } = true;

    /// <summary>
    /// Optional vLLM-style multimodal processor kwargs for route-owned
    /// <see cref="PalLLM.Domain.Inference.InferencePrompt.UserContent"/>
    /// canaries. Omitted unless a prompt supplies multimodal content so normal
    /// text chat and strict endpoints remain field-free.
    /// </summary>
    public MultimodalProcessorOptions MultimodalProcessor { get; set; } = new();

    /// <summary>
    /// Baseline chat-completions sampling temperature. Sidecar startup
    /// validation accepts finite values from <c>0</c> through <c>2</c>.
    /// </summary>
    public float Temperature { get; set; } = 0.7f;

    /// <summary>
    /// Optional nucleus-sampling cap forwarded as <c>top_p</c>. Sidecar startup
    /// validation accepts finite values from <c>0</c> through <c>1</c>.
    /// </summary>
    public float? TopP { get; set; } = 0.8f;

    /// <summary>
    /// Optional OpenAI-compatible presence penalty. Sidecar startup validation
    /// accepts finite values from <c>-2</c> through <c>2</c>.
    /// </summary>
    public float? PresencePenalty { get; set; } = 1.5f;

    /// <summary>
    /// Selects the chat-completions token-budget field PalLLM emits. The
    /// default <c>max_tokens</c> keeps broad local-runtime compatibility;
    /// <c>max_completion_tokens</c> is opt-in for endpoint-proven reasoning
    /// lanes that reject the older field.
    /// </summary>
    public string TokenBudgetField { get; set; } = InferenceTokenBudgetFields.MaxTokens;

    /// <summary>
    /// Optional OpenAI-compatible frequency penalty. Leave empty unless the exact
    /// endpoint/model accepts <c>frequency_penalty</c>; useful for replay-proven
    /// repetition control without changing PalLLM's deterministic fallback path.
    /// </summary>
    public float? FrequencyPenalty { get; set; }

    /// <summary>
    /// Optional local-runtime top-k sampler hint. Leave empty unless the exact
    /// endpoint/model accepts <c>top_k</c>; strict OpenAI-compatible endpoints
    /// can reject non-standard sampler fields.
    /// </summary>
    public int? TopK { get; set; }

    /// <summary>
    /// Optional local-runtime min-p sampler hint. Leave empty unless the exact
    /// endpoint/model accepts <c>min_p</c>; useful for endpoint-proven creative
    /// lanes without making model-family sampler defaults global.
    /// </summary>
    public float? MinP { get; set; }

    /// <summary>
    /// Optional local-runtime repetition penalty. Leave empty unless the exact
    /// endpoint/model accepts <c>repetition_penalty</c>; <c>1.0</c> is normally
    /// neutral, values above one discourage repetition.
    /// </summary>
    public float? RepetitionPenalty { get; set; }

    public bool? EnableThinking { get; set; } = false;

    /// <summary>
    /// Optional OpenAI-compatible reasoning-effort hint for reasoning-capable
    /// endpoints. Leave empty unless the exact local/server endpoint has been
    /// probed, because unsupported endpoints commonly reject unknown request
    /// fields instead of ignoring them.
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// Optional vLLM-compatible cap on reasoning/thinking tokens for models
    /// launched with a reasoning parser. Leave empty for maximum endpoint
    /// portability; use <c>EnableThinking=false</c> instead of <c>0</c> when a
    /// route should avoid reasoning entirely.
    /// </summary>
    public int? ThinkingTokenBudget { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible seed hint for replay-oriented deterministic
    /// sampling. Leave empty unless the exact endpoint/model accepts <c>seed</c>;
    /// unsupported servers may reject unknown request fields.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>
    /// Optional vLLM-compatible request scheduling priority. Leave empty unless
    /// the exact endpoint is launched with priority scheduling; unsupported or
    /// FCFS-only servers may reject non-zero priority values.
    /// </summary>
    public int? RequestPriority { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible service-tier hint for endpoint-proven routing
    /// lanes. Leave empty for local-first portability. Use <c>priority</c> only
    /// when a compatible endpoint has proven lower queue time for player-facing
    /// turns, and <c>flex</c> only for background proof/docs lanes that can wait.
    /// </summary>
    public string? ServiceTier { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible tool-call fan-out hint. Leave empty unless the
    /// exact endpoint accepts <c>parallel_tool_calls</c>; set to <c>false</c>
    /// only after proving strict action/directive routes should emit at most one
    /// tool call.
    /// </summary>
    public bool? ParallelToolCalls { get; set; }

    /// <summary>
    /// Optional OpenAI-compatible stop sequences forwarded on chat-completions
    /// requests. Leave empty for maximum endpoint portability. When configured,
    /// PalLLM sends up to four trimmed delimiters as <c>stop</c> so proven
    /// local runtimes can end strict or low-latency replies before wasting
    /// tokens past a route boundary.
    /// </summary>
    public List<string> StopSequences { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Hard cap on the returned chat-completions payload size. Prevents a
    /// verbose or misconfigured upstream from buffering arbitrarily large JSON
    /// bodies into the sidecar while the hot chat lane is parsing them.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Hard cap on model-catalog probe payloads such as <c>/v1/models</c>,
    /// Foundry Local <c>/openai/models</c>, and <c>/api/tags</c>. These lists
    /// are larger than normal chat replies but still need a bound so tier
    /// discovery cannot buffer arbitrarily large JSON bodies when a local
    /// endpoint misbehaves.
    /// </summary>
    public int ModelCatalogMaxResponseBytes { get; set; } = 256 * 1024;

    /// Consecutive failures that trip the circuit breaker. When breached, subsequent
    /// chat requests skip the HTTP call and fall through to the deterministic fallback
    /// director without paying network timeout cost. Set to 0 to disable.
    public int CircuitBreakerFailureThreshold { get; set; } = 5;

    /// How long the breaker stays open before a single trial call is allowed through.
    public int CircuitBreakerCooldownSeconds { get; set; } = 30;

    /// Single-retry policy for transient failures (network hiccup, 5xx, timeout).
    /// One retry is plenty for a local inference server that may be warming a
    /// model into memory on first use. Set to 0 to disable. Deterministic 4xx
    /// responses are never retried.
    public int MaxTransientRetries { get; set; } = 1;

    /// Base backoff in ms before a retry. Each retry adds jitter up to this value
    /// again so concurrent PalLLM instances don't synchronize their retries on
    /// the same endpoint.
    public int TransientRetryBackoffMs { get; set; } = 500;

    /// <summary>
    /// Optional residency-control provider. <see cref="InferenceResidencyProvider.Auto"/>
    /// detects compatible local runtimes from <see cref="BaseUrl"/> and only then
    /// emits provider-specific residency hints; <see cref="InferenceResidencyProvider.Disabled"/>
    /// suppresses them entirely.
    /// </summary>
    public InferenceResidencyProvider ResidencyProvider { get; set; } = InferenceResidencyProvider.Auto;

    /// <summary>
    /// Optional model-residency TTL in seconds for compatible local runtimes.
    /// <c>0</c> disables residency hints. LM Studio OpenAI-compatible routes map
    /// this to the documented <c>ttl</c> request field; Ollama native warmup maps
    /// it to <c>keep_alive</c>.
    /// </summary>
    public int ResidencyTtlSeconds { get; set; } = 1_800;

    /// <summary>
    /// Enables the bounded model-warmup pass. When true and inference is also
    /// enabled, PalLLM primes the currently active model on startup and after
    /// tier graduations so the first real player turn is less likely to pay the
    /// full model-load penalty.
    /// </summary>
    public bool EnableWarmup { get; set; } = true;

    /// <summary>
    /// Tiny token budget used for warmup requests. Keep this small - the point
    /// is to trigger model load / graph compilation / cache priming, not to do
    /// meaningful work or burn remote-provider tokens.
    /// </summary>
    public int WarmupMaxTokens { get; set; } = 1;

    /// <summary>
    /// Optional periodic keep-alive cadence in seconds. Set to 0 (default) to
    /// disable periodic keep-alives and only warm on startup plus tier changes.
    /// Raise above 0 when your local inference server unloads models after idle
    /// periods and you want PalLLM to keep the active tier resident.
    /// </summary>
    public int WarmupIntervalSeconds { get; set; }

    /// <summary>
    /// Optional ordered model-tier list. When present, PalLLM probes the
    /// configured inference endpoint to see which tier models are actually
    /// available and uses the highest-priority available tier on every chat
    /// request. A background worker re-probes on a cadence so the sidecar
    /// graduates from the small "instant" tier (e.g. <c>gemma3:4b</c>) to
    /// the large "quality" tier (e.g. an Unsloth dynamic quant of a 35B
    /// Qwen-style MoE) the moment the larger model finishes downloading or
    /// warming in the endpoint — the player gets working replies from the
    /// first second of the session and automatically upgrades to better
    /// replies once the heavy tier is ready, without manual config editing.
    /// Empty list (default) disables tier orchestration and <see cref="Model"/>
    /// is used verbatim for every request.
    /// </summary>
    public List<ModelTierOptions> ModelTiers { get; set; } = new();

    /// <summary>
    /// How often the background worker re-probes the inference endpoint for
    /// tier availability changes. Shorter = faster graduation when the large
    /// model finishes loading; longer = less chatter at the endpoint. Ignored
    /// when <see cref="ModelTiers"/> is empty.
    /// </summary>
    public int TierProbeIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Opt-in GPU thermal-gate settings. When <see cref="ThermalGateOptions.Enabled"/>
    /// is <c>true</c> and a sensor is reachable, chat requests that would hit
    /// live inference while the primary GPU is at or above
    /// <see cref="ThermalGateOptions.RejectAboveC"/> short-circuit to the
    /// deterministic fallback director instead of running a throttled round-trip
    /// that slows every turn by the full throttle amount. Off by default to
    /// match PalLLM's every-opt-in-is-off shipping posture.
    /// </summary>
    public ThermalGateOptions ThermalGate { get; set; } = new();
}

/// <summary>
/// Shared allowlist for optional reasoning-effort request hints. These values
/// cover the common OpenAI-compatible chat-completions spellings observed on
/// current reasoning-capable endpoints while keeping typoed config fail-fast.
/// </summary>
public static class InferenceReasoningEfforts
{
    /// <summary>Config values PalLLM will forward as <c>reasoning_effort</c>.</summary>
    public static readonly string[] Allowed =
    [
        "none",
        "minimal",
        "low",
        "medium",
        "high",
        "xhigh",
        "max",
    ];

    /// <summary>Trims and lowercases a known value; returns <c>null</c> for empty or unknown values.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    /// <summary>Returns whether <paramref name="value"/> is a known reasoning-effort hint.</summary>
    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shared allowlist for the mutually exclusive chat-completions output-token
/// budget fields. Keeping this narrow prevents a typo from silently removing
/// PalLLM's response-length bound on upstream requests.
/// </summary>
public static class InferenceTokenBudgetFields
{
    public const string MaxTokens = "max_tokens";

    public const string MaxCompletionTokens = "max_completion_tokens";

    public static readonly string[] Allowed =
    [
        MaxTokens,
        MaxCompletionTokens,
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return MaxTokens;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : MaxTokens;
    }

    public static bool UsesMaxCompletionTokens(string? value) =>
        string.Equals(Normalize(value), MaxCompletionTokens, StringComparison.Ordinal);

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shared allowlist for optional OpenAI-compatible service-tier request hints.
/// The field stays omitted by default because most local runtimes either ignore
/// it or reject unknown parameters, and PalLLM is local-first unless an operator
/// explicitly qualifies a hosted or compatible lane.
/// </summary>
public static class InferenceServiceTiers
{
    public const string Auto = "auto";

    public const string Default = "default";

    public const string Flex = "flex";

    public const string Priority = "priority";

    public const string Scale = "scale";

    public static readonly string[] Allowed =
    [
        Auto,
        Default,
        Flex,
        Priority,
        Scale,
    ];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shared allowlist for optional hosted prompt-cache retention request hints.
/// The field is omitted by default because strict local endpoints commonly
/// reject unknown request fields.
/// </summary>
public static class InferencePromptCacheRetentions
{
    public const string InMemory = "in_memory";

    public const string TwentyFourHours = "24h";

    public static readonly string[] Allowed =
    [
        InMemory,
        TwentyFourHours,
    ];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shared allowlist for optional outbound request-correlation headers. The
/// field stays omitted by default because PalLLM is local-first and strict
/// local endpoints do not need an extra support header.
/// </summary>
public static class InferenceClientRequestIdHeaders
{
    public const string XClientRequestId = "x-client-request-id";

    public const string XRequestId = "x-request-id";

    public static readonly string[] Allowed =
    [
        XClientRequestId,
        XRequestId,
    ];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shared bounds for optional OpenAI-compatible chat-completions
/// <c>metadata</c>. Mirrors the current hosted field shape and keeps any
/// configured request labels small enough for strict proof receipts.
/// </summary>
public static class InferenceRequestMetadataLimits
{
    public const int MaxEntries = 16;

    public const int MaxKeyLength = 64;

    public const int MaxValueLength = 512;
}

/// <summary>
/// Optional vLLM-compatible <c>mm_processor_kwargs</c> request controls for
/// multimodal proof lanes. The object is omitted unless at least one value is
/// configured, so strict OpenAI-compatible endpoints never see these
/// non-standard fields by default.
/// </summary>
public sealed class MultimodalProcessorOptions
{
    /// <summary>
    /// Qwen/VL-style minimum pixel budget. Useful when a route needs to avoid
    /// over-compressing a small HUD crop before OCR or coordinate review.
    /// </summary>
    [JsonPropertyName("min_pixels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinPixels { get; set; }

    /// <summary>
    /// Qwen/VL-style maximum pixel budget. Lower values reduce vision tokens,
    /// TTFT, and KV pressure on screenshot/video canaries.
    /// </summary>
    [JsonPropertyName("max_pixels")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPixels { get; set; }

    /// <summary>
    /// Gemma-style maximum soft-token budget per image. Typical proven values
    /// are 70, 140, 280, 560, or 1120.
    /// </summary>
    [JsonPropertyName("max_soft_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxSoftTokens { get; set; }

    /// <summary>
    /// Video processor frame-rate hint. Keep low for periodic Palworld
    /// screenshot/video proof loops unless a route proves it needs more
    /// temporal detail.
    /// </summary>
    [JsonPropertyName("fps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public float? Fps { get; set; }

    [JsonIgnore]
    public bool HasAny =>
        MinPixels.HasValue ||
        MaxPixels.HasValue ||
        MaxSoftTokens.HasValue ||
        Fps.HasValue;
}

/// <summary>
/// Shared allowlist for optional OpenAI-compatible verbosity request hints.
/// The field is omitted by default because many local runtimes reject hosted
/// request parameters instead of ignoring them.
/// </summary>
public static class InferenceVerbosities
{
    public const string Low = "low";

    public const string Medium = "medium";

    public const string High = "high";

    public static readonly string[] Allowed =
    [
        Low,
        Medium,
        High,
    ];

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : null;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Shared allowlist for the HTTP payload shape used by
/// <see cref="PalLLM.Domain.Inference.HttpTtsClient"/>. The default legacy
/// shape stays tiny and broadly compatible, while <see cref="OpenAiSpeech"/>
/// targets OpenAI-compatible <c>/v1/audio/speech</c> endpoints such as current
/// vLLM-Omni TTS lanes.
/// </summary>
public static class TtsRequestFormats
{
    public const string Simple = "simple";

    public const string OpenAiSpeech = "openai_speech";

    public static readonly string[] Allowed =
    [
        Simple,
        OpenAiSpeech,
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Simple;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : Simple;
    }

    public static bool UsesOpenAiSpeech(string? value) =>
        string.Equals(Normalize(value), OpenAiSpeech, StringComparison.Ordinal);

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Response formats PalLLM will request from OpenAI-compatible speech
/// endpoints. Kept narrow so a typo does not silently ask a strict voice server
/// for an unsupported container.
/// </summary>
public static class TtsResponseFormats
{
    public const string Wav = "wav";

    public static readonly string[] Allowed =
    [
        Wav,
        "mp3",
        "opus",
        "aac",
        "flac",
        "pcm",
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Wav;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : Wav;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string ToMimeType(string? value) =>
        Normalize(value) switch
        {
            "mp3" => "audio/mpeg",
            "opus" => "audio/opus",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            "pcm" => "audio/pcm",
            _ => "audio/wav",
        };
}

/// <summary>
/// Audio MIME types PalLLM accepts on the ASR proof lane. Kept narrow so
/// caller-supplied media stays predictable before it is forwarded to an
/// OpenAI-compatible transcription endpoint.
/// </summary>
public static class AsrAudioMimeTypes
{
    public const string Wav = "audio/wav";

    public static readonly string[] Allowed =
    [
        Wav,
        "audio/x-wav",
        "audio/mpeg",
        "audio/mp3",
        "audio/flac",
        "audio/ogg",
        "audio/opus",
        "audio/webm",
        "audio/mp4",
        "audio/x-m4a",
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Wav;
        }

        string trimmed = value.Trim().ToLowerInvariant();
        return IsAllowed(trimmed) ? trimmed : Wav;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);

    public static string ToFileName(string? value) =>
        Normalize(value) switch
        {
            "audio/mpeg" or "audio/mp3" => "audio.mp3",
            "audio/flac" => "audio.flac",
            "audio/ogg" => "audio.ogg",
            "audio/opus" => "audio.opus",
            "audio/webm" => "audio.webm",
            "audio/mp4" or "audio/x-m4a" => "audio.m4a",
            _ => "audio.wav",
        };
}

/// <summary>
/// Response formats PalLLM will request from OpenAI-compatible transcription
/// endpoints. Both allowed values keep a top-level <c>text</c> field so the
/// runtime can parse transcripts without retaining token or segment text.
/// </summary>
public static class AsrResponseFormats
{
    public const string Json = "json";

    public const string VerboseJson = "verbose_json";

    public static readonly string[] Allowed =
    [
        Json,
        VerboseJson,
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Json;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : Json;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Optional timestamp granularities for OpenAI-compatible ASR verbose-json
/// canaries. Kept separate from <see cref="AsrResponseFormats"/> because
/// strict endpoints commonly reject timestamp fields unless
/// <c>response_format=verbose_json</c> has already been proven.
/// </summary>
public static class AsrTimestampGranularities
{
    public const string Segment = "segment";

    public const string Word = "word";

    public static readonly string[] Allowed =
    [
        Segment,
        Word,
    ];

    public static string[] NormalizeMany(IEnumerable<string>? values) =>
        values is null
            ? []
            : values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(IsAllowed)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Optional OpenAI-compatible file-transcription chunking strategies. Empty
/// keeps the request field-free for strict local ASR endpoints; <c>auto</c>
/// lets compatible endpoints use their own VAD-based chunk boundary picker.
/// </summary>
public static class AsrChunkingStrategies
{
    public const string Auto = "auto";

    public static readonly string[] Allowed =
    [
        Auto,
    ];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        return IsAllowed(trimmed) ? trimmed.ToLowerInvariant() : string.Empty;
    }

    public static bool IsAllowed(string value) =>
        Allowed.Contains(value, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Configuration for the opt-in <c>ThermalGate</c> in
/// <c>PalLLM.Domain.Runtime</c>. All fields are safe to leave at the default;
/// the gate is only consulted when <see cref="Enabled"/> is <c>true</c>.
/// </summary>
public sealed class ThermalGateOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Temperature (°C) at which live inference is gated to the fallback
    /// director. Conservative default for consumer NVIDIA cards; most
    /// desktops begin thermal throttling a few degrees above this point, so
    /// gating here keeps the player-visible latency budget predictable.
    /// </summary>
    public double RejectAboveC { get; set; } = 83.0;

    /// <summary>
    /// Temperature (°C) at which the gate surfaces a "warm" warning in
    /// <c>/api/inference/performance</c> and the Field Console ribbon
    /// without rejecting calls.
    /// </summary>
    public double WarnAboveC { get; set; } = 78.0;

    /// <summary>
    /// How long a successful sensor read is trusted before resampling. Set
    /// to a value at or below the typical chat cadence so a thermal spike
    /// can't hide behind a stale read.
    /// </summary>
    public int CacheTtlSeconds { get; set; } = 5;
}

/// <summary>
/// Selects which provider-specific residency-control hints PalLLM may emit for
/// compatible local inference runtimes.
/// </summary>
public enum InferenceResidencyProvider
{
    /// <summary>
    /// Detect the provider from <see cref="InferenceOptions.BaseUrl"/> and only
    /// emit hints for known compatible runtimes.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Disable provider-specific residency hints even when the endpoint is a
    /// known compatible runtime.
    /// </summary>
    Disabled = 1,

    /// <summary>
    /// Treat the endpoint as LM Studio-compatible and use TTL hints on
    /// chat-completions requests when possible.
    /// </summary>
    LmStudio = 3,

    // Pass 346: enum value 2 (Ollama) removed. The runtime no longer ships
    // an Ollama-aware residency path; llama-server (PalLLM's bundled default)
    // keeps the loaded model resident for the lifetime of the server process,
    // so no per-request keep-alive hint is needed. Operators with
    // ResidencyProvider:"Ollama" in their existing config will fall back to
    // Auto detection at bind time; in practice this turns into Disabled
    // unless the BaseUrl host matches the LM Studio pattern.
}

/// <summary>
/// A single tier in the model-availability cascade. Tiers carry a priority —
/// the orchestrator picks the highest-priority tier whose <see cref="Model"/>
/// tag is reported as available by the inference endpoint. Ties are broken
/// by list order (earlier wins). A typical two-tier config for a local
/// Ollama deployment pairs a ~4B parameter instant-start model (<c>small</c>,
/// priority 1) with a ~35B quality model (<c>large</c>, priority 10): the
/// sidecar uses the small one while the large one is still being pulled,
/// then graduates to the large one once it is pulled and loaded.
/// </summary>
public sealed class ModelTierOptions
{
    /// <summary>Human-readable tier id (e.g. <c>small</c>, <c>large</c>,
    /// <c>vision</c>). Surfaced in health probes, traces, and logs so
    /// operators can see which tier a reply came from.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The exact model tag the inference endpoint expects. For
    /// Ollama this is the <c>name:tag</c> identifier; for other OpenAI-
    /// compatible servers this is the <c>id</c> surfaced on <c>/v1/models</c>.
    /// For Foundry Local lanes this is the loaded alias or <c>/openai/models</c>
    /// id.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Higher = preferred. The orchestrator picks the highest-priority
    /// tier that is currently available. Use non-contiguous values (1, 10, 100)
    /// so new tiers can be inserted between existing ones without re-numbering.</summary>
    public int Priority { get; set; }

    /// <summary>Optional description. Not consumed by the runtime — helps
    /// operators reading the config understand why the tier exists.</summary>
    public string? Description { get; set; }
}

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
    /// exactly (ordinal comparison). When null or empty (default) the
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

public sealed class TtsOptions
{
    /// Off by default — TTS needs a separate HTTP server that accepts
    /// <c>POST { "text", "voice" }</c> and returns audio bytes. Any server that
    /// follows that shape is supported. When disabled, the runtime and endpoints
    /// return a graceful "not configured" response so callers can check Success
    /// without catching.
    public bool Enabled { get; set; }

    /// Configured TTS endpoint. PalLLM's default implementation POSTs JSON
    /// <c>{ "text", "voice" }</c> and expects audio bytes in the response body.
    /// The default URL is a placeholder; supply your own. Swap the
    /// implementation for other server shapes by binding a different
    /// <c>ITtsClient</c> in DI.
    public string BaseUrl { get; set; } = "http://127.0.0.1:5002/synthesize";

    /// Request JSON shape for the configured endpoint. Default <c>simple</c>
    /// preserves existing local adapters. Set to <c>openai_speech</c> for
    /// OpenAI-compatible speech routes such as vLLM-Omni Qwen3-TTS.
    public string RequestFormat { get; set; } = TtsRequestFormats.Simple;

    /// Optional model id sent only by the <c>openai_speech</c> request shape.
    /// Some local endpoints infer the served model from the server, while
    /// stricter OpenAI-compatible providers require this field.
    public string? Model { get; set; }

    public string DefaultVoice { get; set; } = "en_US-amy-medium";

    /// Audio container requested from OpenAI-compatible speech endpoints.
    /// Ignored by the default <c>simple</c> adapter shape.
    public string ResponseFormat { get; set; } = TtsResponseFormats.Wav;

    /// Optional softer voice used for cozy, companion, or reassurance-forward
    /// cue plans when the backing TTS server exposes multiple voices.
    public string? WarmVoice { get; set; }

    /// Optional neutral operational voice used for guide, planner, and support
    /// cue plans.
    public string? SteadyVoice { get; set; }

    /// Optional urgent or command-weighted voice used for directive, sentry,
    /// or rally cue plans.
    public string? UrgentVoice { get; set; }

    /// Optional low-intensity voice used for whisper, hush, stealth, or quiet
    /// cue plans.
    public string? WhisperVoice { get; set; }

    public string? ApiKey { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// Hard cap on synthesis input length. Prevents a runaway caller from pushing
    /// novel-sized text through a TTS engine that may OOM on it.
    public int MaxCharacters { get; set; } = 1_200;

    /// Hard cap on the returned audio payload size. Prevents a misconfigured or
    /// runaway TTS server from streaming arbitrarily large responses into the
    /// sidecar's memory or onto disk.
    public int MaxResponseBytes { get; set; } = 16 * 1024 * 1024;

    /// Retention cap for synthesized speech artifacts written under runtime-root/TTS.
    /// Enforced inline on each successful write so a long session cannot accumulate
    /// unbounded audio files.
    public int MaxStoredFiles { get; set; } = 128;

    public int MaxStoredAgeHours { get; set; } = 24;
}

public sealed class AsrOptions
{
    /// <summary>
    /// Off by default. When enabled, PalLLM forwards bounded local audio clips
    /// to an OpenAI-compatible <c>/v1/audio/transcriptions</c> endpoint and
    /// returns only the transcript text plus compact evidence.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Configured transcription endpoint. Current vLLM and transformers-serve
    /// ASR lanes use a multipart/form-data OpenAI-compatible route.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8000/v1/audio/transcriptions";

    /// <summary>
    /// Exact ASR model id required by the configured endpoint. Leave empty while
    /// disabled; startup validation requires it when <see cref="Enabled"/> is true.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    public string? ApiKey { get; set; }

    /// <summary>
    /// Optional default input-audio language hint forwarded as multipart
    /// <c>language</c> when a request does not supply its own value. Use a
    /// two-letter ISO-639-1 code such as <c>en</c> only after the endpoint proves
    /// it accepts language hints; leaving this null keeps strict local servers
    /// field-free.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// Optional default transcription prompt forwarded as multipart
    /// <c>prompt</c> when a request does not supply its own value. Keep it short
    /// and operator-curated, such as pronunciation or command-vocabulary hints;
    /// never put player identity, save paths, secrets, or raw chat history here.
    /// </summary>
    public string? Prompt { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// OpenAI-compatible multipart <c>response_format</c> value for ASR calls.
    /// <c>json</c> is the compatibility default; <c>verbose_json</c> is opt-in
    /// for endpoint-proven canaries that need richer upstream metadata while
    /// PalLLM still returns only transcript text plus compact receipts.
    /// </summary>
    public string ResponseFormat { get; set; } = AsrResponseFormats.Json;

    /// <summary>
    /// Optional timestamp granularities forwarded as
    /// <c>timestamp_granularities[]</c> only when
    /// <see cref="ResponseFormat"/> is <c>verbose_json</c>. Leave empty for
    /// broad local-runtime compatibility; set to <c>segment</c> for cheap turn
    /// timing proof or <c>word</c> only after latency has been measured.
    /// Returned timestamps are reduced to content-free counts/durations.
    /// </summary>
    public List<string> TimestampGranularities { get; set; } = [];

    /// <summary>
    /// Optional OpenAI-compatible multipart <c>chunking_strategy</c> for file
    /// transcription. Leave empty for maximum local-runtime compatibility; set
    /// to <c>auto</c> only after proving the endpoint accepts server-side VAD
    /// chunking without regressing PalLLM voice-turn latency or receipts.
    /// </summary>
    public string? ChunkingStrategy { get; set; }

    /// <summary>
    /// Optional transcription sampling temperature forwarded as multipart
    /// <c>temperature</c> only when explicitly configured. Current
    /// OpenAI-compatible ASR APIs treat <c>0</c> as the deterministic default;
    /// leaving this null keeps strict local endpoints field-free until proven.
    /// </summary>
    public float? Temperature { get; set; }

    /// <summary>
    /// Optional transcription sampling seed forwarded as multipart
    /// <c>seed</c> only when explicitly configured. This is a vLLM-compatible
    /// replay canary for local ASR endpoints; leave null for strict
    /// OpenAI-compatible transcription servers.
    /// </summary>
    public int? Seed { get; set; }

    /// <summary>
    /// When true, PalLLM sends <c>include[]=logprobs</c> to compatible
    /// transcription endpoints and reduces any returned token logprobs to a
    /// content-free confidence receipt. Token text is never stored.
    /// </summary>
    public bool RequestLogprobs { get; set; }

    /// <summary>
    /// Logprob threshold used to count low-confidence ASR tokens in the
    /// content-free receipt. The default mirrors current speech-to-text
    /// guidance that values below roughly -1 deserve review.
    /// </summary>
    public float LowConfidenceLogprobThreshold { get; set; } = -1.0f;

    /// <summary>
    /// Hard cap on decoded input audio bytes. The default comfortably covers a
    /// short mono 16 kHz player utterance while keeping JSON/base64 ingress well
    /// below the API request-body cap.
    /// </summary>
    public int MaxAudioBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Hard cap on the returned transcription JSON payload.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 64 * 1024;

    /// <summary>
    /// Hard cap on the transcript text returned to callers and later proof
    /// lanes. This prevents an ASR server from turning one short utterance into
    /// an unbounded text payload.
    /// </summary>
    public int MaxTranscriptCharacters { get; set; } = 8 * 1024;

    /// <summary>
    /// Content-free turn duration budget for client-side VAD / endpointing
    /// receipts attached to ASR requests. The runtime records only timing
    /// metadata, never audio bytes or transcript text.
    /// </summary>
    public int MaxTurnDurationMs { get; set; } = 30_000;

    /// <summary>
    /// Target pre-speech padding used by the native/client voice gate. Current
    /// realtime VAD defaults commonly keep about 300 ms before detected speech
    /// so the first syllable is not clipped.
    /// </summary>
    public int PreSpeechPaddingMs { get; set; } = 300;

    /// <summary>
    /// Target trailing silence used to close a spoken turn. Current server-VAD
    /// defaults commonly use about 500 ms; lower values feel faster but can cut
    /// in during natural pauses.
    /// </summary>
    public int EndpointSilenceMs { get; set; } = 500;
}

public sealed class SessionOptions
{
    /// When true, the runtime loads <c>session.json</c> on startup and saves on demand.
    /// Keeping this on preserves per-character memory and relationships across restarts
    /// so companions feel continuous between sessions.
    public bool Enabled { get; set; } = true;

    /// Hard cap for the persisted <c>session.json</c> payload. Prevents a runaway
    /// or corrupted local file from forcing an unbounded startup read before the
    /// sidecar can fall back to a fresh in-memory session or the rotated backup.
    public int MaxPersistedBytes { get; set; } = 8 * 1024 * 1024;

    /// Periodic autosave cadence (seconds). The autosave worker writes the session
    /// file on the interval below so a crash never costs more than this many seconds
    /// of conversation history.
    public bool EnableAutosave { get; set; } = true;

    public int AutosaveIntervalSeconds { get; set; } = 60;
}

public sealed class VisionOptions
{
    /// Vision is opt-in because it requires a separate multimodal model. When enabled,
    /// the runtime will call the configured endpoint to describe images for chat
    /// augmentation, world-state inference, and Pal identification.
    public bool Enabled { get; set; }

    /// HTTP-reachable multimodal endpoint following the chat-completions JSON
    /// schema with <c>image_url</c> content parts. Defaults to the same host
    /// PalLLM uses for text so a single server can cover both models.
    public string BaseUrl { get; set; } = "http://127.0.0.1:11434/v1/";

    /// Default model tag is an illustrative placeholder pointing at a small
    /// edge-class multimodal model suitable for low-latency scene analysis.
    /// Replace with any tag your configured HTTP endpoint recognises.
    public string Model { get; set; } = "gemma4:e2b";

    public string? ApiKey { get; set; }

    /// Lower temperature by default — most vision calls in PalLLM want structured
    /// extraction (world-state JSON, terse scene summaries), not creative prose.
    /// Sidecar startup validation accepts finite values from <c>0</c> through
    /// <c>2</c>.
    public float Temperature { get; set; } = 0.2f;

    /// Small cap: replies should stay terse. Raise for structured JSON extraction
    /// with many fields.
    public int DefaultMaxTokens { get; set; } = 180;

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Hard cap on the returned multimodal chat-completions payload size.
    /// Prevents a runaway endpoint from sending arbitrarily large JSON bodies
    /// into the sidecar while the vision lane is parsing them.
    /// </summary>
    public int MaxResponseBytes { get; set; } = 64 * 1024;

    /// Hard cap on incoming image payload size to avoid OOM / DoS. Default 6 MB
    /// (fits a 4K-ish PNG screenshot). Applied to base64 payload length after decode.
    public int MaxImageBytes { get; set; } = 6 * 1024 * 1024;

    /// <summary>
    /// Adds a stable content-hash <c>uuid</c> to outgoing vision <c>image_url</c>
    /// parts. vLLM-compatible multimodal servers can use this as a media-cache
    /// key for repeated screenshots; strict endpoints that reject unknown content
    /// fields can disable it without changing the rest of the vision request.
    /// </summary>
    public bool UseMediaCacheIds { get; set; } = true;

    /// <summary>
    /// Optional vLLM-style <c>mm_processor_kwargs</c> for screenshot/image
    /// requests. Use to cap pixels, frame rate, or soft-token budget on a
    /// proven local multimodal lane; omitted by default for strict endpoint
    /// portability.
    /// </summary>
    public MultimodalProcessorOptions MultimodalProcessor { get; set; } = new();

    /// When true, chat requests that carry an ImageBase64 field will first call the
    /// vision client for a short description and splice the result into the system
    /// prompt as visual context. Off by default so the text chat path stays fast.
    public bool UseForChatAugmentation { get; set; } = true;

    /// Enables the periodic screenshot watcher. When true, the sidecar polls the
    /// Bridge/Screenshots directory and feeds each new image through the structured
    /// world-state extractor, merging the result into the live snapshot. The Lua
    /// side produces screenshots on a separate cadence, so this value only controls
    /// how often the sidecar scans for new files.
    public bool EnableScreenshotWatcher { get; set; }

    public int ScreenshotPollIntervalMs { get; set; } = 15_000;

    /// Bound how many screenshots the background watcher processes per poll. This
    /// keeps a sudden screenshot backlog from monopolizing the vision model and
    /// preserves chat latency under long unattended runs.
    public int MaxScreenshotsPerPoll { get; set; } = 2;

    /// Retention policy for pending screenshots still sitting in Bridge/Screenshots.
    /// When vision is disabled or falls behind, the watcher prunes old screenshots so
    /// stale images do not consume disk forever or create a high-latency backlog.
    public int PendingScreenshotMaxFiles { get; set; } = 32;

    public int PendingScreenshotMaxAgeHours { get; set; } = 1;

    /// <summary>
    /// When true (default), world-state extraction requests include an
    /// OpenAI-style <c>response_format: { type: "json_schema", ... }</c> so
    /// endpoints that support structured outputs (OpenAI, Ollama ≥ 0.5, LM
    /// Studio, vLLM, and most current HTTP multimodal servers) constrain the
    /// model to the PalLLM world-state schema instead of returning prose.
    /// Endpoints that don't recognise the field silently ignore it. Flip off
    /// if your endpoint rejects unknown parameters strictly.
    /// </summary>
    public bool UseStructuredOutputs { get; set; } = true;
}

public sealed class FallbackOptions
{
    public bool Enabled { get; set; } = true;

    public bool UseWhenInferenceDisabled { get; set; } = true;

    public bool UseWhenInferenceFails { get; set; } = true;

    public bool EnablePolicyBypass { get; set; } = true;

    public bool PreferForReactiveBarks { get; set; } = true;

    public bool PreferForRoutineTacticalTasks { get; set; } = true;

    public bool PreferForRecoveryAndCampTasks { get; set; } = true;

    public int RecentMemoryWindow { get; set; } = 12;

    /// Enables the deterministic memory-reflection pass after each chat. Off by
    /// default so reproducible test fixtures do not accrue surprise entries.
    /// Turn on in production configs to let the runtime consolidate
    /// high-importance moments into retrievable insight memories over a session.
    public bool EnableReflection { get; set; }

    /// Task-focus toggle. When enabled, the system prompt reminds the model to
    /// stay task-focused instead of leaning into performative character shtick.
    /// Off by default to preserve existing roleplay feel.
    public bool PreferTaskFocus { get; set; }

    /// Rate-limit ceiling for chat requests per character per minute. Set to 0 to
    /// disable (default). When a character breaches the limit, subsequent calls
    /// short-circuit to the deterministic fallback — preserves a working reply
    /// without paying inference tokens on a runaway producer.
    public int MaxCharacterRequestsPerMinute { get; set; }
}

/// <summary>
/// Hardware-tier override config (Pass 25 / D1). Optional. When
/// <see cref="ForceTier"/> names a valid <c>DuoHardwareTier</c>
/// enum value, the /api/hardware surface reports that as the
/// effective tier regardless of detection. Empty or unparsable
/// values are ignored.
/// </summary>
public sealed class HardwareOptions
{
    /// <summary>Optional force-tier value: Constrained / Standard / Generous.</summary>
    public string? ForceTier { get; set; }
}

/// <summary>
/// Configuration for the promotion apply verb (Pass 24). When
/// <see cref="AllowApply"/> is true, <c>POST /api/promotion/apply</c>
/// is allowed to persist a durable staging artifact (template +
/// rollback marker + audit packet) under <see cref="StagingRoot"/>
/// for a candidate promotion. Apply never mutates source code in-place;
/// the staging artifact is meant to be cherry-picked by a human
/// reviewer. Rollback is simply deleting the staged files.
/// </summary>
public sealed class PromotionApplyOptions
{
    /// <summary>
    /// Master safety flag. Default off — the promotion pipeline stays
    /// observation-only out of the box. Flip to <c>true</c> only in
    /// environments where a human reviewer is going to cherry-pick
    /// the staged artifacts.
    /// </summary>
    public bool AllowApply { get; set; } = false;

    /// <summary>
    /// Directory where Pass-24 apply writes <c>template-*.md</c>,
    /// <c>rollback-*.txt</c>, and <c>packet-*.json</c> per apply
    /// invocation. Relative paths are resolved against the runtime's
    /// <c>Runtime/</c> root. Defaults to <c>PromotionStaging</c>.
    /// </summary>
    public string StagingRoot { get; set; } = "PromotionStaging";

    /// <summary>
    /// Cap on the number of artifacts retained. When exceeded, the
    /// oldest staged apply is removed from disk. 64 is an order of
    /// magnitude of headroom for the expected per-task candidacy rate.
    /// </summary>
    public int MaxStagedArtifacts { get; set; } = 64;
}

/// <summary>
/// Configuration for the background <c>PromotionLedgerFeeder</c>.
/// Every tick the feeder reads the live <c>PalLlmMetrics</c> snapshot,
/// diffs the fallback-strategy counts against the prior tick, and
/// writes one "success" observation into the ledger per increment.
/// Pure observer — never mutates runtime state beyond the ledger.
/// </summary>
public sealed class PromotionFeederOptions
{
    /// <summary>Master switch. Default on because behaviour is purely additive.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the feeder reads the metrics snapshot. Too low
    /// wastes CPU on diffs; too high means a slow-flowing strategy may
    /// miss observations between ticks. 30s balances both.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Cap on the number of ledger records emitted per tick per
    /// strategy. Bounds the feeder so a brief burst of fallback fires
    /// cannot flood the ledger with hundreds of identical observations
    /// in one tick.
    /// </summary>
    public int MaxObservationsPerStrategyPerTick { get; set; } = 25;

    /// <summary>
    /// Task class identifier recorded against every fallback-director
    /// observation the feeder emits. Default maps fallback fires to a
    /// dedicated slot so operators can tell auto-fed observations from
    /// manual ones at a glance.
    /// </summary>
    public string FallbackTaskClass { get; set; } = "fallback-director";

    /// <summary>
    /// Task class recorded for live-inference deltas. Each observation's
    /// <c>PatternId</c> is the active model id from <c>RuntimeHealth</c>
    /// so different models populate separate observation streams. Set
    /// <see cref="TrackLiveInference"/> to <c>false</c> to disable.
    /// </summary>
    public string LiveInferenceTaskClass { get; set; } = "live-inference";

    /// <summary>Toggle for live-inference observation recording.</summary>
    public bool TrackLiveInference { get; set; } = true;

    /// <summary>
    /// Task class recorded when the per-character rate limiter engages.
    /// Recorded as <c>OutcomeSuccess</c> because engagement means the
    /// limiter is correctly protecting the live-inference lane from a
    /// runaway caller — the player-visible reply still lands via fallback.
    /// Set <see cref="TrackRateLimiter"/> to <c>false</c> to disable.
    /// </summary>
    public string RateLimiterTaskClass { get; set; } = "rate-limiter";

    /// <summary>Toggle for rate-limiter observation recording.</summary>
    public bool TrackRateLimiter { get; set; } = true;

    /// <summary>
    /// Task class recorded for model-tier graduation transitions
    /// (small → large, large → small). Pattern id is the
    /// <c>from→to</c> tuple from the metric. Set
    /// <see cref="TrackTierTransitions"/> to <c>false</c> to disable.
    /// </summary>
    public string TierTransitionTaskClass { get; set; } = "model-tier-transition";

    /// <summary>Toggle for model-tier-transition observation recording.</summary>
    public bool TrackTierTransitions { get; set; } = true;
}

/// <summary>
/// Conservative background self-healing watchdog. On a cadence, the worker
/// audits the live runtime for stuck state and applies fixes that are safe
/// to perform without operator input:
///
/// <list type="bullet">
///   <item>Archive outbox envelopes older than <see cref="OrphanEnvelopeAgeSeconds"/>
///         to <c>Runtime/SelfHealingEvidence/recovered-&lt;UTC&gt;/</c> so a
///         stuck consumer never starves a future producer.</item>
///   <item>Log the current <c>OperatorHealthScore</c> when it drops below
///         <see cref="UnhealthyScoreFloor"/>, so a long-running sidecar in
///         degraded state surfaces in server logs even if nobody is watching
///         the dashboard.</item>
///   <item>Write <c>Runtime/SelfHealingEvidence/latest-self-healing.json</c>
///         every tick so operators can audit exactly what the watchdog
///         observed and what it did.</item>
/// </list>
///
/// <para>Deliberately does NOT mutate circuit-breaker state or restart the
/// sidecar — those are destructive operations reserved for the human-driven
/// <c>recover.bat</c> path. The watchdog is additive observability + gentle
/// janitorial work only.</para>
/// </summary>
public sealed class SelfHealingOptions
{
    /// <summary>Master switch. Defaults to <c>true</c> because the default
    /// behaviour is non-destructive (archive + log) and the worker is tiny.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the watchdog ticks. Tight enough to catch stuck
    /// state within a reasonable window; loose enough to never contribute
    /// measurable CPU.</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>Outbox envelopes older than this are archived to
    /// <c>Runtime/SelfHealingEvidence/recovered-&lt;UTC&gt;/</c>. Set to 0 to
    /// disable orphan-envelope sweeping.</summary>
    public int OrphanEnvelopeAgeSeconds { get; set; } = 600;

    /// <summary>Operator-health scores at or below this value trigger a
    /// structured log line every tick. Set to 0 to disable the log signal.</summary>
    public int UnhealthyScoreFloor { get; set; } = 40;

    /// <summary>How many history snapshots under
    /// <c>Runtime/SelfHealingEvidence/History/</c> to keep. 0 disables
    /// history; the <c>latest</c> snapshot is always written regardless.</summary>
    public int HistoryRetention { get; set; } = 200;
}

/// <summary>
/// One role binding in <see cref="PalLlmOptions.ModelRoles"/>. Declares
/// that a given model endpoint fills a specific <c>ModelRole</c> slot
/// (Edge / Worker / Judge / Media / Validator) in the local-first AI
/// mesh. Multiple bindings per role are allowed — the first
/// <see cref="Enabled"/> one per role is treated as the active
/// endpoint.
/// </summary>
public sealed class ModelRoleBinding
{
    /// <summary>Which of the five mesh roles this binding fills.</summary>
    public PalLLM.Domain.Inference.ModelRole Role { get; set; }

    /// <summary>Short operator-facing id so log lines and tool output
    /// can reference the binding without a UUID (e.g. <c>"gemma-edge"</c>,
    /// <c>"qwen-fast"</c>, <c>"qwen-dense"</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The model tag the endpoint expects (e.g.
    /// <c>"gemma3:4b"</c>, <c>"qwen3.6:35b-a3b"</c>). Informational —
    /// the runtime does not re-issue this to the endpoint automatically.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Base URL this role binding points at. Used by
    /// <c>/api/airgap/verify</c> and future role-aware routing; not
    /// automatically called today.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Free-form operator note (capacity, quant level,
    /// residency expectation, etc.).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Per-binding enable switch so operators can pre-declare a
    /// role binding and flip it on when the endpoint is ready without
    /// editing the list structure.</summary>
    public bool Enabled { get; set; } = true;
}
