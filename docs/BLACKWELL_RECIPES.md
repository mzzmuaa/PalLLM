# Blackwell + NVFP4 / MXFP4 recipes for PalLLM

Last audited: `2026-05-22`

Concrete, copy-pastable recipes for PalLLM-style local companion work:
Palworld companion dialogue, screenshot/vision review, world-state narration,
and the operator's supporting coding/release checks. The format primer (NVFP4
vs MXFP4 vs FP8 vs Q4_K_M vs Q8_0) lives in
[`QUANTIZATION.md`](QUANTIZATION.md); this doc is the applied companion:
vLLM / TensorRT-LLM startup snippets, prompt templates, monitoring checks, and
failure-mode handlers.

> **Honest scope.** PalLLM itself doesn't quantize models -- it's an
> HTTP client. Every recipe below is for the operator's inference
> server (vLLM / TensorRT-LLM / SGLang / NIM) plus the *consumer*
> app (PalLLM). Recipes are written
> narrowly for PalLLM's Palworld companion, bridge, screenshot,
> narration, and release-check workloads.

## Index

| Use case | Section |
|---|---|
| Companion / NPC dialogue (small-context, low-latency) | [§1](#1-companion--npc-dialogue) |
| Agentic coding (long-context, multi-turn, tool calls) | [§2](#2-agentic-coding-assistant) |
| Productivity / general chatbot | [§3](#3-productivity--general-chatbot) |
| Vision + text combined | [§4](#4-vision--text-combined) |
| Game world-state narration | [§5](#5-game-world-state-narration) |
| When NVFP4 quality drifts mid-session | [§6](#6-graceful-quality-fallback) |
| 2027 outlook hooks | [§7](#7-2027-outlook--hooks-to-build-now) |
| 2035 outlook hooks | [§8](#8-2035-outlook--seams-to-leave-loose) |

## 0. Universal Blackwell stack (2026 defaults)

```bash
# 1. Pull a NVIDIA-published NVFP4 model
huggingface-cli download nvidia/Llama-3.3-70B-Instruct-FP4

# 2. Boot vLLM (V1 engine, default since 0.8.x; APC and chunked prefill
#    on by default; EAGLE-3 speculative decoding opt-in below)
docker run --gpus all --rm -p 11434:8000 \
  -v ${HF_HOME:-~/.cache/huggingface}:/root/.cache/huggingface \
  vllm/vllm-openai:latest \
  --model nvidia/Llama-3.3-70B-Instruct-FP4 \
  --quantization fp4 \
  --max-model-len 8192 \
  --gpu-memory-utilization 0.92 \
  --structured-outputs-config.backend xgrammar \
  --enable-prefix-caching \
  --prefix-caching-hash-algo sha256_cbor

# 3. (Optional) speculative decoding via EAGLE-3 - 2-6x faster at batch=1,
#    high acceptance on agent-loop output (structured tool calls, JSON)
#    Add to the docker run above:
#    --speculative-config '{"method":"eagle3","model":"nvidia/Llama-3.3-70B-EAGLE-Draft","num_speculative_tokens":4}'

# 4. Verify the engine is using FP4 tensor cores
curl -s http://localhost:11434/v1/models | jq '.data[].id'
```

For TensorRT-LLM proof instead of vLLM, start with the connector so the
PalLLM config shape and proof checklist stay consistent:

```powershell
pwsh ./pal.ps1 connect tensorrt -Model Qwen/Qwen3-8B -ToolCallParser qwen3
```

Do not promote TensorRT-LLM from startup alone. Capture `/health`,
`/v1/models`, `/v1/chat/completions`, `/metrics`, config-YAML hash, backend,
tp/pp/ep sizes, warm p50/p95, exact JSON/tool-call parse success, and fallback
activation on PalLLM replay traffic first.

**What landed in 2026 that's worth the flag-flicks:**

- `--structured-outputs-config.backend xgrammar` -- the current vLLM
  server-side way to pin xgrammar when you want an explicit backend.
  Eliminates malformed JSON / wrong tool names without
  pre/post-processing. Pair with `response_format:
  {"type":"json_schema",...}` on the request side. Older request-side
  `guided_*` fields are gone in current vLLM; use `structured_outputs`
  for per-request extras.
- `--enable-prefix-caching` -- on by default in V1 (this flag is just
  belt-and-braces). Critical for companion mods where the system prompt
  + persona pack is reused per turn -- multiplies throughput at high
  hit rates with <1% miss-case overhead.
- `--prefix-caching-hash-algo sha256_cbor` -- uses deterministic,
  cross-language cache identity for vLLM prefix caching. Pair with
  `PalLLM:Inference:PrefixCacheSalt` when a shared endpoint needs one stable
  non-secret salt per player/save/profile trust domain; avoid one random salt
  per request because that erases cache hits.
- `--speculative-config` (EAGLE-3) -- opt-in but the standard 2026
  speedup. NeurIPS 2025 paper, integrated v0.8.5+, CUDA-graph in
  v0.9.1, P-EAGLE (parallel) in v0.16. **2-6x faster at batch=1**;
  works because companion-mod output is highly structured (tool calls,
  JSON, predictable response shape).
- **Continuous batching + chunked prefill** -- keep vLLM's scheduler on this
  path and make `--enable-chunked-prefill` explicit in copy-paste recipes so
  long proof/doc prompts do not stall short companion turns.
- **Proof-gated KV-cache compression** -- `--kv-cache-dtype fp8` can reduce
  KV-cache memory pressure and keep longer PalLLM contexts resident. Treat it
  as a measured lane setting, not a blind default: compare against `auto` KV
  cache for quality, exact JSON/tool-call parse success, TTFT, ITL, cache-hit
  behavior, and fallback activation. Compare `nvfp4` only when the vLLM server
  and Blackwell-class hardware advertise support.
- **N-gram / suffix speculation before heavier draft-model topology** --
  current vLLM accepts `--speculative-config` JSON for n-gram and suffix
  speculation. For PalLLM's repetitive short companion turns, qualify those
  low-footprint methods before adding a separate draft model. Promotion proof
  should record accepted/proposed token ratio, end-to-end speedup, and zero
  structured-output regressions; strict JSON/tool-call routes stay no-spec
  until route-specific proof exists.
- **Sequence-count caps at the serving edge** -- when a vLLM endpoint is
  reachable beyond loopback, set a workload-sized `VLLM_MAX_N_SEQUENCES` and
  keep request-body/rate limits at the reverse proxy so one request cannot
  monopolize GPU memory.
- **Disaggregated prefill only for tail-latency isolation** -- current vLLM
  marks it experimental. It can split prefill and decode across separate
  instances to tune TTFT and ITL independently, but it is not a throughput
  booster and should not be the default companion-mod setup. MoRIIOConnector
  read/write modes belong in this same proof bucket: compare read mode, write
  mode, and monolithic vLLM with the same PalLLM replay, and keep prefix-cache-
  disabled evidence separate from normal prefix-cache evidence.
- **Sparse-MoE DBO is proof-only** -- current vLLM exposes `--enable-dbo`,
  `--dbo-decode-token-threshold`, and `--dbo-prefill-token-threshold` for
  multi-GPU data/expert-parallel MoE deployments. For PalLLM, test it only
  after scheduler caps and mixed short-turn / long-proof replay are already
  healthy; record no-DBO baseline, DP/EP topology, all2all backend,
  microbatch/overlap evidence, TTFT/ITL/E2E deltas, queue pressure, parser
  stability, and fallback counters before promotion.
- **Cache topology is now a qualification item, not a generic claim.** Local companion
  turns start with ordinary prefix caching. Repeated Palworld screenshots,
  video clips, or audio clips can additionally qualify multimodal encoder cache
  through vLLM v1 + LMCache EC; multi-instance experiments can qualify
  sticky or KV cache-aware routing, `LMCacheConnectorV1`,
  `FlexKVConnectorV1`, or KV offloading. Promote none of those until
  cold/warm TTFT, cache-hit evidence, memory budget, salt-isolation behavior
  when configured, async-transfer/load-store failure behavior, and rollback are
  recorded for the actual PalLLM workload.
- **Precomputed multimodal embeddings stay trusted-only.** vLLM exposes
  `--enable-mm-embeds` for `image_embeds`, `audio_embeds`, and similar
  embedding inputs. Use it only on a private PalLLM-owned preprocessing lane
  with exact tensor-shape proof and malformed-shape isolation; ordinary player
  media should keep using local bytes plus stable media UUIDs.
- **Local LoRA personality adapters stay opt-in and hash-pinned.** vLLM can
  serve LoRA adapters with `--enable-lora`; for PalLLM, start with
  `--max-loras 1`, a static local adapter mapping, and no runtime updating.
  Promote only after base-model compatibility, per-adapter cache identity,
  parse stability, missing-adapter fallback, and deterministic fallback on
  adapter load failure are proven. Keep `VLLM_ALLOW_RUNTIME_LORA_UPDATING`
  off unless `/v1/load_lora_adapter` is loopback-only and the adapter path is
  hash-pinned by the operator.

Replace step 1's model with whichever NVFP4 variant fits your tier:

- **8B class** — `nvidia/Llama-3.1-8B-Instruct-FP4`,
  `nvidia/Qwen3-8B-FP4` (fits on a single 5070, ~10 GB)
- **70B class** — `nvidia/Llama-3.3-70B-Instruct-FP4`
  (~37 GB, fits on a 5090 with KV-cache pressure)
- **120B+ class** — `nvidia/DeepSeek-V3-FP4`, `nvidia/Mistral-Large-FP4`
  (B200 / GB200 territory)

**MXFP4 cross-vendor variant** — for AMD MI300+ / MI350+ deployments,
swap to a vLLM-ROCm container and an MXFP4-quantized model (e.g.
`amd/Llama-3.3-70B-MXFP4` or community quants from Bartowski's
account). The HTTP API is identical; only the engine + weights change.

The PalLLM-side configuration mirror is just three env vars:

```powershell
$env:PalLLM__Inference__Enabled = "true"
$env:PalLLM__Inference__BaseUrl = "http://127.0.0.1:11434/v1/"
$env:PalLLM__Inference__Model   = "nvidia/Llama-3.3-70B-Instruct-FP4"
```

PalLLM uses the same OpenAI-compatible contract whether the operator picks
llama.cpp (default), vLLM (high-config), TensorRT-LLM, SGLang, OpenVINO, or
Foundry Local.

## 1. Companion / NPC dialogue

**Profile:** short turns, frequent calls, latency dominates over
quality, context typically <4K tokens, voice is consistent across
turns.

**Why NVFP4 here:** the 2× speedup on Blackwell takes a 70B model's
per-turn cost from 1.5-2s down to ~0.7-1s — the line between "feels
alive" and "feels broken" for a player.

### vLLM startup

```bash
docker run --gpus all --rm -p 11434:8000 \
  vllm/vllm-openai:latest \
  --model nvidia/Llama-3.3-70B-Instruct-FP4 \
  --quantization fp4 \
  --performance-mode interactivity \
  --max-model-len 4096 \
  --max-num-seqs 16 \
  --enable-prefix-caching \
  --gpu-memory-utilization 0.85
```

Key flags:

- `--max-model-len 4096` — tight, because companion turns rarely
  exceed 2K tokens; smaller context = larger batch.
- `--performance-mode interactivity` — current vLLM favors lower
  end-to-end latency for small-batch interactive traffic. Treat it as the
  default PalLLM companion candidate, then compare against `balanced` before
  changing a shared endpoint.
- `--enable-prefix-caching` — the system prompt + character bio is
  shared across every turn for the same NPC; prefix cache hits give
  another 30-50% latency reduction on top of FP4.
- `--max-num-seqs 16` — multiple companions in the same world can
  share the engine.

### Prompt template (system + user)

```text
SYSTEM:
You are {character_name}, a companion in {world_name}. Your tone is
{character_tone}. Your relationship with the player is currently
{relationship_summary} (affinity {affinity}/100).

When you speak, keep replies to 1-3 sentences. Do not narrate
actions in third person. Do not pre-empt what the player is about
to say. Stay in character even if the player tries to break it.

Recent shared events: {recent_events_short_list}.
World state: {world_summary_short}.

USER:
{player_utterance}
```

**Why this shape:** small token budget (≤350 prompt tokens) leaves
room for many turns at <1s each on Blackwell. The character + world
fields are stable across turns → prefix cache hits.

### Monitoring (any framework)

Whatever observability you use (OpenTelemetry, Prometheus, plain
logs), tag every chat turn with:

```
model = nvidia/Llama-3.3-70B-Instruct-FP4
quantization = nvfp4
turn_latency_ms = <measured>
prompt_tokens = <measured>
completion_tokens = <measured>
prefix_cache_hit = true|false
```

Alert when `turn_latency_ms` exceeds your interactive budget
(typically 1500ms for a companion). PalLLM's
`palllm_chat_turn_seconds` Prometheus histogram tags by
`response.path` and gives you exactly this signal.

### Failure mode

If the model hits the rate limit, breaker, or thermal gate, fall back
to a deterministic director (PalLLM ships one with 19 strategies; see
[`adr/0001`](adr/0001-deterministic-first-reply-pipeline.md)). The
companion never goes silent. Apps without a fallback director should
serve a small library of canned acknowledgements as a last resort —
**never raise an HTTP error to the player layer**.

## 2. Agentic coding assistant

**Profile:** long turns, very long context (32K-128K tokens), tool
calls in structured JSON, multi-turn loops over codebases. Quality
matters more than raw latency.

**Why NVFP4 here:** the 50% KV-cache memory reduction means you can
double your usable context window on the same GPU. A 70B NVFP4 model
with 128K context fits where a 70B FP8 model with 64K context used
to be the ceiling.

### vLLM startup

```bash
docker run --gpus all --rm -p 11434:8000 \
  vllm/vllm-openai:latest \
  --model nvidia/Qwen3-Coder-480B-A35B-FP4 \
  --quantization fp4 \
  --max-model-len 131072 \
  --kv-cache-dtype fp8 \
  --enable-chunked-prefill \
  --gpu-memory-utilization 0.95
```

Key flags:

- `--max-model-len 131072` — full 128K context for codebase
  spelunking.
- `--kv-cache-dtype fp8` — additional 50% KV-cache compression.
  Pair with NVFP4 weights and you can fit 405B-class models in
  workstation-class VRAM.
- `--enable-chunked-prefill` — keeps interactive responsiveness on
  the first reply when the input is huge.

### Tool-call prompt template

```text
SYSTEM:
You are an expert software engineer. You have access to the
following tools:
{tool_schemas_json}

When you want to use a tool, respond with EXACTLY a JSON object of
the form:
{
  "tool": "<tool_name>",
  "arguments": { ... }
}

Never wrap the JSON in markdown fences. Never add commentary outside
the JSON when calling a tool. After a tool result is provided to you,
either call another tool or produce the final answer in markdown.

Repository overview:
{repo_layout_or_filtered_codebase}

USER:
{user_request}
```

**Critical gotcha for NVFP4-quantized models:** v1 NVFP4 quants of
some coding models (Qwen3-Coder before mid-2025, DeepSeek-Coder-V3
before the November 2025 re-quant) had a measurable degradation in
strict-JSON tool-call formatting at very long context. **Always use
v2-or-newer NVFP4 quants for tool-calling agents.** Verify by
running 100 tool calls and counting JSON parse failures — you want
zero. If you see >1%, you have a bad quant.

### Recommended structured-output mode

If your inference server supports it (vLLM ≥ 0.6, TGI, SGLang),
**always enable structured output** for tool calls:

```python
# Python / OpenAI client style
response = client.chat.completions.create(
    model="nvidia/Qwen3-Coder-480B-A35B-FP4",
    messages=[...],
    response_format={
        "type": "json_schema",
        "json_schema": {
            "name": "tool_call",
            "schema": tool_call_schema,
            "strict": True
        }
    }
)
```

Structured output forces the engine's logits to never produce
unparseable JSON, regardless of quantization. This matters more
than the quant choice for tool-call accuracy.

### Monitoring

Critical metrics for agentic loops:

| Metric | Target | What it catches |
|---|---|---|
| `tool_call_parse_success_rate` | > 99% | Quant regression breaking JSON |
| `multi_turn_loop_length` | logged per session | Loops > 50 turns where small errors compound |
| `kv_cache_utilization` | < 90% | About to hit context-window cliff |
| `prefill_seconds` (long context) | depends on tier | Watch for >5s on Standard, >2s on Generous |
| `media_encoder_cache_hit` | measured on repeated media | Distinguishes screenshot/video/audio EC wins from text KV wins |

### Idle VRAM reclaim

Current vLLM sleep mode can reclaim GPU memory during idle windows on CUDA and
ROCm, but it is not a free latency feature. Enable it only for an explicit
idle policy, with `VLLM_SERVER_DEV_MODE=1 --enable-sleep-mode` bound behind a
loopback-only admin surface. Never expose `/sleep` or `/wake_up` to players,
LAN browsers, or a public reverse proxy.

Before enabling it on a PalLLM lane, record:

- GPU memory reclaimed while asleep
- wake latency back to first-token readiness
- whether prefix, KV, media UUID, and encoder-cache claims still hold after
  wake-up
- deterministic PalLLM fallback behavior while the model lane is asleep

Keep sleep/wake off the text-chat hot path unless the operator accepts a cold
wake. It is useful for reclaiming VRAM between sessions, not for making a live
companion turn faster.

### Failure mode

When tool-call parse fails, **don't retry blindly**. Either:
1. Re-prompt the model with the explicit error: "Your last response
   was not valid JSON: {error}. Please re-emit the tool call as
   exactly the JSON schema."
2. Or fall back to a smaller, well-calibrated model for that turn
   only (FP8 Hopper-class as a backstop).

Never silently drop tool calls — you want loud signal in your
metrics.

## 3. Productivity / general chatbot

**Profile:** mixed workload — short Q&A, occasional long summaries,
some tool calls, web-fetch RAG. Latency matters but quality matters
more.

**Why NVFP4 here:** pure throughput. A productivity bot that handles
many concurrent users gets the most absolute benefit from FP4 tensor
cores — 2× tokens/sec means 2× concurrent conversations on the same
hardware.

### vLLM startup

```bash
docker run --gpus all --rm -p 11434:8000 \
  vllm/vllm-openai:latest \
  --model nvidia/Llama-3.3-70B-Instruct-FP4 \
  --quantization fp4 \
  --max-model-len 32768 \
  --max-num-seqs 64 \
  --enable-prefix-caching \
  --enable-chunked-prefill \
  --gpu-memory-utilization 0.92
```

`--max-num-seqs 64` is the production-realistic concurrency for a
70B NVFP4 model on a single 5090.

### System prompt

```text
SYSTEM:
You are a helpful assistant. Answer the user's question accurately
and concisely. If the question requires real-time information you
do not have, ask the user to provide it or note your knowledge
cutoff. Use tools when explicitly available; otherwise answer
directly.

When asked to summarize a long document, produce: (1) a one-sentence
TL;DR, (2) 3-5 bullet points of key takeaways, (3) any open
questions worth following up on.
```

This shape works well across NVFP4 / FP8 / Q4_K_M / Q8 — it's
quant-agnostic and lets the backend swap freely.

## 4. Vision + text combined

**Profile:** image + text input, structured-output common (scene
description, OCR, UI understanding). This is where NVFP4's
floating-point dynamic range advantage over INT4 shows clearly —
vision feature maps benefit from outlier-friendly numerics.

### vLLM startup

```bash
docker run --gpus all --rm -p 11434:8000 \
  vllm/vllm-openai:latest \
  --model nvidia/Llama-3.2-90B-Vision-Instruct-FP4 \
  --quantization fp4 \
  --max-model-len 16384 \
  --limit-mm-per-prompt 'image=4' \
  --gpu-memory-utilization 0.92
```

`--limit-mm-per-prompt 'image=4'` lets the operator cap multi-image
inputs (game screenshots, multi-frame analysis).

### Prompt template

```text
SYSTEM:
You analyze {use_case} images. Reply in a single JSON object with the
fields {fields}. Do not include explanatory prose.

USER:
{image} {optional_text_context}
```

**Use cases the community has validated NVFP4-VL on:**

- Game-screenshot scene understanding (PalLLM
  `pal_vision_describe`-style)
- UI element extraction (button labels, tooltip text)
- Document OCR + structuring
- Spatial reasoning ("what's to the left of the red box?")

### Monitoring

Vision-language models are more sensitive to quantization than
text-only models. Track:

```
vision_call_success_rate    > 99%
vision_field_completeness   per-field % of expected JSON keys present
vision_call_seconds         depends on image count + size
```

If `vision_field_completeness` drops below 95%, your specific NVFP4
quant likely regressed on vision. Try a different community quant
or fall back to FP8.

## 5. Game world-state narration

**Profile:** game produces structured world-state events (NPC moved,
weather changed, raid started, quest progressed). The LLM produces
short narrative beats that connect them. Latency budget: 200-500ms
per beat. Context: small (current world snapshot only).

**Why NVFP4 here:** NPCs are *constantly* generating narration —
this is the highest-throughput case in any game. NVFP4 + 8B model
gives you 200+ narrations/second on a 5090.

### vLLM startup

```bash
docker run --gpus all --rm -p 11434:8000 \
  vllm/vllm-openai:latest \
  --model nvidia/Llama-3.1-8B-Instruct-FP4 \
  --quantization fp4 \
  --max-model-len 2048 \
  --max-num-seqs 32 \
  --enable-prefix-caching
```

The 8B model is the right tool here — narration beats don't need
the world's biggest brain, they need to be *fast* and *coherent*.

### Prompt template

```text
SYSTEM:
You are the world narrator for {game_name}. Produce ONE short
narration line (1-2 sentences) describing the event below, in
present tense, neutral tone. Do not include character dialogue. Do
not narrate the player's internal state.

Recent narration history (do not repeat any of these):
{recent_history}

USER:
Event: {event_type}
Context: {event_context_json}
```

The "do not repeat" anchor is critical — without it, narration gets
repetitive within a few minutes of play. Match against the last 8-12
narrations.

### Failure mode

If the model is unavailable, fall back to deterministic templates
keyed by `event_type`. PalLLM's `WorldNarrationAdvisor` does this
already; lift the pattern.

## 6. Graceful quality fallback

What every Blackwell-aware app should ship: a way to swap a
quantization down the quality ladder when something breaks.

```text
Detection signal              → Action
NVFP4 tool-call JSON failures → Retry with structured output, then
                                fall back to FP8 (Hopper / Ada side-
                                car) for that turn
NVFP4 long-context drift      → Cap context at 32K, summarize older
                                turns
GPU OOM mid-session            → Reduce max_num_seqs, then degrade
                                to a smaller model
Inference endpoint down       → Deterministic director (companion-
                                shaped apps) or canned response
                                (utility apps)
Persistent quant regression   → Human-flagged quant blacklist;
                                operator pulls a different
                                community quant
```

Every signal should be a *named ResponsePath value* (PalLLM-style)
or a *labeled metric counter*. Operators want to read post-hoc
"why did the assistant feel different in the 8pm session?" and get
a one-line answer.

## 7. 2027 outlook — hooks to build now

Things to leave seam-friendly for the 2026-27 horizon:

- **FP6 (post-Blackwell, Rubin generation, 2026-27).** A 6-bit FP
  format that trades NVFP4's speed for closer-to-FP8 accuracy.
  Build your `RecommendedQuantization` enum to allow `fp6`
  alongside `nvfp4`. PalLLM's `HardwareProfiler` already returns a
  string here — extending is a one-line change.
- **MXFP6 (OCP standardized).** Cross-vendor 6-bit. Same
  enum-extension story.
- **Block FP4 with learned scales.** Research direction; scaling
  parameters trained jointly with quantization rather than
  calibrated post-hoc. Likely a 2027 release path. App-side
  impact: zero — same OpenAI-API endpoint shape.
- **Per-token quantization scheduling.** Mixed-precision where
  certain tokens (e.g. JSON tool-call payload) get higher
  precision and freeform prose stays at FP4. Likely a vLLM /
  TensorRT-LLM 2027 feature. App-side impact: opt-in flag in
  the inference engine config.
- **Distributed-tensor-parallel across multiple Blackwell nodes.**
  Already the default for 405B+ models in production but
  becomes consumer-accessible (multi-5090 home rigs) in 2026-27.
  App-side: no change; the OpenAI-API endpoint is the seam.

## 8. 2035 outlook — seams to leave loose

Things that will likely matter in 2030-35:

- **3-bit and 2-bit floating-point formats with hardware support.**
  4-bit becomes "the FP16 of its era" — high quality but using
  twice the memory of the new default. PalLLM's
  `RecommendedQuantization` field is already a string, so adding
  `nvfp3` / `nvfp2` is a one-character change.
- **Inference fabric.** Distributed-tensor-parallel across remote
  nodes (other people's GPUs in your trusted compute pool) becomes
  practical; the OpenAI-API HTTP shape stays.
- **Model fingerprinting at the file level.** SHA-pinned model
  weights with attestation, similar to the SHA-pinned GitHub
  Actions PalLLM ships today. Apps that build their model-load
  path with a verification step will be ahead of the curve.
- **Long-running agent state.** PalLLM's session memory + autosave
  + relationship tracker pattern (running for years across
  thousands of turns) becomes the norm; persisted-state app
  hygiene becomes table stakes.
- **Local-first by default.** Cloud inference becomes the
  exception, not the norm — PalLLM's "everything that leaves the
  machine is opt-in" posture stops being unusual and becomes the
  baseline expectation. Apps that hardcode cloud paths today will
  be re-architecting in 2030.

## How to validate this on your own setup

Drop-in benchmark before committing a quant choice:

```bash
# 1. Baseline current quant for 100 representative prompts
cat prompts.jsonl | while read line; do
  curl -s -X POST http://localhost:11434/v1/chat/completions \
    -H "Content-Type: application/json" -d "$line" \
    | jq '.choices[0].message.content'
done > baseline.txt

# 2. Switch the model to a different quant, repeat
# 3. Diff the outputs for sanity; run your domain-specific eval

# 4. Compare latency
time (cat prompts.jsonl | parallel -j 8 ...)
```

Don't take community sentiment on faith — every model + quant
pairing has its own personality. A 1-hour A/B test on your specific
prompts beats reading 50 forum threads.

## Related

- [`QUANTIZATION.md`](QUANTIZATION.md) — the format primer
  (NVFP4 vs MXFP4 vs FP8 vs Q4_K_M vs Q8_0) with community
  sentiment + hardware matrix
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) — per-tier
  role-pairing recommendations
- [`HOT_PATH.md`](HOT_PATH.md) — how the inference latency
  budget interacts with the chat hot path
- [`OBSERVABILITY.md`](OBSERVABILITY.md) — wiring up traces +
  metrics to validate any of the above
- [`adr/0001`](adr/0001-deterministic-first-reply-pipeline.md) —
  why the deterministic fallback is the load-bearing safety net
  for every recipe in this doc
