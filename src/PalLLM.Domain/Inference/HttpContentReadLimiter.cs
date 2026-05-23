using System.Buffers;
using System.IO;
using System.Text;
using System.Text.Json;

namespace PalLLM.Domain.Inference;

internal static class HttpContentReadLimiter
{
    private const string TextResponseLabel = "Response body";

    public static async Task<JsonDocument> ParseJsonDocumentAsync(
        HttpContent content,
        int maxBytes,
        string responseLabel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);
        EnsureContentLengthWithinLimit(content, effectiveMaxBytes, responseLabel);

        await using Stream responseStream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var boundedStream = new BoundedReadStream(responseStream, effectiveMaxBytes, responseLabel);
        return await JsonDocument.ParseAsync(boundedStream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BoundedTextReadResult> ReadTextAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);
        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength is > 0 && declaredLength > effectiveMaxBytes)
        {
            return new BoundedTextReadResult(ExceededLimit: true, Text: string.Empty);
        }

        if (declaredLength == 0)
        {
            return new BoundedTextReadResult(ExceededLimit: false, Text: string.Empty);
        }

        await using Stream responseStream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var boundedStream = new BoundedReadStream(responseStream, effectiveMaxBytes, TextResponseLabel);
        using var reader = new StreamReader(
            boundedStream,
            ResolveEncoding(content),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: GetTextReaderBufferBytes(declaredLength, effectiveMaxBytes),
            leaveOpen: false);

        try
        {
            return new BoundedTextReadResult(
                ExceededLimit: false,
                Text: await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (InvalidDataException)
        {
            return new BoundedTextReadResult(ExceededLimit: true, Text: string.Empty);
        }
    }

    public static async Task<byte[]> ReadBytesAsync(
        HttpContent content,
        int maxBytes,
        string responseLabel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);
        EnsureContentLengthWithinLimit(content, effectiveMaxBytes, responseLabel);

        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength == 0)
        {
            return [];
        }

        await using Stream responseStream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        byte[] rented = ArrayPool<byte>.Shared.Rent(GetInitialBufferBytes(declaredLength, effectiveMaxBytes));
        int totalBytes = 0;

        try
        {
            while (true)
            {
                if (totalBytes == rented.Length)
                {
                    if (rented.Length == effectiveMaxBytes)
                    {
                        if (await HasMoreBytesAsync(responseStream, cancellationToken).ConfigureAwait(false))
                        {
                            throw new InvalidDataException(BuildExceededLimitMessage(responseLabel, effectiveMaxBytes));
                        }

                        break;
                    }

                    int expandedLength = Math.Min(
                        effectiveMaxBytes,
                        Math.Max(rented.Length * 2, totalBytes + 1));
                    byte[] expanded = ArrayPool<byte>.Shared.Rent(expandedLength);
                    Buffer.BlockCopy(rented, 0, expanded, 0, totalBytes);
                    ArrayPool<byte>.Shared.Return(rented);
                    rented = expanded;
                }

                int read = await responseStream.ReadAsync(
                        rented.AsMemory(totalBytes, rented.Length - totalBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
            }

            if (totalBytes == 0)
            {
                return [];
            }

            byte[] bytes = new byte[totalBytes];
            Buffer.BlockCopy(rented, 0, bytes, 0, totalBytes);
            return bytes;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public static string BuildExceededLimitMessage(string responseLabel, int maxBytes) =>
        $"{responseLabel} exceeds the configured cap of {NormalizeMaxBytes(maxBytes)} bytes.";

    private static void EnsureContentLengthWithinLimit(HttpContent content, int maxBytes, string responseLabel)
    {
        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength is > 0 && declaredLength > maxBytes)
        {
            throw new InvalidDataException(BuildExceededLimitMessage(responseLabel, maxBytes));
        }
    }

    private static int NormalizeMaxBytes(int maxBytes) => Math.Max(1_024, maxBytes);

    private static int GetInitialBufferBytes(long? declaredLength, int maxBytes)
    {
        if (declaredLength is > 0 and <= int.MaxValue && declaredLength <= maxBytes)
        {
            return Math.Max(1_024, (int)declaredLength.Value);
        }

        return Math.Min(64 * 1_024, maxBytes);
    }

    private static int GetTextReaderBufferBytes(long? declaredLength, int maxBytes)
    {
        if (declaredLength is > 0 and <= int.MaxValue && declaredLength <= maxBytes)
        {
            return Math.Max(256, Math.Min((int)declaredLength.Value, 4 * 1_024));
        }

        return Math.Min(4 * 1_024, maxBytes);
    }

    private static Encoding ResolveEncoding(HttpContent content)
    {
        if (content.Headers.ContentType?.CharSet is string charset &&
            !string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Fall through to UTF-8.
            }
        }

        return Encoding.UTF8;
    }

    public readonly record struct BoundedTextReadResult(bool ExceededLimit, string Text);

    private static async Task<bool> HasMoreBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] probe = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            return await stream.ReadAsync(probe.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe);
        }
    }

    private sealed class BoundedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _maxBytes;
        private readonly string _responseLabel;
        private int _bytesRead;

        public BoundedReadStream(Stream inner, int maxBytes, string responseLabel)
        {
            _inner = inner;
            _maxBytes = maxBytes;
            _responseLabel = responseLabel;
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
                throw new InvalidDataException(HttpContentReadLimiter.BuildExceededLimitMessage(_responseLabel, _maxBytes));
            }

            return read;
        }
    }
}
