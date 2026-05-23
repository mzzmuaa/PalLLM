using NUnit.Framework;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pass 24 — regression coverage for the promotion apply verb.
/// Pinned contract:
/// <list type="number">
///   <item>AllowApply=false (default) never touches disk and returns Status=refused.</item>
///   <item>AllowApply=true writes exactly 3 files per invocation (template, rollback, packet) with matching timestamp prefixes.</item>
///   <item>Rollback file contains the delete-the-triple instructions so rolling back is visibly safe.</item>
///   <item>MaxStagedArtifacts is honoured — older triples are pruned on overflow.</item>
/// </list>
/// </summary>
[TestFixture]
public class PromotionApplierTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests.Apply", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Test]
    public void Apply_WhenAllowApplyIsFalse_RefusesWithoutWriting()
    {
        var options = new PalLlmOptions
        {
            PalSavedRoot = _root,
            PromotionApply = new PromotionApplyOptions { AllowApply = false },
        };
        PromotionApplyPreview preview = BuildPreview();

        PromotionApplyResult result = PromotionApplier.Apply(preview, options);

        Assert.That(result.Status, Is.EqualTo("refused"));
        Assert.That(result.TemplatePath, Is.Null);
        Assert.That(result.RollbackPath, Is.Null);
        Assert.That(result.PacketPath, Is.Null);
        Assert.That(result.StagingRoot, Does.Contain("PromotionStaging"),
            "Even on refusal, the result must tell the caller where a future apply would write.");
        Assert.That(Directory.Exists(result.StagingRoot), Is.False,
            "Refused apply must not create the staging directory.");
    }

    [Test]
    public void Apply_WhenAllowApplyIsTrue_WritesTripleUnderStagingRoot()
    {
        var options = new PalLlmOptions
        {
            PalSavedRoot = _root,
            PromotionApply = new PromotionApplyOptions { AllowApply = true },
        };
        PromotionApplyPreview preview = BuildPreview();
        DateTimeOffset fixedTime = new(2026, 4, 24, 12, 0, 0, TimeSpan.Zero);

        PromotionApplyResult result = PromotionApplier.Apply(preview, options, fixedTime);

        Assert.That(result.Status, Is.EqualTo("staged"));
        Assert.That(result.TemplatePath, Is.Not.Null);
        Assert.That(result.RollbackPath, Is.Not.Null);
        Assert.That(result.PacketPath, Is.Not.Null);
        Assert.That(File.Exists(result.TemplatePath!), Is.True);
        Assert.That(File.Exists(result.RollbackPath!), Is.True);
        Assert.That(File.Exists(result.PacketPath!), Is.True);

        string template = File.ReadAllText(result.TemplatePath!);
        Assert.That(template, Does.Contain("# Promotion staging template"));
        Assert.That(template, Does.Contain("fallback-director"),
            "Template must surface the task class so a reviewer can identify the candidate.");
        Assert.That(template, Does.Contain("## Rollback"),
            "Template must document how to roll back.");

        string rollback = File.ReadAllText(result.RollbackPath!);
        Assert.That(rollback, Does.Contain("Remove-Item"),
            "Rollback file must carry the Remove-Item instructions.");
        Assert.That(rollback, Does.Contain(result.TemplatePath!.Replace('\\', '/')),
            "Rollback file must reference its own template path.");
    }

    [Test]
    public void Apply_HonoursMaxStagedArtifactsBound()
    {
        var options = new PalLlmOptions
        {
            PalSavedRoot = _root,
            PromotionApply = new PromotionApplyOptions
            {
                AllowApply = true,
                MaxStagedArtifacts = 2,
            },
        };
        PromotionApplyPreview preview = BuildPreview();

        // Write three invocations at distinct timestamps so they sort.
        PromotionApplier.Apply(preview, options, new DateTimeOffset(2026, 4, 24, 10, 0, 0, TimeSpan.Zero));
        PromotionApplier.Apply(preview, options, new DateTimeOffset(2026, 4, 24, 11, 0, 0, TimeSpan.Zero));
        PromotionApplyResult third = PromotionApplier.Apply(preview, options, new DateTimeOffset(2026, 4, 24, 12, 0, 0, TimeSpan.Zero));

        // After three invocations with MaxStagedArtifacts=2, exactly
        // one triple should have been pruned (3 files removed).
        Assert.That(third.ArchivedCount, Is.EqualTo(3));
        int templateCount = Directory.GetFiles(third.StagingRoot, "template-*.md").Length;
        int rollbackCount = Directory.GetFiles(third.StagingRoot, "rollback-*.txt").Length;
        int packetCount = Directory.GetFiles(third.StagingRoot, "packet-*.json").Length;
        Assert.That(templateCount, Is.EqualTo(2));
        Assert.That(rollbackCount, Is.EqualTo(2));
        Assert.That(packetCount, Is.EqualTo(2));
    }

    [Test]
    public void Apply_PacketJsonMatchesPreviewProvenance()
    {
        var options = new PalLlmOptions
        {
            PalSavedRoot = _root,
            PromotionApply = new PromotionApplyOptions { AllowApply = true },
        };
        PromotionApplyPreview preview = BuildPreview();

        PromotionApplyResult result = PromotionApplier.Apply(preview, options);

        string packetJson = File.ReadAllText(result.PacketPath!);
        Assert.That(packetJson, Does.Contain(preview.Provenance.Id),
            "Packet JSON must carry the exact provenance id so auditors can cross-reference.");
        Assert.That(packetJson, Does.Contain(preview.Provenance.Subsystem),
            "Packet JSON must carry the subsystem marker.");
    }

    private static PromotionApplyPreview BuildPreview()
    {
        var task = new PromotionTaskSummary(
            TaskClass: "fallback-director",
            TotalObservations: 25,
            SuccessCount: 22,
            DisagreementBlockCount: 2,
            ValidatorFailCount: 1,
            HumanOverrideCount: 0,
            SuccessRate: 0.88,
            MostCommonPatternId: "general-director",
            IsPromotionCandidate: true,
            Recommendation: "Stable enough to propose as a deterministic shortcut.");
        DateTimeOffset captured = DateTimeOffset.UtcNow;
        PromotionSuggestion suggestion = PromotionSuggestionBuilder.BuildForTask(task, captured);
        return PromotionApplyPreviewBuilder.Build(suggestion);
    }
}
