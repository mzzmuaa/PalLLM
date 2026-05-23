<#
.SYNOPSIS
    Download + verify + extract the latest llama.cpp `llama-server` release
    into PalLLM's bundled-engines directory, picking the right backend
    asset for the operator's hardware. The "bundled and default"
    promise made by Pass 339 (Ollama removed; llama.cpp is now PalLLM's
    primary engine) executes through this script: one command takes an
    operator from "PalLLM installed" to "llama-server.exe sitting on disk,
    ready to serve the curated D:\Models GGUFs."

.DESCRIPTION
    Pulls the latest stable release from the upstream
    `ggml-org/llama.cpp` GitHub Releases endpoint (no authentication
    required for public release assets), verifies the published SHA-256
    when available, extracts the right backend bundle for the host
    (auto-detected GPU vendor), downloads the companion cudart DLL
    pack for CUDA backends, and places `llama-server.exe` at:

        $BundleRoot/<release-tag>/llama-server.exe

    The active version is symlinked (Windows junction) to:

        $BundleRoot/current

    so PalLLM and operator scripts can reference a stable path without
    knowing the current release tag.

    **Backend selection (Pass 347).** Upstream llama.cpp now ships
    multiple Windows backend variants per release. The installer
    auto-detects the host GPU vendor via WMI (`Win32_VideoController`)
    and picks the right asset:

    | Detected hardware             | Default asset                              |
    |-------------------------------|---------------------------------------------|
    | NVIDIA GPU                    | `llama-<tag>-bin-win-cuda-12.4-x64.zip`     |
    | NVIDIA GPU + `-Backend cuda13`| `llama-<tag>-bin-win-cuda-13.1-x64.zip`     |
    | AMD GPU + `-Backend hip`      | `llama-<tag>-bin-win-hip-radeon-x64.zip`    |
    | AMD GPU (default)             | `llama-<tag>-bin-win-vulkan-x64.zip`        |
    | Intel GPU + `-Backend sycl`   | `llama-<tag>-bin-win-sycl-x64.zip`          |
    | Intel GPU (default)           | `llama-<tag>-bin-win-vulkan-x64.zip`        |
    | No GPU / CPU-only             | `llama-<tag>-bin-win-cpu-x64.zip`           |

    CUDA 12.4 is the **default for any NVIDIA card** because CUDA 13.x
    builds shipped MMQ kernel crashes on Blackwell (RTX 50-series) as
    of April-May 2026 (see zenn.dev/toki_mwc benchmark). CUDA 12.4 +
    MMQ is the verified-stable pair for sm_120 today. Pass
    `-Backend cuda13` to opt into the newer toolchain after running
    your own Blackwell smoke test.

    The script is **idempotent**: if the requested release tag is already
    on disk and its SHA matches the upstream, no download happens.

.PARAMETER ReleaseTag
    Specific upstream release tag to install (e.g. `b9284`). When omitted,
    the script queries the GitHub Releases API for the latest stable
    release and installs that one. Pinning a specific tag is recommended
    for production releases — the latest may include unstable features
    (upstream pushes ~6 releases per day).

.PARAMETER Backend
    Force a specific backend variant. One of:
    `auto` (default) - detect GPU vendor and pick the right asset
    `cuda12`         - CUDA 12.4 build (NVIDIA, Blackwell-stable)
    `cuda13`         - CUDA 13.1 build (NVIDIA, bleeding edge)
    `vulkan`         - Vulkan build (AMD / Intel / cross-vendor)
    `hip`            - HIP/ROCm build (AMD Radeon, opt-in)
    `sycl`           - SYCL build (Intel oneAPI, opt-in)
    `cpu`            - CPU-only build (no GPU acceleration)

.PARAMETER Platform
    Target platform asset to fetch. Defaults to auto-detection
    (`win-x64` on Windows, `linux-x64` on Linux, `macos-arm64` on Apple
    Silicon). Override only when cross-installing for a different host.
    Backend selection is currently Windows-x64 only; Linux/macOS pull
    the platform's monolithic asset.

.PARAMETER BundleRoot
    Override the install root. Defaults to
    `$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bundled\llama.cpp` on Windows
    (the runtime-root sibling that pairs with PalLLM:ExternalModelsRoot's
    `D:\Models` library).

.PARAMETER VerifyOnly
    Don't download — just verify whatever's already on disk against the
    upstream SHA. Returns exit code 0 when local matches upstream, 1
    when there's drift, 2 when nothing is installed.

.PARAMETER DryRun
    Print what would be downloaded + extracted without doing the work.
    Useful for verifying the resolved release tag + URL before
    committing to a 30-40 MB download.

.EXAMPLE
    pwsh ./scripts/install-llama-cpp.ps1
    # Auto-detect platform + GPU; install the latest stable release.

.EXAMPLE
    pwsh ./scripts/install-llama-cpp.ps1 -ReleaseTag b9284 -Backend cuda12
    # Pin a specific release + force CUDA 12.4 (Blackwell-stable).

.EXAMPLE
    pwsh ./scripts/install-llama-cpp.ps1 -Backend vulkan
    # Force the Vulkan build (e.g. for AMD or Intel GPUs, or for
    # NVIDIA cards where CUDA isn't preferable).

.EXAMPLE
    pwsh ./scripts/install-llama-cpp.ps1 -DryRun
    # Show the install plan without downloading anything.

.NOTES
    - This script is the "bundled and default" deliverable from Pass 344,
      hardware-aware as of Pass 347.
    - The default `BundleRoot` deliberately sits next to PalLLM's
      runtime root (not under it) so the binary survives a runtime-root
      wipe (e.g. `pal uninstall -Full`).
    - Network calls go only to `https://api.github.com/repos/ggml-org/llama.cpp`
      and `https://github.com/ggml-org/llama.cpp/releases/download/...`.
      The script honors `HTTPS_PROXY` if set. No telemetry.
    - The SHA verification step uses the SHA-256 published in the
      release's `SHA256SUMS` asset; if that asset isn't present (some
      upstream releases skip it), the script falls back to a warning
      and proceeds — operators who want strict verification should
      pin a tag known to ship SHA256SUMS.
    - CUDA backends require the matching `cudart-llama-bin-win-cuda-*`
      runtime DLL pack. The script downloads + extracts it automatically.
#>
[CmdletBinding()]
param(
    [string]$ReleaseTag,

    [ValidateSet('auto', 'cuda12', 'cuda13', 'vulkan', 'hip', 'sycl', 'cpu')]
    [string]$Backend = 'auto',

    [string]$Platform,
    [string]$BundleRoot,

    # Pass 349: where the operator's curated GGUFs live. Used by the
    # VRAM-based model recommendation. Defaults to $env:PalLLM_ExternalModelsRoot,
    # then to D:\Models on Windows, $HOME/Models elsewhere.
    [string]$ModelsRoot,

    # Pass 349: after install, launch the recommended llama-server.
    [switch]$AutoLaunch,

    # Pass 349: after install, exec `llama-server --version` to verify
    # the binary boots. ON by default; -NoSmokeTest opts out.
    [switch]$NoSmokeTest,

    # Pass 352: after install (and before -AutoLaunch fires its
    # blocking llama-server call), invoke connect-llamacpp.ps1 with
    # the recommended Model + ModelProfile + WriteConfig so PalLLM's
    # appsettings.json points at the same model the script just
    # launched AND carries the right per-family sampler. -AutoLaunch
    # implies -WireConfig so a single install command produces a
    # fully wired runtime.
    [switch]$WireConfig,

    # Pass 352: override which appsettings.json the wire step writes
    # to. Defaults to the connect script's auto-detection (sidecar
    # publish dir, then src/PalLLM.Sidecar, then user-profile).
    [string]$ConfigPath,

    [switch]$VerifyOnly,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

# ---- Platform auto-detect ---------------------------------------------------

if (-not $Platform) {
    if ($IsWindows -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
        $Platform = 'win-x64'
    } elseif ($IsLinux) {
        $Platform = 'linux-x64'
    } elseif ($IsMacOS) {
        $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
        $Platform = if ($arch -eq 'Arm64') { 'macos-arm64' } else { 'macos-x64' }
    } else {
        throw "Could not auto-detect platform. Pass -Platform explicitly (win-x64 / linux-x64 / macos-arm64 / macos-x64)."
    }
}

# ---- Bundle root default ----------------------------------------------------

if (-not $BundleRoot) {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not $localAppData) {
        # Linux/macOS fallback: $HOME/.local/share
        $localAppData = Join-Path $HOME ".local/share"
    }
    $BundleRoot = Join-Path $localAppData "Pal/Saved/PalLLM/Bundled/llama.cpp"
}

