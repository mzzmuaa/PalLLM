<#
.SYNOPSIS
    Print and optionally wire a Microsoft Foundry Local lane.

.DESCRIPTION
    Foundry Local can run small local ONNX models on the operator's own PC and
    exposes an OpenAI-compatible REST surface when a model is running. This
    helper keeps that path PalLLM-shaped: single-user, loopback-first, dynamic
    port aware, and proof-gated before it becomes the live player lane.

    The script does not install Foundry Local or start a model. It prints the
    copy-paste commands and, with -WriteConfig, updates appsettings.json so the
    next sidecar restart points at the Foundry Local endpoint reported by
    `foundry service status` or supplied through -FoundryEndpoint.

.PARAMETER Model
    Foundry Local alias or model id to run. Defaults to qwen2.5-0.5b because it
    is a small catalog alias suitable for first proof on typical hardware.

.PARAMETER LoadedModelId
    Optional exact model id to send in PalLLM requests. If omitted, PalLLM uses
    -Model. Use this when `foundry service ps` or `/openai/models` reports a
    hardware-specific id that differs from the alias.

.PARAMETER FoundryEndpoint
    Endpoint root reported by `foundry service status`, for example
    http://localhost:5272. The script normalizes it to BaseUrl
    http://localhost:5272/v1/.

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Inference block. Default off.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER DryRun
    Print the planned config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-foundry.ps1

.EXAMPLE
    pwsh ./scripts/connect-foundry.ps1 -FoundryEndpoint http://localhost:5272 -WriteConfig

.NOTES
    Verb shortcut: pal connect foundry
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$Model = 'qwen2.5-0.5b',

    [string]$LoadedModelId,

    [string]$FoundryEndpoint,

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
        Write-Warning "[connect-foundry] Existing config at $Path is not valid JSON: $_"
        Write-Warning "[connect-foundry] Refusing to overwrite. Fix the JSON first or pass -ConfigPath to a different file."
        exit 1
    }
}

function Normalize-FoundryBaseUrl {
    param([string]$Endpoint)
    if ([string]::IsNullOrWhiteSpace($Endpoint)) {
        return $null
    }

    $trimmed = $Endpoint.Trim().TrimEnd('/')
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

function Resolve-FoundryEndpoint {
    param([string]$ExplicitEndpoint)
    if (-not [string]::IsNullOrWhiteSpace($ExplicitEndpoint)) {
        return Normalize-FoundryBaseUrl $ExplicitEndpoint
    }

    if (-not (Get-Command foundry -ErrorAction SilentlyContinue)) {
        return $null
    }

    try {
        $status = (& foundry service status 2>&1 | Out-String)
        if ($status -match '(https?://(?:localhost|127\.0\.0\.1):\d+)') {
            return Normalize-FoundryBaseUrl $Matches[1]
        }
    } catch {
        return $null
    }

    return $null
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

$modelForConfig = if ([string]::IsNullOrWhiteSpace($LoadedModelId)) { $Model } else { $LoadedModelId }
$baseUrl = Resolve-FoundryEndpoint -ExplicitEndpoint $FoundryEndpoint
$serviceRoot = if ($baseUrl) { Get-ServiceRootFromBaseUrl $baseUrl } else { $null }

$setupCommands = @(
    'winget install Microsoft.FoundryLocal',
    'foundry model list --filter task=chat-completion',
    "foundry model run $Model",
    'foundry service status'
)

Write-Host ""
Write-Host "PalLLM <- Foundry Local" -ForegroundColor Cyan
Write-Host ""
Write-Host ("Model alias/id : {0}" -f $Model) -ForegroundColor Green
if (-not [string]::IsNullOrWhiteSpace($LoadedModelId)) {
    Write-Host ("Loaded model  : {0}" -f $LoadedModelId)
}
if ($baseUrl) {
    Write-Host ("BaseUrl       : {0}" -f $baseUrl)
} else {
    Write-Host "BaseUrl       : (not resolved yet - run foundry model run, then foundry service status)" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "Copy-paste setup:" -ForegroundColor White
Write-Host ""
foreach ($command in $setupCommands) {
    Write-Host $command
}
Write-Host ""
Write-Host "After it boots, verify:" -ForegroundColor White
if ($serviceRoot) {
    Write-Host ("  curl {0}/openai/status" -f $serviceRoot)
    Write-Host ("  curl {0}/openai/models" -f $serviceRoot)
    Write-Host ("  curl {0}/v1/chat/completions -H ""Content-Type: application/json"" -d '{{""model"":""{1}"",""messages"":[{{""role"":""user"",""content"":""hi""}}],""max_tokens"":32}}'" -f $serviceRoot, $modelForConfig)
} else {
    Write-Host "  foundry service status        # copy the endpoint it reports"
    Write-Host "  curl http://localhost:<PORT>/openai/status"
    Write-Host "  curl http://localhost:<PORT>/openai/models"
}
Write-Host ""
Write-Host "Promotion guardrails:" -ForegroundColor White
Write-Host "  - Foundry Local uses a dynamic local port; do not hardcode it."
Write-Host "  - First use may download models and execution providers; capture offline proof after warmup."
Write-Host "  - Record execution provider, warm p50/p95 latency, parse success, and fallback behavior."
Write-Host "  - Use vLLM or SGLang instead for shared multi-user serving."
Write-Host ""

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -FoundryEndpoint <url> -WriteConfig to wire PalLLM's appsettings.json.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun = $DryRun.IsPresent
        WroteConfig = $false
        Model = $Model
        LoadedModelId = $LoadedModelId
        BaseUrl = $baseUrl
        NeedsEndpoint = [string]::IsNullOrWhiteSpace($baseUrl)
        SetupCommands = $setupCommands
    } | Write-Output
    return
}

if ([string]::IsNullOrWhiteSpace($baseUrl)) {
    Write-Host "Config target : $ConfigPath" -ForegroundColor White
    Write-Host "Cannot write config until Foundry Local has reported its dynamic endpoint." -ForegroundColor Yellow
    Write-Host "Run: foundry model run $Model" -ForegroundColor Yellow
    Write-Host "Then: foundry service status" -ForegroundColor Yellow
    if ($DryRun.IsPresent) {
        [pscustomobject]@{
            DryRun = $true
            WroteConfig = $false
            NeedsEndpoint = $true
            Model = $Model
            LoadedModelId = $LoadedModelId
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

$inference['BaseUrl'] = $baseUrl
$inference['Model'] = $modelForConfig
$inference['Enabled'] = $true

$delta = @()
if ($priorBaseUrl -ne $baseUrl) { $delta += "  Inference.BaseUrl : $priorBaseUrl -> $baseUrl" }
if ($priorModel -ne $modelForConfig) { $delta += "  Inference.Model   : $priorModel -> $modelForConfig" }
if ($priorEnabled -ne $true) { $delta += "  Inference.Enabled : $priorEnabled -> True" }

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

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM inference -> Foundry Local ($modelForConfig)")) {
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
Write-Host "  1. Keep Foundry Local running with the model command above."
Write-Host "  2. Verify /openai/models lists $modelForConfig."
Write-Host "  3. Restart PalLLM: pal play"
Write-Host "  4. Probe chat: pal hello"
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    WroteConfig = $true
    Model = $Model
    LoadedModelId = $LoadedModelId
    BaseUrl = $baseUrl
    Backup = "$ConfigPath.bak"
} | Write-Output
