# PalLLM Roadmap

Last audited: `2026-05-24`

This roadmap is derived from the live code, tests, Lua bridge, scripts, and
operator docs in this repository. The official score is weighted by
player-facing shipping value and dependency completeness, not raw subsystem
count.

## Audit basis

Verified directly against the current tree and a fresh test run:

- `57` `/api` routes across `src/PalLLM.Sidecar/Program.cs` and
  `src/PalLLM.Sidecar/RouteRegistrations/*.cs`
- `6` operational routes outside `/api`: `/`, `/metrics`, `/health/live`,
  `/health/ready`, `/openapi/v1.json`, `/openapi/v1.yaml`
- `1` separate protocol route: `/mcp`
- `122` feature-catalog entries in
  `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`
- feature status split: `119 ready`, `2 scaffolded`, `1 deferred`
  - scaffolded: `native-hud-attachment`, `production-sampler`
  - deferred: `autopilot-port`
- `19` deterministic fallback strategies in
  `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs`
- `1315` passing NUnit tests from `dotnet test PalLLM.sln`
- first-pass player/operator tooling in `scripts/install-mod.ps1`,
  `scripts/play-palllm.ps1`,
  `scripts/doctor.ps1`, `scripts/run-sidecar-smoke.ps1`,
  `scripts/run-native-proof.ps1`, `scripts/export-release-proof-bundle.ps1`,
  `scripts/verify-release-package.ps1`, `scripts/run-delivery-replay.ps1`,
  `scripts/start-sidecar.ps1`, and `scripts/package-release.ps1`, plus the
  package-root `play.bat` launcher
- current default posture from `src/PalLLM.Sidecar/appsettings.json`:
  inference off, fallback on, vision off, TTS off, ASR off, session on

## Audit result

### Implemented now

- self-contained local-first sidecar runtime and portable adapter surface
- deterministic fallback director, prompt building, memory, relationships,
  reflection, narrative packs, and task routing
- live inference client, tiered model routing, and hardware-aware
  PalLLM model-collaboration planning
- bounded inference warmup worker plus manual warmup endpoint for lower
  first-turn latency after startup or tier changes
- vision describe, structured vision world-state extraction, screenshot watcher,
  and session persistence
- TTS synthesis, ASR transcription, and advisory action-intent planning
- ASP.NET Core operational surface: health, metrics, OpenAPI JSON/YAML,
  dashboard, ProblemDetails, rate limits, response compression, ETags,
  optional API-key auth, and MCP server/client discovery surfaces
- bridge-loop proof on top of runtime health, so request ingress, outbox
  writes, visible delivery, optional speech playback, and action feedback can
  be distinguished instead of being inferred from sidecar uptime alone
- machine-readable bridge proof at `/api/bridge/proof`, so native readiness,
  widget-seam evidence, HUD-bind recommendations, and loop closure can be
  harvested without scraping multiple endpoints
- durable smoke evidence wired into `/api/release/readiness`, so a successful
  sidecar smoke run leaves behind a machine-readable artifact for release
  tooling under `Runtime/ReleaseEvidence`
- durable live native-proof evidence wired into `/api/release/readiness`, so a
  successful Palworld proof run leaves behind a separate machine-readable
  artifact under the same release-evidence root, including watcher timing,
  timeout/poll settings, poll count, completion reason, stable diagnosis
  code/summary fields, and a bounded status-transition trail for replayable
  troubleshooting
- durable proof-bundle evidence wired into `/api/release/readiness`, so a
  successful export pass leaves behind a machine-readable manifest plus zip
  archive that packages the current bridge/readiness/smoke/native-proof
  evidence together, and the release surface verifies that paired archive is
  readable, path-safe, and contains the manifest-listed files before trusting it
  as recorded
