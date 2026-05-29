using System.Net.Http.Headers;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmInferenceServiceCollectionExtensions
{
    public static IServiceCollection AddPalLlmInference(this IServiceCollection services, bool isOpenApiBuild)
    {
        // HttpClient for the availability probe uses a shorter timeout than chat -
        // a probe that takes 60s would pin the tier-upgrade worker for that long if
        // the endpoint is hung. 10s keeps it snappy without being so tight that a
        // slow-but-alive endpoint gets a false negative.
        services.AddHttpClient<IModelAvailabilityProbe, HttpModelAvailabilityProbe>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        })
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        // Process-wide domain metrics. Owns thread-safe counters for fallback
        // strategy usage, model-tier transitions, and chat-latency histogram
        // buckets. Injected into PalLlmRuntime + ModelTierOrchestrator so those
        // record at their respective call sites. Rendered as labeled Prometheus
        // counters + histogram by PrometheusExporter.
        services.AddSingleton<PalLlmMetrics>();
        services.AddSingleton<InferencePerformanceTracker>();

        services.AddSingleton<ModelTierOrchestrator>(sp => new ModelTierOrchestrator(
            sp.GetRequiredService<PalLlmOptions>(),
            sp.GetRequiredService<IModelAvailabilityProbe>(),
            sp.GetRequiredService<PalLlmMetrics>()));
        services.AddSingleton<ModelCollaborationPlanner>();
        services.AddSingleton<ModelCollaborationDecisionPlanner>();
        // Local-first mesh role registry: reads PalLLM:ModelRoles[] and reports
        // coverage across Edge / Worker / Judge / Media / Validator slots for the
        // /api/roles endpoint, the pal_model_roles MCP tool, and the dashboard
        // role panel. Metadata-only today; future passes may layer role-aware
        // routing on top without changing the operator shape.
        services.AddSingleton<ModelRoleRegistry>();
        // Qwen Duo Mesh planner: deterministic router that turns a
        // (task, risk, hardware, live role coverage) tuple into one of the ten
        // cooperation patterns. Pure C#, no inference call, no external I/O.
        services.AddSingleton<DuoOrchestratorPlanner>();

        // Hard-code promotion ledger: records per-task-class observations so
        // operators + AI callers can see which patterns are stable enough to
        // promote from "AI-assisted workflow" into "deterministic product
        // logic". In-memory bounded deque; see PromotionLedger for the
        // conservative promotion criterion.
        services.AddSingleton<PromotionLedger>();

        // HttpJsonInferenceClient's ctor takes (HttpClient, PalLlmOptions,
        // ModelTierOrchestrator?). The DI container picks the 3-arg ctor because
        // both PalLlmOptions and ModelTierOrchestrator are registered as
        // singletons above, so tier orchestration is wired automatically without
        // an explicit factory.
        services.AddHttpClient<IInferenceClient, HttpJsonInferenceClient>((sp, client) =>
        {
            PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Inference.TimeoutSeconds));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        })
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddHttpClient<IVisionClient, HttpVisionClient>((sp, client) =>
        {
            PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Vision.TimeoutSeconds));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        })
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddHttpClient<ITtsClient, HttpTtsClient>((sp, client) =>
        {
            PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Tts.TimeoutSeconds));
        })
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddHttpClient<IAudioTranscriptionClient, HttpAudioTranscriptionClient>((sp, client) =>
        {
            PalLlmOptions palOptions = sp.GetRequiredService<PalLlmOptions>();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, palOptions.Asr.TimeoutSeconds));
        })
            .UseSocketsHttpHandler((handler, _) =>
            {
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);
                handler.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

        services.AddSingleton<PalLlmRuntime>(sp => new PalLlmRuntime(
            sp.GetRequiredService<PalLlmOptions>(),
            sp.GetRequiredService<IInferenceClient>(),
            sp.GetRequiredService<IVisionClient>(),
            sp.GetRequiredService<ITtsClient>(),
            sp.GetRequiredService<PalLlmMetrics>(),
            sp.GetRequiredService<InferencePerformanceTracker>(),
            sp.GetRequiredService<IAudioTranscriptionClient>()));
        if (!isOpenApiBuild)
        {
            services.AddHostedService<BridgeInboxWorker>();
            services.AddHostedService<InferenceWarmupWorker>();
            services.AddHostedService<ScreenshotWatcher>();
            services.AddHostedService<SessionAutosaveWorker>();
            services.AddHostedService<ModelTierUpgradeWorker>();
            // Conservative watchdog: archives orphan envelopes + writes audit
            // evidence + logs when operator-health drops below the unhealthy
            // floor. Never restarts the sidecar or resets the circuit breaker.
            services.AddHostedService<SelfHealingWorker>();
            // Auto-feeder: turns PalLlmMetrics fallback-strategy delta increments
            // into PromotionLedger observations on a cadence. Opt-in (default on)
            // via PalLlmOptions.PromotionFeeder; pure observer pattern.
            services.AddHostedService<PromotionLedgerFeeder>();
        }

        return services;
    }
}
