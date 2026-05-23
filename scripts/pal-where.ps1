<#
.SYNOPSIS
    Natural-language query -> ranked file paths. The "where do I add /
    find / change X?" verb for agents and humans skimming the repo.

.DESCRIPTION
    Takes a free-text query and returns a ranked list of files most
    likely to be relevant. No external services - pure local search
    against:

      1. The CODE_MAP.md "where does X live?" rows.
      2. File names + path components.
      3. Header comments at the top of source files.
      4. Per-file AGENT-CARD blocks (Pass 102+).

    Scoring is keyword-overlap with bonuses for: filename match, doc
    reference match, AGENT-CARD match, and exact-phrase match. Runs
    in well under a second on the full repo.

    Designed to be read by an agent: the output is plain text by
    default, but -Json emits structured records for programmatic
    consumption.

.PARAMETER Query
    The natural-language query. Required. Example: 'chat hot path',
    'fallback strategies', 'where do MCP tools live'.

.PARAMETER Limit
    Maximum results to return. Default 10.

.PARAMETER Json
    Emit a JSON array of ranked records instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-where.ps1 'fallback strategies'
    # Returns the file that owns the 19 deterministic strategies plus
    # the doc that explains the design.

.EXAMPLE
    pwsh ./scripts/pal-where.ps1 'where do MCP tools live' -Limit 5
    # Top-5 ranked files relevant to MCP tool authoring.

.EXAMPLE
    pwsh ./scripts/pal-where.ps1 'pack content hash' -Json | ConvertFrom-Json | Format-Table
    # Structured output for machine consumption.

.NOTES
    Verb shortcut:  pal where '<query>'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$QueryParts,

    [int]$Limit = 10,

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Polyfill: Windows PowerShell 5.1 doesn't have [IO.Path]::GetRelativePath.
function Get-RelPath {
    param([string]$Root, [string]$Full)
    $rootFull = [IO.Path]::GetFullPath($Root)
    $candidateFull = [IO.Path]::GetFullPath($Full)
    if (-not $rootFull.EndsWith([IO.Path]::DirectorySeparatorChar)) {
        $rootFull = $rootFull + [IO.Path]::DirectorySeparatorChar
    }
    if ($candidateFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        return $candidateFull.Substring($rootFull.Length)
    }
    return $candidateFull
}

$Query = ($QueryParts -join ' ').Trim()
if ([string]::IsNullOrWhiteSpace($Query)) {
    Write-Error "Query is required. Example: pal where 'chat hot path'"
    exit 1
}

# -----------------------------------------------------------------------------
# Scoring
# -----------------------------------------------------------------------------

$queryLower = $Query.ToLowerInvariant()
$queryTokens = ($queryLower -split '[\s\-_./,]+') | Where-Object { $_ -and $_.Length -gt 2 }
$stopwords = @('the', 'and', 'where', 'does', 'what', 'how', 'this', 'that',
               'with', 'from', 'into', 'when', 'live', 'lives', 'do', 'is')
$tokens = @($queryTokens | Where-Object { $stopwords -notcontains $_ })
if (-not $tokens -or $tokens.Count -eq 0) {
    # Fall back to using the entire query as a single token if every
    # term was a stopword - prevents zero-result responses.
    $tokens = @($queryLower)
}

# Build the file pool. Limit to text-likely surfaces; ignore obj/, bin/,
# release/, .git/, artifacts/, node_modules/, sidecar/publish/ etc.
$ignoreSegments = @('obj', 'bin', 'release', 'artifacts', 'sidecar/publish',
                    'node_modules', '.git', '.github/workflows/_cache',
                    'TestResults', 'coverage')
$includeExtensions = @('.cs', '.md', '.json', '.ps1', '.bat', '.lua', '.txt', '.yaml', '.yml', '.cake')

