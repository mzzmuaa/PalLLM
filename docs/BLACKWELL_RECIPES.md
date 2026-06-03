# Blackwell + NVFP4 / MXFP4 recipes for PalLLM

Last audited: `2026-06-03`

Concrete, copy-pastable recipes for PalLLM-style local companion work:
Palworld companion dialogue, screenshot/vision review, world-state narration,
and the operator's supporting coding/release checks. The format primer (NVFP4
vs MXFP4 vs FP8 vs Q4_K_M vs Q8_0) lives in
[`QUANTIZATION.md`](QUANTIZATION.md); this doc is the applied companion:
bundled `llama-server` startup snippets, prompt templates, monitoring checks,
and failure-mode handlers.

> **Honest scope.** PalLLM itself doesn't quantize models -- it's an HTTP
> client. Its only local engine is the bundled llama.cpp `llama-server`, which
> loads GGUF model files; below-reference hardware uses the OpenAI-compatible
> cloud escape (`pal connect cloud`) instead. Every recipe below is for that
> bundled engine plus the *consumer* app (PalLLM). Recipes are written narrowly
> for PalLLM's Palworld companion, bridge, screenshot, narration, and
> release-check workloads. (Pass 436 removed PalLLM's vLLM / TensorRT-LLM /
> SGLang / OpenVINO / Foundry Local / LM Studio / transformers-serve / Ollama
> connectors; the recipes here are llama.cpp-only.)

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
# 1. Fetch an NVFP4-quantized GGUF (Blackwell FP4 tensor cores) into your
#    curated model library — see docs/LOCAL_MODELS_INVENTORY.md.

# 2. Boot the bundled llama.cpp llama-server. pal connect llamacpp prints and
#    optionally wires the exact recipe; the raw form is:
llama-server -m D:\Models\Qwen\Qwen3.6-35B-A3B-NVFP4.gguf -a Qwen3.6-35B-A3B-NVFP4 \
  --host 127.0.0.1 --port 8080 \
  -c 8192 -np <player-slot-count> -b <measured> -ub <measured> -ngl 99 \
  --flash-attn on --cache-prompt --cache-reuse 256 \
  --metrics --no-webui

# 3. (Optional, proof-gated) native llama.cpp speculation for repetitive
#    companion text. Qualify before any player-facing use:
#    --spec-type ngram-mod --spec-ngram-mod-n-match 24 --spec-ngram-mod-n-min 48 --spec-ngram-mod-n-max 64
#    or, for Qwen3.6 / Gemma 4 model-native MTP GGUFs:
#    --spec-type draft-mtp --spec-draft-n-min <measured> --spec-draft-n-max <measured>

