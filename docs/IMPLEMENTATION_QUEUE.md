# PalLLM Implementation Queue

Last audited: `2026-05-22`

This doc converts the remaining roadmap gap into an executable build queue.
`ROADMAP.md` explains what is implemented vs missing; this doc explains what to
build next, in what order, and how to prove each step.

If you are taking over the repo from an interrupted coding session, start with
[`HANDOFF.md`](HANDOFF.md) first, then come back here for the concrete build
order.

Post-foundation companion-intelligence ideas such as visible memory, replay
traces, advisory world-model planning, and confidence-calibrated escalation are
tracked separately in COMPANION_INTELLIGENCE (retired Pass 418).
They are intentionally **not** part of the ship-critical queue below until the
live Palworld proof and native delivery path are fully locked.

Current status: `76.2%` -> `100%` (remaining `23.8%`).

The per-queue "Roadmap value unlocked" percentages below are
deliberately rough estimates (each prefixed `about` / `~`) and sum
to roughly `~24%`, not exactly `23.8%`. The milestone totals are
the load-bearing figures; treat each queue's individual value as
an effort hint, not an accounting line. The canonical baseline of
`76.2%` is pinned in `docs/PROJECT_NUMBERS.json`.

The queue is intentionally ordered to reduce rework:

1. prove compatibility and smoke first
2. deepen the bridge truth and confirm native seams
3. bind the native player-facing surface
4. add native audio on top of that surface
5. expand native actions only after the loop is observable
6. finish with clean-machine release proof

## Queue 1: Compatibility proof + real Palworld smoke foundation

Goal:

- prove the live Palworld/UE4SS hook set, the render seam, and the full loop
  before building more native features on top

Current baseline:

- `bridge_boot` already reports compat hints
- `ui_probe` already captures and ranks candidate `UserWidget` surfaces
- `scripts/run-sidecar-smoke.ps1` and `scripts/run-delivery-replay.ps1`
  already prove the sidecar contract
- `RuntimeHealth.BridgeLoop`, `scripts/doctor.ps1`, and the smoke script now
  prove the synthetic request -> outbox -> delivery -> feedback loop without a
  live Palworld session
- `scripts/run-native-proof.ps1` can now watch `/api/bridge/proof`, persist a
  durable native-proof artifact, separate "real Palworld HUD delivery was
  proven" from synthetic smoke evidence, and fail fast with a blocked artifact
  when a local proof run starts before Palworld is actually running; the
  artifact now includes watcher timing, poll count, timeout state, completion
  reason, and a bounded status-transition trail for failed and successful runs
- `scripts/export-release-proof-bundle.ps1` can now package the current
  release/readiness snapshot, bridge proof, smoke artifact, native-proof
  artifact, and HUD config into one durable validation bundle
- what is still missing is a real in-Palworld scripted pass

Why first:

- every remaining queue depends on the current build's hooks and widget seams
  being real, not assumed

Scope:

- add a reproducible Palworld smoke path that validates bridge ingest, chat,
  visible in-game output, and feedback ingestion
- capture current-build evidence for the hook families the remaining work
  depends on (`PalBaseCampManager`, `PalMapManager`, `UserWidget`)
- teach `doctor.ps1` to distinguish sidecar health from Palworld-native
  readiness and delivery proof where practical
- persist a release-friendly native-proof artifact whenever a live Palworld
  session actually reaches `delivery_proven`
- persist a release-friendly proof bundle whenever the current smoke/native
  proof set is ready to travel with a candidate package

Suggested code areas:

- `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`
- `scripts/doctor.ps1`
- `scripts/run-sidecar-smoke.ps1`
- `scripts/run-native-proof.ps1`
- `scripts/export-release-proof-bundle.ps1`
- `tests/PalLLM.Tests/`

Acceptance criteria:

- one scripted pass validates event in -> chat reply out -> visible in-game
  delivery -> feedback back into the bridge
- one operator-facing proof artifact exists for the current Palworld build's
  required hooks and seams
- native-delivery prerequisites are observable without source diving

