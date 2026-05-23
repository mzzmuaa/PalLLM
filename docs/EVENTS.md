# Events — bridge events, traces, metrics

Last audited: `2026-05-20`

Single-stop reference for every observable event PalLLM produces or
consumes. Three categories:

1. **Bridge events** — JSON envelopes on the filesystem under
   `Bridge/Inbox/` and `Bridge/Outbox/`. Producers and consumers
   are PalLLM and the Lua bridge; an external integrator can
   write or read them too.
2. **OpenTelemetry spans + tags** — emitted by the runtime when
   `OTEL_EXPORTER_OTLP_ENDPOINT` is set.
3. **Prometheus metrics** — counters / gauges / histograms exposed
   on `/metrics`.

Pairs with [`OBSERVABILITY.md`](OBSERVABILITY.md) (how to wire up a
collector) and [`docs/schemas/`](schemas/) (formal JSON Schemas for
the bridge envelopes).

## 1. Bridge inbox events (Lua → sidecar)

Producers write JSON files matching
[`schemas/bridge-event-envelope.schema.json`](schemas/bridge-event-envelope.schema.json)
into `Bridge/Inbox/`. The sidecar's `BridgeInboxWorker` drains them through a
bounded JSON reader capped by `PalLLM:Bridge:MaxInboxEventBytes` (`65536`
bytes by default), so malformed, unreadable, or oversized envelopes are
quarantined to `Bridge/Failed/` instead of being buffered unchecked.

| `EventType` | When emitted | Consumed by | Outcome |
|---|---|---|---|
| `bridge_boot` | Lua bridge startup / heartbeat with compat and native-HUD posture | `PalLlmRuntime.ProcessBridgeEvent` | Bridge activity snapshot, boot proof, and native-HUD recommendation surfaces refresh |
| `chat_message` | Player chat captured by the game-side bridge | `PalLlmRuntime.ProcessBridgeEvent` | Memory records the utterance for later planning; separate callers still use `POST /api/chat` for immediate replies |
| `snapshot` | Periodic world-state snapshot from the bridge | `PalLlmRuntime.ProcessBridgeEvent` | Cached `GameWorldSnapshot` is replaced |
| `base_discovered` | Bridge observes or confirms a base / outpost | `PalLlmRuntime.ProcessBridgeEvent` | Base memory recorded and `KnownBases` promoted |
| `combat_start` | Combat begins | `PalLlmRuntime.ProcessBridgeEvent` | Combat memory + recent-event marker recorded |
| `combat_end` | Combat ends | `PalLlmRuntime.ProcessBridgeEvent` | Combat memory + recent-event marker recorded |
| `pal_status` | Pal task/status feedback from the game side | `PalLlmRuntime.ProcessBridgeEvent` | Action-feedback memory and recent-event marker recorded |
| `production` | Base production or logistics feedback | `PalLlmRuntime.ProcessBridgeEvent` | Production snapshot and memory updated |
| `travel` | Travel or waypoint feedback | `PalLlmRuntime.ProcessBridgeEvent` | Travel snapshot updated; memory recorded unless marked live movement |
| `weather_change` | Weather/biome shift observed by the bridge | `PalLlmRuntime.ProcessBridgeEvent` | Snapshot weather/biome and recent-event marker updated |
| `raid` | Base raid warning or phase update | `PalLlmRuntime.ProcessBridgeEvent` | Raid memory + recent-event marker recorded |
| `ui_probe` | Widget/HUD discovery summary pointing to a dump under `Bridge/Diagnostics/` | `PalLlmRuntime.ProcessBridgeEvent` | UI-probe snapshot stored for `/api/bridge/ui-probe` and `/api/bridge/proof` |
| `reply_delivery` | Game-side renderer reports whether a reply card rendered | `PalLlmRuntime.ProcessBridgeEvent` | Delivery-loop proof and reply-delivery snapshot updated |
| `speech_playback` | Game-side bridge reports a local speech playback attempt or proof-only skip for a TTS artifact | `PalLlmRuntime.ProcessBridgeEvent` | Content-free speech playback proof updated with request id, playback sequence, superseded request id/count/age/prior-buffer/estimated-remaining-buffer, cancellation mode, artifact byte count, WAV or raw-PCM encoding / sample format / byte order / native-mixer conversion hint / native-mixer queue and buffer-duration estimates / sample rate / channel count / bit depth / duration / byte-rate / block-align / audio-data-size / sample-frame count / partial-frame remainder / valid-bits / channel-mask metadata when available, launch attempt count, helper-launch elapsed milliseconds, mode/hint, MIME/extension, started/skipped state, reason, and stable `FailureCode`; unsupported WAV encodings report `wave_encoding_unsupported`, WAV block-alignment mismatches report `wave_block_alignment_invalid`, raw PCM reports mode `raw_pcm`, zero launch attempts, and `raw_pcm_native_mixer_required` while the default-off native mixer callback is disabled; enabled callback paths can report `native_audio_mixer_unavailable`, `native_audio_mixer_failed`, or `native_audio_mixer_rejected`, and only a started `native_mixer` receipt proves engine-side raw PCM playback; incomplete raw sample frames report `raw_pcm_block_alignment_invalid`; no audio bytes or file path are stored |