- durable support-bundle evidence wired into `/api/release/readiness`, so a
  successful support export leaves behind a machine-readable manifest plus zip
  archive for launch/proof/package/audit handoff under `Runtime/SupportEvidence`,
  with the same archive-shape/path verification before tester handoff
- durable package-verification evidence wired into `/api/release/readiness`, so
  a successful package build or manual verify pass leaves behind a
  machine-readable record that a concrete candidate zip matches its embedded
  `RELEASE_PACKAGE_MANIFEST.json`
- durable artifact-integrity evidence wired into `/api/release/readiness`, so
  a successful checksum pass leaves behind a machine-readable record that
  `SHA256SUMS`, `SHA512SUMS`, and `checksums.json` were generated for the
  candidate package and whether detached signature files were present
- durable full-audit evidence wired into `/api/release/readiness`, so a
  successful `scripts/run_full_audit.ps1` pass leaves behind a
  machine-readable record of build, tests, drift gates, and packaging truth
  instead of burying that state only in repo-local artifacts
- Lua-side bridge capture for chat, base discovery, combat, pal status,
  weather, raids, coarse travel, `ui_probe`, and screenshots
- staged themed card delivery through the outbox consumer, plus guarded action
  previews/results and a native waypoint-label hint path
- install, doctor, smoke, native-proof, proof-bundle export, delivery replay,
  sidecar start, release package, and package-verification scripts
- one obvious packaged player-launch path (`play.bat`) that installs or
  refreshes the mod, starts or reuses a bundled self-contained sidecar by
  default, runs doctor, writes a durable latest-launch snapshot under
  `Runtime/LaunchEvidence`, opens the dashboard, and launches Palworld with
  lower-level packaged fallbacks still intact
- one obvious packaged support-capture path (`support.bat`) that exports a
  durable latest support bundle under `Runtime/SupportEvidence` instead of
  asking players to assemble launch/proof files manually

### Implemented but not ship-complete

- `native-hud-attachment` exists but is default-off and has no confirmed ship
  widget target yet
- `production-sampler` exists but is default-off pending live hook validation
- TTS playback is best-effort local Windows playback, not a native in-world
  audio surface
- `recall_pals` and `request_craft_queue` still degrade to guarded feedback
  events instead of richer native gameplay calls
- sidecar-level smoke and delivery replay are strong, but there is still no
  true Palworld end-to-end smoke harness
- install/package flow is real and now defaults to a bundled self-contained
  sidecar, but clean-machine publication proof is still missing

### Missing for 100%

- operator-confirmed stable HUD/widget seam promoted into ship configuration
- native subtitle, portrait, or HUD surface as the primary in-game renderer
- native in-world audio playback for synthesized speech
- validated and default-on `production` producer, plus richer `travel` detail
- native implementations for the remaining allowlisted actions
- real Palworld smoke evidence plus clean-machine release validation

## Code vs documentation reconciliation

- `README.md`, `docs/ARCHITECTURE.md`, `docs/OPERATIONS.md`, and the live
  feature catalog now agree on the current route count, test count, feature
  count, and scaffolded/deferred split.
- `docs/IMPLEMENTATION_QUEUE.md` was the stale holdout: it still carried the
  older `89.6% -> 100%` framing and a feature-first build order. It is now
  reset to the same `76.2%` baseline used here and reordered to be
  dependency-safe.
- Official source of truth for counts:
  - routes: `src/PalLLM.Sidecar/Program.cs` +
    `src/PalLLM.Sidecar/RouteRegistrations/*.cs`
  - feature status: `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`
  - test count: `dotnet test PalLLM.sln`

## Official completion metric

- systems-completion view (raw subsystem count): about `91%`
- player-experience-weighted view, with scaffolded features discounted and
  verification gaps treated as real blockers: **`76.2%`**

The official roadmap number is now **`76.2%`**.

The previous `89.6%` overstated ship readiness because it gave too much credit
to code paths that exist but are still default-off, unproven on a live Palworld
build, or not yet native to the in-game experience. The remaining gap is not in
the sidecar core; it is concentrated in Palworld-facing native delivery,
native audio, richer native actions, and proof tooling.

