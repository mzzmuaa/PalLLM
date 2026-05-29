# Quick reference — every surface, alphabetically

Last audited: `2026-05-24`

The pure-reference companion to [`CHEAT_SHEET.md`](CHEAT_SHEET.md).
Where the cheat sheet is narrative, this doc is sortable / grep-able.
One row per surface; no prose. If you're looking for something, this
is the page that has the most `Ctrl+F`-friendly hit rate.

## `pal.ps1` verbs

| Verb | What it does | Underlying |
|---|---|---|
| `aot-readiness` | Check trim/Native AOT readiness; optional native publish probe | `scripts/aot-readiness.ps1` |
| `audit` | Full drift audit (build + tests + 16 gates) | `scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging` |
| `benchmark` | Measure chat-turn latency vs tier budgets. Subcommand `cold-start` (Pass 360) measures clone→first-chat via `scripts/pal-benchmark-coldstart.ps1` | `scripts/pal-benchmark.ps1`, `scripts/pal-benchmark-coldstart.ps1` |
| `build` | `dotnet build` (Release) | `dotnet build PalLLM.sln --configuration Release --nologo` |
| `campfire` | Offline-capable companion REPL | `scripts/pal-campfire.ps1` |
| `check-updates` | Check releases for a newer version | `scripts/check-updates.ps1` |
| `cleanup` | Preview/remove generated audit coverage and build outputs | `scripts/pal-cleanup.ps1` |
| `config` | Edit/show/wizard runtime config | `scripts/pal-config-show.ps1`, `scripts/pal-config-wizard.ps1` |
| `connect` | Wire inference to a local engine or a cloud API (Pass 357: `pal connect cloud` for below-reference hardware) | `scripts/connect-cloud.ps1`, `scripts/connect-llamacpp.ps1`, `scripts/connect-lmstudio.ps1`, `scripts/connect-vllm.ps1`, `scripts/connect-vllm-omni.ps1`, `scripts/connect-transformers.ps1`, `scripts/connect-tensorrt.ps1`, `scripts/connect-openvino.ps1`, `scripts/connect-foundry.ps1` |
| `context` | Agent context JSON | `scripts/agent-context.ps1` |
| `demo` | Self-running fallback demo | `scripts/demo-pal.ps1` |
| `doctor` | Environment + smoke + delivery-replay | `scripts/doctor.ps1 -RunSmoke -RunDeliveryReplay` |
| `explain` | Explain a file or directory | `scripts/pal-explain.ps1` |
| `fast-audit` | Drift gates only (no rebuild / tests) | `scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging -SkipTests` |
| `fortune` | Date-seeded companion fortune | `scripts/pal-fortune.ps1` |
| `harvest` | Browse harvestable units | `scripts/pal-harvest.ps1` |
| `health` | Write one Markdown + JSON health snapshot | `scripts/pal-health.ps1` |
| `hello` | Send a one-shot chat probe | (built-in) |
| `help` | Help text + verb table | (built-in) |
| `list` | Verb table only | (built-in) |
| `logs` | Recent launch/native/audit activity | `scripts/pal-logs.ps1` |
| `mcp` | Configure an MCP client | `scripts/connect-mcp-client.ps1` |
| `models` | Recommend model/quantization; `models serving` prints live model-server policy; `models probe` writes no-prompt endpoint evidence | (built-in + `scripts/compatibility.json`, `scripts/pal-model-serving.ps1`, `scripts/pal-model-probe.ps1`) |
| `news` | Print recent changelog entries | `scripts/pal-news.ps1` |
| `next` | Context-aware "what should I do right now?" advisor | `scripts/pal-next.ps1` |
| `onboard` | First-time setup | `scripts/onboard.ps1` |
| `openapi` | Regenerate OpenAPI snapshot | `scripts/export-openapi.ps1` |
| `pack` | Manage personality packs | `scripts/pal-pack-list.ps1`, `scripts/pal-pack-copy.ps1`, `scripts/scaffold-pack.ps1` |
| `package` | Build the release zip | `scripts/package-release.ps1` |
| `patrol-report` | Print a companion night-watch report | `scripts/pal-patrol-report.ps1` |
| `play` | Boot sidecar + open dashboard | `scripts/play-palllm.ps1` |
| `preflight` | Fast readiness checklist | `scripts/pal-preflight.ps1` |
| `proof` | Read-only native proof status + next action | `scripts/pal-proof.ps1` |
| `publish-audit` | Local publication preflight | `scripts/publish-audit.ps1` |
| `quest` | Suggest a short in-session challenge | `scripts/pal-quest.ps1` |
| `readiness` | Candid readiness scorecard | (built-in) |
| `recover` | Last-resort recovery | `scripts/recover-palllm.ps1` |
| `run` | `dotnet run` the sidecar (foreground) | `dotnet run --configuration Release --project src/PalLLM.Sidecar/PalLLM.Sidecar.csproj` |
| `scaffold` | Scaffold a new pattern file | `scripts/scaffold.ps1` |
| `smoke` | Smoke test against a running sidecar | `scripts/run-sidecar-smoke.ps1` |
| `status` | One-line state check (counts + audit) | (built-in) |
| `support` | Export a privacy-redacted support bundle | `scripts/export-support-bundle.ps1` |
| `tale` | Print a short campfire story | `scripts/pal-tale.ps1` |
| `test` | `dotnet test` (Release, quiet) | `dotnet test PalLLM.sln --configuration Release --nologo --verbosity quiet` |
| `uninstall` | Remove the mod safely | `scripts/uninstall-mod.ps1` |
| `welcome` | First-run guided tour | `scripts/pal-welcome.ps1` |
| `where` | Natural-language file lookup | `scripts/pal-where.ps1` |
| `whisper` | Print a quiet companion one-liner | `scripts/pal-whisper.ps1` |
| `workflow-pins` | GitHub Actions full-SHA pin audit | `scripts/audit-workflow-action-pins.ps1` |

