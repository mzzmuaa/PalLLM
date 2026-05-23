using System.Diagnostics;
using System.Text.Json;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// High-level vision use cases for PalLLM. Rather than exposing the raw
/// <see cref="IVisionClient"/> everywhere, the orchestrator owns the prompts and
/// the JSON parsing, so the sidecar endpoints and chat path stay declarative.
/// </summary>
internal sealed class VisionOrchestrator
{
    // Structured extraction prompt. Kept terse: the schema is the contract,
    // and modern instruction-tuned multimodal models follow JSON schemas well at
    // low temperature.
    private const string WorldStateSystemPrompt =
        "You are PalLLM's Palworld scene analyst. Inspect the screenshot and reply with ONE JSON object, nothing else. " +
        "Schema (omit fields you are unsure about):\n" +
        "{\"TimeOfDay\":\"dawn|day|dusk|night\",\"Weather\":\"clear|rain|storm|fog|snow|sand|ash|wind\"," +
        "\"Biome\":\"short label\",\"InCombat\":true|false,\"InBase\":true|false,\"VisibleHostileCount\":int," +
        "\"PlayerActivity\":\"one short phrase\",\"NotableLandmark\":\"one short phrase\"," +
        "\"LightLevel\":0.0-1.0,\"Hostiles\":[\"name\",...],\"Resources\":[\"name\",...]}\n" +
        "Respond only with JSON.";

    private const string ChatAugmentationSystemPrompt =
        "You are PalLLM's scene summariser. Describe what the player can see in ONE compact sentence " +
        "(under 25 words), focused on what matters for tactical or companion decisions (threats, weather, " +
        "landmarks, current activity). No preamble, no pleasantries.";

    private static readonly JsonSerializerOptions ParseOptions = PalLlmDomainJsonOptions.Create(static options =>
    {
        options.PropertyNameCaseInsensitive = true;
        options.AllowTrailingCommas = true;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
    });
    private static readonly PalLlmDomainJsonSerializerContext ParseJsonContext = new(ParseOptions);

    // Schema forwarded via chat-completions `response_format` when structured
    // outputs are enabled. Keep it as pre-parsed JSON so the request hot path
    // stays reflection-free and Native-AOT-friendly.
    private const string WorldStateResponseFormatJson = """
    {
      "type": "json_schema",
      "json_schema": {
        "name": "palllm_world_state",
        "schema": {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "TimeOfDay": { "type": [ "string", "null" ] },
            "Weather": { "type": [ "string", "null" ] },
            "Biome": { "type": [ "string", "null" ] },
            "InCombat": { "type": [ "boolean", "null" ] },
            "InBase": { "type": [ "boolean", "null" ] },
            "VisibleHostileCount": { "type": [ "integer", "null" ] },
            "PlayerActivity": { "type": [ "string", "null" ] },
            "NotableLandmark": { "type": [ "string", "null" ] },
            "LightLevel": { "type": [ "number", "null" ] },
            "Hostiles": { "type": "array", "items": { "type": "string" } },
            "Resources": { "type": "array", "items": { "type": "string" } }
          }
        }
      }
    }
    """;

    private static readonly JsonElement WorldStateResponseFormat = CreateWorldStateResponseFormat();

    private readonly IVisionClient _client;
    private readonly PalLlmOptions _options;
    private readonly InferencePerformanceTracker _performance;

    public VisionOrchestrator(
        IVisionClient client,
        PalLlmOptions options,
        InferencePerformanceTracker performance)
    {
        _client = client;
        _options = options;
        _performance = performance;
    }

    private static JsonElement CreateWorldStateResponseFormat()
    {
        using JsonDocument document = JsonDocument.Parse(WorldStateResponseFormatJson);
        return document.RootElement.Clone();
    }

    /// Short natural-language description. Used both by the public
    /// /api/vision/describe endpoint and inline by ChatAsync when a request
    /// carries an ImageBase64 and vision-for-chat is enabled.
    public async Task<VisionDescribeResponse> DescribeAsync(
        VisionDescribeRequest request,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        VisionResult result = await _client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = request.ImageBase64,
            ImageMimeType = request.ImageMimeType ?? "image/png",
            SystemPrompt = string.IsNullOrWhiteSpace(request.SystemPrompt)
                ? ChatAugmentationSystemPrompt
                : request.SystemPrompt,
            UserPrompt = request.Prompt ?? string.Empty,
            MaxTokens = request.MaxTokens,
            Temperature = request.Temperature,
        }, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        RecordVisionOperation(result, stopwatch.ElapsedMilliseconds);

