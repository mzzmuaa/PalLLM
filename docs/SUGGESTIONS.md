# Runtime Suggestions — operator-actionable hint surface

Last audited: `2026-05-06`

The Suggestions[] surface is PalLLM's single source of truth for the
question **"what should the operator do right now?"**. It sits on
[`/api/health`](API.md), is consumed by every operator-facing entry
point in the project, and is the canonical extension point for
"surface a new actionable hint". This doc explains the design, lists
the live hint codes + consumer surfaces, and gives the concrete
recipe for adding a new code.

> **Related:** [`INVARIANTS.md`](INVARIANTS.md) for the load-bearing
> guarantees, [`COOKBOOK.md`](COOKBOOK.md) §10 for the recipe in
> alphabetical order alongside the other "how do I add X?" recipes.

## Why this surface exists

Before Pass 132, "is anything broken?" was scattered across:

- `OperatorHealthScore.TopReasons` (text strings, no commands)
- Doctor checks (PASS / WARN / FAIL with no programmatic codes)
- `pal next` advisor heuristics (one verdict, no detail)
- `pal status` output (counts only)
- The dashboard's various panels (each surface owned its own
  rendering)

A new operator who saw their companion replying with the default
voice had to know to check pack count. An operator whose Ollama
crashed had to read the circuit-breaker state in
`/api/health.InferenceCircuitState` and remember what `Open` meant.
There was no single place that said "no packs loaded — run
`pal pack copy companion-warrior`".

Pass 132 added one central abstraction; passes 133-142 fanned it out
to every consumer surface. Adding the **next** hint is now a single
edit in the builder.

## The data model

Three types live in
[`src/PalLLM.Domain/Integration/Contracts.cs`](../src/PalLLM.Domain/Integration/Contracts.cs)
and
[`src/PalLLM.Domain/Runtime/HealthSuggestionBuilder.cs`](../src/PalLLM.Domain/Runtime/HealthSuggestionBuilder.cs):

```csharp
// One actionable next-step hint. Carried inside RuntimeHealth.Suggestions[].
public sealed record HealthSuggestion(
    string Code,        // stable kebab-case (e.g. "no-packs-loaded")
    string Message,     // plain English, single sentence
    string? Command,    // optional copy-paste command; null when no single-shot fix
    string Severity);   // "info" | "warn" | "urgent"

// Inputs to the builder. A small record struct so callers don't have to
// pass the full RuntimeHealth and the test surface is explicit.
public readonly record struct HealthSuggestionInputs(
    int LoadedPackCount,
    bool InferenceConfigured,
    string InferenceCircuitState,
    long InferenceSuccessCount,
    long InferenceFailureCount,
    bool VisionEnabled,
    long VisionCallCount,
    long VisionFailureCount,
    bool TtsEnabled,
    long TtsCallCount,
    long TtsFailureCount,
    bool BridgeEnabled,
    long BridgeBootCount,
    long BridgeEventCount,
    int InboxPendingCount,
    int OutboxPendingCount,
    int FailedFileCount,
    int ScreenshotPendingCount,
    bool AutomationEnabled,
    int AutomationAllowedActionCount);

// Three buckets carried on every HealthSuggestion. Stable strings so
// consumers (dashboard CSS, pal-next colour switch, MCP-aware agents)
// switch on them without enums leaking through the wire.
public static class HealthSuggestionBuilder.Severity
{
    public const string Info   = "info";   // common operator state, not a failure
    public const string Warn   = "warn";   // mildly off; look at it within a session
    public const string Urgent = "urgent"; // active failure breaking chat
}
```

The builder is pure: same inputs always produce the same output, no
I/O, no global state. Every entry declares its own severity at
construction so consumers don't maintain a code-to-severity map.

## Ordering

The builder sorts entries **urgent > warn > info**, stable within
each bucket. Every consumer that takes `Suggestions[0]` therefore
gets the most pressing hint without picking a random one. A dashboard
card that only has room for one item surfaces the right thing. A
`pal next` advisor that prints in detection order prints the most
urgent first.

