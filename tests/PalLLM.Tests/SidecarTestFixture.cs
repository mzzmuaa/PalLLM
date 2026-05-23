using System.IO;
using System.Net.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PalLLM.Tests;

/// <summary>
/// Shared ASP.NET Core test host for sidecar integration tests. Configures
/// an isolated runtime root under <c>%TEMP%</c>, disables every opt-in
/// feature (inference, vision, TTS, autosave) so the baseline is purely
/// deterministic, strips all <see cref="IHostedService"/> registrations so
/// background workers don't contend with test assertions, and lets callers
/// layer on <paramref name="extraConfig"/> overrides (e.g. to flip
/// <c>PalLLM:Auth:ApiKey</c> on for a single test) plus optional service
/// replacements when a test needs to force a specific runtime behavior.
/// </summary>
internal sealed class SidecarTestFixture : IAsyncDisposable
{
    public SidecarTestFixture(
        IReadOnlyDictionary<string, string?>? extraConfig = null,
        Action<IServiceCollection>? configureServices = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "PalLLM.Sidecar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureLogging(logging => logging.ClearProviders());
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    Dictionary<string, string?> baseline = new()
                    {
                        ["PalLLM:PalSavedRoot"] = Root,
                        ["PalLLM:Inference:Enabled"] = "false",
                        ["PalLLM:Vision:Enabled"] = "false",
                        ["PalLLM:Tts:Enabled"] = "false",
                        ["PalLLM:Session:Enabled"] = "true",
                        ["PalLLM:Session:EnableAutosave"] = "false",
                        ["PalLLM:Bridge:Enabled"] = "true",
                    };
                    if (extraConfig is not null)
                    {
                        foreach (KeyValuePair<string, string?> pair in extraConfig)
                        {
                            baseline[pair.Key] = pair.Value;
                        }
                    }
                    config.AddInMemoryCollection(baseline);
                });
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    configureServices?.Invoke(services);
                });
            });

        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    public string Root { get; }

    public WebApplicationFactory<Program> Factory { get; }

    public HttpClient Client { get; }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();

        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}
