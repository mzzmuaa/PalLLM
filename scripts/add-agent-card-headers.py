"""
One-shot inserter for Pass 420: add AGENT-CARD headers to the
25 files Codex extracted in Passes 375-402 (eight `PalLlmRuntime.*.cs`
partials, five `*ServiceCollectionExtensions.cs`, twelve `*Routes.cs`).

Convention (matches existing headers in PromotionLedger.cs, WhyEngine.cs,
PalLlmRuntime.cs):

    using ...;

    // ---------------------------------------------------------------------------
    // AGENT-CARD:
    //   what:    <one paragraph: what this file does>
    //   surface: <public types/methods, comma-separated>
    //   gate:    <which test fixture(s) cover it>
    //   adr:     <related ADR or "None directly">
    //   docs:    <relevant doc paths>
    // ---------------------------------------------------------------------------

    namespace ...;

The script:
  1. Reads each file.
  2. Skips if the header is already present.
  3. Inserts the header between the last `using` line and the
     `namespace` declaration.
  4. Idempotent: running twice produces no further diff.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]

# Per-file content. Each entry is (relative_path, card_lines).
# Keep card_lines as a list of strings WITHOUT the `//   ` prefix —
# the script formats them.
HEADERS: dict[str, dict[str, str]] = {
    # ---------- PalLlmRuntime partials ----------
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.Helpers.cs": {
        "what": (
            "Static helpers + small private utility methods shared by the other "
            "PalLlmRuntime.*.cs partials. Endpointing math (NormalizeEndpointingMs, "
            "SumEndpointingMs), bridge-directory bookkeeping caps, file-sort + "
            "budget clamps. Pure functions; no I/O, no state."
        ),
        "surface": (
            "PalLlmRuntime.NormalizeEndpointingMs, PalLlmRuntime.SumEndpointingMs, "
            "PalLlmRuntime.ClampPositiveBudget, the DirectoryActivityCountCap "
            "constant. All internal/private."
        ),
        "gate": (
            "Covered transitively by every PalLlmRuntime fixture that calls a "
            "method using these helpers; pinned directly by "
            "tests/PalLLM.Tests/RuntimeTests.cs."
        ),
        "adr": "None directly.",
        "docs": "docs/CODE_MAP.md, docs/REFACTORING_ROADMAP.md (Phase 1a).",
    },
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.UiProbe.cs": {
        "what": (
            "Owns the ui_probe dump-parsing pipeline: parses Lua-emitted widget "
            "trees out of Bridge/UiProbe, scores candidates against keyword "
            "allow/deny lists, caches parsed dumps under a 2-second TTL with a "
            "512-entry LRU cap, surfaces ranked HUD-bind recommendations."
        ),
        "surface": (
            "PalLlmRuntime.GetUiProbeStatus, PalLlmRuntime.GetUiProbeWidgetCandidates, "
            "PalLlmRuntime.IngestUiProbeDump, the UiProbeDumpCacheMaxEntries / "
            "UiProbeDiagnosticsSnapshotTtl tuning constants."
        ),
        "gate": (
            "tests/PalLLM.Tests/UiProbeTests.cs + "
            "tests/PalLLM.Tests/RuntimeTests.cs (drain + status paths)."
        ),
        "adr": "ADR 0003 (one-way advisory bridge).",
        "docs": (
            "docs/ARCHITECTURE.md (HUD-bind seam), "
            "docs/API.md (/api/ui-probe endpoints)."
        ),
    },
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.Prompt.cs": {
        "what": (
            "Prompt-building helpers: resolves speaker name, formats area ranges, "
            "stitches memory + relationship hints + personality packs + world "
            "snapshot into the prompt that Chat.Inference sends downstream. "
            "Pure formatting; no model calls."
        ),
        "surface": (
            "PalLlmRuntime.ResolveSpeakerName, "
            "PalLlmRuntime.FormatAreaRange, "
            "PalLlmRuntime.BuildPromptInputs (all private)."
        ),
        "gate": (
            "tests/PalLLM.Tests/PalLlmRuntimeChatTests.cs + "
            "tests/PalLLM.Tests/PromptBuilderTests.cs."
        ),
        "adr": "ADR 0001 (deterministic-first reply pipeline).",
        "docs": (
            "docs/DATAFLOW.md (Chat hot path), "
            "docs/PROMPT_CARDS.md (deterministic-fallback strategies)."
        ),
    },
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.BridgeBoot.cs": {
        "what": (
            "Records and normalises the Lua mod's boot payload (UE4SS version, "
            "HUD compatibility signals, native-hud-widget targets, ui_probe "
            "enablement). Cached under _bridgeGate so /api/bridge/proof can "
            "return it without re-parsing the inbox file."
        ),
        "surface": (
            "PalLlmRuntime.RememberBridgeBoot, "
            "PalLlmRuntime.GetLastBridgeBoot (returns BridgeBootPayload)."
        ),
        "gate": (
            "tests/PalLLM.Tests/BridgeBootTests.cs + "
            "tests/PalLLM.Tests/BridgeIngestAdversarialTests.cs (fuzz)."
        ),
        "adr": "ADR 0003 (one-way advisory bridge).",
        "docs": (
            "docs/STATE_MACHINES.md (bridge boot lifecycle), "
            "docs/EVENTS.md (bridge_boot envelope shape)."
        ),
    },
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.Bridge.cs": {
        "what": (
            "Inbox drain loop: pulls Bridge/Inbox/*.json envelopes through the "
            "bridge gate, dispatches each to ProcessBridgeEvent, quarantines "
            "malformed payloads to Bridge/Failed/, and updates the activity "
            "snapshot. The 'one-way advisory bridge' contract lives here."
        ),
        "surface": (
            "PalLlmRuntime.DrainInbox(int maxFiles), "
            "BridgeDrainResult (return type), "
            "internal ProcessBridgeEvent dispatch."
        ),
        "gate": (
            "tests/PalLLM.Tests/BridgeIngestAdversarialTests.cs (35 fuzz cases) + "
            "tests/PalLLM.Tests/DrainInboxTests.cs + every event-type fixture "
            "named DrainInbox_*Tests.cs."
        ),
        "adr": "ADR 0003 (one-way advisory bridge).",
        "docs": (
            "docs/DATAFLOW.md (bridge sequence diagram), "
            "docs/EVENTS.md (event taxonomy)."
        ),
    },
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.Outbox.cs": {
        "what": (
            "Vision/snapshot ingest: pulls Bridge/Screenshots/*.png through the "
            "vision orchestrator, merges the extracted world-state into the live "
            "snapshot, and writes outbox responses back to Bridge/Outbox/. "
            "Bounded by maxFiles to keep long sessions responsive."
        ),
        "surface": (
            "PalLlmRuntime.ProcessScreenshotsAsync(CancellationToken, int maxFiles), "
            "ScreenshotIngestResult (return type)."
        ),
        "gate": (
            "tests/PalLLM.Tests/SnapshotVisionFallbackTests.cs + "
            "tests/PalLLM.Tests/RuntimeTests.cs (Outbox lane)."
        ),
        "adr": "ADR 0003 (one-way advisory bridge).",
        "docs": (
            "docs/DATAFLOW.md (vision sequence diagram), "
            "docs/MULTIMODAL_RECIPES.md."
        ),
    },
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.Snapshot.cs": {
        "what": (
            "Health + activity snapshot construction. Composes GameWorldSnapshot, "
            "BridgeActivitySnapshot, DirectoryActivitySnapshot, and runtime gauges "
            "into the RuntimeHealth payload consumed by /api/health, the dashboard, "
            "and the SLO Prometheus scrape."
        ),
        "surface": (
            "PalLlmRuntime.GetHealth(), "
            "PalLlmRuntime.GetDirectoryActivitySnapshot (private), "
            "PalLlmRuntime.BuildRuntimeHealth (private)."
        ),
        "gate": (
            "tests/PalLLM.Tests/RuntimeTests.cs + "
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (/api/health route)."
        ),
        "adr": "None directly (composes ADR 0001 + ADR 0003 surfaces).",
        "docs": (
            "docs/API.md (/api/health), "
            "docs/OBSERVABILITY.md, docs/OBSERVABILITY_SLO.md."
        ),
    },
    "src/PalLLM.Domain/Runtime/PalLlmRuntime.Inference.cs": {
        "what": (
            "Inference-lane introspection: surfaces the active model id, tier id, "
            "performance snapshot, and circuit-breaker state to the snapshot "
            "builder, /api/inference/* routes, and the SLO metrics scrape. "
            "Calls into _inferenceClient; never sends a chat completion."
        ),
        "surface": (
            "PalLlmRuntime.GetInferencePerformanceSnapshot, "
            "PalLlmRuntime.GetInferenceCircuitState (private), "
            "PalLlmRuntime.GetInferenceActiveModel (private), "
            "PalLlmRuntime.GetInferenceActiveTierId (private)."
        ),
        "gate": (
            "tests/PalLLM.Tests/InferenceClientTests.cs + "
            "tests/PalLLM.Tests/ModelTierTests.cs + "
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (inference routes)."
        ),
        "adr": "ADR 0001 (deterministic-first reply pipeline).",
        "docs": (
            "docs/API.md (/api/inference/*), "
            "docs/MODEL_COLLABORATION.md, docs/HOT_PATH.md."
        ),
    },
    # ---------- Sidecar Configuration ----------
    "src/PalLLM.Sidecar/Configuration/PalLlmCoreServiceCollectionExtensions.cs": {
        "what": (
            "DI registration entry point for the PalLLM Domain runtime: portable "
            "adapter, runtime, fallback engine, memory store, relationship "
            "aggregator, narrative pack service, and all the advisor/builder "
            "singletons. Called by Program.cs.AddPalLlmCore()."
        ),
        "surface": (
            "PalLlmCoreServiceCollectionExtensions.AddPalLlmCore(IServiceCollection, "
            "IConfiguration)."
        ),
        "gate": (
            "tests/PalLLM.Tests/SidecarTestFixture.cs (fixture wiring) + "
            "every fixture that boots the host."
        ),
        "adr": "ADR 0002 (portable adapter seam).",
        "docs": (
            "docs/ARCHITECTURE.md (DI lanes), "
            "docs/CODE_MAP.md (where things live)."
        ),
    },
    "src/PalLLM.Sidecar/Configuration/PalLlmInferenceServiceCollectionExtensions.cs": {
        "what": (
            "DI registration for the inference lane: chat completions client + "
            "vision client + ASR client + reranker, all wired against "
            "SocketsHttpHandler pooling (PooledConnectionLifetime, no per-request "
            "HttpClient instances). Reads PalLLM:Inference / :Vision / :Asr."
        ),
        "surface": (
            "PalLlmInferenceServiceCollectionExtensions.AddPalLlmInference("
            "IServiceCollection, IConfiguration)."
        ),
        "gate": (
            "tests/PalLLM.Tests/InferenceClientTests.cs + "
            "tests/PalLLM.Tests/MetaTests.cs (SocketsHttpHandler invariants)."
        ),
        "adr": "ADR 0001 (deterministic-first reply pipeline).",
        "docs": (
            "docs/MODEL_COLLABORATION.md, docs/LLAMA_CPP_BUNDLED.md."
        ),
    },
    "src/PalLLM.Sidecar/Configuration/PalLlmMcpServiceCollectionExtensions.cs": {
        "what": (
            "DI registration for the MCP server: tools, resources, prompts, and "
            "the SSE transport. Reads PalLLM:Mcp:Enabled and wires the 38 tools "
            "+ 6 resources + 1 templated resource + 4 prompts surface."
        ),
        "surface": (
            "PalLlmMcpServiceCollectionExtensions.AddPalLlmMcp(IServiceCollection, "
            "IConfiguration)."
        ),
        "gate": "tests/PalLLM.Tests/McpServerTests.cs.",
        "adr": "ADR 0006 (opt-in everything by default).",
        "docs": (
            "docs/MCP_QUICKSTART.md, "
            "docs/API.md (/mcp route)."
        ),
    },
    "src/PalLLM.Sidecar/Configuration/PalLlmHealthAndOpenApiServiceCollectionExtensions.cs": {
        "what": (
            "DI registration for ASP.NET Core health checks (live + ready), "
            "OpenAPI 3.1 document generation, and the /openapi/v1.{json,yaml} "
            "static asset routes."
        ),
        "surface": (
            "PalLlmHealthAndOpenApiServiceCollectionExtensions.AddPalLlmHealthAndOpenApi("
            "IServiceCollection)."
        ),
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (health + OpenAPI routes)."
        ),
        "adr": "None directly.",
        "docs": "docs/API.md (operational routes), docs/openapi/.",
    },
    "src/PalLLM.Sidecar/Configuration/PalLlmObservabilityServiceCollectionExtensions.cs": {
        "what": (
            "DI registration for OpenTelemetry: traces, metrics, logs. Adds the "
            "PalLLM ActivitySource + Meter, the Prometheus scrape endpoint, and "
            "the GenAI semantic-convention emitters used by InferenceClient. "
            "Aligned with docs/OBSERVABILITY_SLO.md."
        ),
        "surface": (
            "PalLlmObservabilityServiceCollectionExtensions.AddPalLlmObservability("
            "IServiceCollection, IConfiguration)."
        ),
        "gate": (
            "tests/PalLLM.Tests/ObservabilitySloTests.cs."
        ),
        "adr": "None directly.",
        "docs": (
            "docs/OBSERVABILITY.md, docs/OBSERVABILITY_SLO.md, "
            "scripts/observability/palllm.alerts.yaml."
        ),
    },
    # ---------- Route registrations ----------
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmBridgeRoutes.cs": {
        "what": (
            "Maps the /api/bridge/* operational routes: bridge proof, native-proof "
            "evidence, HUD-bind recommendations. Each route is a thin shim that "
            "calls into PalLlmRuntime and serialises through "
            "PalLlmDomainJsonSerializerContext."
        ),
        "surface": "PalLlmBridgeRoutes.MapBridge(IEndpointRouteBuilder).",
        "gate": "tests/PalLLM.Tests/SidecarEndpointTests.cs (bridge routes).",
        "adr": "ADR 0003 (one-way advisory bridge).",
        "docs": "docs/API.md (/api/bridge/*).",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmContentWorldRoutes.cs": {
        "what": (
            "Maps the /api/content/* and /api/world/* routes: world-snapshot "
            "read, content-pack inspection, narrative-pack list."
        ),
        "surface": (
            "PalLlmContentWorldRoutes.MapContentWorld(IEndpointRouteBuilder)."
        ),
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (content + world routes)."
        ),
        "adr": "None directly.",
        "docs": "docs/API.md (/api/content/*, /api/world/*).",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmConversationRoutes.cs": {
        "what": (
            "Maps the /api/chat/* + /api/conversation/* + /api/memory/* routes: "
            "the chat hot path, conversation transcript reads, memory inspection, "
            "and the SSE chat stream endpoint."
        ),
        "surface": (
            "PalLlmConversationRoutes.MapConversation(IEndpointRouteBuilder)."
        ),
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (chat hot path) + "
            "tests/PalLLM.Tests/ConversationMemoryStoreTests.cs."
        ),
        "adr": "ADR 0001 (deterministic-first reply pipeline).",
        "docs": (
            "docs/API.md (/api/chat/*), docs/DATAFLOW.md (chat hot path)."
        ),
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmHealthRoutes.cs": {
        "what": (
            "Maps /api/health, /api/release/readiness, /api/inference/health, and "
            "operational /health/live + /health/ready. Each emits a RuntimeHealth-"
            "derived payload from PalLlmRuntime.GetHealth()."
        ),
        "surface": "PalLlmHealthRoutes.MapHealth(IEndpointRouteBuilder).",
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (health routes)."
        ),
        "adr": "None directly.",
        "docs": "docs/API.md (health routes), docs/OBSERVABILITY_SLO.md.",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmInferenceRoutes.cs": {
        "what": (
            "Maps /api/inference/* routes: performance snapshot, active model "
            "and tier, collaboration plan, circuit-breaker state. Read-only "
            "introspection of the inference lane."
        ),
        "surface": "PalLlmInferenceRoutes.MapInference(IEndpointRouteBuilder).",
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (inference routes) + "
            "tests/PalLLM.Tests/InferenceClientTests.cs."
        ),
        "adr": "ADR 0001 (deterministic-first reply pipeline).",
        "docs": (
            "docs/API.md (/api/inference/*), docs/MODEL_COLLABORATION.md."
        ),
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmInspectionRoutes.cs": {
        "what": (
            "Maps /api/inspect/* routes: feature catalog, advisor inventory, "
            "fallback strategy enumeration, environment posture, suggestion "
            "surface. Read-only introspection of the runtime composition."
        ),
        "surface": "PalLlmInspectionRoutes.MapInspection(IEndpointRouteBuilder).",
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (inspect routes)."
        ),
        "adr": "None directly.",
        "docs": "docs/API.md (/api/inspect/*), docs/ADVISORS.md.",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmMediaRoutes.cs": {
        "what": (
            "Maps /api/media/* routes: vision describe, TTS synth (when enabled), "
            "ASR transcribe, multimodal cache-id lookup, screenshot ingest."
        ),
        "surface": "PalLlmMediaRoutes.MapMedia(IEndpointRouteBuilder).",
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (media routes) + "
            "tests/PalLLM.Tests/SnapshotVisionFallbackTests.cs."
        ),
        "adr": "ADR 0006 (opt-in everything by default).",
        "docs": "docs/MULTIMODAL_RECIPES.md, docs/API.md (/api/media/*).",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmPlanningRoutes.cs": {
        "what": (
            "Maps /api/plan/* routes: action-intent planning, world-model "
            "advisory planning, fallback-director dry-run. Surfaces the "
            "deterministic planner outputs without executing anything."
        ),
        "surface": "PalLlmPlanningRoutes.MapPlanning(IEndpointRouteBuilder).",
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (planning routes) + "
            "tests/PalLLM.Tests/ActionIntentPlannerTests.cs."
        ),
        "adr": "ADR 0006 (opt-in everything by default).",
        "docs": "docs/API.md (/api/plan/*), docs/ADVISORS.md.",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmPromotionRoutes.cs": {
        "what": (
            "Maps /api/promotion/* + /api/why/* routes: promotion ledger reads, "
            "why-engine traces, proof packet inspection. Read-only views into "
            "the deterministic-promotion pipeline."
        ),
        "surface": "PalLlmPromotionRoutes.MapPromotion(IEndpointRouteBuilder).",
        "gate": (
            "tests/PalLLM.Tests/PromotionApply*Tests.cs + "
            "tests/PalLLM.Tests/WhyEngineTests.cs + "
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (promotion routes)."
        ),
        "adr": "None directly.",
        "docs": (
            "docs/STATE_MACHINES.md (promotion ledger), docs/API.md."
        ),
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmProofReadinessRoutes.cs": {
        "what": (
            "Maps /api/release/readiness + the proof-bundle export endpoints. "
            "Surfaces release-evidence artifacts (smoke proof, native-proof, "
            "full-audit, package verification) as one machine-readable shape."
        ),
        "surface": (
            "PalLlmProofReadinessRoutes.MapProofReadiness(IEndpointRouteBuilder)."
        ),
        "gate": (
            "tests/PalLLM.Tests/ReleaseReadinessTests.cs + "
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (release routes)."
        ),
        "adr": "None directly.",
        "docs": "docs/RELEASE.md, docs/API.md (/api/release/readiness).",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmStateRoutes.cs": {
        "what": (
            "Maps /api/state/* routes: lifetime relationship aggregate, "
            "personality pack list, session snapshot, opt-in posture report. "
            "Read-only views into persistent state."
        ),
        "surface": "PalLlmStateRoutes.MapState(IEndpointRouteBuilder).",
        "gate": (
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (state routes) + "
            "tests/PalLLM.Tests/LifetimeRelationshipAggregatorTests.cs."
        ),
        "adr": "None directly.",
        "docs": "docs/API.md (/api/state/*).",
    },
    "src/PalLLM.Sidecar/RouteRegistrations/PalLlmStaticAssetRoutes.cs": {
        "what": (
            "Maps the static asset surface: dashboard at /, /welcome.html, "
            "/openapi/v1.{json,yaml}, source-generated conditional ETags + "
            "last-modified validation. The Field Console UI lives behind these "
            "routes."
        ),
        "surface": (
            "PalLlmStaticAssetRoutes.MapStaticAssets(IEndpointRouteBuilder)."
        ),
        "gate": (
            "tests/PalLLM.Tests/StaticAssetTests.cs + "
            "tests/PalLLM.Tests/SidecarEndpointTests.cs (static routes)."
        ),
        "adr": "None directly.",
        "docs": (
            "docs/API.md (operational routes), "
            "src/PalLLM.Sidecar/wwwroot/ (the dashboard sources)."
        ),
    },
}


def format_card(card: dict[str, str]) -> str:
    border = "// ---------------------------------------------------------------------------"
    lines = [border, "// AGENT-CARD:"]
    for key in ("what", "surface", "gate", "adr", "docs"):
        value = card[key].strip()
        # Wrap long values at ~70 chars, indented under the field.
        # Align field colons at column 12 (matches the existing header
        # convention used in PromotionLedger.cs, WhyEngine.cs, etc.).
        prefix = f"//   {key}:{' ' * (8 - len(key) - 1)} "
        wrapped = wrap_field(prefix, value)
        lines.extend(wrapped)
    lines.append(border)
    return "\n".join(lines)


def wrap_field(prefix: str, value: str, width: int = 75) -> list[str]:
    """Hard-wrap a single AGENT-CARD field across multiple comment lines."""
    indent = "//            "  # under "what:    ", 4-space namespace + 7-char field + 1 space
    words = value.split()
    lines: list[str] = []
    current = prefix
    first = True
    for word in words:
        if first:
            candidate = current + word
        else:
            candidate = (current + " " + word) if not current.endswith(" ") else current + word
        if len(candidate) > width and not first and current.strip() != prefix.strip():
            lines.append(current.rstrip())
            current = indent + word
        else:
            current = candidate if first else current + " " + word
        first = False
    if current.strip():
        lines.append(current.rstrip())
    return lines


SENTINEL = "// AGENT-CARD:"


def insert_header(file_path: Path, card: dict[str, str]) -> bool:
    text = file_path.read_text(encoding="utf-8")
    if SENTINEL in text[:2000]:
        return False  # already present
    header_block = format_card(card)
    # Insert between the last `using ...;` line and the `namespace` line.
    # Pattern: any number of using lines, blank line, namespace declaration.
    match = re.search(r"^(namespace\s+[^\s;]+\s*;)", text, re.MULTILINE)
    if not match:
        print(f"  !! no namespace in {file_path}")
        return False
    insert_at = match.start()
    new_text = text[:insert_at] + header_block + "\n\n" + text[insert_at:]
    file_path.write_text(new_text, encoding="utf-8")
    return True


def main() -> int:
    inserted = 0
    skipped = 0
    for rel, card in HEADERS.items():
        path = REPO / rel
        if not path.is_file():
            print(f"  !! missing {rel}")
            continue
        if insert_header(path, card):
            print(f"  ++ {rel}")
            inserted += 1
        else:
            print(f"  .. {rel} (already has header)")
            skipped += 1
    print(f"\nInserted: {inserted}; already-present: {skipped}; total target: {len(HEADERS)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
