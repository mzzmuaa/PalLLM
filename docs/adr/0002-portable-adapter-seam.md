# ADR 0002 — Portable adapter seam

- **Status:** Accepted
- **Date:** 2026-04
- **Tags:** harvestability, layering, portability
- **Depends on:** none
- **Supports:** [`0003`](0003-one-way-advisory-bridge.md) (the bridge is the seam's first concrete consumer)

## Context

PalLLM ships with a Palworld-specific Lua bridge today, but the
runtime is shaped to be lifted into other game targets. A harvester
who wants the Duo orchestrator, the deterministic fallback, the
proof-packet pattern, or the role mesh shouldn't have to drag along
a Palworld dependency to get them.

The risk: if domain code accumulates references to game-specific
state ("nearest pal", "active base coordinates") it slowly turns
into a Palworld-only library — and the next person who wants to
harvest it has to rewrite half of it.

## Decision

`PalLLM.Domain` owns its portable adapter seam under
`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`. Five
interfaces describe everything domain code needs from a host game:

- `IGameAdapter` — top-level adapter handle; returns the other four
- `ICharacter` — addressable character (id, name, voice profile, mood)
- `IWorldClock` — wall-clock + in-game time
- `IPathProvider` — runtime root, models dir, packs dir, bridge dirs
- `ILogger` — minimal log surface

Every concrete consumer of "what's happening in the game" goes
through `IGameAdapter`. The Sidecar project supplies a
Palworld-specific implementation (via the Lua bridge file
producer/consumer); the domain project never references it.

`PalLLM.Domain.csproj` deliberately has **no** project reference to
the Sidecar or the UE4SS layer. Build the domain in isolation and it
compiles to a self-contained NuGet-shape DLL.

## Alternatives considered

- **One assembly, factor later.** Tempting in a small repo; lethal
  once the codebase is large. By the time you need the seam, the
  cross-cutting references are everywhere and the refactor takes
  weeks. We paid the up-front cost.
- **Inversion-of-control container with reflection-based discovery.**
  Adds ceremony for no gain — there are five interfaces, two
  implementations (Palworld + the in-memory test fake), and the
  binding happens once at startup. Constructor injection beats DI
  containers here.
- **Pinned adapter as an interface inside `PalLlmRuntime`.** Mixes
  layers. Anyone harvesting just the runtime would inherit the pinned
  shape. The standalone interfaces compose better.

## Consequences

**Positive:**
- A harvester implements 5 small interfaces and gets the entire
  PalLLM runtime in their own game. `docs/HARVEST.md` § "Lift the
  runtime into a different game" walks through this end-to-end.
- The domain project's tests can use an in-memory adapter fake. No
  game required to run `dotnet test`.
- Drift gates can enforce the seam: any new domain-code reference to
  Palworld-specific types would be a build-time failure (the project
  doesn't reference them, so the symbol wouldn't resolve).

**Negative:**
- Two projects to think about (Domain + Sidecar). Slightly higher
  cognitive load for "where does this go?" in early code.
- Adapter interface evolution is a real concern — adding a new method
  to `ICharacter` is a breaking change for any external harvester
  who has implemented their own. We treat the Portable surface as a
  semver-style public API.

## Harvest hint

The literal seam file is the harvest target:
`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`. Implement
the five interfaces against your target environment and pass the
`IGameAdapter` to `PalLlmRuntime`'s constructor. Everything else
(memory, fallback, presentation, vision orchestration, TTS, role
mesh, promotion ledger, advisors, builders, validators) comes
along.

## Related

- Code: `src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`
- Docs: [`HARVEST.md`](../HARVEST.md) (the harvesting recipe doc),
  [`ARCHITECTURE.md`](../ARCHITECTURE.md) § "Layering",
  [`DESIGN_PRINCIPLES.md`](../DESIGN_PRINCIPLES.md) § 4
  ("Harvestable by design")
