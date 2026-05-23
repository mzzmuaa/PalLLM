using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using PalLLM.Domain.Integration;

namespace PalLLM.Sidecar;

internal static class OpenApiSchemaReferenceIds
{
    public static string? Create(JsonTypeInfo typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);

        return TryGetOverrideName(typeInfo.Type, out string? schemaId)
            ? schemaId
            : OpenApiOptions.CreateDefaultSchemaReferenceId(typeInfo);
    }

    public static bool TryGetOverrideName(Type type, out string? schemaId)
    {
        ArgumentNullException.ThrowIfNull(type);

        schemaId = type switch
        {
            var candidate when candidate == typeof(GameWorldSnapshot) => "GameWorldSnapshot",
            var candidate when candidate == typeof(GameCharacterSnapshot) => "GameCharacterSnapshot",
            var candidate when candidate == typeof(GameBaseSnapshot) => "GameBaseSnapshot",
            _ => null,
        };

        return schemaId is not null;
    }
}
