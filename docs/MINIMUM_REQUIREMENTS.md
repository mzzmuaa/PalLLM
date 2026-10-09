# Minimum requirements

Last audited: `2026-06-05`

This page defines the hardware floor for local PalLLM inference. The README is
the authority for the whole project; this page exists so operators can inspect
the local llama.cpp target without scanning every tuning document.

## v1.0 reference rig

| Component | Minimum target |
| --- | --- |
| GPU | NVIDIA RTX 3060 12 GB or better |
| RAM | 16 GB DDR4 / DDR5 |
| CPU | 6-core x64 desktop CPU |
| OS | Windows 10 or Windows 11 |
| Runtime | Bundled `llama.cpp` `llama-server` only |

Pass 448 current-lane callout: the supported local model set is
`Qwen3.5-9B-UD-Q6_K_XL` for the fast Worker lane plus
`gemma-4-12b-it-UD-Q6_K_XL` for the smart/multimodal Edge lane. The reference
rig is expected to run the fast lane locally and use the smart lane when VRAM
headroom, sequential loading, or a remote llama.cpp host is proven.

## What the installer does

`scripts/install-llama-cpp.ps1` probes GPU vendor, VRAM, RAM, CUDA toolkit, and
platform. It then chooses the llama.cpp backend and the safest model from the
supported catalog. On below-reference hardware, the default path is to skip
local install and show the two escape paths below.

The local installer is intentionally conservative:

- NVIDIA defaults to CUDA 12.4.
- CUDA 13.0-13.2 is treated as a warning band.
- CPU-only live inference is not a shipping target.
- Extra local models are ignored unless a future pass adds them to the active
  catalog with tests and receipts.

## Escape path #1: cloud API

Use any OpenAI-compatible hosted chat-completions endpoint when the local
machine is below the reference rig:

```powershell
pwsh ./scripts/connect-cloud.ps1 -Provider custom -BaseUrl https://example.invalid/v1/ -Model your-model
```

The contract is OpenAI-compatible chat completions. PalLLM still uses the same
advisor/builder/validator flow; only the endpoint changes.

## Escape path #2: remote PC

Run llama.cpp on a stronger LAN/VPN machine, then point PalLLM at it:

```powershell
pwsh ./scripts/connect-llamacpp.ps1 `
  -LlamaCppUrl http://192.168.1.50:8080 `
  -Model Qwen3.5-9B-UD-Q6_K_XL `
  -ModelProfile qwen35 `
  -WriteConfig
```

Remote local inference is still llama.cpp-only. Do not add a second local
engine path.

## Promotion checklist

Before changing the minimum target or default local lane, collect:

- exact model and mmproj artifact names
- llama.cpp build tag and backend
- `/v1/models` identity receipt
- PalLLM route replay for companion chat, automation, screenshots, and save
  parsing
- rollback command
- updated README, `docs/LLAMA_CPP_BUNDLED.md`, and tests