## Bundled inference engine (Pass 352)

One-command path from clone to running local LLM:

| Command | What it does | Underlying |
|---|---|---|
| `pwsh ./scripts/install-llama-cpp.ps1` | Detect hardware (GPU vendor / VRAM / RAM / CUDA toolkit), pick the right backend asset, download + smoke-test the upstream release, recommend a curated model | `scripts/install-llama-cpp.ps1` |
| `pwsh ./scripts/install-llama-cpp.ps1 -WireConfig` | Above + write PalLLM `appsettings.json` with the recommended model and per-family sampler | `scripts/install-llama-cpp.ps1` + `scripts/connect-llamacpp.ps1 -WriteConfig` |
| `pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch` | Above + launch `llama-server` with the recommended recipe (`-AutoLaunch` implies `-WireConfig`) | `scripts/install-llama-cpp.ps1` |
| `pwsh ./scripts/install-llama-cpp.ps1 -Backend cuda12 -ReleaseTag b9284` | Force a specific backend / pin a specific upstream tag | same |

Deep-dive: [`LLAMA_CPP_BUNDLED.md`](LLAMA_CPP_BUNDLED.md) (hardware-tier matrix, per-model recipes for all 7 curated families, MoE offloading recipes, KV-cache math, backend-specific safety nets, multi-GPU perf knobs).

## Drift gates (in order)

