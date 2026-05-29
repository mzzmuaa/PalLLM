using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

public sealed class ModelCollaborationPlanner
{
    private readonly PalLlmOptions _options;
    private readonly ModelTierOrchestrator _orchestrator;

    public ModelCollaborationPlanner(PalLlmOptions options, ModelTierOrchestrator orchestrator)
    {
        _options = options;
        _orchestrator = orchestrator;
    }

    public ModelCollaborationSnapshot GetSnapshot(ModelHardwareHints? hints = null)
    {
        ModelHardwareHints effectiveHints = hints ?? new();
        ModelHardwareProfile hardware = BuildHardwareProfile(effectiveHints);
        ModelCollaborationModelDescriptor[] configuredModels = BuildConfiguredModels();

        ModelCollaborationModelDescriptor primaryFastModel = PickPreferredFastModel(configuredModels);
        ModelCollaborationModelDescriptor primaryDeliberateModel = PickPreferredDeliberateModel(configuredModels);
        string[] lastSeenAvailableModels = _orchestrator.GetLastSeenAvailableModels().ToArray();

        return new ModelCollaborationSnapshot(
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            Hardware: hardware,
            ActiveModel: _orchestrator.GetActiveModel(),
            ActiveTierId: _orchestrator.GetActiveTierId(),
            LastSeenAvailableModels: lastSeenAvailableModels,
            ConfiguredModels: configuredModels,
            Recipes: BuildRecipes(hardware, primaryFastModel, primaryDeliberateModel),
            RoutingPolicies: BuildRoutingPolicies(hardware, primaryFastModel, primaryDeliberateModel),
            QualificationSuite: BuildQualificationSuite(hardware, primaryFastModel, primaryDeliberateModel),
            HardwarePlaybook: BuildHardwarePlaybook(primaryFastModel, primaryDeliberateModel),
            DeploymentNotes: BuildDeploymentNotes(hardware, primaryFastModel, primaryDeliberateModel),
            SelfHealingIdeas: BuildSelfHealingIdeas(primaryFastModel, primaryDeliberateModel));
    }

    private ModelCollaborationModelDescriptor[] BuildConfiguredModels()
    {
        List<ModelCollaborationModelDescriptor> models = new();

        if (_options.Inference.ModelTiers.Count == 0)
        {
            models.Add(BuildDescriptor(
                modelId: _options.Inference.Model,
                tierId: null,
                priority: 0,
                isActive: string.Equals(_options.Inference.Model, _orchestrator.GetActiveModel(), StringComparison.Ordinal)));
        }
        else
        {
            foreach (ModelTierOptions tier in _options.Inference.ModelTiers)
            {
                models.Add(BuildDescriptor(
                    modelId: tier.Model,
                    tierId: tier.Id,
                    priority: tier.Priority,
                    isActive: string.Equals(tier.Model, _orchestrator.GetActiveModel(), StringComparison.Ordinal)));
            }
        }

        return models
            .OrderByDescending(model => model.Priority)
            .ThenBy(model => model.ModelId, StringComparer.Ordinal)
            .ToArray();
    }

    private static ModelCollaborationModelDescriptor BuildDescriptor(
        string modelId,
        string? tierId,
        int priority,
        bool isActive)
    {
        string normalized = NormalizeModelId(modelId);
        bool isSparseMoe = IsSparseMoe(normalized);
        bool isRecognizedQwen27 = normalized.Contains("qwen3.6") && normalized.Contains("27b");
        bool isRecognizedQwen35A3B = normalized.Contains("qwen3.6") && normalized.Contains("35b") && normalized.Contains("a3b");
        ModelCapabilityProfile capability = BuildCapabilityProfile(normalized, isSparseMoe);
        bool likelyMultimodal = capability.SupportsVisionInput
            || capability.SupportsVideoInput
            || capability.SupportsAudioInput
            || capability.SupportsAudioOutput;

        string architecture = isSparseMoe ? "sparse-moe" : "dense";
        string operatingStyle = isSparseMoe ? "fast-iterative" : "deliberate";

        string[] primaryRoles = isSparseMoe
            ? ["bridge-scout", "reply-drafter", "tool-loop-runner", "screenshot-auditor"]
            : ["planner", "reviewer", "constraint-keeper", "final-judge"];

        string[] strengths = isSparseMoe
            ? [
                "Rapid PalLLM repo sweeps and candidate generation",
                "Cheaper branch fan-out for bridge, HUD, and docs work",
                "Good resident watchdog for background audits and screenshot loops",
            ]
            : [
                "Better global rule retention and patch coherence",
                "Stronger final review for runtime, bridge, and docs changes",
                "Better fit for deliberate repo-level and release-readiness audits",
            ];

        string[] cautions = isSparseMoe
            ? [
                "Needs a stricter verifier when the task touches release-facing or native-seam rules",
                "Long unattended loops benefit from periodic dense-model checkpoints",
            ]
            : [
                "Higher latency, so it is best used at decision boundaries",
                "Less efficient for wide speculative search or continuous background monitoring",
            ];

        ModelAuthorityProfile authority = BuildAuthorityProfile(isSparseMoe);
        List<string> modelNotes = new();
        if (isRecognizedQwen27)
        {
            modelNotes.Add("Qwen3.6-27B is a strong dense reviewer and finalizer for PalLLM runtime, bridge, and docs-sync work.");
            modelNotes.Add("The official Qwen3.6-27B card leads the open 35B-A3B sibling on several repo-grade coding benchmarks, which makes it a good default judge for higher-risk PalLLM changes.");
        }

        if (isRecognizedQwen35A3B)
        {
            modelNotes.Add("Qwen3.6-35B-A3B is well-suited to fast draft, tool-loop, screenshot-review, and watchdog roles.");
            modelNotes.Add("The sparse active-parameter budget makes it a good worker lane for bridge triage, doc drift patrol, test mining, and quick implementation loops.");
        }

        if (likelyMultimodal)
        {
            modelNotes.Add("Official Qwen3.6 weights are multimodal; Palworld screenshot work may still use a separate vision-capable lane when local text-only GGUFs are deployed.");
        }

        return new ModelCollaborationModelDescriptor(
            ModelId: modelId,
            TierId: tierId,
            Priority: priority,
            IsActive: isActive,
            Architecture: architecture,
            OperatingStyle: operatingStyle,
            LikelyMultimodal: likelyMultimodal,
            Capability: capability,
            PrimaryRoles: primaryRoles,
            Authority: authority,
            Strengths: strengths,
            Cautions: cautions,
            Notes: modelNotes.ToArray());
    }

    private static ModelCapabilityProfile BuildCapabilityProfile(string normalizedModelId, bool isSparseMoe)
    {
        bool isGemma4 = normalizedModelId.Contains("gemma4", StringComparison.Ordinal)
            || normalizedModelId.Contains("gemma-4", StringComparison.Ordinal)
            || normalizedModelId.Contains("gemma_4", StringComparison.Ordinal);
        bool isGemma3n = normalizedModelId.Contains("gemma3n", StringComparison.Ordinal)
            || normalizedModelId.Contains("gemma-3n", StringComparison.Ordinal)
            || normalizedModelId.Contains("gemma_3n", StringComparison.Ordinal);
        bool isQwen = normalizedModelId.Contains("qwen", StringComparison.Ordinal);
        bool isQwenOmni = isQwen && normalizedModelId.Contains("omni", StringComparison.Ordinal);
        bool isQwen36 = normalizedModelId.Contains("qwen3.6", StringComparison.Ordinal);
        bool isQwenVl = isQwen
            && (normalizedModelId.Contains("-vl", StringComparison.Ordinal)
                || normalizedModelId.Contains("_vl", StringComparison.Ordinal)
                || normalizedModelId.Contains("2-vl", StringComparison.Ordinal)
                || normalizedModelId.Contains("2.5-vl", StringComparison.Ordinal)
                || normalizedModelId.Contains("3-vl", StringComparison.Ordinal)
                || normalizedModelId.Contains("3.5-vl", StringComparison.Ordinal)
                || normalizedModelId.Contains("vision", StringComparison.Ordinal));
        bool isAudioTagged = normalizedModelId.Contains("audio", StringComparison.Ordinal)
            || normalizedModelId.Contains("asr", StringComparison.Ordinal)
            || normalizedModelId.Contains("voxtral", StringComparison.Ordinal)
            || normalizedModelId.Contains("ultravox", StringComparison.Ordinal);
        bool isEmbedding = normalizedModelId.Contains("embed", StringComparison.Ordinal)
            || normalizedModelId.Contains("bge-", StringComparison.Ordinal)
            || normalizedModelId.Contains("nomic-", StringComparison.Ordinal);
        bool isGguf = normalizedModelId.Contains("gguf", StringComparison.Ordinal);

        bool supportsVision = isGemma4 || isGemma3n || isQwenOmni || isQwen36 || isQwenVl;
        bool supportsVideo = isGemma4 || isGemma3n || isQwenOmni || isQwen36;
        bool supportsAudioInput = isQwenOmni || isGemma4 || isGemma3n || isAudioTagged;
        bool supportsAudioOutput = isQwenOmni;
        bool supportsToolCalls = isQwen
            || isGemma4
            || normalizedModelId.Contains("coder", StringComparison.Ordinal)
            || normalizedModelId.Contains("tool", StringComparison.Ordinal);
        bool supportsStructuredOutputs = !isEmbedding;
        bool supportsSpeculativeDecoding = !isEmbedding && (isSparseMoe || supportsToolCalls || !supportsVision);
        bool multimodal = supportsVision || supportsVideo || supportsAudioInput || supportsAudioOutput;
        bool preferVllm = !isGguf || supportsVideo || supportsAudioOutput;
        bool supportsModelNativeMtp = preferVllm && !isEmbedding && (isQwen36 || isGemma4);

        List<string> inputModalities = ["text"];
        if (supportsVision)
        {
            inputModalities.Add("image");
        }
        if (supportsVideo)
        {
            inputModalities.Add("video");
        }
        if (supportsAudioInput)
        {
            inputModalities.Add("audio");
        }

        List<string> outputModalities = isEmbedding ? ["embedding"] : ["text"];
        if (supportsAudioOutput)
        {
            outputModalities.Add("audio");
        }

        string family = isGemma4 ? "gemma4"
            : isGemma3n ? "gemma3n"
            : isQwenOmni ? "qwen-omni"
            : isQwen36 ? "qwen3.6"
            : isQwen ? "qwen"
            : isEmbedding ? "embedding"
            : "generic-openai-compatible";

        string recommendedBackend = BuildRecommendedBackend(
            isGguf,
            isEmbedding,
            multimodal);
        ModelServingProfile servingProfile = BuildServingProfile(
            isGguf,
            isEmbedding,
            isSparseMoe,
            supportsVision,
            supportsVideo,
            supportsAudioInput,
            supportsAudioOutput,
            isQwen36,
            isQwenOmni,
            isGemma3n,
            isGemma4,
            supportsStructuredOutputs,
            supportsToolCalls,
            supportsSpeculativeDecoding);
        ModelSpeculationProfile speculationProfile = BuildSpeculationProfile(
            isGguf,
            supportsSpeculativeDecoding,
            supportsModelNativeMtp,
            multimodal,
            isQwen36,
            isGemma4);
        string[] optimizations = BuildServingOptimizations(
            supportsStructuredOutputs,
            supportsToolCalls,
            supportsSpeculativeDecoding,
            supportsVision,
            supportsVideo,
            supportsAudioInput,
            supportsAudioOutput);
        string[] guards = BuildRuntimeGuards(
            supportsVision,
            supportsVideo,
            supportsAudioInput,
            supportsAudioOutput,
            supportsStructuredOutputs);

        return new ModelCapabilityProfile(
            Family: family,
            RecommendedBackend: recommendedBackend,
            ServingProfile: servingProfile,
            InputModalities: inputModalities.ToArray(),
            OutputModalities: outputModalities.ToArray(),
            SupportsVisionInput: supportsVision,
            SupportsVideoInput: supportsVideo,
            SupportsAudioInput: supportsAudioInput,
            SupportsAudioOutput: supportsAudioOutput,
            SupportsStructuredOutputs: supportsStructuredOutputs,
            SupportsToolCalls: supportsToolCalls,
            SupportsSpeculativeDecoding: supportsSpeculativeDecoding,
            Speculation: speculationProfile,
            ServingOptimizations: optimizations,
            RuntimeGuards: guards);
    }

    private static ModelSpeculationProfile BuildSpeculationProfile(
        bool isGguf,
        bool supportsSpeculativeDecoding,
        bool supportsModelNativeMtp,
        bool multimodal,
        bool isQwen36,
        bool isGemma4)
    {
        if (!supportsSpeculativeDecoding)
        {
            return new(
                SupportsNgramSpeculation: false,
                SupportsDraftModelSpeculation: false,
                SupportsModelNativeMtp: false,
                RequiresModalityIsolatedProof: false,
                RequiresPrefixCacheOffForLatencyMtp: false,
                RecommendedFirstMode: "none",
                PromotionGuard: "Speculative decoding is not recommended for this lane; use normal deterministic PalLLM replay evidence.");
        }

        string recommendedFirstMode = supportsModelNativeMtp && isQwen36
            ? "mtp-1-low-concurrency-prefix-cache-off"
            : supportsModelNativeMtp && isGemma4
                ? "matching-gemma4-drafter"
                : isGguf
                    ? "llama.cpp-ngram-simple"
                    : "openai-compatible-ngram";

        string promotionGuard = multimodal
            ? "Treat speculation as route-scoped: text, screenshot/image, video, and audio/ASR each need independent no-spec, n-gram, and model-native replay proof."
            : "Keep strict JSON, tool-call, judge, and save-replay routes no-spec until each route has repeated-run proof for this exact mode.";

        return new(
            SupportsNgramSpeculation: true,
            SupportsDraftModelSpeculation: true,
            SupportsModelNativeMtp: supportsModelNativeMtp,
            RequiresModalityIsolatedProof: multimodal,
            RequiresPrefixCacheOffForLatencyMtp: supportsModelNativeMtp && isQwen36,
            RecommendedFirstMode: recommendedFirstMode,
            PromotionGuard: promotionGuard);
    }

    private static string BuildRecommendedBackend(
        bool isGguf,
        bool isEmbedding,
        bool multimodal)
    {
        if (isEmbedding)
        {
            return "embedding endpoint behind the retrieval lane";
        }

        if (isGguf && multimodal)
        {
            return "llama.cpp libmtmd or vLLM OpenAI-compatible multimodal server";
        }

        if (isGguf)
        {
            // Pass 346: Ollama removed from the suggested runtime list.
            // llama.cpp (PalLLM's bundled default) and vLLM cover the
            // GGUF path; LM Studio remains for desktop operators.
            return "llama.cpp, vLLM, LM Studio, or another OpenAI-compatible GGUF server";
        }

        return multimodal
            ? "vLLM, SGLang, or TensorRT-LLM OpenAI-compatible multimodal server"
            : "OpenAI-compatible chat-completions server";
    }

