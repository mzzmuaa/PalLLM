using System.Security.Cryptography;
using System.Text;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Builds machine-readable ProofPacket bundles for every
//            automated PalLLM decision (capture suggestion, fallback
//            switch, retreat call, etc.) so "provenance becomes a normal
//            product artifact" has a concrete portable implementation.
//            Each packet carries inputs + signals + decision + content
//            hash so an operator can replay any past decision exactly.
//   surface: ProofPacketBuilder.Build (decision -> packet); ProofPacket
//            record. Surfaces via GET /api/proof/* HTTP routes and the
//            matching MCP tools.
//   gate:    Drift_Api_route_count + Drift_OpenApi_snapshot via the
//            registered routes.
//   adr:     The "every automated change gets a proof packet" rule is
//            documented as a convention; load-bearing for harvestability.
//   docs:    docs/CONVENTIONS.md (proof-packet rule),
//            docs/HARVEST.md (proof-packets harvest recipe),
//            docs/REVIEW_CHECKLIST.md (every PR cross-checks
//            proof-packet coverage).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Builds machine-readable <see cref="ProofPacket"/> bundles for
/// automated PalLLM decisions so the 2035 "provenance becomes a normal
/// product artifact" prediction has a concrete, portable implementation.
///
/// <para>Each packet captures:</para>
/// <list type="bullet">
///   <item><b>Who decided</b> — the subsystem (fallback director,
///         self-healing watchdog, duo planner, thermal gate, etc.) and
///         any model artifacts involved.</item>
///   <item><b>What happened</b> — the decision + a short primary
///         reason sentence.</item>
///   <item><b>Why it happened</b> — an evidence list of observable
///         runtime state + signals the decision was grounded in.</item>
///   <item><b>How to undo it</b> — a named rollback path the operator
///         can follow.</item>
///   <item><b>Whether to trust it</b> — a confidence tag plus
///         HumanReviewRequired flag keyed to risk.</item>
/// </list>
///
/// <para>Deterministic — no inference call. Safe to invoke from the
/// always-available layer and from tests. Packet id is a stable hash of
/// the subsystem + decision + captured-at tick so the same decision
/// twice doesn't produce two different ids (important for
/// deduplication in downstream evidence stores).</para>
///
/// <para>Proof packets compose with every existing evidence surface in
/// PalLLM: SelfHealingEvidence, ReleaseEvidence, LaunchEvidence. Rather
/// than replacing those, this builder gives every NEW automated
/// decision a uniform shape so future evidence stores can adopt the
/// same contract without breaking existing consumers.</para>
/// </summary>
public static class ProofPacketBuilder
{
    public const string VersionTag = "proof-packet/1";

    public static ProofPacket Build(
        string subsystem,
        string decision,
        string primaryReason,
        IEnumerable<string> evidenceLines,
        string rollbackPath,
        string confidence = "medium",
        bool humanReviewRequired = false,
        IEnumerable<ProofPacketArtifact>? modelArtifacts = null,
        IEnumerable<ProofPacketValidator>? validatorResults = null,
        DateTimeOffset? capturedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subsystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(decision);

        DateTimeOffset captured = capturedAtUtc ?? DateTimeOffset.UtcNow;
        string[] evidence = (evidenceLines ?? Array.Empty<string>())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        ProofPacketArtifact[] artifacts = (modelArtifacts ?? Array.Empty<ProofPacketArtifact>()).ToArray();
        ProofPacketValidator[] validators = (validatorResults ?? Array.Empty<ProofPacketValidator>()).ToArray();

        string id = ComputeStableId(subsystem, decision, captured);

        return new ProofPacket(
            Version: VersionTag,
            Id: id,
            Subsystem: subsystem.Trim(),
            Decision: decision.Trim(),
            PrimaryReason: (primaryReason ?? string.Empty).Trim(),
            CapturedAtUtc: captured,
            Evidence: evidence,
            ModelArtifacts: artifacts,
            ValidatorResults: validators,
            RollbackPath: (rollbackPath ?? "(no rollback path recorded)").Trim(),
            Confidence: NormalizeConfidence(confidence),
            HumanReviewRequired: humanReviewRequired);
    }

