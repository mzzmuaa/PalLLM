using System.Net;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

public sealed class SidecarEndpointTests
{
    [Test]
    public async Task DashboardEndpoint_ReturnsDashboardPayloadAndServerTiming()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
            TimeOfDay = "Morning",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 7,
                    DisplayName = "CampGuardian",
                    Species = "CampGuardian",
                },
            ],
        });

        runtime.Adapter.Logger.Info("dashboard-log-entry");
        string outboxPath = Path.Combine(options.BridgeOutboxDir, "reply-001.json");
        await File.WriteAllTextAsync(outboxPath, "{}");

        HttpResponseMessage response = await fixture.Client.GetAsync("/api/dashboard");
        DashboardSnapshot? payload = await response.Content.ReadFromJsonAsync<DashboardSnapshot>();

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(response.Headers.TryGetValues("Server-Timing", out IEnumerable<string>? values), Is.True);
        Assert.That(values!.Single(), Does.StartWith("dashboard;dur="));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Health.AdapterReady, Is.True);
        Assert.That(payload.World.Snapshot.WorldName, Is.EqualTo("Palpagos"));
        Assert.That(payload.Logs.Select(entry => entry.Message), Contains.Item("dashboard-log-entry"));
        Assert.That(payload.Outbox.Select(entry => entry.FileName), Contains.Item("reply-001.json"));
        Assert.That(payload.ServerLatencyMs, Is.GreaterThanOrEqualTo(0));
    }

    [Test]
    public async Task DashboardEndpoint_WhenIfNoneMatchMatches_Returns304NotModified()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/dashboard");
        string? etag = first.Headers.ETag?.ToString();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(etag, Is.Not.Null.And.Not.Empty);
        Assert.That(first.Headers.CacheControl?.NoCache, Is.True);
        Assert.That(first.Headers.CacheControl?.Private, Is.True);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(second.Headers.ETag?.ToString(), Is.EqualTo(etag));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task InferencePerformanceEndpoint_ReturnsRecentLaneSummaryAndSupportsConditionalReads()
    {
        var inferenceClient = new WarmupAwareInferenceClient();
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Inference:Enabled"] = "true",
                ["PalLLM:Inference:Model"] = "warm-model-q4",
                ["PalLLM:Fallback:EnablePolicyBypass"] = "false",
            },
            services =>
            {
                services.RemoveAll<IInferenceClient>();
                services.AddSingleton<IInferenceClient>(inferenceClient);
            });

        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
            runtime.UpdateSnapshot(new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 41,
                        DisplayName = "CampScout",
                        Species = "CampScout",
                    },
                ],
            });

            await runtime.ChatAsync(new ChatRequest
            {
                CharacterId = 41,
                RequestId = "perf-lane-001",
                UserMessage = "Status check.",
                TaskTag = "chat_status",
            }, CancellationToken.None);
        }

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/inference/performance");
        InferencePerformanceSnapshot? payload = await first.Content.ReadFromJsonAsync<InferencePerformanceSnapshot>();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(first.Headers.ETag, Is.Not.Null);
        Assert.That(first.Headers.CacheControl?.NoCache, Is.True);
        Assert.That(first.Headers.CacheControl?.Private, Is.True);
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.SampleCount, Is.EqualTo(1));
        Assert.That(payload.SuccessCount, Is.EqualTo(1));
        Assert.That(payload.Assessment.Status, Is.EqualTo("insufficient_data"));
        Assert.That(payload.Assessment.TargetHitRatioPercent, Is.EqualTo(100));
        Assert.That(payload.Lanes.Select(lane => lane.Model), Contains.Item("warm-model-q4"));
        Assert.That(payload.Lanes.Select(lane => lane.OperationName), Contains.Item("chat"));
        Assert.That(payload.Lanes[0].Assessment.Status, Is.EqualTo("insufficient_data"));
        Assert.That(payload.Lanes[0].LastPromptTokens, Is.EqualTo(1));
        Assert.That(payload.Lanes[0].LastCompletionTokens, Is.EqualTo(1));
        Assert.That(payload.Lanes[0].LastTotalTokens, Is.EqualTo(2));
        Assert.That(payload.Lanes[0].LastCachedPromptTokens, Is.EqualTo(1));
        Assert.That(payload.Lanes[0].LastCompletionReasoningTokens, Is.EqualTo(1));
        Assert.That(payload.Lanes[0].LastFinishReasons, Is.EqualTo(new[] { "stop" }));
        Assert.That(payload.Lanes[0].LastUpstreamRequestId, Is.EqualTo("req-warmup-aware-001"));
        Assert.That(payload.Lanes[0].LastUpstreamProcessingMs, Is.EqualTo(16.5));
        Assert.That(payload.Lanes[0].LastUpstreamQueueMs, Is.EqualTo(1.25));
        Assert.That(payload.Lanes[0].LastUpstreamTimeToFirstTokenMs, Is.EqualTo(8.75));
        Assert.That(payload.Lanes[0].LastUpstreamPrefillMs, Is.EqualTo(5.5));
        Assert.That(payload.Lanes[0].LastUpstreamDecodeMs, Is.EqualTo(9.25));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/inference/performance");
        request.Headers.IfNoneMatch.ParseAdd(first.Headers.ETag!.ToString());
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task RootDashboardEndpoint_ReturnsBaselineBrowserSecurityHeaders()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/");

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(response.Headers.TryGetValues("Content-Security-Policy", out IEnumerable<string>? cspValues), Is.True);
        Assert.That(cspValues!.Single(), Does.Contain("default-src 'self'"));
        Assert.That(response.Headers.TryGetValues("Permissions-Policy", out IEnumerable<string>? permissionsPolicyValues), Is.True);
        Assert.That(permissionsPolicyValues!.Single(), Does.Contain("camera=()"));
        Assert.That(response.Headers.TryGetValues("Referrer-Policy", out IEnumerable<string>? referrerPolicyValues), Is.True);
        Assert.That(referrerPolicyValues!.Single(), Is.EqualTo("no-referrer"));
        Assert.That(response.Headers.TryGetValues("X-Content-Type-Options", out IEnumerable<string>? contentTypeOptionsValues), Is.True);
        Assert.That(contentTypeOptionsValues!.Single(), Is.EqualTo("nosniff"));
        Assert.That(response.Headers.TryGetValues("X-Frame-Options", out IEnumerable<string>? frameOptionsValues), Is.True);
        Assert.That(frameOptionsValues!.Single(), Is.EqualTo("DENY"));
        Assert.That(response.Headers.ETag, Is.Not.Null);
        Assert.That(response.Headers.ETag!.IsWeak, Is.True);
        Assert.That(response.Headers.CacheControl?.NoCache, Is.True);
        Assert.That(response.Headers.CacheControl?.Public, Is.True);
        Assert.That(response.Content.Headers.LastModified, Is.Not.Null);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.IfNoneMatch.Add(response.Headers.ETag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(second.Headers.ETag?.ToString(), Is.EqualTo(response.Headers.ETag.ToString()));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));

        using var lastModifiedRequest = new HttpRequestMessage(HttpMethod.Get, "/");
        lastModifiedRequest.Headers.IfModifiedSince = response.Content.Headers.LastModified;
        using HttpResponseMessage third = await fixture.Client.SendAsync(lastModifiedRequest);

        Assert.That(third.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(third.Headers.ETag?.ToString(), Is.EqualTo(response.Headers.ETag.ToString()));
        Assert.That(third.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task HealthEndpoints_ReturnStructuredJsonProbePayloads()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage liveResponse = await fixture.Client.GetAsync("/health/live");
        using HttpResponseMessage readyResponse = await fixture.Client.GetAsync("/health/ready");

        Assert.That(liveResponse.IsSuccessStatusCode, Is.True);
        Assert.That(readyResponse.IsSuccessStatusCode, Is.True);
        Assert.That(liveResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/health+json"));
        Assert.That(readyResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/health+json"));

        using JsonDocument live = JsonDocument.Parse(await liveResponse.Content.ReadAsStringAsync());
        using JsonDocument ready = JsonDocument.Parse(await readyResponse.Content.ReadAsStringAsync());

        Assert.That(live.RootElement.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
        Assert.That(live.RootElement.GetProperty("results").TryGetProperty("liveness", out JsonElement liveness), Is.True);
        Assert.That(liveness.GetProperty("data").TryGetProperty("runtime_root", out _), Is.True);

        Assert.That(ready.RootElement.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
        Assert.That(ready.RootElement.GetProperty("results").TryGetProperty("readiness", out JsonElement readiness), Is.True);
        Assert.That(readiness.GetProperty("data").TryGetProperty("adapter_ready", out JsonElement adapterReady), Is.True);
        Assert.That(adapterReady.ValueKind is JsonValueKind.True or JsonValueKind.False, Is.True);
        Assert.That(readiness.GetProperty("data").TryGetProperty("inference_warmup_status", out JsonElement warmupStatus), Is.True);
        Assert.That(warmupStatus.ValueKind, Is.EqualTo(JsonValueKind.String));
        Assert.That(ready.RootElement.GetProperty("results").TryGetProperty("inference_recent_window", out JsonElement inferenceRecentWindow), Is.True);
        Assert.That(inferenceRecentWindow.GetProperty("status").GetString(), Is.EqualTo("Healthy"));
        Assert.That(inferenceRecentWindow.GetProperty("data").TryGetProperty("assessment", out JsonElement assessment), Is.True);
        Assert.That(assessment.GetProperty("Status").GetString(), Is.EqualTo("no_data"));
        Assert.That(inferenceRecentWindow.GetProperty("data").TryGetProperty("alerting_lanes", out JsonElement alertingLanes), Is.True);
        Assert.That(alertingLanes.ValueKind, Is.EqualTo(JsonValueKind.Array));
    }

    [Test]
    public async Task ReadyHealthEndpoint_WhenRecentWindowTurnsCritical_ReturnsDegradedAssessmentData()
    {
        var inferenceClient = new SequencedInferenceClient(
            InferenceResult.Failed(
                "Inference timed out.",
                providerName: "openai_compatible",
                requestModel: "worker-q4",
                responseModel: "worker-q4",
                latencyMs: 9_500,
                errorType: "timeout"),
            InferenceResult.Succeeded(
                "Fallback is holding for now.",
                new TokenUsage(16, 8, 24),
                providerName: "openai_compatible",
                requestModel: "worker-q4",
                responseModel: "worker-q4",
                latencyMs: 8_600),
            InferenceResult.Succeeded(
                "Still above budget.",
                new TokenUsage(14, 6, 20),
                providerName: "openai_compatible",
                requestModel: "worker-q4",
                responseModel: "worker-q4",
                latencyMs: 7_900));
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Inference:Enabled"] = "true",
                ["PalLLM:Inference:Model"] = "worker-q4",
                ["PalLLM:Fallback:EnablePolicyBypass"] = "false",
            },
            services =>
            {
                services.RemoveAll<IInferenceClient>();
                services.AddSingleton<IInferenceClient>(inferenceClient);
            });

        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
            runtime.UpdateSnapshot(new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 52,
                        DisplayName = "CampScout",
                        Species = "CampScout",
                    },
                ],
            });

            for (int index = 0; index < 3; index++)
            {
                await runtime.ChatAsync(new ChatRequest
                {
                    CharacterId = 52,
                    RequestId = $"perf-critical-00{index}",
                    UserMessage = "Status check.",
                    TaskTag = "chat_status",
                }, CancellationToken.None);
            }
        }

        using HttpResponseMessage response = await fixture.Client.GetAsync("/health/ready");
        using JsonDocument ready = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = ready.RootElement;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "Degraded readiness should still return 200 so lightweight probes can keep reading the body.");
        Assert.That(root.GetProperty("status").GetString(), Is.EqualTo("Degraded"));
        Assert.That(root.GetProperty("results").TryGetProperty("inference_recent_window", out JsonElement inferenceRecentWindow), Is.True);
        Assert.That(inferenceRecentWindow.GetProperty("status").GetString(), Is.EqualTo("Degraded"));
        Assert.That(inferenceRecentWindow.GetProperty("data").GetProperty("assessment").GetProperty("Status").GetString(), Is.EqualTo("critical"));
        Assert.That(inferenceRecentWindow.GetProperty("data").GetProperty("critical_lane_count").GetInt32(), Is.EqualTo(1));
        JsonElement firstAlertingLane = inferenceRecentWindow.GetProperty("data").GetProperty("alerting_lanes")[0];
        Assert.That(firstAlertingLane.GetProperty("Model").GetString(), Is.EqualTo("worker-q4"));
        Assert.That(firstAlertingLane.GetProperty("Status").GetString(), Is.EqualTo("critical"));
        Assert.That(firstAlertingLane.GetProperty("LastErrorType").GetString(), Is.EqualTo("timeout"));
    }

    [Test]
    public async Task MetricsEndpoint_ExportsRecentWindowStatusForAlerting()
    {
        var inferenceClient = new WarmupAwareInferenceClient();
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Inference:Enabled"] = "true",
                ["PalLLM:Inference:Model"] = "warm-model-q4",
                ["PalLLM:Fallback:EnablePolicyBypass"] = "false",
            },
            services =>
            {
                services.RemoveAll<IInferenceClient>();
                services.AddSingleton<IInferenceClient>(inferenceClient);
            });

        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
            runtime.UpdateSnapshot(new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 41,
                        DisplayName = "CampScout",
                        Species = "CampScout",
                    },
                ],
            });

            await runtime.ChatAsync(new ChatRequest
            {
                CharacterId = 41,
                RequestId = "metrics-lane-001",
                UserMessage = "Status check.",
                TaskTag = "chat_status",
            }, CancellationToken.None);
        }

        string metrics = await fixture.Client.GetStringAsync("/metrics");

        Assert.That(metrics, Does.Contain("palllm_inference_recent_window_status{status=\"insufficient_data\",budget=\"interactive_chat\"} 1"));
        Assert.That(metrics, Does.Contain("palllm_inference_lane_status{operation=\"chat\",provider=\"openai_compatible\",model=\"warm-model-q4\",budget=\"interactive_chat\",status=\"insufficient_data\"} 1"));
        Assert.That(metrics, Does.Contain("palllm_inference_lane_sample_count{operation=\"chat\",provider=\"openai_compatible\",model=\"warm-model-q4\",budget=\"interactive_chat\"} 1"));
    }

    [Test]
    public async Task ApiHealthEndpoint_ReturnsNativeReadinessSnapshotFromBridgeBoot()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        string bridgeFile = Path.Combine(options.BridgeInboxDir, "boot-001.json");
        await File.WriteAllTextAsync(
            bridgeFile,
            JsonSerializer.Serialize(new
            {
                EventType = "bridge_boot",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    Version = "0.3.0",
                    Status = "booted",
                    Compat = "PalGameStateInGame=present | PalBaseCampManager=present | PalMapManager=present",
                    CompatSignals = new[]
                    {
                        new { Key = "PalGameStateInGame", Present = true },
                        new { Key = "PalBaseCampManager", Present = true },
                        new { Key = "PalMapManager", Present = true },
                    },
                    UiProbeEnabled = true,
                    ActionExecutorEnabled = true,
                    NativeHudRenderEnabled = false,
                    NativeHudWidgetTargetCount = 0,
                    NativeHudWidgetTargets = Array.Empty<string>(),
                    NativeHudConfigSource = "inline_defaults",
                    NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
                    ProductionSamplerEnabled = true,
                    WaypointNativeMarkerEnabled = true,
                },
            }));

        runtime.DrainInbox();

        RuntimeHealth? payload = await fixture.Client.GetFromJsonAsync<RuntimeHealth>("/api/health");

        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.NativeReadiness.BridgeBootSeen, Is.True);
        Assert.That(payload.NativeReadiness.ActionExecutorEnabled, Is.True);
        Assert.That(payload.NativeReadiness.ProductionSamplerReady, Is.True);
        Assert.That(payload.NativeReadiness.WaypointMarkerReady, Is.True);
        Assert.That(payload.NativeReadiness.NativeHudConfigSource, Is.EqualTo("inline_defaults"));
        Assert.That(payload.NativeReadiness.NativeHudConfigPath, Does.EndWith(@"config\native-hud.lua"));
        Assert.That(payload.NativeReadiness.HudBindRecommendation.Status, Is.EqualTo("missing_userwidget_compat"));
        Assert.That(payload.NativeReadiness.CompatSignals.Select(signal => signal.Key),
            Is.SupersetOf(new[] { "PalGameStateInGame", "PalBaseCampManager", "PalMapManager" }));
    }

    [Test]
    public async Task ApiHealthEndpoint_WhenBridgeLoopCloses_ReturnsDeliveryProofSnapshot()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 88,
                    DisplayName = "CampScout",
                    Species = "CampScout",
                },
            ],
        });

        ChatResponse response = await runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 88,
            RequestId = "sidecar-bridge-loop-001",
            UserMessage = "How should we secure this camp?",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        string deliveryFile = Path.Combine(options.BridgeInboxDir, "delivery-001.json");
        await File.WriteAllTextAsync(
            deliveryFile,
            JsonSerializer.Serialize(new
            {
                EventType = "reply_delivery",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = response.RequestId,
                    Speaker = response.CharacterName,
                    ResponsePath = response.ResponsePath,
                    StrategyId = response.FallbackStrategy ?? string.Empty,
                    Phase = response.FallbackPhase ?? string.Empty,
                    UsedFallback = response.UsedFallback,
                    Rendered = true,
                    Surface = "client_message",
                    CardLabel = "Reply",
                    CardIndex = 1,
                    CardCount = 1,
                    Note = "sidecar health test delivery",
                },
            }));

        runtime.DrainInbox();

        string feedbackFile = Path.Combine(options.BridgeInboxDir, "feedback-001.json");
        await File.WriteAllTextAsync(
            feedbackFile,
            JsonSerializer.Serialize(new
            {
                EventType = "travel",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    Origin = "Verdant Hub",
                    Destination = "Hill Watch",
                    Waypoint = "North Gate",
                    Mode = "guided_route",
                    Note = "sidecar health test feedback",
                    RequestId = response.RequestId,
                    SourceStrategy = response.FallbackStrategy ?? string.Empty,
                },
            }));

        runtime.DrainInbox();

        RuntimeHealth? payload = await fixture.Client.GetFromJsonAsync<RuntimeHealth>("/api/health");

        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.BridgeLoop.Status, Is.EqualTo("closed"));
        Assert.That(payload.BridgeLoop.ActiveRequestId, Is.EqualTo(response.RequestId));
        Assert.That(payload.BridgeLoop.VisibleDeliveryConfirmed, Is.True);
        Assert.That(payload.BridgeLoop.ActionFeedbackObserved, Is.True);
        Assert.That(payload.BridgeLoop.LoopClosed, Is.True);
        Assert.That(payload.BridgeLoop.LastReplyDelivery, Is.Not.Null);
        Assert.That(payload.BridgeLoop.LastReplyDelivery!.Surface, Is.EqualTo("client_message"));
        Assert.That(payload.BridgeLoop.LastActionFeedback, Is.Not.Null);
        Assert.That(payload.BridgeLoop.LastActionFeedback!.EventType, Is.EqualTo("travel"));
    }

    [Test]
    public async Task OpenApiEndpoint_ReturnsOpenApi31DocumentCoveringEveryApiRoute()
    {
        // .NET 10's built-in OpenAPI generator produces the document directly
        // from the route registrations in Program.cs, so the spec literally
        // cannot drift from the actual routes â€” but a test that the endpoint
        // exists and lists every /api path keeps us honest against a future
        // refactor that might accidentally drop the MapOpenApi() call.
        await using var fixture = new SidecarTestFixture();

        string json = await fixture.Client.GetStringAsync("/openapi/v1.json");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.That(root.TryGetProperty("openapi", out JsonElement version), Is.True);
        Assert.That(version.GetString(), Does.StartWith("3."),
            "Built-in generator should emit OpenAPI 3.x");

        Assert.That(root.GetProperty("info").GetProperty("title").GetString(),
            Is.EqualTo("PalLLM sidecar API"));

        JsonElement paths = root.GetProperty("paths");
        string[] expectedApiPaths =
        [
            "/api/health",
            "/api/dashboard",
            "/api/features",
            "/api/bridge/proof",
            "/api/inference/performance",
            "/api/release/readiness",
            "/api/inference/collaboration",
            "/api/inference/collaboration/plan",
            "/api/inference/warmup",
            "/api/packs",
            "/api/logs",
            "/api/world",
            "/api/relationships",
            "/api/relationships/{characterId}",
            "/api/bridge/outbox",
            "/api/bridge/ui-probe",
            "/api/packs/reload",
            "/api/packs/validate",
            "/api/snapshot",
            "/api/bridge/drain",
            "/api/bridge/outbox/clear",
            "/api/chat",
            "/api/memory/recall",
            "/api/vision/describe",
            "/api/vision/world-state",
            "/api/vision/screenshots/process",
            "/api/session/save",
            "/api/session/reload",
            "/api/tts/synthesize",
            "/api/audio/transcribe",
            "/api/mcp/upstream",
        ];

        foreach (string path in expectedApiPaths)
        {
            Assert.That(paths.TryGetProperty(path, out _), Is.True,
                $"OpenAPI document is missing path '{path}'. The generator is built on route registrations, so a missing path means Program.cs no longer registers it.");
        }

        JsonElement chatPost = paths.GetProperty("/api/chat").GetProperty("post");
        Assert.That(chatPost.GetProperty("operationId").GetString(), Is.EqualTo("PostChatTurn"));
        Assert.That(chatPost.GetProperty("summary").GetString(), Does.Contain("chat turn"));
        Assert.That(chatPost.GetProperty("tags")[0].GetString(), Is.EqualTo("Conversation"));
        Assert.That(chatPost.GetProperty("responses").TryGetProperty("429", out _), Is.True,
            "The OpenAPI contract should advertise the concurrency-shed 429 response for /api/chat.");

        JsonElement packValidatePost = paths.GetProperty("/api/packs/validate").GetProperty("post");
        Assert.That(packValidatePost.GetProperty("operationId").GetString(), Is.EqualTo("ValidateNarrativePack"));
        Assert.That(
            packValidatePost.GetProperty("requestBody").GetProperty("content").TryGetProperty("application/json", out _),
            Is.True,
            "The pack validator consumes HttpRequest directly, so Accepts(application/json) metadata must keep the request body content type visible in OpenAPI.");

        JsonElement schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.That(schemas.TryGetProperty("GameWorldSnapshot", out JsonElement worldSnapshotSchema), Is.True);
        Assert.That(schemas.TryGetProperty("GameCharacterSnapshot", out _), Is.True);
        Assert.That(schemas.TryGetProperty("GameBaseSnapshot", out _), Is.True);
        string[] leakedInternalSchemaNames = schemas.EnumerateObject()
            .Select(property => property.Name)
            .Where(name =>
                name.StartsWith("Palworld", StringComparison.Ordinal)
                || name.StartsWith("BridgeGame", StringComparison.Ordinal)
                || name.StartsWith("SnapshotWorldClock", StringComparison.Ordinal)
                || name.StartsWith("RuntimePathProvider", StringComparison.Ordinal)
                || name.StartsWith("AdapterLogger", StringComparison.Ordinal))
            .ToArray();
        Assert.That(leakedInternalSchemaNames, Is.Empty,
            "OpenAPI should publish only neutral snapshot schema ids, not internal bridge implementation names.");
        Assert.That(
            paths.GetProperty("/api/snapshot")
                .GetProperty("post")
                .GetProperty("requestBody")
                .GetProperty("content")
                .GetProperty("application/json")
                .GetProperty("schema")
                .GetProperty("$ref")
                .GetString(),
            Is.EqualTo("#/components/schemas/GameWorldSnapshot"));
        Assert.That(
            worldSnapshotSchema.GetProperty("description").GetString(),
            Does.Contain("neutral"),
            "The world-snapshot schema should document why the published contract uses a neutral name.");
    }

    [Test]
    public async Task InferenceCollaborationEndpoint_ReturnsStructuredHardwareAwarePlan()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Inference:ModelTiers:0:Id"] = "worker",
            ["PalLLM:Inference:ModelTiers:0:Model"] = "unsloth/Qwen3.6-35B-A3B-GGUF",
            ["PalLLM:Inference:ModelTiers:0:Priority"] = "10",
            ["PalLLM:Inference:ModelTiers:1:Id"] = "judge",
            ["PalLLM:Inference:ModelTiers:1:Model"] = "unsloth/Qwen3.6-27B-GGUF",
            ["PalLLM:Inference:ModelTiers:1:Priority"] = "9",
        });

        string json = await fixture.Client.GetStringAsync("/api/inference/collaboration?vramGb=48&ramGb=128&preferParallel=true");
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.That(document.RootElement.GetProperty("Hardware").GetProperty("ClassId").GetString(), Is.EqualTo("workstation"));
        Assert.That(document.RootElement.GetProperty("ConfiguredModels").GetArrayLength(), Is.EqualTo(2));
        Assert.That(document.RootElement.GetProperty("Recipes")[0].GetProperty("Id").GetString(), Is.EqualTo("fast-draft-dense-judge"));
        Assert.That(
            document.RootElement.GetProperty("ConfiguredModels")[0].GetProperty("Authority").GetProperty("MayDraftChanges").GetBoolean(),
            Is.True);
        JsonElement servingProfile = document.RootElement
            .GetProperty("ConfiguredModels")[0]
            .GetProperty("Capability")
            .GetProperty("ServingProfile");
        Assert.That(servingProfile.GetProperty("ProfileId").GetString(), Is.EqualTo("gguf-libmtmd-multimodal"));
        Assert.That(
            servingProfile.GetProperty("StartupHints").EnumerateArray().Select(element => element.GetString()),
            Contains.Item("--limit-mm-per-prompt.video 1"));
        Assert.That(
            servingProfile.GetProperty("SecurityControls").EnumerateArray().Select(element => element.GetString()),
            Has.Some.Contains("allowed-media-domains"));
        Assert.That(
            servingProfile.GetProperty("SecurityControls").EnumerateArray().Select(element => element.GetString()),
            Has.Some.Contains("VLLM_MAX_N_SEQUENCES"));
        Assert.That(
            servingProfile.GetProperty("VerificationChecks").EnumerateArray().Select(element => element.GetString()),
            Has.Some.Contains("media UUIDs"));
        Assert.That(
            document.RootElement.GetProperty("RoutingPolicies")[0].GetProperty("Id").GetString(),
            Is.EqualTo("low-risk-fast-lane"));
        Assert.That(
            document.RootElement.GetProperty("QualificationSuite").GetProperty("Checks")[0].GetProperty("Id").GetString(),
            Is.EqualTo("exact-json-tool-call"));
        Assert.That(
            document.RootElement.GetProperty("HardwarePlaybook")[0].GetProperty("TierId").GetString(),
            Is.EqualTo("cpu-only"));
        Assert.That(
            document.RootElement.GetProperty("SelfHealingIdeas")[0].GetProperty("Id").GetString(),
            Is.EqualTo("doc-and-contract-drift-patrol"));
    }

    [Test]
    public async Task SnapshotEndpoint_NormalizesNullCollectionsFromBridgePayload()
    {
        await using var fixture = new SidecarTestFixture();

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/api/snapshot", new
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
            ActiveBaseIds = (string[]?)null,
            KnownBases = (object[]?)null,
            NearbyHostiles = (string[]?)null,
            NearbyFriendlies = (string[]?)null,
            NearbyResources = (string[]?)null,
            RecentEvents = (string[]?)null,
            Characters = new object[]
            {
                new
                {
                    Id = 7,
                    DisplayName = "Lifmunk",
                    Species = "Lifmunk",
                    Position = (object?)null,
                    Skills = (object?)null,
                    Needs = (object?)null,
                    Loadout = (string[]?)null,
                    RecentEvents = (string[]?)null,
                    Traits = (string[]?)null,
                    Tags = (string[]?)null,
                },
            },
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));

        RuntimeWorldState? world = await fixture.Client.GetFromJsonAsync<RuntimeWorldState>("/api/world");
        Assert.That(world, Is.Not.Null);
        Assert.That(world!.Snapshot.ActiveBaseIds, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.KnownBases, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.NearbyHostiles, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.NearbyFriendlies, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.NearbyResources, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.RecentEvents, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.Characters, Has.Count.EqualTo(1));
        Assert.That(world.Snapshot.Characters[0].Position, Is.Not.Null);
        Assert.That(world.Snapshot.Characters[0].Skills, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.Characters[0].Needs, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.Characters[0].Loadout, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.Characters[0].RecentEvents, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.Characters[0].Traits, Is.Not.Null.And.Empty);
        Assert.That(world.Snapshot.Characters[0].Tags, Is.Not.Null.And.Empty);
    }

    [Test]
    public async Task InferenceWarmupEndpoint_WarmsActiveLaneAndHealthSurfaceReportsIt()
    {
        var fakeInference = new WarmupAwareInferenceClient();
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Inference:Enabled"] = "true",
                ["PalLLM:Inference:Model"] = "warm-model-q4",
                ["PalLLM:Inference:EnableWarmup"] = "true",
                ["PalLLM:Inference:WarmupMaxTokens"] = "1",
            },
            services =>
            {
                services.RemoveAll<IInferenceClient>();
                services.AddSingleton<IInferenceClient>(fakeInference);
            });

        using HttpResponseMessage warmupResponse = await fixture.Client.PostAsync("/api/inference/warmup", content: null);
        InferenceWarmupSnapshot? warmup = await warmupResponse.Content.ReadFromJsonAsync<InferenceWarmupSnapshot>();
        RuntimeHealth? health = await fixture.Client.GetFromJsonAsync<RuntimeHealth>("/api/health");

        Assert.That(warmupResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(warmup, Is.Not.Null);
        Assert.That(warmup!.Status, Is.EqualTo("ready"));
        Assert.That(warmup.ActiveModel, Is.EqualTo("warm-model-q4"));
        Assert.That(warmup.ActiveTierId, Is.EqualTo("small"));
        Assert.That(warmup.LastWarmedModel, Is.EqualTo("warm-model-q4"));
        Assert.That(warmup.LastReason, Is.EqualTo("manual_api"));
        Assert.That(warmup.SuccessCount, Is.EqualTo(1));
        Assert.That(warmup.AttemptCount, Is.EqualTo(1));
        Assert.That(warmup.LastSeenAvailableModels, Is.EqualTo(new[] { "warm-model-q4", "warm-model-q8" }));

        Assert.That(fakeInference.CallCount, Is.EqualTo(1));
        Assert.That(fakeInference.LastPrompt, Is.Not.Null);
        Assert.That(fakeInference.LastPrompt!.UserPrompt, Is.EqualTo("OK"));
        Assert.That(fakeInference.LastPrompt.SystemPrompt, Does.Contain("warmup request"));
        Assert.That(fakeInference.LastPrompt.MaxTokens, Is.EqualTo(1));

        Assert.That(health, Is.Not.Null);
        Assert.That(health!.InferenceActiveModel, Is.EqualTo("warm-model-q4"));
        Assert.That(health.InferenceActiveTierId, Is.EqualTo("small"));
        Assert.That(health.InferenceLastSeenAvailableModels, Is.EqualTo(new[] { "warm-model-q4", "warm-model-q8" }));
        Assert.That(health.InferenceWarmup.Status, Is.EqualTo("ready"));
        Assert.That(health.InferenceWarmup.LastReason, Is.EqualTo("manual_api"));
    }

    [Test]
    public async Task InferenceCollaborationPlanEndpoint_ReturnsTaskSpecificExecutionPlan()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Inference:ModelTiers:0:Id"] = "worker",
            ["PalLLM:Inference:ModelTiers:0:Model"] = "unsloth/Qwen3.6-35B-A3B-GGUF",
            ["PalLLM:Inference:ModelTiers:0:Priority"] = "10",
            ["PalLLM:Inference:ModelTiers:1:Id"] = "judge",
            ["PalLLM:Inference:ModelTiers:1:Model"] = "unsloth/Qwen3.6-27B-GGUF",
            ["PalLLM:Inference:ModelTiers:1:Priority"] = "9",
        });

        using HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/api/inference/collaboration/plan", new
        {
            Task = "Audit a release-facing auth migration with tool-driven edits",
            RiskLevel = "high",
            TaskClass = "coding",
            ToolHeavy = true,
            ReleaseGate = true,
            VramGb = 48,
            RamGb = 128,
            PreferParallel = true,
        });
        string json = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), json);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.That(document.RootElement.GetProperty("SelectedPolicyId").GetString(), Is.EqualTo("high-risk-deliberate-bookends"));
        Assert.That(document.RootElement.GetProperty("SelectedRecipeId").GetString(), Is.EqualTo("dense-plan-fast-execute-dense-audit"));
        Assert.That(document.RootElement.GetProperty("RunMode").GetString(), Is.EqualTo("parallel"));
        Assert.That(document.RootElement.GetProperty("FastLaneModel").GetString(), Does.Contain("35B-A3B"));
        Assert.That(document.RootElement.GetProperty("DeliberateLaneModel").GetString(), Does.Contain("27B"));
        Assert.That(document.RootElement.GetProperty("HumanReviewRequired").GetBoolean(), Is.True);
        Assert.That(document.RootElement.GetProperty("PromotionCriteria")[0].GetString(), Does.Contain("repeated-run-stability"));
    }

    [Test]
    public async Task OpenApiYamlEndpoint_ReturnsYamlVariantOfTheDocument()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/openapi/v1.yaml");
        string yaml = await response.Content.ReadAsStringAsync();

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(yaml, Does.Contain("openapi: '3."));
        Assert.That(yaml, Does.Contain("/api/chat:"));
        Assert.That(yaml, Does.Contain("operationId: PostChatTurn"));
    }

    [Test]
    public async Task FeaturesEndpoint_ReturnsEveryCatalogEntryWithTheirShippedShape()
    {
        // The /api/features endpoint is how the dashboard and any integration
        // discovers what PalLLM can do. It is also the only observable surface
        // for the feature catalog â€” a regression where an entry disappears
        // (or a new entry lacks required fields) would otherwise only show up
        // in the dashboard. Test that the endpoint returns every entry and
        // that each carries the full shape.
        await using var fixture = new SidecarTestFixture();

        HttpResponseMessage response = await fixture.Client.GetAsync("/api/features");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement array = document.RootElement;
        Assert.That(array.ValueKind, Is.EqualTo(JsonValueKind.Array));

        int entryCount = array.GetArrayLength();
        Assert.That(entryCount, Is.EqualTo(PalLlmFeatureCatalog.All.Count),
            "Endpoint count must match the static catalog. If this fails, either the catalog shrank or the endpoint is filtering.");

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JsonElement entry in array.EnumerateArray())
        {
            string? id = entry.GetProperty("Id").GetString();
            Assert.That(string.IsNullOrWhiteSpace(id), Is.False, "Every entry must carry a non-empty Id.");
            Assert.That(ids.Add(id!), Is.True, $"Duplicate feature id '{id}' across catalog entries â€” ids must be unique.");

            Assert.That(string.IsNullOrWhiteSpace(entry.GetProperty("Source").GetString()), Is.False,
                $"Entry '{id}' must have a non-empty Source.");
            Assert.That(string.IsNullOrWhiteSpace(entry.GetProperty("Status").GetString()), Is.False,
                $"Entry '{id}' must have a non-empty Status.");
            Assert.That(string.IsNullOrWhiteSpace(entry.GetProperty("Summary").GetString()), Is.False,
                $"Entry '{id}' must have a non-empty Summary.");
            // Notes is the only optional field â€” entries can omit it, but the
            // property must still exist (empty string default) for consumer
            // parsing simplicity.
            Assert.That(entry.TryGetProperty("Notes", out _), Is.True,
                $"Entry '{id}' must expose a Notes property even if empty.");
        }
    }

    [Test]
    public async Task FeaturesEndpoint_WhenIfNoneMatchMatches_Returns304AndPrivateCacheHeaders()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/features");
        string? etag = first.Headers.ETag?.ToString();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(etag, Is.Not.Null.And.Not.Empty);
        Assert.That(first.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromMinutes(60)));
        Assert.That(first.Headers.CacheControl?.Private, Is.True);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/features");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(second.Headers.ETag?.ToString(), Is.EqualTo(etag));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_ReturnsMachineReadablePublicationSnapshot_AndSanitizesOversizedArtifacts()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? firstPayload = await first.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(firstPayload, Is.Not.Null);
        Assert.That(firstPayload!.Runtime.AdapterName, Is.Not.Null.And.Not.Empty);
        Assert.That(firstPayload.Runtime.ApiRouteCount, Is.EqualTo(57));
        Assert.That(firstPayload.Runtime.ProtocolRouteCount, Is.EqualTo(1));
        Assert.That(firstPayload.Runtime.FeaturedOperationalSurfaceCount, Is.EqualTo(6));
        Assert.That(firstPayload.Runtime.ConditionalReadPaths, Does.Contain("/api/describe"));
        Assert.That(firstPayload.Runtime.ConditionalReadPaths, Does.Contain("/api/bridge/proof"));
        Assert.That(firstPayload.Runtime.ConditionalReadPaths, Does.Contain("/api/inference/performance"));
        Assert.That(firstPayload.Runtime.ConditionalReadPaths, Does.Contain("/api/release/readiness"));
        Assert.That(firstPayload.Features.Total, Is.EqualTo(PalLlmFeatureCatalog.All.Count));
        Assert.That(firstPayload.Features.Ready, Is.EqualTo(PalLlmFeatureCatalog.All.Count(feature => feature.Status == "ready")));
        Assert.That(firstPayload.Features.Scaffolded, Is.EqualTo(PalLlmFeatureCatalog.All.Count(feature => feature.Status == "scaffolded")));
        Assert.That(firstPayload.Features.Deferred, Is.EqualTo(PalLlmFeatureCatalog.All.Count(feature => feature.Status == "deferred")));
        Assert.That(firstPayload.Publication.Status, Is.EqualTo("caution"));
        Assert.That(firstPayload.Publication.NextRecommendedCommand, Does.Contain("scripts/run-native-proof.ps1"));
        Assert.That(firstPayload.Publication.CurrentBlockers.Any(blocker => blocker.Contains("product name", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(firstPayload.SmokeEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.SmokeEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(firstPayload.SmokeEvidence.FreshnessWindowHours, Is.EqualTo(24));
        Assert.That(firstPayload.SmokeEvidence.ArtifactPath, Does.EndWith("latest-smoke.json"));
        Assert.That(firstPayload.NativeProofEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.NativeProofEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(firstPayload.NativeProofEvidence.FreshnessWindowHours, Is.EqualTo(24));
        Assert.That(firstPayload.NativeProofEvidence.ArtifactPath, Does.EndWith("latest-native-proof.json"));
        Assert.That(firstPayload.NativeProofEvidence.DiagnosisAction, Does.Contain("Capture"));
        Assert.That(firstPayload.NativeProofEvidence.DiagnosisCommand, Does.Contain("scripts/run-native-proof.ps1"));
        Assert.That(firstPayload.ProofBundleEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.ProofBundleEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(firstPayload.ProofBundleEvidence.FreshnessWindowHours, Is.EqualTo(24));
        Assert.That(firstPayload.ProofBundleEvidence.ArtifactPath, Does.EndWith("latest-proof-bundle.json"));
        Assert.That(firstPayload.ProofBundleEvidence.ArchivePath, Does.EndWith("latest-proof-bundle.zip"));
        Assert.That(firstPayload.SupportBundleEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.SupportBundleEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(firstPayload.SupportBundleEvidence.FreshnessWindowHours, Is.EqualTo(24));
        Assert.That(firstPayload.SupportBundleEvidence.ArtifactPath, Does.EndWith("latest-support-bundle.json"));
        Assert.That(firstPayload.SupportBundleEvidence.ArchivePath, Does.EndWith("latest-support-bundle.zip"));
        Assert.That(firstPayload.PackageVerificationEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.PackageVerificationEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(firstPayload.PackageVerificationEvidence.FreshnessWindowHours, Is.EqualTo(24));
        Assert.That(firstPayload.PackageVerificationEvidence.ArtifactPath, Does.EndWith("latest-package-verification.json"));
        Assert.That(firstPayload.ArtifactIntegrityEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.ArtifactIntegrityEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(firstPayload.ArtifactIntegrityEvidence.FreshnessWindowHours, Is.EqualTo(24));
        Assert.That(firstPayload.ArtifactIntegrityEvidence.ArtifactPath, Does.EndWith("latest-artifact-integrity.json"));
        Assert.That(firstPayload.FullAuditEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.FullAuditEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(firstPayload.FullAuditEvidence.FreshnessWindowHours, Is.EqualTo(24));
        Assert.That(firstPayload.FullAuditEvidence.ArtifactPath, Does.EndWith("latest-full-audit.json"));
        Assert.That(firstPayload.Surfaces.Select(surface => surface.Path), Contains.Item("/api/bridge/proof"));
        Assert.That(firstPayload.Surfaces.Select(surface => surface.Path), Contains.Item("/api/inference/performance"));
        Assert.That(firstPayload.Surfaces.Select(surface => surface.Path), Contains.Item("/api/release/readiness"));
        Assert.That(firstPayload.Surfaces.Select(surface => surface.Path), Contains.Item("/mcp"));
        Assert.That(firstPayload.Audits.Select(audit => audit.Id), Contains.Item("full-audit"));
        Assert.That(firstPayload.Audits.Select(audit => audit.Id), Contains.Item("sidecar-smoke"));
        Assert.That(firstPayload.Audits.Select(audit => audit.Id), Contains.Item("native-proof"));
        Assert.That(firstPayload.Audits.Select(audit => audit.Id), Contains.Item("proof-bundle"));
        Assert.That(firstPayload.Audits.Select(audit => audit.Id), Contains.Item("support-bundle"));
        Assert.That(firstPayload.Audits.Select(audit => audit.Id), Contains.Item("package-verify"));
        Assert.That(firstPayload.Audits.Select(audit => audit.Id), Contains.Item("artifact-integrity"));
        Assert.That(firstPayload.Documents.Select(document => document.Path), Contains.Item("docs/RELEASE.md"));

        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();
            await File.WriteAllTextAsync(
                options.LatestSmokeEvidencePath,
                new string('x', options.Http.LocalArtifactMaxBytes + 32));
        }

        using HttpResponseMessage second = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? secondPayload = await second.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(secondPayload, Is.Not.Null);
        Assert.That(secondPayload!.SmokeEvidence.Status, Is.EqualTo("invalid"));
        Assert.That(secondPayload.SmokeEvidence.Summary, Does.Contain("configured size limit"));
        Assert.That(secondPayload.SmokeEvidence.Summary, Does.Not.Contain("System."));
        Assert.That(secondPayload.SmokeEvidence.Summary, Does.Not.Contain("Exception"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenSmokeArtifactExists_ReturnsSmokeEvidenceSummary()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        ReleaseSmokeEvidenceSnapshot expected = new()
        {
            Status = "recorded",
            Summary = "Palworld smoke loop closed and native readiness was captured.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestSmokeEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "smoke-20260422-001.json"),
            BaseUrl = "http://localhost:5088",
            RequestId = "release-smoke-001",
            ResponsePath = "Responses\\release-smoke-001.txt",
            BridgeProofStatus = "delivery_proven",
            BridgeLoopStatus = "closed",
            LoopClosed = true,
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = true,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "native_hud",
            ActionType = "waypoint_suggest",
            UsedFallback = false,
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestSmokeEvidencePath,
            JsonSerializer.Serialize(expected));

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.SmokeEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(payload.SmokeEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(payload.SmokeEvidence.FreshUntilUtc, Is.GreaterThan(expected.CapturedAtUtc));
        Assert.That(payload.SmokeEvidence.RequestId, Is.EqualTo(expected.RequestId));
        Assert.That(payload.SmokeEvidence.BridgeProofStatus, Is.EqualTo("delivery_proven"));
        Assert.That(payload.SmokeEvidence.NativeHudBindReady, Is.True);
        Assert.That(payload.SmokeEvidence.RecommendedHudTarget, Is.EqualTo("/Game/UI/WBP_PalHudOverlay"));
        Assert.That(payload.SmokeEvidence.ConfiguredHudTargets, Contains.Item("/Game/UI/WBP_PalHudOverlay"));
        Assert.That(payload.SmokeEvidence.NativeHudConfigSource, Is.EqualTo("mod_override_file"));
        Assert.That(payload.SmokeEvidence.NativeHudConfigPath, Does.EndWith(@"config\native-hud.lua"));
        Assert.That(payload.NativeProofEvidence.Status, Is.EqualTo("missing"));
        Assert.That(payload.ProofBundleEvidence.Status, Is.EqualTo("missing"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenNativeProofArtifactExists_ReturnsNativeProofEvidenceSummary()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        DateTimeOffset watcherStartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-8);
        DateTimeOffset watcherFinishedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        ReleaseNativeProofEvidenceSnapshot expected = new()
        {
            Status = "proven",
            Summary = "Live Palworld native HUD delivery was proven on the current build.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            WatcherStartedAtUtc = watcherStartedAtUtc,
            WatcherFinishedAtUtc = watcherFinishedAtUtc,
            WatcherCompletionReason = "delivery_proven",
            TimeoutSeconds = 180,
            PollIntervalSeconds = 2,
            PollCount = 3,
            TimedOut = false,
            DiagnosisCode = "native_hud_delivery_proven",
            DiagnosisSummary = "Live Palworld native HUD delivery has matching bridge proof.",
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-20260422-001.json"),
            BaseUrl = "http://localhost:5088",
            BridgeProofStatus = "delivery_proven",
            ActiveRequestId = "native-proof-001",
            LiveDeliveryProven = true,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "native_hud",
            LoopStatus = "closed",
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = true,
            AppliedHudRecommendation = true,
            AppliedHudRecommendationPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            RecommendedNextStep = "Capture and archive this proof as release evidence.",
            CurrentBlockers = [],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
            StatusTransitions =
            [
                new ReleaseNativeProofStatusTransition
                {
                    ObservedAtUtc = watcherStartedAtUtc,
                    PollIndex = 0,
                    BridgeProofStatus = "awaiting_delivery",
                    Summary = "Bridge is waiting for visible delivery.",
                    ActiveRequestId = "native-proof-001",
                    LoopStatus = "awaiting_delivery",
                    LiveDeliveryProven = false,
                    NativeHudBindReady = true,
                    VisibleDeliveryConfirmed = false,
                    DeliverySurface = "",
                },
                new ReleaseNativeProofStatusTransition
                {
                    ObservedAtUtc = watcherFinishedAtUtc,
                    PollIndex = 3,
                    BridgeProofStatus = "delivery_proven",
                    Summary = "Live Palworld native HUD delivery was proven on the current build.",
                    ActiveRequestId = "native-proof-001",
                    LoopStatus = "closed",
                    LiveDeliveryProven = true,
                    NativeHudBindReady = true,
                    VisibleDeliveryConfirmed = true,
                    DeliverySurface = "native_hud",
                },
            ],
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestNativeProofEvidencePath,
            JsonSerializer.Serialize(expected));

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.NativeProofEvidence.Status, Is.EqualTo("proven"));
        Assert.That(payload.NativeProofEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(payload.NativeProofEvidence.FreshUntilUtc, Is.GreaterThan(expected.CapturedAtUtc));
        Assert.That(payload.NativeProofEvidence.BridgeProofStatus, Is.EqualTo("delivery_proven"));
        Assert.That(payload.NativeProofEvidence.ActiveRequestId, Is.EqualTo(expected.ActiveRequestId));
        Assert.That(payload.NativeProofEvidence.NativeHudBindReady, Is.True);
        Assert.That(payload.NativeProofEvidence.ConfiguredHudTargets, Contains.Item("/Game/UI/WBP_PalHudOverlay"));
        Assert.That(payload.NativeProofEvidence.NativeHudConfigSource, Is.EqualTo("mod_override_file"));
        Assert.That(payload.NativeProofEvidence.NativeHudConfigPath, Does.EndWith(@"config\native-hud.lua"));
        Assert.That(payload.NativeProofEvidence.WatcherCompletionReason, Is.EqualTo("delivery_proven"));
        Assert.That(payload.NativeProofEvidence.PollCount, Is.EqualTo(3));
        Assert.That(payload.NativeProofEvidence.TimedOut, Is.False);
        Assert.That(payload.NativeProofEvidence.DiagnosisCode, Is.EqualTo("native_hud_delivery_proven"));
        Assert.That(payload.NativeProofEvidence.DiagnosisSummary, Does.Contain("matching bridge proof"));
        Assert.That(payload.NativeProofEvidence.DiagnosisAction, Does.Contain("release smoke"));
        Assert.That(payload.NativeProofEvidence.DiagnosisCommand, Does.Contain("scripts/run-sidecar-smoke.ps1"));
        Assert.That(payload.NativeProofEvidence.StatusTransitions, Has.Count.EqualTo(2));
        Assert.That(payload.NativeProofEvidence.StatusTransitions[0].BridgeProofStatus, Is.EqualTo("awaiting_delivery"));
        Assert.That(payload.NativeProofEvidence.StatusTransitions[1].BridgeProofStatus, Is.EqualTo("delivery_proven"));
        Assert.That(payload.NativeProofEvidence.StatusTransitions[1].DeliverySurface, Is.EqualTo("native_hud"));
        Assert.That(payload.ProofBundleEvidence.Status, Is.EqualTo("missing"));
        Assert.That(payload.Publication.NextRecommendedPass, Does.Not.Contain("scripts/run-native-proof.ps1"));
        Assert.That(payload.Publication.NextRecommendedPass, Does.Contain("scripts/run-sidecar-smoke.ps1"));
        Assert.That(payload.Publication.NextRecommendedCommand, Does.Not.Contain("scripts/run-native-proof.ps1"));
        Assert.That(payload.Publication.NextRecommendedCommand, Does.Contain("scripts/run-sidecar-smoke.ps1"));

        ReleaseNativeProofEvidenceSnapshot contradicted = new()
        {
            Status = "proven",
            Summary = "Tampered artifact claims proof without matching bridge evidence.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-contradicted.json"),
            BaseUrl = "http://localhost:5088",
            BridgeProofStatus = "awaiting_delivery",
            ActiveRequestId = "native-proof-contradicted",
            LiveDeliveryProven = false,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "native_hud",
            LoopStatus = "awaiting_delivery",
            VisibleDeliveryConfirmed = false,
            ActionFeedbackObserved = false,
            RecommendedNextStep = "This should not unblock packaging.",
        };

        await File.WriteAllTextAsync(
            options.LatestNativeProofEvidencePath,
            JsonSerializer.Serialize(contradicted));

        using HttpResponseMessage contradictedResponse = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? contradictedPayload =
            await contradictedResponse.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(contradictedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(contradictedPayload, Is.Not.Null);
        Assert.That(contradictedPayload!.NativeProofEvidence.Status, Is.EqualTo("invalid"));
        Assert.That(contradictedPayload.NativeProofEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(contradictedPayload.NativeProofEvidence.FreshUntilUtc, Is.Null);
        Assert.That(contradictedPayload.NativeProofEvidence.DiagnosisCode, Is.EqualTo("native_proof_artifact_contradiction"));
        Assert.That(contradictedPayload.NativeProofEvidence.DiagnosisSummary, Does.Contain("claims proven native delivery"));
        Assert.That(contradictedPayload.NativeProofEvidence.DiagnosisAction, Does.Contain("Recapture"));
        Assert.That(contradictedPayload.NativeProofEvidence.DiagnosisCommand, Does.Contain("scripts/run-native-proof.ps1"));
        Assert.That(
            contradictedPayload.NativeProofEvidence.CurrentBlockers,
            Contains.Item("Native proof artifact claims proven status without matching native HUD delivery evidence."));
        Assert.That(contradictedPayload.Publication.NextRecommendedPass, Does.Contain("scripts/run-native-proof.ps1"));
        Assert.That(contradictedPayload.Publication.NextRecommendedCommand, Does.Contain("scripts/run-native-proof.ps1"));

        ReleaseNativeProofEvidenceSnapshot fallbackSurface = new()
        {
            Status = "proven",
            Summary = "Tampered artifact claims native proof through a fallback surface.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-fallback-surface.json"),
            BaseUrl = "http://localhost:5088",
            BridgeProofStatus = "delivery_proven",
            ActiveRequestId = "native-proof-fallback-surface",
            LiveDeliveryProven = true,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "client_message",
            LoopStatus = "closed",
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = true,
            RecommendedNextStep = "This should not unblock packaging.",
        };

        await File.WriteAllTextAsync(
            options.LatestNativeProofEvidencePath,
            JsonSerializer.Serialize(fallbackSurface));

        using HttpResponseMessage fallbackSurfaceResponse = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? fallbackSurfacePayload =
            await fallbackSurfaceResponse.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(fallbackSurfaceResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fallbackSurfacePayload, Is.Not.Null);
        Assert.That(fallbackSurfacePayload!.NativeProofEvidence.Status, Is.EqualTo("invalid"));
        Assert.That(fallbackSurfacePayload.NativeProofEvidence.FreshnessStatus, Is.EqualTo("unknown"));
        Assert.That(fallbackSurfacePayload.NativeProofEvidence.DiagnosisCode, Is.EqualTo("native_proof_artifact_contradiction"));
        Assert.That(
            fallbackSurfacePayload.NativeProofEvidence.CurrentBlockers,
            Contains.Item("Native proof artifact claims proven status without matching native HUD delivery evidence."));
        Assert.That(fallbackSurfacePayload.Publication.NextRecommendedCommand, Does.Contain("scripts/run-native-proof.ps1"));

        ReleaseNativeProofEvidenceSnapshot timedOutFallbackSurface = new()
        {
            Status = "timed_out",
            Summary = "Native proof timed out after visible fallback delivery.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            WatcherCompletionReason = "delivery_proven_timeout",
            TimedOut = true,
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-timeout-fallback-surface.json"),
            BaseUrl = "http://localhost:5088",
            BridgeProofStatus = "delivery_proven_pending_native_hud_surface",
            ActiveRequestId = "native-proof-timeout-fallback-surface",
            LiveDeliveryProven = true,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "client_message",
            LoopStatus = "closed",
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = true,
            RecommendedNextStep = "Inspect native_hud_render_enabled, native_hud_widget_targets, and UE4SS HUD bind logs.",
        };

        await File.WriteAllTextAsync(
            options.LatestNativeProofEvidencePath,
            JsonSerializer.Serialize(timedOutFallbackSurface));

        using HttpResponseMessage timedOutFallbackSurfaceResponse = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? timedOutFallbackSurfacePayload =
            await timedOutFallbackSurfaceResponse.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(timedOutFallbackSurfaceResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(timedOutFallbackSurfacePayload, Is.Not.Null);
        Assert.That(timedOutFallbackSurfacePayload!.NativeProofEvidence.Status, Is.EqualTo("timed_out"));
        Assert.That(timedOutFallbackSurfacePayload.NativeProofEvidence.DiagnosisCode, Is.EqualTo("native_hud_surface_mismatch"));
        Assert.That(timedOutFallbackSurfacePayload.NativeProofEvidence.DiagnosisSummary, Does.Contain("fallback surface"));
        Assert.That(timedOutFallbackSurfacePayload.NativeProofEvidence.DiagnosisAction, Does.Contain("surface=native_hud"));
        Assert.That(timedOutFallbackSurfacePayload.NativeProofEvidence.DiagnosisCommand, Does.Contain("-ApplyHudRecommendation"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenProofBundleArtifactExists_ReturnsProofBundleEvidenceSummary()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        ReleaseProofBundleEvidenceSnapshot expected = new()
        {
            Status = "recorded",
            Summary = "Palworld release proof bundle captured the current runtime and evidence surfaces.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestProofBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-20260422-001.json"),
            ArchivePath = options.LatestProofBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-20260422-001.zip"),
            BaseUrl = "http://localhost:5088",
            ReleasePublicationStatus = "caution",
            BridgeProofStatus = "delivery_proven",
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            InferencePerformanceStatus = "healthy",
            InferencePerformanceSampleCount = 4,
            InferencePerformanceLaneCount = 2,
            InferencePerformanceAlertingLaneCount = 0,
            InferencePerformanceLatestReceiptLaneCount = 2,
            InferencePerformanceTokenReceiptLaneCount = 1,
            InferencePerformanceFinishReasonReceiptLaneCount = 2,
            InferencePerformanceUpstreamRequestIdReceiptLaneCount = 2,
            InferencePerformanceUpstreamProcessingReceiptLaneCount = 2,
            InferencePerformancePhaseTimingReceiptLaneCount = 2,
            InferencePerformanceUsageDetailReceiptLaneCount = 1,
            InferencePerformanceTotalTokens = 384,
            InferencePerformanceCachedPromptTokens = 192,
            InferencePerformanceCompletionReasoningTokens = 24,
            TtsEnabled = true,
            TtsCallCount = 3,
            TtsFailureCount = 1,
            TtsSuccessEvidenceCount = 2,
            AsrEnabled = true,
            AsrCallCount = 5,
            AsrFailureCount = 2,
            AsrSuccessEvidenceCount = 3,
            AsrEndpointingReceiptCount = 4,
            AsrBargeInCount = 1,
            AsrEndpointingReviewCount = 2,
            AsrConfidenceReceiptCount = 2,
            AsrConfidenceReviewCount = 1,
            AsrTimingReceiptCount = 2,
            AsrTimingReviewCount = 1,
            AsrQualityReceiptCount = 2,
            AsrQualityReviewCount = 1,
            AsrUpstreamRequestIdReceiptCount = 2,
            AsrUpstreamProcessingReceiptCount = 2,
            AsrUpstreamPhaseTimingReceiptCount = 1,
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 6,
            PrivacyRedactionRedactedFileCount = 1,
            PrivacyRedactionRuleHits = ["windows-user-path"],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 6,
            PublicationScanViolations = [],
            IncludedFiles = ["health.json", "release-readiness.json", "bridge-proof.json", "inference-performance.json", "latest-smoke.json", "latest-native-proof.json", "native-hud.lua", "proof-bundle.json"],
            MissingOptionalFiles = [],
            CurrentBlockers = ["The product name itself remains scope-coupled to a third-party game."],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestProofBundleEvidencePath,
            JsonSerializer.Serialize(expected));
        await File.WriteAllBytesAsync(options.LatestProofBundleArchivePath, [1, 2, 3, 4]);

        using HttpResponseMessage invalidResponse = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? invalidPayload = await invalidResponse.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(invalidResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(invalidPayload, Is.Not.Null);
        Assert.That(invalidPayload!.ProofBundleEvidence.Status, Is.EqualTo("invalid"));
        Assert.That(invalidPayload.ProofBundleEvidence.Summary, Does.Contain("safe zip archive"));

        await WriteProofBundleArchiveAsync(options.LatestProofBundleArchivePath, expected);

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.ProofBundleEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(payload.ProofBundleEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(payload.ProofBundleEvidence.FreshUntilUtc, Is.GreaterThan(expected.CapturedAtUtc));
        Assert.That(payload.ProofBundleEvidence.ArchivePath, Does.EndWith("latest-proof-bundle.zip"));
        Assert.That(payload.ProofBundleEvidence.BridgeProofStatus, Is.EqualTo("delivery_proven"));
        Assert.That(payload.ProofBundleEvidence.SmokeEvidenceStatus, Is.EqualTo("recorded"));
        Assert.That(payload.ProofBundleEvidence.NativeProofEvidenceStatus, Is.EqualTo("proven"));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceStatus, Is.EqualTo("healthy"));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceSampleCount, Is.EqualTo(4));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceLaneCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceAlertingLaneCount, Is.Zero);
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceLatestReceiptLaneCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceTokenReceiptLaneCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceFinishReasonReceiptLaneCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceUpstreamRequestIdReceiptLaneCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceUpstreamProcessingReceiptLaneCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.InferencePerformancePhaseTimingReceiptLaneCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceUsageDetailReceiptLaneCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceTotalTokens, Is.EqualTo(384));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceCachedPromptTokens, Is.EqualTo(192));
        Assert.That(payload.ProofBundleEvidence.InferencePerformanceCompletionReasoningTokens, Is.EqualTo(24));
        Assert.That(payload.ProofBundleEvidence.TtsEnabled, Is.True);
        Assert.That(payload.ProofBundleEvidence.TtsCallCount, Is.EqualTo(3));
        Assert.That(payload.ProofBundleEvidence.TtsFailureCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.TtsSuccessEvidenceCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrEnabled, Is.True);
        Assert.That(payload.ProofBundleEvidence.AsrCallCount, Is.EqualTo(5));
        Assert.That(payload.ProofBundleEvidence.AsrFailureCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrSuccessEvidenceCount, Is.EqualTo(3));
        Assert.That(payload.ProofBundleEvidence.AsrEndpointingReceiptCount, Is.EqualTo(4));
        Assert.That(payload.ProofBundleEvidence.AsrBargeInCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.AsrEndpointingReviewCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrConfidenceReceiptCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrConfidenceReviewCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.AsrTimingReceiptCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrTimingReviewCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.AsrQualityReceiptCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrQualityReviewCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.AsrUpstreamRequestIdReceiptCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrUpstreamProcessingReceiptCount, Is.EqualTo(2));
        Assert.That(payload.ProofBundleEvidence.AsrUpstreamPhaseTimingReceiptCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.PrivacyRedactionApplied, Is.True);
        Assert.That(payload.ProofBundleEvidence.PrivacyRedactionCheckedFileCount, Is.EqualTo(6));
        Assert.That(payload.ProofBundleEvidence.PrivacyRedactionRedactedFileCount, Is.EqualTo(1));
        Assert.That(payload.ProofBundleEvidence.PrivacyRedactionRuleHits, Contains.Item("windows-user-path"));
        Assert.That(payload.ProofBundleEvidence.PublicationScanPassed, Is.True);
        Assert.That(payload.ProofBundleEvidence.PublicationScanCheckedFileCount, Is.EqualTo(6));
        Assert.That(payload.ProofBundleEvidence.PublicationScanViolations, Is.Empty);
        Assert.That(payload.ProofBundleEvidence.IncludedFiles, Contains.Item("proof-bundle.json"));
        Assert.That(payload.Publication.NextRecommendedPass, Does.Not.Contain("scripts/export-release-proof-bundle.ps1"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenSupportBundleArtifactExists_ReturnsSupportBundleEvidenceSummary()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        ReleaseSupportBundleEvidenceSnapshot expected = new()
        {
            Status = "recorded",
            Summary = "PalLLM support bundle captured the latest launch, bridge, and release-readiness evidence.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestSupportBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.SupportEvidenceHistoryDir, "support-bundle-20260423-001.json"),
            ArchivePath = options.LatestSupportBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.SupportEvidenceHistoryDir, "support-bundle-20260423-001.zip"),
            BaseUrl = "http://localhost:5088",
            RuntimeRoot = options.RuntimeRoot,
            LaunchEvidenceStatus = "recorded",
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            ProofBundleEvidenceStatus = "recorded",
            PackageVerificationEvidenceStatus = "verified",
            FullAuditEvidenceStatus = "passed",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 7,
            PrivacyRedactionRedactedFileCount = 2,
            PrivacyRedactionRuleHits = ["api-key-field", "windows-user-path"],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 7,
            PublicationScanViolations = [],
            IncludedFiles = ["health.json", "release-readiness.json", "bridge-proof.json", "latest-player-launch.json", "latest-proof-bundle.zip", "support-bundle.json"],
            MissingOptionalFiles = [],
            CurrentBlockers = ["The product name itself remains scope-coupled to a third-party game."],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
        };

        Directory.CreateDirectory(options.SupportEvidenceDir);
        Directory.CreateDirectory(options.SupportEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestSupportBundleEvidencePath,
            JsonSerializer.Serialize(expected));
        await WriteSupportBundleArchiveAsync(options.LatestSupportBundleArchivePath, expected);

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.SupportBundleEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(payload.SupportBundleEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(payload.SupportBundleEvidence.FreshUntilUtc, Is.GreaterThan(expected.CapturedAtUtc));
        Assert.That(payload.SupportBundleEvidence.ArchivePath, Does.EndWith("latest-support-bundle.zip"));
        Assert.That(payload.SupportBundleEvidence.LaunchEvidenceStatus, Is.EqualTo("recorded"));
        Assert.That(payload.SupportBundleEvidence.ProofBundleEvidenceStatus, Is.EqualTo("recorded"));
        Assert.That(payload.SupportBundleEvidence.PackageVerificationEvidenceStatus, Is.EqualTo("verified"));
        Assert.That(payload.SupportBundleEvidence.FullAuditEvidenceStatus, Is.EqualTo("passed"));
        Assert.That(payload.SupportBundleEvidence.PrivacyRedactionApplied, Is.True);
        Assert.That(payload.SupportBundleEvidence.PrivacyRedactionCheckedFileCount, Is.EqualTo(7));
        Assert.That(payload.SupportBundleEvidence.PrivacyRedactionRedactedFileCount, Is.EqualTo(2));
        Assert.That(payload.SupportBundleEvidence.PrivacyRedactionRuleHits, Contains.Item("api-key-field"));
        Assert.That(payload.SupportBundleEvidence.PublicationScanPassed, Is.True);
        Assert.That(payload.SupportBundleEvidence.PublicationScanCheckedFileCount, Is.EqualTo(7));
        Assert.That(payload.SupportBundleEvidence.PublicationScanViolations, Is.Empty);
        Assert.That(payload.SupportBundleEvidence.IncludedFiles, Contains.Item("support-bundle.json"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenProofBundleArchiveHasUnsafeEntry_ReturnsInvalid()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        ReleaseProofBundleEvidenceSnapshot expected = new()
        {
            Status = "recorded",
            Summary = "Proof bundle has a manifest-backed archive.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestProofBundleEvidencePath,
            ArchivePath = options.LatestProofBundleArchivePath,
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 1,
            PrivacyRedactionRuleHits = [],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 1,
            PublicationScanViolations = [],
            IncludedFiles = ["proof-bundle.json"],
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        await File.WriteAllTextAsync(
            options.LatestProofBundleEvidencePath,
            JsonSerializer.Serialize(expected));
        await WriteProofBundleArchiveAsync(
            options.LatestProofBundleArchivePath,
            expected,
            ["../outside.txt"]);

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.ProofBundleEvidence.Status, Is.EqualTo("invalid"));
        Assert.That(payload.ProofBundleEvidence.Summary, Does.Contain("unsafe entry names"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenSupportBundleManifestListsUnsafeEntry_ReturnsInvalid()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        ReleaseSupportBundleEvidenceSnapshot expected = new()
        {
            Status = "recorded",
            Summary = "Support bundle has a manifest-backed archive.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestSupportBundleEvidencePath,
            ArchivePath = options.LatestSupportBundleArchivePath,
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 1,
            PrivacyRedactionRuleHits = [],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 1,
            PublicationScanViolations = [],
            IncludedFiles = ["/absolute-support-log.json", "support-bundle.json"],
        };

        Directory.CreateDirectory(options.SupportEvidenceDir);
        await File.WriteAllTextAsync(
            options.LatestSupportBundleEvidencePath,
            JsonSerializer.Serialize(expected));
        await WriteBundleArchiveWithManifestOnlyAsync(
            options.LatestSupportBundleArchivePath,
            "support-bundle.json",
            JsonSerializer.Serialize(expected));

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.SupportBundleEvidence.Status, Is.EqualTo("invalid"));
        Assert.That(payload.SupportBundleEvidence.Summary, Does.Contain("manifest lists unsafe entry names"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenPackageVerificationArtifactExists_ReturnsPackageVerificationSummary()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        ReleasePackageVerificationEvidenceSnapshot expected = new()
        {
            Status = "verified",
            Summary = "PalLLM release package verified successfully against RELEASE_PACKAGE_MANIFEST.json.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestPackageVerificationEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "package-verification-20260422-001.json"),
            PackagePath = @"D:\Coding\PalLLM\artifacts\packaging\PalLLM-20260422T220000Z.zip",
            PackageKind = "zip_archive",
            ReleaseName = "PalLLM-20260422T220000Z",
            ManifestRelativePath = "RELEASE_PACKAGE_MANIFEST.json",
            ManifestSchemaVersion = 1,
            PackageSha256 = "ABCDEF1234567890",
            VerifiedFromArchive = true,
            IncludesSidecarPublish = true,
            SelfContainedSidecar = true,
            RequiredFilesPresent = true,
            CheckedFileCount = 24,
            MissingRequiredFiles = [],
            UnexpectedFiles = [],
            MismatchedFiles = [],
            CurrentBlockers = [],
            ReadyEvidence = ["Package manifest parsed successfully.", "Validated 24 manifest-declared files."],
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestPackageVerificationEvidencePath,
            JsonSerializer.Serialize(expected));

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.PackageVerificationEvidence.Status, Is.EqualTo("verified"));
        Assert.That(payload.PackageVerificationEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(payload.PackageVerificationEvidence.FreshUntilUtc, Is.GreaterThan(expected.CapturedAtUtc));
        Assert.That(payload.PackageVerificationEvidence.PackageKind, Is.EqualTo("zip_archive"));
        Assert.That(payload.PackageVerificationEvidence.PackagePath, Does.EndWith(".zip"));
        Assert.That(payload.PackageVerificationEvidence.IncludesSidecarPublish, Is.True);
        Assert.That(payload.PackageVerificationEvidence.SelfContainedSidecar, Is.True);
        Assert.That(payload.PackageVerificationEvidence.RequiredFilesPresent, Is.True);
        Assert.That(payload.PackageVerificationEvidence.CheckedFileCount, Is.EqualTo(24));
        Assert.That(payload.PackageVerificationEvidence.ReadyEvidence, Contains.Item("Package manifest parsed successfully."));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenFullAuditArtifactExists_ReturnsFullAuditEvidenceSummary()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        string auditRoot = Path.Combine(options.ReleaseEvidenceHistoryDir, "audit-20260422-220000");
        string stepsDirectoryPath = Path.Combine(auditRoot, "steps");
        string resultsPath = Path.Combine(auditRoot, "RESULTS.md");
        Directory.CreateDirectory(stepsDirectoryPath);
        await WriteFullAuditResultsAsync(resultsPath, "PASS");
        await WriteFullAuditStepLogsAsync(stepsDirectoryPath, 14);

        ReleaseFullAuditEvidenceSnapshot expected = new()
        {
            Status = "passed",
            Summary = "PalLLM full audit passed and recorded the current build, test, drift, and packaging posture.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestFullAuditEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "full-audit-20260422-220000.json"),
            AuditRoot = auditRoot,
            ResultsPath = resultsPath,
            StepsDirectoryPath = stepsDirectoryPath,
            TestsEnabled = true,
            CoverageEnabled = false,
            SbomEnabled = false,
            PackagingEnabled = true,
            TotalStepCount = 14,
            PassedStepCount = 14,
            FailedStepCount = 0,
            StepNames = ["Build_Release", "Tests", "Release_Package_verification"],
            FailedSteps = [],
            CurrentBlockers = [],
            ReadyEvidence = ["Build_Release passed.", "Tests passed.", "All recorded full-audit steps passed."],
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestFullAuditEvidencePath,
            JsonSerializer.Serialize(expected));

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.FullAuditEvidence.Status, Is.EqualTo("passed"));
        Assert.That(payload.FullAuditEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(payload.FullAuditEvidence.FreshUntilUtc, Is.GreaterThan(expected.CapturedAtUtc));
        Assert.That(payload.FullAuditEvidence.AuditRoot, Is.EqualTo(auditRoot));
        Assert.That(payload.FullAuditEvidence.ResultsPath, Is.EqualTo(resultsPath));
        Assert.That(payload.FullAuditEvidence.StepsDirectoryPath, Is.EqualTo(stepsDirectoryPath));
        Assert.That(payload.FullAuditEvidence.TestsEnabled, Is.True);
        Assert.That(payload.FullAuditEvidence.PackagingEnabled, Is.True);
        Assert.That(payload.FullAuditEvidence.TotalStepCount, Is.EqualTo(14));
        Assert.That(payload.FullAuditEvidence.PassedStepCount, Is.EqualTo(14));
        Assert.That(payload.FullAuditEvidence.FailedStepCount, Is.EqualTo(0));
        Assert.That(payload.FullAuditEvidence.StepNames, Contains.Item("Build_Release"));
        Assert.That(payload.FullAuditEvidence.ReadyEvidence, Contains.Item("All recorded full-audit steps passed."));

        ReleaseFullAuditEvidenceSnapshot contradictory = new()
        {
            Status = "passed",
            Summary = "Contradictory audit evidence should not be trusted.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestFullAuditEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "full-audit-contradictory.json"),
            AuditRoot = auditRoot,
            ResultsPath = resultsPath,
            StepsDirectoryPath = stepsDirectoryPath,
            TestsEnabled = true,
            CoverageEnabled = false,
            SbomEnabled = false,
            PackagingEnabled = true,
            TotalStepCount = 14,
            PassedStepCount = 13,
            FailedStepCount = 1,
            StepNames = ["Build_Release", "Tests"],
            FailedSteps = [],
            CurrentBlockers = [],
            ReadyEvidence = ["All recorded full-audit steps passed."],
        };
        await File.WriteAllTextAsync(options.LatestFullAuditEvidencePath, JsonSerializer.Serialize(contradictory));

        using HttpResponseMessage contradictoryResponse = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? contradictoryPayload =
            await contradictoryResponse.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(contradictoryResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(contradictoryPayload, Is.Not.Null);
        Assert.That(contradictoryPayload!.FullAuditEvidence.Status, Is.EqualTo("invalid"));
        Assert.That(contradictoryPayload.FullAuditEvidence.Summary, Does.Contain("claims PASS"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenFullAuditIsStale_AvisesRerunningFullAuditAfterProofSteps()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);

        ReleaseSmokeEvidenceSnapshot smoke = new()
        {
            Status = "recorded",
            Summary = "Fresh smoke evidence is available.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestSmokeEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "smoke-fresh.json"),
            BridgeProofStatus = "delivery_proven",
            BridgeLoopStatus = "closed",
            LoopClosed = true,
            VisibleDeliveryConfirmed = true,
        };
        await File.WriteAllTextAsync(options.LatestSmokeEvidencePath, JsonSerializer.Serialize(smoke));

        ReleaseNativeProofEvidenceSnapshot nativeProof = new()
        {
            Status = "proven",
            Summary = "Fresh native proof is available.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-fresh.json"),
            BridgeProofStatus = "delivery_proven",
            LiveDeliveryProven = true,
            NativeHudBindReady = true,
            DeliverySurface = "native_hud",
            LoopStatus = "closed",
            VisibleDeliveryConfirmed = true,
        };
        await File.WriteAllTextAsync(options.LatestNativeProofEvidencePath, JsonSerializer.Serialize(nativeProof));

        ReleaseProofBundleEvidenceSnapshot proofBundle = new()
        {
            Status = "recorded",
            Summary = "Fresh proof bundle is available.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestProofBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-fresh.json"),
            ArchivePath = options.LatestProofBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-fresh.zip"),
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 3,
            PrivacyRedactionRedactedFileCount = 0,
            PrivacyRedactionRuleHits = [],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 3,
            PublicationScanViolations = [],
        };
        await File.WriteAllTextAsync(options.LatestProofBundleEvidencePath, JsonSerializer.Serialize(proofBundle));
        await WriteProofBundleArchiveAsync(options.LatestProofBundleArchivePath, proofBundle);

        ReleasePackageVerificationEvidenceSnapshot packageVerification = new()
        {
            Status = "verified",
            Summary = "Fresh package verification is available.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestPackageVerificationEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "package-verification-fresh.json"),
            PackagePath = @"D:\Coding\PalLLM\artifacts\packaging\PalLLM-latest.zip",
            PackageKind = "zip_archive",
            ReleaseName = "PalLLM-latest",
            ManifestRelativePath = "RELEASE_PACKAGE_MANIFEST.json",
            ManifestSchemaVersion = 1,
            VerifiedFromArchive = true,
            IncludesSidecarPublish = true,
            SelfContainedSidecar = true,
            RequiredFilesPresent = true,
            CheckedFileCount = 24,
        };
        await File.WriteAllTextAsync(options.LatestPackageVerificationEvidencePath, JsonSerializer.Serialize(packageVerification));

        string auditRoot = Path.Combine(options.ReleaseEvidenceHistoryDir, "audit-stale");
        string stepsDirectoryPath = Path.Combine(auditRoot, "steps");
        string resultsPath = Path.Combine(auditRoot, "RESULTS.md");
        Directory.CreateDirectory(stepsDirectoryPath);
        await WriteFullAuditResultsAsync(resultsPath, "PASS");
        await WriteFullAuditStepLogsAsync(stepsDirectoryPath, 14);

        ReleaseFullAuditEvidenceSnapshot staleAudit = new()
        {
            Status = "passed",
            Summary = "PalLLM full audit passed previously.",
            CapturedAtUtc = DateTimeOffset.UtcNow.AddHours(-30),
            ArtifactPath = options.LatestFullAuditEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "full-audit-stale.json"),
            AuditRoot = auditRoot,
            ResultsPath = resultsPath,
            StepsDirectoryPath = stepsDirectoryPath,
            TestsEnabled = true,
            CoverageEnabled = false,
            SbomEnabled = false,
            PackagingEnabled = true,
            TotalStepCount = 14,
            PassedStepCount = 14,
            FailedStepCount = 0,
            StepNames = ["Build_Release", "Tests"],
            FailedSteps = [],
            CurrentBlockers = [],
            ReadyEvidence = ["All recorded full-audit steps passed."],
        };
        await File.WriteAllTextAsync(options.LatestFullAuditEvidencePath, JsonSerializer.Serialize(staleAudit));

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.FullAuditEvidence.Status, Is.EqualTo("passed"));
        Assert.That(payload.FullAuditEvidence.FreshnessStatus, Is.EqualTo("stale"));
        Assert.That(payload.Publication.NextRecommendedPass, Does.Contain("scripts/run_full_audit.ps1"));
        Assert.That(payload.Publication.NextRecommendedCommand, Does.Contain("scripts/run_full_audit.ps1"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_RefreshesAfterSmokeArtifactChanges()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? firstPayload = await first.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();
        string? firstEtag = first.Headers.ETag?.ToString();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(firstPayload, Is.Not.Null);
        Assert.That(firstPayload!.SmokeEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.NativeProofEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.ProofBundleEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.SupportBundleEvidence.Status, Is.EqualTo("missing"));
        Assert.That(firstPayload.PackageVerificationEvidence.Status, Is.EqualTo("missing"));

        ReleaseSmokeEvidenceSnapshot updated = new()
        {
            Status = "recorded",
            Summary = "Fresh smoke evidence was captured after the first readiness read.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestSmokeEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "smoke-20260422-002.json"),
            BaseUrl = "http://localhost:5088",
            RequestId = "release-smoke-002",
            ResponsePath = "Responses\\release-smoke-002.txt",
            BridgeProofStatus = "delivery_proven",
            BridgeLoopStatus = "closed",
            LoopClosed = true,
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = false,
            NativeHudBindReady = false,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = [],
            DeliverySurface = "smoke_replay",
            ActionType = string.Empty,
            UsedFallback = true,
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestSmokeEvidencePath,
            JsonSerializer.Serialize(updated));

        ReleaseNativeProofEvidenceSnapshot nativeProof = new()
        {
            Status = "proven",
            Summary = "Live native proof arrived after the first readiness read.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-20260422-002.json"),
            BaseUrl = "http://localhost:5088",
            BridgeProofStatus = "delivery_proven",
            ActiveRequestId = "native-proof-002",
            LiveDeliveryProven = true,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "native_hud",
            LoopStatus = "closed",
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = true,
            AppliedHudRecommendation = true,
            AppliedHudRecommendationPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            RecommendedNextStep = "Archive the proven native HUD bind.",
            CurrentBlockers = [],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
        };

        await File.WriteAllTextAsync(
            options.LatestNativeProofEvidencePath,
            JsonSerializer.Serialize(nativeProof));

        ReleaseProofBundleEvidenceSnapshot proofBundle = new()
        {
            Status = "recorded",
            Summary = "The current runtime proof surfaces were archived together after the second readiness read.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestProofBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-20260422-002.json"),
            ArchivePath = options.LatestProofBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-20260422-002.zip"),
            BaseUrl = "http://localhost:5088",
            ReleasePublicationStatus = "caution",
            BridgeProofStatus = "delivery_proven",
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 5,
            PrivacyRedactionRedactedFileCount = 1,
            PrivacyRedactionRuleHits = ["windows-user-path"],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 5,
            PublicationScanViolations = [],
            IncludedFiles = ["health.json", "release-readiness.json", "bridge-proof.json", "latest-smoke.json", "latest-native-proof.json", "proof-bundle.json"],
            MissingOptionalFiles = [],
            CurrentBlockers = ["The product name itself remains scope-coupled to a third-party game."],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
        };

        await File.WriteAllTextAsync(
            options.LatestProofBundleEvidencePath,
            JsonSerializer.Serialize(proofBundle));
        await WriteProofBundleArchiveAsync(options.LatestProofBundleArchivePath, proofBundle);

        ReleaseSupportBundleEvidenceSnapshot supportBundle = new()
        {
            Status = "recorded",
            Summary = "Portable support evidence was captured after the second readiness read.",
            CapturedAtUtc = DateTimeOffset.UtcNow,
            ArtifactPath = options.LatestSupportBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.SupportEvidenceHistoryDir, "support-bundle-20260422-002.json"),
            ArchivePath = options.LatestSupportBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.SupportEvidenceHistoryDir, "support-bundle-20260422-002.zip"),
            BaseUrl = "http://localhost:5088",
            RuntimeRoot = options.RuntimeRoot,
            LaunchEvidenceStatus = "recorded",
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            ProofBundleEvidenceStatus = "recorded",
            PackageVerificationEvidenceStatus = "missing",
            FullAuditEvidenceStatus = "missing",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 5,
            PrivacyRedactionRedactedFileCount = 1,
            PrivacyRedactionRuleHits = ["windows-user-path"],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 5,
            PublicationScanViolations = [],
            IncludedFiles = ["health.json", "release-readiness.json", "bridge-proof.json", "latest-player-launch.json", "support-bundle.json"],
            MissingOptionalFiles = ["latest-full-audit.json"],
            CurrentBlockers = ["Latest player launch evidence is missing."],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
        };

        Directory.CreateDirectory(options.SupportEvidenceDir);
        Directory.CreateDirectory(options.SupportEvidenceHistoryDir);
        await File.WriteAllTextAsync(
            options.LatestSupportBundleEvidencePath,
            JsonSerializer.Serialize(supportBundle));
        await WriteSupportBundleArchiveAsync(options.LatestSupportBundleArchivePath, supportBundle);

        using HttpResponseMessage second = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? secondPayload = await second.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();
        string? secondEtag = second.Headers.ETag?.ToString();

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(secondPayload, Is.Not.Null);
        Assert.That(secondPayload!.SmokeEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(secondPayload.SmokeEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(secondPayload.SmokeEvidence.RequestId, Is.EqualTo("release-smoke-002"));
        Assert.That(secondPayload.SmokeEvidence.UsedFallback, Is.True);
        Assert.That(secondPayload.NativeProofEvidence.Status, Is.EqualTo("proven"));
        Assert.That(secondPayload.NativeProofEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(secondPayload.NativeProofEvidence.ActiveRequestId, Is.EqualTo("native-proof-002"));
        Assert.That(secondPayload.ProofBundleEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(secondPayload.ProofBundleEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(secondPayload.ProofBundleEvidence.ArchivePath, Does.EndWith("latest-proof-bundle.zip"));
        Assert.That(secondPayload.SupportBundleEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(secondPayload.SupportBundleEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(secondPayload.SupportBundleEvidence.ArchivePath, Does.EndWith("latest-support-bundle.zip"));
        Assert.That(secondPayload.PackageVerificationEvidence.Status, Is.EqualTo("missing"));
        Assert.That(secondEtag, Is.Not.EqualTo(firstEtag),
            "Release-readiness should reflect the latest smoke/native-proof artifacts immediately instead of serving a stale server-cached snapshot.");
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenEvidenceIsStale_FlagsFreshnessAndRequestsRefresh()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        DateTimeOffset staleCapture = DateTimeOffset.UtcNow.AddHours(-(options.ReleaseEvidenceFreshnessHours + 2));

        ReleaseSmokeEvidenceSnapshot smoke = new()
        {
            Status = "recorded",
            Summary = "Palworld smoke loop closed on an older build session.",
            CapturedAtUtc = staleCapture,
            ArtifactPath = options.LatestSmokeEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "smoke-stale.json"),
            BaseUrl = "http://localhost:5088",
            RequestId = "release-smoke-stale",
            ResponsePath = "Responses\\release-smoke-stale.txt",
            BridgeProofStatus = "delivery_proven",
            BridgeLoopStatus = "closed",
            LoopClosed = true,
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = true,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "native_hud",
            ActionType = "waypoint_suggest",
            UsedFallback = false,
        };
        ReleaseNativeProofEvidenceSnapshot nativeProof = new()
        {
            Status = "proven",
            Summary = "Live Palworld native HUD delivery was proven before the latest candidate build.",
            CapturedAtUtc = staleCapture,
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-stale.json"),
            BaseUrl = "http://localhost:5088",
            BridgeProofStatus = "delivery_proven",
            ActiveRequestId = "native-proof-stale",
            LiveDeliveryProven = true,
            NativeHudBindReady = true,
            RecommendedHudTarget = "/Game/UI/WBP_PalHudOverlay",
            ConfiguredHudTargets = ["/Game/UI/WBP_PalHudOverlay"],
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            DeliverySurface = "native_hud",
            LoopStatus = "closed",
            VisibleDeliveryConfirmed = true,
            ActionFeedbackObserved = true,
            AppliedHudRecommendation = true,
            AppliedHudRecommendationPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            RecommendedNextStep = "Refresh this proof before packaging.",
            CurrentBlockers = [],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
        };
        ReleaseProofBundleEvidenceSnapshot proofBundle = new()
        {
            Status = "recorded",
            Summary = "The Palworld release proof bundle was exported before the latest candidate build.",
            CapturedAtUtc = staleCapture,
            ArtifactPath = options.LatestProofBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-stale.json"),
            ArchivePath = options.LatestProofBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-stale.zip"),
            BaseUrl = "http://localhost:5088",
            ReleasePublicationStatus = "caution",
            BridgeProofStatus = "delivery_proven",
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            NativeHudConfigSource = "mod_override_file",
            NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 5,
            PrivacyRedactionRedactedFileCount = 1,
            PrivacyRedactionRuleHits = ["windows-user-path"],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 5,
            PublicationScanViolations = [],
            IncludedFiles = ["release-readiness.json", "bridge-proof.json", "latest-smoke.json", "latest-native-proof.json", "proof-bundle.json"],
            MissingOptionalFiles = [],
            CurrentBlockers = ["The product name itself remains scope-coupled to a third-party game."],
            ReadyEvidence = ["HUD target bound", "Delivery rendered"],
        };

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        await File.WriteAllTextAsync(options.LatestSmokeEvidencePath, JsonSerializer.Serialize(smoke));
        await File.WriteAllTextAsync(options.LatestNativeProofEvidencePath, JsonSerializer.Serialize(nativeProof));
        await File.WriteAllTextAsync(options.LatestProofBundleEvidencePath, JsonSerializer.Serialize(proofBundle));
        await WriteProofBundleArchiveAsync(options.LatestProofBundleArchivePath, proofBundle);

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.SmokeEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(payload.SmokeEvidence.FreshnessStatus, Is.EqualTo("stale"));
        Assert.That(payload.NativeProofEvidence.Status, Is.EqualTo("proven"));
        Assert.That(payload.NativeProofEvidence.FreshnessStatus, Is.EqualTo("stale"));
        Assert.That(payload.ProofBundleEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(payload.ProofBundleEvidence.FreshnessStatus, Is.EqualTo("stale"));
        Assert.That(payload.PackageVerificationEvidence.Status, Is.EqualTo("missing"));
        Assert.That(payload.Publication.NextRecommendedPass, Does.Contain("scripts/run-native-proof.ps1"));
        Assert.That(payload.Publication.NextRecommendedCommand, Does.Contain("scripts/run-native-proof.ps1"));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenSupportBundleIsStale_RequestsSupportBundleRefreshAfterReleaseProof()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        DateTimeOffset freshCapture = DateTimeOffset.UtcNow;
        DateTimeOffset staleCapture = DateTimeOffset.UtcNow.AddHours(-(options.ReleaseEvidenceFreshnessHours + 2));

        Directory.CreateDirectory(options.ReleaseEvidenceDir);
        Directory.CreateDirectory(options.ReleaseEvidenceHistoryDir);
        Directory.CreateDirectory(options.SupportEvidenceDir);
        Directory.CreateDirectory(options.SupportEvidenceHistoryDir);

        await File.WriteAllTextAsync(options.LatestSmokeEvidencePath, JsonSerializer.Serialize(new ReleaseSmokeEvidenceSnapshot
        {
            Status = "recorded",
            Summary = "Fresh smoke evidence is available.",
            CapturedAtUtc = freshCapture,
            ArtifactPath = options.LatestSmokeEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "smoke-fresh-support.json"),
            BridgeProofStatus = "delivery_proven",
            BridgeLoopStatus = "closed",
            LoopClosed = true,
            VisibleDeliveryConfirmed = true,
        }));

        await File.WriteAllTextAsync(options.LatestNativeProofEvidencePath, JsonSerializer.Serialize(new ReleaseNativeProofEvidenceSnapshot
        {
            Status = "proven",
            Summary = "Fresh native proof is available.",
            CapturedAtUtc = freshCapture,
            ArtifactPath = options.LatestNativeProofEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "native-proof-fresh-support.json"),
            BridgeProofStatus = "delivery_proven",
            LiveDeliveryProven = true,
            NativeHudBindReady = true,
            DeliverySurface = "native_hud",
            LoopStatus = "closed",
            VisibleDeliveryConfirmed = true,
        }));

        await File.WriteAllTextAsync(options.LatestProofBundleEvidencePath, JsonSerializer.Serialize(new ReleaseProofBundleEvidenceSnapshot
        {
            Status = "recorded",
            Summary = "Fresh proof bundle is available.",
            CapturedAtUtc = freshCapture,
            ArtifactPath = options.LatestProofBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-fresh-support.json"),
            ArchivePath = options.LatestProofBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.ReleaseEvidenceHistoryDir, "proof-bundle-fresh-support.zip"),
            BridgeProofStatus = "delivery_proven",
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 3,
            PrivacyRedactionRedactedFileCount = 0,
            PrivacyRedactionRuleHits = [],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 3,
            PublicationScanViolations = [],
        }));
        ReleaseProofBundleEvidenceSnapshot freshProofBundle = JsonSerializer.Deserialize<ReleaseProofBundleEvidenceSnapshot>(
            await File.ReadAllTextAsync(options.LatestProofBundleEvidencePath))!;
        await WriteProofBundleArchiveAsync(options.LatestProofBundleArchivePath, freshProofBundle);

        string auditRoot = Path.Combine(options.ReleaseEvidenceHistoryDir, "audit-support-fresh");
        string stepsDirectoryPath = Path.Combine(auditRoot, "steps");
        string resultsPath = Path.Combine(auditRoot, "RESULTS.md");
        Directory.CreateDirectory(stepsDirectoryPath);
        await WriteFullAuditResultsAsync(resultsPath, "PASS");
        await WriteFullAuditStepLogsAsync(stepsDirectoryPath, 3);
        await File.WriteAllTextAsync(options.LatestFullAuditEvidencePath, JsonSerializer.Serialize(new ReleaseFullAuditEvidenceSnapshot
        {
            Status = "passed",
            Summary = "Fresh full audit is available.",
            CapturedAtUtc = freshCapture,
            ArtifactPath = options.LatestFullAuditEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "full-audit-fresh-support.json"),
            AuditRoot = auditRoot,
            ResultsPath = resultsPath,
            StepsDirectoryPath = stepsDirectoryPath,
            TotalStepCount = 3,
            PassedStepCount = 3,
            FailedStepCount = 0,
        }));

        await File.WriteAllTextAsync(options.LatestPackageVerificationEvidencePath, JsonSerializer.Serialize(new ReleasePackageVerificationEvidenceSnapshot
        {
            Status = "verified",
            Summary = "Fresh package verification is available.",
            CapturedAtUtc = freshCapture,
            ArtifactPath = options.LatestPackageVerificationEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "package-verify-fresh-support.json"),
            PackagePath = @"D:\Coding\PalLLM\artifacts\packaging\PalLLM-support.zip",
            PackageKind = "zip_archive",
            RequiredFilesPresent = true,
            CheckedFileCount = 12,
        }));

        string packagingRoot = Path.Combine(options.ReleaseEvidenceHistoryDir, "packaging-integrity");
        Directory.CreateDirectory(packagingRoot);
        string checksumsJsonPath = Path.Combine(packagingRoot, "checksums.json");
        string sha256SumsPath = Path.Combine(packagingRoot, "SHA256SUMS");
        string sha512SumsPath = Path.Combine(packagingRoot, "SHA512SUMS");
        await File.WriteAllTextAsync(checksumsJsonPath, "{\"Artifacts\":[]}");
        await File.WriteAllTextAsync(sha256SumsPath, "0123456789abcdef  PalLLM-support.zip\n");
        await File.WriteAllTextAsync(sha512SumsPath, "0123456789abcdef  PalLLM-support.zip\n");
        await File.WriteAllTextAsync(options.LatestArtifactIntegrityEvidencePath, JsonSerializer.Serialize(new ReleaseArtifactIntegrityEvidenceSnapshot
        {
            Status = "recorded",
            Summary = "Fresh artifact integrity evidence is available.",
            CapturedAtUtc = freshCapture,
            ArtifactPath = options.LatestArtifactIntegrityEvidencePath,
            HistoryArtifactPath = Path.Combine(options.ReleaseEvidenceHistoryDir, "artifact-integrity-fresh-support.json"),
            PackagingRoot = packagingRoot,
            ChecksumsJsonPath = checksumsJsonPath,
            Sha256SumsPath = sha256SumsPath,
            Sha512SumsPath = sha512SumsPath,
            ArtifactCount = 1,
            ChecksumsJsonPresent = true,
            Sha256SumsPresent = true,
            Sha512SumsPresent = true,
            DetachedSignaturePresent = false,
            ReadyEvidence = ["SHA256SUMS covers 1 artifact."],
        }));

        await File.WriteAllTextAsync(options.LatestSupportBundleEvidencePath, JsonSerializer.Serialize(new ReleaseSupportBundleEvidenceSnapshot
        {
            Status = "recorded",
            Summary = "Support bundle was captured before the latest candidate build.",
            CapturedAtUtc = staleCapture,
            ArtifactPath = options.LatestSupportBundleEvidencePath,
            HistoryArtifactPath = Path.Combine(options.SupportEvidenceHistoryDir, "support-bundle-stale.json"),
            ArchivePath = options.LatestSupportBundleArchivePath,
            HistoryArchivePath = Path.Combine(options.SupportEvidenceHistoryDir, "support-bundle-stale.zip"),
            BaseUrl = "http://localhost:5088",
            RuntimeRoot = options.RuntimeRoot,
            LaunchEvidenceStatus = "recorded",
            SmokeEvidenceStatus = "recorded",
            NativeProofEvidenceStatus = "proven",
            ProofBundleEvidenceStatus = "recorded",
            PackageVerificationEvidenceStatus = "verified",
            FullAuditEvidenceStatus = "passed",
            PrivacyRedactionApplied = true,
            PrivacyRedactionCheckedFileCount = 1,
            PrivacyRedactionRedactedFileCount = 0,
            PrivacyRedactionRuleHits = [],
            PublicationScanPassed = true,
            PublicationScanCheckedFileCount = 1,
            PublicationScanViolations = [],
            IncludedFiles = ["support-bundle.json"],
        }));
        ReleaseSupportBundleEvidenceSnapshot staleSupportBundle = JsonSerializer.Deserialize<ReleaseSupportBundleEvidenceSnapshot>(
            await File.ReadAllTextAsync(options.LatestSupportBundleEvidencePath))!;
        await WriteSupportBundleArchiveAsync(options.LatestSupportBundleArchivePath, staleSupportBundle);

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/release/readiness");
        ReleaseReadinessSnapshot? payload = await response.Content.ReadFromJsonAsync<ReleaseReadinessSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.ArtifactIntegrityEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(payload.ArtifactIntegrityEvidence.FreshnessStatus, Is.EqualTo("fresh"));
        Assert.That(payload.ArtifactIntegrityEvidence.Sha256SumsPresent, Is.True);
        Assert.That(payload.ArtifactIntegrityEvidence.DetachedSignaturePresent, Is.False);
        Assert.That(payload.SupportBundleEvidence.Status, Is.EqualTo("recorded"));
        Assert.That(payload.SupportBundleEvidence.FreshnessStatus, Is.EqualTo("stale"));
        Assert.That(payload.Publication.NextRecommendedPass, Does.Contain("scripts/export-support-bundle.ps1"));
    }

    [Test]
    public async Task BridgeProofEndpoint_ReturnsMachineReadableNativeReadinessAndDeliverySnapshot()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        string diagnosticsPath = Path.Combine(options.BridgeDiagnosticsDir, "ui-probe-001.json");
        string dumpPath = Path.Combine(options.BridgeDiagnosticsDir, "ui-probe-dump-001.json");
        string bridgeBootPath = Path.Combine(options.BridgeInboxDir, "boot-bridge-proof.json");
        string uiProbePath = Path.Combine(options.BridgeInboxDir, "ui-probe-bridge-proof.json");

        await File.WriteAllTextAsync(
            diagnosticsPath,
            JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                Reason = "bridge-proof-test",
                Summary = "Ranked widget candidates ready",
                ObservedWidgetCount = 1,
                ActiveWidgetCount = 1,
                Widgets = new[]
                {
                    new
                    {
                        DisplayName = "PalHudOverlay",
                        FullName = "/Game/UI/WBP_PalHudOverlay",
                        ClassName = "WBP_PalHudOverlay_C",
                        SeenCount = 4,
                        IsActive = true,
                        LastLifecycle = "Construct",
                    },
                },
            }));

        await File.WriteAllTextAsync(
            bridgeBootPath,
            JsonSerializer.Serialize(new
            {
                EventType = "bridge_boot",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    Version = "0.3.1",
                    Status = "booted",
                    Compat = "PalGameStateInGame=present | UserWidget=present | PalMapManager=present",
                    CompatSignals = new[]
                    {
                        new { Key = "PalGameStateInGame", Present = true },
                        new { Key = "UserWidget", Present = true },
                        new { Key = "PalMapManager", Present = true },
                    },
                    UiProbeEnabled = true,
                    ActionExecutorEnabled = true,
                    NativeHudRenderEnabled = true,
                    NativeHudWidgetTargetCount = 1,
                    NativeHudWidgetTargets = new[]
                    {
                        "/Game/UI/WBP_PalHudOverlay",
                    },
                    NativeHudConfigSource = "mod_override_file",
                    NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
                    ProductionSamplerEnabled = false,
                    WaypointNativeMarkerEnabled = true,
                },
            }));

        await File.WriteAllTextAsync(
            uiProbePath,
            JsonSerializer.Serialize(new
            {
                EventType = "ui_probe",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    Reason = "bridge-proof-test",
                    Summary = "Ranked widget candidates ready",
                    DumpPath = dumpPath,
                    ObservedWidgetCount = 1,
                    ActiveWidgetCount = 1,
                    Widgets = new[]
                    {
                        new
                        {
                            DisplayName = "PalHudOverlay",
                            FullName = "/Game/UI/WBP_PalHudOverlay",
                            ClassName = "WBP_PalHudOverlay_C",
                            SeenCount = 4,
                            IsActive = true,
                            LastLifecycle = "Construct",
                        },
                    },
                },
            }));

        runtime.DrainInbox();

        runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 11,
                    DisplayName = "Lifmunk",
                    Species = "Lifmunk",
                },
            ],
        });

        ChatResponse chat = await runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 11,
            RequestId = "bridge-proof-chat-001",
            UserMessage = "Give me a quick camp update.",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        string fallbackDeliveryPath = Path.Combine(options.BridgeInboxDir, "delivery-fallback-surface-bridge-proof.json");
        await File.WriteAllTextAsync(
            fallbackDeliveryPath,
            JsonSerializer.Serialize(new
            {
                EventType = "reply_delivery",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = chat.RequestId,
                    Speaker = chat.CharacterName,
                    ResponsePath = chat.ResponsePath,
                    StrategyId = chat.FallbackStrategy ?? string.Empty,
                    Phase = chat.FallbackPhase ?? string.Empty,
                    UsedFallback = chat.UsedFallback,
                    Rendered = true,
                    Surface = "client_message",
                    CardLabel = "Reply",
                    CardIndex = 1,
                    CardCount = 1,
                    Note = "bridge proof endpoint fallback surface",
                },
            }));

        string fallbackFeedbackPath = Path.Combine(options.BridgeInboxDir, "feedback-fallback-surface-bridge-proof.json");
        await File.WriteAllTextAsync(
            fallbackFeedbackPath,
            JsonSerializer.Serialize(new
            {
                EventType = "travel",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    Origin = "Camp Gate",
                    Destination = "Berry Patch",
                    Waypoint = "North Trail",
                    Mode = "guided_route",
                    Note = "bridge proof endpoint fallback feedback",
                    RequestId = chat.RequestId,
                    SourceStrategy = chat.FallbackStrategy ?? string.Empty,
                },
            }));

        runtime.DrainInbox();

        using HttpResponseMessage fallbackResponse = await fixture.Client.GetAsync("/api/bridge/proof");
        BridgeProofSnapshot? fallbackPayload = await fallbackResponse.Content.ReadFromJsonAsync<BridgeProofSnapshot>();

        Assert.That(fallbackResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(fallbackPayload, Is.Not.Null);
        Assert.That(fallbackPayload!.Status, Is.EqualTo("delivery_proven_pending_native_hud_surface"));
        Assert.That(fallbackPayload.LiveDeliveryProven, Is.True);
        Assert.That(fallbackPayload.NativeHudBindReady, Is.True);
        Dictionary<string, BridgeProofLaneSnapshot> fallbackProofLanes = fallbackPayload.ProofLanes
            .ToDictionary(lane => lane.Name, StringComparer.Ordinal);
        Assert.That(fallbackProofLanes["visible_delivery"].Status, Is.EqualTo("PASS"));
        Assert.That(fallbackProofLanes["native_hud_delivery"].Required, Is.True);
        Assert.That(fallbackProofLanes["native_hud_delivery"].Status, Is.EqualTo("FAIL"));
        Assert.That(fallbackProofLanes["native_hud_delivery"].Summary, Does.Contain("client_message"));
        Assert.That(fallbackProofLanes["native_hud_delivery"].NextAction, Does.Contain("native_hud_render_enabled"));
        Assert.That(
            fallbackPayload.CurrentBlockers,
            Contains.Item("The tracked request closed through 'client_message', but release native-proof requires reply_delivery surface=native_hud."));

        string deliveryPath = Path.Combine(options.BridgeInboxDir, "delivery-bridge-proof.json");
        await File.WriteAllTextAsync(
            deliveryPath,
            JsonSerializer.Serialize(new
            {
                EventType = "reply_delivery",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = chat.RequestId,
                    Speaker = chat.CharacterName,
                    ResponsePath = chat.ResponsePath,
                    StrategyId = chat.FallbackStrategy ?? string.Empty,
                    Phase = chat.FallbackPhase ?? string.Empty,
                    UsedFallback = chat.UsedFallback,
                    Rendered = true,
                    Surface = "native_hud",
                    CardLabel = "Reply",
                    CardIndex = 1,
                    CardCount = 1,
                    Note = "bridge proof endpoint test",
                },
            }));

        string feedbackPath = Path.Combine(options.BridgeInboxDir, "feedback-bridge-proof.json");
        await File.WriteAllTextAsync(
            feedbackPath,
            JsonSerializer.Serialize(new
            {
                EventType = "travel",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    Origin = "Camp Gate",
                    Destination = "Berry Patch",
                    Waypoint = "North Trail",
                    Mode = "guided_route",
                    Note = "bridge proof endpoint test feedback",
                    RequestId = chat.RequestId,
                    SourceStrategy = chat.FallbackStrategy ?? string.Empty,
                },
            }));

        runtime.DrainInbox();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/bridge/proof");
        BridgeProofSnapshot? payload = await response.Content.ReadFromJsonAsync<BridgeProofSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Status, Is.EqualTo("delivery_proven"));
        Assert.That(payload.LiveDeliveryProven, Is.True);
        Assert.That(payload.NativeHudBindReady, Is.True);
        Assert.That(payload.LastBridgeEventType, Is.EqualTo("travel"));
        Assert.That(payload.LoopProof.ActiveRequestId, Is.EqualTo(chat.RequestId));
        Assert.That(payload.LoopProof.VisibleDeliveryConfirmed, Is.True);
        Assert.That(payload.LoopProof.ActionFeedbackObserved, Is.True);
        Assert.That(payload.NativeReadiness.TopUiProbeCandidate, Is.EqualTo("PalHudOverlay"));
        Assert.That(payload.NativeReadiness.ConfiguredHudTargets, Contains.Item("/Game/UI/WBP_PalHudOverlay"));
        Assert.That(payload.NativeReadiness.NativeHudConfigSource, Is.EqualTo("mod_override_file"));
        Assert.That(payload.NativeReadiness.NativeHudConfigPath, Does.EndWith(@"config\native-hud.lua"));
        Assert.That(payload.NativeReadiness.HudBindRecommendation.Status, Is.EqualTo("bind_ready"));
        Assert.That(payload.NativeReadiness.HudBindRecommendation.ConfiguredTargetMatchesRecommendation, Is.True);
        Assert.That(payload.NativeReadiness.HudBindRecommendation.RecommendedTarget, Is.EqualTo("/Game/UI/WBP_PalHudOverlay"));
        Assert.That(payload.UiProbeDiagnostics, Is.Not.Null);
        Assert.That(payload.UiProbeDiagnostics!.CandidateCount, Is.EqualTo(1));
        Assert.That(payload.LastUiProbe, Is.Not.Null);
        Assert.That(payload.LastUiProbe!.DumpPath, Is.EqualTo(dumpPath));
        Dictionary<string, BridgeProofLaneSnapshot> proofLanes = payload.ProofLanes
            .ToDictionary(lane => lane.Name, StringComparer.Ordinal);
        Assert.That(proofLanes.Keys, Is.SupersetOf(new[]
        {
            "bridge_boot",
            "user_widget_compat",
            "ui_probe_capture",
            "native_hud_bind",
            "chat_ingress",
            "outbox_reply",
            "visible_delivery",
            "native_hud_delivery",
            "action_feedback",
            "native_audio_mixer",
        }));
        Assert.That(proofLanes["bridge_boot"].Status, Is.EqualTo("PASS"));
        Assert.That(proofLanes["user_widget_compat"].Status, Is.EqualTo("PASS"));
        Assert.That(proofLanes["ui_probe_capture"].Summary, Does.Contain("PalHudOverlay"));
        Assert.That(proofLanes["native_hud_bind"].Required, Is.False);
        Assert.That(proofLanes["native_hud_bind"].Status, Is.EqualTo("PASS"));
        Assert.That(proofLanes["chat_ingress"].Summary, Does.Contain(chat.RequestId));
        Assert.That(proofLanes["visible_delivery"].Summary, Does.Contain("native_hud"));
        Assert.That(proofLanes["native_hud_delivery"].Status, Is.EqualTo("PASS"));
        Assert.That(proofLanes["action_feedback"].Required, Is.False);
        Assert.That(proofLanes["action_feedback"].Status, Is.EqualTo("PASS"));
        Assert.That(proofLanes["action_feedback"].Summary, Does.Contain("No guarded action"));
        Assert.That(proofLanes["native_audio_mixer"].Required, Is.False);
        Assert.That(proofLanes["native_audio_mixer"].Status, Is.EqualTo("PASS"));
        Assert.That(proofLanes.Values.Where(lane => lane.Status != "PASS").Select(lane => lane.NextAction), Is.Empty);
        Assert.That(payload.ReadyEvidence.Any(item => item.Contains("Ranked widget candidates", StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(payload.CurrentBlockers, Is.Empty);
    }

    [Test]
    public async Task BridgeProofEndpoint_WhenRawPcmNeedsMixer_ReturnsNativeAudioMixerProofLane()
    {
        byte[] fakeAudio = new byte[48_000];
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Tts:Enabled"] = "true",
            },
            services =>
            {
                services.RemoveAll<ITtsClient>();
                services.AddSingleton<ITtsClient>(new EndpointCannedTtsClient(
                    request => TtsResult.Succeeded(fakeAudio, "audio/L16; rate=24000; channels=1", request.Voice ?? "proof-voice")));
            });

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 12,
                    DisplayName = "Lifmunk",
                    Species = "Lifmunk",
                },
            ],
        });

        ChatResponse chat = await runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 12,
            RequestId = "bridge-proof-raw-audio-001",
            UserMessage = "Say this one in a very short voice line.",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        RuntimeHealth outboxHealth = runtime.GetHealth();
        Assert.That(outboxHealth.BridgeLoop.SpeechPlaybackExpected, Is.True);

        string deliveryPath = Path.Combine(options.BridgeInboxDir, "delivery-raw-audio-proof.json");
        await File.WriteAllTextAsync(
            deliveryPath,
            JsonSerializer.Serialize(new
            {
                EventType = "reply_delivery",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = chat.RequestId,
                    Speaker = chat.CharacterName,
                    ResponsePath = chat.ResponsePath,
                    StrategyId = chat.FallbackStrategy ?? string.Empty,
                    Phase = chat.FallbackPhase ?? string.Empty,
                    UsedFallback = chat.UsedFallback,
                    Rendered = true,
                    Surface = "native_hud",
                    CardLabel = "Reply",
                    CardIndex = 1,
                    CardCount = 1,
                    Note = "raw audio proof delivery",
                },
            }));

        string speechPlaybackPath = Path.Combine(options.BridgeInboxDir, "speech-playback-raw-audio-proof.json");
        await File.WriteAllTextAsync(
            speechPlaybackPath,
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow.AddMilliseconds(25),
                Payload = new
                {
                    RequestId = chat.RequestId,
                    Started = false,
                    ArtifactBytes = fakeAudio.Length,
                    AttemptCount = 0,
                    ElapsedMs = 2,
                    PlaybackMode = "raw_pcm",
                    PlaybackHint = "raw_pcm",
                    MimeType = "audio/L16; rate=24000; channels=1",
                    FileExtension = ".pcm",
                    AudioEncoding = "l16_pcm",
                    SampleFormat = "signed_integer",
                    ByteOrder = "big_endian",
                    MixerConversionHint = "byte_swap_integer_to_float32",
                    SampleRateHz = 24_000,
                    ChannelCount = 1,
                    BitsPerSample = 16,
                    DurationMs = 1_000,
                    ByteRate = 48_000L,
                    BlockAlignBytes = 2,
                    AudioDataBytes = 48_000L,
                    FrameCount = 24_000L,
                    Reason = "speech raw pcm requires native mixer binding",
                    FailureCode = "raw_pcm_native_mixer_required",
                },
            }));

        runtime.DrainInbox();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/bridge/proof");
        BridgeProofSnapshot? payload = await response.Content.ReadFromJsonAsync<BridgeProofSnapshot>();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.Status, Is.EqualTo("speech_playback_failed"));

        Dictionary<string, BridgeProofLaneSnapshot> proofLanes = payload.ProofLanes
            .ToDictionary(lane => lane.Name, StringComparer.Ordinal);
        Assert.That(proofLanes.Keys, Contains.Item("native_audio_mixer"));
        Assert.That(proofLanes["speech_playback"].Status, Is.EqualTo("FAIL"));
        Assert.That(proofLanes["native_audio_mixer"].Required, Is.True);
        Assert.That(proofLanes["native_audio_mixer"].Status, Is.EqualTo("FAIL"));
        Assert.That(proofLanes["native_audio_mixer"].Summary, Does.Contain("Raw PCM"));
        Assert.That(proofLanes["native_audio_mixer"].Summary, Does.Contain("24,000 Hz"));
        Assert.That(proofLanes["native_audio_mixer"].NextAction, Does.Contain("native PCM mixer"));

        await File.WriteAllTextAsync(
            Path.Combine(options.BridgeInboxDir, "speech-playback-native-mixer-unavailable.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow.AddMilliseconds(40),
                Payload = new
                {
                    RequestId = chat.RequestId,
                    Started = false,
                    ArtifactBytes = fakeAudio.Length,
                    AttemptCount = 1,
                    ElapsedMs = 2,
                    PlaybackMode = "native_mixer",
                    PlaybackHint = "raw_pcm",
                    MimeType = "audio/L16; rate=24000; channels=1",
                    FileExtension = ".pcm",
                    AudioEncoding = "l16_pcm",
                    SampleFormat = "signed_integer",
                    ByteOrder = "big_endian",
                    MixerConversionHint = "byte_swap_integer_to_float32",
                    SampleRateHz = 24_000,
                    ChannelCount = 1,
                    BitsPerSample = 16,
                    DurationMs = 1_000,
                    ByteRate = 48_000L,
                    BlockAlignBytes = 2,
                    AudioDataBytes = 48_000L,
                    FrameCount = 24_000L,
                    Reason = "native audio mixer callback unavailable",
                    FailureCode = "native_audio_mixer_unavailable",
                },
            }));

        runtime.DrainInbox();

        using HttpResponseMessage unavailableResponse = await fixture.Client.GetAsync("/api/bridge/proof");
        BridgeProofSnapshot? unavailablePayload = await unavailableResponse.Content.ReadFromJsonAsync<BridgeProofSnapshot>();

        Assert.That(unavailableResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(unavailablePayload, Is.Not.Null);
        Dictionary<string, BridgeProofLaneSnapshot> unavailableProofLanes = unavailablePayload!.ProofLanes
            .ToDictionary(lane => lane.Name, StringComparer.Ordinal);
        Assert.That(unavailableProofLanes["native_audio_mixer"].Required, Is.True);
        Assert.That(unavailableProofLanes["native_audio_mixer"].Status, Is.EqualTo("FAIL"));
        Assert.That(unavailableProofLanes["native_audio_mixer"].Summary, Does.Contain("callback was unavailable"));
        Assert.That(unavailableProofLanes["native_audio_mixer"].NextAction, Does.Contain("PalLLM_NativeAudioMixer_PlayRawPcm"));

        await File.WriteAllTextAsync(
            Path.Combine(options.BridgeInboxDir, "speech-playback-native-mixer-proof.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow.AddMilliseconds(50),
                Payload = new
                {
                    RequestId = chat.RequestId,
                    Started = true,
                    ArtifactBytes = fakeAudio.Length,
                    AttemptCount = 1,
                    ElapsedMs = 3,
                    PlaybackMode = "native_mixer",
                    PlaybackHint = "raw_pcm",
                    MimeType = "audio/L16; rate=24000; channels=1",
                    FileExtension = ".pcm",
                    AudioEncoding = "l16_pcm",
                    SampleFormat = "signed_integer",
                    ByteOrder = "big_endian",
                    MixerConversionHint = "byte_swap_integer_to_float32",
                    SampleRateHz = 24_000,
                    ChannelCount = 1,
                    BitsPerSample = 16,
                    DurationMs = 1_000,
                    ByteRate = 48_000L,
                    BlockAlignBytes = 2,
                    AudioDataBytes = 48_000L,
                    FrameCount = 24_000L,
                    Reason = "native audio mixer accepted raw pcm",
                    FailureCode = string.Empty,
                },
            }));

        runtime.DrainInbox();

        using HttpResponseMessage nativeMixerResponse = await fixture.Client.GetAsync("/api/bridge/proof");
        BridgeProofSnapshot? nativeMixerPayload = await nativeMixerResponse.Content.ReadFromJsonAsync<BridgeProofSnapshot>();

        Assert.That(nativeMixerResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(nativeMixerPayload, Is.Not.Null);
        Dictionary<string, BridgeProofLaneSnapshot> nativeMixerProofLanes = nativeMixerPayload!.ProofLanes
            .ToDictionary(lane => lane.Name, StringComparer.Ordinal);
        Assert.That(nativeMixerProofLanes["speech_playback"].Status, Is.EqualTo("PASS"));
        Assert.That(nativeMixerProofLanes["native_audio_mixer"].Required, Is.True);
        Assert.That(nativeMixerProofLanes["native_audio_mixer"].Status, Is.EqualTo("PASS"));
        Assert.That(nativeMixerProofLanes["native_audio_mixer"].Summary, Does.Contain("Native audio mixer"));
        Assert.That(nativeMixerProofLanes["native_audio_mixer"].Summary, Does.Contain("24,000 Hz"));

        byte[] fakeWavAudio = [0x52, 0x49, 0x46, 0x46, 0x08, 0x00, 0x00, 0x00];
        await using var wavFixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Tts:Enabled"] = "true",
            },
            services =>
            {
                services.RemoveAll<ITtsClient>();
                services.AddSingleton<ITtsClient>(new EndpointCannedTtsClient(
                    request => TtsResult.Succeeded(fakeWavAudio, "audio/wav", request.Voice ?? "proof-voice")));
            });

        using IServiceScope wavScope = wavFixture.Factory.Services.CreateScope();
        PalLlmRuntime wavRuntime = wavScope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions wavOptions = wavScope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        wavRuntime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 13,
                    DisplayName = "Lifmunk",
                    Species = "Lifmunk",
                },
            ],
        });

        ChatResponse wavChat = await wavRuntime.ChatAsync(new ChatRequest
        {
            CharacterId = 13,
            RequestId = "bridge-proof-wav-audio-001",
            UserMessage = "Say this one through the helper.",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        await File.WriteAllTextAsync(
            Path.Combine(wavOptions.BridgeInboxDir, "delivery-wav-audio-proof.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "reply_delivery",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = wavChat.RequestId,
                    Speaker = wavChat.CharacterName,
                    ResponsePath = wavChat.ResponsePath,
                    Rendered = true,
                    Surface = "native_hud",
                },
            }));

        await File.WriteAllTextAsync(
            Path.Combine(wavOptions.BridgeInboxDir, "speech-playback-wav-audio-proof.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow.AddMilliseconds(25),
                Payload = new
                {
                    RequestId = wavChat.RequestId,
                    Started = true,
                    ArtifactBytes = fakeWavAudio.Length,
                    AttemptCount = 1,
                    ElapsedMs = 4,
                    PlaybackMode = "sound_player",
                    PlaybackHint = "sound_player",
                    MimeType = "audio/wav",
                    FileExtension = ".wav",
                    AudioEncoding = "pcm",
                    SampleFormat = "signed_integer",
                    SampleRateHz = 24_000,
                    ChannelCount = 1,
                    BitsPerSample = 16,
                    Reason = "sound_player",
                    FailureCode = string.Empty,
                },
            }));

        wavRuntime.DrainInbox();

        using HttpResponseMessage wavResponse = await wavFixture.Client.GetAsync("/api/bridge/proof");
        BridgeProofSnapshot? wavPayload = await wavResponse.Content.ReadFromJsonAsync<BridgeProofSnapshot>();

        Assert.That(wavResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(wavPayload, Is.Not.Null);
        Dictionary<string, BridgeProofLaneSnapshot> wavProofLanes = wavPayload!.ProofLanes
            .ToDictionary(lane => lane.Name, StringComparer.Ordinal);
        Assert.That(wavProofLanes["speech_playback"].Status, Is.EqualTo("PASS"));
        Assert.That(wavProofLanes["native_audio_mixer"].Required, Is.False);
        Assert.That(wavProofLanes["native_audio_mixer"].Status, Is.EqualTo("PASS"));
    }

    [Test]
    public async Task BridgeProofEndpoint_WhenIfNoneMatchMatches_Returns304AndPrivateCacheHeaders()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/bridge/proof");
        string? etag = first.Headers.ETag?.ToString();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(etag, Is.Not.Null.And.Not.Empty);
        Assert.That(first.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromMinutes(60)));
        Assert.That(first.Headers.CacheControl?.Private, Is.True);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/bridge/proof");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(second.Headers.ETag?.ToString(), Is.EqualTo(etag));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task ReleaseReadinessEndpoint_WhenIfNoneMatchMatches_Returns304AndPrivateCacheHeaders()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/release/readiness");
        string? etag = first.Headers.ETag?.ToString();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(etag, Is.Not.Null.And.Not.Empty);
        Assert.That(first.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromMinutes(60)));
        Assert.That(first.Headers.CacheControl?.Private, Is.True);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/release/readiness");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(second.Headers.ETag?.ToString(), Is.EqualTo(etag));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task UpstreamMcpEndpoint_WhenIfNoneMatchMatches_Returns304AndPrivateCacheHeaders()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage first = await fixture.Client.GetAsync("/api/mcp/upstream");
        string? etag = first.Headers.ETag?.ToString();

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(etag, Is.Not.Null.And.Not.Empty);
        Assert.That(first.Headers.CacheControl?.MaxAge, Is.EqualTo(TimeSpan.FromSeconds(5)));
        Assert.That(first.Headers.CacheControl?.Private, Is.True);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/mcp/upstream");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        using HttpResponseMessage second = await fixture.Client.SendAsync(request);

        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.NotModified));
        Assert.That(second.Headers.ETag?.ToString(), Is.EqualTo(etag));
        Assert.That(second.Content.Headers.ContentLength ?? 0, Is.EqualTo(0));
    }

    [Test]
    public async Task FeaturesEndpoint_EveryStatusIsFromTheKnownVocabulary()
    {
        // Status is an open string today but the UI, tests, and roadmap
        // accounting all read from a fixed vocabulary:
        //   * `ready`      â€” implementation is live on the default ship path
        //   * `scaffolded` â€” code exists and passes its own checks, but is
        //                    OFF by default pending operator validation
        //                    against a live game session (e.g., a widget
        //                    seam or a hook signature on the current
        //                    Palworld build). Scaffolded features count
        //                    toward partial roadmap credit, not full.
        //   * `deferred`   â€” documented non-goal for the current scope
        // Catch drift early â€” a typo like `reaedy` would silently break
        // dashboard filters and roadmap math without this guard.
        await using var fixture = new SidecarTestFixture();
        string json = await fixture.Client.GetStringAsync("/api/features");
        using JsonDocument document = JsonDocument.Parse(json);

        HashSet<string> allowedStatuses = new(StringComparer.Ordinal)
        {
            "ready",
            "scaffolded",
            "deferred",
        };
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            string status = entry.GetProperty("Status").GetString() ?? string.Empty;
            Assert.That(
                allowedStatuses.Contains(status), Is.True,
                $"Feature '{entry.GetProperty("Id").GetString()}' reports status '{status}' which is not in the known vocabulary. Add the new status to the allowlist here if you intentionally introduced it.");
        }
    }

    [Test]
    public async Task FeaturesEndpoint_ScaffoldedEntriesMatchRoadmapCommitment()
    {
        // The roadmap's honest completion % depends on exactly which
        // features are `scaffolded` (code exists but default-off). Lock
        // in the set so someone flipping a Status=ready to scaffolded
        // (or vice-versa) updates the roadmap in the same PR.
        await using var fixture = new SidecarTestFixture();
        string json = await fixture.Client.GetStringAsync("/api/features");
        using JsonDocument document = JsonDocument.Parse(json);

        List<string> scaffoldedIds = new();
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("Status").GetString() == "scaffolded")
            {
                scaffoldedIds.Add(entry.GetProperty("Id").GetString() ?? string.Empty);
            }
        }

        string[] expected = ["native-hud-attachment", "production-sampler"];
        Assert.That(
            scaffoldedIds.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            Is.EqualTo(expected.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
            "docs/ROADMAP.md pins the set of scaffolded features. If you add or remove one, update the roadmap's completion math in the same change.");
    }

    [Test]
    public async Task FeaturesEndpoint_ExactlyOneDeferredEntryMatchesRoadmapCommitment()
    {
        // docs/ARCHITECTURE.md Â§ "Feature-catalog snapshot" calls out that the
        // single `deferred` entry is `autopilot-port`. If someone adds another
        // deferred feature without updating that doc, this test catches it.
        await using var fixture = new SidecarTestFixture();
        string json = await fixture.Client.GetStringAsync("/api/features");
        using JsonDocument document = JsonDocument.Parse(json);

        List<string> deferredIds = new();
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("Status").GetString() == "deferred")
            {
                deferredIds.Add(entry.GetProperty("Id").GetString() ?? string.Empty);
            }
        }

        Assert.That(deferredIds, Is.EqualTo(new[] { "autopilot-port" }),
            "docs/ARCHITECTURE.md commits to exactly one deferred entry (autopilot-port). Update that doc if you intentionally added or removed a deferred feature.");
    }

    [Test]
    public async Task RootEndpoint_ReturnsInterfaceShellWithSkeletonStates()
    {
        await using var fixture = new SidecarTestFixture();

        string html = await fixture.Client.GetStringAsync("/");
        string appScript = await fixture.Client.GetStringAsync("/app.js");
        string styles = await fixture.Client.GetStringAsync("/styles.css");

        Assert.That(html, Does.Contain("PalLLM Field Console"));
        Assert.That(html, Does.Contain("skeleton-card"));
        Assert.That(html, Does.Contain("status-strip"));
        Assert.That(html, Does.Contain("section-nav"));
        Assert.That(html, Does.Contain("/favicon.svg"),
            "The Field Console must declare the existing SVG favicon so Chromium does not fall back to a noisy /favicon.ico request.");
        Assert.That(appScript.Length, Is.GreaterThan(10_000),
            "The Field Console script endpoint must return the real asset, not an empty static-asset response.");
        Assert.That(styles.Length, Is.GreaterThan(10_000),
            "The Field Console stylesheet endpoint must return the real asset, not an empty static-asset response.");
        Assert.That(appScript, Does.Contain("readLocalStorage"));
        Assert.That(appScript, Does.Contain("writeLocalStorage"));
        Assert.That(appScript, Does.Not.Contain("window.localStorage.getItem"),
            "The Field Console must not hard-fail startup when browser storage is blocked.");
        Assert.That(appScript, Does.Not.Contain("window.localStorage.setItem"),
            "The Field Console must keep auto-refresh usable when browser storage is blocked.");
        Assert.That(appScript.Split("function escapeHtml(", StringSplitOptions.None).Length - 1, Is.EqualTo(1),
            "The Field Console app.js is an ES module; duplicate top-level helper declarations fail parsing.");
        Assert.That(appScript, Does.Contain("class=\"feature-notes\""),
            "Feature notes must render with the CSS class that wraps long paths and command fragments.");
        Assert.That(styles, Does.Contain("minmax(min(280px, 100%), 1fr)"),
            "Feature-card columns must shrink inside narrow or dense viewports instead of forcing horizontal page overflow.");
        Assert.That(styles, Does.Contain("grid-template-columns: minmax(min(8rem, 100%), max-content) minmax(0, 1fr)"),
            "Suggestion cards must not force page overflow in a narrow sidecar pane.");
        Assert.That(styles, Does.Contain("overflow-wrap: anywhere;"),
            "Long feature notes, file paths, and command fragments must wrap inside their cards.");
        Assert.That(styles, Does.Contain(".quickstart-instructions dd"),
            "Quickstart instructions must keep long setup commands wrapped on mobile.");
        Assert.That(styles, Does.Contain("--shell: min(1280px, calc(100dvw - 2rem));"),
            "The Field Console shell must size against the dynamic viewport so snapped sidecar panes do not clip.");
        Assert.That(styles, Does.Contain("@media (max-width: 420px), (max-height: 760px) and (max-width: 720px)"),
            "The Field Console needs a compact side-panel breakpoint for game-adjacent browser panes.");
        Assert.That(styles, Does.Contain("overscroll-behavior-inline: contain;"),
            "The section nav must scroll horizontally inside very narrow panes instead of widening the page.");
    }

    [Test]
    public async Task WorldEndpoint_WhenUiProbeWasCaptured_ReturnsProbeSummary()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();
        options.EnsureDirectories();

        string bridgeFile = Path.Combine(options.BridgeInboxDir, "ui-probe-001.json");
        await File.WriteAllTextAsync(bridgeFile, JsonSerializer.Serialize(new
        {
            EventType = "ui_probe",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Reason = "auto_widget_sample",
                Summary = "4 observed, 1 active | WBP_HudRoot active | WBP_Inventory x2",
                DumpPath = @"C:\Users\Tester\AppData\Local\Pal\Saved\PalLLM\Bridge\Diagnostics\palllm-ui-probe-001.json",
                ObservedWidgetCount = 4,
                ActiveWidgetCount = 1,
                Widgets = new[]
                {
                    new
                    {
                        DisplayName = "WBP_HudRoot",
                        FullName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        ClassName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        SeenCount = 1,
                        IsActive = true,
                        LastLifecycle = "construct",
                    },
                },
            },
        }));

        HttpResponseMessage drainResponse = await fixture.Client.PostAsync("/api/bridge/drain", content: null);
        RuntimeWorldState? world = await fixture.Client.GetFromJsonAsync<RuntimeWorldState>("/api/world");

        Assert.That(drainResponse.IsSuccessStatusCode, Is.True);
        Assert.That(world, Is.Not.Null);
        Assert.That(world!.Bridge.LastEventType, Is.EqualTo("ui_probe"));
        Assert.That(world.Bridge.LastUiProbe, Is.Not.Null);
        Assert.That(world.Bridge.LastUiProbe!.Reason, Is.EqualTo("auto_widget_sample"));
        Assert.That(world.Bridge.LastUiProbe.Summary, Does.Contain("WBP_HudRoot"));
        Assert.That(world.Bridge.LastUiProbe.Widgets.Single().DisplayName, Is.EqualTo("WBP_HudRoot"));
    }

    [Test]
    public async Task UiProbeEndpoint_WhenDiagnosticsExist_ReturnsRankedCandidates()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();
        options.EnsureDirectories();

        await File.WriteAllTextAsync(
            Path.Combine(options.BridgeDiagnosticsDir, "palllm-ui-probe-001.json"),
            JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = new DateTimeOffset(2026, 4, 18, 2, 0, 0, TimeSpan.Zero),
                Reason = "auto_widget_sample",
                Summary = "Hud root persists while map menu closes",
                ObservedWidgetCount = 5,
                ActiveWidgetCount = 1,
                Widgets = new[]
                {
                    new
                    {
                        DisplayName = "WBP_HudRoot",
                        FullName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        ClassName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        SeenCount = 6,
                        IsActive = true,
                        LastLifecycle = "construct",
                    },
                    new
                    {
                        DisplayName = "WBP_MapMenu",
                        FullName = "/Game/UI/WBP_MapMenu.WBP_MapMenu_C",
                        ClassName = "/Game/UI/WBP_MapMenu.WBP_MapMenu_C",
                        SeenCount = 2,
                        IsActive = false,
                        LastLifecycle = "destruct",
                    },
                },
            }));

        HttpResponseMessage response = await fixture.Client.GetAsync("/api/bridge/ui-probe");
        UiProbeDiagnosticsSnapshot? payload = await response.Content.ReadFromJsonAsync<UiProbeDiagnosticsSnapshot>();

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.DumpCount, Is.EqualTo(1));
        Assert.That(payload.CandidateCount, Is.EqualTo(2));
        Assert.That(payload.Candidates, Has.Count.EqualTo(2));
        Assert.That(payload.Candidates[0].DisplayName, Is.EqualTo("WBP_HudRoot"));
        Assert.That(payload.Candidates[0].Score, Is.GreaterThan(payload.Candidates[1].Score));
    }

    [Test]
    public async Task LifetimeRelationshipsEndpoint_WhenPersistedAggregateExceedsConfiguredCap_ReturnsEmptyAggregate()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();
        options.EnsureDirectories();

        string lifetimeDir = Path.Combine(options.PalSavedRoot, "Runtime", "LifetimeRelationships");
        Directory.CreateDirectory(lifetimeDir);

        LifetimeRelationshipAggregate oversizedAggregate = new(
            CapturedAtUtc: new DateTimeOffset(2026, 4, 28, 15, 0, 0, TimeSpan.Zero),
            Characters:
            [
                new LifetimeRelationship(
                    CharacterId: 1,
                    CharacterName: new string('L', options.Http.LocalArtifactMaxBytes + 2048),
                    FirstSeenUtc: new DateTimeOffset(2026, 4, 20, 12, 0, 0, TimeSpan.Zero),
                    LastSeenUtc: new DateTimeOffset(2026, 4, 28, 15, 0, 0, TimeSpan.Zero),
                    SessionCount: 3,
                    CurrentAffinity: 50,
                    PeakAffinity: 70,
                    FloorAffinity: 10,
                    CumulativeAffinity: 130,
                    MoodTally: new Dictionary<string, int>
                    {
                        ["Warm"] = 2,
                        ["Neutral"] = 1,
                    }),
            ]);

        await File.WriteAllTextAsync(
            Path.Combine(lifetimeDir, "latest.json"),
            LifetimeRelationshipAggregator.Serialize(oversizedAggregate));

        HttpResponseMessage response = await fixture.Client.GetAsync("/api/relationships/lifetime");
        string json = await response.Content.ReadAsStringAsync();
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(document.RootElement.GetProperty("Aggregate").GetProperty("Characters").GetArrayLength(), Is.EqualTo(0));
        Assert.That(document.RootElement.GetProperty("Summaries").GetArrayLength(), Is.EqualTo(0));
    }

    [Test]
    public async Task ChatEndpoint_SmokeLoop_DrainsBridgeInboxAndWritesReplyEnvelope()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            WorldName = "Palpagos",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 12,
                    DisplayName = "CampScout",
                    Species = "CampScout",
                },
            ],
        });

        string bridgeFile = Path.Combine(options.BridgeInboxDir, "base-001.json");
        await File.WriteAllTextAsync(bridgeFile, JsonSerializer.Serialize(new
        {
            EventType = "base_discovered",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                BaseId = "FortVerdant",
                AreaRange = 42.5f,
            },
        }));

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/api/chat", new ChatRequest
        {
            CharacterId = 12,
            RequestId = "smoke-chat-001",
            UserMessage = "How should we set up this camp?",
            TaskTag = "chat_camp",
        });
        ChatResponse? payload = await response.Content.ReadFromJsonAsync<ChatResponse>();

        Assert.That(response.IsSuccessStatusCode, Is.True);
        Assert.That(payload, Is.Not.Null);
        Assert.That(payload!.RequestId, Is.EqualTo("smoke-chat-001"));
        Assert.That(payload.AssistantMessage, Is.Not.Null.And.Not.Empty);
        Assert.That(payload.ResponsePath, Is.EqualTo("fallback_inference_disabled"));
        Assert.That(payload.SystemPrompt, Does.Contain("Known bases: FortVerdant (range 42.5)"));
        Assert.That(payload.SystemPrompt, Does.Contain("Recent world events: base_discovered:FortVerdant"));
        Assert.That(runtime.GetWorldState().Snapshot.ActiveBaseIds, Contains.Item("FortVerdant"));
        Assert.That(File.Exists(bridgeFile), Is.False);
        Assert.That(Directory.GetFiles(options.BridgeArchiveDir, "*.json"), Has.Length.EqualTo(1));

        string outboxFile = Directory.GetFiles(options.BridgeOutboxDir, "*.json").Single();
        OutboxEnvelope? envelope = JsonSerializer.Deserialize<OutboxEnvelope>(
            await File.ReadAllTextAsync(outboxFile));

        Assert.That(envelope, Is.Not.Null);
        Assert.That(envelope!.EventType, Is.EqualTo("chat_reply"));
        Assert.That(envelope.Source, Is.EqualTo("palllm"));
        Assert.That(envelope.Payload.RequestId, Is.EqualTo(payload.RequestId));
        Assert.That(envelope.Payload.AssistantMessage, Is.EqualTo(payload.AssistantMessage));
        Assert.That(envelope.Payload.ResponsePath, Is.EqualTo(payload.ResponsePath));
        Assert.That(envelope.Payload.Presentation.Source, Is.EqualTo(payload.Presentation.Source));
        Assert.That(envelope.Payload.Presentation.Summary, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.FamilyId, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.LayoutMode, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.PathBadge, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.PrimaryTitle, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.CueTitle, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.ReadoutTitle, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.SupportTitle, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.ActionPreviewTitle, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.ActionFeedbackTitle, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.FollowupOrder, Has.Count.EqualTo(3));
        Assert.That(envelope.Payload.Presentation.Surface.HeaderTokens, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.CueTokens, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.FocusTokens, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.StatusTokens, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.StageTokens, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.AtmosphereTokens, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.FooterTokens, Is.Not.Empty);
        Assert.That(envelope.Payload.Presentation.Surface.CardBudget, Is.InRange(1, 3));
        Assert.That(envelope.Payload.Presentation.Surface.PrimaryCueTokenCount, Is.InRange(0, 2));
        Assert.That(envelope.Payload.Presentation.Surface.PrimaryFocusTokenCount, Is.InRange(0, 2));
        Assert.That(envelope.Payload.Presentation.Surface.PrimaryStatusTokenCount, Is.InRange(0, 2));
        Assert.That(envelope.Payload.Presentation.Surface.PrimaryStageTokenCount, Is.InRange(0, 1));
        Assert.That(envelope.Payload.Presentation.Surface.PrimaryAtmosphereTokenCount, Is.InRange(0, 1));
    }

    [Test]
    public async Task BridgeOutboxEndpoints_ListAndClearGeneratedReply()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 19,
                    DisplayName = "CampGuardian",
                    Species = "CampGuardian",
                },
            ],
        });

        HttpResponseMessage chatResponse = await fixture.Client.PostAsJsonAsync("/api/chat", new ChatRequest
        {
            CharacterId = 19,
            RequestId = "outbox-listing-001",
            UserMessage = "Give me a quick update.",
            TaskTag = "chat_general",
        });

        Assert.That(chatResponse.IsSuccessStatusCode, Is.True);

        HttpResponseMessage listResponse = await fixture.Client.GetAsync("/api/bridge/outbox");
        OutboxListing[]? listings = await listResponse.Content.ReadFromJsonAsync<OutboxListing[]>();

        Assert.That(listResponse.IsSuccessStatusCode, Is.True);
        Assert.That(listings, Is.Not.Null);
        Assert.That(listings!, Has.Length.EqualTo(1));

        string outboxFile = Directory.GetFiles(options.BridgeOutboxDir, "*.json").Single();
        Assert.That(listings[0].FileName, Is.EqualTo(Path.GetFileName(outboxFile)));
        Assert.That(listings[0].SizeBytes, Is.GreaterThan(0));

        HttpResponseMessage clearResponse = await fixture.Client.PostAsync("/api/bridge/outbox/clear", content: null);
        using JsonDocument clearPayload = JsonDocument.Parse(await clearResponse.Content.ReadAsStringAsync());

        Assert.That(clearResponse.IsSuccessStatusCode, Is.True);
        Assert.That(clearPayload.RootElement.GetProperty("removed").GetInt32(), Is.EqualTo(1));
        Assert.That(Directory.GetFiles(options.BridgeOutboxDir, "*.json"), Is.Empty);

        HttpResponseMessage afterClearResponse = await fixture.Client.GetAsync("/api/bridge/outbox");
        OutboxListing[]? afterClear = await afterClearResponse.Content.ReadFromJsonAsync<OutboxListing[]>();

        Assert.That(afterClearResponse.IsSuccessStatusCode, Is.True);
        Assert.That(afterClear, Is.Not.Null);
        Assert.That(afterClear!, Is.Empty);
    }

    [Test]
    public async Task ChatEndpoint_WhenRequestIsInvalid_ReturnsValidationProblemWithoutWritingOutbox()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/api/chat", new ChatRequest
        {
            CharacterId = 7,
            UserMessage = string.Empty,
            TaskTag = "chat_general",
        });
        using JsonDocument payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.BadRequest));
        Assert.That(payload.RootElement.GetProperty("errors").TryGetProperty("UserMessage", out JsonElement errors), Is.True);
        Assert.That(errors.EnumerateArray().Select(value => value.GetString()), Contains.Item("UserMessage is required."));
        Assert.That(Directory.GetFiles(options.BridgeOutboxDir, "*.json"), Is.Empty);
    }

    [Test]
    public async Task ChatEndpoint_WhenUnexpectedRuntimeExceptionOccurs_ReturnsSanitizedProblemDetails()
    {
        await using var fixture = new SidecarTestFixture(
            new Dictionary<string, string?>
            {
                ["PalLLM:Inference:Enabled"] = "true",
                ["PalLLM:Fallback:EnablePolicyBypass"] = "false",
            },
            services =>
            {
                services.RemoveAll<IInferenceClient>();
                services.AddSingleton<IInferenceClient>(new ThrowingInferenceClient());
            });

        HttpResponseMessage response = await fixture.Client.PostAsJsonAsync("/api/chat", new ChatRequest
        {
            CharacterId = 7,
            UserMessage = "Force the live inference lane.",
            TaskTag = "chat_general",
        });
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument payload = JsonDocument.Parse(body);

        Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.InternalServerError));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(payload.RootElement.GetProperty("title").GetString(), Is.EqualTo("Chat turn failed."));
        Assert.That(payload.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(500));
        Assert.That(payload.RootElement.GetProperty("detail").GetString(),
            Is.EqualTo("The chat orchestration pipeline raised an unexpected internal error. Re-run the request; if the issue persists, check the sidecar log for the matching warning entry."));
        Assert.That(body, Does.Not.Contain(nameof(InvalidOperationException)));
        Assert.That(body, Does.Not.Contain(nameof(ThrowingInferenceClient)));
        Assert.That(body, Does.Not.Contain("D:\\secret"));
        Assert.That(body, Does.Not.Contain("Type="));
    }

    [Test]
    public async Task ChatEndpoint_DeliveryReplayScenarioSet_ProducesRepresentativeOutboxContracts()
    {
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        var strategyIds = new List<string>();
        var surfaceFamilies = new List<string>();
        var audioBehaviors = new List<string>();
        var visualBehaviors = new List<string>();

        foreach (DeliveryReplayScenario scenario in BuildDeliveryReplayScenarios())
        {
            string requestId = $"replay-{scenario.Name}-{Guid.NewGuid():N}"[..24];

            HttpResponseMessage snapshotResponse = await fixture.Client.PostAsJsonAsync("/api/snapshot", scenario.Snapshot);
            Assert.That(snapshotResponse.IsSuccessStatusCode, Is.True, $"Snapshot update failed for scenario '{scenario.Name}'.");

            HttpResponseMessage chatResponse = await fixture.Client.PostAsJsonAsync("/api/chat", new ChatRequest
            {
                CharacterId = scenario.Chat.CharacterId,
                RequestId = requestId,
                UserMessage = scenario.Chat.UserMessage,
                TaskTag = scenario.Chat.TaskTag,
            });
            ChatResponse? payload = await chatResponse.Content.ReadFromJsonAsync<ChatResponse>();

            Assert.That(chatResponse.IsSuccessStatusCode, Is.True, $"Chat failed for scenario '{scenario.Name}'.");
            Assert.That(payload, Is.Not.Null);
            Assert.That(payload!.AssistantMessage, Is.Not.Null.And.Not.Empty);

            OutboxEnvelope envelope = await WaitForOutboxEnvelopeAsync(options.BridgeOutboxDir, requestId);

            Assert.That(envelope.EventType, Is.EqualTo("chat_reply"));
            Assert.That(envelope.Payload.RequestId, Is.EqualTo(requestId));
            Assert.That(envelope.Payload.Presentation.StrategyId, Is.EqualTo(scenario.ExpectedStrategyId));
            Assert.That(envelope.Payload.Presentation.Surface.FamilyId, Is.EqualTo(scenario.ExpectedFamilyId));
            Assert.That(envelope.Payload.Presentation.Surface.LayoutMode, Is.EqualTo(scenario.ExpectedLayoutMode));
            Assert.That(envelope.Payload.Presentation.Source, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Summary, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Audio.BehaviorId, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Audio.SubtitleStyle, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Visual.BehaviorId, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Visual.HudAccent, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Visual.WorldMarker, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Visual.ScreenTreatment, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Visual.PortraitExpression, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.PathBadge, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.FamilyBadge, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.PhaseBadge, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.PrimaryTitle, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.CueTitle, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.ReadoutTitle, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.SupportTitle, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.ActionPreviewTitle, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.ActionFeedbackTitle, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.FollowupOrder, Has.Count.EqualTo(3));
            Assert.That(envelope.Payload.Presentation.Surface.HeaderTokens, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.CueTokens, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.FocusTokens, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.StatusTokens, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.StageTokens, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.AtmosphereTokens, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.FooterTokens, Is.Not.Empty);
            Assert.That(envelope.Payload.Presentation.Surface.CardBudget, Is.InRange(1, 3));
            Assert.That(envelope.Payload.Presentation.Surface.PrimaryCueTokenCount, Is.InRange(0, 2));
            Assert.That(envelope.Payload.Presentation.Surface.PrimaryFocusTokenCount, Is.InRange(0, 2));
            Assert.That(envelope.Payload.Presentation.Surface.PrimaryStatusTokenCount, Is.InRange(0, 2));
            Assert.That(envelope.Payload.Presentation.Surface.PrimaryStageTokenCount, Is.InRange(0, 1));
            Assert.That(envelope.Payload.Presentation.Surface.PrimaryAtmosphereTokenCount, Is.InRange(0, 1));
            Assert.That(envelope.Payload.Presentation.Surface.WidthChars, Is.GreaterThan(0));
            Assert.That(envelope.Payload.Presentation.Surface.MaxBodyLines, Is.GreaterThan(0));
            Assert.That(envelope.Payload.Presentation.Surface.PrimaryDurationMs, Is.GreaterThan(0));
            Assert.That(envelope.Payload.Presentation.Surface.FollowupDurationMs, Is.GreaterThan(0));

            strategyIds.Add(envelope.Payload.Presentation.StrategyId);
            surfaceFamilies.Add(envelope.Payload.Presentation.Surface.FamilyId);
            audioBehaviors.Add(envelope.Payload.Presentation.Audio.BehaviorId);
            visualBehaviors.Add(envelope.Payload.Presentation.Visual.BehaviorId);
        }

        Assert.That(strategyIds.Distinct().Count(), Is.EqualTo(5),
            "The replay harness should cover five distinct strategy families called out by the roadmap.");
        Assert.That(surfaceFamilies.Distinct().Count(), Is.GreaterThanOrEqualTo(4),
            "The deterministic replay set should span multiple surface families even before live inference-specific moods are added.");
        Assert.That(audioBehaviors.Distinct().Count(), Is.EqualTo(5),
            "Each replay scenario should preserve a distinct audio behavior id for renderer and TTS coordination.");
        Assert.That(visualBehaviors.Distinct().Count(), Is.EqualTo(5),
            "Each replay scenario should preserve a distinct visual behavior id for in-game delivery tuning.");
    }

    private static Task WriteProofBundleArchiveAsync(
        string archivePath,
        ReleaseProofBundleEvidenceSnapshot manifest,
        IEnumerable<string>? extraEntries = null) =>
        WriteBundleArchiveAsync(
            archivePath,
            "proof-bundle.json",
            JsonSerializer.Serialize(manifest),
            manifest.IncludedFiles,
            extraEntries);

    private static Task WriteSupportBundleArchiveAsync(
        string archivePath,
        ReleaseSupportBundleEvidenceSnapshot manifest,
        IEnumerable<string>? extraEntries = null) =>
        WriteBundleArchiveAsync(
            archivePath,
            "support-bundle.json",
            JsonSerializer.Serialize(manifest),
            manifest.IncludedFiles,
            extraEntries);

    private static Task WriteFullAuditResultsAsync(string resultsPath, string overall)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
        return File.WriteAllTextAsync(
            resultsPath,
            $"""
            # PalLLM Full-Audit Results

            - Generated: `20260422-220000` UTC
            - Overall: **{overall}**

            """);
    }

    private static async Task WriteFullAuditStepLogsAsync(string stepsDirectoryPath, int count)
    {
        Directory.CreateDirectory(stepsDirectoryPath);
        for (int index = 1; index <= count; index++)
        {
            await File.WriteAllTextAsync(
                Path.Combine(stepsDirectoryPath, $"{index:D2}_Step_{index}.log"),
                "PASS");
        }
    }

    private static async Task WriteBundleArchiveWithManifestOnlyAsync(
        string archivePath,
        string manifestEntryName,
        string manifestJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry(manifestEntryName);
        using Stream stream = entry.Open();
        using StreamWriter writer = new(stream);
        await writer.WriteAsync(manifestJson);
    }

    private static async Task WriteBundleArchiveAsync(
        string archivePath,
        string manifestEntryName,
        string manifestJson,
        IEnumerable<string> includedFiles,
        IEnumerable<string>? extraEntries = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        string[] entries = includedFiles
            .Append(manifestEntryName)
            .Concat(extraEntries ?? Array.Empty<string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Replace('\\', '/').Trim().TrimStart('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (string entryName in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            using Stream stream = entry.Open();
            using StreamWriter writer = new(stream);
            await writer.WriteAsync(string.Equals(entryName, manifestEntryName, StringComparison.Ordinal)
                ? manifestJson
                : "{}");
        }
    }

    private static async Task<OutboxEnvelope> WaitForOutboxEnvelopeAsync(
        string outboxDirectory,
        string requestId,
        int timeoutMilliseconds = 4000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMilliseconds));
        while (DateTime.UtcNow < deadline)
        {
            foreach (string file in Directory.GetFiles(outboxDirectory, "*.json"))
            {
                OutboxEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<OutboxEnvelope>(await File.ReadAllTextAsync(file));
                }
                catch
                {
                    continue;
                }

                if (string.Equals(envelope?.Payload.RequestId, requestId, StringComparison.Ordinal))
                {
                    return envelope!;
                }
            }

            await Task.Delay(150);
        }

        throw new AssertionException($"No outbox envelope matching request id '{requestId}' was observed in {outboxDirectory}.");
    }

    private static DeliveryReplayScenario[] BuildDeliveryReplayScenarios() =>
    [
        new DeliveryReplayScenario
        {
            Name = "camp",
            ExpectedStrategyId = "crafting-discipline",
            ExpectedFamilyId = "base",
            ExpectedLayoutMode = "operations_panel",
            Snapshot = new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                IsInBase = true,
                WorldName = "Palpagos",
                TimeOfDay = "night",
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 9101,
                        DisplayName = "CampScout",
                        Species = "CampScout",
                    },
                ],
            },
            Chat = new ChatRequest
            {
                CharacterId = 9101,
                UserMessage = "How should we prepare this camp for the night?",
                TaskTag = "chat_camp",
            },
        },
        new DeliveryReplayScenario
        {
            Name = "stealth",
            ExpectedStrategyId = "stealth-shadow",
            ExpectedFamilyId = "stealth",
            ExpectedLayoutMode = "stealth_whisper",
            Snapshot = new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                WorldName = "Palpagos",
                NearbyHostiles = ["Syndicate Thug"],
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 9102,
                        DisplayName = "Direhowl",
                        Species = "Direhowl",
                    },
                ],
            },
            Chat = new ChatRequest
            {
                CharacterId = 9102,
                UserMessage = "Give me a stealth route past these guards.",
                TaskTag = "chat_stealth",
            },
        },
        new DeliveryReplayScenario
        {
            Name = "triage",
            ExpectedStrategyId = "emergency-triage",
            ExpectedFamilyId = "combat",
            ExpectedLayoutMode = "combat_alert",
            Snapshot = new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                WorldName = "Palpagos",
                ThreatLevel = 0.9f,
                AlertLevel = 0.9f,
                NearbyHostiles = ["Rayhound", "Syndicate Gunner", "Incineram"],
                RecentEvents = ["base_raid"],
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 9103,
                        DisplayName = "Mammorest",
                        Species = "Mammorest",
                        HealthFraction = 0.35f,
                        NearbyEnemyCount = 4,
                        RecentDamageFraction = 0.6f,
                    },
                ],
            },
            Chat = new ChatRequest
            {
                CharacterId = 9103,
                UserMessage = "What do we do right now?",
                TaskTag = "chat_defense",
            },
        },
        new DeliveryReplayScenario
        {
            Name = "travel",
            ExpectedStrategyId = "safe-travel",
            ExpectedFamilyId = "travel",
            ExpectedLayoutMode = "route_strip",
            Snapshot = new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                WorldName = "Palpagos",
                KnownBases =
                [
                    new GameBaseSnapshot
                    {
                        BaseId = "Verdant Hub",
                        AreaRange = 42.5f,
                    },
                ],
                LastTravel = new TravelStatusSnapshot
                {
                    Origin = "Verdant Hub",
                    Destination = "Alpha Tower",
                    Waypoint = "Obsidian Outpost",
                    Mode = "guided_route",
                },
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 9104,
                        DisplayName = "Nitewing",
                        Species = "Nitewing",
                    },
                ],
            },
            Chat = new ChatRequest
            {
                CharacterId = 9104,
                UserMessage = "How should we travel to the tower?",
                TaskTag = "chat_travel",
            },
        },
        new DeliveryReplayScenario
        {
            Name = "base-network",
            ExpectedStrategyId = "base-network",
            ExpectedFamilyId = "base",
            ExpectedLayoutMode = "operations_panel",
            Snapshot = new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                IsInBase = true,
                WorldName = "Palpagos",
                ActiveBaseIds = ["Verdant Hub", "Obsidian Outpost"],
                KnownBases =
                [
                    new GameBaseSnapshot
                    {
                        BaseId = "Verdant Hub",
                        AreaRange = 42.5f,
                    },
                    new GameBaseSnapshot
                    {
                        BaseId = "Obsidian Outpost",
                        AreaRange = 31f,
                    },
                ],
                LastProduction = new ProductionStatusSnapshot
                {
                    BaseId = "Verdant Hub",
                    Station = "assembly_line",
                    Item = "advanced_sphere",
                    Quantity = 2,
                    Status = "queued",
                },
                Characters =
                [
                    new GameCharacterSnapshot
                    {
                        Id = 9105,
                        DisplayName = "Anubis",
                        Species = "Anubis",
                    },
                ],
            },
            Chat = new ChatRequest
            {
                CharacterId = 9105,
                UserMessage = "How should we split work between our bases?",
                TaskTag = "chat_bases",
            },
        },
    ];

    private sealed class DeliveryReplayScenario
    {
        public string Name { get; init; } = string.Empty;

        public string ExpectedStrategyId { get; init; } = string.Empty;

        public string ExpectedFamilyId { get; init; } = string.Empty;

        public string ExpectedLayoutMode { get; init; } = string.Empty;

        public GameWorldSnapshot Snapshot { get; init; } = new();

        public ChatRequest Chat { get; init; } = new();
    }

    [Test]
    public async Task ChatEndpoint_FullActionCycle_DrivesOutboxActionIntoLuaSimulatedFeedback()
    {
        // Full loop: chat -> outbox carries ActionIntent -> simulated Lua writes a
        // travel feedback event into Bridge/Inbox -> next chat drains the feedback
        // and the trace tags (request id + source strategy) arrive in memory. This
        // is the sidecar-level stand-in for the Palworld-side executor path; when
        // the real Lua executor emits feedback through the bridge, the trace must
        // survive end-to-end. Guards the promise in IMPLEMENTATION_QUEUE.md Queue 6.
        await using var fixture = new SidecarTestFixture();

        using IServiceScope scope = fixture.Factory.Services.CreateScope();
        PalLlmRuntime runtime = scope.ServiceProvider.GetRequiredService<PalLlmRuntime>();
        PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();

        // Enable automation and the operator allowlist so a safe intent emits.
        options.Automation.Enabled = true;
        options.Automation.EmitToOutbox = true;
        if (!options.Automation.AllowedActions.Contains("waypoint_suggest"))
        {
            options.Automation.AllowedActions.Add("waypoint_suggest");
        }

        runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
            ActiveBaseIds = ["FortVerdant"],
            KnownBases =
            [
                new GameBaseSnapshot { BaseId = "FortVerdant", AreaRange = 40f },
                new GameBaseSnapshot { BaseId = "RidgeCamp", AreaRange = 30f },
            ],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 22,
                    DisplayName = "CampScout",
                    Species = "CampScout",
                },
            ],
        });

        HttpResponseMessage travelAsk = await fixture.Client.PostAsJsonAsync("/api/chat", new ChatRequest
        {
            CharacterId = 22,
            RequestId = "cycle-chat-001",
            UserMessage = "Plot a safe route to RidgeCamp.",
            TaskTag = "chat_travel",
        });
        ChatResponse? travelReply = await travelAsk.Content.ReadFromJsonAsync<ChatResponse>();

        Assert.That(travelAsk.IsSuccessStatusCode, Is.True);
        Assert.That(travelReply, Is.Not.Null);
        Assert.That(travelReply!.Action, Is.Not.Null, "Waypoint suggest should emit when allowlisted.");
        Assert.That(travelReply.Action!.Type, Is.EqualTo("waypoint_suggest"));
        Assert.That(travelReply.FallbackStrategy, Is.EqualTo("safe-travel"));

        string outboxFile = Directory.GetFiles(options.BridgeOutboxDir, "*.json").Single();
        OutboxEnvelope? envelope = JsonSerializer.Deserialize<OutboxEnvelope>(
            await File.ReadAllTextAsync(outboxFile));
        Assert.That(envelope, Is.Not.Null);
        Assert.That(envelope!.Payload.Action, Is.Not.Null);
        Assert.That(envelope.Payload.Action!.Type, Is.EqualTo("waypoint_suggest"));

        // Simulate the Lua guarded executor writing a travel feedback event back
        // through the bridge. The request id + source strategy are how operators
        // trace in-game execution back to the originating chat turn.
        string feedbackPath = Path.Combine(options.BridgeInboxDir, "travel-feedback-001.json");
        await File.WriteAllTextAsync(feedbackPath, JsonSerializer.Serialize(new
        {
            EventType = "travel",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Origin = "FortVerdant",
                Destination = "RidgeCamp",
                Waypoint = "",
                Mode = "guided_route",
                Note = "cycle executor feedback",
                RequestId = "cycle-chat-001",
                SourceStrategy = "safe-travel",
            },
        }));

        // A follow-up chat triggers the pre-chat bridge sync so the feedback event
        // lands in memory before the next plan is built.
        HttpResponseMessage followUp = await fixture.Client.PostAsJsonAsync("/api/chat", new ChatRequest
        {
            CharacterId = 22,
            RequestId = "cycle-chat-002",
            UserMessage = "Status update?",
            TaskTag = "chat_status",
        });
        Assert.That(followUp.IsSuccessStatusCode, Is.True);

        Assert.That(runtime.GetWorldState().Snapshot.LastTravel, Is.Not.Null);
        Assert.That(runtime.GetWorldState().Snapshot.LastTravel!.SourceStrategy, Is.EqualTo("safe-travel"));

        ConversationMemoryEntry? travelMemory = runtime.MemoryStore.Export()
            .FirstOrDefault(entry => entry.Tags.Contains("travel") && entry.Tags.Contains("request:cycle-chat-001"));
        Assert.That(travelMemory, Is.Not.Null,
            "Lua-simulated travel feedback must survive into memory with the originating request id.");
        Assert.That(travelMemory!.Tags, Does.Contain("strategy:safe-travel"));
        Assert.That(travelMemory.Content, Does.Contain("cycle executor feedback"));
        Assert.That(travelMemory.Content, Does.Not.Contain("Ã¢â‚¬"),
            "Travel feedback memory must not carry mojibake separators.");
    }

    [Test]
    public async Task Auth_NoApiKeyConfigured_DefaultsToUnauthenticatedAccess()
    {
        // Default PalLLM posture is localhost-only and unauthenticated. The
        // auth middleware must stay invisible until an operator explicitly
        // configures an ApiKey â€” otherwise we'd break every existing
        // localhost deployment on upgrade.
        await using var fixture = new SidecarTestFixture();

        HttpResponseMessage response = await fixture.Client.GetAsync("/api/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Auth_ApiKeyConfiguredAndMissingHeader_Returns401()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Auth:ApiKey"] = "secret-key-xyz",
        });

        HttpResponseMessage response = await fixture.Client.GetAsync("/api/health");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Headers.WwwAuthenticate.ToString(), Does.Contain("Bearer"),
            "401 response must advertise Bearer scheme via WWW-Authenticate.");
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("title").GetString(), Is.EqualTo("Unauthorized"));
        Assert.That(body.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(401));
        Assert.That(body.RootElement.GetProperty("detail").GetString(), Is.EqualTo("Missing bearer credential."));
        Assert.That(body.RootElement.GetProperty("traceId").GetString(), Is.Not.Empty);
        Assert.That(body.RootElement.GetProperty("instance").GetString(), Is.EqualTo("/api/health"));
    }

    [Test]
    public async Task Auth_ApiKeyConfiguredAndWrongKey_Returns401()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Auth:ApiKey"] = "secret-key-xyz",
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-key");
        HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));

        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.That(body.RootElement.GetProperty("title").GetString(), Is.EqualTo("Unauthorized"));
        Assert.That(body.RootElement.GetProperty("status").GetInt32(), Is.EqualTo(401));
        Assert.That(body.RootElement.GetProperty("detail").GetString(), Is.EqualTo("Invalid bearer credential."));
        Assert.That(body.RootElement.GetProperty("traceId").GetString(), Is.Not.Empty);
        Assert.That(body.RootElement.GetProperty("instance").GetString(), Is.EqualTo("/api/health"));
    }

    [Test]
    public async Task Auth_ApiKeyConfiguredAndCorrectKey_Returns200()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Auth:ApiKey"] = "secret-key-xyz",
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-key-xyz");
        HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task Auth_ApiKeyConfigured_OperationalRoutesStayOpenByDefault()
    {
        // /metrics, /health/live, /health/ready, /openapi/v1.{json,yaml}, and /
        // must remain reachable without a credential so Prometheus, container
        // orchestrators, SDK generators, and the static dashboard keep working
        // in every deployment. Flip ProtectMetrics / ProtectHealth to require
        // the credential on those too.
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Auth:ApiKey"] = "secret-key-xyz",
        });

        Assert.That((await fixture.Client.GetAsync("/metrics")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await fixture.Client.GetAsync("/health/live")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await fixture.Client.GetAsync("/health/ready")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await fixture.Client.GetAsync("/openapi/v1.json")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await fixture.Client.GetAsync("/openapi/v1.yaml")).StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await fixture.Client.GetAsync("/")).StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "Static dashboard root must stay open â€” it's inert HTML/CSS/JS with no data access of its own.");
    }

    [Test]
    public async Task Auth_ProtectMetricsFlag_RequiresBearerOnMetrics()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Auth:ApiKey"] = "secret-key-xyz",
            ["PalLLM:Auth:ProtectMetrics"] = "true",
        });

        HttpResponseMessage noAuth = await fixture.Client.GetAsync("/metrics");
        Assert.That(noAuth.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        using var authed = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        authed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret-key-xyz");
        HttpResponseMessage ok = await fixture.Client.SendAsync(authed);
        Assert.That(ok.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

}

internal sealed class WarmupAwareInferenceClient : IInferenceClient, IInferenceLaneMetadata
{
    public int CallCount { get; private set; }

    public InferencePrompt? LastPrompt { get; private set; }

    public Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken)
    {
        CallCount++;
        LastPrompt = prompt;
        return Task.FromResult(InferenceResult.Succeeded(
            "OK",
            new TokenUsage(1, 1, 2, CachedPromptTokens: 1, CompletionReasoningTokens: 1),
            finishReasons: ["stop"],
            upstreamRequestId: "req-warmup-aware-001",
            upstreamProcessingMs: 16.5,
            upstreamQueueMs: 1.25,
            upstreamTimeToFirstTokenMs: 8.75,
            upstreamPrefillMs: 5.5,
            upstreamDecodeMs: 9.25));
    }

    public string GetActiveModelId() => "warm-model-q4";

    public string? GetActiveTierId() => "small";

    public IReadOnlyList<string> GetLastSeenAvailableModels() => ["warm-model-q4", "warm-model-q8"];
}

