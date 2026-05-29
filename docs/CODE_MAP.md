# PalLLM Code Map

Last audited: `2026-05-24`

"Where does X live?" � symbol-to-file navigation for coding agents and
harvesters. Maintained alongside the code; freshness gated by the
`Drift_Doc_freshness` audit step.

## Project layout at a glance

```
D:\Coding\PalLLM\
+-- src/
|   +-- PalLLM.Domain/              -> portable runtime (NO ASP.NET, NO UE4SS)
|   |   +-- Configuration/          -> PalLlmOptions + every sub-options class
|   |   +-- Inference/              -> LLM plumbing + role registry + Duo planner
|   |   +-- Integration/            -> wire contracts (ChatRequest/Response, snapshots)
|   |   +-- Memory/                 -> ConversationMemoryStore, MemoryImportance
|   |   +-- Packs/                  -> narrative + personality pack loaders
|   |   +-- Portable/               -> redistributable adapter seam (DO NOT RENAME)
|   |   +-- Runtime/                -> the workhorses: PalLlmRuntime + advisors + builders
|   +-- PalLLM.Sidecar/             -> ASP.NET Core host
|   |   +-- Program.cs              -> host builder, middleware, OpenAPI/MCP, operational endpoints
|   |   +-- Configuration/          -> service-registration extension methods
|   |   +-- RouteRegistrations/     -> extracted route-registration extension methods (57 /api/* routes) plus static assets
|   |   +-- Mcp/                    -> MCP tools + resources + prompts (38 tools)
|   |   +-- wwwroot/                -> Field Console dashboard (static HTML/JS/CSS)
|   +-- mod/ue4ss/Mods/PalLLM/      -> Lua bridge (Windows-only, Palworld-specific)
+-- tests/PalLLM.Tests/             -> NUnit, 1315 tests, one file per subsystem
+-- scripts/                        -> PowerShell: install, doctor, smoke, audit, package
+-- docs/                           -> Di�taxis-organised documentation
```

## The big hot files

