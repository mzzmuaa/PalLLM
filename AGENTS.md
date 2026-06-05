# PalLLM — The Single Source of Truth

Last audited: `2026-06-05`

**This one file describes PalLLM completely** — enough for any human to
understand what it is and run it, and enough for any coding agent (Claude Code,
Codex, Cursor, Copilot, Aider, Continue, …) to work on it safely. Read it top to
bottom. Everything else in the repo is *deeper detail one link away* — this file
never sends you elsewhere just to learn the basics.

> **Why this file has no live numbers.** Counts (tests, routes, MCP tools,
> features, roadmap %) drift, so they are **not duplicated here**. The single
> machine-readable source of truth is `docs/PROJECT_NUMBERS.json` (drift-gated by
> the audit). Wherever this doc would state a count, it points there instead — so
> this file stays correct without churn. That is what "hardened + always updated"
> means: durable prose here, volatile facts by reference.

---

## 1. What PalLLM is (for humans)

PalLLM is a **local-first AI companion runtime for Palworld**. It gives your
in-game Pals — and a desktop dashboard — memory, personality, situational
awareness, and a voice, running entirely on your own machine by default with
**zero outbound network traffic** unless you opt in.

Three ideas define it:

- **Local-first & private.** Inference runs on your GPU through a bundled
  `llama.cpp` server. Nothing leaves the machine unless you wire a cloud model on
  purpose. See [`docs/PRIVACY.md`](docs/PRIVACY.md).
- **Deterministic-first.** The companion **always** replies — even with the model
  off, broken, rate-limited, or thermal-throttled — via a hand-authored fallback
  director. The LLM makes replies *better*, never *possible*. This is the
  headline product promise
  ([`docs/adr/0001-deterministic-first-reply-pipeline.md`](docs/adr/0001-deterministic-first-reply-pipeline.md)).
- **One-way advisory bridge.** The sidecar observes the game and *suggests*; it
  never reaches into Palworld to act without an explicit, guarded opt-in
  ([`docs/adr/0003-one-way-advisory-bridge.md`](docs/adr/0003-one-way-advisory-bridge.md)).

Plain-English narrative + first-time questions:
[`docs/PITCH.md`](docs/PITCH.md), [`docs/FAQ.md`](docs/FAQ.md). Hit an unfamiliar
word? Every PalLLM term is defined in [`docs/GLOSSARY.md`](docs/GLOSSARY.md).

## 2. The shape of the system (one read)

PalLLM is **three cooperating processes plus an inference server**:

| Piece | What it is | Lives in |
|---|---|---|
| **Sidecar** | Self-contained .NET 10 ASP.NET Core minimal-API service: the `/api` HTTP surface, the `/mcp` server, the dashboard, and all runtime logic. | `src/PalLLM.Sidecar/` |
| **Domain** | The portable, host-agnostic core — chat, memory, personas, world model, inference clients, advisors. No ASP.NET, no Palworld, no UE4SS. Pure .NET. | `src/PalLLM.Domain/` |
| **Mod (bridge)** | A UE4SS Lua mod running *inside* Palworld that exchanges files with the sidecar (inbox / outbox / screenshots). Windows-only (UE4SS is a Win64 injector). | `mod/ue4ss/Mods/PalLLM/` |
| **Engine** | `llama.cpp`'s `llama-server` — the only local inference engine. Installed by a script; PalLLM is just an OpenAI-compatible HTTP client to it. | external + `D:\Models` GGUFs |

**A chat turn, end to end:** the player speaks in-game → the Lua bridge writes an
inbox file → the sidecar's bridge worker drains it → `PalLlmRuntime.ChatAsync`
assembles the prompt (persona + world snapshot + relationship + recalled memory)
→ it either calls the inference server **or** serves the deterministic director →
the reply is trimmed, remembered, and written to the outbox → the bridge surfaces
it in-game. The same `ChatAsync` backs `POST /api/chat` for the dashboard.
Latency budgets per hot-path method: [`docs/HOT_PATH.md`](docs/HOT_PATH.md).

