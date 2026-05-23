[CmdletBinding()]
param(
    [string]$RepoRoot = "",
    [string]$WriteReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
    $RepoRoot = Join-Path $PSScriptRoot ".."
}

$repoRoot = (Resolve-Path -LiteralPath $RepoRoot).Path
. (Join-Path $PSScriptRoot "public_copy_policy.ps1")
$policy = Get-PublicCopyPolicy -RepoRoot $repoRoot

$publicFiles = $policy.PublicFiles
$publicSupportFiles = $policy.PublicSupportFiles
$blockedPublicBrandPattern = $policy.BlockedPublicBrandPattern
$blockedPublicScopePatterns = $policy.BlockedPublicScopePatterns
$blockedPublicFranchisePatterns = $policy.BlockedPublicFranchisePatterns
$blockedPublicLegalOverclaimPatterns = $policy.BlockedPublicLegalOverclaimPatterns
$blockedSiblingProjectPatterns = $policy.BlockedSiblingProjectPatterns

function Get-RelativePath {
    param([string]$FullPath)

    return $FullPath.Substring($repoRoot.Length).TrimStart('\', '/') -replace '\\', '/'
}

function Add-Issue {
    param(
        [System.Collections.Generic.List[object]]$List,
        [string]$FilePath,
        [string]$Kind,
        [string]$Message
    )

    $List.Add([pscustomobject]@{
        File = Get-RelativePath -FullPath $FilePath
        Kind = $Kind
        Message = $Message
    }) | Out-Null
}

function Test-PublicCopyFiles {
    param(
        [string[]]$Files,
        [string]$BrandMessage,
        [switch]$CheckSiblingPatterns,
        [switch]$CheckScopePatterns,
        [switch]$CheckFranchisePatterns,
        [switch]$CheckLegalOverclaimPatterns
    )

    foreach ($file in $Files) {
        if (-not (Test-Path -LiteralPath $file)) {
            Add-Issue -List $issues -FilePath $file -Kind "missing-file" -Message "Required publication-facing file is missing."
            continue
        }

        $text = Get-Content -LiteralPath $file -Raw
        if ($text -match $blockedPublicBrandPattern) {
            $match = [regex]::Match($text, $blockedPublicBrandPattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            Add-Issue -List $issues -FilePath $file -Kind "blocked-brand" -Message ("{0} Found: {1}" -f $BrandMessage, $match.Value)
        }

        if ($CheckSiblingPatterns) {
            foreach ($blocked in $blockedSiblingProjectPatterns) {
                if ($text -match $blocked.Pattern) {
                    $match = [regex]::Match($text, $blocked.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    Add-Issue -List $issues -FilePath $file -Kind "sibling-project-bleed" -Message ("{0} Found: {1}" -f $blocked.Message, $match.Value)
                }
            }
        }

        if ($CheckScopePatterns) {
            foreach ($blocked in $blockedPublicScopePatterns) {
                if ($text -match $blocked.Pattern) {
                    $match = [regex]::Match($text, $blocked.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    Add-Issue -List $issues -FilePath $file -Kind "scope-drift" -Message ("{0} Found: {1}" -f $blocked.Message, $match.Value)
                }
            }
        }

        if ($CheckFranchisePatterns) {
            foreach ($blocked in $blockedPublicFranchisePatterns) {
                if ($text -match $blocked.Pattern) {
                    $match = [regex]::Match($text, $blocked.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    Add-Issue -List $issues -FilePath $file -Kind "blocked-ip" -Message ("{0} Found: {1}" -f $blocked.Message, $match.Value)
                }
            }
        }

        if ($CheckLegalOverclaimPatterns) {
            foreach ($blocked in $blockedPublicLegalOverclaimPatterns) {
                if ($text -match $blocked.Pattern) {
                    $match = [regex]::Match($text, $blocked.Pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                    Add-Issue -List $issues -FilePath $file -Kind "legal-overclaim" -Message ("{0} Found: {1}" -f $blocked.Message, $match.Value)
                }
            }
        }
    }
}

$issues = New-Object System.Collections.Generic.List[object]

Test-PublicCopyFiles `
    -Files $publicFiles `
    -BrandMessage "Public release-facing copy should prefer neutral protocol and capability language over third-party client or model brands." `
    -CheckSiblingPatterns `
    -CheckScopePatterns `
    -CheckFranchisePatterns `
    -CheckLegalOverclaimPatterns
Test-PublicCopyFiles `
    -Files $publicSupportFiles `
    -BrandMessage "Contributor/support copy should avoid unnecessary third-party client or model brands when the protocol or file path is enough." `
    -CheckSiblingPatterns `
    -CheckFranchisePatterns `
    -CheckLegalOverclaimPatterns

$readmePath = Join-Path $repoRoot "README.md"
if (Test-Path -LiteralPath $readmePath) {
    $readmeText = Get-Content -LiteralPath $readmePath -Raw
    if ($readmeText -notmatch 'docs/RELEASE\.md') {
        Add-Issue -List $issues -FilePath $readmePath -Kind "missing-release-link" -Message "README should link to docs/RELEASE.md so publishability guidance is visible from the repo front page."
    }
}

$securityPath = Join-Path $repoRoot "SECURITY.md"
if (Test-Path -LiteralPath $securityPath) {
    $securityText = Get-Content -LiteralPath $securityPath -Raw
    if ($securityText -notmatch 'private vulnerability reporting') {
        Add-Issue -List $issues -FilePath $securityPath -Kind "missing-security-guidance" -Message "SECURITY.md should mention GitHub private vulnerability reporting."
    }
    if ($securityText -notmatch 'push protection') {
        Add-Issue -List $issues -FilePath $securityPath -Kind "missing-security-guidance" -Message "SECURITY.md should mention secret-scanning push protection for public hosting."
    }
}

$releaseGuidePath = Join-Path $repoRoot "docs/RELEASE.md"
if (-not (Test-Path -LiteralPath $releaseGuidePath)) {
    Add-Issue -List $issues -FilePath $releaseGuidePath -Kind "missing-file" -Message "docs/RELEASE.md should exist and define the publication checklist."
} else {
    $releaseText = Get-Content -LiteralPath $releaseGuidePath -Raw
    foreach ($required in @('public copy audit', 'path reference audit', 'publish-audit', 'push protection', 'private vulnerability reporting', 'publication blockers')) {
        if ($releaseText -notmatch [regex]::Escape($required)) {
            Add-Issue -List $issues -FilePath $releaseGuidePath -Kind "missing-release-guidance" -Message "docs/RELEASE.md should mention '$required'."
        }
    }
    foreach ($required in @('unrelated third-party franchise', 'broader platform', 'sibling-project bleed')) {
        if ($releaseText -notmatch [regex]::Escape($required)) {
            Add-Issue -List $issues -FilePath $releaseGuidePath -Kind "missing-release-guidance" -Message "docs/RELEASE.md should mention '$required' public-copy guardrails."
        }
    }
}

$contributingPath = Join-Path $repoRoot "CONTRIBUTING.md"
if (Test-Path -LiteralPath $contributingPath) {
    $contributingText = Get-Content -LiteralPath $contributingPath -Raw
    if ($contributingText -notmatch 'pre-commit|\.pre-commit-config\.yaml') {
        Add-Issue -List $issues -FilePath $contributingPath -Kind "missing-contributor-guardrail" -Message "CONTRIBUTING.md should explain the pre-commit hooks and publication audits."
    }
}

$report = New-Object System.Text.StringBuilder
[void]$report.AppendLine("# PalLLM Public Copy Audit")
[void]$report.AppendLine()
[void]$report.AppendLine("Generated: $([DateTimeOffset]::Now.ToString('yyyy-MM-dd HH:mm zzz'))")
[void]$report.AppendLine("Repo root: $repoRoot")
[void]$report.AppendLine()
[void]$report.AppendLine("Release-facing files checked:")
foreach ($file in $publicFiles) {
    [void]$report.AppendLine("- $(Get-RelativePath -FullPath $file)")
}
[void]$report.AppendLine()
[void]$report.AppendLine("Support-facing files checked:")
foreach ($file in $publicSupportFiles) {
    [void]$report.AppendLine("- $(Get-RelativePath -FullPath $file)")
}
[void]$report.AppendLine()

if ($issues.Count -eq 0) {
    [void]$report.AppendLine("- Public copy is neutral enough for publication-facing surfaces, free of blocked sibling-project bleed, unrelated franchise, legal-overclaim, or scope-drift language, and the release/security guidance is present.")
} else {
    [void]$report.AppendLine("## Issues")
    [void]$report.AppendLine()
    foreach ($issue in $issues) {
        [void]$report.AppendLine("- [$($issue.Kind)] $($issue.File): $($issue.Message)")
    }
}

$output = $report.ToString().TrimEnd()
if ($WriteReportPath) {
    $reportPath = if ([System.IO.Path]::IsPathRooted($WriteReportPath)) {
        $WriteReportPath
    } else {
        Join-Path $repoRoot $WriteReportPath
    }

    $reportDir = Split-Path -Parent $reportPath
    if ($reportDir -and -not (Test-Path -LiteralPath $reportDir)) {
        New-Item -ItemType Directory -Path $reportDir | Out-Null
    }

    Set-Content -LiteralPath $reportPath -Value $output -Encoding UTF8
}

$output

if ($issues.Count -gt 0) {
    Write-Error ("Public copy audit found {0} issue(s)." -f $issues.Count)
    exit 1
}
