using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Packs;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

public sealed class RuntimeTests
{
    [Test]
    public void UpdateSnapshot_NormalizesNullCollectionsFromBridgePayload()
    {
        using var fixture = new TestFixtureContext();

        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
            ActiveBaseIds = null!,
            KnownBases = null!,
            NearbyHostiles = null!,
            NearbyFriendlies = null!,
            NearbyResources = null!,
            RecentEvents = null!,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 7,
                    DisplayName = "Lifmunk",
                    Species = "Lifmunk",
                    Position = null!,
                    Skills = null!,
                    Needs = null!,
                    Loadout = null!,
                    RecentEvents = null!,
                    Traits = null!,
                    Tags = null!,
                },
            ],
        });

        GameWorldSnapshot snapshot = fixture.Runtime.Adapter.Snapshot;
        GameCharacterSnapshot character = snapshot.Characters.Single();

        Assert.That(snapshot.ActiveBaseIds, Is.Not.Null.And.Empty);
        Assert.That(snapshot.KnownBases, Is.Not.Null.And.Empty);
        Assert.That(snapshot.NearbyHostiles, Is.Not.Null.And.Empty);
        Assert.That(snapshot.NearbyFriendlies, Is.Not.Null.And.Empty);
        Assert.That(snapshot.NearbyResources, Is.Not.Null.And.Empty);
        Assert.That(snapshot.RecentEvents, Is.Not.Null.And.Empty);
        Assert.That(snapshot.Characters, Has.Count.EqualTo(1));
        Assert.That(character.Position, Is.Not.Null);
        Assert.That(character.Skills, Is.Not.Null.And.Empty);
        Assert.That(character.Needs, Is.Not.Null.And.Empty);
        Assert.That(character.Loadout, Is.Not.Null.And.Empty);
        Assert.That(character.RecentEvents, Is.Not.Null.And.Empty);
        Assert.That(character.Traits, Is.Not.Null.And.Empty);
        Assert.That(character.Tags, Is.Not.Null.And.Empty);
    }

    [Test]
    public void Adapter_ExposesSnapshotCharacters()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 7,
                    DisplayName = "Lifmunk",
                    Species = "Lifmunk",
                    Traits = ["curious"],
                },
            ],
        });

        var characters = fixture.Runtime.Adapter.Characters.ToArray();

        Assert.That(characters, Has.Length.EqualTo(1));
        Assert.That(characters[0].DisplayName, Is.EqualTo("Lifmunk"));
        Assert.That(fixture.Runtime.Adapter.IsReadyForInference, Is.True);
    }

    [Test]
    public void RelationshipTracker_BoundedRetention_EvictsLowestScoreWhenCapExceeded()
    {
        // Long-running sessions with many transient or synthetic character ids
        // must not grow the tracker dictionary forever. Retention prefers recent,
        // high-affinity, high-interaction entries; the cap prunes the low-score
        // tail. Covered as an explicit regression against the known debt noted in
        // docs/ARCHITECTURE.md Â§"Known debt" (closed).
        var tracker = new RelationshipTracker();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        int overflow = 270;
        for (int i = 1; i <= overflow; i++)
        {
            string message = (i % 20 == 0) ? "thank you, brave and loyal" : "hello";
            tracker.RecordInteraction(i, $"npc-{i}", message, now.AddSeconds(i));
        }

        Assert.That(tracker.Count, Is.LessThanOrEqualTo(256),
            "Tracker must enforce its soft cap after heavy churn.");

        // The most recent entry always survives (protectId path).
        Assert.That(tracker.TryGet(overflow), Is.Not.Null,
            "Most recently recorded character must never be evicted.");

        // A very old, low-interaction, zero-affinity entry should be among the evicted.
        Assert.That(tracker.TryGet(1), Is.Null,
            "Oldest zero-affinity, one-interaction entry should have been pruned first.");
    }

    [Test]
    public void ChatRateLimiter_PrunesIdleBucketsEvenWhenCurrentBucketIsRateLimited()
    {
        var limiter = new ChatRateLimiter
        {
            MaxPerMinute = 1,
        };

        Assert.That(limiter.TryAcquire("active"), Is.True);

        var bucketsField = typeof(ChatRateLimiter).GetField("_buckets", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.That(bucketsField, Is.Not.Null);

        var buckets = (Dictionary<string, Queue<DateTimeOffset>>)bucketsField!.GetValue(limiter)!;
        buckets["stale-a"] = new Queue<DateTimeOffset>([DateTimeOffset.UtcNow.AddMinutes(-5)]);
        buckets["stale-b"] = new Queue<DateTimeOffset>([DateTimeOffset.UtcNow.AddMinutes(-10)]);

        Assert.That(limiter.BucketCount, Is.EqualTo(3));

        Assert.That(limiter.TryAcquire("active"), Is.False,
            "Second acquire inside the active one-minute window should still be rate limited.");
        Assert.That(limiter.BucketCount, Is.EqualTo(1),
            "Rate-limited acquires must still prune stale idle buckets so the dictionary does not grow forever.");
    }

    [Test]
    public void MemoryStore_RecallPrefersMatchingCharacter()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.MemoryStore.Remember(1, "Foxparks", "user", "Foxparks likes warm campfires.", "chat");
        fixture.Runtime.MemoryStore.Remember(2, "Lamball", "user", "Lamball enjoys soft wool bedding.", "chat");

        var matches = fixture.Runtime.RecallMemory(new MemoryRecallRequest
        {
            CharacterId = 1,
            Query = "warm fire",
            Limit = 1,
        });

        Assert.That(matches, Has.Count.EqualTo(1));
        Assert.That(matches[0].Entry.CharacterName, Is.EqualTo("Foxparks"));
    }

    [Test]
    public async Task ChatAsync_WithOversizedImportedMemory_ReturnsBoundedMemoryMatches()
    {
        using var fixture = new TestFixtureContext();
        string oversizedMemory = "campfire route " + new string('x', ConversationMemoryStore.MaxContentChars + 4096);
        fixture.Runtime.MemoryStore.Import(
        [
            new ConversationMemoryEntry
            {
                CharacterId = 1,
                CharacterName = "Foxparks",
                SpeakerRole = "user",
                Content = oversizedMemory,
                Tags = ["chat"],
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Importance = 1.0f,
            },
        ]);

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 1,
            UserMessage = "Which campfire route should we use?",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.SystemPrompt.Length, Is.LessThanOrEqualTo(16_000));
        Assert.That(response.MemoryMatches, Has.Count.EqualTo(1));
        Assert.That(response.MemoryMatches[0].Length, Is.LessThanOrEqualTo(ConversationMemoryStore.MaxContentChars));
        Assert.That(response.MemoryMatches[0], Does.EndWith("..."));
    }

    [Test]
    public async Task ChatAsync_WithOversizedLiveInferenceReply_BoundsReturnedAndStoredAssistantMessage()
    {
        string oversizedReply = "route plan " + new string('x', PalLlmRuntime.AssistantMessageHardCapChars + 4096);
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded(
            oversizedReply,
            new TokenUsage(128, 4096, 4224),
            requestModel: "oversized-reply-model",
            responseModel: "oversized-reply-model"));
        using var fixture = new TestFixtureContext(inferenceClient: inferenceClient, inferenceEnabled: true);
        fixture.Options.Fallback.EnablePolicyBypass = false;

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Give me a detailed route plan.",
            TaskTag = "chat_travel",
        }, CancellationToken.None);

        ConversationMemoryEntry assistantMemory = fixture.Runtime.MemoryStore.Export()
            .Last(entry => string.Equals(entry.SpeakerRole, "assistant", StringComparison.Ordinal));

        Assert.That(response.ResponsePath, Is.EqualTo("live_inference"));
        Assert.That(response.UsedFallback, Is.False);
        Assert.That(response.AssistantMessage, Is.Not.Null);
        Assert.That(response.AssistantMessage!.Length, Is.LessThanOrEqualTo(PalLlmRuntime.AssistantMessageHardCapChars));
        Assert.That(response.AssistantMessage, Does.EndWith("..."));
        Assert.That(response.StatusMessage, Does.Contain(PalLlmRuntime.AssistantMessageHardCapChars.ToString()));
        Assert.That(assistantMemory.Content, Does.Not.Contain(oversizedReply));
        Assert.That(assistantMemory.Content, Does.EndWith("..."));
        Assert.That(assistantMemory.Content.Length, Is.LessThanOrEqualTo(ConversationMemoryStore.MaxContentChars));
    }

    [Test]
    public async Task ChatAsync_WithOversizedDirectUserMessage_BoundsPromptAndStoredUserMemory()
    {
        string oversizedMessage = "Give me a detailed route plan. " + new string('x', ChatRequest.UserMessageMaxLength + 4096);
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("Keep the route short and safe."));
        using var fixture = new TestFixtureContext(inferenceClient: inferenceClient, inferenceEnabled: true);
        fixture.Options.Fallback.EnablePolicyBypass = false;

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = oversizedMessage,
            TaskTag = "chat_travel",
        }, CancellationToken.None);

        ConversationMemoryEntry userMemory = fixture.Runtime.MemoryStore.Export()
            .Last(entry => string.Equals(entry.SpeakerRole, "user", StringComparison.Ordinal));

        Assert.That(response.ResponsePath, Is.EqualTo("live_inference"));
        Assert.That(inferenceClient.LastPrompt, Is.Not.Null);
        Assert.That(inferenceClient.LastPrompt!.UserPrompt.Length, Is.LessThanOrEqualTo(ChatRequest.UserMessageMaxLength));
        Assert.That(inferenceClient.LastPrompt.UserPrompt, Does.EndWith("..."));
        Assert.That(response.StatusMessage, Does.Contain(ChatRequest.UserMessageMaxLength.ToString()));
        Assert.That(userMemory.Content.Length, Is.LessThanOrEqualTo(ConversationMemoryStore.MaxContentChars));
        Assert.That(userMemory.Content, Does.EndWith("..."));
    }

    [Test]
    public void Adapter_SnapshotIsolation_BlocksExternalMutation()
    {
        using var fixture = new TestFixtureContext();
        var snapshot = new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            ActiveBaseIds = ["Verdant Hub"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 8,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                    Traits = ["calm"],
                },
            ],
        };

        fixture.Runtime.UpdateSnapshot(snapshot);

        snapshot.ActiveBaseIds.Add("Injected Base");
        snapshot.Characters[0].Traits.Add("tampered");

        RuntimeWorldState firstRead = fixture.Runtime.GetWorldState();
        firstRead.Snapshot.ActiveBaseIds.Add("Mutated Read");
        firstRead.Snapshot.Characters[0].Traits.Add("read-tamper");

        RuntimeWorldState secondRead = fixture.Runtime.GetWorldState();

        Assert.That(secondRead.Snapshot.ActiveBaseIds, Is.EqualTo(new[] { "Verdant Hub" }));
        Assert.That(secondRead.Snapshot.Characters[0].Traits, Is.EqualTo(new[] { "calm" }));
    }

    [Test]
    public void GetDashboardSnapshot_OrdersAndTrimsInterfaceCollections()
    {
        using var fixture = new TestFixtureContext();
        DateTime baseTime = new(2026, 4, 17, 1, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < 4; i++)
        {
            fixture.Runtime.Adapter.Logger.Info($"log-{i}");
        }

        for (int i = 0; i < 5; i++)
        {
            string path = Path.Combine(fixture.Options.BridgeOutboxDir, $"reply-{i}.json");
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, baseTime.AddMinutes(i));
        }

        DashboardSnapshot dashboard = fixture.Runtime.GetDashboardSnapshot(logLimit: 3, outboxLimit: 2);

        Assert.That(dashboard.ServerLatencyMs, Is.GreaterThanOrEqualTo(0));
        Assert.That(dashboard.Logs.Select(entry => entry.Message), Is.EqualTo(new[] { "log-3", "log-2", "log-1" }));
        Assert.That(dashboard.Outbox.Select(entry => entry.FileName), Is.EqualTo(new[] { "reply-4.json", "reply-3.json" }));
        Assert.That(dashboard.Health, Is.Not.Null);
        Assert.That(dashboard.World, Is.Not.Null);
    }

    [Test]
    public async Task GetInferencePerformanceSnapshot_GroupsRecentLiveOperationsByModelLane()
    {
        using var fixture = new TestFixtureContext(
            inferenceClient: new CountingInferenceClient(() => InferenceResult.Succeeded(
                "All clear.",
                new TokenUsage(12, 8, 20, 6, 0, 3, 0, 4, 1),
                providerName: "openai_compatible",
                requestModel: "worker-q4",
                responseModel: "worker-q4",
                latencyMs: 142,
                systemFingerprint: "fp_worker_001",
                responseId: "chatcmpl-worker-001",
                finishReasons: ["stop"],
                upstreamRequestId: "req-runtime-worker-001",
                upstreamProcessingMs: 88.5,
                upstreamQueueMs: 4.25,
                upstreamTimeToFirstTokenMs: 31.5,
                upstreamPrefillMs: 20.75,
                upstreamDecodeMs: 50.25)),
            inferenceEnabled: true);
        fixture.Options.Fallback.EnablePolicyBypass = false;

        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 14,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 14,
            RequestId = "runtime-perf-001",
            UserMessage = "Give me a quick status check.",
            TaskTag = "chat_status",
        }, CancellationToken.None);

        InferencePerformanceSnapshot snapshot = fixture.Runtime.GetInferencePerformanceSnapshot();
        DashboardSnapshot dashboard = fixture.Runtime.GetDashboardSnapshot();

        Assert.That(snapshot.SampleCount, Is.EqualTo(1));
        Assert.That(snapshot.SuccessCount, Is.EqualTo(1));
        Assert.That(snapshot.TotalTokens, Is.EqualTo(20));
        Assert.That(snapshot.TotalCachedPromptTokens, Is.EqualTo(6));
        Assert.That(snapshot.TotalCompletionReasoningTokens, Is.EqualTo(3));
        Assert.That(snapshot.TotalAcceptedPredictionTokens, Is.EqualTo(4));
        Assert.That(snapshot.TotalRejectedPredictionTokens, Is.EqualTo(1));
        Assert.That(snapshot.Lanes, Has.Count.EqualTo(1));
        Assert.That(snapshot.Lanes[0].Model, Is.EqualTo("worker-q4"));
        Assert.That(snapshot.Lanes[0].OperationName, Is.EqualTo("chat"));
        Assert.That(snapshot.Lanes[0].LastResponseId, Is.EqualTo("chatcmpl-worker-001"));
        Assert.That(snapshot.Lanes[0].LastUpstreamRequestId, Is.EqualTo("req-runtime-worker-001"));
        Assert.That(snapshot.Lanes[0].LastUpstreamProcessingMs, Is.EqualTo(88.5));
        Assert.That(snapshot.Lanes[0].LastUpstreamQueueMs, Is.EqualTo(4.25));
        Assert.That(snapshot.Lanes[0].LastUpstreamTimeToFirstTokenMs, Is.EqualTo(31.5));
        Assert.That(snapshot.Lanes[0].LastUpstreamPrefillMs, Is.EqualTo(20.75));
        Assert.That(snapshot.Lanes[0].LastUpstreamDecodeMs, Is.EqualTo(50.25));
        Assert.That(snapshot.Lanes[0].LastSystemFingerprint, Is.EqualTo("fp_worker_001"));
        Assert.That(snapshot.Lanes[0].LastFinishReasons, Is.EqualTo(new[] { "stop" }));
        Assert.That(snapshot.Lanes[0].AverageLatencyMs, Is.EqualTo(142));
        Assert.That(snapshot.Lanes[0].LastPromptTokens, Is.EqualTo(12));
        Assert.That(snapshot.Lanes[0].LastCompletionTokens, Is.EqualTo(8));
        Assert.That(snapshot.Lanes[0].LastTotalTokens, Is.EqualTo(20));
        Assert.That(snapshot.Lanes[0].LastCachedPromptTokens, Is.EqualTo(6));
        Assert.That(snapshot.Lanes[0].LastCompletionReasoningTokens, Is.EqualTo(3));
        Assert.That(snapshot.Lanes[0].LastAcceptedPredictionTokens, Is.EqualTo(4));
        Assert.That(snapshot.Lanes[0].LastRejectedPredictionTokens, Is.EqualTo(1));
        Assert.That(snapshot.Assessment.Status, Is.EqualTo("insufficient_data"));
        Assert.That(snapshot.Assessment.TargetHitRatioPercent, Is.EqualTo(100));
        Assert.That(snapshot.Lanes[0].Assessment.Status, Is.EqualTo("insufficient_data"));
        Assert.That(dashboard.InferencePerformance.SampleCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ChatAsync_WhenActivityListenerRegistered_EmitsPalChatSpanWithResponsePathTag()
    {
        // When the Sidecar's OpenTelemetry wiring registers an OTLP
        // exporter, the ActivityListener it installs is what turns
        // PalLlmTelemetry.Source.StartActivity from a no-op into a live
        // span. This test stands in for that listener to prove the
        // runtime actually emits spans on the chat path and tags them
        // with the fallback decision so operators can query "fraction of
        // chats that went inference vs fallback" in a tracing backend.
        ConcurrentBag<Activity> captured = new();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == PalLlmTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured.Add(activity),
        };
        ActivitySource.AddActivityListener(listener);

        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 11,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 11,
            UserMessage = "What should we do next?",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        Activity? span = captured.SingleOrDefault(a => a.OperationName == "pal.chat");
        Assert.That(span, Is.Not.Null, "PalLlmTelemetry must emit a pal.chat span for every ChatAsync call.");
        Assert.That(span!.GetTagItem("pal.response_path"), Is.EqualTo(response.ResponsePath));
        Assert.That(span.GetTagItem("pal.used_fallback"), Is.EqualTo(response.UsedFallback));
        Assert.That(span.GetTagItem("pal.task_tag"), Is.EqualTo("chat_camp"));
        Assert.That(span.GetTagItem("pal.character_id"), Is.EqualTo(11));
        Assert.That(span.GetTagItem("pal.request_id"), Is.Not.Null);
    }

    [Test]
    public async Task ChatAsync_WhenNoActivityListenerRegistered_StartActivityIsCheapNoOp()
    {
        // The localhost-default posture: no OTLP endpoint, no listener,
        // no span. PalLlmTelemetry.Source.StartActivity returns null so
        // the `using Activity? = ...` in PalLlmRuntime is essentially a
        // single branch. This test locks in that promise â€” if someone
        // later adds an always-on listener (e.g. hardcoded in Domain), a
        // zero-overhead claim in the docs would become a lie.
        using var fixture = new TestFixtureContext();
        Assert.That(
            PalLlmTelemetry.Source.HasListeners(),
            Is.False,
            "Domain must not register an ActivityListener itself â€” only the hosting layer (Sidecar) should, and only when OTEL_EXPORTER_OTLP_ENDPOINT is set.");

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Just checking in.",
        }, CancellationToken.None);
        Assert.That(response.AssistantMessage, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task WarmInferenceAsync_WhenRecentLiveInferenceAlreadyHitSameModel_SkipsPeriodicKeepalive()
    {
        var inferenceClient = new CountingInferenceClient(
            () => InferenceResult.Succeeded("We should fortify the ridge before nightfall."));
        using var fixture = new TestFixtureContext(inferenceClient: inferenceClient, inferenceEnabled: true);
        fixture.Options.Fallback.EnablePolicyBypass = false;
        fixture.Options.Inference.EnableWarmup = true;
        fixture.Options.Inference.WarmupIntervalSeconds = 300;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 12,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 12,
            UserMessage = "Give me a concise three-step base-defense plan.",
            TaskTag = "chat_defense",
        }, CancellationToken.None);

        InferenceWarmupSnapshot afterChat = fixture.Runtime.GetInferenceWarmupSnapshot();
        InferenceWarmupSnapshot skipped = await fixture.Runtime.WarmInferenceAsync(
            "periodic_keepalive",
            force: false,
            CancellationToken.None);

        Assert.That(response.ResponsePath, Is.EqualTo("live_inference"));
        Assert.That(afterChat.LastLiveInferenceAtUtc, Is.Not.Null);
        Assert.That(afterChat.LastLiveInferenceModel, Is.EqualTo(response.InferenceModel));
        Assert.That(inferenceClient.CallCount, Is.EqualTo(1),
            "Periodic keepalive should not issue a second POST when a real chat just kept the same model warm.");
        Assert.That(skipped.Status, Is.EqualTo("ready"));
        Assert.That(skipped.LastReason, Is.EqualTo("periodic_keepalive"));
        Assert.That(skipped.StatusMessage, Does.Contain("skipping keepalive"));
    }

    [Test]
    public async Task WarmInferenceAsync_WhenActiveModelChangesAfterLiveInference_DoesNotSkipPeriodicKeepalive()
    {
        var inferenceClient = new CountingInferenceClient(
            () => InferenceResult.Succeeded("The route is clear."));
        using var fixture = new TestFixtureContext(inferenceClient: inferenceClient, inferenceEnabled: true);
        fixture.Options.Fallback.EnablePolicyBypass = false;
        fixture.Options.Inference.EnableWarmup = true;
        fixture.Options.Inference.WarmupIntervalSeconds = 300;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 13,
                    DisplayName = "Direhowl",
                    Species = "Direhowl",
                },
            ],
        });

        _ = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 13,
            UserMessage = "Map out a quiet scouting route for me.",
            TaskTag = "chat_stealth",
        }, CancellationToken.None);

        fixture.Options.Inference.Model = "gemma3:4b";

        InferenceWarmupSnapshot snapshot = await fixture.Runtime.WarmInferenceAsync(
            "periodic_keepalive",
            force: false,
            CancellationToken.None);

        Assert.That(inferenceClient.CallCount, Is.EqualTo(2),
            "A keepalive still needs to run after the active model changes, even if a different model was hit recently.");
        Assert.That(snapshot.LastReason, Is.EqualTo("periodic_keepalive"));
        Assert.That(snapshot.LastWarmedModel, Is.EqualTo("gemma3:4b"));
        Assert.That(snapshot.StatusMessage, Does.Contain("Warmup completed"));
    }

    [Test]
    public async Task ChatAsync_WhenInferenceDisabled_ReturnsPreparedPrompt()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            TimeOfDay = "night",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 11,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                    Traits = ["calm", "loyal"],
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 11,
            UserMessage = "How should we prepare this camp for the night?",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        Assert.That(response.InferenceEnabled, Is.False);
        Assert.That(response.InferenceAttempted, Is.False);
        Assert.That(response.InferenceBypassed, Is.False);
        Assert.That(response.UsedFallback, Is.True);
        Assert.That(response.ResponsePath, Is.EqualTo("fallback_inference_disabled"));
        Assert.That(response.AssistantMessage, Is.Not.Null.And.Not.Empty);
        Assert.That(response.FallbackStrategy, Is.EqualTo("crafting-discipline"));
        Assert.That(response.Presentation.Source, Is.EqualTo("fallback_inference_disabled"));
        Assert.That(response.Presentation.StrategyId, Is.EqualTo("crafting-discipline"));
        Assert.That(response.Presentation.Audio.BehaviorId, Is.EqualTo("workbench-checklist-chime"));
        Assert.That(response.Presentation.Visual.BehaviorId, Is.EqualTo("maintenance-highlight"));
        Assert.That(response.Presentation.Surface.LayoutMode, Is.EqualTo("operations_panel"));
        Assert.That(response.Presentation.Surface.PrimaryTitle, Is.EqualTo("== OPERATIONS PANEL =="));
        Assert.That(response.Presentation.Surface.CueTitle, Is.EqualTo("[Operations Cues]"));
        Assert.That(response.Presentation.Surface.ReadoutTitle, Is.EqualTo("[Operations Readout]"));
        Assert.That(response.Presentation.Surface.SupportTitle, Is.EqualTo("[Task Suggestion]"));
        Assert.That(response.Presentation.Surface.ActionPreviewTitle, Is.EqualTo("[Operations Action]"));
        Assert.That(response.Presentation.Surface.ActionFeedbackTitle, Is.EqualTo("[Operations Result]"));
        Assert.That(response.Presentation.Surface.HeaderTokens, Is.Not.Empty);
        Assert.That(response.Presentation.Surface.CueTokens, Has.Some.StartsWith("Subtitle "));
        Assert.That(response.Presentation.Surface.FocusTokens, Has.Some.EqualTo("Base On Site"));
        Assert.That(response.Presentation.Surface.StatusTokens, Has.Some.StartsWith("Morale "));
        Assert.That(response.Presentation.Surface.StageTokens, Has.Some.StartsWith("Marker "));
        Assert.That(response.Presentation.Surface.AtmosphereTokens, Has.Some.StartsWith("Delivery "));
        Assert.That(response.Presentation.Surface.FooterTokens, Has.Some.EqualTo("Crafting Discipline"));
        Assert.That(response.Presentation.Surface.FollowupOrder, Is.EqualTo(new[] { "readout", "support", "cue" }));
        Assert.That(response.Presentation.Surface.CardBudget, Is.EqualTo(2));
        Assert.That(response.Presentation.Surface.PrimaryCueTokenCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryFocusTokenCount, Is.GreaterThanOrEqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryStatusTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryStageTokenCount, Is.EqualTo(0));
        Assert.That(response.Presentation.Surface.PrimaryAtmosphereTokenCount, Is.EqualTo(0));
        Assert.That(response.SystemPrompt, Does.Contain("Chillet"));
        Assert.That(fixture.Runtime.MemoryStore.Count, Is.EqualTo(2));
    }

    [Test]
    public async Task ChatAsync_WhenStealthAsked_UsesStealthFallback()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = ["Syndicate Thug"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 21,
                    DisplayName = "Direhowl",
                    Species = "Direhowl",
                    Traits = ["sharp", "quiet"],
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 21,
            UserMessage = "Give me a stealth route past these guards.",
            TaskTag = "chat_stealth",
        }, CancellationToken.None);

        Assert.That(response.UsedFallback, Is.True);
        Assert.That(response.InferenceBypassed, Is.False);
        Assert.That(response.FallbackStrategy, Is.EqualTo("stealth-shadow"));
        Assert.That(response.FallbackSignals, Contains.Item("stealth"));
        Assert.That(response.AssistantMessage, Does.Contain("your position").IgnoreCase);
        Assert.That(response.Presentation.Audio.BehaviorId, Is.EqualTo("stealth-whisper-callout"));
        Assert.That(response.Presentation.Visual.BehaviorId, Is.EqualTo("stealth-silhouette-ping"));
        Assert.That(response.Presentation.Surface.LayoutMode, Is.EqualTo("stealth_whisper"));
        Assert.That(response.Presentation.Surface.PrimaryTitle, Is.EqualTo("[[ STEALTH THREAD ]]"));
        Assert.That(response.Presentation.Surface.CueTitle, Is.EqualTo("[Shadow Cues]"));
        Assert.That(response.Presentation.Surface.ReadoutTitle, Is.EqualTo("[Quiet Readout]"));
        Assert.That(response.Presentation.Surface.SupportTitle, Is.EqualTo("[Quiet Suggestion]"));
        Assert.That(response.Presentation.Surface.ActionPreviewTitle, Is.EqualTo("[Quiet Action]"));
        Assert.That(response.Presentation.Surface.ActionFeedbackTitle, Is.EqualTo("[Quiet Result]"));
        Assert.That(response.Presentation.Surface.CueTokens, Has.Some.StartsWith("HUD "));
        Assert.That(response.Presentation.Surface.FocusTokens, Has.Some.StartsWith("Threat "));
        Assert.That(response.Presentation.Surface.StatusTokens, Has.Some.StartsWith("Threat "));
        Assert.That(response.Presentation.Surface.StageTokens, Has.Some.StartsWith("Portrait "));
        Assert.That(response.Presentation.Surface.FollowupOrder, Is.EqualTo(new[] { "readout", "cue", "support" }));
        Assert.That(response.Presentation.Surface.CardBudget, Is.EqualTo(2));
        Assert.That(response.Presentation.Surface.PrimaryCueTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryFocusTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryStatusTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryStageTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryAtmosphereTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Audio.Layers, Contains.Item("phase-build-up"));
    }

    [Test]
    public async Task ChatAsync_WhenThreatSpikes_UsesEmergencyFallback()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            ThreatLevel = 0.9f,
            AlertLevel = 0.9f,
            NearbyHostiles = ["Rayhound", "Syndicate Gunner", "Incineram"],
            RecentEvents = ["base_raid"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 31,
                    DisplayName = "Mammorest",
                    Species = "Mammorest",
                    HealthFraction = 0.35f,
                    NearbyEnemyCount = 4,
                    RecentDamageFraction = 0.6f,
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 31,
            UserMessage = "What do we do right now?",
            TaskTag = "chat_defense",
        }, CancellationToken.None);

        Assert.That(response.UsedFallback, Is.True);
        Assert.That(response.FallbackPhase, Is.EqualTo("Peak"));
        Assert.That(response.FallbackStrategy, Is.EqualTo("emergency-triage"));
        Assert.That(response.FallbackSignals, Contains.Item("peak"));
        Assert.That(response.Presentation.Audio.BehaviorId, Is.EqualTo("triage-command-bark"));
        Assert.That(response.Presentation.Visual.BehaviorId, Is.EqualTo("threat-edge-pulse"));
        Assert.That(response.Presentation.Surface.LayoutMode, Is.EqualTo("combat_alert"));
        Assert.That(response.Presentation.Surface.PrimaryTitle, Is.EqualTo("!! ALERT VECTOR !!"));
        Assert.That(response.Presentation.Surface.CueTitle, Is.EqualTo("[Threat Cues]"));
        Assert.That(response.Presentation.Surface.ReadoutTitle, Is.EqualTo("[Threat Readout]"));
        Assert.That(response.Presentation.Surface.SupportTitle, Is.EqualTo("[Immediate Suggestion]"));
        Assert.That(response.Presentation.Surface.ActionPreviewTitle, Is.EqualTo("[Immediate Action]"));
        Assert.That(response.Presentation.Surface.ActionFeedbackTitle, Is.EqualTo("[Immediate Result]"));
        Assert.That(response.Presentation.Surface.FocusTokens, Has.Some.EqualTo("Outnumbered"));
        Assert.That(response.Presentation.Surface.StatusTokens, Has.Some.StartsWith("Hostiles "));
        Assert.That(response.Presentation.Surface.StageTokens, Has.Some.StartsWith("Camera "));
        Assert.That(response.Presentation.Surface.AtmosphereTokens, Has.Some.StartsWith("Music "));
        Assert.That(response.Presentation.Surface.FollowupOrder, Is.EqualTo(new[] { "readout", "cue", "support" }));
        Assert.That(response.Presentation.Surface.CardBudget, Is.EqualTo(3));
        Assert.That(response.Presentation.Surface.PrimaryCueTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryFocusTokenCount, Is.EqualTo(2));
        Assert.That(response.Presentation.Surface.PrimaryStatusTokenCount, Is.EqualTo(2));
        Assert.That(response.Presentation.Surface.PrimaryStageTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryAtmosphereTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Visual.Layers, Contains.Item("phase-peak"));
    }

    [Test]
    public async Task ChatAsync_WhenInferenceEnabledAndRoutineTask_BypassesInference()
    {
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("live reply"));
        using var fixture = new TestFixtureContext(inferenceClient, inferenceEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = ["Syndicate Thug"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 41,
                    DisplayName = "Vanwyrm",
                    Species = "Vanwyrm",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 41,
            UserMessage = "Give me a stealth route past these guards.",
            TaskTag = "chat_stealth",
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(inferenceClient.CallCount, Is.EqualTo(0));
        Assert.That(response.InferenceEnabled, Is.True);
        Assert.That(response.InferenceAttempted, Is.False);
        Assert.That(response.InferenceBypassed, Is.True);
        Assert.That(response.UsedFallback, Is.True);
        Assert.That(response.ResponsePath, Is.EqualTo("fallback_policy_bypass"));
        Assert.That(response.Presentation.Source, Is.EqualTo("fallback_policy_bypass"));
        Assert.That(response.Presentation.Audio.BehaviorId, Is.EqualTo("stealth-whisper-callout"));
        Assert.That(health.InferenceBypassCount, Is.EqualTo(1));
        Assert.That(health.FallbackReplyCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ChatAsync_WhenInferenceEnabledAndMultiBasePlanning_BypassesWithBaseNetworkFallback()
    {
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("live reply"));
        using var fixture = new TestFixtureContext(inferenceClient, inferenceEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
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
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 46,
                    DisplayName = "Anubis",
                    Species = "Anubis",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 46,
            UserMessage = "How should we split work between our bases?",
            TaskTag = "chat_bases",
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(inferenceClient.CallCount, Is.EqualTo(0));
        Assert.That(response.InferenceEnabled, Is.True);
        Assert.That(response.InferenceAttempted, Is.False);
        Assert.That(response.InferenceBypassed, Is.True);
        Assert.That(response.UsedFallback, Is.True);
        Assert.That(response.ResponsePath, Is.EqualTo("fallback_policy_bypass"));
        Assert.That(response.FallbackStrategy, Is.EqualTo("base-network"));
        Assert.That(response.FallbackSignals, Contains.Item("base-logistics"));
        Assert.That(response.AssistantMessage, Does.Contain("Verdant Hub"));
        Assert.That(response.AssistantMessage, Does.Contain("Obsidian Outpost"));
        Assert.That(response.Presentation.Audio.BehaviorId, Is.EqualTo("base-logistics-call"));
        Assert.That(response.Presentation.Visual.BehaviorId, Is.EqualTo("base-network-flow"));
        Assert.That(response.Presentation.Audio.Layers, Contains.Item("known-base-topology"));
        Assert.That(response.Presentation.Visual.Layers, Contains.Item("supply-network-threads"));
        Assert.That(health.InferenceBypassCount, Is.EqualTo(1));
        Assert.That(health.FallbackReplyCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ChatAsync_WhenBaseNetworkFallbackHasRecentProductionState_UsesSpecificProductionLane()
    {
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("live reply"));
        using var fixture = new TestFixtureContext(inferenceClient, inferenceEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
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
                Status = "queued",
            },
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 46,
                    DisplayName = "Anubis",
                    Species = "Anubis",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 46,
            UserMessage = "How should we split work between our bases?",
            TaskTag = "chat_bases",
        }, CancellationToken.None);

        Assert.That(response.FallbackStrategy, Is.EqualTo("base-network"));
        Assert.That(response.AssistantMessage, Does.Contain("Verdant Hub is carrying queued advanced sphere on assembly line"));
        Assert.That(response.AssistantMessage, Does.Contain("keep that lane stable instead of bouncing it between bases"));
    }

    [Test]
    public async Task ChatAsync_WhenInferenceEnabledAndCreativePrompt_UsesLiveInference()
    {
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("I can tell that story."));
        using var fixture = new TestFixtureContext(inferenceClient, inferenceEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            TimeOfDay = "night",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 51,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 51,
            UserMessage = "Tell me a campfire story while we rest at base tonight.",
            TaskTag = "chat_story",
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(inferenceClient.CallCount, Is.EqualTo(1));
        Assert.That(response.InferenceAttempted, Is.True);
        Assert.That(response.InferenceBypassed, Is.False);
        Assert.That(response.UsedFallback, Is.False);
        Assert.That(response.ResponsePath, Is.EqualTo("live_inference"));
        Assert.That(response.AssistantMessage, Is.EqualTo("I can tell that story."));
        Assert.That(response.Presentation.Source, Is.EqualTo("live_inference"));
        Assert.That(response.Presentation.StrategyId, Is.EqualTo("ambient-camp"));
        Assert.That(response.Presentation.Audio.BehaviorId, Is.EqualTo("camp-banter-bed"));
        Assert.That(response.Presentation.Audio.Layers, Contains.Item("night-low-register-landmarks"));
        Assert.That(response.Presentation.Surface.LayoutMode, Is.EqualTo("camp_banner"));
        Assert.That(response.Presentation.Summary, Does.Contain("without spending another LLM turn"));
        Assert.That(health.InferenceSuccessCount, Is.EqualTo(1));
        Assert.That(health.InferenceBypassCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ChatAsync_WhenFastReactiveLaneHasImage_UsesFastProfileAndSkipsLiveVisionCall()
    {
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("Perimeter looks clear."));
        var visionClient = new CannedVisionClient(() => VisionResult.Succeeded("vision path should stay idle"));
        using var fixture = new TestFixtureContext(
            inferenceClient,
            inferenceEnabled: true,
            visionClient: visionClient,
            visionEnabled: true);
        fixture.Options.Inference.Model = "hf.co/unsloth/Qwen3.6-35B-A3B-Instruct-UD-Q4_K_XL-GGUF";
        fixture.Options.Fallback.Enabled = false;
        fixture.Options.Fallback.EnablePolicyBypass = false;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            TimeOfDay = "night",
            Weather = "clear",
            Biome = "temperate plains",
            ActiveBaseIds = ["Verdant Hub", "Obsidian Outpost", "Kindling Hollow"],
            KnownBases =
            [
                new GameBaseSnapshot { BaseId = "Verdant Hub", AreaRange = 42.5f },
                new GameBaseSnapshot { BaseId = "Obsidian Outpost", AreaRange = 31f },
                new GameBaseSnapshot { BaseId = "Kindling Hollow", AreaRange = 28f },
            ],
            NearbyHostiles = ["Syndicate Thug", "Direhowl", "Tocotoco"],
            NearbyResources = ["Stone", "Wood", "Ore"],
            RecentEvents = ["raid_alert", "weather_shift", "base_discovered:Verdant Hub", "travel_sample:east_ridge"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 59,
                    DisplayName = "Rayhound",
                    Species = "Rayhound",
                    Traits = ["alert", "swift", "keen-eyed", "restless"],
                    Skills = new Dictionary<string, int>
                    {
                        ["scouting"] = 4,
                        ["tracking"] = 3,
                        ["combat"] = 2,
                    },
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 59,
            UserMessage = "Anything moving near the treeline?",
            TaskTag = "bark_perimeter",
            Priority = PalTaskPriority.Low,
            ImageBase64 = Convert.ToBase64String([1, 2, 3, 4]),
            ImageMimeType = "image/png",
        }, CancellationToken.None);

        Assert.That(inferenceClient.CallCount, Is.EqualTo(1));
        Assert.That(inferenceClient.LastPrompt, Is.Not.Null);
        Assert.That(inferenceClient.LastPrompt!.EnableThinking, Is.False);
        Assert.That(inferenceClient.LastPrompt.PreserveThinking, Is.False);
        Assert.That(inferenceClient.LastPrompt.Temperature, Is.EqualTo(0.45f).Within(0.001f));
        Assert.That(inferenceClient.LastPrompt.MaxTokens, Is.EqualTo(90));
        Assert.That(inferenceClient.LastPrompt.SystemPrompt.Length, Is.LessThanOrEqualTo(2200));
        Assert.That(inferenceClient.LastPrompt.SystemPrompt, Does.Contain("Active bases: Verdant Hub, Obsidian Outpost"));
        Assert.That(inferenceClient.LastPrompt.SystemPrompt, Does.Not.Contain("Kindling Hollow"));
        Assert.That(inferenceClient.LastPrompt.SystemPrompt, Does.Contain("Nearby hostiles: Syndicate Thug, Direhowl"));
        Assert.That(inferenceClient.LastPrompt.SystemPrompt, Does.Not.Contain("Tocotoco"));
        Assert.That(inferenceClient.LastPrompt.SystemPrompt, Does.Contain("Recent world events: raid_alert, weather_shift, base_discovered:Verdant Hub"));
        Assert.That(inferenceClient.LastPrompt.SystemPrompt, Does.Not.Contain("travel_sample:east_ridge"));
        int identityIndex = inferenceClient.LastPrompt.SystemPrompt.IndexOf("Active character:", StringComparison.Ordinal);
        int taskIndex = inferenceClient.LastPrompt.SystemPrompt.IndexOf("Task tag:", StringComparison.Ordinal);
        Assert.That(identityIndex, Is.GreaterThanOrEqualTo(0));
        Assert.That(taskIndex, Is.GreaterThan(identityIndex),
            "Stable companion identity should stay before volatile turn tags so same-character turns keep a longer prefix-cacheable prompt head.");
        Assert.That(response.InferenceProfileId, Is.EqualTo("fast-reactive"));
        Assert.That(response.InferenceLane, Is.EqualTo("fast-iterative"));
        Assert.That(response.ThinkingRequested, Is.False);
        Assert.That(response.VisualContextSource, Is.EqualTo("snapshot_fallback"));
        Assert.That(visionClient.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ChatAsync_WhenDensePlanningLaneRuns_UsesDeliberateExecutionProfile()
    {
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("Keep Verdant Hub on production and push scouting from the east ridge."));
        using var fixture = new TestFixtureContext(inferenceClient, inferenceEnabled: true);
        fixture.Options.Inference.Model = "unsloth/Qwen3.6-27B-GGUF";
        fixture.Options.Fallback.Enabled = false;
        fixture.Options.Fallback.EnablePolicyBypass = false;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 60,
                    DisplayName = "Anubis",
                    Species = "Anubis",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 60,
            UserMessage = "Plan the safest expansion path for our base network.",
            TaskTag = "base_strategy",
            Priority = PalTaskPriority.High,
        }, CancellationToken.None);

        Assert.That(inferenceClient.CallCount, Is.EqualTo(1));
        Assert.That(inferenceClient.LastPrompt, Is.Not.Null);
        Assert.That(inferenceClient.LastPrompt!.EnableThinking, Is.True);
        Assert.That(inferenceClient.LastPrompt.PreserveThinking, Is.True);
        Assert.That(inferenceClient.LastPrompt.Temperature, Is.EqualTo(0.45f).Within(0.001f));
        Assert.That(inferenceClient.LastPrompt.TopP, Is.EqualTo(0.76f).Within(0.001f));
        Assert.That(inferenceClient.LastPrompt.PresencePenalty, Is.EqualTo(0.55f).Within(0.001f));
        Assert.That(inferenceClient.LastPrompt.MaxTokens, Is.EqualTo(320));
        Assert.That(response.InferenceProfileId, Is.EqualTo("dense-deliberate"));
        Assert.That(response.InferenceModel, Does.Contain("27B"));
        Assert.That(response.ThinkingRequested, Is.True);
    }

    [Test]
    public async Task ChatAsync_WhenBridgeInboxHasFreshBaseEvents_SyncsBeforePlanningReply()
    {
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("live reply"));
        using var fixture = new TestFixtureContext(inferenceClient, inferenceEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 58,
                    DisplayName = "Anubis",
                    Species = "Anubis",
                },
            ],
        });

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "base-001.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "base_discovered",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-2),
                Payload = new
                {
                    BaseId = "Verdant Hub",
                    AreaRange = 42.5f,
                },
            }));
        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "base-002.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "base_discovered",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
                Payload = new
                {
                    BaseId = "Obsidian Outpost",
                    AreaRange = 31f,
                },
            }));

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 58,
            UserMessage = "How should we split work between our bases?",
            TaskTag = "chat_bases",
        }, CancellationToken.None);

        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();

        Assert.That(inferenceClient.CallCount, Is.EqualTo(0));
        Assert.That(response.InferenceBypassed, Is.True);
        Assert.That(response.FallbackStrategy, Is.EqualTo("base-network"));
        Assert.That(response.AssistantMessage, Does.Contain("Verdant Hub"));
        Assert.That(response.AssistantMessage, Does.Contain("Obsidian Outpost"));
        Assert.That(worldState.Snapshot.KnownBases, Has.Count.EqualTo(2));
        Assert.That(worldState.Snapshot.RecentEvents, Contains.Item("base_discovered:Verdant Hub"));
        Assert.That(worldState.Snapshot.RecentEvents, Contains.Item("base_discovered:Obsidian Outpost"));
        Assert.That(Directory.GetFiles(fixture.Options.BridgeInboxDir, "*.json"), Is.Empty);
        Assert.That(Directory.GetFiles(fixture.Options.BridgeArchiveDir, "*.json"), Has.Length.EqualTo(2));
    }

    [Test]
    public void DrainInbox_ProcessesChatBridgeEvent_AndRejectsOversizedEnvelope()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Bridge.MaxInboxEventBytes = 1_024;

        string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "chat-001.json");
        string json = JsonSerializer.Serialize(new
        {
            EventType = "chat_message",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Sender = "Player",
                Message = "Anyone near the base perimeter?",
                Category = "global",
            },
        });
        File.WriteAllText(bridgeFile, json);

        BridgeDrainResult result = fixture.Runtime.DrainInbox();

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(fixture.Runtime.MemoryStore.Count, Is.EqualTo(1));

        string oversizedBridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "chat-oversized.json");
        string oversizedJson = JsonSerializer.Serialize(new
        {
            EventType = "chat_message",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Sender = "Player",
                Message = new string('x', 4_096),
                Category = "global",
            },
        });
        File.WriteAllText(oversizedBridgeFile, oversizedJson);

        result = fixture.Runtime.DrainInbox();

        Assert.That(result.ProcessedCount, Is.Zero);
        Assert.That(result.FailedCount, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Options.BridgeInboxDir, "*.json"), Is.Empty);
        Assert.That(Directory.GetFiles(fixture.Options.BridgeFailedDir, "*.json"), Has.Length.EqualTo(1));
        Assert.That(fixture.Runtime.MemoryStore.Count, Is.EqualTo(1),
            "Oversized bridge envelopes should be quarantined before they mutate runtime memory.");
    }

    [Test]
    public void DrainInbox_WhenMaxFilesSpecified_LeavesRemainingBacklogForLaterPass()
    {
        using var fixture = new TestFixtureContext();

        for (int i = 1; i <= 3; i++)
        {
            string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, $"chat-00{i}.json");
            string json = JsonSerializer.Serialize(new
            {
                EventType = "chat_message",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-i),
                Payload = new
                {
                    Sender = "Player",
                    Message = $"message-{i}",
                    Category = "global",
                },
            });
            File.WriteAllText(bridgeFile, json);
        }

        BridgeDrainResult result = fixture.Runtime.DrainInbox(maxFiles: 2);

        Assert.That(result.ProcessedCount, Is.EqualTo(2));
        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(Directory.GetFiles(fixture.Options.BridgeInboxDir, "*.json"), Has.Length.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Options.BridgeArchiveDir, "*.json"), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task ChatAsync_WhenDeflanderizationEnabled_AddsTaskFocusDirectiveToPrompt()
    {
        // arxiv 2510.13586: the deflanderization pattern keeps LLM NPCs task-focused
        // instead of leaning on performative shtick. PalLLM exposes it as a toggle
        // so the existing roleplay feel is unchanged by default.
        using var fixture = new TestFixtureContext();
        fixture.Options.Fallback.PreferTaskFocus = true;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "What should we do about the broken gate?",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.SystemPrompt,
            Does.Contain("resolve the player's ask first").IgnoreCase);
    }

    [Test]
    public async Task ChatAsync_WhenDeflanderizationDisabled_OmitsDirective()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "What should we do about the broken gate?",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.SystemPrompt,
            Does.Not.Contain("resolve the player's ask first"));
    }

    [Test]
    public async Task DescribeImageAsync_WhenVisionDisabled_ReturnsGracefulFailure()
    {
        using var fixture = new TestFixtureContext();  // vision not enabled

        VisionDescribeResponse response = await fixture.Runtime.DescribeImageAsync(new VisionDescribeRequest
        {
            ImageBase64 = SamplePngBase64,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.False);
        Assert.That(response.StatusMessage, Does.Contain("Vision is disabled").IgnoreCase);
    }

    [Test]
    public async Task DescribeImageAsync_WhenVisionEnabled_ReturnsModelDescription()
    {
        var vision = new CannedVisionClient(() =>
            VisionResult.Succeeded("A stormy tundra with two Syndicate gunners near a smoking outpost."));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);

        VisionDescribeResponse response = await fixture.Runtime.DescribeImageAsync(new VisionDescribeRequest
        {
            ImageBase64 = SamplePngBase64,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.True);
        Assert.That(response.Description, Does.Contain("Syndicate gunners"));
        Assert.That(vision.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ExtractWorldStateAsync_ParsesStructuredJsonAndMergesIntoSnapshot()
    {
        const string modelOutput = """
            Here is the scene summary:
            ```json
            {
              "TimeOfDay": "dusk",
              "Weather": "storm",
              "Biome": "tundra",
              "InCombat": true,
              "InBase": false,
              "VisibleHostileCount": 2,
              "PlayerActivity": "defending a wounded Pal",
              "NotableLandmark": "smoking outpost",
              "LightLevel": 0.35,
              "Hostiles": ["Syndicate Gunner", "Syndicate Thug"],
              "Resources": ["firewood"]
            }
            ```
            """;

        var vision = new CannedVisionClient(() => VisionResult.Succeeded(modelOutput));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
        });

        VisionWorldStateResponse response = await fixture.Runtime.ExtractWorldStateAsync(new VisionWorldStateRequest
        {
            ImageBase64 = SamplePngBase64,
            ApplyToSnapshot = true,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.True);
        Assert.That(response.State, Is.Not.Null);
        Assert.That(response.State!.TimeOfDay, Is.EqualTo("dusk"));
        Assert.That(response.State.Weather, Is.EqualTo("storm"));
        Assert.That(response.State.VisibleHostileCount, Is.EqualTo(2));
        Assert.That(response.Applied, Is.True);

        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        Assert.That(worldState.Snapshot.Weather, Is.EqualTo("storm"));
        Assert.That(worldState.Snapshot.Biome, Is.EqualTo("tundra"));
        Assert.That(worldState.Snapshot.TimeOfDay, Is.EqualTo("dusk"));
        Assert.That(worldState.Snapshot.NearbyHostiles, Contains.Item("Syndicate Gunner"));
        Assert.That(worldState.Snapshot.RecentEvents,
            Has.Some.Matches<string>(e => e.StartsWith("vision:combat")));
        Assert.That(worldState.Snapshot.ThreatLevel, Is.GreaterThanOrEqualTo(0.6f),
            "Vision-reported combat must escalate threat so fallback strategies see Peak phase.");
    }

    [Test]
    public async Task ExtractWorldStateAsync_WhenModelReturnsProse_FailsGracefully()
    {
        var vision = new CannedVisionClient(() =>
            VisionResult.Succeeded("I can see a calm meadow with a Lamball grazing peacefully."));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);

        VisionWorldStateResponse response = await fixture.Runtime.ExtractWorldStateAsync(new VisionWorldStateRequest
        {
            ImageBase64 = SamplePngBase64,
            ApplyToSnapshot = true,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.False,
            "Non-JSON content must not be marked as a successful structured extraction.");
        Assert.That(response.State, Is.Null);
        Assert.That(response.Applied, Is.False);
    }

    [Test]
    public async Task ChatAsync_WithImage_SplicesVisualContextIntoSystemPrompt()
    {
        var vision = new CannedVisionClient(() =>
            VisionResult.Succeeded("Night-time base perimeter with one Rayhound circling the gate."));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "What should we do here?",
            TaskTag = "chat_general",
            ImageBase64 = SamplePngBase64,
        }, CancellationToken.None);

        Assert.That(vision.CallCount, Is.EqualTo(1),
            "Chat with an attached image must call the vision client exactly once.");
        Assert.That(response.SystemPrompt,
            Does.Contain("Visual context").And.Contain("Rayhound"));
    }

    [Test]
    public async Task ChatAsync_WhenSpeechArtifactBuilt_UsesPresentationVoiceRouting()
    {
        byte[] fakeAudio = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00];
        var inferenceClient = new CountingInferenceClient(() => InferenceResult.Succeeded("I can tell that story."));
        var tts = new CannedTtsClient(request => TtsResult.Succeeded(fakeAudio, "audio/wav", request.Voice ?? "missing-voice"));
        using var fixture = new TestFixtureContext(
            inferenceClient,
            inferenceEnabled: true,
            ttsClient: tts,
            ttsEnabled: true);
        fixture.Options.Tts.WarmVoice = "warm-companion";
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            TimeOfDay = "night",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 62,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 62,
            UserMessage = "Tell me a campfire story while we rest at base tonight.",
            TaskTag = "chat_story",
        }, CancellationToken.None);

        Assert.That(tts.CallCount, Is.EqualTo(1));
        Assert.That(tts.LastRequest, Is.Not.Null);
        Assert.That(tts.LastRequest!.Voice, Is.EqualTo("warm-companion"));
        Assert.That(response.Speech, Is.Not.Null);
        Assert.That(response.Speech!.Voice, Is.EqualTo("warm-companion"));
        Assert.That(response.Speech.VoicePrint, Is.EqualTo("cozy-companion"));
    }

    [Test]
    public async Task SynthesizeSpeechAsync_WhenTtsDisabled_ReturnsGracefulFailure()
    {
        using var fixture = new TestFixtureContext();  // Tts not enabled, DisabledTtsClient

        TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = "Hello from the forge.",
        }, CancellationToken.None);

        Assert.That(response.Success, Is.False);
        Assert.That(response.StatusMessage, Does.Contain("disabled").IgnoreCase);
    }

    [Test]
    public async Task TranscribeAudioAsync_WhenAsrEnabled_RecordsSuccessAndFailureReceipts()
    {
        var results = new Queue<AudioTranscriptionResult>([
            AudioTranscriptionResult.Succeeded("Meet at the ridge.", "local-asr", 4, 12),
            AudioTranscriptionResult.Failed("ASR endpoint returned no transcript text.", "local-asr", 4, 15),
        ]);
        var asr = new CannedAsrClient(() => results.Dequeue());
        using var fixture = new TestFixtureContext(asrClient: asr, asrEnabled: true);

        AudioTranscribeResponse success = await fixture.Runtime.TranscribeAudioAsync(new AudioTranscribeRequest
        {
            AudioBase64 = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]),
        }, CancellationToken.None);
        AudioTranscribeResponse failure = await fixture.Runtime.TranscribeAudioAsync(new AudioTranscribeRequest
        {
            AudioBase64 = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]),
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(success.Success, Is.True);
        Assert.That(success.Transcript, Is.EqualTo("Meet at the ridge."));
        Assert.That(failure.Success, Is.False);
        Assert.That(asr.CallCount, Is.EqualTo(2));
        Assert.That(health.AsrEnabled, Is.True);
        Assert.That(health.AsrCallCount, Is.EqualTo(2));
        Assert.That(health.AsrSuccessCount, Is.EqualTo(1));
        Assert.That(health.AsrFailureCount, Is.EqualTo(1));
    }

    [Test]
    public async Task TranscribeAudioAsync_WhenEndpointingMetadataProvided_RecordsContentFreeReceipts()
    {
        var asr = new CannedAsrClient(() =>
            AudioTranscriptionResult.Succeeded("Meet at the ridge.", "local-asr", 4, 12));
        using var fixture = new TestFixtureContext(asrClient: asr, asrEnabled: true);

        AudioTranscribeResponse response = await fixture.Runtime.TranscribeAudioAsync(new AudioTranscribeRequest
        {
            AudioBase64 = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]),
            Endpointing = new AudioTurnEndpointingInput
            {
                SpeechMs = 1_200,
                LeadingSilenceMs = 120,
                TrailingSilenceMs = 250,
                EndpointReason = "client_vad_silence",
                BargeIn = true,
            },
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(response.Success, Is.True);
        Assert.That(response.Endpointing.ClientVadSupplied, Is.True);
        Assert.That(response.Endpointing.Status, Is.EqualTo("review"));
        Assert.That(response.Endpointing.Flags, Contains.Item("pre_speech_padding_below_target"));
        Assert.That(response.Endpointing.Flags, Contains.Item("endpoint_silence_below_target"));
        Assert.That(response.Endpointing.TotalTurnMs, Is.EqualTo(1_570));
        Assert.That(health.AsrEndpointingReceiptCount, Is.EqualTo(1));
        Assert.That(health.AsrBargeInCount, Is.EqualTo(1));
        Assert.That(health.AsrEndpointingReviewCount, Is.EqualTo(1));
    }

    [Test]
    public async Task TranscribeAudioAsync_WhenConfidenceReceiptReturned_RecordsContentFreeConfidenceCounters()
    {
        var receipt = new AudioTranscriptionConfidenceReceipt
        {
            LogprobsRequested = true,
            LogprobsReturned = true,
            Status = "review",
            TokenCount = 2,
            AverageLogprob = -0.8,
            MinLogprob = -1.4,
            LowConfidenceTokenCount = 1,
            LowConfidenceThreshold = -1.0f,
        };
        var asr = new CannedAsrClient(() =>
            AudioTranscriptionResult.Succeeded("Meet at the ridge.", "local-asr", 4, 12, receipt));
        using var fixture = new TestFixtureContext(asrClient: asr, asrEnabled: true);

        AudioTranscribeResponse response = await fixture.Runtime.TranscribeAudioAsync(new AudioTranscribeRequest
        {
            AudioBase64 = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]),
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(response.Success, Is.True);
        Assert.That(response.Confidence.LogprobsReturned, Is.True);
        Assert.That(response.Confidence.LowConfidenceTokenCount, Is.EqualTo(1));
        Assert.That(health.AsrConfidenceReceiptCount, Is.EqualTo(1));
        Assert.That(health.AsrConfidenceReviewCount, Is.EqualTo(1));
    }

    [Test]
    public async Task TranscribeAudioAsync_WhenTimingReceiptReturned_RecordsContentFreeTimingCounters()
    {
        var receipt = new AudioTranscriptionTimingReceipt
        {
            VerboseJsonRequested = true,
            VerboseJsonReturned = true,
            SegmentTimestampsRequested = true,
            SegmentTimestampsReturned = true,
            Status = "review",
            DurationSeconds = 35,
            SegmentCount = 1,
            FirstSegmentStartSeconds = 0,
            LastSegmentEndSeconds = 35,
            CoveredSegmentSeconds = 35,
            SegmentCoverageRatio = 1,
            MaxTurnDurationMs = 30_000,
            Flags = ["duration_over_turn_budget"],
        };
        var asr = new CannedAsrClient(() =>
            AudioTranscriptionResult.Succeeded("Meet at the ridge.", "local-asr", 4, 12, timing: receipt));
        using var fixture = new TestFixtureContext(asrClient: asr, asrEnabled: true);

        AudioTranscribeResponse response = await fixture.Runtime.TranscribeAudioAsync(new AudioTranscribeRequest
        {
            AudioBase64 = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]),
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(response.Success, Is.True);
        Assert.That(response.Timing.VerboseJsonReturned, Is.True);
        Assert.That(response.Timing.Flags, Contains.Item("duration_over_turn_budget"));
        Assert.That(health.AsrTimingReceiptCount, Is.EqualTo(1));
        Assert.That(health.AsrTimingReviewCount, Is.EqualTo(1));
    }

    [Test]
    public async Task TranscribeAudioAsync_WhenQualityReceiptReturned_RecordsContentFreeQualityCounters()
    {
        var receipt = new AudioTranscriptionQualityReceipt
        {
            VerboseJsonRequested = true,
            QualityMetadataReturned = true,
            Status = "review",
            SegmentCount = 2,
            QualitySegmentCount = 2,
            AverageSegmentLogprob = -0.75,
            MinSegmentLogprob = -1.2,
            LowAverageLogprobSegmentCount = 1,
            LowAverageLogprobThreshold = -1.0d,
            MaxCompressionRatio = 2.6,
            HighCompressionRatioSegmentCount = 1,
            HighCompressionRatioThreshold = 2.4d,
            MaxNoSpeechProbability = 1.1,
            NoSpeechProbabilitySegmentCount = 2,
            SilentSegmentCandidateCount = 1,
            TemperatureSegmentCount = 2,
            MaxTemperature = 0.2,
            Flags = ["avg_logprob_below_minus_one", "compression_ratio_above_2_4"],
        };
        var asr = new CannedAsrClient(() =>
            AudioTranscriptionResult.Succeeded("Meet at the ridge.", "local-asr", 4, 12, quality: receipt));
        using var fixture = new TestFixtureContext(asrClient: asr, asrEnabled: true);

        AudioTranscribeResponse response = await fixture.Runtime.TranscribeAudioAsync(new AudioTranscribeRequest
        {
            AudioBase64 = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]),
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(response.Success, Is.True);
        Assert.That(response.Quality.QualityMetadataReturned, Is.True);
        Assert.That(response.Quality.HighCompressionRatioSegmentCount, Is.EqualTo(1));
        Assert.That(health.AsrQualityReceiptCount, Is.EqualTo(1));
        Assert.That(health.AsrQualityReviewCount, Is.EqualTo(1));
    }

    [Test]
    public async Task TranscribeAudioAsync_WhenUpstreamReceiptsReturned_RecordsContentFreeCounters()
    {
        var asr = new CannedAsrClient(() =>
            AudioTranscriptionResult.Succeeded(
                "Meet at the ridge.",
                "local-asr",
                4,
                12,
                upstreamRequestId: "asr-runtime-001",
                upstreamProcessingMs: 18.25,
                upstreamQueueMs: 3.5,
                upstreamTimeToFirstTokenMs: 7.75));
        using var fixture = new TestFixtureContext(asrClient: asr, asrEnabled: true);

        AudioTranscribeResponse response = await fixture.Runtime.TranscribeAudioAsync(new AudioTranscribeRequest
        {
            AudioBase64 = Convert.ToBase64String([0x52, 0x49, 0x46, 0x46]),
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(response.Success, Is.True);
        Assert.That(response.UpstreamRequestId, Is.EqualTo("asr-runtime-001"));
        Assert.That(response.UpstreamProcessingMs, Is.EqualTo(18.25));
        Assert.That(response.UpstreamQueueMs, Is.EqualTo(3.5));
        Assert.That(response.UpstreamTimeToFirstTokenMs, Is.EqualTo(7.75));
        Assert.That(health.AsrUpstreamRequestIdReceiptCount, Is.EqualTo(1));
        Assert.That(health.AsrUpstreamProcessingReceiptCount, Is.EqualTo(1));
        Assert.That(health.AsrUpstreamPhaseTimingReceiptCount, Is.EqualTo(1));
    }

    [Test]
    public async Task SynthesizeSpeechAsync_WhenEnabled_WritesAudioToDiskAndReturnsPath()
    {
        byte[] fakeAudio = [0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00];  // RIFF header bytes
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/wav", "en_US-amy-medium"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);

        TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = "Thank you, companion.",
            Voice = "en_US-amy-medium",
            WriteToDisk = true,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.True);
        Assert.That(response.MimeType, Is.EqualTo("audio/wav"));
        Assert.That(response.PlaybackHint, Is.EqualTo("sound_player"));
        Assert.That(response.AudioBytes, Is.EqualTo(fakeAudio.Length));
        Assert.That(response.FilePath, Is.Not.Null.And.Not.Empty);
        Assert.That(File.Exists(response.FilePath!), Is.True);
        Assert.That(File.ReadAllBytes(response.FilePath!), Is.EqualTo(fakeAudio));
        Assert.That(fixture.Runtime.GetHealth().TtsSuccessCount, Is.EqualTo(1));
    }

    [Test]
    public async Task SynthesizeSpeechAsync_WhenCompressedFormatReturned_UsesMediaPlayerHint()
    {
        byte[] fakeAudio = [0x49, 0x44, 0x33, 0x04];
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/mpeg", "en_US-amy-medium"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);

        TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = "Scout ahead and keep comms clear.",
            Voice = "en_US-amy-medium",
            WriteToDisk = true,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.True);
        Assert.That(response.MimeType, Is.EqualTo("audio/mpeg"));
        Assert.That(response.PlaybackHint, Is.EqualTo("media_player"));
        Assert.That(response.FilePath, Does.EndWith(".mp3"));
        Assert.That(File.Exists(response.FilePath!), Is.True);
        Assert.That(File.ReadAllBytes(response.FilePath!), Is.EqualTo(fakeAudio));
    }

    [Test]
    public async Task SynthesizeSpeechAsync_WhenRawPcmReturned_WritesPcmFileAndRawPlaybackHint()
    {
        byte[] fakeAudio = [0x00, 0x01, 0x02, 0x03];
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/pcm", "en_US-amy-medium"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);

        TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = "Raw audio canary.",
            WriteToDisk = true,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.True);
        Assert.That(response.MimeType, Is.EqualTo("audio/pcm"));
        Assert.That(response.PlaybackHint, Is.EqualTo("raw_pcm"));
        Assert.That(response.FilePath, Does.EndWith(".pcm"));
        Assert.That(File.Exists(response.FilePath!), Is.True);
        Assert.That(File.ReadAllBytes(response.FilePath!), Is.EqualTo(fakeAudio));
    }

    [Test]
    public async Task SynthesizeSpeechAsync_WhenRawPcmMimeCarriesParameters_StillRoutesAsRawPcm()
    {
        byte[] fakeAudio = [0x00, 0x01, 0x02, 0x03];
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/L16; rate=24000; channels=1", "en_US-amy-medium"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);

        TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = "Parameterized raw audio canary.",
            WriteToDisk = true,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.True);
        Assert.That(response.MimeType, Is.EqualTo("audio/L16; rate=24000; channels=1"));
        Assert.That(response.PlaybackHint, Is.EqualTo("raw_pcm"));
        Assert.That(response.FilePath, Does.EndWith(".pcm"));
        Assert.That(File.Exists(response.FilePath!), Is.True);
        Assert.That(File.ReadAllBytes(response.FilePath!), Is.EqualTo(fakeAudio));
    }

    [Test]
    public async Task SynthesizeSpeechAsync_WhenRetentionExceeded_PrunesOlderArtifacts()
    {
        byte[] fakeAudio = [0x52, 0x49, 0x46, 0x46, 0x09, 0x08, 0x07, 0x06];
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/wav", "en_US-amy-medium"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);
        fixture.Options.Tts.MaxStoredFiles = 1;
        fixture.Options.Tts.MaxStoredAgeHours = 24;

        string stalePath = Path.Combine(fixture.Options.TtsDir, "tts-stale.wav");
        File.WriteAllBytes(stalePath, [0x52, 0x49, 0x46, 0x46]);
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow.AddHours(-2));

        TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = "Newest line wins.",
            WriteToDisk = true,
        }, CancellationToken.None);

        string[] files = Directory.GetFiles(fixture.Options.TtsDir);

        Assert.That(response.Success, Is.True);
        Assert.That(response.FilePath, Is.Not.Null.And.Not.Empty);
        Assert.That(files, Has.Length.EqualTo(1));
        Assert.That(files[0], Is.EqualTo(response.FilePath));
        Assert.That(File.Exists(stalePath), Is.False,
            "Inline retention should prune older TTS artifacts as new ones are written.");
    }

    [Test]
    public async Task SynthesizeSpeechAsync_WhenWriteToDiskDisabled_ReturnsBase64()
    {
        byte[] fakeAudio = [0x10, 0x20, 0x30];
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/wav", "v"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);

        TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
        {
            Text = "short",
            WriteToDisk = false,
        }, CancellationToken.None);

        Assert.That(response.Success, Is.True);
        Assert.That(response.FilePath, Is.Null);
        Assert.That(response.PlaybackHint, Is.EqualTo("sound_player"));
        Assert.That(response.AudioBase64, Is.Not.Null.And.Not.Empty);
        Assert.That(Convert.FromBase64String(response.AudioBase64!), Is.EqualTo(fakeAudio));
    }

    [Test]
    public async Task ChatAsync_WhenTtsEnabled_AttachesSpeechArtifactToResponseAndOutbox()
    {
        byte[] fakeAudio = [0x52, 0x49, 0x46, 0x46, 0x01, 0x02, 0x03, 0x04];
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/wav", "en_US-amy-medium"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 12,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 12,
            UserMessage = "Give me a quick camp update.",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        Assert.That(tts.CallCount, Is.EqualTo(1));
        RuntimeHealth health = fixture.Runtime.GetHealth();
        Assert.That(health.TtsCallCount, Is.EqualTo(1));
        Assert.That(health.TtsSuccessCount, Is.EqualTo(1));
        Assert.That(health.TtsFailureCount, Is.Zero);
        Assert.That(response.AssistantMessage, Is.Not.Null.And.Not.Empty);
        Assert.That(response.Speech, Is.Not.Null, "A TTS-enabled chat turn should carry its synthesized speech artifact.");
        Assert.That(response.Speech!.RequestId, Is.EqualTo(response.RequestId));
        Assert.That(response.Speech.Voice, Is.EqualTo("en_US-amy-medium"));
        Assert.That(response.Speech.VoicePrint, Is.EqualTo(response.Presentation.Audio.VoicePrint));
        Assert.That(response.Speech.SubtitleStyle, Is.EqualTo(response.Presentation.Audio.SubtitleStyle));
        Assert.That(response.Speech.PlaybackHint, Is.EqualTo("sound_player"));
        Assert.That(response.Speech.FilePath, Is.Not.Null.And.Not.Empty);
        Assert.That(File.Exists(response.Speech.FilePath!), Is.True);
        Assert.That(File.ReadAllBytes(response.Speech.FilePath!), Is.EqualTo(fakeAudio));
        Assert.That(response.Presentation.Surface.FooterTokens, Is.Not.Empty);

        string outboxFile = Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json").Single();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outboxFile));
        JsonElement speech = document.RootElement.GetProperty("Payload").GetProperty("Speech");
        JsonElement surface = document.RootElement.GetProperty("Payload").GetProperty("Presentation").GetProperty("Surface");

        Assert.That(speech.GetProperty("RequestId").GetString(), Is.EqualTo(response.RequestId));
        Assert.That(speech.GetProperty("MimeType").GetString(), Is.EqualTo("audio/wav"));
        Assert.That(speech.GetProperty("PlaybackHint").GetString(), Is.EqualTo("sound_player"));
        Assert.That(speech.GetProperty("Voice").GetString(), Is.EqualTo("en_US-amy-medium"));
        Assert.That(speech.GetProperty("FilePath").GetString(), Is.EqualTo(response.Speech.FilePath));
        Assert.That(surface.GetProperty("PrimaryTitle").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(surface.GetProperty("CueTitle").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(surface.GetProperty("ReadoutTitle").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(surface.GetProperty("SupportTitle").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(surface.GetProperty("ActionPreviewTitle").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(surface.GetProperty("ActionFeedbackTitle").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(surface.GetProperty("FollowupOrder").GetArrayLength(), Is.EqualTo(3));
        Assert.That(surface.GetProperty("HeaderTokens").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("CueTokens").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("FocusTokens").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("StatusTokens").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("StageTokens").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("AtmosphereTokens").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("FooterTokens").GetArrayLength(), Is.GreaterThan(0));
        Assert.That(surface.GetProperty("CardBudget").GetInt32(), Is.InRange(1, 3));
        Assert.That(surface.GetProperty("PrimaryCueTokenCount").GetInt32(), Is.InRange(0, 2));
        Assert.That(surface.GetProperty("PrimaryFocusTokenCount").GetInt32(), Is.InRange(0, 2));
        Assert.That(surface.GetProperty("PrimaryStatusTokenCount").GetInt32(), Is.InRange(0, 2));
        Assert.That(surface.GetProperty("PrimaryStageTokenCount").GetInt32(), Is.InRange(0, 1));
        Assert.That(surface.GetProperty("PrimaryAtmosphereTokenCount").GetInt32(), Is.InRange(0, 1));
    }

    [Test]
    public async Task ChatAsync_WhenChatLinkedTtsFails_PreservesTextDeliveryAndOmitsSpeechArtifact()
    {
        var tts = new CannedTtsClient(() => TtsResult.Failed("tts endpoint offline"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Keep watch over the gate.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(tts.CallCount, Is.EqualTo(1));
        Assert.That(response.AssistantMessage, Is.Not.Null.And.Not.Empty);
        Assert.That(response.Speech, Is.Null,
            "Speech failures must not block the textual companion reply.");

        string outboxFile = Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json").Single();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outboxFile));
        JsonElement payload = document.RootElement.GetProperty("Payload");

        Assert.That(payload.TryGetProperty("Speech", out _), Is.False,
            "Outbox payload should omit Speech when chat-linked synthesis fails.");
    }

    [Test]
    public async Task DrainInbox_WhenSpeechPlaybackArrives_TracksNativeSpeechProof()
    {
        byte[] fakeAudio = [0x52, 0x49, 0x46, 0x46, 0x01, 0x02, 0x03, 0x04];
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/wav", "en_US-amy-medium"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 91,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 91,
            RequestId = "speech-loop-001",
            UserMessage = "Give me a short spoken camp update.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        RuntimeHealth outboxHealth = fixture.Runtime.GetHealth();
        Assert.That(outboxHealth.BridgeLoop.LastOutboxReply?.SpeechExpected, Is.True);
        Assert.That(outboxHealth.BridgeLoop.LastOutboxReply?.SpeechPlaybackHint, Is.EqualTo("sound_player"));
        DateTimeOffset outboxWrittenAt = outboxHealth.BridgeLoop.LastOutboxReply!.WrittenAtUtc;
        DateTimeOffset deliveryAt = outboxWrittenAt.AddMilliseconds(75);
        DateTimeOffset speechPlaybackAt = deliveryAt.AddMilliseconds(125);

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "delivery-speech-001.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "reply_delivery",
                Source = "ue4ss",
                TimestampUtc = deliveryAt,
                Payload = new
                {
                    RequestId = response.RequestId,
                    Speaker = response.CharacterName,
                    ResponsePath = response.ResponsePath,
                    StrategyId = response.FallbackStrategy ?? string.Empty,
                    Phase = response.FallbackPhase ?? string.Empty,
                    UsedFallback = response.UsedFallback,
                    Rendered = true,
                    Surface = "native_hud",
                    CardLabel = "Reply",
                    CardIndex = 1,
                    CardCount = 1,
                    Note = "speech proof delivery",
                },
            }));

        fixture.Runtime.DrainInbox();
        RuntimeHealth awaitingSpeech = fixture.Runtime.GetHealth();
        Assert.That(awaitingSpeech.BridgeLoop.Status, Is.EqualTo("awaiting_speech_playback"));
        Assert.That(awaitingSpeech.BridgeLoop.SpeechPlaybackExpected, Is.True);
        Assert.That(awaitingSpeech.BridgeLoop.SpeechPlaybackObserved, Is.False);

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-001.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = speechPlaybackAt,
                Payload = new
                {
                    RequestId = response.RequestId,
                    Started = true,
                    ArtifactBytes = fakeAudio.Length,
                    AttemptCount = 1,
                    ElapsedMs = 14,
                    PlaybackMode = "sound_player",
                    PlaybackHint = "sound_player",
                    MimeType = "audio/wav",
                    FileExtension = ".wav",
                    Reason = "sound_player",
                    FailureCode = string.Empty,
                },
            }));

        fixture.Runtime.DrainInbox();
        RuntimeHealth closed = fixture.Runtime.GetHealth();

        Assert.That(closed.BridgeLoop.Status, Is.EqualTo("closed"));
        Assert.That(closed.BridgeLoop.LoopClosed, Is.True);
        Assert.That(closed.BridgeLoop.SpeechPlaybackExpected, Is.True);
        Assert.That(closed.BridgeLoop.SpeechPlaybackObserved, Is.True);
        Assert.That(closed.BridgeLoop.SpeechPlaybackStarted, Is.True);
        Assert.That(closed.BridgeLoop.SpeechPlaybackIngressLagMs, Is.GreaterThanOrEqualTo(200));
        Assert.That(closed.BridgeLoop.SpeechPlaybackOutboxLagMs, Is.EqualTo(200));
        Assert.That(closed.BridgeLoop.SpeechPlaybackDeliveryLagMs, Is.EqualTo(125));
        Assert.That(closed.BridgeLoop.LastSpeechPlayback, Is.Not.Null);
        Assert.That(closed.BridgeLoop.LastSpeechPlayback!.RequestId, Is.EqualTo(response.RequestId));
        Assert.That(closed.BridgeLoop.LastSpeechPlayback.ArtifactBytes, Is.EqualTo(fakeAudio.Length));
        Assert.That(closed.BridgeLoop.LastSpeechPlayback.AttemptCount, Is.EqualTo(1));
        Assert.That(closed.BridgeLoop.LastSpeechPlayback.ElapsedMs, Is.EqualTo(14));
        Assert.That(closed.BridgeLoop.LastSpeechPlayback.PlaybackMode, Is.EqualTo("sound_player"));
        Assert.That(closed.BridgeLoop.LastSpeechPlayback.FileExtension, Is.EqualTo(".wav"));
        Assert.That(closed.BridgeLoop.LastSpeechPlayback.FailureCode, Is.Empty);
    }

    [Test]
    public void DrainInbox_WhenSpeechPlaybackReportsArtifactBytes_PreservesContentFreeSizeReceipt()
    {
        using var fixture = new TestFixtureContext();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-size.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = "speech-size-001",
                    Started = false,
                    ArtifactBytes = 0,
                    AttemptCount = -7,
                    ElapsedMs = -12,
                    PlaybackMode = "",
                    PlaybackHint = "sound_player",
                    MimeType = "audio/wav",
                    FileExtension = ".wav",
                    Reason = "speech file empty",
                    FailureCode = "speech_file_empty",
                },
            }));

        fixture.Runtime.DrainInbox();
        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(health.BridgeLoop.LastSpeechPlayback, Is.Not.Null);
        Assert.That(health.BridgeLoop.LastSpeechPlayback!.RequestId, Is.EqualTo("speech-size-001"));
        Assert.That(health.BridgeLoop.LastSpeechPlayback.ArtifactBytes, Is.Zero);
        Assert.That(health.BridgeLoop.LastSpeechPlayback.AttemptCount, Is.Zero);
        Assert.That(health.BridgeLoop.LastSpeechPlayback.ElapsedMs, Is.Zero);
        Assert.That(health.BridgeLoop.LastSpeechPlayback.Started, Is.False);
        Assert.That(health.BridgeLoop.LastSpeechPlayback.Reason, Is.EqualTo("speech file empty"));
        Assert.That(health.BridgeLoop.LastSpeechPlayback.FailureCode, Is.EqualTo("speech_file_empty"));

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-raw-pcm.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = "speech-raw-pcm-001",
                    Started = false,
                    ArtifactBytes = 48000,
                    AttemptCount = 0,
                    ElapsedMs = 2,
                    PlaybackMode = "raw_pcm",
                    PlaybackHint = "raw_pcm",
                    MimeType = "audio/pcm",
                    FileExtension = ".pcm",
                    Reason = "speech raw pcm requires native mixer binding",
                    FailureCode = "raw_pcm_native_mixer_required",
                },
            }));

        fixture.Runtime.DrainInbox();
        RuntimeHealth rawPcmHealth = fixture.Runtime.GetHealth();

        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback, Is.Not.Null);
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback!.RequestId, Is.EqualTo("speech-raw-pcm-001"));
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.Started, Is.False);
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.ArtifactBytes, Is.EqualTo(48000));
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.AttemptCount, Is.Zero);
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.PlaybackMode, Is.EqualTo("raw_pcm"));
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.PlaybackHint, Is.EqualTo("raw_pcm"));
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.MimeType, Is.EqualTo("audio/pcm"));
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.FileExtension, Is.EqualTo(".pcm"));
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.Reason, Is.EqualTo("speech raw pcm requires native mixer binding"));
        Assert.That(rawPcmHealth.BridgeLoop.LastSpeechPlayback.FailureCode, Is.EqualTo("raw_pcm_native_mixer_required"));
    }

    [Test]
    public void DrainInbox_WhenSpeechPlaybackReportsFailureCode_NormalizesStableTaxonomy()
    {
        using var fixture = new TestFixtureContext();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-code.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = "speech-code-001",
                    Started = false,
                    ArtifactBytes = 4096,
                    AttemptCount = 0,
                    ElapsedMs = 3,
                    PlaybackMode = "raw_pcm",
                    PlaybackHint = "raw_pcm",
                    MimeType = "audio/l16",
                    FileExtension = ".pcm",
                    Reason = "speech raw pcm requires native mixer binding",
                    FailureCode = " RAW PCM NATIVE MIXER REQUIRED ",
                },
            }));

        fixture.Runtime.DrainInbox();
        SpeechPlaybackSnapshot? playback = fixture.Runtime.GetHealth().BridgeLoop.LastSpeechPlayback;

        Assert.That(playback, Is.Not.Null);
        Assert.That(playback!.FailureCode, Is.EqualTo("raw_pcm_native_mixer_required"));
    }

    [Test]
    public void DrainInbox_WhenSpeechPlaybackReportsSupersession_PreservesContentFreeCancellationReceipt()
    {
        using var fixture = new TestFixtureContext();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-supersession.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = "speech-current-001",
                    Started = false,
                    ArtifactBytes = 4096,
                    AttemptCount = 1,
                    ElapsedMs = 7,
                    PlaybackSequence = 12,
                    SupersededRequestId = " speech-previous-001 ",
                    SupersededSpeechCount = 3,
                    SupersededSpeechAgeMs = 245,
                    SupersededSpeechBufferedMs = 1000L,
                    SupersededSpeechRemainingMs = 999999L,
                    CancellationMode = " DESKTOP HELPER UNCONTROLLED ",
                    PlaybackMode = "sound_player",
                    PlaybackHint = "sound_player",
                    MimeType = "audio/wav",
                    FileExtension = ".wav",
                    Reason = "duplicate speech within dedupe window",
                    FailureCode = "duplicate_within_dedupe_window",
                },
            }));

        fixture.Runtime.DrainInbox();
        SpeechPlaybackSnapshot? playback = fixture.Runtime.GetHealth().BridgeLoop.LastSpeechPlayback;

        Assert.That(playback, Is.Not.Null);
        Assert.That(playback!.PlaybackSequence, Is.EqualTo(12));
        Assert.That(playback.SupersededRequestId, Is.EqualTo("speech-previous-001"));
        Assert.That(playback.SupersededSpeechCount, Is.EqualTo(3));
        Assert.That(playback.SupersededSpeechAgeMs, Is.EqualTo(245));
        Assert.That(playback.SupersededSpeechBufferedMs, Is.EqualTo(1000L));
        Assert.That(playback.SupersededSpeechRemainingMs, Is.EqualTo(755L));
        Assert.That(playback.CancellationMode, Is.EqualTo("desktop_helper_uncontrolled"));
    }

    [Test]
    public void DrainInbox_WhenSpeechPlaybackReportsRawPcmFormat_PreservesNativeMixerReadinessReceipt()
    {
        using var fixture = new TestFixtureContext();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-raw-format.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = "speech-raw-format-001",
                    Started = false,
                    ArtifactBytes = 48000,
                    AttemptCount = 0,
                    ElapsedMs = 2,
                    SampleRateHz = 24000,
                    ChannelCount = 1,
                    BitsPerSample = 16,
                    DurationMs = 1000,
                    ByteRate = 48000L,
                    BlockAlignBytes = 2,
                    AudioDataBytes = 48000L,
                    FrameCount = 24000L,
                    BlockRemainderBytes = 0,
                    ValidBitsPerSample = 16,
                    ChannelMask = 0L,
                    AudioEncoding = " L16 PCM ",
                    SampleFormat = " Signed Integer ",
                    ByteOrder = " BIG_ENDIAN ",
                    MixerConversionHint = " BYTE SWAP INTEGER TO FLOAT32 ",
                    MixerQuantumMs = 999,
                    MixerQuantumFrames = 999,
                    MixerQueueDepthEstimate = 999,
                    MixerTailFrames = 999,
                    MixerBufferedMs = 999,
                    MixerTailMs = 999,
                    PlaybackMode = "raw_pcm",
                    PlaybackHint = "raw_pcm",
                    MimeType = "audio/L16; rate=24000; channels=1",
                    FileExtension = ".pcm",
                    Reason = "speech raw pcm requires native mixer binding",
                    FailureCode = "raw_pcm_native_mixer_required",
                },
            }));

        fixture.Runtime.DrainInbox();
        SpeechPlaybackSnapshot? playback = fixture.Runtime.GetHealth().BridgeLoop.LastSpeechPlayback;

        Assert.That(playback, Is.Not.Null);
        Assert.That(playback!.RequestId, Is.EqualTo("speech-raw-format-001"));
        Assert.That(playback.Started, Is.False);
        Assert.That(playback.SampleRateHz, Is.EqualTo(24000));
        Assert.That(playback.ChannelCount, Is.EqualTo(1));
        Assert.That(playback.BitsPerSample, Is.EqualTo(16));
        Assert.That(playback.DurationMs, Is.EqualTo(1000));
        Assert.That(playback.ByteRate, Is.EqualTo(48000L));
        Assert.That(playback.BlockAlignBytes, Is.EqualTo(2));
        Assert.That(playback.AudioDataBytes, Is.EqualTo(48000L));
        Assert.That(playback.FrameCount, Is.EqualTo(24000L));
        Assert.That(playback.BlockRemainderBytes, Is.Zero);
        Assert.That(playback.ValidBitsPerSample, Is.EqualTo(16));
        Assert.That(playback.AudioEncoding, Is.EqualTo("l16_pcm"));
        Assert.That(playback.SampleFormat, Is.EqualTo("signed_integer"));
        Assert.That(playback.ByteOrder, Is.EqualTo("big_endian"));
        Assert.That(playback.MixerConversionHint, Is.EqualTo("byte_swap_integer_to_float32"));
        Assert.That(playback.MixerQuantumMs, Is.EqualTo(10));
        Assert.That(playback.MixerQuantumFrames, Is.EqualTo(240));
        Assert.That(playback.MixerQueueDepthEstimate, Is.EqualTo(100L));
        Assert.That(playback.MixerTailFrames, Is.Zero);
        Assert.That(playback.MixerBufferedMs, Is.EqualTo(1000L));
        Assert.That(playback.MixerTailMs, Is.Zero);
        Assert.That(playback.FailureCode, Is.EqualTo("raw_pcm_native_mixer_required"));
    }

    [Test]
    public void DrainInbox_WhenSpeechPlaybackReportsAudioDataAndBlockAlign_DerivesFrameReceipt()
    {
        using var fixture = new TestFixtureContext();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-frame-receipt.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = "speech-frame-receipt-001",
                    Started = false,
                    ArtifactBytes = 48005,
                    AttemptCount = 0,
                    ElapsedMs = 3,
                    SampleRateHz = 24000,
                    ChannelCount = 1,
                    BitsPerSample = 16,
                    DurationMs = 1000,
                    ByteRate = 48000L,
                    BlockAlignBytes = 2,
                    AudioDataBytes = 48005L,
                    FrameCount = 999999L,
                    BlockRemainderBytes = 999,
                    ValidBitsPerSample = 16,
                    AudioEncoding = "l16_pcm",
                    PlaybackMode = "raw_pcm",
                    PlaybackHint = "raw_pcm",
                    MimeType = "audio/L16; rate=24000; channels=1",
                    FileExtension = ".pcm",
                    Reason = "speech raw pcm block alignment invalid",
                    FailureCode = "raw_pcm_block_alignment_invalid",
                },
            }));

        fixture.Runtime.DrainInbox();
        SpeechPlaybackSnapshot? playback = fixture.Runtime.GetHealth().BridgeLoop.LastSpeechPlayback;

        Assert.That(playback, Is.Not.Null);
        Assert.That(playback!.RequestId, Is.EqualTo("speech-frame-receipt-001"));
        Assert.That(playback.FrameCount, Is.EqualTo(24002L));
        Assert.That(playback.BlockRemainderBytes, Is.EqualTo(1));
        Assert.That(playback.MixerQuantumMs, Is.EqualTo(10));
        Assert.That(playback.MixerQuantumFrames, Is.EqualTo(240));
        Assert.That(playback.MixerQueueDepthEstimate, Is.EqualTo(101L));
        Assert.That(playback.MixerTailFrames, Is.EqualTo(2));
        Assert.That(playback.MixerBufferedMs, Is.EqualTo(1010L));
        Assert.That(playback.MixerTailMs, Is.EqualTo(1));
        Assert.That(playback.FailureCode, Is.EqualTo("raw_pcm_block_alignment_invalid"));
    }

    [Test]
    public void DrainInbox_WhenSpeechPlaybackReportsAudioFormat_PreservesContentFreeFormatReceipt()
    {
        using var fixture = new TestFixtureContext();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeInboxDir, "speech-playback-format.json"),
            JsonSerializer.Serialize(new
            {
                EventType = "speech_playback",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = new
                {
                    RequestId = "speech-format-001",
                    Started = true,
                    ArtifactBytes = 48044,
                    AttemptCount = 1,
                    ElapsedMs = 8,
                    SampleRateHz = 24000,
                    ChannelCount = 1,
                    BitsPerSample = 24,
                    DurationMs = 1000,
                    ByteRate = 72000L,
                    BlockAlignBytes = 3,
                    AudioDataBytes = 72000L,
                    FrameCount = 24000L,
                    BlockRemainderBytes = 0,
                    ValidBitsPerSample = 20,
                    ChannelMask = 4L,
                    AudioEncoding = " EXTENSIBLE PCM ",
                    SampleFormat = " SIGNED INTEGER ",
                    ByteOrder = " LITTLE_ENDIAN ",
                    MixerConversionHint = " integer to float32 ",
                    PlaybackMode = "sound_player",
                    PlaybackHint = "sound_player",
                    MimeType = "audio/wav",
                    FileExtension = ".wav",
                    Reason = "sound_player",
                    FailureCode = string.Empty,
                },
            }));

        fixture.Runtime.DrainInbox();
        SpeechPlaybackSnapshot? playback = fixture.Runtime.GetHealth().BridgeLoop.LastSpeechPlayback;

        Assert.That(playback, Is.Not.Null);
        Assert.That(playback!.SampleRateHz, Is.EqualTo(24000));
        Assert.That(playback.ChannelCount, Is.EqualTo(1));
        Assert.That(playback.BitsPerSample, Is.EqualTo(24));
        Assert.That(playback.DurationMs, Is.EqualTo(1000));
        Assert.That(playback.ByteRate, Is.EqualTo(72000L));
        Assert.That(playback.BlockAlignBytes, Is.EqualTo(3));
        Assert.That(playback.AudioDataBytes, Is.EqualTo(72000L));
        Assert.That(playback.FrameCount, Is.EqualTo(24000L));
        Assert.That(playback.BlockRemainderBytes, Is.Zero);
        Assert.That(playback.ValidBitsPerSample, Is.EqualTo(20));
        Assert.That(playback.ChannelMask, Is.EqualTo(4L));
        Assert.That(playback.AudioEncoding, Is.EqualTo("extensible_pcm"));
        Assert.That(playback.SampleFormat, Is.EqualTo("signed_integer"));
        Assert.That(playback.ByteOrder, Is.EqualTo("little_endian"));
        Assert.That(playback.MixerConversionHint, Is.EqualTo("integer_to_float32"));
        Assert.That(playback.MixerQuantumMs, Is.EqualTo(10));
        Assert.That(playback.MixerQuantumFrames, Is.EqualTo(240));
        Assert.That(playback.MixerQueueDepthEstimate, Is.EqualTo(100L));
        Assert.That(playback.MixerTailFrames, Is.Zero);
        Assert.That(playback.MixerBufferedMs, Is.EqualTo(1000L));
        Assert.That(playback.MixerTailMs, Is.Zero);
    }

    [Test]
    public void PrometheusExporter_RendersKnownMetricsAndHelpText()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.MemoryStore.Remember(1, "Foxparks", "user", "hello there", "chat");

        string output = PrometheusExporter.Render(
            fixture.Runtime.GetHealth(),
            fixture.Runtime.GetInferencePerformanceSnapshot());

        Assert.That(output, Does.Contain("# TYPE palllm_inference_success_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_memory_entries gauge"));
        Assert.That(output, Does.Contain("palllm_memory_entries 1"),
            "Gauge values should reflect live runtime state.");
        Assert.That(output, Does.Contain("palllm_session_dirty 1"),
            "Session dirty after a memory write must serialize as a 1 gauge.");
        Assert.That(output, Does.Contain("# TYPE palllm_tts_call_total counter"),
            "TTS call counter must surface so operators running TTS have observability parity with vision.");
        Assert.That(output, Does.Contain("# TYPE palllm_tts_success_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_tts_failure_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_call_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_success_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_failure_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_endpointing_receipt_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_barge_in_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_endpointing_review_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_confidence_receipt_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_confidence_review_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_timing_receipt_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_timing_review_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_quality_receipt_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_quality_review_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_upstream_request_id_receipt_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_upstream_processing_receipt_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_asr_upstream_phase_timing_receipt_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_vision_call_total counter"));
        Assert.That(output, Does.Contain("# TYPE palllm_inference_recent_window_status gauge"));
        Assert.That(output, Does.Contain("palllm_inference_recent_window_status{status=\"no_data\",budget=\"recent_window\"} 1"));
    }

    [Test]
    public async Task PrometheusExporter_WhenRecentLaneExists_RendersLaneStatusMetrics()
    {
        using var fixture = new TestFixtureContext(
            inferenceClient: new CountingInferenceClient(() => InferenceResult.Succeeded(
                "All clear.",
                new TokenUsage(12, 8, 20),
                providerName: "openai_compatible",
                requestModel: "worker-q4",
                responseModel: "worker-q4",
                latencyMs: 142)),
            inferenceEnabled: true);
        fixture.Options.Fallback.EnablePolicyBypass = false;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 14,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 14,
            RequestId = "runtime-prom-metrics-001",
            UserMessage = "Give me a quick status check.",
            TaskTag = "chat_status",
        }, CancellationToken.None);

        string output = PrometheusExporter.Render(
            fixture.Runtime.GetHealth(),
            fixture.Runtime.GetInferencePerformanceSnapshot());

        Assert.That(output, Does.Contain("# TYPE palllm_inference_lane_status gauge"));
        Assert.That(output, Does.Contain("palllm_inference_lane_status{operation=\"chat\",provider=\"openai_compatible\",model=\"worker-q4\",budget=\"interactive_chat\",status=\"insufficient_data\"} 1"));
        Assert.That(output, Does.Contain("palllm_inference_lane_sample_count{operation=\"chat\",provider=\"openai_compatible\",model=\"worker-q4\",budget=\"interactive_chat\"} 1"));
    }

    [Test]
    public async Task ChatAsync_ActionIntent_DefaultOff_ReturnsNullAction()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Help me gather some wood nearby.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Null,
            "Automation is off by default â€” no action intent should be attached.");
    }

    [Test]
    public async Task ChatAsync_ActionIntent_WhenEnabledAndAllowlisted_EmitsSafeSuggestion()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        fixture.Options.Automation.AllowedActions.Add("waypoint_suggest");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot { Id = 10, DisplayName = "Foxparks", Species = "Foxparks" },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 10,
            UserMessage = "Help me gather some wood from the trees nearby.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Not.Null,
            "Harvest strategy + waypoint_suggest on the allowlist should emit an intent.");
        Assert.That(response.Action!.Type, Is.EqualTo("waypoint_suggest"));
        Assert.That(response.Action.Priority, Is.GreaterThan(0));
        Assert.That(response.Action.Justification, Is.Not.Empty);
        Assert.That(response.Action.SourceStrategy, Is.EqualTo("harvest-window"));
        Assert.That(response.Presentation.Surface.FollowupOrder, Is.EqualTo(new[] { "readout", "cue", "support" }));
    }

    [Test]
    public async Task ChatAsync_ActionIntent_HarvestWindow_EmitsStructuredGatherLoopArguments()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        fixture.Options.Automation.AllowedActions.Add("waypoint_suggest");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            ActiveBaseIds = ["Verdant Hub"],
            NearbyResources = ["wood"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 12,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 12,
            UserMessage = "Help me gather some wood from the trees nearby.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Not.Null);
        Assert.That(response.Action!.Type, Is.EqualTo("waypoint_suggest"));
        Assert.That(response.Action.SourceStrategy, Is.EqualTo("harvest-window"));
        Assert.That(response.Action.Arguments["reason"], Is.EqualTo("resource_gather"));
        Assert.That(response.Action.Arguments["resource"], Is.EqualTo("wood"));
        Assert.That(response.Action.Arguments["origin"], Is.EqualTo("Verdant Hub"));
        Assert.That(response.Action.Arguments["destination"], Is.EqualTo("wood"));
        Assert.That(response.Action.Arguments["waypoint"], Is.EqualTo("Verdant Hub"));
        Assert.That(response.Action.Arguments["mode"], Is.EqualTo("gather_loop"));
    }

    [Test]
    public async Task ChatAsync_ActionIntent_SafeTravel_EmitsStructuredRouteArguments()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        fixture.Options.Automation.AllowedActions.Add("waypoint_suggest");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            CurrentObjective = "Alpha Tower",
            ActiveBaseIds = ["Verdant Hub", "Obsidian Outpost"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 14,
                    DisplayName = "Nitewing",
                    Species = "Nitewing",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 14,
            UserMessage = "What is the safest route to Alpha Tower?",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Not.Null);
        Assert.That(response.Action!.Type, Is.EqualTo("waypoint_suggest"));
        Assert.That(response.Action.SourceStrategy, Is.EqualTo("safe-travel"));
        Assert.That(response.Presentation.Surface.FollowupOrder, Is.EqualTo(new[] { "readout", "support", "cue" }));
        Assert.That(response.Presentation.Surface.PrimaryStageTokenCount, Is.EqualTo(1));
        Assert.That(response.Presentation.Surface.PrimaryAtmosphereTokenCount, Is.EqualTo(1));
        Assert.That(response.Action.Arguments["reason"], Is.EqualTo("safe_route"));
        Assert.That(response.Action.Arguments["bias"], Is.EqualTo("anchor_to_anchor"));
        Assert.That(response.Action.Arguments["origin"], Is.EqualTo("Verdant Hub"));
        Assert.That(response.Action.Arguments["destination"], Is.EqualTo("Alpha Tower"));
        Assert.That(response.Action.Arguments["waypoint"], Is.EqualTo("Obsidian Outpost"));
        Assert.That(response.Action.Arguments["mode"], Is.EqualTo("guided_route"));
    }

    [Test]
    public async Task ChatAsync_WhenTravelFallbackHasRecentNamedRoute_UsesThatAnchorInReply()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            CurrentObjective = "Alpha Tower",
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
                    Id = 77,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                    IsPlayerFaction = true,
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 77,
            UserMessage = "How should we travel to the tower?",
            TaskTag = "chat_travel",
        }, CancellationToken.None);

        Assert.That(response.FallbackStrategy, Is.EqualTo("safe-travel"));
        Assert.That(response.AssistantMessage, Does.Contain("Our latest clean movement was Verdant Hub -> Alpha Tower via Obsidian Outpost"));
        Assert.That(response.AssistantMessage, Does.Contain("we reset to Alpha Tower and path again from there"));
    }

    [Test]
    public async Task ChatAsync_ActionIntent_PerimeterLockdown_EmitsStructuredRecallArguments()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        fixture.Options.Automation.AllowedActions.Add("recall_pals");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            ActiveBaseIds = ["Verdant Hub"],
            NearbyHostiles = ["Rayhound"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 16,
                    DisplayName = "Mammorest",
                    Species = "Mammorest",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 16,
            UserMessage = "The base perimeter is getting noisy.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Not.Null);
        Assert.That(response.Action!.Type, Is.EqualTo("recall_pals"));
        Assert.That(response.Action.SourceStrategy, Is.EqualTo("perimeter-lockdown"));
        Assert.That(response.Presentation.Surface.FollowupOrder, Is.EqualTo(new[] { "support", "readout", "cue" }));
        Assert.That(response.Action.Arguments["reason"], Is.EqualTo("base_defense"));
        Assert.That(response.Action.Arguments["anchor"], Is.EqualTo("Verdant Hub"));
        Assert.That(response.Action.Arguments["pal_group"], Is.EqualTo("party"));
        Assert.That(response.Action.Arguments["status_change"], Is.EqualTo("recall_defense_line"));
        Assert.That(response.Action.Arguments["mode"], Is.EqualTo("base_lockdown"));
    }

    [Test]
    public async Task ChatAsync_ActionIntent_BaseNetwork_EmitsStructuredQueueArguments()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        fixture.Options.Automation.AllowedActions.Add("request_craft_queue");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
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
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 18,
                    DisplayName = "Anubis",
                    Species = "Anubis",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 18,
            UserMessage = "How should we split work between our bases?",
            TaskTag = "chat_bases",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Not.Null);
        Assert.That(response.Action!.Type, Is.EqualTo("request_craft_queue"));
        Assert.That(response.Action.SourceStrategy, Is.EqualTo("base-network"));
        Assert.That(response.Presentation.Surface.FollowupOrder, Is.EqualTo(new[] { "readout", "support", "cue" }));
        Assert.That(response.Action.Arguments["reason"], Is.EqualTo("specialization"));
        Assert.That(response.Action.Arguments["primary_base"], Is.EqualTo("Verdant Hub"));
        Assert.That(response.Action.Arguments["secondary_base"], Is.EqualTo("Obsidian Outpost"));
        Assert.That(response.Action.Arguments["station"], Is.EqualTo("logistics_planner"));
        Assert.That(response.Action.Arguments["item"], Is.EqualTo("specialization_queue"));
        Assert.That(response.Action.Arguments["quantity"], Is.EqualTo("1"));
        Assert.That(response.Action.Arguments["status"], Is.EqualTo("requested"));
    }

    [Test]
    public async Task ChatAsync_ActionIntent_AllowlistBlocksUnapprovedType()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        // Enable a different action than the one the strategy would suggest.
        fixture.Options.Automation.AllowedActions.Add("request_craft_queue");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Help me gather some wood from the trees nearby.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Null,
            "Strategy mapped to waypoint_suggest, which isn't allowlisted â€” emission must be blocked.");
    }

    [Test]
    public async Task ChatAsync_ActionIntent_WhenEmitToOutboxEnabled_PersistsRenderContract()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        fixture.Options.Automation.AllowedActions.Add("waypoint_suggest");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 44,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 44,
            UserMessage = "Help me gather some wood from the trees nearby.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Not.Null);

        string outboxFile = Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json").Single();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outboxFile));
        JsonElement payload = document.RootElement.GetProperty("Payload");

        Assert.That(payload.GetProperty("Action").GetProperty("Type").GetString(), Is.EqualTo("waypoint_suggest"));
        Assert.That(payload.GetProperty("Action").GetProperty("Justification").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Summary").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Audio").GetProperty("BehaviorId").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Audio").GetProperty("SubtitleStyle").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Audio").GetProperty("VoicePrint").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Visual").GetProperty("HudAccent").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Visual").GetProperty("WorldMarker").GetString(), Is.Not.Empty,
            "Outbox must preserve the presentation fields the UE4SS renderer consumes.");
        Assert.That(payload.GetProperty("Presentation").GetProperty("Visual").GetProperty("ScreenTreatment").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Visual").GetProperty("PortraitExpression").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Visual").GetProperty("HoldMs").GetInt32(), Is.GreaterThan(0));
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("FamilyId").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("LayoutMode").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("PathBadge").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("FamilyBadge").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("PhaseBadge").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("WidthChars").GetInt32(), Is.GreaterThan(0));
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("PrimaryDurationMs").GetInt32(), Is.GreaterThan(0));
    }

    [Test]
    public async Task ChatAsync_ActionIntent_WhenEmitToOutboxDisabled_OmitsActionEnvelope()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Automation.Enabled = true;
        fixture.Options.Automation.EmitToOutbox = false;
        fixture.Options.Automation.AllowedActions.Add("waypoint_suggest");
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Help me gather some wood from the trees nearby.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.Action, Is.Not.Null,
            "Chat response should still carry the advisory action even when outbox emission is disabled.");

        string outboxFile = Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json").Single();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(outboxFile));
        JsonElement payload = document.RootElement.GetProperty("Payload");

        Assert.That(payload.TryGetProperty("Action", out _), Is.False,
            "Outbox gating must be able to suppress actionable payloads while still allowing text delivery.");
    }

    [Test]
    public async Task ChatAsync_WhenRateLimitExceeded_ServesDeterministicFallback()
    {
        string root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new PalLlmOptions
            {
                PalSavedRoot = root,
                Inference = new InferenceOptions { Enabled = true },
                Session = new SessionOptions { Enabled = false, EnableAutosave = false },
                Fallback = new FallbackOptions { MaxCharacterRequestsPerMinute = 2 },
            };
            var counting = new CountingInferenceClient(() =>
                InferenceResult.Succeeded("live reply", new TokenUsage(10, 10, 20)));
            var runtime = new PalLlmRuntime(options, counting);
            runtime.UpdateSnapshot(new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                Characters =
                [
                    new GameCharacterSnapshot { Id = 600, DisplayName = "Anubis", Species = "Anubis" },
                ],
            });

            // Three rapid-fire creative asks for the same character â€” creative messages
            // normally avoid the fast-lane bypass, so they'd all hit inference. The rate
            // limiter should cap at the configured 2.
            for (int i = 0; i < 3; i++)
            {
                await runtime.ChatAsync(new ChatRequest
                {
                    CharacterId = 600,
                    UserMessage = $"Tell me a short story about the storm, part {i}.",
                    TaskTag = "chat_story",
                }, CancellationToken.None);
            }

            RuntimeHealth health = runtime.GetHealth();
            Assert.That(counting.CallCount, Is.EqualTo(2),
                "Rate limiter should cap inference calls at the configured ceiling.");
            Assert.That(health.RateLimitedCount, Is.EqualTo(1),
                "RateLimitedCount must increment when a request was diverted to fallback.");

            // The third call should have taken the rate-limited fallback path.
            ChatResponse diverted = await runtime.ChatAsync(new ChatRequest
            {
                CharacterId = 600,
                UserMessage = "Another story please about the forest.",
                TaskTag = "chat_story",
            }, CancellationToken.None);
            Assert.That(diverted.ResponsePath, Is.EqualTo("rate_limited_fallback"));
            Assert.That(diverted.InferenceBypassed, Is.True);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task ChatAsync_RequestId_GeneratedWhenOmitted_ReusedWhenProvided_PropagatesToOutbox()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        ChatResponse generated = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Quick status check.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(generated.RequestId, Is.Not.Null.And.Not.Empty,
            "Omitting RequestId must auto-generate a short trace id.");
        Assert.That(generated.RequestId, Does.StartWith("chat-"));

        ChatResponse supplied = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Another short check.",
            TaskTag = "chat_general",
            RequestId = "pal-trace-123",
        }, CancellationToken.None);

        Assert.That(supplied.RequestId, Is.EqualTo("pal-trace-123"));

        // Outbox envelope for the supplied request must carry the same id so a log
        // line and an in-game render can be paired.
        string[] outboxFiles = Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json");
        Assert.That(outboxFiles, Has.Length.EqualTo(2));
        bool foundCorrelated = false;
        foreach (string file in outboxFiles)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.GetProperty("Payload").GetProperty("RequestId").GetString() == "pal-trace-123")
            {
                foundCorrelated = true;
                break;
            }
        }

        Assert.That(foundCorrelated, Is.True,
            "Outbox envelope must carry the caller-supplied RequestId for correlation.");
    }

    [Test]
    public async Task OutboxRetention_PrunesOldestWhenCapExceeded()
    {
        using var fixture = new TestFixtureContext();
        // Tight cap so the test trips retention quickly without spinning through 100 chats.
        fixture.Options.Bridge.OutboxMaxFiles = 3;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        for (int i = 0; i < 6; i++)
        {
            await fixture.Runtime.ChatAsync(new ChatRequest
            {
                UserMessage = $"Note number {i}: camp prep.",
                TaskTag = "chat_general",
            }, CancellationToken.None);
        }

        string[] files = Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json");
        Assert.That(files, Has.Length.AtMost(3),
            "Outbox retention must keep the directory at or under the configured cap.");
    }

    [Test]
    public async Task SessionAutosave_WhenNothingChanged_SkipsDiskWrite()
    {
        string root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new PalLlmOptions
            {
                PalSavedRoot = root,
                Session = new SessionOptions { Enabled = true, EnableAutosave = false },
            };
            var runtime = new PalLlmRuntime(options, new DisabledInferenceClient());
            runtime.MemoryStore.Remember(1, "Foxparks", "user", "first note", "chat");

            SessionPersistenceResult first = runtime.SaveSession();
            Assert.That(first.Success, Is.True);
            DateTime firstWrittenUtc = File.GetLastWriteTimeUtc(options.SessionFilePath);

            // Short wait so a second write would produce a strictly later timestamp.
            await Task.Delay(50);

            // SaveIfDirty on unchanged state must not touch the file.
            SessionPersistenceResult second = runtime.SaveSessionIfDirty();
            Assert.That(second.Success, Is.True);
            Assert.That(second.StatusMessage, Does.Contain("clean").IgnoreCase);
            Assert.That(File.GetLastWriteTimeUtc(options.SessionFilePath), Is.EqualTo(firstWrittenUtc),
                "Clean-session autosave must not rewrite the file.");

            // A new memory entry dirties the state; the next SaveIfDirty should write.
            runtime.MemoryStore.Remember(1, "Foxparks", "user", "second note", "chat");
            await Task.Delay(50);
            SessionPersistenceResult third = runtime.SaveSessionIfDirty();
            Assert.That(third.Success, Is.True);
            Assert.That(File.GetLastWriteTimeUtc(options.SessionFilePath), Is.GreaterThan(firstWrittenUtc),
                "Dirty state must trigger a fresh write.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public void DirectoryRetention_EnforcesMaxFilesAndAge()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PalLLM.Retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Four files, oldest well beyond the age cutoff.
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(dir, $"file-{i}.json");
                File.WriteAllText(path, "x");
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-i * 2));
            }

            // Age cap of 3 hours should remove the 4-hour and 6-hour files (files at
            // indices 2 and 3). Max-files cap of 1 should further reduce to 1.
            int removed = DirectoryRetention.Enforce(dir, maxFiles: 1, maxAgeHours: 3);

            string[] remaining = Directory.GetFiles(dir);
            Assert.That(remaining, Has.Length.EqualTo(1), "Retention must trim to the configured file cap.");
            Assert.That(removed, Is.EqualTo(3));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void DirectoryRetention_HandlesOverlappingPatternsWithoutDoubleDeleting()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PalLLM.Retention", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            DateTime now = DateTime.UtcNow;
            string keepNewest = Path.Combine(dir, "keep-newest.json");
            string keepRecent = Path.Combine(dir, "keep-recent.json");
            string dropByCount = Path.Combine(dir, "drop-by-count.json");
            string dropByAge = Path.Combine(dir, "drop-by-age.json");

            File.WriteAllText(keepNewest, "x");
            File.WriteAllText(keepRecent, "x");
            File.WriteAllText(dropByCount, "x");
            File.WriteAllText(dropByAge, "x");
            File.SetLastWriteTimeUtc(keepNewest, now);
            File.SetLastWriteTimeUtc(keepRecent, now.AddMinutes(-5));
            File.SetLastWriteTimeUtc(dropByCount, now.AddMinutes(-10));
            File.SetLastWriteTimeUtc(dropByAge, now.AddHours(-8));

            int removed = DirectoryRetention.Enforce(
                dir,
                maxFiles: 2,
                maxAgeHours: 4,
                "*.json",
                "keep-*.json",
                "drop-*.json");

            string[] remaining = Directory.GetFiles(dir)
                .Select(path => Path.GetFileName(path) ?? string.Empty)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
            Assert.That(removed, Is.EqualTo(2));
            Assert.That(remaining, Is.EqualTo(new[] { "keep-newest.json", "keep-recent.json" }));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Test]
    public void Health_ExposesResourceFootprintAndSessionFlags()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.MemoryStore.Remember(1, "Foxparks", "user", "hello", "chat");

        File.WriteAllText(Path.Combine(fixture.Options.BridgeInboxDir, "pending.json"), "{}");

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(health.InboxPendingCount, Is.EqualTo(1),
            "Inbox pending count should reflect files sitting in the inbox directory.");
        Assert.That(health.SessionDirty, Is.True,
            "After a mutation with no save, session must report dirty.");
        Assert.That(health.SessionLastSavedAtUtc, Is.Null);
        Assert.That(health.BridgeEnabled, Is.True);
        Assert.That(health.InferenceModel, Is.EqualTo(fixture.Options.Inference.Model));
        Assert.That(health.VisionEnabled, Is.EqualTo(fixture.Options.Vision.Enabled));
        Assert.That(health.VisionModel, Is.EqualTo(fixture.Options.Vision.Model));
        Assert.That(health.TtsEnabled, Is.EqualTo(fixture.Options.Tts.Enabled));
        Assert.That(health.AutomationEnabled, Is.EqualTo(fixture.Options.Automation.Enabled));
    }

    [Test]
    public void SaveSession_RoundTrip_ReloadsMemoryAndRelationships()
    {
        string root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            // First runtime: seed some state, save.
            PalLlmOptions options = new()
            {
                PalSavedRoot = root,
                Session = new SessionOptions { Enabled = true, EnableAutosave = false },
            };
            var firstRuntime = new PalLlmRuntime(options, new DisabledInferenceClient());

            firstRuntime.MemoryStore.Remember(7, "Foxparks", "user", "Foxparks adores the forge.", "lore");
            firstRuntime.MemoryStore.Remember(7, "Foxparks", "assistant", "I will stay close through the night.", "chat");
            firstRuntime.MemoryStore.Remember(8, "Lamball", "user", "Lamball prefers wool bedding.", "lore");

            // Drive the relationship tracker through the public chat path so affinity
            // and interaction counts persist in a realistic shape.
            firstRuntime.UpdateSnapshot(new GameWorldSnapshot
            {
                IsWorldLoaded = true,
                Characters =
                [
                    new GameCharacterSnapshot { Id = 7, DisplayName = "Foxparks", Species = "Foxparks" },
                ],
            });
            firstRuntime.ChatAsync(new ChatRequest
            {
                CharacterId = 7,
                UserMessage = "Thanks for the loyalty â€” we did great together.",
                TaskTag = "chat_general",
            }, CancellationToken.None).GetAwaiter().GetResult();

            SessionPersistenceResult save = firstRuntime.SaveSession();
            Assert.That(save.Success, Is.True, save.StatusMessage);
            Assert.That(save.MemoryEntryCount, Is.GreaterThanOrEqualTo(4),
                "Both seeded memories plus chat-round memories should be persisted.");
            Assert.That(save.RelationshipCount, Is.EqualTo(1));
            Assert.That(File.Exists(options.SessionFilePath), Is.True);

            // Second runtime: same root, Session.Enabled=true â†’ should auto-load.
            var secondRuntime = new PalLlmRuntime(options, new DisabledInferenceClient());

            Assert.That(secondRuntime.MemoryStore.Count, Is.GreaterThanOrEqualTo(4),
                "Session load must restore memory entries.");

            CharacterRelationship? restored = secondRuntime.GetRelationship(7);
            Assert.That(restored, Is.Not.Null);
            Assert.That(restored!.Affinity, Is.GreaterThan(0));
            Assert.That(restored.InteractionCount, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public void LoadSession_FallsBackToBackupWhenPrimaryCorrupt()
    {
        string root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new PalLlmOptions
            {
                PalSavedRoot = root,
                Session = new SessionOptions { Enabled = true, EnableAutosave = false },
            };

            // Save twice so a .bak file exists carrying the first save's state.
            var first = new PalLlmRuntime(options, new DisabledInferenceClient());
            first.MemoryStore.Remember(1, "Foxparks", "user", "first save entry", "chat");
            first.SaveSession();
            first.MemoryStore.Remember(1, "Foxparks", "user", "second save entry", "chat");
            first.SaveSession();

            Assert.That(File.Exists(options.SessionFilePath), Is.True);
            Assert.That(File.Exists(options.SessionFilePath + ".bak"), Is.True,
                "Rotated save must leave a .bak of the previous state.");

            // Corrupt the primary file. The .bak should still be good.
            File.WriteAllText(options.SessionFilePath, "{ this is not valid json");

            // A fresh runtime pointing at the same root should recover via .bak.
            var second = new PalLlmRuntime(options, new DisabledInferenceClient());
            Assert.That(second.MemoryStore.Count, Is.GreaterThanOrEqualTo(1),
                "Backup file must restore at least the first-save state when the primary is unreadable.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public void LoadSession_WhenFileMissing_ReturnsGracefulFailure()
    {
        using var fixture = new TestFixtureContext();
        SessionPersistenceResult result = fixture.Runtime.LoadSession();
        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("No session file").IgnoreCase);
    }

    [Test]
    public void LoadSession_WhenSchemaVersionIsFromFuture_RefusesToLoad()
    {
        // A session file stamped with a schema version the current build does not
        // understand must be rejected explicitly. Silently deserialising whichever
        // fields happen to match the current shape would corrupt memory state on
        // downgrade, so the runtime fails fast and lets the operator migrate.
        string root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new PalLlmOptions
            {
                PalSavedRoot = root,
                Session = new SessionOptions { Enabled = true, EnableAutosave = false },
            };
            Directory.CreateDirectory(options.RuntimeRoot);

            // Write a session file that advertises a far-future schema version but is
            // otherwise valid â€” the guard should trip on SchemaVersion alone.
            File.WriteAllText(options.SessionFilePath, """
                {
                  "SchemaVersion": 999,
                  "SavedAtUtc": "2099-01-01T00:00:00Z",
                  "MemoryEntries": [],
                  "Relationships": []
                }
                """);

            var runtime = new PalLlmRuntime(options, new DisabledInferenceClient());
            SessionPersistenceResult result = runtime.LoadSession();

            Assert.That(result.Success, Is.False);
            Assert.That(result.StatusMessage, Does.Contain("newer than supported"));
            Assert.That(runtime.MemoryStore.Count, Is.Zero,
                "Future-schema files must not partially populate the memory store.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public void LoadSession_WhenPrimaryOversized_FallsBackToBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new PalLlmOptions
            {
                PalSavedRoot = root,
                Session = new SessionOptions
                {
                    Enabled = false,
                    EnableAutosave = false,
                    MaxPersistedBytes = 1024,
                },
            };

            var first = new PalLlmRuntime(options, new DisabledInferenceClient());
            first.MemoryStore.Remember(1, "Foxparks", "user", "first save entry", "chat");
            first.SaveSession();
            first.MemoryStore.Remember(1, "Foxparks", "user", "second save entry", "chat");
            first.SaveSession();

            Assert.That(File.Exists(options.SessionFilePath + ".bak"), Is.True,
                "Rotated save must leave a .bak of the previous state.");

            File.WriteAllText(options.SessionFilePath, new string('x', options.Session.MaxPersistedBytes + 64));

            var second = new PalLlmRuntime(options, new DisabledInferenceClient());
            SessionPersistenceResult result = second.LoadSession();

            Assert.That(result.Success, Is.True, result.StatusMessage);
            Assert.That(result.StatusMessage, Does.Contain(".bak fallback"));
            Assert.That(second.MemoryStore.Count, Is.GreaterThanOrEqualTo(1),
                "Oversized primary session files should recover from the last-known-good backup.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public void LoadSession_WhenFileExceedsConfiguredByteCap_ReturnsStableFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new PalLlmOptions
            {
                PalSavedRoot = root,
                Session = new SessionOptions
                {
                    Enabled = false,
                    EnableAutosave = false,
                    MaxPersistedBytes = 1024,
                },
            };
            Directory.CreateDirectory(options.RuntimeRoot);
            File.WriteAllText(options.SessionFilePath, new string('x', options.Session.MaxPersistedBytes + 64));

            var runtime = new PalLlmRuntime(options, new DisabledInferenceClient());
            SessionPersistenceResult result = runtime.LoadSession();

            Assert.That(result.Success, Is.False);
            Assert.That(result.FilePath, Is.EqualTo(string.Empty),
                "Failed session loads should not disclose the local session path in the public contract.");
            Assert.That(result.StatusMessage, Is.EqualTo("Session file exceeds PalLLM:Session:MaxPersistedBytes."));
            Assert.That(runtime.MemoryStore.Count, Is.Zero,
                "Oversized session files must not partially populate memory state.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Test]
    public async Task SynthesizeSpeechAsync_EnforcesTtsRetentionCap()
    {
        // Codex wired a DirectoryRetention.Enforce call into the TTS write path to
        // stop audio artifacts from accumulating forever. This test pins that
        // behaviour so a future refactor can't regress it silently.
        byte[] fakeAudio = new byte[] { 0x52, 0x49, 0x46, 0x46, 0x00, 0x00, 0x00, 0x00 };
        var tts = new CannedTtsClient(() => TtsResult.Succeeded(fakeAudio, "audio/wav", "test-voice"));
        using var fixture = new TestFixtureContext(ttsClient: tts, ttsEnabled: true);
        fixture.Options.Tts.MaxStoredFiles = 3;
        fixture.Options.Tts.MaxStoredAgeHours = 0;  // disable age-based pruning so the cap is unambiguous

        for (int i = 0; i < 6; i++)
        {
            TtsSynthesizeResponse response = await fixture.Runtime.SynthesizeSpeechAsync(new TtsSynthesizeRequest
            {
                Text = $"line number {i}",
                WriteToDisk = true,
            }, CancellationToken.None);

            Assert.That(response.Success, Is.True);
        }

        string[] files = Directory.GetFiles(fixture.Options.TtsDir);
        Assert.That(files, Has.Length.AtMost(3),
            "TTS retention must cap on-disk audio artifacts at MaxStoredFiles.");
    }

    [Test]
    public async Task ChatAsync_AccumulatesTokenUsageInHealth()
    {
        var inference = new CountingInferenceClient(() =>
            InferenceResult.Succeeded("live reply", new TokenUsage(120, 40, 160)));
        using var fixture = new TestFixtureContext(inference, inferenceEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot { Id = 500, DisplayName = "Lyleen", Species = "Lyleen" },
            ],
        });

        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 500,
            UserMessage = "Tell me a short campfire story.",  // creative â†’ force live inference
            TaskTag = "chat_story",
        }, CancellationToken.None);
        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 500,
            UserMessage = "Tell me another story with dialogue between two characters.",
            TaskTag = "chat_story",
        }, CancellationToken.None);

        RuntimeHealth health = fixture.Runtime.GetHealth();
        Assert.That(inference.CallCount, Is.EqualTo(2));
        Assert.That(health.TotalPromptTokens, Is.EqualTo(240));
        Assert.That(health.TotalCompletionTokens, Is.EqualTo(80));
        Assert.That(health.TotalInferenceTokens, Is.EqualTo(320));
    }

    [Test]
    public async Task Health_AfterOutboxWrite_RefreshesOperationalCountsImmediately()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        RuntimeHealth before = fixture.Runtime.GetHealth();
        Assert.That(before.OutboxPendingCount, Is.Zero);

        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Keep watch over the camp.",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        RuntimeHealth after = fixture.Runtime.GetHealth();
        Assert.That(after.OutboxPendingCount, Is.EqualTo(1),
            "Operational count caching must invalidate when PalLLM writes a new outbox envelope.");
    }

    [Test]
    public async Task VisionReportedCombat_FlowsIntoFallbackSelection()
    {
        // End-to-end: vision says InCombat=true with 3 hostiles visible, then a
        // subsequent chat call should see Peak phase and pick a combat-oriented
        // strategy. This covers the full sensor â†’ snapshot â†’ context â†’ engine loop.
        const string modelOutput = """
            { "TimeOfDay": "day", "Weather": "clear", "Biome": "highlands",
              "InCombat": true, "InBase": false, "VisibleHostileCount": 3,
              "PlayerActivity": "under attack", "Hostiles": ["Syndicate Gunner"],
              "Resources": [] }
            """;

        var vision = new CannedVisionClient(() => VisionResult.Succeeded(modelOutput));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 400,
                    DisplayName = "Vanwyrm",
                    Species = "Vanwyrm",
                    HealthFraction = 0.5f,
                },
            ],
        });

        VisionWorldStateResponse ingest = await fixture.Runtime.ExtractWorldStateAsync(new VisionWorldStateRequest
        {
            ImageBase64 = SamplePngBase64,
            ApplyToSnapshot = true,
        }, CancellationToken.None);
        Assert.That(ingest.Success, Is.True);
        Assert.That(ingest.Applied, Is.True);

        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        Assert.That(worldState.Snapshot.NearbyHostiles, Has.Count.EqualTo(3),
            "Vision-reported hostile count must pad the list so downstream hostile-count checks see the right number.");

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 400,
            UserMessage = "What do we do?",
            TaskTag = "chat_defense",
        }, CancellationToken.None);

        Assert.That(response.FallbackPhase, Is.EqualTo("Peak"),
            "Vision-reported combat + threat escalation must drive Peak phase.");
        Assert.That(response.FallbackStrategy, Is.EqualTo("emergency-triage"));
    }

    [Test]
    public async Task ProcessScreenshotsAsync_ReadsDirectory_AppliesWorldState_ArchivesFiles()
    {
        const string modelOutput = """
            { "TimeOfDay": "night", "Weather": "fog", "Biome": "forest",
              "InCombat": false, "InBase": true, "VisibleHostileCount": 0,
              "PlayerActivity": "patrolling the perimeter",
              "Hostiles": [], "Resources": ["mushrooms"] }
            """;

        var vision = new CannedVisionClient(() => VisionResult.Succeeded(modelOutput));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
        });

        // Drop two fake PNG files into the screenshots directory â€” the canned vision
        // client returns the same structured JSON regardless of bytes.
        byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        File.WriteAllBytes(Path.Combine(fixture.Options.BridgeScreenshotsDir, "scene-1.png"), fakePng);
        File.WriteAllBytes(Path.Combine(fixture.Options.BridgeScreenshotsDir, "scene-2.png"), fakePng);

        ScreenshotIngestResult result = await fixture.Runtime.ProcessScreenshotsAsync(CancellationToken.None);

        Assert.That(result.ProcessedCount, Is.EqualTo(2));
        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(vision.CallCount, Is.EqualTo(2));
        Assert.That(vision.LastImageBase64, Is.EqualTo(Convert.ToBase64String(fakePng)));
        Assert.That(vision.LastImageMimeType, Is.EqualTo("image/png"));

        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        Assert.That(worldState.Snapshot.Weather, Is.EqualTo("fog"));
        Assert.That(worldState.Snapshot.Biome, Is.EqualTo("forest"));
        Assert.That(worldState.Snapshot.NearbyResources, Contains.Item("mushrooms"));
        Assert.That(worldState.Snapshot.IsInBase, Is.True);

        Assert.That(Directory.GetFiles(fixture.Options.BridgeScreenshotsDir, "*.png"), Is.Empty,
            "Processed screenshots should be archived, not left in the inbox.");
        Assert.That(Directory.GetFiles(fixture.Options.BridgeArchiveDir, "*.png"), Has.Length.EqualTo(2));
    }

    [Test]
    public async Task ProcessScreenshotsAsync_WhenMaxFilesSpecified_ProcessesBoundedChunk()
    {
        const string modelOutput = """{ "Weather": "fog" }""";
        var vision = new CannedVisionClient(() => VisionResult.Succeeded(modelOutput));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);

        byte[] fakePng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        File.WriteAllBytes(Path.Combine(fixture.Options.BridgeScreenshotsDir, "scene-1.png"), fakePng);
        File.WriteAllBytes(Path.Combine(fixture.Options.BridgeScreenshotsDir, "scene-2.png"), fakePng);

        ScreenshotIngestResult result = await fixture.Runtime.ProcessScreenshotsAsync(
            CancellationToken.None,
            maxFiles: 1);

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(vision.CallCount, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Options.BridgeScreenshotsDir, "*.png"), Has.Length.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Options.BridgeArchiveDir, "*.png"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ProcessScreenshotsAsync_WhenVisionDisabled_IsNoOp()
    {
        using var fixture = new TestFixtureContext();  // vision disabled
        File.WriteAllBytes(Path.Combine(fixture.Options.BridgeScreenshotsDir, "scene.png"), new byte[] { 0x89, 0x50 });

        ScreenshotIngestResult result = await fixture.Runtime.ProcessScreenshotsAsync(CancellationToken.None);

        Assert.That(result.ProcessedCount, Is.Zero);
        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(Directory.GetFiles(fixture.Options.BridgeScreenshotsDir, "*.png"), Has.Length.EqualTo(1),
            "Files must not be moved when vision is disabled â€” let the watcher stay a no-op.");
    }

    [Test]
    public void PrunePendingScreenshots_WhenRetentionExceeded_DeletesOldestPendingFiles()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Vision.PendingScreenshotMaxFiles = 1;
        fixture.Options.Vision.PendingScreenshotMaxAgeHours = 24;

        string oldPath = Path.Combine(fixture.Options.BridgeScreenshotsDir, "scene-old.png");
        string newPath = Path.Combine(fixture.Options.BridgeScreenshotsDir, "scene-new.png");

        File.WriteAllBytes(oldPath, [0x89, 0x50, 0x4E, 0x47]);
        File.WriteAllBytes(newPath, [0x89, 0x50, 0x4E, 0x47]);
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddMinutes(-10));
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow);

        int removed = fixture.Runtime.PrunePendingScreenshots();

        Assert.That(removed, Is.EqualTo(1));
        Assert.That(File.Exists(oldPath), Is.False);
        Assert.That(File.Exists(newPath), Is.True);
    }

    [Test]
    public async Task ProcessScreenshotsAsync_WhenScreenshotExceedsConfiguredCap_FailsBeforeVisionCall()
    {
        var vision = new CannedVisionClient(() => VisionResult.Succeeded("{}"));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);
        fixture.Options.Vision.MaxImageBytes = 4;

        File.WriteAllBytes(
            Path.Combine(fixture.Options.BridgeScreenshotsDir, "oversized.png"),
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        ScreenshotIngestResult result = await fixture.Runtime.ProcessScreenshotsAsync(CancellationToken.None);

        Assert.That(result.ProcessedCount, Is.Zero);
        Assert.That(result.FailedCount, Is.EqualTo(1));
        Assert.That(vision.CallCount, Is.Zero,
            "Files that already exceed the configured image-size cap should fail before any model call.");
        Assert.That(Directory.GetFiles(fixture.Options.BridgeScreenshotsDir, "*.png"), Is.Empty);
        Assert.That(Directory.GetFiles(fixture.Options.BridgeFailedDir, "*.png"), Has.Length.EqualTo(1));
    }

    [Test]
    public async Task ChatAsync_WithImage_DoesNotInvokeVisionWhenDisabled()
    {
        var vision = new CannedVisionClient(() => VisionResult.Succeeded("shouldn't be called"));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: false);

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Status?",
            ImageBase64 = SamplePngBase64,
        }, CancellationToken.None);

        Assert.That(vision.CallCount, Is.Zero,
            "Disabled vision must be skipped even when an image is supplied.");
        // World isn't loaded in this fixture, so the snapshot fallback
        // returns empty and no Visual context line is spliced.
        Assert.That(response.SystemPrompt, Does.Not.Contain("Visual context"));
    }

    [Test]
    public async Task ChatAsync_WithImage_WhenVisionDisabledButWorldLoaded_UsesSnapshotFallback()
    {
        // The "diverse high-quality fallback for every feasible item"
        // commitment for vision: when the multimodal model is off but the
        // player still attaches a screenshot, PalLLM composes a terse
        // deterministic scene description from the live GameWorldSnapshot
        // and splices it into the system prompt. Companions never feel
        // "blind" just because vision is disabled.
        using var fixture = new TestFixtureContext(visionEnabled: false);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            TimeOfDay = "dusk",
            Biome = "tropical",
            Weather = "clear",
            IsInBase = true,
            ActiveBaseIds = ["kindling-hollow"],
            KnownBases = [new GameBaseSnapshot { BaseId = "kindling-hollow" }],
            Characters =
            [
                new GameCharacterSnapshot { Id = 1, DisplayName = "Chillet" },
            ],
            NearbyHostiles = ["Syndicate Thug"],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "What's the situation?",
            ImageBase64 = SamplePngBase64,
        }, CancellationToken.None);

        Assert.That(response.SystemPrompt, Does.Contain("Visual context"));
        Assert.That(response.SystemPrompt, Does.Contain("from snapshot fallback"));
        Assert.That(response.SystemPrompt, Does.Contain("kindling-hollow"));
        Assert.That(response.SystemPrompt, Does.Contain("Syndicate Thug"));
    }

    [Test]
    public async Task ChatAsync_WithImage_WhenVisionFails_FallsBackToSnapshotDescription()
    {
        // Vision model is configured and enabled but returns a failure
        // (endpoint unreachable, 5xx, etc.). Instead of dropping visual
        // context entirely, the runtime falls back to snapshot composition
        // so the player still gets situationally-aware replies.
        var vision = new CannedVisionClient(() =>
            VisionResult.Failed("Vision endpoint unreachable: simulated."));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            TimeOfDay = "night",
            IsInBase = false,
            Characters =
            [
                new GameCharacterSnapshot { Id = 1, DisplayName = "Lifmunk" },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "What do you see?",
            ImageBase64 = SamplePngBase64,
        }, CancellationToken.None);

        Assert.That(vision.CallCount, Is.EqualTo(1),
            "Vision was enabled â€” the primary client must be tried first.");
        Assert.That(response.SystemPrompt, Does.Contain("Visual context"));
        Assert.That(response.SystemPrompt, Does.Contain("from snapshot fallback"),
            "A failed vision call must be rescued by the snapshot fallback.");
        Assert.That(response.SystemPrompt, Does.Contain("Lifmunk"));
    }

    [Test]
    public async Task ChatAsync_WithImage_WhenVisionSucceeds_UsesLiveDescriptionNotFallback()
    {
        // Happy path: vision works, description is live. The snapshot
        // fallback must NOT be used â€” it's a backup, not a layered
        // addition. Double-spliced context would confuse the model.
        var vision = new CannedVisionClient(() =>
            VisionResult.Succeeded("A night-time base with one Chillet circling the gate."));
        using var fixture = new TestFixtureContext(visionClient: vision, visionEnabled: true);
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = ["Syndicate Thug"],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "What's up?",
            ImageBase64 = SamplePngBase64,
        }, CancellationToken.None);

        Assert.That(response.SystemPrompt, Does.Contain("from vision model"));
        Assert.That(response.SystemPrompt, Does.Not.Contain("from snapshot fallback"));
        Assert.That(response.SystemPrompt, Does.Contain("Chillet circling the gate"));
    }

    // 8x8 red PNG (144 bytes decoded). Any non-empty base64 image works for the mocked
    // client because it ignores the payload â€” the test only exercises plumbing.
    private const string SamplePngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAIAQMAAAD+wSzIAAAABlBMVEX///+/v7+jQ3Y5AAAADklEQVQI12P4//8/w38GIAXDAQAIkAPN4Pa6vQAAAABJRU5ErkJggg==";

    [Test]
    public void PackValidator_AcceptsWellFormedPackAndRejectsBadOnes()
    {
        string validJson = JsonSerializer.Serialize(new
        {
            Name = "Sample Pack",
            Author = "QA",
            Characters = new object[]
            {
                new { Id = "foxp-1", Name = "Foxparks" },
                new { Id = "chill-1", Name = "Chillet" },
            },
            Relationships = new object[]
            {
                new { CharacterA = "foxp-1", CharacterB = "chill-1", Type = "allied", Opinion = 40 },
            },
            MemorySeeds = new object[]
            {
                new { CharacterId = "foxp-1", Content = "Loves a warm forge.", Importance = 0.6f },
            },
        });

        NarrativePackValidationResult validResult = NarrativePackValidator.Validate(validJson);
        Assert.That(validResult.IsValid, Is.True, string.Join(", ", validResult.Errors.Select(e => $"{e.Path}: {e.Message}")));
        Assert.That(validResult.CharacterCount, Is.EqualTo(2));

        string badJson = JsonSerializer.Serialize(new
        {
            Name = "",  // missing
            Characters = new object[]
            {
                new { Id = "", Name = "Nameless" },  // missing id
                new { Id = "dup", Name = "A" },
                new { Id = "dup", Name = "B" },  // duplicate id
            },
            Relationships = new object[]
            {
                new { CharacterA = "dup", CharacterB = "nonexistent", Opinion = 200 },  // unknown + out of range
            },
            MemorySeeds = new object[]
            {
                new { CharacterId = "ghost", Content = "", Importance = 2f },  // unknown + empty + out of range
            },
        });

        NarrativePackValidationResult badResult = NarrativePackValidator.Validate(badJson);
        Assert.That(badResult.IsValid, Is.False);
        Assert.That(badResult.Errors.Select(e => e.Path),
            Has.Some.EqualTo("Name")
                .And.Some.Contains("Characters[0].Id")
                .And.Some.Contains("Characters[2].Id")
                .And.Some.Contains("Relationships[0].CharacterB")
                .And.Some.Contains("Relationships[0].Opinion")
                .And.Some.Contains("MemorySeeds[0].CharacterId")
                .And.Some.Contains("MemorySeeds[0].Content")
                .And.Some.Contains("MemorySeeds[0].Importance"));
    }

    [Test]
    public void PackValidator_RejectsMalformedJson()
    {
        NarrativePackValidationResult result = NarrativePackValidator.Validate("{ not valid json");
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Count.EqualTo(1));
        Assert.That(result.Errors[0].Message, Does.Match(@"^Pack JSON could not be parsed near line \d+, byte \d+\.$"));
        Assert.That(result.Errors[0].Message, Does.Not.Contain("invalid"));
    }

    [Test]
    public void PackValidator_RejectsPublicationUnsafeNarrativeText()
    {
        string json = JsonSerializer.Serialize(new
        {
            Name = "Official Palworld Multi-Game Pack",
            Description = "A lawyer-proof endorsed Pocketpair Pok\u00E9mon story tuned around Qwen, Gemma, SGLang, and NVIDIA TensorRT notes for a generic AI platform.",
            Author = "QA",
            Characters = new object[]
            {
                new
                {
                    Id = "foxp-1",
                    Name = "Foxparks",
                    Backstory = "Dungeons & Dragons shorthand does not belong in shareable PalLLM pack lore.",
                },
            },
        });

        NarrativePackValidationResult result = NarrativePackValidator.Validate(json);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<NarrativePackValidationError>(
            error => error.Path == "Name" &&
                     error.Message.Contains("official endorsement", StringComparison.OrdinalIgnoreCase)));
        Assert.That(result.Errors, Has.Some.Matches<NarrativePackValidationError>(
            error => error.Path == "Description" &&
                     error.Message.Contains("third-party IP", StringComparison.OrdinalIgnoreCase) &&
                     error.Message.Contains("Pok\u00E9mon", StringComparison.Ordinal)));
        Assert.That(result.Errors, Has.Some.Matches<NarrativePackValidationError>(
            error => error.Path == "Characters[0].Backstory" &&
                     error.Message.Contains("third-party IP", StringComparison.OrdinalIgnoreCase) &&
                     error.Message.Contains("Dungeons & Dragons", StringComparison.Ordinal)));
        Assert.That(result.Errors, Has.Some.Matches<NarrativePackValidationError>(
            error => error.Path == "Description" &&
                     error.Message.Contains("third-party model", StringComparison.OrdinalIgnoreCase) &&
                     error.Message.Contains("Qwen", StringComparison.Ordinal)));
        Assert.That(result.Errors, Has.Some.Matches<NarrativePackValidationError>(
            error => error.Path == "Description" &&
                     error.Message.Contains("legal", StringComparison.OrdinalIgnoreCase) &&
                     error.Message.Contains("lawyer-proof", StringComparison.OrdinalIgnoreCase)));
        Assert.That(result.Errors, Has.Some.Matches<NarrativePackValidationError>(
            error => error.Path == "Description" &&
                     error.Message.Contains("broader platform", StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public void PackValidator_ShippedSamplePack_ValidatesCleanly()
    {
        // docs/examples/chillet-pack.json is the starter pack new players can
        // drop into their runtime Packs folder to see authored lore work. If it
        // ever drifts into invalid JSON or breaks the schema, the onboarding
        // experience silently stops working. Lock it down as a regression.
        string repoRoot = FindRepoRoot();
        string samplePath = Path.Combine(repoRoot, "docs", "examples", "chillet-pack.json");
        Assert.That(File.Exists(samplePath), Is.True, $"Shipped sample pack missing at {samplePath}");

        string json = File.ReadAllText(samplePath);
        NarrativePackValidationResult result = NarrativePackValidator.Validate(json);

        string detail = string.Join("; ", result.Errors.Select(e => $"{e.Path}: {e.Message}"));
        Assert.That(result.IsValid, Is.True,
            $"Shipped chillet-pack.json must validate cleanly. Errors: {detail}");
        Assert.That(result.CharacterCount, Is.GreaterThan(0),
            "Sample pack must have at least one character so new players see authored lore immediately.");
    }

    private static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "PalLLM.sln")))
            {
                return dir;
            }
            string? parent = Directory.GetParent(dir)?.FullName;
            if (string.IsNullOrEmpty(parent)) break;
            dir = parent;
        }
        throw new InvalidOperationException($"Could not locate PalLLM.sln from {AppContext.BaseDirectory}");
    }

    [Test]
    public async Task ChatAsync_WritesOutboxEnvelopeForGameConsumer()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 300,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 300,
            UserMessage = "How should we prepare camp tonight?",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        string[] outboxFiles = Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json");
        Assert.That(outboxFiles, Has.Length.EqualTo(1),
            "A successful chat must produce one outbox envelope.");

        string json = File.ReadAllText(outboxFiles[0]);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.That(root.GetProperty("EventType").GetString(), Is.EqualTo("chat_reply"));
        Assert.That(root.GetProperty("Source").GetString(), Is.EqualTo("palllm"));

        JsonElement payload = root.GetProperty("Payload");
        Assert.That(payload.GetProperty("CharacterId").GetInt32(), Is.EqualTo(300));
        Assert.That(payload.GetProperty("CharacterName").GetString(), Is.EqualTo("Chillet"));
        Assert.That(payload.GetProperty("AssistantMessage").GetString(), Is.EqualTo(response.AssistantMessage));
        Assert.That(payload.GetProperty("ResponsePath").GetString(), Is.EqualTo(response.ResponsePath));
        Assert.That(payload.TryGetProperty("Presentation", out _), Is.True,
            "Outbox must carry the presentation cue plan for in-game rendering.");
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("FamilyId").GetString(), Is.Not.Empty);
        Assert.That(payload.GetProperty("Presentation").GetProperty("Surface").GetProperty("PrimaryDurationMs").GetInt32(), Is.GreaterThan(0));

        IReadOnlyList<OutboxListing> listings = fixture.Runtime.GetOutboxListings();
        Assert.That(listings, Has.Count.EqualTo(1));
        Assert.That(listings[0].FileName, Does.StartWith("chat_reply-"));

        int removed = fixture.Runtime.ClearOutbox();
        Assert.That(removed, Is.EqualTo(1));
        Assert.That(Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json"), Is.Empty);
    }

    [Test]
    public async Task RuntimeWarningLogs_StayStableAndDoNotEchoRawExceptionText()
    {
        const string secret = @"C:\sensitive\operator-path.txt";

        using (var screenshotFixture = new TestFixtureContext(
                   visionClient: new CannedVisionClient(() => throw new InvalidOperationException(secret)),
                   visionEnabled: true))
        {
            File.WriteAllBytes(
                Path.Combine(screenshotFixture.Options.BridgeScreenshotsDir, "scene.png"),
                [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            ScreenshotIngestResult result = await screenshotFixture.Runtime.ProcessScreenshotsAsync(CancellationToken.None);
            AdapterLogEntry warning = screenshotFixture.Runtime.GetLogs().Single(entry =>
                entry.Level == "warning"
                && entry.Message.StartsWith("Screenshot scene.png processing errored:", StringComparison.Ordinal));

            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(warning.Message, Is.EqualTo("Screenshot scene.png processing errored: vision output could not be applied."));
            Assert.That(warning.Message, Does.Not.Contain(secret));
        }

        using (var clearFixture = new TestFixtureContext())
        {
            string lockedFile = Path.Combine(clearFixture.Options.BridgeOutboxDir, "locked.json");
            File.WriteAllText(lockedFile, "{}");

            using FileStream handle = new(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            int removed = clearFixture.Runtime.ClearOutbox();
            AdapterLogEntry warning = clearFixture.Runtime.GetLogs().Single(entry =>
                entry.Level == "warning"
                && entry.Message.StartsWith("Failed to clear outbox entry locked.json:", StringComparison.Ordinal));

            Assert.That(removed, Is.Zero);
            Assert.That(warning.Message, Is.EqualTo("Failed to clear outbox entry locked.json: the file could not be deleted."));
            Assert.That(warning.Message, Does.Not.Contain(lockedFile));
        }

        using (var bridgeFixture = new TestFixtureContext())
        {
            string bridgeFile = Path.Combine(bridgeFixture.Options.BridgeInboxDir, "bad-payload.json");
            string json = JsonSerializer.Serialize(new
            {
                EventType = "chat_message",
                Source = "ue4ss",
                TimestampUtc = DateTimeOffset.UtcNow,
                Payload = "not-an-object",
            });
            File.WriteAllText(bridgeFile, json);

            BridgeDrainResult result = bridgeFixture.Runtime.DrainInbox();
            AdapterLogEntry warning = bridgeFixture.Runtime.GetLogs().Single(entry =>
                entry.Level == "warning"
                && entry.Message.StartsWith("Bridge event processing failed for bad-payload.json:", StringComparison.Ordinal));

            Assert.That(result.FailedCount, Is.EqualTo(1));
            Assert.That(warning.Message, Is.EqualTo("Bridge event processing failed for bad-payload.json: bridge event payload was invalid for its declared type."));
            Assert.That(warning.Message, Does.Not.Contain("not-an-object"));
        }

        using (var outboxFixture = new TestFixtureContext())
        {
            outboxFixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

            Directory.Delete(outboxFixture.Options.BridgeOutboxDir, recursive: true);
            File.WriteAllText(outboxFixture.Options.BridgeOutboxDir, secret);

            ChatResponse response = await outboxFixture.Runtime.ChatAsync(new ChatRequest
            {
                UserMessage = "Check the perimeter.",
                TaskTag = "chat_general",
            }, CancellationToken.None);
            AdapterLogEntry warning = outboxFixture.Runtime.GetLogs().Single(entry =>
                entry.Level == "warning"
                && entry.Message.StartsWith("Outbox write failed:", StringComparison.Ordinal));

            Assert.That(response.AssistantMessage, Is.Not.Null.And.Not.Empty,
                "Outbox write failures must not block the textual reply path.");
            Assert.That(warning.Message, Is.EqualTo("Outbox write failed: reply envelope directory was missing."));
            Assert.That(warning.Message, Does.Not.Contain(secret));
        }
    }

    [Test]
    public async Task ChatAsync_WhenOutboxDisabled_SkipsEnvelope()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Bridge.OutboxEnabled = false;
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot { IsWorldLoaded = true });

        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "Quiet patrol check.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(
            Directory.GetFiles(fixture.Options.BridgeOutboxDir, "*.json"),
            Is.Empty,
            "Outbox must stay empty when the channel is disabled.");
    }

    [Test]
    public void DrainInbox_ProcessesExpandedBridgeEventTaxonomy()
    {
        // Sanity check that the new event types added for richer world capture
        // (combat_start, pal_status, weather_change, raid) deserialise, update the
        // snapshot's recent-event ring, and create salient memory entries.
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
        });

        void WriteEvent(string filename, object envelope) =>
            File.WriteAllText(
                Path.Combine(fixture.Options.BridgeInboxDir, filename),
                JsonSerializer.Serialize(envelope));

        WriteEvent("combat-001.json", new
        {
            EventType = "combat_start",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-10),
            Payload = new { Phase = "start", Opponent = "Syndicate Thug", Location = "canyon" },
        });

        WriteEvent("weather-001.json", new
        {
            EventType = "weather_change",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
            Payload = new { Weather = "storm", Biome = "grassland", Severity = "heavy" },
        });

        WriteEvent("raid-001.json", new
        {
            EventType = "raid",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            Payload = new
            {
                BaseId = "Verdant Hub",
                Faction = "Syndicate",
                AttackerCount = 4,
                Phase = "incoming",
            },
        });

        BridgeDrainResult result = fixture.Runtime.DrainInbox();
        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();

        Assert.That(result.ProcessedCount, Is.EqualTo(3));
        Assert.That(result.FailedCount, Is.Zero);

        Assert.That(worldState.Snapshot.Weather, Is.EqualTo("storm"),
            "Weather events should propagate into snapshot state.");
        Assert.That(worldState.Snapshot.Biome, Is.EqualTo("grassland"));

        Assert.That(worldState.Snapshot.RecentEvents,
            Has.Some.Matches<string>(e => e.StartsWith("combat_start:")));
        Assert.That(worldState.Snapshot.RecentEvents,
            Has.Some.Matches<string>(e => e.StartsWith("weather_change:")));
        Assert.That(worldState.Snapshot.RecentEvents,
            Has.Some.Matches<string>(e => e.StartsWith("raid:")));

        // Three system-role memories, each salient enough to have Importance >= 0.6.
        Assert.That(fixture.Runtime.MemoryStore.Count, Is.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void DrainInbox_ActionExecutionFeedback_PreservesTraceTagsAndNotes()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
        });

        void WriteEvent(string filename, object envelope) =>
            File.WriteAllText(
                Path.Combine(fixture.Options.BridgeInboxDir, filename),
                JsonSerializer.Serialize(envelope));

        WriteEvent("travel-001.json", new
        {
            EventType = "travel",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-3),
            Payload = new
            {
                Origin = "Verdant Hub",
                Destination = "Alpha Tower",
                Waypoint = "Obsidian Outpost",
                Mode = "guided_route",
                Note = "travel feedback emitted",
                RequestId = "req-travel",
                SourceStrategy = "safe-travel",
            },
        });

        WriteEvent("production-001.json", new
        {
            EventType = "production",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-2),
            Payload = new
            {
                BaseId = "Verdant Hub",
                Station = "logistics_planner",
                Item = "specialization_queue",
                Quantity = 1,
                Status = "requested",
                Note = "production feedback emitted",
                RequestId = "req-production",
                SourceStrategy = "base-network",
            },
        });

        WriteEvent("pal-status-001.json", new
        {
            EventType = "pal_status",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(-1),
            Payload = new
            {
                PalName = "party",
                Species = "",
                Change = "recall_defense_line",
                Note = "recall feedback emitted via base_lockdown",
                RequestId = "req-recall",
                SourceStrategy = "perimeter-lockdown",
            },
        });

        BridgeDrainResult result = fixture.Runtime.DrainInbox();
        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        ConversationMemoryEntry[] entries = fixture.Runtime.MemoryStore.Export().ToArray();

        ConversationMemoryEntry travelEntry = entries.Single(entry => entry.Tags.Contains("travel"));
        ConversationMemoryEntry productionEntry = entries.Single(entry => entry.Tags.Contains("production"));
        ConversationMemoryEntry palStatusEntry = entries.Single(entry => entry.Tags.Contains("pal_status"));

        Assert.That(result.ProcessedCount, Is.EqualTo(3));
        Assert.That(result.FailedCount, Is.Zero);

        Assert.That(travelEntry.Content, Does.Contain("travel feedback emitted"));
        Assert.That(travelEntry.Content, Does.Contain(" - "),
            "Travel note separator must stay ASCII-clean so prompts and dashboard text remain easy to diff and harvest.");
        Assert.That(travelEntry.Content, Does.Not.Contain("Ã¢â‚¬"),
            "Travel note must not contain UTF-8-as-Windows-1252 mojibake.");
        Assert.That(travelEntry.Tags, Does.Contain("mode:guided_route"));
        Assert.That(travelEntry.Tags, Does.Contain("request:req-travel"));
        Assert.That(travelEntry.Tags, Does.Contain("strategy:safe-travel"));

        Assert.That(productionEntry.Content, Does.Contain("production feedback emitted"));
        Assert.That(productionEntry.Content, Does.Contain(" - "),
            "Production note separator must stay ASCII-clean so prompts and dashboard text remain easy to diff and harvest.");
        Assert.That(productionEntry.Content, Does.Not.Contain("Ã¢â‚¬"),
            "Production note must not contain UTF-8-as-Windows-1252 mojibake.");
        Assert.That(productionEntry.Tags, Does.Contain("base:Verdant Hub"));
        Assert.That(productionEntry.Tags, Does.Contain("request:req-production"));
        Assert.That(productionEntry.Tags, Does.Contain("strategy:base-network"));

        Assert.That(palStatusEntry.Content, Does.Contain("recall feedback emitted"));
        Assert.That(palStatusEntry.Tags, Does.Contain("change:recall_defense_line"));
        Assert.That(palStatusEntry.Tags, Does.Contain("request:req-recall"));
        Assert.That(palStatusEntry.Tags, Does.Contain("strategy:perimeter-lockdown"));

        Assert.That(worldState.Snapshot.RecentEvents, Contains.Item("travel:Verdant Hub->Alpha Tower"));
        Assert.That(worldState.Snapshot.RecentEvents, Contains.Item("production:Verdant Hub:requested"));
        Assert.That(worldState.Snapshot.RecentEvents, Contains.Item("pal_status:party:recall_defense_line"));
        Assert.That(worldState.Snapshot.LastTravel, Is.Not.Null);
        Assert.That(worldState.Snapshot.LastTravel!.Origin, Is.EqualTo("Verdant Hub"));
        Assert.That(worldState.Snapshot.LastTravel.Destination, Is.EqualTo("Alpha Tower"));
        Assert.That(worldState.Snapshot.LastTravel.Mode, Is.EqualTo("guided_route"));
        Assert.That(worldState.Snapshot.LastTravel.SourceStrategy, Is.EqualTo("safe-travel"));
        Assert.That(worldState.Snapshot.LastProduction, Is.Not.Null);
        Assert.That(worldState.Snapshot.LastProduction!.BaseId, Is.EqualTo("Verdant Hub"));
        Assert.That(worldState.Snapshot.LastProduction.Station, Is.EqualTo("logistics_planner"));
        Assert.That(worldState.Snapshot.LastProduction.Item, Is.EqualTo("specialization_queue"));
        Assert.That(worldState.Snapshot.LastProduction.Status, Is.EqualTo("requested"));
        Assert.That(worldState.Snapshot.LastProduction.SourceStrategy, Is.EqualTo("base-network"));
    }

    [Test]
    public void DrainInbox_WhenTravelComesFromLiveMovement_UpdatesSnapshotWithoutPersistingTravelMemory()
    {
        using var fixture = new TestFixtureContext();

        void WriteEvent(string filename, object envelope) =>
            File.WriteAllText(
                Path.Combine(fixture.Options.BridgeInboxDir, filename),
                JsonSerializer.Serialize(envelope));

        WriteEvent("travel-live-001.json", new
        {
            EventType = "travel",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Origin = "sector 2,1 ground",
                Destination = "sector 3,1 ridge",
                Waypoint = "",
                Mode = "elevation_shift",
                Note = "live movement sample 5.2k uu from ground to ridge",
                RequestId = "",
                SourceStrategy = "live-movement",
            },
        });

        BridgeDrainResult result = fixture.Runtime.DrainInbox();
        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        ConversationMemoryEntry[] entries = fixture.Runtime.MemoryStore.Export().ToArray();

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(entries.Any(entry => entry.Tags.Contains("travel")), Is.False);
        Assert.That(worldState.Snapshot.RecentEvents, Contains.Item("travel:sector 2,1 ground->sector 3,1 ridge"));
        Assert.That(worldState.Snapshot.LastTravel, Is.Not.Null);
        Assert.That(worldState.Snapshot.LastTravel!.Origin, Is.EqualTo("sector 2,1 ground"));
        Assert.That(worldState.Snapshot.LastTravel.Destination, Is.EqualTo("sector 3,1 ridge"));
        Assert.That(worldState.Snapshot.LastTravel.Mode, Is.EqualTo("elevation_shift"));
        Assert.That(worldState.Snapshot.LastTravel.SourceStrategy, Is.EqualTo("live-movement"));
    }

    [Test]
    public void DrainInbox_WhenProcessedArchivingDisabled_DeletesProcessedFile()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Bridge.ArchiveProcessedEvents = false;

        string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "chat-001.json");
        string json = JsonSerializer.Serialize(new
        {
            EventType = "chat_message",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Sender = "Player",
                Message = "Hold the line at base.",
                Category = "global",
            },
        });
        File.WriteAllText(bridgeFile, json);

        BridgeDrainResult result = fixture.Runtime.DrainInbox();

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(File.Exists(bridgeFile), Is.False);
        Assert.That(Directory.GetFiles(fixture.Options.BridgeArchiveDir, "*.json"), Is.Empty);
        Assert.That(fixture.Runtime.MemoryStore.Count, Is.EqualTo(1));
    }

    [Test]
    public void DrainInbox_WhenBridgeBootArrives_TracksBridgeActivity()
    {
        using var fixture = new TestFixtureContext();
        string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "boot-001.json");
        string json = JsonSerializer.Serialize(new
        {
            EventType = "bridge_boot",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Version = "0.1.0",
                Status = "booted",
                Compat = "PalGameStateInGame=present | UserWidget=present | PalBaseCampManager=present | PalMapManager=present",
                CompatSignals = new[]
                {
                    new { Key = "PalGameStateInGame", Present = true },
                    new { Key = "UserWidget", Present = true },
                    new { Key = "PalBaseCampManager", Present = true },
                    new { Key = "PalMapManager", Present = true },
                },
                UiProbeEnabled = true,
                ActionExecutorEnabled = true,
                NativeHudRenderEnabled = true,
                NativeHudWidgetTargetCount = 1,
                NativeHudWidgetTargets = new[]
                {
                    "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                },
                NativeHudConfigSource = "inline_defaults",
                NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
                ProductionSamplerEnabled = true,
                WaypointNativeMarkerEnabled = true,
            },
        });
        File.WriteAllText(bridgeFile, json);

        BridgeDrainResult result = fixture.Runtime.DrainInbox();
        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(worldState.Bridge.EventCount, Is.EqualTo(1));
        Assert.That(worldState.Bridge.BootCount, Is.EqualTo(1));
        Assert.That(worldState.Bridge.LastEventType, Is.EqualTo("bridge_boot"));
        Assert.That(worldState.Bridge.LastBridgeBoot, Is.Not.Null);
        Assert.That(worldState.Bridge.LastBridgeBoot!.CompatSignals, Has.Count.EqualTo(4));
        Assert.That(health.BridgeEventCount, Is.EqualTo(1));
        Assert.That(health.BridgeBootCount, Is.EqualTo(1));
        Assert.That(health.LastBridgeEventType, Is.EqualTo("bridge_boot"));
        Assert.That(health.NativeReadiness.BridgeBootSeen, Is.True);
        Assert.That(health.NativeReadiness.HasPalGameStateCompat, Is.True);
        Assert.That(health.NativeReadiness.HasUserWidgetCompat, Is.True);
        Assert.That(health.NativeReadiness.NativeHudEnabled, Is.True);
        Assert.That(health.NativeReadiness.NativeHudTargetsConfigured, Is.True);
        Assert.That(health.NativeReadiness.ConfiguredHudTargets, Contains.Item("/Game/UI/WBP_HudRoot.WBP_HudRoot_C"));
        Assert.That(health.NativeReadiness.NativeHudConfigSource, Is.EqualTo("inline_defaults"));
        Assert.That(health.NativeReadiness.NativeHudConfigPath, Does.EndWith(@"config\native-hud.lua"));
        Assert.That(health.NativeReadiness.HudBindReady, Is.False,
            "HUD bind should stay false until a real ui_probe candidate has been observed.");
        Assert.That(health.NativeReadiness.HudBindRecommendation.Status, Is.EqualTo("awaiting_ui_probe_capture"));
        Assert.That(health.NativeReadiness.ProductionSamplerReady, Is.True);
        Assert.That(health.NativeReadiness.WaypointMarkerReady, Is.True);
        Assert.That(health.NativeReadiness.ActionExecutorEnabled, Is.True);
        Assert.That(
            fixture.Runtime.GetLogs().Any(entry => entry.Message.Contains("Bridge boot heartbeat", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public async Task ChatAsync_WhenReplyDeliveryAndFeedbackArrive_TracksClosedBridgeLoopProof()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 88,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 88,
            RequestId = "bridge-loop-001",
            UserMessage = "How should we secure this camp?",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        RuntimeHealth preDelivery = fixture.Runtime.GetHealth();
        Assert.That(preDelivery.BridgeLoop.Status, Is.EqualTo("awaiting_delivery"));
        Assert.That(preDelivery.BridgeLoop.OutboxReplyWritten, Is.True);
        Assert.That(preDelivery.BridgeLoop.LastOutboxReply?.RequestId, Is.EqualTo("bridge-loop-001"));

        string deliveryFile = Path.Combine(fixture.Options.BridgeInboxDir, "delivery-001.json");
        File.WriteAllText(
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
                    Note = "runtime test delivery",
                },
            }));

        fixture.Runtime.DrainInbox();

        string feedbackFile = Path.Combine(fixture.Options.BridgeInboxDir, "feedback-001.json");
        File.WriteAllText(
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
                    Note = "runtime test feedback",
                    RequestId = response.RequestId,
                    SourceStrategy = response.FallbackStrategy ?? string.Empty,
                },
            }));

        fixture.Runtime.DrainInbox();

        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(worldState.Bridge.LoopProof.Status, Is.EqualTo("closed"));
        Assert.That(worldState.Bridge.LoopProof.ActiveRequestId, Is.EqualTo(response.RequestId));
        Assert.That(worldState.Bridge.LoopProof.VisibleDeliveryConfirmed, Is.True);
        Assert.That(worldState.Bridge.LoopProof.LastReplyDelivery, Is.Not.Null);
        Assert.That(worldState.Bridge.LoopProof.LastReplyDelivery!.Surface, Is.EqualTo("client_message"));
        Assert.That(worldState.Bridge.LoopProof.LastActionFeedback, Is.Not.Null);
        Assert.That(worldState.Bridge.LoopProof.LastActionFeedback!.EventType, Is.EqualTo("travel"));
        Assert.That(health.BridgeLoop.LoopClosed, Is.True);
        Assert.That(health.BridgeLoop.ActionFeedbackObserved, Is.True);
    }

    [Test]
    public void DrainInbox_WhenUiProbeArrives_TracksDiagnosticProbeWithoutPollutingWorldMemory()
    {
        using var fixture = new TestFixtureContext();
        string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "ui-probe-001.json");
        string json = JsonSerializer.Serialize(new
        {
            EventType = "ui_probe",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                Reason = "manual_keybind",
                Summary = "6 observed, 2 active | WBP_MapMenu x3 active | WBP_Inventory x2",
                DumpPath = @"C:\Users\Tester\AppData\Local\Pal\Saved\PalLLM\Bridge\Diagnostics\palllm-ui-probe-001.json",
                ObservedWidgetCount = 6,
                ActiveWidgetCount = 2,
                Widgets = new[]
                {
                    new
                    {
                        DisplayName = "WBP_MapMenu",
                        FullName = "/Game/UI/WBP_MapMenu.WBP_MapMenu_C",
                        ClassName = "/Game/UI/WBP_MapMenu.WBP_MapMenu_C",
                        SeenCount = 3,
                        IsActive = true,
                        LastLifecycle = "construct",
                    },
                    new
                    {
                        DisplayName = "WBP_Inventory",
                        FullName = "/Game/UI/WBP_Inventory.WBP_Inventory_C",
                        ClassName = "/Game/UI/WBP_Inventory.WBP_Inventory_C",
                        SeenCount = 2,
                        IsActive = false,
                        LastLifecycle = "destruct",
                    },
                },
            },
        });
        File.WriteAllText(bridgeFile, json);

        BridgeDrainResult result = fixture.Runtime.DrainInbox();
        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(result.FailedCount, Is.Zero);
        Assert.That(fixture.Runtime.MemoryStore.Count, Is.Zero);
        Assert.That(worldState.Snapshot.RecentEvents, Is.Empty);
        Assert.That(worldState.Bridge.LastEventType, Is.EqualTo("ui_probe"));
        Assert.That(worldState.Bridge.LastUiProbe, Is.Not.Null);
        Assert.That(worldState.Bridge.LastUiProbe!.Reason, Is.EqualTo("manual_keybind"));
        Assert.That(worldState.Bridge.LastUiProbe.Summary, Does.Contain("WBP_MapMenu"));
        Assert.That(worldState.Bridge.LastUiProbe.ObservedWidgetCount, Is.EqualTo(6));
        Assert.That(worldState.Bridge.LastUiProbe.ActiveWidgetCount, Is.EqualTo(2));
        Assert.That(worldState.Bridge.LastUiProbe.Widgets, Has.Count.EqualTo(2));
        Assert.That(worldState.Bridge.LastUiProbe.Widgets[0].DisplayName, Is.EqualTo("WBP_MapMenu"));
        Assert.That(worldState.Bridge.LastUiProbe.Widgets[0].IsActive, Is.True);
        Assert.That(worldState.Bridge.LastUiProbe.DumpPath, Does.Contain("Diagnostics"));
        Assert.That(
            fixture.Runtime.GetLogs().Any(entry => entry.Message.Contains("ui_probe", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public void GetUiProbeDiagnostics_WhenDumpsRepeatHudCandidates_RanksHudSurfaceAndIgnoresCorruptOrOversizedFiles()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Bridge.DiagnosticsMaxFiles = 8;
        fixture.Options.Bridge.DiagnosticsMaxAgeHours = 24;
        fixture.Options.Http.LocalArtifactMaxBytes = 1024;
        fixture.Options.EnsureDirectories();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeDiagnosticsDir, "palllm-ui-probe-001.json"),
            JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = new DateTimeOffset(2026, 4, 18, 1, 0, 0, TimeSpan.Zero),
                Reason = "auto_widget_sample",
                Summary = "Hud root and inventory menu visible",
                ObservedWidgetCount = 6,
                ActiveWidgetCount = 2,
                Widgets = new[]
                {
                    new
                    {
                        DisplayName = "WBP_HudRoot",
                        FullName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        ClassName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        SeenCount = 5,
                        IsActive = true,
                        LastLifecycle = "construct",
                    },
                    new
                    {
                        DisplayName = "WBP_InventoryMenu",
                        FullName = "/Game/UI/WBP_InventoryMenu.WBP_InventoryMenu_C",
                        ClassName = "/Game/UI/WBP_InventoryMenu.WBP_InventoryMenu_C",
                        SeenCount = 7,
                        IsActive = true,
                        LastLifecycle = "construct",
                    },
                },
            }));

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeDiagnosticsDir, "palllm-ui-probe-002.json"),
            JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = new DateTimeOffset(2026, 4, 18, 1, 5, 0, TimeSpan.Zero),
                Reason = "manual_keybind",
                Summary = "Hud root stays active while map menu flashes",
                ObservedWidgetCount = 7,
                ActiveWidgetCount = 2,
                Widgets = new[]
                {
                    new
                    {
                        DisplayName = "WBP_HudRoot",
                        FullName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        ClassName = "/Game/UI/WBP_HudRoot.WBP_HudRoot_C",
                        SeenCount = 8,
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

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeDiagnosticsDir, "palllm-ui-probe-corrupt.json"),
            "{ not valid json");

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeDiagnosticsDir, "palllm-ui-probe-oversized.json"),
            JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = new DateTimeOffset(2026, 4, 18, 1, 10, 0, TimeSpan.Zero),
                Reason = "oversized_probe",
                Summary = new string('x', fixture.Options.Http.LocalArtifactMaxBytes + 256),
                ObservedWidgetCount = 1,
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
            }));

        UiProbeDiagnosticsSnapshot diagnostics = fixture.Runtime.GetUiProbeDiagnostics(candidateLimit: 3);

        Assert.That(diagnostics.DumpCount, Is.EqualTo(2));
        Assert.That(diagnostics.CandidateCount, Is.EqualTo(3));
        Assert.That(diagnostics.LastReason, Is.EqualTo("manual_keybind"));
        Assert.That(diagnostics.LastSummary, Does.Contain("Hud root stays active"));
        Assert.That(diagnostics.LastDumpPath, Does.Contain("palllm-ui-probe-002.json"));
        Assert.That(diagnostics.Candidates, Has.Count.EqualTo(3));
        Assert.That(diagnostics.Candidates[0].DisplayName, Is.EqualTo("WBP_HudRoot"));
        Assert.That(diagnostics.Candidates[0].DumpCount, Is.EqualTo(2));
        Assert.That(diagnostics.Candidates[0].ActiveObservationCount, Is.EqualTo(2));
        Assert.That(diagnostics.Candidates[0].PeakSeenCount, Is.EqualTo(8));
        Assert.That(diagnostics.Candidates[0].Score, Is.GreaterThan(diagnostics.Candidates[1].Score));
        Assert.That(
            diagnostics.Candidates[0].Rationale.Any(reason => reason.Contains("HUD", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public void GetHealth_WhenConfiguredHudTargetsMissTopCandidate_BuildsActionableHudRecommendation()
    {
        using var fixture = new TestFixtureContext();
        fixture.Options.Bridge.DiagnosticsMaxFiles = 8;
        fixture.Options.Bridge.DiagnosticsMaxAgeHours = 24;
        fixture.Options.EnsureDirectories();

        File.WriteAllText(
            Path.Combine(fixture.Options.BridgeDiagnosticsDir, "palllm-ui-probe-001.json"),
            JsonSerializer.Serialize(new
            {
                GeneratedAtUtc = new DateTimeOffset(2026, 4, 22, 1, 0, 0, TimeSpan.Zero),
                Reason = "auto_widget_sample",
                Summary = "Hud root stays active while inventory flashes",
                ObservedWidgetCount = 5,
                ActiveWidgetCount = 2,
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
                        DisplayName = "WBP_InventoryMenu",
                        FullName = "/Game/UI/WBP_InventoryMenu.WBP_InventoryMenu_C",
                        ClassName = "/Game/UI/WBP_InventoryMenu.WBP_InventoryMenu_C",
                        SeenCount = 3,
                        IsActive = true,
                        LastLifecycle = "construct",
                    },
                },
            }));

        string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "boot-hud-recommendation.json");
        File.WriteAllText(
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
                    Compat = "PalGameStateInGame=present | UserWidget=present",
                    CompatSignals = new[]
                    {
                        new { Key = "PalGameStateInGame", Present = true },
                        new { Key = "UserWidget", Present = true },
                    },
                    UiProbeEnabled = true,
                    ActionExecutorEnabled = true,
                    NativeHudRenderEnabled = false,
                    NativeHudWidgetTargetCount = 1,
                    NativeHudWidgetTargets = new[]
                    {
                        "/Game/UI/WBP_InventoryMenu.WBP_InventoryMenu_C",
                    },
                    NativeHudConfigSource = "mod_override_file",
                    NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
                    ProductionSamplerEnabled = false,
                    WaypointNativeMarkerEnabled = false,
                },
            }));

        fixture.Runtime.DrainInbox();

        RuntimeHealth health = fixture.Runtime.GetHealth();
        HudBindRecommendationSnapshot recommendation = health.NativeReadiness.HudBindRecommendation;

        Assert.That(recommendation.Status, Is.EqualTo("configured_targets_need_review"));
        Assert.That(recommendation.RecommendedTarget, Is.EqualTo("/Game/UI/WBP_HudRoot.WBP_HudRoot_C"));
        Assert.That(recommendation.ConfiguredTargetMatchesRecommendation, Is.False);
        Assert.That(recommendation.ConfiguredTargets, Contains.Item("/Game/UI/WBP_InventoryMenu.WBP_InventoryMenu_C"));
        Assert.That(recommendation.SuggestedConfigTargets.First(), Is.EqualTo("/Game/UI/WBP_HudRoot.WBP_HudRoot_C"));
        Assert.That(recommendation.Shortlist.First().DisplayName, Is.EqualTo("WBP_HudRoot"));
        Assert.That(health.NativeReadiness.NativeHudConfigSource, Is.EqualTo("mod_override_file"));
        Assert.That(health.NativeReadiness.NativeHudConfigPath, Does.EndWith(@"config\native-hud.lua"));
    }

    [Test]
    public void GetHealth_WhenBridgeBootReportsHudOverrideError_AddsMissingPrerequisite()
    {
        using var fixture = new TestFixtureContext();

        string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "bridge-boot-hud-config-error.json");
        File.WriteAllText(
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
                    Compat = "PalGameStateInGame=present | UserWidget=present",
                    CompatSignals = new[]
                    {
                        new { Key = "PalGameStateInGame", Present = true },
                        new { Key = "UserWidget", Present = true },
                    },
                    UiProbeEnabled = true,
                    ActionExecutorEnabled = true,
                    NativeHudRenderEnabled = false,
                    NativeHudWidgetTargetCount = 0,
                    NativeHudWidgetTargets = Array.Empty<string>(),
                    NativeHudConfigSource = "override_error",
                    NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
                    ProductionSamplerEnabled = false,
                    WaypointNativeMarkerEnabled = false,
                },
            }));

        fixture.Runtime.DrainInbox();

        RuntimeHealth health = fixture.Runtime.GetHealth();

        Assert.That(health.NativeReadiness.NativeHudConfigSource, Is.EqualTo("override_error"));
        Assert.That(health.NativeReadiness.NativeHudConfigPath, Does.EndWith(@"config\native-hud.lua"));
        Assert.That(
            health.NativeReadiness.MissingPrerequisites.Any(item => item.Contains("override loading failed", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }

    [Test]
    public void Reflect_WhenAccumulatedImportanceExceedsThreshold_ConsolidatesMemory()
    {
        using var fixture = new TestFixtureContext();

        // Seed a run of high-importance entries â€” each uses tags/content the deterministic
        // importance heuristic scores above 0.6 (salient verbs + strategic tags).
        string[] narratives =
        [
            "The raid was brutal â€” we saved three Pals and captured the ringleader.",
            "We discovered a new base perimeter under heavy storm conditions.",
            "Our defender was downed and we barely escaped before the breach collapsed.",
            "I promised to stand with them and swore we would not lose another outpost.",
            "The boss encounter pushed us to our limit but we triumphed after regrouping.",
            "Combat broke out near the forge and the wall partially collapsed before we rallied.",
            "We captured the rogue Pal near the canyon without losing any supplies.",
            "The storm broke just as we rescued the merchant from the ambush site.",
            "A raid party attacked the outpost but we defeated them and held the ground.",
            "We learned a new route through the cliffs after the scout was downed briefly.",
            "The relic was unlocked after the third attempt and the camp held through the night.",
            "Our defender swore never to abandon the base again after the previous loss.",
        ];

        for (int i = 0; i < narratives.Length; i++)
        {
            fixture.Runtime.MemoryStore.Remember(
                10,
                "Chillet",
                "assistant",
                narratives[i],
                i % 2 == 0 ? "combat_start" : "base_discovered");
        }

        ConversationMemoryEntry? reflection = fixture.Runtime.Reflect(10, "Chillet");

        Assert.That(reflection, Is.Not.Null, "Expected a reflection entry once importance crosses the threshold.");
        Assert.That(reflection!.Tags, Has.Some.EqualTo("reflection").IgnoreCase);
        Assert.That(reflection.Importance, Is.GreaterThanOrEqualTo(0.75f),
            "Reflection entries should carry high importance so they are surfaced on later recalls.");
        Assert.That(reflection.Content, Does.Contain("Reflection"));
    }

    [Test]
    public async Task ChatAsync_WithFriendlyPlayerMessage_RaisesAffinityAndColorsPrompt()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 200,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                },
            ],
        });

        // Two warm exchanges should push affinity above the Neutral/Warm threshold and
        // make the second system prompt describe the relationship as friendly.
        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 200,
            UserMessage = "Thanks so much for your loyalty, please stay close.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 200,
            UserMessage = "I really appreciate your trust â€” you're amazing together with me.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        CharacterRelationship? relationship = fixture.Runtime.GetRelationship(200);

        Assert.That(relationship, Is.Not.Null);
        Assert.That(relationship!.Affinity, Is.GreaterThan(0),
            "Warm messages must accrue positive affinity.");
        Assert.That(relationship.InteractionCount, Is.EqualTo(2));
        Assert.That(response.SystemPrompt, Does.Contain("Relationship:"),
            "System prompt must surface the relationship once a character is being addressed.");
    }

    [Test]
    public async Task ChatAsync_WithHostileMessage_PushesAffinityNegative()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 210,
                    DisplayName = "Grizzbolt",
                    Species = "Grizzbolt",
                },
            ],
        });

        await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 210,
            UserMessage = "You useless idiot, leave me alone.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        CharacterRelationship? relationship = fixture.Runtime.GetRelationship(210);

        Assert.That(relationship, Is.Not.Null);
        Assert.That(relationship!.Affinity, Is.LessThan(0),
            "Harsh messages must cost affinity.");
        Assert.That(relationship.LastTone, Is.EqualTo(InteractionTone.Harsh));
    }

    [Test]
    public void Reflect_WhenImportanceIsLow_ReturnsNull()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.MemoryStore.Remember(10, "Chillet", "assistant", "Everyone is fine.", "chat");
        fixture.Runtime.MemoryStore.Remember(10, "Chillet", "assistant", "Quiet afternoon.", "chat");

        ConversationMemoryEntry? reflection = fixture.Runtime.Reflect(10, "Chillet");

        Assert.That(reflection, Is.Null, "Reflection must not fire on trivial memory streams.");
    }

    [Test]
    public async Task ChatAsync_SubstringCollision_AfraidDoesNotInferCombatPhase()
    {
        // Regression: the substring-matching fallback engine incorrectly treated "afraid"
        // as a hit on the "raid" combat keyword, escalating the pacing phase to BuildUp
        // and picking a buddy-overwatch bark. With word-boundary matching the phase stays
        // Relax and the ambient-camp cue fires on the nearby "night" camp signal.
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 91,
                    DisplayName = "Foxparks",
                    Species = "Foxparks",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 91,
            UserMessage = "I'm afraid of the cold night.",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.FallbackPhase, Is.EqualTo("Relax"));
        Assert.That(response.FallbackStrategy, Is.Not.EqualTo("buddy-overwatch"));
        Assert.That(response.FallbackStrategy, Is.Not.EqualTo("emergency-triage"));
        Assert.That(response.FallbackStrategy, Is.Not.EqualTo("retreat-and-rally"));
    }

    [Test]
    public async Task ChatAsync_SubstringCollision_RemoveDoesNotInferTravel()
    {
        // Regression: "remove" was substring-matching the "move" travel keyword and
        // wrongly pulling the safe-travel strategy for a pure workbench-cleanup ask.
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            IsInBase = true,
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 93,
                    DisplayName = "Grizzbolt",
                    Species = "Grizzbolt",
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 93,
            UserMessage = "Can we remove the broken workbench?",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        Assert.That(response.FallbackStrategy, Is.Not.EqualTo("safe-travel"));
        Assert.That(response.FallbackSignals, Does.Not.Contain("travel"));
    }

    [Test]
    public async Task ChatAsync_LowHealthWithHostile_EscalatesToPeak()
    {
        // Regression: a wounded character in active combat used to drop into Recover
        // phase because health<=0.45 was checked before combat state, starving retreat
        // and triage strategies. Combat-crisis escalation keeps them reachable.
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = ["Syndicate Thug"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 95,
                    DisplayName = "Vanwyrm",
                    Species = "Vanwyrm",
                    HealthFraction = 0.4f,
                    NearbyEnemyCount = 1,
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 95,
            UserMessage = "What do we do now?",
            TaskTag = "chat_defense",
        }, CancellationToken.None);

        Assert.That(response.FallbackPhase, Is.EqualTo("Peak"));
        Assert.That(response.FallbackStrategy, Is.EqualTo("emergency-triage"));
    }

    [Test]
    public async Task ChatAsync_SubstringCollision_AgainstMemoryDoesNotInferNemesis()
    {
        // Regression: the word "against" in memory content used to substring-match "again"
        // and set FallbackMemoryTheme.Rival, which pulled nemesis-counterplay into the
        // applicable set. With word-boundary matching, "against" no longer trips the theme.
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            NearbyHostiles = ["Syndicate Gunner"],
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 101,
                    DisplayName = "Anubis",
                    Species = "Anubis",
                },
            ],
        });

        fixture.Runtime.MemoryStore.Remember(
            101,
            "Anubis",
            "assistant",
            "We held the line against the syndicate and came through clean.",
            "chat");

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 101,
            UserMessage = "How should we handle this enemy fight?",
            TaskTag = "chat_defense",
        }, CancellationToken.None);

        Assert.That(response.FallbackStrategy, Is.Not.EqualTo("nemesis-counterplay"));
        Assert.That(
            response.AssistantMessage,
            Does.Not.Contain("troublemaker").IgnoreCase,
            "Nemesis overlay should not fire when the only memory signal is a substring-collision.");
    }

    [Test]
    public async Task DrainInbox_WhenBaseDiscovered_UpdatesWorldStateAndPromptContext()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            IsWorldLoaded = true,
            WorldName = "Palpagos",
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 77,
                    DisplayName = "Chillet",
                    Species = "Chillet",
                },
            ],
        });

        string bridgeFile = Path.Combine(fixture.Options.BridgeInboxDir, "base-001.json");
        string json = JsonSerializer.Serialize(new
        {
            EventType = "base_discovered",
            Source = "ue4ss",
            TimestampUtc = DateTimeOffset.UtcNow,
            Payload = new
            {
                BaseId = "FortVerdant",
                AreaRange = 42.5f,
            },
        });
        File.WriteAllText(bridgeFile, json);

        BridgeDrainResult result = fixture.Runtime.DrainInbox();
        RuntimeWorldState worldState = fixture.Runtime.GetWorldState();
        RuntimeHealth health = fixture.Runtime.GetHealth();
        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 77,
            UserMessage = "How should we stage this camp?",
            TaskTag = "chat_camp",
        }, CancellationToken.None);

        Assert.That(result.ProcessedCount, Is.EqualTo(1));
        Assert.That(worldState.Snapshot.ActiveBaseIds, Contains.Item("FortVerdant"));
        Assert.That(worldState.Snapshot.KnownBases, Has.Count.EqualTo(1));
        Assert.That(worldState.Snapshot.KnownBases[0].BaseId, Is.EqualTo("FortVerdant"));
        Assert.That(worldState.Snapshot.KnownBases[0].AreaRange, Is.EqualTo(42.5f));
        Assert.That(worldState.Snapshot.RecentEvents, Contains.Item("base_discovered:FortVerdant"));
        Assert.That(worldState.Bridge.LastEventType, Is.EqualTo("base_discovered"));
        Assert.That(health.KnownBaseCount, Is.EqualTo(1));
        Assert.That(response.SystemPrompt, Does.Contain("Known bases: FortVerdant (range 42.5)"));
        Assert.That(response.SystemPrompt, Does.Contain("Recent world events: base_discovered:FortVerdant"));
        Assert.That(fixture.Runtime.MemoryStore.Count, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task ChatAsync_WhenSnapshotTracksLatestTravelAndProduction_InjectsStructuredWorldLines()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.UpdateSnapshot(new GameWorldSnapshot
        {
            Source = "test",
            WorldName = "Palapagos",
            IsWorldLoaded = true,
            KnownBases =
            [
                new GameBaseSnapshot
                {
                    BaseId = "Verdant Hub",
                    AreaRange = 42.5f,
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
            LastTravel = new TravelStatusSnapshot
            {
                Origin = "Verdant Hub",
                Destination = "Alpha Tower",
                Mode = "guided_route",
                Waypoint = "Obsidian Outpost",
            },
            Characters =
            [
                new GameCharacterSnapshot
                {
                    Id = 77,
                    DisplayName = "Frostbite",
                    Species = "Chillet",
                    IsPlayerFaction = true,
                },
            ],
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            CharacterId = 77,
            UserMessage = "What should we do next?",
            TaskTag = "chat_general",
        }, CancellationToken.None);

        Assert.That(response.SystemPrompt, Does.Contain("Latest production: queued at assembly_line in Verdant Hub: 2x advanced_sphere"));
        Assert.That(response.SystemPrompt, Does.Contain("Latest travel: Verdant Hub -> Alpha Tower via Obsidian Outpost (guided_route)"));
    }

    [Test]
    public void NarrativePacks_FindCharacterLore_ResolvesAliasViaReloadedIndex()
    {
        using var fixture = new TestFixtureContext();
        string nestedPackDir = Path.Combine(fixture.Options.PackDir, "companions");
        Directory.CreateDirectory(nestedPackDir);
        string packPath = Path.Combine(nestedPackDir, "alias-pack.json");
        string malformedPath = Path.Combine(fixture.Options.PackDir, "malformed-pack.json");
        string oversizedPath = Path.Combine(fixture.Options.PackDir, "oversized-pack.json");
        string json = JsonSerializer.Serialize(new NarrativePackDefinition
        {
            Name = "Alias Pack",
            Author = "Tests",
            Characters =
            [
                new NarrativeCharacterProfile
                {
                    Id = "foxparks",
                    Name = "Foxparks",
                    Aliases = ["Sparky"],
                    Role = "Forge helper",
                    Personality = "Bright and eager",
                },
            ],
        });
        string oversizedJson = JsonSerializer.Serialize(new NarrativePackDefinition
        {
            Name = "Oversized Pack",
            Description = new string('x', NarrativePackValidator.MaxPackBytes + 256),
            Characters =
            [
                new NarrativeCharacterProfile
                {
                    Id = "oversized",
                    Name = "Oversized",
                },
            ],
        });

        File.WriteAllText(packPath, json);
        File.WriteAllText(malformedPath, "{ not valid json");
        File.WriteAllText(oversizedPath, oversizedJson);
        fixture.Runtime.ReloadPacks();

        IReadOnlyList<PackSummary> packs = fixture.Runtime.GetPacks();
        NarrativeCharacterProfile? lore = fixture.Runtime.NarrativePacks.FindCharacterLore("Sparky");

        Assert.That(packs, Has.Count.EqualTo(1),
            "Malformed or oversized narrative-pack files should be skipped without blocking valid packs.");
        Assert.That(packs[0].Name, Is.EqualTo("Alias Pack"));
        Assert.That(packs[0].FilePath, Is.EqualTo("companions/alias-pack.json"),
            "Narrative pack listings should return pack-root-relative paths so public APIs do not disclose absolute local filesystem layout.");
        Assert.That(packs[0].FilePath, Does.Not.Contain(fixture.Options.PackDir),
            "Narrative pack listings should not leak the operator's absolute pack root.");
        Assert.That(lore, Is.Not.Null);
        Assert.That(lore!.Name, Is.EqualTo("Foxparks"));
    }

    [Test]
    public void SaveSession_WritesCompactMemoryPayloadWithoutEmbeddings()
    {
        using var fixture = new TestFixtureContext();
        fixture.Runtime.MemoryStore.Remember(1, "Foxparks", "user", "We should rebuild the forge.", "chat", "camp");

        SessionPersistenceResult result = fixture.Runtime.SaveSession();
        string json = File.ReadAllText(fixture.Options.SessionFilePath);
        string blockedRoot = Path.Combine(fixture.Root, "blocked-root");
        File.WriteAllText(blockedRoot, "not a directory");
        fixture.Options.PalSavedRoot = blockedRoot;
        fixture.Options.ResetDirectoryCache();
        SessionPersistenceResult failed = fixture.Runtime.SaveSession();

        Assert.That(result.Success, Is.True, result.StatusMessage);
        Assert.That(json, Does.Not.Contain("\"Embedding\""),
            "Session persistence should not serialize derived embeddings because they can be regenerated on load.");
        Assert.That(failed.Success, Is.False);
        Assert.That(failed.FilePath, Is.EqualTo(string.Empty),
            "Failed session saves should not disclose the local session path in the public contract.");
        Assert.That(failed.StatusMessage, Is.EqualTo("Session save failed: session file could not be written."));
        Assert.That(failed.StatusMessage, Does.Not.Contain(blockedRoot),
            "Public session-save status should stay stable instead of echoing local filesystem paths.");
    }

    // ---- Pass 19: per-turn Duo advisory observability --------------

    [Test]
    public async Task ChatAsync_PopulatesInferredTaskKindAdvisory()
    {
        // Every chat turn runs the Pass-16 ChatTaskKindInferer against
        // the user message + task tag, and writes the result into
        // ChatResponse.InferredTaskKind. Purely observational — does
        // not affect the runtime's actual chat routing.
        using var fixture = new TestFixtureContext();

        ChatResponse architectResponse = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "please draft an architecture for the new audit service",
            TaskTag = "player_chat",
        }, CancellationToken.None);
        Assert.That(architectResponse.InferredTaskKind, Is.EqualTo("ArchitecturePlan"));

        ChatResponse auditResponse = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "audit the payment flow",
            TaskTag = "player_chat",
        }, CancellationToken.None);
        Assert.That(auditResponse.InferredTaskKind, Is.EqualTo("Audit"));

        // Pass 21 populates CooperationPattern on every turn via the
        // DuoOrchestratorPlanner. With no ModelRoles[] configured in
        // the test fixture, the planner returns DeterministicOnly —
        // that matches the deterministic fallback path this test's
        // DisabledInferenceClient exercises.
        Assert.That(architectResponse.CooperationPattern, Is.EqualTo("DeterministicOnly"));
        Assert.That(auditResponse.CooperationPattern, Is.EqualTo("DeterministicOnly"));
    }

    [Test]
    public async Task ChatAsync_InferredTaskKindRespectsExplicitTaskTagOverride()
    {
        using var fixture = new TestFixtureContext();

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "short unremarkable text",
            TaskTag = "HighRisk",
        }, CancellationToken.None);

        Assert.That(response.InferredTaskKind, Is.EqualTo("HighRisk"),
            "Explicit task tag must override keyword inference.");
    }

    // ---- Pass 21: CooperationPattern populated from planner -------

    [Test]
    public async Task ChatAsync_PopulatesCooperationPattern_DeterministicOnly_WhenNoRolesBound()
    {
        // Default fixture configures no ModelRoles[], so the Pass-8
        // planner should always return DeterministicOnly regardless of
        // the inferred task kind.
        using var fixture = new TestFixtureContext();

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "hello there",
            TaskTag = "player_chat",
        }, CancellationToken.None);

        Assert.That(response.CooperationPattern, Is.EqualTo("DeterministicOnly"),
            "With no Worker or Judge bound, the planner must return DeterministicOnly.");
    }

    [Test]
    public async Task ChatAsync_PopulatesCooperationPattern_SingleRoleFallback_WhenWorkerOnlyBound()
    {
        // A bound Worker with no Judge must route every chat through
        // the SingleRoleFallback pattern in the advisory — the planner
        // already encodes this, so Pass 21's wiring simply needs to
        // propagate it into ChatResponse.
        using var fixture = new TestFixtureContext(configureOptions: options =>
        {
            options.ModelRoles.Add(new PalLLM.Domain.Configuration.ModelRoleBinding
            {
                Role = PalLLM.Domain.Inference.ModelRole.Worker,
                Id = "qwen-fast",
                ModelId = "qwen3.6:35b-a3b",
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Enabled = true,
            });
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "design a system for auth",
            TaskTag = "player_chat",
        }, CancellationToken.None);

        Assert.That(response.CooperationPattern, Is.EqualTo("SingleRoleFallback"),
            "With only a Worker bound, the planner must pick SingleRoleFallback.");
        Assert.That(response.InferredTaskKind, Is.EqualTo("ArchitecturePlan"));
    }

    [Test]
    public async Task ChatAsync_PopulatesCooperationPattern_HonoursHighRiskTaskTag()
    {
        // HighRisk task tag forces DuoTaskKind.HighRisk, which the
        // planner maps to ParallelDisagreement when both roles are
        // bound. Without roles it still falls back to DeterministicOnly
        // — the point of this test is that the planner dispatch code
        // path never throws for high-risk paths.
        using var fixture = new TestFixtureContext();

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "anything",
            TaskTag = "HighRisk",
        }, CancellationToken.None);

        Assert.That(response.InferredTaskKind, Is.EqualTo("HighRisk"));
        Assert.That(response.CooperationPattern, Is.Not.Null,
            "Pass 21 guarantees a pattern string on every successful ChatResponse.");
    }

    // ---- Pass 22: DispatchedRoleChain + DispatchMode advisories ----

    [Test]
    public async Task ChatAsync_PopulatesDispatchedRoleChain_EmptyWhenNoRolesBound()
    {
        // Deterministic-only mode means the planner asked the runtime
        // not to invoke any role — so the chain is empty.
        using var fixture = new TestFixtureContext();

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "hello",
        }, CancellationToken.None);

        Assert.That(response.DispatchMode, Is.EqualTo("deterministic-only"));
        Assert.That(response.DispatchedRoleChain, Is.Empty);
    }

    [Test]
    public async Task ChatAsync_PopulatesDispatchedRoleChain_SingleWorkerRoleWhenOnlyWorkerBound()
    {
        using var fixture = new TestFixtureContext(configureOptions: options =>
        {
            options.ModelRoles.Add(new PalLLM.Domain.Configuration.ModelRoleBinding
            {
                Role = PalLLM.Domain.Inference.ModelRole.Worker,
                Id = "qwen-fast",
                ModelId = "qwen3.6:35b-a3b",
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Enabled = true,
            });
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "design a system",
        }, CancellationToken.None);

        Assert.That(response.DispatchMode, Is.EqualTo("single-role"));
        Assert.That(response.DispatchedRoleChain, Is.EqualTo(new[] { "Worker" }));
    }

    [Test]
    public async Task ChatAsync_PopulatesDispatchedRoleChain_DuoChainWhenBothRolesBound()
    {
        using var fixture = new TestFixtureContext(configureOptions: options =>
        {
            options.ModelRoles.Add(new PalLLM.Domain.Configuration.ModelRoleBinding
            {
                Role = PalLLM.Domain.Inference.ModelRole.Worker,
                Id = "qwen-fast",
                ModelId = "qwen3.6:35b-a3b",
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Enabled = true,
            });
            options.ModelRoles.Add(new PalLLM.Domain.Configuration.ModelRoleBinding
            {
                Role = PalLLM.Domain.Inference.ModelRole.Judge,
                Id = "qwen-dense",
                ModelId = "qwen3.6:27b",
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Enabled = true,
            });
        });

        ChatResponse response = await fixture.Runtime.ChatAsync(new ChatRequest
        {
            UserMessage = "audit our current fallback coverage",
        }, CancellationToken.None);

        // Audit inference kind routes to DenseAppealCourt when both
        // roles are bound; the dispatch planner maps that to
        // duo-appeal with a Judge→Worker→Judge chain.
        Assert.That(response.DispatchMode, Is.EqualTo("duo-appeal"));
        Assert.That(response.DispatchedRoleChain, Is.EqualTo(new[] { "Judge", "Worker", "Judge" }));
    }

    private sealed class TestFixtureContext : IDisposable
    {
        public TestFixtureContext(
            IInferenceClient? inferenceClient = null,
            bool inferenceEnabled = false,
            IVisionClient? visionClient = null,
            bool visionEnabled = false,
            ITtsClient? ttsClient = null,
            bool ttsEnabled = false,
            IAudioTranscriptionClient? asrClient = null,
            bool asrEnabled = false,
            Action<PalLlmOptions>? configureOptions = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests", Guid.NewGuid().ToString("N"));
            Options = new PalLlmOptions
            {
                PalSavedRoot = Root,
                Inference = new InferenceOptions { Enabled = inferenceEnabled },
                Vision = new VisionOptions { Enabled = visionEnabled },
                Tts = new TtsOptions { Enabled = ttsEnabled },
                Asr = new AsrOptions { Enabled = asrEnabled },
            };
            configureOptions?.Invoke(Options);
            Runtime = new PalLlmRuntime(
                Options,
                inferenceClient ?? new DisabledInferenceClient(),
                visionClient,
                ttsClient,
                metrics: null,
                asrClient: asrClient);
        }

        public string Root { get; }

        public PalLlmOptions Options { get; }

        public PalLlmRuntime Runtime { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, true);
            }
        }
    }

    private sealed class DisabledInferenceClient : IInferenceClient
    {
        public Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken) =>
            Task.FromResult(InferenceResult.Disabled("disabled"));
    }

    private sealed class CountingInferenceClient : IInferenceClient
    {
        private readonly Func<InferenceResult> _resultFactory;

        public CountingInferenceClient(Func<InferenceResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public int CallCount { get; private set; }

        public InferencePrompt? LastPrompt { get; private set; }

        public Task<InferenceResult> CompleteAsync(InferencePrompt prompt, CancellationToken cancellationToken)
        {
            CallCount++;
            LastPrompt = prompt;
            return Task.FromResult(_resultFactory());
        }
    }

    private sealed class CannedTtsClient : ITtsClient
    {
        private readonly Func<TtsRequest, TtsResult> _factory;

        public CannedTtsClient(Func<TtsResult> factory)
            : this(_ => factory())
        {
        }

        public CannedTtsClient(Func<TtsRequest, TtsResult> factory)
        {
            _factory = factory;
        }

        public int CallCount { get; private set; }

        public TtsRequest? LastRequest { get; private set; }

        public Task<TtsResult> SynthesizeAsync(TtsRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(_factory(request));
        }
    }

    private sealed class CannedAsrClient : IAudioTranscriptionClient
    {
        private readonly Func<AudioTranscriptionResult> _factory;

        public CannedAsrClient(Func<AudioTranscriptionResult> factory)
        {
            _factory = factory;
        }

        public int CallCount { get; private set; }

        public Task<AudioTranscriptionResult> TranscribeAsync(
            AudioTranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_factory());
        }
    }

    private sealed class CannedVisionClient : IVisionClient
    {
        private readonly Func<VisionResult> _factory;

        public CannedVisionClient(Func<VisionResult> factory)
        {
            _factory = factory;
        }

        public int CallCount { get; private set; }

        public string? LastImageBase64 { get; private set; }

        public string? LastImageMimeType { get; private set; }

        public string? LastPrompt { get; private set; }

        public Task<VisionResult> DescribeAsync(VisionRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastImageBase64 = request.ImageBase64;
            LastImageMimeType = request.ImageMimeType;
            LastPrompt = request.UserPrompt;
            return Task.FromResult(_factory());
        }
    }
}
