# Extension points — where to add code for X

Last audited: `2026-05-24`

A purely structural map: "I want to add X. Which file, which
section, which existing example should I copy?" Pairs with
[`COOKBOOK.md`](COOKBOOK.md) (the step-by-step recipes) and
[`CODE_MAP.md`](CODE_MAP.md) (the symbol-to-file index).

The rule of thumb: **find the closest existing example, copy
its shape.** PalLLM has converged on a small number of
patterns (advisor / builder / validator / feeder); fitting a
new addition into one of them keeps the codebase legible.

## Per surface area

| To add a... | Edit | Existing example to copy |
|---|---|---|
| `GET /api/*` route | `src/PalLLM.Sidecar/Program.cs` or a focused `src/PalLLM.Sidecar/RouteRegistrations/*.cs` companion | `api.MapGet("/budgets", ...)` in `PalLlmInspectionRoutes.cs`; `PalLlmHealthRoutes.cs` or `PalLlmBridgeRoutes.cs` for a split domain |
| `POST /api/*` route | `src/PalLLM.Sidecar/Program.cs` or the matching route companion | `api.MapPost("/chat/party", ...)`; `api.MapPost("/inference/warmup", ...)`; `api.MapPost("/bridge/drain", ...)` |
| Sidecar service registration | `src/PalLLM.Sidecar/Configuration/*ServiceCollectionExtensions.cs` | `AddPalLlmInference(...)` for pooled model clients |
| MCP tool | `src/PalLLM.Sidecar/Mcp/PalLlmMcpTools.cs` | `[McpTool] PalChatPlan(...)` |
| MCP resource | `src/PalLLM.Sidecar/Mcp/PalLlmMcpResources.cs` | `palllm://world/snapshot` resource |
| MCP prompt | `src/PalLLM.Sidecar/Mcp/PalLlmMcpPrompts.cs` | the existing 4 prompts |
| Wire-level request / response shape | `src/PalLLM.Domain/Integration/Contracts.cs` | `ChatRequest` / `ChatResponse` records |
| New `PalLlmOptions` sub-options class | `src/PalLLM.Domain/Configuration/PalLlmOptions.cs` (after the existing classes) | `MoodWeatherOptions` shape |
| Default value for an existing option | same file, in the property initializer | every option |
| Pure deterministic advisor (returns a posture record) | new file under `src/PalLLM.Domain/Runtime/` | `MoodWeatherAdvisor.cs` |
| Pure deterministic builder (composes a snapshot) | new file under `src/PalLLM.Domain/Runtime/` | `PrivacyPostureBuilder.cs` |
| Validator (checks shape + returns structured result) | new file under `src/PalLLM.Domain/Packs/` or `Runtime/` | `PersonalityPack.cs` (`PersonalityPackValidator`) |
| Feeder (subscribes to runtime events, writes elsewhere) | new file under `src/PalLLM.Domain/Runtime/` | `PromotionLedgerFeeder.cs` |
| Background worker (`IHostedService`) | new file under `src/PalLLM.Sidecar/` | `BridgeInboxWorker.cs` |
| TTL-cached posture surface | extend an existing builder with `*Cached` | `AirGapVerifier.VerifyCached` (the cleanest example) |
| Fallback strategy | `src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs`, add a `Try_*` method | the existing 19 strategies, e.g. `Try_NarrativeRecall` |
| Presentation cue family | `src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs`, extend the family-mapping switch | the `tactical` family case |
| Bridge event handler | `src/PalLLM.Domain/Runtime/PalLlmRuntime.Bridge.cs` `ProcessBridgeEvent` switch | `chat_message` case |
| Bridge event producer (Lua side) | `mod/ue4ss/Mods/PalLLM/Scripts/main.lua` | the `emit_*` helpers |
| Guarded action type | `src/PalLLM.Domain/Runtime/ActionIntentPlanner.cs` (sidecar map) + `main.lua` (executor + allowlist) | `execute_waypoint_suggest` |
| Cooperation pattern (planner) | `src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs` | the existing 10 patterns |
| Role binding (Edge / Worker / Judge / Media / Validator) | configured in `appsettings.json` `PalLLM:ModelRoles[]` — runtime side already supports it | example bindings in `examples/` |
| Personality pack | new directory under `runtime-root/Packs/personalities/<id>/` with `pack.json` matching `docs/schemas/personality-pack.schema.json` | bundled example pack |
| Narrative pack | new directory under `runtime-root/Packs/narrative/<id>/` | bundled example pack |
| ADR | new file under `docs/adr/` | most recent ADR file |
| Drift gate | `scripts/run_full_audit.ps1` — add a step function and register it in the gate list | the `Drift_Doc_freshness` step |
| Test fixture | new file under `tests/PalLLM.Tests/` | match the file naming convention `<SubsystemName>Tests.cs` |
| Smoke check | `scripts/run-sidecar-smoke.ps1` | the existing `/api/health` and `/api/chat` checks |
| Doctor check | `scripts/doctor.ps1` | the existing port / SDK / smoke / replay checks |
| Public-copy guard | `scripts/audit_public_copy.ps1` and `scripts/public_copy_policy.ps1` | the existing brand-name guard |
| Config example for an MCP client | `examples/` | `mcp-claude-desktop.example.json` |

