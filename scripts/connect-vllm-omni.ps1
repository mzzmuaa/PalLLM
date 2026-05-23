<#
.SYNOPSIS
    "One-click vLLM-Omni" wizard for multimodal-in / audio-out
    deployments. Picks the right Qwen3-Omni / Gemma 4 / Gemma 3n recipe
    based on detected hardware, prints the docker command, and
    optionally wires PalLLM's vision/media lane at the omni endpoint.

.DESCRIPTION
    Companion to `connect-vllm.ps1` (text-only Blackwell recipes).
    Where that wizard ships a single-model NVFP4 / FP8 / AWQ recipe,
    this wizard ships the **omni** stack: vLLM-Omni serving Qwen3-Omni
    or vLLM / transformers-compatible Gemma 4 / Gemma 3n lanes
    (Gemma accepts image/audio/video in and returns text; Qwen Omni
    can emit audio out). Two recipe families:

      - omni-text-out  : multimodal in (image + audio + video),
                         text out. Smaller hardware footprint.
                         Models: Gemma 4, Gemma 3n.
      - omni-full      : multimodal in, multimodal out (audio
                         streaming via /v1/realtime). Bigger.
                         Model: Qwen3-Omni Instruct / Flash.

    The wizard does NOT boot the container itself - vLLM-Omni is
    heavyweight and operators want to control it directly. It
    prints the command for copy-paste; with -WriteConfig it wires
    `appsettings.json`'s Vision block to point at the omni endpoint so
    the very next sidecar restart can use it for image/media proof.
    The normal text chat lane is left untouched unless -WireInference
    is supplied after route-specific proof.

    Honest note: PalLLM today consumes the standard
    /v1/chat/completions surface. The realtime WS is documented in
    docs/MULTIMODAL_RECIPES.md but not yet hosted by the sidecar.
    This wizard prints both - the chat URL is what the Vision block
    gets; the realtime WS URL is reference material for future work.

.PARAMETER Profile
    Which profile to deploy: omni-text-out (default) or omni-full.

.PARAMETER ModelOverride
    Force a specific HuggingFace model id.

.PARAMETER VllmPort
    Host port for the vLLM-Omni container. Default 8001 (offset
    from connect-vllm's 8000 to avoid collision).

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Vision block to point at the
    omni endpoint. Default off.

.PARAMETER WireInference
    Also update appsettings.json's PalLLM:Inference block to point at
    the same omni endpoint. This is a proof-lane switch: keep it off
    until text-only chat, vision, audio, fallback, and strict JSON
    routes have all been replayed against the exact runtime/model.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER SidecarUrl
    URL where the sidecar's /api/hardware is reachable. Default
    http://localhost:5088. If unreachable, falls back to safe
    Generous-tier defaults.

.PARAMETER DryRun
    Print the planned command + config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-vllm-omni.ps1
    # Detect hardware, print Gemma 4 31B omni-text-out recipe.

.EXAMPLE
    pwsh ./scripts/connect-vllm-omni.ps1 -Profile omni-full -WriteConfig
    # Print a Qwen3-Omni full omni recipe AND wire the Vision block.

.EXAMPLE
    pwsh ./scripts/connect-vllm-omni.ps1 -Profile omni-full -WriteConfig -WireInference
    # Proof-lane only: wire both Inference and Vision to the omni endpoint.

.NOTES
    Verb shortcut:  pal connect omni

    Recipes derived from docs/MULTIMODAL_RECIPES.md. If that doc
    changes, this wizard should track. Drift_Doc_freshness keeps
    the doc fresh; the recipes here are mirrored mechanically.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('omni-text-out', 'omni-full')]
    [string]$Profile = 'omni-text-out',

    [string]$ModelOverride,

    [int]$VllmPort = 8001,

    [switch]$WriteConfig,

    [switch]$WireInference,

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
# JSON helpers (shared with connect-vllm.ps1)
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

function Get-Hardware {
    param([string]$BaseUrl)
    try {
        return Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/hardware" -Method Get -TimeoutSec 3 -ErrorAction Stop
    } catch {
        return $null
    }
}

# -----------------------------------------------------------------------------
# Recipe matrix - omni profiles per hardware tier
# -----------------------------------------------------------------------------

