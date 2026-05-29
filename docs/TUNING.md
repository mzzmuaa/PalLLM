# PalLLM Tuning Reference

Last audited: `2026-05-28`

Audience: operators who need to dial in PalLLM's behaviour for their
hardware, their hosting model, and the feel they want companions to
have. This is a reference doc — skim the table of contents and skip to
the block you're tuning. Every parameter is settable via `appsettings.json`
under the `PalLLM:<Block>:<Name>` key, via the corresponding
`PalLLM__<Block>__<Name>` environment variable (works inside Docker),
or via any standard ASP.NET Core configuration source.

Implementation note for maintainers and harvesters: the sidecar enables the
.NET configuration binding source generator for the `PalLLM` section. Operators
still use the same keys and environment-variable shapes; the generated binder
only removes reflection from startup binding of the large `PalLlmOptions` tree.

> **If you are not looking for a specific parameter:** the shipping
> defaults in `src/PalLLM.Sidecar/appsettings.json` are tuned for a
> localhost llama-server deployment serving the operator's curated
> `D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf` quality tier
> behind a `gemma-4-E4B-it-UD-Q4_K_XL` fast-start tier. You can run
> PalLLM without touching any of these knobs.

## Contents

1. [Runtime personality — what's real vs what's faked](#runtime-personality--whats-real-vs-whats-faked)
2. [Inference block](#inference-block)
3. [Model tiers (within Inference)](#model-tiers-within-inference)
4. [Fallback block](#fallback-block)
5. [Vision block](#vision-block)
6. [TTS block](#tts-block)
7. [ASR block](#asr-block)
8. [Bridge block](#bridge-block)
9. [Session block](#session-block)
10. [Automation block](#automation-block)
11. [Http block](#http-block)
12. [Auth block](#auth-block)
13. [McpClient block (upstream MCP servers)](#mcpclient-block-upstream-mcp-servers)
14. [Performance budget](#performance-budget)
15. [Tuning workflow](#tuning-workflow)

---

## Runtime personality — what's real vs what's faked

PalLLM's design principle: **prioritise felt companionship and
responsiveness, not simulation fidelity for its own sake.** Every
response-producing path is classified as one of four modes. Knowing
which mode applies to the path you're tuning tells you where the
knobs matter most.

| Path | Mode | Cost class | Where to tune |
|---|---|---|---|
| Chat reply (live model path) | Real | HTTP round-trip + model compute | [Inference block](#inference-block), [Model tiers](#model-tiers-within-inference) |
| Chat reply (fallback path) | Deterministic | Sub-millisecond CPU | [Fallback block](#fallback-block) |
| Chat augmentation from screenshot | Real (if Vision on) → Deterministic fallback | HTTP + compute → pure CPU | [Vision block](#vision-block) |
| Scene description (for MCP + prompts) | Deterministic from snapshot | Sub-millisecond CPU | [Fallback block](#fallback-block) |
| TTS synthesis | Real (optional) | HTTP + compute | [TTS block](#tts-block) |
| Audio transcription | Real (optional) | HTTP + compute | [ASR block](#asr-block) |
| Action intents | Deterministic planner | Sub-millisecond CPU | [Automation block](#automation-block) |
| Memory recall | Deterministic embeddings + cosine + exact-token rerank | ~1 ms per query | [Fallback block](#fallback-block) (RecentMemoryWindow) |
| Presentation plan | Always synthesised (required) | Sub-millisecond CPU | Not operator-tuned |
| Bridge drain | Filesystem I/O | Disk IOPS | [Bridge block](#bridge-block) |

**The shipping defaults keep every "real" path opt-in** (`Inference.Enabled = false`, `Vision.Enabled = false`, `Tts.Enabled = false`, `Asr.Enabled = false`, `Automation.Enabled = false`). Out of the box PalLLM runs entirely on deterministic paths — zero HTTP calls, sub-millisecond reply latency — and still produces coherent replies via the 19 fallback strategies. Flip features on one at a time as you add the infrastructure each needs.

---

## Inference block

Controls the live model-call path. **Off by default.** Configure an OpenAI-compatible HTTP endpoint (llama.cpp server (default), vLLM (high-config GPUs), TensorRT-LLM, LM Studio, OpenVINO Model Server, Foundry Local, vLLM-Omni, or `transformers serve` — Pass 339 dropped Ollama support) and flip `Enabled` to route chat through it; if inference fails or is bypassed, the fallback director takes over.

Implementation note: successful inference replies are read with
`ResponseHeadersRead` and parsed directly from the response stream, and
PalLLM accepts plain `message.content` strings, text-part arrays, and
tool-call-only assistant messages that current chat-completions servers emit.
Route-specific callers can also forward prompt-level `response_format`,
`structured_outputs`, `tools`, `tool_choice`, `prediction`, `logprobs`,
`top_logprobs`, `modalities`, `audio`, and `UserContent` payloads for
structured-output, tool-call, predicted-output, confidence, audio-output, and
multimodal-input canaries without changing ordinary companion-chat defaults.
When
`OTEL_EXPORTER_OTLP_ENDPOINT` is set, the same live inference path also
emits `gen_ai.client.operation.duration` and `gen_ai.client.token.usage`
histograms plus `CLIENT` GenAI spans tagged with request/response model
metadata. Provider labels stay low-cardinality: hosted providers use a known
vendor label; common local runtime hosts, paths, and loopback/LAN default
ports resolve to labels such as `ollama`, `lmstudio`, `llama.cpp`, `vllm`,
`openvino`, and `tensorrt_llm`; ambiguous endpoints remain
`openai_compatible`. The sidecar host also exports bounded recent-window
readiness gauges for the active live lanes under the `palllm.inference.*`
namespace.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Master switch for live-inference calls. | `false` | — | — | Always fallback (fine for offline / dev). | Every chat pays HTTP latency. | Flip on → make one `/api/chat` call → confirm `UsedFallback=false`. | Operator | No (config only) |
| `BaseUrl` | Root URL of the inference endpoint. Use `/v1/` for most OpenAI-compatible servers, including TensorRT-LLM, and `/v3/` for OpenVINO Model Server. | `http://127.0.0.1:11434/v1/` | — | — | The configured `models` probe fails and Foundry Local `/openai/models` / Ollama `/api/tags` fallback has nothing. | Same. | Confirm `curl <BaseUrl>models` returns JSON; for TensorRT-LLM also confirm `/health`, for OpenVINO this is `/v3/models`, and for Foundry Local also confirm `curl <serviceRoot>/openai/models`. | Operator | No |
| `Model` | Static fallback when `ModelTiers` is empty. | `qwen3.6:35b-a3b` | — | — | N/A | Pull the tag to your endpoint before flipping `Enabled`. | `pal_active_model_tier` MCP tool or `/api/health`. | Operator | No |
| `ApiKey` | Bearer token for hosted OpenAI-compat endpoints. | `null` | — | — | N/A | N/A | Endpoint-specific. | Operator | No |
| `PrefixCacheSalt` | Optional vLLM `cache_salt` forwarded on chat-completions requests. Use one stable, non-secret value per player/save/profile trust domain when a shared vLLM endpoint needs cache isolation. | `null` | `null` | 128 chars | Shared endpoint can reuse prefixes across trust domains if the server allows it. | One-salt-per-request fragmentation destroys prefix-cache hit rate. | Send two same-prefix requests with matching and different salts; confirm reuse only within the matching salt. | Operator | No |
| `PromptCacheKey` | Optional OpenAI-compatible `prompt_cache_key` forwarded on chat-completions requests. Use one stable, non-secret key per Palworld save/profile/task family when a hosted endpoint supports prompt-cache routing. | `null` | `null` | 128 chars | Hosted cache routing uses only prompt-prefix hash and may miss repeat PalLLM prefixes more often. | Over-specific keys fragment cache hits; keys must not contain player names, secrets, or save contents. | Send repeated same-prefix requests with matching and different keys; confirm cached-token and latency receipts improve only for matching keys. | Operator | Yes |
| `PromptCacheRetention` | Optional OpenAI-compatible `prompt_cache_retention` forwarded on chat-completions requests. Startup accepts `in_memory` or `24h`, but the field is omitted unless configured. | `null` | `null` | `in_memory` / `24h` | Endpoint chooses its default prompt-cache retention policy. | Unsupported endpoints can reject the field; longer retention can be cost/availability gated and should not compete with local-only gameplay defaults. | Replay the same long-prefix route with and without the field; record accepted request shape, cached-token receipts, p95 latency, and fallback counters. | Operator | Yes |
| `Verbosity` | Optional OpenAI-compatible `verbosity` hint for hosted or compatible endpoints. Startup accepts `low`, `medium`, or `high`; the field is omitted unless configured. | `null` | `null` | allowlist | Endpoint chooses its default answer length. | Unsupported local endpoints can reject the field; `high` can spend extra tokens and `low` can under-explain strict proof failures. | Replay the same player and proof routes with and without `verbosity`; record accepted request shape, generated tokens, p95 latency, parse quality, and fallback counters. | Operator | Yes |
| `SafetyIdentifier` | Optional OpenAI-compatible `safety_identifier` for explicitly configured hosted lanes. Use only a stable pseudonymous hash scoped to the PalLLM install/profile. | `null` | `null` | 128 chars | Hosted safety/abuse correlation gets no PalLLM-specific pseudonymous signal. | Raw player names, save paths, account ids, emails, or secrets would leak identity; unsupported endpoints can reject the field. | Send one hosted canary and confirm the request shape contains only the pseudonymous id; verify support/public bundles never store the value. | Operator | No |
| `StoreCompletions` | Optional OpenAI-compatible `store` switch for explicitly configured hosted lanes. Omitted by default for local-runtime portability and local-first privacy. | `null` | `null` | `true` / `false` | Hosted endpoint uses its default retention posture. | Unsupported endpoints can reject the field; `true` can intentionally opt a completion into provider-side eval/distillation storage and should not be used for ordinary Palworld companion turns. | Replay one hosted canary with `false`; confirm accepted request shape, no fallback regression, and support/public bundles do not record prompt or completion text from the canary. | Operator | No |
| `RequestMetadata` | Optional OpenAI-compatible `metadata` labels for hosted proof canaries. Use only bounded, low-cardinality labels such as route family, build channel, or canary id. | `{}` | 0 entries | 16 entries; key 64 chars; value 512 chars | Hosted stored-completion dashboards cannot filter PalLLM proof canaries by route/build label. | Unsupported endpoints can reject the field; raw prompt text, player names, save paths, account ids, secrets, or raw game state would leak identity/context. | Replay one hosted canary with `store=false` and metadata labels; confirm accepted request shape, no fallback regression, and public/support bundles contain only bounded labels. | Operator | No |
| `ClientRequestIdHeader` | Optional outbound request-correlation header for compatible inference endpoints. PalLLM sends the current bounded chat/proof request id as `x-client-request-id` or `x-request-id` only when this is set. | `null` | `null` | `x-client-request-id` / `x-request-id` | Provider and local logs cannot be joined by PalLLM's turn id unless the endpoint returns its own request id. | Unsupported endpoints may reject the header; request ids are high-cardinality and must not be used as metric labels or contain player identity, save paths, prompts, or secrets. | Replay one canary and confirm the outgoing header value is visible ASCII, provider request-id receipts line up, and public bundles contain no prompt text or raw identity. | Operator | No |
| `LlamaCppCachePrompt` | Optional llama.cpp `cache_prompt` pass-through for prompt-cache proof lanes. | `null` | `null` | `true` / `false` | PalLLM relies on the llama-server default and sends no endpoint-specific field. | Strict non-llama endpoints can reject the field; forcing it on an unproven model family can hide template/context cache misses. | Replay two same-prefix turns on the exact llama-server build and record accepted request shape, second-turn TTFT, slot id, cache metrics, and fallback counters. | Operator | No |
| `LlamaCppSlotId` | Optional llama.cpp `id_slot` selector for warm-slot canaries. | `null` | `-1` | `int.MaxValue` | llama-server picks a slot by its own policy. | Pinning the wrong slot can serialize unrelated work or evict hotter prompts; strict endpoints can reject the field. | Run mixed short companion and long proof turns with and without a pinned slot; confirm the foreground slot stays warm and background work is not starved. | Operator | No |
| `LlamaCppCacheReuseTokens` | Optional llama.cpp `n_cache_reuse` floor for measured stable-prefix reuse canaries. | `null` | `0` | `int.MaxValue` | llama-server decides how much prefix can be reused. | Too high can assume reuse across changed templates, adapters, contexts, or model builds and may reduce correctness or portability. | Measure a stable system-prompt prefix length, replay same/different-prefix turns, and confirm reuse only when the prefix identity matches. | Operator | No |
| `UseMediaCacheIds` | Adds stable content-hash `uuid` fields to prompt-level `InferencePrompt.UserContent` parts that carry local base64 image/video/audio data. | `true` | — | — | Repeated multimodal proof turns rely on endpoint-side content hashing. | Strict endpoints can reject unknown content-part fields; set false. | Capture outgoing `UserContent` canary JSON; local media parts have `palllm-{image|video|audio}-sha256-*`, while ordinary text chat stays a string. | Programmer | No |
| `MultimodalProcessor` | Optional vLLM-style `mm_processor_kwargs` for prompt-level multimodal `UserContent` canaries. Fields: `MinPixels`, `MaxPixels`, `MaxSoftTokens`, and `Fps`; all stay null by default and prompt-level overrides can supply route-specific values. | all fields `null` | pixels: `1`; fps `0.001`; soft tokens allowlist | pixels: `int.MaxValue`; fps `120`; soft tokens `70/140/280/560/1120` | Media may be over-compressed for OCR or small HUD crops. | Unsupported endpoints can reject the field; high caps can spend more prefill, VRAM, and TTFT than the route needs. | Replay the same image/video/audio `UserContent` canary with and without the caps; record accepted request shape, processor token/pixel evidence, p95 TTFT, VRAM pressure, and fallback counters. | Programmer | No |
| `Temperature` | Sampling temperature for the outgoing request; startup rejects non-finite or out-of-range values. | `0.7` | `0` | `2` | Deterministic / repetitive replies. | Incoherent / off-character replies. | Compare a chat turn at `0.2`, `0.7`, `1.2`. | Designer | Yes (per-request override) |
| `TopP` | Nucleus sampling cutoff; startup rejects non-finite or out-of-range values. | `0.8` | `0` | `1` | Flat / unsurprising diction. | Chaotic, runs off topic. | Side-by-side at `0.6` / `0.8` / `0.95`. | Designer | Yes |
| `PresencePenalty` | Dampens repeated tokens; startup rejects non-finite or out-of-range values. | `1.5` | `-2` | `2` | Model loops on a word. | Model avoids natural repetition. | Long reply → count repeated phrases. | Designer | Yes |
| `TokenBudgetField` | Selects which chat-completions field carries PalLLM's route token budget. `max_tokens` remains the compatibility default; `max_completion_tokens` is for endpoint-proven reasoning lanes that reject the older field. | `max_tokens` | allowlist | `max_tokens` / `max_completion_tokens` | Reasoning-only endpoints can reject `max_tokens`. | Older local endpoints can reject `max_completion_tokens`. | Replay the same route with both field names; record accepted request shape, usage counters, p95 latency, and fallback counters. | Operator | Yes |
| `FrequencyPenalty` | Optional OpenAI-compatible repetition-control hint. Omitted unless configured so strict endpoints stay portable. | `null` | `-2` | `2` | No extra pressure against repeated phrases. | Natural tactical terms can be over-suppressed. | Replay long companion turns with and without the field; compare repeated phrase rate, generated tokens, latency, and fallback counters. | Designer | Yes |
| `TopK` | Optional local-runtime `top_k` sampler hint. Omitted unless configured so strict endpoints stay portable. | `null` | `1` | `65536` | No top-k cap beyond endpoint defaults. | Too small can make replies flat; unsupported endpoints can reject it. | Replay the same companion/task routes with and without the field; compare style, loop rate, token count, p95 latency, and fallback counters. | Designer | Yes |
| `MinP` | Optional local-runtime `min_p` sampler hint. Omitted unless configured so strict endpoints stay portable. | `null` | `0` | `1` | No minimum relative probability filter beyond endpoint defaults. | Too high can prune useful low-probability tactical wording. | Replay creative and strict routes; verify accepted request shape, useful style delta, stable parse success, p95 latency, and fallback counters. | Designer | Yes |
| `RepetitionPenalty` | Optional local-runtime `repetition_penalty` sampler hint. Omitted unless configured; `1.0` is normally neutral. | `null` | `0` | `2` | Values below `1.0` can encourage repetition. | Values above `1.0` can over-suppress natural repeated terms. | Replay long companion turns; compare repeated phrase rate, tactical-term quality, generated tokens, latency, and fallback counters. | Designer | Yes |
| `EnableThinking` | Sends `enable_thinking` field to endpoints that accept it (Alibaba DashScope etc.). | `false` | — | — | `ResponseCleanup.StripReasoning` still runs; output unchanged. | Some endpoints reject unknown field → 400. | One chat at `true`, verify no 400s. | Operator | No |
| `ReasoningEffort` | Optional `reasoning_effort` chat-completions hint for reasoning-capable endpoints. Startup accepts `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, or `max`, but the field is omitted unless configured. | `null` | `null` | allowlist | No effect; endpoint uses its default reasoning budget. | Higher effort can raise TTFT, token use, heat, and 400 risk on unsupported endpoints. | One chat per candidate value against the exact server/model; confirm no 400s, acceptable TTFT, and stable fallback counters before promotion. | Operator | No |
| `ThinkingTokenBudget` | Optional vLLM `thinking_token_budget` cap for endpoints launched with a reasoning parser. The field is omitted unless configured, and startup rejects zero or negative values. | `null` | `1` | `int.MaxValue` | Reasoning models can spend most of `MaxTokens` on hidden reasoning before producing visible text. | Unsupported endpoints can reject the field; too-low budgets can truncate useful reasoning or return weak final answers. | Replay the same reasoning route with no budget and with the configured budget; record reasoning-parser config, accepted request shape, visible/reasoning token usage, p95 latency, and fallback counters. | Operator | No |
| `Seed` | Optional OpenAI-compatible `seed` request hint for replay-oriented deterministic sampling. The field is omitted unless configured because support is endpoint-specific. | `null` | `null` | 32-bit integer | Replays rely on prompt/request identity only. | A fixed seed can create a false sense of determinism across changed model/runtime/replica layouts, and unsupported endpoints can reject the request. | Send the same request twice with the same seed on the exact server/model; record served model id, runtime version, system fingerprint if exposed, and output drift before promotion. | Operator | No |
| `RequestPriority` | Optional vLLM-compatible `priority` request hint for foreground scheduling. The field is omitted unless configured; lower values are more urgent on vLLM servers launched with `--scheduling-policy priority`. | `null` | `int.MinValue` | `int.MaxValue` | Shared servers run first-come-first-served unless their own scheduler settings help short prompts. | Non-zero values can be rejected by vLLM FCFS-only servers or strict non-vLLM endpoints; overusing foreground values can starve proof/docs lanes. | Queue one short companion turn beside one long proof/docs prompt; confirm lower priority value wins queue time and fallback counters stay stable. | Operator | No |
| `ServiceTier` | Optional OpenAI-compatible `service_tier` request hint for endpoint-proven routing lanes. The field is omitted unless configured. | `null` | `null` | `auto` / `default` / `flex` / `priority` / `scale` | Hosted or compatible endpoints use their default service lane. | Unsupported local endpoints can reject the field; `priority` / `scale` can cost more, and `flex` can be too slow for live player turns. | Replay the same route with and without the tier; record accepted request shape, queue/TTFT evidence, p95 latency, cost posture where applicable, and fallback counters. | Operator | Yes |
| `ParallelToolCalls` | Optional OpenAI-compatible `parallel_tool_calls` request hint. The field is omitted unless configured; set `false` only when qualifying strict action/directive lanes that must return zero or one tool call. | `null` | `null` | `true` / `false` | Endpoint default decides whether tool-call fan-out is allowed. | Unsupported endpoints can reject unknown fields; setting `true` without a fan-out contract can produce multiple actions for a route that expects one. | On the exact server/model, send a strict tool-call canary with `false` and confirm zero-or-one call, schema validity, and stable fallback counters before promotion. | Operator | No |
| Prompt-level `StructuredOutputs` | Internal `InferencePrompt` hook for vLLM-specific `structured_outputs` canaries. Use it for route-owned choice, regex, JSON, grammar, or structural-tag constraints only after the exact vLLM endpoint proves support. | `null` | `null` | valid JSON | vLLM-only constrained-decoding shapes stay unavailable outside `response_format`. | Strict non-vLLM endpoints can reject the field; stale or over-broad constraints can hide parser failures. | Replay the exact route with and without `structured_outputs`; record accepted request shape, backend id, parse/schema validation, p95 latency, token usage, and fallback counters. | Programmer | Yes |
| Prompt-level `Tools` / `ToolChoice` | Internal `InferencePrompt` hook for strict route-specific tool-call canaries. Ordinary companion chat omits both fields; callers that opt in forward OpenAI-compatible `tools` and `tool_choice` verbatim, and PalLLM preserves returned `tool_calls` as a receipt. | `null` | `null` | valid JSON | No model-native tool-call proof lane. | Unsupported endpoints can reject the fields, and tool-call-only responses produce no player text unless the route handles the receipt. | Send a strict canary with one local guarded-action schema; confirm accepted request shape, returned `tool_calls`, parse stability, p95 latency, and deterministic fallback on malformed or empty content. | Programmer | Yes |
| Prompt-level `Prediction` | Internal `InferencePrompt` hook for route-specific predicted-output canaries. Ordinary companion chat omits `prediction`; proof/docs lanes can supply a stable expected scaffold after the exact endpoint proves support. | `null` | `null` | valid JSON | Proof/docs regeneration gets no predicted-output acceleration. | Unsupported endpoints can reject the field, and stale predictions can hide whether latency wins come from cache or from a matching scaffold. | Replay the same proof/docs route with and without `prediction`; record accepted request shape, accepted/rejected prediction-token counters when exposed, p95 latency, and fallback counters. | Programmer | Yes |
| Prompt-level `Logprobs` / `TopLogprobs` | Internal `InferencePrompt` hook for route-specific confidence and evaluator canaries. Ordinary companion chat omits `logprobs` and `top_logprobs`; callers that opt in can request output-token probability receipts, and PalLLM preserves returned choice-level `logprobs` JSON. | `null` | `null` | `TopLogprobs` 0-20 | No model-native token-confidence receipt. | Unsupported endpoints can reject the fields, and large top-logprob payloads can increase latency and response bytes. | Replay the exact validator/evaluator route with and without `logprobs`; record accepted request shape, returned logprob receipt, p95 latency, response size, and fallback counters before promotion. | Programmer | Yes |
| Prompt-level `Modalities` / `Audio` | Internal `InferencePrompt` hook for isolated audio-output canaries. Ordinary companion chat omits `modalities` and `audio`; callers that opt in can request OpenAI-compatible text/audio output and PalLLM preserves returned `message.audio` JSON on `InferenceResult.AudioJson`. | `null` | `null` | `Modalities`: `text` / `audio`; `Audio`: valid JSON | No model-native audio-output receipt. | Unsupported endpoints can reject the fields, and audio payloads can increase response bytes or omit a usable text mirror. | Replay the exact voice route with and without `modalities` / `audio`; record accepted request shape, returned audio receipt, text mirror, p95 latency, response size, and fallback counters before promotion. | Programmer | Yes |
| Prompt-level `UserContent` | Internal `InferencePrompt` hook for route-specific multimodal input canaries. Ordinary companion chat keeps the user message as a plain string; callers that opt in can forward OpenAI/vLLM-style content-part arrays for `text`, `image_url`, `video_url`, `input_audio`, or endpoint-proven `audio_url`. Local base64 media gets stable `uuid` fields when `UseMediaCacheIds=true`, and prompt-level `MultimodalProcessor` can override configured processor caps for that route. | `null` | `null` | valid JSON user `content` value | No unified text+media proof hook outside the dedicated vision path. | Unsupported endpoints can reject media content parts or `mm_processor_kwargs`; remote media URLs can add SSRF and latency risk unless admission controls block private hosts and redirects. | Replay text-only, image, video, and audio content parts on the exact endpoint; record accepted request shape, media byte caps, processor kwargs, p95 latency, parse stability, and fallback counters before promotion. | Programmer | Yes |
| `StopSequences` | Optional OpenAI-compatible `stop` delimiter list forwarded as a JSON array. The field is omitted unless at least one trimmed delimiter is configured; startup accepts up to four non-empty, unique, 128-character-or-shorter entries. | `[]` | empty list | 4 entries | The endpoint runs until natural stop or `MaxTokens`, which can waste tokens on strict delimiter routes. | Unsupported endpoints can reject unknown fields, and overly broad delimiters can clip useful companion text. | Replay the exact route with and without the delimiters; confirm lower generated tokens, no 400s, no clipped text, and stable fallback counters before promotion. | Operator | No |
| `TimeoutSeconds` | End-to-end timeout budget for the chat request. With `ResponseHeadersRead`, PalLLM also applies this budget while reading the response body. | `60` | `1` | `600` | Timeouts on slow models -> fallback path. | Hung calls tie up the circuit breaker. | Trip deliberately to test. | Programmer | No |
| `MaxResponseBytes` | Hard cap on the returned chat-completions JSON payload size. | `65536` (64 KB) | `1024` | `10485760` | Legitimate long structured replies can be rejected. | Runaway or misconfigured endpoints can waste memory before the sidecar gives up. | Point at a test server that returns >64 KB chat-completions JSON -> confirm fallback/error path. | Programmer | No |
| `ModelCatalogMaxResponseBytes` | Hard cap on OpenAI-compatible `models` catalogs (`/v1/models` or OpenVINO `/v3/models`), Foundry Local `/openai/models`, and Ollama `/api/tags` discovery payloads used by tier probing. | `262144` (256 KB) | `1024` | `10485760` | Large local model catalogs can be treated as unavailable. | Misbehaving discovery endpoints can waste memory during background tier probes. | Point tier probing at a test server that returns >256 KB model-catalog JSON -> confirm the probe returns an empty set instead of buffering the body. | Programmer | No |
| `CircuitBreakerFailureThreshold` | Consecutive failures before the breaker trips. Set to 0 to disable. | `5` | `0` | `50` | Breaker trips on transient blips → unnecessary fallback. | Stuck endpoint burns timeout budget many times before tripping. | Simulate 5 failures → verify 6th short-circuits. | Programmer | No |
| `CircuitBreakerCooldownSeconds` | How long the breaker stays open before a trial call. | `30` | `1` | `600` | Constant retries on a dead endpoint. | Long outages stall recovery. | Wait through the window → verify a trial call fires. | Programmer | No |
| `MaxTransientRetries` | Single-retry policy for 5xx / timeout / 429. Only one retry is almost always correct. | `1` | `0` | `5` | Transient blips → fallback unnecessarily. | Deterministic 4xx retried → wasted latency. | Scripted handler returns 500 then 200. | Programmer | No |
| `TransientRetryBackoffMs` | Base backoff before retry (jittered). | `500` | `0` | `10000` | Zero backoff → hammer recovering endpoint. | Slow recovery. | Measure second attempt latency. | Programmer | No |
| `ResidencyProvider` | Provider-aware residency-control mode for compatible local runtimes. `Auto` resolves from `BaseUrl`; `Disabled` suppresses hints even on known hosts. | `Auto` | — | — | Warmup stays generic even when the host can keep a model resident more deliberately. | Wrong explicit provider can send runtime-specific hints to the wrong host. | Compare `/api/health` warmup snapshot before/after flipping between `Auto`, `Disabled`, `Ollama`, and `LmStudio`. | Operator | No |
| `ResidencyTtlSeconds` | Residency TTL budget in seconds for compatible local runtimes. `0` disables residency hints without disabling warmup. | `1800` | `0` | `604800` | Models unload too aggressively between turns on idle local hosts. | Memory stays occupied longer than you want on shared machines. | Warm the lane, idle past the TTL, then confirm the next turn either stays hot or reloads as expected. | Operator | No |
| `EnableWarmup` | Enable the bounded startup / tier-change warmup path. | `true` | - | - | First live turn after startup or graduation pays the full model-load cost. | Idle hosts do tiny keepalive calls when paired with `WarmupIntervalSeconds`. | Call `POST /api/inference/warmup`; confirm `InferenceWarmup.Status=ready`. | Operator | No |
| `WarmupMaxTokens` | Token budget for the warmup request. Keep it tiny. | `1` | `1` | `32` | Warmup can fail to trigger some model runtimes cleanly. | Burns unnecessary local or remote tokens for a non-user-facing request. | Inspect the upstream request body; confirm the configured token-budget field stays tiny. | Programmer | No |
| `WarmupIntervalSeconds` | Optional periodic keepalive cadence. `0` disables periodic keepalives and warms only on startup + tier changes. Recent successful live chat on the same model suppresses the keepalive tick inside this window. Compatible local runtimes can pair this with explicit residency hints (`keep_alive` for Ollama-native warmups, `ttl` for LM Studio chat-completions). | `0` | `0` | `86400` | Local inference runtime unloads the active model during idle gaps. | Extra background inference traffic for a lane that may not need to stay hot. | Set to `300`, chat once, then confirm `InferenceWarmup.LastLiveInferenceAtUtc` is populated and the next `periodic_keepalive` skip is visible in `StatusMessage`. | Operator | No |
| `TierProbeIntervalSeconds` | How often the background worker re-probes for tier availability changes. | `30` | `5` | `3600` | Probe chatter at the endpoint. | Slow graduation when the large model finishes downloading. | Set to `10`, pull a model, watch log. | Operator | No |
| `ThermalGate.Enabled` | Opt-in GPU-temperature gate: routes live inference to the deterministic fallback director when the primary GPU is already throttling, so chat latency stays predictable instead of absorbing the throttled round-trip time. | `false` | - | - | No thermal protection — heavy inference runs on top of an already-throttling card. | Under hot conditions live inference bypasses to fallback even while the GPU can still work. | Set `PALLLM_FAKE_GPU_TEMP_C=90`, flip this to `true`, issue one `POST /api/chat`, expect `ErrorType=thermal_gated` in the inference trace. | Operator | No |
| `ThermalGate.RejectAboveC` | GPU temperature (°C) at which live inference is gated to fallback. Set to match the temperature your specific card begins thermal throttling. | `83.0` | `50.0` | `105.0` | Gate triggers while the card could still do useful work. | Gate triggers too late — inference still slows down behind throttling. | Compare GPU peak temp from nvidia-smi vs. observed inference latency; tune down until throttled rounds stop hitting the model. | Operator | Yes |
| `ThermalGate.WarnAboveC` | GPU temperature (°C) that surfaces a "warm" warning on dashboards without rejecting calls. Use as a visibility signal before the reject threshold kicks in. | `78.0` | `40.0` | `100.0` | Operators never see the heads-up before hard gating. | Dashboard warns constantly on normal workloads. | Trigger with `PALLLM_FAKE_GPU_TEMP_C=79`; confirm the gate reports `Warn` but still lets calls through. | Operator | Yes |
| `ThermalGate.CacheTtlSeconds` | How long a successful sensor read is trusted before resampling. | `5` | `1` | `60` | Stale reads can hide a real spike for up to this many seconds. | Sensor reads burn CPU / spawn `nvidia-smi` too often under bursty chat. | Issue a burst of chats, watch the gate's sample source/time. | Programmer | Yes |

---

`pal connect lmstudio` writes `ResidencyProvider=LmStudio` and the selected
TTL alongside the local `/v1/` BaseUrl, which is safer than relying on `Auto`
when an operator runs LM Studio on a non-default port.

`pal connect llamacpp` writes `ResidencyProvider=Disabled` because current
`llama-server` residency is controlled by server flags such as
`--sleep-idle-seconds`, not by a per-request chat-completions TTL. Use PalLLM
warmup plus `/metrics` proof to decide whether idle sleep belongs on a player
lane.

The table above is now the **baseline** inference configuration, not the full
story for `/api/chat`. The shipped chat runtime layers a role-aware
`InferenceExecutionPlanner` on top of the active model tier and chooses a
per-turn execution profile such as `fast-reactive`, `fast-interactive`,
`fast-deliberate`, `fast-creative`, `dense-interactive`,
`dense-deliberate`, or `dense-creative`. Those profiles can override
temperature, max-token budget, `top_p`, presence penalty, thinking mode,
`preserve_thinking` on supported Qwen-compatible servers, and whether a turn
is allowed to invoke live vision augmentation. They also enforce smaller or
larger Palworld prompt/evidence budgets per lane so fast turns stay compact
and deliberate turns can retain more bridge, screenshot, and memory context. If a
`ChatRequest` also supplies `Temperature` or `MaxTokens`, the request wins for
that one turn.

The chat path also applies non-configurable local safety rails after the
upstream JSON body is parsed: `SystemPrompt` is capped at `16,000` characters,
live-model `AssistantMessage` text is capped at `8 KiB` before it reaches
memory, optional TTS, the bridge outbox, or the HTTP response, and remembered
content is capped at `4 KiB` per entry. These caps protect local latency and
bridge payload size; raise model `MaxTokens` only after proving the rendered
reply still fits that player-facing envelope.

## Model tiers (within Inference)

Controls the cascade of local models. Each entry is `{ Id, Model, Priority, Description? }`. Higher `Priority` wins. See [Configuring tiered model loading](OPERATIONS.md#configuring-tiered-model-loading) for the full walkthrough.

| Parameter | Purpose | Default | Guidance |
|---|---|---|---|
| `Id` | Human-readable tier id (`small`, `large`, `vision`). | — | Non-empty, unique per tier. Surfaces in logs + OTel spans. |
| `Model` | Exact model tag the inference endpoint exposes. | — | For llama-server / vLLM / LM Studio / TensorRT-LLM: the configured `models` catalog id (`/v1/models`). For OpenVINO: `/v3/models`. For Foundry Local: the loaded alias or `/openai/models` id. The operator's curated identifiers are unsloth UD-* strings (e.g. `Qwen3.6-35B-A3B-UD-Q8_K_XL`) — see `LOCAL_MODELS_INVENTORY.md`. |
| `Priority` | Higher wins when the model is available. | — | Use non-contiguous values (1, 10, 100) so new tiers can slot between existing ones. |
| `Description` | Free-form operator note. | `null` | Not consumed at runtime — helps reviewers understand the config. |

Shipping defaults (Pass 337 onward): `small=gemma-4-E4B-it-UD-Q4_K_XL` (priority 1) + `large=Qwen3.6-35B-A3B-UD-Q8_K_XL` (priority 10). Both identifiers resolve to files in the operator's curated `D:\Models` library — see `LOCAL_MODELS_INVENTORY.md`. Empty list disables tier orchestration entirely and `Inference.Model` is used verbatim.

In practice that means model tiers answer **which lane is active**, while the
execution planner answers **how that lane should behave for this turn**.

Residency control is orthogonal to tier selection: the tier system chooses the
active model lane, while `ResidencyProvider` and `ResidencyTtlSeconds` decide
whether PalLLM should ask a compatible local host to keep that lane resident
between turns.

Runtime visibility for the active lane now lives in `/api/health`,
`POST /api/inference/warmup`, the MCP `pal_active_model_tier` tool, and the
`palllm://model/tier/active` resource. If you are tuning tier behavior and
cannot see the active tier or warmup state there, the runtime and docs have
drifted.

---

## Fallback block

Controls the deterministic director — the runtime's "personality without a model". On by default so offline / dev deployments still produce coherent replies.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Master switch for the fallback director. | `true` | — | — | Disabled fallback + disabled inference = null `AssistantMessage`. | — | Flip off with inference also off → confirm null reply. | Operator | No |
| `UseWhenInferenceDisabled` | Activate fallback when `Inference.Enabled=false`. | `true` | — | — | Offline replies are null. | — | — | Operator | No |
| `UseWhenInferenceFails` | Activate fallback when a live call fails. | `true` | — | — | Model blips become user-visible errors. | — | Trip the circuit breaker → verify fallback rescues. | Operator | No |
| `EnablePolicyBypass` | Allow the runtime to skip inference for reactive tasks the deterministic director handles well. | `true` | — | — | Every chat pays inference latency even for trivial barks. | Live inference never runs for reactive tasks. | Reactive bark test → confirm `ResponsePath = fallback_policy_bypass`. | Designer | No |
| `PreferForReactiveBarks` | Route reactive barks to fallback by default. | `true` | — | — | — | — | — | Designer | No |
| `PreferForRoutineTacticalTasks` | Route routine tactical tasks to fallback. | `true` | — | — | — | — | — | Designer | No |
| `PreferForRecoveryAndCampTasks` | Route recovery / camp tasks to fallback. | `true` | — | — | — | — | — | Designer | No |
| `RecentMemoryWindow` | How many recent conversation entries the fallback considers. | `12` | `0` | `100` | Fallback forgets context → feels shallow. | Irrelevant old entries crowd the prompt budget. | Try `4`, `12`, `32` and inspect system prompt. | Designer | No |
| `EnableReflection` | Periodic deterministic memory consolidation that writes back `reflection`-tagged entries without calling a live model. | `false` | — | — | — | High-salience moments stay fragmented in the raw stream. | Extra reflection entries accumulate in long sessions, though still within the bounded store. | Turn on in a real session; inspect memory store. | Designer | No |
| `PreferTaskFocus` | Prompt-level instruction to stay task-focused rather than lean into performative roleplay. | `false` | — | — | — | Companions feel stiff. | A/B a chat session. | Designer | No |
| `MaxCharacterRequestsPerMinute` | Rate-limit ceiling per character; excess goes to fallback. `0` = disabled. | `0` | `0` | `1000` | Runaway producer burns inference budget. | Rate-limited users see fallback replies at steady state. | Smoke loop → confirm limit kicks in. | Operator | No |

---

## Vision block

Controls the optional multimodal path. **Off by default.** When disabled, chat augmentation with screenshots falls back to `SnapshotVisionFallback` composition.

The vision success path uses the same streamed response reader as text
inference, so describe/world-state calls do not first buffer a full JSON body
string and can tolerate both plain-text and text-part-array message payloads.
With OTLP enabled, these calls also emit `generate_content` GenAI spans
and the matching duration/token histograms when the upstream returns
usage data, plus the same bounded recent-window readiness gauges the chat lane
uses.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Master switch for vision calls. | `false` | — | — | Chat augmentation uses snapshot fallback only. | Every screenshot pays a vision model call. | `/api/vision/describe` with an image → `Success=true`. | Operator | No |
| `BaseUrl` | Multimodal endpoint root. Often same host as Inference. | `http://127.0.0.1:11434/v1/` | — | — | — | — | `curl <BaseUrl>models`. | Operator | No |
| `Model` | Vision model tag. | `gemma4:e2b` | — | — | — | Larger models = more latency per chat turn. | — | Operator | No |
| `ApiKey` | Bearer token. | `null` | — | — | — | — | — | Operator | No |
| `Temperature` | Sampling temperature. | `0.2` | `0` | `2` | Extraction is brittle. | Hallucinated scene content. | Compare outputs. | Designer | Yes |
| `DefaultMaxTokens` | Reply size cap for describe calls. | `180` | `16` | `2000` | Descriptions truncated mid-sentence. | Slow + token-heavy. | Check reply length. | Designer | Yes |
| `TimeoutSeconds` | End-to-end timeout budget. With `ResponseHeadersRead`, PalLLM also applies this budget while reading the response body. | `30` | `1` | `600` | Timeouts on slow models -> fallback. | Stalls. | - | Programmer | No |
| `MaxResponseBytes` | Hard cap on the returned vision chat-completions JSON payload size. | `65536` (64 KB) | `1024` | `10485760` | Legitimate long structured extracts can be rejected. | Runaway or misconfigured endpoints can waste memory before the sidecar gives up. | Point at a test server that returns >64 KB vision JSON -> confirm `Success=false`. | Programmer | No |
| `MaxImageBytes` | Hard cap on incoming image payload (after base64 decode). | `6291456` (6 MB) | `65536` | `67108864` | Screenshots rejected as oversized. | Memory pressure on burst requests. | POST oversized image → expect 400. | Programmer | No |
| `UseMediaCacheIds` | Add a stable content-hash `uuid` to outgoing `image_url` parts so vLLM-compatible endpoints can identify repeated screenshots. | `true` | — | — | Repeated media has no cache identity. | Strict endpoints may reject unknown content-part fields; set false. | Capture outgoing vision JSON -> image part has `uuid=palllm-image-sha256-*`. | Programmer | No |
| `MultimodalProcessor` | Optional vLLM-style `mm_processor_kwargs` for screenshot/image requests. Fields: `MinPixels`, `MaxPixels`, `MaxSoftTokens`, and `Fps`; all stay null by default. | all fields `null` | pixels: `1`; fps `0.001`; soft tokens allowlist | pixels: `int.MaxValue`; fps `120`; soft tokens `70/140/280/560/1120` | Small HUD crops can lose useful detail after processor resizing. | Unsupported endpoints can reject the field; high caps can increase vision tokens, TTFT, and VRAM pressure. | Replay the same screenshot with and without caps; record accepted request shape, processor token/pixel evidence, p95 latency, and fallback counters. | Programmer | No |
| `UseForChatAugmentation` | Splice vision description into chat system prompt. | `true` | — | — | Rich chat context lost. | Extra inference call per turn with image. | — | Designer | No |
| `EnableScreenshotWatcher` | Background worker that polls `Bridge/Screenshots/` and runs structured extraction. | `false` | — | — | Screenshots don't update the world snapshot. | Continuous background vision cost. | Drop a PNG → watch `VisionCallCount`. | Operator | No |
| `ScreenshotPollIntervalMs` | Watcher poll cadence. | `15000` | `1000` | `3600000` | Watcher chatters every second. | Slow world-state updates. | — | Operator | No |
| `MaxScreenshotsPerPoll` | Bound screenshots processed per tick. | `2` | `1` | `20` | Backlog grows unbounded. | Vision model monopolised, chat latency spikes. | Drop many PNGs → monitor chat latency. | Operator | No |
| `PendingScreenshotMaxFiles` | Retention cap for pending screenshots. | `32` | `0` | `10000` | Files pruned too eagerly. | Disk growth during vision outages. | — | Operator | No |
| `PendingScreenshotMaxAgeHours` | Max age for pending screenshots. | `1` | `0` | `720` | Fresh screenshots pruned. | Stale screenshots re-processed days later. | — | Operator | No |
| `UseStructuredOutputs` | Send `response_format: json_schema` to endpoints that support it (llama-server, vLLM, LM Studio). | `true` | — | — | Model returns prose → extraction brittle. | Endpoint may reject unknown field → fall back to prose. | — | Designer | No |

---

## TTS block

Controls text-to-speech synthesis. **Off by default.**

Successful TTS responses are read header-first and bounded by
`MaxResponseBytes`, so a misconfigured endpoint cannot stream an
arbitrarily large audio body into the sidecar.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Master switch. | `false` | — | — | Chat replies silent. | Every reply pays TTS compute + disk write. | `/api/tts/synthesize` returns audio. | Operator | No |
| `BaseUrl` | TTS endpoint. With `RequestFormat=simple`, POSTs `{ text, voice }`; with `openai_speech`, targets OpenAI-compatible `/v1/audio/speech`. Both expect audio bytes. | `http://127.0.0.1:5002/synthesize` | — | — | — | — | `curl` the URL. | Operator | No |
| `RequestFormat` | Outbound request JSON shape: `simple` for legacy local adapters, `openai_speech` for OpenAI-compatible speech APIs such as vLLM-Omni Qwen3-TTS. | `simple` | allowlist | `simple` / `openai_speech` | `/v1/audio/speech` endpoints reject `{ text, voice }`. | Legacy adapters can reject `input` / `response_format`. | Scripted TTS server asserts request body fields. | Operator | No |
| `Model` | Optional model id sent by the `openai_speech` shape. Leave empty for local servers that infer the served model; set for stricter OpenAI-compatible providers. | `null` | `null` | 256 chars | Strict providers can reject missing `model`. | Wrong model id can route to the wrong voice model or fail. | Send one canary and confirm accepted request shape. | Operator | No |
| `DefaultVoice` | Voice tag used when requests don't specify. | `en_US-amy-medium` | — | — | — | — | — | Designer | Yes (per-request) |
| `ResponseFormat` | Audio container requested by `openai_speech`: `wav`, `mp3`, `opus`, `aac`, `flac`, or `pcm`. Also acts as PalLLM's MIME fallback when a speech endpoint omits `Content-Type` or returns generic binary. Ignored by `simple`. | `wav` | allowlist | allowlist | Defaults to WAV even when a realtime PCM canary wanted raw audio. | Unsupported values fail strict speech endpoints. | Request `pcm` against a vLLM-Omni speech lane and confirm `.pcm` plus `PlaybackHint=raw_pcm`; `/api/bridge/proof` should show zero launch attempts, optional MIME-parameter timing metadata for `audio/L16; rate=...; channels=...`, sample-format / byte-order / mixer-conversion receipts, sample-frame / partial-frame receipts, mixer-queue and buffer-duration receipts, speech sequence/supersession/prior-buffer-overlap receipts, request-to-speech / outbox-to-speech / delivery-to-speech lag when matching events exist, and `FailureCode=raw_pcm_native_mixer_required` while `native_audio_mixer_enabled=false`. When a native callback is enabled, verify failure-specific `native_audio_mixer_unavailable` / `native_audio_mixer_failed` / `native_audio_mixer_rejected` receipts or a started `PlaybackMode=native_mixer` receipt before promoting raw PCM. | Operator | No |
| `ApiKey` | Bearer token. | `null` | — | — | — | — | — | Operator | No |
| `TimeoutSeconds` | End-to-end timeout budget for synthesis. | `30` | `1` | `600` | - | - | - | Programmer | No |
| `MaxCharacters` | Hard cap on synthesis input length. | `1200` | `100` | `50000` | Long replies truncated. | OOM on some TTS engines. | Send a 1200-char text → confirm OK. | Programmer | No |
| `MaxResponseBytes` | Hard cap on the returned audio payload size. | `16777216` (16 MB) | `1024` | `268435456` | Legitimate long-form audio can be rejected. | Runaway or misconfigured servers can waste memory and disk before the sidecar gives up. | Point at a test server that returns >16 MB audio → confirm `Success=false`. | Programmer | No |
| `MaxStoredFiles` | Retention cap for synthesised audio files. | `128` | `0` | `100000` | — | Disk growth. | — | Operator | No |
| `MaxStoredAgeHours` | Max age for synthesised audio files. | `24` | `0` | `720` | — | — | — | Operator | No |

---

Additional optional voice-routing keys under `Tts`:

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `WarmVoice` | Optional softer voice for cozy, companion, or reassurance-forward speech artifacts generated by the chat runtime. Falls back to `DefaultVoice` when unset. | `null` | - | - | Warm replies stay on the generic voice. | - | Generate a cozy chat reply with TTS on; confirm the speech artifact voice changes. | Designer | No |
| `SteadyVoice` | Optional neutral operational voice for guide, planner, and support-oriented speech artifacts. Falls back to `DefaultVoice` when unset. | `null` | - | - | Operational replies stay on the generic voice. | - | Generate a route or guide reply with TTS on; inspect `ChatResponse.Speech.Voice`. | Designer | No |
| `UrgentVoice` | Optional command-weighted voice for directive, sentry, or protector-style speech artifacts. Falls back to `DefaultVoice` when unset. | `null` | - | - | Urgent replies stay on the generic voice. | - | Generate a high-pressure combat or alert reply with TTS on; inspect `ChatResponse.Speech.Voice`. | Designer | No |
| `WhisperVoice` | Optional low-intensity voice for hush, stealth, or whisper-style speech artifacts. Falls back to `DefaultVoice` when unset. | `null` | - | - | Quiet replies stay on the generic voice. | - | Generate a stealth or whisper reply with TTS on; inspect `ChatResponse.Speech.Voice`. | Designer | No |

Those four optional voice slots are only used on the chat-driven speech path.
Direct `POST /api/tts/synthesize` callers still control `Voice` explicitly per
request.

## ASR block

Controls audio transcription. **Off by default.**

ASR targets OpenAI-compatible `/v1/audio/transcriptions` endpoints. PalLLM
accepts base64 audio over JSON at `POST /api/audio/transcribe`, validates the
decoded byte count and MIME type locally, then sends multipart `file` data to
the configured endpoint. This keeps mic/clip transcription isolated from normal
chat fallback: typed chat still answers even if ASR is off, slow, or failing.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Master switch. | `false` | - | - | Voice/clip transcription returns a disabled status. | Every transcription request can pay model latency. | `/api/audio/transcribe` with a short WAV returns `Success=true`. | Operator | No |
| `BaseUrl` | OpenAI-compatible transcription endpoint. | `http://127.0.0.1:8000/v1/audio/transcriptions` | - | - | Enabled ASR fails startup if this is missing or relative. | Wrong endpoint can waste timeout budget. | `curl` or a scripted ASR server asserts multipart shape. | Operator | No |
| `Model` | Model id sent as the multipart `model` field. Required when ASR is enabled. | `null` | non-empty when enabled | 256 chars | Strict endpoints reject missing model. | Wrong model routes audio to the wrong lane. | One short transcription canary against the exact endpoint. | Operator | No |
| `ApiKey` | Bearer token. | `null` | - | - | N/A for localhost endpoints. | Secret should stay in env/user config, not committed JSON. | Handler test or endpoint access log confirms auth header. | Operator | No |
| `Language` | Optional default multipart `language` hint used when a request does not supply its own value. Use a two-letter ISO-639-1 code such as `en` after endpoint proof. | `null` | allowlist | 2 ASCII letters | Endpoint must auto-detect language, adding latency and avoidable mis-detection risk. | Wrong language can degrade transcripts or fail strict endpoints. | Replay the same short clip with and without the hint; confirm accepted request shape, p95 latency, transcript stability, and no fallback regression. | Operator | No |
| `Prompt` | Optional default multipart `prompt` hint used when a request does not supply its own value. Use for short pronunciation or command-vocabulary hints only. | `null` | `null` | 2048 chars | No default ASR vocabulary/style nudge for operator voice lanes. | Prompt text can leak private names or reduce accuracy when it does not match the audio language. | Replay a known command clip and confirm the field is accepted, prompt text is not stored in proof/support bundles, and transcript quality improves or stays neutral. | Operator | No |
| `TimeoutSeconds` | End-to-end HTTP client timeout for transcription. | `30` | `1` | `600` | Slow but healthy ASR calls return sanitized failure. | Hung ASR jobs occupy the shared speech/audio admission lane longer. | Point ASR at a hanging endpoint and confirm bounded failure. | Operator | No |
| `ResponseFormat` | Multipart `response_format` sent to the transcription endpoint. `json` is the compatibility default; `verbose_json` is for endpoint-proven metadata canaries that still return a top-level `text` field. | `json` | allowlist | `json` / `verbose_json` | Verbose-only timestamp/segment proof stays unavailable. | Unsupported endpoints can reject the value or return larger JSON, especially with segment metadata. | Replay the same short clip with `json` and `verbose_json`; confirm accepted request shape, capped response size, transcript parse, and no raw segment/token text in proof surfaces. | Operator | No |
| `TimestampGranularities` | Optional multipart `timestamp_granularities[]` entries for verbose transcription timing canaries. Allowed values are `segment` and `word`; validation rejects this list unless `ResponseFormat=verbose_json`. Returned metadata is reduced to a content-free timing receipt. | `[]` | allowlist | `segment` / `word` | No segment/word timing proof for player-speech latency or coverage checks. | Unsupported endpoints can reject the field or return larger verbose JSON. | Replay a short clip with `segment` and `word`; confirm `Timing.*Returned=true`, coverage fields are populated, and no segment/word text reaches proof surfaces. | Operator | No |
| `ChunkingStrategy` | Optional multipart `chunking_strategy` sent to compatible file-transcription endpoints. Empty keeps strict local ASR servers field-free; `auto` lets the endpoint pick VAD-based chunk boundaries. | `null` | allowlist | `auto` | Longer utterances rely only on PalLLM/client-side clipping and endpoint defaults. | Unsupported endpoints can reject the field; automatic chunking can change latency and transcript boundaries. | Replay the same short and pause-heavy clips with and without `auto`; compare accepted request shape, p95 latency, endpointing receipts, transcript stability, and fallback behavior. | Operator | No |
| Verbose segment quality receipt | Derived automatically when `ResponseFormat=verbose_json` returns `segments[]` quality metadata. Records compact `avg_logprob`, `compression_ratio`, `no_speech_prob`, and segment-temperature counts/extrema only. | n/a | n/a | n/a | No segment-level quality review hints for noisy or silent speech turns. | Verbose JSON may be larger; unsupported endpoints may omit the fields. | Replay clear/noisy/silent clips; confirm `Quality.*` counts, review flags, and no segment text, token ids, raw audio, or verbose JSON in proof surfaces. | Operator | No |
| `Temperature` | Optional multipart `temperature` field for endpoints that support transcription sampling control. Omitted when `null` so strict local servers stay field-free until proven. | `null` | `0` | `1` | Endpoint default decides ASR sampling behavior. | Unsupported endpoints can reject the field; high values can make transcripts less stable. | Replay the same short clip with and without the field; compare transcript stability and latency. | Operator | No |
| `Seed` | Optional multipart `seed` field for endpoint-proven vLLM transcription replay canaries. Omitted when `null` so strict local ASR servers stay field-free. | `null` | `int.MinValue` | `int.MaxValue` | ASR replay proof relies only on clip/request identity. | Unsupported endpoints can reject the field; fixed seeds do not prove determinism across changed model/runtime builds. | Replay the same short clip twice with the same seed; record served model id, runtime version, transcript drift, latency, and fallback counters. | Operator | No |
| `RequestLogprobs` | Optional multipart `include[]=logprobs` confidence probe. Returned token logprobs are reduced to counts/min/average/low-confidence count only; token text is not stored. | `false` | - | - | No model-native ASR confidence receipt. | Unsupported endpoints can reject the field or return larger JSON. | Use a compatible endpoint; confirm `Confidence.LogprobsReturned=true` and no token text in the response receipt. | Operator | No |
| `LowConfidenceLogprobThreshold` | Threshold used to count low-confidence ASR tokens when logprobs are returned. | `-1.0` | `-20` | `0` | Too many noisy transcripts look ready. | Normal transcripts may be flagged for review too often. | Replay a known clear clip and noisy clip; compare `LowConfidenceTokenCount`. | Operator | No |
| `MaxAudioBytes` | Hard cap on decoded request audio bytes before any upstream call. | `4194304` (4 MB) | `1024` | `268435456` | Longer clips reject before transcription. | Large payloads can pressure memory and GPU queues. | POST an oversized base64 clip and expect validation failure/no HTTP call. | Programmer | No |
| `MaxResponseBytes` | Hard cap on upstream transcription JSON. | `65536` (64 KB) | `1024` | `10485760` | Large verbose transcript metadata can be rejected. | Misbehaving endpoints can waste memory before failure. | Test server returns >64 KB JSON -> `Success=false`. | Programmer | No |
| `MaxTranscriptCharacters` | Hard cap on the returned transcript text. | `8192` | `1` | `1048576` | Valid longer dictation is rejected. | Overlong transcripts can crowd downstream UIs or prompts. | Test server returns a long `text` value and confirm bounded failure. | Programmer | No |
| `MaxTurnDurationMs` | Content-free duration cap used to grade optional `AudioTranscribeRequest.Endpointing` receipts. | `30000` | `1` | `int.MaxValue` | Longer legitimate dictation is flagged for review. | Long clips can hide stale or queued player speech. | Send Endpointing total > cap and confirm response `Endpointing.Status=review`. | Programmer | No |
| `PreSpeechPaddingMs` | Target client/native VAD pre-roll before detected speech. | `300` | `0` | `int.MaxValue` | First syllables may be clipped. | More audio is sent before speech, raising latency/bytes. | Send low `LeadingSilenceMs` and confirm `pre_speech_padding_below_target`. | Programmer | No |
| `EndpointSilenceMs` | Target trailing silence used to close a spoken turn. | `500` | `1` | `int.MaxValue` | Assistant may cut in during natural pauses. | Voice turns feel slow and stale. | Send low/high `TrailingSilenceMs` and confirm endpointing flags. | Programmer | No |

## Bridge block

Controls the UE4SS Lua bridge filesystem pipeline. On by default.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Master switch for inbox drain + outbox write. | `true` | — | — | Mod silently does nothing. | — | Drop a JSON into `Bridge/Inbox` → verify archive. | Operator | No |
| `PollIntervalMs` | `BridgeInboxWorker` poll cadence. | `1000` | `100` | `60000` | Worker hammers disk. | Slow event delivery → stale companions. | — | Programmer | No |
| `MaxEventsPerPoll` | Bound events processed per tick. | `32` | `1` | `1000` | Backlog grows. | One tick dominates the sidecar thread. | Drop 100 events → monitor drain rate. | Programmer | No |
| `ArchiveProcessedEvents` | Move drained events to `Bridge/Archive` instead of deleting. | `true` | — | — | — | Disk usage grows to retention cap. | — | Operator | No |
| `OutboxEnabled` | Write reply envelopes to `Bridge/Outbox`. | `true` | — | — | Lua side has nothing to render. | — | — | Operator | No |
| `OutboxMaxFiles` | Retention cap. | `100` | `0` | `100000` | Lua loses replies before rendering. | Disk growth if Lua isn't running. | — | Operator | No |
| `OutboxMaxAgeHours` | Max age. | `24` | `0` | `720` | — | Stale replies re-rendered. | — | Operator | No |
| `ArchiveMaxFiles` | Retention cap for archived events + processed screenshots. | `500` | `0` | `100000` | Diagnostic history shallow. | Disk growth. | — | Operator | No |
| `ArchiveMaxAgeHours` | Max age. | `72` | `0` | `720` | — | — | — | Operator | No |
| `FailedMaxFiles` | Retention cap for `Bridge/Failed`. | `200` | `0` | `100000` | Lose diagnostic context for rare failures. | — | — | Operator | No |
| `FailedMaxAgeHours` | Max age. | `168` (1 week) | `0` | `8760` | — | — | — | Operator | No |
| `DiagnosticsMaxFiles` | Retention cap for `ui_probe` widget dumps. | `128` | `0` | `10000` | Miss a widget during a short probe session. | Disk growth over long probe campaigns. | — | Operator | No |
| `DiagnosticsMaxAgeHours` | Max age. | `168` | `0` | `8760` | — | — | — | Operator | No |

---

## Session block

Controls session persistence (memory + relationships across sidecar restarts).

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Load/save `session.json` on startup + demand. | `true` | — | — | Every restart = fresh slate. | — | Restart → verify memories persist. | Operator | No |
| `MaxPersistedBytes` | Hard cap for `session.json` before load deserialization starts. | `8388608` | `1024` | — | Legitimate long-session state can be skipped and fall back to `session.json.bak` or a fresh session. | Cold-boot load and corruption recovery pay a larger local-file read budget. | Write an oversized file; verify `POST /api/session/reload` returns a stable oversized status or loads from `.bak`. | Operator | No |
| `EnableAutosave` | `SessionAutosaveWorker` periodic flush. | `true` | — | — | Crash loses more history. | — | — | Operator | No |
| `AutosaveIntervalSeconds` | Autosave cadence. | `60` | `5` | `3600` | Disk churn. | Crash can lose up to `N` seconds of chat. | — | Operator | No |

---

## Automation block

Controls action-intent emission — the narrow bridge between runtime suggestions and in-game side effects. **Off by default.**

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `Enabled` | Master switch for action-intent emission. | `false` | — | — | Companions stay purely advisory. | Every chat can trigger game-side actions. | `/api/chat` → `ChatResponse.Action` non-null. | Operator | No |
| `AllowedActions` | Allowlist of action type ids. Empty = no intents emitted regardless of `Enabled`. | `[]` | — | — | No automation. | Unchecked action classes reach the Lua executor. | Lua allowlist also gates — belt + suspenders. | Operator | No |
| `EmitToOutbox` | Attach intent to outbox envelope (so Lua consumes it). | `true` | — | — | Intent only visible on HTTP response — useful for dry-run. | Lua will act on intents. | — | Operator | No |

Known safe action types: `waypoint_suggest`, `recall_pals`, `request_craft_queue`. See [OPERATIONS § Enabling the action executor](OPERATIONS.md#enabling-the-action-executor).

---

## Http block

Controls the sidecar's HTTP-contract surfaces and its protective admission
control for local heavy-work lanes. Admission limits reject overload before it
queues too long; request timeouts stop a lane that accepted work but then hung
past its measured endpoint budget.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `OpenApiCacheMinutes` | Server-side output-cache TTL for `/openapi/v1.json` and `/openapi/v1.yaml`. `0` disables the cache. | `10` | `0` | `1440` | Contract requests always regenerate the document; harmless but noisier under SDK/codegen polling. | Stale docs linger after route edits until the cache expires. | Fetch the same OpenAPI URL twice while profiling allocations; set to `0` when iterating on route metadata locally. | Operator | No |
| `FeatureCatalogCacheMinutes` | Private-cache TTL for `GET /api/features`, `GET /api/bridge/proof`, and `GET /api/release/readiness`, paired with `ETag`s so dashboards and browser tooling can revalidate cheaply. `0` keeps the `ETag` but disables the max-age. `GET /api/inference/performance` always uses `private, no-cache, must-revalidate` so the latest recent-window lane posture is revalidated every poll. | `60` | `0` | `1440` | Polling dashboards redownload the same inspection payloads every time. | Catalog/proof/readiness changes after a deployment take longer to become visible to a browser that respects cache headers. | Fetch `/api/features` or `/api/release/readiness`, capture the `ETag`, then repeat with `If-None-Match` and expect `304`; `/api/inference/performance` should also return `304` on a matching revalidation request. | Operator | No |
| `SelfDescriptionCacheSeconds` | Server-side output-cache TTL and private-cache max-age for `GET /api/describe`, paired with an `ETag`. Keep short because the payload is read-heavy but includes live operator posture and health. `0` disables the short TTL and falls back to `private, no-cache, must-revalidate`. | `15` | `0` | `3600` | AI/MCP clients reconnecting or handshaking repeatedly force a full manifest rebuild every time. | Callers can briefly see stale runtime posture after configuration or health changes. | Fetch `/api/describe`, confirm `Cache-Control: private, max-age=<n>` and an `ETag`, then repeat with `If-None-Match` and expect `304`; set to `0` if you are iterating rapidly on runtime posture copy. | Operator | No |
| `UpstreamSnapshotCacheSeconds` | Private-cache TTL for `GET /api/mcp/upstream`, paired with an `ETag`. Short by design because the background discovery worker refreshes snapshots at runtime. | `5` | `0` | `3600` | Tooling repeatedly redownloads identical upstream snapshots during polling. | Stale upstream-tool catalogs linger briefly after a discovery refresh. | Fetch `/api/mcp/upstream`, then repeat with `If-None-Match` and expect `304`; compare freshness against `McpClient:DiscoveryIntervalSeconds`. | Operator | No |
| `LocalArtifactMaxBytes` | Maximum JSON bytes PalLLM will read from local evidence/status/diagnostic artifacts before surfacing them through `GET /api/release/readiness`, `GET /api/self-healing/status`, `GET /api/relationships/lifetime`, or the ui-probe diagnostics behind `GET /api/bridge/ui-probe` and `GET /api/bridge/proof`. Oversized, truncated, or malformed files degrade to stable sanitized `invalid`/`unreadable` or empty payloads instead of echoing raw file-read errors. | `65536` | `1024` | `16777216` | Legitimate evidence or diagnostic files above the cap are ignored or treated as invalid until regenerated or the cap is raised deliberately. | A tampered or runaway local artifact can consume more memory and delay readiness, watchdog, relationship, or ui-probe inspection surfaces. | Write a deliberately oversized `Runtime/ReleaseEvidence/latest-smoke.json`, `Runtime/SelfHealingEvidence/latest-self-healing.json`, `Runtime/LifetimeRelationships/latest.json`, or a large `Bridge/Diagnostics/palllm-ui-probe-*.json`, then fetch the corresponding surface and confirm it degrades cleanly without buffering the file. | Operator | No |
| `ApiRequestBodyMaxBytes` | Maximum HTTP request-body bytes accepted on `/api/*` and `/mcp` JSON routes before model binding. Field validators still apply tighter semantic caps after binding. | `10485760` (10 MiB) | `1024` | `134217728` | Valid large screenshot/base64 test payloads can get `413` before route validation. | Oversized JSON can allocate more memory before the semantic validators reject it. | Set `PalLLM:Http:ApiRequestBodyMaxBytes=1024`, POST a larger `/api/chat` or `/mcp` JSON body, and expect sanitized `413 ProblemDetails`. | Operator | No |
| `ChatConcurrentRequests` | Concurrent request budget for `POST /api/chat`, `POST /api/chat/stream`, `POST /api/chat/party`, and `POST /api/inference/warmup`. | `2` | `1` | `128` | Callers hit `429` during brief bursts even though the host has spare CPU. | Too many chats run at once and local-model latency spikes for everyone. | Fire overlapping chat-class requests; confirm the first few complete and excess calls shed with `429`. | Operator | No |
| `ChatQueueLimit` | Queue depth behind the chat concurrency gate. | `4` | `0` | `1024` | Excess work sheds immediately; lowest latency, least buffering. | Callers wait in line too long and interactive latency becomes unpredictable. | Burst 5-10 chat calls and compare p95 latency vs reject rate. | Operator | No |
| `ChatRequestTimeoutSeconds` | Outer timeout for `POST /api/chat`, `POST /api/chat/stream`, `POST /api/chat/party`, and `POST /api/inference/warmup`. One-shot routes return sanitized `503 ProblemDetails`; the SSE stream reports timeout as `event: error` with `reason=request_timeout` because headers were already flushed. | `130` | `1` | `600` | Slow-but-healthy local models can be cut off before the inference client's configured retry has a chance to fall back cleanly. | Hung requests hold slots longer and make tail latency look like queue pressure. | Point inference at a deliberately hanging endpoint and confirm one-shot routes return `503`; stream confirms an `error` event and no `final` event. | Operator | No |
| `VisionConcurrentRequests` | Concurrent request budget for `POST /api/vision/*`. | `1` | `1` | `128` | Large screenshot batches serialize; backlog drains slowly. | Vision work competes with itself and can starve chat on a shared local GPU. | Drop multiple screenshots and watch both drain speed and `/api/chat` latency. | Operator | No |
| `VisionQueueLimit` | Queue depth behind the vision concurrency gate. | `2` | `0` | `1024` | Screenshot/process requests shed immediately under overlap. | Burst screenshot work waits too long and stale frames become less useful. | Run manual screenshot processing while posting describe/world-state calls. | Operator | No |
| `VisionRequestTimeoutSeconds` | Outer ASP.NET Core request timeout for `POST /api/vision/describe`, `POST /api/vision/world-state`, and `POST /api/vision/screenshots/process`. | `45` | `1` | `600` | Large but valid images can time out before the vision client returns its structured failure/description payload. | Stale screenshot work can occupy the single vision lane while chat waits for shared hardware. | Point vision at a hanging endpoint and confirm a sanitized `503 ProblemDetails` response. | Operator | No |
| `TtsConcurrentRequests` | Concurrent request budget for speech/audio endpoints (`POST /api/tts/synthesize` and `POST /api/audio/transcribe`). | `2` | `1` | `128` | Audio requests reject under modest overlap. | Too many synth/transcribe jobs pile onto the host and steal CPU from chat. | Fire overlapping synth/transcribe requests; measure 429s vs end-to-end latency. | Operator | No |
| `TtsQueueLimit` | Queue depth behind the speech/audio concurrency gate. | `4` | `0` | `1024` | Extra synth/transcribe requests fail fast. | Audio work lingers long enough that players hear stale speech or see stale transcripts. | Compare queueing behavior with short and long text/audio payloads. | Operator | No |
| `TtsRequestTimeoutSeconds` | Outer ASP.NET Core request timeout for `POST /api/tts/synthesize` and `POST /api/audio/transcribe`. | `45` | `1` | `600` | A slow voice or ASR server can time out before returning an artifact/transcript. | Stale audio jobs linger and can produce speech/transcripts that no longer match the current turn. | Point TTS or ASR at a hanging endpoint and confirm a sanitized `503 ProblemDetails` response. | Operator | No |

When a limiter rejects a request, PalLLM returns `429 Too Many Requests`
with a `ProblemDetails` body. When an accepted one-shot heavy request exceeds
its configured lane timeout, PalLLM returns `503 Service Unavailable` with a
sanitized `ProblemDetails` body. `/api/chat/stream` has already sent the SSE
headers, so the same timeout is surfaced as a sanitized `error` event and the
stream closes without `final`. Both are deliberate: predictable rejection or
timeout is better than turning every local-model call into a slow tail-latency
event.

---

## Auth block

The auth block also governs browser-origin checks for `/mcp`. Requests without
an `Origin` header still work, loopback browser origins are allowed
automatically, and non-loopback browser origins must be explicitly allowlisted
via `PalLLM:Auth:McpAllowedOrigins[]`.

Controls the bearer-token guard on `/api/*` and `/mcp`. **Off by default** — safe for localhost, required when exposing the sidecar beyond.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `ApiKey` | Bearer key. When non-empty, `/api/*` and `/mcp` require `Authorization: Bearer <key>`. | `null` | — | — | LAN / public deployments unauthenticated. | — | Set → verify 401 without header, 200 with. | Operator | No |
| `ProtectMetrics` | Also gate `/metrics` behind the key. | `false` | — | — | Prometheus can still scrape. | Scraper needs credential config. | — | Operator | No |
| `ProtectHealth` | Also gate `/health/live` + `/health/ready`. | `false` | — | — | Orchestrator probes pass. | Probes need credential config. | — | Operator | No |
| `McpAllowedOrigins[]` | Exact `http://` or `https://` browser origins allowed to call `/mcp` when they send `Origin`. Loopback browser origins stay allowed automatically, and hosts that do not send `Origin` are unaffected. | `[]` | - | - | Non-loopback browser hosts get `403 Forbidden` even when auth is correct. | Over-broad allowlists weaken the localhost browser-origin guard. | Call `/mcp` from a non-loopback browser host, confirm `403`, then add the exact origin and confirm success. | Operator | No |

---

## McpClient block (upstream MCP servers)

Controls the pool of external MCP servers PalLLM probes (the sidecar acting as an MCP *client*, alongside its own MCP server role). **Empty by default** — when no upstreams are configured, the discovery worker skips entirely and `/api/mcp/upstream` returns `[]`.

| Parameter | Purpose | Default | Min | Max | Too low | Too high | Test | Owner | Runtime-adjustable? |
|---|---|---|---|---|---|---|---|---|---|
| `UpstreamServers[].Id` | Human-readable id used in logs, status endpoints, tool output. | — | — | — | Omitted upstreams are skipped silently. | — | `GET /api/mcp/upstream` — id appears. | Operator | No |
| `UpstreamServers[].Url` | Streamable HTTP endpoint URL of the remote MCP server. | — | — | — | Empty URL → upstream skipped. | — | `curl <Url>` rejects GET but POST works. | Operator | No |
| `UpstreamServers[].BearerToken` | Optional `Authorization: Bearer` header for protected upstreams. | `null` | — | — | — | — | Upstream `/api/health` requires the key. | Operator | No |
| `UpstreamServers[].Enabled` | Per-server switch so operators can pause a noisy upstream without deleting config. | `true` | — | — | — | — | Flip to `false` → upstream disappears from snapshot. | Operator | No |
| `DiscoveryIntervalSeconds` | Periodic re-discovery cadence. | `300` | `5` | `86400` | Probe chatter at every upstream. | Slow pickup of newly-added upstream tools. | Add a tool on upstream, wait `N` seconds, confirm it appears. | Operator | No |
| `DiscoveryTimeoutSeconds` | Per-probe HTTP timeout. | `10` | `1` | `300` | False negatives when upstream is slow. | One slow upstream pins the worker. | Point at slow URL, observe tick duration. | Operator | No |
| `MaxToolsPerServer` | Hard cap on cached tool names per upstream snapshot. | `128` | `1` | — | Real upstream catalogs are truncated too aggressively. | One noisy upstream can grow the cached snapshot and JSON response body more than necessary. | Point at an upstream with >128 tools; confirm `/api/mcp/upstream` caps the list. | Operator | No |
| `MaxResourcesPerServer` | Hard cap on cached resource URIs per upstream snapshot. | `128` | `1` | — | Real upstream resource catalogs are truncated too aggressively. | One noisy upstream can grow the cached snapshot and JSON response body more than necessary. | Point at an upstream with a large resource list; confirm the snapshot stays bounded. | Operator | No |
| `MaxPromptsPerServer` | Hard cap on cached prompt names per upstream snapshot. | `64` | `1` | — | Real upstream prompt catalogs are truncated too aggressively. | One noisy upstream can grow the cached snapshot and JSON response body more than necessary. | Point at an upstream with a large prompt list; confirm the snapshot stays bounded. | Operator | No |
| `MaxMetadataEntryLength` | Hard cap on the length of any cached upstream tool/resource/prompt identifier after whitespace/control-character normalization. | `256` | `1` | — | Legitimate long identifiers are trimmed more than desired. | Very long upstream identifiers churn larger strings through the cache, logs, and response bodies. | Return a very long tool name from a test upstream and confirm the snapshot trims it. | Operator | No |

Invalid or unsupported upstream URLs surface `Connected=false` with
`ErrorCode=invalid_endpoint`. Timeout failures surface `ErrorCode=timeout`.
All probe failures use sanitized `Error` strings rather than raw exception
text. Successful snapshots are bounded too, so one configured upstream cannot
force unbounded catalog metadata into the cache.

---

## Performance budget

Per `/api/chat` turn, targets on a typical localhost setup:

| Stage | Target | Dominant cost |
|---|---|---|
| Request parse + validation | < 1 ms | `PalApiRequestValidator` |
| Bridge drain (if due) | < 10 ms | Filesystem reads |
| Memory recall | < 2 ms | Deterministic embedding + cosine + stack-bounded exact-token rerank |
| Fallback decision + compose | < 5 ms | 19-strategy classifier |
| Live inference (if on) | 200 ms – 20 s | Model compute (dominates) |
| Vision augmentation (if on + image) | +200 ms – 5 s | Vision model compute |
| Presentation plan + outbox write | < 3 ms | JSON serialise + file I/O |
| MCP call overhead | < 10 ms | Transport framing |

**If the fallback-only path exceeds 30 ms on your hardware**, something is wrong — profile via the OpenTelemetry spans and GenAI client metrics (see [OPERATIONS § Enabling distributed tracing](OPERATIONS.md#enabling-distributed-tracing)) to find the slow step.

**If live-inference chat exceeds 30 s**, either lower `TimeoutSeconds` to trip faster (and let the fallback director rescue), or reduce `MaxTokens` / graduate to a smaller tier.

Recent-window readiness is also exposed directly at
`GET /api/inference/performance`, `GET /health/ready`, `GET /metrics`, and in
the Field Console. The built-in assessment defaults are:

- Chat lanes: `3.0 s` target, `8.0 s` ceiling.
- Vision lanes: `2.5 s` target, `6.0 s` ceiling.
- Statuses: `healthy` means the lane is meeting its target budget with strong
  success ratio; `degraded` means it is still mostly working but drifting past
  the target; `critical` means failures or ceiling misses are too frequent;
  `insufficient_data` means the recent window looks good but has fewer than
  three live samples; `no_data` means nothing recent has executed in that lane.

---

## Tuning workflow

1. **Change one parameter at a time.** The 19 fallback strategies + 3
   model tiers + vision + TTS + automation interact non-trivially.
   If you flip three knobs and the feel changes, you won't know which
   caused it.
2. **Test against a fixed seed.** `ChatRequest.RequestId` + a
   deterministic mock snapshot gives you reproducible before/after
   comparisons.
3. **Watch the telemetry backend** (if OTel is enabled). The `pal.chat`
   span's `pal.response_path`, `pal.used_fallback`, and
   `pal.visual_context_source` tags tell you exactly which route the
   turn took and why, while `gen_ai.client.operation.duration`,
   `gen_ai.client.token.usage`, and the `palllm.inference.*` readiness gauges
   show whether the active model lane is blowing its latency or token budget.
   `GET /api/inference/performance` also carries each lane's latest
   `LastUpstreamRequestId`, `LastUpstreamProcessingMs`, `LastResponseId`,
   `LastUpstreamQueueMs`, `LastUpstreamTimeToFirstTokenMs`,
   `LastUpstreamPrefillMs`, `LastUpstreamDecodeMs`, `LastSystemFingerprint`,
   `LastFinishReasons`, `LastPromptTokens`, `LastCompletionTokens`,
   `LastTotalTokens`, `LastCachedPromptTokens`,
   `LastCompletionReasoningTokens`, audio-token detail counts, and
   predicted-output accepted/rejected counts when the upstream exposes them,
   so replay and seed checks can correlate a specific provider/local log line
   and completion, tell whether a call stopped normally, compare provider-side
   processing time against PalLLM-observed latency, separate upstream queue,
   TTFT, prefill, and decode pressure when the server exposes it, prove
   cache/prediction wins, account for reasoning or audio-heavy lanes,
   distinguish output drift from backend drift, and compare the latest call's
   token cost against the lane average.
4. **Prefer operator defaults unless you have a specific goal.**
   Every default ships tuned for a baseline use case. Deviation
   should be driven by an observed need, not intuition.
5. **Drift-check after each change.** The CI doc-drift audit catches
   accidental drift in route counts, feature counts, fallback counts,
   and test counts — run
   `scripts/doctor.ps1` for the operator-side equivalent.