# 4. Verify the engine is serving the model
curl -s http://localhost:8080/v1/models | jq '.data[].id'
```

Wire it with the bundled connector so the PalLLM config shape and proof
checklist stay consistent:

```powershell
pwsh ./pal.ps1 connect llamacpp -ModelPath D:\Models\Qwen\Qwen3.6-35B-A3B-NVFP4.gguf -Model Qwen3.6-35B-A3B-NVFP4 -WriteConfig
```

**What's worth the flag-flicks on llama-server:**

- `--flash-attn on` — flash attention keeps KV memory and TTFT down on
  consumer cards; leave it on for player lanes.
- `--cache-prompt` + `--cache-reuse` — the system prompt + persona pack is
  reused per turn, so prompt-cache reuse is the biggest free win for a
  companion. Verify reuse through `/metrics` rather than assuming it.
- `--metrics --no-webui` — expose `/metrics` and keep the dev web UI off a
  player lane.
- Native speculation (`--spec-type ngram-simple` / `ngram-mod` / proof-only
  `draft-mtp`) — the standard 2026 speedup for PalLLM's repetitive,
  structured companion output. Qualify accepted/proposed token ratio,
  end-to-end speedup, and zero structured-output regressions before promotion;
  strict JSON/tool-call routes stay no-spec until route-specific proof exists.
- Proof-gated KV compression (`-ctk q8_0 -ctv q8_0`) — can keep longer
  contexts resident. Compare against default f16 KV for quality, exact
  JSON/tool-call parse success, TTFT, ITL, and fallback activation first.
- `--cache-prompt` deterministic identity — pair a shared endpoint with
  `PalLLM:Inference:PrefixCacheSalt` (one stable non-secret salt per
  player/save/profile trust domain); avoid one random salt per request because
  that erases cache hits.
- Cap `-np` to the real player/session count and bind `--host 127.0.0.1`; if
  exposed beyond loopback, require `--api-key` and keep `--webui-mcp-proxy`,
  `--tools`, `/props`, and `/slots` behind an admin-only surface.

Replace step 1's model with whichever GGUF fits your tier:

- **~4B class** — `gemma-4-E4B-it-UD-Q4_K_XL` (fits a small card; instant start)
- **~35B-A3B class** — `Qwen3.6-35B-A3B-UD-Q8_K_XL` or its NVFP4 GGUF on
  Blackwell (~37-39 GB; fits a 5090 with KV-cache pressure)
- **larger** — the cloud escape (`pal connect cloud`) for models that do not
  fit a usable local GGUF

The PalLLM-side configuration mirror is just three env vars (or one
`pal connect llamacpp -WriteConfig`):

```powershell
$env:PalLLM__Inference__Enabled = "true"
$env:PalLLM__Inference__BaseUrl = "http://127.0.0.1:8080/v1/"
$env:PalLLM__Inference__Model   = "Qwen3.6-35B-A3B-NVFP4"
```

PalLLM uses the same OpenAI-compatible `/v1/chat/completions` contract whether
the operator runs the bundled local llama.cpp engine or the cloud escape lane.

## 1. Companion / NPC dialogue

**Profile:** short turns, frequent calls, latency dominates over
quality, context typically <4K tokens, voice is consistent across
turns.

**Why NVFP4 here:** the ~2× speedup on Blackwell FP4 tensor cores takes a
35B-A3B model's per-turn cost down toward the "feels alive" range for a player.

### llama-server startup

```bash
llama-server -m D:\Models\Qwen\Qwen3.6-35B-A3B-NVFP4.gguf -a Qwen3.6-35B-A3B-NVFP4 \
  --host 127.0.0.1 --port 8080 \
  -c 4096 -np 16 -ngl 99 \
  --flash-attn on --cache-prompt --cache-reuse 256 \
  --metrics --no-webui
```

Key flags:

- `-c 4096` — tight, because companion turns rarely exceed 2K tokens;
  smaller context = more slot headroom.
- `--cache-prompt --cache-reuse 256` — the system prompt + character bio is
  shared across every turn for the same NPC; prompt-cache reuse gives a large
  latency reduction on top of FP4.
- `-np 16` — multiple companions in the same world can share the engine; cap
  it to the real session count.

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
room for many turns at low latency on Blackwell. The character + world
fields are stable across turns → prompt-cache hits.

### Monitoring (any framework)

Whatever observability you use (OpenTelemetry, Prometheus, plain
logs), tag every chat turn with:

```
model = Qwen3.6-35B-A3B-NVFP4
quantization = nvfp4
turn_latency_ms = <measured>
prompt_tokens = <measured>
completion_tokens = <measured>
prefix_cache_hit = true|false
```

Alert when `turn_latency_ms` exceeds your interactive budget
(typically 1500ms for a companion). PalLLM's
`palllm_chat_duration_seconds` Prometheus histogram tags by
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

**Why NVFP4 here:** the ~50% KV-cache memory reduction means you can
fit a larger usable context window on the same GPU than an FP8/FP16 GGUF
would allow.

### llama-server startup

```bash
llama-server -m D:\Models\Qwen\Qwen3.6-35B-A3B-NVFP4.gguf -a Qwen3.6-35B-A3B-NVFP4 \
  --host 127.0.0.1 --port 8080 \
  -c 131072 -ngl 99 \
  -ctk q8_0 -ctv q8_0 \
  --flash-attn on --cache-prompt --metrics --no-webui
