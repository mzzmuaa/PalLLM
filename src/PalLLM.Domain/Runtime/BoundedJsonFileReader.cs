using System.IO;
using System.Text.Json;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Bounded local JSON reader for file-backed runtime artifacts.
/// Opens the file for sequential shared reads, enforces a hard byte cap,
/// and degrades to stable failure categories instead of buffering the whole
/// file into a <see cref="string"/>.
/// </summary>
public static class BoundedJsonFileReader
{
    public static JsonReadResult<T> TryRead<T>(
        string path,
        int maxBytes,
        Func<Stream, T?> read)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(read);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);

        try
        {
            using FileStream stream = OpenRead(path);
            if (stream.Length > effectiveMaxBytes)
            {
                return new JsonReadResult<T>(default, JsonReadFailureCode.Oversized);
            }

            using var boundedStream = new BoundedReadStream(stream, effectiveMaxBytes);
            T? value = read(boundedStream);
            return value is null
                ? new JsonReadResult<T>(default, JsonReadFailureCode.MalformedJson)
                : new JsonReadResult<T>(value, FailureCode: null);
        }
        catch (JsonException)
        {
            return new JsonReadResult<T>(default, JsonReadFailureCode.MalformedJson);
        }
        catch (InvalidDataException)
        {
            return new JsonReadResult<T>(default, JsonReadFailureCode.Oversized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new JsonReadResult<T>(default, JsonReadFailureCode.Unreadable);
        }
    }

    public static JsonDocumentReadResult TryReadDocument(string path, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        JsonReadResult<JsonDocument> result = TryRead(path, maxBytes, stream => JsonDocument.Parse(stream));
        return new JsonDocumentReadResult(result.Value, result.FailureCode);
    }

    public readonly record struct JsonReadResult<T>(
        T? Value,
        JsonReadFailureCode? FailureCode)
    {
        public bool Succeeded => FailureCode is null;
    }

    public readonly record struct JsonDocumentReadResult(
        JsonDocument? Document,
        JsonReadFailureCode? FailureCode)
    {
        public bool Succeeded => FailureCode is null;
    }

    public enum JsonReadFailureCode
    {
        Oversized,
        MalformedJson,
        Unreadable,
    }

    private static FileStream OpenRead(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
        });

    private static int NormalizeMaxBytes(int maxBytes) => Math.Max(1_024, maxBytes);

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxBytes;
        private int _bytesRead;

        public BoundedReadStream(Stream inner, int maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            TrackRead(_inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) =>
            TrackRead(_inner.Read(buffer));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            TrackRead(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            TrackRead(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        private int TrackRead(int read)
        {
            if (read <= 0)
            {
                return read;
            }

            _bytesRead += read;
            if (_bytesRead > _maxBytes)
            {
                throw new InvalidDataException();
            }

            return read;
        }
    }
}
