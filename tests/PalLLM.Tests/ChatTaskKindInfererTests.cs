using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Pins the deterministic keyword classifier. Every task kind has at
/// least one positive-match test; edge cases (empty input, unmatched
/// phrasing, task-tag override) have dedicated coverage so the
/// inferer's behaviour is stable across unrelated passes.
/// </summary>
public sealed class ChatTaskKindInfererTests
{
    [Test]
    public void Infer_NullOrEmpty_ReturnsImplementDraft()
    {
        Assert.That(ChatTaskKindInferer.Infer(null), Is.EqualTo(DuoTaskKind.ImplementDraft));
        Assert.That(ChatTaskKindInferer.Infer(""), Is.EqualTo(DuoTaskKind.ImplementDraft));
        Assert.That(ChatTaskKindInferer.Infer("   "), Is.EqualTo(DuoTaskKind.ImplementDraft));
    }

    [Test]
    public void Infer_ExplicitTaskTag_OverridesMessageContent()
    {
        // Message would naturally classify as ImplementDraft, but the
        // explicit tag wins.
        DuoTaskKind kind = ChatTaskKindInferer.Infer(
            userMessage: "make this better",
            taskTag: "HighRisk");

        Assert.That(kind, Is.EqualTo(DuoTaskKind.HighRisk));
    }

    [Test]
    public void Infer_UnknownTaskTag_FallsThroughToKeywordInference()
    {
        DuoTaskKind kind = ChatTaskKindInferer.Infer(
            userMessage: "review this pull request",
            taskTag: "NotAValidEnumName");

        Assert.That(kind, Is.EqualTo(DuoTaskKind.Audit));
    }

    [Test]
    public void Infer_HighRiskKeywords_AlwaysWin()
    {
        foreach (string phrase in new[]
        {
            "delete all user records",
            "please wipe the production database",
            "rm -rf the logs directory",
            "rotate the api key for staging",
            "release tag v1.2.3",
        })
        {
            Assert.That(ChatTaskKindInferer.Infer(phrase), Is.EqualTo(DuoTaskKind.HighRisk),
                $"Phrase '{phrase}' must classify as high risk.");
        }
    }

    [Test]
    public void Infer_ArchitecturePhrases_ClassifyAsArchitecturePlan()
    {
        Assert.That(ChatTaskKindInferer.Infer("draft an architecture for our ingestion pipeline"),
            Is.EqualTo(DuoTaskKind.ArchitecturePlan));
        Assert.That(ChatTaskKindInferer.Infer("write a spec for the new notification service"),
            Is.EqualTo(DuoTaskKind.ArchitecturePlan));
        Assert.That(ChatTaskKindInferer.Infer("design a system for rate-limiting chat turns"),
            Is.EqualTo(DuoTaskKind.ArchitecturePlan));
    }

    [Test]
    public void Infer_AuditPhrases_ClassifyAsAudit()
    {
        Assert.That(ChatTaskKindInferer.Infer("review this pull request and flag risks"),
            Is.EqualTo(DuoTaskKind.Audit));
        Assert.That(ChatTaskKindInferer.Infer("audit the current thermal-gate thresholds"),
            Is.EqualTo(DuoTaskKind.Audit));
        Assert.That(ChatTaskKindInferer.Infer("security review the auth flow"),
            Is.EqualTo(DuoTaskKind.Audit));
    }

    [Test]
    public void Infer_ParallelCandidatePhrases_ClassifyAsParallelCandidates()
    {
        Assert.That(ChatTaskKindInferer.Infer("give me 3 alternatives for this header copy"),
            Is.EqualTo(DuoTaskKind.ParallelCandidates));
        Assert.That(ChatTaskKindInferer.Infer("brainstorm variants for the fallback tone"),
            Is.EqualTo(DuoTaskKind.ParallelCandidates));
        Assert.That(ChatTaskKindInferer.Infer("list 5 candidates for the error card"),
            Is.EqualTo(DuoTaskKind.ParallelCandidates));
    }

    [Test]
    public void Infer_MediaPromptingPhrases_ClassifyAsMediaPrompting()
    {
        Assert.That(ChatTaskKindInferer.Infer("write an image prompt for the menu background"),
            Is.EqualTo(DuoTaskKind.MediaPrompting));
        Assert.That(ChatTaskKindInferer.Infer("generate video prompt for the intro"),
            Is.EqualTo(DuoTaskKind.MediaPrompting));
    }

    [Test]
    public void Infer_ToolExecutionPhrases_ClassifyAsToolExecution()
    {
        Assert.That(ChatTaskKindInferer.Infer("please invoke the browser tool"),
            Is.EqualTo(DuoTaskKind.ToolExecution));
        Assert.That(ChatTaskKindInferer.Infer("run the script and summarise the output"),
            Is.EqualTo(DuoTaskKind.ToolExecution));
    }

    [Test]
    public void Infer_LongContextPhrases_ClassifyAsLongContextSynthesis()
    {
        Assert.That(ChatTaskKindInferer.Infer("summarise the entire repo"),
            Is.EqualTo(DuoTaskKind.LongContextSynthesis));
        Assert.That(ChatTaskKindInferer.Infer("read the full transcript and extract decisions"),
            Is.EqualTo(DuoTaskKind.LongContextSynthesis));
    }

    [Test]
    public void Infer_FinalSynthesisPhrases_ClassifyAsFinalSynthesis()
    {
        Assert.That(ChatTaskKindInferer.Infer("merge these three drafts into one"),
            Is.EqualTo(DuoTaskKind.FinalSynthesis));
        Assert.That(ChatTaskKindInferer.Infer("give me the final answer"),
            Is.EqualTo(DuoTaskKind.FinalSynthesis));
    }

    [Test]
    public void Infer_ShortCommandPhrases_ClassifyAsCommandRouting()
    {
        Assert.That(ChatTaskKindInferer.Infer("open the map"),
            Is.EqualTo(DuoTaskKind.CommandRouting));
        Assert.That(ChatTaskKindInferer.Infer("toggle night vision"),
            Is.EqualTo(DuoTaskKind.CommandRouting));
    }

    [Test]
    public void Infer_UnmatchedPhrase_DefaultsToImplementDraft()
    {
        Assert.That(ChatTaskKindInferer.Infer("the sky is purple today"),
            Is.EqualTo(DuoTaskKind.ImplementDraft));
    }

    [Test]
    public void Infer_ShapeIsStable()
    {
        // Same input → same output across calls (no hidden randomness).
        DuoTaskKind first = ChatTaskKindInferer.Infer("audit the thermal gate thresholds");
        DuoTaskKind second = ChatTaskKindInferer.Infer("audit the thermal gate thresholds");
        Assert.That(second, Is.EqualTo(first));
    }
}
