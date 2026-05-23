[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5088",
    [int]$TimeoutSeconds = 180,
    [int]$PollIntervalSeconds = 2,
    [switch]$ApplyHudRecommendation,
    [switch]$SkipPalworldProcessCheck,
    [string]$PalworldPath,
    [switch]$WriteToSourceMod
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

$script:NormalizedBaseUrl = Get-PalLlmNormalizedBaseUrl -BaseUrl $BaseUrl
$script:NativeProofStatusTransitions = New-Object System.Collections.Generic.List[object]

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

function Get-BridgeProof {
    return Invoke-PalApi -Method GET -Path "/api/bridge/proof"
}

function Resolve-ArtifactStatus {
    param(
        [Parameter(Mandatory = $true)]
        [object]$BridgeProof,

        [bool]$TimedOut
    )

    if ([string]::Equals([string]$BridgeProof.Status, "delivery_proven", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "proven"
    }

    if ($TimedOut) {
        return "timed_out"
    }

    if (($BridgeProof.CurrentBlockers | Measure-Object).Count -gt 0) {
        return "blocked"
    }

    return "captured"
}

function New-HistoryToken {
    param(
        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "no-request"
    }

    $invalidChars = [System.IO.Path]::GetInvalidFileNameChars()
    $builder = New-Object System.Text.StringBuilder
    foreach ($char in $Value.ToCharArray()) {
        if ($invalidChars -contains $char) {
            [void]$builder.Append('-')
        }
        elseif ([char]::IsWhiteSpace($char)) {
            [void]$builder.Append('-')
        }
        else {
            [void]$builder.Append($char)
        }
    }

    $token = $builder.ToString().Trim('-')
    if ([string]::IsNullOrWhiteSpace($token)) {
        return "no-request"
    }

    if ($token.Length -gt 48) {
        return $token.Substring(0, 48)
    }

    return $token
}

function Test-PalworldProcessCheckApplies {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl
    )

    try {
        $uri = [System.Uri]$BaseUrl
    }
    catch {
        return $false
    }

    $hostName = $uri.Host
    return [string]::Equals($hostName, "localhost", [System.StringComparison]::OrdinalIgnoreCase) `
        -or [string]::Equals($hostName, "127.0.0.1", [System.StringComparison]::OrdinalIgnoreCase) `
        -or [string]::Equals($hostName, "::1", [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-PalworldProcessActive {
    $processNames = @("Palworld-Win64-Shipping", "Palworld")
    foreach ($processName in $processNames) {
        $process = Get-Process -Name $processName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($null -ne $process) {
            return $true
        }
    }

    return $false
}

function Add-NativeProofStatusTransition {
    param(
        [AllowNull()]
        [object]$BridgeProof,

        [int]$PollIndex
    )

    if ($null -eq $BridgeProof) {
        return
    }

    $loopProof = $BridgeProof.LoopProof
    $loopStatus = ""
    $visibleDeliveryConfirmed = $false
    $deliverySurface = ""
    if ($loopProof) {
        $loopStatus = [string]$loopProof.Status
        $visibleDeliveryConfirmed = [bool]$loopProof.VisibleDeliveryConfirmed
        if ($loopProof.LastReplyDelivery) {
            $deliverySurface = [string]$loopProof.LastReplyDelivery.Surface
        }
    }

    $transition = [pscustomobject]@{
        ObservedAtUtc = [DateTimeOffset]::UtcNow
        PollIndex = [Math]::Max(0, $PollIndex)
        BridgeProofStatus = [string]$BridgeProof.Status
        Summary = [string]$BridgeProof.Summary
        ActiveRequestId = [string]$BridgeProof.ActiveRequestId
        LoopStatus = $loopStatus
        LiveDeliveryProven = [bool]$BridgeProof.LiveDeliveryProven
        NativeHudBindReady = [bool]$BridgeProof.NativeHudBindReady
        VisibleDeliveryConfirmed = $visibleDeliveryConfirmed
        DeliverySurface = $deliverySurface
    }

    if ($script:NativeProofStatusTransitions.Count -gt 0) {
        $last = $script:NativeProofStatusTransitions[$script:NativeProofStatusTransitions.Count - 1]
        if ([string]::Equals([string]$last.BridgeProofStatus, [string]$transition.BridgeProofStatus, [System.StringComparison]::Ordinal) `
            -and [string]::Equals([string]$last.Summary, [string]$transition.Summary, [System.StringComparison]::Ordinal) `
            -and [string]::Equals([string]$last.ActiveRequestId, [string]$transition.ActiveRequestId, [System.StringComparison]::Ordinal) `
            -and [string]::Equals([string]$last.LoopStatus, [string]$transition.LoopStatus, [System.StringComparison]::Ordinal) `
            -and ([bool]$last.LiveDeliveryProven -eq [bool]$transition.LiveDeliveryProven) `
            -and ([bool]$last.NativeHudBindReady -eq [bool]$transition.NativeHudBindReady) `
            -and ([bool]$last.VisibleDeliveryConfirmed -eq [bool]$transition.VisibleDeliveryConfirmed) `
            -and [string]::Equals([string]$last.DeliverySurface, [string]$transition.DeliverySurface, [System.StringComparison]::Ordinal)) {
            return
        }
    }

    if ($script:NativeProofStatusTransitions.Count -lt 32) {
        $script:NativeProofStatusTransitions.Add($transition)
    }
}

function Test-NativeHudDeliverySurface {
    param(
        [string]$Surface
    )

    if ([string]::IsNullOrWhiteSpace($Surface)) {
        return $false
    }

    $normalized = $Surface.Trim()
    return [string]::Equals($normalized, "native_hud", [System.StringComparison]::OrdinalIgnoreCase) `
        -or $normalized.StartsWith("native_hud:", [System.StringComparison]::OrdinalIgnoreCase)
}

function Resolve-NativeProofDiagnosisAction {
    param([string]$Code)

    switch ($Code) {
        "native_hud_delivery_proven" { return "Archive the current live proof and continue with the release smoke and proof-bundle lanes." }
        "palworld_process_missing" { return "Start Palworld with the UE4SS bridge loaded, then rerun native proof." }
        "bridge_boot_missing" { return "Wait for a live bridge_boot heartbeat from the UE4SS bridge before validating native delivery." }
        "ui_probe_missing" { return "Capture ui_probe widget evidence during representative gameplay before binding the HUD target." }
        "native_hud_bind_not_ready" { return "Apply or review the recommended native HUD target, then rerun native proof." }
        "native_hud_surface_mismatch" { return "Fix the HUD bind until reply_delivery reports surface=native_hud." }
        "delivery_proven_timeout" { return "Rerun native proof with more time after confirming the sidecar, bridge, and HUD bind are active." }
        "awaiting_visible_delivery" { return "Inspect the UE4SS outbox consumer and renderer for the tracked request id." }
        "native_proof_artifact_invalid" { return "Recapture native proof from a live Palworld session instead of trusting this artifact." }
        "native_proof_artifact_contradiction" { return "Recapture native proof from a live Palworld session instead of trusting this artifact." }
        "native_proof_missing" { return "Capture the first live Palworld native-proof artifact." }
        default { return "Inspect the bridge proof lanes, fix the listed blocker, and rerun native proof." }
    }
}

function Resolve-NativeProofDiagnosisCommand {
    param([string]$Code)

    switch ($Code) {
        "native_hud_delivery_proven" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-sidecar-smoke.ps1" }
        "native_hud_bind_not_ready" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -ApplyHudRecommendation" }
        "native_hud_surface_mismatch" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -ApplyHudRecommendation" }
        "delivery_proven_timeout" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -TimeoutSeconds 300" }
        "awaiting_visible_delivery" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -TimeoutSeconds 300" }
        default { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1" }
    }
}

function New-NativeProofDiagnosis {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Code,

        [Parameter(Mandatory = $true)]
        [string]$Summary
    )

    $normalizedCode = $Code.Trim().ToLowerInvariant()
    return [pscustomobject]@{
        Code = $normalizedCode
        Summary = $Summary.Trim()
        Action = Resolve-NativeProofDiagnosisAction -Code $normalizedCode
        Command = Resolve-NativeProofDiagnosisCommand -Code $normalizedCode
    }
}

function Resolve-NativeProofDiagnosis {
    param(
        [Parameter(Mandatory = $true)]
        [object]$BridgeProof,

        [Parameter(Mandatory = $true)]
        [string]$Status,

        [bool]$TimedOut,

        [string[]]$AdditionalBlockers = @()
    )

    $nativeReadiness = $BridgeProof.NativeReadiness
    $loopProof = $BridgeProof.LoopProof
    $bridgeStatus = [string]$BridgeProof.Status
    $loopStatus = ""
    $visibleDeliveryConfirmed = $false
    $deliverySurface = ""
    if ($loopProof) {
        $loopStatus = [string]$loopProof.Status
        $visibleDeliveryConfirmed = [bool]$loopProof.VisibleDeliveryConfirmed
        if ($loopProof.LastReplyDelivery) {
            $deliverySurface = [string]$loopProof.LastReplyDelivery.Surface
        }
    }

    $allBlockers = @(@($BridgeProof.CurrentBlockers) + @($AdditionalBlockers) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $blockerText = ($allBlockers -join " | ")

    if ([string]::Equals($Status, "proven", [System.StringComparison]::OrdinalIgnoreCase)) {
        return New-NativeProofDiagnosis -Code "native_hud_delivery_proven" -Summary "Live Palworld native HUD delivery has matching bridge proof."
    }

    if ($blockerText -match "Palworld process is not running") {
        return New-NativeProofDiagnosis -Code "palworld_process_missing" -Summary "The local proof watcher started before a Palworld process was visible."
    }

    if ([string]::IsNullOrWhiteSpace($bridgeStatus) -or [string]::Equals($bridgeStatus, "awaiting_bridge_boot", [System.StringComparison]::OrdinalIgnoreCase)) {
        return New-NativeProofDiagnosis -Code "bridge_boot_missing" -Summary "The sidecar has not received a live bridge_boot event from the UE4SS bridge."
    }

    if ([string]::Equals($bridgeStatus, "awaiting_ui_probe_capture", [System.StringComparison]::OrdinalIgnoreCase) -or $blockerText -match "ui_probe") {
        return New-NativeProofDiagnosis -Code "ui_probe_missing" -Summary "The live bridge has not captured enough ui_probe evidence to recommend a HUD target."
    }

    if (-not [bool]$BridgeProof.NativeHudBindReady `
        -or $blockerText -match "native_hud_render_enabled" `
        -or $blockerText -match "native_hud_widget_targets") {
        return New-NativeProofDiagnosis -Code "native_hud_bind_not_ready" -Summary "Native HUD rendering is not ready; apply or review the recommended widget target before rerunning proof."
    }

    if ($visibleDeliveryConfirmed -and -not (Test-NativeHudDeliverySurface -Surface $deliverySurface)) {
        return New-NativeProofDiagnosis -Code "native_hud_surface_mismatch" -Summary "The reply became visible through a fallback surface instead of native_hud."
    }

    if ($TimedOut) {
        return New-NativeProofDiagnosis -Code "delivery_proven_timeout" -Summary "The proof watcher timed out before bridge proof reached delivery_proven."
    }

    if ($bridgeStatus -match "awaiting_delivery" -or $loopStatus -match "awaiting_delivery") {
        return New-NativeProofDiagnosis -Code "awaiting_visible_delivery" -Summary "The sidecar has a tracked reply but has not received matching visible-delivery proof yet."
    }

    if ($allBlockers.Count -gt 0) {
        return New-NativeProofDiagnosis -Code "bridge_proof_blocked" -Summary ([string]$allBlockers[0])
    }

    return New-NativeProofDiagnosis -Code "native_proof_incomplete" -Summary "Native proof has not reached a release-blocking or proven terminal state yet."
}

function Write-NativeProofArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RuntimeRoot,

        [Parameter(Mandatory = $true)]
        [object]$BridgeProof,

        [Parameter(Mandatory = $true)]
        [string]$Status,

        [bool]$AppliedHudRecommendation,

        [string]$AppliedHudRecommendationPath,

        [string[]]$AdditionalBlockers = @(),

        [DateTimeOffset]$WatcherStartedAtUtc,

        [DateTimeOffset]$WatcherFinishedAtUtc,

        [string]$WatcherCompletionReason = "",

        [int]$TimeoutSeconds = 0,

        [int]$PollIntervalSeconds = 0,

        [int]$PollCount = 0,

        [bool]$TimedOut,

        [object[]]$StatusTransitions = @()
    )

    $releaseEvidenceDir = Join-Path $RuntimeRoot "ReleaseEvidence"
    $historyDir = Join-Path $releaseEvidenceDir "History"
    New-Item -ItemType Directory -Force -Path $historyDir | Out-Null

    $capturedAtUtc = [DateTimeOffset]::UtcNow
    $requestToken = New-HistoryToken -Value ([string]$BridgeProof.ActiveRequestId)
    $historyStamp = $capturedAtUtc.ToString("yyyyMMdd-HHmmss")
    $latestArtifactPath = Join-Path $releaseEvidenceDir "latest-native-proof.json"
    $historyArtifactPath = Join-Path $historyDir ("native-proof-{0}-{1}.json" -f $historyStamp, $requestToken)

    $nativeReadiness = $BridgeProof.NativeReadiness
    $loopProof = $BridgeProof.LoopProof
    $deliverySurface = ""
    if ($loopProof -and $loopProof.LastReplyDelivery) {
        $deliverySurface = [string]$loopProof.LastReplyDelivery.Surface
    }
    $diagnosis = Resolve-NativeProofDiagnosis `
        -BridgeProof $BridgeProof `
        -Status $Status `
        -TimedOut:$TimedOut `
        -AdditionalBlockers $AdditionalBlockers

    $artifact = [pscustomobject]@{
        Status = $Status
        Summary = [string]$BridgeProof.Summary
        CapturedAtUtc = $capturedAtUtc
        WatcherStartedAtUtc = $WatcherStartedAtUtc
        WatcherFinishedAtUtc = $WatcherFinishedAtUtc
        WatcherCompletionReason = $WatcherCompletionReason
        TimeoutSeconds = [Math]::Max(0, $TimeoutSeconds)
        PollIntervalSeconds = [Math]::Max(0, $PollIntervalSeconds)
        PollCount = [Math]::Max(0, $PollCount)
        TimedOut = $TimedOut
        DiagnosisCode = [string]$diagnosis.Code
        DiagnosisSummary = [string]$diagnosis.Summary
        DiagnosisAction = [string]$diagnosis.Action
        DiagnosisCommand = [string]$diagnosis.Command
        ArtifactPath = $latestArtifactPath
        HistoryArtifactPath = $historyArtifactPath
        BaseUrl = $script:NormalizedBaseUrl
        BridgeProofStatus = [string]$BridgeProof.Status
        ActiveRequestId = [string]$BridgeProof.ActiveRequestId
        LiveDeliveryProven = [bool]$BridgeProof.LiveDeliveryProven
        NativeHudBindReady = [bool]$BridgeProof.NativeHudBindReady
        RecommendedHudTarget = [string]$nativeReadiness.HudBindRecommendation.RecommendedTarget
        ConfiguredHudTargets = @($nativeReadiness.ConfiguredHudTargets)
        NativeHudConfigSource = [string]$nativeReadiness.NativeHudConfigSource
        NativeHudConfigPath = [string]$nativeReadiness.NativeHudConfigPath
        DeliverySurface = $deliverySurface
        LoopStatus = [string]$loopProof.Status
        VisibleDeliveryConfirmed = [bool]$loopProof.VisibleDeliveryConfirmed
        ActionFeedbackObserved = [bool]$loopProof.ActionFeedbackObserved
        AppliedHudRecommendation = $AppliedHudRecommendation
        AppliedHudRecommendationPath = $AppliedHudRecommendationPath
        RecommendedNextStep = [string]$BridgeProof.RecommendedNextStep
        CurrentBlockers = @($BridgeProof.CurrentBlockers) + @($AdditionalBlockers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        ReadyEvidence = @($BridgeProof.ReadyEvidence)
        StatusTransitions = @($StatusTransitions)
    }

    $json = ConvertTo-PalLlmJsonBody -InputObject $artifact
    Set-Content -LiteralPath $historyArtifactPath -Value $json -Encoding UTF8
    Set-Content -LiteralPath $latestArtifactPath -Value $json -Encoding UTF8

    return $artifact
}

$watcherStartedAtUtc = [DateTimeOffset]::UtcNow
$pollCount = 0

Write-Host ("Watching PalLLM native proof at {0} ..." -f $script:NormalizedBaseUrl)

$health = Invoke-PalApi -Method GET -Path "/api/health"
if ([string]::IsNullOrWhiteSpace([string]$health.RuntimeRoot)) {
    throw "The health payload did not include RuntimeRoot, so the native proof artifact could not be written."
}

$runtimeRoot = [string]$health.RuntimeRoot
$appliedHudRecommendation = $false
$appliedHudRecommendationPath = ""

$bridgeProof = Get-BridgeProof
if ($null -eq $bridgeProof -or $null -eq $bridgeProof.NativeReadiness) {
    throw "Bridge proof did not return a NativeReadiness payload. Start the PalLLM sidecar and a live Palworld bridge session, then try again."
}
Add-NativeProofStatusTransition -BridgeProof $bridgeProof -PollIndex $pollCount

$localProcessCheckApplies = -not $SkipPalworldProcessCheck -and (Test-PalworldProcessCheckApplies -BaseUrl $script:NormalizedBaseUrl)
if ($localProcessCheckApplies `
    -and -not (Test-PalworldProcessActive) `
    -and (-not [string]::Equals([string]$bridgeProof.Status, "delivery_proven", [System.StringComparison]::OrdinalIgnoreCase))) {
    $processBlocker = "Palworld process is not running on this machine. Start Palworld with the UE4SS bridge loaded, or pass -SkipPalworldProcessCheck when watching a remote sidecar."
    $artifact = Write-NativeProofArtifact `
        -RuntimeRoot $runtimeRoot `
        -BridgeProof $bridgeProof `
        -Status "blocked" `
        -AppliedHudRecommendation:$appliedHudRecommendation `
        -AppliedHudRecommendationPath $appliedHudRecommendationPath `
        -AdditionalBlockers @($processBlocker) `
        -WatcherStartedAtUtc $watcherStartedAtUtc `
        -WatcherFinishedAtUtc ([DateTimeOffset]::UtcNow) `
        -WatcherCompletionReason "palworld_process_missing" `
        -TimeoutSeconds $TimeoutSeconds `
        -PollIntervalSeconds $PollIntervalSeconds `
        -PollCount $pollCount `
        -TimedOut:$false `
        -StatusTransitions ($script:NativeProofStatusTransitions.ToArray())

    Write-Host ("Native proof blocked before polling. Artifact: {0}" -f $artifact.ArtifactPath)
    throw $processBlocker
}

if ($ApplyHudRecommendation -and -not [bool]$bridgeProof.NativeHudBindReady) {
    $applyScript = Join-Path $PSScriptRoot "apply-hud-bind-recommendation.ps1"
    if (-not (Test-Path -LiteralPath $applyScript)) {
        throw "apply-hud-bind-recommendation.ps1 was not found next to run-native-proof.ps1."
    }

    $applyArgs = @("-BaseUrl", $script:NormalizedBaseUrl)
    if ($PSBoundParameters.ContainsKey("PalworldPath") -and -not [string]::IsNullOrWhiteSpace($PalworldPath)) {
        $applyArgs += @("-PalworldPath", $PalworldPath)
    }
    if ($WriteToSourceMod) {
        $applyArgs += "-WriteToSourceMod"
    }

    $applyResult = & $applyScript @applyArgs
    $appliedHudRecommendation = $true
    $appliedHudRecommendationPath = [string]$applyResult.ConfigPath
    Write-Host ("Applied native HUD recommendation to {0}. Reload or restart Palworld if the bridge has not picked up the override yet." -f $appliedHudRecommendationPath)
}

$deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(5, $TimeoutSeconds))
$pollDelayMs = [Math]::Max(1, $PollIntervalSeconds) * 1000
$lastStatus = ""
$lastSummary = ""

while ([DateTime]::UtcNow -lt $deadline) {
    $pollCount++
    $bridgeProof = Get-BridgeProof
    Add-NativeProofStatusTransition -BridgeProof $bridgeProof -PollIndex $pollCount
    $status = [string]$bridgeProof.Status
    $summary = [string]$bridgeProof.Summary

    if ((-not [string]::Equals($status, $lastStatus, [System.StringComparison]::Ordinal)) `
        -or (-not [string]::Equals($summary, $lastSummary, [System.StringComparison]::Ordinal))) {
        Write-Host ("Bridge proof status: {0}" -f $status)
        if (-not [string]::IsNullOrWhiteSpace($summary)) {
            Write-Host ("  {0}" -f $summary)
        }
        $lastStatus = $status
        $lastSummary = $summary
    }

    if ([string]::Equals($status, "delivery_proven", [System.StringComparison]::OrdinalIgnoreCase)) {
        break
    }

    Start-Sleep -Milliseconds $pollDelayMs
}

$timedOut = -not [string]::Equals([string]$bridgeProof.Status, "delivery_proven", [System.StringComparison]::OrdinalIgnoreCase)
$artifactStatus = Resolve-ArtifactStatus -BridgeProof $bridgeProof -TimedOut:$timedOut
$watcherCompletionReason = if ($timedOut) { "delivery_proven_timeout" } else { "delivery_proven" }
$artifact = Write-NativeProofArtifact `
    -RuntimeRoot $runtimeRoot `
    -BridgeProof $bridgeProof `
    -Status $artifactStatus `
    -AppliedHudRecommendation:$appliedHudRecommendation `
    -AppliedHudRecommendationPath $appliedHudRecommendationPath `
    -WatcherStartedAtUtc $watcherStartedAtUtc `
    -WatcherFinishedAtUtc ([DateTimeOffset]::UtcNow) `
    -WatcherCompletionReason $watcherCompletionReason `
    -TimeoutSeconds $TimeoutSeconds `
    -PollIntervalSeconds $PollIntervalSeconds `
    -PollCount $pollCount `
    -TimedOut:$timedOut `
    -StatusTransitions ($script:NativeProofStatusTransitions.ToArray())

if ($timedOut) {
    $blockers = @($bridgeProof.CurrentBlockers | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $blockerText = if ($blockers.Count -gt 0) {
        $blockers -join " | "
    }
    else {
        [string]$bridgeProof.RecommendedNextStep
    }

    throw ("Native proof did not reach delivery_proven within {0}s. Final status={1}. {2}" -f $TimeoutSeconds, ([string]$bridgeProof.Status), $blockerText)
}

Write-Host "PalLLM native proof succeeded."
$artifact
