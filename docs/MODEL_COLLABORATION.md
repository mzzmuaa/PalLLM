# Local Model Collaboration

Last audited: `2026-05-22`

> **Quantization choice → see [`QUANTIZATION.md`](QUANTIZATION.md)** for
> the full NVFP4 / MXFP4 / FP8 / Q4_K_M / Q8_0 matrix with community
> sentiment and per-architecture defaults. This doc focuses on the
> *role pairing* — quantization is the layer below.

> **Engine recommendation as of Pass 339:** **llama.cpp is the default
> inference engine; vLLM is the high-config option.** Pass 339 removed
> Ollama from PalLLM's operator-facing surfaces (no `pal connect ollama`
> verb, no Ollama-defaulted appsettings, no Ollama compose example).
> The Ollama-specific knobs described below (`OLLAMA_CONTEXT_LENGTH`,
> `ollama ps` residency receipts, native Ollama keep-alive, etc.) remain
> documented because the runtime's `ModelCollaborationPlanner` still
> emits them as provider-aware hints when an operator's
> `Inference.BaseUrl` points at Ollama out-of-band — but no shipping
> recommendation in PalLLM points there anymore. Treat all Ollama
> references in this doc as "this is what happens IF you point
> PalLLM at Ollama yourself," not "this is what PalLLM recommends."

This guide documents how PalLLM uses local model pairings for Palworld-mod
work only. The scope here is intentionally narrow:

- PalLLM runtime and sidecar code
- UE4SS bridge and compatibility drift
- HUD and subtitle seam work
- screenshot and dashboard review
- documentation sync, release hardening, and model promotion

It is not a generic "AI studio" guide.

## Current posture

PalLLM's default collaboration shape is a fast lane plus a deliberate lane:

- `Qwen3.6-35B-A3B` as the fast worker, scout, and watchdog
- `Qwen3.6-27B` as the slower dense planner, reviewer, and final judge

That split is useful in this repo because PalLLM has two very different kinds
of work:

- quick bridge, documentation, or runtime edits where latency matters
- release-facing and native-seam work where correctness matters more than speed

## What each lane should own

Every lane in `GET /api/inference/collaboration` now carries a
`Capability` block so operators do not have to reverse-engineer model fit from
the tag string. The block is deterministic and local-only: it records family,
recommended backend, input/output modalities, vision/video/audio support,
structured-output/tool-call/speculative-decoding fit, a nested `ServingProfile`,
the precise `Speculation` mode profile, serving optimizations, promotion
receipts, metric receipts, and runtime guards.

Use it as a routing sanity check:

- Screenshot or video work needs `InputModalities` containing `image` or
  `video`; otherwise route through `PalLLM:Vision` or a Media role.
- Audio-in and realtime voice remain opt-in lanes; a model advertising audio
  capability does not change the text chat fallback contract.
- Qwen Omni streaming video remains a proof-only lane; `/v1/video/chat/stream`
  evidence must include frame cadence, duration caps, optional PCM16 chunk
  policy, reconnect/stall behavior, and still-image or world-state fallback.
- Newer OpenAI-compatible `/v1/responses` surfaces stay proof-only for
  PalLLM until their stateful response ids, SSE event names, tool events,
  usage receipts, cleanup behavior, and fallback counters are replayed route by
  route. The primary companion lane remains stateless
  `/v1/chat/completions`.
- vLLM-Omni `/v1/videos` and `/v1/videos/sync` are async diffusion-job
  surfaces, not chat, screenshot understanding, or live HUD routes. Treat them
  as offline release-proof or walkthrough material only after job cleanup,
  cancellation, prompt-publication hygiene, and no-interference proof.
- Strict JSON/tool-call work should use the structured-output hint
  (`InferencePrompt.ResponseFormat` for text lanes or the vision
  world-state schema hook for image lanes) and keep speculative decoding
  behind qualification tests.
- Treat schema-constrained output as provider/request-shape-specific. Promotion
  receipts must record schema name/digest, PalLLM route, served model, request
  shape (`response_format`, `guided_json`, Ollama `format`, or grammar),
  grammar/backend id, parse/schema validation, token/p95/fallback evidence, and
  the app-side validator result.
- Multimodal lanes should prefer local base64 media, stable media UUIDs for
  repeated proof replays, and explicit server-side media limits/allowlists.
- `pal connect omni -WriteConfig` is intentionally media-first: it points
  `PalLLM:Vision` at the omni endpoint and preserves the existing text chat
  endpoint. Use `-WireInference` only as a proof-lane override after the exact
  endpoint passes text-only, media, strict JSON/tool-call, latency, and fallback
  replay.
- `Capability.Speculation` splits speculation into n-gram, draft-model, and
  model-native MTP readiness so tools do not treat one broad capability bit as
  a green light for every route.
- Model artifact provenance is part of lane promotion. A downloaded model,
  quant, adapter, mmproj, or drafter is not a PalLLM default until the
  operator has recorded source URL or local path, immutable revision or file
  hash, model-card license metadata, base-model/adapter relation, weight
  format, runtime/tokenizer revision, `trust_remote_code` status, and whether
  redistribution is allowed.
- Capability claims need their own receipt. Do not infer vision, audio,
  tool-call, realtime, or MTP readiness from a family name alone; record the
  primary model-card/vendor-doc revision, the local runtime catalog identity,
  the enabled launch flags, positive canaries for claimed capabilities, and
  negative canaries for unsupported modalities.
- Promotion evidence now has a dedicated `PromotionReceipts[]` array so tools
  can distinguish provenance, route replay, media-admission, redistribution,
  and fallback-proof records from pure Prometheus-style metric names.

`Capability.ServingProfile` makes those rules executable by tools instead of
leaving them as prose. It includes:

- `ProfileId` and `PreferredRuntime` - compact routing keys such as
  `gguf-chat`, `gguf-libmtmd-multimodal`, `vllm-openai-multimodal`, or
  `omni-realtime-opt-in`, with Foundry Local / Windows ML appearing as a
  preferred runtime for proof-gated non-GGUF text and multimodal lanes, and
  OpenVINO Model Server appearing as a `/v3` local proof lane for Intel CPU,
  GPU, and NPU hardware. TensorRT-LLM appears as a `/v1` proof lane for NVIDIA
  systems where `trtllm-serve` can expose `/health`, `/metrics`, `/v1/models`,
  and `/v1/chat/completions`.
