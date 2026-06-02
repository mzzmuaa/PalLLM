# State machines

Last audited: `2026-06-01`

Mermaid `stateDiagram-v2` views of the explicit state machines
inside PalLLM. Each diagram is the canonical mental model — the
code aligns with these states and transitions. If the code drifts
from the diagram, **fix the code** (or, if the diagram is wrong,
fix the diagram in the same PR).

## 1. Inference circuit breaker

Protects the chat hot path from a flaky inference endpoint.
Counts consecutive failures; trips when the threshold breaches;
half-opens after a cooldown to test recovery; closes on the
first successful trial.

```mermaid
stateDiagram-v2
    [*] --> Closed
    Closed --> Open: consecutive failures &ge; threshold
    Open --> HalfOpen: cooldown elapsed
    HalfOpen --> Closed: trial succeeds
    HalfOpen --> Open: trial fails
    Closed --> Closed: success<br/>(reset failure counter)

    note right of Closed
        Live inference attempts proceed.
        InferenceCircuitOpen = false.
    end note

    note right of Open
        All chat turns route to deterministic
        fallback. The open breaker surfaces as an inference
        failure, so ResponsePath = 'fallback_inference_failed'.
        InferenceCircuitOpen = true.
    end note

    note right of HalfOpen
        Exactly one trial inference is allowed.
        Subsequent calls keep the breaker Open
        until the trial completes.
    end note
```

**Source of truth**: `src/PalLLM.Domain/Inference/InferenceClient.cs`
(circuit-breaker logic). The threshold and cooldown are
configurable via
`Inference:CircuitBreakerFailureThreshold` and
`Inference:CircuitBreakerCooldownSeconds`.

**Observability**: every transition emits a structured log line
and a tag on the next `Chat.Inference` span. The breaker's
current state is reported in `RuntimeHealth.InferenceCircuitOpen`
and the dashboard's circuit-breaker chip.

**Recovery without restart**: no operator action is needed. Once
the cooldown elapses the breaker self-transitions Open → HalfOpen,
and the next ordinary chat turn becomes the trial inference — if the
underlying endpoint is healthy the trial succeeds and the breaker
closes.

## 2. Bridge inbox worker

Background `IHostedService` that drains `Bridge/Inbox/`. Stays
in `Polling` while the sidecar is up; transitions to `Draining`
when files are present; back to `Polling` when the directory is
empty again. `Stopped` only on host shutdown.

```mermaid
stateDiagram-v2
    [*] --> Starting
    Starting --> Polling: hosted service ready
    Polling --> Draining: file count > 0 at poll tick
    Draining --> Polling: drained up to MaxEventsPerPoll
    Polling --> Stopped: HostApplicationLifetime.StopApplication
    Draining --> Stopped: cancellation requested mid-drain

    note right of Polling
        Sleeps PollIntervalMs between scans.
        Default 1000 ms.
    end note

    note right of Draining
        Processes envelopes in directory order.
        Per-envelope budget &lt; 100 ms.
        Successes: move to Bridge/Archive/.
        Failures: move to Bridge/Failed/ with reason.
    end note

    note right of Stopped
        In-flight envelope (if any) is
        completed before shutdown returns.
    end note
```

**Source of truth**: `src/PalLLM.Sidecar/BridgeInboxWorker.cs`.

## 3. Promotion ledger lifecycle

A bounded in-memory window of observations kept per task class. Each entry has
a class (`task class` like `fallback-director`, `live-inference`)
and a pattern id. Suggestions read the top-N entries; apply
optionally promotes one to staging artifacts.

