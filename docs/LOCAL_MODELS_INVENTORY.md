# Local model inventory — operator-curated `D:\Models`

Last audited: `2026-05-22`

This doc maps the operator's hand-curated GGUF library under
`D:\Models` to PalLLM's role mesh. It's a **machine-specific operator
note**, not a publishable manifest — anyone else running PalLLM will
have a different library at a different path. See
[`MODELS_2026.md`](MODELS_2026.md) for the abstract recommendations
this curation reflects, and
[`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) for the role-mesh
the GGUFs slot into.

## Operator preferences

These are the operator's stated preferences for future model selection.
They override the abstract recommendations in
[`MODELS_2026.md`](MODELS_2026.md) when the two conflict.

- **Preferred Hugging Face publisher: `unsloth/`.** The curated library
  is entirely unsloth UD-* (ultra-detail dynamic) quants. When a new
  model lands and unsloth ships a UD variant, that variant wins over
  vanilla K-quants from the original publisher. UD quants ship with
  calibration-aware mixed-precision tensors and tend to preserve quality
  at lower bit budgets than standard K-quants — matches the operator's
  apparent quality-per-GB priority.
- **Preferred quantization band: `UD-Q4_K_XL` to `UD-Q8_K_XL`** for
  routine lanes, **`UD-IQ3_XXS` / `UD-IQ4_XS`** acceptable for frontier
  proof lanes where bit budget dominates over quality.
- **MTP-capable variants preferred** whenever both an MTP and a non-MTP
  variant exist. The operator's curation already biases toward the
  Qwen 3.6-A3B line which ships model-native MTP heads.
- **Bonsai family: deprioritised.** Operator's stated view is that the
  Bonsai small-LLM line is overrated relative to its hype. Future model
  recommendations should not promote Bonsai variants without a specific
  reason that overcomes this default. Not currently present in the
  curated library and not currently recommended by
  [`MODELS_2026.md`](MODELS_2026.md), so this is a forward-looking
  guard.

If a future pass adds a new recommended model, walk this list first.
A recommendation that names a non-unsloth publisher, a non-MTP variant,
or a Bonsai model needs a per-case justification in its proposing
pass entry; otherwise default to the operator's preferences.

## Curated library snapshot

Resolved via `Get-ChildItem D:\Models -Recurse` on `2026-05-22`:

```
D:\Models
├── DeepSeek
│   └── DeepSeekV4-Flash-158B-Q3_K_M.gguf                            (99.9 GB)
├── Gemma
│   ├── gemma-4-31B-it-UD-Q8_K_XL.gguf                               (35.0 GB)
│   └── gemma-4-E4B-it-UD-Q4_K_XL.gguf                               ( 5.1 GB)
├── MiniMax-M2.7
│   ├── UD-IQ3_XXS                                                   (~80   GB, 3 shards)
│   │   ├── MiniMax-M2.7-UD-IQ3_XXS-00001-of-00003.gguf
│   │   ├── MiniMax-M2.7-UD-IQ3_XXS-00002-of-00003.gguf
│   │   └── MiniMax-M2.7-UD-IQ3_XXS-00003-of-00003.gguf
│   └── UD-IQ4_XS                                                    (~108  GB, 4 shards)
│       ├── MiniMax-M2.7-UD-IQ4_XS-00001-of-00004.gguf
│       ├── MiniMax-M2.7-UD-IQ4_XS-00002-of-00004.gguf
│       ├── MiniMax-M2.7-UD-IQ4_XS-00003-of-00004.gguf
│       └── MiniMax-M2.7-UD-IQ4_XS-00004-of-00004.gguf
├── Qwen
│   ├── BF16                                                         (empty placeholder)
│   ├── Qwen3-Coder-Next
│   │   └── UD-Q6_K_XL                                               (~73   GB, 3 shards)
│   │       ├── Qwen3-Coder-Next-UD-Q6_K_XL-00001-of-00003.gguf
│   │       ├── Qwen3-Coder-Next-UD-Q6_K_XL-00002-of-00003.gguf
│   │       └── Qwen3-Coder-Next-UD-Q6_K_XL-00003-of-00003.gguf
│   ├── Qwen3.6-27B-UD-Q8_K_XL.gguf                                  (35.8 GB)
│   └── Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf                              (39.1 GB)
└── mmproj
    ├── mmproj-F16.gguf                                              (928 MB)
    └── mmproj-gemma-4-31B-F16.gguf                                  ( 1.2 GB)
```

All entries are unsloth UD-* (ultra-detail dynamic) quants — the
operator's stated preference. UD quants ship with calibration-aware
mixed-precision tensors and tend to preserve quality at lower bit
budgets than vanilla K-quants.

## Role-mesh mapping

How the curated library maps onto PalLLM's role-mesh (see
[`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) "What each lane
should own"). Sorted by hot-path proximity — top entries are the
ones that actually get hit on every chat turn.

