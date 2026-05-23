// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Per-bucket (character-id) sliding-window rate limiter on the
//            chat hot path. Stops a runaway caller / misbehaving UE4SS loop
//            from burning the inference budget; rate-limited chats fall
//            through to the deterministic fallback so the player still
//            gets a reply.
//   surface: ChatRateLimiter (TryAcquire / Reset). Constructed once per
//            sidecar and shared across PalLlmRuntime.ChatAsync calls.
//   gate:    None directly; behaviour pinned by ChatRateLimiterTests.cs.
//   adr:     None.
//   docs:    docs/HOT_PATH.md (chat-path budgets), docs/TUNING.md
//            ("MaxCharacterRequestsPerMinute" knob).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Per-bucket sliding-window rate limiter. Used by <c>PalLlmRuntime.ChatAsync</c>
/// to stop a runaway caller (or a misbehaving UE4SS loop) from burning the
/// inference budget. Each bucket — by default the character id — has an
/// independent one-minute window.
///
/// When the window is full, <see cref="TryAcquire"/> returns <c>false</c> so the
/// caller can skip the inference call and fall through to deterministic fallback
/// instead. Fallback replies are free, so a rate-limited chat still produces a
/// sensible reply; it just doesn't cost tokens.
///
/// The implementation is bounded by design: the per-bucket queue holds at most
/// <c>MaxPerMinute</c> timestamps. Buckets with no recent activity are pruned on
/// every acquire so a long-running sidecar does not accumulate dictionary entries
/// for one-shot callers.
/// </summary>
public sealed class ChatRateLimiter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _buckets = new(StringComparer.Ordinal);

    public int MaxPerMinute { get; set; }

    public bool IsEnabled => MaxPerMinute > 0;

    public bool TryAcquire(string bucket)
    {
        if (MaxPerMinute <= 0)
        {
            return true;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset cutoff = now - Window;

        lock (_gate)
        {
            if (!_buckets.TryGetValue(bucket, out Queue<DateTimeOffset>? queue))
            {
                queue = new Queue<DateTimeOffset>();
                _buckets[bucket] = queue;
            }

            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxPerMinute)
            {
                PruneIdleBucketsLocked(cutoff, bucket);
                return false;
            }

            queue.Enqueue(now);
            PruneIdleBucketsLocked(cutoff, bucket);
            return true;
        }
    }

    public int BucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    private void PruneIdleBucketsLocked(DateTimeOffset cutoff, string? preserveBucket = null)
    {
        // Drop buckets whose entire window has aged out. O(buckets) but runs only
        // on each acquire and dictionary mutation is cheap for the small counts
        // PalLLM sees (one bucket per active character).
        List<string>? toRemove = null;
        foreach (KeyValuePair<string, Queue<DateTimeOffset>> entry in _buckets)
        {
            if (!string.IsNullOrWhiteSpace(preserveBucket)
                && string.Equals(entry.Key, preserveBucket, StringComparison.Ordinal))
            {
                continue;
            }

            Queue<DateTimeOffset> queue = entry.Value;
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count == 0)
            {
                (toRemove ??= []).Add(entry.Key);
            }
        }

        if (toRemove is null)
        {
            return;
        }

        foreach (string key in toRemove)
        {
            if (_buckets.TryGetValue(key, out Queue<DateTimeOffset>? q) && q.Count == 0)
            {
                _buckets.Remove(key);
            }
        }
    }
}
