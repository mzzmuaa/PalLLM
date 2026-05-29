# Completion status - "are we 100%?"

Last audited: `2026-05-29`

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

> **For a small coding agent picking up the repo:** the autonomous
> portion of completion is **done**. The remaining `23.8%` requires
> live Palworld + a clean Windows machine — see the
> [`Clean small steps to 100%`](#clean-small-steps-to-100) section
> below for the six atomic checkboxes that close the gap. Do **not**
> try to advance these from the repo alone; each one needs in-game
> evidence the live operator must capture.

## TL;DR

`76.2%` complete by phase. The remaining `23.8%` is **entirely
live-Palworld + clean-machine work** - no autonomous coding agent
can advance it from the repo. Everything that was within autonomous
reach has been pushed to its ceiling:

- **Sidecar runtime:** `119 / 122` features ready (two are
  `scaffolded` behind kill-switches because they need live build
  validation; one is `deferred` by design - see
  [`adr/0006-opt-in-everything-by-default.md`](adr/0006-opt-in-everything-by-default.md)).
- **Tests:** `1315 / 1315` passing.
- **Drift gates:** `16 / 16` green on every audit.
- **Build warnings:** `0`.
- **Operator surface:** every script that gates a roadmap queue
  is wrapped as a single `pal` verb.
- **Drift protection:** `28` meta-tests + `8` JSON Schemas + `16`
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

The remaining `23.8%` is **six atomic checkboxes**. Each has one
command, one expected artifact, one verifiable success condition,
one explicit "DONE WHEN" criterion, and one "FAILED IF" criterion
that catches the most common confusion. Read top-to-bottom — each
step depends on the previous step's captured evidence.

> ⚠️ **Live hardware required for all six.** A coding agent picking
> up the repo cannot move these from the repo alone. Treat the
> headline `76.2%` as the **autonomous ceiling** and do not edit
> code, docs, or counts trying to advance it without live evidence
> attached.

### ☐ Step 1 — capture live-Palworld smoke proof  *(unlocks ~6.0%)*

**Pre-req:** Palworld is installed on a Windows machine and you have
the bundled sidecar release extracted.

**Command:**

```powershell
pwsh ./pal.ps1 play          # boot sidecar + Palworld + dashboard
pwsh ./pal.ps1 native-proof  # active watcher polls /api/bridge/proof
```

**Expected artifact:** `Runtime/ReleaseEvidence/latest-native-proof.json`
written by the watcher. Contains `LiveDeliveryProven: true` once the
in-game bridge has emitted a `delivery_proven` event.

**DONE WHEN:** `pal proof` reports `overall: PROVEN` AND the
artifact's freshness window (default 7 days) hasn't expired.

**FAILED IF:** `pal proof` reports `overall: STALE PROOF` (proof exists
but is older than the freshness window — re-run the watcher) OR `pal
proof -RequireProven` exits non-zero (no proof at all).

---

### ☐ Step 2 — confirm the production sampler  *(unlocks ~4.5%)*

**Pre-req:** Step 1 has captured a live session.

**Command:** In a running game session, walk past a base camp with
active crafting stations.

The Lua mod's `production_sampler_enabled` flag is OFF by default
because it needs validation against the live `PalBaseCampManager`
hook signature on the current Palworld build.

```lua
-- mod/ue4ss/Mods/PalLLM/Scripts/main.lua
-- flip the kill switch on:
production_sampler_enabled = true
```

Observe `production` events flowing into `Bridge/Inbox/`. Confirm
they merge into the world snapshot via `GET /api/world`.

**DONE WHEN:** `pal proof -Json | ConvertFrom-Json` shows
`BridgeProofStatus: ready_for_hud_bind` AND the snapshot's
`Bases[].Production[]` is non-empty.

**FAILED IF:** the snapshot's `Bases[].Production[]` stays empty
even with the flag on (hook signature drift — file an issue
against the current Palworld build).

---

### ☐ Step 3 — bind the native HUD  *(unlocks ~7.5%)*

**Pre-req:** Step 2 confirmed `ready_for_hud_bind`.

**Command:**

```powershell
pwsh ./pal.ps1 hud-bind   # writes config/native-hud.lua
```

The verb writes the ranked HUD-bind recommendation from `ui_probe`
into `config/native-hud.lua`, which the installed mod loads as its
render target.

**Verify:** in-game, send a chat message via the dashboard or
`pal hello`. The reply renders through the bound widget, NOT the
generic `ClientMessage` chat surface.

**DONE WHEN:** a subsequent `pal native-proof` run shows
`VisibleDeliveryConfirmed: true` in the persisted artifact.

**FAILED IF:** the reply still appears in the chat box, not the
HUD widget (the bind picked the wrong widget — re-run `ui_probe`,
inspect candidates with `pal proof -Json`, pick a different
recommendation and re-bind).

---

### ☐ Step 4 — wire native audio  *(unlocks ~2.0%)*

**Pre-req:** Step 3 confirmed native HUD delivery works.

**Configure:** in `appsettings.json`, set `PalLLM:Tts:Enabled = true`
and pick a configured TTS engine (Piper local is the safe default;
see `docs/MULTIMODAL_RECIPES.md`).

**Implement:** replace the local Windows playback fallback with a
UE4SS-side `Play2DSound` (or equivalent native audio call) in
`main.lua`'s outbox consumer. Preserve the existing content-free
`speech_playback` receipt — it includes low-latency native-mixer
queue + buffer-duration estimates + stale-speech supersession +
prior-buffer-overlap + cancellation-mode receipts. The current
bridge already emits these; this step swaps the playback primitive
underneath, not the proof surface above.

**DONE WHEN:** a chat turn emits a TTS-flagged outbox envelope AND
the mod renders text + plays audio in-world AND the
`Speech.PlaybackHint` + `/api/bridge/proof` `speech_playback` lane
round-trip correctly.

**FAILED IF:** audio plays via local Windows speakers instead of
in-world (the fallback didn't get replaced — check the outbox
consumer dispatch table).

---

### ☐ Step 5 — promote remaining native actions  *(unlocks ~3.0%)*

**Scope:** two allowlisted action types still degrade to
feedback-only paths instead of native execution:

- `recall_pals`
- `request_craft_queue`

Each needs a native UE4SS implementation in `main.lua`'s
`execute_*` family beside the existing `execute_waypoint_suggest`.
Preserve the existing kill switches (`actions_enabled`,
`actions_dry_run`) and the per-type allowlist.

**DONE WHEN:** with automation enabled AND the type in the
allowlist, the action executes natively in the game AND emits a
`feedback` event. With automation disabled, nothing executes.

**FAILED IF:** the action executes even when automation is
disabled (kill switch broken — STOP and revert).

---

### ☐ Step 6 — ship the clean-machine release  *(unlocks ~1.5%)*

**Pre-req:** Steps 1-5 have all captured proven evidence.

**Build:**

```powershell
pwsh ./pal.ps1 package        # build the release zip
pwsh ./pal.ps1 verify         # verify the zip's manifest + hashes
pwsh ./pal.ps1 proof-bundle   # bundle every evidence artifact
```

**Validate on a clean Windows machine** (no dev artifacts, no
source repo, no installed dotnet SDK):

1. Extract the zip to any folder.
2. Double-click `play.bat`.
3. Confirm the dashboard opens at `http://localhost:5088`.
4. Confirm `pal doctor` reports no errors.
5. Send a chat message — confirm reply renders through the native HUD.

**DONE WHEN:** the clean-machine walkthrough reaches a working
companion + dashboard + native HUD without any reference to the
source repo.

**FAILED IF:** any step requires manual SDK install, manual
appsettings edit, or other dev-tooling assumption (the packaged
flow has a gap — add the missing piece to `play.bat`).

---

### After step 6

Headline rolls `76.2 -> 100`. `pal complete` reports every queue
as `PROVEN`. The release tag can ship.

Each step has a `pal` verb. Each verb has a meta-test pinning it
to its script. The operator never has to remember script paths.

### Reminder for coding agents

- The autonomous portion of completion is **done**.
- New post-foundation ideas belong in
  [`ROADMAP.md`](ROADMAP.md) → "Post-100 surfaces" OR
  [`FUTURE_2035.md`](FUTURE_2035.md) (cutting-edge / 2030+).
- Anything that would advance the headline `76.2%` requires
  live hardware. **Do not** edit the `honestRoadmap` field in
  `PROJECT_NUMBERS.json` without an attached live-evidence
  artifact under `Runtime/ReleaseEvidence/`.

## How to read the live verb

```text
$ pwsh ./pal.ps1 complete
PalLLM completion status
  honest roadmap   : 76.2%
  remaining        : 23.8%  (live-Palworld + clean-machine work)
test count       : 1315 / 1315
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

