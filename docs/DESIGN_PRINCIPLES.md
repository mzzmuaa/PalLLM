# PalLLM Design Principles

Last audited: `2026-05-22`

Why the codebase is shaped the way it is. Read this before proposing
an architectural change - if a principle here is in your way, it's
probably doing load-bearing work and the right move is to understand
it before routing around it.

These principles are ordered. Earlier ones win when they conflict
with later ones.

## 1. Deterministic-first

**Every capability lands as a pure function first. THEN gets an
HTTP surface, MCP tool, and dashboard chip layered on top.**

Why: the drift audit can only mechanically verify what is
deterministic. A pure function gives us unit tests that pin
behaviour forever. Side-effectful code layered on top inherits that
contract.

Example: `WorldNarrationAdvisor.Advise(snapshot, lastNarrationUtc)` is
a pure function. The HTTP endpoint at `GET /api/narration/cue` just
forwards to it. The MCP tool `pal_narration_cue` does the same. All
three callers agree because they call the same function.

**Consequence for contributors:** when adding a capability, write the
static class with the `(Inputs) -> Record` signature first. Add tests
against it. Only then wire the HTTP/MCP/dashboard surfaces.

## 2. Observer-only, never destructive

**Feeders, watchdogs, and advisors read counters and write bounded
records. They never mutate `ChatAsync`, restart the sidecar, or
delete a file. Destructive recovery stays explicitly with
`recover.bat`.**

Why: the runtime has to stay trustworthy. A watchdog that "helpfully"
restarted the sidecar on a false alarm would break the companion
mid-conversation. Instead, the `SelfHealingWorker` writes
observations to `Runtime/SelfHealing/` and exposes them via
`GET /api/self-healing/status`. A human (or `recover.bat`) decides
what to do about them.

**Consequence for contributors:** anything you add that looks like
"clean up X" or "restart Y when Z" belongs in `recover.bat`, not in
runtime code.

## 3. Every automated change gets a proof packet

**When the system makes a decision on behalf of the operator, it
emits a `ProofPacket` via `ProofPacketBuilder.Build(...)` with a
stable SHA-256 id.**

Why: automated decisions that can't be audited are invisible
decisions. A player can't know why their companion acted a certain
way if there's no trail. Proof packets give every automated subsystem
a uniform provenance format: subsystem + decision + evidence +
confidence + rollback path + human-review flag.

**Consequence for contributors:** if your new feature makes a
decision without explicit player input, it emits a proof packet.
Reuse `ProofPacketBuilder` - don't invent a new provenance format.

## 4. Local-first, opt-in-only outbound

**Default install is fully local. Zero outbound network traffic
unless the operator explicitly configures live inference / vision
/ TTS / OTLP endpoints.**

Why: PalLLM runs on a player's own machine. The player has not
consented to their chat history leaving that machine. Every
outbound surface is a deliberate operator choice with a
documented config key.

This is enforced by the `/api/airgap/verify` endpoint (classifies
every configured outbound endpoint as `loopback` / `private-lan` /
`public-internet` / `disabled`) and the `/api/privacy/posture`
endpoint (enumerates every data-emitting surface with three-status
classification).

**Consequence for contributors:** any new feature that could emit
network traffic needs (a) an explicit opt-in config key, (b) a row
in `PrivacyPostureBuilder`, and (c) a row in the Airgap-verifier's
surface list.

## 5. Count invariants are contracts

**Test count, route count, MCP tool count, feature-catalog count,
fallback strategy count - each is pinned by a drift gate in
`scripts/run_full_audit.ps1`.**

Why: docs drift silently when counts are scattered across prose. The
16 drift gates catch every count disagreement mechanically. Contributors
know before pushing whether their change introduced drift.

**Consequence for contributors:** if you add a new HTTP route, the
audit will fail until you update `README.md`, `docs/ROADMAP.md`,
`docs/ARCHITECTURE.md`, and `docs/API.md`. That's a feature, not a
bug. See `CONVENTIONS.md` Section "HTTP routes" for the sync checklist.

## 6. Three parallel surfaces for every capability

**HTTP endpoint + MCP tool + (sometimes) dashboard chip.**

Why: different consumers prefer different surfaces. An operator
scripting against the sidecar wants HTTP. An MCP-aware AI client
wants the tool. A human reading the dashboard wants a chip.
PalLLM commits to surfacing every capability through all three
when the capability is observable, read-only, and useful outside
the game loop.

The feature catalog in
[`PalLlmFeatureCatalog.cs`](../src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs)
(surfaced via `GET /api/features` and the dashboard) is the living
record of which capability has which combination of HTTP / MCP /
dashboard surfaces.

