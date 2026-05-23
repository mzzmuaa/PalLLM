namespace PalLLM.Domain.Inference;

/// <summary>
/// Simple three-state circuit breaker for inference calls.
///
/// <para>Closed → every call goes through. On a failure, the consecutive-failure
/// counter bumps; when it hits <c>FailureThreshold</c> the breaker flips Open.</para>
///
/// <para>Open → calls short-circuit without touching the network for
/// <c>CooldownSeconds</c>. Once the cooldown passes, the breaker flips to HalfOpen.</para>
///
/// <para>HalfOpen → a single trial call is allowed. Success closes the breaker;
/// failure re-opens it with a fresh cooldown.</para>
///
/// Trading a failed HTTP call for an immediate fallback reply keeps
/// companions responsive during extended outages (stopped inference server,
/// bad config, flaky cloud tier) while allowing automatic recovery when the
/// endpoint comes back.
/// </summary>
public sealed class InferenceCircuitBreaker
{
    private readonly object _gate = new();
    private CircuitState _state = CircuitState.Closed;
    private int _consecutiveFailures;
    private DateTimeOffset _openedAtUtc;

    public int FailureThreshold { get; set; } = 5;

    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(30);

    public CircuitState State
    {
        get
        {
            lock (_gate)
            {
                RefreshStateLocked(DateTimeOffset.UtcNow);
                return _state;
            }
        }
    }

    public int ConsecutiveFailures
    {
        get
        {
            lock (_gate)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>
    /// Checks whether a call is allowed right now. Call this BEFORE the HTTP request.
    /// When the breaker is Open (within cooldown), returns <c>false</c> so the caller
    /// can skip the network and go straight to fallback.
    /// </summary>
    public bool ShouldAllowCall()
    {
        lock (_gate)
        {
            RefreshStateLocked(DateTimeOffset.UtcNow);
            return _state != CircuitState.Open;
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _state = CircuitState.Closed;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_state == CircuitState.HalfOpen || _consecutiveFailures >= FailureThreshold)
            {
                _state = CircuitState.Open;
                _openedAtUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>Used by tests and operational tools to force a clean state.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = CircuitState.Closed;
            _consecutiveFailures = 0;
            _openedAtUtc = default;
        }
    }

    private void RefreshStateLocked(DateTimeOffset now)
    {
        if (_state != CircuitState.Open)
        {
            return;
        }

        if (now - _openedAtUtc >= Cooldown)
        {
            _state = CircuitState.HalfOpen;
        }
    }
}

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen,
}
