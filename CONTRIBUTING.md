# Contributing to PalLLM

Thanks for your interest in making PalLLM better. This guide keeps the
contribution loop tight so the runtime stays testable and the in-game surface
stays reliable.

> **Coding agents** (any MCP- or CLI-capable coding assistant) — read
> [`AGENTS.md`](AGENTS.md) first. It summarises this guide plus the
> agent-specific handoff protocol, the drift-gate contract, and
> anti-patterns. [`CLAUDE.md`](CLAUDE.md) is the Claude-Code-specific
> shortcut.

## Quickstart for contributors

```powershell
dotnet build D:\Coding\PalLLM\PalLLM.sln
dotnet test  D:\Coding\PalLLM\PalLLM.sln
dotnet run   --project D:\Coding\PalLLM\src\PalLLM.Sidecar\PalLLM.Sidecar.csproj
```

The sidecar listens on `http://localhost:5088` and serves the Field Console
dashboard at `/`. See [`docs/QUICKSTART.md`](docs/QUICKSTART.md) for the
full first-chat walkthrough.

## Layout you should know

- [`src/PalLLM.Domain`](src/PalLLM.Domain) — portable runtime (memory,
  fallback director, presentation planner, session persistence, vision,
  TTS, relationships). No ASP.NET or UE4SS dependencies.
- [`src/PalLLM.Sidecar`](src/PalLLM.Sidecar) — ASP.NET Core minimal-API
  host, background workers, static dashboard under `wwwroot/`.
- [`tests/PalLLM.Tests`](tests/PalLLM.Tests) — NUnit. One fixture per
  subsystem; `SidecarEndpointTests.cs` covers end-to-end HTTP paths.
- [`mod/ue4ss/Mods/PalLLM/Scripts/main.lua`](mod/ue4ss/Mods/PalLLM/Scripts/main.lua)
  — the UE4SS bridge. Event producers, outbox consumer, guarded action
  executor, and the in-game delivery scaffold all live here.
- [`scripts/`](scripts) — install, doctor, smoke, delivery-replay, and
  release-package PowerShell.
- [`docs/`](docs) — Diátaxis-organized documentation.

## Pre-flight checklist before a PR

1. `dotnet test` passes locally (currently `1309 passed`). Shortcut that
   runs build + tests + every drift gate CI runs in one shot:
   `powershell -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom`.
   Writes a timestamped `artifacts/full-audit/<ts>/RESULTS.md`; a PASS
   there is the same green you'll see in CI.
2. `powershell -File scripts/run-sidecar-smoke.ps1` passes against a
   running sidecar when your change touches the outbox or action path.
3. `powershell -File scripts/doctor.ps1 -RunSmoke -RunDeliveryReplay` passes
   when your change touches install / health / runtime folder semantics.
4. Recommended before every PR: install `pre-commit`, run
   `pre-commit install`, then `pre-commit run --all-files`. The repo's
   `.pre-commit-config.yaml` runs gitleaks plus the repo-local path-reference
   and public-copy audits so publication drift is caught before CI.
5. New feature? Add a `FeatureDescriptor` entry in
   [`PalLlmFeatureCatalog.cs`](src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs)
   so `GET /api/features` and the dashboard surface it. Bump the
   catalog count in [`docs/ROADMAP.md`](docs/ROADMAP.md) — CI's
   `doc-drift` job fails the build if the count in code and ROADMAP
   disagree.
6. New endpoint or changed HTTP contract? Update [`docs/API.md`](docs/API.md),
   bump the route count in [`docs/ROADMAP.md`](docs/ROADMAP.md) and
   [`README.md`](README.md), then regenerate the committed snapshot with
   `powershell -File scripts/export-openapi.ps1`. CI enforces both the
   route-count math and the checked-in `docs/openapi/palllm-sidecar-v1.json`
   snapshot against the live route surface.
7. New bridge event type? Add a case in
   `PalLlmRuntime.ProcessBridgeEvent` and cover it with a `DrainInbox_*`
   test.
8. New fallback strategy? Add a `Try*` method in `FallbackBehaviorEngine`,
   bump the strategy count in ROADMAP, and extend the
   `PresentationCuePlanner` family mapping so the new strategy renders.
   CI enforces the ROADMAP count matches the number of
   `Try*`/`CreateGeneralDirector` methods.
9. New kill-switch / config? Add a row to the opt-in feature matrix in
   [`docs/OPERATIONS.md`](docs/OPERATIONS.md) and a per-feature enable
   subsection if it has non-trivial verification steps.
10. Touched a hot path or added new runtime code? Eyeball the coverage
   report posted to the CI run's step summary. `PalLLM.Domain` +
   `PalLLM.Sidecar` sit at `~88% line / ~75% branch` on the current baseline;
   PRs that noticeably drop either number without good reason should
   come with new tests. To inspect locally:
   `dotnet test PalLLM.sln --collect:"XPlat Code Coverage" --settings tests/PalLLM.Tests/coverlet.runsettings --results-directory ./TestResults`
   then `dotnet tool install --global dotnet-reportgenerator-globaltool`
   and `reportgenerator "-reports:TestResults/**/coverage.cobertura.xml" "-targetdir:CoverageReport" "-reporttypes:HtmlInline"`.
   Open `CoverageReport/index.html`. Both output dirs are
   `.gitignore`d.

## Design constraints

