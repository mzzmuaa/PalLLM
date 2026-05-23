using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 35 / D10 — deterministic advisor that surfaces the current
/// resource-budget posture per feature: what's the configured cap,
/// what's the recent-window value, and is the feature comfortably
/// inside or flirting with the ceiling. Pairs with the existing
/// Prometheus lane-readiness gauges — those report raw numbers, this
/// advisor renders them as a budget-vs-consumption view operators
/// can reason about.
///
/// <para>Each entry has:
/// <list type="bullet">
///   <item>Budget id + category (inference / vision / tts / memory / bridge / runtime).</item>
///   <item>Configured ceiling (e.g. "requests/min", "keep-alive seconds", "max-files").</item>
///   <item>Source config key so operators can find + tune the knob.</item>
///   <item>Status bucket: "ok" / "review" / "exhausted" / "unknown".</item>
///   <item>Plain-English recommendation.</item>
/// </list>
/// </para>
///
/// <para>Pure function — no side effects, no inference call, safe on
/// hot paths. Calls out to <see cref="PalLlmMetrics"/> only to read
/// counters; never mutates them.</para>
/// </summary>
public static class ResourceBudgetPostureBuilder
{
    // Cache slot — third application of the TTL-cache pattern documented
    // in docs/DESIGN_PRINCIPLES.md § 8 (after HardwareProfiler and
    // PrivacyPostureBuilder). Budget posture is a 9-entry list composed
    // from a handful of options fields + two metric scalars — cheap, but
    // called on every /api/budgets hit, and the content shifts slowly in
    // practice (options don't change mid-process; metrics drift). Signature
    // captures the relevant inputs so dashboard-driven rapid polls reuse
    // the cached result while a meaningful change (e.g. TTS enabled
    // flipping, fallback-share tier boundary crossed) recomputes.
    private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(15);
    private static volatile ResourceBudgetPostureCacheEntry? _cached;

    /// <summary>
    /// Capture the full budget posture for the current config + runtime state.
    /// Always recomputes — for a TTL-cached variant safe for dashboard-poll
    /// rates, use <see cref="CaptureCached"/>.
    /// </summary>
    public static ResourceBudgetPosture Capture(PalLlmOptions options, ResourceBudgetMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);

        var entries = new List<ResourceBudgetEntry>();

        // ---- Inference budgets ----------------------------------------
        entries.Add(new ResourceBudgetEntry(
            Id: "inference-circuit-breaker",
            Category: "inference",
            Budget: $"{options.Inference.CircuitBreakerFailureThreshold} consecutive failures",
            Current: "n/a (event-driven, not a rate)",
            ConfigKey: "PalLLM:Inference:CircuitBreakerFailureThreshold",
            Status: "ok",
            Notes: "Circuit breaker trips on this many consecutive failures and cools down for the configured cooldown seconds."));

        entries.Add(new ResourceBudgetEntry(
            Id: "inference-rate-per-character",
            Category: "inference",
            Budget: $"{options.Fallback.MaxCharacterRequestsPerMinute} chat turns / minute / character",
            Current: "per-character sliding window",
            ConfigKey: "PalLLM:Fallback:MaxCharacterRequestsPerMinute",
            Status: "ok",
            Notes: "Drop this on a multi-player dedicated server so one player can't monopolise the live lane (see docs/SERVER_OPERATOR.md)."));

        // ---- Vision budgets -------------------------------------------
        if (options.Vision.Enabled)
        {
            entries.Add(new ResourceBudgetEntry(
                Id: "vision-screenshot-max-files",
                Category: "vision",
                Budget: $"{options.Vision.PendingScreenshotMaxFiles} queued screenshots",
                Current: "watched by ScreenshotWatcher",
                ConfigKey: "PalLLM:Vision:PendingScreenshotMaxFiles",
                Status: "ok",
                Notes: "Beyond this cap, the oldest screenshots are discarded so the queue never blocks the game thread."));
        }
        else
        {
            entries.Add(new ResourceBudgetEntry(
                Id: "vision-disabled",
                Category: "vision",
                Budget: "vision off",
                Current: "vision off",
                ConfigKey: "PalLLM:Vision:Enabled",
                Status: "ok",
                Notes: "Vision is off — zero budget consumed."));
        }

