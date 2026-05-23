# Claude Code — PalLLM Quick Reference

This file is what Claude Code reads when it loads the repo. It's a
subset of [`AGENTS.md`](AGENTS.md) with Claude-specific shortcuts.

## Project

- **Name:** PalLLM
- **Kind:** local-first LLM companion runtime (Palworld + UE4SS)
- **Runtime:** .NET 10 LTS, ASP.NET Core minimal APIs, Lua 5.4 via UE4SS
- **Test count:** `1309` passing (see `README.md` badge for live)
- **Docs root:** `docs/INDEX.md`

## Working loop

```bash
# 1. Build + test (fast path)
dotnet test D:\Coding\PalLLM\PalLLM.sln --configuration Release --nologo

# 2. Before handing back: full drift audit
powershell -NoProfile -ExecutionPolicy Bypass \
  -File D:\Coding\PalLLM\scripts\run_full_audit.ps1 \
  -SkipCoverage -SkipSbom -SkipPackaging

# 3. If HTTP contract changed: regenerate OpenAPI snapshot
powershell -NoProfile -ExecutionPolicy Bypass \
  -File D:\Coding\PalLLM\scripts\export-openapi.ps1
```

## File-path conventions

Claude Code runs under Git Bash on Windows. Use `D:/Coding/PalLLM/...`
or `D:\\Coding\\PalLLM\\...` for tool paths. Relative paths work from
the repo root.

## Drift gates

16 gates in `scripts/run_full_audit.ps1`. If you change code, one of
these will usually fire until you update the docs:

- `Drift_Api_route_count` — `README.md` + `docs/ROADMAP.md` + `docs/ARCHITECTURE.md` + `docs/API.md` must agree with the `api.Map*` call count in `src/PalLLM.Sidecar/Program.cs`
- `Drift_Feature_catalog_count` — same, for `Id = "..."` entries in `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`
- `Drift_Test_count_docs` — for `[Test]` attributes in `tests/PalLLM.Tests/*.cs`
- `Drift_OpenApi_snapshot` — for the committed `docs/openapi/palllm-sidecar-v1.json`
- `Drift_Doc_freshness` — 45-day cap on `Last audited: \`YYYY-MM-DD\`` stamps
- Full list in `AGENTS.md` § "Working loop"

## Handoff

- Always read [`docs/HANDOFF.md`](docs/HANDOFF.md) before starting a task
- Always update [`docs/HANDOFF.md`](docs/HANDOFF.md) "What just landed" when finishing
- Pin the current audited state in [`docs/HANDOFF.md`](docs/HANDOFF.md) "Current audited state"

## Anti-patterns specific to this repo

- **Never delegate understanding via sub-agents when a Grep will do.** The
  drift gates + file header conventions make this repo easy to navigate
  directly.
- **Never mass-reformat.** This repo's ~4,729-line `PalLlmRuntime.cs`
  looks intimidating but every method is documented inline. Touch only
  what you're changing.
- **Never skip the drift audit.** It's 5 seconds and catches 90% of
  doc-code mismatches before they ship.

See [`AGENTS.md`](AGENTS.md) for the full briefing.
