// AudioTranscription wire contracts: request + result DTOs (with the
// Disabled/Failed/Succeeded factories). Split out of AudioTranscriptionClient.cs.
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

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
