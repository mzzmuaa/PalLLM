# PalLLM cheat sheet - one page

Last audited: `2026-05-23`

The TL;DR-of-TL;DRs. Everything you need to operate the repo
fits on one screen. For the full doc tour, see
[`INDEX.md`](INDEX.md).

## Commands (verb-driven, via `pal.ps1`)

```powershell
pwsh ./pal.ps1 next           # "what should I do right now?" - context-aware single-action advisor
pwsh ./pal.ps1 onboard        # first-time setup -- SDK + build + test + audit + dashboard
pwsh ./pal.ps1 build          # dotnet build (Release)
pwsh ./pal.ps1 test           # dotnet test  (Release, quiet) -- expects 1313 / 1313
pwsh ./pal.ps1 audit          # full drift audit -- build + tests + 16 gates (~30 s)
pwsh ./pal.ps1 fast-audit     # drift gates only -- skip coverage / SBOM / packaging
pwsh ./pal.ps1 cleanup        # preview generated clutter; add -Apply to delete
pwsh ./pal.ps1 run            # dotnet run the sidecar (foreground)
pwsh ./pal.ps1 play           # boot sidecar + open Field Console dashboard
pwsh ./pal.ps1 doctor         # environment + smoke + delivery-replay diagnostics
pwsh ./pal.ps1 smoke          # smoke test against a running sidecar
pwsh ./pal.ps1 workflow-pins  # verify GitHub Actions use full-SHA action pins
pwsh ./pal.ps1 publish-audit  # local publication preflight (copy / paths / notices)
pwsh ./pal.ps1 aot-readiness  # local AOT/trim readiness scan (-PublishProbe opt-in)
pwsh ./pal.ps1 health         # write a local Markdown + JSON health snapshot
pwsh ./pal.ps1 proof          # read-only native proof status + next action
pwsh ./pal.ps1 models serving # live model-server checklist from /api/inference/collaboration
pwsh ./pal.ps1 models probe   # no-prompt /v1/models + /metrics evidence artifact
pwsh ./pal.ps1 openapi        # regenerate the OpenAPI snapshot
pwsh ./pal.ps1 package        # build the release zip
pwsh ./pal.ps1 recover        # last-resort recovery (archive runtime root + clean start)
pwsh ./pal.ps1 list           # show this table
```

## Bundled local inference (one command)

```powershell
pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch
```

Detects GPU vendor / VRAM / RAM / CUDA cross-platform, picks the
right backend (cuda12 / cuda13 / vulkan / hip / sycl / cpu),
downloads + smoke-tests the latest upstream release, recommends
the best curated GGUF that fits VRAM (with MoE partial offload
when needed), wires PalLLM `appsettings.json` with the
per-family sampler (Qwen3.6 / Qwen3-Coder / MiniMax / Gemma /
DeepSeek), and launches `llama-server`. Deep-dive:
[`LLAMA_CPP_BUNDLED.md`](LLAMA_CPP_BUNDLED.md).

## Key files