        // ---- TTS budgets ----------------------------------------------
        if (options.Tts.Enabled)
        {
            entries.Add(new ResourceBudgetEntry(
                Id: "tts-max-characters",
                Category: "tts",
                Budget: $"{options.Tts.MaxCharacters} characters / synthesis call",
                Current: "per-call guard",
                ConfigKey: "PalLLM:Tts:MaxCharacters",
                Status: "ok",
                Notes: "Text longer than this is truncated before synthesis to keep local TTS engines responsive."));
        }
        else
        {
            entries.Add(new ResourceBudgetEntry(
                Id: "tts-disabled",
                Category: "tts",
                Budget: "tts off",
                Current: "tts off",
                ConfigKey: "PalLLM:Tts:Enabled",
                Status: "ok",
                Notes: "TTS is off — zero budget consumed."));
        }

        // ---- ASR budgets ----------------------------------------------
        if (options.Asr.Enabled)
        {
            entries.Add(new ResourceBudgetEntry(
                Id: "asr-max-audio-bytes",
                Category: "asr",
                Budget: $"{options.Asr.MaxAudioBytes} decoded audio bytes / transcription call",
                Current: "per-call guard",
                ConfigKey: "PalLLM:Asr:MaxAudioBytes",
                Status: "ok",
                Notes: "Audio larger than this is rejected locally before PalLLM sends it to the ASR endpoint."));
        }
        else
        {
            entries.Add(new ResourceBudgetEntry(
                Id: "asr-disabled",
                Category: "asr",
                Budget: "asr off",
                Current: "asr off",
                ConfigKey: "PalLLM:Asr:Enabled",
                Status: "ok",
                Notes: "ASR is off - zero audio transcription budget consumed."));
        }

        // ---- Memory budgets -------------------------------------------
        entries.Add(new ResourceBudgetEntry(
            Id: "memory-recent-window",
            Category: "memory",
            Budget: $"{options.Fallback.RecentMemoryWindow} recent conversation entries",
            Current: "per-character rolling window",
            ConfigKey: "PalLLM:Fallback:RecentMemoryWindow",
            Status: "ok",
            Notes: "Size of the short-term window the fallback director considers when composing replies."));

        // ---- Bridge budgets -------------------------------------------
        entries.Add(new ResourceBudgetEntry(
            Id: "bridge-outbox-retention",
            Category: "bridge",
            Budget: $"{options.Bridge.OutboxMaxFiles} files retained",
            Current: "rolling",
            ConfigKey: "PalLLM:Bridge:OutboxMaxFiles",
            Status: options.Bridge.OutboxMaxFiles > 0 ? "ok" : "review",
            Notes: options.Bridge.OutboxMaxFiles > 0
                ? "Outbox writer keeps at most this many envelopes before pruning oldest."
                : "OutboxMaxFiles=0 disables retention — Lua must consume reliably or disk grows."));

        // ---- Runtime / process budgets --------------------------------
        entries.Add(new ResourceBudgetEntry(
            Id: "runtime-chat-total",
            Category: "runtime",
            Budget: "unbounded",
            Current: $"{metrics.ChatTotal} total chat turns since start",
            ConfigKey: null,
            Status: "ok",
            Notes: "Lifetime chat-turn counter. Useful to spot anomaly bursts in the dashboard."));

        entries.Add(new ResourceBudgetEntry(
            Id: "runtime-fallback-share",
            Category: "runtime",
            Budget: "ideally < 50% on live-configured installs",
            Current: metrics.ChatTotal == 0
                ? "no chats recorded yet"
                : $"{((double)metrics.ChatFallbackTotal / metrics.ChatTotal):P0} fallback share",
            ConfigKey: null,
            Status: metrics.ChatTotal == 0
                ? "unknown"
                : ((double)metrics.ChatFallbackTotal / metrics.ChatTotal) > 0.75
                    ? "review"
                    : "ok",
            Notes: "High fallback share on an install that has live inference configured usually means the live lane is struggling — check the circuit breaker + thermal gate + rate limiter."));

        int okCount = entries.Count(e => e.Status == "ok");
        int reviewCount = entries.Count(e => e.Status == "review");
        int exhaustedCount = entries.Count(e => e.Status == "exhausted");

        string headline = exhaustedCount > 0
            ? $"{exhaustedCount} budget(s) exhausted — review recommended."
            : reviewCount > 0
                ? $"{reviewCount} budget(s) want review; {okCount} in the clear."
                : $"All {okCount} budgets comfortably inside their ceiling.";

