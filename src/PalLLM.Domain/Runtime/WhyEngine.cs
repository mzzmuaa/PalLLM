using PalLLM.Domain.Integration;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Local-first "why engine". Answers natural-language causal
//            questions about the PalLLM runtime's recent behaviour with a
//            structured deterministic explanation (signals + evidence +
//            verdict). No model call; pure inspection of the runtime state
//            + recent fallback decisions.
//   surface: WhyEngine.Explain (entry point), WhyEvidence + WhyVerdict
//            (records). Wired to GET /api/why/<topic> and the matching
//            MCP tool.
//   gate:    Drift_Api_route_count + Drift_OpenApi_snapshot via the
//            registered routes.
//   adr:     None directly; explanatory transparency is part of the
//            "every automated change gets a proof packet" convention.
//   docs:    docs/API.md (/api/why endpoints), docs/CONVENTIONS.md
//            (proof-packet rule).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Local-first "why engine" — answers natural-language causal questions
/// about the PalLLM runtime's recent behaviour with a structured
/// deterministic explanation.
///
/// <para>Pattern motivation: every complex game and app in the 2026+
/// local-first AI mesh architecture should let its operator or player
/// ask causal questions ("why did X happen?") and get a grounded answer,
/// not a vague LLM guess. PalLLM's <see cref="WhyEngine"/> implements
/// this for the runtime itself: questions about chat replies, inference
/// routing, bridge health, self-healing activity, and thermal gating
/// all resolve to a <see cref="WhyAnswer"/> whose causal chain is pinned
/// to observable evidence (runtime health, recent world events, fallback
/// strategy metrics, operator-health score).</para>
///
/// <para>Deterministic-first: the engine NEVER calls out to live
/// inference. Even when every external dependency is down, a caller gets
/// a correct, grounded structural answer. This keeps the "why" surface
/// on the same always-available guarantee as the deterministic fallback
/// director.</para>
///
/// <para>Question intent is inferred from a tiny keyword matcher
/// (<see cref="WhyQuestionIntent"/>) so operators and AI agents can ask
/// in plain English without learning a taxonomy. Unclassified questions
/// fall through to a generic runtime-posture explanation so the engine
/// still produces a useful answer for anything the matcher doesn't
/// recognise.</para>
/// </summary>
public static class WhyEngine
{
    public static WhyAnswer Answer(
        string? question,
        RuntimeHealth health,
        PalLlmMetricsSnapshot metrics,
        OperatorHealthScore score,
        GameWorldSnapshot? worldSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(score);

        string text = (question ?? string.Empty).Trim();
        WhyQuestionIntent intent = Classify(text);

        return intent switch
        {
            WhyQuestionIntent.FallbackTriggered => ExplainFallback(text, health, metrics),
            WhyQuestionIntent.InferenceBypassed => ExplainInferenceBypass(text, health, metrics),
            WhyQuestionIntent.CircuitBreaker => ExplainCircuitBreaker(text, health),
            WhyQuestionIntent.BridgeNotReady => ExplainBridgeState(text, health),
            WhyQuestionIntent.LowHealthScore => ExplainHealthScore(text, score),
            WhyQuestionIntent.RateLimited => ExplainRateLimit(text, health),
            WhyQuestionIntent.ThermalGate => ExplainThermalGate(text, metrics),
            _ => ExplainGenericPosture(text, health, score, worldSnapshot),
        };
    }