| File | Lines | What lives there |
|---|---|---|
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` | `1104` | `ChatAsync` - THE hot path. Also TTS/ASR entry points, memory, vision description, relationships, and session persistence. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.Helpers.cs` | `391` | Extracted pure static helpers for endpoint timing, MIME routing, bounded directory counts, receipt text sanitizing, and sorted bridge file enumeration. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.Inference.cs` | `357` | Extracted inference partial: performance snapshots, circuit/model metadata, warmup, live-inference residency tracking, and operation receipts. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.UiProbe.cs` | `628` | Extracted `ui_probe` diagnostics partial: bounded metadata-keyed dump parse cache, dump parsing, HUD candidate ranking, UI-probe cloning, and local diagnostics retention. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.BridgeBoot.cs` | `482` | Extracted bridge-boot and native-readiness partial: heartbeat normalization, compat-signal parsing, native HUD readiness, and HUD bind recommendations. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.Bridge.cs` | `912` | Extracted bridge-drain and activity partial: inbox events, bridge proof snapshots, delivery receipts, speech playback receipts, and action feedback. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.Prompt.cs` | `312` | Extracted prompt-rendering partial: system prompt assembly, bounded text trimming, assistant-message/status formatting, and world/lore/memory context appenders. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.Snapshot.cs` | `489` | Extracted snapshot/health partial: health/dashboard assembly, world-state reads, vision state application, character lookup, and event-driven snapshot mutation. |
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.Outbox.cs` | `289` | Extracted outbox/archive partial: screenshot ingest queue, outbox listing/clearing/writing, and archive retention. |
| `src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs` | `1427` | Deterministic visual/audio cue planner � pairs with every chat reply |
| `src/PalLLM.Sidecar/Program.cs` | `336` | Host builder, middleware, static-asset manifest resolution, OpenAPI/MCP mapping, and route spine. Service registration lives in `Configuration/*.cs`; static assets and `/api` route domains live in `RouteRegistrations/*.cs`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmStaticAssetRoutes.cs` | `125` | Extracted Field Console static-asset route companion: `/`, `/app.js`, `/styles.css`, `/index.html`, `/welcome.html`, `/favicon.svg`, and `/manifest.webmanifest`, with metadata-keyed weak content-hash ETags plus `If-Modified-Since` revalidation for the physical-file fallback. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmInferenceRoutes.cs` | `99` | Extracted inference/MCP-upstream route companion: `/api/inference/*` plus `/api/mcp/upstream`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmBridgeRoutes.cs` | `32` | Extracted bridge/outbox route companion: `/api/bridge/drain`, `/api/bridge/outbox`, `/api/bridge/ui-probe`, and `/api/bridge/outbox/clear`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmConversationRoutes.cs` | `358` | Extracted conversation route companion: `/api/chat/party`, `/api/chat`, `/api/chat/stream`, and `/api/chat/plan`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmPlanningRoutes.cs` | `95` | Extracted deterministic planning/explanation route companion: `/api/directives/plan`, `/api/duo/plan`, `/api/disagreement/check`, and `/api/why`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmMediaRoutes.cs` | `92` | Extracted multimodal media route companion: `/api/vision/*`, `/api/tts/synthesize`, and `/api/audio/transcribe`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmHealthRoutes.cs` | `143` | Extracted health/manifest route companion: `/api/health`, `/api/dashboard`, `/api/features`, `/api/describe`, `/api/quickstart`, `/metrics`, `/health/live`, and `/health/ready`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmInspectionRoutes.cs` | `200` | Extracted read-only inspection/advisory route companion: self-healing, air-gap, roles, hardware, degradation, budgets, narration, lifetime relationships, character mood, and privacy posture. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmStateRoutes.cs` | `65` | Extracted memory/relationship/session state route companion: `/api/memory/recall`, `/api/relationships*`, and `/api/session/*`. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmContentWorldRoutes.cs` | `127` | Extracted content/world route companion: narrative-pack listing/resolution/reload/validation, adapter logs, world-state reads, and snapshot updates. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmPromotionRoutes.cs` | `203` | Extracted promotion-loop route companion: observation recording, summaries, suggestions, editor-ready previews, and staging-only apply. |
| `src/PalLLM.Sidecar/RouteRegistrations/PalLlmProofReadinessRoutes.cs` | `91` | Extracted proof/readiness route companion: proof packets, release-readiness snapshots, and bridge-proof snapshots. |

If you're here to find the chat path: start at `PalLlmRuntime.ChatAsync`
(`PalLlmRuntime.cs` around line 476). Upstream: `PalLlmConversationRoutes`
route `POST /api/chat`. Downstream: `ChatResponse` contract in
`src/PalLLM.Domain/Integration/Contracts.cs`.

## "Where does X live?" � by concept

### Chat pipeline
- **Chat request/response contracts** -> `src/PalLLM.Domain/Integration/Contracts.cs`
- **Single-turn chat orchestrator** -> `PalLlmRuntime.ChatAsync` (direct callers trim user text at `ChatRequest.UserMessageMaxLength`)
- **Fan-out across characters (party chat)** -> `RouteRegistrations/PalLlmConversationRoutes.cs` route `POST /api/chat/party` + `PalApiValidation.ValidatePartyChatRequest`
- **SSE streaming variant** -> `RouteRegistrations/PalLlmConversationRoutes.cs` route `POST /api/chat/stream` + `ChatStreamWriter.cs`
- **Task-kind inference from utterance** -> `Inference/ChatTaskKindInferer.cs`
- **Cooperation-pattern planner** -> `Inference/DuoOrchestratorPlanner.cs`
- **Concrete role-chain dispatch advisory** -> `Inference/ChatDispatchPlanner.cs`
- **Per-character rate limiter** -> `Runtime/ChatRateLimiter.cs`

### Fallback (the zero-inference reply path)
- **19 strategy engine** -> `Runtime/FallbackBehaviorEngine.cs`
- **Strategy context + inputs** -> `Runtime/FallbackBehaviorContext.cs`
- **Strategy-to-response models** -> `Runtime/FallbackBehaviorModels.cs`
- **Last-resort canned reply** -> `Runtime/EmergencyFallback.cs`
- **Stable hash for dedup** -> `Runtime/FallbackHash.cs`

### Memory + relationships
- **Deterministic embedding memory** -> `Memory/ConversationMemoryStore.cs`
- **Salience scorer** -> `Memory/MemoryImportance.cs`
- **Reflection (summary memories)** -> `Memory/ReflectionService.cs`
- **Per-character affinity/mood/tone** -> `Runtime/RelationshipTracker.cs`
- **Lifetime (cross-session) aggregate** -> `Runtime/LifetimeRelationshipAggregator.cs`
- **Bounded persisted JSON reader (lifetime/ui-probe/release evidence)** -> `Runtime/BoundedJsonFileReader.cs`
- **Bounded pooled base64 file reader (screenshot ingress)** -> `Runtime/BoundedBase64FileReader.cs`
- **Base64 payload inspector (HTTP/MCP image ingress)** -> `Runtime/Base64PayloadInspector.cs`
- **Lazy bounded directory retention** -> `Runtime/DirectoryRetention.cs`
- **Mood-weather forecast per character** -> `Runtime/MoodWeatherAdvisor.cs`

### Inference client
- **OpenAI-style HTTP client** -> `Inference/InferenceClient.cs` (has `HttpJsonInferenceClient`)
- **Upstream response header receipts (request id + processing/phase timing)** -> `Inference/HttpResponseReceiptExtractor.cs`
- **Bounded upstream-body readers (JSON/text/bytes)** -> `Inference/HttpContentReadLimiter.cs`
- **Stable local media-cache ids for multimodal request parts** -> `Inference/MediaCacheIdBuilder.cs` + `Inference/MultimodalContentPartMediaCacheIds.cs`
- **Circuit breaker** -> `Inference/InferenceCircuitBreaker.cs`
- **Thermal gate** -> `Runtime/ThermalGate.cs`
- **Execution-profile planner** -> `Inference/InferenceExecutionPlanner.cs`
- **Residency hint policy** -> `Inference/InferenceResidencyPolicy.cs`
- **Model availability probe** -> `Inference/ModelAvailabilityProbe.cs`
- **Tier orchestrator (small-to-large model warm-up)** -> `Inference/ModelTierOrchestrator.cs`

### Vision + TTS + ASR
- **Vision client** -> `Inference/VisionClient.cs` (bounded image checks,
  OpenAI-style `image_url` request body, optional stable media-cache `uuid`)
- **Vision orchestrator (chat augmentation + world extraction)** -> `Runtime/VisionOrchestrator.cs`
- **Screenshot-watcher fallback describer** -> `Inference/SnapshotVisionFallback.cs`
- **TTS client** -> `Inference/TtsClient.cs`
- **ASR transcription client** -> `Inference/AudioTranscriptionClient.cs`

### Pack validation
- **Narrative pack validator** -> `Packs/NarrativePackValidator.cs`
- **Personality pack validator** -> `Packs/PersonalityPack.cs`
- **Shared pack publication-safety scanner** -> `Packs/PackPublicationSafetyValidator.cs`

### Role mesh + observability
- **Role registry (Edge/Worker/Judge/Media/Validator)** -> `Inference/ModelRoleRegistry.cs`
- **Hardware profiler (auto-detect tier + quantization hint)** -> `Inference/HardwareProfiler.cs`
- **Model collaboration + capability/serving/speculation profiles** -> `Inference/ModelCollaborationPlanner.cs`
- **Disagreement detector** -> `Runtime/DisagreementDetector.cs`
- **Proof packet builder** -> `Runtime/ProofPacketBuilder.cs`
- **Why engine (causal answers)** -> `Runtime/WhyEngine.cs`
- **Operator health scorer** -> `Runtime/OperatorHealthScorer.cs`

### Hard-code promotion loop
- **Ledger** -> `Runtime/PromotionLedger.cs`
- **Suggestion builder** -> `Runtime/PromotionSuggestionBuilder.cs`
- **Apply preview builder** -> `Runtime/PromotionApplyPreviewBuilder.cs`
- **Applier (staging-only file writes)** -> `Runtime/PromotionApplier.cs`
- **Background feeder** -> `PalLLM.Sidecar/PromotionLedgerFeeder.cs`

### Inference-engine connectors (`pal connect <target>`)
- **Ollama** -> `scripts/connect-ollama.ps1` (`pal connect ollama`)
- **llama.cpp `llama-server`** -> `scripts/connect-llamacpp.ps1` (`pal connect llamacpp`)
- **LM Studio** -> `scripts/connect-lmstudio.ps1` (`pal connect lmstudio`)
- **vLLM** -> `scripts/connect-vllm.ps1` (`pal connect vllm`)
- **vLLM-Omni (multimodal)** -> `scripts/connect-vllm-omni.ps1` (`pal connect omni`)
- **Hugging Face transformers serve** -> `scripts/connect-transformers.ps1` (`pal connect transformers`)
- **NVIDIA TensorRT-LLM** -> `scripts/connect-tensorrt.ps1` (`pal connect tensorrt`)
- **OpenVINO Model Server** -> `scripts/connect-openvino.ps1` (`pal connect openvino`)
- **Microsoft Foundry Local** -> `scripts/connect-foundry.ps1` (`pal connect foundry`)

### Posture / advisory surfaces (the "what's happening?" answerers)
- **Privacy posture** -> `Runtime/PrivacyPostureBuilder.cs`
- **Air-gap verifier** -> `PalLLM.Sidecar/AirGapVerifier.cs`
- **Graceful degradation** -> `Runtime/GracefulDegradationAdvisor.cs`
- **Resource-budget posture** -> `Runtime/ResourceBudgetPostureBuilder.cs`
- **World-narration cue** -> `Runtime/WorldNarrationAdvisor.cs`
- **Directive-intent translator** -> `Runtime/DirectiveIntentTranslator.cs`

### HTTP + MCP machinery
- **Operator serving-profile checklist** -> `scripts/pal-model-serving.ps1` (`pal models serving`)
- **Live model-endpoint proof** -> `scripts/pal-model-probe.ps1` (`pal models probe`)
- **Route-registration spine** -> `src/PalLLM.Sidecar/Program.cs`
- **Extracted inference routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmInferenceRoutes.cs`
- **Extracted bridge/outbox routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmBridgeRoutes.cs`
- **Extracted conversation routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmConversationRoutes.cs`
- **Extracted planning/explanation routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmPlanningRoutes.cs`
- **Extracted multimodal media routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmMediaRoutes.cs`
- **Extracted health/manifest routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmHealthRoutes.cs`
- **Extracted inspection/advisory routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmInspectionRoutes.cs`
- **Extracted memory/relationship/session routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmStateRoutes.cs`
- **Extracted content/world routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmContentWorldRoutes.cs`
- **Extracted promotion routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmPromotionRoutes.cs`
- **Extracted proof/readiness routes** -> `src/PalLLM.Sidecar/RouteRegistrations/PalLlmProofReadinessRoutes.cs`
- **Core service registration** -> `src/PalLLM.Sidecar/Configuration/PalLlmCoreServiceCollectionExtensions.cs`
- **Inference/runtime service registration** -> `src/PalLLM.Sidecar/Configuration/PalLlmInferenceServiceCollectionExtensions.cs`
- **MCP service registration** -> `src/PalLLM.Sidecar/Configuration/PalLlmMcpServiceCollectionExtensions.cs`
- **Health/OpenAPI registration** -> `src/PalLLM.Sidecar/Configuration/PalLlmHealthAndOpenApiServiceCollectionExtensions.cs`
- **OpenTelemetry registration** -> `src/PalLLM.Sidecar/Configuration/PalLlmObservabilityServiceCollectionExtensions.cs`
- **MCP tools** -> `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs`
- **MCP resources** -> `src/PalLLM.Sidecar/Mcp/PalLlmMcpResources.cs`
- **MCP prompts** -> `src/PalLLM.Sidecar/Mcp/PalLlmMcpPrompts.cs`
- **Sidecar source-generated JSON context** -> `src/PalLLM.Sidecar/PalLlmJsonSerializerContext.cs`
- **Domain source-generated JSON context** -> `PalLLM.Domain/PalLlmDomainJsonSerializerContext.cs`
- **Request validation filter** -> `src/PalLLM.Sidecar/PalApiValidation.cs`
- **API/MCP request body cap** -> `src/PalLLM.Sidecar/Program.cs` middleware + `HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize`
- **Heavy-lane admission control + request timeouts** -> `src/PalLLM.Sidecar/Configuration/PalLlmCoreServiceCollectionExtensions.cs` (`AddRateLimiter`, `AddRequestTimeouts`) + `Program.cs` / `RouteRegistrations/*.cs` (`.RequireRateLimiting(...)`, `.WithRequestTimeout(...)`) + `HttpSurfaceOptions`
- **Conditional caching (ETag)** -> `PalLLM.Sidecar/ConditionalHttp.cs`
  (`JsonTypeInfo<T>` fingerprints through the sidecar source-generated JSON context)
- **Health checks** -> `src/PalLLM.Sidecar/PalLlmHealthChecks.cs`

### Release + packaging
- **Portable proof/support bundle privacy redactor** -> `scripts/PalLLM.Tooling.ps1`
- **Release-readiness builder** -> `PalLLM.Sidecar/ReleaseReadinessBuilder.cs`
- **Full-audit evidence** -> `PalLLM.Sidecar/ReleaseFullAuditEvidenceBuilder.cs`
- **Artifact-integrity evidence** -> `PalLLM.Sidecar/ReleaseArtifactIntegrityEvidenceBuilder.cs`
- **Bridge proof** -> `PalLLM.Sidecar/BridgeProofBuilder.cs`
- **Self-description** -> `PalLLM.Sidecar/SelfDescriptionBuilder.cs`
- **Quickstart guide** -> `PalLLM.Sidecar/QuickstartGuideBuilder.cs`
- **Shared publication text-surface scanner** -> `scripts/PalLLM.Tooling.ps1`
- **Packager** -> `scripts/package-release.ps1`
- **Proof/support bundle exporters** -> `scripts/export-release-proof-bundle.ps1` / `scripts/export-support-bundle.ps1`
- **Proof/support bundle archive verifier** -> `PalLLM.Sidecar/ReleaseBundleArchiveInspector.cs` (readable zip, expected manifest, manifest-listed files, duplicate-entry guard, and relative path-safe entry names)
- **Checksums + artifact-integrity evidence** -> `scripts/compute-release-checksums.ps1`

- **Workflow action pin audit** -> `scripts/audit-workflow-action-pins.ps1`

### Feature catalog
- **All 122 entries** -> `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`
- Each entry has `Id`, `Source`, `Status` (`ready` | `scaffolded` | `deferred`), `Summary`, `Notes`.

## "Where is route X registered?"

Application `/api/*` routes live in
`src/PalLLM.Sidecar/RouteRegistrations/*.cs`; the same folder also owns the
Field Console static-asset routes. `Program.cs` keeps host creation,
middleware, static-asset manifest discovery, OpenAPI/MCP mapping, and the route
spine. Service registration moved to `src/PalLLM.Sidecar/Configuration/*.cs`
in Phase 2a. Grep route paths across both route locations:

```bash
rg -n '"/chat"|"/inference' src/PalLLM.Sidecar/Program.cs src/PalLLM.Sidecar/RouteRegistrations
```

There are no controllers - everything is minimal-API `api.Map{Get,Post,...}`
calls. The audit pipeline counts `api.Map*` in `Program.cs` and
`RouteRegistrations/*.cs`, so route companions stay drift-gated.

## "Where is MCP tool X defined?"

All tools: `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs` � one `[McpServerTool(Name = "pal_*")]`-attributed method per tool.

## "Where is feature X described?"

`src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`. This is the
runtime-accessible catalog � `GET /api/features` returns it.

## Tests

One fixture per subsystem. Naming follows `<Subsystem>Tests.cs`. The
big integration fixture is `tests/PalLLM.Tests/SidecarEndpointTests.cs`
(boots an in-process sidecar, tests HTTP routes end-to-end).

To find the tests for a piece of code: the test file usually shares
the code file's name. `MoodWeatherAdvisor.cs` -> `MoodWeatherAdvisorTests.cs`.

## Harvest-friendly boundaries

See [`HARVEST.md`](HARVEST.md) for the full menu of what can lift
cleanly into another project. Quick answer: anything under
`src/PalLLM.Domain/` is .NET 10 portable (no Palworld / UE4SS / ASP.NET
dependencies). Anything under `src/PalLLM.Sidecar/` is ASP.NET Core.
Anything under `mod/` is Windows-only UE4SS Lua.



