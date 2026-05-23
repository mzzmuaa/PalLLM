<#
.SYNOPSIS
    Interactive first-time configuration wizard for PalLLM.
    Walks the operator through five questions and writes a coherent
    appsettings.json without manual JSON editing.

.DESCRIPTION
    The five questions, in order:

      1. Where is Palworld installed? (skipped on non-Windows)
      2. Wire an OpenAI-compatible inference endpoint? (none / local
         engine on default port / custom URL)
      3. Wire local TTS? (off / on at default port)
      4. Wire local vision describer? (off / on at default port)
      5. Privacy posture confirmation - any of the above flips
         away from "fully local; zero outbound traffic"?

    The wizard NEVER writes credentials. NEVER opens a network
    connection itself. NEVER edits any file outside the configured
    appsettings.json target. -DryRun previews the result without
    writing.

    Same priority order as `pal config` for resolving which
    appsettings.json to update:
      1. release zip path: sidecar/publish/appsettings.json
      2. dev path:         src/PalLLM.Sidecar/appsettings.json
      3. user-local path:  %LOCALAPPDATA%\Pal\Saved\PalLLM\appsettings.json

    On the first overwrite of an existing file the wizard takes a
    one-shot .bak so re-running is safe.

.PARAMETER ConfigPath
    Override the appsettings.json target. Default: same priority
    order as `pal config`.

.PARAMETER DryRun
    Print the planned config without writing anything.

.PARAMETER SkipPrompts
    Refuse to prompt; useful for CI smoke-testing the script's
    structure without a TTY. Returns early with a banner.

.EXAMPLE
    pwsh ./scripts/pal-config-wizard.ps1
    # Walk through the five questions; write appsettings.json on confirm.

.EXAMPLE
    pwsh ./scripts/pal-config-wizard.ps1 -DryRun
    # Same flow, but print the resulting JSON instead of writing.

.NOTES
    Verb shortcut:  pal config wizard

    For a non-interactive view of the effective config, see:
        pal config show

    For the underlying file directly:
        pal config        (opens in your default editor)

    See docs/ENV_VARS.md for every config knob with defaults +
    effects, and docs/TUNING.md for too-low / too-high guidance.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ConfigPath,
    [switch]$DryRun,
    [switch]$SkipPrompts
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
# JSON helpers (shared shape with connect-ollama / connect-vllm)
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

function Read-OrSeedConfig {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ PalLLM = [ordered]@{} }
    }
    $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return [ordered]@{ PalLLM = [ordered]@{} }
    }
    $parsed = $raw | ConvertFrom-Json -ErrorAction Stop
    return ConvertTo-MutableConfig $parsed
}

# -----------------------------------------------------------------------------
# Prompt helpers
# -----------------------------------------------------------------------------

function Ask-Choice {
    param(
        [string]$Question,
        [string[]]$Choices,
        [int]$Default = 0
    )
    Write-Host ""
    Write-Host $Question -ForegroundColor White
    for ($i = 0; $i -lt $Choices.Count; $i++) {
        $marker = if ($i -eq $Default) { '>' } else { ' ' }
        Write-Host ("  {0} {1}) {2}" -f $marker, ($i + 1), $Choices[$i])
    }
    $reply = Read-Host "  Pick (1-$($Choices.Count)) [default $($Default+1)]"
    if ([string]::IsNullOrWhiteSpace($reply)) { return $Default }
    if ($reply -match '^\d+$') {
        $idx = [int]$reply - 1
        if ($idx -ge 0 -and $idx -lt $Choices.Count) { return $idx }
    }
    Write-Host "  (invalid; using default)" -ForegroundColor DarkGray
    return $Default
}

function Ask-String {
    param(
        [string]$Question,
        [string]$Default = ''
    )
    Write-Host ""
    Write-Host $Question -ForegroundColor White
    $reply = if ([string]::IsNullOrWhiteSpace($Default)) {
        Read-Host "  >"
    } else {
        Read-Host "  > [default: $Default]"
    }
    if ([string]::IsNullOrWhiteSpace($reply)) { return $Default }
    return $reply.Trim()
}

