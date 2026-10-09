# llama.cpp bundled local engine

Last audited: `2026-06-05`

This is the operator guide for PalLLM's bundled local engine. PalLLM talks to
one local runtime only: `llama.cpp` `llama-server`, using its
OpenAI-compatible `/v1/chat/completions` endpoint.

> **v1.0 shipping target:** use the hardware floor in
> [`MINIMUM_REQUIREMENTS.md`](MINIMUM_REQUIREMENTS.md). Below-reference players
> should use the cloud API or remote-PC escape paths there. Deferred hardware
> experiments live in [`POST_RELEASE_ANNEX.md`](POST_RELEASE_ANNEX.md), not in
> this active launch guide.

## Supported local model set

The supported local catalog is intentionally narrow:

| Lane | GGUF | Role | Default profile |
| --- | --- | --- | --- |
| Fast Worker | `Qwen3.5-9B-UD-Q6_K_XL.gguf` | Text turns, build/tool drafts, routine companion replies | `qwen35` |
| Smart multimodal Edge | `gemma-4-12b-it-UD-Q6_K_XL.gguf` | Slower reasoning, screenshot/vision proof, final arbitration | `gemma` |

Both entries are **single-shard** GGUF files in the curated model inventory.
Keep any extra local downloads out of PalLLM's supported catalog until a pass
adds source receipts, launch proof, sampler proof, and route replay.

## Backend matrix

Use `scripts/install-llama-cpp.ps1` to pick and install the llama.cpp build.

| Host | Default backend | Notes |
| --- | --- | --- |
| NVIDIA Windows | `cuda12` | PalLLM defaults to CUDA 12.4 because the CUDA 13.x band has active Blackwell risk. |
| AMD / Intel GPU | `vulkan` | Most portable non-NVIDIA path. |
| AMD ROCm Linux | `hip` | Only when the host already has a working ROCm stack. |
| Intel oneAPI | `sycl` | Operator proof lane. |
| CPU only | `cpu` | Deterministic-only or remote/cloud escape path; not a normal live-play target. |

The installer also exposes `cuda13`, but treat CUDA `13.0-13.2` as
known-risk. On that band, prefer CUDA 12.4 or Vulkan until route replay proves
stable output on the target host.

## Recipe: Qwen3.5 fast Worker

```powershell
pwsh ./scripts/connect-llamacpp.ps1 `
  -ModelPath D:\Models\Qwen\Qwen3.5-9B-UD-Q6_K_XL.gguf `
  -Model Qwen3.5-9B-UD-Q6_K_XL `
  -ModelProfile qwen35 `
  -ContextSize 8192 `
  -GpuLayers 99 `
  -WriteConfig
```

Sampler profile:

```text
--temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

Thinking is off by default. `connect-llamacpp.ps1` emits
`--chat-template-kwargs '{"enable_thinking":false}'` for the Qwen profile so
manual launches and PalLLM's request body agree.

## Recipe: Gemma 4 smart multimodal Edge

```powershell
pwsh ./scripts/connect-llamacpp.ps1 `
  -ModelPath D:\Models\Gemma\gemma-4-12b-it-UD-Q6_K_XL.gguf `
  -Model gemma-4-12b-it-UD-Q6_K_XL `
  -ModelProfile gemma `
  -ContextSize 8192 `
  -GpuLayers 99 `
  -WriteConfig
```

Sampler profile:

```text
--temp 0.7 --top-p 0.95 --top-k 20 --min-p 0.0
```

For screenshot or vision proof, wire a matching mmproj only after a local
Palworld screenshot replay passes. Upstream llama.cpp issue `#21402` tracks a
Gemma-4 mmproj `SIGABRT` crash in CUDA paths, with
`clip_model_loader::load_tensors` in the failing trace. Use Vulkan or keep the
Gemma lane text-only until that exact host and model/projector pair is proven.

## Speculative decoding

**Speculative decoding is off by default.** `Qwen3.5 / Gemma 4 MTP` is a
hardware- and route-specific proof lane, not a player-facing default. Before
promotion, record side-by-side replay for companion chat, structured JSON,
tool-call routes, screenshot loops, and save-replay parsing with:

- served model id from `/v1/models`
- exact llama.cpp build and backend
- TTFT, ITL, acceptance rate, parse success, and fallback behavior
- the no-spec baseline kept available

Strict JSON, tool-call, judge, save-replay, and docs-sync routes stay no-spec
until their own proof exists.

## KV-cache-aware VRAM math

The installer budgets model weights plus KV cache. The rough F16/BF16 formula:

```text
KV bytes/token = 2 * layers * kv_heads * head_dim * bytes_per_element
KV GiB = KV bytes/token * context_tokens / 1024^3
```

Both supported local entries default to 8192 context in operator recipes. Raise
context only after measuring prompt-cache behavior and VRAM headroom on the
target host.

## MoE offloading recipes

The current supported Qwen3.5 and Gemma 4 lanes do not require MoE offload, but
the connector keeps explicit knobs for operator experiments:

```powershell
pwsh ./scripts/connect-llamacpp.ps1 -NCpuMoe 16 -OverrideTensor "\.ffn_.*_exps\.weight=CPU"
```

`--n-cpu-moe` moves a count of expert layers to CPU RAM. `--override-tensor`
uses regex placement for more precise control. Do not promote either path
without route replay, RAM pressure measurements, and a rollback recipe.

## Backend-specific safety nets

- `#14999`: MoE plus `--no-mmap` can create severe memory pressure. Use
  `--no-mmap` only with measured host-cache benefit.
- `#4903`: HIP plus `--mlock` has platform-specific stability concerns. Keep
  `--mlock` off on ROCm unless a host-specific proof says otherwise.
- Apple Silicon experiments may use `--mlock --prio 2` when unified memory
  headroom is proven. This is not the Windows reference-rig default.

## Multi-GPU + advanced perf knobs

`connect-llamacpp.ps1` exposes `-TensorSplit`, `-SplitMode`, `-CudaDevices`,
`-Prio`, `-PrioBatch`, `-Poll`, `-CtxCheckpoints`, and
`-CtxCheckpointTokens`. These are proof-lane controls. Keep the default single
GPU, single-player lane until a measured replay shows lower latency without
parse regressions.

## Promotion rule

A local model or backend becomes trusted only after the evidence packet exists:

- exact GGUF path and hash
- exact mmproj path and hash when vision is involved
- llama.cpp build tag and backend
- `/v1/models` identity receipt
- PalLLM route replay results
- rollback command

If that packet is missing, the lane is experimental even when it launches.