- `StartupHints[]` - current runtime flags to check before a lane is promoted:
  llama.cpp / GGUF locality knobs (`--host 127.0.0.1`, `-c`, `-np`,
  `-b`, `-ub`, `-ngl`, `--flash-attn on`, `--metrics`, `--no-webui`),
  prompt-cache sizing (`--cache-prompt`, `--cache-reuse`, `-sps`,
  `-cram`), a state-cache canary for `--swa-full`, `--slot-save-path`, and
  host prompt-cache restore on SWA / hybrid / recurrent / long-context GGUFs,
  proof-gated llama.cpp KV cache compression (`-ctk q8_0 -ctv q8_0`), idle
  sleep (`--sleep-idle-seconds`), `pal connect llamacpp` proof guidance,
  llama.cpp schema proof through `response_format` / grammar conversion with a
  PalLLM schema digest receipt,
  llama.cpp
  speculative proof lanes (`--spec-type ngram-simple` or
  `--spec-type ngram-mod` with current `--spec-draft-*` /
  `--spec-ngram-mod-*` flags), optional GGUF `--spec-type draft-mtp` proof for
  text-only Qwen3.6 or Gemma 4 lanes, Ollama structured-output proof that keeps native
  `format` JSON schema and OpenAI-compatible `response_format` as separate
  request shapes, Ollama low-latency knobs
  (`OLLAMA_CONTEXT_LENGTH`, `OLLAMA_KEEP_ALIVE`, `OLLAMA_FLASH_ATTENTION`,
  `OLLAMA_KV_CACHE_TYPE`, `OLLAMA_NUM_PARALLEL`,
  `OLLAMA_MAX_LOADED_MODELS`, `OLLAMA_MAX_QUEUE`),
  `--enable-prefix-caching`,
  `--prefix-caching-hash-algo sha256_cbor`,
  `--enable-chunked-prefill`, `--performance-mode interactivity` for
  player-facing vLLM companion / vision / narration lanes, scheduler caps
  (`--max-num-batched-tokens`, `--max-num-seqs`,
  `--max-num-partial-prefills`, `--max-long-partial-prefills`, and
  `--long-prefill-token-threshold`) to keep short companion turns from sitting
  behind long proof/docs prompts, proof-only vLLM disaggregated prefill/decode
  topologies using `NixlConnector`, `P2pNcclConnector`,
  `MooncakeConnector`, or `MultiConnector` only after monolithic baseline
  replay, optional sparse-MoE DBO proof lanes using `--enable-dbo`,
  `--dbo-decode-token-threshold`, and `--dbo-prefill-token-threshold` only after
  multi-GPU data/expert-parallel topology and all2all receipts are captured,
  optional `--scheduling-policy priority` plus
  `PalLLM:Inference:RequestPriority` for foreground lanes after mixed replay
  proves lower priority values win queue time without starving proof/docs
  lanes, optional `--generation-config vllm` for
  reproducible PalLLM sampling when model-repo defaults would otherwise
  override configured temperature/top-p during qualification, optional
  proof-gated `--kv-cache-dtype fp8` for memory-pressure or long-context lanes,
  `--structured-outputs-config.backend xgrammar` plus separate vLLM
  `response_format`, `guided_json`, `guided_choice`, `guided_regex`,
  `guided_grammar`, and `structural_tag` proof shapes, `--limit-mm-per-prompt.*`,
  `--mm-processor-cache-gb`, `--mm-processor-cache-type`, `--mmproj`,
  optional `--ec-transfer-config` for qualified vLLM/LMCache encoder-cache
  experiments, optional idle-only `VLLM_SERVER_DEV_MODE=1
  --enable-sleep-mode` for vLLM VRAM reclaim, trusted-only
  `MOONCAKE_CONFIG_PATH` plus
  `--kv-transfer-config '{"kv_connector":"MooncakeStoreConnector","kv_role":"kv_both"}'`
  as a proof-only Mooncake Store lane for local CPU/offload or
  multi-instance prefix-reuse replays, `MultiConnector` only after
  disaggregated prefill/decode proof,
  qualification-only vLLM KV-event publishing through a loopback
  `KVEventsConfig` / ZMQ subscriber when block-store and block-remove proof is
  needed,
  proof-only external KV cache process-boundary experiments using PegaFlow /
  `PegaKVConnector` or another `kv_connector_module_path` cache daemon only
  after daemon pool, SSD/RDMA mode, endpoint binding, namespace/model identity,
  redacted `kv_transfer_config`, and local-prefix-cache baseline receipts are
  recorded,
  `--enable-mm-embeds` for precomputed media-embedding lanes,
  optional local personality-adapter hints (`--enable-lora`,
  `--max-loras 1`, and qualified `--fully-sharded-loras`) for
  operator-approved pack adapters, staging-only
  `--enable-tower-connector-lora` proof for multimodal adapter tests,
  model-artifact provenance checks (`--revision`, `--code-revision`,
  `--tokenizer-revision`, local SHA-256, safetensors/pickle status, and
  license metadata before promotion or redistribution),
  SGLang alternative-lane checks (`--mem-fraction-static`,
  `--max-running-requests`, `--chunked-prefill-size`, radix cache kept on,
  `--enable-metrics`, proof-only `--enable-deterministic-inference`,
  attention-backend proof for auto-selection versus pinned FlashInfer,
  FA3/FA4, Triton, TRTLLM MHA/MLA, AITER, or Intel XPU backends with page-size,
  FP8/FP4 KV, multimodal, and spec-topk support receipts, proof-only HiCache /
  `--enable-hierarchical-cache` qualification with measured `--page-size`,
  `--hicache-ratio` or `--hicache-size`, `--hicache-io-backend`, write policy,
  optional storage backend, SGLang EAGLE-3/adaptive/SpecV2 speculation proof
  with topk/draft-token/acceptance/OOM receipts, and
  `--grammar-backend xgrammar` / `structural_tag` for structured-output proof
  shapes), plus SGLang
  Model
  Gateway qualification for multi-worker lanes: retries with jitter,
  worker-scoped circuit breakers, token-bucket queuing, background health
  checks, request-id propagation, and cache-aware load monitoring must be
  visible in metrics before a pool replaces a single player-facing worker,
  TensorRT-LLM checks (`trtllm-serve serve <model>`, `--backend pytorch`,
  `--enable_chunked_prefill`, `--served_model_name`, `--tool_call_parser`,
  `/health`, `/metrics`, YAML `--config`, and `pal connect tensorrt` proof
  guidance),
  vLLM `/v1/responses` checks for response lifecycle, streaming event parsing,
  response-id cleanup, built-in tool payload retention, usage receipts, and
  ordinary chat fallback before any migration from `/v1/chat/completions`,
  Hugging Face `transformers serve` checks (`pip install
  transformers[serving]`, positional `transformers serve <repo@revision>`,
  `--continuous-batching`, `/load_model`, `/v1/models`, and optional
  `/v1/audio/transcriptions` plus experimental `/v1/responses` proof lanes),
  LM Studio desktop/server checks
  (`lms server start`, `lms load`, `/v1/models`, `/v1/chat/completions`,
  `response_format: json_schema`, tools, `ttl`, auto-evict, and
  `pal connect lmstudio` proof guidance), OpenVINO Model Server checks
  (`ovms.exe` or `openvino/model_server:2026.1`, `--task text_generation`,
  `--target_device`, `/v3/models`, `/v3/chat/completions`, VLM local-media
  allowlists, NPU `PREFILL_HINT` / `GENERATE_HINT` qualification through
  GenAI pipelines, and `pal connect openvino` proof guidance), Microsoft
  Foundry Local /
  Windows ML checks (`winget install Microsoft.FoundryLocal`,
  `foundry model list --filter task=chat-completion`, `foundry model run`,
  `foundry service status`, `/openai/status`, `/openai/models`, execution
  provider proof, and optional `/v1/audio/transcriptions` ASR proof lanes),
  Gemma 3n edge-memory checks (PLE caching, conditional parameter loading,
  and text-only parameter skipping), Qwen Omni `vllm serve <model> --omni`
  voice proof lanes,
  concrete n-gram speculative-decoding config for qualified text lanes,
  guarded draft/EAGLE speculative-decoding config where appropriate,
  model-native Qwen3.6 / Gemma 4 MTP hints after per-route proof, Qwen3
  reasoning parser setup, Qwen3.6 MTP-1 latency proof with
  `--no-enable-prefix-caching`, and Qwen3.6 `--language-model-only`
  split-lane guidance when a text-only server should skip the vision encoder
  and keep KV cache headroom for companion chat.
- `Capability.Speculation` - machine-readable flags for
  `SupportsNgramSpeculation`, `SupportsDraftModelSpeculation`,
  `SupportsModelNativeMtp`, `RequiresModalityIsolatedProof`, and
  `RequiresPrefixCacheOffForLatencyMtp`, plus the recommended first mode and
  promotion guard. Qwen3.6 / Qwen3-Next-style low-concurrency lanes now report
  `mtp-1-low-concurrency-prefix-cache-off`; Gemma 4 lanes report
  `matching-gemma4-drafter`.
