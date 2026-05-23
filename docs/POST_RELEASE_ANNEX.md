# Post-release annex

Last audited: `2026-05-22`

Surfaces that **are present in the codebase** but are NOT in the
v1.0 shipping support matrix per
[`MINIMUM_REQUIREMENTS.md`](MINIMUM_REQUIREMENTS.md). Each entry
documents (1) what's deferred, (2) why, (3) what would have to be
true to bring it back in scope, and (4) where the code lives.

This is the inverse companion to MINIMUM_REQUIREMENTS.md: that doc
says "v1.0 ships for this rig"; this doc says "here's what was
considered and intentionally pushed past v1.0."

## Why an annex exists

Passes 339-355 built broad multi-hardware / multi-model support
under the working assumption that "more configs supported = better
project." A senior-dev review (Pass 353) flagged this as scope
sprawl: each path multiplies test surface, doc surface, and bug
variance. Pass 356 narrowed v1.0 to a single reference rig
(RTX 3090 / 32 GB DDR4 / 5800X3D). The code for the broader
matrix wasn't deleted because:

1. **It's tested.** Ripping working tested code is debt of its
   own kind — undoing the work is harder than disabling it.
2. **The operator might want it.** Advanced operators with
   different hardware can still pass explicit flags to opt into
   the broader matrix.
3. **Post-v1.0 expansion has a clear path back.** Each entry
   below names the contract for re-promotion.

## Deferred: alternate GPU backends

**What's deferred:** Vulkan (AMD/Intel/cross-vendor), HIP
(AMD ROCm), SYCL (Intel oneAPI), CUDA 13.x (Blackwell-experimental).

**Why:** v1.0 ships for a single NVIDIA Ampere card. The
backend-specific safety nets (HIP `--mlock` issue, SYCL toolchain,
Vulkan-vs-CUDA performance variance, CUDA 13.0-13.2 broken band)
each add documentation and operator-bug-report surface that's not
warranted for one targeted card.

**Re-promotion contract:** validated on a real card of each
class with: cold-start + first-chat + 100-message replay + the
existing 35-test adversarial suite all passing. Plus a public
benchmark page comparing tok/s and parse stability vs the
reference rig.

**Where in code:** `install-llama-cpp.ps1` keeps `-Backend
vulkan|hip|sycl|cuda13` accepting opt-in operators. Not removed
from the `ValidateSet`; just not the default.

## Deferred: Apple Silicon / macOS

**What's deferred:** macOS arm64 + macOS x64 GPU detection,
Metal backend recipes, `--mlock --prio 2` Apple Silicon tuning.

**Why:** PalLLM's UE4SS bridge is Windows-only — the in-game
companion experience requires Palworld + UE4SS, which means
Windows. The sidecar runtime runs cross-platform but the
end-to-end product story stops at Windows. Spending v1.0 effort
on Apple Silicon is paying for a path most users can't complete.

**Re-promotion contract:** when (a) a non-Windows Palworld
bridge surface lands (UE4SS-on-Wine, Crossover, official native
client, etc.), AND (b) Apple Silicon has been validated against
the existing adversarial test suite.

**Where in code:** install-llama-cpp.ps1's macOS branches
(`macos-arm64`, `macos-x64`) still present + functional;
hardware detection helpers (`Get-DetectedGpuVendor`,
`Get-DetectedVramGb`, `Get-DetectedSystemRamGb`) still handle
macOS. They just don't show up in operator-facing recommendation
output for v1.0.

## Deferred: CPU-only inference

**What's deferred:** running the sidecar with no GPU. Bundled
engine's `cpu` backend asset still installs; PalLLM's
recommendation engine no longer suggests CPU-only as a workable
configuration.

**Why:** CPU-only inference of 7B-13B GGUFs lands at ~1-2 tok/s
on AVX-512. That's not a player-experience-acceptable latency
for live companion chat. PalLLM's deterministic-fallback
director makes the no-GPU case work *without* an LLM, which is
strictly better than waiting 30 seconds per chat turn.

**Re-promotion contract:** if dedicated NPUs (Intel AI Boost,
Snapdragon Hexagon, Apple Neural Engine) reach a tok/s on
PalLLM's tier-1 model that's competitive with the reference rig's
RTX 3090. When that happens, NPU-specific backend support gets
its own re-promotion path.

**Where in code:** `Get-RecommendedModel` retains the
CPU-fallback branch with `-ngl 0`; it's just never the default
recommendation on a host that has the reference rig.

## Deferred: multi-GPU

**What's deferred:** `--tensor-split` for asymmetric VRAM,
`--split-mode graph` for symmetric pairs, dual-GPU detection in
the install script's hardware summary.

**Why:** the reference rig is single-GPU. Multi-GPU configurations
are rare in the indie-game-mod demographic and each topology
(symmetric matched, asymmetric mixed, NVLink-bridged, PCIe-only)
has its own tuning. v1.0 supports the modal user; rare topologies
are post-release.

