using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

public sealed class ModelTierTests
{
    // ---------------------------------------------------------------------
    // HttpModelAvailabilityProbe
    // ---------------------------------------------------------------------

    [Test]
    public async Task HttpProbe_WhenOpenAiV1ModelsReturnsList_ExtractsIds()
    {
        const string openAiBody = "{\"object\":\"list\",\"data\":[{\"id\":\"gemma3:4b\",\"object\":\"model\"},{\"id\":\"qwen3:14b\",\"object\":\"model\"}]}";
        using var handler = new ScriptedProbeHandler(
            new[]
            {
                new ScriptedProbeHandler.Route("/v1/models", HttpStatusCode.OK, openAiBody, "application/json"),
            });
        using var httpClient = new HttpClient(handler);

        var probe = new HttpModelAvailabilityProbe(httpClient, BuildOptionsWithBaseUrl("http://127.0.0.1:11434/v1/"));
        IReadOnlySet<string> models = await probe.GetAvailableModelsAsync(CancellationToken.None);

        Assert.That(models, Is.EquivalentTo(new[] { "gemma3:4b", "qwen3:14b" }));
    }

    // Pass 346: HttpProbe_WhenOpenAiEndpointMissing_FallsBackToOllamaTags
    // deleted alongside the Ollama back-compat path. The /api/tags
    // probe candidate was removed; every supported runtime
    // (llama-server, vLLM, SGLang, LM Studio, OpenVINO) exposes
    // /v1/models so the OpenAI-compatible branch is sufficient.

    [Test]
    public async Task HttpProbe_WhenBothEndpointsReturnData_MergesResults()
    {
        // PalLLM supported deployments expose /v1/models (OpenAI-compat,
        // covers llama-server, vLLM, SGLang, LM Studio, OpenVINO) and
        // /openai/models (Foundry Local cached-model catalog). The
        // probe should merge so a model only reachable via one route
        // still counts as available.
        //
        // Pass 346: the /api/tags Ollama-native route was removed from
        // the probe chain; this test no longer registers or asserts on it.
        const string openAiBody = "{\"data\":[{\"id\":\"gemma-4-E4B-it\"}]}";
        const string foundryBody = "[\"phi-4-mini-instruct-generic-cpu\"]";
        using var handler = new ScriptedProbeHandler(
            new[]
            {
                new ScriptedProbeHandler.Route("/v1/models", HttpStatusCode.OK, openAiBody, "application/json"),
                new ScriptedProbeHandler.Route("/openai/models", HttpStatusCode.OK, foundryBody, "application/json"),
            });
        using var httpClient = new HttpClient(handler);

        var probe = new HttpModelAvailabilityProbe(httpClient, BuildOptionsWithBaseUrl("http://127.0.0.1:8080/v1/"));
        IReadOnlySet<string> models = await probe.GetAvailableModelsAsync(CancellationToken.None);

        Assert.That(models, Is.EquivalentTo(new[] { "gemma-4-E4B-it", "phi-4-mini-instruct-generic-cpu" }));
    }

    [Test]
    public async Task HttpProbe_WhenEndpointUnreachable_ReturnsEmptySetWithoutBubblingException()
    {
        using var handler = new ThrowingProbeHandler();
        using var httpClient = new HttpClient(handler);

        var probe = new HttpModelAvailabilityProbe(httpClient, BuildOptionsWithBaseUrl("http://127.0.0.1:11434/v1/"));
        IReadOnlySet<string> models = await probe.GetAvailableModelsAsync(CancellationToken.None);

        Assert.That(models, Is.Empty,
            "Probe must degrade quietly on network failure so the orchestrator can keep the current tier.");
    }

    [Test]
    public async Task HttpProbe_WhenBaseUrlEmpty_ReturnsEmptySetWithoutNetworkCall()
    {
        using var handler = new ScriptedProbeHandler(Array.Empty<ScriptedProbeHandler.Route>());
        using var httpClient = new HttpClient(handler);

        var probe = new HttpModelAvailabilityProbe(httpClient, BuildOptionsWithBaseUrl(string.Empty));
        IReadOnlySet<string> models = await probe.GetAvailableModelsAsync(CancellationToken.None);

        Assert.That(models, Is.Empty);
        Assert.That(handler.CallCount, Is.EqualTo(0),
            "Empty BaseUrl must short-circuit before any HTTP attempt.");
    }

    [Test]
    public async Task HttpProbe_WhenDeclaredCatalogExceedsCap_ReturnsEmptyWithoutReadingBody()
    {
        var trackingStream = new TrackingReadStream(Encoding.UTF8.GetBytes("{\"data\":[]}"));
        var content = new StreamContent(trackingStream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Headers.ContentLength = 2_048;

        using var handler = new ScriptedProbeHandler(
            new[]
            {
                new ScriptedProbeHandler.Route("/v1/models", HttpStatusCode.OK, content),
            });
        using var httpClient = new HttpClient(handler);
        PalLlmOptions options = BuildOptionsWithBaseUrl("http://127.0.0.1:11434/v1/");
        options.Inference.ModelCatalogMaxResponseBytes = 1_024;

        var probe = new HttpModelAvailabilityProbe(httpClient, options);
        IReadOnlySet<string> models = await probe.GetAvailableModelsAsync(CancellationToken.None);

        Assert.That(models, Is.Empty);
        Assert.That(trackingStream.ReadStarted, Is.False,
            "Declared oversized model catalogs should fail from headers alone without opening the response body.");
    }

    [Test]
    public async Task HttpProbe_WhenStreamingCatalogCrossesCap_ReturnsEmptySet()
    {
        byte[] oversizedBody = Encoding.UTF8.GetBytes("{\"data\":[{\"id\":\"" + new string('x', 2_048) + "\"}]}");
        var trackingStream = new TrackingReadStream(oversizedBody);
        var content = new UnknownLengthReadContent(trackingStream, "application/json");

        using var handler = new ScriptedProbeHandler(
            new[]
            {
                new ScriptedProbeHandler.Route("/v1/models", HttpStatusCode.OK, content),
            });
        using var httpClient = new HttpClient(handler);
        PalLlmOptions options = BuildOptionsWithBaseUrl("http://127.0.0.1:11434/v1/");
        options.Inference.ModelCatalogMaxResponseBytes = 1_024;

        var probe = new HttpModelAvailabilityProbe(httpClient, options);
        IReadOnlySet<string> models = await probe.GetAvailableModelsAsync(CancellationToken.None);

        Assert.That(models, Is.Empty);
        Assert.That(trackingStream.ReadStarted, Is.True,
            "When Content-Length is absent, the probe should stream the catalog and stop once the configured cap is crossed.");
    }

    // ---------------------------------------------------------------------
    // ModelTierOrchestrator
    // ---------------------------------------------------------------------

    [Test]
    public async Task Orchestrator_WhenNoTiersConfigured_AlwaysReturnsStaticModelAndRefreshIsNoOp()
    {
        var options = new PalLlmOptions();
        options.Inference.Model = "qwen3.6:35b-a3b";

        var orchestrator = new ModelTierOrchestrator(options, new NullModelAvailabilityProbe());

        Assert.That(orchestrator.GetActiveModel(), Is.EqualTo("qwen3.6:35b-a3b"));
        Assert.That(orchestrator.GetActiveTierId(), Is.Null);

        ModelTierRefreshResult result = await orchestrator.RefreshAsync(CancellationToken.None);
        Assert.That(result.ActiveModel, Is.EqualTo("qwen3.6:35b-a3b"));
        Assert.That(result.ActiveTierId, Is.Null);
        Assert.That(result.Changed, Is.False);
    }

    [Test]
    public void Orchestrator_WhenTiersConfigured_SeedsWithLowestPriorityTier()
    {
        // Before any probe runs, the sidecar still needs to answer chat
        // requests. Seed with the lowest-priority tier (typically the
        // "small" fast-start one) so the first request works immediately.
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "small", Model = "gemma3:4b", Priority = 1 },
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
        ];

