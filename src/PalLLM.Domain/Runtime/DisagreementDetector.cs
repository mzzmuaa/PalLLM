using System.Globalization;
using PalLLM.Domain.Portable;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Compares two model outputs deterministically and emits a structured
/// <see cref="DisagreementAnalysis"/> so Pass 8's ParallelDisagreement
/// cooperation pattern can turn "did the Worker and Judge give the same
/// answer?" into a first-class safety signal.
///
/// <para>Three independent similarity measures are blended into the
/// final verdict so a single measure's blind spot doesn't dominate:</para>
/// <list type="bullet">
///   <item><c>SemanticSimilarity</c> — cosine similarity from the
///         portable <see cref="SemanticEmbedder"/>. Catches "same
///         meaning, different wording".</item>
///   <item><c>TokenOverlap</c> — bag-of-words Jaccard index. Catches
///         "same vocabulary, different framing".</item>
///   <item><c>LengthRatio</c> — ratio of the shorter reply over the
///         longer. Catches "one model punted with a one-liner".</item>
/// </list>
///
/// <para>Verdict thresholds are deliberately conservative. The point of
/// the ParallelDisagreement pattern is to BLOCK auto-promotion on
/// disagreement, not to merge contested outputs — so "minor-drift" is
/// surfaced as a warning even when the semantic similarity is high, and
/// any major disagreement always emits <c>SafetySignal=block</c>.</para>
///
/// <para>Deterministic-first: no inference call, no external I/O. Safe
/// to invoke from the always-available layer and from tests without any
/// fixture setup beyond the two strings to compare.</para>
/// </summary>
public static class DisagreementDetector
{
    public static DisagreementAnalysis Compare(string? workerOutput, string? judgeOutput)
    {
        string a = (workerOutput ?? string.Empty).Trim();
        string b = (judgeOutput ?? string.Empty).Trim();

        // Degenerate cases first.
        if (a.Length == 0 && b.Length == 0)
        {
            return new DisagreementAnalysis(
                SemanticSimilarity: 1.0,
                TokenOverlap: 1.0,
                LengthRatio: 1.0,
                CombinedScore: 1.0,
                Verdict: "agree",
                SafetySignal: "proceed",
                Recommendation: "Both outputs are empty — trivially agree, but the caller should probably retry.",
                KeyEntityAgreement: Array.Empty<string>());
        }
        if (a.Length == 0 || b.Length == 0)
        {
            return new DisagreementAnalysis(
                SemanticSimilarity: 0.0,
                TokenOverlap: 0.0,
                LengthRatio: 0.0,
                CombinedScore: 0.0,
                Verdict: "major-disagreement",
                SafetySignal: "block",
                Recommendation: "One output is empty. Treat as a structured failure of that model — do not auto-promote.",
                KeyEntityAgreement: Array.Empty<string>());
        }

        // Three independent similarity signals.
        float[] embedA = SemanticEmbedder.FallbackEmbed(a);
        float[] embedB = SemanticEmbedder.FallbackEmbed(b);
        double semantic = Math.Clamp(SemanticEmbedder.CosineSimilarity(embedA, embedB), 0.0, 1.0);

        (string[] tokensA, string[] tokensB) = (Tokenize(a), Tokenize(b));
        double jaccard = JaccardIndex(tokensA, tokensB);
        double lengthRatio = LengthRatio(a, b);

        // Blend: 55% semantic, 30% token overlap, 15% length ratio.
        // Semantic gets the biggest weight because it's the only signal
        // that catches paraphrases; token overlap catches bag-of-words
        // drift; length ratio catches one-sided punts.
        double combined = (0.55 * semantic) + (0.30 * jaccard) + (0.15 * lengthRatio);
        combined = Math.Clamp(combined, 0.0, 1.0);

        // Entity agreement: capitalised-looking tokens that appear in both.
        string[] sharedEntities = ExtractKeyEntities(tokensA, tokensB);

        // Verdict thresholds — conservative because a false "agree"
        // costs more than a false "disagree" (you re-check the work).
        (string verdict, string safetySignal, string recommendation) = combined switch
        {
            >= 0.85 => ("agree", "proceed",
                "Outputs agree strongly on both meaning and vocabulary. Safe to auto-promote under normal risk gates."),
            >= 0.60 => ("minor-drift", "review",
                "Outputs agree in meaning but drift on wording or length. Surface both to the user or run a validator pass before promoting."),
            _ => ("major-disagreement", "block",
                "Outputs disagree on meaning. Block auto-promotion, run validators, and escalate to human review or a frontier model."),
        };

        return new DisagreementAnalysis(
            SemanticSimilarity: semantic,
            TokenOverlap: jaccard,
            LengthRatio: lengthRatio,
            CombinedScore: combined,
            Verdict: verdict,
            SafetySignal: safetySignal,
            Recommendation: recommendation,
            KeyEntityAgreement: sharedEntities);
    }

    // ---- Helpers ----------------------------------------------------

    private static string[] Tokenize(string text)
    {
        return text
            .ToLowerInvariant()
            .Split(
                new[] { ' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '"', '(', ')', '[', ']', '{', '}', '/', '\\', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static double JaccardIndex(string[] a, string[] b)
    {
        if (a.Length == 0 && b.Length == 0) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;

        HashSet<string> setA = new(a, StringComparer.Ordinal);
        HashSet<string> setB = new(b, StringComparer.Ordinal);
        int intersection = setA.Count(token => setB.Contains(token));
        int union = setA.Count + setB.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static double LengthRatio(string a, string b)
    {
        int la = a.Length;
        int lb = b.Length;
        if (la == 0 && lb == 0) return 1.0;
        if (la == 0 || lb == 0) return 0.0;
        return (double)Math.Min(la, lb) / Math.Max(la, lb);
    }

    private static string[] ExtractKeyEntities(string[] tokensA, string[] tokensB)
    {
        // Simple heuristic: tokens >= 4 chars that appear in both sides.
        // Not perfect NER, but catches file names, function names,
        // distinct nouns, and any capitalised identifiers lowercased
        // during tokenization that still act as content anchors.
        HashSet<string> setB = new(tokensB, StringComparer.Ordinal);
        return tokensA
            .Where(t => t.Length >= 4 && setB.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .Take(10)
            .ToArray();
    }
}

public sealed class DisagreementCheckRequest
{
    public string? WorkerOutput { get; init; }
    public string? JudgeOutput { get; init; }
}

public sealed record DisagreementAnalysis(
    double SemanticSimilarity,
    double TokenOverlap,
    double LengthRatio,
    double CombinedScore,
    string Verdict,
    string SafetySignal,
    string Recommendation,
    IReadOnlyList<string> KeyEntityAgreement)
{
    /// <summary>Convenience formatter for logs and evidence artifacts.</summary>
    public string ToSummaryLine() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "disagreement verdict={0} safety={1} combined={2:F3} (sem={3:F3} tok={4:F3} len={5:F3})",
            Verdict, SafetySignal, CombinedScore, SemanticSimilarity, TokenOverlap, LengthRatio);
}
