[CmdletBinding()]
param(
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

function Resolve-PackageCandidate {
    param(
        [string]$PackagePath
    )

    if (-not [string]::IsNullOrWhiteSpace($PackagePath)) {
        $resolved = Resolve-ExistingPath -Path $PackagePath
        if (-not $resolved) {
            throw "Package path does not exist: $PackagePath"
        }

        return $resolved
    }

    $packagingRoot = Join-Path (Get-PalLlmRepoRoot) "artifacts\packaging"
    if (-not (Test-Path -LiteralPath $packagingRoot)) {
        throw "No package path was supplied and artifacts\packaging does not exist yet."
    }

    $latestZip = Get-ChildItem -LiteralPath $packagingRoot -Filter "*.zip" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($latestZip) {
        return $latestZip.FullName
    }

    $latestDirectory = Get-ChildItem -LiteralPath $packagingRoot -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($latestDirectory) {
        return $latestDirectory.FullName
    }

    throw "No release package candidate was found under $packagingRoot. Run scripts/package-release.ps1 first or pass -PackagePath."
}

function ConvertTo-PackageRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageRoot,

        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    return ConvertTo-PalLlmRelativePath -RootPath $PackageRoot -FilePath $FilePath
}

function Resolve-ExpandedPackageRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExpandedRoot
    )

    $manifestCandidates = @(Get-ChildItem -LiteralPath $ExpandedRoot -Recurse -Filter "RELEASE_PACKAGE_MANIFEST.json" -File -ErrorAction SilentlyContinue)
    if (-not $manifestCandidates) {
        throw "RELEASE_PACKAGE_MANIFEST.json was not found under $ExpandedRoot."
    }

    if ($manifestCandidates.Count -gt 1) {
        throw "Multiple RELEASE_PACKAGE_MANIFEST.json files were found under $ExpandedRoot. Package shape is ambiguous."
    }

    $manifestPath = $manifestCandidates[0].FullName
    return [pscustomobject]@{
        PackageRoot = Split-Path -Parent $manifestPath
        ManifestPath = $manifestPath
        ManifestRelativePath = ConvertTo-PackageRelativePath -PackageRoot (Split-Path -Parent $manifestPath) -FilePath $manifestPath
    }
}

function Get-ManifestEntryMap {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$ManifestFiles
    )

    $map = @{}
    foreach ($entry in @($ManifestFiles)) {
        $relativePath = [string]$entry.Path
        if ([string]::IsNullOrWhiteSpace($relativePath)) {
            continue
        }

        $map[$relativePath.Trim()] = $entry
    }

    return $map
}

