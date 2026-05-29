# Invariants - what's guaranteed to be true at runtime

Last audited: `2026-05-24`

[`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) lists what NOT to do. This
doc lists what IS GUARANTEED - the load-bearing invariants the
runtime upholds, the docs reflect, and the drift gates enforce. If
you observe one of these violated, treat it as a bug, not a quirk.

Each invariant names where it's enforced (code or gate) so you can
trace claim -> implementation.

## Reply pipeline

1. **Every chat turn produces a working `ChatResponse`.**
   Even if inference is off / down / breaker-tripped /
   rate-limited / thermal-gated, the deterministic fallback
   director returns a multi-sentence reply. The third-tier
   `EmergencyFallback` guards even that - a broken
   personality pack returns a canned acknowledgement instead
   of crashing. **Enforced:**
   `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` (`ChatAsync`),
   `tests/PalLLM.Tests/RuntimeTests.cs`,
   `tests/PalLLM.Tests/EmergencyFallbackTests.cs`. **ADR:**
   [`0001`](adr/0001-deterministic-first-reply-pipeline.md).

2. **`ChatAsync` never throws through the HTTP layer for
   inference faults.** Upstream model errors surface as
   `ChatResponse` with a diagnostic `ResponsePath` value, not
   HTTP 5xx. **Enforced:** `tests/PalLLM.Tests/RuntimeTests.cs`,
   `SidecarEndpointTests.cs`.

3. **Every `ChatResponse` carries a non-null
   `PresentationCuePlan`.** A UE4SS consumer can render any
   reply without a "did the cue planner run?" check.
   **Enforced:** `src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs`
   (every fallback strategy is paired with a cue family),
   `tests/PalLLM.Tests/RuntimeTests.cs`.

4. **`ResponsePath` is a stable diagnostic string.** Every
   reason a chat could land somewhere unexpected has a known
   `ResponsePath` value (see [`QUICKREF.md`](QUICKREF.md)
   "ResponsePath values"). Operators and tests can dispatch
   on it.

## Bridge

5. **The sidecar never reaches into Palworld's process.**
   No FFI, no injection, no shared memory. Communication is
   filesystem-only, one-way at any moment. **Enforced:**
   `src/PalLLM.Domain/PalLLM.Domain.csproj` has no UE4SS
   reference; the audit's `Drift_Path_references` gate would
   fail if it did. **ADR:**
   [`0003`](adr/0003-one-way-advisory-bridge.md).

6. **Outbox writes are advisory.** The sidecar fires-and-
   forgets. The Lua bridge decides whether to render any
   envelope; action intents inside an envelope are doubly
   advisory and subject to the Lua-side allowlist.

7. **Every bridge directory has a retention cap.**
   `OutboxMaxFiles`, `ArchiveMaxFiles`, `FailedMaxFiles`,
   `DiagnosticsMaxFiles`. The disk never fills from PalLLM
   alone. **Enforced:**
   `src/PalLLM.Domain/Configuration/PalLlmOptions.cs` defaults,
   `src/PalLLM.Domain/Runtime/DirectoryRetention.cs`. Retention
   sweeps enumerate lazily and keep only a bounded newest-file queue
   while pruning, so backlog cleanup does not require a full directory
   materialization.

## Configuration

8. **Every feature that emits network traffic is off by
   default.** Inference, Vision, TTS, OTLP export,
   action-intent emission, MCP upstream proxy.
   **Enforced:** `src/PalLLM.Domain/Configuration/PalLlmOptions.cs`
   property defaults; `tests/PalLLM.Tests/AirGapVerifierTests.cs`
   checks the default-config air-gap report. **ADR:**
   [`0006`](adr/0006-opt-in-everything-by-default.md).

9. **Every operator-configurable knob has a documented
   default.** The runtime has a coherent posture without
   any operator input. **Enforced:** every property in
   `PalLlmOptions.cs` has a default initializer; the XML
   docs explain the effect.

10. **The privacy posture surface tells the truth.**
    `/api/privacy/posture` reads the live `PalLlmOptions`
    instance - not a copy, not a snapshot. If a flag is
    flipped at runtime, the next privacy-posture call
    reflects it (subject to the 30 s TTL cache, which uses
    signature-based invalidation so a config change forces
    recompute regardless of TTL). **Enforced:**
    `tests/PalLLM.Tests/PrivacyPostureBuilderTests.cs`
    invalidate-cache-on-signature-change tests.

## Persistence

11. **Memory autosave skips zero-cost writes.**
    `ConversationMemoryStore.PersistAsync` checks the
    mutation version against the last-saved version and
    skips the file write when nothing changed. Idle turns
    cost zero I/O. **Enforced:**
    `src/PalLLM.Domain/Memory/ConversationMemoryStore.cs`,
    `tests/PalLLM.Tests/ConversationMemoryStoreTests.cs`.

12. **`session.json` writes are atomic.** Write to
    `session.json.tmp`, rename to `session.json`. A torn
    write during shutdown can never happen - either the
    full new state lands or the previous file is intact.
    **Enforced:** `ConversationMemoryStore.PersistAsync`.

## Observability

13. **OpenTelemetry is genuinely opt-in.** No outbound
    network traffic from the OTel packages until
    `OTEL_EXPORTER_OTLP_ENDPOINT` is set. **Enforced:**
    `src/PalLLM.Sidecar/Configuration/PalLlmObservabilityServiceCollectionExtensions.cs`
    registers the OTel pipeline only when the env var is present; the
    air-gap verifier's default-config test confirms zero
    outbound endpoints.

14. **Every public route on `/api/*` returns
    `ProblemDetails` for non-success responses.**
    Validation errors, auth failures, not-found -
    consistent shape, consistent content-type. No ad-hoc
    plain-text bodies, stack traces, or raw exception text.
    **Enforced:**
    `src/PalLLM.Sidecar/PalApiValidation.cs`,
    `tests/PalLLM.Tests/BackendValidationTests.cs`.

15. **`/metrics` reflects live state.** Counters /
    gauges / histograms in `PalLlmMetrics.cs` are
    incremented from the producer site, not from a
    background sweep. The Prometheus scrape always sees
    the current values.

## Documentation

16. **Counts in docs match counts in code.** Test count,
    route count, feature catalog count, fallback strategy
    count - all enforced by drift gates. **Enforced:**
    `scripts/run_full_audit.ps1` gates 4, 7, 8, 9, 10.

17. **Every long-form doc carries a `Last audited:`
    stamp.** 45-day cap. **Enforced:**
    `Drift_Doc_freshness` gate.

18. **Every repo-relative path mentioned in a doc
    actually resolves.** **Enforced:**
    `Drift_Path_references` gate.

19. **Every markdown link in a doc resolves.**
    **Enforced:** `Drift_Dangling_markdown_links` gate.

20. **Release-facing copy avoids third-party brand
    pinning.** README, NOTICE, SECURITY, INDEX, RELEASE,
    CONTRIBUTING, issue templates use neutral language
    ("any MCP-capable client"). **Enforced:**
    `Drift_Public_copy` gate.

## Build

21. **`dotnet build` produces zero warnings.** A new
    warning is treated as a regression. **Enforced:**
    `Build_Release` gate (the audit fails if `dotnet build`
    surfaces any warning).

22. **`dotnet test` produces 1315 / 1315 green.**
    **Enforced:** `Tests` gate plus the
    `Drift_Test_count_docs` gate verifying the count
    matches docs.

23. **The committed OpenAPI snapshot matches the live
    route surface.** **Enforced:**
    `Drift_OpenApi_snapshot` gate.

## Code shape

24. **`PalLLM.Domain` has no project reference to
    `PalLLM.Sidecar` or any UE4SS / Palworld package.**
    The portable seam is real. **Enforced:**
    `src/PalLLM.Domain/PalLLM.Domain.csproj`.

25. **The portable adapter seam is exactly five
    interfaces.** `IGameAdapter`, `ICharacter`,
    `IWorldClock`, `IPathProvider`, `ILogger` in
    `Portable/PortableAdapterContracts.cs`. Renaming or
    splitting would be a breaking change requiring an
    ADR. **ADR:**
    [`0002`](adr/0002-portable-adapter-seam.md).

26. **Every `*Cached` posture method has signature-based
    invalidation.** Time-only caches go stale silently;
    every cache that ships uses signatures.
    **Enforced:**
    `tests/PalLLM.Tests/HardwareProfilerTests.cs`,
    `PrivacyPostureBuilderTests.cs`,
    `ResourceBudgetPostureBuilderTests.cs`,
    `AirGapVerifierTests.cs`. **ADR:**
    [`0005`](adr/0005-ttl-cache-for-posture-surfaces.md).

## How invariants are added

If you find a load-bearing assumption the runtime relies on
that isn't listed here:

1. Add it as a numbered item.
2. Name where it's enforced (code path or gate).
3. If it deserves a full ADR, write one and reference it.
4. Run `pwsh ./pal.ps1 audit` - the gates verify any
   path / link references.

## Related

- [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) - what NOT to do
  (the inverse of this doc)
- [`CONVENTIONS.md`](CONVENTIONS.md) - the four code patterns
  + three hard rules
- [`adr/`](adr/) - full ADRs for the load-bearing decisions
- [`STATE_MACHINES.md`](STATE_MACHINES.md) - explicit
  diagrams for the systems whose invariants matter most


