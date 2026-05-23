using Microsoft.Extensions.Options;
using PalLLM.Domain.Configuration;

namespace PalLLM.Sidecar;

/// <summary>
/// Fail-fast configuration validation for the sidecar host. The runtime already
/// degrades gracefully at request time, but invalid startup config should stop the
/// process before background workers and HTTP endpoints begin serving traffic.
/// </summary>
public sealed class PalLlmOptionsValidator : IValidateOptions<PalLlmOptions>
{
    private const int MaxPrefixCacheSaltLength = 128;
    private const int MaxStopSequenceCount = 4;
    private const int MaxStopSequenceLength = 128;
    private const int MaxTtsModelLength = 256;
    private const int MaxAsrModelLength = 256;

    public ValidateOptionsResult Validate(string? name, PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        ValidateRuntimeFolderName(options, failures);
        RequirePositive(
            options.ReleaseEvidenceFreshnessHours,
            "PalLLM:ReleaseEvidenceFreshnessHours",
            failures);
        ValidateBridge(options.Bridge, failures);
        ValidateInference(options.Inference, failures);
        ValidateVision(options.Vision, failures);
        ValidateTts(options.Tts, failures);
        ValidateAsr(options.Asr, failures);
        ValidateSession(options.Session, failures);
        ValidateFallback(options.Fallback, failures);
        ValidateHttp(options.Http, failures);
        ValidateAuth(options.Auth, failures);
        ValidateMcpClient(options.McpClient, failures);
        ValidateHardware(options.Hardware, failures);
        ValidateSelfHealing(options.SelfHealing, failures);
        ValidatePromotionFeeder(options.PromotionFeeder, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRuntimeFolderName(PalLlmOptions options, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(options.RuntimeFolderName))
        {
            failures.Add("PalLLM:RuntimeFolderName is required.");
            return;
        }

        if (options.RuntimeFolderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            failures.Add("PalLLM:RuntimeFolderName contains invalid path characters.");
        }
    }

    private static void ValidateBridge(BridgeOptions bridge, List<string> failures)
    {
        RequirePositive(bridge.PollIntervalMs, "PalLLM:Bridge:PollIntervalMs", failures);
        RequirePositive(bridge.MaxEventsPerPoll, "PalLLM:Bridge:MaxEventsPerPoll", failures);
        RequirePositive(bridge.MaxInboxEventBytes, "PalLLM:Bridge:MaxInboxEventBytes", failures);
        RequirePositive(bridge.OutboxMaxFiles, "PalLLM:Bridge:OutboxMaxFiles", failures);
        RequirePositive(bridge.OutboxMaxAgeHours, "PalLLM:Bridge:OutboxMaxAgeHours", failures);
        RequirePositive(bridge.ArchiveMaxFiles, "PalLLM:Bridge:ArchiveMaxFiles", failures);
        RequirePositive(bridge.ArchiveMaxAgeHours, "PalLLM:Bridge:ArchiveMaxAgeHours", failures);
        RequirePositive(bridge.FailedMaxFiles, "PalLLM:Bridge:FailedMaxFiles", failures);
        RequirePositive(bridge.FailedMaxAgeHours, "PalLLM:Bridge:FailedMaxAgeHours", failures);
        RequirePositive(bridge.DiagnosticsMaxFiles, "PalLLM:Bridge:DiagnosticsMaxFiles", failures);
        RequirePositive(bridge.DiagnosticsMaxAgeHours, "PalLLM:Bridge:DiagnosticsMaxAgeHours", failures);
    }

    private static void ValidateInference(InferenceOptions inference, List<string> failures)
    {
        RequirePositive(inference.TimeoutSeconds, "PalLLM:Inference:TimeoutSeconds", failures);
        RequireNonNegative(inference.CircuitBreakerFailureThreshold, "PalLLM:Inference:CircuitBreakerFailureThreshold", failures);
        RequirePositive(inference.CircuitBreakerCooldownSeconds, "PalLLM:Inference:CircuitBreakerCooldownSeconds", failures);
        RequireNonNegative(inference.MaxTransientRetries, "PalLLM:Inference:MaxTransientRetries", failures);
        RequireNonNegative(inference.TransientRetryBackoffMs, "PalLLM:Inference:TransientRetryBackoffMs", failures);
        RequireNonNegative(inference.ResidencyTtlSeconds, "PalLLM:Inference:ResidencyTtlSeconds", failures);
        RequirePositive(inference.WarmupMaxTokens, "PalLLM:Inference:WarmupMaxTokens", failures);
        RequireNonNegative(inference.WarmupIntervalSeconds, "PalLLM:Inference:WarmupIntervalSeconds", failures);
        RequirePositive(inference.TierProbeIntervalSeconds, "PalLLM:Inference:TierProbeIntervalSeconds", failures);
        RequirePositive(inference.MaxResponseBytes, "PalLLM:Inference:MaxResponseBytes", failures);
        RequirePositive(inference.ModelCatalogMaxResponseBytes, "PalLLM:Inference:ModelCatalogMaxResponseBytes", failures);
        RequireMaxLengthIfPresent(
            inference.PrefixCacheSalt,
            MaxPrefixCacheSaltLength,
            "PalLLM:Inference:PrefixCacheSalt",
            failures);
        RequireFloatRange(
            inference.Temperature,
            0.0f,
            2.0f,
            "PalLLM:Inference:Temperature",
            failures);
        RequireNullableFloatRange(
            inference.TopP,
            0.0f,
            1.0f,
            "PalLLM:Inference:TopP",
            failures);
        RequireNullableFloatRange(
            inference.PresencePenalty,
            -2.0f,
            2.0f,
            "PalLLM:Inference:PresencePenalty",
            failures);
        RequireNullableFloatRange(
            inference.FrequencyPenalty,
            -2.0f,
            2.0f,
            "PalLLM:Inference:FrequencyPenalty",
            failures);
        RequireNullableIntRange(
            inference.TopK,
            1,
            65_536,
            "PalLLM:Inference:TopK",
            failures);
        RequireNullableFloatRange(
            inference.MinP,
            0.0f,
            1.0f,
            "PalLLM:Inference:MinP",
            failures);
        RequireNullableFloatRange(
            inference.RepetitionPenalty,
            0.0f,
            2.0f,
            "PalLLM:Inference:RepetitionPenalty",
            failures);
        ValidateTokenBudgetField(inference.TokenBudgetField, failures);
        ValidateReasoningEffort(inference.ReasoningEffort, failures);
        ValidateStopSequences(inference.StopSequences, failures);

        ValidateModelTiers(inference.ModelTiers, failures);
        ValidateThermalGate(inference.ThermalGate, failures);

        if (!inference.Enabled)
        {
            return;
        }

        RequireAbsoluteUri(inference.BaseUrl, "PalLLM:Inference:BaseUrl", failures);
        RequireNonEmptyString(inference.Model, "PalLLM:Inference:Model", failures);
    }

    private static void ValidateReasoningEffort(string? reasoningEffort, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(reasoningEffort))
        {
            return;
        }

        if (!InferenceReasoningEfforts.IsAllowed(reasoningEffort.Trim()))
        {
            failures.Add(
                "PalLLM:Inference:ReasoningEffort must be empty or one of: " +
                string.Join(", ", InferenceReasoningEfforts.Allowed) + ".");
        }
    }

