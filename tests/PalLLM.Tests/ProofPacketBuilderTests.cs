using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="ProofPacketBuilder"/>. Contract pinned here:
///
/// 1. Basic packets have all required fields populated and NEVER throw
///    with reasonable inputs.
/// 2. Packet id is stable: the same (subsystem, decision, captured-at)
///    produces the same id so downstream stores can dedupe.
/// 3. Confidence is normalised into {high, medium, low}; unknown values
///    default to "medium".
/// 4. <see cref="ProofPacketBuilder.FromDisagreement"/> correctly sets
///    HumanReviewRequired when the safety signal says "block".
/// 5. <see cref="ProofPacketBuilder.FromFallbackDecision"/> turns a
///    FallbackBehaviorDecision into a proof packet with strategy +
///    signals captured as evidence lines.
/// </summary>
public sealed class ProofPacketBuilderTests
{
    [Test]
    public void Build_BasicPacket_PopulatesAllFields()
    {
        var captured = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        ProofPacket packet = ProofPacketBuilder.Build(
            subsystem: "self-healing-watchdog",
            decision: "archived-orphans=3",
            primaryReason: "Three outbox envelopes older than 10 minutes moved to RecoveryArchive.",
            evidenceLines: new[] { "outbox-age=14m", "archive-dir=RecoveryArchive/2026-05-01" },
            rollbackPath: "Restore files from the RecoveryArchive/2026-05-01 folder manually.",
            confidence: "high",
            capturedAtUtc: captured);

        Assert.That(packet.Version, Is.EqualTo(ProofPacketBuilder.VersionTag));
        Assert.That(packet.Id, Has.Length.EqualTo(12),
            "Id must be a 12-char hex identifier.");
        Assert.That(packet.Subsystem, Is.EqualTo("self-healing-watchdog"));
        Assert.That(packet.Decision, Is.EqualTo("archived-orphans=3"));
        Assert.That(packet.PrimaryReason, Is.Not.Empty);
        Assert.That(packet.Evidence, Has.Count.EqualTo(2));
        Assert.That(packet.RollbackPath, Does.Contain("RecoveryArchive"));
        Assert.That(packet.Confidence, Is.EqualTo("high"));
        Assert.That(packet.HumanReviewRequired, Is.False);
        Assert.That(packet.CapturedAtUtc, Is.EqualTo(captured));
    }

    [Test]
    public void Build_SameInputs_ProduceStableId()
    {
        var captured = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

        ProofPacket first = ProofPacketBuilder.Build(
            "duo-planner", "pattern=BranchTournament", "High-risk task", Array.Empty<string>(), "(none)",
            capturedAtUtc: captured);
        ProofPacket second = ProofPacketBuilder.Build(
            "duo-planner", "pattern=BranchTournament", "Different reason text", Array.Empty<string>(), "(none)",
            capturedAtUtc: captured);

        Assert.That(second.Id, Is.EqualTo(first.Id),
            "Packet id must be a hash of subsystem + decision + captured-at only, so repeat decisions dedupe cleanly.");
    }

    [Test]
    public void Build_DifferentCaptureTimes_ProduceDifferentIds()
    {
        ProofPacket a = ProofPacketBuilder.Build(
            "duo-planner", "pattern=BranchTournament", "Reason", Array.Empty<string>(), "(none)",
            capturedAtUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero));
        ProofPacket b = ProofPacketBuilder.Build(
            "duo-planner", "pattern=BranchTournament", "Reason", Array.Empty<string>(), "(none)",
            capturedAtUtc: new DateTimeOffset(2026, 5, 1, 12, 0, 1, TimeSpan.Zero));

