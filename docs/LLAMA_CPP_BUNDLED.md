# Bundled llama.cpp - hardware-aware install + launch recipes

Last audited: `2026-05-22`

> **v1.0 shipping target (Pass 356).** PalLLM v1.0 ships configured
> for a single reference rig: **NVIDIA RTX 3090 (24 GB VRAM) +
> 32 GB DDR4 RAM + AMD Ryzen 7 5800X3D**. See
> [`MINIMUM_REQUIREMENTS.md`](MINIMUM_REQUIREMENTS.md) for the
> authoritative spec. The hardware-tier matrix and per-model
> recipes below remain available via explicit operator flags and
> are documented for transparency, but the shipping default
> recommendation, the smoke-test path, and the `ModelTiers[]`
> config all target the reference rig. Non-target hardware moves
> to the v1.x post-release roadmap — see
> [`POST_RELEASE_ANNEX.md`](POST_RELEASE_ANNEX.md).

Concrete operator recipes for PalLLM's bundled llama.cpp engine. This
doc is the applied companion to [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md)
(vLLM/TensorRT-LLM oriented) and
[`LOCAL_MODELS_INVENTORY.md`](LOCAL_MODELS_INVENTORY.md) (the operator's
curated `D:\Models` library). When something here disagrees with one
of those, this doc is the source of truth for **the bundled
llama-server lane specifically** — the path PalLLM ships as the default
since Pass 339.

> **Honest scope.** PalLLM doesn't bundle a fork of llama.cpp; it
> downloads the upstream `ggml-org/llama.cpp` release binaries on
> demand. The "bundled" part is the install script
> ([`scripts/install-llama-cpp.ps1`](../scripts/install-llama-cpp.ps1))
> + the hardware-aware backend selection. The kernel work
> (CUDA/Vulkan/HIP/SYCL) is all upstream.

## Why this doc exists (Pass 347)

Pass 344 shipped the first version of the install script. It hard-coded
the asset name `llama-<tag>-bin-win-cuda-x64.zip` because that was the
upstream convention at the time. Some time in early 2026, upstream
**split the Windows CUDA build** into two variants (`cuda-12.4` and
`cuda-13.1`) and stopped publishing the monolithic `cuda-x64` asset.
The Pass 344 installer broke silently — the download URL 404s on every
release after the split, and the operator's only recourse was a stale
locally-cached copy.

Pass 347 rewrites the installer to:

1. **Auto-detect the GPU vendor** via `Win32_VideoController` on Windows.
2. **Pick the right backend asset** for the detected hardware
   (`cuda12`, `cuda13`, `vulkan`, `hip`, `sycl`, or `cpu`).
