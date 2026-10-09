using System.IO;
using System.Text.Json;
using NUnit.Framework;

namespace PalLLM.Tests;

/// <summary>
/// Pass 347 — pins the hardware-aware bundled-llama.cpp installer and
/// the Qwen3.5 canonical sampler defaults that pair with it. The
/// research lead-in: as of May 2026 the upstream llama.cpp releases
/// ship multiple Windows backend variants per tag (CPU, CUDA 12.4,
/// CUDA 13.1, Vulkan, SYCL, HIP/Radeon). Before Pass 347 the install
/// script asked for <c>llama-&lt;tag&gt;-bin-win-cuda-x64.zip</c>, an asset
/// name that hasn't existed since upstream split the CUDA build.
/// These tests catch that regression and any future drift (e.g. someone
/// rolling the script forward to a CUDA 14.x build that hasn't been
/// validated on Blackwell).
///
/// They also pin the Qwen3.5 thinking-OFF sampler
/// (temp 0.7, top-p 0.8, top-k 20, min-p 0.0, presence-penalty 1.5)
/// into the shipping appsettings.json. PalLLM's per-request sampler
/// must agree with the numbers printed by the install + connect
/// scripts, otherwise the operator's manual llama-server launch and
/// PalLLM's chat traffic disagree on sampling.
/// </summary>
[TestFixture]
public class LlamaCppBundlingTests
{
    // ---------- Installer asset-name shape (Pass 347) ----------

    [Test]
    public void InstallScript_ReferencesPerBackendCudaAssetNames_NotMonolithicCudaName()
    {
        string text = ReadInstallScript();

        // Pass 347: upstream split the Windows CUDA build into 12.4 and 13.1
        // variants. The old `cuda-x64` shape is dead.
        Assert.That(text, Does.Not.Contain("llama-$Tag-bin-win-cuda-x64"),
            "install-llama-cpp.ps1 must not reference the pre-split 'cuda-x64' asset name. " +
            "Upstream now ships cuda-12.4 and cuda-13.1 variants per release.");
        Assert.That(text, Does.Not.Contain("llama-$ReleaseTag-bin-win-cuda-x64"),
            "install-llama-cpp.ps1 must not reference the pre-split 'cuda-x64' asset name (variant). " +
            "See docs/LLAMA_CPP_BUNDLED.md for the current backend matrix.");

        Assert.That(text, Does.Contain("llama-$Tag-bin-win-cuda-12.4-x64.zip"),
            "install-llama-cpp.ps1 must offer the cuda-12.4 asset (Blackwell-stable default).");
        Assert.That(text, Does.Contain("llama-$Tag-bin-win-cuda-13.1-x64.zip"),
            "install-llama-cpp.ps1 must offer the cuda-13.1 asset (opt-in via -Backend cuda13).");
        Assert.That(text, Does.Contain("llama-$Tag-bin-win-vulkan-x64.zip"),
            "install-llama-cpp.ps1 must offer the Vulkan asset (default for AMD/Intel GPUs).");
        Assert.That(text, Does.Contain("llama-$Tag-bin-win-hip-radeon-x64.zip"),
            "install-llama-cpp.ps1 must offer the HIP/Radeon asset (opt-in via -Backend hip).");
        Assert.That(text, Does.Contain("llama-$Tag-bin-win-sycl-x64.zip"),
            "install-llama-cpp.ps1 must offer the SYCL asset (opt-in via -Backend sycl).");
        Assert.That(text, Does.Contain("llama-$Tag-bin-win-cpu-x64.zip"),
            "install-llama-cpp.ps1 must offer the CPU-only asset (no-GPU fallback).");
    }

