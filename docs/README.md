# `docs/`

Diataxis-organized documentation. **39+ docs at last audit**,
all cross-linked through [`INDEX.md`](INDEX.md).

## If you're here for the first time

Start with one of these depending on who you are:

| You are... | Start here |
|---|---|
| A curious layperson | [`PITCH.md`](PITCH.md) |
| A first-time operator | [`QUICKSTART.md`](QUICKSTART.md) |
| A coding agent | [`../AGENTS.md`](../AGENTS.md) → [`HANDOFF.md`](HANDOFF.md) |
| A returning contributor | [`HANDOFF.md`](HANDOFF.md) → [`CHEAT_SHEET.md`](CHEAT_SHEET.md) |
| A harvester | [`HARVEST.md`](HARVEST.md) |
| Someone debugging an incident | [`RUNBOOK.md`](RUNBOOK.md) |

The full doc map is in [`INDEX.md`](INDEX.md).

## Sub-directories

```
docs/
├── adr/                ← Architecture Decision Records (6 accepted)
├── schemas/            ← JSON Schemas for off-HTTP wire shapes
├── openapi/            ← committed OpenAPI snapshot for /api/*
└── examples/           ← runnable example MCP client configs
```

## Conventions

- Every long-form doc carries a `Last audited:` stamp at the
  top. The drift gate `Drift_Doc_freshness` enforces a 45-day
  cap.
- Every code path mentioned uses backticks and a repo-relative
  path (`src/PalLLM.Sidecar/Program.cs`), never absolute.
- HTTP examples use PowerShell-style backtick line
  continuations so they paste into a default Windows dev box.
- When a number appears in more than one doc, one doc explains
  the source of truth and the rest match. The drift gates
  enforce that the numbers don't disagree.

## Adding a doc

1. Choose the Diataxis quadrant: tutorial / how-to / reference
   / explanation. See [`INDEX.md`](INDEX.md) § "By Diataxis
   quadrant" for the existing examples in each.
2. Add the `Last audited:` stamp at the top.
3. Cross-link from [`INDEX.md`](INDEX.md) (both "Start here"
   table and the relevant Diataxis-quadrant section).
4. If the doc references code paths, the
   `Drift_Path_references` gate verifies they resolve.
5. If the doc has internal markdown links,
   `Drift_Dangling_markdown_links` verifies they resolve.

Run `pwsh ../pal.ps1 fast-audit` before pushing.
