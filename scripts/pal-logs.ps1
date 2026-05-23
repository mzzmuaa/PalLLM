<#
.SYNOPSIS
    Show recent sidecar log paths and the most recent activity entries.
    The "what just happened?" verb. Lighter than `pal support` (which
    builds a full bundle), heavier than `pal hello` (which is a one-shot
    probe). Designed for "the chat reply felt off; what's in the logs?"

.DESCRIPTION
    PalLLM's sidecar logs land in three places depending on how it was
    started. This verb checks all three, names what's where, and reads
    the most recent entries from each:

      1. The launch evidence directory under the runtime root:
         %LOCALAPPDATA%\Pal\Saved\PalLLM\LaunchEvidence\
      2. The smoke / proof / native artifacts under the runtime root:
         %LOCALAPPDATA%\Pal\Saved\PalLLM\NativeReadiness\
      3. The latest full-audit results:
         <repo>/artifacts/full-audit/<timestamp>/RESULTS.md

    Pure local read; no network call.

.PARAMETER Tail
    Number of recent lines / entries to show per source. Default 20.
    Higher values produce noisier output but cover more history.

.PARAMETER WhereOnly
    Print only the paths the logs live at, then exit. Useful when you
    want to grep / tail those files yourself.

.PARAMETER Json
    Emit a structured record (paths + recent entries per source) for
    programmatic consumption.

.EXAMPLE
    pwsh ./scripts/pal-logs.ps1
    # Default: paths + last 20 entries per source.

.EXAMPLE
    pwsh ./scripts/pal-logs.ps1 -WhereOnly
    # Just the paths so you can `tail -f` them yourself.

.NOTES
    Verb shortcut:  pal logs

    For a full anonymized triage bundle (everything redacted, packaged
    into a zip), use `pal support`. This verb is the lighter "give me
    a fast read" alternative.
#>
[CmdletBinding()]
param(
    [int]$Tail = 20,
    [switch]$WhereOnly,
    [switch]$Json
)

$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$runtimeRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal\Saved\PalLLM'
$launchEvidenceDir = Join-Path $runtimeRoot 'LaunchEvidence'
$nativeReadinessDir = Join-Path $runtimeRoot 'NativeReadiness'
$auditDir = Join-Path $repoRoot 'artifacts/full-audit'

$paths = [pscustomobject]@{
    LaunchEvidenceDir   = $launchEvidenceDir
    NativeReadinessDir  = $nativeReadinessDir
    AuditDir            = $auditDir
}

function Get-RecentFiles {
    param([string]$Dir, [int]$Count = 5, [string[]]$Patterns = @('*'))
    if (-not (Test-Path -LiteralPath $Dir)) { return @() }
    $files = New-Object System.Collections.ArrayList
    foreach ($p in $Patterns) {
        $found = Get-ChildItem -Path $Dir -Filter $p -File -ErrorAction SilentlyContinue
        foreach ($f in $found) { [void]$files.Add($f) }
    }
    return $files | Sort-Object LastWriteTime -Descending | Select-Object -First $Count
}

function Read-Tail {
    param([string]$File, [int]$Count)
    if (-not (Test-Path -LiteralPath $File)) { return @() }
    return Get-Content -LiteralPath $File -Tail $Count -ErrorAction SilentlyContinue
}

# Collect launch evidence (most recent JSON / log files).
$launchFiles = Get-RecentFiles -Dir $launchEvidenceDir -Count 3 -Patterns @('*.json','*.log','*.txt')

# Collect native readiness (proof packets, smoke replays).
$nativeFiles = Get-RecentFiles -Dir $nativeReadinessDir -Count 3 -Patterns @('*.json','*.log','*.txt')

# Latest audit timestamped folder.
$latestAudit = $null
if (Test-Path -LiteralPath $auditDir) {
    $latestAudit = Get-ChildItem -Path $auditDir -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}
$auditResultsPath = if ($latestAudit) { Join-Path $latestAudit.FullName 'RESULTS.md' } else { $null }

