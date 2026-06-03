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
public sealed partial class HttpAudioTranscriptionClient : IAudioTranscriptionClient
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

        string? language = NormalizeOptional(request.Language) ?? NormalizeOptional(asr.Language);
        if (language is not null)
        {
            form.Add(new StringContent(language, Encoding.UTF8), "language");
        }

        string? prompt = NormalizeOptional(request.Prompt) ?? NormalizeOptional(asr.Prompt);
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

        if (asr.Seed is { } seed)
        {
            form.Add(new StringContent(seed.ToString(CultureInfo.InvariantCulture), Encoding.UTF8), "seed");
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
}