| File | Purpose |
|---|---|
| `PalLLM.sln` | Solution root; build entry point |
| `pal.ps1` | Verb-driven task runner (this cheat sheet's commands) |
| `Directory.Build.props` | Repo-wide MSBuild settings (NoWarn, etc.) |
| `src/PalLLM.Domain/Configuration/PalLlmOptions.cs` | Every config knob with default + XML doc |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` | The ~4744-line monolith - every chat turn lives here |
| `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` | Registers every feature for `/api/features` and the dashboard |
| `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs` | The 19 deterministic strategies + emergency tier |
| `src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs` | Visual + audio cue planner (paired with every fallback strategy) |
| `src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` | The harvest seam - 5 interfaces (DO NOT RENAME) |
| `src/PalLLM.Sidecar/Program.cs` | Every HTTP route registration (57 `/api/*` routes) |
| `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs` | Every MCP tool (38 tools) |
| `mod/ue4ss/Mods/PalLLM/Scripts/main.lua` | The Lua bridge (event producer + outbox consumer) |
| `tests/PalLLM.Tests/SidecarTestFixture.cs` | Canonical fixture wiring (read this once -> know every HTTP test) |
| `scripts/run_full_audit.ps1` | The 16 drift gates |
| `docs/INDEX.md` | Doc map (read first if lost) |
| `docs/HANDOFF.md` | Current state + "what just landed" |
| `AGENTS.md` | Coding-agent briefing (read before any code change) |
| `CLAUDE.md` | Claude-Code-specific shortcut |

## Drift gates (run via `pal.ps1 audit`)

| # | Gate | What it checks |
|---|---|---|
| 1 | `Build_Release` | `dotnet build` succeeds with zero warnings |
| 2 | `Tests` | `dotnet test` all-green (1313 / 1313 currently) |
| 3 | `Drift_Mojibake` | No UTF-8 corruption in tracked files |
| 4 | `Drift_Api_route_count` | `api.Map*` calls in `Program.cs` agree with README / ROADMAP / ARCHITECTURE / API counts |
| 5 | `Drift_Api_reference_surface` | `API.md` route list matches `Program.cs` registrations |
| 6 | `Drift_OpenApi_snapshot` | Committed `docs/openapi/...` matches live route surface |
| 7 | `Drift_Feature_catalog_count` | `Id = "..."` entries match counts in docs |
| 8 | `Drift_Feature_status_split` | ready / scaffolded / deferred counts match docs |
| 9 | `Drift_Fallback_strategy_count` | `Try_*` methods match ROADMAP count |
| 10 | `Drift_Test_count_docs` | `[Test]` attribute count matches docs |
| 11 | `Drift_Public_copy` | Release-facing files avoid third-party brand pinning |
| 12 | `Drift_Path_references` | Every repo-relative path in a doc actually exists |
| 13 | `Drift_Agents_manifest` | `agents.json` conforms to required keys + types contract (Pass 110) |
| 14 | `Drift_Doc_freshness` | `Last audited:` stamps within 45 days |
| 15 | `Drift_Hot_file_line_count` | Hot-file line-count mirrors stay within tolerance |
| 16 | `Drift_Dangling_markdown_links` | Every `[text](path)` resolves |

## Hard rules

1. **Deterministic-first** - every chat turn produces a working
   reply even if inference is off / down / rate-limited. ADR
   0001.
2. **Observer-only** - automation pipelines observe; explicit
   operator opt-in is required to *act*. ADRs 0006, 0003.
3. **Every automated change gets a proof packet** - every
   suggestion / promotion / apply records a `ProofPacket` with
   SHA-256 id.

## Patterns (CONVENTIONS.md)

- **Advisor** - pure function, returns a posture record
- **Builder** - composes a snapshot from multiple sources
- **Validator** - checks a payload, returns structured result
- **Feeder** - observes runtime, writes elsewhere

## Layout

```
D:\Coding\PalLLM\
+-- pal.ps1                              -> task runner
+-- PalLLM.sln                           -> solution
+-- Directory.Build.props                -> shared MSBuild
+-- AGENTS.md / CLAUDE.md / llms.txt     -> agent briefings
+-- README.md / CONTRIBUTING.md          -> human briefings
+-- SECURITY.md / SECURITY.txt           -> security policy + RFC 9116
+-- src/
|   +-- PalLLM.Domain/                   -> portable runtime (NO ASP.NET, NO UE4SS)
|   +-- PalLLM.Sidecar/                  -> ASP.NET Core host
+-- tests/PalLLM.Tests/                  -> NUnit, 1313 tests
+-- mod/ue4ss/Mods/PalLLM/               -> Lua bridge
+-- scripts/                             -> install / doctor / smoke / audit / package
+-- docs/
    +-- adr/                             -> Architecture Decision Records (6)
    +-- schemas/                         -> JSON Schemas for off-HTTP shapes
    +-- INDEX.md                         -> doc map
    +-- HANDOFF.md                       -> current state
    +-- ARCHITECTURE.md                  -> system at a glance + Mermaid diagram
    +-- COOKBOOK.md                      -> recipes for common changes
    +-- EXTENSION_POINTS.md              -> "where do I add X?" map
    +-- DATAFLOW.md                      -> sequence diagrams
    +-- STATE_MACHINES.md                -> stateDiagrams
    +-- HOT_PATH.md                      -> latency budgets
    +-- OBSERVABILITY.md                 -> OpenTelemetry primer
    +-- RUNBOOK.md                       -> incident response
    +-- ANTI_PATTERNS.md                 -> what NOT to do
    +-- (plus 20+ other Diataxis-organised docs)
```

## "I want to add X" - quick map

| Add | Recipe | Edit |
|---|---|---|
| HTTP endpoint | [COOKBOOK Section 1](COOKBOOK.md#1-new-http-endpoint) | `Program.cs` + `Contracts.cs` + builder + tests + docs |
| Fallback strategy | [COOKBOOK Section 2](COOKBOOK.md#2-new-fallback-strategy) | `FallbackBehaviorEngine.cs` + `PresentationCuePlanner.cs` |
| MCP tool | [COOKBOOK Section 3](COOKBOOK.md#3-new-mcp-tool) | `Mcp/PalLlmMcpTools.cs` |
| Config flag | [COOKBOOK Section 4](COOKBOOK.md#4-new-config-flag) | `PalLlmOptions.cs` |
| Advisor / builder | [COOKBOOK Section 5](COOKBOOK.md#5-new-advisor-or-builder) | new file under `Domain/Runtime/` |
| Bridge event type | [COOKBOOK Section 6](COOKBOOK.md#6-new-bridge-event-type) | `PalLlmRuntime.cs` `ProcessBridgeEvent` + `main.lua` |
| Guarded action | [COOKBOOK Section 7](COOKBOOK.md#7-new-guarded-action-type) | `ActionIntentPlanner.cs` + `main.lua` |
| Feature catalog entry | [COOKBOOK Section 8](COOKBOOK.md#8-new-feature-catalog-entry) | `PalLlmFeatureCatalog.cs` |
| ADR | [COOKBOOK Section 9](COOKBOOK.md#9-new-adr) | `docs/adr/000N-...md` |

## Health endpoints (running sidecar at localhost:5088)

```powershell
curl -s http://localhost:5088/api/health      # full RuntimeHealth + opt-in flags
curl -s http://localhost:5088/api/describe    # one-shot self-description manifest
curl -s http://localhost:5088/api/quickstart  # state-aware "what should I do next?"
curl -s http://localhost:5088/api/features    # feature catalog (121 entries)
curl -s http://localhost:5088/api/release/readiness   # machine-readable release posture
curl -s http://localhost:5088/api/bridge/proof        # native-readiness + loop-proof snapshot
```

Dashboard: http://localhost:5088/

## When something's wrong

[`RUNBOOK.md`](RUNBOOK.md) names the symptom -> diagnose -> fix
fix. Top of mind:

- Sidecar won't start -> port 5088 conflict (`Get-NetTCPConnection -LocalPort 5088`)
- Chat returns deterministic only -> check `ResponsePath` for the reason
- Outbox stuck -> check `Bridge/Failed/` for write errors
- Drift audit fails in CI -> run locally with `pal.ps1 audit`
- Memory corrupt -> quarantine `session.json` and let the sidecar regenerate

## When ending a session

- Update [`HANDOFF.md`](HANDOFF.md) "What just landed"
- Bump counts everywhere if the audit fires
- Run [`pal.ps1 audit`](../pal.ps1) before handoff

## Related

- [`INDEX.md`](INDEX.md) - full doc map
- [`AGENTS.md`](../AGENTS.md) - full agent briefing
- [`HANDOFF.md`](HANDOFF.md) - current rolling state
