using Microsoft.AspNetCore.Routing;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Builds the <c>/api/describe</c> self-description manifest. One endpoint,
/// one payload — an AI assistant (Claude Desktop, Cursor, VS Code, a custom
/// MCP client) can call it once on connect and learn everything it needs to
/// know about the running PalLLM instance without scraping docs or making
/// multiple round-trips.
///
/// <para>This is deliberately concise: the full feature catalog, the full
/// route inventory, and the full dashboard are still reachable at their own
/// endpoints. <c>/api/describe</c> returns the summary an LLM needs to decide
/// which specific endpoint or tool to reach for next.</para>
///
/// <para>Shipping a dedicated self-description surface is a 2026 MCP
/// best-practice equivalent of HATEOAS for JSON APIs: every caller, human or
/// otherwise, can learn what the server offers without hard-coding anything.</para>
/// </summary>
internal static class SelfDescriptionBuilder
{
    public static SelfDescription Build(
        PalLlmRuntime runtime,
        PalLlmOptions options,
        EndpointDataSource endpoints)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoints);

        IReadOnlyList<FeatureDescriptor> features = runtime.GetFeatures();
        int ready = features.Count(f => string.Equals(f.Status, "ready", StringComparison.OrdinalIgnoreCase));
        int scaffolded = features.Count(f => string.Equals(f.Status, "scaffolded", StringComparison.OrdinalIgnoreCase));
        int deferred = features.Count(f => string.Equals(f.Status, "deferred", StringComparison.OrdinalIgnoreCase));

        int apiRoutes = 0;
        foreach (Endpoint endpoint in endpoints.Endpoints)
        {
            if (endpoint is RouteEndpoint re && re.RoutePattern.RawText is { } raw)
            {
                if (raw.StartsWith("api/", StringComparison.OrdinalIgnoreCase) ||
                    raw.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                {
                    apiRoutes++;
                }
            }
        }

        RuntimeHealth health = runtime.GetHealth();

        CommonAsk[] commonAsks =
        [
            new("Ask what PalLLM is",
                "GET /api/describe — this endpoint; one-shot manifest of the running instance."),
            new("Hear from the companion",
                "POST /api/chat with { userMessage, characterId? } — deterministic fallback always replies; live inference answers when enabled."),
            new("Inspect the game world the companion sees",
                "GET /api/world — current in-game snapshot + recent bridge events."),
            new("List what PalLLM can do",
                "GET /api/features — all feature-catalog entries with ready/scaffolded/deferred status."),
            new("Read the live readiness posture",
                "GET /api/release/readiness — machine-readable release snapshot with proof/package/audit evidence."),
            new("Recall what the companion remembers",
                "POST /api/memory/recall with { characterId, userMessage }."),
            new("Describe what's on screen",
                "POST /api/vision/describe with an image URL or base64 payload (live vision) or GET /api/world (snapshot fallback)."),
            new("Synthesize spoken audio",
                "POST /api/tts/synthesize with { text, voice? } — optional; off by default."),
            new("Transcribe player audio",
                "POST /api/audio/transcribe with bounded base64 audio - optional; off by default."),
            new("Use PalLLM from an MCP-capable client",
                "POST /mcp (JSON-RPC 2.0 over streamable HTTP, protocol version 2025-06-18). 38 tools, 6 resources + 1 template, 4 prompts."),
        ];

        OperatorHealthScore score = OperatorHealthScorer.Score(health);

        return new SelfDescription(
            Identity: new(
                Product: "PalLLM",
                Purpose: "Local-first LLM companion runtime with a self-owned portable adapter surface. Currently shipping a Lua bridge for a UE4SS-enabled game target (Palworld).",
                License: "MIT",
                Redistributable: true),
            OperatorHealth: score,
            Version: new(
                Sidecar: typeof(SelfDescriptionBuilder).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                McpProtocol: "2025-06-18",
                AuditedOn: "2026-05-17"),
            CurrentState: new(
                AdapterName: health.AdapterName,
                AdapterReady: health.AdapterReady,
                BridgeEnabled: health.BridgeEnabled,
                InferenceConfigured: health.InferenceConfigured,
                InferenceActiveModel: health.InferenceActiveModel,
                VisionEnabled: health.VisionEnabled,
                TtsEnabled: health.TtsEnabled,
                AsrEnabled: health.AsrEnabled,
                AutomationEnabled: health.AutomationEnabled,
                FallbackAlwaysAvailable: true),
            Surface: new(
                ApiRouteCount: apiRoutes,
                FeatureCount: features.Count,
                FeatureReady: ready,
                FeatureScaffolded: scaffolded,
                FeatureDeferred: deferred,
                FallbackStrategyCount: 19,
                DocumentationIndex: "docs/INDEX.md"),
            PostureGuarantees: new(
                LocalFirst: "No hard cloud dependency. Live inference is an opt-in.",
                DeterministicFallbackAlwaysAvailable: "A working reply exists even when every external dependency is down.",
                OptInsDefaultOff: "Inference, vision, TTS, ASR, action intents, screenshot watcher, thermal gate, API-key auth, and OTLP export are all off by default and individually reversible.",
                ThirdPartyLiability: "All third-party references are interoperability references; see NOTICE.md and THIRD_PARTY_NOTICES.md.",
                Trademarks: "All trademarks remain with their respective owners; PalLLM asserts no association."),
            CommonAsks: commonAsks,
            SafetyNotes: new(
                EmergencyFallbackTier: "Third-tier EmergencyFallback guards every deterministic director call; even a broken pack or throwing strategy hands the player a canned acknowledgement instead of crashing the chat turn.",
                CircuitBreaker: "Inference circuit breaker trips on consecutive failures and short-circuits to fallback while it cools down.",
                ThermalGate: health.InferenceConfigured && options.Inference.ThermalGate.Enabled
                    ? $"Opt-in thermal gate is ON (reject >= {options.Inference.ThermalGate.RejectAboveC}°C); live inference routes to fallback on throttled GPUs."
                    : "Opt-in thermal gate is off by default; live inference is not gated on GPU temperature.",
                RateLimit: options.Fallback.MaxCharacterRequestsPerMinute > 0
                    ? $"Per-character sliding-window rate limiter caps live inference at {options.Fallback.MaxCharacterRequestsPerMinute} requests/min; excess traffic routes to fallback."
                    : "Per-character rate limiter is off by default."));
    }
}

