# Future direction - companion-runtime ideas through 2035

Last audited: `2026-05-22`

PalLLM today is a local-first companion runtime with a portable adapter
seam, a deterministic fallback director, a 38-tool MCP surface, an
opt-in inference path that reaches eight engines (llama.cpp default; vLLM for high-config GPUs; Ollama removed in Pass 339), an
operator-actionable Suggestions surface, and a 122-entry feature
catalog. Most of the architecture is already in place; what stays
deliberately in front of the project is a set of forward-looking ideas
that fit the existing seams without breaking the four hard rules
(deterministic-first reply pipeline, observer-only automation,
filesystem-only one-way bridge, opt-in everything).

This doc is the project's **horizon scan**. It pairs each idea with:

- **Where it fits** - the existing file or seam it lands on.
- **First deliverable** - the smallest concrete shippable slice.
- **What blocks it today** - why we deliberately haven't shipped yet.
- **Hard-rule check** - which of the four invariants the idea must
  not violate.

If you are an agent or a contributor reading this looking for a
non-trivial change, every idea below has a clear first deliverable and
a clear stop condition. Pick one, ship the slice, and the next slice
becomes obvious.

> **Companion to:** [`ROADMAP.md`](ROADMAP.md) (the current build
> queue, weighted by player-experience), [`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md)
> (specific pattern recipes), [`MEMORY_RECIPES.md`](MEMORY_RECIPES.md)
> (long-context patterns), [`SUGGESTIONS.md`](SUGGESTIONS.md) (the
> operator-actionable diagnostic surface).

## Status quo (what's already there)

PalLLM 1.0 ships these architectural seams that the ideas below
extend rather than replace:

- **Portable adapter surface** in
  `src/PalLLM.Domain/Portable/PortableAdapterContracts.cs` - five
  interfaces (`IGameAdapter`, `ICharacter`, `IWorldClock`,
  `IPathProvider`, `ILogger`) that let the runtime move between
  games without recompiling.
- **Deterministic fallback director** with 19 strategies + emergency
  tier - every chat turn produces a reply even with zero inference.
- **Memory + relationships subsystem** with deterministic embeddings,
  importance scoring, reflection consolidation, and per-character
  affinity / mood / tone tracking.
- **Proof packets** - every automated change carries a SHA-256 id +
  decision + evidence + provenance, queryable post-hoc.
- **Suggestions surface** - operator-actionable hints flowing through
  9 consumer surfaces from one builder.
- **Nine inference connectors** - Ollama, llama.cpp, LM Studio, vLLM,
  vLLM-Omni, transformers serve, TensorRT-LLM, OpenVINO, Foundry
  Local - each with `-DryRun` preview, `.bak` config backup, and
  hardware-tier-aware recipe selection where applicable.
- **MCP-over-HTTP** at `/mcp` with 38 tools, 6 resources + 1 template,
  4 prompts. Discoverable and stable across protocol version
  `2025-06-18`.
- **16 drift gates** in `scripts/run_full_audit.ps1` plus a meta-test
  that pins C# literals against `PROJECT_NUMBERS.json`.

These primitives are the ground every idea below builds on. None of
the ideas requires a new framework, a new bridge model, or a
re-architecture; each one slots into an existing seam.

## Near-term (12-18 months out)

### 1. Tool Search Tool as a meta-MCP surface

**Where it fits.**
[`src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs`](../src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs)
would add a `pal_tool_search` tool. Internal helper indexes the existing
38 tools by name, description, and tags.

**First deliverable.** A single MCP tool that takes a natural-language
query and returns up to 10 matching tool names + descriptions. No
new dependencies; pure in-process search over the attribute table.

**What blocks it today.** The 38-tool surface fits in a single MCP
discovery call. The pattern only pays off above ~80-100 tools, which
is roughly when prompt-bloat starts dominating context. Worth
implementing as a proof-of-concept now so the abstraction is in
place when the surface grows.

**Hard-rule check.** Read-only. No bridge change. Local-first
(in-process index). Honors opt-in (the meta-tool is itself an MCP
tool an operator can choose to ignore).

Recipe pattern documented in
[`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md) Section 1.

### 2. Three-tier memory graph

**Where it fits.**
[`src/PalLLM.Domain/Memory/ConversationMemoryStore.cs`](../src/PalLLM.Domain/Memory/ConversationMemoryStore.cs)
is the working tier today. New `RecallMemoryStore` (vector tier) and
`ArchivalMemoryGraph` (KV-tier) sit alongside it under
`src/PalLLM.Domain/Memory/`.

