# Your first hour with PalLLM - guided tour

Last audited: `2026-05-23`

A 60-minute self-directed walk-through that takes you from "fresh
clone, never touched the code" to "comfortable shipping a small
change." Aimed at someone who's already past the
[`PITCH.md`](PITCH.md) elevator pitch and wants the *feel* of the
codebase, not just facts about it.

If you just want to talk to PalLLM without coding, read
[`QUICKSTART.md`](QUICKSTART.md) instead - that's the 5-minute
path. This doc is the *coding* on-ramp.

## Before you start

Have these on your machine:

- .NET 10 SDK (`dotnet --version` should print `10.x`)
- PowerShell 5.1+ (Windows native) or PowerShell 7+ (cross-platform)
- A code editor that consumes `.editorconfig` (any modern one)
- Optional: `make` for the cross-platform shorthand

Fresh clone:

```powershell
git clone <repo-url> PalLLM
cd PalLLM
```

## Minute 0-5: the one-command setup

Run:

```powershell
pwsh ./pal.ps1 onboard
```

This walks five steps end-to-end: SDK check -> build -> 1308-test suite
-> drift audit -> boots the sidecar in a window -> opens
http://localhost:5088/ in your default browser.

If the dashboard loaded with a green health pill, you're ready. If
it didn't, the script's last printed line names the failure; jump
to [`RUNBOOK.md`](RUNBOOK.md) "Sidecar won't start".

## Minute 5-10: the rolling baseline

Once the dashboard is up, in another window:

```powershell
pwsh ./pal.ps1 status
```

You'll see the project's rolling baseline: tests, drift gates,
build warnings, route count, feature catalog count, ADR count,
honest roadmap percentage. Memorize the shape: every PR-worthy
change you make will move at least one of these numbers, and the
audit verifies the docs match.

If you'll be picking up the codebase as an AI agent, instead try:

```powershell
pwsh ./pal.ps1 context | ConvertFrom-Json | Format-List
```

That emits the same baseline plus the ADR inventory, schema
inventory, and doc-freshness map as a single JSON document - one
read, full session state.

## Minute 10-25: build the right mental model

Open three docs in three tabs:

1. [`MENTAL_MODEL.md`](MENTAL_MODEL.md) - ten paragraphs with
   analogies. Read straight through. Don't try to memorize; just
   absorb the *shape*.
2. [`ARCHITECTURE.md`](ARCHITECTURE.md) "System at a glance" -
   the Mermaid diagram showing the four shells (Game / Bridge /
   Sidecar / Domain) plus the opt-in dependencies.
3. [`adr/`](adr/) - skim the six ADR titles. Open ADR 0001
   (deterministic-first reply pipeline) and read its
   "Decision" + "Consequences" sections. That's the load-bearing
   choice everything else hangs from.

By the end of this slice, you should be able to answer:

- Where does the chat path start? (`PalLlmRuntime.ChatAsync`)
- What happens if inference is unreachable? (Deterministic
  fallback director runs; the chat turn still produces a
  reply.)
- How does the sidecar talk to Palworld? (One-way through the
  filesystem, in `runtime-root/Bridge/Inbox/` and
  `Bridge/Outbox/`.)
- What's the seam to lift the runtime into another game?
  (Implement the five interfaces in
  `Portable/PortableAdapterContracts.cs`.)

If any of those don't have an obvious answer, re-read the relevant
ADR.

## Minute 25-40: explore the running runtime

The sidecar is still running from minute 0. Hit some endpoints:

```powershell
# Health check - what does the runtime think is going on?
curl -s http://localhost:5088/api/health | ConvertFrom-Json | Format-List

# Self-description - "what is this server?" in one read
curl -s http://localhost:5088/api/describe | ConvertFrom-Json | Format-List

# State-aware "what should I do next?" guide
curl -s http://localhost:5088/api/quickstart | ConvertFrom-Json | Format-List

# Privacy posture - what's currently emitting traffic?
curl -s http://localhost:5088/api/privacy/posture | ConvertFrom-Json | Format-List

# Now have an actual chat
$body = @{ userMessage = "hi"; characterId = 1 } | ConvertTo-Json
curl -s -X POST http://localhost:5088/api/chat -H "Content-Type: application/json" -d $body |
    ConvertFrom-Json | Format-List
```

