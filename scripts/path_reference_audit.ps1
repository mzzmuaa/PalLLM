[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$WriteReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $scriptPath = if (-not [string]::IsNullOrWhiteSpace($PSCommandPath)) {
        $PSCommandPath
    } else {
        $MyInvocation.MyCommand.Path
    }

    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw "Could not resolve script path to infer RepoRoot."
    }

    $RepoRoot = Split-Path -Parent (Split-Path -Parent $scriptPath)
}

$repoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

$scanRoots = @(
    "README.md",
    "CHANGELOG.md",
    "CONTRIBUTING.md",
    "NOTICE.md",
    "SECURITY.md",
    "THIRD_PARTY_NOTICES.md",
    ".github",
    "docs",
    "scripts"
)

$scanExtensions = @(
    ".md",
    ".txt",
    ".ps1",
    ".bat",
    ".sh",
    ".yml",
    ".yaml",
    ".json"
)

$repoPrefixes = @(
    ".github/",
    "CHANGELOG.md",
    "CONTRIBUTING.md",
    "LICENSE",
    "NOTICE.md",
    "README.md",
    "SECURITY.md",
    "THIRD_PARTY_NOTICES.md",
    "docs/",
    "mod/",
    "scripts/",
    "src/",
    "tests/",
    "PalLLM.sln"
)

$ignoredSubstrings = @(
    "://",
    "mailto:",
    "plugin://",
    "app://",
    "vscode://",
    "file://"
)

$ignoredPrefixes = @(
    "artifacts/",
    "bin/",
    "obj/"
)

$knownExtensionlessNames = @(
    "LICENSE",
    "NOTICE",
    "Dockerfile",
    "Makefile"
)

$quotedTokenPattern = @'
`([^`
]+)`|"([^"]+)"|'([^']+)'
'@

$markdownLinkPattern = '\[[^\]]+\]\(([^)]+)\)'
# Pass 372: also strip `:26,31` (comma-separated line+column) and
# `:26:5` (compiler-style line:column) tails in addition to the older
# `:26` and `:26-31` forms.
$lineRefPattern = '([.][A-Za-z0-9]{1,8}):\d+(?:[-,:]\d+)?$'
$ignoredCharPattern = '[*?\[\]{}<>|$]'

function Get-ScanFiles {
    param([string]$RootPath)

    $files = New-Object System.Collections.Generic.List[string]
    foreach ($relative in $scanRoots) {
        $candidate = Join-Path $RootPath $relative
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $files.Add((Resolve-Path -LiteralPath $candidate).Path) | Out-Null
            continue
        }

        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) {
            continue
        }

        $matched = Get-ChildItem -LiteralPath $candidate -Recurse -File |
            Where-Object { $scanExtensions -contains $_.Extension.ToLowerInvariant() } |
            Sort-Object FullName

        foreach ($file in $matched) {
            $files.Add($file.FullName) | Out-Null
        }
    }

    return $files
}

function Get-RelativeUnixPath {
    param(
        [string]$BasePath,
        [string]$FullPath
    )

    $relative = $FullPath.Substring($BasePath.Length).TrimStart('\', '/')
    return ($relative -replace '\\', '/')
}

function Normalize-Candidate {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return ""
    }

    $text = $Candidate.Trim().Trim("`"'")
    $text = $text.TrimEnd('.', ',', ':', ';', ')', '`')
    $text = $text.Trim("`"'")
    $text = ($text -split '#', 2)[0]
    $text = [regex]::Replace($text, $lineRefPattern, '$1')
    $text = $text -replace '\\', '/'
    $text = [regex]::Replace($text, '/+', '/')
    return $text
}

function Convert-ToOsRelativePath {
    param([string]$RelativePath)

    $parts = $RelativePath -split '[\\/]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    return [string]::Join([System.IO.Path]::DirectorySeparatorChar, $parts)
}

function Test-MatchesRepoPrefix {
    param([string]$Candidate)

    foreach ($prefix in $repoPrefixes) {
        if ($prefix.EndsWith('/')) {
            if ($Candidate.StartsWith($prefix, [System.StringComparison]::Ordinal)) {
                return $true
            }

            continue
        }

        if ($Candidate.Equals($prefix, [System.StringComparison]::Ordinal)) {
            return $true
        }
    }

    return $false
}

