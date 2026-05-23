# Minimum requirements

Last audited: `2026-05-23`

PalLLM v1.0 ships configured for a specific **reference rig**. Hardware
above the reference rig works better (more headroom, larger models);
hardware below the reference rig uses one of the two
[escape paths](#what-if-my-hardware-is-smaller). All in-game features
(chat, vision, TTS, ASR, embeddings, reranker, deterministic fallback,
guarded bridge actions, MCP server) run on the reference rig - the
shipping defaults are tuned for it.

## The reference rig

| Component | Minimum                                       | Notes                                                                                              |
|-----------|-----------------------------------------------|----------------------------------------------------------------------------------------------------|
| **GPU**   | NVIDIA RTX 3060 12 GB (Ampere) or newer       | Single-GPU. 8 GB variants (3060 Ti, 3070, 4060) work but trigger the small-tier model only.       |
| **RAM**   | 16 GB DDR4 / DDR5                             | Leaves ~6-8 GB for OS + Palworld + sidecar after the small-tier model resides.                     |
| **CPU**   | 6-core x86 (Ryzen 5 5600 / Core i5-11400 era) | Lower core counts work for chat but stall prompt processing; AVX2 required.                        |
| **OS**    | Windows 10 / 11 x64                           | Linux x64 supported for the bundled engine; the in-game UE4SS bridge is Windows-only.              |
| **Disk**  | ~15 GB free                                   | 5 GB for the shipping chat model + ~2 GB combined for the auxiliary models + ~8 GB headroom.       |

## What runs on the reference rig

PalLLM's tier orchestrator boots the small tier so the companion is
responsive within seconds of `pal play`, then auto-graduates to a
quality tier the moment a larger model is reachable (local or remote).
On the reference rig the small tier IS the shipping default - the
companion stays responsive forever without a second model load.

| Subsystem    | Default on the reference rig                  | VRAM footprint        | RAM footprint  |
|--------------|-----------------------------------------------|-----------------------|----------------|
| Chat (small tier) | `gemma-4-E4B-it-UD-Q4_K_XL.gguf` (5.1 GB)   | ~5-6 GB (full GPU)   | ~0.5 GB        |
| Vision       | Same Gemma-4-E4B model (multimodal)           | (shared with chat)    | (shared)       |
| TTS          | Piper or Coqui (CPU)                          | 0 GB                  | ~0.1 GB        |
| ASR          | whisper.cpp tiny / base (CPU)                 | 0 GB                  | ~0.15 GB       |
| Embeddings   | Nomic-embed-text-1.5 or all-MiniLM-L6 (FP16)  | ~0.14 GB              | ~0.05 GB       |
| Reranker     | bge-reranker-v2-m3 (Q8)                       | ~0.3 GB               | ~0.05 GB       |
| KV cache     | Q8-quantised, 8K context                      | ~1 GB                 | -              |
| **Total**    | -                                             | **~7 GB / 12 GB used**| **~1 GB / 16 GB used** |

Plenty of headroom for Palworld + Windows + browser + voice chat.

## What runs on hardware above the reference rig

| GPU class           | VRAM     | Auto-recommended chat model                                | RAM target | Notes                                                              |
|---------------------|----------|------------------------------------------------------------|------------|--------------------------------------------------------------------|
| RTX 3060 12 GB      | 12 GB    | `gemma-4-E4B-it-UD-Q4_K_XL` (5.1 GB)                       | 16 GB+     | Reference rig. Comfortable headroom.                               |
| RTX 4070 / 4070S    | 12 GB    | `gemma-4-E4B-it-UD-Q4_K_XL` (5.1 GB)                       | 16 GB+     | Same as ref; Ada gen ~25% faster prompt-processing.                |
| RTX 4070 Ti S / 4080| 16 GB    | `gemma-4-E4B-it-UD-Q4_K_XL`                                | 16 GB+     | Larger KV cache budget (32K context comfortable).                  |
| RTX 5070 Ti / 5080  | 16 GB    | `gemma-4-E4B-it-UD-Q4_K_XL`                                | 16 GB+     | Blackwell FP4/FP8 paths via opt-in `-Backend cuda13`.              |
| RTX 3090 / 4090     | 24 GB    | `Qwen3.6-35B-A3B-UD-Q8_K_XL` MoE with `--n-cpu-moe ~25`    | 32 GB+     | The quality-tier model. Auto-graduates from small once available. |
| RTX 5090 / PRO 6000 | 32-48 GB | `Qwen3.6-35B-A3B-UD-Q8_K_XL` MoE without offload           | 32 GB+     | Full GPU residency; no expert tensors in RAM.                      |

The bundled installer (`pwsh ./scripts/install-llama-cpp.ps1`)
auto-detects VRAM and system RAM and picks the largest model that
fits each budget. `Get-RecommendedModel` skips entries whose
`MoeMinSystemRamGb` exceeds the detected RAM, so a 3060/16GB system
never gets steered toward the 35B MoE that would thrash its memory.

## What's NOT in scope for v1.0

These have code paths but are NOT covered by shipping defaults and
NOT validated against the reference rig:

- **CPU-only inference.** Bundled engine supports it (`-Backend cpu`),
  but no shipping model recommendation routes through it.
- **Apple Silicon / macOS.** Bundled engine downloads the Metal asset
  but the UE4SS bridge is Windows-only.
- **AMD / Intel GPUs (Vulkan / HIP / SYCL).** Available via explicit
  `-Backend vulkan|hip|sycl`, not in shipping defaults.
- **Multi-GPU.** `--tensor-split` and `--split-mode graph` exist in
  the connect script but aren't validated for v1.0.
- **Heavyweight models.** `MiniMax-M2.7` (80-108 GB),
  `Qwen3-Coder-Next` (73 GB), `DeepSeekV4-Flash-158B` (99.9 GB) sit
  in the operator's curated library and remain launchable via
  `llama-server -m <path>` directly, but the recommendation engine
  doesn't propose them on the reference rig.

See [`POST_RELEASE_ANNEX.md`](POST_RELEASE_ANNEX.md) for the deferred
surfaces and the contract for when they might return.

## Why this specific rig?

- **RTX 3060 12 GB.** Cheapest current-gen NVIDIA card with enough
  VRAM to comfortably hold a 5 GB Q4 chat model plus the auxiliary
  vision/embedding/reranker models without thrashing.
- **16 GB system RAM.** The baseline for any modern gaming build.
  Palworld itself recommends 16 GB; PalLLM stays within that
  envelope.
- **6-core CPU.** Same baseline as Palworld's recommended spec. Lower
  core counts work but the bridge poll loop competes with the game
  for cores.

This rig matches a typical "I can play modern games at 1080p ultra"
configuration. PalLLM v1.0 explicitly **does not** require a
$1500 GPU.

## What if my hardware is smaller?

PalLLM v1.0 does NOT run a local LLM on hardware below the reference
rig (less than 12 GB VRAM OR less than 16 GB RAM). The bundled engine
install detects off-target hardware and **skips the local model
recommendation entirely** (Pass 357), pointing at two shipping escape
paths instead:

### Escape path #1: cloud API

PalLLM speaks OpenAI-compatible `/v1/chat/completions`, so any cloud
provider that exposes that endpoint is one config-write away. Use
`scripts/connect-cloud.ps1`:

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
`openrouter`, `deepseek`, `mistral`, `custom`. The script optionally
probes `/v1/models` (`-Probe`) before writing config so a typo'd key
fails fast.

**Security:** the API key writes to `appsettings.json`. Prefer setting
`$env:PalLLM__Inference__ApiKey` from a secrets manager when possible;
both routes work but env vars don't risk committing the key to
version control.

### Escape path #2: remote PC running llama-server

If a friend, household member, or your own beefier machine has the
reference rig (or better) and is reachable over LAN / VPN / Tailscale,
they can run `pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch` and
bind to the LAN interface (with `PalLLM:Auth:ApiKey` set — Pass 354's
startup guard enforces this). Point your PalLLM at their llama-server:

```powershell
pwsh ./scripts/connect-llamacpp.ps1 \
    -LlamaCppUrl http://<remote-ip>:8080 \
    -Model gemma-4-E4B-it-UD-Q4_K_XL \
    -WriteConfig
```

The connector accepts any non-loopback URL; no new script needed. The
remote PC's `install-llama-cpp.ps1 -AutoLaunch` flow writes its own
appsettings; your client's `connect-llamacpp.ps1 -WriteConfig` writes
the URL to point at that remote. Two separate machines, two separate
`appsettings.json` files.

### What still works without an LLM

Even with no cloud account and no remote PC, PalLLM's
deterministic-fallback director gives the companion something to say
on every chat turn. See [`README.md`](../README.md) § "Is this ready
to use?". The companion won't be as smart, but they won't be mute.

## Migration from the previous reference rig

Earlier PalLLM passes targeted an RTX 3090 / 32 GB / 5800X3D rig as
the v1.0 minimum. Pass 373 lowered the minimum to RTX 3060 / 16 GB /
6-core at full feature parity by:

1. Shifting the default chat model from the 39 GB
   `Qwen3.6-35B-A3B-UD-Q8_K_XL` MoE to the 5.1 GB
   `gemma-4-E4B-it-UD-Q4_K_XL` dense model. The MoE model is still
   the auto-pick on hosts with 24+ GB VRAM and 32+ GB RAM.
2. Adding a `MoeMinSystemRamGb` field to the catalog so MoE entries
   that would need partial-offload into more RAM than the host has
   get skipped during recommendation.
3. Keeping every feature subsystem on its existing model (Piper,
   whisper.cpp, Nomic embed, bge-reranker) — all of which were
   already small enough for the lower spec.

No behavioural change for operators on 3090+ rigs — they keep
auto-graduating to the larger MoE model. Operators on
3060-3070/3080 rigs now get a working install instead of a
"below reference rig - skip local" message.
