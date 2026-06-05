# AGENTS.md - agent entry point

Last audited: `2026-06-05`

**[`README.md`](README.md) is the single source of truth.** It describes PalLLM
completely - what it is, how it is shaped, everything it exposes, how to run it,
and (in its [Develop it](README.md#develop-it---the-authority-model) section)
the full authority model: the one rule, the seven non-negotiable invariants, the
drift gates, the working loop, what-not-to-touch, and the verify commands.

**Read `README.md` first.** This file adds only the few agent-specific deltas
the README cannot carry - because `README.md` is one of five
publication-facing files that a drift gate keeps free of vendor brand names, so
it deliberately says "the bundled local engine" instead of naming it. Below is
what that name is, plus the agent depth chain. Nothing here re-describes the
system; go to `README.md` for that.

## The one rule (every loop)

**Every change starts green and ends green.** The two commands, inline for
convenience (full context in the README's Develop it section):

```powershell
dotnet test PalLLM.sln -c Release --nologo
pwsh -NoProfile -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging
```

## The brand-ful deltas (what README is not allowed to name)

- **The bundled local engine is `llama.cpp`.** Where the README says "the
  bundled local engine," it means `llama.cpp`'s `llama-server`. It is the
  **only** local engine - never add a second local engine path (invariant 3).
  PalLLM is just an HTTP chat-completions client to it.
- **The shipping model strategy is a per-turn mesh** - a fast lane plus a
  smart/multimodal lane - configured in
  `src/PalLLM.Sidecar/appsettings.json`. The concrete model identifiers, the
  hardware-tier matrix, the per-model sampler profiles, and the known-bug
  catalog live in [`docs/LLAMA_CPP_BUNDLED.md`](docs/LLAMA_CPP_BUNDLED.md) and
  [`docs/LOCAL_MODELS_INVENTORY.md`](docs/LOCAL_MODELS_INVENTORY.md). Keep model
  names out of the five publication files; they belong in those operator docs
  and in `appsettings.json`.
- **The escape path** for below-reference hardware is any hosted
  chat-completions endpoint (`scripts/connect-cloud.ps1`), wired the same way.

## The agent depth chain

When the README's Develop it section sends you deeper, follow this order:

1. [`docs/HANDOFF.md`](docs/HANDOFF.md) - what just landed + current audited state.
2. [`docs/CODE_MAP.md`](docs/CODE_MAP.md) - symbol-to-file navigation.
3. [`docs/CONVENTIONS.md`](docs/CONVENTIONS.md) - the advisor / builder / validator / feeder patterns.
4. [`docs/ANTI_PATTERNS.md`](docs/ANTI_PATTERNS.md) - what was already considered and rejected.
5. [`docs/INVARIANTS.md`](docs/INVARIANTS.md) - each invariant's enforcement site.

Machine-readable companion to this file: [`agents.json`](agents.json) (schema-validated).
Every number lives in [`docs/PROJECT_NUMBERS.json`](docs/PROJECT_NUMBERS.json).
Every doc, mapped: [`docs/INDEX.md`](docs/INDEX.md).

## History + the review ritual

History is **append-only**: never rewrite past `CHANGELOG.md` /
`docs/HANDOFF.md` entries, ADRs, or dated research notes. Before a change, read
the README's Develop it section; after a change, update `README.md` and its
drift-gated numbers so they never go stale - the audit fails if they do.

## Your per-agent worklog

You are not the only agent here. To keep parallel work conflict-free, log what
you change in your **own** file under `docs/AgentWork/` - create
`docs/AgentWork/<YourName>.md` if it does not exist, name yourself in it, and
append one short entry per pass (what / why / files / how you verified). That
keeps the shared `CHANGELOG.md` and `docs/HANDOFF.md` from becoming
merge-conflict hot spots while still leaving a clear, attributable trail. The
protocol is in [`docs/AgentWork/README.md`](docs/AgentWork/README.md).

---

The other agent doorways defer here and to `README.md`:
[`CLAUDE.md`](CLAUDE.md) (Claude Code shortcut), [`.cursorrules`](.cursorrules),
[`llms.txt`](llms.txt), and the file under `.github/`. Welcome - human or agent,
`README.md` gives you the whole picture; this page gives you the engine's name.
