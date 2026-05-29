# Research notes — 2026-05 model implementation pass

Last audited: `2026-05-28`

This doc captures the implementation-grade research that drove
Pass 112's multimodal recipes + the 2026 Blackwell defaults.
Citations preserved here so a future contributor (human or
agent) can verify each claim against upstream and bump versions
without redoing the survey.

> **Honest scope note.** This file started as a snapshot of *what was
> true on 2026-05-22* and now carries dated refresh sections above that
> base survey. Bug-fix references are pinned to the serving versions
> current on the refresh date. When a future pass picks this up, some
> pitfalls will already be fixed upstream. The doc shape is meant to be
> re-runnable: replace the version pins, re-cite, and the rest of the
> structure stays.

## 0.99j. 2026-05-28 refresh: ASR seeds stay replay-only

Current vLLM OpenAI-compatible transcription docs list `seed` among the
multipart sampling parameters accepted by `/v1/audio/transcriptions`. That
makes it useful for PalLLM voice-proof replays: the same short clip can be
sent twice to the same local ASR endpoint and compared for transcript drift,
latency, served model id, and fallback behavior. It is not a general
determinism guarantee because changed model weights, tokenizer/audio frontend,
runtime version, replica layout, and endpoint defaults can still change the
result.

Implementation impact: Pass 417 adds optional `PalLLM:Asr:Seed`.
`HttpAudioTranscriptionClient` forwards it as multipart `seed` only when the
operator explicitly configures it; normal ASR calls and strict
OpenAI-compatible transcription endpoints remain field-free by default. The
public `AudioTranscriptionRequest` shape is unchanged, so per-request audio
payloads still carry only audio bytes plus optional language/prompt hints.

Focused sibling scan impact: active sibling workspaces reinforced the generic
pattern of voice-lane replay receipts and deterministic prompt/audio seeds, but
PalLLM lifted no sibling code, names, prompts, assets, branding, product
identity, or unrelated IP.

Primary source:

- vLLM OpenAI-compatible server transcription parameters:
  https://docs.vllm.ai/en/stable/serving/openai_compatible_server/

## 0.99i. 2026-05-25 refresh: vLLM thinking budgets stay proof-lane scoped

Current vLLM reasoning-output guidance documents request-level
`thinking_token_budget` for models served with reasoning parsers. The budget
caps reasoning tokens before the model is forced toward its configured
reasoning end marker; leaving the field unset means the endpoint applies no
extra thinking cap beyond normal generation limits. The SamplingParams API
reference lists the same field as the maximum token count for thinking
operations. For PalLLM, that makes the field useful as a latency canary for
Qwen-style reasoning lanes, but not a default: strict local endpoints can
reject it, and too-small budgets can weaken the visible answer.

Implementation impact: Pass 416 adds optional
`PalLLM:Inference:ThinkingTokenBudget` plus prompt-level
`InferencePrompt.ThinkingTokenBudget`. `HttpJsonInferenceClient` serializes
`thinking_token_budget` only when a positive budget is explicitly configured or
supplied by a route-owned prompt; normal companion chat remains field-free.
Startup validation rejects zero and negative values so operators use
`EnableThinking=false` for fast non-thinking turns instead of sending an
ambiguous zero-budget request. Promotion proof now requires reasoning-parser
config, accepted request shape, visible/reasoning token usage, p95 latency, and
fallback counters before a budget is trusted.

Focused sibling scan impact: active sibling workspaces reinforced the generic
rule that reasoning routes need real budget proof, not just an
`enable_thinking` or parser label. PalLLM lifted no sibling code, names,
prompts, assets, branding, product identity, or unrelated IP.

Primary sources:

- vLLM reasoning outputs and thinking budget control:
  https://docs.vllm.ai/en/latest/features/reasoning_outputs/
- vLLM SamplingParams API reference:
  https://docs.vllm.ai/en/latest/api/vllm/sampling_params/

## 0.99h. 2026-05-25 refresh: ASR language/prompt defaults stay opt-in

Current OpenAI transcription reference keeps `language` and `prompt` as
optional multipart form fields. The `language` hint uses ISO-639-1 style
values such as `en` and is documented as an accuracy/latency helper, while
the `prompt` hint is contextual text that should match the audio language.
Current speech-to-text guidance also frames prompting as useful for
recognizing uncommon words, preserving segment context, and nudging
punctuation/style. Current vLLM OpenAI-compatible serving documentation lists
`/v1/audio/transcriptions` as an ASR-only supported API, so the PalLLM side
should keep ASR hints scoped to proven ASR endpoints rather than the ordinary
chat lane.

Implementation impact: Pass 415 adds optional `PalLLM:Asr:Language` and
`PalLLM:Asr:Prompt` defaults. `AudioTranscriptionClient` now uses
request-level `Language` / `Prompt` when present and otherwise falls back to
the configured defaults; both remain omitted when blank. Startup validation
keeps the language hint to two ASCII letters and caps the prompt at `2048`
characters so it stays a short pronunciation/command vocabulary nudge, not a
player identity, save path, secret, raw transcript, or durable prompt store.
The docs now make clear that proof bundles retain ASR receipts but never store
prompt hints, token text, raw audio, verbose JSON, or transcript content.

Focused sibling scan impact: active sibling workspaces reinforced the generic
pattern of push-to-talk / client-VAD consent gates and short ASR vocabulary
hints. PalLLM lifted no sibling code, names, prompts, assets, branding,
product identity, or unrelated IP.

Primary sources:

- OpenAI create transcription API reference:
  https://developers.openai.com/api/reference/resources/audio/subresources/transcriptions/methods/create
- OpenAI speech-to-text prompting guidance:
  https://developers.openai.com/api/docs/guides/speech-to-text
- vLLM OpenAI-compatible server supported APIs:
  https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/
- ASP.NET Core options validation and `ValidateOnStart`:
  https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/options?view=aspnetcore-10.0

## 0.99g. 2026-05-25 refresh: vLLM structured_outputs stays prompt-level

Current vLLM stable structured-output guidance says the older `guided_*`
request fields were removed and should be expressed through
`structured_outputs` (`choice`, `regex`, `json`, `grammar`,
`structural_tag`, etc.) or through portable OpenAI-compatible
`response_format` where that is enough. The OpenAI-compatible server docs also
show vLLM extra parameters being merged directly into the JSON payload when a
client is not using an `extra_body` wrapper. For PalLLM, the safe shape is a
route-owned prompt hook, not a global config knob: ordinary companion chat must
keep omitting endpoint-specific fields, while proof callers can qualify one
exact vLLM route/model/backend shape at a time.

Implementation impact: Pass 414 adds prompt-level
`InferencePrompt.StructuredOutputs`. `HttpJsonInferenceClient` serializes it
as `structured_outputs` only when a caller supplies it, alongside the existing
portable `InferencePrompt.ResponseFormat` hook. The default request-shape test
now pins that normal companion chat omits `structured_outputs`, and the
structured-output transport test pins raw JSON forwarding. Model-collaboration,
TUNING, OPERATIONS, API, ARCHITECTURE, and feature-catalog docs now frame this
as a vLLM-specific proof lane with accepted request shape, backend id,
schema/constraint digest, parse/schema validation, token, latency, and
fallback receipts before promotion.

Focused sibling scan impact: active sibling workspaces reinforced the generic
pattern of pairing schema digest, route identity, and runtime request shape in
structured-output receipts. PalLLM lifted no sibling code, names, prompts,
assets, branding, product identity, or unrelated IP.

Primary sources:

- vLLM v0.21.0 structured outputs:
  https://docs.vllm.ai/en/v0.21.0/features/structured_outputs/
- vLLM OpenAI-compatible server extra-parameter guidance:
  https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/

## 0.99f. 2026-05-25 refresh: multimodal processor caps stay proof-only

Current vLLM multimodal serving guidance supports forwarding
`mm_processor_kwargs` through OpenAI-compatible requests for models whose
processors expose image/video controls. That makes route-level image/video
caps useful for PalLLM screenshot and media canaries: Qwen-style processors can
bound `min_pixels` / `max_pixels`, Gemma-style processors can bound
`max_soft_tokens`, and video lanes can carry `fps`. The field is not a broad
OpenAI compatibility guarantee, so strict endpoints must keep it omitted until
the exact endpoint/model has accepted the request shape.

Implementation impact: Pass 413 adds omitted-by-default
`PalLLM:Inference:MultimodalProcessor`, prompt-level
`InferencePrompt.MultimodalProcessor`, and
`PalLLM:Vision:MultimodalProcessor`. `HttpJsonInferenceClient` serializes
`mm_processor_kwargs` only for route-owned multimodal `UserContent` canaries,
and `HttpVisionClient` serializes it only for configured vision requests.
Startup validation bounds pixel values and `fps`, and accepts only the
soft-token budgets already used by the model-collaboration guidance: 70, 140,
280, 560, or 1120. Ordinary text chat remains field-free.

Focused sibling scan impact: active sibling workspaces reinforced the generic
pattern of processor caps plus cold/warm media proof for multimodal lanes.
PalLLM lifted no sibling code, names, prompts, assets, branding, product
identity, or unrelated IP.

Primary sources:

- vLLM multimodal inputs and processor kwargs:
  https://docs.vllm.ai/en/latest/features/multimodal_inputs/
- vLLM OpenAI-compatible server extra-parameter guidance:
  https://docs.vllm.ai/en/stable/serving/openai_compatible_server.html

## 0.99e. 2026-05-25 refresh: hosted metadata labels stay bounded

Current Chat Completions request docs expose optional `metadata` as a bounded
map: up to 16 key/value pairs, 64-character keys, and 512-character values.
That makes it useful for hosted proof labels and stored-completion filtering,
but it is not a local-runtime default. Strict local endpoints can reject the
field, and careless labels can leak identity or high-cardinality data.

Implementation impact: Pass 412 adds omitted-by-default
`PalLLM:Inference:RequestMetadata` plus prompt-level
`InferencePrompt.RequestMetadata`. `HttpJsonInferenceClient` now serializes
`metadata` only when explicitly configured or supplied by a route-owned
canary. Startup validation enforces the 16-entry / 64-key / 512-value bounds,
and prompt-level labels are trimmed, bounded, and merged over configured labels
before serialization. Docs require only low-cardinality proof labels such as
route family, build channel, or canary id; prompt text, player identity, save
paths, secrets, raw game state, and metric-label values remain forbidden.

Focused sibling scan impact: active sibling workspaces reinforced bounded
request metadata and request-id correlation as proof receipts, not prompts or
public assets. PalLLM lifted no sibling code, names, prompts, assets, branding,
product identity, or unrelated IP.

Primary sources:

- OpenAI Chat Completions reference:
  https://developers.openai.com/api/reference/resources/chat
- vLLM OpenAI-compatible server extra-parameter guidance:
  https://docs.vllm.ai/en/v0.21.0/serving/openai_compatible_server/

## 0.99d. 2026-05-25 refresh: llama.cpp prompt-cache hints stay explicit

Current llama.cpp server docs expose OpenAI-compatible chat completions while
also carrying llama-server-specific prompt-cache and slot controls such as
`cache_prompt`, slot endpoints, `id_slot`, `n_cache_reuse`, and
`--slot-prompt-similarity`. Those are useful low-latency proof hooks for a
stable PalLLM system prompt, but they are not portable OpenAI fields. Strict
non-llama endpoints can reject them, and cache reuse must be tied to the exact
model, tokenizer, chat template, adapter, context size, server build, and slot
state.

Implementation impact: Pass 411 adds omitted-by-default
`PalLLM:Inference:LlamaCppCachePrompt`, `LlamaCppSlotId`, and
`LlamaCppCacheReuseTokens`, plus prompt-level overrides. `HttpJsonInferenceClient`
now serializes `cache_prompt`, `id_slot`, and `n_cache_reuse` only when
explicitly configured or supplied by a route-owned canary. Startup validation
rejects slot ids below `-1` and cache-reuse floors below `0`; normal companion
chat stays field-free. Promotion proof requires accepted request shape,
same-prefix and changed-prefix replay, slot id, cache metrics, cache RAM
pressure, second-turn TTFT, and fallback counters.

Focused sibling scan impact: active sibling workspaces reinforced the generic
pattern of prompt-cache reuse counters, explicit off/on request shapes, and
warm-slot replay proof. PalLLM lifted no sibling code, names, prompts, assets,
branding, product identity, or unrelated IP.

Primary sources:

- llama.cpp server README:
  https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md
- vLLM OpenAI-compatible server extra-parameter guidance:
  https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/

## 0.99c. 2026-05-25 refresh: outbound request ids stay opt-in

Current OpenAI API debugging guidance recommends retaining provider request
ids and supports an operator-supplied `X-Client-Request-Id` header for
production support correlation. Current vLLM OpenAI-compatible serving docs
also expose request-id headers behind the server-side
`--enable-request-id-headers` flag. The portable PalLLM stance is therefore a
header canary, not a gameplay default: strict local endpoints should stay
field/header-minimal unless an operator proves support.

Implementation impact: Pass 410 adds
`PalLLM:Inference:ClientRequestIdHeader`, allowlisted to
`x-client-request-id` or `x-request-id`. When configured, normal chat turns
forward the existing PalLLM chat `RequestId` as a bounded visible-ASCII header
value; otherwise no request-correlation header is sent. Existing inference
request-shape tests now prove the default omission and both supported header
names without changing the total test count. Docs require that request ids
never contain prompts, save paths, player identity, secrets, or metric labels.

Primary sources:

- OpenAI API debugging and `X-Client-Request-Id` guidance:
  https://developers.openai.com/api/reference/overview
- vLLM OpenAI-compatible server request-id header guidance:
  https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/

## 0.99b. 2026-05-25 refresh: hosted store canaries stay explicit

Current Chat Completions request docs expose an optional `store` switch for
provider-side retention/eval workflows. That is useful as a hosted-lane proof
receipt, but it is not a local-first gameplay default: strict local endpoints
can reject the extra field, and `store=true` is the opposite of PalLLM's normal
privacy posture.

Implementation impact: Pass 409 adds omitted-by-default
`PalLLM:Inference:StoreCompletions` plus a prompt-level override. The inference
client serializes `store` only when explicitly configured, existing request
shape tests prove ordinary companion chat stays field-free, and operator docs
describe the safe canary as `store=false` on an endpoint that has already
proven support. Public/support bundles still must not retain prompt or
completion text from that canary.

Primary sources:

- OpenAI Chat Completions reference:
  https://developers.openai.com/api/reference/resources/chat
- Ollama OpenAI compatibility reference:
  https://docs.ollama.com/api/openai-compatibility
- vLLM OpenAI-compatible server / structured-output guidance:
  https://docs.vllm.ai/en/v0.21.0/features/structured_outputs/

## 0.99a. 2026-05-24 refresh: verbosity and safety-id canaries stay hosted-only

Current Chat Completions request docs expose optional `verbosity` values and a
`safety_identifier` request field. These are useful only after the exact hosted
or compatible endpoint proves support: `verbosity=low` can reduce generated
tokens for terse player/proof turns, while `safety_identifier` gives hosted
providers a pseudonymous safety-correlation signal without sending player
identity.

Implementation impact: Pass 407 adds omitted-by-default
`PalLLM:Inference:Verbosity` and `PalLLM:Inference:SafetyIdentifier`, plus
prompt-level overrides for route-owned canaries. Startup validation allowlists
`low`, `medium`, and `high`, caps safety ids at 128 characters, and the runtime
suppresses blank or oversized request identifiers before serialization. The
docs require a stable non-secret hash and explicitly forbid player names, save
paths, account ids, emails, secrets, or raw save contents.

Primary sources:

- OpenAI Chat Completions reference:
  https://developers.openai.com/api/reference/resources/chat
- vLLM OpenAI-compatible server reference:
  https://docs.vllm.ai/en/latest/serving/openai_compatible_server.html

## 0.99. 2026-05-24 refresh: hosted prompt-cache canaries stay opt-in

Current OpenAI prompt-caching guidance says prompt cache hits need exact
prefix matches, that `prompt_cache_key` can influence cache routing for common
prefixes, that `prompt_cache_retention` can be set on Chat Completions, and
that allowed retention values are `in_memory` and `24h`. The same guide says
cached-token counts are exposed through `usage.prompt_tokens_details`, which
PalLLM already parses into token receipts.

Implementation impact: Pass 406 adds `PalLLM:Inference:PromptCacheKey` and
`PalLLM:Inference:PromptCacheRetention` plus prompt-level overrides. Both are
omitted by default, validated at startup, normalized before serialization, and
documented as endpoint-proven cache-routing canaries. This keeps strict local
servers field-free while giving hosted or compatible proof/docs lanes a way to
measure accepted request shape, cached-token receipts, p95 latency, and fallback
counters before promotion.

The same refresh confirmed that current Chat Completions service tiers include
`scale`; Pass 406 extends the existing `ServiceTier` allowlist without changing
the default omitted request shape.

Primary sources:

- OpenAI prompt caching guide:
  https://developers.openai.com/api/docs/guides/prompt-caching
- OpenAI Chat Completions reference:
  https://developers.openai.com/api/reference/resources/chat
- vLLM OpenAI-compatible server reference:
  https://docs.vllm.ai/en/v0.12.0/serving/openai_compatible_server/
- llama.cpp speculative decoding reference:
  https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md

## 0.98. 2026-05-24 refresh: service-tier hints stay proof-lane scoped

Current Chat Completions documentation exposes `service_tier` as an
OpenAI-compatible request hint with `auto`, `default`, `flex`, `priority`, and
`scale` modes; vLLM multimodal and llama.cpp speculative guidance still
support PalLLM's conservative rule that latency features should be promoted
only after route-local evidence. For a local-first Palworld companion, this
does not justify sending hosted-service fields on ordinary chat. It does give
operators a controlled proof hook for split routing: priority for the exact
player-facing lane that has measured lower queue/TTFT, flex for background
proof/docs work that can wait, and no field at all for strict local endpoints.

Implementation impact: Pass 405 adds `PalLLM:Inference:ServiceTier` plus
prompt-level `InferencePrompt.ServiceTier`. The value is validated against the
known allowlist, normalized to lowercase, and serialized as `service_tier`
only when explicitly configured or supplied by a route-specific canary. Normal
companion chat still omits the field by default, preserving endpoint
portability and the deterministic fallback path. Promotion proof now requires
accepted request shape, queue/TTFT evidence, p95 latency, cost posture where
applicable, and fallback counters before the hint is trusted.

Focused sibling scan impact: active sibling workspaces reinforced the same
generic pattern as recent passes: keep foreground and background model lanes
separately evidenced, and do not let hosted-routing hints become a default for
local gameplay. PalLLM lifted no sibling code, prompts, names, branding,
product identity, or unrelated IP.

Primary sources:

- OpenAI Chat Completions `service_tier`:
  <https://platform.openai.com/docs/api-reference/chat/create-chat-completion>
- OpenAI Priority processing:
  <https://platform.openai.com/docs/guides/priority-processing>
- OpenAI Flex processing:
  <https://platform.openai.com/docs/guides/flex-processing?api-mode=chat>
- vLLM multimodal cached inputs:
  <https://docs.vllm.ai/en/v0.15.0/features/multimodal_inputs/>
- llama.cpp speculative decoding:
  <https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md>

## 0.97. 2026-05-24 refresh: physical dashboard assets should honor both validators

Current ASP.NET Core output-cache guidance documents two cheap
revalidation paths for unchanged GET responses: `ETag`/`If-None-Match`
and `Last-Modified`/`If-Modified-Since`. The source-generation guidance
from the previous pass still applies to JSON fingerprint payloads, while
model-serving research continues to point PalLLM toward stable
content-addressed IDs and proof lanes for expensive multimodal work:
vLLM accepts stable multimodal media UUIDs for cached inputs, llama.cpp
prints speculative decoding statistics, Ollama vision requests carry
base64 images in an `images` array, and vLLM-Omni documents the current
Qwen3-Omni realtime audio caveat.

Implementation impact: Pass 404 keeps PalLLM's static dashboard fallback
dependency-light but closes the second validator path. The manually mapped
Field Console physical-file routes now return `304 Not Modified` when a
client sends a matching `If-Modified-Since` date and no `If-None-Match`
header is present. The route normalizes `Last-Modified` values to whole
HTTP-date seconds before comparing so browser revalidation does not miss
on filesystem sub-second ticks. Public route counts, OpenAPI, MCP,
feature count, fallback strategies, and test count stay unchanged.

Focused sibling scan impact: active sibling workspaces reinforced the
generic pattern of browser-cache validator parity and machine-readable
proof evidence. PalLLM lifted no sibling code, prompts, names, branding,
product identity, or unrelated IP.

Primary sources:

- ASP.NET Core output caching and revalidation:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0>
- System.Text.Json source generation:
  <https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation>
- vLLM multimodal cached inputs:
  <https://docs.vllm.ai/en/v0.15.0/features/multimodal_inputs/>
- llama.cpp speculative decoding:
  <https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md>
- Ollama vision payload shape:
  <https://docs.ollama.com/capabilities/vision>
- vLLM-Omni Qwen3-Omni realtime serving note:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>

## 0.96. 2026-05-24 refresh: conditional-cache fingerprints should stay source-generated

Current System.Text.Json guidance says source generation is the preferred path
when a runtime wants lower first-use metadata cost, smaller private memory, and
trim/AOT friendliness. The documented source-generation usage pattern is to
call serializer overloads that take the generated `JsonTypeInfo<T>` (or the
generated context) instead of falling back to options-only reflection metadata.
ASP.NET Core output-cache guidance still centers `ETag` plus
`If-None-Match` as the standard cheap-revalidation path for unchanged GET
responses.

Implementation impact: Pass 403 tightens PalLLM's conditional-cache helper.
`ConditionalHttp.CreateStrongEtag` now requires a generated `JsonTypeInfo<T>`,
and every dashboard/proof/readiness/manifest call site passes the exact
`PalLlmJsonSerializerContext.Default.*` metadata for its fingerprint payload.
The HTTP contract is unchanged: the same deliberate read-mostly endpoints still
emit strong ETags and return `304 Not Modified` on matching revalidation. The
change only removes the reflection-capable ETag serialization path from a
read-heavy sidecar helper and keeps route counts, OpenAPI, MCP, feature count,
fallback strategies, and test count unchanged.

Focused sibling scan impact: active sibling workspaces reinforced the generic
pattern of validator-first dashboard polling and cache-proof receipts that
record whether isolation is present without retaining raw cache secrets.
PalLLM lifted no sibling code, prompts, names, branding, product identity, or
unrelated IP.

Primary sources:

- System.Text.Json source generation:
  <https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation>
- System.Text.Json reflection vs source generation:
  <https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/reflection-vs-source-generation>
- ASP.NET Core output caching and revalidation:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0>

## 0.95. 2026-05-24 refresh: packaged dashboard assets need validator parity

Current ASP.NET Core static-file guidance treats endpoint-routed static assets
as first-class endpoints with build-time compression, SHA-256 fingerprinting,
`ETag`, `Last-Modified`, and content-type headers. The same guidance also
documents that physical/static-file fallback paths can be useful outside the
normal static-web-assets manifest, but they should preserve cache validators so
browsers avoid re-downloading unchanged CSS/JS. Request-timeout, rate-limiter,
and output-cache guidance still points in the same direction for the sidecar:
use endpoint-specific policies and cheap validators rather than broad
middleware that hides current runtime state.

Implementation impact: Pass 402 extracts the Field Console physical-file
routes into `PalLlmStaticAssetRoutes` and adds a tiny metadata-keyed SHA-256
fingerprint cache. The packaged-EXE fallback for `/`, `/app.js`,
`/styles.css`, `/welcome.html`, `/favicon.svg`, and
`/manifest.webmanifest` now emits weak content-hash `ETag`s,
`Last-Modified`, and `Cache-Control: public, no-cache, must-revalidate`, and
returns `304 Not Modified` when the browser sends a matching
`If-None-Match`. The cache key includes path, byte length, and UTC write ticks,
so edited assets rehash while stable packaged assets avoid repeated disk
hashing. Public route counts, OpenAPI, MCP, feature count, fallback strategies,
and test count stay unchanged.

Focused sibling scan impact: active sibling workspaces reinforced the generic
ETag/content-fingerprint pattern for low-latency dashboards and proof surfaces.
PalLLM lifted no sibling code, prompts, names, branding, product identity, or
unrelated IP.

Primary sources:

- ASP.NET Core static files and `MapStaticAssets`:
  <https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0>
- ASP.NET Core request timeouts:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0>
- ASP.NET Core rate limiting:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0>
- ASP.NET Core output caching:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0>

## 0.94. 2026-05-24 refresh: route-owned multimodal canaries should reuse stable local media IDs

Current vLLM multimodal serving docs show the OpenAI-compatible chat payload
accepting optional `uuid` fields on `image_url`, `video_url`, `input_audio`,
`audio_url`, and embedding content parts. The same docs distinguish media-cache
UUIDs from prefix/KV cache: callers still send media bytes for cold requests,
then only consider UUID-only replay after the same server process has a proven
cache hit. They also call out default HTTP fetch timeouts for remote video and
audio URLs, reinforcing PalLLM's local-byte default and remote-media proof gate.

Implementation impact: Pass 401 extends the Pass 400 vision-cache work to
route-owned multimodal inference canaries. `HttpJsonInferenceClient` now clones
prompt-level `InferencePrompt.UserContent` arrays and, when
`PalLLM:Inference:UseMediaCacheIds=true` (default), adds stable
`palllm-{image|video|audio}-sha256-*` IDs to local base64 `image_url`,
`video_url`, `audio_url`, and `input_audio` content parts that do not already
carry a UUID. Ordinary companion chat still sends the user message as a string;
strict endpoints can set `PalLLM:Inference:UseMediaCacheIds=false` and receive
the caller-owned content-part array without injected fields. The dedicated
vision client now uses the same hash helper, preserving the existing
`palllm-image-sha256-*` request shape and opt-out.

Focused sibling scan impact: active sibling workspaces reinforced the same
generic rule as recent passes: media-heavy proof lanes need content-addressed
local evidence, opt-out controls for strict endpoints, and promotion proof
separate from ordinary text chat. PalLLM lifted no sibling code, prompts,
names, branding, product identity, or unrelated IP.

Primary sources:

- vLLM multimodal inputs and OpenAI-compatible media UUIDs:
  <https://docs.vllm.ai/en/v0.17.0/features/multimodal_inputs/>
- ASP.NET Core output caching:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0>

## 0.93. 2026-05-24 refresh: repeated vision media should carry stable cache IDs

