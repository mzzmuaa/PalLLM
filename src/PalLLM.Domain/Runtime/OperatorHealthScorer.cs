using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Computes a single-number operator happiness score from a
/// <see cref="RuntimeHealth"/> snapshot. Designed so a non-technical
/// operator can answer the question "is this companion likely to give
/// good replies right now?" in one glance.
///
/// <para>The score starts at 100 and subtracts for each signal that
/// degrades the player-visible experience. It's deliberately simple and
/// easy to audit: anyone can read the subtraction rules and predict the
/// output.</para>
///
/// <para>Four grades summarise the number for human readers:</para>
/// <list type="bullet">
///   <item><c>Excellent</c> (90-100) — companion fully operational, no
///         recent friction signals.</item>
///   <item><c>Good</c> (70-89) — companion responsive; some optional
///         features are off but nothing is failing.</item>
///   <item><c>Degraded</c> (40-69) — noticeable friction; operators
///         should check <c>/api/health</c> for the specific cause.</item>
///   <item><c>Critical</c> (0-39) — companion still replying via
///         deterministic fallback, but live inference / bridge / vision
///         are broken enough that operator attention is required.</item>
/// </list>
///
/// <para>The top three subtraction reasons (when present) are returned
/// alongside the number so the Field Console and AI callers can show the
/// operator exactly what to fix. The order is stable for a given
/// <see cref="RuntimeHealth"/>, so repeated polls don't flap.</para>
/// </summary>
public static class OperatorHealthScorer
{
    public static OperatorHealthScore Score(RuntimeHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);

        int score = 100;
        List<(int Penalty, string Reason)> reasons = new();

        if (!health.AdapterReady)
        {
            score -= 20;
            reasons.Add((20, "Game adapter is not ready — the bridge cannot deliver replies yet."));
        }

        if (!health.BridgeEnabled)
        {
            score -= 10;
            reasons.Add((10, "Bridge is disabled — chat events from the game will not reach the sidecar."));
        }

        // The deterministic fallback director is the load-bearing guarantee,
        // so if for any reason the runtime reports it missing we drop hard.
        // In practice PalLLM always has it, but we model the signal for any
        // re-harvest consumer who wants the same safety posture.
        if (string.IsNullOrWhiteSpace(health.Status))
        {
            score -= 5;
            reasons.Add((5, "Runtime status line is empty — something initialised incorrectly."));
        }

        // Inference-side penalties only apply when inference is enabled, so
        // an operator running fallback-only never sees a lower score just
        // because they haven't flipped inference on.
        if (health.InferenceConfigured)
        {
            string circuit = health.InferenceCircuitState ?? string.Empty;
            if (string.Equals(circuit, "Open", StringComparison.OrdinalIgnoreCase))
            {
                score -= 15;
                reasons.Add((15, "Inference circuit breaker is OPEN; chat is running on fallback while it cools down."));
            }
            else if (string.Equals(circuit, "HalfOpen", StringComparison.OrdinalIgnoreCase))
            {
                score -= 5;
                reasons.Add((5, "Inference circuit breaker is HALF-OPEN; one trial call will decide the next state."));
            }

            long success = health.InferenceSuccessCount;
            long failure = health.InferenceFailureCount;
            long total = success + failure;
            if (total >= 10)
            {
                double failureRate = (double)failure / total;
                if (failureRate >= 0.25)
                {
                    score -= 15;
                    reasons.Add((15, $"Inference failure rate is high ({failureRate:P0} of {total} recent attempts)."));
                }
                else if (failureRate >= 0.10)
                {
                    score -= 5;
                    reasons.Add((5, $"Inference failure rate is elevated ({failureRate:P0} of {total} recent attempts)."));
                }
            }
        }

        if (health.RateLimitedCount > 0)
        {
            // Runaway callers cap at -5 — the rate limiter is working as
            // intended and the fallback is still replying to players, so
            // this is a note, not a crisis.
            score -= 5;
            reasons.Add((5, $"Rate limiter engaged for {health.RateLimitedCount} request(s); runaway callers routed to fallback."));
        }

        if (score < 0) score = 0;
        if (score > 100) score = 100;

        string grade = score switch
        {
            >= 90 => "Excellent",
            >= 70 => "Good",
            >= 40 => "Degraded",
            _ => "Critical",
        };

        string[] topReasons = reasons
            .OrderByDescending(r => r.Penalty)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .Take(3)
            .Select(r => r.Reason)
            .ToArray();

        string summary = score switch
        {
            >= 90 => "Companion is fully operational. Chat replies should feel responsive and accurate.",
            >= 70 => "Companion is responsive. Some optional subsystems are not contributing, but nothing is failing.",
            >= 40 => "Companion is running on degraded rails. Check /api/health for the specific signal. Fallback keeps replies flowing.",
            _ => "Companion is in critical state. Live inference or bridge wiring is broken; deterministic fallback still replies to the player. Operator attention required.",
        };

        return new OperatorHealthScore(score, grade, summary, topReasons);
    }
}

public sealed record OperatorHealthScore(
    int Score,
    string Grade,
    string Summary,
    IReadOnlyList<string> TopReasons);