# -----------------------------------------------------------------------------
# Banner
# -----------------------------------------------------------------------------

Write-Host ""
Write-Host "PalLLM configuration wizard" -ForegroundColor Cyan
Write-Host "  Five questions. None are sticky. You can re-run anytime." -ForegroundColor DarkGray
Write-Host "  Target: $ConfigPath" -ForegroundColor DarkGray
Write-Host "  Default: zero outbound traffic. Every opt-in below is asked, never assumed." -ForegroundColor DarkGray
Write-Host ""

if ($SkipPrompts.IsPresent) {
    Write-Host "[SkipPrompts] Skipping prompts. Use without -SkipPrompts to run the wizard." -ForegroundColor Yellow
    return
}

# -----------------------------------------------------------------------------
# Question 1 - Palworld install path (Windows-only; informational, not stored)
# -----------------------------------------------------------------------------

$onWindows = ($IsWindows -or $env:OS -eq 'Windows_NT')

if ($onWindows) {
    $palworldGuess = ''
    foreach ($drive in @('C:', 'D:', 'E:', 'F:')) {
        $candidate = Join-Path $drive 'SteamLibrary\steamapps\common\Palworld'
        if (Test-Path -LiteralPath $candidate) { $palworldGuess = $candidate; break }
    }
    if (-not $palworldGuess) {
        $candidate = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\Palworld'
        if (Test-Path -LiteralPath $candidate) { $palworldGuess = $candidate }
    }

    $palworldPath = Ask-String -Question "1. Where is Palworld installed? (Enter to confirm, or paste a path; leave blank if not installed)" -Default $palworldGuess
} else {
    Write-Host "1. (skipping Palworld path question - non-Windows host; mod surface is Windows-only)" -ForegroundColor DarkGray
    $palworldPath = ''
}

# -----------------------------------------------------------------------------
# Question 2 - Inference wiring
# -----------------------------------------------------------------------------

$inferenceChoice = Ask-Choice -Question "2. Wire an OpenAI-compatible inference endpoint?" -Choices @(
    "No - keep deterministic-fallback only (zero outbound traffic)"
    "Yes - local engine at default port http://127.0.0.1:11434/v1/"
    "Yes - custom URL (you'll be asked next)"
) -Default 0

$inferenceBaseUrl = ''
$inferenceModel = ''
$inferenceEnabled = $false
switch ($inferenceChoice) {
    1 {
        $inferenceBaseUrl = 'http://127.0.0.1:11434/v1/'
        $inferenceModel = Ask-String -Question "  Model name? (e.g. gemma3:4b, qwen3:14b-instruct)" -Default 'gemma3:4b'
        $inferenceEnabled = $true
    }
    2 {
        $inferenceBaseUrl = Ask-String -Question "  Custom base URL? (e.g. http://localhost:8000/v1/)" -Default 'http://localhost:8000/v1/'
        $inferenceModel = Ask-String -Question "  Model name?" -Default 'placeholder-model'
        $inferenceEnabled = $true
    }
}

# -----------------------------------------------------------------------------
# Question 3 - TTS
# -----------------------------------------------------------------------------

$ttsChoice = Ask-Choice -Question "3. Wire local TTS (text-to-speech) at the default port?" -Choices @(
    "No - keep TTS off"
    "Yes - local TTS at http://127.0.0.1:5002/synthesize"
) -Default 0
$ttsEnabled = ($ttsChoice -eq 1)

# -----------------------------------------------------------------------------
# Question 4 - Vision
# -----------------------------------------------------------------------------

$visionChoice = Ask-Choice -Question "4. Wire local vision describer at the default port?" -Choices @(
    "No - keep vision off"
    "Yes - local vision at http://127.0.0.1:11434/v1/ (same engine, different model)"
) -Default 0
$visionEnabled = ($visionChoice -eq 1)
$visionModel = ''
if ($visionEnabled) {
    $visionModel = Ask-String -Question "  Vision model name?" -Default 'gemma4:e2b'
}

# -----------------------------------------------------------------------------
# Question 5 - Privacy posture confirmation
# -----------------------------------------------------------------------------