| # | Gate | Source code | Doc target(s) |
|---|---|---|---|
| 1 | `Build_Release` | `dotnet build` zero warnings | n/a |
| 2 | `Tests` | `[Test]` count green | n/a |
| 3 | `Drift_Mojibake` | every tracked file | n/a |
| 4 | `Drift_Api_route_count` | `Program.cs` + `RouteRegistrations/*.cs` `api.Map*` | README, ROADMAP, ARCHITECTURE, API |
| 5 | `Drift_Api_reference_surface` | live route list | API.md |
| 6 | `Drift_OpenApi_snapshot` | live route surface | `docs/openapi/palllm-sidecar-v1.json` |
| 7 | `Drift_Feature_catalog_count` | `PalLlmFeatureCatalog.cs` `Id =` count | README, ROADMAP, ARCHITECTURE, HANDOFF, CODE_MAP |
| 8 | `Drift_Feature_status_split` | catalog ready/scaffolded/deferred | README, ROADMAP, HANDOFF |
| 9 | `Drift_Fallback_strategy_count` | `FallbackBehaviorEngine.cs` `Try_*` | ROADMAP |
| 10 | `Drift_Test_count_docs` | `[Test]` count | README, ROADMAP, ARCHITECTURE, HANDOFF, CODE_MAP |
| 11 | `Drift_Public_copy` | brand-name policy | README, NOTICE, SECURITY, INDEX, RELEASE, CONTRIBUTING, issue templates |
| 12 | `Drift_Path_references` | every `path/like/this.cs` reference | every doc |
| 13 | `Drift_Agents_manifest` | `agents.json` required keys + types | validated against `docs/schemas/agents.schema.json` |
| 14 | `Drift_Doc_freshness` | `Last audited:` stamps | every doc with a stamp |
| 15 | `Drift_Hot_file_line_count` | hot file `wc -l` vs prose mirrors | CLAUDE.md, CHEAT_SHEET.md, copilot-instructions.md, ANTI_PATTERNS.md, HARVEST.md |
| 16 | `Drift_Dangling_markdown_links` | every `[text](path)` | every doc |

## Hot-path methods + budgets

| Method | File | Cold | Warm |
|---|---|---|---|
| `PalLlmRuntime.ChatAsync` (deterministic) | `PalLlmRuntime.cs` | < 200 ms | < 100 ms |
| `PalLlmRuntime.ChatAsync` (with inference) | `PalLlmRuntime.cs` | < 2.5 s | < 2 s |
| `ChatDispatchPlanner.Decide` | `ChatDispatchPlanner.cs` | < 5 ms | < 1 ms |
| `FallbackBehaviorEngine.CreateGeneralDirector` | `FallbackBehaviorEngine.cs` | < 30 ms | < 10 ms |
| `PresentationCuePlanner.Build` | `PresentationCuePlanner.cs` | < 20 ms | < 5 ms |
| `PalLlmRuntime.GetWorldSnapshot` | `PalLlmRuntime.Snapshot.cs` | < 50 ms | < 20 ms |
| `PalLlmRuntime.GetHealth` | `PalLlmRuntime.Snapshot.cs` | < 20 ms | < 5 ms |
| `OperatorHealthScorer.Score` | `OperatorHealthScorer.cs` | < 1 ms | < 1 ms |
| `HardwareProfiler.CaptureCached` | `HardwareProfiler.cs` | < 20 ms | < 1 ms |
| `PrivacyPostureBuilder.CaptureCached` | `PrivacyPostureBuilder.cs` | < 5 ms | < 1 ms |
| `ResourceBudgetPostureBuilder.CaptureCached` | `ResourceBudgetPostureBuilder.cs` | < 3 ms | < 1 ms |
| `AirGapVerifier.VerifyCached` | `AirGapVerifier.cs` | < 50 ms | < 1 ms |
| `BridgeInboxWorker.ExecuteAsync` (per envelope) | `BridgeInboxWorker.cs` | < 100 ms | < 50 ms |
| `PalLlmRuntime.WriteOutboxReplyAsync` | `PalLlmRuntime.Outbox.cs` | < 20 ms | < 10 ms |

Full table with rationale in [`HOT_PATH.md`](HOT_PATH.md).

## OpenTelemetry spans

| Span | Source | Tags | Where |
|---|---|---|---|
| `pal.chat` | `PalLLM.Runtime` | `pal.request_id`, `pal.character_id`, `pal.task_tag`, `pal.response_path`, `pal.used_fallback`, `pal.fallback_strategy`, `pal.visual_context_source`, `pal.inference_model`, `pal.inference_profile`, `pal.inference_lane`, `pal.inference_attempted` | `PalLlmRuntime.ChatAsync` |
| `pal.model_tier.transition` | `PalLLM.Runtime` | `pal.model_tier.previous`, `pal.model_tier.current`, `pal.model_tier.model`, `pal.model_tier.available_count` | `ModelTierOrchestrator` on tier change |
| `<model-id>` (GenAI client span) | `PalLLM.Runtime` | OpenTelemetry GenAI semantic-convention attributes | `GenAiTelemetry` per upstream inference call |
| (auto) | `Microsoft.AspNetCore.*` | OTel HTTP server semconv | One root span per inbound HTTP request |
| (auto) | `System.Net.Http.*` | OTel HTTP client semconv | One span per outbound `HttpClient` call |