- **Local-first.** Cloud inference is optional, never required.
- **Deterministic fallback is a feature.** The director produces a
  multi-sentence reply without a live model; do not assume a chat reply
  must go through inference.
- **Every chat turn gets a presentation plan.** If you add a new fallback
  strategy, also extend the `PresentationCuePlanner` so a UE4SS consumer
  can render it.
- **Bridge is one-way + advisory.** The sidecar never reaches into
  Palworld directly. Anything in-game happens through the Lua bridge or
  the guarded executor, both of which carry their own kill switches.
- **No chain of exceptions.** Runtime methods return structured
  success/failure results; upstream model faults surface as `ChatResponse`
  with a diagnostic `ResponsePath`, not HTTP 5xx.

## Changing runtime contracts

`OutboxEnvelope`, `ChatResponse`, `PresentationCuePlan`, and the
`BridgeEventEnvelope` payloads are the durable seams between the sidecar
and the UE4SS layer. Any field renames or type changes need:

- A test in `SidecarEndpointTests.cs` covering the new shape.
- An update to [`docs/API.md`](docs/API.md) if the change is observable via
  HTTP.
- A parallel update to [`main.lua`](mod/ue4ss/Mods/PalLLM/Scripts/main.lua)
  (both the producer side and the outbox consumer) so in-game consumption
  does not regress silently.

## Adding a guarded action type

1. Add the new `(type, args, priority, justification)` mapping in
   [`ActionIntentPlanner.cs`](src/PalLLM.Domain/Runtime/ActionIntentPlanner.cs).
2. Add the type to the operator allowlist doc in
   [`docs/OPERATIONS.md`](docs/OPERATIONS.md) under "Enabling the action
   executor".
3. Add a handler in `main.lua` mirroring `execute_waypoint_suggest` /
   `execute_recall_pals` / `execute_craft_queue`, including the Lua-side
   allowlist entry.
4. Test both the emit path (chat → outbox carries the intent) and the
   blocked path (disabled or non-allowlisted type emits nothing).

## Style

- C# follows the default `.editorconfig` + analyzer set shipping with the
  `net10.0` templates. Prefer expression-bodied members where one-liner is
  natural; otherwise braces.
- Lua matches the existing `main.lua` conventions: snake_case names,
  pcall-guarded engine calls, small focused helpers, no global writes.
- Markdown uses sentence-case headings and backtick code paths relative to
  the repo root.

## Opening issues and PRs

- Bug reports, feature proposals, and compatibility drift reports
  each have a dedicated template under `.github/ISSUE_TEMPLATE/`.
  GitHub picks the right one when you click "New issue".
- Security vulnerabilities do **not** go through the issue tracker.
  See [`SECURITY.md`](SECURITY.md) for the private disclosure channel.
- The PR template mirrors the CI drift gates — tick the boxes that
  apply and CI will confirm the rest.

## Supply-chain defences

The repo has [Dependabot](.github/dependabot.yml),
[gitleaks](.gitleaks.toml),
[pre-commit](.pre-commit-config.yaml),
[CodeQL](.github/workflows/codeql.yml), and
[luacheck](.github/workflows/lua.yml) turned on:

- **gitleaks + pre-commit** give contributors a local-first guardrail for
  secret scanning, public-copy drift, and broken repo-local path references
  before a change ever reaches CI. Use `pre-commit install` once, then
  `pre-commit run --all-files` whenever a PR touches docs, scripts, or release
  surfaces.
- **Dependabot** opens grouped weekly PRs for NuGet packages, GitHub
  Actions, and Docker base images. Immediate PRs land on any newly
  published CVE against a tracked dependency. Merging those PRs keeps
  the runtime patched without a manual audit loop.
- **CodeQL** runs GitHub's `security-extended` C# query suite on every
  push, every PR, and weekly on a schedule. Findings land in the
  repo's Security tab under "Code scanning alerts".
- **luacheck** runs static analysis on the UE4SS Lua bridge on every
  push or PR that touches `mod/` or `.luacheckrc`. Catches undefined
  globals, shadowed locals, and empty blocks before the mod reaches
  a player's game — where a Lua error silently manifests as "nothing
  happened in-game." Config lives in `.luacheckrc` at the repo root;
  UE4SS globals are whitelisted there.
- **Code coverage** is collected on every CI run via `coverlet.collector`
  and rendered into the Actions step summary by `ReportGenerator`.
  The full HTML drilldown is uploaded as the `coverage-report`
  artefact (14-day retention). Use the numbers to spot regressions
  and to pick targeted test backfill; the runsettings file
  (`tests/PalLLM.Tests/coverlet.runsettings`) excludes generated code
  and the test assembly so numbers reflect real production code.

Contributors should review and act on all of these surfaces before
shipping a release. CI does not currently block a PR on open CodeQL
findings or on a coverage drop, but leaving a high-severity finding
unacknowledged is a release blocker.

## Reporting bugs

Open an issue with:
- PalLLM version / commit SHA.
- Output of `GET /api/health`.
- `scripts/doctor.ps1` output (include the full table).
- Reproduction steps or the exact outbox envelope that triggered it.

## Security

PalLLM runs entirely on `localhost` by default. If you find a way for an
outbox / inbox / pack / screenshot file to escape its runtime directory,
trigger an unauthenticated remote call, or run arbitrary code through one
of the guarded surfaces, email the maintainer privately before filing a
public issue.
