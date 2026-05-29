# PalLLM — Agent Onboarding

Last audited: `2026-05-07`

Read this file first if you're a coding agent (Claude Code, Cursor,
Copilot, Codex, Aider, Continue, etc.) picking up this repo.

## TL;DR (60 seconds)

- **What this is:** a local-first LLM companion runtime for Palworld.
  Self-contained .NET 10 sidecar + UE4SS Lua bridge + 37 MCP tools +
  portable adapter surface.
- **Where to start:** [`docs/HANDOFF.md`](docs/HANDOFF.md) is your
  short-form briefing. [`docs/INDEX.md`](docs/INDEX.md) is the full
  doc map. [`docs/CODE_MAP.md`](docs/CODE_MAP.md) tells you where
  any symbol lives. **For small models or full replication-from-docs,
  start at [`docs/CODE_MAP.md`](docs/CODE_MAP.md) plus
  [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).**
- **Source of truth for counts:** the feature catalog file, the live
  `Program.cs` route registrations, and the NUnit suite. Never trust
  a doc count without cross-checking — every doc count is drift-gated
  by `scripts/run_full_audit.ps1`.
- **How to know if your change is safe:** run
  `dotnet test PalLLM.sln` and
  `powershell -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging`.
  Both green = your change preserves every invariant this codebase
  cares about.

## Essential reading (in order)

1. [`docs/HANDOFF.md`](docs/HANDOFF.md) — 30-second briefing: current
   audited state, latest landed passes, highest-value remaining
   blockers, recommended next coding pass.
2. [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) — layout,
   data flow, HTTP surface, MCP surface, background workers.
3. [`docs/CODE_MAP.md`](docs/CODE_MAP.md) — "where does X live?"
   symbol-to-file navigation for harvesting and editing.
4. [`docs/ADVISORS.md`](docs/ADVISORS.md) — one-page catalog of every
   advisor / builder / validator / feeder / tracker with file path,
   public surface, kind, and surfacing. Use this when harvesting or
   adding a new capability.
5. [`docs/CONVENTIONS.md`](docs/CONVENTIONS.md) — the small set of
   patterns every new contribution should follow (advisor pattern,
   builder pattern, deterministic-first, etc.).
6. [`docs/DESIGN_PRINCIPLES.md`](docs/DESIGN_PRINCIPLES.md) — the
   10 principles that hold the codebase together. Read this before
   proposing an architectural change.
7. [`docs/ROADMAP.md`](docs/ROADMAP.md) — honest position (76.2%
   player-experience-weighted) + phase-ordered build queue.

> If you're trying to explain the project to a human stakeholder,
> [`docs/PITCH.md`](docs/PITCH.md) is the plain-English narrative
> and [`docs/FAQ.md`](docs/FAQ.md) answers the most common
> first-time questions. [`docs/GLOSSARY.md`](docs/GLOSSARY.md)
> defines every PalLLM-specific term (advisor, director, duo, role,
> posture, proof packet, etc.) — look there first if you hit an
> unfamiliar word in the codebase.

## Non-negotiable invariants

Every change MUST preserve these. If you can't, the change is wrong:

- **Deterministic fallback always answers.** If inference is off,
  broken, rate-limited, or thermal-throttled, `POST /api/chat` still
  returns a reply. Never break this path.
- **Default install is fully local.** Zero outbound network traffic
  unless the operator explicitly configures live inference / vision
  / TTS / OTLP endpoints. See [`docs/PRIVACY.md`](docs/PRIVACY.md).
- **Every drift gate stays green.** 16 gates in
  `scripts/run_full_audit.ps1`. Your change must not introduce new
  drift.
- **Every feature currently in the catalog stays in the catalog.**
  `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` is an explicit
  contract. Remove an entry only with an accompanying
  `status: deprecated` flag + `CHANGELOG.md` note.
- **Never leak implementation details through `/api/*` error
  bodies.** Use `ProblemDetails` with the standard shape. No stack
  traces, no exception type names.
- **Never break the packaged-EXE boot path.** Passes 2 + 15 + 17
  invested real work in making `sidecar/publish/PalLLM.Sidecar.exe`
  boot cleanly with static web assets. If you change static-asset
  handling, the packaged EXE is where it fails — test there.

