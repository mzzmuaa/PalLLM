using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Covers <see cref="DisagreementDetector"/>. Contract pinned here:
///
/// 1. Identical strings → verdict "agree" / safetySignal "proceed" /
///    combined score near 1.0.
/// 2. Paraphrases of the same idea → verdict "agree" or "minor-drift"
///    (semantic similarity dominates).
/// 3. Completely different outputs → verdict "major-disagreement" /
///    safetySignal "block".
/// 4. One output empty → always "block" — never treated as agreement.
/// 5. Both empty → degenerate-agree with a hint that the caller should
///    retry.
/// 6. Verdict thresholds (0.85 agree / 0.60 review / under block) are
///    stable so dashboards don't flap.
/// 7. Key-entity agreement list is populated when shared substantial
///    tokens exist in both outputs.
/// </summary>
public sealed class DisagreementDetectorTests
{
    [Test]
    public void Compare_IdenticalStrings_ReturnsAgree()
    {
        const string output = "The circuit breaker is open because of repeated timeout errors against the configured endpoint.";

        DisagreementAnalysis analysis = DisagreementDetector.Compare(output, output);

        Assert.That(analysis.Verdict, Is.EqualTo("agree"));
        Assert.That(analysis.SafetySignal, Is.EqualTo("proceed"));
        Assert.That(analysis.CombinedScore, Is.GreaterThanOrEqualTo(0.85));
        Assert.That(analysis.SemanticSimilarity, Is.GreaterThanOrEqualTo(0.99));
        Assert.That(analysis.LengthRatio, Is.EqualTo(1.0));
    }

    [Test]
    public void Compare_Paraphrase_ReturnsAgreeOrMinorDrift()
    {
        // Same meaning, different wording.
        string worker = "The request failed because the inference endpoint timed out repeatedly.";
        string judge = "Inference endpoint timed out several times, causing the request to fail.";

        DisagreementAnalysis analysis = DisagreementDetector.Compare(worker, judge);

        Assert.That(analysis.Verdict, Is.AnyOf("agree", "minor-drift"),
            $"Paraphrases must not be flagged as major-disagreement. Combined={analysis.CombinedScore:F3}");
        Assert.That(analysis.SafetySignal, Is.Not.EqualTo("block"));
    }

    [Test]
    public void Compare_CompletelyDifferentOutputs_ReturnsMajorDisagreement()
    {
        string worker = "The plan is to refactor the cache layer and add invalidation callbacks.";
        string judge = "Purple elephants do not dance in the rain during the harvest moon.";

        DisagreementAnalysis analysis = DisagreementDetector.Compare(worker, judge);

        Assert.That(analysis.Verdict, Is.EqualTo("major-disagreement"));
        Assert.That(analysis.SafetySignal, Is.EqualTo("block"));
        Assert.That(analysis.Recommendation, Does.Contain("Block"));
    }

    [Test]
    public void Compare_OneOutputEmpty_AlwaysBlocks()
    {
        DisagreementAnalysis a = DisagreementDetector.Compare("Something here", "");
        Assert.That(a.Verdict, Is.EqualTo("major-disagreement"));
        Assert.That(a.SafetySignal, Is.EqualTo("block"));

        DisagreementAnalysis b = DisagreementDetector.Compare("", "Something here");
        Assert.That(b.Verdict, Is.EqualTo("major-disagreement"));
        Assert.That(b.SafetySignal, Is.EqualTo("block"));
    }

    [Test]
    public void Compare_BothEmpty_DegenerateAgreeWithRetryHint()
    {
        DisagreementAnalysis analysis = DisagreementDetector.Compare("", "");

        Assert.That(analysis.Verdict, Is.EqualTo("agree"));
        Assert.That(analysis.CombinedScore, Is.EqualTo(1.0));
        Assert.That(analysis.Recommendation, Does.Contain("retry"));
    }

    [Test]
    public void Compare_NullInputs_TreatedAsEmpty()
    {
        // Both null should behave identically to both empty.
        DisagreementAnalysis nullBoth = DisagreementDetector.Compare(null, null);
        Assert.That(nullBoth.Verdict, Is.EqualTo("agree"));

        DisagreementAnalysis nullOne = DisagreementDetector.Compare(null, "something");
        Assert.That(nullOne.Verdict, Is.EqualTo("major-disagreement"));
        Assert.That(nullOne.SafetySignal, Is.EqualTo("block"));
    }

    [Test]
    public void Compare_SharedEntities_PopulatedWhenTokensOverlap()
    {
        string worker = "The config file uses the baseurl inference setting for the worker endpoint.";
        string judge = "Update config file, baseurl inference setting, and worker endpoint.";

        DisagreementAnalysis analysis = DisagreementDetector.Compare(worker, judge);

        // "config", "baseurl", "inference", "worker", "endpoint", "file", "setting" should all be shared.
        Assert.That(analysis.KeyEntityAgreement, Is.Not.Empty);
        Assert.That(analysis.KeyEntityAgreement, Contains.Item("baseurl").Or.Contains("inference"));
    }

    [Test]
    public void Compare_OneWordVsParagraph_FlagsLengthRatio()
    {
        string worker = "yes";
        string judge = "Yes, the request should succeed because the configured endpoint is reachable, the circuit breaker is closed, and the operator has provided a valid model tag; we have seen consistent sub-second latency in the last fifty chat turns.";

        DisagreementAnalysis analysis = DisagreementDetector.Compare(worker, judge);

        Assert.That(analysis.LengthRatio, Is.LessThan(0.1),
            "A one-word reply against a paragraph should have a very low length ratio.");
    }

    [Test]
    public void Compare_OutputShapeIsStable()
    {
        // Repeated calls with the same input always return the same
        // verdict + scores so downstream dashboards / evidence files
        // don't flap between ticks.
        string worker = "The inference circuit breaker opened after five consecutive failures.";
        string judge = "Five consecutive failures tripped the circuit breaker.";

        DisagreementAnalysis first = DisagreementDetector.Compare(worker, judge);
        DisagreementAnalysis second = DisagreementDetector.Compare(worker, judge);

        Assert.That(second.Verdict, Is.EqualTo(first.Verdict));
        Assert.That(second.CombinedScore, Is.EqualTo(first.CombinedScore));
    }

    [Test]
    public void SummaryLine_FormatsAllNumbersInvariantCulture()
    {
        DisagreementAnalysis analysis = DisagreementDetector.Compare(
            "The endpoint failed.",
            "The endpoint succeeded.");

        string line = analysis.ToSummaryLine();

        Assert.That(line, Does.StartWith("disagreement verdict="));
        // Invariant culture uses '.' for decimals — never ',' — so
        // log lines parse consistently in any locale.
        Assert.That(line, Does.Not.Contain(","));
    }
}
