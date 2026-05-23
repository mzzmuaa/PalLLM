<#
.SYNOPSIS
    Runs the local publication-readiness checks that matter before a release.

.DESCRIPTION
    Focused "can this be published?" wrapper. It does not build, test, package,
    or contact package registries. It composes the local release-copy,
    path-reference, workflow-pin, and third-party notice coverage checks into
    one timestamped report under artifacts/publish-audit/.
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$OutputRoot = "",
    [switch]$Json
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$repoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $timestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $OutputRoot = Join-Path $repoRoot "artifacts\publish-audit\$timestamp"
}

$outputRootPath = if ([IO.Path]::IsPathRooted($OutputRoot)) {
    $OutputRoot
}
else {
    Join-Path $repoRoot $OutputRoot
}

New-Item -ItemType Directory -Force -Path $outputRootPath | Out-Null

$resultsPath = Join-Path $outputRootPath "RESULTS.md"
$jsonPath = Join-Path $outputRootPath "publish-audit.json"
$steps = [System.Collections.Generic.List[object]]::new()

function ConvertTo-RelativeRepoPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($repoRoot)
    if (-not $base.EndsWith([IO.Path]::DirectorySeparatorChar.ToString(), [StringComparison]::Ordinal)) {
        $base += [IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [Uri]$base
    $fullUri = [Uri]$full
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fullUri).ToString()).Replace('/', '\')
}

function Invoke-CheckedPowerShellScript {
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [string[]]$Arguments = @()
    )

    $scriptArgs = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $ScriptPath
    ) + $Arguments

    $output = & powershell @scriptArgs 2>&1
    $exitCode = $LASTEXITCODE
    $output | ForEach-Object { $_ }
    if ($exitCode -ne 0) {
        throw "script exited with code $exitCode"
    }
}

function Add-PublishAuditStep {
    param(
        [Parameter(Mandatory = $true)][string]$Id,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][scriptblock]$Body
    )

    $safeId = $Id -replace '[^\w.-]', '_'
    $logPath = Join-Path $outputRootPath ("{0:D2}_{1}.log" -f ($steps.Count + 1), $safeId)
    $start = Get-Date
    $status = "pass"
    $errorText = ""
    $lines = @()

    try {
        $global:LASTEXITCODE = 0
        $captured = & $Body 2>&1
        $lines = @($captured | ForEach-Object { $_.ToString() })
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            throw "step exit code $LASTEXITCODE"
        }
    }
    catch {
        $status = "fail"
        $errorText = $_.Exception.Message
        if ($lines.Count -eq 0) {
            $lines = @($errorText)
        }
        else {
            $lines += "ERROR: $errorText"
        }
    }

    Set-Content -LiteralPath $logPath -Value ($lines -join [Environment]::NewLine) -Encoding UTF8
    $duration = (Get-Date) - $start

    $steps.Add([pscustomobject]@{
        Id = $Id
        Description = $Description
        Status = $status
        Seconds = [math]::Round($duration.TotalSeconds, 1)
        Error = $errorText
        LogPath = $logPath
        LogRelativePath = ConvertTo-RelativeRepoPath -Path $logPath
    }) | Out-Null
}

