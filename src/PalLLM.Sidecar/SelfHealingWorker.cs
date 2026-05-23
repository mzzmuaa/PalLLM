using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Conservative background watchdog that keeps PalLLM running smoothly over
/// long sessions without ever doing anything destructive. On every tick:
///
/// <list type="number">
///   <item>Archive outbox envelopes older than the configured threshold into
///         <c>Runtime/SelfHealingEvidence/recovered-&lt;UTC&gt;/</c>. Nothing
///         is deleted — an operator can inspect what was moved.</item>
///   <item>Score the current <see cref="RuntimeHealth"/> via
///         <see cref="OperatorHealthScorer"/>; log a structured warning line
///         if the score dips at or below the unhealthy floor so a sidecar
///         running in degraded state surfaces even when nobody is watching
///         the dashboard.</item>
///   <item>Write a durable audit artifact at
///         <c>Runtime/SelfHealingEvidence/latest-self-healing.json</c>
///         describing what the watchdog saw and what it did, plus a rotating
///         history under <c>Runtime/SelfHealingEvidence/History/</c>.</item>
/// </list>
///
/// <para>The worker deliberately does NOT reset the inference circuit
/// breaker or restart the sidecar — those destructive operations stay with
/// the human-driven <c>recover.bat</c> flow. Every tick is strictly
/// additive (archive + log + evidence) so leaving the watchdog on is
/// always safe.</para>
/// </summary>
public sealed class SelfHealingWorker : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly PalLlmRuntime _runtime;
    private readonly ILogger<SelfHealingWorker> _logger;

    public SelfHealingWorker(
        PalLlmOptions options,
        PalLlmRuntime runtime,
        ILogger<SelfHealingWorker> logger)
    {
        _options = options;
        _runtime = runtime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.SelfHealing.Enabled)
        {
            return;
        }

        int seconds = Math.Max(5, _options.SelfHealing.CheckIntervalSeconds);
        TimeSpan interval = TimeSpan.FromSeconds(seconds);

        // Small initial delay so the watchdog doesn't race with startup wiring.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, seconds)), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Self-healing tick failed; deferring to next interval.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Single watchdog pass. Exposed for tests so we can drive a deterministic
    /// tick without spinning up a full hosted-service loop.
    /// </summary>
    public SelfHealingReport Tick(DateTimeOffset nowUtc)
    {
        SelfHealingOptions opt = _options.SelfHealing;
        List<string> observations = new();
        List<string> actions = new();

        // --- Orphan envelope sweep ---------------------------------------
        int orphanCount = 0;
        string? archiveTarget = null;
        if (opt.OrphanEnvelopeAgeSeconds > 0)
        {
            string outbox = _options.BridgeOutboxDir;
            if (Directory.Exists(outbox))
            {
                DateTimeOffset cutoff = nowUtc.AddSeconds(-opt.OrphanEnvelopeAgeSeconds);
                FileInfo[] stale = new DirectoryInfo(outbox)
                    .GetFiles("*.json", SearchOption.TopDirectoryOnly)
                    .Where(f => f.LastWriteTimeUtc < cutoff.UtcDateTime)
                    .ToArray();

                if (stale.Length > 0)
                {
                    string stamp = nowUtc.ToString("yyyyMMdd-HHmmss");
                    archiveTarget = Path.Combine(EvidenceRoot(), "recovered-" + stamp);
                    Directory.CreateDirectory(archiveTarget);
                    foreach (FileInfo file in stale)
                    {
                        try
                        {
                            string dest = Path.Combine(archiveTarget, file.Name);
                            File.Move(file.FullName, dest, overwrite: false);
                            orphanCount++;
                        }
                        catch (IOException)
                        {
                            // Consumer may have picked the file up mid-move.
                            // Best-effort; try again next tick.
                        }
                    }
                    if (orphanCount > 0)
                    {
                        observations.Add($"Found {orphanCount} orphan outbox envelope(s) older than {opt.OrphanEnvelopeAgeSeconds}s.");
                        actions.Add($"Archived {orphanCount} envelope(s) to {archiveTarget}.");
                    }
                }
            }
        }

        // --- Health scoring -----------------------------------------------
        RuntimeHealth health = _runtime.GetHealth();
        OperatorHealthScore score = OperatorHealthScorer.Score(health);
        if (opt.UnhealthyScoreFloor > 0 && score.Score <= opt.UnhealthyScoreFloor)
        {
            _logger.LogWarning(
                "PalLLM operator-health score is {Score} ({Grade}). Top reasons: {Reasons}",
                score.Score,
                score.Grade,
                string.Join(" | ", score.TopReasons));
            observations.Add($"Operator health score {score.Score} ({score.Grade}) is at or below the unhealthy floor ({opt.UnhealthyScoreFloor}).");
        }

        // --- Evidence artifact --------------------------------------------
        SelfHealingReport report = new(
            CapturedAtUtc: nowUtc,
            TickIntervalSeconds: Math.Max(5, opt.CheckIntervalSeconds),
            OrphanEnvelopesArchived: orphanCount,
            ArchiveDirectory: archiveTarget,
            OperatorHealth: score,
            Observations: observations,
            Actions: actions);

        try
        {
            WriteEvidence(report, opt);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Self-healing evidence write failed; next tick will retry.");
        }

        return report;
    }

    private Task TickAsync(CancellationToken stoppingToken)
    {
        _ = Tick(DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }

    private string EvidenceRoot()
        => Path.Combine(_options.RuntimeRoot, "SelfHealingEvidence");

    private void WriteEvidence(SelfHealingReport report, SelfHealingOptions opt)
    {
        string root = EvidenceRoot();
        Directory.CreateDirectory(root);

        string latest = Path.Combine(root, "latest-self-healing.json");
        string payload = JsonSerializer.Serialize(report, SelfHealingEvidenceJsonContext.Default.SelfHealingReport);
        File.WriteAllText(latest, payload);

        if (opt.HistoryRetention > 0)
        {
            string historyDir = Path.Combine(root, "History");
            Directory.CreateDirectory(historyDir);
            string stamp = report.CapturedAtUtc.ToString("yyyyMMdd-HHmmss");
            File.WriteAllText(Path.Combine(historyDir, "self-healing-" + stamp + ".json"), payload);

            // Retention sweep — keep only the most recent N snapshots.
            FileInfo[] history = new DirectoryInfo(historyDir)
                .GetFiles("self-healing-*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToArray();
            for (int i = opt.HistoryRetention; i < history.Length; i++)
            {
                try { history[i].Delete(); }
                catch (IOException) { /* best-effort */ }
            }
        }
    }
}

public sealed record SelfHealingReport(
    DateTimeOffset CapturedAtUtc,
    int TickIntervalSeconds,
    int OrphanEnvelopesArchived,
    string? ArchiveDirectory,
    OperatorHealthScore OperatorHealth,
    IReadOnlyList<string> Observations,
    IReadOnlyList<string> Actions);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(GenerationMode = System.Text.Json.Serialization.JsonSourceGenerationMode.Metadata)]
[System.Text.Json.Serialization.JsonSerializable(typeof(SelfHealingReport))]
internal partial class SelfHealingEvidenceJsonContext : System.Text.Json.Serialization.JsonSerializerContext { }