$flippedAway = $inferenceEnabled -or $ttsEnabled -or $visionEnabled
if ($flippedAway) {
    Write-Host ""
    Write-Host "Privacy posture: " -NoNewline -ForegroundColor White
    Write-Host "no longer fully air-gapped." -ForegroundColor Yellow
    Write-Host "  Outbound traffic now hits:" -ForegroundColor DarkGray
    if ($inferenceEnabled) { Write-Host "    - $inferenceBaseUrl  (chat inference)" -ForegroundColor DarkGray }
    if ($ttsEnabled)       { Write-Host "    - http://127.0.0.1:5002/synthesize  (TTS)" -ForegroundColor DarkGray }
    if ($visionEnabled)    { Write-Host "    - $inferenceBaseUrl  (vision describer)" -ForegroundColor DarkGray }
    $okay = Ask-Choice -Question "5. Confirm this posture?" -Choices @(
        "Yes - proceed"
        "No - go back to defaults (everything stays off)"
    ) -Default 0
    if ($okay -eq 1) {
        $inferenceEnabled = $false
        $ttsEnabled = $false
        $visionEnabled = $false
    }
} else {
    Write-Host ""
    Write-Host "Privacy posture: fully local, zero outbound traffic." -ForegroundColor Green
    $null = Ask-Choice -Question "5. Confirm this posture?" -Choices @("Yes - proceed", "No - cancel") -Default 0
}

# -----------------------------------------------------------------------------
# Apply
# -----------------------------------------------------------------------------

$config = Read-OrSeedConfig -Path $ConfigPath
if (-not (Test-ConfigKey $config 'PalLLM')) { $config['PalLLM'] = [ordered]@{} }
$pal = $config['PalLLM']

# Inference
if (-not (Test-ConfigKey $pal 'Inference')) { $pal['Inference'] = [ordered]@{} }
$pal['Inference']['Enabled'] = $inferenceEnabled
if ($inferenceEnabled) {
    $pal['Inference']['BaseUrl'] = $inferenceBaseUrl
    $pal['Inference']['Model']   = $inferenceModel
}

# TTS
if (-not (Test-ConfigKey $pal 'Tts')) { $pal['Tts'] = [ordered]@{} }
$pal['Tts']['Enabled'] = $ttsEnabled

# Vision
if (-not (Test-ConfigKey $pal 'Vision')) { $pal['Vision'] = [ordered]@{} }
$pal['Vision']['Enabled'] = $visionEnabled
if ($visionEnabled) {
    $pal['Vision']['BaseUrl'] = 'http://127.0.0.1:11434/v1/'
    $pal['Vision']['Model']   = $visionModel
}

Write-Host ""
Write-Host "Resulting PalLLM block (preview):" -ForegroundColor Cyan
$preview = ($pal | ConvertTo-Json -Depth 6)
Write-Host $preview -ForegroundColor DarkGray
Write-Host ""

if ($DryRun.IsPresent) {
    Write-Host "[DryRun] No file changes." -ForegroundColor Yellow
    [pscustomobject]@{
        DryRun           = $true
        ConfigPath       = $ConfigPath
        InferenceEnabled = $inferenceEnabled
        InferenceModel   = $inferenceModel
        TtsEnabled       = $ttsEnabled
        VisionEnabled    = $visionEnabled
        PalworldPath     = $palworldPath
    } | Write-Output
    return
}

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Write PalLLM config from wizard")) {
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
Write-Host "  1. Boot the sidecar so it picks up the new config: pal play"
Write-Host "  2. Probe: pal hello"
Write-Host "  3. See effective config later: pal config show"
Write-Host ""

[pscustomobject]@{
    DryRun           = $false
    ConfigPath       = $ConfigPath
    InferenceEnabled = $inferenceEnabled
    InferenceModel   = $inferenceModel
    TtsEnabled       = $ttsEnabled
    VisionEnabled    = $visionEnabled
    PalworldPath     = $palworldPath
    Backup           = "$ConfigPath.bak"
} | Write-Output
