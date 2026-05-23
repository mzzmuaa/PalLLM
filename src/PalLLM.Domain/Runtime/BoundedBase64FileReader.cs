using System.Buffers;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Bounded local binary-file reader for file-backed image handoff surfaces that
/// need a base64 transport payload. Opens the file for sequential shared reads,
/// enforces a hard byte cap, rents buffers from <see cref="ArrayPool{T}"/>, and
/// degrades to stable failure categories instead of allocating a fresh byte
/// array per read.
/// </summary>
internal static class BoundedBase64FileReader
{
    public static async Task<Base64ReadResult> TryReadAsync(
        string path,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);

        try
        {
            await using FileStream stream = OpenRead(path);
            if (CanTreatAsOversized(stream, effectiveMaxBytes))
            {
                return new Base64ReadResult(Base64: null, BytesRead: 0, Base64ReadFailureCode.Oversized);
            }

            BufferReadResult bufferReadResult = await ReadIntoBufferAsync(
                    stream,
                    GetInitialBufferBytes(stream, effectiveMaxBytes),
                    effectiveMaxBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                int bytesRead = bufferReadResult.BytesRead;
                if (bytesRead == 0)
                {
                    return new Base64ReadResult(Base64: null, BytesRead: 0, Base64ReadFailureCode.Empty);
                }

                return new Base64ReadResult(
                    Convert.ToBase64String(bufferReadResult.Buffer.AsSpan(0, bytesRead)),
                    bytesRead,
                    FailureCode: null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bufferReadResult.Buffer, clearArray: false);
            }
        }
        catch (InvalidDataException)
        {
            return new Base64ReadResult(Base64: null, BytesRead: 0, Base64ReadFailureCode.Oversized);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Base64ReadResult(Base64: null, BytesRead: 0, Base64ReadFailureCode.Unreadable);
        }
    }

    internal readonly record struct Base64ReadResult(
        string? Base64,
        int BytesRead,
        Base64ReadFailureCode? FailureCode)
    {
        public bool Succeeded => FailureCode is null;
    }

    internal enum Base64ReadFailureCode
    {
        Oversized,
        Empty,
        Unreadable,
    }

    private static FileStream OpenRead(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan | FileOptions.Asynchronous,
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

    private static int GetInitialBufferBytes(FileStream stream, int maxBytes)
    {
        try
        {
            if (stream.CanSeek &&
                stream.Length > 0 &&
                stream.Length <= maxBytes &&
                stream.Length <= int.MaxValue)
            {
                return Math.Max(1_024, (int)stream.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            // Fall back to the configured hard cap.
        }

        return maxBytes;
    }

    private static async Task<BufferReadResult> ReadIntoBufferAsync(
        FileStream stream,
        int initialBufferBytes,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(initialBufferBytes);
        int totalBytes = 0;

        try
        {
            while (true)
            {
                if (totalBytes == buffer.Length)
                {
                    if (buffer.Length == maxBytes)
                    {
                        if (await HasMoreBytesAsync(stream, cancellationToken).ConfigureAwait(false))
                        {
                            throw new InvalidDataException();
                        }

                        break;
                    }

                    int expandedLength = Math.Min(maxBytes, Math.Max(buffer.Length * 2, totalBytes + 1));
                    byte[] expanded = ArrayPool<byte>.Shared.Rent(expandedLength);
                    Buffer.BlockCopy(buffer, 0, expanded, 0, totalBytes);
                    ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
                    buffer = expanded;
                }

                int read = await stream.ReadAsync(
                        buffer.AsMemory(totalBytes, buffer.Length - totalBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalBytes += read;
            }

            return new BufferReadResult(buffer, totalBytes);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
            throw;
        }
    }

    private static async Task<bool> HasMoreBytesAsync(FileStream stream, CancellationToken cancellationToken)
    {
        byte[] probe = ArrayPool<byte>.Shared.Rent(1);
        try
        {
            return await stream.ReadAsync(probe.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) > 0;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(probe, clearArray: false);
        }
    }

    private readonly record struct BufferReadResult(byte[] Buffer, int BytesRead);
}
