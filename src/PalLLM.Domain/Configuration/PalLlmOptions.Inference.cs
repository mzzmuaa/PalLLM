// PalLlmOptions (partial): inference / multimodal-processor / thermal-gate / residency / model-tier options.
// Nested option classes bound from the "PalLLM" appsettings section; see PalLlmOptions.cs for the root aggregator + AGENT-CARD.
using System.Text.Json.Serialization;

namespace PalLLM.Domain.Configuration;


public sealed class InferenceOptions
{
    public bool Enabled { get; set; }

    // Pass 426: default to the bundled llama.cpp engine's loopback endpoint
    // and curated model id, matching src/PalLLM.Sidecar/appsettings.json. The
    // previous default pointed at the Ollama port (11434), which was removed
    // repo-wide in Pass 339; keeping it here left a dead default for anyone
    // running the portable adapter without a config file. See
    // docs/LLAMA_CPP_BUNDLED.md and docs/LOCAL_MODELS_INVENTORY.md.
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080/v1/";

    public string Model { get; set; } = "Qwen3.6-35B-A3B-UD-Q8_K_XL";

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