    internal static WhyQuestionIntent Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return WhyQuestionIntent.Unknown; }
        string q = text.ToLowerInvariant();

        // Ordered so more-specific matches win over generic keywords.
        if (Contains(q, "circuit", "breaker", "half-open")) { return WhyQuestionIntent.CircuitBreaker; }
        if (Contains(q, "thermal", "gpu hot", "throttle", "throttling")) { return WhyQuestionIntent.ThermalGate; }
        if (Contains(q, "rate limit", "rate-limit", "rate limited", "429", "slow down")) { return WhyQuestionIntent.RateLimited; }
        if (Contains(q, "bridge", "ue4ss", "adapter not", "game not connected")) { return WhyQuestionIntent.BridgeNotReady; }
        if (Contains(q, "fallback", "deterministic director", "canned", "strategy")) { return WhyQuestionIntent.FallbackTriggered; }
        if (Contains(q, "bypassed", "bypass", "skipped inference", "skip inference", "not using inference")) { return WhyQuestionIntent.InferenceBypassed; }
        if (Contains(q, "health score", "health", "degraded", "critical", "unhealthy", "score")) { return WhyQuestionIntent.LowHealthScore; }

        return WhyQuestionIntent.Unknown;
    }

    private static bool Contains(string text, params string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.Ordinal)) { return true; }
        }
        return false;
    }

    // --------- Intent-specific explainers --------------------------

    private static WhyAnswer ExplainFallback(string text, RuntimeHealth health, PalLlmMetricsSnapshot metrics)
    {
        List<string> chain = new();
        List<string> evidence = new();

        // Identify top strategies by count for the last-chosen-one hint.
        string topStrategy = metrics.FallbackStrategyCounts
            .OrderByDescending(s => s.Count)
            .Select(s => s.StrategyId)
            .FirstOrDefault() ?? "general-director";

        if (!health.InferenceConfigured)
        {
            chain.Add("Live inference is off in the current config, so every turn deterministically routes through the fallback director.");
            chain.Add($"The fallback engine recently ran '{topStrategy}' most often — look at /metrics → palllm_fallback_strategy_total for the full breakdown.");
            chain.Add("The reply is still guaranteed to arrive because the deterministic director is PalLLM's always-available safety net.");
            evidence.Add("RuntimeHealth.InferenceConfigured=false");
        }
        else if (string.Equals(health.InferenceCircuitState, "Open", StringComparison.OrdinalIgnoreCase))
        {
            chain.Add("Live inference is configured, but the circuit breaker is OPEN after repeated failures, so turns short-circuit to the fallback director until the cooldown elapses.");
            chain.Add($"The director most recently leaned on '{topStrategy}'. Investigate the underlying endpoint health and see /api/inference/performance for the failing-lane detail.");
            evidence.Add("RuntimeHealth.InferenceCircuitState=Open");
        }
        else
        {
            chain.Add("The fallback director fires whenever live inference bypass fires (rate limit, deterministic fast-path, thermal gate, or a live-inference failure).");
            chain.Add($"The director recently picked '{topStrategy}' most often; that's a signal of which context is triggering bypass. Inspect palllm_inference_bypass_total and palllm_fallback_strategy_total in /metrics.");
        }

        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.FallbackTriggered.ToString(),
            PrimaryReason: "The deterministic fallback director replies whenever live inference is unavailable or deliberately bypassed. It is PalLLM's always-available safety net.",
            CausalChain: chain,
            EvidenceReferences: evidence,
            Confidence: "high");
    }

    private static WhyAnswer ExplainInferenceBypass(string text, RuntimeHealth health, PalLlmMetricsSnapshot metrics)
    {
        List<string> chain = new();
        List<string> evidence = new();

        chain.Add("PalLLM bypasses live inference for four documented reasons: live inference disabled, per-character rate limit tripped, deterministic fast-path policy matched, or thermal-gate rejected.");
        chain.Add($"Current posture: inference is {(health.InferenceConfigured ? "configured and enabled" : "OFF")}, circuit is '{health.InferenceCircuitState ?? "unknown"}'.");
        if (health.RateLimitedCount > 0)
        {
            chain.Add($"The rate limiter has engaged {health.RateLimitedCount} time(s) this session — a runaway caller is the most likely bypass cause right now.");
            evidence.Add($"RuntimeHealth.RateLimitedCount={health.RateLimitedCount}");
        }
        chain.Add("Look at palllm_inference_bypass_total and palllm_fallback_strategy_total in /metrics to see the distribution.");

        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.InferenceBypassed.ToString(),
            PrimaryReason: "Inference is bypassed for safety (rate limit, thermal), determinism (fast-path policy), or availability (inference off / circuit open). The deterministic director keeps the chat turn alive.",
            CausalChain: chain,
            EvidenceReferences: evidence,
            Confidence: "high");
    }

    private static WhyAnswer ExplainCircuitBreaker(string text, RuntimeHealth health)
    {
        string state = health.InferenceCircuitState ?? "unknown";
        string primary = state.ToLowerInvariant() switch
        {
            "open" => "The inference circuit breaker has tripped OPEN after repeated failures. Chat is running on the fallback director until the cooldown elapses.",
            "halfopen" => "The circuit breaker is HALF-OPEN: a single trial inference call is allowed through to see whether the endpoint has recovered.",
            _ => "The circuit breaker is CLOSED and letting inference calls through normally.",
        };

        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.CircuitBreaker.ToString(),
            PrimaryReason: primary,
            CausalChain:
            [
                "The circuit breaker trips OPEN after PalLLM:Inference:CircuitBreakerFailureThreshold consecutive failures.",
                "After PalLLM:Inference:CircuitBreakerCooldownSeconds, the breaker flips HALF-OPEN and allows one trial call.",
                "A successful trial closes the breaker; a failed trial re-opens it for another cooldown window.",
                "Chat is always answered by the deterministic director whenever the breaker is non-CLOSED, so the player never loses a turn.",
            ],
            EvidenceReferences: [$"RuntimeHealth.InferenceCircuitState={state}"],
            Confidence: "high");
    }

    private static WhyAnswer ExplainBridgeState(string text, RuntimeHealth health)
    {
        List<string> chain = new();
        List<string> evidence = new();

        if (!health.BridgeEnabled)
        {
            chain.Add("The bridge is disabled in config, so no UE4SS Lua events are being drained even if the game is running.");
            evidence.Add("RuntimeHealth.BridgeEnabled=false");
        }
        else if (!health.AdapterReady)
        {
            chain.Add("The bridge is enabled but the game adapter reports not ready: the game isn't running, or UE4SS hasn't loaded the PalLLM Lua mod yet.");
            chain.Add("The sidecar auto-connects as soon as the bridge writes its first event to Runtime/Bridge/Inbox.");
            chain.Add("Run scripts/doctor.ps1 -RunSmoke to verify hook resolution.");
            evidence.Add("RuntimeHealth.AdapterReady=false");
        }
        else
        {
            chain.Add("The bridge is ready and the adapter is delivering events normally.");
            evidence.Add("RuntimeHealth.AdapterReady=true");
        }

        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.BridgeNotReady.ToString(),
            PrimaryReason: health.AdapterReady
                ? "The bridge is healthy; chat and world events are flowing normally."
                : "The bridge is not delivering events: either it is disabled in config, or the game/UE4SS aren't live yet.",
            CausalChain: chain,
            EvidenceReferences: evidence,
            Confidence: "high");
    }

    private static WhyAnswer ExplainHealthScore(string text, OperatorHealthScore score)
    {
        List<string> chain = new();
        chain.Add($"Current operator-health score is {score.Score}/100 ({score.Grade}). {score.Summary}");
        if (score.TopReasons.Count > 0)
        {
            chain.Add("The top subtraction reasons right now:");
            for (int i = 0; i < score.TopReasons.Count; i++)
            {
                chain.Add($"  {i + 1}. {score.TopReasons[i]}");
            }
        }
        else
        {
            chain.Add("No subtraction reasons — every monitored signal is green.");
        }
        chain.Add("Call /api/quickstart for next-step guidance derived from the same signals.");

        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.LowHealthScore.ToString(),
            PrimaryReason: $"Operator-health is {score.Score}/100 ({score.Grade}). {score.Summary}",
            CausalChain: chain,
            EvidenceReferences: [$"OperatorHealthScore={score.Score}", $"Grade={score.Grade}"],
            Confidence: "high");
    }

    private static WhyAnswer ExplainRateLimit(string text, RuntimeHealth health)
    {
        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.RateLimited.ToString(),
            PrimaryReason: health.RateLimitedCount > 0
                ? $"A caller has been rate-limited {health.RateLimitedCount} time(s). PalLLM:Fallback:MaxCharacterRequestsPerMinute protects live inference from runaway producers by routing excess traffic to the deterministic fallback."
                : "The per-character rate limiter is armed but no caller has breached it yet. Live inference is not being throttled.",
            CausalChain:
            [
                "When a character breaches PalLLM:Fallback:MaxCharacterRequestsPerMinute within a one-minute sliding window, subsequent turns skip inference.",
                "The skipped turn still gets a deterministic reply — the player never sees a blank response.",
                "Set PalLLM:Fallback:MaxCharacterRequestsPerMinute to 0 to disable rate limiting entirely.",
            ],
            EvidenceReferences: [$"RuntimeHealth.RateLimitedCount={health.RateLimitedCount}"],
            Confidence: "high");
    }

    private static WhyAnswer ExplainThermalGate(string text, PalLlmMetricsSnapshot metrics)
    {
        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.ThermalGate.ToString(),
            PrimaryReason: "The opt-in thermal gate short-circuits live inference to the deterministic director when the primary GPU is at or above PalLLM:Inference:ThermalGate:RejectAboveC. This keeps chat latency predictable under thermal pressure instead of absorbing the throttled round-trip time.",
            CausalChain:
            [
                "Gate is off by default. When on, a sampler reads nvidia-smi on a short cache TTL.",
                "If temperature is >= RejectAboveC, the inference client returns InferenceResult.Failed with ErrorType=thermal_gated and the runtime routes to fallback.",
                "If temperature is between WarnAboveC and RejectAboveC, inference proceeds but the gate reports 'Warn' for dashboard visibility.",
                "If no sensor is reachable, the gate is permissive — it is always safe to leave enabled.",
            ],
            EvidenceReferences: ["PalLLM:Inference:ThermalGate:*"],
            Confidence: "medium");
    }

    private static WhyAnswer ExplainGenericPosture(
        string text,
        RuntimeHealth health,
        OperatorHealthScore score,
        GameWorldSnapshot? worldSnapshot)
    {
        List<string> chain = new();
        List<string> evidence = new();

        chain.Add($"Operator-health is {score.Score}/100 ({score.Grade}): {score.Summary}");
        chain.Add(health.InferenceConfigured
            ? $"Live inference is ON; active model '{health.InferenceActiveModel}', circuit '{health.InferenceCircuitState}'."
            : "Live inference is OFF; every turn is replying via the deterministic director.");
        chain.Add(health.AdapterReady
            ? "Game adapter is ready and the bridge is delivering events."
            : "Game adapter is not ready — the bridge isn't delivering events yet.");
        if (worldSnapshot is { } w && w.RecentEvents.Count > 0)
        {
            chain.Add($"Most recent world events (up to 5): {string.Join("; ", w.RecentEvents.Take(5))}");
            evidence.Add($"RecentEvents.Count={w.RecentEvents.Count}");
        }
        chain.Add("If you wanted a specific 'why' — try keywords like 'fallback', 'bypass', 'circuit', 'bridge', 'health', 'rate limit', or 'thermal' — or call /api/quickstart for concrete next-step guidance.");

        return new WhyAnswer(
            Question: text,
            Intent: WhyQuestionIntent.Unknown.ToString(),
            PrimaryReason: "I didn't match the question to a specific cause. Here's a grounded posture summary you can anchor the next question against.",
            CausalChain: chain,
            EvidenceReferences: evidence,
            Confidence: "medium");
    }
}

public enum WhyQuestionIntent
{
    Unknown,
    FallbackTriggered,
    InferenceBypassed,
    CircuitBreaker,
    BridgeNotReady,
    LowHealthScore,
    RateLimited,
    ThermalGate,
}

public sealed class WhyRequest
{
    public string? Question { get; init; }
}

public sealed record WhyAnswer(
    string Question,
    string Intent,
    string PrimaryReason,
    IReadOnlyList<string> CausalChain,
    IReadOnlyList<string> EvidenceReferences,
    string Confidence);
