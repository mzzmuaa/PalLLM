<#
.SYNOPSIS
    Print and optionally wire a local LM Studio lane.

.DESCRIPTION
    LM Studio exposes local models through OpenAI-compatible /v1 endpoints on
    the developer server, and its CLI can start the server, list local models,
    load a model with GPU/context settings, and report loaded models. This
    helper keeps that path PalLLM-shaped: loopback-first, TTL-aware, and
    proof-gated before it becomes a live player lane.

    The script does not install LM Studio or download model weights. It prints
    copy-paste setup commands and, with -WriteConfig, updates appsettings.json
    so the next sidecar restart points at the selected local server.

.PARAMETER LmStudioUrl
    LM Studio server root. Default http://localhost:1234. PalLLM consumes
    the OpenAI-compatible BaseUrl http://localhost:1234/v1/.

.PARAMETER Model
    Exact loaded model id to send in PalLLM requests. If omitted, the script
    attempts to read /v1/models and uses the first reported id. Pass this
    explicitly for reproducible promotion evidence.

.PARAMETER ContextLength
    Optional context length to show in the suggested lms load command.

.PARAMETER Gpu
    Optional lms GPU offload setting to show in the suggested load command
    (for example max, auto, or 1.0).

.PARAMETER ResidencyTtlSeconds
    Optional LM Studio TTL hint in seconds. Defaults to PalLLM's 1800-second
    runtime default. Set 0 to write ResidencyProvider=Disabled.

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Inference block. Default off.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER DryRun
    Print the planned config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-lmstudio.ps1

.EXAMPLE
    pwsh ./scripts/connect-lmstudio.ps1 -Model local-qwen -WriteConfig

.NOTES
    Verb shortcut: pal connect lmstudio
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$LmStudioUrl = 'http://localhost:1234',

    [string]$Model,

    [int]$ContextLength = 8192,

    [string]$Gpu = 'auto',

    [int]$ResidencyTtlSeconds = 1800,

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
        Write-Warning "[connect-lmstudio] Existing config at $Path is not valid JSON: $_"
        Write-Warning "[connect-lmstudio] Refusing to overwrite. Fix the JSON first or pass -ConfigPath to a different file."
        exit 1
    }
}

function Normalize-LmStudioBaseUrl {
    param([string]$Url)
    $trimmed = $Url.Trim().TrimEnd('/')
    if ($trimmed.EndsWith('/v1', [System.StringComparison]::OrdinalIgnoreCase)) {
        return "$trimmed/"
    }
    return "$trimmed/v1/"
}

