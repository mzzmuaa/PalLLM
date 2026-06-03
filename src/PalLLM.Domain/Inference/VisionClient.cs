using System.Diagnostics;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

public interface IVisionClient
{
    Task<VisionResult> DescribeAsync(VisionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// HTTP multimodal client that talks to any server implementing the JSON
/// chat-completions schema with <c>image_url</c> content parts. Sends one image
/// plus optional text and returns the raw model string. Latency and quality
/// depend on the endpoint the operator configures via <c>VisionOptions</c>.
/// </summary>
public sealed class HttpVisionClient : IVisionClient
{
    private const string ChatCompletionsPath = "chat/completions";
    private const string ResponseLabel = "Vision response";

    private readonly HttpClient _httpClient;
    private readonly PalLlmOptions _options;

    public HttpVisionClient(HttpClient httpClient, PalLlmOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<VisionResult> DescribeAsync(VisionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        VisionOptions vision = _options.Vision;
        string providerName = GenAiTelemetry.GetProviderName(vision.BaseUrl);
        if (!vision.Enabled || string.IsNullOrWhiteSpace(vision.BaseUrl) || string.IsNullOrWhiteSpace(vision.Model))
        {
            return VisionResult.Disabled(
                "Vision is disabled. Set PalLLM:Vision:Enabled=true and configure a multimodal endpoint.",
                providerName,
                vision.Model);
        }

        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            return VisionResult.Failed(
                "Vision request had no image payload.",
                providerName,
                vision.Model,
                errorType: "missing_image");
        }

        // Inspect the media payload before building a data URL so malformed or
        // oversized screenshots never reach the network layer or model server.
        Base64PayloadInspection inspection = Base64PayloadInspector.Inspect(
            request.ImageBase64,
            vision.MaxImageBytes);
        if (!inspection.Accepted)
        {
            return VisionResult.Failed(
                Base64PayloadInspector.BuildImageFailureMessage(inspection, vision.MaxImageBytes),
                providerName,
                vision.Model,
                errorType: inspection.ErrorCode);
        }

        string mimeType = string.IsNullOrWhiteSpace(request.ImageMimeType) ? "image/png" : request.ImageMimeType;
        string dataUrl = $"data:{mimeType};base64,{request.ImageBase64}";
        string? mediaCacheId = vision.UseMediaCacheIds
            ? MediaCacheIdBuilder.Build("image", mimeType, request.ImageBase64)
            : null;
        VisionChatCompletionsRequestBody requestBody = BuildRequestBody(request, vision, dataUrl, mediaCacheId);
        GenAiOperationContext telemetryContext = GenAiTelemetry.CreateContext(
            GenAiTelemetry.OperationGenerateContent,
            vision.BaseUrl,
            vision.Model,
            request.ResponseFormat.HasValue ? GenAiTelemetry.OutputTypeJson : GenAiTelemetry.OutputTypeText,
            maxTokens: request.MaxTokens ?? vision.DefaultMaxTokens,
            temperature: request.Temperature ?? vision.Temperature);
        using Activity? activity = GenAiTelemetry.StartClientActivity(telemetryContext);
        long startedAt = Stopwatch.GetTimestamp();
        string? errorType = null;
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, vision.TimeoutSeconds));
        int maxResponseBytes = Math.Max(1_024, vision.MaxResponseBytes);
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        CancellationToken effectiveCancellationToken = requestTimeout.Token;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(vision.BaseUrl));
        if (!string.IsNullOrWhiteSpace(vision.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", vision.ApiKey);
        }

        httpRequest.Content = JsonContent.Create(
            requestBody,
            PalLlmDomainJsonSerializerContext.Default.VisionChatCompletionsRequestBody);

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
                _ = await ReadStatusBodyAsync(response.Content, maxResponseBytes, effectiveCancellationToken)
                    .ConfigureAwait(false);
                int statusCode = (int)response.StatusCode;
                errorType = statusCode.ToString();
                GenAiTelemetry.MarkError(activity, errorType);
                return VisionResult.Failed(
                    TransportFailureStatusBuilder.HttpStatus("Vision", statusCode),
                    providerName,
                    vision.Model,
                    latencyMs: GetElapsedMilliseconds(startedAt),
                    errorType: errorType,
                    upstreamRequestId: upstreamRequestId,
                    upstreamProcessingMs: upstreamProcessingMs,
                    upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                    upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                    upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
            }

            ChatCompletionsReadResult parsed = await ChatCompletionsResponseReader.ReadAsync(
                    response.Content,
                    maxResponseBytes,
                    ResponseLabel,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            if (parsed.Success)
            {
                telemetryContext = telemetryContext with
                {
                    ResponseModel = string.IsNullOrWhiteSpace(parsed.ResponseModel) ? null : parsed.ResponseModel,
                };
                GenAiTelemetry.ApplyResponse(activity, parsed);
                GenAiTelemetry.RecordTokenUsage(telemetryContext, parsed.Usage);
            }
            else
            {
                errorType = GenAiTelemetry.ErrorTypeInvalidResponse;
                GenAiTelemetry.MarkError(activity, errorType);
            }

            return parsed.Success
                ? VisionResult.Succeeded(
                    parsed.Content,
                    parsed.Usage,
                    providerName,
                    vision.Model,
                    parsed.ResponseModel,
                    GetElapsedMilliseconds(startedAt),
                    parsed.FinishReasons,
                    upstreamRequestId,
                    upstreamProcessingMs,
                    upstreamPhaseTimings.QueueMs,
                    upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPhaseTimings.PrefillMs,
                    upstreamPhaseTimings.DecodeMs)
                : VisionResult.Failed(
                    $"Vision endpoint {parsed.ErrorMessage}",
                    providerName,
                    vision.Model,
                    parsed.ResponseModel,
                    GetElapsedMilliseconds(startedAt),
                    errorType,
                    upstreamRequestId,
                    upstreamProcessingMs,
                    upstreamPhaseTimings.QueueMs,
                    upstreamPhaseTimings.TimeToFirstTokenMs,
                    upstreamPhaseTimings.PrefillMs,
                    upstreamPhaseTimings.DecodeMs);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errorType = GenAiTelemetry.ErrorTypeCancelled;
            GenAiTelemetry.MarkError(activity, errorType);
            throw;
        }
        catch (OperationCanceledException)
        {
            errorType = "timeout";
            GenAiTelemetry.MarkError(activity, errorType);
            return VisionResult.Failed(
                TransportFailureStatusBuilder.Timeout("Vision"),
                providerName,
                vision.Model,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
        }
        catch (HttpRequestException)
        {
            errorType = nameof(HttpRequestException);
            GenAiTelemetry.MarkError(activity, errorType);
            return VisionResult.Failed(
                TransportFailureStatusBuilder.Unreachable("Vision"),
                providerName,
                vision.Model,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType);
        }
        catch (InvalidDataException)
        {
            errorType = "response_too_large";
            GenAiTelemetry.MarkError(activity, errorType);
            return VisionResult.Failed(
                HttpContentReadLimiter.BuildExceededLimitMessage(ResponseLabel, maxResponseBytes),
                providerName,
                vision.Model,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
        }
        catch (JsonException)
        {
            // Some VLM servers return HTML 200s or streaming chunks under load; a
            // malformed body is a real upstream fault but not an exception worth
            // propagating to the chat handler.
            errorType = nameof(JsonException);
            GenAiTelemetry.MarkError(activity, errorType);
            return VisionResult.Failed(
                TransportFailureStatusBuilder.MalformedJson("Vision"),
                providerName,
                vision.Model,
                latencyMs: GetElapsedMilliseconds(startedAt),
                errorType: errorType,
                upstreamRequestId: upstreamRequestId,
                upstreamProcessingMs: upstreamProcessingMs,
                upstreamQueueMs: upstreamPhaseTimings.QueueMs,
                upstreamTimeToFirstTokenMs: upstreamPhaseTimings.TimeToFirstTokenMs,
                upstreamPrefillMs: upstreamPhaseTimings.PrefillMs,
                upstreamDecodeMs: upstreamPhaseTimings.DecodeMs);
        }
        finally
        {
            GenAiTelemetry.RecordOperationDuration(
                telemetryContext,
                Stopwatch.GetElapsedTime(startedAt),
                errorType);
            response?.Dispose();
        }
    }

    private static async Task<string> ReadStatusBodyAsync(
        HttpContent content,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        int maxErrorBytes = Math.Max(1_024, Math.Min(maxResponseBytes, 8 * 1_024));
        HttpContentReadLimiter.BoundedTextReadResult readResult = await HttpContentReadLimiter.ReadTextAsync(
                content,
                maxErrorBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return readResult.ExceededLimit
            ? $"[response body exceeded {maxErrorBytes} bytes]"
            : readResult.Text;
    }

    private static long GetElapsedMilliseconds(long startedAt) =>
        Math.Max(0, (long)Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, MidpointRounding.AwayFromZero));

    private static string BuildEndpoint(string baseUrl)
    {
        string normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        return new Uri(new Uri(normalized), ChatCompletionsPath).ToString();
    }

    private static VisionChatCompletionsRequestBody BuildRequestBody(
        VisionRequest request,
        VisionOptions vision,
        string dataUrl,
        string? mediaCacheId)
    {
        List<VisionChatMessage> messages = [];
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new VisionChatMessage
            {
                Role = "system",
                Content = JsonSerializer.SerializeToElement(
                    request.SystemPrompt,
                    PalLlmDomainJsonSerializerContext.Default.String),
            });
        }

        // Image content BEFORE text is the ordering most multimodal models prefer.
        // The schema accepts both object and string forms for image_url; the
        // object form is portable across the common HTTP vision server shapes.
        VisionContentPart[] userContent =
        {
            new()
            {
                Type = "image_url",
                ImageUrl = new VisionImageUrl { Url = dataUrl },
                Uuid = mediaCacheId,
            },
            new()
            {
                Type = "text",
                Text = string.IsNullOrWhiteSpace(request.UserPrompt)
                    ? "Describe this scene succinctly in one sentence."
                    : request.UserPrompt,
            },
        };
        messages.Add(new VisionChatMessage
        {
            Role = "user",
            Content = JsonSerializer.SerializeToElement(
                userContent,
                PalLlmDomainJsonSerializerContext.Default.VisionContentPartArray),
        });

        return new VisionChatCompletionsRequestBody
        {
            Model = vision.Model,
            Temperature = request.Temperature ?? vision.Temperature,
            MaxTokens = request.MaxTokens ?? vision.DefaultMaxTokens,
            Messages = [.. messages],
            ResponseFormat = request.ResponseFormat,
            MmProcessorKwargs = vision.MultimodalProcessor.HasAny ? vision.MultimodalProcessor : null,
        };

        // Structured outputs are an opt-in per-request hint. OpenAI-compatible
        // endpoints that understand the json_schema wrapper (including
        // llama.cpp) constrain the model to the schema; endpoints that don't
        // silently ignore the unknown field.
    }
}

