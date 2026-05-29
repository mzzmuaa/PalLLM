namespace PalLLM.Sidecar;

internal static class PalLlmMcpServiceCollectionExtensions
{
    public static IServiceCollection AddPalLlmMcp(this IServiceCollection services, bool isOpenApiBuild)
    {
        // Opt-in upstream MCP discovery should reuse pooled handlers just like the
        // hot inference / vision / TTS clients do. The MCP SDK transport receives a
        // factory-created HttpClient so periodic upstream probing does not churn
        // sockets or create one-off handler graphs every refresh tick.
        services.AddHttpClient(PalLLM.Sidecar.Mcp.McpUpstreamClientPool.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // PalLLM-as-MCP-client: discovery-only pool of external MCP servers.
        // Configure `PalLLM:McpClient:UpstreamServers[]` with `{ Id, Url, BearerToken?, Enabled }`
        // entries to have the sidecar probe each on startup and every
        // DiscoveryIntervalSeconds (default 300s). The pool caches the
        // discovered tools/resources/prompts for readers; v1 is read-only -
        // PalLLM does NOT automatically proxy tool calls to upstreams.
        services.AddSingleton<PalLLM.Sidecar.Mcp.McpUpstreamClientPool>();
        if (!isOpenApiBuild)
        {
            services.AddHostedService<McpUpstreamDiscoveryWorker>();
        }

        // Model Context Protocol server over Streamable HTTP. Exposes PalLLM's
        // runtime as a full MCP surface - all three primitives:
        //   * Tools (model-controlled actions)  -> PalLlmMcpTools
        //   * Resources (passive context data)  -> PalLlmMcpResources
        //   * Prompts (user-controlled templates) -> PalLlmMcpPrompts
        // to any MCP-aware agent (Claude Desktop, VS Code, Cursor, ChatGPT,
        // custom clients). Stateless mode - each JSON-RPC call is independent,
        // so a fresh MCP client connects without resumption state. Attribute-
        // based registration means every new [McpServerTool]/[McpServerResource]/
        // [McpServerPrompt] method is picked up automatically; no central
        // registration list to keep in sync.
        services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithToolsFromAssembly()
            .WithResourcesFromAssembly()
            .WithPromptsFromAssembly();

        return services;
    }
}