- `RequestHints[]` and `CacheHints[]` - stable prefix guidance, JSON-schema
  request shaping, OpenAI-compatible media `uuid` fields for repeated
  screenshots/proof replays, optional vLLM `cache_salt` forwarding through
  `PalLLM:Inference:PrefixCacheSalt`, optional proof-gated
  `PalLLM:Inference:ReasoningEffort` forwarding for reasoning-capable chat
  endpoints, optional endpoint-proven `PalLLM:Inference:Seed` forwarding for
  replay comparisons, optional endpoint-proven
  `PalLLM:Inference:TokenBudgetField=max_completion_tokens` forwarding for
  reasoning lanes that reject `max_tokens`, optional endpoint-proven
  `PalLLM:Inference:FrequencyPenalty` forwarding for repetition-control
  canaries, optional endpoint-proven
  `PalLLM:Inference:TopK`, `PalLLM:Inference:MinP`, and
  `PalLLM:Inference:RepetitionPenalty` forwarding for local-sampler canaries,
  optional endpoint-proven
  `PalLLM:Inference:RequestPriority` forwarding only for vLLM priority
  schedulers, optional endpoint-proven
  `PalLLM:Inference:ParallelToolCalls=false` forwarding for strict
  directive/action routes, optional endpoint-proven
  `PalLLM:Inference:StopSequences[]` forwarding for strict delimiter or
  low-latency canaries, optional prompt-level
  `InferencePrompt.ResponseFormat` forwarding for strict JSON-schema text
  canaries with route/model/request-shape/schema-digest cache identity, a
  reminder not to send uuid-only media until the same server
  process has proven the cache entry exists, and a caution
  that experimental vLLM disaggregated prefill is a dual-instance tail-latency
  tool, not a general throughput booster. `MoRIIOConnector` single-node P/D is
  the same class of proof lane: record read/write mode through
  `VLLM_MORIIO_CONNECTOR_READ_MODE`, proxy/http/handshake/notify ports,
  prefix-cache-disabled and normal prefix-cache baselines, remote-KV wait time,
  transfer latency, and worker rollback before promotion. P/D promotion
  evidence must include prefill/decode endpoint ids, router/proxy config, redacted
  `kv_transfer_config`, p95 TTFT, p95 ITL, p95 E2E latency, KV-transfer
  latency/failure evidence, queue pressure, decode-only rollback, and PalLLM
  fallback counters. Repeated screenshot, video, or audio
  proof loops should qualify multimodal encoder-cache behavior separately from
  text KV cache; LMCache EC needs an explicit CPU/disk budget and has no
  implicit disk persistence. If multiple replicas sit behind a router, prove
  sticky or KV cache-aware routing beats round-robin before using that pool for
  live turns. MooncakeStoreConnector is an advanced vLLM distributed KV-cache
  pool, not a default player dependency: replay long proof/docs and companion
  turns against local prefix cache, single-node store offload, and any
  MultiConnector pool; keep only redacted config hashes, store/client health,
  cache-hit rate, cold/warm TTFT/E2E, parser parity, fallback counters, and
  rollback evidence. PegaFlow-style external KV cache services are the same
  class of proof-only infrastructure. `FlexKVConnectorV1` joins that external
  offload bucket for CPU, SSD, or remote-store experiments. Compare local prefix
  cache with the daemon-backed route, restart the vLLM worker while the daemon
  stays alive, then stop the daemon and prove rollback to local prefix cache or
  deterministic fallback. Archive only daemon health, endpoint binding,
  pool/SSD/RDMA budget, namespace/model identity hashes, async transfer counts,
  cold/warm TTFT/E2E, cache-hit rate, load/store failures, and PalLLM fallback
  counters; raw KV blocks, SSD paths, namespace strings, and player text stay
  out of support/public bundles. If
  vLLM KV events are enabled for qualification, reduce
  `BlockStored`, `BlockRemoved`, and `AllBlocksCleared` batches to counts,
  block hashes, block sizes, group metadata, replay-gap counters, and
  extra-key classes only; raw `token_ids`, `extra_keys`, cache salts, media
  ids, LoRA names, and prompt-embedding hashes stay out of support and public
  bundles. Keep route/cache proof indexes separate for companion chat,
  screenshot/world-state, audio/ASR, and proof/docs prompts so a hot docs
  prefix cannot evict the live-player prefix without rollback evidence. If a
  vLLM lane uses sleep mode, treat wake-up as a cold-cache
  boundary until prefix, KV, and media-cache behavior is measured again. FP8 or
  NVFP4 KV-cache compression is a separate memory/context tradeoff, not a free
  default: compare quality, exact parse success, TTFT, ITL, cache-hit behavior,
  and KV-cache utilization against `auto` KV cache before promotion.
  For vLLM tool-call proof, set
  `PalLLM:Inference:ParallelToolCalls=false` so PalLLM forwards
  `parallel_tool_calls=false` on strict directive/action routes until there is
  an explicit, tested multi-call fan-out contract.
  For strict action/directive canaries, set prompt-level
  `InferencePrompt.Tools` and `InferencePrompt.ToolChoice` only on the route
  under test; PalLLM forwards `tools` / `tool_choice` verbatim and preserves
  returned `tool_calls` as a receipt, while ordinary companion chat omits both
  fields and keeps deterministic fallback available for empty or malformed
  tool-call-only output.
  For predicted-output proof lanes, pass `InferencePrompt.Prediction` only on
  the exact proof/docs route being qualified; replay with and without
  `prediction`, record accepted request shape, accepted/rejected
  prediction-token receipts when exposed, p95 latency, and fallback counters,
  and keep ordinary companion chat field-free for local endpoint portability.
  For confidence/evaluator proof lanes, pass `InferencePrompt.Logprobs` and
  optional `InferencePrompt.TopLogprobs` only on the exact validator or judge
  route being qualified; replay with and without `logprobs` / `top_logprobs`,
  record accepted request shape, returned choice-level logprob receipts,
  response bytes, p95 latency, and fallback counters, and keep ordinary
  companion chat field-free for local endpoint portability.
  For audio-output proof lanes, pass `InferencePrompt.Modalities` plus
  `InferencePrompt.Audio` only on the exact isolated voice route being
  qualified; replay with and without `modalities` / `audio`, record accepted
  request shape, returned `InferenceResult.AudioJson`, text mirror, response
  bytes, p95 latency, and fallback counters, and keep ordinary companion chat
  field-free for local endpoint portability.
  For multimodal input proof lanes, pass prompt-level
  `InferencePrompt.UserContent` only on the exact route being qualified. Use
  content-part arrays for text plus `image_url`, `video_url`, `input_audio`, or
  `audio_url`, record accepted request shape, media byte caps, parse stability,
  p95 latency, and fallback counters, and keep ordinary companion chat as a
  plain string user message.
  For delimiter proof, set `PalLLM:Inference:StopSequences[]` only after the
  exact endpoint accepts OpenAI-compatible `stop`; replay before/after token
  counts and inspect companion text for accidental clipping before promotion.
  For structured-output proof, pass `InferencePrompt.ResponseFormat` only on
  the exact route being qualified; replay with and without
  `response_format: json_schema`, record schema name/digest, provider request
  shape, grammar/backend id, parse success, schema-validation success, p95
  latency, token usage, fallback counters, and keep strict JSON/tool-call lanes
  no-spec until schema stability is proven. A `json_object`-only pass is not a
  JSON Schema proof; include a schema-echo canary with a required object, enum,
  bounded array, deliberate violation prompt, and changed-schema digest. The
  PalLLM validator remains authoritative even when the upstream server claims
  constrained decoding.
  For repetition-control proof, set `PalLLM:Inference:FrequencyPenalty` only
  after the exact endpoint accepts OpenAI-compatible `frequency_penalty`;
  compare repeated-phrase rate, generated tokens, latency, and fallback
  counters before making it a player-facing default.
  Baseline `PalLLM:Inference:Temperature`, `TopP`, and `PresencePenalty` stay
  ordinary chat-shaping knobs, but the sidecar now validates their OpenAI-style
  bounds at startup so malformed sampler config fails before any player turn is
  queued against the upstream endpoint.
  For token-budget field proof, set
  `PalLLM:Inference:TokenBudgetField=max_completion_tokens` only after the
  exact endpoint rejects or requires the newer field; compare the same route
  with `max_tokens` and `max_completion_tokens`, including accepted request
  shape, usage counters, p95 latency, and fallback counters.
  For local sampler proof, set `PalLLM:Inference:TopK`,
  `PalLLM:Inference:MinP`, or `PalLLM:Inference:RepetitionPenalty` only after
  the exact local runtime accepts `top_k`, `min_p`, or `repetition_penalty`;
  compare style/loop deltas, parser stability, generated tokens, p95 latency,
  and fallback counters before making the setting a player-facing default.
  Treat vLLM `--performance-mode interactivity` as the first candidate for
  one-player PalLLM latency lanes, but compare it against `balanced` and
  `throughput` when the same server also handles batch proof or multi-client
  traffic. Treat `--max-num-batched-tokens` as a latency and KV-headroom budget,
  not a leaderboard number: pair it with a workload-sized `--max-num-seqs` and
  partial-prefill limits before claiming batching or cache wins on live turns.
  Treat vLLM DBO as sparse-MoE worker proof only: compare no-DBO against
  `--enable-dbo` plus decode/prefill thresholds on the same short-turn plus
  long-proof replay, and reject promotion if queue time, parser stability, or
  fallback counters regress.
  Precomputed `image_embeds` / `audio_embeds` / video embeddings are a
  separate trusted optimization lane: use them only when PalLLM owns the
  encoder, tensor shape, and projector metadata, and keep ordinary player
  media on local bytes plus stable `uuid`. If a personality pack uses a local
  LoRA adapter, pick that adapter from validated pack metadata and operator
  config only, never from player text. Measure cache behavior per
  base-model-plus-adapter id because adapter ids are part of cache identity.
  Treat Qwen3.6 or Gemma 4 MTP as a separate model-native speculation mode:
  keep strict JSON, tool-call, judge, and save-replay routes no-spec until each
  route proves stable. Qwen3.6 official cards advertise very large context
  windows, but ordinary companion turns should stay short; reserve 128K+
  contexts for proof, docs-sync, or deliberate review lanes that can afford the
  KV cache. Treat native 262,144-token, 1,010,000-token extended, hosted, and
  reduced-context GGUF profiles as different proof identities: record served
  model id, source, runtime `max_model_len` / context cap, extension flags,
  route token budget, KV/state memory, and fallback counters before promoting a
  long-context lane. Qwen3.6's hybrid Gated DeltaNet / Mamba serving settings
  are likewise proof-lane controls: record runtime version, scheduler strategy,
  page size, attention backend, context, state memory, TTFT/ITL, exact parse
  success, and fallback counters before treating an alternate scheduler or
  kernel as a live companion default.
  Treat native audio-in separately from screenshot/video media: normalize clips
  to mono 16 kHz, cap ordinary proof clips at 30 seconds, hash the normalized
  bytes after trimming policy is applied, and record audio-token cost before
  assuming the route has headroom. For Gemma audio lanes, budget by family:
  Gemma 4 spends `25` audio tokens per second (`750` for a 30-second clip),
  while Gemma 3n spends `6.25` audio tokens per second (`188` for a
  30-second clip). Prefer typed text or cascaded ASR until privacy, latency,
  and fallback behavior are recorded.
  For llama.cpp / GGUF lanes, keep `cache_prompt` enabled for stable PalLLM
  prefixes, treat prompt cache reuse as server/slot-local, and do not claim a
  cache win until second-turn latency, slot eviction, cache RAM pressure, and
  active KV memory have been measured. Treat host prompt-cache restore as a
  per-model-family capability rather than a RAM-only toggle: a same-slot
  second-turn canary should prove reuse, while changed chat templates, context
  sizes, adapters, model files, or server builds should invalidate instead of
  reusing stale state. Schema-bearing llama.cpp receipts must distinguish
  `json_schema`, `json_object`, and grammar-backed requests. For Ollama lanes,
  keep ordinary companion context
  right-sized, verify `ollama ps` reports the intended `CONTEXT` and
  `PROCESSOR`, and treat KV-cache quantization as a global server policy:
  `f16` is the highest-quality default, `q8_0` is the measured memory-pressure
  lane, and `q4_0` stays proof-only until PalLLM structured replay says
  otherwise. Native Ollama `format` schemas and OpenAI-compatible
  `response_format` are separate request-shape proofs.
  For SGLang, keep ordinary companion traffic on the OpenAI-compatible chat API;
  reserve `structural_tag`, EBNF, native sampling params, explicit attention
  backends, FP4/FP8 KV cache, EAGLE-3, adaptive speculation, and SpecV2 overlap
  for proof lanes with parser tests. Keep radix cache enabled for live turns,
  use `--disable-radix-cache` only for cache-on/cache-off A/B proof, and record
  `cache_hit_rate`, token usage, TTFT, ITL, running requests, queued requests,
  selected attention backend, page size, KV dtype, draft backend, acceptance
  rate, OOM/backoff events, and PalLLM fallback counters before changing serving
  policy. Treat HiCache as a separate hierarchical KV offload proof lane:
  compare radix-only, L2 host cache, and optional L3 storage or P/D profiles
  with the same companion, screenshot/world-state, audio/ASR, and proof/docs
  replays; capture page size, host/storage budget, prefetch/write policy,
  backend namespace hash, cold/warm TTFT and E2E latency, queue depth, parser
  stability, attach/detach or backend-stop rollback, and fallback counters
  before promotion. If SGLang
  Model Gateway fronts multiple workers, prefer cache-aware worker selection
  over round-robin only after gateway metrics show a higher hit rate without
  worse p95 companion latency.
  For TensorRT-LLM, keep ordinary PalLLM traffic on `/v1/chat/completions`,
  verify `/v1/models` and `/health` before tier promotion, and store `/metrics`
  receipts for GPU memory, inflight batching, KV-cache stats, and active
  requests shortly after replay turns because iteration records are transient.