## Foundation-first final roadmap

The order below is dependency-safe. It starts with the runtime and proof
foundations, then the bridge truth they feed, then the player-facing native
surfaces that depend on that truth, then final releaseization.

### Phase 1: Core runtime foundation

Weight: `30%`
Status: `100%`
Contribution: `30.0`

Code-backed delivery:

- sidecar boot, DI wiring, config binding, runtime directory creation
- health, dashboard, feature, pack, world, log, relationship, memory, session,
  vision, bridge, TTS, and model-collaboration endpoints
- deterministic fallback, prompt building, memory recall, relationships,
  reflection, and narrative packs
- live inference client, tiered model routing, and per-turn execution profiles
- MCP server, upstream MCP discovery, metrics, auth, retention, and audit gates

Remaining for this phase:

- nothing material; this phase is effectively done

Why it stays first:

- every later phase depends on this contract surface and runtime core already
  being stable

### Phase 2: Compatibility, observability, and test foundation

Weight: `15%`
Status: `72%`
Contribution: `10.8`

Code-backed delivery:

- bridge compat logging in `bridge_boot`
- `ui_probe` diagnostics and ranked candidate summaries
- sidecar smoke script, delivery replay script, doctor script, and full audit
  pipeline
- synthetic bridge-loop proof in the smoke path: request -> outbox ->
  `reply_delivery` -> matching feedback closes `RuntimeHealth.BridgeLoop`
- durable smoke-artifact persistence at `Runtime/ReleaseEvidence/latest-smoke.json`
  plus timestamped history under `Runtime/ReleaseEvidence/History`
- durable native-proof persistence at
  `Runtime/ReleaseEvidence/latest-native-proof.json` plus timestamped history
  under the same release-evidence directory
- durable proof-bundle persistence at
  `Runtime/ReleaseEvidence/latest-proof-bundle.json` plus
  `Runtime/ReleaseEvidence/latest-proof-bundle.zip` and timestamped history
  artifacts under the same release-evidence directory, with readiness-time
  archive-shape/path verification before the proof is treated as recorded
- machine-readable bridge proof snapshot for native readiness, widget
  evidence, and delivery closure
- OpenAPI snapshot verification, route-count drift checks, and test-count drift
  checks
- install/start/package baseline for sidecar + mod flows

Remaining for this phase:

- true in-Palworld smoke coverage instead of sidecar-only and replay-only proof
- current-build evidence for the hook families native delivery depends on
  (`PalBaseCampManager`, `PalMapManager`, `UserWidget`)
- doctor/release checks that prove native prerequisites separately from sidecar
  health

Why it comes before later phases:

- building native surfaces before hook and smoke proof risks rework every time a
  Palworld patch shifts a symbol or widget seam

### Phase 3: World truth and native seam discovery

Weight: `20%`
Status: `68%`
Contribution: `13.6`

Code-backed delivery:

- live bridge capture for chat, base discovery, combat, pal status, weather,
  raid, screenshots, and coarse travel
- runtime support for `snapshot`, `production`, and `travel` events
- `ui_probe` capture plus ranked diagnostics for native HUD targeting
- HUD bind recommendation export into `config/native-hud.lua` plus live
  bridge-side reporting of the active native-hud config source/path
- screenshot watcher as a secondary world-state sensor
- structured projection of latest `travel` and `production` events into the
  runtime snapshot

Remaining for this phase:

- validate the `production` sampler on a live build and promote it to the ship
  path
- deepen travel detail beyond the current coarse movement-sector sampler
- confirm and populate a stable HUD/widget target list from real `ui_probe`
  evidence
- enrich base-operational detail so delivery/actions do not rely on guesswork

Why it comes before delivery and audio:

- native presentation and native actions are only trustworthy when their
  upstream world signals and widget seams are proven first

### Phase 4: Native player delivery and voice

Weight: `20%`
Status: `55%`
Contribution: `11.0`

Code-backed delivery:

- outbox return channel with structured `chat_reply` envelopes
- staged cue-aware themed card delivery with runtime-authored
  `Presentation.Surface`
- family-specific titles, focus/status rails, follow-up ordering, queue
  compaction, and action preview/result cards
- chat-linked TTS synthesis and runtime-authored playback hints
- best-effort local playback fallback when TTS is enabled

Remaining for this phase:

- replace generic screen-message delivery with a true subtitle, portrait, or
  HUD surface
- replace local Windows playback fallback with native in-world audio playback
- execute more authored presentation cues natively instead of only logging them

Why it comes after phases 2 and 3:

- the native surface should only be bound after the seam is proven and the full
  loop can be smoke-tested

### Phase 5: Safe native actions and releaseization

Weight: `15%`
Status: `72%`
Contribution: `10.8`

Code-backed delivery:

- advisory action intents with explicit allowlists
- guarded Lua executor with kill switch, dry-run mode, trace feedback, and
  themed preview/result cards
- native waypoint-label hint path for `waypoint_suggest`
- install, doctor, start, smoke, delivery replay, and package-release baseline
- one-click player launcher (`play.bat` + `scripts/play-palllm.ps1`) that
  collapses install, start, doctor, dashboard, and game launch into one
  packaged release path backed by a bundled self-contained sidecar by default,
  while preserving lower-level scripts for support

Remaining for this phase:

- native `recall_pals` path beyond feedback-only behavior
- native `request_craft_queue` path beyond feedback-only behavior
- clean-machine release walkthrough from zip extraction to verified install
- final player-facing first-run polish and publishable release proof

Why it stays last:

- actions and release proof should land only after the presentation surface,
  bridge truth, and smoke harness are trustworthy

## Weighted total

| Phase | Weight | Status | Contribution |
| --- | --- | --- | --- |
| 1. Core runtime foundation | 30% | 100% | 30.0 |
| 2. Compatibility, observability, and test foundation | 15% | 72% | 10.8 |
| 3. World truth and native seam discovery | 20% | 68% | 13.6 |
| 4. Native player delivery and voice | 20% | 55% | 11.0 |
| 5. Safe native actions and releaseization | 15% | 72% | 10.8 |
| **Total** | | | **76.2%** |

## Dependency-safe implementation order

This is the build order that minimizes avoidable breakage and rework:

1. Compatibility proof plus real Palworld smoke
2. Bridge truth plus seam confirmation
3. Native HUD/subtitle delivery
4. Native audio playback
5. Expand safe native actions
6. Clean-machine release proof and first-run polish

The executable version of that order lives in `docs/IMPLEMENTATION_QUEUE.md`,
with acceptance criteria and proof expectations for each queue.

## Where PalLLM is now on the 100% roadmap

- Current official position: **`76.2 / 100`**
- Phase 1 is complete
- Phase 2 is mostly in place but still lacks real Palworld smoke proof
- Phase 3 is partially complete: the runtime understands more than the live ship
  bridge currently proves
- Phases 4 and 5 are where most of the remaining work lives: native HUD,
  native audio, richer native actions, and final publication proof

In plain terms: the sidecar foundation is done, the bridge is good but not fully
proven, and the final `23.8` points are mostly about making the Lua side feel
native and demonstrably publishable.

## Non-goals for now

- blind port of any external game-automation library's modules
- cloud-first inference posture
- knowledge-graph memory before the in-game delivery layer is finished
- deeply coupled UI logic inside the domain runtime
- broad AGI-style companion expansion before the native Palworld proof chain is finished; post-foundation ideas are tracked separately in [`COMPANION_INTELLIGENCE.md`](COMPANION_INTELLIGENCE.md)