internal sealed class ThrowingInferenceClient : IInferenceClient, IInferenceLaneMetadata
{
    public Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(@"raw upstream detail D:\secret\model.bin");

    public string GetActiveModelId() => "throwing-model";

    public string? GetActiveTierId() => "fault";

    public IReadOnlyList<string> GetLastSeenAvailableModels() => ["throwing-model"];
}

internal sealed class SequencedInferenceClient : IInferenceClient, IInferenceLaneMetadata
{
    private readonly Queue<InferenceResult> _results;
    private readonly string _modelId;

    public SequencedInferenceClient(params InferenceResult[] results)
    {
        InferenceResult[] seed = results ?? Array.Empty<InferenceResult>();
        _results = new Queue<InferenceResult>(seed);
        InferenceResult? first = seed.FirstOrDefault();
        _modelId = string.IsNullOrWhiteSpace(first?.ResponseModel)
            ? (string.IsNullOrWhiteSpace(first?.RequestModel) ? "worker-q4" : first.RequestModel)
            : first.ResponseModel;
    }

    public Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken)
    {
        if (_results.Count == 0)
        {
            return Task.FromResult(InferenceResult.Succeeded(
                "OK",
                providerName: "openai_compatible",
                requestModel: _modelId,
                responseModel: _modelId,
                latencyMs: 100));
        }

        return Task.FromResult(_results.Dequeue());
    }

    public string GetActiveModelId() => _modelId;

    public string? GetActiveTierId() => "small";

    public IReadOnlyList<string> GetLastSeenAvailableModels() => [_modelId];
}

internal sealed class EndpointCannedTtsClient : ITtsClient
{
    private readonly Func<TtsRequest, TtsResult> _factory;

    public EndpointCannedTtsClient(Func<TtsRequest, TtsResult> factory)
    {
        _factory = factory;
    }

    public Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(_factory(request));
}
