using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PalLLM.Sidecar.Mcp;

namespace PalLLM.Tests;

public sealed class McpUpstreamClientTests
{
    private const string McpAcceptHeader = "application/json, text/event-stream";

    [Test]
    public async Task GetApiMcpUpstream_WhenNoUpstreamsConfigured_ReturnsEmptyArray()
    {
        // Default config: no upstreams, endpoint must still exist and
        // return a well-typed empty array so consumers don't 404.
        await using var fixture = new SidecarTestFixture();

        UpstreamSnapshot[]? snapshots = await fixture.Client
            .GetFromJsonAsync<UpstreamSnapshot[]>("/api/mcp/upstream");

        Assert.That(snapshots, Is.Not.Null);
        Assert.That(snapshots!, Is.Empty);
    }

    [Test]
    public async Task UpstreamPool_WhenConfiguredServerIsUnreachable_ReportsDisconnectedWithStableFailureCode()
    {
        // Point the pool at a URL nothing is listening on. The discovery
        // worker is stripped in the test fixture so we trigger refresh
        // manually. The snapshot must be recorded as disconnected with a
        // stable machine-readable failure code — never throw, never 5xx
        // the consumer, never leak host-OS socket text into the snapshot.
        var extraConfig = new Dictionary<string, string?>
        {
            // Use a routable but closed port. 127.0.0.1:65000 is reserved
            // and unused in default test environments. Bearer header is
            // deliberately omitted to also exercise the auth-optional
            // path.
            ["PalLLM:McpClient:UpstreamServers:0:Id"] = "test-unreachable",
            ["PalLLM:McpClient:UpstreamServers:0:Url"] = "http://127.0.0.1:65000/mcp",
            ["PalLLM:McpClient:UpstreamServers:0:Enabled"] = "true",
            ["PalLLM:McpClient:DiscoveryTimeoutSeconds"] = "2",
        };
        await using var fixture = new SidecarTestFixture(extraConfig);

        McpUpstreamClientPool pool = fixture.Factory.Services
            .GetRequiredService<McpUpstreamClientPool>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pool.RefreshAsync(cts.Token);

        UpstreamSnapshot[]? snapshots = await fixture.Client
            .GetFromJsonAsync<UpstreamSnapshot[]>("/api/mcp/upstream");

        Assert.That(snapshots, Is.Not.Null);
        Assert.That(snapshots!, Has.Length.EqualTo(1));

        UpstreamSnapshot snapshot = snapshots![0];
        Assert.That(snapshot.Id, Is.EqualTo("test-unreachable"));
        Assert.That(snapshot.Connected, Is.False);
        Assert.That(snapshot.ErrorCode, Is.AnyOf("network", "timeout"));
        Assert.That(snapshot.Error, Is.Not.Null.And.Not.Empty);
        Assert.That(snapshot.Error, Does.Not.Contain("actively refused").And.Not.Contain("No connection could be made"));
        Assert.That(snapshot.Tools, Is.Empty);
        Assert.That(snapshot.Resources, Is.Empty);
        Assert.That(snapshot.Prompts, Is.Empty);
    }

    [Test]
    public async Task UpstreamPool_WhenUpstreamCatalogIsLargeOrVerbose_BoundsCachedMetadata()
    {
        await using var upstream = await FakeUpstreamMcpServer.StartAsync();
        var extraConfig = new Dictionary<string, string?>
        {
            ["PalLLM:McpClient:UpstreamServers:0:Id"] = "bounded-upstream",
            ["PalLLM:McpClient:UpstreamServers:0:Url"] = upstream.Endpoint,
            ["PalLLM:McpClient:UpstreamServers:0:Enabled"] = "true",
            ["PalLLM:McpClient:DiscoveryTimeoutSeconds"] = "5",
            ["PalLLM:McpClient:MaxToolsPerServer"] = "3",
            ["PalLLM:McpClient:MaxResourcesPerServer"] = "2",
            ["PalLLM:McpClient:MaxPromptsPerServer"] = "1",
            ["PalLLM:McpClient:MaxMetadataEntryLength"] = "16",
        };
        await using var fixture = new SidecarTestFixture(extraConfig);

        McpUpstreamClientPool pool = fixture.Factory.Services
            .GetRequiredService<McpUpstreamClientPool>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pool.RefreshAsync(cts.Token);

        UpstreamSnapshot[]? snapshots = await fixture.Client
            .GetFromJsonAsync<UpstreamSnapshot[]>("/api/mcp/upstream");

        Assert.That(snapshots, Is.Not.Null);
        Assert.That(snapshots!, Has.Length.EqualTo(1));

        UpstreamSnapshot snapshot = snapshots![0];
        Assert.That(snapshot.Connected, Is.True);
        Assert.That(snapshot.Tools, Has.Count.EqualTo(3));
        Assert.That(snapshot.Resources.Count, Is.LessThanOrEqualTo(2));
        Assert.That(snapshot.Prompts, Has.Count.EqualTo(1));
        Assert.That(snapshot.Tools.All(static name => name.Length <= 16), Is.True);
        Assert.That(snapshot.Resources.All(static name => name.Length <= 16), Is.True);
        Assert.That(snapshot.Prompts.All(static name => name.Length <= 16), Is.True);
        Assert.That(snapshot.Tools.Any(static name => name.Contains("tool-with-a-very", StringComparison.Ordinal)), Is.True,
            "At least one oversized upstream tool name should be trimmed to the configured metadata-entry cap.");
    }

