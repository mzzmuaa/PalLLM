<#
.SYNOPSIS
    Print and optionally wire a TensorRT-LLM lane.

.DESCRIPTION
    TensorRT-LLM can expose an OpenAI-compatible local server through
    trtllm-serve with /v1/models, /v1/chat/completions, /health, and
    /metrics. This helper keeps that path PalLLM-shaped: local-first,
    proof-gated, metrics-backed, and optional.

    The script does not install TensorRT-LLM, download a model, build an
    engine, or start a model server. It prints copy-paste commands and, with
    -WriteConfig, updates appsettings.json so the next sidecar restart points
    at the selected /v1 endpoint.

.PARAMETER Model
    Hugging Face model id, local model directory, or TensorRT engine path to
    serve. Defaults to Qwen/Qwen3-8B as a small text proof lane.

.PARAMETER ModelName
    Model id PalLLM should send in requests. Defaults to -Model.

.PARAMETER TensorRtPort
    HTTP port used by trtllm-serve. Default 8000.

.PARAMETER Backend
    TensorRT-LLM backend passed to trtllm-serve. Default pytorch.

.PARAMETER TpSize
    Tensor-parallel size. Default 1.

.PARAMETER PpSize
    Pipeline-parallel size. Default 1.

.PARAMETER EpSize
    Expert-parallel size. Default 1.

.PARAMETER MaxBatchSize
    Proof-lane batch cap passed to trtllm-serve. Default 8.

.PARAMETER MaxNumTokens
    Proof-lane token cap passed to trtllm-serve. Default 4096.

.PARAMETER ToolCallParser
    Optional TensorRT-LLM tool-call parser, such as auto, qwen3, or
    qwen3_coder. Leave empty until the exact model/tokenizer passes PalLLM
    strict JSON and tool-call replay.

.PARAMETER DisableChunkedPrefill
    Omit --enable_chunked_prefill from the printed command.

.PARAMETER ServingConfigPath
    Optional TensorRT-LLM YAML config path to include via --config.

.PARAMETER ContainerImage
    TensorRT-LLM container image to print. Defaults to a pinned release tag.

.PARAMETER WireVision
    Also point PalLLM:Vision at this server. Keep off unless the selected
    model and server config were qualified as a Palworld screenshot VLM lane.

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Inference block. Default off.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER DryRun
    Print the planned config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-tensorrt.ps1

.EXAMPLE
    pwsh ./scripts/connect-tensorrt.ps1 -ToolCallParser qwen3 -WriteConfig -DryRun

.EXAMPLE
    pwsh ./scripts/connect-tensorrt.ps1 -Model Qwen/Qwen2.5-VL-7B-Instruct -WireVision -ServingConfigPath trtllm-vlm.yaml

.NOTES
    Verb shortcut: pal connect tensorrt
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Model = 'Qwen/Qwen3-8B',

    [string]$ModelName,

    [int]$TensorRtPort = 8000,

    [ValidateSet('pytorch', 'trt')]
    [string]$Backend = 'pytorch',

    [int]$TpSize = 1,

    [int]$PpSize = 1,

    [int]$EpSize = 1,

    [int]$MaxBatchSize = 8,

    [int]$MaxNumTokens = 4096,

    [string]$ToolCallParser,

    [switch]$DisableChunkedPrefill,

    [string]$ServingConfigPath,

    [string]$ContainerImage = 'nvcr.io/nvidia/tensorrt-llm/release:1.2.0rc6',

    [switch]$WireVision,

    [switch]$WriteConfig,

    [string]$ConfigPath,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Get-DefaultConfigPath {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repoRoot 'sidecar/publish/appsettings.json')
        (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM/appsettings.json')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    return $candidates[2]
}

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

function Ensure-ObjectKey {
    param(
        [System.Collections.IDictionary]$Container,
        [string]$Key
    )
    if (-not (Test-ConfigKey $Container $Key) -or -not ($Container[$Key] -is [System.Collections.IDictionary])) {
        $Container[$Key] = [ordered]@{}
    }
    return $Container[$Key]
}

function Read-Config {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
    }

    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
        }
        return ConvertTo-MutableConfig ($raw | ConvertFrom-Json -ErrorAction Stop)
    } catch {
        Write-Warning "[connect-tensorrt] Existing config at $Path is not valid JSON: $_"
        Write-Warning "[connect-tensorrt] Refusing to overwrite. Fix the JSON first or pass -ConfigPath to a different file."
        exit 1
    }
}

function Add-OptionalArgument {
    param(
        [System.Collections.ArrayList]$Parts,
        [string]$Name,
        [string]$Value
    )
    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        [void]$Parts.Add($Name)
        [void]$Parts.Add($Value.Trim())
    }
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

