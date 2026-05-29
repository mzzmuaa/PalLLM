using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar.Mcp;

/// <summary>
/// Model Context Protocol (MCP) tool surface for PalLLM.
///
/// <para>These methods expose PalLLM's runtime to any MCP-aware agent -
/// Claude Desktop, Visual Studio Code, Cursor, ChatGPT, or custom clients
/// - via the Streamable HTTP transport wired up in <c>Program.cs</c>. They
/// sit alongside the existing REST API: REST clients keep using
/// <c>/api/*</c> unchanged; MCP clients get a standardised JSON-RPC 2.0
/// interface rooted at <c>/mcp</c>.</para>
///
/// <para>Design notes:
/// <list type="bullet">
/// <item>Tools use flat parameter shapes - MCP clients auto-generate forms
/// from the declared JSON Schema, so complex request objects produce poor
/// UX.</item>
/// <item>Returns are JSON-serialised strings so the MCP content layer
/// surfaces structured data as text content. Complex runtime types
/// (snapshots, catalogs) are shaped via the options below to stay stable
/// across server versions.</item>
/// <item><see cref="PalLlmRuntime"/> and <see cref="Domain.Configuration.PalLlmOptions"/>
/// are resolved from DI via method-parameter injection - no manual service
/// lookups.</item>
/// <item>Read-only tools only: PalLLM keeps live game actions behind the
/// existing guarded-executor allowlist. MCP is discovery / observation /
/// conversation, not a new side-effect surface.</item>
/// </list>
/// </para>
/// </summary>
[McpServerToolType]
public static class PalLlmMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = PalLlmJsonOptions.Create(static options =>
    {
        options.WriteIndented = false;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });

    private static string SerializeStatus(string status, string detail) =>
        JsonSerializer.Serialize(
            new McpStatusPayload(status, detail),
            PalLlmJsonSerializerContext.Default.McpStatusPayload);

    [McpServerTool(Name = "pal_world_snapshot")]
    [Description(
        "Returns the current game-world snapshot as JSON: world name, biome, weather, time of day, "
        + "nearby hostiles/friendlies/resources, base activity, and all known companion characters. "
        + "Use this to understand the player's current in-game situation before answering questions.")]
    public static string GetWorldSnapshot(PalLlmRuntime runtime)
    {
        GameWorldSnapshot snapshot = runtime.Adapter.Snapshot;
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    [McpServerTool(Name = "pal_scene_description")]
    [Description(
        "Returns a short 1-3 sentence human-readable description of the current scene - time of day, "
        + "location (at-base vs wild), nearby companions, threats, and current objective. Composed "
        + "deterministically from the world snapshot so it works even when no vision model is running. "
        + "Faster and cheaper than pal_world_snapshot when you only need a terse overview.")]
    public static string GetSceneDescription(PalLlmRuntime runtime)
    {
        string description = PalLLM.Domain.Inference.SnapshotVisionFallback.Compose(runtime.Adapter.Snapshot);
        return string.IsNullOrEmpty(description)
            ? "No world loaded - no scene to describe."
            : description;
    }

    [McpServerTool(Name = "pal_chat")]
    [Description(
        "Sends a chat message to a companion and returns the assistant's reply plus its "
        + "response path (live inference vs deterministic fallback strategy). This is the primary "
        + "conversation entry point - equivalent to calling POST /api/chat but with MCP-standard "
        + "shape. The same fallback cascade applies: if live inference is off or fails, the runtime "
        + "uses one of 19 hand-authored strategies.")]
    public static async Task<string> ChatAsync(
        PalLlmRuntime runtime,
        [Description("The user message to send to the companion. Required.")]
        string message,
        [Description("Optional companion character id. Leave null for the default companion.")]
        int? characterId,
        [Description("Optional task tag (e.g. 'chat_general', 'chat_camp', 'chat_travel'). Defaults to 'player_chat'.")]
        string? taskTag,
        CancellationToken cancellationToken)
    {
        ChatResponse response = await runtime.ChatAsync(new ChatRequest
        {
            UserMessage = message,
            CharacterId = characterId,
            TaskTag = string.IsNullOrWhiteSpace(taskTag) ? "player_chat" : taskTag,
        }, cancellationToken).ConfigureAwait(false);

        McpChatToolResultPayload payload = new(
            response.RequestId,
            response.CharacterName,
            response.AssistantMessage,
            response.ResponsePath,
            response.UsedFallback,
            response.FallbackStrategy,
            response.InferenceAttempted,
            response.TaskKind);
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerTool(Name = "pal_recall_memory")]
    [Description(
        "Recalls semantically-similar past conversation memory for a given query. Useful when you "
        + "need to know what the companion has previously experienced or said about a topic. Returns "
        + "an array of matching entries with speaker, content, and similarity score.")]
    public static string RecallMemory(
        PalLlmRuntime runtime,
        [Description("The query to search memory for. Required.")]
        string query,
        [Description("Optional companion character id to filter by.")]
        int? characterId,
        [Description("Max number of matches to return (1-25). Defaults to 5.")]
        int? limit)
    {
        int cap = Math.Clamp(limit ?? 5, 1, 25);
        IReadOnlyList<PalLLM.Domain.Memory.ConversationMemoryMatch> matches =
            runtime.MemoryStore.Recall(query, characterId, cap);

        McpMemoryRecallItem[] shaped = matches
            .Select(m => new McpMemoryRecallItem(
                m.Entry.CharacterName,
                m.Entry.SpeakerRole,
                m.Entry.Content,
                m.Entry.CharacterId,
                m.Entry.CreatedAtUtc,
                m.Entry.Importance,
                m.Score))
            .ToArray();
        return JsonSerializer.Serialize(shaped, JsonOptions);
    }

    [McpServerTool(Name = "pal_list_characters")]
    [Description(
        "Lists all known companion characters currently in the player's party / snapshot: "
        + "id, display name, species, role, current task, and trait tags. Use before pal_chat when "
        + "you need to pick a specific character.")]
    public static string ListCharacters(PalLlmRuntime runtime)
    {
        return JsonSerializer.Serialize(runtime.Adapter.Snapshot.Characters.ToArray(), JsonOptions);
    }

    [McpServerTool(Name = "pal_list_features")]
    [Description(
        "Lists every feature PalLLM ships with (id, status, summary, notes). Use this to discover "
        + "what the sidecar can do before reaching for a specific tool - the catalog is exhaustive "
        + "and versioned.")]
    public static string ListFeatures()
    {
        return JsonSerializer.Serialize(PalLlmFeatureCatalog.All.ToArray(), JsonOptions);
    }

    [McpServerTool(Name = "pal_personality_for_species")]
    [Description(
        "Pick the personality-pack id that should apply to a Palworld character of the given species. "
        + "Looks the species up in the operator-configured PalLlmOptions:Packs:DefaultBySpecies map "
        + "(case-insensitive); falls back to the caller-supplied fallbackPackId when no species default "
        + "is configured. Returns { packId, source, species } where source is 'SpeciesDefault', "
        + "'Fallback', or 'None'. Pure deterministic lookup, no side effects.")]
    public static string PersonalityForSpecies(
        PalLlmOptions options,
        [Description("Palworld species label. Trimmed and matched case-insensitively.")]
        string species,
        [Description("Optional per-character pack id to use when the species map has no entry. Trimmed.")]
        string? fallbackPackId)
    {
        PalLLM.Domain.Packs.SpeciesPersonalityResolution resolution =
            PalLLM.Domain.Packs.SpeciesPersonalityResolver.Resolve(
                species,
                options.Packs.DefaultBySpecies,
                fallbackPackId);
        return JsonSerializer.Serialize(resolution, JsonOptions);
    }

    [McpServerTool(Name = "pal_list_recent_bridge_events")]
    [Description(
        "Returns the most recent bridge events drained from the UE4SS inbox (chat, combat, "
        + "weather, raids, travel, etc.). Use this to see what's been happening in-game lately. "
        + "Capped at 50 events.")]
    public static string ListRecentBridgeEvents(
        PalLlmRuntime runtime,
        [Description("Max number of events to return (1-50). Defaults to 20.")]
        int? limit)
    {
        int cap = Math.Clamp(limit ?? 20, 1, 50);
        // Existing runtime exposes a log summary rather than raw events; shape
        // that into the MCP response so MCP consumers see recent activity.
        DashboardSnapshot dashboard = runtime.GetDashboardSnapshot(logLimit: cap, outboxLimit: cap);
        McpRecentBridgeEventsPayload payload = new(
            dashboard.Logs.ToArray(),
            dashboard.Outbox.ToArray());
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerTool(Name = "pal_active_model_tier")]
    [Description(
        "Returns the currently active model tier id and model tag, plus the list of configured "
        + "tiers and their availability snapshot from the latest probe. Useful for operators who "
        + "want to confirm whether the small fast-start tier is in use or the large quality tier "
        + "has finished pulling.")]
    public static string GetActiveModelTier(
        PalLLM.Domain.Inference.ModelTierOrchestrator orchestrator,
        PalLLM.Domain.Configuration.PalLlmOptions options,
        PalLlmRuntime runtime)
    {
        McpActiveModelTierPayload payload = new(
            ActiveModel: orchestrator.GetActiveModel(),
            ActiveTierId: orchestrator.GetActiveTierId(),
            LastSeenAvailableModels: orchestrator.GetLastSeenAvailableModels().ToArray(),
            ConfiguredTiers: options.Inference.ModelTiers.ToArray(),
            Warmup: runtime.GetInferenceWarmupSnapshot());
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerTool(Name = "pal_model_collaboration")]
    [Description(
        "Returns a hardware-aware collaboration plan for the configured local models: which model should scout, "
        + "which should judge, when to run them in parallel versus sequentially, and which self-healing loops "
        + "fit the current lane mix for PalLLM's Palworld-mod work. Use it for runtime, bridge, HUD, screenshot, "
        + "docs-sync, and release-hardening decisions. For explicit hardware overrides, call the REST endpoint "
        + "`GET /api/inference/collaboration` with query parameters.")]
    public static string GetModelCollaboration(ModelCollaborationPlanner planner) =>
        JsonSerializer.Serialize(planner.GetSnapshot(), JsonOptions);

    [McpServerTool(Name = "pal_plan_model_collaboration_task")]
    [Description(
        "Plans the exact model-collaboration strategy for a concrete PalLLM task. Use this when you already know "
        + "the task, risk posture, and rough hardware budget and want a machine-readable answer about which lane "
        + "should scout, which should review, whether the run should be parallel or sequential, and which validator "
        + "gates must fire before promotion.")]
    public static string PlanModelCollaborationTask(
        ModelCollaborationDecisionPlanner planner,
        [Description("The concrete task the duo should solve. Required.")]
        string task,
        [Description("Optional task class label such as runtime, bridge, hud, screenshot-audit, docs-sync, release-hardening, or repo-audit.")]
        string? taskClass = null,
        [Description("Optional risk level override: low, medium, or high.")]
        string? riskLevel = null,
        [Description("True when the task will draft or execute many tool calls.")]
        bool? toolHeavy = null,
        [Description("True when the task inspects the Palworld HUD, dashboard, screenshots, or other player-facing surfaces.")]
        bool? frontendOrVisual = null,
        [Description("True when the task needs long-context or whole-repo reasoning.")]
        bool? largeContext = null,
        [Description("True when the task is about player-facing presentation output rather than bridge or data logic.")]
        bool? assetOrMedia = null,
        [Description("True when a vision-capable lane or screenshot review matters.")]
        bool? needsVision = null,
        [Description("True when the output is release-facing or otherwise near a ship gate.")]
        bool? releaseGate = null,
        [Description("True when the player-facing change is highly visible, ship-sensitive, or otherwise especially sensitive.")]
        bool? heroAsset = null,
        [Description("Optional GPU VRAM budget in GB.")]
        double? vramGb = null,
        [Description("Optional system RAM budget in GB.")]
        double? ramGb = null,
        [Description("Optional unified-memory budget in GB.")]
        double? unifiedMemoryGb = null,
        [Description("Set true for CPU-only operation.")]
        bool? cpuOnly = null,
        [Description("Set false to force baton passing even when the hardware could run lanes in parallel.")]
        bool? preferParallel = null,
        [Description("Optional free-form note listing the local quants or model variants currently available.")]
        string? availableQuants = null,
        [Description("Optional free-form note about the desired context window budget.")]
        string? contextBudget = null)
    {
        ModelCollaborationDecisionRequest request = new(
            Task: task,
            TaskClass: taskClass,
            RiskLevel: riskLevel,
            ToolHeavy: toolHeavy ?? false,
            FrontendOrVisual: frontendOrVisual ?? false,
            LargeContext: largeContext ?? false,
            AssetOrMedia: assetOrMedia ?? false,
            NeedsVision: needsVision ?? false,
            ReleaseGate: releaseGate ?? false,
            HeroAsset: heroAsset ?? false,
            VramGb: vramGb,
            RamGb: ramGb,
            UnifiedMemoryGb: unifiedMemoryGb,
            CpuOnly: cpuOnly ?? false,
            PreferParallel: preferParallel ?? true,
            AvailableQuants: availableQuants,
            ContextBudget: contextBudget);

        return JsonSerializer.Serialize(planner.Plan(request), JsonOptions);
    }

    [McpServerTool(Name = "pal_list_upstream_mcp")]
    [Description(
        "Returns the status and discovered primitives (tools, resources, prompts) of every "
        + "external MCP server PalLLM is configured to probe. Useful when the sidecar is acting "
        + "as an MCP hub - one endpoint lists what every connected upstream can do without "
        + "each MCP host having to query them individually. Read-only: this does not invoke "
        + "any upstream tool, just reflects the most recent discovery snapshot.")]
    public static string ListUpstreamMcp(McpUpstreamClientPool pool)
    {
        UpstreamSnapshot[] ordered = pool.GetSnapshots().Values
            .OrderBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();
        return JsonSerializer.Serialize(ordered, JsonOptions);
    }

    // ----------------------------------------------------------------
    // Self-awareness tools
    // ----------------------------------------------------------------
    // These mirror the AI-first HTTP endpoints (/api/describe, /api/quickstart,
    // /api/airgap/verify) as MCP tools so Claude Desktop / Cursor / VS Code
    // users can ask natural-language questions ("what is PalLLM?", "what
    // should I do next?", "are you offline?") and get the same
    // machine-readable payload without an HTTP round-trip. A fourth tool
    // exposes the latest self-healing evidence so an AI agent can answer
    // "is the watchdog running and happy?" in one call.

    [McpServerTool(Name = "pal_describe")]
    [Description(
        "Returns a one-shot self-description manifest for the running PalLLM instance: "
        + "identity (product / purpose / license), operator happiness score (0-100 + grade), "
        + "version, live current state (adapter / bridge / inference / vision / TTS / automation), "
        + "surface counts (routes / features / fallback strategies), posture guarantees "
        + "(local-first / fallback-always-available / opt-ins-default-off / trademarks), "
        + "common-ask shortcuts, and safety-tier notes. Use this on session start to learn "
        + "what PalLLM is and what it can do right now.")]
    public static string GetSelfDescription(
        PalLlmRuntime runtime,
        PalLLM.Domain.Configuration.PalLlmOptions options,
        Microsoft.AspNetCore.Routing.EndpointDataSource endpoints)
    {
        SelfDescription description = SelfDescriptionBuilder.Build(runtime, options, endpoints);
        return JsonSerializer.Serialize(description, JsonOptions);
    }

    [McpServerTool(Name = "pal_quickstart")]
    [Description(
        "Returns live state-aware 'what should I do next?' guidance derived from the current "
        + "RuntimeHealth + options. Overall status is one of 'ready' / 'needs-setup' / "
        + "'needs-attention'. Each step carries a priority (critical / recommended / optional), "
        + "a human label, a plain-English reason, the concrete action to take, and how to verify "
        + "it worked. Call this to help the operator finish setup or recover from a degraded state "
        + "without reading any docs.")]
    public static string GetQuickstartGuide(
        PalLlmRuntime runtime,
        PalLLM.Domain.Configuration.PalLlmOptions options,
        PalLLM.Domain.Inference.ModelRoleRegistry roleRegistry)
    {
        QuickstartGuide guide = QuickstartGuideBuilder.Build(runtime, options, roleRegistry);
        return JsonSerializer.Serialize(guide, JsonOptions);
    }

    [McpServerTool(Name = "pal_status")]
    [Description(
        "Returns a compact one-shot status summary of the runtime - the structured "
        + "equivalent of the 'pal status' CLI verb. Combines the OperatorHealthScore (numeric "
        + "Score + Grade), the active suggestion counts grouped by severity (urgent / warn / "
        + "info), the top suggestion Code (or null when healthy), and headline configuration "
        + "state (inference / vision / TTS configured, bridge enabled, loaded pack count, "
        + "outbox/inbox depth). Use this as the agent's 'is everything OK?' single tool call "
        + "instead of fetching pal_health_score + pal_health_suggestions + pal_describe "
        + "separately.")]
    public static string GetStatusSummary(PalLlmRuntime runtime)
    {
        RuntimeHealth health = runtime.GetHealth();
        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        int urgent = 0;
        int warn = 0;
        int info = 0;
        string? topCode = null;
        if (health.Suggestions.Count > 0)
        {
            // Builder already orders urgent-first (Pass 139), so [0] is the
            // most pressing entry without re-sorting here.
            topCode = health.Suggestions[0].Code;
            // Use the canonical Severity constants from
            // HealthSuggestionBuilder rather than raw string literals so a
            // future builder change (capitalisation tweak / new bucket /
            // rename) lights up at compile time instead of silently
            // miscounting here.
            foreach (HealthSuggestion s in health.Suggestions)
            {
                if (s.Severity == HealthSuggestionBuilder.Severity.Urgent)      urgent++;
                else if (s.Severity == HealthSuggestionBuilder.Severity.Warn)   warn++;
                else if (s.Severity == HealthSuggestionBuilder.Severity.Info)   info++;
            }
        }

        var summary = new
        {
            Score = score.Score,
            Grade = score.Grade,
            Summary = score.Summary,
            SuggestionsTotal = health.Suggestions.Count,
            SuggestionsUrgent = urgent,
            SuggestionsWarn = warn,
            SuggestionsInfo = info,
            TopSuggestionCode = topCode,
            InferenceConfigured = health.InferenceConfigured,
            InferenceCircuitState = health.InferenceCircuitState,
            VisionEnabled = health.VisionEnabled,
            TtsEnabled = health.TtsEnabled,
            AsrEnabled = health.AsrEnabled,
            BridgeEnabled = health.BridgeEnabled,
            LoadedPackCount = health.LoadedPackCount,
            OutboxPendingCount = health.OutboxPendingCount,
            InboxPendingCount = health.InboxPendingCount,
        };
        return JsonSerializer.Serialize(summary, JsonOptions);
    }

    [McpServerTool(Name = "pal_health_score")]
    [Description(
        "Returns the OperatorHealthScore for the current RuntimeHealth snapshot - a single "
        + "0-100 number plus grade (Excellent / Good / Degraded / Critical), one-paragraph "
        + "Summary, and the top three penalty Reasons that drove the score down. Companion to "
        + "pal_health_suggestions: the score is a coarse 'is anything degraded right now?' "
        + "verdict, the suggestions are the specific copy-paste actions. Call this when the "
        + "user asks 'is the runtime healthy?' or 'how's the companion doing?' for a one-shot "
        + "answer the agent can quote without parsing the full health snapshot.")]
    public static string GetOperatorHealthScore(PalLlmRuntime runtime)
    {
        OperatorHealthScore score = OperatorHealthScorer.Score(runtime.GetHealth());
        return JsonSerializer.Serialize(score, JsonOptions);
    }

    [McpServerTool(Name = "pal_health_suggestions")]
    [Description(
        "Returns the live operator-actionable Suggestions[] from the RuntimeHealth snapshot - "
        + "the same list that powers the 'pal next' advisor and surfaces in /api/health. Each entry "
        + "carries a stable kebab-case Code (e.g. 'no-packs-loaded', 'inference-circuit-open', "
        + "'bridge-idle'), a plain-English Message explaining the situation, and an optional "
        + "Command an operator can copy-paste to address it. The list is empty when the runtime "
        + "is healthy. Call this when the user asks 'what should I do right now?' or 'is anything "
        + "broken?' - it's faster and more focused than full RuntimeHealth and gives the AI agent "
        + "the same actionable hints a human operator sees.")]
    public static string GetHealthSuggestions(PalLlmRuntime runtime)
    {
        RuntimeHealth health = runtime.GetHealth();
        return JsonSerializer.Serialize(health.Suggestions, JsonOptions);
    }

    [McpServerTool(Name = "pal_airgap_verify")]
    [Description(
        "Classifies every enabled outbound surface (inference, vision, TTS, OTLP, MCP upstreams) "
        + "as loopback / private / public / disabled / unknown so the caller can prove PalLLM's "
        + "air-gap posture without running a packet capture. NEVER opens a TCP connection or "
        + "emits a live request - pure host-string inspection + bounded DNS resolution. Overall "
        + "verdict is one of 'strict-airgapped' / 'lan-airgapped' / 'not-airgapped' / "
        + "'indeterminate'. Use this before sending sensitive input to verify the instance "
        + "makes no outbound calls off this machine.")]
    public static string GetAirGapReport(PalLLM.Domain.Configuration.PalLlmOptions options)
    {
        AirGapReport report = AirGapVerifier.VerifyCached(options);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    [McpServerTool(Name = "pal_self_healing_status")]
    [Description(
        "Returns the latest SelfHealingWorker evidence artifact: when the watchdog last ticked, "
        + "how many orphan outbox envelopes it archived (if any), the operator-health score it "
        + "observed, and any structured observations or actions. Use this to confirm the "
        + "background janitor is alive and the sidecar is stable over long sessions. Returns "
        + "a 'watchdog has not ticked yet' marker if no evidence file exists yet.")]
    public static string GetSelfHealingStatus(PalLLM.Domain.Configuration.PalLlmOptions options)
    {
        // Shared reader also powers /api/self-healing/status and the dashboard
        // chip so every consumer sees the same payload + pending-marker contract.
        using JsonDocument doc = SelfHealingStatusReader.Read(options);
        return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
    }

    [McpServerTool(Name = "pal_promotion_apply_preview")]
    [Description(
        "Returns a concrete, editor-ready change template for a specific promotion candidate. Requires "
        + "a task class that already passes the stability gate in the live ledger (otherwise returns an "
        + "error message). Output includes DiffPreview (file path + before-context + after-code in a "
        + "Markdown-friendly block), SafetyWarnings (up to 3 short sentences operators must check), "
        + "RollbackCommand (single-line git checkout), and Provenance (ProofPacket tagged "
        + "promotion-apply-preview with HumanReviewRequired=true). Deterministic — no file reads, no "
        + "inference call. Use this after pal_promotion_suggestions identifies a candidate to get the "
        + "actual change the operator would hand-apply.")]
    public static string GetPromotionApplyPreview(
        PalLLM.Domain.Runtime.PromotionLedger ledger,
        [Description("Task class name as recorded in the ledger (e.g. 'fallback-director', 'live-inference').")] string taskClass,
        [Description("Optional specific pattern id within that task class. Defaults to the task's most-common pattern.")] string? patternId = null)
    {
        if (string.IsNullOrWhiteSpace(taskClass))
        {
            return SerializeStatus("rejected", "taskClass is required.");
        }

        PalLLM.Domain.Runtime.PromotionSummary summary = ledger.Snapshot();
        PalLLM.Domain.Runtime.PromotionTaskSummary? task = summary.Tasks.FirstOrDefault(t =>
            string.Equals(t.TaskClass, taskClass, StringComparison.Ordinal));
        if (task is null)
        {
            return SerializeStatus("not-found", $"No observations recorded against task class '{taskClass}'.");
        }
        if (!task.IsPromotionCandidate)
        {
            return SerializeStatus("not-a-candidate", task.Recommendation);
        }

        string effectivePattern = string.IsNullOrWhiteSpace(patternId)
            ? (task.MostCommonPatternId ?? "(unspecified)")
            : patternId.Trim();

        PalLLM.Domain.Runtime.PromotionTaskSummary pinned = task with { MostCommonPatternId = effectivePattern };
        PalLLM.Domain.Runtime.PromotionSuggestion suggestion =
            PalLLM.Domain.Runtime.PromotionSuggestionBuilder.BuildForTask(pinned, summary.CapturedAtUtc);
        PalLLM.Domain.Runtime.PromotionApplyPreview preview =
            PalLLM.Domain.Runtime.PromotionApplyPreviewBuilder.Build(suggestion);
        return JsonSerializer.Serialize(preview, JsonOptions);
    }

    [McpServerTool(Name = "pal_mood_weather")]
    [Description(
        "Pass 38 / C10 — deterministic mood-weather forecast per character. Blends the current "
        + "RelationshipTracker record (affinity, last tone) with the world snapshot (threat level, player "
        + "health, time-of-day) to produce a short mood bucket (content/uneasy/agitated/affectionate/"
        + "weary/wary), a weather metaphor (clear-sky/partly-cloudy/cold-front/storm-front/cloudburst/"
        + "golden-hour/twilight), and a tone word. Returns 404 if no relationship exists for the id yet.")]
    public static string GetMoodWeather(
        PalLLM.Domain.Runtime.PalLlmRuntime runtime,
        [Description("Tracked character id. Must have chatted at least once.")] int characterId)
    {
        PalLLM.Domain.Runtime.CharacterRelationship? rel = runtime.GetRelationships()
            .FirstOrDefault(r => r.CharacterId == characterId);
        if (rel is null)
        {
            return SerializeStatus("not-found", $"No relationship record for character id {characterId}.");
        }
        PalLLM.Domain.Runtime.MoodWeather forecast = PalLLM.Domain.Runtime.MoodWeatherAdvisor.Forecast(
            rel,
            runtime.Adapter.Snapshot);
        return JsonSerializer.Serialize(forecast, JsonOptions);
    }

    [McpServerTool(Name = "pal_narration_cue")]
    [Description(
        "Pass 36 / C2 — world-narration advisor. Returns { ShouldNarrate, Trigger, PromptFragment, "
        + "MinimumGapSeconds, Reason } based on the current GameWorldSnapshot. Triggers: combat-start, "
        + "threat-spike, night-fall, weather-change, low-health, objective-update. Use this before asking "
        + "the companion to narrate so you don't over-narrate: drop cues when Trigger=no-trigger or "
        + "rate-limited.")]
    public static string GetNarrationCue(PalLLM.Domain.Runtime.PalLlmRuntime runtime)
    {
        PalLLM.Domain.Runtime.NarrationCue cue = PalLLM.Domain.Runtime.WorldNarrationAdvisor.Advise(
            runtime.Adapter.Snapshot,
            lastNarrationUtc: null);
        return JsonSerializer.Serialize(cue, JsonOptions);
    }

    [McpServerTool(Name = "pal_resource_budgets")]
    [Description(
        "Pass 35 / D10 — resource-budget posture. Enumerates every tracked runtime budget (inference rate "
        + "limit, circuit breaker, vision queue depth, TTS character cap, memory window, bridge outbox "
        + "retention, chat fallback share) with a configured ceiling, current consumption, source config "
        + "key, and status bucket (ok / review / exhausted / unknown). Pure advisory — never mutates "
        + "counters. Use this to answer 'which knob should I tune?' from a single deterministic view.")]
    public static string GetResourceBudgets(
        PalLLM.Domain.Configuration.PalLlmOptions options,
        PalLLM.Domain.Runtime.PalLlmMetrics metrics)
    {
        PalLLM.Domain.Runtime.ResourceBudgetMetrics derived =
            PalLLM.Domain.Runtime.ResourceBudgetMetrics.FromSnapshot(metrics.Snapshot());
        PalLLM.Domain.Runtime.ResourceBudgetPosture posture =
            PalLLM.Domain.Runtime.ResourceBudgetPostureBuilder.CaptureCached(options, derived);
        return JsonSerializer.Serialize(posture, JsonOptions);
    }

    [McpServerTool(Name = "pal_degradation_advisory")]
    [Description(
        "Pass 33 / D2 — graceful-degradation advisory. Inspects the current hardware + options and "
        + "returns a posture classification (CpuOnlyConstrained / CpuOnlyCapable / GpuEntry / Standard / "
        + "NoDegradation) plus ordered recommendations (keep / disable / review / opt-in / leave-off). "
        + "Use this to answer 'my laptop has no GPU, can I still play?' — yes, in deterministic-first "
        + "mode. Pure advisory — never mutates runtime state.")]
    public static string GetDegradationAdvisory(PalLLM.Domain.Configuration.PalLlmOptions options)
    {
        PalLLM.Domain.Inference.HardwareProfile profile =
            PalLLM.Domain.Inference.HardwareProfiler.CaptureCached(options.Hardware.ForceTier);
        PalLLM.Domain.Runtime.DegradationAdvisory advisory =
            PalLLM.Domain.Runtime.GracefulDegradationAdvisor.Recommend(profile, options);
        return JsonSerializer.Serialize(advisory, JsonOptions);
    }

    [McpServerTool(Name = "pal_directives_plan")]
    [Description(
        "Pass 31 / C3 — deterministic directive translator. Given a natural-language player utterance "
        + "(e.g. 'hey helper, stop mining and help me fight'), returns an ordered list of allowlisted "
        + "PalDirective entries the UE4SS mod can forward to the native pal-AI controller. Never emits "
        + "above PalLLM:Automation:AllowedActions — candidates that match a cue but aren't allowlisted "
        + "land in RejectedCandidates[] with a reason. Empty utterance or nothing matched returns an "
        + "empty Directives[] with Reason explaining why. No inference, no side effects — pure advisory.")]
    public static string PostDirectivePlan(
        PalLLM.Domain.Configuration.PalLlmOptions options,
        [Description("The player's natural-language utterance.")] string utterance,
        [Description("Optional pal name the utterance addresses.")] string? addressedPal = null)
    {
        PalLLM.Domain.Runtime.DirectivePlan plan = PalLLM.Domain.Runtime.DirectiveIntentTranslator.Translate(
            utterance,
            options.Automation.AllowedActions,
            addressedPal);
        return JsonSerializer.Serialize(plan, JsonOptions);
    }

    [McpServerTool(Name = "pal_vision_describe")]
    [Description(
        "Pass 30 — on-demand vision describe. Given a base64-encoded image, run it through the configured "
        + "vision endpoint (if enabled) and return the plain-text description. Off by default — returns "
        + "status='disabled' when PalLLM:Vision:Enabled is false. When enabled, pairs with the existing "
        + "screenshot-watcher path but lets AI agents push a frame directly (e.g. 'here's a screenshot — "
        + "what am I looking at?'). Deterministic fallback: if the upstream vision endpoint fails, returns "
        + "status='failed' with a brief diagnostic instead of throwing.")]
    public static async Task<string> DescribeImageAsync(
        PalLLM.Domain.Runtime.PalLlmRuntime runtime,
        [Description("Base64-encoded image payload (no 'data:' prefix — the client adds the MIME header).")] string imageBase64,
        [Description("MIME type of the image. Defaults to image/png.")] string? imageMimeType = null,
        [Description("Optional free-form prompt. Leave blank to get a terse default scene description.")] string? prompt = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return SerializeStatus("rejected", "imageBase64 is required.");
        }

        var request = new PalLLM.Domain.Integration.VisionDescribeRequest
        {
            ImageBase64 = imageBase64,
            ImageMimeType = string.IsNullOrWhiteSpace(imageMimeType) ? "image/png" : imageMimeType,
            Prompt = prompt,
        };
        PalLLM.Domain.Integration.VisionDescribeResponse response = await runtime.DescribeImageAsync(request, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(response, JsonOptions);
    }

    [McpServerTool(Name = "pal_privacy_posture")]
    [Description(
        "Pass 27 — machine-readable privacy posture. Enumerates every data-emitting surface PalLLM ships "
        + "(conversation memory, dashboard, health probes, proof packets, live inference, vision, TTS, OTLP "
        + "telemetry, upstream MCP, narrative packs, crash reports, update checks, analytics) and classifies "
        + "each as 'never-leaves', 'only-with-opt-in', or 'leaves-by-default'. Returns { CapturedAtUtc, "
        + "Headline, NeverLeavesCount, OptInAvailableCount, ActiveOutboundCount, Surfaces[] }. Use this to "
        + "answer 'what does this install actually transmit?' or to generate a privacy disclosure for a "
        + "distribution listing. Pairs with pal_airgap_verify (endpoint-scope view).")]
    public static string GetPrivacyPosture(PalLLM.Domain.Configuration.PalLlmOptions options)
    {
        PalLLM.Domain.Runtime.PrivacyPosture posture =
            PalLLM.Domain.Runtime.PrivacyPostureBuilder.CaptureCached(options);
        return JsonSerializer.Serialize(posture, JsonOptions);
    }

    [McpServerTool(Name = "pal_hardware_profile")]
    [Description(
        "Pass 25 — deterministic hardware posture of the PalLLM host box. Returns OS, logical core count, "
        + "physical RAM (rounded GiB), GPU-likelihood signal (env-var cue or driver-marker path), detected "
        + "DuoHardwareTier (Constrained / Standard / Generous), effective tier after any PalLLM:Hardware:ForceTier "
        + "override, detection confidence, and a one-sentence recommendation. Never launches tools, never "
        + "talks to the network. Pairs with pal_model_roles and pal_duo_plan: pal_hardware_profile tells you "
        + "what the box can handle, pal_duo_plan tells you what pattern to run on it.")]
    public static string GetHardwareProfile(
        PalLLM.Domain.Configuration.PalLlmOptions options)
    {
        PalLLM.Domain.Inference.HardwareProfile profile =
            PalLLM.Domain.Inference.HardwareProfiler.CaptureCached(options.Hardware.ForceTier);
        return JsonSerializer.Serialize(profile, JsonOptions);
    }

    [McpServerTool(Name = "pal_promotion_apply")]
    [Description(
        "Pass 24 — persist a Pass-14 promotion preview as a durable staging triple (template + rollback "
        + "+ provenance packet) under the configured staging root. NEVER mutates source code. Gated by "
        + "PalLLM:PromotionApply:AllowApply=false by default; returns status='refused' when off. When on, "
        + "returns status='staged' with the absolute paths of the three written files. Pairs with "
        + "pal_promotion_apply_preview: preview first to see what would be applied, then apply to persist "
        + "a human-reviewable artifact set. Rollback is deleting the staged triple.")]
    public static string PostPromotionApply(
        PalLLM.Domain.Runtime.PromotionLedger ledger,
        PalLLM.Domain.Configuration.PalLlmOptions options,
        [Description("Task class name as recorded in the ledger.")] string taskClass,
        [Description("Optional specific pattern id within that task class. Defaults to the task's most-common pattern.")] string? patternId = null)
    {
        if (string.IsNullOrWhiteSpace(taskClass))
        {
            return SerializeStatus("rejected", "taskClass is required.");
        }

        PalLLM.Domain.Runtime.PromotionSummary applySummary = ledger.Snapshot();
        PalLLM.Domain.Runtime.PromotionTaskSummary? applyTask = applySummary.Tasks.FirstOrDefault(t =>
            string.Equals(t.TaskClass, taskClass, StringComparison.Ordinal));
        if (applyTask is null)
        {
            return SerializeStatus("not-found", $"No observations recorded against task class '{taskClass}'.");
        }
        if (!applyTask.IsPromotionCandidate)
        {
            return SerializeStatus("not-a-candidate", applyTask.Recommendation);
        }

        string effectivePattern = string.IsNullOrWhiteSpace(patternId)
            ? (applyTask.MostCommonPatternId ?? "(unspecified)")
            : patternId.Trim();

        PalLLM.Domain.Runtime.PromotionTaskSummary pinnedApply = applyTask with { MostCommonPatternId = effectivePattern };
        PalLLM.Domain.Runtime.PromotionSuggestion applySuggestion =
            PalLLM.Domain.Runtime.PromotionSuggestionBuilder.BuildForTask(pinnedApply, applySummary.CapturedAtUtc);
        PalLLM.Domain.Runtime.PromotionApplyPreview applyPreview =
            PalLLM.Domain.Runtime.PromotionApplyPreviewBuilder.Build(applySuggestion);
        PalLLM.Domain.Runtime.PromotionApplyResult result =
            PalLLM.Domain.Runtime.PromotionApplier.Apply(applyPreview, options);
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "pal_promotion_suggestions")]
    [Description(
        "Returns one actionable suggestion per promotion candidate: concrete target file "
        + "(e.g. 'src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs'), one-sentence suggested change, "
        + "evidence summary with counts + rate, rollback path if the hard-code misfires, and a full "
        + "ProofPacket with HumanReviewRequired=true so the suggestion itself has audit provenance. "
        + "Non-candidate tasks are skipped. Use this to answer 'which AI patterns are ready to "
        + "hard-code, and exactly what would the change be?'")]
    public static string GetPromotionSuggestions(PalLLM.Domain.Runtime.PromotionLedger ledger)
    {
        PalLLM.Domain.Runtime.PromotionSummary summary = ledger.Snapshot();
        PalLLM.Domain.Runtime.PromotionSuggestionSet set = PalLLM.Domain.Runtime.PromotionSuggestionBuilder.Build(summary);
        return JsonSerializer.Serialize(set, JsonOptions);
    }

    [McpServerTool(Name = "pal_promotion_summary")]
    [Description(
        "Returns the hard-code promotion-ledger summary: per-task-class counts (total / success / "
        + "disagreement-block / validator-fail / human-override), success rate, most-common pattern, "
        + "and an IsPromotionCandidate flag. Promotion criterion is conservative: >=20 observations, "
        + ">=95% success rate, and zero disagreement-block / human-override in the last 10. Use to answer "
        + "'which AI-assisted patterns are stable enough to hard-code into deterministic product logic?'")]
    public static string GetPromotionSummary(PalLLM.Domain.Runtime.PromotionLedger ledger)
    {
        PalLLM.Domain.Runtime.PromotionSummary summary = ledger.Snapshot();
        return JsonSerializer.Serialize(summary, JsonOptions);
    }

    [McpServerTool(Name = "pal_promotion_record")]
    [Description(
        "Record one observation in the hard-code promotion ledger. Outcome must be one of: 'success', "
        + "'disagreement-block', 'validator-fail', 'human-override'. Task class and pattern id are "
        + "operator-defined strings (e.g. task='ImplementDraft', pattern='duo-branch-tournament'). Use to "
        + "feed the ledger from any automated decision path; combined with pal_promotion_summary, this "
        + "lets an AI agent ask 'has this pattern been stable enough to rely on?' before trusting it.")]
    public static string RecordPromotionObservation(
        PalLLM.Domain.Runtime.PromotionLedger ledger,
        [Description("Task class identifier (e.g. 'ImplementDraft', 'Audit', 'MediaPrompting').")] string taskClass,
        [Description("Pattern identifier (e.g. 'duo-branch-tournament', 'fallback-strategy:stealth-withdraw').")] string patternId,
        [Description("Outcome: 'success', 'disagreement-block', 'validator-fail', or 'human-override'.")] string outcome,
        [Description("Optional free-form note (kept on the observation for audit).")] string? note = null)
    {
        if (string.IsNullOrWhiteSpace(taskClass))
        {
            return SerializeStatus("rejected", "taskClass is required.");
        }

        if (string.IsNullOrWhiteSpace(patternId))
        {
            return SerializeStatus("rejected", "patternId is required.");
        }

        if (string.IsNullOrWhiteSpace(outcome))
        {
            return SerializeStatus("rejected", "outcome is required.");
        }

        if (!PalLLM.Domain.Runtime.PromotionLedger.TryNormalizeOutcome(outcome, out _))
        {
            return SerializeStatus(
                "rejected",
                $"Outcome must be one of: {string.Join(", ", PalLLM.Domain.Runtime.PromotionLedger.AllowedOutcomeValues)}.");
        }

        PalLLM.Domain.Runtime.PromotionObservation observation = ledger.Record(taskClass, patternId, outcome, note);
        return JsonSerializer.Serialize(observation, JsonOptions);
    }

    [McpServerTool(Name = "pal_disagreement_check")]
    [Description(
        "Compare two model outputs and emit a structured disagreement verdict. Returns {SemanticSimilarity, "
        + "TokenOverlap, LengthRatio, CombinedScore, Verdict (agree/minor-drift/major-disagreement), "
        + "SafetySignal (proceed/review/block), Recommendation, KeyEntityAgreement[]}. Use to complete the "
        + "ParallelDisagreement cooperation pattern: turn 'did the Worker and Judge give the same answer?' "
        + "into a first-class safety signal that blocks auto-promotion on disagreement. Deterministic — "
        + "never calls live inference.")]
    public static string CheckDisagreement(
        [Description("The Worker model's output.")] string workerOutput,
        [Description("The Judge model's output.")] string judgeOutput)
    {
        PalLLM.Domain.Runtime.DisagreementAnalysis analysis =
            PalLLM.Domain.Runtime.DisagreementDetector.Compare(workerOutput, judgeOutput);
        return JsonSerializer.Serialize(analysis, JsonOptions);
    }

    [McpServerTool(Name = "pal_proof_packet")]
    [Description(
        "Build a machine-readable proof packet for an automated decision. Returns {Version, Id, Subsystem, "
        + "Decision, PrimaryReason, CapturedAtUtc, Evidence[], ModelArtifacts[], ValidatorResults[], "
        + "RollbackPath, Confidence, HumanReviewRequired}. Id is a stable SHA-256 hash of subsystem + "
        + "decision + captured-at so the same decision doesn't produce duplicate packets. Use to attach "
        + "provenance to any AI-assisted change so operators can reconstruct 'who decided / why / how to "
        + "undo it' without reading server logs.")]
    public static string BuildProofPacket(
        [Description("Subsystem that made the decision (e.g. 'fallback-director', 'self-healing-watchdog', 'duo-planner').")] string subsystem,
        [Description("Short decision string (e.g. 'strategy=stealth-withdraw', 'archived-orphans=3').")] string decision,
        [Description("One-sentence primary reason for the decision.")] string primaryReason,
        [Description("Comma-separated evidence lines (optional).")] string? evidence = null,
        [Description("How to undo this decision.")] string? rollbackPath = null,
        [Description("Confidence in the decision: high / medium / low. Defaults to medium.")] string? confidence = null,
        [Description("Whether a human should review before trusting the decision. Defaults to false.")] bool humanReviewRequired = false)
    {
        string[] evidenceLines = string.IsNullOrWhiteSpace(evidence)
            ? Array.Empty<string>()
            : evidence.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        PalLLM.Domain.Runtime.ProofPacket packet = PalLLM.Domain.Runtime.ProofPacketBuilder.Build(
            subsystem: subsystem,
            decision: decision,
            primaryReason: primaryReason ?? string.Empty,
            evidenceLines: evidenceLines,
            rollbackPath: rollbackPath ?? "(no rollback path recorded)",
            confidence: confidence ?? "medium",
            humanReviewRequired: humanReviewRequired);
        return JsonSerializer.Serialize(packet, JsonOptions);
    }

    [McpServerTool(Name = "pal_chat_plan")]
    [Description(
        "Advisory: return the Qwen Duo cooperation pattern the planner WOULD pick for a specific chat "
        + "request. Infers the DuoTaskKind from the user message (via a deterministic keyword classifier) "
        + "and routes it through the Pass-8 planner with the operator's risk + hardware preference. "
        + "Deterministic — no inference call, no runtime mutation. Use this before sending a chat turn "
        + "to see how it would flow through the mesh, or to plan a script that will fan out many turns "
        + "efficiently.")]
    public static string GetChatPlanAdvice(
        PalLLM.Domain.Inference.DuoOrchestratorPlanner planner,
        PalLLM.Domain.Inference.ModelRoleRegistry registry,
        [Description("The user message whose task-kind will be inferred.")] string userMessage,
        [Description("Optional explicit task tag — must match one of the DuoTaskKind enum names (case-insensitive) to override keyword inference.")] string? taskTag = null,
        [Description("Risk level: Low / Medium / High. Defaults to Low.")] string? risk = null,
        [Description("Hardware tier: Constrained / Standard / Generous. Defaults to Standard.")] string? hardware = null)
    {
        var request = new ChatPlanRequest
        {
            UserMessage = userMessage,
            TaskTag = taskTag,
            Risk = risk,
            Hardware = hardware,
        };
        ChatPlanAdvice advice = ChatPlanAdvisor.Advise(request, planner, registry);
        return JsonSerializer.Serialize(advice, JsonOptions);
    }

    [McpServerTool(Name = "pal_duo_plan")]
    [Description(
        "Return the recommended Qwen Duo Mesh cooperation pattern for a task. Deterministic — never calls "
        + "live inference — so the tool is always available. Accepts task kind (CommandRouting / ImplementDraft "
        + "/ ArchitecturePlan / Audit / ParallelCandidates / FinalSynthesis / LongContextSynthesis / "
        + "ToolExecution / HighRisk / MediaPrompting), risk (Low/Medium/High), and hardware tier "
        + "(Constrained / Standard / Generous). Returns one of the ten cooperation patterns with per-step "
        + "role assignments, thinking-mode hints, context-budget hints, and an escalation path. Pairs with "
        + "pal_model_roles: pal_model_roles tells you what's bound; pal_duo_plan tells you how to use it.")]
    public static string GetDuoPlan(
        PalLLM.Domain.Inference.DuoOrchestratorPlanner planner,
        [Description("Task kind. One of: CommandRouting, ImplementDraft, ArchitecturePlan, Audit, ParallelCandidates, FinalSynthesis, LongContextSynthesis, ToolExecution, HighRisk, MediaPrompting.")] string? kind = null,
        [Description("Risk level. One of: Low, Medium, High. Defaults to Low.")] string? risk = null,
        [Description("Hardware tier. One of: Constrained (8-20GB VRAM or CPU), Standard (24-32GB), Generous (48GB+). Defaults to Standard.")] string? hardware = null)
    {
        var request = new PalLLM.Domain.Inference.DuoPlanRequest
        {
            Kind = Enum.TryParse(kind, ignoreCase: true, out PalLLM.Domain.Inference.DuoTaskKind k) ? k : PalLLM.Domain.Inference.DuoTaskKind.ImplementDraft,
            Risk = Enum.TryParse(risk, ignoreCase: true, out PalLLM.Domain.Inference.DuoRiskLevel r) ? r : PalLLM.Domain.Inference.DuoRiskLevel.Low,
            Hardware = Enum.TryParse(hardware, ignoreCase: true, out PalLLM.Domain.Inference.DuoHardwareTier h) ? h : PalLLM.Domain.Inference.DuoHardwareTier.Standard,
        };
        PalLLM.Domain.Inference.DuoPlan plan = planner.Plan(request);
        return JsonSerializer.Serialize(plan, JsonOptions);
    }

    [McpServerTool(Name = "pal_model_roles")]
    [Description(
        "Returns the coverage of the local-first AI mesh roles (Edge / Worker / Judge / Media / Validator). "
        + "For each role, reports whether the operator has bound a local endpoint, which bindings are active, "
        + "and what a good pairing would look like for the current setup. Use this when an AI agent is "
        + "planning tasks that need to split drafting (Worker) from auditing (Judge), or when answering "
        + "'what local models has this operator configured?'")]
    public static string GetModelRoleCoverage(PalLLM.Domain.Inference.ModelRoleRegistry registry)
    {
        PalLLM.Domain.Inference.ModelRoleCoverage coverage = registry.GetCoverage();
        return JsonSerializer.Serialize(coverage, JsonOptions);
    }

    [McpServerTool(Name = "pal_why")]
    [Description(
        "Answer a natural-language causal question about the PalLLM runtime's recent behaviour. "
        + "Deterministic-first: never calls out to live inference, so the tool is always available "
        + "and ships the same structured answer shape regardless of whether live inference is off "
        + "or down. Understands keywords like 'fallback', 'bypass', 'circuit', 'bridge', 'health', "
        + "'rate limit', and 'thermal'; falls through to a grounded posture explanation when the "
        + "question doesn't match a specific intent. Use this when an operator or player asks "
        + "'why did my companion say that?' or 'why is the bridge not ready?' and you need a "
        + "structured, cite-able answer.")]
    public static string AnswerWhy(
        PalLlmRuntime runtime,
        PalLlmMetrics metrics,
        [Description("The natural-language question to explain. Short and concrete works best; e.g. 'why did my last reply come from the fallback?' or 'why is the bridge not ready?'")] string question)
    {
        WhyAnswer answer = WhyEngine.Answer(
            question,
            runtime.GetHealth(),
            metrics.Snapshot(),
            OperatorHealthScorer.Score(runtime.GetHealth()),
            runtime.Adapter.Snapshot);
        return JsonSerializer.Serialize(answer, JsonOptions);
    }
}
