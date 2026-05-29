using PalLLM.Domain.Integration;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Authoritative feature catalog. Every shipped capability has an
//            entry here with Id, Status, Summary, and Notes. Used by docs,
//            the dashboard, and /api/features.
//   surface: PalLlmFeatureCatalog.All (IReadOnlyList<FeatureDescriptor>).
//   gate:    Drift_Feature_catalog_count + Drift_Feature_status_split.
//            Adding/removing/changing-status of any entry forces a docs +
//            PROJECT_NUMBERS.json bump in the same commit.
//   adr:     None directly; entries reference their owning ADRs in Notes.
//   docs:    docs/ARCHITECTURE.md (feature inventory section), docs/API.md
//            (/api/features endpoint), docs/ROADMAP.md (status split).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

public static class PalLlmFeatureCatalog
{
    public static IReadOnlyList<FeatureDescriptor> All { get; } =
    [
        new FeatureDescriptor
        {
            Id = "portable-adapter-surface",
            Source = "PalLLM.Domain.Portable",
            Status = "ready",
            Summary = "PalLLM.Domain owns a self-contained portable adapter surface (IGameAdapter, ICharacter, IWorldClock, IPathProvider, ILogger, Vec3 plus deterministic SemanticEmbedder + ResponseCleanup helpers) so the runtime stays game-agnostic without requiring an external project.",
            Notes = "Lives in src/PalLLM.Domain/Portable/PortableAdapterContracts.cs - a single file, ~250 lines, no external dependencies, freely copy-able under the project's MIT license. Makes the published binary redistributable on its own (no sibling repo required at build time) and decouples PalLLM from any concurrent refactoring in external portable-adapter libraries. See docs/CORE_LIBRARY.md.",
        },
        new FeatureDescriptor
        {
            Id = "local-first-sidecar",
            Source = "PalLLM architecture",
            Status = "ready",
            Summary = "PalLLM runs as a local sidecar with optional HTTP chat-completions inference.",
            Notes = "Local-first posture: cloud inference is an opt-in, never a hard dependency. API and MCP JSON ingress is bounded by PalLLM:Http:ApiRequestBodyMaxBytes before model binding so oversized local or LAN callers fail with sanitized 413 ProblemDetails before expensive parsing or model work.",
        },
        new FeatureDescriptor
        {
            Id = "inference-defaults",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "PalLLM defaults to an illustrative self-hostable model tag and exposes generic sampling knobs for any HTTP chat-completions endpoint.",
            Notes = "The shipped default Model string is an operator-overridable placeholder; any HTTP endpoint that follows the JSON chat-completions schema is supported. Default sampling is tuned for mid-size instruction-tuned models, and the live runtime now enforces Palworld-specific per-turn prompt/evidence budgets so fast lanes stay lean while deliberate lanes keep more bridge, screenshot, and memory context.",
        },
        new FeatureDescriptor
        {
            Id = "conversation-memory",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Stores recalled conversation snippets using deterministic fallback embeddings plus a bounded exact-token rerank pass from the portable adapter library.",
            Notes = "Ready for dialogue memory now; exact-token reranking keeps named Palworld events, bosses, bases, and raids from losing tied embedding buckets. Deeper world memory can layer on later.",
        },
        new FeatureDescriptor
        {
            Id = "narrative-packs",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Loads narrative packs for authored personalities, lore, and seeded memories.",
            Notes = "Pack JSON lives under the PalLLM runtime folder so content stays local and moddable.",
        },
        new FeatureDescriptor
        {
            Id = "fallback-behavior-director",
            Source = "Research-backed hardcoded behaviors",
            Status = "ready",
            Summary = "PalLLM uses a deterministic fallback director for outage recovery and for routine low-cost tactical beats that do not need live inference.",
            Notes = "The fallback engine blends pacing, buddy support, replanning, traversal caution, morale, memory callbacks, base-network logistics planning, visual/audio presentation cues, and policy-based fast paths inspired by published game-AI talks and papers.",
        },
        new FeatureDescriptor
        {
            Id = "presentation-cue-planner",
            Source = "Hardcoded multimodal fallback layer",
            Status = "ready",
            Summary = "Every chat reply now carries deterministic visual and audio cue plans so Palworld clients can render readable presentation without another model call.",
            Notes = "Stealth, triage, regroup, capture, route guidance, camp ambience, and recovery all get phase-aware cue bundles with overlays for night, weather, morale, base defense, and memory echoes.",
        },
        new FeatureDescriptor
        {
            Id = "ue4ss-chat-hook",
            Source = "Palworld bridge",
            Status = "ready",
            Summary = "UE4SS Lua hook captures in-game chat and writes bridge events that the sidecar consumes.",
            Notes = "Uses the BroadcastChatMessage hook pattern documented by the Palworld modding community.",
        },
        new FeatureDescriptor
        {
            Id = "base-world-events",
            Source = "Palworld bridge",
            Status = "ready",
            Summary = "Bridge-driven base discovery is promoted into live world state, prompt context, and inspection surfaces.",
            Notes = "Base hooks now update known-base state, active-base IDs, recent world events, and the sidecar world endpoint, and chat requests sync the bridge inbox before planning so fresh world events can influence prompt building and deterministic fallback selection immediately.",
        },
        new FeatureDescriptor
        {
            Id = "memory-importance-scoring",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Every remembered entry carries a deterministic importance score; recall is a weighted blend of semantic similarity, recency, importance, and character affinity.",
            Notes = "The importance score is derived locally from content, role, and tags, so no LLM call is needed to compute it.",
        },
        new FeatureDescriptor
        {
            Id = "reflection-consolidation",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "When accumulated importance in the recent window crosses a threshold, a reflection memory consolidates the top salient moments into a single high-importance entry.",
            Notes = "Opt-in via PalLLM:Fallback:EnableReflection and manually triggerable via PalLlmRuntime.Reflect(). Uses a deterministic summariser so the local-first posture is preserved.",
        },
        new FeatureDescriptor
        {
            Id = "relationship-affinity",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Per-character affinity, mood, and tone tracker updated by a light sentiment heuristic on each player message; surfaced in the system prompt so replies read the room.",
            Notes = "Exposed via /api/relationships and /api/relationships/{id}. Affinity clamps to [-100, 100] with bounded deltas so a single exchange can't whiplash the relationship.",
        },
        new FeatureDescriptor
        {
            Id = "expanded-bridge-events",
            Source = "Palworld bridge",
            Status = "ready",
            Summary = "The runtime understands combat_start/end, pal_status, production, travel, weather_change, raid, and ui_probe events in addition to chat, snapshot, and base_discovered.",
            Notes = "World-state events write salient memory plus RecentEvents markers so fallback and prompt building see the latest live state. The new ui_probe event is diagnostic-only: it captures candidate UserWidget names, classes, and dump paths without polluting prompt memory. Current UE4SS Lua producers actively emit combat, pal_status, weather_change, raid, coarse live travel samples, and widget-probe summaries; production still mainly flows back through the guarded action executor's feedback path, so richer native production hooks are still pending.",
        },
        new FeatureDescriptor
        {
            Id = "widget-surface-probe",
            Source = "UE4SS UMG bridge",
            Status = "ready",
            Summary = "The UE4SS layer now watches UserWidget construct/destruct lifecycles and can emit bounded ui_probe snapshots plus dump files to discover real Palworld UI attachment points.",
            Notes = "This is the discovery seam for replacing generic screen messages with a native-feeling subtitle or HUD surface. The sidecar dashboard now exposes the last probe summary, reason, and dump path so widget targeting stays observable, the bridge proof surface now converts the ranked candidates into a concrete HUD bind recommendation shortlist plus proof-lane checklist, and scripts/apply-hud-bind-recommendation.ps1 can export that shortlist into config/native-hud.lua for the installed mod.",
        },
        new FeatureDescriptor
        {
            Id = "native-hud-attachment",
            Source = "PalLLM bridge + UE4SS UMG",
            Status = "scaffolded",
            Summary = "UE4SS Lua consumer has a native UMG widget attachment path that would render reply text through a configured Palworld widget - implemented but NOT player-facing yet because the operator-confirmed widget seam is not populated in ship configuration.",
            Notes = "Default OFF (native_hud_render_enabled=false, native_hud_widget_targets empty) so existing screen-message delivery stays authoritative until an operator confirms a stable widget seam via /api/bridge/ui-probe. The bridge now also supports config/native-hud.lua overrides beside the installed mod, reports the live config source/path in bridge_boot, and ships scripts/apply-hud-bind-recommendation.ps1 to write the ranked recommendation into that override file without hand-editing main.lua. When on, targets are tried in order, TextBlock-named-children are preferred, and a WidgetTree scan covers variable naming conventions. Any pcall failure degrades cleanly to the existing ClientMessage/PrintString path. Scaffolded status reflects the default-off posture: the code path exists and passes its own unit checks, but players do not experience a native HUD surface out of the box.",
        },
        new FeatureDescriptor
        {
            Id = "production-sampler",
            Source = "UE4SS BaseCampManager polling",
            Status = "scaffolded",
            Summary = "UE4SS layer ships a bounded base/station polling sampler that emits production bridge events when item+status tuples change - implemented but OFF by default pending hook-signature validation on a live build.",
            Notes = "Default OFF (production_sampler_enabled=false) because Palworld production-hook signatures vary by build. Poll cadence is 12s with a per-poll cap of 3 bases, and every engine lookup is pcall-guarded so a signature rename degrades to a no-op instead of throwing inside the LoopAsync tick. Emitted events flow through the existing ApplyProductionToSnapshot path so fallback and prompts see the latest production lane. Scaffolded status reflects the ship posture: a live session does not receive production events until an operator flips the kill-switch after validating the underlying `PalBaseCampManager` fields on the current Palworld build.",
        },
        new FeatureDescriptor
        {
            Id = "native-waypoint-marker-hint",
            Source = "Guarded action executor + PalMapManager",
            Status = "ready",
            Summary = "The waypoint_suggest guarded executor now attempts a best-effort PalMapManager waypoint-label hint before emitting bridge feedback.",
            Notes = "Cosmetic and safe by construction: no game-state mutation, and if the current build does not expose a compatible AddWaypointHint / SetPlayerWaypointLabel / NotifyWaypointHint API the executor records the skip reason in the trace note and continues down the existing feedback path. Operators can flip waypoint_native_marker_enabled off entirely in main.lua without touching the sidecar.",
        },
        new FeatureDescriptor
        {
            Id = "relationship-bounded-retention",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "RelationshipTracker now enforces a soft cap of 256 tracked relationships and prunes to 192 by lowest retention score when exceeded.",
            Notes = "Score blends recency, |affinity|, and interaction volume so strong bonds and recent activity are preserved while long-dormant transient ids age out first. The most recently recorded character id is always protected from eviction to prevent the caller from seeing its own record disappear.",
        },
        new FeatureDescriptor
        {
            Id = "outbox-return-channel",
            Source = "PalLLM bridge",
            Status = "ready",
            Summary = "Every successful chat response is written as a JSON envelope to Bridge/Outbox for in-game consumers to render without calling back into the sidecar.",
            Notes = "Endpoint: GET /api/bridge/outbox lists pending files; POST /api/bridge/outbox/clear empties the directory. Toggle via PalLLM:Bridge:OutboxEnabled.",
        },
        new FeatureDescriptor
        {
            Id = "advisory-action-intents",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Chat replies can carry safe, allowlisted action suggestions without granting the runtime permission to act on the game by itself. Intent emission on the sidecar is fully ready; native game-side execution coverage varies per type and is documented in the companion `native-waypoint-marker-hint` entry.",
            Notes = "Opt-in via PalLLM:Automation. Known suggestion types are waypoint_suggest, recall_pals, and request_craft_queue. The UE4SS consumer has a second-stage guarded executor with its own allowlist, kill switch, dry-run mode, and RequestId/strategy trace feedback written back through the bridge. Actual native game-side mutation is wired ONLY for `waypoint_suggest` (see native-waypoint-marker-hint); `recall_pals` and `request_craft_queue` currently emit bridge feedback events only - the party-state and crafting-station UE4SS calls are tracked in `docs/IMPLEMENTATION_QUEUE.md` Queue 3.",
        },
        new FeatureDescriptor
        {
            Id = "pack-validator",
            Source = "PalLLM tooling",
            Status = "ready",
            Summary = "POST /api/packs/validate returns structured per-field errors (missing ids, unknown character references, out-of-range importance/opinion, duplicate ids, and publication-safety findings) before a pack goes live.",
            Notes = "Complements the resilient per-file load path in NarrativePackService so content authors can debug without guessing at the schema. The validator also rejects obvious official-endorsement claims, unrelated third-party IP/vendor references, legal/IP/compliance overclaims, and broad multi-game platform language before shareable pack text reaches public surfaces.",
        },
        new FeatureDescriptor
        {
            Id = "task-focus-directive",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Optional system-prompt directive nudging the model to stay task-focused while remaining in character.",
            Notes = "Toggle via PalLLM:Fallback:PreferTaskFocus. Off by default to preserve existing roleplay feel; flip on for quest/utility companions.",
        },
        new FeatureDescriptor
        {
            Id = "vision-augmentation",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Optional vision pipeline that talks to any HTTP multimodal endpoint following the OpenAI-style chat-completions schema with image_url content parts.",
            Notes = "Endpoints: POST /api/vision/describe (freeform), POST /api/vision/world-state (structured JSON that optionally merges into the live snapshot). ChatRequest.ImageBase64 splices a one-sentence visual summary into the system prompt. Outgoing image_url parts can carry stable palllm-image-sha256-* media-cache ids for vLLM-compatible repeated screenshot lanes, with PalLLM:Vision:UseMediaCacheIds=false as the strict-endpoint opt-out. PalLLM:Vision:MultimodalProcessor can emit vLLM-style mm_processor_kwargs (min/max pixels, max_soft_tokens, fps) only after endpoint proof. Off by default; configure PalLLM:Vision to enable.",
        },
        new FeatureDescriptor
        {
            Id = "structured-vision-outputs",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "World-state extraction forwards an OpenAI-style response_format json_schema so endpoints that support structured outputs (llama.cpp, LM Studio, vLLM, OpenAI-compatible servers in general) constrain the model to the PalLLM world-state schema instead of returning prose.",
            Notes = "Toggle via PalLLM:Vision:UseStructuredOutputs (default true). Endpoints that do not recognise response_format silently ignore the field, and the orchestrator's graceful-fail JSON parser still handles narrated or fenced output. Two regression tests pin the outgoing request body shape.",
        },
        new FeatureDescriptor
        {
            Id = "screenshot-watcher",
            Source = "PalLLM vision + UE4SS bridge",
            Status = "ready",
            Summary = "Background watcher polls Bridge/Screenshots and feeds each PNG through the vision world-state extractor, merging the result into the live snapshot as a complementary sensor to UE4SS hooks.",
            Notes = "Enable via PalLLM:Vision:EnableScreenshotWatcher. UE4SS Lua producer writes screenshots at a configurable cadence (default 20s). Archive/fail routing follows the same pattern as the bridge inbox.",
        },
        new FeatureDescriptor
        {
            Id = "session-persistence",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Memory stream and per-character relationships serialise to a single JSON file under the runtime root, auto-load on startup, and autosave on a configurable interval.",
            Notes = "Endpoints: POST /api/session/save, POST /api/session/reload. Writes go through a temp-file-rename to survive crashes mid-write. Toggle via PalLLM:Session:Enabled and EnableAutosave.",
        },
        new FeatureDescriptor
        {
            Id = "token-usage-accounting",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Prompt, completion, and total tokens reported by the inference endpoint are accumulated into RuntimeHealth so cost and throughput stay observable.",
            Notes = "Fallback replies and bypassed inference do not consume tokens and therefore do not contribute to the counters. Vision and TTS call counts are reported alongside.",
        },
        new FeatureDescriptor
        {
            Id = "bounded-directory-retention",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Outbox, archive, failed, TTS, and pending-screenshot directories are bounded by file-count + age so long-running sessions stay low-latency and disk-safe.",
            Notes = "Configurable via BridgeOptions.OutboxMax{Files,AgeHours}, ArchiveMax{Files,AgeHours}, FailedMax{Files,AgeHours}, TtsOptions.MaxStored{Files,AgeHours}, and VisionOptions.PendingScreenshotMax{Files,AgeHours}. Pruning runs inline with writes or worker passes so cleanup does not depend on an eventual full sweep.",
        },
        new FeatureDescriptor
        {
            Id = "bounded-worker-chunks",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Bridge and screenshot workers process bounded chunks per poll instead of trying to clear entire backlogs in one pass.",
            Notes = "Configurable via BridgeOptions.MaxEventsPerPoll and VisionOptions.MaxScreenshotsPerPoll. Manual drains still support the full queue; the background workers use chunking to preserve steady latency under backlog.",
        },
        new FeatureDescriptor
        {
            Id = "dirty-tracking-autosave",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Session autosave is version-aware: if the memory store and relationship tracker have not mutated since the last save, the worker skips the disk write entirely.",
            Notes = "Every Remember / Import / RecordInteraction increments an internal mutation version. SessionPersistence compares against the last saved version and no-ops when clean. Turns a quiet 60s autosave cycle into zero I/O.",
        },
        new FeatureDescriptor
        {
            Id = "resource-observability",
            Source = "RuntimeHealth surface",
            Status = "ready",
            Summary = "RuntimeHealth exposes disk footprint counts (inbox / outbox / screenshots / archive / failed) and session dirty flag + last-saved timestamp so operators can spot leaks without poking the filesystem.",
            Notes = "Counts refresh on a short cached cadence inside RuntimeHealth and are explicitly invalidated after runtime writes that change the tracked directories. File enumeration tolerates missing directories and transient I/O errors without throwing.",
        },
        new FeatureDescriptor
        {
            Id = "bridge-loop-proof",
            Source = "PalLLM bridge observability",
            Status = "ready",
            Summary = "PalLLM tracks the live request -> outbox -> visible-delivery -> optional speech playback/native-mixer callback -> feedback loop so operators can tell whether a Palworld chat turn actually rendered, spoke, and closed the bridge loop.",
            Notes = "RuntimeHealth.BridgeLoop and BridgeActivitySnapshot.LoopProof surface the last ingress, outbox reply, delivery event, speech playback event, and matching action feedback with a compact status state machine (`idle`, `awaiting_reply`, `awaiting_delivery`, `awaiting_speech_playback`, `awaiting_action_feedback`, `speech_playback_failed`, `closed`, and mismatch states). The speech receipt is content-free: request id, playback mode/hint, MIME/extension, artifact byte count, WAV or raw-PCM encoding/sample-format/byte-order/native-mixer-conversion/native-mixer-queue/native-mixer-buffer-duration/sample-rate/channel/bit-depth/duration/byte-rate/block-align/audio-data-size/sample-frame-count/partial-frame-remainder/valid-bits/channel-mask metadata when available, playback sequence, superseded request id/count/age/prior-buffer/estimated-remaining-buffer, cancellation mode, launch attempt count, helper-launch elapsed milliseconds, started/skipped state, stable failure code, and reason only - no audio bytes or local file path. The loop proof also derives `SpeechPlaybackIngressLagMs`, `SpeechPlaybackOutboxLagMs`, and `SpeechPlaybackDeliveryLagMs` from existing event timestamps so request-to-speech, outbox-to-speech, and visible-delivery-to-speech lag are measurable without adding audio content or paths to bridge payloads. The Lua bridge rejects unreadable, empty, invalid WAV, unsupported WAV encodings, block-alignment mismatches, unsupported containers, duplicate-window, launch-failed, raw PCM partial-frame mismatches, and proof-only raw PCM artifacts before claiming helper playback, so proof cannot claim playback started for a zero-byte TTS file, a float/compressed WAV, a partial PCM frame, or a `.pcm` artifact that still needs native mixer binding. `/api/bridge/proof` also exposes a separate `native_audio_mixer` lane, making raw PCM promotion blockers visible without overloading the generic `speech_playback` lane. scripts/doctor.ps1 reports bridge proof separately from NativeReadiness, scripts/run-sidecar-smoke.ps1 injects synthetic delivery + feedback events and persists a machine-readable smoke artifact under Runtime/ReleaseEvidence, scripts/run-native-proof.ps1 polls the live Palworld bridge until native HUD readiness plus visible in-game delivery are proven and then persists a separate native-proof artifact alongside the smoke evidence, local native-proof runs fail fast with a blocked artifact when the Palworld process is not running, native-proof artifacts now include watcher timing, poll settings, poll count, completion reason, timeout state, stable diagnosis code/summary/action/command fields, and a bounded status-transition trail, and scripts/export-release-proof-bundle.ps1 packages the current bridge proof, inference-performance receipt snapshot with response/finish/token counts, and both artifacts into one durable validation bundle.",
        },
        new FeatureDescriptor
        {
            Id = "bridge-proof-snapshot",
            Source = "PalLLM sidecar inspection surface",
            Status = "ready",
            Summary = "GET /api/bridge/proof exposes a single machine-readable Palworld bridge proof snapshot so tools can read native readiness, widget-seam evidence, and live request/delivery closure without stitching multiple endpoints together.",
            Notes = "The snapshot is built from the live RuntimeHealth + BridgeActivity surfaces and adds a release-friendly progress state (`awaiting_bridge_boot`, `awaiting_ui_probe_capture`, `ready_for_hud_bind`, `awaiting_speech_playback`, `delivery_proven`, mismatch states, and more), explicit ready evidence, current blockers, the next recommended operator step, the exact bridge-reported `native_hud_widget_targets`, the live native-hud config source/path, a speech_playback proof lane for TTS artifacts, a native_audio_mixer proof lane for raw PCM promotion blockers, speech playback ingress/outbox/delivery lag receipts, and a ranked HUD bind recommendation block. It follows the same ETag/private-cache contract as /api/features and now feeds the durable SmokeEvidence, NativeProofEvidence, and ProofBundleEvidence blocks on /api/release/readiness without server-side output caching that could hide a newly captured proof run. Release readiness treats a native-proof artifact that claims `proven` as invalid unless the same artifact also carries `delivery_proven` bridge status, live-delivery proof, and native-HUD bind readiness, and it now exposes the native-proof watcher's timing, poll count, timeout state, completion reason, diagnosis code/summary/action/command, and status-transition trail when present.",
        },
        new FeatureDescriptor
        {
            Id = "release-package-verification",
            Source = "PalLLM release tooling",
            Status = "ready",
            Summary = "Release packaging now emits a manifest-backed package layout and can verify a concrete PalLLM zip or expanded release directory before clean-machine install validation begins.",
            Notes = "scripts/package-release.ps1 now writes RELEASE_PACKAGE_MANIFEST.json into the package root, includes the HUD/proof helper scripts the player-support flow actually needs, bundles a self-contained sidecar by default for the packaged play.bat flow, and can immediately verify the finished zip. scripts/verify-release-package.ps1 validates required files, manifest-declared hashes, sidecar publish flags, package shape, and publication-surface hygiene for sibling-project bleed, endorsement claims, unrelated franchise references, broad platform scope drift, and root player-copy brand drift. scripts/compute-release-checksums.ps1 now also persists ArtifactIntegrityEvidence after writing SHA256SUMS, SHA512SUMS, and checksums.json, so /api/release/readiness can report whether digest manifests and detached-signature sidecars exist for the candidate zip. The proof/support bundle exporters reuse the same scanner for portable evidence archives, privacy-redact staged text before archiving, and persist PrivacyRedactionApplied, PrivacyRedactionCheckedFileCount, PrivacyRedactionRedactedFileCount, PrivacyRedactionRuleHits, PublicationScanPassed, PublicationScanCheckedFileCount, and PublicationScanViolations in their manifests. /api/release/readiness exposes the same evidence blocks, adds an exact Publication.NextRecommendedCommand beside the prose next pass, and now verifies the paired proof/support zip is readable, contains its own manifest, includes the manifest-listed entries, avoids duplicate normalized file entries, uses only relative path-safe entry names, and carries an archived manifest whose promotion/status, native-HUD config, optional-file, blocker, and ready-evidence fields match the sidecar-readable manifest before treating a portable handoff bundle as recorded, so automation can tell whether a concrete candidate package and its portable handoff bundles have been structurally verified, privacy-redacted, path-safe, checksum-covered, internally consistent, and publication-scan clean, not just whether smoke/native proof exists.",
        },
        new FeatureDescriptor
        {
            Id = "one-click-player-launcher",
            Source = "PalLLM player tooling",
            Status = "ready",
            Summary = "Released builds now expose a single primary player launcher that installs or refreshes the mod, starts or reuses the sidecar with the best available packaged fallback, primes the active inference lane when warmup is enabled, runs doctor, opens the dashboard, and launches Palworld.",
            Notes = "play.bat is the package-root entry point; scripts/play-palllm.ps1 owns the orchestration. Release packaging now ships a packaged self-contained sidecar exe by default, and the launch chain still falls back to a packaged DLL plus dotnet, then to the repo project when needed. The Field Console physical-file fallback routes now emit metadata-keyed weak content-hash ETags and Last-Modified headers, so packaged-player browsers can revalidate the dashboard shell cheaply even when the self-contained EXE path cannot rely on the normal static-web-assets manifest. The launcher treats warmup as best-effort so deterministic fallback still keeps PalLLM usable if live inference is disabled or cold, persists Runtime/LaunchEvidence/latest-player-launch.json plus .md for support/debugging, and is paired with support.bat plus scripts/export-support-bundle.ps1 for one-click evidence capture. install.bat remains available as the lower-level manual installer for support and debugging.",
        },
        new FeatureDescriptor
        {
            Id = "release-full-audit-evidence",
            Source = "PalLLM release tooling",
            Status = "ready",
            Summary = "scripts/run_full_audit.ps1 now persists a durable latest-full-audit artifact so the sidecar can expose fresh build, test, drift, and packaging truth through /api/release/readiness.",
            Notes = "The durable artifact lives at Runtime/ReleaseEvidence/latest-full-audit.json with history copies under Runtime/ReleaseEvidence/History, points back to the timestamped artifacts/full-audit/<stamp>/ bundle, and marks whether tests, coverage, SBOM, and packaging were enabled. This lets release-readiness reason about source-code quality gates and Palworld runtime proof in one machine-readable surface instead of forcing operators to inspect repo-local audit folders manually.",
        },
        new FeatureDescriptor
        {
            Id = "inference-circuit-breaker",
            Source = "classic resilience pattern + PalLLM runtime",
            Status = "ready",
            Summary = "Three-state circuit breaker (Closed / Open / HalfOpen) on the inference client short-circuits failing endpoints, routing chat traffic straight to fallback instead of burning timeout on every call.",
            Notes = "Configurable via InferenceOptions.CircuitBreakerFailureThreshold (default 5) and CircuitBreakerCooldownSeconds (default 30). State + consecutive-failure count surface in RuntimeHealth.",
        },
        new FeatureDescriptor
        {
            Id = "correlation-ids",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Every chat turn gets a short correlation id (auto-generated if the caller doesn't supply one), echoed into ChatResponse and the outbox envelope so a log line can be paired with an in-game render.",
            Notes = "Supply via ChatRequest.RequestId; caller-supplied ids are preserved verbatim so an upstream tool can use its own tracing conventions.",
        },
        new FeatureDescriptor
        {
            Id = "chat-rate-limiter",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Per-character sliding-window rate limit for chat requests. Exceeded buckets serve deterministic fallback instead of paying inference, so a runaway producer cannot drain the budget.",
            Notes = "Off by default (MaxCharacterRequestsPerMinute=0). Rate-limited turns tag ResponsePath=\"rate_limited_fallback\" and increment RuntimeHealth.RateLimitedCount.",
        },
        new FeatureDescriptor
        {
            Id = "prometheus-metrics",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GET /metrics renders Prometheus exposition format (counters + gauges) from the runtime health snapshot - no client library, no extra dependency.",
            Notes = "Designed for single-tenant scrape. Metrics have no per-request labels to keep cardinality bounded. Pair with Grafana Agent or the Prometheus operator for dashboards.",
        },
        new FeatureDescriptor
        {
            Id = "tts-interface",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pluggable ITtsClient with a default HTTP adapter that supports legacy POST { text, voice }, opt-in OpenAI-compatible /v1/audio/speech request bodies, and proof-gated raw PCM native-mixer callbacks. Off by default; writes synthesised audio to runtime-root/TTS or returns base64 inline.",
            Notes = "Endpoint: POST /api/tts/synthesize. Tts.RequestFormat=simple preserves the old local adapter shape; RequestFormat=openai_speech sends input/voice/model/response_format for current speech APIs. Missing/generic speech response content types fall back to Tts.ResponseFormat so pcm/flac/mp3/opus/aac lanes keep correct file and playback hints. The runtime routes parameterized raw PCM content types by media-type base, so values such as `audio/L16; rate=24000; channels=1` still write `.pcm` and emit `PlaybackHint=raw_pcm`. The UE4SS bridge now emits content-free speech_playback receipts after local helper attempts or proof-only skips, including artifact byte count, WAV or raw-PCM encoding/sample-format/byte-order/native-mixer-conversion/native-mixer-queue/native-mixer-buffer-duration/sample-rate/channel/bit-depth/duration/byte-rate/block-align/audio-data-size/sample-frame-count/partial-frame-remainder/valid-bits/channel-mask metadata when available, playback sequence, superseded request id/count/age/prior-buffer/estimated-remaining-buffer, cancellation mode, launch attempt count, helper-launch elapsed milliseconds, stable failure code, and preflight failure reasons for unreadable, empty, invalid WAV, unsupported WAV encoding, block-alignment mismatch, unsupported container, duplicate-window, launch-failed, or raw PCM files, letting /api/bridge/proof distinguish pending, started, skipped helper playback, stale-speech supersession, prior-buffer overlap, request-to-speech/outbox-to-speech/delivery-to-speech lag, and `.pcm` native-mixer blockers without persisting audio bytes or file paths. `/api/bridge/proof` now splits those `.pcm` blockers into a dedicated `native_audio_mixer` lane so dashboards can see whether raw PCM is merely skipped helper playback or still missing a native mixer receipt. The Lua playback resolver honors the same compressed media-player containers as the runtime hint surface: mp3, m4a, aac, wma, ogg, opus, and flac; raw PCM (`.pcm`, `audio/pcm`, `audio/l16`) reports `PlaybackMode=raw_pcm`, `FailureCode=raw_pcm_native_mixer_required`, zero launch attempts, optional MIME-parameter timing plus sample-format/byte-order/native-mixer-conversion/native-mixer-queue/native-mixer-buffer-duration metadata, and `speech raw pcm requires native mixer binding` until a native mixer seam is proven; partial raw frames report `raw_pcm_block_alignment_invalid`. Hard cap on input length (MaxCharacters), response bytes, and timeout configurable via TtsOptions.",
        },
        new FeatureDescriptor
        {
            Id = "asr-transcription-interface",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Opt-in OpenAI-compatible ASR proof lane accepts bounded local audio and returns transcript text without changing ordinary typed chat.",
            Notes = "Endpoint: POST /api/audio/transcribe. HttpAudioTranscriptionClient sends multipart/form-data to PalLLM:Asr:BaseUrl, forwards model/language/prompt hints (request values override optional configured ASR defaults) plus optional chunking_strategy=auto, temperature, vLLM seed replay canaries, and logprob confidence probes when configured, and sends an allowlisted ASR response_format (`json` by default, endpoint-proven `verbose_json` for metadata canaries). The chunking strategy is empty by default and startup-validated to `auto` only, while seed stays null by default, so strict local ASR servers stay field-free until server/VAD chunking or replay has been measured. When verbose_json is selected it can also send allowlisted timestamp_granularities[] (`segment`, `word`) and reduce returned segment/word timing metadata to content-free counts, duration/coverage fields, and review flags. Verbose segment quality metadata is also reduced to content-free counts for avg_logprob, compression_ratio, no_speech_prob, and segment temperature, with review flags for low average logprob, high compression ratio, and silent-segment candidates. It parses the standard `{ text }` response and keeps audio byte caps, response byte caps, transcript caps, timeout, auth, air-gap posture, privacy posture, resource budgets, and sanitized failures explicit. Callers can attach content-free Endpointing timing metadata (speech/leading/trailing silence, close reason, barge-in flag); responses also preserve sanitized upstream request-id and processing/phase-timing receipts from compatible headers. RuntimeHealth, /metrics, and proof-bundle manifests count VAD/turn-close receipts, confidence receipts, timing receipts, quality receipts, and upstream ASR receipt evidence without storing raw audio, token text, prompt hints, transcript text, upstream segment/word text, verbose JSON, or upstream logs. Default off so the install remains local and typed/fallback-first until a player-speech lane is measured.",
        },
        new FeatureDescriptor
        {
            Id = "container-image",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Production-shaped Dockerfile at the repo root ships a multi-stage, non-root, .NET 10 ASP.NET Core image for hosting the sidecar on a remote server.",
            Notes = "Self-contained build: `docker build -t palllm:latest .` from the repo root - no sibling checkout required because the portable adapter surface is inlined at src/PalLLM.Domain/Portable/PortableAdapterContracts.cs. Runtime root under /var/palllm is a volume mount. Any PalLLM:* config value can be overridden via the PalLLM__* env var convention. See docs/OPERATIONS.md Sec. Container deployment.",
        },
        new FeatureDescriptor
        {
            Id = "emergency-fallback-tier",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Third-tier safety net around the deterministic fallback director. If a strategy throws or returns an empty message, EmergencyFallback hands the player a canned neutral acknowledgement instead of crashing the chat turn.",
            Notes = "Wrapped around every _fallbackBehaviorEngine.Generate() call site in PalLlmRuntime.ChatAsync. Tags emergency messages with StrategyId=\"emergency-recovery\" so operators can count the tier in the existing palllm_fallback_strategy_total{strategy=\"emergency-recovery\"} metric without new plumbing. Five-message rotation indexed by Environment.TickCount64 so repeated emergencies don't all read identically. Portable: zero dependencies beyond the BCL, sits in PalLLM.Domain.Runtime, harvestable into any other LLM companion runtime.",
        },
        new FeatureDescriptor
        {
            Id = "promotion-apply-preview-builder",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Turns a Pass-12 PromotionSuggestion into a concrete editor-ready change template. For each known task class (fallback-director, duo-*, live-inference, duo-disagreement-detector, rate-limiter, model-tier-transition) emits a multi-line DiffPreview with file path + before-context + after-code, a capped SafetyWarnings list (up to 3), a single-line git-checkout RollbackCommand, and a ProofPacket tagged subsystem=promotion-apply-preview.",
            Notes = "Deterministic, no file reads, no inference call. The DiffPreview is intentionally descriptive rather than a real unified diff so it can't drift with live repo contents. Unknown task classes fall through to a generic template that still produces a usable preview — consistent with every other deterministic surface. Closes the full loop: Pass 10 ledger, Pass 11 auto-feed, Pass 12 suggestions, Pass 14 editor-ready template.",
        },
        new FeatureDescriptor
        {
            Id = "promotion-apply-preview-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "POST /api/promotion/apply/preview accepts { TaskClass, PatternId? } and returns the editor-ready change template. 400 on missing TaskClass / 404 if not yet observed / 409 if observed but not a candidate yet / 200 with the preview otherwise. pal_promotion_apply_preview MCP tool wraps the same surface for AI clients.",
            Notes = "ProblemDetails responses use the same content-type + shape as every other input-validation error on the sidecar, so MCP clients can surface a consistent 'why did it reject?' message. If PatternId is omitted, the server defaults to the candidate's most-common pattern so the happy path is a one-field request.",
        },
        new FeatureDescriptor
        {
            Id = "hardware-profiler",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 25 / D1 — deterministic, dependency-free hardware profiler. Captures OS, logical core count, rounded RAM GiB, GPU-likelihood (via env-var cues + driver markers), and derives a DuoHardwareTier recommendation. Surfaced via GET /api/hardware, pal_hardware_profile MCP tool, and honours PalLLM:Hardware:ForceTier override.",
            Notes = "No subprocess launch, no network, no GPU library load. Safe to call on hot paths. Fallback confidence='low' on unknown OS with no gpu cue. Pairs with ModelRoleRegistry + DuoOrchestratorPlanner so operators can answer 'which tier should I bind?' without guessing. Apple Silicon / ROCm / ARM platforms covered by the marker list.",
        },
        new FeatureDescriptor
        {
            Id = "hardware-profile-ttl-cache",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "TTL-cache wrapper around HardwareProfiler.Capture so /api/hardware, /api/degradation/advisory, and the two MCP tool handlers no longer pay the 5-20ms OS + driver-marker probe cost on every hit. Cache keyed on the forceTier override so operator overrides bypass cached entries correctly.",
            Notes = "5-minute default TTL. Hardware posture is boot-stable (cores, RAM, driver presence don't change mid-process), so caching is safe. Volatile-read + atomic swap pattern; a brief cold-start double-compute is harmless because the result is deterministic. Same pattern mirrored by privacy-posture-ttl-cache. See docs/DESIGN_PRINCIPLES.md § 8 'Cache TTLs over recomputation on hot paths'.",
        },
        new FeatureDescriptor
        {
            Id = "privacy-posture-ttl-cache",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "TTL-cache wrapper around PrivacyPostureBuilder.Capture so /api/privacy/posture + pal_privacy_posture MCP tool no longer recompose the 16-surface list on every hit. Cache keyed on a compact signature of the relevant options (Inference/Vision/TTS enabled + BaseUrl, OTLP env-var presence) so a config change invalidates automatically.",
            Notes = "30-second default TTL. Shorter than HardwareProfiler's TTL because privacy posture can shift with options reload; the signature-based invalidation makes this safe within a single process. Pattern matches hardware-profile-ttl-cache. See docs/DESIGN_PRINCIPLES.md § 8.",
        },
        new FeatureDescriptor
        {
            Id = "resource-budget-ttl-cache",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "TTL-cache wrapper around ResourceBudgetPostureBuilder.Capture so /api/budgets + pal_resource_budgets MCP tool no longer recompose the 9-entry budget list on every dashboard-poll hit. Cache keyed on a compact signature of relevant options + metrics (Vision/TTS enabled, numeric budgets, chat total + fallback total, fallback-share tier boundary).",
            Notes = "15-second default TTL — shorter than the other two caches because metrics drift faster than options. The signature explicitly includes the fallback-share > 75% boundary as a boolean so the 'ok'→'review' flip on that row always bypasses cached entries. Third application of the pattern (after HardwareProfiler and PrivacyPostureBuilder). See docs/DESIGN_PRINCIPLES.md § 8 and docs/ADVISORS.md.",
        },
        new FeatureDescriptor
        {
            Id = "airgap-verifier-ttl-cache",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "TTL-cache wrapper around AirGapVerifier.Verify so /api/airgap/verify + pal_airgap_verify MCP tool + the /api/describe self-description no longer re-classify every outbound surface on every hit. Signature includes Inference/Vision/TTS enable+BaseUrl, OTLP env-var, and every configured upstream MCP server so any config flip invalidates automatically.",
            Notes = "30-second default TTL. Fourth and final application of the TTL-cache pattern across PalLLM's read-heavy posture surfaces — the complete set (HardwareProfiler + PrivacyPostureBuilder + ResourceBudgetPostureBuilder + AirGapVerifier) now all follow the signature-based invalidation shape documented in docs/DESIGN_PRINCIPLES.md § 8. Cache state is a volatile field with atomic swap — no locks on the hot path; benign double-compute on cold start is harmless because the result is deterministic.",
        },
        new FeatureDescriptor
        {
            Id = "compatibility-matrix",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 42 / D9 — docs/COMPATIBILITY.md + scripts/compatibility.json describe known-good and known-conflicting Palworld / UE4SS / .NET / mod / AV combos. Data-driven so new entries land by JSON patch, not code.",
            Notes = "Consumed by `scripts/doctor.ps1` at run time to warn operators about known conflicts and suggest mitigations. Schema v1 covers palworld/ue4ss/dotnet version ranges, OS matrix (sidecar + mod), known-good mods, known conflicts (mod + tooling), and inference-endpoint reference URLs with status.",
        },
        new FeatureDescriptor
        {
            Id = "release-checksums-helper",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 41 / E2 — `scripts/compute-release-checksums.ps1` walks artifacts/packaging/ and writes SHA256SUMS + SHA512SUMS + checksums.json with canonical LF line endings and sort-stable ordering. Release-signing policy + minisign / GPG workflow documented in docs/RELEASE_SIGNING.md.",
            Notes = "Digests are always produced by the build; signatures (minisign or GPG) are opt-in by maintainer. Code-signing the EXE is a separate documented step. Consumers verify via `sha256sum -c SHA256SUMS` or `minisign -V -p <pub> -m SHA256SUMS`. Integrates with the existing release-readiness surface: a future pass can ingest checksums.json into /api/release/readiness so a stale digest is caught by CI.",
        },
        new FeatureDescriptor
        {
            Id = "lifetime-relationship-aggregator",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 40 / C8 — LifetimeRelationshipAggregator rolls per-session CharacterRelationship records into a cross-session aggregate (first-seen, last-seen, session-count, peak/floor affinity, cumulative average, mood-tally). Renders a life-story summary for each character. Surfaced via GET /api/relationships/lifetime.",
            Notes = "Pure function — Merge() takes a prior aggregate + the current session's relationships and returns a new aggregate (no in-place mutation). Persistence lives under PalSavedRoot/Runtime/LifetimeRelationships/latest.json; reads and writes are the caller's responsibility so test fixtures stay clean. Empty/malformed JSON falls back to an empty aggregate so the endpoint never 500s on a fresh install.",
        },
        new FeatureDescriptor
        {
            Id = "personality-pack-v1-format",
            Source = "PalLLM runtime",
            Status = "ready",
                Summary = "Pass 39 / C6 — PersonalityPack v1 format: a local pack directory with pack.json manifest + prompt.md + optional voice-hint.md / voice-ref audio with VoiceConsent provenance / portrait.png / audio / local .safetensors adapter metadata. Ships with PersonalityPackValidator (bounded manifest ingress, schema checks, prompt and audio-asset budgets, root-contained tracked paths, audio and adapter extension allowlists, publication-safety scan, streaming content-hash verification) so an operator-installed pack can't read outside its directory, publish obvious unsafe copy, or tamper with on-disk content without detection.",
                Notes = "Deterministic — no signature verification, no download path. Packs are hand-copied into the runtime packs directory (by default %LOCALAPPDATA%/Pal/Saved/PalLLM/Packs/) or any other local directory an operator stages before validation. pack.json is capped at 65536 bytes, every tracked file path must resolve back under the pack root, optional VoiceRefPath and AudioSamples are capped at 10 MiB per file before hash acceptance, VoiceRefPath requires VoiceConsent (self_recorded, licensed, synthetic, or public_domain), optional VoiceConsentNotes is capped at 1024 characters, VoiceRefPath and LoraAdapterPath reject remote URLs and are hash-covered, MemoryNamespace is a kebab-case identity only, prompt/manifest text is scanned for obvious official-endorsement, unrelated IP/vendor, legal/IP/compliance overclaims, and broad-scope claims, and ContentHash is recomputed from sorted-path file bytes with byte separators through a streaming SHA-256 pass rather than full-file buffering. Pure local: no external dependencies beyond System.Security.Cryptography. See src/PalLLM.Domain/Packs/PersonalityPack.cs for the authoritative format.",
        },
        new FeatureDescriptor
        {
            Id = "species-personality-resolver",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 315 — per-species personality-pack resolver. Operators map Palworld species names to personality-pack ids once in PalLlmOptions:Packs:DefaultBySpecies, and the resolver picks the right pack for any character of that species. Surfaced via GET /api/packs/resolve + pal_personality_for_species MCP tool. Dispatch order: species default → caller-supplied fallback → none.",
            Notes = "Pure function — no inference, no IO, no state. Keys and values are trimmed; keys matched case-insensitively; blank entries silently skipped; never throws. Closes the per-species personality gap that previously required authoring one pack per character id (so every same-species companion can share one default voice instead of needing one pack per tame). The PersonalityPack format itself stays per-character; this advisor is the lookup table that decides which pack to apply when no per-character override is in play. See src/PalLLM.Domain/Packs/SpeciesPersonalityResolver.cs for the implementation and tests/PalLLM.Tests/SpeciesPersonalityResolverTests.cs for the 19 focused regression tests pinning every branch.",
        },
        new FeatureDescriptor
        {
            Id = "mood-weather-advisor",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 38 / C10 — deterministic per-character mood forecast that blends affinity + last-tone + world snapshot (threat, player HP, time-of-day) into a short mood / weather-metaphor / tone triple. Surfaced via GET /api/characters/{id}/mood + pal_mood_weather MCP tool.",
            Notes = "Pure function — no inference call, no mutable state. Priority order: combat/low-HP → affinity tier → night-time softening → recent-tone nudge. Rendered as a dashboard pill (mood + weather) and injected into chat prompts as one extra system-prompt line. 404 when no relationship record exists for the character id yet (chat at least once to create one).",
        },
        new FeatureDescriptor
        {
            Id = "world-narration-advisor",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 36 / C2 — deterministic advisor that decides whether the current world snapshot warrants a companion's one-line narration quip. Triggers: combat-start, threat-spike, night-fall, weather-change, low-health, objective-update. Returns ShouldNarrate + Trigger + PromptFragment + MinimumGapSeconds + Reason. Surfaced via GET /api/narration/cue + pal_narration_cue MCP tool.",
            Notes = "Pure function, no inference call. Rate-limit is enforced by the caller using MinimumGapSeconds (default 90s). A future background narrator worker can poll this on a cadence and fan valid cues out through /api/chat. The advisor returns trigger='rate-limited' when called inside the gap so the caller doesn't have to track its own clock.",
        },
        new FeatureDescriptor
        {
            Id = "resource-budget-posture",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 35 / D10 — single-shot view of every tracked runtime budget: inference rate limit, circuit breaker, vision queue depth, TTS character cap, memory window, bridge outbox retention, chat fallback share. Each entry carries budget / current / config key / status (ok / review / exhausted / unknown) / notes. Surfaced via GET /api/budgets + pal_resource_budgets MCP tool.",
            Notes = "Pure function over PalLlmOptions + PalLlmMetricsSnapshot. Never mutates counters. High fallback share on a live-configured install flips the chat-fallback-share row to 'review' so operators catch live-lane regressions without deep-diving the Prometheus scrape. Disabled features collapse their rows to a single 'surface-off' status so the posture never lies about consumption.",
        },
        new FeatureDescriptor
        {
            Id = "party-chat-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 34 / C1 — POST /api/chat/party fans a single utterance out across multiple character ids in order. Each per-character turn runs through the existing ChatAsync pipeline so task-aware execution profiles, the Pass-8 planner, rate limiting, and deterministic fallback all apply per-turn. Threaded mode seeds each turn with a summary of earlier replies so a conversation forms.",
            Notes = "Deliberately NOT a breaking change on ChatRequest — party chat is a sibling orchestration endpoint so single-character callers see zero behaviour change. Rate-limited via 'chat-heavy' bucket just like /api/chat. Returns { PartyId, Turns[], Threaded, TotalLatencyMs, FallbackTurnCount, CapturedAtUtc } where each Turn carries the full per-character ChatResponse. Empty CharacterIds[] returns ProblemDetails 400.",
        },
        new FeatureDescriptor
        {
            Id = "graceful-degradation-advisor",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 33 / D2 — inspects HardwareProfile + PalLlmOptions and recommends a posture (CpuOnlyConstrained / CpuOnlyCapable / GpuEntry / Standard / NoDegradation) with ordered recommendations for inference model size, vision, TTS, and role bindings. Answers 'my laptop has no GPU, can I still play?' Surfaced via GET /api/degradation/advisory + pal_degradation_advisory MCP tool.",
            Notes = "Pure advisory — never mutates runtime state, so opting out is always possible. Pairs with HardwareProfiler: one reports what's on the box, this one reports what to DO about it. Verbs (keep/disable/review/opt-in/leave-off) are stable so a future dashboard chip can colour-code each recommendation.",
        },
        new FeatureDescriptor
        {
            Id = "directive-intent-translator",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 31 / C3 — deterministic translator that converts player chat ('hey helper, stop mining and help me fight') into an ordered PalDirective[] the UE4SS mod can forward to the native pal-AI controller. Recognises 8 intents: stop_current_task, recall_pals, help_in_combat, gather_resources, request_craft_queue, mark_waypoint, follow_player, guard_position. Surfaced via POST /api/directives/plan + pal_directives_plan MCP tool.",
            Notes = "NEVER emits above PalLLM:Automation:AllowedActions — cues that match an unallowlisted action land in RejectedCandidates[] with a reason. Pure function (no inference). Order-preserving: earlier candidates win on conflict, so 'stop and follow' → [stop_current_task, follow_player] with matching priority. Pairs with the existing ActionIntentPlanner — this one parses the player's intent, that one plans the companion's own action suggestions.",
        },
        new FeatureDescriptor
        {
            Id = "pal-vision-describe-mcp",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 30 / C5 — pal_vision_describe MCP tool. On-demand describe that wraps POST /api/vision/describe so an MCP client can push a base64 image and get a plain-text scene description in one call. Returns status='disabled' when vision is off, status='failed' with diagnostic on upstream failure, or the full VisionDescribeResponse on success.",
            Notes = "Pure wrapper — reuses the existing VisionOrchestrator + HttpVisionClient so the privacy posture for vision traffic stays honest (same endpoint, same rate limits, same circuit breaker). Pairs with the screenshot-watcher path: watcher auto-processes files from Bridge/Screenshots/, this tool lets an AI agent push a specific frame directly.",
        },
        new FeatureDescriptor
        {
            Id = "privacy-posture",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 27 / E3 — machine-readable privacy posture. Enumerates every data-emitting surface (memory, dashboard, health, inference, vision, TTS, OTLP, upstream MCP, packs, crash reports, analytics) and classifies each as never-leaves / only-with-opt-in / leaves-by-default. Surfaced via GET /api/privacy/posture + pal_privacy_posture MCP tool.",
            Notes = "Pure function; reads only PalLlmOptions + the OTEL_EXPORTER_OTLP_ENDPOINT env-var. Pairs with /api/airgap/verify: airgap classifies endpoints by network scope (loopback / private / public); privacy-posture explains what kind of data each surface would transmit. Enables the docs/PRIVACY.md inventory to be generated from live data.",
        },
        new FeatureDescriptor
        {
            Id = "promotion-apply-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass 24 — POST /api/promotion/apply + pal_promotion_apply MCP tool. Persists a candidate promotion as a durable staging triple (template-*.md + rollback-*.txt + packet-*.json) under PalLLM:PromotionApply:StagingRoot. NEVER mutates source code; the staged template is meant to be cherry-picked by a human reviewer. Gated by PalLLM:PromotionApply:AllowApply=false by default, so the verb returns 403 until explicitly enabled.",
            Notes = "Retention-bounded by PalLLM:PromotionApply:MaxStagedArtifacts (default 64) — the oldest triple is pruned on overflow. Reuses the same 400/404/409 error shapes as /apply/preview so ProblemDetails stays consistent. Rollback is simply Remove-Item on the three files — no source-tree mutation to undo. A future pass can add a separate stricter flag that actually writes to source files. Closes the full 2035 hard-code promotion loop (Pass 10 ledger → Pass 11 auto-feed → Pass 12 suggestions → Pass 14 preview → Pass 24 apply).",
        },
        new FeatureDescriptor
        {
            Id = "promotion-suggestion-builder",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Turns the Pass 10+11 IsPromotionCandidate signal into actionable hard-code suggestions. For each stable pattern the builder emits {TaskClass, PatternId, TargetFile, SuggestedChange, EvidenceSummary, RollbackPath, Provenance (full ProofPacket)}. Recognised task classes (fallback-director, duo-branch-tournament, live-inference, duo-disagreement-detector, etc.) get tailored file targets; unknown classes fall through to a generic suggestion so the builder never fails to produce output.",
            Notes = "Every suggestion carries HumanReviewRequired=true and confidence=high — hard-coding is always a human-gated step, never auto-applied. Pure deterministic function (no inference call). Closes the 2035 loop: Pass 10 added the ledger, Pass 11 auto-populates it from metrics, Pass 12 turns 'this is stable' into 'here is exactly what to change'.",
        },
        new FeatureDescriptor
        {
            Id = "promotion-suggestions-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GET /api/promotion/suggestions returns one structured suggestion per promotion candidate. pal_promotion_suggestions MCP tool wraps the same surface for AI clients (bringing the MCP tool count to 23).",
            Notes = "Non-candidate tasks are skipped automatically. Each suggestion surfaces the concrete target file, a one-sentence recipe, evidence counts, and the rollback path — so operators can act on 'promote this' without cross-referencing the ledger manually.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-promotion-suggestion-inline",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "The Field Console Promotion panel's candidate cards now expand to show the inline suggestion: target file (as code), suggested change, evidence summary, and rollback path. Suggestions fetched in parallel with the summary; failures degrade gracefully to the Pass 11 summary-only rendering.",
            Notes = "Uses a standard <details> element with open-by-default for candidate cards so keyboard + screen-reader users get the same detail view as mouse users. Suggestion block uses the candidate-green palette with a subtle tint to distinguish it from the surrounding card.",
        },
        new FeatureDescriptor
        {
            Id = "promotion-ledger-auto-feeder",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Background PromotionLedgerFeeder converts fallback-strategy deltas, live-inference success/failure deltas, rate-limiter engagement deltas, and model-tier transition deltas into automatic ledger observations every 30s (configurable). Opt-in via PalLLM:PromotionFeeder, default ON because behaviour is pure observer — reads metric + RuntimeHealth counters, writes to the bounded in-memory ledger. Populates five dedicated task classes (fallback-director / live-inference / rate-limiter / model-tier-transition) so the dashboard Promotion panel reflects the full observed runtime.",
            Notes = "Pass 13 extended the feeder from fallback-only to the full counter surface. Each surface has its own opt-out (TrackLiveInference / TrackRateLimiter / TrackTierTransitions). live-inference pattern id is the active model id from RuntimeHealth so different models populate separate observation streams. tier-transition pattern id is 'from->to'. rate-limiter fires are recorded as 'success' because the limiter working is itself a positive signal. Seeds baselines on startup so pre-existing counters never retroactively flood the ledger. Per-tick cap still applies but baseline advances past the full delta, so a bursty window is absorbed permanently.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-promotion-panel",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Field Console Promotion panel reads /api/promotion/summary on every refresh and renders per-task cards that flag which patterns are stable enough to hard-code. Candidates glow green, collecting-data cards sit amber, blocked cards (with recent disagreement-block or human-override) turn red. Headline counts promotion candidates at a glance.",
            Notes = "Rides the existing dashboard refresh cadence — no separate polling loop. Cards sort candidates first then by total observations. Section anchor <a href=\"#promotion\">Promotion</a> added to the top nav. Composes with the auto-feeder so operators see live behaviour without running a single HTTP POST.",
        },
        new FeatureDescriptor
        {
            Id = "promotion-ledger",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Hard-code promotion ledger — records per-task-class outcomes (success / disagreement-block / validator-fail / human-override) and reports which patterns have been stable enough to hard-code into deterministic product logic. Implements the 2035 doc's 'successful AI behavior becomes hard-coded, safe, fast product logic' prediction.",
            Notes = "Conservative criterion: >=20 observations, >=95% success rate, zero disagreement-block / human-override in the most recent 10. Bounded in-memory per-task deque (200 observations) with oldest-first eviction so long-running sidecars can't blow up memory. Thread-safe (per-task lock, no global lock). Pairs with the Pass 9 DisagreementDetector (every disagreement-block in the ledger is a direct refusal to promote) and with ProofPacketBuilder (each observation can be wrapped as a ProofPacket for audit).",
        },
        new FeatureDescriptor
        {
            Id = "promotion-endpoints",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "POST /api/promotion/record + GET /api/promotion/summary expose the ledger to REST callers. Invalid outcomes (anything other than 'success' / 'disagreement-block' / 'validator-fail' / 'human-override') return ProblemDetails 400 with a helpful message rather than 500. Summary response is a stable PascalCase JSON document with per-task counts + IsPromotionCandidate flag + recommendation sentence.",
            Notes = "pal_promotion_record + pal_promotion_summary MCP tools wrap the same endpoints for AI clients, bringing the MCP tool count to 22. Task-class list is operator-defined — there is no enum, so any string makes sense (e.g. 'ImplementDraft', 'Audit', 'fallback-strategy:stealth-withdraw').",
        },
        new FeatureDescriptor
        {
            Id = "duo-disagreement-detector",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Completes the ParallelDisagreement cooperation pattern: takes two model outputs (typically Worker + Judge), blends three similarity signals (semantic cosine from SemanticEmbedder, token-overlap Jaccard, length ratio) into a CombinedScore, and emits a structured {Verdict (agree/minor-drift/major-disagreement), SafetySignal (proceed/review/block), Recommendation, KeyEntityAgreement[]}. Deterministic — no inference call.",
            Notes = "Thresholds are conservative: >=0.85 combined = agree, >=0.60 = minor-drift/review, below = major-disagreement/block. The point of the pattern is to BLOCK auto-promotion on disagreement, so false 'disagree' is cheaper than false 'agree'. Surface is POST /api/disagreement/check + pal_disagreement_check MCP tool (brings MCP tool count to 19).",
        },
        new FeatureDescriptor
        {
            Id = "proof-packet-builder",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Machine-readable provenance bundle for automated PalLLM decisions. Every packet carries {Version, Id (stable SHA-256 of subsystem+decision+captured-at), Subsystem, Decision, PrimaryReason, CapturedAtUtc, Evidence[], ModelArtifacts[], ValidatorResults[], RollbackPath, Confidence, HumanReviewRequired}. Ships convenience builders FromFallbackDecision and FromDisagreement so existing deterministic paths can attach provenance in one call.",
            Notes = "Implements the 2035 doc's 'provenance becomes a normal product artifact' prediction. Same (subsystem, decision, timestamp) always produces the same id so downstream evidence stores can dedupe. Surface is POST /api/proof/packet + pal_proof_packet MCP tool (brings MCP tool count to 20). Composes with existing SelfHealingEvidence / ReleaseEvidence / LaunchEvidence rather than replacing them.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-chat-panel",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Field Console #chat section lets operators talk to the companion directly from the dashboard: textarea input, conversation history, optional character id, SSE streaming toggle, per-turn Duo task-kind chip, live-vs-fallback badge, latency pill, and presentation-cue chips. Closes the biggest UX gap — the dashboard was read-only until now.",
            Notes = "Pure client-side addition — no runtime changes. Uses POST /api/chat by default; toggling Stream switches to POST /api/chat/stream and renders phase events as they arrive. Every submitted turn fires a parallel /api/chat/plan so the inferred Duo task-kind + cooperation-pattern chip shows even if the chat reply is slow. Section anchor <a href=\"#chat\">Chat</a> added to the top nav.",
        },
        new FeatureDescriptor
        {
            Id = "chat-dispatch-planner",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Pass-22 deterministic planner that turns a (DuoCooperationPattern, ModelRoleCoverage) pair into a concrete ChatDispatchDecision: mode (deterministic-only / single-role / duo-sequential / duo-parallel / duo-fanout / duo-tournament / duo-background / duo-watchdog / duo-appeal), ordered role chain, primary/reviewer role + binding ids, and a plain-English reason. Surfaced on every ChatResponse (DispatchedRoleChain + DispatchMode) and on every /api/chat/plan (Dispatch).",
            Notes = "Pure function, no inference. Advisory only: chat still dispatches through the single-lane inference client today. The field is here so operators + AI agents can see the concrete execution plan and so a future pass can flip the single-lane passthrough to actually invoke the chain without a breaking contract change. Pairs with ModelRoleRegistry (reads role coverage) and DuoOrchestratorPlanner (reads the chosen pattern).",
        },
        new FeatureDescriptor
        {
            Id = "chat-task-kind-inferer",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Deterministic keyword-based classifier that maps a raw chat request (user message + optional task tag) to one of the 10 DuoTaskKind values. TaskTag override wins over message keywords; high-risk phrasing (delete / wipe / rm -rf / api key / production deploy) always takes precedence so HighRisk task-kinds route through ParallelDisagreement. Sibling to WhyEngine.Classify.",
            Notes = "Unknown shape collapses to ImplementDraft, which is the safest general-purpose default. Order of checks is deliberate: high-risk → architecture → audit → tool-execution → long-context → parallel-candidates → final-synthesis → media-prompting → command-routing → default. Enables the Pass-16 chat-plan advisor to route without touching ChatAsync.",
        },
        new FeatureDescriptor
        {
            Id = "chat-plan-advisor",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "POST /api/chat/plan + pal_chat_plan MCP tool: given a {UserMessage, TaskTag?, Risk?, Hardware?} request, infers the DuoTaskKind and returns the full Pass-8 DuoPlan the planner would pick. Pure advisory — does not mutate runtime state and does not run inference.",
            Notes = "Operators can call this before choosing which Role bindings to enable; AI agents can call it to forecast how a chat turn would flow through the mesh. Brings the MCP tool count to 25.",
        },
        new FeatureDescriptor
        {
            Id = "duo-orchestrator-planner",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Deterministic planner that turns a (task kind, risk, hardware tier, live role coverage) tuple into one of ten Qwen Duo Mesh cooperation patterns (Scout→Judge, Architect→Implementer→Auditor, Fan-out→Synthesis, Parallel Disagreement, Branch Tournament, Sequential Swap, Worker-live/Judge-background, Draft→Finalize, Duo Watchdog, Dense Appeal Court, Single-role fallback, Deterministic-only). Each plan carries step-by-step role assignments, thinking-mode hints, context-budget hints, and an escalation path.",
            Notes = "Pure C#, no inference call, no external I/O. Always produces a usable plan: if neither Worker nor Judge is bound the planner falls through to DeterministicOnly with a clear nudge to declare roles. High-risk tasks always take ParallelDisagreement so disagreement between Worker and Judge becomes a first-class safety signal. Constrained-hardware requests always take SequentialSwap so only one model needs to be resident.",
        },
        new FeatureDescriptor
        {
            Id = "duo-plan-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "POST /api/duo/plan accepts { Kind, Risk, Hardware } (numeric enum values) and returns the DuoPlan shape the planner computes. Deterministic: no inference, so the endpoint is always available even when every external dependency is down.",
            Notes = "Pairs with /api/roles: /api/roles tells you WHAT is bound; /api/duo/plan tells you HOW to use it. Both share the same ModelRoleRegistry + DuoOrchestratorPlanner so their views of 'what role is bound' never drift.",
        },
        new FeatureDescriptor
        {
            Id = "pal-duo-plan-mcp-tool",
            Source = "PalLLM MCP server",
            Status = "ready",
            Summary = "MCP tool pal_duo_plan wraps DuoOrchestratorPlanner.Plan so Claude Desktop / Cursor / VS Code users can ask 'which cooperation pattern should I use for this task?' in natural language and get the same structured plan the HTTP endpoint returns. Accepts kind / risk / hardware as string enum names (case-insensitive) so AI callers can be sloppy with input.",
            Notes = "Complements pal_model_roles (what's bound) with pal_duo_plan (how to use it). Brings the MCP tool count to 18.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-mesh-duo-hint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Dashboard Mesh panel appends a small hint line showing the default Qwen Duo cooperation pattern the planner would pick for a standard ImplementDraft task. Operators see at a glance which pattern the orchestrator recommends given their current role coverage.",
            Notes = "Fire-and-forget fetch after the main coverage render — never blocks the Mesh panel if /api/duo/plan fails. Friendly enum labels (Scout → Judge, Branch Tournament, etc.) are mapped client-side so the dashboard stays readable regardless of the server's enum-serialisation policy.",
        },
        new FeatureDescriptor
        {
            Id = "model-role-registry",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Declarative Edge / Worker / Judge / Media / Validator role bindings. Operators bind each role to a local endpoint under PalLLM:ModelRoles[]; the registry reports coverage, active bindings, critical gaps, and a pairing-pattern recommendation based on the 2035 local-first AI mesh architecture.",
            Notes = "Metadata-only today — binding a role does not automatically route inference traffic, but it makes the mesh architecture legible to AI clients, operators, and future role-aware routing passes. Each slot carries a Description and Recommendation so new operators know what belongs in each role without reading external docs. Critical gaps flag Edge + Worker because those two are the minimum viable local-first mesh; Judge + Media + Validator are graceful upgrades.",
        },
        new FeatureDescriptor
        {
            Id = "model-roles-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GET /api/roles reports the live Edge / Worker / Judge / Media / Validator coverage computed by ModelRoleRegistry. Returns per-slot { Role, Description, IsConfigured, IsActive, Bindings[], Recommendation } plus top-level ActiveBindings, TotalBindings, CriticalGaps[], and a PairingPattern message.",
            Notes = "Shared with the pal_model_roles MCP tool, the dashboard Mesh panel, and the /api/quickstart nudge so every consumer sees the same coverage shape.",
        },
        new FeatureDescriptor
        {
            Id = "pal-model-roles-mcp-tool",
            Source = "PalLLM MCP server",
            Status = "ready",
            Summary = "MCP tool pal_model_roles wraps ModelRoleRegistry.GetCoverage so Claude Desktop / Cursor / VS Code users can ask 'what local models has this operator configured?' in natural language and get the same structured coverage payload the HTTP endpoint returns.",
            Notes = "Complements pal_describe (identity), pal_quickstart (next steps), pal_why (causation), and pal_airgap_verify (posture). Brings the MCP tool count to 17.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-mesh-panel",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Field Console Mesh panel renders the 5-slot role-coverage grid with active / configured / missing state chips and per-slot recommendations. Pairing-pattern string becomes the surface-copy headline so operators see at a glance which brain is doing what in the local-first mesh.",
            Notes = "Rides the existing refresh cadence — no extra polling. Active bindings glow green, configured-but-disabled sit amber, missing slots stay neutral. Section anchor <a href=\"#mesh\">Mesh</a> added to the top nav.",
        },
        new FeatureDescriptor
        {
            Id = "quickstart-role-coverage-nudge",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "/api/quickstart consumes ModelRoleRegistry and emits a recommended step when the operator has no role bindings or a critical role (Edge / Worker) is unconfigured. Makes the mesh surface visible even to operators who haven't read the ARCHITECTURE.md role section.",
            Notes = "The step always stays in the 'optional' tier — role binding is metadata-only today and never blocks the chat path. Disappears automatically once the operator declares an Edge + Worker binding.",
        },
        new FeatureDescriptor
        {
            Id = "why-engine",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Local, deterministic-first 'why engine' that answers natural-language causal questions about the runtime's recent behaviour ('why did my reply come from the fallback?', 'why is the bridge not ready?', 'why is the circuit breaker open?'). Always responds — no live inference call — so the answer is available even when every external dependency is down.",
            Notes = "WhyEngine keyword-classifies the question into one of FallbackTriggered / InferenceBypassed / CircuitBreaker / BridgeNotReady / LowHealthScore / RateLimited / ThermalGate / Unknown, then returns a structured WhyAnswer { Question, Intent, PrimaryReason, CausalChain[], EvidenceReferences[], Confidence }. Unmatched questions fall through to a grounded generic-posture explanation so the engine never fails to produce useful output. Portable, BCL-only, harvestable by any other local-first LLM runtime.",
        },
        new FeatureDescriptor
        {
            Id = "why-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "POST /api/why accepts a {Question} body and returns the same structured WhyAnswer the MCP pal_why tool and the dashboard Why panel render. Deterministic-first: no inference, so the endpoint is always available.",
            Notes = "Pairs with pal_why MCP tool and the dashboard Why panel (all three share WhyEngine.Answer so the payload shape stays consistent). Useful for operators asking 'why did the companion just say that?' or for AI agents asking the sidecar to introspect its own recent state before composing a user-facing reply.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-why-panel",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Field Console section with a single input box and five preset question chips (Fallback / Bridge / Health / Bypass / Circuit). Submitting a question calls POST /api/why and renders the structured causal chain as a numbered list with evidence references. Each answer is colour-banded by intent (bridge = red, circuit = amber, fallback = blue) so operators can triage at a glance.",
            Notes = "Lives in wwwroot/index.html + app.js + styles.css. Shares escapeHtml with the quickstart panel. Works even when inference is off because the underlying engine is fully deterministic. Section anchor <a href=\"#why\">Why</a> added to the top nav.",
        },
        new FeatureDescriptor
        {
            Id = "pal-why-mcp-tool",
            Source = "PalLLM MCP server",
            Status = "ready",
            Summary = "MCP tool pal_why wraps WhyEngine.Answer so Claude Desktop / Cursor / VS Code users can ask causal questions about PalLLM's recent behaviour in natural language and get the same structured WhyAnswer the HTTP endpoint returns.",
            Notes = "Complements pal_describe (static identity) and pal_quickstart (dynamic next steps) with pal_why (causal explanation). Together the three form a complete AI-introspection surface. Brings the MCP tool count to 16.",
        },
        new FeatureDescriptor
        {
            Id = "ai-first-mcp-tools",
            Source = "PalLLM MCP server",
            Status = "ready",
            Summary = "Four new MCP tools (pal_describe, pal_quickstart, pal_airgap_verify, pal_self_healing_status) mirror the AI-first HTTP endpoints so Claude Desktop / Cursor / VS Code users can ask natural-language questions ('what is PalLLM?', 'what should I do next?', 'are you offline?', 'is the watchdog alive?') and get the same machine-readable payloads without a manual HTTP round-trip.",
            Notes = "pal_describe wraps /api/describe. pal_quickstart wraps /api/quickstart. pal_airgap_verify wraps /api/airgap/verify (never emits a live request). pal_self_healing_status reads Runtime/SelfHealingEvidence/latest-self-healing.json via the shared SelfHealingStatusReader so the MCP tool, the HTTP endpoint, and the dashboard chip all see the exact same payload contract. Brings the MCP tool count to 15 (up from 11).",
        },
        new FeatureDescriptor
        {
            Id = "self-healing-status-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GET /api/self-healing/status returns the latest SelfHealingWorker evidence artifact or a structured pending marker when the watchdog has not ticked yet. Used by the dashboard chip and the pal_self_healing_status MCP tool so both surfaces see the same payload.",
            Notes = "Shared SelfHealingStatusReader powers the HTTP endpoint, the MCP tool, and the dashboard chip. Pending-marker shape is {status: 'pending' | 'unreadable', detail: '...'} so callers can always distinguish 'worker has not ticked yet' from 'worker tick landed with these results'.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-self-healing-chip",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Compact pill in the Field Console top ribbon showing the self-healing watchdog's last-tick age and archived-envelope count. Quiet green when the janitor has nothing to do, amber when it archived orphan envelopes, grey for pending / error states. Proves to operators that the background worker is alive without leaving the dashboard.",
            Notes = "Reads /api/self-healing/status on every dashboard refresh — no separate polling loop. Hovering the chip shows the exact operator-health score the last tick observed. Adapts gracefully to the pending marker shape so a fresh install displays 'Watchdog: pending first tick' instead of an empty pill.",
        },
        new FeatureDescriptor
        {
            Id = "dashboard-quickstart-panel",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Field Console's first surface is a state-aware Quickstart panel that fetches /api/quickstart on every refresh and renders ordered critical / recommended / optional steps with label / why / do / verify fields. New operators see exactly what to do next without reading any docs; the panel collapses to a quiet \"ready\" banner when nothing is pending.",
            Notes = "Zero new endpoints: the panel mirrors GET /api/quickstart so humans and AI see the same guidance. Priority-coloured cards (red / amber / blue / green) and a top border that matches overall status give a glance-level read of whether the sidecar is operational, needs-setup, or needs-attention. Lives in wwwroot/index.html + app.js + styles.css and rides the existing dashboard refresh cadence — no extra polling loop. Section anchor <a href=\"#quickstart\">Quickstart</a> added to the top nav.",
        },
        new FeatureDescriptor
        {
            Id = "self-healing-watchdog",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Conservative background watchdog that keeps long-running sidecars clean without destructive actions. Every 60s (configurable): archives orphan outbox envelopes older than the configured threshold to Runtime/SelfHealingEvidence/recovered-<UTC>/, logs a structured warning when operator-health drops at or below the unhealthy floor, and writes Runtime/SelfHealingEvidence/latest-self-healing.json plus a rotating history so operators can audit exactly what was observed.",
            Notes = "Defaults ON because behaviour is strictly additive: no file is deleted, no envelope is destroyed, the inference circuit breaker is never reset, and the sidecar is never restarted. Destructive recovery stays with the human-driven recover.bat path. Options under PalLLM:SelfHealing (Enabled/CheckIntervalSeconds/OrphanEnvelopeAgeSeconds/UnhealthyScoreFloor/HistoryRetention). Evidence artifact is a PascalCase JSON document so the same snapshot shape flows through the durable history, /api/release/readiness future wiring, and operator dashboards.",
        },
        new FeatureDescriptor
        {
            Id = "chat-stream-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "POST /api/chat/stream returns the chat pipeline as Server-Sent Events so web UIs and AI clients see progress phases (started / phase / token* / presentation / speech / action? / final, plus error on failure) before the final ChatResponse arrives. Keeps /api/chat unchanged for callers that want one-shot synchronous replies.",
            Notes = "SSE frames use standard 'event: <name>\\ndata: <json>\\n\\n' format consumable by every browser EventSource and every MCP SSE client. Sets X-Accel-Buffering: no so reverse proxies (Caddy, nginx) pass through without buffering. Pass 23 added word-level 'token' events (each carrying `{index, total, text}` from AssistantMessage), plus dedicated 'presentation', 'speech', and 'action' events so streaming clients render incrementally instead of parsing the final payload. The concatenated 'token' text exactly equals the final AssistantMessage (regression-tested). The 'final' event always carries the complete ChatResponse JSON, so callers that only care about the final answer can ignore intermediate phases. Errors never leak implementation details — an 'error' event carries a retryable flag + short detail. Pairs with deterministic fallback so a streamed turn never leaves the caller empty-handed.",
        },
        new FeatureDescriptor
        {
            Id = "air-gap-verifier",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GET /api/airgap/verify classifies every enabled outbound surface (inference, vision, TTS, OTLP, MCP upstreams) as loopback / private / public / disabled so operators and AI callers can prove the sidecar's air-gap posture in one machine-readable shot. Useful for compliance-minded publication and AI agents deciding whether to trust the instance with sensitive input.",
            Notes = "Never emits a live request. Classification is pure host-string inspection + bounded DNS resolution. Overall verdict is one of: 'strict-airgapped' (every enabled surface loopback-or-disabled), 'lan-airgapped' (no public, some private), 'not-airgapped' (at least one public host), 'indeterminate' (host could not be resolved). Per-surface findings include endpoint, host, and classification so operators can fix a specific red surface without guessing.",
        },
        new FeatureDescriptor
        {
            Id = "quickstart-guide-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GET /api/quickstart returns live state-aware \"do this next\" guidance. Where /api/describe is the static manifest, this endpoint is dynamic: it reads RuntimeHealth + options and returns ordered critical/recommended/optional steps, each with a plain-English label, reason, concrete action, and how-to-verify pointer. Useful for both human operators and AI assistants trying to decide what to do first.",
            Notes = "Overall status is one of: 'ready' (nothing critical or recommended pending), 'needs-setup' (recommended upgrades available), 'needs-attention' (critical signal red). The guide is derived on every call, so the state always matches the current runtime. Pairs with /api/describe for the classic 'static identity + dynamic next-step' contract.",
        },
        new FeatureDescriptor
        {
            Id = "self-description-endpoint",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GET /api/describe returns a one-shot self-description manifest AI / MCP callers can fetch on connect to learn what the running PalLLM instance is, what it can do right now, and which specific endpoints or tools to reach for next — no doc scraping or multi-round-trip discovery required.",
            Notes = "Payload includes Identity (product/license/redistributable), Version (sidecar/MCP protocol/audit date), OperatorHealth (single 0-100 score + grade + top-3 reasons), CurrentState (adapter/bridge/inference/vision/TTS/automation posture), Surface (live route count, feature counts, fallback strategy count), PostureGuarantees (local-first, deterministic-fallback-always-available, opt-ins-default-off, third-party liability, trademarks), CommonAsks (Goal + How pairs for the top nine things an AI caller typically wants), and SafetyNotes (EmergencyFallback + circuit breaker + thermal gate + rate limit posture). Participates in the same strong-ETag private-cache protocol as /api/features so repeat callers cheap-revalidate with 304.",
        },
        new FeatureDescriptor
        {
            Id = "operator-happiness-score",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Single-number 0-100 health score (with Excellent / Good / Degraded / Critical grade) rolled up from the live RuntimeHealth snapshot. Answers the one-glance question \"is this companion likely to give good replies right now?\" for non-technical operators.",
            Notes = "Starts at 100, subtracts deterministic amounts per degradation signal (adapter not ready -20, bridge disabled -10, inference circuit open -15, high failure rate -15, rate-limiter engaged -5, etc.). Inference-side penalties only apply when inference is configured, so fallback-only operators never see a lower score just for not flipping inference on. Top-3 subtraction reasons returned alongside the number; order is stable so dashboards don't flap. Surfaced under SelfDescription.OperatorHealth on /api/describe.",
        },
        new FeatureDescriptor
        {
            Id = "recover-bat",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Package-root recover.bat / scripts/recover-palllm.ps1 one-click recovery: stops any running sidecar cleanly, archives stuck bridge envelopes to a timestamped Bridge/RecoveryArchive/ folder (nothing is lost), prunes durable evidence older than -RetainEvidenceDays (default 14), restarts the sidecar, and reports the operator happiness score.",
            Notes = "Every step is best-effort: a sidecar that was already stopped, an empty outbox, or a missing evidence folder are all success cases. Pairs with install.bat / play.bat / support.bat as the fourth one-click verb so a stuck install has an obvious recovery path that doesn't require reading docs. -SkipRestart flag lets an operator stop + clean without immediately restarting; -RetainEvidenceDays 0 skips evidence pruning entirely.",
        },
        new FeatureDescriptor
        {
            Id = "thermal-gate",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Opt-in GPU-temperature gate short-circuits live inference to the deterministic fallback director when the primary GPU is already throttling, so player-visible chat latency stays predictable under thermal pressure instead of absorbing the throttled round-trip time.",
            Notes = "Configured under PalLLM:Inference:ThermalGate (Enabled/RejectAboveC/WarnAboveC/CacheTtlSeconds). Off by default, matching PalLLM's every-opt-in-is-off posture. Best-effort sampler: when nvidia-smi isn't on PATH the gate stays permissive — the feature is always safe to leave enabled. The PALLLM_FAKE_GPU_TEMP_C env var lets tests and operators simulate a hot GPU without real sensors. Portable (zero NVML dependency), lives in PalLLM.Domain, harvestable by any other local-first LLM runtime exactly like SemanticEmbedder / ResponseCleanup. See docs/TUNING.md for thresholds.",
        },
        new FeatureDescriptor
        {
            Id = "full-audit-pipeline",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Single-command `scripts/run_full_audit.ps1` reproduces every CI gate (build, tests, 6 drift checks) plus optional coverage + CycloneDX SBOM locally, and emits a self-contained timestamped `artifacts/full-audit/<ts>/RESULTS.md` with pass/fail table + per-step logs + environment metadata.",
            Notes = "the external prompt-pack project-level mojibake detector avoids the Windows-1252 false-positive that a naive Get-Content would hit on PS 5.1. Exit code 0 on all-pass lets CI / git pre-push hooks gate on it. Flags: -SkipCoverage, -SkipSbom, -SkipTests, -FailFast. The timestamped RESULTS.md doubles as a shippable audit snapshot for anyone asking \"is this repo in a good state?\" on any given day. See CONTRIBUTING.md Sec. Pre-flight checklist.",
        },
        new FeatureDescriptor
        {
            Id = "supply-chain-defences",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "GitHub Dependabot, CodeQL, and a full-SHA workflow-action pin audit catch known-vulnerable dependency versions, C# security-pattern regressions, and mutable CI/release action refs without relying on manual audits.",
            Notes = "Dependabot opens weekly grouped PRs for NuGet, GitHub Actions, and Docker base images, plus immediate PRs on published CVEs. CodeQL runs C# `security-extended` query packs on every push, PR, and weekly schedule; findings land in the repo's Security tab. `scripts/audit-workflow-action-pins.ps1`, the CI doc-drift job, and MetaTests require every external `.github/workflows/*.yml` action reference to use a 40-character commit SHA, while comments preserve the intended major tag for maintenance review.",
        },
        new FeatureDescriptor
        {
            Id = "api-key-authentication",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Optional bearer-token authentication on the sidecar HTTP surface. Off by default (localhost-only posture); opt in by setting PalLLM:Auth:ApiKey to any non-empty string.",
            Notes = "When configured, every /api/* request requires `Authorization: Bearer <key>`; unauthenticated requests get 401 + `WWW-Authenticate: Bearer`. Metrics, health, OpenAPI, and the static dashboard stay open by default so monitoring / orchestrators / SDK generators keep working; flip PalLLM:Auth:ProtectMetrics or ProtectHealth on to lock those too. Constant-time key comparison via CryptographicOperations.FixedTimeEquals avoids timing-side-channel prefix leaks. Key rotation is a config edit + restart - no shared state to flush.",
        },
        new FeatureDescriptor
        {
            Id = "code-coverage-reporting",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Every CI run collects line + branch coverage via coverlet.collector, renders a per-assembly summary into the GitHub Actions step summary, and uploads a clickable HTML drilldown as a build artefact.",
            Notes = "Driven by `dotnet test --collect:\"XPlat Code Coverage\" --settings tests/PalLLM.Tests/coverlet.runsettings`; ReportGenerator 5.5.5 post-processes the Cobertura XML into `MarkdownSummaryGithub` + `HtmlInline` variants. The runsettings file excludes generated files, test assemblies, and compiler-generated display classes so the numbers reflect production code. Baseline on day one: 81.1% line / 73.2% branch across PalLLM.Domain + PalLLM.Sidecar - useful for spotting regressions and guiding future test additions.",
        },
        new FeatureDescriptor
        {
            Id = "distributed-tracing",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Opt-in OpenTelemetry distributed tracing. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set, the sidecar emits spans for every incoming HTTP request, every outgoing HttpClient call, and every PalLlmRuntime chat turn - queryable from any OTLP backend (Tempo, Jaeger, Honeycomb, Datadog, etc.).",
            Notes = "Zero overhead when off. The env var acts as the switch: if unset, no ActivityListener is registered, `PalLlmTelemetry.Source.StartActivity` returns null, and the ASP.NET Core + HttpClient instrumentation never installs DiagnosticListener adapters. When set, standard OTel env vars (`OTEL_SERVICE_NAME`, `OTEL_RESOURCE_ATTRIBUTES`, `OTEL_EXPORTER_OTLP_HEADERS`) are honoured. The PalLLM chat span carries request_id, character_id, task_tag, response_path, used_fallback, and fallback_strategy tags so operators can query inference-vs-fallback ratios and per-strategy latency from the tracing backend. Health and metrics endpoints are filtered out so scrape traffic doesn't drown the interesting spans. Two regression tests pin the emit-when-listened-to and no-op-when-not-listened-to behaviours.",
        },
        new FeatureDescriptor
        {
            Id = "business-prometheus-metrics",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Domain-level Prometheus metrics alongside the existing process counters - per-strategy fallback usage, per-pair model-tier transitions, and a cumulative chat-latency histogram. Operators get Grafana-ready dashboards on top of what was previously OTel-only visibility.",
            Notes = "Three new metric families at `/metrics`: `palllm_fallback_strategy_total{strategy=\"...\"}` (which of the 19 deterministic strategies is hitting - bounded cardinality); `palllm_model_tier_transition_total{from=\"...\",to=\"...\"}` (how often + when the tier orchestrator graduates - typically `<none>->small` once on startup and `small->large` once when the large model finishes pulling); `palllm_chat_duration_seconds` as a cumulative Prometheus histogram with 13 buckets from 5ms to 60s covering fallback-only paths through large-model inference. Paired with `_count` + `_sum` so Grafana's `histogram_quantile()` reads it natively. Backed by a new `PalLlmMetrics` type in PalLLM.Domain - thread-safe counters via `ConcurrentDictionary<string,long>` and `Interlocked` on a fixed-bucket array, no external dependency. 8 new unit tests lock in the bucket-cumulative semantics, per-strategy isolation, null/whitespace defensive paths, and the Prometheus exposition format.",
        },
        new FeatureDescriptor
        {
            Id = "tls-deployment-guide",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Complete TLS deployment playbook for exposing PalLLM beyond localhost. Ships a ready-to-adapt Caddyfile with auto-HTTPS, SSE-compatible proxy settings, and baseline security headers; plus copy-paste nginx and Traefik configs in `docs/TLS.md`.",
            Notes = "Closes the final production-deployment gap. Before this, operators who wanted to expose PalLLM to a LAN or the internet had to figure out TLS termination themselves. The Caddyfile pre-configures `flush_interval -1` so MCP's Streamable-HTTP / SSE responses aren't buffered, forwards `X-Forwarded-*` headers so PalLLM's tracing sees real client info, and ships HSTS + nosniff + Referrer-Policy baseline headers. The TLS.md guide includes a hardening checklist paired with `PalLLM:Auth:ApiKey` + `ProtectMetrics` / `ProtectHealth` flags so operators know what to tighten before going public. Bundled Caddyfile ships inside the release ZIP alongside the Claude Desktop / VS Code / Compose examples - no separate download.",
        },
        new FeatureDescriptor
        {
            Id = "mcp-upstream-client",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "PalLLM acts as an MCP client too - operators declare external MCP servers in `PalLLM:McpClient:UpstreamServers[]` and the sidecar probes each on startup and periodically, caching the discovered tools / resources / prompts behind `GET /api/mcp/upstream` and the `pal_list_upstream_mcp` MCP tool. Makes PalLLM a lightweight MCP hub: one endpoint lists what every connected upstream can do.",
            Notes = "V1 is discovery-only and read-only by design - PalLLM does NOT automatically proxy tool calls to upstream servers. Each upstream is configured with `{Id, Url, BearerToken?, Enabled}`; bearer tokens are never returned on any API surface. The `McpUpstreamDiscoveryWorker` IHostedService runs probes in parallel on a cadence (`DiscoveryIntervalSeconds`, default 300s) with a short per-probe timeout (`DiscoveryTimeoutSeconds`, default 10s). Cached tool/resource/prompt metadata is bounded per upstream (`MaxToolsPerServer`, `MaxResourcesPerServer`, `MaxPromptsPerServer`, `MaxMetadataEntryLength`) so one noisy upstream cannot balloon the snapshot cache or `/api/mcp/upstream` response body. Unreachable servers are marked `Connected=false` with a stable `ErrorCode` plus a sanitized `Error` string, not thrown, so one dead upstream never takes down the pool. Built on the `HttpClientTransport` + `McpClient` client API from `ModelContextProtocol.Core`. Integration coverage pins the empty-config path, unreachable-URL graceful handling, disabled-server skip semantics, MCP-tool<->REST-endpoint equivalence, startup-validation for the new bounds, and bounded metadata shaping against a live upstream test server. Future revisions can layer selective invocation on top once the security model is designed.",
        },
        new FeatureDescriptor
        {
            Id = "mcp-server",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Complete Model Context Protocol (MCP) server exposed over Streamable HTTP at `/mcp` - all three primitives (tools, resources, prompts). Any MCP-aware agent (Claude Desktop, VS Code, Cursor, ChatGPT, custom clients) discovers and uses PalLLM through the standardised JSON-RPC 2.0 protocol with zero custom integration code.",
            Notes = "Built on `ModelContextProtocol.AspNetCore` 1.2.0. Three primitive surfaces: (1) 38 tools in `PalLlmMcpTools`; (2) 6 direct resources + 1 template in `PalLlmMcpResources`; (3) 4 prompts in `PalLlmMcpPrompts`. Attribute-based registration via `[McpServerTool]`/`[McpServerResource]`/`[McpServerPrompt]` auto-discovered with `WithToolsFromAssembly()` + `WithResourcesFromAssembly()` + `WithPromptsFromAssembly()` - adding a new primitive means adding one attributed method, no central registration list to maintain. DI services (PalLlmRuntime, PalLlmOptions, ModelTierOrchestrator) inject directly as method parameters. Protocol version `2025-06-18`. Stateless transport. Auth middleware covers `/mcp` identically to `/api/*`. All MCP primitives are read-only by design - guarded actions stay gated behind the existing `AutomationOptions.AllowedActions` allowlist so MCP is observation + conversation, not unchecked side effects. Integration coverage in `McpEndpointTests` and `McpUpstreamClientTests` pins the negotiated protocol version, discovery lists, auth/origin posture, MCP-tool execution, and the upstream-catalog reflection path.",
        },
        new FeatureDescriptor
        {
            Id = "snapshot-vision-fallback",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Deterministic scene-description fallback for chat visual augmentation - when the player attaches a screenshot but the multimodal model is disabled or unreachable, PalLLM composes a terse 1-3 sentence scene summary from the live `GameWorldSnapshot` (time of day, biome, weather, base/wild location, nearby pals, hostiles, objective) instead of dropping visual context. Companions never feel 'blind'.",
            Notes = "Implemented as pure `SnapshotVisionFallback.Compose(snapshot)` in `PalLLM.Domain/Inference`. Wired into `PalLlmRuntime.ChatAsync` between the live vision call and the prompt builder: tries the multimodal model first if enabled, falls back to snapshot composition if the model is off OR the describe call returns `Success=false`. System prompt labels the source explicitly (`(from vision model)` vs `(from snapshot fallback)`) so the model can weight the context appropriately. The active `pal.chat` OpenTelemetry span carries a `pal.visual_context_source` tag with value `vision_model`, `snapshot_fallback`, or `none` - operators can query fallback activation rates from the tracing backend. The `/api/vision/describe` endpoint is deliberately NOT wrapped with this fallback - external tools calling that endpoint want ground-truth vision output, not a synthesised stand-in.",
        },
        new FeatureDescriptor
        {
            Id = "tiered-model-orchestration",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Tiered local-model orchestration - the sidecar probes the inference endpoint for currently available models, uses the highest-priority available tier on every request, and automatically graduates from the 'small' fast-start tier (e.g. unsloth gemma-4-E4B-it-UD-Q4_K_XL via llama.cpp) to the 'large' quality tier (e.g. unsloth Qwen3.6-35B-A3B-UD-Q8_K_XL) the moment the larger model is loaded by the server.",
            Notes = "Configured via `PalLLM:Inference:ModelTiers[]` in appsettings.json with `{Id, Model, Priority, Description?}`. On startup the orchestrator seeds the active tier with the lowest-priority entry in list order so the very first chat request works before any probe completes - the player sees replies from second one of the session. A `ModelTierUpgradeWorker` IHostedService re-probes every `TierProbeIntervalSeconds` (default 30s) and atomically swaps the active tier when a higher-priority one appears, emitting a `pal.model_tier.transition` OpenTelemetry span so operators can see the graduation in their tracing backend. Probe tries OpenAI-compatible `/v1/models` first (covers llama.cpp/llama-server - PalLLM's bundled default - plus vLLM, SGLang, LM Studio, OpenVINO Model Server, and OpenAI itself), then Foundry Local `/openai/models`; results are merged. Pass 346 removed the Ollama-native `/api/tags` fallback - every supported runtime now exposes `/v1/models`. Transient probe failures keep the current tier (no thrashing). Backwards compatible - empty `ModelTiers` list disables orchestration entirely and `Inference.Model` is used verbatim.",
        },
        new FeatureDescriptor
        {
            Id = "hardware-aware-model-collaboration",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Hardware-aware collaboration planner turns configured local model lanes into explicit scout, worker, reviewer, and judge recipes for PalLLM runtime, bridge, HUD, screenshot, docs-sync, and release-hardening work.",
            Notes = "Exposed on `GET /api/inference/collaboration`, `POST /api/inference/collaboration/plan`, `pal models serving`, the `pal_model_collaboration` and `pal_plan_model_collaboration_task` MCP tools, and the `palllm://model/collaboration` MCP resource. The planner classifies configured models by operating style, emits a deterministic capability profile for each lane (modalities, backend fit, serving profile, structured-output/tool-call/speculative-decoding fit, prefill/cache/speculation hints, llama.cpp prompt-cache / slot-count / quantized-KV / native speculation / draft-MTP / idle-sleep guidance plus `pal connect llamacpp` setup for raw GGUF lanes, LM Studio `lms server start`, `lms load`, `/v1/models`, `ttl`, structured/tool-call, auto-evict, loopback, and `pal connect lmstudio` proof guidance for desktop GGUF lanes, sha256_cbor prefix-cache hashing guidance, vLLM `--performance-mode interactivity` proof guidance for player-facing low-latency endpoints, sparse-MoE DBO proof-lane guidance (`--enable-dbo` plus decode/prefill thresholds), optional vLLM `RequestPriority` / `priority` proof for priority-scheduled foreground lanes, optional PalLLM:Inference:ServiceTier / `service_tier` proof for priority/flex/scale routing canaries, optional PalLLM:Inference:PromptCacheKey / PromptCacheRetention proof for hosted prompt-cache key and retention canaries, optional PalLLM:Inference:StoreCompletions / `store` proof for hosted retention-posture canaries, optional PalLLM:Inference:RequestMetadata / `metadata` proof for bounded hosted proof labels, optional PalLLM:Inference:ClientRequestIdHeader proof for outbound `x-client-request-id` or `x-request-id` support correlation, startup-validated PalLLM:Inference:Temperature / TopP / PresencePenalty baseline sampler bounds, optional PalLLM:Inference:TokenBudgetField / `max_completion_tokens` proof for reasoning lanes that reject `max_tokens`, optional PalLLM:Inference:FrequencyPenalty / `frequency_penalty` proof for repetition-control lanes, optional PalLLM:Inference:TopK / `top_k`, MinP / `min_p`, and RepetitionPenalty / `repetition_penalty` proof for local-sampler lanes, optional PalLLM:Inference:ParallelToolCalls / `parallel_tool_calls` proof for strict directive/action lanes, optional PalLLM:Inference:StopSequences[] / `stop` proof for strict delimiter latency lanes, optional prompt-level InferencePrompt.ResponseFormat / `response_format` proof for strict JSON-schema text canaries with schema-digest/request-shape portability receipts, optional prompt-level InferencePrompt.StructuredOutputs / `structured_outputs` proof for vLLM-specific choice, regex, JSON, grammar, or structural-tag canaries, optional prompt-level InferencePrompt.Tools / ToolChoice forwarding plus returned `tool_calls` receipts for strict action/directive canaries, optional prompt-level InferencePrompt.Prediction / `prediction` proof for stable predicted-output replay lanes, optional prompt-level InferencePrompt.Logprobs / TopLogprobs forwarding plus returned choice `logprobs` receipts for confidence/evaluator canaries, optional prompt-level InferencePrompt.Modalities / Audio forwarding plus returned `message.audio` receipts for audio-output canaries, optional prompt-level InferencePrompt.UserContent forwarding for route-owned multimodal input content-part canaries with PalLLM:Inference:UseMediaCacheIds stable local image/video/audio UUIDs plus PalLLM:Inference:MultimodalProcessor / InferencePrompt.MultimodalProcessor / PalLLM:Vision:MultimodalProcessor mm_processor_kwargs proof for min/max pixels, max_soft_tokens, and fps, optional vLLM cache_salt trust-domain isolation, hosted prompt-cache key hygiene, proof-gated FP8/NVFP4 KV-cache dtype compression, sticky/KV cache-aware routing proof for multi-replica pools, qualification-only redacted vLLM KV-event proof, proof-only vLLM disaggregated prefill/decode P/D topology receipts with MoRIIOConnector read/write single-node receipts, Mooncake Store / MultiConnector distributed KV-cache proof for long proof/docs or multi-turn prefix-reuse lanes, PegaFlow-style and FlexKVConnectorV1 external KV cache process-boundary proof for worker-restart and cache-daemon rollback experiments, SGLang radix-cache, HiCache hierarchical KV offload, attention-backend and FP4/FP8 KV proof, EAGLE-3/adaptive/SpecV2 speculation proof, deterministic proof-lane, metrics, and request-admission guidance, TensorRT-LLM `/v1`, `trtllm-serve`, `/health`, `/metrics`, config-YAML, tool-parser, chunked-prefill, KV-cache, speculation, multimodal, disaggregated-serving, and `pal connect tensorrt` proof guidance, transformers serve continuous-batching, `/load_model`, `/v1/responses` proof-lane, revision-pinning, OpenVINO Model Server `/v3`, `--target_device`, INT4 edge-model, VLM, ASR, NPU PREFILL_HINT / GENERATE_HINT, and `pal connect openvino` proof guidance, Foundry Local / Windows ML dynamic-endpoint, `/openai/models`, execution-provider, cache-warmup, loopback-only, and `pal connect foundry` proof guidance, Gemma 3n and Gemma 4 audio-in / edge-memory guidance with family-specific audio-token budgets, Qwen3.6 hybrid-GDN state proof receipts, Qwen Omni audio-out proof-lane guidance with an async_chunk-disabled vLLM-Omni realtime receipt before `/v1/realtime` promotion plus `/v1/video/chat/stream` streaming-video receipts for frame cadence, optional PCM16 audio chunks, reconnect/stall behavior, still-image/world-state fallback proof, and `/v1/videos` offline diffusion-job proof, ASR, and tool-call qualification guidance, primary-source/runtime capability handshake receipts, model-artifact provenance receipts for license, lineage, immutable revision/hash, weight format, trust_remote_code, and redistribution decisions, multimodal encoder-cache and KV-transfer qualification hints, trusted-only precomputed media embedding lanes, local hash-pinned LoRA/personality-adapter guardrails, guarded model-native Qwen3.6/Gemma 4 MTP serving hints with strict-route no-spec proof, serving optimizations, runtime guards, VLLM_MAX_N_SEQUENCES/media security and SSRF controls, vLLM sleep/wake admin isolation, idle VRAM reclaim proof, and promotion verification checks), tailors sequential-versus-parallel lane use to the available hardware budget, and emits authority boundaries, risk-based routing policies, concrete task-routing decisions, and an explicit qualification suite for shadow-testing fresh quants before promotion. The intended high-end local posture is still a fast worker lane backed by a slower dense planner and final judge, but the scope is intentionally narrow: Palworld-mod runtime work, not a generic media or product studio.",
        },
        new FeatureDescriptor
        {
            Id = "distributed-logging",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Opt-in OpenTelemetry logs export. When `OTEL_EXPORTER_OTLP_ENDPOINT` is set (same switch as tracing), every ILogger record is shipped as an OTLP log record carrying the active span's trace_id and span_id - clicking a chat span in the tracing backend shows the logs emitted during that turn.",
            Notes = "Single switch for both pillars: tracing and logging share the OTLP endpoint env var, so there is exactly one on/off to reason about. Off by default - no OpenTelemetryLoggerProvider is registered, ILogger writes only to the standard console sinks, zero extra per-log-record cost. When on, the OpenTelemetryLoggerProvider is chained onto the existing LoggerFactory; console/debug sinks keep working in parallel so operators do not lose local visibility. OTel's ILogger integration automatically attaches the active Activity's trace context, so log-span correlation is free - no manual scope-push. Uses OpenTelemetry.Extensions.Hosting 1.15.2's `AddOpenTelemetry().WithLogging()` pattern (stable since OTel .NET 1.9). See docs/OPERATIONS.md Sec. 'Enabling distributed tracing' for the unified walk-through; the sub-section 'What about logs?' covers the logs-specific behaviour.",
        },
        new FeatureDescriptor
        {
            Id = "lua-lint-ci",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "luacheck static analysis runs on every push + PR that touches the UE4SS Lua bridge, with .luacheckrc declaring UE4SS-injected globals so real typos surface without noise from intentional framework references.",
            Notes = "Targets Lua 5.4 (the UE4SS runtime). Scoped to `mod/ue4ss/Mods/PalLLM/Scripts` so unrelated Lua in gitignored trees doesn't trigger the check. Workflow is `.github/workflows/lua.yml`, config is `.luacheckrc` at the repo root. Closes the previous gap where C# had 112 tests + CodeQL but the 3700-line main.lua had no validation at all.",
        },
        new FeatureDescriptor
        {
            Id = "supply-chain-attestation",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Every tagged release is attested with Sigstore-backed GitHub artifact attestations and published alongside digest manifests plus CycloneDX SBOMs enumerating every NuGet package in the sidecar and Domain projects.",
            Notes = "The release workflow uses GitHub OIDC through actions/attest pinned to a full commit SHA, so there are no long-lived keys to rotate and no mutable release-action tag in the trusted path. Verify the downloaded zip against SHA256SUMS, then verify provenance with `gh attestation verify PalLLM-<tag>.zip --owner <owner>`; the command exits non-zero on tamper or forgery. SBOMs feed into Dependency-Track, OWASP Grype, or any SBOM-aware vulnerability scanner. SECURITY.md Sec. Supply-chain verification documents the verification flow.",
        },
        new FeatureDescriptor
        {
            Id = "editorconfig-style-baseline",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Repo-root .editorconfig fixes whitespace, encoding, and C# style conventions across every IDE that honours the format.",
            Notes = "Charset UTF-8, indent 4 spaces (2 for JSON/YAML/markdown/csproj), LF line endings except Windows-shell files (*.bat, *.ps1). C# rule severities are kept at `suggestion` so they surface as IDE hints without breaking builds - strictness can be dialled up per-rule later without refactoring existing code.",
        },
        new FeatureDescriptor
        {
            Id = "openapi-document",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "Native .NET 10 OpenAPI 3.1 document generation exposes a machine-readable spec at GET /openapi/v1.json covering every /api route.",
            Notes = "The spec is rebuilt from Program.cs route registrations on each request, so it literally cannot drift from the actual surface. JSON Schema 2020-12 compliant. Integrators can feed it directly into client-SDK generators. A regression test pins every expected path so an accidental drop of MapOpenApi() breaks the build.",
        },
        new FeatureDescriptor
        {
            Id = "community-health-files",
            Source = "PalLLM runtime",
            Status = "ready",
            Summary = "SECURITY.md, three issue templates (bug, feature, compat), and a PR template ship with the repo so researchers and contributors have a documented, low-friction onramp.",
            Notes = "SECURITY.md points at GitHub private vulnerability reporting with scope, response timeline, and safe-harbour language. Issue templates cover bug reports, feature proposals, and Palworld/UE4SS compatibility drift. The PR template mirrors the five CI drift gates so contributors check the right boxes before pushing. Blank issues are disabled so every report arrives with baseline context.",
        },
        new FeatureDescriptor
        {
            Id = "autopilot-port",
            Source = "PalLLM architecture",
            Status = "deferred",
            Summary = "Generic gameplay-automation modules are intentionally out of scope for PalLLM.",
            Notes = "Blind ports of external game-automation behaviours would need native actions and safety rails before they could be enabled. PalLLM stays narrow: companion, bridge, runtime. See docs/ROADMAP.md Sec. Non-goals.",
        },
    ];
}
