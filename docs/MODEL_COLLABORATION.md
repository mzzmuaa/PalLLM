# Local Model Collaboration

Last audited: `2026-06-03`

> **Quantization choice → see [`QUANTIZATION.md`](QUANTIZATION.md)** for
> the full NVFP4 / MXFP4 / FP8 / Q4_K_M / Q8_0 matrix with community
> sentiment and per-architecture defaults. This doc focuses on the
> *role pairing* — quantization is the layer below.

> **Engine posture (Pass 436):** PalLLM has exactly **one local inference
> engine — llama.cpp `llama-server`**, wired with `pal connect llamacpp`.
> Models that cannot run as a local GGUF on the operator's hardware route
> through the **OpenAI-compatible cloud escape path** (`pal connect cloud`)
> instead. Both speak `/v1/chat/completions`, so every serving hint in this
> doc targets a single local llama-server or that cloud fallback — there is
> no multi-backend recommendation surface. The alt-engine connectors
> (vLLM, SGLang, TensorRT-LLM, OpenVINO, Foundry Local, LM Studio,
> transformers serve, Ollama) were removed in Pass 436; their serving-profile
> guidance and detection branches went with them.

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

Both lanes are served by the same bundled llama-server (swap the loaded GGUF,
or run two llama-server processes on separate ports), or by the cloud escape
endpoint on below-reference hardware.

## What each lane should own

Every lane in `GET /api/inference/collaboration` carries a `Capability` block
so operators do not have to reverse-engineer model fit from the tag string.
The block is deterministic and local-only: it records family, recommended
backend, input/output modalities, vision/video/audio support,
structured-output/tool-call/speculative-decoding fit, a nested
`ServingProfile`, the precise `Speculation` mode profile, serving
optimizations, promotion receipts, metric receipts, and runtime guards.

Use it as a routing sanity check:

- Screenshot or video work needs `InputModalities` containing `image` or
  `video`; otherwise route through `PalLLM:Vision` or a Media role.
- Audio-in and realtime voice remain opt-in lanes; a model advertising audio
  capability does not change the text chat fallback contract.
- Qwen Omni speech synthesis remains a separate TTS proof lane: the llama.cpp
  talker/code2wav `/v1/audio/speech` endpoint must prove voice, response
  format, optional `PalLLM:Tts:Speed`, output MIME, duration, latency,
  playback receipts, and text fallback before promotion.
- The primary companion lane stays stateless `/v1/chat/completions`. Newer
  stateful `/v1/responses` surfaces stay proof-only until their event names,
  tool events, usage receipts, and fallback counters are replayed.
- Strict JSON/tool-call work should use the structured-output hint
  (`InferencePrompt.ResponseFormat` for text lanes, or the vision world-state
  schema hook for image lanes) and keep speculative decoding behind
  qualification tests.
- Multimodal lanes should prefer local base64 media, stable media UUIDs for
  repeated proof replays, explicit server-side media limits/allowlists, and
  endpoint-proven processor caps (`mm_processor_kwargs`) only after replay
  shows better TTFT/VRAM without parse or fallback regressions.
- `Capability.Speculation` splits speculation into n-gram, draft-model, and
  model-native MTP readiness so tools do not treat one broad capability bit as
  a green light for every route.
- Model artifact provenance is part of lane promotion. A downloaded model,
  quant, adapter, mmproj, or drafter is not a PalLLM default until the operator
  has recorded source URL or local path, immutable revision or file hash,
  model-card license metadata, base-model/adapter relation, weight format,
  runtime/tokenizer revision, `trust_remote_code` status, and whether
  redistribution is allowed.
- Capability claims need their own receipt. Do not infer vision, audio,
  tool-call, realtime, or MTP readiness from a family name alone; record the
  primary model-card/vendor-doc revision, the local `/v1/models` catalog
  identity, the enabled launch flags, positive canaries for claimed
  capabilities, and negative canaries for unsupported modalities.

### The ServingProfile contract