Proof:

- captured smoke log or transcript from a live Palworld session
- compatibility report or doctor output that calls out ready vs missing native
  prerequisites

Roadmap value unlocked:

- about `6.0%`

## Queue 2: Bridge truth + seam confirmation

Goal:

- turn the runtime's broader event and seam model into live, verified truth

Current baseline:

- runtime already supports `production` and `travel`
- Lua already emits coarse `travel` and diagnostic `ui_probe`
- the ranked HUD recommendation can now be exported into
  `config/native-hud.lua`, and `bridge_boot` reports the live config
  source/path back to the sidecar
- `production-sampler` already exists but is default-off pending validation

Why second:

- native HUD, native audio, and native actions should consume proven signals,
  not guessed ones

Scope:

- validate the production sampler against the current Palworld build and either
  promote it or document the exact blocker
- deepen travel detail beyond the current coarse sector sampler
- confirm and populate the initial ship-worthy HUD/widget target shortlist from
  `ui_probe` evidence

Suggested code areas:

- `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`
- `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`
- `tests/PalLLM.Tests/RuntimeTests.cs`

Acceptance criteria:

- live `production` events reach the runtime and affect snapshot state on a
  validated build, or the blocking hook drift is explicitly documented
- travel detail is more useful than "player moved sectors"
- the native HUD bind has at least one confirmed target candidate from real
  probe evidence

Proof:

- live session logs showing `production` and richer `travel` events
- documented widget target shortlist tied to `ui_probe` evidence

Roadmap value unlocked:

- about `4.5%`

## Queue 3: Native delivery layer V2

Goal:

- replace generic screen-message delivery with a real native-feeling subtitle or
  HUD surface

Current baseline:

- the sidecar already emits a rich `Presentation.Surface`
- Lua already renders staged family-themed cards
- a UMG attachment scaffold already exists behind a kill switch
- operators can now apply the top-ranked HUD target without editing `main.lua`
  by writing `config/native-hud.lua`

Why third:

- this is the largest remaining player-facing gap, but it should only be bound
  after Queue 1 and Queue 2 prove the seam

Scope:

- make the native HUD/subtitle path the intended primary render path
- consume the existing cue plan more natively than the current generic fallback
- preserve the current fallback path as an explicit recovery mode

Suggested code areas:

- `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`
- optionally `src/PalLLM.Domain/Integration/Contracts.cs`

Acceptance criteria:

- a `chat_reply` envelope renders through a native-feeling in-game surface
  instead of relying on `ClientMessage` as the default path
- at least five strategy families produce visibly distinct presentation
- deterministic fallback replies render through the same native path
- fallback logging and fallback render path remain available if binding fails

Proof:

- screenshots or video for stealth, triage, camp, travel, and base-network
  responses
- replay or regression coverage for the outbox contract used by the renderer

Roadmap value unlocked:

- about `7.5%`

## Queue 4: Native speech loop integration

Goal:

- replace best-effort local playback with a native in-world audio path

Current baseline:

- `Speech` artifacts already exist on normal chat replies
- runtime already emits playback hints
- Lua can already attempt best-effort local wave/common-media playback

Why fourth:

- native audio should follow the native render surface and the proven smoke loop

Scope:

- preserve the current `Speech` artifact contract
- add a native-feeling game-side playback path
- keep playback optional and failure-tolerant

Suggested code areas:

- `src/PalLLM.Domain/Integration/Contracts.cs`
- `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`
- `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`

Acceptance criteria:

- a normal chat reply can optionally produce playable in-game audio
- playback failure never blocks text rendering
- correlation between text, audio, and request remains visible

Proof:

- one end-to-end pass with TTS enabled
- one failure-mode pass where TTS is unavailable and text still renders cleanly

Roadmap value unlocked:

- about `2.0%`

## Queue 5: Expand guarded native actions

Goal:

- expand the guarded executor from feedback-only behavior into richer native,
  reversible Palworld actions

Current baseline:

- advisory action intents already exist
- guarded executor, dry-run mode, and allowlists already exist
- `waypoint_suggest` already attempts a native waypoint-label hint
- `recall_pals` and `request_craft_queue` still degrade to feedback-only paths

Why fifth:

- action execution should only expand after the render loop and smoke harness
  can show exactly what happened

Scope:

- preserve allowlists, kill switches, and dry-run mode
- add native gameplay paths for the remaining allowlisted actions where safe
- keep explicit fallback feedback when a native call is unavailable

Suggested code areas:

- `src/PalLLM.Domain/Runtime/ActionIntentPlanner.cs`
- `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`

Acceptance criteria:

- when automation is off, nothing executes
- when a type is not allowlisted, nothing executes
- when an allowlisted type is enabled, it runs natively or degrades visibly and
  safely
- at least one remaining action path (`recall_pals` or `request_craft_queue`)
  advances beyond feedback-only behavior

Proof:

- replay fixtures for all allowlisted action types
- one explicit negative test for blocked types
- one live proof of a native action path beyond waypoint hinting

Roadmap value unlocked:

- about `3.0%`

## Queue 6: Clean-machine release proof

Goal:

- turn the current developer-usable flow into a publishable player release flow

Current baseline:

- install, doctor, smoke, native-proof, proof-bundle export, start-sidecar,
  package-release, and verify-release-package scripts already exist
- `play.bat` plus `scripts/play-palllm.ps1` now provide one primary packaged
  install/start/doctor/dashboard/game-launch path, and release packaging now
  bundles a self-contained sidecar by default for that path
- release zips now carry `RELEASE_PACKAGE_MANIFEST.json`, and
  `/api/release/readiness` can expose durable package-verification,
  artifact-integrity, and full-audit artifacts
- what is still missing is proof on a clean machine or clean user profile

Why last:

- release proof should validate the completed native delivery/audio/action stack,
  not an intermediate state

Scope:

- validate the release zip flow end-to-end on a clean machine or clean profile
- tighten release-facing instructions and diagnostics
- keep package verification durable and machine-readable, not just console-only
- reduce repo-only assumptions that still leak into packaged usage

Suggested code areas:

- `scripts/`
- `README.md`
- `docs/RELEASE.md`

Acceptance criteria:

- a clean machine or clean user profile can install, verify, and use the mod
  from the packaged release flow
- the packaged release keeps `play.bat` as the obvious primary entry script for
  the install/verify/start path, ships a bundled self-contained sidecar by
  default, and still leaves the lower-level scripts available
  for debugging and support
- doctor success and failure outputs are documented with actionable next steps
- the release flow no longer depends on repo knowledge

Proof:

- one clean-machine or clean-profile walkthrough from zip extraction to verified
  install
- captured doctor output for success and one failure scenario
- packaged release walkthrough including native delivery verification

Roadmap value unlocked:

- about `1.5%`

## Recommended build order

1. Queue 1: Compatibility proof + real Palworld smoke foundation
2. Queue 2: Bridge truth + seam confirmation
3. Queue 3: Native delivery layer V2
4. Queue 4: Native speech loop integration
5. Queue 5: Expand guarded native actions
6. Queue 6: Clean-machine release proof

## Suggested milestone grouping

### Milestone A: Prove the foundation

- Queue 1
- Queue 2

Expected roadmap movement:

- `76.2% -> about 86.0%`

### Milestone B: Make it feel native

- Queue 3
- Queue 4

Expected roadmap movement:

- `about 86.0% -> about 95.5%`

### Milestone C: Make it safely publishable

- Queue 5
- Queue 6

Expected roadmap movement:

- `about 95.5% -> 100%`

## Definition of 100%

For PalLLM, `100%` means:

- replies render through a real native in-game surface
- chat-linked speech can play natively in-game when enabled
- allowlisted actions execute safely and natively when explicitly enabled
- bridge producers cover the runtime's intended event taxonomy on the current
  Palworld build
- a real Palworld smoke harness protects the full companion loop
- setup is player-usable and proven from the packaged release flow, not just the
  repo
