# PalLLM Advisor & Builder Catalog

Last audited: `2026-05-21`

One-page reference for every **advisor**, **builder**, **validator**,
**feeder**, and **tracker** in the codebase. Complements
[`CODE_MAP.md`](CODE_MAP.md) (project layout) and
[`HARVEST.md`](HARVEST.md) (lift-it-into-your-project recipes).

If you're harvesting a capability, this table tells you at a glance:
what it is, what file it lives in, what dependencies it has, and
whether it's pure (safe to lift as-is) or stateful (needs state
transfer).

## Legend

| Symbol | Meaning |
|---|---|
| **Pure** | Static class with pure function(s). Zero state. Lift as one file. |
| **Stateful** | Holds bounded in-memory state. Harvest the file + adjust persistence. |
| **Cached** | Pure function with TTL-cache wrapper (extension of the Pure pattern). |

See [`CONVENTIONS.md`](CONVENTIONS.md) for the 4-pattern catalogue
(advisor / builder / validator / feeder / tracker).

## Advisors — give structured advice from snapshot inputs

| Name | File | Kind | Public surface | Surfaced as |
|---|---|---|---|---|
| `ChatTaskKindInferer` | `src/PalLLM.Domain/Inference/ChatTaskKindInferer.cs` | Pure | `DuoTaskKind Infer(message, taskTag?)` | `POST /api/chat/plan` (inside), `ChatResponse.InferredTaskKind` |
| `DuoOrchestratorPlanner` | `src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs` | Pure | `DuoPlan Plan(DuoPlanRequest)` | `POST /api/duo/plan` + `pal_duo_plan` |
| `ChatDispatchPlanner` | `src/PalLLM.Domain/Inference/ChatDispatchPlanner.cs` | Pure | `ChatDispatchDecision Decide(pattern, coverage)` | `ChatResponse.DispatchedRoleChain` + `DispatchMode` |
| `WhyEngine` | `src/PalLLM.Domain/Runtime/WhyEngine.cs` | Pure | `WhyAnswer Answer(question, health, metrics, ...)` | `POST /api/why` + `pal_why` |
| `DirectiveIntentTranslator` | `src/PalLLM.Domain/Runtime/DirectiveIntentTranslator.cs` | Pure | `DirectivePlan Translate(utterance, allowed, pal?)` | `POST /api/directives/plan` + `pal_directives_plan` |
| `WorldNarrationAdvisor` | `src/PalLLM.Domain/Runtime/WorldNarrationAdvisor.cs` | Pure | `NarrationCue Advise(snapshot, lastNarrationUtc?)` | `GET /api/narration/cue` + `pal_narration_cue` |
| `MoodWeatherAdvisor` | `src/PalLLM.Domain/Runtime/MoodWeatherAdvisor.cs` | Pure | `MoodWeather Forecast(relationship, snapshot?)` | `GET /api/characters/{id}/mood` + `pal_mood_weather` |
| `GracefulDegradationAdvisor` | `src/PalLLM.Domain/Runtime/GracefulDegradationAdvisor.cs` | Pure | `DegradationAdvisory Recommend(profile, options)` | `GET /api/degradation/advisory` + `pal_degradation_advisory` |
| `DisagreementDetector` | `src/PalLLM.Domain/Runtime/DisagreementDetector.cs` | Pure | `DisagreementAnalysis Compare(workerOutput, judgeOutput)` | `POST /api/disagreement/check` + `pal_disagreement_check` |
| `HardwareProfiler` | `src/PalLLM.Domain/Inference/HardwareProfiler.cs` | **Cached** | `HardwareProfile Capture(forceTier?)` / `CaptureCached(forceTier?, ttl?)` | `GET /api/hardware` + `pal_hardware_profile` |
| `ChatPlanAdvisor` | `src/PalLLM.Sidecar/ChatPlanAdvisor.cs` | Pure | `ChatPlanAdvice Advise(request, planner, registry?)` | `POST /api/chat/plan` + `pal_chat_plan` |
| `OperatorHealthScorer` | `src/PalLLM.Domain/Runtime/OperatorHealthScorer.cs` | Pure | `OperatorHealthScore Score(health)` | embedded in `/api/describe` |
| `SpeciesPersonalityResolver` | `src/PalLLM.Domain/Packs/SpeciesPersonalityResolver.cs` | Pure | `SpeciesPersonalityResolution Resolve(species, defaultBySpecies, fallbackPackId?)` | `GET /api/packs/resolve` + `pal_personality_for_species` |

## Builders — compose inputs into immutable snapshot records

