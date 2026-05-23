<#
.SYNOPSIS
    Print the effective PalLLM config in a readable form, with the
    source annotated per key (file value vs default vs env var).

.DESCRIPTION
    Reads the same appsettings.json as `pal config` (priority order:
    release zip path > dev path > user-local path) and renders the
    PalLLM block grouped by section (Bridge / Inference / Fallback /
    Tts / Vision / Session / Automation / Auth / Http / McpClient).

    Each line shows:
        Section.Key  = value      [source]

    Where source is one of:
        file             - explicit value in appsettings.json
        env              - PalLLM__Section__Key environment variable
                           is set (overrides the file value)
        default          - neither file nor env; value is the
                           Domain-side compiled default

    The wizard, the sidecar runtime, and any operator tooling all
    agree on the same priority. Use this verb to confirm what the
    sidecar will actually see at boot.

.PARAMETER ConfigPath
    Override the appsettings.json target. Default: same priority
    order as `pal config`.

.PARAMETER Section
    Print only one section. Useful for "what's my Inference config
    look like?" focused checks.

.PARAMETER Json
    Emit a structured object instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-config-show.ps1
    # Print every section with its source.

.EXAMPLE
    pwsh ./scripts/pal-config-show.ps1 -Section Inference
    # Just the Inference block.

.EXAMPLE
    pwsh ./scripts/pal-config-show.ps1 -Json | ConvertFrom-Json
    # Programmatic consumption.

.NOTES
    Verb shortcut:  pal config show

    Companion to:
      pal config         (opens the underlying file in your editor)
      pal config wizard  (interactive first-time setup)
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string]$Section,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

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
    return $candidates[1]
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

# Load file. Tolerate missing - render purely from defaults + env.
$fileBlock = [ordered]@{}
$fileExists = Test-Path -LiteralPath $ConfigPath
if ($fileExists) {
    try {
        $raw = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $parsed = $raw | ConvertFrom-Json -ErrorAction Stop
            if ($parsed.PSObject.Properties['PalLLM']) {
                $palObject = $parsed.PalLLM
                # Convert PSCustomObject -> hashtable for uniform access.
                foreach ($prop in $palObject.PSObject.Properties) {
                    $fileBlock[$prop.Name] = $prop.Value
                }
            }
        }
    } catch {
        Write-Warning "Could not parse $ConfigPath as JSON: $_"
    }
}

# Compiled defaults (mirror the C# PalLlmOptions defaults). Single source of
# truth for "what's the value if nobody sets it?".
$defaults = [ordered]@{
    'Bridge.Enabled'                    = $true
    'Bridge.PollIntervalMs'              = 1000
    'Bridge.MaxEventsPerPoll'            = 32
    'Bridge.OutboxEnabled'               = $true
    'Inference.Enabled'                  = $false
    'Inference.BaseUrl'                  = 'http://127.0.0.1:11434/v1/'
    'Inference.Model'                    = '(unset)'
    'Inference.Temperature'              = 0.7
    'Inference.TimeoutSeconds'           = 60
    'Inference.CircuitBreakerFailureThreshold' = 5
    'Fallback.Enabled'                   = $true
    'Fallback.UseWhenInferenceDisabled'  = $true
    'Fallback.UseWhenInferenceFails'     = $true
    'Tts.Enabled'                        = $false
    'Tts.BaseUrl'                        = 'http://127.0.0.1:5002/synthesize'
    'Vision.Enabled'                     = $false
    'Vision.BaseUrl'                     = 'http://127.0.0.1:11434/v1/'
    'Session.Enabled'                    = $true
    'Session.EnableAutosave'             = $true
    'Automation.Enabled'                 = $false
    'Auth.ApiKey'                        = '(unset)'
    'Auth.ProtectMetrics'                = $false
    'Auth.ProtectHealth'                 = $false
    'Http.ChatConcurrentRequests'        = 2
    'Http.ChatQueueLimit'                = 4
    'McpClient.DiscoveryIntervalSeconds' = 300
}

# Resolve every default key against env var + file value.
$rows = New-Object System.Collections.ArrayList
foreach ($entry in $defaults.GetEnumerator()) {
    $dottedKey = $entry.Key
    $defaultVal = $entry.Value
    $parts = $dottedKey -split '\.'
    if ($parts.Count -ne 2) { continue }
    $sectionName = $parts[0]
    $keyName = $parts[1]

    # Filter by -Section
    if (-not [string]::IsNullOrWhiteSpace($Section) -and $sectionName -ne $Section) { continue }

    # Env var override (PalLLM__Section__Key)
    $envName = "PalLLM__$($sectionName)__$($keyName)"
    $envVal = [Environment]::GetEnvironmentVariable($envName)
    $hasEnv = -not [string]::IsNullOrEmpty($envVal)

    # File value
    $hasFile = $false
    $fileVal = $null
    if ($fileBlock.Contains($sectionName)) {
        $sectionObj = $fileBlock[$sectionName]
        if ($sectionObj -and $sectionObj.PSObject.Properties[$keyName]) {
            $hasFile = $true
            $fileVal = $sectionObj.PSObject.Properties[$keyName].Value
        }
    }

    # Effective + source
    if ($hasEnv) {
        $effective = $envVal
        $source = 'env'
    } elseif ($hasFile) {
        $effective = $fileVal
        $source = 'file'
    } else {
        $effective = $defaultVal
        $source = 'default'
    }

    [void]$rows.Add([pscustomobject]@{
        Section   = $sectionName
        Key       = $keyName
        Effective = $effective
        Source    = $source
        Default   = $defaultVal
    })
}

if ($Json.IsPresent) {
    [pscustomobject]@{
        ConfigPath = $ConfigPath
        FileExists = $fileExists
        Section    = $Section
        Rows       = $rows
    } | ConvertTo-Json -Depth 6
    return
}

Write-Host ""
Write-Host "PalLLM effective config" -ForegroundColor Cyan
Write-Host ("  source file : {0}" -f $ConfigPath)
Write-Host ("  exists      : {0}" -f $fileExists)
if (-not [string]::IsNullOrWhiteSpace($Section)) {
    Write-Host ("  section     : {0}" -f $Section)
}
Write-Host ""

if (-not $rows -or $rows.Count -eq 0) {
    Write-Host "  (no rows match)" -ForegroundColor Yellow
    Write-Host ""
    return
}

$maxKeyWidth = ($rows | ForEach-Object { ($_.Section.Length + 1 + $_.Key.Length) } | Measure-Object -Maximum).Maximum
$lastSection = ''
foreach ($r in $rows) {
    if ($r.Section -ne $lastSection) {
        Write-Host ""
        Write-Host ("  [{0}]" -f $r.Section) -ForegroundColor White
        $lastSection = $r.Section
    }
    $keyDot = "$($r.Section).$($r.Key)"
    $padded = $keyDot.PadRight($maxKeyWidth)
    $color = switch ($r.Source) {
        'env'     { 'Yellow' }
        'file'    { 'Green' }
        'default' { 'DarkGray' }
        default   { 'Gray' }
    }
    Write-Host ("  {0}  = {1,-30}  [{2}]" -f $padded, $r.Effective, $r.Source) -ForegroundColor $color
}
Write-Host ""
Write-Host "Source legend:" -ForegroundColor DarkGray
Write-Host "  green   = explicit value in appsettings.json" -ForegroundColor DarkGray
Write-Host "  yellow  = PalLLM__Section__Key env var override" -ForegroundColor DarkGray
Write-Host "  gray    = compiled default" -ForegroundColor DarkGray
Write-Host ""
