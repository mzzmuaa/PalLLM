using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

// Pass 249 - direct unit tests for the bounded pooled-buffer base64 file reader
// that protects the screenshot-ingress path. Every player-provided screenshot
// flows through this reader before its bytes become an OpenAI-compatible
// base64 data-URL payload sent to vision endpoints. If a file is oversized,
// empty, or unreadable, the reader must return a stable failure code instead
// of allocating an unbounded byte[], leaking pooled buffers, or partially
// decoding a half-image.
//
// Until this pass the reader was only covered indirectly through
// vision-orchestrator integration paths plus drift-gate text assertions in
// MetaTests that pinned the callsite shape. No test exercised the actual
// failure-code branches at runtime. A regression that, say, started returning
// a successful `Base64ReadResult` with a null `Base64` string would have
// shipped through every existing test green.
public sealed class BoundedBase64FileReaderTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests.BoundedBase64", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ---------- Happy path ----------

    [Test]
    public async Task TryReadAsync_ValidFileWithinCap_ReturnsBase64()
    {
        byte[] body = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]; // PNG magic
        string path = WriteBinary("ok.png", body);

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 64 * 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.FailureCode, Is.Null);
        Assert.That(result.BytesRead, Is.EqualTo(body.Length));
        Assert.That(result.Base64, Is.EqualTo(Convert.ToBase64String(body)));
    }

    [Test]
    public async Task TryReadAsync_ExactlyAtCap_IsAccepted()
    {
        byte[] body = new byte[1024];
        for (int i = 0; i < body.Length; i++) { body[i] = (byte)(i % 256); }
        string path = WriteBinary("at-cap.bin", body);

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.BytesRead, Is.EqualTo(1024));
        Assert.That(result.Base64, Is.Not.Null);
    }

    [Test]
    public async Task TryReadAsync_NonPositiveMaxBytes_ClampsToFloor()
    {
        // The reader clamps maxBytes to >= 1024. A small valid payload that
        // fits the floor should still be accepted even when the caller passed
        // 0 or a tiny value (e.g. a misconfigured option).
        byte[] body = [1, 2, 3, 4];
        string path = WriteBinary("small.bin", body);

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 0, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.BytesRead, Is.EqualTo(body.Length));
    }

    // ---------- Oversized ----------

    [Test]
    public async Task TryReadAsync_FileLargerThanCap_ReturnsOversizedFailure()
    {
        byte[] body = new byte[8192];
        string path = WriteBinary("oversized.bin", body);

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedBase64FileReader.Base64ReadFailureCode.Oversized));
        Assert.That(result.Base64, Is.Null);
        Assert.That(result.BytesRead, Is.EqualTo(0));
    }

    [Test]
    public async Task TryReadAsync_FileOneByteOverCap_RejectsAsOversized()
    {
        byte[] body = new byte[1025];
        string path = WriteBinary("boundary.bin", body);

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedBase64FileReader.Base64ReadFailureCode.Oversized));
    }

    // ---------- Empty ----------

    [Test]
    public async Task TryReadAsync_EmptyFile_ReturnsEmptyFailure()
    {
        string path = WriteBinary("empty.bin", []);

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedBase64FileReader.Base64ReadFailureCode.Empty));
        Assert.That(result.Base64, Is.Null);
        Assert.That(result.BytesRead, Is.EqualTo(0));
    }

    // ---------- Unreadable ----------

    [Test]
    public async Task TryReadAsync_MissingFile_ReturnsUnreadableFailure()
    {
        string path = Path.Combine(_root, "does-not-exist.bin");

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedBase64FileReader.Base64ReadFailureCode.Unreadable));
        Assert.That(result.Base64, Is.Null);
    }

    [Test]
    public async Task TryReadAsync_PathPointsToDirectory_ReturnsUnreadableFailure()
    {
        // Opening a directory as a file surfaces as IOException /
        // UnauthorizedAccessException on Windows; both map to Unreadable.
        var result = await BoundedBase64FileReader.TryReadAsync(
            _root, maxBytes: 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedBase64FileReader.Base64ReadFailureCode.Unreadable));
    }

    // ---------- Cancellation ----------

    [Test]
    public void TryReadAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        // OperationCanceledException is explicitly re-thrown by the reader's
        // catch chain (it is NOT mapped to a failure code) because callers
        // expect cancellation to propagate so the worker can shut down
        // promptly. Use a non-trivial-sized file so the read actually starts.
        byte[] body = new byte[4096];
        string path = WriteBinary("cancel.bin", body);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.That(
            async () => await BoundedBase64FileReader.TryReadAsync(path, maxBytes: 64 * 1024, cts.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    // ---------- Null guard ----------

    [Test]
    public void TryReadAsync_NullPath_ThrowsArgumentNullException()
    {
        Assert.That(
            async () => await BoundedBase64FileReader.TryReadAsync(null!, maxBytes: 1024, CancellationToken.None),
            Throws.ArgumentNullException);
    }

    // ---------- Result shape ----------

    [Test]
    public async Task TryReadAsync_SuccessResult_BytesReadMatchesBase64Length()
    {
        // BytesRead is the decoded byte count; the base64 string is ceil(N/3)*4
        // chars. The result shape is load-bearing because callers use BytesRead
        // for metrics and the Base64 string as the OpenAI vision payload.
        byte[] body = new byte[100]; // 100 bytes -> ceil(100/3)*4 = 136 base64 chars
        for (int i = 0; i < body.Length; i++) { body[i] = (byte)i; }
        string path = WriteBinary("shape.bin", body);

        var result = await BoundedBase64FileReader.TryReadAsync(
            path, maxBytes: 1024, CancellationToken.None);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.BytesRead, Is.EqualTo(100));
        Assert.That(result.Base64!.Length, Is.EqualTo(136));
        // Round-trip check: decoding the base64 reproduces the original bytes.
        Assert.That(Convert.FromBase64String(result.Base64), Is.EqualTo(body));
    }

    // ---------- Helpers ----------

    private string WriteBinary(string name, byte[] contents)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllBytes(path, contents);
        return path;
    }
}
