# Hot path — performance budgets

Last audited: `2026-05-22`

PalLLM is a real-time companion runtime: a player asks a question,
the companion replies. Anything that delays a reply is noticed. This
doc lists the methods that must stay fast and names a target budget
for each — when a future refactor touches one of them, this is the
yardstick.

The budgets aren't enforced in CI (they'd be flaky on shared CI
hardware). They're a *design contract* — when you add work to a
hot-path method, ask "does this keep me under budget on a target-tier
machine?" If you're not sure, profile with the tracing setup in
[`OBSERVABILITY.md`](OBSERVABILITY.md) and read the `Chat.Turn` span
duration directly.

## Target tiers

Budgets are stated against three baseline machines. PalLLM auto-detects
which tier you're on (`HardwareProfiler.Capture()` →
`DuoHardwareTier`); operators can override via
`PalLLM:Hardware:ForceTier`.

| Tier | Profile | Inference target | Local model |
|---|---|---|---|
| `Constrained` | 4-core CPU, 8 GiB RAM, no GPU or entry-level | n/a or `gemma3:1b` | Lean |
| `Standard` | 8-core CPU, 16-32 GiB RAM, single mid GPU | `qwen3.6:0.6b` to `gemma3:4b` | Studio |
| `Generous` | 16-core CPU, 64+ GiB RAM, multi-GPU | `qwen3.6:35b-a3b` and up | Workstation |
| `Blackwell` | 5090 / B-series with NVFP4 + vLLM | dense MoE / NVFP4 70B-class | Frontier |

### End-to-end chat-turn budgets

Authoritative source: the budget hashtable in
[`scripts/pal-benchmark.ps1`](../scripts/pal-benchmark.ps1).
`pal benchmark` reads each tier's budget from this table at run
time, so the numbers below are the same numbers the verb prints.

| Tier | Cold (first turn) | Warm (subsequent) | Notes |
|---|---|---|---|
| `Constrained` | `< 4000 ms` | `< 1500 ms` | Older laptop / no GPU; deterministic-first stays under 200 ms even here |
| `Standard` | `< 2500 ms` | `< 900 ms` | Mainstream desktop / mid-range GPU |
| `Generous` | `< 1500 ms` | `< 600 ms` | 4070-class / 24+ GiB VRAM |
| `Blackwell` | `< 900 ms` | `< 450 ms` | 5090 / B-series with NVFP4 |

`pal benchmark` warns when measured `median` exceeds `WarmMs * 1.2`,
calls `delivery_proven` if `cold` is within `ColdMs`, and falls back
to the `Standard` tier if hardware detection fails.

Budgets below give a cold and warm number. Cold = first call after a
process boot. Warm = subsequent calls within the cache window
(the TTL-cache pattern from
[`adr/0005-ttl-cache-for-posture-surfaces.md`](adr/0005-ttl-cache-for-posture-surfaces.md)).

## Chat path

| Method | File | Cold | Warm | Notes |
|---|---|---|---|---|
| `PalLlmRuntime.ChatAsync` (deterministic) | `PalLlmRuntime.cs` | < 200 ms | < 100 ms | No external calls; runs on every tier |
| `PalLlmRuntime.ChatAsync` (with inference) | `PalLlmRuntime.cs` | < 2.5 s | < 2 s | Standard tier with `qwen3.6:0.6b` baseline |
| `PalLlmRuntime.ChatAsync` (party fan-out, 4 chars) | `PalLlmRuntime.cs` | < 600 ms | < 400 ms | Deterministic only; inference path scales linearly |
| `ChatDispatchPlanner.Plan` | `ChatDispatchPlanner.cs` | < 5 ms | < 1 ms | Pure deterministic planning |
| `FallbackBehaviorEngine.CreateGeneralDirector` | `FallbackBehaviorEngine.cs` | < 30 ms | < 10 ms | Per-turn director assembly |
| `PresentationCuePlanner.Plan` | `PresentationCuePlanner.cs` | < 20 ms | < 5 ms | Pure deterministic; called once per chat reply |

Budget violations on the chat path are the highest-priority bugs in
the runtime. If you can't keep `ChatAsync` deterministic under
200 ms, something has been added that doesn't belong there — either
move it async, cache it, or push it into a background worker.
Payload-size regressions count too: the hot path caps the built system
prompt at `16,000` characters, live assistant reply text at `8 KiB`, and each
remembered memory entry at `4 KiB` so local-model or bridge display glitches do
not turn one chat into a large response or future prompt tax.