```

Key flags:

- `-c 131072` — full 128K context for codebase spelunking (prove the GGUF's
  context identity first; see `MODEL_COLLABORATION.md`).
- `-ctk q8_0 -ctv q8_0` — proof-gated KV compression to keep a long context
  resident. Compare against default f16 KV before promotion.
- `--cache-prompt` — long shared repo prefixes benefit most from prompt-cache
  reuse.

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

**Critical gotcha for NVFP4-quantized models:** early NVFP4 quants of
some coding models had a measurable degradation in strict-JSON tool-call
formatting at very long context. **Always use a current, well-calibrated
NVFP4 GGUF for tool-calling agents.** Verify by running 100 tool calls and
counting JSON parse failures — you want zero. If you see >1%, you have a
bad quant.

### Recommended structured-output mode

The bundled llama-server converts `response_format: json_schema` into a
grammar constraint. **Always enable structured output** for tool calls:

```python
# Python / OpenAI client style
response = client.chat.completions.create(
    model="Qwen3.6-35B-A3B-NVFP4",
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

Structured output constrains the engine's logits so it cannot produce
unparseable JSON, regardless of quantization. This matters more than the
quant choice for tool-call accuracy. Qualify the schema with a PalLLM schema
digest receipt before relying on it for actions, world-state, or proof packets.

### Monitoring

Critical metrics for agentic loops:

| Metric | Target | What it catches |
|---|---|---|
| `tool_call_parse_success_rate` | > 99% | Quant regression breaking JSON |
| `multi_turn_loop_length` | logged per session | Loops > 50 turns where small errors compound |
| `kv_cache_utilization` | < 90% | About to hit context-window cliff |
| `prefill_seconds` (long context) | depends on tier | Watch for >5s on Standard, >2s on Generous |

### Idle VRAM behaviour

llama-server keeps the loaded model resident for the lifetime of the server
process. If you need to reclaim VRAM during idle windows, use
`--sleep-idle-seconds` and treat wake-up as a cold-cache boundary. Before
enabling it on a PalLLM lane, record:

- wake latency back to first-token readiness
- whether prefix/KV cache claims still hold after wake-up
- deterministic PalLLM fallback behavior while the model lane is asleep

Keep idle sleep off the text-chat hot path unless the operator accepts a cold
wake. It is useful for reclaiming VRAM between sessions, not for making a live
companion turn faster.

### Failure mode

When tool-call parse fails, **don't retry blindly**. Either:
1. Re-prompt the model with the explicit error: "Your last response
   was not valid JSON: {error}. Please re-emit the tool call as
   exactly the JSON schema."
2. Or fall back to a smaller, well-calibrated GGUF for that turn only.

Never silently drop tool calls — you want loud signal in your
metrics.

## 3. Productivity / general chatbot

**Profile:** mixed workload — short Q&A, occasional long summaries,
some tool calls, web-fetch RAG. Latency matters but quality matters
more.

**Why NVFP4 here:** pure throughput. A productivity bot that handles
many concurrent users gets the most absolute benefit from FP4 tensor
cores — ~2× tokens/sec means more concurrent conversations on the same
hardware.

### llama-server startup

```bash
llama-server -m D:\Models\Qwen\Qwen3.6-35B-A3B-NVFP4.gguf -a Qwen3.6-35B-A3B-NVFP4 \
  --host 127.0.0.1 --port 8080 \
  -c 32768 -np 64 -ngl 99 \
  --flash-attn on --cache-prompt --metrics --no-webui
```

`-np 64` is an aggressive concurrency target for a 35B-A3B NVFP4 GGUF on a
single 5090; size it to the real workload and watch `/metrics` for KV
pressure.

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
quant-agnostic and lets the operator swap GGUFs freely.

## 4. Vision + text combined

**Profile:** image + text input, structured-output common (scene
description, OCR, UI understanding). This is where NVFP4's
floating-point dynamic range advantage over INT4 shows clearly —
vision feature maps benefit from outlier-friendly numerics.

### llama-server startup

```bash
llama-server -m D:\Models\Qwen\Qwen3.6-35B-A3B-NVFP4.gguf -a Qwen3.6-35B-A3B-NVFP4 \
  --mmproj D:\Models\mmproj\mmproj-F16.gguf \
  --host 127.0.0.1 --port 8080 \
  -c 16384 -ngl 99 \
  --flash-attn on --metrics --no-webui
```

`--mmproj` loads the matching multimodal projector so libmtmd can ingest
Palworld screenshots; smoke it through `/v1/chat/completions` before routing
real screenshots. Cap multi-image inputs at the PalLLM admission layer
(`image_count<=1` by default).

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
or fall back to a Q8_0 GGUF.

## 5. Game world-state narration

**Profile:** game produces structured world-state events (NPC moved,
weather changed, raid started, quest progressed). The LLM produces
short narrative beats that connect them. Latency budget: 200-500ms
per beat. Context: small (current world snapshot only).

**Why NVFP4 here:** NPCs are *constantly* generating narration —
this is the highest-throughput case in any game. An NVFP4 + small GGUF
gives you the most beats per second on a Blackwell card.

### llama-server startup

```bash
llama-server -m D:\Models\Gemma\gemma-4-E4B-it-NVFP4.gguf -a gemma-4-E4B-it-NVFP4 \
  --host 127.0.0.1 --port 8080 \
  -c 2048 -np 32 -ngl 99 \
  --flash-attn on --cache-prompt --metrics --no-webui
```

A small (~4B) model is the right tool here — narration beats don't need
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
                                fall back to a Q8_0 GGUF for that turn
NVFP4 long-context drift      → Cap context at 32K, summarize older
                                turns
GPU OOM mid-session            → Reduce -np, then degrade to a smaller GGUF
Inference endpoint down       → Deterministic director (companion-
                                shaped apps) or canned response
                                (utility apps)
Persistent quant regression   → Human-flagged quant blacklist;
                                operator pulls a different
                                community GGUF
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
  calibrated post-hoc. Likely a 2027 GGUF-build path. App-side
  impact: zero — same OpenAI-API endpoint shape.
- **Per-token quantization scheduling.** Mixed-precision where
  certain tokens (e.g. JSON tool-call payload) get higher
  precision and freeform prose stays at FP4. App-side impact: an
  opt-in flag in the engine config; the `/v1` seam stays.
- **Distributed-tensor-parallel across multiple Blackwell nodes.**
  Already the default for very large models in production but
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
# 1. Baseline current GGUF for 100 representative prompts
cat prompts.jsonl | while read line; do
  curl -s -X POST http://localhost:8080/v1/chat/completions \
    -H "Content-Type: application/json" -d "$line" \
    | jq '.choices[0].message.content'
done > baseline.txt

# 2. Switch the GGUF to a different quant, repeat
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
- [`LLAMA_CPP_BUNDLED.md`](LLAMA_CPP_BUNDLED.md) — the bundled
  llama.cpp engine: install, auto-launch, and per-model recipes
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) — per-tier
  role-pairing recommendations
- [`HOT_PATH.md`](HOT_PATH.md) — how the inference latency
  budget interacts with the chat hot path
- [`OBSERVABILITY.md`](OBSERVABILITY.md) — wiring up traces +
  metrics to validate any of the above
- [`adr/0001`](adr/0001-deterministic-first-reply-pipeline.md) —
  why the deterministic fallback is the load-bearing safety net
  for every recipe in this doc