public sealed record SelfDescription(
    SelfDescriptionIdentity Identity,
    OperatorHealthScore OperatorHealth,
    SelfDescriptionVersion Version,
    SelfDescriptionState CurrentState,
    SelfDescriptionSurface Surface,
    SelfDescriptionPosture PostureGuarantees,
    IReadOnlyList<CommonAsk> CommonAsks,
    SelfDescriptionSafety SafetyNotes);

public sealed record SelfDescriptionIdentity(
    string Product,
    string Purpose,
    string License,
    bool Redistributable);

public sealed record SelfDescriptionVersion(
    string Sidecar,
    string McpProtocol,
    string AuditedOn);

public sealed record SelfDescriptionState(
    string AdapterName,
    bool AdapterReady,
    bool BridgeEnabled,
    bool InferenceConfigured,
    string InferenceActiveModel,
    bool VisionEnabled,
    bool TtsEnabled,
    bool AsrEnabled,
    bool AutomationEnabled,
    bool FallbackAlwaysAvailable);

public sealed record SelfDescriptionSurface(
    int ApiRouteCount,
    int FeatureCount,
    int FeatureReady,
    int FeatureScaffolded,
    int FeatureDeferred,
    int FallbackStrategyCount,
    string DocumentationIndex);

public sealed record SelfDescriptionPosture(
    string LocalFirst,
    string DeterministicFallbackAlwaysAvailable,
    string OptInsDefaultOff,
    string ThirdPartyLiability,
    string Trademarks);

public sealed record SelfDescriptionSafety(
    string EmergencyFallbackTier,
    string CircuitBreaker,
    string ThermalGate,
    string RateLimit);

public sealed record CommonAsk(string Goal, string How);
