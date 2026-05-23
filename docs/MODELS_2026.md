# Model recommendations — May 2026

Last audited: `2026-05-22`

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

> **Honesty disclaimer.** No claim that *every* model below is the
> single best choice — local-LLM landscape shifts every ~6 weeks. The
> "as of 2026-05-22" stamp is a checkpoint; the priority order and the
> rationale should age more gracefully than the model identifiers.
> Re-audit recommended every 90 days; the `Drift_Doc_freshness` gate
> will surface this doc again automatically.

---

## TL;DR — defaults by hardware class

| Function | Edge (CPU / 4-8 GB GPU) | Consumer (12-16 GB) | Enthusiast (24 GB+) |
|---|---|---|---|
| Fast-start chat | `gemma3:4b` (default) | `qwen3.6-mini:4b-a1b` | `qwen3.6-mini:4b-a1b` |
| Quality chat | `gemma3:4b` (single-tier) | `qwen3.6:35b-a3b` Q4 | `qwen3.6:35b-a3b` Q5 / `qwen3.6:27b` |
| Vision (screenshot) | `openbmb/MiniCPM-V-4.6` (1.3B) | `Qwen3-VL-8B` Q4 | `Qwen3-VL-8B` Q8 / `Qwen2.5-VL-32B` |
| TTS (synthesis) | `hexgrad/Kokoro-82M` | `Kokoro-82M` or `fishaudio/s2-pro` | `fishaudio/s2-pro` |
| ASR (transcription) | `distil-whisper` | `whisper-large-v3-turbo` (faster-whisper backend) | `whisper-large-v3-turbo` |
| Embeddings (memory) | `nomic-embed-v2` (137M) | `bge-m3` | `bge-m3` |
| External reranker (memory, optional) | (skip) | `bge-reranker-v2-m3` | `jina-reranker-v3` |

Every default is configurable via `PalLlmOptions` — see the per-section
table for the exact knob.

---

## 1. Chat — fast-start lane (instant boot)

**Job.** Reply within ~200 ms cold on a Standard-tier machine. The
operator should never see a 10-second model-loading delay on first
chat, even when a larger quality model is still pulling.

**Recommendations.**

| Tier | Model | Size | Why | License |
|---|---|---|---|---|
| Default | `gemma-4-E4B-it-UD-Q4_K_XL` | 4 B | Pinned in shipping `appsettings.json` (Pass 337); unsloth UD-Q4_K_XL via llama-server; chat-tuned | Gemma TOU |
| Better | `qwen3.6-mini:4b-a1b` | 4 B / 1 B active | MoE: only 1 B params active at inference time = effectively a 1 B-class latency at 4 B-class quality | Apache 2.0 |
| Aggressive | `sapientinc/HRM-Text-1B` | 1.2 B | Top-trending Nov 2025–May 2026 due to "hierarchical reasoning" architecture; surprisingly strong for size | Apache 2.0 |
| Edge / CPU | `FrontiersMind/Nandi-Mini-600M` | 600 M | Designed for laptop CPU; usable replies <500 ms even without a GPU | Apache 2.0 |

**Wire-up.** `PalLLM:Inference:ModelTiers[0]` — when the operator's
machine probes llama-server and finds the fast-start model present, it
graduates to the quality lane automatically (see
[`ARCHITECTURE.md` "Tier orchestrator"](ARCHITECTURE.md)).

---

## 2. Chat — quality lane (deliberate replies)

**Job.** Coherent, in-character companion dialogue for the bulk of
chat turns once the model has loaded.

**Recommendations.**