    [Test]
    public async Task UpstreamPool_WhenConfiguredServerHasInvalidUrl_ReportsInvalidEndpointFailure()
    {
        var extraConfig = new Dictionary<string, string?>
        {
            ["PalLLM:McpClient:UpstreamServers:0:Id"] = "broken-url",
            ["PalLLM:McpClient:UpstreamServers:0:Url"] = "not-a-valid-url",
            ["PalLLM:McpClient:UpstreamServers:0:Enabled"] = "true",
            ["PalLLM:McpClient:DiscoveryTimeoutSeconds"] = "2",
        };
        await using var fixture = new SidecarTestFixture(extraConfig);

        McpUpstreamClientPool pool = fixture.Factory.Services
            .GetRequiredService<McpUpstreamClientPool>();
        await pool.RefreshAsync(CancellationToken.None);

        UpstreamSnapshot[]? snapshots = await fixture.Client
            .GetFromJsonAsync<UpstreamSnapshot[]>("/api/mcp/upstream");

        Assert.That(snapshots, Is.Not.Null);
        Assert.That(snapshots!, Has.Length.EqualTo(1));
        Assert.That(snapshots![0].Connected, Is.False);
        Assert.That(snapshots[0].ErrorCode, Is.EqualTo("invalid_endpoint"));
        Assert.That(snapshots[0].Error, Is.EqualTo("Configured upstream URL is not a valid HTTP(S) MCP endpoint."));
    }

    [Test]
    public async Task UpstreamPool_WhenServerIsDisabled_SkipsProbeAndOmitsFromSnapshots()
    {
        // A disabled upstream must not appear in the snapshot map at all
        // — operators use the Enabled=false switch to pause a problematic
        // upstream without deleting config, and the sidecar should
        // honour that cleanly.
        var extraConfig = new Dictionary<string, string?>
        {
            ["PalLLM:McpClient:UpstreamServers:0:Id"] = "disabled-one",
            ["PalLLM:McpClient:UpstreamServers:0:Url"] = "http://127.0.0.1:65001/mcp",
            ["PalLLM:McpClient:UpstreamServers:0:Enabled"] = "false",
            ["PalLLM:McpClient:DiscoveryTimeoutSeconds"] = "2",
        };
        await using var fixture = new SidecarTestFixture(extraConfig);

        McpUpstreamClientPool pool = fixture.Factory.Services
            .GetRequiredService<McpUpstreamClientPool>();
        await pool.RefreshAsync(CancellationToken.None);

        UpstreamSnapshot[]? snapshots = await fixture.Client
            .GetFromJsonAsync<UpstreamSnapshot[]>("/api/mcp/upstream");

        Assert.That(snapshots, Is.Not.Null);
        Assert.That(snapshots!, Is.Empty,
            "Disabled upstreams must not be surfaced in the snapshot — operators rely on this to pause noisy servers without deleting config.");
    }

    [Test]
    public async Task GetApiMcpUpstream_WhenIfNoneMatchMatches_Returns304AndShortPrivateCacheHeaders()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/mcp/upstream");
        string? etag = first.Headers.ETag?.ToString();