# omni-text-out: multimodal IN (image+audio+video), text OUT.
# omni-full: multimodal IN + audio OUT (realtime WS).
$recipeMatrix = @{
    'omni-text-out' = @{
        'Generous' = @{
            Model = 'google/gemma-4-31B-it'
            Quant = 'fp4'
            ContextLen = 8192
            ExtraFlags = @('--limit-mm-per-prompt', 'image=4,audio=1', '--enable-prefix-caching')
            Notes = '5090-class. Gemma 4 31B - vision + audio in, text out.'
        }
        'Standard' = @{
            Model = 'google/gemma-4-E4B-it'
            Quant = 'fp4'
            ContextLen = 8192
            ExtraFlags = @('--limit-mm-per-prompt', 'image=2,audio=1', '--enable-prefix-caching')
            Notes = '4070-class. Gemma 4 E4B - smaller multimodal model.'
        }
        'Constrained' = @{
            Model = 'google/gemma-3n-E2B-it'
            Quant = 'auto'
            ContextLen = 4096
            ExtraFlags = @('--limit-mm-per-prompt', 'image=1,audio=1')
            Notes = 'Edge. Gemma 3n E2B - efficient audio/image/video input, text out.'
        }
    }
    'omni-full' = @{
        'Generous' = @{
            Model = 'Qwen/Qwen3-Omni-30B-A3B-Instruct'
            Quant = 'auto'
            ContextLen = 8192
            ExtraFlags = @('--omni', '--enable-prefix-caching', '--limit-mm-per-prompt', 'image=2,audio=1')
            Notes = '5090+ recommended. Multimodal in + streaming audio out via /v1/realtime.'
        }
        'Standard' = @{
            Model = 'Qwen/Qwen3-Omni-Flash-Instruct'
            Quant = 'auto'
            ContextLen = 4096
            ExtraFlags = @('--omni', '--enable-prefix-caching')
            Notes = '4070-class. Smaller omni model with audio out.'
        }
        'Constrained' = @{
            Model = 'Qwen/Qwen3-Omni-Flash-Instruct'
            Quant = 'auto'
            ContextLen = 4096
            ExtraFlags = @('--omni')
            Notes = 'Edge proof only. Keep text chat on fallback/cascaded ASR unless this lane proves stable.'
        }
    }
}

function Resolve-Recipe {
    param($Hardware, [string]$Profile, [string]$ModelOverride)
    $matrix = $recipeMatrix[$Profile]
    $tier = if ($Hardware -and $Hardware.detectedTier) { $Hardware.detectedTier } else { 'Generous' }
    if (-not $matrix.ContainsKey($tier)) { $tier = 'Generous' }
    $recipe = $matrix[$tier].Clone()
    if (-not [string]::IsNullOrWhiteSpace($ModelOverride)) {
        $recipe.Model = $ModelOverride
        $recipe.Notes = "(operator-specified via -ModelOverride)"
    }
    $recipe.Tier = $tier
    return $recipe
}

# -----------------------------------------------------------------------------
# Render
# -----------------------------------------------------------------------------

Write-Host ""
Write-Host "PalLLM <- vLLM-Omni (multimodal one-click)" -ForegroundColor Cyan
Write-Host ""

$hw = Get-Hardware -BaseUrl $SidecarUrl
if ($hw) {
    Write-Host "Detected hardware (via $SidecarUrl/api/hardware):" -ForegroundColor White
    Write-Host ("  GPU detected         : {0}" -f $hw.gpuLikelyPresent)
    Write-Host ("  Architecture         : {0}" -f $hw.gpuArchitecture)
    Write-Host ("  Detected tier        : {0}" -f $hw.detectedTier)
    Write-Host ("  RAM (GB)             : {0}" -f $hw.physicalRamGigabytes)
} else {
    Write-Host "Sidecar not running on $SidecarUrl. Recommendation will assume Generous tier." -ForegroundColor DarkGray
}

$recipe = Resolve-Recipe -Hardware $hw -Profile $Profile -ModelOverride $ModelOverride
Write-Host ""
Write-Host ("Profile       : {0}" -f $Profile) -ForegroundColor White
Write-Host ("Tier          : {0}" -f $recipe.Tier)
Write-Host ("Model         : {0}" -f $recipe.Model) -ForegroundColor Green
Write-Host ("Quantization  : {0}" -f $recipe.Quant)
Write-Host ("Context length: {0}" -f $recipe.ContextLen)
Write-Host ("Notes         : {0}" -f $recipe.Notes)
Write-Host ""

# Build docker command.
$container = if ($Profile -eq 'omni-full') { 'vllm/vllm-omni:latest' } else { 'vllm/vllm-openai:latest' }
$cmdLines = @(
    "docker run --gpus all --rm -p $($VllmPort):8000 \"
    "  -e VLLM_MEDIA_URL_ALLOW_REDIRECTS=0 \"
    "  -v `${HF_HOME:-~/.cache/huggingface}:/root/.cache/huggingface \"
    "  $container \"
    "  --model $($recipe.Model) \"
    "  --max-model-len $($recipe.ContextLen) \"
)
if ($recipe.Quant -ne 'auto') {
    $cmdLines += "  --quantization $($recipe.Quant) \"
}
foreach ($flag in $recipe.ExtraFlags) {
    $cmdLines += "  $flag \"
}
$cmdLines[-1] = $cmdLines[-1].TrimEnd(' ', '\')
$dockerCmd = $cmdLines -join "`n"

Write-Host "Copy-paste vLLM-Omni startup:" -ForegroundColor White
Write-Host ""
Write-Host $dockerCmd
Write-Host ""
Write-Host "After it boots, verify:" -ForegroundColor White
Write-Host "  curl http://localhost:$VllmPort/v1/models | jq '.data[].id'"
Write-Host ""
Write-Host "Media fetch posture:" -ForegroundColor White
Write-Host "  PalLLM's built-in vision path sends data URLs, so no remote media fetch is needed." -ForegroundColor DarkGray
Write-Host "  If you expose remote image/video URLs to vLLM, add --allowed-media-domains for only those hosts." -ForegroundColor DarkGray
Write-Host "  Gemma 4 audio-in proof should budget 25 audio tokens/sec; Gemma 3n uses 6.25 tokens/sec." -ForegroundColor DarkGray
if ($Profile -eq 'omni-full') {
    Write-Host ""
    Write-Host "Realtime / omni endpoints (audio in / audio out):" -ForegroundColor White
    Write-Host "  ws://localhost:$VllmPort/v1/realtime?model=$($recipe.Model)" -ForegroundColor DarkGray
    Write-Host "  http://localhost:$VllmPort/v1/chat/completions with modalities=[text,audio]" -ForegroundColor DarkGray
    Write-Host "  (PalLLM today does not host a realtime proxy - this URL is for direct clients)" -ForegroundColor DarkGray
}
Write-Host ""

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -WriteConfig to wire PalLLM's Vision block; add -WireInference only after same-endpoint text+media proof.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun        = $DryRun.IsPresent
        WroteConfig   = $false
        WireInference = $WireInference.IsPresent
        Profile       = $Profile
        Tier          = $recipe.Tier
        Model         = $recipe.Model
        VllmPort      = $VllmPort
        DockerCommand = $dockerCmd
    } | Write-Output
    return
}

