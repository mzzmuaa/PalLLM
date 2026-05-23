# PalLLM Glossary

Last audited: `2026-05-22`

The vocabulary. Every PalLLM-specific term used in the codebase and
docs, defined in plain English. If you don't recognise a word while
reading the repo, look here first.

Sorted alphabetically for scannability. Related terms cross-link.

## A

**advisor** - A [pure function](#pure-function) that returns a
structured recommendation from a snapshot input. Examples:
`WorldNarrationAdvisor`, `MoodWeatherAdvisor`,
`GracefulDegradationAdvisor`. See [CONVENTIONS.md Section 1](CONVENTIONS.md)
and the full list in [ADVISORS.md](ADVISORS.md).

**airgap** - Shorthand for "this machine is not making outbound
network calls." `GET /api/airgap/verify` classifies every configured
outbound surface as `loopback` / `private-lan` / `public-internet`
/ `disabled`. A *strict-airgapped* verdict means every surface is
loopback-only or disabled.

**allowlist** - The set of action ids the automation executor is
permitted to emit. Lives at `PalLLM:Automation:AllowedActions`.
Empty allowlist = companion never emits actions, regardless of what
chat replies suggest.

## B

**bridge** - The Lua-side capture + delivery layer running inside
Palworld via UE4SS. Writes events to `Bridge/Inbox/`, reads replies
from `Bridge/Outbox/`. One-way + advisory - sidecar never reaches
into Palworld directly.

**bridge proof** - Machine-readable evidence that the bridge loop is
alive and closed: request arrives, outbox writes, visible delivery,
action feedback. Surfaced at `GET /api/bridge/proof`.

**builder** - Static type with a `Build(...)` or `Capture(...)`
method that composes inputs into an immutable snapshot record.
Sibling to [advisor](#advisor); signals "assembles a structure"
rather than "gives advice." Examples: `PromotionApplyPreviewBuilder`,
`PrivacyPostureBuilder`, `ProofPacketBuilder`.

## C

**cache** (TTL-cache) - Memoised variant of a pure function that
recomputes at most once every configured TTL, with signature-based
invalidation so a relevant input change bypasses the cache even
within the TTL window. PalLLM applies this to `HardwareProfiler`,
`PrivacyPostureBuilder`, `ResourceBudgetPostureBuilder`, and
`AirGapVerifier`. Documented in
[DESIGN_PRINCIPLES.md Section 8](DESIGN_PRINCIPLES.md).

**chat turn** - One round of (user message -> companion reply). The
unit of work `POST /api/chat` and `POST /api/chat/stream` produce.

**circuit breaker** - Inference-reliability guard. Trips after N
consecutive upstream failures, short-circuits to deterministic
fallback for a configurable cooldown, then half-opens to probe.
Lives in `InferenceCircuitBreaker`.

**cooperation pattern** - The specific role-chain shape the Duo
orchestrator picks for a given task kind. Ten patterns total -
`ScoutThenJudge`, `ArchitectThenImplementerThenAuditor`,
`FanOutThenSynthesis`, `ParallelDisagreement`, `BranchTournament`,
`SequentialSwap`, `WorkerLiveJudgeBackground`, `DraftThenFinalize`,
`DuoWatchdog`, `DenseAppealCourt` - plus `SingleRoleFallback` and
`DeterministicOnly` degradations.

## D

**deterministic director** - The zero-inference reply path. Picks
from 19 hand-authored `Try*` strategies in `FallbackBehaviorEngine`.
Always answers. Zero network. Zero GPU.

**director pattern** - PalLLM-specific name for the deterministic
fallback design: a central dispatcher (`FallbackBehaviorEngine`)
with many small single-purpose strategy methods that each emit a
structured reply.

**drift gate** - One of the 16 audit checks in
`scripts/run_full_audit.ps1`. Each gate pins a specific code<->doc
invariant (route count, feature count, test count, OpenAPI snapshot,
markdown link resolvability, public-copy brand scan, doc freshness,
etc.). Runs in under 10 seconds; catches 90% of doc-code mismatches.

**Duo** - The two-model cooperation architecture: a fast Worker
(e.g. Qwen 35B-A3B class) plus a dense Judge (e.g. Qwen 27B class)
working together per the chosen [cooperation pattern](#cooperation-pattern).
See [MODEL_COLLABORATION.md](MODEL_COLLABORATION.md).

## E

**Edge role** - Smallest, fastest model in the mesh. Used for
real-time chat when latency matters more than depth. One of the five
[roles](#role).

## F

**fallback** - See [deterministic director](#deterministic-director).
Also: the 5-tier defence-in-depth structure (live inference -> circuit
breaker -> thermal gate -> rate limiter -> deterministic director ->
emergency tier -> self-healing watchdog -> `recover.bat`).

**feature catalog** - Canonical list of every user-visible capability
at `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`. 121 entries
(as of Pass 44). Each entry: `{Id, Source, Status, Summary, Notes}`.
Surfaced at `GET /api/features`.

**feeder** - Background `IHostedService` that observes metrics and
writes bounded records. Never mutates the thing it observes.
Examples: `PromotionLedgerFeeder`, `SelfHealingWorker`.

**Field Console** - The web dashboard at `http://localhost:5088/`.
Static HTML + vanilla JS under `src/PalLLM.Sidecar/wwwroot/`.

## G

**guarded action** - Action the companion *wants* to emit (e.g.
`waypoint_suggest`, `recall_pals`) that only reaches the game if its
id is in the [allowlist](#allowlist) and `PalLLM:Automation:Enabled`
is true.

## H

**hardware tier** - Classification of the host box: `Constrained` /
`Standard` / `Generous`. Auto-detected by `HardwareProfiler` from
CPU core count, RAM, GPU-likelihood markers. Drives the
[cooperation pattern](#cooperation-pattern) planner's
recommendations.

**harvest** - Lift one capability out of PalLLM into another
project. The Domain layer is designed for this. Recipes in
[HARVEST.md](HARVEST.md). Candidates catalogued in
[ADVISORS.md](ADVISORS.md).

## J

**Judge role** - Dense, slower model in the [Duo](#duo) mesh. Used
for audit, synthesis, and disagreement resolution. One of the five
[roles](#role).

## L

**ledger** (promotion) - Rolling observation window tracking which
automated decisions are stable enough to promote to hard-coded
deterministic logic. When a task's observation count passes the
stability gate, `PromotionSuggestionBuilder` emits a concrete
change recipe. See [`OBSERVABILITY.md`](OBSERVABILITY.md) for how the
ledger surfaces appear in the operator-facing telemetry.

## M

**MCP** - [Model Context Protocol](https://modelcontextprotocol.io).
Open standard from Anthropic for letting desktop AI clients and IDE
extensions talk to local/remote tool servers. PalLLM exposes a
complete MCP server at `/mcp` with 38 tools, 6 resources, 1 template,
4 prompts.

**Media role** - Model in the mesh specialised for vision + TTS.
One of the five [roles](#role). May be the same binding as the
Edge or Worker role if only one model is configured.

**mesh** - The role graph: Edge + Worker + Judge + Media +
Validator. Not all slots need to be filled - advisories tell you
which cooperation patterns are unlocked by the current bindings.

## O

**outbox** - Directory (`Bridge/Outbox/`) where the sidecar writes
reply envelopes for the Lua consumer. Bounded by
`PalLLM:Bridge:OutboxMaxFiles`.

## P

**party chat** - Fan-out across multiple character ids in one
request. `POST /api/chat/party` with an array of CharacterIds;
each per-character turn runs through the full ChatAsync pipeline.
Threaded mode seeds later turns with summaries of earlier replies.

**personality pack** - v1 format: a directory with `pack.json` +
prompt + optional audio/portrait + content-hash integrity check.
Validated by `PersonalityPackValidator`, which bounds the manifest,
keeps tracked files inside the pack root, and recomputes the hash
from a streaming local read.

**posture** - A snapshot-at-capture-time description of some
dimension of runtime state. Four ship today: hardware posture,
privacy posture, resource-budget posture, airgap posture. All four
follow the [cached-builder](#cache) pattern.

**proof packet** - Machine-readable provenance record with a stable
SHA-256 id. Every automated decision emits one. Contains: subsystem,
decision, primary reason, evidence, rollback path, confidence,
human-review flag. Built by `ProofPacketBuilder`.

**pure function** - Static method with no side effects, no mutable
state, identical inputs always produce identical output. PalLLM's
[advisors](#advisor), [builders](#builder), and
[validators](#validator) are all pure. See
[DESIGN_PRINCIPLES.md Section 1](DESIGN_PRINCIPLES.md).

## R

**role** - One of Edge / Worker / Judge / Media / Validator. See
[MODEL_COLLABORATION.md](MODEL_COLLABORATION.md) for the full mesh
architecture. Role-to-model bindings live in `PalLLM:ModelRoles[]`.

## S

**self-healing watchdog** - Background worker that archives orphan
events, detects stuck envelopes, and surfaces low-health observations
without restarting the sidecar or mutating state. Evidence at
`GET /api/self-healing/status`.

**sidecar** - The ASP.NET Core host binary
(`PalLLM.Sidecar.exe`). Self-contained .NET 10 single-file publish.
Everything server-side lives here.

## T

**thermal gate** - GPU-throttle short-circuit. When a bounded
`nvidia-smi` probe (or the test env override) reports a hot GPU, the
gate steers chat turns to the deterministic fallback path so the
user's thermal budget isn't burned on a slow live-inference reply.

**tracker** - Stateful per-key counter / histogram. Examples:
`InferencePerformanceTracker` (latency per-lane),
`RelationshipTracker` (affinity per-character),
`ChatRateLimiter` (per-character sliding window).

## V

**validator** - Static type that checks inputs and returns a
structured verdict. Never throws. Examples: `PersonalityPackValidator`,
`NarrativePackValidator`. See [CONVENTIONS.md Section 3](CONVENTIONS.md).

**Validator role** - Model in the mesh specialised for policy +
format checking. One of the five [roles](#role). Distinct from the
[validator](#validator) code pattern - naming collision we tolerate
because both are load-bearing.

## W

**Why Engine** - Deterministic advisor that answers natural-language
causal questions about runtime behaviour ("why did the companion
fall back to the deterministic director?"). No inference call. Lives
in `WhyEngine.cs`, surfaced at `POST /api/why`.

**Worker role** - Fast, general-purpose model in the [Duo](#duo)
mesh. Handles most chat turns when both [Worker](#worker-role) and
[Judge](#judge-role) are bound. One of the five [roles](#role).

---

**See also**:
[AGENTS.md](../AGENTS.md) - [ARCHITECTURE.md](ARCHITECTURE.md) -
[CODE_MAP.md](CODE_MAP.md) - [CONVENTIONS.md](CONVENTIONS.md) -
[DESIGN_PRINCIPLES.md](DESIGN_PRINCIPLES.md) -
[FAQ.md](FAQ.md) - [PITCH.md](PITCH.md).



