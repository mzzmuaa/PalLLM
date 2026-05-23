# PalLLM Harvest Guide

Last audited: `2026-05-21`

How to lift individual capabilities out of PalLLM into your own
project. The codebase is deliberately structured so most of the
interesting pieces are pure-.NET-10 with no Palworld, UE4SS, or
ASP.NET dependency — they harvest as single files.

## The three layers, and what they require

| Layer | Path | Dependencies | Portability |
|---|---|---|---|
| **Portable** | `src/PalLLM.Domain/Portable/` | None — pure interfaces | **Highest** — lifts to any .NET 6+ project |
| **Domain** | `src/PalLLM.Domain/` everything else | .NET 10 + minimal NuGet (`System.Text.Json`) | **High** — lifts to .NET 10 anywhere |
| **Sidecar** | `src/PalLLM.Sidecar/` | .NET 10 + ASP.NET Core + `ModelContextProtocol.AspNetCore` | Harvest the advisor, leave the route registration |

If you harvest the sidecar host shape, keep
`<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>`
in the destination web project. PalLLM's `PalLlmOptions` tree is large enough
that source-generated binding is part of the low-reflection, trim-friendly
startup posture, while `PalLlmOptionsValidator` remains the semantic guardrail
for invalid operator config.

If you harvest domain persistence or transport helpers, copy
`src/PalLLM.Domain/PalLlmDomainJsonSerializerContext.cs` with the relevant
contract types or provide an equivalent `JsonSerializerContext` in the
destination project. The bridge, session, pack, proof-packet, and opt-in
HTTP request-body helpers now rely on source-generated `JsonTypeInfo` rather
than reflection fallback.

## Ready-to-harvest capabilities

Each entry below is **one file** you can copy into your project. The
table lists the file, what it does, any NuGet dependencies, and the
minimum public surface.

### Pure-deterministic advisors (just pure functions)

| Capability | File | Dependencies | Public surface |
|---|---|---|---|
| **Chat task-kind inference** from an utterance | `src/PalLLM.Domain/Inference/ChatTaskKindInferer.cs` | None | `DuoTaskKind Infer(string? userMessage, string? taskTag = null)` |
| **Duo cooperation-pattern planner** (10 patterns) | `src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs` | `ModelRoleRegistry.cs` | `DuoPlan Plan(DuoPlanRequest)` |
| **Concrete role-chain dispatch** advisor | `src/PalLLM.Domain/Inference/ChatDispatchPlanner.cs` | `ModelRoleRegistry.cs` | `ChatDispatchDecision Decide(pattern, coverage)` |
| **Disagreement detector** (semantic + token + length) | `src/PalLLM.Domain/Runtime/DisagreementDetector.cs` | `Portable/PortableAdapterContracts.cs` (for `SemanticEmbedder`) | `DisagreementAnalysis Compare(workerOutput, judgeOutput)` |
| **Proof packet builder** (provenance bundle) | `src/PalLLM.Domain/Runtime/ProofPacketBuilder.cs` | None | `ProofPacket Build(...)` |
| **Why engine** (natural-language causal answers) | `src/PalLLM.Domain/Runtime/WhyEngine.cs` | Health record types | `WhyAnswer Answer(question, health, metrics, score, worldSnapshot?)` |
| **Hardware profiler** (OS-backed cores / RAM / GPU-architecture detect) | `src/PalLLM.Domain/Inference/HardwareProfiler.cs` | None | `HardwareProfile Capture(string? forceTier = null)` |
| **Graceful degradation advisor** | `src/PalLLM.Domain/Runtime/GracefulDegradationAdvisor.cs` | `HardwareProfiler` | `DegradationAdvisory Recommend(profile, options)` |
| **Privacy posture builder** (data-flow inventory) | `src/PalLLM.Domain/Runtime/PrivacyPostureBuilder.cs` | `PalLlmOptions` — swap for your own config | `PrivacyPosture Capture(options)` |
| **World-narration cue advisor** | `src/PalLLM.Domain/Runtime/WorldNarrationAdvisor.cs` | `GameWorldSnapshot` | `NarrationCue Advise(snapshot, lastNarrationUtc)` |
| **Directive intent translator** (NL → allowlisted actions) | `src/PalLLM.Domain/Runtime/DirectiveIntentTranslator.cs` | None | `DirectivePlan Translate(utterance, allowedActions, addressedPal?)` |
| **Mood-weather advisor** per character | `src/PalLLM.Domain/Runtime/MoodWeatherAdvisor.cs` | `CharacterRelationship` + `GameWorldSnapshot` | `MoodWeather Forecast(relationship, snapshot)` |
| **Operator health scorer** (0-100 grade) | `src/PalLLM.Domain/Runtime/OperatorHealthScorer.cs` | `RuntimeHealth` | `OperatorHealthScore Score(health)` |

