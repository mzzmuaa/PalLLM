using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Packs;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    The runtime singleton. Owns the chat hot path
//            (PalLlmRuntime.ChatAsync), composes the inference call, the
//            fallback director, the rate limiter, the memory store, the
//            pack overlay, and the bridge surface. Every /api/chat,
//            /api/world/*, and /api/memory/* call lands here.
//   surface: PalLlmRuntime.ChatAsync (the hot path), GetHealth, GetWorldState,
//            GetMemorySnapshot. ~3863 lines but every method is documented
//            inline and the file's structure mirrors the runtime composition.
//            Touch only what you're changing.
//   gate:    Drift_Test_count_docs (1315 expected); ChatAsync behaviour pinned
//            by PalLlmRuntimeChatTests + neighbouring suites.
//   adr:     0001-deterministic-first-reply-pipeline.md (load-bearing for
//            the chat hot path), 0005-ttl-cache-for-posture-surfaces.md.
//   docs:    docs/HOT_PATH.md (per-method budgets), docs/DATAFLOW.md
//            (sequence diagrams), docs/MENTAL_MODEL.md, docs/CODE_MAP.md.
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    // Hard safety cap on prompt length. Real prompts sit at 1-3 KB after context
    // assembly; this 16 KB cap is an order of magnitude of headroom that only fires
    // on pathological producers. Modern target models have 128K-262K token windows
    // so aggressive compaction is no longer load-bearing - an earlier version that
    // delegated to a summariser just dropped context. Kept as a last-resort guard.
    private const int PromptHardCapChars = 16_000;
    internal const int AssistantMessageHardCapChars = 8 * 1024;
    private const int MaxMemoryRecallLimit = 5;
    private const int NativeMixerQueueQuantumMs = 10;
    private static readonly TimeSpan DirectoryActivitySnapshotTtl = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan UiProbeDiagnosticsSnapshotTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan InferenceWarmupMinimumGap = TimeSpan.FromSeconds(10);
    private static readonly string[] UiProbePositiveKeywords =
    [
        "hud",
        "overlay",
        "subtitle",
        "caption",
        "chat",
        "message",
        "guide",
        "companion",
        "status",
        "notification",
        "ingame",
        "mainhud",
        "playerhud",
        "reticle",
        "crosshair",
        "marker",
        "root",
    ];
    private static readonly string[] UiProbeNegativeKeywords =
    [
        "inventory",
        "map",
        "menu",
        "pause",
        "settings",
        "option",
        "title",
        "popup",
        "dialog",
        "loading",
        "craft",
        "technology",
        "palbox",
        "storage",
        "guild",
        "tutorial",
    ];

    private static readonly JsonSerializerOptions BridgeJsonOptions = CreateBridgeJsonOptions();

    private static readonly PalLlmDomainJsonSerializerContext BridgeJsonContext = new(BridgeJsonOptions);

    private static readonly UiProbeDumpJsonContext UiProbeDumpJsonContextInstance = new(CreateBridgeJsonOptions());

    private static JsonSerializerOptions CreateBridgeJsonOptions() =>
        PalLlmDomainJsonOptions.Create(static options =>
        {
            options.PropertyNameCaseInsensitive = true;
        });

    private readonly PalLlmOptions _options;
    private readonly IInferenceClient _inferenceClient;
    private readonly InferenceExecutionPlanner _inferenceExecutionPlanner;
    private readonly FallbackBehaviorEngine _fallbackBehaviorEngine;
    private readonly PresentationCuePlanner _presentationCuePlanner;
    private readonly ReflectionService _reflectionService;
    private readonly RelationshipTracker _relationshipTracker;
    private readonly VisionOrchestrator? _visionOrchestrator;
    private readonly SessionPersistence _sessionPersistence;
    private readonly ChatRateLimiter _chatRateLimiter;
    private readonly ITtsClient _ttsClient;
    private readonly IAudioTranscriptionClient _asrClient;
    private readonly ModelRoleRegistry _roleRegistry;
    private readonly DuoOrchestratorPlanner _duoPlanner;
    private long _visionCallCount;
    private long _visionFailureCount;
    private long _rateLimitedCount;
    private long _ttsCallCount;
    private long _ttsSuccessCount;
    private long _ttsFailureCount;
    private long _asrCallCount;
    private long _asrSuccessCount;
    private long _asrFailureCount;
    private long _asrEndpointingReceiptCount;
    private long _asrBargeInCount;
    private long _asrEndpointingReviewCount;
    private long _asrConfidenceReceiptCount;
    private long _asrConfidenceReviewCount;
    private long _asrTimingReceiptCount;
    private long _asrTimingReviewCount;
    private long _asrQualityReceiptCount;
    private long _asrQualityReviewCount;
    private long _asrUpstreamRequestIdReceiptCount;
    private long _asrUpstreamProcessingReceiptCount;
    private long _asrUpstreamPhaseTimingReceiptCount;
    private readonly PalLlmMetrics _metrics;
    private readonly InferencePerformanceTracker _inferencePerformance;
    private readonly object _drainGate = new();
    private readonly object _bridgeGate = new();
    private readonly object _directoryActivityGate = new();
    private readonly object _uiProbeDiagnosticsGate = new();
    private readonly object _inferenceWarmupGate = new();
    private readonly SemaphoreSlim _inferenceWarmupSemaphore = new(1, 1);

    private long _inferenceSuccessCount;
    private long _inferenceFailureCount;
    private long _inferenceBypassCount;
    private long _fallbackReplyCount;
    private long _totalPromptTokens;
    private long _totalCompletionTokens;
    private long _totalTokens;
    private long _bridgeEventCount;
    private long _bridgeBootCount;
    private string _lastBridgeEventType = string.Empty;
    private string _lastBridgeEventSource = string.Empty;
    private DateTimeOffset? _lastBridgeEventAtUtc;
    private BridgeBootPayload? _lastBridgeBoot;
    private UiProbeSnapshot? _lastUiProbe;
    private ChatIngressSnapshot? _lastChatIngress;
    private OutboxReplyTraceSnapshot? _lastOutboxReply;
    private ReplyDeliverySnapshot? _lastReplyDelivery;
    private BridgeActionFeedbackSnapshot? _lastActionFeedback;
    private SpeechPlaybackSnapshot? _lastSpeechPlayback;
    private DirectoryActivitySnapshot _directoryActivity = new();
    private long _nextDirectoryActivityRefreshTick;
    private UiProbeDiagnosticsSnapshot _uiProbeDiagnostics = new();
    private long _nextUiProbeDiagnosticsRefreshTick;
    private InferenceWarmupSnapshot _inferenceWarmup = new();

    public PalLlmRuntime(PalLlmOptions options, IInferenceClient inferenceClient)
        : this(options, inferenceClient, visionClient: null, ttsClient: null, metrics: null)
    {
    }

    public PalLlmRuntime(PalLlmOptions options, IInferenceClient inferenceClient, IVisionClient? visionClient)
        : this(options, inferenceClient, visionClient, ttsClient: null, metrics: null)
    {
    }

    public PalLlmRuntime(
        PalLlmOptions options,
        IInferenceClient inferenceClient,
        IVisionClient? visionClient,
        ITtsClient? ttsClient)
        : this(options, inferenceClient, visionClient, ttsClient, metrics: null)
    {
    }

    public PalLlmRuntime(
        PalLlmOptions options,
        IInferenceClient inferenceClient,
        IVisionClient? visionClient,
        ITtsClient? ttsClient,
        PalLlmMetrics? metrics,
        InferencePerformanceTracker? inferencePerformance = null,
        IAudioTranscriptionClient? asrClient = null)
    {
        _options = options;
        _inferenceClient = inferenceClient;
        _inferenceExecutionPlanner = new InferenceExecutionPlanner(options);
        _metrics = metrics ?? new PalLlmMetrics();
        _inferencePerformance = inferencePerformance ?? new InferencePerformanceTracker();
        _fallbackBehaviorEngine = new FallbackBehaviorEngine(options);
        _presentationCuePlanner = new PresentationCuePlanner();
        Adapter = new BridgeGameAdapter(options);
        MemoryStore = new ConversationMemoryStore();
        NarrativePacks = new NarrativePackService(options);
        _reflectionService = new ReflectionService(MemoryStore);
        _relationshipTracker = new RelationshipTracker();
        _visionOrchestrator = visionClient is not null
            ? new VisionOrchestrator(visionClient, options, _inferencePerformance)
            : null;
        _sessionPersistence = new SessionPersistence(options, MemoryStore, _relationshipTracker);
        _chatRateLimiter = new ChatRateLimiter
        {
            MaxPerMinute = Math.Max(0, options.Fallback.MaxCharacterRequestsPerMinute),
        };
        _ttsClient = ttsClient ?? new DisabledTtsClient();
        _asrClient = asrClient ?? new DisabledAudioTranscriptionClient();
        _roleRegistry = new ModelRoleRegistry(options);
        _duoPlanner = new DuoOrchestratorPlanner(_roleRegistry);
        options.EnsureDirectories();
        NarrativePacks.Reload();

        // Auto-load prior session state when persistence is enabled so companions
        // feel continuous across restarts. Failures are silent - starting fresh is
        // always a valid fallback.
        if (options.Session.Enabled)
        {
            _sessionPersistence.Load();
        }

        _inferenceWarmup = BuildInferenceWarmupSnapshot(
            current: null,
            status: options.Inference.EnableWarmup && options.Inference.Enabled ? "idle" : "disabled",
            statusMessage: options.Inference.EnableWarmup
                ? (options.Inference.Enabled
                    ? "Inference warmup has not run yet."
                    : "Inference is disabled, so no warmup will run.")
                : "Inference warmup is disabled by configuration.");

        PalStatusLine.SetReady(PalTextCatalog.Get("status.ready"));
    }

    public async Task<TtsSynthesizeResponse> SynthesizeSpeechAsync(
        TtsSynthesizeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _ttsCallCount);

        TtsResult result = await _ttsClient.SynthesizeAsync(new TtsRequest
        {
            Text = request.Text ?? string.Empty,
            Voice = request.Voice,
        }, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.IsConfigured)
            {
                Interlocked.Increment(ref _ttsFailureCount);
            }

            return new TtsSynthesizeResponse
            {
                Success = false,
                StatusMessage = result.StatusMessage,
                Voice = result.Voice,
                MimeType = result.MimeType,
            };
        }

        string? filePath = null;
        string? audioBase64 = null;
        if (request.WriteToDisk)
        {
            _options.EnsureDirectories();
            string extension = MimeToExtension(result.MimeType);
            string fileName = $"tts-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}{extension}";
            filePath = Path.Combine(_options.TtsDir, fileName);
            await File.WriteAllBytesAsync(filePath, result.Audio!, cancellationToken).ConfigureAwait(false);
            DirectoryRetention.Enforce(
                _options.TtsDir,
                _options.Tts.MaxStoredFiles,
                _options.Tts.MaxStoredAgeHours);
        }
        else
        {
            audioBase64 = Convert.ToBase64String(result.Audio!);
        }

        Interlocked.Increment(ref _ttsSuccessCount);

        return new TtsSynthesizeResponse
        {
            Success = true,
            StatusMessage = result.StatusMessage,
            Voice = result.Voice,
            MimeType = result.MimeType,
            PlaybackHint = DetermineSpeechPlaybackHint(result.MimeType, filePath),
            AudioBytes = result.Audio!.Length,
            FilePath = filePath,
            AudioBase64 = audioBase64,
        };
    }

    public async Task<AudioTranscribeResponse> TranscribeAudioAsync(
        AudioTranscribeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Interlocked.Increment(ref _asrCallCount);
        AudioTurnEndpointingReceipt endpointing = BuildAudioTurnEndpointingReceipt(request.Endpointing);
        if (endpointing.ClientVadSupplied)
        {
            Interlocked.Increment(ref _asrEndpointingReceiptCount);
            if (endpointing.BargeIn)
            {
                Interlocked.Increment(ref _asrBargeInCount);
            }

            if (string.Equals(endpointing.Status, "review", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _asrEndpointingReviewCount);
            }
        }

        AudioTranscriptionResult result = await _asrClient.TranscribeAsync(
            new AudioTranscriptionRequest
            {
                AudioBase64 = request.AudioBase64 ?? string.Empty,
                AudioMimeType = request.AudioMimeType,
                Language = request.Language,
                Prompt = request.Prompt,
            },
            cancellationToken).ConfigureAwait(false);

        if (result.Success)
        {
            Interlocked.Increment(ref _asrSuccessCount);
        }
        else if (result.IsConfigured)
        {
            Interlocked.Increment(ref _asrFailureCount);
        }

        if (result.Confidence.LogprobsReturned)
        {
            Interlocked.Increment(ref _asrConfidenceReceiptCount);
            if (string.Equals(result.Confidence.Status, "review", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _asrConfidenceReviewCount);
            }
        }

        if (result.Timing.VerboseJsonReturned)
        {
            Interlocked.Increment(ref _asrTimingReceiptCount);
            if (string.Equals(result.Timing.Status, "review", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _asrTimingReviewCount);
            }
        }

        if (result.Quality.QualityMetadataReturned)
        {
            Interlocked.Increment(ref _asrQualityReceiptCount);
            if (string.Equals(result.Quality.Status, "review", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _asrQualityReviewCount);
            }
        }

        if (!string.IsNullOrWhiteSpace(result.UpstreamRequestId))
        {
            Interlocked.Increment(ref _asrUpstreamRequestIdReceiptCount);
        }

        if (result.UpstreamProcessingMs is not null)
        {
            Interlocked.Increment(ref _asrUpstreamProcessingReceiptCount);
        }

        if (result.UpstreamQueueMs is not null ||
            result.UpstreamTimeToFirstTokenMs is not null ||
            result.UpstreamPrefillMs is not null ||
            result.UpstreamDecodeMs is not null)
        {
            Interlocked.Increment(ref _asrUpstreamPhaseTimingReceiptCount);
        }

        return new AudioTranscribeResponse
        {
            Success = result.Success,
            Transcript = result.Transcript,
            StatusMessage = result.StatusMessage,
            Model = result.Model,
            AudioBytes = result.AudioBytes,
            LatencyMs = result.LatencyMs,
            UpstreamRequestId = result.UpstreamRequestId,
            UpstreamProcessingMs = result.UpstreamProcessingMs,
            UpstreamQueueMs = result.UpstreamQueueMs,
            UpstreamTimeToFirstTokenMs = result.UpstreamTimeToFirstTokenMs,
            UpstreamPrefillMs = result.UpstreamPrefillMs,
            UpstreamDecodeMs = result.UpstreamDecodeMs,
            Endpointing = endpointing,
            Confidence = result.Confidence,
            Timing = result.Timing,
            Quality = result.Quality,
        };
    }

    private AudioTurnEndpointingReceipt BuildAudioTurnEndpointingReceipt(AudioTurnEndpointingInput? input)
    {
        int preSpeechPaddingTargetMs = Math.Max(0, _options.Asr.PreSpeechPaddingMs);
        int endpointSilenceTargetMs = Math.Max(1, _options.Asr.EndpointSilenceMs);
        int maxTurnDurationMs = Math.Max(1, _options.Asr.MaxTurnDurationMs);

        if (input is null)
        {
            return new AudioTurnEndpointingReceipt
            {
                ClientVadSupplied = false,
                Status = "not_supplied",
                EndpointReason = "not_supplied",
                PreSpeechPaddingTargetMs = preSpeechPaddingTargetMs,
                EndpointSilenceTargetMs = endpointSilenceTargetMs,
                MaxTurnDurationMs = maxTurnDurationMs,
                Flags = ["client_vad_missing"],
            };
        }

        List<string> flags = [];
        int? speechMs = NormalizeEndpointingMs(input.SpeechMs);
        int? leadingSilenceMs = NormalizeEndpointingMs(input.LeadingSilenceMs);
        int? trailingSilenceMs = NormalizeEndpointingMs(input.TrailingSilenceMs);
        int? totalTurnMs = SumEndpointingMs(speechMs, leadingSilenceMs, trailingSilenceMs);

        if (speechMs is null)
        {
            flags.Add("speech_duration_missing");
        }

        if (leadingSilenceMs is { } leading && leading < preSpeechPaddingTargetMs)
        {
            flags.Add("pre_speech_padding_below_target");
        }

        if (trailingSilenceMs is { } trailing)
        {
            if (trailing < endpointSilenceTargetMs)
            {
                flags.Add("endpoint_silence_below_target");
            }
            else if (trailing > endpointSilenceTargetMs * 4)
            {
                flags.Add("endpoint_silence_high_latency");
            }
        }
        else
        {
            flags.Add("endpoint_silence_missing");
        }

        if (totalTurnMs is { } total && total > maxTurnDurationMs)
        {
            flags.Add("turn_duration_over_cap");
        }

        string endpointReason = NormalizeEndpointReason(input.EndpointReason);
        if (string.Equals(endpointReason, "not_supplied", StringComparison.Ordinal))
        {
            flags.Add("endpoint_reason_missing");
        }

        return new AudioTurnEndpointingReceipt
        {
            ClientVadSupplied = true,
            Status = flags.Count == 0 ? "ready" : "review",
            EndpointReason = endpointReason,
            BargeIn = input.BargeIn,
            SpeechMs = speechMs,
            LeadingSilenceMs = leadingSilenceMs,
            TrailingSilenceMs = trailingSilenceMs,
            TotalTurnMs = totalTurnMs,
            PreSpeechPaddingTargetMs = preSpeechPaddingTargetMs,
            EndpointSilenceTargetMs = endpointSilenceTargetMs,
            MaxTurnDurationMs = maxTurnDurationMs,
            Flags = flags.ToArray(),
        };
    }

    private static int? NormalizeEndpointingMs(int? value) =>
        value is null ? null : Math.Max(0, value.Value);

    private static int? SumEndpointingMs(params int?[] values)
    {
        long total = 0;
        bool any = false;
        foreach (int? value in values)
        {
            if (value is not { } actual)
            {
                continue;
            }

            any = true;
            total += actual;
        }

        return any ? (int)Math.Min(total, int.MaxValue) : null;
    }

    private static string NormalizeEndpointReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "not_supplied";
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, 64)];
        int length = 0;
        foreach (char character in value.Trim())
        {
            if (length >= buffer.Length)
            {
                break;
            }

            if (!char.IsControl(character))
            {
                buffer[length++] = char.ToLowerInvariant(character);
            }
        }

        return length == 0 ? "not_supplied" : new string(buffer[..length]);
    }

    private static string MimeToExtension(string mime) => NormalizeMimeTypeForRouting(mime) switch
    {
        "audio/mpeg" => ".mp3",
        "audio/mp3" => ".mp3",
        "audio/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "audio/flac" => ".flac",
        "audio/pcm" => ".pcm",
        "audio/l16" => ".pcm",
        "audio/mp4" => ".m4a",
        "audio/x-m4a" => ".m4a",
        "audio/aac" => ".aac",
        "audio/wma" => ".wma",
        "audio/x-ms-wma" => ".wma",
        _ => ".wav",
    };

    private static string DetermineSpeechPlaybackHint(string mime, string? filePath)
    {
        string normalizedMime = NormalizeMimeTypeForRouting(mime);
        string extension = Path.GetExtension(filePath ?? string.Empty).Trim().ToLowerInvariant();

        if (extension == ".wav"
            || normalizedMime is "audio/wav" or "audio/wave" or "audio/x-wav")
        {
            return "sound_player";
        }

        if (extension is ".pcm"
            || normalizedMime is "audio/pcm" or "audio/l16")
        {
            return "raw_pcm";
        }

        if (extension is ".mp3" or ".m4a" or ".aac" or ".wma" or ".ogg" or ".opus" or ".flac"
            || normalizedMime is "audio/mpeg" or "audio/mp3" or "audio/mp4" or "audio/x-m4a" or "audio/aac" or "audio/wma" or "audio/x-ms-wma" or "audio/ogg" or "audio/opus" or "audio/flac")
        {
            return "media_player";
        }

        return "unknown";
    }

    public SessionPersistenceResult SaveSession() => _sessionPersistence.Save();

    public SessionPersistenceResult SaveSessionIfDirty() => _sessionPersistence.SaveIfDirty();

    public SessionPersistenceResult LoadSession() => _sessionPersistence.Load();

    public bool SessionIsDirty => _sessionPersistence.IsDirty;

    public DateTimeOffset? SessionLastSavedAtUtc => _sessionPersistence.LastSavedAtUtc;

    public BridgeGameAdapter Adapter { get; }

    public ConversationMemoryStore MemoryStore { get; }

    public NarrativePackService NarrativePacks { get; }

    public IReadOnlyList<CharacterRelationship> GetRelationships() => _relationshipTracker.Snapshot();

    public CharacterRelationship? GetRelationship(int characterId) => _relationshipTracker.TryGet(characterId);

    public RuntimeHealth GetHealth()
    {
        GameWorldSnapshot snapshot = Adapter.Snapshot;
        BridgeActivitySnapshot bridge = GetBridgeActivity();
        DirectoryActivitySnapshot directoryActivity = GetDirectoryActivitySnapshot();
        return BuildRuntimeHealth(snapshot, bridge, directoryActivity);
    }

    public InferencePerformanceSnapshot GetInferencePerformanceSnapshot() =>
        _inferencePerformance.GetSnapshot();

    private string GetInferenceCircuitState() =>
        _inferenceClient is HttpJsonInferenceClient compat
            ? compat.CircuitBreaker.State.ToString()
            : "NotInstrumented";

    private int GetInferenceCircuitFailures() =>
        _inferenceClient is HttpJsonInferenceClient compat
            ? compat.CircuitBreaker.ConsecutiveFailures
            : 0;

    private string GetInferenceActiveModel() =>
        _inferenceClient is IInferenceLaneMetadata metadata
            ? metadata.GetActiveModelId()
            : _options.Inference.Model;

    private string? GetInferenceActiveTierId() =>
        _inferenceClient is IInferenceLaneMetadata metadata
            ? metadata.GetActiveTierId()
            : null;

    private IReadOnlyList<string> GetInferenceLastSeenAvailableModels() =>
        _inferenceClient is IInferenceLaneMetadata metadata
            ? metadata.GetLastSeenAvailableModels()
            : Array.Empty<string>();

    public InferenceWarmupSnapshot GetInferenceWarmupSnapshot()
    {
        lock (_inferenceWarmupGate)
        {
            return BuildInferenceWarmupSnapshot(_inferenceWarmup);
        }
    }

    private void RecordLiveInferenceSuccess(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        lock (_inferenceWarmupGate)
        {
            _inferenceWarmup = BuildInferenceWarmupSnapshot(
                _inferenceWarmup,
                lastLiveInferenceAtUtc: DateTimeOffset.UtcNow,
                lastLiveInferenceModel: modelId.Trim());
        }
    }

    private TimeSpan GetKeepaliveFreshnessWindow() =>
        _options.Inference.WarmupIntervalSeconds > 0
            ? TimeSpan.FromSeconds(Math.Max(5, _options.Inference.WarmupIntervalSeconds))
            : TimeSpan.Zero;

    public async Task<InferenceWarmupSnapshot> WarmInferenceAsync(
        string reason,
        bool force,
        CancellationToken cancellationToken)
    {
        string normalizedReason = string.IsNullOrWhiteSpace(reason) ? "manual_api" : reason.Trim();

        if (!_options.Inference.EnableWarmup)
        {
            lock (_inferenceWarmupGate)
            {
                _inferenceWarmup = BuildInferenceWarmupSnapshot(
                    _inferenceWarmup,
                    status: "disabled",
                    lastReason: normalizedReason,
                    statusMessage: "Inference warmup is disabled by configuration.");
                return _inferenceWarmup;
            }
        }

        if (!_options.Inference.Enabled)
        {
            lock (_inferenceWarmupGate)
            {
                _inferenceWarmup = BuildInferenceWarmupSnapshot(
                    _inferenceWarmup,
                    status: "disabled",
                    lastReason: normalizedReason,
                    statusMessage: "Inference is disabled, so no warmup will run.");
                return _inferenceWarmup;
            }
        }

        await _inferenceWarmupSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            lock (_inferenceWarmupGate)
            {
                InferenceWarmupSnapshot current = BuildInferenceWarmupSnapshot(_inferenceWarmup);
                bool recentSuccess =
                    !force
                    && string.Equals(current.ActiveModel, current.LastWarmedModel, StringComparison.Ordinal)
                    && current.LastSuccessAtUtc.HasValue
                    && now - current.LastSuccessAtUtc.Value < InferenceWarmupMinimumGap;
                if (recentSuccess)
                {
                    _inferenceWarmup = BuildInferenceWarmupSnapshot(
                        current,
                        status: "ready",
                        lastReason: normalizedReason,
                        statusMessage:
                            $"Recent warmup for '{current.ActiveModel}' is still fresh; skipping duplicate request.");
                    return _inferenceWarmup;
                }

                TimeSpan keepaliveFreshnessWindow = GetKeepaliveFreshnessWindow();
                bool recentLiveInference =
                    !force
                    && keepaliveFreshnessWindow > TimeSpan.Zero
                    && string.Equals(normalizedReason, "periodic_keepalive", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(current.ActiveModel)
                    && string.Equals(current.ActiveModel, current.LastLiveInferenceModel, StringComparison.Ordinal)
                    && current.LastLiveInferenceAtUtc.HasValue
                    && now - current.LastLiveInferenceAtUtc.Value < keepaliveFreshnessWindow;
                if (recentLiveInference)
                {
                    TimeSpan age = now - current.LastLiveInferenceAtUtc!.Value;
                    _inferenceWarmup = BuildInferenceWarmupSnapshot(
                        current,
                        status: "ready",
                        lastReason: normalizedReason,
                        statusMessage:
                            $"Recent live inference for '{current.ActiveModel}' {Math.Max(0, age.TotalSeconds):0}s ago already kept the lane warm; skipping keepalive.");
                    return _inferenceWarmup;
                }

                _inferenceWarmup = BuildInferenceWarmupSnapshot(
                    current,
                    status: "warming",
                    lastReason: normalizedReason,
                    lastAttemptAtUtc: now,
                    attemptCount: current.AttemptCount + 1,
                    statusMessage: $"Warming active model '{current.ActiveModel}' for reason '{normalizedReason}'.");
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            InferenceResult result;
            string warmupTransport = "chat_completions";
            bool usedResidencyHint = false;
            try
            {
                var warmupPrompt = new InferencePrompt
                {
                    SystemPrompt = "You are handling a model warmup request. Reply with OK only.",
                    UserPrompt = "OK",
                    Temperature = 0f,
                    MaxTokens = Math.Clamp(_options.Inference.WarmupMaxTokens, 1, 32),
                    TopP = 0.1f,
                    PresencePenalty = 0f,
                    EnableThinking = false,
                    PreserveThinking = false,
                };

                if (_inferenceClient is HttpJsonInferenceClient compat)
                {
                    InferenceWarmupTransportResult transportResult = await compat.WarmAsync(warmupPrompt, cancellationToken)
                        .ConfigureAwait(false);
                    result = transportResult.Result;
                    warmupTransport = transportResult.Transport;
                    usedResidencyHint = transportResult.ResidencyHintApplied;
                }
                else
                {
                    result = await _inferenceClient.CompleteAsync(warmupPrompt, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                lock (_inferenceWarmupGate)
                {
                    InferenceWarmupSnapshot current = BuildInferenceWarmupSnapshot(_inferenceWarmup);
                    _inferenceWarmup = BuildInferenceWarmupSnapshot(
                        current,
                        status: "idle",
                        lastReason: normalizedReason,
                        statusMessage: "Inference warmup was cancelled before completion.");
                    return _inferenceWarmup;
                }
            }

            stopwatch.Stop();
            lock (_inferenceWarmupGate)
            {
                InferenceWarmupSnapshot current = BuildInferenceWarmupSnapshot(_inferenceWarmup);
                _inferenceWarmup = result.Success
                    ? BuildInferenceWarmupSnapshot(
                        current,
                        status: "ready",
                        lastReason: normalizedReason,
                        lastWarmedModel: current.ActiveModel,
                        warmupTransport: warmupTransport,
                        lastWarmupUsedResidencyHint: usedResidencyHint,
                        lastSuccessAtUtc: DateTimeOffset.UtcNow,
                        successCount: current.SuccessCount + 1,
                        lastLatencyMs: Math.Max(0, stopwatch.ElapsedMilliseconds),
                        statusMessage: BuildWarmupStatusMessage(
                            current.ActiveModel,
                            warmupTransport,
                            usedResidencyHint,
                            success: true))
                    : BuildInferenceWarmupSnapshot(
                        current,
                        status: "failed",
                        lastReason: normalizedReason,
                        lastWarmedModel: current.ActiveModel,
                        warmupTransport: warmupTransport,
                        lastWarmupUsedResidencyHint: usedResidencyHint,
                        lastFailureAtUtc: DateTimeOffset.UtcNow,
                        failureCount: current.FailureCount + 1,
                        lastLatencyMs: Math.Max(0, stopwatch.ElapsedMilliseconds),
                        statusMessage: BuildWarmupStatusMessage(
                            current.ActiveModel,
                            warmupTransport,
                            usedResidencyHint,
                            success: false,
                            upstreamStatus: result.StatusMessage));
                return _inferenceWarmup;
            }
        }
        finally
        {
            _inferenceWarmupSemaphore.Release();
        }
    }

    private DirectoryActivitySnapshot GetDirectoryActivitySnapshot()
    {
        long now = Environment.TickCount64;
        lock (_directoryActivityGate)
        {
            if (now < _nextDirectoryActivityRefreshTick)
            {
                return _directoryActivity;
            }

            _directoryActivity = new DirectoryActivitySnapshot
            {
                OutboxPendingCount = CountFiles(_options.BridgeOutboxDir, "*.json"),
                InboxPendingCount = CountFiles(_options.BridgeInboxDir, "*.json"),
                ScreenshotPendingCount = CountFiles(_options.BridgeScreenshotsDir, "*.png", "*.jpg", "*.jpeg"),
                ArchiveFileCount = CountFiles(_options.BridgeArchiveDir, "*"),
                FailedFileCount = CountFiles(_options.BridgeFailedDir, "*"),
            };
            _nextDirectoryActivityRefreshTick = now + (long)DirectoryActivitySnapshotTtl.TotalMilliseconds;
            return _directoryActivity;
        }
    }

    private void InvalidateDirectoryActivitySnapshot()
    {
        lock (_directoryActivityGate)
        {
            _nextDirectoryActivityRefreshTick = 0;
        }
    }

    private static int CountFiles(string directory, params string[] patterns)
    {
        int total = 0;
        foreach (string pattern in patterns)
        {
            total += CountFiles(directory, pattern);
        }

        return total;
    }

    private static int CountFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Count();
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public RuntimeWorldState GetWorldState()
    {
        GameWorldSnapshot snapshot = Adapter.Snapshot;
        BridgeActivitySnapshot bridge = GetBridgeActivity();
        return BuildWorldState(snapshot, bridge);
    }

    public UiProbeDiagnosticsSnapshot GetUiProbeDiagnostics(int candidateLimit = 8)
    {
        _options.EnsureDirectories();
        long now = Environment.TickCount64;

        lock (_uiProbeDiagnosticsGate)
        {
            if (_nextUiProbeDiagnosticsRefreshTick <= now)
            {
                PruneUiProbeDiagnosticsDirectory();
                _uiProbeDiagnostics = BuildUiProbeDiagnosticsSnapshot();
                _nextUiProbeDiagnosticsRefreshTick = now + (long)UiProbeDiagnosticsSnapshotTtl.TotalMilliseconds;
            }

            return CloneUiProbeDiagnostics(_uiProbeDiagnostics, candidateLimit);
        }
    }

    public DashboardSnapshot GetDashboardSnapshot(
        int relationshipLimit = 8,
        int logLimit = 10,
        int outboxLimit = 8)
    {
        var stopwatch = Stopwatch.StartNew();

        GameWorldSnapshot snapshot = Adapter.Snapshot;
        BridgeActivitySnapshot bridge = GetBridgeActivity();
        DirectoryActivitySnapshot directoryActivity = GetDirectoryActivitySnapshot();
        RuntimeHealth health = BuildRuntimeHealth(snapshot, bridge, directoryActivity);
        RuntimeWorldState world = BuildWorldState(snapshot, bridge);
        InferencePerformanceSnapshot inferencePerformance = GetInferencePerformanceSnapshot();
        CharacterRelationship[] relationships = _relationshipTracker.Snapshot()
            .OrderByDescending(relationship => relationship.LastInteractionUtc)
            .ThenByDescending(relationship => Math.Abs(relationship.Affinity))
            .Take(Math.Max(0, relationshipLimit))
            .ToArray();
        AdapterLogEntry[] logs = Adapter.RecentLogs
            .Reverse()
            .Take(Math.Max(0, logLimit))
            .ToArray();
        OutboxListing[] outbox = GetOutboxListings()
            .OrderByDescending(item => item.WrittenAtUtc)
            .Take(Math.Max(0, outboxLimit))
            .ToArray();

        stopwatch.Stop();

        return new DashboardSnapshot
        {
            Health = health,
            World = world,
            InferencePerformance = inferencePerformance,
            Relationships = relationships,
            Logs = logs,
            Outbox = outbox,
            RefreshedAtUtc = DateTimeOffset.UtcNow,
            ServerLatencyMs = Math.Max(0, stopwatch.ElapsedMilliseconds),
        };
    }

    private RuntimeWorldState BuildWorldState(
        GameWorldSnapshot snapshot,
        BridgeActivitySnapshot bridge) =>
        new()
        {
            Snapshot = snapshot,
            Bridge = bridge,
        };

    private RuntimeHealth BuildRuntimeHealth(
        GameWorldSnapshot snapshot,
        BridgeActivitySnapshot bridge,
        DirectoryActivitySnapshot directoryActivity)
    {
        PalLlmMetricsSnapshot metrics = _metrics.Snapshot();

        // Compute the Suggestions[] up front so the assembled snapshot
        // includes them. The builder is pure and cheap (no I/O, no global
        // state), so doing it inline keeps the hot health path simple.
        long inferenceSuccess = Interlocked.Read(ref _inferenceSuccessCount);
        long inferenceFailure = Interlocked.Read(ref _inferenceFailureCount);
        long visionCalls = Interlocked.Read(ref _visionCallCount);
        long visionFailures = Interlocked.Read(ref _visionFailureCount);
        long ttsCalls = Interlocked.Read(ref _ttsCallCount);
        long ttsSuccesses = Interlocked.Read(ref _ttsSuccessCount);
        long ttsFailures = Interlocked.Read(ref _ttsFailureCount);
        long asrCalls = Interlocked.Read(ref _asrCallCount);
        long asrSuccesses = Interlocked.Read(ref _asrSuccessCount);
        long asrFailures = Interlocked.Read(ref _asrFailureCount);
        long asrEndpointingReceipts = Interlocked.Read(ref _asrEndpointingReceiptCount);
        long asrBargeIns = Interlocked.Read(ref _asrBargeInCount);
        long asrEndpointingReviews = Interlocked.Read(ref _asrEndpointingReviewCount);
        long asrConfidenceReceipts = Interlocked.Read(ref _asrConfidenceReceiptCount);
        long asrConfidenceReviews = Interlocked.Read(ref _asrConfidenceReviewCount);
        long asrTimingReceipts = Interlocked.Read(ref _asrTimingReceiptCount);
        long asrTimingReviews = Interlocked.Read(ref _asrTimingReviewCount);
        long asrQualityReceipts = Interlocked.Read(ref _asrQualityReceiptCount);
        long asrQualityReviews = Interlocked.Read(ref _asrQualityReviewCount);
        long asrUpstreamRequestIdReceipts = Interlocked.Read(ref _asrUpstreamRequestIdReceiptCount);
        long asrUpstreamProcessingReceipts = Interlocked.Read(ref _asrUpstreamProcessingReceiptCount);
        long asrUpstreamPhaseTimingReceipts = Interlocked.Read(ref _asrUpstreamPhaseTimingReceiptCount);
        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(
            new HealthSuggestionInputs(
                LoadedPackCount: NarrativePacks.Count,
                InferenceConfigured: _options.Inference.Enabled,
                InferenceCircuitState: GetInferenceCircuitState(),
                InferenceSuccessCount: inferenceSuccess,
                InferenceFailureCount: inferenceFailure,
                VisionEnabled: _options.Vision.Enabled,
                VisionCallCount: visionCalls,
                VisionFailureCount: visionFailures,
                TtsEnabled: _options.Tts.Enabled,
                TtsCallCount: ttsCalls,
                TtsFailureCount: ttsFailures,
                BridgeEnabled: _options.Bridge.Enabled,
                BridgeBootCount: bridge.BootCount,
                BridgeEventCount: bridge.EventCount,
                InboxPendingCount: directoryActivity.InboxPendingCount,
                OutboxPendingCount: directoryActivity.OutboxPendingCount,
                FailedFileCount: directoryActivity.FailedFileCount,
                ScreenshotPendingCount: directoryActivity.ScreenshotPendingCount,
                AutomationEnabled: _options.Automation.Enabled,
                AutomationAllowedActionCount: _options.Automation.AllowedActions?.Count ?? 0));

        return new RuntimeHealth
        {
            AdapterName = Adapter.AdapterName,
            AdapterReady = snapshot.IsWorldLoaded,
            BridgeEnabled = _options.Bridge.Enabled,
            InferenceConfigured = _options.Inference.Enabled,
            InferenceModel = _options.Inference.Model,
            InferenceActiveModel = GetInferenceActiveModel(),
            InferenceActiveTierId = GetInferenceActiveTierId(),
            InferenceLastSeenAvailableModels = GetInferenceLastSeenAvailableModels().ToArray(),
            VisionEnabled = _options.Vision.Enabled,
            VisionModel = _options.Vision.Model,
            TtsEnabled = _options.Tts.Enabled,
            AsrEnabled = _options.Asr.Enabled,
            AutomationEnabled = _options.Automation.Enabled,
            Status = PalStatusLine.Current,
            InferenceSuccessCount = inferenceSuccess,
            InferenceFailureCount = inferenceFailure,
            InferenceBypassCount = Interlocked.Read(ref _inferenceBypassCount),
            FallbackReplyCount = Interlocked.Read(ref _fallbackReplyCount),
            CharacterCount = snapshot.Characters.Count,
            RememberedEntries = MemoryStore.Count,
            LoadedPackCount = NarrativePacks.Count,
            KnownBaseCount = snapshot.KnownBases.Count,
            BridgeEventCount = bridge.EventCount,
            BridgeBootCount = bridge.BootCount,
            LastBridgeEventType = bridge.LastEventType,
            LastBridgeEventAtUtc = bridge.LastEventAtUtc,
            RuntimeRoot = _options.RuntimeRoot,
            TrackedRelationshipCount = _relationshipTracker.Count,
            OutboxPendingCount = directoryActivity.OutboxPendingCount,
            TotalPromptTokens = Interlocked.Read(ref _totalPromptTokens),
            TotalCompletionTokens = Interlocked.Read(ref _totalCompletionTokens),
            TotalInferenceTokens = Interlocked.Read(ref _totalTokens),
            VisionCallCount = visionCalls,
            VisionFailureCount = visionFailures,
            TtsCallCount = ttsCalls,
            TtsSuccessCount = ttsSuccesses,
            TtsFailureCount = ttsFailures,
            AsrCallCount = asrCalls,
            AsrSuccessCount = asrSuccesses,
            AsrFailureCount = asrFailures,
            AsrEndpointingReceiptCount = asrEndpointingReceipts,
            AsrBargeInCount = asrBargeIns,
            AsrEndpointingReviewCount = asrEndpointingReviews,
            AsrConfidenceReceiptCount = asrConfidenceReceipts,
            AsrConfidenceReviewCount = asrConfidenceReviews,
            AsrTimingReceiptCount = asrTimingReceipts,
            AsrTimingReviewCount = asrTimingReviews,
            AsrQualityReceiptCount = asrQualityReceipts,
            AsrQualityReviewCount = asrQualityReviews,
            AsrUpstreamRequestIdReceiptCount = asrUpstreamRequestIdReceipts,
            AsrUpstreamProcessingReceiptCount = asrUpstreamProcessingReceipts,
            AsrUpstreamPhaseTimingReceiptCount = asrUpstreamPhaseTimingReceipts,
            InboxPendingCount = directoryActivity.InboxPendingCount,
            ScreenshotPendingCount = directoryActivity.ScreenshotPendingCount,
            ArchiveFileCount = directoryActivity.ArchiveFileCount,
            FailedFileCount = directoryActivity.FailedFileCount,
            SessionDirty = SessionIsDirty,
            SessionLastSavedAtUtc = SessionLastSavedAtUtc,
            NativeReadiness = BuildNativeReadinessSnapshot(bridge.LastBridgeBoot, bridge.UiProbeDiagnostics),
            BridgeLoop = bridge.LoopProof,
            RateLimitedCount = Interlocked.Read(ref _rateLimitedCount),
            InferenceCircuitState = GetInferenceCircuitState(),
            InferenceCircuitFailures = GetInferenceCircuitFailures(),
            InferenceWarmup = GetInferenceWarmupSnapshot(),
            FallbackStrategyCounts = metrics.FallbackStrategyCounts,
            ModelTierTransitionCounts = metrics.ModelTierTransitionCounts,
            ChatLatency = metrics.ChatLatency,
            Suggestions = suggestions,
        };
    }

    private void RecordInferenceOperation(
        InferenceResult? result,
        string fallbackRequestModel)
    {
        if (result is null || !result.IsConfigured)
        {
            return;
        }

        string requestModel = string.IsNullOrWhiteSpace(result.RequestModel)
            ? fallbackRequestModel
            : result.RequestModel;
        string providerName = string.IsNullOrWhiteSpace(result.ProviderName)
            ? "openai_compatible"
            : result.ProviderName;

        _inferencePerformance.Record(new InferencePerformanceSample(
            GenAiTelemetry.OperationChat,
            providerName,
            requestModel,
            string.IsNullOrWhiteSpace(result.ResponseModel) ? null : result.ResponseModel,
            result.Success,
            string.IsNullOrWhiteSpace(result.ErrorType) ? null : result.ErrorType,
            result.LatencyMs,
            result.Usage.PromptTokens,
            result.Usage.CompletionTokens,
            DateTimeOffset.UtcNow,
            string.IsNullOrWhiteSpace(result.SystemFingerprint) ? null : result.SystemFingerprint,
            string.IsNullOrWhiteSpace(result.ResponseId) ? null : result.ResponseId,
            result.FinishReasons,
            string.IsNullOrWhiteSpace(result.UpstreamRequestId) ? null : result.UpstreamRequestId,
            result.UpstreamProcessingMs,
            result.UpstreamQueueMs,
            result.UpstreamTimeToFirstTokenMs,
            result.UpstreamPrefillMs,
            result.UpstreamDecodeMs,
            result.Usage.CachedPromptTokens,
            result.Usage.PromptAudioTokens,
            result.Usage.CompletionReasoningTokens,
            result.Usage.CompletionAudioTokens,
            result.Usage.AcceptedPredictionTokens,
            result.Usage.RejectedPredictionTokens));
    }

    private InferenceWarmupSnapshot BuildInferenceWarmupSnapshot(
        InferenceWarmupSnapshot? current,
        string? status = null,
        string? lastReason = null,
        string? lastWarmedModel = null,
        string? warmupTransport = null,
        bool? lastWarmupUsedResidencyHint = null,
        string? statusMessage = null,
        DateTimeOffset? lastAttemptAtUtc = null,
        DateTimeOffset? lastSuccessAtUtc = null,
        DateTimeOffset? lastLiveInferenceAtUtc = null,
        string? lastLiveInferenceModel = null,
        DateTimeOffset? lastFailureAtUtc = null,
        long? attemptCount = null,
        long? successCount = null,
        long? failureCount = null,
        long? lastLatencyMs = null)
    {
        InferenceWarmupSnapshot baseline = current ?? new InferenceWarmupSnapshot();
        ResolvedInferenceResidency residency = InferenceResidencyPolicy.Resolve(_options.Inference);
        return new InferenceWarmupSnapshot
        {
            Enabled = _options.Inference.EnableWarmup,
            Status = status ?? baseline.Status,
            ActiveModel = GetInferenceActiveModel(),
            ActiveTierId = GetInferenceActiveTierId(),
            ResidencyProvider = residency.ProviderId,
            ResidencyTtlSeconds = residency.TtlSeconds,
            LastSeenAvailableModels = GetInferenceLastSeenAvailableModels().ToArray(),
            LastWarmedModel = lastWarmedModel ?? baseline.LastWarmedModel,
            LastReason = lastReason ?? baseline.LastReason,
            WarmupTransport = warmupTransport ?? baseline.WarmupTransport,
            LastWarmupUsedResidencyHint = lastWarmupUsedResidencyHint ?? baseline.LastWarmupUsedResidencyHint,
            StatusMessage = statusMessage ?? baseline.StatusMessage,
            LastAttemptAtUtc = lastAttemptAtUtc ?? baseline.LastAttemptAtUtc,
            LastSuccessAtUtc = lastSuccessAtUtc ?? baseline.LastSuccessAtUtc,
            LastLiveInferenceAtUtc = lastLiveInferenceAtUtc ?? baseline.LastLiveInferenceAtUtc,
            LastLiveInferenceModel = lastLiveInferenceModel ?? baseline.LastLiveInferenceModel,
            LastFailureAtUtc = lastFailureAtUtc ?? baseline.LastFailureAtUtc,
            AttemptCount = attemptCount ?? baseline.AttemptCount,
            SuccessCount = successCount ?? baseline.SuccessCount,
            FailureCount = failureCount ?? baseline.FailureCount,
            LastLatencyMs = lastLatencyMs ?? baseline.LastLatencyMs,
        };
    }

    private string BuildWarmupStatusMessage(
        string activeModel,
        string warmupTransport,
        bool usedResidencyHint,
        bool success,
        string? upstreamStatus = null)
    {
        string transport = string.IsNullOrWhiteSpace(warmupTransport) ? "chat_completions" : warmupTransport;
        string residencyHint = InferenceResidencyPolicy.DescribeHint(InferenceResidencyPolicy.Resolve(_options.Inference));

        if (success)
        {
            return usedResidencyHint && !string.IsNullOrWhiteSpace(residencyHint)
                ? $"Warmup completed for '{activeModel}' via {transport} using {residencyHint}."
                : $"Warmup completed for '{activeModel}' via {transport}.";
        }

        string detail = string.IsNullOrWhiteSpace(upstreamStatus)
            ? "Inference warmup failed."
            : upstreamStatus.Trim();

        return usedResidencyHint && !string.IsNullOrWhiteSpace(residencyHint)
            ? $"Warmup failed for '{activeModel}' via {transport} using {residencyHint}: {detail}"
            : $"Warmup failed for '{activeModel}' via {transport}: {detail}";
    }

    public IReadOnlyList<FeatureDescriptor> GetFeatures() => PalLlmFeatureCatalog.All;

    public IReadOnlyList<PackSummary> GetPacks() => NarrativePacks.GetSummaries();

    public IReadOnlyList<AdapterLogEntry> GetLogs() => Adapter.RecentLogs;

    public void ReloadPacks() => NarrativePacks.Reload();

    public void UpdateSnapshot(GameWorldSnapshot snapshot)
    {
        Adapter.UpdateSnapshot(snapshot);
        PalStatusLine.SetReady(PalTextCatalog.Get("status.snapshot"));
        PalStatusLine.NoteActivity();
    }

    public async Task<ChatResponse> ChatAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserMessage);
        request = BoundChatRequest(request, out bool userMessageTrimmed);

        string requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? $"chat-{Guid.NewGuid():N}"[..12]
            : request.RequestId.Trim();

        // Wall-clock stopwatch for the chat-latency histogram metric. Cheap
        // and always-on - the histogram records every turn regardless of
        // whether inference runs, so operators see real distribution even
        // on fallback-only deployments.
        long chatStartTicks = Stopwatch.GetTimestamp();

        // Distributed-tracing span. No-op (StartActivity returns null and
        // the `using` is a nop-dispose) unless an ActivityListener is
        // registered - the Sidecar's OTel wiring only registers one when
        // OTEL_EXPORTER_OTLP_ENDPOINT is set. See docs/OPERATIONS.md.
        using Activity? chatActivity = PalLlmTelemetry.Source.StartActivity(
            "pal.chat",
            ActivityKind.Internal);
        chatActivity?.SetTag("pal.request_id", requestId);
        if (request.CharacterId is int cid)
        {
            chatActivity?.SetTag("pal.character_id", cid);
        }
        if (!string.IsNullOrWhiteSpace(request.TaskTag))
        {
            chatActivity?.SetTag("pal.task_tag", request.TaskTag);
        }

        if (_options.Bridge.Enabled)
        {
            DrainInbox();
        }

        GameCharacterSnapshot? character = ResolveCharacter(request.CharacterId, request.CharacterName);
        GameWorldSnapshot snapshot = Adapter.Snapshot;
        PalTaskProfile taskProfile = PalTaskRouter.Resolve(request.TaskTag, request.UserMessage, request.Priority);
        string activeInferenceModel = (_inferenceClient as IInferenceLaneMetadata)?.GetActiveModelId() ?? _options.Inference.Model;
        InferenceExecutionProfile executionProfile = _inferenceExecutionPlanner.Plan(taskProfile, request, activeInferenceModel);
        int maxTokens = executionProfile.MaxTokens;
        float temperature = executionProfile.Temperature;

        int memoryRecallLimit = Math.Clamp(
            executionProfile.PromptBudget.MaxMemorySnippets,
            1,
            MaxMemoryRecallLimit);
        IReadOnlyList<ConversationMemoryMatch> memoryMatches =
            MemoryStore.Recall(request.UserMessage, character?.Id, memoryRecallLimit);
        IReadOnlyList<ConversationMemoryEntry> recentEntries =
            MemoryStore.GetRecent(_options.Fallback.RecentMemoryWindow, character?.Id);

        NarrativeCharacterProfile? lore = NarrativePacks.FindCharacterLore(
            character?.DisplayName ?? request.CharacterName);
        FallbackBehaviorContext fallbackContext = _fallbackBehaviorEngine.Analyze(
            request,
            taskProfile,
            snapshot,
            character,
            lore,
            memoryMatches,
            recentEntries);

        string userSpeaker = ResolveSpeakerName(character, request, "Player");
        string assistantSpeaker = ResolveSpeakerName(character, request, "Palworld");
        RememberChatIngress(new ChatIngressSnapshot
        {
            RequestId = requestId,
            CharacterName = assistantSpeaker,
            TaskTag = request.TaskTag,
            TaskKind = taskProfile.Kind.ToString(),
            Source = "sidecar",
            CapturedAtUtc = DateTimeOffset.UtcNow,
        });

        // Update per-character affinity BEFORE rendering the prompt so the prompt reflects
        // the latest read of the player's tone (arxiv 2504.13928 favorability pattern).
        CharacterRelationship? relationship = character is null
            ? null
            : _relationshipTracker.RecordInteraction(
                character.Id,
                assistantSpeaker,
                request.UserMessage,
                DateTimeOffset.UtcNow);

        // Optional vision augmentation. When the request carries a screenshot AND the
        // vision pipeline is wired up, run a terse HTTP vision call to extract what
        // the player can see and splice it into the system prompt. Kept optional so
        // the text-only path stays at its current latency budget.
        //
        // Fallback posture: when an image is provided but the vision model is
        // disabled OR the describe call fails, compose a deterministic scene
        // description from the live GameWorldSnapshot via SnapshotVisionFallback.
        // The player still gets situationally-aware replies - companions never
        // feel "blind" just because a multimodal model isn't running. This is
        // the vision counterpart to the 19 deterministic chat strategies.
        string visualContext = string.Empty;
        string visualContextSource = "none";
        if (!string.IsNullOrWhiteSpace(request.ImageBase64)
            && _options.Vision.UseForChatAugmentation
            && executionProfile.AllowLiveVisionAugmentation)
        {
            if (_visionOrchestrator is not null && _options.Vision.Enabled)
            {
                VisionDescribeResponse describe = await DescribeImageAsync(new VisionDescribeRequest
                {
                    ImageBase64 = request.ImageBase64!,
                    ImageMimeType = request.ImageMimeType,
                    MaxTokens = Math.Max(32, executionProfile.ChatVisionMaxTokens),
                    Temperature = 0.2f,
                }, cancellationToken).ConfigureAwait(false);
                if (describe.Success && !string.IsNullOrWhiteSpace(describe.Description))
                {
                    visualContext = TrimToLength(
                        describe.Description.Trim(),
                        executionProfile.PromptBudget.MaxVisualContextChars);
                    visualContextSource = "vision_model";
                }
            }

            if (string.IsNullOrEmpty(visualContext))
            {
                string fallback = SnapshotVisionFallback.Compose(snapshot);
                if (!string.IsNullOrEmpty(fallback))
                {
                    visualContext = fallback;
                    visualContextSource = "snapshot_fallback";
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            string fallback = SnapshotVisionFallback.Compose(snapshot);
            if (!string.IsNullOrEmpty(fallback))
            {
                visualContext = fallback;
                visualContextSource = "snapshot_fallback";
            }
        }
        chatActivity?.SetTag("pal.visual_context_source", visualContextSource);
        chatActivity?.SetTag("pal.inference_model", activeInferenceModel);
        chatActivity?.SetTag("pal.inference_profile", executionProfile.ProfileId);
        chatActivity?.SetTag("pal.inference_lane", executionProfile.Lane);
        if (executionProfile.EnableThinking.HasValue)
        {
            chatActivity?.SetTag("pal.inference_thinking_requested", executionProfile.EnableThinking.Value);
        }

        if (executionProfile.PreserveThinking.HasValue)
        {
            chatActivity?.SetTag("pal.inference_preserve_thinking_requested", executionProfile.PreserveThinking.Value);
        }

        string systemPrompt = BuildSystemPrompt(
            snapshot,
            character,
            lore,
            memoryMatches,
            relationship,
            request.TaskTag,
            _options.Fallback.PreferTaskFocus,
            visualContext,
            executionProfile.PromptBudget,
            visualContextSource);

        MemoryStore.Remember(
            character?.Id,
            userSpeaker,
            "user",
            request.UserMessage,
            request.TaskTag,
            "player_input");

        string? assistantMessage;
        string statusMessage;
        bool usedFallback = false;
        bool inferenceAttempted = false;
        bool inferenceBypassed = false;
        FallbackBehaviorDecision? fallbackDecision = null;
        string responsePath;
        InferenceResult? inferenceResult = null;

        string rateLimitBucket = character?.Id.ToString() ?? $"anon:{assistantSpeaker}";
        bool rateLimited = _chatRateLimiter.IsEnabled && !_chatRateLimiter.TryAcquire(rateLimitBucket);

        if (rateLimited)
        {
            // Runaway caller. Skip inference, keep the UX responsive by serving a
            // deterministic fallback instead. Tagged distinctly so operators can
            // see rate-limited traffic in the health counters. Wrapped in
            // EmergencyFallback.Guard so even a broken pack or null-reffed
            // strategy hands the player a canned acknowledgement instead of
            // crashing the chat turn.
            fallbackDecision = EmergencyFallback.Guard(
                () => _fallbackBehaviorEngine.Generate(fallbackContext),
                Environment.TickCount64);
            assistantMessage = TrimAssistantMessage(fallbackDecision.Message, out _);
            statusMessage = "Chat rate limit engaged - responding from deterministic fallback.";
            usedFallback = true;
            inferenceBypassed = true;
            responsePath = "rate_limited_fallback";

            RememberAssistantFallback(character?.Id, assistantSpeaker, request.TaskTag, fallbackDecision, "rate-limit");

            Interlocked.Increment(ref _rateLimitedCount);
            Interlocked.Increment(ref _fallbackReplyCount);

            PalStatusLine.SetReady(PalTextCatalog.Get("status.fast_path"));
            PalStatusLine.NoteActivity();
        }
        else if (_options.Inference.Enabled &&
            _fallbackBehaviorEngine.ShouldBypassInference(fallbackContext, out string bypassReason))
        {
            fallbackDecision = EmergencyFallback.Guard(
                () => _fallbackBehaviorEngine.Generate(fallbackContext),
                Environment.TickCount64);
            assistantMessage = TrimAssistantMessage(fallbackDecision.Message, out _);
            statusMessage = $"Inference bypassed by deterministic fast path ({bypassReason}).";
            usedFallback = true;
            inferenceBypassed = true;
            responsePath = "fallback_policy_bypass";

            RememberAssistantFallback(character?.Id, assistantSpeaker, request.TaskTag, fallbackDecision, "policy-bypass");

            Interlocked.Increment(ref _inferenceBypassCount);
            Interlocked.Increment(ref _fallbackReplyCount);

            PalStatusLine.SetReady(PalTextCatalog.Get("status.fast_path"));
            PalStatusLine.NoteActivity();
        }
        else
        {
            InferenceResult result = await _inferenceClient.CompleteAsync(new InferencePrompt
            {
                SystemPrompt = systemPrompt,
                UserPrompt = request.UserMessage.Trim(),
                Temperature = temperature,
                MaxTokens = maxTokens,
                TopP = executionProfile.TopP,
                PresencePenalty = executionProfile.PresencePenalty,
                EnableThinking = executionProfile.EnableThinking,
                PreserveThinking = executionProfile.PreserveThinking,
            }, cancellationToken).ConfigureAwait(false);
            inferenceResult = result;

            inferenceAttempted = result.IsConfigured;
            assistantMessage = TrimAssistantMessage(result.Content, out bool assistantMessageTrimmed);
            statusMessage = result.StatusMessage;
            responsePath = result.IsConfigured
                ? "inference_failed_no_fallback"
                : "inference_disabled_no_fallback";

            if (result.Success && !string.IsNullOrWhiteSpace(assistantMessage))
            {
                MemoryStore.Remember(
                    character?.Id,
                    assistantSpeaker,
                    "assistant",
                    assistantMessage,
                    request.TaskTag,
                    "assistant_reply");

                Interlocked.Increment(ref _inferenceSuccessCount);
                if (result.Usage.TotalTokens > 0 || result.Usage.PromptTokens > 0 || result.Usage.CompletionTokens > 0)
                {
                    Interlocked.Add(ref _totalPromptTokens, result.Usage.PromptTokens);
                    Interlocked.Add(ref _totalCompletionTokens, result.Usage.CompletionTokens);
                    Interlocked.Add(ref _totalTokens, result.Usage.TotalTokens);
                }

                RecordLiveInferenceSuccess(activeInferenceModel);
                responsePath = "live_inference";
                if (assistantMessageTrimmed)
                {
                    statusMessage = AppendStatusNotice(
                        statusMessage,
                        $"Reply trimmed to {AssistantMessageHardCapChars} characters by local response cap.");
                }

                PalStatusLine.SetReady(PalTextCatalog.Get("status.ready"));
                PalStatusLine.NoteActivity();
            }
            else
            {
                if (result.IsConfigured)
                {
                    Interlocked.Increment(ref _inferenceFailureCount);
                }

                bool shouldUseFallback =
                    _options.Fallback.Enabled &&
                    ((!result.IsConfigured && _options.Fallback.UseWhenInferenceDisabled) ||
                     (result.IsConfigured && _options.Fallback.UseWhenInferenceFails));

                if (shouldUseFallback)
                {
                    fallbackDecision = EmergencyFallback.Guard(
                        () => _fallbackBehaviorEngine.Generate(fallbackContext),
                        Environment.TickCount64);

                    assistantMessage = TrimAssistantMessage(fallbackDecision.Message, out _);
                    statusMessage = string.IsNullOrWhiteSpace(result.StatusMessage)
                        ? $"Fallback strategy '{fallbackDecision.StrategyId}' is active."
                        : $"{result.StatusMessage} Falling back to '{fallbackDecision.StrategyId}'.";
                    usedFallback = true;
                    responsePath = result.IsConfigured
                        ? "fallback_inference_failed"
                        : "fallback_inference_disabled";

                    string fallbackSourceTag = result.IsConfigured
                        ? "inference-failure"
                        : "inference-disabled";
                    RememberAssistantFallback(character?.Id, assistantSpeaker, request.TaskTag, fallbackDecision, fallbackSourceTag);

                    Interlocked.Increment(ref _fallbackReplyCount);

                    PalStatusLine.SetReady(PalTextCatalog.Get("status.fallback"));
                    PalStatusLine.NoteActivity();
                }
                else
                {
                    PalStatusLine.Set(result.StatusMessage);
                }
            }
        }

        RecordInferenceOperation(inferenceResult, activeInferenceModel);
        if (userMessageTrimmed)
        {
            statusMessage = AppendStatusNotice(
                statusMessage,
                $"UserMessage trimmed to {ChatRequest.UserMessageMaxLength} characters by local input cap.");
        }

        FallbackBehaviorDecision presentationAnchor = fallbackDecision ?? EmergencyFallback.Guard(
            () => _fallbackBehaviorEngine.Generate(fallbackContext),
            Environment.TickCount64);

        // After each exchange is logged, see whether accumulated importance in the
        // recent memory window warrants consolidation into a single high-salience
        // reflection entry. Runs only when enabled in config so benchmarks and
        // deterministic tests can opt out.
        if (_options.Fallback.EnableReflection)
        {
            _reflectionService.TryReflect(character?.Id, assistantSpeaker);
        }

        PresentationCuePlan presentation;

        // Phase-6 automation primitive. Intent is purely advisory - the runtime
        // never acts on it. Opt-in via AutomationOptions, gated further by an
        // operator allowlist of action types. Null when disabled or unmapped.
        ActionIntent? actionIntent = ActionIntentPlanner.Plan(
            fallbackContext,
            presentationAnchor,
            _options.Automation);

        presentation = _presentationCuePlanner.Build(
            fallbackContext,
            presentationAnchor,
            responsePath,
            usedFallback,
            assistantMessage,
            actionIntent?.Priority ?? 0);

        SpeechArtifact? speech = await TryBuildSpeechArtifactAsync(
            requestId,
            assistantMessage,
            presentation,
            cancellationToken).ConfigureAwait(false);

        // Return-channel write. UE4SS (or any other consumer) can watch
        // Bridge/Outbox and render the assistant message + presentation plan
        // in-game without calling back into the sidecar. Async so the chat
        // response can return to the caller before disk I/O finishes - the
        // outbox is advisory and must never delay the hot path.
        if (_options.Bridge.OutboxEnabled && !string.IsNullOrWhiteSpace(assistantMessage))
        {
            await WriteOutboxReplyAsync(new OutboxChatReply
            {
                RequestId = requestId,
                Action = _options.Automation.EmitToOutbox ? actionIntent : null,
                CharacterId = character?.Id,
                CharacterName = assistantSpeaker,
                TaskTag = request.TaskTag,
                TaskKind = taskProfile.Kind.ToString(),
                AssistantMessage = assistantMessage ?? string.Empty,
                ResponsePath = responsePath,
                UsedFallback = usedFallback,
                FallbackStrategy = fallbackDecision?.StrategyId,
                FallbackPhase = fallbackDecision?.Phase.ToString(),
                Speech = speech,
                Presentation = presentation,
            }, cancellationToken).ConfigureAwait(false);
        }

        chatActivity?.SetTag("pal.response_path", responsePath);
        chatActivity?.SetTag("pal.used_fallback", usedFallback);
        if (fallbackDecision is not null)
        {
            chatActivity?.SetTag("pal.fallback_strategy", fallbackDecision.StrategyId);
            _metrics.RecordFallbackStrategy(fallbackDecision.StrategyId);
        }
        chatActivity?.SetTag("pal.inference_attempted", inferenceAttempted);

        _metrics.RecordChatLatency(Stopwatch.GetElapsedTime(chatStartTicks));

        // Pass-19 + Pass-21 + Pass-22 advisory: infer the Duo task
        // kind from the raw user message, ask the Pass-8
        // DuoOrchestratorPlanner which cooperation pattern it would
        // pick, and compute the concrete executable role chain via
        // the Pass-22 ChatDispatchPlanner. All three are observational
        // today — chat dispatch still uses the single-lane
        // `_inferenceClient` — but the dispatch decision is stable
        // enough that a future pass can flip the passthrough to
        // invoke the chain recorded here without a breaking contract
        // change.
        //
        // Every branch is wrapped in a catch so a quirk in the
        // planner, inferer, or dispatch planner can never fail a
        // chat turn.
        string? inferredTaskKind = null;
        string? cooperationPattern = null;
        IReadOnlyList<string> dispatchedRoleChain = Array.Empty<string>();
        string? dispatchMode = null;
        try
        {
            DuoTaskKind kind = Inference.ChatTaskKindInferer
                .Infer(request.UserMessage, request.TaskTag);
            inferredTaskKind = kind.ToString();

            try
            {
                DuoRiskLevel risk = kind switch
                {
                    DuoTaskKind.HighRisk => DuoRiskLevel.High,
                    DuoTaskKind.Audit => DuoRiskLevel.Medium,
                    DuoTaskKind.CommandRouting => DuoRiskLevel.Medium,
                    _ => DuoRiskLevel.Low,
                };
                DuoPlan plan = _duoPlanner.Plan(new DuoPlanRequest
                {
                    Kind = kind,
                    Risk = risk,
                    Hardware = DuoHardwareTier.Standard,
                });
                cooperationPattern = plan.Pattern.ToString();

                try
                {
                    ModelRoleCoverage coverage = _roleRegistry.GetCoverage();
                    ChatDispatchDecision decision = ChatDispatchPlanner.Decide(plan.Pattern, coverage);
                    dispatchedRoleChain = decision.Roles;
                    dispatchMode = decision.Mode;
                }
                catch
                {
                    dispatchedRoleChain = Array.Empty<string>();
                    dispatchMode = null;
                }
            }
            catch
            {
                cooperationPattern = null;
            }
        }
        catch
        {
            // Inferer is deterministic + total, but this catch keeps
            // ChatAsync from ever breaking on advisory work.
            inferredTaskKind = null;
            cooperationPattern = null;
        }

        return new ChatResponse
        {
            RequestId = requestId,
            CharacterName = assistantSpeaker,
            TaskKind = taskProfile.Kind.ToString(),
            InferredTaskKind = inferredTaskKind,
            CooperationPattern = cooperationPattern,
            DispatchedRoleChain = dispatchedRoleChain,
            DispatchMode = dispatchMode,
            InferenceModel = activeInferenceModel,
            InferenceProfileId = executionProfile.ProfileId,
            InferenceLane = executionProfile.Lane,
            ThinkingRequested = executionProfile.EnableThinking,
            InferenceEnabled = _options.Inference.Enabled,
            InferenceAttempted = inferenceAttempted,
            InferenceBypassed = inferenceBypassed,
            StatusMessage = statusMessage,
            ResponsePath = responsePath,
            MaxTokens = maxTokens,
            VisualContextSource = visualContextSource,
            SystemPrompt = systemPrompt,
            AssistantMessage = assistantMessage,
            UsedFallback = usedFallback,
            FallbackStrategy = fallbackDecision?.StrategyId,
            FallbackPhase = fallbackDecision?.Phase.ToString(),
            FallbackSignals = fallbackDecision?.Signals ?? Array.Empty<string>(),
            Presentation = presentation,
            Speech = speech,
            Action = actionIntent,
            MemoryMatches = memoryMatches.Select(match => match.Entry.Content).ToArray(),
        };
    }

    /// Processes screenshots in Bridge/Screenshots through the vision world-state
    /// extractor and merges the result into the snapshot. Exposed as a single method
    /// so the background watcher and tests can share one code path. By default the
    /// method drains the full queue; the watcher can pass a smaller maxFiles budget
    /// to keep long-running sessions responsive under backlog.
    public async Task<ScreenshotIngestResult> ProcessScreenshotsAsync(
        CancellationToken cancellationToken,
        int maxFiles = int.MaxValue)
    {
        if (_visionOrchestrator is null || !_options.Vision.Enabled)
        {
            return new ScreenshotIngestResult();
        }

        _options.EnsureDirectories();
        PrunePendingScreenshots();
        string[] files = GetSortedFiles(_options.BridgeScreenshotsDir, "*.png", "*.jpg", "*.jpeg")
            .Take(ClampPositiveBudget(maxFiles))
            .ToArray();

        int processed = 0;
        int failed = 0;
        foreach (string file in files)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var fileInfo = new FileInfo(file);
                if (!fileInfo.Exists)
                {
                    continue;
                }

                if (fileInfo.Length == 0)
                {
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                    continue;
                }

                if (fileInfo.Length > _options.Vision.MaxImageBytes)
                {
                    Adapter.Logger.Warning(
                        $"Screenshot {fileInfo.Name} exceeded the configured cap of {_options.Vision.MaxImageBytes} bytes.");
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                    continue;
                }

                BoundedBase64FileReader.Base64ReadResult readResult =
                    await BoundedBase64FileReader.TryReadAsync(file, _options.Vision.MaxImageBytes, cancellationToken)
                        .ConfigureAwait(false);
                if (!readResult.Succeeded)
                {
                    Adapter.Logger.Warning(
                        $"Screenshot {fileInfo.Name} {DescribeScreenshotReadFailure(readResult.FailureCode, _options.Vision.MaxImageBytes)}");
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                    continue;
                }

                string mime = file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
                VisionWorldStateResponse response = await ExtractWorldStateAsync(new VisionWorldStateRequest
                {
                    ImageBase64 = readResult.Base64!,
                    ImageMimeType = mime,
                    ApplyToSnapshot = true,
                    Hint = $"screenshot-ingest:{Path.GetFileName(file)}",
                }, cancellationToken).ConfigureAwait(false);

                if (response.Success)
                {
                    Archive(file, _options.BridgeArchiveDir);
                    processed++;
                }
                else
                {
                    Adapter.Logger.Warning($"Screenshot {Path.GetFileName(file)} failed world-state extract: {response.StatusMessage}");
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Adapter.Logger.Warning($"Screenshot {Path.GetFileName(file)} processing errored: {DescribeScreenshotProcessingFailure(ex)}");
                try
                {
                    Archive(file, _options.BridgeFailedDir);
                }
                catch (IOException)
                {
                    // best-effort
                }

                failed++;
            }
        }

        return new ScreenshotIngestResult
        {
            ProcessedCount = processed,
            FailedCount = failed,
        };
    }

    /// Prunes the pending screenshot queue so the bridge cannot accumulate an
    /// arbitrarily stale image backlog while vision is disabled or slower than the
    /// screenshot producer. This keeps disk usage bounded and preserves low-latency
    /// processing by preferring fresher screenshots over very old ones.
    public int PrunePendingScreenshots()
    {
        _options.EnsureDirectories();
        int removed = DirectoryRetention.Enforce(
            _options.BridgeScreenshotsDir,
            _options.Vision.PendingScreenshotMaxFiles,
            _options.Vision.PendingScreenshotMaxAgeHours,
            "*.png",
            "*.jpg",
            "*.jpeg");
        if (removed > 0)
        {
            InvalidateDirectoryActivitySnapshot();
        }

        return removed;
    }

    public IReadOnlyList<OutboxListing> GetOutboxListings()
    {
        if (!Directory.Exists(_options.BridgeOutboxDir))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(_options.BridgeOutboxDir, "*.json", SearchOption.TopDirectoryOnly)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new OutboxListing
                    {
                        FileName = info.Name,
                        WrittenAtUtc = info.LastWriteTimeUtc,
                        SizeBytes = info.Length,
                    };
                })
                .OrderBy(listing => listing.WrittenAtUtc)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    public int ClearOutbox()
    {
        if (!Directory.Exists(_options.BridgeOutboxDir))
        {
            return 0;
        }

        int removed = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_options.BridgeOutboxDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Adapter.Logger.Warning($"Failed to clear outbox entry {Path.GetFileName(file)}: {DescribeOutboxEntryDeleteFailure(ex)}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Adapter.Logger.Warning($"Failed to enumerate outbox entries for clearing: {DescribeOutboxEnumerationFailure(ex)}");
        }

        if (removed > 0)
        {
            InvalidateDirectoryActivitySnapshot();
        }

        return removed;
    }

    private async Task<SpeechArtifact?> TryBuildSpeechArtifactAsync(
        string requestId,
        string? assistantMessage,
        PresentationCuePlan presentation,
        CancellationToken cancellationToken)
    {
        if (!_options.Tts.Enabled || string.IsNullOrWhiteSpace(assistantMessage))
        {
            return null;
        }

        TtsSynthesizeResponse response = await SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = assistantMessage,
            Voice = ResolveTtsVoice(presentation.Audio),
            WriteToDisk = true,
        }, cancellationToken).ConfigureAwait(false);

        if (!response.Success || string.IsNullOrWhiteSpace(response.FilePath))
        {
            if (!string.IsNullOrWhiteSpace(response.StatusMessage))
            {
                Adapter.Logger.Warning($"Chat-linked TTS skipped for {requestId}: {response.StatusMessage}");
            }

            return null;
        }

        return new SpeechArtifact
        {
            RequestId = requestId,
            Delivery = "local_file",
            Voice = response.Voice,
            VoicePrint = presentation.Audio.VoicePrint,
            SubtitleStyle = presentation.Audio.SubtitleStyle,
            MimeType = response.MimeType,
            PlaybackHint = response.PlaybackHint,
            AudioBytes = response.AudioBytes,
            FilePath = response.FilePath,
        };
    }

    private string ResolveTtsVoice(AudioCuePlan audio)
    {
        TtsOptions tts = _options.Tts;
        string voicePrint = audio.VoicePrint?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(voicePrint))
        {
            return tts.DefaultVoice;
        }

        if (ContainsAny(voicePrint, "whisper", "hushed", "quiet"))
        {
            return FirstNonEmpty(tts.WhisperVoice, tts.DefaultVoice);
        }

        if (ContainsAny(voicePrint, "directive", "rally", "lead", "sentry", "protector"))
        {
            return FirstNonEmpty(tts.UrgentVoice, tts.SteadyVoice, tts.DefaultVoice);
        }

        if (ContainsAny(voicePrint, "cozy", "easygoing", "reassurer", "companion"))
        {
            return FirstNonEmpty(tts.WarmVoice, tts.DefaultVoice);
        }

        if (ContainsAny(voicePrint, "guide", "planner", "handler", "quartermaster", "reset", "wingmate", "weathered", "scout"))
        {
            return FirstNonEmpty(tts.SteadyVoice, tts.DefaultVoice);
        }

        return tts.DefaultVoice;
    }

    private static bool ContainsAny(string value, params string[] tokens)
    {
        foreach (string token in tokens)
        {
            if (value.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string NormalizeMimeTypeForRouting(string? mime)
    {
        if (string.IsNullOrWhiteSpace(mime))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> trimmed = mime.AsSpan().Trim();
        int separatorIndex = trimmed.IndexOf(';');
        if (separatorIndex >= 0)
        {
            trimmed = trimmed[..separatorIndex].TrimEnd();
        }

        return trimmed.ToString().ToLowerInvariant();
    }

    private async Task WriteOutboxReplyAsync(OutboxChatReply payload, CancellationToken cancellationToken)
    {
        try
        {
            _options.EnsureDirectories();
            var envelope = new OutboxEnvelope
            {
                EventType = "chat_reply",
                Source = "palllm",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = payload,
            };

            string fileName = $"chat_reply-{envelope.TimestampUtc:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}.json";
            string path = Path.Combine(_options.BridgeOutboxDir, fileName);
            await using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        envelope,
                        OutboxJsonContext.OutboxEnvelope,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            // Enforce retention synchronously so an unattended outbox can never grow
            // unbounded. Cap is configurable; default 100 files / 24 hours.
            DirectoryRetention.Enforce(
                _options.BridgeOutboxDir,
                _options.Bridge.OutboxMaxFiles,
                _options.Bridge.OutboxMaxAgeHours,
                "*.json");
            RememberOutboxReply(payload, envelope.TimestampUtc, envelope.Source);
            InvalidateDirectoryActivitySnapshot();
        }
        catch (OperationCanceledException)
        {
            // Caller cancelled - do not treat as an outbox failure.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Outbox failures must not break the chat response - log and move on.
            Adapter.Logger.Warning($"Outbox write failed: {DescribeOutboxWriteFailure(ex)}");
        }
    }

    private static readonly JsonSerializerOptions OutboxSerializerOptions = PalLlmDomainJsonOptions.Create(static options =>
    {
        options.WriteIndented = false;
        options.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

    private static readonly PalLlmDomainJsonSerializerContext OutboxJsonContext = new(OutboxSerializerOptions);

    public IReadOnlyList<ConversationMemoryMatch> RecallMemory(MemoryRecallRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return MemoryStore.Recall(request.Query, request.CharacterId, request.Limit);
    }

    /// Manually triggers the reflection pass for a specific character. Returns the new
    /// reflection entry if one was produced, or <c>null</c> when the importance
    /// threshold has not been reached. Exposed so tools and tests can drive reflection
    /// without waiting for the implicit post-chat trigger.
    public ConversationMemoryEntry? Reflect(int? characterId, string characterName) =>
        _reflectionService.TryReflect(characterId, characterName);

    public bool IsVisionEnabled => _visionOrchestrator is not null && _options.Vision.Enabled;

    /// Freeform scene description. Returns a failed response (not an exception) when
    /// vision is disabled so callers can treat it as an optional sensor.
    public async Task<VisionDescribeResponse> DescribeImageAsync(
        VisionDescribeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_visionOrchestrator is null || !_options.Vision.Enabled)
        {
            return new VisionDescribeResponse
            {
                Success = false,
                StatusMessage = "Vision is disabled or no vision client is registered.",
                Model = _options.Vision.Model,
            };
        }

        Interlocked.Increment(ref _visionCallCount);
        VisionDescribeResponse response =
            await _visionOrchestrator.DescribeAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.Success)
        {
            Interlocked.Increment(ref _visionFailureCount);
        }

        return response;
    }

    /// Structured world-state extraction. When ApplyToSnapshot is set on the request,
    /// the runtime also folds the result into the live snapshot so the next fallback /
    /// prompt building pass reacts to what the model saw.
    public async Task<VisionWorldStateResponse> ExtractWorldStateAsync(
        VisionWorldStateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_visionOrchestrator is null || !_options.Vision.Enabled)
        {
            return new VisionWorldStateResponse
            {
                Success = false,
                StatusMessage = "Vision is disabled or no vision client is registered.",
                Model = _options.Vision.Model,
            };
        }

        Interlocked.Increment(ref _visionCallCount);
        (VisionWorldStateResponse response, VisionWorldStateSnapshot? parsed) =
            await _visionOrchestrator.ExtractWorldStateAsync(request, cancellationToken).ConfigureAwait(false);

        if (!response.Success)
        {
            Interlocked.Increment(ref _visionFailureCount);
            return response;
        }

        if (!request.ApplyToSnapshot || parsed is null)
        {
            return response;
        }

        GameWorldSnapshot current = Adapter.Snapshot;
        GameWorldSnapshot merged = _visionOrchestrator.MergeIntoSnapshot(current, parsed);
        UpdateSnapshot(merged);

        return new VisionWorldStateResponse
        {
            Success = response.Success,
            StatusMessage = response.StatusMessage + " Applied to snapshot.",
            Model = response.Model,
            LatencyMs = response.LatencyMs,
            RawContent = response.RawContent,
            State = response.State,
            Applied = true,
        };
    }

    public BridgeDrainResult DrainInbox(int maxFiles = int.MaxValue)
    {
        if (!_options.Bridge.Enabled)
        {
            return new BridgeDrainResult();
        }

        lock (_drainGate)
        {
            _options.EnsureDirectories();
            string[] files = GetSortedFiles(_options.BridgeInboxDir, "*.json")
                .Take(ClampPositiveBudget(maxFiles))
                .ToArray();

            int processed = 0;
            int failed = 0;
            foreach (string file in files)
            {
                try
                {
                    BoundedJsonFileReader.JsonReadResult<BridgeEventEnvelope> readResult =
                        TryReadBridgeEventEnvelope(file, _options.Bridge.MaxInboxEventBytes);
                    if (!readResult.Succeeded || readResult.Value is null)
                    {
                        Adapter.Logger.Warning(
                            $"Bridge event processing failed for {Path.GetFileName(file)}: {DescribeBridgeInboxReadFailure(readResult.FailureCode, _options.Bridge.MaxInboxEventBytes)}");
                        Archive(file, _options.BridgeFailedDir);
                        failed++;
                        continue;
                    }

                    BridgeEventEnvelope envelope = readResult.Value;
                    ProcessBridgeEvent(envelope);
                    if (_options.Bridge.ArchiveProcessedEvents)
                    {
                        Archive(file, _options.BridgeArchiveDir);
                    }
                    else
                    {
                        File.Delete(file);
                        InvalidateDirectoryActivitySnapshot();
                    }

                    processed++;
                }
                catch (Exception ex)
                {
                    Adapter.Logger.Warning($"Bridge event processing failed for {Path.GetFileName(file)}: {DescribeBridgeProcessingFailure(ex)}");
                    Archive(file, _options.BridgeFailedDir);
                    failed++;
                }
            }

            if (processed > 0)
            {
                PalStatusLine.SetReady(PalTextCatalog.Get("status.bridge"));
                PalStatusLine.NoteActivity();
            }

            return new BridgeDrainResult
            {
                ProcessedCount = processed,
                FailedCount = failed,
            };
        }
    }

    private static int ClampPositiveBudget(int value) =>
        value <= 0 ? 0 : value;

    private static BoundedJsonFileReader.JsonReadResult<BridgeEventEnvelope> TryReadBridgeEventEnvelope(string file, int maxBytes) =>
        BoundedJsonFileReader.TryRead(
            file,
            maxBytes,
            stream => JsonSerializer.Deserialize(stream, BridgeJsonContext.BridgeEventEnvelope));

    private static string DescribeScreenshotReadFailure(
        BoundedBase64FileReader.Base64ReadFailureCode? failureCode,
        int maxBytes) =>
        failureCode switch
        {
            BoundedBase64FileReader.Base64ReadFailureCode.Oversized =>
                $"exceeded the configured cap of {Math.Max(1_024, maxBytes)} bytes while being read.",
            BoundedBase64FileReader.Base64ReadFailureCode.Empty =>
                "was empty when the bounded reader opened it.",
            _ =>
                "could not be read through the bounded sequential reader.",
        };

    private static string DescribeScreenshotProcessingFailure(Exception exception) =>
        exception switch
        {
            JsonException => "vision output JSON could not be applied.",
            IOException or UnauthorizedAccessException => "local screenshot file handling failed.",
            ArgumentException or FormatException or InvalidOperationException or NotSupportedException =>
                "vision output could not be applied.",
            _ => "runtime screenshot handling failed.",
        };

    private static string DescribeBridgeInboxReadFailure(
        BoundedJsonFileReader.JsonReadFailureCode? failureCode,
        int maxBytes) =>
        failureCode switch
        {
            BoundedJsonFileReader.JsonReadFailureCode.Oversized =>
                $"bridge inbox event exceeded the configured cap of {Math.Max(1_024, maxBytes)} bytes.",
            BoundedJsonFileReader.JsonReadFailureCode.Unreadable =>
                "bridge inbox event could not be read.",
            _ =>
                "bridge inbox event JSON was malformed.",
        };

    private static string DescribeOutboxEntryDeleteFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "access was denied while deleting the file.",
            _ => "the file could not be deleted.",
        };

    private static string DescribeOutboxEnumerationFailure(Exception exception) =>
        exception switch
        {
            UnauthorizedAccessException => "outbox directory access was denied.",
            _ => "outbox directory could not be enumerated.",
        };

    private static string DescribeOutboxWriteFailure(Exception exception) =>
        exception switch
        {
            DirectoryNotFoundException => "reply envelope directory was missing.",
            PathTooLongException => "reply envelope path exceeded platform limits.",
            UnauthorizedAccessException => "reply envelope access was denied.",
            _ => "reply envelope could not be written.",
        };

    private static string DescribeBridgeProcessingFailure(Exception exception) =>
        exception switch
        {
            JsonException => "bridge event payload was invalid for its declared type.",
            IOException or UnauthorizedAccessException => "bridge event archive handling failed.",
            ArgumentException or FormatException or InvalidOperationException or NotSupportedException =>
                "bridge event payload could not be applied.",
            _ => "bridge event handler hit an unexpected runtime failure.",
        };

    private static T DeserializeBridgePayload<T>(
        JsonElement payload,
        JsonTypeInfo<T> jsonTypeInfo,
        T fallback) =>
        JsonSerializer.Deserialize(payload, jsonTypeInfo) ?? fallback;

    private void ProcessBridgeEvent(BridgeEventEnvelope envelope)
    {
        RecordBridgeActivity(envelope);

        switch (envelope.EventType)
        {
            case "bridge_boot":
            {
                BridgeBootPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.BridgeBootPayload, new BridgeBootPayload());
                RememberBridgeBoot(payload);
                string summary = string.IsNullOrWhiteSpace(payload.Compat)
                    ? $"status={payload.Status}"
                    : payload.Compat;
                Adapter.Logger.Info($"Bridge boot heartbeat received from {envelope.Source}: {summary}");
                break;
            }

            case "chat_message":
            {
                ChatHookPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.ChatHookPayload, new ChatHookPayload());
                if (!string.IsNullOrWhiteSpace(payload.Message))
                {
                    MemoryStore.Remember(null, payload.Sender, "bridge", payload.Message, "chat_message", payload.Category);
                }

                Adapter.Logger.Info($"Bridge chat captured from {payload.Sender}.");
                break;
            }

            case "snapshot":
            {
                GameWorldSnapshot payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.GameWorldSnapshot, new GameWorldSnapshot());
                UpdateSnapshot(payload);
                Adapter.Logger.Info("Bridge snapshot applied.");
                break;
            }

            case "base_discovered":
            {
                BaseDiscoveredPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.BaseDiscoveredPayload, new BaseDiscoveredPayload());
                string message = string.IsNullOrWhiteSpace(payload.BaseId)
                    ? "A Palworld base was discovered by the bridge."
                    : $"Base discovered: {payload.BaseId}{FormatAreaRange(payload.AreaRange)}";
                MemoryStore.Remember(
                    null,
                    "World",
                    "system",
                    message,
                    "base_discovered",
                    $"bridge-source:{envelope.Source}");
                PromoteDiscoveredBase(payload, envelope.TimestampUtc, envelope.Source);
                Adapter.Logger.Info($"Bridge discovered base '{payload.BaseId}'.");
                break;
            }

            case "combat_start":
            case "combat_end":
            {
                CombatEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.CombatEventPayload, new CombatEventPayload());
                string phase = string.IsNullOrWhiteSpace(payload.Phase)
                    ? (envelope.EventType == "combat_end" ? "end" : "start")
                    : payload.Phase;
                string opponent = string.IsNullOrWhiteSpace(payload.Opponent) ? "unknown opponents" : payload.Opponent;
                string location = string.IsNullOrWhiteSpace(payload.Location) ? string.Empty : $" at {payload.Location}";
                string message = phase.Equals("end", StringComparison.OrdinalIgnoreCase)
                    ? $"Combat ended against {opponent}{location}."
                    : $"Combat started against {opponent}{location}.";
                MemoryStore.Remember(null, "World", "system", message, envelope.EventType, $"opponent:{opponent}");
                AppendWorldEvent($"{envelope.EventType}:{opponent}", envelope.TimestampUtc, envelope.Source);
                Adapter.Logger.Info($"Bridge {envelope.EventType} captured: {opponent}.");
                break;
            }

            case "pal_status":
            {
                PalStatusEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.PalStatusEventPayload, new PalStatusEventPayload());
                string palLabel = string.IsNullOrWhiteSpace(payload.PalName)
                    ? (string.IsNullOrWhiteSpace(payload.Species) ? "A Pal" : payload.Species)
                    : payload.PalName;
                string change = string.IsNullOrWhiteSpace(payload.Change) ? "state changed" : payload.Change;
                string note = string.IsNullOrWhiteSpace(payload.Note) ? string.Empty : $" - {payload.Note}";
                string message = $"{palLabel} status: {change}{note}.";
                List<string> tags = ["pal_status", $"change:{change}"];
                AppendBridgeTraceTags(tags, payload.RequestId, payload.SourceStrategy);
                RememberActionFeedback(
                    "pal_status",
                    payload.RequestId,
                    payload.SourceStrategy,
                    message,
                    envelope.TimestampUtc,
                    envelope.Source);
                MemoryStore.Remember(null, palLabel, "system", message, tags.ToArray());
                AppendWorldEvent($"pal_status:{palLabel}:{change}", envelope.TimestampUtc, envelope.Source);
                Adapter.Logger.Info($"Bridge pal_status captured: {palLabel} {change}.");
                break;
            }

            case "production":
            {
                ProductionEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.ProductionEventPayload, new ProductionEventPayload());
                string baseLabel = string.IsNullOrWhiteSpace(payload.BaseId) ? "the base" : payload.BaseId;
                string station = string.IsNullOrWhiteSpace(payload.Station) ? string.Empty : $" at {payload.Station}";
                string status = string.IsNullOrWhiteSpace(payload.Status) ? "completed" : payload.Status;
                string note = string.IsNullOrWhiteSpace(payload.Note) ? string.Empty : $" - {payload.Note}";
                string message = payload.Quantity > 0 && !string.IsNullOrWhiteSpace(payload.Item)
                    ? $"Production {status}{station} in {baseLabel}: {payload.Quantity}x {payload.Item}{note}."
                    : $"Production {status}{station} in {baseLabel}{note}.";
                List<string> tags = ["production", $"base:{baseLabel}"];
                AppendBridgeTraceTags(tags, payload.RequestId, payload.SourceStrategy);
                RememberActionFeedback(
                    "production",
                    payload.RequestId,
                    payload.SourceStrategy,
                    message,
                    envelope.TimestampUtc,
                    envelope.Source);
                MemoryStore.Remember(null, "World", "system", message, tags.ToArray());
                ApplyProductionToSnapshot(payload, envelope.TimestampUtc, envelope.Source);
                AppendWorldEvent($"production:{baseLabel}:{status}", envelope.TimestampUtc, envelope.Source);
                Adapter.Logger.Info($"Bridge production captured for base '{baseLabel}'.");
                break;
            }

            case "travel":
            {
                TravelEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.TravelEventPayload, new TravelEventPayload());
                string origin = string.IsNullOrWhiteSpace(payload.Origin) ? "unknown" : payload.Origin;
                string destination = string.IsNullOrWhiteSpace(payload.Destination) ? "unknown" : payload.Destination;
                string waypoint = string.IsNullOrWhiteSpace(payload.Waypoint) ? string.Empty : $" via {payload.Waypoint}";
                string mode = string.IsNullOrWhiteSpace(payload.Mode) ? "on_foot" : payload.Mode;
                string message = $"Travel ({mode}): {origin} -> {destination}{waypoint}.";
                if (!string.IsNullOrWhiteSpace(payload.Note))
                {
                    message = message[..^1] + $" - {payload.Note}.";
                }

                List<string> tags = ["travel", $"mode:{mode}"];
                AppendBridgeTraceTags(tags, payload.RequestId, payload.SourceStrategy);
                RememberActionFeedback(
                    "travel",
                    payload.RequestId,
                    payload.SourceStrategy,
                    message,
                    envelope.TimestampUtc,
                    envelope.Source);
                if (ShouldPersistTravelMemory(payload))
                {
                    MemoryStore.Remember(null, "World", "system", message, tags.ToArray());
                }

                ApplyTravelToSnapshot(payload, envelope.TimestampUtc, envelope.Source);
                AppendWorldEvent($"travel:{origin}->{destination}", envelope.TimestampUtc, envelope.Source);
                Adapter.Logger.Info($"Bridge travel captured: {origin} to {destination}.");
                break;
            }

            case "weather_change":
            {
                WeatherEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.WeatherEventPayload, new WeatherEventPayload());
                string weather = string.IsNullOrWhiteSpace(payload.Weather) ? "weather shift" : payload.Weather;
                string biome = string.IsNullOrWhiteSpace(payload.Biome) ? string.Empty : $" in {payload.Biome}";
                string severity = string.IsNullOrWhiteSpace(payload.Severity) ? "mild" : payload.Severity;
                string message = $"Weather now {weather}{biome} ({severity}).";
                MemoryStore.Remember(null, "World", "system", message, "weather_change", $"severity:{severity}");
                ApplyWeatherToSnapshot(payload);
                AppendWorldEvent($"weather_change:{weather}", envelope.TimestampUtc, envelope.Source);
                Adapter.Logger.Info($"Bridge weather_change captured: {weather}.");
                break;
            }

            case "raid":
            {
                RaidEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.RaidEventPayload, new RaidEventPayload());
                string baseLabel = string.IsNullOrWhiteSpace(payload.BaseId) ? "a base" : payload.BaseId;
                string faction = string.IsNullOrWhiteSpace(payload.Faction) ? "hostiles" : payload.Faction;
                string phase = string.IsNullOrWhiteSpace(payload.Phase) ? "incoming" : payload.Phase;
                string count = payload.AttackerCount.HasValue ? $" with {payload.AttackerCount.Value} attackers" : string.Empty;
                string note = string.IsNullOrWhiteSpace(payload.Note) ? string.Empty : $" - {payload.Note}";
                string message = $"Raid {phase} against {baseLabel} by {faction}{count}{note}.";
                MemoryStore.Remember(null, "World", "system", message, "raid", $"base:{baseLabel}", $"faction:{faction}");
                AppendWorldEvent($"raid:{baseLabel}:{phase}", envelope.TimestampUtc, envelope.Source);
                Adapter.Logger.Info($"Bridge raid captured against '{baseLabel}'.");
                break;
            }

            case "ui_probe":
            {
                UiProbeEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.UiProbeEventPayload, new UiProbeEventPayload());
                UiProbeSnapshot probe = BuildUiProbeSnapshot(payload, envelope);
                RememberUiProbe(probe);
                string reason = string.IsNullOrWhiteSpace(probe.Reason) ? "unspecified" : probe.Reason;
                string summary = string.IsNullOrWhiteSpace(probe.Summary)
                    ? $"{probe.ObservedWidgetCount} widgets observed, {probe.ActiveWidgetCount} active."
                    : probe.Summary;
                Adapter.Logger.Info($"Bridge ui_probe captured ({reason}): {summary}");
                break;
            }

            case "reply_delivery":
            {
                ReplyDeliveryEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.ReplyDeliveryEventPayload, new ReplyDeliveryEventPayload());
                ReplyDeliverySnapshot delivery = BuildReplyDeliverySnapshot(payload, envelope);
                RememberReplyDelivery(delivery);
                string requestLabel = string.IsNullOrWhiteSpace(delivery.RequestId) ? "untracked" : delivery.RequestId;
                string result = delivery.Rendered ? "rendered" : "suppressed";
                string surface = string.IsNullOrWhiteSpace(delivery.Surface) ? "unknown-surface" : delivery.Surface;
                Adapter.Logger.Info($"Bridge reply_delivery captured for {requestLabel}: {result} via {surface}.");
                break;
            }

            case "speech_playback":
            {
                SpeechPlaybackEventPayload payload = DeserializeBridgePayload(envelope.Payload, BridgeJsonContext.SpeechPlaybackEventPayload, new SpeechPlaybackEventPayload());
                SpeechPlaybackSnapshot playback = BuildSpeechPlaybackSnapshot(payload, envelope);
                RememberSpeechPlayback(playback);
                string requestLabel = string.IsNullOrWhiteSpace(playback.RequestId) ? "untracked" : playback.RequestId;
                string result = playback.Started ? "started" : "skipped";
                string mode = string.IsNullOrWhiteSpace(playback.PlaybackMode) ? "unknown-mode" : playback.PlaybackMode;
                Adapter.Logger.Info($"Bridge speech_playback captured for {requestLabel}: {result} via {mode}.");
                break;
            }

            default:
                Adapter.Logger.Warning($"Unknown bridge event type '{envelope.EventType}' was ignored.");
                break;
        }
    }

    private static void AppendBridgeTraceTags(List<string> tags, string? requestId, string? sourceStrategy)
    {
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            tags.Add($"request:{requestId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(sourceStrategy))
        {
            tags.Add($"strategy:{sourceStrategy.Trim()}");
        }
    }

    private static bool ShouldPersistTravelMemory(TravelEventPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.RequestId))
        {
            return true;
        }

        return !string.Equals(payload.SourceStrategy, "live-movement", StringComparison.OrdinalIgnoreCase);
    }

    /// Appends a short marker into the snapshot's RecentEvents without disturbing the
    /// rest of world state. Used by combat/travel/weather/raid handlers so downstream
    /// prompt rendering can reference the latest live events.
    private void AppendWorldEvent(string marker, DateTimeOffset timestamp, string source)
    {
        GameWorldSnapshot current = Adapter.Snapshot;
        List<string> events = current.RecentEvents
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();

        events.RemoveAll(value => string.Equals(value, marker, StringComparison.OrdinalIgnoreCase));
        events.Insert(0, marker);
        while (events.Count > 12)
        {
            events.RemoveAt(events.Count - 1);
        }

        UpdateSnapshot(new GameWorldSnapshot
        {
            Source = string.IsNullOrWhiteSpace(source) ? current.Source : source,
            WorldName = current.WorldName,
            IsWorldLoaded = current.IsWorldLoaded,
            CurrentTick = current.CurrentTick,
            TicksPerHour = current.TicksPerHour,
            TicksPerDay = current.TicksPerDay,
            CapturedAtUtc = timestamp,
            Biome = current.Biome,
            Weather = current.Weather,
            TimeOfDay = current.TimeOfDay,
            ThreatLevel = current.ThreatLevel,
            AlertLevel = current.AlertLevel,
            PlayerHealthFraction = current.PlayerHealthFraction,
            PlayerStaminaFraction = current.PlayerStaminaFraction,
            PlayerHungerFraction = current.PlayerHungerFraction,
            CurrentObjective = current.CurrentObjective,
            LastTravel = current.LastTravel,
            LastProduction = current.LastProduction,
            IsInBase = current.IsInBase,
            ActiveBaseIds = [.. current.ActiveBaseIds],
            KnownBases = [.. current.KnownBases],
            NearbyHostiles = [.. current.NearbyHostiles],
            NearbyFriendlies = [.. current.NearbyFriendlies],
            NearbyResources = [.. current.NearbyResources],
            RecentEvents = events,
            Characters = [.. current.Characters],
        });
    }

    private void ApplyWeatherToSnapshot(WeatherEventPayload payload)
    {
        GameWorldSnapshot current = Adapter.Snapshot;
        string newWeather = string.IsNullOrWhiteSpace(payload.Weather) ? current.Weather : payload.Weather;
        string newBiome = string.IsNullOrWhiteSpace(payload.Biome) ? current.Biome : payload.Biome;
        if (string.Equals(newWeather, current.Weather, StringComparison.OrdinalIgnoreCase)
            && string.Equals(newBiome, current.Biome, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        UpdateSnapshot(new GameWorldSnapshot
        {
            Source = current.Source,
            WorldName = current.WorldName,
            IsWorldLoaded = current.IsWorldLoaded,
            CurrentTick = current.CurrentTick,
            TicksPerHour = current.TicksPerHour,
            TicksPerDay = current.TicksPerDay,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Biome = newBiome,
            Weather = newWeather,
            TimeOfDay = current.TimeOfDay,
            ThreatLevel = current.ThreatLevel,
            AlertLevel = current.AlertLevel,
            PlayerHealthFraction = current.PlayerHealthFraction,
            PlayerStaminaFraction = current.PlayerStaminaFraction,
            PlayerHungerFraction = current.PlayerHungerFraction,
            CurrentObjective = current.CurrentObjective,
            LastTravel = current.LastTravel,
            LastProduction = current.LastProduction,
            IsInBase = current.IsInBase,
            ActiveBaseIds = [.. current.ActiveBaseIds],
            KnownBases = [.. current.KnownBases],
            NearbyHostiles = [.. current.NearbyHostiles],
            NearbyFriendlies = [.. current.NearbyFriendlies],
            NearbyResources = [.. current.NearbyResources],
            RecentEvents = [.. current.RecentEvents],
            Characters = [.. current.Characters],
        });
    }

    private void ApplyProductionToSnapshot(ProductionEventPayload payload, DateTimeOffset timestamp, string source)
    {
        GameWorldSnapshot current = Adapter.Snapshot;
        UpdateSnapshot(new GameWorldSnapshot
        {
            Source = string.IsNullOrWhiteSpace(source) ? current.Source : source,
            WorldName = current.WorldName,
            IsWorldLoaded = current.IsWorldLoaded,
            CurrentTick = current.CurrentTick,
            TicksPerHour = current.TicksPerHour,
            TicksPerDay = current.TicksPerDay,
            CapturedAtUtc = timestamp,
            Biome = current.Biome,
            Weather = current.Weather,
            TimeOfDay = current.TimeOfDay,
            ThreatLevel = current.ThreatLevel,
            AlertLevel = current.AlertLevel,
            PlayerHealthFraction = current.PlayerHealthFraction,
            PlayerStaminaFraction = current.PlayerStaminaFraction,
            PlayerHungerFraction = current.PlayerHungerFraction,
            CurrentObjective = current.CurrentObjective,
            LastTravel = current.LastTravel,
            LastProduction = new ProductionStatusSnapshot
            {
                BaseId = string.IsNullOrWhiteSpace(payload.BaseId) ? "the base" : payload.BaseId,
                Station = payload.Station ?? string.Empty,
                Item = payload.Item ?? string.Empty,
                Quantity = Math.Max(0, payload.Quantity),
                Status = string.IsNullOrWhiteSpace(payload.Status) ? "completed" : payload.Status,
                Note = payload.Note ?? string.Empty,
                RequestId = payload.RequestId ?? string.Empty,
                SourceStrategy = payload.SourceStrategy ?? string.Empty,
                Source = source ?? string.Empty,
                CapturedAtUtc = timestamp,
            },
            IsInBase = current.IsInBase,
            ActiveBaseIds = [.. current.ActiveBaseIds],
            KnownBases = [.. current.KnownBases],
            NearbyHostiles = [.. current.NearbyHostiles],
            NearbyFriendlies = [.. current.NearbyFriendlies],
            NearbyResources = [.. current.NearbyResources],
            RecentEvents = [.. current.RecentEvents],
            Characters = [.. current.Characters],
        });
    }

    private void ApplyTravelToSnapshot(TravelEventPayload payload, DateTimeOffset timestamp, string source)
    {
        GameWorldSnapshot current = Adapter.Snapshot;
        UpdateSnapshot(new GameWorldSnapshot
        {
            Source = string.IsNullOrWhiteSpace(source) ? current.Source : source,
            WorldName = current.WorldName,
            IsWorldLoaded = current.IsWorldLoaded,
            CurrentTick = current.CurrentTick,
            TicksPerHour = current.TicksPerHour,
            TicksPerDay = current.TicksPerDay,
            CapturedAtUtc = timestamp,
            Biome = current.Biome,
            Weather = current.Weather,
            TimeOfDay = current.TimeOfDay,
            ThreatLevel = current.ThreatLevel,
            AlertLevel = current.AlertLevel,
            PlayerHealthFraction = current.PlayerHealthFraction,
            PlayerStaminaFraction = current.PlayerStaminaFraction,
            PlayerHungerFraction = current.PlayerHungerFraction,
            CurrentObjective = current.CurrentObjective,
            LastTravel = new TravelStatusSnapshot
            {
                Origin = string.IsNullOrWhiteSpace(payload.Origin) ? "unknown" : payload.Origin,
                Destination = string.IsNullOrWhiteSpace(payload.Destination) ? "unknown" : payload.Destination,
                Waypoint = payload.Waypoint ?? string.Empty,
                Mode = string.IsNullOrWhiteSpace(payload.Mode) ? "on_foot" : payload.Mode,
                Note = payload.Note ?? string.Empty,
                RequestId = payload.RequestId ?? string.Empty,
                SourceStrategy = payload.SourceStrategy ?? string.Empty,
                Source = source ?? string.Empty,
                CapturedAtUtc = timestamp,
            },
            LastProduction = current.LastProduction,
            IsInBase = current.IsInBase,
            ActiveBaseIds = [.. current.ActiveBaseIds],
            KnownBases = [.. current.KnownBases],
            NearbyHostiles = [.. current.NearbyHostiles],
            NearbyFriendlies = [.. current.NearbyFriendlies],
            NearbyResources = [.. current.NearbyResources],
            RecentEvents = [.. current.RecentEvents],
            Characters = [.. current.Characters],
        });
    }

    private BridgeActivitySnapshot GetBridgeActivity()
    {
        UiProbeDiagnosticsSnapshot diagnostics = GetUiProbeDiagnostics(candidateLimit: 6);

        lock (_bridgeGate)
        {
            return new BridgeActivitySnapshot
            {
                EventCount = _bridgeEventCount,
                BootCount = _bridgeBootCount,
                LastEventType = _lastBridgeEventType,
                LastEventAtUtc = _lastBridgeEventAtUtc,
                LastEventSource = _lastBridgeEventSource,
                LastBridgeBoot = CloneBridgeBootPayload(_lastBridgeBoot),
                LastUiProbe = CloneUiProbe(_lastUiProbe),
                UiProbeDiagnostics = diagnostics,
                LoopProof = BuildBridgeLoopProof(
                    _lastChatIngress,
                    _lastOutboxReply,
                    _lastReplyDelivery,
                    _lastActionFeedback,
                    _lastSpeechPlayback),
            };
        }
    }

    private void RecordBridgeActivity(BridgeEventEnvelope envelope)
    {
        lock (_bridgeGate)
        {
            _bridgeEventCount++;
            if (string.Equals(envelope.EventType, "bridge_boot", StringComparison.OrdinalIgnoreCase))
            {
                _bridgeBootCount++;
            }

            _lastBridgeEventType = envelope.EventType ?? string.Empty;
            _lastBridgeEventSource = envelope.Source ?? string.Empty;
            _lastBridgeEventAtUtc = envelope.TimestampUtc;
        }
    }

    private void RememberUiProbe(UiProbeSnapshot probe)
    {
        lock (_bridgeGate)
        {
            _lastUiProbe = CloneUiProbe(probe);
        }

        InvalidateUiProbeDiagnostics();
        PruneUiProbeDiagnosticsDirectory();
    }

    private void RememberChatIngress(ChatIngressSnapshot ingress)
    {
        lock (_bridgeGate)
        {
            _lastChatIngress = CloneChatIngress(ingress);
        }
    }

    private void RememberOutboxReply(OutboxChatReply payload, DateTimeOffset writtenAtUtc, string source)
    {
        lock (_bridgeGate)
        {
            _lastOutboxReply = new OutboxReplyTraceSnapshot
            {
                RequestId = payload.RequestId ?? string.Empty,
                CharacterName = payload.CharacterName ?? string.Empty,
                TaskTag = payload.TaskTag ?? string.Empty,
                TaskKind = payload.TaskKind ?? string.Empty,
                ResponsePath = payload.ResponsePath ?? string.Empty,
                UsedFallback = payload.UsedFallback,
                FallbackStrategy = payload.FallbackStrategy ?? string.Empty,
                ActionType = payload.Action?.Type ?? string.Empty,
                SpeechExpected = payload.Speech is not null,
                SpeechDelivery = payload.Speech?.Delivery ?? string.Empty,
                SpeechMimeType = payload.Speech?.MimeType ?? string.Empty,
                SpeechPlaybackHint = payload.Speech?.PlaybackHint ?? string.Empty,
                Source = source ?? string.Empty,
                WrittenAtUtc = writtenAtUtc,
            };
        }
    }

    private void RememberReplyDelivery(ReplyDeliverySnapshot delivery)
    {
        lock (_bridgeGate)
        {
            _lastReplyDelivery = CloneReplyDelivery(delivery);
        }
    }

    private void RememberSpeechPlayback(SpeechPlaybackSnapshot playback)
    {
        lock (_bridgeGate)
        {
            _lastSpeechPlayback = CloneSpeechPlayback(playback);
        }
    }

    private void RememberActionFeedback(
        string eventType,
        string? requestId,
        string? sourceStrategy,
        string summary,
        DateTimeOffset capturedAtUtc,
        string source)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        lock (_bridgeGate)
        {
            _lastActionFeedback = new BridgeActionFeedbackSnapshot
            {
                RequestId = requestId.Trim(),
                EventType = eventType ?? string.Empty,
                SourceStrategy = sourceStrategy?.Trim() ?? string.Empty,
                Summary = summary ?? string.Empty,
                Source = source ?? string.Empty,
                CapturedAtUtc = capturedAtUtc,
            };
        }
    }

    private static UiProbeSnapshot BuildUiProbeSnapshot(UiProbeEventPayload payload, BridgeEventEnvelope envelope)
    {
        UiProbeWidgetEntry[] widgets = SanitizeUiProbeWidgetEntries(payload.Widgets, 12);

        return new UiProbeSnapshot
        {
            Reason = payload.Reason ?? string.Empty,
            Summary = payload.Summary ?? string.Empty,
            DumpPath = payload.DumpPath ?? string.Empty,
            ObservedWidgetCount = Math.Max(0, payload.ObservedWidgetCount),
            ActiveWidgetCount = Math.Max(0, payload.ActiveWidgetCount),
            Source = envelope.Source ?? string.Empty,
            CapturedAtUtc = envelope.TimestampUtc,
            Widgets = widgets,
        };
    }

    private static ReplyDeliverySnapshot BuildReplyDeliverySnapshot(ReplyDeliveryEventPayload payload, BridgeEventEnvelope envelope) =>
        new()
        {
            RequestId = payload.RequestId?.Trim() ?? string.Empty,
            Speaker = payload.Speaker ?? string.Empty,
            ResponsePath = payload.ResponsePath ?? string.Empty,
            StrategyId = payload.StrategyId ?? string.Empty,
            Phase = payload.Phase ?? string.Empty,
            UsedFallback = payload.UsedFallback,
            Rendered = payload.Rendered,
            Surface = payload.Surface ?? string.Empty,
            CardLabel = payload.CardLabel ?? string.Empty,
            CardIndex = Math.Max(0, payload.CardIndex),
            CardCount = Math.Max(0, payload.CardCount),
            Note = payload.Note ?? string.Empty,
            Source = envelope.Source ?? string.Empty,
            CapturedAtUtc = envelope.TimestampUtc,
        };

    private static SpeechPlaybackSnapshot BuildSpeechPlaybackSnapshot(SpeechPlaybackEventPayload payload, BridgeEventEnvelope envelope)
    {
        int sampleRateHz = Math.Clamp(payload.SampleRateHz, 0, 768000);
        int channelCount = Math.Clamp(payload.ChannelCount, 0, 64);
        int bitsPerSample = Math.Clamp(payload.BitsPerSample, 0, 128);
        long audioDataBytes = Math.Clamp(payload.AudioDataBytes, 0L, 4_294_967_295L);
        int blockAlignBytes = Math.Clamp(payload.BlockAlignBytes, 0, 65_535);
        long frameCount = Math.Clamp(payload.FrameCount, 0L, 4_294_967_295L);
        int blockRemainderBytes = Math.Clamp(payload.BlockRemainderBytes, 0, 65_535);
        int supersededSpeechAgeMs = Math.Clamp(payload.SupersededSpeechAgeMs, 0, 86_400_000);
        long supersededSpeechBufferedMs = Math.Clamp(payload.SupersededSpeechBufferedMs, 0L, 4_294_967_295L);
        long supersededSpeechRemainingMs = supersededSpeechBufferedMs > supersededSpeechAgeMs
            ? Math.Clamp(supersededSpeechBufferedMs - supersededSpeechAgeMs, 0L, 4_294_967_295L)
            : 0L;
        if (audioDataBytes > 0 && blockAlignBytes > 0)
        {
            frameCount = Math.Clamp(audioDataBytes / blockAlignBytes, 0L, 4_294_967_295L);
            blockRemainderBytes = Math.Clamp((int)(audioDataBytes % blockAlignBytes), 0, 65_535);
        }

        var mixerQueue = BuildNativeMixerQueueReceipt(sampleRateHz, frameCount);

        return new SpeechPlaybackSnapshot
        {
            RequestId = SanitizeBridgeReceiptText(payload.RequestId, 128),
            Started = payload.Started,
            ArtifactBytes = Math.Max(0L, payload.ArtifactBytes),
            AttemptCount = Math.Clamp(payload.AttemptCount, 0, 100),
            ElapsedMs = Math.Max(0, payload.ElapsedMs),
            PlaybackSequence = Math.Clamp(payload.PlaybackSequence, 0, 1_000_000),
            SupersededRequestId = SanitizeBridgeReceiptText(payload.SupersededRequestId, 128),
            SupersededSpeechCount = Math.Clamp(payload.SupersededSpeechCount, 0, 1_000_000),
            SupersededSpeechAgeMs = supersededSpeechAgeMs,
            SupersededSpeechBufferedMs = supersededSpeechBufferedMs,
            SupersededSpeechRemainingMs = supersededSpeechRemainingMs,
            CancellationMode = SanitizeBridgeReceiptCode(payload.CancellationMode, 64),
            SampleRateHz = sampleRateHz,
            ChannelCount = channelCount,
            BitsPerSample = bitsPerSample,
            DurationMs = Math.Clamp(payload.DurationMs, 0, 86_400_000),
            ByteRate = Math.Clamp(payload.ByteRate, 0L, 4_294_967_295L),
            BlockAlignBytes = blockAlignBytes,
            AudioDataBytes = audioDataBytes,
            FrameCount = frameCount,
            BlockRemainderBytes = blockRemainderBytes,
            ValidBitsPerSample = Math.Clamp(payload.ValidBitsPerSample, 0, 128),
            ChannelMask = Math.Clamp(payload.ChannelMask, 0L, 4_294_967_295L),
            AudioEncoding = SanitizeBridgeReceiptCode(payload.AudioEncoding, 64),
            SampleFormat = SanitizeBridgeReceiptCode(payload.SampleFormat, 64),
            ByteOrder = SanitizeBridgeReceiptCode(payload.ByteOrder, 64),
            MixerConversionHint = SanitizeBridgeReceiptCode(payload.MixerConversionHint, 96),
            MixerQuantumMs = mixerQueue.QuantumMs,
            MixerQuantumFrames = mixerQueue.QuantumFrames,
            MixerQueueDepthEstimate = mixerQueue.QueueDepthEstimate,
            MixerTailFrames = mixerQueue.TailFrames,
            MixerBufferedMs = mixerQueue.BufferedMs,
            MixerTailMs = mixerQueue.TailMs,
            PlaybackMode = SanitizeBridgeReceiptText(payload.PlaybackMode, 64),
            PlaybackHint = SanitizeBridgeReceiptText(payload.PlaybackHint, 64),
            MimeType = SanitizeBridgeReceiptText(payload.MimeType, 96),
            FileExtension = SanitizeBridgeReceiptText(payload.FileExtension, 16).ToLowerInvariant(),
            Reason = SanitizeBridgeReceiptText(payload.Reason, 160),
            FailureCode = SanitizeBridgeReceiptCode(payload.FailureCode, 64),
            Source = SanitizeBridgeReceiptText(envelope.Source, 64),
            CapturedAtUtc = envelope.TimestampUtc,
        };
    }

    private static (int QuantumMs, int QuantumFrames, long QueueDepthEstimate, int TailFrames, long BufferedMs, int TailMs) BuildNativeMixerQueueReceipt(
        int sampleRateHz,
        long frameCount)
    {
        if (sampleRateHz <= 0 || frameCount <= 0)
        {
            return (0, 0, 0, 0, 0, 0);
        }

        long quantumFrames = Math.Max(1L, ((long)sampleRateHz * NativeMixerQueueQuantumMs + 500L) / 1000L);
        long queueDepth = Math.Clamp((frameCount + quantumFrames - 1L) / quantumFrames, 0L, 4_294_967_295L);
        int tailFrames = (int)Math.Clamp(frameCount % quantumFrames, 0L, int.MaxValue);
        long bufferedMs = Math.Clamp(queueDepth * NativeMixerQueueQuantumMs, 0L, 4_294_967_295L);
        int tailMs = tailFrames <= 0
            ? 0
            : (int)Math.Clamp(((long)tailFrames * 1000L + sampleRateHz - 1L) / sampleRateHz, 1L, int.MaxValue);

        return (NativeMixerQueueQuantumMs, (int)Math.Min(quantumFrames, int.MaxValue), queueDepth, tailFrames, bufferedMs, tailMs);
    }

    private static string SanitizeBridgeReceiptText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || maxLength <= 0)
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        StringBuilder builder = new(Math.Min(trimmed.Length, maxLength));
        foreach (char character in trimmed)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (!char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static string SanitizeBridgeReceiptCode(string? value, int maxLength)
    {
        string text = SanitizeBridgeReceiptText(value, maxLength).ToLowerInvariant();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        StringBuilder builder = new(text.Length);
        bool previousWasSeparator = false;
        foreach (char character in text)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.')
            {
                builder.Append(character);
                previousWasSeparator = false;
            }
            else if (char.IsWhiteSpace(character) && !previousWasSeparator && builder.Length > 0)
            {
                builder.Append('_');
                previousWasSeparator = true;
            }
        }

        return builder.ToString().Trim('_', '-', '.');
    }

    private void InvalidateUiProbeDiagnostics()
    {
        lock (_uiProbeDiagnosticsGate)
        {
            _nextUiProbeDiagnosticsRefreshTick = 0;
        }
    }

    private void PruneUiProbeDiagnosticsDirectory()
    {
        lock (_uiProbeDiagnosticsGate)
        {
            DirectoryRetention.Enforce(
                _options.BridgeDiagnosticsDir,
                _options.Bridge.DiagnosticsMaxFiles,
                _options.Bridge.DiagnosticsMaxAgeHours,
                "*.json");
        }
    }

    private UiProbeDiagnosticsSnapshot BuildUiProbeDiagnosticsSnapshot()
    {
        string[] files = GetSortedFiles(_options.BridgeDiagnosticsDir, "*.json");
        if (files.Length == 0)
        {
            return new UiProbeDiagnosticsSnapshot();
        }

        var candidates = new Dictionary<string, UiProbeCandidateAccumulator>(StringComparer.OrdinalIgnoreCase);
        int dumpCount = 0;
        DateTimeOffset? lastDumpAtUtc = null;
        string lastDumpPath = string.Empty;
        string lastReason = string.Empty;
        string lastSummary = string.Empty;

        foreach (string file in files)
        {
            UiProbeDumpDocument? dump = TryReadUiProbeDump(file, _options.Http.LocalArtifactMaxBytes);
            if (dump is null)
            {
                continue;
            }

            dumpCount++;
            DateTimeOffset seenAtUtc = ResolveUiProbeDumpTimestamp(file, dump.GeneratedAtUtc);
            if (!lastDumpAtUtc.HasValue || seenAtUtc >= lastDumpAtUtc.Value)
            {
                lastDumpAtUtc = seenAtUtc;
                lastDumpPath = file;
                lastReason = dump.Reason ?? string.Empty;
                lastSummary = dump.Summary ?? string.Empty;
            }

            foreach (UiProbeWidgetEntry widget in SanitizeUiProbeWidgetEntries(dump.Widgets, 24))
            {
                string key = BuildUiProbeCandidateKey(widget);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!candidates.TryGetValue(key, out UiProbeCandidateAccumulator? accumulator))
                {
                    accumulator = new UiProbeCandidateAccumulator(key);
                    candidates[key] = accumulator;
                }

                accumulator.Observe(widget, seenAtUtc);
            }
        }

        UiProbeCandidateSummary[] rankedCandidates = candidates.Values
            .Select(BuildUiProbeCandidateSummary)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.ActiveRatio)
            .ThenByDescending(candidate => candidate.DumpCount)
            .ThenByDescending(candidate => candidate.PeakSeenCount)
            .ThenByDescending(candidate => candidate.LastSeenAtUtc)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new UiProbeDiagnosticsSnapshot
        {
            DumpCount = dumpCount,
            CandidateCount = rankedCandidates.Length,
            LastDumpAtUtc = lastDumpAtUtc,
            LastDumpPath = lastDumpPath,
            LastReason = lastReason,
            LastSummary = lastSummary,
            Candidates = rankedCandidates,
        };
    }

    private static UiProbeDumpDocument? TryReadUiProbeDump(string file, int maxBytes)
    {
        BoundedJsonFileReader.JsonReadResult<UiProbeDumpDocument> readResult =
            BoundedJsonFileReader.TryRead(
                file,
                maxBytes,
                stream => JsonSerializer.Deserialize(stream, UiProbeDumpJsonContextInstance.UiProbeDumpDocument));
        if (!readResult.Succeeded || readResult.Value is null)
        {
            return null;
        }

        UiProbeDumpDocument dump = readResult.Value;
        return new UiProbeDumpDocument
        {
            GeneratedAtUtc = dump.GeneratedAtUtc,
            Reason = dump.Reason ?? string.Empty,
            Summary = dump.Summary ?? string.Empty,
            ObservedWidgetCount = Math.Max(0, dump.ObservedWidgetCount),
            ActiveWidgetCount = Math.Max(0, dump.ActiveWidgetCount),
            Widgets = SanitizeUiProbeWidgetEntries(dump.Widgets, 24),
        };
    }

    private static DateTimeOffset ResolveUiProbeDumpTimestamp(string file, DateTimeOffset? generatedAtUtc)
    {
        if (generatedAtUtc.HasValue && generatedAtUtc.Value != default)
        {
            return generatedAtUtc.Value;
        }

        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        }
        catch (IOException)
        {
            return DateTimeOffset.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static UiProbeCandidateSummary BuildUiProbeCandidateSummary(UiProbeCandidateAccumulator candidate)
    {
        string searchText = BuildUiProbeSearchText(candidate.DisplayName, candidate.ClassName, candidate.FullName);
        string[] positiveHits = FindUiProbeKeywordHits(searchText, UiProbePositiveKeywords);
        string[] negativeHits = FindUiProbeKeywordHits(searchText, UiProbeNegativeKeywords);
        double activeRatio = candidate.DumpCount <= 0
            ? 0
            : Math.Round((double)candidate.ActiveObservationCount / candidate.DumpCount, 2, MidpointRounding.AwayFromZero);

        var rationale = new List<string>();
        int score = candidate.DumpCount * 8;
        score += candidate.ActiveObservationCount * 6;
        score += Math.Min(candidate.PeakSeenCount, 12) * 2;

        if (candidate.DumpCount >= 3)
        {
            score += 12;
            rationale.Add($"recurs across {candidate.DumpCount} dumps");
        }
        else if (candidate.DumpCount == 2)
        {
            score += 5;
            rationale.Add("recurs across multiple dumps");
        }

        if (candidate.ActiveObservationCount > 0)
        {
            rationale.Add($"active in {candidate.ActiveObservationCount}/{candidate.DumpCount} dumps");
            if (activeRatio >= 0.75d)
            {
                score += 10;
            }
            else if (activeRatio >= 0.5d)
            {
                score += 5;
            }
            else
            {
                score += 2;
            }
        }

        if (candidate.PeakSeenCount >= 4)
        {
            rationale.Add($"peaks at x{candidate.PeakSeenCount} lifecycle hits");
        }

        if (positiveHits.Length > 0)
        {
            score += positiveHits.Length * 6;
            rationale.Add($"name suggests HUD usage: {string.Join("/", positiveHits.Take(2))}");
        }

        if (negativeHits.Length > 0)
        {
            score -= negativeHits.Length * 7;
            rationale.Add($"penalized for menu-like naming: {string.Join("/", negativeHits.Take(2))}");
        }

        if (searchText.Contains("root", StringComparison.OrdinalIgnoreCase) && positiveHits.Length > 0)
        {
            score += 4;
            rationale.Add("looks like a root-level surface");
        }

        if (string.Equals(candidate.LastLifecycle, "construct", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return new UiProbeCandidateSummary
        {
            DisplayName = candidate.DisplayName,
            FullName = candidate.FullName,
            ClassName = candidate.ClassName,
            DumpCount = candidate.DumpCount,
            ActiveObservationCount = candidate.ActiveObservationCount,
            PeakSeenCount = candidate.PeakSeenCount,
            ActiveRatio = activeRatio,
            Score = Math.Max(0, score),
            LastLifecycle = candidate.LastLifecycle,
            LastSeenAtUtc = candidate.LastSeenAtUtc,
            Rationale = rationale.ToArray(),
        };
    }

    private static string BuildUiProbeCandidateKey(UiProbeWidgetEntry entry) =>
        TakeFirstNonBlank(entry.FullName, entry.ClassName, entry.DisplayName);

    private static UiProbeWidgetEntry[] SanitizeUiProbeWidgetEntries(
        IEnumerable<UiProbeWidgetEntry>? entries,
        int limit)
    {
        return (entries ?? Array.Empty<UiProbeWidgetEntry>())
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.DisplayName)
                || !string.IsNullOrWhiteSpace(entry.FullName)
                || !string.IsNullOrWhiteSpace(entry.ClassName))
            .Take(Math.Max(0, limit))
            .Select(CloneUiProbeWidget)
            .ToArray();
    }

    private static string BuildUiProbeSearchText(params string?[] values) =>
        string.Join(" | ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string[] FindUiProbeKeywordHits(string text, IEnumerable<string> keywords) =>
        keywords
            .Where(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static UiProbeSnapshot? CloneUiProbe(UiProbeSnapshot? probe)
    {
        if (probe is null)
        {
            return null;
        }

        UiProbeWidgetEntry[] widgets = (probe.Widgets ?? Array.Empty<UiProbeWidgetEntry>())
            .Select(CloneUiProbeWidget)
            .ToArray();

        return new UiProbeSnapshot
        {
            Reason = probe.Reason,
            Summary = probe.Summary,
            DumpPath = probe.DumpPath,
            ObservedWidgetCount = probe.ObservedWidgetCount,
            ActiveWidgetCount = probe.ActiveWidgetCount,
            Source = probe.Source,
            CapturedAtUtc = probe.CapturedAtUtc,
            Widgets = widgets,
        };
    }

    private static ChatIngressSnapshot? CloneChatIngress(ChatIngressSnapshot? ingress)
    {
        if (ingress is null)
        {
            return null;
        }

        return new ChatIngressSnapshot
        {
            RequestId = ingress.RequestId,
            CharacterName = ingress.CharacterName,
            TaskTag = ingress.TaskTag,
            TaskKind = ingress.TaskKind,
            Source = ingress.Source,
            CapturedAtUtc = ingress.CapturedAtUtc,
        };
    }

    private static OutboxReplyTraceSnapshot? CloneOutboxReplyTrace(OutboxReplyTraceSnapshot? trace)
    {
        if (trace is null)
        {
            return null;
        }

        return new OutboxReplyTraceSnapshot
        {
            RequestId = trace.RequestId,
            CharacterName = trace.CharacterName,
            TaskTag = trace.TaskTag,
            TaskKind = trace.TaskKind,
            ResponsePath = trace.ResponsePath,
            UsedFallback = trace.UsedFallback,
            FallbackStrategy = trace.FallbackStrategy,
            ActionType = trace.ActionType,
            SpeechExpected = trace.SpeechExpected,
            SpeechDelivery = trace.SpeechDelivery,
            SpeechMimeType = trace.SpeechMimeType,
            SpeechPlaybackHint = trace.SpeechPlaybackHint,
            Source = trace.Source,
            WrittenAtUtc = trace.WrittenAtUtc,
        };
    }

    private static ReplyDeliverySnapshot? CloneReplyDelivery(ReplyDeliverySnapshot? delivery)
    {
        if (delivery is null)
        {
            return null;
        }

        return new ReplyDeliverySnapshot
        {
            RequestId = delivery.RequestId,
            Speaker = delivery.Speaker,
            ResponsePath = delivery.ResponsePath,
            StrategyId = delivery.StrategyId,
            Phase = delivery.Phase,
            UsedFallback = delivery.UsedFallback,
            Rendered = delivery.Rendered,
            Surface = delivery.Surface,
            CardLabel = delivery.CardLabel,
            CardIndex = delivery.CardIndex,
            CardCount = delivery.CardCount,
            Note = delivery.Note,
            Source = delivery.Source,
            CapturedAtUtc = delivery.CapturedAtUtc,
        };
    }

    private static BridgeActionFeedbackSnapshot? CloneActionFeedback(BridgeActionFeedbackSnapshot? feedback)
    {
        if (feedback is null)
        {
            return null;
        }

        return new BridgeActionFeedbackSnapshot
        {
            RequestId = feedback.RequestId,
            EventType = feedback.EventType,
            SourceStrategy = feedback.SourceStrategy,
            Summary = feedback.Summary,
            Source = feedback.Source,
            CapturedAtUtc = feedback.CapturedAtUtc,
        };
    }

    private static SpeechPlaybackSnapshot? CloneSpeechPlayback(SpeechPlaybackSnapshot? playback)
    {
        if (playback is null)
        {
            return null;
        }

        return new SpeechPlaybackSnapshot
        {
            RequestId = playback.RequestId,
            Started = playback.Started,
            ArtifactBytes = playback.ArtifactBytes,
            AttemptCount = playback.AttemptCount,
            ElapsedMs = playback.ElapsedMs,
            PlaybackSequence = playback.PlaybackSequence,
            SupersededRequestId = playback.SupersededRequestId,
            SupersededSpeechCount = playback.SupersededSpeechCount,
            SupersededSpeechAgeMs = playback.SupersededSpeechAgeMs,
            SupersededSpeechBufferedMs = playback.SupersededSpeechBufferedMs,
            SupersededSpeechRemainingMs = playback.SupersededSpeechRemainingMs,
            CancellationMode = playback.CancellationMode,
            SampleRateHz = playback.SampleRateHz,
            ChannelCount = playback.ChannelCount,
            BitsPerSample = playback.BitsPerSample,
            DurationMs = playback.DurationMs,
            ByteRate = playback.ByteRate,
            BlockAlignBytes = playback.BlockAlignBytes,
            AudioDataBytes = playback.AudioDataBytes,
            FrameCount = playback.FrameCount,
            BlockRemainderBytes = playback.BlockRemainderBytes,
            ValidBitsPerSample = playback.ValidBitsPerSample,
            ChannelMask = playback.ChannelMask,
            AudioEncoding = playback.AudioEncoding,
            SampleFormat = playback.SampleFormat,
            ByteOrder = playback.ByteOrder,
            MixerConversionHint = playback.MixerConversionHint,
            MixerQuantumMs = playback.MixerQuantumMs,
            MixerQuantumFrames = playback.MixerQuantumFrames,
            MixerQueueDepthEstimate = playback.MixerQueueDepthEstimate,
            MixerTailFrames = playback.MixerTailFrames,
            MixerBufferedMs = playback.MixerBufferedMs,
            MixerTailMs = playback.MixerTailMs,
            PlaybackMode = playback.PlaybackMode,
            PlaybackHint = playback.PlaybackHint,
            MimeType = playback.MimeType,
            FileExtension = playback.FileExtension,
            Reason = playback.Reason,
            FailureCode = playback.FailureCode,
            Source = playback.Source,
            CapturedAtUtc = playback.CapturedAtUtc,
        };
    }

    private static int ClampBridgeLagMs(DateTimeOffset start, DateTimeOffset end)
    {
        double lagMs = (end - start).TotalMilliseconds;
        if (double.IsNaN(lagMs) || lagMs <= 0)
        {
            return 0;
        }

        return (int)Math.Clamp(Math.Round(lagMs, MidpointRounding.AwayFromZero), 0, 86_400_000);
    }

    private static BridgeLoopProofSnapshot BuildBridgeLoopProof(
        ChatIngressSnapshot? ingress,
        OutboxReplyTraceSnapshot? outboxReply,
        ReplyDeliverySnapshot? replyDelivery,
        BridgeActionFeedbackSnapshot? actionFeedback,
        SpeechPlaybackSnapshot? speechPlayback)
    {
        ChatIngressSnapshot? ingressClone = CloneChatIngress(ingress);
        OutboxReplyTraceSnapshot? outboxClone = CloneOutboxReplyTrace(outboxReply);
        ReplyDeliverySnapshot? deliveryClone = CloneReplyDelivery(replyDelivery);
        BridgeActionFeedbackSnapshot? feedbackClone = CloneActionFeedback(actionFeedback);
        SpeechPlaybackSnapshot? speechPlaybackClone = CloneSpeechPlayback(speechPlayback);

        bool requestSeen = !string.IsNullOrWhiteSpace(ingressClone?.RequestId);
        bool outboxWritten = !string.IsNullOrWhiteSpace(outboxClone?.RequestId);
        bool freshIngressAwaitingReply =
            requestSeen
            && (!outboxWritten
                || (!string.Equals(
                        ingressClone!.RequestId,
                        outboxClone!.RequestId,
                        StringComparison.OrdinalIgnoreCase)
                    && ingressClone.CapturedAtUtc >= outboxClone.WrittenAtUtc));

        string activeRequestId;
        string status;
        bool visibleDeliveryConfirmed = false;
        bool actionPlanned = false;
        bool actionFeedbackObserved = false;
        bool speechPlaybackExpected = false;
        bool speechPlaybackObserved = false;
        bool speechPlaybackStarted = false;
        int speechPlaybackIngressLagMs = 0;
        int speechPlaybackOutboxLagMs = 0;
        int speechPlaybackDeliveryLagMs = 0;
        bool loopClosed = false;

        if (freshIngressAwaitingReply)
        {
            activeRequestId = ingressClone!.RequestId;
            status = "awaiting_reply";
        }
        else if (outboxWritten)
        {
            activeRequestId = outboxClone!.RequestId;
            actionPlanned = !string.IsNullOrWhiteSpace(outboxClone.ActionType);
            speechPlaybackExpected = outboxClone.SpeechExpected;
            visibleDeliveryConfirmed =
                deliveryClone is not null
                && deliveryClone.Rendered
                && string.Equals(
                    outboxClone.RequestId,
                    deliveryClone.RequestId,
                    StringComparison.OrdinalIgnoreCase);
            actionFeedbackObserved =
                feedbackClone is not null
                && string.Equals(
                    outboxClone.RequestId,
                    feedbackClone.RequestId,
                    StringComparison.OrdinalIgnoreCase);
            speechPlaybackObserved =
                speechPlaybackClone is not null
                && string.Equals(
                    outboxClone.RequestId,
                    speechPlaybackClone.RequestId,
                    StringComparison.OrdinalIgnoreCase);
            speechPlaybackStarted = speechPlaybackObserved && speechPlaybackClone!.Started;
            if (speechPlaybackObserved)
            {
                if (ingressClone is not null
                    && string.Equals(
                        outboxClone.RequestId,
                        ingressClone.RequestId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    speechPlaybackIngressLagMs = ClampBridgeLagMs(
                        ingressClone.CapturedAtUtc,
                        speechPlaybackClone!.CapturedAtUtc);
                }

                speechPlaybackOutboxLagMs = ClampBridgeLagMs(
                    outboxClone.WrittenAtUtc,
                    speechPlaybackClone!.CapturedAtUtc);
                if (visibleDeliveryConfirmed && deliveryClone is not null)
                {
                    speechPlaybackDeliveryLagMs = ClampBridgeLagMs(
                        deliveryClone.CapturedAtUtc,
                        speechPlaybackClone.CapturedAtUtc);
                }
            }

            if (!visibleDeliveryConfirmed)
            {
                status = "awaiting_delivery";
            }
            else if (speechPlaybackExpected && !speechPlaybackObserved)
            {
                status = "awaiting_speech_playback";
            }
            else if (speechPlaybackExpected && !speechPlaybackStarted)
            {
                status = "speech_playback_failed";
            }
            else if (actionPlanned && !actionFeedbackObserved)
            {
                status = "awaiting_action_feedback";
            }
            else
            {
                status = "closed";
                loopClosed = true;
            }
        }
        else if (requestSeen)
        {
            activeRequestId = ingressClone!.RequestId;
            status = "awaiting_reply";
        }
        else if (deliveryClone is not null && !string.IsNullOrWhiteSpace(deliveryClone.RequestId))
        {
            activeRequestId = deliveryClone.RequestId;
            status = deliveryClone.Rendered ? "delivery_unmatched" : "delivery_suppressed";
        }
        else if (feedbackClone is not null && !string.IsNullOrWhiteSpace(feedbackClone.RequestId))
        {
            activeRequestId = feedbackClone.RequestId;
            status = "feedback_unmatched";
        }
        else if (speechPlaybackClone is not null && !string.IsNullOrWhiteSpace(speechPlaybackClone.RequestId))
        {
            activeRequestId = speechPlaybackClone.RequestId;
            status = "speech_playback_unmatched";
        }
        else
        {
            activeRequestId = string.Empty;
            status = "idle";
        }

        return new BridgeLoopProofSnapshot
        {
            Status = status,
            ActiveRequestId = activeRequestId,
            RequestSeen = requestSeen,
            OutboxReplyWritten = outboxWritten,
            VisibleDeliveryConfirmed = visibleDeliveryConfirmed,
            ActionPlanned = actionPlanned,
            ActionFeedbackObserved = actionFeedbackObserved,
            SpeechPlaybackExpected = speechPlaybackExpected,
            SpeechPlaybackObserved = speechPlaybackObserved,
            SpeechPlaybackStarted = speechPlaybackStarted,
            SpeechPlaybackIngressLagMs = speechPlaybackIngressLagMs,
            SpeechPlaybackOutboxLagMs = speechPlaybackOutboxLagMs,
            SpeechPlaybackDeliveryLagMs = speechPlaybackDeliveryLagMs,
            LoopClosed = loopClosed,
            LastIngress = ingressClone,
            LastOutboxReply = outboxClone,
            LastReplyDelivery = deliveryClone,
            LastActionFeedback = feedbackClone,
            LastSpeechPlayback = speechPlaybackClone,
        };
    }

    private void RememberBridgeBoot(BridgeBootPayload payload)
    {
        BridgeBootPayload normalized = NormalizeBridgeBootPayload(payload);
        lock (_bridgeGate)
        {
            _lastBridgeBoot = normalized;
        }
    }

    private static BridgeBootPayload NormalizeBridgeBootPayload(BridgeBootPayload payload)
    {
        BridgeBootCompatSignal[] compatSignals = NormalizeCompatSignals(payload.CompatSignals, payload.Compat);
        string compatSummary = string.IsNullOrWhiteSpace(payload.Compat)
            ? BuildCompatSummary(compatSignals)
            : payload.Compat.Trim();
        string[] nativeHudWidgetTargets = NormalizeHudTargetList(payload.NativeHudWidgetTargets);

        return new BridgeBootPayload
        {
            Version = payload.Version ?? string.Empty,
            Status = payload.Status ?? string.Empty,
            Compat = compatSummary,
            CompatSignals = compatSignals,
            UiProbeEnabled = payload.UiProbeEnabled,
            ActionExecutorEnabled = payload.ActionExecutorEnabled,
            NativeHudRenderEnabled = payload.NativeHudRenderEnabled,
            NativeHudWidgetTargetCount = Math.Max(
                Math.Max(0, payload.NativeHudWidgetTargetCount),
                nativeHudWidgetTargets.Length),
            NativeHudWidgetTargets = nativeHudWidgetTargets,
            NativeHudConfigSource = payload.NativeHudConfigSource?.Trim() ?? string.Empty,
            NativeHudConfigPath = payload.NativeHudConfigPath?.Trim() ?? string.Empty,
            ProductionSamplerEnabled = payload.ProductionSamplerEnabled,
            WaypointNativeMarkerEnabled = payload.WaypointNativeMarkerEnabled,
        };
    }

    private static BridgeBootCompatSignal[] NormalizeCompatSignals(
        IReadOnlyList<BridgeBootCompatSignal>? compatSignals,
        string compatSummary)
    {
        if (compatSignals is { Count: > 0 })
        {
            return compatSignals
                .Where(signal => !string.IsNullOrWhiteSpace(signal.Key))
                .Select(signal => new BridgeBootCompatSignal
                {
                    Key = signal.Key.Trim(),
                    Present = signal.Present,
                })
                .ToArray();
        }

        if (string.IsNullOrWhiteSpace(compatSummary))
        {
            return [];
        }

        List<BridgeBootCompatSignal> parsed = [];
        foreach (string part in compatSummary.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separatorIndex = part.IndexOf('=');
            string key = separatorIndex >= 0 ? part[..separatorIndex].Trim() : part.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            string value = separatorIndex >= 0 ? part[(separatorIndex + 1)..].Trim() : "missing";
            parsed.Add(new BridgeBootCompatSignal
            {
                Key = key,
                Present = value.Equals("present", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("ready", StringComparison.OrdinalIgnoreCase),
            });
        }

        return parsed.ToArray();
    }

    private static string BuildCompatSummary(IReadOnlyList<BridgeBootCompatSignal> compatSignals) =>
        compatSignals.Count == 0
            ? string.Empty
            : string.Join(
                " | ",
                compatSignals.Select(signal => $"{signal.Key}={(signal.Present ? "present" : "missing")}"));

    private static BridgeBootPayload? CloneBridgeBootPayload(BridgeBootPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        string[] nativeHudWidgetTargets = NormalizeHudTargetList(payload.NativeHudWidgetTargets);

        return new BridgeBootPayload
        {
            Version = payload.Version ?? string.Empty,
            Status = payload.Status ?? string.Empty,
            Compat = payload.Compat ?? string.Empty,
            CompatSignals = NormalizeCompatSignals(payload.CompatSignals, payload.Compat ?? string.Empty),
            UiProbeEnabled = payload.UiProbeEnabled,
            ActionExecutorEnabled = payload.ActionExecutorEnabled,
            NativeHudRenderEnabled = payload.NativeHudRenderEnabled,
            NativeHudWidgetTargetCount = Math.Max(
                Math.Max(0, payload.NativeHudWidgetTargetCount),
                nativeHudWidgetTargets.Length),
            NativeHudWidgetTargets = nativeHudWidgetTargets,
            NativeHudConfigSource = payload.NativeHudConfigSource?.Trim() ?? string.Empty,
            NativeHudConfigPath = payload.NativeHudConfigPath?.Trim() ?? string.Empty,
            ProductionSamplerEnabled = payload.ProductionSamplerEnabled,
            WaypointNativeMarkerEnabled = payload.WaypointNativeMarkerEnabled,
        };
    }

    private static NativeReadinessSnapshot BuildNativeReadinessSnapshot(
        BridgeBootPayload? bridgeBoot,
        UiProbeDiagnosticsSnapshot? diagnostics)
    {
        BridgeBootPayload? normalizedBoot = bridgeBoot is null ? null : NormalizeBridgeBootPayload(bridgeBoot);
        IReadOnlyList<BridgeBootCompatSignal> compatSignals = normalizedBoot?.CompatSignals ?? Array.Empty<BridgeBootCompatSignal>();

        bool bridgeBootSeen = normalizedBoot is not null;
        bool uiProbeEnabled = normalizedBoot?.UiProbeEnabled ?? false;
        bool hasPalGameStateCompat = HasCompatSignal(compatSignals, "PalGameStateInGame");
        bool hasPalCharacterCompat = HasCompatSignal(compatSignals, "PalCharacter");
        bool hasPalBaseCampManagerCompat = HasCompatSignal(compatSignals, "PalBaseCampManager");
        bool hasPalMapManagerCompat = HasCompatSignal(compatSignals, "PalMapManager");
        bool hasUserWidgetCompat = HasCompatSignal(compatSignals, "UserWidget");
        bool hasUiProbeCandidates = diagnostics?.Candidates?.Count > 0;
        string[] configuredHudTargets = NormalizeHudTargetList(normalizedBoot?.NativeHudWidgetTargets);
        string nativeHudConfigSource = normalizedBoot?.NativeHudConfigSource ?? string.Empty;
        string nativeHudConfigPath = normalizedBoot?.NativeHudConfigPath ?? string.Empty;
        string topUiProbeCandidate = diagnostics?.Candidates?.FirstOrDefault() switch
        {
            UiProbeCandidateSummary candidate when !string.IsNullOrWhiteSpace(candidate.DisplayName) => candidate.DisplayName,
            UiProbeCandidateSummary candidate when !string.IsNullOrWhiteSpace(candidate.FullName) => candidate.FullName,
            _ => string.Empty,
        };

        bool nativeHudEnabled = normalizedBoot?.NativeHudRenderEnabled ?? false;
        int nativeHudTargetCount = Math.Max(
            configuredHudTargets.Length,
            normalizedBoot?.NativeHudWidgetTargetCount ?? 0);
        bool nativeHudTargetsConfigured = nativeHudTargetCount > 0;
        bool hudSeamDiscovered = bridgeBootSeen && hasUserWidgetCompat && hasUiProbeCandidates;
        bool hudBindReady = hudSeamDiscovered && nativeHudEnabled && nativeHudTargetsConfigured;
        bool productionSamplerEnabled = normalizedBoot?.ProductionSamplerEnabled ?? false;
        bool productionSamplerReady = bridgeBootSeen && hasPalBaseCampManagerCompat && productionSamplerEnabled;
        bool waypointNativeMarkerEnabled = normalizedBoot?.WaypointNativeMarkerEnabled ?? false;
        bool waypointMarkerReady = bridgeBootSeen && hasPalMapManagerCompat && waypointNativeMarkerEnabled;
        bool actionExecutorEnabled = normalizedBoot?.ActionExecutorEnabled ?? false;
        HudBindRecommendationSnapshot hudBindRecommendation = BuildHudBindRecommendation(
            bridgeBootSeen,
            uiProbeEnabled,
            hasUserWidgetCompat,
            nativeHudEnabled,
            nativeHudTargetCount,
            configuredHudTargets,
            diagnostics);

        List<string> readyCapabilities = [];
        List<string> missingPrerequisites = [];

        if (!bridgeBootSeen)
        {
            missingPrerequisites.Add("No bridge_boot heartbeat has been observed yet. Launch Palworld with the UE4SS bridge running.");
        }
        else
        {
            readyCapabilities.Add($"Bridge boot heartbeat received from version '{normalizedBoot!.Version}' with status '{normalizedBoot.Status}'.");

            if (hasPalGameStateCompat)
            {
                readyCapabilities.Add("PalGameStateInGame class is present.");
            }

            if (hasPalCharacterCompat)
            {
                readyCapabilities.Add("PalCharacter class is present.");
            }

            if (hasUserWidgetCompat)
            {
                readyCapabilities.Add("UserWidget class is present for native HUD targeting.");
            }
            else
            {
                missingPrerequisites.Add("UserWidget class was not detected on the current Palworld build.");
            }

            if (hasPalBaseCampManagerCompat)
            {
                readyCapabilities.Add("PalBaseCampManager is present for production sampling.");
            }
            else
            {
                missingPrerequisites.Add("PalBaseCampManager was not detected on the current Palworld build.");
            }

            if (hasPalMapManagerCompat)
            {
                readyCapabilities.Add("PalMapManager is present for waypoint marker hints.");
            }
            else
            {
                missingPrerequisites.Add("PalMapManager was not detected on the current Palworld build.");
            }

            if (uiProbeEnabled)
            {
                if (hasUiProbeCandidates)
                {
                    readyCapabilities.Add($"ui_probe has captured candidate widgets; top candidate: '{topUiProbeCandidate}'.");
                }
                else
                {
                    missingPrerequisites.Add("ui_probe is enabled but has not captured any ranked widget candidates yet.");
                }
            }
            else
            {
                missingPrerequisites.Add("ui_probe is disabled in the UE4SS bridge.");
            }

            if (nativeHudEnabled)
            {
                readyCapabilities.Add("native_hud_render_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("native_hud_render_enabled is false.");
            }

            if (nativeHudTargetsConfigured)
            {
                readyCapabilities.Add("native_hud_widget_targets has at least one configured target.");
            }
            else
            {
                missingPrerequisites.Add("native_hud_widget_targets is empty.");
            }

            if (productionSamplerEnabled)
            {
                readyCapabilities.Add("production_sampler_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("production_sampler_enabled is false.");
            }

            if (waypointNativeMarkerEnabled)
            {
                readyCapabilities.Add("waypoint_native_marker_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("waypoint_native_marker_enabled is false.");
            }

            if (actionExecutorEnabled)
            {
                readyCapabilities.Add("action_executor_enabled is true.");
            }
            else
            {
                missingPrerequisites.Add("action_executor_enabled is false.");
            }
        }

        if (string.Equals(nativeHudConfigSource, "override_error", StringComparison.OrdinalIgnoreCase))
        {
            string pathDetail = string.IsNullOrWhiteSpace(nativeHudConfigPath)
                ? "the configured override file"
                : $"'{nativeHudConfigPath}'";
            missingPrerequisites.Add($"Native HUD override loading failed for {pathDetail}; the bridge fell back to inline defaults.");
        }
        else if (!string.IsNullOrWhiteSpace(nativeHudConfigPath))
        {
            string configSummary = string.Equals(nativeHudConfigSource, "inline_defaults", StringComparison.OrdinalIgnoreCase)
                ? $"Native HUD is currently using inline defaults; preferred override path is '{nativeHudConfigPath}'."
                : $"Native HUD override loaded from '{nativeHudConfigPath}'.";
            readyCapabilities.Add(configSummary);
        }

        if (!string.IsNullOrWhiteSpace(hudBindRecommendation.RecommendedTarget))
        {
            readyCapabilities.Add($"HUD bind recommendation is '{hudBindRecommendation.RecommendedTarget}'.");
        }

        switch (hudBindRecommendation.Status)
        {
            case "configured_targets_need_review":
                missingPrerequisites.Add("Configured native_hud_widget_targets do not include the top ranked ui_probe candidate yet.");
                break;
            case "configured_targets_unreported":
                missingPrerequisites.Add("native_hud_widget_targets count is reported, but the bridge heartbeat did not include exact target names.");
                break;
            case "recommend_target" when !string.IsNullOrWhiteSpace(hudBindRecommendation.RecommendedTarget):
                missingPrerequisites.Add($"native_hud_widget_targets should start with '{hudBindRecommendation.RecommendedTarget}'.");
                break;
        }

        return new NativeReadinessSnapshot
        {
            BridgeBootSeen = bridgeBootSeen,
            BridgeVersion = normalizedBoot?.Version ?? string.Empty,
            BridgeStatus = normalizedBoot?.Status ?? string.Empty,
            CompatSummary = normalizedBoot?.Compat ?? string.Empty,
            CompatSignals = compatSignals.ToArray(),
            UiProbeEnabled = uiProbeEnabled,
            HasPalGameStateCompat = hasPalGameStateCompat,
            HasPalCharacterCompat = hasPalCharacterCompat,
            HasPalBaseCampManagerCompat = hasPalBaseCampManagerCompat,
            HasPalMapManagerCompat = hasPalMapManagerCompat,
            HasUserWidgetCompat = hasUserWidgetCompat,
            HasUiProbeCandidates = hasUiProbeCandidates,
            TopUiProbeCandidate = topUiProbeCandidate,
            ConfiguredHudTargets = configuredHudTargets,
            NativeHudConfigSource = nativeHudConfigSource,
            NativeHudConfigPath = nativeHudConfigPath,
            ActionExecutorEnabled = actionExecutorEnabled,
            NativeHudEnabled = nativeHudEnabled,
            NativeHudTargetsConfigured = nativeHudTargetsConfigured,
            HudSeamDiscovered = hudSeamDiscovered,
            HudBindReady = hudBindReady,
            ProductionSamplerEnabled = productionSamplerEnabled,
            ProductionSamplerReady = productionSamplerReady,
            WaypointNativeMarkerEnabled = waypointNativeMarkerEnabled,
            WaypointMarkerReady = waypointMarkerReady,
            HudBindRecommendation = hudBindRecommendation,
            ReadyCapabilities = readyCapabilities,
            MissingPrerequisites = missingPrerequisites,
        };
    }

    private static HudBindRecommendationSnapshot BuildHudBindRecommendation(
        bool bridgeBootSeen,
        bool uiProbeEnabled,
        bool hasUserWidgetCompat,
        bool nativeHudEnabled,
        int configuredTargetCount,
        IReadOnlyList<string> configuredTargets,
        UiProbeDiagnosticsSnapshot? diagnostics)
    {
        UiProbeCandidateSummary[] shortlist = (diagnostics?.Candidates ?? Array.Empty<UiProbeCandidateSummary>())
            .Take(3)
            .Select(CloneUiProbeCandidate)
            .ToArray();

        UiProbeCandidateSummary? topCandidate = shortlist.FirstOrDefault();
        string recommendedDisplayName = topCandidate?.DisplayName ?? string.Empty;
        string recommendedFullName = topCandidate?.FullName ?? string.Empty;
        string recommendedClassName = topCandidate?.ClassName ?? string.Empty;
        string recommendedTarget = TakeFirstNonBlank(
            recommendedFullName,
            recommendedClassName,
            recommendedDisplayName);
        string[] suggestedConfigTargets = shortlist
            .Select(candidate => candidate.FullName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (suggestedConfigTargets.Length == 0 && !string.IsNullOrWhiteSpace(recommendedTarget))
        {
            suggestedConfigTargets = [recommendedTarget];
        }

        bool configuredTargetsReported = configuredTargets.Count > 0 || configuredTargetCount == 0;
        bool configuredTargetMatchesRecommendation =
            !string.IsNullOrWhiteSpace(recommendedFullName)
            && configuredTargets.Any(target =>
                string.Equals(target, recommendedFullName, StringComparison.Ordinal));

        string status;
        string summary;
        List<string> suggestedNextSteps = [];

        if (!bridgeBootSeen)
        {
            status = "awaiting_bridge_boot";
            summary = "Launch Palworld with UE4SS so the bridge can report native HUD readiness.";
        }
        else if (!hasUserWidgetCompat)
        {
            status = "missing_userwidget_compat";
            summary = "UserWidget compatibility is missing on the current Palworld build, so native HUD binding should stay off.";
        }
        else if (!uiProbeEnabled)
        {
            status = "ui_probe_disabled";
            summary = "Enable ui_probe before choosing a native HUD widget target.";
        }
        else if (topCandidate is null)
        {
            status = "awaiting_ui_probe_capture";
            summary = "Capture a representative ui_probe dump before binding a Palworld HUD widget.";
        }
        else if (configuredTargetCount > 0 && !configuredTargetsReported)
        {
            status = "configured_targets_unreported";
            summary = "HUD targets are configured on the bridge, but the heartbeat did not report their exact names yet.";
            suggestedNextSteps.Add("Update the bridge boot heartbeat so it reports native_hud_widget_targets verbatim.");
        }
        else if (configuredTargetMatchesRecommendation && nativeHudEnabled)
        {
            status = "bind_ready";
            summary = "The top ranked ui_probe candidate is already configured and native HUD rendering is enabled.";
            suggestedNextSteps.Add("Run a live Palworld turn and verify reply_delivery reports surface=native_hud.");
        }
        else if (configuredTargetMatchesRecommendation)
        {
            status = "configured_target_ready";
            summary = "The top ranked ui_probe candidate is already configured; enable native_hud_render_enabled for the next smoke pass.";
            suggestedNextSteps.Add("Flip native_hud_render_enabled to true and rerun a live turn.");
        }
        else if (configuredTargets.Count > 0)
        {
            status = "configured_targets_need_review";
            summary = "Configured HUD targets do not currently include the top ranked ui_probe candidate.";
            suggestedNextSteps.Add("Move the recommended target to the front of native_hud_widget_targets before the next smoke pass.");
        }
        else
        {
            status = "recommend_target";
            summary = "A ranked Palworld HUD target is ready to copy into native_hud_widget_targets.";
            suggestedNextSteps.Add("Copy the recommended target into native_hud_widget_targets as the first entry.");
        }

        if (!string.IsNullOrWhiteSpace(recommendedFullName))
        {
            suggestedNextSteps.Add($"Prefer '{recommendedFullName}' as the first native_hud_widget_targets entry.");
        }

        return new HudBindRecommendationSnapshot
        {
            Status = status,
            Summary = summary,
            RecommendedTarget = recommendedTarget,
            RecommendedDisplayName = recommendedDisplayName,
            RecommendedFullName = recommendedFullName,
            RecommendedClassName = recommendedClassName,
            RecommendedScore = Math.Max(0, topCandidate?.Score ?? 0),
            ConfiguredTargetMatchesRecommendation = configuredTargetMatchesRecommendation,
            ConfiguredTargets = configuredTargets.ToArray(),
            SuggestedConfigTargets = suggestedConfigTargets,
            SuggestedNextSteps = suggestedNextSteps
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            Rationale = (topCandidate?.Rationale ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
            Shortlist = shortlist,
        };
    }

    private static bool HasCompatSignal(
        IReadOnlyList<BridgeBootCompatSignal> compatSignals,
        string key) =>
        compatSignals.Any(signal =>
            signal.Present
            && string.Equals(signal.Key, key, StringComparison.OrdinalIgnoreCase));

    private static string[] NormalizeHudTargetList(IReadOnlyList<string>? targets) =>
        (targets ?? Array.Empty<string>())
            .Where(target => !string.IsNullOrWhiteSpace(target))
            .Select(target => target.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static UiProbeDiagnosticsSnapshot CloneUiProbeDiagnostics(
        UiProbeDiagnosticsSnapshot? diagnostics,
        int candidateLimit = int.MaxValue)
    {
        if (diagnostics is null)
        {
            return new UiProbeDiagnosticsSnapshot();
        }

        UiProbeCandidateSummary[] candidates = (diagnostics.Candidates ?? Array.Empty<UiProbeCandidateSummary>())
            .Take(ClampPositiveBudget(candidateLimit))
            .Select(CloneUiProbeCandidate)
            .ToArray();

        return new UiProbeDiagnosticsSnapshot
        {
            DumpCount = diagnostics.DumpCount,
            CandidateCount = diagnostics.CandidateCount,
            LastDumpAtUtc = diagnostics.LastDumpAtUtc,
            LastDumpPath = diagnostics.LastDumpPath ?? string.Empty,
            LastReason = diagnostics.LastReason ?? string.Empty,
            LastSummary = diagnostics.LastSummary ?? string.Empty,
            Candidates = candidates,
        };
    }

    private static UiProbeCandidateSummary CloneUiProbeCandidate(UiProbeCandidateSummary candidate) =>
        new()
        {
            DisplayName = candidate.DisplayName ?? string.Empty,
            FullName = candidate.FullName ?? string.Empty,
            ClassName = candidate.ClassName ?? string.Empty,
            DumpCount = Math.Max(0, candidate.DumpCount),
            ActiveObservationCount = Math.Max(0, candidate.ActiveObservationCount),
            PeakSeenCount = Math.Max(0, candidate.PeakSeenCount),
            ActiveRatio = candidate.ActiveRatio < 0 ? 0 : candidate.ActiveRatio,
            Score = Math.Max(0, candidate.Score),
            LastLifecycle = candidate.LastLifecycle ?? string.Empty,
            LastSeenAtUtc = candidate.LastSeenAtUtc,
            Rationale = (candidate.Rationale ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
        };

    private static UiProbeWidgetEntry CloneUiProbeWidget(UiProbeWidgetEntry entry) =>
        new()
        {
            DisplayName = entry.DisplayName ?? string.Empty,
            FullName = entry.FullName ?? string.Empty,
            ClassName = entry.ClassName ?? string.Empty,
            SeenCount = Math.Max(0, entry.SeenCount),
            IsActive = entry.IsActive,
            LastLifecycle = entry.LastLifecycle ?? string.Empty,
        };

    private static string TakeFirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private void PromoteDiscoveredBase(BaseDiscoveredPayload payload, DateTimeOffset discoveredAtUtc, string source)
    {
        if (string.IsNullOrWhiteSpace(payload.BaseId))
        {
            return;
        }

        GameWorldSnapshot updated = Adapter.Snapshot.WithBaseDiscovery(
            payload.BaseId,
            payload.AreaRange,
            discoveredAtUtc,
            source);
        UpdateSnapshot(updated);
    }

    private void RememberAssistantFallback(
        int? characterId,
        string speakerName,
        string taskTag,
        FallbackBehaviorDecision decision,
        string fallbackSource) =>
        MemoryStore.Remember(
            characterId,
            speakerName,
            "assistant",
            decision.Message,
            taskTag,
            "assistant_reply",
            "fallback_reply",
            $"fallback:{decision.StrategyId}",
            $"fallback-phase:{decision.Phase.ToString().ToLowerInvariant()}",
            $"fallback-source:{fallbackSource}");

    private static string ResolveSpeakerName(
        GameCharacterSnapshot? character,
        ChatRequest request,
        string fallback) =>
        character?.DisplayName
            ?? (string.IsNullOrWhiteSpace(request.CharacterName) ? fallback : request.CharacterName);

    private static string FormatAreaRange(float? areaRange) =>
        areaRange.HasValue
            ? $" (area range {areaRange.Value.ToString("0.##", CultureInfo.InvariantCulture)})"
            : string.Empty;

    private GameCharacterSnapshot? ResolveCharacter(int? characterId, string? characterName)
    {
        GameWorldSnapshot snapshot = Adapter.Snapshot;
        if (characterId.HasValue)
        {
            return snapshot.Characters.FirstOrDefault(character => character.Id == characterId.Value);
        }

        if (!string.IsNullOrWhiteSpace(characterName))
        {
            return snapshot.Characters.FirstOrDefault(character =>
                string.Equals(character.DisplayName, characterName, StringComparison.OrdinalIgnoreCase));
        }

        return snapshot.Characters.FirstOrDefault(character => character.IsPlayerFaction);
    }

    private static string BuildSystemPrompt(
        GameWorldSnapshot snapshot,
        GameCharacterSnapshot? character,
        NarrativeCharacterProfile? lore,
        IReadOnlyList<ConversationMemoryMatch> memoryMatches,
        CharacterRelationship? relationship,
        string taskTag,
        bool preferTaskFocus,
        string visualContext,
        PromptContextBudget promptBudget,
        string visualContextSource = "vision_model")
    {
        var builder = new StringBuilder(1_024);
        builder.AppendLine("You are PalLLM, a local-first Palworld roleplay and companion layer.");
        builder.AppendLine("Stay grounded in the game world, avoid inventing unsupported mechanics, and keep replies practical.");
        if (preferTaskFocus)
        {
            // Deflanderization directive (arxiv 2510.13586): keep the character voice but
            // refuse to let performative shtick crowd out the actual player ask.
            builder.AppendLine("Stay in character, but resolve the player's ask first - do not lean on roleplay at the expense of the concrete task.");
        }

        AppendStableCharacterContext(builder, character, promptBudget);
        AppendLoreContext(builder, lore, promptBudget);

        builder.AppendLine();
        builder.AppendLine("Turn context:");
        builder.AppendLine($"Task tag: {taskTag}");
        builder.AppendLine($"World loaded: {snapshot.IsWorldLoaded}; World: {snapshot.WorldName}");

        string boundedVisualContext = TrimToLength(visualContext, promptBudget.MaxVisualContextChars);
        if (!string.IsNullOrWhiteSpace(boundedVisualContext))
        {
            // Vision augmentation. Source label tracks whether the description
            // came from the configured multimodal model or from the deterministic
            // snapshot fallback - prompt-level transparency so the model can
            // weight the context appropriately.
            string sourceLabel = visualContextSource switch
            {
                "snapshot_fallback" => "from snapshot fallback",
                _ => "from vision model",
            };
            builder.AppendLine($"Visual context ({sourceLabel}): {boundedVisualContext}");
        }

        AppendWorldContext(builder, snapshot, promptBudget);
        AppendCharacterStateContext(builder, character);
        AppendRelationshipContext(builder, relationship);
        AppendMemoryContext(builder, memoryMatches, promptBudget);

        string prompt = builder.ToString().Trim();
        int effectivePromptCap = Math.Clamp(promptBudget.MaxPromptChars, 1_024, PromptHardCapChars);
        if (prompt.Length <= effectivePromptCap)
        {
            return prompt;
        }

        // Keep the cache-stable header (role + identity + authored lore) and
        // the memory tail, drop the middle. The tail is what recent mutations
        // most affect, and the header carries the persona. Better than
        // summarising to one line.
        int headerBudget = effectivePromptCap / 3;
        int tailBudget = effectivePromptCap - headerBudget - 3;
        return prompt[..headerBudget] + "..." + prompt[^tailBudget..];
    }

    private static void AppendRelationshipContext(StringBuilder builder, CharacterRelationship? relationship)
    {
        if (relationship is null)
        {
            return;
        }

        string moodLabel = relationship.Mood switch
        {
            RelationshipMood.Hostile => "resentful and guarded",
            RelationshipMood.Cold => "wary and short with you",
            RelationshipMood.Neutral => "polite but not especially close",
            RelationshipMood.Warm => "friendly and glad to help",
            RelationshipMood.Attached => "deeply loyal and affectionate",
            _ => "neutral",
        };

        builder.AppendLine();
        builder.AppendLine(
            $"Relationship: {relationship.CharacterName} is {moodLabel} (affinity {relationship.Affinity}, {relationship.InteractionCount} exchanges). " +
            $"Let that colour tone and phrasing without stealing focus from the task.");
    }

    private static void AppendWorldContext(StringBuilder builder, GameWorldSnapshot snapshot, PromptContextBudget promptBudget)
    {
        if (!string.IsNullOrWhiteSpace(snapshot.TimeOfDay) ||
            !string.IsNullOrWhiteSpace(snapshot.Weather) ||
            !string.IsNullOrWhiteSpace(snapshot.Biome))
        {
            builder.AppendLine($"Scene: time={snapshot.TimeOfDay}; weather={snapshot.Weather}; biome={snapshot.Biome}");
        }

        if (!string.IsNullOrWhiteSpace(snapshot.CurrentObjective))
        {
            builder.AppendLine($"Objective: {snapshot.CurrentObjective}");
        }

        if (snapshot.ActiveBaseIds.Count > 0)
        {
            builder.AppendLine($"Active bases: {string.Join(", ", snapshot.ActiveBaseIds.Take(promptBudget.MaxKnownBases))}");
        }

        if (snapshot.KnownBases.Count > 0)
        {
            string knownBases = string.Join(
                ", ",
                snapshot.KnownBases
                    .Take(promptBudget.MaxKnownBases)
                    .Select(FormatKnownBase));
            builder.AppendLine($"Known bases: {knownBases}");
        }

        if (snapshot.LastProduction is not null)
        {
            builder.AppendLine($"Latest production: {FormatLatestProduction(snapshot.LastProduction)}");
        }

        if (snapshot.LastTravel is not null)
        {
            builder.AppendLine($"Latest travel: {FormatLatestTravel(snapshot.LastTravel)}");
        }

        if (snapshot.NearbyHostiles.Count > 0)
        {
            builder.AppendLine($"Nearby hostiles: {string.Join(", ", snapshot.NearbyHostiles.Take(promptBudget.MaxNearbyHostiles))}");
        }

        if (snapshot.NearbyResources.Count > 0)
        {
            builder.AppendLine($"Nearby resources: {string.Join(", ", snapshot.NearbyResources.Take(promptBudget.MaxNearbyResources))}");
        }

        if (snapshot.RecentEvents.Count > 0)
        {
            builder.AppendLine($"Recent world events: {string.Join(", ", snapshot.RecentEvents.Take(promptBudget.MaxRecentEvents))}");
        }
    }

    private static void AppendStableCharacterContext(StringBuilder builder, GameCharacterSnapshot? character, PromptContextBudget promptBudget)
    {
        if (character is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"Active character: {character.DisplayName} ({character.Species})");
        if (character.Traits.Count > 0)
        {
            builder.AppendLine($"Traits: {string.Join(", ", character.Traits.Take(promptBudget.MaxCharacterTraits))}");
        }

        if (character.Skills.Count > 0)
        {
            builder.AppendLine($"Skills: {string.Join(", ", character.Skills.Take(promptBudget.MaxCharacterSkills).Select(skill => $"{skill.Key} {skill.Value}"))}");
        }
    }

    private static void AppendCharacterStateContext(StringBuilder builder, GameCharacterSnapshot? character)
    {
        if (character is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"Character state: alive={character.IsAlive}; playerFaction={character.IsPlayerFaction}; incapacitated={character.IsIncapacitated}");
        if (!string.IsNullOrWhiteSpace(character.Role) || !string.IsNullOrWhiteSpace(character.CurrentTask))
        {
            builder.AppendLine($"Role: {character.Role}; Current task: {character.CurrentTask}");
        }
    }

    private static void AppendLoreContext(StringBuilder builder, NarrativeCharacterProfile? lore, PromptContextBudget promptBudget)
    {
        if (lore is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Narrative pack context:");
        builder.AppendLine($"Role: {lore.Role}");
        builder.AppendLine($"Personality: {lore.Personality}");
        builder.AppendLine($"Backstory: {lore.Backstory}");
        if (lore.Traits.Count > 0)
        {
            builder.AppendLine($"Authored traits: {string.Join(", ", lore.Traits.Take(promptBudget.MaxLoreTraits))}");
        }
    }

    private static void AppendMemoryContext(StringBuilder builder, IReadOnlyList<ConversationMemoryMatch> memoryMatches, PromptContextBudget promptBudget)
    {
        if (memoryMatches.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine("Relevant memory snippets:");
        foreach (ConversationMemoryMatch match in memoryMatches.Take(promptBudget.MaxMemorySnippets))
        {
            builder.AppendLine($"- {match.Entry.Content}");
        }
    }

    private static string TrimToLength(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        if (maxChars <= 0 || trimmed.Length <= maxChars)
        {
            return maxChars <= 0 ? string.Empty : trimmed;
        }

        if (maxChars <= 3)
        {
            return trimmed[..maxChars];
        }

        return trimmed[..(maxChars - 3)] + "...";
    }

    private static ChatRequest BoundChatRequest(ChatRequest request, out bool userMessageTrimmed)
    {
        userMessageTrimmed = false;
        string normalizedUserMessage = request.UserMessage.Trim();
        if (normalizedUserMessage.Length <= ChatRequest.UserMessageMaxLength)
        {
            return request;
        }

        userMessageTrimmed = true;
        return new ChatRequest
        {
            CharacterId = request.CharacterId,
            CharacterName = request.CharacterName,
            TaskTag = request.TaskTag,
            Priority = request.Priority,
            UserMessage = TrimToLength(normalizedUserMessage, ChatRequest.UserMessageMaxLength),
            Temperature = request.Temperature,
            MaxTokens = request.MaxTokens,
            ImageBase64 = request.ImageBase64,
            ImageMimeType = request.ImageMimeType,
            RequestId = request.RequestId,
        };
    }

    private static string? TrimAssistantMessage(string? message, out bool trimmed)
    {
        trimmed = false;
        if (message is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return message;
        }

        string normalized = message.Trim();
        trimmed = normalized.Length > AssistantMessageHardCapChars;
        return TrimToLength(normalized, AssistantMessageHardCapChars);
    }

    private static string AppendStatusNotice(string statusMessage, string notice)
    {
        if (string.IsNullOrWhiteSpace(statusMessage))
        {
            return notice;
        }

        return statusMessage.EndsWith(".", StringComparison.Ordinal)
            ? $"{statusMessage} {notice}"
            : $"{statusMessage}. {notice}";
    }

    private static string FormatKnownBase(GameBaseSnapshot baseInfo) =>
        baseInfo.AreaRange.HasValue
            ? $"{baseInfo.BaseId} (range {baseInfo.AreaRange.Value.ToString("0.##", CultureInfo.InvariantCulture)})"
            : baseInfo.BaseId;

    private static string FormatLatestProduction(ProductionStatusSnapshot status)
    {
        string baseLabel = string.IsNullOrWhiteSpace(status.BaseId) ? "the base" : status.BaseId;
        string state = string.IsNullOrWhiteSpace(status.Status) ? "completed" : status.Status;
        string station = string.IsNullOrWhiteSpace(status.Station) ? string.Empty : $" at {status.Station}";
        string item = status.Quantity > 0 && !string.IsNullOrWhiteSpace(status.Item)
            ? $": {status.Quantity}x {status.Item}"
            : string.Empty;
        return $"{state}{station} in {baseLabel}{item}";
    }

    private static string FormatLatestTravel(TravelStatusSnapshot status)
    {
        string origin = string.IsNullOrWhiteSpace(status.Origin) ? "unknown" : status.Origin;
        string destination = string.IsNullOrWhiteSpace(status.Destination) ? "unknown" : status.Destination;
        string mode = string.IsNullOrWhiteSpace(status.Mode) ? "on_foot" : status.Mode;
        string waypoint = string.IsNullOrWhiteSpace(status.Waypoint) ? string.Empty : $" via {status.Waypoint}";
        return $"{origin} -> {destination}{waypoint} ({mode})";
    }

    private void Archive(string file, string archiveRoot)
    {
        Directory.CreateDirectory(archiveRoot);
        string destination = Path.Combine(archiveRoot, Path.GetFileName(file));
        if (File.Exists(destination))
        {
            destination = Path.Combine(
                archiveRoot,
                $"{Path.GetFileNameWithoutExtension(file)}-{Guid.NewGuid():N}{Path.GetExtension(file)}");
        }

        File.Move(file, destination);

        // Archive directories can fill up fast - every drained bridge event and every
        // processed screenshot lands here. Enforce retention immediately so a long
        // session cannot run the disk out. Archive and failed have separate caps.
        bool isFailed = string.Equals(archiveRoot, _options.BridgeFailedDir, StringComparison.OrdinalIgnoreCase);
        int maxFiles = isFailed ? _options.Bridge.FailedMaxFiles : _options.Bridge.ArchiveMaxFiles;
        int maxAge = isFailed ? _options.Bridge.FailedMaxAgeHours : _options.Bridge.ArchiveMaxAgeHours;
        DirectoryRetention.Enforce(archiveRoot, maxFiles, maxAge);
        InvalidateDirectoryActivitySnapshot();
    }

    private static string[] GetSortedFiles(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var files = new List<string>();
        foreach (string pattern in patterns)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly));
            }
            catch (IOException)
            {
                // Best-effort enumeration: skip patterns that fail with a transient
                // filesystem error (locked file, antivirus contention, sleeping
                // device). The other patterns may still succeed; downstream callers
                // receive a sorted union of whatever was readable.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort enumeration: skip patterns blocked by ACLs. The
                // sidecar runs as the user; an unreadable pattern just yields no
                // matches rather than crashing the helper.
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. files];
    }

    private sealed class UiProbeDumpDocument
    {
        public DateTimeOffset? GeneratedAtUtc { get; init; }

        public string Reason { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public int ObservedWidgetCount { get; init; }

        public int ActiveWidgetCount { get; init; }

        public IReadOnlyList<UiProbeWidgetEntry> Widgets { get; init; } =
            Array.Empty<UiProbeWidgetEntry>();
    }

    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(UiProbeDumpDocument))]
    private sealed partial class UiProbeDumpJsonContext : JsonSerializerContext;

    private sealed class UiProbeCandidateAccumulator
    {
        public UiProbeCandidateAccumulator(string key)
        {
            Key = key;
        }

        public string Key { get; }

        public string DisplayName { get; private set; } = string.Empty;

        public string FullName { get; private set; } = string.Empty;

        public string ClassName { get; private set; } = string.Empty;

        public int DumpCount { get; private set; }

        public int ActiveObservationCount { get; private set; }

        public int PeakSeenCount { get; private set; }

        public string LastLifecycle { get; private set; } = string.Empty;

        public DateTimeOffset? LastSeenAtUtc { get; private set; }

        public void Observe(UiProbeWidgetEntry widget, DateTimeOffset seenAtUtc)
        {
            DisplayName = TakeFirstNonBlank(widget.DisplayName, DisplayName);
            FullName = TakeFirstNonBlank(widget.FullName, FullName);
            ClassName = TakeFirstNonBlank(widget.ClassName, ClassName);
            DumpCount++;
            if (widget.IsActive)
            {
                ActiveObservationCount++;
            }

            PeakSeenCount = Math.Max(PeakSeenCount, Math.Max(0, widget.SeenCount));
            LastLifecycle = TakeFirstNonBlank(widget.LastLifecycle, LastLifecycle);
            if (!LastSeenAtUtc.HasValue || seenAtUtc >= LastSeenAtUtc.Value)
            {
                LastSeenAtUtc = seenAtUtc;
            }
        }
    }

    private sealed class DirectoryActivitySnapshot
    {
        public int OutboxPendingCount { get; init; }

        public int InboxPendingCount { get; init; }

        public int ScreenshotPendingCount { get; init; }

        public int ArchiveFileCount { get; init; }

        public int FailedFileCount { get; init; }
    }
}