$resolvedModelName = if ([string]::IsNullOrWhiteSpace($ModelName)) { $Model } else { $ModelName }
$baseUrl = "http://localhost:$TensorRtPort/v1/"

$serveParts = New-Object System.Collections.ArrayList
@(
    'trtllm-serve',
    'serve',
    $Model,
    '--host', 'localhost',
    '--port', "$TensorRtPort",
    '--backend', $Backend,
    '--tp_size', "$TpSize",
    '--pp_size', "$PpSize",
    '--ep_size', "$EpSize",
    '--max_batch_size', "$MaxBatchSize",
    '--max_num_tokens', "$MaxNumTokens",
    '--served_model_name', $resolvedModelName
) | ForEach-Object { [void]$serveParts.Add($_) }
if (-not $DisableChunkedPrefill.IsPresent) {
    [void]$serveParts.Add('--enable_chunked_prefill')
}
Add-OptionalArgument -Parts $serveParts -Name '--tool_call_parser' -Value $ToolCallParser
Add-OptionalArgument -Parts $serveParts -Name '--config' -Value $ServingConfigPath
$baremetalCommand = $serveParts -join ' '

$dockerServeParts = New-Object System.Collections.ArrayList
foreach ($part in $serveParts) {
    [void]$dockerServeParts.Add($part)
}
$dockerHostIndex = $dockerServeParts.IndexOf('--host')
if ($dockerHostIndex -ge 0 -and ($dockerHostIndex + 1) -lt $dockerServeParts.Count) {
    $dockerServeParts[$dockerHostIndex + 1] = '0.0.0.0'
}

$dockerParts = New-Object System.Collections.ArrayList
@(
    'docker', 'run', '--gpus', 'all', '--rm',
    '-p', "$TensorRtPort`:$TensorRtPort",
    '-v', '${HF_HOME:-$HOME/.cache/huggingface}:/root/.cache/huggingface',
    $ContainerImage
) | ForEach-Object { [void]$dockerParts.Add($_) }
foreach ($part in $dockerServeParts) {
    [void]$dockerParts.Add($part)
}
$dockerCommand = $dockerParts -join ' '

$kvReuseLine = if ($WireVision.IsPresent) { '  enable_block_reuse: false' } else { '  enable_block_reuse: true' }
$proofConfig = @"
enable_iter_perf_stats: true
max_batch_size: $MaxBatchSize
max_num_tokens: $MaxNumTokens
kv_cache_config:
$kvReuseLine
"@

$setupCommands = @(
    'Install TensorRT-LLM or use the NVIDIA container.',
    $baremetalCommand,
    $dockerCommand
)

Write-Host ""
Write-Host "PalLLM <- TensorRT-LLM" -ForegroundColor Cyan
Write-Host ""
Write-Host ("Model source  : {0}" -f $Model) -ForegroundColor Green
Write-Host ("Request model : {0}" -f $resolvedModelName)
Write-Host ("Backend       : {0}" -f $Backend)
Write-Host ("Parallelism   : tp={0} pp={1} ep={2}" -f $TpSize, $PpSize, $EpSize)
Write-Host ("Batch/tokens  : max_batch_size={0} max_num_tokens={1}" -f $MaxBatchSize, $MaxNumTokens)
Write-Host ("Chunked prefill: {0}" -f (-not $DisableChunkedPrefill.IsPresent))
if (-not [string]::IsNullOrWhiteSpace($ToolCallParser)) {
    Write-Host ("Tool parser   : {0}" -f $ToolCallParser)
}
Write-Host ("BaseUrl       : {0}" -f $baseUrl)
Write-Host ("Wire vision   : {0}" -f $WireVision.IsPresent)
Write-Host ""
Write-Host "Copy-paste setup:" -ForegroundColor White
Write-Host ""
Write-Host "Bare metal:"
Write-Host $baremetalCommand
Write-Host ""
Write-Host "Docker:"
Write-Host $dockerCommand
Write-Host "  (Docker binds 0.0.0.0 inside the container; keep the published host port on localhost or behind a private proxy.)" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Optional proof YAML for --config:" -ForegroundColor White
Write-Host $proofConfig
Write-Host ""
Write-Host "After it boots, verify:" -ForegroundColor White
Write-Host ("  curl http://localhost:{0}/health" -f $TensorRtPort)
Write-Host ("  curl http://localhost:{0}/v1/models" -f $TensorRtPort)
Write-Host ("  curl http://localhost:{0}/v1/chat/completions -H ""Content-Type: application/json"" -d '{{""model"":""{1}"",""messages"":[{{""role"":""user"",""content"":""hi""}}],""max_tokens"":32}}'" -f $TensorRtPort, $resolvedModelName)
Write-Host ("  curl http://localhost:{0}/metrics" -f $TensorRtPort)
Write-Host ""
Write-Host "Promotion guardrails:" -ForegroundColor White
Write-Host "  - Use PalLLM BaseUrl http://localhost:<port>/v1/ for TensorRT-LLM."
Write-Host "  - Record /health, /v1/models, /metrics, served_model_name, backend, tp/pp/ep, config hash, warm p50/p95, parse success, and fallback behavior."
Write-Host "  - Keep speculation, disaggregated serving, Dynamo, multimodal, and visual-generation lanes proof-only until PalLLM replay traffic stays inside HOT_PATH.md budgets."
Write-Host "  - Keep raw TensorRT-LLM ports loopback-only unless an authenticated reverse proxy owns API keys, body limits, rate limits, TLS, and /metrics privacy."
Write-Host ""

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -WriteConfig to wire PalLLM's appsettings.json.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun = $DryRun.IsPresent
        WroteConfig = $false
        Model = $Model
        ModelName = $resolvedModelName
        Backend = $Backend
        TensorRtPort = $TensorRtPort
        BaseUrl = $baseUrl
        WireVision = $WireVision.IsPresent
        SetupCommands = $setupCommands
    } | Write-Output
    return
}