| Name | File | Kind | Public surface | Surfaced as |
|---|---|---|---|---|
| `ProofPacketBuilder` | `src/PalLLM.Domain/Runtime/ProofPacketBuilder.cs` | Pure | `ProofPacket Build(subsystem, decision, ...)` | `POST /api/proof/packet` + `pal_proof_packet` |
| `PromotionSuggestionBuilder` | `src/PalLLM.Domain/Runtime/PromotionSuggestionBuilder.cs` | Pure | `PromotionSuggestionSet Build(summary)` / `BuildForTask(task, at)` | `GET /api/promotion/suggestions` + `pal_promotion_suggestions` |
| `PromotionApplyPreviewBuilder` | `src/PalLLM.Domain/Runtime/PromotionApplyPreviewBuilder.cs` | Pure | `PromotionApplyPreview Build(suggestion)` | `POST /api/promotion/apply/preview` + `pal_promotion_apply_preview` |
| `PromotionApplier` | `src/PalLLM.Domain/Runtime/PromotionApplier.cs` | Pure | `PromotionApplyResult Apply(preview, options)` | `POST /api/promotion/apply` + `pal_promotion_apply` |
| `PrivacyPostureBuilder` | `src/PalLLM.Domain/Runtime/PrivacyPostureBuilder.cs` | **Cached** | `PrivacyPosture Capture(options)` / `CaptureCached(options, ttl?)` | `GET /api/privacy/posture` + `pal_privacy_posture` |
| `ResourceBudgetPostureBuilder` | `src/PalLLM.Domain/Runtime/ResourceBudgetPostureBuilder.cs` | **Cached** | `ResourceBudgetPosture Capture(options, metrics)` / `CaptureCached(options, metrics, ttl?)` | `GET /api/budgets` + `pal_resource_budgets` |
| `LifetimeRelationshipAggregator` | `src/PalLLM.Domain/Runtime/LifetimeRelationshipAggregator.cs` | Pure | `LifetimeRelationshipAggregate Merge(prior, session, now?)` + `Summarise` | `GET /api/relationships/lifetime` |
| `BridgeProofBuilder` | `src/PalLLM.Sidecar/BridgeProofBuilder.cs` | Pure | `BridgeProofSnapshot Create(runtime)` | `GET /api/bridge/proof` |
| `ReleaseReadinessBuilder` | `src/PalLLM.Sidecar/ReleaseReadinessBuilder.cs` | Pure | `ReleaseReadinessSnapshot Create(...)` | `GET /api/release/readiness` |
| `ReleaseFullAuditEvidenceBuilder` | `src/PalLLM.Sidecar/ReleaseFullAuditEvidenceBuilder.cs` | Pure | `ReleaseFullAuditEvidenceSnapshot ReadLatest(options)` | inside `/api/release/readiness` |
| `ReleaseArtifactIntegrityEvidenceBuilder` | `src/PalLLM.Sidecar/ReleaseArtifactIntegrityEvidenceBuilder.cs` | Pure | `ReleaseArtifactIntegrityEvidenceSnapshot ReadLatest(...)` | inside `/api/release/readiness` |
| `QuickstartGuideBuilder` | `src/PalLLM.Sidecar/QuickstartGuideBuilder.cs` | Pure | `QuickstartGuide Build(...)` | `GET /api/quickstart` + `pal_quickstart` |
| `SelfDescriptionBuilder` | `src/PalLLM.Sidecar/SelfDescriptionBuilder.cs` | Pure | `SelfDescription Build(...)` | `GET /api/describe` + `pal_describe` |

## Validators — check inputs and return structured verdicts (never throw)

| Name | File | Kind | Returns |
|---|---|---|---|
| `NarrativePackValidator` | `src/PalLLM.Domain/Packs/NarrativePackValidator.cs` | Pure | `NarrativePackValidationResult { IsValid, Issues[] }` |
| `PersonalityPackValidator` | `src/PalLLM.Domain/Packs/PersonalityPack.cs` | Pure | `PersonalityPackValidationResult { IsValid, Checks[], Issues[], ActualContentHash }` |
| `PalApiValidation` | `src/PalLLM.Sidecar/PalApiValidation.cs` | Pure | `ValidationProblemDetails` on reject |
| `PalLlmOptionsValidator` | `src/PalLLM.Sidecar/PalLlmOptionsValidator.cs` | Pure | `ValidateOptionsResult` on startup |

## Feeders — background workers that observe and write bounded records

