<#
.SYNOPSIS
    Preview or remove PalLLM-generated local clutter.

.DESCRIPTION
    Keeps the repo usable for novices by separating disposable generated output
    from source, docs, samples, and release evidence. The default mode is a
    preview. Pass -Apply to remove only verified generated directories.

    By default this script prunes bulky HTML coverage folders nested under
    artifacts/full-audit/<timestamp>/coverage while preserving each audit
    directory and its RESULTS.md file. That keeps changelog / handoff links
    intact but removes the largest local-only clutter.

    Pass -BuildOutputs to also include src/**/bin, src/**/obj, tests/**/bin,
    and tests/**/obj directories.

.EXAMPLE
    pwsh ./scripts/pal-cleanup.ps1

.EXAMPLE
    pwsh ./scripts/pal-cleanup.ps1 -Apply

.EXAMPLE
    pwsh ./scripts/pal-cleanup.ps1 -BuildOutputs -Json
#>
[CmdletBinding()]
param(
    [ValidateRange(0, 1000)]
    [int]$KeepCoverageReports = 1,

    [switch]$BuildOutputs,

    [switch]$Apply,

    [switch]$Json
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$fullAuditRoot = Join-Path $repoRoot "artifacts/full-audit"
$sourceRoot = Join-Path $repoRoot "src"
$testsRoot = Join-Path $repoRoot "tests"

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [System.IO.Path]::GetFullPath($Path)
}

function Test-IsUnderRoot {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Root
    )

    $fullPath = Get-FullPath $Path
    $trimChars = @(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $fullRoot = (Get-FullPath $Root).TrimEnd($trimChars)
    $rootWithSlash = $fullRoot + [System.IO.Path]::DirectorySeparatorChar

    return $fullPath.StartsWith($rootWithSlash, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-DirectorySizeBytes {
    param([Parameter(Mandatory = $true)][string]$Path)

    $sum = Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum
    if ($null -eq $sum.Sum) { return [int64]0 }
    return [int64]$sum.Sum
}

function Assert-SafeCandidate {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Path
    )

    $fullPath = Get-FullPath $Path
    $name = Split-Path -Leaf $fullPath

    if ($Kind -eq "full-audit-coverage") {
        if ($name -ne "coverage") {
            throw "Refusing to remove full-audit candidate '$fullPath' because the leaf is not 'coverage'."
        }

        $parentName = Split-Path -Leaf (Split-Path -Parent $fullPath)
        if ($parentName -notmatch '^\d{8}-\d{6}$') {
            throw "Refusing to remove full-audit candidate '$fullPath' because the parent is not a timestamped audit directory."
        }

        if (-not (Test-IsUnderRoot -Path $fullPath -Root $fullAuditRoot)) {
            throw "Refusing to remove '$fullPath' because it is outside artifacts/full-audit."
        }

        return
    }

    if ($Kind -eq "build-output") {
        if ($name -ne "bin" -and $name -ne "obj") {
            throw "Refusing to remove build-output candidate '$fullPath' because the leaf is not bin or obj."
        }

        $underSource = (Test-IsUnderRoot -Path $fullPath -Root $sourceRoot)
        $underTests = (Test-IsUnderRoot -Path $fullPath -Root $testsRoot)
        if (-not ($underSource -or $underTests)) {
            throw "Refusing to remove '$fullPath' because it is outside src/ or tests/."
        }

        return
    }

    throw "Unknown cleanup candidate kind '$Kind'."
}

function New-Candidate {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Reason
    )

    $fullPath = Get-FullPath $Path
    Assert-SafeCandidate -Kind $Kind -Path $fullPath
    $bytes = Get-DirectorySizeBytes -Path $fullPath

    [pscustomobject]@{
        Kind = $Kind
        Path = $fullPath
        Reason = $Reason
        Bytes = $bytes
        Megabytes = [math]::Round($bytes / 1MB, 2)
        Status = "pending"
    }
}

function Select-TopLevelDirectories {
    param([Parameter(Mandatory = $true)][object[]]$Directories)

    $selected = New-Object System.Collections.Generic.List[string]
    foreach ($dir in ($Directories | Sort-Object { $_.FullName.Length })) {
        $full = Get-FullPath $dir.FullName
        $isNested = $false
        foreach ($parent in $selected) {
            $trimChars = @(
                [System.IO.Path]::DirectorySeparatorChar,
                [System.IO.Path]::AltDirectorySeparatorChar)
            $parentWithSlash = $parent.TrimEnd($trimChars) + [System.IO.Path]::DirectorySeparatorChar
            if ($full.StartsWith($parentWithSlash, [System.StringComparison]::OrdinalIgnoreCase)) {
                $isNested = $true
                break
            }
        }

        if (-not $isNested) {
            $selected.Add($full)
        }
    }

    return $selected
}

