# =============================================================================
# PalLLM - Release checksum helper
# =============================================================================
#
# Computes SHA-256 + (optional) SHA-512 digests for every file under
# artifacts/packaging/ and writes a canonical SHA256SUMS / SHA512SUMS
# file beside them so downstream installers can verify integrity.
#
# This does NOT sign anything - signing is a separate operator step
# documented in docs/RELEASE_SIGNING.md. This script just produces the
# digests a signer (gpg, minisign, codesign, etc.) would attach.
#
# Usage (from the repo root):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/compute-release-checksums.ps1
#
# Output:
#   artifacts/packaging/SHA256SUMS      ← "<hex>  <filename>" per line
#   artifacts/packaging/SHA512SUMS      ← same format with SHA-512
#   artifacts/packaging/checksums.json  ← structured manifest (hash,
#                                         size bytes, computed-at UTC)
#
# Exit code: 0 on success, 1 on any I/O failure.
# =============================================================================

[CmdletBinding()]
param(
    [string]$PackagingRoot,
    [switch]$SkipSha512
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $PackagingRoot) {
    $PackagingRoot = Join-Path $repoRoot "artifacts/packaging"
}
if (-not (Test-Path $PackagingRoot)) {
    Write-Error "Packaging root not found: $PackagingRoot"
    exit 1
}

Write-Host "Computing digests under: $PackagingRoot"

# Exclude the digest files themselves + detached signature sidecars. If a
# maintainer reruns this after signing, the checksum manifest should not start
# hashing the signature over its previous self.
$excluded = @(
    "SHA256SUMS",
    "SHA512SUMS",
    "checksums.json",
    "SHA256SUMS.minisig",
    "SHA256SUMS.asc",
    "SHA256SUMS.sig",
    "SHA512SUMS.minisig",
    "SHA512SUMS.asc",
    "SHA512SUMS.sig",
    "checksums.json.minisig",
    "checksums.json.asc",
    "checksums.json.sig"
)
$files = @(Get-ChildItem -Path $PackagingRoot -File | Where-Object { $excluded -notcontains $_.Name })

if ($files.Count -eq 0) {
    Write-Error "No artifact files found under $PackagingRoot - run scripts/package-release.ps1 first."
    exit 1
}

$sha256Lines = New-Object System.Collections.Generic.List[string]
$sha512Lines = New-Object System.Collections.Generic.List[string]
$entries = New-Object System.Collections.Generic.List[object]

foreach ($f in $files) {
    $sha256 = Get-FileHash -Algorithm SHA256 -Path $f.FullName
    $sha256Lines.Add(("{0}  {1}" -f $sha256.Hash.ToLowerInvariant(), $f.Name))

    $sha512 = $null
    if (-not $SkipSha512) {
        $sha512 = Get-FileHash -Algorithm SHA512 -Path $f.FullName
        $sha512Lines.Add(("{0}  {1}" -f $sha512.Hash.ToLowerInvariant(), $f.Name))
    }

    $entries.Add([PSCustomObject]@{
        Name        = $f.Name
        SizeBytes   = $f.Length
        Sha256      = $sha256.Hash.ToLowerInvariant()
        Sha512      = if ($sha512) { $sha512.Hash.ToLowerInvariant() } else { $null }
        ComputedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    })
}

# Write canonical digest files - no trailing whitespace, LF newlines,
# sorted by filename for reproducibility.
$sha256Path = Join-Path $PackagingRoot "SHA256SUMS"
$sha512Path = Join-Path $PackagingRoot "SHA512SUMS"
$jsonPath   = Join-Path $PackagingRoot "checksums.json"

$sha256Lines = $sha256Lines | Sort-Object
[System.IO.File]::WriteAllText($sha256Path, [string]::Join("`n", $sha256Lines) + "`n", [System.Text.UTF8Encoding]::new($false))

if (-not $SkipSha512) {
    $sha512Lines = $sha512Lines | Sort-Object
    [System.IO.File]::WriteAllText($sha512Path, [string]::Join("`n", $sha512Lines) + "`n", [System.Text.UTF8Encoding]::new($false))
}

