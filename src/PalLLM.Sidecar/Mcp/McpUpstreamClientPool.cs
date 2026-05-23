using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PalLLM.Domain.Configuration;

namespace PalLLM.Sidecar.Mcp;

/// <summary>
/// Connects to every configured external (upstream) MCP server and caches
/// the discovered tools, resources, and prompts. Read-only by design —
/// this pool does not proxy <c>tools/call</c> or <c>resources/read</c>
/// requests to upstream servers. Operators explicitly opt into each
/// upstream URL in <c>PalLLM:McpClient:UpstreamServers[]</c>, and the
/// sidecar only ever asks each server "what can you do?", never "do
/// something for this user".
///
/// <para>The pool is a singleton. On refresh, it probes every enabled
/// server in parallel with a short per-probe timeout; failures are
/// recorded on the snapshot rather than thrown. Callers read the
/// current state via <see cref="GetSnapshots"/> without blocking.</para>
/// </summary>
public sealed class McpUpstreamClientPool
{
    public const string HttpClientName = "mcp-upstream-discovery";

    private readonly PalLlmOptions _options;
    private readonly ILogger<McpUpstreamClientPool> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILoggerFactory _loggerFactory;

    // Volatile snapshot dictionary — readers get a consistent view
    // without locks. Writers replace the reference atomically under
    // a semaphore that serialises RefreshAsync callers.
    private volatile IReadOnlyDictionary<string, UpstreamSnapshot> _snapshots =
        new Dictionary<string, UpstreamSnapshot>(StringComparer.Ordinal);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public McpUpstreamClientPool(
        PalLlmOptions options,
        ILogger<McpUpstreamClientPool> logger,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory)
    {
        _options = options;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _loggerFactory = loggerFactory;
    }

    /// <summary>Last-known snapshot of every configured upstream server.</summary>
    public IReadOnlyDictionary<string, UpstreamSnapshot> GetSnapshots() => _snapshots;

