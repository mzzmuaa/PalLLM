[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5088"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

$script:NormalizedBaseUrl = Get-PalLlmNormalizedBaseUrl -BaseUrl $BaseUrl

function Invoke-PalApi {
    param(
        [Parameter(Mandatory = $true)]
        [ValidateSet("GET", "POST")]
        [string]$Method,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [object]$Body
    )

    if ($PSBoundParameters.ContainsKey("Body")) {
        return Invoke-PalLlmApi -Method $Method -BaseUrl $script:NormalizedBaseUrl -Path $Path -Body $Body
    }

    return Invoke-PalLlmApi -Method $Method -BaseUrl $script:NormalizedBaseUrl -Path $Path
}

function Write-BundleJsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StageRoot,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$IncludedFiles,

        [Parameter(Mandatory = $true)]
        [string]$TargetName,

        [Parameter(Mandatory = $true)]
        [object]$Payload
    )

    $targetPath = Join-Path $StageRoot $TargetName
    Set-Content -LiteralPath $targetPath -Value (ConvertTo-PalLlmJsonBody -InputObject $Payload) -Encoding UTF8
    Add-UniqueString -List $IncludedFiles -Value $TargetName
}

function Copy-OptionalBundleFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$StageRoot,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$IncludedFiles,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$MissingOptionalFiles,

        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$TargetName
    )

    if ([string]::IsNullOrWhiteSpace($SourcePath) -or -not (Test-Path -LiteralPath $SourcePath)) {
        Add-UniqueString -List $MissingOptionalFiles -Value $TargetName
        return $false
    }

    Copy-Item -LiteralPath $SourcePath -Destination (Join-Path $StageRoot $TargetName) -Force
    Add-UniqueString -List $IncludedFiles -Value $TargetName
    return $true
}

function Add-UniqueStringsFromSource {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$List,

        [object[]]$Values
    )

    foreach ($value in @($Values)) {
        Add-UniqueString -List $List -Value ([string]$value)
    }
}

Write-Host ("Capturing PalLLM support bundle from {0} ..." -f $script:NormalizedBaseUrl)

$health = Invoke-PalApi -Method GET -Path "/api/health"
$releaseReadiness = Invoke-PalApi -Method GET -Path "/api/release/readiness"
$bridgeProof = Invoke-PalApi -Method GET -Path "/api/bridge/proof"

if ([string]::IsNullOrWhiteSpace([string]$health.RuntimeRoot)) {
    throw "The health payload did not include RuntimeRoot, so the support bundle could not be written."
}

$runtimeRoot = [string]$health.RuntimeRoot
$supportEvidenceDir = Join-Path $runtimeRoot "SupportEvidence"
$historyDir = Join-Path $supportEvidenceDir "History"
New-Item -ItemType Directory -Force -Path $historyDir | Out-Null

$capturedAtUtc = [DateTimeOffset]::UtcNow
$historyStamp = $capturedAtUtc.ToString("yyyyMMdd-HHmmss")
$latestArtifactPath = Join-Path $supportEvidenceDir "latest-support-bundle.json"
$latestArchivePath = Join-Path $supportEvidenceDir "latest-support-bundle.zip"
$historyArtifactPath = Join-Path $historyDir ("support-bundle-{0}.json" -f $historyStamp)
$historyArchivePath = Join-Path $historyDir ("support-bundle-{0}.zip" -f $historyStamp)
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ("palllm-support-bundle-" + [guid]::NewGuid().ToString("N"))
$includedFiles = [System.Collections.Generic.List[string]]::new()
$missingOptionalFiles = [System.Collections.Generic.List[string]]::new()
$currentBlockers = [System.Collections.Generic.List[string]]::new()
$readyEvidence = [System.Collections.Generic.List[string]]::new()

