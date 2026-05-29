# PalLLM monolith-extraction roadmap

Last audited: `2026-05-24`

This is a **plan**, not a commitment. It captures the phased
extraction strategy for the two largest files in the repo so that
future passes can land the work incrementally without losing the
audit-green property. Each phase is a separate landable pass; this
roadmap exists so reviewers don't have to re-derive the plan from
scratch every time a phase is proposed.

## Why this doc exists

The drift audit reports the current state honestly: PalLLM has two
files that dominate the line-count distribution.

| File | Lines | What it is |
|---|---|---|
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.cs) | `1,104` | Every chat turn lives here. Also owns TTS/ASR entry points, vision description, and session orchestration. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.Helpers.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.Helpers.cs) | `391` | Phase 1a helper companion: endpoint timing, MIME routing, bounded directory counts, receipt sanitizing, and sorted file enumeration. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.Inference.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.Inference.cs) | `357` | Phase 1h inference companion: performance snapshots, circuit/model metadata, warmup, live-inference residency tracking, and operation receipts. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.UiProbe.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.UiProbe.cs) | `628` | Phase 1b UI-probe companion: bounded metadata-keyed dump parse cache, diagnostics snapshot caching, dump parsing, HUD candidate ranking, widget cloning, and local diagnostics retention. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.BridgeBoot.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.BridgeBoot.cs) | `482` | Phase 1d bridge-boot companion: heartbeat normalization, compatibility signals, native-readiness snapshots, and HUD bind recommendations. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.Bridge.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.Bridge.cs) | `912` | Phase 1e bridge companion: inbox drain, event processing, bridge activity snapshots, loop proof, delivery receipts, speech playback receipts, and action feedback. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.Prompt.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.Prompt.cs) | `312` | Phase 1c prompt companion: system prompt assembly, bounded text trimming, assistant-message/status formatting, and world/lore/memory context appenders. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.Snapshot.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.Snapshot.cs) | `489` | Phase 1g snapshot companion: health/dashboard assembly, world-state reads, vision state application, character lookup, and event-driven snapshot mutation. |
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.Outbox.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.Outbox.cs) | `289` | Phase 1f outbox companion: screenshot ingest queue, outbox listing/clearing/writing, archive retention, and outbox JSON metadata. |
| [`src/PalLLM.Sidecar/Program.cs`](../src/PalLLM.Sidecar/Program.cs) | `336` | Host builder, middleware, static-asset manifest resolution, OpenAPI/MCP mapping, and route spine. Service registration lives in `Configuration/*.cs`; static assets and `/api` route domains live in `RouteRegistrations/*.cs`. `0` inline `api.Map{Get,Post}` calls. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmStaticAssetRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmStaticAssetRoutes.cs) | `125` | Post-Phase-2 static-asset companion: Field Console physical-file routes with metadata-keyed weak content-hash ETags, `If-Modified-Since` revalidation, and the packaged-EXE `MapStaticAssets` fallback. |
| [`src/PalLLM.Sidecar/Configuration/PalLlmCoreServiceCollectionExtensions.cs`](../src/PalLLM.Sidecar/Configuration/PalLlmCoreServiceCollectionExtensions.cs) | `172` | Phase 2a core service companion: JSON options, ProblemDetails, response compression, options binding, output cache, rate limits, and request timeouts. |
| [`src/PalLLM.Sidecar/Configuration/PalLlmInferenceServiceCollectionExtensions.cs`](../src/PalLLM.Sidecar/Configuration/PalLlmInferenceServiceCollectionExtensions.cs) | `142` | Phase 2a inference/runtime service companion: pooled HTTP clients, metrics, model planners, runtime singleton, and runtime hosted services. |
| [`src/PalLLM.Sidecar/Configuration/PalLlmMcpServiceCollectionExtensions.cs`](../src/PalLLM.Sidecar/Configuration/PalLlmMcpServiceCollectionExtensions.cs) | `56` | Phase 2a MCP service companion: pooled upstream-MCP client, discovery worker, and Streamable HTTP MCP server registration. |
| [`src/PalLLM.Sidecar/Configuration/PalLlmHealthAndOpenApiServiceCollectionExtensions.cs`](../src/PalLLM.Sidecar/Configuration/PalLlmHealthAndOpenApiServiceCollectionExtensions.cs) | `46` | Phase 2a health/OpenAPI service companion. |
| [`src/PalLLM.Sidecar/Configuration/PalLlmObservabilityServiceCollectionExtensions.cs`](../src/PalLLM.Sidecar/Configuration/PalLlmObservabilityServiceCollectionExtensions.cs) | `76` | Phase 2a opt-in OpenTelemetry service companion. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmInferenceRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmInferenceRoutes.cs) | `99` | Phase 2b inference-route companion: `/api/inference/*` plus `/api/mcp/upstream`. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmBridgeRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmBridgeRoutes.cs) | `32` | Phase 2c bridge/outbox-route companion: `/api/bridge/drain`, `/api/bridge/outbox`, `/api/bridge/ui-probe`, and `/api/bridge/outbox/clear`. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmMediaRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmMediaRoutes.cs) | `92` | Phase 2d multimodal-media route companion: `/api/vision/*`, `/api/tts/synthesize`, and `/api/audio/transcribe`. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmHealthRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmHealthRoutes.cs) | `143` | Phase 2e health/manifest route companion: `/api/health`, `/api/dashboard`, `/api/features`, `/api/describe`, `/api/quickstart`, `/metrics`, `/health/live`, and `/health/ready`. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmInspectionRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmInspectionRoutes.cs) | `200` | Phase 2f inspection/advisory route companion: self-healing, air-gap, roles, hardware, degradation, budgets, narration, lifetime relationships, character mood, and privacy posture. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmStateRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmStateRoutes.cs) | `65` | Phase 2g state route companion: `/api/memory/recall`, `/api/relationships`, `/api/relationships/{characterId:int}`, `/api/session/save`, and `/api/session/reload`. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmContentWorldRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmContentWorldRoutes.cs) | `127` | Phase 2h content/world route companion: narrative-pack listing/resolution/reload/validation, adapter logs, world-state reads, and snapshot updates. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmPromotionRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmPromotionRoutes.cs) | `203` | Phase 2i promotion-loop route companion: observation recording, summaries, suggestions, editor-ready previews, and staging-only apply. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmProofReadinessRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmProofReadinessRoutes.cs) | `91` | Phase 2j proof/readiness route companion: proof packets, release-readiness snapshots, and bridge-proof snapshots. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmConversationRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmConversationRoutes.cs) | `358` | Phase 2k conversation-route companion: party chat, single-turn chat, SSE streaming chat, and chat-plan advisory routes. |
| [`src/PalLLM.Sidecar/RouteRegistrations/PalLlmPlanningRoutes.cs`](../src/PalLLM.Sidecar/RouteRegistrations/PalLlmPlanningRoutes.cs) | `95` | Phase 2l deterministic planning/explanation route companion: directive planning, duo planning, disagreement checks, and why answers. |

