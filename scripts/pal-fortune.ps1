<#
.SYNOPSIS
    A small, in-character daily fortune from a hand-curated catalog.
    Lighthearted, never genuinely predictive. Same answer all day,
    different the next day.

.DESCRIPTION
    Date-seeded selector over a hand-authored fortune catalog. The
    fortunes are written in the companion's deterministic-fallback
    voice - direct, calm, not theatrical. Designed to be the kind
    of thing you put on your terminal login script, or pull up when
    you sit down at the desk.

    Three reasons this is hand-curated and not generated:
      1. Generated fortunes drift in tone within a single session.
         Hand-curated ones don't.
      2. The catalog is harvestable - any other local-AI app can
         lift the JSON file and ship its own fortune verb.
      3. Lighthearted hand-written voice ages better than any model.

    The catalog lives at samples/moments/companion-fortunes.json
    and is freely extensible. Each fortune is a one-line string;
    the file is a JSON array. Pack authors who want their persona
    to have its own fortunes can ship a per-pack overrides file.

.PARAMETER Date
    Override the date used for seed (yyyy-MM-dd). Default: today.
    Useful for verifying the rotation across a week.

.PARAMETER Category
    Filter to a single category. One of: any (default), morning,
    field, base, reflection.

.PARAMETER Json
    Emit a structured record instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-fortune.ps1
    # Today's fortune. Same one all day.

.EXAMPLE
    pwsh ./scripts/pal-fortune.ps1 -Date 2026-12-25
    # Whatever fortune that day will land on.

.EXAMPLE
    pwsh ./scripts/pal-fortune.ps1 -Category reflection -Json
    # Structured record, filtered to the reflective fortunes.

.NOTES
    Verb shortcut:  pal fortune

    The catalog is plain JSON; pull-requests welcome under
    samples/moments/companion-fortunes.json. Keep additions short
    (under 120 characters), in-character (not generic horoscopes),
    and never predictive in a way that could feel wrong.
#>
[CmdletBinding()]
param(
    [string]$Date,
    [ValidateSet('any', 'morning', 'field', 'base', 'reflection')]
    [string]$Category = 'any',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot 'samples/moments/companion-fortunes.json'
if (-not (Test-Path -LiteralPath $catalogPath)) {
    Write-Error "Fortune catalog missing: $catalogPath"
    exit 1
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$fortunes = $catalog.fortunes
if ($Category -ne 'any') {
    $fortunes = $fortunes | Where-Object { $_.category -eq $Category }
}
if (-not $fortunes -or $fortunes.Count -eq 0) {
    Write-Error "No fortunes found for category: $Category"
    exit 1
}

# Deterministic date-based seed: same day -> same fortune; midnight rolls over.
$dt = if ([string]::IsNullOrWhiteSpace($Date)) { Get-Date } else { [DateTime]::ParseExact($Date, 'yyyy-MM-dd', $null) }
$seed = (([int]$dt.Year * 366) + $dt.DayOfYear) % $fortunes.Count
$pick = $fortunes[$seed]

if ($Json.IsPresent) {
    [pscustomobject]@{
        Date     = $dt.ToString('yyyy-MM-dd')
        Category = $pick.category
        Fortune  = $pick.text
        Source   = 'samples/moments/companion-fortunes.json'
    } | ConvertTo-Json
    return
}

Write-Host ""
Write-Host "  ~~~ today's word from the fire ~~~" -ForegroundColor Magenta
Write-Host ""
Write-Host ("  " + $pick.text) -ForegroundColor Cyan
Write-Host ""
Write-Host ("  ($($dt.ToString('yyyy-MM-dd')) - $($pick.category))") -ForegroundColor DarkGray
Write-Host ""
