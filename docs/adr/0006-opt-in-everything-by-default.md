# ADR 0006 — Opt-in everything by default

- **Status:** Accepted
- **Date:** 2026-04
- **Tags:** privacy, safety, defaults
- **Depends on:** [`0001`](0001-deterministic-first-reply-pipeline.md) (a fresh install can be useful with everything off only because deterministic fallback always works)
- **Supports:** the privacy posture surface (`/api/privacy/posture`) and `docs/PRIVACY.md`

## Context

PalLLM is local-first. The headline promise is "this companion runs
on your machine without phoning home, ever, unless you turn
something on." A subtle violation — telemetry, an analytics ping, a
"verify your install" call — would invalidate that promise for every
operator who took it on faith.

But the runtime has plenty of features that *do* talk to the
network: live inference (HTTP to Ollama / OpenAI-compatible
endpoints), vision (HTTP to Florence/CLIP), TTS (HTTP to
Coqui/Piper), action intents (mutates game state), screenshot
watcher (reads from disk), thermal gate (reads GPU temperature),
API-key auth (gates the HTTP surface), OTLP export (ships traces).

If any of these were on by default, an operator who installed PalLLM
expecting a quiet local companion could be surprised to find network
traffic — or, worse, an automated action sequence triggered by an LLM
output without the operator opting in.

## Decision

Every feature that emits network traffic, mutates game state, reads
sensitive local state, or could surprise an operator is **off by
default**. Each is a single config flag, and the docs name the flag
explicitly.

Features explicitly opt-in:

| Feature | Config flag | Default |
|---|---|---|
| Live inference | `PalLLM:Inference:Enabled` | `false` |
| Vision describe | `PalLLM:Vision:Enabled` | `false` |
| TTS synthesis | `PalLLM:Tts:Enabled` | `false` |
| Action intents (chat planning) | `PalLLM:Automation:Enabled` | `false` |
| Action executor (Lua executes intents) | (Lua-side allowlist) | `false` |
| Screenshot watcher | `PalLLM:Vision:EnableScreenshotWatcher` | `false` |
| Thermal gate | `PalLLM:Inference:ThermalGate:Enabled` | `false` |
| API-key auth | `PalLLM:Auth:ApiKey` | `null` (no auth) |
| OTLP export | `OTEL_EXPORTER_OTLP_ENDPOINT` env var | unset |
| MCP upstream proxy | `PalLLM:McpClient:UpstreamServers[]` | empty |

Features on by default — but **only** if they are local-only and
non-surprising:

| Feature | Why on by default |
|---|---|
| Bridge inbox/outbox | Local filesystem only; the runtime is useless without it |
| Self-healing watchdog | Local; archives orphan envelopes, no external calls |
| Promotion feeder | Local; reads metrics, writes to in-memory ledger |
| Memory store + autosave | Local; persists chat history under `runtime-root/session.json` |
| Deterministic fallback | Local; load-bearing per ADR 0001 |

## Alternatives considered

- **Opt-out with a "first-run wizard."** Tempting because it gets
  features in front of users faster. Rejected because it breaks the
  "quiet local companion" promise: the moment a wizard ships
  network traffic during onboarding, every privacy claim has a
  caveat.
- **Pre-baked profiles ("max-privacy", "max-features").** Hides the
  individual flags and makes operator audit harder. Per-feature
  flags map directly to per-feature confidence: an operator can
  read the privacy posture and see exactly which surfaces are
  active.
- **Detect and recommend.** "We notice you have a GPU, want to
  enable inference?" — a fine UX pattern for an opinionated
  product. Rejected here for the same reason as the wizard: any
  detection-and-recommend loop has to *make a network call* to
  recommend a model, breaking the opt-in promise.

## Consequences

**Positive:**
- The privacy posture (`GET /api/privacy/posture`) tells an honest
  story: every "active outbound" surface is something the operator
  explicitly turned on.
- A fresh install is provably quiet: zero outbound traffic, zero
  surprise. Doctor, smoke test, and live-probe scripts all confirm.
- The drift gate `Drift_Public_copy` checks that release-facing
  copy matches this stance — no doc that ships to a player
  describes a feature as "automatic" that actually requires
  opt-in.
- Tests run with all opts off by default; they explicitly enable
  what they need.

**Negative:**
- First-run feels lean. A new operator sees deterministic-only
  replies until they wire up an Ollama endpoint. The
  `/api/quickstart` route + the dashboard's first-run panel
  mitigate this with explicit "here's how to turn on inference"
  steps.
- More config knobs to document. Mitigated by:
  - Every option has an inline XML doc on the property
  - `docs/CONFIGURATION.md` lists every key with default + effect
  - `OPERATIONS.md` § "Enabling X" walks through each opt-in
  - The drift gate `Drift_Feature_catalog_count` ensures every
    operator-visible feature has a `FeatureDescriptor` entry

## Harvest hint

If you're harvesting any feature into a different project, check
its default. The pattern is "`Enabled = false` + an explicit
operator action turns it on." Copy that posture: a feature whose
side effects can surprise users should default off. The
`PalLlmOptions` class
(`src/PalLLM.Domain/Configuration/PalLlmOptions.cs`) is the
single-file canonical source of every default.

## Related

- Code: `src/PalLLM.Domain/Configuration/PalLlmOptions.cs`
- Docs: [`PRIVACY.md`](../PRIVACY.md), [`OPERATIONS.md`](../OPERATIONS.md)
  § "Opt-in feature matrix", [`DESIGN_PRINCIPLES.md`](../DESIGN_PRINCIPLES.md)
  § 3 ("Local-first"), [`SECURITY.md`](../../SECURITY.md)
