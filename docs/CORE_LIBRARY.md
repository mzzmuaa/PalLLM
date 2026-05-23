# Portable Adapter Surface

Last audited: `2026-05-07`

PalLLM owns its own portable adapter surface inlined at
[`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`](../src/PalLLM.Domain/Portable/PortableAdapterContracts.cs).
The runtime carries no project reference to any external library; the
published binary is redistributable on its own.

This document records which types live in that file and what contract
they represent, so anyone reading the code knows exactly which seams
are portable (reusable across games) vs Palworld-specific.

## What the portable surface provides

All in namespace `PalLLM.Domain.Portable`, all in a single file:

- `Vec3` - a 3D world-space coordinate struct with an `Invalid` sentinel
  (NaN) and an `IsValid` check. Engine-neutral.
- `ICharacter` - read-only view of one in-game character: id, display
  name, alive/faction/incapacitated flags, position, age, skills,
  needs, traits.
- `IWorldClock` - in-game time exposed as adapter-native ticks, plus
  derived `TicksPerHour` / `TicksPerDay` ratios.
- `IPathProvider` - runtime-root / models / TTS / pack directories.
- `ILogger` - three-level log sink (info / warning / error).
- `IGameAdapter` - the whole seam: name + logger + clock + paths +
  characters + readiness flag.
- `SemanticEmbedder` - deterministic hashed bag-of-tokens embedder
  with cosine similarity. Pure C#, no external dependency, sub-
  millisecond per call. Backs conversation-memory recall.
- `ResponseCleanup` - strips model-emitted `<think>...</think>` and
  related reasoning tags from completion text before it reaches the
  player-visible reply.

## What Palworld-specific code maps in

[`src/PalLLM.Domain/Integration/BridgeGameAdapter.cs`](../src/PalLLM.Domain/Integration/BridgeGameAdapter.cs)
implements the portable interfaces with the current bridge-backed concrete
adapter types (`BridgeGameCharacter`, `SnapshotWorldClock`, `RuntimePathProvider`,
`AdapterLogger`) and the neutral `GameWorldSnapshot` data model in
`Contracts.cs` for the UE4SS bridge payloads.

Everything in `Portable/` is game-agnostic. Everything in
`Integration/` is Palworld-specific.

The sidecar's publication-facing HTTP/OpenAPI contract now maps the
bridge-owned snapshot family to neutral schema ids
(`GameWorldSnapshot`, `GameBaseSnapshot`, `GameCharacterSnapshot`) so SDKs and
external tools do not have to depend on the current bridge target's internal
CLR type names.

## Why the surface is inlined rather than referenced

An earlier revision pulled these types from a sibling core project via
`ProjectReference`. That coupling created two real
problems:

1. **Redistributability** - the release ZIP could not build from source
   without cloning the sibling repo alongside.
2. **Refactor churn** - when the sibling project's Claude sessions
   reshuffled its namespaces, PalLLM's build broke mid-session even
   though no PalLLM code had changed.

Inlining the minimal surface (one file, ~250 lines) makes PalLLM fully
self-contained while keeping the portable-adapter abstraction intact.
A future second game adapter (e.g., a different sandbox / survival
title) implements the same interfaces in its own `*Adapter.cs`; the
runtime doesn't need to change.

## Re-harvest contract for other programs

The portable surface is intentionally simple and documented so other
LLM-companion runtimes can **copy the file verbatim**, rename the
namespace, and implement the interfaces against their own game
adapter. No runtime licensing or attribution is required beyond the
project's MIT LICENSE and the `NOTICE.md` disclaimer.

## What is deliberately NOT in the portable surface

- Any bridge-specific data shape (`GameWorldSnapshot`,
  `GameBaseSnapshot`, etc.) - those live in
  `src/PalLLM.Domain/Integration/Contracts.cs`.
- Bridge-event schemas - also in `Contracts.cs`, not portable.
- Prompt building, fallback director, presentation planner, narrative
  packs - those are PalLLM-specific runtime and do not need to be
  reusable across games.

See [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md) for the
third-party NuGet packages PalLLM calls at runtime.
