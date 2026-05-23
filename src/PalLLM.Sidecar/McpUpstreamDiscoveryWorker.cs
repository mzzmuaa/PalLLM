using PalLLM.Domain.Configuration;
using PalLLM.Sidecar.Mcp;

namespace PalLLM.Sidecar;

/// <summary>
/// Background worker that drives <see cref="McpUpstreamClientPool.RefreshAsync"/>.
/// Fires an immediate discovery on startup so the <c>/api/mcp/upstream</c>
/// endpoint has data within seconds, then re-probes on
/// <see cref="McpClientOptions.DiscoveryIntervalSeconds"/> so newly-added
/// tools on the remote side become visible without restarting the sidecar.
/// Skips entirely when no upstream servers are configured.
/// </summary>
public sealed class McpUpstreamDiscoveryWorker : BackgroundService
{
    private readonly PalLlmOptions _options;
    private readonly McpUpstreamClientPool _pool;
    private readonly ILogger<McpUpstreamDiscoveryWorker> _logger;

    public McpUpstreamDiscoveryWorker(
        PalLlmOptions options,
        McpUpstreamClientPool pool,
        ILogger<McpUpstreamDiscoveryWorker> logger)
    {
        _options = options;
        _pool = pool;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.McpClient.UpstreamServers.Count == 0)
        {
            return;
        }

        await TryProbeAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            int seconds = Math.Max(5, _options.McpClient.DiscoveryIntervalSeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await TryProbeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TryProbeAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _pool.RefreshAsync(stoppingToken).ConfigureAwait(false);
            int connected = _pool.GetSnapshots().Values.Count(s => s.Connected);
            int total = _pool.GetSnapshots().Count;
            if (total > 0)
            {
                _logger.LogInformation(
                    "Upstream MCP discovery complete: {Connected}/{Total} servers reachable.",
                    connected, total);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upstream MCP discovery tick threw.");
        }
    }
}