**Inference strategy (durable shape).** PalLLM never loads a model itself — it is
an OpenAI-compatible client. `llama.cpp` is the **only** local engine; an
OpenAI-compatible **cloud API is the documented escape path** for
below-reference hardware (`scripts/connect-cloud.ps1`). Each turn is routed to
the best lane on one `llama-server`; anything the server can't serve falls back
to the deterministic director. The current shipping model strategy — a per-turn
**fast lane** + **smart/multimodal lane** mesh — lives in
`src/PalLLM.Sidecar/appsettings.json` and
[`docs/LOCAL_MODELS_INVENTORY.md`](docs/LOCAL_MODELS_INVENTORY.md); the launch
recipe is in [`docs/LLAMA_CPP_BUNDLED.md`](docs/LLAMA_CPP_BUNDLED.md). Full layout
+ data flow: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md); the conceptual model:
[`docs/MENTAL_MODEL.md`](docs/MENTAL_MODEL.md).

## 3. What you can use (the public surface)

Everything PalLLM exposes is counted and drift-gated — exact live figures in
`docs/PROJECT_NUMBERS.json`:

- **HTTP `/api/*`** — chat, memory, relationships, world/vision, personality
  packs, the promotion loop, health/posture, hardware, proof/readiness, planning.
  Registered across `src/PalLLM.Sidecar/Program.cs` +
  `src/PalLLM.Sidecar/RouteRegistrations/`. Reference table:
  [`docs/API.md`](docs/API.md). Errors are always `ProblemDetails` — no stack
  traces or exception type names leak.
- **MCP server `/mcp`** — tools, resources, and prompts for AI clients (Claude
  Desktop, VS Code, Cursor). Wire it with `pal mcp connect`; guide in
  [`docs/MCP_QUICKSTART.md`](docs/MCP_QUICKSTART.md).
- **Feature catalog** — every shipped capability is an explicit entry in
  `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` (a contract, not prose).
  The one-page catalog of advisors / builders / validators / feeders:
  [`docs/ADVISORS.md`](docs/ADVISORS.md).
- **Field Console dashboard** — a no-build vanilla HTML/JS bundle at
  `http://localhost:5088/`.
- **The `pal` CLI** — `pwsh ./pal.ps1 <verb>` (play, install-llama-cpp, connect,
  models, audit, doctor, benchmark, …); the verb table is `pal.json`.

## 4. Operate it (humans / operators)

```powershell
pwsh ./pal.ps1 install-llama-cpp                                   # download + verify the bundled engine
pwsh ./pal.ps1 connect llamacpp -ModelPath <model.gguf> -WriteConfig   # wire the local engine
pwsh ./pal.ps1 play                                               # boot sidecar + dashboard (+ launch the game)
```

Below-reference hardware? Take the escape path:
`pwsh ./pal.ps1 connect cloud -Provider openai -ApiKey <key>`. Hardware floor +
both escape paths: [`docs/MINIMUM_REQUIREMENTS.md`](docs/MINIMUM_REQUIREMENTS.md).
With **no** model at all, the deterministic companion still answers every turn.

## 5. Develop it (coding agents / contributors)

**The one rule that subsumes the rest: every change starts green and ends green.**

```powershell
dotnet test PalLLM.sln -c Release --nologo
pwsh -NoProfile -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging
```

Both green = your change preserved every invariant this codebase enforces. The
audit runs the build (0 warnings required), the full NUnit suite, and the drift
gates — all defined in `scripts/run_full_audit.ps1`.

**Working loop:** read [`docs/HANDOFF.md`](docs/HANDOFF.md) → locate the symbol in
[`docs/CODE_MAP.md`](docs/CODE_MAP.md) → make the smallest change that solves it →
add a test → `dotnet test` → run the audit → update
[`docs/HANDOFF.md`](docs/HANDOFF.md) "What just landed" + `CHANGELOG.md` → commit.

**Non-negotiable invariants** (full list: [`docs/INVARIANTS.md`](docs/INVARIANTS.md)):