if ($WhereOnly.IsPresent -or $Json.IsPresent) {
    $payload = [pscustomobject]@{
        RuntimeRoot         = $runtimeRoot
        LaunchEvidenceDir   = $launchEvidenceDir
        LaunchEvidenceExists= (Test-Path -LiteralPath $launchEvidenceDir)
        NativeReadinessDir  = $nativeReadinessDir
        NativeReadinessExists= (Test-Path -LiteralPath $nativeReadinessDir)
        LatestAudit         = $auditResultsPath
        RecentLaunchFiles   = ($launchFiles | ForEach-Object { $_.FullName })
        RecentNativeFiles   = ($nativeFiles | ForEach-Object { $_.FullName })
    }
    if ($Json.IsPresent) {
        $payload | ConvertTo-Json -Depth 4
        return
    }
    Write-Host ""
    Write-Host "PalLLM log paths" -ForegroundColor Cyan
    Write-Host ("  runtime root          : {0}" -f $runtimeRoot)
    Write-Host ("  launch evidence dir   : {0}" -f $launchEvidenceDir)
    Write-Host ("    (exists)            : {0}" -f (Test-Path -LiteralPath $launchEvidenceDir))
    Write-Host ("  native readiness dir  : {0}" -f $nativeReadinessDir)
    Write-Host ("    (exists)            : {0}" -f (Test-Path -LiteralPath $nativeReadinessDir))
    if ($auditResultsPath) {
        Write-Host ("  latest audit RESULTS  : {0}" -f $auditResultsPath)
    } else {
        Write-Host  "  latest audit RESULTS  : (none yet -- run 'pal fast-audit')"
    }
    Write-Host ""
    return
}

Write-Host ""
Write-Host "PalLLM recent activity" -ForegroundColor Cyan
Write-Host ("  runtime root: {0}" -f $runtimeRoot) -ForegroundColor DarkGray
Write-Host ""

# Latest audit
if ($auditResultsPath -and (Test-Path -LiteralPath $auditResultsPath)) {
    Write-Host "[latest audit]" -ForegroundColor White
    Write-Host ("  {0}" -f $auditResultsPath) -ForegroundColor DarkGray
    Write-Host ""
    $auditTail = Read-Tail -File $auditResultsPath -Count $Tail
    foreach ($l in $auditTail) { Write-Host "  $l" }
    Write-Host ""
} else {
    Write-Host "[latest audit]   (no audit results yet -- run 'pal fast-audit')" -ForegroundColor DarkGray
    Write-Host ""
}

# Recent launch evidence
if ($launchFiles -and $launchFiles.Count -gt 0) {
    Write-Host "[recent launch evidence]" -ForegroundColor White
    foreach ($f in $launchFiles) {
        Write-Host ("  {0}  ({1:yyyy-MM-dd HH:mm:ss})" -f $f.Name, $f.LastWriteTime) -ForegroundColor DarkGray
    }
    Write-Host ""
} else {
    Write-Host "[recent launch evidence]  (none -- has the sidecar booted yet?)" -ForegroundColor DarkGray
    Write-Host ""
}

# Recent native readiness artifacts
if ($nativeFiles -and $nativeFiles.Count -gt 0) {
    Write-Host "[recent native-readiness artifacts]" -ForegroundColor White
    foreach ($f in $nativeFiles) {
        Write-Host ("  {0}  ({1:yyyy-MM-dd HH:mm:ss})" -f $f.Name, $f.LastWriteTime) -ForegroundColor DarkGray
    }
    Write-Host ""
}

Write-Host "Heavier alternatives:" -ForegroundColor DarkGray
Write-Host "  pal doctor          # full sidecar diagnostic + smoke + delivery replay" -ForegroundColor DarkGray
Write-Host "  pal support         # privacy-redacted bundle (zip + JSON manifest)" -ForegroundColor DarkGray
Write-Host "  pal logs -WhereOnly # just the paths so you can tail them yourself" -ForegroundColor DarkGray
Write-Host ""
