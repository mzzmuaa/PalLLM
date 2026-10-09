# PalLLM

![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)
![.NET 10](https://img.shields.io/badge/.NET-10.0--LTS-blueviolet.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20Container-lightgrey.svg)
![Tests](https://img.shields.io/badge/tests-1310%20passing-success.svg)
![MCP](https://img.shields.io/badge/MCP-2025--06--18-purple.svg)
![Coverage](https://img.shields.io/badge/coverage-86.9%25%20line%20%7C%2070.4%25%20branch-brightgreen.svg)
![Status](https://img.shields.io/badge/roadmap-76.2%25%20honest-blue.svg)
[![CI](https://github.com/mzzmuaa/PalLLM/actions/workflows/ci.yml/badge.svg)](https://github.com/mzzmuaa/PalLLM/actions/workflows/ci.yml)
[![CodeQL](https://github.com/mzzmuaa/PalLLM/actions/workflows/codeql.yml/badge.svg)](https://github.com/mzzmuaa/PalLLM/actions/workflows/codeql.yml)

**PalLLM gives every companion in *Palworld* its own local AI voice - on
your own computer, with no cloud account, no subscription, and no data
leaving your machine by default.**

- **100% local by default.** No signup. No phone-home. No subscription.
- **Always answers.** 19 hand-authored reply strategies keep the companion
  responsive even when no AI model is configured, or the model is broken,
  rate-limited, or thermal-throttled. The model makes replies *better*, never
  *possible*.
- **Scales to your hardware.** A modest GPU runs a small model or
  deterministic-only; a workstation runs the full per-turn model mesh.
- **Talk to your companion from any chat app.** A built-in MCP server exposes
  38 tools to any Model-Context-Protocol-aware desktop or IDE client.
- **Privacy is inspectable.** One HTTP call (`GET /api/privacy/posture`)
  enumerates every data-emitting surface as `never-leaves` /
  `only-with-opt-in` / `leaves-by-default`.

> ### This README is the single source of truth
>
> **Reading only this page gives you the whole project** - what it is, how it
> is shaped, everything it exposes, how to run it, and (for contributors and
> coding agents) the authority model, the rules, and the exact verify
> commands. Follow a link only when you want *depth*; this page never sends you
> elsewhere to learn the basics.
>
> - **New here (human)?** Skim [What PalLLM is](#what-palllm-is) and
>   [Quickstart](#quickstart), or read [`docs/PITCH.md`](docs/PITCH.md) for the
>   plain-English tour. Once the sidecar is running, open
>   <http://localhost:5088/welcome.html>.
> - **Working on this repo (human or coding agent)?** Everything you need to
>   work safely is in [Develop it - the authority model](#develop-it---the-authority-model).
>   The agent doorways ([`AGENTS.md`](AGENTS.md), [`CLAUDE.md`](CLAUDE.md),
>   [`.cursorrules`](.cursorrules), the file under `.github/`) all defer here
>   and add only the engine/model specifics this public page is not allowed to
>   name.
> - **The review ritual (do not skip):** read the
>   [Develop it](#develop-it---the-authority-model) section *before* every
>   change and update this README + its drift-gated numbers *after* every
>   change. The audit fails if they drift, so "always current" is mechanically
>   enforced - keep it that way.

> **Status.** `76.2%` on the honest, player-experience-weighted roadmap
> (scaffolded features discounted; verification gaps treated as real blockers -
> see [`docs/ROADMAP.md`](docs/ROADMAP.md)). The sidecar runtime is effectively
> production-ready; the remaining ~24pp is strictly live-Palworld native
> delivery (HUD binding, in-world audio, full action executor) that only an
> in-game session can close.
> `1310` passing tests. `57` `/api` routes plus a complete `MCP` server at
> `/mcp` (`38` tools, `6` resources + `1` template, `4` prompts). `122`
> feature-catalog entries (`119 ready / 2 scaffolded / 1 deferred`). `19`
> deterministic fallback strategies. OpenAPI 3.1 contract at `/openapi/v1.json`.
> Counts here are drift-gated against the code; the machine-readable source is
> [`docs/PROJECT_NUMBERS.json`](docs/PROJECT_NUMBERS.json).
> Last audited: **2026-06-05**.

---

## Contents

- [What PalLLM is](#what-palllm-is)
- [How it works](#how-it-works)
- [The public surface](#the-public-surface)
- [Minimum requirements](#minimum-requirements)
- [Quickstart](#quickstart)
- [Develop it - the authority model](#develop-it---the-authority-model)
- [Roadmap + current state](#roadmap--current-state)
- [Harvest it](#harvest-it)
- [Documentation map](#documentation-map)
- [Contributing](#contributing) - [License](#license)

---

## What PalLLM is

PalLLM is a **local-first AI companion runtime for Palworld**. It gives your
in-game Pals - and a desktop dashboard - memory, personality, situational
awareness, and a voice, running entirely on your own machine by default with
**zero outbound network traffic** unless you opt in.

Three ideas define it, and everything else follows from them:

- **Local-first & private.** Inference runs on your own GPU through a bundled
  local engine. Nothing leaves the machine unless you deliberately wire a
  hosted model. The privacy posture is inspectable at runtime
  ([`docs/PRIVACY.md`](docs/PRIVACY.md)).
- **Deterministic-first.** The companion **always** replies - even with the
  model off, broken, rate-limited, or throttled - through a hand-authored
  fallback director. The model makes replies better, never possible. This is
  the headline product promise
  ([`docs/adr/0001-deterministic-first-reply-pipeline.md`](docs/adr/0001-deterministic-first-reply-pipeline.md)).
- **One-way advisory bridge.** The sidecar *observes* the game and *suggests*;
  it never reaches into Palworld to act without an explicit, guarded opt-in
  ([`docs/adr/0003-one-way-advisory-bridge.md`](docs/adr/0003-one-way-advisory-bridge.md)).

**Scope and ownership boundary.** PalLLM is a Palworld + UE4SS integration
with a neutral, reusable sidecar core. Adjacent local projects may inspire only
generic engineering patterns; tracked PalLLM code and docs must not import their
names, assets, prompts, lore, characters, gameplay rules, or product identity.
Keep feature copy about this mod, keep reusable interfaces generic, and keep
the affiliation disclaimer in [`NOTICE.md`](NOTICE.md) current.

**What it actually does** (every shipped capability is an explicit, drift-gated
entry in `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`; grouped here so
nothing is missed):

- **Conversation** - chat orchestration with per-turn prompt assembly (persona
  + world snapshot + relationship + recalled memory), streaming, response
  cleanup, and protective concurrency gates.
- **Memory & relationships** - semantic memory with importance scoring and
  reflection, per-character relationship tracking, session persistence with
  autosave.
- **Personality** - hot-loadable narrative packs (lore, voice, samples) with
  content-hash validation, plus species-aware personality resolution.
- **Perception & voice** - vision/screenshot description, text-to-speech
  synthesis, speech-to-text transcription, and advisory action intents handed
  to a guarded in-game executor.
- **The deterministic director** - 19 fallback strategies plus a general
  director that emit a multi-sentence reply with a full visual + audio
  presentation plan, with no live model required.
- **Inference orchestration** - delegates live generation to any HTTP
  chat-completions endpoint you choose; applies task-aware execution profiles
  (thinking mode, sampling, token budget, vision use, evidence budget) per
  turn; can keep a local model hot to avoid cold-load latency; and publishes
  machine-readable collaboration plans so a multi-model local stack routes
  deliberately instead of by guesswork.
- **Operational truth** - Prometheus `/metrics`, opt-in OTLP traces/logs/GenAI
  histograms, recent-window lane readiness (`healthy` / `degraded` /
  `critical` / `insufficient_data` / `no_data`), and machine-readable
  release-readiness + bridge-proof snapshots so automation never scrapes
  markdown for state.
- **Player experience** - a no-build Field Console dashboard, a friendly
  zero-config welcome chat (`/welcome.html`) with avatar + voice + PWA install,
  one-click `play.bat` / `support.bat`, and a durable evidence trail under
  `Runtime/` for launches, smoke runs, native proof, and support bundles.

New to the concepts? [`docs/MENTAL_MODEL.md`](docs/MENTAL_MODEL.md) makes them
click in five minutes; [`docs/GLOSSARY.md`](docs/GLOSSARY.md) defines every
PalLLM-specific term; [`docs/FAQ.md`](docs/FAQ.md) answers the common questions.

## How it works

PalLLM is **three independent processes plus an inference server** - three
separate crash domains, so one failing never mutes the companion:

| Piece | What it is | Lives in |
|---|---|---|
| **Sidecar** | Self-contained .NET 10 ASP.NET Core minimal-API service: the `/api` surface, the `/mcp` server, the dashboard, and all runtime logic. Cross-platform (Windows / Linux / macOS / container). | `src/PalLLM.Sidecar/` |
| **Domain** | The portable, host-agnostic core - chat, memory, personas, world model, inference clients, advisors. No ASP.NET, no Palworld, no UE4SS. | `src/PalLLM.Domain/` |
| **Mod (bridge)** | A UE4SS Lua mod running *inside* Palworld that exchanges files with the sidecar (inbox / outbox / screenshots). Windows-only. | `mod/ue4ss/Mods/PalLLM/` |
| **Engine** | The bundled local inference server - the only local engine. PalLLM is just an HTTP chat-completions client to it; a hosted endpoint is the documented escape path for below-reference hardware. | external + local model files |

**A chat turn, end to end:** the player speaks in-game -> the Lua bridge writes
an inbox file -> the sidecar's inbox worker drains it -> `PalLlmRuntime.ChatAsync`
assembles the prompt -> it either calls the inference server **or** serves the
deterministic director -> the reply is trimmed, remembered, and written to the
outbox -> the bridge renders it in-game. The same `ChatAsync` backs
`POST /api/chat` for the dashboard. The bridge is one-way and advisory: the
sidecar never reaches into Palworld; the mod consumes outbox files at its own
cadence.

```text
            +-----------------------------------------------------+
            |  Palworld + UE4SS (Windows-only)                    |
            |   +----------+                  +--------------+     |
            |   | Lua mod  |  -- renders -->  | Native HUD / |     |
            |   | main.lua |  <-- events --   | audio /      |     |
            |   +----------+                  | action exec  |     |
            +--------+----------------------- +--------------+-----+
                     | events                 ^ replies
                     v (Bridge/Inbox/*)       | (Bridge/Outbox/*)
            +-----------------------------------------------------+
            |  PalLLM Sidecar (.NET 10 LTS, ASP.NET Core)         |
            |   InboxWorker -> Runtime -> /api/chat -> Outbox     |
            |                    +- FallbackBehaviorEngine (19)    |
            |                    +- Memory + Relationships         |
            |                    +- Narrative packs                |
            |                    +- Vision / TTS / ASR / Actions   |
            |                    +- /api/* (57) + /mcp (38 tools)  |
            |   Field Console dashboard at http://localhost:5088/  |
            +-----------------------------------------------------+
                     | prompts / tool calls       ^ completions
                     v (HTTP chat-completions)    |
            +-----------------------------------------------------+
            |  Local inference (any chat-completions endpoint),    |
            |  or a hosted endpoint as the below-reference escape. |
            +-----------------------------------------------------+
```

Inference is optional: when it is off the deterministic director still produces
a competent reply; when it is on, the same path splices the model output
through the same presentation plan. Full layout + data flow:
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). The portable seam the published
binary is built around is one file:
[`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`](src/PalLLM.Domain/Portable/PortableAdapterContracts.cs).

## The public surface

Everything PalLLM exposes is counted and drift-gated; the machine-readable
figures live in [`docs/PROJECT_NUMBERS.json`](docs/PROJECT_NUMBERS.json).

- **HTTP API.** **57 `/api` routes** under `http://localhost:5088/api/*` -
  chat, memory, relationships, world/vision, personality packs, the promotion
  loop, health/posture, hardware, proof/readiness, and planning. Errors are
  always `ProblemDetails` (no stack traces or exception type names leak). Full
  reference: [`docs/API.md`](docs/API.md).
- **Operational routes (6).** `/` (dashboard), `/metrics` (Prometheus),
  `/health/live`, `/health/ready`, `/openapi/v1.json`, `/openapi/v1.yaml`.
- **MCP server (1 protocol route).** `/mcp` (Streamable HTTP) exposes 38 tools,
  6 resources + 1 template, and 4 prompts to AI clients. Wire it with
  `pwsh ./pal.ps1 mcp connect <client>`; guide:
  [`docs/MCP_QUICKSTART.md`](docs/MCP_QUICKSTART.md).
- **Machine-readable proof.** `GET /api/release/readiness` (shipped surface,
  audit commands, publication blockers, durable evidence) and
  `GET /api/bridge/proof` (native readiness + request/delivery closure) so
  tooling reads state instead of scraping docs.
- **Field Console dashboard.** A no-build vanilla HTML/JS bundle at
  `http://localhost:5088/`.
- **The `pal` CLI.** `pwsh ./pal.ps1 <verb>` (play, install-llama-cpp, connect,
  models, audit, doctor, benchmark, ...); the machine-readable verb table is
  [`pal.json`](pal.json).
- **Default posture.** Inference off, fallback on, vision off, TTS off, ASR
  off, session persistence on, bridge outbox on. Every opt-in is a reversible
  flag edit - no state migration. Default model tags in
  `src/PalLLM.Sidecar/appsettings.json` are operator-tunable examples, not part
  of the wire contract.

## Minimum requirements

PalLLM v1.0 ships configured for a specific reference rig; the full spec and the
escape paths for other hardware are in
[`docs/MINIMUM_REQUIREMENTS.md`](docs/MINIMUM_REQUIREMENTS.md).

- **GPU:** 12 GB VRAM Ampere-class card (e.g. RTX 3060 12 GB) or newer
- **RAM:** 16 GB DDR4 / DDR5 or better
- **CPU:** 6-core x86 (Zen 3 / 11th-gen Core era) or better, AVX2 required
- **OS:** Windows 10 / 11 x64 (the sidecar itself also runs on Linux / macOS)
- **Disk:** ~80 GB free for the bundled engine + curated models

Other hardware (CPU-only, Apple Silicon, alternate GPU vendors, multi-GPU) may
work via opt-in flags but is outside the v1.0 shipping support matrix - see
[`docs/POST_RELEASE_ANNEX.md`](docs/POST_RELEASE_ANNEX.md). With **no** model at
all, the deterministic companion still answers every turn.

## Quickstart

```powershell
pwsh ./pal.ps1 install-llama-cpp -AutoLaunch   # detect hardware, fetch + verify the engine, pick a model, wire + launch
pwsh ./pal.ps1 play                            # boot sidecar + dashboard (+ launch the game)
```

**Run with local inference (one command).** The install above detects your
GPU/VRAM/RAM cross-platform, picks the right backend asset with SHA-256
verification, smoke-tests the binary, recommends the best curated model that
fits your VRAM, wires `appsettings.json` with a documented per-model sampler,
and launches the server with a hardware-aware recipe. Hardware-tier matrix,
per-model recipes, and the known-bug catalog:
[`docs/LLAMA_CPP_BUNDLED.md`](docs/LLAMA_CPP_BUNDLED.md).

**Player install (from a release zip).** Download the latest
[release](../../releases), extract, and double-click **`play.bat`** - it
auto-detects Palworld, installs/refreshes the mod, starts the sidecar, runs
doctor, opens the dashboard, and launches Palworld. If anything looks wrong,
double-click **`support.bat`** for an anonymized triage bundle.

**Below-reference hardware?** Take the documented escape path:
`pwsh ./pal.ps1 connect cloud -Provider <provider> -ApiKey <key>`. With no model
at all, the deterministic companion still answers every turn.

**Container (remote sidecar):**

```bash
docker build -t palllm:latest .
docker run --rm -p 5088:5088 -v palllm-runtime:/var/palllm palllm:latest
```

When reachable beyond `localhost`, enable bearer-token auth
(`-e PalLLM__Auth__ApiKey=...`). Full operator guide, opt-in feature matrix, and
remote-bridge pattern: [`docs/OPERATIONS.md`](docs/OPERATIONS.md).

## Develop it - the authority model

> **This section is the contract.** A coding agent or contributor can work
> safely from this page alone. Depth is one link away, but the rules,
> invariants, and verify commands are all *here*.

**The one rule that subsumes the rest: every change starts green and ends
green.**

```powershell
dotnet test PalLLM.sln -c Release --nologo
pwsh -NoProfile -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging
```

Both green = your change preserved every invariant this codebase enforces. The
audit builds (0 warnings required), runs the full NUnit suite, and runs the
drift gates - all defined in `scripts/run_full_audit.ps1`. Verified status the
README is pinned against:

```text
$ dotnet test PalLLM.sln
Passed!  - Failed: 0, Passed: 1310, Skipped: 0, Total: 1310
```

**Non-negotiable invariants** (enforcement sites in
[`docs/INVARIANTS.md`](docs/INVARIANTS.md)):

1. The deterministic fallback **always** answers `POST /api/chat`.
2. The default install is fully local - zero outbound traffic unless opted in.
3. The bundled local engine is the **only** local engine - never add a second
   local engine path. (Operators may point inference at any hosted
   chat-completions endpoint as the documented escape path.)
4. Every feature-catalog entry stays (remove only with a `deprecated` status +
   a `CHANGELOG.md` note).
5. `/api/*` errors are `ProblemDetails` only.
6. The packaged-EXE boot path stays working.
7. The build stays at 0 warnings.

**Drift gates + the count cascade.** Adding or removing a `[Test]`, a route, or
a feature forces a documentation cascade: update
[`docs/PROJECT_NUMBERS.json`](docs/PROJECT_NUMBERS.json) plus the docs the audit
names (it tells you exactly which). This is why the numbers on this page are
trustworthy - they cannot silently drift. The five publication-facing files
(`README.md`, `NOTICE.md`, `SECURITY.md`, `docs/INDEX.md`, `docs/RELEASE.md`)
must also stay free of vendor brand names; the block list in
`scripts/public_copy_policy.ps1` is an intentional feature, which is why this
README says "the bundled local engine" rather than naming it. History is
**append-only**: never rewrite past `CHANGELOG.md` / `docs/HANDOFF.md` entries,
ADRs, or dated research notes.

**The working loop.** Read [`docs/HANDOFF.md`](docs/HANDOFF.md) (what just
landed + current audited state) -> locate the symbol in
[`docs/CODE_MAP.md`](docs/CODE_MAP.md) -> make the smallest change that solves
it -> add a test -> `dotnet test` -> run the audit -> update
[`docs/HANDOFF.md`](docs/HANDOFF.md) + `CHANGELOG.md` -> re-read this README's
numbers and refresh anything that moved -> commit.

**Patterns you will mirror** (copy a sibling, rename, tweak): *Advisor*
(`XxxAdvisor.Advise` -> record), *Builder* (`XxxBuilder.Build` -> snapshot),
*Validator* (`XxxValidator.Validate` -> result), *Feeder* (background worker ->
bounded records). Full catalog + naming cheatsheet:
[`docs/CONVENTIONS.md`](docs/CONVENTIONS.md).

**What not to touch:** don't rename `src/PalLLM.Domain/Portable/` (the
redistributable seam - ADR 0002); don't turn `Program.cs` into controllers (the
minimal-API route inventory must stay greppable for the gates); don't add a
NuGet dependency without a `THIRD_PARTY_NOTICES.md` entry; don't change
`PalLLM:` config-key shapes without a `CHANGELOG.md` migration note; don't
mass-fix the CS1591 XML-doc warnings; don't add mock network calls in tests
(fixtures boot a real in-process sidecar with inference/vision/TTS
`Enabled=false`). The full "already considered and rejected" list:
[`docs/ANTI_PATTERNS.md`](docs/ANTI_PATTERNS.md).

## Roadmap + current state

This page is durable; live state lives where the audit keeps it honest:

- **What just landed + current audited state:**
  [`docs/HANDOFF.md`](docs/HANDOFF.md).
- **Honest roadmap (player-experience-weighted) + build queue:**
  [`docs/ROADMAP.md`](docs/ROADMAP.md) and
  [`docs/IMPLEMENTATION_QUEUE.md`](docs/IMPLEMENTATION_QUEUE.md). The dominant
  remaining gap is in-game native HUD / audio / action delivery, which needs a
  live Palworld + UE4SS session to prove.
- **Every number:** [`docs/PROJECT_NUMBERS.json`](docs/PROJECT_NUMBERS.json).

**Shipped + tested:** local-first runtime, 19 deterministic fallback
strategies, semantic memory + reflection, relationship tracking, narrative
packs, vision + screenshot ingest, session persistence, TTS, ASR, advisory
action intents + guarded executor, `ProblemDetails`, circuit breaker + retry +
rate limiter, bounded retention, Prometheus metrics, Field Console dashboard.
**Remaining for 100% (all need in-game validation):** native HUD bind
(scaffolded, kill-switched), in-world audio playback, richer action coverage,
confirmed Palworld hook signatures, clean-machine install walkthrough, an
in-Palworld smoke pass.

## Harvest it

Individual capabilities lift cleanly into other projects. The stable,
redistributable contract is the interface set in
`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` (~250 lines, zero
external dependencies; ADR 0002 - never reshape it). Most of
`src/PalLLM.Domain/` is pure .NET 10 with no Palworld/UE4SS coupling - chat,
memory, relationship, personality-pack, and transport code drop into any
ASP.NET or console host. The full per-capability harvest menu (what's pure,
what's host-coupled, how to extract each) is
[`docs/HARVEST.md`](docs/HARVEST.md).

## Documentation map

This README is the complete picture. The `docs/` tree is *depth one link
away*, organized by the [Diataxis](https://diataxis.fr/) framework; the full
index is [`docs/INDEX.md`](docs/INDEX.md).

| If you want to... | Open |
|---|---|
| Read the plain-English tour | [`docs/PITCH.md`](docs/PITCH.md) |
| Get a chat reply in 5 minutes | [`docs/QUICKSTART.md`](docs/QUICKSTART.md) |
| Connect an MCP client | [`docs/MCP_QUICKSTART.md`](docs/MCP_QUICKSTART.md) |
| Understand the shape + "why" | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| Look up an HTTP endpoint | [`docs/API.md`](docs/API.md) |
| Keep a sidecar healthy / flip an opt-in | [`docs/OPERATIONS.md`](docs/OPERATIONS.md) |
| Install + tune the local engine | [`docs/LLAMA_CPP_BUNDLED.md`](docs/LLAMA_CPP_BUNDLED.md) |
| Write a narrative pack | [`docs/PACK_AUTHORING.md`](docs/PACK_AUTHORING.md) |
| Prepare a release | [`docs/RELEASE.md`](docs/RELEASE.md) |
| Read the roadmap / build queue | [`docs/ROADMAP.md`](docs/ROADMAP.md) / [`docs/IMPLEMENTATION_QUEUE.md`](docs/IMPLEMENTATION_QUEUE.md) |
| Resume after a coding handoff | [`docs/HANDOFF.md`](docs/HANDOFF.md) |
| Review per-agent work notes | [`docs/AgentWork/Codex.md`](docs/AgentWork/Codex.md) |
| Audit what leaves the machine | [`docs/PRIVACY.md`](docs/PRIVACY.md) |
| Review the latest changes | [`CHANGELOG.md`](CHANGELOG.md) |

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the full pre-flight checklist and
the conventions around new features, endpoints, bridge events, and guarded
actions. The short version is the [Develop it](#develop-it---the-authority-model)
section: start green, end green. CI
([`.github/workflows/ci.yml`](.github/workflows/ci.yml)) runs `dotnet build` +
`dotnet test` on Windows and Linux plus the doc-drift audit.

## License

PalLLM is released under the MIT license. See [`LICENSE`](LICENSE),
[`NOTICE.md`](NOTICE.md) for the third-party-affiliation disclaimer, and
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for components PalLLM calls
at runtime.

> **Unaffiliated third-party project.** PalLLM is not affiliated with, endorsed
> by, or sponsored by any game publisher, game developer, middleware vendor, or
> model provider. See [`NOTICE.md`](NOTICE.md) for the full disclaimer.