        Assert.That(first.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK));
        Assert.That(etag, Is.Not.Null.And.Not.Empty);
        Assert.That(first.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(first.Headers.CacheControl?.Private, Is.True);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/mcp/upstream");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotModified));
        Assert.That(second.Headers.ETag?.ToString(), Is.EqualTo(etag));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task McpTool_PalListUpstreamMcp_ExposesSameSnapshotsAsRestEndpoint()
    {
        // The MCP tool must return the same underlying snapshot data as
        // the REST endpoint — so an MCP host (Claude Desktop, Cursor)
        // and an HTTP client see the same truth about what upstreams
        // are reachable.
        var extraConfig = new Dictionary<string, string?>
        {
            ["PalLLM:McpClient:UpstreamServers:0:Id"] = "probe-a",
            ["PalLLM:McpClient:UpstreamServers:0:Url"] = "http://127.0.0.1:65002/mcp",
            ["PalLLM:McpClient:UpstreamServers:0:Enabled"] = "true",
            ["PalLLM:McpClient:DiscoveryTimeoutSeconds"] = "2",
        };
        await using var fixture = new SidecarTestFixture(extraConfig);

        McpUpstreamClientPool pool = fixture.Factory.Services
            .GetRequiredService<McpUpstreamClientPool>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pool.RefreshAsync(cts.Token);

        using var mcpRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                """
                {
                  "jsonrpc":"2.0",
                  "id":1,
                  "method":"tools/call",
                  "params":{"name":"pal_list_upstream_mcp","arguments":{}}
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };
        mcpRequest.Headers.Accept.ParseAdd(McpAcceptHeader);
        using HttpResponseMessage mcpResponse = await fixture.Client.SendAsync(mcpRequest);
        mcpResponse.EnsureSuccessStatusCode();

        string raw = await mcpResponse.Content.ReadAsStringAsync();
        string? dataLine = raw
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
        Assert.That(dataLine, Is.Not.Null);

        using JsonDocument document = JsonDocument.Parse(dataLine!["data:".Length..].TrimStart());
        string text = document.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        Assert.That(text, Does.Contain("probe-a"),
            "MCP tool output must include the configured upstream id.");
        Assert.That(text, Does.Contain("\"Connected\":false"),
            "Unreachable upstream must report Connected=false in the MCP tool payload.");
    }

    private sealed class FakeUpstreamMcpServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeUpstreamMcpServer(WebApplication app, string endpoint)
        {
            _app = app;
            Endpoint = endpoint;
        }

        public string Endpoint { get; }

        public static async Task<FakeUpstreamMcpServer> StartAsync()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
            builder.Services.AddMcpServer()
                .WithHttpTransport(options =>
                {
                    options.Stateless = true;
                })
                .WithTools<FakeUpstreamTools>()
                .WithResources<FakeUpstreamResources>()
                .WithPrompts<FakeUpstreamPrompts>();

            WebApplication app = builder.Build();
            app.MapMcp("/mcp");
            await app.StartAsync();

            IServerAddressesFeature? addresses = app.Services
                .GetService<Microsoft.AspNetCore.Hosting.Server.IServer>()?
                .Features
                .Get<IServerAddressesFeature>();
            string address = addresses?.Addresses.FirstOrDefault()
                ?? throw new AssertionException("Fake upstream MCP server did not expose a listening address.");
            string endpoint = new Uri(new Uri(address), "mcp").ToString();
            return new FakeUpstreamMcpServer(app, endpoint);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [McpServerToolType]
    private sealed class FakeUpstreamTools
    {
        [McpServerTool(Name = "alpha-tool")]
        public static string Alpha() => "alpha";

        [McpServerTool(Name = "tool-with-a-very-long-name-1234567890")]
        public static string Verbose() => "verbose";

        [McpServerTool(Name = "beta-tool")]
        public static string Beta() => "beta";

        [McpServerTool(Name = "gamma-tool")]
        public static string Gamma() => "gamma";
    }

    [McpServerResourceType]
    private sealed class FakeUpstreamResources
    {
        [McpServerResource(UriTemplate = "fake://resource/alpha", Name = "alpha-resource", MimeType = "application/json")]
        public static string Alpha() => "{}";

        [McpServerResource(UriTemplate = "fake://resource/beta", Name = "beta-resource", MimeType = "application/json")]
        public static string Beta() => "{}";

        [McpServerResource(UriTemplate = "fake://resource/gamma", Name = "gamma-resource", MimeType = "application/json")]
        public static string Gamma() => "{}";
    }

    [McpServerPromptType]
    private sealed class FakeUpstreamPrompts
    {
        [McpServerPrompt(Name = "prompt-alpha")]
        public static IEnumerable<Microsoft.Extensions.AI.ChatMessage> Alpha() =>
        [
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "alpha"),
        ];

        [McpServerPrompt(Name = "prompt-beta")]
        public static IEnumerable<Microsoft.Extensions.AI.ChatMessage> Beta() =>
        [
            new(Microsoft.Extensions.AI.ChatRole.Assistant, "beta"),
        ];
    }
}