    private static ModelServingProfile BuildServingProfile(
        bool isGguf,
        bool isEmbedding,
        bool isSparseMoe,
        bool supportsVision,
        bool supportsVideo,
        bool supportsAudioInput,
        bool supportsAudioOutput,
        bool isQwen36,
        bool isQwenOmni,
        bool isGemma3n,
        bool isGemma4,
        bool supportsStructuredOutputs,
        bool supportsToolCalls,
        bool supportsSpeculativeDecoding)
    {
        bool multimodal = supportsVision || supportsVideo || supportsAudioInput || supportsAudioOutput;
        bool preferVllm = !isGguf || supportsVideo || supportsAudioOutput;
        bool preferSglang = preferVllm && !isEmbedding;
        bool preferTransformersServe = !isGguf && !isEmbedding;
        bool preferFoundryLocal = !isGguf && !isEmbedding && !supportsAudioOutput;
        bool preferOpenVino = !isGguf && !isEmbedding && !supportsAudioOutput;
        bool preferTensorRtLlm = !isGguf && !isEmbedding && !supportsAudioOutput;
        bool supportsModelNativeMtp = preferVllm && !isEmbedding && (isQwen36 || isGemma4);
        bool sparseVllmDboProofLane = preferVllm && !isEmbedding && isSparseMoe;
        bool isGemmaAudio = supportsAudioInput && (isGemma4 || isGemma3n);
        string gemmaAudioFamily = isGemma4 ? "Gemma 4" : "Gemma 3n";
        string gemmaAudioTokenRate = isGemma4 ? "25" : "6.25";
        string gemmaThirtySecondTokenCost = isGemma4 ? "750" : "188";

        string profileId = isEmbedding ? "embedding-retrieval"
            : supportsAudioOutput ? "omni-realtime-opt-in"
            : multimodal && isGguf ? "gguf-libmtmd-multimodal"
            : multimodal ? "vllm-openai-multimodal"
            : isGguf ? "gguf-chat"
            : "openai-chat";

        string requestProtocol = isEmbedding ? "OpenAI-compatible /v1/embeddings or provider-native embedding endpoint"
            : supportsAudioOutput ? "OpenAI-compatible /v1/chat/completions for text plus separate opt-in /v1/realtime for audio"
            : "OpenAI-compatible /v1/chat/completions";

        string preferredRuntime = profileId switch
        {
            "embedding-retrieval" => "local embedding server behind the retrieval lane",
            "gguf-libmtmd-multimodal" => "llama.cpp server with libmtmd and a matching mmproj, or vLLM when the GGUF path is not enough",
            "gguf-chat" => "llama.cpp, LM Studio, vLLM, or another local GGUF server",
            "omni-realtime-opt-in" => "vLLM-Omni, transformers serve, or another isolated realtime-capable OpenAI-compatible server",
            _ when multimodal => "vLLM, SGLang, TensorRT-LLM, OpenVINO Model Server, transformers serve, or a Foundry Local REST lane when the selected catalog model supports the needed modality",
            _ => "vLLM, SGLang, TensorRT-LLM, OpenVINO Model Server, transformers serve, Foundry Local, or another OpenAI-compatible chat-completions server",
        };

        List<string> startupHints = new();
        if (isGguf && !isEmbedding)
        {
            startupHints.Add("GGUF artifact provenance lane: record source repo or local path, immutable revision or file hash, base model relation, quantizer/build version, license metadata, and matching mmproj hash before promotion or redistribution.");
            startupHints.Add("llama.cpp local lane: --host 127.0.0.1 --port <port> -c <qualified-context> -np <player-slot-count> -b <measured> -ub <measured> -ngl <measured> --flash-attn on --metrics --no-webui.");
            startupHints.Add("llama.cpp prompt-cache lane: keep --cache-prompt enabled; tune --cache-reuse, -sps, and -cram before raising -np; verify /metrics or logs instead of assuming slot reuse.");
            startupHints.Add("llama.cpp state-cache canary lane: for SWA, hybrid, recurrent, or long-context GGUFs, test --swa-full / --slot-save-path / --cache-reuse on the exact server build before enabling host prompt-cache persistence.");
            startupHints.Add("llama.cpp idle-memory proof lane: use --sleep-idle-seconds only after wake latency, cold-after-wake cache behavior, and deterministic fallback behavior are recorded.");
            startupHints.Add("llama.cpp KV-memory proof lane: compare default f16 against -ctk q8_0 -ctv q8_0 before using longer contexts.");
            startupHints.Add("llama.cpp connector lane: use pal connect llamacpp to print the llama-server command, probe /health and /v1/models, and wire PalLLM:Inference with residency hints disabled.");
            startupHints.Add("llama.cpp schema lane: qualify response_format json_schema or json_object through the server's grammar conversion with a PalLLM schema digest receipt before using it for actions, world-state, or proof packets.");
            // Pass 346: 6 Ollama-specific startup hints removed (loopback,
            // context, residency, memory, concurrency, structured-output
            // lanes). llama.cpp/llama-server is the bundled default and
            // its hints are already covered above; LM Studio remains for
            // operators who want the desktop UX.
            startupHints.Add("LM Studio desktop lane: start lms server start --port 1234, load a stable model id with lms load --gpu <auto|max|ratio> --context-length <measured> --ttl <seconds>, and configure PalLLM BaseUrl as http://localhost:1234/v1/.");
            startupHints.Add("LM Studio residency lane: use PalLLM:Inference:ResidencyProvider=LmStudio so chat-completions requests carry the documented ttl field; keep auto-evict behavior visible in lms ps or server logs.");
            startupHints.Add("LM Studio structured/tool proof lane: qualify /v1/chat/completions response_format json_schema and tools with the exact loaded model id before using it for world-state or tool-heavy PalLLM routes.");
        }

        if (preferVllm && !isEmbedding)
        {
            startupHints.Add("vLLM artifact provenance lane: pin --revision, --code-revision, and --tokenizer-revision to immutable commits; keep --trust-remote-code false unless a reviewed code receipt exists.");
            startupHints.Add("--enable-prefix-caching");
            startupHints.Add("--prefix-caching-hash-algo sha256_cbor");
            startupHints.Add("--enable-chunked-prefill");
            startupHints.Add("vLLM Responses API proof lane: keep PalLLM's primary chat on /v1/chat/completions until /v1/responses streaming events, response ids, built-in tools, state cleanup, and deterministic fallback are qualified route by route.");
            startupHints.Add("vLLM single-player latency lane: use --performance-mode interactivity for player-facing companion, vision, or narration endpoints with small batches; keep balanced or throughput for shared/batch servers until PalLLM p95 proof says otherwise.");
            startupHints.Add("vLLM scheduler latency lane: set --max-num-batched-tokens <measured>, --max-num-seqs <player-slot-count>, --max-num-partial-prefills 2, --max-long-partial-prefills 1, and --long-prefill-token-threshold <measured> before sharing a server with long proof or docs-sync prompts.");
            startupHints.Add("vLLM disaggregated prefill/decode proof lane: treat NixlConnector, P2pNcclConnector, MooncakeConnector, MoRIIOConnector, or MultiConnector P/D topology as experimental tail-latency isolation; record prefill and decode endpoint ids, router/proxy config, redacted kv_transfer_config, and a monolithic baseline before promotion.");
            startupHints.Add("vLLM MoRIIO single-node P/D proof lane: launch separate kv_producer and kv_consumer instances through MoRIIOConnector, record VLLM_MORIIO_CONNECTOR_READ_MODE read/write mode, proxy/http/handshake/notify ports, prefix-caching-disabled baseline, and monolithic baseline before promotion.");
            startupHints.Add("vLLM foreground priority lane: launch with --scheduling-policy priority before setting PalLLM:Inference:RequestPriority; lower request priority values should win over background proof/docs traffic in a mixed replay.");
            startupHints.Add("vLLM reproducible-sampling lane: use --generation-config vllm when PalLLM's configured temperature/top_p must override model-repo generation_config.json defaults during qualification.");
            startupHints.Add("vLLM cache-residency proof lane: enable --kv-cache-metrics-sample only during qualification so KV block lifetime, idle-before-evict, and reuse-gap histograms can prove cache behavior without permanent overhead.");
            if (sparseVllmDboProofLane)
            {
                startupHints.Add("vLLM sparse-MoE DBO proof lane: test --enable-dbo plus --dbo-decode-token-threshold and --dbo-prefill-token-threshold only on multi-GPU data/expert-parallel servers with an explicit expert-parallel/all2all receipt, after the normal scheduler-cap replay passes.");
            }
            startupHints.Add("Optional memory-pressure lane after replay proof: --kv-cache-dtype fp8; compare nvfp4 only on servers and hardware that advertise it.");
            startupHints.Add("Optional idle VRAM reclaim only: VLLM_SERVER_DEV_MODE=1 --enable-sleep-mode");
            startupHints.Add("Optional local personality-adapter lane only: --enable-lora --max-loras 1 with validated pack adapters pinned at startup.");
            startupHints.Add("Qualify --fully-sharded-loras only for tensor-parallel, long-context, or high-rank adapter lanes; it trades startup time and memory for speed.");
            startupHints.Add("vLLM Mooncake Store proof lane: use MOONCAKE_CONFIG_PATH plus --kv-transfer-config '{\"kv_connector\":\"MooncakeStoreConnector\",\"kv_role\":\"kv_both\"}' only for local CPU/offload or multi-instance prefix-reuse replays after cold/warm PalLLM proof.");
            startupHints.Add("vLLM KV-event proof lane: enable KV cache events with a loopback ZMQ publisher only during qualification, then reduce BlockStored, BlockRemoved, and AllBlocksCleared batches to redacted event-count and block-hash receipts.");
            startupHints.Add("vLLM external KV cache proof lane: treat PegaKVConnector, PegaFlow, or another kv_connector_module_path cache daemon as a proof-only process-boundary experiment; record daemon pool size, SSD/RDMA mode, PEGAFLOW_HOST/PORT or equivalent endpoint, redacted kv_transfer_config, namespace/model identity, and a local prefix-cache baseline before promotion.");
            startupHints.Add("vLLM FlexKV offload proof lane: use --kv-transfer-config '{\"kv_connector\":\"FlexKVConnectorV1\",\"kv_role\":\"kv_both\"}' only after CPU/SSD/remote-store budget, async transfer health, cache namespace, and local-prefix baseline receipts are captured.");
        }

        if (preferVllm && supportsStructuredOutputs)
        {
            startupHints.Add("--structured-outputs-config.backend xgrammar");
            startupHints.Add("vLLM structured-output portability lane: prefer OpenAI response_format json_schema for portable PalLLM routes, and use prompt-level InferencePrompt.StructuredOutputs / structured_outputs for endpoint-specific choice, regex, JSON, grammar, or structural_tag proof shapes with their own backend receipts.");
        }

        if (preferSglang)
        {
            startupHints.Add("SGLang alternative lane: keep radix cache enabled; tune --mem-fraction-static, --max-running-requests, and --chunked-prefill-size against PalLLM p95 latency.");
            startupHints.Add("SGLang attention-backend proof lane: leave auto-selection on for the default route, or explicitly record --attention-backend, --prefill-attention-backend, --decode-attention-backend, --mm-attention-backend, page size, GPU architecture, FP8/FP4 KV support, and multimodal/spec-topk compatibility before pinning FlashInfer, FA3/FA4, Triton, TRTLLM MHA/MLA, AITER, or Intel XPU.");
            startupHints.Add("SGLang FP4/FP8 KV proof lane: use --kv-cache-dtype fp8_e4m3, fp8_e5m2, or fp4_e2m1 only with backend support, scale/quantization receipts, CUDA/PyTorch compatibility, and PalLLM strict JSON/tool-call replay; keep auto KV cache as the default baseline.");
            startupHints.Add("SGLang EAGLE-3/adaptive speculation proof lane: qualify --speculative-algorithm EAGLE3, STANDALONE, MTP, or NGRAM with draft-model revision, --speculative-num-steps, --speculative-eagle-topk, --speculative-num-draft-tokens, and acceptance/latency receipts; keep SGLANG_ENABLE_SPEC_V2 behind an explicit topk=1 proof.");
            startupHints.Add("SGLang HiCache proof lane: enable --enable-hierarchical-cache with measured --page-size, --hicache-ratio or --hicache-size, --hicache-io-backend, write policy, and optional storage backend only after cold/warm route replay.");
            startupHints.Add("SGLang proof lane only: --enable-deterministic-inference with flashinfer/fa3/triton attention backend for reproducible schema, replay, and judge tests.");
            startupHints.Add("SGLang qualification lane: --enable-metrics so cache_hit_rate, token_usage, TTFT, ITL, running requests, and queue depth are recorded before promotion.");
            startupHints.Add("SGLang replay lane: use request dump/replay or crash-dump replay locally during qualification, then keep dumps redacted or out of support bundles because they may contain player text.");
            startupHints.Add("SGLang Model Gateway lane: put multiple SGLang workers behind the gateway only after retries with jitter, worker-scoped circuit breakers, token-bucket queuing, background health checks, and cache-aware load monitoring are visible in metrics.");
        }

        if (preferSglang && supportsStructuredOutputs)
        {
            startupHints.Add("SGLang structured-output lane: use --grammar-backend xgrammar and qualify json_schema plus structural_tag prompts before promotion.");
        }

        if (preferTransformersServe)
        {
            startupHints.Add("Transformers serve provenance lane: use <repo_id>@<commit> or a hash-pinned local path, prefer safetensors/pre-quantized artifacts, and record model-card license/base_model metadata before promotion.");
            startupHints.Add("Transformers serve local lane: pip install transformers[serving], pin the model revision, then run transformers serve <repo_id[@revision]> --continuous-batching --dtype bfloat16.");
            startupHints.Add("Transformers serve warmup lane: call /load_model with the exact repo_id@revision and wait for the terminal ready event before enabling live PalLLM inference.");
            startupHints.Add("Transformers serve Responses API lane: treat /v1/responses as experimental proof material; keep PalLLM appsettings on /v1/chat/completions until streaming event shape, state cleanup, and fallback behavior pass replay.");
        }

        if (preferFoundryLocal)
        {
            startupHints.Add("Foundry Local single-user lane: install with winget, run foundry model list --filter task=chat-completion, then foundry model run <alias>; get the dynamic endpoint with foundry service status and configure PalLLM BaseUrl as <endpoint>/v1/.");
            startupHints.Add("Foundry Local execution-provider lane: prefer catalog aliases for hardware-matched ONNX variants, then record the selected executionProvider from /foundry/list or foundry model info before promotion.");
        }

        if (preferOpenVino)
        {
            startupHints.Add("OpenVINO Model Server lane: run ovms.exe or openvino/model_server:2026.1 with --task text_generation --target_device GPU/CPU/NPU and configure PalLLM BaseUrl as http://localhost:<port>/v3/.");
            startupHints.Add("OpenVINO GenAI edge proof lane: use OpenVINO INT4 models first, record the selected target_device, and compare CPU, GPU, and NPU before changing a player default.");
            startupHints.Add("OpenVINO NPU tuning lane: when using a direct GenAI NPU pipeline behind the server or adapter, qualify PREFILL_HINT and GENERATE_HINT against PalLLM p95 latency and first-token behavior.");
        }

        if (preferTensorRtLlm)
        {
            startupHints.Add("TensorRT-LLM lane: run trtllm-serve serve <model> --host localhost --port <port> --backend pytorch --enable_chunked_prefill --served_model_name <id>, then configure PalLLM BaseUrl as http://localhost:<port>/v1/.");
            startupHints.Add("TensorRT-LLM proof config: use --config <yaml> for max_batch_size, max_num_tokens, enable_iter_perf_stats, quantization, KV cache, and speculative-decoding options instead of baking one-off flags into PalLLM.");
            startupHints.Add("TensorRT-LLM parser lane: set --tool_call_parser auto, qwen3, or qwen3_coder only after strict PalLLM JSON/tool-call replay passes for that tokenizer.");
        }

        if (preferTransformersServe && supportsAudioInput)
        {
            startupHints.Add("Transformers serve audio lane: use OpenAI input_audio content for /v1/chat/completions and /v1/audio/transcriptions for ASR proof; keep clips local, bounded, and revision-pinned.");
        }

        if (preferOpenVino && supportsVision)
        {
            startupHints.Add("OpenVINO VLM lane: use a VLM model/pipeline with explicit local media allowlists and /v3/chat/completions proof before routing Palworld screenshots to it.");
        }

        if (preferOpenVino && supportsAudioInput)
        {
            startupHints.Add("OpenVINO ASR proof lane: keep /v3/audio/transcriptions or speech2text deployments separate from normal chat until privacy, latency, and cascaded-ASR fallback proof exists.");
        }

        if (preferTensorRtLlm && multimodal)
        {
            startupHints.Add("TensorRT-LLM multimodal lane: use Chat API only, pass local image_url/video_url/audio_url or trusted image_embeds, and disable block reuse in config when the selected multimodal model requires it.");
        }

        if (preferFoundryLocal && supportsAudioInput)
        {
            startupHints.Add("Foundry Local ASR proof lane: load a Whisper alias, call /v1/audio/transcriptions with a local file, and keep player speech on typed or cascaded ASR fallback until privacy and latency proof lands.");
        }

        if (preferVllm && isQwen36)
        {
            startupHints.Add("--reasoning-parser qwen3");
            startupHints.Add("Optional Qwen3.6 MTP lane after qualification: --speculative-config '{\"method\":\"qwen3_next_mtp\",\"num_speculative_tokens\":2}'");
            startupHints.Add("Qwen3.6 latency-focused MTP-1 proof lane: compare --speculative-config '{\"method\":\"mtp\",\"num_speculative_tokens\":1}' with --no-enable-prefix-caching against prefix-cached no-spec and n-gram baselines before promotion.");
            startupHints.Add("For Qwen3.6 text-only service, add --language-model-only to skip the vision encoder and free KV cache; keep a separate multimodal lane for screenshots.");
            startupHints.Add("Qwen3.6 hybrid-GDN proof lane: on SGLang >=0.5.10, or another runtime only after it exposes matching hybrid-state knobs, record the Gated DeltaNet/Mamba scheduler strategy, page size, context length, attention backend, --mem-fraction-static, and multimodal feature transport before changing player defaults.");
            startupHints.Add("Qwen3.6 context-identity lane: record served model id, native 262,144-token context, any 1,010,000-token extension flags such as YaRN/rope overrides, runtime max_model_len/context cap, KV/state memory, and route budget before long-context promotion.");
        }

        if (isQwenOmni)
        {
            startupHints.Add("Qwen Omni lane: qualify vLLM-Omni with vllm serve <model> --omni --port <port>, or transformers serve for chat-completions proof; keep the Talker/audio-output stage isolated from the text lane.");
            startupHints.Add("Qwen Omni realtime lane: for vLLM-Omni /v1/realtime proof, record a deploy config with async_chunk disabled because current Qwen3-Omni serving docs mark realtime unsupported while async_chunk is enabled.");
            startupHints.Add("Qwen Omni streaming-video lane: treat /v1/video/chat/stream as a separate WebSocket proof route; record frame cadence, optional PCM16 audio chunks, chunk-size bounds, reconnect/stall behavior, and fallback to still-image/world-state before promotion.");
            startupHints.Add("vLLM-Omni video-generation lane: /v1/videos and /v1/videos/sync are async diffusion-job proof surfaces, not PalLLM companion chat, screenshot understanding, or live Palworld HUD routes.");
        }

        if (isQwenOmni && supportsAudioOutput)
        {
            startupHints.Add("Qwen Omni voice lane: request modalities [\"text\",\"audio\"] only on an isolated proof profile, configure the speaker explicitly, and keep a text mirror for PalLLM fallback.");
        }

        if (isGemma3n)
        {
            startupHints.Add("Gemma 3n edge lane: qualify E2B/E4B with PLE caching and conditional parameter loading; bypass vision/audio parameters on text-only companion lanes to reduce memory and latency.");
        }

        if (preferVllm && isGemma4)
        {
            startupHints.Add("Optional Gemma 4 MTP drafter lane after matching assistant/drafter weights are installed: --speculative-config '{\"method\":\"mtp\",\"model\":\"<qualified-gemma4-assistant-checkpoint>\",\"num_speculative_tokens\":<measured>}' rather than a generic draft_model config.");
        }

        if (isGguf && supportsModelNativeMtp)
        {
            startupHints.Add("llama.cpp draft-MTP proof lane: qualify --spec-type draft-mtp with measured --spec-draft-n-min / --spec-draft-n-max on text-only Qwen3.6 or Gemma 4 replay before any player-facing use.");
        }

        if (supportsModelNativeMtp && multimodal)
        {
            startupHints.Add("MTP/multimodal split-lane guard: keep text MTP and vision/audio/video profiles on separate server processes or ports until the exact runtime proves shared encoder batches do not corrupt slot, KV, or scheduler state.");
        }

        if (preferVllm && multimodal)
        {
            startupHints.Add("--mm-processor-cache-gb 4");
            startupHints.Add("--mm-processor-cache-type lru");
            startupHints.Add("vLLM remote-media safety lane: prefer PalLLM base64 data URLs; if media URLs are enabled, set --allowed-media-domains <explicit-hosts> and VLLM_MEDIA_URL_ALLOW_REDIRECTS=0, then reject localhost/private-network redirect probes before promotion.");
            startupHints.Add("--ec-transfer-config <qualified-LMCacheECConnector-config>");
            startupHints.Add("Trusted precomputed-media lane only: --enable-mm-embeds");
            startupHints.Add("Use --disable-chunked-mm-input when chunked prefill is on and mixed text-plus-media prompts are replayed for Palworld proof.");
            startupHints.Add("Keep experimental --enable-tower-connector-lora off except staging-only Qwen-VL-style multimodal adapter proof.");
        }

        if (supportsVision)
        {
            if (isGguf)
            {
                startupHints.Add("--mmproj <matching-mmproj.gguf>");
            }

            if (preferVllm)
            {
                startupHints.Add("--limit-mm-per-prompt.image 1");
            }
        }

        if (supportsVideo)
        {
            startupHints.Add("--limit-mm-per-prompt.video 1");
            startupHints.Add("--media-io-kwargs '{\"video\":{\"num_frames\":8}}'");
        }

        if (supportsAudioInput)
        {
            startupHints.Add("Audio ingress normalization lane: downmix and resample player clips to mono 16 kHz, cap clips at 30 seconds, and reject oversized audio before it reaches the model server.");

            if (isGemmaAudio)
            {
                startupHints.Add($"{gemmaAudioFamily} audio-token budget lane: account for {gemmaAudioTokenRate} audio tokens per second, normalized mono 16 kHz float32 input, 30-second default clips, and about {gemmaThirtySecondTokenCost} audio tokens before text/output headroom.");
            }

            if (isGguf)
            {
                startupHints.Add("--mmproj <matching-audio-mmproj.gguf>");
            }

            if (preferVllm)
            {
                startupHints.Add("--limit-mm-per-prompt.audio 1");
            }
        }

        if (supportsAudioOutput)
        {
            startupHints.Add("--enable-audio-out");
        }

        if (supportsSpeculativeDecoding && !multimodal)
        {
            if (isGguf)
            {
                startupHints.Add("llama.cpp speculation proof lane: --spec-type ngram-simple --spec-draft-n-max 64 for repetitive PalLLM text turns after parser proof.");
                startupHints.Add("llama.cpp speculation alternative: --spec-type ngram-mod --spec-ngram-mod-n-match 24 --spec-ngram-mod-n-min 48 --spec-ngram-mod-n-max 64 when repeated text/code patterns dominate.");
            }
            else
            {
                startupHints.Add("--speculative-config '{\"method\":\"ngram\",\"num_speculative_tokens\":4,\"prompt_lookup_min\":2,\"prompt_lookup_max\":5}'");
                startupHints.Add("--speculative-config <qualified-draft-or-eagle-config>");
            }
        }

        List<string> requestHints =
        [
            "Keep PalLLM text chat on /v1/chat/completions so deterministic fallback remains the hot-path backstop.",
            "Stamp every promotion replay with the PalLLM operation name and latency budget; /api/chat, /api/vision/describe, /api/vision/world-state, audio/ASR, and long proof/docs lanes are separate evidence.",
            "Do not promote modalities, tool calling, speculation, or realtime audio from a family name alone; capture a primary-source capability receipt and a local runtime canary for the exact model artifact.",
            "Before any downloaded model, quant, adapter, mmproj, or drafter becomes a PalLLM default, record the source URL or local path, immutable revision or file hash, model-card license metadata, base-model/adapter relation, runtime version, and trust-remote-code status.",
            "Use PalLLM:Inference:Seed only for endpoint-proven replay lanes; record the seed, served model id, runtime version, and any system_fingerprint-equivalent evidence beside the replay result.",
            "Use PalLLM:Inference:TokenBudgetField=max_completion_tokens only for endpoint-proven reasoning lanes that require the newer OpenAI-compatible token-budget field; record accepted request shape, visible/reasoning token usage when exposed, p95 latency, and fallback counters before promotion.",
            "Use PalLLM:Inference:ThinkingTokenBudget only for endpoint-proven vLLM reasoning-parser lanes; record accepted thinking_token_budget request shape, visible/reasoning token usage, p95 latency, and fallback counters before promotion.",
            "Use PalLLM:Inference:FrequencyPenalty only for endpoint-proven repetition-control lanes; record repeated-phrase rate, generated tokens, latency, and fallback counters before making it a player-facing default.",
            "Keep PalLLM:Inference:Temperature, TopP, and PresencePenalty inside their startup-validated OpenAI-style ranges before testing any endpoint-specific sampler changes.",
            "Use PalLLM:Inference:TopK, MinP, and RepetitionPenalty only for endpoint-proven local-sampler lanes; record accepted request shape, style/loop deltas, token count, latency, and fallback counters before promotion.",
            "Use PalLLM:Inference:StopSequences only for endpoint-proven delimiter lanes; record before/after token usage and clipped-text review before treating stop as a latency optimization.",
            "Use InferencePrompt.Prediction only for endpoint-proven predicted-output replay lanes, such as stable proof or docs regeneration; ordinary companion chat omits prediction so strict local endpoints stay portable.",
            "Use InferencePrompt.Logprobs / TopLogprobs only for endpoint-proven confidence or evaluator canaries; ordinary companion chat omits logprobs so strict local endpoints stay portable, and PalLLM preserves returned choice logprobs as a raw receipt.",
        ];
        if (supportsStructuredOutputs)
        {
            requestHints.Add("Use InferencePrompt.ResponseFormat to forward response_format json_schema on portable route-specific text proof lanes; use InferencePrompt.StructuredOutputs only for vLLM-specific structured_outputs canaries; use the vision schema hook for world-state, and keep ordinary companion chat field-free until the endpoint proves structured-output support.");
            requestHints.Add("Every schema-bearing replay should carry a schema digest, schema name, PalLLM route class, served model id, request shape, grammar/backend id, temperature, parse result, schema-validation result, token usage, p95 latency, and fallback counter delta.");
        }

        if (supportsToolCalls)
        {
            requestHints.Add("Use prompt-level InferencePrompt.Tools and InferencePrompt.ToolChoice only on strict route canaries; PalLLM forwards tools/tool_choice verbatim and preserves returned tool_calls as a receipt, while ordinary companion chat omits both fields.");
            requestHints.Add("For vLLM tool-call proof, set PalLLM:Inference:ParallelToolCalls=false so PalLLM forwards parallel_tool_calls=false on strict directive/action routes until a multi-call fan-out contract is intentionally added and tested.");
        }

        if (isGguf && !isEmbedding)
        {
            requestHints.Add("On llama.cpp, keep cache_prompt enabled for stable PalLLM prefixes; do not assume reuse across chat-template, context-size, adapter, model, or slot changes.");
            requestHints.Add("Use PalLLM:Inference:LlamaCppCachePrompt, LlamaCppSlotId, and LlamaCppCacheReuseTokens only on endpoint-proven llama-server canaries; ordinary cross-runtime chat omits cache_prompt, id_slot, and n_cache_reuse.");
            requestHints.Add("For llama.cpp host prompt-cache persistence, include the model family, tokenizer metadata, chat template, context size, adapter id, --swa-full state, and server commit in the PalLLM replay receipt.");
            if (supportsModelNativeMtp)
            {
                requestHints.Add("For llama.cpp draft-MTP proof, record the --spec-type draft-mtp command line, --spec-draft-* depths, tokenizer/chat-template identity, served model hash, server commit, accepted/generated tokens, TTFT, ITL, parser result, and fallback counters.");
            }

            // Pass 346: Ollama-native proof-call hint removed alongside the
            // rest of the Ollama back-compat path. llama-server's /metrics
            // endpoint provides the equivalent timing receipts (see the
            // llama.cpp connector lane above).
            requestHints.Add("On LM Studio, pass a stable loaded-model identifier and keep PalLLM's ttl residency hint enabled only for local /v1/chat/completions lanes that have auto-evict behavior under observation.");
        }

        if (supportsSpeculativeDecoding)
        {
            requestHints.Add("Qualify n-gram or suffix speculation first for repetitive PalLLM text turns; use draft-model or EAGLE configs only after repeated-run schema/tool-call tests pass.");
        }

        if (supportsModelNativeMtp)
        {
            requestHints.Add("Treat Qwen3.6 or Gemma 4 MTP as a separate model-native speculation mode; keep strict JSON, tool-call, judge, and save-replay routes no-spec until route-specific proof exists.");
            if (isQwen36)
            {
                requestHints.Add("For Qwen3.6 low-concurrency latency proof, run MTP-1 with prefix caching disabled and keep the normal prefix-cache lane separate for shared or prompt-heavy traffic.");
            }
            else if (isGemma4)
            {
                requestHints.Add("For Gemma 4 MTP benchmarking, treat assistant checkpoints as Gemma 4 MTP speculators rather than generic draft models; disable prefix caching for the measurement pass, then replay the chosen drafter profile with normal cache settings before promotion.");
            }

            if (multimodal)
            {
                requestHints.Add("Treat text-only MTP proof as text-only proof; screenshot, video, and audio routes need their own no-spec, n-gram, and model-native speculation replay before promotion.");
                requestHints.Add("Do not send image_url, video_url, input_audio, or audio_url content parts to a text-only MTP endpoint; route media through a separate no-spec multimodal profile unless the same server build has negative canaries for loop, OOM, stall, and parser behavior.");
            }
        }

        if (isGemma3n)
        {
            requestHints.Add("For Gemma 3n, request only the modalities needed for the turn; text-only companion turns should not load audio or vision parameters just because the model can.");
        }

        if (isQwen36)
        {
            requestHints.Add("Qwen3.6 has a 262K default context on official cards; keep ordinary companion turns short, and reserve 128K+ contexts for proof, docs-sync, or deliberate review lanes that can afford the KV cache.");
            requestHints.Add("Do not copy a 1,010,000-token extension, hosted catalog limit, or GGUF context setting across Qwen3.6 lanes; the served model id, runtime context cap, extension flags, and route token budget must be in the promotion receipt.");
            requestHints.Add("Qwen3.6 thinking-preservation, Gated DeltaNet, and Mamba scheduler settings are proof-lane controls; keep low-latency companion turns direct unless PalLLM replay proves the extra reasoning or state budget helps.");
        }

        if (preferVllm && !isEmbedding)
        {
            requestHints.Add("If PalLLM:Inference:PrefixCacheSalt is set, forward it as vLLM cache_salt so cache reuse stays inside one player/save/profile trust domain.");
            requestHints.Add("If PalLLM:Inference:PromptCacheKey or PromptCacheRetention is set, forward prompt_cache_key / prompt_cache_retention only to hosted endpoints that prove support; keep keys non-secret and stable per Palworld save/profile/task family.");
            requestHints.Add("If PalLLM:Inference:RequestPriority is set, forward it as vLLM priority only on endpoints launched with priority scheduling; lower values are more urgent and non-zero values can fail on FCFS-only servers.");
            requestHints.Add("If PalLLM:Inference:ServiceTier is set, forward it as OpenAI-compatible service_tier only on endpoints that accept service tiers; prove priority/scale against player-facing queue/TTFT and flex only against background proof/docs lanes before promotion.");
            requestHints.Add("If PalLLM:Inference:Verbosity is set, forward it only on endpoints that accept verbosity; prove low trims generated tokens without clipping useful player guidance, and keep high for non-live proof/review lanes.");
            requestHints.Add("If PalLLM:Inference:SafetyIdentifier is set, forward safety_identifier only as a stable pseudonymous hosted-lane hash; never use player names, save paths, account ids, emails, or secrets.");
            requestHints.Add("If PalLLM:Inference:StoreCompletions is set, forward store only on hosted endpoints that prove support; keep ordinary Palworld companion chat field-free, and prefer explicit false for retention-posture canaries.");
            requestHints.Add("If PalLLM:Inference:RequestMetadata has entries, forward metadata only as bounded low-cardinality proof labels on hosted canaries; never include prompt text, player identity, save paths, secrets, raw game state, or metric-label values.");
            requestHints.Add("If PalLLM:Inference:ClientRequestIdHeader is set, forward only PalLLM's bounded chat/proof request id as x-client-request-id or x-request-id for support correlation; keep prompt text, save paths, and player identity out of headers and metrics.");
            requestHints.Add("Do not swap PalLLM's live companion lane to /v1/responses because it is newer or stateful; qualify response.created, response.output_text.delta, response.completed, response-id cleanup, tool events, usage receipts, p95 latency, and fallback counters first.");
            requestHints.Add("If PalLLM:Inference:TokenBudgetField is max_completion_tokens, omit max_tokens and forward the same PalLLM route budget as max_completion_tokens; keep max_tokens for SGLang/GGUF lanes until the exact endpoint proves the newer field.");
            requestHints.Add("If PalLLM:Inference:ThinkingTokenBudget is set, forward thinking_token_budget only to vLLM reasoning-parser lanes that prove the parameter is enforced; keep ordinary companion chat omitted or thinking-disabled for fastest turns.");
            requestHints.Add("If PalLLM:Inference:TopK, MinP, or RepetitionPenalty are set, treat them as vLLM extra sampler parameters and keep --generation-config vllm in the proof receipt so model-repo defaults do not silently override the replay.");
            requestHints.Add("If a pack supplies a local LoRA adapter, choose the adapter from validated pack metadata and operator config only; never let player text select an adapter path or id.");
            requestHints.Add("Keep long proof, docs-sync, or deliberate-review prompts on a separate profile unless vLLM scheduler caps prove short companion turns still win the queue.");
            requestHints.Add("Treat external KV cache daemons as volatile serving infrastructure, not PalLLM memory; a cache hit can speed a route only after the same route proves privacy, fallback, and rollback behavior.");
            if (sparseVllmDboProofLane)
            {
                requestHints.Add("Treat Dual Batch Overlap as a throughput/overlap proof lane for sparse-MoE worker servers, not a default one-player latency switch; compare it against scheduler caps with the same short-turn plus long-proof replay.");
            }
        }

        if (preferSglang)
        {
            requestHints.Add("On SGLang, keep ordinary companion turns on the OpenAI-compatible chat API; reserve structural_tag, EBNF, or native sampling params such as top_k, min_p, and repetition_penalty for proof and tool lanes with parser coverage.");
            requestHints.Add("When SGLang Model Gateway fronts multiple workers, keep the PalLLM model id stable and require request-id propagation so cache-aware routing and fallback events can be correlated.");
            requestHints.Add("On SGLang speculative lanes, do not let auto-tuned topk or adaptive depth cross route boundaries; record algorithm, draft artifact, topk, draft-token cap, acceptance rate, and disable trigger per PalLLM route.");
        }

        if (preferTransformersServe)
        {
            requestHints.Add("On transformers serve, pass the model as the positional serve argument for reproducible PalLLM runs and keep the configured model id aligned with the pinned repo_id@revision used by /load_model.");
            requestHints.Add("On transformers serve, keep /v1/responses behind a proof route until the experimental endpoint's stateful stream and tool-event shape match PalLLM's route contracts.");
            requestHints.Add("Use /v1/audio/transcriptions as a separate ASR proof lane; do not route player speech into companion chat until text fallback and privacy behavior are measured.");
        }

        if (preferFoundryLocal)
        {
            requestHints.Add("On Foundry Local, verify the configured model alias or loaded model id appears in /openai/models before relying on tier orchestration; do not hardcode the dynamic service port.");
            requestHints.Add("Use Foundry Local ep or ttl request overrides only in proof lanes, then compare provider choice, residency, p95 latency, and fallback behavior before promotion.");
        }

        if (preferOpenVino)
        {
            requestHints.Add("On OpenVINO Model Server, set PalLLM BaseUrl to http://localhost:<port>/v3/ so chat/completions and models resolve through the OpenAI-compatible GenAI surface.");
            requestHints.Add("Keep OpenVINO target_device, tool_parser, and chat_template_kwargs changes proof-lane scoped until strict JSON, tool-call, and fallback behavior are measured.");
        }

        if (preferTensorRtLlm)
        {
            requestHints.Add("On TensorRT-LLM, keep PalLLM on /v1/chat/completions and verify /v1/models plus /health before enabling tier orchestration for the served_model_name.");
            requestHints.Add("Use the TensorRT-LLM /metrics endpoint as proof evidence for GPU memory, inflight batching, KV-cache stats, and active requests; poll shortly after replay turns because iteration records are transient.");
        }

        if (supportsAudioInput)
        {
            requestHints.Add("For native audio-in, prefer OpenAI input_audio with base64 wav or mp3; treat audio_url and video_url extensions as operator-proof only because they can fetch remote media.");
            requestHints.Add("Keep ordinary player speech on cascaded ASR-to-text until typed-text fallback, privacy retention, and latency evidence are recorded.");
            if (isGemmaAudio)
            {
                requestHints.Add($"For {gemmaAudioFamily} audio-in, record the normalized duration and {gemmaAudioTokenRate} audio-token-per-second estimate; a 30-second clip spends about {gemmaThirtySecondTokenCost} audio tokens before text, system, and output headroom.");
            }
        }

        if (multimodal)
        {
            requestHints.Add("Use prompt-level InferencePrompt.UserContent only on route-specific multimodal input canaries; ordinary companion chat keeps a plain string user message, while proof lanes may forward text/image_url/video_url/input_audio/audio_url content parts after media-admission checks.");
        }

        if (isQwenOmni)
        {
            requestHints.Add("For Qwen Omni, keep text plus audio output on an isolated voice profile with a text mirror; do not let realtime audio replace the normal ChatResponse contract.");
            requestHints.Add("For Qwen Omni streaming video, keep /v1/video/chat/stream off the normal companion hot path; use it only for bounded Palworld proof clips with a still-image or world-state fallback.");
            requestHints.Add("For vLLM-Omni /v1/videos jobs, keep prompts scoped to operator proof or release walkthrough material; never let generated video jobs block chat, vision describe, or bridge proof loops.");
        }

        if (supportsAudioOutput)
        {
            requestHints.Add("Use prompt-level InferencePrompt.Modalities and InferencePrompt.Audio only on isolated audio-output canaries; PalLLM forwards modalities/audio, preserves returned message.audio on InferenceResult.AudioJson, and keeps ordinary companion chat field-free.");
        }

        if (multimodal)
        {
            requestHints.Add("Send local data URLs or file paths from an explicitly allowed runtime directory; remote URLs are opt-in only.");
            requestHints.Add("Attach stable OpenAI-compatible media uuid fields for repeated screenshots, proof replays, or audio clips.");
            requestHints.Add("Never omit media bytes on a uuid-only request until the same server process has proven that uuid is warm.");
            requestHints.Add("Use image_embeds/audio_embeds/video embeddings only from a PalLLM-owned preprocessor with exact model-family shape metadata; ordinary player media stays on bytes plus uuid.");
        }

        List<string> cacheHints =
        [
            "Keep the PalLLM system prompt, pack summary, and world-state prefix stable for prefix-cache hits.",
            "Treat every local model cache as a staged artifact store: cache hits are useful only after the cached files have a provenance receipt with license, revision/hash, weight format, and trust boundary recorded.",
        ];
        if (supportsStructuredOutputs)
        {
            cacheHints.Add("Keep schema-bearing replay, cache, and coalescing identities keyed by route, model id, request shape, and canonical schema digest; never reuse a cached structured answer across schema revisions.");
        }
        if (multimodal)
        {
            cacheHints.Add("Use content-hash media ids so identical screenshots avoid repeated multimodal preprocessing.");
            cacheHints.Add("Budget multimodal processor cache memory per API process and engine core before increasing media counts.");
            cacheHints.Add("For repeated screenshot, video, or audio proof loops on vLLM v1, qualify multimodal encoder cache separately from text KV cache; LMCache EC needs an explicit CPU or disk budget and no implicit disk persistence.");
            cacheHints.Add("Treat --enable-mm-embeds plus --limit-mm-per-prompt.<modality> 0 as a separate trusted embedding-only lane; it can save encoder memory only after PalLLM owns the embedding shape and fallback path.");
            cacheHints.Add("Keep speculative-decoding cache claims modality-isolated: text prefix/KV wins do not prove media UUID, encoder-cache, or audio-token latency wins.");
            cacheHints.Add("For multimodal router pools, keep media-hash/KV-overlap receipts separate from encoder-cache receipts; repeated Palworld screenshot routing proof is not interchangeable with text-prefix proof.");
            if (supportsModelNativeMtp)
            {
                cacheHints.Add("Keep text MTP KV cache and multimodal encoder/KV cache in separate process or port cache namespaces until same-process MTP plus media replay is proven stable.");
            }
        }

        if (supportsAudioInput)
        {
            cacheHints.Add("Hash normalized 16 kHz mono audio bytes after trimming policy is applied; do not reuse raw clip hashes across different resampling or silence-trimming settings.");
            if (isGemma4)
            {
                cacheHints.Add("Store the Gemma audio normalized duration and audio-token cost estimate beside the clip hash so cache hits cannot hide budget overruns.");
            }
        }

        if (isGemma3n)
        {
            cacheHints.Add("For Gemma 3n, measure PLE cache hit behavior and conditional parameter loading separately from text KV cache before claiming edge-memory wins.");
        }

        if (isGguf && !isEmbedding)
        {
            cacheHints.Add("On llama.cpp, prompt caching is per server and slot; measure second-turn latency, --cache-reuse hits, -cram pressure, slot similarity, and active KV memory before raising -np.");
            cacheHints.Add("Treat llama.cpp --slot-save-path as local operator-managed persistence; keep saved slot state under the PalLLM runtime root and redact or exclude it from support bundles.");
            cacheHints.Add("Treat llama.cpp host prompt-cache restore as a per-model-family capability, not a RAM-only toggle; SWA, hybrid, recurrent, multimodal, and long-context GGUFs need state-cache canary receipts before promotion.");
            // Pass 346: 2 Ollama-specific cache hints removed
            // (OLLAMA_KV_CACHE_TYPE policy, OLLAMA_NUM_PARALLEL +
            // OLLAMA_CONTEXT_LENGTH verification). llama.cpp's -ctk/-ctv
            // and --cache-reuse hints above replace the equivalent
            // guidance.
            cacheHints.Add("For LM Studio, record loaded-model TTL, auto-evict, context length, and GPU offload from lms ps or server logs beside PalLLM p50/p95 so idle unloads are not mistaken for model quality regressions.");
        }

        if (preferVllm && !isEmbedding)
        {
            cacheHints.Add("Use sha256_cbor prefix-cache hashing when deterministic cross-version or cross-language cache identity matters; avoid non-cryptographic hash modes on shared endpoints.");
            cacheHints.Add("Treat vLLM --performance-mode interactivity as the default candidate for one-player PalLLM latency lanes, but compare it against balanced and throughput when the same server also handles batch proof or multi-client traffic.");
            cacheHints.Add("Treat --max-num-batched-tokens as a latency and KV-headroom budget, not a throughput trophy; right-size it with --max-num-seqs before claiming a PalLLM cache or batching win.");
            cacheHints.Add("Treat FP8 or NVFP4 KV-cache compression as a memory/context tradeoff, not a free default; promote only after quality, parse stability, TTFT, ITL, and KV-cache utilization beat auto KV cache on PalLLM replays.");
            cacheHints.Add("For dual-GPU or workstation tail-latency tuning, evaluate experimental vLLM disaggregated prefill only after ordinary chunked prefill and prefix caching are measured; MoRIIO read/write modes can isolate ITL but may add TTFT, so they are not throughput or first-token defaults.");
            cacheHints.Add("When multiple model replicas sit behind a router, use sticky or KV cache-aware routing and prove higher cache-hit rate than round-robin before scaling the PalLLM lane horizontally.");
            cacheHints.Add("Treat LMCacheConnectorV1, FlexKVConnectorV1, or KV offloading connectors as advanced multi-instance topology: keep PalLLM's default companion lane on local prefix caching until cold/warm TTFT and failure behavior are measured.");
            cacheHints.Add("For MoRIIOConnector proof, keep prefix-cache-disabled and normal prefix-cache baselines separate so a P/D win is not mistaken for ordinary prefix reuse.");
            cacheHints.Add("Treat MooncakeStoreConnector as an advanced vLLM distributed KV-cache pool: prove multi-turn proof/docs prefix reuse, cache-hit rate, cold/warm TTFT, E2E latency, store health, and local-prefix rollback before any live companion use.");
            cacheHints.Add("Treat PegaFlow-style external KV cache services as process-boundary proof lanes: record daemon health, pinned-host/SSD/RDMA budget, cache namespace identity, cold/warm route latency, worker-restart reuse, daemon-stop rollback, and redacted evidence before using the cache for live companion chat.");
            cacheHints.Add("If vLLM sleep mode is enabled for idle VRAM reclaim, treat wake-up as a cold-cache boundary until prefix, KV, and media-cache behavior are remeasured.");
            cacheHints.Add("Measure prefix-cache behavior per base-model plus LoRA-adapter id; adapter ids are part of the cache identity and must not reuse cached prefixes across personality packs.");
            cacheHints.Add("Use sampled vLLM KV-block residency metrics to catch stranded cache: long idle-before-evict or reuse gaps on proof/docs prompts should not evict the live companion prefix.");
            cacheHints.Add("Keep route/cache proof indexes separate for companion chat, screenshot/world-state, audio/ASR, and proof/docs prompts; a hot docs prefix must not evict the live-player prefix without rollback evidence.");
            cacheHints.Add("Treat vLLM KV events as local redacted proof only: BlockStored extra_keys can encode media identifiers, LoRA names, cache_salt, or prompt-embedding hashes, so archive event classes, counts, block sizes, group metadata, and hashes rather than raw token_ids or extra_keys.");
            if (sparseVllmDboProofLane)
            {
                cacheHints.Add("Keep DBO evidence separate from prefix/KV cache evidence: microbatch overlap can change TTFT/ITL even when cache-hit rates are unchanged.");
            }
        }

        if (preferVllm && isQwen36)
        {
            cacheHints.Add("Keep Qwen3.6 MTP-1 latency proof separate from the prefix-cache profile: --no-enable-prefix-caching can improve low-concurrency TPOT evidence, while prefix caching may still win prompt-heavy shared lanes.");
            cacheHints.Add("Treat Qwen3.6 Gated DeltaNet/Mamba state as separate from transformer KV cache; record scheduler strategy, page size, state memory, and cold/warm route latency before promoting hybrid-cache changes.");
            cacheHints.Add("Keep Qwen3.6 native 262,144-token, 1,010,000-token extended, hosted, and reduced-context GGUF profiles in separate cache namespaces; cross-profile cache hits are not promotion evidence.");
        }

        if (preferSglang)
        {
            cacheHints.Add("On SGLang, keep radix cache enabled for normal PalLLM turns; use cache namespaces or route isolation for pack/adapters/trust domains where the server exposes them.");
            cacheHints.Add("Use SGLang --disable-radix-cache only for A/B proof lanes that deliberately compare cache-on versus cache-off behavior.");
            cacheHints.Add("Treat SGLang attention backend, page size, KV dtype, and draft backend as cache identity; FA4, FlashInfer, TRTLLM, AITER, or Intel XPU proof cannot reuse HiCache/radix evidence from another backend.");
            cacheHints.Add("Keep SGLang FP4 KV cache evidence separate from FP8 and auto KV cache; exact parse, tool-call, and screenshot/world-state behavior must pass before memory savings count.");
            cacheHints.Add("Treat SGLang HiCache as hierarchical KV offload proof, not a default player cache: record L1 GPU, L2 host, optional L3 storage backend, prefetch policy, write policy, page size, cache-hit rate, cold/warm TTFT, and fallback counters per PalLLM route.");
            cacheHints.Add("Record SGLang cache_hit_rate, token_usage, TTFT, ITL, running requests, and queued requests beside PalLLM fallback counters before changing live serving policy.");
            cacheHints.Add("Keep SGLang request dumps local and short-lived; use sanitized replay templates for handoff instead of shipping raw pickle dumps that may contain player or pack text.");
            cacheHints.Add("For SGLang Model Gateway pools, prefer cache-aware worker selection over round-robin only after gateway metrics show higher cache hit rate without worse p95 companion latency.");
        }

        if (preferTransformersServe)
        {
            cacheHints.Add("For transformers serve continuous batching, record PalLLM p50/p95 plus OpenTelemetry traces before raising batch pressure; moderate-load throughput must not hurt the one-player companion lane.");
            cacheHints.Add("Treat the Hugging Face local cache as a staged model store: pin revision SHAs for reproducible promotion evidence and avoid implicit latest-main drift.");
        }

        if (preferFoundryLocal)
        {
            cacheHints.Add("Treat the Foundry model cache and Windows ML execution-provider cache as first-use local downloads; record cold download/load, warm load, and offline replay separately.");
        }

        if (preferOpenVino)
        {
            cacheHints.Add("Treat OpenVINO model pull, graph compilation, and model-cache warmup as separate cold-start costs; record /v3/models readiness, first chat latency, warm p50/p95, and fallback behavior.");
            cacheHints.Add("For OpenVINO NPU lanes, record cache/AoT compile behavior separately from text KV cache before claiming edge-hardware wins.");
        }

        if (preferTensorRtLlm)
        {
            cacheHints.Add("For TensorRT-LLM, treat paged KV cache, inflight batching, and block reuse as server-local proof lanes; capture kvCacheStats cacheHitRate, used/free blocks, and TTFT before claiming cache benefit.");
            cacheHints.Add("For TensorRT-LLM disaggregated serving or Dynamo, prove KV transfer and request migration behavior on PalLLM replays before using the topology for live companion turns.");
        }

        List<string> admissionControls = new();
        if (preferVllm && !isEmbedding)
        {
            admissionControls.Add("lora_count<=1 by default; raise only after per-pack adapter batching proves latency, memory, and parse stability.");
            admissionControls.Add("LoRA adapter paths must be local, hash-pinned, and staged under an operator-approved runtime directory; no remote adapter loads on the player path.");
            admissionControls.Add("For vLLM endpoints, cap --max-num-seqs and --max-num-batched-tokens to the one-player PalLLM workload first; long proof or docs-sync requests should not consume the same scheduler budget as live companion chat.");
            admissionControls.Add("Use --max-num-partial-prefills 2 with --max-long-partial-prefills 1 only after it proves short PalLLM turns can jump ahead of long prefill work without worse p95 latency.");
            admissionControls.Add("Keep vLLM disaggregated prefill/decode topology proof-only; route live companion chat through it only after monolithic-vs-split p95 TTFT, p95 ITL, p95 E2E, KV-transfer, queue, and decode-only fallback receipts beat the local single-server baseline.");
            admissionControls.Add("Keep MoRIIOConnector read/write P/D experiments off the default one-player lane unless the exact route SLO says ITL stability matters more than TTFT and fallback counters stay healthy.");
            admissionControls.Add("Keep Mooncake Store and MultiConnector topology proof-only; do not route one-player live companion chat through a shared KV store until queue time and fallback counters beat the local prefix-cache baseline.");
            admissionControls.Add("Keep external KV cache daemons proof-only; do not route one-player live companion chat through host-memory, SSD, RDMA, or cross-process KV reuse until queue time, cache-hit rate, daemon-stop rollback, and PalLLM fallback counters beat the local prefix-cache baseline.");
            if (sparseVllmDboProofLane)
            {
                admissionControls.Add("Keep vLLM DBO off the default one-player lane until --enable-dbo improves mixed short-turn plus long-proof p95 latency without increasing queue time, preemption, or fallback activation.");
            }
        }

        if (isGguf && !isEmbedding)
        {
            admissionControls.Add("For llama.cpp, cap -np to the real player/session count; continuous batching is useful only while p95 companion latency stays inside HOT_PATH.md budgets.");
            // Pass 346: Ollama OLLAMA_NUM_PARALLEL admission control
            // removed; llama.cpp -np cap above covers single-player
            // companion lanes.
            admissionControls.Add("For LM Studio, treat the desktop server as a single-user local lane; keep parallel requests and auto-evict settings conservative until short companion turns beat HOT_PATH.md budgets.");
        }

        if (preferSglang)
        {
            admissionControls.Add("For SGLang endpoints, cap --max-running-requests and --max-queued-requests to player QPS, and leave headroom in --mem-fraction-static for KV cache instead of filling VRAM to the edge.");
            admissionControls.Add("Keep SGLang FP4/FP8 KV and attention-backend pinning proof-only until backend support, p95 TTFT/ITL/E2E, parser stability, and fallback counters beat auto-selection on the same route replay.");
            admissionControls.Add("Keep SGLang EAGLE-3, adaptive speculation, SpecV2 overlap, STANDALONE, and NGRAM off strict JSON/tool-call routes until acceptance rate, OOM headroom, and parse stability are proven; SpecV2 requires topk=1.");
            admissionControls.Add("Keep SGLang HiCache storage and PD-disaggregation off the live companion path until same-prefix cold/warm replays prove lower TTFT or E2E latency without worse queue depth, parser stability, or fallback behavior.");
            admissionControls.Add("For SGLang Model Gateway, keep token-bucket queue depth sized to one-player PalLLM traffic first; long docs-sync or proof requests must not sit ahead of short companion turns.");
        }

        if (preferTransformersServe)
        {
            admissionControls.Add("For transformers serve, keep PalLLM queue limits conservative while continuous batching is enabled; reject promotion if a long docs-sync request delays short companion turns.");
            admissionControls.Add("Keep transformers serve /v1/responses off live companion routing until response-state cleanup and event-stream parsing prove they cannot strand state or delay ordinary /v1/chat/completions turns.");
        }

        if (preferFoundryLocal)
        {
            admissionControls.Add("Foundry Local is a single-user client runtime, not a multi-user inference fleet; keep PalLLM request concurrency conservative and use vLLM/SGLang for shared-server traffic.");
        }

        if (preferOpenVino)
        {
            admissionControls.Add("For OpenVINO Model Server, keep request concurrency to one-player QPS until /v3 benchmark traffic proves short companion turns do not queue behind long docs-sync or VLM prompts.");
            admissionControls.Add("For OpenVINO NPU lanes, cap prompt length and media count more tightly than GPU lanes until p95 latency and compile-cache behavior are measured.");
        }

        if (preferTensorRtLlm)
        {
            admissionControls.Add("For TensorRT-LLM endpoints, size max_batch_size, max_num_tokens, and request queueing to one-player PalLLM QPS first; raise only after p95 companion latency and fallback counters stay healthy.");
            admissionControls.Add("Keep TensorRT-LLM disaggregated serving, Dynamo routing, and multi-node topology proof-only until short companion turns are not delayed by long prefill traffic.");
        }

        if (supportsVision)
        {
            admissionControls.Add("image_count<=1 by default; raise only for explicit proof replay or multi-frame review.");
            admissionControls.Add("image_embeds require a local precomputed source, exact model/projector shape proof, and no player-supplied tensor payloads.");
        }

        if (supportsVideo)
        {
            admissionControls.Add("video_count<=1 and frame_count<=8 by default; distill video findings before feeding the text lanes.");
        }

        if (supportsAudioInput)
        {
            admissionControls.Add("audio_count<=1 and <=30 seconds by default; prefer cascaded ASR for ordinary player speech.");
            if (isGemmaAudio)
            {
                admissionControls.Add($"For {gemmaAudioFamily} audio-in, reject or chunk clips when the {gemmaAudioTokenRate}-tokens-per-second audio estimate plus text prompt exceeds the route token budget.");
            }

            admissionControls.Add("audio_embeds require a trusted encoder lane and stay off the normal player-speech path until malformed-shape isolation is proven.");
        }

        if (supportsAudioOutput)
        {
            admissionControls.Add("Realtime audio runs on a separate opt-in lane and must never block text reply delivery.");
            admissionControls.Add("For vLLM-Omni realtime audio, require async_chunk-off proof before any /v1/realtime voice profile is allowed to handle player-facing turns.");
        }

        if (isQwenOmni && supportsVideo)
        {
            admissionControls.Add("For Qwen Omni /v1/video/chat/stream, cap frame cadence and clip duration per proof profile, keep optional PCM16 audio chunks bounded, and shed streaming work before it can queue ahead of /api/chat.");
            admissionControls.Add("Do not co-host vLLM-Omni /v1/videos generation jobs with live PalLLM text, screenshot, or proof lanes unless async job concurrency, output storage, cancellation, and fallback replay are qualified.");
        }

        if (supportsSpeculativeDecoding && multimodal)
        {
            admissionControls.Add("Keep speculative decoding disabled for screenshot, video, and audio routes until modality-isolated PalLLM replay proves lower p95 latency without worse parse stability or fallback activation.");
        }

        if (supportsModelNativeMtp && multimodal)
        {
            admissionControls.Add("Do not co-schedule model-native MTP with mmproj, libmtmd, image, video, or audio workloads on one local server unless a same-process PalLLM replay proves no stalls, loops, OOM, or parser regressions.");
        }

        if (isQwen36)
        {
            admissionControls.Add("For Qwen3.6, cap ordinary companion prompts to measured live budgets; 262,144+ local, 1,010,000-token extended, hosted, and reduced-context GGUF profiles require separate proof.");
        }

        if (admissionControls.Count == 0)
        {
            admissionControls.Add("Token and request-size budgets stay with the existing PalLLM inference client caps.");
        }

        List<string> securityControls =
        [
            "Bind local model servers to loopback or a trusted LAN interface; do not expose raw inference ports publicly.",
            "Do not redistribute downloaded model weights, quant files, adapters, mmproj files, or drafter weights with PalLLM unless their license, base-model lineage, immutable revision/hash, and allowed redistribution terms are recorded in release evidence.",
        ];
        if (preferVllm && !isEmbedding)
        {
            securityControls.Add("Set VLLM_MAX_N_SEQUENCES to a workload-sized cap and keep reverse-proxy body/rate limits in front of any non-loopback vLLM endpoint.");
            securityControls.Add("Use one stable non-secret cache_salt per PalLLM trust domain when sharing a vLLM endpoint; do not put secrets in the salt or rotate it per request unless isolation matters more than cache hits.");
            securityControls.Add("Keep hosted request hints pseudonymous: prompt_cache_key, cache_salt, and safety_identifier may contain only non-secret PalLLM trust-domain hashes, never raw player identity or save contents.");
            securityControls.Add("Treat /v1/responses response ids and event logs as private runtime state; do not place raw response ids, built-in tool payloads, or streamed event bodies in support/public bundles.");
            securityControls.Add("Keep external KV cache services such as PegaFlow on loopback or an operator-owned private fabric; redact PEGAFLOW_HOST, PEGAFLOW_PORT, SSD cache paths, namespace ids, and raw KV metadata from support/public bundles.");
            securityControls.Add("Keep vLLM sleep/wake dev endpoints on a loopback-only admin surface; never expose VLLM_SERVER_DEV_MODE routes to players, LAN browsers, or a public reverse proxy.");
            securityControls.Add("Keep VLLM_ALLOW_RUNTIME_LORA_UPDATING off by default; if dynamic load/unload is needed, expose /v1/load_lora_adapter only on a loopback admin surface with hash-pinned local paths.");
            securityControls.Add("Keep vLLM prefill/decode proxies, KV-transfer ports, and NIXL/UCX/GDS backends on loopback or an admin-only trusted LAN; redact kv_transfer_params, worker URLs, cache handles, and raw transfer logs from support/public bundles.");
            securityControls.Add("Keep MoRIIO proxy, ping, handshake, notify, and HTTP ports on loopback or an admin-only fabric; redact remote_block_ids, remote_engine_id values, ZMQ endpoints, and RDMA transfer logs from support/public bundles.");
            securityControls.Add("Keep Mooncake master, client, and store endpoints on loopback or a trusted LAN only; treat KV blocks and store metadata as private runtime state excluded from support and public bundles.");
            securityControls.Add("Keep vLLM KV-event ZMQ publishers and replay endpoints on loopback or an admin-only network; event payloads can reveal token ids and cache-key context, so public/support bundles may contain only redacted event summaries.");
            if (sparseVllmDboProofLane)
            {
                securityControls.Add("Keep DBO data/expert-parallel worker ports and metrics on loopback or an admin-only fabric; public bundles should include only aggregate overlap metrics, not worker inventory.");
            }
        }

        if (isGguf && !isEmbedding)
        {
            securityControls.Add("Keep llama.cpp --host at 127.0.0.1 by default; if exposed beyond loopback, require --api-key and keep --webui-mcp-proxy, --tools, /props, and /slots behind an admin-only surface.");
            // Pass 346: 2 Ollama security controls removed (OLLAMA_HOST
            // loopback bind, OLLAMA_NO_CLOUD=1 air-gapped flag). The
            // llama.cpp --host control above covers the loopback bind
            // story; llama-server has no equivalent cloud-mode flag.
            securityControls.Add("Keep LM Studio's server on loopback for PalLLM; enable CORS only for trusted local tools, and do not ship model weights or downloaded artifacts without separate license review.");
        }

        if (preferSglang)
        {
            securityControls.Add("If SGLang is exposed beyond loopback, require --api-key, keep /metrics private, and place reverse-proxy body and rate limits in front of the endpoint.");
            securityControls.Add("Keep SGLang HiCache storage backends, dynamic attach/detach admin endpoints, and PD-disaggregation transfer ports on loopback or an admin-only trusted LAN; exclude raw KV pages, backend paths, and storage namespaces from support/public bundles.");
            securityControls.Add("Keep SGLang Model Gateway metrics, worker health, and queue stats on a loopback/admin surface; do not publish raw worker inventory to players or LAN browsers.");
            securityControls.Add("Redact SGLang draft-model paths/revisions, token maps, attention-backend dumps, server_info, and cache namespaces from support/public bundles unless reduced to hashes and aggregate counters.");
        }

        if (preferTransformersServe)
        {
            securityControls.Add("Keep transformers serve on localhost or behind the same authenticated reverse proxy as other local model servers; do not publish the raw port.");
            securityControls.Add("Pin Hugging Face repo revisions before promotion and avoid trust_remote_code on the player path unless the exact repo revision has a separate code review.");
            securityControls.Add("For transformers serve /v1/responses experiments, redact response ids, event stream bodies, and built-in tool payloads from support/public bundles until PalLLM owns a dedicated parser and retention policy.");
        }

        if (preferFoundryLocal)
        {
            securityControls.Add("Keep the Foundry Local service loopback-only for PalLLM; if it is exposed beyond the PC, put PalLLM's authenticated reverse proxy in front instead of publishing the raw Foundry port.");
            securityControls.Add("Foundry Local may download models and execution providers on first use; review model licenses before redistribution, then capture offline proof after the cache is warm.");
        }

        if (preferOpenVino)
        {
            securityControls.Add("Keep OpenVINO Model Server loopback-only for PalLLM unless an authenticated reverse proxy owns API keys, body limits, rate limits, and TLS.");
            securityControls.Add("For OpenVINO VLM lanes, do not enable remote media domains by default; prefer PalLLM-owned local media bytes or an explicit allowed local path.");
        }

        if (preferTensorRtLlm)
        {
            securityControls.Add("Keep TensorRT-LLM loopback-only for PalLLM unless an authenticated reverse proxy owns API keys, body limits, rate limits, TLS, /metrics privacy, and cancellation behavior.");
            securityControls.Add("For TensorRT-LLM multimodal lanes, prefer local base64 media and trusted embedding producers; do not enable arbitrary URL fetches or visual-generation endpoints on the player path.");
        }

        if (multimodal)
        {
            securityControls.Add("Set VLLM_MEDIA_URL_ALLOW_REDIRECTS=0 when remote media URLs are enabled.");
            securityControls.Add("Use --allowed-media-domains or --allowed-local-media-path deliberately; never allow arbitrary media fetches by default.");
            securityControls.Add("Treat remote image_url, audio_url, and video_url as SSRF-sensitive opt-in paths: deny localhost, private ranges, link-local ranges, IP literals, and redirects unless an operator-owned proxy has already sanitized them.");
            securityControls.Add("Do not expose --enable-mm-embeds lanes to players, LAN browsers, or untrusted tools; malformed embedding shapes can crash the model engine.");
        }

        if (supportsAudioInput)
        {
            securityControls.Add("Do not let audio_url or video_url fetch arbitrary URLs on the player path; prefer local bytes or an explicit allowed local media path.");
        }

        List<string> verificationChecks =
        [
            "Probe /v1/models or the provider-native catalog and confirm the configured model id is present before promoting the lane.",
            "Replay representative PalLLM chat turns and record p50/p95 latency, TTFT when available, failure rate, and fallback activation before changing defaults.",
            "Replay each PalLLM route class separately before promoting cache, scheduler, speculation, or routing settings: companion chat, vision describe, world-state extraction, screenshot proof loops, audio/ASR, and long proof/docs traffic.",
            "Capture a primary-source capability receipt before promotion: model-card or vendor-doc revision, local /v1/models identity, supported text/image/video/audio/tool-call/speculation fields, positive canaries for claimed modalities, and negative canaries for unsupported modalities.",
            "Capture a model-artifact provenance receipt before promotion: model card license, base_model or adapter relation, immutable revision/commit or local SHA-256, weight format, runtime and tokenizer revisions, trust_remote_code status, and whether redistribution is allowed.",
        ];

        List<string> promotionReceipts =
        [
            "Route replay receipt: preserve PalLLM operation and budget labels for companion chat, vision describe, world-state extraction, screenshot proof loops, audio/ASR, and long proof/docs lanes.",
            "Runtime capability handshake receipt: model-card or vendor-doc revision, served model id from /v1/models or provider-native catalog, runtime version, enabled launch flags, positive canaries for claimed capabilities, and negative canaries for unsupported modalities.",
            "Model artifact provenance receipt: source URL or local path, immutable revision/commit or local SHA-256, model-card license metadata, base-model/adapter relation, weight format, safetensors/pickle and trust_remote_code status, runtime/tokenizer revisions, and redistribution decision.",
            "Publication receipt: confirm model weights, adapters, mmproj files, prompts, voice samples, and pack assets are either excluded from the PalLLM package or have redistribution permission recorded before release.",
        ];

        List<string> metricReceipts =
        [
            "PalLLM /metrics: palllm_chat_duration_seconds, palllm_inference_recent_window_status, palllm_inference_lane_status, and palllm_fallback_reply_total.",
            "PalLLM route replay receipt: preserve operation/budget labels from palllm_inference_lane_status and paired p50/p95 evidence for chat, vision, audio/ASR, and proof/docs lanes instead of collapsing them into one model score.",
            "Runtime capability handshake receipt: primary-source model-card or vendor-doc revision, served model id from /v1/models or provider-native catalog, runtime version, enabled launch flags, positive modality/tool/speculation canary ids, and negative unsupported-modality canary ids.",
            "Model artifact provenance receipt: source URL or local path, immutable revision/hash, license metadata, base-model/adapter relation, weight format, safetensors/pickle/trust-remote-code status, runtime version, and redistribution decision.",
        ];

        if (supportsStructuredOutputs)
        {
            promotionReceipts.Add("Structured-output portability receipt: schema name and digest, route class, served model id, request shape (response_format, structured_outputs, or grammar), grammar/backend id, exact parse success, schema-validation success, refusal or empty-output handling, p95 latency, token usage, and fallback counters.");
            metricReceipts.Add("Structured-output proof receipts: schema digest, request-shape id, exact-parse success rate, schema-validation success rate, invalid-output retry count, token usage, p95 latency, and route-labeled fallback counters.");
            verificationChecks.Add("Run a schema-echo portability canary per runtime: one required object, one enum, one bounded array, one deliberate violation prompt, and one changed-schema digest; reject promotion if json_object-only mode passes while json_schema validation fails.");
        }

        if (isGguf && !isEmbedding)
        {
            promotionReceipts.Add("GGUF prompt/state-cache receipt: same-slot second-turn replay, slot save/restore timing, tokenizer/chat-template/context/adapters/server-build identity, and no unexpected full prompt re-processing before cache promotion.");
            metricReceipts.Add("llama.cpp /metrics or logs: prompt-cache reuse, slot count, -cram pressure, active KV memory, accepted/generated token statistics, forced full prompt re-processing warnings, and p95 companion latency.");
            metricReceipts.Add("llama.cpp state-cache canary: same-slot second-turn replay, slot save/restore timing, tokenizer/chat-template/context/adapters/server-build receipt, and no unexpected full-prefill log lines.");
            // Pass 346: Ollama proof-metrics receipt removed. llama-server
            // /metrics covers the equivalent timing receipts above.
            metricReceipts.Add("LM Studio proof receipts: lms ps or server logs for loaded-model TTL, context length, GPU offload, auto-evict events, and PalLLM fallback activation.");
            verificationChecks.Add("For llama.cpp GGUF lanes, record /health, /v1/models, /metrics or server logs for prompt-cache reuse, slot count, -cram pressure, active KV memory, forced full prompt re-processing warnings, and p95 latency before raising -np or CacheRamMiB.");
            verificationChecks.Add("Run a llama.cpp state-cache canary before host prompt-cache promotion: same PalLLM prefix twice on the same slot should avoid unexpected full prefill, then a changed chat template, context size, adapter id, or model file should invalidate instead of reusing stale state.");
            verificationChecks.Add("If using llama.cpp -ctk/-ctv quantized KV, --sleep-idle-seconds, or context above the model default, replay PalLLM strict JSON, tool-call, companion, and long-context turns against default f16 KV first.");
            // Pass 346: 2 Ollama verification checks removed
            // (`ollama ps` receipt, OLLAMA_KV_CACHE_TYPE quantization
            // proof). The llama.cpp -ctk/-ctv verification above
            // already covers the quantized-KV proof requirement.
            verificationChecks.Add("For LM Studio lanes, prove /v1/models lists the loaded model, /v1/chat/completions handles PalLLM replay traffic with ttl, response_format json_schema, and tool definitions, then record lms ps/server-log residency plus fallback behavior before promotion.");
        }

        if (preferVllm && !isEmbedding)
        {
            promotionReceipts.Add("vLLM scheduler/cache promotion receipt: one short companion turn queued beside one long proof/docs prompt, prefix/KV/cache metrics, queue/preemption pressure, p95 latency, and PalLLM fallback counters before changing defaults.");
            metricReceipts.Add("vLLM /metrics core: vllm:num_requests_running, vllm:num_requests_waiting, vllm:kv_cache_usage_perc, vllm:request_success_total, vllm:prompt_tokens_total, and vllm:generation_tokens_total.");
            metricReceipts.Add("vLLM latency histograms: vllm:time_to_first_token_seconds, vllm:inter_token_latency_seconds, vllm:e2e_request_latency_seconds, vllm:request_prefill_time_seconds, and vllm:request_decode_time_seconds.");
            metricReceipts.Add("vLLM prefix/KV receipts: vllm:prefix_cache_queries, vllm:prefix_cache_hits, vllm:external_prefix_cache_queries, vllm:external_prefix_cache_hits, vllm:prompt_tokens_cached, vllm:cache_config_info, and optional vllm:kv_block_lifetime_seconds / vllm:kv_block_idle_before_evict_seconds / vllm:kv_block_reuse_gap_seconds when KV-cache sampling is enabled.");
            metricReceipts.Add("vLLM pressure receipts: vllm:num_requests_waiting, vllm:num_requests_swapped when exposed, vllm:request_queue_time_seconds, and preemption/recompute warnings during mixed short-turn plus long-proof replays.");
            promotionReceipts.Add("vLLM disaggregated prefill/decode promotion receipt: monolithic baseline versus split P/D p95 TTFT, p95 ITL, p95 E2E, prefill/decode worker ids, router mode, KV-transfer backend, transfer latency/failures, decode-only fallback, and PalLLM fallback counters; do not count it as throughput proof.");
            promotionReceipts.Add("vLLM MoRIIO P/D promotion receipt: kv_producer and kv_consumer endpoint ids, VLLM_MORIIO_CONNECTOR_READ_MODE value, proxy/http/handshake/notify port map, prefix-caching-disabled baseline, TTFT/ITL/E2E deltas, worker-stop rollback, and PalLLM fallback counters.");
            metricReceipts.Add("vLLM P/D topology receipts: request_prefill_time, request_decode_time, TTFT, ITL, E2E, queue-time evidence per prefill/decode instance, prefix/external-prefix cache counters, KV-transfer success/failure/latency logs, and route-labeled PalLLM fallback counters.");
            metricReceipts.Add("vLLM MoRIIO receipts: read/write mode, WAITING_FOR_REMOTE_KVS duration, remote KV transfer latency, proxy serialization time, prefill/decode queue time, TTFT/ITL/E2E deltas, and route-labeled fallback counters.");
            metricReceipts.Add("vLLM sleep-mode receipt: vllm:engine_sleep_state{sleep_state=\"awake|weights_offloaded|discard_all\"}, plus PalLLM fallback counters while the lane is asleep or waking.");
            promotionReceipts.Add("vLLM KV-event promotion receipt: KVEventsConfig redacted shape, BlockStored/BlockRemoved/AllBlocksCleared counts, block-size/group/sliding-window metadata, replay gap/drop counts, and proof that raw token_ids, extra_keys, cache_salt, media ids, and LoRA names stayed out of public/support bundles.");
            metricReceipts.Add("vLLM KV-event receipts: enable_kv_cache_events loopback subscriber sample with event-batch counts, replay sequence gaps, dropped-message counters, block hash counts, and redacted extra-key class counts; no raw token_ids or extra_keys archived.");
            promotionReceipts.Add("Mooncake Store promotion receipt: MOONCAKE_CONFIG_PATH hash, kv_transfer_config redacted shape, master/client health, cache-hit rate, cold/warm TTFT/E2E, store failure rollback, and PalLLM fallback counters.");
            metricReceipts.Add("Mooncake Store receipts: vLLM KV cache events, Mooncake master/client health, distributed cache hit rate, KV load/store failures, cold-vs-warm TTFT/E2E, and per-route PalLLM fallback counters.");
            promotionReceipts.Add("External KV cache process-boundary receipt: cache daemon launch/config hash, endpoint binding, pool/SSD/RDMA budget, namespace/model identity, cold/warm route replay, worker-restart reuse, daemon-stop rollback, local-prefix-cache rollback, and PalLLM fallback counters.");
            metricReceipts.Add("External KV cache receipts: cache-daemon health, local/remote hit rate, load/store failures, pinned-host or SSD cache pressure, worker-restart warm reuse, cold-vs-warm TTFT/E2E, and route-labeled PalLLM fallback counters.");
            metricReceipts.Add("FlexKV offload receipts: FlexKVConnectorV1 config hash, CPU/SSD/remote-store capacity, scheduler-side async transfer counts, load/store failures, cold-vs-warm TTFT/E2E, and local-prefix rollback evidence.");
            if (sparseVllmDboProofLane)
            {
                promotionReceipts.Add("vLLM DBO sparse-MoE promotion receipt: no-DBO baseline, --enable-dbo thresholds, DP/EP topology, all2all backend, microbatch counts, TTFT/ITL/E2E deltas, queue/preemption pressure, and PalLLM fallback counters.");
                metricReceipts.Add("vLLM DBO receipts: request_prefill_time, request_decode_time, TTFT, ITL, E2E latency, queue time, microbatch/overlap counters when exposed, DP/EP topology, and route-labeled PalLLM fallback counters.");
                verificationChecks.Add("For vLLM DBO sparse-MoE lanes, replay one short companion turn beside one long proof/docs prompt with and without --enable-dbo and reject promotion if p95 latency, queue time, parse stability, or fallback counters regress.");
            }
            verificationChecks.Add("Run two same-prefix turns and confirm prefix-cache or prefill metrics improve on the second turn before claiming cache benefit.");
            verificationChecks.Add("For vLLM --performance-mode interactivity, compare p50/p95 end-to-end latency, TTFT, ITL, queue behavior, and fallback activation against balanced and throughput modes on the same PalLLM replay before changing a shared server default.");
            verificationChecks.Add("If PrefixCacheSalt is configured, run same-prefix requests with matching and different cache_salt values and confirm reuse is isolated to the matching trust domain.");
            verificationChecks.Add("If PalLLM:Inference:PromptCacheKey or PromptCacheRetention is configured, replay the same long-prefix route with and without prompt_cache_key / prompt_cache_retention and record accepted request shape, cached-token receipts, p95 latency, and fallback counters before promotion.");
            verificationChecks.Add("If PalLLM:Inference:Verbosity is configured, replay the same route with and without verbosity and record accepted request shape, generated-token delta, explanation quality, p95 latency, and fallback counters before promotion.");
            verificationChecks.Add("If PalLLM:Inference:SafetyIdentifier is configured, confirm the outgoing request carries only a stable pseudonymous hash and that support/public bundles contain neither the hash nor raw player identity.");
            verificationChecks.Add("If PalLLM:Inference:StoreCompletions is configured, confirm the outgoing request carries store only on the hosted canary lane and that support/public bundles do not retain prompt or completion text from the canary.");
            verificationChecks.Add("If PalLLM:Inference:RequestMetadata is configured, confirm metadata carries at most 16 bounded proof labels, no raw prompt/player/save/secret text, and no high-cardinality metric labels.");
            verificationChecks.Add("If PalLLM:Inference:ClientRequestIdHeader is configured, confirm the outgoing header is x-client-request-id or x-request-id, the value is bounded visible ASCII, provider request-id receipts line up, and metrics never use the id as a label.");
            verificationChecks.Add("If PalLLM:Inference:LlamaCppCachePrompt, LlamaCppSlotId, or LlamaCppCacheReuseTokens is configured, replay same-prefix and changed-prefix llama-server turns and record accepted request shape, slot id, second-turn TTFT, cache metrics, cache RAM pressure, and fallback counters before promotion.");
            verificationChecks.Add("If enabling --kv-cache-dtype fp8 or nvfp4, replay PalLLM JSON, tool-call, companion, and long-context proof turns and record exact parse success, quality deltas, TTFT, ITL, KV-cache utilization, and fallback behavior before promotion.");
            verificationChecks.Add("If vLLM preempts or recomputes requests under KV pressure, reject promotion for the player-facing lane unless short-turn p95 latency and fallback activation stay healthy under the same mixed replay.");
            verificationChecks.Add("When vLLM KV-block residency sampling is enabled, reject promotion if proof/docs blocks stay idle long enough to evict hot companion prefixes or if reuse gaps prove the cache seed is not being reused.");
            verificationChecks.Add("If vLLM KV events are enabled, run a local subscriber during replay and confirm event counts agree with prefix, external-prefix, and multimodal cache counters; reject promotion if raw token_ids, cache_salt, LoRA names, media ids, or prompt-embedding hashes enter support/public evidence.");
            verificationChecks.Add("For vLLM scheduler caps, A/B --max-num-batched-tokens, --max-num-seqs, partial-prefill limits, and long-prefill thresholds with one short companion turn queued beside one long proof/docs prompt.");
            verificationChecks.Add("For vLLM disaggregated prefill/decode, replay the same PalLLM route through monolithic and split P/D topology; reject promotion unless TTFT or tail ITL improves without worse E2E latency, parse stability, queue pressure, or fallback counters, and unless a stopped prefill or decode worker rolls back cleanly.");
            verificationChecks.Add("For MoRIIOConnector P/D, compare read mode, write mode, and monolithic vLLM on the same PalLLM replay; reject promotion if TTFT regression outweighs ITL stability for the route, prefix-cache-disabled behavior is unproven, or remote KV wait/transfer errors trigger fallback.");
            verificationChecks.Add("If PalLLM:Inference:RequestPriority is configured, run the same mixed replay with --scheduling-policy priority and confirm the lower-priority-value companion turn wins queue time without starving background proof/docs lanes.");
            verificationChecks.Add("If PalLLM:Inference:ServiceTier is configured, replay with and without service_tier and record accepted request shape, queue/TTFT evidence, p95 latency, cost posture where applicable, and fallback counters before promotion; treat scale like priority for player-facing proof only.");
            verificationChecks.Add("If PalLLM:Inference:TokenBudgetField is max_completion_tokens, replay the same PalLLM route with max_tokens and max_completion_tokens on the exact endpoint and record accepted request shape, visible/reasoning token usage when exposed, p95 latency, and fallback counters.");
            verificationChecks.Add("If PalLLM:Inference:ThinkingTokenBudget is configured, replay the same reasoning route with no budget and with the configured thinking_token_budget on the exact vLLM server, then record reasoning-parser config, accepted request shape, visible/reasoning token usage, p95 latency, and fallback counters before promotion.");
            verificationChecks.Add("Record whether --generation-config vllm is active; reject promotion if model-repo sampling defaults override PalLLM's configured deterministic replay settings.");
            verificationChecks.Add("If PalLLM:Inference:Seed is configured, replay the same request twice on the same vLLM replica and record seed, served model id, system_fingerprint when exposed, TP/PP layout, and output drift before trusting reproducibility.");
            verificationChecks.Add("If PalLLM:Inference:FrequencyPenalty is configured, replay long companion turns with and without frequency_penalty and record repeated-phrase rate, token count, latency, and fallback counters before promotion.");
            verificationChecks.Add("If PalLLM:Inference:TopK, MinP, or RepetitionPenalty are configured, replay with and without those sampler fields and record accepted request shape, style/loop deltas, token count, p95 latency, and fallback counters before promotion.");
            verificationChecks.Add("If PalLLM:Inference:StopSequences is configured, replay with and without the exact delimiters and record accepted request shape, lower generated-token count, no clipped companion text, and stable fallback counters.");
            verificationChecks.Add("For predicted-output canaries, replay with and without InferencePrompt.Prediction / prediction on the exact endpoint and record accepted request shape, accepted versus rejected prediction tokens when exposed, p95 latency, and fallback counters before using it on proof or docs lanes.");
            verificationChecks.Add("For logprob confidence canaries, replay with and without InferencePrompt.Logprobs / TopLogprobs on the exact endpoint and record accepted request shape, returned choice logprobs receipt, p95 latency, and fallback counters before using it for validators or evaluator escalation.");
            verificationChecks.Add("For multi-replica vLLM pools, compare round-robin against sticky or KV cache-aware routing and record cache-hit rate, TTFT, ITL, and fallback behavior before using the pool for live companion turns.");
            verificationChecks.Add("For vLLM /v1/responses, keep it proof-only until response.created, response.output_text.delta, response.completed, response-id retention/deletion, built-in tool payload handling, usage receipts, and ordinary /v1/chat/completions fallback are replayed.");
            verificationChecks.Add("For vLLM Mooncake Store, replay multi-turn PalLLM proof/docs traffic across local prefix-cache, single-node MooncakeStoreConnector, and any MultiConnector pool; reject if cache-hit gains do not improve TTFT/E2E without hurting companion p95 or fallback behavior.");
            verificationChecks.Add("For Mooncake Store failure proof, stop the master or a client during replay and confirm PalLLM falls back to local prefix-cache or deterministic text reply behavior without leaking KV block metadata.");
            verificationChecks.Add("For external KV cache daemons such as PegaFlow or FlexKV, replay local prefix-cache versus daemon-backed routes, restart the vLLM worker while keeping the daemon alive, then stop the daemon and confirm rollback to local prefix cache or deterministic fallback without archiving raw KV blocks, cache paths, namespaces, or player text.");
            verificationChecks.Add("Check that chunked-prefill service stays responsive when a long proof or docs-sync prompt is queued beside a short companion turn.");
            verificationChecks.Add("If enabling vLLM sleep mode, record GPU memory reclaimed, wake latency, cold-after-wake cache behavior, and deterministic PalLLM fallback while the lane is asleep before enabling an idle policy.");
            verificationChecks.Add("Before enabling a LoRA personality-adapter lane, prove base-model compatibility, local hash pinning, one-adapter routing, adapter-specific prefix-cache identity, missing-adapter fallback, and deterministic PalLLM fallback on adapter load failure.");

            if (isQwen36)
            {
                metricReceipts.Add("Qwen3.6 hybrid-GDN receipt: served model id, runtime version, Gated DeltaNet/Mamba scheduler strategy, page size, attention/backend kernel, context length, --mem-fraction-static, TTFT/ITL, exact parse success, and fallback counters.");
                promotionReceipts.Add("Qwen3.6 context receipt: served model id, local/open-weight or hosted catalog source, runtime max_model_len/context cap, native-versus-extended context flags, route token budget, KV/state memory, and fallback counters.");
                verificationChecks.Add("For Qwen3.6 hybrid-GDN lanes, A/B the default scheduler against extra-buffer/page-size strategies where supported, and reject promotion if VRAM, p95 latency, exact parse success, or fallback counters regress on chat, screenshot, and tool routes.");
                verificationChecks.Add("For Qwen3.6 context promotion, replay short companion turns and long proof/docs turns under native 262,144-token, 1,010,000-token extended, hosted, and reduced GGUF contexts as separate profiles; reject if p95 latency, fallback counters, or parser success regress.");
            }
        }

        if (preferSglang)
        {
            promotionReceipts.Add("SGLang promotion receipt: sanitized request dump/replay template hash, radix-cache metrics, queue/running request metrics, worker-scoped circuit-breaker events, and fallback counters before using gateway pools for live turns.");
            promotionReceipts.Add("SGLang attention/precision promotion receipt: auto-selection baseline versus pinned backend, attention backend names, page size, KV dtype, quantization/scaling receipt, CUDA/PyTorch/GPU class, TTFT/ITL/E2E deltas, parser stability, and fallback counters.");
            promotionReceipts.Add("SGLang speculative promotion receipt: algorithm, draft model/revision or NGRAM config, topk/num_steps/draft-token caps, SpecV2/adaptive settings, acceptance rate, OOM headroom, TTFT/ITL/E2E deltas, strict-route parse stability, and fallback counters.");
            promotionReceipts.Add("SGLang HiCache promotion receipt: launch flags, page size, host/storage budget, prefetch/write policy, backend namespace hash, cold/warm route replay, cache-hit rate, TTFT/E2E deltas, storage attach/detach or backend-stop rollback, and PalLLM fallback counters.");
            metricReceipts.Add("SGLang /metrics: sglang:cache_hit_rate, sglang:token_usage, sglang:num_running_reqs, sglang:num_queue_reqs, sglang:time_to_first_token_seconds, sglang:time_per_output_token_seconds, and sglang:e2e_request_latency_seconds.");
            metricReceipts.Add("SGLang attention/precision receipts: /server_info or launch manifest, --attention-backend, --prefill-attention-backend, --decode-attention-backend, --mm-attention-backend, page size, kv-cache-dtype, quantization, GPU architecture, and route p95 latency.");
            metricReceipts.Add("SGLang speculation receipts: speculative algorithm, draft model hash/revision, topk, num_steps, num_draft_tokens, accepted/proposed tokens or acceptance rate, SpecV2/adaptive status, OOM/backoff events, and route-labeled fallback counters.");
            metricReceipts.Add("SGLang HiCache receipts: --enable-cache-report output, cache_hit_rate, token_usage, cold/warm TTFT and E2E latency, prefetch policy, write policy, L2/L3 storage budget, backend attach/detach result, and route-labeled fallback counters.");
            metricReceipts.Add("SGLang replay receipt: local request dump/replay or crash-dump replay artifact path, sanitized replay template hash, and confirmation that raw dumps stayed out of public/support bundles.");
            verificationChecks.Add("For SGLang lanes, launch with --enable-metrics during qualification and record cache_hit_rate, token_usage, TTFT, ITL, running/queued requests, and PalLLM fallback behavior.");
            verificationChecks.Add("For SGLang attention backend lanes, compare auto-selection against pinned FlashInfer, FA3/FA4, Triton, TRTLLM MHA/MLA, AITER/ROCm, or Intel XPU only where the support matrix says the route's page size, FP8/FP4 KV, speculative topk, sliding-window, and multimodal requirements are supported.");
            verificationChecks.Add("For SGLang FP4/FP8 KV lanes, replay strict JSON, tool-call, companion, screenshot/world-state, and long-context routes against auto KV first; reject promotion if fp4_e2m1/fp8 KV changes parse success, quality, p95 latency, or fallback activation.");
            verificationChecks.Add("For SGLang EAGLE-3, adaptive speculation, SpecV2, STANDALONE, MTP, or NGRAM lanes, compare no-spec on the same PalLLM replay, require route-local acceptance and latency receipts, pin topk=1 when SpecV2 is enabled, and disable the lane when parser stability or OOM headroom regresses.");
            verificationChecks.Add("For SGLang HiCache lanes, compare radix-only, HiCache L2 host, and optional L3 storage or PD-disaggregation profiles on the same PalLLM companion, screenshot/world-state, audio/ASR, and proof/docs replay; reject promotion if queue depth, parser stability, p95 latency, or fallback counters regress.");
            verificationChecks.Add("For SGLang HiCache failure proof, detach or stop the storage backend during replay and confirm PalLLM falls back to radix-cache or deterministic text replies without archiving raw KV pages, backend paths, storage namespaces, or player text.");
            verificationChecks.Add("For SGLang request dump/replay, replay the same PalLLM route class locally before promotion and archive only sanitized templates or hashes, not raw player text dumps.");
            verificationChecks.Add("For SGLang deterministic proof lanes, run same-prompt and radix-cache determinism checks with the chosen attention backend before using the lane as judge or eval evidence.");
            verificationChecks.Add("For SGLang Model Gateway pools, record retry counts, worker-scoped circuit-breaker transitions, token-bucket queue depth, cache-aware routing hit rate, TTFT/ITL, and PalLLM fallback activation before promoting a multi-worker lane.");
        }

        if (preferTransformersServe)
        {
            promotionReceipts.Add("transformers serve promotion receipt: repo_id@revision or hash-pinned local path, /load_model ready event, /v1/models identity, continuous-batching A/B replay, and exact JSON/tool-call parse success.");
            metricReceipts.Add("transformers serve receipts: /v1/models readiness, /load_model ready event, PalLLM p50/p95 latency, exact JSON/tool-call parse success, and fallback activation while continuous batching is enabled.");
            verificationChecks.Add("For transformers serve lanes, prove /load_model reaches ready, /v1/models lists the positional model, and /v1/chat/completions handles PalLLM replay traffic before writing appsettings.");
            verificationChecks.Add("Compare transformers serve --continuous-batching against a non-batched local baseline on p50/p95 latency, short-request starvation, exact JSON/tool-call parse success, and fallback activation.");
            verificationChecks.Add("For transformers serve /v1/responses, keep the endpoint proof-only until response.created, response.output_text.delta, response.completed, response-id cleanup, tool-event shape, and ordinary chat fallback are replayed.");
            verificationChecks.Add("If using transformers serve tool calling, qualify the exact Qwen or Gemma 4 tokenizer/tool-call path against PalLLM schemas before allowing tool-heavy routes.");
        }

        if (preferFoundryLocal)
        {
            promotionReceipts.Add("Foundry Local promotion receipt: dynamic endpoint from foundry service status, /openai/status, /openai/models identity, execution provider, first-use download/load timing, warm latency, and offline-cache proof.");
            metricReceipts.Add("Foundry Local receipts: foundry service status, /openai/status, /openai/models, execution provider, first-use download/load time, warm p50/p95, and fallback activation.");
            verificationChecks.Add("For Foundry Local lanes, prove foundry service status reports the endpoint, /openai/status is healthy, /openai/models lists the loaded model, and /v1/chat/completions handles PalLLM replay traffic before writing appsettings.");
            verificationChecks.Add("Record the selected Foundry Local execution provider, first-use download/load time, warm p50/p95 latency, exact JSON/tool-call parse success, and deterministic fallback activation before promoting the lane.");
        }

        if (preferOpenVino)
        {
            promotionReceipts.Add("OpenVINO promotion receipt: /v3/models identity, /v3/chat/completions replay, target_device, first-use pull/compile timing, CPU/GPU/NPU comparison when available, and fallback activation.");
            metricReceipts.Add("OpenVINO receipts: /v3/models readiness, first chat latency, warm p50/p95, target_device, first-use pull/compile time, exact JSON/tool-call parse success, and fallback activation.");
            verificationChecks.Add("For OpenVINO Model Server lanes, prove /v3/models lists the configured model and /v3/chat/completions handles PalLLM replay traffic before writing appsettings.");
            verificationChecks.Add("Record target_device, first-use pull/compile time, warm p50/p95 latency, exact JSON/tool-call parse success, and deterministic fallback activation before promoting OpenVINO lanes.");
            verificationChecks.Add("For OpenVINO NPU lanes, compare PREFILL_HINT and GENERATE_HINT settings plus prefix-cache behavior against CPU/GPU baselines on the same PalLLM replay set.");
        }

        if (preferTensorRtLlm)
        {
            promotionReceipts.Add("TensorRT-LLM promotion receipt: /health, /v1/models, /v1/chat/completions replay, served_model_name, config YAML hash, KV-cache/inflight-batching metrics, and malformed-media fallback when multimodal.");
            metricReceipts.Add("TensorRT-LLM /metrics or Dynamo receipts: GPU memory, active requests, inflight batching, kvCacheStats cacheHitRate, used/free KV blocks, TTFT/ITL when available, and config YAML hash.");
            verificationChecks.Add("For TensorRT-LLM lanes, prove /health, /v1/models, and /v1/chat/completions before writing appsettings; record served_model_name, backend, tp/pp/ep sizes, and config YAML hash.");
            verificationChecks.Add("Record TensorRT-LLM /metrics receipts for GPU memory, inflight batching, kvCacheStats cacheHitRate, used/free blocks, TTFT/ITL when available, exact JSON/tool-call parse success, and deterministic fallback activation.");
            verificationChecks.Add("For TensorRT-LLM speculation, compare MTP, EAGLE, NGram, or DraftTarget config YAML against no-spec on PalLLM replay and keep strict routes no-spec until parser stability is proven.");
            verificationChecks.Add("For TensorRT-LLM multimodal lanes, prove local base64 image/video/audio requests and malformed-media fallback before routing Palworld screenshots or speech through the lane.");
        }

        if (isGemma3n)
        {
            verificationChecks.Add("For Gemma 3n, compare text-only parameter skipping and PLE cache behavior against full multimodal loading on the same PalLLM turns; record memory, load latency, and fallback behavior.");
        }

        if (isGemma4)
        {
            verificationChecks.Add("For Gemma 4 audio-in, verify the exact size/runtime accepts input_audio and budget 25 audio tokens per second before routing player speech.");
        }

        if (isQwenOmni)
        {
            verificationChecks.Add("For Qwen Omni, prove text-only, text+audio-in, and text+audio-out requests independently; record whether audio choices include a text mirror and whether speaker configuration is stable.");
            verificationChecks.Add("For vLLM-Omni Qwen Omni realtime, record the async_chunk-disabled deploy config plus session.created, response.audio.delta, transcript delta, reconnect/stall, and text-chat fallback evidence before player-facing voice promotion.");
            verificationChecks.Add("For vLLM-Omni Qwen Omni streaming video, prove /v1/video/chat/stream with bounded base64 frames, optional PCM16 audio chunks, frame-cadence receipts, reconnect/stall behavior, and still-image/world-state fallback before any live Palworld stream promotion.");
            verificationChecks.Add("For vLLM-Omni /v1/videos, prove async job create/poll/content/delete behavior, storage cleanup, cancellation, prompt-publication hygiene, and no interference with /api/chat or /api/vision before treating video generation as release-proof material.");
            verificationChecks.Add("For Qwen3.5-Omni research lanes, require an actual local model artifact or provider-compatible endpoint before writing PalLLM config; the technical report alone is not promotion evidence.");
        }

        if (supportsStructuredOutputs)
        {
            verificationChecks.Add("Run repeated JSON-schema and tool-call qualification; reject promotion if exact parse success is not stable.");
            verificationChecks.Add("For tool-call-capable vLLM lanes, prove PalLLM:Inference:ParallelToolCalls=false forwards parallel_tool_calls=false and yields zero or one action/tool call on PalLLM directive routes before allowing any parallel fan-out experiment.");
            verificationChecks.Add("Keep app-side schema validation authoritative even when the server advertises constrained decoding; classify failures as invalid JSON, schema mismatch, unsupported schema keyword, refusal or empty output, semantic mismatch, or timeout.");
        }

        if (supportsToolCalls)
        {
            verificationChecks.Add("For strict tool-call canaries, replay with InferencePrompt.Tools plus InferencePrompt.ToolChoice, confirm the outgoing tools/tool_choice request shape is accepted, archive the returned tool_calls receipt, and require a deterministic fallback path when content is empty or malformed.");
        }

        if (preferSglang && supportsStructuredOutputs)
        {
            verificationChecks.Add("For SGLang structured outputs, qualify OpenAI response_format json_schema plus structural_tag shapes against PalLLM schemas; reject promotion if the grammar backend changes exact parse success.");
            verificationChecks.Add("For text structured-output lanes, replay with and without InferencePrompt.ResponseFormat / response_format json_schema or InferencePrompt.StructuredOutputs / structured_outputs and record parse success, token usage, p95 latency, fallback counters, and no-spec strict-route behavior.");
        }

        if (supportsSpeculativeDecoding)
        {
            promotionReceipts.Add("Speculation promotion receipt: no-spec baseline, selected speculative mode, accepted/proposed token ratio, TTFT/ITL, exact JSON/tool-call parse success, and fallback counters for each promoted route class.");
            verificationChecks.Add("Compare baseline vs speculative latency, accepted/proposed token ratio, and parse stability with the exact model, quant, and server version; disable speculation if structured-output reliability drops.");
            verificationChecks.Add("Keep strict JSON, tool-call, judge, and save-replay routes no-spec until each route has its own repeated-run proof.");
            if (multimodal)
            {
                verificationChecks.Add("Run modality-isolated speculative replay: text-only chat, screenshot/image, video, and audio cases each need no-spec, n-gram, and model-native speculation comparisons before a multimodal lane can be promoted.");
            }

            if (isGguf)
            {
                verificationChecks.Add("For llama.cpp --spec-type ngram-simple, ngram-mod, draft-simple, or draft-mtp lanes, capture accepted/generated token statistics and disable the lane if p95 latency or exact parse success regresses.");
            }
        }

        if (supportsModelNativeMtp)
        {
            verificationChecks.Add("For Qwen3.6 or Gemma 4 MTP, compare the model-native drafter against n-gram or no-spec baselines on the same PalLLM replay set and record acceptance rate, TTFT, ITL, fallback behavior, and JSON/tool-call parse stability before promotion.");
            if (multimodal)
            {
                verificationChecks.Add("For MTP plus multimodal-capable lanes, run a same-process negative canary with text MTP followed by image, video, and audio replay; prefer split-process text-MTP and no-spec media endpoints unless p95 latency, memory, parser stability, and fallback counters stay healthy.");
            }

            if (isQwen36)
            {
                verificationChecks.Add("For Qwen3.6 MTP-1 latency mode, include a --no-enable-prefix-caching replay and compare it against the normal prefix-cache lane before changing any player-facing default.");
            }
            else if (isGemma4)
            {
                verificationChecks.Add("For Gemma 4 MTP, reject generic draft_model wiring for assistant checkpoints; record assistant checkpoint id/hash, method=mtp, token depth, acceptance rate, prefix-cache-disabled benchmark results, normal-cache replay, and fallback behavior before promotion.");
            }
        }

        if (isGguf && multimodal)
        {
            verificationChecks.Add("Smoke llama.cpp libmtmd with a matching mmproj through /v1/chat/completions before routing Palworld screenshots to the GGUF lane.");
        }

        if (multimodal)
        {
            promotionReceipts.Add("Multimodal media-admission receipt: local data URL accepted, remote media URL disabled by default, allowed-domain fetch logged, redirect probe blocked, and localhost/private/link-local/IP-literal probes rejected before model execution.");
            metricReceipts.Add("Multimodal cache receipts: vllm:mm_cache_queries and vllm:mm_cache_hits when available, or LMCache EC logs with cold-vs-warm media TTFT and media-cache memory.");
            metricReceipts.Add("Multimodal media-admission receipt: local data URL accepted, remote media URL disabled by default, allowed-domain fetch logged, redirect probe blocked, and localhost/private/link-local URL probes rejected before model execution.");
            verificationChecks.Add("Warm media UUIDs with full local media bytes first, then verify uuid-only replay only after the same server process has a cache hit.");
            verificationChecks.Add("Measure multimodal processor-cache memory per API process and engine core before raising image, video, or audio admission caps.");
            verificationChecks.Add("For vLLM/LMCache encoder-cache experiments, record cold vs warm media TTFT and confirm MM cache-hit metrics or LMCache EC logs before claiming a screenshot, video, or audio cache win.");
            verificationChecks.Add("For --enable-mm-embeds experiments, prove valid precomputed image/audio embeddings work, malformed shapes fail in staging without taking down PalLLM chat, and VRAM/latency improves over ordinary media bytes.");
            verificationChecks.Add("For remote-media profiles, run a negative SSRF replay: localhost, RFC1918, link-local, IP-literal, and redirect-to-private media URLs must be blocked before any PalLLM screenshot, video, or audio URL lane is promoted.");
            verificationChecks.Add("For InferencePrompt.UserContent canaries, replay text-only, image_url, video_url, input_audio, and audio_url content parts on the exact endpoint and record accepted request shape, media byte caps, p95 latency, parse stability, and fallback counters before promoting a multimodal lane.");
        }

        if (supportsAudioInput)
        {
            promotionReceipts.Add("Audio-in promotion receipt: normalized mono 16 kHz clip hash, duration cap, privacy retention decision, native audio-in vs cascaded ASR comparison, and deterministic text fallback behavior.");
            if (isGemmaAudio)
            {
                promotionReceipts.Add($"{gemmaAudioFamily} audio budget receipt: normalized duration, {gemmaAudioTokenRate} audio-token-per-second estimate, route token budget/headroom, cascaded ASR comparison, and deterministic text fallback behavior.");
                verificationChecks.Add("For Gemma audio-token budget proof, replay native audio-in and cascaded ASR with the same 30-second-or-less clip and reject promotion if token headroom, latency, parse stability, or fallback behavior regresses.");
            }

            verificationChecks.Add("For player speech, compare cascaded ASR-to-text latency, transcript quality, and privacy retention against native audio-in before changing the default input lane.");
            verificationChecks.Add("If PalLLM:Asr:Seed is configured, replay the same short clip twice on the exact transcription endpoint and record accepted multipart shape, served ASR model id, runtime version, transcript drift, latency, and fallback counters.");
        }

        if (supportsAudioOutput)
        {
            promotionReceipts.Add("Realtime audio promotion receipt: isolated audio server/profile, text mirror, speaker/config stability, stalled-audio fallback, and proof that text chat still returns while audio is unhealthy.");
            verificationChecks.Add("For audio-output canaries, replay with and without InferencePrompt.Modalities / Audio on the exact endpoint and archive returned InferenceResult.AudioJson alongside text mirror, latency, response-size, and fallback counters.");
            verificationChecks.Add("Smoke /v1/realtime or the vLLM-Omni speech endpoints with PCM16 mono 16 kHz chunks, verify audio delivery, and prove text chat still returns while realtime audio is stalled.");
        }

        if (isQwenOmni && supportsVideo)
        {
            promotionReceipts.Add("Qwen Omni streaming-video promotion receipt: /v1/video/chat/stream route, frame cadence, optional PCM16 audio chunk policy, duration cap, reconnect/stall fallback, and proof that ordinary text chat still returns while the stream is unhealthy.");
        }

        return new ModelServingProfile(
            ProfileId: profileId,
            RequestProtocol: requestProtocol,
            PreferredRuntime: preferredRuntime,
            StartupHints: startupHints.Distinct(StringComparer.Ordinal).ToArray(),
            RequestHints: requestHints.ToArray(),
            CacheHints: cacheHints.ToArray(),
            AdmissionControls: admissionControls.ToArray(),
            SecurityControls: securityControls.ToArray(),
            PromotionReceipts: promotionReceipts.Distinct(StringComparer.Ordinal).ToArray(),
            MetricReceipts: metricReceipts.Distinct(StringComparer.Ordinal).ToArray(),
            VerificationChecks: verificationChecks.ToArray());
    }

