# Tuning guide

Last audited: `2026-06-05`

Tune PalLLM from the active runtime outward:

1. Keep the game bridge deterministic.
2. Keep the default local engine as llama.cpp.
3. Keep the model set narrow: `Qwen3.5-9B-UD-Q6_K_XL` plus
   `gemma-4-12b-it-UD-Q6_K_XL`.
4. Promote a setting only after route replay proves it.

## Inference defaults

The shipping sidecar reads `src/PalLLM.Sidecar/appsettings.json`.

| Setting | Current default | Why it matters |
| --- | --- | --- |
| `Enabled` | `true` | Enables the OpenAI-compatible chat-completions client. |
| `BaseUrl` | `http://localhost:8080/v1/` | Points at local llama.cpp by default. |
| `Model` | `Qwen3.5-9B-UD-Q6_K_XL` | Static fallback when no routed tier is available. |
| `Temperature` | `0.7` | Qwen3.5 fast-lane profile. |
| `TopP` | `0.8` | Qwen3.5 fast-lane profile. |
| `TopK` | `20` | Qwen3.5 fast-lane profile. |
| `MinP` | `0.0` | Qwen3.5 fast-lane profile. |
| `PresencePenalty` | `1.5` | Reduces repeated assistant phrasing in routine text turns. |
| `EnableThinking` | `false` | Suppresses reasoning blocks in the player-facing fast lane. |

## Model tiers

`ModelTiers` keeps routing explicit:

| Tier | Model | Role |
| --- | --- | --- |
| `worker` | `Qwen3.5-9B-UD-Q6_K_XL` | Fast text, tool drafts, routine build assistance |
| `edge` | `gemma-4-12b-it-UD-Q6_K_XL` | Smart reasoning, multimodal proof, final arbitration |

The orchestrator probes `/v1/models` and chooses the highest-priority available
tier. Do not add hidden fallback models. If a model is not exposed by
`/v1/models`, PalLLM must fall back cleanly instead of inventing a route.

## Vision

Vision routes are proof-first. The Gemma 4 lane may be wired with a matching
mmproj only after Palworld screenshot replay passes on the target host.

| Setting | Guidance |
| --- | --- |
| `Vision:Enabled` | Keep false until screenshot replay is captured. |
| `Vision:Model` | Use `gemma-4-12b-it-UD-Q6_K_XL` when the local server exposes it. |
| `Vision:MaxImageBytes` | Keep bounded; screenshots are evidence, not unbounded media upload. |

## Sampler changes

Change samplers through `scripts/connect-llamacpp.ps1 -ModelProfile`, not by
hand-editing unrelated config:

```powershell
pwsh ./scripts/connect-llamacpp.ps1 -ModelProfile qwen35 -WriteConfig
pwsh ./scripts/connect-llamacpp.ps1 -ModelProfile gemma -WriteConfig
```

Every sampler change needs replay evidence for companion chat, strict JSON,
tool-call decisions, base-building advice, and save parsing.

## Latency knobs

Use the advanced llama.cpp knobs only when measuring a specific problem:

- `ContextSize`: raise only with KV-cache headroom.
- `Parallel`: keep `1` for the single-player lane unless load testing proves
  benefit.
- `CacheReuse`: useful for repeated prompts; verify parse stability.
- `FlashAttn`: leave `auto` unless backend proof says otherwise.
- `SpecType`: off by default; proof lane only.
- `TensorSplit` / `SplitMode`: multi-GPU proof lane only.

## Promotion receipt

A tuning change is complete only when the pass records:

- setting changed
- route replay used
- before/after p95 latency or parse-success evidence
- failure mode and rollback
- updated README or operator doc when behavior changes
