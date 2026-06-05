# Model recommendations — June 2026

Last audited: `2026-06-04`

This doc is a **research-grounded rethink** of which local models should
sit behind each PalLLM function. Sources: Hugging Face Hub trending feeds
(model + dataset queries, sorted `trendingScore`), the leaderboards and
roundups linked at the bottom, and community-curated rankings.
Recommendations are tiered by hardware (edge / consumer / enthusiast) so
players on any rig get a sensible default and operators can swap up.

> **Scope.** This is the *what to recommend* doc. The *how to wire it
> up* doc is [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) (role
> mesh + serving profiles), and the *what quantization to pick* doc is
> [`QUANTIZATION.md`](QUANTIZATION.md). All three sit on top of
> [`PalLlmOptions.cs`](../src/PalLLM.Domain/Configuration/PalLlmOptions.cs).

> **Current shipping lanes.** PalLLM's default local inference posture is now
> deliberately narrow: one llama.cpp `llama-server` serving
> `Qwen3.5-9B-UD-Q6_K_XL` as the fast Worker lane and
> `gemma-4-12b-it-UD-Q6_K_XL` as the multimodal Edge lane. Any older model
> identifiers in historical docs and tests are not the player-facing default.

> **Honesty disclaimer.** No claim that *every* model below is the
> single best choice — local-LLM landscape shifts every ~6 weeks. The
> "as of 2026-06-04" stamp is a checkpoint; the priority order and the
> rationale should age more gracefully than the model identifiers.
> Re-audit recommended every 90 days; the `Drift_Doc_freshness` gate
> will surface this doc again automatically.

---

## TL;DR — defaults by hardware class

| Function | Edge (CPU / 4-8 GB GPU) | Consumer (12-16 GB) | Enthusiast (24 GB+) |
|---|---|---|---|
| Fast companion worker | deterministic fallback if no local model | `Qwen3.5-9B-UD-Q6_K_XL` | `Qwen3.5-9B-UD-Q6_K_XL` |
| Smart multimodal edge | skip unless 16 GB unified/VRAM is available | `gemma-4-12b-it-UD-Q6_K_XL` | `gemma-4-12b-it-UD-Q6_K_XL` |
| Vision (screenshot) | snapshot fallback if Gemma is unavailable | `gemma-4-12b-it-UD-Q6_K_XL` | `gemma-4-12b-it-UD-Q6_K_XL` |
| TTS (synthesis) | disabled | disabled unless operator-wired | disabled unless operator-wired |
| ASR (transcription) | disabled | disabled unless operator-wired | disabled unless operator-wired |
| Embeddings (memory) | deterministic in-process embedder | deterministic in-process embedder | deterministic in-process embedder |
| External reranker (memory, optional) | skip | skip | skip |

Every default is configurable via `PalLlmOptions` — see the per-section
table for the exact knob.

---

## 1. Chat — fast worker lane

**Job.** Keep ordinary companion turns snappy while the sidecar preserves the
deterministic fallback path for machines that cannot load a local model.

**Recommendations.**

| Tier | Model | Size | Why | License |
|---|---|---|---|---|
| Default | `Qwen3.5-9B-UD-Q6_K_XL` | 9 B | Pinned in shipping `appsettings.json`; Qwen3.5-9B GGUF + MTP gives the fast text lane PalLLM needs while staying inside a practical local llama.cpp recipe | Apache 2.0 |
| Fallback | deterministic director | 0 B | Always available; no model load, no network, no latency surprise | PalLLM code |

**Wire-up.** `PalLLM:Inference:Model` and
`PalLLM:Inference:ModelTiers[0]` both point at `Qwen3.5-9B-UD-Q6_K_XL`
in the shipping config. When the operator's llama-server reports the smart
Gemma lane available, the tier orchestrator can promote to it automatically
(see [`ARCHITECTURE.md` "Tier orchestrator"](ARCHITECTURE.md)).

---

## 2. Chat — smart multimodal edge lane

**Job.** Handle vision, audio-in proof, long-context reasoning, and complex
companion turns without broadening PalLLM beyond the two default llama.cpp
lanes.

**Recommendations.**