$manifest = [PSCustomObject]@{
    ComputedAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    Artifacts     = ($entries | Sort-Object Name)
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding utf8

$runtimeRoot = Get-PalLlmRuntimeRoot
$releaseEvidenceDir = Join-Path $runtimeRoot "ReleaseEvidence"
$historyDir = Join-Path $releaseEvidenceDir "History"
New-Item -ItemType Directory -Force -Path $historyDir | Out-Null

$capturedAtUtc = [DateTimeOffset]::UtcNow
$historyStamp = $capturedAtUtc.ToString("yyyyMMdd-HHmmss")
$latestArtifactPath = Join-Path $releaseEvidenceDir "latest-artifact-integrity.json"
$historyArtifactPath = Join-Path $historyDir ("artifact-integrity-{0}.json" -f $historyStamp)
$signatureCandidates = @(
    "SHA256SUMS.minisig",
    "SHA256SUMS.asc",
    "SHA256SUMS.sig",
    "checksums.json.minisig",
    "checksums.json.asc",
    "checksums.json.sig"
)
$signatureFiles = @(
    foreach ($candidate in $signatureCandidates) {
        $candidatePath = Join-Path $PackagingRoot $candidate
        if (Test-Path -LiteralPath $candidatePath) {
            (Get-Item -LiteralPath $candidatePath).FullName
        }
    }
)
$readyEvidence = New-Object System.Collections.Generic.List[string]
$readyEvidence.Add(("SHA256SUMS covers {0} release artifact(s)." -f $files.Count))
if (-not $SkipSha512) {
    $readyEvidence.Add(("SHA512SUMS covers {0} release artifact(s)." -f $files.Count))
}
if ($signatureFiles.Count -gt 0) {
    $readyEvidence.Add(("Detached signature file(s) present: {0}" -f (($signatureFiles | ForEach-Object { Split-Path -Leaf $_ }) -join ", ")))
}
else {
    $readyEvidence.Add("No detached signature file was present when checksums were computed; CI artifact attestation or a later signature step can still provide authenticity.")
}

$integrityEvidence = [ordered]@{
    Status = "recorded"
    Summary = if ($signatureFiles.Count -gt 0) {
        "PalLLM release artifact digest manifests were computed and detached signature files were present."
    }
    else {
        "PalLLM release artifact digest manifests were computed. Attach a detached signature or publish CI artifact attestations before release."
    }
    CapturedAtUtc = $capturedAtUtc
    ArtifactPath = $latestArtifactPath
    HistoryArtifactPath = $historyArtifactPath
    PackagingRoot = (Resolve-Path -LiteralPath $PackagingRoot).ProviderPath
    ChecksumsJsonPath = $jsonPath
    Sha256SumsPath = $sha256Path
    Sha512SumsPath = if ($SkipSha512) { "" } else { $sha512Path }
    ArtifactCount = [int]$files.Count
    ChecksumsJsonPresent = (Test-Path -LiteralPath $jsonPath)
    Sha256SumsPresent = (Test-Path -LiteralPath $sha256Path)
    Sha512SumsPresent = if ($SkipSha512) { $false } else { (Test-Path -LiteralPath $sha512Path) }
    Sha512Skipped = $SkipSha512.IsPresent
    DetachedSignaturePresent = ($signatureFiles.Count -gt 0)
    DetachedSignaturePaths = @($signatureFiles)
    CurrentBlockers = @()
    ReadyEvidence = @($readyEvidence.ToArray())
}
$integrityJson = ConvertTo-PalLlmJsonBody -InputObject $integrityEvidence
Set-Content -LiteralPath $historyArtifactPath -Value $integrityJson -Encoding UTF8
Set-Content -LiteralPath $latestArtifactPath -Value $integrityJson -Encoding UTF8

Write-Host ""
Write-Host "Wrote $($files.Count) digest(s) to:"
Write-Host "  $sha256Path"
if (-not $SkipSha512) { Write-Host "  $sha512Path" }
Write-Host "  $jsonPath"
Write-Host "  $latestArtifactPath"
Write-Host ""
Write-Host "Next step: attach a detached signature (gpg/minisign) per docs/RELEASE_SIGNING.md."
exit 0
