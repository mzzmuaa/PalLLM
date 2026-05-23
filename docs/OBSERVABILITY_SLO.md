# Observability SLOs + alerting

Last audited: `2026-05-23`

This doc declares PalLLM's Service Level Objectives (SLOs), the
Service Level Indicators (SLIs) that measure them, and the
shipping Prometheus alert rules + reference Grafana dashboard
that monitor them. Companion to
[`OBSERVABILITY.md`](OBSERVABILITY.md) — that doc covers the
OTel + Prometheus wiring; this one covers the **production
contract** the wiring is in service of.

> **Pass 358 (the senior-dev review's Tier-S item #4).** Before
> this pass, PalLLM emitted OTel metrics but shipped no alert
> rules, no SLO contract, no reference dashboard. Operators
> deploying to a remote rig (Pass 357 remote-PC escape path) or
> paying for a cloud account had no off-the-shelf
> observability. Pass 358 fixes that.

## The three SLOs

### 1. Availability SLO

**99.5% of chat turns return a non-empty reply within budget**
(rolling 30-day window).

PalLLM's deterministic-fallback director means the companion is
*never* mute — every chat turn returns something. The
availability question is therefore "did the turn finish within
the latency budget?" rather than "did it finish at all."

**SLI:** `rate(palllm_chat_duration_seconds_count[30d])` ratio
over successful (non-error) turn count. PalLLM emits a
`palllm_chat_errors_total` counter for terminal errors; subtract
this from the total to compute success.

### 2. Latency SLO

**p95 chat turn duration < Standard-tier budget (2500 ms)** over
a 30-day window.

PalLLM's [`HOT_PATH.md`](HOT_PATH.md) declares four hardware
tiers with different budgets:

| Tier         | p95     | p50     | Hardware                                       |
|--------------|---------|---------|------------------------------------------------|
| Constrained  | 4000 ms | 1500 ms | Older laptop / no GPU                          |
| **Standard** | **2500 ms** | **900 ms** | **Mainstream desktop / mid-range GPU**   |
| Generous     | 1500 ms | 600 ms  | 4070-class / 24+ GiB VRAM                      |
| Blackwell    | 900 ms  | 450 ms  | 5090 / B-series with NVFP4                     |

The shipping SLO targets the Standard tier so the same dashboard
applies to most deployments. The reference rig (Pass 356:
RTX 3090 + 32 GB DDR4 + 5800X3D) lands closer to the Generous
budget in practice; deployments on stronger hardware can tighten
the SLO in their fork.

**SLI:**
```promql
histogram_quantile(
  0.95,
  sum by (le) (rate(palllm_chat_duration_seconds_bucket[30d]))
) < 2.5
```

### 3. Quality SLO

**Fallback-reply rate < 30%** over a rolling 24-hour window.

PalLLM's deterministic fallback director kicks in when the LLM
endpoint is unreachable, too slow, or returns malformed output.
Sustained fallback above 30% means inference is structurally
degraded (cloud quota exhausted, remote PC down, llama-server
crashed, etc.). Below 30% is acceptable transient degradation.

**SLI:**
```promql
(
  rate(palllm_fallback_reply_total[24h])
    /
  rate(palllm_chat_duration_seconds_count[24h])
) < 0.30
```

## The bridge SLI (not an SLO)

The bridge drain worker is one-way (Lua mod → sidecar), so its
backlog is informational rather than user-facing. Still
load-bearing for liveness — a wedged drain worker means new
chat events never reach the runtime.

**SLI:** `palllm_inbox_pending_files < 50` (informational; alert
warns when sustained above the threshold).

## Shipping artifacts

Two files in [`scripts/observability/`](../scripts/observability):

### `palllm.alerts.yaml` — Prometheus rules

Six alerts grouped by SLO category:

| Alert                              | Threshold                                   | For    | Severity   | SLO          |
|------------------------------------|---------------------------------------------|--------|------------|--------------|
| `PalLLMServiceDown`                | `up == 0` (scrape failure)                  | 1m     | critical   | availability |
| `PalLLMChatLatencyHigh_Warning`    | p95 > 2.5s (Standard-tier budget)           | 10m    | warning    | latency      |
| `PalLLMChatLatencyHigh_Critical`   | p95 > 4.0s (even Constrained tier breached) | 5m     | critical   | latency      |
| `PalLLMFallbackRateHigh`           | fallback share > 30% over 15m               | 15m    | warning    | quality      |
| `PalLLMInboxBacklogGrowing`        | `palllm_inbox_pending_files > 50`           | 10m    | warning    | bridge       |
| `PalLLMInferenceLaneRed`           | `palllm_inference_lane_status{state="red"}` | 5m     | warning    | quality      |

Each alert includes a `runbook_url` annotation pointing to the
relevant section below. Operators forking PalLLM should replace
`<owner>` with their GitHub org in the annotations.

### `palllm-grafana-dashboard.json` — Grafana dashboard

Four panels matching the SLOs:

1. **Chat turn latency (p50 / p95 / p99)** — threshold-coloured;
   orange at the Standard-tier budget, red at the
   Constrained-tier budget
2. **Fallback-reply rate** — percentage gauge; orange at 30%
3. **Bridge inbox backlog** — pending-file count over time
4. **Inference lane status** — per-lane state gauge

## Import recipes

### Prometheus alerts

Drop `palllm.alerts.yaml` into your Prometheus rules directory
and reload:

```bash
# Wherever your rule files live (your Prometheus config defines
# this via `rule_files`).
cp scripts/observability/palllm.alerts.yaml /etc/prometheus/rules/
curl -X POST http://localhost:9090/-/reload
```

Verify alerts are registered:

```bash
curl -s http://localhost:9090/api/v1/rules | jq '.data.groups[] | select(.name | startswith("palllm_")) | .name'
```

### Grafana dashboard

Import via the Grafana UI:

1. Sidebar → **+** → **Import**
2. **Upload JSON file** → choose
   `scripts/observability/palllm-grafana-dashboard.json`
3. Pick your Prometheus datasource on the import wizard
4. **Import**

Dashboard UID is `palllm-slo-overview`; reuse this if you want
deep links from runbook docs.

## Alertmanager routing

The alerts use a `severity` label (`critical` / `warning`) so
your existing Alertmanager routing tree picks them up without
per-alert rules:

```yaml
# alertmanager.yml example
route:
  routes:
    - matchers:
        - severity = critical
      receiver: pager
    - matchers:
        - severity = warning
      receiver: ticket
```

Severity-routing matches the
[Google SRE workbook](https://sre.google/workbook/alerting-on-slos/)
convention.

## Runbook hints

Each alert points back here via its `runbook_url`. The
single-source-of-truth runbook lives below; copy-edit it for
your deployment.

### PalLLMServiceDown {#palllmservicedown}

1. Try `curl http://<instance>/health/live` directly — if 200
   then it's a Prometheus scrape problem
2. Check process status: `Get-Process palllm-sidecar` on Windows
3. Check `Runtime/LaunchEvidence/latest-player-launch.json` for
   the most recent launch outcome
4. If process is dead, restart via `play.bat` (player install) or
   `dotnet run --project src/PalLLM.Sidecar` (dev install)

### PalLLMChatLatencyHigh {#palllmchatlatencyhigh}

1. Check inference lane status via
   `curl http://<instance>/api/inference/lane-status`
2. If a lane is RED, the runtime already routed away from it —
   check the underlying engine (llama-server log, cloud API
   status page, remote-PC reachability)
3. Run `pwsh ./pal.ps1 models probe` against the model endpoint
   to capture `/v1/models` plus cache/speculation/latency metric
   family evidence without sending a prompt
4. GPU thermal throttle: `nvidia-smi` on the host running
   llama-server; sustained > 85 °C drops clocks
5. KV-cache fragmentation: restart llama-server (or rely on
   `--sleep-idle-seconds` if configured)
6. Cloud provider latency: check the provider's status page;
   p95 spikes on Groq / OpenAI / etc. are public

### PalLLMFallbackRateHigh {#palllmfallbackratehigh}

1. Check `/v1/models` against the configured `PalLLM:Inference:BaseUrl`
2. Run `pwsh ./pal.ps1 models probe -BaseUrl <endpoint>/v1`
   to archive model-catalog and metrics-readiness evidence beside the incident
3. If using cloud: validate the API key
   (`pwsh ./scripts/connect-cloud.ps1 -Provider ... -Probe`)
4. If using bundled llama.cpp: check
   `Get-Process llama-server` — restart via
   `install-llama-cpp.ps1 -AutoLaunch` if dead
5. If using remote PC: ping the remote IP, try
   `/health` directly

### PalLLMInboxBacklogGrowing {#palllminboxbacklog}

1. Check
   `Get-ChildItem Runtime/Bridge/Inbox/*.json | Measure-Object`
2. Inspect oldest file:
   `Get-ChildItem Runtime/Bridge/Inbox/*.json | Sort CreationTime | Select -First 1 | Get-Content -Tail 50`
3. If the drain worker is wedged on a corrupt event (Pass 355
   adversarial tests guard against most shapes), check
   `Runtime/Bridge/Failed/` — that's where the worker moves
   files it can't deserialize
4. Raise `PalLLM:Bridge:MaxEventsPerPoll` if the production rate
   exceeds the drain rate consistently

### PalLLMInferenceLaneRed {#palllminferencelane}

1. Look up the operation in `/api/inference/lane-status`
2. The recent-window check threshold lives in
   `PalLLM:Inference:RecentWindow:*` config — check whether
   you have the threshold tuned for your hardware
3. Underlying engine is the cause; same triage as
   `PalLLMFallbackRateHigh` above

## Tuning for your deployment

The shipped thresholds target a Standard-tier deployment on
PalLLM's reference rig. For other deployments:

- **Generous tier (RTX 4070 / 24+ GB VRAM):** drop the p95
  warning to 1500ms and the critical to 2500ms
- **Blackwell tier (RTX 5090 / B-series with NVFP4):** drop the
  p95 warning to 900ms and the critical to 1500ms
- **Cloud-API deployment:** raise the p95 warning to 4000ms (a
  cloud round-trip has its own non-trivial floor; align with
  your provider's published p95)
- **Heavy MoE on tight VRAM (`--n-cpu-moe N > 30`):** raise the
  p95 warning to 5000ms (CPU-offloaded experts are slower)

Apply changes by editing
`scripts/observability/palllm.alerts.yaml` in your fork and
reloading Prometheus.

## What the SLOs don't measure

These SLOs are **runtime-side**. They don't measure:

- **In-game delivery health** — the Lua bridge → PalLLM →
  outbox → Lua bridge → in-game HUD round trip. PalLLM's
  `/api/bridge/proof` endpoint is the canonical surface for that;
  the `scripts/run-native-proof.ps1` and
  `scripts/export-release-proof-bundle.ps1` tooling captures
  evidence. See [`IMPLEMENTATION_QUEUE.md`](IMPLEMENTATION_QUEUE.md)
  for the in-game delivery work that's gated on live Palworld
  sessions.
- **Pack-author experience** — pack validation latency,
  publication safety pass rate, etc. These are operator-facing,
  not runtime-facing.
- **Cost** — cloud-API spend isn't visible to PalLLM (the
  provider's billing surface owns this). Operators using the
  Pass 357 cloud-API escape path should set their provider's
  budget alerts in addition to PalLLM's SLOs.

## Related

- [`OBSERVABILITY.md`](OBSERVABILITY.md) — OTel + Prometheus
  wiring (the plumbing under these SLOs)
- [`HOT_PATH.md`](HOT_PATH.md) — per-method latency budgets that
  inform the Latency SLO
- [`SECURITY.md`](../SECURITY.md) — Pass 354 startup auth guard
  (a production-safety gate complementary to these SLOs)
- [`MINIMUM_REQUIREMENTS.md`](MINIMUM_REQUIREMENTS.md) —
  reference-rig spec (the hardware the Standard-tier SLO
  targets)
