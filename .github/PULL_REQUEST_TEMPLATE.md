<!-- Thanks for the PR. The checklist below mirrors the CI gates so a
green build is predictable. Delete any sections that don't apply. -->

## Summary

<!-- 1-3 sentences on what and why. The "why" is the useful part — the
diff already shows the what. -->

## Related issue

<!-- Closes #123, or "n/a" if this is drive-by. -->

## Test plan

<!-- - [ ] `dotnet test` green (CI will confirm)
     - [ ] Added or updated NUnit tests for the new behaviour
     - [ ] Ran `scripts/doctor.ps1 -RunSmoke -RunDeliveryReplay` when
           touching install / health / outbox paths -->

## CI drift checklist

Tick every row that applies to this change:

- [ ] **No new `/api` routes** — *or* bumped the route count in
      `README.md` and `docs/ROADMAP.md`.
- [ ] **No new `FeatureDescriptor`** — *or* bumped the feature count
      in `README.md`, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, and
      `docs/API.md`.
- [ ] **No new fallback `Try*` strategy** — *or* bumped the strategy
      count in `docs/ROADMAP.md`.
- [ ] **No new test** — *or* bumped the test count in `README.md` and
      `docs/ROADMAP.md`.
- [ ] **No new markdown links** — *or* confirmed every new link target
      exists (CI runs the same check).

(Any `[x]` above that CI later disagrees with will fail the build with
a clear "ROADMAP says N, code has M" message; nothing you have to do
manually.)

## Documentation

- [ ] Updated `CHANGELOG.md` under the `[Unreleased]` block.
- [ ] Updated any affected doc in `docs/` (README.md, QUICKSTART.md,
      OPERATIONS.md, ARCHITECTURE.md, API.md, ROADMAP.md,
      IMPLEMENTATION_QUEUE.md, FALLBACK_AI_RESEARCH.md, CORE_LIBRARY.md,
      PACK_AUTHORING.md, INDEX.md).
- [ ] Updated `CONTRIBUTING.md` if the contribution loop changed.

## Backwards compatibility

- [ ] This change does **not** break the `OutboxEnvelope`,
      `ChatResponse`, `PresentationCuePlan`, or `BridgeEventEnvelope`
      wire shape.
- [ ] If it does, I've called that out above AND updated
      `main.lua` (both producer and consumer sides) so in-game
      consumption doesn't regress silently.

## Kill-switch / config surface

- [ ] This change introduces no new opt-in feature, *or* the new
      feature ships with an explicit default-off kill switch AND is
      documented in `docs/OPERATIONS.md` § "Opt-in feature matrix".
