<#
.SYNOPSIS
    Print and optionally wire a Hugging Face transformers serve lane.

.DESCRIPTION
    Hugging Face transformers serve exposes an OpenAI-compatible local server
    with /v1/chat/completions, /v1/models, /load_model, continuous batching,
    tool-call support on supported model families, and audio transcription
    endpoints. This helper keeps that path PalLLM-shaped: local by default,
    pinned by repo revision for promotion evidence, and proof-gated before it
    becomes a live player lane.

    The script does not install Python packages or start the server. It prints
    copy-paste commands and, with -WriteConfig, updates appsettings.json so the
    next sidecar restart points at the selected endpoint.

.PARAMETER Model
    Hugging Face repo id to serve. Defaults to Qwen/Qwen3.6-35B-A3B.

.PARAMETER Revision
    Optional exact Hugging Face revision SHA or tag. Promotion evidence should
    use a commit SHA, not an implicit moving branch.

.PARAMETER TransformersPort
    Host port for transformers serve. Default 8002.

.PARAMETER WireVision
    Also point PalLLM:Vision at this server. Keep off unless the selected model
    and server build were qualified for Palworld screenshots.

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Inference block. Default off.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER DryRun
    Print the planned config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-transformers.ps1

.EXAMPLE
    pwsh ./scripts/connect-transformers.ps1 -Model Qwen/Qwen3.6-35B-A3B -Revision <sha> -WriteConfig

.NOTES
    Verb shortcut: pal connect transformers
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Model = 'Qwen/Qwen3.6-35B-A3B',

    [string]$Revision,

    [int]$TransformersPort = 8002,

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
        Write-Warning "[connect-transformers] Existing config at $Path is not valid JSON: $_"
        Write-Warning "[connect-transformers] Refusing to overwrite. Fix the JSON first or pass -ConfigPath to a different file."
        exit 1
    }
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

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

$modelRef = if ([string]::IsNullOrWhiteSpace($Revision)) { $Model } else { "$Model@$Revision" }
$baseUrl = "http://localhost:$TransformersPort/v1/"

$installCommands = @(
    'python -m pip install --upgrade "transformers[serving]"',
    "transformers serve $modelRef --host localhost --port $TransformersPort --continuous-batching --dtype bfloat16"
)

Write-Host ""
Write-Host "PalLLM <- transformers serve" -ForegroundColor Cyan
Write-Host ""
Write-Host ("Model         : {0}" -f $Model) -ForegroundColor Green
if (-not [string]::IsNullOrWhiteSpace($Revision)) {
    Write-Host ("Revision      : {0}" -f $Revision)
} else {
    Write-Host "Revision      : (not pinned - use a commit SHA for promotion evidence)" -ForegroundColor Yellow
}
Write-Host ("BaseUrl       : {0}" -f $baseUrl)
Write-Host ("Wire vision   : {0}" -f $WireVision.IsPresent)
Write-Host ""
Write-Host "Copy-paste setup:" -ForegroundColor White
Write-Host ""
foreach ($command in $installCommands) {
    Write-Host $command
}
Write-Host ""
Write-Host "After it boots, verify:" -ForegroundColor White
Write-Host ("  curl http://localhost:{0}/v1/models" -f $TransformersPort)
Write-Host ("  curl -X POST http://localhost:{0}/load_model -H ""Content-Type: application/json"" -d '{{""model"":""{1}""}}'" -f $TransformersPort, $modelRef)
Write-Host ""
Write-Host "Promotion guardrails:" -ForegroundColor White
Write-Host "  - Replay PalLLM chat turns before changing defaults."
Write-Host "  - Compare continuous batching against a non-batched local baseline."
Write-Host "  - Keep player speech on TTS/ASR proof lanes until privacy and fallback behavior are recorded."
Write-Host ""

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -WriteConfig to wire PalLLM's appsettings.json.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun = $DryRun.IsPresent
        WroteConfig = $false
        Model = $Model
        Revision = $Revision
        BaseUrl = $baseUrl
        WireVision = $WireVision.IsPresent
        SetupCommands = $installCommands
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
$inference['Model'] = $Model
$inference['Enabled'] = $true

$delta = @()
if ($priorBaseUrl -ne $baseUrl) { $delta += "  Inference.BaseUrl : $priorBaseUrl -> $baseUrl" }
if ($priorModel -ne $Model) { $delta += "  Inference.Model   : $priorModel -> $Model" }
if ($priorEnabled -ne $true) { $delta += "  Inference.Enabled : $priorEnabled -> True" }

if ($WireVision.IsPresent) {
    $vision = Ensure-ObjectKey -Container $pal -Key 'Vision'
    $priorVisionBaseUrl = if (Test-ConfigKey $vision 'BaseUrl') { [string]$vision['BaseUrl'] } else { '' }
    $priorVisionModel = if (Test-ConfigKey $vision 'Model') { [string]$vision['Model'] } else { '' }
    $priorVisionEnabled = if (Test-ConfigKey $vision 'Enabled') { [bool]$vision['Enabled'] } else { $false }

    $vision['BaseUrl'] = $baseUrl
    $vision['Model'] = $Model
    $vision['Enabled'] = $true

    if ($priorVisionBaseUrl -ne $baseUrl) { $delta += "  Vision.BaseUrl    : $priorVisionBaseUrl -> $baseUrl" }
    if ($priorVisionModel -ne $Model) { $delta += "  Vision.Model      : $priorVisionModel -> $Model" }
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

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM inference -> transformers serve ($Model)")) {
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
Write-Host "  1. Start transformers serve with the command above."
Write-Host "  2. Wait for /load_model ready and /v1/models to list the model."
Write-Host "  3. Restart PalLLM: pal play"
Write-Host "  4. Probe chat: pal hello"
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    WroteConfig = $true
    Model = $Model
    Revision = $Revision
    BaseUrl = $baseUrl
    WireVision = $WireVision.IsPresent
    Backup = "$ConfigPath.bak"
} | Write-Output