Prompt-order regressions count as latency regressions. The system prompt keeps
the stable companion contract, character identity, traits, skills, and authored
pack lore before the volatile `Turn context` block (`Task tag`, world state,
visual context, relationship, and memory snippets). This gives local servers
with prefix/KV-cache support a longer reusable head across repeated turns for
the same companion while keeping fast reactive lanes compact.

## World snapshot

| Method | File | Cold | Warm | Notes |
|---|---|---|---|---|
| `PalLlmRuntime.GetWorldSnapshot` | `PalLlmRuntime.cs` | < 50 ms | < 20 ms | Snapshot of current bridge state |
| `BridgeProofBuilder.Build` | `BridgeProofBuilder.cs` | < 100 ms | < 30 ms | Serializes recent bridge events |

## Health / posture

| Method | File | Cold | Warm | Notes |
|---|---|---|---|---|
| `PalLlmRuntime.GetHealth` | `PalLlmRuntime.cs` | < 20 ms | < 5 ms | Assembles `RuntimeHealth` |
| `OperatorHealthScorer.Score` | `OperatorHealthScorer.cs` | < 1 ms | < 1 ms | Pure arithmetic |
| `HardwareProfiler.CaptureCached` | `HardwareProfiler.cs` | < 20 ms | < 1 ms | TTL 5 min; cold path uses OS-backed RAM + bounded GPU probes |
| `PrivacyPostureBuilder.CaptureCached` | `PrivacyPostureBuilder.cs` | < 5 ms | < 1 ms | TTL 30 s |
| `ResourceBudgetPostureBuilder.CaptureCached` | `ResourceBudgetPostureBuilder.cs` | < 3 ms | < 1 ms | TTL 15 s |
| `AirGapVerifier.VerifyCached` | `AirGapVerifier.cs` | < 50 ms | < 1 ms | TTL 30 s; cold cost is DNS |
| `SelfDescriptionBuilder.Build` | `SelfDescriptionBuilder.cs` | < 5 ms | < 1 ms | Cached at HTTP layer via output cache |

The posture builders are the canonical TTL-cache surfaces. Their
warm path is fast enough that polling once per second from the
dashboard is fine. Their cold path is bounded by OS calls (CPU
count, RAM probe, GPU marker file probe).

## Bridge

| Method | File | Cold | Warm | Notes |
|---|---|---|---|---|
| `BridgeInboxWorker.ExecuteAsync` (per envelope) | `BridgeInboxWorker.cs` | < 100 ms | < 50 ms | Single envelope process |
| `PalLlmRuntime.WriteOutboxAsync` | `PalLlmRuntime.cs` | < 20 ms | < 10 ms | Per envelope written |
| `DirectoryRetention.Enforce` | `DirectoryRetention.cs` | < 30 ms | < 10 ms | Lazy per-directory sweep; age delete + bounded newest-file queue |

Bridge polls run on a 1-second cadence by default
(`Bridge:PollIntervalMs = 1000`). The 100 ms per-envelope budget
gives the worker headroom to process up to ~10 envelopes per poll
without falling behind, which matches the
`MaxEventsPerPoll = 32` default cap (a backlog beyond that is rare
and will catch up across poll ticks).

## Memory

| Method | File | Cold | Warm | Notes |
|---|---|---|---|---|
| `ConversationMemoryStore.Recall` | `ConversationMemoryStore.cs` | < 10 ms | < 5 ms | Pooled snapshot recall + stack-bounded exact-token rerank |
| `ConversationMemoryStore.Record` | `ConversationMemoryStore.cs` | < 5 ms | < 2 ms | Append + bump mutation version |
| `ConversationMemoryStore.PersistAsync` (autosave) | `ConversationMemoryStore.cs` | < 30 ms | < 10 ms | Skips when mutation version unchanged |
| `RelationshipTracker.RecordInteraction` | `RelationshipTracker.cs` | < 5 ms | < 2 ms | Lock + dictionary update |
| `MemoryImportanceCalculator.Score` | `MemoryImportance.cs` | < 1 ms | < 1 ms | Pure scoring |

## Vision / TTS / Inference (opt-in)

These are network-bound, so the runtime budget is bounded by the
HTTP timeout configuration, not by PalLLM code. The relevant knobs:

| Setting | Default | Bound |
|---|---|---|
| `Inference:TimeoutSeconds` | 60 | Hard upper bound on a chat turn that reaches inference |
| `Vision:TimeoutSeconds` | 30 | Hard upper bound on `/api/vision/describe` |
| `Tts:TimeoutSeconds` | 30 | Hard upper bound on `/api/tts/synthesize` |

The runtime budget *around* these calls (assembly, response
shaping, persistence) is < 100 ms cold / < 50 ms warm. If a vision
call returns in 200 ms, the route should respond in 250 ms — not
500 ms.