Unknown event types are ignored and then archived to `Bridge/Failed/`. The
exact payload schemas live under `$defs` in
[`schemas/bridge-event-envelope.schema.json`](schemas/bridge-event-envelope.schema.json).

## 2. Bridge outbox events (sidecar → Lua)

The sidecar writes one envelope per chat reply matching
[`schemas/outbox-envelope.schema.json`](schemas/outbox-envelope.schema.json).
The Lua bridge polls and renders.

| `EventType` | When emitted | Consumed by | Payload |
|---|---|---|---|
| `chat_reply` | After every successful `ChatAsync` (including deterministic fallback) | Lua `consume_outbox()` loop | Full `OutboxChatReply` (assistant text + presentation cues + optional action intent + optional speech artifact) |

The outbox is **advisory** — the Lua side decides whether to render
each envelope. Action intents inside the envelope are doubly
advisory: even when emitted, the Lua-side allowlist can refuse.

## 3. OpenTelemetry spans

Source: `PalLLM.Runtime` (defined in
`src/PalLLM.Domain/Runtime/PalLlmTelemetry.cs`). Activated only
when `OTEL_EXPORTER_OTLP_ENDPOINT` is set.

### Chat path

| Span | Started in | Tags | Notes |
|---|---|---|---|
| `Chat.Turn` | `PalLlmRuntime.ChatAsync` | `character.id`, `task.kind`, `response.path` | Root span for one chat turn — every other chat-path span is a child |
| `Chat.Plan` | `ChatDispatchPlanner.Plan` | `pattern`, `roles`, `requires_inference` | Records the planner's decision before any inference call |
| `Chat.Inference` | inference HTTP call site | `model`, `endpoint`, `tokens.in`, `tokens.out` | Only emitted when inference is reached (not on fallback-only turns) |
| `Chat.Fallback` | `FallbackBehaviorEngine.CreateGeneralDirector` | `strategy`, `tier` | Fires when the deterministic director runs (with or without inference) |

### Bridge

| Span | Started in | Tags |
|---|---|---|
| `Bridge.Drain` | `BridgeInboxWorker.ExecuteAsync` (per tick) | `events.read`, `events.processed`, `events.failed` |
| `Bridge.Outbox.Write` | `PalLlmRuntime.WriteOutboxAsync` | `envelope.kind`, `bytes` |

### Memory

| Span | Started in | Tags |
|---|---|---|
| `Memory.Recall` | `ConversationMemoryStore.Recall` | `character.id`, `top_k`, `recall.strategy` |
| `Memory.Persist` | `ConversationMemoryStore.PersistAsync` | `entries.written`, `mutation.version` (zero when skipped) |

### Vision / TTS

| Span | Started in | Tags |
|---|---|---|
| `Vision.Describe` | `HttpVisionClient.DescribeAsync` | `endpoint`, `bytes.in`, `result.bytes` |
| `TTS.Synthesize` | `HttpTtsClient.SynthesizeAsync` | `endpoint`, `chars.in`, `bytes.out` |

### Posture surfaces