# Write config.
$baseUrl = "http://localhost:$VllmPort/v1/"
$config = if (Test-Path -LiteralPath $ConfigPath) {
    try {
        $raw = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) {
            [ordered]@{ PalLLM = [ordered]@{ Vision = [ordered]@{} } }
        } else {
            ConvertTo-MutableConfig ($raw | ConvertFrom-Json -ErrorAction Stop)
        }
    } catch {
        Write-Warning "[connect-vllm-omni] Existing config at $ConfigPath is not valid JSON: $_"
        exit 1
    }
} else {
    [ordered]@{ PalLLM = [ordered]@{ Vision = [ordered]@{} } }
}

if (-not (Test-ConfigKey $config 'PalLLM')) { $config['PalLLM'] = [ordered]@{} }
$pal = $config['PalLLM']

# Vision -> omni endpoint (multimodal in)
if (-not (Test-ConfigKey $pal 'Vision')) { $pal['Vision'] = [ordered]@{} }
$pal['Vision']['BaseUrl'] = $baseUrl
$pal['Vision']['Model']   = $recipe.Model
$pal['Vision']['Enabled'] = $true

# Optional proof lane: Inference -> same omni endpoint.
if ($WireInference.IsPresent) {
    if (-not (Test-ConfigKey $pal 'Inference')) { $pal['Inference'] = [ordered]@{} }
    $pal['Inference']['BaseUrl'] = $baseUrl
    $pal['Inference']['Model']   = $recipe.Model
    $pal['Inference']['Enabled'] = $true
}

if ($DryRun.IsPresent) {
    Write-Host "[DryRun] No file changes. Resulting JSON would be:" -ForegroundColor Yellow
    Write-Host ($config | ConvertTo-Json -Depth 12)
    return
}

$targetDescription = if ($WireInference.IsPresent) { "inference + vision" } else { "vision" }
if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM $targetDescription -> vLLM-Omni ($($recipe.Model))")) {
    return
}

$parent = Split-Path -Parent $ConfigPath
if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
if ((Test-Path -LiteralPath $ConfigPath) -and -not (Test-Path -LiteralPath "$ConfigPath.bak")) {
    Copy-Item -LiteralPath $ConfigPath -Destination "$ConfigPath.bak" -Force
    Write-Host "Backed up existing config to $ConfigPath.bak" -ForegroundColor DarkGray
}

$resultJson = $config | ConvertTo-Json -Depth 12
Set-Content -LiteralPath $ConfigPath -Value $resultJson -Encoding UTF8

Write-Host "Wrote $ConfigPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Boot vLLM-Omni with the docker command above."
Write-Host "  2. Wait for the model to load (first run pulls weights -- 5-15 min)."
Write-Host "  3. Restart the sidecar so it picks up the new config: pal play"
Write-Host "  4. Test single-image vision: pal hello (deterministic) then via the dashboard."
if (-not $WireInference.IsPresent) {
    Write-Host "  5. Keep the existing text chat endpoint until same-server text+media replay proof passes."
} else {
    Write-Host "  5. Run text-only chat, image, audio, strict JSON, and fallback replay before promoting this as the default endpoint."
}
Write-Host ""
Write-Host "More recipes + the full multimodal contract:" -ForegroundColor DarkGray
Write-Host "  docs/MULTIMODAL_RECIPES.md" -ForegroundColor DarkGray
Write-Host ""

[pscustomobject]@{
    DryRun        = $false
    WroteConfig   = $true
    WireInference = $WireInference.IsPresent
    Profile       = $Profile
    Tier          = $recipe.Tier
    Model         = $recipe.Model
    VllmPort      = $VllmPort
    DockerCommand = $dockerCmd
    Backup        = "$ConfigPath.bak"
} | Write-Output