Current local-model serving guidance keeps converging on explicit, bounded
media identities rather than implicit global caches. vLLM's multimodal docs
show optional `uuid` fields on image, video, audio, and embedding content parts
and document a cached-input mode where callers can later reference the UUID
without resending media. Ollama's current vision docs continue to use base64
image payloads for REST calls, which matches PalLLM's local-byte default and
keeps remote media fetching out of the normal player path. ASP.NET Core's
request-timeout and output-cache docs also reinforce the same production rule:
heavy lanes need explicit policy, bounded behavior, and opt-out controls.

Implementation impact: Pass 400 adds stable content-hash media IDs to the
vision client. When `PalLLM:Vision:UseMediaCacheIds=true` (default),
`HttpVisionClient` tags the outgoing `image_url` content part with
`uuid: palllm-image-sha256-<digest>`. This does not skip image payloads or
change route behavior; it gives vLLM-compatible endpoints a deterministic
cache key for repeated screenshots while preserving a strict-endpoint opt-out.
Existing image byte caps, response byte caps, structured-output behavior, and
deterministic snapshot fallback are unchanged.

Focused sibling scan impact: the useful transferable idea remains generic:
media-heavy proof loops should use stable local evidence IDs and bounded
caches. PalLLM lifted no sibling code, prompts, names, branding, product
identity, or unrelated IP.

Primary sources:

- vLLM multimodal inputs and cached media:
  <https://docs.vllm.ai/en/latest/features/multimodal_inputs/>
- Ollama vision API:
  <https://docs.ollama.com/capabilities/vision>
- ASP.NET Core request timeouts:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0>
- ASP.NET Core output caching:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/caching/output?view=aspnetcore-10.0>

## 0.92. 2026-05-24 refresh: local proof caches should be metadata-keyed and bounded

Current platform and model-serving docs reinforce the same low-latency rule at
two levels. ASP.NET Core's in-memory-cache guidance says cached values must
have a fallback path, growth limits, and expirations because the runtime does
not trim application caches automatically under memory pressure. vLLM's
multimodal guidance similarly treats media reuse as explicit, bounded, and
identity-keyed: cached inputs are keyed by supplied UUIDs, and server media
loading should restrict domains or local paths before using higher-throughput
media lanes. Gemma 4's current MTP guidance and vLLM's MTP docs also make the
same promotion point for latency features: measure and prove the exact route,
model, and assistant/checkpoint pairing rather than enabling a broad family
default. Qwen3-Omni and vLLM-Omni remain useful for audio/video experiments,
but their realtime/video paths are explicit proof lanes, not a reason to make
the ordinary PalLLM proof path heavier.

Implementation impact: Pass 399 closes the local `ui_probe` diagnostics rebuild
debt. `PalLlmRuntime.UiProbe.cs` now keeps a small in-process parse cache for
diagnostic dump JSON, keyed by file path, byte length, and UTC write ticks.
Snapshot polling still enumerates the retained diagnostic directory and still
falls back to the bounded JSON reader when metadata changes, but stable widget
dumps are not reparsed on every proof/readiness rebuild. The cache is capped
independently from disk retention and is cleared when files leave the retained
set. Existing route count, MCP surface, feature catalog, fallback strategies,
and test count stay unchanged.

Focused sibling scan impact: active external sibling workspaces reinforced the
generic pattern of local proof caches, machine-readable evidence, and
route-specific model promotion. PalLLM lifted no sibling code, prompts, names,
branding, product identity, or unrelated IP.

Primary sources:

- ASP.NET Core in-memory caching:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory?view=aspnetcore-10.0>
- vLLM multimodal inputs and cached media:
  <https://docs.vllm.ai/en/v0.17.0/features/multimodal_inputs/>
- vLLM media connector controls:
  <https://docs.vllm.ai/en/latest/api/vllm/multimodal/media/>
- Google Gemma 4 MTP:
  <https://blog.google/innovation-and-ai/technology/developers-tools/multi-token-prediction-gemma-4/>
- vLLM Gemma 4 MTP:
  <https://docs.vllm.ai/en/latest/features/speculative_decoding/mtp/>
- Qwen3-Omni:
  <https://github.com/QwenLM/Qwen3-Omni>
- vLLM-Omni streaming video:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/video_stream_api/>

## 0.91. 2026-05-23 refresh: diagnosis codes should carry remediation

Current upstream model-serving and ASP.NET operations docs keep reinforcing the
same practical rule: operators need bounded evidence plus the next safe action,
not just a terminal label. vLLM's latest multimodal docs expose additional
media robustness controls such as video frame recovery via `--media-io-kwargs`,
which are useful only when the proof lane records exactly what was enabled.
llama.cpp's current multimodal docs keep audio input marked experimental and
make `libmtmd` / `--mmproj` setup explicit, which supports PalLLM's split-lane
and proof-first posture for screenshot and audio paths. llama.cpp speculative
decoding docs also expose multiple `--spec-type` modes and acceptance
statistics, again making route-local proof more important than broad feature
claims. ASP.NET Core health checks are built around real-time health endpoints
and optional key-value data, which maps cleanly to compact machine-readable
diagnosis plus remediation.

Implementation impact: Pass 364 makes the native-proof diagnosis contract
actionable. `scripts/run-native-proof.ps1`, `/api/release/readiness`, and
`scripts/pal-proof.ps1 -Json` now carry `DiagnosisAction` and
`DiagnosisCommand` next to `DiagnosisCode` / `DiagnosisSummary`. Examples:
`native_hud_bind_not_ready` points at `scripts/run-native-proof.ps1
-ApplyHudRecommendation`, delivery timeouts suggest a longer proof watcher, and
proven native HUD delivery points to the next release-smoke lane. This keeps
dashboards, support bundles, and automation from maintaining separate
diagnosis-to-command maps.

Focused sibling scan impact: RimLLM, external sibling research reinforced
the generic pattern of machine-readable release evidence with explicit next
actions. PalLLM lifted no sibling code, prompts, names, branding, product
identity, or unrelated IP.

Primary sources:

- vLLM multimodal inputs:
  <https://docs.vllm.ai/en/latest/features/multimodal_inputs/>
- llama.cpp multimodal support:
  <https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md>
- llama.cpp speculative decoding:
  <https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md>
- ASP.NET Core 10 health checks:
  <https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0>

## 0.90. 2026-05-23 refresh: native-proof blockers need stable diagnosis codes

Current serving and operations docs reinforce the same release-engineering
pattern PalLLM already uses: powerful runtime experiments need bounded,
machine-readable evidence that can distinguish "not started", "blocked",
"timed out", and "proven" without scraping console text. vLLM's newer
disaggregated-prefill docs frame P/D as a way to tune TTFT and ITL separately
and list multiple connector families including NixlConnector,
P2pNcclConnector, MooncakeConnector, MultiConnector, OffloadingConnector, and
FlexKVConnectorV1. The Mooncake Store writeup shows why shared-prefix KV cache
evidence can matter for agentic workloads, but the scope is high-scale
inference infrastructure, not a Palworld release default. ASP.NET Core health
checks similarly separate health status from implementation-specific JSON
payloads, making custom response details a normal production surface.

Implementation impact: Pass 361 keeps PalLLM's live native proof path additive.
`scripts/run-native-proof.ps1` now writes `DiagnosisCode` and
`DiagnosisSummary` into `latest-native-proof.json`, and
`/api/release/readiness` normalizes the same fields for older artifacts. Codes
such as `palworld_process_missing`, `bridge_boot_missing`,
`ui_probe_missing`, `native_hud_bind_not_ready`,
`native_hud_surface_mismatch`, `delivery_proven_timeout`, and
`native_hud_delivery_proven` let `pal proof`, dashboards, support bundles, and
automation route the next operator action without treating prose blocker text
as an API. No native-HUD default, model default, or deterministic fallback path
changed.

Focused sibling scan impact: this pass did not lift sibling code. The useful
cross-project lesson remains generic: release proof artifacts should carry
small stable failure taxonomies plus replayable transition trails, while
project identity, prompts, names, and unrelated IP stay out of PalLLM.

Primary sources:

- vLLM Mooncake Store blog:
  <https://vllm.ai/blog/2026-05-06-mooncake-store>
- vLLM disaggregated prefilling docs:
  <https://docs.vllm.ai/en/v0.21.0/features/disagg_prefill/>
- ASP.NET Core 10 health checks:
  <https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0>

## 0.89. 2026-05-22 refresh: SGLang backend choices need route-local proof

Current SGLang docs expose more hardware-specific knobs than PalLLM should ever
promote from a family name alone. The attention-backend matrix now separates
MHA, MLA, GDN, DSA, multimodal support, page-size support, FP8 KV, FP4 KV, and
speculative topk compatibility across FlashInfer, FA3/FA4, Triton, TRTLLM
MHA/MLA, AITER, Intel XPU, and other backends. Server arguments expose FP8 KV
cache dtypes plus `fp4_e2m1` for MXFP4-oriented environments with newer CUDA and
PyTorch. SGLang speculative decoding docs now describe EAGLE-3, MTP,
STANDALONE, NGRAM, adaptive speculation, and SpecV2 overlap-scheduler lanes,
with an explicit topk=1 requirement for SpecV2.

Implementation impact: Pass 331 hardens `ModelCollaborationPlanner` only.
SGLang lanes now emit support-matrix-gated proof guidance for attention backend
pinning, FP4/FP8 KV cache, EAGLE-3/adaptive speculation, and SpecV2. Promotion
receipts now require an auto-selection baseline, exact backend names, page size,
KV dtype, quantization/scaling receipt, GPU/CUDA/PyTorch class, draft-model
revision/hash or NGRAM config, topk/num-step/draft-token caps, acceptance rate,
OOM/backoff evidence, strict-route parser stability, route p95 TTFT/ITL/E2E
latency, and PalLLM fallback counters. No request shape, route count, model
default, or deterministic fallback path changed.

Focused sibling scan impact: RimLLM and an external asset-generation sibling reinforced the same generic
rule: FP4/FP8 and speculative decoding need hardware, quality, and telemetry
gates before they become defaults. PalLLM lifted no sibling code, prompts,
names, branding, product identity, or unrelated IP.

Primary sources:

- SGLang attention backend matrix:
  <https://docs.sglang.io/docs/advanced_features/attention_backend>
- SGLang server arguments:
  <https://docs.sglang.io/advanced_features/server_arguments.html>
- SGLang speculative decoding:
  <https://docs.sglang.io/advanced_features/speculative_decoding.html>
- SGLang adaptive speculative decoding:
  <https://docs.sglang.io/advanced_features/adaptive_speculative_decoding.html>

## 0.88. 2026-05-22 refresh: native-proof evidence needs replayable transitions

Current observability guidance points in the same direction across the stack:
debugging and release promotion need bounded, replayable evidence rather than a
single final status string. SGLang documents request dump/replay and crash dump
replay as first-class debugging workflows. vLLM exposes queue, running-request,
TTFT, and latency metrics from `/metrics` so operators can distinguish "not
started", "waiting", and "slow but progressing" states. ASP.NET Core 10 keeps
health checks and built-in metrics as separate monitoring surfaces for
real-time health and request/connection behavior.

Implementation impact: Pass 330 keeps the PalLLM live proof lane additive and
local. `scripts/run-native-proof.ps1` still writes the same
`Runtime/ReleaseEvidence/latest-native-proof.json` artifact, but each new run
now includes watcher start/finish time, timeout and poll settings, poll count,
timeout state, a completion reason, and up to 32 status transitions sampled
from `/api/bridge/proof`. `/api/release/readiness` now preserves those fields in
`NativeProofEvidence`, so a failed live Palworld proof run can be diagnosed from
the durable JSON alone without treating console output as source of truth.

Focused sibling scan impact: an external project's playable-proof scripts and RimLLM's
release-evidence/replay notes reinforced the generic transition-log pattern.
PalLLM lifted no sibling code, prompts, names, branding, product identity, or
unrelated IP.

Primary sources:

- SGLang observability and request/crash dump replay:
  <https://docs.sglang.io/docs/advanced_features/observability>
- vLLM production metrics:
  <https://docs.vllm.ai/en/v0.9.1/usage/metrics.html>
- ASP.NET Core 10 built-in metrics:
  <https://learn.microsoft.com/en-us/aspnet/core/log-mon/metrics/built-in?view=aspnetcore-10.0>
- ASP.NET Core 10 health checks:
  <https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/health-checks?view=aspnetcore-10.0>

## 0.87. 2026-05-22 refresh: llama.cpp draft-MTP is a text-only GGUF proof lane

Current llama.cpp speculative-decoding docs expose the modern server vocabulary
for GGUF speculation: `ngram-simple`, `ngram-mod`, `draft-simple`, and
`draft-mtp` are selected through `--spec-type`, while draft depth is expressed
through `--spec-draft-*` flags and n-gram-mod has its own
`--spec-ngram-mod-*` controls. The multimodal docs keep the media path explicit
through `libmtmd`, a matching projector, and the server's multimodal content
contract. That combination makes `draft-mtp` useful for text-only Qwen3.6 or
Gemma 4 GGUF experiments, but not a blanket proof for screenshot, video, audio,
strict JSON, tool-call, judge, or save-replay routes.

Implementation impact: Pass 329 updates the raw GGUF planner/helper path only.
`ModelCollaborationPlanner` now emits a llama.cpp `--spec-type draft-mtp`
proof hint for GGUF Qwen3.6/Gemma 4 lanes, updates n-gram examples to current
`--spec-draft-*` / `--spec-ngram-mod-*` flags, and requires command-line,
tokenizer/chat-template, model hash, server commit, accepted/generated tokens,
TTFT/ITL, parser result, and fallback receipts before promotion. The
`connect-llamacpp.ps1` helper now prints those current flag names and keeps old
`-SpecType draft` as a compatibility alias for `draft-simple`. No player-facing
default changes; text-MTP and media/libmtmd lanes remain split until proven.

Sources:

- llama.cpp speculative decoding:
  <https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md>
- llama.cpp multimodal support:
  <https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md>

## 0.86. 2026-05-22 refresh: MoRIIO/FlexKV are proof-only topology lanes

Current vLLM sources make two useful but easy-to-overstate points. The MoRIIO
connector shows prefill/decode disaggregation can run on one multi-GPU node,
with separate `kv_producer` and `kv_consumer` instances, read/write transfer
modes controlled by `VLLM_MORIIO_CONNECTOR_READ_MODE`, and proxy/handshake/
notify ports that need explicit accounting. The tradeoff is not "always
faster": it can stabilize ITL under load, while TTFT can get worse unless the
route SLO values steady decode more than first-token speed. The FlexKV connector
is likewise a KV offload path for CPU memory, SSD, or remote storage, not a
normal companion-chat cache toggle.

Implementation impact: Pass 328 hardens `ModelCollaborationPlanner` only.
Serving profiles now name `MoRIIOConnector` beside the other P/D connectors,
emit a separate single-node MoRIIO proof lane, require read/write mode, port
map, prefix-cache-disabled baseline, remote-KV wait, transfer latency, worker
rollback, and PalLLM fallback counters before promotion, and name
`FlexKVConnectorV1` as an external/offload proof lane with storage budget,
async-transfer, cache-namespace, load/store failure, and local-prefix rollback
receipts. No route count, MCP tool count, feature-catalog entry, OpenAPI schema,
request shape, or deterministic fallback behavior changed.

Focused sibling scan impact: external sibling research reinforced the generic
lesson that split prefill/decode and KV offload need route-SLO proof, topology
receipts, rollback canaries, and sanitized evidence before any promotion. Hermes
Gateway and RimLLM reinforced runtime-detected KV/cache capability guardrails.
PalLLM lifted no sibling code, prompts, names, branding, product identity, or
unrelated IP.

Primary sources:

- vLLM MoRIIO single-node P/D:
  <https://vllm.ai/blog/2026-04-07-moriio-kv-connector>
- vLLM MoRIIO connector API:
  <https://docs.vllm.ai/en/latest/api/vllm/distributed/kv_transfer/kv_connector/v1/moriio/moriio_connector/>
- vLLM FlexKV connector API:
  <https://docs.vllm.ai/en/latest/api/vllm/distributed/kv_transfer/kv_connector/v1/flexkv_connector/>
- vLLM / PegaFlow external KV cache:
  <https://vllm.ai/blog/2026-05-18-pegaflow>
- SGLang HiCache best practices:
  <https://docs.sglang.io/docs/advanced_features/hicache_best_practices>

## 0.85. 2026-05-22 refresh: schema-constrained output is not fully portable

Current local-serving docs agree on the useful direction: constrained decoding
can make strict JSON and tool-heavy routes much more reliable. They do not
agree on one portable request shape. vLLM supports OpenAI-compatible
`response_format` plus endpoint-specific `structured_outputs` shapes such as
`choice`, `regex`, `json`, `grammar`, and `structural_tag`; SGLang supports
JSON schema, regular expression, and EBNF via grammar backends such as
XGrammar; Ollama exposes native `format` schemas and OpenAI-compatible
`response_format`; llama.cpp server converts a subset of JSON Schema through
`response_format` / grammar paths; `transformers serve` exposes
OpenAI-compatible tool calling for tokenizer-supported models. The PalLLM rule
is therefore "schema proof is route/model/provider/request-shape proof", not
"OpenAI-compatible means schema-compatible."

Implementation impact: Pass 327 hardens `ModelCollaborationPlanner` only. Model
serving profiles now ask schema-bearing lanes to record schema name, canonical
schema digest, PalLLM route class, served model id, provider request shape
(`response_format`, `structured_outputs`, Ollama `format`, or grammar),
grammar/backend id, parse result, schema-validation result, token usage, p95
latency, and fallback counters. They also add a schema-echo portability canary:
required
object, enum, bounded array, deliberate violation prompt, and changed-schema
digest. App-side validation remains the authority even when the upstream server
claims constrained decoding. No route count, MCP tool count, feature-catalog
entry, OpenAPI schema, request shape, or deterministic fallback behavior changed.

Focused sibling scan impact: RimLLM reinforced the generic pattern that
schema-bearing inference should include schema digest plus route/model identity
in cache and proof keys, while an external asset-generation sibling reinforced schema-backed handoff
contracts. PalLLM lifted no sibling code, prompts, names, branding, product
identity, or unrelated IP.

Primary sources:

- vLLM structured outputs:
  <https://docs.vllm.ai/en/latest/features/structured_outputs/>
- vLLM OpenAI-compatible server:
  <https://docs.vllm.ai/en/stable/serving/openai_compatible_server/>
- SGLang structured outputs:
  <https://docs.sglang.io/docs/advanced_features/structured_outputs>
- Ollama structured outputs:
  <https://docs.ollama.com/capabilities/structured-outputs>
- llama.cpp server README:
  <https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md>
- llama.cpp grammar docs:
  <https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md>
- Hugging Face `transformers serve`:
  <https://huggingface.co/docs/transformers/main/serving>

## 0.84. 2026-05-22 refresh: DBO stays sparse-MoE proof-only

Current vLLM Dual Batch Overlap docs describe DBO as a MoE DP+EP optimization
that overlaps sparse all-to-all communication with computation by splitting
batches into microbatches. The serve CLI exposes `--enable-dbo` plus
`--dbo-decode-token-threshold` and `--dbo-prefill-token-threshold`, but the
design note also makes the scope narrow: multi-GPU data-parallel plus
expert-parallel deployments, currently with DeepEP/all2all setup requirements.
That is useful for PalLLM's high-end sparse-MoE worker proof lane, not a reason
to change the default one-player companion path before scheduler caps,
chunked-prefill, prefix-cache, and priority replay are already healthy.

Implementation impact: Pass 326 extends `ModelCollaborationPlanner` so
sparse-MoE vLLM-compatible lanes emit DBO proof-lane startup, request, cache,
admission, security, promotion, metric, and verification receipts. The planner
keeps DBO off the default player-facing lane until the same mixed short-turn
plus long-proof replay proves better p95 latency or overlap evidence without
queue-time, preemption, parser, or fallback regressions. This is guidance and
proof-contract hardening only: no route count, MCP tool count, feature-catalog
entry, OpenAPI schema, request shape, or deterministic fallback behavior
changed.

Focused sibling scan impact: Hermes Gateway, RimLLM, and an action-RPG sibling runtime reinforced
generic evidence-ledger, histogram, and "model work must not block live
gameplay" patterns. PalLLM lifted no sibling code, prompts, names, branding,
product identity, or unrelated IP.

Primary sources:

- vLLM Dual Batch Overlap:
  <https://docs.vllm.ai/en/latest/design/dbo/>
- vLLM serve CLI:
  <https://docs.vllm.ai/en/stable/cli/serve/>

## 0.83. 2026-05-22 refresh: Gemma 4 MTP needs assistant-checkpoint proof

Current vLLM speculative-decoding docs say Gemma 4 assistant checkpoints are
handled as Gemma 4 MTP speculators, not as generic draft-model speculative
decoding. That matters for PalLLM because a generic "draft model" toggle would
blur target-model lineage, assistant-checkpoint identity, token depth,
acceptance rate, parser stability, and fallback behavior into one broad
speculation claim.

Implementation impact: `ModelCollaborationPlanner` now tells Gemma 4 lanes to
wire assistant/drafter checkpoints through `method=mtp` and reject generic
`draft_model` promotion for those artifacts. The existing
`Capability.ServingProfile` proof contract now asks for the assistant
checkpoint id or hash, target-model relation, measured token depth,
acceptance-rate, prefix-cache-disabled benchmark, normal-cache replay, and
fallback behavior before any Gemma 4 MTP lane becomes player-facing. This is a
guidance/profile hardening only: no route count, MCP tool count,
feature-catalog entry, OpenAPI schema, request shape, or deterministic fallback
behavior changed.

Focused sibling scan impact: an action-RPG sibling runtime and RimLLM independently reinforced the
same generic native-MTP lesson: a drafter must have its own manifest/proof
chain instead of piggybacking on a broad speculation flag. PalLLM lifted no
sibling code, prompts, names, branding, product identity, or unrelated IP.

Primary source:

- vLLM speculative decoding:
  <https://docs.vllm.ai/en/stable/features/speculative_decoding/>

## 0.82. 2026-05-22 refresh: Responses and video jobs stay proof-only

Current Hugging Face `transformers serve` docs list `/v1/chat/completions`,
`/v1/responses`, `/v1/audio/transcriptions`, and `/v1/models`, but explicitly
call the Responses API experimental. Current vLLM OpenAI-compatible docs also
expose Responses API support, while vLLM-Omni added a separate Videos API for
async diffusion jobs through `/v1/videos` and benchmark-oriented
`/v1/videos/sync`. These are useful surfaces, but they are not drop-in
replacements for PalLLM's stateless, low-latency companion chat path.

Implementation impact: Pass 324 updates `ModelCollaborationPlanner` so
vLLM-like and `transformers serve` lanes keep `/v1/responses` proof-only until
response lifecycle events, response-id cleanup, built-in tool payload handling,
usage receipts, p95 latency, and deterministic fallback are replayed route by
route. Qwen Omni/vLLM-Omni lanes now also call out `/v1/videos` and
`/v1/videos/sync` as offline diffusion-job proof material only: job
create/poll/content/delete, cancellation, output storage cleanup, prompt
publication hygiene, and no interference with `/api/chat` or `/api/vision`
must be proven before those outputs can appear in release evidence.

Focused sibling scan impact: active `D:\Coding` projects again reinforced the
generic rule that media jobs and model-runtime experiments need route-specific
receipts and should not silently enter live player loops. PalLLM lifted no
sibling code, prompts, names, branding, product identity, or unrelated IP.

Primary sources:

- Hugging Face `transformers serve`:
  <https://huggingface.co/docs/transformers/main/serving>
- vLLM OpenAI-compatible server:
  <https://docs.vllm.ai/en/latest/serving/openai_compatible_server.html>
- vLLM-Omni Videos API:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/videos_api/>

## 0.81. 2026-05-22 refresh: deterministic memory rerank before model rerank

