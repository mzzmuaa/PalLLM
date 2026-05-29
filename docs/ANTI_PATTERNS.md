# Anti-patterns — things PalLLM deliberately doesn't do

Last audited: `2026-05-24`

A coding agent (or new contributor) often arrives with reasonable
ideas that have already been tried, considered, or rejected for a
specific reason. This file is the short version of "before you
propose X, check whether the maintainers already thought about it
and chose not to."

If you find yourself wanting to do something on this list, that's
fine — but you owe the change a paragraph in [`adr/`](adr/) or in
your PR description explaining why the trade-off has shifted.

## Runtime / chat path

- **DON'T crash chat on inference failure.** Every chat turn must
  return a working reply even if the inference endpoint is down,
  rate-limited, thermally gated, or the model file is missing. The
  deterministic fallback director is the load-bearing guarantee.
  See [`adr/0001-deterministic-first-reply-pipeline.md`](adr/0001-deterministic-first-reply-pipeline.md).
- **DON'T return HTTP 5xx for upstream model faults on `/api/chat`.**
  Surface the failure in `ChatResponse.ResponsePath` (e.g.
  `"fallback-after-inference-circuit-breaker-trip"`) and serve the
  deterministic reply with HTTP 200. The HTTP status reflects
  *whether the runtime worked*, not whether the optional model
  succeeded.
- **DON'T add a chain of exceptions through the runtime.** Methods
  return structured success/failure result types (`ChatResponse`,
  `OutboxEnvelope`, `BridgeProcessResult`). Exceptions are reserved
  for genuinely exceptional conditions like out-of-memory or
  filesystem errors.

## Bridge / game integration

- **DON'T reach into Palworld from the sidecar.** The sidecar never
  imports a UE4SS package, never opens a process handle to
  Palworld, never injects a hook. Communication is **filesystem
  one-way** through `Bridge/Inbox/` and `Bridge/Outbox/`.
  See [`adr/0003-one-way-advisory-bridge.md`](adr/0003-one-way-advisory-bridge.md).
- **DON'T treat the outbox as a command channel.** The outbox is
  *advisory*. The Lua bridge decides whether to render any
  envelope, and the action executor has its own per-action
  allowlist. Even when the sidecar emits a "craft this item"
  intent, the game-side allowlist can refuse.
- **DON'T add a polling interval below 100ms** on the bridge worker.
  The filesystem isn't designed for that, the disk thrashes, and
  the game loop is the actual real-time surface — the bridge is
  chat-rate (~1 Hz).

## Configuration / defaults

- **DON'T add a feature that's on by default if it sends data
  off-machine.** Inference, vision, TTS, OTLP, MCP upstream proxy,
  action intents — all default off, all flip with one config flag.
  See [`adr/0006-opt-in-everything-by-default.md`](adr/0006-opt-in-everything-by-default.md).
- **DON'T hardcode third-party endpoints.** Every external URL
  comes from `PalLlmOptions` (`Inference:BaseUrl`, `Vision:BaseUrl`,
  `Tts:BaseUrl`). The defaults are `localhost` URLs that point
  nowhere by default; an operator names the endpoint they want.
- **DON'T add a config knob without a default.** If the runtime
  can't decide what to do without operator input, it has the
  shape wrong. Every new option has a documented default.

## Documentation discipline

- **DON'T add a route without bumping the route count** in README,
  ROADMAP, ARCHITECTURE, and API.md, and regenerating the OpenAPI
  snapshot. The drift gate `Drift_Api_route_count` will fire if
  you skip; don't disable it.
- **DON'T add a feature without a `FeatureDescriptor` entry** in
  `PalLlmFeatureCatalog.cs`. The dashboard, `/api/features`, and
  `/api/describe` all read this catalog. Skipping the entry hides
  your feature from operators.
- **DON'T let a doc go stale past 45 days.** The
  `Drift_Doc_freshness` gate enforces a `Last audited:` stamp on
  every long-form doc. If you read the doc and it's still
  accurate, bump the stamp; if it's not, fix it.