function Test-PackagePublicationSurface {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageRoot
    )

    return Test-PalLlmPublicationTextSurface `
        -RootPath $PackageRoot `
        -RootBrandMinimalFiles @("PLAYER_README.txt", "CHANGELOG.md", "README.md") `
        -ScannerFiles @("scripts/verify-release-package.ps1", "scripts/PalLLM.Tooling.ps1")
}

function Write-PackageVerificationArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Artifact
    )

    $runtimeRoot = Get-PalLlmRuntimeRoot
    $releaseEvidenceDir = Join-Path $runtimeRoot "ReleaseEvidence"
    $historyDir = Join-Path $releaseEvidenceDir "History"
    New-Item -ItemType Directory -Force -Path $historyDir | Out-Null

    $capturedAtUtc = [DateTimeOffset]::UtcNow
    $historyStamp = $capturedAtUtc.ToString("yyyyMMdd-HHmmss")
    $latestArtifactPath = Join-Path $releaseEvidenceDir "latest-package-verification.json"
    $historyArtifactPath = Join-Path $historyDir ("package-verification-{0}.json" -f $historyStamp)

    $Artifact.CapturedAtUtc = $capturedAtUtc
    $Artifact.ArtifactPath = $latestArtifactPath
    $Artifact.HistoryArtifactPath = $historyArtifactPath

    $json = ConvertTo-PalLlmJsonBody -InputObject $Artifact
    Set-Content -LiteralPath $historyArtifactPath -Value $json -Encoding UTF8
    Set-Content -LiteralPath $latestArtifactPath -Value $json -Encoding UTF8

    return [pscustomobject]@{
        LatestPackageVerificationArtifact = $latestArtifactPath
        PackageVerificationHistoryArtifact = $historyArtifactPath
    }
}

$candidatePath = Resolve-PackageCandidate -PackagePath $PackagePath
$packageItem = Get-Item -LiteralPath $candidatePath
$packageKind = if ($packageItem.PSIsContainer) { "expanded_directory" } else { "zip_archive" }
$packageRoot = $null
$manifestPath = $null
$expandedRoot = $null

try {
    if ($packageKind -eq "zip_archive") {
        $expandedRoot = Join-Path ([IO.Path]::GetTempPath()) ("palllm-package-verify-" + [guid]::NewGuid().ToString("N"))
        Expand-Archive -LiteralPath $candidatePath -DestinationPath $expandedRoot -Force
        $resolvedPackage = Resolve-ExpandedPackageRoot -ExpandedRoot $expandedRoot
    }
    else {
        $resolvedPackage = Resolve-ExpandedPackageRoot -ExpandedRoot $candidatePath
    }

    $packageRoot = $resolvedPackage.PackageRoot
    $manifestPath = $resolvedPackage.ManifestPath
    $manifestRelativePath = $resolvedPackage.ManifestRelativePath

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($null -eq $manifest) {
        throw "RELEASE_PACKAGE_MANIFEST.json could not be parsed."
    }

    $manifestSchemaVersion = [int]$manifest.SchemaVersion
    if ($manifestSchemaVersion -lt 1) {
        throw "RELEASE_PACKAGE_MANIFEST.json did not include a supported SchemaVersion."
    }

    $manifestFiles = @($manifest.Files)
    if ($manifestFiles.Count -eq 0) {
        throw "RELEASE_PACKAGE_MANIFEST.json did not contain any Files entries."
    }

    $actualFileMap = @{}
    Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        ForEach-Object {
            $relativePath = ConvertTo-PackageRelativePath -PackageRoot $packageRoot -FilePath $_.FullName
            $actualFileMap[$relativePath] = $_
        }

    $manifestEntryMap = Get-ManifestEntryMap -ManifestFiles $manifestFiles
    $manifestPaths = @($manifestEntryMap.Keys | Sort-Object)
    $actualPaths = @($actualFileMap.Keys | Sort-Object)
    $requiredPaths = @($manifest.RequiredPaths | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Sort-Object -Unique)
    $publicationScan = Test-PackagePublicationSurface -PackageRoot $packageRoot

    $missingRequiredFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($requiredPath in $requiredPaths) {
        if (-not $actualFileMap.ContainsKey($requiredPath)) {
            Add-UniqueString -List $missingRequiredFiles -Value $requiredPath
        }
    }

    $unexpectedFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($actualPath in $actualPaths) {
        if (-not $manifestEntryMap.ContainsKey($actualPath)) {
            Add-UniqueString -List $unexpectedFiles -Value $actualPath
        }
    }

    $mismatchedFiles = [System.Collections.Generic.List[string]]::new()
    foreach ($manifestPathEntry in $manifestPaths) {
        if (-not $actualFileMap.ContainsKey($manifestPathEntry)) {
            Add-UniqueString -List $mismatchedFiles -Value $manifestPathEntry
            continue
        }

        $manifestEntry = $manifestEntryMap[$manifestPathEntry]
        $actualFile = $actualFileMap[$manifestPathEntry]
        $expectedSize = [int64]$manifestEntry.SizeBytes
        $expectedHash = [string]$manifestEntry.Sha256
        $actualHash = (Get-FileHash -LiteralPath $actualFile.FullName -Algorithm SHA256).Hash

        if ($actualFile.Length -ne $expectedSize -or -not [string]::Equals($actualHash, $expectedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
            Add-UniqueString -List $mismatchedFiles -Value $manifestPathEntry
        }
    }

    $currentBlockers = [System.Collections.Generic.List[string]]::new()
    if ($missingRequiredFiles.Count -gt 0) {
        Add-UniqueString -List $currentBlockers -Value ("Missing required files: " + ($missingRequiredFiles -join ", "))
    }
    if ($unexpectedFiles.Count -gt 0) {
        Add-UniqueString -List $currentBlockers -Value ("Unexpected files not declared in the manifest: " + ($unexpectedFiles -join ", "))
    }
    if ($mismatchedFiles.Count -gt 0) {
        Add-UniqueString -List $currentBlockers -Value ("Manifest hash or size mismatch: " + ($mismatchedFiles -join ", "))
    }
    if (@($publicationScan.Violations).Count -gt 0) {
        Add-UniqueString -List $currentBlockers -Value ("Publication scan violations: " + (@($publicationScan.Violations) -join "; "))
    }

    $readyEvidence = [System.Collections.Generic.List[string]]::new()
    if ($currentBlockers.Count -eq 0) {
        Add-UniqueString -List $readyEvidence -Value "Package manifest parsed successfully."
        Add-UniqueString -List $readyEvidence -Value ("Validated " + $manifestFiles.Count + " manifest-declared files.")
        Add-UniqueString -List $readyEvidence -Value ("Package publication surface scan passed across " + $publicationScan.CheckedFileCount + " text files.")
        if ([bool]$manifest.IncludesSidecarPublish) {
            Add-UniqueString -List $readyEvidence -Value "Package includes a published sidecar payload."
        }
        else {
            Add-UniqueString -List $readyEvidence -Value "Package is source-driven and expects the sidecar to run from source."
        }
    }

    $status = if ($currentBlockers.Count -eq 0) { "verified" } else { "invalid" }
    $summary = if ($status -eq "verified") {
        "PalLLM release package verified successfully against RELEASE_PACKAGE_MANIFEST.json."
    }
    else {
        "PalLLM release package verification failed. Inspect the current blockers before trusting this candidate package."
    }

    $artifact = [ordered]@{
        Status = $status
        Summary = $summary
        PackagePath = $candidatePath
        PackageKind = $packageKind
        ReleaseName = [string]$manifest.ReleaseName
        ManifestRelativePath = $manifestRelativePath
        ManifestSchemaVersion = $manifestSchemaVersion
        PackageSha256 = if ($packageKind -eq "zip_archive") { (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash } else { "" }
        VerifiedFromArchive = ($packageKind -eq "zip_archive")
        IncludesSidecarPublish = [bool]$manifest.IncludesSidecarPublish
        SelfContainedSidecar = [bool]$manifest.SelfContained
        RequiredFilesPresent = ($missingRequiredFiles.Count -eq 0)
        CheckedFileCount = $manifestFiles.Count
        PublicationScanPassed = (@($publicationScan.Violations).Count -eq 0)
        PublicationScanCheckedFileCount = [int]$publicationScan.CheckedFileCount
        PublicationScanViolations = @($publicationScan.Violations)
        MissingRequiredFiles = @($missingRequiredFiles)
        UnexpectedFiles = @($unexpectedFiles)
        MismatchedFiles = @($mismatchedFiles)
        CurrentBlockers = @($currentBlockers)
        ReadyEvidence = @($readyEvidence)
    }

    $artifactWrite = Write-PackageVerificationArtifact -Artifact $artifact

    $result = [pscustomobject]@{
        Status = $status
        Summary = $summary
        PackagePath = $candidatePath
        PackageKind = $packageKind
        ReleaseName = [string]$manifest.ReleaseName
        LatestPackageVerificationArtifact = $artifactWrite.LatestPackageVerificationArtifact
        PackageVerificationHistoryArtifact = $artifactWrite.PackageVerificationHistoryArtifact
        CurrentBlockers = @($currentBlockers)
        ReadyEvidence = @($readyEvidence)
    }

    if ($status -ne "verified") {
        throw ($summary + " " + ($currentBlockers -join " "))
    }

    Write-Host ("PalLLM package verification passed for {0}." -f $candidatePath)
    $result
}
finally {
    if ($expandedRoot -and (Test-Path -LiteralPath $expandedRoot)) {
        Remove-Item -LiteralPath $expandedRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