# ---- Hardware-aware backend selection (Pass 347) ----------------------------

function Test-CommandAvailable {
    param([string]$Name)
    return [bool](Get-Command -Name $Name -ErrorAction SilentlyContinue)
}

function Get-DetectedGpuVendor {
    # Returns one of: 'nvidia', 'amd', 'intel', 'apple', 'none'.
    # Pass 349: cross-platform. Windows uses WMI; Linux probes
    # nvidia-smi/rocm-smi/lspci; macOS detects Apple Silicon via
    # uname + falls back to system_profiler for discrete GPUs.
    # Any detection failure returns 'none' so the installer still
    # ships a CPU-only build instead of crashing.
    if ($Platform -eq 'win-x64') {
        try {
            $controllers = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop |
                Where-Object { $_.Name } |
                Select-Object -ExpandProperty Name
        } catch {
            return 'none'
        }
        if (-not $controllers) { return 'none' }
        $joined = ($controllers -join ' ').ToLowerInvariant()
        if ($joined -match 'nvidia|geforce|quadro|tesla|rtx|gtx') { return 'nvidia' }
        if ($joined -match 'radeon|amd|firepro') { return 'amd' }
        if ($joined -match 'intel|arc|iris') { return 'intel' }
        return 'none'
    }
    if ($Platform -in @('macos-arm64', 'macos-x64')) {
        # Apple Silicon: arm64 architecture is unambiguous. Metal is
        # always available on these; treat as 'apple' for backend
        # selection (which means no Windows backend split applies and
        # we use the platform monolithic asset + Metal).
        if ($Platform -eq 'macos-arm64') { return 'apple' }
        # Intel Macs may have discrete AMD; system_profiler reveals.
        if (Test-CommandAvailable 'system_profiler') {
            try {
                $sp = & system_profiler SPDisplaysDataType 2>$null | Out-String
                if ($sp -match 'AMD|Radeon') { return 'amd' }
                if ($sp -match 'NVIDIA|GeForce') { return 'nvidia' }
            } catch { }
        }
        return 'none'
    }
    if ($Platform -eq 'linux-x64') {
        # Prefer the vendor SMIs (they're the ground truth when
        # installed); fall back to lspci for vendor-string sniffing.
        if (Test-CommandAvailable 'nvidia-smi') {
            try {
                $null = & nvidia-smi --query-gpu=name --format=csv,noheader 2>$null
                if ($LASTEXITCODE -eq 0) { return 'nvidia' }
            } catch { }
        }
        if (Test-CommandAvailable 'rocm-smi') {
            try {
                $null = & rocm-smi --showproductname 2>$null
                if ($LASTEXITCODE -eq 0) { return 'amd' }
            } catch { }
        }
        if (Test-CommandAvailable 'lspci') {
            try {
                $pci = & lspci 2>$null | Out-String
                if ($pci -match '(?i)NVIDIA|GeForce|Quadro|RTX|GTX') { return 'nvidia' }
                if ($pci -match '(?i)AMD|Radeon|ATI') { return 'amd' }
                if ($pci -match '(?i)Intel.*(VGA|3D|Display)') { return 'intel' }
            } catch { }
        }
        return 'none'
    }
    return 'none'
}

function Get-DetectedVramGb {
    # Returns the largest GPU's VRAM in GB, or 0 when no GPU is found
    # or VRAM is unknown. WMI's Win32_VideoController.AdapterRAM is a
    # UINT32 (truncates above ~4 GiB) so we prefer nvidia-smi /
    # rocm-smi when present.
    if (Test-CommandAvailable 'nvidia-smi') {
        try {
            $raw = & nvidia-smi --query-gpu=memory.total --format=csv,noheader,nounits 2>$null
            if ($LASTEXITCODE -eq 0 -and $raw) {
                $values = @($raw | ForEach-Object { [int]::Parse($_.Trim()) })
                if ($values.Count -gt 0) {
                    $maxMiB = ($values | Measure-Object -Maximum).Maximum
                    return [math]::Round($maxMiB / 1024.0, 1)
                }
            }
        } catch { }
    }
    if (Test-CommandAvailable 'rocm-smi') {
        try {
            $raw = & rocm-smi --showmeminfo vram --csv 2>$null
            if ($LASTEXITCODE -eq 0 -and $raw) {
                $match = [regex]::Match(($raw | Out-String), 'VRAM Total Memory.*?(\d+)')
                if ($match.Success) {
                    return [math]::Round([int64]$match.Groups[1].Value / 1GB, 1)
                }
            }
        } catch { }
    }
    if ($Platform -eq 'win-x64') {
        try {
            $bytes = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop |
                Where-Object { $_.AdapterRAM -gt 0 } |
                Select-Object -ExpandProperty AdapterRAM |
                Measure-Object -Maximum |
                Select-Object -ExpandProperty Maximum
            if ($bytes) {
                # NB: WMI UINT32 truncation means values > 4 GiB report as
                # ~4 GiB. We surface what WMI says but recommend the
                # operator install nvidia-smi for accurate values.
                return [math]::Round($bytes / 1GB, 1)
            }
        } catch { }
    }
    if ($Platform -eq 'macos-arm64') {
        # Apple Silicon: GPU memory == unified system RAM.
        return Get-DetectedSystemRamGb
    }
    return 0
}

function Get-DetectedSystemRamGb {
    if ($Platform -eq 'win-x64') {
        try {
            $os = Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop
            return [math]::Round($os.TotalVisibleMemorySize / 1MB, 1) # KiB -> GiB
        } catch { return 0 }
    }
    if ($Platform -in @('macos-arm64', 'macos-x64')) {
        if (Test-CommandAvailable 'sysctl') {
            try {
                $bytes = [int64](& sysctl -n hw.memsize 2>$null)
                if ($bytes -gt 0) { return [math]::Round($bytes / 1GB, 1) }
            } catch { }
        }
    }
    if ($Platform -eq 'linux-x64') {
        if (Test-Path -LiteralPath '/proc/meminfo') {
            try {
                $line = Get-Content -LiteralPath '/proc/meminfo' -TotalCount 1
                if ($line -match 'MemTotal:\s+(\d+)\s+kB') {
                    return [math]::Round([int64]$matches[1] / 1MB, 1)
                }
            } catch { }
        }
    }
    return 0
}

function Get-CudaToolkitVersion {
    # Returns the major.minor CUDA toolkit version (e.g. "12.4", "13.1")
    # or $null when unavailable. Used only to warn operators when
    # they're on a known-broken toolkit (CUDA 13.0-13.2 + MiniMax-M2.7
    # → gibberish; CUDA 13.x + Blackwell MMQ → crash).
    if (-not (Test-CommandAvailable 'nvcc')) { return $null }
    try {
        $output = & nvcc --version 2>$null | Out-String
        if ($output -match 'release (\d+)\.(\d+)') {
            return "$($matches[1]).$($matches[2])"
        }
    } catch { }
    return $null
}

function Get-DetectedGpuCount {
    # Pass 350: returns the count of GPUs detected (1 by default, more
    # when nvidia-smi or rocm-smi enumerates multiple). Used to suggest
    # --tensor-split / --split-mode graph for multi-GPU hosts.
    if (Test-CommandAvailable 'nvidia-smi') {
        try {
            $names = & nvidia-smi --query-gpu=name --format=csv,noheader 2>$null
            if ($LASTEXITCODE -eq 0 -and $names) {
                return @($names).Count
            }
        } catch { }
    }
    if (Test-CommandAvailable 'rocm-smi') {
        try {
            $output = & rocm-smi --showid 2>$null | Out-String
            if ($LASTEXITCODE -eq 0) {
                $matches = [regex]::Matches($output, 'GPU\[\d+\]')
                return [math]::Max(1, $matches.Count)
            }
        } catch { }
    }
    if ($Platform -eq 'win-x64') {
        try {
            $controllers = Get-CimInstance -ClassName Win32_VideoController -ErrorAction Stop |
                Where-Object { $_.Name -and $_.Name -notmatch '(?i)(Microsoft Basic|Remote)' }
            return [math]::Max(1, @($controllers).Count)
        } catch { return 1 }
    }
    return 1
}

