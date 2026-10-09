# AgentWork - per-agent change logs

This folder holds one running worklog per AI agent (or human) that works on
PalLLM. It exists so several agents can work in parallel without colliding on
the shared, append-only `CHANGELOG.md` and `docs/HANDOFF.md`: each agent
appends to *its own* file here, so two agents editing the repo at the same
time never conflict on the same lines.

## The protocol

1. **One file per agent, named after the agent.** When you start work, create
   `docs/AgentWork/<YourName>.md` if it does not exist (for example
   `Claude-Opus.md`, `Codex.md`, `Gemini.md`). Humans use their handle.
2. **Name yourself in the document.** The first lines under the title say who
   you are and how you run (model + harness), so attribution is unambiguous.
3. **Log every change you make to the code, newest entry first.** One entry
   per pass / unit of work. Keep each entry *succinct but useful* - enough
   that any other agent or a moderately experienced human understands what
   changed, why, and how it was verified, without reading the diff.
4. **Keep the stamp current.** Every `<agent>.md` carries a `Last audited:`
   date near the top (a drift gate requires it - see Gate notes below).

## Entry shape

Each entry is a short dated block:

    ## <date> - <pass id or short title>

    - **What:** one or two lines on the change.
    - **Why:** the goal, or the rule it serves.
    - **Files:** the main paths touched.
    - **Verify:** how you confirmed green (tests + audit, or what you ran).

That is enough. Do not paste large diffs - the git history holds those.

## How this relates to the other history files (no duplication)

These three are complementary, not duplicates - keep them in their lanes:

- **`docs/AgentWork/<agent>.md` (here)** - *who did what, and why*, from one
  agent's point of view. A running, per-agent, conflict-free record owned by
  that agent.
- **`CHANGELOG.md`** - the canonical, project-wide, append-only release
  history: the formal record of what shipped, in order.
- **`docs/HANDOFF.md`** - the *current* handoff snapshot: what just landed,
  what to read first, the current audited state. Always describes "now".

Rule of thumb: log routine, in-progress, per-agent work here; promote the
landed, canonical summary into `CHANGELOG.md` + `docs/HANDOFF.md` when a pass
is integrated and green. The authority model for all of this is the
[README](../../README.md) "Develop it" section.

## Gate notes (why this folder is safe)

Files in this subfolder are deliberately invisible to the doc-count and
INDEX-catalogue gates - both enumerate `docs/*.md` non-recursively - so adding
or removing an agent log never changes a gated number, and logs do not need
INDEX entries. The one gate that does reach in is the recursive "long-form
docs carry a `Last audited:` stamp" meta-test, which is why every `<agent>.md`
here needs the stamp (this `README.md` is exempt by filename). Keep files
clean UTF-8 (the mojibake gate scans this tree).