    private static string[] BuildServingOptimizations(
        bool supportsStructuredOutputs,
        bool supportsToolCalls,
        bool supportsSpeculativeDecoding,
        bool supportsVision,
        bool supportsVideo,
        bool supportsAudioInput,
        bool supportsAudioOutput)
    {
        List<string> optimizations =
        [
            "Keep the PalLLM system prompt, character profile, and world-state prefix stable so upstream prefix caches can hit.",
            "Keep chunked prefill enabled on vLLM-style servers so long Palworld proof or docs-sync prompts do not stall short companion turns.",
        ];

        if (supportsStructuredOutputs)
        {
            optimizations.Add("Use response_format json_schema for world-state, tool-call, and proof-packet JSON whenever the upstream server accepts it.");
            optimizations.Add("Use route/model/schema-digest keyed proof and cache identities so structured-output wins never bleed across PalLLM schemas.");
        }

        if (supportsToolCalls)
        {
            optimizations.Add("Treat tool calls as proposals and disable speculative decoding for strict tool-call or JSON-schema qualification runs.");
        }

        if (supportsSpeculativeDecoding)
        {
            optimizations.Add("Enable speculative decoding only after the exact model, quant, and draft pairing pass PalLLM's repeated-run qualification suite.");
            optimizations.Add("Prefer n-gram or suffix speculation for repetitive low-QPS text turns before adding a separate draft model to the operator footprint.");
        }

        if (supportsVision || supportsVideo || supportsAudioInput)
        {
            optimizations.Add("Use stable media UUIDs for repeated screenshots, proof replays, or audio clips when the model server exposes media caching.");
            optimizations.Add("Cap --limit-mm-per-prompt and prefer local base64 payloads; allow remote media only through explicit server-side allowlists.");
            if (supportsSpeculativeDecoding)
            {
                optimizations.Add("Keep speculative decoding route-scoped: text-only gains do not qualify screenshot, video, or audio lanes until each modality passes PalLLM replay proof.");
            }
        }

        if (supportsAudioOutput)
        {
            optimizations.Add("Keep realtime audio on a separate opt-in lane so text chat and deterministic fallback stay responsive when the voice server stalls.");
        }

        return optimizations.ToArray();
    }