function Test-IsIgnoredPath {
    param([string]$Path)
    $rel = (Get-RelPath -Root $repoRoot -Full $Path).Replace('\', '/')
    foreach ($seg in $ignoreSegments) {
        if ($rel -like "$seg/*" -or $rel -like "*/$seg/*") { return $true }
    }
    return $false
}

$candidates = Get-ChildItem -Path $repoRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object {
        $includeExtensions -contains $_.Extension.ToLowerInvariant() -and
        -not (Test-IsIgnoredPath $_.FullName)
    }

$results = New-Object System.Collections.ArrayList

foreach ($file in $candidates) {
    $rel = (Get-RelPath -Root $repoRoot -Full $file.FullName).Replace('\', '/')
    $relLower = $rel.ToLowerInvariant()

    $score = 0
    $matchedTerms = New-Object System.Collections.Generic.HashSet[string]

    # Filename + path matches are the strongest signal. The exact basename
    # match is weighted highest; a path-segment match is next; a substring
    # match anywhere in the relative path is the weakest of the three.
    $baseLower = [IO.Path]::GetFileNameWithoutExtension($file.Name).ToLowerInvariant()
    foreach ($t in $tokens) {
        $hit = $false
        if ($baseLower -eq $t -or $baseLower -match ('(^|[^a-z])' + [regex]::Escape($t) + '($|[^a-z])')) {
            $score += 30
            $hit = $true
        }
        elseif ($relLower -match [regex]::Escape($t)) {
            $score += 12
            $hit = $true
        }
        if ($hit) { [void]$matchedTerms.Add($t) }
    }

    # Boost: source code under src/ usually answers "where does X live?"
    # better than any doc that just mentions X.
    if ($relLower -like 'src/*' -and $matchedTerms.Count -gt 0) { $score += 8 }
    # Boost: docs/CODE_MAP.md is the canonical "where does X live?" map.
    if ($rel -eq 'docs/CODE_MAP.md' -and $matchedTerms.Count -gt 0) { $score += 6 }

    # Read up to the first 64 KiB - more than enough for header comments
    # and the AGENT-CARD block, capped so the search stays under a second.
    try {
        $stream = [IO.File]::OpenRead($file.FullName)
        try {
            $buffer = New-Object byte[] (64 * 1024)
            $read = $stream.Read($buffer, 0, $buffer.Length)
        }
        finally { $stream.Dispose() }
    }
    catch {
        continue
    }
    if ($read -le 0) { continue }
    $head = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read).ToLowerInvariant()

    # AGENT-CARD blocks are an explicit signal - bonus weight.
    $hasAgentCard = $head -match 'agent-card'
    foreach ($t in $tokens) {
        $occurrences = ([regex]::Matches($head, [regex]::Escape($t))).Count
        if ($occurrences -gt 0) {
            $score += [Math]::Min($occurrences, 5)  # cap to avoid keyword stuffing
            [void]$matchedTerms.Add($t)
            if ($hasAgentCard -and ($head -match "agent-card[^`n]{0,2000}" + [regex]::Escape($t))) {
                $score += 4  # AGENT-CARD-block keyword match
            }
        }
    }

    # Exact-phrase bonus
    if ($head -match [regex]::Escape($queryLower)) {
        $score += 8
    }

    if ($score -gt 0) {
        # Read first non-empty title-ish line for the result preview.
        $previewLine = $null
        foreach ($line in ($head -split "`n")) {
            $stripped = $line.Trim()
            if ($stripped -and $stripped -notmatch '^[/<>{};\[\]\-\*\#]+$' -and $stripped.Length -gt 4) {
                $previewLine = $stripped.TrimStart('/', '*', '#', '<', '!', ' ').TrimEnd('-', '*').Trim()
                if ($previewLine.Length -gt 100) { $previewLine = $previewLine.Substring(0, 97) + '...' }
                break
            }
        }

        [void]$results.Add([pscustomobject]@{
            Path = $rel
            Score = $score
            Matched = ($matchedTerms -join ', ')
            Preview = $previewLine
            HasAgentCard = $hasAgentCard
        })
    }
}

$ranked = $results | Sort-Object -Property @{ Expression = 'Score'; Descending = $true }, Path | Select-Object -First $Limit

if ($Json.IsPresent) {
    $ranked | ConvertTo-Json -Depth 4
    return
}

Write-Host ""
Write-Host "pal where: '$Query'" -ForegroundColor Cyan
if (-not $ranked -or $ranked.Count -eq 0) {
    Write-Host ""
    Write-Host "No matching files found. Try a different query, or:" -ForegroundColor Yellow
    Write-Host "  pal explain <known-path>   # structured explanation of a file"
    Write-Host "  cat docs/INDEX.md          # full doc map"
    Write-Host "  cat docs/CODE_MAP.md       # symbol-to-file index"
    Write-Host ""
    return
}

$maxPathWidth = ($ranked | ForEach-Object { $_.Path.Length } | Measure-Object -Maximum).Maximum
foreach ($r in $ranked) {
    $tag = if ($r.HasAgentCard) { ' [AGENT-CARD]' } else { '' }
    $padded = $r.Path.PadRight($maxPathWidth)
    Write-Host ("  {0}  score={1,3}{2}" -f $padded, $r.Score, $tag) -ForegroundColor White
    if ($r.Preview) {
        Write-Host ("    {0}" -f $r.Preview) -ForegroundColor DarkGray
    }
}
Write-Host ""
Write-Host "Hint: pal explain <path>   # for a structured deep-dive on one of these" -ForegroundColor DarkGray
Write-Host ""
