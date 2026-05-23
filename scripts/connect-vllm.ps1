<#
.SYNOPSIS
    "One-click Blackwell" wizard. Detects the operator's hardware,
    picks the right NVFP4 / MXFP4 / FP8 vLLM recipe from
    docs/BLACKWELL_RECIPES.md, prints the docker command, and
    optionally rewrites appsettings.json to point PalLLM at it.

.DESCRIPTION
    Three things in one:

      1. Probe /api/hardware on the running sidecar (or fall back to
         a local detection pass) to determine GPU architecture +
         FP4 likelihood.
      2. Pick the right recipe:
         - Blackwell (FP4 native)         -> NVFP4 vLLM recipe
         - Hopper / Ada (FP8 native)      -> FP8 vLLM recipe
         - Ampere or older                -> AWQ-INT4 vLLM recipe
                                            or punt to Ollama
         - No GPU                         -> instruct to use Ollama
      3. Print the matching docker command (copy-pastable). With
         -WriteConfig, also update appsettings.json's
         PalLLM:Inference section to point at the vLLM endpoint
         (http://localhost:8000/v1/ by default).

    The script does NOT boot vLLM itself - that's a heavyweight,
    long-running container the operator wants to control directly.
    It prints the command and, with consent, wires the config so
    the very next sidecar restart talks to vLLM.

    Why this script exists: docs/BLACKWELL_RECIPES.md has 8 sections
    of detailed startup snippets. Most operators only need ONE,
    auto-picked from their hardware. This is the one-command path.

.PARAMETER UseCase
    Which BLACKWELL_RECIPES.md section to source the recipe from:
      - companion       (default; small context, low latency, §1)
      - coding          (long context, tool calls, §2)
      - productivity    (general chat, §3)
      - vision          (vision + text, §4)
      - narration       (game world-state, §5)

.PARAMETER ModelOverride
    Force a specific HuggingFace model ID. If omitted, the wizard
    picks based on detected VRAM + use case.

.PARAMETER VllmPort
    Host port for the vLLM container. Default 8000.

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Inference to point at the
    vLLM endpoint (BaseUrl + Model + Enabled=true). Default off -
    the script only prints the command unless you opt in.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER SidecarUrl
    URL where the sidecar's /api/hardware is reachable. Default
    http://localhost:5088. If unreachable, falls back to the
    safe defaults (Standard tier).

.PARAMETER DryRun
    Print the planned command + config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-vllm.ps1
    # Detect hardware, print the right docker command, do not
    # touch the config.

.EXAMPLE
    pwsh ./scripts/connect-vllm.ps1 -UseCase coding -WriteConfig
    # Print the agentic-coding recipe AND wire PalLLM to it.

.EXAMPLE
    pwsh ./scripts/connect-vllm.ps1 -ModelOverride nvidia/Qwen3-8B-FP4
    # Force a specific model.

.NOTES
    Verb shortcut:  pal connect vllm

    Recipes are derived from docs/BLACKWELL_RECIPES.md. If you change
    that doc, the recipes here should track it. The drift gate
    Drift_Doc_freshness keeps the doc fresh (45-day cap).
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('companion', 'coding', 'productivity', 'vision', 'narration')]
    [string]$UseCase = 'companion',

    [string]$ModelOverride,

    [int]$VllmPort = 8000,

    [switch]$WriteConfig,

    [string]$ConfigPath,

    [string]$SidecarUrl = 'http://localhost:5088',

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Resolve config path (matches `pal config` priority order)
# -----------------------------------------------------------------------------

function Get-DefaultConfigPath {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repoRoot 'sidecar/publish/appsettings.json')
        (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM/appsettings.json')
    )
    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $candidates[2]
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

# -----------------------------------------------------------------------------
# Mutable JSON helpers (shared shape with connect-ollama.ps1)
# -----------------------------------------------------------------------------

function ConvertTo-MutableConfig {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in $Value.Keys) { $copy[$key] = ConvertTo-MutableConfig $Value[$key] }
        return $copy
    }
    if ($Value -is [psobject] -and $Value.PSObject.Properties.Count -gt 0 -and -not ($Value -is [string])) {
        $copy = [ordered]@{}
        foreach ($prop in $Value.PSObject.Properties) {
            $copy[$prop.Name] = ConvertTo-MutableConfig $prop.Value
        }
        return $copy
    }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $list = New-Object System.Collections.ArrayList
        foreach ($item in $Value) { [void]$list.Add((ConvertTo-MutableConfig $item)) }
        return $list.ToArray()
    }
    return $Value
}