| PalLLM lane | Recommended GGUF | Why this one | Cold load (24 GB GPU) |
|---|---|---|---|
| **Fast-start / edge chat** | `Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf` | 5 GB, dense, Gemma 4 E4B is the "4B-effective active params" tune; loads in seconds; usable while heavier tiers are still warming | ~3-4 s |
| **Quality chat (default)** | `Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf` | 39 GB MoE with 3 B active params; the [`MODELS_2026.md`](MODELS_2026.md) reference default; UD-Q8_K_XL is a quality step up over the previously-recommended UD-Q4_K_XL | ~30-40 s |
| **Quality chat (dense alt)** | `Qwen\Qwen3.6-27B-UD-Q8_K_XL.gguf` | 36 GB pure dense; no MoE routing overhead, sometimes preferable for short prompts where routing dominates | ~30-40 s |
| **Quality chat (Gemma alt)** | `Gemma\gemma-4-31B-it-UD-Q8_K_XL.gguf` | 35 GB Gemma 4 dense; different family for ensemble disagreement detection | ~30-40 s |
| **Frontier sparse (proof)** | `MiniMax-M2.7\UD-IQ4_XS\*.gguf` (4 shards) | 108 GB highest-quality MoE shards; for batched promotion-replay or quality-ceiling tests, not chat hot path | ~3-5 min |
| **Frontier sparse (compact)** | `MiniMax-M2.7\UD-IQ3_XXS\*.gguf` (3 shards) | 80 GB IQ3 variant; same architecture as above at a lower bit budget | ~2-3 min |
| **Frontier dense (proof)** | `DeepSeek\DeepSeekV4-Flash-158B-Q3_K_M.gguf` | 100 GB DeepSeek V4 Flash dense Q3; off-hot-path proof lane for raw capability comparisons | ~3-5 min |
| **Coding / structured-output** | `Qwen\Qwen3-Coder-Next\UD-Q6_K_XL\*.gguf` (3 shards) | 73 GB Qwen3-Coder-Next sparse MoE; the right pick for proof-packet builder runs and structured-output promotion experiments | ~1-2 min |
| **Vision projector (default)** | `mmproj\mmproj-F16.gguf` | 928 MB F16 projector; pairs with the Qwen 3.6-35B-A3B chat model for screenshot description | n/a |
| **Vision projector (Gemma)** | `mmproj\mmproj-gemma-4-31B-F16.gguf` | 1.2 GB F16 projector; pairs with `gemma-4-31B-it` when the operator wants Gemma vision instead of Qwen | n/a |

The `Qwen\BF16` directory is empty — placeholder for a future BF16
master from which lower-precision quants can be re-derived if a
benchmark requires the bit-identical reference.

## PalLLM wire-up

The operator-curated library is exposed to PalLLM through two
mechanisms:

### 1. `PalLLM:ExternalModelsRoot` config

Set in `appsettings.Development.json` (or any env-specific config
override):

```json
{
  "PalLLM": {
    "ExternalModelsRoot": "D:\\Models"
  }
}
```

When this is set, `PalLlmOptions.ModelsDir` returns the operator's
path; when unset, it falls back to the legacy
`%LOCALAPPDATA%\Pal\Saved\PalLLM\Models` location. No automatic
write happens to either path — PalLLM is HTTP-only against
inference engines; it never owns GGUF files itself.

### 2. Connector scripts pass the model path explicitly

`pal connect llamacpp` (and the other engine-specific connectors) take
a `-ModelPath` argument that points directly at one of the curated
GGUFs. Example for the quality lane:

```powershell
pwsh ./pal.ps1 connect llamacpp `
  -ModelPath D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
  -Mmproj    D:\Models\mmproj\mmproj-F16.gguf `
  -ContextSize 16384 `
  -GpuLayers 99 `
  -WriteConfig
```

For sharded models (MiniMax / Qwen3-Coder-Next), pass the first shard
— llama.cpp auto-discovers the rest via the `-of-NNNNN` suffix:

```powershell
pwsh ./pal.ps1 connect llamacpp `
  -ModelPath D:\Models\MiniMax-M2.7\UD-IQ4_XS\MiniMax-M2.7-UD-IQ4_XS-00001-of-00004.gguf `
  -ContextSize 32768 `
  -GpuLayers 99 `
  -WriteConfig
```

### 3. llama.cpp is the only supported loader

Pass 339 removed Ollama from PalLLM's operator surfaces (no
`connect-ollama.ps1`, shipping `appsettings.json` points at
`:8080`, doc recommendations all default to llama.cpp). The
codebase keeps some Ollama-aware response-shape parsing for
back-compat with operators who still run Ollama out-of-band, but
none of the shipping verbs, scripts, or recommended workflows
involve Ollama anymore. **Use `pal connect llamacpp` for every
chat lane.** For high-config / Blackwell-class GPUs that want
the vLLM throughput path, use `pal connect vllm`.

## MTP (Multi-Token Prediction) status