try {
    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "health.json" -Payload $health
    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "release-readiness.json" -Payload $releaseReadiness
    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "bridge-proof.json" -Payload $bridgeProof

    $launchEvidenceDir = Join-Path $runtimeRoot "LaunchEvidence"
    $launchArtifactPath = Join-Path $launchEvidenceDir "latest-player-launch.json"
    $launchMarkdownPath = Join-Path $launchEvidenceDir "latest-player-launch.md"
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $launchArtifactPath -TargetName "latest-player-launch.json")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $launchMarkdownPath -TargetName "latest-player-launch.md")

    $smokeArtifactPath = [string]$releaseReadiness.SmokeEvidence.ArtifactPath
    $nativeProofArtifactPath = [string]$releaseReadiness.NativeProofEvidence.ArtifactPath
    $proofBundleArtifactPath = [string]$releaseReadiness.ProofBundleEvidence.ArtifactPath
    $proofBundleArchivePath = [string]$releaseReadiness.ProofBundleEvidence.ArchivePath
    $packageVerificationArtifactPath = [string]$releaseReadiness.PackageVerificationEvidence.ArtifactPath
    $fullAuditArtifactPath = [string]$releaseReadiness.FullAuditEvidence.ArtifactPath

    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $smokeArtifactPath -TargetName "latest-smoke.json")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $nativeProofArtifactPath -TargetName "latest-native-proof.json")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $proofBundleArtifactPath -TargetName "latest-proof-bundle.json")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $proofBundleArchivePath -TargetName "latest-proof-bundle.zip")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $packageVerificationArtifactPath -TargetName "latest-package-verification.json")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $fullAuditArtifactPath -TargetName "latest-full-audit.json")

    $nativeHudConfigPath = [string]$releaseReadiness.NativeProofEvidence.NativeHudConfigPath
    if ([string]::IsNullOrWhiteSpace($nativeHudConfigPath)) {
        $nativeHudConfigPath = [string]$bridgeProof.NativeReadiness.NativeHudConfigPath
    }
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $nativeHudConfigPath -TargetName "native-hud.lua")

    Add-UniqueStringsFromSource -List $currentBlockers -Values @($releaseReadiness.Publication.CurrentBlockers)
    Add-UniqueStringsFromSource -List $currentBlockers -Values @($bridgeProof.CurrentBlockers)
    Add-UniqueStringsFromSource -List $readyEvidence -Values @($bridgeProof.ReadyEvidence)

    $privacyRedaction = Protect-PalLlmPortableTextSurface -RootPath $stageRoot
    $publicationScan = Test-PalLlmPublicationTextSurface -RootPath $stageRoot
    $publicationScanPassed = (@($publicationScan.Violations).Count -eq 0)
    if (-not $publicationScanPassed) {
        Add-UniqueString -List $currentBlockers -Value ("Support bundle publication scan violations: " + (@($publicationScan.Violations) -join "; "))
    }

    $launchArtifactPresent = (Test-Path -LiteralPath $launchArtifactPath)
    $launchStatus = if ($launchArtifactPresent) { "recorded" } else { "missing" }
    if (-not $launchArtifactPresent) {
        Add-UniqueString -List $currentBlockers -Value "Latest player launch evidence is missing."
    }

    $bundleStatus = if ($publicationScanPassed) { "recorded" } else { "invalid" }
    $summary = if (-not $publicationScanPassed) {
        "PalLLM support bundle captured the latest runtime snapshots, but the portable publication scan found text-surface blockers."
    }
    elseif ($missingOptionalFiles.Count -eq 0) {
        "PalLLM support bundle captured the latest launch, proof, and release-readiness evidence."
    }
    else {
        "PalLLM support bundle captured the latest runtime snapshots, but one or more optional support artifacts were missing."
    }

    $manifest = [pscustomobject]@{
        Status = $bundleStatus
        Summary = $summary
        CapturedAtUtc = $capturedAtUtc
        ArtifactPath = $latestArtifactPath
        HistoryArtifactPath = $historyArtifactPath
        ArchivePath = $latestArchivePath
        HistoryArchivePath = $historyArchivePath
        BaseUrl = $script:NormalizedBaseUrl
        RuntimeRoot = $runtimeRoot
        LaunchEvidenceStatus = $launchStatus
        SmokeEvidenceStatus = [string]$releaseReadiness.SmokeEvidence.Status
        NativeProofEvidenceStatus = [string]$releaseReadiness.NativeProofEvidence.Status
        ProofBundleEvidenceStatus = [string]$releaseReadiness.ProofBundleEvidence.Status
        PackageVerificationEvidenceStatus = [string]$releaseReadiness.PackageVerificationEvidence.Status
        FullAuditEvidenceStatus = [string]$releaseReadiness.FullAuditEvidence.Status
        NativeHudConfigPath = $nativeHudConfigPath
        PrivacyRedactionApplied = $true
        PrivacyRedactionCheckedFileCount = [int]$privacyRedaction.CheckedFileCount
        PrivacyRedactionRedactedFileCount = [int]$privacyRedaction.RedactedFileCount
        PrivacyRedactionRuleHits = @($privacyRedaction.RuleHits)
        PublicationScanPassed = $publicationScanPassed
        PublicationScanCheckedFileCount = [int]$publicationScan.CheckedFileCount
        PublicationScanViolations = @($publicationScan.Violations)
        IncludedFiles = @($includedFiles)
        MissingOptionalFiles = @($missingOptionalFiles)
        CurrentBlockers = @($currentBlockers)
        ReadyEvidence = @($readyEvidence)
    }

    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "support-bundle.json" -Payload $manifest
    # The recorded manifest keeps local filesystem paths so release-readiness
    # can re-open the paired archive. The portable copy inside the zip is
    # redacted before compression because that is the file a tester may share.
    [void](Protect-PalLlmPortableTextSurface -RootPath $stageRoot)

    if (Test-Path -LiteralPath $historyArchivePath) {
        Remove-Item -LiteralPath $historyArchivePath -Force
    }
    Compress-Archive -Path (Join-Path $stageRoot "*") -DestinationPath $historyArchivePath -CompressionLevel Optimal -Force

    $manifestJson = ConvertTo-PalLlmJsonBody -InputObject $manifest
    Set-Content -LiteralPath $historyArtifactPath -Value $manifestJson -Encoding UTF8
    Set-Content -LiteralPath $latestArtifactPath -Value $manifestJson -Encoding UTF8
    Copy-Item -LiteralPath $historyArchivePath -Destination $latestArchivePath -Force
}
finally {
    if (Test-Path -LiteralPath $stageRoot) {
        Remove-Item -LiteralPath $stageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$result = [pscustomobject]@{
    Status = [string]$manifest.Status
    Summary = [string]$manifest.Summary
    BaseUrl = $script:NormalizedBaseUrl
    RuntimeRoot = $runtimeRoot
    LatestSupportBundleArtifact = $latestArtifactPath
    LatestSupportBundleArchive = $latestArchivePath
    SupportBundleHistoryArtifact = $historyArtifactPath
    SupportBundleHistoryArchive = $historyArchivePath
    IncludedFiles = @($includedFiles)
    MissingOptionalFiles = @($missingOptionalFiles)
}

Write-Host "PalLLM support bundle captured."
$result
