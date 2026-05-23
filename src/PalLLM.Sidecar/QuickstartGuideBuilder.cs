using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Builds the <c>/api/quickstart</c> payload. Where <c>/api/describe</c> is
/// the STATIC manifest of what this sidecar is, <c>/api/quickstart</c> is the
/// DYNAMIC "what should I do right now?" guidance computed from the live
/// <see cref="RuntimeHealth"/> snapshot and <see cref="PalLlmOptions"/>.
///
/// <para>Designed so either a human operator or an AI assistant can call
/// this once and know exactly what to do next without scraping the full
/// dashboard, reading the opt-in matrix, or guessing. Each step carries a
/// short label, a plain-English reason, the concrete action, and how to
/// verify it worked — a structure that matches 2026 best-practice
/// "machine-and-human-readable next-action guidance" for local-first tools.</para>
/// </summary>
internal static class QuickstartGuideBuilder
{
    public static QuickstartGuide Build(PalLlmRuntime runtime, PalLlmOptions options)
        => Build(runtime, options, roleRegistry: null);

    public static QuickstartGuide Build(
        PalLlmRuntime runtime,
        PalLlmOptions options,
        ModelRoleRegistry? roleRegistry)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);

        RuntimeHealth health = runtime.GetHealth();
        OperatorHealthScore score = OperatorHealthScorer.Score(health);
        List<QuickstartStep> steps = new();

        // ---- Critical: things that block the core chat path ----
        if (!health.AdapterReady)
        {
            steps.Add(new QuickstartStep(
                Priority: "critical",
                Label: "Launch the game so the bridge can connect",
                Why: "The game adapter is not ready, which means the UE4SS Lua bridge has not started delivering events. Chat still replies via the deterministic fallback, but world events won't flow into the runtime.",
                Action: "Start Palworld with UE4SS installed. The sidecar auto-connects as soon as the bridge writes its first event to the runtime root's Bridge/Inbox folder.",
                Verify: "GET /api/health -> AdapterReady=true, or rerun scripts/doctor.ps1 and watch 'Bridge readiness' go green."));
        }

        if (string.IsNullOrWhiteSpace(health.Status))
        {
            steps.Add(new QuickstartStep(
                Priority: "critical",
                Label: "Investigate empty runtime status",
                Why: "The runtime initialised without a status line, which means something in startup did not complete. The deterministic fallback will still reply, but diagnostics will be thin.",
                Action: "Run scripts/doctor.ps1 -RunSmoke and inspect the failing check; if the sidecar is wedged, double-click recover.bat.",
                Verify: "GET /api/health -> Status is non-empty."));
        }

        // ---- Recommended: quality-of-life upgrades most operators want ----
        if (!health.InferenceConfigured)
        {
            steps.Add(new QuickstartStep(
                Priority: "recommended",
                Label: "Turn on live inference for richer replies",
                Why: "Live inference is off today — the companion is replying entirely via the deterministic fallback director. That is a supported shipping posture, but live inference typically gives more varied and contextual replies.",
                Action: "Install the bundled engine with `pwsh ./pal.ps1 install-llama-cpp` (or use LM Studio / vLLM for high-config GPUs / Foundry Local on Windows), start the server pointing at a curated D:\\Models GGUF, then set PalLLM:Inference:Enabled=true plus BaseUrl / Model in appsettings.json and restart the sidecar.",
                Verify: "palllm_inference_success_total rises above 0, or GET /api/inference/performance shows recent live lane activity."));
        }
        else
        {
            string circuit = health.InferenceCircuitState ?? string.Empty;
            if (string.Equals(circuit, "Open", StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new QuickstartStep(
                    Priority: "critical",
                    Label: "Recover the inference circuit breaker",
                    Why: "The circuit breaker is OPEN after repeated failures against the configured inference endpoint. Chat is currently running on the fallback director until the cooldown window elapses or the endpoint recovers.",
                    Action: "Check that the configured PalLLM:Inference:BaseUrl is reachable and the configured model is loaded. If the endpoint moved, update appsettings.json and restart. Otherwise wait for the automatic half-open probe.",
                    Verify: "GET /api/health -> InferenceCircuitState=Closed; palllm_inference_success_total begins rising again."));
            }

            long total = health.InferenceSuccessCount + health.InferenceFailureCount;
            if (total >= 10)
            {
                double failureRate = (double)health.InferenceFailureCount / total;
                if (failureRate >= 0.10)
                {
                    steps.Add(new QuickstartStep(
                        Priority: "recommended",
                        Label: "Investigate elevated inference failure rate",
                        Why: $"Inference has failed on {failureRate:P0} of {total} recent attempts. The fallback is still replying to players, but live lane quality is degraded.",
                        Action: "GET /api/inference/performance to see per-lane latency and failure counts, then verify your endpoint + model tag are still valid.",
                        Verify: "Failure rate in /api/inference/performance trends back under 10%."));
                }
            }
        }

        if (options.Inference.Enabled && !options.Inference.EnableWarmup)
        {
            steps.Add(new QuickstartStep(
                Priority: "optional",
                Label: "Enable warmup so first replies are snappy",
                Why: "Warmup is off, so the first live reply after sidecar start or tier graduation pays the full model-load latency.",
                Action: "Set PalLLM:Inference:EnableWarmup=true in appsettings.json and restart.",
                Verify: "POST /api/inference/warmup returns Status=ready; the first live chat after boot is noticeably faster."));
        }

        // ---- Quantization-specific hardware nudges ----
        // Surface opt-in next steps when the host advertises a stronger
        // serving path than the generic "bring up any OpenAI-compatible
        // endpoint" guidance. These stay optional because they require a
        // deliberate operator-side backend/model choice, not a PalLLM
        // toggle.
        HardwareProfile hardware = HardwareProfiler.CaptureCached(options.Hardware.ForceTier);
        if (hardware.Fp4TensorCoresLikely)
        {
            steps.Add(new QuickstartStep(
                Priority: "optional",
                Label: "Switch to NVFP4 to take advantage of your Blackwell tensor cores",
                Why: $"Blackwell GPU detected ({hardware.GpuArchitectureDetail}). NVFP4 + vLLM / TensorRT-LLM gives ~2x throughput vs FP8 and ~4x vs FP16 at near-FP16 accuracy on a 70B model. Memory drops from ~140 GB (FP16) to ~37 GB (NVFP4).",
                Action: "Run vLLM 0.6+ with --quantization fp4 and a NVIDIA-published or community-quantized NVFP4 model (e.g. nvidia/Llama-3.3-70B-Instruct-FP4). Update PalLLM:Inference:BaseUrl + Model accordingly.",
                Verify: "GET /api/hardware -> RecommendedQuantization=nvfp4. /api/inference/performance shows ~2x lower per-token latency compared to FP8. See docs/QUANTIZATION.md for the full setup walkthrough."));
        }
        else if (string.Equals(hardware.RecommendedQuantization, "mxfp4", StringComparison.OrdinalIgnoreCase))
        {
            steps.Add(new QuickstartStep(
                Priority: "optional",
                Label: "Validate MXFP4 on your AMD Instinct serving lane",
                Why: $"AMD accelerator hint detected ({hardware.GpuArchitectureDetail}). Current ROCm-oriented serving stacks expose MXFP4 support, which can lower model memory while keeping a standards-based 4-bit path. Coverage still depends on the exact backend and model family.",
                Action: "If you are serving through vLLM on ROCm or another MXFP4-capable stack, benchmark an MXFP4 checkpoint beside your current FP8 or Q4_K_M default before switching PalLLM over.",
                Verify: "GET /api/hardware -> RecommendedQuantization=mxfp4. Then compare /api/inference/performance before and after the serving change on the same workload."));
        }

        if (!options.Vision.Enabled)
        {
            steps.Add(new QuickstartStep(
                Priority: "optional",
                Label: "Turn on the vision pipeline to let the companion see the scene",
                Why: "Vision is off. The companion still falls back to the deterministic snapshot-derived scene description, but live vision gives much richer image understanding when you have a multimodal model.",
                Action: "Set PalLLM:Vision:Enabled=true and configure a multimodal BaseUrl + Model.",
                Verify: "POST /api/vision/describe returns Success=true."));
        }

        if (!options.Tts.Enabled)
        {
            steps.Add(new QuickstartStep(
                Priority: "optional",
                Label: "Turn on TTS if you want the companion to speak",
                Why: "TTS synthesis is off. Companion replies are text-only today.",
                Action: "Set PalLLM:Tts:Enabled=true and point BaseUrl at a speech endpoint; keep RequestFormat=simple for {text, voice} adapters or use openai_speech for /v1/audio/speech.",
                Verify: "POST /api/tts/synthesize returns a FilePath."));
        }

        // ---- Optional: operational polish worth mentioning once ----
        if (string.IsNullOrWhiteSpace(options.Auth.ApiKey))
        {
            steps.Add(new QuickstartStep(
                Priority: "optional",
                Label: "Set an API key if this sidecar is reachable beyond localhost",
                Why: "Bearer-token auth is off. This is fine for localhost-only posture, but mandatory the moment you expose the sidecar on a LAN or public network.",
                Action: "Set PalLLM:Auth:ApiKey to any non-empty string and restart. Operators + MCP clients must then send Authorization: Bearer <key>.",
                Verify: "GET /api/features without the header returns 401 + WWW-Authenticate: Bearer."));
        }

        if (!options.Inference.ThermalGate.Enabled)
        {
            steps.Add(new QuickstartStep(
                Priority: "optional",
                Label: "Enable the thermal gate if you game on the same GPU you inference on",
                Why: "Opt-in GPU-temperature gate is off. If you run the game and local inference on the same card, enabling this keeps chat latency predictable when the GPU throttles.",
                Action: "Set PalLLM:Inference:ThermalGate:Enabled=true; tune RejectAboveC to your card's throttling point.",
                Verify: "Under heat, inference traces carry ErrorType=thermal_gated and the reply comes from the fallback director."));
        }

        // ---- Local-first mesh role coverage ------------------------
        // When the operator hasn't declared at least a Worker role, the
        // 2035 AI-mesh architecture isn't visible to AI clients. Surface
        // that as a recommended step so the operator knows the mesh
        // surface exists even if they haven't bound endpoints to it yet.
        if (roleRegistry is not null)
        {
            ModelRoleCoverage coverage = roleRegistry.GetCoverage();
            if (coverage.TotalBindings == 0)
            {
                steps.Add(new QuickstartStep(
                    Priority: "optional",
                    Label: "Declare your local model roles for the AI-mesh surface",
                    Why: "No Edge / Worker / Judge / Media / Validator bindings are declared. Declaring roles doesn't change inference behaviour today, but it lets AI clients and /api/describe see which models fill which slot — the foundation for future role-aware routing.",
                    Action: "Add entries under PalLLM:ModelRoles[] in appsettings.json: { Role: Worker|Edge|Judge|Media|Validator, Id, ModelId, BaseUrl, Description }.",
                    Verify: "GET /api/roles reports the new bindings and the PairingPattern string updates."));
            }
            else if (coverage.CriticalGaps.Count > 0)
            {
                string gaps = string.Join(" + ", coverage.CriticalGaps);
                steps.Add(new QuickstartStep(
                    Priority: "optional",
                    Label: $"Fill the missing critical mesh role(s): {gaps}",
                    Why: $"{coverage.TotalBindings} role binding(s) are configured, but {gaps} is still unbound. {coverage.PairingPattern}",
                    Action: "Add a binding under PalLLM:ModelRoles[] for each missing role; see /api/roles for the per-slot recommendation.",
                    Verify: "GET /api/roles -> CriticalGaps empties and PairingPattern advances."));
            }
        }

        // ---- Overall status + human one-liner ----
        bool anyCritical = steps.Any(s => string.Equals(s.Priority, "critical", StringComparison.OrdinalIgnoreCase));
        bool anyRecommended = steps.Any(s => string.Equals(s.Priority, "recommended", StringComparison.OrdinalIgnoreCase));
        string overallStatus = anyCritical ? "needs-attention" : (anyRecommended ? "needs-setup" : "ready");
        string headline = overallStatus switch
        {
            "ready" => "PalLLM is operational. All critical paths are live; optional upgrades below are purely quality-of-life.",
            "needs-setup" => "PalLLM is replying, but a few recommended upgrades would improve the experience materially.",
            _ => "PalLLM needs attention. The deterministic fallback is keeping the companion responsive, but one or more critical signals are red.",
        };

        return new QuickstartGuide(
            OverallStatus: overallStatus,
            Headline: headline,
            OperatorHealth: score,
            Steps: steps);
    }
}

public sealed record QuickstartGuide(
    string OverallStatus,
    string Headline,
    OperatorHealthScore OperatorHealth,
    IReadOnlyList<QuickstartStep> Steps);

public sealed record QuickstartStep(
    string Priority,
    string Label,
    string Why,
    string Action,
    string Verify);
