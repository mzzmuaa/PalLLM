using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

// Pass 218 - direct unit tests for the upstream-body size limiter that
// protects PalLLM from oversized or hostile model-server responses.
//
// HttpContentReadLimiter is the boundary between the sidecar and ANY remote
// inference endpoint (Ollama, llama.cpp, vLLM, LM Studio, transformers
// serve, TensorRT-LLM, OpenVINO, Foundry Local). If a misconfigured or
// compromised upstream returned an unbounded body, PalLLM's worker would
// allocate without limit. The limiter caps every text/bytes/JSON read to
// the per-lane configured cap and surfaces a deterministic
// `InvalidDataException` or `BoundedTextReadResult.ExceededLimit=true`.
//
// Until this pass the limiter was only covered indirectly through
// `InferenceClient` and `VisionClient` integration paths (drift-gate text
// assertions in MetaTests pin the source shape, but no test exercised
// the actual size-cap branches). This file pins each declared-length and
// streamed-body branch with a focused fast unit test.
public sealed class HttpContentReadLimiterTests
{
    private const string ResponseLabel = "Test response body";
    private const int OneKbCap = 1024;

    // ---------- ParseJsonDocumentAsync ----------

    [Test]
    public async Task ParseJsonDocumentAsync_DeclaredLengthWithinCap_ParsesJson()
    {
        using HttpContent content = NewJsonContent(@"{""ok"":true}");

        using JsonDocument document = await HttpContentReadLimiter.ParseJsonDocumentAsync(
            content, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);

        Assert.That(document.RootElement.GetProperty("ok").GetBoolean(), Is.True);
    }

