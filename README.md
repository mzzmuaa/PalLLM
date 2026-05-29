# PalLLM

![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)
![.NET 10](https://img.shields.io/badge/.NET-10.0--LTS-blueviolet.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20Container-lightgrey.svg)
![Tests](https://img.shields.io/badge/tests-1315%20passing-success.svg)
![MCP](https://img.shields.io/badge/MCP-2025--06--18-purple.svg)
![Coverage](https://img.shields.io/badge/coverage-86.9%25%20line%20%7C%2070.4%25%20branch-brightgreen.svg)
![Status](https://img.shields.io/badge/roadmap-76.2%25%20honest-blue.svg)
[![CI](https://github.com/mzzmuaa/PalLLM/actions/workflows/ci.yml/badge.svg)](https://github.com/mzzmuaa/PalLLM/actions/workflows/ci.yml)
[![CodeQL](https://github.com/mzzmuaa/PalLLM/actions/workflows/codeql.yml/badge.svg)](https://github.com/mzzmuaa/PalLLM/actions/workflows/codeql.yml)


**PalLLM gives every companion in *Palworld* its own local AI voice -
on your own computer, with no cloud account, no subscription, and no
data leaving your machine by default.**

- **100% local by default.** No signup. No phone-home. No subscription.
- **Zero-inference fallback.** 19 hand-authored reply strategies keep
  the companion responsive even when no AI model is configured.
- **Scales to your hardware.** CPU-only laptop -> small model or
  deterministic-only. Multi-GPU workstation -> full Duo-mesh mode.
- **Talk to your companion from any chat app.** Built-in MCP server
  exposes 38 tools to any Model Context Protocol-aware desktop or IDE
  client.
- **Privacy posture is inspectable.** A single HTTP call
  (`GET /api/privacy/posture`) enumerates every data-emitting surface
  with `never-leaves` / `only-with-opt-in` / `leaves-by-default` status.

> **New here?** Read [`docs/PITCH.md`](docs/PITCH.md) first - plain-English
> tour of what PalLLM does, why it's cool, and whether it's safe.
> Five-minute read.

> **AI agent reading this in 2026+?** Start with
> [`AGENTS.md`](AGENTS.md), then `docs/MENTAL_MODEL.md`, then
> `docs/HANDOFF.md`. The repo also exposes a single-shot agent
> snapshot:
> ```powershell
> pwsh ./pal.ps1 context        # JSON: counts + ADRs + schemas + freshness
> pwsh ./pal.ps1 status         # human-readable rolling baseline
> pwsh ./pal.ps1 onboard        # one-command first-run setup
> ```
> Tool-specific entry points: [`CLAUDE.md`](CLAUDE.md),
> [`.cursorrules`](.cursorrules), and the editor-specific
> instruction file under `.github/`.
> Universal source of truth: `AGENTS.md`.

## Minimum requirements (v1.0 reference rig)

PalLLM v1.0 ships configured for a specific reference rig. See
[`docs/MINIMUM_REQUIREMENTS.md`](docs/MINIMUM_REQUIREMENTS.md) for
the full spec.

- **GPU:** 12 GB VRAM Ampere-class card (e.g. RTX 3060 12 GB) or newer
- **RAM:** 16 GB DDR4 / DDR5 or better
- **CPU:** 6-core x86 (Zen 3 / 11th-gen Core era) or better, AVX2 required
- **OS:** Windows 10 / 11 x64
- **Disk:** ~80 GB free for the bundled engine + curated models

Other hardware (CPU-only, Apple Silicon, alternate GPU vendors,
multi-GPU) may work via opt-in flags, but is not in the v1.0
shipping support matrix — see
[`docs/POST_RELEASE_ANNEX.md`](docs/POST_RELEASE_ANNEX.md). The
deterministic-fallback companion runs on any host even without
an LLM (see the "Is this ready to use?" section below).

## Is this ready to use?

**Sidecar + dashboard + MCP surface: yes, today.** The packaged
`PalLLM-v1.0.0.zip` installs via `install.bat`, launches via
`play.bat`, and serves the Field Console at
`http://localhost:5088`. 1315 NUnit tests pass. 16/16 drift gates
green. 57 `/api` routes + 38 MCP tools all respond on a cold-boot
of the packaged single-file `.exe`.

**In-game experience: 76.2% honest.** The sidecar runtime is
production-ready. The *player-visible* story still has three gaps
that can only be closed with live in-game sessions: native HUD
widget binding, native in-world audio playback, and native action
executor coverage beyond the current feedback-only paths. You can
already chat with the companion from the dashboard + MCP clients
today; in-game delivery needs the remaining native work to feel
seamless. [`docs/ROADMAP.md`](docs/ROADMAP.md) has the honest
phase-by-phase math.

**TL;DR for an end user:** install it, open the dashboard, chat
with your companion there. The in-Palworld surface is still
shipping; the runtime under it is done.

## How it works (one paragraph)

A single ASP.NET Core sidecar does the thinking on your machine; a
Windows Lua bridge (UE4SS) carries events into Palworld and replies
back out. The sidecar owns a portable adapter surface in
[`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`](src/PalLLM.Domain/Portable/PortableAdapterContracts.cs)
so the published binary is self-contained and redistributable. A
deterministic director keeps the companion responsive when live
inference is off, broken, or rate-limited - the player is never left
with a mute companion.

### Topology at a glance

```text
            +-----------------------------------------------------+
            |  Palworld + UE4SS (Windows-only)                    |
            |                                                     |
            |   +----------+                  +--------------+    |
            |   | Lua mod  |  -- renders -->  | Native HUD / |    |
            |   | main.lua |  <-- events --   | audio /      |    |
            |   +----------+                  | action exec  |    |
            |        |                        +--------------+    |
            +--------+--------------------------------------------+
                     | events                 ^ replies
                     v (Bridge/Inbox/*)       | (Bridge/Outbox/*)
            +-----------------------------------------------------+
            |  PalLLM Sidecar (.NET 10 LTS, ASP.NET Core)         |
            |  Cross-platform: Windows / Linux / macOS / OCI      |
            |                                                     |
            |   InboxWorker -> Runtime -> /api/chat -> Outbox     |
            |                    |                                |
            |                    +- FallbackBehaviorEngine (19)   |
            |                    +- MemoryStream + Relationships  |
            |                    +- NarrativePackService (packs)  |
            |                    +- Vision / TTS / ASR / Actions  |
            |                    +- /api/* (57) + /mcp (38 tools) |
            |                                                     |
            |   Field Console dashboard at http://localhost:5088/  |
            +-----------------------------------------------------+
                     | prompts / tool calls       ^ completions
                     v (HTTP, chat-completions)   |
            +-----------------------------------------------------+
            |  Local inference (any chat-completions endpoint)    |
            |  9 first-party connectors covering CPU / GPU / NPU  |
            |  paths and self-hosted multimodal lanes.            |
            |  Wired in one verb: pal connect <target>            |
            |  Full list: pwsh ./pal.ps1 connect                  |
            +-----------------------------------------------------+
```

Three independent processes, three independent crash domains.
Inference is optional; when it's off the deterministic director
still produces a competent reply. When it's on, the same path
just splices the model's output through the same presentation
plan. The bridge is one-way + advisory: the sidecar never reaches
into Palworld; the mod consumes outbox files at its own cadence.

> **Unaffiliated third-party project.** PalLLM is not affiliated with,
> endorsed by, or sponsored by any game publisher, game developer,
> middleware vendor, or model provider. See [`NOTICE.md`](NOTICE.md) for
> the full disclaimer and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)
> for the components PalLLM calls at runtime.

> **Status:** `76.2%` on the honest player-experience-weighted roadmap
> (scaffolded features at 40% credit; see `docs/ROADMAP.md`, 2026-04-23
> honesty pass). Sidecar runtime is effectively production-ready; the
> remaining ~24pp live in the UE4SS Lua mod (native HUD binding, native
> audio playback, full action executor coverage).
> `1315` tests passing. `57` `/api` routes + complete `MCP` server at `/mcp`
> (`38` tools, `6` direct resources + `1` template, `4` prompts). `122`
> feature-catalog entries (`119 ready / 2 scaffolded / 1 deferred`).
> `19` deterministic fallback strategies.
> OpenAPI 3.1 spec at `/openapi/v1.json` and `/openapi/v1.yaml`.
> Publication-facing snapshot schemas use neutral ids (`GameWorldSnapshot`,
> `GameBaseSnapshot`, `GameCharacterSnapshot`) even though the current live
> bridge target still remains Palworld-specific operationally.
> Machine-readable release posture at `/api/release/readiness`, including the latest durable `SmokeEvidence` snapshot from `Runtime/ReleaseEvidence/latest-smoke.json`, the latest live `NativeProofEvidence` snapshot from `Runtime/ReleaseEvidence/latest-native-proof.json`, the latest packaged `ProofBundleEvidence` manifest/archive from `Runtime/ReleaseEvidence/latest-proof-bundle.json` + `.zip` with compact inference-performance receipt counts for upstream request IDs, response identity, finish reasons, processing/phase timing, and tokens plus content-free TTS/ASR call-success, ASR endpointing, ASR confidence, and ASR timing evidence, the latest portable `SupportBundleEvidence` manifest/archive from `Runtime/SupportEvidence/latest-support-bundle.json` + `.zip`, the latest `PackageVerificationEvidence` snapshot from `Runtime/ReleaseEvidence/latest-package-verification.json`, the latest `ArtifactIntegrityEvidence` snapshot from `Runtime/ReleaseEvidence/latest-artifact-integrity.json`, the latest `FullAuditEvidence` snapshot from `Runtime/ReleaseEvidence/latest-full-audit.json`, and freshness markers so stale proof is visible before a release tag.
> Machine-readable bridge proof at `/api/bridge/proof`, including a HUD-bind recommendation shortlist plus the live native-hud config source/path.
> Committed contract snapshot at [`docs/openapi/palllm-sidecar-v1.json`](docs/openapi/palllm-sidecar-v1.json), regenerated and verified by `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-openapi.ps1`.
> Last audit: **2026-05-17**.
> Full breakdown: [`docs/ROADMAP.md`](docs/ROADMAP.md).
> Change log: [`CHANGELOG.md`](CHANGELOG.md).
> Release checklist: [`docs/RELEASE.md`](docs/RELEASE.md).

---

## Contents

- [What PalLLM does](#what-palllm-does)
- [Quickstart](#quickstart)
- [Solution layout](#solution-layout)
- [Portable adapter surface](#portable-adapter-surface)
- [Runtime surface and defaults](#runtime-surface-and-defaults)
- [Bridge flow (high level)](#bridge-flow-high-level)
- [What's shipped vs. what's remaining](#whats-shipped-vs-whats-remaining)
- [Documentation map](#documentation-map)
- [Contributing](#contributing)
- [License](#license)

---

## What PalLLM does

- **Runs a local sidecar** that hosts chat orchestration, memory, narrative
  packs, relationships, reflection, vision, session persistence, TTS, ASR,
  action intents, metrics, and a read-only dashboard.
- **Emits standards-based OTLP observability on demand** so opt-in
  deployments export runtime spans, correlated logs, GenAI client
  latency/token histograms, and recent-window lane-readiness gauges
  without changing the default localhost posture.
- **Promotes live-lane readiness into first-class operator signals** so
  `/health/ready`, `/metrics`, and OTLP all expose the same bounded
  recent-window status (`healthy`, `degraded`, `critical`,
  `insufficient_data`, `no_data`) for live chat and vision lanes.
- **Drains a live game bridge** populated by a Lua producer
  (chat, base discovery, combat, pal status, weather, raids, travel
  samples, widget-probe diagnostics, screenshots, and a guarded
  production sampler scaffold).
- **Replies through a deterministic director** (19 fallback strategies
  plus a general fallback) that emits a multi-sentence reply with a full
  visual + audio presentation plan - no live model required.
- **Delegates live inference** to any HTTP server that follows the JSON
  chat-completions schema, when enabled. Operator picks the server. When the
  endpoint is a compatible local runtime, PalLLM can also apply residency
  hints so warmup keeps the active model hot instead of paying a cold load on
  the next turn.
- **Applies task-aware execution profiles** so the live chat path can switch
  thinking mode, sampling, token budget, live vision use, and prompt evidence
  budget per turn based on the active model lane and task shape instead of one
  global preset.
- **Publishes machine-readable collaboration plans** for multi-model local
  stacks so dense judge lanes and fast worker lanes can be routed
  deliberately instead of guessed ad hoc.
- **Publishes a machine-readable release/readiness snapshot** so docs,
  automation, and release tooling can discover the shipped surface,
  audit commands, current publication blockers, and the latest durable
  smoke, live native-proof, packaged proof-bundle, portable support-bundle,
  and package-verification artifacts without scraping markdown.
- **Ships a one-click player launcher** so the release zip has a single
  obvious entry point (`play.bat`) that installs or refreshes the mod,
  starts or reuses a bundled self-contained sidecar by default, primes the
  active inference lane when warmup is enabled, runs doctor, writes a durable
  launch snapshot under `Runtime/LaunchEvidence/latest-player-launch.{json,md}`,
  opens the dashboard, and launches Palworld with lower-level
  sidecar fallbacks still available.
- **Ships a one-click support bundle exporter** so release installs can capture
  the latest launch evidence, health snapshots, bridge proof, and
  release-readiness artifacts into `Runtime/SupportEvidence/latest-support-bundle.zip`
  without asking players to assemble files by hand.
- **Publishes a machine-readable bridge proof snapshot** so operators and
  automation can inspect native readiness, widget-seam evidence, HUD-bind
  recommendations, and live request/delivery closure without reconstructing
  that truth from multiple endpoints.
- **Returns replies to the game** via structured `chat_reply` envelopes in
  `Bridge/Outbox` that the Lua layer renders as staged cue-aware themed
  cards plus best-effort TTS playback and a guarded action executor, with
  a UMG widget attachment scaffold (kill-switched; see
  [`OPERATIONS.md`](docs/OPERATIONS.md), section "Enabling the native HUD bind")
  standing by for a native HUD bind.
- **Proves the bridge loop instead of assuming it.** `RuntimeHealth.BridgeLoop`
  and the doctor/smoke scripts now distinguish request seen, outbox written,
  visible delivery confirmed, and matching action feedback, so "healthy
  sidecar" is no longer mistaken for "the Palworld turn really rendered."
- **Persists release smoke evidence** under `Runtime/ReleaseEvidence` so a
  successful smoke run leaves behind both `latest-smoke.json` and a timestamped
  history artifact that release tooling can harvest later.
- **Persists live native proof evidence** under `Runtime/ReleaseEvidence` so a
  successful `scripts/run-native-proof.ps1` pass leaves behind
  `latest-native-proof.json` plus a timestamped history artifact tied to a
  real Palworld bridge-proof snapshot.
- **Captures a release proof bundle** so a successful
  `scripts/export-release-proof-bundle.ps1` pass leaves behind
  `latest-proof-bundle.json` and `latest-proof-bundle.zip`, plus timestamped
  history artifacts, bundling the current release/readiness snapshot,
  bridge proof, inference-performance snapshot, smoke artifact,
  native-proof artifact, and HUD config when present. The manifest carries
  compact inference status, lane, upstream request-id, response/fingerprint,
  finish-reason, and token-receipt counts without raw prompt or completion
  text.
  `/api/release/readiness` verifies that paired archive is readable and
  contains the manifest-listed files before trusting it as recorded proof.
- **Verifies a concrete release package** so a successful
  `scripts/package-release.ps1` or `scripts/verify-release-package.ps1` pass
  leaves behind `latest-package-verification.json`, proving that a candidate
  zip matches its embedded `RELEASE_PACKAGE_MANIFEST.json` before clean-machine
  install validation starts.
- **Records release artifact integrity evidence** so a successful
  `scripts/compute-release-checksums.ps1` pass leaves behind
  `latest-artifact-integrity.json`, `SHA256SUMS`, `SHA512SUMS`, and
  `checksums.json`, making local digest manifests visible through
  `/api/release/readiness` before publication.

## Quickstart

### Prerequisites

- **Palworld** (any recent build - the mod auto-detects and logs which
  UE4SS hooks survived the current patch).
- **UE4SS v3.x or newer** installed into your Palworld `Win64` folder.
  [UE4SS releases](https://github.com/UE4SS-RE/RE-UE4SS/releases).
- **.NET 10 SDK** only if you are building from the repo or intentionally
  producing a framework-dependent package. Official release zips now bundle a
  self-contained sidecar by default.
- Windows 10/11 for the UE4SS bridge; the sidecar itself is portable .NET
  and also runs on Linux/macOS.

### Player install (from a release zip)

1. Download the latest release zip from **[GitHub Releases](../../releases)**.
2. Extract anywhere writable.
3. Double-click **`play.bat`**. It auto-detects Palworld, installs or
   refreshes the mod, starts or reuses the sidecar, primes the active
   inference lane when warmup is enabled, runs doctor, writes
   `Runtime/LaunchEvidence/latest-player-launch.json` plus `.md`, opens the
   dashboard, and launches Palworld.

> **First-time, non-technical user?** Once the sidecar is running,
> open <http://localhost:5088/welcome.html> in any modern browser
> (Chrome, Edge, Firefox, Safari). It's a friendly chat surface
> with a Pal avatar, voice input/output, accessibility toggles, and
> install-as-PWA support - all on a default install, zero
> configuration. The full operator dashboard at `/` is one click
> away when you want it.
4. If something looks wrong, double-click **`support.bat`**. It writes
   `Runtime/SupportEvidence/latest-support-bundle.zip` plus `.json` with the
   latest launch, health, bridge-proof, and release-readiness evidence.
5. If you want the manual path instead, run:
   ```powershell
   install.bat
   powershell -File scripts\start-sidecar.ps1
   powershell -File scripts\doctor.ps1 -RunSmoke
   ```
6. When Palworld starts, the UE4SS console shows
   `[PalLLM] UE4SS bridge booting` plus a
   `[PalLLM][Compat] ...` line summarising which hooks resolved against
   your current game version.

### Run with local inference (one command)

The sidecar ships fully functional without an LLM (deterministic
fallback director keeps companion dialogue responsive); to enable
live inference, install the bundled local-inference engine and
wire PalLLM to it in a single command:

```powershell
pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch
```

That one command does:

1. **Detect** your GPU vendor, VRAM, system RAM, and toolkit
   version cross-platform (Windows / Linux / macOS arm64 /
   macOS x64).
2. **Pick** the right backend asset (`cuda12` / `cuda13` / `vulkan` /
   `hip` / `sycl` / `cpu`) — the 12.x toolkit lane is the
   workstation-safe default.
3. **Download** the latest stable upstream release (asset +
   matching `cudart-*` runtime DLL pack for CUDA backends) with
   SHA-256 verification.
4. **Smoke-test** the binary by running `llama-server --version`,
   with actionable hints when the cudart/VC++ runtime is missing.
5. **Recommend** the best curated GGUF that fits your VRAM (MoE
   models get partial CPU offload via `--n-cpu-moe`; quantized KV
   cache for tight VRAM).
6. **Wire PalLLM**'s `appsettings.json` with the recommended
   model and the per-family sampler profile (each curated family
   ships its own documented sampler).
7. **Launch** the server with the hardware-aware + family-aware
   recipe.

Hardware-tier matrix, per-model recipes, and the known-bug
catalog (mmproj-projector crashes, toolkit-version gibberish
bands, broken-spec-decode caveats, etc.) live in
[`docs/LLAMA_CPP_BUNDLED.md`](docs/LLAMA_CPP_BUNDLED.md).

### Container deployment (for running the sidecar on a remote machine)

```bash
# From the repo root. The build is self-contained - no sibling checkout
# required (see docs/CORE_LIBRARY.md for the portable adapter surface).
docker build -t palllm:latest .
docker run --rm -p 5088:5088 -v palllm-runtime:/var/palllm palllm:latest
```

See [`docs/OPERATIONS.md`](docs/OPERATIONS.md), section "Container deployment"
for the full set of environment-variable overrides and the remote-Lua
bridge-mounting pattern.

**When the container is reachable from anywhere beyond `localhost`**,
enable bearer-token auth on the way up:
```bash
docker run --rm -p 5088:5088 \
  -e PalLLM__Auth__ApiKey="$(openssl rand -hex 24)" \
  -v palllm-runtime:/var/palllm palllm:latest
```
See [`OPERATIONS.md`](docs/OPERATIONS.md), section "Enabling API-key authentication"
for the full posture. `/metrics`, `/health/*`,
`/openapi/v1.{json,yaml}`, and the static dashboard stay open by default
so monitoring and the public contract still reach the sidecar; flip
`PalLLM:Auth:ProtectMetrics` / `ProtectHealth` to lock those too.

### Developer build (from the repo)

```powershell
dotnet build D:\Coding\PalLLM\PalLLM.sln
dotnet test  D:\Coding\PalLLM\PalLLM.sln      # 1315 passing
dotnet run   --project D:\Coding\PalLLM\src\PalLLM.Sidecar\PalLLM.Sidecar.csproj
```

Sidecar listens at `http://localhost:5088`. The Field Console dashboard
is at `/`, Prometheus exposition at `/metrics`, full JSON at `/api/*`.

Dev-only install helpers (junction instead of copy, so edits land live):

```powershell
powershell -File D:\Coding\PalLLM\scripts\install-dev-mod.ps1
powershell -File D:\Coding\PalLLM\scripts\run-sidecar-smoke.ps1
powershell -File D:\Coding\PalLLM\scripts\run-native-proof.ps1
powershell -File D:\Coding\PalLLM\scripts\export-release-proof-bundle.ps1
powershell -File D:\Coding\PalLLM\scripts\verify-release-package.ps1
powershell -File D:\Coding\PalLLM\scripts\publish-audit.ps1
powershell -File D:\Coding\PalLLM\scripts\aot-readiness.ps1
powershell -File D:\Coding\PalLLM\scripts\run-delivery-replay.ps1
powershell -File D:\Coding\PalLLM\scripts\pal-model-probe.ps1
powershell -File D:\Coding\PalLLM\scripts\doctor.ps1 -RunSmoke -RunDeliveryReplay
```

See [`docs/QUICKSTART.md`](docs/QUICKSTART.md) for the clone-to-first-reply
walkthrough and [`docs/OPERATIONS.md`](docs/OPERATIONS.md),
"Palworld and UE4SS compatibility" for which hooks PalLLM depends on and
how it degrades when a game patch renames one.

## Solution layout

- [`src/PalLLM.Domain`](src/PalLLM.Domain) - portable runtime: memory,
  packs, fallback director, presentation planner, vision, TTS, ASR, session
  persistence, action-intent planning, relationship tracker.
- [`src/PalLLM.Sidecar`](src/PalLLM.Sidecar) - ASP.NET Core minimal-API
  host, background workers, static dashboard under `wwwroot/`.
- [`mod/ue4ss/Mods/PalLLM`](mod/ue4ss/Mods/PalLLM) - UE4SS Lua bridge:
  event producers, outbox consumer, guarded action executor, HUD scaffold.
- [`tests/PalLLM.Tests`](tests/PalLLM.Tests) - NUnit coverage: runtime,
  inference, validation, sidecar endpoints, smoke, delivery replay.
- [`scripts/`](scripts) - install, one-click launch, support-bundle export,
  doctor, smoke, native-proof, proof-bundle export, delivery-replay,
  start-sidecar, package-release, and package-verification PowerShell.
- [`docs/`](docs) - roadmap, architecture, API reference, quickstart,
  operations, operator enablement, pack authoring, fallback research,
  portable-adapter-surface notes, implementation queue.

Full doc index with Diataxis grouping: [`docs/INDEX.md`](docs/INDEX.md).

Verified test status on `2026-05-17`:

```text
$ dotnet test D:\Coding\PalLLM\PalLLM.sln
Passed!  - Failed: 0, Passed: 1315, Skipped: 0, Total: 1315
```

## Portable adapter surface

PalLLM owns its own portable adapter surface, inlined in one file at
[`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`](src/PalLLM.Domain/Portable/PortableAdapterContracts.cs)
(~250 lines, zero external dependencies). It exposes:

- `IGameAdapter` + `ICharacter` + `IWorldClock` + `IPathProvider` +
  `ILogger` - the portable seam a game-specific adapter implements.
- `Vec3` - engine-neutral 3D coordinate struct.
- `SemanticEmbedder` - FNV-1a hashed bag-of-tokens embedder with
  cosine similarity, backed by conversation-memory recall's bounded
  exact-token rerank pass.
- `ResponseCleanup` - strips `<think>` / `<reasoning>` wrappers from
  model output before it reaches the player-visible reply.

The bridge-backed adapter implementation (`BridgeGameCharacter`,
`SnapshotWorldClock`, `RuntimePathProvider`, `AdapterLogger`) lives next door in
[`src/PalLLM.Domain/Integration/BridgeGameAdapter.cs`](src/PalLLM.Domain/Integration/BridgeGameAdapter.cs).
The public HTTP/OpenAPI contract intentionally publishes the snapshot family
under neutral schema ids (`GameWorldSnapshot`, `GameBaseSnapshot`,
`GameCharacterSnapshot`) so external tooling can integrate without depending
on those bridge-specific CLR names.

Generic gameplay-automation modules are intentionally **out of scope** -
they'd need game-native hooks and safety rails first. See
[`docs/CORE_LIBRARY.md`](docs/CORE_LIBRARY.md) for the full re-harvest
contract (any other LLM-companion runtime can copy the file verbatim,
rename the namespace, and implement the interfaces against its own
game adapter) and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)
for dependency terms.

## Runtime surface and defaults

- **57 `/api` routes** under `http://localhost:5088/api/*` - full reference
  in [`docs/API.md`](docs/API.md).
- **6 operational routes**: `/` (dashboard UI), `/metrics` (Prometheus),
  `/health/live`, `/health/ready`, `/openapi/v1.json`, `/openapi/v1.yaml`.
- **1 protocol route**: `/mcp` for the Streamable HTTP MCP server.
- **Machine-readable proof surfaces**: `/api/release/readiness` exposes the
  shipped route inventory, featured surfaces, audit commands, canonical docs,
  current publication blockers, the latest durable smoke artifact, the latest
  live native-proof artifact, the latest packaged proof bundle, the latest
  portable support bundle, and the latest package-verification artifact for
  automation or release tooling, while `/api/bridge/proof` exposes the live
  Palworld-native proof state for bridge boot, widget-seam evidence, and
  request/delivery closure.
- **Inference lane visibility**: `/api/health`, `pal_active_model_tier`, and
  `palllm://model/tier/active` expose the active model, active tier, last-seen
  available models, bounded warmup state, and the residency-control provider
  currently in effect for the active lane.
- **Recent inference-window readiness**: `/api/inference/performance`, the
  Field Console, `/health/ready`, and `/metrics` expose the last `15`
  minutes of live chat and vision work, grouped by provider/model lane with
  budget status, success posture, latency, latest upstream request-id and
  processing/phase timing receipts, latest and aggregate token totals, and the
  latest failure signal.
- **Default posture**: inference off, fallback on, vision off, TTS off, ASR off,
  session persistence on, bridge outbox on. All opt-ins are reversible
  flag edits - no state migration. See
  [`docs/OPERATIONS.md`](docs/OPERATIONS.md), section "Opt-in feature matrix".
- **Built-in HTTP guardrails**: `/api/chat`, `/api/vision/*`,
  `/api/tts/synthesize`, and `/api/audio/transcribe` now use protective
  concurrency gates so bursty
  callers fail fast with `429` instead of wrecking local-model latency.
- **Default model tags** live in `src/PalLLM.Sidecar/appsettings.json` and are
  operator-tunable examples rather than part of the wire contract. See
  [`docs/TUNING.md`](docs/TUNING.md) for the supported knobs and low-latency
  tradeoffs.

## Bridge flow (high level)

```
UE4SS Lua -> Bridge/Inbox/*.json -> BridgeInboxWorker -> runtime state +
memory -> POST /api/chat -> ChatResponse (with presentation plan, optional
Speech artifact, optional ActionIntent) -> Bridge/Outbox/chat_reply-*.json
-> UE4SS Lua consumer renders in-game
```

Live Lua producers currently emit `bridge_boot`, `chat_message`,
`base_discovered`, `combat_start`/`combat_end`, `pal_status`,
`weather_change`, `raid`, coarse live `travel` samples, bounded
`ui_probe` diagnostics, periodic screenshots, and a guarded
`production` sampler (kill-switched by default). Full data-flow diagram
in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md), section "Runtime data flow".

## What's shipped vs. what's remaining

Shipped and tested: local-first runtime, 19 deterministic fallback
strategies, semantic memory with importance scoring + reflection,
per-character relationship tracking, narrative packs, vision endpoints,
screenshot ingest, session persistence with autosave, TTS synthesis, ASR,
advisory action intents + guarded Lua executor, response compression,
ProblemDetails, circuit breaker + transient retry + rate limiter,
bounded directory retention, Prometheus metrics, Field Console dashboard.

Remaining for `100%` (all require in-game validation): native HUD widget
bind (scaffolded, kill-switched), native in-world audio playback, richer
native action coverage, confirmed Palworld hook signatures for the
production sampler, clean-machine install walkthrough, in-Palworld
smoke pass. See [`docs/ROADMAP.md`](docs/ROADMAP.md) for the audited
phase-by-phase breakdown and
[`docs/IMPLEMENTATION_QUEUE.md`](docs/IMPLEMENTATION_QUEUE.md) for the
executable next-builds queue.

## Documentation map

| If you want to... | Open |
|---|---|
| Connect PalLLM to an MCP-capable client in 5 min | [`docs/MCP_QUICKSTART.md`](docs/MCP_QUICKSTART.md) |
| Get a working chat reply in 5 minutes | [`docs/QUICKSTART.md`](docs/QUICKSTART.md) |
| Understand the shape and "why" | [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) |
| Look up an HTTP endpoint | [`docs/API.md`](docs/API.md) |
| Keep a sidecar healthy in prod, or flip on any opt-in subsystem | [`docs/OPERATIONS.md`](docs/OPERATIONS.md) |
| Write a narrative pack | [`docs/PACK_AUTHORING.md`](docs/PACK_AUTHORING.md) |
| Prepare a publishable release | [`docs/RELEASE.md`](docs/RELEASE.md) |
| Plan PalLLM's local model collaboration posture | [`docs/MODEL_COLLABORATION.md`](docs/MODEL_COLLABORATION.md) |
| Read the roadmap | [`docs/ROADMAP.md`](docs/ROADMAP.md) |
| See what's queued to build | [`docs/IMPLEMENTATION_QUEUE.md`](docs/IMPLEMENTATION_QUEUE.md) |
| Resume work after a temporary coding handoff | [`docs/HANDOFF.md`](docs/HANDOFF.md) |
| Read the fallback-AI research | [`docs/FALLBACK_AI_RESEARCH.md`](docs/FALLBACK_AI_RESEARCH.md) |
| See which external interfaces PalLLM consumes | [`docs/CORE_LIBRARY.md`](docs/CORE_LIBRARY.md) |
| Review the latest changes | [`CHANGELOG.md`](CHANGELOG.md) |

Every doc carries a `Last audited:` stamp at the top. When a number
appears in more than one doc (test count, route count, feature count),
one doc owns the source and the rest link to it -
[`docs/INDEX.md`](docs/INDEX.md) "Cross-doc invariants" records which.

## Contributing

See [`CONTRIBUTING.md`](CONTRIBUTING.md) for the build/test loop, the
pre-flight checklist, and the conventions around new features, endpoints,
bridge events, and guarded actions. CI is defined in
[`.github/workflows/ci.yml`](.github/workflows/ci.yml) and runs
`dotnet build` + `dotnet test` on both Windows and Linux plus a doc-drift
audit that fails the build if the mojibake regression, HTTP contract docs,
or cross-doc counts drift.

## License

PalLLM is released under the MIT license. See [`LICENSE`](LICENSE) for
the full text, [`NOTICE.md`](NOTICE.md) for the third-party-affiliation
disclaimer, and [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for
components PalLLM calls at runtime.
