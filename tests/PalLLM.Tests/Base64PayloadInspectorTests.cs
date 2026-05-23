using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

// Pass 202 - direct unit tests for the allocation-free base64 admission
// inspector. The inspector is on the hot path for every untrusted vision
// payload (chat, vision-describe, world-state, MCP vision tools), and was
// previously only covered indirectly via PalApiRequestValidatorTests.
// Direct tests pin the padding, whitespace, and decoded-size math so a
// regression cannot slip through a 4-line change to PalApiValidation.
public sealed class Base64PayloadInspectorTests
{
    private const int LargeCap = 1024 * 1024;

    // ---------- Valid payloads ----------

    [TestCase("Zm9v", 3)]                  // "foo" - no padding
    [TestCase("Zm9vYmE=", 5)]              // "fooba" - one padding
    [TestCase("Zm9vYg==", 4)]              // "foob" - two padding
    [TestCase("QQ==", 1)]                  // single byte 'A'
    [TestCase("QUJD", 3)]                  // "ABC" - exactly fills 4 chars
    [TestCase("QUJDREVG", 6)]              // "ABCDEF" - longer no padding
    public void Inspect_ValidBase64_AcceptsAndComputesDecodedSize(string payload, int expectedBytes)
    {
        var inspection = Base64PayloadInspector.Inspect(payload, LargeCap);

        Assert.That(inspection.Accepted, Is.True);
        Assert.That(inspection.DecodedBytes, Is.EqualTo(expectedBytes));
        Assert.That(inspection.ErrorCode, Is.Empty);
    }

    // ---------- Whitespace tolerance (matches Convert.FromBase64String) ----------

    [TestCase("Zm 9v")]
    [TestCase("Zm9v\n")]
    [TestCase("Zm9v\r\n")]
    [TestCase("Zm9v\t")]
    [TestCase(" Zm9v ")]
    [TestCase("Zm\t9v\nYg==")]
    public void Inspect_WhitespaceInsidePayload_IsIgnored(string payload)
    {
        var inspection = Base64PayloadInspector.Inspect(payload, LargeCap);

        Assert.That(inspection.Accepted, Is.True);
        Assert.That(inspection.DecodedBytes, Is.GreaterThan(0));
    }

    // ---------- Malformed payloads ----------

    [TestCase("not base64!!!")]            // contains illegal chars `!` `space`
    [TestCase("Zm9vYmF$")]                 // illegal char `$`
    [TestCase("Zm9vYmF<")]                 // illegal char `<`
    [TestCase("Zm-9v")]                    // url-safe `-` not accepted (RFC 4648 std)
    [TestCase("Zm_9v")]                    // url-safe `_` not accepted
    [TestCase("Zm9")]                      // length not multiple of 4
    [TestCase("Zm9vY")]                    // length not multiple of 4
    [TestCase("Zm9vYg=")]                  // length not multiple of 4 even after padding count
    [TestCase("===")]                      // padding only (no useful content prefix)
    [TestCase("Zm9v====")]                 // 4 padding chars - too many
    [TestCase("Zm=9v")]                    // padding interspersed with non-padding
    [TestCase("Zm9=v")]                    // single padding then more content
    [TestCase("=Zm9v")]                    // padding before any useful char
    public void Inspect_MalformedBase64_RejectsWithInvalidBase64(string payload)
    {
        var inspection = Base64PayloadInspector.Inspect(payload, LargeCap);

        Assert.That(inspection.Accepted, Is.False);
        Assert.That(inspection.ErrorCode, Is.EqualTo(Base64PayloadInspector.InvalidBase64));
    }

