# PalLLM Code Conventions

Last audited: `2026-05-10`

A small set of patterns every contribution should follow. Kept short
on purpose — if a rule needs a whole section to justify, it's
probably the wrong rule.

## The four patterns to recognise

### 1. Advisor pattern

An advisor is a **pure function** that takes a snapshot-ish input
and returns a structured verdict record. No side effects, no
inference calls, safe on hot paths.

```csharp
public static class XxxAdvisor
{
    public static XxxAdvisory Advise(SomeSnapshot snapshot, /* optional */ DateTimeOffset? now = null)
    {
        // ... pure logic ...
        return new XxxAdvisory(...);
    }
}

public sealed record XxxAdvisory(/* ... */);
```

**Examples:**
`WorldNarrationAdvisor`, `MoodWeatherAdvisor`, `GracefulDegradationAdvisor`,
`DisagreementDetector`, `WhyEngine`, `ChatDispatchPlanner`.

**Surfacing:** one `GET /api/xxx` or `POST /api/xxx` endpoint, one
`pal_xxx` MCP tool, sometimes a dashboard chip. Every advisor has a
regression test fixture `XxxAdvisorTests.cs`.

### 2. Builder pattern

A builder is a **pure function** that composes inputs into an
immutable snapshot record. Sibling to the advisor pattern; the name
`Builder` (vs `Advisor`) signals "this assembles a structure" rather
than "this gives advice."

```csharp
public static class XxxBuilder
{
    public static XxxPosture Capture(SomeOptions options, SomeMetrics metrics)
    {
        // ... compose ...
        return new XxxPosture(...);
    }
}
```

**Examples:**
`PromotionApplyPreviewBuilder`, `PrivacyPostureBuilder`,
`ResourceBudgetPostureBuilder`, `ProofPacketBuilder`.

### 3. Validator pattern

A validator takes a thing-to-validate and returns a structured
result with per-check status and a list of issues. **Never throws**
on validation failure — the result records the problem.

```csharp
public static class XxxValidator
{
    public static XxxValidationResult Validate(string inputPath)
    {
        var issues = new List<string>();
        var checks = new List<XxxCheck>();
        // ... populate ...
        return new XxxValidationResult(IsValid: issues.Count == 0, Checks: checks, Issues: issues);
    }
}
```

**Examples:**
`NarrativePackValidator`, `PersonalityPackValidator`.

### 4. Feeder pattern

A feeder is a background worker that **observes** metrics or state
and writes bounded records. Never mutates the thing it observes.
Implements `IHostedService`.

```csharp
internal sealed class XxxFeeder : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // read counters
            // diff against prior tick
            // write observations
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }
}
```

**Examples:** `PromotionLedgerFeeder`, `SelfHealingWorker`.

## The three hard rules

1. **Deterministic-first.** Every new capability lands as a pure
   function first. THEN gets an HTTP + MCP + dashboard surface
   layered on. Never the other way around.
2. **Observer-only, never destructive.** Feeders and watchdogs
   write to bounded in-memory stores or evidence files; never
   mutate `ChatAsync`, never restart the sidecar, never delete a
   file. Destructive recovery stays explicitly with `recover.bat`.
3. **Every automated change gets a proof packet.** Use
   `ProofPacketBuilder.Build(...)` for the provenance record. The
   proof packet has a stable SHA-256 id so auditors can
   cross-reference.

## Type naming

| Suffix | Meaning | Example |
|---|---|---|
| `Options` | DI-bound config record (`IOptions<T>` shape) | `PromotionApplyOptions` |
| `Snapshot` | Immutable point-in-time record | `GameWorldSnapshot` |
| `Advisory` / `Cue` / `Decision` / `Posture` | Advisor output | `DegradationAdvisory`, `NarrationCue` |
| `Packet` | Provenance + audit record | `ProofPacket` |
| `Builder` | Static type with a `Build(...)` or `Capture(...)` method | `ResourceBudgetPostureBuilder` |
| `Advisor` | Static type with an `Advise(...)` or `Forecast(...)` / `Decide(...)` method | `MoodWeatherAdvisor` |
| `Validator` | Static type with a `Validate(...)` method | `PersonalityPackValidator` |
| `Feeder` | `BackgroundService` implementation | `PromotionLedgerFeeder` |
| `Tracker` | Stateful per-key counter/histogram | `InferencePerformanceTracker` |
| `Store` | In-memory collection with persistence hook | `ConversationMemoryStore` |

Follow these suffixes so `CODE_MAP.md` stays greppable and the drift
gates stay predictable.

## File-header convention

Every `.cs` file under `src/` starts with a doc-comment on the
**first public type**, not a file-level header comment. This keeps
the file grep-friendly:

```csharp
using System;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Something;

/// <summary>
/// One-paragraph explanation of what this type is and why it exists.
/// Mention the surfacing (endpoint + MCP tool) if applicable.
///
/// <para>If the type is an advisor/builder/validator/feeder,
/// explicitly say so and link to the pattern in docs/CONVENTIONS.md.</para>
/// </summary>
public static class SomethingAdvisor
{
    // ...
}
```