function Test-ConfigKey {
    param($Container, [string]$Key)
    if ($null -eq $Container) { return $false }
    if ($Container -is [System.Collections.IDictionary]) {
        return $Container.Contains($Key)
    }
    return $false
}

# -----------------------------------------------------------------------------
# Probe sidecar for hardware tier + GPU architecture
# -----------------------------------------------------------------------------

function Get-Hardware {
    param([string]$BaseUrl)
    try {
        return Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/hardware" -Method Get -TimeoutSec 3 -ErrorAction Stop
    } catch {
        return $null
    }
}

# -----------------------------------------------------------------------------
# Recipe matrix (mirrors docs/BLACKWELL_RECIPES.md sections 1-5)
# -----------------------------------------------------------------------------

# Each recipe: model + quantization + the use-case-specific flags. Sized
# in GB so we can pick by detected VRAM. The 8B class is the safe default
# when we can't detect VRAM; 70B requires a 5090 / B-series; 120B is
# B200 / GB200 territory.

$recipeMatrix = @{
    companion = @{
        '8B'   = @{ Model = 'nvidia/Llama-3.1-8B-Instruct-FP4';      Quant = 'fp4'; ContextLen = 4096; Notes = 'Fits a single 5070 (~10 GB).' }
        '70B'  = @{ Model = 'nvidia/Llama-3.3-70B-Instruct-FP4';     Quant = 'fp4'; ContextLen = 4096; Notes = '5090 territory (~37 GB + KV cache).' }
        'fp8'  = @{ Model = 'meta-llama/Llama-3.1-8B-Instruct';      Quant = 'fp8'; ContextLen = 4096; Notes = 'Hopper / Ada (no FP4 tensor cores).' }
        'awq'  = @{ Model = 'TheBloke/Llama-2-7B-Chat-AWQ';          Quant = 'awq'; ContextLen = 4096; Notes = 'Pre-Hopper Ampere fallback.' }
    }
    coding = @{
        '8B'   = @{ Model = 'nvidia/Qwen3-8B-FP4';                   Quant = 'fp4'; ContextLen = 32768; Notes = 'Long-context coder, fits 5070.' }
        '70B'  = @{ Model = 'nvidia/Qwen3-72B-Instruct-FP4';         Quant = 'fp4'; ContextLen = 32768; Notes = '5090 territory; tool-call heavy.' }
        'fp8'  = @{ Model = 'Qwen/Qwen3-8B-Instruct';                Quant = 'fp8'; ContextLen = 32768; Notes = 'Hopper / Ada (no FP4 tensor cores).' }
        'awq'  = @{ Model = 'TheBloke/CodeLlama-13B-Instruct-AWQ';   Quant = 'awq'; ContextLen = 16384; Notes = 'Pre-Hopper Ampere fallback.' }
    }
    productivity = @{
        '8B'   = @{ Model = 'nvidia/Llama-3.1-8B-Instruct-FP4';      Quant = 'fp4'; ContextLen = 8192; Notes = 'General chat, fits 5070.' }
        '70B'  = @{ Model = 'nvidia/Llama-3.3-70B-Instruct-FP4';     Quant = 'fp4'; ContextLen = 8192; Notes = 'Best-in-class chat on 5090.' }
        'fp8'  = @{ Model = 'meta-llama/Llama-3.1-8B-Instruct';      Quant = 'fp8'; ContextLen = 8192; Notes = 'Hopper / Ada (no FP4 tensor cores).' }
        'awq'  = @{ Model = 'TheBloke/Llama-2-13B-Chat-AWQ';         Quant = 'awq'; ContextLen = 4096; Notes = 'Pre-Hopper Ampere fallback.' }
    }
    vision = @{
        '8B'   = @{ Model = 'nvidia/Llama-3.2-11B-Vision-FP4';       Quant = 'fp4'; ContextLen = 8192; Notes = 'Vision + text, fits 5070+.' }
        '70B'  = @{ Model = 'nvidia/Llama-3.2-90B-Vision-FP4';       Quant = 'fp4'; ContextLen = 8192; Notes = '5090+ for vision-heavy workloads.' }
        'fp8'  = @{ Model = 'meta-llama/Llama-3.2-11B-Vision';       Quant = 'fp8'; ContextLen = 8192; Notes = 'Hopper / Ada (no FP4 tensor cores).' }
        'awq'  = $null
    }
    narration = @{
        '8B'   = @{ Model = 'nvidia/Llama-3.1-8B-Instruct-FP4';      Quant = 'fp4'; ContextLen = 16384; Notes = 'World-state narration, fits 5070.' }
        '70B'  = @{ Model = 'nvidia/Llama-3.3-70B-Instruct-FP4';     Quant = 'fp4'; ContextLen = 16384; Notes = '5090 territory; long-form scenes.' }
        'fp8'  = @{ Model = 'meta-llama/Llama-3.1-8B-Instruct';      Quant = 'fp8'; ContextLen = 16384; Notes = 'Hopper / Ada (no FP4 tensor cores).' }
        'awq'  = @{ Model = 'TheBloke/Llama-2-13B-Chat-AWQ';         Quant = 'awq'; ContextLen = 8192; Notes = 'Pre-Hopper Ampere fallback.' }
    }
}