**Consequence for contributors:** a new capability should land all
three surfaces in the same PR if all three make sense. If only the
HTTP surface makes sense (e.g. a write endpoint that requires
confirmation), that's fine - document why the MCP tool is absent.

## 7. Harvestable by design

**`src/PalLLM.Domain/` has no ASP.NET or UE4SS dependency. Every
advisor, builder, validator, and feeder is a single-file capability
with a minimal public surface, designed to lift into another
project with namespace rename + minor adaptation.**

Why: most of the interesting pieces (Duo planner, proof packets,
promotion ledger, disagreement detector, privacy posture) are
general-purpose. Tying them to Palworld would be wasteful. Keeping
the Domain layer clean costs nothing at build time and earns us
community reuse.

See `HARVEST.md` for the recipe catalogue.

**Consequence for contributors:** when in doubt, put a new
capability in `PalLLM.Domain`. Only reach for `PalLLM.Sidecar` if
you literally need an ASP.NET dependency.

## 8. Cache TTLs over recomputation on hot paths

**For pure-but-expensive inputs (hardware probe, privacy posture,
budget snapshot), cache the result with an explicit TTL via the
`CaptureCached(key, ttl)` pattern.**

Why: HTTP endpoints can be hit many times per second by dashboard
polls. Recomputing a 5-20ms probe on every hit is wasteful when
the result changes at most every few minutes.

The pattern: a single `private static volatile TCacheEntry? _cached;`
slot, a TTL check, a recompute on miss. Never introduces locks on
the happy path; benign double-compute on cold start is harmless
when the result is deterministic.

Example: `HardwareProfiler.CaptureCached` (Pass 44). Extending
the pattern to `PrivacyPostureBuilder` + `ResourceBudgetPostureBuilder`
is tracked separately.

**Consequence for contributors:** if you add a read-heavy endpoint
that computes from deterministic inputs, reach for the cached
pattern. Don't introduce a full-blown `IMemoryCache` dependency
unless you genuinely need per-key TTL + eviction policy.

## 9. Single source of truth for counts

**Code is truth. Docs echo.**

- Route count -> `src/PalLLM.Sidecar/Program.cs` +
  `src/PalLLM.Sidecar/RouteRegistrations/*.cs` (count of `api.Map*`)
- Feature count -> `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` (count of `Id = "..."`)
- Test count -> `tests/PalLLM.Tests/*.cs` (count of `[Test]`)
- MCP tool count -> `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs` (count of `[McpServerTool`)
- OpenAPI -> `docs/openapi/palllm-sidecar-v1.json` (regenerated by `scripts/export-openapi.ps1`)

Every doc number is a best-effort echo of these. The audit pipeline
enforces the echo.

## 10. No mock network in tests

**Integration tests boot an in-process sidecar with
`Inference.Enabled = false`, `Vision.Enabled = false`,
`Tts.Enabled = false`. That's the reference test posture.**

Why: mocking HTTP clients means we're testing the mock, not the
system. The deterministic fallback path is what every player hits
by default and is what CI exercises. When we need to test the
live-inference path, we use a real tiny in-process server, not a
mock.

**Consequence for contributors:** don't add `Moq` or `NSubstitute`.
When you need a test double, write a minimal in-project
implementation of the interface (see `DisabledInferenceClient`).

## Industry parallels (for the curious)

- **Hexagonal / Clean Architecture** - Portable / Domain / Application
  layers; outer layers depend on inner ones. See the folder
  structure in `CODE_MAP.md`.
- **Simple Made Easy** (Rich Hickey, 2011) - prefer simple over
  easy. Deterministic-first is the clearest expression.
- **12-Factor App** - local-first design matches the "dev/prod
  parity" factor; stateless workers match the "processes" factor.
- **Diataxis** (Procida) - docs organised as Tutorial / How-To /
  Reference / Explanation. See `docs/INDEX.md`.
- **llms.txt** (Jeremy Howard, 2024) - the `llms.txt` at repo root
  is the machine-readable discoverability index for AI agents.
- **AGENTS.md convention** - emerging 2024-2025 standard from agent
  tooling (OpenAI Codex, Anthropic Claude Code, Cursor, Aider). The
  root `AGENTS.md` is the canonical agent entrypoint.

## When a principle is in your way

First ask: "is the thing I'm trying to do actually important, or am
I rebuilding something that already exists?" Check `CODE_MAP.md`.

If it's genuinely important and the principle is blocking: open an
issue, propose an amendment to this doc, and wait for buy-in before
changing the code. These principles hold the codebase together.


