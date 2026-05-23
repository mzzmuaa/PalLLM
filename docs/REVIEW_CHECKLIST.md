# Review checklist - what to verify on every change

Last audited: `2026-05-22`

A focused checklist for reviewing a PR (yourself, a teammate, or
an agent's work). Most of the items are also enforced by the
16 drift gates (`pal audit`); having the list separately means a
reviewer can skim it in 60 seconds and notice the items the
gates can't enforce mechanically.

## Quick green-light path

If all four of these are true, the change is structurally sound
and the gates have done most of the review work for you:

- [ ] `pal audit` passes (16 / 16 gates green) on the branch
- [ ] `pal test` passes (no regressed test, count bumped if
      a test was added)
- [ ] The change updates docs in lockstep - README / ROADMAP /
      ARCHITECTURE / HANDOFF / CODE_MAP if any count moved
- [ ] No `appsettings.json` / `Auth:ApiKey` / secrets-shaped
      values committed

The rest of this doc is for the work the gates *can't* do.

## Conceptual review

### Does the change uphold the load-bearing invariants?

See [`INVARIANTS.md`](INVARIANTS.md) for the full list.

- [ ] If the change touches the chat path, every chat turn
      still produces a working `ChatResponse` (no new throw
      sites bubbling up to the HTTP layer).
- [ ] If the change adds a feature that emits network
      traffic, it defaults to **off**. The
      `Drift_Public_copy` gate catches some violations; the
      reviewer catches the rest.
- [ ] If the change touches the bridge, it doesn't introduce
      a sidecar ? game call path. Filesystem-only,
      one-way at any moment.
- [ ] If the change adds a cache, the cache uses
      signature-based invalidation (not time-only).

### Does the change avoid the documented anti-patterns?

See [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md).

- [ ] No mass-reformat of `PalLlmRuntime.cs` /
      `PresentationCuePlanner.cs` / `Program.cs`. Touch only
      what's changing.
- [ ] No new chain-of-exceptions on the chat path.
- [ ] No HTTP 5xx for upstream model faults - surface in
      `ResponsePath` and return 200.
- [ ] No new sub-agent delegation when a Grep would do.
- [ ] No promotion-apply that mutates source code in-place.
      Apply writes to `Runtime/PromotionStaging/` only.

### Does the change fit the existing patterns?

See [`CONVENTIONS.md`](CONVENTIONS.md) (the four code patterns)
+ [`EXTENSION_POINTS.md`](EXTENSION_POINTS.md) (the file
location map).

- [ ] New code fits one of the four patterns: advisor,
      builder, validator, feeder.
- [ ] If the new code is read-heavy, the TTL-cache pattern
      from [`adr/0005`](adr/0005-ttl-cache-for-posture-surfaces.md)
      is followed (with `*Cached` companion + signature
      invalidation + the original `.Capture()` /
      `.Verify()` API preserved).
- [ ] If the new code defines new wire shapes, they live in
      `src/PalLLM.Domain/Integration/Contracts.cs`.

## Documentation review

### Did the right docs get bumped?

The drift gates catch counts; the reviewer catches everything
else.

- [ ] If a new HTTP endpoint landed:
      [`COOKBOOK.md -1`](COOKBOOK.md#1-new-http-endpoint)
      and [`API.md`](API.md) updated, OpenAPI snapshot
      regenerated.
- [ ] If a new feature landed: `FeatureDescriptor` entry in
      `PalLlmFeatureCatalog.cs`.
- [ ] If the change is observably new functionality, the
      relevant audience-facing doc is updated (PITCH for
      laypeople, OPERATIONS for operators, COOKBOOK for
      contributors, RUNBOOK if it has a new failure mode).
- [ ] If the change is load-bearing enough to warrant an
      ADR, an ADR was added under `docs/adr/` with a
      `Depends on:` / `Supports:` cross-link.
- [ ] `docs/HANDOFF.md` "What just landed" updated with a
      bullet for the change.
- [ ] `CHANGELOG.md` `[Unreleased]` updated with the entry
      (and count deltas if any moved).
- [ ] `docs/PROJECT_NUMBERS.json` updated if any count moved.

### Are the new docs neutral?

The `Drift_Public_copy` gate catches release-facing files;
new internal docs aren't always covered.

- [ ] No third-party brand pinning in release-facing copy.
      Use "any MCP-capable client", "any OTLP collector",
      etc.
- [ ] No "we" / "us" / "the team" - PalLLM is a
      single-maintainer repo today; speak in the imperative
      or the second person.
- [ ] No "100% / lawyer-proof / IP-neutral" claims in the
      public copy. The portable seam is neutral but the
      shipped mod is Palworld-specific.

## Test review

See [`TESTING.md`](TESTING.md).

- [ ] Every new behaviour has a focused test.
- [ ] Tests that need a live external dependency (model,
      Palworld) - there are none in the regular suite. New
      tests use the in-process fakes.
- [ ] No `Thread.Sleep`, no literal
      `DateTimeOffset.UtcNow` in assertions, no unseeded
      `Random`.
- [ ] Tests that exercise a TTL-cached surface
      `InvalidateCache()` in `[SetUp]`.

## Security review

See [`SECURITY.md`](../SECURITY.md).

- [ ] No new endpoint that bypasses `Auth:ApiKey` when
      configured.
- [ ] No new path that escapes the runtime root
      (`runtime-root/Bridge/`, `runtime-root/Packs/`, etc.).
- [ ] No new outbound HTTP call site without a configured
      timeout + cancellation token.
- [ ] No new hard-coded URL pointing off-machine. Every
      external endpoint is configurable via
      `PalLlmOptions`.

## Performance review

See [`HOT_PATH.md`](HOT_PATH.md).

- [ ] If the change adds work to a hot-path method
      (`ChatAsync`, `GetWorldSnapshot`, `GetHealth`,
      `*PostureBuilder.CaptureCached`), the budget still
      holds (cold and warm).
- [ ] If the change adds work to a poll loop
      (`BridgeInboxWorker`, `SelfHealingWorker`), the
      per-tick budget still holds.
- [ ] If the change adds an HTTP call, it has explicit
      timeout + cancellation.

## Bridge contract review

If the change touches the runtime ? Lua boundary:

- [ ] The new event type's payload is added to
      [`schemas/bridge-event-envelope.schema.json`](schemas/bridge-event-envelope.schema.json)
      under `$defs`.
- [ ] The C# `BridgeEventEnvelope` handler in
      `PalLlmRuntime.ProcessBridgeEvent` and the Lua
      producer in `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`
      are updated in lockstep.
- [ ] A new test under `tests/PalLLM.Tests/` covers the
      drain path with a fixture envelope.

## Author self-review (before the PR)

The shortcut: run all of these locally.

```powershell
pwsh ./pal.ps1 build           # zero warnings expected
pwsh ./pal.ps1 test            # 1154+ tests green
pwsh ./pal.ps1 audit           # 16 / 16 gates green
pwsh ./pal.ps1 status          # confirm rolling baseline reflects the change
```

If all four are green, the structural review is mostly done
and the conceptual review (this doc's higher sections) is
what's left.

## Related

- [`CONTRIBUTING.md`](../CONTRIBUTING.md) - the human-facing
  contributor guide (this doc focuses on review specifically)
- [`.github/PULL_REQUEST_TEMPLATE.md`](../.github/PULL_REQUEST_TEMPLATE.md)
  - the PR-template form that mirrors the gates
- [`INVARIANTS.md`](INVARIANTS.md) - what's guaranteed
- [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) - what's rejected