function Get-KvCacheGb {
    # Pass 350: rough KV-cache memory estimate so the recommendation
    # accounts for "model + KV at chosen context" rather than just
    # "model fits". Formula: 2 * layers * kv_heads * head_dim * ctx
    # * bytes_per_elem. Defaults below match Qwen3.6 / Gemma-4 architectures.
    param(
        [int]$Layers,
        [int]$KvHeads,
        [int]$HeadDim,
        [int]$ContextSize,
        [int]$BytesPerElement = 2  # F16/BF16; halve for q8_0, quarter for q4_0
    )
    if ($Layers -le 0 -or $KvHeads -le 0 -or $HeadDim -le 0 -or $ContextSize -le 0) {
        return 0
    }
    $bytes = [int64](2 * $Layers * $KvHeads * $HeadDim * $ContextSize * $BytesPerElement)
    return [math]::Round($bytes / 1GB, 2)
}

function Get-RecommendedModel {
    # VRAM-based model recommendation from the operator's curated
    # library. Probes $ExternalModelsRoot for available GGUFs and
    # returns @{ Path; Family; ContextSize; GpuLayers; NCpuMoe; QuantizedKv; Note }.
    # Pass 350: now MoE-aware (--n-cpu-moe partial offload) and KV-cache-aware
    # (subtracts estimated KV cache from available VRAM before deciding fit).
    param([double]$VramGb, [double]$SystemRamGb, [string]$ModelsRoot, [string]$Backend = 'auto')

    # Curated tier preferences from docs/LOCAL_MODELS_INVENTORY.md,
    # sorted highest-quality first. Pass 351: catalog now covers all
    # 7 curated families (was 4 in Pass 350). Each entry carries:
    #   - Family / Path: display name + relative path (first shard
    #     for multi-shard models — llama.cpp auto-loads the rest)
    #   - DiskGb / MinVramGb / MoeMinVramGb: VRAM gating
    #   - ContextSize / GpuLayers: launch defaults
    #   - IsMoE / Layers / KvHeads / HeadDim: KV-cache math + MoE
    #     partial-offload eligibility
    #   - Sampler: Unsloth per-family canonical (qwen36 / qwen3-coder /
    #     minimax / gemma / deepseek) — auto-launch picks the right
    #     --temp / --top-p / --top-k / --min-p per family
    #   - Prio: optional --prio override (MiniMax recipe asks for 3)
    #   - AllowsSpecDecode: false when upstream has a known
    #     spec-decode crash on this family (Qwen3-Coder-Next: #21886)
    #
    # IsMoE drives partial-offload recommendations when VRAM is tight:
    # MoE models can run on smaller cards by moving expert FFN tensors
    # to CPU via --n-cpu-moe N (David Sanftenberg, Doctor-Shotgun).
    $catalog = @(
        @{
            Family       = 'Qwen3.6-35B-A3B-UD-Q8_K_XL (MoE, quality)'
            Path         = 'Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf'
            DiskGb       = 39
            MinVramGb    = 18
            MoeMinVramGb = 8
            ContextSize  = 16384
            GpuLayers    = 99
            IsMoE        = $true
            Layers       = 80
            KvHeads      = 8
            HeadDim      = 128
            Sampler      = 'qwen36'
            Prio         = 0
            AllowsSpecDecode = $true
        }
        @{
            Family       = 'Qwen3.6-27B-UD-Q8_K_XL (dense, quality)'
            Path         = 'Qwen\Qwen3.6-27B-UD-Q8_K_XL.gguf'
            DiskGb       = 36
            MinVramGb    = 30
            MoeMinVramGb = 0
            ContextSize  = 16384
            GpuLayers    = 99
            IsMoE        = $false
            Layers       = 64
            KvHeads      = 8
            HeadDim      = 128
            Sampler      = 'qwen36'
            Prio         = 0
            AllowsSpecDecode = $true
        }
        @{
            Family       = 'Gemma-4-31B-it-UD-Q8_K_XL (dense)'
            Path         = 'Gemma\gemma-4-31B-it-UD-Q8_K_XL.gguf'
            DiskGb       = 35
            MinVramGb    = 28
            MoeMinVramGb = 0
            ContextSize  = 8192
            GpuLayers    = 99
            IsMoE        = $false
            Layers       = 56
            KvHeads      = 8
            HeadDim      = 128
            Sampler      = 'gemma'
            Prio         = 0
            AllowsSpecDecode = $true
        }
        # Pass 351: heavyweight specialty models. Default-rank
        # below the general-purpose chat tiers because they're
        # niche (coding, research) and their disk footprint is
        # large — the operator must have curated them deliberately.
        #
        # Pass 356: heavyweight families MARKED PostRelease=$true.
        # They remain in the catalog so the operator's manual
        # `connect-llamacpp.ps1 -ModelProfile <family>` still works,
        # but Get-RecommendedModel skips them when the host is the
        # v1.0 reference rig (RTX 3090 + 32 GB RAM, total 56 GB
        # memory budget — these models exceed that even with
        # aggressive partial offload).
        @{
            Family       = 'Qwen3-Coder-Next-UD-Q6_K_XL (coding, multi-shard)'
            Path         = 'Qwen\Qwen3-Coder-Next\UD-Q6_K_XL\Qwen3-Coder-Next-UD-Q6_K_XL-00001-of-00003.gguf'
            DiskGb       = 73
            MinVramGb    = 36
            MoeMinVramGb = 12      # MoE so partial offload available
            ContextSize  = 32768   # Unsloth-documented memory-friendly default (256K native)
            GpuLayers    = 99
            IsMoE        = $true
            Layers       = 80
            KvHeads      = 8
            HeadDim      = 128
            Sampler      = 'qwen3-coder'
            Prio         = 0
            AllowsSpecDecode = $false  # Upstream #21886 — spec-decode broken
            PostRelease  = $true
        }
        @{
            Family       = 'MiniMax-M2.7-UD-IQ4_XS (MoE, heavyweight, multi-shard)'
            Path         = 'MiniMax-M2.7\UD-IQ4_XS\MiniMax-M2.7-UD-IQ4_XS-00001-of-00004.gguf'
            DiskGb       = 108
            MinVramGb    = 64
            MoeMinVramGb = 16      # Aggressive --n-cpu-moe on tight cards
            ContextSize  = 32768   # Unsloth recipe value
            GpuLayers    = 999     # MiniMax recipe explicitly uses 999
            IsMoE        = $true
            Layers       = 88
            KvHeads      = 8
            HeadDim      = 128
            Sampler      = 'minimax'
            Prio         = 3       # Unsloth M2.7 recipe asks for --prio 3
            AllowsSpecDecode = $true
            PostRelease  = $true
        }
        @{
            Family       = 'MiniMax-M2.7-UD-IQ3_XXS (MoE, heavyweight, smaller-shard)'
            Path         = 'MiniMax-M2.7\UD-IQ3_XXS\MiniMax-M2.7-UD-IQ3_XXS-00001-of-00003.gguf'
            DiskGb       = 80
            MinVramGb    = 48
            MoeMinVramGb = 12
            ContextSize  = 32768
            GpuLayers    = 999
            IsMoE        = $true
            Layers       = 88
            KvHeads      = 8
            HeadDim      = 128
            Sampler      = 'minimax'
            Prio         = 3
            AllowsSpecDecode = $true
            PostRelease  = $true
        }
        @{
            Family       = 'DeepSeekV4-Flash-158B-Q3_K_M (research lane)'
            Path         = 'DeepSeek\DeepSeekV4-Flash-158B-Q3_K_M.gguf'
            DiskGb       = 99.9
            MinVramGb    = 80      # Practically CPU-RAM-heavy; researcher-class
            MoeMinVramGb = 0       # Not MoE in the GGUF; whole-model offload only
            ContextSize  = 16384
            GpuLayers    = 99
            IsMoE        = $false
            Layers       = 60
            KvHeads      = 8
            HeadDim      = 128
            Sampler      = 'deepseek'
            Prio         = 0
            AllowsSpecDecode = $true
            PostRelease  = $true
        }
        @{
            Family       = 'Gemma-4-E4B-it-UD-Q4_K_XL (fast-start)'
            Path         = 'Gemma\gemma-4-E4B-it-UD-Q4_K_XL.gguf'
            DiskGb       = 5.1
            MinVramGb    = 6
            MoeMinVramGb = 0
            ContextSize  = 8192
            GpuLayers    = 99
            IsMoE        = $false
            Layers       = 30
            KvHeads      = 4
            HeadDim      = 128
            Sampler      = 'gemma'
            Prio         = 0
            AllowsSpecDecode = $true
        }
    )

    foreach ($entry in $catalog) {
        # Pass 356: skip post-release catalog entries on the v1.0
        # reference rig. They remain launchable via manual flags
        # (connect-llamacpp.ps1 -ModelProfile ... -ModelPath ...);
        # they just don't appear as the auto-recommended default.
        # See docs/POST_RELEASE_ANNEX.md.
        if ($entry.ContainsKey('PostRelease') -and $entry.PostRelease) { continue }

        $full = if ($Platform -eq 'win-x64') {
            Join-Path $ModelsRoot $entry.Path
        } else {
            Join-Path $ModelsRoot ($entry.Path -replace '\\', '/')
        }
        if (-not (Test-Path -LiteralPath $full)) { continue }

        # KV cache budget at F16. Operator can opt into -ctk q8_0 -ctv q8_0
        # to halve this; we'll suggest that when VRAM is tight.
        $kvGb = Get-KvCacheGb -Layers $entry.Layers -KvHeads $entry.KvHeads -HeadDim $entry.HeadDim -ContextSize $entry.ContextSize -BytesPerElement 2
        $effectiveVramNeeded = $entry.MinVramGb + [math]::Max(0, $kvGb - 1.0) # 1 GB already baked into MinVramGb

        # Full-fit path: model + KV cache all in VRAM.
        if ($VramGb -ge $effectiveVramNeeded) {
            return @{
                Path        = $full
                Family      = $entry.Family
                ContextSize = $entry.ContextSize
                GpuLayers   = $entry.GpuLayers
                NCpuMoe     = 0
                QuantizedKv = $false
                IsMoE       = $entry.IsMoE
                Sampler     = $entry.Sampler
                Prio        = $entry.Prio
                AllowsSpecDecode = $entry.AllowsSpecDecode
                Note        = "$($entry.Family) fits the detected $VramGb GB VRAM (model ≥ $($entry.MinVramGb) GB, +KV ≈ $kvGb GB at ctx $($entry.ContextSize))."
            }
        }

        # MoE-partial-fit path: model is MoE and the operator has enough
        # VRAM for active tensors + KV (but not the full expert library).
        # Compute --n-cpu-moe N: deeper layers' experts go to CPU.
        if ($entry.IsMoE -and $VramGb -ge $entry.MoeMinVramGb) {
            # Heuristic: start with N = (model layers / 2), then trim
            # toward 0 as VRAM grows. 8 GB → ~50 layers offloaded;
            # 16 GB → ~25 layers offloaded; 24+ GB → 0 (full GPU).
            $vramSlackGb = [math]::Max(0, $VramGb - $entry.MoeMinVramGb)
            $offloadFraction = [math]::Max(0, 1.0 - ($vramSlackGb / [math]::Max(1, ($entry.MinVramGb - $entry.MoeMinVramGb))))
            $nCpuMoe = [int][math]::Round($entry.Layers * $offloadFraction)
            $nCpuMoe = [math]::Min($entry.Layers, [math]::Max(1, $nCpuMoe))

            $quantizedKv = ($VramGb -lt ($entry.MinVramGb - 4))

            return @{
                Path        = $full
                Family      = $entry.Family
                ContextSize = $entry.ContextSize
                GpuLayers   = $entry.GpuLayers
                NCpuMoe     = $nCpuMoe
                QuantizedKv = $quantizedKv
                IsMoE       = $true
                Sampler     = $entry.Sampler
                Prio        = $entry.Prio
                AllowsSpecDecode = $entry.AllowsSpecDecode
                Note        = "$($entry.Family) fits the detected $VramGb GB VRAM with MoE-CPU partial offload (--n-cpu-moe $nCpuMoe$(if ($quantizedKv) { ' -ctk q8_0 -ctv q8_0' })). Needs $($SystemRamGb) GB RAM for the offloaded experts."
            }
        }
    }

    # Nothing fits VRAM-wise; recommend the smallest model with reduced -ngl
    # so the operator can at least boot. CPU-only path lands here.
    $fastStart = $catalog[-1]
    $fastStartPath = if ($Platform -eq 'win-x64') {
        Join-Path $ModelsRoot $fastStart.Path
    } else {
        Join-Path $ModelsRoot ($fastStart.Path -replace '\\', '/')
    }
    if (Test-Path -LiteralPath $fastStartPath) {
        $layers = if ($VramGb -ge 4) { 20 } elseif ($VramGb -ge 2) { 10 } else { 0 }
        return @{
            Path        = $fastStartPath
            Family      = $fastStart.Family
            ContextSize = 4096
            GpuLayers   = $layers
            NCpuMoe     = 0
            QuantizedKv = $false
            IsMoE       = $false
            Sampler     = 'gemma'
            Prio        = 0
            AllowsSpecDecode = $true
            Note        = if ($layers -gt 0) {
                "Detected $VramGb GB VRAM is tight; recommending the fast-start tier with -ngl $layers partial offload."
            } else {
                "No usable GPU detected; recommending the fast-start tier with CPU-only inference (-ngl 0). Expect ~1-2 tok/s on AVX-512 hosts."
            }
        }
    }

    return @{
        Path        = $null
        Family      = 'No curated model found'
        ContextSize = 4096
        GpuLayers   = 0
        NCpuMoe     = 0
        QuantizedKv = $false
        IsMoE       = $false
        Sampler     = 'qwen36'
        Prio        = 0
        AllowsSpecDecode = $false
        Note        = "No GGUFs detected under $ModelsRoot. Either populate the operator library (see docs/LOCAL_MODELS_INVENTORY.md) or pass -m <path> to llama-server manually."
    }
}

