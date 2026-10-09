using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

/// <summary>
/// The five roles a local-first AI mesh needs to cover the 2035
/// architecture pattern from <c>docs/ARCHITECTURE.md</c> / the direction
/// brief: one role per responsibility so operators can pick the best
/// model for each slot without collapsing them all into a single
/// "inference endpoint".
///
/// <para>Roles are metadata-only today: the registry records what an
/// operator has configured and reports coverage gaps. Future passes can
/// layer role-aware routing on top (fast worker draft → dense judge
/// audit → media generator → validator gate) without changing the
/// operator-facing configuration shape.</para>
/// </summary>
public enum ModelRole
{
    /// <summary>Edge perception: screen/audio/UI understanding, fast
    /// multimodal audits, accessibility. Gemma 4 12B class.</summary>
    Edge = 0,

    /// <summary>Fast worker: drafts, tool calls, prompt variants,
    /// branch generation. Qwen3.5 9B class.</summary>
    Worker = 1,

    /// <summary>Dense judge: audits, specs, repair planning, proof
    /// packets, promotion decisions. Gemma 4 12B class.</summary>
    Judge = 2,

    /// <summary>Media generator: images, video, audio, textures.
    /// Off by default for PalLLM's current local mesh.</summary>
    Media = 3,

    /// <summary>Deterministic validator: tests, accessibility checks,
    /// performance probes, policy gates. Not a model — a gate.</summary>
    Validator = 4,
}

/// <summary>
/// Reads the configured <see cref="ModelRoleBinding"/> list from
/// <see cref="PalLlmOptions.ModelRoles"/> and produces a
/// <see cref="ModelRoleCoverage"/> snapshot describing which roles are
/// bound, which are missing, and which combinations would give the
/// operator the strongest local-first mesh given their current
/// hardware posture.
///
/// <para>The registry deliberately does not mutate runtime inference
/// behaviour. Its job is to make the mesh architecture legible to
/// operators, AI clients, and validators so that future routing can
/// target roles by name instead of model-id strings.</para>
/// </summary>
public sealed class ModelRoleRegistry
{
    private readonly PalLlmOptions _options;

    public ModelRoleRegistry(PalLlmOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public ModelRoleCoverage GetCoverage()
    {
        ModelRoleBinding[] bindings = _options.ModelRoles
            .Where(b => b is not null)
            .ToArray();

        List<ModelRoleSlot> slots = new();
        foreach (ModelRole role in Enum.GetValues<ModelRole>())
        {
            ModelRoleBinding[] forRole = bindings
                .Where(b => b.Role == role)
                .ToArray();

            ModelRoleBinding? active = forRole.FirstOrDefault(b => b.Enabled);
            slots.Add(new ModelRoleSlot(
                Role: role.ToString(),
                Description: DescribeRole(role),
                IsConfigured: forRole.Length > 0,
                IsActive: active is not null,
                Bindings: forRole,
                Recommendation: RecommendForRole(role)));
        }

        string[] criticalGaps = slots
            .Where(s => !s.IsActive && IsCriticalRole(s.Role))
            .Select(s => s.Role)
            .ToArray();

        return new ModelRoleCoverage(
            Slots: slots,
            ActiveBindings: bindings.Count(b => b.Enabled),
            TotalBindings: bindings.Length,
            CriticalGaps: criticalGaps,
            PairingPattern: SuggestPairingPattern(slots));
    }

    private static bool IsCriticalRole(string role)
    {
        // Edge + Worker are the two slots a local-first mesh must have
        // before any of the background / creative work becomes useful.
        // Judge + Media + Validator are graceful upgrades.
        return string.Equals(role, nameof(ModelRole.Edge), StringComparison.Ordinal)
            || string.Equals(role, nameof(ModelRole.Worker), StringComparison.Ordinal);
    }

    private static string DescribeRole(ModelRole role) => role switch
    {
        ModelRole.Edge => "Edge perception — local screen / audio / UI understanding, accessibility, fast multimodal audits. Gemma 4 12B class.",
        ModelRole.Worker => "Fast worker — drafts, tool calls, prompt variants, branch generation. Qwen3.5 9B class.",
        ModelRole.Judge => "Smart judge — audits, specs, repair planning, proof-packet author, promotion decisions. Gemma 4 12B class.",
        ModelRole.Media => "Media generator — images / video / audio / textures. Off by default for PalLLM's current local mesh.",
        ModelRole.Validator => "Deterministic validator — accessibility / performance / policy / engine-import gates. Not a model; a gate.",
        _ => string.Empty,
    };

    private static string RecommendForRole(ModelRole role) => role switch
    {
        ModelRole.Edge => "Point at the Gemma 4 12B lane when screenshot or UI/audio proof is enabled.",
        ModelRole.Worker => "Point at the Qwen3.5 9B fast lane. Generates drafts, tool calls, and variants.",
        ModelRole.Judge => "Point at the Gemma 4 12B smart lane. Used sparingly for audits and final decisions.",
        ModelRole.Media => "Keep media generation unbound unless a PalLLM-owned asset pipeline is explicitly added and licensed.",
        ModelRole.Validator => "Attach deterministic check scripts (schema, accessibility, FPS, policy). Not a model endpoint — a list of validators the mesh consults before promoting a change.",
        _ => string.Empty,
    };

    private static string SuggestPairingPattern(IReadOnlyList<ModelRoleSlot> slots)
    {
        bool edge = slots.Any(s => s.Role == nameof(ModelRole.Edge) && s.IsActive);
        bool worker = slots.Any(s => s.Role == nameof(ModelRole.Worker) && s.IsActive);
        bool judge = slots.Any(s => s.Role == nameof(ModelRole.Judge) && s.IsActive);

        if (edge && worker && judge)
        {
            return "Full local mesh: Edge sees → Worker drafts → Judge audits. This is the 2035 recommended pattern for 24GB+ workstations.";
        }
        if (worker && judge)
        {
            return "Worker + Judge only: add an Edge model when you want screen/audio understanding. Today, a fast-worker + dense-judge pair covers most drafting + audit loops.";
        }
        if (edge && worker)
        {
            return "Edge + Worker only: add a dense Judge model when you want a second-pass auditor. Good setup for 8–16GB edge devices.";
        }
        if (worker)
        {
            return "Worker only: deterministic fallback + direct live inference. Add an Edge model for perception or a Judge for audit as hardware allows.";
        }
        if (edge)
        {
            return "Edge only: perception without a drafting worker. Add a fast Worker for drafting replies and tool calls.";
        }
        return "No roles configured. The deterministic fallback director is still the always-available path; add roles as you plug in local model endpoints.";
    }
}

public sealed record ModelRoleCoverage(
    IReadOnlyList<ModelRoleSlot> Slots,
    int ActiveBindings,
    int TotalBindings,
    IReadOnlyList<string> CriticalGaps,
    string PairingPattern);

public sealed record ModelRoleSlot(
    string Role,
    string Description,
    bool IsConfigured,
    bool IsActive,
    IReadOnlyList<ModelRoleBinding> Bindings,
    string Recommendation);
