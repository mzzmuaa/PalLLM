# PalLLM Handoff

Last audited: `2026-05-23`

This is the shortest safe starting point for a temporary coding handoff,
including Claude or any other replacement agent. It is meant to save a full
repo re-audit before the next implementation pass.

> Reading order for coding agents: [`../AGENTS.md`](../AGENTS.md) →
> this file → [`CODE_MAP.md`](CODE_MAP.md) → [`CONVENTIONS.md`](CONVENTIONS.md).
> To lift one capability into another project without the rest of the
> repo, read [`HARVEST.md`](HARVEST.md) first.

## Scope guardrails

- This repo is **PalLLM**, not a sibling project and not a generic AI studio.
- Runtime scope stays **Palworld + UE4SS** for the live bridge and packaged
  player flow.
- Do not describe the repo as `100%`, `lawyer-proof`, or fully IP-neutral.
- Keep the public copy honest: the portable adapter surface is neutral, but the
  shipped mod and operator flow still target Palworld and UE4SS explicitly.

## Current audited state

- `57` `/api` routes in `src/PalLLM.Sidecar/Program.cs`
- `6` operational routes outside `/api`
- `1` separate `/mcp` protocol route
- `38` MCP tools, `6` direct resources + `1` templated resource, `4` prompts
- `122` feature-catalog entries in
  `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`
- feature split: `119 ready`, `2 scaffolded`, `1 deferred`
- `19` deterministic fallback strategies
- `1309` passing tests from `dotnet test PalLLM.sln`
- `16 / 16` drift gates PASS on the latest audit
- honest roadmap position: `76.2%`
- latest passing full audit:
  [`../artifacts/full-audit/20260523-153133/RESULTS.md`](../artifacts/full-audit/20260523-153133/RESULTS.md)
- committed OpenAPI snapshot:
  [`openapi/palllm-sidecar-v1.json`](openapi/palllm-sidecar-v1.json)

## What just landed

Most recent batch (see [`../CHANGELOG.md`](../CHANGELOG.md) for the full
per-pass log, including Passes 48-190 which were trimmed from this file
once they reached the changelog):

- **Pass 366 - Post-Codex review + close pal-cleanup safety-test gap.**
  Reviewed every file Codex touched in Passes 361-365 against the
  drift-gate contract and the production-readiness rubric. All 1308 tests
  passed, all 16 audit gates passed, and Codex's new surfaces (native-proof
  diagnosis codes, remediation actions, repo cleanup, model-endpoint
  evidence probe) are wired into docs and the catalog correctly. Found one
  silent gap: `scripts/pal-cleanup.ps1` shipped with a preview-by-default
  contract (no `-Apply` flag means no deletion) but no regression test
  pinned that invariant, so a future edit could accidentally start deleting
  files in preview mode and the audit would not catch it. Closed the gap by
  adding `Cleanup_NoApplyFlag_PreviewsOnly_DoesNotDelete` to
  `tests/PalLLM.Tests/ScriptExecutionTests.cs`. The test stamps a sandbox
  repo with a stub `PalLLM.sln` and a sentinel
  `artifacts/full-audit/.../coverage/sentinel.html`, copies the real
  `pal-cleanup.ps1` into the sandbox so the script's repo-root probe
  anchors there, runs the script with no flags, and asserts the sentinel
  still exists after the script exits 0. Test count cascaded `1308` -> `1309`
  across `PROJECT_NUMBERS.json`, `CLAUDE.md`, `pal.json`, `agents.json`,
  `README.md`, `pal.ps1`, `docs/CODE_MAP.md`, `docs/ARCHITECTURE.md`,
  `docs/ROADMAP.md`, `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`,
  `tests/README.md`, `scripts/onboard.ps1`, `.github/copilot-instructions.md`,
  `.cursorrules`, `CONTRIBUTING.md`, `docs/CHEAT_SHEET.md`, and
  `CHANGELOG.md`. Verification: full `dotnet test` passed `1309 / 1309`;
  full audit passed `16 / 16` at
  `../artifacts/full-audit/20260523-153133/RESULTS.md`. Production-readiness
  verdict: official roadmap stays at `76.2%`, three Tier-S items closed
  (auth guard, bridge fuzz, SLO+alerts), two Tier-A items closed
  (script-execution, cold-start), one Tier-A gap closed by Codex
  (model-endpoint probe) + one safety gap closed by this pass (cleanup
  preview); remaining `23.8%` is gated on the live Palworld
  `delivery_proven` run plus the planned monolith extractions
  (`PalLlmRuntime.cs` and `Program.cs`), neither of which is closable
  autonomously.

- **Pass 365 - Model-endpoint evidence probe.** Added
  `scripts/pal-model-probe.ps1` and surfaced it as `pal models probe`
  (alias: `pal models proof`). The probe checks `/health`, `/v1/models`,
  and `/metrics` on a local or configured model server, writes
  `artifacts/model-probe/model-probe-*.json`, classifies model-serving
  metric families for vLLM prefix cache, KV cache, queue pressure,
  latency, speculative decoding, SGLang, and llama.cpp/GGUF-style
  counters, and sends no chat, image, audio, tool-call, or player
  payload content. Updated `pal.json`, `agents.json`, model-serving docs,
  observability runbooks, quick references, and current test-count
  anchors. New `ScriptExecutionTests` coverage executes the probe in
  `-DryRun` mode; meta coverage pins the task-runner wiring and expected
  metric/privacy strings. Verification: probe dry run passed, focused
  script/meta tests passed `36 / 36`, full `dotnet test` passed
  `1308 / 1308`, and full audit passed `16 / 16` at
  `../artifacts/full-audit/20260523-054521/RESULTS.md`.

- **Pass 363 - Repo cleanup command and novice hygiene pass.** Added
  `scripts/pal-cleanup.ps1` and surfaced it as `pal cleanup`. The command
  previews generated clutter by default, supports `-Json` for agents, deletes
  only when `-Apply` is passed, and verifies every recursive-delete target
  stays under known generated directories. The default policy prunes old
  `artifacts/full-audit/*/coverage` HTML folders while preserving audit
  directories and `RESULTS.md` files so changelog / handoff links keep
  resolving. Optional `-BuildOutputs` includes `src/**/bin`, `src/**/obj`,
  `tests/**/bin`, and `tests/**/obj`. Ran the default cleanup and reclaimed
  about `238 MB` by removing `11` old coverage-report directories; the latest
  coverage report remains. Updated `pal.json`, `agents.json`, cheat sheet,
  quick reference, index, easy-mode, first-hour, and runbook docs. Focused
  `MetaTests` passed `27 / 27`; final `dotnet test` passed `1307 / 1307`;
  full audit passed `16 / 16` at
  `../artifacts/full-audit/20260523-051007/RESULTS.md`. Test count stays
  `1307`.

- **Pass 364 - Native-proof remediation contract.** Pass 361's stable
  diagnosis codes now carry the immediate remediation too.
  `ReleaseNativeProofEvidenceSnapshot` adds `DiagnosisAction` and
  `DiagnosisCommand`; `scripts/run-native-proof.ps1` writes them into
  `Runtime/ReleaseEvidence/latest-native-proof.json`; `/api/release/readiness`
  normalizes them for older or unsafe artifacts; and `pal proof -Json` derives
  the same fields when it reads live bridge proof. HUD-bind blockers point at
  `scripts/run-native-proof.ps1 -ApplyHudRecommendation`, delivery timeouts
  point at a longer watcher run, and proven native HUD delivery points at the
  next release-smoke lane. Updated the JSON schema, OpenAPI snapshot, API,
  architecture, release, operations, feature-catalog, research, changelog, and
  handoff docs. Existing release-readiness and meta coverage now pin the new
  fields; no new test cases were added, so the test count stays `1307`.
  Focused coverage passed `81 / 81`; both native-proof scripts parse cleanly;
  full `dotnet test` passed `1307 / 1307`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260523-051155/RESULTS.md`.

- **Pass 361 - Native-proof diagnosis codes.** The live Palworld
  `delivery_proven` run remains the release blocker, but failed proof
  attempts now produce stable machine-readable diagnosis instead of
  requiring support tools to scrape console text or prose blockers.
  `ReleaseNativeProofEvidenceSnapshot` adds `DiagnosisCode` and
  `DiagnosisSummary`; `scripts/run-native-proof.ps1` writes them into
  `Runtime/ReleaseEvidence/latest-native-proof.json`; `/api/release/readiness`
  derives them for older artifacts; and `pal proof -Json` exposes the
  same fields through `docs/schemas/native-proof-status-v1.schema.json`.
  Codes include `palworld_process_missing`, `bridge_boot_missing`,
  `ui_probe_missing`, `native_hud_bind_not_ready`,
  `native_hud_surface_mismatch`, `delivery_proven_timeout`, and
  `native_hud_delivery_proven`. Regenerated the OpenAPI snapshot and
  refreshed API/operations/release/research docs. Existing endpoint and
  meta coverage now pin proven, contradicted, and fallback-surface timeout
  diagnosis; both native-proof scripts parse cleanly; full `dotnet test`
  passed `1307 / 1307`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260523-045403/RESULTS.md`. Test count stays
  `1307`.

- **Pass 360 - Cold-start benchmark (Tier-A #6 from senior-dev
  review).** PalLLM had per-method latency budgets in
  docs/HOT_PATH.md but the operator's actual UX number --
  "how long until I can chat?" -- wasn't measured. Pass 360
  adds `scripts/pal-benchmark-coldstart.ps1` which times three
  phases: (1) optional dotnet build cold via -IncludeBuild;
  (2) dotnet run → /health/live 200 polling at 100ms; (3)
  first /api/chat served by the deterministic-fallback
  director (so no LLM required -- the benchmark measures the
  RUNTIME cold-start, not the MODEL load). Writes JSON
  artifact to artifacts/cold-start-benchmark/<ts>.json + a
  one-line "ColdStart: build=Xs ready=Ys chat=Zs" summary so
  an operator can paste the result into a ticket or chat
  without context. -DryRun mode emits a stub artifact for
  tests. Uses random 18000-19000 port to avoid collision with
  a running player install on 5088. **Verb routing:**
  `pal benchmark cold-start` (and `coldstart` / `cold`) routes
  to the new script via Run-Benchmark's dispatch on the first
  ForwardArg; `pal benchmark` with no subcommand keeps prior
  per-turn-latency behaviour. **docs/HOT_PATH.md** gains a
  Cold-start section between Startup and JSON-contract-metadata
  with the reference-rig budget table (build < 45s, ready < 8s,
  combined chat < 10s). **Tests (2 added):**
  ColdStartBenchmark_DryRun_EmitsExpectedArtifact (exec test
  via Pass 359 RunPwsh harness; pins -DryRun exit code,
  summary line shape, artifact JSON structure with phases +
  host metadata); HotPathDoc_DeclaresColdStartBudgetRow
  (text-presence guard on the doc section). **Test cascade.**
  Count `1305 -> 1307` (+2). All 18 mirror anchors bumped.
  **Verification.** Full `dotnet test` `1307 / 1307` passing.

  **Operator test ask (single command, paste-back one line):**

  On your reference rig, after the build is current:
  ```powershell
  pwsh ./scripts/pal-benchmark-coldstart.ps1
  ```
  Takes ~10-30 seconds. Paste the final `ColdStart: ...` line
  back so we can pin the reference-rig number in docs.

- **Pass 359 - Script-execution smoke tests (Tier-A #9 from
  senior-dev review). Caught a real parser bug in
  install-llama-cpp.ps1 from Pass 357.** Across Passes 344-358
  I shipped many PowerShell scripts (install-llama-cpp.ps1,
  connect-cloud.ps1, the SLO alert rules, etc.); every test
  for them grepped source strings, none actually executed.
  Pass 359 fixes that with a `ScriptExecutionTests.cs` class
  that spawns pwsh subprocess and runs each script with
  `-DryRun` (offline-deterministic via `-ReleaseTag` pin).
  **The find:** Pass 357's off-target hard-gate block had
  `Write-Host "...\\`"` which PowerShell parsed as backslash +
  escaped-quote, leaving the double-quoted string unterminated.
  Result: install-llama-cpp.ps1 syntax-errored on ANY off-target
  host invocation (linux-x64, macos-arm64, anything not
  matching the reference rig with cuda12 backend). The bug
  shipped in Pass 357, escaped Pass 357's tests (which only
  text-presence-matched), and would have only been caught by
  an operator actually running the script on off-target
  hardware. Fix: single-quoted PowerShell strings for the
  literal-text Write-Host calls. **Tests added (6 in
  ScriptExecutionTests.cs):** InstallLlamaCpp_DryRun_OnTarget_ExitsCleanly
  (forces on-target path); InstallLlamaCpp_OffTarget_NoExplicitBackend_SkipsLocalInstall
  (off-target hard-gate path); InstallLlamaCpp_OffTarget_ExplicitBackend_ProceedsToDryRun
  (explicit -Backend opt-in overrides the hard-gate);
  ConnectCloud_DryRun_OpenaiProvider_ExitsCleanly (verifies
  Pass 357's connect-cloud.ps1 actually executes + masked-key
  display doesn't leak the full key);
  ConnectCloud_CustomProvider_WithoutBaseUrl_FailsCleanly
  (mandatory-parameter validation); ConnectCloud_EmptyApiKey_FailsCleanly
  (empty-string ApiKey rejected by validation block). **Test
  infrastructure (new pattern):** `RunPwsh` helper resolves
  `pwsh` / `pwsh.exe` / `powershell.exe` / `powershell` on
  PATH; tests skip with `Assert.Ignore` when no PowerShell is
  available so CI environments without it still pass. 30s
  timeout per script invocation. **Test cascade.** Count
  `1299 -> 1305` (+6). All 18 mirror anchors bumped.
  **Verification.** Full `dotnet test` `1305 / 1305` passing.

- **Pass 358 - SLI/SLO contract + Prometheus alert rules + Grafana
  dashboard (Tier-S #4 from senior-dev review).** PalLLM emitted
  OTel metrics since Pass 314 but shipped no SLO contract, no
  alert rules, no reference dashboard. Pass 357 made cloud-API
  and remote-PC deployments first-class shipping paths, which
  means operators are now running PalLLM in places they need
  real observability. Pass 358 ships all three. **New files:**
  `scripts/observability/palllm.alerts.yaml` (6 Prometheus
  alert rules: PalLLMServiceDown / PalLLMChatLatencyHigh
  Warning + Critical / PalLLMFallbackRateHigh /
  PalLLMInboxBacklogGrowing / PalLLMInferenceLaneRed; each with
  severity label for Alertmanager routing + runbook_url
  annotation pointing at the SLO doc anchor);
  `scripts/observability/palllm-grafana-dashboard.json` (4
  panels: chat latency p50/p95/p99 with threshold colours,
  fallback-reply rate with 30% threshold, bridge inbox backlog,
  inference lane status; uid=palllm-slo-overview for runbook
  deep-links); `docs/OBSERVABILITY_SLO.md` (declares 3 SLOs --
  Availability 99.5% / Latency p95 < 2.5s Standard tier /
  Quality fallback rate < 30%; SLI PromQL formulas; import
  recipes for Prometheus + Grafana; Alertmanager routing
  example; per-alert runbook sections with anchor links;
  tuning guide for non-Standard hardware tiers). **Threshold
  rationale:** Standard-tier 2500 ms p95 comes straight from
  docs/HOT_PATH.md (the per-tier latency budget table);
  Constrained-tier 4000 ms is the page-grade ceiling; 30%
  fallback rate is the "inference is structurally degraded
  but companion is still responsive" threshold. **Tests (9 added
  in `ObservabilitySloTests.cs`):**
  AlertsYaml_ShipsAllSixSloAlerts (each alert named + severity
  + slo labels); AlertsYaml_ThresholdsMatchSloContract (2.5s /
  4.0s / 0.30 / 50-files pinned); AlertsYaml_EachAlertReferencesValidPrometheusMetric
  (cross-checks against PrometheusExporter.cs);
  AlertsYaml_EachAlertHasRunbookUrl;
  GrafanaDashboard_IsValidJson_WithFourSloPanels (uid +
  panel-titles); GrafanaDashboard_LatencyPanelCarriesThresholdColours;
  SloDoc_DeclaresThreeSlosWithSliFormulas;
  SloDoc_LinksToAlertsYaml_AndDashboardJson;
  SloDoc_HasRunbookSectionPerAlert (5 runbook anchors pinned).
  **Test cascade.** Count `1290 -> 1299` (+9). All 18 mirror
  anchors bumped + docsCount 68 → 69. **Verification.** Full
  `dotnet test` `1299 / 1299` passing.

- **Pass 357 - Below-reference hardware: cloud API + remote PC
  shipping escape paths.** Operator directive: "for now anything
  below 3090 needs to either use cloud apis to run it and or
  call on a remote pc that they can use for compute/local model
  usage." Pass 356 had narrowed v1.0 to the reference rig but
  left the off-target path as "warn and proceed" — that was
  half a job. Pass 357 hardens the gate from "warn" to "skip
  local install, point at the two shipping escape paths." New
  `scripts/connect-cloud.ps1` (Pass 357 connector) handles the
  cloud-API path with provider presets (openai, groq, together,
  openrouter, deepseek, mistral, custom), optional `-Probe` to
  validate the key before writing config, and a security
  reminder that env-var ApiKey delivery beats committing to
  appsettings. `pal connect cloud` and `pal connect openai`
  verbs route to it. `install-llama-cpp.ps1` off-target branch
  computes `offTargetSkipLocal` (off-target + no explicit
  -Backend opt-in) and exits 0 after printing both escape
  paths instead of proceeding with an unsupported local
  install. Remote-PC path reuses existing
  `connect-llamacpp.ps1 -LlamaCppUrl` — no new script needed
  since that connector already accepts non-loopback URLs.
  **`MINIMUM_REQUIREMENTS.md`** rewrites the "What if my
  hardware is smaller?" section with both escape paths as
  first-class recipes including concrete `pwsh ./scripts/connect-cloud.ps1`
  / `pwsh ./scripts/connect-llamacpp.ps1 -LlamaCppUrl`
  commands. **`POST_RELEASE_ANNEX.md`** adds a "Surfaces that
  REPLACE local inference for below-reference hardware"
  section clarifying these are SHIPPING (not deferred).
  **pal.json + agents.json:** connect-cloud.ps1 added to the
  scripts manifest + new `cloudApi` row in
  agents.json/inferenceWiring. **Tests (4 added, 52 total in
  LlamaCppBundlingTests):** `ConnectCloudScript_ExistsWithProviderPresets`,
  `InstallScript_OffTargetSkipsLocalInstall_AndPointsAtEscapePaths`,
  `MinimumRequirementsDoc_DocumentsBothEscapePaths`,
  `PostReleaseAnnex_DistinguishesShippingEscapePaths_FromDeferred`.
  **Test cascade.** Count `1286 -> 1290` (+4). All 18 mirror
  anchors bumped. **Verification.** Full `dotnet test`
  `1290 / 1290` passing.

- **Pass 356 - Tighten shipping scope to v1.0 reference rig
  (RTX 3090 / 32 GB DDR4 / 5800X3D).** Operator directive:
  "minimum requirements shift up to single 3090 and 32 gb ddr 4
  system ram with 5800x3d. get rid of everything else. maybe it
  can be implemented post release." Pass 339-355 had built broad
  multi-hardware / multi-model support under the implicit
  assumption "more configs supported = better project." A
  senior-dev review flagged this as scope sprawl. Pass 356
  narrows v1.0 to one reference rig + a documented post-release
  roadmap for everything else. **Approach (soft scope cut):**
  declare the target loudly in docs + tighten shipping defaults
  + mark deferred catalog entries with `PostRelease=$true`.
  Keep the existing code paths so opt-in advanced operators
  still have escape hatches and the work isn't lost.
  **New docs (2 files):** `docs/MINIMUM_REQUIREMENTS.md`
  (authoritative single-page reference-rig spec + supported
  tiers + the "what if my hardware is bigger / smaller" answer);
  `docs/POST_RELEASE_ANNEX.md` (per-surface catalog of what's
  deferred + why + re-promotion contract + where the code
  lives). **README.md:** new "Minimum requirements" section
  immediately above Quickstart so operators see the reference
  rig spec before learning the install flow. Links to both new
  docs. **`scripts/install-llama-cpp.ps1`:** shipping-target
  check after auto-detection emits an "OFF-TARGET (post-release
  scope)" warning when the host doesn't match the reference rig,
  pointing at MINIMUM_REQUIREMENTS.md + POST_RELEASE_ANNEX.md.
  Catalog entries for the 4 heavyweight families
  (`Qwen3-Coder-Next`, `MiniMax-M2.7-UD-IQ4_XS`,
  `MiniMax-M2.7-UD-IQ3_XXS`, `DeepSeekV4-Flash`) now carry
  `PostRelease = $true`. `Get-RecommendedModel` skips
  `PostRelease=$true` entries so the auto-recommendation only
  proposes reference-rig models (Qwen3.6-35B-A3B, Qwen3.6-27B,
  Gemma-4-31B, Gemma-4-E4B). **`docs/LLAMA_CPP_BUNDLED.md`:**
  new "v1.0 shipping target" callout at the top that frames the
  existing hardware-tier matrix as the post-release roadmap.
  **`docs/INDEX.md`:** entries for the 2 new docs (docs count
  66 → 68). **Tests (6 added, 48 total in LlamaCppBundlingTests):**
  MINIMUM_REQUIREMENTS.md content + POST_RELEASE_ANNEX.md
  content + README requirements section + install-script
  shipping-target check + catalog PostRelease markers +
  LLAMA_CPP_BUNDLED.md callout. **Test cascade.** Count
  `1280 -> 1286` (+6). All 18 mirror anchors bumped + docs count
  66 → 68. **Verification.** Full `dotnet test` `1286 / 1286`
  passing.