$candidates = New-Object System.Collections.Generic.List[object]

if (Test-Path -LiteralPath $fullAuditRoot) {
    $coverageRuns = Get-ChildItem -LiteralPath $fullAuditRoot -Directory -Force |
        Where-Object {
            $_.Name -match '^\d{8}-\d{6}$' -and
            (Test-Path -LiteralPath (Join-Path $_.FullName "coverage"))
        } |
        Sort-Object LastWriteTime -Descending

    $oldCoverageRuns = @($coverageRuns | Select-Object -Skip $KeepCoverageReports)
    foreach ($run in $oldCoverageRuns) {
        $coveragePath = Join-Path $run.FullName "coverage"
        $candidates.Add((New-Candidate `
            -Kind "full-audit-coverage" `
            -Path $coveragePath `
            -Reason "Older than the latest $KeepCoverageReports coverage report(s); audit RESULTS.md is retained."))
    }
}

if ($BuildOutputs) {
    foreach ($root in @($sourceRoot, $testsRoot)) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $buildDirs = Get-ChildItem -LiteralPath $root -Directory -Recurse -Force |
            Where-Object { $_.Name -eq "bin" -or $_.Name -eq "obj" }
        foreach ($dir in (Select-TopLevelDirectories -Directories @($buildDirs))) {
            $candidates.Add((New-Candidate `
                -Kind "build-output" `
                -Path $dir `
                -Reason "Generated .NET build output; rebuilt by dotnet build/test."))
        }
    }
}

if ($Apply) {
    foreach ($candidate in $candidates) {
        Assert-SafeCandidate -Kind $candidate.Kind -Path $candidate.Path
        try {
            Remove-Item -LiteralPath $candidate.Path -Recurse -Force -ErrorAction Stop
            $candidate.Status = "removed"
        } catch {
            $candidate.Status = "failed: $($_.Exception.Message)"
        }
    }
} else {
    foreach ($candidate in $candidates) {
        $candidate.Status = "preview"
    }
}

$mode = if ($Apply) { "apply" } else { "preview" }
$resolvedRepoRoot = Get-FullPath $repoRoot
$totalBytes = [int64]($candidates | Measure-Object -Property Bytes -Sum).Sum
$candidateArray = $candidates.ToArray()
$result = [pscustomobject]@{
    mode = $mode
    repoRoot = $resolvedRepoRoot
    keptCoverageReports = $KeepCoverageReports
    includesBuildOutputs = [bool]$BuildOutputs
    candidateCount = $candidates.Count
    totalBytes = $totalBytes
    totalMegabytes = [math]::Round($totalBytes / 1MB, 2)
    candidates = $candidateArray
}

if ($Json) {
    $result | ConvertTo-Json -Depth 5
    return
}

Write-Host ""
Write-Host "PalLLM generated-artifact cleanup" -ForegroundColor Cyan
Write-Host ("Mode              : {0}" -f $result.mode)
Write-Host ("Coverage retained : latest {0}" -f $KeepCoverageReports)
Write-Host ("Build outputs     : {0}" -f ($(if ($BuildOutputs) { "included" } else { "skipped (pass -BuildOutputs to include)" })))
Write-Host ("Candidates        : {0}" -f $result.candidateCount)
Write-Host ("Potential reclaim : {0} MB" -f $result.totalMegabytes)
Write-Host ""

if ($candidates.Count -eq 0) {
    Write-Host "No generated clutter candidates found." -ForegroundColor Green
    Write-Host ""
    return
}

$candidates |
    Sort-Object Kind, Path |
    Select-Object Kind, Megabytes, Status, Reason, Path |
    Format-Table -AutoSize -Wrap

if (-not $Apply) {
    Write-Host ""
    Write-Host "Preview only. Re-run with -Apply to delete these generated directories." -ForegroundColor Yellow
    Write-Host "This preserves source, docs, samples, release evidence, and audit RESULTS.md files."
} else {
    $failed = @($candidates | Where-Object { $_.Status -like "failed:*" })
    if ($failed.Count -gt 0) {
        Write-Host ""
        Write-Host ("Cleanup finished with {0} failure(s)." -f $failed.Count) -ForegroundColor Red
        exit 1
    }
    Write-Host ""
    Write-Host "Cleanup applied." -ForegroundColor Green
}

Write-Host ""
