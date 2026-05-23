using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

// Pass 304 - direct unit tests for the inbound HTTP request-body size
// limiter. This is the sidecar's INBOUND counterpart to Pass 224's
// HttpContentReadLimiter (which caps outbound model-server responses).
// `HttpRequestBodyReadLimiter` protects /api/* and /mcp endpoints from
// oversized request bodies: it pins the
// `IHttpMaxRequestBodySizeFeature` to the lane-specific cap before
// minimal-API model binding, and it reads UTF-8 text payloads through
// the request `PipeReader` with a hard byte cap while stripping the
// UTF-8 BOM if present.
//
// Until this pass the helper was only covered indirectly through
// `/api/release/readiness` and chat-endpoint fixtures. The 6+
// `TrySetMaxRequestBodySize` branches and the 6+ `ReadUtf8Async`
// branches had no direct fast-feedback coverage. A regression that
// stopped reducing an oversized current limit, or skipped the BOM
// strip, or failed to short-circuit on declared-length-over-cap, would
// have shipped through every existing test green.
public sealed class HttpRequestBodyReadLimiterTests
{
    // ---------- TrySetMaxRequestBodySize ----------

    [Test]
    public void TrySetMaxRequestBodySize_FeatureMissing_IsNoOp()
    {
        // DefaultHttpContext doesn't register IHttpMaxRequestBodySizeFeature
        // by default — the helper must treat the missing feature as a no-op
        // rather than throw.
        HttpRequest request = NewRequest();

        Assert.That(
            () => HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(request, 4096),
            Throws.Nothing);
    }

    [Test]
    public void TrySetMaxRequestBodySize_FeatureReadOnly_IsNoOp()
    {
        // Once a Kestrel request has started reading the body the feature is
        // marked read-only; the helper must respect that without throwing.
        HttpRequest request = NewRequest();
        var feature = new FakeMaxRequestBodySizeFeature { IsReadOnly = true };
        request.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(request, 4096);

        // Read-only feature must NOT be mutated.
        Assert.That(feature.MaxRequestBodySize, Is.Null);
    }

    [Test]
    public void TrySetMaxRequestBodySize_LimitNull_SetsToEffectiveMax()
    {
        HttpRequest request = NewRequest();
        var feature = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = null };
        request.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(request, 8192);

