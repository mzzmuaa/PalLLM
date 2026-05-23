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

Write-Host ("Capturing PalLLM release proof bundle from {0} ..." -f $script:NormalizedBaseUrl)

$health = Invoke-PalApi -Method GET -Path "/api/health"
$releaseReadiness = Invoke-PalApi -Method GET -Path "/api/release/readiness"
$bridgeProof = Invoke-PalApi -Method GET -Path "/api/bridge/proof"
$inferencePerformance = Invoke-PalApi -Method GET -Path "/api/inference/performance"

if ([string]::IsNullOrWhiteSpace([string]$health.RuntimeRoot)) {
    throw "The health payload did not include RuntimeRoot, so the release proof bundle could not be written."
}

$runtimeRoot = [string]$health.RuntimeRoot
$releaseEvidenceDir = Join-Path $runtimeRoot "ReleaseEvidence"
$historyDir = Join-Path $releaseEvidenceDir "History"
New-Item -ItemType Directory -Force -Path $historyDir | Out-Null

$capturedAtUtc = [DateTimeOffset]::UtcNow
$historyStamp = $capturedAtUtc.ToString("yyyyMMdd-HHmmss")
$latestArtifactPath = Join-Path $releaseEvidenceDir "latest-proof-bundle.json"
$latestArchivePath = Join-Path $releaseEvidenceDir "latest-proof-bundle.zip"
$historyArtifactPath = Join-Path $historyDir ("proof-bundle-{0}.json" -f $historyStamp)
$historyArchivePath = Join-Path $historyDir ("proof-bundle-{0}.zip" -f $historyStamp)
$stageRoot = Join-Path ([IO.Path]::GetTempPath()) ("palllm-proof-bundle-" + [guid]::NewGuid().ToString("N"))
$includedFiles = [System.Collections.Generic.List[string]]::new()
$missingOptionalFiles = [System.Collections.Generic.List[string]]::new()
$currentBlockers = [System.Collections.Generic.List[string]]::new()
$readyEvidence = [System.Collections.Generic.List[string]]::new()

