using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace PalLLM.Sidecar;

internal static class HttpRequestBodyReadLimiter
{
    public static void TrySetMaxRequestBodySize(HttpRequest request, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(request);

        long effectiveMaxBytes = NormalizeMaxBytes(maxBytes);
        IHttpMaxRequestBodySizeFeature? feature = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly)
        {
            return;
        }

        long? currentLimit = feature.MaxRequestBodySize;
        if (currentLimit is null || currentLimit > effectiveMaxBytes)
        {
            feature.MaxRequestBodySize = effectiveMaxBytes;
        }
    }

    public static async Task<BoundedTextReadResult> ReadUtf8Async(
        HttpRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        int effectiveMaxBytes = (int)NormalizeMaxBytes(maxBytes);
        long? declaredLength = request.ContentLength;
        if (declaredLength is > 0 && declaredLength > effectiveMaxBytes)
        {
            return new BoundedTextReadResult(ExceededLimit: true, Text: string.Empty);
        }

        if (declaredLength == 0)
        {
            return new BoundedTextReadResult(ExceededLimit: false, Text: string.Empty);
        }

        PipeReader reader = request.BodyReader;
        var buffer = declaredLength is > 0 and <= int.MaxValue
            ? new ArrayBufferWriter<byte>((int)declaredLength.Value)
            : new ArrayBufferWriter<byte>(Math.Min(effectiveMaxBytes, 16 * 1024));
        int totalBytes = 0;

        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlySequence<byte> sequence = result.Buffer;
            long remainingBytes = effectiveMaxBytes - totalBytes;
            if (sequence.Length > remainingBytes)
            {
                reader.AdvanceTo(sequence.Start, sequence.End);
                return new BoundedTextReadResult(ExceededLimit: true, Text: string.Empty);
            }

            foreach (ReadOnlyMemory<byte> segment in sequence)
            {
                buffer.Write(segment.Span);
            }

            totalBytes += checked((int)sequence.Length);
            reader.AdvanceTo(sequence.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        ReadOnlySpan<byte> written = buffer.WrittenSpan;
        if (written.Length >= 3
            && written[0] == 0xEF
            && written[1] == 0xBB
            && written[2] == 0xBF)
        {
            written = written[3..];
        }

        return new BoundedTextReadResult(
            ExceededLimit: false,
            Text: Encoding.UTF8.GetString(written));
    }

    private static long NormalizeMaxBytes(long maxBytes) => Math.Max(1_024, maxBytes);

    public readonly record struct BoundedTextReadResult(bool ExceededLimit, string Text);
}
