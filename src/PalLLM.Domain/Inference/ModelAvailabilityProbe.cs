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
/// loaded or available models. Tries OpenAI-compatible <c>/v1/models</c>
/// first (covers llama.cpp/llama-server — PalLLM's bundled default — plus
/// vLLM, SGLang, OpenVINO Model Server when BaseUrl ends in <c>/v3/</c>,
/// LM Studio, and OpenAI itself), then checks Foundry Local's
/// <c>/openai/models</c> cached-model catalog. Any network failure returns
/// an empty set; the orchestrator treats that as "no tier available yet"
/// and keeps the current active tier.
///
/// Pass 346: the Ollama-native <c>/api/tags</c> fallback was removed along
/// with the rest of the Ollama back-compat path. Every supported runtime
/// now ships an OpenAI-compatible <c>/v1/models</c> endpoint (llama-server
/// since b3500, vLLM, SGLang, LM Studio, OpenVINO Model Server).
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
    /// Yields candidate (url, parser-id) pairs in probe order. OpenAI-compat
    /// stays first because it is the universal model-list shape — every
    /// supported runtime (llama-server, vLLM, SGLang, LM Studio, OpenVINO,
    /// OpenAI) exposes <c>/v1/models</c>. Foundry Local comes second because
    /// its documented cached-model endpoint is rooted at /openai/models and
    /// must be resolved off the server root in case BaseUrl carries a
    /// trailing /v1/.
    /// </summary>
    private static IEnumerable<string[]> CandidateProbes(string baseUrl)
    {
        string normalized = baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";
        Uri root = new(normalized);

        // /v1/models - OpenAI-compat "list models" endpoint.
        yield return [new Uri(root, "models").ToString(), "openai"];

        // /openai/models - Foundry Local cached-model endpoint. Resolve this
        // off the server root because baseUrl may include a trailing /v1/.
        if (Uri.TryCreate(root, "/openai/models", out Uri? foundryModelsUrl))
        {
            yield return [foundryModelsUrl.ToString(), "foundry"];
        }

        // Pass 346: /api/tags Ollama-native fallback removed. The bundled
        // llama-server runtime exposes /v1/models, so no operator running
        // a supported PalLLM stack should reach a probe miss here.
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
                "foundry" => ParseFoundryModels(document),
                // Pass 346: "ollama" dispatch branch removed alongside
                // ParseOllamaTags. /api/tags is no longer probed.
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

    private static HashSet<string> ParseFoundryModels(JsonDocument document)
    {
        // Foundry Local shape: [ "Phi-4-mini-instruct-generic-cpu", ... ]
        HashSet<string> models = new(StringComparer.Ordinal);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return models;
        }

        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                string? name = entry.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    models.Add(name);
                }
            }
        }

        return models;
    }

    // Pass 346: ParseOllamaTags method removed alongside the rest of the
    // Ollama back-compat path. Every supported runtime exposes
    // /v1/models so ParseOpenAiModels covers them all; Foundry Local's
    // bare-array shape is the only remaining outlier.
}
