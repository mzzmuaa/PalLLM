# PalLLM Privacy

Last audited: `2026-05-07`

This is the complete inventory of every data-emitting surface PalLLM
ships, classified as **never-leaves**, **only-with-opt-in**, or
**leaves-by-default**. The same data is available in machine-readable
form at `GET /api/privacy/posture` and via the `pal_privacy_posture`
MCP tool, so the public copy here can't drift without the test suite
catching it.

## TL;DR

- **Default install is fully local.** Zero outbound network traffic
  unless you configure a live inference, vision, or TTS endpoint, or
  set the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable.
- **No telemetry, no crash reports, no update checks.** PalLLM never
  phones home.
- **Every outbound surface is explicitly opt-in.** Config keys are
  documented below; the machine-readable `/api/privacy/posture`
  surface shows which are currently active.

## The three categories

| Status | Meaning |
| --- | --- |
| `never-leaves` | Data stays in the process or on local disk. No network transmit path exists. |
| `only-with-opt-in` | An operator config key must be set to `true` (or a value) before data leaves the machine. |
| `leaves-by-default` | An operator has already turned the opt-in on. Shown per-install so you can see what's active. |

## Surface inventory

### Always local (never-leaves)

| Surface | Category | Notes |
| --- | --- | --- |
| `conversation-memory` | memory | `ConversationMemoryStore` lives in-process. `PalLLM:Session:Enabled` persists it to `runtime-root/session.json` - still local. |
| `relationship-tracker` | memory | Per-character affection/trust/stress. In-process + local-disk only. |
| `dashboard` | http | Field Console served from `wwwroot/` on the sidecar's bound addresses. The shipped launchers default to `http://localhost:5088`. |
| `health-probes` | http | `/health/live`, `/health/ready`, `/metrics`, `/openapi/v1.*` follow the sidecar's bound addresses and default to localhost in the shipped launchers. |
| `proof-packets` | evidence | Proof packets, release readiness snapshots, and support bundles are local files only. |
| `promotion-staging` | evidence | Pass-24 apply verb writes to `PalLLM:PromotionApply:StagingRoot` only. Gated by `AllowApply=false`. |
| `deterministic-fallback` | inference | `FallbackBehaviorEngine` replies are composed from local templates. No network. |
| `crash-reports` | telemetry | `SelfHealingWorker` writes local evidence under `Runtime/SelfHealing/`. No remote. |
| `update-check` | network | PalLLM does not automatically check for updates. |
| `analytics` | telemetry | No product analytics. `/metrics` is localhost-only Prometheus. |

### Opt-in only (off by default)

| Surface | Controlled by | What happens when you enable it |
| --- | --- | --- |
| `live-inference` | `PalLLM:Inference:Enabled` + `PalLLM:Inference:BaseUrl` | Chat turns POST to your configured endpoint. You pick the endpoint (Ollama loopback, private LAN, or a remote URL). |
| `vision-describe` | `PalLLM:Vision:Enabled` + `PalLLM:Vision:BaseUrl` | Screenshots are POSTed to your configured vision endpoint. Default off. |
| `tts-synthesis` | `PalLLM:Tts:Enabled` + `PalLLM:Tts:BaseUrl` | Text is POSTed to your configured TTS endpoint. Audio is played locally. Speech playback receipts record compact status, artifact byte counts, WAV or raw-PCM encoding/sample-format/byte-order/mixer-conversion/mixer-queue/mixer-buffer-duration/sample-rate/channel/bit-depth/duration/byte-rate/block-align/audio-data-size/sample-frame/partial-frame/valid-bits/channel-mask metadata when available, playback sequence, superseded request id/count/age/prior-buffer/estimated-remaining-buffer, cancellation mode, launch attempt counts, and elapsed milliseconds only; they do not store audio bytes, generated text, or local file paths. |
| `otlp-telemetry` | `OTEL_EXPORTER_OTLP_ENDPOINT` env-var | OpenTelemetry traces, metrics, and logs are sent to the configured OTLP collector. |
| `upstream-mcp` | `PalLLM:McpClient:UpstreamServers[]` | PalLLM connects to the upstream MCP servers you list. Each URL is explicit. |
| `narrative-pack-loading` | hand-copy files into `runtime-root/Packs/` | Narrative packs are loaded from disk. Sharing a pack is hand-copy only. |

## How to prove it

```bash
# Run the local sidecar
sidecar\publish\PalLLM.Sidecar.exe

# Ask for the posture
curl http://localhost:5088/api/privacy/posture

# Confirm endpoint network scope (loopback / private-lan / public-internet)
curl http://localhost:5088/api/airgap/verify
```

Or via MCP from Claude Desktop / VS Code / Cursor:

```
@palllm pal_privacy_posture
@palllm pal_airgap_verify
```

## Privacy deltas from v1.0.0 baseline

| v1.0.0 | Latest (post-Pass-42) | Delta |
| --- | --- | --- |
| Airgap verifier surface | + Privacy posture surface (Pass 27) | + machine-readable data-flow inventory |
| Dashboard chip: airgap | + privacy chip (planned) | + per-surface drill-down |
| Docs scattered across OPERATIONS.md | + dedicated `PRIVACY.md` (Pass 27) | + single authoritative inventory |
| No release-verification helper | + `SHA256SUMS` + `checksums.json` (Pass 41) | + local-verifiable downloads without key infra |
| No compat-disclosure file | + `COMPATIBILITY.md` (Pass 42) | + players can see known-good / known-bad combos before installing |

No surface added in Passes 28-42 sends new network traffic by default.
The privacy posture surface covers every data-emitting path added since
v1.0.0; `/api/privacy/posture` is the authoritative runtime view.

## What is intentionally NOT done

- **No remote logging destination.** PalLLM's structured logs stay in
  the ASP.NET Core host's default sinks (stdout/stderr + file when
  configured). Nothing is forwarded to a third-party log aggregator.
- **No encrypted outbound transport is added on top of what the
  operator configures.** If you send to a loopback HTTP endpoint, it's
  cleartext. If you point at an `https://` endpoint, .NET's standard
  TLS stack applies. PalLLM doesn't add any opinionated transport
  layer of its own.
- **No "anonymous telemetry" mode.** There is no "help us improve"
  opt-in. There is no quiet opt-out. The closest thing is the local
  `/metrics` Prometheus surface, which the operator chooses whether to
  expose.

## Reporting a privacy concern

Open an issue with the `security` label, or see
[`SECURITY.md`](../SECURITY.md) for responsible-disclosure guidance.