## Per concept

### "I want to track new state per character"

Add to `RelationshipTracker` (`src/PalLLM.Domain/Runtime/RelationshipTracker.cs`)
or `MoodWeatherAdvisor` if it's mood-shaped. If the state needs
to persist across sessions, add it to the persistence path in
`PalLlmRuntime` autosave.

### "I want to record a new metric"

`src/PalLLM.Domain/Runtime/PalLlmMetrics.cs` — add the counter /
gauge / histogram. The `/metrics` Prometheus endpoint exposes
them automatically.

### "I want a new health signal in `RuntimeHealth`"

`src/PalLLM.Domain/Integration/Contracts.cs` — add the field to
`RuntimeHealth`. Then update `PalLlmRuntime.GetHealth` to
populate it. If the new signal should subtract from the
operator score, also update `OperatorHealthScorer.Score`.

### "I want a new entry on `/api/describe`"

`src/PalLLM.Sidecar/SelfDescriptionBuilder.cs` — extend the
`Build` method. The response shape is recorded in
`SelfDescription` records in the same file.

### "I want a new entry on `/api/quickstart`"

`src/PalLLM.Sidecar/QuickstartGuideBuilder.cs` — add to the
critical / recommended / optional list with label / why /
action / verify.

### "I want a new evidence file under runtime-root"

Add the path constant to `PalLlmOptions.cs` (e.g.
`LatestMyEvidencePath => Path.Combine(ReleaseEvidenceDir,
"latest-my-thing.json")`). Update `EnsureDirectories` if
needed. Add the writer at the producer site.

### "I want a new posture surface (Pure / Stateful / Cached)"

Choose:
- **Pure** — pure function from inputs to a record. Easy. See
  `OperatorHealthScorer`.
- **Stateful** — accumulates over time. See
  `RelationshipTracker`.
- **Cached** — pure but called frequently. See
  `AirGapVerifier.VerifyCached` and ADR 0005.

## "I want to lift X out of the repo into another project"

Different doc — see [`HARVEST.md`](HARVEST.md) for the
extraction recipe per capability.

## Related

- [`COOKBOOK.md`](COOKBOOK.md) — step-by-step recipes (where
  this doc names the file, the cookbook walks through every
  drift gate / test / doc bump)
- [`CODE_MAP.md`](CODE_MAP.md) — symbol-to-file index
- [`CONVENTIONS.md`](CONVENTIONS.md) — the four patterns
  (advisor / builder / validator / feeder) + three hard rules
- [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) — what NOT to add
  even if it fits structurally