`Capability.ServingProfile` makes those rules executable by tools instead of
leaving them as prose. Because llama.cpp is the only local engine, the profile
resolves to one of a small set of profile ids and two preferred runtimes:

- `ProfileId` is one of `gguf-chat`, `gguf-libmtmd-multimodal`,
  `omni-realtime-opt-in`, `embedding-retrieval`, or `cloud-openai-chat`.
- `RequestProtocol` is `OpenAI-compatible /v1/chat/completions` (or
  `/v1/embeddings` for retrieval lanes, plus a separate `/v1/audio/speech`
  lane when audio output is advertised).
- `PreferredRuntime` is either `llama.cpp llama-server (the bundled, only local
  engine)` or `an OpenAI-compatible cloud API via pal connect cloud`.

The profile then carries these arrays:

- `StartupHints[]` — the local llama-server launch line
  (`--host 127.0.0.1 --port <port> -c <ctx> -np <slots> -b/-ub/-ngl <measured>
  --flash-attn on --metrics --no-webui`), prompt-cache and state-cache canary
  lanes (`--cache-prompt`, `--cache-reuse`, `-sps`, `-cram`, `--swa-full`,
  `--slot-save-path`), idle-memory (`--sleep-idle-seconds`) and KV-memory
  (`-ctk`/`-ctv`) proof lanes, the `pal connect llamacpp` connector lane, GGUF
  grammar / `response_format` schema conversion, GGUF artifact provenance,
  `--mmproj` vision/audio projectors, native llama.cpp speculation
  (`--spec-type ngram-simple` / `ngram-mod` / proof-only `draft-mtp` with
  `--spec-draft-*` / `--spec-ngram-mod-*` flags), Qwen Omni GGUF speech
  (`--talker-model` / `--code2wav-model`), Gemma 3n edge tuning, and the
  `pal connect cloud` escape lane with its prompts-leave-the-machine privacy
  note.
- `Speculation` — machine-readable flags for `SupportsNgramSpeculation`,
  `SupportsDraftModelSpeculation`, `SupportsModelNativeMtp`,
  `RequiresModalityIsolatedProof`, and `RequiresPrefixCacheOffForLatencyMtp`,
  plus the recommended first mode and the promotion guard. Qwen3.6 local GGUF
  lanes report model-native MTP after replay proof; Gemma 4 lanes report a
  matching-Gemma-4-drafter mode.
- `RequestHints[]` — keep PalLLM text chat on `/v1/chat/completions` as the
  deterministic-fallback backstop, plus the optional OpenAI-compatible request
  knobs through their `PalLLM:Inference:*` config keys: `Seed`,
  `TokenBudgetField` / `max_completion_tokens`, `FrequencyPenalty`,
  `TopK` / `MinP` / `RepetitionPenalty`, `StopSequences`, the prompt-level
  `InferencePrompt.ResponseFormat` / `StructuredOutputs` / `Tools` /
  `ToolChoice` / `Prediction` / `Logprobs` / `Modalities` / `Audio` /
  `UserContent` proof hooks, the llama.cpp-only `cache_prompt` / `id_slot` /
  `n_cache_reuse` canaries, and model-native Qwen3.6 / Gemma 4 MTP guidance.
  Ordinary companion chat omits every optional field so strict endpoints stay
  portable.
- `CacheHints[]` — stable-prefix guidance, JSON-schema request shaping,
  content-hash media UUIDs for repeated screenshots/proof replays, optional
  `PalLLM:Inference:PrefixCacheSalt` (`cache_salt`) trust-domain isolation,
  optional proof-gated `PromptCacheKey` / `PromptCacheRetention` for hosted
  prompt-cache routing on the cloud escape lane, and a reminder not to send
  uuid-only media until the same server process has proven the cache entry
  exists.
