<#
.SYNOPSIS
    Wire PalLLM to an OpenAI-compatible cloud API provider.

.DESCRIPTION
    Pass 357: shipping escape path for operators on hardware below the
    v1.0 reference rig (RTX 3090 / 32 GB DDR4 / 5800X3D). The local
    `install-llama-cpp.ps1 -AutoLaunch` flow targets the reference
    rig; this script targets the cloud-API alternative.

    PalLLM's runtime speaks OpenAI-compatible `/v1/chat/completions`
    already. Cloud setup is therefore three config fields:
    `PalLLM.Inference.BaseUrl`, `Model`, `ApiKey`. This script
    presets the base URL per known provider, validates the API key
    is non-empty, optionally probes `/v1/models` to confirm the
    credential works, and writes the result to `appsettings.json`
    via `-WriteConfig`.

    For the other escape path -- pointing PalLLM at a remote PC
    running llama-server on a beefier machine -- use
    `connect-llamacpp.ps1 -LlamaCppUrl http://<remote-ip>:8080`
    (the existing connector already accepts a non-loopback URL).

.PARAMETER Provider
    Cloud provider preset. One of:
      openai      - api.openai.com/v1/         (GPT-class models)
      groq        - api.groq.com/openai/v1/    (LPU-accelerated open-weight)
      together    - api.together.xyz/v1/       (broad open-weight catalog)
      openrouter  - openrouter.ai/api/v1/      (multi-provider aggregator)
      deepseek    - api.deepseek.com/v1/       (DeepSeek-published)
      mistral     - api.mistral.ai/v1/         (Mistral-published)
      custom      - operator supplies -BaseUrl

.PARAMETER BaseUrl
    Override the provider preset's base URL. Required when -Provider
    is 'custom'; optional otherwise. Must end with /v1/ for
    OpenAI-compatible routing.

.PARAMETER Model
    Model identifier the provider exposes (e.g. 'gpt-4o-mini',
    'llama-3.1-70b-versatile', 'meta-llama/Llama-3.1-70B-Instruct').
    Required.

.PARAMETER ApiKey
    The provider's bearer-token API key. SECURITY: prefer passing
    via environment variable. PalLLM reads
    `PalLLM__Inference__ApiKey` from the environment when
    `appsettings.json` doesn't declare one. When supplied here, the
    key is written to `appsettings.json` -- ensure that file is not
    in version control (it's already in `.gitignore` for the
    operator's runtime root).

.PARAMETER Probe
    When set, the script GETs `/v1/models` with the supplied API
    key before writing config. A non-200 response aborts the wire.
    Optional; useful for catching typo'd keys early.

.PARAMETER WriteConfig
    When set, the resolved {BaseUrl, Model, ApiKey} writes to
    `appsettings.json` under `PalLLM.Inference`. Without it the
    script only prints the planned config.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER DryRun
    Print the planned config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-cloud.ps1 -Provider openai -Model gpt-4o-mini -ApiKey $env:OPENAI_API_KEY -WriteConfig
    # Wire PalLLM to OpenAI's GPT-4o-mini.

.EXAMPLE
    pwsh ./scripts/connect-cloud.ps1 -Provider groq -Model llama-3.1-70b-versatile -ApiKey $env:GROQ_API_KEY -Probe -WriteConfig
    # Wire to Groq with a /v1/models probe to validate the key first.

.EXAMPLE
    pwsh ./scripts/connect-cloud.ps1 -Provider custom -BaseUrl https://my-gateway/v1/ -Model my-model -ApiKey $env:GATEWAY_KEY -WriteConfig
    # Custom OpenAI-compatible gateway.

.NOTES
    Verb shortcut: pal connect cloud (Pass 357).
    Shipping escape path for below-reference-rig operators.
    See docs/MINIMUM_REQUIREMENTS.md § "Below-reference hardware".
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('openai', 'groq', 'together', 'openrouter', 'deepseek', 'mistral', 'custom')]
    [string]$Provider,

    [string]$BaseUrl,

    [Parameter(Mandatory = $true)]
    [string]$Model,

    [Parameter(Mandatory = $true)]
    [string]$ApiKey,

    [switch]$Probe,
    [switch]$WriteConfig,
    [string]$ConfigPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ---- Resolve base URL from provider preset --------------------------------

$providerPresets = @{
    openai     = 'https://api.openai.com/v1/'
    groq       = 'https://api.groq.com/openai/v1/'
    together   = 'https://api.together.xyz/v1/'
    openrouter = 'https://openrouter.ai/api/v1/'
    deepseek   = 'https://api.deepseek.com/v1/'
    mistral    = 'https://api.mistral.ai/v1/'
}

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    if ($Provider -eq 'custom') {
        throw "Provider 'custom' requires -BaseUrl. Example: -BaseUrl https://my-gateway.example.com/v1/"
    }
    $BaseUrl = $providerPresets[$Provider]
}

# ---- Validate inputs ------------------------------------------------------

if (-not $BaseUrl.EndsWith('/')) {
    $BaseUrl = "$BaseUrl/"
}
if ($BaseUrl -notmatch '/v\d+/$') {
    Write-Warning "BaseUrl '$BaseUrl' doesn't end with a versioned path like '/v1/'. PalLLM's runtime appends '/chat/completions' to this; verify your provider's routing."
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw "ApiKey is empty. Pass `-ApiKey` with a non-empty value (or `-ApiKey `$env:PROVIDER_API_KEY`)."
}
if ([string]::IsNullOrWhiteSpace($Model)) {
    throw "Model is empty. Pass `-Model` with the provider's model identifier (e.g. 'gpt-4o-mini', 'llama-3.1-70b-versatile')."
}

