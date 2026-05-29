# UX principles - operator + contributor experience

Last audited: `2026-05-22`

The seven principles that shape every operator-facing surface in
PalLLM (install scripts, dashboard, CLI scripts, error messages)
and every contributor-facing surface (docs, scaffolders, drift
gates, on-ramp). When in doubt, this is the tie-breaker doc.

These aren't aspirational - they're the choices already
encoded across the repo. If you find a surface that violates one,
treat it as a bug; if you propose a change that requires
violating one, write an ADR.

## 1. Defaults make a fresh install useful - and quiet

The first-time user gets a working sidecar with deterministic
chat in 90 seconds (`pal onboard`). They get zero outbound
network calls until they explicitly enable a feature. They get a
dashboard that loads and is honest about what's on / off.
**Why:** Surprise is the worst UX failure for a local-first
product. Surprise = "I didn't know it would do X."

See [`adr/0006`](adr/0006-opt-in-everything-by-default.md).

## 2. One memorable command per common operation

`pal onboard`, `pal build`, `pal test`, `pal audit`, `pal play`,
`pal status`, `pal context`, `pal scaffold`. No remembering
script paths, no remembering flag combinations, no `cd scripts &&
.\some-script.ps1 -Switch1 -Switch2`. **Why:** Cognitive cost
adds up. Every "wait what was that command again?" friction
point chips at flow.

The Makefile mirrors `pal.ps1` so `make build` works for
contributors with that muscle memory. The `.vscode/tasks.json`
mirrors it again so the IDE shortcut works. Three doors, one
source of truth.

## 3. Diagnostics name the failure, not the symptom

Every error surface - `pal onboard` failure, audit gate failure,
chat fallback `ResponsePath` - names what specifically went
wrong, not just "something failed." Examples:

- `ResponsePath: fallback-after-breaker-open` (not just "fallback")
- `Drift_Test_count_docs FAIL: code=554 readme=544` (not just "drift")
- `Sidecar won't start: port 5088 already in use by PID 14820` (not just "port error")

**Why:** A diagnostic that names the cause turns a bug report
into a 30-second fix. A diagnostic that names the symptom turns
it into a 30-minute investigation.

## 4. Every surface has an inspectable shape

`/api/health`, `/api/describe`, `/api/quickstart`,
`/api/privacy/posture`, `/api/release/readiness`,
`/api/bridge/proof` - all return structured JSON whose shape is
contract-tested. Operators (and AI clients) can build dashboards,
alerting, and automation without scraping prose.

`pal status` and `pal context` mirror the same idea on the CLI:
machine-readable output that doubles as human-readable output.

**Why:** Anything an operator might want to monitor or assert on
should be queryable as data, not a screenshot.

## 5. The audit is the safety net, not the gatekeeper

The 16 drift gates run in seconds. They fire on real drift, not
on stylistic preference. When they fail, they say what to fix.
They never block a legitimate change for a fixable reason.

The implicit promise: a contributor who writes good code and
keeps the docs in sync will pass the audit on the first try.
**Why:** Gates that fire on noise erode trust. The drift gates
have to feel like a colleague who catches your typos, not a
bureaucrat.

See [`adr/0004`](adr/0004-drift-gates-over-manual-review.md).

## 6. Documentation is layered, not monolithic

Different audiences need different doors. PalLLM ships:

| Door | Audience | Length |
|---|---|---|
| `PITCH.md` | Curious layperson | 5 min |
| `QUICKSTART.md` | First-time operator | 5 min |
| `QUICKSTART.md` | First-time contributor | 60 min |
| `MENTAL_MODEL.md` | Anyone forming the conceptual map | 10 min |
| `CHEAT_SHEET.md` | Returning contributor | 1 page |
| `QUICKREF.md` | Returning contributor (sortable lookup) | 1 page |
| `COOKBOOK.md` | "I want to add X" | per-recipe |
| `RUNBOOK.md` | "Something's wrong" | per-symptom |
| `AGENTS.md` + `MENTAL_MODEL.md` + `HANDOFF.md` | AI agent | tour |

**Why:** A single 100-page README serves no audience well. Many
short focused docs serve every audience well, and `INDEX.md` /
`CHEAT_SHEET.md` route readers to the right one.

## 7. Provenance is automatic, not asked-for

Every automated change in PalLLM (chat reply, promotion
suggestion, fallback-strategy fire, audit run) generates a
`ProofPacket` with a SHA-256 id, the decision text, the evidence
lines, the rollback path, and a confidence label. The operator
never has to *ask* "why did the runtime do that?" - the trail is
emitted alongside the decision.

The same posture extends to the supply chain: every release zip
ships with sigstore signing + SLSA provenance + CycloneDX SBOMs.
**Why:** A local AI runtime that explains itself by default is a
runtime that operators trust and reviewers can audit without
prompting.

## Anti-principles (things PalLLM deliberately *does not* do)

- **Don't pre-bake "easy" by hiding consequence.** The dashboard
  doesn't have a "make this faster" button that secretly enables
  cloud inference. Every speed-up is opt-in and named.
- **Don't optimize for the demo path at the expense of the
  long-tail path.** The 5-minute quickstart works because the
  underlying runtime works, not because the runtime fakes the
  demo while breaking everything else.
- **Don't add a config knob to apologize for a bad default.** If
  a default is wrong, fix the default. Knobs are for genuine
  per-environment variation, not for sweeping bad UX under a
  flag.
- **Don't give an agent advice that contradicts what the
  audit enforces.** The drift gates are the contract. The docs
  describe the contract. They never disagree; if they do, fix
  the docs.

## Related

- [`DESIGN_PRINCIPLES.md`](DESIGN_PRINCIPLES.md) - the deeper
  architectural principles (this doc focuses on UX
  specifically)
- [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) - what's been
  deliberately rejected
- [`MENTAL_MODEL.md`](MENTAL_MODEL.md) - the conceptual
  scaffolding for new contributors


