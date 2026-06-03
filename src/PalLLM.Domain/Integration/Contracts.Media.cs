// Contracts (partial): TTS / ASR / vision / outbox / session-persistence contracts.
// Part of the PalLLM.Domain.Integration wire contract; see Contracts.cs for the core game/bridge/chat shapes.
using System.Text.Json;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Integration;

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