| Span | Started in | Tags |
|---|---|---|
| `Posture.Capture` | `*PostureBuilder.CaptureCached` (and `AirGapVerifier.VerifyCached`) | `surface` (`hardware`/`privacy`/`budget`/`airgap`/`describe`), `cache.hit` |

## 4. Prometheus metrics (`GET /metrics`)

Source: `src/PalLLM.Domain/Runtime/PalLlmMetrics.cs`. Exposed
unconditionally; the `/metrics` route is public-by-default but can
be protected via `Auth:ProtectMetrics = true`.

### Counters (monotonic)

| Metric | What it counts |
|---|---|
| `palllm_chat_turns_total{path}` | Chat turns, labeled by `ResponsePath` (`inference-completed`, `fallback-after-breaker-open`, etc.) |
| `palllm_inference_calls_total{outcome}` | Live inference attempts, labeled by `success` / `error` / `timeout` / `breaker-skip` / `rate-limited` / `thermal-gated` |
| `palllm_inference_circuit_trips_total` | Number of times the breaker tripped from Closed to Open |
| `palllm_bridge_inbox_events_total{outcome}` | Inbox events processed, labeled by `success` / `failed` |
| `palllm_bridge_outbox_writes_total{kind}` | Outbox envelopes written, labeled by event kind |
| `palllm_memory_recalls_total{strategy}` | Recall calls, labeled by recall strategy |
| `palllm_memory_persists_total{outcome}` | Autosave invocations, labeled by `wrote` / `skipped` |
| `palllm_vision_calls_total{outcome}` | Vision describe calls, labeled by `success` / `error` / `timeout` / `disabled` |
| `palllm_tts_calls_total{outcome}` | TTS synthesis calls, labeled by `success` / `error` / `timeout` / `disabled` |
| `palllm_health_score_drops_total` | Times the operator-health score dropped below `UnhealthyScoreFloor` |

### Gauges (point-in-time)

| Metric | What it reports |
|---|---|
| `palllm_health_score` | Current `OperatorHealthScore` (0-100) |
| `palllm_inference_circuit_state` | `0` = Closed, `1` = HalfOpen, `2` = Open |
| `palllm_outbox_pending_files` | Files in `Bridge/Outbox/` awaiting consumption |
| `palllm_inbox_pending_files` | Files in `Bridge/Inbox/` awaiting drain |
| `palllm_archive_total_bytes` | Disk usage of `Bridge/Archive/` |

### Histograms (latency distribution)

| Metric | What it tracks |
|---|---|
| `palllm_chat_turn_seconds` | End-to-end `ChatAsync` latency, exemplars by `response.path` |
| `palllm_inference_seconds` | Inference HTTP call duration |
| `palllm_bridge_drain_seconds` | Per-envelope drain duration |
| `palllm_memory_recall_seconds` | Recall call duration |

Buckets: `0.005`, `0.01`, `0.025`, `0.05`, `0.1`, `0.25`, `0.5`,
`1`, `2.5`, `5`, `10` seconds.

## 5. Cross-event correlation

A single chat turn flows through the layers like this:

1. `Bridge.Drain` ingests `chat_message` from
   `Bridge/Inbox/`.
2. `Chat.Turn` (parent span) wraps `ChatAsync`.
3. `Memory.Recall` runs as a child of `Chat.Turn`.
4. `Chat.Plan` runs.
5. Either `Chat.Inference` or `Chat.Fallback` runs (sometimes
   both: inference fails → fallback fires).
6. `Memory.Persist` runs (no-op if mutation version unchanged).
7. `Bridge.Outbox.Write` runs.

The `palllm_chat_turns_total` counter and the
`palllm_chat_turn_seconds` histogram are incremented on the way
out of step 7. Each metric increment has the same
`response.path` label as the parent span's tag, so a query like
"show me the histogram for `fallback-after-breaker-open`
turns" works without joins.

## Related

- [`OBSERVABILITY.md`](OBSERVABILITY.md) — how to wire up Jaeger /
  Tempo / Honeycomb to consume these spans
- [`schemas/`](schemas/) — JSON Schema 2020-12 contracts for the
  bridge envelopes
- [`STATE_MACHINES.md`](STATE_MACHINES.md) — state diagrams for
  the systems whose transitions become events