| Name | File | Cadence | Writes to |
|---|---|---|---|
| `PromotionLedgerFeeder` | `src/PalLLM.Sidecar/PromotionLedgerFeeder.cs` | 30s default | `PromotionLedger` in-memory + observation proofs |
| `SelfHealingWorker` | `src/PalLLM.Sidecar/SelfHealingWorker.cs` | 60s default | `Runtime/SelfHealingEvidence/*.json` |
| `SessionAutosaveWorker` | `src/PalLLM.Sidecar/SessionAutosaveWorker.cs` | 60s default | `runtime-root/session.json` |
| `InferenceWarmupWorker` | `src/PalLLM.Sidecar/InferenceWarmupWorker.cs` | on tier transition | prewarms active inference lane |
| `BridgeInboxWorker` | `src/PalLLM.Sidecar/BridgeInboxWorker.cs` | 1s poll | drains `Bridge/Inbox/*.json` |
| `McpUpstreamDiscoveryWorker` | `src/PalLLM.Sidecar/McpUpstreamDiscoveryWorker.cs` | 5 min default | `UpstreamMcpClient` registry |
| `ModelTierUpgradeWorker` | `src/PalLLM.Sidecar/ModelTierUpgradeWorker.cs` | on availability probe | `ModelTierOrchestrator` active tier |

## Trackers — stateful per-key counters / histograms

| Name | File | Scope | What it tracks |
|---|---|---|---|
| `InferencePerformanceTracker` | `src/PalLLM.Domain/Runtime/InferencePerformanceTracker.cs` | per-lane | latency p95, reliability, latest token receipts, recent-window budget |
| `RelationshipTracker` | `src/PalLLM.Domain/Runtime/RelationshipTracker.cs` | per-character | affinity, mood, last tone |
| `ChatRateLimiter` | `src/PalLLM.Domain/Runtime/ChatRateLimiter.cs` | per-character | sliding-window request count |
| `PalLlmMetrics` | `src/PalLLM.Domain/Runtime/PalLlmMetrics.cs` | global | chat totals, fallback strategy hits, tier transitions, latency histogram |
| `InferenceCircuitBreaker` | `src/PalLLM.Domain/Inference/InferenceCircuitBreaker.cs` | global | consecutive-failure count, cooldown state |
| `ThermalGate` | `src/PalLLM.Domain/Runtime/ThermalGate.cs` | global | GPU throttle signal (`nvidia-smi` + env override, bounded read) |
| `PromotionLedger` | `src/PalLLM.Domain/Runtime/PromotionLedger.cs` | per-task-class | rolling observation window, stability gate |

## Stores — in-memory collections with persistence hooks

| Name | File | Persistence |
|---|---|---|
| `ConversationMemoryStore` | `src/PalLLM.Domain/Memory/ConversationMemoryStore.cs` | written by `SessionPersistence` when `Session.Enabled=true` |
| `SessionPersistence` | `src/PalLLM.Domain/Runtime/SessionPersistence.cs` | writes memory + relationships to `runtime-root/session.json` |
| `NarrativePackService` | `src/PalLLM.Domain/Packs/NarrativePackService.cs` | disk packs under `runtime-root/Packs/` |

## Harvest status at a glance

**Ready to lift as single files** (no adapter, no Palworld, no UE4SS):

All entries in Advisors, Builders, Validators, Trackers above except:
- `BridgeProofBuilder` (references bridge-specific types)
- `ReleaseReadinessBuilder` (references package-manifest types)

**Requires the portable adapter seam** (i.e. implement `IGameAdapter`
for your target): any builder that reads from a `GameWorldSnapshot`
or `BridgeEventEnvelope`. See `HARVEST.md` for the portable-seam
recipe.

**Stateful — needs state transfer if harvested**: all Feeders,
Trackers, and Stores. The state is bounded, documented inline, and
has explicit persistence seams so transfer is mechanical.

## Adding a new advisor

1. Create `src/PalLLM.Domain/<Area>/XxxAdvisor.cs` with the pattern
   from `CONVENTIONS.md` § 1.
2. Add the HTTP route to `src/PalLLM.Sidecar/Program.cs` or the closest
   `src/PalLLM.Sidecar/RouteRegistrations/*.cs` companion.
3. Add the MCP tool to `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs`.
4. Add a `FeatureDescriptor` entry to
   `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`.
5. Add a row to this file under **Advisors**.
6. Add a regression test under `tests/PalLLM.Tests/` named `XxxAdvisorTests.cs` (mirror the closest sibling, e.g. `MoodWeatherAdvisorTests.cs`).
7. Update `docs/API.md` "Surface at a glance."
8. Run `scripts/run_full_audit.ps1` to catch any drift.

If the advisor has expensive inputs and is called on a hot path,
also wrap with `XxxAdvisor.AdviseCached(...)` following the pattern
in `HardwareProfiler.CaptureCached` + `PrivacyPostureBuilder.CaptureCached`
+ `ResourceBudgetPostureBuilder.CaptureCached`. See
[`DESIGN_PRINCIPLES.md`](DESIGN_PRINCIPLES.md) § 8.