Unsloth UD-XL quants for the Qwen3.6 family typically expose
**model-native MTP heads** for speculative decoding. To use them in
llama.cpp:

```powershell
# When the GGUF metadata includes MTP heads:
pwsh ./pal.ps1 connect llamacpp `
  -ModelPath D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
  -SpecType ngram-mod `
  -WriteConfig
```

The `-SpecType` flag emits `--spec-type` in the printed
`llama-server` command; the model's internal MTP head is used as the
drafter. No external draft model is needed for this path. Confirm
with `llama-server --version` >= the cut-off where
`--spec-type ngram-mod` was added (current llama.cpp builds support
it). For draft-MTP proof receipts, see
[`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) "Speculative
decoding proof lanes."

The DeepSeek V4-Flash Q3_K_M is **not** known to ship MTP heads in
this quant; speculation against that one would need an external
drafter (`-DraftModelPath`). Worth keeping it as a quality-ceiling
proof lane rather than a hot-path chat model on this machine.

## Diffusion subdirectory (`D:\Models\Diffusion`)

Pass 340 wired `D:\Models\Diffusion` as PalLLM's canonical diffusion-
weight location (Stable Diffusion / Flux / Hunyuan / etc., used by
the future portrait-variant + scene-narration lane described in
[`FUTURE_2035.md`](FUTURE_2035.md) idea #15). Code wiring:

- `PalLlmOptions.DiffusionModelsDir` is a computed property that
  always returns `Path.Combine(ModelsDir, "Diffusion")`. When the
  operator sets `ExternalModelsRoot`, the diffusion path follows
  automatically — one knob to relocate the entire curation.
- The portable adapter surface
  ([`IPathProvider.DiffusionModelsDir`](../src/PalLLM.Domain/Portable/PortableAdapterContracts.cs))
  exposes the same path so any harvested capability that needs
  diffusion weights resolves them the same way.
- `BridgeGameAdapter` and any other implementer of `IPathProvider`
  honor the path automatically (default interface method).

The directory exists on disk and is empty pending the operator
curating their diffusion weights. Recommended initial occupants
(forward-looking — none required today):

| Tier | Candidate | Why |
|---|---|---|
| Fast portrait variants | `flux.1-schnell` (12 B, ~12 GB FP8) | 4-step generation, real-time-ish on consumer GPUs |
| Quality portrait variants | `flux.1-dev` (12 B, ~24 GB) | Production-quality SDXL successor |
| Scene narration | `hunyuan-image-2.1` | Strong text rendering for in-scene labels |
| Edge / older GPU | `sdxl-turbo` (~7 GB) | Works on 8 GB cards; lower quality |

PalLLM doesn't ship a diffusion connector yet — the path is
groundwork. When the lane lands (FUTURE_2035 idea #15), the wire-up
will point at the diffusion server's `BaseUrl` and pass the model
identifier the same way `Inference.Model` does today.

## What's NOT here

The curated library covers the chat + vision lanes well, has the
diffusion subdirectory wired (empty), but is silent on:

- **TTS** — no Kokoro / Fish Speech / F5-TTS weights present. PalLLM
  routes TTS through an HTTP server anyway; the operator's TTS lane
  presumably loads weights from wherever that server's own model
  store is configured. See [`MODELS_2026.md` §4](MODELS_2026.md#4-tts--text-to-speech).
- **ASR** — no Whisper / Parakeet / Voxtral weights present. Same
  pattern: ASR runs in its own HTTP server.
- **Embeddings** — PalLLM's in-process FNV-1a embedder doesn't load
  external weights. A future optional `bge-m3` server would (see
  [`MEMORY_RECIPES.md`](MEMORY_RECIPES.md) recommended trajectory
  step 2 + [`FUTURE_2035.md` §5c](FUTURE_2035.md)).

These can stay in their own engine-specific paths; nothing in PalLLM
forces them to live under `D:\Models`.

## How to refresh this doc

The inventory above is a snapshot. When the operator adds or removes
a GGUF:

1. Re-run `Get-ChildItem D:\Models -Recurse -Filter *.gguf`.
2. Update the directory tree above.
3. Re-map any new file to the role-mesh table.
4. Bump the `Last audited:` stamp.

The `Drift_Doc_freshness` 45-day cap will surface this doc again
automatically if it ages.

## Related

- [`MODELS_2026.md`](MODELS_2026.md) — abstract model recommendations
  per function × hardware tier; this doc is the concrete realisation
  for one operator's machine.
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) — the role-mesh
  the curated GGUFs slot into.
- [`QUANTIZATION.md`](QUANTIZATION.md) — quant choice rationale
  (Q4_K_XL vs Q6_K_XL vs Q8_K_XL vs IQ-class).
- [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md) — recipes specific
  to Blackwell-class GPUs the operator's machine may have.
- [`scripts/connect-llamacpp.ps1`](../scripts/connect-llamacpp.ps1)
  — the connector that ingests `-ModelPath` and writes
  `appsettings.json`.
