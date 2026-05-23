using System.Text;
using System.Text.Json;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

// Pass 225 - direct unit tests for the bounded persisted-JSON file reader
// that protects every runtime read of untrusted on-disk artifacts (lifetime
// relationship state, ui-probe results, release-evidence artifacts, pack
// manifests, native-proof snapshots). If a file is corrupted, oversized,
// or unreadable, the reader must return a stable failure code instead of
// throwing, allocating the whole file into memory, or partially
// deserialising into a half-initialised object.
//
// Until this pass the reader was only covered indirectly by callers
// (PalLlmRuntime, NarrativePackService, PersonalityPack) plus drift-gate
// text assertions in MetaTests that pinned the callsite shape. No test
// exercised the actual failure-code branches at runtime. A regression
// that, say, started swallowing MalformedJson as a successful read would
// have shipped through every existing test green.
public sealed class BoundedJsonFileReaderTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests.BoundedJson", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ---------- TryRead<T> happy path ----------

    [Test]
    public void TryRead_ValidJsonWithinCap_ReturnsDeserialisedValue()
    {
        string path = WriteFile("ok.json", @"{""name"":""pal"",""level"":42}");

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: 64 * 1024,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.FailureCode, Is.Null);
        Assert.That(result.Value, Is.Not.Null);
        Assert.That(result.Value!.Name, Is.EqualTo("pal"));
        Assert.That(result.Value.Level, Is.EqualTo(42));
    }

    [Test]
    public void TryRead_ExactlyAtCap_IsAccepted()
    {
        // Build a payload whose file size equals the cap exactly.
        string padded = new('a', 1010); // adjust below to land on the cap
        string body = $@"{{""name"":""{padded}"",""level"":0}}";
        string path = WriteFile("at-cap.json", body);
        int cap = (int)new FileInfo(path).Length;

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: cap,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Value, Is.Not.Null);
    }

    // ---------- Oversized ----------

    [Test]
    public void TryRead_FileLargerThanCap_ReturnsOversizedFailure()
    {
        string padded = new('a', 8192);
        string path = WriteFile("oversized.json", $@"{{""name"":""{padded}"",""level"":0}}");

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: 1024,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.Oversized));
        Assert.That(result.Value, Is.Null);
    }

    [Test]
    public void TryRead_NonPositiveMaxBytes_ClampsToFloor()
    {
        // The reader clamps maxBytes to >= 1024 (the configured floor). A
        // small valid payload that fits the floor should still be accepted
        // even when the caller passed 0 or a tiny value.
        string path = WriteFile("small.json", @"{""name"":""x"",""level"":1}");

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: 0,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Value!.Name, Is.EqualTo("x"));
    }

    // ---------- MalformedJson ----------

    [Test]
    public void TryRead_MalformedJsonInsideCap_ReturnsMalformedFailure()
    {
        string path = WriteFile("malformed.json", @"{""name"":""pal"",""level"": NOT-A-NUMBER}");

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: 64 * 1024,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.MalformedJson));
        Assert.That(result.Value, Is.Null);
    }

    [Test]
    public void TryRead_EmptyFile_ReturnsMalformedFailure()
    {
        string path = WriteFile("empty.json", string.Empty);

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: 64 * 1024,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.MalformedJson));
    }

    [Test]
    public void TryRead_ReadFunctionReturnsNull_TreatedAsMalformed()
    {
        // A read function that decodes to literal `null` JSON is treated as
        // a malformed read from the runtime's perspective — null values
        // cannot satisfy downstream consumers.
        string path = WriteFile("null-value.json", "null");

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: 64 * 1024,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.MalformedJson));
    }

    // ---------- Unreadable ----------

    [Test]
    public void TryRead_MissingFile_ReturnsUnreadableFailure()
    {
        string path = Path.Combine(_root, "does-not-exist.json");

        var result = BoundedJsonFileReader.TryRead(
            path,
            maxBytes: 64 * 1024,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.Unreadable));
    }

    [Test]
    public void TryRead_PathPointsToDirectory_ReturnsUnreadableFailure()
    {
        // Opening a directory as a file surfaces as an IOException /
        // UnauthorizedAccessException family on Windows, both of which the
        // reader maps to Unreadable.
        var result = BoundedJsonFileReader.TryRead(
            _root, // directory, not a file
            maxBytes: 64 * 1024,
            stream => JsonSerializer.Deserialize<SamplePayload>(stream, JsonOptions));

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.Unreadable));
    }

    // ---------- TryReadDocument ----------

    [Test]
    public void TryReadDocument_ValidJson_ReturnsDocument()
    {
        string path = WriteFile("doc.json", @"{""ok"":true}");

        var result = BoundedJsonFileReader.TryReadDocument(path, maxBytes: 64 * 1024);

        Assert.That(result.Succeeded, Is.True);
        Assert.That(result.Document, Is.Not.Null);
        Assert.That(result.Document!.RootElement.GetProperty("ok").GetBoolean(), Is.True);
        result.Document.Dispose();
    }

    [Test]
    public void TryReadDocument_Oversized_ReturnsOversizedFailure()
    {
        string padded = new('z', 8192);
        string path = WriteFile("doc-oversized.json", $@"{{""payload"":""{padded}""}}");

        var result = BoundedJsonFileReader.TryReadDocument(path, maxBytes: 1024);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.Oversized));
    }

    [Test]
    public void TryReadDocument_Malformed_ReturnsMalformedFailure()
    {
        string path = WriteFile("doc-malformed.json", "{not-json}");

        var result = BoundedJsonFileReader.TryReadDocument(path, maxBytes: 64 * 1024);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.MalformedJson));
    }

    [Test]
    public void TryReadDocument_Missing_ReturnsUnreadableFailure()
    {
        string path = Path.Combine(_root, "absent.json");

        var result = BoundedJsonFileReader.TryReadDocument(path, maxBytes: 64 * 1024);

        Assert.That(result.Succeeded, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo(BoundedJsonFileReader.JsonReadFailureCode.Unreadable));
    }

    // ---------- Null guards ----------

    [Test]
    public void TryRead_NullPath_ThrowsArgumentNullException()
    {
        Assert.That(
            () => BoundedJsonFileReader.TryRead<SamplePayload>(
                null!,
                maxBytes: 1024,
                stream => null),
            Throws.ArgumentNullException);
    }

    [Test]
    public void TryRead_NullReadFunction_ThrowsArgumentNullException()
    {
        string path = WriteFile("any.json", "{}");

        Assert.That(
            () => BoundedJsonFileReader.TryRead<SamplePayload>(
                path,
                maxBytes: 1024,
                read: null!),
            Throws.ArgumentNullException);
    }

    [Test]
    public void TryReadDocument_NullPath_ThrowsArgumentNullException()
    {
        Assert.That(
            () => BoundedJsonFileReader.TryReadDocument(null!, maxBytes: 1024),
            Throws.ArgumentNullException);
    }

    // ---------- Helpers ----------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private string WriteFile(string name, string contents)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, contents, Encoding.UTF8);
        return path;
    }

    private sealed record SamplePayload
    {
        public string Name { get; init; } = string.Empty;
        public int Level { get; init; }
    }
}
