<#
.SYNOPSIS
    Structured explanation of any file or directory in the repo.
    Designed for an agent (or human) who has just landed on a path
    and needs the "what is this and how does it fit?" answer in one
    payload.

.DESCRIPTION
    Given a file or directory, returns:

      - Kind            (source / doc / script / test / config / sample)
      - Purpose         (extracted from header comments / front matter
                         / AGENT-CARD blocks)
      - Public surface  (top-level types / exported symbols / verb name /
                         endpoint route - whichever applies)
      - Related docs    (cross-referenced from INDEX, CODE_MAP, ADVISORS)
      - Related tests   (heuristic match by symbol / filename)
      - Drift gates     (which gates pin counts that include this file)
      - Sibling files   (immediate neighbours, summarised)

    Output is human-readable text by default, structured JSON with
    -Json. No external services. Pure local search.

.PARAMETER Path
    File or directory path. Required.

.PARAMETER Json
    Emit a structured JSON record instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-explain.ps1 src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs
    # Structured explanation of the deterministic-fallback engine.

.EXAMPLE
    pwsh ./scripts/pal-explain.ps1 docs/READINESS.md -Json | ConvertFrom-Json
    # Machine-readable record for programmatic consumption.

.NOTES
    Verb shortcut:  pal explain <path>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Path,

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

$absolute = if ([IO.Path]::IsPathRooted($Path)) { $Path } else { Join-Path $repoRoot $Path }
$absolute = [IO.Path]::GetFullPath($absolute)
if (-not (Test-Path -LiteralPath $absolute)) {
    Write-Error "Path not found: $absolute"
    exit 1
}

