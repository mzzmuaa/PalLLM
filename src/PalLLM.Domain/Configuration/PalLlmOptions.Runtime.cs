// PalLlmOptions (partial): fallback / hardware / promotion / self-healing / model-role options.
// Nested option classes bound from the "PalLLM" appsettings section; see PalLlmOptions.cs for the root aggregator + AGENT-CARD.
using System.Text.Json.Serialization;

namespace PalLLM.Domain.Configuration;


public sealed class FallbackOptions
{
    public bool Enabled { get; set; } = true;

    public bool UseWhenInferenceDisabled { get; set; } = true;

    public bool UseWhenInferenceFails { get; set; } = true;

    public bool EnablePolicyBypass { get; set; } = true;

    public bool PreferForReactiveBarks { get; set; } = true;

    public bool PreferForRoutineTacticalTasks { get; set; } = true;

    public bool PreferForRecoveryAndCampTasks { get; set; } = true;

    public int RecentMemoryWindow { get; set; } = 12;

    /// Enables the deterministic memory-reflection pass after each chat. Off by
    /// default so reproducible test fixtures do not accrue surprise entries.
    /// Turn on in production configs to let the runtime consolidate
    /// high-importance moments into retrievable insight memories over a session.
    public bool EnableReflection { get; set; }

    /// Task-focus toggle. When enabled, the system prompt reminds the model to
    /// stay task-focused instead of leaning into performative character shtick.
    /// Off by default to preserve existing roleplay feel.
    public bool PreferTaskFocus { get; set; }

    /// Rate-limit ceiling for chat requests per character per minute. Set to 0 to
    /// disable (default). When a character breaches the limit, subsequent calls
    /// short-circuit to the deterministic fallback — preserves a working reply
    /// without paying inference tokens on a runaway producer.
    public int MaxCharacterRequestsPerMinute { get; set; }
}

/// <summary>
/// Hardware-tier override config (Pass 25 / D1). Optional. When
/// <see cref="ForceTier"/> names a valid <c>DuoHardwareTier</c>
/// enum value, the /api/hardware surface reports that as the
/// effective tier regardless of detection. Empty or unparsable
/// values are ignored.
/// </summary>
public sealed class HardwareOptions
{
    /// <summary>Optional force-tier value: Constrained / Standard / Generous.</summary>
    public string? ForceTier { get; set; }
}

/// <summary>
/// Configuration for the promotion apply verb (Pass 24). When
/// <see cref="AllowApply"/> is true, <c>POST /api/promotion/apply</c>
/// is allowed to persist a durable staging artifact (template +
/// rollback marker + audit packet) under <see cref="StagingRoot"/>
/// for a candidate promotion. Apply never mutates source code in-place;
/// the staging artifact is meant to be cherry-picked by a human
/// reviewer. Rollback is simply deleting the staged files.
/// </summary>
public sealed class PromotionApplyOptions
{
    /// <summary>
    /// Master safety flag. Default off — the promotion pipeline stays
    /// observation-only out of the box. Flip to <c>true</c> only in
    /// environments where a human reviewer is going to cherry-pick
    /// the staged artifacts.
    /// </summary>
    public bool AllowApply { get; set; } = false;

    /// <summary>
    /// Directory where Pass-24 apply writes <c>template-*.md</c>,
    /// <c>rollback-*.txt</c>, and <c>packet-*.json</c> per apply
    /// invocation. Relative paths are resolved against the runtime's
    /// <c>Runtime/</c> root. Defaults to <c>PromotionStaging</c>.
    /// </summary>
    public string StagingRoot { get; set; } = "PromotionStaging";

    /// <summary>
    /// Cap on the number of artifacts retained. When exceeded, the
    /// oldest staged apply is removed from disk. 64 is an order of
    /// magnitude of headroom for the expected per-task candidacy rate.
    /// </summary>
    public int MaxStagedArtifacts { get; set; } = 64;
}

/// <summary>
/// Configuration for the background <c>PromotionLedgerFeeder</c>.
/// Every tick the feeder reads the live <c>PalLlmMetrics</c> snapshot,
/// diffs the fallback-strategy counts against the prior tick, and
/// writes one "success" observation into the ledger per increment.
/// Pure observer — never mutates runtime state beyond the ledger.
/// </summary>
public sealed class PromotionFeederOptions
{
    /// <summary>Master switch. Default on because behaviour is purely additive.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the feeder reads the metrics snapshot. Too low
    /// wastes CPU on diffs; too high means a slow-flowing strategy may
    /// miss observations between ticks. 30s balances both.</summary>
    public int CheckIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// Cap on the number of ledger records emitted per tick per
    /// strategy. Bounds the feeder so a brief burst of fallback fires
    /// cannot flood the ledger with hundreds of identical observations
    /// in one tick.
    /// </summary>
    public int MaxObservationsPerStrategyPerTick { get; set; } = 25;

    /// <summary>
    /// Task class identifier recorded against every fallback-director
    /// observation the feeder emits. Default maps fallback fires to a
    /// dedicated slot so operators can tell auto-fed observations from
    /// manual ones at a glance.
    /// </summary>
    public string FallbackTaskClass { get; set; } = "fallback-director";

