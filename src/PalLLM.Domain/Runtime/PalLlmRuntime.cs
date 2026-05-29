using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
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
//            GetMemorySnapshot. The primary runtime spine is ~1104 lines;
//            pure helpers, inference warmup/metrics, UI-probe diagnostics,
//            prompt rendering, snapshot assembly, and bridge/outbox activity
//            live in companion partials.
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
    private static readonly TimeSpan InferenceWarmupMinimumGap = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions BridgeJsonOptions = CreateBridgeJsonOptions();

    private static readonly PalLlmDomainJsonSerializerContext BridgeJsonContext = new(BridgeJsonOptions);

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

    public IReadOnlyList<FeatureDescriptor> GetFeatures() => PalLlmFeatureCatalog.All;

    public IReadOnlyList<PackSummary> GetPacks() => NarrativePacks.GetSummaries();

    public IReadOnlyList<AdapterLogEntry> GetLogs() => Adapter.RecentLogs;

    public void ReloadPacks() => NarrativePacks.Reload();

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
                ClientRequestId = request.RequestId,
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

}