        return new VisionDescribeResponse
        {
            Success = result.Success,
            Description = result.Content ?? string.Empty,
            StatusMessage = result.StatusMessage,
            Model = _options.Vision.Model,
            LatencyMs = stopwatch.ElapsedMilliseconds,
        };
    }

    /// Structured world-state extraction. Emits a compact JSON object that the
    /// runtime can merge into its snapshot as a complementary sensor to UE4SS.
    public async Task<(VisionWorldStateResponse Response, VisionWorldStateSnapshot? Parsed)> ExtractWorldStateAsync(
        VisionWorldStateRequest request,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        string userPrompt = string.IsNullOrWhiteSpace(request.Hint)
            ? "Analyse this Palworld screenshot and return the JSON."
            : $"Analyse this Palworld screenshot and return the JSON. Hint: {request.Hint}";

        VisionResult result = await _client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = request.ImageBase64,
            ImageMimeType = request.ImageMimeType ?? "image/png",
            SystemPrompt = WorldStateSystemPrompt,
            UserPrompt = userPrompt,
            MaxTokens = 240,
            Temperature = 0.15f,
            ResponseFormat = _options.Vision.UseStructuredOutputs ? WorldStateResponseFormat : null,
        }, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        RecordVisionOperation(result, stopwatch.ElapsedMilliseconds);

        if (!result.Success)
        {
            return (new VisionWorldStateResponse
            {
                Success = false,
                StatusMessage = result.StatusMessage,
                Model = _options.Vision.Model,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                RawContent = result.Content,
                State = null,
                Applied = false,
            }, null);
        }

        VisionWorldStateSnapshot? parsed = TryParseSnapshot(result.Content);
        return (new VisionWorldStateResponse
        {
            Success = parsed is not null,
            StatusMessage = parsed is null
                ? "Vision call succeeded but the JSON did not parse against the world-state schema."
                : "Vision world-state extracted.",
            Model = _options.Vision.Model,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            RawContent = result.Content,
            State = parsed,
            Applied = false,
        }, parsed);
    }

    private void RecordVisionOperation(VisionResult result, long fallbackLatencyMs)
    {
        if (!result.IsConfigured)
        {
            return;
        }

        _performance.Record(new InferencePerformanceSample(
            GenAiTelemetry.OperationGenerateContent,
            string.IsNullOrWhiteSpace(result.ProviderName) ? "openai_compatible" : result.ProviderName,
            string.IsNullOrWhiteSpace(result.RequestModel) ? _options.Vision.Model : result.RequestModel,
            string.IsNullOrWhiteSpace(result.ResponseModel) ? null : result.ResponseModel,
            result.Success,
            string.IsNullOrWhiteSpace(result.ErrorType) ? null : result.ErrorType,
            result.LatencyMs > 0 ? result.LatencyMs : fallbackLatencyMs,
            result.Usage.PromptTokens,
            result.Usage.CompletionTokens,
            DateTimeOffset.UtcNow,
            FinishReasons: result.FinishReasons,
            UpstreamRequestId: string.IsNullOrWhiteSpace(result.UpstreamRequestId) ? null : result.UpstreamRequestId,
            UpstreamProcessingMs: result.UpstreamProcessingMs,
            UpstreamQueueMs: result.UpstreamQueueMs,
            UpstreamTimeToFirstTokenMs: result.UpstreamTimeToFirstTokenMs,
            UpstreamPrefillMs: result.UpstreamPrefillMs,
            UpstreamDecodeMs: result.UpstreamDecodeMs,
            CachedPromptTokens: result.Usage.CachedPromptTokens,
            PromptAudioTokens: result.Usage.PromptAudioTokens,
            CompletionReasoningTokens: result.Usage.CompletionReasoningTokens,
            CompletionAudioTokens: result.Usage.CompletionAudioTokens,
            AcceptedPredictionTokens: result.Usage.AcceptedPredictionTokens,
            RejectedPredictionTokens: result.Usage.RejectedPredictionTokens));
    }

    private static VisionWorldStateSnapshot? TryParseSnapshot(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // Models occasionally wrap JSON in code fences or add a prose preamble even
        // with a strict prompt. Peel the outer braces before deserialising.
        int firstBrace = raw.IndexOf('{');
        int lastBrace = raw.LastIndexOf('}');
        if (firstBrace < 0 || lastBrace <= firstBrace)
        {
            return null;
        }

        string candidate = raw.Substring(firstBrace, lastBrace - firstBrace + 1);
        try
        {
            VisionWorldStateSnapshot? parsed = JsonSerializer.Deserialize(candidate, ParseJsonContext.VisionWorldStateSnapshot);
            return parsed is null ? null : NormalizeSnapshot(parsed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VisionWorldStateSnapshot NormalizeSnapshot(VisionWorldStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new VisionWorldStateSnapshot
        {
            TimeOfDay = snapshot.TimeOfDay,
            Weather = snapshot.Weather,
            Biome = snapshot.Biome,
            InCombat = snapshot.InCombat,
            InBase = snapshot.InBase,
            VisibleHostileCount = snapshot.VisibleHostileCount,
            PlayerActivity = snapshot.PlayerActivity,
            NotableLandmark = snapshot.NotableLandmark,
            LightLevel = snapshot.LightLevel,
            Hostiles = snapshot.Hostiles?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [],
            Resources = snapshot.Resources?
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? [],
        };
    }

    public GameWorldSnapshot MergeIntoSnapshot(GameWorldSnapshot current, VisionWorldStateSnapshot visual)
    {
        // Every merge is conservative: only overwrite a snapshot field when the vision
        // extractor produced a non-empty value. That way the UE4SS bridge remains the
        // primary authority and vision acts as a fill-in sensor.
        string timeOfDay = !string.IsNullOrWhiteSpace(visual.TimeOfDay) ? visual.TimeOfDay! : current.TimeOfDay;
        string weather = !string.IsNullOrWhiteSpace(visual.Weather) ? visual.Weather! : current.Weather;
        string biome = !string.IsNullOrWhiteSpace(visual.Biome) ? visual.Biome! : current.Biome;
        bool? inBase = visual.InBase ?? current.IsInBase;

        List<string> nearbyHostiles = visual.Hostiles.Count > 0
            ? visual.Hostiles
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [.. current.NearbyHostiles];

        // If the model reported a visible-hostile count that exceeds the names it
        // actually listed, pad with generic placeholders so the downstream Fallback
        // context picks up the correct hostile count. Keeps the extractor honest
        // even when it struggles to name every enemy on screen.
        if (visual.VisibleHostileCount.HasValue && visual.VisibleHostileCount.Value > nearbyHostiles.Count)
        {
            int missing = visual.VisibleHostileCount.Value - nearbyHostiles.Count;
            for (int i = 1; i <= missing; i++)
            {
                nearbyHostiles.Add($"unidentified-hostile-{nearbyHostiles.Count + 1}");
            }
        }

        List<string> nearbyResources = visual.Resources.Count > 0
            ? visual.Resources
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [.. current.NearbyResources];

        List<string> recentEvents = [.. current.RecentEvents];
        string marker = $"vision:{(visual.InCombat == true ? "combat" : "scene")}:" +
            (string.IsNullOrWhiteSpace(visual.PlayerActivity) ? "observed" : visual.PlayerActivity);
        recentEvents.RemoveAll(v => string.Equals(v, marker, StringComparison.OrdinalIgnoreCase));
        recentEvents.Insert(0, marker);
        while (recentEvents.Count > 12)
        {
            recentEvents.RemoveAt(recentEvents.Count - 1);
        }

        return new GameWorldSnapshot
        {
            Source = "vision",
            WorldName = current.WorldName,
            IsWorldLoaded = current.IsWorldLoaded,
            CurrentTick = current.CurrentTick,
            TicksPerHour = current.TicksPerHour,
            TicksPerDay = current.TicksPerDay,
            CapturedAtUtc = DateTimeOffset.UtcNow,
            Biome = biome,
            Weather = weather,
            TimeOfDay = timeOfDay,
            ThreatLevel = visual.InCombat == true
                ? Math.Max(current.ThreatLevel ?? 0f, 0.6f)
                : current.ThreatLevel,
            AlertLevel = current.AlertLevel,
            PlayerHealthFraction = current.PlayerHealthFraction,
            PlayerStaminaFraction = current.PlayerStaminaFraction,
            PlayerHungerFraction = current.PlayerHungerFraction,
            CurrentObjective = current.CurrentObjective,
            LastTravel = current.LastTravel,
            LastProduction = current.LastProduction,
            IsInBase = inBase,
            ActiveBaseIds = [.. current.ActiveBaseIds],
            KnownBases = [.. current.KnownBases],
            NearbyHostiles = nearbyHostiles,
            NearbyFriendlies = [.. current.NearbyFriendlies],
            NearbyResources = nearbyResources,
            RecentEvents = recentEvents,
            Characters = [.. current.Characters],
        };
    }
}