$rel = (Get-RelPath -Root $repoRoot -Full $absolute).Replace('\', '/')
$isDir = (Get-Item -LiteralPath $absolute) -is [IO.DirectoryInfo]
$ext = if ($isDir) { '' } else { [IO.Path]::GetExtension($absolute).ToLowerInvariant() }

# -----------------------------------------------------------------------------
# Classify
# -----------------------------------------------------------------------------

function Get-FileKind {
    param([string]$Rel, [string]$Ext, [bool]$IsDir)
    if ($IsDir) { return 'directory' }
    if ($Rel -match '^docs/adr/') { return 'adr' }
    if ($Rel -like 'docs/*.md' -or $Rel -like '*.md' -or $Rel -like 'docs/**.md') { return 'doc' }
    if ($Rel -like 'tests/*' -and $Ext -eq '.cs') { return 'test' }
    if ($Rel -like 'src/*' -and $Ext -eq '.cs') { return 'source' }
    if ($Rel -like 'scripts/*' -and ($Ext -eq '.ps1' -or $Ext -eq '.cake')) { return 'script' }
    if ($Rel -like 'samples/*') { return 'sample' }
    if ($Rel -like 'docs/schemas/*') { return 'schema' }
    if ($Ext -in @('.json', '.yaml', '.yml')) { return 'config' }
    if ($Ext -eq '.bat') { return 'launcher' }
    if ($Ext -eq '.lua') { return 'lua-bridge' }
    return 'other'
}

$kind = Get-FileKind -Rel $rel -Ext $ext -IsDir $isDir

# -----------------------------------------------------------------------------
# Extract purpose / header / AGENT-CARD
# -----------------------------------------------------------------------------

function Read-Head {
    param([string]$Path, [int]$MaxBytes = 65536)
    try {
        $stream = [IO.File]::OpenRead($Path)
        try {
            $buffer = New-Object byte[] $MaxBytes
            $read = $stream.Read($buffer, 0, $buffer.Length)
        } finally { $stream.Dispose() }
        if ($read -le 0) { return '' }
        return [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
    } catch {
        return ''
    }
}

function Get-Purpose {
    param([string]$Head, [string]$Kind)
    if ([string]::IsNullOrWhiteSpace($Head)) { return $null }

    # 1. AGENT-CARD block
    if ($Head -match '(?s)AGENT-CARD:\s*(.+?)(?:\n\s*\n|\Z)') {
        return ($matches[1] -replace '^\s*[/*#<\->]+\s*', '' -replace "`r", '' -replace "`n\s*[/*#<\->]+\s*", "`n").Trim()
    }
    # 2. C# /// <summary>
    if ($Head -match '(?s)///\s*<summary>(.+?)</summary>') {
        return ($matches[1] -replace '^\s*///\s*', '' -replace "`r", '' -replace "`n\s*///\s*", "`n").Trim()
    }
    # 3. PowerShell .SYNOPSIS
    if ($Head -match '(?s)\.SYNOPSIS\s*\r?\n(.+?)(?:\.\w+\s*\r?\n|#>)') {
        return ($matches[1] -replace '^\s*', '' -replace "`r", '').Trim()
    }
    # 4. Markdown first paragraph after H1
    if ($Kind -eq 'doc' -or $Kind -eq 'adr') {
        if ($Head -match '(?s)^#\s+[^\n]+\n+(?:Last audited:[^\n]*\n+)?(?:>[^\n]*\n+)*([^\n][^\n]+(?:\n[^\n#][^\n]+)*)') {
            return ($matches[1] -replace "`r", '').Trim()
        }
    }
    # 5. Block-comment header
    if ($Head -match '(?s)/\*+\s*(.+?)\s*\*+/') {
        return ($matches[1] -replace "`n\s*\*\s*", "`n").Trim()
    }
    return $null
}

$head = if ($isDir) { '' } else { Read-Head -Path $absolute }
$purpose = Get-Purpose -Head $head -Kind $kind

# -----------------------------------------------------------------------------
# Public surface heuristic
# -----------------------------------------------------------------------------

function Get-PublicSurface {
    param([string]$Head, [string]$Kind, [string]$Path)
    $surface = New-Object System.Collections.ArrayList
    if ($Kind -in @('source', 'test')) {
        foreach ($m in [regex]::Matches($Head, '(?m)^\s*public\s+(?:static\s+)?(?:abstract\s+)?(?:sealed\s+)?(?:partial\s+)?(?:readonly\s+)?(?:record\s+)?(?:class|interface|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)')) {
            [void]$surface.Add(("type: " + $m.Groups[1].Value))
        }
    }
    if ($Kind -eq 'script') {
        if ($Head -match '(?ms)^\s*\[CmdletBinding[^\n]*\nparam\(\s*(.+?)\)') {
            $params = $matches[1]
            foreach ($pm in [regex]::Matches($params, '\$([A-Za-z_][A-Za-z0-9_]*)')) {
                [void]$surface.Add(("param: -" + $pm.Groups[1].Value))
            }
        }
    }
    if ($Kind -eq 'schema') {
        if ($Head -match '"\$id"\s*:\s*"([^"]+)"') {
            [void]$surface.Add("schemaId: $($matches[1])")
        }
        if ($Head -match '"required"\s*:\s*\[\s*([^\]]+)\]') {
            [void]$surface.Add(("required: " + ($matches[1] -replace '\s+', ' ').Trim()))
        }
    }
    return $surface.ToArray()
}

$surface = if ($isDir) { @() } else { Get-PublicSurface -Head $head -Kind $kind -Path $absolute }

# -----------------------------------------------------------------------------
# Related docs / tests / siblings
# -----------------------------------------------------------------------------

$baseName = if ($isDir) { Split-Path $absolute -Leaf } else { [IO.Path]::GetFileNameWithoutExtension($absolute) }

# Related docs: any docs/*.md that mentions this file's relative path or basename.
$relatedDocs = @()
if (-not $isDir) {
    $needle = [regex]::Escape($rel)
    $needle2 = [regex]::Escape($baseName)
    $relatedDocs = Get-ChildItem -Path (Join-Path $repoRoot 'docs') -Filter '*.md' -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $content = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
            $content -and ($content -match $needle -or $content -match $needle2)
        } |
        ForEach-Object { (Get-RelPath -Root $repoRoot -Full $_.FullName).Replace('\', '/') } |
        Select-Object -First 8
}

# Related tests: tests/*.cs that mention the symbol or basename.
$relatedTests = @()
$testsDir = Join-Path $repoRoot 'tests/PalLLM.Tests'
if (-not $isDir -and $kind -eq 'source' -and (Test-Path -LiteralPath $testsDir)) {
    $relatedTests = Get-ChildItem -Path $testsDir -Filter '*.cs' -Recurse -ErrorAction SilentlyContinue |
        Where-Object {
            $content = Get-Content -LiteralPath $_.FullName -Raw -ErrorAction SilentlyContinue
            $content -and $content -match [regex]::Escape($baseName)
        } |
        ForEach-Object { (Get-RelPath -Root $repoRoot -Full $_.FullName).Replace('\', '/') } |
        Select-Object -First 6
}

# Sibling files (in the same directory).
$siblings = @()
$dir = if ($isDir) { $absolute } else { Split-Path -Parent $absolute }
if (Test-Path -LiteralPath $dir) {
    $siblings = Get-ChildItem -Path $dir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $absolute } |
        ForEach-Object { $_.Name } |
        Select-Object -First 12
}

# -----------------------------------------------------------------------------
# Drift gate cross-reference
# -----------------------------------------------------------------------------

$gateHits = New-Object System.Collections.Generic.HashSet[string]
$gateMap = @{
    'src/PalLLM.Sidecar/Program.cs'                                 = 'Drift_Api_route_count, Drift_Api_reference_surface, Drift_OpenApi_snapshot'
    'src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs'             = 'Drift_Feature_catalog_count, Drift_Feature_status_split'
    'src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs'           = 'Drift_Fallback_strategy_count'
    'tests/PalLLM.Tests'                                            = 'Drift_Test_count_docs'
    'docs/openapi/palllm-sidecar-v1.json'                           = 'Drift_OpenApi_snapshot'
}
foreach ($k in $gateMap.Keys) {
    if ($rel -eq $k -or $rel -like ($k + '*')) {
        [void]$gateHits.Add($gateMap[$k])
    }
}

# -----------------------------------------------------------------------------
# Output
# -----------------------------------------------------------------------------

$result = [pscustomobject]@{
    Path           = $rel
    Kind           = $kind
    IsDirectory    = $isDir
    Purpose        = $purpose
    PublicSurface  = $surface
    RelatedDocs    = $relatedDocs
    RelatedTests   = $relatedTests
    Siblings       = $siblings
    DriftGates     = ($gateHits -join '; ')
}

if ($Json.IsPresent) {
    $result | ConvertTo-Json -Depth 6
    return
}

Write-Host ""
Write-Host "pal explain: $rel" -ForegroundColor Cyan
Write-Host ("  kind     : {0}" -f $result.Kind)
if ($result.Purpose) {
    $shortPurpose = if ($result.Purpose.Length -gt 600) { $result.Purpose.Substring(0, 600) + '...' } else { $result.Purpose }
    Write-Host ""
    Write-Host "  purpose  :" -ForegroundColor White
    foreach ($line in ($shortPurpose -split "`n")) {
        Write-Host ("             " + $line.Trim())
    }
}
if ($result.PublicSurface -and $result.PublicSurface.Count -gt 0) {
    Write-Host ""
    Write-Host "  surface  :" -ForegroundColor White
    foreach ($s in $result.PublicSurface) { Write-Host ("             " + $s) }
}
if ($result.DriftGates) {
    Write-Host ""
    Write-Host ("  gates    : {0}" -f $result.DriftGates) -ForegroundColor Yellow
    Write-Host  "             (changing this file probably forces a docs / count bump too)"
}
if ($result.RelatedDocs -and $result.RelatedDocs.Count -gt 0) {
    Write-Host ""
    Write-Host "  related docs:" -ForegroundColor White
    foreach ($d in $result.RelatedDocs) { Write-Host ("             " + $d) }
}
if ($result.RelatedTests -and $result.RelatedTests.Count -gt 0) {
    Write-Host ""
    Write-Host "  related tests:" -ForegroundColor White
    foreach ($t in $result.RelatedTests) { Write-Host ("             " + $t) }
}
if ($result.Siblings -and $result.Siblings.Count -gt 0) {
    Write-Host ""
    Write-Host "  siblings :" -ForegroundColor White
    foreach ($s in $result.Siblings) { Write-Host ("             " + $s) }
}
Write-Host ""
Write-Host "Hint: pal where '<query>'   # find related files by topic" -ForegroundColor DarkGray
Write-Host ""
