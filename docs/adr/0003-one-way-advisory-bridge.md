# ADR 0003 — One-way advisory bridge between sidecar and game

- **Status:** Accepted
- **Date:** 2026-04
- **Tags:** safety, coupling, deployment
- **Depends on:** [`0002`](0002-portable-adapter-seam.md) (the bridge sits at the adapter seam)
- **Supports:** [`0006`](0006-opt-in-everything-by-default.md) (action-intent emission and execution are independently opt-in)

## Context

The sidecar runs as an out-of-process ASP.NET Core service. The
Palworld game process loads UE4SS, which loads the
`mod/ue4ss/Mods/PalLLM/Scripts/main.lua` bridge. These are two
completely separate runtime worlds — different memory spaces,
different language ecosystems, different update cycles, different
crash domains.

How they communicate is the load-bearing decision. The naive options
are bad:

- **In-process embed:** Faster, but every sidecar bug crashes the
  game and vice versa. Every redeploy of the sidecar requires a
  game restart.
- **Direct RPC from sidecar to game:** Sidecar would need an
  injected hook into Palworld's process. That's a security vector
  *and* a maintenance burden — every Palworld patch potentially
  breaks the hook.
- **gRPC / shared socket:** Same coupling problems as RPC, plus you
  need to keep both endpoints in lockstep on protocol versions.

## Decision

The sidecar and the Lua bridge communicate **only** through the
filesystem. Specifically:

- **Lua → Sidecar: `Bridge/Inbox/`.** The Lua bridge writes JSON
  envelopes (player utterances, game events). The sidecar's bridge
  drain worker polls the directory, parses, and processes.
- **Sidecar → Lua: `Bridge/Outbox/`.** The sidecar writes
  `OutboxEnvelope` JSON files (chat replies, presentation cues,
  optional action intents). The Lua bridge polls and renders.
- **Auxiliary: `Bridge/Archive/`, `Bridge/Failed/`,
  `Bridge/Screenshots/`, `Bridge/Diagnostics/`.** History,
  per-direction failure quarantine, screenshot pickup, widget probe
  diagnostics.

The bridge is **one-way at any moment**: the producer of a directory
never reads its own output, and the consumer never writes back into
the same directory. The bridge is **advisory**: the consumer (Lua,
typically) decides whether to render an outbox envelope or skip it.
Action intents are doubly advisory — even when emitted, the Lua
bridge has its own allowlist that can refuse.

## Alternatives considered

See "Context" — embed, RPC, and socket-based approaches all couple
the two processes' lifecycles together. The filesystem decouples
them: the game can crash without taking out the sidecar, the sidecar
can redeploy without disturbing the game session, and either side
can run in isolation for testing.

The cost we accepted: the filesystem isn't *fast* (think ~10ms
round-trip per envelope on Windows). For a 60Hz game loop polling
for chat replies, that's fine. For per-frame UI state sync, it
wouldn't be — but per-frame UI state is a different problem, solved
in-process by the Lua bridge talking directly to the UE4SS API.

## Consequences

**Positive:**
- Security boundary by construction: the sidecar literally cannot
  reach into Palworld's address space. The worst it can do is write
  malformed JSON, which the Lua bridge ignores.
- Independent crash domains: a sidecar bug doesn't crash the game,
  a game crash doesn't take out the sidecar (the sidecar just stops
  seeing inbox events).
- Independent deploy cycles: redeploy the sidecar without restarting
  Palworld; restart Palworld without restarting the sidecar.
- Audit trail by default: every bridge event is a JSON file, archived
  to `Bridge/Archive/` after processing. Operators can replay any
  envelope through `scripts/run-delivery-replay.ps1`.

**Negative:**
- Filesystem latency. Acceptable for chat-rate (~1Hz) traffic;
  unacceptable for per-frame UI state.
- Disk usage grows over time. Mitigated by retention caps on every
  bridge directory (`OutboxMaxFiles`, `ArchiveMaxFiles`, etc.) plus
  the self-healing watchdog that archives orphan envelopes.
- Two consumers must agree on JSON schema, and the schema change
  must be coordinated across two languages (C# and Lua). The
  `Drift_OpenApi_snapshot` gate covers the C# side; manual
  inspection of `main.lua` is required for the Lua side, called out
  in `CONTRIBUTING.md` § "Changing runtime contracts".

## Harvest hint

If you're harvesting the Outbox/Inbox pattern for any
two-process system: the producers' write paths are in
`PalLlmRuntime.WriteOutboxAsync` and the Lua bridge's
`emit_event(envelope)` helper. The drain side is
`BridgeInboxWorker.cs` (sidecar) and the polling loop in
`main.lua`. The retention math lives in `BridgeOptions` and
applies on every write — that's the part most "queue-on-disk"
tutorials skip and that you'll regret skipping the first time
the disk fills up.

## Related

- Code: `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`,
  `src/PalLLM.Sidecar/BridgeInboxWorker.cs`,
  `src/PalLLM.Domain/Configuration/PalLlmOptions.cs`
  (BridgeOptions section)
- Docs: [`ARCHITECTURE.md`](../ARCHITECTURE.md) "Bridge protocol",
  [`OPERATIONS.md`](../OPERATIONS.md) § "Disk layout"