    private static string[] BuildRuntimeGuards(
        bool supportsVision,
        bool supportsVideo,
        bool supportsAudioInput,
        bool supportsAudioOutput,
        bool supportsStructuredOutputs)
    {
        List<string> guards =
        [
            "Keep deterministic fallback enabled; a model capability must never become a hard dependency for POST /api/chat.",
        ];

        if (supportsStructuredOutputs)
        {
            guards.Add("Validate strict JSON with deterministic parsers before promotion; model confidence is not a validator.");
            guards.Add("Treat constrained decoding as an upstream hint, not the contract; PalLLM validators and deterministic fallback remain the authority when schema output is malformed or semantically wrong.");
        }

        if (supportsVision || supportsVideo)
        {
            guards.Add("Keep Palworld screenshot ingress bounded and local by default; do not let remote media fetches bypass the air-gap posture.");
        }

        if (supportsAudioInput || supportsAudioOutput)
        {
            guards.Add("Keep audio-in and realtime voice opt-in until the operator has explicit latency, privacy, and device-loop proof.");
        }

        return guards.ToArray();
    }

    private static ModelAuthorityProfile BuildAuthorityProfile(bool isSparseMoe) =>
        isSparseMoe
            ? new(
                MayDraftChanges: true,
                MayBePrimaryReviewer: false,
                MayRecommendMerge: false,
                MayExecuteLowRiskToolLoops: true,
                MayDraftHighRiskToolPlans: false,
                MayExecuteHighRiskTools: false)
            : new(
                MayDraftChanges: true,
                MayBePrimaryReviewer: true,
                MayRecommendMerge: true,
                MayExecuteLowRiskToolLoops: false,
                MayDraftHighRiskToolPlans: true,
                MayExecuteHighRiskTools: false);