    private static void ValidateTokenBudgetField(string? tokenBudgetField, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(tokenBudgetField) ||
            !InferenceTokenBudgetFields.IsAllowed(tokenBudgetField.Trim()))
        {
            failures.Add(
                "PalLLM:Inference:TokenBudgetField must be one of: " +
                string.Join(", ", InferenceTokenBudgetFields.Allowed) + ".");
        }
    }

    private static void ValidateStopSequences(IReadOnlyList<string>? stopSequences, List<string> failures)
    {
        if (stopSequences is null || stopSequences.Count == 0)
        {
            return;
        }

        if (stopSequences.Count > MaxStopSequenceCount)
        {
            failures.Add($"PalLLM:Inference:StopSequences must contain {MaxStopSequenceCount} entries or fewer.");
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int index = 0; index < stopSequences.Count; index++)
        {
            string? stopSequence = stopSequences[index];
            string key = $"PalLLM:Inference:StopSequences[{index}]";

            if (string.IsNullOrWhiteSpace(stopSequence))
            {
                failures.Add($"{key} must be non-empty.");
                continue;
            }

            string trimmed = stopSequence.Trim();
            if (trimmed.Length > MaxStopSequenceLength)
            {
                failures.Add($"{key} must be {MaxStopSequenceLength} characters or fewer.");
            }

            if (!seen.Add(trimmed))
            {
                failures.Add($"{key} duplicates an earlier stop sequence after trimming.");
            }
        }
    }

    private static void ValidateModelTiers(IReadOnlyList<ModelTierOptions> tiers, List<string> failures)
    {
        // Empty list is valid - it disables tier orchestration and InferenceOptions.Model
        // is used verbatim. When tiers ARE configured, every entry must carry an id and
        // a model tag so the orchestrator can probe and surface a meaningful tier name
        // in /api/inference/* and traces.
        if (tiers is null || tiers.Count == 0)
        {
            return;
        }

        for (int index = 0; index < tiers.Count; index++)
        {
            ModelTierOptions tier = tiers[index];
            string prefix = $"PalLLM:Inference:ModelTiers[{index}]";

            if (string.IsNullOrWhiteSpace(tier.Id))
            {
                failures.Add($"{prefix}:Id is required (each model tier must have a non-empty identifier).");
            }

            if (string.IsNullOrWhiteSpace(tier.Model))
            {
                failures.Add($"{prefix}:Model is required (each model tier must name the exact model tag the inference endpoint expects).");
            }
        }
    }

    private static void ValidateThermalGate(ThermalGateOptions thermalGate, List<string> failures)
    {
        // CacheTtlSeconds gates the resampling cadence whenever the gate is consulted,
        // so a non-positive cache TTL would either pin a stale read forever (negative)
        // or force a fresh sample on every hot chat call (zero). Validate it always so
        // an operator who flips Enabled=true later doesn't get a runtime surprise.
        if (thermalGate is null)
        {
            return;
        }

        RequirePositive(thermalGate.CacheTtlSeconds, "PalLLM:Inference:ThermalGate:CacheTtlSeconds", failures);

        if (!thermalGate.Enabled)
        {
            return;
        }

        if (thermalGate.RejectAboveC <= 0)
        {
            failures.Add("PalLLM:Inference:ThermalGate:RejectAboveC must be greater than 0 when ThermalGate is enabled.");
        }

        if (thermalGate.WarnAboveC <= 0)
        {
            failures.Add("PalLLM:Inference:ThermalGate:WarnAboveC must be greater than 0 when ThermalGate is enabled.");
        }

        if (thermalGate.WarnAboveC >= thermalGate.RejectAboveC)
        {
            failures.Add("PalLLM:Inference:ThermalGate:WarnAboveC must be less than PalLLM:Inference:ThermalGate:RejectAboveC so the warn band fires before the reject band.");
        }
    }

    private static void ValidateVision(VisionOptions vision, List<string> failures)
    {
        RequireFloatRange(vision.Temperature, 0.0f, 2.0f, "PalLLM:Vision:Temperature", failures);
        RequirePositive(vision.TimeoutSeconds, "PalLLM:Vision:TimeoutSeconds", failures);
        RequirePositive(vision.DefaultMaxTokens, "PalLLM:Vision:DefaultMaxTokens", failures);
        RequirePositive(vision.MaxImageBytes, "PalLLM:Vision:MaxImageBytes", failures);
        RequirePositive(vision.MaxResponseBytes, "PalLLM:Vision:MaxResponseBytes", failures);
        RequirePositive(vision.ScreenshotPollIntervalMs, "PalLLM:Vision:ScreenshotPollIntervalMs", failures);
        RequirePositive(vision.MaxScreenshotsPerPoll, "PalLLM:Vision:MaxScreenshotsPerPoll", failures);
        RequirePositive(vision.PendingScreenshotMaxFiles, "PalLLM:Vision:PendingScreenshotMaxFiles", failures);
        RequirePositive(vision.PendingScreenshotMaxAgeHours, "PalLLM:Vision:PendingScreenshotMaxAgeHours", failures);

        if (!vision.Enabled)
        {
            return;
        }

        RequireAbsoluteUri(vision.BaseUrl, "PalLLM:Vision:BaseUrl", failures);
        RequireNonEmptyString(vision.Model, "PalLLM:Vision:Model", failures);
    }

    private static void ValidateTts(TtsOptions tts, List<string> failures)
    {
        ValidateTtsRequestFormat(tts.RequestFormat, failures);
        ValidateTtsResponseFormat(tts.ResponseFormat, failures);
        RequireMaxLengthIfPresent(tts.Model, MaxTtsModelLength, "PalLLM:Tts:Model", failures);
        RequirePositive(tts.TimeoutSeconds, "PalLLM:Tts:TimeoutSeconds", failures);
        RequirePositive(tts.MaxCharacters, "PalLLM:Tts:MaxCharacters", failures);
        RequirePositive(tts.MaxResponseBytes, "PalLLM:Tts:MaxResponseBytes", failures);
        RequirePositive(tts.MaxStoredFiles, "PalLLM:Tts:MaxStoredFiles", failures);
        RequirePositive(tts.MaxStoredAgeHours, "PalLLM:Tts:MaxStoredAgeHours", failures);

        if (!tts.Enabled)
        {
            return;
        }

        RequireAbsoluteUri(tts.BaseUrl, "PalLLM:Tts:BaseUrl", failures);
        RequireNonEmptyString(tts.DefaultVoice, "PalLLM:Tts:DefaultVoice", failures);
    }

    private static void ValidateTtsRequestFormat(string? requestFormat, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(requestFormat) ||
            !TtsRequestFormats.IsAllowed(requestFormat.Trim()))
        {
            failures.Add(
                "PalLLM:Tts:RequestFormat must be one of: " +
                string.Join(", ", TtsRequestFormats.Allowed) + ".");
        }
    }

    private static void ValidateTtsResponseFormat(string? responseFormat, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(responseFormat) ||
            !TtsResponseFormats.IsAllowed(responseFormat.Trim()))
        {
            failures.Add(
                "PalLLM:Tts:ResponseFormat must be one of: " +
                string.Join(", ", TtsResponseFormats.Allowed) + ".");
        }
    }

    private static void ValidateAsr(AsrOptions asr, List<string> failures)
    {
        ValidateAsrResponseFormat(asr.ResponseFormat, failures);
        ValidateAsrTimestampGranularities(asr.TimestampGranularities, asr.ResponseFormat, failures);
        ValidateAsrChunkingStrategy(asr.ChunkingStrategy, failures);
        RequireMaxLengthIfPresent(asr.Model, MaxAsrModelLength, "PalLLM:Asr:Model", failures);
        RequirePositive(asr.TimeoutSeconds, "PalLLM:Asr:TimeoutSeconds", failures);
        RequirePositive(asr.MaxAudioBytes, "PalLLM:Asr:MaxAudioBytes", failures);
        RequirePositive(asr.MaxResponseBytes, "PalLLM:Asr:MaxResponseBytes", failures);
        RequirePositive(asr.MaxTranscriptCharacters, "PalLLM:Asr:MaxTranscriptCharacters", failures);
        RequirePositive(asr.MaxTurnDurationMs, "PalLLM:Asr:MaxTurnDurationMs", failures);
        RequireNonNegative(asr.PreSpeechPaddingMs, "PalLLM:Asr:PreSpeechPaddingMs", failures);
        RequirePositive(asr.EndpointSilenceMs, "PalLLM:Asr:EndpointSilenceMs", failures);
        RequireNullableFloatRange(asr.Temperature, 0.0f, 1.0f, "PalLLM:Asr:Temperature", failures);
        RequireFloatRange(
            asr.LowConfidenceLogprobThreshold,
            -20.0f,
            0.0f,
            "PalLLM:Asr:LowConfidenceLogprobThreshold",
            failures);

        if (!asr.Enabled)
        {
            return;
        }

        RequireAbsoluteUri(asr.BaseUrl, "PalLLM:Asr:BaseUrl", failures);
        RequireNonEmptyString(asr.Model, "PalLLM:Asr:Model", failures);
    }

    private static void ValidateAsrResponseFormat(string? responseFormat, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(responseFormat) ||
            !AsrResponseFormats.IsAllowed(responseFormat.Trim()))
        {
            failures.Add(
                "PalLLM:Asr:ResponseFormat must be one of: " +
                string.Join(", ", AsrResponseFormats.Allowed) + ".");
        }
    }

    private static void ValidateAsrTimestampGranularities(
        IEnumerable<string>? timestampGranularities,
        string? responseFormat,
        List<string> failures)
    {
        string[] configured = (timestampGranularities ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToArray();
        if (configured.Length == 0)
        {
            return;
        }

        foreach (string value in configured)
        {
            if (!AsrTimestampGranularities.IsAllowed(value))
            {
                failures.Add(
                    "PalLLM:Asr:TimestampGranularities values must be one of: " +
                    string.Join(", ", AsrTimestampGranularities.Allowed) + ".");
                break;
            }
        }

        if (!string.Equals(
                AsrResponseFormats.Normalize(responseFormat),
                AsrResponseFormats.VerboseJson,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                "PalLLM:Asr:TimestampGranularities requires PalLLM:Asr:ResponseFormat=verbose_json.");
        }
    }

    private static void ValidateAsrChunkingStrategy(string? chunkingStrategy, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(chunkingStrategy))
        {
            return;
        }

        if (!AsrChunkingStrategies.IsAllowed(chunkingStrategy.Trim()))
        {
            failures.Add(
                "PalLLM:Asr:ChunkingStrategy must be empty or one of: " +
                string.Join(", ", AsrChunkingStrategies.Allowed) + ".");
        }
    }

    private static void ValidateSession(PalLLM.Domain.Configuration.SessionOptions session, List<string> failures)
    {
        RequirePositive(session.MaxPersistedBytes, "PalLLM:Session:MaxPersistedBytes", failures);

        if (!session.EnableAutosave)
        {
            return;
        }

        RequirePositive(session.AutosaveIntervalSeconds, "PalLLM:Session:AutosaveIntervalSeconds", failures);
    }

    private static void ValidateFallback(FallbackOptions fallback, List<string> failures)
    {
        RequirePositive(fallback.RecentMemoryWindow, "PalLLM:Fallback:RecentMemoryWindow", failures);
        RequireNonNegative(fallback.MaxCharacterRequestsPerMinute, "PalLLM:Fallback:MaxCharacterRequestsPerMinute", failures);
    }

    private static void ValidateHttp(HttpSurfaceOptions http, List<string> failures)
    {
        RequireNonNegative(http.OpenApiCacheMinutes, "PalLLM:Http:OpenApiCacheMinutes", failures);
        RequireNonNegative(http.FeatureCatalogCacheMinutes, "PalLLM:Http:FeatureCatalogCacheMinutes", failures);
        RequireNonNegative(http.SelfDescriptionCacheSeconds, "PalLLM:Http:SelfDescriptionCacheSeconds", failures);
        RequireNonNegative(http.UpstreamSnapshotCacheSeconds, "PalLLM:Http:UpstreamSnapshotCacheSeconds", failures);
        RequirePositive(http.LocalArtifactMaxBytes, "PalLLM:Http:LocalArtifactMaxBytes", failures);
        RequirePositive(http.ApiRequestBodyMaxBytes, "PalLLM:Http:ApiRequestBodyMaxBytes", failures);
        RequirePositive(http.ChatConcurrentRequests, "PalLLM:Http:ChatConcurrentRequests", failures);
        RequireNonNegative(http.ChatQueueLimit, "PalLLM:Http:ChatQueueLimit", failures);
        RequirePositive(http.ChatRequestTimeoutSeconds, "PalLLM:Http:ChatRequestTimeoutSeconds", failures);
        RequirePositive(http.VisionConcurrentRequests, "PalLLM:Http:VisionConcurrentRequests", failures);
        RequireNonNegative(http.VisionQueueLimit, "PalLLM:Http:VisionQueueLimit", failures);
        RequirePositive(http.VisionRequestTimeoutSeconds, "PalLLM:Http:VisionRequestTimeoutSeconds", failures);
        RequirePositive(http.TtsConcurrentRequests, "PalLLM:Http:TtsConcurrentRequests", failures);
        RequireNonNegative(http.TtsQueueLimit, "PalLLM:Http:TtsQueueLimit", failures);
        RequirePositive(http.TtsRequestTimeoutSeconds, "PalLLM:Http:TtsRequestTimeoutSeconds", failures);
    }

    private static void ValidateAuth(AuthOptions auth, List<string> failures)
    {
        foreach (string origin in auth.McpAllowedOrigins)
        {
            if (string.IsNullOrWhiteSpace(origin))
            {
                failures.Add("PalLLM:Auth:McpAllowedOrigins[] entries must be non-empty absolute HTTP(S) origins.");
                continue;
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add($"PalLLM:Auth:McpAllowedOrigins[] entry '{origin}' must be an absolute HTTP(S) origin.");
            }
        }
    }

    private static void ValidateMcpClient(McpClientOptions mcpClient, List<string> failures)
    {
        RequirePositive(mcpClient.DiscoveryIntervalSeconds, "PalLLM:McpClient:DiscoveryIntervalSeconds", failures);
        RequirePositive(mcpClient.DiscoveryTimeoutSeconds, "PalLLM:McpClient:DiscoveryTimeoutSeconds", failures);
        RequirePositive(mcpClient.MaxToolsPerServer, "PalLLM:McpClient:MaxToolsPerServer", failures);
        RequirePositive(mcpClient.MaxResourcesPerServer, "PalLLM:McpClient:MaxResourcesPerServer", failures);
        RequirePositive(mcpClient.MaxPromptsPerServer, "PalLLM:McpClient:MaxPromptsPerServer", failures);
        RequirePositive(mcpClient.MaxMetadataEntryLength, "PalLLM:McpClient:MaxMetadataEntryLength", failures);

        // Each populated upstream entry must declare a non-empty Id and Url so the
        // discovery pool has something to record and probe. We deliberately do NOT
        // reject malformed URLs at startup — McpUpstreamClientPool surfaces those
        // at probe time as a structured ErrorCode="invalid_endpoint" snapshot in
        // GET /api/mcp/upstream, which is the more operator-friendly UX (the
        // sidecar still boots; the broken upstream is named, not hidden). The test
        // McpUpstreamClientTests.UpstreamPool_WhenConfiguredServerHasInvalidUrl_*
        // pins that contract.
        IReadOnlyList<McpUpstreamServer> servers = mcpClient.UpstreamServers;
        if (servers is null || servers.Count == 0)
        {
            return;
        }

        for (int index = 0; index < servers.Count; index++)
        {
            McpUpstreamServer server = servers[index];
            string prefix = $"PalLLM:McpClient:UpstreamServers[{index}]";

            if (string.IsNullOrWhiteSpace(server.Id))
            {
                failures.Add($"{prefix}:Id is required (each upstream MCP server must have a non-empty identifier so it can be named in logs and /api/mcp/upstream).");
            }

            if (string.IsNullOrWhiteSpace(server.Url))
            {
                failures.Add($"{prefix}:Url is required (each upstream MCP server must have a non-empty endpoint URL).");
            }
        }
    }

    private static void ValidateHardware(HardwareOptions hardware, List<string> failures)
    {
        // ForceTier is null / empty by default (auto-detect). When set, it must
        // match a DuoHardwareTier enum member (case-insensitive); a typo like
        // "Generus" would otherwise be silently ignored at runtime and the
        // operator would wonder why their override has no effect.
        if (string.IsNullOrWhiteSpace(hardware.ForceTier))
        {
            return;
        }

        if (!Enum.TryParse<PalLLM.Domain.Inference.DuoHardwareTier>(hardware.ForceTier.Trim(), ignoreCase: true, out _))
        {
            failures.Add(
                $"PalLLM:Hardware:ForceTier '{hardware.ForceTier}' is not a recognised tier. " +
                "Allowed values: Constrained, Standard, Generous (or empty for auto-detect).");
        }
    }

    private static void ValidateSelfHealing(SelfHealingOptions selfHealing, List<string> failures)
    {
        // The watchdog floors most of these to safe defaults at runtime, but a
        // negative value still indicates operator confusion. Validate at startup
        // so the operator gets a clear field name in the failure list instead of
        // silently-floored behavior.
        RequirePositive(selfHealing.CheckIntervalSeconds, "PalLLM:SelfHealing:CheckIntervalSeconds", failures);
        RequireNonNegative(selfHealing.OrphanEnvelopeAgeSeconds, "PalLLM:SelfHealing:OrphanEnvelopeAgeSeconds", failures);
        RequireNonNegative(selfHealing.UnhealthyScoreFloor, "PalLLM:SelfHealing:UnhealthyScoreFloor", failures);
        RequireNonNegative(selfHealing.HistoryRetention, "PalLLM:SelfHealing:HistoryRetention", failures);
    }

    private static void ValidatePromotionFeeder(PromotionFeederOptions promotionFeeder, List<string> failures)
    {
        RequirePositive(promotionFeeder.CheckIntervalSeconds, "PalLLM:PromotionFeeder:CheckIntervalSeconds", failures);
        RequirePositive(promotionFeeder.MaxObservationsPerStrategyPerTick, "PalLLM:PromotionFeeder:MaxObservationsPerStrategyPerTick", failures);
    }

    private static void RequireAbsoluteUri(string? value, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} is required when the feature is enabled.");
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            failures.Add($"{key} must be an absolute URI.");
        }
    }

    private static void RequireNonEmptyString(string? value, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"{key} is required when the feature is enabled.");
        }
    }

    private static void RequireMaxLengthIfPresent(string? value, int maxLength, string key, List<string> failures)
    {
        if (!string.IsNullOrEmpty(value) && value.Length > maxLength)
        {
            failures.Add($"{key} must be {maxLength} characters or fewer.");
        }
    }

    private static void RequirePositive(int value, string key, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{key} must be greater than 0.");
        }
    }

    private static void RequireNonNegative(int value, string key, List<string> failures)
    {
        if (value < 0)
        {
            failures.Add($"{key} must be greater than or equal to 0.");
        }
    }

    private static void RequireFloatRange(float value, float min, float max, string key, List<string> failures)
    {
        if (float.IsNaN(value) || float.IsInfinity(value) || value < min || value > max)
        {
            failures.Add($"{key} must be between {min} and {max}.");
        }
    }

    private static void RequireNullableFloatRange(float? value, float min, float max, string key, List<string> failures)
    {
        if (value is not { } actual)
        {
            return;
        }

        if (float.IsNaN(actual) || float.IsInfinity(actual) || actual < min || actual > max)
        {
            failures.Add($"{key} must be between {min} and {max}.");
        }
    }

    private static void RequireNullableIntRange(int? value, int min, int max, string key, List<string> failures)
    {
        if (value is not { } actual)
        {
            return;
        }

        if (actual < min || actual > max)
        {
            failures.Add($"{key} must be between {min} and {max}.");
        }
    }
}
