# Cookbook - recipes for common changes

Last audited: `2026-05-22`

Step-by-step recipes for the changes you'll most often want to
make to PalLLM. Each recipe names the exact files, the exact
sections inside them, and the exact tests you need to add. Treat
this as the "how do I add X?" reference; treat
[`EXTENSION_POINTS.md`](EXTENSION_POINTS.md) as the "where in the
file does the change go?" map.

If a recipe drifts from reality (a file moved, a count drift gate
catches you), this doc is wrong - the code wins, and please bump
the recipe in your PR.

## Index

| I want to add a... | Recipe |
|---|---|
| New `/api/*` endpoint | [Section 1 New HTTP endpoint](#1-new-http-endpoint) |
| New deterministic fallback strategy | [Section 2 New fallback strategy](#2-new-fallback-strategy) |
| New MCP tool | [Section 3 New MCP tool](#3-new-mcp-tool) |
| New `PalLlmOptions` config flag | [Section 4 New config flag](#4-new-config-flag) |
| New advisor / builder / validator | [Section 5 New advisor or builder](#5-new-advisor-or-builder) |
| New bridge event type | [Section 6 New bridge event type](#6-new-bridge-event-type) |
| New guarded action type | [Section 7 New guarded action type](#7-new-guarded-action-type) |
| New feature-catalog entry | [Section 8 New feature-catalog entry](#8-new-feature-catalog-entry) |
| New ADR | [Section 9 New ADR](#9-new-adr) |
| New runtime-suggestion hint code | [`SUGGESTIONS.md` "How to add a new hint code"](SUGGESTIONS.md#how-to-add-a-new-hint-code) |

## Universal pre-flight

Before any recipe:

```powershell
pwsh ./pal.ps1 fast-audit   # confirm 16 / 16 gates green
pwsh ./pal.ps1 test         # confirm 1154 / 1154 tests
```

If either is red, fix that *first*. Don't layer changes on a red
baseline.

## 1. New HTTP endpoint

Goal: add `GET /api/example/posture` returning a structured
posture document.

**Files to touch**:

1. `src/PalLLM.Sidecar/Program.cs` - register the route.
   - Find the `// Inspection` (or relevant subsystem) block and
     add `api.MapGet("/example/posture", ...)`.
   - Use `IResult` return type and `TypedResults.Ok(...)`.
   - Decorate with `.WithName("GetExamplePosture")`,
     `.WithTags(...)`, `.WithSummary(...)`.
2. `src/PalLLM.Domain/Integration/Contracts.cs` - define the
   response shape as a sealed record.
3. New file `src/PalLLM.Domain/Runtime/<YourSurface>PostureBuilder.cs`
   - pure deterministic builder following the pattern in
   [`CONVENTIONS.md`](CONVENTIONS.md). Existing reference:
   `PrivacyPostureBuilder.cs`.
4. `tests/PalLLM.Tests/SidecarEndpointTests.cs` - add an HTTP
   integration test that hits the route and asserts the shape.
5. New file `tests/PalLLM.Tests/<YourSurface>PostureBuilderTests.cs`
   - pure-logic tests for the builder. Existing reference:
   `PrivacyPostureBuilderTests.cs`.
6. `docs/API.md` - add the route to the reference list.
7. `docs/openapi/palllm-sidecar-v1.json` - regenerate via
   `pwsh ./pal.ps1 openapi`.
8. `README.md` + `docs/ROADMAP.md` + `docs/ARCHITECTURE.md` -
   bump the route count.
9. `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` - add
   a `FeatureDescriptor` entry so the dashboard surfaces it.
10. `docs/CODE_MAP.md` - add a row under the relevant subsystem.

**Drift gates that will fire if you skip a step**:

- `Drift_Api_route_count` - counts in README / ROADMAP /
  ARCHITECTURE / API.md disagree with `Program.cs`
- `Drift_OpenApi_snapshot` - the committed snapshot is stale
- `Drift_Feature_catalog_count` - feature count drifted

**Verify**:

```powershell
pwsh ./pal.ps1 build
pwsh ./pal.ps1 test
pwsh ./pal.ps1 openapi      # regenerates the snapshot in place
pwsh ./pal.ps1 audit        # all 16 gates
curl -s http://localhost:5088/api/example/posture | ConvertFrom-Json
```

## 2. New fallback strategy

Goal: add `Try_<MyStrategy>` to the deterministic director with a
matching presentation cue.

**Files to touch**:

1. `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs` - add a
   `private static FallbackResult? Try_MyStrategy(...)` method.
   The method either returns a populated `FallbackResult` or
   `null` to indicate "doesn't match this turn".
2. `src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs` - add a
   case in the family-mapping switch so the new strategy renders
   coherent visual + audio cues. Per ADR 0001, every strategy
   must have a paired cue.
3. `tests/PalLLM.Tests/RuntimeTests.cs` (or a focused new file)
   - add a test that exercises the strategy via `ChatAsync` and
   asserts `ResponsePath` includes the strategy name.
4. `docs/ROADMAP.md` - bump the strategy count.

**Drift gates that will fire if you skip a step**:

- `Drift_Fallback_strategy_count` - `Try*` method count vs
  ROADMAP

**Verify**:

```powershell
pwsh ./pal.ps1 audit
```

The audit's strategy-count check parses the `Try*` methods and
the `CreateGeneralDirector` factory and asserts the total
matches `19` (or `20` after your addition) in `ROADMAP.md`.

## 3. New MCP tool

Goal: add an MCP tool that wraps an existing HTTP endpoint or
a domain method, surfaced through the `/mcp` JSON-RPC server.

**Files to touch**:

1. `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs` - add a tool
   method:
   ```csharp
   [McpTool(name: "pal_my_thing", description: "...")]
   public async Task<...> PalMyThing(...) { ... }
   ```
2. `tests/PalLLM.Tests/McpEndpointTests.cs` - add a test that
   sends a `tools/call` JSON-RPC request and asserts the
   response shape.
3. `docs/API.md` - add the tool to the MCP-tool inventory.
4. `README.md` + `docs/ARCHITECTURE.md` - bump the MCP tool
   count (currently `35`).
5. `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` - add a
   `FeatureDescriptor` entry if the tool is observably new
   functionality (not just a wrapper).

**Drift gates that will fire**:

- `Drift_OpenApi_snapshot` - the MCP tool list shows up in the
  generated OpenAPI snapshot (via the JSON-RPC schema)
- `Drift_Feature_catalog_count` - if you forgot the catalog
  entry

**Verify**:

```powershell
pwsh ./pal.ps1 test  # the McpEndpointTests will exercise the tool
pwsh ./pal.ps1 audit
```

## 4. New config flag

Goal: add `PalLLM:Foo:NewKnob = 42` to `PalLlmOptions`.

**Files to touch**:

1. `src/PalLLM.Domain/Configuration/PalLlmOptions.cs` - add the
   property to the relevant sub-options class with an XML
   `<summary>` describing the default + effect.
2. The consumer code that reads the new knob.
3. The relevant test fixture(s) - set the knob in setup if your
   tests rely on a non-default value.
4. `docs/TUNING.md` - add a row to the parameter table:
   default, min/max, what-if-too-low, what-if-too-high, how to
   test.
5. `docs/OPERATIONS.md` - if the knob enables / disables a
   subsystem, add it to the "Opt-in feature matrix" + the
   per-feature enable subsection.
6. `docs/PRIVACY.md` - if the knob affects whether the surface
   emits network traffic, mark it accordingly.

**Drift gates that will fire**:

- None directly (config flags don't have a count gate). But the
  `Drift_Path_references` gate will catch any new
  documentation reference to a non-existent file.

## 5. New advisor or builder

Goal: add a new pure-deterministic posture surface like
`MoodWeatherAdvisor` or `PrivacyPostureBuilder`.

**Files to touch**:

1. New file `src/PalLLM.Domain/Runtime/<YourName>Advisor.cs`.
   Choose one of the four patterns from
   [`CONVENTIONS.md`](CONVENTIONS.md) - advisor / builder /
   validator / feeder. Static class, single public method,
   pure function, returns a sealed record. Existing reference:
   `MoodWeatherAdvisor.cs`.
2. If the surface is read-heavy and called from polling
   contexts, follow ADR 0005 and add a `*Cached` companion
   with signature-based invalidation.
3. The HTTP endpoint that exposes it - see recipe Section 1.
4. The MCP tool wrapper - see recipe Section 3.
5. New file `tests/PalLLM.Tests/<YourName>AdvisorTests.cs` - pure-logic tests. Existing reference: `MoodWeatherAdvisorTests.cs`.
6. `docs/ADVISORS.md` - add a row with file path, public
   surface, kind (Pure / Stateful / Cached), and surfacing
   (HTTP route + MCP tool).
7. `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` -
   `FeatureDescriptor` entry.

**Drift gates that will fire**:

- `Drift_Feature_catalog_count`, `Drift_Api_route_count`,
  `Drift_OpenApi_snapshot` - same as recipe Section 1.

## 6. New bridge event type

Goal: add a new event the Lua bridge can write to
`Bridge/Inbox/`.

**Files to touch**:

1. `src/PalLLM.Domain/Runtime/PalLlmRuntime.cs` -
   `ProcessBridgeEvent` (or its dispatch table) handles
   the new type. Add a case.
2. `mod/ue4ss/Mods/PalLLM/Scripts/main.lua` - add a producer
   helper that writes the new event type with the right
   payload shape.
3. `docs/schemas/bridge-event-envelope.schema.json` - add
   the new payload's `oneOf` branch under `$defs`.
4. `tests/PalLLM.Tests/RuntimeTests.cs` (or
   `BridgeInboxWorkerTests` if it exists) - add a
   `DrainInbox_<NewEventType>` test that writes a fixture
   envelope and asserts the runtime processed it correctly.

**Drift gates that will fire**:

- `Drift_Path_references` - if the schema link is wrong.
- `Drift_Dangling_markdown_links` - same.

## 7. New guarded action type

Goal: add an action type the runtime can emit through the
outbox and the Lua bridge can execute (subject to its
allowlist).

**Files to touch**:

1. `src/PalLLM.Domain/Runtime/ActionIntentPlanner.cs` - add the
   `(type, args, priority, justification)` mapping.
2. `mod/ue4ss/Mods/PalLLM/Scripts/main.lua` - add a handler
   mirroring `execute_waypoint_suggest` /
   `execute_recall_pals` / `execute_craft_queue`, including
   the Lua-side allowlist entry.
3. `docs/OPERATIONS.md` - add the type to the operator
   allowlist matrix under "Enabling the action executor".
4. `tests/PalLLM.Tests/RuntimeTests.cs` - add tests for both
   the emit path (chat -> outbox carries the intent) and the
   blocked path (disabled or non-allowlisted type emits
   nothing).

**Per `ADR 0006` (opt-in everything by default):**
- The new action type defaults to NOT being in
  `Automation:AllowedActions`.
- The Lua-side allowlist defaults to NOT including the new
  type.

**Drift gates that will fire**:

- None directly, but missing tests will fail
  `Drift_Test_count_docs`.

## 8. New feature-catalog entry

Goal: register a new feature so it shows on the dashboard,
`/api/features`, and `/api/describe`.

**Files to touch**:

1. `src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs` - add a
   `FeatureDescriptor` with an `Id`, `Name`, `Status`
   (`"ready"` / `"scaffolded"` / `"deferred"`),
   `Description`, and the relevant `Surface` list.
2. `docs/ROADMAP.md` - bump the feature count + per-status
   split.
3. `README.md` + `docs/ARCHITECTURE.md` + `docs/HANDOFF.md` +
   `docs/CODE_MAP.md` - bump the feature count.

**Drift gates that will fire**:

- `Drift_Feature_catalog_count`, `Drift_Feature_status_split`

## 9. New ADR

Goal: document a new load-bearing decision.

**Files to touch**:

1. `docs/adr/000N-kebab-case-title.md` - copy the most recent
   ADR file as your template. Fill in Status, Date, Tags,
   Depends on, Supports, Context, Decision, Alternatives
   considered, Consequences, Harvest hint, Related.
2. `docs/adr/README.md` - add a row to the index table.
3. The relevant other docs - link the ADR from
   `ARCHITECTURE.md` / `CONVENTIONS.md` /
   `DESIGN_PRINCIPLES.md` if it covers ground in those docs.

ADR numbers are immutable. If an ADR is replaced, the new ADR
gets a fresh number and the old one's status changes to
"Superseded by ADR-NNNN".

## When in doubt

```powershell
pwsh ./pal.ps1 audit
```

The audit catches more than you'd expect. If the audit is
green, your change is structurally consistent with the rest of
the repo.

## Related

- [`EXTENSION_POINTS.md`](EXTENSION_POINTS.md) - "where in the
  file does X go?" map
- [`CONVENTIONS.md`](CONVENTIONS.md) - the four code patterns
  + three hard rules
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) - the human-facing
  contributor guide
- [`AGENTS.md`](../AGENTS.md) - the agent-facing briefing


