using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Pass 24 — the "apply" verb for the hard-code promotion pipeline.
/// Takes a <see cref="PromotionApplyPreview"/> produced by Pass 14's
/// <see cref="PromotionApplyPreviewBuilder"/> and persists a durable
/// staging artifact set under
/// <c>{RuntimeRoot}/{PromotionApplyOptions.StagingRoot}/</c> so a human
/// reviewer can cherry-pick the change into real source code.
///
/// <para><b>Never mutates source code in place.</b> The apply verb
/// writes three sibling files per invocation:
/// <list type="bullet">
///   <item><c>template-{timestamp}-{task-class}.md</c> — the editor-ready preview + recipe</item>
///   <item><c>rollback-{timestamp}-{task-class}.txt</c> — the single-line rollback command</item>
///   <item><c>packet-{timestamp}-{task-class}.json</c> — the Pass-9 provenance packet</item>
/// </list>
/// Rollback is `Remove-Item` on the three files. There is no source-tree
/// mutation so there is nothing catastrophic to undo. A future pass can
/// extend <see cref="Apply"/> to also write to real source files behind
/// a separate stricter flag — the staging format is designed so that
/// extension is a pure addition.</para>
///
/// <para>Behaviour is fully controlled by <see cref="PromotionApplyOptions"/>:
/// <c>AllowApply=false</c> (default) means <see cref="Apply"/> always
/// returns <see cref="PromotionApplyResult.Status"/> = <c>"refused"</c>
/// without touching disk. Retention is bounded by
/// <see cref="PromotionApplyOptions.MaxStagedArtifacts"/> — the oldest
/// template/rollback/packet triple is deleted on overflow.</para>
/// </summary>
public static class PromotionApplier
{
    /// <summary>
    /// Apply the supplied preview by persisting its template, rollback,
    /// and provenance under the configured staging root. Returns a
    /// structured result describing what happened.
    /// </summary>
    /// <param name="preview">
    /// The Pass-14 preview to materialise. Must be the result of a
    /// successful <c>PromotionApplyPreviewBuilder.Build</c> call — the
    /// caller is responsible for the candidate-status check.
    /// </param>
    /// <param name="options">Runtime options; only <see cref="PalLlmOptions.PromotionApply"/> and the PalSavedRoot are inspected.</param>
    /// <param name="capturedAtUtc">Optional fixed timestamp for deterministic tests. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
    public static PromotionApplyResult Apply(
        PromotionApplyPreview preview,
        PalLlmOptions options,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(options);

        PromotionApplyOptions apply = options.PromotionApply;

        if (!apply.AllowApply)
        {
            return new PromotionApplyResult(
                Status: "refused",
                Reason: "PalLLM:PromotionApply:AllowApply is false. Flip this flag only in environments where a human reviewer will cherry-pick the staging artifacts.",
                StagingRoot: ResolveStagingRoot(options),
                TemplatePath: null,
                RollbackPath: null,
                PacketPath: null,
                ArchivedCount: 0,
                CapturedAtUtc: capturedAtUtc ?? DateTimeOffset.UtcNow);
        }

        string stagingRoot = ResolveStagingRoot(options);
        Directory.CreateDirectory(stagingRoot);

        DateTimeOffset captured = capturedAtUtc ?? DateTimeOffset.UtcNow;
        string stamp = captured.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string safeTask = SanitiseSlug(preview.TaskClass);

        string templatePath = Path.Combine(stagingRoot, $"template-{stamp}-{safeTask}.md");
        string rollbackPath = Path.Combine(stagingRoot, $"rollback-{stamp}-{safeTask}.txt");
        string packetPath = Path.Combine(stagingRoot, $"packet-{stamp}-{safeTask}.json");

        File.WriteAllText(templatePath, BuildTemplateBody(preview, captured), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(rollbackPath, BuildRollbackBody(preview, templatePath, rollbackPath, packetPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.WriteAllText(
            packetPath,
            JsonSerializer.Serialize(preview.Provenance, SerializerJsonContext.ProofPacket),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        int archivedCount = PruneBeyondRetention(stagingRoot, apply.MaxStagedArtifacts);

        return new PromotionApplyResult(
            Status: "staged",
            Reason: $"Promotion staged under {stagingRoot}. Cherry-pick the template into source, commit the change, and delete the staged triple to roll back.",
            StagingRoot: stagingRoot,
            TemplatePath: templatePath,
            RollbackPath: rollbackPath,
            PacketPath: packetPath,
            ArchivedCount: archivedCount,
            CapturedAtUtc: captured);
    }

    private static string ResolveStagingRoot(PalLlmOptions options)
    {
        string configured = string.IsNullOrWhiteSpace(options.PromotionApply.StagingRoot)
            ? "PromotionStaging"
            : options.PromotionApply.StagingRoot!;
        if (Path.IsPathRooted(configured))
        {
            return configured;
        }
        string saveRoot = string.IsNullOrWhiteSpace(options.PalSavedRoot)
            ? AppContext.BaseDirectory
            : options.PalSavedRoot!;
        return Path.Combine(saveRoot, "Runtime", configured);
    }

    private static string BuildTemplateBody(PromotionApplyPreview preview, DateTimeOffset captured)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Promotion staging template");
        sb.AppendLine();
        sb.AppendLine($"- Task class: `{preview.TaskClass}`");
        sb.AppendLine($"- Pattern id:  `{preview.PatternId}`");
        sb.AppendLine($"- Target file: `{preview.TargetFile}`");
        sb.AppendLine($"- Captured:    `{captured:yyyy-MM-dd HH:mm:ss'Z'}`");
        sb.AppendLine();
        sb.AppendLine("## Suggested change");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(preview.DiffPreview.TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        if (preview.SafetyWarnings.Count > 0)
        {
            sb.AppendLine("## Safety warnings");
            sb.AppendLine();
            foreach (string warning in preview.SafetyWarnings)
            {
                sb.AppendLine($"- {warning}");
            }
            sb.AppendLine();
        }
        sb.AppendLine("## Rollback");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine(preview.RollbackCommand);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Provenance");
        sb.AppendLine();
        sb.AppendLine($"- Proof packet id: `{preview.Provenance.Id}`");
        sb.AppendLine($"- Subsystem:       `{preview.Provenance.Subsystem}`");
        sb.AppendLine($"- Decision:        `{preview.Provenance.Decision}`");
        sb.AppendLine($"- Primary reason:  {preview.Provenance.PrimaryReason}");
        sb.AppendLine($"- Human review required: {(preview.Provenance.HumanReviewRequired ? "**yes**" : "no")}");
        return sb.ToString();
    }

    private static string BuildRollbackBody(
        PromotionApplyPreview preview,
        string templatePath,
        string rollbackPath,
        string packetPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Rollback script for promotion staging of task '{preview.TaskClass}'");
        sb.AppendLine("# Apply did NOT mutate source code. Deleting the staged triple fully rolls back.");
        sb.AppendLine();
        sb.AppendLine("# PowerShell:");
        sb.AppendLine($"Remove-Item -Force -Path '{templatePath.Replace('\\', '/')}'");
        sb.AppendLine($"Remove-Item -Force -Path '{rollbackPath.Replace('\\', '/')}'");
        sb.AppendLine($"Remove-Item -Force -Path '{packetPath.Replace('\\', '/')}'");
        sb.AppendLine();
        sb.AppendLine("# Originally surfaced rollback command for the suggested change:");
        sb.AppendLine($"# {preview.RollbackCommand}");
        return sb.ToString();
    }

    private static int PruneBeyondRetention(string stagingRoot, int maxArtifacts)
    {
        if (maxArtifacts <= 0) { return 0; }
        // Group by the timestamp-prefix so a template/rollback/packet triple is removed together.
        var files = new DirectoryInfo(stagingRoot).EnumerateFiles("*.*")
            .Where(f => f.Name.StartsWith("template-", StringComparison.OrdinalIgnoreCase)
                     || f.Name.StartsWith("rollback-", StringComparison.OrdinalIgnoreCase)
                     || f.Name.StartsWith("packet-", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var triples = files
            .GroupBy(f => ExtractTimestamp(f.Name))
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .OrderByDescending(g => g.Key, StringComparer.Ordinal)
            .ToList();
        int removed = 0;
        for (int i = maxArtifacts; i < triples.Count; i++)
        {
            foreach (FileInfo file in triples[i])
            {
                try { file.Delete(); removed++; } catch { /* best-effort prune */ }
            }
        }
        return removed;
    }

    private static string ExtractTimestamp(string filename)
    {
        // Filenames are template-YYYYMMDD-HHMMSS-taskclass.ext etc.
        // Extract `YYYYMMDD-HHMMSS` as the group key.
        string[] parts = filename.Split('-');
        if (parts.Length < 3) { return string.Empty; }
        return parts[1] + "-" + parts[2];
    }

    private static string SanitiseSlug(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) { return "unknown"; }
        var sb = new StringBuilder(value.Length);
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c) || c == '_' || c == '/')
            {
                sb.Append('-');
            }
        }
        return sb.Length == 0 ? "unknown" : sb.ToString().Trim('-');
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly PalLlmDomainJsonSerializerContext SerializerJsonContext = new(SerializerOptions);
}

/// <summary>
/// Wire-level request shape for <c>POST /api/promotion/apply</c>.
/// </summary>
public sealed class PromotionApplyRequest
{
    /// <summary>Task class to promote. Must match a live ledger candidate.</summary>
    public string? TaskClass { get; init; }

    /// <summary>Optional pattern id filter — when omitted, the highest-count pattern for the task is used.</summary>
    public string? PatternId { get; init; }
}

/// <summary>
/// Structured outcome of a <see cref="PromotionApplier.Apply"/> call.
/// </summary>
/// <param name="Status">One of "staged", "refused", "not-candidate", "not-found", "error".</param>
/// <param name="Reason">Plain-English reason the apply returned this status.</param>
/// <param name="StagingRoot">Absolute staging-root path, always set (even on refusal) so callers can show where a future apply would write.</param>
/// <param name="TemplatePath">Absolute path of the template file, or null when not staged.</param>
/// <param name="RollbackPath">Absolute path of the rollback file, or null when not staged.</param>
/// <param name="PacketPath">Absolute path of the provenance packet JSON, or null when not staged.</param>
/// <param name="ArchivedCount">Number of older triples deleted during retention-pruning of the staging root.</param>
/// <param name="CapturedAtUtc">When the apply ran, in UTC.</param>
public sealed record PromotionApplyResult(
    string Status,
    string Reason,
    string StagingRoot,
    string? TemplatePath,
    string? RollbackPath,
    string? PacketPath,
    int ArchivedCount,
    DateTimeOffset CapturedAtUtc);
