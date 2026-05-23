<#
.SYNOPSIS
    Checks GitHub Releases for a newer PalLLM release than the one
    currently installed.

.DESCRIPTION
    Opt-in network call. Hits the GitHub Releases API for the
    configured OWNER/REPO and compares the latest tag against the
    locally-discoverable version (from a release zip's
    RELEASE_PACKAGE_MANIFEST.json or the sidecar exe's FileVersion).

    Honest about the network call: this is the only PalLLM script
    that contacts a public-internet endpoint, and it never sends any
    user data. Reads only the tag and HTML URL from the response.

.PARAMETER Owner
    GitHub repository owner. Defaults to 'palllm-dev' as a placeholder
    since the canonical owner has not been pinned yet. Override with
    your fork's owner.

.PARAMETER Repo
    GitHub repository name. Defaults to 'PalLLM'.

.PARAMETER InstalledVersion
    Override the discovered installed version. By default the script
    looks at the release manifest, then the sidecar exe FileVersion,
    then falls back to "dev".

.PARAMETER TimeoutSeconds
    HTTP timeout for the GitHub call. Default 8 seconds; the GitHub
    API typically answers in well under 1 second.

.EXAMPLE
    pwsh ./scripts/check-updates.ps1
    # Default check against the placeholder owner; prints the latest
    # tag if reachable.

.EXAMPLE
    pwsh ./scripts/check-updates.ps1 -Owner my-fork
    # Check a fork instead.

.NOTES
    Verb shortcut:  pal check-updates

    Privacy posture:
      - Only outbound traffic is the GitHub API call.
      - No user data is transmitted; only the User-Agent
        ('PalLLM-update-check/1.0').
      - Run is opt-in; no PalLLM verb auto-runs this on boot.
    See docs/PRIVACY.md "Update check" for the full disclosure.
#>
[CmdletBinding()]
param(
    [string]$Owner = 'palllm-dev',
    [string]$Repo = 'PalLLM',
    [string]$InstalledVersion,
    [int]$TimeoutSeconds = 8
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

# -----------------------------------------------------------------------------
# Discover the installed version locally
# -----------------------------------------------------------------------------

function Get-InstalledVersion {
    # Strategy:
    #   1. Release zip: RELEASE_PACKAGE_MANIFEST.json next to install.bat
    #   2. Packaged sidecar exe FileVersion (when running from a release)
    #   3. Source-built sidecar Assembly version
    #   4. 'dev' fallback
    $repoRoot = Get-PalLlmRepoRoot

    $manifestPath = Join-Path $repoRoot 'RELEASE_PACKAGE_MANIFEST.json'
    if (Test-Path -LiteralPath $manifestPath) {
        try {
            $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.PSObject.Properties['version'] -and -not [string]::IsNullOrWhiteSpace($manifest.version)) {
                return $manifest.version
            }
        }
        catch { }
    }

    $exePath = Get-PalLlmPackagedSidecarExePath
    if (Test-Path -LiteralPath $exePath) {
        try {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
            if (-not [string]::IsNullOrWhiteSpace($info.FileVersion)) {
                return $info.FileVersion
            }
        }
        catch { }
    }

    return 'dev'
}

if ([string]::IsNullOrWhiteSpace($InstalledVersion)) {
    $InstalledVersion = Get-InstalledVersion
}

# -----------------------------------------------------------------------------
# Hit GitHub Releases API
# -----------------------------------------------------------------------------

$apiUrl = "https://api.github.com/repos/$Owner/$Repo/releases/latest"

Write-Host ""
Write-Host "PalLLM update check" -ForegroundColor Cyan
Write-Host "  Owner / Repo      : $Owner/$Repo"
Write-Host "  Installed version : $InstalledVersion"
Write-Host "  GitHub endpoint   : $apiUrl" -ForegroundColor DarkGray
Write-Host ""

try {
    $response = Invoke-RestMethod -Uri $apiUrl -Method Get -TimeoutSec $TimeoutSeconds `
        -Headers @{
            'User-Agent' = 'PalLLM-update-check/1.0'
            'Accept' = 'application/vnd.github+json'
        }
} catch {
    Write-Host "Could not reach GitHub:" -ForegroundColor Red
    Write-Host "  $($_.Exception.Message)"
    Write-Host ""
    Write-Host "Possible causes:" -ForegroundColor Yellow
    Write-Host "  - No internet access (this is the only PalLLM script that needs it)"
    Write-Host "  - GitHub API rate limit hit (60/hour for unauthenticated requests)"
    Write-Host "  - The Owner / Repo placeholder is not your fork - pass -Owner / -Repo"
    Write-Host "  - Releases page is empty (project hasn't shipped yet)"
    exit 1
}

$latestTag = $response.tag_name
$latestUrl = $response.html_url
$publishedAt = $response.published_at
$prerelease = $response.prerelease

Write-Host "Latest GitHub release:" -ForegroundColor White
Write-Host ("  Tag         : {0}" -f $latestTag)
Write-Host ("  Published   : {0}" -f $publishedAt)
Write-Host ("  Pre-release : {0}" -f $prerelease)
Write-Host ("  URL         : {0}" -f $latestUrl)
Write-Host ""

# -----------------------------------------------------------------------------
# Compare versions (best-effort semver-ish)
# -----------------------------------------------------------------------------

function Compare-Version {
    param([string]$Installed, [string]$Latest)
    $normalize = {
        param($s)
        if ([string]::IsNullOrWhiteSpace($s)) { return $null }
        # Strip leading 'v' and any +build / -prerelease suffixes.
        $core = ($s -replace '^[vV]', '') -replace '[-+].*$', ''
        $parts = $core -split '\.'
        $vparts = @()
        foreach ($p in $parts) {
            $n = 0
            if ([int]::TryParse($p, [ref]$n)) { $vparts += $n } else { return $null }
        }
        return ,$vparts
    }

    $a = & $normalize $Installed
    $b = & $normalize $Latest
    if ($null -eq $a -or $null -eq $b) { return 'unknown' }

    $max = [Math]::Max($a.Count, $b.Count)
    for ($i = 0; $i -lt $max; $i++) {
        $av = if ($i -lt $a.Count) { $a[$i] } else { 0 }
        $bv = if ($i -lt $b.Count) { $b[$i] } else { 0 }
        if ($av -lt $bv) { return 'newer-available' }
        if ($av -gt $bv) { return 'installed-is-newer' }
    }
    return 'up-to-date'
}

$verdict = Compare-Version -Installed $InstalledVersion -Latest $latestTag

switch ($verdict) {
    'up-to-date' {
        Write-Host "Verdict: UP TO DATE." -ForegroundColor Green
    }
    'newer-available' {
        Write-Host "Verdict: NEWER VERSION AVAILABLE." -ForegroundColor Yellow
        Write-Host "Download: $latestUrl"
    }
    'installed-is-newer' {
        Write-Host "Verdict: your installed version is newer than the latest release." -ForegroundColor Cyan
        Write-Host "(Common for dev builds; nothing to do.)"
    }
    default {
        Write-Host "Verdict: could not compare versions ('$InstalledVersion' vs '$latestTag')." -ForegroundColor Yellow
        Write-Host "Manually compare via $latestUrl"
    }
}
Write-Host ""

[pscustomobject]@{
    InstalledVersion = $InstalledVersion
    LatestTag = $latestTag
    LatestUrl = $latestUrl
    PublishedAt = $publishedAt
    Prerelease = $prerelease
    Verdict = $verdict
} | Write-Output
