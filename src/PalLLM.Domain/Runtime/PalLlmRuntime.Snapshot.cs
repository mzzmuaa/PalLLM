using System.Diagnostics;
using PalLLM.Domain;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Health + activity snapshot construction. Composes
//            GameWorldSnapshot, BridgeActivitySnapshot,
//            DirectoryActivitySnapshot, and runtime gauges into the
//            RuntimeHealth payload consumed by /api/health, the dashboard,
//            and the SLO Prometheus scrape.
//   surface: PalLlmRuntime.GetHealth(),
//            PalLlmRuntime.GetDirectoryActivitySnapshot (private),
//            PalLlmRuntime.BuildRuntimeHealth (private).
//   gate:    tests/PalLLM.Tests/RuntimeTests.cs +
//            tests/PalLLM.Tests/SidecarEndpointTests.cs (/api/health
//            route).
//   adr:     None directly (composes ADR 0001 + ADR 0003 surfaces).
//   docs:    docs/API.md (/api/health), docs/OBSERVABILITY.md,
//            docs/OBSERVABILITY_SLO.md.
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    public RuntimeHealth GetHealth()
    {
        GameWorldSnapshot snapshot = Adapter.Snapshot;
        BridgeActivitySnapshot bridge = GetBridgeActivity();
        DirectoryActivitySnapshot directoryActivity = GetDirectoryActivitySnapshot();
        return BuildRuntimeHealth(snapshot, bridge, directoryActivity);
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

    public RuntimeWorldState GetWorldState()
    {
        GameWorldSnapshot snapshot = Adapter.Snapshot;
        BridgeActivitySnapshot bridge = GetBridgeActivity();
        return BuildWorldState(snapshot, bridge);
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

    public void UpdateSnapshot(GameWorldSnapshot snapshot)
    {
        Adapter.UpdateSnapshot(snapshot);
        PalStatusLine.SetReady(PalTextCatalog.Get("status.snapshot"));
        PalStatusLine.NoteActivity();
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
}
