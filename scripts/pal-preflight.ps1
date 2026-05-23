<#
.SYNOPSIS
    Single-command readiness check. Answers "am I ready to play?" with
    one verdict (READY / NEARLY READY / NOT READY) plus a per-check
    punch list.

.DESCRIPTION
    Distinct from `pal doctor`:
      - `pal doctor`    runs heavy diagnostics (smoke loop + delivery
                        replay) and reports what's broken.
      - `pal preflight` runs a fast (~3 second) checklist of common
                        gotchas and reports a single READY / NEARLY /
                        NOT READY verdict.

    The checks, in order:

      1.  .NET 10 SDK present                 (build / dev only)
      2.  Sidecar reachable on the configured BaseUrl
      3.  Inference wired? (config check, not behaviour check)
      4.  At least one personality pack loaded
      5.  Hardware tier detected
      6.  Latest audit RESULTS.md = PASS  (if one exists)
      7.  No drift in PROJECT_NUMBERS.json (parses cleanly)
      8.  Disk space available on the runtime root drive (>= 1 GB)
      9.  agents.json + pal.json parse as valid JSON
     10.  PalLLM repo root well-formed (pal.ps1 + sidecar src present)

    Each check returns one of: pass / warn / fail. Failures push the
    overall verdict to NOT READY. Warns push to NEARLY READY but
    don't block. All passes = READY.

    Pure local checks; the only network call is the sidecar probe at
    /api/health (LAN, default localhost:5088).

.PARAMETER BaseUrl
    Sidecar URL. Default http://localhost:5088.

.PARAMETER Json
    Emit a structured record (verdict + per-check rows) instead of
    pretty text.

.EXAMPLE
    pwsh ./scripts/pal-preflight.ps1
    # Single-command verdict + punch list.

.EXAMPLE
    pwsh ./scripts/pal-preflight.ps1 -Json | ConvertFrom-Json | Where-Object Status -eq fail
    # Programmatic: just the failures.

.NOTES
    Verb shortcut:  pal preflight

    For deeper diagnostics:
        pal doctor       runs smoke + delivery replay
        pal benchmark    measures actual chat latency
        pal logs         shows recent activity
        pal support      builds a privacy-redacted bundle
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [switch]$Json
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot

$rows = New-Object System.Collections.ArrayList

function Add-Check {
    param(
        [string]$Id,
        [string]$Question,
        [string]$Status,    # pass / warn / fail
        [string]$Detail = ''
    )
    [void]$rows.Add([pscustomobject]@{
        Id       = $Id
        Question = $Question
        Status   = $Status
        Detail   = $Detail
    })
}

# 1. .NET SDK
try {
    $dotnetVersion = (& dotnet --version 2>$null).Trim()
    if ([string]::IsNullOrWhiteSpace($dotnetVersion)) {
        Add-Check 'dotnet-sdk' '.NET SDK installed?' 'fail' 'dotnet command not found'
    } elseif ($dotnetVersion -match '^10\.') {
        Add-Check 'dotnet-sdk' '.NET 10 SDK installed?' 'pass' "version $dotnetVersion"
    } else {
        Add-Check 'dotnet-sdk' '.NET 10 SDK installed?' 'warn' "found $dotnetVersion (want 10.x for build)"
    }
} catch {
    Add-Check 'dotnet-sdk' '.NET SDK installed?' 'fail' $_.Exception.Message
}

# 2. Sidecar reachable
$sidecarReachable = $false
try {
    $null = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/health" -Method Get -TimeoutSec 3 -ErrorAction Stop
    $sidecarReachable = $true
    Add-Check 'sidecar-up' "Sidecar reachable at $BaseUrl ?" 'pass' 'health endpoint responded'
} catch {
    Add-Check 'sidecar-up' "Sidecar reachable at $BaseUrl ?" 'warn' "not reachable (run 'pal play')"
}

