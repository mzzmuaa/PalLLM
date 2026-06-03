using System.Text.Json;
using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

/// <summary>
/// Returns the set of model tags the inference endpoint currently reports as
/// available. Used by <see cref="ModelTierOrchestrator"/> to decide which
/// configured tier to route traffic to, picking the highest-priority tier
/// whose model is in this set.
/// </summary>
public interface IModelAvailabilityProbe
{
    Task<IReadOnlySet<string>> GetAvailableModelsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Never-fails probe used when tier orchestration is disabled. Returns an
/// empty set so the orchestrator always falls through to the static
/// <see cref="InferenceOptions.Model"/>. Avoids wiring an HttpClient when
/// there is no tier list to probe.
/// </summary>
public sealed class NullModelAvailabilityProbe : IModelAvailabilityProbe
{
    public Task<IReadOnlySet<string>> GetAvailableModelsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<string>>(new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>
/// HTTP probe that queries the configured inference endpoint for currently
/// loaded or available models via the OpenAI-compatible <c>/v1/models</c>
/// endpoint. llama.cpp/llama-server (PalLLM's only local engine, since b3500)
/// and the OpenAI-compatible cloud escape path both expose it. Any network
/// failure returns an empty set; the orchestrator treats that as "no tier
/// available yet" and keeps the current active tier.
///
/// Pass 436: the Foundry Local <c>/openai/models</c> candidate was removed with
/// the rest of the alt-engine purge; Pass 346 removed the Ollama-native
/// <c>/api/tags</c> fallback before that. <c>/v1/models</c> is now the sole probe.
/// </summary>
public sealed class HttpModelAvailabilityProbe : IModelAvailabilityProbe
{
    private readonly HttpClient _httpClient;
    private readonly PalLlmOptions _options;

    public HttpModelAvailabilityProbe(HttpClient httpClient, PalLlmOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<IReadOnlySet<string>> GetAvailableModelsAsync(CancellationToken cancellationToken)
    {
        string baseUrl = _options.Inference.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        int maxResponseBytes = Math.Max(1_024, _options.Inference.ModelCatalogMaxResponseBytes);
        HashSet<string> merged = new(StringComparer.Ordinal);

        foreach (string[] attempt in CandidateProbes(baseUrl))
        {
            IReadOnlySet<string>? found = await TryProbeAsync(
                    attempt[0],
                    attempt[1],
                    maxResponseBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            if (found is not null)
            {
                foreach (string model in found)
                {
                    merged.Add(model);
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Yields the single (url, parser-id) probe pair. <c>/v1/models</c> is the
    /// universal OpenAI-compatible model-list shape that llama-server (PalLLM's
    /// only local engine) and any OpenAI-compatible cloud endpoint expose.
    /// </summary>
    private static IEnumerable<string[]> CandidateProbes(string baseUrl)
    {
        string normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        Uri root = new(normalized);

        // /v1/models - the sole probe. llama.cpp llama-server (PalLLM's only
        // local engine) and the OpenAI-compatible cloud escape path both expose
        // it. Pass 436 removed the Foundry Local /openai/models candidate and
        // Pass 346 removed the Ollama /api/tags fallback.
        yield return [new Uri(root, "models").ToString(), "openai"];
    }

    private async Task<IReadOnlySet<string>?> TryProbeAsync(
        string url,
        string parser,
        int maxResponseBytes,
        CancellationToken cancellationToken)
    {
        using var requestTimeout = CreateRequestTimeout(cancellationToken);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(_options.Inference.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", _options.Inference.ApiKey);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using JsonDocument document = await HttpContentReadLimiter.ParseJsonDocumentAsync(
                    response.Content,
                    maxResponseBytes,
                    "Model catalog response",
                    requestTimeout.Token)
                .ConfigureAwait(false);

            return parser switch
            {
                "openai" => ParseOpenAiModels(document),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private CancellationTokenSource CreateRequestTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (_httpClient.Timeout is { } httpTimeout &&
            httpTimeout != Timeout.InfiniteTimeSpan &&
            httpTimeout > TimeSpan.Zero)
        {
            timeout.CancelAfter(httpTimeout);
        }

        return timeout;
    }

    private static HashSet<string> ParseOpenAiModels(JsonDocument document)
    {
        // OpenAI shape: { "data": [ { "id": "model-name", ... }, ... ] }
        HashSet<string> models = new(StringComparer.Ordinal);
        if (!document.RootElement.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (JsonElement entry in data.EnumerateArray())
        {
            if (entry.TryGetProperty("id", out JsonElement idElement) &&
                idElement.ValueKind == JsonValueKind.String)
            {
                string? id = idElement.GetString();
                if (!string.IsNullOrWhiteSpace(id))
                {
                    models.Add(id);
                }
            }
        }

        return models;
    }

    // Pass 436: ParseFoundryModels removed with the Foundry Local probe
    // candidate; Pass 346 removed ParseOllamaTags. Every supported endpoint
    // (llama-server + the OpenAI-compatible cloud escape) exposes /v1/models, so
    // ParseOpenAiModels covers them all.
}
