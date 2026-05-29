using System.Collections.Concurrent;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Sliding-window ledger of per-task-class outcomes used to decide
//            "which AI-assisted patterns have run cleanly long enough to be
//            promoted into deterministic product logic?". Records observations
//            with outcome tags; reports promotion-readiness summaries.
//   surface: PromotionLedger (record/query), PromotionObservation (record),
//            PromotionSummary, the /api/promotion/* HTTP routes.
//   gate:    None directly; promotion-related tests live in
//            tests/PalLLM.Tests/PromotionApply*Tests.cs.
//   adr:     None directly; pattern is documented in
//            docs/STATE_MACHINES.md (promotion ledger state machine).
//   docs:    docs/STATE_MACHINES.md, docs/HARVEST.md (Promotion ledger
//            harvest recipe).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Records per-task-class observations so PalLLM can answer the 2035
/// doc's core question: "which AI-assisted patterns have been running
/// cleanly long enough to be promoted into deterministic product logic?"
///
/// <para>An observation is one concrete occurrence of a pattern (for
/// example "duo-branch-tournament on ImplementDraft" or "fallback-
/// director on rate-limit-triggered") and carries an outcome tag
/// (<c>success</c> / <c>disagreement-block</c> / <c>validator-fail</c>
/// / <c>human-override</c>). The ledger keeps a bounded sliding window
/// per task class so an unbounded feature flag never blows up memory.</para>
///
/// <para>Promotion criterion is deliberately conservative:</para>
/// <list type="bullet">
///   <item>At least <see cref="MinObservationsForPromotion"/> observations.</item>
///   <item>Overall success rate ≥ <see cref="SuccessRateFloor"/>.</item>
///   <item>Zero <c>disagreement-block</c> or <c>human-override</c>
///         outcomes in the most recent
///         <see cref="RecentBlockWindow"/> observations.</item>
/// </list>
///
/// <para>Deterministic, in-memory, thread-safe. No persistence in this
/// pass — a later follow-up can dump to
/// <c>Runtime/PromotionEvidence/latest-summary.json</c> alongside the
/// existing self-healing / release-readiness evidence stores if the
/// feature is exercised enough to justify the on-disk footprint.</para>
/// </summary>
public sealed class PromotionLedger
{
    /// <summary>Window the ledger keeps per task class.</summary>
    public const int PerTaskWindow = 200;

    /// <summary>Minimum observations before a pattern can be promoted.</summary>
    public const int MinObservationsForPromotion = 20;

    /// <summary>Success-rate threshold (0..1) for promotion.</summary>
    public const double SuccessRateFloor = 0.95;

    /// <summary>Most-recent window that must be free of blocks / human overrides.</summary>
    public const int RecentBlockWindow = 10;

    public const string OutcomeSuccess = "success";
    public const string OutcomeDisagreementBlock = "disagreement-block";
    public const string OutcomeValidatorFail = "validator-fail";
    public const string OutcomeHumanOverride = "human-override";

    private static readonly string[] AllowedOutcomeValuesOrdered =
    [
        OutcomeSuccess,
        OutcomeDisagreementBlock,
        OutcomeValidatorFail,
        OutcomeHumanOverride,
    ];

    private static readonly HashSet<string> AllowedOutcomes =
        new(AllowedOutcomeValuesOrdered, StringComparer.Ordinal);

    // ConcurrentDictionary of per-task deques. Each deque is guarded
    // by a lock on the deque itself — finer-grained than a whole-ledger
    // lock so observations from different task classes can proceed in
    // parallel.
    private readonly ConcurrentDictionary<string, LinkedList<PromotionObservation>> _byTask = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public PromotionLedger() : this(() => DateTimeOffset.UtcNow) { }

    /// <summary>Test constructor injecting a deterministic clock.</summary>
    public PromotionLedger(Func<DateTimeOffset> clock)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Record a single observation. Returns the observation as stored
    /// (including the assigned id + timestamp) so callers can chain it
    /// into a proof packet.
    /// </summary>
    public PromotionObservation Record(string taskClass, string patternId, string outcome, string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(patternId);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);

        if (!TryNormalizeOutcome(outcome, out string normalizedOutcome))
        {
            throw new ArgumentException(
                $"Outcome '{outcome}' not recognised. Expected one of: {string.Join(", ", AllowedOutcomeValuesOrdered)}",
                nameof(outcome));
        }

        var observation = new PromotionObservation(
            Id: Guid.NewGuid().ToString("N")[..12],
            TaskClass: taskClass.Trim(),
            PatternId: patternId.Trim(),
            Outcome: normalizedOutcome,
            CapturedAtUtc: _clock(),
            Note: string.IsNullOrWhiteSpace(note) ? null : note.Trim());

