# PalLLM Documentation Index

Last audited: `2026-05-23`

Docs are organised by the [Diataxis](https://diataxis.fr/) framework: the
right doc depends on what you're trying to do right now, not who you are.

> **Coding agents** — [`../AGENTS.md`](../AGENTS.md) is the root-level
> briefing. [`../CLAUDE.md`](../CLAUDE.md) is the Claude-Code-specific
> shortcut. Read those first, then come back here for the doc map.

## Start here

| If you want to... | Read |
|---|---|
| **Just talk to your companion (zero config, no jargon)** | open <http://localhost:5088/welcome.html> after the sidecar is running — friendly chat surface with avatar, voice in/out, accessibility, PWA install |
| Understand what PalLLM is, in plain English | [`PITCH.md`](PITCH.md) |
| Find the answer to a common question | [`FAQ.md`](FAQ.md) |
| Look up a PalLLM-specific term | [`GLOSSARY.md`](GLOSSARY.md) |
| Pick up this repo as a coding agent | [`../AGENTS.md`](../AGENTS.md) |
| Know *why* the codebase is shaped this way | [`DESIGN_PRINCIPLES.md`](DESIGN_PRINCIPLES.md) |
| Navigate the code — "where does X live?" | [`CODE_MAP.md`](CODE_MAP.md) |
| One-page catalog of every advisor / builder / validator / feeder | [`ADVISORS.md`](ADVISORS.md) |
| Harvest one capability into your own project | [`HARVEST.md`](HARVEST.md) |
| Follow the repo's code conventions (advisor/builder/validator/feeder patterns) | [`CONVENTIONS.md`](CONVENTIONS.md) |
| Connect PalLLM to an MCP-capable client | [`MCP_QUICKSTART.md`](MCP_QUICKSTART.md) |
| Get a working chat reply in 5 minutes | [`QUICKSTART.md`](QUICKSTART.md) |
| Expose PalLLM beyond localhost with TLS + auth | [`TLS.md`](TLS.md) |
| Run PalLLM on a dedicated server for multiple players | [`SERVER_OPERATOR.md`](SERVER_OPERATOR.md) |
| Audit what does and doesn't leave the machine | [`PRIVACY.md`](PRIVACY.md) |
| Sign and verify a release archive | [`RELEASE_SIGNING.md`](RELEASE_SIGNING.md) |
| Check PalLLM against your Palworld / UE4SS / mod list | [`COMPATIBILITY.md`](COMPATIBILITY.md) |
| Understand why PalLLM is shaped the way it is | [`ARCHITECTURE.md`](ARCHITECTURE.md) |
| Keep a running sidecar healthy in production | [`OPERATIONS.md`](OPERATIONS.md) |
| Look up an HTTP endpoint's request/response shape | [`API.md`](API.md) |
| Inspect the machine-readable bridge proof used by smoke and release tooling | [`API.md`](API.md) (`GET /api/bridge/proof`) |
| Inspect the machine-readable release posture used by automation | [`API.md`](API.md) (`GET /api/release/readiness`) |
| Design a dense-plus-MoE local model mesh | [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) |
| Prove a running model endpoint exposes `/v1/models` and useful `/metrics` before promotion | `pwsh ../pal.ps1 models probe` |
| Pick which local model to use for each PalLLM function (chat, vision, TTS, ASR, embeddings, reranker) — refreshed quarterly | [`MODELS_2026.md`](MODELS_2026.md) |
| See the operator's curated `D:\Models` library + the role-mesh mapping (machine-specific) | [`LOCAL_MODELS_INVENTORY.md`](LOCAL_MODELS_INVENTORY.md) |
| Prepare a publishable release | [`RELEASE.md`](RELEASE.md) |
| Run a focused local publication preflight | `pwsh ../pal.ps1 publish-audit` |
| Check sidecar trim / Native AOT readiness before a native publish experiment | `pwsh ../pal.ps1 aot-readiness` |
| Write or validate a narrative pack | [`PACK_AUTHORING.md`](PACK_AUTHORING.md) |
| Browse the top-level runtime posture and route inventory | [`../README.md`](../README.md) |
| Know what's shipped vs coming | [`ROADMAP.md`](ROADMAP.md) |
| Know what's actively queued for build | [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md) |
| Get the canonical "are we 100% complete?" answer in one doc | [`COMPLETION.md`](COMPLETION.md) (or `pwsh ../pal.ps1 complete` for the live version) |
| Replicate PalLLM from documentation alone (small-model friendly) | [`REPLICATION_KIT.md`](REPLICATION_KIT.md) — single-doc "rebuild from scratch" recipe with reading order, prerequisites, verification |
| Pick up the repo after an interrupted coding session | [`HANDOFF.md`](HANDOFF.md) |
| Explore post-foundation companion-intelligence / pseudo-AGI ideas that still fit a Palworld mod | [`COMPANION_INTELLIGENCE.md`](COMPANION_INTELLIGENCE.md) |
| Read the research that drives the fallback layer | [`FALLBACK_AI_RESEARCH.md`](FALLBACK_AI_RESEARCH.md) |
| Read the dated research snapshot behind the 2026-05 multimodal/Blackwell defaults | [`RESEARCH_NOTES_2026-05.md`](RESEARCH_NOTES_2026-05.md) |
| Know exactly which external library interfaces PalLLM consumes | [`CORE_LIBRARY.md`](CORE_LIBRARY.md) |
| Flip on a specific opt-in subsystem | [`OPERATIONS.md`](OPERATIONS.md) section "Opt-in feature matrix" |
| Tune a specific parameter | [`TUNING.md`](TUNING.md) - consolidated table of every configurable knob with too-low / too-high guidance |
| Review what changed in the latest pass | [`../CHANGELOG.md`](../CHANGELOG.md) |
| Contribute code | [`../CONTRIBUTING.md`](../CONTRIBUTING.md) |
| Read the load-bearing decisions and why they were made | [`adr/`](adr/) (Architecture Decision Records) |
| Avoid proposing something already considered and rejected | [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) |
| Wire up OpenTelemetry traces / spans / metrics | [`OBSERVABILITY.md`](OBSERVABILITY.md) |
| Know the per-method performance budget on the chat hot path | [`HOT_PATH.md`](HOT_PATH.md) |
| One-command first-time setup (build + test + audit + dashboard) | `powershell -File scripts/onboard.ps1` |
| Fix a runtime incident (sidecar won't start, chat gone deterministic, outbox stuck) | [`RUNBOOK.md`](RUNBOOK.md) |
| Validate an outbox / bridge / personality-pack / native-proof-status payload programmatically | [`schemas/`](schemas/) (JSON Schema 2020-12) |
| Look up the security disclosure channel | [`../SECURITY.md`](../SECURITY.md) (full policy) or [`../SECURITY.txt`](../SECURITY.txt) (RFC 9116 discovery file) |
| Run the most-used commands without remembering script paths | `pwsh ../pal.ps1 list` (verb-driven task runner; `make` works too) |
| Remove generated local clutter without touching source/docs/evidence | `pwsh ../pal.ps1 cleanup` (preview first; add `-Apply` to delete) |
| Get the one-page summary of everything | [`CHEAT_SHEET.md`](CHEAT_SHEET.md) |
| Look up a single surface in a sortable / grep-able table | [`QUICKREF.md`](QUICKREF.md) |
| Read every event PalLLM emits or consumes | [`EVENTS.md`](EVENTS.md) |
| List every environment variable that affects the runtime | [`ENV_VARS.md`](ENV_VARS.md) |
| Read the current rolling counts machine-readably | [`PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) |
| One-line state check from the terminal | `pwsh ../pal.ps1 status` |
| F5-runnable sidecar in any editor that consumes `.vscode/` configs | `.vscode/launch.json` is preconfigured |
| Build the right mental model in 5 minutes | [`MENTAL_MODEL.md`](MENTAL_MODEL.md) |
| Look up what's guaranteed to be true at runtime | [`INVARIANTS.md`](INVARIANTS.md) |
| Use the review checklist (human or agent) | [`REVIEW_CHECKLIST.md`](REVIEW_CHECKLIST.md) |
| Get a single-shot agent context JSON | `pwsh ../pal.ps1 context` (also `make context`) |
| Spend a guided 60-minute tour from clone to first ship | [`FIRST_HOUR.md`](FIRST_HOUR.md) |
| Pick the right model quantization (NVFP4 vs FP8 vs Q4_K_M etc.) | [`QUANTIZATION.md`](QUANTIZATION.md) |
| Apply GPU serving patterns to your own app (startup snippets + prompt templates + monitoring patterns) | [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md) |
| Install + tune the bundled local-inference engine with hardware-aware backend selection (Tiers A-F per GPU class) | [`LLAMA_CPP_BUNDLED.md`](LLAMA_CPP_BUNDLED.md) |
| Check v1.0 minimum-requirements (reference rig spec) | [`MINIMUM_REQUIREMENTS.md`](MINIMUM_REQUIREMENTS.md) |
| See PalLLM's SLOs + Prometheus alert rules + Grafana dashboard (Pass 358) | [`OBSERVABILITY_SLO.md`](OBSERVABILITY_SLO.md) |
| See what's deferred to post-release (heavyweight models, alternate hardware) | [`POST_RELEASE_ANNEX.md`](POST_RELEASE_ANNEX.md) |
| Wire vision-in / audio-in / audio-out via a multimodal-capable inference engine (recipes + realtime WS) | [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md) (or `pwsh ../pal.ps1 connect omni`) |
| Apply 2026 agentic patterns (Tool Search Tool / Programmatic Tool Calling / Pyramid MoA) | [`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md) |
| Plan long-running companion memory beyond the working window (Mem0 / Letta / Zep / archival store) | [`MEMORY_RECIPES.md`](MEMORY_RECIPES.md) |
| Cleanly uninstall PalLLM from Palworld (one-click + dry-run + full-wipe) | [`UNINSTALL.md`](UNINSTALL.md) (or double-click [`../uninstall.bat`](../uninstall.bat)) |
| Get started with zero technical knowledge | [`EASY_MODE.md`](EASY_MODE.md) |
| See the candid 10/10 scorecard ("is it ready for me?") | [`READINESS.md`](READINESS.md) (or `pwsh ../pal.ps1 readiness`) |
| Wire PalLLM into any MCP-capable desktop or IDE client in one command | `pwsh ../pal.ps1 mcp connect <client>` |
| Check for a newer PalLLM release on GitHub | `pwsh ../pal.ps1 check-updates -Owner <fork>` |
| See the most recent CHANGELOG entry without leaving the terminal | `pwsh ../pal.ps1 news` (`-Count N` for the last N entries) |
| Validate `agents.json` against its formal JSON Schema | [`schemas/agents.schema.json`](schemas/agents.schema.json) (referenced from `agents.json` `$schema` key) |
| Get a single-action "what should I do right now?" recommendation based on current state | `pwsh ../pal.ps1 next` (probes sidecar / inference / packs / audit; recommends ONE action) |
| Take an interactive 60-second guided tour of "you just installed this; what now?" | `pwsh ../pal.ps1 welcome` (or `-Quick` for an at-once readout) |
| Take a 30-second self-running tour of the companion across six fallback families | `pwsh ../pal.ps1 demo` (live or canned) |
| Browse all 19 deterministic fallback strategies as scenario cards with example replies | [`PROMPT_CARDS.md`](PROMPT_CARDS.md) |
| Have a five-minute campfire moment with your companion outside the game (REPL) | `pwsh ../pal.ps1 campfire` (slash: `/whisper` `/fortune` `/quest` `/tale`) |
| Read the in-character daily fortune (date-seeded; same all day) | `pwsh ../pal.ps1 fortune` |
| Get one quiet ambient one-liner from the companion (no fanfare) | `pwsh ../pal.ps1 whisper` |
| Ask the companion to suggest a small ~30-minute self-contained challenge | `pwsh ../pal.ps1 quest -Tier easy/medium/spicy/quiet` |
| Hear a 3-4-line in-character campfire story | `pwsh ../pal.ps1 tale` (or `-Title <prefix>`) |
| Read the companion's patrol report from the night you slept (4-6 lines, atmospheric) | `pwsh ../pal.ps1 patrol-report` |
| Run a single-command readiness checklist with READY / NEARLY READY / NOT READY verdict | `pwsh ../pal.ps1 preflight` |
| Scaffold a new personality pack with valid manifest + computed `ContentHash` | `pwsh ../pal.ps1 pack new -Id <id> -DisplayName "..." -Author <name>` |
| List the personality packs the running sidecar has loaded | `pwsh ../pal.ps1 pack list` |
| See the moments catalog (scripted reactive lines tied to in-world triggers) | [`MOMENTS.md`](MOMENTS.md) |
| Wire PalLLM's inference path to a local chat-completions engine in one command | `pwsh ../pal.ps1 connect <engine>` (auto-recommend by hardware) |
| Print or wire a raw GGUF server lane | `pwsh ../pal.ps1 connect <gguf-engine> -ModelPath <model.gguf>` |
| Print or wire a local desktop `/v1` model-server lane | `pwsh ../pal.ps1 connect <desktop-engine> -Model <loaded-id>` |
| Pick a Blackwell / Hopper / Ampere recipe and copy-paste the inference-server docker command | `pwsh ../pal.ps1 connect <gpu-engine> -UseCase companion` |
| Print or wire a lightweight transformers serving lane | `pwsh ../pal.ps1 connect <transformers-engine> -Revision <sha>` |
| Print or wire an accelerated `/v1` GPU-serving lane | `pwsh ../pal.ps1 connect <accelerated-engine>` |
| Print or wire an Intel CPU/GPU/NPU `/v3` model-server lane | `pwsh ../pal.ps1 connect <intel-engine> -TargetDevice GPU` |
| Print or wire a local Windows ML lane | `pwsh ../pal.ps1 connect <windows-ml-engine> -FoundryEndpoint <url>` |
| Pick a starting personality pack (Warrior / Scholar / Healer / Trickster) | [`PACK_SAMPLES.md`](PACK_SAMPLES.md) |
| Compute the canonical `ContentHash` for a personality pack | `pwsh ../scripts/compute-pack-hash.ps1 ./my-pack -Update` |
| Export an anonymized support bundle for triage | `pwsh ../pal.ps1 support` (or double-click [`../support.bat`](../support.bat)) |
| Summarize native delivery proof status without starting a watcher | `pwsh ../pal.ps1 proof` (`-RequireProven` for release gating) |
| See recent activity (launch evidence + native artifacts + latest audit) without building a full bundle | `pwsh ../pal.ps1 logs` (`-WhereOnly` for just the paths) |
| Run the local publication checks without building a package | `pwsh ../pal.ps1 publish-audit` |
| Run a local AOT/trim readiness scan without publishing | `pwsh ../pal.ps1 aot-readiness` (`-PublishProbe` opts into native publish) |
| Browse harvestable units (capabilities to lift into another project) | `pwsh ../pal.ps1 harvest` (or `harvest show "<name>"`) |
| Read PalLLM as an AI agent — what it is, what's allowed, what's gated | [`../agents.json`](../agents.json) (machine-readable companion to [`../AGENTS.md`](../AGENTS.md)) |
| Discover every `pal.ps1` verb programmatically without parsing PowerShell | [`../pal.json`](../pal.json) (machine-readable verb manifest) |
| Get a structured explanation of any file: kind, surface, deps, related docs and tests | `pwsh ../pal.ps1 explain <path>` |
| Walk an interactive 5-question first-time setup wizard for `appsettings.json` | `pwsh ../pal.ps1 config wizard` |
| See the effective config (file + env + compiled defaults) with each key's source | `pwsh ../pal.ps1 config show` (`-Section <Name>` to filter) |
| Measure real-world chat-turn latency vs the per-tier `HOT_PATH.md` budget | `pwsh ../pal.ps1 benchmark` (`-Probes N` to override sample size) |
| Find files by topic in plain English: "where is the chat hot path?" | `pwsh ../pal.ps1 where '<query>'` |
| Understand the agent-native design (AGENT-CARD blocks, manifests, drift gates) | [`AGENT_NATIVE.md`](AGENT_NATIVE.md) |
| Understand the operator + contributor UX choices | [`UX_PRINCIPLES.md`](UX_PRINCIPLES.md) |
| Scaffold placeholder files for a new advisor / builder / etc. | `pwsh ../pal.ps1 scaffold <kind> <Name>` |
| Add an HTTP endpoint / fallback strategy / MCP tool / config flag | [`COOKBOOK.md`](COOKBOOK.md) (step-by-step recipes) |
| Find which file an extension goes in | [`EXTENSION_POINTS.md`](EXTENSION_POINTS.md) |
| See the chat / bridge / memory data flows visually | [`DATAFLOW.md`](DATAFLOW.md) (Mermaid sequence diagrams) |
| See the breaker / bridge worker / promotion ledger as state machines | [`STATE_MACHINES.md`](STATE_MACHINES.md) |
| Write a test for a new feature | [`TESTING.md`](TESTING.md) (testing cookbook) |

## By Diataxis quadrant

### Tutorial - learning-oriented

- [`PITCH.md`](PITCH.md) - plain-English tour of what PalLLM is, who it's for, and why it's interesting. Start here if you're new. Five-minute read.
- [`FAQ.md`](FAQ.md) - Q&A format for the most common questions (cost, safety, hardware, privacy, uninstall). Two-minute skim.
- [`QUICKSTART.md`](QUICKSTART.md) - the whole path from clone to first chat reply, no model required.
- [`MCP_QUICKSTART.md`](MCP_QUICKSTART.md) - 5-minute walk-through from downloaded release ZIP to working companion chat inside an MCP-capable desktop client. Ships with ready-to-paste example configs under [`examples/`](examples/).

### How-to - task-oriented

- [`OPERATIONS.md`](OPERATIONS.md) - health probes, metrics, retention, the opt-in feature matrix with per-feature enable/verify/rollback steps, troubleshooting, upgrades.
- [`OBSERVABILITY.md`](OBSERVABILITY.md) - wire up OpenTelemetry traces and spans in 30 seconds. Full span inventory, the local Jaeger workflow, vendor-agnostic OTLP setup, and how to disable specific instrumentation when benchmarking PalLLM-only code.
- [`HOT_PATH.md`](HOT_PATH.md) - per-method performance budgets for the chat path, world snapshot, health/posture, bridge, and memory. Cold and warm targets across Constrained / Standard / Generous hardware tiers. Treat as the design contract for future refactors of those methods.
- [`RUNBOOK.md`](RUNBOOK.md) - incident response playbook. "Sidecar won't start", "chat returns deterministic replies even though I have inference enabled", "outbox isn't emptying", "drift audit failing in CI but not locally", "memory store is corrupt", and the last-resort recovery path.
- [`schemas/`](schemas/) - JSON Schema 2020-12 contracts for the off-HTTP wire shapes: outbox envelope, bridge event envelope, personality pack manifest, and `pal proof -Json` native-proof status. Drop-in for ajv / `Test-Json` / any other validator.
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) - hardware-aware local-model collaboration patterns, especially dense-plus-MoE worker-and-judge pairings.
- [`RELEASE.md`](RELEASE.md) - publication checklist, release-facing copy guardrails, and the current publication blockers.
- [`TLS.md`](TLS.md) - deploying PalLLM behind a TLS-terminating reverse proxy (Caddy, nginx, Traefik). Covers auto-HTTPS, MCP Streamable-HTTP / SSE passthrough, HSTS, and the hardening checklist for public exposure.
- [`SERVER_OPERATOR.md`](SERVER_OPERATOR.md) - dedicated-server deployment guide: Topology A (sidecar on server box) vs Topology B (separate AI host), systemd unit, per-player rate-limiting, backup/upgrade flow, privacy disclosure for server admins.
- [`PRIVACY.md`](PRIVACY.md) - complete inventory of every data-emitting surface classified as never-leaves / only-with-opt-in / leaves-by-default. Mirrored by the machine-readable `GET /api/privacy/posture` surface.
- [`TUNING.md`](TUNING.md) - consolidated reference for every configurable parameter across all eight option blocks: default, min/max, what-if-too-low, what-if-too-high, how to test, owner, runtime-adjustable? Includes the runtime-personality "real vs fake vs simplified" classification and the per-turn performance budget.
- [`PACK_AUTHORING.md`](PACK_AUTHORING.md) - write a narrative pack, validate it, hot-reload it, debug why lore isn't being picked up.

### Reference - information-oriented

- [`../README.md`](../README.md) - canonical route inventory, runtime posture, bridge flow, feature pointers.
- [`../AGENTS.md`](../AGENTS.md) - agent-oriented root-level briefing: essential reading order, non-negotiable invariants, working loop, anti-patterns, handoff protocol.
- [`../CLAUDE.md`](../CLAUDE.md) - Claude-Code-specific quick reference with drift-gate shortcuts.
- [`CHEAT_SHEET.md`](CHEAT_SHEET.md) - one-page summary of everything: every `pal.ps1` verb, every key file, every drift gate, the hard rules, the layout, and the "I want to add X" quick map.
- [`QUICKREF.md`](QUICKREF.md) - sortable / grep-able alphabetical table of every surface: pal.ps1 verbs, drift gates, hot-path budgets, OpenTelemetry spans, ResponsePath values, bridge directories, runtime root layout, health endpoints, configuration root keys, environment variables, doc map.
- [`EVENTS.md`](EVENTS.md) - reference for every event PalLLM emits or consumes: bridge inbox/outbox event types, OpenTelemetry spans + tags, Prometheus metrics (counters / gauges / histograms).
- [`ENV_VARS.md`](ENV_VARS.md) - every environment variable, default, and effect. Covers ASP.NET Core, OpenTelemetry, PalLLM-specific overrides, and the `PalLLM__*` env-var pattern for any `PalLlmOptions` field.
- [`PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) - machine-readable rolling state for tests / drift gates / build warnings / routes / features / fallback strategies / ADRs / honest roadmap. Single source of truth for cross-doc numbers.
- [`COOKBOOK.md`](COOKBOOK.md) - step-by-step recipes for the nine most common changes (HTTP endpoint, fallback strategy, MCP tool, config flag, advisor / builder, bridge event, guarded action, feature-catalog entry, ADR).
- [`SUGGESTIONS.md`](SUGGESTIONS.md) - architecture + recipe for the operator-actionable runtime suggestions surface. Documents the data model, severity buckets, ordering contract, eleven live hint codes, nine consumer surfaces, and the one-line builder edit that lights up all of them.
- [`FUTURE_2035.md`](FUTURE_2035.md) - horizon scan of cutting-edge ideas (Tool Search Tool, three-tier memory graph, speculative replies, sleep-mode dreaming, LoRA hot-swap, Pyramid MoA, Programmatic Tool Calling, federated companion identity, multi-agent mesh, diffusion narration). Each idea names where it slots into the existing architecture, the first deliverable, what blocks it today, and which of the four hard rules it must not violate.
- [`EXTENSION_POINTS.md`](EXTENSION_POINTS.md) - "where does the new code for X go?" map. Pairs with the cookbook: cookbook is verbose, extension-points is one-line per surface area.
- [`DATAFLOW.md`](DATAFLOW.md) - Mermaid sequence diagrams for the major flows: player chat, bridge inbox drain, memory persistence, vision describe, promotion lifecycle, chat streaming.
- [`STATE_MACHINES.md`](STATE_MACHINES.md) - Mermaid stateDiagrams for the explicit subsystems: inference circuit breaker, bridge inbox worker, promotion ledger, TTL cache, chat reply path.
- [`TESTING.md`](TESTING.md) - "how to write a test for X" cookbook. Pure-logic class, TTL-cached surface, HTTP endpoint, MCP tool, streaming endpoint, bridge event, fallback strategy.
- [`CODE_MAP.md`](CODE_MAP.md) - symbol-to-file navigation: where every capability, advisor, builder, validator, and worker lives.
- [`HARVEST.md`](HARVEST.md) - recipe-style guide to lifting individual PalLLM capabilities (Duo planner, proof packets, promotion loop, privacy posture, advisors) into your own project. Each recipe names specific files and public surface.
- [`ADVISORS.md`](ADVISORS.md) - one-page catalog of every advisor / builder / validator / feeder / tracker / store in the codebase with file path, public surface, kind (Pure / Stateful / Cached), and surfacing (HTTP endpoint + MCP tool). The "go here first when harvesting" lookup.
- [`CONVENTIONS.md`](CONVENTIONS.md) - the four patterns (advisor / builder / validator / feeder) + three hard rules (deterministic-first / observer-only / every automated change gets a proof packet) + type-naming cheatsheet.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) - solution layout, HTTP surface, background workers, configuration model, data flow diagram.
- [`API.md`](API.md) - full HTTP API reference: request/response shapes, validation rules, error handling, correlation, the machine-readable bridge proof snapshot, and the machine-readable release/readiness snapshot.
- `GET /api/bridge/proof` in [`API.md`](API.md) is the native-readiness and live-loop proof snapshot for smoke tooling, dashboards, and release checks.
- `GET /api/inference/performance` in [`API.md`](API.md) is the recent per-lane latency-budget, readiness, and token snapshot for dashboards and operator checks.
- `GET /api/release/readiness` in [`API.md`](API.md) is the automation-facing release snapshot for route counts, audit commands, canonical docs, and current publication blockers.
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) - the live planning model behind `/api/inference/collaboration`, the collaboration MCP tool, and the dense-vs-MoE operator guidance.

### Explanation - understanding-oriented

- [`PITCH.md`](PITCH.md) - plain-English narrative: what does PalLLM do, why is it interesting, who is it for, is it safe.
- [`DESIGN_PRINCIPLES.md`](DESIGN_PRINCIPLES.md) - the 10 principles that hold the codebase together (deterministic-first, observer-only, local-first, harvestable by design, etc.) with industry-practice context (Simple Made Easy, 12-Factor, Diátaxis, Hexagonal, llms.txt, AGENTS.md).
- [`adr/`](adr/) - Architecture Decision Records for every load-bearing decision. Six accepted ADRs covering deterministic-first replies, the portable adapter seam, the one-way bridge, drift gates, the TTL-cache pattern, and opt-in defaults. Read these before refactoring a load-bearing piece.
- [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) - what's been deliberately rejected and why. Read this before proposing a "wouldn't it be cleaner if..." change; the maintainers may have already considered it.
- [`MENTAL_MODEL.md`](MENTAL_MODEL.md) - ten paragraphs with analogies that make the core concepts click in five minutes. Read this before opening the code if you're new.
- [`INVARIANTS.md`](INVARIANTS.md) - what's guaranteed to be true at runtime, with the enforcement site for each. The positive complement to ANTI_PATTERNS.
- [`REVIEW_CHECKLIST.md`](REVIEW_CHECKLIST.md) - what a reviewer (human or agent) checks for in a PR. Quick green-light path + conceptual review + per-area dives.
- [`GLOSSARY.md`](GLOSSARY.md) - every PalLLM-specific term (advisor, director, duo, role, posture, proof packet, promotion ledger, etc.) defined in plain English. Look here first if you hit an unfamiliar word.
- [`FALLBACK_AI_RESEARCH.md`](FALLBACK_AI_RESEARCH.md) - the published game-AI ideas behind the 19 deterministic strategies.
- [`CORE_LIBRARY.md`](CORE_LIBRARY.md) - the portable adapter surface (inlined in one self-contained file) and how other runtimes can re-harvest it.
- [`ROADMAP.md`](ROADMAP.md) - phased delivery plan, current audited status, next build order.
- [`REFACTORING_ROADMAP.md`](REFACTORING_ROADMAP.md) - phased monolith-extraction plan for `PalLlmRuntime.cs` (4,744 lines) and `Program.cs` (2,105 lines). Uses C# `partial class` companions so the public surface, DI registrations, and tests don't move while the internal layout improves. Read this before proposing "should we split the runtime?" - the plan is already written and phased.
- [`COMPANION_INTELLIGENCE.md`](COMPANION_INTELLIGENCE.md) - post-foundation PalLLM-specific "pseudo AGI" ideas adapted from the sibling external prompt-pack research without breaking PalLLM's scope guardrails.

## Audience shortcuts

- **Curious layperson**: `PITCH.md` -> `QUICKSTART.md` -> `PRIVACY.md`.
- **First-time user**: `QUICKSTART.md` -> `ARCHITECTURE.md` -> whichever integration doc matches your goal.
- **Operator**: `OPERATIONS.md` -> `API.md` for concrete request/response shapes -> `ARCHITECTURE.md` for the "why".
- **Integrator** (external tool / bridge / custom client): `API.md` -> `ARCHITECTURE.md` section "Runtime data flow" -> `QUICKSTART.md` for a smoke loop.
- **Contributor**: `ARCHITECTURE.md` -> `CONVENTIONS.md` -> `CODE_MAP.md` -> `FALLBACK_AI_RESEARCH.md` -> `ROADMAP.md` -> `IMPLEMENTATION_QUEUE.md`.
- **Temporary coding handoff**: `HANDOFF.md` -> `ROADMAP.md` -> `IMPLEMENTATION_QUEUE.md`.
- **Coding agent** (any MCP- or CLI-capable coding assistant): `../AGENTS.md` -> `../CLAUDE.md` -> `HANDOFF.md` -> `CODE_MAP.md` -> `CONVENTIONS.md` -> `ANTI_PATTERNS.md`.
- **Harvester** (lifting one capability into your project): `HARVEST.md` -> `CODE_MAP.md` -> `adr/` for the original decisions -> `ARCHITECTURE.md` for the integration seam.
- **Performance investigator**: `OBSERVABILITY.md` -> `HOT_PATH.md` -> `adr/0005-ttl-cache-for-posture-surfaces.md`.
- **First-time builder**: `powershell -File scripts/onboard.ps1` (one command) -> `HANDOFF.md` -> `CONTRIBUTING.md`.
- **Pack author**: `PACK_AUTHORING.md` -> `API.md` section `/api/packs/validate` -> `ARCHITECTURE.md` for the memory + relationship tie-in.

## Cross-doc invariants

A few things are intentionally single-sourced and should not diverge across docs:

- **Live test count**: `dotnet test PalLLM.sln` at audit time. Each doc records the number it was audited against; if you find drift, the code is the source of truth.
- **Route count**: `src/PalLLM.Sidecar/Program.cs`. The README reports `/api` routes; `API.md` and `ROADMAP.md` also pin the operational routes (`/`, `/metrics`, `/health/*`, `/openapi/*`) plus the separate `/mcp` protocol route.
- **Feature catalog entries**: `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` and the runtime `GET /api/features` endpoint. The README and catalog should match.
- **Machine-readable release posture**: `src/PalLLM.Sidecar/ReleaseReadinessBuilder.cs` and `GET /api/release/readiness` own the automation-facing release snapshot. `README.md` and `docs/RELEASE.md` should agree with it.
- **Machine-readable bridge proof**: `src/PalLLM.Sidecar/BridgeProofBuilder.cs` and `GET /api/bridge/proof` own the native-readiness and loop-proof snapshot. `README.md`, `docs/API.md`, and `docs/OPERATIONS.md` should agree with it.
- **Weighted completion**: `ROADMAP.md` holds the phase breakdown. The README quotes the headline number; if they drift, `ROADMAP.md` wins.

## Conventions

- Every doc carries a `Last audited:` stamp at the top.
- Concrete code paths use backticks and relative repo paths (`src/PalLLM.Sidecar/Program.cs`), not absolute Windows paths.
- HTTP examples use PowerShell-style `curl` so they run on a default Windows dev box; replace backticks with backslashes on Linux/macOS.
- When a number appears in more than one doc (test count, route count, feature count), one doc explains the source and the rest link to it.
