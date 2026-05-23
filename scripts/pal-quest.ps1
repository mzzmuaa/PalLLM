<#
.SYNOPSIS
    The companion suggests a small, specific, ~30-minute self-contained
    challenge. Hand-curated catalog of micro-quests. Lighthearted - the
    companion is suggesting, not assigning.

.DESCRIPTION
    Random selection from samples/moments/companion-quests.json.
    Each quest has a tier (easy / medium / spicy / quiet) and a one-line
    summary. Filter by tier with -Tier; default 'any'.

    Quests are not progression-blocking. They're prompts for "I sat
    down to play but don't know what to do today" moments. Specific
    enough to be fun, open enough to fail gracefully without a sense
    of failure.

    The 'quiet' tier is for sessions where the player wants something
    soft - sort the storage chest, sit at the fire, walk a known route
    slowly. Counted as a real session, not a chore.

.PARAMETER Tier
    Filter to a single difficulty tier. One of: any (default), easy,
    medium, spicy, quiet.

.PARAMETER Json
    Emit a structured record instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-quest.ps1
    # One random quest, any tier.

.EXAMPLE
    pwsh ./scripts/pal-quest.ps1 -Tier quiet
    # A low-key 'today I rest' quest.

.NOTES
    Verb shortcut:  pal quest [-Tier <tier>]

    Companion to:
      pal whisper  - quiet one-line check-in
      pal fortune  - in-character daily fortune
      pal tale     - 3-4-line campfire story
      pal campfire - REPL with all four available as slash commands
#>
[CmdletBinding()]
param(
    [ValidateSet('any', 'easy', 'medium', 'spicy', 'quiet')]
    [string]$Tier = 'any',

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot 'samples/moments/companion-quests.json'
if (-not (Test-Path -LiteralPath $catalogPath)) {
    Write-Error "Quests catalog missing: $catalogPath"
    exit 1
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$pool = $catalog.quests
if ($Tier -ne 'any') {
    $pool = $pool | Where-Object { $_.tier -eq $Tier }
}
if (-not $pool -or $pool.Count -eq 0) {
    Write-Error "No quests found for tier: $Tier"
    exit 1
}

$pick = $pool | Get-Random

if ($Json.IsPresent) {
    [pscustomobject]@{
        Tier    = $pick.tier
        Quest   = $pick.summary
        Source  = 'samples/moments/companion-quests.json'
    } | ConvertTo-Json
    return
}

Write-Host ""
Write-Host "  ~~~ today, if you'd like one ~~~" -ForegroundColor Magenta
Write-Host ""
Write-Host ("  " + $pick.summary) -ForegroundColor Cyan
Write-Host ""
Write-Host ("  ($($pick.tier) tier)") -ForegroundColor DarkGray
Write-Host ""