```mermaid
stateDiagram-v2
    [*] --> Empty
    Empty --> Observed: feeder records first observation
    Observed --> Observed: feeder records additional observation
    Observed --> Suggested: GET /api/promotion/suggestions reads
    Suggested --> Suggested: same suggestion served from same data
    Suggested --> Staged: POST /api/promotion/apply (AllowApply=true)
    Staged --> Suggested: operator deletes staging files
    Observed --> Empty: ring-buffer eviction

    note right of Empty
        No observations yet. Suggest endpoint
        returns an empty list.
    end note

    note right of Observed
        In-memory only. Bounded per task class
        (PromotionLedger.PerTaskWindow = 200
        observations); drops oldest when full.
    end note

    note right of Staged
        Files written to Runtime/PromotionStaging/:
        - template-&lt;id&gt;.md (the change recipe)
        - rollback-&lt;id&gt;.txt (how to undo)
        - packet-&lt;id&gt;.json (audit provenance)
        Source code is NEVER mutated.
    end note
```

**Source of truth**: `src/PalLLM.Domain/Runtime/PromotionLedger.cs`,
`PromotionLedgerFeeder.cs`, `PromotionApplier.cs`.

## 4. TTL cache (posture surfaces)

The pattern from ADR 0005, abstracted. Every `*Cached` builder
has the same shape.

```mermaid
stateDiagram-v2
    [*] --> Cold
    Cold --> Warm: Capture()<br/>store entry { posture, signature, capturedAt }
    Warm --> Warm: subsequent call<br/>signature matches AND age &lt; TTL
    Warm --> Cold: signature changed (config flag flipped)
    Warm --> Cold: age &gt;= TTL
    Warm --> Cold: InvalidateCache()

    note right of Cold
        Next call recomputes from inputs.
        Replaces _cached with the fresh entry.
    end note

    note right of Warm
        Subsequent calls return the cached
        snapshot in microseconds (signature
        compare + branch, no I/O).
    end note
```

**Source of truth**: every `*Cached` method follows this shape.
The cleanest reference implementation is
`src/PalLLM.Sidecar/AirGapVerifier.cs` (`VerifyCached`).

## 5. Chat reply path (which strategy fires?)

Not a true state machine — more a deterministic decision tree.
Documenting it here because the choice tree is what produces the
`ResponsePath` value on every `ChatResponse`.

```mermaid
stateDiagram-v2
    [*] --> RateLimited?
    RateLimited? --> Fallback_director: yes<br/>(ResponsePath: rate_limited_fallback)
    RateLimited? --> PolicyBypass?: no

    PolicyBypass? --> Fallback_director: fast-path match<br/>(ResponsePath: fallback_policy_bypass)
    PolicyBypass? --> Inference_enabled?: no match

    Inference_enabled? --> Inference_attempted: yes
    Inference_enabled? --> Disabled_fallback?: no

    Disabled_fallback? --> Fallback_director: fallback on<br/>(ResponsePath: fallback_inference_disabled)
    Disabled_fallback? --> [*]: fallback off<br/>(ResponsePath: inference_disabled_no_fallback)

    Inference_attempted --> [*]: success<br/>(ResponsePath: live_inference)
    Inference_attempted --> Failed_fallback?: timeout / 5xx / breaker open

    Failed_fallback? --> Fallback_director: fallback on<br/>(ResponsePath: fallback_inference_failed)
    Failed_fallback? --> [*]: fallback off<br/>(ResponsePath: inference_failed_no_fallback)

    Fallback_director --> [*]: a Try_* matched<br/>(reply carries the fallback ResponsePath above)
    Fallback_director --> [*]: every Try_* null → EmergencyFallback.Guard<br/>(canned acknowledgement, same ResponsePath)
```

**Source of truth**: `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`
(`ChatAsync`).

The `ResponsePath` value is the single most useful diagnostic in
the runtime. Every reason a chat could land somewhere unexpected
shows up there.

## Related

- [`DATAFLOW.md`](DATAFLOW.md) — sequence diagrams for the
  flows these state machines participate in
- [`OBSERVABILITY.md`](OBSERVABILITY.md) — every state
  transition above is observable as a tagged span
- [`HOT_PATH.md`](HOT_PATH.md) — the latency budgets the
  states must hit
- [`adr/0005-ttl-cache-for-posture-surfaces.md`](adr/0005-ttl-cache-for-posture-surfaces.md)
  — the cache state machine's ADR
- [`RUNBOOK.md`](RUNBOOK.md) — what to do when you observe an
  unexpected transition
