<#
.SYNOPSIS
    The companion tells a 3-4 line campfire story. Atmospheric, brief,
    in-character. Hand-curated catalog of 12 yarns.

.DESCRIPTION
    Random selection from samples/moments/companion-tales.json. Each
    tale has a title and an ordered list of lines. The companion
    delivers the lines in sequence with small visual pacing.

    Designed for end-of-session moments: you've played for a while,
    you're not ready to log off but you're done playing actively, you
    want a beat of texture. `pal tale` is that beat.

.PARAMETER Title
    Optional. Pick a specific tale by title (case-insensitive prefix
    match). Without this, picks randomly.

.PARAMETER Json
    Emit a structured record instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-tale.ps1
    # A random campfire story.

.EXAMPLE
    pwsh ./scripts/pal-tale.ps1 -Title 'small good day'
    # That specific tale.

.NOTES
    Verb shortcut:  pal tale

    Companion to:
      pal whisper  - quiet one-line check-in
      pal fortune  - in-character daily fortune
      pal quest    - micro-quest suggestion
      pal campfire - REPL with all four available as slash commands
#>
[CmdletBinding()]
param(
    [string]$Title,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $repoRoot 'samples/moments/companion-tales.json'
if (-not (Test-Path -LiteralPath $catalogPath)) {
    Write-Error "Tales catalog missing: $catalogPath"
    exit 1
}

$catalog = Get-Content -LiteralPath $catalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
$tales = $catalog.tales

if (-not [string]::IsNullOrWhiteSpace($Title)) {
    $needle = $Title.ToLowerInvariant()
    $pick = $tales | Where-Object { $_.title.ToLowerInvariant().StartsWith($needle) } | Select-Object -First 1
    if (-not $pick) {
        Write-Error "No tale matched title prefix: $Title"
        Write-Host "Available titles:" -ForegroundColor DarkGray
        foreach ($t in $tales) { Write-Host ("  - " + $t.title) -ForegroundColor DarkGray }
        exit 1
    }
} else {
    $pick = $tales | Get-Random
}

if ($Json.IsPresent) {
    [pscustomobject]@{
        Title  = $pick.title
        Lines  = $pick.lines
        Source = 'samples/moments/companion-tales.json'
    } | ConvertTo-Json -Depth 4
    return
}

Write-Host ""
Write-Host ("  ~~~ {0} ~~~" -f $pick.title) -ForegroundColor Magenta
Write-Host ""
foreach ($line in $pick.lines) {
    Write-Host ("  " + $line) -ForegroundColor Cyan
}
Write-Host ""
