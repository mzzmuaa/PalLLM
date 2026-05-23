# ADR 0004 - Drift gates over manual review

- **Status:** Accepted
- **Date:** 2026-04
- **Tags:** ci, documentation, automation
- **Depends on:** none (works on any repo)
- **Supports:** every other ADR (drift gates protect the invariants those ADRs establish)

## Context

This repo is built to be picked up and continued by coding agents (or
new contributors arriving cold). Documentation drift is the single
biggest reason a new arrival makes a wrong assumption: a doc says
"55 routes" while the code has 56, the README says "534 tests" while
the suite has 582, the OpenAPI snapshot is six versions stale.

Manual review catches some of this. It also misses some, and worse,
the things it catches are *boring* - counting `[Test]` attributes is
not a high-value review activity. Doing it 30 times per PR is the
sort of cognitive tax that makes contributors give up.

## Decision

Every count, every snapshot, every well-formedness invariant that can
be checked mechanically gets a **drift gate** in
`scripts/run_full_audit.ps1`. The current set of 16 gates runs:

1. `Build_Release` - `dotnet build` succeeds with zero warnings
2. `Tests` - `dotnet test` passes (1154 / 1154 currently)
3. `Drift_Mojibake` - no UTF-8 corruption in tracked files
4. `Drift_Api_route_count` - `api.Map*` calls in `Program.cs` agree
   with route counts in README, ROADMAP, ARCHITECTURE, API
5. `Drift_Api_reference_surface` - the `docs/API.md` route list
   matches the Program.cs registrations one-to-one
6. `Drift_OpenApi_snapshot` - the committed
   `docs/openapi/palllm-sidecar-v1.json` matches the live route
   surface
7. `Drift_Feature_catalog_count` - `Id = "..."` entries in
   `PalLlmFeatureCatalog.cs` agree with counts in README, ROADMAP,
   ARCHITECTURE, HANDOFF, CODE_MAP
8. `Drift_Feature_status_split` - ready / scaffolded / deferred
   counts in code match counts in docs
9. `Drift_Fallback_strategy_count` - `Try*` methods in
   `FallbackBehaviorEngine` plus `CreateGeneralDirector` agree with
   ROADMAP
10. `Drift_Test_count_docs` - `[Test]` attribute count agrees with
    counts in README, ROADMAP, ARCHITECTURE, HANDOFF, CODE_MAP
11. `Drift_Public_copy` - release-facing files (README, NOTICE,
    SECURITY, INDEX, RELEASE) and support-facing files (CONTRIBUTING,
    issue templates) avoid third-party brand references
12. `Drift_Path_references` - every repo-relative path mentioned in
    a doc actually exists
13. `Drift_Agents_manifest` - `agents.json` conforms to required keys
    and type contracts
14. `Drift_Doc_freshness` - every doc with a `Last audited:` stamp
    is within 45 days
15. `Drift_Hot_file_line_count` - hot-file line-count mirrors stay
    within the configured tolerance
16. `Drift_Dangling_markdown_links` - every `[text](path)` link
    resolves

If a gate fires, the audit report names the exact file and the
expected vs. actual values. The fix is mechanical: bump the doc to
match the code (or fix the code if the doc was right).

## Alternatives considered

- **Code review checklist.** Putting "remember to bump the route
  count" in a PR template gets ignored 1 PR in 3 by tired humans.
- **Pre-commit hook only.** Better, but a contributor without
  pre-commit installed bypasses it. The gates run in CI too, so
  they're hard to skip.
- **Generated docs only.** Tempting, but PalLLM's docs are
  hand-written for narrative clarity. Generating "55 routes" into
  README from the code surface is fine; generating *the prose around
  it* makes the doc unreadable.

## Consequences

**Positive:**
- Counts are always accurate. A coding agent reading the README sees
  the live state, not a 6-month-old snapshot.
- The reviewing-the-PR cognitive tax goes way down. Reviewers can
  focus on whether the *code change* is right, not on whether
  six docs got updated to match.
- New contributors get fast feedback: run
  `powershell -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom`
  locally and see exactly what's out of sync.
- The PR template can mirror the gates one-to-one - the contributor
  ticks boxes, and the gates verify each tick.

**Negative:**
- The audit script is now a load-bearing piece of code itself
  (`scripts/run_full_audit.ps1` is ~750 lines). When a new doc or
  invariant is added, the script must be extended.
- Gates run for ~10 seconds locally. Acceptable, but a slow disk or
  cold cache can push it longer.
- The gates only catch what they're written to catch. Drift in
  prose (a stale paragraph) won't be caught - that's still a human
  task. The doc-freshness gate is a partial mitigation: any doc
  older than 45 days needs a refresh stamp, which forces a re-read.

## Harvest hint

The audit script is in PowerShell, but the pattern works in any
shell. Read `scripts/run_full_audit.ps1` for the structure: each
gate is a small function that returns a result object with
`PASS`/`FAIL`/`WARN` + a summary line. The orchestrator collects
them into a Markdown report under
`artifacts/full-audit/<timestamp>/RESULTS.md`.

## Related

- Code: `scripts/run_full_audit.ps1`,
  `.github/workflows/*` (CI runs the same gates)
- Docs: [`AGENTS.md`](../../AGENTS.md) Section "Working loop" lists every
  gate, [`CONTRIBUTING.md`](../../CONTRIBUTING.md) Section "Pre-flight
  checklist before a PR" walks through the local-run shortcut



