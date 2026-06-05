# Claude-Opus - work log

Last audited: `2026-06-05`

**Agent:** Claude (Opus 4.8), working via Claude Code.
**Lane:** documentation hardening - the central `README.md` as the single
source of truth, doc structure, and repo hygiene. I deliberately stay out of
the two-model mesh source/test lane while another agent holds it uncommitted.

Newest entries first. The protocol is in [README](README.md) in this folder.

## 2026-06-05 - Pass 448: establish docs/AgentWork/

- **What:** created this per-agent worklog folder, its protocol, and my own
  log; added a pointer from `AGENTS.md`.
- **Why:** lets parallel agents record changes without colliding on the
  shared `CHANGELOG.md` / `docs/HANDOFF.md`; fulfils the operator request that
  "each agent names a document after itself and logs every change it makes".
- **Files:** `docs/AgentWork/README.md`, `docs/AgentWork/Claude-Opus.md`,
  `AGENTS.md`.
- **Verify:** gate-safe by construction (doc-count + INDEX catalogue are
  non-recursive; this log carries the required `Last audited:` stamp; clean
  UTF-8). Committed as an isolated change touching only my own files, so it
  does not disturb the mesh pass another agent had in flight.

## 2026-06-05 - Pass 447: README promoted to the single source of truth

- **What:** rewrote `README.md` into the complete, brand-free single source of
  truth - inlined the authority model (the one rule, the invariants, the
  drift-gate cascade, the exact verify commands, the before/after review
  ritual) and restored the Minimum requirements section, with descriptive
  unnumbered headings. Deduped `AGENTS.md` into a lean agent delta that defers
  to the README.
- **Why:** operator directive - reading only the README must give a complete
  picture, and a coding agent the authority model without leaving the page;
  remove the README/AGENTS duplication.
- **Files:** `README.md`, `AGENTS.md`, `CLAUDE.md`, `docs/INDEX.md`,
  `CHANGELOG.md`, `docs/HANDOFF.md`.
- **Verify:** full audit 16/16 gates + 1310 tests green; committed `178e4a0`.

## 2026-06-05 - Pass 446: AGENTS.md hardened (then refined by 447)

- **What:** first hardened `AGENTS.md` into a full source-of-truth root; Pass
  447 then moved that role to the README and slimmed `AGENTS.md` back to a
  brand-ful delta.
- **Why:** the brand gate forbids the README from naming the engine/models,
  which briefly made `AGENTS.md` look like the natural SSOT; resolved in 447
  by keeping the README as the brand-free complete SSOT and `AGENTS.md` as the
  delta that names what the README cannot.
- **Files:** `AGENTS.md` (committed `4f73bea`).
- **Verify:** green at commit; superseded by 447.