Full inventory + how-to-wire-up in [`OBSERVABILITY.md`](OBSERVABILITY.md).

## ResponsePath values (chat reply diagnostics)

| Value | When it fires |
|---|---|
| `inference-completed` | Live inference returned a response |
| `fallback-after-inference-disabled` | `Inference:Enabled = false` |
| `fallback-after-inference-error` | Inference returned 5xx / timed out |
| `fallback-after-breaker-open` | Circuit breaker is open |
| `fallback-after-rate-limit` | Per-character rate limit breached |
| `fallback-after-thermal-gate` | Thermal gate fired |
| `fallback-<strategy>` | Specific strategy fired (e.g. `fallback-narrative-recall`) |
| `emergency-fallback` | Every primary strategy returned null |

Full decision tree in [`STATE_MACHINES.md`](STATE_MACHINES.md) §5.

## Bridge directories

| Path | Producer | Consumer | Retention |
|---|---|---|---|
| `Bridge/Inbox/` | Lua bridge | Sidecar (`BridgeInboxWorker`) | Drained per poll |
| `Bridge/Outbox/` | Sidecar | Lua bridge | `OutboxMaxFiles = 100`, `OutboxMaxAgeHours = 24` |
| `Bridge/Archive/` | Sidecar (after drain) | (history only) | `ArchiveMaxFiles = 500`, `ArchiveMaxAgeHours = 72` |
| `Bridge/Failed/` | Sidecar (on failure) | Operator | `FailedMaxFiles = 200`, `FailedMaxAgeHours = 168` |
| `Bridge/Screenshots/` | Lua bridge (when watcher on) | Sidecar (vision) | `PendingScreenshotMaxFiles = 32`, `PendingScreenshotMaxAgeHours = 1` |
| `Bridge/Diagnostics/` | Lua bridge (widget probes) | Sidecar (proof builder) | `DiagnosticsMaxFiles = 128`, `DiagnosticsMaxAgeHours = 168` |

## Runtime root layout

```
runtime-root/                           ($env:LOCALAPPDATA\Pal\Saved\PalLLM by default)
├── session.json                        ← memory + relationships, atomic-write persisted
├── Models/                             ← reserved for downloaded weights (created on demand)
├── Packs/                              ← personality + narrative packs
│   ├── personalities/<id>/             ← see docs/schemas/personality-pack.schema.json
│   └── narrative/<id>/
├── TTS/                                ← cached TTS audio when synthesis on
├── Bridge/
│   ├── Inbox/ Outbox/ Archive/ Failed/ Screenshots/ Diagnostics/
├── ReleaseEvidence/                    ← latest-*.json + History/
└── SupportEvidence/                    ← support-bundle outputs + History/
```

## Health endpoints

| Endpoint | What it returns | Target latency |
|---|---|---|
| `GET /api/health` | `RuntimeHealth` (full posture flags + breaker state) | < 20 ms |
| `GET /api/describe` | `SelfDescription` (embeds `OperatorHealthScore` — 0-100 + grade + reasons) | < 20 ms |
| `GET /health/live` | Plain `200 OK` (Kubernetes liveness shape) | < 1 ms |
| `GET /health/ready` | `200` if ready, `503` if not | < 5 ms |
| `GET /metrics` | Prometheus-formatted counters | < 10 ms |

## Configuration root keys

