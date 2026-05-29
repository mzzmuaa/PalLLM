# PalLLM HTTP API Reference

Audience: anyone integrating with the PalLLM sidecar - UE4SS mod authors, external tools, test harnesses, dashboards.

Last audited: `2026-05-28`

This is a reference document in the [Diataxis](https://diataxis.fr/) sense: it's complete, accurate, and uninterpreted. For learning, see [`QUICKSTART.md`](QUICKSTART.md). For operational concerns, see [`OPERATIONS.md`](OPERATIONS.md).

## Conventions

- Base URL: `http://localhost:5088` (dev default).
- All JSON bodies use PascalCase field names (PalLLM does not apply camelCase naming).
- Responses set `Content-Type: application/json` unless noted.
- Most API, metrics, and health surfaces send `Cache-Control: no-store`.
  The static Field Console (`GET /` plus its core CSS/JS/manifest/favicon
  assets) emits weak content-hash `ETag`s plus `Last-Modified` so browsers can
  revalidate packaged-player assets cheaply without hiding changed files.
  The read-mostly inspection endpoints `GET /api/dashboard`, `GET /api/features`,
  `GET /api/describe`, `GET /api/bridge/proof`,
  `GET /api/inference/performance`, `GET /api/release/readiness`, and
  `GET /api/mcp/upstream` are the deliberate exceptions: they emit `ETag`
  plus private cache headers and can return `304 Not Modified` on a matching
  `If-None-Match`. The self-description surface uses its own short
  `PalLLM:Http:SelfDescriptionCacheSeconds` TTL (`15` seconds by default) so
  AI/MCP callers can reconnect cheaply without carrying a long stale window. The mutable proof
  endpoints (`/api/bridge/proof` and `/api/release/readiness`) do this
  without server-side output caching, so a newly captured smoke, native-proof,
  or proof-bundle export is visible immediately.
- Browser-facing responses also include baseline security headers:
  `Content-Security-Policy`, `Permissions-Policy`, `Referrer-Policy`,
  `X-Content-Type-Options`, and `X-Frame-Options`.
- Health probes return machine-readable `application/health+json` payloads with `status`, `totalDurationMs`, and per-check `results.*.data`. `GET /health/ready` now returns both `results.readiness` (runtime invariants) and `results.inference_recent_window` (live-lane budget posture).
- `400 Bad Request` is returned with a standard `ValidationProblemDetails` body for requests the `PalApiValidation` filter rejects.
- Request types not covered by the validator still return `400 Bad Request` with `ProblemDetails` for unparseable JSON.
- API and MCP JSON request bodies are capped by `PalLLM:Http:ApiRequestBodyMaxBytes`
  (`10485760` bytes by default). Oversized declared bodies return
  `413 ProblemDetails` before model binding; streamed bodies use the same
  ASP.NET Core request-size feature before endpoint code reads them.
- Heavy local-work lanes (`/api/chat`, `/api/chat/stream`, `/api/chat/party`, `/api/inference/warmup`, `/api/vision/*`, `/api/tts/synthesize`, and `/api/audio/transcribe`) combine concurrency admission control with endpoint request timeouts. Saturated lanes return `429 ProblemDetails`; accepted one-shot requests that exceed their configured `PalLLM:Http:*RequestTimeoutSeconds` budget return sanitized `503 ProblemDetails`. The SSE chat stream has already flushed `200 text/event-stream`, so it reports the same chat timeout as a sanitized `error` event with `reason=request_timeout`.

## Surface at a glance

| Category | Endpoint | Body | Notes |
|---|---|---|---|
| **Ops** | `GET /` | - | Static Field Console dashboard with weak content-hash ETag revalidation |
| | `GET /metrics` | - | Prometheus exposition format, including recent-window readiness gauges |
| | `GET /health/live` | - | Standard liveness probe as `application/health+json` |
| | `GET /health/ready` | - | Readiness probe as `application/health+json` with both core-runtime and recent-window inference diagnostic `data` |
| | `GET /openapi/v1.json` | - | OpenAPI 3.1 document in JSON |
| | `GET /openapi/v1.yaml` | - | OpenAPI 3.1 document in YAML |
| **Protocol** | `POST /mcp` | JSON-RPC 2.0 | Streamable HTTP MCP transport endpoint; browser requests with `Origin` must be loopback or listed in `PalLLM:Auth:McpAllowedOrigins[]` |
| **Inspection** | `GET /api/health` | - | `RuntimeHealth` counters + state |
| | `GET /api/dashboard` | - | `DashboardSnapshot` aggregate |
| | `GET /api/features` | - | Feature catalog (`122` entries: `119 ready`, `2 scaffolded`, `1 deferred`) |
| | `POST /api/chat/plan` | `{ UserMessage, TaskTag?, Risk?, Hardware? }` | Advisory: infer DuoTaskKind from the user message and return the full Duo cooperation plan that would be used. Deterministic � no inference call |
| | `POST /api/promotion/apply/preview` | `{ TaskClass, PatternId? }` | Build an editor-ready change template for a specific candidate. 400 on missing TaskClass / 404 if task unobserved / 409 if not yet a candidate / 200 with DiffPreview + SafetyWarnings + RollbackCommand + Provenance otherwise |
| | `POST /api/promotion/apply` | `{ TaskClass, PatternId? }` | Persist a candidate promotion as a durable staging triple under `Runtime/PromotionStaging/`. NEVER mutates source code. 403 when `PalLLM:PromotionApply:AllowApply` is false (default), same 400/404/409 as `/apply/preview` otherwise, 200 with `PromotionApplyResult { Status, Reason, StagingRoot, TemplatePath, RollbackPath, PacketPath, ArchivedCount }` on success |
| | `GET /api/promotion/summary` | - | Hard-code promotion ledger summary: per-task-class counts (success / disagreement-block / validator-fail / human-override), success rate, most-common pattern, IsPromotionCandidate flag, recommendation sentence |
| | `GET /api/promotion/suggestions` | - | One actionable suggestion per promotion candidate: `{TaskClass, PatternId, TargetFile, SuggestedChange, EvidenceSummary, RollbackPath, Provenance (full ProofPacket)}`. Non-candidate tasks are skipped |
| | `POST /api/promotion/record` | `{ TaskClass, PatternId, Outcome, Note? }` | Record one observation; invalid outcomes return ProblemDetails 400 |
| | `GET /api/roles` | - | Local-first mesh role coverage (Edge / Worker / Judge / Media / Validator): per-slot bindings + active flag + recommendation, plus top-level ActiveBindings, CriticalGaps[], and PairingPattern message |
| | `POST /api/duo/plan` | `{ Kind, Risk, Hardware }` (numeric enum values) | Qwen Duo Mesh cooperation-pattern planner. Returns one of 10 patterns (Scout?Judge, Branch Tournament, Parallel Disagreement, etc.) with step-by-step role assignments, thinking-mode hints, context-budget hints, and escalation path. Deterministic � no inference call |
| | `POST /api/disagreement/check` | `{ WorkerOutput, JudgeOutput }` | Duo disagreement detector. Returns `{SemanticSimilarity, TokenOverlap, LengthRatio, CombinedScore, Verdict (agree/minor-drift/major-disagreement), SafetySignal (proceed/review/block), Recommendation, KeyEntityAgreement[]}`. Completes the ParallelDisagreement cooperation pattern |
| | `POST /api/proof/packet` | `{ Subsystem, Decision, PrimaryReason, Evidence[], RollbackPath, Confidence, HumanReviewRequired }` | Machine-readable provenance bundle for an automated decision. Returns `ProofPacket { Version, Id (stable SHA-256), Subsystem, Decision, PrimaryReason, CapturedAtUtc, Evidence[], ModelArtifacts[], ValidatorResults[], RollbackPath, Confidence, HumanReviewRequired }` |
| | `GET /api/self-healing/status` | - | Latest SelfHealingWorker evidence or a structured `pending` / `unreadable` marker if the watchdog has not ticked yet or the local evidence file is malformed / oversized |
| | `POST /api/why` | `{ Question: string }` | Local deterministic "why engine" � natural-language causal questions about the runtime's recent behaviour. Returns `{ Question, Intent, PrimaryReason, CausalChain[], EvidenceReferences[], Confidence }`. Never calls live inference |
| | `GET /api/describe` | - | One-shot self-description manifest for AI / MCP consumers: identity, operator happiness score (0-100 + grade + top reasons), version, current state, live surface, posture guarantees, common asks, safety notes |
| | `GET /api/quickstart` | - | Live state-aware next-step guidance for humans + AI: headline + ordered critical / recommended / optional steps, each with label / why / action / verify |
| | `GET /api/airgap/verify` | - | Air-gap posture report classifying every enabled outbound surface as loopback / private / public / disabled so operators + AI can prove "no outbound traffic" without running a packet capture |
| | `GET /api/hardware` | - | Deterministic hardware posture: OS, logical cores, rounded RAM GiB, GPU-likelihood signal, detected + effective `DuoHardwareTier`, detection confidence, and a one-sentence recommendation. Honours `PalLLM:Hardware:ForceTier` |
| | `GET /api/privacy/posture` | - | Machine-readable privacy posture. Enumerates every data-emitting surface and classifies each as `never-leaves` / `only-with-opt-in` / `leaves-by-default`. Pairs with `/api/airgap/verify` for the full "what does this install transmit?" picture. See [`PRIVACY.md`](PRIVACY.md) |
| | `POST /api/directives/plan` | `{ Utterance, AddressedPal? }` | Translate a natural-language player utterance into an ordered plan of allowlisted `PalDirective[]` the UE4SS mod can forward to the native pal-AI controller. Never emits above `PalLLM:Automation:AllowedActions`. Deterministic � no inference call |
| | `GET /api/degradation/advisory` | - | Graceful-degradation advisory from the detected `HardwareProfile` + current options. Returns a posture bucket and ordered recommendations (keep / disable / review / opt-in / leave-off). Never mutates state |
| | `GET /api/budgets` | - | Resource-budget posture per feature (inference rate, vision queue, TTS and ASR caps, memory window, bridge retention, fallback share) with ok / review / exhausted bucketing |
| | `GET /api/narration/cue` | - | World-narration advisor. Deterministic decision on whether the current scene warrants a companion's one-line quip |
| **Conversation** | `POST /api/chat/party` | `{ CharacterIds, UserMessage, CharacterNames?, TaskTag?, Threaded?, Temperature? }` | Fan a single utterance across multiple characters in order. Each per-character reply runs through the full ChatAsync pipeline. `CharacterIds` must contain 1-8 positive ids; `UserMessage` uses the same 16 KiB cap as `/api/chat`; threaded mode seeds later replies with earlier reply summaries |
| **Relationships** | `GET /api/relationships/lifetime` | - | Cross-session summary for every tracked character: first-seen, last-seen, session-count, peak/floor affinity, cumulative average, mood tally, life-story rendering; persisted JSON is read through the bounded local-artifact cap |
| | `GET /api/characters/{characterId}/mood` | - | Deterministic mood-weather forecast per character (mood / weather metaphor / tone) blended from relationship + world snapshot |
| **Streaming** | `POST /api/chat/stream` | `ChatRequest` | Server-Sent Events variant of `/api/chat`. Emits `started` / `phase` / `final` (or `error`) events so clients see progress before the final `ChatResponse` lands. The runtime work uses `PalLLM:Http:ChatRequestTimeoutSeconds`; timeout is reported as `event: error` with `reason=request_timeout` because the stream status has already been sent |
| | `GET /api/bridge/proof` | - | `BridgeProofSnapshot` for native readiness, widget-seam evidence, and live loop closure |
| | `GET /api/release/readiness` | - | `ReleaseReadinessSnapshot` for route counts, audits, docs, blockers, and bounded local evidence readers (`SmokeEvidence`, `NativeProofEvidence`, `ProofBundleEvidence`, `SupportBundleEvidence`, `PackageVerificationEvidence`, `ArtifactIntegrityEvidence`, `FullAuditEvidence`) |
| | `GET /api/inference/performance` | - | `InferencePerformanceSnapshot` with recent per-model latency-budget, reliability, and token trends |
| | `GET /api/inference/collaboration` | Query params only | Hardware-aware PalLLM collaboration snapshot |
| | `POST /api/inference/collaboration/plan` | `ModelCollaborationDecisionRequest` | Task-specific PalLLM collaboration plan |
| | `POST /api/inference/warmup` | - | Bounded manual warmup of the currently active inference lane |
| | `GET /api/mcp/upstream` | - | Discovered upstream MCP server snapshots with stable `ErrorCode` values on failure; tool/resource/prompt arrays are bounded per upstream |
| | `GET /api/packs` | - | Loaded narrative packs; `PackSummary.FilePath` is pack-root-relative so listings stay stable across machines without exposing absolute local paths |
| | `GET /api/logs` | - | Adapter log tail; common bridge/outbox/screenshot warning lines use stable local failure summaries instead of raw exception text |
| | `GET /api/world` | - | Current snapshot + bridge activity |
| | `GET /api/relationships` | - | All per-character relationships |
| | `GET /api/relationships/{characterId:int}` | - | Single relationship, `404` if none |
| | `GET /api/bridge/outbox` | - | Pending `chat_reply` envelopes |
| | `GET /api/bridge/ui-probe` | - | Ranked `UserWidget` candidates from UE4SS diagnostics; corrupt or oversized dump files are ignored |
| **Mutate** | `POST /api/packs/reload` | - | Rescan pack directory; malformed or oversized narrative-pack JSON files are skipped instead of aborting the whole reload |
| | `POST /api/packs/validate` | Raw `application/json` pack payload (`1,000,000` byte max) | Structured `400` on schema or publication-safety errors, `413` when oversized |
| | `POST /api/snapshot` | `GameWorldSnapshot` | Updates adapter state |
| | `POST /api/bridge/drain` | - | Manual inbox drain; oversized, malformed, unreadable, or unknown inbox files are quarantined to `Bridge/Failed` |
| | `POST /api/bridge/outbox/clear` | - | Empty outbox directory |
| | `POST /api/chat` | `ChatRequest` | Core chat orchestration |
| | `POST /api/memory/recall` | `MemoryRecallRequest` | Scored recall |
| | `POST /api/vision/describe` | `VisionDescribeRequest` | Freeform scene description |
| | `POST /api/vision/world-state` | `VisionWorldStateRequest` | Structured world-state extract |
| | `POST /api/vision/screenshots/process` | - | Manual screenshot ingest pass |
| | `POST /api/session/save` | - | Persist memory + relationships; local write failures return `Success=false` with stable status text and a blank `FilePath` rather than raw filesystem details |
| | `POST /api/session/reload` | - | Load from disk; oversized, malformed, or unreadable primary files fall back to `session.json.bak`, and local load failures report stable status text with a blank `FilePath` |
| | `POST /api/audio/transcribe` | `AudioTranscribeRequest` | Transcribe caller-supplied base64 audio through the opt-in ASR endpoint |
| | `POST /api/tts/synthesize` | `TtsSynthesizeRequest` | Synthesize speech audio |

Total `/api` routes: 57. Operational routes outside `/api`: 6 (`/`, `/metrics`, `/health/live`, `/health/ready`, `/openapi/v1.json`, `/openapi/v1.yaml`). Separate protocol route: 1 (`/mcp`).

Read-mostly inspection surfaces deliberately support conditional requests:
`GET /api/dashboard`, `GET /api/features`, `GET /api/describe`,
`GET /api/bridge/proof`, `GET /api/inference/performance`,
`GET /api/release/readiness`, and `GET /api/mcp/upstream`
emit strong `ETag` values plus private cache headers and can return
`304 Not Modified` when the client sends a matching `If-None-Match`.

## Operational endpoints

### GET /health/live

Returns `application/health+json` with a single `liveness` entry. This probe
only answers "is the sidecar process alive and serving HTTP?" and stays
`Healthy` as long as the runtime object exists and the process is responsive.

### GET /health/ready

Returns `application/health+json` with two readiness-oriented results:

- `results.readiness` covers core runtime invariants such as runtime-root
  availability, inference circuit state, and queue backlogs.
- `results.inference_recent_window` mirrors the bounded
  `InferencePerformanceSnapshot.Assessment` surface from
  `GET /api/inference/performance` into the lightweight health payload.

`results.inference_recent_window.data.assessment.Status` uses the recent-window
status vocabulary (`healthy`, `degraded`, `critical`, `insufficient_data`,
`no_data`), while the enclosing health-check `status` stays in ASP.NET Core's
health-check terms (`Healthy` or `Degraded`). When live chat or vision lanes
fall outside the proven latency/reliability envelope, `/health/ready` remains
HTTP `200 OK` but reports top-level `status=Degraded` and includes a bounded
`alerting_lanes[]` preview in the result data.

### GET /metrics

Prometheus exposition format v0.0.4. In addition to the long-lived runtime
counters and gauges, the endpoint now exports first-class recent-window
readiness families for live chat and vision lanes:

- `palllm_inference_recent_window_status{status,budget}` - one-hot overall
  recent-window readiness state.
- `palllm_inference_recent_window_sample_count` plus the matching
  success/target-hit/ceiling-hit ratio gauges.
- `palllm_inference_lane_status{operation,provider,model,budget,status}` -
  one-hot per-lane readiness state for each active lane in the recent window.
- `palllm_inference_lane_sample_count{operation,provider,model,budget}` -
  recent-window sample count for each active lane.

`GET /api/release/readiness` also carries:

- `SmokeEvidence` backed by `Runtime/ReleaseEvidence/latest-smoke.json`. A
  successful `scripts/run-sidecar-smoke.ps1` pass updates that durable
  artifact and a timestamped history file under
  `Runtime/ReleaseEvidence/History`.
- `NativeProofEvidence` backed by
  `Runtime/ReleaseEvidence/latest-native-proof.json`. A successful
  `scripts/run-native-proof.ps1` pass updates that durable artifact and a
  timestamped history file under the same history directory. The evidence
  carries stable `DiagnosisCode` / `DiagnosisSummary` fields plus
  `DiagnosisAction` / `DiagnosisCommand`, so automation can route the next
  fix without parsing console prose.
- `ProofBundleEvidence` backed by
  `Runtime/ReleaseEvidence/latest-proof-bundle.json` plus the sibling archive
  `Runtime/ReleaseEvidence/latest-proof-bundle.zip`. A successful
  `scripts/export-release-proof-bundle.ps1` pass updates both durable files and
  timestamped history artifacts under the same history directory. The manifest
  also reports compact `/api/inference/performance` evidence:
  `InferencePerformanceStatus`, sample/lane counts, alerting lane count,
  latest response/fingerprint receipt lane count, latest token receipt lane
  count, upstream processing-duration receipt lane count, upstream phase-timing
  receipt lane count, total tokens, and content-free TTS/ASR proof fields
  (`TtsEnabled`, `TtsCallCount`, `TtsFailureCount`, and
  `TtsSuccessEvidenceCount`; `AsrEnabled`, `AsrCallCount`, `AsrFailureCount`,
  `AsrSuccessEvidenceCount`, `AsrEndpointingReceiptCount`,
  `AsrBargeInCount`, `AsrEndpointingReviewCount`,
  `AsrConfidenceReceiptCount`, `AsrConfidenceReviewCount`,
  `AsrTimingReceiptCount`, `AsrTimingReviewCount`,
  `AsrQualityReceiptCount`, `AsrQualityReviewCount`,
  `AsrUpstreamRequestIdReceiptCount`,
  `AsrUpstreamProcessingReceiptCount`, and
  `AsrUpstreamPhaseTimingReceiptCount`), plus
  `PrivacyRedactionApplied`,
  checked/redacted file counts, rule hits,
  `PublicationScanPassed`,
  `PublicationScanCheckedFileCount`, and `PublicationScanViolations` after
  redacting and scanning the portable bundle text surface, and the
  release-readiness reader verifies the paired zip
  contains the bundle manifest plus its manifest-listed relative path-safe
  entries. The archived `proof-bundle.json` must also match the latest
  manifest's release publication, bridge proof, smoke/native-proof status,
  inference-performance receipt counts, TTS/ASR proof counters,
  native-HUD config, optional-file,
  blocker, and ready-evidence fields before the bundle is trusted as recorded.
- `SupportBundleEvidence` backed by
  `Runtime/SupportEvidence/latest-support-bundle.json` plus the sibling
  archive `Runtime/SupportEvidence/latest-support-bundle.zip`. A successful
  `scripts/export-support-bundle.ps1` pass or packaged `support.bat` run
  updates both durable files and timestamped history artifacts under
  `Runtime/SupportEvidence/History`. The manifest carries the same portable
  privacy-redaction and publication-scan fields so tester handoff archives can
  fail fast before they are shared, and the paired zip receives the same
  manifest/path-safe-entry check. Its archived `support-bundle.json` must match
  the latest launch/smoke/native-proof/proof-bundle/package/full-audit status,
  runtime root, native-HUD config, optional-file, blocker, and ready-evidence
  fields before the support bundle is trusted as recorded.
- Every local JSON artifact read on this surface is bounded by
  `PalLLM:Http:LocalArtifactMaxBytes` (`65536` bytes by default). The same
  cap also protects `GET /api/self-healing/status`, `GET /api/relationships/lifetime`,
  and the ui-probe diagnostics that feed `GET /api/bridge/ui-probe` and
  `GET /api/bridge/proof`. Oversized, truncated, or malformed files degrade to
  sanitized `invalid`/empty results instead of echoing raw file-read exceptions.
- `PackageVerificationEvidence` backed by
  `Runtime/ReleaseEvidence/latest-package-verification.json`. A successful
  `scripts/package-release.ps1` or `scripts/verify-release-package.ps1` pass
  updates that durable artifact and a timestamped history file under the same
  history directory after validating the candidate package against
  `RELEASE_PACKAGE_MANIFEST.json` and scanning the shipped text surface for
  private sibling-project terms, endorsement/approval claims, unrelated
  franchise references, broad platform-scope drift, and root player-copy brand
  drift.
- `ArtifactIntegrityEvidence` backed by
  `Runtime/ReleaseEvidence/latest-artifact-integrity.json`. A successful
  `scripts/compute-release-checksums.ps1` pass writes this durable evidence
  artifact plus `SHA256SUMS`, `SHA512SUMS`, and `checksums.json` under
  `artifacts/packaging/`, so release-readiness can tell whether the current
  candidate zip has local digest manifests and whether detached signature files
  were present when checksums were computed.
- `FullAuditEvidence` backed by
  `Runtime/ReleaseEvidence/latest-full-audit.json`. A successful
  `scripts/run_full_audit.ps1` pass updates that durable artifact and a
  timestamped history file under the same history directory after recording the
  build/test/drift/package-verification posture plus pointers back to the
  timestamped repo-local `artifacts/full-audit/<stamp>/` bundle. The
  release-readiness reader verifies the referenced `RESULTS.md` verdict,
  audit-root path containment, pass/fail count consistency, and step-log count
  before trusting the artifact as recorded.
- Each evidence block now also carries `FreshnessStatus`, `FreshUntilUtc`, and
  `FreshnessWindowHours` so release automation can tell "artifact exists" from
  "artifact is still fresh enough to trust for the current candidate build".

## Machine-readable spec

`GET /openapi/v1.json` returns a JSON Schema 2020-12-compliant **OpenAPI 3.1
document** generated directly from the minimal-API route registrations in
`src/PalLLM.Sidecar/Program.cs` and
`src/PalLLM.Sidecar/RouteRegistrations/*.cs`. `GET /openapi/v1.yaml` serves the same
document in YAML form. The runtime annotates endpoints with stable
`operationId` values (`WithName`), summaries, tags, and explicit request-body
metadata where binding is manual (`/api/packs/validate`), so SDK generators and
other machine consumers get a cleaner contract than raw route discovery alone.
The OpenAPI document intentionally publishes neutral component names for the
bridge-owned snapshot family (`GameWorldSnapshot`, `GameBaseSnapshot`,
`GameCharacterSnapshot`) so SDKs and external clients do not have to couple
to the current game target's internal type names.
The repo also commits the live-endpoint snapshot at
[`openapi/palllm-sidecar-v1.json`](openapi/palllm-sidecar-v1.json); regenerate
or verify it with `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-openapi.ps1` (add `-Verify`
to fail on drift). CI and `scripts/run_full_audit.ps1` both use that command so
the checked-in machine-readable contract cannot silently diverge from code.
Feed either format into [OpenAPI Generator](https://openapi-generator.tech/) or
other client-SDK tooling; this markdown reference stays authoritative for the
human-readable context that a spec alone can't carry (task-kind enums,
`ResponsePath` semantics, request-id propagation, `ProblemDetails` shapes).

---

## Core request types

### ChatRequest

```jsonc
{
  "CharacterId": 7,                              // int, optional - resolved against the snapshot
  "CharacterName": "Camp Guardian",              // string, optional - used as fallback if Id not found
  "TaskTag": "chat_camp",                        // string, default "player_chat"
  "Priority": "Normal",                          // "High" | "Normal" | "Low"
  "UserMessage": "How should we set up camp?",   // string, REQUIRED, non-blank
  "Temperature": 0.7,                            // float, optional - overrides InferenceOptions default
  "MaxTokens": 220,                              // int, optional - overrides task-profile default
  "ImageBase64": null,                           // string, optional - triggers vision augmentation
  "ImageMimeType": null,                         // string, optional - defaults to "image/png"
  "RequestId": null                              // string, optional - runtime auto-generates if blank
}
```

**Validation** (400 on failure):
- `UserMessage` is required, must be non-blank, and must be at most `16 KiB`.
- `Temperature`, if supplied, must be in `[0.0, 2.0]`.
- `MaxTokens`, if supplied, must be positive.

`POST /api/chat/party` applies the same `UserMessage` and `Temperature`
validation, also requiring `CharacterIds` to contain at least one and at most
eight positive ids. The cap is deliberately below any "batch job" scale because
party chat runs a full chat turn per id.

The deterministic advisory/proof POST endpoints (`/api/chat/plan`, `/api/why`,
`/api/directives/plan`, `/api/duo/plan`, `/api/disagreement/check`,
`/api/proof/packet`, and `/api/inference/collaboration/plan`) also reject
caller-supplied free text above `16 KiB`. Short labels such as pal names, task
tags, risk tags, context-budget labels, and subsystem names are capped at `128`
characters. Proof-packet `Evidence` is capped at `32` lines; each line uses the
same `16 KiB` text cap. Empty `{}` bodies remain valid for advisory endpoints
that have deterministic defaults.
When `Temperature` and `MaxTokens` are omitted, `/api/chat` uses the
runtime-selected `InferenceExecutionProfile` for the active model lane rather
than only the global `Inference` defaults.

**Response** (`ChatResponse`):

```jsonc
{
  "RequestId": "chat-0a1b2c3d4e5f",
  "CharacterName": "Camp Guardian",
  "TaskKind": "InteractiveChat",                 // PalTaskKind enum as string
  "InferenceModel": "hf.co/unsloth/Qwen3.6-35B-A3B-Instruct-UD-Q4_K_XL-GGUF",
  "InferenceProfileId": "fast-interactive",      // execution profile chosen for this turn
  "InferenceLane": "fast-iterative",             // higher-level lane behind the profile
  "ThinkingRequested": false,                    // null only when the upstream request carried no thinking toggle
  "InferenceEnabled": false,
  "InferenceAttempted": false,
  "InferenceBypassed": false,
  "StatusMessage": "Fallback strategy 'crafting-discipline' is active.",
  "ResponsePath": "fallback_inference_disabled", // see below for enum
  "MaxTokens": 220,
  "VisualContextSource": "none",                 // none | vision_model | snapshot_fallback
  "SystemPrompt": "You are PalLLM ...",
  "AssistantMessage": "Start by ...",            // null only when everything is disabled
  "UsedFallback": true,
  "FallbackStrategy": "crafting-discipline",
  "FallbackPhase": "Relax",
  "FallbackSignals": ["maintenance", "single_bottleneck"],
  "Presentation": { /* PresentationCuePlan */ },
  "Action": null,                                // ActionIntent when Automation is enabled + allowlisted
  "MemoryMatches": ["..."]
}
```

`SystemPrompt` is hard-bounded to `16,000` characters, live-model
`AssistantMessage` text is hard-bounded to `8 KiB` before memory/TTS/outbox
fan-out, and each surfaced recalled-memory entry is bounded by
`ConversationMemoryStore.MaxContentChars` (`4 KiB`). These are local safety
rails, not caller-tunable contract knobs.

**`ResponsePath` enum:**

| Value | Meaning |
|---|---|
| `live_inference` | Live endpoint produced the reply |
| `fallback_policy_bypass` | Routine task, bypassed inference deliberately |
| `fallback_inference_disabled` | Inference disabled in config |
| `fallback_inference_failed` | Upstream failed / circuit breaker open / malformed JSON |
| `rate_limited_fallback` | Per-character rate limiter diverted the call |
| `inference_disabled_no_fallback` | Both inference and fallback are disabled |
| `inference_failed_no_fallback` | Inference failed, fallback also disabled |

**Execution metadata notes:**

- `InferenceModel` is the active model lane that handled the turn.
- `InferenceProfileId` is the runtime-selected execution profile for that
  turn. Current shipped values are `fast-reactive`, `fast-interactive`,
  `fast-deliberate`, `fast-creative`, `dense-interactive`,
  `dense-deliberate`, and `dense-creative`. The profile also controls the
  prompt/evidence budget used to build the Palworld system prompt.
- `InferenceLane` is the higher-level lane classification behind the profile:
  `fast-iterative` or `deliberate`.
- `ThinkingRequested` records whether PalLLM asked the upstream server to run
  with thinking enabled for that turn.
- `VisualContextSource` distinguishes whether the turn used no image context,
  live vision augmentation (`vision_model`), or the deterministic
  snapshot-derived fallback (`snapshot_fallback`).
- Successful upstream inference JSON bodies are capped by
  `Inference.MaxResponseBytes` (`65536` bytes by default) before PalLLM fully
  parses them.
- Successful upstream assistant text is also trimmed to the local `8 KiB`
  player-facing cap before it can reach memory, optional TTS, the bridge outbox,
  or the HTTP response.
- On upstream transport failure, `StatusMessage` intentionally stays high-level
  (`HTTP 502`, timed out, unreachable, malformed JSON, etc.) and never echoes
  raw exception text or upstream response bodies.
- If the success body itself exceeds that cap, `StatusMessage` stays on the
  explicit local contract `Inference response exceeds the configured cap of N
  bytes.` rather than depending on transport exception text.

### MemoryRecallRequest

```jsonc
{
  "CharacterId": 7,                              // int, optional - applies +character-boost to scoring
  "Query": "camp preparation at night",          // string, REQUIRED, non-blank
  "Limit": 5                                     // int, default 5, must be positive
}
```

**Response**: array of `{ Score, CharacterId, CharacterName, SpeakerRole, Content, Tags, CreatedAtUtc, Importance }`.

### VisionDescribeRequest

```jsonc
{
  "ImageBase64": "<base64>",                     // REQUIRED
  "ImageMimeType": "image/png",                  // default "image/png"
  "Prompt": null,                                // free text override; runtime applies a default if blank
  "SystemPrompt": null,                          // optional system message
  "MaxTokens": 180,                              // default from VisionOptions
  "Temperature": 0.2                             // default from VisionOptions
}
```

**Validation** (422): `ImageBase64` must be non-blank, structurally valid base64 image data, and decode to at most `Vision.MaxImageBytes` (6 MB default). The sidecar rechecks the same base64 contract inside the vision client so MCP/direct callers cannot bypass HTTP validation.

Successful upstream vision JSON bodies are capped by
`Vision.MaxResponseBytes` (`65536` bytes by default) before PalLLM fully
parses them.
- If the success body crosses that budget, `StatusMessage` stays on the same
  explicit local contract: `Vision response exceeds the configured cap of N
  bytes.`

**Response** (`VisionDescribeResponse`):

```jsonc
{
  "Success": true,
  "Description": "Night-time base perimeter with one Rayhound circling the gate.",
  "StatusMessage": "Vision describe completed.",
  "Model": "gemma4:e2b",
  "LatencyMs": 412
}
```

On upstream failure, `StatusMessage` uses the same sanitized transport-summary
policy as chat: high-level cause, no raw upstream body or exception text.

### VisionWorldStateRequest

```jsonc
{
  "ImageBase64": "<base64>",
  "ImageMimeType": "image/png",
  "Hint": "player just crested the hill",        // optional prose the model can use
  "ApplyToSnapshot": true                        // if true, runtime merges extracted state into snapshot
}
```

**Response** (`VisionWorldStateResponse`):

```jsonc
{
  "Success": true,
  "StatusMessage": "Vision world-state extracted. Applied to snapshot.",
  "Model": "gemma4:e2b",
  "LatencyMs": 1103,
  "RawContent": "{ ... model's raw JSON ... }",
  "State": { /* VisionWorldStateSnapshot */ },
  "Applied": true
}
```

### TtsSynthesizeRequest

```jsonc
{
  "Text": "Thank you, companion.",               // REQUIRED, capped by Tts.MaxCharacters
  "Voice": "en_US-amy-medium",                   // optional - falls back to Tts.DefaultVoice
  "WriteToDisk": true                            // if true, audio lands under runtime-root/TTS
}
```

**Response** (`TtsSynthesizeResponse`):

```jsonc
{
  "Success": true,
  "StatusMessage": "TTS synthesis completed.",
  "Voice": "en_US-amy-medium",
  "MimeType": "audio/wav",
  "AudioBytes": 28244,
  "FilePath": "C:\\Users\\...\\PalLLM\\TTS\\tts-20260417...wav",
  "AudioBase64": null                            // populated when WriteToDisk=false
}
```

Successful upstream audio bodies are capped by `Tts.MaxResponseBytes`
through the shared bounded HTTP byte reader before PalLLM materializes or
writes them.
The outbound TTS adapter defaults to the legacy local body
`{ "text", "voice" }`. Set `PalLLM:Tts:RequestFormat=openai_speech` to target
OpenAI-compatible speech endpoints such as vLLM-Omni `/v1/audio/speech`, which
uses `input`, `voice`, optional `model`, and `response_format`.
For `openai_speech`, PalLLM trusts a concrete upstream audio `Content-Type`
when present; if the endpoint omits it or returns generic
`application/octet-stream`, PalLLM infers the MIME type from
`Tts.ResponseFormat` so `mp3`, `flac`, `opus`, `aac`, and raw `pcm` can land
with the right file extension and playback hint.
The UE4SS Lua bridge recognizes the same compressed media-player containers as
the runtime hint surface (`mp3`, `m4a`, `aac`, `wma`, `ogg`, `opus`, `flac`),
so those artifacts no longer get rejected before the local helper launch just
because they are not WAV.
Raw PCM artifacts (`.pcm`, `audio/pcm`, `audio/l16`, including MIME parameters
such as `audio/L16; rate=24000; channels=1`) are recognized as `raw_pcm`
proof-only output. The bridge records artifact size, mode/hint,
MIME/extension, elapsed milliseconds, zero launch attempts, optional
content-free raw timing metadata, the stable
`FailureCode=raw_pcm_native_mixer_required`, and
`speech raw pcm requires native mixer binding` instead of launching a desktop
helper until a native mixer seam is proven. Incomplete raw sample frames report
`FailureCode=raw_pcm_block_alignment_invalid`. Operators can opt into a real
engine-side callback by setting `native_audio_mixer_enabled=true` and
`native_audio_mixer_callback_name` in `config/native-hud.lua`; missing,
throwing, or rejecting callbacks emit `native_audio_mixer_unavailable`,
`native_audio_mixer_failed`, or `native_audio_mixer_rejected`, and only a
callback return of `true` or `{ started = true }` emits `Started=true` with
`PlaybackMode=native_mixer`. Raw PCM and WAV receipts can
also include `MixerConversionHint` values such as
`byte_swap_integer_to_float32` or `integer_to_float32`, so native-audio proof
can show conversion work before a future in-world mixer is promoted. Receipts
also include `MixerQuantumMs`, `MixerQuantumFrames`,
`MixerQueueDepthEstimate`, `MixerTailFrames`, `MixerBufferedMs`, and
`MixerTailMs` when sample frames and sample rate are known, giving the future
native mixer low-latency queue-depth and buffer-duration estimates without
storing audio. Speech receipts also include `PlaybackSequence`,
`SupersededRequestId`, `SupersededSpeechCount`, `SupersededSpeechAgeMs`,
`SupersededSpeechBufferedMs`, `SupersededSpeechRemainingMs`, and
`CancellationMode`, so native-audio proof can show when a newer turn
superseded stale speech, whether a prior clip likely still had buffered audio,
and whether the current desktop helper could hard-cancel it. `/api/bridge/proof`
also derives `SpeechPlaybackIngressLagMs`, `SpeechPlaybackOutboxLagMs`, and
`SpeechPlaybackDeliveryLagMs` from existing bridge event timestamps, so a
matching playback receipt can prove request-to-speech, outbox-to-speech, and
visible-delivery-to-speech lag without adding audio content or paths to the
bridge payload. Other skipped local playback
paths also carry stable `FailureCode` values such as `unsupported_format`,
`speech_file_empty`, `wave_header_invalid`, `wave_encoding_unsupported`,
`wave_block_alignment_invalid`, and `launch_failed` so automation does not
parse reason prose.
When synthesis fails, `StatusMessage` reports a sanitized transport summary
(`HTTP 502`, timed out, unreachable, oversized audio body, etc.) rather than
echoing raw upstream bodies or exception text.

### AudioTranscribeRequest

```jsonc
{
  "AudioBase64": "UklGRiQAAABXQVZF...",      // REQUIRED, capped by Asr.MaxAudioBytes after decode
  "AudioMimeType": "audio/wav",               // optional - allowlisted audio MIME
  "Language": "en",                           // optional short language hint
  "Prompt": "Player voice command.",          // optional transcription context
  "Endpointing": {                             // optional, content-free client VAD receipt
    "SpeechMs": 1200,
    "LeadingSilenceMs": 300,
    "TrailingSilenceMs": 520,
    "EndpointReason": "client_vad_silence",
    "BargeIn": false
  }
}
```

**Response** (`AudioTranscribeResponse`):

```jsonc
{
  "Success": true,
  "Transcript": "Meet at the ridge.",
  "StatusMessage": "Audio transcription completed.",
  "Model": "local-whisper-small",
  "AudioBytes": 88244,
  "LatencyMs": 241,
  "UpstreamRequestId": "asr-req-001",
  "UpstreamProcessingMs": 21.125,
  "UpstreamQueueMs": 3,
  "UpstreamTimeToFirstTokenMs": 8.5,
  "UpstreamPrefillMs": 5,
  "UpstreamDecodeMs": 9,
  "Endpointing": {
    "ClientVadSupplied": true,
    "Status": "ready",
    "EndpointReason": "client_vad_silence",
    "BargeIn": false,
    "SpeechMs": 1200,
    "LeadingSilenceMs": 300,
    "TrailingSilenceMs": 520,
    "TotalTurnMs": 2020,
    "PreSpeechPaddingTargetMs": 300,
    "EndpointSilenceTargetMs": 500,
    "MaxTurnDurationMs": 30000,
    "Flags": []
  },
  "Confidence": {
    "LogprobsRequested": true,
    "LogprobsReturned": true,
    "Status": "ready",
    "TokenCount": 4,
    "AverageLogprob": -0.42,
    "MinLogprob": -0.91,
    "LowConfidenceTokenCount": 0,
    "LowConfidenceThreshold": -1.0
  },
  "Timing": {
    "VerboseJsonRequested": true,
    "VerboseJsonReturned": true,
    "SegmentTimestampsRequested": true,
    "WordTimestampsRequested": true,
    "SegmentTimestampsReturned": true,
    "WordTimestampsReturned": true,
    "Status": "ready",
    "Language": "en",
    "DurationSeconds": 2.02,
    "SegmentCount": 1,
    "WordCount": 5,
    "FirstSegmentStartSeconds": 0.16,
    "LastSegmentEndSeconds": 1.91,
    "CoveredSegmentSeconds": 1.75,
    "SegmentCoverageRatio": 0.866,
    "MaxTurnDurationMs": 30000,
    "Flags": []
  }
}
```

ASR is **off by default**. When `PalLLM:Asr:Enabled=true`, PalLLM posts
`multipart/form-data` to `PalLLM:Asr:BaseUrl`, with `file`, optional `model`,
optional `language`, optional `prompt`, optional `temperature`,
optional endpoint-proven `seed` from `PalLLM:Asr:Seed`,
`response_format` from `PalLLM:Asr:ResponseFormat` (`json` by default,
endpoint-proven `verbose_json` for metadata canaries), optional
`timestamp_granularities[]` entries from
`PalLLM:Asr:TimestampGranularities[]` (`segment` / `word`, only valid with
`verbose_json`), optional `chunking_strategy=auto` from
`PalLLM:Asr:ChunkingStrategy` for endpoint-proven server/VAD chunking, and
optional `include[]=logprobs` when
`PalLLM:Asr:RequestLogprobs=true`. The
intended target shape is an OpenAI-compatible `/v1/audio/transcriptions`
endpoint. Request-level `Language` and `Prompt` override configured
`PalLLM:Asr:Language` and `PalLLM:Asr:Prompt` defaults; blank values are
omitted entirely, and prompt hints must stay short, language-matched, and free
of player names, save paths, secrets, raw chat, or transcript content.
Incoming audio is validated for base64 shape, decoded byte cap, and
MIME allowlist before an upstream call is attempted; upstream JSON is read
through `Asr.MaxResponseBytes`, and the returned `text` transcript is capped by
`Asr.MaxTranscriptCharacters`. `Endpointing` is never forwarded upstream; it is
a local proof receipt for client/native VAD timing, turn-close latency, and
barge-in cancellation evidence. `Confidence` is also content-free: it records
only whether logprobs were requested/returned plus compact token-count and
logprob summary fields, never token text or raw audio. `UpstreamRequestId` and
`Upstream*Ms` fields are sanitized response-header receipts from compatible
servers (`x-request-id`, `openai-processing-ms`, or `Server-Timing`) and remain
empty/null when the upstream omits them. `Timing` is another content-free
receipt: verbose `segments[]` / `words[]` timestamps are reduced to counts,
durations, coverage, and review flags only; segment text, word text, prompt
hints, raw audio, and verbose JSON are not stored in health, metrics, or proof
bundles. `Quality` is content-free as well: verbose segment
`avg_logprob`, `compression_ratio`, `no_speech_prob`, and `temperature`
metadata are reduced to counts, extrema, thresholds, and review flags; segment
text, token ids, raw audio, and verbose JSON are not stored in health, metrics,
or proof bundles. Disabled, malformed, oversized, timeout, and upstream-error
cases return `Success=false` with sanitized status text.

### NarrativePackDefinition (pack-validate body)

See `src/PalLLM.Domain/Packs/NarrativePackModels.cs` for the authoritative shape. Validator responses on invalid packs are shaped as `NarrativePackValidationResult`:

```jsonc
{
  "IsValid": false,
  "Name": "",
  "CharacterCount": 2,
  "RelationshipCount": 1,
  "MemorySeedCount": 1,
  "Errors": [
    { "Path": "Name", "Message": "Pack name is required." },
    { "Path": "Characters[0].Id", "Message": "Character id is required." }
  ]
}
```

Malformed JSON uses the same `400` result shape, but the top-level error stays
on a stable location-based message contract such as `Pack JSON could not be
parsed near line 1, byte 3.` rather than forwarding raw `JsonException.Message`
text.

Publication-safety findings use the same `Errors[]` shape. The validator is a
deterministic guardrail, not legal advice: it rejects obvious official
endorsement/sponsorship claims, unrelated third-party IP references,
model/runtime/vendor brand references, and broad multi-game platform language
before shareable pack text is loaded or published.

### GameWorldSnapshot (snapshot body)

Publication-facing OpenAPI schema id for the snapshot body accepted by
`POST /api/snapshot`. The current bridge implementation still backs it with the
internal `GameWorldSnapshot` CLR type in
`src/PalLLM.Domain/Integration/Contracts.cs`. Key fields:

- `Source`, `WorldName`, `IsWorldLoaded`, `CurrentTick`, `TicksPerHour`, `TicksPerDay`
- `Biome`, `Weather`, `TimeOfDay` - free-text; PalLLM keyword-matches these without assuming a schema
- `ThreatLevel`, `AlertLevel`, `PlayerHealthFraction`, `PlayerStaminaFraction`, `PlayerHungerFraction` - `float?`, expected `[0, 1]`
- `IsInBase` - `bool?`; `null` lets the runtime infer from `ActiveBaseIds` + keyword cues
- `ActiveBaseIds: string[]`, `KnownBases: GameBaseSnapshot[]`
- `NearbyHostiles: string[]`, `NearbyFriendlies: string[]`, `NearbyResources: string[]`, `RecentEvents: string[]`
- `Characters: GameCharacterSnapshot[]` - each carries `Id`, `DisplayName`, `Species`, `Traits`, `Skills`, `Needs`, `HealthFraction`, etc.

The runtime always deep-clones on update, so callers can reuse the payload safely.
Bridge-facing JSON is normalized on ingest: omitted or explicit `null` lists,
dictionaries, and nested `Position` objects are converted to empty collections
or a zeroed vector instead of faulting the sidecar. That keeps partial
Palworld bridge payloads and smoke scripts resilient while preserving the same
published `GameWorldSnapshot` contract.

---

## Inspection endpoints

### GET /api/health -> RuntimeHealth

Atomic snapshot of every counter the runtime exposes. Documented fields include:

- Inference: `InferenceSuccessCount`, `InferenceFailureCount`, `InferenceBypassCount`, `FallbackReplyCount`, `RateLimitedCount`, `InferenceCircuitState`, `InferenceCircuitFailures`
- Active inference lane: `InferenceModel`, `InferenceActiveModel`,
  `InferenceActiveTierId`, `InferenceLastSeenAvailableModels[]`, and
  `InferenceWarmup` (`Enabled`, `Status`, `LastReason`,
  `LastAttemptAtUtc`, `LastSuccessAtUtc`, `LastLiveInferenceAtUtc`,
  `LastLiveInferenceModel`, `LastFailureAtUtc`,
  `AttemptCount`, `SuccessCount`, `FailureCount`, `LastLatencyMs`)
- Tokens: `TotalPromptTokens`, `TotalCompletionTokens`, `TotalInferenceTokens`
- Vision: `VisionCallCount`, `VisionFailureCount`
- TTS/audio: `TtsEnabled`, `TtsCallCount`, `TtsSuccessCount`,
  `TtsFailureCount`
- ASR/audio: `AsrEnabled`, `AsrCallCount`, `AsrSuccessCount`,
  `AsrFailureCount`, `AsrEndpointingReceiptCount`, `AsrBargeInCount`,
  `AsrEndpointingReviewCount`, `AsrConfidenceReceiptCount`,
  `AsrConfidenceReviewCount`, `AsrTimingReceiptCount`,
  `AsrTimingReviewCount`, `AsrQualityReceiptCount`,
  `AsrQualityReviewCount`, `AsrUpstreamRequestIdReceiptCount`,
  `AsrUpstreamProcessingReceiptCount`, `AsrUpstreamPhaseTimingReceiptCount`
- Bridge: `BridgeEventCount`, `BridgeBootCount`, `LastBridgeEventType`, `LastBridgeEventAtUtc`, `InboxPendingCount`, `ArchiveFileCount`, `FailedFileCount`, `OutboxPendingCount`, `ScreenshotPendingCount`. Directory backlog counts are exact up to `1024` and cap at `1024` once a folder is already well past warning thresholds.
- Native-readiness: `NativeReadiness.BridgeBootSeen`, compat signals, `HudBindReady`, `ProductionSamplerReady`, `WaypointMarkerReady`, `ActionExecutorEnabled`, `ConfiguredHudTargets`, `HudBindRecommendation`, and the current missing-prerequisite list that explains why a Palworld-native seam is not yet proven
- Bridge-loop proof: `BridgeLoop.Status`, `ActiveRequestId`, `RequestSeen`, `OutboxReplyWritten`, `VisibleDeliveryConfirmed`, `ActionPlanned`, `ActionFeedbackObserved`, `SpeechPlaybackIngressLagMs`, `SpeechPlaybackOutboxLagMs`, `SpeechPlaybackDeliveryLagMs`, `LoopClosed`, plus the last ingress / outbox / delivery / content-free speech-playback / feedback snapshots used to derive that state. `LastSpeechPlayback.ArtifactBytes` records only the local artifact size, `AudioEncoding` / `SampleFormat` / `ByteOrder` / `MixerConversionHint` plus `SampleRateHz` / `ChannelCount` / `BitsPerSample` / `DurationMs` / `ByteRate` / `BlockAlignBytes` / `AudioDataBytes` / `FrameCount` / `BlockRemainderBytes` / `ValidBitsPerSample` / `ChannelMask` record content-free WAV or raw-PCM format metadata when available, `MixerQuantumMs` / `MixerQuantumFrames` / `MixerQueueDepthEstimate` / `MixerTailFrames` / `MixerBufferedMs` / `MixerTailMs` record the low-latency native-mixer queue and buffer-duration estimate when sample frames and sample rate are known, `PlaybackSequence` / `SupersededRequestId` / `SupersededSpeechCount` / `SupersededSpeechAgeMs` / `SupersededSpeechBufferedMs` / `SupersededSpeechRemainingMs` / `CancellationMode` record content-free stale-speech supersession and prior-buffer overlap proof, `SpeechPlaybackIngressLagMs` / `SpeechPlaybackOutboxLagMs` / `SpeechPlaybackDeliveryLagMs` record matching request-to-speech, outbox-to-speech, and delivery-to-speech receipt lag, `AttemptCount` / `ElapsedMs` record bounded helper-launch proof, and `FailureCode` records a stable skipped-playback taxonomy; raw PCM keeps `AttemptCount=0` and `FailureCode=raw_pcm_native_mixer_required` while the native mixer callback is disabled, callback failures use `native_audio_mixer_unavailable`, `native_audio_mixer_failed`, or `native_audio_mixer_rejected`, partial raw frames use `raw_pcm_block_alignment_invalid`, and only started `PlaybackMode=native_mixer` proves engine-side raw PCM playback. No audio bytes or path are stored.
- Session: `SessionDirty`, `SessionLastSavedAtUtc`
- Adapter: `AdapterName`, `AdapterReady`, `Status`, `CharacterCount`, `RememberedEntries`, `LoadedPackCount`, `KnownBaseCount`, `TrackedRelationshipCount`, `RuntimeRoot`

`BridgeLoop.Status` is the compact operator-facing state machine for the latest
tracked request. Current values are:

- `idle` - nothing tracked yet
- `awaiting_reply` - request seen but no outbox reply written yet
- `awaiting_delivery` - outbox reply written but no delivery event seen yet
- `awaiting_speech_playback` - visible delivery happened and the outbox reply
  carried a TTS artifact, but no matching speech playback receipt has arrived
- `awaiting_action_feedback` - delivery confirmed and an action was planned, but feedback has not arrived yet
- `closed` - delivery was confirmed and any required speech playback and planned
  action feedback arrived
- `delivery_unmatched`, `delivery_suppressed`, `feedback_unmatched`,
  `speech_playback_failed`, `speech_playback_unmatched` - the bridge emitted an
  event that does not cleanly close the latest tracked request

### GET /api/dashboard -> DashboardSnapshot

Aggregate of `{ Health, World, Outbox[], Logs[], Relationships[], InferencePerformance }` plus a `ServerLatencyMs` figure. The endpoint also sets a `Server-Timing: dashboard;dur=...` header so the UI can chart end-to-end latency.

### GET /api/features -> FeatureDescriptor[]

The feature catalog from `PalLlmFeatureCatalog.All`. Each entry carries `Id`, `Source`, `Status`, `Summary`, `Notes`. Use this as the authoritative in-runtime inventory rather than doc scrapes.

### GET /api/bridge/proof -> BridgeProofSnapshot

Machine-readable bridge proof built from the live runtime state plus the latest
bridge diagnostics. This is the single harvestable surface for "is the
Palworld-native loop actually proven yet?" instead of forcing operators to
compare `/api/health`, `ui_probe`, and recent bridge events manually.

Response highlights:

- `Status` - compact operator-facing state such as `awaiting_bridge_boot`,
  `ready_for_hud_bind`, `awaiting_delivery`, `awaiting_speech_playback`,
  `awaiting_action_feedback`, `delivery_proven_pending_native_hud_surface`,
  or `delivery_proven`
- `Summary` / `RecommendedNextStep` - terse human-readable guidance for the
  current bridge-proof state
- `LiveDeliveryProven` / `NativeHudBindReady` - booleans for the two most
  important player-facing proof questions
- `NativeReadiness` - the same structured readiness contract surfaced on
  `RuntimeHealth`, including compat signals, HUD readiness, configured
  target names reported by `bridge_boot`, the native-hud config source/path
  currently in effect on the bridge, the `HudBindRecommendation`
  shortlist/recommended target block, sampler readiness, and current missing
  prerequisites
- `LoopProof` - the latest tracked request/response proof chain, including
  ingress, outbox, delivery, content-free speech playback, and matching feedback.
  Speech playback receipts include request id, started/skipped state,
  playback mode/hint, MIME/extension, artifact byte count, WAV encoding,
  sample rate, channel count, bit depth, duration milliseconds, byte rate,
  block alignment, audio-data byte count, playback sequence, superseded request
  id, superseded-speech count/age, cancellation mode, launch attempt count,
  helper-launch elapsed milliseconds, stable failure code, and a short reason;
  they do not include audio bytes, text, or local file paths.
- `LastBridgeBoot`, `LastUiProbe`, `UiProbeDiagnostics` - the concrete bridge
  evidence that led to the current proof state
- `ProofLanes[]` - normalized PASS / WARN / FAIL checklist for the live proof
  path: bridge boot, `UserWidget` compatibility, `ui_probe` capture, native
  HUD bind, chat ingress, outbox reply, visible delivery, native HUD delivery,
  and guarded-action feedback when a reply planned an action. The
  `native_hud_delivery` lane becomes required once HUD bind is ready and a
  visible delivery exists; fallback `ClientMessage` / `PrintString` rendering
  keeps the reply visible but does not qualify `delivery_proven`. The
  `speech_playback` lane becomes required only when the tracked outbox reply
  carried a TTS artifact. The `native_audio_mixer` lane becomes required when
  the latest speech receipt is raw PCM or when a future native mixer reports
  started playback, separating "helper skipped raw bytes" from "native mixer
  is proven." Callback-path
  failures such as `native_audio_mixer_unavailable`,
  `native_audio_mixer_failed`, and `native_audio_mixer_rejected` get
  failure-specific summaries and next actions. Each lane carries `Name`,
  `Required`, `Status`, `Summary`, and `NextAction`; failed speech lanes use
  `LastSpeechPlayback.FailureCode` for route-specific next actions so
  dashboards and scripts do not need to rederive the same state from multiple nested fields.
- `ReadyEvidence[]` / `CurrentBlockers[]` - explicit evidence and blockers for
  dashboards, smoke tooling, and release automation

`HudBindRecommendation` is the action-oriented piece of the payload:

- `Status` - compact state such as `awaiting_ui_probe_capture`,
  `recommend_target`, `configured_targets_need_review`, or `bind_ready`
- `RecommendedTarget` - the exact first widget target to prefer in
  `native_hud_widget_targets`
- `ConfiguredTargets[]` - the currently reported bridge-side configured target
  list from the latest `bridge_boot`
- `NativeHudConfigSource` / `NativeHudConfigPath` - whether the live bridge is
  using inline defaults, a mod-side override file, a runtime-root override
  file, or failed override loading; plus the path the operator should inspect
- `Shortlist[]` / `SuggestedConfigTargets[]` - the top-ranked candidates that
  should be reviewed or exported into `config/native-hud.lua` in order

### GET /api/release/readiness -> ReleaseReadinessSnapshot

Machine-readable publication/readiness snapshot built by
`ReleaseReadinessBuilder`. Use this when automation, release tooling, or
external dashboards need the shipped runtime surface, canonical audit
commands, canonical docs, and current publication blockers without scraping
markdown.

Response highlights:

- `Runtime` - adapter name, `/api` route count, protocol route count,
  featured operational surface count, canonical paths, and conditional-read
  paths.
- `Features` - total plus `ready` / `scaffolded` / `deferred` counts sourced
  from the live feature catalog.
- `Publication` - current publication status, the next recommended hardening
  pass, the exact `NextRecommendedCommand` when one exists, and the current
  blocker list.
- `SmokeEvidence` - the latest durable smoke artifact, including loop closure,
  HUD bind readiness, configured HUD targets, and the native-hud config
  source/path captured during the smoke pass.
- `NativeProofEvidence` - the latest live Palworld proof artifact, including
  the current bridge-proof status, whether native HUD delivery was actually
  proven, the active HUD config source/path, the last delivery surface, the
  latest ready evidence/blockers, whether the helper script applied a HUD
  recommendation before the proof run, watcher start/finish timestamps, timeout
  and poll cadence, poll count, completion reason, timeout state, and a bounded
  status-transition trail. It also exposes `DiagnosisCode`,
  `DiagnosisSummary`, `DiagnosisAction`, and `DiagnosisCommand`: stable
  machine-readable proof-stop classification plus the immediate remediation,
  such as `palworld_process_missing`, `native_hud_bind_not_ready`,
  `native_hud_surface_mismatch`, or `delivery_proven_timeout`, so support tools
  do not have to scrape prose blockers or maintain their own command map. A
  readable artifact that claims
  `Status = "proven"` is still treated as `invalid` unless it also carries
  matching `BridgeProofStatus = "delivery_proven"`, `LiveDeliveryProven =
  true`, and `NativeHudBindReady = true` evidence from the same native-proof
  run.
- `ProofBundleEvidence` - the latest packaged validation bundle manifest,
  including the sibling zip path, which runtime snapshots and artifacts were
  included, which optional files were missing, and whether the bundle is
  complete enough to travel with a release candidate. It also exposes compact
  inference proof fields: `InferencePerformanceStatus`,
  `InferencePerformanceSampleCount`, `InferencePerformanceLaneCount`,
  `InferencePerformanceAlertingLaneCount`,
  `InferencePerformanceLatestReceiptLaneCount`,
  `InferencePerformanceTokenReceiptLaneCount`,
  `InferencePerformanceFinishReasonReceiptLaneCount`,
  `InferencePerformanceUpstreamRequestIdReceiptLaneCount`,
  `InferencePerformanceUpstreamProcessingReceiptLaneCount`,
  `InferencePerformancePhaseTimingReceiptLaneCount`,
  `InferencePerformanceUsageDetailReceiptLaneCount`,
  `InferencePerformanceTotalTokens`, `InferencePerformanceCachedPromptTokens`,
  and `InferencePerformanceCompletionReasoningTokens`. Those fields summarize
  the archived `inference-performance.json` snapshot without storing prompt or
  completion text in the manifest. It also carries content-free TTS/ASR
  evidence from the archived health snapshot: `TtsEnabled`, `TtsCallCount`,
  `TtsFailureCount`, `TtsSuccessEvidenceCount`, `AsrEnabled`,
  `AsrCallCount`, `AsrFailureCount`, `AsrSuccessEvidenceCount`,
  `AsrEndpointingReceiptCount`, `AsrBargeInCount`,
  `AsrEndpointingReviewCount`, `AsrConfidenceReceiptCount`,
  `AsrConfidenceReviewCount`, `AsrTimingReceiptCount`,
  `AsrTimingReviewCount`, `AsrQualityReceiptCount`,
  `AsrQualityReviewCount`, `AsrUpstreamRequestIdReceiptCount`,
  `AsrUpstreamProcessingReceiptCount`, and
  `AsrUpstreamPhaseTimingReceiptCount`. The bundle also exposes
  `PrivacyRedactionApplied`,
  `PrivacyRedactionCheckedFileCount`,
  `PrivacyRedactionRedactedFileCount`, and `PrivacyRedactionRuleHits` for the
  staged portable evidence files, plus `PublicationScanPassed`,
  `PublicationScanCheckedFileCount`, and `PublicationScanViolations` for
  portable proof-bundle publication hygiene.
  The release-readiness reader also verifies that the paired zip is readable,
  contains `proof-bundle.json`, carries every manifest-listed included file,
  and does not contain absolute, drive-qualified, traversal, or duplicate
  normalized file entries before treating the bundle as `recorded`. The
  archived manifest must also match the sidecar-readable manifest's proof
  status, inference-performance receipt counts, native-HUD config,
  optional-file, blocker, and ready-evidence fields.
- `SupportBundleEvidence` - the latest portable support bundle manifest,
  including the sibling zip path, whether launch/proof/package/audit evidence
  was present, which optional files were missing, and which blocker/evidence
  strings were captured for support handoff. It exposes the same
  `PrivacyRedaction*` and `PublicationScan*` fields for tester/support archive
  hygiene. The paired zip must also be readable, contain
  `support-bundle.json`, carry the manifest-listed included files, and use only
  relative path-safe entry names before the evidence block is trusted as
  `recorded`. The archived manifest must also match the sidecar-readable
  manifest's launch/proof/package/audit status, native-HUD config,
  optional-file, blocker, and ready-evidence fields.
- `PackageVerificationEvidence` - the latest package-verification artifact,
  including the candidate package path/type, manifest version, whether a
  packaged sidecar publish was included, and which required, unexpected, or
  mismatched files blocked verification. It also exposes
  `PublicationScanPassed`, `PublicationScanCheckedFileCount`, and
  `PublicationScanViolations` for release-package publication hygiene.
- `ArtifactIntegrityEvidence` - the latest checksum/signature evidence
  artifact, including the packaging root, `SHA256SUMS`, `SHA512SUMS`,
  `checksums.json`, artifact count, detached-signature presence, current
  blockers, and ready-evidence strings.
- `FullAuditEvidence` - the latest durable source-tree audit artifact,
  including the repo-local audit bundle paths, whether test execution, code
  coverage, SBOM generation, and packaging were enabled, how many steps passed
  or failed, and which blockers still keep the candidate from being treated as
  a fully green build. The evidence reader also validates the referenced
  `RESULTS.md` verdict, audit-root path containment, pass/fail count
  consistency, and step-log count before trusting the artifact as recorded.
- All release/self-healing artifact reads are bounded and sanitized. Malformed
  or oversized local JSON files degrade to stable `invalid` / `unreadable`
  payloads instead of surfacing raw file-read exceptions.
- `Surfaces[]`, `Audits[]`, `Documents[]` - the featured routes, canonical
  preflight commands, and doc pointers release automation should treat as the
  source of truth.

### GET /api/inference/performance -> InferencePerformanceSnapshot

Bounded recent-window summary of live inference and vision activity. Use this
when operators, dashboards, or automation need a cheap per-lane answer to
"which provider/model is currently serving traffic, how fast is it, and is it
failing?" without scraping raw metrics.

Response highlights:

- `WindowMinutes` / `GeneratedAtUtc` - the summary horizon and snapshot time.
- `SampleCount`, `SuccessCount`, `FailureCount` - the aggregate recent-window
  throughput and reliability totals.
- `AverageLatencyMs` / `P95LatencyMs` - bounded latency summary for the full
  recent window.
- `TotalPromptTokens`, `TotalCompletionTokens`, `TotalTokens` - aggregate token
  usage across the retained window.
- `Assessment` - recent-window budget/readiness summary with `Status`
  (`healthy`, `degraded`, `critical`, `insufficient_data`, or `no_data`),
  good-event ratios, and a human-readable `Summary`.
- `Assessment.BudgetName` - `interactive_chat`, `vision_extract`, or
  `mixed_recent_window`.
- `Assessment.LatencyTargetMs` / `LatencyCeilingMs` - per-budget thresholds
  when the recent window is lane-pure; `null` when the window mixes chat and
  vision and the assessment is computed from per-operation budgets instead.
- `Lanes[]` - grouped by operation kind, provider, and effective model lane.
- `Lanes[].RequestModel`, `ResponseModel`, `LastUpstreamRequestId`,
  `LastUpstreamProcessingMs`,
  `LastUpstreamQueueMs`, `LastUpstreamTimeToFirstTokenMs`,
  `LastUpstreamPrefillMs`, `LastUpstreamDecodeMs`,
  `LastResponseId`, `LastSystemFingerprint`, and `LastFinishReasons` - the
  latest served-model, upstream HTTP request/correlation id, upstream
  processing-duration receipt, upstream queue/TTFT/prefill/decode phase
  timing receipts, completion-id, backend-fingerprint, and choice-level
  stop-reason evidence observed for replay, truncation, tool-call, support-log,
  latency, or seed investigations.
  `LastUpstreamRequestId`, `LastResponseId`, and `LastSystemFingerprint` are
  empty when the upstream omits those fields; `LastUpstreamProcessingMs` is
  `null` when the upstream omits `openai-processing-ms`, a compatible
  millisecond header, or `Server-Timing` duration; the phase-timing fields are
  `null` unless the upstream exposes compatible millisecond headers or
  `Server-Timing` metrics such as `queue`, `ttft`, `prefill`, or `decode`;
  `LastFinishReasons` is empty when the upstream omits `finish_reason`.
- `Lanes[].AverageLatencyMs` / `P95LatencyMs` - bounded latency summary for the
  recent window.
- `Lanes[].LastPromptTokens`, `LastCompletionTokens`, and `LastTotalTokens` -
  the latest per-call usage receipt observed for that lane. They are `0` when
  the upstream omits usage.
- `Lanes[].LastCachedPromptTokens`, `LastPromptAudioTokens`,
  `LastCompletionReasoningTokens`, `LastCompletionAudioTokens`,
  `LastAcceptedPredictionTokens`, and `LastRejectedPredictionTokens` - detailed
  usage receipts when an OpenAI-compatible endpoint exposes cache hits, audio
  token accounting, reasoning-token accounting, or predicted-output token
  matches. They stay `0` for endpoints that omit the nested usage-detail
  objects.
- `Lanes[].AveragePromptTokens`, `AverageCompletionTokens`,
  `TotalPromptTokens`, `TotalCompletionTokens`, and `TotalTokens` - recent
  average and aggregate token-cost evidence for lane promotion decisions.
- `Lanes[].TotalCachedPromptTokens`, `TotalPromptAudioTokens`,
  `TotalCompletionReasoningTokens`, `TotalCompletionAudioTokens`,
  `TotalAcceptedPredictionTokens`, and `TotalRejectedPredictionTokens` - recent
  aggregate detailed usage evidence for proving cache, audio, reasoning, and
  prediction lanes without archiving raw prompt or completion content.
- `Lanes[].LastObservedAtUtc`, `LastSuccessAtUtc`, `LastFailureAtUtc`, and
  `LastErrorType` - the quickest way to tell whether a lane is actively serving
  or has started failing.
- `Lanes[].Assessment` - the same budget/readiness shape computed per lane,
  using the active lane budget for that operation kind.

The same bounded assessment now fans out into the lightweight operational
surfaces too: `/health/ready` exposes it as `results.inference_recent_window`,
and `/metrics` exports the same state as Prometheus gauges so alerting can key
off degraded or critical lanes without polling the full JSON snapshot.

### GET /api/inference/collaboration -> ModelCollaborationSnapshot

Machine-readable collaboration plan for the configured local model lanes.
This surface is intentionally scoped to PalLLM's Palworld-mod work: runtime,
bridge, HUD, screenshot, docs-sync, and release-hardening tasks.

Optional query parameters:

- `vramGb`
- `ramGb`
- `unifiedMemoryGb`
- `cpuOnly`
- `preferParallel`

Response highlights:

- `Hardware` - hardware class and whether PalLLM prefers parallel residency or sequential baton passing
- `ConfiguredModels[]` - operating-style classification for each configured lane (`fast-iterative` vs `deliberate`)
- `ConfiguredModels[].Capability` - deterministic capability profile for the lane: model family, recommended backend, nested `ServingProfile`, input/output modalities, vision/video/audio flags, structured-output/tool-call/speculative-decoding fit, schema-digest portability proof, precise `Speculation` mode profile, prefill/cache/speculation hints, serving optimizations, promotion receipts, metric receipts, and runtime guards
- `ConfiguredModels[].Capability.Speculation` - machine-readable speculative-decoding mode split: n-gram support, draft-model support, model-native MTP support, whether modality-isolated proof is required, whether Qwen3.6-style latency MTP should be qualified with prefix caching disabled, the recommended first mode, and the promotion guard string
- `ConfiguredModels[].Capability.ServingProfile` - machine-readable operator plan for the model server: profile id, request protocol, preferred runtime, startup hints such as `--enable-chunked-prefill`, `--prefix-caching-hash-algo sha256_cbor`, optional sparse-MoE DBO proof lanes using `--enable-dbo`, `--dbo-decode-token-threshold`, and `--dbo-prefill-token-threshold` for multi-GPU data/expert-parallel worker servers, optional proof-gated `--kv-cache-dtype fp8` for memory-pressure or long-context vLLM lanes, optional idle-only `VLLM_SERVER_DEV_MODE=1 --enable-sleep-mode`, trusted-only `--enable-mm-embeds`, optional local personality-adapter `--enable-lora --max-loras 1` guidance, qualified `--fully-sharded-loras` and staging-only `--enable-tower-connector-lora` cautions, `--limit-mm-per-prompt.*`, `--structured-outputs-config.backend xgrammar`, SGLang radix-cache / HiCache `--enable-hierarchical-cache` / `--mem-fraction-static` / `--max-running-requests` / `--enable-metrics` / `--enable-deterministic-inference` / attention-backend / FP4-FP8 KV / EAGLE-3-adaptive-SpecV2 / `--grammar-backend xgrammar` proof-lane guidance, TensorRT-LLM `/v1` guidance (`trtllm-serve`, `/health`, `/metrics`, `/v1/models`, `/v1/chat/completions`, YAML config, `--tool_call_parser`, KV-cache, speculation, disaggregated-serving, and multimodal proof receipts), Hugging Face `transformers serve` / `--continuous-batching` / `/load_model` / revision-pinning / ASR / tool-call proof guidance, OpenVINO Model Server `/v3` guidance (`--task text_generation`, `--target_device`, `/v3/models`, `/v3/chat/completions`, VLM allowlists, ASR proof, INT4 edge-model proof, and NPU `PREFILL_HINT` / `GENERATE_HINT` tuning receipts), Microsoft Foundry Local / Windows ML dynamic endpoint guidance (`foundry service status`, `/openai/status`, `/openai/models`, `/v1/chat/completions`, execution-provider proof, first-use cache/download proof, and optional ASR proof), n-gram speculative-decoding config for qualified text lanes, model-native Qwen3.6/Gemma 4 MTP guidance after replay proof (`--reasoning-parser qwen3`, Qwen `qwen3_next_mtp`, Qwen latency-focused MTP-1 with `--no-enable-prefix-caching`, Gemma 4 matching drafter weights), modality-isolated speculative replay for screenshot/video/audio lanes before reusing text-only MTP wins, Qwen3.6 `--language-model-only` split-lane guidance for text-only servers, multimodal processor-cache hints, per-request media UUID, optional `cache_salt`, optional OpenAI-compatible `prompt_cache_key` / `prompt_cache_retention` guidance through `PalLLM:Inference:PromptCacheKey` and `PromptCacheRetention`, optional vLLM `priority` / `PalLLM:Inference:RequestPriority` guidance, optional OpenAI-compatible `service_tier` / `PalLLM:Inference:ServiceTier` guidance for priority/flex/scale routing canaries, optional OpenAI-compatible `max_completion_tokens` / `PalLLM:Inference:TokenBudgetField` guidance for reasoning-model token-budget canaries, optional OpenAI-compatible `frequency_penalty` / `PalLLM:Inference:FrequencyPenalty` guidance for repetition-control canaries, optional local-runtime `top_k` / `min_p` / `repetition_penalty` guidance through `PalLLM:Inference:TopK`, `MinP`, and `RepetitionPenalty` for sampler canaries, optional OpenAI-compatible `parallel_tool_calls` / `PalLLM:Inference:ParallelToolCalls` guidance for strict action/directive canaries, optional OpenAI-compatible `stop` / `PalLLM:Inference:StopSequences[]` guidance for delimiter latency canaries, optional prompt-level `InferencePrompt.ResponseFormat` / `response_format` guidance for strict JSON-schema text canaries with schema-digest/request-shape portability receipts, optional prompt-level `InferencePrompt.StructuredOutputs` / `structured_outputs` guidance for vLLM-specific choice/regex/JSON/grammar/structural-tag canaries, optional prompt-level `InferencePrompt.Prediction` / `prediction` guidance for stable predicted-output proof/docs canaries, optional prompt-level `InferencePrompt.Logprobs` / `TopLogprobs` guidance plus returned choice-level `logprobs` receipts for confidence/evaluator canaries, admission controls, media-fetch, `VLLM_MAX_N_SEQUENCES`, SGLang request-admission/security controls, cache-salt security controls, sleep/wake admin-endpoint security controls, local LoRA adapter security controls, precomputed-embedding security controls, `PromotionReceipts[]` for provenance, route replay, media-admission, redistribution, and fallback evidence, `MetricReceipts[]` for concrete PalLLM/upstream observability evidence, and `VerificationChecks[]` for promotion proof before a lane becomes trusted, including sparse-MoE DBO baseline/threshold/topology/queue/fallback proof, KV-cache dtype proof, SGLang cache-hit / HiCache / attention-backend / FP4-FP8 KV / EAGLE-3-adaptive-SpecV2 / TTFT / ITL metrics proof, TensorRT-LLM `kvCacheStats`, inflight-batching, speculation, and malformed-media proof, transformers serve p50/p95 and short-request-starvation proof, OpenVINO target-device warm p50/p95 and fallback-activation proof, Foundry Local warm p50/p95 and fallback-activation proof, service-tier proof for accepted request shape, queue/TTFT evidence, p95 latency, cost posture, and fallback counters, prompt-cache key/retention proof for accepted request shape, cached-token receipts, p95 latency, and fallback counters, token-budget field proof for accepted request shape, visible/reasoning token accounting, p95 latency, and fallback counters, sampler proof for style/loop deltas without parser, p95 latency, or fallback regression, structured-output proof for schema name/digest, request shape, grammar/backend id, parse/schema validation, token usage, p95 latency, changed-schema canary, and fallback counters, predicted-output proof for accepted/rejected prediction-token receipts when exposed, p95 latency, and fallback counters, logprob confidence proof for returned choice-level receipts, response-size, p95 latency, and fallback counters, and no-spec strict-route behavior, accepted/proposed token ratio, TTFT/ITL evidence, no-spec proof for strict JSON/tool-call routes, Qwen3.6 prefix-cache-off MTP-1 comparison against the normal prefix-cache lane, and a Qwen3.6 long-context warning that keeps ordinary companion turns short while reserving 128K+ contexts for proof/docs/review lanes
- `ConfiguredModels[].Capability.ServingProfile` also documents optional OpenAI-compatible `verbosity` / `PalLLM:Inference:Verbosity` canaries for concise player turns or expanded proof/review lanes, plus hosted-lane `safety_identifier` / `PalLLM:Inference:SafetyIdentifier` guidance that only permits a stable pseudonymous hash and excludes player names, save paths, account ids, emails, and secrets from requests and bundles.
- `ConfiguredModels[].Capability.ServingProfile` also documents optional outbound request-correlation canaries through `PalLLM:Inference:ClientRequestIdHeader`, limited to `x-client-request-id` or `x-request-id` with bounded visible-ASCII PalLLM request ids for support traces and no metric-label use.
- `ConfiguredModels[].Capability.ServingProfile` also keeps newer stateful/generative HTTP surfaces proof-only: vLLM-like and `transformers serve` `/v1/responses` lanes require response lifecycle events, response-id cleanup, event parsing, tool payload retention, usage receipts, p95 latency, and fallback proof before replacing `/v1/chat/completions`; vLLM-Omni `/v1/videos` and `/v1/videos/sync` are offline diffusion-job proof surfaces that require async job lifecycle, output cleanup, cancellation, prompt-publication hygiene, and no-interference evidence before appearing in release proof.
- `ConfiguredModels[].Capability.ServingProfile` also emits vLLM scheduler guardrails for low-latency player lanes: `--max-num-batched-tokens`, `--max-num-seqs`, `--max-num-partial-prefills`, `--max-long-partial-prefills`, `--long-prefill-token-threshold`, optional sparse-MoE DBO `--enable-dbo` threshold proof after scheduler-cap replay, and optional `--scheduling-policy priority` / `RequestPriority` must be replay-proven with a short companion turn queued beside a long proof/docs prompt before one server is shared across both workloads. Lower vLLM priority values are more urgent; non-zero request priorities can fail on FCFS-only servers.
- `ConfiguredModels[].Capability.ServingProfile` also emits hosted request-metadata guardrails: `PalLLM:Inference:RequestMetadata` forwards OpenAI-compatible `metadata` only for hosted proof canaries, capped at 16 bounded labels. Ordinary companion chat leaves it empty; promotion requires accepted request shape plus proof that labels contain no prompt text, player identity, save paths, secrets, raw game state, or high-cardinality metric values.
- `ConfiguredModels[].Capability.ServingProfile` also emits multimodal processor guardrails: `PalLLM:Inference:MultimodalProcessor`, prompt-level `InferencePrompt.MultimodalProcessor`, and `PalLLM:Vision:MultimodalProcessor` can forward vLLM-style `mm_processor_kwargs` (`min_pixels`, `max_pixels`, `max_soft_tokens`, `fps`) only for route-owned multimodal `UserContent` or vision requests. Ordinary text chat leaves the field absent; promotion requires accepted request shape, processor token/pixel receipts where exposed, p95 TTFT/latency, VRAM or queue-pressure evidence, parse stability, and fallback counters.
- `ConfiguredModels[].Capability.ServingProfile` also emits llama.cpp prompt-cache canary guardrails: `PalLLM:Inference:LlamaCppCachePrompt`, `LlamaCppSlotId`, and `LlamaCppCacheReuseTokens` forward `cache_prompt`, `id_slot`, and `n_cache_reuse` only when explicitly configured or supplied by a prompt-level proof lane. Promotion requires accepted request shape, same-prefix and changed-prefix replay, slot id, cache metrics, cache RAM pressure, second-turn TTFT, and fallback counters.
- `ConfiguredModels[].Capability.ServingProfile` also emits vLLM disaggregated prefill/decode guardrails for dual-GPU or workstation tail-latency experiments: P/D topologies using NixlConnector, P2pNcclConnector, MooncakeConnector, MoRIIOConnector, or MultiConnector must stay proof-only until a monolithic-vs-split replay records prefill/decode endpoint ids, router/proxy config, redacted `kv_transfer_config`, p95 TTFT, p95 ITL, p95 E2E latency, KV-transfer latency/failure evidence, queue pressure, worker-stop rollback, decode-only fallback, and PalLLM fallback counters. MoRIIO single-node proof also records `VLLM_MORIIO_CONNECTOR_READ_MODE`, proxy/http/handshake/notify ports, remote-KV wait, and prefix-cache-disabled versus normal-prefix baselines. The profile explicitly treats P/D as tail-latency isolation, not throughput proof.
- `ConfiguredModels[].Capability.ServingProfile` also emits replayable cache-evidence guardrails for vLLM and SGLang lanes: vLLM `--kv-cache-metrics-sample` is a qualification-only hint for KV-block residency / idle-before-evict / reuse-gap evidence, while SGLang request dump/replay or crash-dump replay receipts must stay local and only sanitized replay templates or hashes may be archived.
- `ConfiguredModels[].Capability.ServingProfile` also emits vLLM KV-event guardrails for cache/router proof: enable `KVEventsConfig` only during qualification on a loopback/admin ZMQ publisher, reduce `BlockStored`, `BlockRemoved`, and `AllBlocksCleared` batches to counts, block hashes, block sizes, group metadata, replay gaps, and extra-key classes, and keep raw `token_ids`, `extra_keys`, cache salts, media ids, LoRA names, and prompt-embedding hashes out of support/public bundles.
- `ConfiguredModels[].Capability.ServingProfile` also emits Mooncake Store guardrails for vLLM distributed KV-cache experiments: `MOONCAKE_CONFIG_PATH`, redacted `MooncakeStoreConnector` / `MultiConnector` config shape, store/client health, cache-hit rate, cold/warm TTFT and E2E latency, companion p95, parser parity, fallback counters, and rollback when the store or a client is stopped must be captured before any shared KV store is used for live companion traffic.
- `ConfiguredModels[].Capability.ServingProfile` also emits external KV cache process-boundary guardrails for PegaFlow-style and FlexKV vLLM connector experiments: `PegaKVConnector`, `FlexKVConnectorV1`, or another `kv_connector_module_path` daemon must stay proof-only until local-prefix-cache versus daemon-backed replay records daemon health, endpoint binding, pool/SSD/RDMA budget, namespace/model identity, cache-hit rate, scheduler-side async transfer counts, load/store failures, worker-restart warm reuse, daemon-stop rollback, local-prefix-cache rollback, cold/warm TTFT/E2E, and PalLLM fallback counters. Raw KV blocks, SSD cache paths, namespace strings, endpoint details, and player text stay out of support/public bundles.
- `ConfiguredModels[].Capability.ServingProfile` also emits SGLang HiCache guardrails for hierarchical KV-cache experiments: `--enable-hierarchical-cache`, page size, host-cache ratio or size, I/O backend, write policy, optional storage backend, prefetch policy, `--enable-cache-report`, backend namespace hash, cold/warm route replay, cache-hit rate, TTFT/E2E deltas, queue depth, parser stability, storage attach/detach or backend-stop rollback, and PalLLM fallback counters must be captured before any host/storage offload lane handles live companion traffic. Raw KV pages, backend paths, and storage namespaces stay out of support/public bundles.
- `ConfiguredModels[].Capability.ServingProfile` also emits SGLang attention, precision, and speculation guardrails: explicit `--attention-backend`, `--prefill-attention-backend`, `--decode-attention-backend`, `--mm-attention-backend`, `--kv-cache-dtype fp8_e4m3|fp8_e5m2|fp4_e2m1`, EAGLE-3, adaptive speculation, and `SGLANG_ENABLE_SPEC_V2` are proof-only until auto-selection baselines, support-matrix compatibility, page size, GPU/CUDA/PyTorch receipts, draft-model revision/hash or NGRAM config, topk/num-steps/draft-token caps, acceptance rate, OOM headroom, strict-route parser stability, route p95 latency, and fallback counters are captured. SpecV2 proof must pin topk=1.
- `ConfiguredModels[].Capability.ServingProfile` also emits reproducible vLLM sampling and tool-call guardrails: `--generation-config vllm` should be recorded when PalLLM replay settings must not inherit model-repo generation defaults, `PalLLM:Inference:FrequencyPenalty` must prove lower repeated-phrase rate without worse latency, token count, or fallback pressure, `PalLLM:Inference:TopK` / `MinP` / `RepetitionPenalty` must prove accepted local-sampler request shape plus style or loop improvement without parser, p95 latency, or fallback regression, `PalLLM:Inference:ParallelToolCalls=false` must prove zero-or-one directive/action tool call before any multi-call fan-out experiment is promoted, and prompt-level `InferencePrompt.Tools` / `ToolChoice` canaries must prove accepted `tools` / `tool_choice` request shape plus archived `tool_calls` receipts before a route trusts tool-call-only output.
- Baseline sampler config now fails fast at startup: `PalLLM:Inference:Temperature` and `PalLLM:Vision:Temperature` must be finite and within `0` to `2`, `PalLLM:Inference:TopP` within `0` to `1`, and `PalLLM:Inference:PresencePenalty` within `-2` to `2`.
- `ConfiguredModels[].Capability.ServingProfile` also emits token-budget field guardrails: keep `PalLLM:Inference:TokenBudgetField=max_tokens` unless a reasoning lane proves it needs `max_completion_tokens`; replay the same route with both fields and record accepted request shape, token usage, p95 latency, and fallback counters before promotion.
- `ConfiguredModels[].Capability.ServingProfile` also emits thinking-token-budget guardrails: `PalLLM:Inference:ThinkingTokenBudget` forwards vLLM `thinking_token_budget` only on endpoint-proven reasoning-parser lanes, and promotion requires accepted request shape, visible/reasoning token usage, p95 latency, and fallback counters.
- `ConfiguredModels[].Capability.ServingProfile` also emits stop-delimiter guardrails: `PalLLM:Inference:StopSequences[]` must prove accepted request shape, lower generated-token count, no clipped companion text, and stable fallback counters before delimiters become a player-facing latency optimization.
- `ConfiguredModels[].Capability.ServingProfile` also emits structured-output guardrails: prompt-level `InferencePrompt.ResponseFormat` can forward `response_format: json_schema` on exact strict text canaries, prompt-level `InferencePrompt.StructuredOutputs` can forward vLLM-specific `structured_outputs` constraints, and prompt-level `InferencePrompt.Tools` / `ToolChoice` can forward `tools` / `tool_choice` on strict action/directive canaries. Ordinary companion chat omits those fields, and promotion requires schema name/digest, PalLLM route class, served model id, provider request shape, grammar/backend id, parse stability, app-side schema validation, token usage, p95 latency, returned `tool_calls` receipts where applicable, fallback counters, a changed-schema canary, and no-spec evidence for strict JSON/tool-call routes.
- `ConfiguredModels[].Capability.ServingProfile` also emits predicted-output guardrails: prompt-level `InferencePrompt.Prediction` can forward `prediction` on exact proof/docs canaries. Ordinary companion chat omits the field, and promotion requires accepted request shape, accepted/rejected prediction-token receipts when exposed, p95 latency, and fallback counters.
- `ConfiguredModels[].Capability.ServingProfile` also emits logprob confidence guardrails: prompt-level `InferencePrompt.Logprobs` / `TopLogprobs` can forward `logprobs` / `top_logprobs` on exact validator/evaluator canaries. Ordinary companion chat omits the fields, and promotion requires accepted request shape, returned choice-level logprob receipts, response-size, p95 latency, and fallback counters.
- `ConfiguredModels[].Capability.ServingProfile` also emits audio-output guardrails: prompt-level `InferencePrompt.Modalities` / `Audio` can forward `modalities` / `audio` on isolated voice canaries. Ordinary companion chat omits the fields, and promotion requires accepted request shape, returned `message.audio` receipts on `InferenceResult.AudioJson`, a usable text mirror, response-size, p95 latency, and fallback counters.
- `ConfiguredModels[].Capability.ServingProfile` also emits multimodal-input guardrails: prompt-level `InferencePrompt.UserContent` can replace the user message `content` with a route-owned content-part array for exact image/video/audio canaries. With `PalLLM:Inference:UseMediaCacheIds=true`, local base64 media parts receive stable `palllm-{image|video|audio}-sha256-*` UUIDs for vLLM-compatible cache reuse. Ordinary companion chat stays as a string message, and promotion requires accepted request shape, media byte caps/admission receipts, parse stability, p95 latency, and fallback counters.
- `ConfiguredModels[].Capability.ServingProfile` also distinguishes Gemma 3n audio-in / edge-memory lanes, Gemma 4 native audio-in lanes, Qwen3.6 hybrid-GDN state proof lanes, Qwen3.6 native/extended/hosted/GGUF context identities, and Qwen Omni audio-out lanes: Gemma audio hints require mono 16 kHz audio normalization, bounded clips, family-specific budget receipts (`25` audio tokens/sec for Gemma 4, `6.25` for Gemma 3n), deterministic text fallback, and cascaded-ASR comparison before player-speech promotion; Qwen3.6 GDN/context hints require runtime version, served model id, scheduler strategy, page size, attention backend, context cap, extension flags, context/state-memory, route token budget, TTFT/ITL, parse-success, and fallback receipts before changing serving defaults; Qwen Omni hints require a local vLLM-Omni `--omni` proof lane, `modalities=["text","audio"]` audio replies with a text mirror, bounded media admission, an `async_chunk`-disabled deploy receipt before `/v1/realtime` voice promotion, `/v1/video/chat/stream` streaming-video receipts with frame cadence, optional PCM16 audio chunk policy, reconnect/stall behavior, still-image or world-state fallback proof, and no promotion of research-only Qwen3.5 references unless an actual local model artifact or provider endpoint is configured
- `ConfiguredModels[].Capability.ServingProfile` also emits llama.cpp / Ollama / LM Studio / GGUF-specific proof hints when the model id points at a GGUF lane: loopback `--host`, measured `-c` / `-np` / `-b` / `-ub` / `-ngl`, prompt-cache sizing with `--cache-prompt`, `--cache-reuse`, `-sps`, and `-cram`, proof-gated llama.cpp KV compression via `-ctk` / `-ctv`, idle sleep via `--sleep-idle-seconds`, native llama.cpp speculation (`--spec-type ngram-simple`, `ngram-mod`, or proof-only `draft-mtp` with current `--spec-draft-*` / `--spec-ngram-mod-*` flags) instead of vLLM `--speculative-config`, `pal connect llamacpp` setup and config wiring, Ollama context / keep-alive / Flash Attention / KV-cache / concurrency guidance via `OLLAMA_CONTEXT_LENGTH`, `OLLAMA_KEEP_ALIVE`, `OLLAMA_FLASH_ATTENTION`, `OLLAMA_KV_CACHE_TYPE`, `OLLAMA_NUM_PARALLEL`, `OLLAMA_MAX_LOADED_MODELS`, and `OLLAMA_MAX_QUEUE`, LM Studio `lms server start`, `lms load`, `/v1/models`, `ttl`, structured-output/tool proof, context/GPU-offload/auto-evict receipts, loopback/API-key/admin-only controls, `OLLAMA_NO_CLOUD=1` air-gap proof, and verification receipts for `/health`, `/v1/models`, `/metrics`, prompt-cache reuse, slot selection, active KV memory, `ollama ps` PROCESSOR/CONTEXT, cold-vs-warm `load_duration`, usage timing fields, accepted/generated token statistics, exact parse success, and fallback behavior
- `ConfiguredModels[].Capability.ServingProfile.CacheHints[]`, `MetricReceipts[]`, and `VerificationChecks[]` now distinguish text prefix/KV cache, deterministic prefix-cache hashing, optional trust-domain cache salts, FP8/NVFP4 KV-cache compression proof versus `auto` KV cache, sampled vLLM KV-block residency evidence, redacted vLLM KV-event proof, disaggregated prefill/decode P/D topology proof, Mooncake Store distributed KV-cache proof, PegaFlow-style external KV cache process-boundary proof, SGLang sanitized replay receipts, SGLang HiCache hierarchical KV proof, SGLang attention-backend/KV-dtype/draft-backend cache identity, adapter-specific prefix-cache identity for base-model-plus-LoRA lanes, sticky or KV cache-aware routing for multi-replica pools, SGLang radix-cache behavior and `cache_hit_rate` metrics, media UUID reuse, multimodal processor cache, optional vLLM/LMCache encoder cache, vLLM sleep/wake cold-cache boundaries, trusted precomputed `image_embeds` / `audio_embeds` / video embedding lanes, and advanced KV-transfer/offload topologies so repeated Palworld screenshot, video, or audio proof loops have separate cold/warm evidence instead of a generic "cache worked" claim
- `ConfiguredModels[].Capability.ServingProfile` also keeps promotion evidence route-labeled: companion chat, vision describe, world-state extraction, screenshot proof loops, audio/ASR, and long proof/docs traffic each need their own replay receipts before cache, scheduler, speculation, or routing settings can be promoted across lanes. vLLM-like lanes additionally call out queue/preemption pressure receipts so a mixed short-turn plus long-proof replay cannot hide KV-pressure recomputation behind an average model score.
- `ConfiguredModels[].Capability.ServingProfile` also emits primary-source capability and model-artifact provenance receipts before promotion or redistribution: model-card/vendor-doc revision, served model id from `/v1/models` or the provider catalog, runtime version, launch flags, positive canaries for claimed text/image/video/audio/tool/speculation support, negative canaries for unsupported modalities, source URL or local path, immutable revision/commit or SHA-256, model-card license metadata, base-model/adapter relation, weight format, safetensors/pickle and `trust_remote_code` status, runtime/tokenizer revisions, and whether redistribution is allowed.
- `ConfiguredModels[].Capability.ServingProfile.PromotionReceipts[]` separates non-metric promotion evidence from metric names: route-labeled replay, runtime capability handshakes, model-artifact provenance, package/redistribution decisions, GGUF prompt/state-cache canaries, Qwen3.6 context receipts, Gemma audio-budget receipts, vLLM scheduler/cache proof, disaggregated prefill/decode topology proof, redacted vLLM KV-event proof, external KV cache process-boundary proof, SGLang sanitized replay proof, SGLang attention/precision proof, SGLang speculative proof, SGLang HiCache hierarchical KV proof, transformers serve / Foundry Local / OpenVINO / TensorRT-LLM readiness proof, speculation A/B proof, multimodal media-admission proof, Qwen Omni streaming-video fallback proof, and audio/realtime fallback proof where those lane capabilities apply.
- `ConfiguredModels[].Authority` - what each lane is allowed to do by default (`MayDraftChanges`, `MayBePrimaryReviewer`, low-risk tool loops, merge recommendations)
- `Recipes[]` - staged collaboration patterns for bridge work, HUD review, screenshot loops, docs sync, and release hardening
- `RoutingPolicies[]` - task-class and risk-aware default routes such as `low-risk-fast-lane`, `high-risk-deliberate-bookends`, and `tool-heavy-guarded`
- `QualificationSuite` - deterministic promotion gates for fresh quants or fresh local model revisions before they become trusted defaults
- `HardwarePlaybook[]` - per-tier run-mode, quant, and context guidance from CPU-only through workstation-class setups
- `DeploymentNotes[]` - practical operating notes for the inferred hardware class
- `SelfHealingIdeas[]` - shipped self-healing loops for model promotion, doc drift, bridge drift, and shadow repair

### POST /api/inference/collaboration/plan -> ModelCollaborationDecision

Task-specific execution planner layered on top of the collaboration snapshot.
Use this when you already know the concrete job and want the runtime to return
an exact fast-lane vs dense-lane operating plan instead of the generic hardware
playbook.

Request body (`ModelCollaborationDecisionRequest`):

```jsonc
{
  "Task": "Audit the Palworld HUD seam after a bridge_boot compatibility change and doc update",
  "TaskClass": "bridge",                        // optional prose classifier
  "RiskLevel": "high",                         // optional: low | medium | high
  "ToolHeavy": true,
  "FrontendOrVisual": true,
  "LargeContext": true,
  "AssetOrMedia": false,
  "NeedsVision": true,
  "ReleaseGate": true,
  "HeroAsset": false,
  "VramGb": 48,
  "RamGb": 128,
  "UnifiedMemoryGb": null,
  "CpuOnly": false,
  "PreferParallel": true,
  "AvailableQuants": "35B: UD-Q4_K_M, 27B: Q5_K_M", // optional freeform note
  "ContextBudget": "35B at 32K, 27B at 64K"         // optional freeform note
}
```

Validation (`422` on failure):

- `Task` is required and must be non-blank.
- `RiskLevel`, when supplied, must be `low`, `medium`, or `high`.
- `VramGb`, `RamGb`, and `UnifiedMemoryGb` must be `>= 0` when supplied.

Response highlights:

- `SelectedPolicyId` - chosen routing policy such as `high-risk-deliberate-bookends`
- `SelectedRecipeId` - concrete staged recipe such as `dense-plan-fast-execute-dense-audit`
- `FastLaneModel` / `DeliberateLaneModel` - chosen lane models from the configured collaboration mesh
- `FastLaneRole` / `DeliberateLaneRole` - human-readable lane assignments for this exact task
- `RunMode` - `parallel`, `sequential`, or `one_model_only`
- `ThinkingMode` / `PreserveThinking` - lane-specific thinking hints for the task
- `ContextBudget` / `QuantRecommendation` - hardware-aware operating hints
- `Validators[]` - deterministic checks the task must clear before trust
- `PromotionCriteria[]` - qualification gates relevant to the task class
- `Fallback` - what to do when one lane is unavailable
- `Steps[]` - the ordered collaboration steps from the chosen routing policy

### POST /api/inference/warmup -> InferenceWarmupSnapshot

Runs a deliberately tiny warmup request against the currently active inference
lane. This is useful after startup, after a tier graduation, or before a live
session when the operator wants to pay model-load latency ahead of the first
real player turn.

Response highlights:

- `Enabled` - whether inference warmup is enabled in config
- `Status` / `StatusMessage` - current warmup posture and latest outcome
- `ActiveModel` / `ActiveTierId` - the model lane the warmup targeted
- `ResidencyProvider` / `ResidencyTtlSeconds` - the resolved provider-specific
  residency posture for the active lane (`none`, `lmstudio`, or back-compat `ollama` for out-of-band Ollama users) plus
  the configured TTL budget in seconds
- `LastSeenAvailableModels[]` - the last discovery snapshot from the tier
  probe/orchestrator path
- `LastWarmedModel` / `LastReason` - what most recently triggered warmup
- `WarmupTransport` / `LastWarmupUsedResidencyHint` - whether the warmup went
  through generic chat-completions or a provider-native preload path, and
  whether a provider-specific residency hint was actually emitted
- `LastAttemptAtUtc`, `LastSuccessAtUtc`, `LastLiveInferenceAtUtc`,
  `LastLiveInferenceModel`, `LastFailureAtUtc` - timing markers plus the
  latest successful real chat turn that exercised the active lane
- `AttemptCount`, `SuccessCount`, `FailureCount`, `LastLatencyMs` - bounded
  observability for warmup effectiveness
- repeated calls dedupe against a recent successful warmup for the same model,
  so callers can safely use this as a low-cost "prime now" endpoint
- periodic keepalive warmups also self-suppress when a recent successful live
  inference turn already touched the same active model inside the keepalive
  window, which avoids duplicate POST work on busy sessions
- llama-server keeps the loaded model resident for the lifetime of the server process; LM Studio supports `ttl` per request. The OpenAI-compatible `/api/chat` preload path is also recognised for any out-of-band Ollama setup an operator might still run, but Pass 339 dropped first-party Ollama support
  path and `keep_alive`; LM Studio-compatible chat-completions requests can
  carry `ttl` on both live turns and generic warmups
- `LastReason` is `manual_api` for explicit HTTP-triggered warmups

### Inspection surface revalidation

Additional HTTP contract details for the read-mostly inspection surfaces:

#### Dashboard revalidation

- `GET /api/dashboard` also emits `ETag`.
- `Cache-Control: private, no-cache, must-revalidate`.
- Matching `If-None-Match` returns `304 Not Modified` with an empty body.
- `Server-Timing: dashboard;dur=...` remains present on `200 OK` responses.

#### Feature catalog revalidation

- `GET /api/features` emits `ETag`.
- `Cache-Control: private, max-age=<FeatureCatalogCacheMinutes>` (`60` minutes
  by default).
- Matching `If-None-Match` returns `304 Not Modified`.

#### Self-description revalidation

- `GET /api/describe` emits `ETag`.
- `Cache-Control: private, max-age=<SelfDescriptionCacheSeconds>` (`15`
  seconds by default).
- Matching `If-None-Match` returns `304 Not Modified`.

#### Bridge proof revalidation

- `GET /api/bridge/proof` emits `ETag`.
- `Cache-Control: private, max-age=<FeatureCatalogCacheMinutes>` (`60`
  minutes by default).
- Matching `If-None-Match` returns `304 Not Modified`.

#### Release/readiness snapshot revalidation

- `GET /api/release/readiness` emits `ETag`.
- `Cache-Control: private, max-age=<FeatureCatalogCacheMinutes>` (`60`
  minutes by default).
- Matching `If-None-Match` returns `304 Not Modified`.

#### Inference performance revalidation

- `GET /api/inference/performance` emits `ETag`.
- `Cache-Control: private, no-cache, must-revalidate`.
- Matching `If-None-Match` returns `304 Not Modified`.

#### Upstream MCP snapshot revalidation

- `GET /api/mcp/upstream` returns the ordered snapshot array from
  `McpUpstreamClientPool`.
- The route emits `ETag`.
- `Cache-Control: private, max-age=<UpstreamSnapshotCacheSeconds>` (`5`
  seconds by default).
- Failure entries expose `Connected=false`, a stable machine-friendly
  `ErrorCode`, and a sanitized operator-facing `Error` string instead of raw
  exception text.
- Each snapshot's `Tools`, `Resources`, and `Prompts` arrays are bounded by
  `PalLLM:McpClient:MaxToolsPerServer`, `MaxResourcesPerServer`,
  `MaxPromptsPerServer`, and `MaxMetadataEntryLength` so one upstream cannot
  balloon the cached response or force large-string churn in the sidecar.
- Matching `If-None-Match` returns `304 Not Modified`.

---

## Error handling

| Kind | Status | Body shape |
|---|---|---|
| Unparseable JSON / wrong Content-Type | 400 or 415 | `ProblemDetails` |
| Validator rejection (blank `UserMessage`, oversized TTS text, too many party ids, ...) | 400 | `ValidationProblemDetails` with `errors[fieldName]` arrays |
| Missing or invalid bearer credential on protected `/api/*` or `/mcp` routes | 401 | `ProblemDetails` plus `WWW-Authenticate: Bearer` |
| Disallowed browser `Origin` on `/mcp` | 403 | `ProblemDetails` |
| Schema or publication-safety error in `packs/validate` | 400 | `NarrativePackValidationResult` with `IsValid=false` |
| Oversized `/api/*` or `/mcp` request body | 413 | `ProblemDetails` (`Payload Too Large`) |
| Oversized `packs/validate` body | 413 | `ProblemDetails` (`Payload Too Large`) |
| HTTP admission-control limiter trip (`/api/chat`, `/api/vision/*`, `/api/tts/synthesize`, `/api/audio/transcribe`) | 429 | `ProblemDetails` with a saturation message; `Retry-After` is included when the active limiter can estimate it |
| Heavy HTTP lane request timeout (`/api/chat`, `/api/chat/party`, `/api/inference/warmup`, `/api/vision/*`, `/api/tts/synthesize`, `/api/audio/transcribe`) | 503 | `ProblemDetails` with a sanitized timeout message tied to the matching `PalLLM:Http:*RequestTimeoutSeconds` knob |
| Chat stream timeout after SSE starts (`/api/chat/stream`) | 200 stream with `event: error` | `ChatStreamErrorPayload` with `reason=request_timeout`; no `final` event is emitted |
| Upstream inference timeout / 5xx / malformed JSON | 200 | `ChatResponse` with `ResponsePath` in the fallback family; circuit breaker records failure internally, and `StatusMessage` stays sanitized |
| Upstream vision timeout / malformed JSON | 200 | `VisionDescribeResponse` with `Success=false` and a sanitized `StatusMessage` |
| ASR disabled, server down, malformed transcript JSON, or oversized audio / transcript body | 200 | `AudioTranscribeResponse` with `Success=false` and a sanitized `StatusMessage` |
| TTS disabled, server down, or oversized audio body | 200 | `TtsSynthesizeResponse` with `Success=false` and a sanitized `StatusMessage` |
| Session save or reload local I/O / JSON failure | 200 | `SessionPersistenceResult` with `Success=false`, a stable `StatusMessage`, and blank `FilePath`; raw exception text or local paths are not exposed |
| Missing relationship (`/api/relationships/{id}`) | 404 | empty |
| Ready but degraded | 200 (from `/health/ready`) | `HealthCheckResult` with `status=Degraded` |
| Conditional cache revalidation match (`/api/dashboard`, `/api/features`, `/api/describe`, `/api/bridge/proof`, `/api/inference/performance`, `/api/release/readiness`, `/api/mcp/upstream`) | 304 | empty body; current `ETag` header is still emitted |

PalLLM deliberately avoids raising HTTP 5xx for upstream model faults - the deterministic fallback director is always available and the caller should get a valid reply with a diagnostic status field.

---

## Correlation and replay

Every chat turn carries a short `RequestId`. Callers can supply their own (preserved verbatim) or let the runtime generate one (`chat-` + 12 hex chars). The id propagates to:

- `ChatResponse.RequestId`
- The outbox envelope payload (`OutboxEnvelope.Payload.RequestId`)
- Any subsequent log line that mentions the turn

Use it to pair an in-game render with a server-side log when triaging. The `SidecarEndpointTests.ChatEndpoint_DeliveryReplayScenarioSet_ProducesRepresentativeOutboxContracts` fixture demonstrates the full reply-to-outbox replay.