- `AdmissionControls[]` and `SecurityControls[]` - default image/video/audio
  caps plus local-first media rules: loopback/private binding, redirect-disabled
  vLLM media fetches, and explicit `--allowed-media-domains` or
  `--allowed-local-media-path` only when the operator chooses them. vLLM-like
  lanes also remind operators to set a workload-sized `VLLM_MAX_N_SEQUENCES`
  cap, keep `--max-num-seqs` / `--max-num-batched-tokens` sized to one-player
  PalLLM traffic first, and keep body/rate limits in front of any non-loopback
  endpoint. Disaggregated prefill/decode, Mooncake Store, and MultiConnector
  topologies stay proof-only until queue time, p95 TTFT/ITL/E2E, KV-transfer
  failure, rollback, and fallback counters beat the local prefix-cache
  baseline. vLLM
  cache salts must be stable, non-secret trust-domain identifiers, not player
  secrets and not one random value per request. vLLM sleep/wake dev endpoints
  are admin-only and must stay loopback-private; do not publish
  `VLLM_SERVER_DEV_MODE` routes to players, LAN browsers, or a public reverse
  proxy. Prefill/decode proxies, KV-transfer ports, Mooncake master, client,
  and store endpoints are loopback/trusted-LAN only; KV blocks, transfer
  handles, raw `kv_transfer_params`, and store metadata are private runtime state and stay out of
  support/public bundles except as hashes and redacted receipt fields.
  `--enable-mm-embeds` lanes are also admin/trusted-tool only because
  malformed embedding shapes can crash the model engine. LoRA adapter lanes
  default to `lora_count<=1`, local hash-pinned adapter paths, no remote
  adapter loads on the player path, and `VLLM_ALLOW_RUNTIME_LORA_UPDATING`
  off unless `/v1/load_lora_adapter` is exposed only on a loopback admin
  surface with hash-pinned local paths. Remote `image_url`, `audio_url`, and
  `video_url` lanes are treated as SSRF-sensitive opt-ins: localhost, private
  ranges, link-local ranges, IP literals, and redirects to private networks
  must be blocked before the lane reaches player traffic.
  llama.cpp lanes should keep `-np` capped to the real player/session
  count and stay bound to `127.0.0.1` by default. If exposed beyond loopback,
  require `--api-key` and keep `--webui-mcp-proxy`, `--tools`, `/props`, and
  `/slots` behind an admin-only surface.
  SGLang lanes should cap `--max-running-requests` and `--max-queued-requests`
  to the actual player workload, leave `--mem-fraction-static` headroom for KV
  cache, require `--api-key` if the server is exposed beyond loopback, keep
  `/metrics` private, and sit behind body/rate limits on any non-loopback path.
  HiCache storage backends, dynamic attach/detach admin endpoints, and
  PD-disaggregation transfer ports must stay on loopback or an admin-only
  trusted LAN, and raw KV pages, backend paths, and storage namespaces stay out
  of support/public bundles.
  SGLang Model Gateway queue depth should be sized to one-player PalLLM traffic
  first; long docs-sync or proof requests must not sit ahead of short companion
  turns, and gateway metrics, worker health, and queue stats stay loopback/admin
  only.
  TensorRT-LLM lanes should keep `trtllm-serve` loopback-only unless an
  authenticated reverse proxy owns API keys, body limits, rate limits, TLS,
  `/metrics` privacy, and cancellation behavior. Disaggregated serving, Dynamo,
  P/D KV-transfer ports, and multi-node topologies stay proof-only until long
  prefill traffic cannot delay short companion turns.
  Ollama lanes should keep `OLLAMA_NUM_PARALLEL=1` on the default one-player
  companion path, raise concurrency only after queue / 503 / p95 evidence, keep
  `OLLAMA_HOST` loopback by default, set `OLLAMA_ORIGINS` deliberately when
  browser access is needed, and use `OLLAMA_NO_CLOUD=1` for air-gapped proof
  lanes.
  Downloaded model weights, GGUF quants, adapters, mmproj files, and MTP
  drafter weights stay outside PalLLM release artifacts unless license,
  lineage, immutable revision/hash, and redistribution terms are captured in
  release evidence.
- `VerificationChecks[]` - promotion-proof checks for the lane: model-catalog
  presence, primary-source capability receipts, model-artifact provenance
  receipts, repeated PalLLM latency and fallback measurements, prefix-cache and
  chunked-prefill proof, structured-output parse stability, speculative
  decoding A/B results with accepted/proposed token ratio, strict JSON/tool-call
  no-spec proof before route promotion, libmtmd/mmproj smoke for GGUF
  multimodal lanes, media UUID cache proof, multimodal processor-cache memory
  measurements, cold/warm multimodal encoder-cache TTFT and cache-hit evidence,
  route-class replay proof for companion chat, vision describe, world-state
  extraction, screenshot proof loops, audio/ASR, and long proof/docs traffic
  before promoting any cache, scheduler, speculation, or routing setting,
  cache-salt isolation proof when configured, KV-cache dtype proof for
  `--kv-cache-dtype fp8` or `nvfp4` experiments, multi-replica cache-aware
  routing proof when a pool is used, vLLM performance-mode A/B proof for
  player-facing latency versus shared-server throughput, vLLM scheduler-cap
  proof with a short companion turn queued beside a long proof/docs prompt,
  vLLM request-priority proof when `PalLLM:Inference:RequestPriority` is
  configured, including lower-value queue precedence and no starvation of
  background proof/docs lanes,
  vLLM disaggregated prefill/decode proof comparing monolithic and split P/D
  p95 TTFT, p95 ITL, p95 E2E, queue pressure, KV-transfer failures, and
  worker-stop rollback,
  proof that `PalLLM:Inference:TokenBudgetField=max_completion_tokens`
  forwards the same route budget through `max_completion_tokens` while omitting
  `max_tokens`, and that the exact endpoint does not regress usage accounting,
  latency, or fallback counters,
  proof that vLLM `--generation-config vllm` is active when deterministic
  replay settings must not inherit model-repo sampling defaults, proof that
  endpoint-specific `PalLLM:Inference:TopK`, `MinP`, and
  `RepetitionPenalty` sampler fields improve style or repetition without
  parser, latency, or fallback regression, proof that
  `PalLLM:Inference:ParallelToolCalls=false` yields zero or one tool/action call before any
  parallel fan-out experiment, proof that endpoint-specific
  `PalLLM:Inference:StopSequences[]` delimiters reduce output tokens without
  clipping useful text, negative remote-media replay for localhost,
  RFC1918, link-local, IP-literal, and redirect-to-private probes,
  sampled vLLM KV-block residency proof for cache lifetimes,
  idle-before-evict, and reuse gaps, Mooncake Store proof for single-node
  offload and MultiConnector pools, including cache-hit gain, store/client
  health, companion p95, fallback counters, and master/client-stop rollback,
  vLLM sleep-mode proof
  for memory reclaimed, wake latency, deterministic fallback while the lane is
  asleep, precomputed
  embedding proof that valid tensors work while malformed shapes fail only in
  staging and do not take down PalLLM chat, LoRA adapter proof for base-model
  compatibility, local hash pinning, one-adapter routing, adapter-specific
  prefix-cache identity, missing-adapter fallback, and deterministic fallback
  on adapter load failure, Qwen3.6/Gemma 4 MTP A/B proof against n-gram or
  no-spec baselines with TTFT, ITL, acceptance rate, fallback behavior, and
  JSON/tool-call parse stability recorded, SGLang `--enable-metrics`
  qualification with cache-hit / token-usage / TTFT / ITL / queue-depth
  receipts, SGLang attention-backend and FP4/FP8 KV proof comparing
  auto-selection against only support-matrix-compatible pinned backends,
  SGLang EAGLE-3/adaptive/SpecV2 proof with route-local acceptance, OOM
  headroom, and no-spec parse-stability baselines, SGLang HiCache proof
  comparing radix-only, L2 host, optional L3 storage, and optional P/D profiles
  plus backend detach/stop rollback receipts, SGLang deterministic proof runs
  with the chosen attention backend before using the lane as judge/eval evidence,
  SGLang `json_schema` and
  `structural_tag` shape checks against PalLLM schemas, SGLang request
  dump/replay proof with only sanitized replay templates or hashes archived,
  llama.cpp prompt-cache
  and slot-count proof, llama.cpp quantized-KV proof against default f16 KV,
  accepted/generated token statistics for llama.cpp speculation, Ollama
  `ollama ps` residency/context receipts, cold-vs-warm `load_duration`, native
  usage timing fields, queue / 503 behavior, and Ollama KV-cache replay proof,
  SGLang Model Gateway retry counts, worker-scoped circuit-breaker transitions,
  token-bucket queue depth, cache-aware routing hit rate, TTFT/ITL, and PalLLM
  fallback activation,
  TensorRT-LLM `/health`, `/v1/models`, `/v1/chat/completions`, `/metrics`,
  YAML config hash, backend, tp/pp/ep size, `kvCacheStats`, speculation config,
  and multimodal malformed-media proof,
  plus realtime audio isolation checks when a lane advertises audio output.
  For Qwen Omni, text-only, text+audio-in, and text+audio-out paths must be
  proven independently; audio output needs a stable speaker configuration and a
  text mirror so PalLLM can still return a normal `ChatResponse`.
- `PromotionReceipts[]` - machine-readable records that must exist before a
  model-serving lane becomes a player default or a release recommendation.
  These receipts cover route-labeled replay, runtime capability handshakes,
  model-artifact provenance, package/redistribution decisions, GGUF
  prompt/state-cache canaries, vLLM scheduler/cache proof, disaggregated
  prefill/decode topology proof, Mooncake Store distributed KV-cache proof,
  SGLang sanitized replay proof, SGLang HiCache hierarchical KV proof,
  transformers serve / Foundry Local / OpenVINO / TensorRT-LLM readiness proof,
  speculation A/B proof, multimodal media-admission proof, and native
  audio/realtime fallback proof where those modalities apply.