| Tier | Model | Size | Active | Why | License |
|---|---|---|---|---|---|
| Default | `qwen3.6:35b-a3b` | 35 B | 3 B | Already pinned; A3B MoE = enthusiast quality at consumer hardware cost | Apache 2.0 |
| Sibling | `qwen3.6:27b` | 27 B dense | 27 B | Heavier per-token, but no MoE routing overhead — sometimes preferable for short prompts where the routing decision dominates | Apache 2.0 |
| Alternative | `inclusionAI/Ring-2.6-1T` | 1 T | unknown | Enthusiast-only; trending #3 on the Hub; for operators with NVLink dual-3090 or H100 | Mixed |
| Roleplay-tuned | `Snowpiercer-15B` / `Rocinante-X-12B` | 12-15 B | dense | Hugging Face community favorites for "low AI-slop" companion dialogue | Various |
| Frontier | `deepseek-ai/DeepSeek-V4-Flash` | 158 B | sparse | When the operator has the hardware and wants raw capability over latency | Apache 2.0 |

**Wire-up.** `PalLLM:Inference:ModelTiers[1]`. The tier orchestrator
upgrades to this once it's present and warm.

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
| Edge | `openbmb/MiniCPM-V-4.6` | 1.3 B | 2-3 GB | Top-trending VLM on the Hub right now; well-supported in llama.cpp; punches up | MiniCPM-Model-License |
| Default | `Qwen3-VL-8B` Q4 | 8 B | 6-7 GB | Multiple sources call it "the new default local VLM"; outperforms Llama 3.2 Vision 11B on benchmarks | Apache 2.0 |
| Tight | `gemma3-vision:4b` (int4) | 4 B | 2.6 GB | Practical vision on 8 GB rigs after the chat model is also loaded | Gemma TOU |
| Quality | `Qwen2.5-VL-32B` | 32 B | ~21 GB | Best local document accuracy when the operator has 24 GB VRAM | Apache 2.0 |
| Frontier | `OpenGVLab/InternVL3-78B` | 78 B | server | Record-holder on MMMU (72.2); for offline labs only | Various |

**Wire-up.** `PalLLM:Vision:BaseUrl` + `PalLLM:Vision:Model` already
exist. The shipped default stays `gemma4:e2b` as a small edge-class
placeholder; operators with a consumer GPU should pin `Qwen3-VL-8B`
after proving the exact endpoint accepts PalLLM's image content-part
shape and structured-output request.

---

## 4. TTS — text-to-speech

**Job.** Synthesise a short companion line for in-game playback when
TTS is enabled. Latency matters: a 2-second delay kills the
companion-feel.

**Recommendations.**

| Tier | Model | Size | Latency | Voice clone | License |
|---|---|---|---|---|---|
| Default | `hexgrad/Kokoro-82M` | 82 M | <300 ms / line | No | Apache 2.0 |
| Multilingual | `Supertone/supertonic-3` | small | ~500 ms | No | OpenRAIL |
| Voice clone (open commercial) | `fishaudio/s2-pro` (Fish Speech 2) | 4.5 B | ~1-2 s | Yes | Apache-style (verify pack) |
| Voice clone (no commercial) | `SWivid/F5-TTS` | medium | ~1-2 s | Yes (6-sec reference) | CC-BY-NC 4.0 |
| Realtime | `mistralai/Voxtral-Mini-4B-Realtime-2602` | 4 B | streaming | partial | Apache 2.0 |
| Production sub | `ResembleAI/Dramabox` (LTX-2 base) | unknown | medium | Yes | Other |
| Zero-shot multilingual | `k2-fsa/OmniVoice` | small | medium | Yes | Apache 2.0 |

**Wire-up.** `PalLLM:Tts:BaseUrl`, `PalLLM:Tts:RequestFormat`,
`PalLLM:Tts:Model`, and `PalLLM:Tts:DefaultVoice` already exist. The
operator picks Kokoro / Fish / F5 by pointing at an
OpenAI-compatible TTS server that wraps the chosen model
(`vllm-omni`, `openvoice-api`, `kokoro-fastapi`, etc.).

**Honest note on Fish vs F5.** Both are commonly recommended for
voice cloning. Fish Speech ships under a permissive license and runs
in <2 s after warm-up; F5 has subjectively higher reference fidelity
in community polls but its CC-BY-NC 4.0 license rules it out for any
commercial mod redistribution. PalLLM should default to Fish when an
operator wants to ship voice presets in a community pack, and document
F5 as a personal-use alternative.