- **DON'T use third-party brand names in release-facing copy.**
  README, NOTICE, SECURITY, INDEX, RELEASE, and CONTRIBUTING are
  scanned by `Drift_Public_copy`. Use neutral language ("any
  MCP-capable client") instead of pinning specific vendor brands.

## Code style / refactoring

- **DON'T mass-reformat.** This repo's hot files (`PalLlmRuntime.cs`
  at ~1104 lines, `PresentationCuePlanner.cs` at ~1427 lines, etc.)
  look intimidating but every method is documented inline. A
  formatter pass loses the carefully-grouped structure.
- **DON'T delegate understanding via sub-agents when a Grep will
  do.** The drift gates + per-file conventions make this repo easy
  to navigate directly. Spinning a sub-agent for a question a Grep
  could answer in seconds wastes context and obscures provenance.
- **DON'T rename `Portable/` or its interfaces** without a
  migration ADR. That seam is the *single* contract a downstream
  harvester relies on. See
  [`adr/0002-portable-adapter-seam.md`](adr/0002-portable-adapter-seam.md).
- **DON'T add a new helper class when an existing advisor /
  builder / validator / feeder fits.** The four patterns are
  documented in [`CONVENTIONS.md`](CONVENTIONS.md); a fifth
  pattern raises onboarding cost.

## Performance

- **DON'T cache without a signature.** The TTL-cache pattern uses
  signature-based invalidation so a config change forces a
  recompute regardless of TTL. Time-only caches go stale silently
  and are very hard to debug.
  See [`adr/0005-ttl-cache-for-posture-surfaces.md`](adr/0005-ttl-cache-for-posture-surfaces.md).
- **DON'T put blocking I/O on the chat hot path.** `ChatAsync`
  must complete in human-conversational time (deterministic path:
  <200ms; with inference: <2s). New work added to that method
  must be either (1) async, (2) cached, or (3) explicitly off the
  hot path. See [`HOT_PATH.md`](HOT_PATH.md) for budgets.
- **DON'T poll a network endpoint synchronously from a request
  handler.** Use `IHttpClientFactory` and the configured client
  with explicit timeout + cancellation token.

## Promotion / automation

- **DON'T let promotion-apply mutate source code in-place.** Apply
  writes to the staging root (`Runtime/PromotionStaging/`); a
  human reviewer cherry-picks. The runtime never edits its own
  code without explicit operator action — that's reserved for the
  human-driven recover/install paths.
- **DON'T add a guarded action without a Lua-side allowlist
  entry.** The sidecar can emit an action intent only if the type
  is in `AutomationOptions.AllowedActions`; the Lua bridge can
  execute it only if the type is also in its own allowlist. Two
  fences — both required.

## Testing

- **DON'T require a live inference endpoint for tests.** The test
  suite must run on a clean machine with no external
  dependencies. Tests that need an LLM use the in-process
  deterministic fallback or a fake.
- **DON'T require Palworld for tests.** The test suite uses the
  in-memory adapter fake. A test that boots the real Palworld
  process belongs in a separate manual or integration job, not
  the regular suite.
- **DON'T weaken a test to make it pass.** The tests are the
  spec. If a test is wrong, fix it explicitly and explain why in
  the PR; don't `Assert.Pass()` your way out of a regression.

## Things that look like anti-patterns but aren't

- **`PalLlmRuntime.cs` is still ~1104 lines after the helper,
  UI-probe, prompt, bridge-boot, bridge-activity, outbox, snapshot, and inference splits.** Yes —
  and intentional for now. Phase 1a moved pure helpers into
  `PalLlmRuntime.Helpers.cs`; Phase 1b moved diagnostics into
  `PalLlmRuntime.UiProbe.cs`; Phase 1c moved prompt rendering into
  `PalLlmRuntime.Prompt.cs`; Phase 1d moved bridge-boot/native-readiness
  helpers into `PalLlmRuntime.BridgeBoot.cs`; Phase 1e moved bridge
  drain/activity/proof helpers into `PalLlmRuntime.Bridge.cs`; Phase
  1f moved outbox/archive helpers into `PalLlmRuntime.Outbox.cs`; Phase
  1g moved snapshot/health helpers into `PalLlmRuntime.Snapshot.cs`;
  Phase 1h moved inference warmup/metrics helpers into
  `PalLlmRuntime.Inference.cs`; the remaining runtime spine stays
  searchable and inline-documented. Phase 2a has now moved service
  registration into `src/PalLLM.Sidecar/Configuration/*.cs`; Phase 2b
  widened the route-count audit and moved the inference/MCP-upstream
  routes into `src/PalLLM.Sidecar/RouteRegistrations/PalLlmInferenceRoutes.cs`;
  Phase 2c moved the bridge/outbox routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmBridgeRoutes.cs`;
  Phase 2d moved the multimodal media routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmMediaRoutes.cs`;
  Phase 2e moved the health/manifest routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmHealthRoutes.cs`;
  Phase 2f moved the read-only inspection/advisory routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmInspectionRoutes.cs`;
  Phase 2g moved the memory, relationship, and session state routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmStateRoutes.cs`;
  Phase 2h moved the content/world routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmContentWorldRoutes.cs`;
  Phase 2i moved the promotion-loop routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmPromotionRoutes.cs`;
  Phase 2j moved the proof/readiness routes into
  `src/PalLLM.Sidecar/RouteRegistrations/PalLlmProofReadinessRoutes.cs`.
- **Heavy XML doc comments on options classes.** The cost is real
  but the harvester benefit is large — every config knob is
  self-explaining without a separate config reference doc.
- **Multiple "do the same thing" surfaces (`/api/describe` +
  `/api/features` + `/api/health`).** Different consumers
  (operators, AI clients, dashboard) need different shapes. Each
  endpoint has a clear, distinct audience. See
  [`API.md`](API.md) for the full surface map.

## Related

- [`CONVENTIONS.md`](CONVENTIONS.md) — the four patterns + three
  hard rules positively expressed
- [`DESIGN_PRINCIPLES.md`](DESIGN_PRINCIPLES.md) — the ten numbered
  principles
- [`adr/`](adr/) — full Architecture Decision Records for the
  load-bearing decisions