        LinkedList<PromotionObservation> deque = _byTask.GetOrAdd(observation.TaskClass, _ => new LinkedList<PromotionObservation>());
        lock (deque)
        {
            deque.AddLast(observation);
            while (deque.Count > PerTaskWindow)
            {
                deque.RemoveFirst();
            }
        }
        return observation;
    }

    public static IReadOnlyList<string> AllowedOutcomeValues => AllowedOutcomeValuesOrdered;

    public static bool TryNormalizeOutcome(string? outcome, out string normalizedOutcome)
    {
        normalizedOutcome = string.Empty;
        if (string.IsNullOrWhiteSpace(outcome))
        {
            return false;
        }

        string candidate = outcome.Trim().ToLowerInvariant();
        if (!AllowedOutcomes.Contains(candidate))
        {
            return false;
        }

        normalizedOutcome = candidate;
        return true;
    }

    /// <summary>
    /// Snapshot summary across every task class the ledger has seen.
    /// Safe to call from any thread; takes a brief per-task lock but
    /// never holds a global lock.
    /// </summary>
    public PromotionSummary Snapshot()
    {
        List<PromotionTaskSummary> taskSummaries = new();
        foreach (KeyValuePair<string, LinkedList<PromotionObservation>> entry in _byTask)
        {
            PromotionObservation[] snapshot;
            lock (entry.Value)
            {
                snapshot = entry.Value.ToArray();
            }
            taskSummaries.Add(Summarise(entry.Key, snapshot));
        }

        taskSummaries.Sort(static (a, b) =>
            string.CompareOrdinal(a.TaskClass, b.TaskClass));

        int promotionCandidates = taskSummaries.Count(s => s.IsPromotionCandidate);
        return new PromotionSummary(
            CapturedAtUtc: _clock(),
            Tasks: taskSummaries,
            PromotionCandidateCount: promotionCandidates);
    }

    private static PromotionTaskSummary Summarise(string taskClass, PromotionObservation[] observations)
    {
        int total = observations.Length;
        int success = observations.Count(o => o.Outcome == OutcomeSuccess);
        int disagreement = observations.Count(o => o.Outcome == OutcomeDisagreementBlock);
        int validatorFail = observations.Count(o => o.Outcome == OutcomeValidatorFail);
        int humanOverride = observations.Count(o => o.Outcome == OutcomeHumanOverride);

        double successRate = total == 0 ? 0.0 : (double)success / total;

        // Check the most-recent window for blocking outcomes.
        int recentCutoff = Math.Max(0, total - RecentBlockWindow);
        bool recentClean = true;
        for (int i = recentCutoff; i < total; i++)
        {
            string o = observations[i].Outcome;
            if (o == OutcomeDisagreementBlock || o == OutcomeHumanOverride)
            {
                recentClean = false;
                break;
            }
        }

        bool isCandidate =
            total >= MinObservationsForPromotion &&
            successRate >= SuccessRateFloor &&
            recentClean;

        string? patternHint = observations
            .GroupBy(o => o.PatternId, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();

        return new PromotionTaskSummary(
            TaskClass: taskClass,
            TotalObservations: total,
            SuccessCount: success,
            DisagreementBlockCount: disagreement,
            ValidatorFailCount: validatorFail,
            HumanOverrideCount: humanOverride,
            SuccessRate: successRate,
            MostCommonPatternId: patternHint,
            IsPromotionCandidate: isCandidate,
            Recommendation: BuildRecommendation(total, successRate, recentClean, isCandidate, patternHint));
    }

    private static string BuildRecommendation(
        int total,
        double successRate,
        bool recentClean,
        bool isCandidate,
        string? patternHint)
    {
        if (isCandidate)
        {
            string suffix = patternHint is null ? string.Empty : $" Most common pattern: '{patternHint}'.";
            return $"Stable: {total} observations, success rate {successRate:P0}, no recent blocks. Consider hard-coding the pattern into deterministic logic.{suffix}";
        }

        if (total < MinObservationsForPromotion)
        {
            return $"Not enough data yet: {total}/{MinObservationsForPromotion} observations. Keep collecting before evaluating promotion.";
        }

        if (!recentClean)
        {
            return $"Not ready: last {RecentBlockWindow} observations contain a disagreement-block or human-override. Resolve the root cause before promoting.";
        }

        return $"Not ready: success rate {successRate:P0} is below the {SuccessRateFloor:P0} floor. Improve the workflow before promoting.";
    }
}

public sealed record PromotionObservation(
    string Id,
    string TaskClass,
    string PatternId,
    string Outcome,
    DateTimeOffset CapturedAtUtc,
    string? Note);

public sealed record PromotionSummary(
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<PromotionTaskSummary> Tasks,
    int PromotionCandidateCount);

public sealed record PromotionTaskSummary(
    string TaskClass,
    int TotalObservations,
    int SuccessCount,
    int DisagreementBlockCount,
    int ValidatorFailCount,
    int HumanOverrideCount,
    double SuccessRate,
    string? MostCommonPatternId,
    bool IsPromotionCandidate,
    string Recommendation);

/// <summary>Wire-level request shape for <c>POST /api/promotion/record</c>.</summary>
public sealed class PromotionRecordRequest
{
    public string? TaskClass { get; init; }
    public string? PatternId { get; init; }
    public string? Outcome { get; init; }
    public string? Note { get; init; }
}
