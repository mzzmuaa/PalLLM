using System.Text;
using System.Text.Json;
using PalLLM.Domain.Integration;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Static helpers + small private utility methods shared by the
//            other PalLlmRuntime.*.cs partials. Endpointing math
//            (NormalizeEndpointingMs, SumEndpointingMs), bridge-directory
//            bookkeeping caps, file-sort + budget clamps. Pure functions;
//            no I/O, no state.
//   surface: PalLlmRuntime.NormalizeEndpointingMs,
//            PalLlmRuntime.SumEndpointingMs,
//            PalLlmRuntime.ClampPositiveBudget, the
//            DirectoryActivityCountCap constant. All internal/private.
//   gate:    Covered transitively by every PalLlmRuntime fixture that calls
//            a method using these helpers; pinned directly by
//            tests/PalLLM.Tests/RuntimeTests.cs.
//   adr:     None directly.
//   docs:    docs/CODE_MAP.md, docs/REFACTORING_ROADMAP.md (Phase 1a).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    private const int DirectoryActivityCountCap = 1_024;

    private static int? NormalizeEndpointingMs(int? value) =>
        value is null ? null : Math.Max(0, value.Value);

    private static int? SumEndpointingMs(params int?[] values)
    {
        long total = 0;
        bool any = false;
        foreach (int? value in values)
        {
            if (value is not { } actual)
            {
                continue;
            }

            any = true;
            total += actual;
        }

        return any ? (int)Math.Min(total, int.MaxValue) : null;
    }

    private static string NormalizeEndpointReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "not_supplied";
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 64)];
        int length = 0;
        foreach (char character in value.Trim())
        {
            if (length >= buffer.Length)
            {
                break;
            }

            if (!char.IsControl(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return length == 0 ? "not_supplied" : new string(buffer[..length]);
    }

    private static string MimeToExtension(string mime) => NormalizeMimeTypeForRouting(mime) switch
    {
        "audio/mpeg" => ".mp3",
        "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "audio/flac" => ".flac",
        "audio/pcm" => ".pcm",
        "audio/l16" => ".pcm",
        "audio/mp4" => ".m4a",
        "audio/x-m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/wma" => ".wma",
        "audio/x-ms-wma" => ".wma",
        _ => ".wav",
    };

    private static string DetermineSpeechPlaybackHint(string mime, string? filePath)
    {
        string normalizedMime = NormalizeMimeTypeForRouting(mime);
        string extension = Path.GetExtension(filePath ?? string.Empty).Trim().ToLowerInvariant();

        if (extension == ".wav"
            || normalizedMime is "audio/wav" or "audio/wave" or "audio/x-wav")
        {
            return "sound_player";
        }

        if (extension is ".pcm"
            || normalizedMime is "audio/pcm" or "audio/l16")
        {
            return "raw_pcm";
        }

        if (extension is ".mp3" or ".m4a" or ".aac" or ".wma" or ".ogg" or ".opus" or ".flac"
            || normalizedMime is "audio/mpeg" or "audio/mp3" or "audio/mp4" or "audio/x-m4a" or "audio/aac" or "audio/wma" or "audio/x-ms-wma" or "audio/ogg" or "audio/opus" or "audio/flac")
        {
            return "media_player";
        }

        return "unknown";
    }

    private static int CountFiles(string directory, params string[] patterns)
    {
        int total = 0;
        foreach (string pattern in patterns)
        {
            int remainingBudget = DirectoryActivityCountCap - total;
            if (remainingBudget <= 0)
            {
                return DirectoryActivityCountCap;
            }

            total += CountFiles(directory, pattern, remainingBudget);
        }

        return total;
    }

    private static int CountFiles(string directory, string pattern, int maxFiles)
    {
        if (maxFiles <= 0 || !Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            int count = 0;
            foreach (string _ in Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly))
            {
                count++;
                if (count >= maxFiles)
                {
                    return maxFiles;
                }
            }

            return count;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool ContainsAny(string value, params string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string NormalizeMimeTypeForRouting(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> trimmed = mime.AsSpan().Trim();
        int separatorIndex = trimmed.IndexOf(';');
        if (separatorIndex >= 0)
        {
            trimmed = trimmed[..separatorIndex].TrimEnd();
        }

        return trimmed.ToString().ToLowerInvariant();
    }

    private static int ClampPositiveBudget(int value) =>
        value <= 0 ? 0 : value;

    private static string DescribeScreenshotReadFailure(
        BoundedBase64FileReader.Base64ReadFailureCode? failureCode,
        int maxBytes) =>
        failureCode switch
        {
            BoundedBase64FileReader.Base64ReadFailureCode.Oversized =>
                $"exceeded the configured cap of {Math.Max(1_024, maxBytes)} bytes while being read.",
            BoundedBase64FileReader.Base64ReadFailureCode.Empty =>
                "was empty when the bounded reader opened it.",
            _ =>
                "could not be read through the bounded sequential reader.",
        };

    private static string DescribeScreenshotProcessingFailure(Exception exception) =>
        exception switch
        {
            JsonException => "vision output JSON could not be applied.",
            IOException or UnauthorizedAccessException => "local screenshot file handling failed.",
            ArgumentException or FormatException or InvalidOperationException or NotSupportedException =>
                "vision output could not be applied.",
            _ => "runtime screenshot handling failed.",
        };

    private static string DescribeBridgeInboxReadFailure(
        BoundedJsonFileReader.JsonReadFailureCode? failureCode,
        int maxBytes) =>
        failureCode switch
        {
            BoundedJsonFileReader.JsonReadFailureCode.Oversized =>
                $"bridge inbox event exceeded the configured cap of {Math.Max(1_024, maxBytes)} bytes.",
            BoundedJsonFileReader.JsonReadFailureCode.Unreadable =>
                "bridge inbox event could not be read.",
            _ =>
                "bridge inbox event JSON was malformed.",
        };

    private static string DescribeOutboxEntryDeleteFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "access was denied while deleting the file.",
            _ => "the file could not be deleted.",
        };

    private static string DescribeOutboxEnumerationFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "outbox directory access was denied.",
            _ => "outbox directory could not be enumerated.",
        };

    private static string DescribeOutboxWriteFailure(Exception exception) =>
        exception switch
        {
            DirectoryNotFoundException => "reply envelope directory was missing.",
            PathTooLongException => "reply envelope path exceeded platform limits.",
            UnauthorizedAccessException => "reply envelope access was denied.",
            _ => "reply envelope could not be written.",
        };

    private static string DescribeBridgeProcessingFailure(Exception exception) =>
        exception switch
        {
            JsonException => "bridge event payload was invalid for its declared type.",
            IOException or UnauthorizedAccessException => "bridge event archive handling failed.",
            ArgumentException or FormatException or InvalidOperationException or NotSupportedException =>
                "bridge event payload could not be applied.",
            _ => "bridge event handler hit an unexpected runtime failure.",
        };

    private static string SanitizeBridgeReceiptText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        StringBuilder builder = new(Math.Min(trimmed.Length, maxLength));
        foreach (char character in trimmed)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string SanitizeBridgeReceiptCode(string? value, int maxLength)
    {
        string text = SanitizeBridgeReceiptText(value, maxLength).ToLowerInvariant();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        bool previousWasSeparator = false;
        foreach (char character in text)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (char.IsWhiteSpace(character) && !previousWasSeparator && builder.Length > 0)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('_', '-', '.');
    }

    private static int ClampBridgeLagMs(DateTimeOffset start, DateTimeOffset end)
    {
        double lagMs = (end - start).TotalMilliseconds;
        if (double.IsNaN(lagMs) || lagMs <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp(Math.Round(lagMs, MidpointRounding.AwayFromZero), 0, 86_400_000);
    }

    private static string TakeFirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static ChatRequest BoundChatRequest(ChatRequest request, out bool userMessageTrimmed)
    {
        userMessageTrimmed = false;
        string normalizedUserMessage = request.UserMessage.Trim();
        if (normalizedUserMessage.Length <= ChatRequest.UserMessageMaxLength)
        {
            return request;
        }

        userMessageTrimmed = true;
        return new ChatRequest
        {
            CharacterId = request.CharacterId,
            CharacterName = request.CharacterName,
            TaskTag = request.TaskTag,
            Priority = request.Priority,
            UserMessage = TrimToLength(normalizedUserMessage, ChatRequest.UserMessageMaxLength),
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ImageBase64 = request.ImageBase64,
            ImageMimeType = request.ImageMimeType,
            RequestId = request.RequestId,
        };
    }

    private static string[] GetSortedFiles(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<string>();
        foreach (string pattern in patterns)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
            }
            catch (IOException)
            {
                // Best-effort enumeration: skip patterns that fail with a transient
                // filesystem error (locked file, antivirus contention, sleeping
                // device). The other patterns may still succeed; downstream callers
                // receive a sorted union of whatever was readable.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort enumeration: skip patterns blocked by ACLs. The
                // sidecar runs as the user; an unreadable pattern just yields no
                // matches rather than crashing the helper.
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. files];
    }
}
