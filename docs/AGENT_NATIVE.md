# Agent-native repository - design notes

Last audited: `2026-05-22`

PalLLM is built as an **agent-native** codebase: every surface that
matters is programmatically discoverable, every contract is
machine-readable, and every "where do I start?" answer is a single
structured payload away. This doc explains what that means
concretely, why it's worth the cost, and how the moving parts hang
together.

If you're a coding agent (Claude Code, Cursor, Copilot, Codex,
Aider, Continue, or any future agent that lands here), this is the
explanation of *why* the repo looks the way it does. If you're a
human contributor, this is the rationale for the JSON files at the
root that look unusual at first glance.

## What "agent-native" means here

Three constraints:

1. **One-payload onboarding.** An agent landing on the repo with
   no prior knowledge should be able to load *one* file and know
   what this project is, what it can do, what the rules are, and
   how to verify a change. That one file is
   [`agents.json`](../agents.json).
2. **Machine-readable surfaces win ties.** Where a human-readable
   doc and a machine-readable manifest both exist, the manifest
   is the source of truth and the doc references it. Examples:
   - [`pal.json`](../pal.json) is canonical; the
     [`CHEAT_SHEET.md`](CHEAT_SHEET.md) verb table renders from
     the same data.
   - [`PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) is canonical;
     every doc that pins a count cross-references it via the
     drift gates.
   - [`openapi/palllm-sidecar-v1.json`](openapi/palllm-sidecar-v1.json)
     is canonical; [`API.md`](API.md) is the human-friendly view.
3. **Every helper has a structured-output mode.** Verbs designed
   for agent use (`pal context`, `pal explain`, `pal where`) emit
   plain text by default and JSON with `-Json`. The JSON shape is
   stable enough that an agent can rely on field names without
   parsing prose.

## The four agent-native surfaces

### 1. `agents.json` - capability manifest

Top-level summary of the project as a structured object:

- Project identity (name / kind / scope / runtime)
- Root entry points (the half-dozen files an agent should know
  about by name)
- Rolling state (cross-checked counts; same numbers every drift
  gate enforces)
- Capabilities (what an agent can exercise here, with paths)
- Validation gates (the 14 checks; how to run them; what each
  one means)
- Hard rules (invariants no change may violate)
- Reading orders per audience
- Honest verdict (aggregate readiness score + top gap)

The schema is stable; future agents can rely on field names. The
file is drift-gated: every path it mentions is verified to exist.

### 2. `pal.json` - verb manifest

Every `pal.ps1` verb as a structured record with summary,
underlying script path, and example invocations. Two reasons this
is JSON instead of just text in `CHEAT_SHEET.md`:

- An agent can build its own verb-completion / auto-suggest layer
  without parsing PowerShell.
- The `pal list` table renders cleanly because the data is
  structured upstream (it doesn't currently - `pal list` reads
  `pal.ps1`'s `Run-List` array, but the JSON is the spec; the
  PowerShell array follows).

### 3. `pal explain <path>` - file-level structured deep-dive

Given any file or directory, returns:

- **Kind** (source / doc / script / test / config / sample / adr / schema)
- **Purpose** (extracted from `AGENT-CARD:` blocks, C# XML doc
  summaries, PowerShell `.SYNOPSIS`, or markdown title paragraphs)
- **Public surface** (top-level types / param names / schema id)
- **Related docs** (any doc that mentions this file's path or
  basename)
- **Related tests** (tests that mention the symbol)
- **Drift gates** (which gates pin counts that include this file
  - i.e. "if you change this, you'll trigger gate X")
- **Siblings** (immediate neighbours in the same directory)

`-Json` mode emits a structured record. Lets an agent ask "what
am I looking at?" without manual context assembly.

### 4. `pal where <query>` - natural-language -> file paths

Free-text query, ranked file list. Pure local search (no external
service). Ranking weights:

- Exact basename match (highest)
- Path-segment match (next)
- Path substring match (lowest)
- Filename match boost when target is under `src/`
- Filename match boost when target is `docs/CODE_MAP.md`
- Header-content keyword frequency (capped to discourage
  keyword-stuffed files)
- Exact-phrase bonus
- AGENT-CARD-block keyword bonus

Returns plain text by default with a one-line preview per file;
`-Json` for structured output.

## `AGENT-CARD:` blocks

A small, optional convention: the top of a file can carry an
`AGENT-CARD:` block containing a one-paragraph summary written for
agent consumption. Format:

```csharp
// ---------------------------------------------------------------
// AGENT-CARD:
//   what:    one-sentence summary of what this file owns.
//   surface: the publicly-visible types or methods.
//   gate:    drift gate name(s) that pin counts including this file.
//   adr:     load-bearing ADR(s), if any.
//   docs:    primary doc(s) covering the design.
// ---------------------------------------------------------------
```

The block is plain text (any comment style works); `pal explain`
extracts it preferentially over the regular file header. Files
that already have rich `///<summary>` doc comments don't need an
AGENT-CARD block - the C# XML doc comments serve the same purpose
and `pal explain` reads them.

The minimum file count we ship with cards is the load-bearing few:

- `src/PalLLM.Sidecar/Program.cs` +
  `src/PalLLM.Sidecar/RouteRegistrations/*.cs` - route registration sites
- `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` - feature
  catalog
- `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs` - fallback
  director
- `src/PalLLM.Domain/Packs/PersonalityPack.cs` - pack format
- `pal.ps1` - verb runner

Adding more cards is welcome but not required. The drift gates
care about counts, not card density.

## Reading orders

`agents.json` carries four reading orders by audience:

- **agent** -> `AGENTS.md` -> `agents.json` -> `HANDOFF.md` ->
  `CODE_MAP.md` -> `CONVENTIONS.md` -> `ANTI_PATTERNS.md`
- **harvester** -> `HARVEST.md` -> `CODE_MAP.md` -> `ADVISORS.md` ->
  ADRs -> `ARCHITECTURE.md`
- **operator** -> `OPERATIONS.md` -> `API.md` -> `RUNBOOK.md` ->
  `PRIVACY.md`
- **newcomer** -> `EASY_MODE.md` -> `QUICKSTART.md` ->
  `PROMPT_CARDS.md` -> `PITCH.md`

Pick the one that matches your task; don't read everything.

## Why this is worth the cost

Three benefits, one cost.

**Benefits.**

1. **Onboarding speed.** A fresh agent can load `agents.json`,
   know what this repo is in 30 lines of JSON, and start working
   without re-deriving the structure from the directory tree.
2. **Drift resistance.** Machine-readable manifests + drift gates
   = "the docs are out of date" can never linger. If
`PROJECT_NUMBERS.json` says 1154 tests, the gate verifies the
   live code agrees, on every audit.
3. **Harvestability.** Want to lift one capability into another
   project? `agents.json`'s `harvestableUnits` section names the
   capability + its files + its doc. No archaeology required.

**Cost.**

- One more file to keep updated. Mitigated by:
  - Drift gates that fail loudly if `agents.json` references a
    path that doesn't exist
  - The 45-day `Last audited` cap that forces a refresh if the
    file goes stale
  - Most fields are derived from other source-of-truth files
    (`PROJECT_NUMBERS.json`, `pal.ps1` verbs) so updates are
    largely mechanical

The trade has been worth it consistently - every "agent landed
on the repo and got lost" moment in the past was something this
file would have prevented.

## What is NOT in scope here

- An agent runtime framework. PalLLM has agents that *use* it
  (Claude Code, etc.) but doesn't ship its own.
- Auto-generating docs from code annotations. Tried, rejected -
  the `AGENT-CARD:` block is the manual, intentional alternative
  (see [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) for related
  rejected ideas).
- Replacing `AGENTS.md` with `agents.json`. The Markdown form has
  prose, code blocks, and reading flow; the JSON has structure
  and stable field names. They're complements, not substitutes.

## How to extend the agent-native layer

Four paths in increasing-effort order:

1. **Add an `AGENT-CARD:` block to a load-bearing file** (lowest
   effort, immediate `pal explain` upgrade).
2. **Add a `harvestableUnits` entry to `agents.json`** when you
   ship a new self-contained capability that another project
   could lift.
3. **Add a structured-output mode (`-Json`) to a new agent
   helper script.** Mirror the shape used by
   [`scripts/pal-explain.ps1`](../scripts/pal-explain.ps1).
4. **Propose a new agent-native surface** via an ADR. The bar:
   it must be drift-gateable, machine-parseable, and answer a
   question that humans currently answer by reading prose.

## Cross-references

- [`AGENTS.md`](../AGENTS.md) - the human-readable companion
- [`agents.json`](../agents.json) - the machine-readable manifest
- [`pal.json`](../pal.json) - verb manifest
- [`scripts/pal-explain.ps1`](../scripts/pal-explain.ps1) -
  file-level deep-dive
- [`scripts/pal-where.ps1`](../scripts/pal-where.ps1) -
  natural-language file lookup
- [`docs/PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) - rolling
  count manifest
- [`docs/openapi/palllm-sidecar-v1.json`](openapi/palllm-sidecar-v1.json)
  - HTTP contract snapshot
- [`docs/schemas/`](schemas/) - JSON Schema 2020-12 contracts
  for off-HTTP wire shapes


