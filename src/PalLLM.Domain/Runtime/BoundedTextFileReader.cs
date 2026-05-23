using System.Text;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Bounded local text-file reader for deterministic runtime probes.
/// Opens the file for sequential shared reads, enforces a hard byte cap,
/// and degrades to stable failure categories instead of buffering an
/// arbitrarily large file into a <see cref="string"/>.
/// </summary>
internal static class BoundedTextFileReader
{
    public static TextReadResult TryRead(string path, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);

        try
        {
            using FileStream stream = OpenRead(path);
            if (CanTreatAsOversized(stream, effectiveMaxBytes))
            {
                return new TextReadResult(Text: null, FailureCode: TextReadFailureCode.Oversized);
            }

            using var boundedStream = new BoundedReadStream(stream, effectiveMaxBytes);
            using var reader = new StreamReader(
                boundedStream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: false);

            return new TextReadResult(reader.ReadToEnd(), FailureCode: null);
        }
        catch (InvalidDataException)
        {
            return new TextReadResult(Text: null, FailureCode: TextReadFailureCode.Oversized);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new TextReadResult(Text: null, FailureCode: TextReadFailureCode.Unreadable);
        }
    }

    internal readonly record struct TextReadResult(
        string? Text,
        TextReadFailureCode? FailureCode)
    {
        public bool Succeeded => FailureCode is null;
    }

    internal enum TextReadFailureCode
    {
        Oversized,
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

    private static bool CanTreatAsOversized(FileStream stream, int maxBytes)
    {
        try
        {
            return stream.CanSeek && stream.Length > maxBytes;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            return false;
        }
    }

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
