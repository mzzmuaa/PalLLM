namespace PalLLM.Domain.Runtime;

/// <summary>
/// Allocation-free base64 shape and decoded-size inspector for caller-supplied
/// media payloads. It accepts the same ASCII whitespace that
/// <see cref="Convert.FromBase64String(string)"/> permits, rejects malformed
/// padding early, and computes decoded bytes without materializing the image.
/// </summary>
public static class Base64PayloadInspector
{
    public const string InvalidBase64 = "invalid_base64";

    public const string PayloadTooLarge = "payload_too_large";

    public static Base64PayloadInspection Inspect(string payload, int maxDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(payload);

        int effectiveMaxBytes = Math.Max(1, maxDecodedBytes);
        int usefulChars = 0;
        int paddingChars = 0;
        bool seenPadding = false;

        foreach (char c in payload)
        {
            if (IsAsciiWhitespace(c))
            {
                continue;
            }

            if (c == '=')
            {
                seenPadding = true;
                paddingChars++;
                if (paddingChars > 2)
                {
                    return Base64PayloadInspection.Rejected(InvalidBase64);
                }

                usefulChars++;
                continue;
            }

            if (seenPadding || !IsBase64Character(c))
            {
                return Base64PayloadInspection.Rejected(InvalidBase64);
            }

            usefulChars++;
        }

        if (usefulChars == 0 || usefulChars % 4 != 0)
        {
            return Base64PayloadInspection.Rejected(InvalidBase64);
        }

        long decodedBytes = ((long)usefulChars / 4 * 3) - paddingChars;
        if (decodedBytes <= 0 || decodedBytes > int.MaxValue)
        {
            return Base64PayloadInspection.Rejected(InvalidBase64);
        }

        if (decodedBytes > effectiveMaxBytes)
        {
            return Base64PayloadInspection.Rejected(PayloadTooLarge, (int)decodedBytes);
        }

        return Base64PayloadInspection.Allowed((int)decodedBytes);
    }

    public static string BuildImageFailureMessage(Base64PayloadInspection inspection, int maxDecodedBytes) =>
        inspection.ErrorCode switch
        {
            InvalidBase64 => "ImageBase64 must be valid base64 image data.",
            PayloadTooLarge => $"Image payload exceeds the configured cap of {Math.Max(1, maxDecodedBytes)} bytes.",
            _ => "Image payload could not be accepted.",
        };

    public static string BuildAudioFailureMessage(Base64PayloadInspection inspection, int maxDecodedBytes) =>
        inspection.ErrorCode switch
        {
            InvalidBase64 => "AudioBase64 must be valid base64 audio data.",
            PayloadTooLarge => $"Audio payload exceeds the configured cap of {Math.Max(1, maxDecodedBytes)} bytes.",
            _ => "Audio payload could not be accepted.",
        };

    private static bool IsAsciiWhitespace(char c) =>
        c is ' ' or '\t' or '\r' or '\n';

    private static bool IsBase64Character(char c) =>
        c is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '+'
            or '/';
}

public readonly record struct Base64PayloadInspection(
    bool Accepted,
    int DecodedBytes,
    string ErrorCode)
{
    public static Base64PayloadInspection Allowed(int decodedBytes) =>
        new(true, decodedBytes, string.Empty);

    public static Base64PayloadInspection Rejected(string errorCode, int decodedBytes = 0) =>
        new(false, decodedBytes, errorCode);
}