- `MetricReceipts[]` - exact metric families or local proof records to capture
  before promoting a lane. Every lane names PalLLM's own `/metrics` receipts
  (`palllm_chat_duration_seconds`,
  `palllm_inference_recent_window_status`, `palllm_inference_lane_status`, and
  `palllm_fallback_reply_total`) plus a route replay receipt that preserves
  operation/budget labels and paired p50/p95 evidence instead of collapsing
  chat, vision, audio/ASR, and proof/docs lanes into one model score. vLLM
  lanes additionally name running/waiting request gauges, KV-cache pressure,
  prefix-cache counters, TTFT/ITL/e2e latency histograms, queue-time and
  swapped/preemption pressure receipts when exposed, cache config, optional
  KV-block residency histograms, P/D topology receipts for prefill/decode
  instance queue time and KV-transfer latency/failure logs, Mooncake Store health/cache-hit/failure
  receipts, sleep state, and multimodal cache counters.
  SGLang lanes name cache-hit,
  token-usage, running/queued request, TTFT, TPOT, e2e latency metrics,
  attention backend, page size, KV dtype, quantization/scaling, draft-model
  revision/hash, speculative topk/steps/draft-token caps, and HiCache receipts
  for `--enable-cache-report`, page size, host/storage budgets, prefetch/write
  policy, backend attach/detach, cold/warm latency, and route-labeled fallback
  counters, plus a local request dump/replay receipt that confirms raw dumps
  stayed out of public/support bundles.
  GGUF, Ollama, LM Studio, TensorRT-LLM, OpenVINO, Foundry Local, and
  transformers serve lanes list the equivalent local metrics, logs, or
  readiness receipts that make promotion reproducible.

For a live, operator-readable projection of those fields, run:

```powershell
pwsh ./pal.ps1 models serving
pwsh ./pal.ps1 models serving -Json
pwsh ./pal.ps1 models probe
pwsh ./pal.ps1 models probe -Json
```

Both commands are read-only. `pal models serving` calls
`/api/inference/collaboration`, filters by
`-ModelId` when requested, and prints startup hints, request hints, admission
caps, cache hints, security controls, promotion receipts, metric receipts, and
verification checks for each configured lane. `pal models probe` checks the
running model endpoint itself (`/health`, `/v1/models`, `/metrics`) and writes
`artifacts/model-probe/model-probe-*.json` with endpoint status, model ids, and
metric family names only. It sends no chat, image, audio, tool-call, or player
payload content.

For low-latency text lanes, qualify the cheap path first:

```bash
--performance-mode interactivity
--max-num-seqs <player-slot-count>
--max-num-batched-tokens <measured-short-turn-budget>
--max-num-partial-prefills 2
--max-long-partial-prefills 1
--long-prefill-token-threshold <measured-long-prompt-cutoff>
--speculative-config '{"method":"ngram","num_speculative_tokens":4,"prompt_lookup_min":2,"prompt_lookup_max":5}'
```

Use vLLM interactivity mode for the player-facing companion, vision, or
narration lane only after comparing it with `balanced` on the same PalLLM
replay set. Use suffix or draft/EAGLE speculation only after the exact model,
quant, and server version pass PalLLM's repeated-run schema and tool-call
qualification suite with accepted/proposed token ratio, end-to-end latency, and
parse stability recorded. Strict JSON, tool-call, judge, and save-replay routes
stay no-spec until each route has its own proof. On dual-GPU or larger workstations,
  disaggregated prefill can help tune time-to-first-token and tail inter-token
  latency separately, but it should stay an advanced topology choice after
  ordinary prefix caching and chunked prefill have been measured. For
  `MoRIIOConnector`, run read mode, write mode, and monolithic vLLM against the
  same PalLLM replay; reject the lane if TTFT regression outweighs ITL
  stability for the route. Capture
  monolithic-vs-split p95 TTFT, p95 ITL, p95 E2E, route labels, KV-transfer
backend, transfer failures, queue pressure, and decode-only rollback before a
P/D profile touches live companion chat. On SGLang,
radix cache and chunked prefill are the normal low-latency baseline; use
deterministic inference as a proof-lane setting for reproducible schema,
replay, and judge work, not as a reason to make model output a hard
dependency for `/api/chat`.

For TensorRT-LLM lanes, keep the first profile local and metrics-backed:

```bash
trtllm-serve serve Qwen/Qwen3-8B \
  --host localhost \
  --port 8000 \
  --backend pytorch \
  --tp_size 1 \
  --pp_size 1 \
  --ep_size 1 \
  --max_batch_size 8 \
  --max_num_tokens 4096 \
  --enable_chunked_prefill \
  --served_model_name Qwen/Qwen3-8B
```

Wire it with:

```powershell
pwsh ./pal.ps1 connect tensorrt -Model Qwen/Qwen3-8B -WriteConfig
```

Before promotion, prove `/health`, `/v1/models`, `/v1/chat/completions`, and
`/metrics`; record the served model name, backend, tp/pp/ep sizes, YAML config
hash, warm p50/p95, exact JSON/tool-call parse success, and deterministic
fallback activation. Use `/metrics` receipts for GPU memory, inflight batching,
`kvCacheStats`, and active requests. Treat TensorRT-LLM speculation
(`MTP`, `Eagle`, `NGram`, or `DraftTarget` in config YAML), Dynamo,
disaggregated serving, and multimodal media as separate proof lanes rather than
default player-path settings.

For GGUF / llama.cpp text lanes, use the native server flags instead of vLLM
`--speculative-config` JSON:

```bash
llama-server -m <model.gguf> -a pal-llamacpp \
  --host 127.0.0.1 --port <port> \
  -c <qualified-context> -np <player-slot-count> \
  -b <measured> -ub <measured> -ngl <measured> \
  --flash-attn on --cache-prompt --cache-reuse 256 -sps 0.10 \
  --metrics --no-webui

# Optional proof lanes only:
-cram <MiB>
-ctk q8_0 -ctv q8_0
--sleep-idle-seconds <seconds>
--spec-type ngram-simple --spec-draft-n-max 64
--spec-type ngram-mod --spec-ngram-mod-n-match 24 --spec-ngram-mod-n-min 48 --spec-ngram-mod-n-max 64
--spec-type draft-mtp --spec-draft-n-min <measured> --spec-draft-n-max <measured>
```

Wire it with:

```powershell
pwsh ./pal.ps1 connect llamacpp -ModelPath C:\Models\qwen.gguf -Model pal-llamacpp -WriteConfig
```

Promote none of those optional lanes from a benchmark alone. Record
`/health`, `/v1/models`, `/metrics`, second-turn latency, slot selection,
`-cram` pressure, active KV memory, accepted/generated token statistics, exact
JSON/tool-call parse success, and deterministic fallback behavior on PalLLM
replay traffic before changing the default model server recipe. Use
`--sleep-idle-seconds` only after wake latency and cold-after-wake fallback
behavior are recorded.

For Ollama GGUF lanes, keep the first profile boring and observable:

```bash
OLLAMA_CONTEXT_LENGTH=8192 OLLAMA_KEEP_ALIVE=24h ollama serve

# Optional memory-pressure proof only:
OLLAMA_FLASH_ATTENTION=1 OLLAMA_KV_CACHE_TYPE=q8_0 ollama serve
```

Use `OLLAMA_CONTEXT_LENGTH` for the actual PalLLM workload, not the largest
value the hardware can allocate. Ollama's current defaults scale by VRAM, but
large contexts increase memory and can slow ordinary companion turns. The proof
receipt is `ollama ps` showing the intended `CONTEXT` and GPU residency, plus
native response usage fields: `total_duration`, `load_duration`,
`prompt_eval_count`, `prompt_eval_duration`, `eval_count`, and `eval_duration`.
Keep `OLLAMA_NUM_PARALLEL=1` for the default one-player lane; raise it only
after p95 latency, queue / 503 behavior, and structured-output replay stay
healthy. `OLLAMA_KV_CACHE_TYPE=q8_0` can be a good memory lane after Flash
Attention is enabled; `q4_0` stays proof-only for PalLLM because multi-turn
coherence and strict JSON/tool-call parse stability matter more than maximum
context on the player path.

For Hugging Face `transformers serve` lanes, use it as the lightweight local
OpenAI-compatible bridge for evaluation, moderate load, and fast model
qualification before you decide whether vLLM or SGLang is worth the extra
operational surface:

```bash
python -m pip install --upgrade "transformers[serving]"
transformers serve Qwen/Qwen3.6-35B-A3B@<revision-sha> \
  --host localhost \
  --port 8002 \
  --continuous-batching \
  --dtype bfloat16
```

Wire it with:

```powershell
pwsh ./pal.ps1 connect transformers -Model Qwen/Qwen3.6-35B-A3B -Revision <revision-sha> -WriteConfig
```

Do not treat this as a free production promotion. Prove `/load_model` ends in
`ready`, `/v1/models` lists the pinned model, and `/v1/chat/completions`
handles PalLLM replay traffic before writing config on a player install.
Compare `--continuous-batching` against a non-batched local baseline for p50,
p95, short-request starvation, exact JSON/tool-call parse success, and
deterministic fallback activation. If you use the audio transcription endpoint,
keep it as an ASR proof lane until privacy, latency, and text-chat fallback
behavior are recorded.

For LM Studio lanes, use the local desktop/server path when the operator wants
a low-friction GGUF lane with a GUI, `lms` CLI automation, and OpenAI-compatible
chat completions:

```powershell
lms server start --port 1234
lms load <model-id> --gpu auto --context-length 8192 --identifier <stable-pal-model-id> --ttl 1800
lms ps
```

Wire it with:

```powershell
pwsh ./pal.ps1 connect lmstudio -Model <stable-pal-model-id> -WriteConfig
```

Before promotion, prove `/v1/models` lists the loaded model and
`/v1/chat/completions` handles PalLLM replay traffic with the same `ttl`,
structured JSON, and tool definitions the sidecar will send. Record `lms ps`
or server logs beside PalLLM p50/p95 latency so context length, GPU offload,
TTL, and auto-evict behavior are visible. Keep the server loopback-only; enable
CORS only for trusted local tools, and do not ship model weights or downloaded
artifacts without separate license review.

For Microsoft Foundry Local lanes, use the Windows ML / ONNX Runtime path as a
single-user local proof lane on Windows machines where the catalog has a
hardware-matched model alias. Foundry Local exposes an OpenAI-compatible REST
surface on a dynamic localhost endpoint, so always discover the endpoint first:

```powershell
winget install Microsoft.FoundryLocal
foundry model list --filter task=chat-completion
foundry model run <alias>
foundry service status
```

Wire it with:

```powershell
pwsh ./pal.ps1 connect foundry -FoundryEndpoint http://localhost:<port> -WriteConfig
```

Use `<endpoint>/v1/` for `PalLLM:Inference:BaseUrl`. The probe also checks the
service-root `/openai/models` catalog because Foundry Local reports cached or
loaded model ids there. Before promotion, prove `/openai/status`,
`/openai/models`, and `/v1/chat/completions`; record the selected execution
provider, first-use download/load behavior, warm p50/p95 latency, exact
JSON/tool-call parse success, and deterministic fallback activation. If you
try its audio transcription endpoint, keep it as an ASR proof lane until
privacy and latency are measured. For shared multi-client serving, keep vLLM
or SGLang as the production lane; Foundry Local is treated here as a local
client runtime and model-qualification path.

For OpenVINO Model Server lanes, use the `/v3` OpenAI-compatible surface when
the operator wants Intel CPU, GPU, or NPU proof without changing PalLLM's chat
client:

```powershell
pwsh ./pal.ps1 connect openvino -TargetDevice GPU -WriteConfig
```

The connector prints both an `ovms.exe` shape and a Docker shape using
`--task text_generation`, `--target_device`, and `--model_name`, then wires
`PalLLM:Inference:BaseUrl` to `http://localhost:<port>/v3/`. Before promotion,
prove `/v3/models` lists the configured model and `/v3/chat/completions`
handles PalLLM replay traffic. Record the selected target device, first-use
pull/compile time, warm p50/p95 latency, exact JSON/tool-call parse success,
and deterministic fallback activation. Keep NPU `PREFILL_HINT` /
`GENERATE_HINT` tuning and VLM media-domain settings in proof lanes until the
same Palworld screenshot and chat replays stay inside `HOT_PATH.md` budgets.

For Gemma 3n and Gemma 4 audio-input lanes, keep the first proof path
local and short:

```bash
transformers serve google/gemma-3n-E4B-it@<revision-sha> \
  --host localhost \
  --port 8002 \
  --continuous-batching \
  --dtype bfloat16
```

Normalize player clips to mono 16 kHz and cap them at 30 seconds before they
reach the model server. For Gemma 3n, prove PLE caching and conditional
parameter loading separately from text KV cache, and bypass audio/vision
parameters on text-only companion turns when the serving stack supports it.
For Gemma 4, prove native audio-in against cascaded ASR-to-text before using it
for player speech. Record the normalized duration and audio-token estimate
beside the clip hash; current Gemma audio guidance budgets Gemma 4 at `25`
audio tokens per second, so a 30-second clip costs about `750` audio tokens
before the route adds text, system, and output headroom. Gemma 3n remains
`6.25` audio tokens per second, or about `188` tokens for 30 seconds. In both
cases, PalLLM's typed text chat remains the fallback-grade contract.

For Qwen Omni lanes, keep audio output isolated from the normal companion text
lane:

```bash
vllm serve Qwen/Qwen3-Omni-30B-A3B-Instruct --omni --port 8091
```

For realtime WebSocket proof on vLLM-Omni, record the deploy config and keep
`async_chunk` disabled. Current Qwen3-Omni serving docs explicitly warn that
`/v1/realtime` is unsupported while `async_chunk` is enabled, so a generic
"server started" receipt is not enough for PalLLM voice promotion.

For streaming video proof on vLLM-Omni, keep `/v1/video/chat/stream` separate
from `/api/chat` and from the realtime voice lane. The receipt must include
the frame cadence, duration cap, optional PCM16 audio chunk policy,
reconnect/stall behavior, and fallback to a still image or current world-state
snapshot before any live Palworld stream is promoted.

For vLLM-Omni video generation, keep `/v1/videos` and `/v1/videos/sync` off
the live companion path. Those routes create or benchmark diffusion video jobs;
they are useful only as offline release-walkthrough or proof-bundle material
after prompt-publication hygiene, async job polling, output storage cleanup,
cancellation, and no-interference with `/api/chat` and `/api/vision` are
proven.

Use `/v1/chat/completions` with
`InferencePrompt.Modalities=["text","audio"]` and prompt-level
`InferencePrompt.Audio` only in a voice proof profile, configure the speaker
explicitly, preserve returned audio on `InferenceResult.AudioJson`, and verify
the response has a text mirror before any audio is played in-game. For audio,
image, or video input canaries on the same family, use
`InferencePrompt.UserContent` content parts only after media-admission and
fallback proof accepts the exact request shape.
Qwen3.5-Omni is useful current research context, but it is not PalLLM
promotion evidence by itself; do not write appsettings for it until there is a
concrete local model artifact or provider-compatible endpoint to prove.

For Qwen3.6 / Qwen3-Next-style lanes, qualify model-native MTP separately:

```bash
--reasoning-parser qwen3
--speculative-config '{"method":"qwen3_next_mtp","num_speculative_tokens":2}'
```

For the low-concurrency latency profile, qualify the vLLM MTP-1 shape
separately:

```bash
--reasoning-parser qwen3
--speculative-config '{"method":"mtp","num_speculative_tokens":1}'
--no-enable-prefix-caching
```

Keep the first MTP pass conservative. Run it on PalLLM replay traffic against
the same model without speculation and against the existing n-gram path; record
TTFT, ITL, accepted/proposed ratio, parser stability, and deterministic
fallback behavior before promotion. Compare prefix-cache-off MTP-1 against the
normal prefix-cache profile instead of assuming one mode dominates all PalLLM
workloads. For a Qwen3.6 text-only lane, use `--language-model-only` and keep
screenshot/video work on a separate multimodal server.

If a model family can do both model-native MTP and image/video/audio input,
keep the text-MTP profile and the multimodal profile on separate server
processes or ports until the exact runtime build passes a same-process negative
canary. Do not send `image_url`, `video_url`, `input_audio`, or `audio_url`
content parts to a text-only MTP endpoint; prove text-MTP, media no-spec, and
any future media-speculative profile as separate lanes with separate KV/cache
receipts.

If the same model family also advertises image, video, or audio input, do not
promote those routes from a text-only MTP win. Run modality-isolated replays:
plain text chat, Palworld screenshot/image, video summary, and audio-in or ASR
cases each need their own no-spec, n-gram, and model-native speculation
comparison before a multimodal lane becomes player-facing.

For Qwen3.6 / Qwen3-Next-style low-concurrency latency lanes, current vLLM
recipes recommend MTP-1 with prefix caching disabled. PalLLM therefore treats
that as a separate proof profile: compare `--no-enable-prefix-caching` MTP-1
against the normal prefix-cache lane, a no-spec baseline, and the existing
n-gram path before changing any player-facing default. The normal prefix-cache
profile may still win prompt-heavy, shared, or docs-sync traffic.

For Qwen3.6 long-context lanes, keep context identity in the receipt instead
of assuming all Qwen3.6 servers are equivalent. Native 262,144-token context,
1,010,000-token extended context, hosted catalog context, and reduced-context
GGUF profiles need separate cache namespaces and separate PalLLM replays. The
receipt should include served model id, runtime context cap, extension flags,
KV/state memory, route token budget, p95 latency, exact parse success, and
fallback counters.

For Gemma 4 lanes, MTP uses matching Gemma 4 assistant/drafter weights with
`method=mtp`; do not wire those assistant checkpoints as a generic
`draft_model` speculative profile. Treat the drafter as a qualified local
artifact: it needs assistant checkpoint id or hash, target-model lineage,
runtime version, token depth, acceptance-rate, latency, parser, and fallback
proof before it can serve player-facing turns. Disable prefix caching for the
benchmark pass when you need consistent measurement, then rerun with the
intended serving cache profile before promotion. Gemma 4 can advertise vision,
audio, structured thinking, and tool-call support; PalLLM should expose those
as lane capabilities, not as default hot-path requirements. Route player speech
through cascaded ASR, a Gemma 4 lane, or a Qwen Omni lane only after an exact
audio-capable artifact, runtime canary, token-budget receipt, and fallback
proof are checked.

Current multimodal-speculation research reinforces the same rule: throughput
speedups are not enough, and text-only speculative methods can behave
differently once vision tokens are involved. For PalLLM, a speculative lane is
therefore route-scoped evidence, not a global model setting.

Promotion evidence should be boring and local. For every lane, capture the
catalog proof, a representative PalLLM replay, and before/after latency
measurements. Also capture the artifact receipt: model card license metadata,
base model or adapter relation, exact revision/commit or local SHA-256, weight
format, runtime and tokenizer revisions, `trust_remote_code` status, and a
clear redistribution decision. Replay route classes separately: companion chat, vision describe,
world-state extraction, screenshot proof loops, audio/ASR, and long proof/docs
traffic are different budgets. For llama.cpp / GGUF servers, capture a
state-cache canary before host prompt-cache promotion: the same PalLLM prefix
on the same slot should avoid unexpected full prefill, and template/context/
adapter/model changes should invalidate cleanly. For vLLM-style servers, verify
that the second same-prefix turn improves through cache or prefill metrics
before claiming a cache win. If `--performance-mode interactivity` is enabled,
compare p50/p95
end-to-end latency, TTFT, ITL, queue behavior, and fallback activation against
`balanced` and `throughput` before changing a shared server default. If
`PrefixCacheSalt` is configured, verify matching salts reuse cached prefixes and
different salts do not. If `--kv-cache-dtype fp8` or `nvfp4` is enabled, replay
PalLLM JSON, tool-call, companion, and long-context proof turns against the same
  lane using `auto` KV cache first; promote only if exact parse success, quality,
  TTFT, ITL, KV-cache utilization, and fallback behavior stay healthy. If several
  sparse-MoE vLLM DBO runs are tested, keep their no-DBO baseline,
  decode/prefill thresholds, DP/EP topology, all2all backend, microbatch counts,
  TTFT/ITL/E2E deltas, queue/preemption pressure, and PalLLM fallback counters
  separate from prefix/KV cache evidence.
  If several
  vLLM lanes are tested against repo-hosted model defaults, record whether
  `--generation-config vllm` is active before comparing deterministic replay