| Root key | Purpose | ADR |
|---|---|---|
| `PalLlmOptions:Bridge` | Bridge polling, retention, outbox shape | [`adr/0003`](adr/0003-one-way-advisory-bridge.md) |
| `PalLlmOptions:Inference` | Live inference endpoint + breaker + thermal gate | [`adr/0001`](adr/0001-deterministic-first-reply-pipeline.md) |
| `PalLlmOptions:Fallback` | Deterministic-fallback engine + rate limits | [`adr/0001`](adr/0001-deterministic-first-reply-pipeline.md) |
| `PalLlmOptions:Vision` | Vision describe + screenshot watcher | [`adr/0006`](adr/0006-opt-in-everything-by-default.md) |
| `PalLlmOptions:Tts` | TTS synthesis endpoint + caps | [`adr/0006`](adr/0006-opt-in-everything-by-default.md) |
| `PalLlmOptions:Auth` | API-key bearer auth | [`adr/0006`](adr/0006-opt-in-everything-by-default.md) |
| `PalLlmOptions:Http` | HTTP surface tuning + cache TTLs | n/a |
| `PalLlmOptions:Automation` | Action-intent emit + executor allowlist | [`adr/0003`](adr/0003-one-way-advisory-bridge.md) |
| `PalLlmOptions:Hardware` | Tier override | [`adr/0005`](adr/0005-ttl-cache-for-posture-surfaces.md) |
| `PalLlmOptions:Session` | Memory autosave cadence | n/a |
| `PalLlmOptions:SelfHealing` | Watchdog ticks | n/a |
| `PalLlmOptions:PromotionFeeder` | Promotion ledger feeder | n/a |
| `PalLlmOptions:PromotionApply` | Apply staging root + safety flag | [`adr/0006`](adr/0006-opt-in-everything-by-default.md) |
| `PalLlmOptions:McpClient` | Upstream MCP server registry | n/a |
| `PalLlmOptions:ModelRoles[]` | Edge / Worker / Judge / Media / Validator bindings | n/a |

Per-property defaults + effects in [`TUNING.md`](TUNING.md). Each
property carries its own XML doc on the C# class — read
`src/PalLLM.Domain/Configuration/PalLlmOptions.cs` for the source
of truth.

## Environment variables

| Variable | What it does |
|---|---|
| `ASPNETCORE_URLS` | Override the bound HTTP URL(s) — e.g. `http://localhost:5089` |
| `ASPNETCORE_ENVIRONMENT` | `Development` / `Production`; controls verbose error pages |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Turn on OpenTelemetry export to the named OTLP collector |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` (default) or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Vendor auth headers if needed |
| `PALLLM_OTLP_DISABLE_ASPNETCORE` | `1` = don't add AspNetCore instrumentation |
| `PALLLM_OTLP_DISABLE_HTTPCLIENT` | `1` = don't add HttpClient instrumentation |
| `PalLLM__*` | Any `PalLlmOptions` field can be set this way (double-underscore = `:` separator) |

Full table with examples in [`ENV_VARS.md`](ENV_VARS.md).

## Documentation surfaces

| When you want... | Read |
|---|---|
| The plain-English pitch | [`PITCH.md`](PITCH.md) |
| The code-shaped quick reference | [`CHEAT_SHEET.md`](CHEAT_SHEET.md) |
| This sortable table | (you are here) |
| Recipes for adding things | [`COOKBOOK.md`](COOKBOOK.md) |
| "Where does X go?" | [`EXTENSION_POINTS.md`](EXTENSION_POINTS.md) |
| Visual data flows | [`DATAFLOW.md`](DATAFLOW.md) |
| State machines | [`STATE_MACHINES.md`](STATE_MACHINES.md) |
| Performance budgets | [`HOT_PATH.md`](HOT_PATH.md) |
| OpenTelemetry primer | [`OBSERVABILITY.md`](OBSERVABILITY.md) |
| Incident response | [`RUNBOOK.md`](RUNBOOK.md) |
| Foundational decisions | [`adr/`](adr/) |
| What NOT to do | [`ANTI_PATTERNS.md`](ANTI_PATTERNS.md) |
| Symbol-to-file map | [`CODE_MAP.md`](CODE_MAP.md) |
| Lifting one capability out | [`HARVEST.md`](HARVEST.md) |
| Writing a test | [`TESTING.md`](TESTING.md) |
| Current rolling counts | [`PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) (machine-readable) |

## Related

- [`CHEAT_SHEET.md`](CHEAT_SHEET.md) — narrative one-pager
- [`INDEX.md`](INDEX.md) — full doc map (Diataxis-organised)
- [`PROJECT_NUMBERS.json`](PROJECT_NUMBERS.json) — machine-readable counts