        Assert.That(b.Id, Is.Not.EqualTo(a.Id));
    }

    [Test]
    public void Build_WithoutEvidenceLines_DoesNotThrow()
    {
        ProofPacket packet = ProofPacketBuilder.Build(
            "thermal-gate", "decision=reject", "GPU over 85C", null!, "(none)");

        Assert.That(packet.Evidence, Is.Empty);
        Assert.That(packet.Decision, Is.EqualTo("decision=reject"));
    }

    [Test]
    public void Build_UnknownConfidence_NormalisesToMedium()
    {
        ProofPacket a = ProofPacketBuilder.Build(
            "subsystem", "decision", "reason", null!, "(none)", confidence: "excellent");
        ProofPacket b = ProofPacketBuilder.Build(
            "subsystem", "decision", "reason", null!, "(none)", confidence: "");
        ProofPacket c = ProofPacketBuilder.Build(
            "subsystem", "decision", "reason", null!, "(none)", confidence: null!);

        Assert.That(a.Confidence, Is.EqualTo("medium"));
        Assert.That(b.Confidence, Is.EqualTo("medium"));
        Assert.That(c.Confidence, Is.EqualTo("medium"));
    }

    [Test]
    public void Build_MissingSubsystem_Throws()
    {
        // Empty → ArgumentException. Null → ArgumentNullException
        // (a subclass of ArgumentException). Both are acceptable.
        Assert.That(() => ProofPacketBuilder.Build("", "decision", "reason", null!, "(none)"),
            Throws.InstanceOf<ArgumentException>());
        Assert.That(() => ProofPacketBuilder.Build(null!, "decision", "reason", null!, "(none)"),
            Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public void FromDisagreement_BlockVerdict_SetsHumanReviewRequired()
    {
        DisagreementAnalysis analysis = DisagreementDetector.Compare(
            "The refactor plan is to split the cache layer.",
            "Purple elephants in the rain harvest moon.");

        ProofPacket packet = ProofPacketBuilder.FromDisagreement(
            analysis, workerModelId: "qwen3.6:35b-a3b", judgeModelId: "qwen3.6:27b");

        Assert.That(analysis.SafetySignal, Is.EqualTo("block"));
        Assert.That(packet.HumanReviewRequired, Is.True);
        Assert.That(packet.Confidence, Is.EqualTo("low"));
        Assert.That(packet.ModelArtifacts, Has.Count.EqualTo(2));
        Assert.That(packet.ModelArtifacts[0].Kind, Is.EqualTo("worker-model"));
        Assert.That(packet.ModelArtifacts[1].Kind, Is.EqualTo("judge-model"));
    }

    [Test]
    public void FromDisagreement_AgreeVerdict_DoesNotRequireHumanReview()
    {
        const string sameOutput = "The circuit breaker is open because of repeated timeouts.";
        DisagreementAnalysis analysis = DisagreementDetector.Compare(sameOutput, sameOutput);

        ProofPacket packet = ProofPacketBuilder.FromDisagreement(
            analysis, "qwen3.6:35b-a3b", "qwen3.6:27b");

        Assert.That(packet.HumanReviewRequired, Is.False);
        Assert.That(packet.Confidence, Is.EqualTo("high"));
    }

    [Test]
    public void FromFallbackDecision_CapturesStrategyAndSignals()
    {
        var decision = new FallbackBehaviorDecision(
            strategyId: "stealth-withdraw",
            phase: FallbackPacingPhase.Relax,
            message: "Let's slip back into cover.",
            priority: 10,
            signals: ["hostile-nearby", "low-morale"],
            isApplicable: true);

        ProofPacket packet = ProofPacketBuilder.FromFallbackDecision(
            decision, chatRequestId: "chat-42", reason: "Rate limit engaged");

        Assert.That(packet.Subsystem, Is.EqualTo("fallback-director"));
        Assert.That(packet.Decision, Does.Contain("stealth-withdraw"));
        Assert.That(packet.PrimaryReason, Is.EqualTo("Rate limit engaged"));
        Assert.That(packet.Evidence, Does.Contain("signal=hostile-nearby"));
        Assert.That(packet.Evidence, Does.Contain("signal=low-morale"));
        Assert.That(packet.ModelArtifacts, Has.Count.EqualTo(1));
        Assert.That(packet.ModelArtifacts[0].Identifier, Is.EqualTo("chat-42"));
    }
}
