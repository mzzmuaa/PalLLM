using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

// Pass 302 (companion) - direct unit tests for the global status-line
// state used by the sidecar host to surface startup / ready / error
// state to the operator (dashboard `/api/health`, console banner, and
// dashboard "PalLLM is ..." line). The state is intentionally static
// because there is exactly one sidecar process and the status-line is
// observed by multiple workers concurrently; this fixture is marked
// non-parallelizable so concurrent tests do not race on the shared
// state.
//
// Until this pass the helper was only covered indirectly via sidecar
// startup tests. The 3 state-mutator methods (`Set` /  `SetReady` /
// `SetError`) and the activity-count `Interlocked.Increment` had no
// direct fast-feedback coverage. A regression that, say, left
// `IsReady=true` when `SetError` was called would silently mislead the
// operator dashboard into reporting a healthy sidecar mid-failure.
[NonParallelizable]
public sealed class PalStatusLineTests
{
    [SetUp]
    public void ResetStaticState()
    {
        // Reset to a known baseline before each test. The static state
        // persists across tests in the same process so we need an
        // explicit reset to avoid order-dependent flakes.
        PalStatusLine.Set("test-baseline");
    }

    // ---------- Set: clears both ready and error flags ----------

    [Test]
    public void Set_ClearsReadyAndErrorFlags()
    {
        PalStatusLine.SetReady("ready first");
        Assert.That(PalStatusLine.IsReady, Is.True);

        PalStatusLine.Set("now in-progress");

        Assert.That(PalStatusLine.Current, Is.EqualTo("now in-progress"));
        Assert.That(PalStatusLine.IsReady, Is.False);
        Assert.That(PalStatusLine.IsError, Is.False);
    }

    [Test]
    public void Set_AfterError_ClearsErrorFlag()
    {
        PalStatusLine.SetError("boom");
        Assert.That(PalStatusLine.IsError, Is.True);

        PalStatusLine.Set("recovered");

        Assert.That(PalStatusLine.IsError, Is.False);
        Assert.That(PalStatusLine.Current, Is.EqualTo("recovered"));
    }

    // ---------- SetReady ----------

    [Test]
    public void SetReady_SetsReadyFlagAndClearsError()
    {
        PalStatusLine.SetError("pre-flight failure");

        PalStatusLine.SetReady("ready on port 5088");

        Assert.That(PalStatusLine.Current, Is.EqualTo("ready on port 5088"));
        Assert.That(PalStatusLine.IsReady, Is.True);
        Assert.That(PalStatusLine.IsError, Is.False);
    }

    // ---------- SetError ----------

    [Test]
    public void SetError_SetsErrorFlagAndClearsReady()
    {
        PalStatusLine.SetReady("ready");

        PalStatusLine.SetError("port bind failed");

        Assert.That(PalStatusLine.Current, Is.EqualTo("port bind failed"));
        Assert.That(PalStatusLine.IsError, Is.True);
        Assert.That(PalStatusLine.IsReady, Is.False);
    }

    [Test]
    public void Ready_And_Error_AreMutuallyExclusive()
    {
        // Sanity: across every state-mutator sequence, at most one of
        // {IsReady, IsError} is true. The dashboard relies on this
        // invariant to render a single posture.
        PalStatusLine.Set("starting");
        Assert.That(PalStatusLine.IsReady && PalStatusLine.IsError, Is.False);

        PalStatusLine.SetReady("ready");
        Assert.That(PalStatusLine.IsReady && PalStatusLine.IsError, Is.False);

        PalStatusLine.SetError("boom");
        Assert.That(PalStatusLine.IsReady && PalStatusLine.IsError, Is.False);

        PalStatusLine.Set("recovering");
        Assert.That(PalStatusLine.IsReady && PalStatusLine.IsError, Is.False);
    }

    // ---------- Current message is the most-recent call ----------

    [Test]
    public void Current_ReflectsMostRecentMessage()
    {
        PalStatusLine.Set("first");
        PalStatusLine.Set("second");
        PalStatusLine.SetReady("third");

        Assert.That(PalStatusLine.Current, Is.EqualTo("third"));
    }

    // ---------- ActivityCount ----------

    [Test]
    public void ActivityCount_MonotonicallyIncreases_AcrossNoteActivityCalls()
    {
        int before = PalStatusLine.ActivityCount;

        PalStatusLine.NoteActivity();
        PalStatusLine.NoteActivity();
        PalStatusLine.NoteActivity();

        Assert.That(PalStatusLine.ActivityCount, Is.EqualTo(before + 3));
    }

    [Test]
    public void NoteActivity_IsThreadSafe()
    {
        // The activity counter must use Interlocked semantics so concurrent
        // workers do not lose increments. Run 1000 increments across 10
        // parallel tasks and assert the final count matches exactly.
        int before = PalStatusLine.ActivityCount;
        const int tasks = 10;
        const int incrementsPerTask = 100;

        Parallel.For(0, tasks, _ =>
        {
            for (int i = 0; i < incrementsPerTask; i++)
            {
                PalStatusLine.NoteActivity();
            }
        });

        Assert.That(PalStatusLine.ActivityCount, Is.EqualTo(before + tasks * incrementsPerTask),
            "NoteActivity must be thread-safe: concurrent increments cannot be lost.");
    }

    // ---------- Empty / whitespace messages forwarded as-is ----------

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("ready 🎮")]
    public void Set_AcceptsAnyMessageStringWithoutValidation(string message)
    {
        // The status line is purely descriptive — operators may pass any
        // string. The class deliberately does not validate or trim.
        PalStatusLine.Set(message);

        Assert.That(PalStatusLine.Current, Is.EqualTo(message));
    }
}
