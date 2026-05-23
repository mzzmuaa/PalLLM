using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

// AGENT-CARD ---------------------------------------------------------------
// what:    Computes the operator-actionable Suggestions[] list for
//          RuntimeHealth. Each suggestion has a stable Code, a human
//          Message, an optional copy-paste Command, and a Severity bucket
//          (info / warn / urgent).
// inputs:  Numbers from the assembled RuntimeHealth snapshot (pack count,
//          circuit states, lane success/failure counters, queue depths).
//          Pure function; no I/O, no allocations beyond the result list.
// outputs: IReadOnlyList<HealthSuggestion>. Empty when everything is healthy.
//          Stable order (same inputs → same output) so dashboards don't
//          flap between polls.
// reads:   Only its arguments. Does NOT touch global state.
// writes:  Nothing.
// who calls: PalLlmRuntime.BuildRuntimeHealth on every /api/health request.
//          The list also flows to the Field Console dashboard, the
//          `pal next` advisor (consumed via /api/health), `pal doctor`,
//          and the `pal_health_suggestions` MCP tool.
// related: OperatorHealthScorer (numeric score + reasons; complementary
//          but text-only, no copy-paste commands).
// --------------------------------------------------------------------------

/// <summary>
/// Builds the operator-actionable next-step hints surfaced in
/// <see cref="RuntimeHealth.Suggestions"/>. The list is empty when nothing
/// in the snapshot indicates degraded state; otherwise every entry carries
/// the exact next command an operator should run.
///
/// <para>Suggestions are ordered most-impactful first so a UI that only
/// has room for one hint can take <c>[0]</c> and be useful. Codes are
/// stable kebab-case identifiers so dashboards and pal verbs can match
/// on them without parsing prose. Each entry also declares its own
/// severity bucket so consumers don't need to maintain a code-to-severity
/// map -- this builder is the single source of truth. Idempotent and
/// pure: same inputs ⇒ same outputs, no I/O, no global state.</para>
/// </summary>
public static class HealthSuggestionBuilder
{
    /// <summary>Severity buckets carried on every <see cref="HealthSuggestion"/>.
    /// Stable strings so consumers (dashboard CSS, pal-next colour switch,
    /// MCP-aware agents) can switch on them without enums leaking through
    /// the wire.</summary>
    public static class Severity
    {
        /// <summary>Informational signal. Common operator state, not a failure.</summary>
        public const string Info = "info";

        /// <summary>Something is mildly off; the runtime is still working but
        /// an operator should look at it within a session.</summary>
        public const string Warn = "warn";

        /// <summary>Active failure surface; chat is degraded right now and
        /// the operator should fix it immediately.</summary>
        public const string Urgent = "urgent";
    }