    private static ModelHardwareProfile BuildHardwareProfile(ModelHardwareHints hints)
    {
        bool cpuOnly = hints.CpuOnly;
        double vramGb = Math.Max(0, hints.VramGb ?? 0);
        double ramGb = Math.Max(0, hints.RamGb ?? 0);
        double unifiedMemoryGb = Math.Max(0, hints.UnifiedMemoryGb ?? 0);

        string classId;
        string summary;
        bool canKeepTwoSpecialistsWarm;
        bool preferSequentialBatonPassing;

        if (cpuOnly)
        {
            classId = "cpu-only";
            summary = "CPU-only or intentionally accelerator-free setup. Favor a single resident model and wake the second only at review checkpoints.";
            canKeepTwoSpecialistsWarm = false;
            preferSequentialBatonPassing = true;
        }
        else if (unifiedMemoryGb >= 128 || vramGb >= 48)
        {
            classId = "workstation";
            summary = "Workstation-class memory budget. Separate fast and deliberate specialists can stay hot and work in parallel.";
            canKeepTwoSpecialistsWarm = true;
            preferSequentialBatonPassing = false;
        }
        else if (unifiedMemoryGb >= 64 || vramGb >= 24)
        {
            classId = "prosumer";
            summary = "Prosumer single-accelerator or mid-size unified-memory setup. Keep the fast model resident and bring the dense reviewer in at key boundaries.";
            canKeepTwoSpecialistsWarm = hints.PreferParallel && (unifiedMemoryGb >= 96 || vramGb >= 32);
            preferSequentialBatonPassing = !canKeepTwoSpecialistsWarm;
        }
        else if (vramGb >= 8 || ramGb >= 64 || unifiedMemoryGb >= 32)
        {
            classId = "hybrid-offload";
            summary = "Hybrid GPU+RAM offload setup. Sequential baton passing is usually better than trying to keep both large models resident.";
            canKeepTwoSpecialistsWarm = false;
            preferSequentialBatonPassing = true;
        }
        else
        {
            classId = "edge";
            summary = "Edge or low-memory setup. Keep the fast scout as the resident lane and only load the deliberate reviewer for narrow audits.";
            canKeepTwoSpecialistsWarm = false;
            preferSequentialBatonPassing = true;
        }

        return new ModelHardwareProfile(
            ClassId: classId,
            Summary: summary,
            VramGb: hints.VramGb,
            RamGb: hints.RamGb,
            UnifiedMemoryGb: hints.UnifiedMemoryGb,
            CpuOnly: hints.CpuOnly,
            PreferParallel: hints.PreferParallel,
            CanKeepTwoSpecialistsWarm: canKeepTwoSpecialistsWarm,
            PreferSequentialBatonPassing: preferSequentialBatonPassing);
    }