        return new ResourceBudgetPosture(
            CapturedAtUtc: DateTimeOffset.UtcNow,
            Headline: headline,
            OkCount: okCount,
            ReviewCount: reviewCount,
            ExhaustedCount: exhaustedCount,
            Budgets: entries);
    }

    /// <summary>
    /// Memoised variant of <see cref="Capture"/> — recomputes at most
    /// once every <paramref name="cacheTtl"/> (default 15 seconds), or
    /// on signature change (relevant options flip, or metric-derived
    /// status bucket boundary crossed), whichever comes first. Safe to
    /// call on the <c>/api/budgets</c> hot path at dashboard-poll rates.
    /// Third application of the pattern documented in
    /// <c>docs/DESIGN_PRINCIPLES.md</c> § 8.
    /// </summary>
    public static ResourceBudgetPosture CaptureCached(
        PalLlmOptions options,
        ResourceBudgetMetrics metrics,
        TimeSpan? cacheTtl = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metrics);
        TimeSpan ttl = cacheTtl ?? DefaultCacheTtl;
        string signature = ComputeSignature(options, metrics);
        ResourceBudgetPostureCacheEntry? snapshot = _cached;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        if (snapshot is not null
            && snapshot.Signature == signature
            && now - snapshot.CapturedAt < ttl)
        {
            return snapshot.Posture;
        }

        ResourceBudgetPosture fresh = Capture(options, metrics);
        _cached = new ResourceBudgetPostureCacheEntry(fresh, signature, now);
        return fresh;
    }

    /// <summary>
    /// Invalidate the cache. Next <see cref="CaptureCached"/> call
    /// recomputes from scratch. Exposed for tests and explicit refresh.
    /// </summary>
    public static void InvalidateCache() => _cached = null;

    private static string ComputeSignature(PalLlmOptions options, ResourceBudgetMetrics metrics)
    {
        // Compact signature of every field Capture() reads. Numeric
        // budgets are included verbatim; metric-derived status booleans
        // are included so a crossing of the fallback-share tier boundary
        // (75%) invalidates the cache even within TTL.
        bool highFallback = metrics.ChatTotal > 0
            && ((double)metrics.ChatFallbackTotal / metrics.ChatTotal) > 0.75;
        return string.Concat(
            options.Vision.Enabled ? "V+" : "V-", ";",
            options.Vision.PendingScreenshotMaxFiles, ";",
            options.Tts.Enabled ? "T+" : "T-", ";",
            options.Tts.MaxCharacters, ";",
            options.Asr.Enabled ? "A+" : "A-", ";",
            options.Asr.MaxAudioBytes, ";",
            options.Fallback.RecentMemoryWindow, ";",
            options.Fallback.MaxCharacterRequestsPerMinute, ";",
            options.Inference.CircuitBreakerFailureThreshold, ";",
            options.Bridge.OutboxMaxFiles, ";",
            "CT=", metrics.ChatTotal, ";",
            "FT=", metrics.ChatFallbackTotal, ";",
            "HF=", highFallback ? "1" : "0");
    }

    private sealed record ResourceBudgetPostureCacheEntry(
        ResourceBudgetPosture Posture,
        string Signature,
        DateTimeOffset CapturedAt);
}

/// <summary>
/// Read-only snapshot the posture builder needs from
/// <see cref="PalLlmMetrics"/>. Minimal by design so tests can
/// construct one without spinning up the full runtime.
/// </summary>
public sealed record ResourceBudgetMetrics(
    long ChatTotal,
    long ChatFallbackTotal)
{
    /// <summary>
    /// Derive a <see cref="ResourceBudgetMetrics"/> from the full
    /// <see cref="PalLlmMetricsSnapshot"/>: ChatTotal comes from the
    /// chat-latency histogram count; ChatFallbackTotal is the sum of
    /// every fallback strategy count.
    /// </summary>
    public static ResourceBudgetMetrics FromSnapshot(PalLlmMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        long total = snapshot.ChatLatency.Count;
        long fallback = 0;
        foreach (FallbackStrategyCount c in snapshot.FallbackStrategyCounts)
        {
            fallback += c.Count;
        }
        return new ResourceBudgetMetrics(total, fallback);
    }
}

/// <summary>
/// Complete budget posture — one entry per tracked resource.
/// </summary>
public sealed record ResourceBudgetPosture(
    DateTimeOffset CapturedAtUtc,
    string Headline,
    int OkCount,
    int ReviewCount,
    int ExhaustedCount,
    IReadOnlyList<ResourceBudgetEntry> Budgets);

/// <summary>
/// Single budget row inside <see cref="ResourceBudgetPosture"/>.
/// </summary>
public sealed record ResourceBudgetEntry(
    string Id,
    string Category,
    string Budget,
    string Current,
    string? ConfigKey,
    string Status,
    string Notes);
