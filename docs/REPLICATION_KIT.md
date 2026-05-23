# PalLLM Replication Kit

Last audited: `2026-05-22`

> **Audience.** Any coding agent - including small / quantized
> models - that needs to **understand or rebuild PalLLM from
> documentation alone**. Read top to bottom in order. Each step
> is short. Each step names the canonical source you should
> consult and the one verifiable invariant you should expect.

PalLLM is intentionally designed to be **fully reconstructable
from this repo's text**. Every count is in a JSON file that's
drift-gated against the live code. Every architectural decision
is in an ADR. Every conceptual model is in a doc. Every contract
is in a JSON Schema. Nothing load-bearing exists only in
someone's head.

## TL;DR (60 seconds)

PalLLM is a local-first LLM companion runtime targeting Palworld
+ UE4SS, with a portable adapter seam that decouples the runtime
from the game host. **Three layers, three independent processes,
three independent crash domains:**

```text
Lua mod (UE4SS, Windows)  --events--> Bridge/Inbox/*.json -->
Sidecar (.NET 10 ASP.NET) --reply-->  Bridge/Outbox/*.json -->
Lua mod renders to Palworld native HUD / audio / actions
                                 |
                                 | (HTTP)
                                 v
                  Local inference engine (any OpenAI-compatible)
```

The sidecar is **production-ready by every conventional metric**
(zero warnings, 1154 tests, 16/16 drift gates, supply-chain
scanned). The remaining 23.8% to "100%" is the **in-game
experience**: native HUD bind, native audio, native actions, plus
a clean-machine release proof. All four require live hardware to
close.

## Reading order - minimum viable understanding

These eight files give you the **complete mental model** of the
project. Reading them top-to-bottom takes ~30 minutes and is
sufficient to make a competent change without reading code.