## Working loop

```
1. Read HANDOFF.md. Identify the task.
2. Skim CODE_MAP.md for the relevant symbol.
3. Make the smallest change that solves the task.
4. Add a test.
5. dotnet test PalLLM.sln            → 0 failing
6. scripts/run_full_audit.ps1 ...    → 16/16 PASS
7. Update CHANGELOG.md if user-visible.
8. If route/feature/test count changed, also update:
   - README.md badge + surface summary
   - docs/ROADMAP.md "Audit basis" block
   - docs/ARCHITECTURE.md feature-catalog snapshot
   - docs/HANDOFF.md "Current audited state"
   - docs/API.md surface-at-a-glance table
```

The drift-gate audit enforces step 8 — if you skip it, the audit will
tell you exactly which doc to fix.

## What not to do

- Don't rename or delete files in `src/PalLLM.Domain/Portable/` —
  they're the redistributable seam and downstream consumers depend on
  the shapes.
- Don't rewrite `Program.cs` into controllers. The minimal-API style
  is deliberate; it keeps the route inventory greppable for the
  audit gates.
- Don't introduce a new NuGet dependency without adding it to
  `THIRD_PARTY_NOTICES.md`.
- Don't change `PalLLM:` config-key shapes without a migration note
  in CHANGELOG.
- Don't fix the CS1591 XML-doc warnings in bulk — they're tracked
  cleanup debt and a known-safe low-priority. Fix them incrementally
  alongside the files you were going to touch anyway.
- Don't add mock network calls in tests. The test fixtures boot a
  real in-process sidecar with inference / vision / TTS all
  `Enabled=false` — that's the reference test posture.

## Pattern recognition (what you'll see a lot)

See [`docs/CONVENTIONS.md`](docs/CONVENTIONS.md) for the full pattern
catalog. The short version:

- **Advisor pattern** — `XxxAdvisor.Advise(...)` or `.Forecast(...)`
  returns a structured record. Pure function, no side effects.
  Surfaced via `GET /api/xxx` + `pal_xxx` MCP tool + sometimes a
  dashboard chip. Examples: `WorldNarrationAdvisor`,
  `MoodWeatherAdvisor`, `GracefulDegradationAdvisor`.
- **Builder pattern** — `XxxBuilder.Build(...)` returns a snapshot
  record. Examples: `PromotionApplyPreviewBuilder`,
  `PrivacyPostureBuilder`, `ResourceBudgetPostureBuilder`.
- **Validator pattern** — `XxxValidator.Validate(...)` returns a
  `XxxValidationResult { IsValid, Checks[], Issues[] }`. Examples:
  `PersonalityPackValidator`, `NarrativePackValidator`.
- **Feeder pattern** — a background worker that observes metrics +
  writes bounded records. Examples: `PromotionLedgerFeeder`,
  `SelfHealingWorker`.

If you're adding a new capability, pick the matching pattern and
mirror the existing structure. Copy-paste a sibling, rename, tweak.

## How to harvest a capability

This repo is explicitly designed so you can lift individual
capabilities into other projects. See [`docs/HARVEST.md`](docs/HARVEST.md)
for the full menu. Short answer: most of
`src/PalLLM.Domain/Runtime/*.cs` is pure .NET 10 with no Palworld /
UE4SS dependency — it harvests cleanly into any ASP.NET or console
host.

## If you break something

- Tests fail → read the failure message; drift-gate errors are
  self-explanatory.
- Audit fails → `artifacts/full-audit/<latest-ts>/steps/*.log` has
  the per-gate detail.
- Live probe fails → check `D:/PalLLM-1.0.0-prod/sidecar.stderr.log`
  (or wherever you extracted the zip) for the sidecar output.
- Completely stuck → `git status` + the last green audit artifact
  should give you enough to revert surgically.

## Handoff protocol

When you finish a task, update [`docs/HANDOFF.md`](docs/HANDOFF.md)'s
"What just landed" block so the next agent picks up the current
state, not a three-pass-stale one. The doc freshness drift gate
enforces the `Last audited: YYYY-MM-DD` stamp on every doc.

Welcome. The repo is ready for you.
