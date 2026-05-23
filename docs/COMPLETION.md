# Completion status - "are we 100%?"

Last audited: `2026-05-22`

> A single canonical answer to the question that matters: **is the
> program 100% complete?** This doc consolidates the per-phase numbers
> in [`ROADMAP.md`](ROADMAP.md), the build order in
> [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md), the per-aspect
> 10/10 scorecard in [`READINESS.md`](READINESS.md), and the live
> proof state from `/api/bridge/proof` into one queue-by-queue
> board.

For a live, terminal-rendered version of the same board, run
`pwsh ./pal.ps1 complete`. For programmatic consumption, run
`pwsh ./pal.ps1 complete -Json`.

## TL;DR

`76.2%` complete by phase. The remaining `23.8%` is **entirely
live-Palworld + clean-machine work** - no autonomous coding agent
can advance it from the repo. Everything that was within autonomous
reach has been pushed to its ceiling:

- **Sidecar runtime:** `119 / 122` features ready (two are
  `scaffolded` behind kill-switches because they need live build
  validation; one is `deferred` by design - see
  [`adr/0006-opt-in-everything-by-default.md`](adr/0006-opt-in-everything-by-default.md)).
- **Tests:** `1154 / 1154` passing.
- **Drift gates:** `16 / 16` green on every audit.
- **Build warnings:** `0`.
- **Operator surface:** every script that gates a roadmap queue
  is wrapped as a single `pal` verb.
- **Drift protection:** `27` meta-tests + `8` JSON Schemas + `16`
  audit gates pin every cross-document numerical claim.
- **Aggregate readiness:** `~8.0 / 10` across 23 aspects (computed
  arithmetic mean of the per-aspect column in `READINESS.md`).
- **Honest verdict:** the runtime is publication-ready as a
  sidecar; the `23.8%` gap is the in-game story, which only a live
  operator can advance.

## The six queues that gate `100%`

| # | Queue | Pct | Status | Next command for the live operator |
|---|---|---|---|---|
| 1 | Compat proof + real Palworld smoke | `~6.0%` | PENDING | `pal play; pal native-proof` |
| 2 | Bridge truth + seam confirmation | `~4.5%` | PENDING | live session: validate `production-sampler` + travel detail |
| 3 | Native delivery layer V2 | `~7.5%` | PENDING | `pal hud-bind` (after Q1+Q2 capture evidence) |
| 4 | Native speech loop integration | `~2.0%` | PENDING | live session: enable TTS + validate native audio path |
| 5 | Expand guarded native actions | `~3.0%` | PENDING | live session: validate `recall_pals` + `request_craft_queue` natively |
| 6 | Clean-machine release proof | `~1.5%` | PENDING | `pal package; pal verify` (then run on a clean Windows box) |

Each queue's full scope, acceptance criteria, and proof requirements
live in [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md). This
doc tracks the headline state.

## Definition of `100%`

