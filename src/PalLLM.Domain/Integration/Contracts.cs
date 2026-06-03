using System.Text.Json;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Integration;

public sealed class Vector3Snapshot
{
    public float X { get; init; }

    public float Y { get; init; }

    public float Z { get; init; }
}

/// <summary>
/// Internal bridge-authored character snapshot.
///
/// <para>The current live bridge is game-specific, but the publication-facing
/// HTTP/OpenAPI contract exposes this shape under the neutral
/// <c>GameCharacterSnapshot</c> schema id so external consumers can depend on
/// a target-agnostic contract.</para>
/// </summary>
public sealed class GameCharacterSnapshot
{
    public int Id { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string Species { get; init; } = string.Empty;

    public bool IsAlive { get; init; } = true;

    public bool IsPlayerFaction { get; init; } = true;

    public bool IsIncapacitated { get; init; }

    public int Age { get; init; }

    public Vector3Snapshot Position { get; init; } = new();

    public Dictionary<string, int> Skills { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, float> Needs { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string Role { get; init; } = string.Empty;

    public string CurrentTask { get; init; } = string.Empty;

    public float? HealthFraction { get; init; }

    public float? StaminaFraction { get; init; }

    public float? HungerFraction { get; init; }

    public float? Morale { get; init; }

    public float? Loyalty { get; init; }

    public float? RecentDamageFraction { get; init; }

    public int NearbyEnemyCount { get; init; }

    public int NearbyAllyCount { get; init; }

    public List<string> Loadout { get; init; } = [];

    public List<string> RecentEvents { get; init; } = [];

    public List<string> Traits { get; init; } = [];

    public List<string> Tags { get; init; } = [];
}

/// <summary>
/// Internal bridge-authored snapshot of the current game world.
///
/// <para>The sidecar currently fills this from the shipped Palworld bridge,
/// but the publication-facing HTTP/OpenAPI contract exposes the same shape
/// under the neutral <c>GameWorldSnapshot</c> schema id.</para>
/// </summary>
public sealed class GameWorldSnapshot
{
    public string Source { get; init; } = "unknown";

    public string WorldName { get; init; } = string.Empty;

    public bool IsWorldLoaded { get; init; }

    public long CurrentTick { get; init; }

    public long TicksPerHour { get; init; } = 3_600;

    public long TicksPerDay { get; init; } = 86_400;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Biome { get; init; } = string.Empty;

    public string Weather { get; init; } = string.Empty;

    public string TimeOfDay { get; init; } = string.Empty;

    public float? ThreatLevel { get; init; }

    public float? AlertLevel { get; init; }

    public float? PlayerHealthFraction { get; init; }

    public float? PlayerStaminaFraction { get; init; }

    public float? PlayerHungerFraction { get; init; }

    public string CurrentObjective { get; init; } = string.Empty;

    public TravelStatusSnapshot? LastTravel { get; init; }

    public ProductionStatusSnapshot? LastProduction { get; init; }

    public bool? IsInBase { get; init; }

    public List<string> ActiveBaseIds { get; init; } = [];

    public List<GameBaseSnapshot> KnownBases { get; init; } = [];

    public List<string> NearbyHostiles { get; init; } = [];

    public List<string> NearbyFriendlies { get; init; } = [];

    public List<string> NearbyResources { get; init; } = [];

    public List<string> RecentEvents { get; init; } = [];

    public List<GameCharacterSnapshot> Characters { get; init; } = [];
}

public sealed class BridgeEventEnvelope
{
    public string EventType { get; init; } = string.Empty;

    public string Source { get; init; } = "ue4ss";

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public JsonElement Payload { get; init; }
}

public sealed class BridgeBootCompatSignal
{
    public string Key { get; init; } = string.Empty;

    public bool Present { get; init; }
}

public sealed class BridgeBootPayload
{
    public string Version { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Compat { get; init; } = string.Empty;

    public IReadOnlyList<BridgeBootCompatSignal> CompatSignals { get; init; } =
        Array.Empty<BridgeBootCompatSignal>();

    public bool UiProbeEnabled { get; init; }

    public bool ActionExecutorEnabled { get; init; }

    public bool NativeHudRenderEnabled { get; init; }

    public int NativeHudWidgetTargetCount { get; init; }

    public IReadOnlyList<string> NativeHudWidgetTargets { get; init; } =
        Array.Empty<string>();

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public bool ProductionSamplerEnabled { get; init; }

    public bool WaypointNativeMarkerEnabled { get; init; }
}

public sealed class ChatHookPayload
{
    public string Sender { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;
}

public sealed class BaseDiscoveredPayload
{
    public string BaseId { get; init; } = string.Empty;

    public float? AreaRange { get; init; }
}

public sealed class CombatEventPayload
{
    public string Phase { get; init; } = "start";

    public string Opponent { get; init; } = string.Empty;

    public string Location { get; init; } = string.Empty;

    public int? AlliesCount { get; init; }

    public int? HostilesCount { get; init; }

    public float? ThreatLevel { get; init; }
}

public sealed class PalStatusEventPayload
{
    public string PalName { get; init; } = string.Empty;

    public string Species { get; init; } = string.Empty;

    public string Change { get; init; } = string.Empty;

    public float? HealthFraction { get; init; }

    public float? StaminaFraction { get; init; }

    public string Note { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string SourceStrategy { get; init; } = string.Empty;
}

public sealed class ProductionEventPayload
{
    public string BaseId { get; init; } = string.Empty;

    public string Station { get; init; } = string.Empty;

    public string Item { get; init; } = string.Empty;

    public int Quantity { get; init; }

    public string Status { get; init; } = "completed";

    public string Note { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string SourceStrategy { get; init; } = string.Empty;
}

public sealed class ProductionStatusSnapshot
{
    public string BaseId { get; init; } = string.Empty;

    public string Station { get; init; } = string.Empty;

    public string Item { get; init; } = string.Empty;

    public int Quantity { get; init; }

    public string Status { get; init; } = "completed";

    public string Note { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string SourceStrategy { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class TravelEventPayload
{
    public string Origin { get; init; } = string.Empty;

    public string Destination { get; init; } = string.Empty;

    public string Waypoint { get; init; } = string.Empty;

    public string Mode { get; init; } = "on_foot";

    public string Note { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string SourceStrategy { get; init; } = string.Empty;
}

public sealed class TravelStatusSnapshot
{
    public string Origin { get; init; } = string.Empty;

    public string Destination { get; init; } = string.Empty;

    public string Waypoint { get; init; } = string.Empty;

    public string Mode { get; init; } = "on_foot";

    public string Note { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string SourceStrategy { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class WeatherEventPayload
{
    public string Weather { get; init; } = string.Empty;

    public string Biome { get; init; } = string.Empty;

    public string Severity { get; init; } = "mild";
}

public sealed class RaidEventPayload
{
    public string BaseId { get; init; } = string.Empty;

    public string Faction { get; init; } = string.Empty;

    public int? AttackerCount { get; init; }

    public string Phase { get; init; } = "incoming";

    public string Note { get; init; } = string.Empty;
}

public sealed class UiProbeWidgetEntry
{
    public string DisplayName { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public string ClassName { get; init; } = string.Empty;

    public int SeenCount { get; init; }

    public bool IsActive { get; init; }

    public string LastLifecycle { get; init; } = string.Empty;
}

public sealed class UiProbeEventPayload
{
    public string Reason { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string DumpPath { get; init; } = string.Empty;

    public int ObservedWidgetCount { get; init; }

    public int ActiveWidgetCount { get; init; }

    public IReadOnlyList<UiProbeWidgetEntry> Widgets { get; init; } =
        Array.Empty<UiProbeWidgetEntry>();
}

public sealed class UiProbeSnapshot
{
    public string Reason { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string DumpPath { get; init; } = string.Empty;

    public int ObservedWidgetCount { get; init; }

    public int ActiveWidgetCount { get; init; }

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<UiProbeWidgetEntry> Widgets { get; init; } =
        Array.Empty<UiProbeWidgetEntry>();
}

public sealed class UiProbeCandidateSummary
{
    public string DisplayName { get; init; } = string.Empty;

    public string FullName { get; init; } = string.Empty;

    public string ClassName { get; init; } = string.Empty;

    public int DumpCount { get; init; }

    public int ActiveObservationCount { get; init; }

    public int PeakSeenCount { get; init; }

    public double ActiveRatio { get; init; }

    public int Score { get; init; }

    public string LastLifecycle { get; init; } = string.Empty;

    public DateTimeOffset? LastSeenAtUtc { get; init; }

    public IReadOnlyList<string> Rationale { get; init; } = Array.Empty<string>();
}

public sealed class UiProbeDiagnosticsSnapshot
{
    public int DumpCount { get; init; }

    public int CandidateCount { get; init; }

    public DateTimeOffset? LastDumpAtUtc { get; init; }

    public string LastDumpPath { get; init; } = string.Empty;

    public string LastReason { get; init; } = string.Empty;

    public string LastSummary { get; init; } = string.Empty;

    public IReadOnlyList<UiProbeCandidateSummary> Candidates { get; init; } =
        Array.Empty<UiProbeCandidateSummary>();
}

public sealed class HudBindRecommendationSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string RecommendedTarget { get; init; } = string.Empty;

    public string RecommendedDisplayName { get; init; } = string.Empty;

    public string RecommendedFullName { get; init; } = string.Empty;

    public string RecommendedClassName { get; init; } = string.Empty;

    public int RecommendedScore { get; init; }

    public bool ConfiguredTargetMatchesRecommendation { get; init; }

    public IReadOnlyList<string> ConfiguredTargets { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> SuggestedConfigTargets { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> SuggestedNextSteps { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> Rationale { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<UiProbeCandidateSummary> Shortlist { get; init; } =
        Array.Empty<UiProbeCandidateSummary>();
}

/// <summary>
/// Internal bridge-authored record for one known base / camp / outpost in the
/// current world snapshot. Published through the neutral
/// <c>GameBaseSnapshot</c> schema id on the public OpenAPI surface.
/// </summary>
public sealed class GameBaseSnapshot
{
    public string BaseId { get; init; } = string.Empty;

    public float? AreaRange { get; init; }

    public DateTimeOffset FirstSeenUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastSeenUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Source { get; init; } = "bridge";
}

public sealed class ChatRequest
{
    /// <summary>
    /// Hard cap for a single user-authored chat turn before prompt assembly.
    /// HTTP callers are rejected above this size; direct runtime callers are
    /// trimmed to this cap so MCP/internal paths cannot bypass the same safety
    /// budget.
    /// </summary>
    public const int UserMessageMaxLength = 16 * 1024;

    public int? CharacterId { get; init; }

    public string? CharacterName { get; init; }

    public string TaskTag { get; init; } = "player_chat";

    public PalTaskPriority Priority { get; init; } = PalTaskPriority.Normal;

    public string UserMessage { get; init; } = string.Empty;

    public float? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    /// Optional base64-encoded screenshot the runtime will pass through the vision
    /// client (if enabled) to produce a short visual-context hint spliced into the
    /// system prompt. Off by default; set via the caller to augment a specific ask
    /// with what the player is actually looking at.
    public string? ImageBase64 { get; init; }

    public string? ImageMimeType { get; init; }

    /// Caller-supplied correlation id. When omitted the runtime generates a short
    /// id so every chat turn gets traceable in logs, outbox envelopes, and the
    /// ChatResponse. Useful for pairing a UE4SS-rendered reply with a server log.
    public string? RequestId { get; init; }
}

public sealed class PresentationCuePlan
{
    public string Source { get; init; } = string.Empty;

    public string StrategyId { get; init; } = string.Empty;

    public string Phase { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public AudioCuePlan Audio { get; init; } = new();

    public VisualCuePlan Visual { get; init; } = new();

    public DeliverySurfacePlan Surface { get; init; } = new();
}

public sealed class AudioCuePlan
{
    public string BehaviorId { get; init; } = string.Empty;

    public string Delivery { get; init; } = string.Empty;

    public string VoicePrint { get; init; } = string.Empty;

    public string SubtitleStyle { get; init; } = string.Empty;

    public string MusicMode { get; init; } = string.Empty;

    public string Stinger { get; init; } = string.Empty;

    public string MixProfile { get; init; } = string.Empty;

    public string Spatialization { get; init; } = string.Empty;

    public int Priority { get; init; }

    public int CooldownMs { get; init; }

    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
}

public sealed class VisualCuePlan
{
    public string BehaviorId { get; init; } = string.Empty;

    public string PortraitExpression { get; init; } = string.Empty;

    public string BodyPose { get; init; } = string.Empty;

    public string HudAccent { get; init; } = string.Empty;

    public string WorldMarker { get; init; } = string.Empty;

    public string ScreenTreatment { get; init; } = string.Empty;

    public string CameraTreatment { get; init; } = string.Empty;

    public string LightCue { get; init; } = string.Empty;

    public string Emote { get; init; } = string.Empty;

    public int Priority { get; init; }

    public int HoldMs { get; init; }

    public IReadOnlyList<string> Layers { get; init; } = Array.Empty<string>();
}

public sealed class DeliverySurfacePlan
{
    public string FamilyId { get; init; } = string.Empty;

    public string LayoutMode { get; init; } = string.Empty;

    public string PathBadge { get; init; } = string.Empty;

    public string FamilyBadge { get; init; } = string.Empty;

    public string PhaseBadge { get; init; } = string.Empty;

    /// Primary family-authored card title for the first player-facing strip.
    public string PrimaryTitle { get; init; } = string.Empty;

    /// Family-authored cue title for follow-up cue-focused strips.
    public string CueTitle { get; init; } = string.Empty;

    /// Family-authored readout title for follow-up strips that carry leftover
    /// route, threat, base, or status context.
    public string ReadoutTitle { get; init; } = string.Empty;

    /// Family-authored support title for action or speech follow-ups.
    public string SupportTitle { get; init; } = string.Empty;

    /// Family-authored title for game-side action preview cards.
    public string ActionPreviewTitle { get; init; } = string.Empty;

    /// Family-authored title for game-side action feedback cards.
    public string ActionFeedbackTitle { get; init; } = string.Empty;

    /// Display-ready badge rail for the primary strip header.
    public IReadOnlyList<string> HeaderTokens { get; init; } = Array.Empty<string>();

    /// Compact display-ready cue labels for subtitle/HUD/screen treatment.
    public IReadOnlyList<string> CueTokens { get; init; } = Array.Empty<string>();

    /// Compact display-ready staging labels for marker, portrait, pose, camera,
    /// and similar scene-direction cues.
    public IReadOnlyList<string> StageTokens { get; init; } = Array.Empty<string>();

    /// Compact display-ready atmosphere labels for delivery, voice, music, and
    /// stinger coordination.
    public IReadOnlyList<string> AtmosphereTokens { get; init; } = Array.Empty<string>();

    /// Display-ready focus rail for route, threat, base, or objective anchors.
    public IReadOnlyList<string> FocusTokens { get; init; } = Array.Empty<string>();

    /// Display-ready compact status rail for health, threat, morale, and other
    /// quickly scannable state.
    public IReadOnlyList<string> StatusTokens { get; init; } = Array.Empty<string>();

    /// Display-ready footer rail for the primary strip.
    public IReadOnlyList<string> FooterTokens { get; init; } = Array.Empty<string>();

    /// Ordered follow-up kind preference for the game-side renderer. Values are
    /// compact identifiers such as `support`, `readout`, or `cue`.
    public IReadOnlyList<string> FollowupOrder { get; init; } = Array.Empty<string>();

    /// Maximum number of delivery cards the game-side renderer should stage for
    /// a single reply before it starts compacting follow-ups.
    public int CardBudget { get; init; }

    /// Number of cue tokens the primary card should consume before follow-up
    /// cards pick up the remainder.
    public int PrimaryCueTokenCount { get; init; }

    /// Number of focus tokens the primary card should consume before follow-up
    /// cards pick up the remainder.
    public int PrimaryFocusTokenCount { get; init; }

    /// Number of status tokens the primary card should consume before follow-up
    /// cards pick up the remainder.
    public int PrimaryStatusTokenCount { get; init; }

    /// Number of stage tokens the primary card should consume before follow-up
    /// cards pick up the remainder.
    public int PrimaryStageTokenCount { get; init; }

    /// Number of atmosphere tokens the primary card should consume before
    /// follow-up cards pick up the remainder.
    public int PrimaryAtmosphereTokenCount { get; init; }

    public int WidthChars { get; init; }

    public int MaxBodyLines { get; init; }

    public int PrimaryDurationMs { get; init; }

    public int FollowupDurationMs { get; init; }
}

public sealed class ActionIntent
{
    /// Canonical action name. One of the operator-approved allowlist values.
    public string Type { get; init; } = string.Empty;

    /// Arbitrary name-to-value arguments. Kept as a flat dictionary of strings so
    /// Lua consumers can parse without a JSON schema, and so PalLLM never
    /// encodes assumptions about game internals that it cannot verify.
    public IReadOnlyDictionary<string, string> Arguments { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// Relative urgency for the consumer (0-100). Peak-phase retreats emit with
    /// high priority; ambient harvest suggestions with low.
    public int Priority { get; init; }

    /// Short human-readable reason - lets a player inspect why the companion
    /// suggested this (e.g. "Three hostiles visible and health at 40%"), and
    /// makes the runtime's action recommendations debuggable without logs.
    public string Justification { get; init; } = string.Empty;

    /// Strategy id that produced this intent, for traceability.
    public string SourceStrategy { get; init; } = string.Empty;
}

public sealed class ChatResponse
{
    public string RequestId { get; init; } = string.Empty;

    /// Optional action the companion would like the game to take. Always null
    /// unless <c>PalLLM:Automation:Enabled</c> is flipped on and the chosen
    /// strategy maps to a type on the AllowedActions allowlist. Purely advisory
    /// - the game side decides whether to act on it.
    public ActionIntent? Action { get; init; }

    public string CharacterName { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string InferenceModel { get; init; } = string.Empty;

    public string InferenceProfileId { get; init; } = string.Empty;

    public string InferenceLane { get; init; } = string.Empty;

    public bool? ThinkingRequested { get; init; }

    public bool InferenceEnabled { get; init; }

    public bool InferenceAttempted { get; init; }

    public bool InferenceBypassed { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public int MaxTokens { get; init; }

    public string VisualContextSource { get; init; } = string.Empty;

    public string SystemPrompt { get; init; } = string.Empty;

    public string? AssistantMessage { get; init; }

    public bool UsedFallback { get; init; }

    public string? FallbackStrategy { get; init; }

    public string? FallbackPhase { get; init; }

    public IReadOnlyList<string> FallbackSignals { get; init; } = Array.Empty<string>();

    public PresentationCuePlan Presentation { get; init; } = new();

    /// Optional speech artifact synthesized for this reply. When present the
    /// audio already exists on disk and can be played without calling back into
    /// the sidecar.
    public SpeechArtifact? Speech { get; init; }

    public IReadOnlyList<string> MemoryMatches { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Advisory — the DuoTaskKind the Pass-16 ChatTaskKindInferer
    /// assigned to this turn. Null on turns where the runtime skipped
    /// advisory inference (rate-limited, missing DI, etc.). Purely
    /// observational: chat routing has already happened by the time
    /// this lands in the response.
    /// </summary>
    public string? InferredTaskKind { get; init; }

    /// <summary>
    /// Advisory — the DuoCooperationPattern the Pass-8 planner picked
    /// for this turn given the inferred task kind + current role
    /// coverage. Populated on every chat turn starting Pass 21; null
    /// only when the planner throws (which is deterministic so should
    /// never happen in practice). Does NOT affect actual inference
    /// routing today — the single-lane `_inferenceClient` still
    /// handles every dispatch. Pass 22 added `DispatchedRoleChain` to
    /// expose the concrete role chain the planner's pattern would
    /// invoke once multi-role dispatch lands.
    /// </summary>
    public string? CooperationPattern { get; init; }

    /// <summary>
    /// Advisory — the ordered role chain the Pass-22 ChatDispatchPlanner
    /// would invoke for this turn, given current role bindings + the
    /// planner's pattern. Empty when the planner chose
    /// DeterministicOnly or both roles are unbound. The runtime still
    /// dispatches through the single-lane inference client today, so
    /// this is observational; the field is here so operators + AI
    /// agents can see the concrete execution plan and so a future pass
    /// can flip the single-lane passthrough to actually invoke the
    /// chain recorded here without a breaking contract change.
    /// </summary>
    public IReadOnlyList<string> DispatchedRoleChain { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Advisory — short dispatch-mode bucket ("deterministic-only",
    /// "single-role", "duo-sequential", "duo-parallel", "duo-fanout",
    /// "duo-tournament", "duo-background", "duo-watchdog",
    /// "duo-appeal"). Correlates with the Pass-22
    /// <c>ChatDispatchDecision.Mode</c>. Null when no decision was
    /// captured for this turn.
    /// </summary>
    public string? DispatchMode { get; init; }
}

