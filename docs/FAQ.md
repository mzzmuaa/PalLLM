# PalLLM FAQ

Last audited: `2026-05-21`

Short answers to the questions that come up most often. If you want
the full story, [`PITCH.md`](PITCH.md) is the plain-English narrative,
and [`INDEX.md`](INDEX.md) is the full doc map.

## About the project

### What is PalLLM in one sentence?

A local-first AI runtime that gives in-game companions in *Palworld*
their own voice � running on your computer, with no cloud account,
no subscription, and no data leaving your machine by default.

### Is it free?

Yes. MIT licensed, no paywall, no "pro tier." If someone asks you
to pay for PalLLM, they're not the project.

### Will this ban my Palworld account?

PalLLM is unaffiliated with Palworld's publisher. The mod uses
UE4SS (a third-party Unreal Engine scripting framework) � you assume
the same risk you assume with any UE4SS mod. The companion runtime
itself never reaches into Palworld's servers; the bridge is one-way
advisory and runs purely client-side. That said: we can't promise
anything about the publisher's stance on client mods, so read the
game's ToS and make your own call.

### Does PalLLM collect any data?

By default, **no.** Run `curl http://localhost:5088/api/privacy/posture`
on a running sidecar � it prints every data-emitting surface
classified as `never-leaves` / `only-with-opt-in` / `leaves-by-default`.
On a fresh install, nothing is in the last bucket. See
[`PRIVACY.md`](PRIVACY.md) for the full inventory.

### Who's behind the project?

See [`NOTICE.md`](../NOTICE.md). PalLLM is an unaffiliated
third-party project. No publisher sponsorship. No cloud company
relationship.

## Setup & requirements

### Do I need a GPU?

**No.** PalLLM detects your hardware (`GET /api/hardware`) and picks
a matching path:

- **CPU only, 8 GB RAM:** deterministic director + tiny model
  (gemma3:1b class) gives you conversational replies with ~2-10s
  latency.
- **Entry-level GPU (4-8 GB VRAM):** Worker role only, fast-reactive
  profile.
- **Single-GPU studio (12-24 GB VRAM):** Worker + optional Judge.
- **Multi-GPU / workstation (48 GB+ total VRAM):** full Duo-mesh
  with speculative patterns worth trying.

The deterministic fallback path works with zero VRAM. You always
get a reply.

### Do I need to install an AI model?