| Tier | Model | Size | Active | Why | License |
|---|---|---|---|---|---|
| Default | `gemma-4-12b-it-UD-Q6_K_XL` | 12 B | 12 B | Gemma 4 12B is the current PalLLM multimodal Edge lane: image + audio inputs, local-laptop target, and MTP drafter support | Apache 2.0 |
| Fast partner | `Qwen3.5-9B-UD-Q6_K_XL` | 9 B | 9 B | Keep this as the ordinary chat lane even when Gemma is present, unless a route explicitly needs the smart edge capabilities | Apache 2.0 |

**Wire-up.** `PalLLM:Inference:ModelTiers[1]` and
`PalLLM:Vision:Model` point at `gemma-4-12b-it-UD-Q6_K_XL`.

**Roleplay-specific note.** PalLLM's companion chat benefits from the
"low slop" tunes for ambient voice but the **base instruct models stay
the safer default** because deterministic-fallback prompts and tool-call
prompts both rely on instruction-following fidelity. If the operator
wants a roleplay-tuned model, document the swap in their config — the
existing `pal connect llamacpp -ModelPath <gguf>` flow accepts any local GGUF.

---

## 3. Vision — screenshot description

**Job.** Turn a Palworld screenshot (PNG/JPEG bytes) into a structured
scene description that the chat lane can use to ground the companion's
reply.

**Recommendations.**

| Tier | Model | Size | VRAM | Why | License |
|---|---|---|---|---|---|
| Default | `gemma-4-12b-it-UD-Q6_K_XL` | 12 B | ~10-16 GB | Same multimodal Edge lane as complex chat; avoids a third default local model | Apache 2.0 |
| Fallback | snapshot vision fallback | 0 B | none | Deterministic summary from `GameWorldSnapshot` when vision is off or unhealthy | PalLLM code |

**Wire-up.** `PalLLM:Vision:BaseUrl` + `PalLLM:Vision:Model` already
exist. The shipped default is `gemma-4-12b-it-UD-Q6_K_XL`. Operators can
only swap it after proving the exact endpoint accepts PalLLM's image
content-part shape and structured-output request.

---

## 4. TTS — text-to-speech

**Job.** Synthesise a short companion line for in-game playback when
TTS is enabled. Latency matters: a 2-second delay kills the
companion-feel.

**Current recommendation.** No bundled TTS model. Keep
`PalLLM:Tts:Enabled=false` unless the operator explicitly wires a local,
license-reviewed endpoint. Do not add a third default model to the PalLLM
package just for voice.

**Wire-up.** `PalLLM:Tts:BaseUrl`, `PalLLM:Tts:RequestFormat`,
`PalLLM:Tts:Model`, and `PalLLM:Tts:DefaultVoice` already exist for operator
experiments. The default supported model set remains Qwen3.5 9B for fast text
and Gemma 4 12B for multimodal edge work through llama.cpp. Voice presets,
voice cloning, and redistribution need separate license receipts before they
belong in a public build.

---

## 5. ASR — speech-to-text

**Job.** Player speaks into a mic, sidecar transcribes for chat
ingest. Optional today; opt-in pathway.

**Current recommendation.** No bundled ASR model. Keep
`PalLLM:Asr:Enabled=false` unless the operator explicitly wires a local
endpoint. Gemma 4 12B can be used for bounded audio-understanding experiments
after llama.cpp proof, but it should not be documented as a reliable
speech-to-text replacement until PalLLM has route-level replay evidence.

**Wire-up.** `PalLLM:Asr:BaseUrl` + `PalLLM:Asr:Model` already exist;
`Model` is required when ASR is enabled. The default install still works with
no mic, no ASR model, and no outbound traffic.

---

## 6. Embeddings — memory recall

**Job.** Embed every chat turn so `ConversationMemoryStore.Recall(...)`
can pull semantically similar past memories for the prompt.

**Current recommendation.** Keep the deterministic in-process embedder as the
shipping default. Do not add an external embedding model to the default local
profile while the project is standardizing on Qwen3.5 + Gemma 4 through
llama.cpp.

**Wire-up.** PalLLM's shipped `SemanticEmbedder` lives inside
`Portable/PortableAdapterContracts.cs` today and remains a deterministic,
in-process FNV-1a bag-of-tokens projection. There is no `/v1/embeddings`
call in the shipping memory path, which is deliberate for the default
local-first / zero-network posture. A future external-embedding lane should
be additive and guarded the same way as chat, vision, TTS, and ASR:
bounded timeout, response-size cap, circuit breaker, and deterministic
fallback to the current embedder.

