using System.Text;
using System.Text.Json;

namespace PalLLM.Tests;

public sealed class OpenApiSnapshotTests
{
    [Test]
    public async Task OpenApiEndpoint_MatchesCommittedSnapshot()
    {
        await using var fixture = new SidecarTestFixture();

        string runtimeDocument = await fixture.Client.GetStringAsync("/openapi/v1.json");
        string snapshotPath = LocateSnapshotPath();

        Assert.That(File.Exists(snapshotPath), Is.True,
            $"Committed OpenAPI snapshot not found at '{snapshotPath}'. Run scripts/export-openapi.ps1 to regenerate it.");

        string committedDocument = await File.ReadAllTextAsync(snapshotPath);

        Assert.That(
            CanonicalizeJson(runtimeDocument),
            Is.EqualTo(CanonicalizeJson(committedDocument)),
            "The committed OpenAPI snapshot drifted from the live sidecar contract. Run scripts/export-openapi.ps1 and commit the updated docs/openapi file.");
    }

    private static string LocateSnapshotPath()
    {
        string current = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8; depth++)
        {
            string candidate = Path.Combine(current, "docs", "openapi", "palllm-sidecar-v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent == current)
            {
                break;
            }

            current = parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "palllm-sidecar-v1.json");
    }

    private static string CanonicalizeJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonical(document.RootElement, writer, isRoot: true);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer, bool isRoot = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (isRoot && string.Equals(property.Name, "servers", StringComparison.Ordinal))
                    {
                        // The live test host injects a base URL while the build-time
                        // snapshot does not. Ignore host-specific metadata so the
                        // assertion pins the API contract itself.
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }
                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }
}
