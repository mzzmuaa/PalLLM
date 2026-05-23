<#
.SYNOPSIS
    A quiet, one-line in-character check-in from the companion. The
    "thinking of you" verb. Random selection from a hand-curated
    catalog. No fanfare, no questions, no prompts.

.DESCRIPTION
    Where `pal fortune` has fanfare and date-determinism, `pal whisper`
    is ambient. One quiet line. Different on every call. Designed for
    "I want a small wordless presence" moments - a terminal break, a
    pause between commands, a passing acknowledgment.

    The catalog lives at samples/moments/companion-whispers.json.
    Hand-authored, freely extensible, harvestable.

.PARAMETER Json
    Emit a structured record instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-whisper.ps1
    # One quiet line. Different every call.

.NOTES
    Verb shortcut:  pal whisper

    Companion to:
      pal fortune  - in-character daily fortune (date-seeded)
      pal quest    - micro-quest suggestion
      pal tale     - 3-4-line campfire story
      pal campfire - REPL with all four available as slash commands
#>
[CmdletBinding()]
param(
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot 'samples/moments/companion-whispers.json'
if (-not (Test-Path -LiteralPath $catalogPath)) {
    Write-Error "Whispers catalog missing: $catalogPath"
    exit 1
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$lines = $catalog.whispers
if (-not $lines -or $lines.Count -eq 0) {
    Write-Error "Whispers catalog is empty."
    exit 1
}

$pick = $lines | Get-Random

if ($Json.IsPresent) {
    [pscustomobject]@{
        Whisper = $pick
        Source  = 'samples/moments/companion-whispers.json'
    } | ConvertTo-Json
    return
}

Write-Host ""
Write-Host ("  " + $pick) -ForegroundColor DarkCyan
Write-Host ""