---

## 7. Reranker — memory recall stage 2

**Job.** Refine the top-K candidates from the embedding recall before
they're injected into the prompt. PalLLM now ships a tiny deterministic
exact-token rerank term inside `ConversationMemoryStore.Recall(...)`; this
keeps named Palworld events, bosses, bases, and raids from losing tied
embedding buckets without adding a model call to the hot path.

**Current recommendation.** Keep the exact-token reranker as the only default
rerank stage. A model reranker would add a third model family and a new latency
tax, so it should stay out of the default profile until a future proof shows a
clear player-facing win. The current exact-token reranker remains always local
and sub-millisecond.

---

## Remaining wire-up changes proposed

The default-recommendation upgrade is mostly **documentation and config**:
PalLLM is already model-agnostic via OpenAI-compatible HTTP shapes for chat,
vision, TTS, and ASR lanes, but the supported default local profile should stay
small. The remaining concrete code state is:

1. **`PalLLM:Inference:ModelTiers[]` defaults** — already updated in the
   shipped sample config to `Qwen3.5-9B-UD-Q6_K_XL` (fast worker) +
   `gemma-4-12b-it-UD-Q6_K_XL` (smart multimodal edge), both served by
   llama.cpp.
2. **TTS, ASR, embeddings, rerank** — keep disabled or deterministic by
   default. Any future model-backed lane needs explicit opt-in, local proof,
   license receipts, and a deterministic fallback.

Each of these is a clean additive feature pass — exactly the shape
of Pass 315 (the species resolver) — and each could ship in one
focused commit.

---

## Re-audit checklist

Quarterly: walk the seven sections, re-run the HF trending queries,
note which model identifiers have been deprecated or rolled forward
(e.g. `Qwen3.5-9B-UD-Q6_K_XL` -> a later 9B-class GGUF). Mark this doc with the new
audit date. The drift gate will surface this doc again automatically
once `Last audited` ages past 45 days; **don't refresh the stamp
without re-running the queries** — that's the freshness-theater
anti-pattern Passes 307-309 deliberately rejected.

The HF Hub query script suitable for the next re-audit:

```text
# Run these via the mcp__*__hf_hub_query tool or equivalent.
# (1) text-generation, sort=trendingScore, limit=10
# (2) image-text-to-text (VLM), sort=trendingScore, limit=8
# (3) text-to-speech, sort=trendingScore, limit=8
# (4) automatic-speech-recognition, sort=trendingScore, limit=6
# (5) feature-extraction (embeddings), sort=trendingScore, limit=8
# (6) Cross-check Reddit /r/LocalLLaMA top-of-week for roleplay-tuned
#     finetune drift on (1) + (3).
```

---

## Sources

Curated 2026-06-04. Primary sources for the current default local lanes:

- [Google: Introducing Gemma 4 12B](https://blog.google/innovation-and-ai/technology/developers-tools/introducing-gemma-4-12b/)
- [Qwen/Qwen3.5-9B model card](https://huggingface.co/Qwen/Qwen3.5-9B)
- [Unsloth Qwen3.5-9B MTP GGUF model card](https://huggingface.co/unsloth/Qwen3.5-9B-MTP-GGUF)
- [llama.cpp multimodal documentation](https://github.com/ggml-org/llama.cpp/blob/master/docs/multimodal.md)

## Related

- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) — role-mesh
  pairing (worker / judge / scout / reviewer) and serving profiles
- [`QUANTIZATION.md`](QUANTIZATION.md) — quant choice (NVFP4 / MXFP4
  / FP8 / Q4_K_M / Q5_K_M / Q8_0) per-architecture
- [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md) — Blackwell-class
  GPU specific tuning (FP4 / NVFP4 / TRT-LLM)
- [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md) — vision + audio
  end-to-end recipes
- `MEMORY_RECIPES.md` (retired Pass 418) — embedding + retrieval
  + reflection composition
- [`adr/0001-deterministic-first-reply-pipeline.md`](adr/0001-deterministic-first-reply-pipeline.md)
  — why every chat turn still works with no model loaded at all