| # | File | What it answers | Time |
|---|---|---|---|
| 1 | [`agents.json`](../agents.json) | Every count, every entry point, every hard rule, every harvestable unit, every honest score, in one machine-readable manifest. | 1 min |
| 2 | [`PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) | The single source-of-truth scalar table (test count, route count, MCP tool count, etc.). Drift-gated against live code. | 30 sec |
| 3 | [`MENTAL_MODEL.md`](MENTAL_MODEL.md) | Five conceptual primitives (companion-not-chatbot, mailbox-not-phone, runtime-vs-host, inference-as-enrichment, deterministic-first). | 5 min |
| 4 | [`ARCHITECTURE.md`](ARCHITECTURE.md) | Complete topology with mermaid diagrams: portable adapters, runtime, sidecar, mod, data flow. | 10 min |
| 5 | [`CODE_MAP.md`](CODE_MAP.md) | "Where does X live?" - file/folder map with one-line annotations. | 5 min |
| 6 | [`ROADMAP.md`](ROADMAP.md) | Honest 76.2% position + per-phase breakdown of what's shipped vs what's pending. | 5 min |
| 7 | [`CONVENTIONS.md`](CONVENTIONS.md) | Advisor / builder / validator / feeder patterns. Every new code goes into one of these shapes. | 5 min |
| 8 | [`adr/`](adr/) | Six ADRs with the load-bearing decisions: deterministic-first, portable seam, advisory bridge, drift gates over manual review, TTL caches, opt-in-everything. | 10 min |

## Replicating from scratch

If you have only this repo's docs (no running build, no remote
server, no live answer), the following recipe rebuilds the
project. Every step has an exact command and an exact expected
output.

### 1. Prerequisites

| Tool | Version | Source |
|---|---|---|
| .NET SDK | 10.0+ | https://dotnet.microsoft.com/download/dotnet/10.0 |
| PowerShell | 5.1+ (7+ recommended) | Windows 10 ships with 5.1; install 7+ from https://learn.microsoft.com/powershell/scripting/install/installing-powershell |
| Lua (for mod) | 5.4 via UE4SS | https://github.com/UE4SS-RE/RE-UE4SS |
| Git | any recent | https://git-scm.com/ |

The sidecar runs on **Windows / Linux / macOS / containers**.
The Lua mod runs on **Windows only** (Palworld is Windows-only).

### 2. Clone and build

```powershell
git clone <url> PalLLM
cd PalLLM
dotnet build PalLLM.sln --configuration Release
```

Expect: `Build succeeded. 0 Warning(s) 0 Error(s)`.

### 3. Run tests

```powershell
dotnet test PalLLM.sln --configuration Release --nologo --verbosity quiet
```

Expect: `Passed!  - Failed: 0, Passed: 1135, Skipped: 0, Total: 1135`.

### 4. Run drift audit

```powershell
powershell -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging
```

Expect: `Audit complete. Overall: PASS` with `16 / 16` gates green.

### 5. Run sidecar

```powershell
dotnet run --configuration Release --project src/PalLLM.Sidecar/PalLLM.Sidecar.csproj
```

Expect: HTTP listener on `http://localhost:5088`. Three
user-facing surfaces:

| Path | Audience | What it is |
|---|---|---|
| `/welcome.html` | non-technical / first-time | Friendly Pal avatar + chat + voice in/out + accessibility toggles + PWA install. Zero jargon. |
| `/` | operator / power user | Field Console dashboard with health, packs, memory, MCP discovery, bridge proof. |
| `/openapi/v1.json` | API integrator | Full machine-readable API contract (56 routes). |
| `/mcp` | MCP client (Claude Desktop, VS Code, Cursor) | JSON-RPC 2.0 streaming HTTP, 38 tools. |

Plus `/metrics` (Prometheus exposition), `/health/live`,
`/health/ready` for ops tooling.

### 6. Verify a chat reply (no inference required)

```powershell
curl -X POST http://localhost:5088/api/chat -H "Content-Type: application/json" -d '{"userMessage":"hi","characterId":1}'
```

Expect: a JSON `ChatResponse` with `assistantMessage` populated
even though no live inference engine is wired. The deterministic
fallback director answered.

### 7. Optional - wire live inference

```powershell
pwsh ./pal.ps1 connect llamacpp -ModelPath D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf
```

Probes `localhost:11434`, picks a hardware-appropriate model,
writes `appsettings.json` idempotently. Other connectors:
`pal connect llamacpp / lmstudio / vllm / vllm-omni / transformers
/ tensorrt / openvino / foundry`.

### 8. Optional - install the mod

```powershell
pwsh ./scripts/install-mod.ps1
```

Atomic install with rollback. Requires UE4SS pre-installed.
Reverse with `pwsh ./scripts/uninstall-mod.ps1`.

## Layered detail - pick by need

### For the lay reader

[`PITCH.md`](PITCH.md) - plain-English "what is PalLLM and why
should I care?" with player-facing scenarios. Read this first
if you've never seen the project.

### For the operator

[`OPERATIONS.md`](OPERATIONS.md) - running surface, opt-in
feature matrix, troubleshooting. Pair with
[`RUNBOOK.md`](RUNBOOK.md) for per-symptom incident response.

### For the contributor

[`CONTRIBUTING.md`](../CONTRIBUTING.md) - pre-flight checklist,
style, drift-gate contract, anti-patterns to avoid.

### For the agent picking up cold

[`AGENTS.md`](../AGENTS.md) - same pre-flight checklist as
`CONTRIBUTING.md` but framed for an agent that landed on this
repo with no prior session.

### For the harvester

[`HARVEST.md`](HARVEST.md) - how to lift individual capabilities
(advisors, builders, the fallback director, the proof packet
machinery, etc.) into a different project. Each unit has a
self-contained extraction recipe.

### For the security reviewer

[`PRIVACY.md`](PRIVACY.md) - every surface that crosses the
network boundary, default posture for each, and how to verify
the air-gap claim with `GET /api/airgap/verify`.

[`../SECURITY.md`](../SECURITY.md) - supply-chain posture,
disclosure channel, signed releases, SBOM, CodeQL,
Dependabot.

### For the release manager

[`RELEASE.md`](RELEASE.md) - full release walkthrough.
[`RELEASE_SIGNING.md`](RELEASE_SIGNING.md) - signing posture.

## Where every contract lives

| Contract | Authoritative source | Schema |
|---|---|---|
| HTTP API | `src/PalLLM.Sidecar/Program.cs` (route registration) | [`openapi/palllm-sidecar-v1.json`](openapi/palllm-sidecar-v1.json) |
| MCP tool surface | `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs` | derived from `[McpServerTool]` attributes |
| Bridge inbox events | `src/PalLLM.Domain/Integration/Contracts.cs` (`BridgeEventEnvelope`) | [`schemas/bridge-event-envelope.schema.json`](schemas/bridge-event-envelope.schema.json) |
| Bridge outbox replies | `src/PalLLM.Domain/Integration/Contracts.cs` (`OutboxEnvelope`) | [`schemas/outbox-envelope.schema.json`](schemas/outbox-envelope.schema.json) |
| Personality packs | `src/PalLLM.Domain/Packs/PersonalityPack.cs` | [`schemas/personality-pack.schema.json`](schemas/personality-pack.schema.json) |
| Install manifest | `scripts/PalLLM.InstallManifest.ps1` | [`schemas/install-manifest.schema.json`](schemas/install-manifest.schema.json) |
| Native proof status | `scripts/pal-proof.ps1 -Json` | [`schemas/native-proof-status-v1.schema.json`](schemas/native-proof-status-v1.schema.json) |
| Agent capability manifest | `agents.json` | [`schemas/agents.schema.json`](schemas/agents.schema.json) |
| Verb manifest | `pal.json` | [`schemas/pal-verbs.schema.json`](schemas/pal-verbs.schema.json) |
| Project numbers | `docs/PROJECT_NUMBERS.json` | [`schemas/project-numbers.schema.json`](schemas/project-numbers.schema.json) |

Every contract has an automated drift gate. See
[`schemas/README.md`](schemas/README.md) for validation recipes.

## Hard invariants (cannot drift)

These are enforced by either tests, drift gates, or both. Any
change that violates one of them is wrong.

1. **Deterministic fallback always answers.** `POST /api/chat`
   never returns 5xx because inference is off. See
   [`adr/0001`](adr/0001-deterministic-first-reply-pipeline.md).
2. **Default install is fully local.** Zero outbound traffic
   without explicit operator opt-in. See
   [`adr/0006`](adr/0006-opt-in-everything-by-default.md) and
   [`PRIVACY.md`](PRIVACY.md).
3. **Bridge is one-way + advisory.** Sidecar never reaches into
   Palworld; the mod consumes outbox files at its own cadence.
   See [`adr/0003`](adr/0003-one-way-advisory-bridge.md).
4. **Portable adapter seam.** Domain has zero ASP.NET / UE4SS /
   Palworld dependencies. See
   [`adr/0002`](adr/0002-portable-adapter-seam.md).
5. **Every drift gate stays green.** 16 gates in
   `scripts/run_full_audit.ps1` plus 24 NUnit meta-tests in
   `tests/PalLLM.Tests/MetaTests.cs`. See
   [`adr/0004`](adr/0004-drift-gates-over-manual-review.md).
6. **Every documented count is verified against code.** No
   handwritten number stays unchecked. See `PROJECT_NUMBERS.json`
   and the meta-test suite.

## How to verify your replication

Run all four of these. If they all succeed, your replica matches
the canonical behavior:

```powershell
dotnet build PalLLM.sln --configuration Release       # 0 warnings
dotnet test  PalLLM.sln --configuration Release       # 1154/1154
powershell -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging  # 16/16 PASS
pwsh ./pal.ps1 hello   # deterministic chat reply (sidecar must be running)
```

## What this doc deliberately does NOT cover

- **Live Palworld behavior** - that's [`COMPATIBILITY.md`](COMPATIBILITY.md).
- **Operator runbook** - that's [`OPERATIONS.md`](OPERATIONS.md).
- **Per-feature deep dives** - those live in subsystem docs
  ([`MEMORY_RECIPES.md`](MEMORY_RECIPES.md),
  [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md),
  [`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md), etc.).
- **Reaching 100%** - that's [`COMPLETION.md`](COMPLETION.md)
  plus [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md).

This doc is the **first read**, not the only read. The full doc
map is in [`INDEX.md`](INDEX.md).