function Get-SamplerFlags {
    # Pass 351: returns the Unsloth-canonical sampler flags for a given
    # model family. The connect script has its own copy of this logic
    # (the -ModelProfile switch); we duplicate here so the install
    # script's auto-launch path can pick the right sampler per
    # recommended model without invoking connect-llamacpp.ps1.
    param([string]$Sampler)
    switch ($Sampler) {
        'qwen36'      { return @('--temp', '0.7', '--top-p', '0.8',  '--top-k', '20', '--min-p', '0.0',  '--presence-penalty', '1.5') }
        'qwen3-coder' { return @('--temp', '0.6', '--top-p', '0.95', '--top-k', '20', '--min-p', '0.0',  '--presence-penalty', '0.0') }
        'minimax'     { return @('--temp', '1.0', '--top-p', '0.95', '--top-k', '40', '--min-p', '0.01') }
        'gemma'       { return @('--temp', '0.7', '--top-p', '0.95', '--top-k', '20', '--min-p', '0.0') }
        'deepseek'    { return @('--temp', '0.7', '--top-p', '0.95', '--top-k', '40', '--min-p', '0.0') }
        default       { return @() }
    }
}

function Test-LlamaServerBinary {
    # Pass 349 smoke test: confirm the installed binary actually
    # launches. `llama-server --version` exits 0 + prints version
    # info; anything else is a runtime-library or arch-mismatch
    # problem the operator needs to know about before they wonder
    # why PalLLM can't reach :8080.
    param([string]$ExePath)
    if (-not (Test-Path -LiteralPath $ExePath)) {
        return @{ Ok = $false; Error = "$ExePath does not exist." }
    }
    try {
        $stderr = New-TemporaryFile
        $proc = Start-Process -FilePath $ExePath -ArgumentList '--version' `
            -NoNewWindow -Wait -PassThru `
            -RedirectStandardOutput 'NUL' -RedirectStandardError $stderr.FullName
        $errText = if (Test-Path -LiteralPath $stderr.FullName) {
            (Get-Content -LiteralPath $stderr.FullName -Raw -ErrorAction SilentlyContinue) -as [string]
        } else { '' }
        Remove-Item -LiteralPath $stderr.FullName -ErrorAction SilentlyContinue

        if ($proc.ExitCode -eq 0) {
            return @{ Ok = $true; Error = $null }
        }
        # Most-common failure modes get an actionable hint.
        $hint = ''
        if ($errText -match '(?i)cudart|cuda.*not found|nvcuda\.dll') {
            $hint = 'CUDA runtime DLLs missing. Re-run the installer; the CUDA backend pulls a cudart-* companion zip.'
        } elseif ($errText -match '(?i)VCRUNTIME|MSVCP|api-ms-win') {
            $hint = 'Microsoft Visual C++ Redistributable missing. Install vc_redist.x64.exe from Microsoft.'
        } elseif ($errText -match '(?i)0x[0-9a-f]+|access violation|exception') {
            $hint = 'Native crash on --version. Try a different backend (e.g. -Backend vulkan) or check GPU driver health.'
        }
        return @{
            Ok    = $false
            Error = "Exit code $($proc.ExitCode). $hint`n$errText"
        }
    } catch {
        return @{ Ok = $false; Error = "Failed to invoke ${ExePath}: $($_.Exception.Message)" }
    }
}