- `AdmissionControls[]` and `SecurityControls[]` — default image/video/audio
  caps plus local-first media rules; llama-server lanes keep `-np` capped to
  the real player/session count and bind to `127.0.0.1` by default (require
  `--api-key` and keep `--webui-mcp-proxy`, `--tools`, `/props`, and `/slots`
  behind an admin-only surface if exposed beyond loopback); the cloud escape
  lane stores the API key only via config or environment variable and treats
  the provider as outside the air-gap boundary; remote `image_url` /
  `audio_url` / `video_url` are SSRF-sensitive opt-ins; downloaded weights,
  quants, adapters, and mmproj files stay outside release artifacts unless
  license, lineage, revision/hash, and redistribution terms are recorded.
- `VerificationChecks[]` — `/v1/models` presence, primary-source capability
  receipts, model-artifact provenance receipts, repeated PalLLM latency and
  fallback measurements, prefix-cache proof, structured-output parse stability,
  speculative-decoding A/B with accepted/proposed token ratio, llama.cpp
  prompt-cache / slot-count / quantized-KV proof, libmtmd/mmproj smoke for GGUF
  multimodal lanes, media UUID cache proof, route-class replay proof, and
  cloud-escape network-unreachable fallback proof.
- `PromotionReceipts[]` and `MetricReceipts[]` — machine-readable records that
  must exist before a lane becomes a player default or release recommendation:
  route-labeled replay (companion chat, vision describe, world-state
  extraction, screenshot proof loops, audio/ASR, long proof/docs), runtime
  capability handshakes, model-artifact provenance, package/redistribution
  decisions, GGUF prompt/state-cache canaries, Qwen3.6 context receipts, Gemma
  audio-budget receipts, llama.cpp speculation A/B proof, multimodal
  media-admission proof, Qwen Omni streaming-video / speech fallback proof, and
  PalLLM's own `/metrics` receipts (`palllm_chat_duration_seconds`,
  `palllm_inference_recent_window_status`, `palllm_inference_lane_status`,
  `palllm_fallback_reply_total`).

For a live, operator-readable projection of those fields, run:

```powershell
pwsh ./pal.ps1 models serving
pwsh ./pal.ps1 models serving -Json
pwsh ./pal.ps1 models probe
pwsh ./pal.ps1 models probe -Json
```

Both commands are read-only. `pal models serving` calls
`/api/inference/collaboration`, filters by `-ModelId` when requested, and
prints startup hints, request hints, admission caps, cache hints, security
controls, promotion receipts, metric receipts, and verification checks for
each configured lane. `pal models probe` checks the running model endpoint
itself (`/health`, `/v1/models`, `/metrics`) and writes
`artifacts/model-probe/model-probe-*.json` with endpoint status, model ids, and
metric family names only. It sends no chat, image, audio, tool-call, or player
payload content.

### The local llama.cpp recipe

Keep the first profile loopback-only, metrics-enabled, and conservative; use
the bundled `pal connect llamacpp` helper to print and optionally wire it:

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

Promote none of those optional lanes from a benchmark alone. Record `/health`,
`/v1/models`, `/metrics`, second-turn latency, slot selection, `-cram`
pressure, active KV memory, accepted/generated token statistics, exact
JSON/tool-call parse success, and deterministic fallback behavior on PalLLM
replay traffic before changing the default model server recipe. Use
`--sleep-idle-seconds` only after wake latency and cold-after-wake fallback
behavior are recorded. For GGUF / llama.cpp lanes, keep `cache_prompt` enabled
for stable PalLLM prefixes, treat prompt-cache reuse as server/slot-local, and
do not claim a cache win until second-turn latency, slot eviction, cache RAM
pressure, and active KV memory have been measured. Treat host prompt-cache
restore (`--slot-save-path`) as a per-model-family capability rather than a
RAM-only toggle: a same-slot second-turn canary should prove reuse, while
changed chat templates, context sizes, adapters, model files, or server builds
should invalidate instead of reusing stale state.

### The cloud escape recipe

On below-reference hardware where local llama.cpp cannot serve the needed
model, point PalLLM at an OpenAI-compatible cloud API:

```powershell
pwsh ./pal.ps1 connect cloud -Provider <provider> -Model <id> -ApiKey <key> -WriteConfig
```

Prompts leave the machine on this path. Keep player text, save paths, and
secrets out of system prompts; verify the provider's retention posture; store
the API key only via `PalLLM:Auth/Inference` config or an environment variable
(never in committed files); confirm `/v1/models` lists the configured model and
`/v1/chat/completions` handles PalLLM replay traffic; and prove PalLLM falls
back to the deterministic reply when the cloud endpoint is unreachable before
promotion.

### Per-route proof, samplers, and speculation

- Baseline `PalLLM:Inference:Temperature`, `TopP`, and `PresencePenalty` stay
  ordinary chat-shaping knobs, but the sidecar validates their OpenAI-style
  bounds at startup so malformed sampler config fails before any player turn is
  queued against the upstream endpoint.
- Use `PalLLM:Inference:TopK`, `MinP`, or `RepetitionPenalty` only after the
  exact local runtime accepts `top_k`, `min_p`, or `repetition_penalty`;
  compare style/loop deltas, parser stability, generated tokens, p95 latency,
  and fallback counters before making the setting a player-facing default.
- For structured-output proof, pass `InferencePrompt.ResponseFormat` or
  `InferencePrompt.StructuredOutputs` only on the exact route being qualified;
  a `json_object`-only pass is not a JSON Schema proof. Include a schema-echo
  canary with a required object, enum, bounded array, deliberate violation
  prompt, and changed-schema digest. The PalLLM validator remains authoritative
  even when the upstream server claims constrained decoding.
- Treat Qwen3.6 or Gemma 4 MTP as a separate model-native speculation mode:
  keep strict JSON, tool-call, judge, and save-replay routes no-spec until each
  route proves stable. For Qwen3.6 low-concurrency latency proof, compare an
  MTP-1 lane with prefix caching disabled against the normal prefix-cache lane
  and a no-spec baseline.
- Qwen3.6 official cards advertise very large context windows, but ordinary
  companion turns should stay short; reserve 128K+ contexts for proof,
  docs-sync, or deliberate review lanes that can afford the KV cache. Record
  served model id, runtime context cap, extension flags, route token budget,
  and KV/state memory before promoting a long-context lane.
- Treat native audio-in separately from screenshot/video media: normalize clips
  to mono 16 kHz, cap ordinary proof clips at 30 seconds, hash the normalized
  bytes after trimming policy is applied, and record audio-token cost before
  assuming the route has headroom. Gemma 4 budgets `25` audio tokens per second
  (`750` for a 30-second clip); Gemma 3n budgets `6.25` (`188` for 30 seconds).
  PalLLM's typed-text chat remains the fallback-grade contract in both cases.
- Keep speculative-decoding evidence modality-isolated: a text prefix/KV-cache
  or MTP win does not prove media UUID, multimodal encoder-cache, audio-token,
  parser, or fallback behavior. Run plain text chat, Palworld
  screenshot/image, video summary, and audio-in/ASR cases as separate
  no-spec / n-gram / model-native comparisons before a multimodal lane becomes
  player-facing.

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

The `HardwareProfiler` reports `RecommendedQuantization` based on the detected
GPU architecture. Every row runs on the bundled llama.cpp `llama-server` — the
recommendation is **which GGUF quant to load**, not which engine. Operators can
override; the defaults follow the matrix in [`QUANTIZATION.md`](QUANTIZATION.md):