**First deliverable.** A `RecallMemoryStore` that mirrors the
working store's API but stores summarised chunks indexed by
deterministic embedding. Existing `ReflectionService` writes the
summary; the new store reads it. Vector search remains pure C#
(no embedding-server dependency).

**What blocks it today.** Working-tier memory is sufficient for
most current play sessions. The recall + archival tiers matter most
above ~20 hours of session continuity. Shipping the abstraction
early lets it accumulate data quietly so a later upgrade is just
swapping the index implementation.

**Hard-rule check.** Local-first (no embedding service). Opt-in
(default off; new `Memory:EnableRecall=true` flag). Filesystem-only
persistence.

Recipe pattern documented in
[`MEMORY_RECIPES.md`](MEMORY_RECIPES.md) Section 1.

### 3. Speculative companion replies

**Where it fits.**
[`src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.cs)
`ChatAsync` gains a sibling `WarmAsync(predictedRequest)` that runs
the inference call but cancels on actual-request arrival.

**First deliverable.** When `ChatAsync` produces a fallback reply
because inference was cold, the runtime kicks off a background
`WarmAsync(samePrompt)` that primes the KV cache. Next real turn
hits warm cache.

**What blocks it today.** Most local engines (Ollama, llama.cpp)
already do prompt caching automatically. The speculative win is
small for typical conversation cadence. Worth implementing when
the latency budget tightens (e.g. real-time voice loops).

**Hard-rule check.** Doesn't change the chat contract. Honors
rate limiter (the speculative call goes through the same chat-heavy
limiter). Cancellable.

### 4. Sleep-mode dreaming (idle-tier reflection)

**Where it fits.**
[`src/PalLLM.Domain/Memory/ReflectionService.cs`](../src/PalLLM.Domain/Memory/ReflectionService.cs)
gains a triggered-on-idle path. New
`src/PalLLM.Sidecar/SleepModeDreamWorker.cs` background service
fires when the sidecar has been idle for N minutes.

**First deliverable.** When the bridge inbox has been quiet for
>=10 minutes AND the operator opted in via
`Memory:EnableSleepDreaming=true`, the worker walks the working
memory and asks the inference lane (or fallback director) for a
one-paragraph "what mattered today" summary. Persisted as an
archival memory entry tagged `kind=dream`.

**What blocks it today.** Reflection ships but is per-turn; no idle
trigger exists. Needs the recall/archival tier (idea #2) for the
dream to land somewhere useful.

**Hard-rule check.** Opt-in. Bounded (one entry per idle window).
Cancellable on bridge wake.

### 5a. Hierarchical-reasoning small-model advisor

**Where it fits.** New
`src/PalLLM.Domain/Inference/HrmAdvisor.cs` runs alongside the
existing Duo planner. Targets the May 2026 wave of small models
(`sapientinc/HRM-Text-1B`, `FrontiersMind/Nandi-Mini-600M`) that
ship with built-in hierarchical scratchpad / chain-of-thought. The
advisor exposes a `Reason(prompt) -> (reasoning, finalAnswer)`
shape so a fallback strategy can ask "would a small reasoning model
have a more grounded answer here?" without paying the full
inference-lane cost.

**First deliverable.** A `HrmReasoningResponse` record + an opt-in
HTTP route `POST /api/inference/reason` that forwards to a configured
HRM-class endpoint, returns both the scratchpad and the answer. The
chat path stays on the existing Duo planner; the reasoning lane is
an explicit operator choice.

**What blocks it today.** Hierarchical-reasoning models are still
new on the Hub (HRM-Text-1B started trending Nov 2025). Worth
implementing once one model in this class has six months of
production usage data.

**Hard-rule check.** Local-first (HRM models are small enough to
run on edge tier). Opt-in (new endpoint, not on the chat hot path).
Deterministic fallback preserved (the chat path still works without
the reasoning lane).

Source: [`MODELS_2026.md` §1 — Fast-start chat lane](MODELS_2026.md#1-chat--fast-start-lane-instant-boot).

### 5b. Always-on realtime audio understanding

**Where it fits.** Today's ASR lane transcribes player speech on
demand. A realtime-audio model like
`mistralai/Voxtral-Mini-4B-Realtime-2602` could continuously listen
to *game audio* (not just player voice) and inject signal-level
events back into the bridge — "footsteps approaching from the
north," "boss music started," "raid horn sounded." The companion
then narrates with hearing-grounded awareness, not just snapshot-state
guessing.

**First deliverable.** A new `audio_event` bridge envelope kind
(filesystem one-way, same shape as `chat_message`) carrying detected
audio events from a player-side Voxtral process. PalLLM's runtime
treats them as ambient world events the narration advisor can pick
up. No PalLLM-side audio capture — the realtime model lives in the
operator's chosen audio pipeline.

**What blocks it today.** Voxtral-class realtime models are
brand-new (May 2026). Detected-event taxonomy ("boss-music",
"footsteps", "ambient-rain") needs a stable schema before PalLLM
commits to consuming them.

**Hard-rule check.** Filesystem one-way (audio events flow inbound
only, same as every other bridge event). Opt-in (default off; the
realtime model runs in a separate operator-managed process).
Local-first (no cloud audio service involved).

Source: [`MODELS_2026.md` §5 — ASR](MODELS_2026.md#5-asr--speech-to-text) (Voxtral realtime entry).

### 5c. Hybrid-retrieval memory upgrade

**Where it fits.**
[`src/PalLLM.Domain/Memory/ConversationMemoryStore.cs`](../src/PalLLM.Domain/Memory/ConversationMemoryStore.cs)
already does deterministic FNV-1a bag-of-tokens recall. The 2026
generation of embedding models (`BAAI/bge-m3`) ships dense + sparse
+ multivector retrieval in one model, plus a context-aware exact-token
companion. The upgrade preserves the local-first guarantee — the
embedder still lives in-process — but the *retrieval algorithm*
upgrades from bag-of-tokens-similarity to hybrid scoring.

**First deliverable.** Implement `HybridLocalEmbedder` next to the
existing `SemanticEmbedder` in
`Portable/PortableAdapterContracts.cs`. Initially the dense lane is
still the FNV-1a projection; the sparse lane is BM25 over the
turn's tokens. Both stay deterministic and in-process. Operators can
later wire an external `bge-m3` server if they want the model-quality
dense lane, with the local two-lane hybrid as fallback.

**What blocks it today.** No urgency — the exact-token reranker
added by an earlier pass already covers the worst loss-of-recall
cases. The hybrid lane is the next quality-quartile improvement
on top, not a fix for an outstanding bug.

**Hard-rule check.** Local-first (deterministic dense + sparse +
exact-token lanes all in-process). Default-off external embedder
preserves the no-network promise. Filesystem-only persistence
unchanged.

Source: [`MODELS_2026.md` §6 — Embeddings](MODELS_2026.md#6-embeddings--memory-recall).

### 5. Per-companion LoRA hot-swap

**Where it fits.**
[`src/PalLLM.Domain/Inference/InferenceClient.cs`](../src/PalLLM.Domain/Inference/InferenceClient.cs)
forwards an optional `lora_request` field on chat-completions when
the active personality pack declares one. vLLM and llama.cpp both
support adapter hot-swap natively.

**First deliverable.** `PersonalityPackManifest` now has
`LoraAdapterPath` plus `VoiceRefPath`, `VoiceConsent`,
`VoiceConsentNotes`, and `MemoryNamespace`. The validator keeps those
local to the pack, checks extensions, requires a consent/provenance
category for voice references, caps voice/audio clip files, rejects
remote URLs, and includes declared files in `ContentHash`. Runtime
adapter selection is still future work: when implemented, the
inference request should carry an operator-approved adapter id and
fall back gracefully when the backend reports the adapter unknown.

**What blocks it today.** Adapter publication / signing /
trust-boundary story. Sample packs currently ship plain prompts
only.

**Hard-rule check.** Opt-in (per pack). Local-first (adapter
files live with the pack on disk). Pack publication-safety scanner
already validates pack content.

## Mid-term (3-5 years)

### 6. Pyramid Mixture-of-Agents router

**Where it fits.** New
`src/PalLLM.Domain/Inference/PyramidRouter.cs` - sits in front of
`DuoOrchestratorPlanner`. Reads the chat request, decides between
`{direct, escalate, escalate-full}` based on a tiny dense router
model (Gemma 4 E2B / Qwen3-4B class).

**First deliverable.** A deterministic heuristic router that uses
keyword + task-kind classification (no model). Same interface,
swappable later for the small-dense model.

**What blocks it today.** The dense-router model adds a hot-path
dependency. The Duo planner already covers the 80% case. Worth
implementing when an operator explicitly wants the split.

Recipe pattern documented in
[`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md) Section 3.