**Re-promotion contract:** a public benchmark page showing
dual-3090 / 3090+4090 / 4090+5090 with the reference rig as
baseline, plus the adversarial-test suite plus a multi-GPU
specific test fixture.

**Where in code:** `Get-DetectedGpuCount` retained;
`-TensorSplit` and `-SplitMode` parameters retained in
`connect-llamacpp.ps1`. The install script's "Multi-GPU
detected" hint section is still present but the v1.0 default
recommendation is single-GPU.

## Deferred: heavyweight models

**What's deferred (from default recommendation):**
- `MiniMax-M2.7-UD-IQ3_XXS` (80 GB, 3 shards)
- `MiniMax-M2.7-UD-IQ4_XS` (108 GB, 4 shards)
- `Qwen3-Coder-Next-UD-Q6_K_XL` (73 GB, 3 shards)
- `DeepSeekV4-Flash-158B-Q3_K_M` (99.9 GB)

**Why:** total memory budget on the reference rig is
24 GB VRAM + 32 GB RAM = 56 GB. These models exceed that even
with aggressive offload. They're still in the curated
[`LOCAL_MODELS_INVENTORY.md`](LOCAL_MODELS_INVENTORY.md) — the
operator owns those files on disk — but PalLLM's recommendation
engine doesn't propose them for the v1.0 reference rig.

**Re-promotion contract:** when v1.x re-promotes a hardware tier
that fits them (e.g. 64 GB+ RAM rig, dual-GPU rig, or workstation
class card with 32+ GB VRAM), each heavyweight family gets its
own recipe in `LLAMA_CPP_BUNDLED.md` and its own catalog entry.

**Where in code:** per-family recipes still present in
`docs/LLAMA_CPP_BUNDLED.md` § "Per-model recipes" (Pass 348).
The `-ModelProfile minimax|qwen3-coder|deepseek` values in
`connect-llamacpp.ps1` are still accepted (Pass 351). Operators
can still launch these manually with
`llama-server -m D:\Models\<family>\<file>.gguf`.

## Deferred: operator-extensible UX surface

**What's deferred:** a "your hardware doesn't match the reference
rig — here's how to find your tier" wizard. Currently the install
script's mismatch path just emits a warning + log message.

**Why:** v1.0 lands on the reference rig fast. A wizard for
non-target hardware would create the false impression that
non-target rigs are fully supported.

**Re-promotion contract:** when a real post-release tier (e.g.
"v1.1: dual-3090 rigs", "v1.2: macOS arm64 sidecar-only mode")
is committed to a roadmap, the wizard ships with that release.

## Surfaces that REPLACE local inference for below-reference hardware

These are NOT deferred; they're the shipping escape paths Pass
357 added when the local-on-below-reference path was cut:

### Cloud API (any OpenAI-compatible provider)

`scripts/connect-cloud.ps1` (Pass 357) wires PalLLM's
`/v1/chat/completions` to any OpenAI-compatible endpoint. Preset
providers: openai, groq, together, openrouter, deepseek, mistral.
`custom` lets the operator point at any gateway. Recipe lives in
[`MINIMUM_REQUIREMENTS.md`](MINIMUM_REQUIREMENTS.md) §
"Escape path #1".

### Remote PC running llama-server

`scripts/connect-llamacpp.ps1` (Pass 348+) already accepts a
`-LlamaCppUrl` pointing at a non-loopback host. The remote
host runs the standard reference-rig install
(`install-llama-cpp.ps1 -AutoLaunch`) with auth enabled
(Pass 354 startup guard refuses to boot otherwise on a
non-loopback bind). The client points at the remote URL. No
new script needed. Recipe in
[`MINIMUM_REQUIREMENTS.md`](MINIMUM_REQUIREMENTS.md) §
"Escape path #2".

## Surfaces explicitly NOT deferred

These were considered for deferral and kept because they reach
the reference-rig operator:

- **Bundled engine install + smoke test + auto-launch** (Passes
  344-353): keystone of the operator's first run.
- **Per-family sampler propagation** (Pass 352): the reference
  rig runs the Qwen3.6 family, which has its own canonical
  sampler that overrides llama-server defaults via PalLLM's
  request body.
- **MoE offloading via `--n-cpu-moe`** (Pass 350): load-bearing
  for the reference rig because the 39 GB Qwen3.6-35B-A3B
  doesn't fit in 24 GB VRAM alone.
- **KV-cache math** (Pass 350): the reference rig's context
  budgets get tight at 16K+; the recommendation engine needs
  to subtract KV before deciding fit.
- **Startup auth guard** (Pass 354): production safety, applies
  to any deployment regardless of rig.
- **Bridge ingest adversarial tests** (Pass 355): production
  safety; the trust boundary between Lua and the runtime is
  the same on every rig.
