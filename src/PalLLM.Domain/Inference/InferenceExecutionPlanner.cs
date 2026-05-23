using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain.Inference;

public sealed class InferenceExecutionPlanner
{
    private readonly PalLlmOptions _options;

    public InferenceExecutionPlanner(PalLlmOptions options)
    {
        _options = options;
    }

    public InferenceExecutionProfile Plan(
        PalTaskProfile taskProfile,
        ChatRequest request,
        string? activeModelId)
    {
        ArgumentNullException.ThrowIfNull(taskProfile);
        ArgumentNullException.ThrowIfNull(request);

        string model = string.IsNullOrWhiteSpace(activeModelId)
            ? _options.Inference.Model
            : activeModelId;
        bool fastLane = IsFastIterativeModel(model);
        bool reactive = taskProfile.Kind == PalTaskKind.ReactiveBark;
        bool baseAdvisor = taskProfile.Kind == PalTaskKind.BaseAdvisor;
        bool packAuthoring = taskProfile.Kind == PalTaskKind.PackAuthoring;
        bool likelyCreative = packAuthoring
            || ContainsAny(request.TaskTag, "story", "creative", "brainstorm", "lore", "banter", "flavor")
            || ContainsAny(request.UserMessage, "story", "creative", "brainstorm", "lore", "banter", "flavor");
        bool likelyDeliberate = baseAdvisor
            || request.Priority == PalTaskPriority.High
            || ContainsAny(request.TaskTag, "audit", "plan", "review", "repair", "compare", "why", "strategy")
            || ContainsAny(request.UserMessage, "audit", "plan", "review", "repair", "compare", "why", "strategy", "route", "architecture");

        if (fastLane)
        {
            if (reactive)
            {
                return Create(
                    request,
                    model,
                    profileId: "fast-reactive",
                    lane: "fast-iterative",
                    temperature: 0.45f,
                    maxTokens: Math.Min(taskProfile.DefaultMaxTokens, 96),
                    topP: 0.72f,
                    presencePenalty: 0.45f,
                    enableThinking: false,
                    preserveThinking: false,
                    allowLiveVisionAugmentation: false,
                    chatVisionMaxTokens: 0,
                    promptBudget: new PromptContextBudget(
                        MaxPromptChars: 2_200,
                        MaxVisualContextChars: 120,
                        MaxKnownBases: 2,
                        MaxNearbyHostiles: 2,
                        MaxNearbyResources: 2,
                        MaxRecentEvents: 3,
                        MaxCharacterTraits: 3,
                        MaxCharacterSkills: 2,
                        MaxLoreTraits: 3,
                        MaxMemorySnippets: 2));
            }

            if (likelyCreative)
            {
                return Create(
                    request,
                    model,
                    profileId: "fast-creative",
                    lane: "fast-iterative",
                    temperature: 0.82f,
                    maxTokens: Clamp(taskProfile.DefaultMaxTokens, 240, 420),
                    topP: 0.92f,
                    presencePenalty: 1.1f,
                    enableThinking: true,
                    preserveThinking: false,
                    allowLiveVisionAugmentation: true,
                    chatVisionMaxTokens: 96,
                    promptBudget: new PromptContextBudget(
                        MaxPromptChars: 5_600,
                        MaxVisualContextChars: 220,
                        MaxKnownBases: 4,
                        MaxNearbyHostiles: 4,
                        MaxNearbyResources: 4,
                        MaxRecentEvents: 5,
                        MaxCharacterTraits: 6,
                        MaxCharacterSkills: 5,
                        MaxLoreTraits: 6,
                        MaxMemorySnippets: 4));
            }

            if (likelyDeliberate)
            {
                return Create(
                    request,
                    model,
                    profileId: "fast-deliberate",
                    lane: "fast-iterative",
                    temperature: 0.55f,
                    maxTokens: Clamp(taskProfile.DefaultMaxTokens, 220, 320),
                    topP: 0.82f,
                    presencePenalty: 0.75f,
                    enableThinking: true,
                    preserveThinking: false,
                    allowLiveVisionAugmentation: true,
                    chatVisionMaxTokens: 80,
                    promptBudget: new PromptContextBudget(
                        MaxPromptChars: 4_800,
                        MaxVisualContextChars: 180,
                        MaxKnownBases: 4,
                        MaxNearbyHostiles: 4,
                        MaxNearbyResources: 4,
                        MaxRecentEvents: 5,
                        MaxCharacterTraits: 5,
                        MaxCharacterSkills: 4,
                        MaxLoreTraits: 5,
                        MaxMemorySnippets: 4));
            }

            return Create(
                request,
                model,
                profileId: "fast-interactive",
                lane: "fast-iterative",
                temperature: 0.65f,
                maxTokens: Clamp(taskProfile.DefaultMaxTokens, 140, 220),
                topP: 0.85f,
                presencePenalty: 0.9f,
                enableThinking: false,
                preserveThinking: false,
                allowLiveVisionAugmentation: true,
                chatVisionMaxTokens: 64,
                promptBudget: new PromptContextBudget(
                    MaxPromptChars: 3_200,
                    MaxVisualContextChars: 160,
                    MaxKnownBases: 3,
                    MaxNearbyHostiles: 3,
                    MaxNearbyResources: 3,
                    MaxRecentEvents: 4,
                    MaxCharacterTraits: 4,
                    MaxCharacterSkills: 3,
                    MaxLoreTraits: 4,
                    MaxMemorySnippets: 3));
        }

        if (likelyCreative)
        {
            return Create(
                request,
                model,
                profileId: "dense-creative",
                lane: "deliberate",
                temperature: 0.72f,
                maxTokens: Clamp(taskProfile.DefaultMaxTokens, 320, 520),
                topP: 0.9f,
                presencePenalty: 0.95f,
                enableThinking: true,
                preserveThinking: true,
                allowLiveVisionAugmentation: true,
                chatVisionMaxTokens: 112,
                promptBudget: new PromptContextBudget(
                    MaxPromptChars: 9_000,
                    MaxVisualContextChars: 256,
                    MaxKnownBases: 5,
                    MaxNearbyHostiles: 5,
                    MaxNearbyResources: 5,
                    MaxRecentEvents: 6,
                    MaxCharacterTraits: 8,
                    MaxCharacterSkills: 6,
                    MaxLoreTraits: 8,
                    MaxMemorySnippets: 5));
        }

        if (likelyDeliberate || reactive)
        {
            return Create(
                request,
                model,
                profileId: "dense-deliberate",
                lane: "deliberate",
                temperature: 0.45f,
                maxTokens: Clamp(taskProfile.DefaultMaxTokens, 220, 420),
                topP: 0.76f,
                presencePenalty: 0.55f,
                enableThinking: true,
                preserveThinking: true,
                allowLiveVisionAugmentation: true,
                chatVisionMaxTokens: 96,
                promptBudget: new PromptContextBudget(
                    MaxPromptChars: 8_000,
                    MaxVisualContextChars: 220,
                    MaxKnownBases: 5,
                    MaxNearbyHostiles: 5,
                    MaxNearbyResources: 5,
                    MaxRecentEvents: 6,
                    MaxCharacterTraits: 7,
                    MaxCharacterSkills: 6,
                    MaxLoreTraits: 7,
                    MaxMemorySnippets: 5));
        }

        return Create(
            request,
            model,
            profileId: "dense-interactive",
            lane: "deliberate",
            temperature: 0.6f,
            maxTokens: Clamp(taskProfile.DefaultMaxTokens, 160, 260),
            topP: 0.82f,
            presencePenalty: 0.8f,
            enableThinking: true,
            preserveThinking: false,
            allowLiveVisionAugmentation: true,
            chatVisionMaxTokens: 80,
            promptBudget: new PromptContextBudget(
                MaxPromptChars: 5_200,
                MaxVisualContextChars: 180,
                MaxKnownBases: 4,
                MaxNearbyHostiles: 4,
                MaxNearbyResources: 4,
                MaxRecentEvents: 5,
                MaxCharacterTraits: 5,
                MaxCharacterSkills: 4,
                MaxLoreTraits: 5,
                MaxMemorySnippets: 4));
    }

