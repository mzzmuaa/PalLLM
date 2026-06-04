using PalLLM.Domain.Memory;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Concurrency regression guards for the three shared, mutable singletons that
/// the chat hot path, the bridge worker, and health polling all hit at the same
/// time: <see cref="ConversationMemoryStore"/>, <see cref="ChatRateLimiter"/>,
/// and <see cref="RelationshipTracker"/>. Each of these classes guards its state
/// with a single private lock; the rest of the suite exercises them only on one
/// thread, so these tests are the only thing that would catch a future change
/// that drops or narrows that lock.
///
/// Every assertion here is a deterministic invariant (a bounded count, an exact
/// admission total, a lost-update check) rather than a timing budget, so the
/// tests are not flaky on shared CI hardware: they either expose a torn
/// read/write or they pass. Matches the <c>PalStatusLine.NoteActivity</c>
/// thread-safety idiom already used in the suite.
/// </summary>
public sealed class ConcurrencyStressTests
{
    [Test]
    public void ConversationMemoryStore_ConcurrentRememberAndRecall_StaysBoundedAndCrashFree()
    {
        // The store caps at 2,000 entries (MaxEntries). Eight writers each add
        // 400 entries (3,200 total) so the concurrent add-then-trim path under the
        // lock is exercised past the cap, while four readers hammer Recall and
        // GetRecent at the same time. Without the lock, the writers' List<T>
        // Add/RemoveRange would race the readers' CollectionsMarshal.AsSpan copy
        // and throw or corrupt, so a missing lock cannot pass this test.
        const int writerTasks = 8;
        const int writesPerTask = 400;
        const int readerTasks = 4;
        const int readsPerTask = 500;
        const int hardCap = 2_000;

        var store = new ConversationMemoryStore();
        int readerProblems = 0;

        Parallel.For(0, writerTasks + readerTasks, index =>
        {
            if (index < writerTasks)
            {
                for (int i = 0; i < writesPerTask; i++)
                {
                    store.Remember(
                        index,
                        $"Char{index}",
                        "user",
                        $"entry {index}-{i} about raids and base defense",
                        "combat_start");
                }

                return;
            }

            int characterId = index % writerTasks;
            for (int i = 0; i < readsPerTask; i++)
            {
                try
                {
                    IReadOnlyList<ConversationMemoryMatch> matches =
                        store.Recall("raids and base defense", characterId, 5);
                    foreach (ConversationMemoryMatch match in matches)
                    {
                        if (match.Entry is null || float.IsNaN(match.Score))
                        {
                            Interlocked.Increment(ref readerProblems);
                        }
                    }

                    _ = store.GetRecent(10, characterId);
                }
                catch
                {
                    Interlocked.Increment(ref readerProblems);
                }
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(
                store.Count,
                Is.EqualTo(hardCap),
                "Remember must clamp to the 2,000-entry cap atomically: 3,200 concurrent writes must leave exactly 2,000 entries.");
            Assert.That(
                readerProblems,
                Is.Zero,
                "Recall/GetRecent must never throw or observe torn state (null entry / NaN score) while writers mutate the store.");
        });
    }

    [Test]
    public void ChatRateLimiter_ConcurrentTryAcquireOnOneBucket_AdmitsExactlyTheLimit()
    {
        // 1,600 acquire attempts on a single bucket with a per-minute cap of 50.
        // The whole test runs in well under the one-minute window, so no
        // timestamp ages out mid-run: the lock must admit EXACTLY 50 and deny the
        // other 1,550. A broken check-then-enqueue would over-admit.
        const int maxPerMinute = 50;
        const int attemptTasks = 16;
        const int attemptsPerTask = 100;

        var limiter = new ChatRateLimiter { MaxPerMinute = maxPerMinute };
        int granted = 0;

        Parallel.For(0, attemptTasks, _ =>
        {
            for (int i = 0; i < attemptsPerTask; i++)
            {
                if (limiter.TryAcquire("shared-bucket"))
                {
                    Interlocked.Increment(ref granted);
                }
            }
        });

        Assert.That(
            granted,
            Is.EqualTo(maxPerMinute),
            "Concurrent TryAcquire on one bucket must admit exactly MaxPerMinute within the window - no over-admission.");
    }

    [Test]
    public void RelationshipTracker_ConcurrentRecordInteraction_LosesNoUpdates()
    {
        // Read-modify-write on InteractionCount under the lock. 1,600 concurrent
        // interactions for one character must leave InteractionCount == 1,600;
        // a dropped lock would lose increments (count < 1,600). The message is
        // sentiment-neutral so affinity does not enter into the invariant.
        const int interactionTasks = 16;
        const int interactionsPerTask = 100;
        const int characterId = 42;
        DateTimeOffset timestamp = new(2026, 6, 3, 12, 0, 0, TimeSpan.Zero);

        var tracker = new RelationshipTracker();

        Parallel.For(0, interactionTasks, _ =>
        {
            for (int i = 0; i < interactionsPerTask; i++)
            {
                tracker.RecordInteraction(characterId, "Pal", "concurrent ping", timestamp);
            }
        });

        CharacterRelationship? relationship = tracker.TryGet(characterId);
        Assert.Multiple(() =>
        {
            Assert.That(relationship, Is.Not.Null, "The tracked character must survive concurrent writes.");
            Assert.That(
                relationship!.InteractionCount,
                Is.EqualTo(interactionTasks * interactionsPerTask),
                "Concurrent RecordInteraction must not lose any increments: every interaction must be counted.");
        });
    }
}