    /// <summary>Build the suggestion list. Pass values straight from the
    /// assembled <see cref="RuntimeHealth"/> snapshot; the builder never
    /// mutates them.</summary>
    public static IReadOnlyList<HealthSuggestion> Build(in HealthSuggestionInputs inputs)
    {
        List<HealthSuggestion> suggestions = new(capacity: 6);

        // 1. No personality packs loaded — most painful for new users
        // because the companion replies with the default voice and the
        // operator has no idea why. The fix is a single pal verb.
        if (inputs.LoadedPackCount == 0)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "no-packs-loaded",
                Message: "No personality packs loaded. The companion replies with the default voice. Drop one of the four shipped samples into the runtime pack dir and the sidecar picks it up on the next reload.",
                Command: "pal pack copy companion-warrior",
                Severity: Severity.Info));
        }

        // 2. Inference circuit breaker is OPEN — chat is on fallback while
        // the configured backend cools down. This is the most common
        // operator pain after install: config says "inference enabled",
        // but the configured llama.cpp / vLLM / LM Studio server is not
        // actually responding. We flag this only when inference is
        // configured (otherwise an OPEN state is expected on a
        // fallback-only deployment).
        string circuit = inputs.InferenceCircuitState ?? string.Empty;
        if (inputs.InferenceConfigured &&
            string.Equals(circuit, "Open", StringComparison.OrdinalIgnoreCase))
        {
            suggestions.Add(new HealthSuggestion(
                Code: "inference-circuit-open",
                Message: "Inference circuit breaker is OPEN. The configured backend is not responding; chat is running on the deterministic fallback while it cools down. Boot the backend (or pick a different lane with 'pal connect') and the breaker re-closes on the next successful trial call.",
                Command: "pal doctor",
                Severity: Severity.Urgent));
        }

        // 3. Inference configured + every recorded request has failed.
        // Sister condition to "circuit open" -- catches the case where
        // the breaker hasn't tripped yet (fewer than the threshold) but
        // every attempt is still failing. Strong signal that the
        // operator's BaseUrl / Model / API key is misconfigured.
        if (inputs.InferenceConfigured &&
            inputs.InferenceFailureCount > 0 &&
            inputs.InferenceSuccessCount == 0)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "inference-only-failures",
                Message: $"Inference is wired but every recorded request has failed ({inputs.InferenceFailureCount} failure(s), 0 successes). Likely a wrong BaseUrl, Model id, or backend not running.",
                Command: "pal doctor",
                Severity: Severity.Urgent));
        }

        // 4. Vision configured + every recorded request has failed. Same
        // shape as inference-only-failures but for the vision lane. Catches
        // the painful "vision was working last week but the gemma image
        // model got pulled" case where the operator wonders why the
        // companion stopped describing screenshots.
        if (inputs.VisionEnabled &&
            inputs.VisionFailureCount > 0 &&
            inputs.VisionCallCount == inputs.VisionFailureCount)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "vision-only-failures",
                Message: $"Vision lane is wired but every recorded request has failed ({inputs.VisionFailureCount} failure(s), 0 successes). The chat path keeps working without it; the companion just can't describe screenshots until the lane recovers.",
                Command: "pal doctor",
                Severity: Severity.Warn));
        }

        // 5. TTS configured + every recorded request has failed. Same shape
        // as vision-only-failures. The chat reply text still arrives;
        // synthesised speech doesn't.
        if (inputs.TtsEnabled &&
            inputs.TtsFailureCount > 0 &&
            inputs.TtsCallCount == inputs.TtsFailureCount)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "tts-only-failures",
                Message: $"TTS lane is wired but every recorded request has failed ({inputs.TtsFailureCount} failure(s), 0 successes). Chat replies still render as text; spoken audio is dark until the synth server recovers.",
                Command: "pal doctor",
                Severity: Severity.Warn));
        }

        // 6. Bridge enabled but no events yet. Either Palworld + UE4SS is
        // not actually running, or the mod was not installed cleanly.
        // Skipped if the bridge has booted at least once -- a bridge that
        // has booted but is currently idle is fine.
        if (inputs.BridgeEnabled &&
            inputs.BridgeBootCount == 0 &&
            inputs.BridgeEventCount == 0)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "bridge-idle",
                Message: "Bridge is enabled but no events have arrived yet. Either Palworld + UE4SS isn't running, or the mod isn't installed. The sidecar still serves /api routes; only the in-game live loop is dark.",
                Command: "pal doctor",
                Severity: Severity.Info));
        }

        // 7. Outbox backlog. The sidecar writes replies into Bridge/Outbox/
        // for the Lua bridge to read and render. A growing backlog means
        // the bridge isn't reading them -- usually because Palworld
        // crashed, the mod isn't running, or the consumer side has stalled.
        // Threshold is conservative: the outbox normally drains within
        // seconds.
        if (inputs.OutboxPendingCount > 50)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "outbox-backlog",
                Message: $"Bridge outbox has {inputs.OutboxPendingCount} pending replies. The Lua bridge isn't draining them -- usually Palworld is closed, the mod is unloaded, or the consumer stalled. New replies will continue queueing; nothing is lost.",
                Command: null,
                Severity: Severity.Warn));
        }

        // 8. Excessive failed-file backlog. The bridge moves events that
        // can't be parsed into Runtime/Bridge/Failed/. A small number is
        // normal during testing; a large number means something is
        // consistently producing malformed envelopes.
        if (inputs.FailedFileCount > 25)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "bridge-failed-files-accumulating",
                Message: $"{inputs.FailedFileCount} failed bridge files have accumulated. Inspect Runtime/Bridge/Failed/ for the recurring envelope shape; usually a Lua-side schema mismatch.",
                Command: null,
                Severity: Severity.Warn));
        }

        // 9. Inbox pending backlog. A short-lived spike on bridge boot is
        // normal; a sustained backlog means the worker isn't draining at
        // the production rate.
        if (inputs.InboxPendingCount > 200)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "bridge-inbox-backlog",
                Message: $"Bridge inbox has {inputs.InboxPendingCount} pending events. The drain worker is falling behind production rate; consider raising 'PalLLM:Bridge:MaxEventsPerPoll' or shortening 'PollIntervalMs'.",
                Command: null,
                Severity: Severity.Warn));
        }

        // 10. Automation enabled but allowlist is empty. A real footgun:
        // the operator flips Automation.Enabled=true expecting the
        // companion to start acting in-game, but forgets to add action ids
        // to AllowedActions. The runtime correctly emits zero action
        // intents (allowlist enforced) and the operator wonders why
        // nothing happens. Surface the half-done config explicitly.
        if (inputs.AutomationEnabled &&
            inputs.AutomationAllowedActionCount == 0)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "automation-allowlist-empty-but-enabled",
                Message: "Automation is enabled but the AllowedActions list is empty. The companion will not emit any in-game action intents until at least one action id is added. See docs/OPERATIONS.md 'Enabling the action executor' for the supported ids.",
                Command: null,
                Severity: Severity.Warn));
        }

        // 11. Screenshots queueing with no consumer. The screenshot watcher
        // wrote files into Runtime/Screenshots/Pending/ but the vision lane
        // is disabled, so nothing will read them. Either enable vision or
        // turn the watcher off; otherwise the queue grows until the
        // PendingScreenshotMaxFiles cap kicks in and starts dropping.
        if (inputs.ScreenshotPendingCount > 0 &&
            !inputs.VisionEnabled)
        {
            suggestions.Add(new HealthSuggestion(
                Code: "screenshots-pending-but-vision-disabled",
                Message: $"{inputs.ScreenshotPendingCount} screenshot(s) queued under Runtime/Screenshots/Pending/ but vision is disabled. Either flip 'PalLLM:Vision:Enabled=true' to consume them or 'PalLLM:Vision:EnableScreenshotWatcher=false' to stop queueing.",
                Command: null,
                Severity: Severity.Info));
        }

        // Final ordering: severity-first (urgent > warn > info), stable
        // within each bucket so the detection order above acts as the
        // tiebreaker. Every consumer now sees the most pressing entry at
        // index 0 -- a UI that only has room for one card surfaces the
        // right thing without picking a random hint.
        return SortBySeverity(suggestions);
    }

    private static IReadOnlyList<HealthSuggestion> SortBySeverity(List<HealthSuggestion> suggestions)
    {
        if (suggestions.Count <= 1)
        {
            return suggestions;
        }

        // Stable sort by severity rank. List<T>.Sort isn't stable; LINQ's
        // OrderBy is. We materialize back to a List<T> so the cast at the
        // call site (IReadOnlyList<HealthSuggestion>) doesn't allocate
        // again.
        return suggestions.OrderBy(s => SeverityRank(s.Severity)).ToList();
    }

    private static int SeverityRank(string severity) => severity switch
    {
        Severity.Urgent => 0,
        Severity.Warn => 1,
        Severity.Info => 2,
        // Unknown severity sorts last so future builder additions that
        // forget to set the field don't shadow the codes that did.
        _ => 3,
    };
}

/// <summary>
/// Inputs to <see cref="HealthSuggestionBuilder.Build"/>. A small record
/// struct so callers don't have to pass the full <see cref="RuntimeHealth"/>
/// (which contains many fields the builder doesn't read) and so the test
/// surface is explicit.
/// </summary>
public readonly record struct HealthSuggestionInputs(
    int LoadedPackCount,
    bool InferenceConfigured,
    string InferenceCircuitState,
    long InferenceSuccessCount,
    long InferenceFailureCount,
    bool VisionEnabled,
    long VisionCallCount,
    long VisionFailureCount,
    bool TtsEnabled,
    long TtsCallCount,
    long TtsFailureCount,
    bool BridgeEnabled,
    long BridgeBootCount,
    long BridgeEventCount,
    int InboxPendingCount,
    int OutboxPendingCount,
    int FailedFileCount,
    int ScreenshotPendingCount,
    bool AutomationEnabled,
    int AutomationAllowedActionCount);