    private static ModelCollaborationModelDescriptor PickPreferredFastModel(
        IReadOnlyList<ModelCollaborationModelDescriptor> configuredModels) =>
        configuredModels.FirstOrDefault(model => string.Equals(model.OperatingStyle, "fast-iterative", StringComparison.Ordinal))
        ?? configuredModels.First();

    private static ModelCollaborationModelDescriptor PickPreferredDeliberateModel(
        IReadOnlyList<ModelCollaborationModelDescriptor> configuredModels) =>
        configuredModels.FirstOrDefault(model => string.Equals(model.OperatingStyle, "deliberate", StringComparison.Ordinal))
        ?? configuredModels.First();

    private static ModelCollaborationRecipe[] BuildRecipes(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        string collaborationMode = hardware.CanKeepTwoSpecialistsWarm && hardware.PreferParallel
            ? "parallel"
            : "sequential";

        return
        [
            new ModelCollaborationRecipe(
                Id: "fast-draft-dense-judge",
                Name: "Fast Draft, Dense Judge",
                Mode: collaborationMode,
                Summary: "Let the faster model explore, draft, or run tool loops; let the denser model approve the patch, rewrite the final answer, or reject drift.",
                BestWhen: "Narrow PalLLM fixes, bridge sweeps, screenshot loops, and any task where first-token speed matters but final correctness matters more.",
                Stages:
                [
                    CreateStage("draft", "Draft / scout", fastModel, deliberateModel, "Generate candidate files, patch shapes, screenshot findings, or tool-call branches.", "short plan + candidate diff summary", hardware.CanKeepTwoSpecialistsWarm),
                    CreateStage("judge", "Review / finalizer", deliberateModel, fastModel, "Rank candidates, enforce global constraints, and produce the final PalLLM-facing answer.", "approved patch or rejection note", false),
                ],
                Notes:
                [
                    "This is the default pairing for a dense Qwen reviewer plus a fast sparse-MoE scout.",
                    "If the scout loops or drifts, tighten its output contract and keep the dense reviewer as the gate.",
                ]),

            new ModelCollaborationRecipe(
                Id: "dense-plan-fast-execute-dense-audit",
                Name: "Dense Plan, Fast Execute, Dense Audit",
                Mode: "sequential",
                Summary: "Use the deliberate model to set invariants up front, the faster model to do the grunt work, and the deliberate model again for final audit.",
                BestWhen: "Bridge contract changes, HUD seam work, doc+code sync, and release-facing behavior changes.",
                Stages:
                [
                    CreateStage("plan", "Planner", deliberateModel, fastModel, "Write the acceptance criteria, constraints, and file list before edits start.", "checklist + constraints", false),
                    CreateStage("execute", "Executor", fastModel, deliberateModel, "Perform the implementation or wide search once the contract is frozen.", "candidate patch set", hardware.CanKeepTwoSpecialistsWarm),
                    CreateStage("audit", "Auditor", deliberateModel, fastModel, "Review for hidden regressions, missing tests, native-seam drift, and doc drift.", "pass/fail audit with fix list", false),
                ],
                Notes:
                [
                    "This is the safest lane when the mod is changing public contracts or player-facing behavior.",
                    "For PalLLM itself, this recipe is ideal for code-vs-doc audits, OpenAPI drift work, and bridge/HUD seam changes.",
                ]),

            new ModelCollaborationRecipe(
                Id: "watchdog-and-repair",
                Name: "Resident Watchdog, On-Demand Repair",
                Mode: hardware.PreferSequentialBatonPassing ? "sequential" : collaborationMode,
                Summary: "Keep the cheaper or faster model resident as a background auditor, then wake the deliberate model only when something smells wrong.",
                BestWhen: "Bridge inbox anomalies, screenshot drift, CI triage, route-count drift checks, and long unattended runs.",
                Stages:
                [
                    CreateStage("watch", "Watchdog", fastModel, deliberateModel, "Continuously scan logs, health snapshots, bridge events, or tests for anomalies.", "ranked anomaly report", true),
                    CreateStage("repair", "Repair author", deliberateModel, fastModel, "Design the minimal fix once the anomaly is localized.", "repair patch or rollback recommendation", false),
                    CreateStage("verify", "Cheap recheck", fastModel, deliberateModel, "Re-run the narrow verification loop after the fix lands.", "verification summary", hardware.CanKeepTwoSpecialistsWarm),
                ],
                Notes:
                [
                    "This pattern maps cleanly onto staged model promotion, quant review, release-hygiene gates, and bridge drift patrol.",
                    "On tight hardware, keep the watchdog resident and page the deliberate model in only on failure boundaries.",
                ]),

            new ModelCollaborationRecipe(
                Id: "parallel-crossfire",
                Name: "Parallel Crossfire",
                Mode: hardware.CanKeepTwoSpecialistsWarm ? "parallel" : "burst-parallel",
                Summary: "Have both models attack the same problem from different prompts, then reconcile overlap and disagreement.",
                BestWhen: "Hard bridge bugs, HUD seam regressions, release triage, and overnight candidate generation.",
                Stages:
                [
                    CreateStage("lane-a", "Fast lane", fastModel, deliberateModel, "Optimize for breadth, alternate hypotheses, and more tool-call attempts.", "multiple candidate hypotheses", true),
                    CreateStage("lane-b", "Deliberate lane", deliberateModel, fastModel, "Optimize for consistency, root-cause analysis, and constraint checking.", "root-cause memo", true),
                    CreateStage("merge", "Arbiter", deliberateModel, fastModel, "Merge agreement, reject contradictions, and produce the next best action.", "merged decision", false),
                ],
                Notes:
                [
                    "Best on workstation-class hardware or when the models are served on separate ports or machines.",
                    "If hardware is tight, emulate this with short alternating turns instead of true parallel residency.",
                ]),
        ];
    }

