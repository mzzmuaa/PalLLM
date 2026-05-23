# PalLLM Code Map

Last audited: `2026-05-21`

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
|   |   +-- Program.cs              -> every HTTP route registration (57 /api/* routes)
|   |   +-- Mcp/                    -> MCP tools + resources + prompts (38 tools)
|   |   +-- wwwroot/                -> Field Console dashboard (static HTML/JS/CSS)
|   +-- mod/ue4ss/Mods/PalLLM/      -> Lua bridge (Windows-only, Palworld-specific)
+-- tests/PalLLM.Tests/             -> NUnit, 1313 tests, one file per subsystem
+-- scripts/                        -> PowerShell: install, doctor, smoke, audit, package
+-- docs/                           -> Di�taxis-organised documentation
```

## The big three hot files

| File | Lines | What lives there |
|---|---|---|
| `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` | `4744` | `ChatAsync` - THE hot path. Also memory, vision orchestration, relationships, session persistence, bridge drain, outbox writes. |
| `src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs` | `1427` | Deterministic visual/audio cue planner � pairs with every chat reply |
| `src/PalLLM.Sidecar/Program.cs` | `2056` | Every HTTP route, DI wiring, middleware, health checks |

If you're here to find the chat path: start at `PalLlmRuntime.ChatAsync`
(`PalLlmRuntime.cs` around line 900). Upstream: `Program.cs` route
`POST /api/chat`. Downstream: `ChatResponse` contract in
`src/PalLLM.Domain/Integration/Contracts.cs`.

## "Where does X live?" � by concept

### Chat pipeline
- **Chat request/response contracts** -> `src/PalLLM.Domain/Integration/Contracts.cs`
- **Single-turn chat orchestrator** -> `PalLlmRuntime.ChatAsync` (direct callers trim user text at `ChatRequest.UserMessageMaxLength`)
- **Fan-out across characters (party chat)** -> `Program.cs` route `POST /api/chat/party` + `PalApiValidation.ValidatePartyChatRequest`
- **SSE streaming variant** -> `Program.cs` route `POST /api/chat/stream` + `ChatStreamWriter.cs`
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
- **Circuit breaker** -> `Inference/InferenceCircuitBreaker.cs`
- **Thermal gate** -> `Runtime/ThermalGate.cs`
- **Execution-profile planner** -> `Inference/InferenceExecutionPlanner.cs`
- **Residency hint policy** -> `Inference/InferenceResidencyPolicy.cs`
- **Model availability probe** -> `Inference/ModelAvailabilityProbe.cs`
- **Tier orchestrator (small-to-large model warm-up)** -> `Inference/ModelTierOrchestrator.cs`

### Vision + TTS + ASR
- **Vision client** -> `Inference/VisionClient.cs`
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
- **All routes** -> `PalLLM.Sidecar/Program.cs`
- **MCP tools** -> `PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs`
- **MCP resources** -> `PalLLM.Sidecar/Mcp/PalLlmMcpResources.cs`
- **MCP prompts** -> `PalLLM.Sidecar/Mcp/PalLlmMcpPrompts.cs`
- **Sidecar source-generated JSON context** -> `PalLLM.Sidecar/PalLlmJsonSerializerContext.cs`
- **Domain source-generated JSON context** -> `PalLLM.Domain/PalLlmDomainJsonSerializerContext.cs`
- **Request validation filter** -> `PalLLM.Sidecar/PalApiValidation.cs`
- **API/MCP request body cap** -> `PalLLM.Sidecar/Program.cs` middleware + `HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize`
- **Heavy-lane admission control + request timeouts** -> `PalLLM.Sidecar/Program.cs` (`AddRateLimiter`, `AddRequestTimeouts`, `.RequireRateLimiting(...)`, `.WithRequestTimeout(...)`) + `HttpSurfaceOptions`
- **Conditional caching (ETag)** -> `PalLLM.Sidecar/ConditionalHttp.cs`
- **Health checks** -> `PalLLM.Sidecar/PalLlmHealthChecks.cs`

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

Every `/api/*` route is in `src/PalLLM.Sidecar/Program.cs`. Grep for
the path literal:

```bash
grep -n '"/api/chat"' src/PalLLM.Sidecar/Program.cs
```

There are no controllers � everything is minimal-API `api.Map{Get,Post,...}`
calls in a single file. This is deliberate: the audit pipeline counts
`api.Map*` literally, and splitting across files breaks the count.

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



