<#
.SYNOPSIS
    Print the most recent CHANGELOG entry. Pairs with `pal check-updates`
    so the operator who just learned a new release exists can see what's
    in it without leaving the terminal.

.DESCRIPTION
    Reads CHANGELOG.md and prints the most recent dated entry (default)
    or the last N entries (with -Count). The format follows
    Keep a Changelog: every entry header looks like

        ### Pass NNN - Title (yyyy-MM-dd)

    The script greps for that pattern and slices out the entry body
    between the header and the next entry header. Pure local read, no
    network call.

    Companion verbs:
        pal check-updates    queries GitHub Releases (opt-in network)
        pal news             prints the local CHANGELOG entry (offline)

.PARAMETER Count
    Number of entries to print. Default 1 (the most recent only).

.PARAMETER Pass
    Print a specific pass by its number (e.g. -Pass 100). Overrides
    -Count.

.PARAMETER Json
    Emit a structured record (id / title / date / body) instead of
    pretty text.

.EXAMPLE
    pwsh ./scripts/pal-news.ps1
    # Most recent CHANGELOG entry.

.EXAMPLE
    pwsh ./scripts/pal-news.ps1 -Count 3
    # The three most recent entries.

.EXAMPLE
    pwsh ./scripts/pal-news.ps1 -Pass 100 -Json
    # The "Pass 100" entry as JSON.

.NOTES
    Verb shortcut:  pal news

    The CHANGELOG is the source of truth; this verb just renders it.
    For supply-chain style "what's in this release zip?" surface
    use the RELEASE_PACKAGE_MANIFEST.json shipped with each release.
#>
[CmdletBinding()]
param(
    [int]$Count = 1,
    [int]$Pass,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$changelog = Join-Path $repoRoot 'CHANGELOG.md'
if (-not (Test-Path -LiteralPath $changelog)) {
    Write-Error "CHANGELOG.md not found: $changelog"
    exit 1
}

$lines = Get-Content -LiteralPath $changelog -Encoding UTF8
$entries = New-Object System.Collections.ArrayList
$current = $null
$bodyLines = $null

foreach ($line in $lines) {
    if ($line -match '^###\s+Pass\s+(\d+)\s*[-–—]\s*(.+?)\s*\((\d{4}-\d{2}-\d{2})\)') {
        if ($current) {
            $current.Body = ($bodyLines -join "`n").TrimEnd()
            [void]$entries.Add($current)
        }
        $current = [pscustomobject]@{
            Pass  = [int]$matches[1]
            Title = $matches[2].Trim()
            Date  = $matches[3]
            Body  = ''
        }
        $bodyLines = New-Object System.Collections.ArrayList
        continue
    }
    if ($current) { [void]$bodyLines.Add($line) }
}
if ($current) {
    $current.Body = ($bodyLines -join "`n").TrimEnd()
    [void]$entries.Add($current)
}

if ($entries.Count -eq 0) {
    Write-Error "No '### Pass NNN - Title (yyyy-MM-dd)' entries found in CHANGELOG.md."
    exit 1
}

# Sort newest-first by date (lexicographic works for yyyy-MM-dd) then by pass number.
$ordered = $entries | Sort-Object -Property @{Expression='Date';Descending=$true}, @{Expression='Pass';Descending=$true}

if ($PSBoundParameters.ContainsKey('Pass')) {
    $picked = $ordered | Where-Object { $_.Pass -eq $Pass } | Select-Object -First 1
    if (-not $picked) {
        Write-Error "No entry found for Pass $Pass."
        exit 1
    }
    $output = @($picked)
} else {
    if ($Count -lt 1) { $Count = 1 }
    $output = @($ordered | Select-Object -First $Count)
}

if ($Json.IsPresent) {
    $output | ConvertTo-Json -Depth 6
    return
}

Write-Host ""
foreach ($e in $output) {
    Write-Host ("  ~~~ Pass {0}  {1}  ({2}) ~~~" -f $e.Pass, $e.Title, $e.Date) -ForegroundColor Magenta
    Write-Host ""
    foreach ($bl in ($e.Body -split "`n")) {
        Write-Host $bl
    }
    Write-Host ""
}
Write-Host "Full log: CHANGELOG.md" -ForegroundColor DarkGray
Write-Host ""
