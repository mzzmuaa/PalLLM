# Local models inventory

Last audited: `2026-06-05`

This is the canonical local model inventory for PalLLM. It is intentionally
small: PalLLM supports a two-lane llama.cpp mesh, not a general local-model
launcher.

## Supported GGUF artifacts

| Lane | Relative path under `D:\Models` | Purpose |
| --- | --- | --- |
| Fast Worker | `Qwen\Qwen3.5-9B-UD-Q6_K_XL.gguf` | Fast text turns, tool drafts, routine companion replies |
| Smart multimodal Edge | `Gemma\gemma-4-12b-it-UD-Q6_K_XL.gguf` | Slower reasoning, screenshot/vision proof, final arbitration |

The exact model ids shipped in `src/PalLLM.Sidecar/appsettings.json` are:

- `Qwen3.5-9B-UD-Q6_K_XL`
- `gemma-4-12b-it-UD-Q6_K_XL`

Do not add another local model family without updating:

- `README.md`
- `src/PalLLM.Sidecar/appsettings.json`
- `scripts/install-llama-cpp.ps1`
- `scripts/connect-llamacpp.ps1`
- `docs/LLAMA_CPP_BUNDLED.md`
- `docs/MINIMUM_REQUIREMENTS.md`
- `tests/PalLLM.Tests/LlamaCppBundlingTests.cs`

## Multimodal projector policy

Projector files are proof artifacts, not automatic defaults. Keep them next to
the model inventory and promote them only after Palworld screenshot replay:

| Lane | Expected projector location | Promotion state |
| --- | --- | --- |
| Qwen3.5 fast Worker | `mmproj\Qwen3.5-9B-mmproj-BF16.gguf` | Optional proof lane |
| Gemma 4 smart Edge | `mmproj\gemma-4-12b-it-mmproj-BF16.gguf` | Optional proof lane; prefer Vulkan if CUDA mmproj proof fails |

## Out-of-scope local files

`D:\Models` may contain unrelated downloads for other projects or experiments.
They are not PalLLM-supported lanes unless they appear in the supported table
above and have a matching test contract. Do not let discovery code,
documentation examples, or README copy imply broader support.

## Audit checklist

Before changing this inventory, capture:

- model source URL and license/redistribution decision
- exact GGUF filename, size, and hash
- exact mmproj filename and hash when vision is involved
- llama.cpp build tag and backend
- sampler profile
- route replay evidence
- rollback command