Together they are `7,405` lines, about `15.6%` of all non-test C# code.
None of these files is bad — they are well-commented and the methods are
focused — but a new reader has to scroll a *lot* to find the seam
they're looking for.

This roadmap converts that single-file scroll into a directory
listing, **without changing behavior, without touching the portable
adapter contract (ADR 0002), and without rewriting tests**.

## Hard constraints (these never bend)

1. **Zero behavioral change.** Every phase is a pure restructuring.
   No new tests, no removed tests, no changed assertions.
2. **Zero portable-adapter-contract drift (ADR 0002).** The 5
   interfaces in
   [`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`](../src/PalLLM.Domain/Portable/PortableAdapterContracts.cs)
   keep their exact shape.
3. **All 16 drift gates stay green before and after every phase.**
   Including `Drift_Hot_file_line_count`, which is the gate that
   actually measures the monoliths.
4. **One pass = one phase.** No omnibus refactors. Each phase lands
   on its own merge, with its own CHANGELOG entry, and its own
   `audit` run.

## Pattern: C# `partial class` over class extraction

C# allows a class declaration to span multiple files (`partial
class`). Every other consumer continues to see one `PalLlmRuntime`
type with one constructor and one public surface; the implementation
gets distributed across companion files in the same namespace.

**Why partial classes, not extracted helper classes:**

- **Zero call-site churn.** No `_bridgeCoordinator.ProcessEvent(...)`
  rewrites — the same method `ProcessBridgeEvent` still lives on
  `PalLlmRuntime`, just defined in a different file.
- **Zero DI churn.** Every test fixture, every endpoint that takes
  `PalLlmRuntime` from DI keeps working without changes.
- **Zero public-API churn.** The OpenAPI snapshot doesn't move; the
  feature catalog doesn't move; nothing downstream notices.
- **Reversible.** If a phase introduces a problem, `git revert`
  collapses the companion files back into the monolith. No state
  was extracted, so there's nothing else to roll back.

**Why not extracted classes (the obvious alternative):**

- Forces a new constructor chain (DI registrations, test fixtures).
- Forces every internal call site to be rewritten with a new
  receiver.
- Risks behavioral drift: state that's currently a private field
  becomes a parameter — easy to forget one.
- Breaks the "one pass = audit-green" property because the cascade
  touches every test that mocks `PalLlmRuntime`.

The audit gate `Drift_Hot_file_line_count` doesn't care which file
the lines live in — only that the *primary* file stays under its
budget. Partial classes split the lines exactly the way the gate
wants.

## Phase 1 — `PalLlmRuntime.cs` (complete: 1,452 → 1,104 lines + inference companion)

The file already has clear responsibility clusters separated by
inline comments. Each cluster becomes a `PalLlmRuntime.<Topic>.cs`
companion file.

| New file | Lines (est.) | Responsibility | Source lines |
|---|---|---|---|
| `PalLlmRuntime.cs` (kept) | 1,104 | Constructor, TTS/ASR entry points, public `ChatAsync`, memory/session entry points, vision description | primary spine |
| `PalLlmRuntime.Helpers.cs` | 391 | **Done in Phase 1a.** Pure static helpers: `NormalizeEndpointingMs`, `MimeToExtension`, `DetermineSpeechPlaybackHint`, `SanitizeBridgeReceiptText`, `FirstNonEmpty`, bounded directory counts, and sorted file enumeration | scattered `private static` |
| `PalLlmRuntime.Inference.cs` | 357 | **Done in Phase 1h.** Inference performance snapshots, circuit/model metadata, `WarmInferenceAsync`, `RecordLiveInferenceSuccess`, `BuildWarmupStatusMessage`, `RecordInferenceOperation` | moved in Pass 386 |
| `PalLlmRuntime.Snapshot.cs` | 489 | **Done in Phase 1g.** `GetHealth`, `GetWorldState`, `GetDashboardSnapshot`, `UpdateSnapshot`, `ExtractWorldStateAsync`, `ResolveCharacter`, `ApplyWeatherToSnapshot`, `ApplyProductionToSnapshot`, `ApplyTravelToSnapshot`, and `AppendWorldEvent` | moved in Pass 385 |
| `PalLlmRuntime.Outbox.cs` | 289 | **Done in Phase 1f.** `ProcessScreenshotsAsync`, `WriteOutboxReplyAsync`, `GetOutboxListings`, `ClearOutbox`, `PrunePendingScreenshots`, `Archive`, and outbox source-generated JSON metadata | moved in Pass 383 |
| `PalLlmRuntime.Bridge.cs` | 912 | **Done in Phase 1e.** `DrainInbox`, `ProcessBridgeEvent`, `RecordBridgeActivity`, `RememberUiProbe`, `RememberChatIngress`, `RememberOutboxReply`, `RememberReplyDelivery`, `RememberSpeechPlayback`, `RememberActionFeedback`, bridge-loop proof cloning/building, `PromoteDiscoveredBase`, `RememberAssistantFallback` | moved in Pass 382 |
| `PalLlmRuntime.UiProbe.cs` | 628 | **Done in Phase 1b + cache-hardened in Pass 399.** `GetUiProbeDiagnostics`, `BuildUiProbeSnapshot`, `InvalidateUiProbeDiagnostics`, `PruneUiProbeDiagnosticsDirectory`, bounded metadata-keyed dump parse caching, dump parsing, HUD candidate ranking, clone helpers, and the 5 nested classes (`UiProbeDumpMetadata`, `UiProbeDumpCacheEntry`, `UiProbeDumpDocument`, `UiProbeCandidateAccumulator`, `DirectoryActivitySnapshot`) | moved in Pass 378; cache-hardened in Pass 399 |
| `PalLlmRuntime.BridgeBoot.cs` | 482 | **Done in Phase 1d.** `RememberBridgeBoot`, `NormalizeBridgeBootPayload`, `NormalizeCompatSignals`, `CloneBridgeBootPayload`, `BuildNativeReadinessSnapshot`, `BuildHudBindRecommendation`, `HasCompatSignal`, `NormalizeHudTargetList` | moved in Pass 381 |
| `PalLlmRuntime.Prompt.cs` | 312 | **Done in Phase 1c.** `BuildSystemPrompt`, `AppendRelationshipContext`, `AppendWorldContext`, `AppendStableCharacterContext`, `AppendCharacterStateContext`, `AppendLoreContext`, `AppendMemoryContext`, `TrimToLength`, `TrimAssistantMessage`, `AppendStatusNotice`, `FormatKnownBase`, `FormatLatestProduction`, `FormatLatestTravel`, `FormatAreaRange`, `ResolveSpeakerName` | moved in Pass 380 |

Current state after Phase 1h:
`PalLlmRuntime.cs` is `1,104` lines,
`PalLlmRuntime.Helpers.cs` is `391` lines,
`PalLlmRuntime.Inference.cs` is `357` lines,
`PalLlmRuntime.UiProbe.cs` is `628` lines,
`PalLlmRuntime.BridgeBoot.cs` is `482` lines,
`PalLlmRuntime.Bridge.cs` is `912` lines,
`PalLlmRuntime.Prompt.cs` is `312` lines,
`PalLlmRuntime.Snapshot.cs` is `489` lines, and
`PalLlmRuntime.Outbox.cs` is `289` lines. Phase 1 is now complete;
further runtime reductions should be proposed as a new roadmap phase
after the queued `Program.cs` extraction.

**Verification checklist for Phase 1:**

- [ ] No new public method on `PalLlmRuntime` (private partial members
      stay private).
- [ ] `git log --stat` shows zero changes outside
      `src/PalLLM.Domain/Runtime/PalLlmRuntime*.cs`.
- [ ] `dotnet test` passes exactly the same `1315` tests with the
      same names.
- [ ] Audit gate `Drift_Hot_file_line_count` passes — the *primary*
      `PalLlmRuntime.cs` is now well under its budget, and the
      mirror line-counts in `docs/CODE_MAP.md` and
      `docs/ARCHITECTURE.md` are updated to reflect the new
      primary-file size.
- [ ] OpenAPI snapshot: unchanged (no route changes).
- [ ] Feature catalog: unchanged (no feature changes).

## Phase 2 — `Program.cs` (complete: 2,146 -> 336 lines + service and route/static companions)

Program.cs is top-level-statements style — there are no methods,
just sequential `builder.Services.AddXxx(...)` and
`api.MapXxx(...)`. The right extraction tool here is **extension
methods on `IServiceCollection` and `IEndpointRouteBuilder`**, not
partial classes.

| New file | Lines (est.) | Responsibility |
|---|---|---|
| `Program.cs` (kept) | 336 | `WebApplication.CreateBuilder`, `builder.Services.AddPalLlm*()` calls, middleware, static-asset manifest resolution, OpenAPI/MCP mapping, route spine, `app.Run()` |
| `Configuration/PalLlmCoreServiceCollectionExtensions.cs` | 172 | **Done in Phase 2a.** `AddPalLlmCore(IServiceCollection, IConfiguration, HttpSurfaceOptions)` — JSON options, problem details, response compression, options binding, validator, OutputCache, rate limiting, request timeouts |
| `Configuration/PalLlmInferenceServiceCollectionExtensions.cs` | 142 | **Done in Phase 2a.** `AddPalLlmInference(...)` — every `HttpClient` for inference / vision / TTS / ASR, model tier orchestrator, collaboration planners, runtime singleton, hosted runtime workers |
| `Configuration/PalLlmMcpServiceCollectionExtensions.cs` | 56 | **Done in Phase 2a.** Upstream MCP client pool, discovery worker, MCP HTTP server |
| `Configuration/PalLlmHealthAndOpenApiServiceCollectionExtensions.cs` | 46 | **Done in Phase 2a.** Health checks + .NET 10 OpenAPI generation |
| `Configuration/PalLlmObservabilityServiceCollectionExtensions.cs` | 76 | **Done in Phase 2a.** Opt-in OpenTelemetry traces, metrics, and logs |
| `RouteRegistrations/PalLlmInferenceRoutes.cs` | 99 | **Done in Phase 2b.** `MapPalLlmInferenceRoutes(api, httpOptions)` — `/api/inference/performance`, `/api/inference/collaboration`, `/api/inference/collaboration/plan`, `/api/inference/warmup`, `/api/mcp/upstream` |
| `RouteRegistrations/PalLlmBridgeRoutes.cs` | 32 | **Done in Phase 2c.** `MapPalLlmBridgeRoutes(api)` — `/api/bridge/drain`, `/api/bridge/outbox`, `/api/bridge/ui-probe`, `/api/bridge/outbox/clear` |
| `RouteRegistrations/PalLlmMediaRoutes.cs` | 92 | **Done in Phase 2d.** `MapPalLlmVisionRoutes(api)` + `MapPalLlmAudioRoutes(api)` — `/api/vision/describe`, `/api/vision/world-state`, `/api/vision/screenshots/process`, `/api/tts/synthesize`, `/api/audio/transcribe` |
| `RouteRegistrations/PalLlmHealthRoutes.cs` | 143 | **Done in Phase 2e.** `MapPalLlmHealthRoutes(app, api, httpOptions)` — `/api/health`, `/api/dashboard`, `/metrics`, `/health/live`, `/health/ready`, `/api/features`, `/api/describe`, `/api/quickstart` |
| `RouteRegistrations/PalLlmInspectionRoutes.cs` | 200 | **Done in Phase 2f.** `MapPalLlmInspectionRoutes(api)` — `/api/self-healing/status`, `/api/airgap/verify`, `/api/roles`, `/api/hardware`, `/api/degradation/advisory`, `/api/budgets`, `/api/narration/cue`, `/api/relationships/lifetime`, `/api/characters/{characterId:int}/mood`, `/api/privacy/posture` |
| `RouteRegistrations/PalLlmStateRoutes.cs` | 65 | **Done in Phase 2g.** `MapPalLlmMemoryRelationshipRoutes(api)` + `MapPalLlmSessionRoutes(api)` — `/api/memory/recall`, `/api/relationships*`, and `/api/session/*` |
| `RouteRegistrations/PalLlmContentWorldRoutes.cs` | 127 | **Done in Phase 2h.** `MapPalLlmContentWorldRoutes(api)` — `/api/packs*`, `/api/logs`, `/api/world`, and `/api/snapshot` |
| `RouteRegistrations/PalLlmPromotionRoutes.cs` | 203 | **Done in Phase 2i.** `MapPalLlmPromotionRoutes(api)` — `/api/promotion/record`, `/api/promotion/summary`, `/api/promotion/suggestions`, `/api/promotion/apply/preview`, and `/api/promotion/apply` |
| `RouteRegistrations/PalLlmProofReadinessRoutes.cs` | 91 | **Done in Phase 2j.** `MapPalLlmProofPacketRoute(api)` + `MapPalLlmReleaseProofRoutes(api, httpOptions)` — `/api/proof/packet`, `/api/release/readiness`, and `/api/bridge/proof` |
| `RouteRegistrations/PalLlmConversationRoutes.cs` | 358 | **Done in Phase 2k.** `MapPalLlmPartyChatRoute(api)` + `MapPalLlmChatTurnRoutes(api)` — `/api/chat/party`, `/api/chat`, `/api/chat/stream`, and `/api/chat/plan` |
| `RouteRegistrations/PalLlmPlanningRoutes.cs` | 95 | **Done in Phase 2l.** `MapPalLlmPlanningRoutes(api)` + `MapPalLlmWhyRoute(api)` — `/api/directives/plan`, `/api/duo/plan`, `/api/disagreement/check`, and `/api/why` |
| `RouteRegistrations/PalLlmStaticAssetRoutes.cs` | 125 | **Done in post-Phase-2 cleanup.** `MapPalLlmFieldConsoleStaticAssets(app, staticAssetsManifestPath)` — Field Console physical-file routes, validator revalidation, and `MapStaticAssets` fallback |

**Why Phase 2b widened the route audit first:** the drift gates now scan both
`Program.cs` and `RouteRegistrations/*.cs` for `api.Map*` calls. That keeps
future route companions count-gated while allowing domain slices to move out
of the startup spine without changing the public surface. Splitting routes by
domain remains the target because related endpoints stay co-located the same
way an operator would expect them in `docs/API.md`.

**Verification checklist for Phase 2:**

- [ ] `Drift_Api_route_count` still reports `57` total routes across
      `Program.cs` and `RouteRegistrations/*.cs`.
- [ ] `Drift_OpenApi_snapshot` passes — no route ordering, no
      grouping changes that would alter the generated OpenAPI doc.
- [ ] `Drift_Api_reference_surface` passes — `docs/API.md`'s route
      list still matches.

## Non-goals

- **Splitting `PalLlmRuntime` into multiple types.** The class
  encapsulates per-companion state (memory, snapshot, circuit
  breaker, outbox cursor); fragmenting that into multiple objects
  would force every test fixture to wire them up explicitly. Stays
  as one type, one DI registration, one constructor.
- **Changing the test layout.** Tests stay in
  `tests/PalLLM.Tests/RuntimeTests.cs` even after the source splits.
  Test reorganization is its own decision (see "Future work" below).
- **Touching ADR 0002 (portable adapter seam).** The 5 interfaces in
  `src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` stay
  byte-identical.
- **Performance tuning.** Pure restructuring — same allocations,
  same call paths, same hot loops.

## Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| A partial-class companion accidentally references a non-existent field due to a copy/paste slip | Medium | `dotnet build` catches every typo; CI matrix runs build on both Windows + Linux |
| Audit gate `Drift_Hot_file_line_count` reports the *combined* file count instead of just the primary | Low | The gate already reads the primary-file line count from a single `wc -l`; partial-class siblings are scored separately |
| A future contributor splits a partial across so many files that finding code gets harder | Medium | This roadmap caps each phase at 7-8 companion files. Beyond that, propose a new ADR. |
| Splitting `Program.cs` extension methods changes the order of `AddSingleton` registrations and a test starts failing because a service resolves differently | Low | Each phase runs `dotnet test` before merging. Order-sensitive registrations are flagged by the test failure, not silently shipped. |

## Phasing recommendation

Land Phase 1 sub-passes in this order — smallest blast radius first:

1. **Phase 1a:** Extract `PalLlmRuntime.Helpers.cs` (pure static
   functions, zero state). **Done in Pass 375.**
2. **Phase 1b:** Extract `PalLlmRuntime.UiProbe.cs` (includes the 3
   nested classes — moving them gets the most lines for the least
   risk). **Done in Pass 378.**
3. **Phase 1c:** Extract `PalLlmRuntime.Prompt.cs` (pure static
   builders, no state). **Done in Pass 380.**
4. **Phase 1d:** Extract `PalLlmRuntime.BridgeBoot.cs`. **Done in Pass 381.**
5. **Phase 1e:** Extract `PalLlmRuntime.Bridge.cs`. **Done in Pass 382.**
6. **Phase 1f:** Extract `PalLlmRuntime.Outbox.cs`. **Done in Pass 383.**
7. **Phase 1g:** Extract `PalLlmRuntime.Snapshot.cs`. **Done in Pass 385.**
8. **Phase 1h:** Extract `PalLlmRuntime.Inference.cs`. **Done in Pass 386.**

Phase 1 is complete. `PalLlmRuntime.cs` is now `1,104` lines with the
largest helper clusters split into named companion files.

Phase 2 is similar but coarser-grained. Phase 2a extracted the
service-collection extensions. Phase 2b widened the route-count/reference
audit to support `RouteRegistrations/*.cs` and moved the inference/MCP-upstream
route slice. Phase 2c moved the bridge/outbox route slice. Phase 2d moved the
multimodal media route slice. Phase 2e moved the health/manifest route slice
and widened operational-route counting to include route companions. Phase 2f
moved the read-only inspection/advisory route slice. Phase 2g moved the
memory/relationship/session state route slice. Phase 2h moved the
content/world route slice. Phase 2i moved the promotion-loop route
slice. Phase 2j moved the proof/readiness route slice. Phase 2k moved
the conversation route slice. Phase 2l moved the deterministic planning /
explanation route slice. Phase 2 is complete; further `Program.cs` work
should be proposed as a new scoped phase rather than extending this one.

## Future work (out of scope here)

- **Test file reorganization.** `tests/PalLLM.Tests/RuntimeTests.cs`
  is `5,200+` lines. Once Phase 1 lands, mirroring the source-side
  split in the test layout becomes natural — but that's its own
  pass, gated by the source split landing first.
- **`PalLlmFeatureCatalog.cs` (`~120 KB`).** Catalog-shape data, not
  logic; deferred until it actually slows reads.
- **`SidecarEndpointTests.cs` (`~212 KB`).** Same as above —
  data-heavy test fixtures.

## Related

- [`ADR 0002`](adr/0002-portable-adapter-seam.md) — the contract this
  refactor must not touch.
- [`ADR 0004`](adr/0004-drift-gates-over-manual-review.md) — the gates
  that hold the refactor honest.
- [`CODE_MAP.md`](CODE_MAP.md) — the directory listing that this
  refactor materially improves.
- [`HOT_PATH.md`](HOT_PATH.md) — the budgets the refactor must not
  regress.