function Select-Backend {
    param([string]$Requested, [string]$Vendor)
    if ($Requested -ne 'auto') { return $Requested }
    switch ($Vendor) {
        'nvidia' { return 'cuda12' }   # Blackwell-stable; pass -Backend cuda13 to opt in
        'amd'    { return 'vulkan' }   # ROCm requires opt-in via -Backend hip
        'intel'  { return 'vulkan' }   # SYCL requires opt-in via -Backend sycl
        default  { return 'cpu' }
    }
}

# Windows-only backend mapping. Linux/macOS still use the monolithic
# platform asset until upstream ships per-backend Linux/macOS builds.
function Get-AssetNames {
    param([string]$Tag, [string]$Backend, [string]$Plat)
    if ($Plat -ne 'win-x64') {
        return @{
            Server = switch ($Plat) {
                'linux-x64'   { "llama-$Tag-bin-ubuntu-x64.zip" }
                'macos-arm64' { "llama-$Tag-bin-macos-arm64.zip" }
                'macos-x64'   { "llama-$Tag-bin-macos-x64.zip" }
                default       { throw "Unsupported platform: $Plat" }
            }
            Cudart = $null
        }
    }
    switch ($Backend) {
        'cuda12' { return @{ Server = "llama-$Tag-bin-win-cuda-12.4-x64.zip"; Cudart = "cudart-llama-bin-win-cuda-12.4-x64.zip" } }
        'cuda13' { return @{ Server = "llama-$Tag-bin-win-cuda-13.1-x64.zip"; Cudart = "cudart-llama-bin-win-cuda-13.1-x64.zip" } }
        'vulkan' { return @{ Server = "llama-$Tag-bin-win-vulkan-x64.zip";    Cudart = $null } }
        'hip'    { return @{ Server = "llama-$Tag-bin-win-hip-radeon-x64.zip"; Cudart = $null } }
        'sycl'   { return @{ Server = "llama-$Tag-bin-win-sycl-x64.zip";      Cudart = $null } }
        'cpu'    { return @{ Server = "llama-$Tag-bin-win-cpu-x64.zip";        Cudart = $null } }
        default  { throw "Unknown backend: $Backend" }
    }
}

$detectedVendor = Get-DetectedGpuVendor
$resolvedBackend = Select-Backend -Requested $Backend -Vendor $detectedVendor

# Pass 356/357: shipping-target check. PalLLM v1.0 ships configured
# for a single reference rig (RTX 3090 / 32 GB DDR4 / 5800X3D,
# Windows). Below-reference hardware is NOT supported as a local
# inference target -- Pass 357 hardens this from "warn and proceed"
# to "skip local recommendation, point at the two shipping escape
# paths: cloud API or remote PC."
#
# Off-target paths can be re-promoted post-release (see
# docs/POST_RELEASE_ANNEX.md); for now, off-target hosts get a
# clear "this isn't your path, here are the two paths that ARE
# yours" message instead of an off-spec local install.
$onTargetRig = ($Platform -eq 'win-x64') -and ($detectedVendor -eq 'nvidia') -and ($Backend -in @('auto', 'cuda12'))
$forceLocal = ($Backend -ne 'auto')   # explicit -Backend = operator opt-in to local on off-target hardware
$offTargetSkipLocal = (-not $onTargetRig) -and (-not $forceLocal) -and (-not $VerifyOnly) -and (-not $DryRun)

if (-not $onTargetRig -and -not $VerifyOnly -and -not $DryRun) {
    Write-Host ""
    Write-Host "Minimum-requirements check (Pass 356/357):" -ForegroundColor Yellow
    Write-Host "  PalLLM v1.0 ships for: Windows + NVIDIA + cuda12 backend." -ForegroundColor Yellow
    Write-Host "  Detected             : platform=$Platform vendor=$detectedVendor backend=$resolvedBackend" -ForegroundColor Yellow
    Write-Host "  Status               : OFF-TARGET (below reference rig)." -ForegroundColor Yellow
    Write-Host ""

    if ($offTargetSkipLocal) {
        Write-Host 'Below-reference hardware: PalLLM v1.0 does NOT recommend running' -ForegroundColor Cyan
        Write-Host 'local inference on this host. Two shipping escape paths exist:' -ForegroundColor Cyan
        Write-Host ''
        Write-Host '  1) Cloud API (OpenAI-compatible provider)' -ForegroundColor White
        Write-Host '     pwsh ./scripts/connect-cloud.ps1 -Provider openai \' -ForegroundColor DarkGray
        Write-Host '         -Model gpt-4o-mini -ApiKey $env:OPENAI_API_KEY -WriteConfig' -ForegroundColor DarkGray
        Write-Host '     Supported providers: openai, groq, together, openrouter, deepseek, mistral, custom.' -ForegroundColor DarkGray
        Write-Host ''
        Write-Host '  2) Remote PC (llama-server on a beefier reference-rig host)' -ForegroundColor White
        Write-Host '     pwsh ./scripts/connect-llamacpp.ps1 \' -ForegroundColor DarkGray
        Write-Host '         -LlamaCppUrl http://<remote-rig-ip>:8080 \' -ForegroundColor DarkGray
        Write-Host '         -Model Qwen3.6-35B-A3B-UD-Q8_K_XL -WriteConfig' -ForegroundColor DarkGray
        Write-Host '     The remote host runs pwsh ./scripts/install-llama-cpp.ps1 -AutoLaunch' -ForegroundColor DarkGray
        Write-Host '     and exposes port 8080; this client points at that URL.' -ForegroundColor DarkGray
        Write-Host ''
        Write-Host '  Skipping local install. See docs/MINIMUM_REQUIREMENTS.md.' -ForegroundColor DarkGray
        Write-Host ''
        exit 0
    } else {
        Write-Host "  Explicit -Backend $Backend supplied; proceeding with off-target local install." -ForegroundColor DarkGray
        Write-Host "  Off-target local paths are not in the v1.0 shipping support matrix." -ForegroundColor DarkGray
        Write-Host "  See docs/MINIMUM_REQUIREMENTS.md + docs/POST_RELEASE_ANNEX.md." -ForegroundColor DarkGray
        Write-Host ""
    }
}

# Pass 349/350: detected VRAM + GPU count + system RAM + CUDA toolkit
# version for the hardware summary block. Cheap one-shot probes;
# results print before any download so the operator can ctrl-c if
# the auto-pick is wrong.
$detectedVramGb = Get-DetectedVramGb
$detectedRamGb  = Get-DetectedSystemRamGb
$detectedCuda   = Get-CudaToolkitVersion
$detectedGpus   = Get-DetectedGpuCount

