using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using PalLLM.Domain;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
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

    private static BoundedJsonFileReader.JsonReadResult<BridgeEventEnvelope> TryReadBridgeEventEnvelope(string file, int maxBytes) =>
        BoundedJsonFileReader.TryRead(
            file,
            maxBytes,
            stream => JsonSerializer.Deserialize(stream, BridgeJsonContext.BridgeEventEnvelope));

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
}