The portable `PalLLM.Domain` project also opts into .NET's AOT compatibility
analyzers with `IsAotCompatible=true`. That does not make the sidecar publish
as Native AOT by default; it keeps the harvestable chat, memory, bridge, pack,
and transport code warning-clean for trimming, single-file, and native-publish
experiments while the proven packaged-EXE path remains the release default.

## Startup / configuration

Startup is not a per-turn hot path, but it controls first-click latency for the
packaged player flow. The sidecar binds the large `PalLLM` configuration tree
through .NET's configuration binding source generator, then runs
`PalLlmOptionsValidator` before serving routes. Keep that project setting in
place when adding new option blocks: it preserves the same operator-facing
config keys while avoiding reflection-heavy startup binding and keeping the
host friendlier to trimming/AOT experiments.

Use `pwsh ./pal.ps1 aot-readiness` before any publish-mode change. The default
scan is local-only and static: it checks the sidecar target framework, source-
generated configuration binding, the domain analyzer opt-in, JSON serializer
contexts, common sidecar source-generated payload shapes, Minimal API host
shape, dynamic-code markers, and AOT-review dependency surface. Use
`-PublishProbe` only on a machine with the native compiler prerequisites
installed; that switch runs the expensive Native AOT publish experiment and
writes logs under `artifacts/aot-readiness/`.

## Cold-start (Pass 360)

The end-to-end "clone → first chat reply" path. Measured by
`scripts/pal-benchmark-coldstart.ps1` (verb: `pal benchmark cold-start`).
Distinct from the per-turn budgets above — cold-start is the
one-shot operator UX number, not a sustained workload.

| Phase                              | Reference-rig budget | What it covers                                                          |
|------------------------------------|----------------------|-------------------------------------------------------------------------|
| `dotnet build` (cold, `-IncludeBuild`) | < 45 s            | Full Release build of `PalLLM.sln` from clean intermediate state.       |
| `dotnet run` → `/health/live` 200  | < 8 s                | Sidecar process start + DI graph + config binding + middleware mount.  |
| `/health` → `/api/chat` first reply| < 10 s combined       | First chat turn served by the deterministic-fallback director (no LLM). |

The chat-reply budget is "first chat after ready" — not "first chat
including ready." Combined `ready + first-chat` budget therefore
< 10 s, of which < 8 s is sidecar startup and < 2 s is the first
fallback turn (most of which is bridge dir init + memory store first-touch).

Run it:

```powershell
pwsh ./scripts/pal-benchmark-coldstart.ps1
# Writes artifacts/cold-start-benchmark/<ts>.json + prints:
# ColdStart: build=skipped ready=Xs chat=Ys
```

Add `-IncludeBuild` for the full clone-to-chat path including the
Release build. The artifact JSON captures the host OS / arch / CPU
count so cross-rig comparisons stay honest.

## JSON contract metadata

Domain JSON hot paths should stay on source-generated `JsonTypeInfo` overloads.
That includes bridge inbox/outbox files, session persistence, lifetime
relationship snapshots, promotion proof packets, pack manifests, and opt-in
inference/vision/TTS HTTP request bodies. Avoid reintroducing
`DefaultJsonTypeInfoResolver`, options-only serializer overloads, or anonymous
`JsonContent.Create(...)` bodies on these paths unless a trim/AOT probe proves
the new shape remains warning-free.

Sidecar-only operator surfaces should follow the same direction even when the
broader sidecar still keeps its deliberate fallback resolver. Health probe data,
chat SSE progress frames, MCP status payloads, self-healing pending markers, and
tiny command responses should use named DTOs plus source-generated metadata
instead of `object` dictionaries or anonymous payloads.

## How to verify

1. Wire up tracing per [`OBSERVABILITY.md`](OBSERVABILITY.md).
2. Open Jaeger, find the relevant span, read the duration.
3. Compare to the budget above. If the warm number is over budget
   on a target-tier machine, the change you just made owes a
   profiling note.

For the chat path specifically, the audit script's smoke test
includes a deterministic round-trip against a running sidecar; on
a Standard-tier dev box it consistently lands inside budget. If
your local run is materially slower, something is wrong (often:
antivirus scanning the sidecar binary on every boot, or a
`runtime-root/` directory on a slow disk).

## Related

- Code: `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` (the chat
  path), `src/PalLLM.Domain/Inference/HardwareProfiler.cs` (tier
  detection)
- Docs: [`OBSERVABILITY.md`](OBSERVABILITY.md) (how to measure),
  [`adr/0005-ttl-cache-for-posture-surfaces.md`](adr/0005-ttl-cache-for-posture-surfaces.md)
  (the cache pattern), [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md)
  § "Performance"