# -----------------------------------------------------------------------------
# Pick the right recipe based on detected hardware
# -----------------------------------------------------------------------------

function Resolve-Recipe {
    param(
        $Hardware,
        [string]$UseCase,
        [string]$ModelOverride
    )
    $matrix = $recipeMatrix[$UseCase]

    # If operator forced a model, build a recipe around it.
    if (-not [string]::IsNullOrWhiteSpace($ModelOverride)) {
        $quant = if ($ModelOverride -match 'FP4') { 'fp4' }
                 elseif ($ModelOverride -match 'FP8') { 'fp8' }
                 elseif ($ModelOverride -match 'AWQ') { 'awq' }
                 else { 'fp16' }
        return @{
            Model = $ModelOverride
            Quant = $quant
            ContextLen = 4096
            Notes = '(operator-specified via -ModelOverride)'
            Reason = 'override'
        }
    }

    # No hardware info: default to 8B FP4 -- safe enough for 5070+, and
    # the operator can override.
    if (-not $Hardware) {
        $r = $matrix['8B'].Clone()
        $r.Reason = 'default-no-hardware-detection'
        return $r
    }

    $arch = $Hardware.gpuArchitecture
    $fp4Likely = [bool]$Hardware.fp4TensorCoresLikely
    $tier = $Hardware.detectedTier

    if ($fp4Likely -and $arch -match 'blackwell') {
        # Blackwell: pick 70B if Generous / Blackwell tier, else 8B.
        if ($tier -in @('Generous', 'Blackwell')) {
            $r = $matrix['70B'].Clone()
            $r.Reason = "blackwell-fp4-{0}-tier" -f $tier.ToLowerInvariant()
        } else {
            $r = $matrix['8B'].Clone()
            $r.Reason = 'blackwell-fp4-standard-tier'
        }
        return $r
    }

    if ($arch -match 'hopper|ada' -or ($Hardware.gpuLikelyPresent -and -not $fp4Likely)) {
        # Hopper / Ada or undetected modern GPU: FP8 path.
        $r = $matrix['fp8']
        if ($null -eq $r) {
            $r = $matrix['8B'].Clone()
            $r.Reason = 'no-fp8-recipe-for-usecase'
            return $r
        }
        $r = $r.Clone()
        $r.Reason = 'hopper-or-ada-fp8'
        return $r
    }

    # Older / unknown / no-GPU: AWQ fallback if we have one, else 8B.
    $r = $matrix['awq']
    if ($null -eq $r) {
        $r = $matrix['8B'].Clone()
        $r.Reason = 'no-awq-recipe-fallback-to-fp4'
        return $r
    }
    $r = $r.Clone()
    $r.Reason = 'pre-hopper-awq-fallback'
    return $r
}

# -----------------------------------------------------------------------------
# Build the docker command
# -----------------------------------------------------------------------------

