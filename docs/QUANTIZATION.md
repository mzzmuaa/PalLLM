# Quantization — choosing the right format for your hardware

Last audited: `2026-05-22`

PalLLM is an HTTP client; it sends chat requests to whatever
inference server an operator configures. The model file format
(GGUF / safetensors / TensorRT engine) and its quantization
(NVFP4 / MXFP4 / FP8 / Q4_K_M / etc.) are entirely the inference
server's concern. This doc is the operator-facing primer — what
each format is, when to pick it, and what real users on Reddit /
HuggingFace forums / NVIDIA blogs are reporting about each.

If you're just looking for "what should I use?", jump to the
[recommendation matrix](#recommendation-matrix) at the bottom.

> **Source-of-truth note.** Numbers cited below are from public
> NVIDIA papers, current vLLM and AMD ROCm documentation, the OCP
> Microscaling specification, and recurring themes from
> r/LocalLLaMA threads through early 2026. Where the community is
> split, the doc says so. Re-check current benchmarks before
> committing to a format for production — the ecosystem moves
> quickly.

## The formats at a glance

| Format | Bits | Hardware | Software | VRAM (70B) | Speed (Blackwell baseline = 1.0) |
|---|---|---|---|---|---|
| **FP16 / BF16** | 16 | Any | Anything | ~140 GB | 0.25 |
| **FP8 (E4M3)** | 8 | Hopper / Ada / Blackwell | vLLM, TRT-LLM, SGLang | ~70 GB | 0.5 |
| **NVFP4** | 4 | **Blackwell only** | vLLM, TRT-LLM, NIM | **~37 GB** | **1.0 (reference)** |
| **MXFP4** | 4 | Blackwell + AMD MI300+/MI350+ | vLLM, TRT-LLM | ~37 GB | ~0.85 |
| **Q8_0** (GGUF) | 8 | Any | llama.cpp, LM Studio | ~70 GB | 0.4 |
| **Q4_K_M** (GGUF) | ~4.5 | Any | llama.cpp, LM Studio | ~40 GB | 0.55 (CPU-friendly) |
| **unsloth UD-Q8_K_XL** (GGUF) | ~8.5 | Any | llama.cpp, LM Studio | ~75 GB | 0.4 (calibration-aware Q8_0) |
| **unsloth UD-Q4_K_XL** (GGUF) | ~4.8 | Any | llama.cpp, LM Studio | ~42 GB | 0.55 (calibration-aware Q4_K_M) |
| **unsloth UD-Q6_K_XL** (GGUF) | ~6.5 | Any | llama.cpp, LM Studio | ~56 GB | 0.5 (sharded for large models) |
| **unsloth UD-IQ3_XXS / UD-IQ4_XS** | 3-4 | Any | llama.cpp | ~30-37 GB | 0.5 (frontier-large GGUF) |
| **AWQ-INT4** | 4 | Ampere+ (any CUDA) | vLLM, TGI | ~37 GB | 0.6 |
| **GPTQ-INT4** | 4 | Ampere+ | vLLM, TGI, ExLlama | ~37 GB | 0.6 |

Numbers are approximations — exact figures depend on the model,
the calibration corpus, and the inference engine version.

## NVFP4 (NVIDIA FP4)

**What it is.** NVIDIA's proprietary 4-bit floating-point format
introduced with Blackwell. The values themselves are E2M1 (1 sign,
2 exponent, 1 mantissa) — same bit layout as MXFP4 — but NVFP4
adds a **two-level scale**: per-block FP8 (E4M3) micro-scales
multiplied by a per-tensor FP32 macro-scale. The macro-scale is
what differentiates it from MXFP4 and gives it better accuracy at
the same bit width.

**Hardware requirement.** Blackwell tensor cores. RTX 50 series
(5060 / 5070 / 5080 / 5090) consumer cards, B100 / B200 /
B300 datacenter cards, GB200 superchips. Pre-Blackwell GPUs
(Hopper H100 / H200, Ada RTX 4090 / L40, Ampere RTX 3090 / A100)
do not have FP4 tensor cores; running NVFP4 there is software-
emulated and offers zero speed advantage.

**Speed.** On Blackwell tensor cores, FP4 runs at 2× the
throughput of FP8 and 4× of FP16. Single-stream tokens-per-second
on a 5090 with a 70B NVFP4 model lands in the 85-100 t/s range
(community reports, late 2025).

**Memory.** A 70B model in NVFP4 is ~37 GB. That fits on a single
5090 (32 GB) only with aggressive context truncation; on a B200
(192 GB HBM3e) you can fit a 405B NVFP4 model with room for
substantial KV cache.

**Accuracy (community + NVIDIA benchmarks).** The published numbers
and community reports converge on:

- **vs FP16:** typically 0.3-1.5 points lower on coding benchmarks
  (HumanEval, MBPP, LiveCodeBench, BigCodeBench) for 70B+ models;
  closes to <0.5 points at 405B.
- **vs FP8:** essentially indistinguishable. NVIDIA's whitepaper
  shows Llama-3.3-70B-NVFP4 within noise of the FP8 variant on
  every benchmark in their suite.
- **vs INT4 (AWQ/GPTQ):** NVFP4 wins by 2-4 points on
  reasoning-heavy and long-context tasks — the floating-point
  format handles outliers in attention much better than fixed-point.
- **vs Q4_K_M (llama.cpp):** mixed. NVFP4 wins on 70B+ models;
  Q4_K_M can match or exceed it on smaller models (<13B) where
  llama.cpp's K-quants have very mature calibration.

**What people are saying (mid-to-late 2025):**

The recurring r/LocalLLaMA take is "if you have a 5090 or B-series,
NVFP4 + vLLM is the new default." Specific themes:

- **Coding ability:** Most users report NVFP4 70B coding models
  (Qwen3-Coder, DeepSeek-Coder-V3, Llama-3.3-70B-Instruct-NVFP4)
  feel indistinguishable from their FP8 / FP16 variants on
  day-to-day code tasks. A handful of benchmark threads show
  small regressions on edge cases (very long single-file
  refactors >32K tokens, where the FP4 numerics start to drift).
- **Tool-use / agentic loops:** Generally positive. The threads
  on r/LocalLLaMA + the LangChain Discord report no measurable
  degradation in tool-call JSON formatting accuracy — the
  structured-output performance hasn't suffered. Where users do
  see issues is in **multi-turn agent loops over 50 turns**
  where small per-turn errors accumulate; the better-calibrated
  v2 NVFP4 model releases mostly addressed this.
- **Long-context degradation:** The most common complaint is
  context >32K tokens showing noticeable quality regression on
  early NVFP4 quants. NVIDIA's Model Optimizer v0.15+ ships
  fixed calibration that mostly addresses this.
- **Calibration matters a lot:** Two NVFP4 quants of the same
  base model can differ by 4-5 benchmark points if one had a
  poor calibration corpus. Stick to NVIDIA-published or
  reputable quantizers (Bartowski, TheBloke-style accounts that
  have moved to FP4).

**When to pick NVFP4:**
- You have Blackwell hardware
- You're using vLLM, TensorRT-LLM, SGLang, or an NVIDIA NIM container
- You want the best speed/quality tradeoff for 13B-405B models
- Long-context (>32K) is rare or you've validated the specific quant

**When NOT to pick NVFP4:**
- You're on Hopper / Ada / Ampere or earlier — use FP8 (Hopper/Ada)
  or Q4_K_M (Ampere and older)
- You're using llama.cpp / LM Studio — they don't
  support NVFP4 (use Q4_K_M or Q8_0 instead)
- You need cross-vendor portability — NVFP4 is NVIDIA-specific
- You're on a model the community hasn't well-calibrated yet

## MXFP4 (Microscaling FP4 — OCP standard)

**What it is.** The Open Compute Project's standardized FP4
format. Same E2M1 value layout as NVFP4 but uses **single-level
per-block FP8 (E8M0) scales** with no per-tensor macro-scale.
Designed to be cross-vendor — both NVIDIA Blackwell and AMD
MI300+ (and future Intel Gaudi) commit to the OCP Microscaling
spec.

**Hardware requirement.** Blackwell tensor cores fully accelerate
MXFP4 (it's a degenerate case of NVFP4 with macro-scale=1). AMD
MI300X / MI325X have FP4 hardware support per the spec but the
software path is less mature than vLLM-on-NVIDIA.

**Accuracy vs NVFP4.** Slightly worse — typically 0.5-1.5 points
behind NVFP4 on the same model + benchmarks. The single-level
scale means MXFP4 can't represent very wide-dynamic-range tensors
as accurately. For most workloads the difference is in the noise
band; for outlier-heavy workloads (long context, vision, very
small batch sizes) NVFP4 wins.

**Speed.** Slightly faster than NVFP4 on Blackwell (no macro-scale
multiply per tensor) but the difference is <5% in practice.

**Software ecosystem.** Current vLLM docs expose MXFP4 on both CUDA
and ROCm backends, and the ROCm platform docs now list `mxfp4`
alongside the supported quantization families for MI300/MI350-class
hosts. TensorRT-LLM has it natively. Exact kernel/model coverage
still varies, so operators should validate their specific backend +
model pairing before promoting MXFP4 to the default lane.

**What people are saying:**
- The cross-vendor angle is the main draw for MXFP4 — same model
  weight file works on NVIDIA Blackwell and AMD MI300+.
- Most threads conclude "if you're NVIDIA-only, NVFP4 wins; if you
  need to support both vendors, MXFP4 is the practical choice."
- Open-source projects that want vendor-neutrality (vLLM upstream,
  TGI, llama.cpp future support) are aligning around MXFP4 as the
  long-term format.

**When to pick MXFP4:**
- You need a single model file that runs on both NVIDIA and AMD
- You're contributing to a project that wants cross-vendor
  portability
- You don't need the last 1-2 points of accuracy

**When NOT to pick MXFP4:**
- You're NVIDIA-only — NVFP4 is strictly better
- You're using Hopper / Ada / earlier — same answer as NVFP4

## FP8 (E4M3 / E5M2)

**What it is.** 8-bit floating-point with two common variants —
**E4M3** (4 exponent, 3 mantissa) for forward weights/activations
and **E5M2** (5 exponent, 2 mantissa) for gradients in training.
For inference, E4M3 is the relevant format.

**Hardware requirement.** Hopper (H100 / H200), Ada (RTX 4090,
L40, L4), Blackwell, AMD MI300+. Pre-Hopper GPUs lack FP8 tensor
cores.

**Accuracy.** Near-FP16 quality at half the memory. The de facto
"safe" datacenter quant.

**Speed.** 2× FP16 on Hopper/Ada; 4× FP16 on Blackwell.

**When to pick FP8:**
- You're on Hopper or Ada (best native format)
- You're on Blackwell but the model isn't available in NVFP4
- You want maximum quality at half the FP16 memory

## Q4_K_M and friends (GGUF / llama.cpp)

**What they are.** llama.cpp's K-quant family (Q4_K_M, Q5_K_M,
Q4_K_S, Q3_K_M, etc.) — integer quantization with per-block
FP16 scales and aggressive calibration. The `_K_M` suffix indicates
"K-quant Medium" — a specific block size + scale precision tradeoff.

**Hardware requirement.** Any. llama.cpp software-dequantizes on
the fly; works on CPU, CUDA, ROCm, Metal, Vulkan.

**Speed.** On a high-end GPU, Q4_K_M is roughly half the speed of
NVFP4 because llama.cpp's CUDA path doesn't use FP4 tensor cores.
On CPU or low-end GPU, Q4_K_M is the fastest practical option.

**Accuracy.** Surprisingly good — for models 1B-13B, Q4_K_M often
matches or beats NVFP4 because llama.cpp's K-quant calibration is
extremely mature.

**When to pick Q4_K_M:**
- You're on llama.cpp / LM Studio (no choice anyway)
- You want maximum cross-platform portability (the same file runs
  on every OS / GPU / CPU)
- You're on pre-Hopper hardware
- You're running smaller models (≤13B)

## unsloth UD-* (Ultra-Detail Dynamic) quants

**What it is.** Unsloth's calibration-aware dynamic-quantization
variant of the GGUF K-quant family. The `UD-` prefix marks a quant
that uses **per-tensor importance scoring** during calibration
(unlike vanilla K-quants which use a single fixed mix), so layers
that matter more for downstream quality keep more bits and layers
that matter less get squeezed harder. The `_XL` suffix indicates
"extra layers" — a tensor mix that holds onto FP16 / Q6_K for the
most quality-critical tensors (typically the attention output
projections and the LM head) while letting the bulk of the weights
ride at the named bit budget.

**Bit budgets seen in practice.**

| Tag | Effective bpw | Notes |
|---|---|---|
| `UD-Q4_K_XL` | ~4.8 | Best speed/quality for 4-bit-class lanes; mirrors `Q4_K_M` shape with calibration-aware mix |
| `UD-Q5_K_XL` | ~5.6 | Mid-band; quality between Q4 and Q6 |
| `UD-Q6_K_XL` | ~6.5 | Quality-leaning lane; common for sharded large models |
| `UD-Q8_K_XL` | ~8.5 | Calibration-aware Q8_0 equivalent; the project's quality default when VRAM allows |
| `UD-IQ3_XXS` | ~3.1 | Frontier-large lane (e.g. 158B+) where the bit budget dominates |
| `UD-IQ4_XS` | ~4.2 | Frontier-large lane with one bit of headroom over IQ3 |

**Hardware requirement.** Any (it's a GGUF format). Runs on
llama.cpp / LM Studio / kobold.cpp on CPU, CUDA, ROCm, Metal,
Vulkan, SYCL. Doesn't use FP4 tensor cores even on Blackwell —
that's NVFP4's job. The UD-* family is the **local-first /
cross-platform** answer to "I want this exact GGUF to run on every
GPU I might own."

**Accuracy vs vanilla K-quants.** UD-XL routinely beats the
same-bit-budget vanilla K-quant by **0.3-0.8 benchmark points** on
HumanEval / MMLU / GSM8K because the importance-weighted
calibration preserves the tensors that matter most. The
difference is largest at the lower bit budgets (UD-IQ3 vs Q3_K_M
is more pronounced than UD-Q8 vs Q8_0).

**Speed.** Identical to the underlying vanilla K-quant — the
calibration choice only affects which tensors get which bits, not
the inference kernel. UD-Q4_K_XL is the same llama.cpp speed as
Q4_K_M; UD-Q8_K_XL is the same as Q8_0.

**MTP head support.** Unsloth-published UD-XL quants for the **Qwen
3.6-A3B family** ship with model-native MTP (Multi-Token Prediction)
heads accessible to llama.cpp via `--spec-type ngram-mod`. The
DeepSeek V4-Flash UD-Q3_K_M does *not* currently ship the MTP head
in this quant (verify per-release). When the MTP head is present,
speculative decoding produces 30-50% throughput gain on the active-
params lane without any external draft model.

**What people are saying:**

- The UD-XL family is the dominant default on r/LocalLLaMA for
  llama.cpp / LM Studio workflows through 2026. Unsloth's
  calibration-aware approach is the recurring "if you have a choice
  between Bartowski's Q4_K_M and unsloth's UD-Q4_K_XL, take the
  unsloth one" advice.
- The naming convention (`UD-Q{bits}_K_XL`) is now stable enough
  that operator config can pattern-match on it (PalLLM's
  `ShippingAppsettingsCurationTests` does exactly this — it
  requires every shipping model identifier to contain `UD-Q` or
  `UD-IQ`).
- For models that have both an NVFP4 release and a UD-Q4_K_XL
  release, NVFP4 wins on a Blackwell box (2× speed, similar
  quality) but the UD-Q4_K_XL is the only path on every other
  GPU class — and it's the format that ports cleanly between
  player machines.

**When to pick UD-XL:**
- You're running llama.cpp / LM Studio with custom
  GGUFs
- You want one model file that works on any GPU (Blackwell down
  to Ampere) and any OS
- You want calibration-aware quality without paying the NVFP4
  hardware tax
- You want MTP-native speculative decoding (Qwen 3.6 lines)
- You're an operator with a curated GGUF library (see
  [`LOCAL_MODELS_INVENTORY.md`](LOCAL_MODELS_INVENTORY.md))

**When NOT to pick UD-XL:**
- You're on Blackwell + vLLM and want maximum throughput — NVFP4
  is 2× faster on this stack
- You're on Hopper + vLLM — FP8 is faster on hardware with E4M3
  tensor cores
- The model you want isn't published by unsloth and the upstream
  has only vanilla K-quants — use the upstream Q4_K_M / Q8_0

## Q8_0 (GGUF / llama.cpp 8-bit)

**What it is.** llama.cpp's 8-bit integer quantization. Per-block
FP16 scale, simple round-to-nearest.

**Hardware requirement.** Any.

**Speed.** Slower than Q4_K_M because of the larger memory
footprint (memory bandwidth dominates). On a GPU, ~70-80% the
throughput of Q4_K_M on the same model.

**Accuracy.** Within 0.1-0.3 points of FP16 on virtually all
benchmarks. The "I want to remove quantization as a variable"
choice.

**When to pick Q8_0:**
- You have plenty of VRAM and you want quality > speed
- You're benchmarking and want to isolate model-quality effects
  from quantization-quality effects
- You're running an evaluation suite where 0.5 benchmark points
  matters

**When NOT to pick Q8_0:**
- You're on Hopper or newer (FP8 is faster and similar quality)
- VRAM is tight (NVFP4 / Q4_K_M cut memory in half with small
  quality loss)

## Recommendation matrix

`HardwareProfiler.RecommendedQuantization` reports one of these
defaults based on detected hardware or an explicit
`PALLLM_GPU_ARCHITECTURE` hint. Operators can always override by
configuring a different model in their inference server.

Detection stays local and no-subprocess: explicit env-var hints win, Linux
NVIDIA model names come from a bounded `/proc/driver/nvidia/gpus/*/information`
read, Linux RAM comes from bounded `/proc/meminfo`, and Windows uses
`GlobalMemoryStatusEx` plus sanitized display-adapter registry strings under
`HKLM\SYSTEM\CurrentControlSet\Control\Video`.

| If your GPU is... | Then use... | Why |
|---|---|---|
| Blackwell (RTX 50, B100/B200/B300, GB200) | **NVFP4** via vLLM / TensorRT-LLM | 2× speed of FP8 at near-FP16 accuracy |
| Hopper (H100, H200, GH200) | **FP8** via vLLM / TensorRT-LLM | Native hardware support; near-FP16 quality |
| Ada (RTX 40, L40, L4) | **FP8** via vLLM | Same as Hopper |
| Ampere (RTX 30, A100, A40) | **unsloth UD-Q4_K_XL** via llama.cpp, or AWQ-INT4 via vLLM | No FP8/FP4 hardware; calibration-aware UD-XL beats vanilla Q4_K_M by 0.3-0.8 points at the same bit budget |
| Older NVIDIA (Turing, Volta) | **unsloth UD-Q4_K_XL** | Same as Ampere; smaller models recommended |
| AMD MI300+/MI350+ | **MXFP4** via vLLM-ROCm | Best cross-vendor 4-bit; validate exact backend/model coverage |
| AMD RDNA (RX 7000, etc.) | **unsloth UD-Q4_K_XL** via llama.cpp ROCm | No matrix-FP4 hardware on consumer AMD; UD-XL ports cleanly |
| Apple Silicon (M-series) | **unsloth UD-Q4_K_XL** via llama.cpp Metal | Excellent llama.cpp support; same file as CUDA/ROCm |
| CPU only | **unsloth UD-Q4_K_XL** (small models) or deterministic-only | llama.cpp is the only practical CPU path |

## What changes at the PalLLM layer

PalLLM is an HTTP client — none of the above changes the
PalLLM code path. What does change:

1. **`HardwareProfiler.GpuArchitecture` + `Fp4TensorCoresLikely`
   + `RecommendedQuantization`** are populated automatically.
   `GET /api/hardware` returns them; the dashboard's hardware
   panel displays them; AI agents read them via
   `pal context` / `/api/describe`.
2. **`/api/quickstart`** surfaces an NVFP4 hint when Blackwell
   is detected and an MXFP4 hint when an AMD Instinct-class
   architecture hint is present.
3. **`scripts/compatibility.json`** lists known-good
   inference-server + quantization pairings the doctor can
   verify.

Switching your inference server from llama-server+UD-Q4_K_XL to
vLLM+NVFP4 on a Blackwell box is purely an operator action:

```powershell
# Stop your current llama-server
# Start vLLM with an NVFP4 model:
docker run --gpus all -p 11434:8000 vllm/vllm-openai:latest \
    --model nvidia/Llama-3.3-70B-Instruct-FP4 \
    --quantization fp4 \
    --max-model-len 8192

# Update PalLLM config — no PalLLM restart needed beyond a
# normal config reload:
$env:PalLLM__Inference__BaseUrl = "http://127.0.0.1:11434/v1/"
$env:PalLLM__Inference__Model = "nvidia/Llama-3.3-70B-Instruct-FP4"
$env:PalLLM__Inference__Enabled = "true"

pwsh ./pal.ps1 play
```

PalLLM sees a 2× speedup on every chat turn (the inference HTTP
client span emitted by `System.Net.Http.*` shrinks proportionally;
the `pal.chat` parent span's `pal.inference_*` tags surface the
new model identifier and lane) without any code change. See
[`OBSERVABILITY.md`](OBSERVABILITY.md) for the actual span
inventory — earlier doc revisions referenced a richer
`Chat.Inference` / `Chat.Plan` hierarchy that was aspirational
and never shipped; one `pal.chat` span per turn plus auto-
instrumentation is what actually runs.

## Agentic / tool-use considerations

This is where the choice matters most. Companion-style apps
(PalLLM, AI assistants, NPC dialogue systems) have these
characteristics:

1. **Many short turns** — small per-turn quality loss can
   accumulate over a long session.
2. **Tool-call JSON precision** — a single wrong character in a
   structured response breaks downstream parsers.
3. **Long shared context** — memory + relationship + world
   snapshot can push prompts to 8-32K tokens.
4. **Latency sensitivity** — the player is waiting; 200ms vs
   2000ms is the line between "feels alive" and "feels broken."

Concrete recommendations:

| Scenario | Recommendation |
|---|---|
| Companion chat (PalLLM-style) | NVFP4 70B if Blackwell, else FP8 (Hopper/Ada) or Q4_K_M (Ampere). Tool-use is fine across all of them. |
| Agentic coding (Claude Code-style) | NVFP4 70B+ on Blackwell. Validate the quant on long-context refactors before committing. Q8_0 is the safest fallback if you have VRAM. |
| NPC dialogue (game integration) | NVFP4 7B-13B is plenty. Latency dominates over quality at this size. |
| Multi-step reasoning agents | NVFP4 70B+ or full FP16. Below 70B, integer quants like Q4_K_M can drift on long chains. |
| Tool use with strict JSON schemas | All formats handle this fine for major models; structured-output mode (`response_format=json_schema`) is more important than the quant. |
| Vision-language combined | NVFP4 wins because vision feature maps benefit from floating-point dynamic range. |

## Future formats (2027 / 2035 outlook)

- **FP6** is being explored for the post-Blackwell architectures
  (NVIDIA "Rubin" generation, expected 2026-27). Trades NVFP4's
  speed for closer-to-FP8 accuracy. Probably useful for the
  long-context tail.
- **MXFP6** standardized by OCP alongside MXFP4; same cross-vendor
  story.
- **Block FP4 with learned scales** — research direction where the
  scaling parameters are trained jointly with the quantization
  rather than calibrated post-hoc. Could close the remaining
  NVFP4-vs-FP16 gap.
- **2035 expectation:** the dominant inference format is likely a
  3-bit or 2-bit floating-point with hardware support, with
  4-bit relegated to "the FP16 of its era" -- high quality but
  using twice the memory of the new default.

## PalLLM Operator Guidance

This is the PalLLM version of the guidance:

1. **Detect Blackwell.** Whatever your framework, look for the
   GPU architecture signal (the same `nvidia-smi` model parsing
   or env-var hint pattern PalLLM uses).
2. **Don't hardcode the quantization choice.** Let the operator
   pick -- but recommend NVFP4 + vLLM or TensorRT-LLM as a Blackwell default.
3. **Surface the quant in your privacy / posture surface.**
   PalLLM operators should be able to see what
   quant their model is running.
4. **Long-context UX:** if your app has variable context (chat
   memory, codebase indexing), warn when context exceeds the
   well-tested NVFP4 range (~32K tokens) on early NVFP4 quants.
5. **Fail over gracefully:** if the inference server has rolled
   out a new quant variant and quality regresses noticeably,
   make it easy to swap back. PalLLM's
   `Inference:CircuitBreakerFailureThreshold` and the
   deterministic fallback are this in miniature.

**Looking for concrete copy-pastable recipes** (vLLM and TensorRT-LLM startup
snippets, prompt templates, monitoring patterns, failure-mode
handlers) for Palworld companion dialogue, screenshot/vision proof,
world-state narration, and release checks? See
[`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md). It's the
applied-engineering companion to this primer for PalLLM operators.

## Related

- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) -- full
  per-tier model-pairing recommendations
- [`HOT_PATH.md`](HOT_PATH.md) — how the inference latency
  budget interacts with the chat hot path
- [`OBSERVABILITY.md`](OBSERVABILITY.md) — the
  `Chat.Inference` span tags `model` + `endpoint` so you can
  validate quality / speed before/after a quant switch
- [`adr/0001`](adr/0001-deterministic-first-reply-pipeline.md) —
  why a quantization regression isn't catastrophic in PalLLM
  (deterministic fallback always works)