results. If `PalLLM:Inference:Seed` is configured, replay the exact request
twice on the same replica and record the seed, served model id, runtime version,
system fingerprint if exposed, TP/PP layout, and output drift before trusting
reproducibility. PalLLM preserves the latest exposed `system_fingerprint` on
the `/api/inference/performance` lane snapshot so seed drift can be reviewed
beside latency and token receipts. If
`PalLLM:Inference:TokenBudgetField=max_completion_tokens` is configured, record
a same-route comparison against `max_tokens` so visible/reasoning token usage,
p95 latency, and fallback counters stay comparable. For strict directive/action routes, record a `PalLLM:Inference:ParallelToolCalls=false`
proof run that returns zero or one call before allowing a multi-call fan-out
experiment. For strict delimiter routes, record a `PalLLM:Inference:StopSequences[]`
proof run with before/after token receipts and a clipped-text review before
using delimiters as a player-facing latency optimization. If several
vLLM receipts are available, prefer concrete Prometheus families over prose:
`vllm:prefix_cache_queries` / `vllm:prefix_cache_hits` for local prefix reuse,
`vllm:external_prefix_cache_queries` /
`vllm:external_prefix_cache_hits` for connector-backed cross-instance reuse,
`vllm:kv_cache_usage_perc` and sampled KV-block residency histograms for cache
pressure, TTFT/ITL/e2e histograms for latency, and `vllm:engine_sleep_state`
when an idle VRAM reclaim profile is active. KV-event ZMQ receipts are useful
only as local redacted proof for block-store/remove behavior and router-index
integrity; do not archive raw event payloads. If vLLM preempts or recomputes
requests under KV pressure during a mixed short-turn plus long-proof replay,
do not promote that player-facing lane unless p95 latency and fallback
activation stay healthy. Long idle-before-evict or reuse-gap evidence on
proof/docs prompts is a warning that those prompts can strand cache or evict
the live companion prefix. If several
replicas sit behind a router, compare
round-robin against sticky or KV cache-aware routing and record cache-hit rate,
TTFT, ITL, and fallback behavior before using that pool for live turns. For
multimodal lanes, send full local media bytes before relying on media UUID
replay. For vLLM/LMCache encoder-cache experiments, record cold vs warm media
TTFT and check vLLM MM cache-hit metrics or LMCache EC log evidence before
claiming a screenshot, video, or audio cache win. If testing
`--enable-mm-embeds`, compare ordinary media bytes against precomputed
embeddings on the same model, record VRAM/latency deltas, and keep the lane
private until malformed-shape isolation is proven. For realtime audio, prove
`/v1/realtime` can stall or reconnect without blocking `/api/chat`; on
vLLM-Omni Qwen Omni lanes, include the `async_chunk`-disabled deploy receipt
beside `session.created`, audio delta, transcript delta, and reconnect proof.
For streaming video, prove `/v1/video/chat/stream` can reconnect or fail back
to still-image/world-state proof without blocking ordinary text chat.
Keep speculative-decoding evidence modality-isolated in the same way: a text
prefix/KV-cache or MTP win does not prove media UUID, multimodal encoder-cache,
audio-token, parser, or fallback behavior.

For personality-adapter experiments, keep the feature local and boring:

1. Train or obtain the adapter outside PalLLM.
2. Stage it under an operator-approved runtime directory with a content hash.
3. Start vLLM with `--enable-lora --max-loras 1` and static, known adapter
   mappings first.
4. Replay the same PalLLM turns with no adapter, the intended adapter, and a
   missing-adapter case.
5. Promote only after latency, parse stability, cache identity, and fallback
   behavior are recorded.

Dynamic adapter load/unload is an admin operation, not a player feature. Leave
`VLLM_ALLOW_RUNTIME_LORA_UPDATING` off unless the endpoint is loopback-only and
the adapter path is hash-pinned. Do not add online training or automatic
adapter updates to the default hot path.

### Fast lane

Use the fast lane for:

- bridge-log and route-surface triage
- screenshot review loops
- narrow implementation drafts
- documentation drift patrol
- shadow smoke checks for fresh quants

Do not treat the fast lane as the final authority for high-risk changes.

### Deliberate lane

Use the deliberate lane for:

- acceptance criteria and file-scope decisions
- release-facing review
- bridge compatibility review
- HUD or subtitle seam review
- final documentation/code alignment decisions
- promotion or rollback decisions for fresh quants

## Recommended recipes

### Fast draft, dense judge

Use when the task is a narrow PalLLM fix.

1. Fast lane scouts files and drafts the change.
2. Deterministic validators run immediately.
3. Deliberate lane reviews only if the change is medium risk, release-facing, or the validators raise doubt.

### Dense plan, fast execute, dense audit

Use when the task touches bridge contracts, HUD seams, documentation drift, or release-facing behavior.

1. Deliberate lane writes the contract and files in scope.
2. Fast lane implements the approved plan.
3. Validators run.
4. Deliberate lane signs off or blocks.

### Watchdog and repair

Use for unattended or recurring hygiene.

1. Fast lane monitors bridge health, documentation drift, route drift, or failing tests.
2. Deliberate lane wakes only when there is a real anomaly.
3. Fast lane rechecks the repair after it lands.

## Hardware guidance

### CPU-only or very low memory

- Keep one heavyweight model loaded at a time.
- Use the fast lane for interactive work.
- Wake the deliberate lane only for short audit windows.
- Keep context short.

### 16 GB VRAM or 24-32 GB unified memory

- Treat this as a sequential duo tier.
- Keep the fast lane loaded for everyday bridge and documentation work.
- Swap to the deliberate lane for review, release, or native-seam decisions.

### 24-32 GB VRAM

- This is the best single-accelerator PalLLM tier.
- Keep the fast lane resident most of the time.
- Batch deliberate review work so you do not thrash the card with constant swaps.

### 48 GB+ VRAM or dual GPU

- Separate endpoints are preferred.
- Use the fast lane for concurrent bridge, screenshot, and watchdog tasks.
- Use the deliberate lane as the review and promotion gate.

## Per-architecture quantization defaults

The `HardwareProfiler` reports `RecommendedQuantization` based on
detected GPU architecture. Operators can override; the defaults
follow the matrix in [`QUANTIZATION.md`](QUANTIZATION.md):

| Detected architecture | `RecommendedQuantization` | Recommended inference server | Why |
|---|---|---|---|
| Blackwell (RTX 50, B100/B200, GB200) | `nvfp4` | vLLM or TensorRT-LLM | Native FP4 tensor cores; 2× speed of FP8 at near-FP16 quality |
| AMD Instinct CDNA3/CDNA4 (MI300/MI325/MI350) | `mxfp4` | vLLM on ROCm | Standards-based FP4 path; validate exact backend/model coverage before promoting it |
| Hopper (H100/H200) | `fp8` | vLLM or TensorRT-LLM | Native FP8 tensor cores |
| Ada (RTX 40, L40) | `fp8` | vLLM | Native FP8 tensor cores |
| Ampere (RTX 30, A100) | `q4_k_m` | Ollama or llama.cpp (or vLLM AWQ-INT4) | No FP8/FP4 hardware |
| Older NVIDIA / AMD consumer / Apple Silicon | `q4_k_m` | Ollama or llama.cpp | Best cross-platform choice |
| CPU only | `q4_k_m` (small model) | Ollama / llama.cpp | Only practical CPU path |

For a PalLLM operator with a Blackwell box (RTX 5090, B200), the
recommended stack is:

```
vLLM 0.6+
  --model nvidia/Llama-3.3-70B-Instruct-FP4   (or Qwen3-Coder-NVFP4)
  --quantization fp4
  --max-model-len 8192          (validate quality before extending)

PalLLM
  Inference:Enabled = true
  Inference:BaseUrl = http://127.0.0.1:11434/v1/   (vLLM bound there)
  Inference:Model   = nvidia/Llama-3.3-70B-Instruct-FP4
```

Operators on Hopper, Ada, or Ampere should swap NVFP4 for the
native format their architecture supports. Auto-detection uses bounded
Linux procfs probes and sanitized Windows display-adapter registry strings;
setting `PALLLM_GPU_ARCHITECTURE=blackwell` (or `hopper` / `ada` /
`ampere`) still hints the profiler when the exact chip cannot be determined.

## Thinking, vision, audio, and context

PalLLM is partly optimized here, not completely.

### Thinking

- Text chat lanes are role-aware.
- Fast lanes default to lower-latency profiles and often run with thinking off.
- Deliberate lanes use thinking more often and may request preserved thinking.

### Vision

- Vision is already useful for Palworld screenshots.
- Chat augmentation stays terse.
- World-state extraction uses low temperature plus structured output.
- Screenshot work should stay tied to Palworld scene analysis, not generic media generation.

### Audio

- Audio is still not a first-party mic-capture path.
- Voice selection for TTS is configurable.
- ASR, native audio-in, and Qwen Omni audio-out now exist as proof-gated
  serving lanes through `Capability.ServingProfile`; they do not replace the
  typed-text chat contract until privacy, latency, and deterministic fallback
  proof exists.

### Context

- PalLLM now enforces per-lane prompt and evidence budgets during prompt assembly.
- PalLLM still does not hard-enforce the upstream server's real context window per request.
- Retrieval and evidence packs are preferred over blindly widening context.

## Validation rules

PalLLM should trust validators more than model confidence.

For model collaboration work, the important checks are:

- targeted tests
- diff-scope validation
- OpenAPI and doc drift checks
- screenshot or dashboard verification for visual tasks
- bridge and release-readiness review for player-facing changes

Fresh quants should stay shadow-only until they pass the qualification suite.

## API and MCP surfaces

PalLLM exposes the collaboration contract on:

- `GET /api/inference/collaboration`
- `POST /api/inference/collaboration/plan`
- MCP tool: `pal_model_collaboration`
- MCP tool: `pal_plan_model_collaboration_task`
- MCP resource: `palllm://model/collaboration`
- MCP prompt: `palllm_model_collaboration_orchestrator`

