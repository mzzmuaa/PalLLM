# Environment variables

Last audited: `2026-05-21`

Every environment variable that affects PalLLM, with default,
effect, and example. PalLLM follows the standard ASP.NET Core +
OpenTelemetry env-var conventions plus a handful of repo-specific
flags.

The canonical config surface is `PalLlmOptions` (see
`src/PalLLM.Domain/Configuration/PalLlmOptions.cs`). Any field on
that class can be set via the env-var pattern
`PalLLM__<Section>__<Field>` (double underscore = `:` separator
in ASP.NET Core's environment provider).

## ASP.NET Core

| Variable | Default | Effect |
|---|---|---|
| `ASPNETCORE_URLS` | `http://localhost:5088` | Bind address(es). Multiple URLs can be semicolon-separated. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | `Development` enables verbose error pages + Swagger UI. Production is the right default for the release ZIP. |
| `DOTNET_NOLOGO` | unset | Suppresses the `.NET` startup banner. The release `play.bat` sets this for cleaner output. |

## Examples

```powershell
# Bind to a different port
$env:ASPNETCORE_URLS = "http://localhost:5089"

# Bind to all interfaces (REQUIRES API-key auth — see SECURITY.md)
$env:ASPNETCORE_URLS = "http://0.0.0.0:5088"

# Multiple bindings (e.g. for TLS-terminated reverse proxy)
$env:ASPNETCORE_URLS = "http://localhost:5088;http://localhost:5089"
```

## OpenTelemetry

Only relevant when you want distributed tracing. Tracing is
**off by default** — none of these need to be set for normal
operation.

| Variable | Default | Effect |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | unset | The OTLP collector URL. Setting this is the master switch for tracing. |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | `grpc` (port 4317) or `http/protobuf` (port 4318). |
| `OTEL_EXPORTER_OTLP_HEADERS` | unset | Vendor auth headers. E.g. `x-honeycomb-team=YOUR_API_KEY` for Honeycomb. |
| `OTEL_RESOURCE_ATTRIBUTES` | unset | Comma-separated `key=value` pairs added as resource attributes. |
| `OTEL_SERVICE_NAME` | `PalLLM` (set by code) | Override the service name reported to the collector. |

### Examples

```powershell
# Local Jaeger
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "http://localhost:4317"

# Honeycomb
$env:OTEL_EXPORTER_OTLP_ENDPOINT = "https://api.honeycomb.io"
$env:OTEL_EXPORTER_OTLP_PROTOCOL = "http/protobuf"
$env:OTEL_EXPORTER_OTLP_HEADERS = "x-honeycomb-team=$env:HONEYCOMB_API_KEY"
```

Full primer in [`OBSERVABILITY.md`](OBSERVABILITY.md).

## PalLLM-specific

| Variable | Default | Effect |
|---|---|---|
| `PALLLM_OTLP_DISABLE_ASPNETCORE` | unset | `1` = don't add the AspNetCore instrumentation package. Useful when benchmarking PalLLM-only spans. |
| `PALLLM_OTLP_DISABLE_HTTPCLIENT` | unset | `1` = don't add the HttpClient instrumentation package. Same purpose as above. |

## `PalLlmOptions` overrides

Every field on the `PalLlmOptions` tree can be set via env-var
using the pattern `PalLLM__<Section>__<Field>` (note: **double
underscore** for the section separator).

Common overrides:

| Variable | Effect |
|---|---|
| `PalLLM__Inference__Enabled` | `true` / `false` — turn live inference on / off |
| `PalLLM__Inference__BaseUrl` | Inference HTTP endpoint (e.g. Ollama at `http://127.0.0.1:11434/v1/`) |
| `PalLLM__Inference__Model` | Model name passed to the inference API (e.g. `qwen3.6:0.6b`) |
| `PalLLM__Inference__ApiKey` | Bearer token for the inference endpoint, if it requires one |
| `PalLLM__Inference__PrefixCacheSalt` | Optional vLLM `cache_salt` trust-domain value for isolating prefix-cache reuse on shared endpoints |
| `PalLLM__Inference__Temperature` | Baseline chat temperature (`0` to `2`); per-turn execution profiles can override it for task fit |
| `PalLLM__Inference__TopP` | Baseline nucleus-sampling cap (`0` to `1`) forwarded when configured |
| `PalLLM__Inference__PresencePenalty` | Baseline OpenAI-compatible presence penalty (`-2` to `2`) forwarded when configured |
| `PalLLM__Inference__TokenBudgetField` | Selects the emitted output-token budget field: `max_tokens` by default, or endpoint-proven `max_completion_tokens` for reasoning lanes that reject `max_tokens` |
| `PalLLM__Inference__ReasoningEffort` | Optional `reasoning_effort` hint (`none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`) forwarded only when explicitly configured and endpoint-proven |
| `PalLLM__Inference__Seed` | Optional OpenAI-compatible `seed` hint forwarded only when explicitly configured and endpoint-proven for replay comparisons |
| `PalLLM__Inference__FrequencyPenalty` | Optional OpenAI-compatible `frequency_penalty` hint (`-2` to `2`) forwarded only when explicitly configured and endpoint-proven for repetition control |
| `PalLLM__Inference__TopK` | Optional local-runtime `top_k` sampler hint (`1` to `65536`) forwarded only when explicitly configured and endpoint-proven |
| `PalLLM__Inference__MinP` | Optional local-runtime `min_p` sampler hint (`0` to `1`) forwarded only when explicitly configured and endpoint-proven |
| `PalLLM__Inference__RepetitionPenalty` | Optional local-runtime `repetition_penalty` hint (`0` to `2`) forwarded only when explicitly configured and endpoint-proven |
| `PalLLM__Inference__RequestPriority` | Optional vLLM `priority` hint forwarded only when explicitly configured and the endpoint is launched with priority scheduling |
| `PalLLM__Inference__ParallelToolCalls` | Optional OpenAI-compatible `parallel_tool_calls` hint forwarded only when explicitly configured and strict tool-call fan-out has been endpoint-proven |
| `PalLLM__Inference__StopSequences__0` | First optional OpenAI-compatible `stop` delimiter forwarded only after the exact endpoint/model has proven delimiter handling |
| `PalLLM__Inference__TimeoutSeconds` | Inference HTTP timeout (default `60`) |
| `PalLLM__Inference__MaxResponseBytes` | Cap on inference response size in bytes (default `65536`, 64 KB) |
| `PalLLM__Inference__ModelCatalogMaxResponseBytes` | Cap on OpenAI-compatible model catalogs (`/v1/models` or OpenVINO `/v3/models`), Foundry Local `/openai/models`, and `/api/tags` discovery payloads in bytes (default `262144`, 256 KB) |
| `PalLLM__Vision__Enabled` | `true` / `false` — vision describe on / off |
| `PalLLM__Vision__BaseUrl` | Vision HTTP endpoint |
| `PalLLM__Vision__Model` | Vision model id sent to the configured multimodal endpoint (default `gemma4:e2b`) |
| `PalLLM__Vision__ApiKey` | Bearer token for the vision endpoint, if it requires one |
| `PalLLM__Vision__Temperature` | Vision-lane temperature (`0` to `2`), defaulting low for extraction-style calls |
| `PalLLM__Vision__MaxResponseBytes` | Cap on vision response size in bytes (default `65536`, 64 KB) |
| `PalLLM__Vision__EnableScreenshotWatcher` | Background screenshot watcher on / off (default `false`) |
| `PalLLM__Tts__Enabled` | `true` / `false` — TTS synthesis on / off |
| `PalLLM__Tts__BaseUrl` | TTS HTTP endpoint |
| `PalLLM__Tts__RequestFormat` | TTS request body shape: `simple` (`{ text, voice }`) or `openai_speech` (`/v1/audio/speech` style `input`, `voice`, `response_format`) |
| `PalLLM__Tts__Model` | Optional model id sent only for `RequestFormat=openai_speech`; leave empty when the local server infers the loaded speech model |
| `PalLLM__Tts__ResponseFormat` | Audio container requested for `RequestFormat=openai_speech`: `wav`, `mp3`, `opus`, `aac`, `flac`, or `pcm`; also the MIME fallback for missing/generic speech response content types |
| `PalLLM__Tts__MaxResponseBytes` | Cap on TTS response size in bytes (default `16777216`, 16 MB) |
| `PalLLM__Asr__Enabled` | `true` / `false` - audio transcription on / off |
| `PalLLM__Asr__BaseUrl` | OpenAI-compatible transcription endpoint (default `http://127.0.0.1:8000/v1/audio/transcriptions`) |
| `PalLLM__Asr__Model` | Model id sent as multipart `model`; required when ASR is enabled |
| `PalLLM__Asr__ApiKey` | Bearer token for the ASR endpoint, if it requires one |
| `PalLLM__Asr__ResponseFormat` | Multipart `response_format` for ASR calls: `json` by default, or endpoint-proven `verbose_json` for richer transcription metadata canaries |
| `PalLLM__Asr__TimestampGranularities__0` | Optional verbose ASR timestamp granularity (`segment` or `word`); valid only with `PalLLM__Asr__ResponseFormat=verbose_json` |
| `PalLLM__Asr__ChunkingStrategy` | Optional multipart `chunking_strategy`; leave empty by default, or set endpoint-proven `auto` for server/VAD-selected file transcription chunks |
| `PalLLM__Asr__Temperature` | Optional multipart `temperature` field for endpoint-proven ASR sampler canaries (`0` to `1`; omitted when unset) |
| `PalLLM__Asr__RequestLogprobs` | Optional `include[]=logprobs` request switch for compatible ASR confidence receipts |
| `PalLLM__Asr__LowConfidenceLogprobThreshold` | Logprob threshold used to count low-confidence ASR tokens in the content-free confidence receipt (default `-1.0`) |
| `PalLLM__Asr__MaxAudioBytes` | Cap on decoded incoming audio bytes before any upstream call (default `4194304`, 4 MB) |
| `PalLLM__Asr__MaxResponseBytes` | Cap on upstream transcription JSON (default `65536`, 64 KB) |
| `PalLLM__Asr__MaxTranscriptCharacters` | Cap on returned transcript text (default `8192`) |
| `PalLLM__Asr__MaxTurnDurationMs` | Content-free endpointing receipt turn-duration cap (default `30000`) |
| `PalLLM__Asr__PreSpeechPaddingMs` | Target client/native VAD pre-speech padding for endpointing receipts (default `300`) |
| `PalLLM__Asr__EndpointSilenceMs` | Target trailing silence used to close an ASR voice turn (default `500`) |
| `PalLLM__Auth__ApiKey` | Required bearer token to access protected endpoints. **Set this any time you bind beyond `localhost`.** |
| `PalLLM__Auth__ProtectMetrics` | `true` / `false` — gate `/metrics` behind the API key |
| `PalLLM__Auth__ProtectHealth` | `true` / `false` — gate `/health/*` behind the API key |
| `PalLLM__Auth__McpAllowedOrigins__0` | First explicit non-loopback browser origin allowed to call `/mcp` when the host sends `Origin` |
| `PalLLM__Http__ApiRequestBodyMaxBytes` | Max request-body bytes accepted on `/api/*` and `/mcp` JSON routes before model binding (default `10485760`, 10 MiB) |
| `PalLLM__Http__SelfDescriptionCacheSeconds` | TTL for `/api/describe` output cache (default `15`) |
| `PalLLM__Http__ChatRequestTimeoutSeconds` | Outer timeout for chat-class HTTP lanes, including `/api/chat`, `/api/chat/stream`, party chat, and manual inference warmup (default `130`) |
| `PalLLM__Http__VisionRequestTimeoutSeconds` | Outer timeout for vision HTTP lanes, including screenshot processing (default `45`) |
| `PalLLM__Http__TtsRequestTimeoutSeconds` | Outer timeout for speech/audio lanes, including `/api/tts/synthesize` and `/api/audio/transcribe` (default `45`) |
| `PalLLM__Bridge__Enabled` | `true` / `false` — bridge inbox / outbox on (default `true`) |
| `PalLLM__Bridge__PollIntervalMs` | Drain cadence (default `1000`) |
| `PalLLM__Bridge__OutboxEnabled` | Whether to write replies to `Bridge/Outbox/` (default `true`) |
| `PalLLM__Automation__Enabled` | Master switch for planning advisory action intents (default `false`) |
| `PalLLM__Automation__EmitToOutbox` | Mirror planned action intents into `Bridge/Outbox` envelopes (default `true`, only matters when automation is enabled) |
| `PalLLM__Automation__AllowedActions__0` | First entry in the action-intent allowlist |
| `PalLLM__Hardware__ForceTier` | `Constrained` / `Standard` / `Generous` — override hardware detection |
| `PalLLM__SelfHealing__Enabled` | Watchdog on / off (default `true`) |
| `PalLLM__PalSavedRoot` | Override the runtime root parent (default `%LOCALAPPDATA%\Pal\Saved`) |
| `PalLLM__RuntimeFolderName` | Override the runtime root folder name (default `PalLLM`) |

### Setting array fields

Arrays use a numeric index after a double underscore:

```powershell
# Allowlist three action types
$env:PalLLM__Automation__AllowedActions__0 = "waypoint_suggest"
$env:PalLLM__Automation__AllowedActions__1 = "recall_pals"
$env:PalLLM__Automation__AllowedActions__2 = "craft_queue"

# Add endpoint-proven stop delimiters for strict canary routes
$env:PalLLM__Inference__StopSequences__0 = "</pal-action>"
$env:PalLLM__Inference__StopSequences__1 = "<END>"

# Allow a non-loopback browser-based MCP host
$env:PalLLM__Auth__McpAllowedOrigins__0 = "https://mcp-ui.example"

# Wire up two MCP upstream proxy servers
$env:PalLLM__McpClient__UpstreamServers__0__Id = "primary"
$env:PalLLM__McpClient__UpstreamServers__0__Url = "https://primary.example/mcp"
$env:PalLLM__McpClient__UpstreamServers__1__Id = "fallback"
$env:PalLLM__McpClient__UpstreamServers__1__Url = "https://fallback.example/mcp"
```

## Precedence

ASP.NET Core's standard configuration provider order applies:

1. **Defaults** baked into `PalLlmOptions` property initializers
2. **`appsettings.json`** at the sidecar's working directory
3. **`appsettings.<Environment>.json`** (e.g.
   `appsettings.Development.json`)
4. **Environment variables** (this doc's territory)
5. **Command-line `--key=value`** arguments

Later sources override earlier ones. Env vars are typically the
right place for production overrides; the JSON files are the
right place for committed dev defaults.

## Reading the active config

```powershell
# Hit the describe endpoint — every active opt-in is reported there
curl -s http://localhost:5088/api/describe | ConvertFrom-Json | Format-List

# Or the privacy posture — explains what's currently emitting traffic
curl -s http://localhost:5088/api/privacy/posture | ConvertFrom-Json | Format-List
```

The live runtime never lies about its config — every operator-facing
posture surface (`/api/health`, `/api/describe`,
`/api/privacy/posture`, `/api/airgap/verify`) reads the actual
`PalLlmOptions` instance, not a copy.

## Related

- `src/PalLLM.Domain/Configuration/PalLlmOptions.cs` — source of truth
- [`TUNING.md`](TUNING.md) — every parameter with too-low / too-high
  guidance
- [`OPERATIONS.md`](OPERATIONS.md) — opt-in feature matrix with
  per-feature enable / verify / rollback steps
- [`PRIVACY.md`](PRIVACY.md) — what each opt-in flag changes about
  network traffic
- [`adr/0006-opt-in-everything-by-default.md`](adr/0006-opt-in-everything-by-default.md)