Notice the chat reply's `ResponsePath` field - it's
`fallback-after-inference-disabled` because you haven't wired up
an inference endpoint. The deterministic director produced the
reply.

If you have a llama-server (the default) or vLLM running locally,
set `PalLLM__Inference__Enabled = true` and
`PalLLM__Inference__BaseUrl` (see
[`ENV_VARS.md`](ENV_VARS.md)) and try again. The same chat turn
should now show `ResponsePath: inference-completed`.

Open the dashboard at http://localhost:5088/ and click around.
Each panel maps to a `/api/*` route - the chat panel uses
`/api/chat`, the diagnostics panel uses `/api/diagnostics`, etc.

## Minute 40-50: poke at the code

Time for code, not docs. Open in your editor:

1. **`src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`** - find
   `ChatAsync`. Read the top 50 lines of the method. Notice the
   inline comments explaining each step.
2. **`src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs`** -
   pick any `Try_*` method. Notice each strategy is a
   pattern-match function returning a `FallbackResult` or null.
3. **`src/PalLLM.Sidecar/Program.cs`** - search for `MapPost("/chat"`.
   That's the HTTP entry point. Trace it back to `runtime.ChatAsync`.
4. **`tests/PalLLM.Tests/RuntimeTests.cs`** - open and skim a
   `ChatAsync_*` test. Notice it uses `SidecarTestFixture`
   (the canonical fixture wiring, in the same directory).

Don't try to understand everything. The point is to feel the
shape: most files are small, well-named, and inline-documented.

## Minute 50-60: ship a tiny change

Pick a small change to make sure your end-to-end loop works.
Suggestion: bump a one-liner doc.

```powershell
# Edit any doc - let's add a comma to docs/CHEAT_SHEET.md
# Then run the audit:
pwsh ./pal.ps1 audit
```

If the audit passes, your change is structurally sound. If it
fails, the report names the gate; fix the issue and re-run.

For a code change, try:

```powershell
# Scaffold a new advisor (placeholder files only - never edits existing)
pwsh ./pal.ps1 scaffold advisor MyFirst

# Read the new files (paths are MyFirstAdvisor.cs and MyFirstAdvisorTests.cs
# under src/PalLLM.Domain/Runtime and tests/PalLLM.Tests respectively)

# Both have TODO markers showing where to fill in logic.
# Once you fill them in:
pwsh ./pal.ps1 build      # zero warnings expected
pwsh ./pal.ps1 test       # 1308+ tests now (you added one)
pwsh ./pal.ps1 audit      # 16 / 16 still expected (after bumping count refs)

# To clean up:
git restore .             # if this is a git checkout
pwsh ./pal.ps1 cleanup    # preview generated clutter
pwsh ./pal.ps1 cleanup -Apply
```

You've now done the full loop: scaffold -> fill -> build -> test ->
audit. Every meaningful change in this repo follows that shape.

## What to read next

Pick one based on your interest:

- **Want to add a real feature?** [`COOKBOOK.md`](COOKBOOK.md) +
  [`EXTENSION_POINTS.md`](EXTENSION_POINTS.md).
- **Want to lift a piece into another project?**
  [`HARVEST.md`](HARVEST.md).
- **Want to understand why every choice was made?**
  [`adr/`](adr/) (six accepted ADRs).
- **Want to know what NOT to do?**
  [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md).
- **Want the operator's perspective?**
  [`OPERATIONS.md`](OPERATIONS.md).

When you're done coding for the session, update
[`HANDOFF.md`](HANDOFF.md) "What just landed" with a one-line
bullet. The next person (or your next session) reads that file
first.

## If you got stuck

- The sidecar wouldn't start -> [`RUNBOOK.md`](RUNBOOK.md)
- A drift gate failed -> the audit report names the gate; the
  log explains the mismatch
- A test failed -> [`TESTING.md`](TESTING.md) covers the patterns
- The architecture did not click ->
  [`MENTAL_MODEL.md`](MENTAL_MODEL.md) is the "right analogy"
  doc

## What you know now

After 60 minutes:

- [x] The runtime is running and you've talked to it
- [x] You understand the deterministic-first reply pipeline
- [x] You understand why the bridge is a filesystem mailbox
- [x] You can run build / test / audit / status / scaffold
- [x] You've made a change and seen the gates respond
- [x] You know which doc answers which question

That's the on-ramp. Welcome to PalLLM.
