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

        // Pass 369: the OpenAPI generator captures XML doc-comment summaries
        // verbatim, so multi-line summaries land inside JSON string values
        // with whichever line ending the *generator host* used. A Windows
        // host writes "\r\n"; a Linux host writes "\n". The committed
        // snapshot was originally generated on Windows; running the same
        // test against a freshly-generated document on a Linux CI runner
        // therefore drifts on the embedded line endings even though the
        // API contract is byte-identical. Normalize "\r\n" -> "\n" before
        // comparison so the canonicalization stays platform-agnostic.
        string canonical = Encoding.UTF8.GetString(stream.ToArray());
        return canonical.Replace("\\r\\n", "\\n").Replace("\r\n", "\n");
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
