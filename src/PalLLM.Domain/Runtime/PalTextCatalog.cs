namespace PalLLM.Domain.Runtime;

public static class PalTextCatalog
{
    private static readonly Dictionary<string, string> Strings = new()
    {
        ["status.starting"] = "Starting PalLLM...",
        ["status.ready"] = "PalLLM sidecar is ready.",
        ["status.snapshot"] = "Snapshot updated from Palworld bridge.",
        ["status.bridge"] = "Processed UE4SS bridge events.",
        ["status.inference_disabled"] = "Inference is disabled. Configure a local endpoint to enable live replies.",
        ["status.fast_path"] = "Deterministic fallback fast path is serving routine tasks.",
        ["status.fallback"] = "Inference unavailable; fallback behavior director is active.",
    };

    public static string Get(string key) => Strings.TryGetValue(key, out string? value) ? value : key;
}