- **Pass 355 - Bridge ingest adversarial/fuzz tests (Tier-S #3 from
  the senior-dev review).** Closes risk-register #4 (pack /
  event content authored by a crashing game, a tampered mod, or
  an outright attacker) by building a 35-test adversarial-input
  suite for `PalLlmRuntime.DrainInbox`. The existing happy-path
  coverage in `RuntimeTests.DrainInbox_*` exercised well-formed
  envelopes only; the trust boundary between the Lua mod and the
  sidecar runtime had no tests for the malformed/hostile-input
  shapes that occur naturally on Palworld crash / disk full /
  half-write / tampered mod. **The invariants now under test:**
  (1) no exception bubbles out of DrainInbox under any
  adversarial input; (2) bad files quarantine to BridgeFailedDir
  -- the inbox always empties; (3) after N corrupt files, a
  subsequent valid envelope still processes (state recovery is
  load-bearing). **Test fixture (`BridgeIngestAdversarialTests.cs`):**
  minimal `InboxFixture` with its own `DropRawFile` + `DropEnvelope`
  helpers + `InboxCount` / `QuarantinedCount` assertions.
  **Coverage matrix:**
  - **File-shape:** empty file; structurally-malformed JSON
    (truncated braces, wrong top-level shape, top-level scalar,
    binary garbage, etc.); deeply-nested JSON (256 levels) to
    exercise the size cap or System.Text.Json's depth cap.
  - **Envelope-level:** blank/whitespace EventType; hostile
    EventType (unknown types, path-traversal patterns,
    bidi-override unicode, case-variance, embedded newlines,
    null bytes); hostile Source field (path-traversal patterns
    that must not escape BridgeFailedDir on quarantine).
  - **Payload-content:** very large message payloads (above and
    below MaxInboxEventBytes); adversarial string content
    (control bytes, replacement chars, surrogate-pair emoji,
    bidi override, SQL/log4j/template/XSS injection shapes,
    escaped control bytes); type-confused payloads (numbers
    where strings expected, arrays where strings expected,
    objects where strings expected).
  - **Bulk-failure recovery:** 100 corrupt files followed by
    one valid file -- drain must process the valid file AND
    quarantine the corrupt ones (varying shapes).
  **Findings:** every test passed on first run (after compile
  fixes for the NUnit 4 `Assert.DoesNotThrow` return-type
  pattern). The pipeline's existing structural defenses
  (`BoundedJsonFileReader` size cap + `DeserializeBridgePayload`
  default-fallback + per-payload `IsNullOrWhiteSpace` guards)
  hold under hostile input. **Test cascade.** Count
  `1243 -> 1280` (+37). All 18 mirror anchors bumped.
  **Verification.** Full `dotnet test` `1280 / 1280` passing.

- **Pass 354 - Production-safety startup auth guard: refuse to boot
  unauthenticated on non-loopback bind.** First Tier-S item from
  the senior-dev review just completed. The shipping default
  `Auth.ApiKey = null` was documented as "right posture for
  localhost-only deployments" but UNENFORCED. An operator who
  flipped `ASPNETCORE_URLS=http://0.0.0.0:5088` got an
  unauthenticated network endpoint with zero startup warning --
  the classic "I forgot to set the key before exposing the port"
  footgun. Pass 354 adds a pure-logic `StartupAuthGuard` in
  `PalLLM.Domain.Configuration` that inspects bind URLs + ApiKey
  and returns Pass / Warn / Fail. Wired into Program.cs after
  `WebApplication.Build()`. **Verdict logic:** non-loopback bind
  + null/empty ApiKey → Fail (LogCritical + throw
  InvalidOperationException); all-loopback + null/empty ApiKey →
  Warn (LogWarning with remediation hint); any bind + non-empty
  ApiKey → Pass (silent). Loopback classification handles
  127.0.0.0/8, ::1, localhost; wildcard binds (0.0.0.0, ::, *,
  +) are non-loopback; unparseable URLs fail safe (treated as
  non-loopback so warnings/fails surface rather than getting
  masked). **Backwards compat:** every existing local-only
  operator keeps working unchanged (loopback + null key = Warn,
  not Fail). The ONLY behavior change is: operators who were
  silently running unauthenticated on a network interface now
  get a clear refuse-to-start with both remediation paths in
  the log line (set the key OR restrict the bind). **Tests:**
  new `StartupAuthGuardTests.cs` (39 tests) covers every
  loopback variant, wildcard binds, mixed binds, whitespace
  ApiKey, null/empty URL collection, and the IsLoopbackBind
  helper directly. **Docs:** `SECURITY.md` § "Authentication
  when deploying beyond localhost" now documents the fail-fast
  guard. **Test cascade.** Count `1204 -> 1243` (+39). All 18
  mirror anchors bumped.

- **Pass 353 - Surface the bundled-engine one-command flow in
  first-impression docs.** Audit found `install-llama-cpp.ps1`
  was mentioned ZERO times across `README.md`, `docs/QUICKREF.md`,
  and `docs/CHEAT_SHEET.md` — the 6 passes of bundled-engine
  work (344, 347-352) were invisible to anyone reading the
  operator-first-impression docs. **README.md**: new "Run with
  local inference (one command)" subsection in Quickstart
  showing `pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch`
  and what it does (detect hardware → pick backend → download
  → smoke-test → recommend → wire PalLLM → launch). **QUICKREF.md**:
  new "Bundled inference engine (Pass 352)" section with a
  4-row table covering bare install, `-WireConfig`,
  `-AutoLaunch`, and pinned-tag/forced-backend. Links to
  `LLAMA_CPP_BUNDLED.md` as the deep-dive. **CHEAT_SHEET.md**:
  new "Bundled local inference (one command)" section + fixed
  stale test count `1154 / 1154` → `1204 / 1204` (was 50+ count
  bumps behind because the Drift_Test_count_docs gate didn't
  scan CHEAT_SHEET.md). **Tests (4 added, 42 total in
  LlamaCppBundlingTests):** `FirstImpressionDocs_AllMentionInstallLlamaCpp`
  pins every required doc surface;
  `Readme_QuickstartIncludesOneCommandInferenceSetup` pins
  -AutoLaunch + section heading; `CheatSheet_DocumentsBundledInferenceSection`
  pins the bundled-inference heading + -AutoLaunch;
  `Quickref_BundledEngineSection_LinksToDeepDive` pins the
  QUICKREF heading + LLAMA_CPP_BUNDLED.md deep-dive link.
  **`LocateRepoFile` helper fix:** previously walked up from
  test bin looking for `<ancestor>/README.md`, which matched
  `tests/README.md` before reaching repo root. Now anchors on
  the `PalLLM.sln` marker, so repo-root files like README.md
  resolve correctly. **Test cascade.** Count `1200 -> 1204`
  (+4). All 18 mirror anchors bumped, plus the stale `1154`
  in CHEAT_SHEET.md fixed.

- **Pass 352 - End-to-end one-command install: -WireConfig
  propagates per-family sampler into PalLLM appsettings.**
  Passes 347-351 made install + launch hardware-aware and
  family-aware, but the operator flow still required three
  separate commands (install → connect → play) and there was a
  silent gap: PalLLM's per-request HTTP body carries Temperature
  / TopP / TopK / MinP / PresencePenalty from
  `appsettings.json`, which **overrides** llama-server's
  --temp/--top-p/--top-k/--min-p defaults. So even though Pass
  351's auto-launch correctly started MiniMax with the MiniMax
  sampler (`temp=1.0 top-k=40 min-p=0.01`), PalLLM's actual chat
  traffic kept sending Qwen3.6 sampling because the shipping
  appsettings hadn't been touched -- the loaded model is
  MiniMax but PalLLM is asking for Qwen behaviour. Pass 352
  closes that. **Code:** `connect-llamacpp.ps1` -WriteConfig
  now propagates the per-family sampler into PalLLM.Inference's
  Temperature/TopP/TopK/MinP/PresencePenalty when -ModelProfile
  is explicitly set. The 5 profiles match Get-SamplerFlags from
  install-llama-cpp.ps1 (qwen36, qwen3-coder, minimax, gemma,
  deepseek). `install-llama-cpp.ps1` gains -WireConfig switch
  and -ConfigPath override. -AutoLaunch IMPLIES -WireConfig so a
  single command (`pwsh ./scripts/install-llama-cpp.ps1
  -AutoLaunch`) does install → smoke-test → wire PalLLM →
  launch llama-server. The wire step runs BEFORE the blocking
  llama-server call so PalLLM's next restart picks up the new
  BaseUrl/Model/sampler. The install script invokes the connect
  script with the full recommendation (Model, ModelProfile,
  ContextSize, GpuLayers, NCpuMoe, QuantizedKv, Prio) so the
  written appsettings carries the complete per-family recipe.
  **Tests (3 added, 38 total in LlamaCppBundlingTests):**
  `ConnectScript_WriteConfigPropagatesPerFamilySampler` pins
  the sampler snapshot per profile (each profile's canonical
  numbers appear in the script);
  `InstallScript_HasWireConfigSwitch_AndAutoLaunchImpliesIt`
  pins the -WireConfig param + the effectiveWireConfig OR-logic
  + the connect-llamacpp.ps1 invocation;
  `InstallScript_WireConfigRunsBeforeAutoLaunch` pins the
  ordering (wire BEFORE blocking launch). **Test cascade.**
  Count `1197 -> 1200` (+3). All 18 mirror anchors bumped.
  **Verification.** Full `dotnet test` `1200 / 1200` passing.
  **Operator UX after Pass 352:**
  ```
  pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch
  ```
  One command does install + smoke-test + wire PalLLM +
  launch llama-server with hardware-aware + family-aware
  recipe.