    /// <summary>
    /// Task class recorded for live-inference deltas. Each observation's
    /// <c>PatternId</c> is the active model id from <c>RuntimeHealth</c>
    /// so different models populate separate observation streams. Set
    /// <see cref="TrackLiveInference"/> to <c>false</c> to disable.
    /// </summary>
    public string LiveInferenceTaskClass { get; set; } = "live-inference";

    /// <summary>Toggle for live-inference observation recording.</summary>
    public bool TrackLiveInference { get; set; } = true;

    /// <summary>
    /// Task class recorded when the per-character rate limiter engages.
    /// Recorded as <c>OutcomeSuccess</c> because engagement means the
    /// limiter is correctly protecting the live-inference lane from a
    /// runaway caller — the player-visible reply still lands via fallback.
    /// Set <see cref="TrackRateLimiter"/> to <c>false</c> to disable.
    /// </summary>
    public string RateLimiterTaskClass { get; set; } = "rate-limiter";

    /// <summary>Toggle for rate-limiter observation recording.</summary>
    public bool TrackRateLimiter { get; set; } = true;

    /// <summary>
    /// Task class recorded for model-tier graduation transitions
    /// (small → large, large → small). Pattern id is the
    /// <c>from→to</c> tuple from the metric. Set
    /// <see cref="TrackTierTransitions"/> to <c>false</c> to disable.
    /// </summary>
    public string TierTransitionTaskClass { get; set; } = "model-tier-transition";

    /// <summary>Toggle for model-tier-transition observation recording.</summary>
    public bool TrackTierTransitions { get; set; } = true;
}

/// <summary>
/// Conservative background self-healing watchdog. On a cadence, the worker
/// audits the live runtime for stuck state and applies fixes that are safe
/// to perform without operator input:
///
/// <list type="bullet">
///   <item>Archive outbox envelopes older than <see cref="OrphanEnvelopeAgeSeconds"/>
///         to <c>Runtime/SelfHealingEvidence/recovered-&lt;UTC&gt;/</c> so a
///         stuck consumer never starves a future producer.</item>
///   <item>Log the current <c>OperatorHealthScore</c> when it drops below
///         <see cref="UnhealthyScoreFloor"/>, so a long-running sidecar in
///         degraded state surfaces in server logs even if nobody is watching
///         the dashboard.</item>
///   <item>Write <c>Runtime/SelfHealingEvidence/latest-self-healing.json</c>
///         every tick so operators can audit exactly what the watchdog
///         observed and what it did.</item>
/// </list>
///
/// <para>Deliberately does NOT mutate circuit-breaker state or restart the
/// sidecar — those are destructive operations reserved for the human-driven
/// <c>recover.bat</c> path. The watchdog is additive observability + gentle
/// janitorial work only.</para>
/// </summary>
public sealed class SelfHealingOptions
{
    /// <summary>Master switch. Defaults to <c>true</c> because the default
    /// behaviour is non-destructive (archive + log) and the worker is tiny.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the watchdog ticks. Tight enough to catch stuck
    /// state within a reasonable window; loose enough to never contribute
    /// measurable CPU.</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>Outbox envelopes older than this are archived to
    /// <c>Runtime/SelfHealingEvidence/recovered-&lt;UTC&gt;/</c>. Set to 0 to
    /// disable orphan-envelope sweeping.</summary>
    public int OrphanEnvelopeAgeSeconds { get; set; } = 600;

    /// <summary>Operator-health scores at or below this value trigger a
    /// structured log line every tick. Set to 0 to disable the log signal.</summary>
    public int UnhealthyScoreFloor { get; set; } = 40;

    /// <summary>How many history snapshots under
    /// <c>Runtime/SelfHealingEvidence/History/</c> to keep. 0 disables
    /// history; the <c>latest</c> snapshot is always written regardless.</summary>
    public int HistoryRetention { get; set; } = 200;
}

/// <summary>
/// One role binding in <see cref="PalLlmOptions.ModelRoles"/>. Declares
/// that a given model endpoint fills a specific <c>ModelRole</c> slot
/// (Edge / Worker / Judge / Media / Validator) in the local-first AI
/// mesh. Multiple bindings per role are allowed — the first
/// <see cref="Enabled"/> one per role is treated as the active
/// endpoint.
/// </summary>
public sealed class ModelRoleBinding
{
    /// <summary>Which of the five mesh roles this binding fills.</summary>
    public PalLLM.Domain.Inference.ModelRole Role { get; set; }

    /// <summary>Short operator-facing id so log lines and tool output
    /// can reference the binding without a UUID (e.g. <c>"gemma-edge"</c>,
    /// <c>"qwen-fast"</c>, <c>"qwen-dense"</c>).</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The model tag the endpoint expects (e.g.
    /// <c>"gemma3:4b"</c>, <c>"qwen3.6:35b-a3b"</c>). Informational —
    /// the runtime does not re-issue this to the endpoint automatically.</summary>
    public string ModelId { get; set; } = string.Empty;

    /// <summary>Base URL this role binding points at. Used by
    /// <c>/api/airgap/verify</c> and future role-aware routing; not
    /// automatically called today.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Free-form operator note (capacity, quant level,
    /// residency expectation, etc.).</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Per-binding enable switch so operators can pre-declare a
    /// role binding and flip it on when the endpoint is ready without
    /// editing the list structure.</summary>
    public bool Enabled { get; set; } = true;
}
