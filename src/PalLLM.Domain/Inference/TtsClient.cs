using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

public interface ITtsClient
{
    Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken);
}

public sealed class DisabledTtsClient : ITtsClient
{
    public Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(TtsResult.Disabled("TTS is disabled. Configure PalLLM:Tts to enable synthesis."));
}

/// <summary>
/// Minimal HTTP TTS adapter. POSTs <c>{ "text", "voice" }</c> to the configured
/// endpoint and treats the raw response body as audio. Works against any
/// server that follows that request/response shape. Operators with a different
/// contract can replace this class in DI.
/// </summary>
public sealed class HttpTtsClient : ITtsClient
{
    private const string AudioResponseLabel = "TTS audio";

    private readonly HttpClient _httpClient;
    private readonly PalLlmOptions _options;

    public HttpTtsClient(HttpClient httpClient, PalLlmOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TtsOptions tts = _options.Tts;

        if (!tts.Enabled || string.IsNullOrWhiteSpace(tts.BaseUrl))
        {
            return TtsResult.Disabled("TTS is disabled. Configure PalLLM:Tts to enable synthesis.");
        }

        if (string.IsNullOrWhiteSpace(request.Text))
        {
            return TtsResult.Failed("TTS request had no text.");
        }

        if (request.Text.Length > tts.MaxCharacters)
        {
            return TtsResult.Failed(
                $"TTS text exceeds the configured cap of {tts.MaxCharacters} characters.");
        }

        string voice = string.IsNullOrWhiteSpace(request.Voice) ? tts.DefaultVoice : request.Voice!;
        int maxResponseBytes = Math.Max(1_024, tts.MaxResponseBytes);
        TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, tts.TimeoutSeconds));
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(timeout);
        CancellationToken effectiveCancellationToken = requestTimeout.Token;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, tts.BaseUrl)
        {
            Content = CreateRequestContent(tts, request.Text, voice),
        };
        if (!string.IsNullOrWhiteSpace(tts.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tts.ApiKey);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await _httpClient.SendAsync(
                    httpRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _ = await ReadStatusBodyAsync(
                        response.Content,
                        maxResponseBytes,
                        effectiveCancellationToken)
                    .ConfigureAwait(false);
                return TtsResult.Failed(
                    TransportFailureStatusBuilder.HttpStatus("TTS", (int)response.StatusCode));
            }

            byte[] audio = await HttpContentReadLimiter.ReadBytesAsync(
                    response.Content,
                    maxResponseBytes,
                    AudioResponseLabel,
                    effectiveCancellationToken)
                .ConfigureAwait(false);
            string mime = ResolveResponseMimeType(tts, response.Content.Headers.ContentType?.MediaType);
            return TtsResult.Succeeded(audio, mime, voice);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return TtsResult.Failed(
                HttpContentReadLimiter.BuildExceededLimitMessage(AudioResponseLabel, maxResponseBytes));
        }
        catch (OperationCanceledException)
        {
            return TtsResult.Failed(TransportFailureStatusBuilder.Timeout("TTS"));
        }
        catch (HttpRequestException)
        {
            return TtsResult.Failed(TransportFailureStatusBuilder.Unreachable("TTS"));
        }
        finally
        {
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

    private static JsonContent CreateRequestContent(TtsOptions tts, string text, string voice)
    {
        if (TtsRequestFormats.UsesOpenAiSpeech(tts.RequestFormat))
        {
            return JsonContent.Create(
                new OpenAiSpeechTtsHttpRequestBody
                {
                    Model = NormalizeOptional(tts.Model),
                    Input = text,
                    Voice = voice,
                    ResponseFormat = TtsResponseFormats.Normalize(tts.ResponseFormat),
                },
                PalLlmDomainJsonSerializerContext.Default.OpenAiSpeechTtsHttpRequestBody);
        }

        return JsonContent.Create(
            new TtsHttpRequestBody { Text = text, Voice = voice },
            PalLlmDomainJsonSerializerContext.Default.TtsHttpRequestBody);
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ResolveResponseMimeType(TtsOptions tts, string? responseMimeType)
    {
        string? normalized = NormalizeOptional(responseMimeType);
        if (normalized is not null &&
            (!TtsRequestFormats.UsesOpenAiSpeech(tts.RequestFormat) ||
             !IsGenericBinaryMime(normalized)))
        {
            return normalized;
        }

        return TtsRequestFormats.UsesOpenAiSpeech(tts.RequestFormat)
            ? TtsResponseFormats.ToMimeType(tts.ResponseFormat)
            : normalized ?? "audio/wav";
    }

    private static bool IsGenericBinaryMime(string mimeType) =>
        string.Equals(mimeType, "application/octet-stream", StringComparison.OrdinalIgnoreCase);
}

public sealed class TtsRequest
{
    public string Text { get; init; } = string.Empty;

    public string? Voice { get; init; }
}

internal sealed class TtsHttpRequestBody
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("voice")]
    public string Voice { get; init; } = string.Empty;
}

internal sealed class OpenAiSpeechTtsHttpRequestBody
{
    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; init; }

    [JsonPropertyName("input")]
    public string Input { get; init; } = string.Empty;

    [JsonPropertyName("voice")]
    public string Voice { get; init; } = string.Empty;

    [JsonPropertyName("response_format")]
    public string ResponseFormat { get; init; } = TtsResponseFormats.Wav;
}

public sealed class TtsResult
{
    private TtsResult(bool isConfigured, bool success, byte[]? audio, string mimeType, string voice, string statusMessage)
    {
        IsConfigured = isConfigured;
        Success = success;
        Audio = audio;
        MimeType = mimeType;
        Voice = voice;
        StatusMessage = statusMessage;
    }

    public bool IsConfigured { get; }

    public bool Success { get; }

    public byte[]? Audio { get; }

    public string MimeType { get; }

    public string Voice { get; }

    public string StatusMessage { get; }

    public static TtsResult Disabled(string statusMessage) =>
        new(isConfigured: false, success: false, audio: null, string.Empty, string.Empty, statusMessage);

    public static TtsResult Failed(string statusMessage) =>
        new(isConfigured: true, success: false, audio: null, string.Empty, string.Empty, statusMessage);

    public static TtsResult Succeeded(byte[] audio, string mimeType, string voice) =>
        new(isConfigured: true, success: true, audio, mimeType, voice, "TTS synthesis completed.");
}
