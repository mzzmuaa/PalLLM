# Minimum requirements

Last audited: `2026-05-22`

PalLLM v1.0 ships configured for a specific **reference rig**. Other
hardware configurations may work via opt-in advanced flags, but they
are not in the shipping support matrix and aren't covered by tests
or operator-facing documentation. See
[`POST_RELEASE_ANNEX.md`](POST_RELEASE_ANNEX.md) for the deferred
surfaces and the contract for when they might return.

## The reference rig

| Component | Minimum                                       | Notes                                                                                              |
|-----------|-----------------------------------------------|----------------------------------------------------------------------------------------------------|
| **GPU**   | Single NVIDIA RTX 3090 (24 GB VRAM, Ampere)   | Single-GPU only. Multi-GPU and non-NVIDIA paths are post-release.                                  |
| **RAM**   | 32 GB DDR4                                    | Required to hold MoE expert tensors offloaded from VRAM via `--n-cpu-moe`.                          |
| **CPU**   | AMD Ryzen 7 5800X3D (Zen 3 + 96 MB L3 cache)  | The L3 cache is load-bearing for prompt-processing throughput on the partial-offload MoE path.     |
| **OS**    | Windows 10 / 11 x64                           | Linux x64 supported for the bundled engine but not for the in-game UE4SS bridge.                   |
| **Disk**  | ~80 GB free                                   | 39 GB for the quality-tier model + 5 GB for the fast-start tier + ~30 GB for cache/logs headroom.   |

## What runs on the reference rig

| Tier         | Model                                                | Memory pattern                                              |
|--------------|------------------------------------------------------|-------------------------------------------------------------|
| Quality (default) | `Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf` (39 GB MoE)   | `--n-cpu-moe ~25` puts expert FFN tensors of the deepest 25 layers in RAM; attention + active experts + KV cache stay on VRAM. |
| Fast-start (PalLLM small tier) | `Gemma-4-E4B-it-UD-Q4_K_XL.gguf` (5.1 GB) | Full GPU offload (`-ngl 99`). Boots in seconds.             |

PalLLM's tier orchestrator boots on the fast-start tier so the
companion is responsive within seconds of `pal play`, then
auto-graduates to the quality tier the moment it appears in
`/v1/models`. No operator action required between tiers.

## What's NOT in scope for v1.0

The bundled engine code retains paths for these, but they are NOT
covered by shipping defaults, NOT validated against the reference
rig, and NOT supported in the v1.0 release notes:

- **CPU-only inference.** Target rig has a 24 GB GPU; CPU-only
  paths are deferred to post-release.
- **Apple Silicon / macOS.** Bundled engine downloads the
  platform asset but PalLLM's UE4SS bridge is Windows-only.
  Mac path is dev-only for the sidecar.
- **AMD / Intel GPUs (Vulkan / HIP / SYCL backends).** Available
  via explicit `-Backend vulkan|hip|sycl` on the installer, but
  not in the shipping defaults and not in operator-facing UX.
- **Multi-GPU.** `--tensor-split` and `--split-mode graph` flags
  exist in the connect script but are not validated for v1.0.
- **Heavyweight models.** `MiniMax-M2.7` (80-108 GB),
  `Qwen3-Coder-Next` (73 GB), `DeepSeekV4-Flash-158B` (99.9 GB).
  These sit in the operator's curated library
  ([`LOCAL_MODELS_INVENTORY.md`](LOCAL_MODELS_INVENTORY.md)) and
  remain launchable via `llama-server -m <path>` directly, but
  PalLLM's recommendation engine doesn't propose them on the
  reference rig and they aren't in `ModelTiers[]` defaults.

## Why this narrowing?

A senior-dev review (Pass 353-356) noted that PalLLM was trying to
"kinda work for everyone" with no canonical target. That's a
common indie-project trap: every hardware path multiplies test
surface, doc surface, and bug-report variance without proportionally
multiplying users served. Pass 356 fixes this by:

1. **Picking one specific rig** that's affordable, common in the
   gaming demographic, and powerful enough to run the quality
   tier with proper MoE offload.
2. **Letting v1.0 ship for that rig** while keeping post-release
   doors open via opt-in flags and the catalog in
   [`POST_RELEASE_ANNEX.md`](POST_RELEASE_ANNEX.md).
3. **Reducing operator bug-report surface** so when someone
   says "PalLLM doesn't work for me," the first question
   ("are you on the reference rig?") has a crisp answer.

## What if my hardware is bigger?

It works. Beefier GPUs (RTX 4090, RTX 5090, RTX PRO 6000) run
the quality tier with less or no CPU offload (lower
`--n-cpu-moe N`), faster. The reference rig is the **minimum**,
not the recommended maximum. The
[`LLAMA_CPP_BUNDLED.md`](LLAMA_CPP_BUNDLED.md) advanced section
covers what to flip when you have more VRAM to play with.

## What if my hardware is smaller?

PalLLM v1.0 does NOT run a local LLM on hardware below the
reference rig. The bundled engine install detects off-target
hardware and **skips the local model recommendation entirely**
(Pass 357), pointing at two shipping escape paths instead:

### Escape path #1: cloud API

PalLLM speaks OpenAI-compatible `/v1/chat/completions`, so any
cloud provider that exposes that endpoint is one config-write
away. Use `scripts/connect-cloud.ps1`:

```powershell
# Using OpenAI's GPT-4o-mini
pwsh ./scripts/connect-cloud.ps1 \
    -Provider openai \
    -Model gpt-4o-mini \
    -ApiKey $env:OPENAI_API_KEY \
    -WriteConfig

# Using Groq (LPU-accelerated open-weight)
pwsh ./scripts/connect-cloud.ps1 \
    -Provider groq \
    -Model llama-3.1-70b-versatile \
    -ApiKey $env:GROQ_API_KEY \
    -WriteConfig

# Using a custom OpenAI-compatible gateway
pwsh ./scripts/connect-cloud.ps1 \
    -Provider custom \
    -BaseUrl https://my-gateway.example.com/v1/ \
    -Model my-model \
    -ApiKey $env:GATEWAY_KEY \
    -WriteConfig
```

Supported provider presets: `openai`, `groq`, `together`,
`openrouter`, `deepseek`, `mistral`, `custom`. The script
optionally probes `/v1/models` (`-Probe`) before writing config
so a typo'd key fails fast.

**Security:** the API key writes to `appsettings.json`. Prefer
setting `$env:PalLLM__Inference__ApiKey` from a secrets manager
when possible; both routes work but env vars don't risk
committing the key to version control.

### Escape path #2: remote PC running llama-server

If a friend, household member, or your own beefier machine has
the reference rig and is reachable over LAN / VPN / Tailscale,
they can run `pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch`
and bind to the LAN interface (with `PalLLM:Auth:ApiKey` set --
Pass 354's startup guard enforces this). Point your PalLLM at
their llama-server:

```powershell
pwsh ./scripts/connect-llamacpp.ps1 \
    -LlamaCppUrl http://<remote-ip>:8080 \
    -Model Qwen3.6-35B-A3B-UD-Q8_K_XL \
    -WriteConfig
```

The connector already accepts a non-loopback URL; no new
script needed. The remote PC's `install-llama-cpp.ps1
-AutoLaunch` flow writes its appsettings; your client's
`connect-llamacpp.ps1 -WriteConfig` writes the URL to point at
that remote. Two separate machines, two separate
`appsettings.json` files.

### What still works without an LLM

Even with no cloud account and no remote PC, PalLLM's
deterministic-fallback director gives the companion something
to say on every chat turn. See
[`README.md`](../README.md) § "Is this ready to use?". The
companion won't be as smart, but they won't be mute.