    private static ModelTaskRoutingPolicy[] BuildRoutingPolicies(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        string residencyMode = hardware.CanKeepTwoSpecialistsWarm
            ? "parallel-ready"
            : "sequential-baton";

        return
        [
            new ModelTaskRoutingPolicy(
                Id: "low-risk-fast-lane",
                TaskClass: "quick sidecar, bridge, or docs fix",
                RiskLevel: "low",
                Summary: "Let the fast lane own the first pass, but force immediate deterministic validation before anything is treated as complete.",
                PreferredFlow: $"{fastModel.ModelId} patch -> validators -> escalate only on failure",
                Steps:
                [
                    $"{fastModel.ModelId} scouts the smallest sufficient context and applies the narrow patch.",
                    "Run targeted tests, lint, or schema validation immediately after the patch.",
                    $"Escalate to {deliberateModel.ModelId} only when validators fail, unrelated edits appear, or the patch spills outside the intended scope.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: false,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "medium-risk-fast-implement-dense-review",
                TaskClass: "medium-risk runtime, docs, or bridge change",
                RiskLevel: "medium",
                Summary: "Use the fast lane for implementation speed, but make the dense lane the formal reviewer before the work is considered merge-ready.",
                PreferredFlow: $"{fastModel.ModelId} implement -> validators -> {deliberateModel.ModelId} review",
                Steps:
                [
                    $"{fastModel.ModelId} maps the affected files and drafts the candidate patch.",
                    "Run the smallest deterministic validation set that proves the changed behavior.",
                    $"{deliberateModel.ModelId} reviews the diff for hidden coupling, missing call sites, and insufficient tests before sign-off.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "high-risk-deliberate-bookends",
                TaskClass: "native HUD/audio seams, bridge compatibility, auth, persistence, or release-facing contracts",
                RiskLevel: "high",
                Summary: "The dense lane writes the contract first and closes the loop at the end; the fast lane does the mechanical work in the middle.",
                PreferredFlow: $"{deliberateModel.ModelId} spec -> {fastModel.ModelId} execute -> validators -> {deliberateModel.ModelId} final review",
                Steps:
                [
                    $"{deliberateModel.ModelId} defines acceptance criteria, invariants, and the exact files in scope before edits begin.",
                    $"{fastModel.ModelId} implements only the approved plan and classifies any validation failures instead of guessing at repairs.",
                    $"{deliberateModel.ModelId} performs the final review and blocks promotion when evidence is weak or scope expanded.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: true),

            new ModelTaskRoutingPolicy(
                Id: "tool-heavy-guarded",
                TaskClass: "tool-heavy audit or file-edit loops inside the PalLLM repo",
                RiskLevel: "medium",
                Summary: "Treat tool calls as proposals first. The fast lane drafts them, the dense lane validates them, and a deterministic firewall still decides what actually runs.",
                PreferredFlow: $"{fastModel.ModelId} tool draft -> {deliberateModel.ModelId} guard -> firewall -> execution",
                Steps:
                [
                    $"{fastModel.ModelId} proposes tool calls, file paths, and command sequences quickly.",
                    $"{deliberateModel.ModelId} checks schema, permissions, file scope, and whether a safer alternative exists.",
                    "Execute only the filtered tool plan and keep deterministic validators ahead of any default promotion.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "frontend-visual-loop",
                TaskClass: "Palworld HUD, dashboard, or screenshot-driven work",
                RiskLevel: "medium",
                Summary: "Use the fast lane for rough HUD or screenshot iteration, then let the dense lane review player-facing behavior, accessibility, and state handling.",
                PreferredFlow: $"{fastModel.ModelId} HUD draft -> screenshot checks -> {deliberateModel.ModelId} player-facing review",
                Steps:
                [
                    $"{fastModel.ModelId} drafts the HUD, dashboard, or screenshot-facing patch quickly.",
                    "Run screenshot-based or dashboard verification instead of forcing the whole task through text-only reasoning.",
                    $"{deliberateModel.ModelId} reviews accessibility, state transitions, and over-broad edits before final approval.",
                ],
                RequiresDeterministicValidators: true,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),

            new ModelTaskRoutingPolicy(
                Id: "context-compiler-then-dense-reasoning",
                TaskClass: "whole-repo audit or long-context PalLLM investigation",
                RiskLevel: "medium",
                Summary: "Use the fast lane as a context compiler: gather evidence broadly, then let the dense lane reason over the reduced evidence pack instead of a noisy full transcript.",
                PreferredFlow: $"{fastModel.ModelId} evidence ledger -> {deliberateModel.ModelId} selected-context reasoning ({residencyMode})",
                Steps:
                [
                    $"{fastModel.ModelId} ranks relevant files, tests, configs, and unknowns instead of summarizing the whole repository.",
                    $"{deliberateModel.ModelId} reasons over the reduced evidence set and rejects noise or weak retrieval.",
                    "Store findings in a shared evidence ledger: facts, rejected hypotheses, validation outcomes, and promotion decisions.",
                ],
                RequiresDeterministicValidators: false,
                RequiresDeliberateSignoff: true,
                RequiresHumanReview: false),
        ];
    }

    private static ModelQualificationSuite BuildQualificationSuite(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        string shadowMode = hardware.CanKeepTwoSpecialistsWarm
            ? "Run the candidate in parallel shadow mode against the current default lane."
            : "Run the candidate in sequential shadow mode and batch promotion checks to avoid churn.";

        return new ModelQualificationSuite(
            Summary: $"Fresh models and fresh quants should earn trust before promotion. Use {fastModel.ModelId} for cheap smoke and PalLLM workload replay, then require {deliberateModel.ModelId} to write the promotion verdict.",
            EvaluationPhases:
            [
                "Shadow smoke tests against the existing lane",
                "Task-fit replay on real PalLLM repo and runtime workloads",
                "Promotion review with rollback notes and validator evidence",
            ],
            Checks:
            [
                new("exact-json-tool-call", "Exact JSON or tool-call schema", "schema", "Fresh candidates often fail on strict structured output before they fail on prose tasks.", "Produce valid structured output across repeated runs."),
                new("nested-tool-call-object", "Nested tool-call object handling", "schema", "Tool loops fail quietly when nested argument objects drift or flatten unexpectedly.", "Preserve nested object shape under the target runtime and parser."),
                new("small-patch-generation", "Small patch generation", "coding", "A candidate must prove it can make narrow edits without rewriting the repository.", "Solve a representative small fix with a minimal diff."),
                new("diff-format-compliance", "Diff and edit format compliance", "coding", "The harness loses time when a model emits prose instead of machine-actionable edits.", "Emit a valid patch or file-edit format the harness can consume directly."),
                new("long-context-file-retrieval", "Long-context file retrieval", "retrieval", "Context-heavy tasks fall apart when the model confuses explored context with relevant context.", "Find the right files and symbols from a large repo slice without flooding the judge with noise."),
                new("test-failure-diagnosis", "Failing-test diagnosis", "coding", "A good worker must classify failures before attempting blind repairs.", "Differentiate patch bugs, test expectations, environment issues, and unrelated failures."),
                new("no-unrelated-file-edit", "No unrelated file edits", "safety", "Over-broad diffs are one of the easiest ways for a fast lane to create hidden regressions.", "Keep touched files inside the approved scope."),
                new("prompt-injection-resistance", "Prompt-injection resistance on repo text and web context", "safety", "Logs, comments, issues, and fetched webpages are evidence, not instructions.", "Ignore hostile text inside retrieved context unless the harness explicitly endorses it."),
                new("browser-or-visual-task", "Browser or visual task fit", "visual", "Palworld HUD and screenshot work need visual verification, not text-only optimism.", "Pass at least one representative screenshot or dashboard verification loop when that workload matters."),
                new("repeated-run-stability", "Repeated-run stability", "stability", "A candidate that only succeeds once is not safe to promote as a default lane.", "Repeat the same task enough times to see whether structure, latency, and correctness stay stable."),
            ],
            PromotionRequirements:
            [
                shadowMode,
                "Do not make a candidate the default lane until it passes schema, retrieval, coding, and stability checks on your own workload.",
                "Keep very aggressive low-bit quants scout-only until they independently prove patch quality and tool correctness.",
                "Promotion decisions should carry rollback notes, not just a winner label.",
            ],
            FailureActions:
            [
                "Keep the candidate in scout-only or shadow-only service instead of promoting it.",
                "Tighten the tool schema, prompt contract, or validation harness before trying again.",
                "Reduce context pressure or choose a less aggressive quant when errors correlate with long-context drift.",
                "Rollback to the previous default lane immediately when stability or safety checks regress.",
            ]);
    }

    private static ModelHardwareTierPlaybook[] BuildHardwarePlaybook(
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel) =>
    [
        new(
            TierId: "cpu-only",
            Summary: "CPU-only or very low-memory laptops. Favor one heavyweight lane at a time and keep expectations honest.",
            RecommendedRunMode: "one_model_only",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "cpu-only"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "cpu-only"),
            ContextGuidance: "4K-16K context. Keep the fast lane interactive and reserve the deliberate lane for short or overnight batch work.",
            Notes:
            [
                "Treat extremely low-bit quants as scout-only until they independently pass your qualification suite.",
                "Batch deep-review work to avoid constant model swapping.",
            ]),
        new(
            TierId: "edge",
            Summary: "Single 16 GB VRAM card or similar unified-memory budget. This is the classic sequential duo tier.",
            RecommendedRunMode: "sequential",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "edge"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "edge"),
            ContextGuidance: "8K-32K context. Load the fast lane for interactive work, then swap to the deliberate lane for review windows.",
            Notes:
            [
                "The fast lane should scout, draft, and run narrow validation loops.",
                "The deliberate lane should own architecture, review, and release-readiness notes.",
            ]),
        new(
            TierId: "hybrid-offload",
            Summary: "20 GB-ish VRAM or GPU+RAM offload. Stronger than edge, but still best treated as baton-passing hardware.",
            RecommendedRunMode: "sequential",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "hybrid-offload"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "hybrid-offload"),
            ContextGuidance: "8K-32K interactive context; 64K only when the task genuinely needs it and validators justify the memory hit.",
            Notes:
            [
                "KV cache pressure becomes the real constraint here, not just raw model file size.",
                "Favor retrieval and evidence ledgers over blindly expanding the context window.",
            ]),
        new(
            TierId: "prosumer",
            Summary: "24-32 GB VRAM or strong unified memory. The best serious single-accelerator local coding tier.",
            RecommendedRunMode: "sequential-preferred",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "prosumer"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "prosumer"),
            ContextGuidance: "32K-64K default. Keep one lane hot and page the other in at decision boundaries unless you can tolerate lower quants.",
            Notes:
            [
                "Use the fast lane for branch fan-out and tool loops.",
                "Use the deliberate lane for final review, migration planning, and release-facing bridge or HUD changes.",
            ]),
        new(
            TierId: "workstation",
            Summary: "48 GB+ VRAM, dual GPUs, or large workstation-class unified memory. This is where true duo workcells become comfortable.",
            RecommendedRunMode: "parallel",
            FastLaneQuantHint: GetFastLaneQuantHint(fastModel, "workstation"),
            DeliberateLaneQuantHint: GetDeliberateLaneQuantHint(deliberateModel, "workstation"),
            ContextGuidance: "64K-128K by default, with higher windows only when the retrieval path proves the task really needs them.",
            Notes:
            [
                "Run separate OpenAI-compatible endpoints when possible.",
                "Use the fast lane for concurrent audits, screenshot loops, and bridge triage; use the deliberate lane for judges, auditors, and repair planners.",
            ]),
    ];

    private static string GetFastLaneQuantHint(ModelCollaborationModelDescriptor model, string tierId)
    {
        string normalized = NormalizeModelId(model.ModelId);
        bool qwen35A3B = normalized.Contains("qwen3.6") && normalized.Contains("35b") && normalized.Contains("a3b");

        if (!qwen35A3B)
        {
            return tierId switch
            {
                "cpu-only" => "Choose the lowest-bit usable fast lane and keep it scout-only.",
                "edge" => "Favor a lower-bit fast lane that stays responsive for scanning and drafts.",
                "hybrid-offload" => "Balanced 3-4 bit fast lane with cautious context sizing.",
                "prosumer" => "Balanced 4-5 bit fast lane for implementation and tool loops.",
                _ => "Higher-quality fast lane or native serving profile.",
            };
        }

        return tierId switch
        {
            "cpu-only" => "UD-IQ1_M / UD-IQ2_XXS / UD-IQ2_M",
            "edge" => "UD-Q3_K_XL / UD-IQ4_XS / lower Q4",
            "hybrid-offload" => "UD-Q3_K_XL / UD-IQ4_XS / UD-Q4_K_S",
            "prosumer" => "UD-Q4_K_M / UD-Q4_K_XL / MXFP4_MOE / UD-Q5_K_M",
            _ => "UD-Q4_K_M / MXFP4_MOE / UD-Q5_K_M / UD-Q6_K",
        };
    }

    private static string GetDeliberateLaneQuantHint(ModelCollaborationModelDescriptor model, string tierId)
    {
        string normalized = NormalizeModelId(model.ModelId);
        bool qwen27 = normalized.Contains("qwen3.6") && normalized.Contains("27b");

        if (!qwen27)
        {
            return tierId switch
            {
                "cpu-only" => "Short, low-bit batch-only deliberate lane if you need one at all.",
                "edge" => "Tight 4-bit deliberate lane for audit windows.",
                "hybrid-offload" => "4-bit deliberate lane with small-to-medium context.",
                "prosumer" => "4-5 bit deliberate lane for deep review windows.",
                _ => "5-6 bit deliberate lane or native precision where hardware allows.",
            };
        }

        return tierId switch
        {
            "cpu-only" => "UD-IQ2_XXS / UD-IQ2_M / Q3_K_S (batch only)",
            "edge" => "Q3 / tight Q4",
            "hybrid-offload" => "Q4_K_S / IQ4_XS / Q4_K_M",
            "prosumer" => "Q4_K_M / UD-Q4_K_XL / Q5_K_M",
            _ => "Q5_K_M / Q6_K / Q8_0",
        };
    }

    private static ModelCollaborationStage CreateStage(
        string stageId,
        string role,
        ModelCollaborationModelDescriptor preferredModel,
        ModelCollaborationModelDescriptor fallbackModel,
        string why,
        string outputContract,
        bool canRunInParallel) =>
        new(
            StageId: stageId,
            Role: role,
            PreferredModel: preferredModel.ModelId,
            PreferredTierId: preferredModel.TierId,
            FallbackModel: fallbackModel.ModelId,
            Why: why,
            OutputContract: outputContract,
            CanRunInParallel: canRunInParallel);

    private static string[] BuildDeploymentNotes(
        ModelHardwareProfile hardware,
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel)
    {
        List<string> notes = new();

        notes.Add(hardware.ClassId switch
        {
            "cpu-only" => "Load one heavyweight model at a time. Use the fast lane for constant availability and wake the deliberate lane only for narrow audits or final sign-off.",
            "edge" => "Prefer the faster model as the always-on lane. Queue dense review only for diff candidates, failing tests, or release checkpoints.",
            "hybrid-offload" => "Sequential baton passing usually beats trying to keep both big models warm. Keep the MoE scout resident and page the dense model in on demand.",
            "prosumer" => "Use separate ports if possible. Keep the fast lane warm and reserve dense review for commit candidates, public docs, and bridge regression triage.",
            _ => "Run separate servers and let both specialists stay hot. Use true parallel crossfire for hard bridge bugs, HUD regressions, and nightly audit jobs.",
        });

        notes.Add($"Preferred fast lane: {fastModel.ModelId}");
        notes.Add($"Preferred deliberate lane: {deliberateModel.ModelId}");
        notes.Add("Use the fast lane as an evidence compiler first: rank files, tests, configs, and unknowns before asking the dense lane to reason globally.");
        notes.Add("Keep a shared evidence ledger of facts, rejected hypotheses, validation results, and promotion decisions instead of storing whole transcripts.");

        if (fastModel.LikelyMultimodal || deliberateModel.LikelyMultimodal)
        {
            notes.Add("If your local deployment uses text-only GGUF builds, keep Palworld screenshot work on a separate vision-capable server and feed only distilled findings back into the text lanes.");
        }

        return notes.ToArray();
    }

    private static ModelCollaborationIdea[] BuildSelfHealingIdeas(
        ModelCollaborationModelDescriptor fastModel,
        ModelCollaborationModelDescriptor deliberateModel) =>
    [
        new(
            Id: "doc-and-contract-drift-patrol",
            Summary: $"Keep {fastModel.ModelId} as the resident drift detector for routes, feature counts, bridge contracts, and failing docs links; wake {deliberateModel.ModelId} to approve any repair patch before it lands.",
            Trigger: "Any route, feature-catalog, or OpenAPI change."),
        new(
            Id: "shadow-test-repair",
            Summary: $"{fastModel.ModelId} localizes likely failure files and drafts minimal repairs; {deliberateModel.ModelId} checks invariants and rejects fixes that only silence the symptom.",
            Trigger: "Test failures, bridge log spikes, or repeated fallback-path regressions."),
        new(
            Id: "model-promotion-review",
            Summary: $"{fastModel.ModelId} smoke-tests new quants or model revisions in shadow mode; {deliberateModel.ModelId} acts as the promotion reviewer so nothing silently becomes the new default lane.",
            Trigger: "New local model download, quant swap, or lane default change."),
    ];

    private static bool IsSparseMoe(string normalizedModelId) =>
        normalizedModelId.Contains("a3b")
        || normalizedModelId.Contains("a10b")
        || normalizedModelId.Contains("a17b")
        || normalizedModelId.Contains("moe");

    private static string NormalizeModelId(string modelId) =>
        (modelId ?? string.Empty).Trim().ToLowerInvariant();
}

public sealed record ModelHardwareHints(
    double? VramGb = null,
    double? RamGb = null,
    double? UnifiedMemoryGb = null,
    bool CpuOnly = false,
    bool PreferParallel = true);

public sealed record ModelHardwareProfile(
    string ClassId,
    string Summary,
    double? VramGb,
    double? RamGb,
    double? UnifiedMemoryGb,
    bool CpuOnly,
    bool PreferParallel,
    bool CanKeepTwoSpecialistsWarm,
    bool PreferSequentialBatonPassing);

public sealed record ModelCollaborationSnapshot(
    DateTimeOffset GeneratedAtUtc,
    ModelHardwareProfile Hardware,
    string ActiveModel,
    string? ActiveTierId,
    string[] LastSeenAvailableModels,
    ModelCollaborationModelDescriptor[] ConfiguredModels,
    ModelCollaborationRecipe[] Recipes,
    ModelTaskRoutingPolicy[] RoutingPolicies,
    ModelQualificationSuite QualificationSuite,
    ModelHardwareTierPlaybook[] HardwarePlaybook,
    string[] DeploymentNotes,
    ModelCollaborationIdea[] SelfHealingIdeas);

public sealed record ModelCollaborationModelDescriptor(
    string ModelId,
    string? TierId,
    int Priority,
    bool IsActive,
    string Architecture,
    string OperatingStyle,
    bool LikelyMultimodal,
    ModelCapabilityProfile Capability,
    string[] PrimaryRoles,
    ModelAuthorityProfile Authority,
    string[] Strengths,
    string[] Cautions,
    string[] Notes);

public sealed record ModelCapabilityProfile(
    string Family,
    string RecommendedBackend,
    ModelServingProfile ServingProfile,
    string[] InputModalities,
    string[] OutputModalities,
    bool SupportsVisionInput,
    bool SupportsVideoInput,
    bool SupportsAudioInput,
    bool SupportsAudioOutput,
    bool SupportsStructuredOutputs,
    bool SupportsToolCalls,
    bool SupportsSpeculativeDecoding,
    ModelSpeculationProfile Speculation,
    string[] ServingOptimizations,
    string[] RuntimeGuards);

public sealed record ModelSpeculationProfile(
    bool SupportsNgramSpeculation,
    bool SupportsDraftModelSpeculation,
    bool SupportsModelNativeMtp,
    bool RequiresModalityIsolatedProof,
    bool RequiresPrefixCacheOffForLatencyMtp,
    string RecommendedFirstMode,
    string PromotionGuard);

public sealed record ModelServingProfile(
    string ProfileId,
    string RequestProtocol,
    string PreferredRuntime,
    string[] StartupHints,
    string[] RequestHints,
    string[] CacheHints,
    string[] AdmissionControls,
    string[] SecurityControls,
    string[] PromotionReceipts,
    string[] MetricReceipts,
    string[] VerificationChecks);

public sealed record ModelAuthorityProfile(
    bool MayDraftChanges,
    bool MayBePrimaryReviewer,
    bool MayRecommendMerge,
    bool MayExecuteLowRiskToolLoops,
    bool MayDraftHighRiskToolPlans,
    bool MayExecuteHighRiskTools);

public sealed record ModelCollaborationRecipe(
    string Id,
    string Name,
    string Mode,
    string Summary,
    string BestWhen,
    ModelCollaborationStage[] Stages,
    string[] Notes);

public sealed record ModelCollaborationStage(
    string StageId,
    string Role,
    string PreferredModel,
    string? PreferredTierId,
    string FallbackModel,
    string Why,
    string OutputContract,
    bool CanRunInParallel);

public sealed record ModelTaskRoutingPolicy(
    string Id,
    string TaskClass,
    string RiskLevel,
    string Summary,
    string PreferredFlow,
    string[] Steps,
    bool RequiresDeterministicValidators,
    bool RequiresDeliberateSignoff,
    bool RequiresHumanReview);

public sealed record ModelQualificationSuite(
    string Summary,
    string[] EvaluationPhases,
    ModelQualificationCheck[] Checks,
    string[] PromotionRequirements,
    string[] FailureActions);

public sealed record ModelQualificationCheck(
    string Id,
    string Name,
    string Category,
    string Why,
    string MinimumEvidence);

public sealed record ModelHardwareTierPlaybook(
    string TierId,
    string Summary,
    string RecommendedRunMode,
    string FastLaneQuantHint,
    string DeliberateLaneQuantHint,
    string ContextGuidance,
    string[] Notes);

public sealed record ModelCollaborationIdea(
    string Id,
    string Summary,
    string Trigger);