    private static InferenceExecutionProfile Create(
        ChatRequest request,
        string model,
        string profileId,
        string lane,
        float temperature,
        int maxTokens,
        float? topP,
        float? presencePenalty,
        bool? enableThinking,
        bool? preserveThinking,
        bool allowLiveVisionAugmentation,
        int chatVisionMaxTokens,
        PromptContextBudget promptBudget)
    {
        return new InferenceExecutionProfile(
            ProfileId: profileId,
            Lane: lane,
            Model: model,
            Temperature: request.Temperature ?? temperature,
            MaxTokens: request.MaxTokens ?? maxTokens,
            TopP: topP,
            PresencePenalty: presencePenalty,
            EnableThinking: enableThinking,
            PreserveThinking: preserveThinking,
            AllowLiveVisionAugmentation: allowLiveVisionAugmentation,
            ChatVisionMaxTokens: chatVisionMaxTokens,
            PromptBudget: promptBudget);
    }

    private static bool IsFastIterativeModel(string? modelId)
    {
        string normalized = Normalize(modelId);
        return normalized.Contains("a3b", StringComparison.Ordinal)
            || normalized.Contains("moe", StringComparison.Ordinal)
            || normalized.Contains("mixtral", StringComparison.Ordinal)
            || normalized.Contains("deepseek-v3", StringComparison.Ordinal);
    }

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Trim().ToLowerInvariant();

    private static bool ContainsAny(string? value, params string[] tokens)
    {
        string normalized = Normalize(value);
        if (normalized.Length == 0)
        {
            return false;
        }

        foreach (string token in tokens)
        {
            if (normalized.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}

public sealed record InferenceExecutionProfile(
    string ProfileId,
    string Lane,
    string Model,
    float Temperature,
    int MaxTokens,
    float? TopP,
    float? PresencePenalty,
    bool? EnableThinking,
    bool? PreserveThinking,
    bool AllowLiveVisionAugmentation,
    int ChatVisionMaxTokens,
    PromptContextBudget PromptBudget);

public sealed record PromptContextBudget(
    int MaxPromptChars,
    int MaxVisualContextChars,
    int MaxKnownBases,
    int MaxNearbyHostiles,
    int MaxNearbyResources,
    int MaxRecentEvents,
    int MaxCharacterTraits,
    int MaxCharacterSkills,
    int MaxLoreTraits,
    int MaxMemorySnippets);
