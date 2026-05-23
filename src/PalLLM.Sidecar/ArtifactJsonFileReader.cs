using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class ArtifactJsonFileReader
{
    public static ArtifactJsonReadResult<T> TryRead<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo,
        int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);

        BoundedJsonFileReader.JsonReadResult<T> result = BoundedJsonFileReader.TryRead(
            path,
            effectiveMaxBytes,
            stream => JsonSerializer.Deserialize(stream, jsonTypeInfo));
        return new ArtifactJsonReadResult<T>(result.Value, MapFailureCode(result.FailureCode));
    }

    public static ArtifactJsonDocumentReadResult TryReadDocument(string path, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);

        int effectiveMaxBytes = NormalizeMaxBytes(maxBytes);

        BoundedJsonFileReader.JsonDocumentReadResult result =
            BoundedJsonFileReader.TryReadDocument(path, effectiveMaxBytes);
        return new ArtifactJsonDocumentReadResult(result.Document, MapFailureCode(result.FailureCode));
    }

    public static string BuildFailureMessage(
        string subject,
        ArtifactJsonReadFailureCode failureCode,
        int maxBytes) =>
        failureCode switch
        {
            ArtifactJsonReadFailureCode.Oversized =>
                $"{subject} exceeds the configured size limit of {NormalizeMaxBytes(maxBytes)} bytes.",
            ArtifactJsonReadFailureCode.MalformedJson =>
                $"{subject} contains malformed JSON.",
            _ =>
                $"{subject} could not be read safely.",
        };

    private static int NormalizeMaxBytes(int maxBytes) => Math.Max(1_024, maxBytes);

    private static ArtifactJsonReadFailureCode? MapFailureCode(BoundedJsonFileReader.JsonReadFailureCode? failureCode) =>
        failureCode switch
        {
            BoundedJsonFileReader.JsonReadFailureCode.Oversized => ArtifactJsonReadFailureCode.Oversized,
            BoundedJsonFileReader.JsonReadFailureCode.MalformedJson => ArtifactJsonReadFailureCode.MalformedJson,
            BoundedJsonFileReader.JsonReadFailureCode.Unreadable => ArtifactJsonReadFailureCode.Unreadable,
            _ => null,
        };

    internal readonly record struct ArtifactJsonReadResult<T>(
        T? Value,
        ArtifactJsonReadFailureCode? FailureCode)
    {
        public bool Succeeded => FailureCode is null;
    }

    internal readonly record struct ArtifactJsonDocumentReadResult(
        JsonDocument? Document,
        ArtifactJsonReadFailureCode? FailureCode)
    {
        public bool Succeeded => FailureCode is null;
    }

    public enum ArtifactJsonReadFailureCode
    {
        Oversized,
        MalformedJson,
        Unreadable,
    }
}
