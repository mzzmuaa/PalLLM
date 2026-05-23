# Companion moments — scripted lines tied to in-world triggers

Last audited: `2026-05-10`

A "moment" in PalLLM is a scripted, in-character line the companion
says when a specific in-world trigger fires. The moments catalog is
hand-curated, harvestable JSON shipped under
[`samples/moments/companion-moments.json`](../samples/moments/companion-moments.json).
This doc explains what moments are, why they exist alongside the
deterministic-fallback engine and the LLM, and how to add new ones.

If you're a player, you don't see these as "moments" — you see them
as "the companion noticed". If you're a contributor, this is the
extension point that makes the companion feel hand-crafted in
specific situations without writing more fallback strategies.

## What a moment is

A trigger + a tone + a list of one-liners. The runtime fires the
matching trigger and selects one of the lines. Example:

```json
{
  "trigger": "alpha-defeated",
  "context": "Player just defeated an alpha-tier pal in the wild.",
  "tone": "dry",
  "lines": [
    "Big one's down. Don't loot until you've checked your six.",
    "Done. Hold position a beat - alpha aggro pulls for thirty seconds.",
    "That fight was longer than it had to be. Worth it."
  ]
}
```

When `alpha-defeated` fires, the consumer (the runtime, a future
HUD layer, or any harvesting app) selects one of the three lines.
Selection strategy is up to the consumer — rotation, weighted
random, last-N suppression.

## Why moments exist alongside fallbacks and LLMs

Three layers, three jobs:

| Layer | When it fires | Author |
|---|---|---|
| **Moments catalog** | Specific in-world triggers fire (alpha down, weather shift, low HP, etc.) | Hand-written, in-character, **finite** set per trigger |
| **Deterministic fallback** | Every chat turn has to answer; no specific trigger applies | Hand-written generators per scenario family (19 strategies) |
| **Live LLM** | Operator wired an inference endpoint and the moment didn't already cover it | The model |

Moments win when a trigger has a *small* number of "right" responses
that you'd want regardless of model availability. "Pal died" is a
moment — there are maybe five things you ever want the companion to
say there, and you want all five hand-written. "Tell me about the
ridge to the north" is not a moment — that needs the model.

## Phase status

Today (`docs/ROADMAP.md` ~76.2% complete) the moments **catalog** ships
as harvestable JSON + this design doc. The runtime that fires
triggers from in-game events is **Phase 4** work (native HUD /
audio / action wiring). The catalog is shippable today because:

1. Pack authors can write their own moments per persona.
2. Other local-AI apps can lift the format unchanged.
3. The deterministic fallback engine can already use moments as
   candidate lines for matching scenarios.
4. When Phase 4 lands, the moment selector is a thin layer on top
   of the existing catalog, not a from-scratch build.

## Format

Each entry has these fields:

| Field | Required | Notes |
|---|---|---|
| `trigger` | yes | Stable kebab-case identifier. The contract between the catalog and the consumer. |
| `context` | yes | Authoring hint — what the player is doing when this fires. Not runtime data. |
| `tone` | optional | One of `warm`, `dry`, `urgent`, `quiet`, `curious`, `playful`. Authoring hint. |
| `lines` | yes | Array of one-liners (at least one). Each line is the actual text the companion says. |

Lines must be:
- **In character.** Match the deterministic-fallback voice — direct,
  not theatrical.
- **One sentence preferred, two lines maximum.** Long lines lose
  the moment.
- **Specific, not generic.** "That fight was longer than it had
  to be" beats "Nice fight!" by every measure that matters.

## How to add a new moment

```powershell
# 1. Open the catalog
code samples/moments/companion-moments.json

# 2. Add an entry with a new trigger id
#    Pick something stable - the runtime contracts on this string.

# 3. Validate the JSON parses
Get-Content samples/moments/companion-moments.json | ConvertFrom-Json | Out-Null
```

There's no schema validator yet — the catalog is plain JSON and
the format is small enough to follow by example. A formal
`samples/moments/companion-moments.schema.json` is on the
roadmap.

## The five ritual catalogs

Passes 103, 104, and 109 ship five hand-curated catalogs
alongside the moments file. Each has a matching `pal` verb plus
a slash command inside `pal campfire`:

| Catalog | Verb | Slash | What it is |
|---|---|---|---|
| [`companion-fortunes.json`](../samples/moments/companion-fortunes.json) (~28 lines) | `pal fortune` | `/fortune` | Date-seeded daily one-liner. Same all day, different tomorrow. Categories: morning / field / base / reflection. |
| [`companion-whispers.json`](../samples/moments/companion-whispers.json) (~30 lines) | `pal whisper` | `/whisper` | Random quiet one-liner. Ambient, no fanfare, no questions. |
| [`companion-quests.json`](../samples/moments/companion-quests.json) (~30 quests) | `pal quest` | `/quest [tier]` | Small ~30-minute self-contained challenge. Tiers: easy / medium / spicy / quiet. |
| [`companion-tales.json`](../samples/moments/companion-tales.json) (~12 tales) | `pal tale` | `/tale [prefix]` | 3-4-line in-character campfire story. |
| [`companion-patrols.json`](../samples/moments/companion-patrols.json) (~12 reports) | `pal patrol-report` | `/patrol [prefix]` | The companion narrates the night they spent watching while you slept. 4-6 lines, atmospheric. Designed for "first login of the session" moments. |

Same authoring rules apply across all four:
- **In character.** Match the deterministic-fallback voice — direct,
  not theatrical.
- **One sentence preferred** for whispers / fortunes; 2-4 lines for
  tales; one summary line for quests.
- **Specific, not generic.** "That fight was longer than it had
  to be" beats "Nice fight!" by every measure that matters.
- **Lighthearted, never predictive.** Fortunes don't claim to know
  the future. Whispers don't perform. Tales are vague enough to
  belong to anyone's playthrough.

## Pack-specific moments

Personality packs (see [`PACK_SAMPLES.md`](PACK_SAMPLES.md)) can
ship their own per-persona moments by including a
`PackRoot/moments.json` file with the same shape as the global
catalog. The runtime, when it lands, will prefer pack-level
entries over the global catalog when both define the same
trigger. This lets the warrior pack respond differently to
`alpha-defeated` than the trickster pack — same event, different
companion voice.

## Why hand-curated and not LLM-generated

Three reasons the moments are written by hand and not produced
by a model:

1. **Tone consistency.** Hand-written moments don't drift inside
   a session. A model can produce different "alpha defeated"
   replies that read like different characters.
2. **Latency.** A moment fires in milliseconds; a model call is
   hundreds of milliseconds. For trigger-driven UX,
   hand-curation wins on every machine.
3. **Harvestability.** The JSON catalog is freely liftable into
   any other app. A model-driven equivalent would be locked
   behind whoever's running the model.

The LLM still does the heavy lifting on open-ended chat — moments
are the small, sharp tool for the small, sharp situations.

## Cross-references

- [`PACK_SAMPLES.md`](PACK_SAMPLES.md) — the four reference
  personality packs
- [`PACK_AUTHORING.md`](PACK_AUTHORING.md) — pack format deep dive
- [`PROMPT_CARDS.md`](PROMPT_CARDS.md) — the 19 deterministic
  fallback strategies (the "default voice" before any pack overlays)
- [`FALLBACK_AI_RESEARCH.md`](FALLBACK_AI_RESEARCH.md) — design
  rationale for the layered fallback / moment / LLM approach