    /// <summary>
    /// Convenience: wrap a <see cref="FallbackBehaviorDecision"/> from
    /// the deterministic director as a proof packet. Useful in
    /// PalLlmRuntime.ChatAsync to attach provenance to every
    /// fallback-served turn without hand-coding the evidence list.
    /// </summary>
    public static ProofPacket FromFallbackDecision(
        FallbackBehaviorDecision decision,
        string chatRequestId,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(decision);

        string[] evidence =
        [
            $"FallbackBehaviorDecision.StrategyId={decision.StrategyId}",
            $"FallbackBehaviorDecision.Phase={decision.Phase}",
            $"FallbackBehaviorDecision.Priority={decision.Priority}",
            .. decision.Signals.Take(10).Select(s => $"signal={s}"),
        ];

        return Build(
            subsystem: "fallback-director",
            decision: $"strategy={decision.StrategyId}",
            primaryReason: string.IsNullOrWhiteSpace(reason) ? decision.Message : reason,
            evidenceLines: evidence,
            rollbackPath: "Disable the fallback path for this turn by setting PalLLM:Fallback:Enabled=false and restarting. The director is the safety net so operators rarely want to do this.",
            confidence: "high",
            humanReviewRequired: false,
            modelArtifacts:
            [
                new ProofPacketArtifact(Kind: "chat-request", Identifier: chatRequestId, Note: "Correlates with outbox envelope + chat trace"),
            ]);
    }

    /// <summary>
    /// Convenience: wrap a <see cref="DisagreementAnalysis"/> result
    /// as a proof packet. When a ParallelDisagreement pattern fires
    /// and the detector returns a block verdict, this packet becomes
    /// the audit record of exactly why we refused to auto-promote.
    /// </summary>
    public static ProofPacket FromDisagreement(
        DisagreementAnalysis analysis,
        string workerModelId,
        string judgeModelId)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        string[] evidence =
        [
            $"SemanticSimilarity={analysis.SemanticSimilarity:F3}",
            $"TokenOverlap={analysis.TokenOverlap:F3}",
            $"LengthRatio={analysis.LengthRatio:F3}",
            $"CombinedScore={analysis.CombinedScore:F3}",
            $"Verdict={analysis.Verdict}",
            $"SafetySignal={analysis.SafetySignal}",
            $"SharedEntities={string.Join(',', analysis.KeyEntityAgreement.Take(5))}",
        ];

        bool humanReview = string.Equals(analysis.SafetySignal, "block", StringComparison.Ordinal);
        return Build(
            subsystem: "duo-disagreement-detector",
            decision: $"verdict={analysis.Verdict} signal={analysis.SafetySignal}",
            primaryReason: analysis.Recommendation,
            evidenceLines: evidence,
            rollbackPath: humanReview
                ? "Block landed automatically. To override, the operator must review both outputs manually and mark the decision accepted; the detector will not accept a retry with the same two outputs."
                : "No rollback needed — the outputs agree enough to proceed normally.",
            confidence: analysis.CombinedScore >= 0.85 ? "high" : (analysis.CombinedScore >= 0.60 ? "medium" : "low"),
            humanReviewRequired: humanReview,
            modelArtifacts:
            [
                new ProofPacketArtifact(Kind: "worker-model", Identifier: workerModelId, Note: "Fast MoE worker output"),
                new ProofPacketArtifact(Kind: "judge-model", Identifier: judgeModelId, Note: "Dense judge output"),
            ]);
    }

    // ---- Helpers ----------------------------------------------------

    private static string ComputeStableId(string subsystem, string decision, DateTimeOffset captured)
    {
        // SHA-256 of the salient inputs, truncated to 12 hex chars. Same
        // (subsystem, decision, ISO 8601 UTC timestamp) → same id, so a
        // packet for "fallback-director emitted strategy=X at tick Y"
        // has a stable identifier across restarts.
        string salt = $"{VersionTag}|{subsystem}|{decision}|{captured.UtcDateTime:o}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(salt));
        StringBuilder sb = new(12);
        for (int i = 0; i < 6; i++) { sb.Append(bytes[i].ToString("x2")); }
        return sb.ToString();
    }

    private static string NormalizeConfidence(string raw)
    {
        string normalized = (raw ?? "medium").Trim().ToLowerInvariant();
        return normalized switch
        {
            "high" or "medium" or "low" => normalized,
            _ => "medium",
        };
    }
}

public sealed class ProofPacketRequest
{
    public string? Subsystem { get; init; }
    public string? Decision { get; init; }
    public string? PrimaryReason { get; init; }
    public List<string>? Evidence { get; init; }
    public string? RollbackPath { get; init; }
    public string? Confidence { get; init; }
    public bool HumanReviewRequired { get; init; }
}

public sealed record ProofPacket(
    string Version,
    string Id,
    string Subsystem,
    string Decision,
    string PrimaryReason,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<ProofPacketArtifact> ModelArtifacts,
    IReadOnlyList<ProofPacketValidator> ValidatorResults,
    string RollbackPath,
    string Confidence,
    bool HumanReviewRequired);

public sealed record ProofPacketArtifact(string Kind, string Identifier, string Note);

public sealed record ProofPacketValidator(string Name, string Status, string Detail);
