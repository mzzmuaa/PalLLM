using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar.Mcp;

/// <summary>
/// Model Context Protocol (MCP) resource surface for PalLLM.
///
/// <para>Resources complement <see cref="PalLlmMcpTools"/>. Tools are
/// model-controlled action functions; resources are application-controlled
/// passive data the MCP host can read (and optionally subscribe to) as
/// context. Claude Desktop, VS Code, and Cursor all surface resources as
/// "context cards" the user can attach to a conversation - so exposing
/// world state / feature catalog / runtime health as resources makes them
/// one-click draggable into any MCP conversation.</para>
///
/// <para>Naming convention: <c>palllm://&lt;namespace&gt;/&lt;path&gt;</c>.
/// Direct resources have a fixed URI; templated resources use
/// <c>{parameterName}</c> placeholders and receive them as method
/// parameters.</para>
/// </summary>
[McpServerResourceType]
public static class PalLlmMcpResources
{
    private static readonly JsonSerializerOptions JsonOptions = PalLlmJsonOptions.Create(static options =>
    {
        options.WriteIndented = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    });
    private static readonly PalLlmJsonSerializerContext JsonContext = new(JsonOptions);

    [McpServerResource(
        UriTemplate = "palllm://world/snapshot",
        Name = "World snapshot",
        MimeType = "application/json")]
    [Description(
        "The current game-world snapshot - world name, biome, weather, time of day, player "
        + "vitals, nearby hostiles/friendlies/resources, base activity, and every known "
        + "companion. Consume this as a context card when you want the model to reason about "
        + "the player's in-game situation.")]
    public static string GetWorldSnapshot(PalLlmRuntime runtime)
    {
        GameWorldSnapshot snapshot = runtime.Adapter.Snapshot;
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    [McpServerResource(
        UriTemplate = "palllm://features",
        Name = "Feature catalog",
        MimeType = "application/json")]
    [Description(
        "Complete list of every feature PalLLM ships (id, status, summary, notes). Attach this "
        + "when you want the model to know what PalLLM can and cannot do before planning - the "
        + "catalog is exhaustive and versioned.")]
    public static string GetFeatureCatalog() =>
        JsonSerializer.Serialize(PalLlmFeatureCatalog.All.ToArray(), JsonOptions);

    [McpServerResource(
        UriTemplate = "palllm://runtime/health",
        Name = "Runtime health",
        MimeType = "application/json")]
    [Description(
        "Current runtime health - inference / vision / TTS configuration, circuit-breaker "
        + "state, bridge activity, and session persistence status. Useful when you need to "
        + "diagnose why a chat turn routed to fallback instead of live inference.")]
    public static string GetRuntimeHealth(PalLlmRuntime runtime) =>
        JsonSerializer.Serialize(runtime.GetHealth(), JsonContext.RuntimeHealth);

    [McpServerResource(
        UriTemplate = "palllm://characters",
        Name = "Companion roster",
        MimeType = "application/json")]
    [Description(
        "Array of companion characters currently in the player's party / snapshot "
        + "(id, display name, species, role, current task, traits). Attach as context before "
        + "asking the model to pick a companion or write dialogue.")]
    public static string GetCharacters(PalLlmRuntime runtime) =>
        JsonSerializer.Serialize(runtime.Adapter.Snapshot.Characters.ToArray(), JsonOptions);

    [McpServerResource(
        UriTemplate = "palllm://model/tier/active",
        Name = "Active model tier",
        MimeType = "application/json")]
    [Description(
        "Current active model tier id and model tag, plus the list of configured tiers and "
        + "their availability from the latest probe. Shows whether the small fast-start tier "
        + "is still in use or the large quality tier has finished pulling and graduated.")]
    public static string GetActiveModelTier(
        PalLLM.Domain.Inference.ModelTierOrchestrator orchestrator,
        PalLLM.Domain.Configuration.PalLlmOptions options,
        PalLlmRuntime runtime)
    {
        McpActiveModelTierPayload payload = new(
            ActiveModel: orchestrator.GetActiveModel(),
            ActiveTierId: orchestrator.GetActiveTierId(),
            LastSeenAvailableModels: orchestrator.GetLastSeenAvailableModels().ToArray(),
            ConfiguredTiers: options.Inference.ModelTiers.ToArray(),
            Warmup: runtime.GetInferenceWarmupSnapshot());
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerResource(
        UriTemplate = "palllm://model/collaboration",
        Name = "Model collaboration plan",
        MimeType = "application/json")]
    [Description(
        "Hardware-aware collaboration plan for the configured local model lanes. Explains which model should "
        + "act as the fast scout or worker, which should act as the dense reviewer or final judge, and which "
        + "self-healing loops fit the current setup.")]
    public static string GetModelCollaboration(ModelCollaborationPlanner planner) =>
        JsonSerializer.Serialize(planner.GetSnapshot(), JsonOptions);

    [McpServerResource(
        UriTemplate = "palllm://character/{characterId}",
        Name = "Character profile",
        MimeType = "application/json")]
    [Description(
        "Per-character profile keyed by numeric characterId. Returns the snapshot entry for "
        + "that character if present, otherwise a 'not-found' sentinel. Templated URI so the "
        + "MCP host can build resource pickers that let the user pick a specific character.")]
    public static string GetCharacterProfile(
        PalLlmRuntime runtime,
        [Description("The numeric character id. Must match an id from the current snapshot.")]
        string characterId)
    {
        if (!int.TryParse(characterId, out int parsed))
        {
            return JsonSerializer.Serialize(
                new McpCharacterProfileErrorPayload("characterId must be an integer."),
                JsonOptions);
        }

        GameCharacterSnapshot? character = runtime.Adapter.Snapshot.Characters
            .FirstOrDefault(c => c.Id == parsed);
        if (character is null)
        {
            McpCharacterProfileNotFoundPayload payload = new(
                $"Character {parsed} not found in the current snapshot.",
                runtime.Adapter.Snapshot.Characters.Select(c => c.Id).ToArray());
            return JsonSerializer.Serialize(payload, JsonOptions);
        }

        return JsonSerializer.Serialize(character, JsonOptions);
    }
}