    [Test]
    public void InstallScript_DownloadsCompanionCudartPackForCudaBackends()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("cudart-llama-bin-win-cuda-12.4-x64.zip"),
            "install-llama-cpp.ps1 must pull the cuda-12.4 cudart runtime DLL pack. " +
            "Without it, llama-server.exe fails to load CUDA at boot.");
        Assert.That(text, Does.Contain("cudart-llama-bin-win-cuda-13.1-x64.zip"),
            "install-llama-cpp.ps1 must pull the cuda-13.1 cudart runtime DLL pack.");
    }

    [Test]
    public void InstallScript_HasHardwareDetection_AndBackendSelectionMatrix()
    {
        string text = ReadInstallScript();

        // Hardware detection via WMI.
        Assert.That(text, Does.Contain("Win32_VideoController"),
            "install-llama-cpp.ps1 must probe Win32_VideoController to detect GPU vendor.");
        Assert.That(text, Does.Contain("Get-DetectedGpuVendor"),
            "install-llama-cpp.ps1 must expose the GPU-vendor detection helper.");

        // Backend ValidateSet (parameter contract).
        foreach (string backend in new[] { "auto", "cuda12", "cuda13", "vulkan", "hip", "sycl", "cpu" })
        {
            Assert.That(text, Does.Contain($"'{backend}'"),
                $"install-llama-cpp.ps1 -Backend parameter must accept '{backend}'.");
        }
    }

    [Test]
    public void InstallScript_DefaultsToCuda12ForNvidiaGpus_BlackwellSafe()
    {
        string text = ReadInstallScript();

        // The Blackwell-safety justification is load-bearing — keep the
        // comment alive so a future "let's modernize to 13.x" pass has
        // to argue with the prior-art benchmark first.
        Assert.That(text, Does.Contain("'nvidia'").And.Contain("'cuda12'"),
            "install-llama-cpp.ps1 must default NVIDIA hosts to the cuda12 backend.");
        Assert.That(text, Does.Contain("Blackwell"),
            "install-llama-cpp.ps1 must explain why CUDA 12.4 (not 13.x) is the NVIDIA default. " +
            "Source: zenn.dev/toki_mwc benchmark, RTX 5090 MMQ crash on CUDA 13.x as of May 2026.");
    }

    // ---------- Qwen3.5 canonical sampler defaults (Pass 347/448) ----------

    [Test]
    public void ShippingAppsettings_InferenceSampler_MatchesQwen35ThinkingOffProfile()
    {
        string path = LocateShippingAppsettings();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement inference = doc.RootElement
            .GetProperty("PalLLM")
            .GetProperty("Inference");

        // Pass 448: the Qwen3.5 fast-lane thinking-OFF profile.
        // Source: unsloth.ai/docs/models/qwen3.5.
        // The same numbers ship in install-llama-cpp.ps1 + connect-llamacpp.ps1
        // so PalLLM's per-request sampler agrees with the operator's
        // manual server invocation.
        AssertNumericFieldEqual(inference, "Temperature", 0.7);
        AssertNumericFieldEqual(inference, "TopP", 0.8);
        AssertNumericFieldEqual(inference, "TopK", 20);
        AssertNumericFieldEqual(inference, "MinP", 0.0);
        AssertNumericFieldEqual(inference, "PresencePenalty", 1.5);
    }

    [Test]
    public void ShippingAppsettings_InferenceSampler_DocumentsUnslothSource()
    {
        // Belt-and-braces: the JSON comment that explains *why* these
        // numbers are pinned. If a future pass tunes the sampler away
        // from Unsloth's canonical, the comment must move with it (or
        // get justified separately).
        string path = LocateShippingAppsettings();
        string text = File.ReadAllText(path);

        Assert.That(text, Does.Contain("_comment_Sampler"),
            "shipping appsettings.json must carry a _comment_Sampler key explaining the sampler choice.");
        Assert.That(text, Does.Contain("unsloth.ai/docs/models/qwen3.5"),
            "the _comment_Sampler explanation must name the source profile so future readers can audit.");
    }

    // ---------- Connect script harmonisation (Pass 347) ----------

    [Test]
    public void ConnectScript_DefaultsFlashAttnToAuto_NotOn()
    {
        string text = ReadConnectScript();

        // Pass 347: the April-2026 stream_k_fixup crash on RTX 5090 Blackwell
        // means `--flash-attn on` isn't a safe shipping default. `auto`
        // lets llama-server make the right call per host.
        Assert.That(text, Does.Contain("[string]$FlashAttn = 'auto'"),
            "connect-llamacpp.ps1 must default -FlashAttn to 'auto' (Pass 347).");
    }

    [Test]
    public void ConnectScript_EmitsThinkingTemplateKwarg_MatchingEnableThinkingFlag()
    {
        string text = ReadConnectScript();

        Assert.That(text, Does.Contain("--chat-template-kwargs"),
            "connect-llamacpp.ps1 must emit --chat-template-kwargs so Qwen3 reasoning is toggleable.");
        Assert.That(text, Does.Contain("enable_thinking"),
            "connect-llamacpp.ps1 must emit the enable_thinking key in the chat-template kwargs.");
        Assert.That(text, Does.Contain("[bool]$EnableThinking = $false"),
            "connect-llamacpp.ps1 must default -EnableThinking to $false to match PalLLM:Inference:EnableThinking shipping default.");
    }

    [Test]
    public void ConnectScript_EmitsUnslothCanonicalSamplerInLaunchCommand()
    {
        string text = ReadConnectScript();

        // Same numbers as the appsettings, printed into the operator's
        // copy-paste llama-server command so the two agree.
        Assert.That(text, Does.Contain("'--temp', '0.7'"),
            "connect-llamacpp.ps1 must print temp 0.7 in the launch recipe (thinking-OFF default).");
        Assert.That(text, Does.Contain("'--top-p', '0.8'"));
        Assert.That(text, Does.Contain("'--top-k', '20'"));
        Assert.That(text, Does.Contain("'--min-p', '0.0'"));
        Assert.That(text, Does.Contain("'--presence-penalty', '1.5'"));
    }

    // ---------- Pass 348: per-model recipes + known-bug caveats ----------

    [Test]
    public void BundledDoc_HasPerModelRecipeForEveryCuratedFamily()
    {
        string text = ReadBundledDoc();

        // Pass 448: every supported model in LOCAL_MODELS_INVENTORY.md must have
        // its own recipe block in LLAMA_CPP_BUNDLED.md. If a future pass
        // adds a new family to the curated library, this test catches
        // the missing recipe before it ships.
        string[] requiredFamilies = new[]
        {
            "Qwen3.5-9B-UD-Q6_K_XL.gguf",
            "gemma-4-12b-it-UD-Q6_K_XL.gguf",
        };

        foreach (string family in requiredFamilies)
        {
            Assert.That(text, Does.Contain(family),
                $"LLAMA_CPP_BUNDLED.md must include a recipe section referencing '{family}'. " +
                $"See LOCAL_MODELS_INVENTORY.md for the canonical filenames.");
        }
    }

    [Test]
    public void BundledDoc_DocumentsGemmaSamplerProfile_NotQwenProfile()
    {
        string text = ReadBundledDoc();

        // Pass 448: the Gemma 4 smart lane has a different top-p profile
        // than the Qwen3.5 fast lane, so the operator doc must show the
        // family-specific sampler instead of implying one profile fits all.
        Assert.That(text, Does.Contain("gemma-4-12b-it-UD-Q6_K_XL"));
        Assert.That(text, Does.Contain("--top-p 0.95").And.Contain("--min-p 0.0"),
            "LLAMA_CPP_BUNDLED.md must document Gemma 4's top-p=0.95 / min-p=0.0 sampler.");
    }

    [Test]
    public void BundledDoc_DoesNotPromoteRetiredHeavyweightFamilies()
    {
        foreach (string relative in new[]
        {
            Path.Combine("docs", "LLAMA_CPP_BUNDLED.md"),
            Path.Combine("docs", "LOCAL_MODELS_INVENTORY.md"),
            Path.Combine("docs", "MINIMUM_REQUIREMENTS.md"),
            Path.Combine("docs", "TUNING.md"),
            Path.Combine("docs", "CHEAT_SHEET.md"),
            Path.Combine("docs", "MCP_QUICKSTART.md"),
            Path.Combine("docs", "API.md"),
            Path.Combine("docs", "ARCHITECTURE.md"),
            Path.Combine("docs", "OPERATIONS.md"),
            Path.Combine("docs", "examples", "compose.yaml"),
            Path.Combine("scripts", "install-llama-cpp.ps1"),
            Path.Combine("scripts", "connect-llamacpp.ps1"),
        })
        {
            string path = LocateRepoFile(relative.Split(Path.DirectorySeparatorChar));
            AssertNoRetiredLocalModelPromotion(File.ReadAllText(path), relative);
        }
    }

    [Test]
    public void BundledDoc_PinsGemma4MmprojCudaCrash()
    {
        string text = ReadBundledDoc();

        Assert.That(text, Does.Contain("#21402").And.Contain("SIGABRT"),
            "LLAMA_CPP_BUNDLED.md must reference upstream issue #21402 (Gemma-4 mmproj SIGABRT on CUDA). " +
            "The recommended workaround is the Vulkan backend or text-only Gemma 4 + snapshot vision fallback.");
        Assert.That(text, Does.Contain("clip_model_loader::load_tensors"),
            "LLAMA_CPP_BUNDLED.md must name the crashing function so operators can confirm a bug match in their llama-server logs.");
    }

    [Test]
    public void BundledDoc_PinsSpeculationProofOnlyForCurrentLanes()
    {
        string text = ReadBundledDoc();

        Assert.That(text, Does.Contain("Speculative decoding").And.Contain("off by default"),
            "LLAMA_CPP_BUNDLED.md must keep speculation proof-only by default.");
        Assert.That(text, Does.Contain("Qwen3.5 / Gemma 4 MTP"),
            "LLAMA_CPP_BUNDLED.md must describe speculation only for the current supported lanes.");
    }

    [Test]
    public void BundledDoc_DocumentsSingleShardCurrentCatalog()
    {
        string text = ReadBundledDoc();

        Assert.That(text, Does.Contain("single-shard"));
        Assert.That(text, Does.Not.Contain("-00001-of-"),
            "LLAMA_CPP_BUNDLED.md must not promote retired multi-shard lanes in the active recipe catalog.");
    }

    [Test]
    public void ConnectScript_ExposesPerModelSamplerProfileParameter()
    {
        string text = ReadConnectScript();

        Assert.That(text, Does.Contain("[string]$ModelProfile"),
            "connect-llamacpp.ps1 must expose -ModelProfile so the operator can pick per-model sampler defaults.");
        foreach (string profile in new[] { "qwen35", "gemma", "generic" })
        {
            Assert.That(text, Does.Contain($"'{profile}'"),
                $"connect-llamacpp.ps1 -ModelProfile must accept '{profile}'.");
        }
    }

    [Test]
    public void ConnectScript_EmitsGemmaSamplerWhenProfileMatches()
    {
        string text = ReadConnectScript();

        // When -ModelProfile gemma is selected, the launch line must
        // carry Gemma's sampler, not Qwen's.
        Assert.That(text, Does.Contain("'--top-p', '0.95'").And.Contain("'--min-p', '0.0'"),
            "connect-llamacpp.ps1 must emit --top-p 0.95 --min-p 0.0 for the gemma profile.");
    }

    [Test]
    public void ConnectScript_ExposesThreadPriorityMlockTensorSplitKnobs()
    {
        string text = ReadConnectScript();

        // Pass 348 perf-knob params for the heavy-tier launch recipes.
        Assert.That(text, Does.Contain("[int]$Threads"));
        Assert.That(text, Does.Contain("[int]$ThreadsBatch"));
        Assert.That(text, Does.Contain("[int]$Prio"));
        Assert.That(text, Does.Contain("[switch]$Mlock"));
        Assert.That(text, Does.Contain("[switch]$NoMmap"));
        Assert.That(text, Does.Contain("[string]$TensorSplit"));
        Assert.That(text, Does.Contain("[string]$SplitMode"));

        // Each must round-trip into the emitted server args.
        Assert.That(text, Does.Contain("'--threads',"));
        Assert.That(text, Does.Contain("'--threads-batch',"));
        Assert.That(text, Does.Contain("'--prio',"));
        Assert.That(text, Does.Contain("'--mlock'"));
        Assert.That(text, Does.Contain("'--no-mmap'"));
        Assert.That(text, Does.Contain("'--tensor-split',"));
        Assert.That(text, Does.Contain("'--split-mode',"));

        // Pass 436d: CUDA launch-recipe knobs folded in from an external
        // best-available portable llama.cpp build's proven dual-GPU co-load
        // recipe — the CUDA-specific tunings the Pass 348 knobs did not cover.
        Assert.That(text, Does.Contain("[int]$PrioBatch"),
            "connect-llamacpp.ps1 must expose -PrioBatch for the GPU-offload --prio/--prio-batch pairing.");
        Assert.That(text, Does.Contain("[int]$Poll"),
            "connect-llamacpp.ps1 must expose -Poll for the busy-wait percentage knob.");
        Assert.That(text, Does.Contain("[int]$CtxCheckpoints"),
            "connect-llamacpp.ps1 must expose -CtxCheckpoints for long-context prompt-cache resilience.");
        Assert.That(text, Does.Contain("[int]$CtxCheckpointTokens"),
            "connect-llamacpp.ps1 must expose -CtxCheckpointTokens.");
        Assert.That(text, Does.Contain("[string]$CudaDevices"),
            "connect-llamacpp.ps1 must expose -CudaDevices for multi-GPU CUDA_VISIBLE_DEVICES pinning.");
        Assert.That(text, Does.Contain("'--prio-batch',"));
        Assert.That(text, Does.Contain("'--poll',"));
        Assert.That(text, Does.Contain("'--ctx-checkpoints',"));
        Assert.That(text, Does.Contain("'--ctx-checkpoint-tokens',"));
        // CUDA_VISIBLE_DEVICES is an env-var prefix, not a llama-server flag.
        Assert.That(text, Does.Contain("CUDA_VISIBLE_DEVICES"),
            "connect-llamacpp.ps1 must emit a CUDA_VISIBLE_DEVICES launch-environment prefix when -CudaDevices is set.");
    }

    // ---------- Pass 349: zero-config / any-hardware setup ----------

    [Test]
    public void InstallScript_DetectsCrossPlatformGpuVendor_NotJustWindows()
    {
        string text = ReadInstallScript();

        // Pass 349: vendor detection must work on Linux + macOS, not
        // only Windows. Probes are guarded by Test-CommandAvailable so
        // missing tools degrade gracefully.
        Assert.That(text, Does.Contain("'macos-arm64'"),
            "install-llama-cpp.ps1 must include a macOS Apple Silicon branch in Get-DetectedGpuVendor.");
        Assert.That(text, Does.Contain("'linux-x64'"),
            "install-llama-cpp.ps1 must include a Linux branch in Get-DetectedGpuVendor.");
        Assert.That(text, Does.Contain("nvidia-smi"),
            "install-llama-cpp.ps1 must probe nvidia-smi on Linux.");
        Assert.That(text, Does.Contain("rocm-smi"),
            "install-llama-cpp.ps1 must probe rocm-smi on Linux for AMD GPUs.");
        Assert.That(text, Does.Contain("lspci"),
            "install-llama-cpp.ps1 must fall back to lspci on Linux when SMIs aren't installed.");
        Assert.That(text, Does.Contain("system_profiler"),
            "install-llama-cpp.ps1 must probe system_profiler on macOS for discrete GPUs.");
    }

    [Test]
    public void InstallScript_HasVramAndRamDetectionHelpers()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("function Get-DetectedVramGb"),
            "install-llama-cpp.ps1 must expose Get-DetectedVramGb.");
        Assert.That(text, Does.Contain("function Get-DetectedSystemRamGb"),
            "install-llama-cpp.ps1 must expose Get-DetectedSystemRamGb.");
        Assert.That(text, Does.Contain("function Get-CudaToolkitVersion"),
            "install-llama-cpp.ps1 must expose Get-CudaToolkitVersion (used to warn on broken 13.0-13.2 toolkits).");

        // VRAM detection prefers nvidia-smi over WMI's truncated UINT32.
        Assert.That(text, Does.Contain("memory.total"),
            "install-llama-cpp.ps1 must query nvidia-smi memory.total for accurate VRAM (WMI UINT32 truncates above 4 GiB).");

        // System RAM uses platform-appropriate APIs.
        Assert.That(text, Does.Contain("Win32_OperatingSystem"),
            "install-llama-cpp.ps1 must query Win32_OperatingSystem.TotalVisibleMemorySize for Windows RAM.");
        Assert.That(text, Does.Contain("hw.memsize"),
            "install-llama-cpp.ps1 must query sysctl hw.memsize for macOS RAM.");
        Assert.That(text, Does.Contain("/proc/meminfo"),
            "install-llama-cpp.ps1 must parse /proc/meminfo for Linux RAM.");
    }

    [Test]
    public void InstallScript_HasVramBasedModelRecommendation()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("function Get-RecommendedModel"),
            "install-llama-cpp.ps1 must expose Get-RecommendedModel for VRAM-based auto-pick.");

        // The recommendation walks the current PalLLM local catalog.
        foreach (string family in new[]
        {
            "Qwen3.5-9B-UD-Q6_K_XL",
            "gemma-4-12b-it-UD-Q6_K_XL",
        })
        {
            Assert.That(text, Does.Contain(family),
                $"install-llama-cpp.ps1 recommendation catalog must include '{family}'.");
        }

        // The catalog must carry MinVramGb gates so the recommendation
        // doesn't pick a 39 GB MoE for a 6 GB card.
        Assert.That(text, Does.Contain("MinVramGb"),
            "install-llama-cpp.ps1 catalog entries must declare MinVramGb so VRAM gating works.");
    }

    [Test]
    public void InstallScript_ExposesAutoLaunchAndSmokeTestSwitches()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("[switch]$AutoLaunch"),
            "install-llama-cpp.ps1 must expose -AutoLaunch.");
        Assert.That(text, Does.Contain("[switch]$NoSmokeTest"),
            "install-llama-cpp.ps1 must expose -NoSmokeTest (smoke test ON by default; opt-out).");
        Assert.That(text, Does.Contain("function Test-LlamaServerBinary"),
            "install-llama-cpp.ps1 must expose Test-LlamaServerBinary for the post-install smoke check.");
        Assert.That(text, Does.Contain("Smoke test OK"),
            "install-llama-cpp.ps1 must print a Smoke-test-OK confirmation when the binary boots.");

        // The smoke-test failure path emits actionable hints.
        Assert.That(text, Does.Contain("cudart").And.Contain("VCRUNTIME"),
            "install-llama-cpp.ps1 smoke-test failure path must hint at missing cudart and VC++ runtime issues.");
    }

    [Test]
    public void InstallScript_WarnsAboutBrokenCudaToolkitBand()
    {
        string text = ReadInstallScript();

        // CUDA 13.0-13.2 has the MMQ-crash / unstable-output issues.
        // The installer must surface this if it detects the operator
        // is running on that band AND has forced -Backend cuda13.
        Assert.That(text, Does.Contain("13.0-13.2"),
            "install-llama-cpp.ps1 must call out the broken CUDA 13.0-13.2 band when the operator is on it.");
        Assert.That(text, Does.Contain("unstable output"),
            "install-llama-cpp.ps1 must describe CUDA 13.0-13.2 unstable-output risk without promoting retired model families.");
    }

    [Test]
    public void InstallScript_PrintsHardwareSummaryBeforeDownload()
    {
        string text = ReadInstallScript();

        // The hardware summary lets the operator ctrl-c if auto-pick
        // is wrong (e.g. detected vendor is 'none' when they expect
        // an NVIDIA card because nvidia-smi isn't on PATH).
        Assert.That(text, Does.Contain("Detected GPU"));
        Assert.That(text, Does.Contain("Detected VRAM"));
        Assert.That(text, Does.Contain("Detected RAM"));
        Assert.That(text, Does.Contain("CUDA toolkit"));
        Assert.That(text, Does.Contain("Models root"));
    }

    // ---------- Pass 350: MoE offload + KV math + safety nets ----------

    [Test]
    public void InstallScript_HasMoeOffloadRecommendationFields()
    {
        string text = ReadInstallScript();

        // Pass 350: Get-RecommendedModel now returns NCpuMoe and
        // QuantizedKv alongside Path/Family/ContextSize/GpuLayers.
        // Without these the operator can't run the 4 MoE families on
        // consumer cards.
        Assert.That(text, Does.Contain("NCpuMoe"),
            "install-llama-cpp.ps1 recommendation must surface NCpuMoe for MoE partial offload.");
        Assert.That(text, Does.Contain("QuantizedKv"),
            "install-llama-cpp.ps1 recommendation must surface QuantizedKv for tight-VRAM lanes.");
        Assert.That(text, Does.Contain("MoeMinVramGb"),
            "install-llama-cpp.ps1 catalog entries must declare MoeMinVramGb so MoE partial-offload eligibility can be computed.");
        Assert.That(text, Does.Contain("IsMoE"),
            "install-llama-cpp.ps1 catalog entries must mark which families are MoE.");

        // Auto-launch must emit --n-cpu-moe / -ctk q8_0 / -ctv q8_0 when
        // the recommendation flags them.
        Assert.That(text, Does.Contain("--n-cpu-moe"),
            "install-llama-cpp.ps1 auto-launch must emit --n-cpu-moe when recommendation says so.");
        Assert.That(text, Does.Contain("'-ctk', 'q8_0'"),
            "install-llama-cpp.ps1 auto-launch must emit -ctk q8_0 when QuantizedKv is true.");
        Assert.That(text, Does.Contain("'-ctv', 'q8_0'"),
            "install-llama-cpp.ps1 auto-launch must emit -ctv q8_0 when QuantizedKv is true.");
    }

    [Test]
    public void InstallScript_HasKvCacheBudgetHelper()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("function Get-KvCacheGb"),
            "install-llama-cpp.ps1 must expose Get-KvCacheGb so the recommendation can subtract KV from available VRAM before deciding fit.");
        // The formula: 2 × layers × kv_heads × head_dim × ctx × bytes_per_elem.
        Assert.That(text, Does.Contain("2 * $Layers * $KvHeads * $HeadDim * $ContextSize"),
            "install-llama-cpp.ps1 KV cache formula must match the canonical GQA shape (2 × layers × kv_heads × head_dim × ctx × bytes).");
    }

    [Test]
    public void InstallScript_HasMultiGpuDetection()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("function Get-DetectedGpuCount"),
            "install-llama-cpp.ps1 must expose Get-DetectedGpuCount.");
        Assert.That(text, Does.Contain("Multi-GPU detected"),
            "install-llama-cpp.ps1 must surface a multi-GPU hint when more than one card is detected.");
        // The hint must reference both symmetric (split-mode graph) and
        // asymmetric (tensor-split) paths.
        Assert.That(text, Does.Contain("-SplitMode graph").And.Contain("-TensorSplit"),
            "install-llama-cpp.ps1 multi-GPU hint must point at both -SplitMode graph (symmetric) and -TensorSplit (asymmetric).");
    }

    [Test]
    public void ConnectScript_ExposesNCpuMoeAndOverrideTensorParams()
    {
        string text = ReadConnectScript();

        Assert.That(text, Does.Contain("[int]$NCpuMoe = 0"),
            "connect-llamacpp.ps1 must expose -NCpuMoe with default 0 (off).");
        Assert.That(text, Does.Contain("[string]$OverrideTensor"),
            "connect-llamacpp.ps1 must expose -OverrideTensor for regex-based MoE tensor placement.");

        // The flags must round-trip into the emitted server args.
        Assert.That(text, Does.Contain("'--n-cpu-moe',"),
            "connect-llamacpp.ps1 must emit --n-cpu-moe when NCpuMoe > 0.");
        Assert.That(text, Does.Contain("'--override-tensor',"),
            "connect-llamacpp.ps1 must emit --override-tensor when OverrideTensor is set.");
    }

    [Test]
    public void BundledDoc_DocumentsMoeOffloadingRecipes()
    {
        string text = ReadBundledDoc();

        Assert.That(text, Does.Contain("MoE offloading recipes"),
            "LLAMA_CPP_BUNDLED.md must include a MoE offloading section.");
        Assert.That(text, Does.Contain("--n-cpu-moe"),
            "LLAMA_CPP_BUNDLED.md must document --n-cpu-moe.");
        Assert.That(text, Does.Contain("--override-tensor"),
            "LLAMA_CPP_BUNDLED.md must document --override-tensor as the regex alternative.");
        Assert.That(text, Does.Contain("ffn_.*_exps").Or.Contain(".ffn_"),
            "LLAMA_CPP_BUNDLED.md must include the canonical MoE expert FFN tensor regex pattern.");
    }

    [Test]
    public void BundledDoc_DocumentsKvCacheBudgetAndSafetyNets()
    {
        string text = ReadBundledDoc();

        Assert.That(text, Does.Contain("KV-cache-aware VRAM math"),
            "LLAMA_CPP_BUNDLED.md must include the KV-cache-aware VRAM math section.");
        Assert.That(text, Does.Contain("KV bytes/token"),
            "LLAMA_CPP_BUNDLED.md must spell out the per-token KV bytes formula.");

        Assert.That(text, Does.Contain("Backend-specific safety nets"),
            "LLAMA_CPP_BUNDLED.md must include the backend-specific safety nets section.");
        // Each safety net must be named so the operator can match it to
        // their llama-server log.
        Assert.That(text, Does.Contain("#14999"),
            "LLAMA_CPP_BUNDLED.md must cite upstream #14999 (MoE + --no-mmap memory crit).");
        Assert.That(text, Does.Contain("#4903"),
            "LLAMA_CPP_BUNDLED.md must cite ROCm #4903 (HIP + --mlock).");
        Assert.That(text, Does.Contain("--mlock --prio 2"),
            "LLAMA_CPP_BUNDLED.md must document the Apple Silicon --mlock --prio 2 recipe.");
    }

    // ---------- Pass 351: full curated-library catalog ----------

    [Test]
    public void InstallScript_CatalogCoversEveryCuratedFamily()
    {
        string text = ReadInstallScript();

        // Pass 448: the recommendation catalog must include exactly the
        // current supported local lanes from LOCAL_MODELS_INVENTORY.md.
        string[] requiredFamilies = new[]
        {
            "Qwen3.5-9B-UD-Q6_K_XL",
            "gemma-4-12b-it-UD-Q6_K_XL",
        };

        foreach (string family in requiredFamilies)
        {
            Assert.That(text, Does.Contain(family),
                $"install-llama-cpp.ps1 catalog must include '{family}'.");
        }

        Assert.That(text, Does.Not.Contain("Qwen3.6-35B-A3B"));
        Assert.That(text, Does.Not.Contain("Qwen3-Coder-Next"));
        Assert.That(text, Does.Not.Contain("MiniMax"));
        Assert.That(text, Does.Not.Contain("DeepSeekV4-Flash"));
    }

    [Test]
    public void InstallScript_CatalogEntriesCarrySamplerAndPrioMetadata()
    {
        string text = ReadInstallScript();

        // Each catalog entry must declare a Sampler field so
        // auto-launch picks the right --temp/--top-p/--top-k/--min-p.
        Assert.That(text, Does.Contain("Sampler      = 'qwen35'"),
            "install-llama-cpp.ps1 Qwen3.5 catalog entry must declare Sampler='qwen35'.");
        Assert.That(text, Does.Contain("Sampler      = 'gemma'"),
            "install-llama-cpp.ps1 Gemma 4 catalog entry must declare Sampler='gemma'.");

        Assert.That(text, Does.Match(@"Qwen3\.5-9B-UD-Q6_K_XL[\s\S]{0,500}Prio\s*=\s*0"));
        Assert.That(text, Does.Match(@"gemma-4-12b-it-UD-Q6_K_XL[\s\S]{0,500}Prio\s*=\s*0"));
    }

    [Test]
    public void InstallScript_HasGetSamplerFlagsHelper()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("function Get-SamplerFlags"),
            "install-llama-cpp.ps1 must expose Get-SamplerFlags for auto-launch's per-model sampler.");
        // Each supported family's canonical numbers must appear.
        Assert.That(text, Does.Contain("'qwen35'").And.Contain("'0.7'").And.Contain("'0.8'"),
            "Get-SamplerFlags qwen35 branch must emit temp 0.7 / top-p 0.8.");
        Assert.That(text, Does.Contain("'gemma'").And.Contain("'0.95'"),
            "Get-SamplerFlags gemma branch must emit top-p 0.95.");
    }

    [Test]
    public void InstallScript_AutoLaunchEmitsPerModelSamplerAndPrio()
    {
        string text = ReadInstallScript();

        // Auto-launch must call Get-SamplerFlags + honor Prio + only
        // emit --chat-template-kwargs for Qwen profiles.
        Assert.That(text, Does.Contain("Get-SamplerFlags -Sampler $recommendation.Sampler"),
            "install-llama-cpp.ps1 auto-launch must call Get-SamplerFlags for per-model sampler emission.");
        Assert.That(text, Does.Contain("recommendation.Prio -gt 0"),
            "install-llama-cpp.ps1 auto-launch must check recommendation.Prio before emitting --prio.");
        Assert.That(text, Does.Contain("recommendation.Sampler -eq 'qwen35'"),
            "install-llama-cpp.ps1 must gate the --chat-template-kwargs emission to Qwen profiles only.");
    }

    [Test]
    public void InstallScript_CatalogDoesNotTargetRetiredMultiShardModels()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Not.Contain("-00001-of-"));
        Assert.That(text, Does.Not.Contain("Qwen3-Coder-Next"));
        Assert.That(text, Does.Not.Contain("MiniMax"));
    }

    // ---------- Pass 352: end-to-end wire + sampler propagation ----------

    [Test]
    public void ConnectScript_WriteConfigPropagatesPerFamilySampler()
    {
        string text = ReadConnectScript();

        // Pass 352: -WriteConfig now mutates PalLLM.Inference's sampler
        // fields when -ModelProfile is set, not just BaseUrl/Model/Enabled.
        // Without this, PalLLM keeps sending Qwen3.5 sampler values even
        // when llama-server has a Gemma model loaded -- PalLLM's
        // per-request body overrides llama-server's defaults.
        Assert.That(text, Does.Contain("PSBoundParameters.ContainsKey('ModelProfile')"),
            "connect-llamacpp.ps1 -WriteConfig must gate the sampler propagation on -ModelProfile being explicitly set.");
        Assert.That(text, Does.Contain("$samplerSnapshot"),
            "connect-llamacpp.ps1 must build a $samplerSnapshot hashtable per ModelProfile.");

        // Each supported profile's canonical sampler must appear in the snapshot.
        Assert.That(text, Does.Match(@"'qwen35'\s*\{[^}]*Temperature = 0\.7"),
            "connect-llamacpp.ps1 qwen35 sampler snapshot must set Temperature=0.7.");
        Assert.That(text, Does.Match(@"'qwen35'\s*\{[^}]*PresencePenalty = 1\.5"),
            "connect-llamacpp.ps1 qwen35 sampler snapshot must set PresencePenalty=1.5.");
        Assert.That(text, Does.Match(@"'gemma'\s*\{[^}]*TopP = 0\.95"),
            "connect-llamacpp.ps1 gemma sampler snapshot must set TopP=0.95.");
        Assert.That(text, Does.Match(@"'gemma'\s*\{[^}]*PresencePenalty = \$null"),
            "connect-llamacpp.ps1 gemma sampler snapshot must not set a presence penalty.");
    }

    [Test]
    public void InstallScript_HasWireConfigSwitch_AndAutoLaunchImpliesIt()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("[switch]$WireConfig"),
            "install-llama-cpp.ps1 must expose -WireConfig.");
        // -AutoLaunch must imply -WireConfig so a single install command
        // produces a fully wired runtime.
        Assert.That(text, Does.Match(@"WireConfig\.IsPresent\s+-or\s+\$AutoLaunch\.IsPresent"),
            "install-llama-cpp.ps1 must compute effectiveWireConfig as (-WireConfig OR -AutoLaunch) so the autoflow is one command.");

        // The wire step must invoke connect-llamacpp.ps1 with the
        // recommended ModelProfile, ContextSize, GpuLayers, NCpuMoe,
        // QuantizedKv, Prio so PalLLM's appsettings carries the full
        // per-family recipe.
        Assert.That(text, Does.Contain("connect-llamacpp.ps1"),
            "install-llama-cpp.ps1 -WireConfig must invoke connect-llamacpp.ps1.");
        Assert.That(text, Does.Contain("ModelProfile  = $recommendation.Sampler"),
            "install-llama-cpp.ps1 -WireConfig must pass the recommendation's Sampler as -ModelProfile to connect-llamacpp.ps1.");
    }

    [Test]
    public void InstallScript_WireConfigRunsBeforeAutoLaunch()
    {
        string text = ReadInstallScript();

        // Ordering matters: the wire step must run BEFORE the blocking
        // auto-launch call. Otherwise the appsettings update never
        // happens (auto-launch's `& $serverExe @launchArgs` blocks).
        int wireIdx = text.IndexOf("Wiring PalLLM appsettings", System.StringComparison.Ordinal);
        int launchIdx = text.IndexOf("Auto-launching llama-server with the recommended", System.StringComparison.Ordinal);
        Assert.That(wireIdx, Is.GreaterThan(0),
            "install-llama-cpp.ps1 must contain the 'Wiring PalLLM appsettings' block.");
        Assert.That(launchIdx, Is.GreaterThan(0),
            "install-llama-cpp.ps1 must contain the 'Auto-launching llama-server' block.");
        Assert.That(wireIdx, Is.LessThan(launchIdx),
            "install-llama-cpp.ps1 wire step must run BEFORE auto-launch (auto-launch is blocking).");
    }

    // ---------- Pass 353: first-impression docs surface the bundled engine ----------

    [Test]
    public void FirstImpressionDocs_AllMentionInstallLlamaCpp()
    {
        // After 6 passes of bundled-engine work (Passes 344, 347-352)
        // the install-llama-cpp.ps1 script is the operator's one-command
        // path from clone to running local LLM. The first-impression
        // docs MUST surface it -- the Pass 353 audit found it mentioned
        // zero times across README/QUICKREF/CHEAT_SHEET despite the
        // substantial install surface that landed.
        string[] expectedDocs = new[]
        {
            Path.Combine("README.md"),
            Path.Combine("docs", "QUICKREF.md"),
            Path.Combine("docs", "CHEAT_SHEET.md"),
        };

        foreach (string relative in expectedDocs)
        {
            string path = LocateRepoFile(relative.Split(Path.DirectorySeparatorChar));
            string text = File.ReadAllText(path);
            Assert.That(text, Does.Contain("install-llama-cpp"),
                $"{relative} must reference scripts/install-llama-cpp.ps1 so the operator's first-impression docs surface the one-command install + wire + launch flow. Bundled-engine work has been invisible without this.");
        }
    }

    [Test]
    public void Readme_QuickstartIncludesOneCommandInferenceSetup()
    {
        string path = LocateRepoFile("README.md");
        string text = File.ReadAllText(path);

        // The Quickstart should contain the -AutoLaunch one-command
        // path so a new operator sees it before reaching the doc tree.
        Assert.That(text, Does.Contain("-AutoLaunch"),
            "README must document the -AutoLaunch one-command flow somewhere in the Quickstart.");
        Assert.That(text, Does.Contain("local inference").Or.Contain("Local inference"),
            "README must label the section so it's discoverable (e.g. 'Run with local inference').");
    }

    [Test]
    public void CheatSheet_DocumentsBundledInferenceSection()
    {
        string path = LocateRepoFile("docs", "CHEAT_SHEET.md");
        string text = File.ReadAllText(path);

        Assert.That(text, Does.Contain("Bundled local inference").Or.Contain("Bundled inference"),
            "CHEAT_SHEET.md must include a 'Bundled local inference' section.");
        Assert.That(text, Does.Contain("-AutoLaunch"),
            "CHEAT_SHEET.md must show the -AutoLaunch one-command path.");
    }

    [Test]
    public void Quickref_BundledEngineSection_LinksToDeepDive()
    {
        string path = LocateRepoFile("docs", "QUICKREF.md");
        string text = File.ReadAllText(path);

        Assert.That(text, Does.Contain("Bundled inference engine"),
            "QUICKREF.md must include a 'Bundled inference engine' section heading.");
        Assert.That(text, Does.Contain("LLAMA_CPP_BUNDLED.md"),
            "QUICKREF.md's bundled-engine section must link to LLAMA_CPP_BUNDLED.md as the deep-dive.");
    }

    // ---------- Pass 356: v1.0 reference-rig scope tighten ----------

    [Test]
    public void MinimumRequirementsDoc_DeclaresReferenceRig()
    {
        string path = LocateRepoFile("docs", "MINIMUM_REQUIREMENTS.md");
        string text = File.ReadAllText(path);

        // Pass 373: reference rig lowered from RTX 3090 / 32 GB / 5800X3D
        // to RTX 3060 12 GB / 16 GB / 6-core. The doc must name each
        // component of the new baseline unambiguously. The 3090 path is
        // retained as a higher-tier auto-graduation row in the same doc.
        Assert.That(text, Does.Contain("RTX 3060 12 GB"),
            "MINIMUM_REQUIREMENTS.md must name the new GPU baseline (RTX 3060 12 GB).");
        Assert.That(text, Does.Contain("16 GB DDR4").Or.Contain("16 GB DDR5").Or.Contain("16 GB DDR4 / DDR5"),
            "MINIMUM_REQUIREMENTS.md must name the new RAM baseline (16 GB DDR4/DDR5).");
        Assert.That(text, Does.Contain("6-core"),
            "MINIMUM_REQUIREMENTS.md must name the new CPU baseline (6-core).");
        Assert.That(text, Does.Contain("Windows 10").Or.Contain("Windows 11"),
            "MINIMUM_REQUIREMENTS.md must name the OS baseline (Windows 10/11).");
        Assert.That(text, Does.Contain("Qwen3.5-9B-UD-Q6_K_XL"),
            "MINIMUM_REQUIREMENTS.md must name the Qwen3.5 fast Worker lane.");
        Assert.That(text, Does.Contain("gemma-4-12b-it-UD-Q6_K_XL"),
            "MINIMUM_REQUIREMENTS.md must name the Gemma 4 12B smart/multimodal lane.");
    }

    [Test]
    public void PostReleaseAnnex_DocumentsDeferredSurfaces()
    {
        string path = LocateRepoFile("docs", "POST_RELEASE_ANNEX.md");
        string text = File.ReadAllText(path);

        // Each deferred surface must be enumerated so the
        // re-promotion contract is auditable.
        Assert.That(text, Does.Contain("MiniMax-M2.7"),
            "POST_RELEASE_ANNEX.md must catalogue MiniMax-M2.7 as deferred.");
        Assert.That(text, Does.Contain("DeepSeekV4-Flash"),
            "POST_RELEASE_ANNEX.md must catalogue DeepSeekV4-Flash as deferred.");
        Assert.That(text, Does.Contain("Qwen3-Coder-Next"),
            "POST_RELEASE_ANNEX.md must catalogue Qwen3-Coder-Next as deferred.");
        Assert.That(text, Does.Contain("Apple Silicon").Or.Contain("macOS"),
            "POST_RELEASE_ANNEX.md must catalogue Apple Silicon / macOS as deferred.");
        Assert.That(text, Does.Contain("CPU-only").Or.Contain("CPU only"),
            "POST_RELEASE_ANNEX.md must catalogue CPU-only inference as deferred.");
        Assert.That(text, Does.Contain("multi-GPU"),
            "POST_RELEASE_ANNEX.md must catalogue multi-GPU as deferred.");
    }

    [Test]
    public void Readme_HasMinimumRequirementsSection_NearTop()
    {
        string path = LocateRepoFile("README.md");
        string text = File.ReadAllText(path);

        Assert.That(text, Does.Contain("Minimum requirements"),
            "README.md must include a 'Minimum requirements' section.");
        // Pass 373: lowered minimum names the RTX 3060 12 GB baseline.
        Assert.That(text, Does.Contain("RTX 3060 12 GB"),
            "README's minimum-requirements section must name the new GPU baseline (RTX 3060 12 GB).");
        Assert.That(text, Does.Contain("MINIMUM_REQUIREMENTS.md"),
            "README must link to MINIMUM_REQUIREMENTS.md as the authoritative deep-dive.");
        Assert.That(text, Does.Contain("POST_RELEASE_ANNEX.md"),
            "README must link to POST_RELEASE_ANNEX.md so operators on non-target hardware know where the v1.x roadmap lives.");

        // Discoverability: the minimum-requirements section must
        // appear BEFORE the Quickstart so an operator reads the
        // reference rig spec before learning the install flow.
        int reqIdx = text.IndexOf("Minimum requirements", System.StringComparison.Ordinal);
        int quickstartIdx = text.IndexOf("## Quickstart", System.StringComparison.Ordinal);
        Assert.That(reqIdx, Is.GreaterThan(0));
        Assert.That(quickstartIdx, Is.GreaterThan(0));
        Assert.That(reqIdx, Is.LessThan(quickstartIdx),
            "Minimum-requirements section must come BEFORE Quickstart so operators see the reference rig spec first.");
    }

    [Test]
    public void InstallScript_HasShippingTargetCheck()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("Minimum-requirements check"),
            "install-llama-cpp.ps1 must run a shipping-target check before download.");
        Assert.That(text, Does.Contain("OFF-TARGET"),
            "install-llama-cpp.ps1 must label non-target hardware as OFF-TARGET in the operator output.");
        Assert.That(text, Does.Contain("MINIMUM_REQUIREMENTS.md"),
            "install-llama-cpp.ps1 must point at MINIMUM_REQUIREMENTS.md when off-target.");
        Assert.That(text, Does.Contain("POST_RELEASE_ANNEX.md"),
            "install-llama-cpp.ps1 must point at POST_RELEASE_ANNEX.md when off-target.");
    }

    [Test]
    public void InstallScript_CatalogContainsOnlyCurrentSupportedFamilies()
    {
        string text = ReadInstallScript();

        Assert.That(text, Does.Contain("Qwen3.5-9B-UD-Q6_K_XL"));
        Assert.That(text, Does.Contain("gemma-4-12b-it-UD-Q6_K_XL"));
        Assert.That(text, Does.Not.Contain("PostRelease"));
        Assert.That(text, Does.Not.Contain("Qwen3.6-35B-A3B"));
        Assert.That(text, Does.Not.Contain("Qwen3-Coder-Next"));
        Assert.That(text, Does.Not.Contain("MiniMax"));
        Assert.That(text, Does.Not.Contain("DeepSeekV4-Flash"));
    }

    [Test]
    public void BundledDoc_HasShippingTargetCallout()
    {
        string text = ReadBundledDoc();

        Assert.That(text, Does.Contain("v1.0 shipping target"),
            "LLAMA_CPP_BUNDLED.md must open with a 'v1.0 shipping target' callout.");
        Assert.That(text, Does.Contain("MINIMUM_REQUIREMENTS.md"),
            "LLAMA_CPP_BUNDLED.md must link to MINIMUM_REQUIREMENTS.md in the callout.");
        Assert.That(text, Does.Contain("POST_RELEASE_ANNEX.md"),
            "LLAMA_CPP_BUNDLED.md must link to POST_RELEASE_ANNEX.md in the callout.");
    }

    // ---------- Pass 357: below-reference escape paths ----------

    [Test]
    public void ConnectCloudScript_ExistsWithProviderPresets()
    {
        string path = LocateRepoFile("scripts", "connect-cloud.ps1");
        string text = File.ReadAllText(path);

        // Pass 357: scripts/connect-cloud.ps1 is the shipping escape
        // path for below-reference hardware. Must expose -Provider
        // with the full preset list and validate inputs.
        Assert.That(text, Does.Contain("ValidateSet('openai', 'groq', 'together', 'openrouter', 'deepseek', 'mistral', 'custom')"),
            "connect-cloud.ps1 must expose -Provider with all preset values.");
        Assert.That(text, Does.Contain("https://api.openai.com/v1/"),
            "connect-cloud.ps1 must include the OpenAI base URL preset.");
        Assert.That(text, Does.Contain("https://api.groq.com/openai/v1/"),
            "connect-cloud.ps1 must include the Groq base URL preset.");
        Assert.That(text, Does.Contain("$env:PalLLM__Inference__ApiKey"),
            "connect-cloud.ps1 must mention the env-var path as the preferred ApiKey delivery method.");
    }

    [Test]
    public void InstallScript_OffTargetSkipsLocalInstall_AndPointsAtEscapePaths()
    {
        string text = ReadInstallScript();

        // Pass 357: off-target hardware (without explicit -Backend
        // override) must NOT proceed with local install. Must point
        // at the two shipping escape paths.
        Assert.That(text, Does.Contain("offTargetSkipLocal"),
            "install-llama-cpp.ps1 must compute the offTargetSkipLocal gate.");
        Assert.That(text, Does.Contain("connect-cloud.ps1"),
            "install-llama-cpp.ps1 off-target path must point at connect-cloud.ps1.");
        Assert.That(text, Does.Contain("Cloud API"),
            "install-llama-cpp.ps1 off-target path must label escape #1 as Cloud API.");
        Assert.That(text, Does.Contain("Remote PC"),
            "install-llama-cpp.ps1 off-target path must label escape #2 as Remote PC.");
        Assert.That(text, Does.Contain("LlamaCppUrl http://"),
            "install-llama-cpp.ps1 off-target path must show the remote-PC connect command.");
        Assert.That(text, Does.Contain("Skipping local install"),
            "install-llama-cpp.ps1 must explicitly skip local install on off-target hardware (Pass 357 hard-gate).");
    }

    [Test]
    public void MinimumRequirementsDoc_DocumentsBothEscapePaths()
    {
        string path = LocateRepoFile("docs", "MINIMUM_REQUIREMENTS.md");
        string text = File.ReadAllText(path);

        Assert.That(text, Does.Contain("Escape path #1: cloud API"),
            "MINIMUM_REQUIREMENTS.md must document the cloud API escape path.");
        Assert.That(text, Does.Contain("Escape path #2: remote PC"),
            "MINIMUM_REQUIREMENTS.md must document the remote-PC escape path.");
        Assert.That(text, Does.Contain("connect-cloud.ps1"),
            "MINIMUM_REQUIREMENTS.md must reference scripts/connect-cloud.ps1.");
        Assert.That(text, Does.Contain("LlamaCppUrl http://"),
            "MINIMUM_REQUIREMENTS.md must show the LAN/VPN remote-PC pattern with -LlamaCppUrl.");
        Assert.That(text, Does.Contain("OpenAI-compatible"),
            "MINIMUM_REQUIREMENTS.md must call out OpenAI-compatible as the cloud provider contract.");
    }

    [Test]
    public void PostReleaseAnnex_DistinguishesShippingEscapePaths_FromDeferred()
    {
        string path = LocateRepoFile("docs", "POST_RELEASE_ANNEX.md");
        string text = File.ReadAllText(path);

        // Pass 357: clarify that cloud + remote PC are SHIPPING
        // (not deferred). The deferred items are the local paths
        // on alternate hardware.
        Assert.That(text, Does.Contain("REPLACE local inference"),
            "POST_RELEASE_ANNEX.md must have a section labelling cloud + remote PC as the shipping replacement for below-reference local inference.");
        Assert.That(text, Does.Contain("connect-cloud.ps1"),
            "POST_RELEASE_ANNEX.md must reference connect-cloud.ps1.");
    }

    // ---------- Pass 373: lower minimum spec to RTX 3060 / 16 GB / 6-core ----------

    [Test]
    public void InstallScript_DoesNotRecommendMoeOffloadByDefault()
    {
        string script = ReadInstallScript();

        Assert.That(script, Does.Match(@"Qwen3\.5-9B-UD-Q6_K_XL[\s\S]{0,700}IsMoE\s*=\s*\$false"),
            "The Qwen3.5 fast Worker lane must not require MoE offload by default.");
        Assert.That(script, Does.Match(@"gemma-4-12b-it-UD-Q6_K_XL[\s\S]{0,700}IsMoE\s*=\s*\$false"),
            "The Gemma 4 smart lane must not require MoE offload by default.");
        Assert.That(script, Does.Not.Contain("Qwen3.6-35B-A3B"));
    }

    [Test]
    public void MinimumRequirementsDoc_DocumentsLoweredReferenceRig()
    {
        // Pass 373: docs/MINIMUM_REQUIREMENTS.md must declare the new
        // RTX 3060 / 16 GB / 6-core reference rig and keep both escape
        // paths (cloud API + remote PC) for sub-minimum hardware.
        string text = File.ReadAllText(LocateRepoFile("docs", "MINIMUM_REQUIREMENTS.md"));

        Assert.That(text, Does.Contain("RTX 3060 12 GB"),
            "MINIMUM_REQUIREMENTS.md must name the RTX 3060 12 GB reference card.");
        Assert.That(text, Does.Contain("16 GB"),
            "MINIMUM_REQUIREMENTS.md must name the 16 GB RAM minimum.");
        Assert.That(text, Does.Contain("6-core"),
            "MINIMUM_REQUIREMENTS.md must name the 6-core CPU minimum.");
        Assert.That(text, Does.Contain("Qwen3.5-9B-UD-Q6_K_XL"),
            "MINIMUM_REQUIREMENTS.md must name the fast Worker model that ships on the reference rig.");
        Assert.That(text, Does.Contain("gemma-4-12b-it-UD-Q6_K_XL"),
            "MINIMUM_REQUIREMENTS.md must name the smart/multimodal model kept in the supported local set.");
        Assert.That(text, Does.Contain("connect-cloud.ps1"),
            "MINIMUM_REQUIREMENTS.md must keep the cloud-API escape path callout.");
        Assert.That(text, Does.Contain("connect-llamacpp.ps1"),
            "MINIMUM_REQUIREMENTS.md must keep the remote-PC escape path callout.");
        Assert.That(text, Does.Contain("Pass 448"),
            "MINIMUM_REQUIREMENTS.md must reference Pass 448 in its current-lane callout.");
    }

    [Test]
    public void HandoffDoc_ExposesCodexHandoffSection()
    {
        // Pass 374: docs/HANDOFF.md must have a "Codex handoff" section
        // near the top that an incoming agent can read in under a minute
        // and have everything it needs to start safely. Pinning the
        // section ensures the briefing doesn't drift back into prose-only
        // mode that takes longer to digest.
        string text = File.ReadAllText(LocateRepoFile("docs", "HANDOFF.md"));

        Assert.That(text, Does.Contain("Codex handoff"),
            "HANDOFF.md must declare a 'Codex handoff' section so " +
            "an incoming agent's first read is targeted.");
        Assert.That(text, Does.Contain("do not touch without an ADR"),
            "HANDOFF.md Codex section must enumerate the do-not-touch surfaces.");
        Assert.That(text, Does.Contain("verification ritual"),
            "HANDOFF.md Codex section must spell out the per-pass verification ritual.");
        Assert.That(text, Does.Contain("pal.ps1 handoff"),
            "HANDOFF.md Codex section must point at the `pal handoff` verb.");
        Assert.That(text, Does.Contain("REFACTORING_ROADMAP.md"),
            "HANDOFF.md Codex section must point at the refactoring roadmap as the next queued work.");
    }

    [Test]
    public void PalScript_ExposesHandoffVerb()
    {
        // Pass 374: `pal handoff` must exist as a verb and produce a
        // self-contained briefing. We assert against the script source
        // because running the verb in-process here would couple the
        // test to PowerShell + git availability.
        string palScript = File.ReadAllText(LocateRepoFile("pal.ps1"));

        Assert.That(palScript, Does.Contain("Run-Handoff"),
            "pal.ps1 must define a Run-Handoff function.");
        Assert.That(palScript, Does.Contain("'handoff'"),
            "pal.ps1 verb table must contain a 'handoff' entry.");
        Assert.That(palScript, Does.Contain("Verify baseline before changing"),
            "Run-Handoff must explicitly tell the next agent to run the audit first.");
        Assert.That(palScript, Does.Contain("Do-not-touch list"),
            "Run-Handoff must surface the do-not-touch surfaces inline.");
        Assert.That(palScript, Does.Contain("$handoffPath = Join-Path $repoRoot 'docs/HANDOFF.md'"),
            "Run-Handoff must read the central handoff doc before rendering its pass label.");
        Assert.That(palScript, Does.Contain("$handoffPass"),
            "Run-Handoff must derive its pass label from docs/HANDOFF.md instead of hard-coding it.");
        Assert.That(palScript, Does.Contain("$title = \"PalLLM handoff briefing ($passLabel)\""),
            "Run-Handoff title must be assembled from the derived pass label.");
        Assert.That(palScript, Does.Not.Contain("PalLLM handoff briefing (Pass 417)"),
            "Run-Handoff must not reintroduce the stale hard-coded Pass 417 title.");

        string palJson = File.ReadAllText(LocateRepoFile("pal.json"));
        Assert.That(palJson, Does.Contain("handoff").Or.Contain("\"handoff\""),
            "pal.json verb manifest must include the handoff verb so agents reading the JSON catalogue see it.");
    }

    [Test]
    public void ReadmeQuickstart_DocumentsLoweredHardwareTier()
    {
        // README.md's hardware section must reflect the Pass 373 lowered
        // minimum — no stale 24 GB VRAM Ampere-class copy that would
        // scare off 3060 / 4060 / 4070 operators who actually do fit
        // the new minimum.
        string readme = File.ReadAllText(LocateRepoFile("README.md"));

        Assert.That(readme, Does.Contain("RTX 3060 12 GB"),
            "README.md must name the RTX 3060 12 GB reference card after Pass 373.");
        Assert.That(readme, Does.Contain("16 GB DDR"),
            "README.md must name the 16 GB RAM minimum after Pass 373.");
        Assert.That(readme, Does.Not.Contain("24 GB VRAM Ampere-class"),
            "Stale 24 GB VRAM Ampere-class copy must not remain in README.md after Pass 373.");
    }

    // ---------- Helpers ----------

    private static void AssertNoRetiredLocalModelPromotion(string text, string label)
    {
        foreach (string retired in new[]
        {
            "Qwen3.6-35B-A3B",
            "Qwen3.6-27B",
            "gemma-4-E4B",
            "gemma-3-4b",
            "Qwen3-Coder-Next",
            "MiniMax",
            "DeepSeekV4",
        })
        {
            Assert.That(text, Does.Not.Contain(retired),
                $"{label} must not promote retired local model family '{retired}' in the active llama.cpp operator surface.");
        }
    }

    private static void AssertNumericFieldEqual(JsonElement parent, string name, double expected)
    {
        Assert.That(parent.TryGetProperty(name, out JsonElement child), Is.True,
            $"appsettings.json missing PalLLM.Inference.{name}");
        Assert.That(child.ValueKind == JsonValueKind.Number, Is.True,
            $"PalLLM.Inference.{name} must be a JSON number, was {child.ValueKind}");
        double actual = child.GetDouble();
        Assert.That(actual, Is.EqualTo(expected).Within(1e-9),
            $"PalLLM.Inference.{name} expected {expected}, got {actual}");
    }

    private static string ReadInstallScript() => File.ReadAllText(LocateRepoFile("scripts", "install-llama-cpp.ps1"));
    private static string ReadConnectScript() => File.ReadAllText(LocateRepoFile("scripts", "connect-llamacpp.ps1"));
    private static string ReadBundledDoc() => File.ReadAllText(LocateRepoFile("docs", "LLAMA_CPP_BUNDLED.md"));

    private static string LocateShippingAppsettings() => LocateRepoFile("src", "PalLLM.Sidecar", "appsettings.json");

    private static string LocateRepoFile(params string[] segments)
    {
        // Walk up looking for the repo root (identified by the
        // unique PalLLM.sln marker), then resolve the requested
        // path relative to that. Necessary for top-level files
        // like README.md whose name collides with subdirectory
        // siblings (tests/README.md, .github/README.md, etc.).
        string testBin = TestContext.CurrentContext.TestDirectory;
        DirectoryInfo? current = new(testBin);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PalLLM.sln")))
            {
                string candidate = Path.Combine(new[] { current.FullName }.Concat(segments).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                throw new FileNotFoundException(
                    $"Could not locate {string.Join(Path.DirectorySeparatorChar, segments)} under the repo root at {current.FullName}.");
            }
            current = current.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate the repo root (no PalLLM.sln found walking up from the test bin directory).");
    }
}
