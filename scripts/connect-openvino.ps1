<#
.SYNOPSIS
    Print and optionally wire an OpenVINO Model Server lane.

.DESCRIPTION
    OpenVINO Model Server can expose OpenAI-compatible generative endpoints
    under /v3, including /v3/models and /v3/chat/completions, while targeting
    Intel CPU, GPU, or NPU hardware. This helper keeps that path PalLLM-shaped:
    loopback-first, explicit target-device proof, and measured before it is
    promoted to the live player lane.

    The script does not install OpenVINO Model Server, download a model, or
    start a model server. It prints copy-paste commands and, with -WriteConfig,
    updates appsettings.json so the next sidecar restart points at the selected
    /v3 endpoint.

.PARAMETER Model
    OpenVINO-format Hugging Face model id to serve. Defaults to
    OpenVINO/Qwen3-8B-int4-ov, a small current text-generation proof model.

.PARAMETER ModelName
    Model id PalLLM should send in requests. Defaults to -Model.

.PARAMETER OpenVinoPort
    REST port used by OpenVINO Model Server. Default 8000.

.PARAMETER TargetDevice
    OpenVINO target device, such as GPU, CPU, NPU, AUTO, MULTI, or HETERO.
    Default GPU.

.PARAMETER ToolParser
    Optional OpenVINO Model Server tool parser, for example hermes3 for
    Qwen-style tool-call proof lanes.

.PARAMETER WireVision
    Also point PalLLM:Vision at this server. Keep off unless the selected model
    and server build were qualified as a VLM for Palworld screenshots.

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Inference block. Default off.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER DryRun
    Print the planned config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-openvino.ps1

.EXAMPLE
    pwsh ./scripts/connect-openvino.ps1 -TargetDevice NPU -ToolParser hermes3 -WriteConfig -DryRun

.NOTES
    Verb shortcut: pal connect openvino
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Model = 'OpenVINO/Qwen3-8B-int4-ov',

    [string]$ModelName,

    [int]$OpenVinoPort = 8000,

    [string]$TargetDevice = 'GPU',

    [string]$ToolParser,

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
        Write-Warning "[connect-openvino] Existing config at $Path is not valid JSON: $_"
        Write-Warning "[connect-openvino] Refusing to overwrite. Fix the JSON first or pass -ConfigPath to a different file."
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
$baseUrl = "http://localhost:$OpenVinoPort/v3/"
$target = if ([string]::IsNullOrWhiteSpace($TargetDevice)) { 'GPU' } else { $TargetDevice.Trim().ToUpperInvariant() }
$dockerImage = if ($target -eq 'CPU') { 'openvino/model_server:2026.1' } else { 'openvino/model_server:2026.1-gpu' }

$ovmsParts = New-Object System.Collections.ArrayList
@(
    'ovms.exe',
    '--source_model', $Model,
    '--model_repository_path', 'models',
    '--rest_port', "$OpenVinoPort",
    '--task', 'text_generation',
    '--target_device', $target,
    '--model_name', $resolvedModelName
) | ForEach-Object { [void]$ovmsParts.Add($_) }
Add-OptionalArgument -Parts $ovmsParts -Name '--tool_parser' -Value $ToolParser
$baremetalCommand = $ovmsParts -join ' '

$dockerParts = New-Object System.Collections.ArrayList
@(
    'docker', 'run', '--rm', '-p', "$OpenVinoPort`:$OpenVinoPort",
    '-v', '$(pwd)/models:/models:rw',
    $dockerImage,
    '--pull',
    '--source_model', $Model,
    '--model_repository_path', '/models',
    '--rest_port', "$OpenVinoPort",
    '--task', 'text_generation',
    '--target_device', $target,
    '--model_name', $resolvedModelName
) | ForEach-Object { [void]$dockerParts.Add($_) }
Add-OptionalArgument -Parts $dockerParts -Name '--tool_parser' -Value $ToolParser
$dockerCommand = $dockerParts -join ' '

$setupCommands = @(
    'Install OpenVINO Model Server or use the Docker image.',
    $baremetalCommand,
    $dockerCommand
)

Write-Host ""
Write-Host "PalLLM <- OpenVINO Model Server" -ForegroundColor Cyan
Write-Host ""
Write-Host ("Model source  : {0}" -f $Model) -ForegroundColor Green
Write-Host ("Request model : {0}" -f $resolvedModelName)
Write-Host ("Target device : {0}" -f $target)
if (-not [string]::IsNullOrWhiteSpace($ToolParser)) {
    Write-Host ("Tool parser   : {0}" -f $ToolParser)
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
if ($target -ne 'CPU') {
    Write-Host "  Add the platform-specific GPU/NPU device flags your OpenVINO install requires." -ForegroundColor DarkGray
}
Write-Host ""
Write-Host "After it boots, verify:" -ForegroundColor White
Write-Host ("  curl http://localhost:{0}/v3/models" -f $OpenVinoPort)
Write-Host ("  curl http://localhost:{0}/v3/chat/completions -H ""Content-Type: application/json"" -d '{{""model"":""{1}"",""messages"":[{{""role"":""user"",""content"":""hi""}}],""max_tokens"":32}}'" -f $OpenVinoPort, $resolvedModelName)
Write-Host ""
Write-Host "Promotion guardrails:" -ForegroundColor White
Write-Host "  - Use PalLLM BaseUrl http://localhost:<port>/v3/ for OpenVINO Model Server."
Write-Host "  - Record target_device, first-use pull/compile time, warm p50/p95, parse success, and fallback behavior."
Write-Host "  - Keep NPU and VLM lanes proof-only until PalLLM replay traffic stays inside HOT_PATH.md budgets."
Write-Host "  - Keep remote media domains and non-loopback serving disabled unless an authenticated proxy owns them."
Write-Host ""

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -WriteConfig to wire PalLLM's appsettings.json.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun = $DryRun.IsPresent
        WroteConfig = $false
        Model = $Model
        ModelName = $resolvedModelName
        TargetDevice = $target
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

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM inference -> OpenVINO Model Server ($resolvedModelName)")) {
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
Write-Host "  1. Start OpenVINO Model Server with one of the commands above."
Write-Host "  2. Wait for /v3/models to list $resolvedModelName."
Write-Host "  3. Restart PalLLM: pal play"
Write-Host "  4. Probe chat: pal hello"
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    WroteConfig = $true
    Model = $Model
    ModelName = $resolvedModelName
    TargetDevice = $target
    BaseUrl = $baseUrl
    WireVision = $WireVision.IsPresent
    Backup = "$ConfigPath.bak"
} | Write-Output