        Assert.That(feature.MaxRequestBodySize, Is.EqualTo(8192));
    }

    [Test]
    public void TrySetMaxRequestBodySize_LimitLargerThanCap_ReducesToCap()
    {
        // The Kestrel default is 30 MiB. PalLLM lane-specific caps are
        // smaller; the helper must REDUCE the limit (never expand it).
        HttpRequest request = NewRequest();
        var feature = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = 30 * 1024 * 1024 };
        request.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(request, 16 * 1024);

        Assert.That(feature.MaxRequestBodySize, Is.EqualTo(16 * 1024));
    }

    [Test]
    public void TrySetMaxRequestBodySize_LimitSmallerThanCap_LeftAlone()
    {
        // If an upstream component already set a tighter limit, the helper
        // must NOT raise it (that would weaken the protection).
        HttpRequest request = NewRequest();
        var feature = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = 2048 };
        request.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(request, 16 * 1024);

        Assert.That(feature.MaxRequestBodySize, Is.EqualTo(2048));
    }

    [Test]
    public void TrySetMaxRequestBodySize_NegativeMaxBytes_ClampedToFloor()
    {
        // `NormalizeMaxBytes` clamps to >= 1024 so a misconfigured 0 or
        // negative cap cannot effectively disable the body-size protection.
        HttpRequest request = NewRequest();
        var feature = new FakeMaxRequestBodySizeFeature { MaxRequestBodySize = null };
        request.HttpContext.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);

        HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(request, -500);

        Assert.That(feature.MaxRequestBodySize, Is.EqualTo(1024));
    }

    [Test]
    public void TrySetMaxRequestBodySize_NullRequest_ThrowsArgumentNullException()
    {
        Assert.That(
            () => HttpRequestBodyReadLimiter.TrySetMaxRequestBodySize(null!, 1024),
            Throws.ArgumentNullException);
    }

    // ---------- ReadUtf8Async ----------

    [Test]
    public async Task ReadUtf8Async_DeclaredLengthWithinCap_ReadsText()
    {
        HttpRequest request = NewRequestWithBody("hello pal");

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, 1024, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.EqualTo("hello pal"));
    }

    [Test]
    public async Task ReadUtf8Async_DeclaredLengthZero_ReturnsEmpty()
    {
        HttpRequest request = NewRequestWithBody(string.Empty);

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, 1024, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.Empty);
    }

    [Test]
    public async Task ReadUtf8Async_DeclaredLengthOverCap_ReturnsExceededLimit()
    {
        // 5000-byte body, 1024 cap. Declared length short-circuit must
        // fire before any bytes are read off the wire.
        HttpRequest request = NewRequestWithBody(new string('x', 5000));

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, 1024, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.True);
        Assert.That(result.Text, Is.Empty);
    }

    [Test]
    public async Task ReadUtf8Async_BomPrefixed_StripsBom()
    {
        // The helper detects the 3-byte UTF-8 BOM (EF BB BF) and strips it
        // before decoding, so JSON binders aren't confused by a stray
        // zero-width character at the start of the request body.
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] body = Encoding.UTF8.GetBytes("after-bom");
        byte[] combined = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, combined, 0, bom.Length);
        Buffer.BlockCopy(body, 0, combined, bom.Length, body.Length);

        HttpRequest request = NewRequestWithBytes(combined);

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, 1024, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.EqualTo("after-bom"));
    }

    [Test]
    public async Task ReadUtf8Async_BareTwoByteBomPrefix_DoesNotStripIncomplete()
    {
        // Only the full 3-byte BOM is stripped. A truncated prefix (2 bytes)
        // is treated as data — important so a coincidental two-byte prefix
        // in legitimate data doesn't accidentally get dropped.
        byte[] body = [0xEF, 0xBB, (byte)'a', (byte)'b'];

        HttpRequest request = NewRequestWithBytes(body);

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, 1024, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        // Encoding.UTF8.GetString folds the malformed `EF BB` prefix into a
        // single replacement character, then decodes "ab" normally — so the
        // result is one replacement + 'a' + 'b' = 3 characters total. The
        // load-bearing assertion is "the bytes were NOT stripped as if they
        // were a BOM" (which would have produced just "ab", length 2).
        Assert.That(result.Text, Does.EndWith("ab"));
        Assert.That(result.Text.Length, Is.EqualTo(3),
            "Incomplete BOM prefix must NOT be stripped; the malformed bytes must remain in the decoded output.");
    }

    [Test]
    public async Task ReadUtf8Async_NoDeclaredLength_ChunkedBodyWithinCap_Reads()
    {
        // When ContentLength is not declared (e.g. chunked transfer), the
        // helper falls back to a streaming read with a 16 KiB starter
        // buffer and the same effective-max cap.
        HttpRequest request = NewRequestWithBytes(Encoding.UTF8.GetBytes("chunked-content"), declareContentLength: false);

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, 1024, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text, Is.EqualTo("chunked-content"));
    }

    [Test]
    public async Task ReadUtf8Async_NoDeclaredLength_StreamedBodyOverCap_ReturnsExceededLimit()
    {
        // Chunked body 5000 bytes, 1024 cap. The streaming branch must
        // surface ExceededLimit and stop reading rather than silently
        // truncate.
        byte[] body = Encoding.UTF8.GetBytes(new string('x', 5000));
        HttpRequest request = NewRequestWithBytes(body, declareContentLength: false);

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, 1024, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.True);
        Assert.That(result.Text, Is.Empty);
    }

    [Test]
    public async Task ReadUtf8Async_NegativeMaxBytes_ClampedToFloor()
    {
        // 800-byte body, cap value 0 → effective cap 1024 (the floor). The
        // 800-byte body fits, so it must be returned.
        HttpRequest request = NewRequestWithBody(new string('y', 800));

        var result = await HttpRequestBodyReadLimiter.ReadUtf8Async(request, maxBytes: 0, CancellationToken.None);

        Assert.That(result.ExceededLimit, Is.False);
        Assert.That(result.Text.Length, Is.EqualTo(800));
    }

    [Test]
    public void ReadUtf8Async_NullRequest_ThrowsArgumentNullException()
    {
        Assert.That(
            async () => await HttpRequestBodyReadLimiter.ReadUtf8Async(null!, 1024, CancellationToken.None),
            Throws.ArgumentNullException);
    }

    // ---------- Helpers ----------

    private static HttpRequest NewRequest()
    {
        var context = new DefaultHttpContext();
        return context.Request;
    }

    private static HttpRequest NewRequestWithBody(string body)
    {
        return NewRequestWithBytes(Encoding.UTF8.GetBytes(body), declareContentLength: true);
    }

    private static HttpRequest NewRequestWithBytes(byte[] body, bool declareContentLength = true)
    {
        var context = new DefaultHttpContext();
        var stream = new MemoryStream(body, writable: false);
        context.Request.Body = stream;
        context.Features.Set<IRequestBodyPipeFeature>(new FakeRequestBodyPipeFeature(PipeReader.Create(stream)));
        if (declareContentLength)
        {
            context.Request.ContentLength = body.Length;
        }
        return context.Request;
    }

    private sealed class FakeMaxRequestBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; set; }
        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class FakeRequestBodyPipeFeature : IRequestBodyPipeFeature
    {
        public FakeRequestBodyPipeFeature(PipeReader reader) { Reader = reader; }
        public PipeReader Reader { get; }
    }
}