        var orchestrator = new ModelTierOrchestrator(options, new NullModelAvailabilityProbe());

        Assert.That(orchestrator.GetActiveTierId(), Is.EqualTo("small"));
        Assert.That(orchestrator.GetActiveModel(), Is.EqualTo("gemma3:4b"));
    }

    [Test]
    public async Task Orchestrator_WhenProbeReportsOnlySmallAvailable_KeepsSmallTier()
    {
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "small", Model = "gemma3:4b", Priority = 1 },
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
        ];
        var probe = new StubProbe("gemma3:4b");

        var orchestrator = new ModelTierOrchestrator(options, probe);
        ModelTierRefreshResult result = await orchestrator.RefreshAsync(CancellationToken.None);

        Assert.That(result.ActiveTierId, Is.EqualTo("small"));
        Assert.That(result.ActiveModel, Is.EqualTo("gemma3:4b"));
        Assert.That(result.Changed, Is.False,
            "Seed was already the small tier, so no transition.");
    }

    [Test]
    public async Task Orchestrator_WhenProbeReportsLargeAvailable_GraduatesAndReportsChange()
    {
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "small", Model = "gemma3:4b", Priority = 1 },
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
        ];
        var probe = new StubProbe("gemma3:4b", "qwen3.6:35b-a3b");

        var orchestrator = new ModelTierOrchestrator(options, probe);
        ModelTierRefreshResult result = await orchestrator.RefreshAsync(CancellationToken.None);

        Assert.That(result.ActiveTierId, Is.EqualTo("large"),
            "Higher-priority tier must win when its model is available.");
        Assert.That(result.ActiveModel, Is.EqualTo("qwen3.6:35b-a3b"));
        Assert.That(result.PreviousTierId, Is.EqualTo("small"));
        Assert.That(result.Changed, Is.True);
        Assert.That(orchestrator.GetLastSeenAvailableModels(), Is.EquivalentTo(new[] { "gemma3:4b", "qwen3.6:35b-a3b" }));
    }

    [Test]
    public async Task Orchestrator_WhenProbeBecomesEmpty_KeepsCurrentTierInsteadOfThrashing()
    {
        // A transient probe failure (network blip, endpoint restart) must
        // NOT knock us back to the seed tier. Holding the previously-
        // graduated tier is safer than bouncing the whole sidecar down a
        // tier every time the endpoint hiccups.
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "small", Model = "gemma3:4b", Priority = 1 },
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
        ];
        var probe = new StubProbe("gemma3:4b", "qwen3.6:35b-a3b");

        var orchestrator = new ModelTierOrchestrator(options, probe);
        await orchestrator.RefreshAsync(CancellationToken.None);
        Assert.That(orchestrator.GetActiveTierId(), Is.EqualTo("large"));

        probe.SetAvailable(); // probe returns empty
        ModelTierRefreshResult second = await orchestrator.RefreshAsync(CancellationToken.None);

        Assert.That(second.ActiveTierId, Is.EqualTo("large"),
            "Empty probe result keeps the active tier — transient probe failures must not demote.");
        Assert.That(second.Changed, Is.False);
    }

    [Test]
    public async Task Orchestrator_WithCustomUnstoredStaticModel_PreservesModelWhenNoTierAvailable()
    {
        var options = new PalLlmOptions();
        options.Inference.Model = "custom-static";
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
        ];
        var probe = new StubProbe(); // nothing available

        var orchestrator = new ModelTierOrchestrator(options, probe);
        ModelTierRefreshResult result = await orchestrator.RefreshAsync(CancellationToken.None);

        Assert.That(result.ActiveTierId, Is.EqualTo("large"),
            "Seed still picks the only configured tier so the first request works.");
        Assert.That(result.ActiveModel, Is.EqualTo("qwen3.6:35b-a3b"));
    }

    [Test]
    public async Task Orchestrator_WithHigherPriorityTierBelowInList_StillPicksHigherPriority()
    {
        // Order in the list shouldn't matter — only Priority does.
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
            new ModelTierOptions { Id = "small", Model = "gemma3:4b", Priority = 1 },
        ];
        var probe = new StubProbe("gemma3:4b", "qwen3.6:35b-a3b");

        var orchestrator = new ModelTierOrchestrator(options, probe);
        ModelTierRefreshResult result = await orchestrator.RefreshAsync(CancellationToken.None);

        Assert.That(result.ActiveTierId, Is.EqualTo("large"));
    }

    // ---------------------------------------------------------------------
    // Orchestrator ↔ HttpJsonInferenceClient integration
    // ---------------------------------------------------------------------

    [Test]
    public void CollaborationPlanner_WhenQwenDenseAndMoeConfigured_AssignsWorkerAndJudgeRoles()
    {
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "worker", Model = "unsloth/Qwen3.6-35B-A3B-GGUF", Priority = 10 },
            new ModelTierOptions { Id = "judge", Model = "unsloth/Qwen3.6-27B-GGUF", Priority = 9 },
            new ModelTierOptions { Id = "omni", Model = "Qwen/Qwen3-Omni-30B-A3B-Instruct", Priority = 3 },
            new ModelTierOptions { Id = "edge3n", Model = "google/gemma-3n-E4B-it", Priority = 2 },
            new ModelTierOptions { Id = "edge", Model = "gemma4:e4b", Priority = 2 },
            new ModelTierOptions { Id = "gemma-dense", Model = "gemma4:31b", Priority = 2 },
            new ModelTierOptions { Id = "text", Model = "qwen-coder-text", Priority = 1 },
            new ModelTierOptions { Id = "portable", Model = "local-text-worker-GGUF", Priority = 0 },
        ];

        var orchestrator = new ModelTierOrchestrator(options, new StubProbe(
            "unsloth/Qwen3.6-35B-A3B-GGUF",
            "unsloth/Qwen3.6-27B-GGUF",
            "Qwen/Qwen3-Omni-30B-A3B-Instruct",
            "google/gemma-3n-E4B-it",
            "gemma4:e4b",
            "gemma4:31b",
            "qwen-coder-text",
            "local-text-worker-GGUF"));
        var planner = new ModelCollaborationPlanner(options, orchestrator);

        ModelCollaborationSnapshot snapshot = planner.GetSnapshot(new ModelHardwareHints(
            VramGb: 48,
            RamGb: 128,
            PreferParallel: true));

        Assert.That(snapshot.Hardware.ClassId, Is.EqualTo("workstation"));
        Assert.That(snapshot.Hardware.CanKeepTwoSpecialistsWarm, Is.True);

        ModelCollaborationModelDescriptor fastModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Contains("35B-A3B", StringComparison.Ordinal));
        ModelCollaborationModelDescriptor deliberateModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Contains("27B", StringComparison.Ordinal));

        Assert.That(fastModel.OperatingStyle, Is.EqualTo("fast-iterative"));
        Assert.That(deliberateModel.OperatingStyle, Is.EqualTo("deliberate"));
        Assert.That(fastModel.Authority.MayExecuteLowRiskToolLoops, Is.True);
        Assert.That(fastModel.Authority.MayRecommendMerge, Is.False);
        Assert.That(deliberateModel.Authority.MayBePrimaryReviewer, Is.True);
        Assert.That(deliberateModel.Authority.MayDraftHighRiskToolPlans, Is.True);
        Assert.That(fastModel.Capability.Family, Is.EqualTo("qwen3.6"));
        Assert.That(fastModel.Capability.InputModalities, Is.SupersetOf(new[] { "text", "image", "video" }));
        Assert.That(fastModel.Capability.OutputModalities, Is.EqualTo(new[] { "text" }));
        Assert.That(fastModel.Capability.SupportsAudioInput, Is.False);
        Assert.That(fastModel.Capability.SupportsStructuredOutputs, Is.True);
        Assert.That(fastModel.Capability.ServingOptimizations, Has.Some.Contains("stable media UUIDs"));
        Assert.That(fastModel.Capability.ServingProfile.ProfileId, Is.EqualTo("gguf-libmtmd-multimodal"));
        Assert.That(fastModel.Capability.ServingProfile.RequestProtocol, Does.Contain("/v1/chat/completions"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-chunked-prefill"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("vLLM artifact provenance lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--performance-mode interactivity"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--prefix-caching-hash-algo sha256_cbor"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Responses API proof lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--kv-cache-metrics-sample"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--kv-cache-dtype fp8"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--max-num-batched-tokens"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--max-long-partial-prefills"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("disaggregated prefill/decode proof lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("MoRIIOConnector"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--generation-config vllm"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-sleep-mode"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-lora"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--fully-sharded-loras"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Mooncake Store proof lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("KV-event proof lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("PegaFlow"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("kv_connector_module_path"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("FlexKVConnectorV1"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-dbo"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--dbo-decode-token-threshold"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("SGLang alternative lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("SGLang attention-backend proof lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("fp4_e2m1"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("EAGLE-3/adaptive speculation"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("SGLang HiCache proof lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-deterministic-inference"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-metrics"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("request dump/replay"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--grammar-backend xgrammar"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("SGLang Model Gateway lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--reasoning-parser qwen3"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("qwen3_next_mtp"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("num_speculative_tokens\":1"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--no-enable-prefix-caching"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--language-model-only"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--spec-type draft-mtp"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("MTP/multimodal split-lane guard"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("hybrid-GDN proof lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("context-identity lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--mmproj <matching-mmproj.gguf>"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("LMCacheECConnector"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("remote-media safety lane"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-mm-embeds"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--disable-chunked-mm-input"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-tower-connector-lora"));
        Assert.That(fastModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--limit-mm-per-prompt.video 1"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("model-card license metadata"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("stable OpenAI-compatible media uuid"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("image_embeds"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("LoRA adapter"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("model-native speculation"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("MTP-1 with prefix caching disabled"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("llama.cpp draft-MTP proof"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("text-only MTP proof"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("text-only MTP endpoint"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("primary-source capability receipt"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("262K default context"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("1,010,000-token extension"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("thinking-preservation"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("short companion turns still win the queue"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("operation name and latency budget"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("/v1/responses"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("schema digest"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("grammar/backend id"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("PresencePenalty"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("FrequencyPenalty"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("TopK"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("MinP"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("parallel_tool_calls=false"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("StopSequences"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("TopLogprobs"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("InferencePrompt.UserContent"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("On SGLang"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("request-id propagation"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("auto-tuned topk"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("external KV cache daemons"));
        Assert.That(fastModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("Dual Batch Overlap"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("staged artifact store"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("canonical schema digest"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("disaggregated prefill"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("encoder cache"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("embedding-only lane"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("sha256_cbor"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("performance-mode interactivity"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("KV-cache compression"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("KV-block residency"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("KV cache-aware routing"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("LMCacheConnectorV1"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("MoRIIOConnector proof"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("MooncakeStoreConnector"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("PegaFlow-style external KV cache services"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("BlockStored extra_keys"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("cold-cache boundary"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("LoRA-adapter id"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("modality-isolated"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("text MTP KV cache"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("radix cache enabled"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("page size, KV dtype"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("SGLang FP4 KV cache"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("hierarchical KV offload proof"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("cache_hit_rate"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("sanitized replay templates"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("--max-num-batched-tokens"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("cache-aware worker selection"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("MTP-1 latency proof separate"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("Gated DeltaNet/Mamba state"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("separate cache namespaces"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("media-hash/KV-overlap"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("route/cache proof indexes"));
        Assert.That(fastModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("DBO evidence separate"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("lora_count<=1"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("--max-num-seqs"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("--max-num-partial-prefills 2"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("p95 TTFT"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("MoRIIOConnector read/write"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("Mooncake Store"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("external KV cache daemons"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("--max-running-requests"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("attention-backend pinning"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("SpecV2 requires topk=1"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("HiCache storage"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("token-bucket queue depth"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("speculative decoding disabled"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("Do not co-schedule model-native MTP"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("262,144+ local"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("video_count<=1"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("image_embeds"));
        Assert.That(fastModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("vLLM DBO"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("VLLM_MEDIA_URL_ALLOW_REDIRECTS=0"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("SSRF-sensitive"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("VLLM_MAX_N_SEQUENCES"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("cache_salt"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("response ids"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("sleep/wake dev endpoints"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("VLLM_ALLOW_RUNTIME_LORA_UPDATING"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("KV-transfer ports"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("MoRIIO proxy"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("Mooncake master"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("KV-event ZMQ"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("PEGAFLOW_HOST"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("HiCache storage backends"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("redistribute downloaded model weights"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("--api-key"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("--enable-mm-embeds lanes"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("worker health"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("draft-model paths/revisions"));
        Assert.That(fastModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("DBO data/expert-parallel"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Route replay receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Runtime capability handshake receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Model artifact provenance receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Publication receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Structured-output portability receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("vLLM scheduler/cache promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("SGLang promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("SGLang attention/precision promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("SGLang speculative promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("SGLang HiCache promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("GGUF prompt/state-cache receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Qwen3.6 context receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Speculation promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Multimodal media-admission receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Mooncake Store promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("External KV cache process-boundary receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("KV-event promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("disaggregated prefill/decode promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("vLLM MoRIIO P/D promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("vLLM DBO sparse-MoE promotion receipt"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("palllm_chat_duration_seconds"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vllm:num_requests_running"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vllm:time_to_first_token_seconds"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vllm:prefix_cache_queries"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vllm:external_prefix_cache_queries"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vLLM KV-event receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vllm:engine_sleep_state"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("sglang:num_queue_reqs"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("SGLang attention/precision receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("SGLang speculation receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("SGLang HiCache receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("SGLang replay receipt"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vllm:mm_cache_hits"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("Model artifact provenance receipt"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("media-admission receipt"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("route replay receipt"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("Runtime capability handshake receipt"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("Structured-output proof receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vLLM pressure receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("P/D topology receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vLLM MoRIIO receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("Mooncake Store receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("External KV cache receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("FlexKV offload receipts"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("Qwen3.6 hybrid-GDN receipt"));
        Assert.That(fastModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("vLLM DBO receipts"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("media UUIDs"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("libmtmd"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("encoder-cache"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("cache_salt"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("performance-mode interactivity"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("RequestPriority"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("StopSequences"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("/v1/responses"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("--kv-cache-dtype fp8"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("multi-replica vLLM pools"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("GPU memory reclaimed"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("malformed shapes"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("LoRA personality-adapter lane"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("vLLM scheduler caps"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("split P/D topology"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("MoRIIOConnector P/D"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("model-repo sampling defaults"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("frequency_penalty"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("RepetitionPenalty"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("parallel_tool_calls=false"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("logprob confidence canaries"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("UserContent canaries"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("SGLang lanes"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("SGLang attention backend lanes"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("SGLang FP4/FP8 KV lanes"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("SGLang EAGLE-3"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("SGLang HiCache lanes"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("HiCache failure proof"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("radix-cache determinism"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("structural_tag"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("schema-echo portability canary"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("schema mismatch"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("worker-scoped circuit-breaker transitions"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("modality-isolated speculative replay"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Qwen3.6 or Gemma 4 MTP"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("same-process negative canary"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("--no-enable-prefix-caching replay"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("route class separately"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("preempts or recomputes"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("request dump/replay"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("KV-block residency sampling"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("vLLM KV events"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("vLLM Mooncake Store"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Mooncake Store failure proof"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("external KV cache daemons"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("PegaFlow or FlexKV"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("primary-source capability receipt"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("model-artifact provenance receipt"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("state-cache canary"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("hybrid-GDN lanes"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Qwen3.6 context promotion"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("negative SSRF replay"));
        Assert.That(fastModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("vLLM DBO sparse-MoE lanes"));
        Assert.That(fastModel.Capability.Speculation.SupportsNgramSpeculation, Is.True);
        Assert.That(fastModel.Capability.Speculation.SupportsDraftModelSpeculation, Is.True);
        Assert.That(fastModel.Capability.Speculation.SupportsModelNativeMtp, Is.True);
        Assert.That(fastModel.Capability.Speculation.RequiresModalityIsolatedProof, Is.True);
        Assert.That(fastModel.Capability.Speculation.RequiresPrefixCacheOffForLatencyMtp, Is.True);
        Assert.That(fastModel.Capability.Speculation.RecommendedFirstMode, Is.EqualTo("mtp-1-low-concurrency-prefix-cache-off"));
        Assert.That(fastModel.Capability.Speculation.PromotionGuard, Does.Contain("route-scoped"));

        ModelCollaborationModelDescriptor edgeModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Equals("gemma4:e4b", StringComparison.Ordinal));
        Assert.That(edgeModel.Capability.Family, Is.EqualTo("gemma4"));
        Assert.That(edgeModel.Capability.InputModalities, Is.SupersetOf(new[] { "text", "image", "video", "audio" }));
        Assert.That(edgeModel.Capability.SupportsAudioInput, Is.True);
        Assert.That(edgeModel.Capability.SupportsAudioOutput, Is.False);
        Assert.That(edgeModel.Capability.SupportsToolCalls, Is.True);
        Assert.That(edgeModel.Capability.SupportsSpeculativeDecoding, Is.True);
        Assert.That(edgeModel.Capability.RecommendedBackend, Does.Contain("multimodal"));
        Assert.That(edgeModel.Capability.ServingProfile.ProfileId, Is.EqualTo("vllm-openai-multimodal"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Gemma 4 MTP drafter"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("generic draft_model"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("MTP/multimodal split-lane guard"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("OpenVINO VLM lane"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("TensorRT-LLM multimodal lane"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--limit-mm-per-prompt.audio 1"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("audio-token budget lane"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--max-num-batched-tokens"));
        Assert.That(edgeModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--media-io-kwargs"));
        Assert.That(edgeModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("model-native speculation"));
        Assert.That(edgeModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("Gemma 4 MTP benchmarking"));
        Assert.That(edgeModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("rather than generic draft models"));
        Assert.That(edgeModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("25 audio-token-per-second"));
        Assert.That(edgeModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("text-only MTP proof"));
        Assert.That(edgeModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("text-only MTP endpoint"));
        Assert.That(edgeModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("multimodal processor cache memory"));
        Assert.That(edgeModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("audio-token cost estimate"));
        Assert.That(edgeModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("text MTP KV cache"));
        Assert.That(edgeModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("LMCache EC"));
        Assert.That(edgeModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("speculative decoding disabled"));
        Assert.That(edgeModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("Do not co-schedule model-native MTP"));
        Assert.That(edgeModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("25-tokens-per-second audio estimate"));
        Assert.That(edgeModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Audio-in promotion receipt"));
        Assert.That(edgeModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Gemma 4 audio budget receipt"));
        Assert.That(edgeModel.Capability.ServingOptimizations, Has.Some.Contains("route-scoped"));
        Assert.That(edgeModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Qwen3.6 or Gemma 4 MTP"));
        Assert.That(edgeModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("modality-isolated speculative replay"));
        Assert.That(edgeModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("same-process negative canary"));
        Assert.That(edgeModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("prefix-cache-disabled benchmark"));
        Assert.That(edgeModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("assistant checkpoint id/hash"));
        Assert.That(edgeModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Gemma audio-token budget proof"));
        Assert.That(edgeModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("cascaded ASR-to-text"));
        Assert.That(edgeModel.Capability.Speculation.SupportsModelNativeMtp, Is.True);
        Assert.That(edgeModel.Capability.Speculation.RequiresModalityIsolatedProof, Is.True);
        Assert.That(edgeModel.Capability.Speculation.RequiresPrefixCacheOffForLatencyMtp, Is.False);
        Assert.That(edgeModel.Capability.Speculation.RecommendedFirstMode, Is.EqualTo("matching-gemma4-drafter"));

        ModelCollaborationModelDescriptor gemmaDenseModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Equals("gemma4:31b", StringComparison.Ordinal));
        Assert.That(gemmaDenseModel.Capability.Family, Is.EqualTo("gemma4"));
        Assert.That(gemmaDenseModel.Capability.InputModalities, Is.SupersetOf(new[] { "text", "image", "video", "audio" }));
        Assert.That(gemmaDenseModel.Capability.SupportsAudioInput, Is.True);
        Assert.That(gemmaDenseModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--limit-mm-per-prompt.audio 1"));
        Assert.That(gemmaDenseModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("25 audio tokens per second"));
        Assert.That(gemmaDenseModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("Gemma 4 audio-in"));
        Assert.That(gemmaDenseModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Gemma 4 audio budget receipt"));
        Assert.That(gemmaDenseModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("exact size/runtime accepts input_audio"));

        ModelCollaborationModelDescriptor gemma3nModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Equals("google/gemma-3n-E4B-it", StringComparison.Ordinal));
        Assert.That(gemma3nModel.Capability.Family, Is.EqualTo("gemma3n"));
        Assert.That(gemma3nModel.Capability.InputModalities, Is.SupersetOf(new[] { "text", "image", "video", "audio" }));
        Assert.That(gemma3nModel.Capability.OutputModalities, Is.EqualTo(new[] { "text" }));
        Assert.That(gemma3nModel.Capability.SupportsAudioInput, Is.True);
        Assert.That(gemma3nModel.Capability.SupportsAudioOutput, Is.False);
        Assert.That(gemma3nModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Gemma 3n edge lane"));
        Assert.That(gemma3nModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("OpenVINO ASR proof lane"));
        Assert.That(gemma3nModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Audio ingress normalization"));
        Assert.That(gemma3nModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("6.25 audio tokens per second"));
        Assert.That(gemma3nModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("input_audio"));
        Assert.That(gemma3nModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("modalities needed"));
        Assert.That(gemma3nModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("6.25 audio-token-per-second"));
        Assert.That(gemma3nModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("PLE cache"));
        Assert.That(gemma3nModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("<=30 seconds"));
        Assert.That(gemma3nModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("6.25-tokens-per-second audio estimate"));
        Assert.That(gemma3nModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Gemma 3n audio budget receipt"));
        Assert.That(gemma3nModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("parameter skipping"));

        ModelCollaborationModelDescriptor qwenOmniModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Equals("Qwen/Qwen3-Omni-30B-A3B-Instruct", StringComparison.Ordinal));
        Assert.That(qwenOmniModel.Capability.Family, Is.EqualTo("qwen-omni"));
        Assert.That(qwenOmniModel.Capability.InputModalities, Is.SupersetOf(new[] { "text", "image", "video", "audio" }));
        Assert.That(qwenOmniModel.Capability.OutputModalities, Is.SupersetOf(new[] { "text", "audio" }));
        Assert.That(qwenOmniModel.Capability.SupportsAudioInput, Is.True);
        Assert.That(qwenOmniModel.Capability.SupportsAudioOutput, Is.True);
        Assert.That(qwenOmniModel.Capability.ServingProfile.ProfileId, Is.EqualTo("omni-realtime-opt-in"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.PreferredRuntime, Does.Contain("vLLM-Omni"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("vllm serve <model> --omni"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("async_chunk disabled"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("/v1/video/chat/stream"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("/v1/videos"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("modalities [\"text\",\"audio\"]"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("text mirror"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("streaming video"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("/v1/videos jobs"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("InferencePrompt.Modalities"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("InferenceResult.AudioJson"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("Realtime audio"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("async_chunk-off proof"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("/v1/video/chat/stream"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("/v1/videos generation jobs"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Realtime audio promotion receipt"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("streaming-video promotion receipt"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Qwen Omni"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("/v1/video/chat/stream"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("response.audio.delta"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("session.created"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("InferenceResult.AudioJson"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("/v1/videos"));
        Assert.That(qwenOmniModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Qwen3.5-Omni research"));
        Assert.That(qwenOmniModel.Capability.RuntimeGuards, Has.Some.Contains("audio-in and realtime voice opt-in"));

        ModelCollaborationModelDescriptor textModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Equals("qwen-coder-text", StringComparison.Ordinal));
        Assert.That(textModel.Capability.InputModalities, Is.EqualTo(new[] { "text" }));
        Assert.That(textModel.Capability.SupportsSpeculativeDecoding, Is.True);
        Assert.That(textModel.Capability.Speculation.SupportsNgramSpeculation, Is.True);
        Assert.That(textModel.Capability.Speculation.SupportsModelNativeMtp, Is.False);
        Assert.That(textModel.Capability.Speculation.RequiresModalityIsolatedProof, Is.False);
        Assert.That(textModel.Capability.Speculation.RecommendedFirstMode, Is.EqualTo("openai-compatible-ngram"));
        Assert.That(textModel.Capability.ServingProfile.PreferredRuntime, Does.Contain("SGLang"));
        Assert.That(textModel.Capability.ServingProfile.PreferredRuntime, Does.Contain("OpenVINO Model Server"));
        Assert.That(textModel.Capability.ServingProfile.PreferredRuntime, Does.Contain("TensorRT-LLM"));
        Assert.That(textModel.Capability.ServingProfile.PreferredRuntime, Does.Contain("transformers serve"));
        Assert.That(textModel.Capability.ServingProfile.PreferredRuntime, Does.Contain("Foundry Local"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("\"method\":\"ngram\""));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--enable-lora"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("OpenVINO Model Server lane"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("PREFILL_HINT"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Transformers serve provenance lane"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Transformers serve local lane"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("/load_model"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Responses API lane"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("Foundry Local single-user lane"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("TensorRT-LLM lane"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--max-num-seqs"));
        Assert.That(textModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--tool_call_parser"));
        Assert.That(textModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("n-gram or suffix"));
        Assert.That(textModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("BaseUrl to http://localhost:<port>/v3/"));
        Assert.That(textModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("positional serve argument"));
        Assert.That(textModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("experimental endpoint"));
        Assert.That(textModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("/openai/models"));
        Assert.That(textModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("/metrics endpoint"));
        Assert.That(textModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("Hugging Face local cache"));
        Assert.That(textModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("Foundry model cache"));
        Assert.That(textModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("OpenVINO model pull"));
        Assert.That(textModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("TensorRT-LLM"));
        Assert.That(textModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("Dynamo"));
        Assert.That(textModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("lora_count<=1"));
        Assert.That(textModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("continuous batching"));
        Assert.That(textModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("/v1/responses off live companion routing"));
        Assert.That(textModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("OpenVINO Model Server"));
        Assert.That(textModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("TensorRT-LLM endpoints"));
        Assert.That(textModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("single-user client runtime"));
        Assert.That(textModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("trust_remote_code"));
        Assert.That(textModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("response ids"));
        Assert.That(textModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("OpenVINO Model Server loopback-only"));
        Assert.That(textModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("TensorRT-LLM loopback-only"));
        Assert.That(textModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("Foundry Local service loopback-only"));
        Assert.That(textModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("SGLang Model Gateway metrics"));
        Assert.That(textModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("transformers serve promotion receipt"));
        Assert.That(textModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Foundry Local promotion receipt"));
        Assert.That(textModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("OpenVINO promotion receipt"));
        Assert.That(textModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("TensorRT-LLM promotion receipt"));
        Assert.That(textModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("transformers serve receipts"));
        Assert.That(textModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("Foundry Local receipts"));
        Assert.That(textModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("OpenVINO receipts"));
        Assert.That(textModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("TensorRT-LLM /metrics"));
        Assert.That(textModel.Capability.ServingOptimizations, Has.Some.Contains("chunked prefill"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("structured-output reliability"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("accepted/proposed token ratio"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("strict JSON"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("OpenVINO Model Server lanes"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("transformers serve --continuous-batching"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("/v1/responses"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("Foundry Local lanes"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("TensorRT-LLM lanes"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("kvCacheStats"));
        Assert.That(textModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("tool calling"));

        ModelCollaborationModelDescriptor ggufTextModel = snapshot.ConfiguredModels
            .Single(model => model.ModelId.Equals("local-text-worker-GGUF", StringComparison.Ordinal));
        Assert.That(ggufTextModel.Capability.ServingProfile.ProfileId, Is.EqualTo("gguf-chat"));
        Assert.That(ggufTextModel.Capability.ServingProfile.PreferredRuntime, Does.Contain("GGUF"));
        Assert.That(ggufTextModel.Capability.Speculation.RecommendedFirstMode, Is.EqualTo("llama.cpp-ngram-simple"));
        Assert.That(ggufTextModel.Capability.Speculation.RequiresPrefixCacheOffForLatencyMtp, Is.False);
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("GGUF artifact provenance lane"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("llama.cpp prompt-cache lane"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("state-cache canary"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("pal connect llamacpp"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("llama.cpp schema lane"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--sleep-idle-seconds"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("--spec-type ngram-simple --spec-draft-n-max"));
        // Pass 346: 6 OLLAMA_* / "Ollama structured-output lane" startup-hint
        // assertions removed — the corresponding planner hints were
        // deleted alongside the rest of the Ollama back-compat path.
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.None.Contains("OLLAMA_"),
            "Pass 346 removed every Ollama startup hint; no OLLAMA_* env-var advice should leak through.");
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.None.Contains("Ollama"),
            "Pass 346 removed every Ollama startup hint; no operator-facing 'Ollama' mention should remain.");
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("LM Studio desktop lane"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints, Has.Some.Contains("ResidencyProvider=LmStudio"));
        Assert.That(ggufTextModel.Capability.ServingProfile.StartupHints.Any(hint => hint.Contains("--speculative-config", StringComparison.Ordinal)), Is.False);
        Assert.That(ggufTextModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("cache_prompt enabled"));
        Assert.That(ggufTextModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("tokenizer metadata"));
        // Pass 346: the "load_duration" Ollama-native proof-call hint was
        // removed; llama-server's /metrics endpoint now provides the
        // equivalent timing receipts via the llama.cpp connector lane.
        Assert.That(ggufTextModel.Capability.ServingProfile.RequestHints, Has.None.Contains("load_duration"));
        Assert.That(ggufTextModel.Capability.ServingProfile.RequestHints, Has.Some.Contains("ttl residency hint"));
        Assert.That(ggufTextModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("-cram pressure"));
        Assert.That(ggufTextModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("host prompt-cache restore"));
        // Pass 346: the "q4_0 off the player path" cache hint was the
        // OLLAMA_KV_CACHE_TYPE advice — removed alongside the rest of
        // the Ollama back-compat path. llama.cpp -ctk/-ctv guidance
        // higher up replaces the equivalent quantized-KV proof note.
        Assert.That(ggufTextModel.Capability.ServingProfile.CacheHints, Has.None.Contains("q4_0 off the player path"));
        Assert.That(ggufTextModel.Capability.ServingProfile.CacheHints, Has.Some.Contains("loaded-model TTL"));
        Assert.That(ggufTextModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("GGUF prompt/state-cache receipt"));
        Assert.That(ggufTextModel.Capability.ServingProfile.PromotionReceipts, Has.Some.Contains("Structured-output portability receipt"));
        Assert.That(ggufTextModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("llama.cpp /metrics"));
        Assert.That(ggufTextModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("forced full prompt re-processing"));
        // Pass 346: "Ollama proof metrics" receipt removed.
        Assert.That(ggufTextModel.Capability.ServingProfile.MetricReceipts, Has.None.Contains("Ollama"));
        Assert.That(ggufTextModel.Capability.ServingProfile.MetricReceipts, Has.Some.Contains("LM Studio proof receipts"));
        Assert.That(ggufTextModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("cap -np"));
        // Pass 346: OLLAMA_NUM_PARALLEL=1 admission control removed.
        Assert.That(ggufTextModel.Capability.ServingProfile.AdmissionControls, Has.None.Contains("OLLAMA_"));
        Assert.That(ggufTextModel.Capability.ServingProfile.AdmissionControls, Has.Some.Contains("single-user local lane"));
        Assert.That(ggufTextModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("--webui-mcp-proxy"));
        // Pass 346: OLLAMA_HOST and OLLAMA_NO_CLOUD=1 security controls
        // removed.
        Assert.That(ggufTextModel.Capability.ServingProfile.SecurityControls, Has.None.Contains("OLLAMA_"));
        Assert.That(ggufTextModel.Capability.ServingProfile.SecurityControls, Has.Some.Contains("LM Studio's server on loopback"));
        Assert.That(ggufTextModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("accepted/generated token statistics"));
        Assert.That(ggufTextModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("same PalLLM prefix twice"));
        Assert.That(ggufTextModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("/health"));
        // Pass 346: `ollama ps` verification check + OLLAMA_KV_CACHE_TYPE
        // quantization proof removed (the q8_0/q4_0 receipt is now
        // covered by the llama.cpp -ctk/-ctv verification).
        Assert.That(ggufTextModel.Capability.ServingProfile.VerificationChecks, Has.None.Contains("ollama"));
        Assert.That(ggufTextModel.Capability.ServingProfile.VerificationChecks, Has.None.Contains("OLLAMA_"));
        Assert.That(ggufTextModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("json_object-only mode"));
        Assert.That(ggufTextModel.Capability.ServingProfile.VerificationChecks, Has.Some.Contains("LM Studio lanes"));

        ModelCollaborationRecipe recipe = snapshot.Recipes.Single(r => r.Id == "fast-draft-dense-judge");
        Assert.That(recipe.Stages[0].PreferredModel, Does.Contain("35B-A3B"));
        Assert.That(recipe.Stages[1].PreferredModel, Does.Contain("27B"));
        Assert.That(
            snapshot.RoutingPolicies.Single(policy => policy.Id == "high-risk-deliberate-bookends").RequiresHumanReview,
            Is.True);
        Assert.That(
            snapshot.QualificationSuite.Checks.Select(check => check.Id),
            Contains.Item("exact-json-tool-call"));
        Assert.That(
            snapshot.HardwarePlaybook.Single(tier => tier.TierId == "workstation").RecommendedRunMode,
            Is.EqualTo("parallel"));
    }

    [Test]
    public void CollaborationPlanner_WhenHybridOffloadHardwareProvided_PrefersSequentialBatonPassing()
    {
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "worker", Model = "unsloth/Qwen3.6-35B-A3B-GGUF", Priority = 10 },
            new ModelTierOptions { Id = "judge", Model = "unsloth/Qwen3.6-27B-GGUF", Priority = 9 },
        ];

        var planner = new ModelCollaborationPlanner(
            options,
            new ModelTierOrchestrator(options, new StubProbe("unsloth/Qwen3.6-35B-A3B-GGUF")));

        ModelCollaborationSnapshot snapshot = planner.GetSnapshot(new ModelHardwareHints(
            VramGb: 12,
            RamGb: 64,
            PreferParallel: true));

        Assert.That(snapshot.Hardware.ClassId, Is.EqualTo("hybrid-offload"));
        Assert.That(snapshot.Hardware.PreferSequentialBatonPassing, Is.True);
        Assert.That(snapshot.DeploymentNotes[0], Does.Contain("Sequential baton passing"));
        Assert.That(
            snapshot.RoutingPolicies.Single(policy => policy.Id == "context-compiler-then-dense-reasoning").PreferredFlow,
            Does.Contain("sequential-baton"));
    }

    [Test]
    public void CollaborationDecisionPlanner_WhenHighRiskToolHeavyTaskProvided_UsesDenseBookendsAndGuardrails()
    {
        var options = new PalLlmOptions();
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "worker", Model = "unsloth/Qwen3.6-35B-A3B-GGUF", Priority = 10 },
            new ModelTierOptions { Id = "judge", Model = "unsloth/Qwen3.6-27B-GGUF", Priority = 9 },
        ];

        var decisionPlanner = new ModelCollaborationDecisionPlanner(
            new ModelCollaborationPlanner(
                options,
                new ModelTierOrchestrator(options, new StubProbe(
                    "unsloth/Qwen3.6-35B-A3B-GGUF",
                    "unsloth/Qwen3.6-27B-GGUF"))));

        ModelCollaborationDecision decision = decisionPlanner.Plan(new ModelCollaborationDecisionRequest(
            Task: "Plan and implement a release-facing auth migration with tool-driven repo edits",
            TaskClass: "coding",
            RiskLevel: "high",
            ToolHeavy: true,
            ReleaseGate: true,
            VramGb: 48,
            RamGb: 128,
            PreferParallel: true));

        Assert.That(decision.SelectedPolicyId, Is.EqualTo("high-risk-deliberate-bookends"));
        Assert.That(decision.SelectedRecipeId, Is.EqualTo("dense-plan-fast-execute-dense-audit"));
        Assert.That(decision.RunMode, Is.EqualTo("parallel"));
        Assert.That(decision.DeliberateLaneModel, Does.Contain("27B"));
        Assert.That(decision.FastLaneModel, Does.Contain("35B-A3B"));
        Assert.That(decision.HumanReviewRequired, Is.True);
        Assert.That(decision.PreserveThinking.DeliberateLane, Is.True);
        Assert.That(decision.Validators, Has.Some.Contains("Security"));
        Assert.That(decision.PromotionCriteria, Has.Some.Contains("exact-json-tool-call"));
    }

    [Test]
    public async Task InferenceClient_WhenOrchestratorProvided_SendsActiveTierModel()
    {
        // The inference client must read the CURRENTLY active model at
        // request time, not the static Options.Model, so a mid-session
        // graduation takes effect on the next chat without restart.
        using var handler = new RecordingChatHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.Model = "qwen3.6:35b-a3b";
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "small", Model = "gemma3:4b", Priority = 1 },
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
        ];
        var probe = new StubProbe("gemma3:4b"); // only small available

        var orchestrator = new ModelTierOrchestrator(options, probe);
        await orchestrator.RefreshAsync(CancellationToken.None);

        var client = new HttpJsonInferenceClient(httpClient, options, orchestrator);
        _ = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "system",
            UserPrompt = "user",
            Temperature = 0.5f,
            MaxTokens = 64,
        }, CancellationToken.None);

        using System.Text.Json.JsonDocument body = System.Text.Json.JsonDocument.Parse(handler.LastRequestBody);
        string? sentModel = body.RootElement.GetProperty("model").GetString();
        Assert.That(sentModel, Is.EqualTo("gemma3:4b"),
            "Inference client must route to the orchestrator's active tier, not Options.Model.");
    }

    [Test]
    public async Task InferenceClient_WhenOrchestratorGraduatesMidSession_NextCallUsesNewTier()
    {
        using var handler = new RecordingChatHandler();
        using var httpClient = new HttpClient(handler);
        var options = new PalLlmOptions();
        options.Inference.Enabled = true;
        options.Inference.Model = "qwen3.6:35b-a3b";
        options.Inference.ModelTiers =
        [
            new ModelTierOptions { Id = "small", Model = "gemma3:4b", Priority = 1 },
            new ModelTierOptions { Id = "large", Model = "qwen3.6:35b-a3b", Priority = 10 },
        ];
        var probe = new StubProbe("gemma3:4b");
        var orchestrator = new ModelTierOrchestrator(options, probe);
        await orchestrator.RefreshAsync(CancellationToken.None);

        var client = new HttpJsonInferenceClient(httpClient, options, orchestrator);

        _ = await client.CompleteAsync(new InferencePrompt { SystemPrompt = "s", UserPrompt = "u" }, CancellationToken.None);
        Assert.That(System.Text.Json.JsonDocument.Parse(handler.LastRequestBody).RootElement.GetProperty("model").GetString(),
            Is.EqualTo("gemma3:4b"));

        // Large finishes downloading
        probe.SetAvailable("gemma3:4b", "qwen3.6:35b-a3b");
        ModelTierRefreshResult graduation = await orchestrator.RefreshAsync(CancellationToken.None);
        Assert.That(graduation.ActiveTierId, Is.EqualTo("large"));

        _ = await client.CompleteAsync(new InferencePrompt { SystemPrompt = "s", UserPrompt = "u" }, CancellationToken.None);
        Assert.That(System.Text.Json.JsonDocument.Parse(handler.LastRequestBody).RootElement.GetProperty("model").GetString(),
            Is.EqualTo("qwen3.6:35b-a3b"),
            "Second request post-graduation must use the newly-active large tier.");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static PalLlmOptions BuildOptionsWithBaseUrl(string baseUrl)
    {
        var options = new PalLlmOptions();
        options.Inference.BaseUrl = baseUrl;
        return options;
    }

    private sealed class StubProbe : IModelAvailabilityProbe
    {
        private HashSet<string> _available;

        public StubProbe(params string[] available)
        {
            _available = new HashSet<string>(available, StringComparer.Ordinal);
        }

        public void SetAvailable(params string[] available)
        {
            _available = new HashSet<string>(available, StringComparer.Ordinal);
        }

        public Task<IReadOnlySet<string>> GetAvailableModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlySet<string>>(_available);
    }

    private sealed class ScriptedProbeHandler : HttpMessageHandler
    {
        public sealed record Route(string PathSuffix, HttpStatusCode Status, HttpContent Content)
        {
            public Route(string pathSuffix, HttpStatusCode status, string body, string contentType)
                : this(pathSuffix, status, new StringContent(body, Encoding.UTF8, contentType))
            {
            }
        }

        private readonly IReadOnlyList<Route> _routes;

        public ScriptedProbeHandler(IReadOnlyList<Route> routes)
        {
            _routes = routes;
        }

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            foreach (Route route in _routes)
            {
                if (path.EndsWith(route.PathSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(route.Status)
                    {
                        Content = route.Content,
                    });
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            });
        }
    }

    private sealed class ThrowingProbeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("simulated probe failure");
        }
    }

    private sealed class TrackingReadStream : MemoryStream
    {
        public TrackingReadStream(byte[] buffer) : base(buffer, writable: false)
        {
        }

        public bool ReadStarted { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadStarted = true;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            ReadStarted = true;
            return base.Read(buffer);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ReadStarted = true;
            return base.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted = true;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    private sealed class UnknownLengthReadContent : HttpContent
    {
        private readonly Stream _stream;

        public UnknownLengthReadContent(Stream stream, string contentType)
        {
            _stream = stream;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException("Test content is only consumed through ReadAsStreamAsync.");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult(_stream);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class RecordingChatHandler : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
