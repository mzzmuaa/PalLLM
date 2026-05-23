# PalLLM Fallback AI Notes

Last audited against the code: `2026-05-07`

This document explains the deterministic ideas behind PalLLM's hardcoded
fallback director and the subsystems layered on top of it. The goal is
not to mimic any one game's runtime. The goal is to turn durable
game-AI patterns into offline-safe, low-latency companion behavior.

## What the fallback layer does in the shipped code

PalLLM's fallback layer is not just an outage backup. It serves four
roles:

1. Graceful degradation when live inference is disabled or unhealthy.
2. Fast-path response generation for routine tactical asks that do not
   need an LLM.
3. Hardcoded visual and audio staging through `PresentationCuePlan`.
4. Continuity glue across memory, relationship state, world state, and
   current pacing phase.

The fallback library is a functional multiplier, not just a failure mode.

## Why this improves program function

The deterministic layer enhances PalLLM in concrete ways:

- Lower latency for routine asks such as stealth routing, regrouping,
  camp recovery, or multi-base logistics.
- Lower LLM usage because those routine asks can bypass inference
  entirely.
- Better resilience because the project still replies coherently when
  an endpoint is down or rate-limited.
- Tighter multimodal consistency because text, visual cues, and audio
  cues all come from the same local state instead of separate model
  calls.
- Cheaper operations because fallback replies do not add prompt or
  completion tokens to the runtime health counters.

## Design patterns PalLLM implements

Each pattern below is a well-known game-AI concept. PalLLM's
implementation is listed alongside so operators can map the behavior
back to code.

### Adaptive pacing

Transferable idea: threat pressure should rise and fall instead of
staying flat. Calm windows are as valuable as pressure windows.

PalLLM implementation:

- Pacing phases: `Relax`, `BuildUp`, `Peak`, `Recover`.
- Fallback strategy selection and presentation cues both key off phase.
- Replies can slow the player down, accelerate them, or explicitly call
  for recovery depending on current pressure.

### Plan plus contingency

Transferable idea: always offer a simple next plan, and always offer a
simple contingency if the lane collapses.

PalLLM implementation:

- Fallback responses are usually action plus contingency.
- Combat-oriented strategies prefer readable next steps over generic
  banter.
- Blocked lanes downgrade cleanly into regroup, reset, or cover-driven
  advice.

### Short, contextual coordination barks

Transferable idea: short, contextual lines create the illusion of
richer coordination when they line up with what the player can
actually perceive.

PalLLM implementation:

- Bark families such as triage, regroup, overwatch, and capture-window
  guidance.
- Subtitle and audio delivery modes change by strategy and phase.
- Presentation planning stays deterministic and traceable.

### Fair agents with local sensing

Transferable idea: tension works better when agents feel dangerous but
not omniscient.

PalLLM implementation:

- Exploration and stealth replies bias toward nearby, visible, or
  recently signaled threats.
- Fallback avoids making map-wide claims that local evidence does not
  support.

### Supportive buddy behavior

Transferable idea: stay on the player's side, avoid breaking stealth,
be helpful without grandstanding.

PalLLM implementation:

- `stealth-shadow`, `buddy-overwatch`.
- `hero-moment` with rarity pressure instead of constant intervention.
- Player-first support language rather than companion self-display.

### Encounter memory

Transferable idea: build on previous encounters instead of resetting
to generic behavior.

PalLLM implementation:

- Memory-aware overlays.
- Strategy choices that explicitly avoid repeating earlier mistakes.
- Lightweight rivalry framing when relevant memories exist.

### Traversal reasoning

Transferable idea: route advice should care about terrain, footing,
anchors, and recoverability.

PalLLM implementation:

- `safe-travel`.
- Anchor-to-anchor movement language.
- Route advice that values recoverability over theoretical shortest
  distance.

### Morale-aware plan downgrading

Transferable idea: morale changes what counts as a good plan.

PalLLM implementation:

- `morale-rally`, `retreat-and-rally`.
- Simpler plans when confidence is collapsing.

### Concurrent downtime behavior

Transferable idea: believable downtime combines compatible actions
instead of serializing every tiny behavior.

PalLLM implementation:

- `ambient-camp`, `crafting-discipline`.
- Light multitask framing during safe windows.

### Readable audiovisual telegraphing

Transferable idea: agents feel smarter when state changes are legible.

PalLLM implementation:

- Explicit visual cue IDs such as route breadcrumbs, scan cones,
  regroup arrows, and threat pulses.
- Explicit audio cue IDs such as hushes, rally shouts, reset breaths,
  and camp ambience.
- Shared phase truth across text, audio, and visual outputs.

### Multi-base specialization

Transferable idea: a multi-base gameplay loop needs logistics-aware
fallback behavior, not just combat or travel logic.

PalLLM implementation:

- `base-network`.
- Known bases from bridge events become logistics anchors.
- Fallback can advise specialization rather than treating every base
  as a clone.

## The shipped deterministic strategies

The current fallback engine defines `19` strategies:

- `hero-moment`
- `emergency-triage`
- `retreat-and-rally`
- `stealth-shadow`
- `nemesis-counterplay`
- `buddy-overwatch`
- `perimeter-lockdown`
- `base-network`
- `safe-travel`
- `capture-window`
- `objective-push`
- `crafting-discipline`
- `harvest-window`
- `weather-shelter`
- `exploration-sweep`
- `morale-rally`
- `recover-window`
- `ambient-camp`
- `general-director`

These are selected by `FallbackBehaviorEngine.Generate(...)` from a
context that combines:

- Player message intent.
- Task kind.
- Current pacing phase.
- World snapshot.
- Known bases.
- Memory matches.
- Recent fallback history.
- Relationship state.

## Visual and audio fallback planning

The deterministic layer does not stop at text. Every chat reply also
gets a `PresentationCuePlan` with:

- Audio behavior ID, delivery mode, subtitle style, music mode, mix
  profile.
- Visual behavior ID, portrait expression, pose, HUD accent, world
  marker, screen treatment, light cue.

This lets the in-game renderer stay expressive without asking an LLM
to invent staging the runtime already knows.

## How the runtime chooses fallback instead of LLM

The deterministic layer is used when:

- Inference is disabled.
- Inference fails and fallback-on-failure is enabled.
- The circuit breaker is open.
- The per-character rate limit is exceeded.
- Policy bypass says the ask is routine enough to answer locally.

Policy bypass currently prefers fallback for reactive barks, routine
tactical requests, and recovery or camp tasks. That is the mechanism
that reduces LLM usage while preserving response quality.

## Related memory behavior

- **Memory importance scoring**: every remembered entry carries a
  deterministic salience score derived from content, role, and tags.
  Recall blends semantic similarity, recency, importance, and
  character affinity.
- **Reflection consolidation**: when accumulated importance in the
  recent window crosses a threshold, the top salient entries are
  consolidated into a single high-importance reflection memory. Runs
  without calling a model.
- **Relationship affinity**: a light sentiment heuristic on the
  player's message updates a per-character affinity, mood, and tone
  record that feeds back into the system prompt.

All three are deterministic, reproducible, and require no external
model call.

## Design constraints

The fallback library follows these rules:

- Local-first and deterministic by default.
- Tactically clear before theatrically clever.
- Flavorful without pretending to know more than the runtime actually
  knows.
- Supportive moments should feel intentional, not spammy.
- Fast paths should remove wasteful LLM calls, not remove meaningful
  nuance.
- Text, visual, and audio outputs should share the same local context
  truth.
