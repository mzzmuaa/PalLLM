using System.IO;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

public sealed class ProcessTextReadLimiterTests
{
    [Test]
    public async Task ReadAsync_WhenInputExceedsLimit_RetainsPrefixAndMarksTruncated()
    {
        using var reader = new StringReader("abcdefghijk");

        ProcessTextReadLimiter.BoundedTextReadResult result =
            await ProcessTextReadLimiter.ReadAsync(reader, maxChars: 5);

        Assert.That(result.Text, Is.EqualTo("abcde"));
        Assert.That(result.Truncated, Is.True);
    }

    [Test]
    public async Task ReadAsync_WhenCaptureDisabled_DrainsWithoutRetainingText()
    {
        using var reader = new StringReader("thermal stderr noise");

        ProcessTextReadLimiter.BoundedTextReadResult result =
            await ProcessTextReadLimiter.ReadAsync(reader, maxChars: 0);

        Assert.That(result.Text, Is.Empty);
        Assert.That(result.Truncated, Is.True);
        Assert.That(reader.Peek(), Is.EqualTo(-1), "The helper should fully drain redirected process text.");
    }
}
