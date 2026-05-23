---
name: Feature request
about: A proposal for new behaviour or a new opt-in subsystem.
title: "[Feature] "
labels: ["enhancement", "triage"]
assignees: []
---

<!-- PalLLM aims to stay local-first, deterministic-fallback-first, and
narrow in scope. Please read CONTRIBUTING.md § "Design constraints" before
filing so the proposal lands with matching assumptions. -->

## Problem you're trying to solve

<!-- What companion / bridge / runtime behaviour do you wish PalLLM had?
Focus on the gap, not the implementation. -->

## Proposed solution

<!-- Rough sketch is fine. If this is a new opt-in feature, note the
default posture (on / off) and the kill switch name. -->

## Alternatives you've considered

## Does this fit PalLLM's scope?

Check each that applies to the proposal:

- [ ] Keeps the runtime local-first (no hard cloud dependency).
- [ ] Preserves the "deterministic fallback is always available"
      invariant — a working reply exists even when every external
      dependency is unavailable.
- [ ] Has an explicit kill switch that defaults to off (for anything
      that makes network calls, mutates game state, or adds new IO).
- [ ] Does not require a blind port of any external game-automation
      module that would extend PalLLM beyond the companion / bridge /
      runtime surface.

## Test plan

<!-- How would you validate the feature? Prefer deterministic fixtures
over live-session requirements. -->

## Additional context
