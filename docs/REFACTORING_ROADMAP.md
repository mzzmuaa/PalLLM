# PalLLM monolith-extraction roadmap

Last audited: `2026-05-23`

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
| [`src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.cs) | `4,744` | Every chat turn lives here. Inference circuit, bridge ingest, outbox, snapshot, prompt building, UI-probe diagnostics, archival. |
| [`src/PalLLM.Sidecar/Program.cs`](../src/PalLLM.Sidecar/Program.cs) | `2,105` | Every HTTP route, every DI registration, every middleware. `57` `api.Map*` calls. |

Together they are `6,849` lines, about `13%` of all non-test C# code.
Neither file is bad — both are well-commented and the methods are
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

## Phase 1 — `PalLlmRuntime.cs` (4,744 → ~600 lines + 7 companions)

The file already has clear responsibility clusters separated by
inline comments. Each cluster becomes a `PalLlmRuntime.<Topic>.cs`
companion file.

| New file | Lines (est.) | Responsibility | Source lines |
|---|---|---|---|
| `PalLlmRuntime.cs` (kept) | ~600 | Constructor, public ChatAsync entry points, partial method declarations, field declarations | 1-493 (header), public surface |
| `PalLlmRuntime.Helpers.cs` | ~400 | Pure static helpers: `NormalizeEndpointingMs`, `MimeToExtension`, `DetermineSpeechPlaybackHint`, `SanitizeBridgeReceiptText`, `FirstNonEmpty`, `Format*` family, `Trim*` family | scattered `private static` |
| `PalLlmRuntime.Inference.cs` | ~600 | Inference circuit state, warmup, model selection, `RecordLiveInferenceSuccess`, `BuildWarmupStatusMessage`, `RecordInferenceOperation` | ~614-1232 |
| `PalLlmRuntime.Snapshot.cs` | ~400 | `UpdateSnapshot`, `ApplyWeatherToSnapshot`, `ApplyProductionToSnapshot`, `ApplyTravelToSnapshot`, `AppendWorldEvent` | ~1235-1900, ~2669-2828 |
| `PalLlmRuntime.Outbox.cs` | ~500 | `WriteOutboxReplyAsync`, `ClearOutbox`, `PrunePendingScreenshots`, `Archive`, MIME routing | ~1920-2280 |
| `PalLlmRuntime.Bridge.cs` | ~800 | `ProcessBridgeEvent`, `RecordBridgeActivity`, `RememberUiProbe`, `RememberChatIngress`, `RememberOutboxReply`, `RememberReplyDelivery`, `RememberSpeechPlayback`, `RememberActionFeedback`, `PromoteDiscoveredBase`, `RememberAssistantFallback` | ~2370-2900, ~4234-4280 |
| `PalLlmRuntime.UiProbe.cs` | ~700 | `InvalidateUiProbeDiagnostics`, `PruneUiProbeDiagnosticsDirectory`, `BuildUiProbeCandidateKey`, `BuildUiProbeSearchText`, + the 3 nested classes (`UiProbeDumpDocument`, `UiProbeCandidateAccumulator`, `DirectoryActivitySnapshot`) | ~3111-3526, ~4665-4744 |
| `PalLlmRuntime.BridgeBoot.cs` | ~400 | `RememberBridgeBoot`, `BuildCompatSummary`, `HasCompatSignal`, `TakeFirstNonBlank`, `ClampBridgeLagMs` | ~3700-4233 |
| `PalLlmRuntime.Prompt.cs` | ~500 | `BuildSystemPrompt`, `AppendRelationshipContext`, `AppendWorldContext`, `AppendStableCharacterContext`, `AppendCharacterStateContext`, `AppendLoreContext`, `AppendMemoryContext`, `TrimToLength`, `TrimAssistantMessage`, `AppendStatusNotice`, `FormatKnownBase`, `FormatLatestProduction`, `FormatLatestTravel`, `FormatAreaRange`, `ResolveSpeakerName` | ~4267-4609 |

Total estimate: `~600 + 400 + 600 + 400 + 500 + 800 + 700 + 400 + 500 = ~4,900` lines, distributed across `9` files instead of one.

**Verification checklist for Phase 1:**

- [ ] No new public method on `PalLlmRuntime` (private partial members
      stay private).
- [ ] `git log --stat` shows zero changes outside
      `src/PalLLM.Domain/Runtime/PalLlmRuntime*.cs`.
- [ ] `dotnet test` passes exactly the same `1309` tests with the
      same names.
- [ ] Audit gate `Drift_Hot_file_line_count` passes — the *primary*
      `PalLlmRuntime.cs` is now well under its budget, and the
      mirror line-counts in `docs/CODE_MAP.md` and
      `docs/ARCHITECTURE.md` are updated to reflect the new
      primary-file size.
- [ ] OpenAPI snapshot: unchanged (no route changes).
- [ ] Feature catalog: unchanged (no feature changes).

## Phase 2 — `Program.cs` (2,105 → ~400 lines + 7 companions)

Program.cs is top-level-statements style — there are no methods,
just sequential `builder.Services.AddXxx(...)` and
`api.MapXxx(...)`. The right extraction tool here is **extension
methods on `IServiceCollection` and `IEndpointRouteBuilder`**, not
partial classes.

| New file | Lines (est.) | Responsibility |
|---|---|---|
| `Program.cs` (kept) | ~400 | `WebApplication.CreateBuilder`, all `builder.Services.AddPalLlm*()` calls, all `app.UsePalLlm*()` calls, all `MapPalLlm*Routes(api)` calls, `app.Run()` |
| `Configuration/PalLlmCoreServiceCollectionExtensions.cs` | ~300 | `AddPalLlmCore(IServiceCollection, IConfiguration)` — JSON options, problem details, response compression, options binding, validator, OutputCache |
| `Configuration/PalLlmInferenceServiceCollectionExtensions.cs` | ~300 | `AddPalLlmInference(...)` — every `HttpClient` for inference / vision / TTS / ASR, model tier orchestrator, collaboration planners |
| `Configuration/PalLlmObservabilityServiceCollectionExtensions.cs` | ~200 | `AddPalLlmObservability(...)` — metrics, performance tracker, health checks, OpenTelemetry |
| `Configuration/PalLlmRateLimitServiceCollectionExtensions.cs` | ~150 | `AddPalLlmRateLimiter(...)` + `AddPalLlmRequestTimeouts(...)` |
| `RouteRegistrations/HealthRoutes.cs` | ~150 | `MapPalLlmHealthRoutes(api)` — `/health`, `/health/live`, `/health/ready`, `/api/health`, `/api/quickstart`, `/api/describe`, `/api/features` |
| `RouteRegistrations/ChatRoutes.cs` | ~200 | `MapPalLlmChatRoutes(api)` — `/api/chat`, `/api/chat/stream`, related |
| `RouteRegistrations/BridgeRoutes.cs` | ~200 | `MapPalLlmBridgeRoutes(api)` — `/api/bridge/*`, `/api/outbox/*` |
| `RouteRegistrations/ModelRoutes.cs` | ~200 | `MapPalLlmModelRoutes(api)` — `/api/inference/*`, `/api/models/*` |
| `RouteRegistrations/ObservabilityRoutes.cs` | ~200 | `MapPalLlmObservabilityRoutes(api)` — `/api/metrics`, `/api/why/*`, `/api/release/*` |
| `RouteRegistrations/BridgeProofRoutes.cs` | ~200 | `MapPalLlmBridgeProofRoutes(api)` — `/api/bridge/proof`, native-proof + delivery surfaces |

**Why split routes by domain, not by HTTP method:** The audit gate
`Drift_Api_route_count` counts `api.Map*` calls regardless of where
they live; splitting by domain keeps related routes co-located the
same way an operator would expect them in `docs/API.md`.

**Verification checklist for Phase 2:**

- [ ] `Drift_Api_route_count` still reports `57` total routes
      (sum across all `RouteRegistrations/` files).
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
   functions, zero state). Quickest win, lowest risk.
2. **Phase 1b:** Extract `PalLlmRuntime.UiProbe.cs` (includes the 3
   nested classes — moving them gets the most lines for the least
   risk).
3. **Phase 1c:** Extract `PalLlmRuntime.Prompt.cs` (pure static
   builders, no state).
4. **Phase 1d:** Extract `PalLlmRuntime.BridgeBoot.cs`.
5. **Phase 1e:** Extract `PalLlmRuntime.Bridge.cs`.
6. **Phase 1f:** Extract `PalLlmRuntime.Outbox.cs`.
7. **Phase 1g:** Extract `PalLlmRuntime.Snapshot.cs`.
8. **Phase 1h:** Extract `PalLlmRuntime.Inference.cs` (largest, most
   stateful — land last when the pattern is well-rehearsed).

Each sub-pass should drop `PalLlmRuntime.cs` by `~400-800` lines and
add one companion file of similar size. After Phase 1h, the primary
`PalLlmRuntime.cs` is `~600` lines (constructor + chat entry +
partial declarations).

Phase 2 is similar but coarser-grained — 4 service-collection
extensions + 5 route-registration files. Land in that order.

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