    [Test]
    public void ParseJsonDocumentAsync_DeclaredLengthOverCap_ThrowsWithCapMessage()
    {
        // 256-byte payload but cap is 128 (clamped to 1024 floor by limiter,
        // so use a much smaller cap floor by going through the same
        // NormalizeMaxBytes logic via a 2KB body and a 1024 cap).
        using HttpContent content = NewJsonContent(new string('a', 4096));

        InvalidDataException? ex = Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using JsonDocument _ = await HttpContentReadLimiter.ParseJsonDocumentAsync(
                content, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);
        });

        Assert.That(ex!.Message, Does.Contain("exceeds the configured cap"));
        Assert.That(ex.Message, Does.Contain(ResponseLabel));
    }

    [Test]
    public async Task ParseJsonDocumentAsync_StreamedBodyOverCap_ThrowsAtBoundary()
    {
        // Use a chunked-like content that doesn't pre-declare length.
        using HttpContent content = new UndeclaredLengthContent(new string('a', 4096), "application/json");

        InvalidDataException? ex = Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            using JsonDocument _ = await HttpContentReadLimiter.ParseJsonDocumentAsync(
                content, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);
        });

        Assert.That(ex!.Message, Does.Contain("exceeds the configured cap"));
    }

    // ---------- ReadTextAsync ----------

    [Test]
    public async Task ReadTextAsync_DeclaredLengthZero_ReturnsEmpty()
    {
        using HttpContent content = new ByteArrayContent([]);

        var result = await HttpContentReadLimiter.ReadTextAsync(
            content, maxBytes: OneKbCap, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.EqualTo(string.Empty));
    }

    [Test]
    public async Task ReadTextAsync_DeclaredLengthWithinCap_ReturnsText()
    {
        using HttpContent content = NewTextContent("hello upstream", "utf-8");

        var result = await HttpContentReadLimiter.ReadTextAsync(
            content, maxBytes: OneKbCap, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.EqualTo("hello upstream"));
    }

    [Test]
    public async Task ReadTextAsync_DeclaredLengthOverCap_ReturnsExceededLimit()
    {
        using HttpContent content = NewTextContent(new string('x', 4096), "utf-8");

        var result = await HttpContentReadLimiter.ReadTextAsync(
            content, maxBytes: OneKbCap, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.True);
        Assert.That(result.Text, Is.Empty);
    }

    [Test]
    public async Task ReadTextAsync_StreamedBodyOverCap_ReturnsExceededLimit()
    {
        using HttpContent content = new UndeclaredLengthContent(new string('x', 4096), "text/plain; charset=utf-8");

        var result = await HttpContentReadLimiter.ReadTextAsync(
            content, maxBytes: OneKbCap, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.True);
        Assert.That(result.Text, Is.Empty);
    }

    [Test]
    public async Task ReadTextAsync_NonUtf8Charset_DecodesAccordingToHeader()
    {
        // Latin1 of "café" = c,a,f,0xe9
        byte[] latin1 = [(byte)'c', (byte)'a', (byte)'f', 0xe9];
        var content = new ByteArrayContent(latin1);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "iso-8859-1" };

        var result = await HttpContentReadLimiter.ReadTextAsync(
            content, maxBytes: OneKbCap, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.EqualTo("café"));
    }

    [Test]
    public async Task ReadTextAsync_MalformedCharset_FallsBackToUtf8()
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello"));
        content.Headers.ContentType = new MediaTypeHeaderValue("text/plain") { CharSet = "not-a-real-charset" };

        var result = await HttpContentReadLimiter.ReadTextAsync(
            content, maxBytes: OneKbCap, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.EqualTo("hello"));
    }

    // ---------- ReadBytesAsync ----------

    [Test]
    public async Task ReadBytesAsync_DeclaredLengthZero_ReturnsEmpty()
    {
        using HttpContent content = new ByteArrayContent([]);

        byte[] bytes = await HttpContentReadLimiter.ReadBytesAsync(
            content, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);

        Assert.That(bytes, Is.Empty);
    }

    [Test]
    public async Task ReadBytesAsync_DeclaredLengthWithinCap_ReturnsBytes()
    {
        byte[] payload = [1, 2, 3, 4, 5];
        using HttpContent content = new ByteArrayContent(payload);

        byte[] bytes = await HttpContentReadLimiter.ReadBytesAsync(
            content, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);

        Assert.That(bytes, Is.EqualTo(payload));
    }

    [Test]
    public void ReadBytesAsync_DeclaredLengthOverCap_ThrowsWithCapMessage()
    {
        using HttpContent content = new ByteArrayContent(new byte[4096]);

        InvalidDataException? ex = Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            _ = await HttpContentReadLimiter.ReadBytesAsync(
                content, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);
        });

        Assert.That(ex!.Message, Does.Contain("exceeds the configured cap"));
        Assert.That(ex.Message, Does.Contain(ResponseLabel));
    }

    [Test]
    public void ReadBytesAsync_StreamedBodyOverCap_ThrowsAtBoundary()
    {
        // Undeclared-length content with body larger than the cap. The limiter
        // must not silently truncate; it must throw to surface the cap
        // violation to the caller.
        using HttpContent content = new UndeclaredLengthContent(new string('y', 4096), "application/octet-stream");

        InvalidDataException? ex = Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            _ = await HttpContentReadLimiter.ReadBytesAsync(
                content, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);
        });

        Assert.That(ex!.Message, Does.Contain("exceeds the configured cap"));
    }

    // ---------- BuildExceededLimitMessage ----------

    [Test]
    public void BuildExceededLimitMessage_IncludesLabelAndCap()
    {
        string message = HttpContentReadLimiter.BuildExceededLimitMessage("Vision response", 8192);

        Assert.That(message, Does.Contain("Vision response"));
        Assert.That(message, Does.Contain("8192"));
        Assert.That(message, Does.Contain("cap"));
    }

    [Test]
    public void BuildExceededLimitMessage_WithBelowFloorCap_ClampsToFloor()
    {
        // The limiter clamps maxBytes to >= 1024 (the configured floor). A
        // misconfigured callsite passing 0 or a tiny value must not produce a
        // misleading error about a `0`-byte cap; the message should report the
        // effective cap, which is the floor.
        string message = HttpContentReadLimiter.BuildExceededLimitMessage("X", 0);

        Assert.That(message, Does.Contain("1024"));
    }

    // ---------- Null guards ----------

    [Test]
    public void ParseJsonDocumentAsync_NullContent_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            using JsonDocument _ = await HttpContentReadLimiter.ParseJsonDocumentAsync(
                null!, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);
        });
    }

    [Test]
    public void ReadTextAsync_NullContent_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            _ = await HttpContentReadLimiter.ReadTextAsync(
                null!, maxBytes: OneKbCap, CancellationToken.None);
        });
    }

    [Test]
    public void ReadBytesAsync_NullContent_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
        {
            _ = await HttpContentReadLimiter.ReadBytesAsync(
                null!, maxBytes: OneKbCap, ResponseLabel, CancellationToken.None);
        });
    }

    // ---------- Helpers ----------

    private static HttpContent NewJsonContent(string body)
    {
        var content = new StringContent(body, Encoding.UTF8, "application/json");
        return content;
    }

    private static HttpContent NewTextContent(string body, string charset)
    {
        var content = new StringContent(body, Encoding.GetEncoding(charset), "text/plain");
        return content;
    }

    /// <summary>
    /// HttpContent that does not pre-declare its length, simulating a chunked
    /// or streaming upstream response. The limiter has to enforce the cap by
    /// reading from the stream and tracking bytes — not by short-circuiting
    /// on `ContentLength`.
    /// </summary>
    private sealed class UndeclaredLengthContent : HttpContent
    {
        private readonly byte[] _bytes;

        public UndeclaredLengthContent(string body, string mediaTypeWithCharset)
        {
            _bytes = Encoding.UTF8.GetBytes(body);
            string[] parts = mediaTypeWithCharset.Split(';', 2, StringSplitOptions.TrimEntries);
            var mediaType = new MediaTypeHeaderValue(parts[0]);
            if (parts.Length == 2 && parts[1].StartsWith("charset=", StringComparison.OrdinalIgnoreCase))
            {
                mediaType.CharSet = parts[1]["charset=".Length..];
            }
            Headers.ContentType = mediaType;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