try {
    New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "health.json" -Payload $health
    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "release-readiness.json" -Payload $releaseReadiness
    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "bridge-proof.json" -Payload $bridgeProof
    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "inference-performance.json" -Payload $inferencePerformance

    $smokeArtifactPath = [string]$releaseReadiness.SmokeEvidence.ArtifactPath
    $nativeProofArtifactPath = [string]$releaseReadiness.NativeProofEvidence.ArtifactPath
    $fullAuditArtifactPath = [string]$releaseReadiness.FullAuditEvidence.ArtifactPath

    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $smokeArtifactPath -TargetName "latest-smoke.json")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $nativeProofArtifactPath -TargetName "latest-native-proof.json")
    [void](Copy-OptionalBundleFile -StageRoot $stageRoot -IncludedFiles $includedFiles -MissingOptionalFiles $missingOptionalFiles -SourcePath $fullAuditArtifactPath -TargetName "latest-full-audit.json")

    $nativeHudConfigPath = [string]$releaseReadiness.NativeProofEvidence.NativeHudConfigPath
    if ([string]::IsNullOrWhiteSpace($nativeHudConfigPath)) {
        $nativeHudConfigPath = [string]$releaseReadiness.SmokeEvidence.NativeHudConfigPath
    }
    if ([string]::IsNullOrWhiteSpace($nativeHudConfigPath) -and $bridgeProof.NativeReadiness) {
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
        Add-UniqueString -List $currentBlockers -Value ("Proof bundle publication scan violations: " + (@($publicationScan.Violations) -join "; "))
    }

    $smokeStatus = [string]$releaseReadiness.SmokeEvidence.Status
    $nativeProofStatus = [string]$releaseReadiness.NativeProofEvidence.Status
    $inferencePerformanceStatus = [string]$inferencePerformance.Assessment.Status
    $ttsEnabled = [bool]$health.TtsEnabled
    $ttsCallCount = [long]$health.TtsCallCount
    $ttsSuccessCount = [long]$health.TtsSuccessCount
    $ttsFailureCount = [long]$health.TtsFailureCount
    $ttsSuccessEvidenceCount = if ($ttsEnabled) { [Math]::Max([long]0, $ttsSuccessCount) } else { [long]0 }
    $asrEnabled = [bool]$health.AsrEnabled
    $asrCallCount = [long]$health.AsrCallCount
    $asrSuccessCount = [long]$health.AsrSuccessCount
    $asrFailureCount = [long]$health.AsrFailureCount
    $asrSuccessEvidenceCount = if ($asrEnabled) { [Math]::Max([long]0, $asrSuccessCount) } else { [long]0 }
    $asrEndpointingReceiptCount = [long]$health.AsrEndpointingReceiptCount
    $asrBargeInCount = [long]$health.AsrBargeInCount
    $asrEndpointingReviewCount = [long]$health.AsrEndpointingReviewCount
    $asrConfidenceReceiptCount = [long]$health.AsrConfidenceReceiptCount
    $asrConfidenceReviewCount = [long]$health.AsrConfidenceReviewCount
    $asrTimingReceiptCount = [long]$health.AsrTimingReceiptCount
    $asrTimingReviewCount = [long]$health.AsrTimingReviewCount
    $asrQualityReceiptCount = [long]$health.AsrQualityReceiptCount
    $asrQualityReviewCount = [long]$health.AsrQualityReviewCount
    $asrUpstreamRequestIdReceiptCount = [long]$health.AsrUpstreamRequestIdReceiptCount
    $asrUpstreamProcessingReceiptCount = [long]$health.AsrUpstreamProcessingReceiptCount
    $asrUpstreamPhaseTimingReceiptCount = [long]$health.AsrUpstreamPhaseTimingReceiptCount
    $inferencePerformanceLanes = @($inferencePerformance.Lanes)
    $inferencePerformanceAlertingLaneCount = @($inferencePerformanceLanes | Where-Object {
        $laneStatus = [string]$_.Assessment.Status
        [string]::Equals($laneStatus, "degraded", [System.StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($laneStatus, "critical", [System.StringComparison]::OrdinalIgnoreCase)
    }).Count
    $inferencePerformanceLatestReceiptLaneCount = @($inferencePerformanceLanes | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.LastResponseId) -or
            -not [string]::IsNullOrWhiteSpace([string]$_.LastSystemFingerprint)
    }).Count
    $inferencePerformanceTokenReceiptLaneCount = @($inferencePerformanceLanes | Where-Object {
        [long]$_.LastTotalTokens -gt 0
    }).Count
    $inferencePerformanceFinishReasonReceiptLaneCount = @($inferencePerformanceLanes | Where-Object {
        @($_.LastFinishReasons).Count -gt 0
    }).Count
    $inferencePerformanceUpstreamRequestIdReceiptLaneCount = @($inferencePerformanceLanes | Where-Object {
        -not [string]::IsNullOrWhiteSpace([string]$_.LastUpstreamRequestId)
    }).Count
    $inferencePerformanceUpstreamProcessingReceiptLaneCount = @($inferencePerformanceLanes | Where-Object {
        $null -ne $_.LastUpstreamProcessingMs
    }).Count
    $inferencePerformancePhaseTimingReceiptLaneCount = @($inferencePerformanceLanes | Where-Object {
        $null -ne $_.LastUpstreamQueueMs -or
            $null -ne $_.LastUpstreamTimeToFirstTokenMs -or
            $null -ne $_.LastUpstreamPrefillMs -or
            $null -ne $_.LastUpstreamDecodeMs
    }).Count
    $inferencePerformanceUsageDetailReceiptLaneCount = @($inferencePerformanceLanes | Where-Object {
        [long]$_.LastCachedPromptTokens -gt 0 -or
            [long]$_.LastPromptAudioTokens -gt 0 -or
            [long]$_.LastCompletionReasoningTokens -gt 0 -or
            [long]$_.LastCompletionAudioTokens -gt 0 -or
            [long]$_.LastAcceptedPredictionTokens -gt 0 -or
            [long]$_.LastRejectedPredictionTokens -gt 0
    }).Count
    $bundleStatus = if (-not $publicationScanPassed) {
        "invalid"
    }
    elseif (
            [string]::Equals($smokeStatus, "recorded", [System.StringComparison]::OrdinalIgnoreCase) -and
            [string]::Equals($nativeProofStatus, "proven", [System.StringComparison]::OrdinalIgnoreCase)
        ) {
        "recorded"
    }
    else {
        "partial"
    }

    if (-not [string]::Equals($smokeStatus, "recorded", [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-UniqueString -List $currentBlockers -Value ("Smoke evidence status is '" + $smokeStatus + "'.")
    }

    if (-not [string]::Equals($nativeProofStatus, "proven", [System.StringComparison]::OrdinalIgnoreCase)) {
        Add-UniqueString -List $currentBlockers -Value ("Native proof evidence status is '" + $nativeProofStatus + "'.")
    }

    $summary = if ([string]::Equals($bundleStatus, "recorded", [System.StringComparison]::OrdinalIgnoreCase)) {
        "Palworld release proof bundle captured the current release/readiness snapshot, bridge proof, smoke artifact, and live native-proof artifact."
    }
    elseif ([string]::Equals($bundleStatus, "invalid", [System.StringComparison]::OrdinalIgnoreCase)) {
        "Palworld release proof bundle captured the current runtime snapshots, but the portable publication scan found text-surface blockers."
    }
    else {
        "Palworld release proof bundle captured the current runtime snapshots, but at least one supporting smoke/native-proof artifact is still missing or incomplete."
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
        ReleasePublicationStatus = [string]$releaseReadiness.Publication.Status
        BridgeProofStatus = [string]$bridgeProof.Status
        SmokeEvidenceStatus = $smokeStatus
        NativeProofEvidenceStatus = $nativeProofStatus
        InferencePerformanceStatus = $inferencePerformanceStatus
        InferencePerformanceSampleCount = [int]$inferencePerformance.SampleCount
        InferencePerformanceLaneCount = [int]$inferencePerformanceLanes.Count
        InferencePerformanceAlertingLaneCount = [int]$inferencePerformanceAlertingLaneCount
        InferencePerformanceLatestReceiptLaneCount = [int]$inferencePerformanceLatestReceiptLaneCount
        InferencePerformanceTokenReceiptLaneCount = [int]$inferencePerformanceTokenReceiptLaneCount
        InferencePerformanceFinishReasonReceiptLaneCount = [int]$inferencePerformanceFinishReasonReceiptLaneCount
        InferencePerformanceUpstreamRequestIdReceiptLaneCount = [int]$inferencePerformanceUpstreamRequestIdReceiptLaneCount
        InferencePerformanceUpstreamProcessingReceiptLaneCount = [int]$inferencePerformanceUpstreamProcessingReceiptLaneCount
        InferencePerformancePhaseTimingReceiptLaneCount = [int]$inferencePerformancePhaseTimingReceiptLaneCount
        InferencePerformanceUsageDetailReceiptLaneCount = [int]$inferencePerformanceUsageDetailReceiptLaneCount
        InferencePerformanceTotalTokens = [long]$inferencePerformance.TotalTokens
        InferencePerformanceCachedPromptTokens = [long]$inferencePerformance.TotalCachedPromptTokens
        InferencePerformanceCompletionReasoningTokens = [long]$inferencePerformance.TotalCompletionReasoningTokens
        TtsEnabled = $ttsEnabled
        TtsCallCount = $ttsCallCount
        TtsFailureCount = $ttsFailureCount
        TtsSuccessEvidenceCount = $ttsSuccessEvidenceCount
        AsrEnabled = $asrEnabled
        AsrCallCount = $asrCallCount
        AsrFailureCount = $asrFailureCount
        AsrSuccessEvidenceCount = $asrSuccessEvidenceCount
        AsrEndpointingReceiptCount = $asrEndpointingReceiptCount
        AsrBargeInCount = $asrBargeInCount
        AsrEndpointingReviewCount = $asrEndpointingReviewCount
        AsrConfidenceReceiptCount = $asrConfidenceReceiptCount
        AsrConfidenceReviewCount = $asrConfidenceReviewCount
        AsrTimingReceiptCount = $asrTimingReceiptCount
        AsrTimingReviewCount = $asrTimingReviewCount
        AsrQualityReceiptCount = $asrQualityReceiptCount
        AsrQualityReviewCount = $asrQualityReviewCount
        AsrUpstreamRequestIdReceiptCount = $asrUpstreamRequestIdReceiptCount
        AsrUpstreamProcessingReceiptCount = $asrUpstreamProcessingReceiptCount
        AsrUpstreamPhaseTimingReceiptCount = $asrUpstreamPhaseTimingReceiptCount
        NativeHudConfigSource = [string]$releaseReadiness.NativeProofEvidence.NativeHudConfigSource
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

    Write-BundleJsonFile -StageRoot $stageRoot -IncludedFiles $includedFiles -TargetName "proof-bundle.json" -Payload $manifest
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
    LatestProofBundleArtifact = $latestArtifactPath
    LatestProofBundleArchive = $latestArchivePath
    ProofBundleHistoryArtifact = $historyArtifactPath
    ProofBundleHistoryArchive = $historyArchivePath
    IncludedFiles = @($includedFiles)
    MissingOptionalFiles = @($missingOptionalFiles)
}

Write-Host "PalLLM release proof bundle captured."
$result