Stable secondary order means dashboards don't flap between polls
when the same set of hints fires — the order matches the detection
order in the builder source.

## Live hint codes (11)

Updated as of Pass 142. The count matches `suggestions.Add(...)` calls in
`HealthSuggestionBuilder.Build()`.

| # | Code | Severity | Trigger |
|---|---|---|---|
| 1 | `inference-circuit-open` | urgent | Inference configured + breaker `Open` |
| 2 | `inference-only-failures` | urgent | Inference configured + every recorded request failed |
| 3 | `vision-only-failures` | warn | Vision configured + every recorded request failed |
| 4 | `tts-only-failures` | warn | TTS configured + every recorded request failed |
| 5 | `outbox-backlog` | warn | Bridge outbox > 50 pending replies |
| 6 | `bridge-failed-files-accumulating` | warn | > 25 failed bridge envelopes |
| 7 | `bridge-inbox-backlog` | warn | Bridge inbox > 200 pending events |
| 8 | `automation-allowlist-empty-but-enabled` | warn | `Automation.Enabled=true` + `AllowedActions` empty |
| 9 | `bridge-idle` | info | Bridge enabled but never booted, no events |
| 10 | `no-packs-loaded` | info | `LoadedPackCount == 0` |
| 11 | `screenshots-pending-but-vision-disabled` | info | Watcher queueing files + Vision disabled |

Severity assignment follows design intent: codes that **break chat**
are urgent (the chat path falls through to fallback); codes that
**leave a lane dark but keep chat working** are warn; codes that
reflect **common operator states** (no packs yet, bridge not booted)
are info.

## Consumer surfaces (9)

The same `HealthSuggestion[]` flows through nine distinct entry
points. Adding a new hint code lights up all of them automatically
with no consumer-side edits.

| Audience | Surface | How it consumes |
|---|---|---|
| API caller | `curl /api/health` | Top-level `Suggestions` array in JSON |
| Operator (CLI) | `pal next` | "Runtime suggestions" section under the verdict, severity-coloured |
| Operator (CLI) | `pal doctor` | "Runtime suggestions" section after check table; urgent severity exits non-zero for CI parity |
| Operator (CLI) | `pal status` | One-line live count: `"3 signals — 1 urgent, 2 warn, 0 info"` |
| Operator (UI) | Field Console topbar badge | Colour-coded count chip visible from any scroll position |
| Operator (UI) | Field Console suggestions panel | Severity-coloured cards with one-click Copy on each command |
| Agent (MCP) | `pal_health_suggestions` | Returns the array directly |
| Agent (MCP) | `pal_status` | Compact summary with per-severity counts + top suggestion code |
| Agent (MCP) | `pal_health_score` | Companion: numeric score + grade (no suggestions, just the verdict) |

## How to add a new hint code

The whole point of the abstraction: **one edit lights up nine
surfaces.** Here's the recipe.

### 1. Add the trigger to `HealthSuggestionBuilder.Build`

Open
[`src/PalLLM.Domain/Runtime/HealthSuggestionBuilder.cs`](../src/PalLLM.Domain/Runtime/HealthSuggestionBuilder.cs).
Add a numbered comment block + an `if` guard + a `suggestions.Add(...)`
call. Pick the `Severity` based on impact:

- `Severity.Urgent` if the hint indicates an **active chat-breaking
  failure** (the chat path falls through to the fallback director).
- `Severity.Warn` if a **lane is dark but the chat path keeps
  working** (vision down but chat replies still text-render).
- `Severity.Info` if the hint reflects a **common operator state**
  that isn't a problem (haven't installed Palworld yet, no packs
  copied yet).

```csharp
// 12. <One-line description of what this signals.>
//
// <Two-three lines: why it matters, what the operator should do,
// any edge cases.>
if (inputs.SomeInput == thresholdCondition)
{
    suggestions.Add(new HealthSuggestion(
        Code: "kebab-case-hint-id",
        Message: "Plain English explanation of the situation. One sentence.",
        Command: "pal some-verb arg-here",  // null when no single-shot remediation
        Severity: Severity.Warn));
}
```

