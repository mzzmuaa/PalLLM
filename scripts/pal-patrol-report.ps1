<#
.SYNOPSIS
    The companion narrates the night they spent watching while the player
    slept. 4-6 lines per report, atmospheric, in-character. Designed for
    "first login of the session" moments.

.DESCRIPTION
    Random selection from samples/moments/companion-patrols.json. Each
    report has a summary title and 4-6 ordered lines of in-character
    narration. The companion's voice: direct, calm, grounded, with the
    occasional small detail ("the kettle was warm when I woke") that
    makes the world feel lived-in.

    A natural daily ritual companion to:
      pal fortune       in-character daily fortune (date-seeded)
      pal whisper       quiet ambient one-liner
      pal quest         micro-quest suggestion
      pal tale          longer campfire story

    All five surfaces (fortune / whisper / quest / tale / patrol) are
    also one-slash-command away inside `pal campfire` (the patrol-report
    surface is added in the same pass).

.PARAMETER Title
    Optional. Pick a specific report by summary prefix (case-insensitive).
    Without this, picks randomly.

.PARAMETER Json
    Emit a structured record instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-patrol-report.ps1
    # A random patrol report.

.EXAMPLE
    pwsh ./scripts/pal-patrol-report.ps1 -Title "quiet night"
    # That specific report.

.NOTES
    Verb shortcut:  pal patrol-report
#>
[CmdletBinding()]
param(
    [string]$Title,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot 'samples/moments/companion-patrols.json'
if (-not (Test-Path -LiteralPath $catalogPath)) {
    Write-Error "Patrol catalog missing: $catalogPath"
    exit 1
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$reports = $catalog.reports

if (-not [string]::IsNullOrWhiteSpace($Title)) {
    $needle = $Title.ToLowerInvariant()
    $pick = $reports | Where-Object { $_.summary.ToLowerInvariant().StartsWith($needle) -or $_.summary.ToLowerInvariant().Contains($needle) } | Select-Object -First 1
    if (-not $pick) {
        Write-Error "No patrol report matched: $Title"
        Write-Host "Available titles:" -ForegroundColor DarkGray
        foreach ($r in $reports) { Write-Host ("  - " + $r.summary) -ForegroundColor DarkGray }
        exit 1
    }
} else {
    $pick = $reports | Get-Random
}

if ($Json.IsPresent) {
    [pscustomobject]@{
        Summary = $pick.summary
        Lines   = $pick.lines
        Source  = 'samples/moments/companion-patrols.json'
    } | ConvertTo-Json -Depth 4
    return
}

Write-Host ""
Write-Host ("  ~~~ patrol report: {0} ~~~" -f $pick.summary) -ForegroundColor Magenta
Write-Host ""
foreach ($line in $pick.lines) {
    Write-Host ("  " + $line) -ForegroundColor Cyan
}
Write-Host ""