function Test-ThirdPartyNoticeCoverage {
    $requiredFiles = @(
        "LICENSE",
        "NOTICE.md",
        "THIRD_PARTY_NOTICES.md",
        "SECURITY.md",
        "SECURITY.txt",
        "docs\RELEASE.md"
    )

    $issues = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $requiredFiles) {
        $absolutePath = Join-Path $repoRoot $relativePath
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
            $issues.Add("Required publication file missing: $relativePath") | Out-Null
        }
    }

    $noticePath = Join-Path $repoRoot "THIRD_PARTY_NOTICES.md"
    $noticeText = if (Test-Path -LiteralPath $noticePath) {
        Get-Content -LiteralPath $noticePath -Raw
    }
    else {
        ""
    }

    if ($noticeText.IndexOf("SPDX", [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        $issues.Add("THIRD_PARTY_NOTICES.md should use SPDX license-expression language for package rows.") | Out-Null
    }

    $packageIds = [System.Collections.Generic.SortedSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $projectRoots = @(
        (Join-Path $repoRoot "src"),
        (Join-Path $repoRoot "tests")
    )

    foreach ($projectRoot in $projectRoots) {
        if (-not (Test-Path -LiteralPath $projectRoot -PathType Container)) {
            continue
        }

        foreach ($projectFile in Get-ChildItem -LiteralPath $projectRoot -Recurse -Filter "*.csproj" -File) {
            if ($projectFile.FullName -match '\\(bin|obj)\\') {
                continue
            }

            $projectText = Get-Content -LiteralPath $projectFile.FullName -Raw
            foreach ($match in [regex]::Matches($projectText, '<PackageReference\s+Include="([^"]+)"')) {
                $packageIds.Add($match.Groups[1].Value) | Out-Null
            }
        }
    }

    foreach ($packageId in $packageIds) {
        if ($noticeText.IndexOf($packageId, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            $issues.Add("THIRD_PARTY_NOTICES.md does not mention PackageReference: $packageId") | Out-Null
        }
    }

    Write-Output ("Publication files checked: {0}" -f $requiredFiles.Count)
    Write-Output ("PackageReference ids checked: {0}" -f $packageIds.Count)
    foreach ($packageId in $packageIds) {
        Write-Output ("  - {0}" -f $packageId)
    }

    if ($issues.Count -gt 0) {
        foreach ($issue in $issues) {
            Write-Output ("ISSUE: {0}" -f $issue)
        }

        throw ("third-party notice coverage failed with {0} issue(s)" -f $issues.Count)
    }

    Write-Output "THIRD_PARTY_NOTICES.md covers every current PackageReference id."
}

$publicCopyReportPath = Join-Path $outputRootPath "public-copy-audit.md"
$pathReferenceReportPath = Join-Path $outputRootPath "path-reference-audit.json"

Add-PublishAuditStep `
    -Id "public-copy" `
    -Description "Release-facing text avoids broad scope drift, sibling-project bleed, and unrelated franchise references." `
    -Body {
        Invoke-CheckedPowerShellScript `
            -ScriptPath (Join-Path $repoRoot "scripts\audit_public_copy.ps1") `
            -Arguments @("-RepoRoot", $repoRoot, "-WriteReportPath", $publicCopyReportPath)
    }

Add-PublishAuditStep `
    -Id "path-references" `
    -Description "Docs and manifests do not point at missing local paths." `
    -Body {
        Invoke-CheckedPowerShellScript `
            -ScriptPath (Join-Path $repoRoot "scripts\path_reference_audit.ps1") `
            -Arguments @("-RepoRoot", $repoRoot, "-WriteReportPath", $pathReferenceReportPath)
    }

Add-PublishAuditStep `
    -Id "workflow-action-pins" `
    -Description "External GitHub Actions are pinned to full commit SHAs." `
    -Body {
        Invoke-CheckedPowerShellScript `
            -ScriptPath (Join-Path $repoRoot "scripts\audit-workflow-action-pins.ps1") `
            -Arguments @("-RepoRoot", $repoRoot)
    }

Add-PublishAuditStep `
    -Id "third-party-notices" `
    -Description "Publication files exist and THIRD_PARTY_NOTICES.md names every current NuGet PackageReference." `
    -Body {
        Test-ThirdPartyNoticeCoverage
    }

$failedSteps = @($steps | Where-Object { $_.Status -ne "pass" })
$overall = if ($failedSteps.Count -eq 0) { "PASS" } else { "FAIL" }

$reportLines = [System.Collections.Generic.List[string]]::new()
$reportLines.Add("# PalLLM Publish Audit Results") | Out-Null
$reportLines.Add("") | Out-Null
$reportLines.Add(("- Generated: {0} UTC" -f ([DateTimeOffset]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'")))) | Out-Null
$reportLines.Add(("- Repo root: {0}" -f $repoRoot)) | Out-Null
$reportLines.Add(("- Overall: **{0}**" -f $overall)) | Out-Null
$reportLines.Add("") | Out-Null
$reportLines.Add("| # | Step | Status | Seconds | Log |") | Out-Null
$reportLines.Add("| -: | :--- | :---: | --: | :--- |") | Out-Null

for ($i = 0; $i -lt $steps.Count; $i++) {
    $step = $steps[$i]
    $reportLines.Add((
        "| {0} | {1} | **{2}** | {3} | [{4}]({5}) |" -f
        ($i + 1),
        $step.Id,
        $step.Status.ToUpperInvariant(),
        $step.Seconds,
        (Split-Path -Leaf $step.LogPath),
        (Split-Path -Leaf $step.LogPath)
    )) | Out-Null
}

if ($failedSteps.Count -gt 0) {
    $reportLines.Add("") | Out-Null
    $reportLines.Add("## Blockers") | Out-Null
    $reportLines.Add("") | Out-Null
    foreach ($step in $failedSteps) {
        $reportLines.Add(("- `{0}`: {1}" -f $step.Id, $step.Error)) | Out-Null
    }
}

$reportLines.Add("") | Out-Null
$reportLines.Add("## Notes") | Out-Null
$reportLines.Add("") | Out-Null
$reportLines.Add("- This audit is local-only. It does not contact package registries or public GitHub APIs.") | Out-Null
$reportLines.Add("- Run the full audit before tagging; this wrapper is a focused publication preflight, not a build/test replacement.") | Out-Null

Set-Content -LiteralPath $resultsPath -Value ($reportLines -join [Environment]::NewLine) -Encoding UTF8

$payload = [pscustomobject]@{
    Status = if ($overall -eq "PASS") { "passed" } else { "failed" }
    GeneratedAtUtc = [DateTimeOffset]::UtcNow
    RepoRoot = $repoRoot
    ResultsPath = $resultsPath
    PublicCopyReportPath = $publicCopyReportPath
    PathReferenceReportPath = $pathReferenceReportPath
    Steps = @($steps)
}

$payloadJson = $payload | ConvertTo-Json -Depth 8
Set-Content -LiteralPath $jsonPath -Value $payloadJson -Encoding UTF8

if ($Json) {
    $payloadJson
}
else {
    Get-Content -LiteralPath $resultsPath
}

if ($overall -ne "PASS") {
    exit 1
}
