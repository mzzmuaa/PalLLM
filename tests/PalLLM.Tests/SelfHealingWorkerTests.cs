using Microsoft.Extensions.Logging.Abstractions;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

/// <summary>
/// Covers the <see cref="SelfHealingWorker.Tick"/> contract. These tests
/// pin the user-facing behaviour of the watchdog:
///
/// 1. With nothing stuck, a tick is a no-op — no archives created, no
///    actions recorded, and the evidence file is still written so
///    operators can confirm the watchdog is alive.
/// 2. Outbox envelopes older than the configured threshold get moved to
///    <c>Runtime/SelfHealingEvidence/recovered-&lt;UTC&gt;/</c>, not
///    deleted, so an operator can recover them if needed.
/// 3. Recent envelopes are untouched — only genuinely stale files are
///    swept.
/// 4. The watchdog never restarts the sidecar or resets the circuit
///    breaker: its job is observability + gentle janitorial work, not
///    destructive recovery.
/// </summary>
public sealed class SelfHealingWorkerTests
{
    [Test]
    public void Tick_WithNothingStuck_WritesEvidenceButTakesNoAction()
    {
        using TestEnvironment env = TestEnvironment.Fresh();
        SelfHealingWorker worker = env.BuildWorker();

        SelfHealingReport report = worker.Tick(DateTimeOffset.UtcNow);

        Assert.That(report.OrphanEnvelopesArchived, Is.EqualTo(0));
        Assert.That(report.ArchiveDirectory, Is.Null);
        Assert.That(report.Actions, Is.Empty, "No actions should be taken when nothing is stuck.");
        Assert.That(File.Exists(Path.Combine(env.RuntimeRoot, "SelfHealingEvidence", "latest-self-healing.json")), Is.True,
            "Evidence file must be written even on no-op ticks so operators can confirm the watchdog is alive.");
    }

    [Test]
    public void Tick_ArchivesOutboxEnvelopesOlderThanThreshold()
    {
        using TestEnvironment env = TestEnvironment.Fresh();
        SelfHealingWorker worker = env.BuildWorker(orphanAgeSeconds: 60);

        // One fresh envelope, one ancient envelope.
        string fresh = Path.Combine(env.Options.BridgeOutboxDir, "chat_reply-fresh.json");
        string stale = Path.Combine(env.Options.BridgeOutboxDir, "chat_reply-stale.json");
        Directory.CreateDirectory(env.Options.BridgeOutboxDir);
        File.WriteAllText(fresh, "{}");
        File.WriteAllText(stale, "{}");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        SelfHealingReport report = worker.Tick(DateTimeOffset.UtcNow);

        Assert.That(report.OrphanEnvelopesArchived, Is.EqualTo(1),
            "Only the stale envelope should have been archived.");
        Assert.That(report.ArchiveDirectory, Is.Not.Null);
        Assert.That(Directory.Exists(report.ArchiveDirectory!), Is.True);
        Assert.That(Directory.GetFiles(report.ArchiveDirectory!), Has.Length.EqualTo(1));

        Assert.That(File.Exists(fresh), Is.True, "Fresh envelope must stay put.");
        Assert.That(File.Exists(stale), Is.False, "Stale envelope must have been moved.");
    }

    [Test]
    public void Tick_OrphanAgeZero_DisablesSweep()
    {
        using TestEnvironment env = TestEnvironment.Fresh();
        SelfHealingWorker worker = env.BuildWorker(orphanAgeSeconds: 0);

        string stale = Path.Combine(env.Options.BridgeOutboxDir, "chat_reply-stale.json");
        Directory.CreateDirectory(env.Options.BridgeOutboxDir);
        File.WriteAllText(stale, "{}");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));

        SelfHealingReport report = worker.Tick(DateTimeOffset.UtcNow);

        Assert.That(report.OrphanEnvelopesArchived, Is.EqualTo(0));
        Assert.That(File.Exists(stale), Is.True,
            "With OrphanEnvelopeAgeSeconds=0 the sweep must be disabled and even very stale envelopes stay put.");
    }

    [Test]
    public void Tick_EvidenceFileIsValidJsonAndMatchesReport()
    {
        using TestEnvironment env = TestEnvironment.Fresh();
        SelfHealingWorker worker = env.BuildWorker();

        SelfHealingReport report = worker.Tick(DateTimeOffset.UtcNow);

        string evidencePath = Path.Combine(env.RuntimeRoot, "SelfHealingEvidence", "latest-self-healing.json");
        string json = File.ReadAllText(evidencePath);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("OrphanEnvelopesArchived").GetInt32(),
            Is.EqualTo(report.OrphanEnvelopesArchived));
        Assert.That(doc.RootElement.GetProperty("OperatorHealth").GetProperty("Score").GetInt32(),
            Is.EqualTo(report.OperatorHealth.Score));
    }

    private sealed class TestEnvironment : IDisposable
    {
        public required string RuntimeRoot { get; init; }
        public required PalLlmOptions Options { get; init; }
        public required PalLlmRuntime Runtime { get; init; }

        public static TestEnvironment Fresh()
        {
            string root = Path.Combine(Path.GetTempPath(), "palllm-selfheal-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(root);

            var options = new PalLlmOptions
            {
                PalSavedRoot = root,
            };
            options.EnsureDirectories();

            // Minimal runtime wiring: use a disabled inference client so no
            // live HTTP happens. The self-healing worker only needs health
            // snapshots, not inference.
            using var httpClient = new HttpClient(new AlwaysFailHandler());
            IInferenceClient client = new HttpJsonInferenceClient(httpClient, options);
            var runtime = new PalLlmRuntime(options, client);

            return new TestEnvironment
            {
                RuntimeRoot = options.RuntimeRoot,
                Options = options,
                Runtime = runtime,
            };
        }

        public SelfHealingWorker BuildWorker(int? orphanAgeSeconds = null)
        {
            if (orphanAgeSeconds.HasValue)
            {
                Options.SelfHealing.OrphanEnvelopeAgeSeconds = orphanAgeSeconds.Value;
            }
            return new SelfHealingWorker(Options, Runtime, NullLogger<SelfHealingWorker>.Instance);
        }

        public void Dispose()
        {
            try { Directory.Delete(RuntimeRoot, recursive: true); }
            catch { /* best-effort */ }
        }

        private sealed class AlwaysFailHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => throw new InvalidOperationException("Inference is disabled in the self-healing tests.");
        }
    }
}
