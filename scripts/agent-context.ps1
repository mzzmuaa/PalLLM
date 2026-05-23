<#
.SYNOPSIS
    Emits a single JSON document an AI agent (or any caller) can
    parse to learn the current PalLLM state in one read.

.DESCRIPTION
    Loads docs/PROJECT_NUMBERS.json (the rolling counts), the
    latest audit report, and the `Last audited:` stamps from
    every doc that carries one. Prints a single JSON document
    to stdout designed to be the first thing an agent reads
    when picking up a coding session.

    Designed to be cheap (<2 s) and side-effect-free. No
    network calls, no writes.

.PARAMETER Pretty
    Pretty-print the JSON output. Off by default to keep the
    output compact for piping into other tools.

.EXAMPLE
    powershell -File scripts/agent-context.ps1
    # Compact JSON suitable for piping into jq or another parser.

.EXAMPLE
    powershell -File scripts/agent-context.ps1 -Pretty | Out-File context.json
    # Human-readable JSON written to a file.

.NOTES
    Pairs with `pal.ps1 context` (the verb shortcut) and
    `docs/PROJECT_NUMBERS.json` (the curated counts).
    See `docs/MENTAL_MODEL.md` for the conceptual scaffolding
    an agent should also read.
#>
[CmdletBinding()]
param(
    [switch]$Pretty
)

$ErrorActionPreference = "Stop"

$repoRoot     = Resolve-Path (Join-Path $PSScriptRoot "..")
$numbersPath  = Join-Path $repoRoot "docs/PROJECT_NUMBERS.json"
$auditDir     = Join-Path $repoRoot "artifacts/full-audit"
$adrDir       = Join-Path $repoRoot "docs/adr"
$schemaDir    = Join-Path $repoRoot "docs/schemas"

# -- Numbers ---------------------------------------------------------------
$numbers = $null
if (Test-Path $numbersPath) {
    try {
        $numbers = Get-Content $numbersPath -Raw | ConvertFrom-Json
    } catch {
        Write-Error "PROJECT_NUMBERS.json is not valid JSON: $_"
        exit 1
    }
}

# -- Latest audit ----------------------------------------------------------
$latestAudit = $null
if (Test-Path $auditDir) {
    $latest = Get-ChildItem $auditDir -Directory |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($latest) {
        $resultsFile = Join-Path $latest.FullName "RESULTS.md"
        if (Test-Path $resultsFile) {
            $overall = (Select-String -Path $resultsFile -Pattern '^- Overall: \*\*(.+?)\*\*' |
                Select-Object -First 1)
            $overallText = if ($overall) { $overall.Matches.Groups[1].Value } else { 'UNKNOWN' }
            $latestAudit = [pscustomobject]@{
                timestamp = $latest.Name
                overall   = $overallText
                report    = (Resolve-Path $resultsFile).Path.Replace('\', '/')
            }
        }
    }
}

# -- ADRs ------------------------------------------------------------------
$adrs = @()
if (Test-Path $adrDir) {
    Get-ChildItem $adrDir -Filter '*.md' |
        Where-Object { $_.Name -ne 'README.md' } |
        Sort-Object Name |
        ForEach-Object {
            # Read first line for title; scan body for status.
            $firstLine = (Get-Content $_.FullName -TotalCount 1)
            $content   = Get-Content $_.FullName -Raw
            $number = $null; $title = $null; $status = 'unknown'
            if ($firstLine -match '^# ADR (\d+)\s+\S+\s+(.+)$') {
                $number = $Matches[1]
                $title  = $Matches[2].Trim()
            }
            if ($content -match '\*\*Status:\*\*\s+(\w+)') {
                $status = $Matches[1].Trim()
            }
            if ($number -and $title) {
                $adrs += [pscustomobject]@{
                    number = $number
                    title  = $title
                    status = $status
                    file   = "docs/adr/$($_.Name)"
                }
            }
        }
}

# -- Schemas ---------------------------------------------------------------
$schemas = @()
if (Test-Path $schemaDir) {
    Get-ChildItem $schemaDir -Filter '*.schema.json' |
        Sort-Object Name |
        ForEach-Object {
            try {
                $schema = Get-Content $_.FullName -Raw | ConvertFrom-Json
                $schemas += [pscustomobject]@{
                    file        = "docs/schemas/$($_.Name)"
                    title       = $schema.title
                    description = $schema.description
                    id          = $schema.'$id'
                }
            } catch {
                # Skip unparseable schemas; they'll fail their own validators
            }
        }
}

# -- Doc freshness map -----------------------------------------------------
$docFreshness = @()
$threshold = 45  # days — matches Drift_Doc_freshness gate
$now = Get-Date
foreach ($docFile in (Get-ChildItem (Join-Path $repoRoot 'docs') -Filter '*.md' -Recurse)) {
    $content = Get-Content $docFile.FullName -Raw
    $stampMatch = $content -match 'Last audited:\s+`(\d{4}-\d{2}-\d{2})`'
    if ($stampMatch) {
        try {
            $stamp = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd', $null)
            $ageDays = [math]::Round(($now - $stamp).TotalDays)
            $rel = $docFile.FullName.Replace($repoRoot.ToString(), '').TrimStart('\', '/').Replace('\', '/')
            $docFreshness += [pscustomobject]@{
                doc     = $rel
                stamp   = $Matches[1]
                ageDays = $ageDays
                stale   = ($ageDays -gt $threshold)
            }
        } catch {
            # Skip invalid stamps
        }
    }
}

# -- Compose ---------------------------------------------------------------
$context = [pscustomobject]@{
    '$schema'      = 'https://palllm.dev/schemas/agent-context-v1.schema.json'
    '$comment'     = 'Single-shot snapshot for AI agents picking up a PalLLM session. Read this first; then docs/MENTAL_MODEL.md, then docs/HANDOFF.md.'
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    repoRoot       = $repoRoot.ToString().Replace('\', '/')
    numbers        = $numbers
    latestAudit    = $latestAudit
    adrs           = $adrs
    schemas        = $schemas
    docFreshness   = [pscustomobject]@{
        thresholdDays = $threshold
        stamps        = $docFreshness
        staleCount    = ($docFreshness | Where-Object { $_.stale }).Count
    }
    quickReadingOrder = @(
        'AGENTS.md',
        'docs/MENTAL_MODEL.md',
        'docs/HANDOFF.md',
        'docs/CHEAT_SHEET.md',
        'docs/QUICKREF.md'
    )
    quickCommands = @{
        onboard   = 'pwsh ./pal.ps1 onboard'
        build     = 'pwsh ./pal.ps1 build'
        test      = 'pwsh ./pal.ps1 test'
        audit     = 'pwsh ./pal.ps1 audit'
        status    = 'pwsh ./pal.ps1 status'
        play      = 'pwsh ./pal.ps1 play'
    }
}

# -- Emit ------------------------------------------------------------------
if ($Pretty) {
    $context | ConvertTo-Json -Depth 10
} else {
    $context | ConvertTo-Json -Depth 10 -Compress
}