Those surfaces should be used for PalLLM runtime, bridge, HUD, screenshot,
documentation-sync, and release-hardening tasks. If a prompt or plan starts drifting
into generic asset, video, or product-studio work, it is out of scope.

## Sources

Primary sources:

- [Qwen3.6-27B model card](https://huggingface.co/Qwen/Qwen3.6-27B)
- [Qwen3.6-35B-A3B model card](https://huggingface.co/Qwen/Qwen3.6-35B-A3B)
- [Qwen3.6 GitHub repo](https://github.com/QwenLM/Qwen3.6)
- [Qwen3-Omni GitHub repo](https://github.com/QwenLM/Qwen3-Omni)
- [Qwen3.5-Omni technical report](https://arxiv.org/abs/2604.15804)
- [Gemma 3n model overview](https://ai.google.dev/gemma/docs/gemma-3n)
- [Gemma audio understanding](https://ai.google.dev/gemma/docs/capabilities/audio)
- [Gemma video understanding](https://ai.google.dev/gemma/docs/capabilities/vision/video-understanding)
- [vLLM Qwen3-Next usage guide](https://docs.vllm.ai/projects/recipes/en/latest/Qwen/Qwen3-Next.html)
- [vLLM Qwen3.5 / Qwen3.6 usage guide](https://docs.vllm.ai/projects/recipes/en/latest/Qwen/Qwen3.5.html)
- [vLLM MTP speculative decoding](https://docs.vllm.ai/en/latest/features/speculative_decoding/mtp/)
- [vLLM Gemma 4 usage guide](https://docs.vllm.ai/projects/recipes/en/stable/Google/Gemma4.html)
- [Google Gemma 4 MTP drafter announcement](https://blog.google/innovation-and-ai/technology/developers-tools/multi-token-prediction-gemma-4/)
- [Unsloth Qwen3.6-27B-GGUF](https://huggingface.co/unsloth/Qwen3.6-27B-GGUF)
- [Unsloth Qwen3.6-35B-A3B-GGUF](https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF)
- [Unsloth Qwen run and fine-tune guide](https://unsloth.ai/docs/models/qwen3-how-to-run-and-fine-tune)
- [Unsloth Dynamic 2.0 GGUFs](https://unsloth.ai/docs/basics/unsloth-dynamic-2.0-ggufs)
- [Ollama context length](https://docs.ollama.com/context-length)
- [Ollama structured outputs](https://docs.ollama.com/capabilities/structured-outputs)
- [Ollama FAQ: preload, keep alive, concurrency, Flash Attention, and KV cache](https://github.com/ollama/ollama/blob/main/docs/faq.mdx)
- [Ollama API usage metrics](https://docs.ollama.com/api/usage)
- [Ollama environment configuration](https://github.com/ollama/ollama/blob/main/envconfig/config.go)
- [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- [llama.cpp speculative decoding](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- [llama.cpp multimodal support](https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md)
- [llama.cpp grammar docs](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md)
- [llama.cpp MTP context-shift issue](https://github.com/ggml-org/llama.cpp/issues/22867)
- [llama.cpp SWA prompt-cache fix PR](https://github.com/ggml-org/llama.cpp/pull/21749)
- [llama.cpp model management](https://huggingface.co/blog/ggml-org/model-management-in-llamacpp)
- [vLLM automatic prefix caching](https://docs.vllm.ai/en/latest/design/prefix_caching/)
- [vLLM production metrics](https://docs.vllm.ai/en/stable/design/metrics/)
- [vLLM KV events example](https://docs.vllm.ai/en/stable/examples/features/kv_events/)
- [vLLM KV events config](https://docs.vllm.ai/en/stable/api/vllm/config/kv_events/)
- [vLLM Production Stack KV cache-aware routing](https://docs.vllm.ai/projects/production-stack/en/latest/use_cases/kv-cache-aware-routing.html)
- [vLLM Router](https://vllm.ai/blog/vllm-router-release)
- [NVIDIA Dynamo multimodal KV routing](https://docs.nvidia.com/dynamo/dev/user-guides/multimodal/multimodal-kv-routing)
- [vLLM multimodal inputs](https://docs.vllm.ai/en/latest/features/multimodal_inputs/)
- [vLLM structured outputs](https://docs.vllm.ai/en/latest/features/structured_outputs/)
- [vLLM OpenAI-compatible server](https://docs.vllm.ai/en/stable/serving/openai_compatible_server/)
- [vLLM speculative decoding](https://docs.vllm.ai/en/latest/features/speculative_decoding/)
- [vLLM LoRA adapters](https://docs.vllm.ai/en/stable/features/lora.html)
- [vLLM serve arguments](https://docs.vllm.ai/en/latest/cli/serve/)
- [vLLM quantized KV cache](https://docs.vllm.ai/en/latest/features/quantization/quantized_kvcache/)
- [vLLM multimodal processor benchmark / cache flags](https://docs.vllm.ai/en/latest/cli/bench/mm_processor/)
- [vLLM security guidance for media URLs](https://docs.vllm.ai/en/latest/usage/security/)
- [vLLM sleep mode](https://docs.vllm.ai/en/stable/features/sleep_mode/)
- [vLLM disaggregated prefilling connectors](https://docs.vllm.ai/en/latest/features/disagg_prefill/)
- [vLLM MoRIIO single-node P/D](https://vllm.ai/blog/2026-04-07-moriio-kv-connector)
- [vLLM MoRIIO connector API](https://docs.vllm.ai/en/latest/api/vllm/distributed/kv_transfer/kv_connector/v1/moriio/moriio_connector/)
- [vLLM FlexKV connector API](https://docs.vllm.ai/en/latest/api/vllm/distributed/kv_transfer/kv_connector/v1/flexkv_connector/)
- [vLLM / PegaFlow external KV cache](https://vllm.ai/blog/2026-05-18-pegaflow)
- [LMCache encoder cache](https://docs.lmcache.ai/non_kv_cache/encoder_cache.html)
- [llama.cpp multimodal docs](https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md)
- [SGLang server arguments](https://docs.sglang.io/docs/advanced_features/server_arguments)
- [SGLang documentation overview](https://docs.sglang.io/index.html)
- [SGLang structured outputs](https://docs.sglang.io/docs/advanced_features/structured_outputs)
- [SGLang deterministic inference](https://docs.sglang.io/docs/advanced_features/deterministic_inference)
- [SGLang attention backend matrix](https://docs.sglang.io/docs/advanced_features/attention_backend)
- [SGLang speculative decoding](https://docs.sglang.io/advanced_features/speculative_decoding.html)
- [SGLang adaptive speculative decoding](https://docs.sglang.io/advanced_features/adaptive_speculative_decoding.html)
- [SGLang observability, request dump/replay, and crash replay](https://docs.sglang.io/docs/advanced_features/observability)
- [SGLang production metrics](https://docs.sglang.io/docs/references/production_metrics)
- [SGLang HiCache best practices](https://docs.sglang.io/docs/advanced_features/hicache_best_practices)
- [SGLang HiCache design](https://docs.sglang.io/docs/advanced_features/hicache_design)
- [SGLang PD Disaggregation](https://docs.sglang.io/docs/advanced_features/pd_disaggregation)
- [TensorRT-LLM `trtllm-serve`](https://nvidia.github.io/TensorRT-LLM/commands/trtllm-serve/trtllm-serve.html)
- [TensorRT-LLM speculative decoding](https://nvidia.github.io/TensorRT-LLM/1.2.0rc6/features/speculative-decoding.html)
- [TensorRT-LLM disaggregated serving](https://nvidia.github.io/TensorRT-LLM/1.2.0rc6/features/disagg-serving.html)
- [NVIDIA Dynamo TensorRT-LLM backend](https://docs.dynamo.nvidia.com/dynamo/components/backends/tensor-rt-llm)
- [Hugging Face transformers serve CLI](https://huggingface.co/docs/transformers/main/serving)
- [Hugging Face transformers tool use](https://huggingface.co/docs/transformers/main/en/chat_extras)
- [OpenVINO Model Server LLM quickstart](https://docs.openvino.ai/2026/model-server/ovms_docs_llm_quickstart.html)
- [OpenVINO Model Server GenAI clients](https://docs.openvino.ai/2026/model-server/ovms_docs_clients_genai.html)
- [OpenVINO Model Server NPU text generation](https://docs.openvino.ai/2026/model-server/ovms_demos_llm_npu.html)
- [OpenVINO GenAI NPU performance hints](https://docs.openvino.ai/2026/openvino-workflow-generative/inference-with-genai/inference-with-genai-on-npu.html)
- [Microsoft Foundry Local architecture](https://learn.microsoft.com/en-us/azure/ai-foundry/foundry-local/concepts/foundry-local-architecture)
- [Microsoft Foundry Local CLI reference](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-cli)
- [Microsoft Foundry Local REST API reference](https://learn.microsoft.com/en-us/azure/foundry-local/reference/reference-rest)
- [vLLM-Omni Qwen3-Omni serving example](https://docs.vllm.ai/projects/vllm-omni/en/latest/user_guide/examples/online_serving/qwen3_omni/)
- [vLLM-Omni Speech API](https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/speech_api/)
- [vLLM-Omni streaming video input API](https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/video_stream_api/)
- [vLLM-Omni Videos API](https://docs.vllm.ai/projects/vllm-omni/en/latest/serving/videos_api/)
- [vLLM streaming/realtime overview](https://vllm.ai/blog/streaming-realtime)
- [Hugging Face model cards](https://huggingface.co/docs/hub/en/model-cards)
- [Hugging Face pickle scanning](https://huggingface.co/docs/hub/main/security-pickle)
- [Hugging Face TGI model safety](https://huggingface.co/docs/text-generation-inference/basic_tutorials/safety)

Research context:

- [ContextBench: context retrieval in coding agents](https://arxiv.org/html/2602.05892v3)
- [MMSpec: Benchmarking Speculative Decoding for Vision-Language Models](https://arxiv.org/abs/2603.14989)
- [SWE Context Bench](https://arxiv.org/abs/2602.08316)