We don't use file-top copyright headers — the MIT licence in
[`../LICENSE`](../LICENSE) + `THIRD_PARTY_NOTICES.md` is sufficient
and the audit pipeline explicitly does not check for them.

## Error handling

- Sidecar endpoints return `ProblemDetails` on 4xx / 5xx. Use
  `Results.Problem(statusCode: ..., title: ..., detail: ...)` so
  the body shape stays consistent.
- Deterministic advisors never throw. On bad input they return an
  advisory with a clear reason field — e.g.
  `NarrationCue { ShouldNarrate=false, Trigger="world-not-loaded", Reason="..." }`.
- Live-inference paths catch exceptions, record in
  `InferencePerformanceTracker`, and let the deterministic fallback
  answer. Never bubble an upstream model exception to the user.

## Async

- Use `async Task` / `async Task<T>`. Return `ValueTask` only when
  profiling shows the allocation matters.
- `CancellationToken` flows through every `async` public method.
  Name it `cancellationToken`, not `ct`.
- Hot-path loops that poll status should respect cancellation every
  iteration.

## JSON + serialization

- Source-generated `System.Text.Json` via
  `src/PalLLM.Sidecar/PalLlmJsonSerializerContext.cs` for HTTP/MCP payloads
  and `src/PalLLM.Domain/PalLlmDomainJsonSerializerContext.cs` for portable
  bridge, pack, persistence, proof, and opt-in transport bodies. Add every new
  wire type to the matching context or the packaged single-file EXE can fail at
  runtime.
- Domain hot paths should use `JsonTypeInfo` overloads, not reflection fallback
  or anonymous `JsonContent.Create(...)` bodies.
- Wire types use **PascalCase** property names. PalLLM deliberately
  does not apply camelCase naming policy because Lua consumers want
  PascalCase to match Unreal convention.

## Testing

- One `XxxTests.cs` file per subsystem under `tests/PalLLM.Tests/`.
- NUnit 4.x. Use `[Test]` attributes, not TestMethod / Fact.
- Integration tests that boot an in-process sidecar live in
  `SidecarEndpointTests.cs` and derivatives.
- Default fixture has `Inference.Enabled = false`,
  `Vision.Enabled = false`, `Tts.Enabled = false`. This is
  **deliberate**: the deterministic fallback path is what every
  player hits by default, and it's what CI exercises.

## Feature catalog

Every user-visible capability needs a `FeatureDescriptor` entry in
`src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs`.

```csharp
new FeatureDescriptor
{
    Id = "my-new-feature",             // kebab-case, stable
    Source = "PalLLM runtime",         // or "PalLLM bridge + UE4SS ..."
    Status = "ready",                   // ready | scaffolded | deferred
    Summary = "One-liner for operators.",
    Notes = "Implementation details, links to related patterns.",
}
```

The drift gate enforces count agreement between this file and the docs.

## HTTP routes

- Register in `src/PalLLM.Sidecar/Program.cs`. Do NOT create
  controllers — the minimal-API style is deliberate for drift
  counting.
- Heavy local-work lanes must use both admission control and a bounded
  endpoint timeout: `.RequireRateLimiting("chat-heavy")` /
  `"vision-heavy"` / `"tts-heavy"` plus `.WithRequestTimeout(...)` with the
  matching named policy for one-shot routes. If the route is SSE or another
  already-started stream, run the expensive work under the same configured
  budget with a linked cancellation token and emit a sanitized stream-level
  error event instead of trying to rewrite the status code after headers flush.
- `/api/*` and `/mcp` JSON request bodies are already capped by
  `PalLLM:Http:ApiRequestBodyMaxBytes` middleware before model binding. New
  POST lanes still need field-level validation for their own semantic caps.
- Document the request shape on the method signature.
- Add a row to [`API.md`](API.md) "Surface at a glance" in the same
  PR.

## MCP tools

- One `[McpServerTool(Name = "pal_xxx")]`-attributed method per
  tool in `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs`.
- Tool names are `pal_` + snake_case.
- Each tool returns `JsonSerializer.Serialize(result, JsonOptions)`
  — no raw strings, no custom formats.

## Dashboard

- Static files in `src/PalLLM.Sidecar/wwwroot/`.
- No build step, no bundler. Vanilla HTML + CSS + ES modules.
- New panels follow the existing `<section id="xxx">` + `#xxx-form`
  / `#xxx-history` id convention so the CSS generalises.

## Scripts

- PowerShell 5.1 compatible (Windows default).
- Script headers document `Usage`, `Flags`, and `Exit code`.
- Scripts that write artifacts do so under
  `{repoRoot}/artifacts/{category}/{timestamp}/` for reproducibility.

## When in doubt

- Look at the closest existing sibling and mirror its shape.
- The drift audit will tell you what you missed.
- `docs/HANDOFF.md` and `docs/CODE_MAP.md` are the two most-visited
  docs — update them when your change affects them.
