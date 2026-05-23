using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

public interface IAudioTranscriptionClient
{
    Task<AudioTranscriptionResult> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken);
}

public sealed class DisabledAudioTranscriptionClient : IAudioTranscriptionClient
{
    public Task<AudioTranscriptionResult> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(AudioTranscriptionResult.Disabled(
            "ASR is disabled. Configure PalLLM:Asr to enable audio transcription."));
}

/// <summary>
/// Minimal OpenAI-compatible ASR adapter. Sends bounded local audio as
/// multipart/form-data to <c>/v1/audio/transcriptions</c> style endpoints and
/// parses the returned <c>{ "text": "..." }</c> payload.
/// </summary>
public sealed class HttpAudioTranscriptionClient : IAudioTranscriptionClient
{
    private const string Surface = "ASR";
    private const string ResponseLabel = "ASR transcription JSON";
    private const double SegmentLowAverageLogprobThreshold = -1.0d;
    private const double SegmentHighCompressionRatioThreshold = 2.4d;
    private const double SegmentSilentNoSpeechProbabilityThreshold = 1.0d;

    private readonly HttpClient _httpClient;
    private readonly PalLlmOptions _options;

    public HttpAudioTranscriptionClient(HttpClient httpClient, PalLlmOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<AudioTranscriptionResult> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AsrOptions asr = _options.Asr;

        if (!asr.Enabled || string.IsNullOrWhiteSpace(asr.BaseUrl))
        {
            return AudioTranscriptionResult.Disabled(
                "ASR is disabled. Configure PalLLM:Asr to enable audio transcription.");
        }

        if (string.IsNullOrWhiteSpace(request.AudioBase64))
        {
            return AudioTranscriptionResult.Failed("ASR request had no audio.");
        }

        Base64PayloadInspection inspection = Base64PayloadInspector.Inspect(
            request.AudioBase64,
            asr.MaxAudioBytes);
        if (!inspection.Accepted)
        {
            return AudioTranscriptionResult.Failed(
                Base64PayloadInspector.BuildAudioFailureMessage(inspection, asr.MaxAudioBytes));
        }

        byte[] audioBytes;
        try
        {
            audioBytes = Convert.FromBase64String(request.AudioBase64);
        }
        catch (FormatException)
        {
            return AudioTranscriptionResult.Failed("AudioBase64 must be valid base64 audio data.");
        }

        string mimeType = AsrAudioMimeTypes.Normalize(request.AudioMimeType);
        using var form = new MultipartFormDataContent();
        using var audioContent = new ByteArrayContent(audioBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(mimeType);
        form.Add(audioContent, "file", AsrAudioMimeTypes.ToFileName(mimeType));

        string model = NormalizeOptional(asr.Model) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(model))
        {
            form.Add(new StringContent(model, Encoding.UTF8), "model");
        }

        string? language = NormalizeOptional(request.Language);
        if (language is not null)
        {
            form.Add(new StringContent(language, Encoding.UTF8), "language");
        }

        string? prompt = NormalizeOptional(request.Prompt);
        if (prompt is not null)
        {
            form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
        }

        string chunkingStrategy = AsrChunkingStrategies.Normalize(asr.ChunkingStrategy);
        if (!string.IsNullOrWhiteSpace(chunkingStrategy))
        {
            form.Add(new StringContent(chunkingStrategy, Encoding.UTF8), "chunking_strategy");
        }

        if (asr.Temperature is { } temperature)
        {
            form.Add(
                new StringContent(temperature.ToString(CultureInfo.InvariantCulture), Encoding.UTF8),
                "temperature");
        }

        string responseFormat = AsrResponseFormats.Normalize(asr.ResponseFormat);
        string[] timestampGranularities = string.Equals(
                responseFormat,
                AsrResponseFormats.VerboseJson,
                StringComparison.OrdinalIgnoreCase)
            ? AsrTimestampGranularities.NormalizeMany(asr.TimestampGranularities)
            : [];

        form.Add(
            new StringContent(responseFormat, Encoding.UTF8),
            "response_format");
        foreach (string timestampGranularity in timestampGranularities)
        {
            form.Add(new StringContent(timestampGranularity, Encoding.UTF8), "timestamp_granularities[]");
        }

        if (asr.RequestLogprobs)
        {
            form.Add(new StringContent("logprobs", Encoding.UTF8), "include[]");
        }

        AudioTranscriptionConfidenceReceipt missingConfidence =
            BuildMissingConfidenceReceipt(asr.RequestLogprobs, asr.LowConfidenceLogprobThreshold);
        AudioTranscriptionTimingReceipt missingTiming =
            BuildMissingTimingReceipt(responseFormat, timestampGranularities, asr.MaxTurnDurationMs);
        AudioTranscriptionQualityReceipt missingQuality = BuildMissingQualityReceipt(responseFormat);

        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, asr.TimeoutSeconds));
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        CancellationToken effectiveCancellationToken = requestTimeout.Token;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, asr.BaseUrl)
        {
            Content = form,
        };
        if (!string.IsNullOrWhiteSpace(asr.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", asr.ApiKey);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        HttpResponseMessage? response = null;
        string upstreamRequestId = string.Empty;
        double? upstreamProcessingMs = null;
        UpstreamPhaseTimingReceipt upstreamPhaseTimings = UpstreamPhaseTimingReceipt.Empty;
        try
        {
            response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            upstreamRequestId = HttpResponseReceiptExtractor.GetUpstreamRequestId(response);
            upstreamProcessingMs = HttpResponseReceiptExtractor.GetUpstreamProcessingMs(response);
            upstreamPhaseTimings = HttpResponseReceiptExtractor.GetUpstreamPhaseTimings(response);

            if (!response.IsSuccessStatusCode)
            {
                _ = await ReadStatusBodyAsync(
                        response.Content,
                        asr.MaxResponseBytes,
                        effectiveCancellationToken)
                    .ConfigureAwait(false);
                return AudioTranscriptionResult.Failed(
                    TransportFailureStatusBuilder.HttpStatus(Surface, (int)response.StatusCode),
                    model,
                    audioBytes.Length,
                    stopwatch.ElapsedMilliseconds,
                    missingConfidence,
                    timing: missingTiming,
                    quality: missingQuality,
                    upstreamRequestId: upstreamRequestId,
                    upstreamProcessingMs: upstreamProcessingMs,
                    upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                    upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                    upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
            }

            HttpContentReadLimiter.BoundedTextReadResult readResult =
                await HttpContentReadLimiter.ReadTextAsync(
                        response.Content,
                        asr.MaxResponseBytes,
                        effectiveCancellationToken)
                    .ConfigureAwait(false);
            if (readResult.ExceededLimit)
            {
                return AudioTranscriptionResult.Failed(
                    HttpContentReadLimiter.BuildExceededLimitMessage(ResponseLabel, asr.MaxResponseBytes),
                    model,
                    audioBytes.Length,
                    stopwatch.ElapsedMilliseconds,
                    missingConfidence,
                    timing: missingTiming,
                    quality: missingQuality,
                    upstreamRequestId: upstreamRequestId,
                    upstreamProcessingMs: upstreamProcessingMs,
                    upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                    upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                    upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
            }

            TranscriptionParseResult parsed = ParseTranscriptionResponse(
                readResult.Text,
                asr.RequestLogprobs,
                asr.LowConfidenceLogprobThreshold,
                responseFormat,
                timestampGranularities,
                asr.MaxTurnDurationMs);
            string transcript = parsed.Transcript;
            AudioTranscriptionConfidenceReceipt confidence = parsed.Confidence;
            AudioTranscriptionTimingReceipt timing = parsed.Timing;
            AudioTranscriptionQualityReceipt quality = parsed.Quality;
            if (string.IsNullOrWhiteSpace(transcript))
            {
                return AudioTranscriptionResult.Failed(
                    "ASR endpoint returned no transcript text.",
                    model,
                    audioBytes.Length,
                    stopwatch.ElapsedMilliseconds,
                    confidence,
                    timing: timing,
                    quality: quality,
                    upstreamRequestId: upstreamRequestId,
                    upstreamProcessingMs: upstreamProcessingMs,
                    upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                    upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                    upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
            }

            if (transcript.Length > asr.MaxTranscriptCharacters)
            {
                return AudioTranscriptionResult.Failed(
                    $"ASR transcript exceeds the configured cap of {asr.MaxTranscriptCharacters} characters.",
                    model,
                    audioBytes.Length,
                    stopwatch.ElapsedMilliseconds,
                    confidence,
                    timing: timing,
                    quality: quality,
                    upstreamRequestId: upstreamRequestId,
                    upstreamProcessingMs: upstreamProcessingMs,
                    upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                    upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                    upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
            }

            return AudioTranscriptionResult.Succeeded(
                transcript,
                model,
                audioBytes.Length,
                stopwatch.ElapsedMilliseconds,
                confidence,
                timing: timing,
                quality: quality,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return AudioTranscriptionResult.Failed(
                TransportFailureStatusBuilder.Timeout(Surface),
                model,
                audioBytes.Length,
                stopwatch.ElapsedMilliseconds,
                missingConfidence,
                timing: missingTiming,
                quality: missingQuality,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
        }
        catch (HttpRequestException)
        {
            return AudioTranscriptionResult.Failed(
                TransportFailureStatusBuilder.Unreachable(Surface),
                model,
                audioBytes.Length,
                stopwatch.ElapsedMilliseconds,
                missingConfidence,
                timing: missingTiming,
                quality: missingQuality,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
        }
        catch (JsonException)
        {
            return AudioTranscriptionResult.Failed(
                TransportFailureStatusBuilder.MalformedJson(Surface),
                model,
                audioBytes.Length,
                stopwatch.ElapsedMilliseconds,
                missingConfidence,
                timing: missingTiming,
                quality: missingQuality,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
        }
        finally
        {
            stopwatch.Stop();
            response?.Dispose();
        }
    }

    private static async Task<string> ReadStatusBodyAsync(
        HttpContent content,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        int maxErrorBytes = Math.Max(1_024, Math.Min(maxResponseBytes, 8 * 1_024));
        HttpContentReadLimiter.BoundedTextReadResult readResult =
            await HttpContentReadLimiter.ReadTextAsync(content, maxErrorBytes, cancellationToken)
                .ConfigureAwait(false);
        return readResult.ExceededLimit
            ? $"[response body exceeded {maxErrorBytes} bytes]"
            : readResult.Text;
    }

    private static TranscriptionParseResult ParseTranscriptionResponse(
        string json,
        bool logprobsRequested,
        float lowConfidenceThreshold,
        string responseFormat,
        IReadOnlyList<string> requestedGranularities,
        int maxTurnDurationMs)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return new TranscriptionParseResult(
            ParseTranscript(root),
            ParseConfidenceReceipt(root, logprobsRequested, lowConfidenceThreshold),
            ParseTimingReceipt(root, responseFormat, requestedGranularities, maxTurnDurationMs),
            ParseQualityReceipt(root, responseFormat));
    }

    private static string ParseTranscript(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        return root.TryGetProperty("text", out JsonElement text) &&
               text.ValueKind == JsonValueKind.String
            ? text.GetString()?.Trim() ?? string.Empty
            : string.Empty;
    }

    private static AudioTranscriptionConfidenceReceipt ParseConfidenceReceipt(
        JsonElement root,
        bool requested,
        float lowConfidenceThreshold)
    {
        if (!requested)
        {
            return BuildMissingConfidenceReceipt(false, lowConfidenceThreshold);
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("logprobs", out JsonElement logprobs) ||
            logprobs.ValueKind != JsonValueKind.Array)
        {
            return BuildMissingConfidenceReceipt(true, lowConfidenceThreshold);
        }

        int tokenCount = 0;
        int lowConfidenceCount = 0;
        double sum = 0;
        double min = double.PositiveInfinity;
        foreach (JsonElement item in logprobs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("logprob", out JsonElement logprob) ||
                !logprob.TryGetDouble(out double value) ||
                double.IsNaN(value) ||
                double.IsInfinity(value))
            {
                continue;
            }

            tokenCount++;
            sum += value;
            min = Math.Min(min, value);
            if (value < lowConfidenceThreshold)
            {
                lowConfidenceCount++;
            }
        }

        if (tokenCount == 0)
        {
            return BuildMissingConfidenceReceipt(true, lowConfidenceThreshold);
        }

        return new AudioTranscriptionConfidenceReceipt
        {
            LogprobsRequested = true,
            LogprobsReturned = true,
            Status = lowConfidenceCount > 0 ? "review" : "ready",
            TokenCount = tokenCount,
            AverageLogprob = Math.Round(sum / tokenCount, 4, MidpointRounding.AwayFromZero),
            MinLogprob = Math.Round(min, 4, MidpointRounding.AwayFromZero),
            LowConfidenceTokenCount = lowConfidenceCount,
            LowConfidenceThreshold = lowConfidenceThreshold,
        };
    }

    private static AudioTranscriptionTimingReceipt ParseTimingReceipt(
        JsonElement root,
        string responseFormat,
        IReadOnlyList<string> requestedGranularities,
        int maxTurnDurationMs)
    {
        bool verboseRequested = string.Equals(
            responseFormat,
            AsrResponseFormats.VerboseJson,
            StringComparison.OrdinalIgnoreCase);
        if (!verboseRequested)
        {
            return BuildMissingTimingReceipt(responseFormat, requestedGranularities, maxTurnDurationMs);
        }

        bool segmentRequested = HasRequestedGranularity(requestedGranularities, AsrTimestampGranularities.Segment);
        bool wordRequested = HasRequestedGranularity(requestedGranularities, AsrTimestampGranularities.Word);

        if (root.ValueKind != JsonValueKind.Object)
        {
            return BuildMissingTimingReceipt(responseFormat, requestedGranularities, maxTurnDurationMs);
        }

        string language = TryGetTrimmedString(root, "language");
        double? durationSeconds = TryGetFiniteNonNegativeDouble(root, "duration");
        SegmentTimingSummary segmentSummary = SummarizeSegments(root, durationSeconds);
        WordTimingSummary wordSummary = SummarizeWords(root);

        bool verboseReturned =
            durationSeconds is not null ||
            !string.IsNullOrWhiteSpace(language) ||
            segmentSummary.PropertyReturned ||
            wordSummary.PropertyReturned;
        if (!verboseReturned)
        {
            return BuildMissingTimingReceipt(responseFormat, requestedGranularities, maxTurnDurationMs);
        }

        List<string> flags = [];
        if (durationSeconds is null)
        {
            flags.Add("duration_missing");
        }
        else if (durationSeconds.Value * 1_000d > Math.Max(1, maxTurnDurationMs))
        {
            flags.Add("duration_over_turn_budget");
        }

        if (segmentRequested && !segmentSummary.TimestampReturned)
        {
            flags.Add("segment_timestamps_missing");
        }

        if (wordRequested && !wordSummary.TimestampReturned)
        {
            flags.Add("word_timestamps_missing");
        }

        if (segmentSummary.PropertyReturned && segmentSummary.Count == 0)
        {
            flags.Add("no_segments_returned");
        }

        if (segmentSummary.InvalidTiming)
        {
            flags.Add("segment_timing_invalid");
        }

        if (segmentSummary.EndExceedsDuration)
        {
            flags.Add("segment_end_exceeds_duration");
        }

        return new AudioTranscriptionTimingReceipt
        {
            VerboseJsonRequested = true,
            VerboseJsonReturned = true,
            SegmentTimestampsRequested = segmentRequested,
            WordTimestampsRequested = wordRequested,
            SegmentTimestampsReturned = segmentSummary.TimestampReturned,
            WordTimestampsReturned = wordSummary.TimestampReturned,
            Status = flags.Count == 0 ? "ready" : "review",
            Language = language,
            DurationSeconds = durationSeconds is { } duration ? RoundSeconds(duration) : null,
            SegmentCount = segmentSummary.Count,
            WordCount = wordSummary.Count,
            FirstSegmentStartSeconds = segmentSummary.FirstStartSeconds is { } first ? RoundSeconds(first) : null,
            LastSegmentEndSeconds = segmentSummary.LastEndSeconds is { } last ? RoundSeconds(last) : null,
            CoveredSegmentSeconds = segmentSummary.CoveredSeconds is { } covered ? RoundSeconds(covered) : null,
            SegmentCoverageRatio = segmentSummary.CoverageRatio is { } ratio ? Math.Round(ratio, 4, MidpointRounding.AwayFromZero) : null,
            MaxTurnDurationMs = Math.Max(1, maxTurnDurationMs),
            Flags = flags.ToArray(),
        };
    }

    private static AudioTranscriptionQualityReceipt ParseQualityReceipt(
        JsonElement root,
        string responseFormat)
    {
        bool verboseRequested = string.Equals(
            responseFormat,
            AsrResponseFormats.VerboseJson,
            StringComparison.OrdinalIgnoreCase);
        if (!verboseRequested || root.ValueKind != JsonValueKind.Object)
        {
            return BuildMissingQualityReceipt(responseFormat);
        }

        if (!root.TryGetProperty("segments", out JsonElement segments) ||
            segments.ValueKind != JsonValueKind.Array)
        {
            return BuildMissingQualityReceipt(responseFormat);
        }

        int segmentCount = 0;
        int qualitySegmentCount = 0;
        int averageLogprobCount = 0;
        int lowAverageLogprobCount = 0;
        int compressionRatioCount = 0;
        int highCompressionRatioCount = 0;
        int noSpeechProbabilityCount = 0;
        int silentSegmentCandidateCount = 0;
        int temperatureCount = 0;
        double averageLogprobSum = 0;
        double minAverageLogprob = double.PositiveInfinity;
        double maxCompressionRatio = double.NegativeInfinity;
        double maxNoSpeechProbability = double.NegativeInfinity;
        double maxTemperature = double.NegativeInfinity;

        foreach (JsonElement segment in segments.EnumerateArray())
        {
            segmentCount++;
            if (segment.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            bool hasQuality = false;
            double? averageLogprob = null;
            if (TryGetFiniteDouble(segment, "avg_logprob", out double averageLogprobValue))
            {
                hasQuality = true;
                averageLogprob = averageLogprobValue;
                averageLogprobCount++;
                averageLogprobSum += averageLogprobValue;
                minAverageLogprob = Math.Min(minAverageLogprob, averageLogprobValue);
                if (averageLogprobValue < SegmentLowAverageLogprobThreshold)
                {
                    lowAverageLogprobCount++;
                }
            }

            if (TryGetFiniteDouble(segment, "compression_ratio", out double compressionRatio))
            {
                hasQuality = true;
                compressionRatioCount++;
                maxCompressionRatio = Math.Max(maxCompressionRatio, compressionRatio);
                if (compressionRatio > SegmentHighCompressionRatioThreshold)
                {
                    highCompressionRatioCount++;
                }
            }

            if (TryGetFiniteDouble(segment, "no_speech_prob", out double noSpeechProbability))
            {
                hasQuality = true;
                noSpeechProbabilityCount++;
                maxNoSpeechProbability = Math.Max(maxNoSpeechProbability, noSpeechProbability);
                if (noSpeechProbability > SegmentSilentNoSpeechProbabilityThreshold &&
                    averageLogprob is { } logprob &&
                    logprob < SegmentLowAverageLogprobThreshold)
                {
                    silentSegmentCandidateCount++;
                }
            }

            if (TryGetFiniteDouble(segment, "temperature", out double temperature))
            {
                hasQuality = true;
                temperatureCount++;
                maxTemperature = Math.Max(maxTemperature, temperature);
            }

            if (hasQuality)
            {
                qualitySegmentCount++;
            }
        }

        if (qualitySegmentCount == 0)
        {
            return new AudioTranscriptionQualityReceipt
            {
                VerboseJsonRequested = true,
                QualityMetadataReturned = false,
                Status = "not_returned",
                SegmentCount = segmentCount,
                LowAverageLogprobThreshold = SegmentLowAverageLogprobThreshold,
                HighCompressionRatioThreshold = SegmentHighCompressionRatioThreshold,
                Flags = segmentCount == 0
                    ? ["no_segments_returned", "segment_quality_missing"]
                    : ["segment_quality_missing"],
            };
        }

        List<string> flags = [];
        if (lowAverageLogprobCount > 0)
        {
            flags.Add("avg_logprob_below_minus_one");
        }

        if (highCompressionRatioCount > 0)
        {
            flags.Add("compression_ratio_above_2_4");
        }

        if (silentSegmentCandidateCount > 0)
        {
            flags.Add("silent_segment_candidate");
        }

        if (qualitySegmentCount < segmentCount)
        {
            flags.Add("segment_quality_partial");
        }

        return new AudioTranscriptionQualityReceipt
        {
            VerboseJsonRequested = true,
            QualityMetadataReturned = true,
            Status = flags.Count == 0 ? "ready" : "review",
            SegmentCount = segmentCount,
            QualitySegmentCount = qualitySegmentCount,
            AverageSegmentLogprob = averageLogprobCount > 0
                ? Math.Round(averageLogprobSum / averageLogprobCount, 4, MidpointRounding.AwayFromZero)
                : null,
            MinSegmentLogprob = averageLogprobCount > 0
                ? Math.Round(minAverageLogprob, 4, MidpointRounding.AwayFromZero)
                : null,
            LowAverageLogprobSegmentCount = lowAverageLogprobCount,
            LowAverageLogprobThreshold = SegmentLowAverageLogprobThreshold,
            MaxCompressionRatio = compressionRatioCount > 0
                ? Math.Round(maxCompressionRatio, 4, MidpointRounding.AwayFromZero)
                : null,
            HighCompressionRatioSegmentCount = highCompressionRatioCount,
            HighCompressionRatioThreshold = SegmentHighCompressionRatioThreshold,
            MaxNoSpeechProbability = noSpeechProbabilityCount > 0
                ? Math.Round(maxNoSpeechProbability, 4, MidpointRounding.AwayFromZero)
                : null,
            NoSpeechProbabilitySegmentCount = noSpeechProbabilityCount,
            SilentSegmentCandidateCount = silentSegmentCandidateCount,
            TemperatureSegmentCount = temperatureCount,
            MaxTemperature = temperatureCount > 0
                ? Math.Round(maxTemperature, 4, MidpointRounding.AwayFromZero)
                : null,
            Flags = flags.ToArray(),
        };
    }

    private static AudioTranscriptionConfidenceReceipt BuildMissingConfidenceReceipt(
        bool requested,
        float lowConfidenceThreshold) =>
        new()
        {
            LogprobsRequested = requested,
            LogprobsReturned = false,
            Status = requested ? "not_returned" : "not_requested",
            LowConfidenceThreshold = lowConfidenceThreshold,
        };

    private static AudioTranscriptionTimingReceipt BuildMissingTimingReceipt(
        string responseFormat,
        IReadOnlyList<string> requestedGranularities,
        int maxTurnDurationMs)
    {
        bool verboseRequested = string.Equals(
            responseFormat,
            AsrResponseFormats.VerboseJson,
            StringComparison.OrdinalIgnoreCase);

        return new AudioTranscriptionTimingReceipt
        {
            VerboseJsonRequested = verboseRequested,
            VerboseJsonReturned = false,
            SegmentTimestampsRequested =
                HasRequestedGranularity(requestedGranularities, AsrTimestampGranularities.Segment),
            WordTimestampsRequested =
                HasRequestedGranularity(requestedGranularities, AsrTimestampGranularities.Word),
            Status = verboseRequested ? "not_returned" : "not_requested",
            MaxTurnDurationMs = Math.Max(1, maxTurnDurationMs),
            Flags = verboseRequested ? ["verbose_json_metadata_missing"] : ["verbose_json_not_requested"],
        };
    }

    private static AudioTranscriptionQualityReceipt BuildMissingQualityReceipt(string responseFormat)
    {
        bool verboseRequested = string.Equals(
            responseFormat,
            AsrResponseFormats.VerboseJson,
            StringComparison.OrdinalIgnoreCase);

        return new AudioTranscriptionQualityReceipt
        {
            VerboseJsonRequested = verboseRequested,
            QualityMetadataReturned = false,
            Status = verboseRequested ? "not_returned" : "not_requested",
            LowAverageLogprobThreshold = SegmentLowAverageLogprobThreshold,
            HighCompressionRatioThreshold = SegmentHighCompressionRatioThreshold,
            Flags = verboseRequested ? ["segment_quality_missing"] : ["verbose_json_not_requested"],
        };
    }

    private static SegmentTimingSummary SummarizeSegments(JsonElement root, double? durationSeconds)
    {
        if (!root.TryGetProperty("segments", out JsonElement segments) ||
            segments.ValueKind != JsonValueKind.Array)
        {
            return SegmentTimingSummary.Missing;
        }

        int count = 0;
        bool invalidTiming = false;
        bool endExceedsDuration = false;
        double? firstStart = null;
        double? lastEnd = null;
        double coveredSeconds = 0;
        foreach (JsonElement segment in segments.EnumerateArray())
        {
            count++;
            if (segment.ValueKind != JsonValueKind.Object ||
                !TryGetFiniteDouble(segment, "start", out double start) ||
                !TryGetFiniteDouble(segment, "end", out double end))
            {
                continue;
            }

            if (start < 0 ||
                end < 0 ||
                end < start)
            {
                invalidTiming = true;
                continue;
            }

            firstStart = firstStart is { } currentFirst
                ? Math.Min(currentFirst, start)
                : start;
            lastEnd = lastEnd is { } currentLast
                ? Math.Max(currentLast, end)
                : end;
            coveredSeconds += end - start;
            if (durationSeconds is { } duration &&
                end > duration + 0.5d)
            {
                endExceedsDuration = true;
            }
        }

        double? coverageRatio = durationSeconds is { } positiveDuration && positiveDuration > 0
            ? Math.Min(1d, Math.Max(0d, coveredSeconds / positiveDuration))
            : null;

        return new SegmentTimingSummary(
            PropertyReturned: true,
            Count: count,
            TimestampReturned: firstStart is not null && lastEnd is not null,
            InvalidTiming: invalidTiming,
            EndExceedsDuration: endExceedsDuration,
            FirstStartSeconds: firstStart,
            LastEndSeconds: lastEnd,
            CoveredSeconds: firstStart is not null ? coveredSeconds : null,
            CoverageRatio: coverageRatio);
    }

    private static WordTimingSummary SummarizeWords(JsonElement root)
    {
        if (!root.TryGetProperty("words", out JsonElement words) ||
            words.ValueKind != JsonValueKind.Array)
        {
            return WordTimingSummary.Missing;
        }

        int count = 0;
        bool timestampReturned = false;
        foreach (JsonElement word in words.EnumerateArray())
        {
            count++;
            if (word.ValueKind == JsonValueKind.Object &&
                TryGetFiniteDouble(word, "start", out double start) &&
                TryGetFiniteDouble(word, "end", out double end) &&
                start >= 0 &&
                end >= start)
            {
                timestampReturned = true;
            }
        }

        return new WordTimingSummary(
            PropertyReturned: true,
            Count: count,
            TimestampReturned: timestampReturned);
    }

    private static bool HasRequestedGranularity(
        IReadOnlyList<string> requestedGranularities,
        string granularity) =>
        requestedGranularities.Any(value => string.Equals(value, granularity, StringComparison.OrdinalIgnoreCase));

    private static string TryGetTrimmedString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static double? TryGetFiniteNonNegativeDouble(JsonElement root, string propertyName) =>
        TryGetFiniteDouble(root, propertyName, out double value) && value >= 0
            ? value
            : null;

    private static bool TryGetFiniteDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out JsonElement element) &&
               element.TryGetDouble(out value) &&
               !double.IsNaN(value) &&
               !double.IsInfinity(value);
    }

    private static double RoundSeconds(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero);

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct SegmentTimingSummary(
        bool PropertyReturned,
        int Count,
        bool TimestampReturned,
        bool InvalidTiming,
        bool EndExceedsDuration,
        double? FirstStartSeconds,
        double? LastEndSeconds,
        double? CoveredSeconds,
        double? CoverageRatio)
    {
        public static SegmentTimingSummary Missing { get; } = new(
            PropertyReturned: false,
            Count: 0,
            TimestampReturned: false,
            InvalidTiming: false,
            EndExceedsDuration: false,
            FirstStartSeconds: null,
            LastEndSeconds: null,
            CoveredSeconds: null,
            CoverageRatio: null);
    }

    private readonly record struct TranscriptionParseResult(
        string Transcript,
        AudioTranscriptionConfidenceReceipt Confidence,
        AudioTranscriptionTimingReceipt Timing,
        AudioTranscriptionQualityReceipt Quality);

    private readonly record struct WordTimingSummary(
        bool PropertyReturned,
        int Count,
        bool TimestampReturned)
    {
        public static WordTimingSummary Missing { get; } = new(
            PropertyReturned: false,
            Count: 0,
            TimestampReturned: false);
    }
}

public sealed class AudioTranscriptionRequest
{
    public string AudioBase64 { get; init; } = string.Empty;

    public string? AudioMimeType { get; init; } = "audio/wav";

    public string? Language { get; init; }

    public string? Prompt { get; init; }
}

public sealed class AudioTranscriptionResult
{
    private AudioTranscriptionResult(
        bool isConfigured,
        bool success,
        string transcript,
        string model,
        int audioBytes,
        long latencyMs,
        string statusMessage,
        AudioTranscriptionConfidenceReceipt? confidence,
        AudioTranscriptionTimingReceipt? timing,
        AudioTranscriptionQualityReceipt? quality,
        string? upstreamRequestId,
        double? upstreamProcessingMs,
        double? upstreamQueueMs,
        double? upstreamTimeToFirstTokenMs,
        double? upstreamPrefillMs,
        double? upstreamDecodeMs)
    {
        IsConfigured = isConfigured;
        Success = success;
        Transcript = transcript;
        Model = model;
        AudioBytes = audioBytes;
        LatencyMs = latencyMs;
        StatusMessage = statusMessage;
        Confidence = confidence ?? new AudioTranscriptionConfidenceReceipt();
        Timing = timing ?? new AudioTranscriptionTimingReceipt();
        Quality = quality ?? new AudioTranscriptionQualityReceipt();
        UpstreamRequestId = HttpResponseReceiptExtractor.NormalizeIdentifier(upstreamRequestId);
        UpstreamProcessingMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamProcessingMs);
        UpstreamQueueMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamQueueMs);
        UpstreamTimeToFirstTokenMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamTimeToFirstTokenMs);
        UpstreamPrefillMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamPrefillMs);
        UpstreamDecodeMs = HttpResponseReceiptExtractor.NormalizeProcessingMs(upstreamDecodeMs);
    }

    public bool IsConfigured { get; }

    public bool Success { get; }

    public string Transcript { get; }

    public string Model { get; }

    public int AudioBytes { get; }

    public long LatencyMs { get; }

    public string StatusMessage { get; }

    public AudioTranscriptionConfidenceReceipt Confidence { get; }

    public AudioTranscriptionTimingReceipt Timing { get; }

    public AudioTranscriptionQualityReceipt Quality { get; }

    public string UpstreamRequestId { get; }

    public double? UpstreamProcessingMs { get; }

    public double? UpstreamQueueMs { get; }

    public double? UpstreamTimeToFirstTokenMs { get; }

    public double? UpstreamPrefillMs { get; }

    public double? UpstreamDecodeMs { get; }

    public static AudioTranscriptionResult Disabled(string statusMessage) =>
        new(false, false, string.Empty, string.Empty, 0, 0, statusMessage, null, null, null, null, null, null, null, null, null);

    public static AudioTranscriptionResult Failed(
        string statusMessage,
        string model = "",
        int audioBytes = 0,
        long latencyMs = 0,
        AudioTranscriptionConfidenceReceipt? confidence = null,
        AudioTranscriptionTimingReceipt? timing = null,
        AudioTranscriptionQualityReceipt? quality = null,
        string? upstreamRequestId = null,
        double? upstreamProcessingMs = null,
        double? upstreamQueueMs = null,
        double? upstreamTimeToFirstTokenMs = null,
        double? upstreamPrefillMs = null,
        double? upstreamDecodeMs = null) =>
        new(
            true,
            false,
            string.Empty,
            model,
            audioBytes,
            latencyMs,
            statusMessage,
            confidence,
            timing,
            quality,
            upstreamRequestId,
            upstreamProcessingMs,
            upstreamQueueMs,
            upstreamTimeToFirstTokenMs,
            upstreamPrefillMs,
            upstreamDecodeMs);

    public static AudioTranscriptionResult Succeeded(
        string transcript,
        string model,
        int audioBytes,
        long latencyMs,
        AudioTranscriptionConfidenceReceipt? confidence = null,
        AudioTranscriptionTimingReceipt? timing = null,
        AudioTranscriptionQualityReceipt? quality = null,
        string? upstreamRequestId = null,
        double? upstreamProcessingMs = null,
        double? upstreamQueueMs = null,
        double? upstreamTimeToFirstTokenMs = null,
        double? upstreamPrefillMs = null,
        double? upstreamDecodeMs = null) =>
        new(
            true,
            true,
            transcript,
            model,
            audioBytes,
            latencyMs,
            "ASR transcription completed.",
            confidence,
            timing,
            quality,
            upstreamRequestId,
            upstreamProcessingMs,
            upstreamQueueMs,
            upstreamTimeToFirstTokenMs,
            upstreamPrefillMs,
            upstreamDecodeMs);
}