Optional. If you skip it, the deterministic director answers every
chat turn using 19 hand-authored strategies. If you install
[llama.cpp](https://github.com/ggml-org/llama.cpp) (default) or
[vLLM](https://github.com/vllm-project/vllm) (high-config GPUs) and
point PalLLM at it via `pal connect llamacpp` or `pal connect vllm`,
you get live inference with the deterministic path still as fallback.
LM Studio / Foundry Local / TensorRT-LLM / OpenVINO / transformers
also work; see `pal connect` for the full eight-engine list.

### How big is the install?

The release zip is about 60 MB. The self-contained `PalLLM.Sidecar.exe`
bundles .NET 10 so you don't need to install anything else. An AI
model download (if you choose to add one) is separate � `gemma3:1b`
is about 1 GB; `qwen3:4b` is about 3 GB.

### Will it run on Linux or macOS?

The **sidecar** runs on Linux x86_64. The **UE4SS Lua mod** is
Windows-only because Palworld's client is Windows. A dedicated-server
operator on Linux can run the sidecar remotely and have Windows
players connect their clients to it. See
[`SERVER_OPERATOR.md`](SERVER_OPERATOR.md) for Topology B.

### How do I install it?

```
1. Download the latest release zip.
2. Unzip it somewhere writable (Documents works fine).
3. Double-click install.bat.
4. Double-click play.bat.
```

That's the full path. [`QUICKSTART.md`](QUICKSTART.md) covers every
opt-in toggle for power users.

### How do I uninstall it?

Delete the unzipped folder. The mod install also places a small
number of files under your Palworld game folder � `recover.bat`
lists them. If you never flipped `Session.Enabled=true`, nothing
persists outside that folder.

## Using it

### How do I talk to my companion outside the game?

Open your browser to `http://localhost:5088` � the Field Console
dashboard has an inline chat panel. Or connect any MCP-aware desktop
AI client to `http://localhost:5088/mcp` and use the `pal_chat`
tool. See [`MCP_QUICKSTART.md`](MCP_QUICKSTART.md) for a 5-minute
walkthrough.

### How does the companion remember things?

`ConversationMemoryStore` uses deterministic embeddings for
similarity-based recall, per-character memory scope, and importance
scoring. Memory persists to
`{PalSavedRoot}/{RuntimeFolderName}/session.json` when
`Session.Enabled=true` (default). Cross-session lifetime memory is at
`GET /api/relationships/lifetime`.

### Can I change the companion's personality?

Yes, two ways:

1. **Narrative packs** � write or edit a JSON pack under
   `{PalSavedRoot}/{RuntimeFolderName}/Packs/`. See
   [`PACK_AUTHORING.md`](PACK_AUTHORING.md).
2. **Personality packs v1** � a validated pack format with prompt +
   optional audio samples + portrait + content-hash integrity
   check. `pack.json` is bounded, declared files must stay inside
   the pack directory, and the content hash is recomputed from a
   streaming local read before the pack is trusted. See the
   personality-pack entries in the feature catalog and
   `src/PalLLM.Domain/Packs/PersonalityPack.cs`.
   Optional `VoiceRefPath`, `VoiceConsent`, `VoiceConsentNotes`,
   `LoraAdapterPath`, and `MemoryNamespace` fields are schema-valid
   for proof-gated local voice, adapter, and memory-identity
   experiments; they do not change the default text chat fallback
   contract. `VoiceConsent` is required whenever a pack declares a
   voice reference.

### Can the companion actually DO things in the game?

Yes, but only allowlisted actions. Set
`PalLLM:Automation:Enabled=true` + add the specific action ids you
want to `PalLLM:Automation:AllowedActions`. Today the shipped
allowlist includes `waypoint_suggest`, `recall_pals`,
`request_craft_queue` and a few more. See
[`OPERATIONS.md`](OPERATIONS.md) "Enabling the action executor."

### What happens if my internet drops?

Nothing changes. PalLLM doesn't use your internet connection unless
you've explicitly configured an endpoint. Default install is
fully offline. Even with llama.cpp configured on localhost,
no internet needed.

### What if the sidecar crashes?

- A companion mid-conversation: nothing breaks � UE4SS mod sees
  the write fail and queues the event for when the sidecar comes
  back.
- The self-healing watchdog archives orphan events and surfaces the
  problem at `GET /api/self-healing/status`.
- Worst case: `recover.bat` stops everything, prunes stale
  evidence, and restarts cleanly.

## For developers

### Can I harvest individual pieces into my own project?

Yes � that's an explicit design goal. See [`HARVEST.md`](HARVEST.md)
for recipes. The pure-.NET-10 layer in `src/PalLLM.Domain/` has no
Palworld / UE4SS / ASP.NET dependency � most files lift as single
copies.

### Can I contribute?

Yes. See [`../CONTRIBUTING.md`](../CONTRIBUTING.md) for the
contributor checklist. The pre-flight is `dotnet test PalLLM.sln`
and `scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom` � both
green means CI will be green.

### Can I port this to a different game?

Yes. The portable adapter seam is at
`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` � implement
`IGameAdapter`, `ICharacter`, `IWorldClock`, `IPathProvider`,
`ILogger` for your target and you have a new game integration.

### Is this a research project or production-ready?

Sidecar runtime: production-ready (1154 tests, 16/16 drift gates,
self-contained binary, machine-readable release readiness). The
in-game delivery layer is `76.2%` on the honest roadmap � see
[`ROADMAP.md`](ROADMAP.md) for the specific gaps (native HUD
binding, native audio playback, action executor coverage).

## Privacy & safety

### Does it phone home?

No. Zero telemetry, zero analytics, zero crash reports leave the
machine. `GET /api/privacy/posture` is the machine-readable proof.

### What about the AI model endpoint?

Operator's choice. Default config has `Inference.Enabled=false`. If
you turn it on, you pick the endpoint (`Inference.BaseUrl`).
Pointing at `http://127.0.0.1:8080/v1/` (llama-server on localhost,
PalLLM's shipping default) keeps everything on your machine.
Pointing at `https://api.openai.com/v1/` sends chat traffic
off-device � `/api/airgap/verify` will mark that lane
`public-internet` so you know.

### Can I see what it's doing?

Yes. Several complementary surfaces:

- `GET /` � Field Console dashboard (live runtime state)
- `GET /api/describe` � one-shot self-description manifest
- `GET /api/privacy/posture` � data-flow inventory
- `GET /api/airgap/verify` � endpoint-scope classifier
- `GET /api/release/readiness` � release + audit evidence
- `/metrics` � Prometheus scrape

Every automated decision (fallback fires, promotions, self-healing
actions) emits a proof packet with a SHA-256 id and rollback path.

### What's the worst that could happen?

Honest answer: because everything is local-first and the bridge is
one-way advisory, the risk surface is basically "UE4SS mod in
your Palworld folder." See [`COMPATIBILITY.md`](COMPATIBILITY.md)
for the known-conflict matrix and `support.bat` for an automatic
support bundle if something does go wrong.

## Getting help

### Where do I report a bug?

GitHub issues. Use the support-export template � it asks you to
attach `support.bat`'s zip which contains every artifact a
maintainer needs to reproduce.

### Where do I report a security issue?

**Not in GitHub issues.** See [`../SECURITY.md`](../SECURITY.md) for
the private disclosure channel.

### Where else can I read?

- [`PITCH.md`](PITCH.md) � plain-English narrative
- [`QUICKSTART.md`](QUICKSTART.md) � 5-minute first-chat guide
- [`INDEX.md`](INDEX.md) � full doc map by Di�taxis quadrant
- `GET /api/features` on a running sidecar � live feature catalog
  with 122 entries
