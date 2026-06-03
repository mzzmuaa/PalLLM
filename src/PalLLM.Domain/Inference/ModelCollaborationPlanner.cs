using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

public sealed partial class ModelCollaborationPlanner
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
        bool richMediaOrNonGguf = !isGguf || supportsVideo || supportsAudioOutput;
        bool supportsModelNativeMtp = richMediaOrNonGguf && !isEmbedding && (isQwen36 || isGemma4);

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
            return "llama.cpp llama-server with libmtmd + a matching mmproj (GGUF multimodal)";
        }

        if (isGguf)
        {
            // Pass 436: llama.cpp is the only local engine. The alt-engine names
            // (vLLM / LM Studio / Ollama) were dropped from the suggested-runtime
            // list with the rest of the purge.
            return "llama.cpp llama-server (the bundled local engine)";
        }

        // Not a local GGUF: route through the OpenAI-compatible cloud escape
        // path. PalLLM ships no non-GGUF local engine.
        return multimodal
            ? "an OpenAI-compatible cloud API via pal connect cloud (escape path for non-GGUF multimodal models)"
            : "an OpenAI-compatible cloud API via pal connect cloud (escape path)";
    }
}