    // ---------- Empty / whitespace-only ----------

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("\n\n\t")]
    public void Inspect_EmptyOrWhitespaceOnly_RejectsWithInvalidBase64(string payload)
    {
        var inspection = Base64PayloadInspector.Inspect(payload, LargeCap);

        Assert.That(inspection.Accepted, Is.False);
        Assert.That(inspection.ErrorCode, Is.EqualTo(Base64PayloadInspector.InvalidBase64));
    }

    // ---------- Size cap enforcement ----------

    [Test]
    public void Inspect_PayloadAtCap_IsAccepted()
    {
        // "AAAA" decodes to 3 bytes (0x00 0x00 0x00). Cap of exactly 3 must accept.
        var inspection = Base64PayloadInspector.Inspect("AAAA", maxDecodedBytes: 3);

        Assert.That(inspection.Accepted, Is.True);
        Assert.That(inspection.DecodedBytes, Is.EqualTo(3));
    }

    [Test]
    public void Inspect_PayloadOneByteOverCap_IsRejectedWithSizeError()
    {
        // "AAAA" decodes to 3 bytes. Cap of 2 must reject with PayloadTooLarge.
        var inspection = Base64PayloadInspector.Inspect("AAAA", maxDecodedBytes: 2);

        Assert.That(inspection.Accepted, Is.False);
        Assert.That(inspection.ErrorCode, Is.EqualTo(Base64PayloadInspector.PayloadTooLarge));
        Assert.That(inspection.DecodedBytes, Is.EqualTo(3));
    }

    [Test]
    public void Inspect_NonPositiveMaxBytes_IsTreatedAsAtLeastOne()
    {
        // The inspector clamps maxDecodedBytes to >= 1 to avoid a divide/branch
        // surprise when callers pass 0 or negative values from misconfigured options.
        var inspection = Base64PayloadInspector.Inspect("AAAA", maxDecodedBytes: 0);

        Assert.That(inspection.Accepted, Is.False);
        Assert.That(inspection.ErrorCode, Is.EqualTo(Base64PayloadInspector.PayloadTooLarge));
    }

    // ---------- Decoded-size math correctness ----------

    [TestCase("AAAA", 3)]                  // no padding -> 3 bytes
    [TestCase("AAA=", 2)]                  // 1 padding -> 2 bytes
    [TestCase("AA==", 1)]                  // 2 padding -> 1 byte
    [TestCase("AAAAAAAA", 6)]              // 8 chars no padding -> 6 bytes
    [TestCase("AAAAAAA=", 5)]              // 8 chars 1 padding -> 5 bytes
    [TestCase("AAAAAA==", 4)]              // 8 chars 2 padding -> 4 bytes
    public void Inspect_DecodedByteMath_IsExact(string payload, int expectedBytes)
    {
        var inspection = Base64PayloadInspector.Inspect(payload, LargeCap);

        Assert.That(inspection.Accepted, Is.True);
        Assert.That(inspection.DecodedBytes, Is.EqualTo(expectedBytes));
    }

    // ---------- Failure-message helper ----------

    [Test]
    public void BuildImageFailureMessage_InvalidBase64_ReturnsHumanFriendlyMessage()
    {
        var rejection = Base64PayloadInspection.Rejected(Base64PayloadInspector.InvalidBase64);

        string message = Base64PayloadInspector.BuildImageFailureMessage(rejection, LargeCap);

        Assert.That(message, Does.Contain("valid base64"));
    }

    [Test]
    public void BuildImageFailureMessage_PayloadTooLarge_IncludesCapInMessage()
    {
        var rejection = Base64PayloadInspection.Rejected(Base64PayloadInspector.PayloadTooLarge, decodedBytes: 1024);

        string message = Base64PayloadInspector.BuildImageFailureMessage(rejection, maxDecodedBytes: 256);

        Assert.That(message, Does.Contain("256"));
        Assert.That(message, Does.Contain("cap"));
    }

    // ---------- Null guard ----------

    [Test]
    public void Inspect_NullPayload_ThrowsArgumentNullException()
    {
        Assert.That(
            () => Base64PayloadInspector.Inspect(null!, LargeCap),
            Throws.TypeOf<ArgumentNullException>());
    }
}