---

## 5. ASR — speech-to-text

**Job.** Player speaks into a mic, sidecar transcribes for chat
ingest. Optional today; opt-in pathway.

**Recommendations.**

| Tier | Model | Size | Speed vs `large-v3` | License |
|---|---|---|---|---|
| Default | `openai/whisper-large-v3-turbo` via `faster-whisper` | 809 M | ~6× faster | MIT |
| Streaming | `nvidia/parakeet-tdt-0.6b-v3` | 600 M | low-latency streaming | CC-BY-4.0 |
| English-only | `distil-whisper/distil-large-v3` | 750 M | ~6× faster, +1% WER | MIT |
| Multimodal | `mistralai/Voxtral-Mini-4B-Realtime-2602` | 4 B | realtime (audio+text in one model) | Apache 2.0 |
| Quality | `openai/whisper-large-v3` | 1.5 B | baseline | MIT |
| Diarization | `pyannote/speaker-diarization-3.1` | 30 M | adjunct | MIT |

**Wire-up.** `PalLLM:Asr:BaseUrl` + `PalLLM:Asr:Model` already exist;
`Model` is required when ASR is enabled. Default-recommend
`whisper-large-v3-turbo` running under
`faster-whisper` (CTranslate2 runtime, 4× speed-up at parity quality).
The Voxtral option is interesting for future work — one model for audio
understanding + ASR + TTS — but PalLLM's current shape keeps these
separate.

---

## 6. Embeddings — memory recall

**Job.** Embed every chat turn so `ConversationMemoryStore.Recall(...)`
can pull semantically similar past memories for the prompt.

**Recommendations.**

| Tier | Model | Size | Notes | License |
|---|---|---|---|---|
| Default | `BAAI/bge-m3` | 568 M | Hybrid retrieval (dense + sparse + multivector) in one model — biggest single-model upgrade over BGE-large | MIT |
| Lightweight | `nomic-ai/nomic-embed-text-v2-moe` | 137 M | Multilingual, easy self-host | Apache 2.0 |
| Heavyweight | `jinaai/jina-embeddings-v3` | 570 M | Best accuracy-per-dollar in MTEB rankings | CC-BY-NC 4.0 (non-commercial) |
| Alternative | `mixedbread-ai/mxbai-embed-large-v1` | 335 M | Mid-pack but stable; widely deployed | Apache 2.0 |

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

**Recommendations.**

| Tier | Model | Size | Latency target | License |
|---|---|---|---|---|
| Default | `BAAI/bge-reranker-v2-m3` | 568 M | <100 ms / pair | Apache 2.0 |
| Premium | `jinaai/jina-reranker-v3` | 570 M | <200 ms / pair (best BEIR nDCG-10) | CC-BY-NC 4.0 |
| Lightweight | `mixedbread-ai/mxbai-rerank-large-v1` | 350 M | ~150 ms / pair | Apache 2.0 |
| Production-ELO leader | `Zerank/zerank-2` | unknown | medium | Other |
| Max-accuracy | `nvidia/nemotron-rerank-1b` | 1 B | slow | NVIDIA license |

**Wire-up.** A model-based reranker is still optional future work and should
sit behind a `PalLlmOptions:Memory:Rerank` block if added. Skip on edge tier;
recommend at consumer tier only after replay proof shows the extra 50-200 ms
does not damage player turn feel. The default exact-token reranker remains
always local and sub-millisecond.

---

## Remaining wire-up changes proposed

The default-recommendation upgrade is mostly **documentation**, not
code: PalLLM is already model-agnostic via OpenAI-compatible HTTP
shapes for chat, vision, TTS, and ASR lanes. The remaining concrete
code changes are small and additive:

1. **`PalLLM:Memory:RerankEndpoint`** - future optional model reranker
   hook. Off by default; on it would add a 50-200 ms stage to memory recall
   and should only promote after side-by-side replay proof against the
   deterministic exact-token reranker now in the shipping path.