Write-Host ""
Write-Host "PalLLM <- cloud API ($Provider)" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Provider : $Provider"
Write-Host "  BaseUrl  : $BaseUrl"
Write-Host "  Model    : $Model"
$keyDisplay = if ($ApiKey.Length -gt 8) { ($ApiKey.Substring(0, 4) + ('*' * ($ApiKey.Length - 8)) + $ApiKey.Substring($ApiKey.Length - 4)) } else { '****' }
Write-Host "  ApiKey   : $keyDisplay  ($($ApiKey.Length) chars)"
Write-Host ""

# ---- Probe /v1/models -----------------------------------------------------

if ($Probe.IsPresent) {
    $modelsUrl = "${BaseUrl}models"
    Write-Host "Probing $modelsUrl ..." -ForegroundColor DarkGray
    try {
        $response = Invoke-WebRequest -Uri $modelsUrl `
            -Headers @{ Authorization = "Bearer $ApiKey" } `
            -UseBasicParsing -ErrorAction Stop
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
            Write-Host "  HTTP $($response.StatusCode) — credential accepted." -ForegroundColor Green
        } else {
            throw "Probe failed: HTTP $($response.StatusCode). Check your -ApiKey."
        }
    } catch [System.Net.WebException] {
        $statusCode = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { -1 }
        Write-Warning "Probe failed (HTTP $statusCode). $($_.Exception.Message)"
        throw "Probe failed. Pass -ApiKey with a working credential, or skip -Probe to write anyway."
    } catch {
        Write-Warning "Probe failed: $($_.Exception.Message)"
        throw "Probe failed. Pass -ApiKey with a working credential, or skip -Probe to write anyway."
    }
    Write-Host ""
}

# ---- Locate appsettings.json ---------------------------------------------

function Get-DefaultConfigPath {
    $candidates = @()
    $scriptDir = $PSScriptRoot
    $repoRoot = Split-Path -Parent $scriptDir
    $candidates += (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
    $candidates += (Join-Path $repoRoot 'src/PalLLM.Sidecar/bin/Release/net10.0/appsettings.json')
    $candidates += (Join-Path $repoRoot 'src/PalLLM.Sidecar/bin/Debug/net10.0/appsettings.json')
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $candidates[0]
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}

Write-Host "Config target : $ConfigPath" -ForegroundColor White
Write-Host ""

# ---- Compute config delta -------------------------------------------------

$config = if (Test-Path -LiteralPath $ConfigPath) {
    Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json -AsHashtable
} else {
    @{ PalLLM = @{ Inference = @{} } }
}
if (-not $config.ContainsKey('PalLLM')) { $config['PalLLM'] = @{} }
if (-not $config['PalLLM'].ContainsKey('Inference')) { $config['PalLLM']['Inference'] = @{} }
$inference = $config['PalLLM']['Inference']

$priorBaseUrl = if ($inference.ContainsKey('BaseUrl')) { [string]$inference['BaseUrl'] } else { '' }
$priorModel = if ($inference.ContainsKey('Model')) { [string]$inference['Model'] } else { '' }
$priorEnabled = if ($inference.ContainsKey('Enabled')) { [bool]$inference['Enabled'] } else { $false }
$priorApiKey = if ($inference.ContainsKey('ApiKey')) { [string]$inference['ApiKey'] } else { '' }

$inference['BaseUrl'] = $BaseUrl
$inference['Model'] = $Model
$inference['ApiKey'] = $ApiKey
$inference['Enabled'] = $true
# Cloud providers have their own server-side residency; PalLLM should
# not attempt the per-request ttl hint. Mirror connect-llamacpp's
# Disabled posture.
$inference['ResidencyProvider'] = 'Disabled'
$inference['ResidencyTtlSeconds'] = 0

$delta = @()
if ($priorBaseUrl -ne $BaseUrl) { $delta += "  Inference.BaseUrl  : $priorBaseUrl -> $BaseUrl" }
if ($priorModel -ne $Model)     { $delta += "  Inference.Model    : $priorModel -> $Model" }
if ($priorEnabled -ne $true)    { $delta += "  Inference.Enabled  : $priorEnabled -> True" }
if ($priorApiKey -ne $ApiKey)   { $delta += "  Inference.ApiKey   : <was=$($priorApiKey.Length) chars> -> <new=$($ApiKey.Length) chars>" }

if ($delta.Count -eq 0) {
    Write-Host "Config already matches the requested cloud setup. No changes needed." -ForegroundColor Green
    exit 0
}

Write-Host "Planned changes:" -ForegroundColor White
$delta | ForEach-Object { Write-Host $_ }
Write-Host ""

if ($DryRun.IsPresent) {
    Write-Host "[DryRun] No file changes." -ForegroundColor Yellow
    exit 0
}

if (-not $WriteConfig.IsPresent) {
    Write-Host "Pass -WriteConfig to apply these changes to appsettings.json." -ForegroundColor Yellow
    exit 0
}

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM cloud lane ($Provider / $Model)")) {
    exit 0
}

$parent = Split-Path -Parent $ConfigPath
if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8

Write-Host "Config written. Restart the sidecar to pick up the cloud lane." -ForegroundColor Green
Write-Host ""
Write-Host "SECURITY REMINDER:" -ForegroundColor Yellow
Write-Host "  The ApiKey is now in $ConfigPath. Verify this file is NOT in version control." -ForegroundColor Yellow
Write-Host "  Prefer setting `$env:PalLLM__Inference__ApiKey` instead of committing the key." -ForegroundColor Yellow