function Format-DockerCommand {
    param(
        [hashtable]$Recipe,
        [int]$Port
    )
    # Escape the multiline correctly with PowerShell-friendly continuation.
    $maxNumSeqs = if ($Recipe.ContextLen -ge 16384) { 8 } else { 16 }
    $gpuMemUtil = '0.85'
    $quant = ([string]$Recipe.Quant).Trim().ToLowerInvariant()
    $kvCacheDtype = if ($Recipe.ContainsKey('KvCacheDtype')) {
        ([string]$Recipe.KvCacheDtype).Trim().ToLowerInvariant()
    } elseif ($quant -in @('fp4', 'fp8')) {
        'fp8'
    } else {
        'auto'
    }
    $performanceMode = if ($Recipe.ContainsKey('PerformanceMode')) {
        ([string]$Recipe.PerformanceMode).Trim().ToLowerInvariant()
    } else {
        ''
    }
    $extraFlags = @(
        $(if ($kvCacheDtype -and $kvCacheDtype -ne 'auto') { "--kv-cache-dtype $kvCacheDtype" })
        $(if ($performanceMode -and $performanceMode -ne 'balanced') { "--performance-mode $performanceMode" })
        "--max-model-len $($Recipe.ContextLen)"
        "--max-num-seqs $maxNumSeqs"
        '--enable-prefix-caching'
        '--prefix-caching-hash-algo sha256_cbor'
        '--enable-chunked-prefill'
        '--structured-outputs-config.backend xgrammar'
        "--gpu-memory-utilization $gpuMemUtil"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $cmd = @(
        "docker run --gpus all --rm -p $($Port):8000 \"
        "  -v `${HF_HOME:-~/.cache/huggingface}:/root/.cache/huggingface \"
        "  vllm/vllm-openai:latest \"
        "  --model $($Recipe.Model) \"
        "  --quantization $($Recipe.Quant) \"
    )
    foreach ($flag in $extraFlags) {
        $cmd += "  $flag \"
    }
    # Trim trailing backslash on the last line
    $cmd[-1] = $cmd[-1].TrimEnd(' ', '\')
    return ($cmd -join "`n")
}

# -----------------------------------------------------------------------------
# Render
# -----------------------------------------------------------------------------

Write-Host ""
Write-Host "PalLLM <- vLLM (one-click Blackwell)" -ForegroundColor Cyan
Write-Host ""

$hw = Get-Hardware -BaseUrl $SidecarUrl
if ($hw) {
    Write-Host "Detected hardware (via $SidecarUrl/api/hardware):" -ForegroundColor White
    Write-Host ("  GPU detected         : {0}" -f $hw.gpuLikelyPresent)
    Write-Host ("  Architecture         : {0}" -f $hw.gpuArchitecture)
    Write-Host ("  FP4 tensor cores     : {0}" -f $hw.fp4TensorCoresLikely)
    Write-Host ("  Detected tier        : {0}" -f $hw.detectedTier)
    Write-Host ("  RAM (GB)             : {0}" -f $hw.physicalRamGigabytes)
} else {
    Write-Host "Sidecar not running on $SidecarUrl. Recommendation will assume 8B FP4 (safe Blackwell default)." -ForegroundColor DarkGray
    Write-Host "(Boot the sidecar with 'pal play' for hardware-aware recommendations.)" -ForegroundColor DarkGray
}

$recipe = Resolve-Recipe -Hardware $hw -UseCase $UseCase -ModelOverride $ModelOverride
$recipe['PerformanceMode'] = if ($UseCase -in @('companion', 'vision', 'narration')) {
    'interactivity'
} else {
    'balanced'
}
Write-Host ""
Write-Host ("Use case      : {0}" -f $UseCase) -ForegroundColor White
Write-Host ("Resolved model: {0}" -f $recipe.Model) -ForegroundColor Green
Write-Host ("Quantization  : {0}" -f $recipe.Quant)
$resolvedKvCacheDtype = if ($recipe.ContainsKey('KvCacheDtype')) {
    [string]$recipe.KvCacheDtype
} elseif (([string]$recipe.Quant).ToLowerInvariant() -in @('fp4', 'fp8')) {
    'fp8'
} else {
    'auto'
}
Write-Host ("KV cache dtype : {0}" -f $resolvedKvCacheDtype)
Write-Host ("Performance   : {0}" -f $recipe.PerformanceMode)
Write-Host ("Context length: {0}" -f $recipe.ContextLen)
Write-Host ("Notes         : {0}" -f $recipe.Notes)
Write-Host ("Reason        : {0}" -f $recipe.Reason) -ForegroundColor DarkGray
Write-Host ""

$dockerCmd = Format-DockerCommand -Recipe $recipe -Port $VllmPort
Write-Host "Copy-paste vLLM startup (one-shot):" -ForegroundColor White
Write-Host ""
Write-Host $dockerCmd
Write-Host ""
Write-Host "After it boots, verify the engine is up:" -ForegroundColor White
Write-Host "  curl http://localhost:$VllmPort/v1/models | jq '.data[].id'"
Write-Host ""

# -----------------------------------------------------------------------------
# Optionally rewrite appsettings to point at vLLM
# -----------------------------------------------------------------------------

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -WriteConfig to wire PalLLM's appsettings.json at the same time.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun = $DryRun.IsPresent
        WroteConfig = $false
        UseCase = $UseCase
        Model = $recipe.Model
        Quantization = $recipe.Quant
        KvCacheDtype = $resolvedKvCacheDtype
        VllmPort = $VllmPort
        Reason = $recipe.Reason
        DockerCommand = $dockerCmd
    } | Write-Output
    return
}

