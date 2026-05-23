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

public sealed class MemoryRecallRequest
{
    public int? CharacterId { get; init; }

    public string Query { get; init; } = string.Empty;

    public int Limit { get; init; } = 5;
}

public sealed class FeatureDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Notes { get; init; } = string.Empty;
}

public sealed class PackSummary
{
    public string Name { get; init; } = string.Empty;

    public string Author { get; init; } = string.Empty;

    /// Pack-root-relative manifest path (`/`-separated) for the loaded pack so
    /// public listings stay stable across machines without disclosing the
    /// operator's absolute local filesystem layout.
    public string FilePath { get; init; } = string.Empty;

    public int CharacterCount { get; init; }
}

public sealed class ChatIngressSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string CharacterName { get; init; } = string.Empty;

    public string TaskTag { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class OutboxReplyTraceSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string CharacterName { get; init; } = string.Empty;

    public string TaskTag { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public string FallbackStrategy { get; init; } = string.Empty;

    public string ActionType { get; init; } = string.Empty;

    public bool SpeechExpected { get; init; }

    public string SpeechDelivery { get; init; } = string.Empty;

    public string SpeechMimeType { get; init; } = string.Empty;

    public string SpeechPlaybackHint { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset WrittenAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ReplyDeliveryEventPayload
{
    public string RequestId { get; init; } = string.Empty;

    public string Speaker { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public string StrategyId { get; init; } = string.Empty;

    public string Phase { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public bool Rendered { get; init; }

    public string Surface { get; init; } = string.Empty;

    public string CardLabel { get; init; } = string.Empty;

    public int CardIndex { get; init; }

    public int CardCount { get; init; }

    public string Note { get; init; } = string.Empty;
}

public sealed class SpeechPlaybackEventPayload
{
    public string RequestId { get; init; } = string.Empty;

    public bool Started { get; init; }

    public long ArtifactBytes { get; init; }

    public int AttemptCount { get; init; }

    public int ElapsedMs { get; init; }

    public int PlaybackSequence { get; init; }

    public string SupersededRequestId { get; init; } = string.Empty;

    public int SupersededSpeechCount { get; init; }

    public int SupersededSpeechAgeMs { get; init; }

    public long SupersededSpeechBufferedMs { get; init; }

    public long SupersededSpeechRemainingMs { get; init; }

    public string CancellationMode { get; init; } = string.Empty;

    public int SampleRateHz { get; init; }

    public int ChannelCount { get; init; }

    public int BitsPerSample { get; init; }

    public int DurationMs { get; init; }

    public long ByteRate { get; init; }

    public int BlockAlignBytes { get; init; }

    public long AudioDataBytes { get; init; }

    public long FrameCount { get; init; }

    public int BlockRemainderBytes { get; init; }

    public int ValidBitsPerSample { get; init; }

    public long ChannelMask { get; init; }

    public string AudioEncoding { get; init; } = string.Empty;

    public string SampleFormat { get; init; } = string.Empty;

    public string ByteOrder { get; init; } = string.Empty;

    public string MixerConversionHint { get; init; } = string.Empty;

    public int MixerQuantumMs { get; init; }

    public int MixerQuantumFrames { get; init; }

    public long MixerQueueDepthEstimate { get; init; }

    public int MixerTailFrames { get; init; }

    public long MixerBufferedMs { get; init; }

    public int MixerTailMs { get; init; }

    public string PlaybackMode { get; init; } = string.Empty;

    public string PlaybackHint { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public string FileExtension { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string FailureCode { get; init; } = string.Empty;
}

public sealed class SpeechPlaybackSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public bool Started { get; init; }

    public long ArtifactBytes { get; init; }

    public int AttemptCount { get; init; }

    public int ElapsedMs { get; init; }

    public int PlaybackSequence { get; init; }

    public string SupersededRequestId { get; init; } = string.Empty;

    public int SupersededSpeechCount { get; init; }

    public int SupersededSpeechAgeMs { get; init; }

    public long SupersededSpeechBufferedMs { get; init; }

    public long SupersededSpeechRemainingMs { get; init; }

    public string CancellationMode { get; init; } = string.Empty;

    public int SampleRateHz { get; init; }

    public int ChannelCount { get; init; }

    public int BitsPerSample { get; init; }

    public int DurationMs { get; init; }

    public long ByteRate { get; init; }

    public int BlockAlignBytes { get; init; }

    public long AudioDataBytes { get; init; }

    public long FrameCount { get; init; }

    public int BlockRemainderBytes { get; init; }

    public int ValidBitsPerSample { get; init; }

    public long ChannelMask { get; init; }

    public string AudioEncoding { get; init; } = string.Empty;

    public string SampleFormat { get; init; } = string.Empty;

    public string ByteOrder { get; init; } = string.Empty;

    public string MixerConversionHint { get; init; } = string.Empty;

    public int MixerQuantumMs { get; init; }

    public int MixerQuantumFrames { get; init; }

    public long MixerQueueDepthEstimate { get; init; }

    public int MixerTailFrames { get; init; }

    public long MixerBufferedMs { get; init; }

    public int MixerTailMs { get; init; }

    public string PlaybackMode { get; init; } = string.Empty;

    public string PlaybackHint { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    public string FileExtension { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string FailureCode { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ReplyDeliverySnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string Speaker { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public string StrategyId { get; init; } = string.Empty;

    public string Phase { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public bool Rendered { get; init; }

    public string Surface { get; init; } = string.Empty;

    public string CardLabel { get; init; } = string.Empty;

    public int CardIndex { get; init; }

    public int CardCount { get; init; }

    public string Note { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class BridgeActionFeedbackSnapshot
{
    public string RequestId { get; init; } = string.Empty;

    public string EventType { get; init; } = string.Empty;

    public string SourceStrategy { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset CapturedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class BridgeLoopProofSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public bool RequestSeen { get; init; }

    public bool OutboxReplyWritten { get; init; }

    public bool VisibleDeliveryConfirmed { get; init; }

    public bool ActionPlanned { get; init; }

    public bool ActionFeedbackObserved { get; init; }

    public bool SpeechPlaybackExpected { get; init; }

    public bool SpeechPlaybackObserved { get; init; }

    public bool SpeechPlaybackStarted { get; init; }

    public int SpeechPlaybackIngressLagMs { get; init; }

    public int SpeechPlaybackOutboxLagMs { get; init; }

    public int SpeechPlaybackDeliveryLagMs { get; init; }

    public bool LoopClosed { get; init; }

    public ChatIngressSnapshot? LastIngress { get; init; }

    public OutboxReplyTraceSnapshot? LastOutboxReply { get; init; }

    public ReplyDeliverySnapshot? LastReplyDelivery { get; init; }

    public BridgeActionFeedbackSnapshot? LastActionFeedback { get; init; }

    public SpeechPlaybackSnapshot? LastSpeechPlayback { get; init; }
}

public sealed class BridgeActivitySnapshot
{
    public long EventCount { get; init; }

    public long BootCount { get; init; }

    public string LastEventType { get; init; } = string.Empty;

    public DateTimeOffset? LastEventAtUtc { get; init; }

    public string LastEventSource { get; init; } = string.Empty;

    public BridgeBootPayload? LastBridgeBoot { get; init; }

    public UiProbeSnapshot? LastUiProbe { get; init; }

    public UiProbeDiagnosticsSnapshot? UiProbeDiagnostics { get; init; }

    public BridgeLoopProofSnapshot LoopProof { get; init; } = new();
}

public sealed class InferenceWarmupSnapshot
{
    public bool Enabled { get; init; }

    public string Status { get; init; } = string.Empty;

    public string ActiveModel { get; init; } = string.Empty;

    public string? ActiveTierId { get; init; }

    public string ResidencyProvider { get; init; } = string.Empty;

    public int ResidencyTtlSeconds { get; init; }

    public IReadOnlyList<string> LastSeenAvailableModels { get; init; } =
        Array.Empty<string>();

    public string LastWarmedModel { get; init; } = string.Empty;

    public string LastReason { get; init; } = string.Empty;

    public string WarmupTransport { get; init; } = string.Empty;

    public bool LastWarmupUsedResidencyHint { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public DateTimeOffset? LastAttemptAtUtc { get; init; }

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public DateTimeOffset? LastLiveInferenceAtUtc { get; init; }

    public string LastLiveInferenceModel { get; init; } = string.Empty;

    public DateTimeOffset? LastFailureAtUtc { get; init; }

    public long AttemptCount { get; init; }

    public long SuccessCount { get; init; }

    public long FailureCount { get; init; }

    public long LastLatencyMs { get; init; }
}

public sealed class NativeReadinessSnapshot
{
    public bool BridgeBootSeen { get; init; }

    public string BridgeVersion { get; init; } = string.Empty;

    public string BridgeStatus { get; init; } = string.Empty;

    public string CompatSummary { get; init; } = string.Empty;

    public IReadOnlyList<BridgeBootCompatSignal> CompatSignals { get; init; } =
        Array.Empty<BridgeBootCompatSignal>();

    public bool UiProbeEnabled { get; init; }

    public bool HasPalGameStateCompat { get; init; }

    public bool HasPalCharacterCompat { get; init; }

    public bool HasPalBaseCampManagerCompat { get; init; }

    public bool HasPalMapManagerCompat { get; init; }

    public bool HasUserWidgetCompat { get; init; }

    public bool HasUiProbeCandidates { get; init; }

    public string TopUiProbeCandidate { get; init; } = string.Empty;

    public IReadOnlyList<string> ConfiguredHudTargets { get; init; } =
        Array.Empty<string>();

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public bool ActionExecutorEnabled { get; init; }

    public bool NativeHudEnabled { get; init; }

    public bool NativeHudTargetsConfigured { get; init; }

    public bool HudSeamDiscovered { get; init; }

    public bool HudBindReady { get; init; }

    public bool ProductionSamplerEnabled { get; init; }

    public bool ProductionSamplerReady { get; init; }

    public bool WaypointNativeMarkerEnabled { get; init; }

    public bool WaypointMarkerReady { get; init; }

    public HudBindRecommendationSnapshot HudBindRecommendation { get; init; } = new();

    public IReadOnlyList<string> ReadyCapabilities { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MissingPrerequisites { get; init; } =
        Array.Empty<string>();
}

public sealed class RuntimeWorldState
{
    public GameWorldSnapshot Snapshot { get; init; } = new();

    public BridgeActivitySnapshot Bridge { get; init; } = new();
}

public sealed class DashboardSnapshot
{
    public RuntimeHealth Health { get; init; } = new();

    public RuntimeWorldState World { get; init; } = new();

    public InferencePerformanceSnapshot InferencePerformance { get; init; } = new();

    public IReadOnlyList<CharacterRelationship> Relationships { get; init; } =
        Array.Empty<CharacterRelationship>();

    public IReadOnlyList<AdapterLogEntry> Logs { get; init; } =
        Array.Empty<AdapterLogEntry>();

    public IReadOnlyList<OutboxListing> Outbox { get; init; } =
        Array.Empty<OutboxListing>();

    public DateTimeOffset RefreshedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public long ServerLatencyMs { get; init; }
}

public sealed class InferencePerformanceSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public int WindowMinutes { get; init; }

    public int RetainedOperationLimit { get; init; }

    public int SampleCount { get; init; }

    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }

    public long AverageLatencyMs { get; init; }

    public long P95LatencyMs { get; init; }

    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public long TotalTokens { get; init; }

    public long TotalCachedPromptTokens { get; init; }

    public long TotalPromptAudioTokens { get; init; }

    public long TotalCompletionReasoningTokens { get; init; }

    public long TotalCompletionAudioTokens { get; init; }

    public long TotalAcceptedPredictionTokens { get; init; }

    public long TotalRejectedPredictionTokens { get; init; }

    public DateTimeOffset? LastOperationAtUtc { get; init; }

    public InferencePerformanceAssessmentSnapshot Assessment { get; init; } = new();

    public string Summary { get; init; } = string.Empty;

    public IReadOnlyList<InferencePerformanceLaneSnapshot> Lanes { get; init; } =
        Array.Empty<InferencePerformanceLaneSnapshot>();
}

public sealed class InferencePerformanceAssessmentSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string BudgetName { get; init; } = string.Empty;

    public int MinimumSampleCount { get; init; }

    public int SuccessRatioPercent { get; init; }

    public int TargetHitCount { get; init; }

    public int CeilingHitCount { get; init; }

    public int TargetHitRatioPercent { get; init; }

    public int CeilingHitRatioPercent { get; init; }

    public int? LatencyTargetMs { get; init; }

    public int? LatencyCeilingMs { get; init; }

    public string Summary { get; init; } = string.Empty;
}

public sealed class InferencePerformanceLaneSnapshot
{
    public string OperationName { get; init; } = string.Empty;

    public string ProviderName { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public string RequestModel { get; init; } = string.Empty;

    public string ResponseModel { get; init; } = string.Empty;

    public string LastResponseId { get; init; } = string.Empty;

    public string LastUpstreamRequestId { get; init; } = string.Empty;

    public double? LastUpstreamProcessingMs { get; init; }

    public double? LastUpstreamQueueMs { get; init; }

    public double? LastUpstreamTimeToFirstTokenMs { get; init; }

    public double? LastUpstreamPrefillMs { get; init; }

    public double? LastUpstreamDecodeMs { get; init; }

    public string LastSystemFingerprint { get; init; } = string.Empty;

    public IReadOnlyList<string> LastFinishReasons { get; init; } =
        Array.Empty<string>();

    public int SampleCount { get; init; }

    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }

    public long AverageLatencyMs { get; init; }

    public long P95LatencyMs { get; init; }

    public long LastLatencyMs { get; init; }

    public long LastPromptTokens { get; init; }

    public long LastCompletionTokens { get; init; }

    public long LastTotalTokens { get; init; }

    public long LastCachedPromptTokens { get; init; }

    public long LastPromptAudioTokens { get; init; }

    public long LastCompletionReasoningTokens { get; init; }

    public long LastCompletionAudioTokens { get; init; }

    public long LastAcceptedPredictionTokens { get; init; }

    public long LastRejectedPredictionTokens { get; init; }

    public long AveragePromptTokens { get; init; }

    public long AverageCompletionTokens { get; init; }

    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public long TotalTokens { get; init; }

    public long TotalCachedPromptTokens { get; init; }

    public long TotalPromptAudioTokens { get; init; }

    public long TotalCompletionReasoningTokens { get; init; }

    public long TotalCompletionAudioTokens { get; init; }

    public long TotalAcceptedPredictionTokens { get; init; }

    public long TotalRejectedPredictionTokens { get; init; }

    public DateTimeOffset? LastObservedAtUtc { get; init; }

    public DateTimeOffset? LastSuccessAtUtc { get; init; }

    public DateTimeOffset? LastFailureAtUtc { get; init; }

    public string LastErrorType { get; init; } = string.Empty;

    public InferencePerformanceAssessmentSnapshot Assessment { get; init; } = new();
}

public sealed class ReleaseReadinessSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public ReleaseRuntimeSurfaceSummary Runtime { get; init; } = new();

    public ReleaseFeatureCatalogSummary Features { get; init; } = new();

    public ReleasePublicationSummary Publication { get; init; } = new();

    public ReleaseSmokeEvidenceSnapshot SmokeEvidence { get; init; } = new();

    public ReleaseNativeProofEvidenceSnapshot NativeProofEvidence { get; init; } = new();

    public ReleaseProofBundleEvidenceSnapshot ProofBundleEvidence { get; init; } = new();

    public ReleaseSupportBundleEvidenceSnapshot SupportBundleEvidence { get; init; } = new();

    public ReleasePackageVerificationEvidenceSnapshot PackageVerificationEvidence { get; init; } = new();

    public ReleaseArtifactIntegrityEvidenceSnapshot ArtifactIntegrityEvidence { get; init; } = new();

    public ReleaseFullAuditEvidenceSnapshot FullAuditEvidence { get; init; } = new();

    public IReadOnlyList<ReleaseSurfaceDescriptor> Surfaces { get; init; } =
        Array.Empty<ReleaseSurfaceDescriptor>();

    public IReadOnlyList<ReleaseAuditDescriptor> Audits { get; init; } =
        Array.Empty<ReleaseAuditDescriptor>();

    public IReadOnlyList<ReleaseDocumentDescriptor> Documents { get; init; } =
        Array.Empty<ReleaseDocumentDescriptor>();
}

public sealed class ReleaseSmokeEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string RequestId { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string BridgeLoopStatus { get; init; } = string.Empty;

    public bool LoopClosed { get; init; }

    public bool VisibleDeliveryConfirmed { get; init; }

    public bool ActionFeedbackObserved { get; init; }

    public bool NativeHudBindReady { get; init; }

    public string RecommendedHudTarget { get; init; } = string.Empty;

    public IReadOnlyList<string> ConfiguredHudTargets { get; init; } =
        Array.Empty<string>();

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public string DeliverySurface { get; init; } = string.Empty;

    public string ActionType { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }
}

public sealed class ReleaseNativeProofEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public DateTimeOffset? WatcherStartedAtUtc { get; init; }

    public DateTimeOffset? WatcherFinishedAtUtc { get; init; }

    public string WatcherCompletionReason { get; init; } = string.Empty;

    public int TimeoutSeconds { get; init; }

    public int PollIntervalSeconds { get; init; }

    public int PollCount { get; init; }

    public bool TimedOut { get; init; }

    public string DiagnosisCode { get; init; } = string.Empty;

    public string DiagnosisSummary { get; init; } = string.Empty;

    public string DiagnosisAction { get; init; } = string.Empty;

    public string DiagnosisCommand { get; init; } = string.Empty;

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public bool LiveDeliveryProven { get; init; }

    public bool NativeHudBindReady { get; init; }

    public string RecommendedHudTarget { get; init; } = string.Empty;

    public IReadOnlyList<string> ConfiguredHudTargets { get; init; } =
        Array.Empty<string>();

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public string DeliverySurface { get; init; } = string.Empty;

    public string LoopStatus { get; init; } = string.Empty;

    public bool VisibleDeliveryConfirmed { get; init; }

    public bool ActionFeedbackObserved { get; init; }

    public bool AppliedHudRecommendation { get; init; }

    public string AppliedHudRecommendationPath { get; init; } = string.Empty;

    public string RecommendedNextStep { get; init; } = string.Empty;

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<ReleaseNativeProofStatusTransition> StatusTransitions { get; init; } =
        Array.Empty<ReleaseNativeProofStatusTransition>();
}

public sealed class ReleaseNativeProofStatusTransition
{
    public DateTimeOffset ObservedAtUtc { get; init; }

    public int PollIndex { get; init; }

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public string LoopStatus { get; init; } = string.Empty;

    public bool LiveDeliveryProven { get; init; }

    public bool NativeHudBindReady { get; init; }

    public bool VisibleDeliveryConfirmed { get; init; }

    public string DeliverySurface { get; init; } = string.Empty;
}

public sealed class ReleaseProofBundleEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string ArchivePath { get; init; } = string.Empty;

    public string HistoryArchivePath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string ReleasePublicationStatus { get; init; } = string.Empty;

    public string BridgeProofStatus { get; init; } = string.Empty;

    public string SmokeEvidenceStatus { get; init; } = string.Empty;

    public string NativeProofEvidenceStatus { get; init; } = string.Empty;

    public string InferencePerformanceStatus { get; init; } = string.Empty;

    public int InferencePerformanceSampleCount { get; init; }

    public int InferencePerformanceLaneCount { get; init; }

    public int InferencePerformanceAlertingLaneCount { get; init; }

    public int InferencePerformanceLatestReceiptLaneCount { get; init; }

    public int InferencePerformanceTokenReceiptLaneCount { get; init; }

    public int InferencePerformanceFinishReasonReceiptLaneCount { get; init; }

    public int InferencePerformanceUpstreamRequestIdReceiptLaneCount { get; init; }

    public int InferencePerformanceUpstreamProcessingReceiptLaneCount { get; init; }

    public int InferencePerformancePhaseTimingReceiptLaneCount { get; init; }

    public int InferencePerformanceUsageDetailReceiptLaneCount { get; init; }

    public long InferencePerformanceTotalTokens { get; init; }

    public long InferencePerformanceCachedPromptTokens { get; init; }

    public long InferencePerformanceCompletionReasoningTokens { get; init; }

    public bool TtsEnabled { get; init; }

    public long TtsCallCount { get; init; }

    public long TtsFailureCount { get; init; }

    public long TtsSuccessEvidenceCount { get; init; }

    public bool AsrEnabled { get; init; }

    public long AsrCallCount { get; init; }

    public long AsrFailureCount { get; init; }

    public long AsrSuccessEvidenceCount { get; init; }

    public long AsrEndpointingReceiptCount { get; init; }

    public long AsrBargeInCount { get; init; }

    public long AsrEndpointingReviewCount { get; init; }

    public long AsrConfidenceReceiptCount { get; init; }

    public long AsrConfidenceReviewCount { get; init; }

    public long AsrTimingReceiptCount { get; init; }

    public long AsrTimingReviewCount { get; init; }

    public long AsrQualityReceiptCount { get; init; }

    public long AsrQualityReviewCount { get; init; }

    public long AsrUpstreamRequestIdReceiptCount { get; init; }

    public long AsrUpstreamProcessingReceiptCount { get; init; }

    public long AsrUpstreamPhaseTimingReceiptCount { get; init; }

    public string NativeHudConfigSource { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public bool PrivacyRedactionApplied { get; init; }

    public int PrivacyRedactionCheckedFileCount { get; init; }

    public int PrivacyRedactionRedactedFileCount { get; init; }

    public IReadOnlyList<string> PrivacyRedactionRuleHits { get; init; } =
        Array.Empty<string>();

    public bool PublicationScanPassed { get; init; }

    public int PublicationScanCheckedFileCount { get; init; }

    public IReadOnlyList<string> PublicationScanViolations { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> IncludedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MissingOptionalFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseSupportBundleEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string ArchivePath { get; init; } = string.Empty;

    public string HistoryArchivePath { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = string.Empty;

    public string RuntimeRoot { get; init; } = string.Empty;

    public string LaunchEvidenceStatus { get; init; } = string.Empty;

    public string SmokeEvidenceStatus { get; init; } = string.Empty;

    public string NativeProofEvidenceStatus { get; init; } = string.Empty;

    public string ProofBundleEvidenceStatus { get; init; } = string.Empty;

    public string PackageVerificationEvidenceStatus { get; init; } = string.Empty;

    public string FullAuditEvidenceStatus { get; init; } = string.Empty;

    public string NativeHudConfigPath { get; init; } = string.Empty;

    public bool PrivacyRedactionApplied { get; init; }

    public int PrivacyRedactionCheckedFileCount { get; init; }

    public int PrivacyRedactionRedactedFileCount { get; init; }

    public IReadOnlyList<string> PrivacyRedactionRuleHits { get; init; } =
        Array.Empty<string>();

    public bool PublicationScanPassed { get; init; }

    public int PublicationScanCheckedFileCount { get; init; }

    public IReadOnlyList<string> PublicationScanViolations { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> IncludedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MissingOptionalFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleasePackageVerificationEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string PackagePath { get; init; } = string.Empty;

    public string PackageKind { get; init; } = string.Empty;

    public string ReleaseName { get; init; } = string.Empty;

    public string ManifestRelativePath { get; init; } = string.Empty;

    public int ManifestSchemaVersion { get; init; }

    public string PackageSha256 { get; init; } = string.Empty;

    public bool VerifiedFromArchive { get; init; }

    public bool IncludesSidecarPublish { get; init; }

    public bool SelfContainedSidecar { get; init; }

    public bool RequiredFilesPresent { get; init; }

    public int CheckedFileCount { get; init; }

    public bool PublicationScanPassed { get; init; }

    public int PublicationScanCheckedFileCount { get; init; }

    public IReadOnlyList<string> MissingRequiredFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> UnexpectedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> MismatchedFiles { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> PublicationScanViolations { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseArtifactIntegrityEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string PackagingRoot { get; init; } = string.Empty;

    public string ChecksumsJsonPath { get; init; } = string.Empty;

    public string Sha256SumsPath { get; init; } = string.Empty;

    public string Sha512SumsPath { get; init; } = string.Empty;

    public int ArtifactCount { get; init; }

    public bool ChecksumsJsonPresent { get; init; }

    public bool Sha256SumsPresent { get; init; }

    public bool Sha512SumsPresent { get; init; }

    public bool Sha512Skipped { get; init; }

    public bool DetachedSignaturePresent { get; init; }

    public IReadOnlyList<string> DetachedSignaturePaths { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseFullAuditEvidenceSnapshot
{
    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public DateTimeOffset? CapturedAtUtc { get; init; }

    public string FreshnessStatus { get; init; } = string.Empty;

    public DateTimeOffset? FreshUntilUtc { get; init; }

    public int FreshnessWindowHours { get; init; }

    public string ArtifactPath { get; init; } = string.Empty;

    public string HistoryArtifactPath { get; init; } = string.Empty;

    public string AuditRoot { get; init; } = string.Empty;

    public string ResultsPath { get; init; } = string.Empty;

    public string StepsDirectoryPath { get; init; } = string.Empty;

    public bool TestsEnabled { get; init; }

    public bool CoverageEnabled { get; init; }

    public bool SbomEnabled { get; init; }

    public bool PackagingEnabled { get; init; }

    public int TotalStepCount { get; init; }

    public int PassedStepCount { get; init; }

    public int FailedStepCount { get; init; }

    public IReadOnlyList<string> StepNames { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> FailedSteps { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();
}

public sealed class BridgeProofSnapshot
{
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string RecommendedNextStep { get; init; } = string.Empty;

    public string ActiveRequestId { get; init; } = string.Empty;

    public string LastBridgeEventType { get; init; } = string.Empty;

    public DateTimeOffset? LastBridgeEventAtUtc { get; init; }

    public bool LiveDeliveryProven { get; init; }

    public bool NativeHudBindReady { get; init; }

    public NativeReadinessSnapshot NativeReadiness { get; init; } = new();

    public BridgeLoopProofSnapshot LoopProof { get; init; } = new();

    public BridgeBootPayload? LastBridgeBoot { get; init; }

    public UiProbeSnapshot? LastUiProbe { get; init; }

    public UiProbeDiagnosticsSnapshot? UiProbeDiagnostics { get; init; }

    public IReadOnlyList<BridgeProofLaneSnapshot> ProofLanes { get; init; } =
        Array.Empty<BridgeProofLaneSnapshot>();

    public IReadOnlyList<string> ReadyEvidence { get; init; } =
        Array.Empty<string>();

    public IReadOnlyList<string> CurrentBlockers { get; init; } =
        Array.Empty<string>();
}

public sealed class BridgeProofLaneSnapshot
{
    public string Name { get; init; } = string.Empty;

    public bool Required { get; init; }

    public string Status { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string NextAction { get; init; } = string.Empty;
}

public sealed class ReleaseRuntimeSurfaceSummary
{
    public string AdapterName { get; init; } = string.Empty;

    public int ApiRouteCount { get; init; }

    public int ProtocolRouteCount { get; init; }

    public int FeaturedOperationalSurfaceCount { get; init; }

    public string DashboardPath { get; init; } = string.Empty;

    public string MetricsPath { get; init; } = string.Empty;

    public string OpenApiJsonPath { get; init; } = string.Empty;

    public string OpenApiYamlPath { get; init; } = string.Empty;

    public string McpPath { get; init; } = string.Empty;

    public IReadOnlyList<string> ConditionalReadPaths { get; init; } =
        Array.Empty<string>();
}

public sealed class ReleaseFeatureCatalogSummary
{
    public int Total { get; init; }

    public int Ready { get; init; }

    public int Scaffolded { get; init; }

    public int Deferred { get; init; }

    public int Other { get; init; }
}

public sealed class ReleasePublicationSummary
{
    public string Status { get; init; } = string.Empty;

    public string NextRecommendedPass { get; init; } = string.Empty;

    public string NextRecommendedCommand { get; init; } = string.Empty;

    public IReadOnlyList<string> CurrentBlockers { get; init; } = Array.Empty<string>();
}

public sealed class ReleaseSurfaceDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Method { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Area { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;
}

public sealed class ReleaseAuditDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Command { get; init; } = string.Empty;

    public string Purpose { get; init; } = string.Empty;
}

public sealed class ReleaseDocumentDescriptor
{
    public string Id { get; init; } = string.Empty;

    public string Path { get; init; } = string.Empty;

    public string Purpose { get; init; } = string.Empty;
}

public sealed class RuntimeHealth
{
    public string AdapterName { get; init; } = string.Empty;

    public bool AdapterReady { get; init; }

    public bool BridgeEnabled { get; init; }

    public bool InferenceConfigured { get; init; }

    public string InferenceModel { get; init; } = string.Empty;

    public string InferenceActiveModel { get; init; } = string.Empty;

    public string? InferenceActiveTierId { get; init; }

    public IReadOnlyList<string> InferenceLastSeenAvailableModels { get; init; } =
        Array.Empty<string>();

    public bool VisionEnabled { get; init; }

    public string VisionModel { get; init; } = string.Empty;

    public bool TtsEnabled { get; init; }

    public bool AsrEnabled { get; init; }

    public bool AutomationEnabled { get; init; }

    public string Status { get; init; } = string.Empty;

    public long InferenceSuccessCount { get; init; }

    public long InferenceFailureCount { get; init; }

    public long InferenceBypassCount { get; init; }

    public long FallbackReplyCount { get; init; }

    public int CharacterCount { get; init; }

    public int RememberedEntries { get; init; }

    public int LoadedPackCount { get; init; }

    public int KnownBaseCount { get; init; }

    public long BridgeEventCount { get; init; }

    public long BridgeBootCount { get; init; }

    public string LastBridgeEventType { get; init; } = string.Empty;

    public DateTimeOffset? LastBridgeEventAtUtc { get; init; }

    public string RuntimeRoot { get; init; } = string.Empty;

    public int TrackedRelationshipCount { get; init; }

    public int OutboxPendingCount { get; init; }

    public long TotalPromptTokens { get; init; }

    public long TotalCompletionTokens { get; init; }

    public long TotalInferenceTokens { get; init; }

    public long VisionCallCount { get; init; }

    public long VisionFailureCount { get; init; }

    public long TtsCallCount { get; init; }

    public long TtsSuccessCount { get; init; }

    public long TtsFailureCount { get; init; }

    public long AsrCallCount { get; init; }

    public long AsrSuccessCount { get; init; }

    public long AsrFailureCount { get; init; }

    public long AsrEndpointingReceiptCount { get; init; }

    public long AsrBargeInCount { get; init; }

    public long AsrEndpointingReviewCount { get; init; }

    public long AsrConfidenceReceiptCount { get; init; }

    public long AsrConfidenceReviewCount { get; init; }

    public long AsrTimingReceiptCount { get; init; }

    public long AsrTimingReviewCount { get; init; }

    public long AsrQualityReceiptCount { get; init; }

    public long AsrQualityReviewCount { get; init; }

    public long AsrUpstreamRequestIdReceiptCount { get; init; }

    public long AsrUpstreamProcessingReceiptCount { get; init; }

    public long AsrUpstreamPhaseTimingReceiptCount { get; init; }

    public int InboxPendingCount { get; init; }

    public int ScreenshotPendingCount { get; init; }

    public int ArchiveFileCount { get; init; }

    public int FailedFileCount { get; init; }

    public bool SessionDirty { get; init; }

    public DateTimeOffset? SessionLastSavedAtUtc { get; init; }

    public NativeReadinessSnapshot NativeReadiness { get; init; } = new();

    public BridgeLoopProofSnapshot BridgeLoop { get; init; } = new();

    public long RateLimitedCount { get; init; }

    public string InferenceCircuitState { get; init; } = string.Empty;

    public int InferenceCircuitFailures { get; init; }

    public InferenceWarmupSnapshot InferenceWarmup { get; init; } = new();

    /// <summary>Per-strategy usage counter from the deterministic fallback director.
    /// Rendered as labeled Prometheus counters (`palllm_fallback_strategy_total{strategy="..."}`).
    /// Empty dictionary when no fallback replies have been served yet.</summary>
    public IReadOnlyList<FallbackStrategyCount> FallbackStrategyCounts { get; init; } = [];

    /// <summary>Per-transition counter for model-tier graduations. Each entry records
    /// a directional transition (for example, `small -> large`) along with how many times that
    /// transition fired since startup. Rendered as labeled Prometheus counters
    /// (`palllm_model_tier_transition_total{from="...",to="..."}`).</summary>
    public IReadOnlyList<ModelTierTransitionCount> ModelTierTransitionCounts { get; init; } = [];

    /// <summary>Cumulative Prometheus-style histogram of end-to-end chat latency
    /// in seconds. Buckets cover the realistic range for PalLLM: sub-10ms for
    /// fallback-only paths, up to 60s for inference-backed paths on large models.</summary>
    public ChatLatencyHistogram ChatLatency { get; init; } = new(0, 0, []);

    /// <summary>Operator-actionable next-step hints derived from the current snapshot.
    /// Empty when the runtime is healthy. Each entry carries a stable <c>Code</c> for
    /// programmatic consumption (dashboards, <c>pal next</c> advisor, MCP tools), a
    /// human-readable <c>Message</c>, and an optional <c>Command</c> the operator can
    /// copy-paste to address it. This is the single source of "what should I do
    /// right now?" for both /api/health curl callers and the Field Console.</summary>
    public IReadOnlyList<HealthSuggestion> Suggestions { get; init; } = Array.Empty<HealthSuggestion>();
}

/// <summary>One actionable next-step hint surfaced in <see cref="RuntimeHealth.Suggestions"/>.
/// <para><see cref="Code"/> is a stable kebab-case identifier (e.g. <c>"no-packs-loaded"</c>,
/// <c>"inference-circuit-open"</c>, <c>"bridge-idle"</c>) so dashboards and CI tooling can
/// match on it without parsing the Message.</para>
/// <para><see cref="Message"/> is a single sentence explaining the situation in plain
/// English.</para>
/// <para><see cref="Command"/> is an optional copy-paste command (a <c>pal</c> verb,
/// a PowerShell one-liner, etc.) the operator can run to address it. Null when no
/// single-shot remediation exists.</para>
/// <para><see cref="Severity"/> is one of <c>"info"</c> / <c>"warn"</c> / <c>"urgent"</c>
/// and lets every consumer (dashboard, MCP client, pal next, pal doctor) render
/// matching visual treatment without duplicating a code-to-severity map. The builder
/// is the source of truth so a new hint code automatically lights up the right colour
/// across every surface without coordinated edits.</para></summary>
public sealed record HealthSuggestion(string Code, string Message, string? Command, string Severity);

/// <summary>Cumulative Prometheus-style histogram record. <see cref="SumSeconds"/>
/// + <see cref="Count"/> give average; each <see cref="LatencyHistogramBucket"/>
/// entry records "count of observations with duration &lt;= UpperBoundSeconds".</summary>
public sealed record ChatLatencyHistogram(
    long Count,
    double SumSeconds,
    IReadOnlyList<LatencyHistogramBucket> Buckets);

public sealed record LatencyHistogramBucket(double UpperBoundSeconds, long CumulativeCount);

/// <summary>Count of chat replies served by a single fallback strategy since startup.</summary>
public sealed record FallbackStrategyCount(string StrategyId, long Count);

/// <summary>Count of times the tier orchestrator graduated from one tier id to another.</summary>
public sealed record ModelTierTransitionCount(string From, string To, long Count);

public sealed class BridgeDrainResult
{
    public int ProcessedCount { get; init; }

    public int FailedCount { get; init; }
}

public sealed class ScreenshotIngestResult
{
    public int ProcessedCount { get; init; }

    public int FailedCount { get; init; }
}

public sealed class TtsSynthesizeRequest
{
    public string Text { get; init; } = string.Empty;

    public string? Voice { get; init; }

    /// When true, runtime writes the audio to <c>runtime-root/TTS/{id}.{ext}</c>
    /// and returns its path so a game-side consumer can play it without round-
    /// tripping the bytes. When false, audio is returned inline as base64.
    public bool WriteToDisk { get; init; } = true;
}

public sealed class TtsSynthesizeResponse
{
    public bool Success { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public string Voice { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    /// Runtime-authored consumer hint for which local playback path is most likely
    /// to succeed. Current values:
    /// <c>sound_player</c> for wave-compatible files and <c>media_player</c> for
    /// common compressed formats the game-side bridge can hand to Windows media
    /// playback helpers.
    public string PlaybackHint { get; init; } = string.Empty;

    public int AudioBytes { get; init; }

    public string? FilePath { get; init; }

    public string? AudioBase64 { get; init; }
}

public sealed class AudioTranscribeRequest
{
    /// Base64-encoded local audio payload (no data URL prefix).
    public string AudioBase64 { get; init; } = string.Empty;

    public string? AudioMimeType { get; init; } = "audio/wav";

    /// Optional language hint forwarded to compatible ASR endpoints.
    public string? Language { get; init; }

    /// Optional prompt/context hint forwarded to compatible ASR endpoints.
    public string? Prompt { get; init; }

    /// Optional content-free client VAD / turn-close receipt. Carries timing
    /// metadata only; no audio bytes, transcript text, or utterance content.
    public AudioTurnEndpointingInput? Endpointing { get; init; }
}

public sealed class AudioTranscribeResponse
{
    public bool Success { get; init; }

    public string Transcript { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int AudioBytes { get; init; }

    public long LatencyMs { get; init; }

    public string UpstreamRequestId { get; init; } = string.Empty;

    public double? UpstreamProcessingMs { get; init; }

    public double? UpstreamQueueMs { get; init; }

    public double? UpstreamTimeToFirstTokenMs { get; init; }

    public double? UpstreamPrefillMs { get; init; }

    public double? UpstreamDecodeMs { get; init; }

    public AudioTurnEndpointingReceipt Endpointing { get; init; } = new();

    public AudioTranscriptionConfidenceReceipt Confidence { get; init; } = new();

    public AudioTranscriptionTimingReceipt Timing { get; init; } = new();

    public AudioTranscriptionQualityReceipt Quality { get; init; } = new();
}

public sealed class AudioTranscriptionConfidenceReceipt
{
    public bool LogprobsRequested { get; init; }

    public bool LogprobsReturned { get; init; }

    public string Status { get; init; } = "not_requested";

    public int TokenCount { get; init; }

    public double? AverageLogprob { get; init; }

    public double? MinLogprob { get; init; }

    public int LowConfidenceTokenCount { get; init; }

    public float LowConfidenceThreshold { get; init; }
}

public sealed class AudioTranscriptionTimingReceipt
{
    public bool VerboseJsonRequested { get; init; }

    public bool VerboseJsonReturned { get; init; }

    public bool SegmentTimestampsRequested { get; init; }

    public bool WordTimestampsRequested { get; init; }

    public bool SegmentTimestampsReturned { get; init; }

    public bool WordTimestampsReturned { get; init; }

    public string Status { get; init; } = "not_requested";

    public string Language { get; init; } = string.Empty;

    public double? DurationSeconds { get; init; }

    public int SegmentCount { get; init; }

    public int WordCount { get; init; }

    public double? FirstSegmentStartSeconds { get; init; }

    public double? LastSegmentEndSeconds { get; init; }

    public double? CoveredSegmentSeconds { get; init; }

    public double? SegmentCoverageRatio { get; init; }

    public int MaxTurnDurationMs { get; init; }

    public string[] Flags { get; init; } = [];
}

public sealed class AudioTranscriptionQualityReceipt
{
    public bool VerboseJsonRequested { get; init; }

    public bool QualityMetadataReturned { get; init; }

    public string Status { get; init; } = "not_requested";

    public int SegmentCount { get; init; }

    public int QualitySegmentCount { get; init; }

    public double? AverageSegmentLogprob { get; init; }

    public double? MinSegmentLogprob { get; init; }

    public int LowAverageLogprobSegmentCount { get; init; }

    public double LowAverageLogprobThreshold { get; init; } = -1.0d;

    public double? MaxCompressionRatio { get; init; }

    public int HighCompressionRatioSegmentCount { get; init; }

    public double HighCompressionRatioThreshold { get; init; } = 2.4d;

    public double? MaxNoSpeechProbability { get; init; }

    public int NoSpeechProbabilitySegmentCount { get; init; }

    public int SilentSegmentCandidateCount { get; init; }

    public int TemperatureSegmentCount { get; init; }

    public double? MaxTemperature { get; init; }

    public string[] Flags { get; init; } = [];
}

public sealed class AudioTurnEndpointingInput
{
    public int? SpeechMs { get; init; }

    public int? LeadingSilenceMs { get; init; }

    public int? TrailingSilenceMs { get; init; }

    public string? EndpointReason { get; init; }

    public bool BargeIn { get; init; }
}

public sealed class AudioTurnEndpointingReceipt
{
    public bool ClientVadSupplied { get; init; }

    public string Status { get; init; } = "not_supplied";

    public string EndpointReason { get; init; } = "not_supplied";

    public bool BargeIn { get; init; }

    public int? SpeechMs { get; init; }

    public int? LeadingSilenceMs { get; init; }

    public int? TrailingSilenceMs { get; init; }

    public int? TotalTurnMs { get; init; }

    public int PreSpeechPaddingTargetMs { get; init; }

    public int EndpointSilenceTargetMs { get; init; }

    public int MaxTurnDurationMs { get; init; }

    public string[] Flags { get; init; } = [];
}

public sealed class SpeechArtifact
{
    public string RequestId { get; init; } = string.Empty;

    /// Current delivery shape. Today PalLLM writes a local file under
    /// runtime-root/TTS and lets the consumer decide how to play it.
    public string Delivery { get; init; } = "local_file";

    /// Concrete voice id accepted by the backing TTS server.
    public string Voice { get; init; } = string.Empty;

    /// Higher-level cue from the presentation planner (e.g. "steady-guide").
    public string VoicePrint { get; init; } = string.Empty;

    public string SubtitleStyle { get; init; } = string.Empty;

    public string MimeType { get; init; } = string.Empty;

    /// Runtime-authored playback-path hint mirrored from the TTS response so the
    /// game-side consumer does not have to re-derive format support from MIME or
    /// file extension alone.
    public string PlaybackHint { get; init; } = string.Empty;

    public int AudioBytes { get; init; }

    public string? FilePath { get; init; }
}

public sealed class SessionPersistenceResult
{
    public bool Success { get; init; }

    public int MemoryEntryCount { get; init; }

    public int RelationshipCount { get; init; }

    public DateTimeOffset? SavedAtUtc { get; init; }

    /// Absolute session file path for successful save/load operations. Blank on
    /// failure so public responses don't disclose the operator's local layout.
    public string FilePath { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;
}

/// Envelope written to the outbox after a successful chat orchestration. The
/// structure mirrors <see cref="BridgeEventEnvelope"/> for incoming events so a
/// UE4SS Lua consumer can parse both halves of the bridge with one schema.
public sealed class OutboxEnvelope
{
    public string EventType { get; init; } = "chat_reply";

    public string Source { get; init; } = "palllm";

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;

    public OutboxChatReply Payload { get; init; } = new();
}

public sealed class OutboxChatReply
{
    public string RequestId { get; init; } = string.Empty;

    public ActionIntent? Action { get; init; }

    public int? CharacterId { get; init; }

    public string CharacterName { get; init; } = string.Empty;

    public string TaskTag { get; init; } = string.Empty;

    public string TaskKind { get; init; } = string.Empty;

    public string AssistantMessage { get; init; } = string.Empty;

    public string ResponsePath { get; init; } = string.Empty;

    public bool UsedFallback { get; init; }

    public string? FallbackStrategy { get; init; }

    public string? FallbackPhase { get; init; }

    public SpeechArtifact? Speech { get; init; }

    public PresentationCuePlan Presentation { get; init; } = new();
}

public sealed class OutboxListing
{
    public string FileName { get; init; } = string.Empty;

    public DateTimeOffset WrittenAtUtc { get; init; }

    public long SizeBytes { get; init; }
}

public sealed class VisionDescribeRequest
{
    /// Base64-encoded image payload (no `data:` prefix - the client adds the MIME header).
    public string ImageBase64 { get; init; } = string.Empty;

    public string? ImageMimeType { get; init; } = "image/png";

    /// Optional free-form prompt. Leave blank to get a terse default scene description.
    public string? Prompt { get; init; }

    /// Optional higher-level system prompt (defaults to a current-game scene analyst persona).
    public string? SystemPrompt { get; init; }

    public int? MaxTokens { get; init; }

    public float? Temperature { get; init; }
}

public sealed class VisionDescribeResponse
{
    public bool Success { get; init; }

    public string Description { get; init; } = string.Empty;

    public string StatusMessage { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public long LatencyMs { get; init; }
}

public sealed class VisionWorldStateRequest
{
    public string ImageBase64 { get; init; } = string.Empty;

    public string? ImageMimeType { get; init; } = "image/png";

    /// Optional free-form context hint (e.g. "player just entered combat zone").
    public string? Hint { get; init; }

    /// When true, runtime applies the extracted state to its snapshot so downstream
    /// prompts and fallback selection react to the visual update immediately.
    public bool ApplyToSnapshot { get; init; } = true;
}

/// Structured scene readout produced by the vision model. All fields optional -
/// the runtime merges whatever the model manages to extract into the current snapshot.
public sealed class VisionWorldStateSnapshot
{
    public string? TimeOfDay { get; init; }

    public string? Weather { get; init; }

    public string? Biome { get; init; }

    public bool? InCombat { get; init; }

    public bool? InBase { get; init; }

    public int? VisibleHostileCount { get; init; }

    public string? PlayerActivity { get; init; }

    public string? NotableLandmark { get; init; }

    public float? LightLevel { get; init; }

    public IReadOnlyList<string> Hostiles { get; init; } = [];

    public IReadOnlyList<string> Resources { get; init; } = [];
}

public sealed class VisionWorldStateResponse
{
    public bool Success { get; init; }

    public string StatusMessage { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public long LatencyMs { get; init; }

    public string? RawContent { get; init; }

    public VisionWorldStateSnapshot? State { get; init; }

    public bool Applied { get; init; }
}