$config = Read-Config -Path $ConfigPath
if (-not ($config -is [System.Collections.IDictionary])) {
    $config = [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
}

$pal = Ensure-ObjectKey -Container $config -Key 'PalLLM'
$inference = Ensure-ObjectKey -Container $pal -Key 'Inference'

$priorBaseUrl = if (Test-ConfigKey $inference 'BaseUrl') { [string]$inference['BaseUrl'] } else { '' }
$priorModel = if (Test-ConfigKey $inference 'Model') { [string]$inference['Model'] } else { '' }
$priorEnabled = if (Test-ConfigKey $inference 'Enabled') { [bool]$inference['Enabled'] } else { $false }

$inference['BaseUrl'] = $baseUrl
$inference['Model'] = $resolvedModelName
$inference['Enabled'] = $true

$delta = @()
if ($priorBaseUrl -ne $baseUrl) { $delta += "  Inference.BaseUrl : $priorBaseUrl -> $baseUrl" }
if ($priorModel -ne $resolvedModelName) { $delta += "  Inference.Model   : $priorModel -> $resolvedModelName" }
if ($priorEnabled -ne $true) { $delta += "  Inference.Enabled : $priorEnabled -> True" }

if ($WireVision.IsPresent) {
    $vision = Ensure-ObjectKey -Container $pal -Key 'Vision'
    $priorVisionBaseUrl = if (Test-ConfigKey $vision 'BaseUrl') { [string]$vision['BaseUrl'] } else { '' }
    $priorVisionModel = if (Test-ConfigKey $vision 'Model') { [string]$vision['Model'] } else { '' }
    $priorVisionEnabled = if (Test-ConfigKey $vision 'Enabled') { [bool]$vision['Enabled'] } else { $false }

    $vision['BaseUrl'] = $baseUrl
    $vision['Model'] = $resolvedModelName
    $vision['Enabled'] = $true

    if ($priorVisionBaseUrl -ne $baseUrl) { $delta += "  Vision.BaseUrl    : $priorVisionBaseUrl -> $baseUrl" }
    if ($priorVisionModel -ne $resolvedModelName) { $delta += "  Vision.Model      : $priorVisionModel -> $resolvedModelName" }
    if ($priorVisionEnabled -ne $true) { $delta += "  Vision.Enabled    : $priorVisionEnabled -> True" }
}

Write-Host "Config target : $ConfigPath" -ForegroundColor White
if ($delta.Count -eq 0) {
    Write-Host "  (no changes - config already matches the recipe)" -ForegroundColor DarkGray
} else {
    Write-Host "Planned changes:" -ForegroundColor White
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

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM inference -> TensorRT-LLM ($resolvedModelName)")) {
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

$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8

Write-Host "Wrote $ConfigPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Start TensorRT-LLM with one of the commands above."
Write-Host "  2. Wait for /health and /v1/models to report $resolvedModelName."
Write-Host "  3. Restart PalLLM: pal play"
Write-Host "  4. Probe chat: pal hello"
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    WroteConfig = $true
    Model = $Model
    ModelName = $resolvedModelName
    Backend = $Backend
    TensorRtPort = $TensorRtPort
    BaseUrl = $baseUrl
    WireVision = $WireVision.IsPresent
    Backup = "$ConfigPath.bak"
} | Write-Output
