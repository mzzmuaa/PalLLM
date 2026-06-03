// PalLlmOptions (partial): TTS / ASR / session / vision options.
// Nested option classes bound from the "PalLLM" appsettings section; see PalLlmOptions.cs for the root aggregator + AGENT-CARD.
using System.Text.Json.Serialization;

namespace PalLLM.Domain.Configuration;


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
    /// OpenAI-compatible speech routes such as OpenAI-compatible omni Qwen3-TTS.
    public string RequestFormat { get; set; } = TtsRequestFormats.Simple;

    /// Optional model id sent only by the <c>openai_speech</c> request shape.
    /// Some local endpoints infer the served model from the server, while
    /// stricter OpenAI-compatible providers require this field.
    public string? Model { get; set; }

    public string DefaultVoice { get; set; } = "en_US-amy-medium";

    /// Audio container requested from OpenAI-compatible speech endpoints.
    /// Ignored by the default <c>simple</c> adapter shape.
    public string ResponseFormat { get; set; } = TtsResponseFormats.Wav;

    /// Optional playback speed sent only by the <c>openai_speech</c> request
    /// shape. Leave unset for strict local endpoints unless the exact speech
    /// server has accepted the field in a proof canary.
    public float? Speed { get; set; }

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
    /// Configured transcription endpoint. Current OpenAI-compatible and transformers-serve
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
    /// <c>seed</c> only when explicitly configured. This is a OpenAI-compatible
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
    /// schema with <c>image_url</c> content parts. Defaults to the same
    /// bundled llama.cpp loopback host PalLLM uses for text so a single
    /// server (with an mmproj projector) can cover both models. Matches
    /// src/PalLLM.Sidecar/appsettings.json. (Pass 426: was the removed
    /// Ollama port 11434.)
    public string BaseUrl { get; set; } = "http://127.0.0.1:8080/v1/";

    /// Default model id matches the chat tier (the curated Qwen3.6-A3B GGUF
    /// is MTP-capable and ships with an mmproj projector, so one loaded model
    /// serves both text and vision). Matches appsettings.json; replace with
    /// any id your configured HTTP endpoint recognises. (Pass 426: was the
    /// illustrative Ollama-style tag gemma4:e2b.)
    public string Model { get; set; } = "Qwen3.6-35B-A3B-UD-Q8_K_XL";

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
    /// parts. OpenAI-compatible multimodal servers can use this as a media-cache
    /// key for repeated screenshots; strict endpoints that reject unknown content
    /// fields can disable it without changing the rest of the vision request.
    /// </summary>
    public bool UseMediaCacheIds { get; set; } = true;

    /// <summary>
    /// Optional OpenAI-compatible <c>mm_processor_kwargs</c> for screenshot/image
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
    /// OpenAI-compatible endpoints that support structured outputs (including
    /// llama.cpp and most current HTTP multimodal servers) constrain the
    /// model to the PalLLM world-state schema instead of returning prose.
    /// Endpoints that don't recognise the field silently ignore it. Flip off
    /// if your endpoint rejects unknown parameters strictly.
    /// </summary>
    public bool UseStructuredOutputs { get; set; } = true;
}