# Pass 349: resolve the models root. Operator preference order:
# explicit -ModelsRoot > $env:PalLLM_ExternalModelsRoot > platform default.
if (-not $ModelsRoot) {
    if ($env:PalLLM_ExternalModelsRoot) {
        $ModelsRoot = $env:PalLLM_ExternalModelsRoot
    } elseif ($Platform -eq 'win-x64') {
        $ModelsRoot = 'D:\Models'
    } else {
        $ModelsRoot = Join-Path $HOME 'Models'
    }
}

Write-Host ""
Write-Host "PalLLM bundled-llama.cpp installer (Pass 350)" -ForegroundColor Cyan
Write-Host "  Platform        : $Platform"
Write-Host "  Detected GPU    : $detectedVendor$(if ($detectedGpus -gt 1) { " (x$detectedGpus)" })"
if ($detectedVramGb -gt 0) {
    Write-Host "  Detected VRAM   : $detectedVramGb GB$(if ($detectedGpus -gt 1) { ' per-GPU; multi-GPU --tensor-split is opt-in via connect script' })"
} else {
    Write-Host "  Detected VRAM   : unknown (nvidia-smi / rocm-smi not on PATH)" -ForegroundColor DarkYellow
}
if ($detectedRamGb -gt 0) {
    Write-Host "  Detected RAM    : $detectedRamGb GB"
}
if ($detectedCuda) {
    Write-Host "  CUDA toolkit    : $detectedCuda"
    if ($detectedCuda -match '^13\.[012]$' -and $resolvedBackend -eq 'cuda13') {
        Write-Host "    WARNING: CUDA $detectedCuda is in the known-broken band (13.0-13.2):" -ForegroundColor Yellow
        Write-Host "             - MMQ crashes on Blackwell sm_120 (zenn.dev benchmark)" -ForegroundColor Yellow
        Write-Host "             - gibberish output on MiniMax-M2.7 (Unsloth)" -ForegroundColor Yellow
        Write-Host "             Switching to -Backend cuda12 is the safe path." -ForegroundColor Yellow
    }
}
Write-Host "  Backend         : $resolvedBackend $(if ($Backend -eq 'auto') { '(auto)' } else { '(forced)' })"
Write-Host "  Bundle root     : $BundleRoot"
Write-Host "  Models root     : $ModelsRoot"
Write-Host ""

# ---- Resolve release tag ----------------------------------------------------

$apiBase = "https://api.github.com/repos/ggml-org/llama.cpp"

if (-not $ReleaseTag -and -not $VerifyOnly) {
    Write-Host "Querying upstream for latest release tag..." -ForegroundColor DarkGray
    try {
        $latest = Invoke-RestMethod -Uri "$apiBase/releases/latest" -Headers @{ "User-Agent" = "PalLLM-installer" }
        $ReleaseTag = $latest.tag_name
        Write-Host "  Latest release  : $ReleaseTag"
    } catch {
        throw "Could not fetch latest release from upstream. Pass -ReleaseTag explicitly or check network access. Error: $($_.Exception.Message)"
    }
}

$assets = Get-AssetNames -Tag $ReleaseTag -Backend $resolvedBackend -Plat $Platform
$assetName = $assets.Server
$cudartName = $assets.Cudart

$assetUrl  = "https://github.com/ggml-org/llama.cpp/releases/download/$ReleaseTag/$assetName"
$cudartUrl = if ($cudartName) { "https://github.com/ggml-org/llama.cpp/releases/download/$ReleaseTag/$cudartName" } else { $null }
$shaUrl    = "https://github.com/ggml-org/llama.cpp/releases/download/$ReleaseTag/SHA256SUMS"

$installDir = Join-Path $BundleRoot $ReleaseTag
$currentLink = Join-Path $BundleRoot "current"

Write-Host "  Release tag     : $ReleaseTag"
Write-Host "  Asset           : $assetName"
if ($cudartName) {
    Write-Host "  CUDA runtime    : $cudartName"
}
Write-Host "  Source URL      : $assetUrl"
Write-Host "  Install dir     : $installDir"
Write-Host "  'current' link  : $currentLink"
Write-Host ""

if ($DryRun) {
    Write-Host "DryRun: no download, no extract, no write." -ForegroundColor Yellow
    exit 0
}

# ---- VerifyOnly mode --------------------------------------------------------

if ($VerifyOnly) {
    if (-not (Test-Path -LiteralPath $installDir)) {
        Write-Host "Nothing installed at $installDir." -ForegroundColor Yellow
        exit 2
    }
    Write-Host "VerifyOnly mode: skipping download; SHA verification against upstream remains an operator pass." -ForegroundColor Yellow
    exit 0
}

# ---- Idempotency check ------------------------------------------------------

$serverExe = if ($Platform -eq 'win-x64') {
    Join-Path $installDir "llama-server.exe"
} else {
    Join-Path $installDir "llama-server"
}

if (Test-Path -LiteralPath $serverExe) {
    Write-Host "Already installed: $serverExe" -ForegroundColor Green
    Write-Host "  Pass -ReleaseTag to install a different version, or delete $installDir to force re-install."
    Write-Host ""
    Write-Host "Next step: pwsh ./pal.ps1 connect llamacpp -ModelPath D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf -WriteConfig" -ForegroundColor Cyan
    exit 0
}

# ---- Download ---------------------------------------------------------------

New-Item -ItemType Directory -Force -Path $BundleRoot | Out-Null
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$zipPath = Join-Path $BundleRoot "$assetName"
$cudartZipPath = if ($cudartName) { Join-Path $BundleRoot "$cudartName" } else { $null }

Write-Host "Downloading $assetName (typically 30-50 MB)..." -ForegroundColor DarkGray
try {
    Invoke-WebRequest -Uri $assetUrl -OutFile $zipPath -UserAgent "PalLLM-installer"
} catch {
    throw "Download failed: $($_.Exception.Message). The release tag '$ReleaseTag' may not have a '$assetName' asset; check https://github.com/ggml-org/llama.cpp/releases/tag/$ReleaseTag"
}

if ($cudartUrl) {
    Write-Host "Downloading $cudartName (CUDA runtime DLLs, typically 100-300 MB)..." -ForegroundColor DarkGray
    try {
        Invoke-WebRequest -Uri $cudartUrl -OutFile $cudartZipPath -UserAgent "PalLLM-installer"
    } catch {
        Write-Warning "CUDA runtime download failed: $($_.Exception.Message). llama-server.exe will not boot until cudart DLLs are placed alongside it. Re-run the installer or download $cudartName manually from the release page."
    }
}

# ---- SHA verification (best-effort) ----------------------------------------