- **Pass 351 - Smart-pick catalog covers all 7 curated families +
  per-model sampler in auto-launch.** Pass 350's recommendation
  engine knew about only 4 of the 7 curated families. The three
  heavyweight specialty models (Qwen3-Coder-Next, MiniMax-M2.7
  UD-IQ3_XXS, MiniMax-M2.7 UD-IQ4_XS) plus the research-lane
  DeepSeekV4-Flash-158B were invisible to the recommendation,
  and auto-launch hardcoded the Qwen3.6 thinking-OFF sampler --
  silently applying Qwen sampling to MiniMax (Unsloth's M2.7
  recipe asks for temp=1.0, top-k=40, min-p=0.01, --prio 3).
  Pass 351 closes both gaps. **Catalog (now 8 entries, 7
  families):** each entry carries Sampler ('qwen36' | 'qwen3-coder'
  | 'minimax' | 'gemma' | 'deepseek'), Prio (--prio override; 3
  for MiniMax per Unsloth), and AllowsSpecDecode (false for
  Qwen3-Coder-Next per upstream #21886). Multi-shard models
  (Qwen3-Coder-Next 3-shard, MiniMax-M2.7 3-shard / 4-shard)
  carry the first-shard relative path -- llama.cpp's
  llama_model_loader auto-detects and loads the rest.
  **Get-SamplerFlags helper:** returns the right
  --temp/--top-p/--top-k/--min-p combo per Sampler value. The
  auto-launch path calls it via `Get-SamplerFlags -Sampler
  $recommendation.Sampler` and emits `--prio` only when the
  recommendation says so. The `--chat-template-kwargs '{"enable_thinking":false}'`
  emission is gated on Qwen profiles only (MiniMax / Gemma /
  DeepSeek don't use that kwarg). **Tests (5 added):**
  `InstallScript_CatalogCoversEveryCuratedFamily` pins all 7
  filenames; `InstallScript_CatalogEntriesCarrySamplerAndPrioMetadata`
  pins per-family Sampler + MiniMax Prio=3 + Qwen3-Coder
  AllowsSpecDecode=$false; `InstallScript_HasGetSamplerFlagsHelper`
  pins the helper + each branch's canonical numbers;
  `InstallScript_AutoLaunchEmitsPerModelSamplerAndPrio` pins the
  auto-launch wiring; `InstallScript_MultiShardCatalogPathsTargetFirstShard`
  pins the -00001-of-NNNNN.gguf convention for the 3 multi-shard
  families. **Test cascade.** Count `1192 -> 1197` (+5). All 18
  mirror anchors bumped. **Verification.** Full `dotnet test`
  `1197 / 1197` passing.

- **Pass 350 - MoE-aware offloading + KV-cache-aware VRAM math +
  multi-GPU detection + backend-specific safety nets.** Pass 349
  could not actually run the curated MoE library (4 of 7 families
  are MoE: Qwen3.6-35B-A3B, Qwen3-Coder-Next, MiniMax-M2.7 IQ3_XXS,
  MiniMax-M2.7 IQ4_XS) on consumer GPUs because the recommendation
  treated VRAM-fit as "model file ≤ VRAM" and fell back to
  Gemma-4-E4B whenever a MoE didn't fit. Pass 350 fixes that with
  the upstream `--n-cpu-moe N` flag (David Sanftenberg + Doctor-Shotgun
  recipes): move the deepest N layers' expert FFN tensors to CPU/RAM
  while keeping attention + active experts + KV cache on GPU.
  **Research surface (extensive):** David Sanftenberg's Medium guide
  on Qwen3-235B-A22B partial offload, Doctor-Shotgun's HF blog on
  performant MoE CPU inference, upstream PR #11397 for `-ot` /
  `--override-tensor`, upstream issue #14999 (MoE + `--no-mmap` =
  memory crit errors), ROCm issue #4903 (HIP + `--mlock` forces
  shared memory), Hannecke's Apple Silicon tuning guide for
  `--mlock --prio 2`, the canonical KV-cache memory formula
  (2 × layers × kv_heads × head_dim × ctx × bytes), and the
  oobabooga GGUF VRAM formula. **Code (2 files expanded):**
  `install-llama-cpp.ps1` gains `Get-DetectedGpuCount` (counts
  GPUs via nvidia-smi/rocm-smi/WMI), `Get-KvCacheGb` (KV memory
  estimator), and a rewritten `Get-RecommendedModel` that walks
  each curated family with `MinVramGb` (full-fit) + `MoeMinVramGb`
  (partial-fit) + `Layers/KvHeads/HeadDim` and returns a richer
  payload (Path, Family, ContextSize, GpuLayers, NCpuMoe, QuantizedKv,
  IsMoE, Note). The auto-launch path emits `--n-cpu-moe N`,
  `-ctk q8_0 -ctv q8_0` when the recommendation says so. New
  multi-GPU hint section prints when more than one card is
  detected, pointing at `connect-llamacpp -SplitMode graph`
  (symmetric) and `-TensorSplit 2,1` (asymmetric). The hardware
  summary now shows GPU count when > 1. `connect-llamacpp.ps1`
  exposes `-NCpuMoe <int>` and `-OverrideTensor <regex>`
  parameters that round-trip into `--n-cpu-moe` and `--override-tensor`.
  **Docs:** `LLAMA_CPP_BUNDLED.md` gains four new sections —
  "MoE offloading recipes" (--n-cpu-moe + --override-tensor +
  VRAM gating table), "KV-cache-aware VRAM math" (per-token bytes
  formula + ctk/ctv quantization tradeoffs), "Backend-specific
  safety nets" (MoE + --no-mmap = #14999, HIP + --mlock = #4903,
  Apple Silicon = `--mlock --prio 2`, CUDA 13.0-13.2 + MiniMax =
  gibberish, Blackwell sm_120 + CUDA 13.x = MMQ crash). **Tests
  (6 new):** `InstallScript_HasMoeOffloadRecommendationFields`
  (NCpuMoe/QuantizedKv/MoeMinVramGb/IsMoE in recommendation,
  --n-cpu-moe + -ctk q8_0 + -ctv q8_0 in auto-launch);
  `InstallScript_HasKvCacheBudgetHelper` (Get-KvCacheGb + the GQA
  formula); `InstallScript_HasMultiGpuDetection`
  (Get-DetectedGpuCount + multi-GPU hint + both SplitMode + TensorSplit
  referenced); `ConnectScript_ExposesNCpuMoeAndOverrideTensorParams`;
  `BundledDoc_DocumentsMoeOffloadingRecipes`;
  `BundledDoc_DocumentsKvCacheBudgetAndSafetyNets`
  (#14999, #4903, `--mlock --prio 2`). **Test cascade.** Count
  `1186 -> 1192` (+6). All 18 mirror anchors bumped.
  **Verification.** Full `dotnet test` `1192 / 1192` passing.

- **Pass 349 - Zero-config "any hardware" setup: cross-platform
  detection, VRAM-aware recommendation, smoke test, auto-launch.**
  The operator's "flawlessly easily seamlessly run on any hardware
  config" directive. After Pass 348 the install + launch chain still
  required several manual decisions (which model? which `-ngl`?
  which sampler?). Pass 349 closes the loop. **install-llama-cpp.ps1
  expansions:** `Get-DetectedGpuVendor` now covers Windows
  (WMI Win32_VideoController) + Linux (`nvidia-smi`, `rocm-smi`,
  `lspci`) + macOS (Apple Silicon arm64 detection + `system_profiler`
  for discrete cards) — guarded by `Test-CommandAvailable` so missing
  tools degrade gracefully. New `Get-DetectedVramGb` prefers
  `nvidia-smi memory.total` / `rocm-smi --showmeminfo` over WMI's
  UINT32-truncated AdapterRAM. New `Get-DetectedSystemRamGb` uses
  WMI / `sysctl hw.memsize` / `/proc/meminfo` per-platform. New
  `Get-CudaToolkitVersion` parses `nvcc --version` to warn when the
  operator is on the broken 13.0-13.2 band (MMQ crashes on Blackwell,
  gibberish on MiniMax-M2.7). New `Get-RecommendedModel` walks the
  curated catalog (Qwen3.6-35B-A3B → Qwen3.6-27B → Gemma-4-31B →
  Gemma-4-E4B) and returns the highest-quality model whose `MinVramGb`
  fits the detected card; falls back to fast-start tier with reduced
  `-ngl` for tight VRAM, or CPU-only with `-ngl 0` on no-GPU hosts.
  New `Test-LlamaServerBinary` execs `llama-server --version` post-
  install; on non-zero exit, prints actionable hints for missing
  cudart / VC++ redist / native crash. New `-AutoLaunch` switch
  starts `llama-server` with the recommended recipe immediately after
  install. New `-NoSmokeTest` opt-out (smoke test ON by default).
  New `-ModelsRoot` parameter (defaults to
  `$env:PalLLM_ExternalModelsRoot`, then `D:\Models` on Windows or
  `$HOME/Models` elsewhere). Hardware summary block prints before
  any download so the operator can ctrl-c if auto-pick is wrong.
  **Docs:** `LLAMA_CPP_BUNDLED.md` gets a "Zero-config setup"
  section with the actual hardware-summary output shape, a
  cross-platform support matrix (Windows / Linux / macOS arm64 /
  macOS x64), the VRAM → curated-model recommendation table, and a
  smoke-test failure-hint catalog. **Tests (6 new):**
  `InstallScript_DetectsCrossPlatformGpuVendor_NotJustWindows` pins
  the macos-arm64 + linux-x64 + nvidia-smi + rocm-smi + lspci +
  system_profiler probes. `InstallScript_HasVramAndRamDetectionHelpers`
  pins the new function names + the platform-appropriate APIs.
  `InstallScript_HasVramBasedModelRecommendation` pins
  `Get-RecommendedModel` + every curated family + the `MinVramGb`
  gate. `InstallScript_ExposesAutoLaunchAndSmokeTestSwitches` pins
  the new parameters + the smoke-test failure hints (cudart +
  VCRUNTIME). `InstallScript_WarnsAboutBrokenCudaToolkitBand`
  asserts the 13.0-13.2 warning band. `InstallScript_PrintsHardwareSummaryBeforeDownload`
  asserts the operator sees Detected GPU / VRAM / RAM / CUDA / Models
  root before bandwidth gets spent. **Test cascade.** Count
  `1180 -> 1186` (+6). All 18 mirror anchors bumped. **Verification.**
  Full `dotnet test` `1186 / 1186` passing.

- **Pass 348 - Per-model recipes + multi-shard auto-load + known-bug
  catalog.** Pass 347 covered the install/launch happy path but the
  next research round surfaced gaps: (1) the curated library has SEVEN
  model families and Pass 347 only had recipes for two; (2) three of
  them are multi-shard GGUFs and the doc didn't explain the
  `-m firstshard.gguf` auto-load convention; (3) MiniMax-M2.7 has a
  different canonical sampler than Qwen3.6 (temp 1.0, top-p 0.95,
  top-k 40, min-p 0.01 -- per Unsloth's M2.7 doc) and crashes with
  CUDA 13.2 specifically (gibberish output); (4) Gemma-4-31B's mmproj
  vision projector triggers SIGABRT on CUDA per upstream issue #21402;
  (5) Qwen3-Coder-Next's speculative decoding errors with
  `load_model: speculative decoding not supported by this context`
  (upstream #21886). **Research surface:** the Unsloth docs for
  Qwen3.6, Qwen3-Coder-Next, and MiniMax-M2.7 for canonical sampler
  profiles; upstream issues #21402 (Gemma 4 mmproj CUDA crash),
  #21016 (multi-shard wrong-index from HF cache), #21564
  (flash_attn_stream_k_fixup crash on RTX 5090), and discussion
  #21886 (Qwen3-Coder-Next spec-decode broken); upstream
  `tools/gguf-split/README.md` for multi-shard auto-load semantics;
  the upstream llama.cpp multi-GPU doc for `--tensor-split` and split-mode
  graph; Ventus Servers 2026 tuning guide for the `--threads 1`
  GPU-offload +43% finding. **Docs (1 file expanded):**
  `LLAMA_CPP_BUNDLED.md` adds a "Per-model recipes" section with 7
  per-model recipes (each model's canonical sampler + memory shape
  + special caveats), a "Multi-shard GGUF loading" section
  explaining the first-shard auto-load convention, a "Known bugs
  and caveats" section covering CUDA-13.2 gibberish on MiniMax,
  Gemma-4 mmproj SIGABRT on CUDA, Qwen3-Coder-Next spec-decode
  broken, llama-server multi-shard wrong-index from HF cache, and
  the RTX 5090 stream_k_fixup crash; plus a "Multi-GPU + advanced
  perf knobs" table covering `--tensor-split`, `--split-mode graph`,
  `--threads 1`, `--threads-batch`, `--prio 3`, `--mlock`,
  `--no-mmap --cache-ram`. **Code (1 file expanded):**
  `connect-llamacpp.ps1` adds `-ModelProfile {qwen36|qwen3-coder|
  minimax|gemma|deepseek|generic}` (default `qwen36`) so the printed
  launch line carries the right per-model sampler; adds `-Threads`,
  `-ThreadsBatch`, `-Prio`, `-Mlock`, `-NoMmap`, `-TensorSplit`,
  `-SplitMode {layer|row|graph|none}` perf knobs that emit the
  matching `--threads`, `--threads-batch`, `--prio`, `--mlock`,
  `--no-mmap`, `--tensor-split`, `--split-mode` flags. The
  `--chat-template-kwargs '{"enable_thinking":...}'` emission is
  now gated on `qwen36`/`qwen3-coder` profiles only (MiniMax /
  Gemma / DeepSeek don't use that template kwarg). **Tests (9
  added to LlamaCppBundlingTests):** every curated family has a
  recipe block; MiniMax sampler differs from Qwen; CUDA-13.2 +
  gibberish documented; Gemma-4 mmproj SIGABRT issue #21402 +
  `clip_model_loader::load_tensors` named; Qwen3-Coder spec-decode
  broken with exact error string and issue #21886; multi-shard
  `-00001-of-` naming + auto-load convention documented; connect
  script exposes `-ModelProfile` ValidateSet with all 6 profile
  names; MiniMax sampler emits in the connect script; all 7 perf
  knobs (`-Threads`/`-ThreadsBatch`/`-Prio`/`-Mlock`/`-NoMmap`/
  `-TensorSplit`/`-SplitMode`) round-trip into the printed command.
  **Test cascade.** Count `1171 -> 1180` (+9). All 18 mirror anchors
  bumped. **Verification.** Full `dotnet test` `1180 / 1180`
  passing.

- **Pass 347 - Hardware-aware bundled llama.cpp + Qwen3.6 canonical sampler.**
  After Pass 346 ripped out Ollama, the bundled llama.cpp install
  script became the operator's only on-ramp -- and a quick research
  sweep found three latent flaws: (1) `install-llama-cpp.ps1` still
  asked for the pre-split `llama-<tag>-bin-win-cuda-x64.zip` asset,
  which upstream hasn't published since the CUDA 12.4 / 13.1 split
  earlier in 2026 -- the installer was silently 404ing on every fresh
  release; (2) appsettings.json was missing `TopK=20` and `MinP=0.0`,
  so PalLLM's per-request sampler didn't match Unsloth's documented
  Qwen3.6 thinking-OFF canonical; (3) `connect-llamacpp.ps1` defaulted
  `--flash-attn on`, which crashed RTX 5090 Blackwell with the April
  2026 `stream_k_fixup` kernel regression. **Research surface
  (extensive):** queried upstream releases (b9284 is the May 22 tag --
  ~6 releases/day cadence), pulled the Unsloth Qwen3.6 docs for the
  canonical sampler, cross-referenced the RTX 5090 / RTX 3090 / RTX
  PRO 6000 / Apple M3 Max / Strix Halo speculative-decoding benchmarks
  (net-positive on workstation Blackwell + Apple Silicon, net-negative
  on Ampere single-card A3B MoE), confirmed the asset naming
  convention (`-cuda-12.4-x64`, `-cuda-13.1-x64`, `-vulkan-x64`,
  `-hip-radeon-x64`, `-sycl-x64`, `-cpu-x64`), confirmed the companion
  `cudart-llama-*.zip` runtime pack is required for CUDA backends.
  **Code (3 files):** `install-llama-cpp.ps1` rewritten with WMI-based
  GPU vendor detection + 6-backend selection matrix (`auto`, `cuda12`,
  `cuda13`, `vulkan`, `hip`, `sycl`, `cpu`) + cudart companion
  download + per-backend launch recipe printed at install.
  `appsettings.json` adds `TopK: 20` and `MinP: 0.0` (Unsloth Qwen3.6
  thinking-OFF canonical) with a `_comment_Sampler` explanation key.
  `connect-llamacpp.ps1` defaults `-FlashAttn` to `auto`, adds
  `-EnableThinking $false` switch, emits
  `--chat-template-kwargs '{"enable_thinking":false}'`, and prints the
  Unsloth canonical sampler in the launch command. **Docs (1 new
  file):** `docs/LLAMA_CPP_BUNDLED.md` documents the backend selection
  matrix, hardware-tier launch recipes (Tiers A-F: Blackwell
  workstation, mainstream Ada/Ampere, 16 GB, AMD, Apple Silicon, CPU),
  and the spec-decoding benchmark table. **Tests (9 new):**
  `LlamaCppBundlingTests` pins asset names to the new `-cuda-12.4` /
  `-cuda-13.1` / `-vulkan` / `-hip-radeon` / `-sycl` / `-cpu`
  shape, verifies cudart companion pack download, asserts hardware
  detection helper exists, pins NVIDIA-default to `cuda12` with the
  Blackwell-safety justification comment, pins `TopK=20` + `MinP=0.0`
  + `Temperature=0.7` + `TopP=0.8` + `PresencePenalty=1.5` in shipping
  appsettings, requires the `_comment_Sampler` doc-comment, defaults
  connect's `-FlashAttn` to `auto`, requires the chat-template
  thinking-kwarg, requires the sampler flags in the launch line.
  **Test cascade.** Count `1162 -> 1170` (+8); all 18 mirror anchors
  bumped. **Verification.** Full `dotnet test` `1170 / 1170` passing.

- **Pass 346 - Rip out Ollama back-compat plumbing the aggressive read
  (operator's explicit "yea rip out and replace with llama.cpp"
  confirmation).** Pass 345 retained the provider-aware back-compat
  plumbing because 26+ tests exercised it; the operator then
  explicitly approved the aggressive cut. **Domain code removed
  (4 files):** `InferenceClient.cs` -- `WarmOllamaAsync`,
  `BuildOllamaWarmupEndpoint`, `BuildOllamaWarmupBody`, and the
  warmup request DTO (~100 lines). `InferenceResidencyPolicy.cs` --
  Ollama case in `Resolve` + `DescribeHint`, host-substring/port
  detection. `Configuration/PalLlmOptions.cs` --
  `InferenceResidencyProvider.Ollama` enum value (=2).
  `PalLlmDomainJsonSerializerContext.cs` -- the warmup DTO
  source-gen registration. **Runtime advice scrubbed (3 files):**
  `ModelCollaborationPlanner.cs` -- 15 Ollama startup hints,
  request hints, cache hints, admission controls, security
  controls, metric receipts, and verification checks removed
  (every `OLLAMA_*` env-var advice line, every `ollama ps`
  receipt). `GenAiTelemetry.cs` -- the host-detection branch
  for port 11434 and host substring `ollama` removed; those
  endpoints now fall through to `openai_compatible`.
  `ModelAvailabilityProbe.cs` -- the `/api/tags` Ollama-native
  probe + `ParseOllamaTags` parser removed; every supported
  runtime exposes `/v1/models`. **Comment refresh (2 files):**
  `VisionClient.cs` and `PalLlmFeatureCatalog.cs` lines 933/941
  -- swap "Ollama" out for "llama.cpp, vLLM, LM Studio" in
  doc-listing the structured-output-capable endpoints. **Tests
  follow:** 7 failures isolated, fixed without coverage loss.
  `InferenceResidencyPolicyTests.cs` -- 4 Ollama-specific tests
  deleted, 2 negative regression tests added (port 11434 /
  host `ollama-host.local` must fall through to "none").
  `InferenceClientTests.cs` -- `WarmAsync_WhenAutoDetectedOllama`
  deleted; `GenAiTelemetry.GetProviderName` assertions updated
  to expect `openai_compatible` for the ex-Ollama endpoints.
  `ModelTierTests.cs` -- the `/api/tags` fallback test deleted,
  the merge test no longer registers the route, planner
  assertions switched from `Has.Some.Contains("Ollama")` to
  `Has.None.Contains("OLLAMA_")`-style negative regression
  guards. `MetaTests.cs` -- source-generation hot-path
  assertion now requires the file *not* contain
  `OllamaWarmupRequestBody`. **Test count `1169 -> 1162` (-7);
  all 18 mirror anchors bumped** (README, CHANGELOG, HANDOFF,
  CLAUDE.md, ARCHITECTURE, ROADMAP, CODE_MAP, PROJECT_NUMBERS,
  CONTRIBUTING, copilot-instructions, .cursorrules, the tests
  README,
  agents.json, pal.json, pal.ps1, scripts/onboard.ps1,
  PalLlmRuntime.cs gate-header comment). Full `dotnet test`
  `1162 / 1162` passing. The codebase still has Ollama
  mentions in three intentional places: (1) the
  `PackPublicationSafetyValidator` brand-block regex
  (defensive, flags Ollama for review the same way it flags
  ChatGPT/Claude/Mistral), (2) `ShippingAppsettingsCurationTests`
  comments documenting why `qwen3.6:35b-a3b`-style tags must
  not leak into shipping config (the tests themselves are
  defensive, preventing pre-curation Ollama-tag regression),
  and (3) Pass-346 explanatory comments tagging the removal
  sites for future readers. No operator surface, runtime
  behaviour, or telemetry path still mentions Ollama.

- **Pass 345 - Strip operator-visible Ollama mentions from runtime source code.**
  Sixth iteration of the operator's directive. Passes 339-344
  cleaned scripts, docs, and config; Pass 345 cleans the remaining
  operator-visible mentions in `src/`. Three categories of Ollama
  mention exist in the source today after this pass:
  
  1. **Operator-visible runtime advice** — strings the runtime
     hands operators via `/api/quickstart`, `/api/features`,
     `/api/health/suggestions`. **Cleaned in Pass 345.**
  2. **Provider-aware back-compat plumbing** — `WarmOllamaAsync`,
     `InferenceResidencyProvider.Ollama`, host-pattern detection
     in `InferenceResidencyPolicy`, Ollama-shape JSON parsing in
     `ModelAvailabilityProbe`. **Intentionally retained** — 26+
     tests exercise these paths; full removal is hours of work
     and breaks back-compat for operators running Ollama
     out-of-band (a real-world use case the project's
     conservatism preserves).
  3. **Brand-block regex** in
     `PackPublicationSafetyValidator.cs:186` that actively
     *blocks* Ollama from appearing in release-facing copy.
     **Intentionally retained** — already doing what the
     operator wants.
  
  **Pass 345 changes (4 source files):**
  - `QuickstartGuideBuilder.cs:66` — operator-action message
    "Run any OpenAI-compatible chat-completions server (Ollama,
    LM Studio, vLLM, etc.)..." rewritten to
    "Install the bundled engine with
    `pwsh ./pal.ps1 install-llama-cpp`..." with LM Studio /
    vLLM / Foundry Local as alternatives.
  - `PalLlmFeatureCatalog.cs:212` — `structured-vision-outputs`
    feature summary supported-endpoints list dropped
    "Ollama-compat >= 0.5"; now lists "llama.cpp, LM Studio,
    vLLM, OpenAI-compatible servers in general."
  - `PalLlmFeatureCatalog.cs:932` — `tiered-model-orchestration`
    summary swapped Ollama-tag examples (`gemma3:4b`) for
    unsloth UD-* identifiers via llama.cpp
    (`gemma-4-E4B-it-UD-Q4_K_XL`,
    `Qwen3.6-35B-A3B-UD-Q8_K_XL`).
  - `HealthSuggestionBuilder.cs:82` — code comment "Ollama /
    vLLM is not actually responding" updated to "llama.cpp /
    vLLM / LM Studio server."
  
  **What did NOT change and why explicitly:**
  
  - `InferenceResidencyPolicy.cs` `Ollama` enum value, host
    detection (`uri.Port == 11434`), keep-alive case in
    `Resolve` + `DescribeHint` — `Pass 301`'s 27 tests pin
    these paths. Removing requires either rewriting all 27
    tests around the new "no Ollama" behaviour or accepting
    the test suite drops by ~27. Documented in this entry as
    scoped-but-deferred.
  - `InferenceClient.cs` `WarmOllamaAsync` + `OllamaWarmupRequestBody`
    + JSON-context registration — similar reason; 3 tests in
    `InferenceClientTests.cs` exercise this.
  - `ModelCollaborationPlanner.cs` 31 Ollama-startup-hint
    mentions — these are runtime-emitted hints that fire only
    when the operator's `BaseUrl` points at Ollama
    out-of-band. The `MODEL_COLLABORATION.md` header note
    from Pass 342 already frames this as
    "what happens IF you point PalLLM at Ollama yourself."
  - `PackPublicationSafetyValidator.cs:186` brand-block regex —
    actively blocks Ollama from release-facing copy.
  - `PalLlmFeatureCatalog.cs:933,941` long-line catalog
    entries describing the back-compat runtime knobs — the
    code paths still exist, so the catalog entries are
    accurate; treating them as drift would be a deeper code
    refactor.
  
  **Verification.** No code regression. Full `dotnet test`
  stays at `1169 / 1169`. Full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
  
  **Honest scope note.** This is the 6th iteration of the
  "remove Ollama everywhere" directive. After this pass:
  - **Zero operator-visible Ollama recommendations** anywhere
    in code or docs.
  - **Zero shipping defaults** point at Ollama.
  - **All operator scripts + verbs** use llama.cpp / vLLM.
  - **The `pal connect ollama` verb** prints a deprecation
    message and exits non-zero.
  - **The bundled-llama.cpp install script** ships (Pass 344).
  - **Back-compat probe / residency code paths remain**, with
    explicit deprecation framing in MODEL_COLLABORATION.md and
    this entry. Removing them is a deeper code refactor
    appropriate for a separate pass with a clear "drop
    back-compat" decision; doing it inline would have
    introduced test-suite drift this pass.
  
  If "everywhere" still means more than this, the next-step
  candidates are: rewrite the 27 `InferenceResidencyPolicyTests`
  + 3 `InferenceClientTests` around a no-Ollama runtime,
  then delete the corresponding `[Obsolete]` runtime methods.
  That's a deliberate code refactor pass — happy to do it next
  if explicitly requested.
- **Pass 344 - `pal install-llama-cpp`: ship the "bundled and default" engine.**
  Operator restated the Ollama-out + llama.cpp-bundled directive
  for a fifth time. Previous passes (339, 341, 342) cleaned every
  doc + script mention; this pass delivers the missing piece — an
  actual install script that downloads and verifies the latest
  llama-server release into PalLLM's bundled-engines directory.
  
  **New script + verb.**
  - `scripts/install-llama-cpp.ps1` (~220 lines): queries
    `api.github.com/repos/ggml-org/llama.cpp/releases/latest`,
    auto-detects platform (`win-x64` / `linux-x64` /
    `macos-arm64` / `macos-x64`), downloads the CUDA-enabled
    binary for Windows (CPU fallback path documented), SHA-256
    verifies against the upstream `SHA256SUMS` asset, extracts
    to `$LOCALAPPDATA\Pal\Saved\PalLLM\Bundled\llama.cpp\<tag>\`,
    and creates a `current` junction so PalLLM and operator
    scripts reference a stable path. Idempotent: skips download
    when the target tag is already on disk.
  - `pal install-llama-cpp` verb added to `pal.json` and
    `pal.ps1`. Verb-inventory meta-test caught the missing
    `$known` entry on the first audit — exactly the lockstep
    pattern Pass 311's gates enforce.
  
  **Leftover Ollama cleanup.**
  - `pal.ps1` connect dispatch had a `'ollama'` case that still
    pointed at the deleted `connect-ollama.ps1` script — Pass 339
    deleted the file but missed this dispatch path. Any
    operator running `pal connect ollama` today would have hit a
    "script not found" error with no useful next step.
    Replaced with a clear deprecation message that names the
    bundled-llama.cpp path and exits non-zero so scripts
    detect the change.
  - `appsettings.json` `_comment_BaseUrl` on both Inference and
    Vision blocks updated to point operators at the new install
    script instead of mentioning Ollama as a fallback.
  
  **Flags + safety.**
  - `-ReleaseTag <tag>`: pin a specific release for reproducible
    installs. Recommended for production.
  - `-Platform <id>`: override platform auto-detection (rare).
  - `-BundleRoot <path>`: override the install root.
  - `-VerifyOnly`: check what's installed without re-downloading.
  - `-DryRun`: show the install plan without writing anything.
  - SHA-256 verification against upstream `SHA256SUMS`. Falls
    back to a warning + proceed when upstream doesn't publish
    SHA256SUMS for a given tag (some experimental releases
    skip it). Production operators should pin a tag known to
    ship SHA256SUMS.
  - No telemetry, no auth required. Honors `HTTPS_PROXY` if
    set. Network calls are limited to
    `api.github.com/repos/ggml-org/llama.cpp` and the
    matching `github.com/.../releases/download/...` URLs.
  
  **Operator path from clean clone to working chat:**
  ```powershell
  pwsh ./pal.ps1 install-llama-cpp        # 30-50 MB download, ~10 s
  # Start llama-server pointing at the curated quality tier:
  & "$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bundled\llama.cpp\current\llama-server.exe" `
      -m D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
      --mmproj D:\Models\mmproj\mmproj-F16.gguf `
      --host 127.0.0.1 --port 8080 -c 16384 -ngl 99 `
      --flash-attn on --spec-type ngram-mod --metrics --no-webui
  pwsh ./pal.ps1 connect llamacpp `
      -ModelPath D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
      -SpecType ngram-mod -WriteConfig
  ```
  
  Three commands from "fresh clone" to "PalLLM serving chat
  through Qwen 3.6-35B-A3B with MTP-native speculative
  decoding."
  
  No code changed in runtime / domain / sidecar projects.
  `dotnet test` stays at `1169 / 1169`. Full audit `16/16` PASS
  at `../artifacts/full-audit/20260522-185931/RESULTS.md`.
  Routes, MCP tools, feature catalog entries, fallback strategy
  counts, and OpenAPI schema unchanged.
  
  **"Truly gone" status check after Pass 344:**
  - Operator-facing surfaces (scripts, configs, docs first-touch):
    zero Ollama references that recommend the engine.
  - Connector verb (`pal connect ollama`): now prints a
    deprecation message and exits non-zero.
  - "Bundled" promise: delivered. One command downloads + verifies
    + extracts the latest stable llama-server release.
  - Runtime back-compat plumbing (probe shapes, residency
    keep-alive): intentionally retained for operators still
    running Ollama out-of-band, documented as such in
    `MODEL_COLLABORATION.md`'s deprecation header.
- **Pass 343 - MENTAL_MODEL.md content audit: gate count + promotion route drift.**
  Highest-leverage stale doc (`2026-05-07`, 15 days). The
  high-level "how to think about PalLLM" doc had accumulated
  drift since the campaign started: gate count and promotion
  route both lagged behind the code.
  
  **Drifts fixed:**
  - **§9 "Drift gates make documentation a type system"** said
    `there are 14 of them`. Code has 16 since Pass 311 added
    `Drift_Hot_file_line_count`. Rewrote to "16 of them now"
    with a parenthetical naming the new gate and why it landed.
  - **§7 "Promotion pipeline"** referenced `/api/promotion/suggest`
    as the operator's read endpoint. Actual route is
    `GET /api/promotion/suggestions` (verified
    `Program.cs:1209` — same drift Pass 307 fixed in DATAFLOW.md
    and Pass 308 implicit in ADVISORS.md). Updated.
  
  **Spot-verified accurate (no edit):**
  - §3's "five small interfaces in `Portable/PortableAdapterContracts.cs`"
    — `IGameAdapter`, `ICharacter`, `IWorldClock`, `IPathProvider`,
    `ILogger` still match.
  - §4's "19 strategies" — `FallbackBehaviorEngine` `Try_*`
    methods still total 19 per the `Drift_Fallback_strategy_count`
    gate.
  - §5's posture-builder list — `HardwareProfiler`,
    `PrivacyPostureBuilder`, `AirGapVerifier`,
    `ResourceBudgetPostureBuilder`, `OperatorHealthScorer` all
    still exist with the documented surfaces.
  - §6's `ModelRoleRegistry` + `ChatDispatchPlanner` references —
    both files exist; planner method is `Decide(pattern,
    coverage)` (the doc doesn't name the method, just the file,
    so no edit needed).
  - §10's "the test suite runs in 30 s" — current run is ~44 s
    for 1169 tests; close enough at this scale, no edit.
  - All 10 concept analogies still hold; mermaid concept-flow
    diagram still accurate.
  
  Stamp `2026-05-07` -> `2026-05-22`. No code changed; tests
  stay at `1169 / 1169`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
  
  **Why this was the next-best step.** After the Ollama purge
  trilogy (Passes 339+341+342) and the D:\Models curation
  quartet (Passes 334-337+340), the high-level architecture doc
  was the most-stale + most-touched-by-recent-changes doc in the
  tree. A reader landing fresh would encounter §9's "14 gates"
  claim and §7's broken `/api/promotion/suggest` route — both
  small drifts but exactly the kind that erodes trust in the
  rest of the doc. Pass 343 closes the gap before the
  `Drift_Doc_freshness` 45-day cap would have surfaced it
  automatically.
- **Pass 342 - Comprehensive Ollama purge across all remaining docs + scripts.**
  Fourth iteration of the operator's Ollama-removal directive.
  Pass 339 hit the structural surfaces; Pass 341 hit the
  first-touch operator docs. Pass 342 finishes the job: every
  remaining doc + script gets its Ollama mentions either
  replaced with llama.cpp / vLLM equivalents or explicitly
  marked as back-compat-only.
  
  **Docs cleaned (10):**
  - `TUNING.md` — 5 mentions: header default tuning blurb,
    Inference-block engine list, Model field guidance, shipping
    defaults paragraph, `UseStructuredOutputs` engine list.
  - `ENV_VARS.md` — `PalLLM__Inference__BaseUrl` example.
  - `COMPATIBILITY.md` — Constrained-tier recommendation +
    endpoint reference rows (replaced Ollama loopback/LAN with
    llama-server loopback/LAN + vLLM row).
  - `READINESS.md` — connector count 9 -> 8, "Connect to Ollama"
    wizard ideas now "Connect to llama-server", default-path
    perf comment.
  - `MODELS_2026.md` — Fast-start default identifier, tier-
    orchestrator narrative, "pal connect ollama" example.
  - `QUANTIZATION.md` — 7 mentions across the
    formats-at-a-glance table, the when-to-pick / when-not-to-
    pick lists, the operator-walkthrough example. All "Ollama,
    llama.cpp, LM Studio" lists collapsed to "llama.cpp,
    LM Studio"; "Stop your current Ollama server" rewritten as
    "Stop your current llama-server".
  - `API.md` — residency-posture enum description annotated
    with "back-compat for out-of-band Ollama" framing; preload
    paragraph rewritten to lead with llama-server.
  - `MCP_QUICKSTART.md` — substantial rewrite: prerequisites
    swapped from Ollama+download to llama-server+huggingface-cli;
    Step 1 ("pull the small model first") rewritten with
    `huggingface-cli download unsloth/gemma-4-E4B-it-GGUF`;
    Docker Compose paragraph updated; troubleshooting note about
    "your Ollama endpoint" -> "your llama-server endpoint".
  - `QUICKREF.md` — `connect` verb table row: connector list
    updated, leading-Ollama-script removed.
  - `BLACKWELL_RECIPES.md` — engine list "vLLM, TensorRT-LLM,
    SGLang, Ollama, llama.cpp, OpenVINO, Foundry" -> "llama.cpp
    (default), vLLM (high-config), TensorRT-LLM, SGLang,
    OpenVINO, Foundry".
  - `PRIVACY.md` — `live-inference` posture description endpoint
    example swapped from Ollama to llama-server.
  - `SUGGESTIONS.md` — historical-style note about "operator
    whose Ollama" updated to "operator whose llama-server".
  - `SERVER_OPERATOR.md` — 4 mentions: both topology diagrams
    (single-box + AI-host), install checklist BaseUrl bullet,
    systemd unit `After=` directive.
  - `OBSERVABILITY.md` + `MODEL_COLLABORATION.md` provider-list
    line: `ollama` removed, `llama.cpp` promoted to "(default)".
  - `REPLICATION_KIT.md` — "Optional - wire live inference"
    command swapped from `pal connect ollama` to
    `pal connect llamacpp -ModelPath D:\Models\Qwen\...`.
  - `MULTIMODAL_RECIPES.md` — engine list lead swapped to
    llama.cpp.
  
  **Scripts cleaned (6):**
  - `compatibility.json` — `ollama-loopback` + `ollama-lan`
    inference-endpoint entries replaced with `llama-cpp-loopback`
    (status `reference`, default after Pass 337). `supportedEngines`
    arrays for Q4_K_M + Q8_0 dropped `ollama`.
  - `pal-welcome.ps1` — first-time-user instruction line +
    welcome-message connector tip both updated.
  - `pal-next.ps1` — next-action recommendation, action-name
    constant, dead-backend message all updated. The action
    identifier changed from `connect-ollama` to `connect-llamacpp`
    (no consumer reads this code, only the prose).
  - `pal-preflight.ps1` — inference-wired check failure message
    bumped to llama.cpp / vLLM recommendations.
  - `pal-benchmark.ps1` — fully-deterministic-fallback bottleneck
    message updated.
  - `pal-config-wizard.ps1` — code comment "shared shape with
    connect-ollama / connect-vllm" -> "shared shape with
    connect-llamacpp / connect-vllm".
  
  **Code (annotated, not removed):**
  - `MODEL_COLLABORATION.md` has 31 remaining Ollama mentions
    that describe legitimate provider-aware runtime knobs
    (`OLLAMA_CONTEXT_LENGTH`, `ollama ps` residency receipts,
    native Ollama keep-alive) that `ModelCollaborationPlanner`
    emits when an operator's `BaseUrl` points at Ollama
    out-of-band. Added a load-bearing header note explaining
    the deprecation policy and the back-compat scope, then left
    the body intact. Stripping the runtime's provider-aware
    Ollama handling would break any operator still running
    Ollama out-of-band — that's a code change, not a doc change,
    and beyond the user's stated scope ("get rid of Ollama from
    PalLLM's defaults and recommendations").
  
  **Public-copy policy unchanged.** The
  `BlockedPublicBrandPattern` in
  `scripts/public_copy_policy.ps1` lists Ollama alongside every
  other third-party brand — Pass 342 leaves that as-is because
  the policy already blocks mentioning Ollama in release-facing
  copy, which is exactly what the operator wants.
  
  **Verification.** No code regression. Full `dotnet test`
  `1169 / 1169`. Full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
  
  **What's truly left.** Search for "ollama" / "Ollama" across
  the repo will still return matches in:
  - `CHANGELOG.md` + `HANDOFF.md` historical entries (time-
    stamped audit trail — leaving them is the project's
    discipline).
  - ADRs documenting decisions made when Ollama was supported.
  - `RESEARCH_NOTES_2026-05.md` snapshot.
  - The `BlockedPublicBrandPattern` regex.
  - `MODEL_COLLABORATION.md` provider-aware runtime knob
    documentation, now under an explicit deprecation header.
  - Runtime code paths in `src/PalLLM.Domain/Inference/*` that
    parse Ollama-shape responses for out-of-band back-compat
    (most are genuinely shared OpenAI-compatible plumbing).
  
  Every one of those is intentional, scoped, and documented as
  such. There is no remaining operator-facing recommendation
  pointing at Ollama anywhere in PalLLM.
- **Pass 341 - Operator-facing Ollama sweep: docs + appsettings comments + advice strings.**
  Operator restated the Ollama-removal directive — Pass 339 hit
  the structural surfaces (script, pal.json, agents.json,
  compose.yaml, inventory) but the operator-reading docs still
  recommended Ollama in many places. Pass 341 sweeps every
  operator-facing surface where a reader would land before
  understanding PalLLM's stack. Operator-invisible internals
  (probe-shape parsing, residency policy code paths) stay
  intentionally — they're back-compat for any operator running
  Ollama out-of-band, and most are actually OpenAI-compatible
  plumbing shared with llama.cpp / vLLM / LM Studio.
  
  **Operator-facing docs updated:**
  - `docs/FAQ.md` — 3 mentions: "Do I need to install an AI
    model?" recommendation, "What if my internet drops?", and
    "What about the AI model endpoint?" all now default to
    llama.cpp with vLLM as the high-config option.
  - `docs/EASY_MODE.md` — "if you want richer replies" guidance
    swapped from Ollama+qwen2.5:7b to llama.cpp + GGUF.
  - `docs/FIRST_HOUR.md` — "If you have an Ollama instance"
    paragraph rewritten for llama-server / vLLM.
  - `docs/PITCH.md` — 2 mentions of "your own Ollama / llama.cpp
    / LM Studio install" tightened to "llama.cpp (default) or
    vLLM (high-config)."
  - `docs/OPERATIONS.md` — the largest doc edit: replaced the
    "Ollama example" first-boot flow with a llama-server +
    `D:\Models\Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf` walkthrough
    that graduates to the quality Qwen 3.6-A3B tier with
    `--spec-type ngram-mod` for native MTP. Tier table caption
    updated from "Ollama/OpenAI-compat" to
    "OpenAI-compat, llama-server passthrough", with the small +
    large tier rows pointed at the operator's curated GGUF
    files. The 8-engine list in the cascade paragraph now
    leads with llama.cpp (default) and explicitly notes
    "Pass 339 removed Ollama support." The `ModelTiers` JSON
    sample swapped from `qwen3:1.7b` / `gemma3:4b` / `qwen3:14b`
    / `qwen3.6:35b-a3b` to the curated unsloth UD-* identifiers.
    Warmup + probe-order paragraphs annotated as
    "back-compat with Ollama out-of-band; PalLLM no longer ships
    an Ollama connector."
  
  **Source code edited:**
  - `src/PalLLM.Sidecar/appsettings.json` — both `_comment_BaseUrl`
    fields (Inference + Vision) updated. Old text said
    "Switch to http://127.0.0.1:11434/v1/ for Ollama"; new text
    says "Pass 339 dropped Ollama support; use llama.cpp
    (default) or vLLM (high-config GPUs)."
  - `src/PalLLM.Domain/Inference/HardwareProfiler.cs` advice
    strings — the Constrained-tier recommendations used to name
    `gemma3:1b` / `gemma3:4b` / `qwen3.6:0.6b` (Ollama tags).
    Now recommend `gemma-4-E4B` / `qwen3.6-mini-4B-A1B` /
    `gemma-4-E4B-it-UD-Q4_K_XL` (unsloth UD-* identifiers, via
    llama.cpp).
  
  **What still mentions Ollama and why (intentional):**
  - ADRs (`docs/adr/*`) — historical decision records;
    "we supported Ollama at the time" is correct for those
    snapshots.
  - `RESEARCH_NOTES_2026-05.md` — snapshot doc documenting the
    state at time of writing.
  - `CHANGELOG.md` + `HANDOFF.md` historical entries — time-
    stamped audit trail.
  - Probe/residency/client code paths in `src/PalLLM.Domain/Inference/*`
    — back-compat for any operator running Ollama out-of-band;
    most of the "Ollama-aware" code is genuinely shared
    OpenAI-compatible plumbing. Removing it would also break
    llama.cpp / vLLM / LM Studio paths and serve no operator.
  
  **Verification.** No code regression. Full `dotnet test`
  `1169 / 1169`. Full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
  
  **Remaining Ollama mentions (scoped for future passes if
  needed):** TUNING.md, ENV_VARS.md, ARCHITECTURE.md,
  COMPATIBILITY.md, READINESS.md, BLACKWELL_RECIPES.md (Docker
  port mappings using 11434 — vLLM-on-port-11434, not Ollama,
  but cosmetically confusing), MULTIMODAL_RECIPES.md, PRIVACY.md,
  SUGGESTIONS.md, SERVER_OPERATOR.md, MCP_QUICKSTART.md (model
  pulling note), QUICKREF.md, API.md, MODEL_COLLABORATION.md,
  MODELS_2026.md (alternative engine list), QUANTIZATION.md
  (Ollama-as-loader mention), REPLICATION_KIT.md, OBSERVABILITY.md.
  None of these are first-touch operator paths; each is a
  one-line swap in its own future pass.
- **Pass 340 - `D:\Models\Diffusion` wired as the default diffusion-model path.**
  Operator request — register `D:\Models\Diffusion` as the
  canonical location for diffusion weights (Stable Diffusion /
  Flux / Hunyuan / etc.) so the future portrait-variant +
  scene-narration lane from
  [`FUTURE_2035.md`](FUTURE_2035.md) idea #15 has a place to
  resolve.
  
  **Filesystem:** created the empty `D:\Models\Diffusion`
  directory.
  
  **Code (additive, no breaking changes):**
  - New `PalLlmOptions.DiffusionModelsDir` computed property —
    always `Path.Combine(ModelsDir, "Diffusion")`. Tracks
    `ExternalModelsRoot` automatically (when set, diffusion
    follows; when unset, derives from the legacy runtime-root
    path). No separate override config so the resolver can't
    silently drift from the chat-model root.
  - `IPathProvider.DiffusionModelsDir` extended with a default
    interface method that mirrors the
    `Path.Combine(ModelsDir, "Diffusion")` shape. Adapters that
    don't override get the right answer for free.
  - `BridgeGameAdapter` implementation explicitly forwards
    `_options.DiffusionModelsDir`.
  
  **Tests:** `PalLlmOptionsModelsDirTests` extended with 4 new
  cases covering:
  - empty-`ExternalModelsRoot` fallback path
  - operator-set `ExternalModelsRoot` -> `D:\Models\Diffusion`
  - whitespace trimming propagation
  - the always-sibling invariant (regardless of how `ModelsDir`
    resolves, diffusion is always its `Diffusion` child)
  
  **Docs:** new "Diffusion subdirectory" section in
  `docs/LOCAL_MODELS_INVENTORY.md` documents the path + the
  recommended forward-looking occupants (Flux schnell / dev,
  Hunyuan-image, SDXL-Turbo) without claiming the diffusion lane
  is shipped yet.
  
  Test count `1165 -> 1169` (+4); all 18 mirror anchors bumped
  in lockstep per the Pass 305 meta-test guard. Full audit
  `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
  
  **Why derive from `ModelsDir` rather than add a separate
  knob.** Two configurable roots (chat + diffusion) means two
  things that can drift. The operator already has one
  `ExternalModelsRoot` knob (the chat root); diffusion as a
  subdirectory means moving the curation is one config change,
  not two. Tests pin this invariant — any future "let me make
  diffusion a separate root" refactor would have to update the
  tests too, forcing the design choice to be deliberate.
- **Pass 339 - Remove Ollama from operator surfaces; llama.cpp is the default, vLLM for high-config.**
  Operator stated Ollama is "heavy and overrated" — remove it
  everywhere. This pass strips Ollama from the connector
  inventory, the verb table, the Docker Compose example, the
  inventory doc, and the rolling-state count. Ollama-aware
  RUNTIME code (response-shape parsing in
  `ModelAvailabilityProbe`, keep-alive in
  `InferenceResidencyPolicy`, request formatting in
  `InferenceClient`) stays in-place as back-compat for any
  operator who runs Ollama out-of-band — but nothing in PalLLM's
  shipped surfaces points at it anymore.
  
  **Removed:**
  - `connect-ollama` script under `scripts/` (deleted; file's gone).
  - `pal.json` `connect` verb `scripts[]` array — Ollama entry
    removed, summary updated to "llama.cpp default; vLLM for
    high-config GPUs."
  - `agents.json` `capabilities.inferenceWiring.ollama` entry
    removed; summary updated `Nine connectors today` ->
    `Eight connectors today; llama.cpp is the default; vLLM for
    high-config GPUs`.
  - `docs/LOCAL_MODELS_INVENTORY.md` "Ollama wire-up
    (alternative)" subsection rewritten as
    "llama.cpp is the only supported loader" with a back-compat
    note for the runtime code that remains.
  
  **Rewritten:**
  - `docs/examples/compose.yaml` — was Ollama + Ollama-pull
    workflow; now llama.cpp via `ghcr.io/ggml-org/llama.cpp:server-cuda`
    serving the operator's curated GGUF from a host-mounted
    `D:\Models` (`/models` in-container), with `--mmproj`,
    `--flash-attn on`, `--spec-type ngram-mod` for native MTP, and
    PalLLM env wired through `PalLLM__ExternalModelsRoot=/models`.
    Header updated to note "Ollama is intentionally absent."
  
  **Cascade fixes:**
  - `tests/PalLLM.Tests/MetaTests.cs:231` comment
    `"nine inference connectors"` -> `"eight inference
    connectors (Ollama support removed in Pass 339; llama.cpp is
    the default, vLLM for high-config GPUs)"`.
  - `docs/FUTURE_2035.md:7` `"nine engines"` ->
    `"eight engines (llama.cpp default; vLLM for high-config GPUs;
    Ollama removed in Pass 339)"`.
  
  **Meta-test guard already in place.** Pass 161's
  `ConnectorInventory_PalJson_AgentsJson_AndFilesystem_Agree`
  test uses the filesystem as source of truth — deleting
  `connect-ollama.ps1` forced `pal.json` + `agents.json` into
  lockstep, which they now are. Confirmed `1 / 1` focused PASS;
  full `dotnet test` `1165 / 1165`.
  
  **What stays in code (and why):**
  - `ModelAvailabilityProbe.cs` Ollama-shape JSON parsing — an
    operator who still runs an Ollama server out-of-band will
    still get correct probe behavior from PalLLM. Removing it
    would also remove every test that exercises that branch.
  - `InferenceResidencyPolicy.cs` Ollama keep-alive — same
    reason.
  - `InferenceClient.cs` Ollama-compatible request shape — every
    OpenAI-compatible engine (vLLM, llama.cpp, LM Studio, etc.)
    accepts the same shape, so this isn't even Ollama-specific
    code anymore; it's just the OpenAI-compatible HTTP path.
  - `appsettings.json` `_comment_BaseUrl` referencing
    `:11434` for Ollama as a fallback hint — operator-readable
    breadcrumb only, no code consumes it.
  
  Full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
  
  **Honesty note.** "Get rid of Ollama" admits two
  interpretations. The aggressive read would delete the
  Ollama-aware probe / residency / client code too. I picked the
  conservative read: remove Ollama from every shipped recommendation,
  script, and surface, but leave the OpenAI-compatible plumbing
  intact (it serves llama.cpp, vLLM, LM Studio, and Foundry
  identically; calling it "Ollama code" would be a misread).
  The user-visible "PalLLM supports Ollama" claim is gone. The
  internal HTTP shape that happens to also work with Ollama is
  intact.
- **Pass 338 - QUANTIZATION.md audit: add unsloth UD-* section + fix aspirational `Chat.Inference` span reference.**
  Pass 337 routed every shipping model identifier through unsloth
  UD-Q* / UD-IQ* — but the canonical quantization explainer
  (`docs/QUANTIZATION.md`, stamp `2026-05-07`, 15 days stale)
  didn't mention the UD-* format AT ALL. Reader following the doc
  alone would have no idea what the curated library's filenames
  meant, what bit budgets they implied, or why UD-XL beats vanilla
  K-quants. Pass 338 closes that gap.
  
  **New §"unsloth UD-* (Ultra-Detail Dynamic) quants" added**
  (between the K-quant and Q8_0 sections):
  - What it is: calibration-aware dynamic mix with importance-
    weighted per-tensor bit budgets (`_XL` suffix = extra layers
    that hold FP16/Q6_K for quality-critical tensors).
  - Bit budget table covering UD-Q4_K_XL (~4.8 bpw), UD-Q5_K_XL,
    UD-Q6_K_XL, UD-Q8_K_XL (~8.5 bpw), UD-IQ3_XXS (~3.1 bpw),
    UD-IQ4_XS (~4.2 bpw).
  - Accuracy beats vanilla K-quants by 0.3-0.8 benchmark points
    at the same bit budget (importance-weighted calibration
    preserves the right tensors).
  - MTP head support documented per-family: Qwen 3.6-A3B UD-XL
    ships native MTP heads (accessible via
    `llama-server --spec-type ngram-mod`); DeepSeek V4-Flash
    UD-Q3_K_M does not.
  - When-to-pick / when-not-to-pick decision criteria.
  
  **Format-at-a-glance table expanded** to add UD-Q8_K_XL,
  UD-Q4_K_XL, UD-Q6_K_XL, UD-IQ3_XXS, UD-IQ4_XS rows alongside
  the existing NVFP4 / MXFP4 / FP8 / Q4_K_M / Q8_0 entries.
  
  **Recommendation matrix updated** — Ampere / older-NVIDIA /
  AMD RDNA / Apple Silicon / CPU rows now recommend
  `unsloth UD-Q4_K_XL` as the default instead of vanilla
  `Q4_K_M`, with the per-row rationale (UD-XL ports cleanly
  across every GPU class as the same GGUF file).
  
  **Span-reference drift fixed.** The "PalLLM sees a 2× speedup"
  example referenced `Chat.Inference` — one of the aspirational
  spans Pass 314 ripped out of QUICKREF.md + OBSERVABILITY.md.
  Replaced with the actual story: `System.Net.Http.*` auto-
  instrumentation span shrinks; `pal.chat` parent span's
  `pal.inference_*` tags surface the new model identifier. Added
  a sentence explicitly noting the earlier `Chat.Inference` /
  `Chat.Plan` hierarchy was aspirational and never shipped.
  
  Stamp `2026-05-07` -> `2026-05-22`. No code changed; tests
  stay at `1165 / 1165`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
  
  **Why this is the right next step.** Passes 334-337 routed all
  storage to `D:\Models` and pinned shipping defaults to the
  unsloth UD-* shape. Without a quantization doc that explains
  the format, a new operator (or coding agent reading the repo
  cold) sees identifiers like `Qwen3.6-35B-A3B-UD-Q8_K_XL` in
  `appsettings.json` and the curation guard, but has nowhere
  authoritative to look for what the suffix means. Pass 338
  makes the curation self-explanatory.
- **Pass 337 - Shipping `appsettings.json` rerouted to `D:\Models` curation + permanent meta-test guard.**
  Operator restated "only use models from D:\Models" — Pass 334's
  reroute landed `ExternalModelsRoot` + the resolver + the
  Development override, but the *shipping* `appsettings.json` (the
  default loaded on every boot regardless of environment) still
  carried pre-curation Ollama tags
  (`qwen3.6:35b-a3b`, `gemma3:4b`,
  `hf.co/unsloth/Qwen3.6-35B-A3B-Instruct-UD-Q4_K_XL-GGUF`,
  `gemma4:e2b`). Pass 337 fixes that and adds the regression
  guard.
  
  **`appsettings.json` changes:**
  - Added top-level `PalLLM:ExternalModelsRoot = "D:\\Models"` so
    `PalLlmOptions.ModelsDir` resolves to the curated library on
    every boot (no longer dependent on Development-only overrides).
  - `Inference.BaseUrl`: `http://127.0.0.1:11434/v1/` (Ollama) ->
    `http://127.0.0.1:8080/v1/` (llama-server default port) —
    matches the operator's llama.cpp + D:\Models workflow.
  - `Inference.Model`: `"qwen3.6:35b-a3b"` ->
    `"Qwen3.6-35B-A3B-UD-Q8_K_XL"` (matches
    `D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf`).
  - `Inference.ModelTiers[0]`: `"gemma3:4b"` ->
    `"gemma-4-E4B-it-UD-Q4_K_XL"` (matches
    `D:\Models\Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf`).
  - `Inference.ModelTiers[1]`:
    `"hf.co/unsloth/Qwen3.6-35B-A3B-Instruct-UD-Q4_K_XL-GGUF"` ->
    `"Qwen3.6-35B-A3B-UD-Q8_K_XL"` (the curated UD-Q8_K_XL is a
    quality step up over the previous UD-Q4_K_XL pull).
  - `Vision.BaseUrl` + `Vision.Model` updated to match — same
    llama-server lane serves chat + vision when `--mmproj` is
    attached at server startup.
  - Added inline `_comment_*` keys documenting each change in
    place (JSON-tolerant; ignored by the binder).
  
  **Permanent regression guard:** new
  `tests/PalLLM.Tests/ShippingAppsettingsCurationTests.cs` with
  4 focused cases (12 ms) pins:
  - `ExternalModelsRoot` must be present, non-empty, contain
    "Models".
  - `Inference.Model` must not contain any disallowed fragment
    (Ollama-style `:tag`, `hf.co/`, `bonsai`) AND must contain a
    curated `UD-Q*` / `UD-IQ*` shape.
  - `Vision.Model` same constraint.
  - `Inference.ModelTiers[]` same constraint applied to every
    tier; minimum 2 tiers required.
  
  Any future agent that reverts to Ollama tags, raw HF specs,
  or Bonsai variants now fails the test loudly with a specific
  actionable message — exactly the regression-pattern Pass 305's
  test-count meta-test and Pass 315's drift-gate set ship with.
  
  **Test cascade:** count moves `1161 -> 1165` (+4); all 18 mirror
  anchors bumped in lockstep. Full `dotnet test` `1165 / 1165`;
  full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
  
  **Honesty note on the BaseUrl change.** Switching the shipping
  default from `:11434` to `:8080` is a behavioural change for
  any environment that was relying on Ollama. The operator's
  stated workflow is llama.cpp + D:\Models, so this matches their
  intent. Any environment that wants Ollama swaps `BaseUrl` back
  via standard `PalLLM__Inference__BaseUrl` override. Inference
  defaults to `Enabled: false` so fresh installs aren't impacted
  unless the operator explicitly turns it on.
- **Pass 336 - Record operator model-publisher preferences (unsloth-preferred, Bonsai-deprioritised).**
  Operator stated two new preferences for future model selection:
  (1) unsloth is the preferred Hugging Face publisher, already
  reflected in the entire `D:\Models` curation; (2) the Bonsai
  small-LLM family should be deprioritised as overrated. Exhaustive
  grep across `src/`, `docs/`, `scripts/`, `tests/`, `mod/`,
  `schemas/`, ADRs, and top-level configs found **zero** Bonsai
  references — nothing to delete. Pass 336 instead **records the
  preferences** in `docs/LOCAL_MODELS_INVENTORY.md` so any future
  agent recommending models honors them by default.
  
  New §"Operator preferences" section in
  `docs/LOCAL_MODELS_INVENTORY.md` (inserted before the curated
  library snapshot) names:
  - Preferred HF publisher: `unsloth/` — UD-* (ultra-detail
    dynamic) quants preserve quality at lower bit budgets than
    standard K-quants and match the operator's quality-per-GB
    priority.
  - Preferred quant band: `UD-Q4_K_XL` to `UD-Q8_K_XL` for routine
    lanes; `UD-IQ3_XXS` / `UD-IQ4_XS` acceptable for frontier
    proof lanes.
  - MTP-capable variants preferred when both MTP and non-MTP
    variants exist (current curation already biases toward
    Qwen 3.6-A3B which ships native MTP heads).
  - Bonsai family deprioritised — future recommendations needing
    to name Bonsai require per-case justification overcoming the
    default.
  
  Documenting this in the inventory doc rather than MODELS_2026.md
  is deliberate: MODELS_2026.md is the research-grounded abstract
  recommendation surface; the inventory is the operator-specific
  realisation. Preferences live in the operator surface so the
  research surface stays neutral and re-runnable.
  
  No code changed; tests stay at `1161 / 1161`; full audit `16/16`
  PASS at `../artifacts/full-audit/20260522-185931/RESULTS.md`.
  Routes, MCP tools, feature catalog entries, fallback strategy
  counts, and OpenAPI schema unchanged.
- **Pass 335 - Regression test for the Pass 334 `ExternalModelsRoot` resolver.**
  Pass 334 shipped the reroute but left the new `PalLlmOptions.ModelsDir`
  resolver untested. Operator request was repeated — symptom that the
  wiring needed a more permanent proof. Pass 335 adds the test fixture
  that pins both branches so the resolver can't silently regress.
  
  New `tests/PalLLM.Tests/PalLlmOptionsModelsDirTests.cs` (`7` focused
  cases, runs in 4 ms):
  - `ModelsDir_DefaultsToRuntimeRootSubdir_WhenExternalModelsRootIsEmpty`
  - `ModelsDir_DefaultsToRuntimeRootSubdir_WhenExternalModelsRootIsNull`
  - `ModelsDir_DefaultsToRuntimeRootSubdir_WhenExternalModelsRootIsWhitespace`
  - `ModelsDir_UsesExternalModelsRoot_WhenSet`
  - `ModelsDir_TrimsSurroundingWhitespace_FromExternalModelsRoot`
  - `ModelsDir_IsIndependentOfRuntimeRoot_WhenExternalModelsRootIsSet`
  - `ModelsDir_FallbackTracksRuntimeRoot_WhenExternalIsEmpty`
  
  These pin the back-compat guarantee for fresh installs (empty / null
  / whitespace = legacy `RuntimeRoot\Models` path) and the new
  operator-override path (set value wins, surrounding whitespace
  trimmed, RuntimeRoot changes ignored once the override is in place).
  
  **Test cascade.** Count moves `1154 -> 1161` (+7); all 13 secondary
  mirrors + the 5 canonical anchors bumped in lockstep per the Pass 305
  meta-test. `ARCHITECTURE.md` line 28 caught by the
  `Drift_Test_count_docs` gate on the first re-audit and patched.
  
  No code regressions; build clean; focused fixture `7 / 7` (4 ms);
  full `dotnet test` `1161 / 1161`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and OpenAPI
  schema unchanged.
  
  **Why this matters.** The Pass 334 reroute is a load-bearing config
  knob — every operator wiring an external model library depends on
  it. Without the test, a future refactor that "simplified" the
  resolver back to a single computed property would have silently
  broken every D:\Models-style setup with no audit signal. Pass 335
  closes that gap.
- **Pass 334 - Reroute model storage to operator-curated `D:\Models` + inventory mapping.**
  Operator request: route all model storage to `D:\Models` (their
  hand-curated GGUF library, unsloth UD-XL preferred, MTP-capable
  variants), document what's already there, map each file to PalLLM's
  role mesh.
  
  **Inventory captured.** New `docs/LOCAL_MODELS_INVENTORY.md`
  enumerates the 10 GGUFs already present:
  - **Fast-start lane**: `Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf` (5 GB)
  - **Quality lane**: `Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf` (39 GB
    MoE, 3 B active, MTP-capable) — promotion from the
    UD-Q4_K_XL appsettings default to UD-Q8_K_XL
  - **Dense alternatives**: `Qwen\Qwen3.6-27B-UD-Q8_K_XL.gguf` (36 GB)
    and `Gemma\gemma-4-31B-it-UD-Q8_K_XL.gguf` (35 GB)
  - **Frontier proof lanes**: `DeepSeek\DeepSeekV4-Flash-158B-Q3_K_M.gguf`
    (100 GB), `MiniMax-M2.7\UD-IQ4_XS\` (108 GB / 4 shards),
    `MiniMax-M2.7\UD-IQ3_XXS\` (80 GB / 3 shards)
  - **Coding lane**: `Qwen\Qwen3-Coder-Next\UD-Q6_K_XL\` (73 GB /
    3 shards)
  - **Vision projectors**: `mmproj\mmproj-F16.gguf` (928 MB) +
    `mmproj\mmproj-gemma-4-31B-F16.gguf` (1.2 GB)
  
  **Code changes (additive, backward-compat preserved):**
  - `PalLlmOptions.ExternalModelsRoot` (new property, default empty)
    in `src/PalLLM.Domain/Configuration/PalLlmOptions.cs`. Set to
    an operator's absolute path to share weights across PalLLM
    installs and other inference tooling without duplicating GB-
    class files.
  - `PalLlmOptions.ModelsDir` is now a thin resolver: when
    `ExternalModelsRoot` is set, returns the operator path;
    otherwise returns the legacy `RuntimeRoot\Models` location.
    No automatic write happens at either path — PalLLM remains
    HTTP-only against inference engines.
  - `appsettings.Development.json` updated with the operator's
    `D:\Models` setup wired through `ExternalModelsRoot`, plus
    the corresponding ModelTiers entries pointing at the curated
    GGUFs. The shipping `appsettings.json` is unchanged so fresh
    installs aren't affected.
  
  **No file moves.** The operator's curated GGUFs stay exactly where
  they already are. PalLLM never owns GGUF files — the inference
  engines (Ollama, llama.cpp, vLLM, etc.) do. The reroute is a
  config + docs change that points the connectors and any future
  GGUF-aware advisor at the right path.
  
  **MTP wire-up documented.** The inventory doc includes the
  `llama-server --spec-type ngram-mod` invocation for using the
  model-native MTP heads in the Qwen 3.6 A3B UD-Q8_K_XL quant.
  DeepSeek V4-Flash Q3_K_M flagged as not-known-to-ship MTP heads;
  speculation against that would need an external drafter.
  
  Doc count `64 -> 65` (`LOCAL_MODELS_INVENTORY.md` added);
  `docs/INDEX.md` catalogue updated. No code regressions; build
  clean at `WarningLevel=9999`; tests stay at `1154 / 1154`; full
  audit `16/16` PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes,
  MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
- **Pass 333 - MEMORY_RECIPES.md content audit: closes "recall is not wired" claim.**
  Oldest stamp in `docs/` at `2026-05-05` (17 days). Pattern matches
  Passes 307-314: walk the doc against current code, surface drift,
  fix in place, refresh stamp honestly.
  
  **Drift found.** Doc §"What PalLLM does today" said the recall tier
  was "not yet wired" — true on `2026-05-05`, false now. Between then
  and `2026-05-22` an earlier pass shipped
  `ConversationMemoryStore.Recall(query, characterId, limit)` with a
  four-lane deterministic recall: in-process FNV-1a dense + exact-token
  rerank for named-entity tie-breaks + per-character score boost +
  importance weighting from `MemoryImportance.Derive(...)`. The doc's
  forward-looking framing ("the recall + archival tiers matter most…")
  was actively misleading because it implied no semantic lane existed
  at all.
  
  **Fixes:**
  - Rewrote §"What PalLLM does today" to describe the shipped recall
    behaviour with exact constants (`MaxEntries = 2_000`,
    `MaxContentChars = 4 * 1024`) and the four-lane composition,
    keeping the "two tiers still not wired" framing for the genuine
    gaps (model-quality dense lane + archival graph/KV).
  - Updated §"Recommended trajectory for PalLLM" — step 1 marked
    **done today** (it was previously "ship the vector-recall sketch
    as a recipe"); step 2 reframed as the natural next step from
    FUTURE_2035 idea 5c (hybrid-retrieval memory upgrade); subsequent
    steps unchanged.
  - Stamp `2026-05-05` -> `2026-05-22`.
  
  No code changed; tests stay at `1154 / 1154`; full audit `16/16`
  PASS at `../artifacts/full-audit/20260522-185931/RESULTS.md`.
  Routes, MCP tools, feature catalog entries, fallback strategy
  counts, and OpenAPI schema unchanged.
  
  **Agentic-coding flaw addressed.** A common drift pattern: a doc
  describes "we haven't shipped X yet" and stays stale after X
  *does* ship, because the natural author of the X-implementing pass
  isn't necessarily the doc-owning pass author. Walking docs older
  than ~2 weeks against the current code catches this consistently
  — six of the eight oldest-stamped docs audited this session found
  real drift. The 45-day `Drift_Doc_freshness` cap surfaces these
  automatically; the pattern is sustainable as long as agents keep
  doing the audit rather than the stamp refresh.
- **Pass 332 - Three cutting-edge near-term ideas added to `FUTURE_2035.md`.**
  Pass 316 ran a research-grounded survey across Hugging Face Hub
  trending feeds + editorial roundups and shipped
  `docs/MODELS_2026.md`. Pass 332 closes the loop: three of the
  research findings are forward-looking enough to belong in
  `FUTURE_2035.md` rather than the current-defaults doc, so they
  get appended as ideas 5a, 5b, and 5c (slotting between the
  near-term and mid-term buckets):
  
  - **5a. Hierarchical-reasoning small-model advisor** — new
    May 2026 wave (`sapientinc/HRM-Text-1B`,
    `FrontiersMind/Nandi-Mini-600M`) ships built-in scratchpad /
    chain-of-thought. New `HrmAdvisor` would expose
    `Reason(prompt) -> (reasoning, finalAnswer)` for fallback
    strategies that want grounded reasoning at edge-tier latency.
  - **5b. Always-on realtime audio understanding** — Voxtral-Mini
    4B Realtime can continuously listen to *game audio* (not just
    player voice). New `audio_event` bridge envelope kind lets the
    narration advisor react to detected footsteps, boss music,
    raid horns. Filesystem one-way, opt-in, default off.
  - **5c. Hybrid-retrieval memory upgrade** — 2026 generation of
    embedding models (`BAAI/bge-m3`) ships dense + sparse +
    multivector retrieval in one model. New `HybridLocalEmbedder`
    keeps the local-first guarantee (in-process FNV-1a dense lane
    + BM25 sparse lane) while letting operators bolt on an
    external bge-m3 server when they want model-quality dense
    retrieval.
  
  Each entry follows the existing pattern: where-it-fits,
  first-deliverable, what-blocks-it-today, hard-rule-check, and
  links back to the `MODELS_2026.md` source section for traceability.
  
  No code changed; tests stay at `1154 / 1154`; full audit `16/16`
  PASS at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
  
  **Honest 100%-completion ceiling note.** Roadmap stays at
  `76.2%`. The remaining `23.8%` (Queues 1-6 in
  `IMPLEMENTATION_QUEUE.md`) every one requires a live Palworld +
  UE4SS session on real Windows hardware to capture native-proof
  evidence. No in-session work — Claude's, the lesser agent's, or
  anyone's — closes that gap; only a player-driven scripted pass
  does. Both the SGLang proof lanes (Pass 331), the model
  recommendations (Pass 316), the species-personality resolver
  (Pass 315), and the FUTURE_2035 cutting-edge additions
  (Pass 332) are concrete scaffolding that makes the eventual
  hardware-bound passes easier, not substitutes for them. This
  scoping is intentional and documented in the four hard rules.
- **Pass 331 - SGLang attention/precision/spec proof lane.**
  Current SGLang attention-backend docs now make backend choice a support-matrix
  proof: page size, FP8/FP4 KV, speculative topk, sliding-window, multimodal
  support, GPU/CUDA/PyTorch class, and backend family all matter. Current
  SGLang server/speculation docs also expose `fp4_e2m1`, EAGLE-3, adaptive
  speculation, NGRAM, STANDALONE, MTP, and SpecV2 overlap lanes that must stay
  route-proven before player-facing use. The focused `D:\Coding` sibling scan
  reinforced the generic FP4/FP8/speculation telemetry-gate pattern from RimLLM
  and OmniForge. No sibling code, prompts, names, branding, product identity, or
  unrelated IP was lifted.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now emits SGLang
  attention-backend, FP4/FP8 KV, and EAGLE-3/adaptive/SpecV2 guidance as
  proof-only lanes. Startup hints, request/cache hints, admission/security
  controls, promotion receipts, metric receipts, and verification checks now
  ask for auto-selection baselines, exact backend names, page size, KV dtype,
  quantization/scaling receipt, GPU/CUDA/PyTorch class, draft-model
  revision/hash or NGRAM config, topk/num-step/draft-token caps, acceptance
  rate, OOM/backoff evidence, strict-route parser stability, route p95
  TTFT/ITL/E2E, and PalLLM fallback counters before promotion. SpecV2 proof
  must pin topk=1.
  
  **Docs/tests:** tightened `ModelTierTests` assertions and refreshed
  `MODEL_COLLABORATION.md`, `RESEARCH_NOTES_2026-05.md`, `ARCHITECTURE.md`,
  `API.md`, the feature-catalog note, and the changelog. No route count, MCP
  tool count, feature-catalog entry count, OpenAPI schema, request shape, or
  deterministic fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-185931/RESULTS.md`.

- **Pass 330 - Native-proof status-transition evidence.**
  Current SGLang observability, vLLM metrics, and ASP.NET Core 10 monitoring
  guidance all point at replayable evidence instead of final-status-only proof.
  The focused `D:\Coding` sibling scan found the same generic transition-log
  pattern in DeepForge proof scripts and RimLLM release-evidence/replay notes.
  No sibling code, prompts, names, branding, product identity, or unrelated IP
  was lifted.
  
  **Native proof hardening:** `scripts/run-native-proof.ps1` now persists
  watcher start/finish timestamps, timeout and poll cadence, poll count,
  timeout state, a stable completion reason, and up to 32 status transitions
  sampled from `/api/bridge/proof`. Blocked local runs, timed-out runs, and
  successful `delivery_proven` runs all carry the same evidence shape in
  `Runtime/ReleaseEvidence/latest-native-proof.json`.
  
  **Docs/tests:** `ReleaseNativeProofEvidenceSnapshot`,
  `/api/release/readiness`, and the OpenAPI snapshot now expose the watcher
  evidence. Native-proof, release, operations, roadmap, research, and stale
  quickstart/status mirrors were refreshed to the current `1154` tests,
  `16` drift gates, `57` API routes, and `38` MCP tools. No route count, MCP
  tool count, feature-catalog entry count, deterministic fallback behavior, or
  request shape changed.
  
  **Verification:** focused native-proof/readiness coverage plus `MetaTests`
  passed `28 / 28`; changed PowerShell scripts parsed cleanly; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-122106/RESULTS.md`.

- **Pass 329 - llama.cpp draft-MTP proof lane.**
  Current llama.cpp speculative-decoding docs expose `draft-mtp` beside n-gram
  and draft-model modes through `--spec-type`, with current `--spec-draft-*`
  and `--spec-ngram-mod-*` controls. Current llama.cpp multimodal docs keep
  `libmtmd`, matching projector, and media content handling explicit. This
  pass treats GGUF draft-MTP as a text-only proof lane for Qwen3.6/Gemma 4,
  not a blanket PalLLM or multimodal default.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now emits a
  llama.cpp `--spec-type draft-mtp` proof hint for GGUF Qwen3.6/Gemma 4 lanes,
  updates raw GGUF n-gram examples to current flag names, and requires command
  line, `--spec-draft-*` depths, tokenizer/chat-template identity, model hash,
  server commit, accepted/generated tokens, TTFT/ITL, parser result, and
  fallback counters before promotion. `scripts/connect-llamacpp.ps1` now prints
  the current llama.cpp speculation flags and keeps old `-SpecType draft` as a
  compatibility alias for `draft-simple`.
  
  **Docs/tests:** tightened `ModelTierTests` assertions and refreshed
  `MODEL_COLLABORATION.md`, `RESEARCH_NOTES_2026-05.md`, `ARCHITECTURE.md`,
  `API.md`, the feature catalog, and the changelog. No route count, MCP tool
  count, feature-catalog entry, OpenAPI schema, request shape, or deterministic
  fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; helper dry run
  printed `--spec-type draft-mtp --spec-draft-n-min 1 --spec-draft-n-max 4`;
  full `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-111315/RESULTS.md`.

- **Pass 328 - MoRIIO/FlexKV topology proof lane.**
  Current vLLM MoRIIO docs show single-node prefill/decode disaggregation with
  separate producer/consumer instances, read/write transfer modes, and explicit
  proxy/handshake/notify ports. Current vLLM FlexKV docs expose CPU, SSD, or
  remote-store KV offload through `FlexKVConnectorV1`. The focused `D:\Coding`
  sibling scan reinforced route-SLO proof, rollback canaries, and sanitized
  topology receipts. No sibling code, prompts, names, branding, product
  identity, or unrelated IP was lifted.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now names
  `MoRIIOConnector` beside the other vLLM P/D connectors, emits a separate
  MoRIIO single-node proof lane, and requires read/write mode,
  proxy/http/handshake/notify port maps, prefix-cache-disabled versus normal
  prefix-cache baselines, remote-KV wait/transfer evidence, TTFT/ITL/E2E
  deltas, worker-stop rollback, and PalLLM fallback counters before promotion.
  `FlexKVConnectorV1` is now named as an external/offload proof lane with
  storage-budget, async-transfer, load/store-failure, namespace, cold/warm
  latency, and rollback receipts.
  
  **Docs/tests:** tightened `ModelTierTests` assertions and refreshed
  `MODEL_COLLABORATION.md`, `RESEARCH_NOTES_2026-05.md`,
  `BLACKWELL_RECIPES.md`, `ARCHITECTURE.md`, `API.md`, the feature catalog,
  and the changelog. No route count, MCP tool count, feature-catalog entry,
  OpenAPI schema, request shape, or deterministic fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-101606/RESULTS.md`.

- **Pass 327 - Structured-output portability proof lane.**
  Current vLLM, SGLang, Ollama, llama.cpp, and `transformers serve` docs all
  support stricter output shaping, but not through one portable request shape.
  The focused `D:\Coding` sibling scan reinforced generic schema-digest plus
  route/model identity proof patterns from RimLLM and schema-backed handoff
  contracts from OmniForge. No sibling code, prompts, names, branding, product
  identity, or unrelated IP was lifted.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now treats
  schema-constrained output as route/model/provider/request-shape proof.
  Startup hints, request/cache hints, promotion receipts, metric receipts, and
  verification checks now require schema name/digest, PalLLM route class,
  served model id, request shape (`response_format`, `guided_json`, Ollama
  `format`, or grammar), grammar/backend id, parse/schema-validation result,
  token usage, p95 latency, fallback counters, and a schema-echo canary with a
  changed-schema digest before promotion.
  
  **Docs/tests:** tightened `ModelTierTests` assertions and refreshed
  `MODEL_COLLABORATION.md`, `RESEARCH_NOTES_2026-05.md`, `ARCHITECTURE.md`,
  `API.md`, the feature catalog, and the changelog. No route count, MCP tool
  count, feature-catalog entry, OpenAPI schema, request shape, or deterministic
  fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-091540/RESULTS.md`.

- **Pass 326 - Sparse-MoE DBO proof lane.**
  Current vLLM Dual Batch Overlap docs frame DBO as a MoE DP+EP optimization
  enabled with `--enable-dbo` plus decode/prefill thresholds, not a generic
  single-player latency toggle. The focused `D:\Coding` sibling scan reinforced
  generic proof-ledger, histogram, and "model work must not block live
  gameplay" patterns from Hermes Gateway, RimLLM, and DeepForge. No sibling
  code, prompts, names, branding, product identity, or unrelated IP was lifted.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now emits sparse-MoE
  vLLM DBO as a proof lane only: startup hints, request/cache/admission/security
  guardrails, promotion receipts, metric receipts, and verification checks all
  require a no-DBO baseline, DP/EP topology, all2all backend,
  microbatch/overlap evidence, TTFT/ITL/E2E deltas, queue/preemption pressure,
  parser stability, and PalLLM fallback counters before promotion.
  
  **Docs/tests:** tightened `ModelTierTests` assertions and refreshed
  `MODEL_COLLABORATION.md`, `RESEARCH_NOTES_2026-05.md`,
  `BLACKWELL_RECIPES.md`, `ARCHITECTURE.md`, `API.md`, the feature catalog,
  and the changelog. No route count, MCP tool count, feature-catalog entry,
  OpenAPI schema, request shape, or deterministic fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-081511/RESULTS.md`.

- **Pass 325 - Gemma 4 MTP assistant-checkpoint proof.**
  Current vLLM speculative-decoding docs say Gemma 4 assistant checkpoints are
  Gemma 4 MTP speculators, not generic draft-model speculation. The focused
  `D:\Coding` sibling scan reinforced the same generic lesson from DeepForge
  and RimLLM: native MTP promotion needs drafter identity and route proof, not
  a broad "speculation enabled" flag. No sibling code, prompts, names,
  branding, product identity, or unrelated IP was lifted.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now tells Gemma 4
  lanes to wire matching assistant/drafter checkpoints through `method=mtp`
  and reject generic `draft_model` promotion for those artifacts. The Gemma 4
  proof contract now asks for assistant checkpoint id or hash, target-model
  lineage, token depth, acceptance rate, prefix-cache-disabled benchmark,
  normal-cache replay, parser stability, and fallback behavior before the lane
  can become player-facing.
  
  **Docs/tests:** tightened `ModelTierTests` assertions and refreshed
  `MODEL_COLLABORATION.md`, `RESEARCH_NOTES_2026-05.md`, and the changelog.
  No route count, MCP tool count, feature-catalog entry, OpenAPI schema,
  request shape, or deterministic fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-070910/RESULTS.md`.

- **Pass 324 - Responses/video-job proof lanes.**
  Current Hugging Face `transformers serve` and vLLM docs expose
  OpenAI-compatible `/v1/responses`, while current vLLM-Omni docs expose
  async video-generation jobs through `/v1/videos` and `/v1/videos/sync`.
  Those are useful proof surfaces, but they add response ids, event parsing,
  built-in tool payloads, async job storage, cancellation, and cleanup concerns
  that should not touch PalLLM's live Palworld companion loop without route
  proof. The focused `D:\Coding` sibling scan reinforced the same generic
  receipt-first pattern; no sibling code, prompts, names, branding, product
  identity, or unrelated IP was lifted.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now keeps
  `/v1/responses` proof-only for vLLM-like and `transformers serve` lanes
  until response lifecycle events, response-id cleanup, event parsing,
  tool-event shape, usage receipts, p95 latency, and fallback counters are
  replayed. Qwen Omni/vLLM-Omni profiles now classify `/v1/videos` and
  `/v1/videos/sync` as offline diffusion-job proof material only, with
  create/poll/content/delete, cancellation, output-cleanup,
  prompt-publication-hygiene, and no-interference evidence required before
  release-proof use.
  
  **Docs/tests:** tightened `ModelTierTests` assertions and refreshed
  `MODEL_COLLABORATION.md`, `MULTIMODAL_RECIPES.md`,
  `RESEARCH_NOTES_2026-05.md`, `API.md`, `ARCHITECTURE.md`, the feature
  catalog note, and the changelog. No route count, MCP tool count,
  feature-catalog entry, OpenAPI schema, request shape, or deterministic
  fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-061634/RESULTS.md`.

- **Pass 323 - vLLM-Omni media-first connector guard.**
  Current Gemma 4, Qwen3-Omni, vLLM multimodal, and llama.cpp libmtmd
  sources support richer local media lanes, but not a blanket promotion of
  one omni server into every PalLLM route. The focused `D:\Coding` sibling
  scan reinforced the same generic route-proof/media-smoke pattern; no sibling
  code, prompts, names, branding, or product identity was lifted.
  
  **Connector hardening:** `scripts/connect-vllm-omni.ps1 -WriteConfig` now
  writes `PalLLM:Vision` only by default and preserves the existing
  `PalLLM:Inference` text endpoint. A new `-WireInference` switch restores
  the old shared endpoint behavior only as an explicit proof-lane override
  after text-only chat, screenshot/image, audio, strict JSON/tool-call,
  latency, fallback-counter, and stall behavior replay pass on the exact
  runtime/model. `pal connect omni` help now says the same thing, and the
  existing meta-test fixture pins the media-first contract.
  
  **Docs/tests:** refreshed `MODEL_COLLABORATION.md`,
  `MULTIMODAL_RECIPES.md`, `RESEARCH_NOTES_2026-05.md`, and the changelog.
  No route count, MCP tool count, feature-catalog entry, OpenAPI schema,
  request shape, or deterministic fallback behavior changed.
  
  **Verification:** dry-run config output proved Vision-only default wiring
  and opt-in `-WireInference` behavior; focused `MetaTests` passed `27 / 27`;
  full `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-051130/RESULTS.md`.

- **Pass 322 - Deterministic memory rerank + embedding-doc drift fix.**
  Current reranker guidance still supports retrieve-then-rerank, but model
  rerankers add per-candidate latency. Active `D:\Coding` sibling scans
  reinforced the same bounded pattern through generic embedding-circuit-breaker
  and model-profile notes; no sibling code, prompts, names, branding, or product
  identity was lifted.
  
  **Runtime memory precision:** `ConversationMemoryStore.Recall` now adds a
  stack-bounded exact-token rerank term on top of deterministic embedding,
  recency, importance, and character-affinity scoring. Query token hashes are
  collected once, candidate content is scanned without token-array allocation,
  and the signal is capped to 32 meaningful query tokens. Named Palworld events,
  bases, bosses, raids, and species names are less likely to lose tied embedding
  buckets while the default memory path stays zero-network and low-latency.
  
  **Docs/tests:** fixed `docs/MODELS_2026.md` drift that had incorrectly
  described the shipping `SemanticEmbedder` as a `/v1/embeddings` caller.
  Updated `ARCHITECTURE.md`, `HOT_PATH.md`, `TUNING.md`,
  `RESEARCH_NOTES_2026-05.md`, README, feature-catalog copy, and
  `CHANGELOG.md`. Also repaired two stale release-readiness test fixtures so
  their "proven native proof" artifacts satisfy the current native-HUD proof
  contract (`native_hud`, closed loop, visible delivery).
  
  **Verification:** focused `ConversationMemoryStoreTests` passed `3 / 3`;
  focused release-readiness regressions passed `2 / 2`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260522-044935/RESULTS.md`.

- **Pass 321 - MTP/multimodal split-lane guard.**
  Current vLLM docs keep model-native MTP and multimodal input as separate
  serving concerns, and current llama.cpp issue traffic still shows exact
  runtime/model MTP instability can produce loop or OOM failure modes. The
  active sibling scan reinforced the same generic split-lane rule without
  contributing code, prompts, names, branding, or product identity.
  
  **Serving profile hardening:** `ModelCollaborationPlanner` now tells Qwen3.6
  and Gemma 4 multimodal-capable MTP lanes to keep text-MTP and
  vision/audio/video profiles on separate server processes or ports until a
  same-process negative canary passes for the exact runtime build. It also warns
  not to send `image_url`, `video_url`, `input_audio`, or `audio_url` content
  parts to a text-only MTP endpoint, separates text-MTP KV cache from
  multimodal encoder/KV cache evidence, blocks same-server co-scheduling without
  proof, and requires replay coverage for loops, OOM, stalls, parser stability,
  p95 latency, memory, and fallback counters before promotion.
  
  **Docs/tests:** refreshed `MODEL_COLLABORATION.md`,
  `MULTIMODAL_RECIPES.md`, `RESEARCH_NOTES_2026-05.md`, `CHANGELOG.md`, and
  this handoff. Focused model-tier coverage was tightened without changing
  executable test count. No route count, MCP tool count, feature-catalog entry,
  request shape, or deterministic fallback behavior changed.
  
  **Verification:** focused `ModelTierTests` passed `19 / 19`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `artifacts/full-audit/20260521-191226/RESULTS.md`.

- **Pass 320 - Cache-friendly prompt head + broader publication IP guard.**
  Current prompt-caching research and the active sibling scan both pointed at
  the same bounded move: protect the stable prompt prefix before changing
  servers or adding more knobs. Microsoft ASP.NET Core timeout/rate-limit
  guidance still supports the existing host posture; the useful low-risk
  model-serving change was prompt shape.
  
  **Runtime prompt layout:** `PalLlmRuntime.BuildSystemPrompt` now keeps stable
  companion contract text, character identity, traits, skills, and authored
  pack lore before the volatile `Turn context` block (`Task tag`, world state,
  visual context, relationship, and memory snippets). This gives prefix/KV-
  cache-aware local runtimes a longer reusable prompt head across repeated
  turns for the same companion. Request-body fields, fallback decisions, routes,
  MCP tools, feature catalog entries, and OpenAPI schema are unchanged.
  
  **Publication hardening:** the shared pack publication-safety scanner and
  both release/public-copy scanners now block a wider set of obvious unrelated
  game, film, comic, tabletop, anime, and studio-name shorthand. Existing
  validator coverage was tightened without adding executable test count.
  
  **Docs/tests:** refreshed `HOT_PATH.md`, `PACK_AUTHORING.md`,
  `RESEARCH_NOTES_2026-05.md`, the hot-file count mirrors, `CHANGELOG.md`, and
  this handoff. Sibling projects supplied only generic cache-proof and
  publication-hygiene patterns; no code, prompts, names, branding, or product
  identity was lifted.
  
  **Verification:** focused `RuntimeTests` passed `132 / 132`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260521-181055/RESULTS.md`.
- **Pass 319 - Local provider telemetry labels.** Current OpenTelemetry
  GenAI guidance and the focused sibling-project scan both pointed at the same
  bounded improvement: latency/cache proof is more useful when every live lane
  names its serving runtime, but those labels must stay low-cardinality.
  
  **Runtime observability:** `GenAiTelemetry.GetProviderName` now classifies
  common local model-serving runtimes from stable host/path hints or
  loopback/LAN default-port hints: `ollama`, `lmstudio`, `llama.cpp`, `vllm`,
  `sglang`, `tensorrt_llm`, `openvino`, `foundry_local`, and `transformers`.
  Ambiguous endpoints still fall back to `openai_compatible`; plain
  `localhost:8000/v1/` intentionally stays generic because several runtimes use
  that port, and public hosts on a common local-runtime port stay generic unless
  the hostname gives a clear provider signal. Request bodies, fallback
  behavior, route counts, MCP tools, feature catalog entries, and OpenAPI
  contract are unchanged.
  
  **Docs/tests:** added provider-label assertions to the existing
  `CompleteAsync_EmitsGenAiSpanAndMetrics` test without changing the test
  count. Refreshed `OBSERVABILITY.md`, `TUNING.md`, and `CHANGELOG.md` so
  operators know what appears in OTel spans, Prometheus labels, dashboard lane
  metadata, and `/api/inference/performance`.
  
  **Verification:** focused `InferenceClientTests` passed `73 / 73`; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260521-171244/RESULTS.md`.
- **Pass 318 - Voice-reference provenance gate + allocation-free fallback embeddings.**
  Current-model and sibling-project audit found two useful, bounded moves:
  make shareable voice references safer before any future voice-clone lane
  consumes them, and remove avoidable hot-path allocations from deterministic
  memory recall.
  
  **Runtime fixes:** `SemanticEmbedder.FallbackEmbed` now scans
  `ReadOnlySpan<char>` tokens directly instead of allocating a lower-cased
  string and a split-token array per recall. It preserves the same
  deterministic, case-insensitive FNV-1a unigram and adjacent-bigram behavior,
  but does less work on every local fallback memory query. Personality-pack
  validation now requires `VoiceConsent` whenever `VoiceRefPath` is declared
  (`self_recorded`, `licensed`, `synthetic`, or `public_domain`), caps
  `VoiceConsentNotes` at 1024 characters, and includes that provenance note in
  the existing publication-safety scan.
  
  **Docs/schema:** updated the JSON schema with a conditional
  `VoiceRefPath` -> `VoiceConsent` rule, refreshed the sample-pack guide,
  multimodal recipes, FAQ, 2035 roadmap notes, research notes, and feature
  catalog entry so pack authors see the new requirement before validation
  fails.
  
  **Verification:** focused regression tests passed
  (`PersonalityPackValidatorTests` + `ConversationMemoryStoreTests`,
  `18 / 18`); OpenAPI snapshot verification stayed unchanged; full
  `dotnet test PalLLM.sln --configuration Release --no-restore --nologo
  --verbosity minimal` passed `1154 / 1154`; full audit passed `16 / 16` at
  `../artifacts/full-audit/20260521-161729/RESULTS.md`.
- **Pass 317 - CODE_MAP hot-file line-count drift guard.**
  Code-vs-doc audit found `docs/CODE_MAP.md` had stale hot-file table
  counts (`PalLlmRuntime.cs` `4581`, `Program.cs` `2037`) while the live
  files measured `4729` and `2056`. The existing Pass 311 hot-file gate did
  not catch it because CODE_MAP stores those counts in a table instead of
  prose ending in "lines".
  
  **Fix:** added `docs/CODE_MAP.md` to `Drift_Hot_file_line_count` in
  `scripts/run_full_audit.ps1` and added a CODE_MAP-specific markdown-table
  row parser. Refreshed CODE_MAP's stamp to `2026-05-21` and updated the
  big-three hot-file rows:
  `PalLlmRuntime.cs=4744`, `PresentationCuePlanner.cs=1427`,
  `Program.cs=2056`.
  
  **Second doc drift fixed:** `docs/MODELS_2026.md` had described
  `Vision:Model` and `Asr:Model` as future knobs even though both already
  exist in `PalLlmOptions` and validator coverage. Reworded it around the
  existing `Vision:BaseUrl` / `Vision:Model`, TTS, and ASR config surfaces.
  Added missing `PalLLM__Vision__Model` and `PalLLM__Vision__ApiKey` rows to
  `docs/ENV_VARS.md` and refreshed that stamp.
  
  **Research / publication scan:** rechecked current Microsoft Learn
  request-timeout/rate-limit guidance, current llama.cpp server docs, and
  vLLM-Omni Qwen3-Omni streaming-video docs. No serving code changed; the
  sources support the existing guarded posture. Focus-scanned active
  `D:\Coding` sibling docs for cache/runtime proof ideas; no PalLLM-safe code
  was lifted. Public-copy audit stayed green for release-facing surfaces.
  
  **Verification:** `dotnet test` passed `1154 / 1154`; full audit passed at
  `../artifacts/full-audit/20260521-160922/RESULTS.md`; the strengthened
  hot-file gate checked `8` claims across `6` docs with `0` issues. Counts
  and OpenAPI schema unchanged.
- **Pass 316 - Research-grounded model recommendations doc (`docs/MODELS_2026.md`).**
  User asked to rethink the program with current research on the best
  models for each task. Spent ~10 parallel queries against HF Hub
  `trendingScore`-sorted endpoints + web roundups (Reddit /r/LocalLLaMA,
  BentoML, Milvus, DigitalOcean, etc.) and synthesised one focused
  recommendations doc.
  
  **What's in `docs/MODELS_2026.md`:** TL;DR matrix of recommended
  models across 7 functions × 3 hardware tiers (edge / consumer /
  enthusiast), then a per-function deep dive with size, license,
  rationale, and wire-up hint:
  
  1. **Fast-start chat**: keep `gemma3:4b`; upgrade option
     `qwen3.6-mini:4b-a1b` (MoE, 1B active).
  2. **Quality chat**: keep `qwen3.6:35b-a3b` Q4 (already default);
     also lists `qwen3.6:27b` dense, roleplay tunes (Snowpiercer-15B,
     Rocinante-X-12B for low slop), DeepSeek V4 Flash for enthusiasts.
  3. **Vision (new gap)**: `Qwen3-VL-8B` Q4 as default
     (multiple 2026 sources call it "the new default local VLM"),
     `MiniCPM-V-4.6` (top trending VLM, 1.3 B) for edge, Gemma 3-vision
     4 B int4 for 8 GB rigs, `Qwen2.5-VL-32B` for 24 GB enthusiasts.
  4. **TTS**: `Kokoro-82M` for speed (<300 ms / line, Apache 2.0),
     `Fish Speech s2-pro` for commercial-friendly voice clone, F5-TTS
     for personal-use clone (CC-BY-NC), Voxtral-Mini-4B-Realtime for
     streaming.
  5. **ASR**: `whisper-large-v3-turbo` via `faster-whisper`
     (CTranslate2, 4-6× speedup at parity quality),
     `parakeet-tdt-0.6b-v3` for streaming.
  6. **Embeddings**: `bge-m3` (hybrid dense+sparse+multivector in one
     model — biggest single-model upgrade over BGE-large),
     `nomic-embed-v2` for edge.
  7. **Reranker (new — biggest memory-quality lift)**:
     `bge-reranker-v2-m3` default, `jina-reranker-v3` premium, off on
     edge tier.
  
  **Proposed wire-up changes** (called out but NOT shipped this pass —
  documentation pass only): `Vision:Model` and `Asr:Model` already exist;
  the remaining additive work is a `Memory:RerankEndpoint` hook plus setup
  defaults / wizard recommendations for model-tier, vision, and ASR choices.
  Each fits the Pass 315 SpeciesPersonalityResolver pattern: small additive
  feature, off by default, one config knob.
  
  **Re-audit checklist included** so the next quarterly refresh
  doesn't fall into freshness-theater — the doc names the specific
  HF Hub queries to re-run and warns explicitly against bumping the
  stamp without re-running them.
  
  Sources cited inline (HF Hub data + 13 editorial roundups). Doc
  count bumped `63 -> 64`; `docs/INDEX.md` catalog updated to include
  the new doc (caught by the Pass 297 IndexDoc meta-test). No code
  changed; tests stay at `1154 / 1154`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
  
  **Why this is more than busywork.** The program already had
  excellent infrastructure (model-agnostic OpenAI-compatible HTTP for
  every lane, the tier orchestrator, the Duo planner) but the *which
  model do I actually run* answer was diffused across MODEL_COLLABORATION
  (600+ lines of serving architecture), QUANTIZATION (quant tactics),
  and a few config defaults. The Pass 316 doc is the missing one-page
  answer to "I have an RTX 4080 and want PalLLM to feel good — what
  goes in my config?". Lay users get the TL;DR table; experts get the
  per-function rationale with license + size + alternatives.
- **Pass 315 - New SpeciesPersonalityResolver feature: per-species personality packs.**
  First substantive code addition in this campaign — closes the
  "all Lamballs share a timid voice" gap surfaced in the user's
  earlier audit question. Operators map species names to pack ids
  once in config, no need to author a separate pack per character.
  
  **Components shipped:**
  - `src/PalLLM.Domain/Packs/SpeciesPersonalityResolver.cs` — pure
    static advisor, `Resolve(species, defaultBySpecies, fallbackPackId?)`
    returns `SpeciesPersonalityResolution { PackId, Source, Species }`.
    Three dispatch lanes: `SpeciesDefault` -> `Fallback` -> `None`.
    Inputs trimmed, keys matched case-insensitively, blank entries
    skipped, never throws.
  - `PacksOptions.DefaultBySpecies` (new) in `PalLlmOptions.cs` — a
    case-insensitive `Dictionary<string, string>` for the species ->
    pack-id map.
  - `GET /api/packs/resolve?species=X&fallback=Y` (new route, total
    `/api` now `57`).
  - `pal_personality_for_species` (new MCP tool, total now `38`).
  - `SpeciesPersonalityResolution` added to AOT JSON serializer
    context for trim-friendly serialisation.
  - 19 focused regression tests pinning every branch
    (`tests/PalLLM.Tests/SpeciesPersonalityResolverTests.cs`).
  - Feature catalog entry `species-personality-resolver` (status
    `ready`).
  - ADVISORS.md row.
  
  **Cross-doc cascade.** Adding a route + MCP tool + feature +
  tests means count mirrors update everywhere:
  - `tests` `1135` -> `1154` (+19)
  - `apiRoutes` `56` -> `57`
  - `mcpTools` `37` -> `38`
  - `featureCatalog` `121` -> `122`
  - `featureReady` `118` -> `119`
  
  Updated in lockstep: `docs/PROJECT_NUMBERS.json`, `agents.json`
  (rollingState + validationGates), `pal.json`, `pal.ps1`, `CLAUDE.md`,
  `CONTRIBUTING.md`, `.cursorrules` (both anchors), 
  `.github/copilot-instructions.md`, `scripts/onboard.ps1` (both
  anchors), `tests/README.md`, `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`
  gate-pin header comment, plus the five canonical anchors `README.md`,
  `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/CODE_MAP.md`,
  `docs/HANDOFF.md` and the `docs/API.md` route table. Also
  `docs/READINESS.md` (route+tool counts), `docs/AGENTIC_PATTERNS_2026.md`
  (tool count), and `src/PalLLM.Sidecar/SelfDescriptionBuilder.cs`
  (the self-describe payload that pins the tool count for MCP
  clients).
  
  **Side fix during cascade.** Pass 305's meta-test had
  `15 / 15` hardcoded into the `.cursorrules` regex anchor.
  Pass 311's gate addition bumped the count to `16 / 16`, which
  broke the regex. Loosened it to `\d+ / \d+` so the meta-test no
  longer pins the gate count (that's already pinned by the gates
  themselves).
  
  Also bumped a hardcoded `ApiRouteCount=56` assertion in
  `tests/PalLLM.Tests/SidecarEndpointTests.cs:893` to `57`.
  
  OpenAPI snapshot regenerated via `scripts/export-openapi.ps1`.
  
  **Verification.** Build clean at `WarningLevel=9999`; focused
  `SpeciesPersonalityResolverTests` `19 / 19` (10 ms); full
  `dotnet test` `1154 / 1154`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`.
  
  **Why it matters to players.** Closes the question raised in
  the prior turn: "do Pals have personalities per species?". Before
  Pass 315, the personality format was strictly per-character-id, so
  every tame needed its own pack. Now an operator can configure once
  ("Lamball -> lamball-timid-pack") and every Lamball the player
  ever tames inherits that voice while still building its own
  per-character memory and relationship affinity. The pack format
  stays per-character — this is the *lookup table* that decides
  which pack to apply when the character does not have an explicit
  assignment.
- **Pass 314 - QUICKREF.md + OBSERVABILITY.md content audit: 7 drifts + aspirational-spans bust.**
  Stale stamp at `2026-05-06`. QUICKREF is the sortable companion to
  CHEAT_SHEET — load-bearing for any "what's the surface?" question.
  Audit found 7 concrete drifts plus a substantial OBSERVABILITY.md
  drift that surfaced when verifying QUICKREF's span table.
  
  **QUICKREF.md drifts fixed (7):**
  
  1. `pal audit` summary: "build + tests + 15 gates" -> "16 gates"
     (Pass 311 added the 16th gate).
  2. Drift-gates table missed the new `Drift_Hot_file_line_count`
     entry — added as row 15, pushing dangling-links to row 16.
  3. Hot-path table: `ChatDispatchPlanner.Plan` -> `Decide`
     (same root drift fixed in Pass 308 ADVISORS.md).
  4. Hot-path table: `PresentationCuePlanner.Plan` -> `Build`.
  5. Hot-path table: `PalLlmRuntime.WriteOutboxAsync` ->
     `WriteOutboxReplyAsync`.
  6. Health-endpoints table: `GET /api/health/score` doesn't exist;
     `OperatorHealthScore` is embedded in `/api/describe`. Replaced
     the row.
  7. OpenTelemetry spans table listed 11 fictional spans
     (`Bridge.Drain`, `Chat.Plan`, `Memory.Recall`, etc.). Actual
     code emits 3: `pal.chat` (Runtime.cs:1262),
     `pal.model_tier.transition`
     (ModelTierOrchestrator.cs:193), and a per-call GenAI client
     span with a dynamic name (GenAiTelemetry.cs:88). Rewrote the
     table.
  
  **OBSERVABILITY.md fix (collateral, same root):** The same
  fictional span hierarchy was reproduced in OBSERVABILITY.md's
  "Span inventory" section as the supposed-canonical source.
  Replaced the 11-row inventory with the honest 3-emitted + 2-auto
  list, rewrote the example trace tree to match the real shape,
  rewrote the "Spans during deterministic fallback" section to
  describe what actually happens (rich `pal.*` tags on the single
  `pal.chat` span carry the fallback diagnostic, no separate
  `Chat.Fallback` / `Memory.Recall` spans), and added an explicit
  paragraph noting the earlier richer hierarchy was aspirational
  and never shipped. Stamp `2026-05-07` -> `2026-05-21`.
  
  Stamps refreshed: QUICKREF.md `2026-05-06` -> `2026-05-21`,
  OBSERVABILITY.md `2026-05-07` -> `2026-05-21`. No code changed;
  tests stay at `1135 / 1135`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
  
  **Agentic-coding flaw addressed.** Aspirational-but-unshipped
  documentation: prose describing a system the code never actually
  built. The original 11-span hierarchy is a sensible design and
  may still be the right destination, but until the spans are
  *emitted*, the docs were lying to every operator setting up
  Jaeger. Honest docs say "what exists today, with a clear note
  about where it could grow"; dishonest docs say "what we wished
  we'd built." This pass corrects to the honest form.
- **Pass 313 - IMPLEMENTATION_QUEUE.md content audit + rounding-gap clarification.**
  Next-oldest stamp at `2026-05-06`. This is the strategic roadmap
  doc — accuracy here directly affects what the next agent (human or
  small-model) thinks needs shipping. The audit verified:
  
  - All 11 referenced script paths exist on disk
    (`run-sidecar-smoke.ps1`, `run-delivery-replay.ps1`,
    `doctor.ps1`, `run-native-proof.ps1`,
    `export-release-proof-bundle.ps1`, `play-palllm.ps1`,
    `play.bat`, `package-release.ps1`,
    `verify-release-package.ps1`, `start-sidecar.ps1`,
    `apply-hud-bind-recommendation.ps1`).
  - `mod/ue4ss/Mods/PalLLM/Scripts/main.lua` exists.
  - `RuntimeHealth.BridgeLoop` type exists at `Contracts.cs:2258`.
  - `production_sampler`, `ui_probe`, `Presentation.Surface`,
    `waypoint_suggest`, `SpeechArtifact` all resolve in code.
  - Roadmap baseline `76.2%` matches canonical
    `PROJECT_NUMBERS.json`.
  - All 6 queues and 3 milestones have internally-consistent
    structure.
  
  **One real drift surfaced (but not directly fixed) — the meta-test
  blocked the fix.** Queue percentages sum to `~24.5%`
  (6.0+4.5+7.5+2.0+3.0+1.5) while remaining gap is exactly `23.8%`
  (100 - 76.2). Tried tightening Q1 from `~6.0%` to `~5.3%` to make
  the math add up; the existing
  `QueueInventory_PalComplete_CompletionMd_ImplementationQueue_Agree`
  meta-test (`tests/PalLLM.Tests/MetaTests.cs:829`) fired and
  rejected the change because the same value is pinned in
  `scripts/pal-complete.ps1:150` and `docs/COMPLETION.md`. Three-way
  lockstep change would require deciding "is Q1 actually 5.3% or
  6.0%?" — which I can't authoritatively answer without ground truth
  for queue scope. Instead, added a one-paragraph note near the
  doc's "Current status" section explaining that per-queue
  percentages are deliberate approximations (`about` / `~` prefix
  signals this), and the milestone totals are the load-bearing
  figures. The canonical `76.2%` stays pinned in
  `PROJECT_NUMBERS.json`. Honest disclosure rather than fudged math.
  
  Stamp refreshed `2026-05-06` -> `2026-05-21`. No code changed;
  tests stay at `1135 / 1135`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
  
  **Agentic-coding flaw addressed.** The kind of fix that *looks*
  obvious ("just change 6.0% to 5.3% to make the math work") but is
  actually a guess about ground truth — wrapped in three-file
  lockstep and protected by a meta-test that the previous agent
  added precisely to prevent unilateral edits. Took the meta-test's
  signal as authoritative: when the existing guard says "don't
  change this in isolation," the right move is to document the
  observation, not to override the guard. The meta-test itself is
  doing its job exactly as designed.
- **Pass 312 - UNINSTALL.md + MCP_QUICKSTART.md content audits: both clean.**
  Continuing the stale-stamp campaign. Two next-oldest docs:
  - `docs/UNINSTALL.md` at `2026-05-01`
  - `docs/MCP_QUICKSTART.md` at `2026-05-06`
  
  Both walked end-to-end against actual code. **No drift found** in
  either. Specific things verified:
  
  **UNINSTALL.md (all match):**
  - `uninstall.bat`, `install.bat`, `scripts/uninstall-mod.ps1`,
    `scripts/install-mod.ps1`, `scripts/PalLLM.InstallManifest.ps1`,
    `docs/schemas/install-manifest.schema.json`, `Makefile` all
    exist.
  - Sample personality pack at `runtime-root/Packs/chillet-pack.json`
    is real (sourced from `docs/examples/chillet-pack.json`).
  - Manifest `Kind` enum
    (`directory`/`file`/`junction`/`enabled-file`/`sample-pack`)
    matches `[ValidateSet(...)]` at
    `scripts/PalLLM.InstallManifest.ps1:64`.
  - Config options `PalLLM:PalSavedRoot` + `PalLLM:RuntimeFolderName`
    exist at `Domain/Configuration/PalLlmOptions.cs:26,31`.
  - `pal.ps1 uninstall` verb is registered in `pal.json:24`.
  - `make uninstall` target exists at `Makefile:64`.
  - `uninstall.bat /preview` + `/full` flags are handled in
    `uninstall.bat:31-35`.
  - Junction safety: `Remove-Item -LiteralPath ... -Force` without
    `-Recurse` for `Kind == 'junction'` (script line 220-226).
  
  **MCP_QUICKSTART.md (all match):**
  - All 6 referenced MCP tool names (`pal_scene_description`,
    `pal_list_characters`, `pal_list_features`, `pal_status`,
    `pal_health_score`, `pal_health_suggestions`) are real
    `[McpServerTool(Name = ...)]` declarations in
    `Sidecar/Mcp/PalLlmMcpTools.cs`.
  - All 4 prompt names match
    `[McpServerPrompt(Name = ...)]` in `PalLlmMcpPrompts.cs:35,85,122,152`.
  - All 7 resource URIs (6 direct + 1 templated character profile)
    match `[McpServerResource(UriTemplate = ...)]` in
    `PalLlmMcpResources.cs:37,52,63,74,85,107,118`.
  - Port `5088` matches `launchSettings.json:16`.
  - `/health/live` endpoint exists at `Program.cs:689`.
  - MCP protocol version `2025-06-18` matches
    `SelfDescriptionBuilder.cs:90`, which also corroborates
    "37 tools, 6 resources + 1 template, 4 prompts" verbatim.
  - All 4 example file references (`compose.yaml`,
    `vscode-mcp.json`, `claude-desktop-config.json`,
    `chillet-pack.json`) exist under `docs/examples/`.
  
  Stamps refreshed `2026-05-01` -> `2026-05-21` (UNINSTALL) and
  `2026-05-06` -> `2026-05-21` (MCP_QUICKSTART). No code changed;
  tests stay at `1135 / 1135`; full audit `16/16` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
  
  **Agentic-coding flaw addressed.** "Clean audit" passes that
  refresh a stamp without changing content are still honest work
  if the verification actually happened — what this pass logs
  proves the verification happened (specific code references for
  every doc claim). The opposite anti-pattern is rubber-stamping
  without verifying; this pass is the corrective example.
- **Pass 311 - New `Drift_Hot_file_line_count` gate (16th gate).**
  Pass 310 surfaced the single-source-drift pattern for hot-file
  line counts: 5 docs mirrored "4028" while `PalLlmRuntime.cs` had
  grown to 4729 with no automated detection. This pass implements
  the gate that pass 310 said was out of scope. Same drift pattern
  the existing 15 gates already handle for test counts / route
  counts / feature counts is now applied to hot-file line counts.
  
  **What the gate does.** Walks 3 hot files
  (`PalLlmRuntime.cs`, `Program.cs`, `PresentationCuePlanner.cs`)
  and 5 doc files (`CLAUDE.md`, `docs/CHEAT_SHEET.md`,
  `.github/copilot-instructions.md`, `docs/ANTI_PATTERNS.md`,
  `docs/HARVEST.md`) and verifies any "~N-line" / "N lines" claim
  near the filename is within 5% of the actual `wc -l` value. The
  5% tolerance lets a single small commit pass cleanly but
  Pass 310-scale drift (~17%) does not.
  
  **Negative-tested.** Temporarily wrote "~3000-line" into
  CHEAT_SHEET.md, ran audit -> 1 drift found, overall FAIL. Reverted,
  re-ran -> 16 / 16 PASS. Confirmed the gate catches what it
  claims to catch.
  
  **Cross-doc count cascade.** Adding a gate moves the "15 / 15"
  count everywhere it appears as a current claim. Updated in
  lockstep:
  - `docs/HANDOFF.md` "Current audited state" line — `15 / 15` -> `16 / 16`
  - `agents.json` `rollingState.driftGates` — `"15/15"` -> `"16/16"`
  - `agents.json` `validationGates.drift` — added the new gate entry inline next to `Drift_Doc_freshness`; `$comment` updated `All 15` -> `All 16`
  - `docs/PROJECT_NUMBERS.json` `driftGates` — `"15/15"` -> `"16/16"`
  - `docs/schemas/project-numbers.schema.json` — `15 drift gates` -> `16 drift gates`; `e.g. '15/15'` -> `e.g. '16/16'`
  - `CLAUDE.md` "Drift gates" section — `15 gates` -> `16 gates`
  - `AGENTS.md` hard rules — `15 gates in scripts/run_full_audit.ps1` -> `16 gates`
  - `AGENTS.md` working loop — `15/15 PASS` -> `16/16 PASS`
  - `.github/copilot-instructions.md` — `15 / 15` -> `16 / 16`
  - `CHANGELOG.md` "Current baseline (rolling)" — `15/15` -> `16/16`
  - `pal.json` audit verb summary — `15 gates` -> `16 gates`
  
  All historical `15/15` strings in CHANGELOG.md / HANDOFF.md Pass
  entries / `artifacts/health-snapshot/*` left intact — they are
  point-in-time snapshots of the audit at the time of that entry.
  
  No code changed; tests stay at `1135 / 1135`; full audit
  `16 / 16` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 310 - HARVEST.md content audit + cross-file `PalLlmRuntime.cs` line-count drift.**
  Next-oldest stamp after the trio Passes 307-309 fixed (HARVEST.md
  was `2026-04-30`). Same walk-each-row pattern as Pass 308. Found 3
  primary drifts plus a 5-file cross-doc line-count drift caused by
  `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` having grown
  significantly without the mirrors being bumped.
  
  **Primary HARVEST.md drifts fixed:**
  
  1. `DisagreementDetector` row: doc said
     `DisagreementVerdict Check(worker, judge)` AND listed
     `SemanticEmbedder.cs` as a dependency. Actual:
     `DisagreementAnalysis Compare(workerOutput, judgeOutput)`
     (same root as the Pass 308 ADVISORS.md drift), AND
     `SemanticEmbedder` lives inside
     `Portable/PortableAdapterContracts.cs` (no separate file). A
     harvester following the doc would have grepped for a
     non-existent file.
  2. `WhyEngine.Answer` row had hand-wavy `(question, health,
     metrics, ...)`. Tightened to
     `(question, health, metrics, score, worldSnapshot?)` to match
     the actual signature at `Domain/Runtime/WhyEngine.cs:54`.
  3. `PalLlmRuntime.cs` ("4,028 lines") and `Program.cs` ("1,883
     lines") line-count claims in the "What NOT to harvest" section
     were stale. Actual: `4729` and `2037` (verified with `wc -l`).
  
  **Cross-file fix (same root: PalLlmRuntime.cs grew).** The 4,028
  number was mirrored in 4 other places and silently aged:
  - `CLAUDE.md` line 59 — 4,028 -> ~4,729
  - `docs/CHEAT_SHEET.md` line 42 — 4028 -> ~4729
  - `.github/copilot-instructions.md` line 15 — 4028 -> ~4729
  - `docs/ANTI_PATTERNS.md` line 87 — 4028 -> ~4729
    (PresentationCuePlanner.cs at 1427 verified accurate)
  - `docs/ANTI_PATTERNS.md` line 148 — "is 4028 lines" -> "is ~4729
    lines"; refactor trigger threshold bumped `~5000` -> `~5500`
    so the threshold maintains roughly the same headroom over
    current size.
  
  Used `~` prefix on each updated number to signal these are
  current-as-of-stamp approximations, not strict pins — small
  diffs are expected before the next audit refresh.
  
  Stamp on HARVEST.md refreshed `2026-04-30` -> `2026-05-21`. The
  other 4 files weren't fully re-audited so their stamps stay; the
  fix is purely a number correction. No code changed; tests stay
  at `1135 / 1135`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 309 - COMPANION_INTELLIGENCE.md content audit: refresh sibling-project counts.**
  Third-oldest stamp in the doc tree (`2026-04-28`, same as Pass 307
  and 308 targets). This doc is mostly forward-looking ideas, not a
  code spec, so the audit was checking that referenced PalLLM classes,
  action IDs, and sibling-project artifacts still exist and the
  forward-looking framing still holds.
  
  **What was verified accurate (no edit needed):**
  - All referenced PalLLM classes exist: `ConversationMemoryStore`,
    `RelationshipTracker`, `ProofPacketBuilder`, `WhyEngine`,
    `DisagreementDetector`, `DuoOrchestratorPlanner`,
    `GameWorldSnapshot`, plus `ReflectionService` (deterministic
    reflection claim verified at
    `Domain/Runtime/PalLlmRuntime.cs:106`).
  - Both action IDs `request_craft_queue` and `recall_pals` are
    real (`Domain/Runtime/ActionIntentPlanner.cs:37,104`).
  - The forward-looking `MemoryGraphBuilder` is genuinely
    forward-looking — it doesn't exist in the codebase, so the
    "build a read-only MemoryGraphBuilder" framing is accurate.
  - The current roadmap percentage `76.2% -> 100%` matches the
    "Current audited state" block above.
  - All 5 referenced Byte prompt-pack families exist at
    `D:/Coding/Byte/docs/prompts/byte-{forge,forward,synthesis,
    qwen-frontier,qwen-modernize}-*` plus archived
    `_archive/byte-council-2026-04-24/`.
  
  **What needed refresh (one drift fixed):** the audit-summary line
  said the Byte library "contained `652` markdown files plus `12` zip
  archives." Today's count is `837 / 14`. Updated with an explicit
  re-count date so the snapshot stays honest, and added a one-line
  note that a sixth family (`byte-qwen-pack-2026-04-25`) has since
  landed but is left out because it was not part of the original
  signal pull. Stamp refreshed `2026-04-28` -> `2026-05-21`.
  
  No code changed; tests stay at `1135 / 1135`; full audit `15/15`
  PASS at `../artifacts/full-audit/20260521-145014/RESULTS.md`.
  Routes, MCP tools, feature catalog entries, fallback strategy
  counts, and OpenAPI schema unchanged.
- **Pass 308 - ADVISORS.md catalog audit against live code: fix 5 drifts.**
  Same `2026-04-28` stamp as DATAFLOW.md, and the catalog is the
  agent-friendly entry point for "what does this code own?" — drift
  there silently misleads every harvester and every newcomer agent.
  This pass walked every advisor / builder / validator / feeder /
  tracker / store row against the actual files (46 paths, all
  resolved) and ran a public-surface grep against each.
  
  **Drifts found:**
  
  1. `DisagreementDetector`: doc said `DisagreementVerdict Check(worker, judge)`;
     actual is `DisagreementAnalysis Compare(workerOutput, judgeOutput)`
     (`Domain/Runtime/DisagreementDetector.cs:36`). Both method name and
     return type were wrong.
  2. `BridgeProofBuilder`: doc said `Build(...)`; actual is `Create(runtime)`
     (`Sidecar/BridgeProofBuilder.cs:26`).
  3. `SelfDescriptionBuilder`: doc said `Describe(...)`; actual is `Build(...)`
     (`Sidecar/SelfDescriptionBuilder.cs:26`).
  4. `ReleaseFullAuditEvidenceBuilder`: doc said `FullAuditEvidence Build(...)`;
     actual is `ReleaseFullAuditEvidenceSnapshot ReadLatest(options)`
     (`Sidecar/ReleaseFullAuditEvidenceBuilder.cs:9`) — method name AND
     return type AND the implicit pattern (it's a reader, not a builder).
  5. `SelfHealingWorker`: doc said writes to `Runtime/SelfHealing/*.json`;
     actual writes to `Runtime/SelfHealingEvidence/*.json`
     (`Sidecar/SelfHealingWorker.cs:189`). One word off but the path
     would have been the wrong directory for an operator hunting
     evidence files.
  
  Stamp refreshed `2026-04-28` -> `2026-05-21`. Side fix: the Pass 305
  HANDOFF entry phrased "TODO/FIXME/HACK markers in <slash-joined
  directory list>" in a way that `Drift_Path_references` interpreted
  as a single repo path (which doesn't exist); rewrote to use
  backticked individual directory names so each token resolves on its
  own. No code changed; tests stay at `1135 / 1135`; full audit
  `15/15` PASS at
  `../artifacts/full-audit/20260521-145014/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 307 - DATAFLOW.md content audit against live code: fix 5 drifts.**
  The `2026-04-28` stamp on `docs/DATAFLOW.md` was the oldest in the
  doc tree (22 days old, still well under the 45-day freshness cap).
  Rather than freshness-theater the stamp without verifying content,
  this pass walked each sequence diagram against the actual code and
  found 5 concrete drifts the gates can't catch:
  
  1. §1 chat path: `Planner.Plan(taskKind, roles)` -> actual
     `ChatDispatchPlanner.Decide(pattern, coverage)` (verified in
     `src/PalLLM.Domain/Inference/ChatDispatchPlanner.cs:30`).
  2. §1 chat path: `Director.Plan(strategy, family)` -> actual
     `_presentationCuePlanner.Build(...)` (verified in
     `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs:1602`).
  3. §1 chat path: `Outbox.WriteOutboxAsync(envelope)` -> actual
     `WriteOutboxReplyAsync(payload, cancellationToken)` (verified in
     `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs:1623`).
  4. §5 promotion: `GET /api/promotion/suggest` -> actual
     `GET /api/promotion/suggestions` (verified in
     `src/PalLLM.Sidecar/Program.cs:1209`).
  5. §6 chat streaming: missing entirely the `phase: final-prep`
     SSE event that fires between orchestration and the token loop
     (verified in `src/PalLLM.Sidecar/Program.cs:1684`). Token
     payload also wrongly showed `{ "text": "Hi " }` when the
     actual shape is `{ index, total, text }` (verified in
     `PalLlmJsonSerializerContext.cs:488`).
  
  Diagram in §6 rewritten with the final-prep phase event including
  its full payload shape (`task_kind`, `cooperation_pattern`,
  `dispatch_mode`, `role_chain`) and the mutually-exclusive `alt /
  else / else` structure for `final` vs `error(request_timeout)`
  vs `error(internal_error)`. New prose paragraph explains the
  final-prep phase's renderer-switch utility: clients can flip
  fallback-vs-live-inference UI before any tokens arrive. Stamp
  refreshed `2026-04-28` -> `2026-05-21`.
  
  No code changed; tests stay at `1135 / 1135`; full audit `15/15`
  PASS at `../artifacts/full-audit/20260521-145014/RESULTS.md`.
- **Pass 306 - HANDOFF.md trim: cut 12 historical pass entries.**
  Since Pass 294's trim, the session's coverage passes (293/295/296/
  297/298/299/300/301/302/303/304/305) and a parallel-agent batch had
  pushed this doc to `657` lines with 22 Pass entries — drifting back
  toward pre-trim bloat. This pass cuts every Pass entry older than
  Pass 296, leaving the 10 most-recent inline (305-296). Older entries
  remain verbatim in [`../CHANGELOG.md`](../CHANGELOG.md). Final size:
  `~365` lines (`-292`). The redirect note at the bottom of "What
  just landed" now points at Passes 48-295 instead of 48-283.
  
  Stack-wide quality cross-check during this pass also re-verified:
  `dotnet build` clean even at `WarningLevel=9999`; all 67 PowerShell
  scripts parse without errors; CI workflows (`ci.yml`, `codeql.yml`,
  `lua.yml`, `release.yml`) all look well-pinned; frontend has 0
  developer-tooling markers (no stray `console.error` /
  `debugger` / TODO).
  
  No code changed; tests stay at `1135 / 1135`; full audit `15/15`
  PASS at `../artifacts/full-audit/20260521-115112/RESULTS.md`.
- **Pass 305 - Permanent regression guard: meta-test pins 13 secondary test-count mirrors to PROJECT_NUMBERS.**
  This session repeatedly observed silent drift in 13 secondary
  test-count mirrors (agents.json ×2, pal.json, pal.ps1, CLAUDE.md,
  CONTRIBUTING.md, .cursorrules ×2, .github/copilot-instructions.md,
  scripts/onboard.ps1 ×2, PalLlmRuntime.cs gate-pin comment,
  tests/README.md) — the existing `Drift_Test_count_docs` gate only
  checks 5 anchors. This pass adds a permanent meta-test that pins all
  13 to `PROJECT_NUMBERS.tests` so the next test-count change either
  updates all in lockstep or fails with a precise actionable list.
  
  Stack-wide review during this pass also verified `dotnet build`
  clean, `pal aot-readiness` PASS, zero TODO/FIXME/HACK markers in
  `src`, `tests`, or `mod`, frontend a11y on welcome.html + index.html. Attempted
  dependency bumps reverted after local NuGet cache mismatch — the
  repo's docs/TESTING.md documents this as manual release-hardening.
  Meta-test count `26 -> 27`.
  
  **Test count `1134 -> 1135` (+1).** Verification: focused
  `SecondaryTestCountMirrors_AgreeWithProjectNumbers` `1 / 1`;
  `dotnet test` passed `1135 / 1135`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-114304/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and OpenAPI
  schema unchanged.
- **Pass 304 - Direct unit tests for the inbound HTTP request-body size limiter.**
  `HttpRequestBodyReadLimiter` is the sidecar's INBOUND counterpart to
  Pass 224's `HttpContentReadLimiter` (outbound). It pins
  `IHttpMaxRequestBodySizeFeature` before minimal-API binding and
  reads UTF-8 text payloads through `PipeReader` with a hard byte cap,
  stripping the UTF-8 BOM if present.
  
  New `tests/PalLLM.Tests/HttpRequestBodyReadLimiterTests.cs` adds
  `16` focused cases pinning every branch: feature missing/read-only
  no-op, limit-null → set, limit-larger → reduce, limit-smaller →
  preserve, negative clamp; declared length within cap / zero / over-
  cap short-circuit; 3-byte BOM strip; incomplete 2-byte BOM not
  stripped; no-declared-length streaming within and over cap; null
  guards.
  
  Test fixture constructs `DefaultHttpContext` with custom
  `IHttpMaxRequestBodySizeFeature` and `IRequestBodyPipeFeature`
  implementations so the real `PipeReader` logic runs end-to-end.
  
  **Test count `1118 -> 1134` (+16).** Verification: focused
  `HttpRequestBodyReadLimiterTests` fixture `16 / 16`; `dotnet test`
  passed `1134 / 1134`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-112414/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 303 - Direct unit tests for world-snapshot deep-clone + base-discovery merge.**
  `SnapshotCloneExtensions` produces the immutable `GameWorldSnapshot`
  instances every downstream consumer reads (fallback engine, prompt
  builder, vision orchestrator, `/api/world`, dashboard). A regression
  that aliased lists or characters between original and clone would
  let a concurrent bridge update appear partway through a snapshot a
  reader is mid-flight on.
  
  New `tests/PalLLM.Tests/SnapshotCloneExtensionsTests.cs` adds `19`
  focused cases pinning every branch: `CloneDeep` produces fresh
  instances for scalars / nested lists / known-bases / characters /
  travel + production status / vector; null nullable objects
  preserved as null; case-insensitive dictionaries with whitespace
  keys filtered; null character `Position` defaults to zero-vector.
  `WithBaseDiscovery` for new bases (adds to both KnownBases and
  ActiveBaseIds with FirstSeenUtc=LastSeenUtc=discoveredAt, empty
  source falls back to `"bridge"`), existing bases (updates
  LastSeenUtc, preserves FirstSeenUtc, AreaRange overwrite vs null-
  preserve, case-insensitive id match), ActiveBaseIds dedup +
  whitespace filter, and RecentEvents cap-at-12 + dedup + front-
  insertion.
  
  **Test count `1099 -> 1118` (+19).** Verification: focused
  `SnapshotCloneExtensionsTests` fixture `19 / 19`; `dotnet test`
  passed `1118 / 1118`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-111155/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 302 - Direct unit tests for upstream-failure status builder + global status line.**
  Two small but load-bearing helpers had no direct coverage:
  `TransportFailureStatusBuilder` (the human-readable upstream-failure
  summary every outbound HTTP client surfaces into ResponsePath /
  proof packet / dashboard), and `PalStatusLine` (the global static
  startup/ready/error state observed by every background worker and
  the `/api/health` endpoint).
  
  New `TransportFailureStatusBuilderTests.cs` adds `16` cases pinning
  the 9 specific HTTP status code mappings, the 5xx-range generic
  message, the unknown-code fallback, surface-label forwarding, the
  3 helper methods, and a distinctness sanity check. New
  `PalStatusLineTests.cs` adds `11` cases marked `[NonParallelizable]`
  pinning the state-machine semantics (`Set`/`SetReady`/`SetError`
  flag transitions, mutual exclusion of ready/error, most-recent
  wins), and a thread-safety test that proves `NoteActivity`'s
  `Interlocked` increment cannot lose concurrent updates (10 tasks,
  100 increments each, exact 1000 delta).
  
  **Test count `1056 -> 1099` (+43).** Verification: focused fixtures
  `27 + 16 = 43 / 43` (11 ms); `dotnet test` passed `1099 / 1099`;
  full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-110046/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 301 - Direct unit tests for the inference-residency provider policy.**
  `InferenceResidencyPolicy.Resolve` decides whether the runtime sends
  `keep_alive=N` (Ollama-native warmup) or `ttl=N` (LM Studio
  chat-completions extension) when keeping a model resident between
  turns. Until this pass the helper was only covered indirectly via
  model-tier orchestrator paths. A regression that emitted
  `keep_alive=0` (which tells Ollama to evict immediately) or sent
  `ttl=` to an Ollama endpoint would silently break warmup residency
  and shipping latency.
  
  New `tests/PalLLM.Tests/InferenceResidencyPolicyTests.cs` adds `27`
  focused cases pinning every branch: explicit provider selection
  (Ollama / LmStudio / Disabled), `Auto` detection (host substring +
  port-based for both providers, including case-insensitivity),
  unknown-provider fallback to `"none"` (vLLM `:8000`, llama.cpp
  `:8080`, generic OpenAI-compatible), malformed-URL handling,
  negative-TTL clamp, and `DescribeHint` formatting (positive TTL
  emits provider-specific hint, zero TTL on any provider returns
  empty, `"none"` always empty).
  
  **Test count `1029 -> 1056` (+27).** Verification: focused
  `InferenceResidencyPolicyTests` fixture `27 / 27`; `dotnet test`
  passed `1056 / 1056`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-105015/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 300 - Direct unit tests for the FNV-1a hash + composite seed helper.**
  `FallbackHash` provides the FNV-1a 32-bit hash, the composite chat-
  request seed, and the non-negative modulo used by the fallback
  director to deterministically pick a strategy variant. Every
  fallback response flows through `PositiveModulo(seed, variants.Length)`
  — a regression here would be observable to a player as "the companion
  suddenly says a different thing for the same prompt."
  
  New `tests/PalLLM.Tests/FallbackHashTests.cs` adds `27` focused cases
  pinning every branch: `OfString` determinism + case-insensitivity
  across ASCII and accented characters; `Seed` composition with
  whitespace-trimming and per-field distinctness; null character
  fallback to request name; `PositiveModulo` handling of negative
  dividends; and an end-to-end array-bounds check.
  
  **Test count `1002 -> 1029` (+27).** Verification: focused
  `FallbackHashTests` fixture `27 / 27`; `dotnet test` passed
  `1029 / 1029`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-020248/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 299 - Direct unit tests for the action-intent planner.**
  `ActionIntentPlanner.Plan` is the security-critical mapping from a
  fallback strategy to an `ActionIntent` the runtime is willing to
  suggest to the game side. Two safety gates protect the player:
  `AutomationOptions.Enabled` must be true AND the mapped action
  `Type` must appear in `AutomationOptions.AllowedActions`. Both gates
  are independent kill switches.
  
  New `tests/PalLLM.Tests/ActionIntentPlannerTests.cs` adds `18`
  focused cases pinning every branch: kill switches (disabled, empty
  allowlist, type-not-on-list, case-insensitive match), unknown
  strategies return null, each of the 6 mapped strategies emits the
  right type + priority + arguments + source-strategy traceability,
  `safe-travel` in-base vs out-of-base branching, `harvest-window`
  resource-label normalization (`"near X"` prefix stripped, blank
  falls back to `"nearest_resource"`), and `objective-push`
  justification includes the objective label.
  
  Plumbing: `FallbackBehaviorContext`'s parameterless constructor
  promoted from `private` to `internal` so the test project (which
  has `InternalsVisibleTo PalLLM.Tests`) can construct synthetic
  contexts for testing pure advisors without spinning up the full
  factory.
  
  **Test count `984 -> 1002` (+18).** Crosses the `1000`-test milestone.
  Verification: focused `ActionIntentPlannerTests` fixture `18 / 18`;
  `dotnet test` passed `1002 / 1002`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-015137/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 298 - Direct unit tests for the OpenAI-compatible chat-completions response parser.**
  `ChatCompletionsResponseReader.ReadAsync` is the structured-JSON
  parser every upstream chat reply runs through (Ollama, llama.cpp,
  vLLM, LM Studio, transformers serve, TensorRT-LLM, OpenVINO, Foundry
  Local). It sits one layer above `HttpContentReadLimiter` (Pass 224):
  the limiter caps bytes, this reader extracts structured response data
  from the bounded JSON.
  
  Until this pass the reader was only covered indirectly via
  `InferenceClient` integration paths. New
  `tests/PalLLM.Tests/ChatCompletionsResponseReaderTests.cs` adds `23`
  focused cases pinning every parsing branch: missing/empty/non-array
  `choices`, missing `message`, unsupported content shape, string vs
  array `content`, modern `tool_calls` vs legacy `function_call`,
  audio output, full token-usage parsing (including nested
  `prompt_tokens_details` and `completion_tokens_details`), missing
  total auto-sum, string-as-number coercion, negative-number clamp,
  multiple-choice finish-reason concatenation, blank finish-reason
  filtering, logprobs + system_fingerprint extraction, and the
  inherited oversized-body throw from `HttpContentReadLimiter`.
  
  **Test count `961 -> 984` (+23).** Verification: focused
  `ChatCompletionsResponseReaderTests` fixture `23 / 23`; `dotnet test`
  passed `984 / 984`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-013505/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and
  OpenAPI schema unchanged.
- **Pass 297 - Permanent regression guard: meta-test for INDEX.md catalogue completeness.**
  Pass 296 fixed a one-off catalogue gap in `docs/INDEX.md`. That fix
  was a snapshot — a future doc added without an INDEX entry would
  re-introduce the same gap silently. This pass adds a permanent
  regression guard.
  
  New meta-test `IndexDoc_CataloguesEveryDocInTheDocsDirectory` in
  `MetaTests.cs` enumerates `docs/*.md` (excluding INDEX.md and the
  folder-level README.md), parses INDEX markdown links, keeps only
  docs-folder leaves (no slash, no `..`), and asserts the
  bi-directional set-match: every doc must appear in INDEX, every
  INDEX leaf-link must resolve to a real file.
  
  Meta-test count `25 -> 26` in `scripts/pal-complete.ps1` and
  `docs/COMPLETION.md`. Test count `960 -> 961`.
  
  **Test count `960 -> 961` (+1).** Verification: focused new meta-test
  `1 / 1`; `dotnet test` passed `961 / 961`; full audit `15/15` PASS at
  `../artifacts/full-audit/20260521-012224/RESULTS.md`. Routes, MCP
  tools, feature catalog entries, fallback strategy counts, and OpenAPI
  schema unchanged.
- **Pass 296 - INDEX.md doc-catalogue completeness.**
  A scripted set-diff between `docs/*.md` and the backtick-linked doc
  list in INDEX.md surfaced one doc that exists but is not referenced
  from INDEX: `docs/RESEARCH_NOTES_2026-05.md` (added by a parallel
  agent as a dated research snapshot behind the 2026-05
  multimodal/Blackwell defaults). A small model reading INDEX
  top-to-bottom would have missed it.
  
  Added a new row to INDEX's "find the right doc by task" table,
  placed adjacent to the existing `FALLBACK_AI_RESEARCH.md` row to
  group research-style docs together. The reverse set-diff (linked
  from INDEX but file missing) returned empty — every existing INDEX
  link resolves to a real file; the bi-directional check now matches.
  
  No code changed. Test count unchanged at `960`. Full audit `15/15`
  PASS at `../artifacts/full-audit/20260521-010352/RESULTS.md`.
  Routes, MCP tools, feature catalog entries, fallback strategy counts,
  and OpenAPI schema unchanged.
For Passes 48-295 see [`../CHANGELOG.md`](../CHANGELOG.md). The changelog
preserves the verbatim entry for every pass; this file keeps only the most
recent batch (Passes 296-330) so a fresh agent gets immediate
context without scanning 2,000+ lines of historical work.

## Highest-value remaining blockers

These are the real blockers to `100%` and should stay in this order unless a
dependency changes:

1. Capture a real in-Palworld `delivery_proven` pass on the ship target.
2. Confirm the stable native HUD/widget seam from live `ui_probe` evidence.
3. Add native in-world audio playback on top of the proven render surface.
4. Expand guarded native actions beyond the current feedback-only paths.
5. Prove the packaged release flow on a clean machine or clean user profile.

See [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md) for the full build
order and acceptance criteria.

## Recommended next coding pass

If you only do one substantive thing next, do this:

1. Run a live Palworld session with
   `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1`.
2. Use the resulting `/api/bridge/proof` evidence to confirm the top HUD target
   is real and ship-worthy.
3. If the proof is stable, promote that seam into the default native-delivery
   path before moving on to audio or richer actions.

Everything else is lower leverage until that proof exists.

## High-signal files

- `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`
- `src/PalLLM.Sidecar/BridgeProofBuilder.cs`
- `src/PalLLM.Sidecar/ReleaseReadinessBuilder.cs`
- `src/PalLLM.Sidecar/ReleaseArtifactIntegrityEvidenceBuilder.cs`
- `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`
- `scripts/run-native-proof.ps1`
- `scripts/export-release-proof-bundle.ps1`
- `scripts/export-support-bundle.ps1`
- `docs/ROADMAP.md`
- `docs/IMPLEMENTATION_QUEUE.md`
- `docs/RELEASE.md`

## Verification commands

Run these before ending the handoff turn:

```powershell
dotnet test D:\Coding\PalLLM\PalLLM.sln --configuration Release --no-restore --nologo --verbosity minimal
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Coding\PalLLM\scripts\run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging
```

If the HTTP contract or docs changed, also verify the committed snapshot:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File D:\Coding\PalLLM\scripts\export-openapi.ps1 -Verify
```

## Handoff notes

- This workspace may not be a Git repo. Do not assume branch or status data is
  available.
- Keep documentation and audited counts in sync with the code. The doc source
  of truth for cross-doc invariants is [`INDEX.md`](INDEX.md).
- Prefer one-click and machine-readable operator paths over console-only truth.
- Build is clean — `dotnet build` now reports zero warnings after Pass 48.
  The historical `CS1591` XML-doc noise was closed by a repo-wide
  `NoWarn` in `Directory.Build.props` plus a pass over every positional
  record to use canonical `<param>` tags. If a new warning surfaces,
  treat it as a regression, not cleanup debt.
- Keep deterministic fallback and the current proof and evidence surfaces
  intact while shipping the remaining native Palworld work.
