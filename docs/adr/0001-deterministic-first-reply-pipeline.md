# ADR 0001 — Deterministic-first reply pipeline

- **Status:** Accepted
- **Date:** 2026-04 (foundational, dated retroactively at the audit pass that wrote ADRs)
- **Tags:** runtime, fallback, reliability
- **Depends on:** none (this is the foundational decision)
- **Supports:** [`0006`](0006-opt-in-everything-by-default.md) (everything is opt-in *because* fallback always works)

## Context

A companion that *hangs when its model is offline* is broken. Players
talk to PalLLM the same way they talk to any other game character:
they expect a reply within human-conversational time, every time. If
the local Ollama instance is paused, the GPU is throttled, the model
file is corrupt, or the user is on a 7-year-old laptop with no
inference at all, the companion still has to respond.

Most LLM-companion stacks treat this as an error path: timeout the
inference call, surface a stack trace, log "model unavailable",
return HTTP 503. That works for an internal tool. It does not work
for a game character whose job description is *be present and
responsive*.

## Decision

Every chat turn produces a working reply through a deterministic
director (`FallbackBehaviorEngine` + `PresentationCuePlanner`) that
needs no external dependency. Live inference, when enabled, runs on
top of the deterministic baseline as an enrichment — never a
prerequisite.

Concretely:

1. `PalLlmRuntime.ChatAsync` always reaches a `ChatResponse`. If the
   inference path fails (offline / circuit-broken / rate-limited /
   thermal-gated), the runtime returns the deterministic reply
   tagged with a diagnostic `ResponsePath` instead of throwing.
2. The deterministic director has 19 strategies that pattern-match
   the player utterance and produce a multi-sentence reply. Even if
   the personality pack is broken or the strategies all return null,
   a third-tier `EmergencyFallback` hands back a canned
   acknowledgement.
3. Every chat turn also gets a `PresentationCuePlan` (audio + visual
   cues) so a UE4SS consumer can render the reply without a separate
   "is the model up?" check.

## Alternatives considered

- **Fail loudly.** Industry-standard for backend services, wrong for a
  player-facing companion. Players would see "PalLLM is down" mid-game
  through no fault of their own.
- **Cache the last reply.** Brittle, surprising, and useless on a fresh
  install where there's nothing cached.
- **Inference-only with a "model unavailable" canned line.** Two
  problems: (1) one canned line gets old fast; (2) the canned line
  has to come from somewhere, and once you're picking one it's a
  short hop to having a small library — at which point you have a
  deterministic director.

## Consequences

**Positive:**
- The runtime is provably online. Tests don't need a live model;
  CI doesn't need a GPU; an air-gapped install works the same as a
  connected one.
- Every operator-facing surface (Field Console, /api/health,
  OperatorHealthScorer) can describe the live state honestly because
  fallback is always available.
- The honest roadmap math (`76.2%` player-experience-weighted) treats
  fallback as a working baseline, not an outage.

**Negative:**
- The deterministic director is its own surface area to maintain. 19
  strategies + a presentation planner + an emergency tier — that's
  ~2200 lines of code that must keep working. The drift gate
  `Drift_Fallback_strategy_count` plus
  `tests/PalLLM.Tests/RuntimeTests.cs` and
  `tests/PalLLM.Tests/EmergencyFallbackTests.cs` keep it honest.
- New fallback strategies must be paired with a presentation cue so
  the visual layer renders something coherent — extra discipline
  beyond just writing the reply text. `CONTRIBUTING.md` § "New
  fallback strategy?" enforces this.

## Harvest hint

Lift `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs` and
`src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs` together.
They're paired — strategies and their cues — and have no external
dependencies beyond the `PalLlmOptions` config surface. See
[`HARVEST.md`](../HARVEST.md) for the full extraction recipe.

## Related

- Code: `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs`,
  `src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs`,
  `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` (`ChatAsync`)
- Tests: `tests/PalLLM.Tests/RuntimeTests.cs`,
  `tests/PalLLM.Tests/EmergencyFallbackTests.cs`
- Docs: [`DESIGN_PRINCIPLES.md`](../DESIGN_PRINCIPLES.md) § 1
  (deterministic-first), [`PITCH.md`](../PITCH.md) "Why this
  matters"