function Test-LooksLikeRepoPath {
    param([string]$Candidate)

    $normalized = Normalize-Candidate $Candidate
    if ([string]::IsNullOrWhiteSpace($normalized)) {
        return $false
    }

    foreach ($substring in $ignoredSubstrings) {
        if ($normalized.Contains($substring)) {
            return $false
        }
    }

    if ($normalized -match '/\.') {
        return $false
    }

    if ($normalized.Length -gt 240) {
        return $false
    }

    if ([regex]::IsMatch($normalized, $ignoredCharPattern)) {
        return $false
    }

    foreach ($prefix in $ignoredPrefixes) {
        if ($normalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    if ($normalized.StartsWith("--", [System.StringComparison]::Ordinal)) {
        return $false
    }

    $isWindowsAbsolute = [regex]::IsMatch($normalized, '^[A-Za-z]:/')
    $isUnixAbsolute = $normalized.StartsWith("/", [System.StringComparison]::Ordinal)

    if (-not $isWindowsAbsolute -and -not $isUnixAbsolute) {
        if (-not (Test-MatchesRepoPrefix -Candidate $normalized)) {
            return $false
        }
    }

    if ($normalized.EndsWith('/')) {
        return $true
    }

    $leaf = [System.IO.Path]::GetFileName($normalized)
    if ($knownExtensionlessNames -contains $leaf) {
        return $true
    }

    if ($leaf.Contains('.')) {
        return $true
    }

    return $normalized.Contains('/')
}

function Resolve-RepoPath {
    param(
        [string]$RootPath,
        [string]$Candidate
    )

    $normalized = Normalize-Candidate $Candidate
    foreach ($prefix in $ignoredPrefixes) {
        if ($normalized.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
    }

    if ([regex]::IsMatch($normalized, '^[A-Za-z]:/')) {
        # Pass 372 (CI parity): a Windows drive-letter prefix means
        # "absolute path on the operator's machine, not a repo-relative
        # reference." On Windows, Path.GetFullPath honours the prefix
        # and the StartsWith check below correctly excludes it. On
        # Linux, Path.GetFullPath treats `G:/SteamLibrary` as a
        # bare relative segment and resolves it against the runner's
        # cwd (`/home/runner/work/PalLLM/PalLLM/G:/SteamLibrary`),
        # which then matches RootPath and the audit yells about the
        # missing file. Skip drive-letter references unconditionally
        # — they are never repo-local by construction.
        return $null
    }

    if ($normalized.StartsWith("/", [System.StringComparison]::Ordinal)) {
        try {
            $resolved = [System.IO.Path]::GetFullPath($normalized)
        } catch {
            return $null
        }
        if (-not $resolved.StartsWith($RootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }

        return $resolved
    }

    if (-not (Test-MatchesRepoPrefix -Candidate $normalized)) {
        return $null
    }

    $relativePath = Convert-ToOsRelativePath ($normalized.TrimEnd('/'))
    try {
        return [System.IO.Path]::GetFullPath((Join-Path $RootPath $relativePath))
    } catch {
        return $null
    }
}

function Get-CandidateParts {
    param([string]$Candidate)

    if ([string]::IsNullOrWhiteSpace($Candidate)) {
        return @()
    }

    if ($Candidate.IndexOfAny([char[]]"`r`n`t ") -ge 0) {
        return ($Candidate -split '\s+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    return @($Candidate)
}

function Add-Finding {
    param(
        [System.Collections.Generic.List[object]]$Findings,
        [string]$FilePath,
        [int]$Line,
        [string]$Reference,
        [string]$ResolvedPath
    )

    $Findings.Add([pscustomobject]@{
        file = $FilePath
        line = $Line
        reference = $Reference
        resolved_path = $ResolvedPath
    }) | Out-Null
}

$findings = New-Object System.Collections.Generic.List[object]
$scannedFiles = 0
$referenceCandidates = 0

foreach ($filePath in (Get-ScanFiles -RootPath $repoRoot)) {
    $scannedFiles++
    $relativeFilePath = Get-RelativeUnixPath -BasePath $repoRoot -FullPath $filePath
    $text = Get-Content -LiteralPath $filePath -Raw

    $matches = New-Object System.Collections.Generic.List[System.Text.RegularExpressions.Match]
    foreach ($match in [regex]::Matches($text, $quotedTokenPattern)) {
        $matches.Add($match) | Out-Null
    }
    foreach ($match in [regex]::Matches($text, $markdownLinkPattern)) {
        $matches.Add($match) | Out-Null
    }

    foreach ($match in $matches) {
        $candidate =
            if ($match.Groups.Count -ge 4 -and ($match.Groups[1].Success -or $match.Groups[2].Success -or $match.Groups[3].Success)) {
                if ($match.Groups[1].Success) { $match.Groups[1].Value }
                elseif ($match.Groups[2].Success) { $match.Groups[2].Value }
                else { $match.Groups[3].Value }
            }
            elseif ($match.Groups.Count -ge 2 -and $match.Groups[1].Success) {
                $match.Groups[1].Value
            }
            else {
                $match.Value
            }

        foreach ($part in (Get-CandidateParts -Candidate $candidate)) {
            if (-not (Test-LooksLikeRepoPath -Candidate $part)) {
                continue
            }

            $resolved = Resolve-RepoPath -RootPath $repoRoot -Candidate $part
            if ([string]::IsNullOrWhiteSpace($resolved)) {
                continue
            }

            $referenceCandidates++
            if (Test-Path -LiteralPath $resolved) {
                continue
            }

            $lineNumber = ([regex]::Matches($text.Substring(0, $match.Index), "`n")).Count + 1
            Add-Finding -Findings $findings `
                -FilePath $relativeFilePath `
                -Line $lineNumber `
                -Reference (Normalize-Candidate $part) `
                -ResolvedPath $resolved
        }
    }
}

$report = [ordered]@{
    root = $repoRoot
    scanned_files = $scannedFiles
    reference_candidates = $referenceCandidates
    finding_count = $findings.Count
    findings = $findings.ToArray()
}

$json = $report | ConvertTo-Json -Depth 8

if (-not [string]::IsNullOrWhiteSpace($WriteReportPath)) {
    $reportPath = if ([System.IO.Path]::IsPathRooted($WriteReportPath)) {
        $WriteReportPath
    } else {
        Join-Path $repoRoot $WriteReportPath
    }

    $reportDir = Split-Path -Parent $reportPath
    if (-not [string]::IsNullOrWhiteSpace($reportDir) -and -not (Test-Path -LiteralPath $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir -Force | Out-Null
    }

    Set-Content -LiteralPath $reportPath -Value $json -Encoding UTF8
}

$json

if ($findings.Count -gt 0) {
    throw "Found $($findings.Count) broken repo-local path references."
}