- Deterministic fallback always answers `POST /api/chat`.
- Default install is fully local — zero outbound traffic unless opted in.
- `llama.cpp` is the only local engine; cloud is the escape path. Never
  reintroduce other engines.
- Every feature-catalog entry stays (remove only with a `deprecated` status +
  a `CHANGELOG.md` note).
- `/api/*` errors are `ProblemDetails` only.
- The packaged-EXE boot path stays working.
- The build stays at 0 warnings.

**Drift gates + the count cascade.** Adding or removing a `[Test]`, a route, or a
feature forces a doc cascade — update `docs/PROJECT_NUMBERS.json` plus the gated
docs the audit names (it tells you exactly which). The five publication-facing
files (`README.md`, `NOTICE.md`, `SECURITY.md`, `docs/INDEX.md`,
`docs/RELEASE.md`) must stay free of vendor brand names — the block list in
`scripts/public_copy_policy.ps1` is an intentional feature. History is
**append-only**: never rewrite past `CHANGELOG.md` / `docs/HANDOFF.md` entries,
ADRs, or dated research notes. Conventions:
[`docs/CONVENTIONS.md`](docs/CONVENTIONS.md); the principles that hold it together:
[`docs/DESIGN_PRINCIPLES.md`](docs/DESIGN_PRINCIPLES.md); what *not* to do:
[`docs/ANTI_PATTERNS.md`](docs/ANTI_PATTERNS.md).

**Patterns you will mirror** (copy a sibling, rename, tweak): *Advisor*
(`XxxAdvisor.Advise` → record), *Builder* (`XxxBuilder.Build` → snapshot),
*Validator* (`XxxValidator.Validate` → result), *Feeder* (background worker →
bounded records). Full catalog in [`docs/CONVENTIONS.md`](docs/CONVENTIONS.md).

**What not to touch:** don't rename `src/PalLLM.Domain/Portable/` (the
redistributable seam — ADR 0002); don't turn `Program.cs` into controllers (the
minimal-API route inventory must stay greppable for the gates); don't add a NuGet
dependency without a `THIRD_PARTY_NOTICES.md` entry; don't change `PalLLM:`
config-key shapes without a `CHANGELOG.md` migration note; don't mass-fix the
CS1591 XML-doc warnings; don't add mock network calls in tests — fixtures boot a
real in-process sidecar with inference / vision / TTS `Enabled=false` (the
reference test posture).

## 6. Harvest it (copy into other programs)

PalLLM is built so individual capabilities lift cleanly into other projects. The
stable, redistributable contract is the interface set in
`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` (ADR 0002 — never reshape
it). Most of `src/PalLLM.Domain/` is pure .NET 10 with no Palworld / UE4SS
coupling — chat, memory, relationship, personality-pack, and transport code drop
into any ASP.NET or console host. The full per-capability harvest menu (what's
pure, what's host-coupled, how to extract each) is
[`docs/HARVEST.md`](docs/HARVEST.md).

## 7. Current state + what's next

This file is deliberately durable — don't infer live state from it. The current
picture lives in:

- **Audited state + what just landed:** [`docs/HANDOFF.md`](docs/HANDOFF.md).
- **Honest roadmap (player-experience-weighted) + build queue:**
  [`docs/ROADMAP.md`](docs/ROADMAP.md). The dominant remaining gap is in-game
  native HUD / audio / action delivery, which needs a live Palworld + UE4SS
  session to prove.
- **Every number:** `docs/PROJECT_NUMBERS.json`.
- **Every doc, mapped:** [`docs/INDEX.md`](docs/INDEX.md).

## 8. If you break something

- Tests fail → read the message; drift-gate failures name the exact doc/count to
  fix.
- Audit fails → `artifacts/full-audit/<latest>/` holds the per-gate detail.
- Stuck → `git status` plus the last green audit artifact let you revert
  surgically. Every pass starts from green; if you are not green, get back to
  green before adding anything new.

Welcome — human or agent, you now have the whole picture. Follow a link only when
you want depth.