Current reranker guidance still supports a two-stage retrieval shape: fast
retrieval first, then a more precise reranker only over the small candidate set.
The [Hugging Face Ettin reranker release](https://huggingface.co/blog/ettin-reranker)
summarizes that cross-encoders are more accurate but costlier because they run
per `(query, document)` pair. [Jina's reranker docs](https://jina.ai/en-US/reranker/)
describe the same initial-retrieval -> reranking pattern and note latency grows
with candidate count and text length. Microsoft's Foundry card for
[`BAAI/bge-reranker-v2-m3`](https://ai.azure.com/catalog/models/baai-bge-reranker-v2-m3)
also exposes a separate `/rerank` route rather than treating ranking as the
same thing as embedding.

PalLLM's hot chat lane is player-facing, so this pass did not add a networked
rerank endpoint to every turn. Instead, `ConversationMemoryStore.Recall` now
keeps the existing deterministic embedding score and adds a stack-bounded
exact-token overlap term. That gives a cheap stage-2 signal for named Palworld
events, bases, bosses, raids, and species names while preserving the default
zero-network memory path. A future `PalLLM:Memory:RerankEndpoint` should remain
off by default and qualify against this local reranker with replay evidence.

Active `D:\Coding` sibling scans reinforced the same bounded pattern:
`RimLLM` has useful embedding-circuit-breaker notes for future external
embedding work, while `an external asset-generation sibling` keeps model-profile metadata for embedding
and rerank-style lanes. No sibling code, prompts, names, branding, or product
identity was lifted into PalLLM.

## 0.80. 2026-05-21 refresh: MTP and media lanes stay split-process until proven

Current vLLM documentation keeps model-native MTP and multimodal content parts
as distinct serving concerns: MTP is a speculative decoding profile, while
image/video/audio input has its own media limits, processor cache, and request
shape. Current llama.cpp issue traffic also shows that MTP/context-shift
instability can still produce loop or OOM failure modes on exact runtime/model
combinations. The safe PalLLM rule is therefore process-boundary first: do not
let text MTP and media-bearing routes share one server process just because a
family advertises both.

Implementation impact: Pass 321 updates `ModelCollaborationPlanner` so Qwen3.6
and Gemma 4 multimodal-capable MTP lanes now surface a split-lane startup hint,
a request guard against sending media content parts to a text-only MTP endpoint,
separate text-MTP versus multimodal cache namespaces, a no-co-scheduling
admission control, and a same-process negative canary before any shared endpoint
can be promoted. This is a guidance/profile change only: no route count, MCP
tool count, fallback path, model artifact, or network default changed.

Sibling scan impact: active projects under `D:\Coding` reinforced the same
generic split-lane pattern for Qwen/Gemma media proof, but PalLLM lifted no
code, prompts, names, branding, product identity, or third-party franchise
language.

Sources:

- https://docs.vllm.ai/en/latest/features/speculative_decoding/mtp/
- https://docs.vllm.ai/en/latest/features/multimodal_inputs/
- https://github.com/ggml-org/llama.cpp/issues/22867

## 0.79. 2026-05-21 refresh: stable prompt heads beat naive cache hope

Current ASP.NET Core guidance still points host-side admission at explicit
request timeouts and endpoint rate limits, but the local-model latency work
inside a successful request is dominated by prefill/cache behavior. The
2026 prompt-caching evaluation "Don't Break the Cache" is directly applicable
to PalLLM's chat path: place dynamic content late in the system prompt instead
of letting task/world/tool churn break the cacheable prefix. Current vLLM and
vLLM-Omni docs keep prefix caching as an explicit serving flag, including
Qwen3-Omni stage-level toggles; that reinforces PalLLM's existing proof-before-
promotion posture for Qwen/Gemma multimodal lanes.

Implementation impact: Pass 320 changes only the prompt layout. Stable PalLLM
contract text, companion identity, traits, skills, and authored pack lore now
come before the volatile `Turn context` block (`Task tag`, world state, visual
context, relationship, and memory snippets). No request-body fields, route
counts, MCP tools, fallback decisions, or public contracts changed. Sibling
scan impact: RimLLM/external sibling research all reinforced cache-proof and
publication-hygiene patterns, but PalLLM lifted no code, prompts, names,
branding, or product identity.

## 0.78. 2026-05-21 refresh: external KV cache daemons stay proof-only

Current vLLM disaggregated-prefill docs still describe P/D serving as
experimental and primarily useful for separating TTFT and ITL tuning rather
than as a throughput guarantee. Current SGLang PD-disaggregation docs describe
the same prefill/decode split and the need for router integration when the
topology is deployed at scale. The new vLLM / PegaFlow external-KV-cache post
adds a related but distinct process-boundary idea: a standalone cache daemon
owns pinned host memory, SSD cache, and optional RDMA/cache metadata while vLLM
continues to own scheduling, batching, model execution, and OpenAI-compatible
serving. That boundary may help worker restarts and multi-instance prefix
reuse, but it adds a new daemon, endpoint, cache namespace, SSD path, and
failure mode to prove.

Pass 292 therefore updates `Capability.ServingProfile` instead of adding a
runtime proxy. vLLM-like lanes now emit a proof-only external KV cache lane for
PegaFlow / `PegaKVConnector` or another `kv_connector_module_path` daemon. A
valid PalLLM receipt must compare local prefix cache with the daemon-backed
route, record daemon health, endpoint binding, pool/SSD/RDMA budget,
namespace/model identity, cache-hit rate, cold/warm TTFT/E2E, worker-restart
reuse, daemon-stop rollback, local-prefix-cache rollback, and PalLLM fallback
counters. Raw KV blocks, SSD paths, namespace strings, endpoint details, and
player text stay out of support/public bundles.

Focused sibling scan impact: Hermes Gateway reinforced the generic
GPU-resource lease pattern of failing closed and requiring manual recovery
after unsafe evictions; RimLLM reinforced lifecycle-linked restart proof and
external-validation evidence contracts. PalLLM adopted only the generic
process-boundary, rollback, and evidence-contract ideas; no sibling code,
prompts, names, branding, or product identity was lifted.

Primary sources:

- vLLM disaggregated prefilling:
  <https://docs.vllm.ai/en/latest/features/disagg_prefill/>
- vLLM / PegaFlow external KV cache:
  <https://vllm.ai/blog/2026-05-18-pegaflow>
- SGLang PD disaggregation:
  <https://docs.sglang.io/docs/advanced_features/pd_disaggregation>

## 0.76. 2026-05-20 refresh: raw PCM proof needs a callback-started mixer receipt

Current vLLM-Omni speech docs expose OpenAI-compatible `/v1/audio/speech`
with response formats including `wav`, `mp3`, `flac`, `pcm`, `aac`, and
`opus`, and note that Qwen3-TTS lanes output 24 kHz audio. The current
vLLM-Omni Qwen3-Omni serving notes keep realtime PCM WebSocket support
conditional on disabling the bundled `async_chunk` configuration. Google's
Gemma audio guide is about audio input/understanding rather than a generic
speech-output lane, and the Epic UE4.27 Audio Mixer docs put procedural audio
behind engine-side source generation and callback/DSP paths. Together, those
sources make the PalLLM rule sharper: a raw `.pcm` artifact is useful proof
material, but the player-facing claim must be a started engine-side mixer
receipt, not an inference from bytes on disk.

Pass 290 therefore adds a default-off UE4SS callback seam:
`native_audio_mixer_enabled=false` preserves the older
`raw_pcm_native_mixer_required` skip, while `native_audio_mixer_enabled=true`
asks the configured `native_audio_mixer_callback_name` to accept the raw PCM
buffer. Missing callbacks emit `native_audio_mixer_unavailable`, thrown
callbacks emit `native_audio_mixer_failed`, rejected callbacks emit
`native_audio_mixer_rejected`, and only callback acceptance emits
`Started=true` with `PlaybackMode=native_mixer`. The local path is visible only
inside the in-process Lua callback; bridge events keep content-free format,
timing, and failure-code metadata. `/api/bridge/proof` now reports these
callback states with route-specific next actions.

Focused sibling scan impact: the external prompt-pack project reinforced "no silent model/fallback
changes" and hardware-fit checks, Hermes Gateway reinforced p50/p95/p99
latency proof instead of averages, an action-RPG sibling runtime reinforced that LLM dialogue must
never block player-facing loops, and RimLLM reinforced exact route smoke proof
before promoting model-runtime features. PalLLM adopted only those generic
proof-gating ideas; no sibling code, prompts, names, branding, or product
identity was lifted.

Primary sources:

- vLLM-Omni Speech API:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>
- vLLM-Omni Qwen3-Omni online serving:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- Google Gemma audio understanding:
  <https://ai.google.dev/gemma/docs/capabilities/audio>
- Epic Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.77. 2026-05-20 refresh: Qwen Omni streaming video stays proof-gated

Current vLLM-Omni docs expose a separate streaming-video WebSocket endpoint,
`/v1/video/chat/stream`. The protocol buffers base64 JPEG/PNG frames, can
accept optional base64 PCM16 16 kHz mono audio chunks, answers `video.query`
messages over the buffered stream, and ends with `video.done`. The same
vLLM-Omni Qwen3-Omni serving docs still keep `/v1/realtime` audio proof
separate from ordinary chat and warn operators to serve without `async_chunk`
for that realtime path, while the speech API remains its own
`/v1/audio/speech` route.

Pass 291 therefore keeps Qwen Omni streaming video as operator proof guidance
inside `Capability.ServingProfile`, not as a default PalLLM runtime route. A
valid receipt must name the `/v1/video/chat/stream` route, frame cadence,
duration cap, optional PCM16 audio chunk policy, reconnect/stall behavior,
still-image or world-state fallback, and proof that ordinary `/api/chat` text
turns still return while the stream is unhealthy. This gives future Palworld
proof clips a modern low-latency lane without letting stateful video streams
queue ahead of deterministic companion chat.

Focused sibling scan impact: external sibling research, and Hermes Gateway reinforced
the same generic pattern - proof lanes need explicit admission limits,
fallback evidence, and p50/p95/p99 receipts. No sibling code, prompts, names,
branding, or product identity were lifted into PalLLM.

Sources:

- vLLM-Omni streaming video input API:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/video_stream_api/>
- vLLM-Omni Qwen3-Omni serving example:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni Speech API:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>

## 0.75. 2026-05-17 refresh: raw PCM needs a separate native mixer proof lane

Current Qwen3-Omni serving docs expose output modality control for text,
audio, or text+audio, and the realtime examples in the same ecosystem still
normalize streaming speech as bounded PCM chunks before handing it to the
model server. Google's Gemma 4 launch notes keep native audio input model-
specific, with E2B/E4B called out separately from ordinary text/vision use.
Epic's Audio Mixer docs describe procedural sources as the engine-side path
that can feed audio from custom client code or independent engines into the
renderer. Together, those sources reinforce PalLLM's rule: raw audio bytes are
useful proof material, but they are not player-facing audio until the bridge
proves the exact engine mixer seam that consumed them.

Pass 289 therefore keeps the existing content-free `speech_playback` receipt
but adds a separate `/api/bridge/proof` lane named `native_audio_mixer`. The
lane is required when the latest speech receipt is raw PCM, fails until a
started native mixer receipt exists, and carries the same audio-format receipt
plus a direct operator next action. Containerized helper formats still pass
the lane as "not required." This keeps raw PCM promotion explicit without
adding audio bytes, file paths, generated text, upstream logs, or sibling-
project material to any payload.

Primary sources:

- vLLM-Omni Qwen3-Omni online serving:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/user_guide/examples/online_serving/qwen3_omni/>
- vLLM realtime speech-to-text examples:
  <https://docs.vllm.ai/en/latest/examples/speech_to_text/realtime/>
- Google Gemma 4 launch notes:
  <https://blog.google/innovation-and-ai/technology/developers-tools/gemma-4/>
- Epic Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.74. 2026-05-17 refresh: speech proof needs end-to-end receipt latency

Current vLLM realtime protocol docs model audio as base64 PCM16 at 16 kHz
buffer append/commit events with incremental transcript deltas. vLLM-Omni's
current Qwen3-Omni serving notes also keep realtime WebSocket use conditional
on `async_chunk` being disabled, while the Qwen3-Omni repository says vLLM is
the preferred low-latency local runtime for large-scale use but that normal
`vllm serve` currently covers the thinker path. Google's Gemma 4 audio guide
keeps input bounded to 30 seconds, mono, 16 kHz float32 frames, and 25 audio
tokens per second. llama.cpp's multimodal docs still describe audio input as
highly experimental. The shared lesson is that PalLLM should expose latency and
fallback receipts before promoting native voice, not infer readiness from a
model family name.

Pass 287 added outbox-to-speech and visible-delivery-to-speech lag receipts.
The remaining proof gap was total request-to-speech latency for the same bridge
turn. PalLLM therefore now derives `SpeechPlaybackIngressLagMs` when the last
tracked ingress request id matches the outbox and `speech_playback` request id.
The sidecar computes it from existing timestamps, clamps it to one day, and
summarizes it in `/api/bridge/proof` beside the existing outbox and delivery
lag fields. No Lua payload field, audio bytes, generated text, local paths,
raw upstream logs, prompts, names, branding, or product identity are added.

Focused sibling scan impact: external sibling research, and the prompt-pack
experiments reinforced no-silent-fallback gates, schema-backed proof receipts,
release smoke artifacts, and slot/latency measurements. PalLLM adopted only
the generic "derive route-owned latency proof from already-tracked events"
pattern; no sibling code, prompts, names, branding, or product identity was
lifted.

Primary sources:

- vLLM realtime protocol:
  <https://docs.vllm.ai/en/v0.18.2/api/vllm/entrypoints/openai/realtime/protocol/>
- vLLM-Omni Qwen3-Omni online serving:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- Qwen3-Omni model repository:
  <https://github.com/QwenLM/Qwen3-Omni>
- Google Gemma audio understanding:
  <https://ai.google.dev/gemma/docs/capabilities/audio>
- llama.cpp multimodal docs:
  <https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md>

## 0.73. 2026-05-17 refresh: speech loop proof needs timestamp-derived lag

Current vLLM realtime transcription docs make the audio loop explicitly
incremental: a client appends 16 kHz mono PCM16 chunks, commits buffers, and
receives transcription deltas before a final done event. Google's current
Gemma audio guidance also names exact preprocessing constraints for native
audio input: mono, 16 kHz, 32 ms frames, and bounded clip length. Qwen Cloud's
current model table keeps Qwen3.6 vision/video/text lanes separate from Omni
lanes that accept audio and can emit audio. The shared implementation lesson
for PalLLM is not "turn on every advertised audio path"; it is to make each
player-facing audio path prove timing, request shape, payload bounds, fallback
behavior, and route ownership before promotion.

Pass 286 already made stale-speech overlap visible by carrying bounded
prior-buffer receipts. The next proof gap was end-to-end bridge timing:
operators could see that a matching `speech_playback` event arrived, but not
how long after the sidecar wrote the outbox reply or after UE4SS reported
visible delivery. PalLLM therefore derives `SpeechPlaybackOutboxLagMs` and
`SpeechPlaybackDeliveryLagMs` in `BridgeLoopProofSnapshot` from existing
outbox, delivery, and speech receipt timestamps. Pass 288 later adds
`SpeechPlaybackIngressLagMs` for total request-to-speech receipt latency. The
fields are computed by the sidecar, clamped to one day, and never require Lua
to send audio bytes, generated text, local paths, raw upstream logs, prompts,
names, branding, or product identity.

Focused sibling scan impact: external sibling research reinforced schema-backed
latency receipts for media/runtime promotion, while RimLLM reinforced exact
route proof before advertising native-runtime speedups. PalLLM adopted only
the generic "derive compact timing proof at the owning lane" idea; no sibling
code, prompts, names, branding, or product identity was lifted.

Primary sources:

- vLLM OpenAI-compatible realtime audio protocol:
  <https://docs.vllm.ai/en/latest/serving/openai_compatible_server/>
- Google Gemma audio understanding:
  <https://ai.google.dev/gemma/docs/capabilities/audio>
- Qwen Cloud visual/omni model table:
  <https://docs.qwencloud.com/developer-guides/getting-started/vision-models>
- Qwen3-Omni model repository:
  <https://github.com/QwenLM/Qwen3-Omni>
- ASP.NET Core request-timeout middleware:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0>

## 0.72. 2026-05-17 refresh: stale speech proof needs remaining-buffer receipts

Current vLLM OpenAI-compatible docs expose one-shot transcription, translation,
and WebSocket realtime transcription as separate audio paths, while Qwen Cloud
now distinguishes text/image/video Qwen3.6 lanes from Qwen Omni lanes that can
take streaming audio and emit audio. Google's Gemma audio guidance keeps
player-audio input bounded and normalized: mono audio, 16 kHz processing, and
short clip caps. Those model surfaces point in the same direction for PalLLM:
voice and audio proof must stay route-specific, bounded, and content-free until
a concrete lane is proven.

On the game side, Unreal's Audio Mixer guidance still makes render-buffer
latency the native constraint. Pass 285 proved that the Lua bridge can detect
when a newer speech request supersedes an older one, but the receipt only
carried the age of the prior speech. That was not enough to prove whether the
older clip probably still had buffered audio. PalLLM therefore extends
`speech_playback` with `SupersededSpeechBufferedMs` and
`SupersededSpeechRemainingMs`. Lua keeps only the prior request id, start
clock, and bounded buffered-duration estimate; when a new request supersedes
that prior request, it emits the prior buffer and an estimated remaining
duration. The sidecar clamps the prior buffer, recomputes estimated remaining
duration from bounded prior age + buffer values, and preserves those fields in
`SpeechPlaybackSnapshot`, `/api/bridge/proof`, OpenAPI, and the bridge-event
schema.

This remains a proof receipt, not a hard cancellation claim. The desktop helper
cannot be force-stopped reliably, and raw PCM still waits for native mixer
binding. The new fields let operators identify overlap risk before promoting a
native in-world audio seam, without storing audio bytes, generated text, local
paths, raw upstream logs, sibling code, prompts, names, branding, or product
identity.

Focused sibling scan impact: external sibling research reinforced only
generic ideas: distinguish live proof from synthetic proof, make cancellation
state observable, and keep latency/posture summaries machine-readable. PalLLM
adopted only the content-free remaining-buffer receipt shape; no sibling code,
prompts, names, branding, or product identity was lifted.

Primary sources:

- vLLM OpenAI-compatible server audio APIs:
  <https://docs.vllm.ai/en/latest/serving/openai_compatible_server/>
- Google Gemma audio understanding:
  <https://ai.google.dev/gemma/docs/capabilities/audio>
- Hugging Face Transformers Gemma4 model docs:
  <https://huggingface.co/docs/transformers/model_doc/gemma4>
- Qwen Cloud visual and omni model table:
  <https://docs.qwencloud.com/developer-guides/getting-started/vision-models>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.71. 2026-05-17 refresh: native audio proof needs buffer-duration receipts

Current vLLM-Omni Qwen3-Omni examples expose text+audio chat completions,
speaker selection, and streaming text/audio output, while the standalone
vLLM-Omni speech API lists Qwen3-TTS variants behind an OpenAI-compatible
`/v1/audio/speech` shape. Current vLLM OpenAI-compatible serving docs also
call out WebSocket realtime transcription as a separate audio path. Those
surfaces make speech artifacts more likely to arrive through multiple local
lanes: ordinary binary speech responses, multimodal chat audio, realtime PCM,
and ASR/transcription loops.

Epic's Audio Mixer guidance keeps the native target honest: real-time audio is
bounded by render-buffer timing, starvation, and game-thread/audio-thread
communication latency. Queue depth alone says how many chunks exist, but an
operator still has to multiply it by the quantum to judge whether a generated
clip is interactive, stale, or just a long-form proof sample. PalLLM therefore
extends the existing `speech_playback` receipt with `MixerBufferedMs` and
`MixerTailMs`. Lua derives those values with the same 10 ms proof quantum as
`MixerQueueDepthEstimate`, and the sidecar recomputes them from bounded sample
rate and frame count before surfacing them in `SpeechPlaybackSnapshot`,
`/api/bridge/proof`, OpenAPI, and the bridge-event schema.

This is still proof-only. It does not claim actual Unreal callback size, live
procedural source binding, or raw PCM playback. It gives the future in-world
audio seam a direct buffer-duration receipt for queue budgeting,
barge-in/cancellation design, and low-latency promotion decisions without
persisting audio bytes, generated text, local paths, raw upstream logs, sibling
code, prompts, names, branding, or product identity.

Focused sibling scan impact: an action-RPG sibling runtime and RimLLM reinforced only generic
voice queue, cancellation, and stale-speech rejection patterns. PalLLM adopted
only the content-free buffer-duration receipt shape; no sibling code, prompts,
names, branding, or product identity was lifted.

Primary sources:

- vLLM-Omni Qwen3-Omni online serving:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni speech API:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>
- vLLM OpenAI-compatible server audio APIs:
  <https://docs.vllm.ai/en/latest/serving/openai_compatible_server/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>
- Unreal Engine Quartz overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/quartz-overview?application_version=4.27>

## 0.70. 2026-05-17 refresh: native audio proof needs queue-depth receipts

Current vLLM-Omni Qwen3-Omni online serving docs keep audio generation tied to
deployment shape: async chunking is enabled by the bundled configuration, stage
overrides can move memory and batching budgets between thinker/talker/code2wav
stages, and realtime PCM sessions require a configuration where async chunking
does not block the OpenAI-style realtime path. The vLLM-Omni speech API also
exposes speech output formats and batch speech calls, so PalLLM should be able
to prove whether a speech artifact is small enough to hand to a low-latency
native mixer queue before promoting it beyond the desktop helper lane.

Epic's `ISampleRateConverter` API exposes chunk and full-buffer conversion
methods for int16 and float inputs. That reinforces the implementation shape:
before PalLLM binds a real in-world audio primitive, its content-free
`speech_playback` receipt should say how many small mixer chunks a WAV or raw
PCM artifact would occupy. PalLLM therefore derives `MixerQuantumMs`,
`MixerQuantumFrames`, `MixerQueueDepthEstimate`, and `MixerTailFrames` from the
already-bounded sample rate and sample-frame count. The current proof quantum
is 10 ms. For example, a 24 kHz one-second mono raw PCM artifact reports
240-frame mixer quanta, an estimated queue depth of 100, and zero tail frames.
The sidecar recomputes these values from sample rate and frame count before
surfacing them, so forged bridge fields cannot inflate proof.

This is still proof-only. It does not claim raw PCM playback, actual Unreal
callback size, or live mixer binding. It gives the future native seam a compact
queue-depth and cancellation-readiness receipt without persisting audio bytes,
generated text, local paths, raw upstream logs, sibling code, prompts, names,
branding, or product identity.

Focused sibling scan impact: active sibling projects reinforced only the
generic pattern of media lanes exposing queue/cancellation receipts before
promotion. PalLLM adopted only the content-free queue-estimate receipt; no
sibling code, prompts, names, branding, or product identity was lifted.

Primary sources:

- vLLM-Omni Qwen3-Omni online serving:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni speech API:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>
- Epic `ISampleRateConverter` API:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/SignalProcessing/ISampleRateConverter>
- ASP.NET Core request-timeout middleware:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0>

## 0.69. 2026-05-17 refresh: native audio proof needs mixer-conversion receipts

Current vLLM-Omni speech docs expose `/v1/audio/speech` with multiple output
formats, including `wav`, compressed containers, and raw `pcm`; Qwen3-Omni
realtime serving still distinguishes normal chunked deployments from
OpenAI-style realtime PCM sessions. Epic's Audio Mixer docs describe a native
pipeline that decodes source audio into 32-bit float buffers, performs
real-time sample-rate conversion, and lets procedural sources feed client audio
into the mixer. The matching DSP API docs show int16 and float inputs being
converted into float output buffers.

PalLLM therefore extends the content-free `speech_playback` receipt with
`MixerConversionHint`. Lua derives the hint from already-sanitized sample
metadata: big-endian multi-byte inputs report `byte_swap`, integer PCM reports
`integer_to_float32`, non-32-bit float reports `float_width_to_float32`,
companded formats report `decode_to_float32`, and wide channel layouts report
`channel_layout_map`. For example, `audio/L16` defaults to
`byte_swap_integer_to_float32`, while little-endian signed PCM typically reports
`integer_to_float32`. This does not promote raw PCM to playback; it gives the
future native mixer an auditable conversion checklist before any in-world audio
surface can be treated as player-ready.

Focused sibling scan impact: active sibling projects reinforced only the
generic pattern of native-media lanes carrying conversion and cancellation
proof before promotion. PalLLM adopted only the generic conversion-hint receipt;
no sibling code, prompts, names, branding, or product identity was lifted.

Primary sources:

- vLLM-Omni Speech API docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>
- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>
- Unreal Engine `ISampleRateConverter` API:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/API/Runtime/SignalProcessing/ISampleRateConverter>

## 0.68. 2026-05-17 refresh: native audio proof needs sample interpretation receipts

RFC 2586 defines `audio/L16` as uncompressed 16-bit signed two's-complement
audio in network byte order, while Microsoft `WAVEFORMATEX` / RIFF metadata is
the Windows-local shape PalLLM already parses for WAV helper playback. Current
Unreal Audio Mixer guidance describes procedural sources as native engine
inputs that are fed by client code into real-time 32-bit float buffers, so a
future in-world mixer binding needs exact sample interpretation before it can
byte-swap, widen, or normalize raw speech safely. Current vLLM-Omni docs also
now expose both `/v1/audio/speech` and Qwen3-Omni multimodal text/audio
outputs, which makes raw PCM and containerized speech canaries more likely to
arrive from different local servers with different byte-order assumptions.

PalLLM therefore extends the content-free `speech_playback` receipt with
`SampleFormat` and `ByteOrder`. WAV PCM / WAVE_FORMAT_EXTENSIBLE PCM receipts
report signed integer samples for sample widths above 8 bits and unsigned
integer for 8-bit PCM, with little-endian byte order for multi-byte PCM or
IEEE-float sample words. `audio/L16` raw PCM reports signed integer samples
and big-endian byte order by default, and raw `audio/pcm` can carry explicit
format / byte-order MIME parameters such as `format=s16le`. The sidecar keeps
the fields as sanitized short codes (`signed_integer`, `float`,
`little_endian`, `big_endian`, etc.) and the proof remains content-free:
no audio bytes, generated text, local paths, raw upstream logs, sibling code,
prompts, names, branding, or product identity are stored.

Focused sibling scan impact: external sibling research reinforced only generic
audio-output transport probes, sidecar proof manifests, latency/voice-budget
receipts, and model-native speech gating. PalLLM adopted only the generic idea
that native audio proof needs explicit sample interpretation before promotion;
no sibling code, prompts, names, branding, or product identity was lifted.

Primary sources:

- RFC 2586 `audio/L16` MIME type:
  <https://www.rfc-editor.org/rfc/rfc2586.html>
- Microsoft `WAVEFORMATEX` docs:
  <https://learn.microsoft.com/en-us/windows/win32/api/mmeapi/ns-mmeapi-waveformatex>
- Microsoft `WAVEFORMATEXTENSIBLE` docs:
  <https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksmedia/ns-ksmedia-waveformatextensible>
- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni Speech API docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.67. 2026-05-17 refresh: native audio proof needs sample-frame receipts

Microsoft `WAVEFORMATEX` documents `nBlockAlign` as the minimum atomic data
unit for a waveform format and ties PCM byte rate to sample rate multiplied by
block alignment. RFC 2586 defines `audio/L16` as interleaved 16-bit signed
samples with a required `rate` parameter and optional channel count. Current
vLLM-Omni Qwen3-Omni realtime examples still stream PCM16 chunks and save
synthesized audio separately from text events, while the speech API exposes
TTS model variants whose response format has to be proven per runtime. Epic's
Audio Mixer guidance also frames game audio as a real-time system where
buffering and underruns matter.

Together, those sources make sample-frame count and partial-frame remainder
the next useful receipt after byte rate / block alignment. PalLLM now derives
`FrameCount=floor(AudioDataBytes / BlockAlignBytes)` and
`BlockRemainderBytes=AudioDataBytes % BlockAlignBytes` on the UE4SS
`speech_playback` receipt, and the sidecar recomputes the same values from the
bounded byte/alignment fields before surfacing them in `SpeechPlaybackSnapshot`
and `/api/bridge/proof`. The values are content-free and path-free, but they
give a future native mixer concrete buffer lifetime and cancellation evidence:
how many complete sample frames exist and whether a partial trailing frame must
block promotion.

Focused sibling scan impact: an external asset-generation sibling reinforced the generic pattern of
source-frame-count receipts for media timeline proof, and an action-RPG sibling runtime reinforced
time-to-first-audio plus real-time-factor proof before claiming generated
speech lanes. PalLLM adopted only the generic frame-count receipt idea; no
sibling code, prompts, names, branding, or product identity was lifted.

Primary sources:

- Microsoft `WAVEFORMATEX` docs:
  <https://learn.microsoft.com/en-us/previous-versions/ms713497(v=vs.85)>
- RFC 2586 `audio/L16` MIME type:
  <https://www.rfc-editor.org/rfc/rfc2586.html>
- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni Speech API docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.66. 2026-05-17 refresh: raw PCM MIME parameters need mixer-readiness receipts

RFC 2586 defines `audio/L16` as uncompressed 16-bit audio with a required
`rate` parameter and optional `channels` parameter, while raw audio generally
has no self-describing header. Current vLLM-Omni Qwen3-Omni docs also keep the
audio-output path deployment-sensitive: async chunking improves
time-to-first-audio and throughput, but `/v1/realtime` needs an
`async_chunk: false` deployment for OpenAI-style realtime PCM sessions. Epic's
Audio Mixer guidance reinforces that real-time game audio should avoid both
underruns and excess buffering, and that procedural sources can feed audio
from external systems into the mixer. Together these point to the same
operator contract for PalLLM: raw PCM can be useful for low-latency native
audio, but only if the bridge preserves enough sample-frame metadata before a
native mixer binding is promoted.

PalLLM therefore routes parameterized raw-audio MIME values by media-type base
in both C# and Lua, so `audio/L16; rate=24000; channels=1` still writes `.pcm`
and reaches the `raw_pcm` proof lane. The UE4SS bridge now derives
content-free raw timing metadata from MIME parameters: sample rate, channel
count, bit depth, byte rate, block alignment, audio-data bytes, duration, valid
bits, and `AudioEncoding` (`l16_pcm` or `raw_pcm`). Raw PCM remains proof-only
with `FailureCode=raw_pcm_native_mixer_required` until native mixer binding is
live-proven, but incomplete sample frames now report
`FailureCode=raw_pcm_block_alignment_invalid` before proof can claim a
mixer-ready artifact.

Focused sibling scan impact: external sibling research reinforced only the
generic pattern of no-raw-audio receipts, explicit audio-output transport
proof, and time-to-first-audio evidence. PalLLM adopted no sibling code,
prompts, names, branding, or product identity.

## 0.65. 2026-05-17 refresh: WAV extensible proof needs precision and speaker layout

Microsoft `WAVEFORMATEXTENSIBLE` documents the exact fields a native mixer will
need once PalLLM moves from a Windows helper to in-world audio: the basic
`WAVEFORMATEX` layout is followed by `Samples.wValidBitsPerSample`,
`dwChannelMask`, and a `SubFormat` GUID. The valid-bits field distinguishes
sample precision from container size, and the channel mask maps interleaved
channels to speaker positions. Current vLLM-Omni Qwen3-Omni docs still support
text+audio outputs through OpenAI-compatible chat completions, while realtime
WebSocket audio remains deployment-sensitive when `async_chunk` is enabled.
The async-chunk design remains valuable for latency, but it reinforces the need
to keep proof receipts route/modality specific instead of assuming that one
audio-output lane proves the final native mixer.

PalLLM therefore extended the content-free `speech_playback` receipt with
`ValidBitsPerSample` and `ChannelMask` when the UE4SS bridge sees a
WAVE_FORMAT_EXTENSIBLE `fmt ` chunk. The bridge also rejects WAV artifacts
whose `data` chunk is not a multiple of `BlockAlignBytes` with
`FailureCode=wave_block_alignment_invalid`, so proof cannot claim helper
playback for a partial PCM frame. The sidecar clamps and preserves those fields
in `SpeechPlaybackSnapshot`, `/api/bridge/proof`, OpenAPI, and the
bridge-event schema without storing audio bytes, generated text, local paths,
raw upstream logs, sibling code, prompts, names, branding, or product identity.

Focused sibling scan impact: RimLLM reinforced the generic need to reject
partial PCM frames before engine playback, and an external asset-generation sibling reinforced redacted
audio-output receipts with compact metadata. PalLLM adopted only those generic
receipt/validation patterns; no sibling code, prompts, names, branding, or
product identity was lifted.

Primary sources:

- Microsoft `WAVEFORMATEXTENSIBLE` docs:
  <https://learn.microsoft.com/en-us/windows/win32/api/mmreg/ns-mmreg-waveformatextensible>
- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni async-chunk design docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/design/feature/async_chunk/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.64. 2026-05-17 refresh: speech proof needs WAV mixer-layout receipts

Current vLLM-Omni Qwen3-Omni serving docs still let operators request audio
output through the OpenAI-compatible chat shape, while the realtime lane stays
deployment-sensitive because `/v1/realtime` is not the same proof path as
async-chunk audio generation. Microsoft `WAVEFORMATEX` docs make the native
mixer contract concrete: `nAvgBytesPerSec` is the average transfer rate, and
`nBlockAlign` is the atomic block size that PCM / WAVE_FORMAT_EXTENSIBLE
playback must respect. Unreal's audio mixer remains a decode/mix/render
pipeline, so PalLLM's future in-world audio seam needs byte-rate and
block-boundary evidence before treating a generated artifact as native-ready.

PalLLM therefore extended the content-free `speech_playback` receipt with
`ByteRate`, `BlockAlignBytes`, and `AudioDataBytes` inferred from the WAV RIFF
`fmt ` and `data` chunks. The sidecar clamps and preserves those fields in
`SpeechPlaybackSnapshot`, `/api/bridge/proof`, OpenAPI, and the bridge-event
schema. This makes proof bundles able to distinguish "a valid helper-playable
WAV exists" from "the future native mixer knows the expected byte cadence and
block alignment" without storing audio bytes, generated text, local paths, raw
upstream logs, sibling code, prompts, names, branding, or product identity.

Focused sibling scan impact: an external asset-generation sibling reinforced stable media receipts,
an action-RPG sibling runtime reinforced explicit audio-output boundary proof, RimLLM reinforced
model/audio capability truth, and the prompt-pack scan reinforced byte-stable
contract fragments for cache reuse. PalLLM adopted only the generic
content-free proof-receipt idea and did not lift sibling code, prompts, names,
branding, or product identity.

Primary sources:

- Microsoft `WAVEFORMATEX` docs:
  <https://learn.microsoft.com/en-us/previous-versions/ms713497%28v%3Dvs.85%29>
- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni async-chunk design docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/design/feature/async_chunk/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.63. 2026-05-17 refresh: speech proof needs WAV encoding before native mixing

Current vLLM-Omni guidance keeps the split clear: ordinary speech endpoints can
return containerized audio or raw PCM, while Qwen3-Omni realtime WebSocket proof
depends on a deployment where `async_chunk` is disabled. The async-chunk design
docs are still valuable for low-latency TTFP work, but they are not a green
light for the `/v1/realtime` proof lane. Unreal's audio mixer docs describe a
decode/mix/render pipeline, so a future native PalLLM mixer must know not only
sample rate and channel count, but also whether a WAV is PCM,
WAVE_FORMAT_EXTENSIBLE PCM, float, or another encoding.

PalLLM therefore extended the existing content-free `speech_playback` receipt
with `AudioEncoding`. The Lua bridge now detects PCM, IEEE float, A-law, mu-law,
WAVE_FORMAT_EXTENSIBLE PCM, WAVE_FORMAT_EXTENSIBLE float, and unknown WAV format
tags from the RIFF `fmt ` chunk. The local helper path only treats PCM and
WAVE_FORMAT_EXTENSIBLE PCM as helper-supported; other recognized WAV encodings
emit `FailureCode=wave_encoding_unsupported` before playback can be reported as
started. The sidecar preserves the bounded encoding code in
`SpeechPlaybackSnapshot`, `/api/bridge/proof`, OpenAPI, and the bridge-event
schema without storing audio bytes, generated text, local paths, raw upstream
logs, sibling code, prompts, names, branding, or product identity.

Focused sibling scan impact: active sibling projects reinforced the generic
pattern of schema-backed media receipts and early format rejection, but no code,
prompts, names, branding, or product identity was lifted into PalLLM.

Primary sources:

- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni async-chunk design docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/design/feature/async_chunk/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>

## 0.62. 2026-05-17 refresh: speech proof needs WAV format receipts before native mixing

Current vLLM-Omni speech serving exposes `response_format` values for WAV,
compressed containers, and raw PCM, and its streaming speech docs surface
per-sentence PCM metadata such as `sample_rate`. The Qwen3-Omni online-serving
docs still make `/v1/realtime` conditional on disabling `async_chunk`, so a
PalLLM realtime voice path must keep transport/topology proof separate from
ordinary helper playback. Unreal's audio mixer ultimately renders decoded
sources into the platform audio endpoint, which means a future native in-world
PCM seam needs concrete sample-rate, channel-count, bit-depth, duration,
lifetime, and cancellation evidence before promotion.

PalLLM therefore now records content-free WAV format metadata on
`speech_playback` receipts when the UE4SS bridge can infer it cheaply from the
RIFF header: `SampleRateHz`, `ChannelCount`, `BitsPerSample`, and `DurationMs`.
The runtime clamps and preserves those values in `SpeechPlaybackSnapshot`,
`RuntimeHealth`, `/api/bridge/proof`, OpenAPI, and the bridge-event schema. Raw
PCM remains proof-only with `FailureCode=raw_pcm_native_mixer_required` because
bare bytes are not self-describing; a native mixer still has to prove its own
format contract before raw PCM can become player-facing.

Focused sibling scan impact: an external asset-generation sibling reinforced schema-backed media receipts
with no raw media in support artifacts, RimLLM reinforced bounded PCM WAVE
header validation before playback, and an action-RPG sibling runtime reinforced sample-rate/channel
metadata as a native-audio proof concern. PalLLM adopted only the generic
content-free WAV metadata receipt pattern and did not lift sibling code,
prompts, names, branding, or product identity.

Primary sources:

- vLLM-Omni Text to Speech API docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/>
- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- Unreal Engine Audio Mixer overview:
  <https://dev.epicgames.com/documentation/en-us/unreal-engine/audio-mixer-overview?application_version=4.27>
- ASP.NET Core request-timeout middleware docs:
  <https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0>

## 0.61. 2026-05-16 refresh: speech playback receipts need stable failure codes

Current local voice guidance still points in the same direction: vLLM-Omni's
Qwen3-Omni docs make OpenAI-style realtime PCM sessions conditional on
disabling async chunk mode, the async-chunk docs describe `async_chunk` as a
pipeline/stage configuration choice, vLLM's realtime example streams PCM16 in
small chunks, and current audio APIs still treat raw `pcm` as a speech output
format. Those are useful proof targets, but they do not make a desktop helper
able to infer sample rate, channels, cancellation, and game-side lifetime from
bare bytes.

PalLLM therefore now records a machine-readable `FailureCode` on every
skipped `speech_playback` receipt. The human `Reason` remains useful for logs,
but proof tooling no longer has to parse prose to distinguish
`speech_file_empty`, `wave_header_invalid`, `unsupported_format`,
`duplicate_within_dedupe_window`, `launch_failed`, and
`raw_pcm_native_mixer_required`. `/api/bridge/proof` uses that stable taxonomy
for the speech lane's next action while still keeping receipts content-free:
request id, mode/hint, MIME/extension, artifact byte count, attempt count,
elapsed milliseconds, result state, short reason, and code only.

Focused sibling scan impact: an external asset-generation sibling reinforced schema-backed media receipts
with stable IDs/codes, RimLLM reinforced bounded audio validation before
playback, and an action-RPG sibling runtime reinforced cancellation/lifetime as native-audio proof
concerns. PalLLM adopted only the generic stable-code receipt pattern and did
not lift sibling code, prompts, names, branding, or product identity.

Primary sources:

- vLLM-Omni Qwen3-Omni online-serving docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/>
- vLLM-Omni async-chunk design docs:
  <https://docs.vllm.ai/projects/vllm-omni/en/latest/design/feature/async_chunk/>
- vLLM realtime speech-to-text example docs:
  <https://docs.vllm.ai/en/latest/examples/speech_to_text/realtime/>
- OpenAI Audio API reference:
  <https://developers.openai.com/api/reference/resources/audio/subresources/transcriptions/methods/create>

## 0.60. 2026-05-16 refresh: raw PCM speech is proof-only until native mixer bind

Current vLLM-Omni speech docs expose OpenAI-compatible
`/v1/audio/speech` with `response_format=pcm`, and current Qwen3-Omni
realtime examples use PCM16 mono input and PCM audio deltas when the server is
configured for realtime. Those paths are useful canaries for low-latency voice,
but raw PCM is not a self-describing desktop audio container. PalLLM's current
UE4SS helper surface can safely launch WAV through `SoundPlayer` and
compressed containers through a media-player helper, but it does not yet bind a
native in-world PCM mixer with sample-rate, channel-count, lifetime, and
cancellation proof.

PalLLM therefore keeps raw PCM as a proof-only speech artifact. The runtime can
still write `.pcm` with `PlaybackHint=raw_pcm` for endpoint canaries, but the
Lua bridge now recognizes `.pcm`, `audio/pcm`, and `audio/l16` as `raw_pcm`,
emits a content-free `speech_playback` receipt with artifact byte count, mode,
hint, MIME/extension, zero launch attempts, elapsed milliseconds, and the
stable reason `speech raw pcm requires native mixer binding`, and does not
launch a desktop helper. That makes `/api/bridge/proof` explain the exact
remaining blocker without persisting audio bytes, local paths, upstream logs,
or sibling-project identity.

Focused sibling scan impact: external sibling research reinforced only the
generic pattern that native audio lanes need explicit sample-rate/admission
policy and proof receipts before promotion; the external prompt-pack project reinforced local-first
bounded voice artifacts. PalLLM adopted only the proof boundary and did not
lift sibling code, prompts, names, branding, or product identity.

Primary sources:

- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
- https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0
- https://docs.ue4ss.com/dev/upgrade-guide.html

## 0.59. 2026-05-16 refresh: SGLang HiCache needs route proof

Current SGLang docs describe HiCache as hierarchical KV caching that extends
RadixAttention from GPU memory into host memory and optional distributed
storage. The best-practices page exposes startup flags for page size,
hierarchical-cache enablement, host cache ratio or size, I/O backend, write
policy, storage backend, prefetch policy, and runtime attach/detach of storage
backends. The design page distinguishes L1 GPU, L2 host, and L3 storage tiers,
and the PD-disaggregation docs show that transfer timeouts, thread-pool
sizing, queue sizing, heartbeats, and heterogeneous TP staging can change TTFT
and failure behavior.

PalLLM should treat HiCache as proof-gated serving guidance, not a default
player cache. It is promising for repeated system prompts, long proof/docs
traffic, and multi-turn replay, but live companion chat should not move to
host/storage offload until radix-only, L2 host, and optional L3 or P/D profiles
are replayed by route. The collaboration planner now asks operators to record
launch flags, page size, host/storage budget, prefetch/write policy, backend
namespace hash, cache-hit rate, cold/warm TTFT and E2E latency, queue depth,
parser stability, attach/detach or backend-stop rollback, and PalLLM fallback
counters before promotion.

Focused sibling scan impact: an action-RPG sibling runtime and RimLLM both carried generic
hierarchical KV/offload proof-gate notes. PalLLM adopted only the general
evidence boundary and did not lift sibling code, prompts, names, branding, or
product identity.

Primary sources:

- https://docs.sglang.io/docs/advanced_features/hicache_best_practices
- https://docs.sglang.io/docs/advanced_features/hicache_design
- https://docs.sglang.io/docs/advanced_features/pd_disaggregation
- https://docs.sglang.io/advanced_features/router.html

## 0.58. 2026-05-16 refresh: disaggregated prefill/decode is tail-latency proof

Current vLLM docs frame disaggregated prefilling as an experimental
dual-instance topology that separates prefill and decode so operators can tune
TTFT and ITL independently and control tail ITL. The same page is explicit
that this is not a throughput improvement. Current examples show P/D proxy
topologies that move KV state through connectors such as NixlConnector,
P2pNcclConnector, MooncakeConnector, LMCacheConnectorV1, and MultiConnector,
and the disaggregated-encoder example applies a similar proof boundary to
encoder cache transfer and local media allowlists.

PalLLM should therefore treat split prefill/decode serving as a
qualification-only topology for workstation or dual-GPU rigs, not as a default
one-player companion path. The model-collaboration planner now asks operators
to capture a monolithic baseline, prefill/decode endpoint ids, router/proxy
config, redacted `kv_transfer_config`, p95 TTFT, p95 ITL, p95 E2E latency,
KV-transfer latency/failure evidence, queue pressure, decode-only rollback,
and PalLLM fallback counters before any P/D topology can handle live companion
turns.

Focused sibling scan impact: external sibling research independently landed
generic P/D topology receipts with the same TTFT/ITL/E2E, KV-transfer,
fallback, failure, and route-proof boundary. PalLLM adopted only that generic
evidence posture and did not lift sibling code, prompts, names, branding, or
product identity.

Primary sources:

- https://docs.vllm.ai/en/latest/features/disagg_prefill/
- https://docs.vllm.ai/en/latest/examples/disaggregated/disaggregated_serving/
- https://docs.vllm.ai/en/stable/examples/online_serving/disaggregated_encoder/

## 0.57. 2026-05-16 refresh: vLLM KV events need redacted proof

Current vLLM docs expose two complementary cache-evidence surfaces. The
production metrics page names local prefix-cache counters, external
prefix-cache counters for connector-backed cross-instance reuse, cached prompt
tokens, KV pressure, preemption, multimodal cache counters, and sleep state.
The KV-events docs and example show a ZMQ publisher/subscriber path that emits
`BlockStored`, `BlockRemoved`, and `AllBlocksCleared` event batches, including
block hashes, block sizes, group metadata, `token_ids`, and `extra_keys` that
can encode multimodal identifiers, LoRA names, `cache_salt`, and prompt
embedding hashes.

PalLLM should use that event stream only as a local qualification aid. It is
useful for proving router-index freshness, block-store/remove behavior,
Mooncake or external-prefix-cache behavior, and cache-locality claims, but raw
KV events are too revealing for support or public release artifacts. The model
collaboration planner now asks operators to keep KV-event publishers and replay
endpoints loopback/admin-only and to reduce evidence to content-free counts,
block hashes, block-size/group/sliding-window metadata, replay-gap/drop
counters, extra-key classes, and parity against `/metrics`. Promotion fails if
raw token ids, cache salts, media ids, LoRA names, prompt-embedding hashes, or
raw event payloads enter support/public bundles.

Focused sibling scan impact: an action-RPG sibling runtime independently landed a KV-event
router-index integrity policy, while the external prompt-pack project reinforced short-interval prefix and
KV pressure receipts from `/metrics`. PalLLM adopted only the generic current
proof/redaction posture; no sibling code, prompts, names, branding, or product
identity was lifted.

Primary sources:

- https://docs.vllm.ai/en/latest/usage/metrics/
- https://docs.vllm.ai/en/stable/examples/features/kv_events/
- https://docs.vllm.ai/en/stable/api/vllm/config/kv_events/
- https://docs.vllm.ai/en/latest/design/prefix_caching/

## 0.56. 2026-05-16 refresh: compressed speech playback parity

Current vLLM-Omni speech docs expose OpenAI-compatible
`/v1/audio/speech` with `response_format` values including `wav`, `mp3`,
`flac`, `pcm`, `aac`, and `opus`, and return binary audio with an audio
`Content-Type`. PalLLM's C# runtime already mapped `flac`, `opus`, and `ogg`
speech artifacts to `PlaybackHint=media_player`, but the UE4SS Lua playback
resolver only recognized the older MP3/M4A/AAC/WMA subset. That meant a valid
compressed artifact could be written with the correct hint and still be
reported as `speech file format unsupported` before helper launch.

PalLLM now keeps the Lua resolver in parity with the runtime hint surface:
`mp3`, `m4a`, `aac`, `wma`, `ogg`, `opus`, and `flac` route to the local
media-player helper, while WAV stays on `SoundPlayer` and raw PCM remains a
proof-only artifact. The existing content-free `speech_playback` receipt still
stores only request id, mode/hint, MIME/extension, artifact byte count,
attempt count, elapsed milliseconds, started/skipped state, and reason; it
does not persist audio bytes, generated speech text, file paths, upstream
logs, sibling code, prompts, names, or product identity.

Focused sibling scan impact: active `D:\Coding` projects reinforced only the
generic pattern that media proof should carry container, byte-count, timing,
and fallback evidence without raw media. PalLLM adopted no sibling code,
prompts, names, branding, or product identity.

Primary sources:

- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
- https://docs.vllm.ai/en/latest/models/supported_models/
- https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0

## 0.55. 2026-05-16 refresh: Mooncake Store is a proof-only KV topology

Current vLLM guidance now documents `MooncakeStoreConnector` as a shared
KV-cache pool path. The stable usage guide shows a single-node offload shape
using `MOONCAKE_CONFIG_PATH` plus
`--kv-transfer-config '{"kv_connector":"MooncakeStoreConnector","kv_role":"kv_both"}'`,
and a disaggregated prefill/decode shape where `MultiConnector` combines
`MooncakeConnector` with `MooncakeStoreConnector`. The API docs describe the
store connector as a shared KV pool that uses hash-based deduplication, while
the May 2026 vLLM blog frames the benefit around agentic traces with repeated
long prefixes.

PalLLM should treat this as advanced serving proof, not a new player default.
It can be useful for long proof/docs or multi-turn agentic replays, but the
live one-player companion lane must stay on local prefix caching until a
route-labeled proof shows better cold/warm TTFT, E2E latency, cache-hit rate,
store health, quality/parser parity, fallback counters, and rollback behavior.
KV blocks and store metadata are private runtime state; support/public bundles
should preserve only hashes, metric receipts, and redacted config shapes.

Sibling scan impact: external sibling research-style active projects reinforced the
same generic rule for hierarchical or distributed KV cache: no cache win is
credible without cold/warm latency, hit-rate, quality, failure, and rollback
evidence. PalLLM adopted only that generic proof posture; no sibling code,
prompts, names, branding, or product identity was lifted.

Primary sources:

- https://vllm.ai/blog/2026-05-06-mooncake-store
- https://docs.vllm.ai/en/stable/features/mooncake_store_connector_usage/
- https://docs.vllm.ai/en/latest/api/vllm/distributed/kv_transfer/kv_connector/v1/mooncake/store/connector/

## 0.54. 2026-05-16 refresh: Qwen Omni realtime needs async-chunk proof

Current Qwen3-Omni primary docs describe a model family that can understand
text, image, audio, and video while generating text and speech. Current
vLLM-Omni Qwen3-Omni serving docs expose ordinary multimodal chat examples and
document the `/v1/realtime` audio path, but they also call out a concrete
serving caveat: the OpenAI-style realtime WebSocket is unsupported while the
`async_chunk` deploy configuration is enabled. That makes a "server started"
receipt too weak for PalLLM voice promotion.

PalLLM therefore records this as model-serving guidance rather than a new
runtime default. Qwen Omni capability profiles now require an
`async_chunk`-disabled deploy receipt before `/v1/realtime` can be considered a
player-facing voice lane. The proof must still include session creation,
audio/transcript delta evidence, reconnect/stall behavior, and confirmation
that normal `/api/chat` text fallback continues while realtime audio is
unhealthy. Ordinary companion chat remains field-free and deterministic
fallback-grade.

Sibling scan impact: an external asset-generation sibling had already converged on provider-free realtime
audio probe plans that carry the same `async_chunk` caveat. PalLLM adopted only
that generic current-doc guardrail; no sibling code, prompts, names, branding,
or product identity was lifted.

Primary sources:

- https://github.com/QwenLM/Qwen3-Omni
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0

## 0.53. 2026-05-16 refresh: speech playback needs launch lifecycle receipts

Current vLLM-Omni Qwen3-Omni documentation exposes both ordinary multimodal
requests and realtime audio delta paths, while the vLLM-Omni speech API
continues to return binary audio with a concrete `Content-Type`. UE4SS now
documents delayed-action APIs with cancellable handles, which is useful future
groundwork for native in-world audio, but PalLLM's shipping bridge still needs
to prove the current helper-launch seam without changing the default
Palworld text-render path. Microsoft ASP.NET Core request-timeout guidance also
keeps reinforcing the same principle already used in the sidecar: expensive
lanes should carry bounded, endpoint-specific proof rather than unbounded
blocking work.

PalLLM therefore extends the content-free `speech_playback` receipt with
`AttemptCount` and `ElapsedMs`. The Lua bridge reports how many local helper
launch attempts were made and how long the launch/preflight path took, while
the sidecar clamps and surfaces those numbers in `RuntimeHealth.BridgeLoop`,
OpenAPI, schemas, and `/api/bridge/proof`. The receipt still stores no audio
bytes, generated speech text, local file path, upstream logs, or sibling
project identity.

Sibling scan impact: an external project's media-smoke runtime receipts reinforce the
generic pattern of recording status, timing, byte counts, and stable proof
fields without raw media. an action-RPG sibling runtime and RimLLM continue to keep audio lanes
route-proof-gated. PalLLM adopted only that generic receipt posture; no sibling
code, prompts, names, branding, or product identity was lifted.

Primary sources:

- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
- https://docs.ue4ss.com/dev/lua-api/global-functions/delayedactions.html
- https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0

## 0.52. 2026-05-16 refresh: ASR server chunking stays opt-in

Current OpenAI transcription docs expose file-level `chunking_strategy`,
including `auto`, so compatible endpoints can choose voice-activity-based
chunk boundaries instead of forcing every caller to pre-split longer audio.
The same documentation and vLLM-compatible ASR posture still make support
model/server-specific: strict local endpoints may reject unknown multipart
fields, and player-facing voice lanes need measured p95 latency before a
server-side chunker becomes a default.

PalLLM therefore added `PalLLM:Asr:ChunkingStrategy` as an empty-by-default,
startup-validated pass-through. Only `auto` is allowed today. When set,
`HttpAudioTranscriptionClient` emits multipart `chunking_strategy=auto`;
when unset, ASR requests remain field-free beyond the already configured
model/language/prompt/response-format probes. This keeps the default local
posture unchanged while giving operators a clean canary for pause-heavy
player speech and longer clips.

Sibling scan impact: external sibling research both reinforce adaptive VAD and
voice-turn proof, but their code and branded voice flows do not fit PalLLM's
Palworld bridge. PalLLM adopted only the generic idea of explicit VAD/chunking
proof before promotion; no sibling code, prompts, names, branding, or product
identity was lifted.

Primary sources:

- https://platform.openai.com/docs/api-reference/audio/transcriptions
- https://platform.openai.com/docs/guides/speech-to-text
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/

## 0.51. 2026-05-16 refresh: ASR verbose segment quality receipts

Current OpenAI transcription verbose responses expose segment-level
`avg_logprob`, `compression_ratio`, `no_speech_prob`, `temperature`, token ids,
and text. The useful publication-proof part is the compact quality signal:
OpenAI's current API reference explicitly calls out low average logprob and
high compression ratio as review-worthy segment conditions. vLLM and SGLang
continue to mirror OpenAI-compatible transcription routes, but support varies
by model and server build, so PalLLM should treat segment quality as an
endpoint-proven canary rather than a default gameplay promise.

PalLLM therefore added `AudioTranscriptionQualityReceipt` beside the existing
ASR confidence/timing receipts. The adapter parses ASR JSON once, then reduces
verbose segment quality to counts, min/max/average numbers, thresholds, and
review flags for low average logprob, high compression ratio, silent-segment
candidates, and partial metadata. It does not preserve raw audio, transcript
text, segment text, word text, token ids, prompt hints, upstream verbose JSON,
or upstream logs in health, metrics, proof bundles, or support evidence.

Sibling scan impact: an external project's active multimodal policy work reinforced
media preflight receipts and uncertainty receipts; RimLLM's voice-lane notes
reinforced proof-gating exact audio routes; an external project's rights/provenance work
reinforced local receipt posture. PalLLM adopted only the generic
content-free receipt idea; no sibling code, prompts, names, branding, or
product identity was lifted.

Primary sources:

- https://developers.openai.com/api/reference/resources/audio
- https://docs.vllm.ai/en/stable/api/vllm/entrypoints/openai/protocol/
- https://sgl-project.github.io/supported_models/text_generation/multimodal_language_models.html
- https://ai.google.dev/gemma/docs/capabilities/audio

## 0.51. 2026-05-16 refresh: speech playback proof needs artifact preflight

Current vLLM-Omni speech serving still returns local audio containers through
OpenAI-compatible `/v1/audio/speech`, while current vLLM transcription docs
make audio file-size limits an explicit serving concern. ASP.NET Core's
request-timeout guidance continues to recommend endpoint-specific policies for
expensive lanes, so PalLLM's HTTP side already bounds the TTS request. The
remaining weak spot was the game-side playback receipt: a hidden local helper
launch could be reported as "started" even if the file was unreadable or
zero-byte.

PalLLM now adds a cheap Lua-side artifact preflight before the helper launch.
The bridge rejects missing, unreadable, empty, and invalid-WAV artifacts and
adds `ArtifactBytes` to the content-free `speech_playback` receipt. The sidecar
surfaces that size in `RuntimeHealth.BridgeLoop` and `/api/bridge/proof`, while
still avoiding raw audio, generated text, absolute file paths, upstream logs,
or sibling-project identity.

Sibling scan impact: RimLLM's TTS validation notes reinforced the generic
"reject empty or malformed audio before playback" pattern, and an external project's media-smoke receipts reinforced recording byte counts and hashes instead of
raw audio. PalLLM adopted only the generic bounded-proof idea; no sibling code,
prompts, names, branding, or product identity was lifted.

Primary sources:

- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0
- https://docs.ue4ss.com/dev/lua-api/global-functions/registerhook.html

## 0.50. 2026-05-16 refresh: native speech needs playback receipts

Current OpenAI-compatible speech serving continues to revolve around
`/v1/audio/speech` responses that are saved or streamed into a local audio
container, and vLLM's current audio docs keep audio file-size limits explicit
for OpenAI-compatible transcription. vLLM-Omni's Qwen3-TTS examples expose the
same OpenAI-style `input` / `voice` speech route plus file output. UE4SS
remains a Lua scripting layer for Unreal Engine games, so the Palworld bridge
should prove game-side delivery through bridge receipts rather than assuming a
local file written by the sidecar was actually played.

PalLLM therefore added a content-free `speech_playback` bridge event. The Lua
outbox consumer emits it after a local helper attempt with request id,
started/skipped state, playback mode, playback hint, MIME/extension, and a
short reason. The sidecar folds that into `RuntimeHealth.BridgeLoop` and
`/api/bridge/proof` as a separate proof lane. Release/support evidence still
does not store audio bytes, TTS text, or absolute local audio paths.

Sibling scan impact: an external project's voice lane stresses local-first audio, bounded
latency, and no silent microphone/audio persistence; RimLLM's recent TTS
passes emphasize bounded playback helpers and lifecycle/cancellation receipts;
an external project's media-smoke pattern keeps media proof schema-backed and
provider-free. PalLLM adopted only the generic receipt posture; no sibling
code, prompts, names, branding, or product identity was lifted.

Primary sources:

- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://docs.vllm.com.cn/projects/vllm-omni/en/latest/user_guide/examples/online_serving/qwen3_tts/
- https://docs.ue4ss.com/
- https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0

## 0.49. 2026-05-16 refresh: ASR needs upstream receipt parity

Current OpenAI-compatible production guidance treats response request IDs as
support/debugging evidence, and OpenAI-style stacks also expose compact
provider processing-duration receipts such as `openai-processing-ms`. vLLM's
current OpenAI-compatible server keeps `/v1/audio/transcriptions` as the
standard multipart ASR route and exposes `X-Request-Id` response headers when
the server enables request-id headers. HTTP `Server-Timing` remains the
standard way to carry backend duration metrics with millisecond `dur` values.

PalLLM therefore extended the ASR proof lane to preserve the same low-content
receipt shape already used by chat and vision: `AudioTranscribeResponse` now
includes sanitized upstream request id, upstream processing duration, and
queue/TTFT/prefill/decode timing fields when a compatible server returns those
headers. `RuntimeHealth`, `/metrics`, and proof-bundle manifests count only
whether those ASR receipts were present. They do not store raw audio, prompt
hints, token text, transcripts, upstream logs, or unbounded headers.

Sibling scan impact: an external project's active media-smoke path records provider
request IDs and processing durations, while RimLLM keeps voice transcription
proof/cancellation evidence content-free. PalLLM adopted only that generic
receipt pattern; no sibling code, prompts, names, branding, or product identity
was lifted.

Primary sources:

- https://developers.openai.com/api/reference/overview#debugging-requests
- https://platform.openai.com/docs/api-reference/audio/createTranscription
- https://docs.vllm.ai/en/v0.20.0/serving/openai_compatible_server/
- https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Server-Timing

## 0.48. 2026-05-16 refresh: ASR confidence should be content-free

Current OpenAI transcription docs expose optional `temperature` and
`include[]=logprobs` fields on compatible speech-to-text models. Returned
logprobs are useful for confidence gating, but the useful publication-proof
signal is not the token text: it is whether confidence evidence was requested,
whether the server returned it, how many tokens were scored, and whether any
scores crossed a low-confidence threshold. The same current docs constrain
logprobs to JSON responses and note that support is model-specific, so PalLLM
keeps the request switch default-off.

Sibling project scanning reinforced the same generic pattern: confidence gates
are useful when they reduce a media/model result to a compact review signal and
avoid storing user content in release evidence. PalLLM therefore added
`PalLLM:Asr:Temperature`, `PalLLM:Asr:RequestLogprobs`, and
`PalLLM:Asr:LowConfidenceLogprobThreshold`. Runtime responses now include an
`AudioTranscriptionConfidenceReceipt` with only requested/returned state,
token count, average/min logprob, low-confidence token count, threshold, and a
ready/review/not-returned status. Health, metrics, and proof bundles count the
receipt and review totals without persisting token text, raw audio, prompts, or
transcript text.

Primary sources:

- https://platform.openai.com/docs/api-reference/audio/transcriptions
- https://platform.openai.com/docs/guides/speech-to-text
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0

## 0.47. 2026-05-16 refresh: voice-turn proof needs endpointing receipts

Current OpenAI realtime turn-detection docs separate transcription from voice
activity detection. Server VAD has explicit knobs for pre-speech padding,
trailing silence, idle timeout, threshold, and interrupt response behavior; the
docs warn that shorter silence closes feel faster but can cut in on natural
pauses. Current vLLM docs keep non-streaming transcription as a multipart
`/v1/audio/transcriptions` route, while local VAD stacks such as Silero and
LocalAI return speech segments or timing decisions that can be summarized
without preserving audio content.

PalLLM therefore records optional `Endpointing` metadata on
`POST /api/audio/transcribe` as content-free proof rather than forwarding it to
ASR servers. The response and health/proof counters carry only speech duration,
leading silence, trailing silence, close reason, barge-in flag, and compact
review flags against the configured padding/silence/duration targets. This is
enough to prove future player-speech gating, barge-in cancellation, and stale
turn rejection without archiving raw audio, prompts, transcripts, or utterance
text.

Primary sources:

- https://developers.openai.com/api/reference/resources/realtime
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://github.com/snakers4/silero-vad
- https://localai.io/features/voice-activity-detection/index.html

## 0.46. 2026-05-16 refresh: bounded ASR belongs on a separate opt-in lane

Current vLLM OpenAI-compatible server docs expose a Transcriptions API that is
compatible with OpenAI's shape, requires audio extras (`vllm[audio]`), accepts
multipart audio files, requires `file` and `model`, and allows optional
`language`, `prompt`, `response_format`, and temperature fields. The documented
upload formats include FLAC, MP3, MP4, MPEG, MPGA, M4A, OGG, WAV, and WEBM.
The same docs also expose a realtime WebSocket transcription path, but that
path requires base64 PCM16 at 16 kHz mono and changes the protocol surface.

PalLLM implemented the conservative first step: a non-streaming,
OpenAI-compatible ASR proof lane at `POST /api/audio/transcribe`. It stays
separate from ordinary chat and defaults off. The route accepts JSON with
base64 audio, validates decoded size and MIME type locally, and only then posts
multipart `file` data plus optional model/language/prompt hints to
`PalLLM:Asr:BaseUrl`. This preserves the repo's low-latency typed/fallback
guarantee when speech infrastructure is absent or failing, while still letting
operators qualify local ASR servers against the same privacy, air-gap,
resource-budget, timeout, and admission-control posture as TTS.

Gemma audio guidance still matters for future native audio-in promotion:
Gemma 4 audio costs 25 tokens per second, Gemma 3n audio costs 6.25 tokens per
second, clips are capped at 30 seconds, and audio should be treated as mono
16 kHz-style model input. That supports PalLLM's current decision to keep
player speech on typed text or cascaded ASR until native audio-in can beat ASR
on privacy, latency, parse stability, and fallback behavior. ASP.NET Core's
request-timeout middleware and rate-limiting docs support keeping ASR on a
named heavy lane with explicit timeout, queue, and overload behavior rather
than allowing audio requests to grow unbounded.

Primary sources:

- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://ai.google.dev/gemma/docs/capabilities/audio
- https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0
- https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0

## 0.45. 2026-05-16 refresh: speech response MIME must survive weak local servers

Current vLLM-Omni speech docs describe `/v1/audio/speech` as returning binary
audio with an appropriate `Content-Type` such as `audio/wav`. In practice, local
servers and simple proxies can omit the header or downgrade it to
`application/octet-stream`, especially when experimenting with raw PCM canaries.
OpenAI-compatible speech clients still know the requested container because the
request carries `response_format`, so PalLLM can safely use that value as a
bounded fallback instead of labelling every unknown response as WAV.

PalLLM now maps the allowlisted `Tts.ResponseFormat` values to MIME types only
for `Tts.RequestFormat=openai_speech` responses whose content type is absent or
generic binary. Concrete upstream audio MIME types still win. Runtime speech
artifact writes preserve raw PCM as `.pcm` with `PlaybackHint=raw_pcm`; `flac`,
`opus`, and `ogg` use the existing media-player hint rather than `unknown`.
This keeps proof-lane audio evidence honest without changing ordinary text
chat, deterministic fallback, legacy `{ text, voice }` adapters, or
proof-bundle privacy.

Related refreshed facts: current vLLM exposes `/v1/audio/transcriptions` for
ASR models with multipart uploads and a default 25 MB file-size limit; current
Gemma audio guidance keeps audio input capped to 30 seconds, with Gemma 4
costing 25 tokens per second and Gemma 3n costing 6.25 tokens per second.
Those support a future bounded ASR lane, but this pass only fixes the TTS
response container contract.

Primary sources:

- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://ai.google.dev/gemma/docs/capabilities/audio
- https://huggingface.co/google/gemma-4-31B

## 0.44. 2026-05-16 refresh: TTS adapter needs OpenAI-compatible speech shape

Current vLLM-Omni speech docs expose TTS-class models through an
OpenAI-compatible `POST /v1/audio/speech` endpoint. The common request body
uses `input`, `voice`, and `response_format`; some examples include `model`,
while local single-model servers can infer the served model. The same docs
call out streaming PCM (`response_format=pcm`, `stream=true`) and model-family
sample-rate differences, so PalLLM should not assume every speech lane accepts
the older local `{ text, voice }` adapter body.

PalLLM now keeps the legacy request body as the default
`Tts.RequestFormat=simple`, and adds an opt-in
`Tts.RequestFormat=openai_speech` body that sends `input`, `voice`, optional
`model`, and `response_format`. Startup validation allowlists the request
format and speech response containers (`wav`, `mp3`, `opus`, `aac`, `flac`,
`pcm`) so typos fail before the sidecar starts. The runtime still reads
successful audio with the bounded byte reader and still archives only
content-free TTS success/failure counters in release proof bundles.

Sibling scan impact: external sibling research all keep voice lanes
separate from ordinary chat and treat `/v1/audio/speech` proof as a dedicated
runtime canary. PalLLM adopted only the generic compatibility shape; no sibling
code, prompts, names, branding, or product identity were lifted.

Primary sources:

- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/
- https://docs.vllm.ai/projects/vllm-omni/en/latest/user_guide/examples/online_serving/text_to_speech/
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/

## 0.44. 2026-05-16 refresh: ASR proof receipts stay content-free

Current OpenAI-compatible speech-to-text APIs use multipart
`/v1/audio/transcriptions` with `file`, `model`, optional `language` and
`prompt`, and `response_format=json` returning a `text` field. Current vLLM
OpenAI-compatible serving docs expose the same transcription route when the
server is installed with audio extras. ASP.NET Core's request-timeout guidance
supports endpoint-level timeouts across Minimal APIs, which matches PalLLM's
existing heavy-lane timeout posture for ASR uploads.

PalLLM already bounded incoming base64 audio, upstream JSON response bytes, and
returned transcript length. This pass closes the evidence gap: `RuntimeHealth`
now reports content-free ASR counters, `/metrics` exports matching Prometheus
counters, and proof bundles archive only enabled/call/failure/success evidence
for ASR. The proof manifest deliberately excludes raw audio, prompt hints,
transcripts, and audio paths, so publication/support bundles can prove the ASR
lane was exercised without carrying sensitive player speech.

Sibling scan impact: an external asset-generation sibling reinforced deterministic transcription profiles
and live-mod proof bundles; the external prompt-pack project reinforced local-first transcription lanes;
an action-RPG sibling runtime reinforced audio turn endpointing and barge-in proof gates. PalLLM
adopted only the generic release-evidence idea and did not lift sibling code,
prompts, names, branding, or product identity.

Primary sources:

- https://platform.openai.com/docs/api-reference/audio/transcriptions
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server.html
- https://learn.microsoft.com/aspnet/core/performance/timeouts?view=aspnetcore-10.0

## 0.43. 2026-05-16 refresh: proof bundles need TTS/audio receipts

Current vLLM-Omni Qwen3-Omni serving docs expose text+audio output through
OpenAI-compatible chat-completions `modalities=["text","audio"]` and preserve
`message.audio` receipts. The same docs warn that `/v1/realtime` requires the
server to run without async chunking for streaming PCM sessions, and the
streaming video API accepts base64 video frames plus optional PCM16 audio
chunks over WebSocket. The separate vLLM-Omni speech API now exposes an
OpenAI-compatible `/v1/audio/speech` route for TTS-class models such as
Qwen3-TTS, Fish Speech, and Voxtral TTS. OpenTelemetry GenAI metrics still
favor content-free token and duration signals over archiving prompt,
completion, or media bodies.

PalLLM already had three audio-adjacent paths: ordinary chat-linked
`SpeechArtifact` files from `TtsClient`, isolated chat-completions audio-output
canaries through `InferencePrompt.Modalities` / `Audio`, and future native
in-world playback from the UE4SS bridge. The weak point was release evidence:
proof bundles archived model-lane receipts but did not preserve whether the
TTS lane was enabled, attempted, failing, or had successful call evidence.
The release proof bundle manifest now records `TtsEnabled`, `TtsCallCount`,
`TtsFailureCount`, and `TtsSuccessEvidenceCount` from the archived health
snapshot. `ReleaseProofBundleEvidenceBuilder` normalizes those counters, and
`ReleaseBundleArchiveInspector` requires the archived manifest to match them
before `/api/release/readiness` trusts a proof bundle as recorded. This keeps
native-audio promotion evidence compact and content-free: no text, no voice
sample, no generated audio payload, and no raw media path is needed to prove
the lane had successful configured calls.

Sibling scan impact: no sibling code or branding was lifted. The useful generic
idea was to keep audio proof in the same durable release bundle as latency and
bridge proof so native playback work has one operator-facing readiness surface.

Primary sources:

- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/
- https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/video_stream_api/
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/

## 0.42. 2026-05-15 refresh: upstream phase timings become lane proof

Current vLLM production metrics separate end-to-end request latency from
time-to-first-token, inter-token latency, queue time, request prefill time, and
request decode time. The vLLM-Omni Qwen3-Omni serving docs also show why
multimodal/audio lanes need phase-level proof: real deployments may split
thinker, talker, and codec stages, can toggle prefix caching, and must disable
async chunking for realtime WebSocket sessions. Current OpenTelemetry GenAI
metrics keep `gen_ai.client.operation.duration` required and include
time-to-first-chunk / time-per-output-chunk client metrics plus server TTFT
metrics, so phase timings are useful low-content evidence when the upstream
server exposes them. vLLM's OpenAI-compatible docs also warn that request-id
headers can cost performance at high QPS, which reinforces PalLLM's current
posture: preserve receipts when present, but do not require extra per-request
headers for ordinary local play.

PalLLM now parses bounded upstream phase timings from compatible millisecond
headers and `Server-Timing` metrics named like `queue`, `ttft`,
`time_to_first_token`, `prefill`, and `decode`. `InferenceResult` and
`VisionResult` carry `UpstreamQueueMs`, `UpstreamTimeToFirstTokenMs`,
`UpstreamPrefillMs`, and `UpstreamDecodeMs`; `/api/inference/performance`
exposes those as `Lanes[].LastUpstream*` fields; the Field Console renders a
compact phase row; and proof bundles record
`InferencePerformancePhaseTimingReceiptLaneCount`. This keeps route-lane
promotion grounded in the same queue/TTFT/prefill/decode signals that current
serving stacks use, without archiving prompts, completions, media, or raw
server logs.

Sibling scan impact: an external project's active resource audit repeatedly requires
TTFT, decode, queue, and route-keyed benchmark proof before promoting local
serving policies, while RimLLM exposes queue state and cache-reuse posture in
its runtime diagnostics. PalLLM adopted the generic evidence shape only; no
sibling code, prompts, names, branding, or product identity were lifted.

Primary sources:

- https://docs.vllm.ai/en/stable/design/metrics/
- https://docs.vllm.ai/en/v0.7.3/serving/openai_compatible_server.html
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/

## 0.41. 2026-05-15 refresh: upstream processing-duration receipts become lane proof

Current HTTP guidance treats `Server-Timing` as the standard response header
for backend timing metrics with `dur` values in milliseconds, while also
warning that public timing disclosure should be deliberate. Current
OpenAI-compatible API debugging docs list `openai-processing-ms` as the
provider-side processing duration and `x-request-id` as the paired support
identifier. Current vLLM production metrics emphasize request-level timing
signals such as TTFT, inter-token latency, queue time, prefill/decode time, and
end-to-end request latency. Current OpenTelemetry GenAI metrics still require
`gen_ai.client.operation.duration` and recommend token usage, which makes
content-free timing receipts a safe complement to PalLLM's existing
client-observed latency.

PalLLM now captures bounded upstream processing-duration receipts from
successful and failed text/vision HTTP responses without changing ordinary
request bodies. `InferenceResult` and `VisionResult` carry
`UpstreamProcessingMs`, `/api/inference/performance ->
Lanes[].LastUpstreamProcessingMs` exposes the latest receipt per lane, the
Field Console shows it beside the upstream id, and proof bundles record
`InferencePerformanceUpstreamProcessingReceiptLaneCount`. Negative,
non-finite, malformed, and implausibly large values are rejected, so the
release proof surface keeps only compact timing evidence and no prompt,
completion, or media content.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced the same direction. RimLLM keeps latency-first foreground lanes and
readiness-loop reductions prominent, an external asset-generation sibling carries per-run latency
posture fields, and an action-RPG sibling runtime animation endpoints report latency totals.
PalLLM adopted no sibling code, names, prompts, branding, or product identity.

Primary sources:

- https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Server-Timing
- https://developers.openai.com/api/reference/overview#debugging-requests
- https://docs.vllm.ai/en/latest/design/metrics/
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/

## 0.40. 2026-05-15 refresh: upstream request-id receipts become lane proof

Current OpenAI-compatible production guidance treats HTTP response request IDs
as support/debugging evidence: the response headers can carry a unique
`x-request-id`, and production deployments are encouraged to log it. Current
vLLM serving docs expose matching `X-Request-Id` support behind
`--enable-request-id-headers`, and current OpenTelemetry GenAI conventions
already keep response ids, response models, finish reasons, and token counts as
low-content model-operation receipts. Sibling scans of actively updated
projects under `D:\Coding` reinforced the same pattern: external sibling research
both use request IDs for log/support correlation, while keeping raw prompt or
media payloads out of portable handoff artifacts.

PalLLM now captures bounded upstream request/correlation ids from successful
and failed text/vision HTTP responses without changing ordinary request bodies.
`InferenceResult` and `VisionResult` carry `UpstreamRequestId`,
`/api/inference/performance -> Lanes[].LastUpstreamRequestId` surfaces the
latest receipt per lane, the Field Console shows it beside finish/token
receipts, and proof bundles record
`InferencePerformanceUpstreamRequestIdReceiptLaneCount`. Header values are
trimmed, capped, and rejected when they contain control characters, so local
model servers can expose useful log-correlation handles without leaking prompt
content or unbounded headers into the release manifest.

Primary sources:

- https://developers.openai.com/api/reference/overview#debugging-requests
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/#extra-http-headers
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/

## 0.39. 2026-05-11 refresh: usage-detail receipts prove cache, reasoning, audio, and prediction lanes

Current OpenAI Chat Completions usage objects expose nested
`prompt_tokens_details.cached_tokens`, prompt audio-token counts, completion
reasoning-token counts, completion audio-token counts, and predicted-output
accepted/rejected token counts. OpenAI's prompt-caching guide explicitly says
cache performance should be monitored and that `cached_tokens` appears on the
usage object even when it is zero. Current OpenTelemetry GenAI span conventions
still keep input/output token totals as the low-cardinality baseline and call
out reasoning output tokens as part of output token accounting. Current vLLM
OpenAI-compatible serving docs also expose an `enable_prompt_tokens_details`
server option, making detailed usage receipts relevant to local lanes too.

PalLLM now parses those nested usage details without changing ordinary request
bodies. `TokenUsage` carries cached prompt, prompt audio, completion reasoning,
completion audio, accepted prediction, and rejected prediction token counts.
`/api/inference/performance` carries both latest and recent-total detailed
usage receipts per lane, the Field Console shows the compact latest cache /
reasoning / audio / prediction row when present, and proof bundles add a compact
`InferencePerformanceUsageDetailReceiptLaneCount` plus cached-prompt and
reasoning-token totals. Missing nested usage objects remain zero, so strict or
older local endpoints keep the same behavior.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced the same direction. BYTE has explicit reasoning-token capability
flags and finish-reason telemetry, while an action-RPG sibling runtime readiness artifacts mention
cached-token evidence. PalLLM adopted no sibling code, names, prompts, branding,
or product identity.

Primary sources:

- https://platform.openai.com/docs/guides/prompt-caching/prompt-caching
- https://platform.openai.com/docs/api-reference/chat/create-chat-completion
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/
- https://docs.vllm.ai/serving/openai_compatible_server.html

## 0.38. 2026-05-11 refresh: finish reasons become lane receipts

Current OpenTelemetry GenAI span conventions recommend
`gen_ai.response.finish_reasons` as the model-operation stop-reason receipt
beside response id/model and usage-token attributes. Current OpenAI-compatible
chat-completions responses carry choice-level `finish_reason` values, and
current vLLM OpenAI-compatible protocol docs expose the same response field for
both non-streaming and streaming chat choices. That makes finish reasons useful
release evidence: `stop`, `length`, and `tool_calls` explain whether a lane
completed normally, was truncated, or intentionally ended in a tool/action
handoff.

PalLLM already parsed finish reasons for GenAI telemetry, but dropped them
before `InferenceResult`, vision results, and `/api/inference/performance`.
The safe fix is response-only: preserve parsed reasons in typed results, carry
the latest lane receipt as `InferencePerformanceSnapshot.Lanes[].LastFinishReasons`,
show it in the Field Console lane row, and add a compact
`InferencePerformanceFinishReasonReceiptLaneCount` to proof-bundle manifests.
No request fields, route count, feature count, MCP tools, deterministic
fallback, or player-chat defaults change.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced the same low-cardinality pattern. BYTE records finish reasons in its
GenAI telemetry helpers, and an external project's runtime optimizer docs call finish
reason evidence out beside route budgets and final-token receipts. PalLLM
adopted no sibling code, names, prompts, branding, or product identity.

Primary sources:

- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/
- https://developers.openai.com/api/reference/resources/chat
- https://docs.vllm.ai/en/v0.15.0/api/vllm/entrypoints/openai/chat_completion/protocol/

## 0.37. 2026-05-11 refresh: proof bundles carry model receipts

Current OpenTelemetry GenAI span conventions recommend completion ids and
input/output usage-token attributes, and warn that recorded input/output
message attributes can contain sensitive data. Current OpenAI-compatible chat
responses expose completion `id`, `model`, optional `system_fingerprint`, and
`usage` totals. Current serving documentation keeps token and latency evidence
operationally visible as well: vLLM exposes prompt/generation token counters,
request token histograms, queue latency, TTFT, and cache metrics, while SGLang
GenAI Bench defines input/output/request token counts plus TTFT, TPOT,
throughput, and error-rate metrics.

PalLLM already surfaced recent lane receipts at `/api/inference/performance`,
but `scripts/export-release-proof-bundle.ps1` archived only release/readiness,
bridge proof, health, smoke/native proof, and HUD config evidence. That made a
release proof bundle weaker than the live sidecar: reviewers could prove the
native loop but not see which model-serving window was current when the bundle
was captured. The safe shape is to archive the full inference-performance
snapshot as a separate bundle file, then put only compact status/count/token
receipt fields into the manifest. That avoids raw prompt/completion text in the
shareable summary while still proving model-lane health.

Implementation impact: Pass 245 adds `inference-performance.json` to release
proof bundles and extends `ReleaseProofBundleEvidenceSnapshot` with
`InferencePerformanceStatus`, sample count, lane count, alerting lane count,
latest response/fingerprint receipt lane count, latest token receipt lane
count, and total tokens. The release-readiness reader normalizes those counts
and the archive inspector requires the archived manifest to agree with the
latest sidecar-readable manifest before trusting the bundle as `recorded`.
Request serialization, route count, feature count, MCP tools, deterministic
fallback, and the existing `/api/inference/performance` contract stay
unchanged.

Sibling scan impact: actively updated sibling repos under `D:\Coding` again
reinforced the generic pattern of low-cardinality GenAI receipts, route-labeled
proof artifacts, and avoiding raw prompt content in portable/support bundles.
PalLLM adopted no sibling code, names, prompts, branding, or product identity.

Primary sources:

- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/
- https://developers.openai.com/api/reference/resources/chat
- https://docs.vllm.ai/en/latest/usage/metrics/
- https://docs.sglang.io/genai-bench/getting-started/metrics-definition/

## 0.36. 2026-05-11 refresh: latest per-lane token receipts

Current OpenTelemetry GenAI semantic conventions keep
`gen_ai.usage.input_tokens` and `gen_ai.usage.output_tokens` in the recommended
receipt family for model operations, alongside completion ids, response model,
and finish reasons. Current OpenAI-compatible chat-completions responses expose
`usage.prompt_tokens`, `usage.completion_tokens`, and `usage.total_tokens`.
Current local serving stacks also keep token metrics operationally visible:
vLLM's production metrics include prompt/generation token counters plus
per-request prompt/generation histograms, and SGLang exposes model/server info
plus health-generate probes for local serving observability.

PalLLM already parsed per-call token usage and exposed recent-window aggregate
and average totals, but `/api/inference/performance` did not identify the
latest token receipt for a lane. That made it harder to answer whether a new
route canary was slow because the last call got larger, or because the backend
lane itself regressed. The safe change is response-only: keep request shapes
unchanged, keep endpoints that omit usage at zero, and add latest prompt,
completion, and total token values to the lane snapshot.

Implementation impact: Pass 244 adds
`InferencePerformanceSnapshot.Lanes[].LastPromptTokens`,
`LastCompletionTokens`, and `LastTotalTokens`, mirrors them in the Field
Console lane row, and documents the fields as proof receipts for lane promotion
and replay review. Deterministic fallback behavior, route count, feature count,
MCP tools, and request serialization are unchanged.

Primary sources:

- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/
- https://platform.openai.com/docs/api-reference/chat/object
- https://docs.vllm.ai/en/v0.18.1/usage/metrics/
- https://docs.sglang.io/basic_usage/native_api.html

## 0.35. 2026-05-11 refresh: completion ids become proof receipts

Current OpenTelemetry GenAI semantic conventions keep `gen_ai.response.id`,
`gen_ai.response.model`, `gen_ai.response.finish_reasons`, and usage tokens in
the recommended receipt family for model operations, while explicitly keeping
the convention set in development. Current OpenAI-compatible chat-completions
objects still expose a top-level completion `id`, and local OpenAI-compatible
runtimes may pass through or synthesize that field.

PalLLM already tagged the parsed completion id on the GenAI span, but the
runtime dropped it from `InferenceResult` and `/api/inference/performance`.
That left operators with a trace-only receipt instead of the same lightweight
evidence in the app-facing lane snapshot. The safe change is response-only: do
not send new request fields, do not change ordinary companion chat, and do not
require endpoints to return an id.

Implementation impact: Pass 243 preserves the parsed chat-completions `id` on
`InferenceResult.ResponseId` and carries the latest non-empty value into
`/api/inference/performance -> Lanes[].LastResponseId`, beside the existing
`LastSystemFingerprint`. Missing or non-string ids remain empty, so strict
local endpoints and deterministic fallback behavior are unchanged.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced the same pattern: keep GenAI model/usage/finish/response identity
structured and route-labeled, and avoid storing raw prompt content in public
or support artifacts. PalLLM adopted no sibling code, prompts, names,
branding, or product identity.

Primary sources:

- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-events/
- https://platform.openai.com/docs/api-reference/chat/object
- https://docs.vllm.ai/en/v0.18.2/api/vllm/entrypoints/openai/chat_completion/protocol/
- https://docs.ollama.com/openai

## 0.34. 2026-05-11 refresh: multimodal input content parts stay route-scoped

Current OpenAI Chat Completions docs define user message `content` as either a
string or an array of content parts, with text, image, audio, and file inputs
varying by model support. Current vLLM multimodal examples use the same
content-part pattern for `image_url`, `video_url`, OpenAI-compatible
`input_audio`, and vLLM `audio_url`. Current vLLM-Omni Qwen3-Omni serving docs
also show the value of modality control when a single model can accept text,
image, video, and audio while also generating audio.

That makes a unified content-part hook useful for PalLLM route canaries: a
single proof caller can test text+image, text+video, or text+audio request
shapes without changing ordinary companion chat. It is not safe as a global
chat default because unsupported endpoints can reject array content, audio and
video payloads can be large, and remote media URLs need SSRF/redirect controls.

Implementation impact: Pass 242 adds prompt-level `InferencePrompt.UserContent`.
`HttpJsonInferenceClient` keeps normal companion chat as a string user message,
but route-specific proof callers can forward a raw user `content` JSON value
such as a content-part array. Serving-profile guidance now requires accepted
request shape, media-admission byte caps, parse stability, p95 latency, and
fallback counters before the hook feeds native multimodal lanes.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced only bounded media-admission and route-scoped proof hooks. PalLLM
adopted no sibling code, prompts, names, branding, or product identity.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create-chat-completion
- https://docs.vllm.ai/en/v0.7.1/serving/multimodal_inputs.html
- https://docs.vllm.ai/en/v0.7.0/getting_started/examples/openai_chat_completion_client_for_multimodal.html
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
## 0.33. 2026-05-11 refresh: audio-output stays isolated and receipt-backed

Current OpenAI Chat Completions docs expose `modalities` for requesting text
and audio outputs, with `audio` response parameters required when audio output
is requested. Current vLLM-Omni Qwen3-Omni serving docs expose the same
OpenAI-compatible `modalities` control and show text-only, audio-only, and
text+audio output paths. Current Gemma audio guidance separately confirms the
audio-token budget that makes audio-in proof lanes expensive: Gemma 4 uses
`25` audio tokens per second, while Gemma 3n uses `6.25`.

That makes audio-output useful for PalLLM voice proof lanes, but it is not a
normal companion-chat default. Audio replies can be large, speaker/runtime
configuration is endpoint-specific, and player text must still return when the
audio lane is stalled or unhealthy.

Implementation impact: Pass 241 adds prompt-level `InferencePrompt.Modalities`
and `InferencePrompt.Audio`. `HttpJsonInferenceClient` forwards
`modalities` / `audio` only when a route-specific proof caller supplies them,
keeps ordinary companion chat field-free, and preserves returned `message.audio`
JSON on `InferenceResult.AudioJson`. Serving-profile guidance now requires
accepted request shape, text mirror, returned audio receipt, response-size
impact, p95 latency, and fallback counters before audio-output canaries feed
native voice work.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced only the generic route-isolated media proof pattern. PalLLM adopted
no sibling code, prompts, names, branding, or product identity.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/
- https://ai.google.dev/gemma/docs/capabilities/audio
- https://huggingface.co/docs/transformers/model_doc/gemma4

## 0.32. 2026-05-11 refresh: logprobs stay confidence-canary scoped

Current OpenAI Chat Completions docs expose `logprobs` and `top_logprobs` for
output-token probability receipts, with `top_logprobs` bounded to 0-20 entries
per token position. Current Ollama OpenAI-compatibility docs list logprobs as
a supported chat-completions feature. Current SGLang sampling docs expose the
same capability family through `return_logprob`, `top_logprobs_num`, and
related native sampling fields.

That makes logprob receipts useful for PalLLM validator/evaluator proof lanes:
they can show whether a route's chosen token was high-confidence, whether an
alternate token was close, and whether an escalation gate should ask a judge
model or deterministic fallback to intervene. It does not make logprobs a
normal player-chat default; payloads can grow quickly and local endpoints still
vary in exact OpenAI-compatible support.

Implementation impact: Pass 240 adds prompt-level `InferencePrompt.Logprobs`
and `InferencePrompt.TopLogprobs`. `HttpJsonInferenceClient` forwards
`logprobs` / `top_logprobs` only when a route-specific proof caller supplies
them and preserves returned choice-level `logprobs` JSON on
`InferenceResult.LogprobsJson`. Serving-profile guidance now requires accepted
request shape, returned logprob receipts, response-size impact, p95 latency,
and fallback counters before logprob receipts feed validators or evaluator
escalation.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced only the generic proof-receipt and confidence-escalation pattern.
PalLLM adopted no sibling code, prompts, names, branding, or product identity.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.ollama.com/api/openai-compatibility
- https://sgl-project.github.io/basic_usage/sampling_params.html

## 0.31. 2026-05-11 refresh: predicted-output stays proof-lane scoped

Current OpenAI Chat Completions docs expose a `prediction` request object for
static predicted output. The documented win is latency: when generated tokens
match supplied content, the response can return faster. That maps well to
PalLLM proof/docs regeneration lanes where most of the expected answer is a
stable scaffold and only a few live receipts change.

That does not make `prediction` a safe global chat default. Current local
OpenAI-compatible runtimes vary in how closely they track newer OpenAI fields,
and PalLLM's ordinary companion lane must stay portable across strict local
servers. The safe shape is therefore the same as structured-output and
tool-call canaries: a prompt-level hook that is omitted unless a route
intentionally qualifies it.

Implementation impact: Pass 239 adds `InferencePrompt.Prediction`.
`HttpJsonInferenceClient` forwards it as `prediction` only when supplied and
continues to omit it for normal companion chat. Serving-profile guidance now
requires accepted request shape, accepted/rejected prediction-token receipts
when exposed, p95 latency, and fallback counters before predicted-output
canaries are used on proof or docs lanes.

Sibling scan impact: actively updated sibling repos under `D:\Coding`
reinforced only the generic pattern of route-scoped proof hooks and receipts.
PalLLM adopted no sibling code, prompts, names, branding, or product identity.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.vllm.ai/en/v0.8.3/serving/openai_compatible_server.html
- https://sgl-project.github.io/basic_usage/sampling_params.html

## 0.30. 2026-05-11 refresh: tool-call proof needs request hooks and receipts

Current OpenAI Chat Completions docs expose `tools`, `tool_choice`, assistant
`tool_calls`, and finish reason `tool_calls`. Current vLLM tool-calling docs
support named function calling plus `auto`, `required`, and `none` tool-choice
modes, but still distinguish constrained named/required calls from parser-only
`auto` behavior. Current vLLM serving docs also state that
`parallel_tool_calls=false` constrains a request to zero or one tool call.
Current SGLang tool-parser docs support OpenAI-compatible tools and
`tool_choice`, with Xgrammar as the reliable tool-choice backend.

PalLLM already had `PalLLM:Inference:ParallelToolCalls` and serving-profile
proof language, but the text client did not have a prompt-level way to send
the actual `tools` array or `tool_choice` value, and tool-call-only assistant
messages with `content: null` were treated as unsupported response bodies.
That meant strict action/directive canaries could document a desired proof
lane but not execute it through PalLLM's own serializer/parser.

Implementation impact: Pass 238 adds route-scoped `InferencePrompt.Tools` and
`InferencePrompt.ToolChoice` JSON hooks. `HttpJsonInferenceClient` omits both
for ordinary companion chat, forwards them verbatim only when a caller supplies
them, and preserves returned `tool_calls` or legacy `function_call` payloads in
`InferenceResult.ToolCallsJson`. Tool-call-only responses are successful
transport receipts, but player-facing routes still need deterministic fallback
when there is no assistant text. Promotion proof now requires accepted request
shape, returned tool-call receipt, parse stability, p95 latency, fallback
counters, and no-spec strict-route evidence before any route trusts the lane.

Sibling scan impact: actively updated sibling repos under `D:\Coding` echoed
only the generic pattern of route-scoped tool contracts plus receipts before
promotion. PalLLM adopted no sibling code, prompts, names, branding, or product
identity.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.vllm.ai/en/latest/features/tool_calling/
- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://docs.sglang.io/docs/advanced_features/tool_parser

## 0.29. 2026-05-11 refresh: baseline sampler knobs need fail-fast bounds

Current OpenAI-compatible chat surfaces keep `temperature`, `top_p`, and
`presence_penalty` as common request controls, and current local runtimes
continue to expose those fields. vLLM documents top-p and presence-penalty
sampling on its OpenAI-compatible server surface, Ollama lists all three among
supported `/v1/chat/completions` fields, and SGLang documents temperature,
top-p, and presence penalty in its sampling parameter table and examples.

These are not new PalLLM features: they are the long-standing baseline sampler
knobs behind text chat and vision extraction. The production-readiness gap was
validation. A malformed `NaN`, infinite, negative top-p, or out-of-range
presence penalty would previously boot and fail later as repeated endpoint
errors on the player lane. The safer posture is to reject invalid baseline
sampler configuration at sidecar startup, before background workers and HTTP
routes begin serving.

Implementation impact: Pass 237 validates text `Temperature` in `[0, 2]`,
text `TopP` in `[0, 1]`, text `PresencePenalty` in `[-2, 2]`, and vision
`Temperature` in `[0, 2]`. Defaults remain unchanged, request serialization is
unchanged, deterministic fallback is unchanged, and endpoint-specific sampler
fields remain proof-gated.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.vllm.ai/en/v0.20.2/serving/openai_compatible_server/
- https://docs.ollama.com/api/openai-compatibility
- https://sgl-project.github.io/basic_usage/sampling_params.html

## 0.28. 2026-05-11 refresh: text structured-output hooks stay route-scoped

Current OpenAI Structured Outputs guidance recommends JSON-schema structured
outputs over legacy JSON mode when schema adherence matters. Current vLLM
structured-output docs show OpenAI-compatible chat-completions requests using
`response_format: { "type": "json_schema", ... }`; current SGLang structured
output docs show the same OpenAI-compatible field for JSON schemas and
separate grammar-style constraints; current Ollama OpenAI-compatibility docs
list `response_format` as a supported `/v1/chat/completions` request field.

That is useful for PalLLM's strict route canaries, proof packets, and future
tool/action lanes, but it is not a safe universal chat default. Older or
strict local endpoints can reject unknown fields, and structured decoding can
change latency or interact with reasoning/speculative settings. The safe
posture is a prompt-level hook, not a global config flip.

Implementation impact: Pass 236 adds `InferencePrompt.ResponseFormat` and
forwards it as `response_format` only when a caller supplies it. Ordinary text
chat still omits the field. GenAI telemetry marks those text calls as JSON
output so structured proof lanes are visible beside vision world-state
extraction. Promotion proof now requires accepted request shape, parse
stability, token usage, p95 latency, fallback counters, and no-spec
strict-route behavior before any route relies on the hook.

Primary sources:

- https://platform.openai.com/docs/guides/structured-outputs?api-mode=chat
- https://docs.vllm.ai/en/latest/features/structured_outputs/
- https://docs.sglang.io/docs/advanced_features/structured_outputs
- https://docs.ollama.com/api/openai-compatibility

## 0.27. 2026-05-11 refresh: token-budget fields are endpoint-specific

Current OpenAI Chat Completions docs use `max_completion_tokens` for newer
reasoning-token budgets. Current vLLM OpenAI-compatible docs also expose
`max_completion_tokens` and document that it maps to the same internal
sampling budget as `max_tokens`. Current Ollama OpenAI-compatibility docs and
SGLang OpenAI-compatible examples still commonly use `max_tokens`.

That split matters for PalLLM because a universal field swap would improve
compatibility for some reasoning-model lanes while breaking strict local
endpoints that still accept only `max_tokens`. The safe production posture is
therefore not a new default. Keep `max_tokens` as the broad local-runtime
default, expose `max_completion_tokens` as an explicit selector, and require
per-endpoint proof before any player-facing lane uses it.

Implementation impact: Pass 235 adds
`PalLLM:Inference:TokenBudgetField`, defaulting to `max_tokens` and accepting
only `max_tokens` or `max_completion_tokens`. `HttpJsonInferenceClient` emits
exactly one budget field per request, and `InferencePrompt.TokenBudgetField`
can override the configured selector for future route-specific canaries.
Startup validation rejects unknown names. Serving-profile and operator docs now
require accepted request-shape, generated-token, p95-latency,
fallback-counter, and replay-stability receipts before promotion.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.vllm.ai/en/v0.20.2/serving/openai_compatible_server/
- https://docs.ollama.com/api/openai-compatibility
- https://docs.sglang.ai/basic_usage/openai_api_completions.html

## 0.26. 2026-05-11 refresh: local sampler fields should stay proof-gated

Current vLLM OpenAI-compatible server docs expose extra sampling parameters
that can be supplied as request extras, including `top_k`, `min_p`, and
`repetition_penalty`. Current SGLang sampling docs expose the same sampler
family and document validation ranges for `min_p`, `top_k`, and
`repetition_penalty`.

These controls are useful for local PalLLM lanes because they can shape
creative companion diction, reduce repeated loops, or keep strict tool/proof
routes less noisy on a specific runtime. They are not safe universal defaults:
they are not standard OpenAI Chat Completions fields, strict endpoints can
reject them, and too-aggressive values can flatten replies or suppress useful
tactical repetition. PalLLM should therefore forward them only when explicitly
configured and after the exact endpoint/model accepts the request shape.

Sibling scan impact: external sibling research all use the same idea-level
pattern of explicit sampler stacks with proof before promotion. PalLLM adopted
only the generic pattern; no sibling code, branding, product identity, or
project-specific defaults were lifted.

Implementation impact: Pass 234 adds optional `PalLLM:Inference:TopK`,
`PalLLM:Inference:MinP`, and `PalLLM:Inference:RepetitionPenalty` plus internal
`InferencePrompt` overrides. `HttpJsonInferenceClient` omits `top_k`, `min_p`,
and `repetition_penalty` by default and forwards them only when configured or
overridden for a future route. Startup validation rejects `TopK` outside
`[1, 65536]`, `MinP` outside `[0, 1]`, and `RepetitionPenalty` outside `[0, 2]`
or non-finite float values. Serving-profile and operator docs now require
accepted request-shape, style/loop, parser-stability, token-count, p95-latency,
and fallback-counter receipts before promotion.

Primary sources:

- https://docs.vllm.ai/en/v0.20.2/serving/openai_compatible_server/
- https://sgl-project.github.io/basic_usage/sampling_params.html

## 0.25. 2026-05-10 refresh: frequency penalty should stay opt-in

Current OpenAI Chat Completions docs expose `frequency_penalty` as an optional
request field, bounded from `-2.0` to `2.0`, that reduces verbatim repetition.
Current local OpenAI-compatible serving surfaces keep the field available too.
Ollama's OpenAI-compatibility docs list `frequency_penalty` alongside
`presence_penalty`, `seed`, `stop`, `stream_options.include_usage`,
`temperature`, `top_p`, and `max_tokens` for `/v1/chat/completions`. Current
vLLM OpenAI-compatible docs also list `frequency_penalty` in the supported
sampling request parameters.

That makes frequency penalty useful for PalLLM companion turns that repeat
phrases during long local-model replies, but it is still not a safe blind
default. It changes model diction, can over-suppress ordinary repeated
tactical terms, and strict endpoints can reject fields outside their proven
schema. PalLLM should therefore forward it only when explicitly configured and
bounded to the OpenAI-compatible `-2` to `2` range.

Sibling scan impact: external sibling research both reinforce keeping per-family
sampling decisions explicit and evidence-backed instead of treating one model
family's profile as a universal runtime default. PalLLM adopted only that
generic proof-before-default pattern; no sibling code, branding, or product
identity was lifted.

Implementation impact: Pass 233 adds optional
`PalLLM:Inference:FrequencyPenalty` plus an internal
`InferencePrompt.FrequencyPenalty` override. `HttpJsonInferenceClient` omits
`frequency_penalty` by default and forwards it only when configured or
overridden for a future route. Startup validation rejects values outside
`[-2, 2]`. Serving-profile and operator docs now require repeated-phrase rate,
generated-token count, latency, and fallback-counter receipts before promoting
the knob to a player-facing default.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.ollama.com/api/openai-compatibility
- https://docs.vllm.ai/en/v0.20.2/serving/openai_compatible_server/

## 0.24. 2026-05-10 refresh: root release copy needs a broader runtime-brand gate

Current release hardening is not just endpoint correctness. Player-facing root
package copy should avoid implying affiliation with model/runtime vendors or
turning provider names into marketing copy. Steam's branding guidance keeps
the same general posture for platform marks: do not imply sponsorship,
endorsement, licensing, or affiliation. GitHub's current security docs also
keep publication preparation tied to preventive checks such as secret scanning
and push protection rather than post-release cleanup.

Sibling scan impact: RimLLM's public-copy policy now treats public docs,
support templates, and shipped text payloads as separate release surfaces and
blocks broad third-party model/runtime brand references. PalLLM adapted only
that generic surface-separation pattern; no sibling code, branding, or product
identity was lifted.

Implementation impact: Pass 232 broadens PalLLM's root-package and public-copy
brand scanner to cover current model/runtime/vendor names already blocked by
shareable pack validation. Root player-facing copy in `README.md`,
`docs/INDEX.md`, and generated `PLAYER_README.txt` now uses neutral
protocol/capability wording.

Primary sources:

- https://partner.steamgames.com/doc/marketing/branding
- https://docs.github.com/en/code-security/how-tos/secure-your-secrets/detect-secret-leaks

## 0.23. 2026-05-10 refresh: stop delimiters are useful but route-specific

Current OpenAI Chat Completions docs expose `stop` as up to four sequences
where generation stops and the stop text is omitted from the returned content.
Current Ollama OpenAI-compatibility docs list `stop` as a supported
`/v1/chat/completions` request field, while current vLLM docs position the
chat API as OpenAI-compatible and document adjacent stop controls such as
`stop_token_ids` / `include_stop_str_in_output`. Current llama.cpp server docs
also route `/v1/chat/completions` through the OpenAI-compatible chat surface.

That makes `stop` a useful PalLLM latency and strict-delimiter canary, but not
a safe default. Unsupported strict endpoints can reject unknown fields, and
over-broad delimiters can clip useful companion text. The field therefore
belongs behind explicit operator config and route replay evidence.

Sibling scan impact: an external asset-generation sibling reinforced proof receipts for latency changes,
RimLLM reinforced backend-neutral player copy, and BYTE reinforced explicit
budget/latency receipts. PalLLM used only those generic patterns; no sibling
code, branding, or product identity was lifted.

Implementation impact: Pass 231 adds optional
`PalLLM:Inference:StopSequences[]` plus an internal
`InferencePrompt.StopSequences` override. `HttpJsonInferenceClient` omits
`stop` by default, forwards a trimmed JSON array only when configured, and
allows a prompt-level empty list to suppress configured stops for a specific
future lane. Startup validation caps the list at four non-empty, unique,
128-character-or-shorter entries.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://help.openai.com/en/articles/5072263
- https://docs.ollama.com/openai
- https://docs.vllm.ai/en/stable/serving/openai_compatible_server/
- https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md

## 0.22. 2026-05-10 refresh: foreground priority is endpoint-specific

Current vLLM OpenAI-compatible docs expose a `priority` request field on
chat-completions-style requests. Current scheduler docs keep the operational
meaning explicit: `fcfs` handles requests by arrival order, while `priority`
handles lower numeric priority values earlier, with arrival time breaking ties.
The same vLLM request docs warn that non-zero priority values can error when
the served model is not using priority scheduling. That makes request priority
useful for PalLLM's player-facing companion lane, but only as an opt-in tied to
the exact endpoint launch flags.

Sibling scan impact: an action-RPG sibling runtime had the directly relevant pattern: model routes
distinguish foreground and background work, and its optimizer notes lower
priority values for vLLM/SGLang-style lower-value-first scheduling. PalLLM used
only that generic scheduling pattern; no sibling code, branding, or product
identity was lifted.

Implementation impact: Pass 229 adds optional
`PalLLM:Inference:RequestPriority` plus `InferencePrompt.RequestPriority`.
`HttpJsonInferenceClient` omits `priority` by default and forwards the
configured or prompt value only when present. Serving-profile guidance now
requires `--scheduling-policy priority`, mixed short-companion / long-proof
replay, queue-time evidence, and no starvation of background proof/docs lanes
before using request priority as a player-facing default.

Primary sources:

- https://docs.vllm.ai/en/latest/serving/openai_compatible_server/
- https://docs.vllm.ai/en/latest/api/vllm/config/scheduler/

## 0.21. 2026-05-10 refresh: fingerprint receipts complete seeded replay

Current OpenAI Chat Completions response docs still expose
`system_fingerprint` as backend-configuration evidence that can be paired with
`seed` to understand determinism drift. Current vLLM and LM Studio docs both
position their servers as OpenAI-compatible chat-completions endpoints, while
OpenTelemetry GenAI span conventions recommend response id/model/finish-reason
and usage attributes, not provider-specific fingerprint tags. That makes the
fingerprint useful as PalLLM-side proof metadata rather than a mandatory OTel
tag.

Sibling scan impact: external sibling research all reinforce the
same generic pattern: replay claims should carry compact receipts, latency
evidence, and proof artifacts instead of prose. PalLLM used only that pattern;
no sibling code, branding, or product identity was lifted.

Implementation impact: Pass 228 parses optional `system_fingerprint` from
successful chat-completions responses, preserves it on `InferenceResult`, and
surfaces the latest value per recent lane as
`/api/inference/performance -> Lanes[].LastSystemFingerprint`. Missing or
non-string values remain empty so strict local endpoints and deterministic
fallback behavior are unchanged.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.vllm.ai/en/stable/serving/openai_compatible_server/
- https://lmstudio.ai/docs/developer/openai-compat
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-spans/

## 0.20. 2026-05-10 refresh: seed is useful only with replay receipts

Current OpenAI Chat Completions reference material still treats `seed` as a
best-effort determinism hint, paired with `system_fingerprint` so callers can
notice backend changes that may affect reproducibility. Current vLLM protocol
source also accepts `seed` on chat completions, while its serving docs warn
that model-repository `generation_config.json` can override sampling defaults
unless the server is launched with `--generation-config vllm`. That makes seed
useful for PalLLM proof replays, but unsafe as a blind default: deterministic
claims depend on the served model id, runtime version, replica/parallelism
layout, sampling defaults, and whether the endpoint even accepts the field.

Sibling scan impact: BYTE and RimLLM continue to reinforce replay-safe proof
packets, deterministic-first fallbacks, and artifact-backed validation. PalLLM
used that only as a local proof pattern; no sibling code, branding, or product
identity was lifted.

Implementation impact: Pass 227 adds optional `PalLLM:Inference:Seed` and an
internal `InferencePrompt.Seed` override. `HttpJsonInferenceClient` omits
`seed` by default, forwards the configured or prompt seed only when present,
and keeps deterministic fallback unchanged. Operator docs now require replay
receipts with seed, served model id, runtime version, system fingerprint when
exposed, replica layout, and output drift before treating seeded runs as proof.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create
- https://docs.vllm.ai/en/v0.20.1/api/vllm/entrypoints/openai/chat_completion/protocol/
- https://docs.vllm.ai/en/v0.18.2/serving/openai_compatible_server/

## 0.19. 2026-05-10 refresh: reasoning effort is useful but endpoint-specific

Current chat-completions-compatible reasoning models expose a
`reasoning_effort` request hint in more than one server family, but the
accepted value set and behavior are endpoint-specific. OpenAI-compatible
documentation uses low/medium/high-style effort controls for reasoning
models; Mistral documents `reasoning_effort` for selected chat-completions
models, including a `none` mode that omits the thinking chunk; current vLLM
protocol source accepts a wider server-side allowlist including `none`,
`minimal`, `xhigh`, and `max`. That makes blind default forwarding wrong for
PalLLM: unsupported endpoints may reject unknown fields with 400s, and higher
effort can raise TTFT, token usage, heat, and fallback pressure.

Implementation impact: Pass 226 adds `PalLLM:Inference:ReasoningEffort` as an
explicit opt-in pass-through. The default remains `null`, so shipped local-first
behavior and deterministic fallback paths do not change. Startup validation
accepts only the known effort spellings (`none`, `minimal`, `low`, `medium`,
`high`, `xhigh`, `max`), request serialization trims/lowercases the value, and
operator docs require a per-server/model probe before promotion.

Primary sources:

- https://platform.openai.com/docs/api-reference/chat/create-chat-completion
- https://docs.mistral.ai/studio-api/conversations/reasoning/adjustable
- https://github.com/vllm-project/vllm/blob/main/vllm/entrypoints/openai/chat_completion/protocol.py

## 0.18. 2026-05-10 refresh: usage receipts need parser compatibility

Current OpenTelemetry GenAI guidance recommends recording token-usage metrics
only when the count is readily available. That makes parser compatibility part
of the telemetry contract: if a local OpenAI-compatible runtime or gateway
returns valid token counters but serializes them as JSON strings, dropping those
counts is worse than accepting them conservatively. vLLM's metrics design also
keeps model-server interval accounting low-overhead and outside the engine hot
loop where possible; PalLLM should follow the same rule at the sidecar boundary
by doing small bounded JSON parsing, not offline token counting.

Implementation impact: Pass 225 updates the shared chat-completions response
reader so `usage.prompt_tokens`, `usage.completion_tokens`, and
`usage.total_tokens` accept either JSON numbers or invariant-culture integer
strings. Invalid, negative, missing, or overflowed values clamp to `0`, and an
invalid/missing total falls back to valid prompt + completion counts. That keeps
valid model-usage proof visible while preserving the "do not report what you
cannot obtain cheaply" posture.

Primary sources:

- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/
- https://docs.vllm.ai/en/latest/design/metrics/

## 0.17. 2026-05-10 refresh: already-started streams need stream-native timeouts

Current ASP.NET Core request-timeout guidance is clear that timeout middleware
uses request cancellation and lets the app produce a response when it handles
the cancellation. That works well for one-shot JSON endpoints whose status code
has not been sent yet. For SSE, PalLLM intentionally flushes `200
text/event-stream` immediately so dashboards and clients can see `started` and
`phase` events. Once those headers are on the wire, a late `503 ProblemDetails`
would be a false contract; the stream needs an in-band timeout event instead.

Current OpenTelemetry GenAI metrics also separate whole-operation duration from
streaming latency (`time_to_first_chunk`, `time_per_output_chunk`), and serving
runtimes expose first-token, latency, queue, and cache signals. vLLM exposes
request, prefix-cache, and KV-cache residency metrics; SGLang exposes
time-to-first-token, end-to-end latency, running/queued request, token-usage,
and throughput metrics. That reinforces the same design rule for PalLLM: a
progress stream should be low-latency, but the expensive final-work lane still
needs a bounded budget and a machine-readable timeout reason.

Sibling scan impact: RimLLM's current changelog and research notes reinforced
linked timeout tokens and bounded streaming/body phases. an action-RPG sibling runtime reinforced
that progress streams should not keep mutating or spinning after the relevant
work is canceled. BYTE reinforced "stream progress early" as a UX pattern, but
PalLLM kept the implementation scoped to Palworld chat SSE. No sibling code,
branding, or project identity was lifted.

Implementation impact: Pass 222 keeps `/api/chat/stream`'s early SSE progress
events but runs `PalLlmRuntime.ChatAsync` under
`PalLLM:Http:ChatRequestTimeoutSeconds` using a linked cancellation token. If
that budget expires before the final reply, the route emits sanitized
`event: error` with `reason=request_timeout`, does not emit `final`, and
releases the chat concurrency slot. One-shot chat-class routes keep their
existing ASP.NET Core request-timeout `503 ProblemDetails` behavior.

Primary sources:

- https://learn.microsoft.com/aspnet/core/performance/timeouts
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/
- https://docs.vllm.ai/en/latest/design/metrics/
- https://docs.sglang.io/references/production_metrics.html

## 0.11. 2026-05-10 refresh: request bodies need an outer budget

Current ASP.NET Core guidance keeps request size as a server or endpoint
resource-control concern, not just a model-validation concern. The request
decompression docs also make the security boundary explicit: decompressed
bodies are constrained by the same request-body limit, which is what prevents
compressed or streamed payloads from bypassing ordinary field validation.
OWASP API4 continues to recommend maximum sizes for incoming parameters and
payloads as part of unrestricted resource-consumption defense.

Sibling scan impact: an external project's hardening notes independently call out both
declared `Content-Length` and streamed/chunked request-body caps. That
reinforced the pattern, but no code or project identity was lifted.

Implementation impact: Pass 212 added `PalLLM:Http:ApiRequestBodyMaxBytes`
(`10 MiB` default) and applies it to `/api/*` plus `/mcp` before minimal-API
model binding. Declared oversized bodies return sanitized `413 ProblemDetails`;
streamed bodies get `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` set
before endpoint code reads. Field validators still carry the narrower semantic
budgets after binding.

Primary sources:

- https://learn.microsoft.com/aspnet/core/fundamentals/middleware/request-decompression
- https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestbodysize
- https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/

## 0.12. 2026-05-10 refresh: model capabilities and artifacts need provenance

Current Hugging Face model-card guidance treats metadata such as license,
base-model relation, datasets, library, and evaluation source as part of the
model's reproducibility and sharing surface. The Hub's pickle-scanning and TGI
safety docs keep the security boundary concrete: pickle can execute code while
loading, safetensors is the safer data-only format, and `trust_remote_code`
communicates a higher level of trust in the model provider. Current vLLM serve
arguments also expose the exact controls PalLLM needs to record: model
`--revision`, `--code-revision`, `--tokenizer-revision`,
`--trust-remote-code`, and local-media allowlists.

Current serving docs also separate advertised capability from usable local
capability. vLLM tool calling still depends on the selected parser/template and
warns that generated arguments can be malformed. vLLM-Omni exposes local
audio/video/image examples and realtime speech events, but those are served by a
specific runtime process with specific flags. Google Gemma audio guidance names
the concrete audio preprocessing contract for Gemma 3n rather than a generic
family promise. That maps to one PalLLM rule: a model family name is a hint, not
promotion evidence.

Sibling scan impact: external sibling research both reinforced machine-readable
publication hygiene and model-artifact provenance receipts. an action-RPG sibling runtime also
reinforced runtime capability handshake receipts: runtime version, model-card
capability ids, launch flags, and media canaries must travel together. PalLLM
adopted the idea only as PalLLM-specific serving-profile guidance; no sibling
code, branding, or project identity was lifted.

Implementation impact: Pass 216 extends `Capability.ServingProfile` so every
model-serving lane asks for a primary-source capability receipt and a
model-artifact provenance receipt before promotion or redistribution. The
capability receipt names model-card or vendor-doc revision, served model id from
`/v1/models` or a provider-native catalog, runtime version, launch flags,
positive canaries for claimed text/image/video/audio/tool/speculation support,
and negative canaries for unsupported modalities. The artifact receipt names
source URL or local path, immutable revision/commit or SHA-256, model-card
license metadata, base-model/adapter relation, weight format, safetensors/pickle
and `trust_remote_code` status, runtime/tokenizer revisions, and whether
redistribution is allowed. This does not change PalLLM defaults or add a new
runtime dependency; it tightens release evidence for local model choices.

Primary sources:

- https://huggingface.co/docs/hub/en/model-cards
- https://huggingface.co/docs/hub/main/security-pickle
- https://huggingface.co/docs/text-generation-inference/basic_tutorials/safety
- https://docs.vllm.ai/en/latest/cli/serve/
- https://docs.vllm.ai/en/latest/features/tool_calling/
- https://docs.vllm.ai/projects/vllm-omni/en/latest/user_guide/examples/online_serving/qwen3_omni/
- https://ai.google.dev/gemma/docs/capabilities/audio

## 0.13. 2026-05-10 refresh: release proof must be corroborated

Current ASP.NET Core guidance still treats expensive routes as explicit,
bounded operations: timeout middleware is opt-in per app/endpoint and exposes
cancellation through `HttpContext.RequestAborted`, so PalLLM should continue
preferring bounded proof/readiness checks over long opaque waits. Current
serving docs reinforce the same evidence posture on the model side: vLLM,
SGLang, llama.cpp, and `transformers serve` all expose runtime metrics or
readiness surfaces that should be recorded before promotion instead of trusting
operator prose. Google Gemma audio docs similarly tie audio capability to a
specific preprocessing/runtime contract, not a generic family name.

Sibling scan impact: RimLLM's latest handoff reinforced external-validation
packets and "trust artifacts over narrative"; an external asset-generation sibling reinforced runtime
qualification/confidence/gate artifacts; an action-RPG sibling runtime reinforced
machine-readable readiness artifacts. No sibling code, identity, or branding
was lifted.

Implementation impact: Pass 218 hardens PalLLM's `NativeProofEvidence` reader.
A readable `latest-native-proof.json` that claims `Status = "proven"` is now
downgraded to `invalid` unless the same artifact also carries
`BridgeProofStatus = "delivery_proven"`, `LiveDeliveryProven = true`, and
`NativeHudBindReady = true`. The human next-pass copy and
`Publication.NextRecommendedCommand` both route the operator back to
`scripts/run-native-proof.ps1`.

Primary sources:

- https://learn.microsoft.com/aspnet/core/performance/timeouts
- https://docs.vllm.ai/en/latest/usage/metrics/
- https://docs.sglang.io/docs/references/production_metrics
- https://docs.sglang.io/advanced_features/router.html
- https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md
- https://huggingface.co/docs/transformers/main/serve-cli/serving_optims
- https://ai.google.dev/gemma/docs/capabilities/audio

## 0.14. 2026-05-10 refresh: promotion receipts are not metrics

Current serving guidance keeps operational telemetry and promotion evidence
adjacent but distinct. vLLM and SGLang expose concrete Prometheus metrics for
queue, latency, cache, and request pressure; Hugging Face model-card and Hub
security guidance covers license/provenance/trust boundaries; ASP.NET Core
request-timeout guidance keeps expensive operations bounded and endpoint
specific. Treating all of those as one "metric receipt" list made automation
parse prose to decide whether it had a real metric, a provenance record, a
security canary, or a release/distribution decision.

Sibling scan impact: RimLLM's handoff reinforced external-validation packets
as first-class artifacts; BYTE reinforced capability handshakes and event-bridge
recovery evidence. PalLLM adopted the pattern only as a PalLLM-specific
serving-profile field; no sibling code, branding, or identity was lifted.

Implementation impact: Pass 219 adds
`Capability.ServingProfile.PromotionReceipts[]` and wires it through
`pal models serving`. `MetricReceipts[]` remains for compatibility and for
metric/log families, while `PromotionReceipts[]` names route-labeled replay,
runtime capability handshakes, model-artifact provenance, package
redistribution decisions, GGUF prompt/state-cache canaries, vLLM
scheduler/cache proof, SGLang sanitized replay proof, transformers serve /
Foundry Local / OpenVINO / TensorRT-LLM readiness proof, speculation A/B proof,
multimodal media-admission proof, and audio/realtime fallback proof.

Primary sources:

- https://learn.microsoft.com/aspnet/core/performance/timeouts
- https://docs.vllm.ai/en/latest/usage/metrics/
- https://docs.sglang.ai/references/production_metrics.html
- https://huggingface.co/docs/hub/en/model-cards
- https://huggingface.co/docs/hub/main/security-pickle

## 0.17. 2026-05-10 refresh: Gemma 4 audio budget correction

Current Google Gemma audio guidance says Gemma 4 accepts text, audio, and image
input, and budgets audio at `25` tokens per second. Gemma 3n remains cheaper at
`6.25` tokens per second. A 30-second proof clip is therefore about `750`
Gemma 4 audio tokens, or about `188` Gemma 3n audio tokens, before the text
prompt and output headroom are counted.

Implementation impact: Pass 223 updates `Capability.ServingProfile` so all
Gemma 4 lanes are treated as proof-gated native audio-in candidates instead of
keeping 26B/31B-style names text/image/video-only. The planner now emits
family-specific Gemma audio budget receipts, request hints, admission controls,
and promotion checks. Player speech still stays typed text or cascaded ASR
until privacy, latency, exact runtime canary, and deterministic fallback
evidence exist.

Primary source:

- https://ai.google.dev/gemma/docs/capabilities/audio

## 0.16. 2026-05-10 refresh: context identity and audio-token budgets

Current Qwen3.6 model cards advertise 262,144-token native context and explicit
1,010,000-token extension recipes. PalLLM should therefore treat native,
extended, hosted, and reduced-context GGUF lanes as separate proof identities
instead of copying a family-level context claim across all servers. The receipt
needs served model id, source, runtime context cap, extension flags, route token
budget, KV/state memory, p95 latency, exact parse success, and fallback
counters.

Current Google Gemma audio guidance documents mono-channel 16 kHz float32
processing, a 30-second recommended clip length, and family-specific token
costs. PalLLM should record the normalized duration and token estimate beside
the clip hash before promoting native audio-in. Pass 223 supersedes this pass's
older single-rate guidance: Gemma 4 is `25` audio tokens/sec, while Gemma 3n is
`6.25` audio tokens/sec.

Sibling scan impact: external sibling research all reinforced
artifact-backed capability receipts and media/proof validation. PalLLM used the
pattern only as PalLLM serving-profile receipts; no sibling code, branding, or
project identity was lifted.

Implementation impact: Pass 221 adds Qwen3.6 context-identity startup,
request, cache, admission, promotion, and verification guardrails to
`Capability.ServingProfile`. The same pass added Gemma audio-token budget
startup, request, cache, admission, promotion, and verification guardrails; the
Pass 223 correction made those guardrails family-specific and expanded Gemma 4
audio-in proof to the current model-card surface.

Primary sources:

- https://huggingface.co/Qwen/Qwen3.6-35B-A3B
- https://huggingface.co/Qwen/Qwen3.6-27B
- https://ai.google.dev/gemma/docs/capabilities/audio
- https://docs.vllm.ai/en/latest/design/metrics/
- https://docs.sglang.io/references/production_metrics.html
- https://opentelemetry.io/docs/specs/semconv/gen-ai/gen-ai-metrics/

## 0.15. 2026-05-10 refresh: portable proof bundles need full manifest agreement

Current Microsoft ZipArchive documentation exposes each entry's full relative
path and uncompressed size, which keeps PalLLM's existing "inspect without
extracting" posture appropriate for proof/support bundles. OWASP API4 resource
consumption guidance reinforces the same boundary: local release artifacts are
still untrusted inputs when an API surface reads them, so PalLLM should keep
manifest sizes bounded and validate server-side before treating a portable
bundle as release evidence.

Current model-serving docs were also rechecked during this pass. vLLM and
SGLang continue to expose concrete metrics/replay surfaces, Hugging Face still
recommends safetensors as the safer tensor format, and Google Gemma audio docs
tie audio claims to exact preprocessing/runtime contracts. No new serving
default was promoted because PalLLM still needs route-specific replay and
fallback receipts before making model/runtime optimizations defaults.

Sibling scan impact: RimLLM, external sibling research all reinforced
artifact-backed release handoffs and runtime qualification receipts. PalLLM
used that only as a local release-readiness hardening pattern; no sibling code,
branding, or project identity was lifted.

Implementation impact: Pass 220 extends the proof/support bundle archive
inspection so the embedded bundle manifest must match the latest
sidecar-readable manifest for promotion-relevant status, native-HUD config,
optional-file, blocker, and ready-evidence fields. A proof or support zip with
a stale embedded manifest now stays `invalid` even if the latest `.json` file
beside it was edited or regenerated.

Primary sources:

- https://learn.microsoft.com/dotnet/api/system.io.compression.ziparchiveentry
- https://owasp.org/API-Security/editions/2023/en/0xa4-unrestricted-resource-consumption/
- https://docs.vllm.ai/en/v0.12.0/design/metrics/
- https://docs.sglang.io/docs/advanced_features/observability
- https://huggingface.co/docs/safetensors/index
- https://ai.google.dev/gemma/docs/capabilities/audio

## 0. 2026-05-08 refresh: pool only after proof

Current primary-source checks reinforced the same PalLLM production rule from
the previous passes: multi-worker model serving is not automatically lower
latency for a single-player companion. It only becomes a player default after
admission control, cache behavior, and fallback behavior are measured on PalLLM
replays.

- ASP.NET Core 10 rate-limiting guidance still frames endpoint policies as a
  resource-protection layer; PalLLM's heavy lanes should keep small queues,
  explicit 429s, and measured budgets instead of silently queueing behind long
  model work.
- OWASP API4 still points at maximum sizes for parameters, payloads, and other
  caller-controlled work. That supports PalLLM's non-configurable caps on user
  text, memory snippets, evidence lists, image payloads, and upstream response
  bodies.
- vLLM's current optimization docs call out multimodal processor caching and
  memory placement. For PalLLM, repeated screenshots or proof clips should
  prove media cache hits separately from text prefix/KV cache.
- SGLang Model Gateway is the most relevant new multi-worker idea for this
  pass: it combines retries with jitter, worker-scoped circuit breakers,
  token-bucket rate limiting with queuing, background health checks, request
  observability, and cache-aware load monitoring. PalLLM now surfaces those
  requirements through `Capability.ServingProfile` so
  `/api/inference/collaboration` and `pal models serving -Json` can warn
  operators before they promote a pool.

Implementation impact: Pass 194 did not add a new model-serving dependency.
It updated the deterministic model-collaboration planner so SGLang gateway
promotion requires receipts for retry counts, circuit-breaker transitions,
queue depth, cache-aware routing hit rate, TTFT/ITL, p95 companion latency, and
fallback activation. The default single-player lane remains loopback, bounded,
and deterministic-fallback-first.

## 0.1. 2026-05-08 refresh: multimodal speculation is route-scoped

Current vLLM docs now expose both Gemma 4 MTP speculative decoding and rich
multimodal controls such as media UUIDs, `limit_mm_per_prompt`, and
precomputed image/audio embeddings. That makes it tempting to turn on a model
family's fastest speculative mode globally.

The PalLLM rule is narrower: text-only MTP or n-gram wins do not qualify
Palworld screenshot, video, or player-audio routes. MMSpec's March 2026
benchmark reports that text-only speculative methods can degrade in
vision-language scenarios and that throughput alone is not a reliable latency
proxy. PalLLM therefore treats speculation evidence as route-scoped:

- text companion chat proves text companion chat only
- screenshot/image replay proves screenshot/image replay only
- video summary replay proves video summary replay only
- audio-in or ASR replay proves audio-in or ASR replay only

Implementation impact: Pass 195 added modality-isolated speculation guidance to
`ModelCollaborationPlanner`. Capability profiles now tell operators to keep
speculation disabled on media routes until each modality has no-spec, n-gram,
and model-native speculation comparisons with p95 latency, parse stability, and
fallback activation evidence.

## 0.2. 2026-05-08 refresh: speculation modes are not one bit

Current vLLM Qwen3.5 / Qwen3.6 recipes now separate the low-concurrency
latency profile from the normal prefix-cache profile: MTP-1 can reduce
time-per-output-token at low concurrency, but the recipe disables prefix
caching for that latency pass and warns that throughput can drop under load.
Gemma 4 recipes make a related measurement point: disable prefix caching for
consistent MTP benchmarks, then choose the serving cache profile after replay.

Implementation impact: Pass 196 added `Capability.Speculation` to
`ModelCollaborationPlanner` so clients can distinguish n-gram speculation,
draft-model speculation, and model-native MTP. Qwen3.6 lanes now surface
`RequiresPrefixCacheOffForLatencyMtp=true` and a
`mtp-1-low-concurrency-prefix-cache-off` first-mode recommendation instead of
forcing operators to infer that nuance from prose. The normal PalLLM prefix
cache guidance stays in place for prompt-heavy, shared, or docs-sync lanes.

## 0.3. 2026-05-08 refresh: scheduler budgets protect live turns

Current vLLM serve arguments make scheduler budgeting explicit:
`--max-num-batched-tokens` caps tokens processed per scheduler iteration,
`--max-num-seqs` caps sequences per iteration, and chunked-prefill settings can
let shorter prompts jump ahead of longer prompts when
`--max-long-partial-prefills` is below `--max-num-partial-prefills`.

Implementation impact: Pass 197 added these scheduler knobs to
`Capability.ServingProfile` so `GET /api/inference/collaboration` and
`pal models serving -Json` no longer imply that a single vLLM server can safely
mix live companion chat with long proof/docs prompts just because prefix cache
or chunked prefill is enabled. Promotion now asks for an A/B replay with one
short PalLLM companion turn queued beside one long proof/docs prompt before a
shared server profile is trusted.

## 0.4. 2026-05-08 refresh: image payloads are admitted before model I/O

Current primary-source checks reinforced that multimodal convenience should not
weaken PalLLM's local-first resource boundary. OWASP API4 still recommends
maximum sizes for all incoming parameters and payloads. OpenAI-compatible vision
servers accept base64 data URLs inside `image_url` content parts, while current
vLLM multimodal docs make media-count limits explicit through
`limit_mm_per_prompt` and show base64 image content as a normal path.

Implementation impact: Pass 198 added a shared base64 payload inspector before
PalLLM builds a vision data URL or forwards media to a model server. HTTP
validation and direct/MCP vision callers now agree on malformed-base64 and
decoded-size failures, so bad image text fails locally instead of consuming a
provider request.

## 0.5. 2026-05-09 refresh: route-level serving proof

Current primary-source checks reinforce that model-serving wins need the same
route shape as the workload they are meant to accelerate:

- ASP.NET Core request-timeout guidance applies budgets selectively per
  endpoint and does not assume every request has the same acceptable duration.
- vLLM exposes queue, KV-pressure, prefix-cache, and latency metrics; its
  optimization docs still warn that KV pressure can trigger preemption and
  recomputation. That makes "the model was fast once" weaker evidence than
  route-labeled p50/p95, TTFT/ITL, queue, preemption, and fallback receipts.
- SGLang Model Gateway advertises request-id propagation, queue stats,
  Prometheus metrics, retries, circuit breakers, and cache-aware load
  monitoring, which only help PalLLM when replay evidence preserves the route
  being promoted.
- NVIDIA Dynamo's multimodal KV routing docs route repeated image requests by
  image-aware KV overlap, separate from encoder-cache reuse. For PalLLM,
  repeated screenshot routing proof is therefore distinct from text-prefix or
  encoder-cache proof.

Implementation impact: Pass 203 extends `Capability.ServingProfile` guidance
without changing any runtime dependency or default inference behavior.
`RequestHints[]`, `CacheHints[]`, `MetricReceipts[]`, and
`VerificationChecks[]` asked operators to keep companion chat, vision
describe, world-state extraction, screenshot proof loops, audio/ASR, and long
proof/docs replays as separate promotion receipts. A cache, speculation,
scheduler, or router win on one route class no longer counts as a win for the
others.

## 0.6. 2026-05-09 refresh: GGUF state-cache canary

Current llama.cpp server docs expose both monitoring metrics and slot
save/restore endpoints for prompt cache persistence. Current llama.cpp
development discussion also shows a live SWA failure class where Gemma 4 and
other SWA models could force full prompt re-processing even when the operator
expected prompt-cache reuse. The useful PalLLM rule is not "turn host prompt
cache off"; it is "prove host prompt-cache restore on the exact model family,
server build, chat template, context size, adapter set, and slot policy before
promotion."

Implementation impact: Pass 204 adds state-cache canary guidance to
`Capability.ServingProfile`. llama.cpp / GGUF lanes now ask for a same-slot
second-turn replay, slot save/restore timing, model/tokenizer/chat-template/
context/adapter/server-build receipts, and log review for unexpected full
prefill before host prompt-cache persistence is promoted. Changed templates,
context sizes, adapters, model files, or server builds should invalidate the
cache instead of reusing stale state. This is proof guidance only; PalLLM's
deterministic fallback and default inference behavior are unchanged.

## 0.7. 2026-05-09 refresh: replayable cache evidence

Current vLLM metrics docs expose sampled KV-block residency histograms for
cache lifetime, idle-before-evict, and reuse gaps. Current SGLang observability
docs expose request dump/replay and crash-dump replay paths in addition to
Prometheus metrics. The useful PalLLM rule is to promote cache or routing
settings only when the exact PalLLM route can be replayed and the evidence is
safe to hand off.

Implementation impact: Pass 205 adds two guardrails to
`Capability.ServingProfile`. vLLM-like lanes now ask operators to enable
`--kv-cache-metrics-sample` only during qualification and reject cache-policy
promotion when proof/docs prompts strand KV blocks or evict the live companion
prefix. SGLang lanes now ask for local request dump/replay or crash-dump replay
receipts while keeping raw dumps out of public/support bundles, since those
artifacts may contain player or pack text. The same pass aligns pack
publication-safety IP blocking with the release-facing public-copy scanner.

## 0.8. 2026-05-09 refresh: reproducible sampling + single-tool proof

Current vLLM OpenAI-compatible server docs call out two promotion details that
matter for PalLLM action and replay lanes:

- the server applies a model repository's `generation_config.json` by default;
  use `--generation-config vllm` when PalLLM's own temperature/top-p and replay
  settings must be the comparison baseline
- `parallel_tool_calls=false` is the documented request-level way to ensure a
  chat-completions response returns zero or one tool call

Implementation impact: Pass 206 adds these two checks to
`Capability.ServingProfile`. vLLM-like lanes now tell operators to record
whether `--generation-config vllm` is active during deterministic replay, and
tool-call-capable vLLM lanes must prove `parallel_tool_calls=false` on strict
directive/action routes before any parallel fan-out experiment is allowed. The
same pass also makes `pal proof` freshness-aware: stale durable native proof is
reported as `STALE PROOF`, and `-RequireProven` no longer passes on an expired
artifact.

Pass 230 turns that proof guidance into an explicit opt-in request field:
`PalLLM:Inference:ParallelToolCalls` and `InferencePrompt.ParallelToolCalls`
are omitted by default, but when set they forward OpenAI-compatible
`parallel_tool_calls` so strict directive/action canaries can prove
zero-or-one tool-call behavior on the exact endpoint/model.

## 0.9. 2026-05-09 refresh: hybrid-state and audio-family truth

Current SGLang Qwen3.6 docs describe the 35B-A3B and 27B variants as hybrid
Gated DeltaNet / Mamba-style serving lanes with scheduler strategy and page-size
knobs, long context, MTP, tool calling, and text/image/video input. That means
PalLLM should not treat a Qwen3.6 cache or scheduler win as ordinary
transformer KV-cache proof only: the receipt also needs runtime version,
attention backend, scheduler strategy, state memory, TTFT/ITL, exact parse
success, and fallback behavior.

Earlier Google Gemma 4 docs were read as splitting audio capability by size;
current Gemma audio guidance now documents Gemma 4 audio input across the
listed Gemma 4 model ids, with exact runtime proof still required.

Implementation impact: Pass 207 added Qwen3.6 hybrid-GDN state receipts to
`Capability.ServingProfile`; Pass 223 supersedes the Gemma 4 non-audio-in
piece. Player speech remains typed text or cascaded ASR unless an exact Gemma
4, Qwen Omni, or ASR lane is explicitly proven.

## 0.10. 2026-05-09 refresh: remote media is SSRF-sensitive

Current vLLM multimodal docs explicitly recommend constraining
`--allowed-media-domains` and disabling media redirects when model servers fetch
remote media. That guidance maps directly to PalLLM's local-first posture:
normal screenshot, video, and audio lanes should use local bytes or local file
paths, while remote `image_url`, `audio_url`, or `video_url` profiles are
operator opt-ins.

Implementation impact: Pass 211 adds remote-media admission receipts to
`Capability.ServingProfile`. vLLM-style multimodal lanes now ask for local data
URL acceptance, remote-URL-disabled-by-default proof, allowed-domain fetch
evidence, blocked redirect evidence, and negative localhost/private/link-local
URL probes before a URL-fetching lane can become player-facing. The same pass
extends publication-safety regexes so release surfaces, package/proof/support
text scans, and shareable pack validators catch both the plain and accented
spellings of the common off-scope comparison term via an ASCII regex escape.

## 0.10. 2026-05-09 refresh: bounded local voice references

Current primary-source checks put the same resource boundary on future voice
work that PalLLM already applies to image ingress and upstream response bodies:

- vLLM-Omni's current Speech API caps uploaded voice samples at `10MB` and
  accepts common local audio containers such as WAV, MP3, FLAC, OGG, AAC, WEBM,
  and MP4 for voice creation.
- vLLM-Omni's Qwen3-Omni examples accept local audio files in MP3, WAV, OGG,
  FLAC, and M4A, while realtime audio examples require mono 16-bit PCM at
  16 kHz.
- Google's Gemma audio guidance keeps clips to a maximum of 30 seconds and
  documents mono 16 kHz processing as the practical target for custom audio
  encoding.
- OWASP API4 still recommends explicit maximum sizes for every caller-
  controlled payload, including files that are stored locally.

Implementation impact: Pass 208 tightens `PersonalityPackValidator` before
runtime voice cloning or ASR consumes pack assets. `VoiceRefPath` now accepts
the common local speech containers PalLLM can reasonably hand to current local
audio stacks (`.wav`, `.mp3`, `.flac`, `.ogg`, `.opus`, `.m4a`, `.aac`), and
both `VoiceRefPath` and `AudioSamples[]` fail validation above `10 MiB` per
file. The hash algorithm stays streaming and local; oversized voice clips never
become valid pack assets just because the content hash matches.

## 0.11. 2026-05-21 refresh: voice-reference provenance

Follow-up sibling-project audit found one useful publish-safety pattern that
fits PalLLM without importing code or branding: every shareable voice
reference should carry a short provenance category before a runtime voice
lane consumes it. Implementation impact: `VoiceRefPath` now requires
`VoiceConsent` with one of `self_recorded`, `licensed`, `synthetic`, or
`public_domain`; `VoiceConsentNotes` can carry a short reviewer note. This is
pack-level metadata only. Runtime voice-clone dispatch still stays opt-in and
requires its own future consent-token / revocation proof before it becomes a
player-facing path.

## 1. OpenAI-compatible request body - every modern field

A concrete request that exercises every field a 2026 inference
client should know:

```json
{
  "model": "Qwen3-VL-8B-Instruct",
  "messages": [...],
  "tools": [
    { "type": "function",
      "function": {
        "name": "list_pals",
        "parameters": { "type": "object", "properties": {} },
        "strict": true
      }
    }
  ],
  "tool_choice": "auto",
  "parallel_tool_calls": true,
  "response_format": {
    "type": "json_schema",
    "json_schema": {
      "name": "PalReply",
      "schema": { /* strict JSON Schema */ },
      "strict": true
    }
  },
  "modalities": ["text", "audio"],
  "audio": { "voice": "Tina", "format": "wav" },
  "stream": true,
  "stream_options": { "include_usage": true },
  "seed": 4242,
  "top_logprobs": 5,
  "logprobs": true,
  "service_tier": "auto",
  "max_completion_tokens": 1024,
  "reasoning_effort": "medium"
}
```

Field-by-field truth table:

| Field | Status in vLLM | Notes |
|---|---|---|
| `tools` / `tool_choice` (`auto` / `required` / `none` / named) | default ≥ 0.8.3 | `required` reached parity in 0.8.3 |
| `parallel_tool_calls` | opt-in default `true` | Whether multiple calls return is up to the parser/template |
| `response_format` | `json_object` (legacy), `json_schema` (modern, xgrammar/guidance), `text` | Older `extra_body.guided_json` deprecated as of 0.16; switch to `response_format` |
| `modalities` | vendor-specific to omni stacks (vLLM-Omni, Qwen Studio) | `["text"]` or `["text", "audio"]`; Qwen3-Omni rejects audio-only |
| `audio` | required when `modalities` includes `"audio"` | `{voice, format}`; voice values are model-specific |
| `seed` | supported since 0.5 | Deterministic across replicas only when same TP layout |
| `stream_options.include_usage` | supported | Final `choices: []` chunk with `usage` arrives **before** `[DONE]` |
| `top_logprobs` (0–20) | requires `logprobs: true` | Schema fixed in 0.10+ |
| `service_tier` | passthrough no-op in vLLM (`auto`/`default`/`flex`) | Real behaviour only on hosted OpenAI |
| `strict` on tools | accepted but **not enforced** as of 0.16 | Use `response_format=json_schema` for structural enforcement |
| `reasoning_effort` | passthrough to reasoning-aware parsers (DeepSeek-R1, Qwen3-Thinking) | Cursor / Claude Code / Cline emit this |

Production usage observed in the wild:

- **Cursor** + **Cline**: `parallel_tool_calls: true`, `stream_options.include_usage: true`, `response_format=json_schema` with `strict: true` for structured edits.
- **Aider**: minimal — `tool_choice: "auto"` only.
- **Anthropic SDK streaming**: uses `jiter from_json(partial_mode=True)` to incrementally re-parse accumulated `function.arguments` deltas.

## 2. Tool-calling pitfalls fixed in 2025–2026

Things that broke in 0.6–0.8 and are now fixed:

- **Streaming finish chunk wiped name/id/type** (vLLM #31437). Final delta in `serving_chat.py` overwrote the parser's `DeltaMessage` with one only containing `index` + `arguments`, breaking tool correlation. Fixed in 0.12+.
- **`type:null` leaked through `model_dump_json(exclude_unset=True)`**. Continuation chunks set `type=None` explicitly so clients saw `"type":null`. Fix: don't set fields on continuation deltas.
- **Hermes parser dropped raw text** (vLLM #31871) — fixed in 0.13.
- **Qwen3Coder + spec decode lost parameters** (vLLM PR #35615) — single-pass `if` replaced with `while` accumulating fragments. **Critical fix; bump if you use Qwen3-Coder.**
- **Missing `"type":"function"` on first chunk** with named `tool_choice` (vLLM #16340).

Practical client rules:

- Buffer on `tool_call.id` not on `index`. Reassemble by `id` if present, fall back to `index` only when null.
- `tool_choice: "required"` forces a function selection but does **not** guarantee schema-valid arguments unless you also set `response_format=json_schema`. Belt-and-suspenders.

## 3. Multi-image content arrays (Gemma 4 31B / Qwen3-VL)

The modern shape — `type: "image_url"` everywhere; `type: "image"` is legacy Qwen-only:

```json
{
  "role": "user",
  "content": [
    { "type": "text", "text": "Compare these two screenshots:" },
    { "type": "image_url", "image_url": { "url": "data:image/png;base64,..." } },
    { "type": "image_url", "image_url": { "url": "https://.../b.jpg", "detail": "high" } },
    { "type": "text", "text": "What changed?" }
  ]
}
```

Gotchas:

- **Order matters.** Both Gemma 4 and Qwen3-VL bind images to nearby text via positional encoding; interleave correctly.
- **Gemma 4 vision token budget is per-request configurable**: `{70, 140, 280 (default), 560, 1120}` tokens per image — pass via `extra_body.mm_processor_kwargs.max_soft_tokens`. (Gemma 3 was fixed at 256 tokens / 896×896.)
- **Qwen3-VL** uses dynamic resolution; cap with `min_pixels` / `max_pixels` in `mm_processor_kwargs` to keep KV cache under control. Default cap ≈ 16k tokens per image at full resolution.
- **Base64 vs URL.** URL is faster (server fetches once, can prefix-cache) but only when vLLM has network egress. Base64 inflates payload ~33% but always works. Keep both code paths.
- **Multi-image limit.** Qwen3-VL supports tens of images per turn; Gemma 4 31B documented at 32+ but degrades after ~16 in a single turn.

## 4. Native audio-in (`input_audio`)

Concrete shape (Qwen3-Omni via vLLM-Omni):

```json
{
  "role": "user",
  "content": [
    { "type": "input_audio",
      "input_audio": { "data": "<base64-pcm16>", "format": "wav" } },
    { "type": "text", "text": "Reply in the same language." }
  ]
}
```

Hard constraints (vLLM-Omni stable docs):

- **Format:** `wav`, `mp3`, `flac`, `ogg`, `m4a` accepted at chat-completion level. Realtime/streaming API requires **PCM16, mono, 16 kHz**.
- **Output sample rate:** 24 kHz when audio is generated (Qwen3-Omni Talker).
- **Max duration:** ~30 s per audio block recommended; longer needs chunking.
- `modalities: ["text", "audio"]` + `audio: {voice: "Cherry", format: "wav"}` triggers Talker.
- Sending audio to a text-only model: vLLM returns 400 with `Multimodal data is not supported by this model`.
- **Output streaming:** `response.audio.delta` events carry incremental base64 PCM, plus a parallel transcript via `response.audio.transcript.delta`.

## 5. Streaming patterns that have settled

- **OpenAI Realtime / Responses API event names** (now mirrored by vLLM-Omni):
  `response.created`, `response.output_item.added`,
  `response.content_part.added`, `response.output_text.delta`,
  `response.function_call_arguments.delta`,
  `response.audio.delta`, `response.audio.transcript.delta`,
  `response.completed`, `error`.
  Chat Completions stays on plain `data: {chunk}\n\n` + `data: [DONE]\n\n`.
- **`[DONE]` sentinel is mandatory.** Never close on first empty chunk; older vLLM (≤ 0.7) sometimes emitted empty `choices: []` mid-stream during preemption.
- **`include_usage: true`** → final usage chunk arrives **before** `[DONE]`. Don't crash if `choices` is empty.
- **Mid-tool-call stream drop:** OpenAI / Anthropic / vLLM all expect the client to discard the partial call and retry the entire request. There is no resume-token.
- **Partial JSON during tool args:** incremental `function.arguments` deltas are **not** JSON-valid. Use a tolerant parser. Anthropic adds beta header `anthropic-beta: fine-grained-tool-streaming-2025-05-14` for this.

## 6. vLLM 0.9–0.16 specific bugs / fixes

Worth-it bumps:

| Version | What landed |
|---|---|
| 0.10 → 0.11 | Chunked-prefill scheduling rewrite, decode prioritization fixed (fewer p99 spikes for single-user) |
| 0.12 | Streaming tool-call finish-chunk fix (#31437) |
| 0.13 | Hermes parser raw-text bug (#31871) fixed |
| 0.14 | EAGLE-3 + full-graph-mode stable; previously crashed with APC enabled |
| 0.15 | Qwen3Coder spec-decode parameter loss (PR #35615) — bump if you use Qwen3-Coder. Realtime API GA. |
| 0.16 (Feb 2026) | Async scheduling + PP, Realtime API stabilized, XPU rewrite, unified parallel drafting for spec-decode. MTP + structured outputs now compose. **Caveat:** Qwen3.6-27B-FP8 + MTP + APC + chunked-prefill still crashes after ~26k tokens (#40756). |

Single-user companion tuning:

- `--max-num-seqs 4` (not 256) — drops KV reservation, lowers TTFT ~30%.
- `--max-num-batched-tokens 8192` minimum; bump to 16384 if VRAM allows.
- `--enable-chunked-prefill` on — single-user benefits because long context is split.
- `--enable-prefix-caching` on — system prompt + character card hits ~95% cache rate.
- Avoid `--enforce-eager` unless CUDA-graph + EAGLE-3 misbehave (true in 0.13–0.14, fine in 0.16).

## 7. Memory tricks landed in 2026

- **PagedAttention v2** is shipped; KV waste < 4% (~2–4× throughput vs. v1).
- **Three-tier KV (HBM → DRAM → NVMe):** not native in vLLM 0.16 main. Use **LMCache** as the offload backend.
- **`max_model_len` vs `max_context_len`:** `max_model_len` is the engine cap; `max_context_len` (per-request) gates KV allocation for that conversation. Set engine cap to model's RoPE limit; clamp per-conversation to keep eviction predictable.
- **Idle eviction:** companion runtime → set short `kv_cache_idle_s` (~ 60 s) so background dreams don't hold the GPU hostage.

## 8. C# patterns for production-grade clients

- **Backwards-compatible parsing.** Design DTOs around 2026 OpenAI shape, but make `tool_calls` a `List<ToolCall>?` (older Ollama returns `function_call` singular). Use `JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = AllowReadingFromString }`. Wrap parsing in `TryParseModern → TryParseLegacy → TryParseError` chain. Error envelopes are `{ error: { message, type, code } }` for OpenAI/vLLM but llama.cpp returns `{ error: "text" }` — support both.
- **Timeouts.** Split the timeout: `HttpClient.Timeout = TimeSpan.FromMinutes(10)` for streaming (generous), but enforce a **per-token watchdog** via `CancellationTokenSource.CancelAfter` reset on every delta. Non-streaming uses a strict 60 s. Never share `HttpClient.Timeout` between the two.
- **Cancellation.** Pass `HttpCompletionOption.ResponseHeadersRead`, then read body with `await using var stream = await resp.Content.ReadAsStreamAsync(ct)`. `ct.Cancel()` aborts the underlying socket; vLLM detects FIN and frees the request. `ConfigureAwait(false)` everywhere.
- **Circuit breaker + EAGLE-3.** Classify failures. Draft-model stalls return `503` with `x-vllm-spec-decode-error` (or `200` with degraded latency). Treat draft-model failures as **degraded but healthy** — don't open the breaker. Open only on connection refused, 5xx without spec-decode header, or 4 consecutive timeout watchdog hits.

## 9. Three creative ideas worth shipping

1. **Per-character LoRA hot-swap as a config flag.** Difficulty: Low. Set `VLLM_ALLOW_RUNTIME_LORA_UPDATING=1` and use the LoRA Resolver Plugin pattern. Wire shape: `model: "qwen3-8b:depresso-v3"` — resolver plugin maps `:variant` to `lora_path=characters/depresso/v3.safetensors` and calls `/v1/load_lora_adapter` with `load_inplace: true`. Companion can swap personality between turns with zero downtime.
2. **Screen-capture vision pipe via NDI.** Difficulty: Medium. NDI Screen Capture HX (GPU-accelerated) → libndi C# binding → grab frame at 2 fps → JPEG-encode → push as `image_url` block to a vision model with a pinned system prompt ("you can see the player's screen"). Tag each image with a Palworld region from UE4SS so the model knows whether you're in inventory vs. combat. Realistic latency: ~250 ms screen-to-token at 5B vision model.
3. **Autonomous "dreams".** Difficulty: Medium-High. While player is idle, runtime queues a background completion: "Summarize today's events into 3 lasting impressions about [companion]'s relationship with the player." Use `service_tier: "flex"` analog (low-priority queue), write distilled embeddings to a per-character vector store (sqlite-vec works), surface those as retrieved context next session. Gate on idle > 5 min + KV cache < 50% utilization so it doesn't compete with active play.

## 10. Voice cloning workflow as of 2026

| Engine | Latency | Hardware | Voice persistence | Verdict |
|---|---|---|---|---|
| **F5-TTS (Fast)** | 33× RTF, ~ 200 ms first audio | 8 GB VRAM | Reference clip per request (3–10 s WAV) | Solid default; mature, well-cloned by community. |
| **Chatterbox Turbo** | < 200 ms latency, 350M params | 4 GB VRAM | Reference clip; fine-tune adapter for stable persona | Best for real-time companion. ~63.75% blind preference vs ElevenLabs. |
| **IndexTTS2** | ~ 400 ms first audio | 8–12 GB VRAM | Speaker-embedding cache | Higher fidelity, slower. Hyped but ecosystem still thin — third-party tooling sparse, treat as immature for production. |

Production wire shape (vLLM-Omni Speech API or external F5 server):

```json
POST /v1/audio/speech
{
  "model": "f5-tts",
  "input": "Hi traveler.",
  "voice": "depresso_clone_v3",
  "response_format": "pcm",
  "speed": 1.0,
  "extra_body": {
    "reference_audio": "<base64 wav>",
    "reference_text": "..."
  }
}
```

Voice-identity persistence pattern: cache `(character_id, version) → reference_audio_blob` in sqlite, send `voice` as a stable handle. F5-TTS rebuilds the embedding each call (fast, deterministic). Chatterbox supports a **voice fine-tune** (~ 30 min on a 3090, 5–10 min audio) that becomes a persistent adapter — the right call if a character ships forever. IndexTTS2 caches speaker embeddings server-side but the 2026 API isn't stable enough to bet on.

## 11. Pass 262 ASR timing receipt note

Official transcription APIs now make verbose transcription metadata useful
enough to qualify, but not safe enough to persist raw. OpenAI documents
`timestamp_granularities` as a transcription option that requires
`response_format=verbose_json`; vLLM's current OpenAI-compatible transcription
docs show `verbose_json` responses carrying `duration` and `segments[]`, and
also call out server-side audio file limits. The PalLLM implementation follows
that shape without copying content into proof artifacts: `segment` / `word`
timestamp requests are startup-validated, multipart-forwarded only on
`verbose_json`, and reduced to counts, duration/coverage fields, and review
flags. This lines up with the existing ASP.NET Core admission posture: timeout
and queue limits stay around the expensive speech lane, while the receipt tells
operators whether an endpoint returned timing metadata worth promoting.

## 12. Pass 323 omni connector split-lane note

Current primary sources support the repo's conservative multimodal posture:
Google's Gemma 4 launch confirms the family now includes multimodal, local-first
models; Qwen's Qwen3-Omni repository describes a natively omni-modal model with
text, image, audio, video, and speech output; vLLM's multimodal docs call out
explicit media admission controls such as `--limit-mm-per-prompt` and media
domain allowlists; llama.cpp documents libmtmd-backed image/audio input through
the OpenAI-compatible chat endpoint.

The implementation consequence is not "point everything at the omni endpoint."
For PalLLM, the safer default is split-lane wiring: `pal connect omni
-WriteConfig` now updates `PalLLM:Vision` only and preserves the existing text
`PalLLM:Inference` endpoint. Operators can still pass `-WireInference`, but that
flag is intentionally proof-lane-only until the exact server/model combination
has passed text-only chat, image, audio, strict JSON/tool-call, latency,
fallback-counter, and stall/reconnect replay. This keeps flashy Gemma/Qwen omni
experiments available without letting a media server silently become the player
chat path.

## Sources

- [OpenAI Audio Transcriptions API](https://platform.openai.com/docs/api-reference/audio/transcriptions)
- [vLLM v0.16.0 release notes](https://github.com/vllm-project/vllm/releases/tag/v0.16.0)
- [vLLM Tool Calling docs](https://docs.vllm.ai/en/latest/features/tool_calling/)
- [vLLM Structured Outputs](https://docs.vllm.ai/en/latest/features/structured_outputs/)
- [vLLM OpenAI-Compatible Server](https://docs.vllm.ai/en/stable/serving/openai_compatible_server/)
- [vLLM Multimodal Inputs](https://docs.vllm.ai/en/latest/features/multimodal_inputs/)
- [vLLM-Omni Qwen3-Omni online serving](https://docs.vllm.ai/projects/vllm-omni/en/stable/user_guide/examples/online_serving/qwen3_omni/)
- [Qwen3-Omni GitHub repository](https://github.com/QwenLM/Qwen3-Omni)
- [Don't Break the Cache: An Evaluation of Prompt Caching for Long-Horizon Agentic Tasks](https://arxiv.org/abs/2601.06007)
- [ASP.NET Core request timeouts](https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts?view=aspnetcore-10.0)
- [ASP.NET Core rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)
- [vLLM serve arguments](https://docs.vllm.ai/en/latest/cli/serve/)
- [vLLM-Omni Speech API](https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/)
- [vLLM-Omni Qwen3-Omni example](https://docs.vllm.ai/projects/vllm-omni/en/latest/user_guide/examples/online_serving/qwen3_omni/)
- [vLLM Multimodal Inputs](https://docs.vllm.ai/en/latest/features/multimodal_inputs/)
- [SGLang observability and request dump/replay](https://docs.sglang.io/docs/advanced_features/observability)
- [SGLang Qwen3.6 serving guide](https://docs.sglang.io/cookbook/autoregressive/Qwen/Qwen3.6)
- [Google Gemma 4 announcement](https://blog.google/innovation-and-ai/technology/developers-tools/gemma-4/)
- [Google Gemma 4 multi-token prediction](https://blog.google/innovation-and-ai/technology/developers-tools/multi-token-prediction-gemma-4/)
- [Gemma 4 vLLM Recipes](https://docs.vllm.ai/projects/recipes/en/latest/Google/Gemma4.html)
- [Qwen3.5 / Qwen3.6 vLLM Recipes](https://docs.vllm.ai/projects/recipes/en/latest/Qwen/Qwen3.5.html)
- [Qwen3-VL vLLM Recipes](https://docs.vllm.ai/projects/recipes/en/latest/Qwen/Qwen3-VL.html)
- [Streaming tool calls finish chunk bug #31437](https://github.com/vllm-project/vllm/issues/31437)
- [Hermes streaming raw text bug #31871](https://github.com/vllm-project/vllm/issues/31871)
- [Qwen3Coder spec-decode fix PR #35615](https://github.com/vllm-project/vllm/pull/35615)
- [Anthropic streaming tool_use docs](https://docs.anthropic.com/en/api/streaming)
- [Anthropic fine-grained tool streaming](https://andyjakubowski.com/engineering/handling-invalid-json-in-anthropic-fine-grained-tool-streaming)
- [vLLM LoRA Resolver Plugins](https://docs.vllm.ai/en/stable/design/lora_resolver_plugins/)
- [vLLM KV cache offload RFC #19854](https://github.com/vllm-project/vllm/issues/19854)
- [LMCache + vLLM](https://docs.vllm.ai/projects/production-stack/en/vllm-stack-0.1.2/tutorials/kv_cache.html)
- [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- [llama.cpp multimodal docs](https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md)
- [llama.cpp SWA prompt-cache fix PR](https://github.com/ggml-org/llama.cpp/pull/21749)
- [Red Hat: structured outputs in vLLM](https://developers.redhat.com/articles/2025/06/03/structured-outputs-vllm-guiding-ai-responses)
- [Red Hat: EAGLE-3 spec decode in vLLM](https://developers.redhat.com/articles/2025/07/01/fly-eagle3-fly-faster-inference-vllm-speculative-decoding)
- [Inferless TTS benchmark 2025](https://www.inferless.com/learn/comparing-different-text-to-speech---tts--models-part-2)
- [F5-TTS fine-tuning guide](https://instavar.com/blog/ai-production-stack/F5_TTS_Fine_Tuning_Voice_Cloning_Guide)
- [Ollama OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility)
- [OpenAI Chat Completions API reference](https://platform.openai.com/docs/api-reference/chat/create)
- [NDI Screen Capture HX](https://docs.ndi.video/all/using-ndi/ndi-tools/ndi-tools-for-windows/screen-capture-hx)
