# PalLLM Companion Intelligence Ideas

Last audited: `2026-05-21`

This doc records the best "pseudo AGI" ideas that fit **PalLLM as a
Palworld companion runtime**, based on a thorough sibling-project prompt-pack
audit under `D:\Coding\Byte\docs\prompts`.

It is intentionally **not** the ship-critical queue. The current
`76.2% -> 100%` path remains the live Palworld proof chain, native HUD
delivery, native speech, guarded native actions, and clean-machine release
validation. See [`ROADMAP.md`](ROADMAP.md) and
[`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md).

## What "pseudo AGI" should mean here

For PalLLM, "more intelligent" should not mean "more autonomous at any cost."
The fit for this repo is:

- better world modeling of the current Palworld session
- better visible memory and continuity across sessions
- stronger self-critique before risky suggestions or actions
- clearer confidence, rationale, and replay trails
- lower-latency scheduling that uses local hardware well

The fit is **not**:

- blind gameplay autopilot
- browser agents, desktop-computer use, or generic coding-agent features
- webcam, microphone, or biometric collection by default
- cloud-first orchestration
- hidden long-term memory the player cannot inspect

## Audit summary

The Byte prompt library contained `837` markdown files plus `14` zip
archives at the most recent re-count (`2026-05-21`). The archives are
zip mirrors of the extracted prompt-pack directories, not hidden extra
packs. The highest-signal material for PalLLM came from these
families (a sixth, `byte-qwen-pack-2026-04-25`, has since landed but
was not part of the original signal pull and is left out for that
reason):

- `byte-forge-2026-04-24`: orchestration, critique, memory, world-model planning
- `byte-forward-2026-04-24`: reflection, constitution, replay, batching, quant adaptation
- `byte-synthesis-2026-04-24`: visible memory and explanatory surfaces
- `byte-qwen-frontier-2026-04-27`: hierarchical memory, debate, escalation, anomaly detection, curiosity
- `byte-qwen-modernize-2026-04-26`: planner/executor and critique-loop modernization
- `_archive/byte-council-2026-04-24`: older multi-phase review loop, now mostly superseded by the newer forge/qwen packs

The most reusable prompt IDs were:

- `G20`, `G21`, `G24`, `G50`
- `F01`, `F04`, `F31`, `F32`, `F35`, `F36`, `F42`
- `S30`
- `T01`, `T02`, `T04`, `T05`, `T09`
- `U05`, `U06`, `U07`
- `P05`, `P29`
- `O07`, `O08`

## Best-fit ideas

### 1. Visible memory graph

**Why it fits**

PalLLM already has deterministic memory, relationship tracking, reflection,
and session persistence. What it lacks is a player-visible answer to "what do
you remember and why?"

**Byte source patterns**

- `G24` agent success/failure memory
- `T01` hierarchical memory
- `P05` memory compaction
- `S30` visible memory graph
- `P29` graph + live world linkage

**PalLLM-native shape**

- Build a read-only `MemoryGraphBuilder` over `ConversationMemoryStore`,
  `RelationshipTracker`, and recent world events.
- Expose recent memories, reflection entries, relationship ties, and event
  anchors through one inspection surface.
- Keep write/mutation separate until the player-edit model is designed.

**Best timing**

Post-`100%`, or earlier only if kept read-only and strictly diagnostic.

### 2. Decision replay plus rationale trace

**Why it fits**

PalLLM already has `ProofPacketBuilder`, `WhyEngine`, bridge proof, release
readiness, and rich health surfaces. A replay/rationale layer would make the
system feel much smarter without granting more autonomy.

**Byte source patterns**

- `F32` decision replay log
- `U07` one-line rationale per action
- `F42` decision graph visualizer

**PalLLM-native shape**

- Emit a bounded per-turn decision envelope for chat, fallback, vision,
  action-intent planning, and guarded action feedback.
- Add a one-line rationale field for "why this reply path / cue / action
  suggestion happened."
- Export a Mermaid-or-JSON trace for a single `RequestId`.

**Why this is high leverage**

This is the best AGI-lite feature that also helps the current native-proof and
publishability work.

### 3. Confidence-calibrated escalation for risky actions

**Why it fits**

PalLLM already has `DisagreementDetector`, `DuoOrchestratorPlanner`,
allowlisted actions, dry-run posture, and explicit proof packets. It does not
yet have a first-class "I'm not confident enough to do this without asking"
layer.

**Byte source patterns**

- `T09` confidence-calibrated escalation
- `T02` multi-agent debate for high-stakes decisions
- `T04` planner + verifier before execution
- `F31` constitutional critique

**PalLLM-native shape**

- For `request_craft_queue`, `recall_pals`, or future native actions, add a
  confidence gate that can stop at:
  - explain-only
  - ask-for-confirmation
  - dry-run preview
  - allowlisted execution
- Use `DisagreementDetector` and validator failures as escalation signals,
  not just prose confidence.

**Best timing**

This one can support the current roadmap directly because it strengthens the
native-action safety story.

### 4. Advisory world-model planner for base, travel, defense, and logistics

**Why it fits**

PalLLM already tracks `GameWorldSnapshot`, recent events, base discovery,
travel, production, world narration, and action intents. That is enough to
support a bounded world model.

**Byte source patterns**

- `G50` world-model planning
- `P29` graph/world linkage

**PalLLM-native shape**

- Keep the planner **advisory only** at first.
- Simulate candidate plans for:
  - base defense response
  - route selection
  - recovery from raids or wipe states
  - production prioritization
  - pal recall suggestions
- Return a ranked plan with rationale and reversible next steps instead of
  mutating gameplay directly.

**Best timing**

After the live bridge and native HUD seams are proven, because a better world
model is only as good as the underlying live world truth.

### 5. Dream-cycle and memory consolidation during idle time

**Why it fits**

PalLLM already has deterministic reflection, importance scoring, and session
persistence. It can compact and narrativize memory locally without adding
hidden state or new network requirements.

**Byte source patterns**

- `F01` dream-cycle memory consolidation
- `F04` self-critique reflection
- `T05` episode replay for skill transfer

**PalLLM-native shape**

- During idle periods, compact salient session moments into a small "companion
  journal" or reflection layer.
- Keep it opt-in and visible.
- Use it to reduce context load, not to invent hidden lore.

**Best timing**

After a visible memory surface exists, so players can inspect what was
consolidated.

### 6. Long-run anomaly detection and curiosity queue

**Why it fits**

PalLLM already has metrics, self-healing, proof surfaces, and a clear
publication-safety posture. It can detect odd behavior and queue review items
without becoming an unsupervised autopilot.

**Byte source patterns**

- `U05` anomaly detection on long-run trajectories
- `U06` curiosity-driven exploration in idle hours

**PalLLM-native shape**

- Detect outlier loops:
  - latency spikes
  - reply-delivery failures
  - repeated fallback churn
  - bridge event storms
  - action-feedback mismatches
- Emit bounded review suggestions such as:
  - "travel events are noisy"
  - "production sampler drifted"
  - "this narrative pack is being ignored"

**Best timing**

Can land incrementally before `100%` if kept observational only.

### 7. Low-latency batching and quant-aware routing

**Why it fits**

PalLLM already has model-tier orchestration, hardware profiling, warmup, and
recent-window inference performance tracking. The next step is making small
local tasks cohere under load.

**Byte source patterns**

- `F35` batched inference scheduler
- `F36` adaptive quantization manager

**PalLLM-native shape**

- Batch tiny reflex-tier calls where the active backend supports it.
- Keep big deliberate lanes out of batching by default.
- Route quant preference by task class, risk, and hardware pressure, while
  keeping the current operator-facing model-control story intact.

**Best timing**

This is the best performance-oriented AGI-lite idea for the current codebase.

## Ideas that do not fit PalLLM

These appeared in the Byte packs but do not belong on PalLLM's mainline:

- browser-use / computer-use agents
- sandboxed code execution
- webcam mood reading
- voice cloning as a core requirement
- social/viral content generators
- federated mesh or multiplayer social layers
- broad "autopilot port" gameplay automation

PalLLM should stay a **Palworld companion runtime**, not drift into a generic
desktop agent.

## Recommended order after the ship-critical roadmap

1. Decision replay plus rationale trace
2. Confidence-calibrated escalation for risky actions
3. Advisory world-model planner
4. Visible memory graph
5. Dream-cycle and memory compaction
6. Anomaly detection and curiosity queue
7. Batched inference and quant-aware routing

## Best near-term leverage without derailing the roadmap

If only one of these ideas is pulled forward before `100%`, it should be:

- **decision replay plus rationale trace**

If two are pulled forward, add:

- **confidence-calibrated escalation for risky native actions**

Those two improve trust, safety, and debugging for the exact parts of PalLLM
that are still on the critical path.