2. **`PalLLM:Inference:ModelTiers[]` defaults** — update the
   shipped sample config to `gemma3:4b` (fast-start) +
   `qwen3.6:35b-a3b` (quality) so a fresh install doesn't ask the
   operator to pick. Already partially done.

Each of these is a clean additive feature pass — exactly the shape
of Pass 315 (the species resolver) — and each could ship in one
focused commit.

---

## Re-audit checklist

Quarterly: walk the seven sections, re-run the HF trending queries,
note which model identifiers have been deprecated or rolled forward
(e.g. `qwen3.6:35b-a3b` → `qwen3.7:X`). Mark this doc with the new
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

Curated 2026-05-21. Where multiple sources said the same thing, the
table above leans on the lower-bias one (HF Hub data, then leaderboards,
then editorial roundups).

- HF Hub `hf_hub_query` results (trending text-gen, TTS, ASR, VLM
  pipelines on 2026-05-21)
- [Best Open-Source LLM Models in 2026 — Hugging Face blog](https://huggingface.co/blog/daya-shankar/open-source-llms)
- [Qwen 3.6 vs Gemma 4 vs Llama 4 vs GLM-5.1 vs DeepSeek V4 — Lushbinary](https://lushbinary.com/blog/qwen-3-6-vs-gemma-4-llama-4-glm-5-1-deepseek-v4-open-source-comparison/)
- [Best AI RP & LLMs for Roleplay in 2026 — Novi AI](https://www.noviai.ai/models-prompts/best-llm-for-roleplay/)
- [Best Local Vision-Language Models for Offline AI — Roboflow](https://blog.roboflow.com/local-vision-language-models/)
- [Best Vision Models You Can Run Locally — InsiderLLM](https://insiderllm.com/guides/vision-models-locally/)
- [Best Open-Source TTS 2026 — FindSkill.ai](https://findskill.ai/blog/best-open-source-tts-2026/)
- [Best Text-to-Speech Models — DigitalOcean](https://www.digitalocean.com/community/tutorials/best-text-to-speech-models)
- [Best Open-Source Embedding Models in 2026 — BentoML](https://www.bentoml.com/blog/a-guide-to-open-source-embedding-models)
- [Best Embedding Model for RAG 2026 — Milvus](https://milvus.io/blog/choose-embedding-model-rag-2026.md)
- [Top 7 Rerankers for RAG — Analytics Vidhya](https://www.analyticsvidhya.com/blog/2025/06/top-rerankers-for-rag/)
- [Reranker Benchmark — AImultiple](https://aimultiple.com/rerankers)
- [Hugging Face Ettin reranker release](https://huggingface.co/blog/ettin-reranker)
- [Jina reranker v3 model card](https://huggingface.co/jinaai/jina-reranker-v3)
- [Microsoft Foundry BGE reranker model card](https://ai.azure.com/catalog/models/baai-bge-reranker-v2-m3)
- [Best open source speech-to-text in 2026 — Northflank](https://northflank.com/blog/best-open-source-speech-to-text-stt-model-in-2026-benchmarks)
- [Faster-Whisper — GitHub SYSTRAN](https://github.com/SYSTRAN/faster-whisper)

## Related

- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) — role-mesh
  pairing (worker / judge / scout / reviewer) and serving profiles
- [`QUANTIZATION.md`](QUANTIZATION.md) — quant choice (NVFP4 / MXFP4
  / FP8 / Q4_K_M / Q5_K_M / Q8_0) per-architecture
- [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md) — Blackwell-class
  GPU specific tuning (FP4 / NVFP4 / TRT-LLM)
- [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md) — vision + audio
  end-to-end recipes
- [`MEMORY_RECIPES.md`](MEMORY_RECIPES.md) — embedding + retrieval
  + reflection composition
- [`adr/0001-deterministic-first-reply-pipeline.md`](adr/0001-deterministic-first-reply-pipeline.md)
  — why every chat turn still works with no model loaded at all