PalLLM is `100%` complete when, per
[`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md):

| Criterion | Gating queue |
|---|---|
| replies render through a real native in-game surface | Q3 |
| chat-linked speech can play natively in-game when enabled | Q4 |
| allowlisted actions execute safely and natively when explicitly enabled | Q5 |
| bridge producers cover the runtime's intended event taxonomy on the current Palworld build | Q2 |
| a real Palworld smoke harness protects the full companion loop | Q1 |
| setup is player-usable from the packaged release flow, not just the repo | Q6 |

All six criteria require **live hardware** (Q1-Q5) or **a clean
Windows machine** (Q6). None can be flipped autonomously.

## Autonomous progress (already at ceiling)

These are the dimensions a coding agent can move from the repo. As
of `2026-05-06`, every one of them is either at `10/10` or at the
ceiling of what's possible without live hardware:

| Dimension | Score | Notes |
|---|---|---|
| Privacy posture | `10/10` | zero outbound traffic by default |
| Security / supply chain | `10/10` | sigstore, SLSA, SBOM, CodeQL, gitleaks |
| Performance (Blackwell + vLLM + NVFP4) | `9.8/10` | sub-second `Chat.Inference` on a 5090 |
| Agent-native discoverability | `9.9/10` | `agents.json` + `pal.json` + AGENT-CARD coverage |
| Diagnose / troubleshoot | `9.7/10` | `pal doctor`, `pal logs`, `pal preflight`, RUNBOOK |
| Install (one-click) | `9.5/10` | `play.bat` atomic install with rollback |
| Uninstall (one-click + manifest) | `9.5/10` | preserves chat history by default |
| Fun / personality | `9.2/10` | 5 ritual catalogs, 19 fallback families |
| First chat (with inference) | `9/10` | nine connectors, `pal connect <target>` |
| MCP integration | `9/10` | 38 tools, 6 resources + 1 template, 4 prompts |
| Documentation | `9/10` | 63 fresh docs, drift-gated, Diataxis-organized |
| Update / re-install | `8.5/10` | `pal check-updates` + `pal news` |
| Customize (personality packs) | `8.5/10` | `pal pack list / copy / new` |
| Configuration UX | `8/10` | `pal config wizard` interactive |
| Discovery (README + pitch) | `8/10` | honest 76.2% disclosure in README |
| Polish (welcome + preflight) | `7.7/10` | `pal welcome` 60-second tour |
| Performance (typical hardware) | `7.5/10` | 1-3s per turn on most PCs |
| First chat (deterministic only) | `7/10` | by-design ceiling - fallback stays predictable |
| Download / extract / signing | `7/10` | SmartScreen unavoidable for unsigned `.bat` |
| Cross-platform mod | `6/10` | mod is Windows-only because Palworld is Windows-only |
| In-game native HUD/audio/action | `5/10` | gated on Q3-Q5 |
| Community / share-ability | `4/10` | needs people |
| Localization | `3/10` | needs translators per locale |

**Aggregate: `~8.0 / 10`** across 23 aspects. The single biggest
drag is "in-game native" at `5/10`, which is the same gating issue
as the queue table above.

## Clean small steps to 100%

The remaining `23.8%` is six concrete steps, each with one
command, one expected artifact, and one verifiable success
condition. Read top-to-bottom. Each step depends on the
previous step's evidence.

### Step 1 - capture live-Palworld smoke proof  *(unlocks ~6.0%)*

```powershell
pwsh ./pal.ps1 play          # boot sidecar + game
pwsh ./pal.ps1 native-proof  # active watcher
```

Expect: the watcher polls `/api/bridge/proof` until the live
session emits `delivery_proven`. Watcher persists
`Runtime/ReleaseEvidence/latest-native-proof.json`.

Verify: `pal proof` reports `overall: PROVEN` when the sidecar is live or the
durable proof is still inside the configured freshness window, and the artifact
has `LiveDeliveryProven: true`. If the only durable proof is older than the
freshness window, `pal proof` reports `overall: STALE PROOF` and
`pal proof -RequireProven` exits non-zero until the live watcher captures a new
session.

### Step 2 - confirm the production sampler  *(unlocks ~4.5%)*

In the running session, walk past a base camp with active
crafting stations. The Lua mod's `production_sampler_enabled`
flag (currently OFF by default) needs validation against the
live `PalBaseCampManager` hook signature on the current build.

Edit `mod/ue4ss/Mods/PalLLM/Scripts/main.lua` to flip the kill
switch on, observe `production` events flowing into
`Bridge/Inbox/`, and confirm they merge into the world snapshot
via `GET /api/world`.

Verify: `pal proof -Json | ConvertFrom-Json` shows
`BridgeProofStatus: ready_for_hud_bind` and the snapshot's
`Bases[].Production[]` is non-empty.

### Step 3 - bind the native HUD  *(unlocks ~7.5%)*

```powershell
pwsh ./pal.ps1 hud-bind   # writes config/native-hud.lua
```

Expect: the ranked HUD bind recommendation from `ui_probe`
becomes the installed mod's render target. A subsequent
`pal native-proof` run shows `VisibleDeliveryConfirmed: true`.

Verify: in-game, send a chat message via the dashboard or
`pal hello`. The reply renders through the bound widget, not the
generic `ClientMessage` chat surface.

### Step 4 - wire native audio  *(unlocks ~2.0%)*

In `appsettings.json`, set `PalLLM:Tts:Enabled = true` and pick
a configured TTS engine. Replace the local Windows playback
fallback with a UE4SS-side `Play2DSound` (or equivalent native
audio call) in `main.lua`'s outbox consumer. The current bridge
already emits a content-free `speech_playback` receipt after local
helper attempts, including low-latency native-mixer queue and buffer-duration
estimates plus stale-speech supersession, prior-buffer-overlap, and
cancellation-mode receipts, so this
queue should preserve that receipt while swapping in the real in-world playback
primitive.

Verify: a chat turn emits a TTS-flagged outbox envelope, the
mod renders text + plays audio in-world, and the
`Speech.PlaybackHint` plus `/api/bridge/proof` `speech_playback`
lane round-trip correctly.

### Step 5 - promote remaining native actions  *(unlocks ~3.0%)*

Two allowlisted action types still degrade to feedback-only
paths: `recall_pals` and `request_craft_queue`. Each needs a
native UE4SS implementation in `main.lua`'s
`execute_*` family beside `execute_waypoint_suggest`. Preserve
the existing kill switches and dry-run mode.

Verify: with automation enabled and the type in the allowlist,
the action executes natively in the game and emits a
`feedback` event. With automation disabled, nothing executes.

### Step 6 - ship the clean-machine release  *(unlocks ~1.5%)*

```powershell
pwsh ./pal.ps1 package         # build the release zip
pwsh ./pal.ps1 verify          # verify the zip's manifest + hashes
pwsh ./pal.ps1 proof-bundle    # bundle the evidence

# On a clean Windows machine without dev artifacts:
#   1. Extract the zip
#   2. Double-click play.bat
#   3. Confirm the dashboard opens at http://localhost:5088
#   4. Confirm the doctor reports no errors
```

Verify: the clean-machine walkthrough reaches a working
companion + dashboard + native HUD without any reference to the
source repo.

### After step 6

Headline rolls `76.2 -> 100`. `pal complete` reports every
queue as `PROVEN`. The release tag can ship.

Each step has a `pal` verb. Each verb has a meta-test pinning it
to its script. The operator never has to remember script paths.

For a coding agent picking up the repo:

- The autonomous portion of completion is **done**.
- New work belongs in [`COMPANION_INTELLIGENCE.md`](COMPANION_INTELLIGENCE.md)
  (post-foundation ideas) or [`FUTURE_2035.md`](FUTURE_2035.md)
  (cutting-edge / 2030+ ideas).
- Anything that would advance the headline 76.2% requires live
  hardware. Don't pretend to advance it from the repo.

## How to read the live verb

```text
$ pwsh ./pal.ps1 complete
PalLLM completion status
  honest roadmap   : 76.2%
  remaining        : 23.8%  (live-Palworld + clean-machine work)
test count       : 1154 / 1154
  drift gates      : 16 / 16
  readiness        : ~8.0 / 10 across 23 aspects

Queue-by-queue status:
  Q1 Compat proof + real Palworld smoke    PENDING (~6.0%)
       -> pal play  # boot sidecar + Palworld so the watcher has a session to observe
  Q2 Bridge truth + seam confirmation       PENDING (~4.5%)
       -> live session: validate production-sampler + travel detail against current Palworld build
  ...

Next command to advance the topmost PENDING queue:
  pal play  # boot sidecar + Palworld so the watcher has a session to observe
```

When Queue 1 captures `delivery_proven`, the verb auto-promotes
`Q1` from `PENDING` -> `PARTIAL` -> `PROVEN` based on the live
`/api/bridge/proof` response. Subsequent queues stay `PENDING`
until their own evidence lands.

## Related

- [`ROADMAP.md`](ROADMAP.md) - the official 76.2% scoring with
  per-phase breakdown
- [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md) - the
  build order to close the remaining 23.8% (Q1-Q6 source of truth)
- [`READINESS.md`](READINESS.md) - the candid 23-aspect 10/10
  scorecard
- [`HANDOFF.md`](HANDOFF.md) - current audited state +
  "what just landed" pass log
- [`PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) - machine-readable
  rolling state
- [`FUTURE_2035.md`](FUTURE_2035.md) - post-100% cutting-edge ideas