    /// <summary>
    /// Probes every enabled upstream server once and atomically replaces
    /// the cached snapshot map. Concurrent calls are serialised — the
    /// second caller waits for the first to finish.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<McpUpstreamServer> servers = _options.McpClient.UpstreamServers;
        if (servers.Count == 0)
        {
            _snapshots = new Dictionary<string, UpstreamSnapshot>(StringComparer.Ordinal);
            return;
        }

        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(1, _options.McpClient.DiscoveryTimeoutSeconds));
            ConcurrentDictionary<string, UpstreamSnapshot> next = new(StringComparer.Ordinal);

            IEnumerable<Task> probes = servers.Select(async server =>
            {
                if (!server.Enabled || string.IsNullOrWhiteSpace(server.Id) || string.IsNullOrWhiteSpace(server.Url))
                {
                    return;
                }

                UpstreamSnapshot snapshot = await ProbeOneAsync(server, timeout, cancellationToken)
                    .ConfigureAwait(false);
                next[server.Id] = snapshot;
            });

            await Task.WhenAll(probes).ConfigureAwait(false);
            _snapshots = next.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<UpstreamSnapshot> ProbeOneAsync(
        McpUpstreamServer server,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probeCts.CancelAfter(timeout);

        DateTimeOffset probedAt = DateTimeOffset.UtcNow;
        try
        {
            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = new Uri(server.Url),
                Name = server.Id,
                ConnectionTimeout = timeout,
            };
            if (!string.IsNullOrWhiteSpace(server.BearerToken))
            {
                transportOptions.AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Authorization"] = $"Bearer {server.BearerToken}",
                };
            }

            HttpClient httpClient = _httpClientFactory.CreateClient(HttpClientName);
            var transport = new HttpClientTransport(transportOptions, httpClient, _loggerFactory, ownsHttpClient: false);
            await using McpClient client = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: _loggerFactory,
                cancellationToken: probeCts.Token).ConfigureAwait(false);

            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: probeCts.Token).ConfigureAwait(false);
            IList<McpClientResource> resources = Array.Empty<McpClientResource>();
            try
            {
                resources = await client.ListResourcesAsync(cancellationToken: probeCts.Token).ConfigureAwait(false);
            }
            catch (McpException)
            {
                // Server doesn't support resources — not an error.
            }

            IList<McpClientPrompt> prompts = Array.Empty<McpClientPrompt>();
            try
            {
                prompts = await client.ListPromptsAsync(cancellationToken: probeCts.Token).ConfigureAwait(false);
            }
            catch (McpException)
            {
                // Server doesn't support prompts — not an error.
            }

            MetadataShapeSummary toolSummary = ShapeMetadataEntries(
                tools.Select(t => t.Name),
                _options.McpClient.MaxToolsPerServer,
                _options.McpClient.MaxMetadataEntryLength);
            MetadataShapeSummary resourceSummary = ShapeMetadataEntries(
                resources.Select(r => r.Uri),
                _options.McpClient.MaxResourcesPerServer,
                _options.McpClient.MaxMetadataEntryLength);
            MetadataShapeSummary promptSummary = ShapeMetadataEntries(
                prompts.Select(p => p.Name),
                _options.McpClient.MaxPromptsPerServer,
                _options.McpClient.MaxMetadataEntryLength);
            LogMetadataShaping(server, toolSummary, resourceSummary, promptSummary);

            return new UpstreamSnapshot(
                Id: server.Id,
                Url: server.Url,
                Connected: true,
                ErrorCode: null,
                Error: null,
                ServerName: client.ServerInfo?.Name,
                ServerVersion: client.ServerInfo?.Version,
                ProtocolVersion: client.ServerCapabilities is null ? null : "connected",
                Tools: toolSummary.Entries,
                Resources: resourceSummary.Entries,
                Prompts: promptSummary.Entries,
                LastProbedAtUtc: probedAt);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("Upstream MCP server {Id} probe timed out.", server.Id);
            return Failed(server, probedAt, "timeout", "Upstream MCP discovery probe timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upstream MCP server {Id} probe failed.", server.Id);
            return Failed(server, probedAt, ClassifyProbeFailure(ex));
        }
    }

    private static (string ErrorCode, string Error) ClassifyProbeFailure(Exception ex)
    {
        bool sawMcpException = false;
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is UriFormatException || current is ArgumentException)
            {
                return ("invalid_endpoint", "Configured upstream URL is not a valid HTTP(S) MCP endpoint.");
            }

            if (current is HttpRequestException httpEx)
            {
                return httpEx.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => ("http_401", "Upstream MCP server rejected discovery with 401 Unauthorized."),
                    HttpStatusCode.Forbidden => ("http_403", "Upstream MCP server rejected discovery with 403 Forbidden."),
                    HttpStatusCode.NotFound => ("http_404", "Upstream MCP endpoint was not found (404)."),
                    HttpStatusCode.BadRequest => ("http_400", "Upstream MCP server rejected discovery as an invalid request (400)."),
                    HttpStatusCode.NotAcceptable => ("http_406", "Upstream MCP server rejected discovery content negotiation (406)."),
                    HttpStatusCode.MethodNotAllowed => ("http_405", "Upstream MCP endpoint rejected the discovery method (405)."),
                    HttpStatusCode status when (int)status >= 500 => ("http_5xx", "Upstream MCP server failed while handling discovery."),
                    HttpStatusCode status when (int)status >= 400 => ("http_4xx", "Upstream MCP server rejected discovery."),
                    _ => ("network", "Upstream MCP server could not be reached."),
                };
            }

            if (current is SocketException or IOException)
            {
                return ("network", "Upstream MCP server could not be reached.");
            }

            if (current is McpException)
            {
                sawMcpException = true;
            }
        }

        if (sawMcpException)
        {
            return ("protocol", "Upstream MCP server returned a protocol-level discovery error.");
        }

        return ("unexpected", "Upstream MCP discovery failed unexpectedly.");
    }

    private static UpstreamSnapshot Failed(
        McpUpstreamServer server,
        DateTimeOffset probedAt,
        string errorCode,
        string error) =>
        new(
            Id: server.Id,
            Url: server.Url,
            Connected: false,
            ErrorCode: errorCode,
            Error: error,
            ServerName: null,
            ServerVersion: null,
            ProtocolVersion: null,
            Tools: Array.Empty<string>(),
            Resources: Array.Empty<string>(),
            Prompts: Array.Empty<string>(),
            LastProbedAtUtc: probedAt);

    private static UpstreamSnapshot Failed(McpUpstreamServer server, DateTimeOffset probedAt, (string ErrorCode, string Error) failure) =>
        Failed(server, probedAt, failure.ErrorCode, failure.Error);

    private void LogMetadataShaping(
        McpUpstreamServer server,
        MetadataShapeSummary toolSummary,
        MetadataShapeSummary resourceSummary,
        MetadataShapeSummary promptSummary)
    {
        if (!toolSummary.Truncated && !resourceSummary.Truncated && !promptSummary.Truncated &&
            toolSummary.TrimmedEntries == 0 && resourceSummary.TrimmedEntries == 0 && promptSummary.TrimmedEntries == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Upstream MCP server {Id} discovery snapshot was bounded. Tools={ToolCount}/{ToolLimit} trimmed={ToolTrimmed}; Resources={ResourceCount}/{ResourceLimit} trimmed={ResourceTrimmed}; Prompts={PromptCount}/{PromptLimit} trimmed={PromptTrimmed}.",
            server.Id,
            toolSummary.Entries.Length,
            _options.McpClient.MaxToolsPerServer,
            toolSummary.TrimmedEntries,
            resourceSummary.Entries.Length,
            _options.McpClient.MaxResourcesPerServer,
            resourceSummary.TrimmedEntries,
            promptSummary.Entries.Length,
            _options.McpClient.MaxPromptsPerServer,
            promptSummary.TrimmedEntries);
    }

    private static MetadataShapeSummary ShapeMetadataEntries(IEnumerable<string?> values, int maxEntries, int maxEntryLength)
    {
        if (maxEntries <= 0 || maxEntryLength <= 0)
        {
            return MetadataShapeSummary.Empty;
        }

        List<string> entries = new(Math.Min(maxEntries, 16));
        HashSet<string> seen = new(StringComparer.Ordinal);
        bool truncated = false;
        int trimmedEntries = 0;

        foreach (string? value in values)
        {
            string? normalized = NormalizeMetadataEntry(value, maxEntryLength, out bool trimmed);
            if (normalized is null)
            {
                continue;
            }

            if (!seen.Add(normalized))
            {
                continue;
            }

            if (entries.Count >= maxEntries)
            {
                truncated = true;
                continue;
            }

            entries.Add(normalized);
            if (trimmed)
            {
                trimmedEntries++;
            }
        }

        entries.Sort(StringComparer.Ordinal);
        return new MetadataShapeSummary(entries.ToArray(), truncated, trimmedEntries);
    }

    private static string? NormalizeMetadataEntry(string? value, int maxEntryLength, out bool trimmed)
    {
        trimmed = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string candidate = value.Trim();
        StringBuilder? builder = null;
        bool previousWasWhitespace = false;

        for (int i = 0; i < candidate.Length; i++)
        {
            char ch = candidate[i];
            bool isWhitespace = char.IsWhiteSpace(ch);
            if (char.IsControl(ch))
            {
                ch = ' ';
                isWhitespace = true;
                trimmed = true;
            }

            if (isWhitespace)
            {
                if (previousWasWhitespace)
                {
                    if (builder is null)
                    {
                        builder = new StringBuilder(candidate.Length);
                        builder.Append(candidate.AsSpan(0, i));
                    }

                    trimmed = true;
                    continue;
                }

                previousWasWhitespace = true;
                if (ch != ' ')
                {
                    trimmed = true;
                }

                ch = ' ';
            }
            else
            {
                previousWasWhitespace = false;
            }

            if (builder is not null)
            {
                builder.Append(ch);
            }
            else if (ch != candidate[i])
            {
                builder = new StringBuilder(candidate.Length);
                builder.Append(candidate.AsSpan(0, i));
                builder.Append(ch);
            }
        }

        string normalized = (builder?.ToString() ?? candidate).Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        if (normalized.Length > maxEntryLength)
        {
            normalized = normalized[..maxEntryLength];
            trimmed = true;
        }

        return normalized;
    }

    private sealed record MetadataShapeSummary(string[] Entries, bool Truncated, int TrimmedEntries)
    {
        public static MetadataShapeSummary Empty { get; } = new(Array.Empty<string>(), false, 0);
    }
}

/// <summary>
/// Per-upstream discovery record. All fields are cache-safe to expose —
/// tool / resource / prompt names are public MCP surface metadata, not
/// secrets. Never includes bearer tokens.
/// </summary>
public sealed record UpstreamSnapshot(
    string Id,
    string Url,
    bool Connected,
    string? ErrorCode,
    string? Error,
    string? ServerName,
    string? ServerVersion,
    string? ProtocolVersion,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Resources,
    IReadOnlyList<string> Prompts,
    DateTimeOffset LastProbedAtUtc);