function Get-ServiceRootFromBaseUrl {
    param([string]$BaseUrl)
    $trimmed = $BaseUrl.TrimEnd('/')
    if ($trimmed.EndsWith('/v1', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $trimmed.Substring(0, $trimmed.Length - 3)
    }
    return $trimmed
}

function Get-LmStudioModelIds {
    param([string]$BaseUrl)
    try {
        $models = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/models" -Method Get -TimeoutSec 5 -ErrorAction Stop
    } catch {
        return $null
    }

    if ($null -eq $models -or $null -eq $models.data) {
        return @()
    }

    return @($models.data | ForEach-Object {
        if ($_.id) { [string]$_.id }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

$baseUrl = Normalize-LmStudioBaseUrl $LmStudioUrl
$serviceRoot = Get-ServiceRootFromBaseUrl $baseUrl
$resolvedModel = $Model
$discoveredModels = Get-LmStudioModelIds -BaseUrl $baseUrl

if ([string]::IsNullOrWhiteSpace($resolvedModel) -and $null -ne $discoveredModels -and $discoveredModels.Count -gt 0) {
    $resolvedModel = $discoveredModels[0]
}

$ttl = [math]::Max(0, $ResidencyTtlSeconds)
$ttlArg = if ($ttl -gt 0) { " --ttl $ttl" } else { '' }

$loadCommand = if ([string]::IsNullOrWhiteSpace($resolvedModel)) {
    "lms load <model-id> --gpu $Gpu --context-length $ContextLength --identifier <stable-pal-model-id>$ttlArg"
} else {
    "lms load $resolvedModel --gpu $Gpu --context-length $ContextLength --identifier $resolvedModel$ttlArg"
}

$setupCommands = @(
    ('lms server start --port ' + ([Uri]$serviceRoot).Port),
    'lms ls',
    $loadCommand,
    'lms ps'
)

Write-Host ""
Write-Host "PalLLM <- LM Studio" -ForegroundColor Cyan
Write-Host ""
Write-Host ("Server root   : {0}" -f $serviceRoot) -ForegroundColor Green
Write-Host ("BaseUrl       : {0}" -f $baseUrl)
if ([string]::IsNullOrWhiteSpace($resolvedModel)) {
    Write-Host "Model         : (not resolved - pass -Model or load a model first)" -ForegroundColor Yellow
} else {
    Write-Host ("Model         : {0}" -f $resolvedModel)
}
Write-Host ("Residency TTL: {0}s" -f $ttl)
if ($null -eq $discoveredModels) {
    Write-Host "Probe         : /v1/models unreachable right now" -ForegroundColor Yellow
} elseif ($discoveredModels.Count -eq 0) {
    Write-Host "Probe         : /v1/models reachable but no loaded models were reported" -ForegroundColor Yellow
} else {
    Write-Host ("Probe         : {0} model(s): {1}" -f $discoveredModels.Count, ($discoveredModels -join ', '))
}
Write-Host ""
Write-Host "Copy-paste setup:" -ForegroundColor White
Write-Host ""
foreach ($command in $setupCommands) {
    Write-Host $command
}
Write-Host ""
Write-Host "After it boots, verify:" -ForegroundColor White
Write-Host ("  curl {0}/v1/models" -f $serviceRoot)
if ([string]::IsNullOrWhiteSpace($resolvedModel)) {
    Write-Host ("  curl {0}/v1/chat/completions -H ""Content-Type: application/json"" -d '{{""model"":""<loaded-model-id>"",""messages"":[{{""role"":""user"",""content"":""hi""}}],""max_tokens"":32}}'" -f $serviceRoot)
} else {
    Write-Host ("  curl {0}/v1/chat/completions -H ""Content-Type: application/json"" -d '{{""model"":""{1}"",""messages"":[{{""role"":""user"",""content"":""hi""}}],""max_tokens"":32,""ttl"":{2}}}'" -f $serviceRoot, $resolvedModel, $ttl)
}
Write-Host ""
Write-Host "Promotion guardrails:" -ForegroundColor White
Write-Host "  - Keep the LM Studio server loopback-only; use CORS only when a trusted local tool requires it."
Write-Host "  - Capture /v1/models, structured JSON, tool-call, p50/p95, and fallback proof before promotion."
Write-Host "  - Record lms ps / server logs so context length, GPU offload, TTL, and auto-evict behavior are visible."
Write-Host "  - Review model licenses before redistributing any model files; PalLLM should ship config, not weights."
Write-Host ""

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -Model <id> -WriteConfig to wire PalLLM's appsettings.json.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun = $DryRun.IsPresent
        WroteConfig = $false
        Model = $resolvedModel
        BaseUrl = $baseUrl
        ResidencyTtlSeconds = $ttl
        ProbeReachable = $null -ne $discoveredModels
        DiscoveredModels = $discoveredModels
        SetupCommands = $setupCommands
    } | Write-Output
    return
}

if ([string]::IsNullOrWhiteSpace($resolvedModel)) {
    Write-Host "Config target : $ConfigPath" -ForegroundColor White
    Write-Host "Cannot write config until a loaded LM Studio model id is known." -ForegroundColor Yellow
    Write-Host "Run: lms ps"
    Write-Host "Then re-run with: -Model <loaded-model-id> -WriteConfig"
    if ($DryRun.IsPresent) {
        [pscustomobject]@{
            DryRun = $true
            WroteConfig = $false
            NeedsModel = $true
            BaseUrl = $baseUrl
        } | Write-Output
        return
    }
    exit 2
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
$priorResidencyProvider = if (Test-ConfigKey $inference 'ResidencyProvider') { [string]$inference['ResidencyProvider'] } else { '' }
$priorResidencyTtl = if (Test-ConfigKey $inference 'ResidencyTtlSeconds') { [int]$inference['ResidencyTtlSeconds'] } else { 0 }

$provider = if ($ttl -gt 0) { 'LmStudio' } else { 'Disabled' }

$inference['BaseUrl'] = $baseUrl
$inference['Model'] = $resolvedModel
$inference['Enabled'] = $true
$inference['ResidencyProvider'] = $provider
$inference['ResidencyTtlSeconds'] = $ttl

$delta = @()
if ($priorBaseUrl -ne $baseUrl) { $delta += "  Inference.BaseUrl             : $priorBaseUrl -> $baseUrl" }
if ($priorModel -ne $resolvedModel) { $delta += "  Inference.Model               : $priorModel -> $resolvedModel" }
if ($priorEnabled -ne $true) { $delta += "  Inference.Enabled             : $priorEnabled -> True" }
if ($priorResidencyProvider -ne $provider) { $delta += "  Inference.ResidencyProvider   : $priorResidencyProvider -> $provider" }
if ($priorResidencyTtl -ne $ttl) { $delta += "  Inference.ResidencyTtlSeconds : $priorResidencyTtl -> $ttl" }

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

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM inference -> LM Studio ($resolvedModel)")) {
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
Write-Host "  1. Keep LM Studio's server running with the loaded model above."
Write-Host "  2. Confirm /v1/models lists $resolvedModel."
Write-Host "  3. Restart PalLLM: pal play"
Write-Host "  4. Probe chat: pal hello"
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    WroteConfig = $true
    Model = $resolvedModel
    BaseUrl = $baseUrl
    ResidencyProvider = $provider
    ResidencyTtlSeconds = $ttl
    Backup = "$ConfigPath.bak"
} | Write-Output