### Stateful advisors (bounded in-memory state)

| Capability | File | Dependencies | Notes |
|---|---|---|---|
| **Promotion ledger** (rolling window + stability gate) | `src/PalLLM.Domain/Runtime/PromotionLedger.cs` | None | Observer pattern, bounded memory |
| **Lifetime relationship aggregator** | `src/PalLLM.Domain/Runtime/LifetimeRelationshipAggregator.cs` | `CharacterRelationship` | Persistence caller's responsibility |
| **Chat rate limiter** (per-key sliding window) | `src/PalLLM.Domain/Runtime/ChatRateLimiter.cs` | None | Drop-in for any per-key throttle |
| **Inference circuit breaker** (3-state) | `src/PalLLM.Domain/Inference/InferenceCircuitBreaker.cs` | None | Classic CB, no framework deps |
| **Thermal gate** (GPU throttle short-circuit) | `src/PalLLM.Domain/Runtime/ThermalGate.cs` | BCL + optional `nvidia-smi` on PATH | Cross-platform best-effort |
| **Inference performance tracker** (p95 histograms) | `src/PalLLM.Domain/Runtime/InferencePerformanceTracker.cs` | None | Rolling-window sample store |

### Low-allocation ingress helpers

| Capability | File | Dependencies | Notes |
|---|---|---|---|
| **Bounded local JSON reader** | `src/PalLLM.Domain/Runtime/BoundedJsonFileReader.cs` | `System.Text.Json` | Sequential shared reads + stable `oversized` / `malformed` / `unreadable` outcomes |
| **Bounded local text reader** | `src/PalLLM.Domain/Runtime/BoundedTextFileReader.cs` | None | Sequential shared reads + stable `oversized` / `unreadable` outcomes |
| **Bounded pooled base64 file reader** | `src/PalLLM.Domain/Runtime/BoundedBase64FileReader.cs` | None | Sequential shared reads + `ArrayPool<byte>` growth for image/file handoff surfaces |

### Builders (take inputs, return immutable snapshot records)

| Capability | File | Dependencies | Notes |
|---|---|---|---|
| **Promotion apply preview builder** | `src/PalLLM.Domain/Runtime/PromotionApplyPreviewBuilder.cs` | `PromotionSuggestion` | Pure function |
| **Promotion suggestion builder** | `src/PalLLM.Domain/Runtime/PromotionSuggestionBuilder.cs` | `PromotionSummary` | Pure function |
| **Promotion applier** (staging-only file write) | `src/PalLLM.Domain/Runtime/PromotionApplier.cs` | `PromotionApplyPreview` + `System.IO` | Never mutates source |
| **Resource-budget posture builder** | `src/PalLLM.Domain/Runtime/ResourceBudgetPostureBuilder.cs` | Options + metrics snapshot | Pure function |

### Validators

| Capability | File | Dependencies |
|---|---|---|
| **Narrative pack validator** | `src/PalLLM.Domain/Packs/NarrativePackValidator.cs` | None |
| **Personality pack validator** (v1 format + content-hash) | `src/PalLLM.Domain/Packs/PersonalityPack.cs` | `System.Security.Cryptography` |

### Integration contracts (wire types)

| File | Contains |
|---|---|
| `src/PalLLM.Domain/Integration/Contracts.cs` | `ChatRequest`, `ChatResponse`, `GameWorldSnapshot`, `BridgeEventEnvelope`, etc. |
| `src/PalLLM.Domain/Integration/PartyChatContracts.cs` | `PartyChatRequest`, `PartyChatResponse`, `PartyChatTurn` |

Copy these verbatim when you need PalLLM-shape records without the rest
of the runtime.

### The portable adapter seam

`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` is the
**redistributable seam**. Any game-agnostic integration target should
implement:

- `IGameAdapter` — per-frame world snapshot + bridge events
- `ICharacter` — single character data
- `IWorldClock` — in-game time
- `IPathProvider` — filesystem roots
- `ILogger` — log sink

Bundle this file alone + write your `BridgeGameAdapter` equivalent +
you've got a new game integration. The rest of PalLLM.Domain is
adapter-agnostic.

## Harvest recipes

### Recipe 1 — "I just want the chat-task inference + Duo planner"

```csharp
// Copy these 3 files verbatim into your project:
//   src/PalLLM.Domain/Inference/ChatTaskKindInferer.cs
//   src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs
//   src/PalLLM.Domain/Inference/ModelRoleRegistry.cs
//
// Update namespaces, add a tiny PalLlmOptions shim (just
// ModelRoles : List<ModelRoleBinding> suffices).

var kind = ChatTaskKindInferer.Infer("please audit this code");
// → DuoTaskKind.Audit

var planner = new DuoOrchestratorPlanner(new ModelRoleRegistry(yourOptions));
var plan = planner.Plan(new DuoPlanRequest { Kind = kind, Risk = DuoRiskLevel.Medium });
// → plan.Pattern, plan.Steps, plan.Escalation
```

### Recipe 2 — "I want the proof-packet provenance format"

```csharp
// One file:
//   src/PalLLM.Domain/Runtime/ProofPacketBuilder.cs
//
// Zero dependencies. The ProofPacket record is a stable contract —
// subsystem, decision, evidence, confidence, human-review flag,
// rollback path, SHA-256 id.

var packet = ProofPacketBuilder.Build(
    subsystem: "my-subsystem",
    decision: "accepted-proposal",
    primaryReason: "passed all gates",
    evidence: new[] { "gate-a: ok", "gate-b: ok" },
    rollbackPath: "git revert HEAD",
    confidence: 0.92,
    humanReviewRequired: false);
```

### Recipe 3 — "I want the promotion loop (observation → candidate → apply)"

Four files, in order:

```
src/PalLLM.Domain/Runtime/PromotionLedger.cs
src/PalLLM.Domain/Runtime/PromotionSuggestionBuilder.cs
src/PalLLM.Domain/Runtime/PromotionApplyPreviewBuilder.cs
src/PalLLM.Domain/Runtime/PromotionApplier.cs
```

All four are pure-.NET-10. Wire a background worker that calls
`ledger.Record(observation)` each time your system makes a
deterministic decision; check `summary.Tasks[i].IsPromotionCandidate`;
when true, build a suggestion → preview → apply.

### Recipe 4 — "I want the privacy-posture inventory for my ASP.NET app"

```
src/PalLLM.Domain/Runtime/PrivacyPostureBuilder.cs
```

Replace the `PalLlmOptions` references with your own config type.
Customise the surface list to reflect your own data-emitting paths.
The three-status taxonomy (`never-leaves` / `only-with-opt-in` /
`leaves-by-default`) is the portable bit.

### Recipe 5 — "I want the whole AI-mesh stack"

Copy all of `src/PalLLM.Domain/Inference/` + the half-dozen advisors
in `src/PalLLM.Domain/Runtime/` (see CODE_MAP.md "Role mesh + observability"
section). You'll have a working role-aware inference planner + disagreement
detector + proof-packet-emitting stack in ~20 files.

## What NOT to harvest

- **`PalLlmRuntime.cs`** — ~4,744 lines of orchestration glue.
  It composes every other piece; you don't want to copy it, you want
  to write your own orchestrator that calls the same underlying
  pieces.
- **`Program.cs`** — ~2,037 lines of route registration. Same reason.
- **`PalLlmFeatureCatalog.cs`** — the list is project-specific.
- **`BridgeGameAdapter.cs`** — Palworld-specific.
- **`mod/ue4ss/Mods/PalLLM/`** — UE4SS + Palworld target.

## Licence + attribution

PalLLM is MIT. Harvesting is explicitly invited. Keep the MIT
copyright notice on harvested files. See [`../LICENSE`](../LICENSE)
and [`../NOTICE.md`](../NOTICE.md).

If you build something interesting on top of a harvested piece, we'd
love to hear about it — open a Discussion / Issue on the GitHub
project once it's listed there.
