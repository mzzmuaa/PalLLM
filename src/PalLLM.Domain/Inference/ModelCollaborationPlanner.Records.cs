// Model-collaboration snapshot DTO records (the read-only shapes projected by ModelCollaborationPlanner).
using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

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