### 2. Add inputs if needed

If the trigger needs runtime state the inputs don't already carry, add
fields to the `HealthSuggestionInputs` record at the bottom of the
same file. Then update the call site in
[`src/PalLLM.Domain/Runtime/PalLlmRuntime.cs`](../src/PalLLM.Domain/Runtime/PalLlmRuntime.cs)
`BuildRuntimeHealth()` to pass the new field.

### 3. Add tests

Open
[`tests/PalLLM.Tests/HealthSuggestionBuilderTests.cs`](../tests/PalLLM.Tests/HealthSuggestionBuilderTests.cs).
Add a positive test (trip the trigger, assert the entry appears with
the right severity + message keyword) and a no-suggest test (the
trigger doesn't fire when the condition is false).

The fixture's `Healthy()` baseline + `with`-expression pattern lets
each test focus on one flipped field:

```csharp
[Test]
public void Build_When<Trigger>_Suggests<Code>_As<Severity>()
{
    HealthSuggestionInputs inputs = Healthy() with { SomeInput = triggerValue };
    IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

    HealthSuggestion entry = suggestions.First(s => s.Code == "kebab-case-hint-id");
    Assert.That(entry.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Warn));
    Assert.That(entry.Message, Does.Contain("expected keyword"));
}
```

### 4. Update this doc

Add a row to the **Live hint codes** table above. Bump the count in
the section heading (`11` → `12`).

### 5. Run the audit

```powershell
pwsh ./pal.ps1 audit
```

Test count drift gate will fire and require bumping the `tests` count
across the doc anchors. The OpenAPI snapshot stays unchanged
(`Suggestions[]` shape is the same).

That's it. **No edits to:**

- Dashboard HTML / CSS / JS (the rendering loop reads `entry.Code`,
  `entry.Severity`, `entry.Command` from the array)
- `pal next.ps1` (severity-aware coloring already switches on the
  field)
- `pal doctor.ps1` (urgent-exit gate already sums urgent severity)
- `pal status.ps1` (counts by severity bucket)
- MCP tools (`pal_health_suggestions`, `pal_status`,
  `pal_health_score` all read from the same `RuntimeHealth.Suggestions`)
- OpenAPI snapshot
- Schema files

**The leverage:** one builder edit reaches every operator and agent
surface in the project, with the right colour, ordering, and
copy-paste affordance.

## Pass history

The surface accumulated across these passes (newest first):

- **142** — One-click Copy on Suggestion command cards
- **141** — `pal_status` MCP tool + dashboard topbar badge
- **140** — `pal_health_score` MCP tool + doctor urgent-exit gate
- **139** — Severity-first ordering + `pal status` live summary
- **138** — KV-cache compression guidance (unrelated, not on this surface)
- **137** — Severity-aware CLI coloring + 2 new hint codes
- **136** — Severity field promoted to first-class on the record + 3 new codes
- **134** — Live runtime suggestions panel in the Field Console
- **133** — MCP tool `pal_health_suggestions` + doctor consumer
- **132** — Initial `Suggestions[]` field + builder + 6 base hint codes
- **130** — Pal next first introduces no-packs hint via inline Copy-Item
- **129** — Validator-backfill pass (the precursor that made startup
  failures visible enough to demand a runtime equivalent)

See [`../CHANGELOG.md`](../CHANGELOG.md) for the full per-pass detail.

## Related

- [`INVARIANTS.md`](INVARIANTS.md) — what's guaranteed to be true
  about the surface (e.g. `Suggestions[]` is always present, possibly
  empty, on `RuntimeHealth`)
- [`COOKBOOK.md`](COOKBOOK.md) — the alphabetical "how do I add X?"
  recipe index; this surface's recipe lives at §10
- [`API.md`](API.md) — the HTTP surface that exposes `Suggestions[]`
- [`MCP_QUICKSTART.md`](MCP_QUICKSTART.md) — how MCP-aware agents
  reach `pal_health_suggestions` / `pal_status` / `pal_health_score`