| Detected architecture | `RecommendedQuantization` | Why |
|---|---|---|
| Blackwell (RTX 50, B100/B200, GB200) | `nvfp4` | Native FP4 tensor cores; an NVFP4-quantized GGUF keeps a large model compact at near-FP16 quality on llama.cpp |
| AMD Instinct CDNA3/CDNA4 (MI300/MI325/MI350) | `mxfp4` | Standards-based FP4 path; validate exact backend/model coverage before promoting it |
| Hopper (H100/H200) | `fp8` | Native FP8 tensor cores |
| Ada (RTX 40, L40) | `fp8` | Native FP8 tensor cores |
| Ampere (RTX 30, A100) | `q4_k_m` | No FP8/FP4 hardware; Q4_K_M GGUF is the right latency/quality balance |
| Older NVIDIA / AMD consumer / Apple Silicon | `q4_k_m` | Best cross-platform GGUF choice |
| CPU only | `q4_k_m` (small model) | Only practical CPU path |

For a PalLLM operator with a Blackwell box (RTX 5090, B200), the recommended
stack is the bundled engine pointed at an NVFP4 GGUF:

```
llama-server
  -m <model-NVFP4.gguf>   (e.g. an NVFP4 Qwen3.6-A3B GGUF)
  --host 127.0.0.1 --port 8080 -c 8192 -ngl 99 --flash-attn on --metrics --no-webui

PalLLM
  Inference:Enabled = true
  Inference:BaseUrl = http://127.0.0.1:8080/v1/
  Inference:Model   = <served model id from /v1/models>
```

Operators on Hopper, Ada, or Ampere should swap NVFP4 for the GGUF quant their
architecture supports. Auto-detection uses bounded Linux procfs probes and
sanitized Windows display-adapter registry strings; setting
`PALLLM_GPU_ARCHITECTURE=blackwell` (or `hopper` / `ada` / `ampere`) still
hints the profiler when the exact chip cannot be determined. Hardware that
cannot run a usable local GGUF should use `pal connect cloud` instead.

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
- ASR, native audio-in, and Qwen Omni audio-out exist as proof-gated serving
  lanes through `Capability.ServingProfile`; they do not replace the typed-text
  chat contract until privacy, latency, and deterministic fallback proof exists.

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
- [Gemma 3n model overview](https://ai.google.dev/gemma/docs/gemma-3n)
- [Gemma audio understanding](https://ai.google.dev/gemma/docs/capabilities/audio)
- [Gemma video understanding](https://ai.google.dev/gemma/docs/capabilities/vision/video-understanding)
- [Google Gemma 4 MTP drafter announcement](https://blog.google/innovation-and-ai/technology/developers-tools/multi-token-prediction-gemma-4/)
- [Unsloth Qwen3.6-27B-GGUF](https://huggingface.co/unsloth/Qwen3.6-27B-GGUF)
- [Unsloth Qwen3.6-35B-A3B-GGUF](https://huggingface.co/unsloth/Qwen3.6-35B-A3B-GGUF)
- [Unsloth Qwen run and fine-tune guide](https://unsloth.ai/docs/models/qwen3-how-to-run-and-fine-tune)
- [Unsloth Dynamic 2.0 GGUFs](https://unsloth.ai/docs/basics/unsloth-dynamic-2.0-ggufs)
- [llama.cpp server README](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)
- [llama.cpp speculative decoding](https://github.com/ggml-org/llama.cpp/blob/master/docs/speculative.md)
- [llama.cpp multimodal support](https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md)
- [llama.cpp grammar docs](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md)
- [llama.cpp MTP context-shift issue](https://github.com/ggml-org/llama.cpp/issues/22867)
- [llama.cpp SWA prompt-cache fix PR](https://github.com/ggml-org/llama.cpp/pull/21749)
- [llama.cpp model management](https://huggingface.co/blog/ggml-org/model-management-in-llamacpp)
- [Hugging Face model cards](https://huggingface.co/docs/hub/en/model-cards)
- [Hugging Face pickle scanning](https://huggingface.co/docs/hub/main/security-pickle)

Research context:

- [ContextBench: context retrieval in coding agents](https://arxiv.org/html/2602.05892v3)
- [MMSpec: Benchmarking Speculative Decoding for Vision-Language Models](https://arxiv.org/abs/2603.14989)
- [SWE Context Bench](https://arxiv.org/abs/2602.08316)
