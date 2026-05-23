# Architecture Decision Records (ADRs)

Last audited: `2026-04-25`

Every ADR captures **one decision**, the **context** that made it
necessary, the **alternatives considered**, and the **consequences**
the team accepted by going that way. Format follows Michael Nygard's
2011 template (the de-facto industry standard) plus a "harvest hint"
section unique to PalLLM that names the file(s) a harvester would
copy if they wanted the decision in their own project.

## Why ADRs in PalLLM

PalLLM is built to be picked up by a coding agent (or a layperson
reading code over the maintainer's shoulder) and continued without a
session of "why is this done this way?" archaeology. The drift gates
keep counts in sync; the ADRs keep **rationale** in sync.

If you're about to refactor or remove something that an ADR named, read
the ADR first. Most "obvious" simplifications were considered and
rejected for a documented reason.

## Status lifecycle

- **Proposed** — written, not yet binding
- **Accepted** — current design; the codebase reflects it
- **Deprecated** — was true, no longer is; kept for history
- **Superseded** — replaced by a newer ADR (linked in the header)

Most PalLLM ADRs are `Accepted` — they describe load-bearing decisions
that subsequent passes built on.

## Index

| # | Title | Status |
|---|---|---|
| [0001](0001-deterministic-first-reply-pipeline.md) | Deterministic-first reply pipeline | Accepted |
| [0002](0002-portable-adapter-seam.md) | Portable adapter seam | Accepted |
| [0003](0003-one-way-advisory-bridge.md) | One-way advisory bridge between sidecar and game | Accepted |
| [0004](0004-drift-gates-over-manual-review.md) | Drift gates over manual review | Accepted |
| [0005](0005-ttl-cache-for-posture-surfaces.md) | TTL-cache pattern for read-heavy posture surfaces | Accepted |
| [0006](0006-opt-in-everything-by-default.md) | Opt-in everything by default | Accepted |

## How to add an ADR

1. Copy the most recent ADR file as `NNNN-kebab-case-title.md` where
   `NNNN` is the next zero-padded number.
2. Fill in the sections — keep it short. An ADR should fit on one
   screen; readers come for the decision, not a thesis.
3. Add a row to the index above.
4. If your change updates a load-bearing seam (anything in
   `src/PalLLM.Domain/Portable/`, the bridge contract, the chat
   pipeline), link the ADR from the relevant doc
   (`ARCHITECTURE.md`, `CONVENTIONS.md`, `DESIGN_PRINCIPLES.md`).
5. Run the drift audit; the markdown-link gate will catch dangling
   links if the index is out of date.

## How to retire an ADR

Don't delete it. Set the status to **Deprecated** or **Superseded**
and (if superseded) link forward to the replacement. ADRs are a
historical record — a future agent may need to know what *used* to be
true before they can safely change it.