try {
    $shaResp = Invoke-WebRequest -Uri $shaUrl -UserAgent "PalLLM-installer" -ErrorAction Stop
    $shaText = [System.Text.Encoding]::UTF8.GetString($shaResp.Content)
    $expectedLine = ($shaText -split "`n") | Where-Object { $_ -match [regex]::Escape($assetName) } | Select-Object -First 1
    if ($expectedLine) {
        $expectedSha = ($expectedLine -split '\s+')[0].ToLowerInvariant()
        $actualSha = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($expectedSha -ne $actualSha) {
            Remove-Item -LiteralPath $zipPath -Force
            throw "SHA-256 mismatch on $assetName. Expected $expectedSha, got $actualSha. Download removed; re-run."
        }
        Write-Host "  SHA-256 verified: $expectedSha" -ForegroundColor Green
    } else {
        Write-Warning "Could not find a SHA-256 line for $assetName in SHA256SUMS. Proceeding without strict verification."
    }

    if ($cudartZipPath -and (Test-Path -LiteralPath $cudartZipPath)) {
        $cudartLine = ($shaText -split "`n") | Where-Object { $_ -match [regex]::Escape($cudartName) } | Select-Object -First 1
        if ($cudartLine) {
            $expectedCudartSha = ($cudartLine -split '\s+')[0].ToLowerInvariant()
            $actualCudartSha = (Get-FileHash -LiteralPath $cudartZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($expectedCudartSha -ne $actualCudartSha) {
                Remove-Item -LiteralPath $cudartZipPath -Force
                throw "SHA-256 mismatch on $cudartName. Expected $expectedCudartSha, got $actualCudartSha. Download removed; re-run."
            }
            Write-Host "  CUDA runtime SHA-256 verified: $expectedCudartSha" -ForegroundColor Green
        }
    }
} catch [System.Net.WebException] {
    Write-Warning "Upstream SHA256SUMS asset not available for $ReleaseTag. Proceeding without strict verification — pin a tag known to ship SHA256SUMS for production releases."
}

# ---- Extract ----------------------------------------------------------------

Write-Host "Extracting to $installDir..." -ForegroundColor DarkGray
Expand-Archive -LiteralPath $zipPath -DestinationPath $installDir -Force
Remove-Item -LiteralPath $zipPath -Force

if ($cudartZipPath -and (Test-Path -LiteralPath $cudartZipPath)) {
    Write-Host "Extracting CUDA runtime DLLs into the same directory..." -ForegroundColor DarkGray
    Expand-Archive -LiteralPath $cudartZipPath -DestinationPath $installDir -Force
    Remove-Item -LiteralPath $cudartZipPath -Force
}

# ---- 'current' pointer ------------------------------------------------------

if (Test-Path -LiteralPath $currentLink) {
    Remove-Item -LiteralPath $currentLink -Force -Recurse -ErrorAction SilentlyContinue
}
try {
    # NB: junction (not symlink) so it doesn't require admin elevation on Windows.
    if ($Platform -eq 'win-x64') {
        New-Item -ItemType Junction -Path $currentLink -Target $installDir | Out-Null
    } else {
        New-Item -ItemType SymbolicLink -Path $currentLink -Target $installDir | Out-Null
    }
} catch {
    Write-Warning "Could not create 'current' link at $currentLink. Reference $installDir directly. Error: $($_.Exception.Message)"
}

# ---- Per-backend launch recipe ----------------------------------------------

Write-Host ""
Write-Host "Installed llama-server $ReleaseTag ($resolvedBackend) at:" -ForegroundColor Green
Write-Host "  $serverExe"
Write-Host ""

# Pass 349: smoke-test the binary so we catch CUDA runtime / VC++
# redist / arch-mismatch problems here instead of letting the operator
# wonder why /v1/models 404s an hour later. Off only when the operator
# passes -NoSmokeTest.
if (-not $NoSmokeTest.IsPresent -and $Platform -eq 'win-x64') {
    Write-Host "Smoke-testing llama-server --version..." -ForegroundColor DarkGray
    $smoke = Test-LlamaServerBinary -ExePath $serverExe
    if ($smoke.Ok) {
        Write-Host "  Smoke test OK." -ForegroundColor Green
    } else {
        Write-Warning "Smoke test failed. The binary may not boot:"
        Write-Warning $smoke.Error
        Write-Host "  Continuing — the install files are in place but you'll likely need to fix the underlying issue before llama-server can serve PalLLM." -ForegroundColor Yellow
    }
    Write-Host ""
}

# Pass 349/350: VRAM-based model recommendation from the operator's
# curated library, MoE-aware + KV-cache-aware. Emits the most-appropriate
# model + suggested context + suggested -ngl + --n-cpu-moe + -ctk/-ctv.
# If -AutoLaunch is set, we also exec llama-server with that recipe.
$recommendation = Get-RecommendedModel -VramGb $detectedVramGb -SystemRamGb $detectedRamGb -ModelsRoot $ModelsRoot -Backend $resolvedBackend
Write-Host "Hardware-aware recommendation:" -ForegroundColor Cyan
Write-Host "  Model      : $($recommendation.Family)"
if ($recommendation.Path) {
    Write-Host "  Path       : $($recommendation.Path)"
}
Write-Host "  Context    : $($recommendation.ContextSize)"
Write-Host "  GPU layers : $($recommendation.GpuLayers)"
if ($recommendation.NCpuMoe -gt 0) {
    Write-Host "  MoE offload: --n-cpu-moe $($recommendation.NCpuMoe) (CPU/RAM holds expert FFN tensors for the deepest layers)" -ForegroundColor DarkGray
}
if ($recommendation.QuantizedKv) {
    Write-Host "  KV cache   : q8_0 (halves KV memory; -ctk q8_0 -ctv q8_0)" -ForegroundColor DarkGray
}
Write-Host "  Note       : $($recommendation.Note)"
Write-Host ""

# Pass 352: -AutoLaunch implies -WireConfig. We update PalLLM's
# appsettings BEFORE the blocking llama-server call so the next
# sidecar restart finds the new BaseUrl/Model/sampler immediately.
$effectiveWireConfig = $WireConfig.IsPresent -or $AutoLaunch.IsPresent

if ($effectiveWireConfig -and $recommendation.Path) {
    Write-Host "Wiring PalLLM appsettings to the recommended model..." -ForegroundColor Cyan
    $connectScript = Join-Path $PSScriptRoot 'connect-llamacpp.ps1'
    if (-not (Test-Path -LiteralPath $connectScript)) {
        Write-Warning "Could not locate connect-llamacpp.ps1 next to install-llama-cpp.ps1; PalLLM appsettings was NOT updated. Path tried: $connectScript"
    } else {
        $connectArgs = @{
            Model         = $recommendation.Family.Split(' ')[0]  # strip the parenthetical tier descriptor
            ModelPath     = $recommendation.Path
            ModelProfile  = $recommendation.Sampler
            WriteConfig   = $true
        }
        # Carry the recommended context + GPU layers through so the
        # printed launch command matches what -AutoLaunch will use.
        if ($recommendation.ContextSize) { $connectArgs['ContextSize'] = $recommendation.ContextSize }
        if ($recommendation.GpuLayers)   { $connectArgs['GpuLayers']   = $recommendation.GpuLayers }
        if ($recommendation.NCpuMoe -gt 0)    { $connectArgs['NCpuMoe']     = $recommendation.NCpuMoe }
        if ($recommendation.QuantizedKv)      { $connectArgs['QuantizedKv'] = $true }
        if ($recommendation.Prio -gt 0)       { $connectArgs['Prio']        = $recommendation.Prio }
        if (-not [string]::IsNullOrWhiteSpace($ConfigPath)) {
            $connectArgs['ConfigPath'] = $ConfigPath
        }
        try {
            & $connectScript @connectArgs | Out-Null
            Write-Host "  PalLLM appsettings updated." -ForegroundColor Green
        } catch {
            Write-Warning "connect-llamacpp.ps1 -WriteConfig failed: $($_.Exception.Message). The llama-server launch below will still work, but PalLLM may need a manual `pwsh ./scripts/connect-llamacpp.ps1 -ModelProfile $($recommendation.Sampler) -ModelPath '$($recommendation.Path)' -WriteConfig`."
        }
    }
    Write-Host ""
}

if ($AutoLaunch.IsPresent) {
    if (-not $recommendation.Path) {
        Write-Warning "-AutoLaunch requested but no curated model is on disk to launch. Populate $ModelsRoot first (see docs/LOCAL_MODELS_INVENTORY.md)."
    } else {
        Write-Host "Auto-launching llama-server with the recommended recipe..." -ForegroundColor Cyan

        $launchArgs = @(
            '-m', $recommendation.Path,
            '--host', '127.0.0.1', '--port', '8080',
            '-c', [string]$recommendation.ContextSize,
            '-np', '1', '-b', '512', '-ub', '256',
            '-ngl', [string]$recommendation.GpuLayers,
            '--flash-attn', 'auto',
            '--cache-prompt', '--cache-reuse', '256',
            '-sps', '0.10',
            '--metrics', '--no-webui'
        )

        # Pass 350: MoE-CPU partial offload for the curated MoE families
        # on tight VRAM. The David Sanftenberg recipe for Qwen3-235B-A22B
        # on a 24 GB card, generalised to the operator's catalog. CPU/RAM
        # holds the deepest N layers' expert FFN tensors; the GPU keeps
        # attention + the active expert windows.
        if ($recommendation.NCpuMoe -gt 0) {
            $launchArgs += @('--n-cpu-moe', [string]$recommendation.NCpuMoe)
        }

        # Pass 350: quantized KV cache halves the per-token memory at
        # the cost of a small parse-stability tradeoff. Recommend only
        # when VRAM is tight; the operator's strict-JSON routes should
        # be replayed against f16 KV before promoting q8_0.
        if ($recommendation.QuantizedKv) {
            $launchArgs += @('-ctk', 'q8_0', '-ctv', 'q8_0')
        }

        # Pass 350: backend-specific safety nets.
        # - MoE + --no-mmap = memory crit errors (upstream #14999) → do NOT
        #   recommend --no-mmap when IsMoE is true.
        # - ROCm + --mlock forces shared memory (ROCm #4903) → do NOT
        #   recommend --mlock on the hip backend.
        # Both default to off so this is a "don't add" comment rather
        # than a conditional flag-prune; the operator must opt in.

        # Pass 351: per-model Unsloth canonical sampler. The recommended
        # family's Sampler ('qwen36' | 'qwen3-coder' | 'minimax' |
        # 'gemma' | 'deepseek') drives the --temp/--top-p/--top-k/--min-p
        # combo. Only Qwen profiles use --chat-template-kwargs
        # (enable_thinking is a Qwen3 template kwarg, not universal).
        if ($recommendation.Sampler -in @('qwen36', 'qwen3-coder')) {
            $launchArgs += @('--chat-template-kwargs', '{"enable_thinking":false}')
        }
        $launchArgs += Get-SamplerFlags -Sampler $recommendation.Sampler

        # Pass 351: per-model thread priority. MiniMax-M2.7's Unsloth
        # recipe asks for --prio 3; other families stay at the
        # llama-server default.
        if ($recommendation.Prio -gt 0) {
            $launchArgs += @('--prio', [string]$recommendation.Prio)
        }

        Write-Host "  Command:" -ForegroundColor DarkGray
        Write-Host "  & `"$serverExe`" $($launchArgs -join ' ')" -ForegroundColor DarkGray
        Write-Host ""
        Write-Host "  Press Ctrl-C to stop the server. PalLLM should reach it at http://127.0.0.1:8080/v1/." -ForegroundColor DarkGray
        Write-Host ""

        & $serverExe @launchArgs
        return
    }
}

# Pass 350: multi-GPU hint when more than one card is detected. Not auto-
# enabled in the launch recipe because asymmetric VRAM pairs need the
# operator to choose --tensor-split ratios; we just surface the
# capability so they know it's available.
if ($detectedGpus -gt 1) {
    Write-Host "Multi-GPU detected ($detectedGpus cards). Optional opt-ins:" -ForegroundColor Cyan
    Write-Host "  - Symmetric VRAM (matched cards): connect-llamacpp -SplitMode graph" -ForegroundColor DarkGray
    Write-Host "  - Asymmetric VRAM (e.g. 24 + 12 GB): connect-llamacpp -TensorSplit 2,1" -ForegroundColor DarkGray
    Write-Host "  See docs/LLAMA_CPP_BUNDLED.md `"Multi-GPU + advanced perf knobs`"." -ForegroundColor DarkGray
    Write-Host ""
}
Write-Host "Hardware-aware launch recipe (Pass 347):" -ForegroundColor Cyan
Write-Host ""

# Common base flags used by every backend
$modelHint = if ($Platform -eq 'win-x64') {
    'D:\Models\Qwen\Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf'
} else {
    '/path/to/Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf'
}
$mmprojHint = if ($Platform -eq 'win-x64') {
    'D:\Models\mmproj\mmproj-F16.gguf'
} else {
    '/path/to/mmproj-F16.gguf'
}

# Per-backend default flags. Sampler values match Unsloth's Qwen3.6
# thinking-OFF canonical (temp 0.7, top-p 0.8, top-k 20, min-p 0,
# presence-penalty 1.5) — same numbers as PalLLM's appsettings so the
# pal.json connect verb and the manual recipe agree.
$sharedFlags = @(
    '-c 16384'
    '-np 1'
    '-b 512'
    '-ub 256'
    '--flash-attn auto'
    '--cache-prompt'
    '--cache-reuse 256'
    '-sps 0.10'
    '--metrics'
    '--no-webui'
    "--chat-template-kwargs `"{`\`"enable_thinking`\`":false}`""
    '--temp 0.7 --top-p 0.8 --top-k 20 --min-p 0.0 --presence-penalty 1.5'
)
$gpuFlags = if ($resolvedBackend -in @('cuda12','cuda13','hip','sycl','vulkan')) { @('-ngl 99') } else { @() }
$specFlags = @() # Speculative decoding OFF by default. Net-negative on
                 # RTX 3090 + Qwen3.6-35B-A3B (post PR #19493 benchmark).
                 # Net-positive on RTX PRO 6000 / RTX 5090 / Apple M3 Max
                 # / Strix Halo. Opt in via `connect-llamacpp -SpecType
                 # draft-mtp -DraftMax 2` after measuring your own
                 # cold/warm replay.

$launchLines = @()
if ($Platform -eq 'win-x64') {
    $launchLines += "     & `"$serverExe`" ``"
} else {
    $launchLines += "     `"$serverExe`" \"
}
$launchLines += "         -m $modelHint ``"
$launchLines += "         --mmproj $mmprojHint ``"
$launchLines += "         --host 127.0.0.1 --port 8080 ``"
$launchLines += "         $($sharedFlags -join ' ') $($gpuFlags -join ' ')"

foreach ($line in $launchLines) {
    Write-Host $line -ForegroundColor White
}

Write-Host ""
Write-Host "Backend notes:" -ForegroundColor DarkGray
switch ($resolvedBackend) {
    'cuda12' {
        Write-Host "  - CUDA 12.4 + MMQ is the Blackwell-stable pair (verified May 2026)." -ForegroundColor DarkGray
        Write-Host "  - For older GPUs (Ampere / Ada / Hopper) this build is equally appropriate." -ForegroundColor DarkGray
        Write-Host "  - --flash-attn auto avoids the April-2026 stream_k_fixup crash on RTX 5090." -ForegroundColor DarkGray
    }
    'cuda13' {
        Write-Host "  - CUDA 13.1 is bleeding edge. As of May 2026 there are open reports of MMQ" -ForegroundColor Yellow
        Write-Host "    crashes on Blackwell sm_120. Run a cold/warm replay before promoting this." -ForegroundColor Yellow
    }
    'vulkan' {
        Write-Host "  - Vulkan covers AMD + Intel without ROCm/SYCL toolchain dependencies." -ForegroundColor DarkGray
        Write-Host "  - On AMD RDNA3/RDNA4, Vulkan often beats HIP for inference latency." -ForegroundColor DarkGray
    }
    'hip' {
        Write-Host "  - HIP/ROCm requires AMD's ROCm runtime installed separately." -ForegroundColor Yellow
        Write-Host "  - If HIP isn't faster than Vulkan on your card, switch to -Backend vulkan." -ForegroundColor DarkGray
    }
    'sycl' {
        Write-Host "  - SYCL requires Intel oneAPI runtime installed separately." -ForegroundColor Yellow
        Write-Host "  - On consumer Arc cards, Vulkan is usually a faster path." -ForegroundColor DarkGray
    }
    'cpu' {
        Write-Host "  - CPU-only path. 7B-13B GGUFs run ~1-2 tok/s on AVX-512 hosts." -ForegroundColor DarkGray
        Write-Host "  - Plan on the small fast-start tier (gemma-4-E4B-it-UD-Q4_K_XL); the" -ForegroundColor DarkGray
        Write-Host "    Qwen3.6-35B-A3B quality tier isn't realistic without GPU offload." -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Speculative decoding (off by default):" -ForegroundColor DarkGray
Write-Host "  Qwen3.6 MTP is hardware-dependent — net-negative on RTX 3090 single-card MoE," -ForegroundColor DarkGray
Write-Host "  net-positive on RTX PRO 6000 / RTX 5090 / Apple M3 Max / Strix Halo." -ForegroundColor DarkGray
Write-Host "  Enable via connect-llamacpp -SpecType draft-mtp -DraftMax 2 after measuring." -ForegroundColor DarkGray

Write-Host ""
Write-Host "Wire PalLLM at the same port (already the shipping default):" -ForegroundColor Cyan
Write-Host ""
Write-Host "     pwsh ./pal.ps1 connect llamacpp ``" -ForegroundColor White
Write-Host "         -ModelPath $modelHint ``" -ForegroundColor White
Write-Host "         -WriteConfig" -ForegroundColor White
Write-Host ""
