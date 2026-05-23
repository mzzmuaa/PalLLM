# PalLLM Operations

Audience: someone already comfortable with the runtime who now has to keep it healthy in production.

Last audited: `2026-05-23`

This is a how-to guide in the [Diataxis](https://diataxis.fr/) sense - each section answers a specific operational question. Skim the table of contents and skip to what you need.

> **First-time here?** Read `docs/QUICKSTART.md` for a learning-oriented walkthrough, or `docs/ARCHITECTURE.md` for the design story.
>
> **Tuning specific parameters?** Every configurable knob - defaults, min/max, too-low / too-high guidance, how to test - lives in one place: [`TUNING.md`](TUNING.md).

## Contents

1. [Palworld and UE4SS compatibility](#palworld-and-ue4ss-compatibility)
2. [Health probes](#health-probes)
3. [Metrics scraping](#metrics-scraping)
4. [Watching for trouble](#watching-for-trouble)
5. [Tuning retention](#tuning-retention)
6. [Opt-in feature matrix](#opt-in-feature-matrix)
7. [Turning on live inference safely](#turning-on-live-inference-safely)
8. [Turning on vision safely](#turning-on-vision-safely)
9. [Turning on TTS](#turning-on-tts)
10. [Enabling the action executor](#enabling-the-action-executor)
11. [Enabling the native HUD bind](#enabling-the-native-hud-bind)
12. [Enabling the production sampler](#enabling-the-production-sampler)
13. [Container deployment](#container-deployment)
14. [Enabling API-key authentication](#enabling-api-key-authentication)
15. [Enabling distributed tracing](#enabling-distributed-tracing)
16. [Configuring tiered model loading](#configuring-tiered-model-loading)
17. [Fallback coverage matrix](#fallback-coverage-matrix)
18. [Exposing PalLLM via MCP](#exposing-palllm-via-mcp)
19. [Troubleshooting](#troubleshooting)
20. [Upgrades and schema migration](#upgrades-and-schema-migration)

---

## Palworld and UE4SS compatibility

PalLLM does not pin itself to a single Palworld patch. The mod is
resilient by design: every Pal-specific UE4SS hook is wrapped in a
`register_hook_safely` helper that reports whether the target class
resolved at startup.

**What this means in practice:**

- A renamed class in a new Palworld patch logs a
  `[PalLLM][Compat] hook registration failed: ...` line and no longer
  contributes events, but the mod does not crash and every other hook
  keeps working.
- The first `bridge_boot` event written after startup carries a `Compat`
  field listing which core classes (`PalGameStateInGame`, `PalCharacter`,
  `PalWeatherManager`, `PalBaseCampManager`, `PalMapManager`,
  `UserWidget`) were found. Operators can read it from the archived
  envelope under `Bridge/Archive` or watch the UE4SS console.
- The dashboard's bridge activity surface reflects which event types
  have actually arrived, so missing `combat_*` or `pal_status` after a
  play session is a visible compat signal.

**Targeted versions:**

| Component | Tested / expected | Notes |
|---|---|---|
| Palworld | tracked "current public build" as of the last audit | Hook names (`BroadcastChatMessage`, `Notify_OnWeatherChanged`, `Notify_OnInvaderSpawned`, `ReceiveAnyDamage`, `OnDead_ToBP`, `ServerAcknowledgePossession`) are standard UE4 naming and have been stable across the public Palworld releases tracked by the repo. |
| UE4SS | `v3.x` or newer | Uses `RegisterHook`, `RegisterKeyBind`, `FindFirstOf`, `FindAllOf`, `LoopAsync`, `ExecuteWithDelay`, `NotifyOnNewObject`. All are stable UE4SS Lua API surface from 3.0 onward. |
| .NET runtime | `.NET 10.0` (LTS) | Enforced by `net10.0` target framework in all three C# projects. Supported through November 2028. |
| OS | Windows 10/11 | Sidecar itself is portable .NET 10 and runs on Linux/macOS, but the UE4SS bridge + Palworld are Windows-only. |

**When a patch breaks a hook**, fix is local:

1. Open `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`.
2. Locate the `register_hook_safely` call for the failing path.
3. Update the `/Script/Pal.Class:Function` string to the new symbol.
4. Reload UE4SS (or relaunch the game). The compat probe will log the
   new resolution state on the next boot.

PalLLM never blocks gameplay - a failed hook just means its event type
stops arriving, and the deterministic fallback director still produces
complete replies from whatever world state it can still see.

---

## Health probes

PalLLM exposes the standard K8s / container-orchestrator pair:

- `GET /health/live` - "process is up and serving HTTP". Use for liveness restart decisions.
- `GET /health/ready` - "process is up AND ready to take traffic". Reports `Degraded` when the inference circuit breaker is open or the outbox/screenshot queues have backed up past warning thresholds.

The `/health/ready` body includes diagnostic data under `data` (adapter ready flag, circuit state, pending file counts, session dirty flag). A scraper can surface that context without hitting `/api/health` separately.
It now also includes a second readiness result named
`inference_recent_window`. That result mirrors the bounded
`GET /api/inference/performance` assessment directly into the health payload,
including the recent-window status, sample counts, and a bounded
`alerting_lanes[]` preview. When the live recent window slips to `degraded` or
`critical`, the overall `/health/ready` payload reports top-level
`status=Degraded` while still returning HTTP `200`, which keeps lightweight
probes alive but makes the body useful for operator nuance.

`GET /api/health` now also carries a `NativeReadiness` snapshot that separates
"the sidecar is healthy" from "the Palworld-native seams are actually proven."
Use it, or `scripts/doctor.ps1`, when you need to know whether a bridge boot
heartbeat has been seen, whether the HUD bind has enough `ui_probe` evidence,
and whether native production / waypoint paths are genuinely ready on the
running build.

`GET /api/bridge/proof` is the machine-readable companion surface for that
question. It rolls the native-readiness snapshot, the latest `ui_probe`
evidence, and the current request/delivery/action loop proof into one
harvestable payload so release tooling and dashboards do not need to join
multiple endpoints.

`GET /api/release/readiness` is the release-facing companion surface. Its
`SmokeEvidence` block reflects the latest durable smoke artifact at
`Runtime/ReleaseEvidence/latest-smoke.json`, which is written by
`scripts/run-sidecar-smoke.ps1`. Its `NativeProofEvidence` block reflects the
latest live Palworld-native proof artifact at
`Runtime/ReleaseEvidence/latest-native-proof.json`, which is written by
`scripts/run-native-proof.ps1`. Its `ProofBundleEvidence` block reflects the
latest packaged release-proof manifest/archive at
`Runtime/ReleaseEvidence/latest-proof-bundle.json` plus `.zip`, which are
written by `scripts/export-release-proof-bundle.ps1`.
Its `SupportBundleEvidence` block reflects the latest portable support-bundle
manifest/archive at `Runtime/SupportEvidence/latest-support-bundle.json` plus
`.zip`, which are written by `scripts/export-support-bundle.ps1` or packaged
`support.bat`. `ProofBundleEvidence` and `SupportBundleEvidence` both expose
`PrivacyRedactionApplied`, `PrivacyRedactionCheckedFileCount`,
`PrivacyRedactionRedactedFileCount`, `PrivacyRedactionRuleHits`,
`PublicationScanPassed`, `PublicationScanCheckedFileCount`, and
`PublicationScanViolations` after redacting and scanning the portable archive
text surface. Missing privacy-redaction evidence or a failed publication scan
marks the next pass as recapture work before tester handoff. The
release-readiness reader also verifies the paired proof/support zip is a
readable archive with the expected bundle manifest, manifest-listed entries,
and relative path-safe entry names, so a stale, mismatched, or path-confused
archive is flagged before it reaches support.
Its `PackageVerificationEvidence` block reflects the latest verified release
package artifact at `Runtime/ReleaseEvidence/latest-package-verification.json`,
which is written by `scripts/package-release.ps1` or
`scripts/verify-release-package.ps1`. That artifact includes the manifest
check plus the packaged text-surface publication scan result
(`PublicationScanPassed`, checked file count, and violations) for
sibling-project bleed, endorsement/approval claims, unrelated franchise
references, broad platform-scope drift, and root player-copy brand drift.
Its `ArtifactIntegrityEvidence` block reflects the latest checksum/signature
posture at `Runtime/ReleaseEvidence/latest-artifact-integrity.json`, which is
written by `scripts/compute-release-checksums.ps1` after candidate packaging.
That artifact points at `SHA256SUMS`, `SHA512SUMS`, and `checksums.json` under
`artifacts/packaging/`, records artifact count, and reports whether detached
signature files were present when the digest manifests were computed.
Its `FullAuditEvidence` block reflects the latest durable source-tree audit
artifact at `Runtime/ReleaseEvidence/latest-full-audit.json`, which is written
by `scripts/run_full_audit.ps1` and points back to the timestamped
`artifacts/full-audit/<stamp>/` bundle plus `RESULTS.md`.

The packaged player launcher leaves behind its own support artifact too:
`scripts/play-palllm.ps1` now persists
`Runtime/LaunchEvidence/latest-player-launch.json` plus `.md`, alongside
timestamped history files, so support can inspect the exact install target,
sidecar status, doctor/warmup outcome, and latest bridge-proof /
release-readiness posture from the player's most recent one-click run without
asking them to paste console output.

For the next step up, `scripts/export-support-bundle.ps1` and the package-root
`support.bat` collect those same launch artifacts plus the latest health,
bridge-proof, and release-readiness snapshots, then archive them under
`Runtime/SupportEvidence/latest-support-bundle.zip` with a matching `.json`
manifest. Use that when you want one file that captures the current PalLLM
support posture without bundling raw session memory or player chat history.
The staged text files are privacy-redacted before the portable zip is written,
so local user-profile paths and bearer/API-key-like values are scrubbed from
the archive copy while release-readiness still keeps enough local path evidence
to re-open and verify the paired archive on the same machine.

That payload now includes `NativeReadiness.HudBindRecommendation`, which is
the fastest way to answer "what exact Palworld widget should I bind next?" It
includes:

- a compact status (`recommend_target`, `configured_targets_need_review`,
  `bind_ready`, and related blocked states)
- the exact recommended first `native_hud_widget_targets` entry
- the currently reported configured target list from the latest `bridge_boot`
- the live native-hud config source/path reported by the bridge
- a short ranked shortlist copied from the `ui_probe` diagnostics

`scripts/doctor.ps1` surfaces the same recommendation as a dedicated
`HUD target recommendation` check so operators do not need to inspect the raw
JSON unless they want the full evidence. The same doctor pass now also surfaces
the latest persisted native-proof artifact separately from the synthetic smoke
artifact, and also reports whether the latest packaged proof bundle, support
bundle, and package-verification artifact are available, archive-shape/path
verified, and publication-scan clean.

`GET /api/health` also carries the active inference lane state:
`InferenceActiveModel`, `InferenceActiveTierId`,
`InferenceLastSeenAvailableModels[]`, and the bounded `InferenceWarmup`
snapshot. `/health/ready` mirrors the warmup summary in
`results.readiness.data.inference_warmup_status`, so lightweight probe
consumers can still tell whether the active lane is warm without calling the
full runtime-health endpoint. That snapshot now also reports the latest
successful live chat hit on the active lane, so operators can tell whether a
keepalive was skipped because real traffic already kept the model warm.

`GET /api/inference/performance` is the recent-window companion surface for
that lane state. It summarizes the last `15` minutes of live chat and vision
work by provider/model lane, including success rate, average latency, p95,
token totals, the latest error marker, and a first-class budget/readiness
assessment. The default budget thresholds are currently `3.0 s target / 8.0 s
ceiling` for chat lanes and `2.5 s target / 6.0 s ceiling` for vision lanes.
The response classifies the recent window and each lane as `healthy`,
`degraded`, `critical`, `insufficient_data`, or `no_data`, so operators can
spot a slow or flaky lane before it becomes a full readiness issue.
That same status now fans out to `/health/ready` and `/metrics`, so alerting
does not have to poll the heavier JSON endpoint just to see whether a lane has
fallen outside its proven budget.

`GET /api/health` also carries `BridgeLoop`, which answers a different
question: did the latest tracked Palworld turn actually complete the bridge
loop? The state machine is intentionally compact:

- `awaiting_reply` - sidecar saw a request but has not written a `chat_reply`
- `awaiting_delivery` - sidecar wrote the outbox reply but has not seen
  `reply_delivery` from the UE4SS renderer yet
- `awaiting_action_feedback` - delivery was confirmed and an action was
  planned, but the matching feedback event has not arrived yet
- `closed` - delivery was confirmed and any planned feedback arrived

That distinction matters in practice: "HTTP is healthy" is not the same as
"Palworld actually rendered the turn."

Curl sketch:

```bash
curl -sf http://localhost:5088/health/live    # exit 0 means live
curl -sf http://localhost:5088/health/ready   # exit 0 means ready; body holds nuance
```

### HTTP contract and overload guardrails

PalLLM now treats its HTTP surface as a production contract, not a debug-only
convenience:

- `GET /openapi/v1.json` and `GET /openapi/v1.yaml` are generated directly from
  the minimal-API route registrations and cached server-side for
  `PalLLM:Http:OpenApiCacheMinutes` (10 minutes by default). Set the value to
  `0` to disable the cache for live contract iteration.
- the repo also commits a build-time snapshot at
  `docs/openapi/palllm-sidecar-v1.json`; `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-openapi.ps1 -Verify`
  is the contract-drift gate used by CI and `scripts/run_full_audit.ps1`.
- `scripts/run_full_audit.ps1` now also builds and verifies a candidate
  release zip by default, so the audit proves release-package shape as well
  as source/doc drift, and it persists a durable latest-full-audit artifact
  under `Runtime/ReleaseEvidence`. Use `-SkipPackaging` when you only want a
  faster source-only iteration pass.
- `GET /api/features`, `GET /api/bridge/proof`, and
  `GET /api/release/readiness` participate in conditional caching with the
  feature-catalog TTL. `GET /api/describe` uses its own short
  `PalLLM:Http:SelfDescriptionCacheSeconds` TTL (`15` seconds by default) so
  reconnecting AI/MCP clients can skip repeated manifest rebuilds without
  holding stale runtime posture for long. `GET /api/inference/performance`
  deliberately uses `Cache-Control: private, no-cache, must-revalidate` so
  dashboards always revalidate the latest recent-window lane posture before
  repainting. `GET /api/mcp/upstream` uses its own shorter upstream-snapshot
  TTL. All six emit strong `ETag` values so dashboards and tooling can
  revalidate cheaply instead of redownloading unchanged JSON on every poll.
  The two mutable proof surfaces (`/api/bridge/proof` and
  `/api/release/readiness`) deliberately avoid server-side output caching so a
  fresh smoke, native-proof, or support-bundle capture is visible immediately.
- `POST /api/chat`, `POST /api/vision/*`, and `POST /api/tts/synthesize` sit
  behind explicit concurrency limiters. When the local runtime is already using
  its configured work budget, the endpoint returns `429 Too Many Requests` with
  a standard `ProblemDetails` body instead of letting burst traffic wreck local
  latency for every caller.
- Protected `/api/*` and `/mcp` routes now also return standard
  `ProblemDetails` bodies on `401 Unauthorized` responses while preserving
  `WWW-Authenticate: Bearer`, so clients see one consistent error shape across
  validation, limiter, and auth failures. Browser-originated `/mcp` requests
  that fail the origin check receive the same standard `ProblemDetails` shape
  on `403 Forbidden`.
- The limiter budgets are intentionally separate per lane: chat, vision, and
  TTS can be tuned independently in [`TUNING.md`](TUNING.md) under the
  `PalLLM:Http:*` block.

---

## Metrics scraping

`GET /metrics` returns Prometheus exposition format v0.0.4. The metric set is bounded-cardinality (no per-request labels), designed for a single-tenant sidecar.

### Representative counters

| Metric | Meaning |
|---|---|
| `palllm_inference_success_total` | Successful live-inference calls |
| `palllm_inference_failure_total` | HTTP errors, timeouts, unreachable endpoints |
| `palllm_inference_bypass_total` | Calls short-circuited by the fast-path policy |
| `palllm_fallback_reply_total` | Replies served from the deterministic director |
| `palllm_rate_limited_total` | Chat requests diverted by the per-character limiter |
| `palllm_vision_call_total` / `_failure_total` | Vision calls and their outcomes |
| `palllm_inference_{prompt,completion,total}_tokens_total` | Token billing counters |
| `palllm_bridge_event_total` / `_boot_total` | Lua-bridge traffic |

### Representative gauges

| Metric | Meaning |
|---|---|
| `palllm_memory_entries` | Live entries in the conversation memory stream |
| `palllm_tracked_relationship_count` | Characters with an active relationship record |
| `palllm_outbox_pending_files` | Chat replies awaiting a game-side consumer |
| `palllm_inbox_pending_files` | Unprocessed bridge events |
| `palllm_screenshot_pending_files` | Screenshots awaiting vision extraction |
| `palllm_archive_files` / `_failed_files` | Current footprint under the runtime-root archive / failed directories |
| `palllm_session_dirty` | `1` when session state has unsaved mutations, else `0` |
| `palllm_inference_circuit_failures` | Consecutive failures driving the breaker state |
| `palllm_inference_recent_window_sample_count` | Live chat/vision operations currently retained in the bounded readiness window |
| `palllm_inference_recent_window_success_ratio_percent` | Aggregate recent-window success ratio |
| `palllm_inference_recent_window_target_hit_ratio_percent` | Aggregate recent-window target-budget hit ratio |
| `palllm_inference_recent_window_ceiling_hit_ratio_percent` | Aggregate recent-window ceiling-budget hit ratio |
| `palllm_inference_recent_window_degraded_lanes` / `_critical_lanes` | Count of active recent-window lanes currently outside the healthy posture |

### Labeled counters (bounded cardinality)

| Metric | Meaning |
|---|---|
| `palllm_fallback_strategy_total{strategy="..."}` | Per-strategy usage count from the 19 deterministic fallback strategies. Grafana - breakdown of which strategies are actually hitting in production vs authored. |
| `palllm_model_tier_transition_total{from="...",to="..."}` | Model-tier graduation counter. Typical pattern: one `<none> -> small` on startup, one `small -> large` when the large model finishes pulling. Persistent flapping between tiers signals an unreliable upstream. |
| `palllm_inference_recent_window_status{status="...",budget="..."}` | One-hot recent-window readiness state. Exactly one status (`healthy`, `degraded`, `critical`, `insufficient_data`, `no_data`) is `1`. |
| `palllm_inference_lane_status{operation="...",provider="...",model="...",budget="...",status="..."}` | One-hot per-lane readiness state for each active lane in the recent window. The label set is bounded to PalLLM's active recent-window lanes, not per-request traffic. |
| `palllm_inference_lane_sample_count{operation="...",provider="...",model="...",budget="..."}` | Sample count backing each active recent-window lane assessment. |

### Chat latency histogram

| Metric | Meaning |
|---|---|
| `palllm_chat_duration_seconds_bucket{le="..."}` | Cumulative Prometheus histogram of end-to-end `/api/chat` latency. 13 buckets from 5 ms to 60 s covering fallback-only paths through large-model inference. |
| `palllm_chat_duration_seconds_sum` / `_count` | Matching sum + count suffixes so Grafana's `histogram_quantile()` reads the shape natively. |

Example PromQL queries:

```promql
# p95 chat latency over the last 5 minutes
histogram_quantile(0.95, sum(rate(palllm_chat_duration_seconds_bucket[5m])) by (le))

# Fallback-strategy mix (useful when auditing 'which strategies never fire')
sum(increase(palllm_fallback_strategy_total[1h])) by (strategy)

# Tier-graduation latency check: how often the orchestrator bumps back down
sum(rate(palllm_model_tier_transition_total{to!~"large.*"}[1h])) by (from, to)

# Any recent-window lane that is currently critical
max by (operation, provider, model, budget) (palllm_inference_lane_status{status="critical"})

# Overall recent-window readiness is degraded or critical
max by (status, budget) (palllm_inference_recent_window_status{status=~"degraded|critical"})
```

### Minimal Prometheus scrape config

```yaml
scrape_configs:
  - job_name: palllm
    metrics_path: /metrics
    static_configs:
      - targets: ['localhost:5088']
    scrape_interval: 15s
```

---

## Watching for trouble

Read these three things on an interval to stay ahead of drift:

1. **Inference circuit state** (`palllm_inference_circuit_failures`, `data.inference_circuit` in readiness). If it's `Open`, the upstream LLM is unreachable and PalLLM is serving pure fallback - users will still get replies, but creative/reasoning tasks are degraded.
2. **Outbox backlog** (`palllm_outbox_pending_files`). A steady climb means no UE4SS consumer is draining. Either the Lua mod isn't running, or Palworld isn't launched.
3. **Session dirty flag** (`palllm_session_dirty` stuck at `1`). Autosave is running every `Session.AutosaveIntervalSeconds` by default; if this stays at `1` the worker is stuck or disk I/O is failing.

Log hints:

- `PalLLM bridge inbox worker failed` - the drain loop threw. Look for the inner `ex.Message`.
- `PalLLM screenshot watcher prune pass failed` - retention sweep couldn't delete files. Check permissions on `Bridge/Screenshots`.
- `Session autosave failed: ...` - usually disk full or permission. `/health/ready` will shortly report `Degraded`.

---

## Tuning retention

All runtime-owned directories have a bounded-retention policy that runs inline with each write.
The sweep uses lazy file enumeration and a bounded newest-file queue, so stale
archive, failed, screenshot, outbox, diagnostics, and TTS cleanup remains
predictable even after a long unattended session.

| Directory | Option | Default |
|---|---|---|
| `Bridge/Outbox` | `PalLLM:Bridge:OutboxMaxFiles` / `OutboxMaxAgeHours` | 100 / 24h |
| `Bridge/Archive` | `PalLLM:Bridge:ArchiveMaxFiles` / `ArchiveMaxAgeHours` | 500 / 72h |
| `Bridge/Failed` | `PalLLM:Bridge:FailedMaxFiles` / `FailedMaxAgeHours` | 200 / 168h |
| `Bridge/Screenshots` (pending) | `PalLLM:Vision:PendingScreenshotMaxFiles` / `PendingScreenshotMaxAgeHours` | 32 / 1h |
| `TTS/` | `PalLLM:Tts:MaxStoredFiles` / `MaxStoredAgeHours` | 128 / 24h |

Guidance:

- Long sessions without a running UE4SS consumer can fill the outbox cap in about an hour of heavy chatter. Either raise the cap, or stop the sidecar when the game isn't running.
- TTS synthesis typically produces tens of KB per second of generated audio; 128 files is well under the 10 MB range for a typical speech engine. Safe default for most operators.
- Archive files are the full bridge-event history. Drop the cap lower if disk pressure matters.

---

## Opt-in feature matrix

Every PalLLM subsystem that costs something (a model, a background worker,
or in-game side effects) is opt-in and individually reversible. This table
is the quick-glance overview; the sections below go deeper per feature.

| Surface | Default | Flip | Verify it's on | Roll back |
|---|---|---|---|---|
| API-key authentication | OFF | `PalLLM:Auth:ApiKey=<key>` | `/api/*` returns 401 without `Authorization: Bearer <key>` | clear the key |
| Distributed observability (OpenTelemetry OTLP) | OFF | `OTEL_EXPORTER_OTLP_ENDPOINT=http://host:4317` | `pal.chat` spans, `gen_ai.client.*` histograms, and `palllm.inference.*` readiness gauges appear in your OTLP backend | unset env var, restart |
| Live inference | OFF | `PalLLM:Inference:Enabled=true` | `palllm_inference_success_total` > 0 | flag `false`, restart |
| Vision describe + world-state | OFF | `PalLLM:Vision:Enabled=true` | `POST /api/vision/describe` -> `Success=true` | flag off |
| Structured vision outputs | ON | set `PalLLM:Vision:UseStructuredOutputs=false` to disable | `response_format` in outgoing request body | flag false |
| Screenshot watcher | OFF | `PalLLM:Vision:EnableScreenshotWatcher=true` | `ScreenshotPendingCount` trends to 0 | flag off |
| TTS synthesis | OFF | `PalLLM:Tts:Enabled=true` | `POST /api/tts/synthesize` -> `FilePath` | flag off |
| Action intents (advisory) | OFF | `PalLLM:Automation:Enabled=true` + populated `AllowedActions` | `ChatResponse.Action` is non-null | clear allowlist |
| Guarded executor (Lua side) | ON | flip `action_executor_enabled=false` in `main.lua` | `[PalLLM][ActionExec] executor disabled` print | reverse |
| Native HUD widget bind | OFF | `native_hud_render_enabled=true` + populate `native_hud_widget_targets` | `[PalLLM][HudRender] ...` print | flag off |
| Native audio mixer callback | OFF | `native_audio_mixer_enabled=true` + installed `PalLLM_NativeAudioMixer_PlayRawPcm` callback | `/api/bridge/proof` shows started `native_mixer` raw PCM receipt | flag off |
| Native waypoint marker | ON | flip `waypoint_native_marker_enabled=false` | trace note `"native marker via ..."` | reverse |
| Production sampler | OFF | `production_sampler_enabled=true` in `main.lua` | `production` events in bridge log | flag off |
| Reflection consolidation | OFF | `PalLLM:Fallback:EnableReflection=true` | memory entry tagged `reflection` | flag off |
| Task-focus directive | OFF | `PalLLM:Fallback:PreferTaskFocus=true` | prompt includes the directive | flag off |
| Per-character rate limit | OFF | `PalLLM:Fallback:MaxCharacterRequestsPerMinute=N` | `palllm_rate_limited_total` on burst | set to 0 |
| GPU thermal gate | OFF | `PalLLM:Inference:ThermalGate:Enabled=true` | inference trace `ErrorType=thermal_gated` under heat; otherwise no effect | flag off |

All feature flags live in `appsettings.json` except the Lua-side ones,
which are module-level locals at the top of `main.lua`. Changes to
`appsettings.json` require a sidecar restart; changes to `main.lua` take
effect on the next UE4SS reload.

Rolling back any of the above is a reverse flag edit - there is no
persistent state migration to undo. Session data in
`runtime-root/session.json` carries a `SchemaVersion` and the loader
refuses any future-schema file (a `.bak` fallback preserves the
last-known-good), so a downgrade never silently mis-loads state.

---

## Turning on live inference safely

1. Run any HTTP server that implements the JSON chat-completions schema and
   pull whichever model tag you intend to use.
2. Flip `PalLLM:Inference:Enabled=true` in `appsettings.json`. Set
   `BaseUrl` and `Model` to match your server. Restart the sidecar; the
   options validator will fail fast if either is invalid.
3. Watch `palllm_inference_success_total` rise and
   `palllm_inference_failure_total` stay near zero.
4. The circuit breaker defaults to 5 consecutive failures -> 30-second
   cooldown. A misconfigured endpoint will surface as
   `data.inference_circuit=Open` on readiness within a few seconds - the
   deterministic fallback still carries the reply.
5. Successful upstream chat-completions JSON bodies are capped by
   `PalLLM:Inference:MaxResponseBytes=65536` (64 KB default), so a verbose or
   broken endpoint cannot stream an unbounded payload into the sidecar.

`PalLLM:Inference:TokenBudgetField` defaults to `max_tokens` because that
field is still the broadest local-runtime compatibility path. Set it to
`max_completion_tokens` only for an exact endpoint/model that rejects
`max_tokens` or requires the newer reasoning-model budget field. Before
promoting that setting, replay the same PalLLM route with both field names and
record accepted request shape, usage counters, p95 latency, and fallback
counters.

For a shared vLLM endpoint, `PalLLM:Inference:PrefixCacheSalt` can forward a
stable non-secret `cache_salt` on chat-completions requests. Use one salt per
player/save/profile trust domain when cache isolation matters; do not rotate it
per request unless you intentionally prefer isolation over prefix-cache hits.
For vLLM startup, prefer `--prefix-caching-hash-algo sha256_cbor` when
deterministic cross-version cache identity matters, and prove sticky or KV
cache-aware routing beats round-robin before putting multiple replicas behind a
live PalLLM companion lane.

For a shared vLLM endpoint that was launched with `--scheduling-policy priority`,
`PalLLM:Inference:RequestPriority` can forward a `priority` integer on
chat-completions requests. Leave it `null` by default: lower values are more
urgent on vLLM priority schedulers, but non-zero values can be rejected by
FCFS-only vLLM servers or strict non-vLLM endpoints. Before promoting it, replay
a short companion turn beside a long proof/docs prompt and confirm the companion
lane wins queue time without starving the background lane.

For replay comparisons, `PalLLM:Inference:Seed` can forward an
OpenAI-compatible `seed` on chat-completions requests. Leave it `null` for
normal play; use it only after the exact server/model accepts the field, and
record the seed, served model id, runtime version, system fingerprint if
exposed, replica layout, and output drift beside any proof result.

For repetition-control canaries, `PalLLM:Inference:FrequencyPenalty` can
forward OpenAI-compatible `frequency_penalty` on chat-completions requests.
Leave it `null` for normal play; use it only after the exact endpoint/model
accepts the field, then replay long companion turns with and without it and
record repeated-phrase rate, generated tokens, latency, and fallback counters.

For local sampler canaries, `PalLLM:Inference:TopK`,
`PalLLM:Inference:MinP`, and `PalLLM:Inference:RepetitionPenalty` can forward
non-standard `top_k`, `min_p`, and `repetition_penalty` fields on
chat-completions requests. Leave them `null` for normal play; use them only
after the exact local runtime accepts the fields, then replay companion,
strict JSON/tool, and long proof turns with and without the sampler change and
record style/loop deltas, generated tokens, p95 latency, parser stability, and
fallback counters.

For strict future action/directive lanes, `PalLLM:Inference:ParallelToolCalls`
can forward OpenAI-compatible `parallel_tool_calls`. Leave it `null` for normal
play. Set it to `false` only on an endpoint that accepts the field and after a
canary proves the route returns zero or one tool call with valid arguments.
Route-specific proof callers can also set prompt-level
`InferencePrompt.Tools` plus `InferencePrompt.ToolChoice`; PalLLM forwards
them as `tools` and `tool_choice` only for that call and preserves returned
`tool_calls` as a receipt. Ordinary companion chat omits those fields, and
tool-call-only responses must still have a deterministic fallback path before
any action/directive route is promoted.

For predicted-output proof lanes, route-specific callers can set
`InferencePrompt.Prediction`; PalLLM forwards it as `prediction` only for that
call. Use it for stable proof/docs scaffolds after the exact endpoint accepts
the field, then compare accepted/rejected prediction-token receipts when
exposed, p95 latency, and fallback counters against the same route without the
field. Ordinary companion chat omits `prediction` so strict local endpoints
stay portable.

For confidence or evaluator proof lanes, route-specific callers can set
`InferencePrompt.Logprobs` and optional `InferencePrompt.TopLogprobs`; PalLLM
forwards `logprobs` / `top_logprobs` only for that call and preserves returned
choice-level `logprobs` JSON as a receipt. Use it only after the exact
endpoint accepts the fields, then compare returned receipt shape, response
bytes, p95 latency, and fallback counters against the same route without the
fields. Ordinary companion chat omits them.

For isolated audio-output proof lanes, route-specific callers can set
`InferencePrompt.Modalities` and `InferencePrompt.Audio`; PalLLM forwards
OpenAI-compatible `modalities` / `audio` only for that call and preserves
returned `message.audio` JSON on `InferenceResult.AudioJson`. Use it only
after the exact endpoint accepts the fields, then compare the returned audio
receipt, text mirror, response bytes, p95 latency, and fallback counters
against the same route without the fields. Ordinary companion chat omits them.

For multimodal input proof lanes, route-specific callers can set
`InferencePrompt.UserContent`; PalLLM forwards that value as the user message
`content` only for that call. Use it for content-part arrays such as `text`,
`image_url`, `video_url`, `input_audio`, and endpoint-proven `audio_url` after
media-admission checks have bounded local bytes or blocked remote-media SSRF
cases. Ordinary companion chat keeps a plain string user message.

For strict delimiter or low-latency canaries, `PalLLM:Inference:StopSequences`
can forward up to four OpenAI-compatible `stop` strings. Leave the list empty
for normal play. Add delimiters only after the exact endpoint/model proves it
accepts `stop`, reduces generated tokens, and does not clip useful companion
text.

### Residency control for local runtimes

PalLLM now distinguishes "send a tiny warmup request" from "keep the active
lane resident on a host that supports explicit residency controls."

- `PalLLM:Inference:ResidencyProvider=Auto` is the shipping default. PalLLM
  resolves provider behavior from `BaseUrl` and only emits a residency hint
  when the host matches a known compatible runtime.
- `PalLLM:Inference:ResidencyTtlSeconds=1800` is the shipping default. Set it
  to `0` to disable residency hints without disabling warmup itself.
- Ollama-compatible hosts use the native `/api/chat` preload path for warmup
  and map the TTL to `keep_alive`.
- LM Studio-compatible hosts keep using chat-completions and map the TTL to
  the documented `ttl` request field. `pal connect lmstudio` writes
  `ResidencyProvider=LmStudio` explicitly so this stays true even if the
  operator moves the server off the default `localhost:1234` port.
- `POST /api/inference/warmup` and `/api/health` expose the resolved
  `ResidencyProvider`, `ResidencyTtlSeconds`, `WarmupTransport`, and whether
  the last warmup actually used a residency hint.
- `scripts/play-palllm.ps1` now issues a best-effort `POST /api/inference/warmup`
  after the sidecar is healthy and doctor has passed, so the one-click player
  path hides cold-start latency when live inference is enabled without turning
  warmup into a launch blocker.
- The same launcher writes `Runtime/LaunchEvidence/latest-player-launch.json`
  plus `.md`, capturing the final install target, warmup result, and latest
  release-readiness / bridge-proof posture for that launch.

### Sampling tuning

The shipped sampling defaults (`temperature=0.7`, `top_p=0.8`,
`presence_penalty=1.5`) are tuned for mid-size instruction-tuned models.
Other model families may want different values - tune
`PalLLM:Inference:Temperature` / `TopP` / `PresencePenalty` per endpoint.

---

## Turning on vision safely

1. Run any HTTP server that implements the JSON chat-completions schema
   with `image_url` content parts and supports a multimodal model.
2. Set `PalLLM:Vision:Enabled=true` and point `BaseUrl`/`Model` at your
   server.
3. Test:
   ```bash
   curl -X POST http://localhost:5088/api/vision/describe \
     -H "Content-Type: application/json" \
     -d '{"ImageBase64":"'$(base64 -w0 <your_screenshot>.png)'"}'
   ```
4. Optional: flip `PalLLM:Vision:EnableScreenshotWatcher=true` to have
   the sidecar consume screenshots from `Bridge/Screenshots` on a timer
   and merge the extracted world state into the live snapshot.

### Cost control

- `MaxScreenshotsPerPoll=2` (default) caps backlog burn through the
  vision model.
- `MaxImageBytes=6 MB` (default) refuses oversized images at the client
  layer before they reach the model.
- `MaxResponseBytes=65536` (default) caps successful upstream vision JSON
  bodies before they are fully parsed.
- `PendingScreenshotMax*` retention prevents a disabled watcher from
  drowning the disk.

---

## Turning on TTS

1. Run any HTTP server that returns `audio/*` bytes. The default
   `PalLLM:Tts:RequestFormat=simple` posts
   `POST { "text", "voice" }`; set `RequestFormat=openai_speech` for
   OpenAI-compatible `/v1/audio/speech` endpoints that expect
   `input`, `voice`, and `response_format`.
2. Set `PalLLM:Tts:Enabled=true` and point `BaseUrl` at the server. For
   strict OpenAI-compatible servers, also set `Tts:Model`; local vLLM-Omni
   speech servers can infer the model from the loaded process.
   For `openai_speech`, set `Tts:ResponseFormat` to the container you intend to
   prove (`wav`, `mp3`, `opus`, `aac`, `flac`, or `pcm`). If the speech server
   omits `Content-Type` or sends generic `application/octet-stream`, PalLLM uses
   that requested format to choose the MIME type, file extension, and playback
   hint.
3. A chat reply now attaches a `Speech` artifact (file under
   `runtime-root/TTS`) when text is produced.
4. Retention is capped by `Tts:MaxStoredFiles=128` and
   `MaxStoredAgeHours=24`, and successful upstream audio bodies are capped by
   `Tts:MaxResponseBytes=16777216` (16 MB). Long runs stay bounded.
5. The UE4SS layer attempts best-effort local playback using the
   runtime-authored `PlaybackHint`. WAV stays on `sound_player`; `mp3`, `m4a`,
   `aac`, `wma`, `ogg`, `opus`, and `flac` use the local media-player helper;
   raw PCM remains proof-only with `PlaybackHint=raw_pcm`. For `.pcm`,
   `audio/pcm`, or `audio/l16`, including parameterized MIME values such as
   `audio/L16; rate=24000; channels=1`, the bridge emits a `speech_playback`
   receipt with mode `raw_pcm`, zero launch attempts, optional content-free
   raw timing metadata, stable `FailureCode=raw_pcm_native_mixer_required`,
   and the native-mixer blocker reason instead of launching a desktop helper.
   `/api/bridge/proof` also raises the dedicated `native_audio_mixer` lane for
   that receipt, so raw PCM cannot be treated as player-facing audio until a
   started native mixer receipt exists. If `config/native-hud.lua` sets
   `native_audio_mixer_enabled=true`, the bridge calls
   `native_audio_mixer_callback_name` and reports
   `native_audio_mixer_unavailable`, `native_audio_mixer_failed`, or
   `native_audio_mixer_rejected` when the callback is missing, throws, or
   declines the buffer; only callback acceptance reports
   `PlaybackMode=native_mixer` with `Started=true`.
   Incomplete raw sample frames report `raw_pcm_block_alignment_invalid`. WAV receipts include
   `AudioEncoding`, `SampleFormat`, `ByteOrder`, `MixerConversionHint`,
   `SampleRateHz`, `ChannelCount`, `BitsPerSample`, and `DurationMs`, plus
   `ByteRate`, `BlockAlignBytes`, `AudioDataBytes`, `FrameCount`,
   `BlockRemainderBytes`, `ValidBitsPerSample`, `ChannelMask`,
   `MixerQuantumMs`, `MixerQuantumFrames`, `MixerQueueDepthEstimate`, and
   `MixerTailFrames`, plus `MixerBufferedMs` and `MixerTailMs` inferred from
   RIFF metadata, plus `PlaybackSequence`, `SupersededRequestId`,
   `SupersededSpeechCount`, `SupersededSpeechAgeMs`,
   `SupersededSpeechBufferedMs`, `SupersededSpeechRemainingMs`, and
   `CancellationMode` for stale-speech/barge-in proof, giving the future native
   mixer concrete format, sample-interpretation, conversion, layout,
   low-latency queue-depth, buffer-duration, prior-buffer overlap, and
   buffer-lifetime proof without storing audio
   bytes or local paths. Unsupported WAV encodings report
   `wave_encoding_unsupported`, and partial block-alignment mismatches report
   `wave_block_alignment_invalid`, before a helper launch can be counted as
   started. A native in-world audio surface is still a remaining roadmap item;
   see [`ROADMAP.md`](ROADMAP.md)
   Sec. "Phase 4: Native player delivery and voice" and
   [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md) Sec. "Queue 4: Native
   speech loop integration".

---

## Enabling the action executor

Action intents are purely advisory by default. To let the game side act on them:

1. Set `PalLLM:Automation:Enabled=true`.
2. Populate the allowlist. **Start small** - e.g. `["waypoint_suggest"]`. Empty means no intent is ever emitted regardless of the enabled flag.
3. On the UE4SS side, `mod/ue4ss/Mods/PalLLM/Scripts/main.lua` has its own independent allowlist and dry-run flag. Both must agree before an action actually runs.
4. Watch `palllm_bridge_event_total` rise when actions fire; per-action telemetry flows back into the bridge inbox.

### Known safe action types

- `waypoint_suggest` - cosmetic; places a marker the player can ignore.
- `recall_pals` - re-issues a follow command; reversible with one click.
- `request_craft_queue` - appends to an existing queue; does not consume materials.

Anything not on this list should stay out of both allowlists until its reversibility is proven.

---

## Enabling the native HUD bind

Requires a running Palworld client with PalLLM installed. This is the
single biggest remaining player-facing item on the roadmap.

1. Run the mod in-game and let `UserWidget` lifecycles flow. The sidecar
   dashboard at `/` surfaces ranked `ui_probe` candidates; or query directly:
   ```powershell
   curl http://localhost:5088/api/bridge/ui-probe
   ```
2. Prefer the top recommendation from `GET /api/bridge/proof` or
   `scripts/doctor.ps1`. The shortlist boosts HUD-shaped keywords
   (`hud`, `subtitle`, `overlay`, `message`) and penalizes menu-shaped
   widgets (`inventory`, `map`, `pause`, ...), so the first suggested target
   should be the default operator choice unless you have a better in-game
   reason to override it.
3. Export the recommendation into the installed mod override file:
   ```powershell
   powershell -File scripts\apply-hud-bind-recommendation.ps1
   ```
   The script writes `config\native-hud.lua` beside the installed PalLLM mod
   when it can detect a Palworld install, or falls back to the repo source mod.
   Use `-WriteToSourceMod` to force the repo source tree, or
   `-KeepNativeHudDisabled` to sync targets without flipping the render flag on.
4. If you need to edit by hand, create `config\native-hud.lua` beside the
   installed mod with:
   ```lua
   return {
       native_hud_render_enabled = true,
       native_hud_widget_targets = {
           "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
       },
       native_audio_mixer_enabled = false,
       native_audio_mixer_callback_name = "PalLLM_NativeAudioMixer_PlayRawPcm",
   }
   ```
   The legacy inline equivalent in `main.lua` was:
   ```lua
   local native_hud_render_enabled = true
   local native_hud_widget_targets = {
       "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",   -- example - prefer the proof recommendation
   }
   ```
   On the next launch, `bridge_boot` will report the configured target names,
   config source, and config path back to the sidecar so `/api/health`,
   `/api/bridge/proof`, and `scripts/doctor.ps1` can tell whether the live
   config matches the current top-ranked `ui_probe` candidate.
5. Trigger a reply. Successful binds print `[PalLLM][HudRender] ...` with
   the chosen `target#field` (e.g. `WBP_HudRoot_C#MessageText`).
6. Capture a real proof artifact once the bind is live:
   ```powershell
   powershell -File scripts\run-native-proof.ps1
   ```
   Use `-ApplyHudRecommendation` if you want the helper to write the current
   ranked target first, then keep polling until `/api/bridge/proof` reaches
   `delivery_proven`. When watching a local sidecar, the helper now fails fast
   and writes a blocked native-proof artifact if the Palworld process is not
   running; pass `-SkipPalworldProcessCheck` only when the proof watcher is
   intentionally observing a remote sidecar or a non-standard launcher. The
   artifact includes watcher start/finish time, timeout and poll settings, poll
   count, completion reason, stable `DiagnosisCode` / `DiagnosisSummary`, a
   `DiagnosisAction` / `DiagnosisCommand` remediation pair, and a bounded
   status-transition trail so support can see how the proof progressed before
   it stopped without parsing console prose.
   If TTS is enabled and a chat reply carries a `Speech` artifact, the Lua
   bridge also writes a content-free `speech_playback` receipt after the local
   helper attempt. The bridge first verifies the artifact is readable and
   non-empty, rejects invalid WAV headers or unsupported WAV encodings before
   helper launch, and records only the artifact byte count, WAV encoding/format
   and layout metadata when available, launch attempt count, helper-launch
   elapsed milliseconds, speech supersession/cancellation-mode metadata,
   mode/hint/MIME/extension, stable failure code, and a short reason.
   `/api/bridge/proof` keeps speech playback and `native_audio_mixer` as
   separate proof lanes so a missing or skipped audio helper cannot be mistaken
   for native speech proof. Keep `native_audio_mixer_enabled=false` until a
   UE4SS/native callback can return true only after the Palworld mixer has
   accepted the raw PCM buffer.
7. Package the current proof set for release validation:
   ```powershell
   powershell -File scripts\export-release-proof-bundle.ps1
   ```
   That archives the current `/api/release/readiness`, `/api/bridge/proof`,
   smoke, native-proof, and HUD-config evidence under
   `Runtime/ReleaseEvidence/latest-proof-bundle.json` plus `.zip`.
8. Verify the concrete candidate package before you hand it to a clean machine:
   ```powershell
   powershell -File scripts\verify-release-package.ps1
   ```
   That validates the current candidate zip against its embedded
   `RELEASE_PACKAGE_MANIFEST.json`, scans shipped text for private
   sibling-project terms, endorsement/approval claims, unrelated franchise
   references, broad platform-scope drift, and root player-copy brand drift,
   and writes `Runtime/ReleaseEvidence/latest-package-verification.json`.
9. Failed binds fall through to the existing `ClientMessage` /
   `PrintString` path, so replies never disappear on a bad guess.
10. Optional speaker / path-badge / HUD-accent widget fields are populated
   automatically from the live presentation plan when the widget exposes
   any of: `SpeakerText`, `NameText`, `Text_Speaker`, `BadgeText`,
   `PathBadgeText`, `AccentText`, `HudAccentText`, `SubtitleStyleText`.

---

## Enabling the production sampler

The runtime understands `production` bridge events but Palworld's crafting
loop does not expose a clean single-shot broadcast. The Lua layer ships a
bounded polling sampler that emits `production` events when a base's
`(item, status)` tuple changes.

1. Set `production_sampler_enabled = true` in `main.lua`.
2. The sampler polls `PalBaseCampManager.BaseCamps` every 12 seconds,
   bounded to 3 bases per poll.
3. All engine lookups are `pcall`-guarded; if a Palworld hook renames, the
   sampler degrades to a no-op instead of throwing.

---

## Container deployment

The repo ships a production-shaped `Dockerfile` at its root. Use when
hosting the sidecar on a Linux server (for a larger GPU, shared
household access, or simply running the brain on a different machine
from the game client).

Prerequisites:
- Docker 24+ with BuildKit enabled (default in recent versions).
- Just the `PalLLM/` repo - since the 2026-04-22 decoupling, PalLLM's
  portable adapter surface lives inside `src/PalLLM.Domain/Portable/`
  (see [`CORE_LIBRARY.md`](CORE_LIBRARY.md)), so no sibling checkout
  is required.

Build (from the repo root - Dockerfile context expects `PalLLM/` only):

```bash
docker build -f PalLLM/Dockerfile -t palllm:latest .
```

Run:

```bash
docker run --rm -p 5088:5088 \
  -v palllm-runtime:/var/palllm \
  palllm:latest
```

Image characteristics:
- **Base**: `mcr.microsoft.com/dotnet/aspnet:10.0` (current LTS).
- **Multi-stage build**: SDK used only for the build stage; runtime
  stage is the smaller `aspnet:10.0` image (~220 MB typical).
- **Non-root**: runs as the unprivileged `$APP_UID` user shipped in
  the base image, per Microsoft's [container hardening
  guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/docker/building-net-docker-images?view=aspnetcore-10.0).
- **Volume**: `/var/palllm` holds session state, outbox envelopes,
  TTS artifacts, bridge inbox, and diagnostics. Mount a named volume
  (shown above) or a host path.
- **Port**: container listens on `:5088`; map it with `-p`.
- **Config**: any `PalLLM:*` option can be overridden via the
  `PalLLM__*` environment variable convention (ASP.NET Core config
  double-underscore path separator). Example:
  `-e PalLLM__Inference__Enabled=true -e PalLLM__Inference__BaseUrl=http://host.docker.internal:11434/v1/`.

Remote Lua bridge: the UE4SS Lua bridge on the Windows game machine
writes to `%LOCALAPPDATA%\Pal\Saved\PalLLM\Bridge\*`. To let a
containerised sidecar on another machine read/write that same bridge,
either share the `Bridge/` folder over SMB/NFS and mount it at
`/var/palllm/Bridge` in the container, or switch to an HTTP
bridge (not yet shipped). Local-host deployments don't need this.

---

## Enabling API-key authentication

PalLLM defaults to **no authentication** because the sidecar is
localhost-only in the shipped posture: the port is only reachable from
the machine owner, and an auth layer would add friction for the
loopback-only case. When you expose the sidecar beyond localhost (the
Dockerfile deployment, a home LAN server, anything behind a reverse
proxy) flip bearer-token auth on.

**Basic posture - protect `/api/*` only:**

```jsonc
// appsettings.json
{
  "PalLLM": {
    "Auth": {
      "ApiKey": "<any non-empty string; treat as a credential>"
    }
  }
}
```

Or via environment variable (works unchanged inside the Docker
container):

```bash
docker run --rm -p 5088:5088 \
  -e PalLLM__Auth__ApiKey="$(openssl rand -hex 24)" \
  -v palllm-runtime:/var/palllm \
  palllm:latest
```

Every request under `/api/*` now requires
`Authorization: Bearer <key>`. Unauthenticated requests get a **401**
with a `WWW-Authenticate: Bearer` header. `/metrics`,
`/health/live`, `/health/ready`, `/openapi/v1.json`, `/openapi/v1.yaml`,
and the static dashboard root stay open so monitoring, container
orchestrators, SDK generators, and the dashboard UI don't need a
credential.

**Paranoid posture - lock metrics + health too:**

```jsonc
{
  "PalLLM": {
    "Auth": {
      "ApiKey": "<key>",
      "ProtectMetrics": true,
      "ProtectHealth": true
    }
  }
}
```

Only `/openapi/v1.json`, `/openapi/v1.yaml`, and the static dashboard remain
open.

**Constant-time comparison**: the middleware compares the presented key
against the configured key with
`CryptographicOperations.FixedTimeEquals` so the response time does
not leak prefix information to an attacker.

**Key rotation**: change the `ApiKey` value and restart the sidecar.
Clients receive 401 until they present the new key; there is no shared
state to flush.

**When to combine with other measures**: the `ApiKey` is bearer-only
and sent in plaintext over HTTP. For anything beyond a trusted LAN,
put the sidecar behind a TLS-terminating reverse proxy (Caddy, nginx,
Traefik) and rate-limit at the proxy layer. See [`TLS.md`](TLS.md) for
the full deployment walk-through including a ready-to-adapt
[`examples/Caddyfile`](examples/Caddyfile). PalLLM's per-character
rate limiter
(`PalLLM:Fallback:MaxCharacterRequestsPerMinute`) is not a substitute
for an external rate limit against anonymous attackers.

---

## Enabling distributed tracing

PalLLM ships with optional OpenTelemetry distributed observability.
Traces, GenAI client metrics, PalLLM's recent-window readiness gauges, and
OTLP log export are all off by default
and activate only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set in the
process environment, so the localhost default deployment carries zero OTel overhead.

The same opt-in switch now carries three signal types together:
distributed traces, OTLP log records, explicit GenAI client histograms, and
PalLLM-specific recent-window readiness gauges emitted by the live inference
and vision lanes.

**When it's useful:**

- Multi-hop debugging (chat request -> inference call -> vision call) -
  tracing shows exact latency of each hop as a flame graph.
- Fallback vs inference accounting - the `pal.chat` span carries a
  `pal.response_path` + `pal.used_fallback` tag, so a tracing backend
  can answer "what percentage of chats hit fallback this hour?".
- Per-strategy latency - the `pal.fallback_strategy` tag lets you
  compare, for example, `stealth-withdraw` vs `base-network-audit`
  response times directly in the backend.
- Request correlation across the sidecar, inference endpoint, and any
  other OTel-instrumented peer in your stack.

**Turning it on:**

| Variable | Meaning | Example |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | **Required.** OTLP receiver URL. Setting this is what activates traces, GenAI client metrics, and logs. | `http://localhost:4317` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | Optional. `grpc` (default, matches port 4317) or `http/protobuf` (port 4318). | `grpc` |
| `OTEL_SERVICE_NAME` | Optional. Service name reported in OTLP signals. Defaults to `pal-llm-sidecar`. | `pal-llm-sidecar-dev` |
| `OTEL_RESOURCE_ATTRIBUTES` | Optional. Extra resource tags (host, environment). | `deployment.environment=prod,host.name=palbox` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Optional. Auth headers for managed OTel backends (Honeycomb, Datadog, Grafana Cloud). | `x-honeycomb-team=<key>` |

**Local-stack recipe (Docker Compose with Jaeger all-in-one):**

```bash
docker run -d --name jaeger \
  -e COLLECTOR_OTLP_ENABLED=true \
  -p 4317:4317 -p 16686:16686 \
  jaegertracing/all-in-one:latest

OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317 \
  dotnet run --project src\PalLLM.Sidecar\PalLLM.Sidecar.csproj
```

Then open `http://localhost:16686` and select `pal-llm-sidecar` from
the service dropdown. Send one chat request to `/api/chat` and the
trace tree shows: `HTTP POST /api/chat` (incoming, tagged by
AspNetCoreInstrumentation) -> `pal.chat` (runtime orchestration span)
-> optional outgoing `HTTP POST /v1/chat/completions` (if inference is
on, tagged by HttpClientInstrumentation).

**What spans are emitted:**

| Span name | Source | Tags |
|---|---|---|
| `HTTP {METHOD} {route}` | `OpenTelemetry.Instrumentation.AspNetCore` | standard `http.*` semconv tags |
| `HTTP {METHOD}` (outgoing) | `OpenTelemetry.Instrumentation.Http` | standard `http.*` tags for the inference / vision / TTS URL |
| `pal.chat` | `PalLLM.Runtime` | `pal.request_id`, `pal.character_id`, `pal.task_tag`, `pal.response_path`, `pal.used_fallback`, `pal.fallback_strategy`, `pal.inference_attempted` |
| `chat {model}` / `generate_content {model}` | `PalLLM.Runtime` | `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.output.type`, token-usage tags, low-cardinality `error.type` on failures |

**What's filtered out:**

- `/health/*` and `/metrics` requests. These get scraped every few
  seconds and would drown the interesting chat/bridge spans. If you
  need health/metrics traffic in traces, remove the `options.Filter`
  lambda in `src/PalLLM.Sidecar/Program.cs`.

### What about metrics?

The same OTLP switch now exports PalLLM's explicit GenAI client
histograms for the real model lanes:

| Metric | Meaning |
|---|---|
| `gen_ai.client.operation.duration` | Duration histogram for each live inference or vision call. Tagged by operation (`chat` vs `generate_content`), provider, request model, response model when available, server address/port, and low-cardinality `error.type` on failures. |
| `gen_ai.client.token.usage` | Input/output token histogram for calls whose upstream returned usage data. Tagged with the same request/response model identity plus `gen_ai.token.type=input|output`. |

It also exports PalLLM's own bounded readiness gauges for the active recent
window:

| Metric | Meaning |
|---|---|
| `palllm.inference.recent_window.status` | One-hot overall recent-window readiness state with `status` and `budget` attributes. |
| `palllm.inference.recent_window.sample_count` | Aggregate recent-window live-operation sample count. |
| `palllm.inference.recent_window.success_ratio` | Aggregate recent-window success ratio gauge. |
| `palllm.inference.recent_window.target_hit_ratio` | Aggregate recent-window target-budget hit ratio gauge. |
| `palllm.inference.recent_window.ceiling_hit_ratio` | Aggregate recent-window ceiling-budget hit ratio gauge. |
| `palllm.inference.lane.status` | One-hot per-lane readiness state with `operation`, `provider`, `model`, `budget`, and `status` attributes. |
| `palllm.inference.lane.sample_count` | Per-lane recent-window sample-count gauge. |

Prometheus at `/metrics` remains the local-first scrape surface for
runtime health, fallback strategy mix, and dashboard-oriented counters.
The OTLP metrics are the cross-service/operator view of the live model
lanes. As of the `2026-04-22` audit, the OpenTelemetry GenAI semantic
conventions these histograms follow are still marked `Development`, so a
future OTel upgrade may evolve names or attributes.

### What about logs?

The same env var (`OTEL_EXPORTER_OTLP_ENDPOINT`) also activates
OpenTelemetry log export. Every ILogger record the sidecar writes is
shipped as an OTLP log record in parallel with the existing console
sink (console output keeps working unchanged), and each record carries
the active Activity's `trace_id` + `span_id` automatically. In the
backend, opening a `pal.chat` span reveals the exact ILogger messages
emitted during that turn - no manual correlation needed.

| Log record attribute | Source |
|---|---|
| `TraceId`, `SpanId` | Automatically propagated from the active Activity when the log call happens inside a span. |
| `EventId`, `Category` | Standard .NET `ILogger` fields. |
| `Body`, `Attributes` | The log message + any structured arguments. |
| `Severity` | Mapped from `LogLevel` (`Trace` -> `TRACE1`, `Information` -> `INFO`, `Error` -> `ERROR`, etc.). |

Turning logs off means the same thing as turning tracing and OTLP GenAI
metrics off: unset
`OTEL_EXPORTER_OTLP_ENDPOINT` and restart. When it is unset, the
OpenTelemetry logger provider is never attached to the `LoggerFactory`,
so the cost of an ILogger call is exactly what it was before OTel was
introduced - the Domain project never took a dependency on any OTel
package in the first place.

**Turning it off:**

Unset `OTEL_EXPORTER_OTLP_ENDPOINT` and restart. No listener is
registered, `ActivitySource.StartActivity` calls return null, the
instrumentation packages never install their DiagnosticListener
adapters, the OpenTelemetry meter pipeline is never created, and the
OpenTelemetry logger provider is never chained onto the LoggerFactory
and there is zero OTLP per-request / per-log-record overhead. A regression test
(`ChatAsync_WhenNoActivityListenerRegistered_StartActivityIsCheapNoOp`)
locks in this promise for the tracing path.

**Verification:**

- With the env var set, the startup log shows an OpenTelemetry
  initialization line.
- Trigger one `/api/chat` call. A span with name `pal.chat` should
  appear in your backend within a second or two (OTLP batches on a
  short timer).
- Trigger one live inference or vision call and confirm the backend
  receives `gen_ai.client.operation.duration` samples, plus
  `gen_ai.client.token.usage` when the upstream reports usage counts.
- Confirm the backend also receives `palllm.inference.recent_window.status`
  and, after at least one live call lands, `palllm.inference.lane.status`
  measurements for the active lane.
- If no spans, metrics, or logs appear: check `OTEL_EXPORTER_OTLP_ENDPOINT`
  matches the receiver port (`4317` for gRPC, `4318` for HTTP), and
  that the receiver is actually listening (`curl http://localhost:4317`
  or `telnet localhost 4317`).

---

## Configuring tiered model loading

PalLLM supports a **tiered local-model cascade** so you can ship a
working sidecar the moment a small-and-fast model is available, then
automatically graduate to a larger, higher-quality model when the heavy
one finishes downloading / warming in your local inference endpoint
(llama.cpp server (default), vLLM, TensorRT-LLM, LM Studio,
OpenVINO Model Server, Foundry Local, `transformers serve`, vLLM-Omni
for multimodal — eight engines total; Pass 339 removed Ollama support).
Zero downtime, zero manual config edit.

For a raw llama.cpp GGUF lane:

```powershell
pwsh ./pal.ps1 connect llamacpp -ModelPath C:\Models\qwen.gguf -Model pal-llamacpp -WriteConfig
```

Keep the first profile loopback-only, metrics-enabled, and conservative:
`-np 1`, measured context, `--cache-prompt`, `--cache-reuse`, and no
speculation or quantized KV until replay proof says the player path is stable.
Before promotion, prove `/health`, `/v1/models`, `/metrics`, p50/p95 latency,
strict JSON/tool-call parse success, and deterministic fallback activation.
Treat `-ctk/-ctv`, `--sleep-idle-seconds`, `--spec-type`, and `--mmproj`
vision as separate proof lanes.

For a low-friction LM Studio desktop lane:

```powershell
lms server start --port 1234
lms load <model-id> --gpu auto --context-length 8192 --identifier <stable-pal-model-id> --ttl 1800
pwsh ./pal.ps1 connect lmstudio -Model <stable-pal-model-id> -WriteConfig
```

Promote it only after `/v1/models`, structured JSON, tool-call, `ttl`,
auto-evict, p50/p95 latency, and deterministic fallback behavior have all been
captured on PalLLM replay traffic.

**The shipping default** in `appsettings.json` configures two tiers:

| Tier id | Model tag (OpenAI-compat, llama-server passthrough) | Priority | Why |
|---|---|---:|---|
| `small` | `gemma-4-E4B-it-UD-Q4_K_XL` | 1 | 5 GB unsloth UD-Q4_K_XL Gemma 4 E4B. Loads in seconds; usable from the first minute of the session. Lives at `D:\Models\Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf` per `LOCAL_MODELS_INVENTORY.md`. |
| `large` | `Qwen3.6-35B-A3B-UD-Q8_K_XL` | 10 | 39 GB unsloth UD-Q8_K_XL Qwen 3.6-A3B MoE (3 B active, MTP-capable). Orchestrator graduates automatically once llama-server reports it loaded. Lives at `D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf`. |

**How it works:**

1. On startup, the orchestrator seeds the active tier with the
   lowest-priority entry in list order (typically `small`) so the
   very first chat request works before any probe completes.
2. `InferenceWarmupWorker` primes the current active lane once on startup.
   If `PalLLM:Inference:WarmupIntervalSeconds > 0`, it also performs a
   periodic keep-alive warmup so idle local runtimes keep the current lane
   resident. A keep-alive tick self-suppresses when a recent successful live
   chat already touched the same active model inside the keepalive window, so
   active sessions do not pay duplicate warmup POSTs. When the lane is backed
   by LM Studio, live chat-completions requests can carry `ttl`; llama.cpp
   keeps the loaded model resident for the lifetime of the server process so
   no per-request keep-alive is needed.
3. `ModelTierUpgradeWorker` (IHostedService) immediately probes the
   inference endpoint's configured `models` catalog (`/v1/models` for most
   servers including llama-server, OpenVINO `/v3/models` when `BaseUrl` ends
   in `/v3/`), then Foundry Local `/openai/models`. The probe code also
   recognises Ollama's `/api/tags` shape for back-compat with any operator
   still running Ollama out-of-band, but PalLLM no longer ships an Ollama
   connector. It atomically swaps the active tier if the probe reveals a
   higher-priority option.
4. When the active tier changes, the sidecar triggers another bounded warmup
   for the new lane so the first player turn after graduation is less likely
   to pay the full model-load cost.
5. The worker re-probes every `PalLLM:Inference:TierProbeIntervalSeconds`
   (default 30s) so the moment a large pull completes, the next
   request uses it.
6. Transient probe failures (network blip, endpoint restart) **keep
   the current tier** - no thrashing down to `small` on every flap.
7. Every tier transition emits a `pal.model_tier.transition` span on
   the `PalLLM.Runtime` ActivitySource, so if OpenTelemetry is on
   (`OTEL_EXPORTER_OTLP_ENDPOINT` set) you see graduations in Tempo /
   Jaeger with the previous tier id, new tier id, model tag, and the
   available-model count at that moment.

**Recommended first-boot flow** (llama.cpp example with the operator's
curated `D:\Models` library — see `LOCAL_MODELS_INVENTORY.md`):

```powershell
# Terminal 1: start llama-server pointing at the small (fast-start) GGUF.
# Loads in seconds; usable from the first chat request.
llama-server -m D:\Models\Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf `
    --host 127.0.0.1 --port 8080 -c 8192 -ngl 99 `
    --flash-attn on --metrics --no-webui --alias gemma-4-E4B-it-UD-Q4_K_XL

# Terminal 2: start the PalLLM sidecar. It picks up the small tier
# immediately and starts serving chat replies.
dotnet run --project src\PalLLM.Sidecar\PalLLM.Sidecar.csproj
# or:
sidecar\publish\PalLLM.Sidecar.exe   # self-contained release

# When ready for the quality tier (large MoE), swap llama-server to:
llama-server -m D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf `
    --mmproj D:\Models\mmproj\mmproj-F16.gguf `
    --host 127.0.0.1 --port 8080 -c 16384 -ngl 99 `
    --flash-attn on --spec-type ngram-mod `
    --metrics --no-webui --alias Qwen3.6-35B-A3B-UD-Q8_K_XL

# Within 30 seconds of llama-server announcing the new model, the
# sidecar logs:
#   Model tier graduated small -> large (Qwen3.6-35B-A3B-UD-Q8_K_XL).
# and the next chat request automatically uses the large tier with
# native MTP speculative decoding (`--spec-type ngram-mod`).
```

> Pass 339 dropped Ollama support. If you previously used
> `ollama pull` + `ollama serve`, switch to the llama-server flow above.
> Your existing GGUF library is reusable — point `-m` at the file path.

**Customising tiers:**

Edit `appsettings.json` under `PalLLM:Inference:ModelTiers`. Each
tier is `{ Id, Model, Priority, Description? }`. Higher priority
wins; ties are broken by list order. Non-contiguous priorities
(1, 10, 100) leave room to insert tiers between existing ones
without renumbering.

```json
"ModelTiers": [
  { "Id": "tiny",   "Model": "qwen3.6-mini-4B-A1B-UD-Q4_K_XL",   "Priority": 1 },
  { "Id": "small",  "Model": "gemma-4-E4B-it-UD-Q4_K_XL",        "Priority": 5 },
  { "Id": "medium", "Model": "Qwen3.6-27B-UD-Q8_K_XL",           "Priority": 20 },
  { "Id": "large",  "Model": "Qwen3.6-35B-A3B-UD-Q8_K_XL",       "Priority": 50 }
]
```

**Disabling tier orchestration:**

Set `ModelTiers` to `[]`. The sidecar falls back to using
`PalLLM:Inference:Model` verbatim for every request - fully backwards
compatible with pre-tier configs.

**Health surfacing:**

- `/api/health` now exposes the active model, active tier id,
  last-seen available models, and `InferenceWarmup` status/timestamps.
- `POST /api/inference/warmup` manually primes the currently active lane and
  returns the updated warmup snapshot.
- the warmup snapshot also includes the resolved residency provider/TTL, the
  transport used for the last warmup (`chat_completions` vs
  `ollama_native_chat_preload`), and whether a provider-specific residency
  hint was emitted.
- Active tier id is also available in traces (every `pal.chat` span
  backed by inference carries the model tag used).
- The upgrade worker logs each graduation at `Information` severity:
  `Model tier graduated <prev> -> <new> (<model>).`
- The MCP `pal_active_model_tier` tool and `palllm://model/tier/active`
  resource also includes the warmup snapshot for remote operators.

---

## Fallback coverage matrix

PalLLM is designed so every player-facing response path has a
deterministic, high-quality fallback. If inference is off, a model
is unreachable, a vision endpoint is down, or TTS isn't configured,
the player still gets a useful response - not an error dialog.

| Surface | Primary path | Fallback when primary fails | Diversity |
|---|---|---|---|
| Chat reply | Live inference via configured tier | Deterministic director with 19 hand-authored fallback strategies (stealth, crafting, combat, base-network, production, travel, etc.) | 19 distinct strategies mapped to intent signals - not a single canned string |
| Chat reply when bypassed | N/A | `fallback_policy_bypass` path uses the director directly | 19 strategies + context-aware selection |
| Chat reply on model failure | N/A | `fallback_inference_failed` path - same 19 strategies with a sanitized failure class preserved in `StatusMessage` (`HTTP 5xx`, timed out, unreachable, malformed JSON, etc.) | Full 19-strategy diversity |
| Chat visual augmentation | Live vision model via `UseForChatAugmentation` | `SnapshotVisionFallback.Compose(snapshot)` - deterministic scene description (time, biome, weather, base vs wild, nearby pals, hostiles, objective) spliced into the system prompt instead of dropping context | One generator x ~dozens of distinct snapshot states = diverse per-situation output |
| Vision describe (standalone `/api/vision/describe`) | HTTP call to configured multimodal endpoint | `VisionResult.Failed(reason)` - deliberately strict so external tooling calling this endpoint gets ground-truth vision output, never a synthesised stand-in | Structured failure per cause |
| Vision world-state | Structured extraction via `response_format: json_schema` | Snapshot is untouched on failure; caller sees `Success=false` | Structured no-op |
| TTS synthesis | HTTP call to configured TTS endpoint | `TtsResult.Disabled` (feature off) or `TtsResult.Failed(reason)` (endpoint error). Public status strings are sanitized and avoid raw upstream body or exception text. `DisabledTtsClient` is injected automatically when `Tts.Enabled=false` | Graceful no-audio + status message |
| Action intent | Deterministic `ActionIntentPlanner` based on allowlist + world snapshot | No action emitted (null) - chat response still works | No-op when allowlist is empty |
| Memory recall | Semantic recall over `ConversationMemoryStore` | Empty list returned when store is empty - downstream prompt builder gracefully handles | Inherent no-op |
| Presentation plan | Always synthesised - chat response never ships without one | N/A - plan is mandatory | Per-strategy mapping for all 19 fallback strategies |
| Pack lore | Authored narrative pack entries | Built-in `PalTextCatalog` defaults when no pack is loaded | Built-in default persona |
| Session persistence | `session.json` autosave via `SessionAutosaveWorker` | In-memory state preserved on disk write failure; save/load API responses use stable status categories, and bounded reload falls back to `session.json.bak` when the primary file is malformed, oversized, or unreadable | Continues running |
| Relationships | `RelationshipTracker` with deterministic seeds | Derived from memory on a fresh boot | Deterministic reconstruction |
| Model tier selection | Highest-priority available tier | Seed tier (lowest priority) works immediately; if no tier is available the probe result is empty and `Inference.Model` is used verbatim | Tier cascade + static model floor |

**What's deliberately not a diverse fallback:**

- **Vision describe (`/api/vision/describe` endpoint)** stays strict-
  failure on purpose. External tools calling that endpoint expect
  ground-truth vision model output; a snapshot-derived stand-in would
  mask a real outage. The snapshot fallback is wired into the *chat
  visual augmentation* path only, not the standalone describe endpoint.
- **TTS** inherently needs a model - there is no deterministic way to
  synthesise audio from game state. `TtsResult.Disabled`/`Failed`
  returns a structured no-audio + sanitized status message.
- **Reflection consolidation** (`PalLLM:Fallback:EnableReflection=true`)
  stays deterministic by design. When enabled, PalLLM consolidates
  salient recent memories locally and writes a `reflection`-tagged
  entry without making a live model call; when disabled, reflection
  quietly skips.

---

## Exposing PalLLM via MCP

PalLLM implements the [Model Context Protocol](https://modelcontextprotocol.io/)
so Claude Desktop, Visual Studio Code, Cursor, ChatGPT, and any other
MCP-aware agent can discover and use PalLLM's runtime directly -
without custom integration code or bespoke plugins.

**Endpoint**: `http://<host>:<port>/mcp` (Streamable HTTP transport,
protocol version `2025-06-18`). Wired up automatically - no opt-in
flag required, the server is always available alongside the REST
surface.

**Browser-origin safety**: desktop MCP hosts typically do not send an
`Origin` header and continue to work unchanged. If a browser or webview host
does send `Origin`, PalLLM only accepts loopback origins by default. To allow a
non-loopback browser origin, add the exact `http://` or `https://` origin to
`PalLLM:Auth:McpAllowedOrigins[]`; otherwise `/mcp` returns `403 Forbidden`
with `ProblemDetails`. This follows the current Streamable HTTP MCP guidance
for localhost-bound servers.

**What MCP clients see - all three MCP primitives:**

*Tools (model-controlled actions):*

| Tool | Purpose |
|---|---|
| `pal_world_snapshot` | Full world snapshot JSON |
| `pal_scene_description` | Terse deterministic scene summary |
| `pal_chat` | Send a message to a companion, get reply |
| `pal_recall_memory` | Semantic memory recall |
| `pal_list_characters` | All known Pal companions |
| `pal_list_features` | Feature catalog |
| `pal_list_recent_bridge_events` | Recent Lua bridge events |
| `pal_active_model_tier` | Current active tier, last probe, and warmup state |

*Resources (application-controlled context data - MCP hosts surface these as draggable context cards):*

| URI | Purpose |
|---|---|
| `palllm://world/snapshot` | Live world snapshot |
| `palllm://features` | Feature catalog |
| `palllm://runtime/health` | Runtime health (inference/vision/TTS/circuit/breaker/bridge status) |
| `palllm://characters` | All companions |
| `palllm://model/tier/active` | Active tier, last probe availability, and warmup state |
| `palllm://character/{characterId}` | **Templated** - per-character profile |

*Prompts (user-controlled slash-command templates - Claude Desktop surfaces these as `/` commands):*

| Prompt | Purpose |
|---|---|
| `palllm_companion_chat` | Open a companion chat with world + character context pre-injected |
| `palllm_threat_analysis` | Tactical analysis of the current situation with vitals + hostiles pre-filled |
| `palllm_base_status` | Review the known base inventory and flag what needs attention |

### PalLLM as MCP client (upstream discovery)

Beyond exposing its own tools, PalLLM can act as an MCP **client** to
other MCP servers. This turns the sidecar into a lightweight MCP hub:
one `GET /api/mcp/upstream` call reveals what every connected upstream
can do, so MCP hosts can discover tools across a fleet without
querying each server directly.

Configure upstreams in `appsettings.json`:

```json
"McpClient": {
  "DiscoveryIntervalSeconds": 300,
  "DiscoveryTimeoutSeconds": 10,
  "MaxToolsPerServer": 128,
  "MaxResourcesPerServer": 128,
  "MaxPromptsPerServer": 64,
  "MaxMetadataEntryLength": 256,
  "UpstreamServers": [
    {
      "Id": "sentry",
      "Url": "https://mcp.sentry.io/mcp",
      "BearerToken": "${SENTRY_MCP_TOKEN}",
      "Enabled": true
    },
    {
      "Id": "local-filesystem",
      "Url": "http://localhost:3001/mcp",
      "Enabled": true
    }
  ]
}
```

**Safety posture**: V1 is **discovery-only and read-only**. PalLLM
never proxies `tools/call` or `resources/read` requests to upstream
servers - it only fetches catalog metadata (`tools/list`,
`resources/list`, `prompts/list`) and caches it. Operators explicitly
opt into each upstream URL + auth; bearer tokens are never surfaced
on any API response. Future revisions may layer selective proxying
on top once a per-upstream allowlist security model is designed.

**Inspect the cached snapshot**:

```bash
curl -s http://localhost:5088/api/mcp/upstream | jq .
# or via MCP:
curl -X POST http://localhost:5088/mcp \
  -H "Content-Type: application/json" -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"pal_list_upstream_mcp","arguments":{}}}'
```

**Behaviour on upstream failure**: the probe records `Connected=false`
with a stable `ErrorCode` plus a sanitized `Error` message on that upstream's
snapshot entry and continues with the rest. One dead upstream never takes down
the pool, and the background worker re-probes on
`DiscoveryIntervalSeconds` so transient outages self-heal.

**Behaviour on oversized upstream catalogs**: discovery metadata is bounded
before caching. Per upstream, PalLLM keeps at most
`MaxToolsPerServer` tool names, `MaxResourcesPerServer` resource URIs, and
`MaxPromptsPerServer` prompt names, and trims each individual entry to
`MaxMetadataEntryLength` after whitespace/control-character normalization.
This keeps the sidecar's cache and `/api/mcp/upstream` response body stable
even if one upstream advertises an unusually large catalog.

Every tool's JSON Schema (parameter names, types, descriptions) is
auto-generated from the C# method signatures in
[`src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs`](../src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs)
- MCP client UIs render the right form fields without any manual
schema authoring.

**Connecting from Claude Desktop:**

Add an entry to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "palllm": {
      "url": "http://localhost:5088/mcp",
      "transport": "streamable-http"
    }
  }
}
```

Restart Claude Desktop; the PalLLM tools appear under the plug icon.
Typing "what's the current scene?" in Claude Desktop now calls
`pal_scene_description` transparently.

**Connecting from VS Code (with GitHub Copilot Chat MCP):**

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "palllm": {
      "type": "http",
      "url": "http://localhost:5088/mcp"
    }
  }
}
```

**Connecting from any HTTP tool (curl, integration tests):**

```bash
# 1. Initialize - negotiate protocol version
curl -X POST http://localhost:5088/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"1"}}}'

# 2. List tools - see what's available
curl -X POST http://localhost:5088/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'

# 3. Call a tool - e.g. get the scene description
curl -X POST http://localhost:5088/mcp \
  -H "Content-Type: application/json" \
  -H "Accept: application/json, text/event-stream" \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"pal_scene_description","arguments":{}}}'
```

**Authentication:**

`/mcp` is protected by the same auth middleware as `/api/*`. When
`PalLLM:Auth:ApiKey` is set, every MCP request must carry
`Authorization: Bearer <key>`. Unauthenticated requests get `401`
just like unauthenticated API requests do - MCP is not a back door. Browser
hosts that send a disallowed `Origin` get `403` even if the bearer token is
otherwise valid.

**Safety posture:**

PalLLM's MCP tools do **not** invoke guarded in-game actions. The
guarded-action executor - which drives real gameplay side effects
through the Lua bridge - stays gated behind the existing
`PalLLM:Automation:AllowedActions` allowlist. MCP clients can observe
world state, chat with companions, and update bounded local operator
surfaces such as the promotion ledger, but they cannot invoke
`waypoint_suggest`, `recall_pals`, or other guarded action types
through MCP. The two surfaces (MCP discovery and action execution)
remain deliberately separated.

**Disabling MCP:**

MCP is wired in `Program.cs` unconditionally so it's always
available. To fully disable, remove the `builder.Services.AddMcpServer()`
block and the `app.MapMcp("/mcp")` call, then rebuild. For most
deployments the `/mcp` endpoint is harmless when unused -
unauthenticated clients that never send a JSON-RPC message never
exercise the server.

---

## Troubleshooting

### Sidecar won't start

- `options validation failed` - read the startup log. `PalLlmOptionsValidator` lists every bad field with a full path (`PalLLM:Inference:Model`).
- `Unable to bind to http://localhost:5088` - something else owns the port. `dotnet run --project src\PalLLM.Sidecar\PalLLM.Sidecar.csproj --urls http://localhost:5089` to move.

### Chat replies are empty / null

- Check `InferenceEnabled` + `FallbackEnabled` in `RuntimeHealth`. If both are off, the runtime produces a null assistant message by design.

### Outbox is filling up

- Is Palworld + UE4SS running? The Lua consumer drains once per second; if it's not running, files accumulate until retention kicks in.
- `POST /api/bridge/outbox/clear` empties the outbox manually. Use it when you're iterating without the game attached.

### Session didn't persist

- `GET /api/health` - check `SessionDirty` and `SessionLastSavedAtUtc`. If dirty stays `true` after the autosave interval, the save is failing silently. Look for `Session autosave failed: ...` in logs.
- `POST /api/session/save` and `POST /api/session/reload` now keep their public `StatusMessage` values stable on local disk/JSON failures and leave `FilePath` blank on failure. The adapter log tail also keeps common bridge/outbox/screenshot file-operation warnings on stable local summaries instead of echoing raw filesystem text or local paths.
- If `session.json` is malformed, oversized for `PalLLM:Session:MaxPersistedBytes`, or unreadable, the runtime falls back to `session.json.bak` automatically on next load.

### Vision calls succeed but world-state doesn't update

- Check that the vision model actually returned JSON - `POST /api/vision/world-state` exposes the raw `Content` field. Some model checkpoints narrate instead of emitting JSON; the runtime treats that as a graceful failure rather than mutating the snapshot.
- The runtime merges conservatively: only non-empty fields overwrite snapshot state.

---

## Upgrades and schema migration

### Session file format

`session.json` carries a `SchemaVersion` integer. Current: `2`. The loader:

- Accepts `SchemaVersion <= CurrentSchemaVersion`.
- **Refuses** `SchemaVersion > CurrentSchemaVersion` explicitly - a future-schema file on disk after a downgrade is a clear signal, not something to silently mis-load.
- Rejects primary files larger than `PalLLM:Session:MaxPersistedBytes` before deserialization.
- Falls back to `session.json.bak` when the primary file is malformed JSON, oversized, or unreadable.

If you downgrade PalLLM and see `Session file schema version N is newer than supported`, either upgrade back or delete the file (you'll lose memory history but everything else is safe).

### Config

`appsettings.json` version-binds through `Microsoft.Extensions.Options` with `ValidateOnStart`. New options land with safe defaults - you don't have to update your config on upgrade unless release notes say otherwise.

### Directory layout

Runtime-owned directories (Outbox, Screenshots, Packs, TTS, and the bridge
folders) are created by `EnsureDirectories()` on startup. Missing
directories are never an error; `ModelsDir` stays a logical path and is
created lazily only by consumers that actually persist local weights.