# Build config delta.
$baseUrl = "http://localhost:$VllmPort/v1/"

if (-not (Test-Path -LiteralPath $ConfigPath)) {
    $config = [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
} else {
    try {
        $raw = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
        $config = if ([string]::IsNullOrWhiteSpace($raw)) {
            [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
        } else {
            ConvertTo-MutableConfig ($raw | ConvertFrom-Json -ErrorAction Stop)
        }
    } catch {
        Write-Warning "[connect-vllm] Existing config at $ConfigPath is not valid JSON: $_"
        Write-Warning "[connect-vllm] Refusing to overwrite. Fix the JSON first."
        exit 1
    }
}

if (-not (Test-ConfigKey $config 'PalLLM')) {
    $config['PalLLM'] = [ordered]@{}
}
$pal = $config['PalLLM']
if (-not (Test-ConfigKey $pal 'Inference')) {
    $pal['Inference'] = [ordered]@{}
}
$inference = $pal['Inference']

$priorBaseUrl = if (Test-ConfigKey $inference 'BaseUrl') { [string]$inference['BaseUrl'] } else { '' }
$priorModel   = if (Test-ConfigKey $inference 'Model')   { [string]$inference['Model'] } else { '' }
$priorEnabled = if (Test-ConfigKey $inference 'Enabled') { [bool]$inference['Enabled'] } else { $false }

$inference['BaseUrl'] = $baseUrl
$inference['Model']   = $recipe.Model
$inference['Enabled'] = $true

Write-Host "Config target : $ConfigPath" -ForegroundColor White
$delta = @()
if ($priorBaseUrl -ne $baseUrl)        { $delta += "  BaseUrl : $priorBaseUrl -> $baseUrl" }
if ($priorModel   -ne $recipe.Model)   { $delta += "  Model   : $priorModel -> $($recipe.Model)" }
if ($priorEnabled -ne $true)           { $delta += "  Enabled : $priorEnabled -> True" }
if ($delta.Count -eq 0) {
    Write-Host "  (no changes -- config already matches the recipe)" -ForegroundColor DarkGray
} else {
    Write-Host "Planned changes (PalLLM.Inference):" -ForegroundColor White
    $delta | ForEach-Object { Write-Host $_ }
}
Write-Host ""

if ($DryRun.IsPresent) {
    Write-Host "[DryRun] No file changes." -ForegroundColor Yellow
    Write-Host ($config | ConvertTo-Json -Depth 12)
    return
}

if ($delta.Count -eq 0) {
    Write-Host "Config unchanged. No write needed." -ForegroundColor Green
    return
}

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM inference -> vLLM ($($recipe.Model))")) {
    return
}

$parent = Split-Path -Parent $ConfigPath
if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
if ((Test-Path -LiteralPath $ConfigPath) -and -not (Test-Path -LiteralPath "$ConfigPath.bak")) {
    Copy-Item -LiteralPath $ConfigPath -Destination "$ConfigPath.bak" -Force
    Write-Host "Backed up existing config to $ConfigPath.bak" -ForegroundColor DarkGray
}

$resultJson = $config | ConvertTo-Json -Depth 12
Set-Content -LiteralPath $ConfigPath -Value $resultJson -Encoding UTF8

Write-Host "Wrote $ConfigPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Boot vLLM with the docker command above."
Write-Host "  2. Wait for the model to load (first run pulls the weights -- can take 5-15 min)."
Write-Host "  3. Restart the sidecar so it picks up the new config:"
Write-Host "       pal play"
Write-Host "  4. Probe with a real chat:"
Write-Host "       pal hello"
Write-Host ""
Write-Host "More recipes per use case:" -ForegroundColor DarkGray
Write-Host "  docs/BLACKWELL_RECIPES.md - 8 sections including 2027 / 2035 outlooks" -ForegroundColor DarkGray
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    WroteConfig = $true
    UseCase = $UseCase
    Model = $recipe.Model
    Quantization = $recipe.Quant
    KvCacheDtype = $resolvedKvCacheDtype
    VllmPort = $VllmPort
    Reason = $recipe.Reason
    DockerCommand = $dockerCmd
    Backup = "$ConfigPath.bak"
} | Write-Output