### 7. Programmatic Tool Calling sandbox

**Where it fits.** Companion to MCP - companion authors a small
JS / Python script that calls MCP tools as functions, runs in a
locked-down sandbox, returns the final result. Intermediate tool
calls don't enter chat history.

**First deliverable.** Out of scope for an autonomous-loop pass.
Documented in
[`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md) Section 2 with
the contract shape so the next contributor has the design.

**What blocks it today.** Real sandbox (Deno permission flags or
constrained Python) is infrastructure work. Trust boundary
between sandbox and the rest of the runtime needs design.

### 8. Constitutional fallback critique

**Where it fits.**
[`src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs`](../src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs)
gains a per-strategy `Critique` step that evaluates the proposed
reply against a small constitution (e.g. "do not invent NPCs",
"do not promise actions the executor cannot perform").

**First deliverable.** Add a `FallbackCritique` static class with
five rules. Run after the strategy emits its result; if any rule
fails, fall through to the next strategy (or the emergency tier).
Pure deterministic; no inference.

**What blocks it today.** The current strategy outputs are already
narrow enough that drift is rare. The critique pays off when more
emergent strategies (idea #9) land.

### 9. Authored-strategy promotion

**Where it fits.**
[`src/PalLLM.Domain/Runtime/PromotionLedger.cs`](../src/PalLLM.Domain/Runtime/PromotionLedger.cs)
already records "this AI-derived response shape stabilized - it
should become deterministic." Today the apply step writes a staged
template the human reviews. Future: the pipeline writes a fully
typed `Try_*` method into a staging file under `PromotionStaging/`
that compiles cleanly and only needs a human "yes, merge."

**First deliverable.**
[`src/PalLLM.Domain/Runtime/PromotionApplyPreviewBuilder.cs`](../src/PalLLM.Domain/Runtime/PromotionApplyPreviewBuilder.cs)
gains a `BuildCompilableScaffold` mode that produces actual C# that
compiles (the current output is a documented placeholder template).

**What blocks it today.** The placeholder template already proves
the ledger works. Compilable output requires the templates to know
the four CONVENTIONS patterns deeply; the next contributor pass
can pattern-match a working `Try_*` method as the template body.

**Hard-rule check.** Apply still writes only to `PromotionStaging/`,
never source. Human review required to merge. ProofPacket attached.

### 10. Voice-cloning with consent gating

**Where it fits.**
[`src/PalLLM.Domain/Inference/TtsClient.cs`](../src/PalLLM.Domain/Inference/TtsClient.cs)
forwards a `voice_id` that maps to operator-uploaded reference
samples. New `Tts:ConsentToken` config knob requires the operator
to type a literal `"I consent to my voice being mirrored"` string
before the runtime sends a clone request.

**First deliverable.** Pack-level provenance now exists:
`VoiceConsent` is required when a personality pack declares
`VoiceRefPath`. Do not ship runtime voice-clone dispatch until the
separate TTS consent-token flow + revocation path are designed.

**What blocks it today.** Privacy model. Even with explicit
consent, the audit trail (every TTS call's ProofPacket records
which voice id was used) needs to be exhaustive enough that an
operator can revoke retroactively.

## Far-term (5-10 years, post-2030)

### 11. Federated companion identity

**Where it fits.** The portable adapter surface
([`src/PalLLM.Domain/Portable/PortableAdapterContracts.cs`](../src/PalLLM.Domain/Portable/PortableAdapterContracts.cs))
already lets a companion travel between games. Federation extends
this: the companion's "soul" - accumulated memory + relationship
state - exports as a signed bundle the operator can carry to a
different machine, a different game, a different runtime.

**First deliverable.** A `pal pack export-companion <character-id>`
verb that writes the working-tier memory + relationship summary +
chosen personality-pack reference into a single signed `.companion`
file. Re-importable into a fresh PalLLM instance.

**What blocks it today.** Crypto signing infrastructure not built
yet. Trust boundaries when importing a `.companion` from an
untrusted source.

### 12. Auditable companion soul-state

**Where it fits.** Every `RelationshipTracker` mood / affinity
change already carries a ProofPacket internally. The far-term
extension: a queryable "show me when your trust in me started
shifting" timeline - a curated subset of ProofPackets attached to
a single companion-character pair.

**First deliverable.** New `/api/companion/<id>/timeline` route +
`pal_companion_timeline` MCP tool that returns the top 20 most
salient relationship-state changes from the existing
`PromotionObservation` ledger filtered to that character.

**What blocks it today.** Storage cost - the existing ledger is
in-memory bounded. Persistent ledger needs a retention policy.

### 13. Multi-agent companion mesh

**Where it fits.** Today each personality pack is a single voice.
A future mesh-mode lets multiple packs collaborate on a single
turn - the warrior asks the scholar a question, the scholar answers,
the warrior synthesizes for the player. Already architecturally
possible via `DuoOrchestratorPlanner`'s `dispatch` patterns.

**First deliverable.** A new dispatch pattern
`mesh-multi-pack` that runs N packs in parallel and uses a
deterministic merger (existing `PresentationCuePlanner`) to compose
the final reply.

**What blocks it today.** Latency budget - running N inference
calls per turn breaks the chat-latency target on consumer hardware.
Pays off when speculative + EAGLE-3 reduce per-call cost
substantially.

### 14. Self-contained on-device fine-tuning

**Where it fits.**
[`src/PalLLM.Domain/Memory/ReflectionService.cs`](../src/PalLLM.Domain/Memory/ReflectionService.cs)
already aggregates "what mattered." A future
`OnDeviceLoraTrainer` consumes the aggregate and produces a
trained LoRA the next session loads.

**First deliverable.** Document the data-export shape; ship no
training code yet. The smallest sensible first step is a
`pal companion export-training-data` verb that writes the
aggregated reflections in the canonical `instruction / input /
output` JSONL format trainers expect.

**What blocks it today.** No on-device trainer in scope. Wait for
sub-1B models that support LoRA training in <2GB VRAM.

### 15. Diffusion-based scene narration

**Where it fits.**
[`src/PalLLM.Domain/Runtime/WorldNarrationAdvisor.cs`](../src/PalLLM.Domain/Runtime/WorldNarrationAdvisor.cs)
emits text cues for the presentation planner. A future advisor
emits image-generation prompts that a local diffusion model could
render as on-the-fly portrait variants.

**First deliverable.** Ship the prompt-emission contract; let
operators wire their own diffusion endpoint via existing
`Vision:BaseUrl` (diffusion endpoints often expose
OpenAI-compatible image-generation routes).

**What blocks it today.** No native diffusion connector script
yet. The recent connectors (Ollama / vLLM / TensorRT) cover text
+ multimodal; image-out is a separate engine class.

## Hard "no"s (deliberately not pursued)

These ideas keep being suggested but violate one of the four hard
rules. Documenting them here so future contributors don't re-derive
the rejection.

- **Auto-acting companion (no operator approval).** Violates
  observer-only. ADR
  [`adr/0006`](adr/0006-opt-in-everything-by-default.md). The action
  executor stays gated by an explicit allowlist.
- **Sidecar reaching into Palworld's process.** Violates the
  filesystem-only one-way bridge. ADR
  [`adr/0003`](adr/0003-one-way-advisory-bridge.md). Communication
  remains through `Bridge/Inbox/` and `Bridge/Outbox/` directories.
- **Cloud-required inference path.** Violates local-first. ADR
  [`adr/0006`](adr/0006-opt-in-everything-by-default.md). Cloud
  endpoints are operator-configurable but never default.
- **Auto-applying promotion suggestions to source.** Violates the
  human-in-loop guarantee on the promotion pipeline. Apply writes to
  `PromotionStaging/`, never source files.
- **Persistent telemetry without explicit consent.** Privacy
  posture surface (`/api/privacy/posture`) classifies every
  data-emitting surface; nothing leaves the machine on a fresh
  install.

## How to propose a new idea

Open a PR that adds an entry to this doc with the four required
sections (where it fits, first deliverable, what blocks it today,
hard-rule check). The PR review answers two questions:

1. Does the idea slot into an existing PalLLM seam, or does it
   require a new framework? (Prefer the former.)
2. Does the idea respect the four hard rules? (If not, document why
   in **Hard "no"s** above instead.)

If both answers are yes, the idea is in scope and a future
contributor pass can pick it up. If the idea has a runnable first
deliverable, the PR can also include the implementation slice.

## Related

- [`ROADMAP.md`](ROADMAP.md) - current build queue, weighted by
  player-experience.
- [`HANDOFF.md`](HANDOFF.md) - what was last shipped, what to read
  before picking up.
- [`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md) - concrete
  recipe patterns for several of the ideas above.
- [`MEMORY_RECIPES.md`](MEMORY_RECIPES.md) - three-tier memory
  patterns from Mem0 / Letta / Zep mapped onto PalLLM.
- [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md) - vision +
  audio pipeline patterns.
- [`adr/`](adr/) - six accepted architecture decisions; load-bearing
  for any new idea.