# 3. Inference wired? (config check)
$appsettingsCandidates = @(
    (Join-Path $repoRoot 'sidecar/publish/appsettings.json')
    (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM/appsettings.json')
)
$activeConfig = $appsettingsCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
$inferenceEnabled = $false
if ($activeConfig) {
    try {
        $cfg = Get-Content -LiteralPath $activeConfig -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($cfg.PalLLM -and $cfg.PalLLM.Inference -and $cfg.PalLLM.Inference.Enabled) {
            $inferenceEnabled = $true
        }
    } catch { }
}
if ($inferenceEnabled) {
    Add-Check 'inference-wired' 'Inference endpoint wired?' 'pass' 'PalLLM:Inference:Enabled = true'
} else {
    Add-Check 'inference-wired' 'Inference endpoint wired?' 'warn' "deterministic-fallback only (run 'pal connect ollama', 'pal connect llamacpp', or 'pal config wizard' to wire one)"
}

# 4. Personality pack loaded
if ($sidecarReachable) {
    try {
        $packs = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/packs" -Method Get -TimeoutSec 3 -ErrorAction Stop
        $packArr = if ($packs.PSObject.Properties['packs']) { @($packs.packs) } else { @() }
        if ($packArr.Count -gt 0) {
            Add-Check 'pack-loaded' 'At least one personality pack loaded?' 'pass' "$($packArr.Count) pack(s) loaded"
        } else {
            Add-Check 'pack-loaded' 'At least one personality pack loaded?' 'warn' "no packs (run 'pal pack new' or copy from samples/packs/)"
        }
    } catch {
        Add-Check 'pack-loaded' 'At least one personality pack loaded?' 'warn' "could not query /api/packs"
    }
} else {
    Add-Check 'pack-loaded' 'At least one personality pack loaded?' 'warn' "sidecar not running"
}

# 5. Hardware tier detected
if ($sidecarReachable) {
    try {
        $hw = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/hardware" -Method Get -TimeoutSec 3 -ErrorAction Stop
        $tier = if ($hw.detectedTier) { $hw.detectedTier } else { '(unknown)' }
        Add-Check 'hardware-tier' 'Hardware tier detected?' 'pass' "tier=$tier"
    } catch {
        Add-Check 'hardware-tier' 'Hardware tier detected?' 'warn' 'could not query /api/hardware'
    }
} else {
    Add-Check 'hardware-tier' 'Hardware tier detected?' 'warn' 'sidecar not running'
}

# 6. Latest audit pass
$auditDir = Join-Path $repoRoot 'artifacts/full-audit'
$latestAudit = $null
if (Test-Path -LiteralPath $auditDir) {
    $latestAudit = Get-ChildItem -Path $auditDir -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if ($latestAudit) {
    $resultsFile = Join-Path $latestAudit.FullName 'RESULTS.md'
    if (Test-Path -LiteralPath $resultsFile) {
        $resultsContent = Get-Content -LiteralPath $resultsFile -Raw -ErrorAction SilentlyContinue
        if ($resultsContent -match '(?m)^- Overall:\s*\*\*PASS\*\*') {
            Add-Check 'latest-audit' 'Latest drift audit = PASS?' 'pass' $latestAudit.Name
        } else {
            Add-Check 'latest-audit' 'Latest drift audit = PASS?' 'fail' "$($latestAudit.Name) (see $resultsFile)"
        }
    } else {
        Add-Check 'latest-audit' 'Latest drift audit = PASS?' 'warn' "no RESULTS.md in $($latestAudit.Name)"
    }
} else {
    Add-Check 'latest-audit' 'Latest drift audit = PASS?' 'warn' "never run (try 'pal fast-audit')"
}

# 7. PROJECT_NUMBERS.json parses
$numbersPath = Join-Path $repoRoot 'docs/PROJECT_NUMBERS.json'
if (Test-Path -LiteralPath $numbersPath) {
    try {
        $null = Get-Content -LiteralPath $numbersPath -Raw -Encoding UTF8 | ConvertFrom-Json
        Add-Check 'project-numbers' 'docs/PROJECT_NUMBERS.json parses cleanly?' 'pass' ''
    } catch {
        Add-Check 'project-numbers' 'docs/PROJECT_NUMBERS.json parses cleanly?' 'fail' $_.Exception.Message
    }
} else {
    Add-Check 'project-numbers' 'docs/PROJECT_NUMBERS.json parses cleanly?' 'warn' 'file missing'
}

# 8. Disk space
try {
    $runtimeRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM'
    $rootFull = (Get-Item -LiteralPath ([System.IO.Path]::GetPathRoot($runtimeRoot)) -ErrorAction Stop)
    $drive = Get-PSDrive -Name ([IO.Path]::GetPathRoot($runtimeRoot).TrimEnd(':\').TrimEnd(':')) -ErrorAction SilentlyContinue
    if ($drive) {
        $freeGB = [math]::Round($drive.Free / 1GB, 1)
        if ($freeGB -ge 1) {
            Add-Check 'disk-space' 'At least 1 GB free on runtime drive?' 'pass' "$freeGB GB free"
        } else {
            Add-Check 'disk-space' 'At least 1 GB free on runtime drive?' 'fail' "$freeGB GB free (need at least 1)"
        }
    } else {
        Add-Check 'disk-space' 'At least 1 GB free on runtime drive?' 'warn' 'could not probe drive'
    }
} catch {
    Add-Check 'disk-space' 'At least 1 GB free on runtime drive?' 'warn' $_.Exception.Message
}

# 9. agents.json + pal.json + agents.schema.json parse
$jsonFiles = @(
    @{ Path = (Join-Path $repoRoot 'agents.json');                          Id = 'agents-json' }
    @{ Path = (Join-Path $repoRoot 'pal.json');                             Id = 'pal-json' }
    @{ Path = (Join-Path $repoRoot 'docs/schemas/agents.schema.json');      Id = 'agents-schema' }
)
foreach ($jf in $jsonFiles) {
    if (Test-Path -LiteralPath $jf.Path) {
        try {
            $null = Get-Content -LiteralPath $jf.Path -Raw -Encoding UTF8 | ConvertFrom-Json
            Add-Check $jf.Id "$([IO.Path]::GetFileName($jf.Path)) parses cleanly?" 'pass' ''
        } catch {
            Add-Check $jf.Id "$([IO.Path]::GetFileName($jf.Path)) parses cleanly?" 'fail' $_.Exception.Message
        }
    } else {
        Add-Check $jf.Id "$([IO.Path]::GetFileName($jf.Path)) present?" 'warn' 'missing'
    }
}

# 10. Repo root well-formed
$expected = @('pal.ps1', 'pal.bat', 'src/PalLLM.Sidecar', 'docs/INDEX.md', 'agents.json')
$missing = New-Object System.Collections.ArrayList
foreach ($e in $expected) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $e))) { [void]$missing.Add($e) }
}
if ($missing.Count -eq 0) {
    Add-Check 'repo-root' 'Repo root well-formed?' 'pass' 'all expected files / dirs present'
} else {
    Add-Check 'repo-root' 'Repo root well-formed?' 'fail' "missing: $($missing -join ', ')"
}

