using System.Text.Json;
using PalLLM.Domain.Configuration;

namespace PalLLM.Sidecar;

/// <summary>
/// Reads the latest <c>SelfHealingWorker</c> evidence artifact so multiple
/// surfaces (HTTP <c>/api/self-healing/status</c>, the MCP
/// <c>pal_self_healing_status</c> tool, and the dashboard chip) all see the
/// exact same payload and pending-marker contract.
///
/// <para>Returns either:</para>
/// <list type="bullet">
///   <item>The full <c>SelfHealingReport</c> JSON written by the worker,
///         passed through as-is so no shape drift can creep in.</item>
///   <item>A tiny structured marker <c>{status: "pending" | "unreadable",
///         detail: "..."}</c> when the worker has not ticked yet or the
///         evidence file could not be read.</item>
/// </list>
/// </summary>
internal static class SelfHealingStatusReader
{
    public static JsonDocument Read(PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string path = Path.Combine(options.RuntimeRoot, "SelfHealingEvidence", "latest-self-healing.json");
        int maxBytes = options.Http.LocalArtifactMaxBytes;
        if (!File.Exists(path))
        {
            return BuildPendingMarker(
                status: "pending",
                detail: "The self-healing watchdog has not ticked yet. First evidence lands within one CheckIntervalSeconds window after boot.");
        }

        ArtifactJsonFileReader.ArtifactJsonDocumentReadResult readResult =
            ArtifactJsonFileReader.TryReadDocument(path, maxBytes);
        if (readResult.Succeeded && readResult.Document is not null)
        {
            return readResult.Document;
        }

        return BuildPendingMarker(
            "unreadable",
            ArtifactJsonFileReader.BuildFailureMessage(
                "The self-healing evidence file",
                readResult.FailureCode ?? ArtifactJsonFileReader.ArtifactJsonReadFailureCode.Unreadable,
                maxBytes));
    }

    private static JsonDocument BuildPendingMarker(string status, string detail)
    {
        string json = JsonSerializer.Serialize(
            new SelfHealingStatusMarker(status, detail),
            PalLlmJsonSerializerContext.Default.SelfHealingStatusMarker);
        return JsonDocument.Parse(json);
    }
}
