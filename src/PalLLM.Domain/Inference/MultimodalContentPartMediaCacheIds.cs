using System.Buffers;
using System.Text.Json;

namespace PalLLM.Domain.Inference;

internal static class MultimodalContentPartMediaCacheIds
{
    public static JsonElement AddStableIds(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return content.Clone();
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteValue(writer, content);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(writer, element);
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteObject(Utf8JsonWriter writer, JsonElement obj)
    {
        string? mediaCacheId = TryBuildMediaCacheId(obj);
        bool shouldAddMediaCacheId = !string.IsNullOrWhiteSpace(mediaCacheId);

        writer.WriteStartObject();
        foreach (JsonProperty property in obj.EnumerateObject())
        {
            if (shouldAddMediaCacheId && property.NameEquals("uuid"))
            {
                continue;
            }

            property.WriteTo(writer);
        }

        if (shouldAddMediaCacheId)
        {
            writer.WriteString("uuid", mediaCacheId);
        }

        writer.WriteEndObject();
    }

    private static string? TryBuildMediaCacheId(JsonElement obj)
    {
        if (HasExplicitUuid(obj) ||
            !obj.TryGetProperty("type", out JsonElement typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? type = typeElement.GetString();
        return type switch
        {
            "image_url" => TryBuildUrlMediaCacheId(obj, "image_url", "image"),
            "video_url" => TryBuildUrlMediaCacheId(obj, "video_url", "video"),
            "audio_url" => TryBuildUrlMediaCacheId(obj, "audio_url", "audio"),
            "input_audio" => TryBuildInputAudioMediaCacheId(obj),
            _ => null,
        };
    }

    private static bool HasExplicitUuid(JsonElement obj)
    {
        if (!obj.TryGetProperty("uuid", out JsonElement uuid) ||
            uuid.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(uuid.GetString());
    }

    private static string? TryBuildUrlMediaCacheId(JsonElement obj, string propertyName, string modality)
    {
        if (!obj.TryGetProperty(propertyName, out JsonElement mediaElement))
        {
            return null;
        }

        string? url = mediaElement.ValueKind switch
        {
            JsonValueKind.Object when mediaElement.TryGetProperty("url", out JsonElement urlElement) &&
                                      urlElement.ValueKind == JsonValueKind.String => urlElement.GetString(),
            JsonValueKind.String => mediaElement.GetString(),
            _ => null,
        };

        return TryParseDataUrl(url, out string mediaType, out string base64Payload)
            ? MediaCacheIdBuilder.Build(modality, mediaType, base64Payload)
            : null;
    }

    private static string? TryBuildInputAudioMediaCacheId(JsonElement obj)
    {
        if (!obj.TryGetProperty("input_audio", out JsonElement inputAudio) ||
            inputAudio.ValueKind != JsonValueKind.Object ||
            !inputAudio.TryGetProperty("data", out JsonElement dataElement) ||
            dataElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? data = dataElement.GetString();
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        string format = "wav";
        if (inputAudio.TryGetProperty("format", out JsonElement formatElement) &&
            formatElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(formatElement.GetString()))
        {
            format = formatElement.GetString()!.Trim();
        }

        return MediaCacheIdBuilder.Build("audio", BuildAudioMediaType(format), data);
    }

    private static bool TryParseDataUrl(string? url, out string mediaType, out string base64Payload)
    {
        mediaType = string.Empty;
        base64Payload = string.Empty;

        if (string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int commaIndex = url.IndexOf(',');
        if (commaIndex <= "data:".Length || commaIndex == url.Length - 1)
        {
            return false;
        }

        string header = url["data:".Length..commaIndex];
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string headerMediaType = header.Split(';', 2)[0].Trim();
        mediaType = string.IsNullOrWhiteSpace(headerMediaType)
            ? "application/octet-stream"
            : headerMediaType;
        base64Payload = url[(commaIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(base64Payload);
    }

    private static string BuildAudioMediaType(string format)
    {
        string normalized = format.Trim().ToLowerInvariant();
        if (normalized.Contains('/'))
        {
            return normalized;
        }

        return normalized switch
        {
            "mp3" => "audio/mpeg",
            "m4a" => "audio/mp4",
            _ => "audio/" + normalized,
        };
    }
}