# -----------------------------------------------------------------------------
# Verdict
# -----------------------------------------------------------------------------

$failCount = ($rows | Where-Object Status -eq 'fail').Count
$warnCount = ($rows | Where-Object Status -eq 'warn').Count
$passCount = ($rows | Where-Object Status -eq 'pass').Count

$verdict = if ($failCount -gt 0) { 'NOT READY' }
           elseif ($warnCount -gt 0) { 'NEARLY READY' }
           else { 'READY' }

if ($Json.IsPresent) {
    [pscustomobject]@{
        Verdict = $verdict
        Pass    = $passCount
        Warn    = $warnCount
        Fail    = $failCount
        Total   = $rows.Count
        Rows    = $rows
    } | ConvertTo-Json -Depth 4
    return
}

Write-Host ""
Write-Host "PalLLM preflight" -ForegroundColor Cyan
Write-Host ("  base url : {0}" -f $BaseUrl)
Write-Host ""

$maxIdWidth = ($rows | ForEach-Object { $_.Id.Length } | Measure-Object -Maximum).Maximum
foreach ($r in $rows) {
    $tag = switch ($r.Status) { 'pass' { '[PASS]' } 'warn' { '[WARN]' } 'fail' { '[FAIL]' } }
    $color = switch ($r.Status) { 'pass' { 'Green' } 'warn' { 'Yellow' } 'fail' { 'Red' } }
    $idPad = $r.Id.PadRight($maxIdWidth)
    Write-Host ("  {0}  {1}  {2}" -f $tag, $idPad, $r.Question) -ForegroundColor $color
    if ($r.Detail) {
        Write-Host ("           " + (' ' * $maxIdWidth) + "  -> $($r.Detail)") -ForegroundColor DarkGray
    }
}

Write-Host ""
$verdictColor = switch ($verdict) { 'READY' { 'Green' } 'NEARLY READY' { 'Yellow' } 'NOT READY' { 'Red' } }
Write-Host ("Verdict: {0}  ({1} pass / {2} warn / {3} fail)" -f $verdict, $passCount, $warnCount, $failCount) -ForegroundColor $verdictColor
Write-Host ""

if ($verdict -eq 'NOT READY') {
    Write-Host "Next: address the [FAIL] items above. Most have a 'try this next' hint." -ForegroundColor DarkGray
    Write-Host ""
    exit 1
}
if ($verdict -eq 'NEARLY READY') {
    Write-Host "Next: the [WARN] items are non-blocking but lift the score from NEARLY to READY." -ForegroundColor DarkGray
    Write-Host ""
}