3. **Download the companion `cudart-llama-*.zip` runtime pack** when
   installing a CUDA backend (without those DLLs, `llama-server.exe`
   can't load CUDA at boot).
4. **Default NVIDIA to CUDA 12.4**, not 13.1, because as of May 2026
   CUDA 13.x builds crash on Blackwell `sm_120` with MMQ kernels
   (zenn.dev/toki_mwc benchmark, RTX 5090 reports).
5. **Default Flash Attention to `auto`**, not `on`, because the
   April-2026 `flash_attn_stream_k_fixup` kernel crashed RTX 5090
   Blackwell with Xid 43 after b8680.
6. **Print a per-backend launch recipe** at the end of install so the
   operator knows exactly what flags to use for their hardware.

## Zero-config setup (Pass 349, 352)

The one-line install path detects the operator's GPU vendor, VRAM,
system RAM, and CUDA toolkit; picks the matching backend asset;
downloads the right release; smoke-tests the binary; auto-recommends
the best curated model that fits the detected VRAM; **wires PalLLM's
`appsettings.json` to point at it with the right per-family sampler**
(Pass 352); and optionally auto-launches `llama-server` with the
recommended recipe.

```powershell
# Detect + download + smoke-test + recommend (no PalLLM config change):
pwsh ./scripts/install-llama-cpp.ps1

# Detect + download + smoke-test + recommend + WIRE PalLLM appsettings:
pwsh ./scripts/install-llama-cpp.ps1 -WireConfig

# Detect + download + smoke-test + recommend + wire + launch (one command):
pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch
# (-AutoLaunch implies -WireConfig, so the appsettings update happens
#  BEFORE the blocking llama-server call.)
```

That prints a hardware summary like:

```
PalLLM bundled-llama.cpp installer (Pass 349)
  Platform        : win-x64
  Detected GPU    : nvidia
  Detected VRAM   : 24 GB
  Detected RAM    : 64 GB
  CUDA toolkit    : 12.4
  Backend         : cuda12 (auto)
  Bundle root     : C:\Users\<you>\AppData\Local\Pal\Saved\PalLLM\Bundled\llama.cpp
  Models root     : D:\Models
```

…then downloads the right asset, smoke-tests `llama-server --version`,
and emits a hardware-aware recommendation:

```
Hardware-aware recommendation:
  Model      : Qwen3.6-35B-A3B-UD-Q8_K_XL (MoE, quality)
  Path       : D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf
  Context    : 16384
  GPU layers : 99
  Note       : Qwen3.6-35B-A3B-UD-Q8_K_XL (MoE, quality) fits the detected 24 GB VRAM (needs ≥ 18 GB).
```

Add `-AutoLaunch` to start `llama-server` with that recipe immediately
after install — single command from clone to running server.

### Cross-platform support matrix (Pass 349)

| OS / Arch         | GPU vendor probe                                | VRAM probe                              | RAM probe              | Backend default                       |
|-------------------|-------------------------------------------------|-----------------------------------------|------------------------|---------------------------------------|
| Windows x64       | WMI `Win32_VideoController` + `nvidia-smi`      | `nvidia-smi` then WMI (UINT32 truncates)| WMI `Win32_OperatingSystem` | NVIDIA → `cuda12`; AMD/Intel → `vulkan` |
| Linux x64         | `nvidia-smi`, `rocm-smi`, `lspci`               | `nvidia-smi` / `rocm-smi`               | `/proc/meminfo`        | Monolithic platform asset (per-backend split is Windows-only upstream) |
| macOS arm64       | Apple Silicon (always Metal)                    | `sysctl hw.memsize` (unified memory)    | `sysctl hw.memsize`    | Monolithic platform asset             |
| macOS x64         | `system_profiler` (discrete AMD/NVIDIA)         | `sysctl hw.memsize`                     | `sysctl hw.memsize`    | Monolithic platform asset             |

Each probe is guarded by a `Test-CommandAvailable` check; missing tools
degrade to the next probe rather than crashing the installer. A host
with no detectable GPU lands on the `cpu` backend automatically.

### VRAM → curated-model recommendation matrix

| Detected VRAM       | Recommended model                                    | Context | GPU layers           |
|---------------------|------------------------------------------------------|---------|----------------------|
| ≥ 30 GB             | Qwen3.6-27B-UD-Q8_K_XL (dense, quality)              | 16384   | 99                   |
| ≥ 28 GB             | Gemma-4-31B-it-UD-Q8_K_XL                            | 8192    | 99                   |
| ≥ 18 GB             | Qwen3.6-35B-A3B-UD-Q8_K_XL (MoE — 3B active fits)    | 16384   | 99                   |
| ≥ 6 GB              | Gemma-4-E4B-it-UD-Q4_K_XL (fast-start)               | 8192    | 99                   |
| 2–5 GB              | Gemma-4-E4B-it-UD-Q4_K_XL                            | 4096    | partial (10–20)      |
| < 2 GB / CPU-only   | Gemma-4-E4B-it-UD-Q4_K_XL                            | 4096    | 0 (full CPU)         |

Recommendations walk the catalog highest-quality first and pick the
first model that both (a) exists under `$ModelsRoot` and (b) fits the
detected VRAM. Override with `-ModelProfile` on the connect script
when you want a specific tier regardless of hardware.

### Smoke-test (always on; opt out with `-NoSmokeTest`)

After extracting the binary, the installer execs `llama-server --version`.
If exit code ≠ 0 the installer prints an actionable hint:

- `cudart` / `nvcuda.dll` missing → re-run installer (cudart companion
  zip downloads alongside the CUDA backend).
- `VCRUNTIME` / `MSVCP` missing → install Microsoft Visual C++
  Redistributable (vc_redist.x64.exe).
- Native crash on `--version` → switch to a different backend
  (e.g. `-Backend vulkan`) or check GPU driver health.

This catches the failure here, where the operator can act on it,
instead of letting PalLLM hit a phantom `:8080` an hour later.

## Backend selection matrix

| Detected hardware                | `-Backend auto` picks  | Notes                                                                                              |
|----------------------------------|------------------------|----------------------------------------------------------------------------------------------------|
| NVIDIA GPU (any compute cap)     | `cuda12`               | CUDA 12.4 + MMQ. Blackwell-stable. Use `-Backend cuda13` to opt into 13.1 after your own smoke.    |
| AMD GPU                          | `vulkan`               | Vulkan is robust + driver-free. Use `-Backend hip` only if your ROCm install is healthy.           |
| Intel GPU                        | `vulkan`               | Vulkan works on consumer Arc cards. SYCL via `-Backend sycl` only with Intel oneAPI installed.     |
| No GPU detected                  | `cpu`                  | AVX-512 hosts get ~1-2 tok/s on 7B-13B; plan for the fast-start tier, not Qwen3.6-35B-A3B.         |
| Cross-vendor / forced            | `-Backend <name>`      | The script never overrides an explicit `-Backend`; it just trusts the operator.                    |

## Hardware-tier launch recipes

Every recipe below uses the **Unsloth canonical Qwen3.6 thinking-OFF
sampler** (`--temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5`).
The same numbers ship in `appsettings.json` so PalLLM's per-request
sampler and the operator's manual launch agree. For thinking-ON
(deliberate reasoning lanes), swap to `--temp 1.0 --top-p 0.95`.

### Tier A: RTX 5090 / RTX PRO 6000 (Blackwell, 24-32 GB VRAM)

The quality tier. Boots Qwen3.6-35B-A3B at UD-Q8_K_XL with full GPU
offload. MTP is **net-positive** on Blackwell (per RTX PRO 6000
benchmarks: 1.73x dense, 1.17x MoE).

```powershell
pwsh ./scripts/install-llama-cpp.ps1 -Backend cuda12

& "$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bundled\llama.cpp\current\llama-server.exe" `
    -m D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
    --mmproj D:\Models\mmproj\mmproj-F16.gguf `
    --host 127.0.0.1 --port 8080 `
    -c 16384 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn auto `
    --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --chat-template-kwargs '{\"enable_thinking\":false}' `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5 `
    --spec-type draft-mtp --spec-draft-n-min 1 --spec-draft-n-max 2
```

`--spec-draft-n-max 2` is the Unsloth-documented sweet spot — Qwen3.6
acceptance drops from 83% at n=2 to 50% at n=4.

### Tier B: RTX 4090 / RTX 4080 / RTX 3090 (Ada / Ampere, 16-24 GB VRAM)

The standard mainstream GPU. MTP is **net-negative on single-card MoE**
(per the thc1006 RTX 3090 benchmark of Qwen3.6-35B-A3B), so leave
speculative decoding off until you measure your own replay.

```powershell
pwsh ./scripts/install-llama-cpp.ps1 -Backend cuda12

& "$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bundled\llama.cpp\current\llama-server.exe" `
    -m D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
    --host 127.0.0.1 --port 8080 `
    -c 8192 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn on `
    --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --chat-template-kwargs '{\"enable_thinking\":false}' `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

`--flash-attn on` is fine on Ada/Ampere; the `stream_k_fixup` crash
that drove the `auto` default is Blackwell-specific.

### Tier C: 16 GB GPU or smaller (e.g. RTX 3080, RTX 4070 Ti)

Drop down to the fast-start tier (gemma-4-E4B-it at UD-Q4_K_XL) or
Qwen3.6-27B at UD-Q3 (~17 GB).

```powershell
pwsh ./scripts/install-llama-cpp.ps1 -Backend cuda12

& "$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bundled\llama.cpp\current\llama-server.exe" `
    -m D:\Models\Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf `
    --host 127.0.0.1 --port 8080 `
    -c 8192 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn on `
    --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --chat-template-kwargs '{\"enable_thinking\":false}' `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

### Tier D: AMD Radeon (RDNA3 / RDNA4)

Vulkan is the safer default. On many cards Vulkan also beats HIP for
short-context companion chat. Switch to `-Backend hip` only if you've
already proven ROCm faster on your replay traffic.

```powershell
pwsh ./scripts/install-llama-cpp.ps1 -Backend vulkan
# ... same launch flags as Tier B/C, but without -ngl 99 if VRAM-tight.
```

### Tier E: Apple Silicon (M2 Pro / M3 / M3 Max / M4 Max)

The cross-platform path. Install the macOS asset (`-Platform macos-arm64`)
and let Metal carry the GPU side. MTP is **net-positive** on M3 Max
(+15-45% for Qwen3.5 per anecdotal HF benchmarks); turn it on after
measuring.

```bash
pwsh ./scripts/install-llama-cpp.ps1 -Platform macos-arm64

"$HOME/.local/share/Pal/Saved/PalLLM/Bundled/llama.cpp/current/llama-server" \
    -m ~/Models/Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf \
    --host 127.0.0.1 --port 8080 \
    -c 16384 -np 1 -b 512 -ub 256 \
    --flash-attn auto \
    --cache-prompt --cache-reuse 256 -sps 0.10 \
    --metrics --no-webui \
    --chat-template-kwargs '{"enable_thinking":false}' \
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

### Tier F: CPU-only (AVX-512 or AVX2)

Plan on the fast-start tier; the quality tier isn't realistic without
GPU offload. Expect 1-2 tok/s on 7B-13B GGUFs.

```powershell
pwsh ./scripts/install-llama-cpp.ps1 -Backend cpu

& "$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bundled\llama.cpp\current\llama-server.exe" `
    -m D:\Models\Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf `
    --host 127.0.0.1 --port 8080 `
    -c 4096 -np 1 -b 512 -ub 256 `
    --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --chat-template-kwargs '{\"enable_thinking\":false}' `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

## Per-model recipes (Pass 348)

Each model in the operator's curated `D:\Models` library has a sampler
profile + memory/context shape that's worth pinning explicitly. The
recipes below all assume the Tier B hardware (RTX 4090 / 4080 / 3090
mainstream) launch shape from above and swap only the model-specific
bits.

### Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf (quality tier, MoE)

The shipping default. 39 GB. 3B active params / 35B total, so VRAM
budget is dominated by the active-tensor working set + KV cache, not
the full weights. Reasoning-OFF profile:

```powershell
& "$llamaServer" `
    -m D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
    --host 127.0.0.1 --port 8080 -c 16384 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn auto --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --chat-template-kwargs '{\"enable_thinking\":false}' `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

For Blackwell workstation cards (RTX 5090 / RTX PRO 6000) enable MTP
at the tail: `--spec-type draft-mtp --spec-draft-n-min 1 --spec-draft-n-max 2`.

### Qwen3.6-27B-UD-Q8_K_XL.gguf (quality tier, dense)

The dense Qwen alternative. 35.8 GB. Same sampler as the MoE variant.
Dense models accept MTP better than MoE; per the jarvislabs benchmark,
27B dense gets 1.73x with draft-mtp on RTX PRO 6000.

```powershell
& "$llamaServer" `
    -m D:\Models\Qwen\Qwen3.6-27B-UD-Q8_K_XL.gguf `
    --host 127.0.0.1 --port 8080 -c 16384 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn auto --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --chat-template-kwargs '{\"enable_thinking\":false}' `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

### Gemma-4-31B-it-UD-Q8_K_XL.gguf (dense + vision)

35 GB. Pairs with `mmproj-gemma-4-31B-F16.gguf` (1.2 GB) for the vision
lane via libmtmd. **Known bug** (see Caveats §): the Gemma 4 mmproj
crashes with SIGABRT in `clip_model_loader::load_tensors` on CUDA
backends (upstream issue #21402). Run the vision lane on the **Vulkan
backend** (`-Backend vulkan`) until upstream lands the fix, or skip
mmproj and use the snapshot-vision fallback PalLLM ships
(`SnapshotVisionFallback.Compose`).

```powershell
# CUDA backend - text only (mmproj crashes; see Caveats):
& "$llamaServer" `
    -m D:\Models\Gemma\gemma-4-31B-it-UD-Q8_K_XL.gguf `
    --host 127.0.0.1 --port 8080 -c 8192 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn auto --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5

# Vulkan backend - text + vision (mmproj works):
& "$llamaServer" `
    -m D:\Models\Gemma\gemma-4-31B-it-UD-Q8_K_XL.gguf `
    --mmproj D:\Models\mmproj\mmproj-gemma-4-31B-F16.gguf `
    --host 127.0.0.1 --port 8080 -c 8192 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn auto --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

### Gemma-4-E4B-it-UD-Q4_K_XL.gguf (fast-start tier)

5.1 GB. Boots in seconds; PalLLM's "small" tier in `ModelTiers[]`.
Pairs with `mmproj-F16.gguf` (928 MB) for vision when needed.

```powershell
& "$llamaServer" `
    -m D:\Models\Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf `
    --mmproj D:\Models\mmproj\mmproj-F16.gguf `
    --host 127.0.0.1 --port 8080 -c 8192 -np 1 -b 512 -ub 256 -ngl 99 `
    --flash-attn auto --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5
```

### Qwen3-Coder-Next-UD-Q6_K_XL (coding lane, 3-shard, 73 GB total)

80B MoE / 3B active / 256K native context. Multi-shard GGUF: point `-m`
at the FIRST shard and llama.cpp auto-detects and loads the remaining
two. **Speculative decoding does not work on this model** as of
May 2026 (upstream discussion #21886, error: "load_model: speculative
decoding not supported by this context"). Use coding-lane sampler:

```powershell
& "$llamaServer" `
    -m D:\Models\Qwen\Qwen3-Coder-Next\UD-Q6_K_XL\Qwen3-Coder-Next-UD-Q6_K_XL-00001-of-00003.gguf `
    --host 127.0.0.1 --port 8080 `
    -c 32768 -np 1 -b 2048 -ub 512 -ngl 99 `
    --flash-attn auto --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --temp 0.6 --top-p 0.95 --top-k 20 --min-p 0.0 --presence-penalty 0.0
```

The 256K native context only fits in VRAM on Blackwell workstation
cards. The 32K above is the Unsloth-documented memory-friendly default
for mainstream hardware.

### MiniMax-M2.7-UD-IQ4_XS (heavyweight, 4-shard, 108 GB total)

MoE designed for agentic coding. **Has its own canonical sampler**
that differs from Qwen3.6's — pulling Unsloth from
`unsloth.ai/docs/models/tutorials/minimax-m27`:

- `--temp 1.0 --top-p 0.95 --min-p 0.01 --top-k 40`
- `--prio 3` (highest thread priority)
- `--ctx-size 32768 --batch-size 2048 --ubatch-size 512`

**Critical bug:** CUDA 13.2 builds produce **gibberish output** on
this model. Must run on CUDA 12.4 (PalLLM default) or upgrade to
CUDA 13.3+. The PalLLM installer defaults to `cuda12`, so the
shipping path is safe; only operators who manually passed
`-Backend cuda13` before upstream 13.3 was packaged are at risk.

```powershell
& "$llamaServer" `
    -m D:\Models\MiniMax-M2.7\UD-IQ4_XS\MiniMax-M2.7-UD-IQ4_XS-00001-of-00004.gguf `
    --host 127.0.0.1 --port 8080 `
    -c 32768 -np 1 -b 2048 -ub 512 -ngl 999 `
    --prio 3 --flash-attn on `
    --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --temp 1.0 --top-p 0.95 --top-k 40 --min-p 0.01
```

Memory profile per Unsloth: ~15 tok/s on 128GB unified-memory Mac,
25+ tok/s on 1× 16 GB GPU + 96 GB RAM with MoE offload.

For tighter VRAM, swap to the IQ3_XXS variant
(`MiniMax-M2.7-UD-IQ3_XXS-00001-of-00003.gguf`, ~80 GB total).

### DeepSeekV4-Flash-158B-Q3_K_M.gguf (research lane, 99.9 GB)

Single-file Q3_K_M (not multi-shard, not Unsloth UD). Heaviest model
in the curated library and the only non-UD entry. Plan on CPU-only or
massive-RAM offload; no realistic single-GPU path. Reasoning lanes:

```powershell
& "$llamaServer" `
    -m D:\Models\DeepSeek\DeepSeekV4-Flash-158B-Q3_K_M.gguf `
    --host 127.0.0.1 --port 8080 `
    -c 16384 -np 1 -b 1024 -ub 256 -ngl 99 `
    --flash-attn auto --cache-prompt --cache-reuse 256 -sps 0.10 `
    --metrics --no-webui `
    --temp 0.7 --top-p 0.95 --top-k 40 --min-p 0.0
```

DeepSeek family canonical sampler is the OpenAI-ish defaults; the
research workflow is typically zero-shot reasoning so `presence_penalty`
is omitted. Do not promote this lane for live companion chat — its
context-fill latency dominates the budget. Use it for long proof or
docs-sync.

## Multi-shard GGUF loading

The curated library has three multi-shard models
(`Qwen3-Coder-Next`, `MiniMax-M2.7`/UD-IQ3_XXS,
`MiniMax-M2.7`/UD-IQ4_XS). llama.cpp **auto-loads the remaining shards**
when you point `-m` at the first shard — there is no concatenation
step, and you don't pass each shard individually. The `gguf-split` tool
splits at tensor level (no tensor is cut in half), so `llama_model_loader`
knows exactly which shard holds which tensor.

Just point `-m` at the file ending in `-00001-of-NNNNN.gguf` and the
rest load automatically.

**Known bug (March 2026, upstream issue #21016).** llama-server may
load the wrong shard when the model is in the HuggingFace cache (not
sorted by index before loading). Workaround: use the explicit
`D:\Models\<family>\<variant>\<file>` path, not the HF-cache symlink,
and the shards load in order. The curated library already uses
explicit paths, so this isn't a practical risk for PalLLM.

## Speculative decoding (off by default)

Spec-decoding on Qwen3.6-A3B is **hardware-dependent** in ways the
"just enable it" advice gets wrong. Published benchmarks as of May 2026:

| Hardware                       | Variant                  | Result                        | Source                                                                                                          |
|--------------------------------|--------------------------|-------------------------------|-----------------------------------------------------------------------------------------------------------------|
| RTX 3090 (Ampere) + 35B-A3B    | ngram-mod / draft-mtp    | -3 to -12% (net-negative)    | thc1006 GitHub benchmark, post PR #19493                                                                        |
| RTX PRO 6000 + 27B dense       | draft-mtp                | +73% (1.73x)                  | jarvislabs.ai blog                                                                                              |
| RTX PRO 6000 + 35B-A3B MoE     | draft-mtp                | +17% (1.17x)                  | same                                                                                                            |
| RTX 5090 + Qwen3-8B Q4_K_M     | EAGLE/MTP                | up to 17K tok/s composite     | sitepoint                                                                                                       |
| Apple M3 Max + Qwen3.5         | ngram-mod                | +15-45%                       | community reports                                                                                               |
| AMD Strix Halo + Qwen3.5       | ngram-mod                | +31 to +119%                  | same                                                                                                            |

Take-away: enable spec-decode after measuring your own host, not on
recommendation. The connect script supports it via `-SpecType draft-mtp
-DraftMax 2`, with `n_max=2` matching Unsloth's documented acceptance
sweet spot (83% at n=2, 50% at n=4).

## Operator-facing PalLLM flow

```powershell
# 1. Install the bundled engine (hardware-aware).
pwsh ./scripts/install-llama-cpp.ps1

# 2. Launch llama-server with one of the recipes above.

# 3. Wire PalLLM at the same port. -WriteConfig updates appsettings.json
#    so the next sidecar restart points at the running server. Sampler
#    settings come from appsettings.json (Unsloth Qwen3.6 thinking-OFF
#    canonical, locked in by Pass 347).
pwsh ./pal.ps1 connect llamacpp `
    -ModelPath D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
    -WriteConfig

# 4. Start PalLLM.
pwsh ./pal.ps1 play

# 5. Prove the running model endpoint exposes catalog + metrics evidence.
pwsh ./pal.ps1 models probe
```

## Known bugs and caveats (Pass 348)

Cross-referenced from upstream llama.cpp issues + Unsloth model docs as
of May 2026. Reflect these in your launch path before promoting a lane.

### CUDA-13.2 gibberish on MiniMax-M2.7 — pinned

CUDA 13.2 builds produce **garbled output** on MiniMax-M2.7-* GGUFs.
The Unsloth M2.7 doc page calls this out explicitly. PalLLM defaults
to `cuda12` (CUDA 12.4), which is unaffected. If you manually selected
`-Backend cuda13` while running the M2.7 quality tier, drop back to
`cuda12` or wait for the cuda-13.3 asset.

### Gemma-4 mmproj SIGABRT on CUDA — upstream #21402

Loading `mmproj-gemma-4-31B-F16.gguf` on any CUDA backend crashes
`llama-server` with `SIGABRT` in `clip_model_loader::load_tensors`.
Two safe paths exist today:

1. **Vulkan backend** (`pwsh ./scripts/install-llama-cpp.ps1 -Backend vulkan`)
   — vision lane works.
2. **CUDA backend + no mmproj** — text-only Gemma 4 31B is unaffected;
   PalLLM's `SnapshotVisionFallback.Compose` covers the visual-context
   role using `GameWorldSnapshot` instead of the live multimodal call.

The smaller `mmproj-F16.gguf` (paired with Gemma-4-E4B) is **not
affected** by the same issue — the crash is specific to the 31B
projector's tensor layout.

### Qwen3-Coder-Next speculative decoding broken — upstream #21886

Attempting `--spec-type draft-*` on Qwen3-Coder-Next currently
errors with the exact string `load_model: speculative decoding not supported by this context`.
The coding lane runs without spec-decode until upstream lands the
fix. The connect script's `-SpecType none` default (unchanged from
Pass 347) preserves this; do not flip it for the coding lane.

### llama-server multi-shard wrong-index from HF cache — upstream #21016

When loading a multi-shard GGUF from the HuggingFace cache directory,
llama-server may try to open the wrong shard first. Workaround: use
explicit file paths (the curated `D:\Models\<family>\<variant>\<file>`
layout) rather than the HF-cache symlinks. The curated library already
uses explicit paths.

### Flash-Attention stream_k_fixup crash on RTX 5090 — upstream #21564

`--flash-attn on` after build `b8680` crashed RTX 5090 (Blackwell sm_120)
with NVIDIA Xid 43 in the `flash_attn_stream_k_fixup` kernel. PalLLM's
connect script defaults `-FlashAttn auto` (Pass 347), which sidesteps
this by letting llama-server pick per host.

## MoE offloading recipes (Pass 350)

Four of the seven curated families are mixture-of-experts (Qwen3.6-35B-A3B,
Qwen3-Coder-Next, MiniMax-M2.7-UD-IQ3_XXS, MiniMax-M2.7-UD-IQ4_XS).
MoE models are special because most of the weight bytes are "experts"
that are only consulted for a fraction of tokens. **Moving those
expert FFN tensors to CPU/RAM is the only way to run a 39 GB Q8 MoE
on a 12 GB consumer card**, and llama.cpp ships two complementary
flags for it:

### `--n-cpu-moe N` (the simple one)

> Source: David Sanftenberg's Medium guide, Doctor-Shotgun's HF blog.

Offloads the **deepest N layers' expert FFN tensors** to CPU/RAM,
keeping everything else (attention, the active expert windows, KV
cache) on GPU. Higher N = more CPU offload = lower VRAM footprint
but slower decode.

```
llama-server -m Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf \
    -c 16384 -ngl 999 --n-cpu-moe 30 --flash-attn auto \
    -b 2048 -ub 2048
```

The Pass 350 install script computes a sensible default N from the
detected VRAM gap and prints it in the recommendation. The connect
script's `-NCpuMoe N` parameter is the manual override.

### `--override-tensor <regex>=<buffer>` (the surgical one)

> Source: upstream PR #11397.

Regex-based control over which tensors go where. The canonical "all
experts to CPU" pattern is:

```
--override-tensor "\.ffn_.*_exps\.weight=CPU"
```

To keep experts only on the deepest layers (20+ here) on CPU:

```
--override-tensor "[2-9][0-9]\.ffn_.*_exps\.=CPU"
```

The connect script exposes this as `-OverrideTensor "<pattern>=CPU"`.

### MoE VRAM gating in the recommendation

The Pass 350 `Get-RecommendedModel` honors a `MoeMinVramGb` floor per
curated family. For Qwen3.6-35B-A3B it's 8 GB — below the model's
"full-fit" 18 GB threshold but enough for active tensors + KV cache
when experts are offloaded.

| Detected VRAM | Qwen3.6-35B-A3B recommendation                                                            |
|---------------|-------------------------------------------------------------------------------------------|
| ≥ 18 GB       | Full GPU offload, `--n-cpu-moe 0`                                                         |
| 12-17 GB      | `--n-cpu-moe ~25` (experts on CPU for deepest ~25 layers, attention on GPU)               |
| 8-11 GB       | `--n-cpu-moe ~50` + `-ctk q8_0 -ctv q8_0` (quantized KV halves per-token memory)          |
| < 8 GB        | Falls back to Gemma-4-E4B-it-UD-Q4_K_XL fast-start tier                                   |

## KV-cache-aware VRAM math (Pass 350)

Pass 349's recommendation gated on "model file fits VRAM". Pass 350
also accounts for the **KV cache** that grows with context size.
Formula (Grouped-Query Attention):

```
KV bytes/token = 2 × layers × kv_heads × head_dim × bytes_per_elem
```

For Qwen3.6-35B-A3B (80 layers, 8 KV heads, 128 head dim, F16): 327 KB
per token. At 16 384 ctx that's **~5.4 GB** of KV cache alone —
substantial against a 24 GB card. At 65 536 ctx it's **~21 GB**.

| Lever                                  | Effect on KV cache                                                                          |
|----------------------------------------|---------------------------------------------------------------------------------------------|
| `-ctk q8_0 -ctv q8_0`                  | Halves the per-token bytes (F16 → INT8). Recommend for tight-VRAM lanes; replay strict-JSON tests first |
| `-ctk q4_0 -ctv q4_0`                  | Quarters it. Keep off the player path until PalLLM strict-route replays prove no drift      |
| Drop context from 64K to 16K           | Multiplies budget by 4× without quality drop for short-turn companion chat                  |
| `--n-cpu-moe N`                        | Doesn't help KV (KV is attention-side); but frees up VRAM for KV by moving experts to CPU   |

The recommendation engine flips `QuantizedKv = true` when the detected
VRAM is more than ~4 GB below the model's full-fit `MinVramGb` —
this is the threshold where halving KV makes the model bootable.

## Backend-specific safety nets (Pass 350)

| Backend       | Don't use                                  | Why                                                                                                 |
|---------------|--------------------------------------------|-----------------------------------------------------------------------------------------------------|
| All MoE       | `--no-mmap`                                | Upstream issue #14999 — memory critical errors when `--no-mmap` is combined with MoE expert loading. |
| HIP / ROCm    | `--mlock`                                  | Upstream ROCm #4903 — ROCm forces shared memory under `--mlock`, defeating the lock.                |
| Apple Silicon | n/a — DO use `--mlock --prio 2`            | Unified-memory architecture benefits from locking; Hannecke's Apple Silicon tuning guide.            |
| CUDA 13.0-13.2 | this entire toolkit band on MiniMax M2.7  | Gibberish output confirmed by Unsloth. Use CUDA 12.4 (PalLLM default) or 13.3+.                     |
| Blackwell sm_120 + CUDA 13.x | the CUDA 13 backend until 13.3+ | MMQ crashes. CUDA 12.4 is the safe pair.                                                            |

The Pass 350 install script does NOT auto-add `--no-mmap` for any
MoE recommendation and does NOT auto-add `--mlock` when the resolved
backend is `hip`. Apple Silicon launch recipes DO get `--mlock --prio 2`.

## Multi-GPU + advanced perf knobs

These flags are out-of-scope for PalLLM's single-player default but
worth a paragraph when an operator brings dual-GPU hardware or wants to
squeeze a Blackwell workstation card harder.

| Flag                              | When to use                                                                                                         |
|-----------------------------------|---------------------------------------------------------------------------------------------------------------------|
| `--tensor-split 2,1`              | Asymmetric VRAM dual-GPU (e.g. 24 GB + 12 GB cards). Comma ratios match each GPU.                                   |
| `--split-mode graph`              | Tensor parallelism at GGML-graph level. 3-4x perf on dual Blackwell vs default layer-split; keeps both GPUs at 100%. |
| `--threads 1`                     | When GPU-offloading 99% of layers: counterintuitively +43% throughput per Ventus Servers 2026 tuning guide.         |
| `--threads-batch <hw-cores>`      | Prompt-processing thread count separate from generation. Useful for long-context prefills.                          |
| `--prio 3`                        | Highest-priority worker threads. MiniMax-M2.7 Unsloth recipe recommends this; otherwise leave at 0 (normal).        |
| `--mlock`                         | Lock weights into RAM (no swap). Apple Silicon + memory-headroom: yes. Tight-memory: thrashes.                      |
| `--no-mmap --cache-ram <MiB>`     | Low-latency lane: skip mmap, use explicit host cache. Pair with `--parallel 1`.                                     |

NCCL is used automatically when llama.cpp was compiled with NCCL
support — relevant on Hopper NVLink dual-GPU setups.

## What this doc deliberately does NOT cover

- **vLLM / TensorRT-LLM recipes** — those live in
  [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md). vLLM is the operator's
  high-config option; llama.cpp is the bundled and default.
- **GGUF quantization theory** — that's
  [`QUANTIZATION.md`](QUANTIZATION.md).
- **MTP / draft-MTP / EAGLE pedagogy** — see
  [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) for the abstract
  recipes and PalLLM's role mesh.
- **The actual model files** — see
  [`LOCAL_MODELS_INVENTORY.md`](LOCAL_MODELS_INVENTORY.md) for the
  curated `D:\Models` library.
