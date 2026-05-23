using System.Text;

namespace PalLLM.Domain.Runtime;

internal static class ProcessTextReadLimiter
{
    public static Task<BoundedTextReadResult> ReadAsync(
        TextReader? reader,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        if (reader is null)
        {
            return Task.FromResult(new BoundedTextReadResult(Text: string.Empty, Truncated: false));
        }

        if (maxChars <= 0)
        {
            return DrainWithoutCaptureAsync(reader, cancellationToken);
        }

        return ReadCoreAsync(reader, maxChars, cancellationToken);
    }

    private static async Task<BoundedTextReadResult> ReadCoreAsync(
        TextReader reader,
        int maxChars,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 512));
        char[] buffer = new char[Math.Clamp(maxChars, 64, 512)];
        bool truncated = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            int remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                int copy = Math.Min(read, remaining);
                builder.Append(buffer, 0, copy);
                if (copy < read)
                {
                    truncated = true;
                }
            }
            else
            {
                truncated = true;
            }
        }

        return new BoundedTextReadResult(builder.ToString(), truncated);
    }

    private static async Task<BoundedTextReadResult> DrainWithoutCaptureAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        char[] buffer = new char[256];
        while (await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false) > 0)
        {
        }

        return new BoundedTextReadResult(Text: string.Empty, Truncated: true);
    }

    public readonly record struct BoundedTextReadResult(string Text, bool Truncated);
}
