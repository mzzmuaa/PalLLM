<#
.SYNOPSIS
    Summarize PalLLM's native proof state without waiting for a live session.

.DESCRIPTION
    Reads the live /api/bridge/proof endpoint when the sidecar is reachable,
    falls back to Runtime/ReleaseEvidence/latest-native-proof.json when it is
    not, and prints the current proof lanes plus the single next command.

    This is read-only. It does not launch Palworld, write HUD config, build,
    audit, or package. Use scripts/run-native-proof.ps1 for the active watcher
    that can produce a new latest-native-proof.json artifact.

.PARAMETER BaseUrl
    Sidecar URL to probe. Default http://localhost:5088.

.PARAMETER Json
    Emit a structured JSON record instead of human-readable text.

.PARAMETER RequireProven
    Return exit code 1 unless live delivery is already proven. Useful for a
    release lane that wants to gate on existing evidence.

.EXAMPLE
    pwsh ./scripts/pal-proof.ps1
    # Print current native proof status and the next action.

.EXAMPLE
    pwsh ./scripts/pal-proof.ps1 -Json
    # Emit machine-readable proof status.

.NOTES
    Verb shortcut: pal proof
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [switch]$Json,
    [switch]$RequireProven
)

$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolingPath = Join-Path $PSScriptRoot 'PalLLM.Tooling.ps1'
if (Test-Path -LiteralPath $toolingPath) {
    . $toolingPath
}

function Get-LocalRuntimeRoot {
    if (Get-Command -Name Get-PalLlmRuntimeRoot -ErrorAction SilentlyContinue) {
        return Get-PalLlmRuntimeRoot
    }

    return (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal\Saved\PalLLM')
}

function Get-NormalizedBaseUrl {
    param([Parameter(Mandatory = $true)][string]$Value)

    if (Get-Command -Name Get-PalLlmNormalizedBaseUrl -ErrorAction SilentlyContinue) {
        return Get-PalLlmNormalizedBaseUrl -BaseUrl $Value
    }

    return $Value.TrimEnd('/')
}

function Read-JsonFileOrNull {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Invoke-ProofGet {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSeconds = 3
    )

    $uri = "{0}{1}" -f $script:NormalizedBaseUrl, $Path
    try {
        $data = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec $TimeoutSeconds -ErrorAction Stop
        return [pscustomobject]@{
            Reachable = $true
            Uri = $uri
            Error = $null
            Data = $data
        }
    } catch {
        return [pscustomobject]@{
            Reachable = $false
            Uri = $uri
            Error = $_.Exception.Message
            Data = $null
        }
    }
}

function Get-PropertyOrNull {
    param(
        [AllowNull()][object]$InputObject,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function ConvertTo-StringArray {
    param([AllowNull()][object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [string]) {
        if ([string]::IsNullOrWhiteSpace($Value)) {
            return @()
        }

        return @($Value)
    }

    if ($Value -is [System.Collections.IEnumerable]) {
        $items = New-Object System.Collections.Generic.List[string]
        foreach ($item in $Value) {
            $text = [string]$item
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $items.Add($text)
            }
        }

        return @($items.ToArray())
    }

    $single = [string]$Value
    if ([string]::IsNullOrWhiteSpace($single)) {
        return @()
    }

    return @($single)
}

function New-ProofLane {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$Passed,
        [AllowEmptyString()][string]$Summary = '',
        [bool]$Required = $true
    )

    if ([string]::IsNullOrWhiteSpace($Summary)) {
        $Summary = '(none)'
    }

    return [pscustomobject]@{
        Name = $Name
        Required = $Required
        Status = if ($Passed) { 'PASS' } elseif ($Required) { 'FAIL' } else { 'WARN' }
        Summary = $Summary
    }
}

function Resolve-DiagnosisAction {
    param([string]$Code)

    switch ($Code) {
        "native_hud_delivery_proven" { return "Archive the current live proof and continue with the release smoke and proof-bundle lanes." }
        "palworld_process_missing" { return "Start Palworld with the UE4SS bridge loaded, then rerun native proof." }
        "sidecar_offline" { return "Start the PalLLM sidecar before reading live bridge proof." }
        "bridge_proof_unavailable" { return "Run doctor against the sidecar and inspect why /api/bridge/proof is unavailable." }
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

function Resolve-DiagnosisCommand {
    param([string]$Code)

    switch ($Code) {
        "native_hud_delivery_proven" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-sidecar-smoke.ps1" }
        "sidecar_offline" { return "pwsh ./pal.ps1 play" }
        "bridge_proof_unavailable" { return "pwsh ./pal.ps1 doctor" }
        "native_hud_bind_not_ready" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -ApplyHudRecommendation" }
        "native_hud_surface_mismatch" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -ApplyHudRecommendation" }
        "delivery_proven_timeout" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -TimeoutSeconds 300" }
        "awaiting_visible_delivery" { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -TimeoutSeconds 300" }
        default { return "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1" }
    }
}

function Resolve-NativeHudBindReady {
    param(
        [AllowNull()][object]$ProofPayload,
        [AllowNull()][object]$NativeReadiness
    )

    if ($NativeReadiness) {
        $hudBindReady = Get-PropertyOrNull -InputObject $NativeReadiness -Name 'HudBindReady'
        if ($null -ne $hudBindReady) {
            return [bool]$hudBindReady
        }
    }

    return [bool](Get-PropertyOrNull -InputObject $ProofPayload -Name 'NativeHudBindReady')
}

function Resolve-FreshnessWindowHours {
    param([AllowNull()][object]$ProofPayload)

    $value = Get-PropertyOrNull -InputObject $ProofPayload -Name 'FreshnessWindowHours'
    if ($null -ne $value) {
        try {
            $hours = [int]$value
            if ($hours -gt 0) {
                return $hours
            }
        } catch {
            # Fall through to the release-readiness default.
        }
    }

    return 24
}

function Resolve-EvidenceFreshness {
    param(
        [Parameter(Mandatory = $true)][string]$ProofSource,
        [AllowNull()][object]$ProofPayload
    )

    $windowHours = Resolve-FreshnessWindowHours -ProofPayload $ProofPayload
    $capturedAtText = [string](Get-PropertyOrNull -InputObject $ProofPayload -Name 'CapturedAtUtc')
    if ([string]::IsNullOrWhiteSpace($capturedAtText)) {
        $capturedAtText = [string](Get-PropertyOrNull -InputObject $ProofPayload -Name 'GeneratedAtUtc')
    }

    if ([string]::Equals($ProofSource, 'live-bridge-proof', [System.StringComparison]::OrdinalIgnoreCase)) {
        return [pscustomobject]@{
            Status = 'live'
            CapturedAtUtc = if ([string]::IsNullOrWhiteSpace($capturedAtText)) { $null } else { $capturedAtText }
            FreshUntilUtc = $null
            FreshnessWindowHours = $windowHours
            Summary = 'Live bridge proof is being read directly from the running sidecar.'
        }
    }

    if ([string]::IsNullOrWhiteSpace($capturedAtText)) {
        return [pscustomobject]@{
            Status = 'unknown'
            CapturedAtUtc = $null
            FreshUntilUtc = $null
            FreshnessWindowHours = $windowHours
            Summary = 'No capture timestamp was present on the durable proof payload.'
        }
    }

    try {
        $capturedAt = [DateTimeOffset]::Parse($capturedAtText, [System.Globalization.CultureInfo]::InvariantCulture)
        $freshUntil = $capturedAt.ToUniversalTime().AddHours($windowHours)
        $now = [DateTimeOffset]::UtcNow
        $status = if ($now -le $freshUntil) { 'fresh' } else { 'stale' }

        return [pscustomobject]@{
            Status = $status
            CapturedAtUtc = $capturedAt.ToUniversalTime().ToString('o')
            FreshUntilUtc = $freshUntil.ToString('o')
            FreshnessWindowHours = $windowHours
            Summary = if ([string]::Equals($status, 'fresh', [System.StringComparison]::OrdinalIgnoreCase)) {
                "Durable proof is inside the $windowHours-hour freshness window."
            } else {
                "Durable proof is older than the $windowHours-hour freshness window."
            }
        }
    } catch {
        return [pscustomobject]@{
            Status = 'unknown'
            CapturedAtUtc = $capturedAtText
            FreshUntilUtc = $null
            FreshnessWindowHours = $windowHours
            Summary = 'The proof capture timestamp could not be parsed.'
        }
    }
}

$script:NormalizedBaseUrl = Get-NormalizedBaseUrl -Value $BaseUrl
$runtimeRoot = Get-LocalRuntimeRoot
$releaseEvidenceDir = Join-Path $runtimeRoot 'ReleaseEvidence'
$nativeProofPath = Join-Path $releaseEvidenceDir 'latest-native-proof.json'
$proofBundlePath = Join-Path $releaseEvidenceDir 'latest-proof-bundle.json'
$historyDir = Join-Path $releaseEvidenceDir 'History'

$historyNativeProof = $null
if (Test-Path -LiteralPath $historyDir) {
    $historyNativeProof = Get-ChildItem -LiteralPath $historyDir -File -Filter 'native-proof-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

$nativeProof = Read-JsonFileOrNull -Path $nativeProofPath
if ($null -eq $nativeProof -and $historyNativeProof) {
    $nativeProof = Read-JsonFileOrNull -Path $historyNativeProof.FullName
}
$proofBundle = Read-JsonFileOrNull -Path $proofBundlePath

$healthProbe = Invoke-ProofGet -Path '/api/health'
$bridgeProbe = Invoke-ProofGet -Path '/api/bridge/proof'

$proofSource = 'none'
$proofPayload = $null
if ($bridgeProbe.Reachable -and $null -ne $bridgeProbe.Data) {
    $proofSource = 'live-bridge-proof'
    $proofPayload = $bridgeProbe.Data
} elseif ($null -ne $nativeProof) {
    $proofSource = 'latest-native-proof-artifact'
    $proofPayload = $nativeProof
} elseif ($null -ne $proofBundle) {
    $proofSource = 'latest-proof-bundle'
    $proofPayload = $proofBundle
}

$artifactStatus = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'Status')
$bridgeStatus = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'BridgeProofStatus')
if ([string]::IsNullOrWhiteSpace($bridgeStatus)) {
    $bridgeStatus = $artifactStatus
}

$summary = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'Summary')
$nativeReadiness = Get-PropertyOrNull -InputObject $proofPayload -Name 'NativeReadiness'
$loopProof = Get-PropertyOrNull -InputObject $proofPayload -Name 'LoopProof'

$liveDeliveryProven = [bool](Get-PropertyOrNull -InputObject $proofPayload -Name 'LiveDeliveryProven')
$nativeHudBindReady = Resolve-NativeHudBindReady -ProofPayload $proofPayload -NativeReadiness $nativeReadiness
$visibleDeliveryConfirmed = [bool](Get-PropertyOrNull -InputObject $proofPayload -Name 'VisibleDeliveryConfirmed')
$actionFeedbackObserved = [bool](Get-PropertyOrNull -InputObject $proofPayload -Name 'ActionFeedbackObserved')

if ($loopProof) {
    $visibleDeliveryConfirmed = [bool](Get-PropertyOrNull -InputObject $loopProof -Name 'VisibleDeliveryConfirmed')
    $actionFeedbackObserved = [bool](Get-PropertyOrNull -InputObject $loopProof -Name 'ActionFeedbackObserved')
}

if ([string]::Equals($bridgeStatus, 'delivery_proven', [System.StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($artifactStatus, 'proven', [System.StringComparison]::OrdinalIgnoreCase)) {
    $liveDeliveryProven = $true
}

$recommendedHudTarget = ''
if ($nativeReadiness) {
    $hudRecommendation = Get-PropertyOrNull -InputObject $nativeReadiness -Name 'HudBindRecommendation'
    $recommendedHudTarget = [string](Get-PropertyOrNull -InputObject $hudRecommendation -Name 'RecommendedTarget')
}
if ([string]::IsNullOrWhiteSpace($recommendedHudTarget)) {
    $recommendedHudTarget = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'RecommendedHudTarget')
}

$blockers = @(ConvertTo-StringArray -Value (Get-PropertyOrNull -InputObject $proofPayload -Name 'CurrentBlockers'))
$readyEvidence = @(ConvertTo-StringArray -Value (Get-PropertyOrNull -InputObject $proofPayload -Name 'ReadyEvidence'))
$recommendedNextStep = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'RecommendedNextStep')
$diagnosisCode = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'DiagnosisCode')
$diagnosisSummary = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'DiagnosisSummary')
if ([string]::IsNullOrWhiteSpace($diagnosisCode)) {
    if ($liveDeliveryProven) {
        $diagnosisCode = 'native_hud_delivery_proven'
        $diagnosisSummary = 'Live Palworld native HUD delivery has matching bridge proof.'
    } elseif ($blockers.Count -gt 0) {
        $diagnosisCode = 'bridge_proof_blocked'
        $diagnosisSummary = [string]$blockers[0]
    } elseif (-not $healthProbe.Reachable) {
        $diagnosisCode = 'sidecar_offline'
        $diagnosisSummary = 'The sidecar is not reachable, so live bridge proof cannot be inspected.'
    } elseif (-not $bridgeProbe.Reachable) {
        $diagnosisCode = 'bridge_proof_unavailable'
        $diagnosisSummary = 'The sidecar is reachable, but /api/bridge/proof did not respond.'
    } else {
        $diagnosisCode = 'native_proof_missing'
        $diagnosisSummary = 'No live Palworld delivery_proven evidence has been captured yet.'
    }
}
$diagnosisCode = $diagnosisCode.Trim().ToLowerInvariant()
$diagnosisAction = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'DiagnosisAction')
$diagnosisCommand = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'DiagnosisCommand')
if ([string]::IsNullOrWhiteSpace($diagnosisAction)) {
    $diagnosisAction = Resolve-DiagnosisAction -Code $diagnosisCode
}
if ([string]::IsNullOrWhiteSpace($diagnosisCommand)) {
    $diagnosisCommand = Resolve-DiagnosisCommand -Code $diagnosisCode
}
$evidenceFreshness = Resolve-EvidenceFreshness -ProofSource $proofSource -ProofPayload $proofPayload
$evidenceFreshForLane = [string]::Equals([string]$evidenceFreshness.Status, 'live', [System.StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals([string]$evidenceFreshness.Status, 'fresh', [System.StringComparison]::OrdinalIgnoreCase)
$staleDurableProof = $liveDeliveryProven `
    -and -not [string]::Equals($proofSource, 'live-bridge-proof', [System.StringComparison]::OrdinalIgnoreCase) `
    -and [string]::Equals([string]$evidenceFreshness.Status, 'stale', [System.StringComparison]::OrdinalIgnoreCase)

$overall = 'NEEDS LIVE SESSION'
$overallReason = 'No live Palworld delivery_proven evidence has been captured yet.'
$nextAction = 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1'

if ($staleDurableProof) {
    $overall = 'STALE PROOF'
    $overallReason = [string]$evidenceFreshness.Summary
    $nextAction = 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1'
} elseif ($liveDeliveryProven) {
    $overall = 'PROVEN'
    $overallReason = 'Live native delivery evidence is present.'
    $nextAction = 'pwsh ./pal.ps1 health'
} elseif (-not $healthProbe.Reachable) {
    $overall = 'SIDECAR OFFLINE'
    $overallReason = 'The sidecar is not reachable, so live bridge proof cannot be inspected.'
    $nextAction = 'pwsh ./pal.ps1 play'
} elseif (-not $bridgeProbe.Reachable) {
    $overall = 'BRIDGE PROOF UNAVAILABLE'
    $overallReason = 'The sidecar is reachable, but /api/bridge/proof did not respond.'
    $nextAction = 'pwsh ./pal.ps1 doctor'
} elseif ($blockers.Count -gt 0) {
    $overall = 'BLOCKED'
    $overallReason = $blockers[0]
    if (-not $nativeHudBindReady -and -not [string]::IsNullOrWhiteSpace($recommendedHudTarget)) {
        $nextAction = 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1 -ApplyHudRecommendation'
    } elseif (-not [string]::IsNullOrWhiteSpace($recommendedNextStep)) {
        $nextAction = $recommendedNextStep
    }
}

$lanes = @(
    (New-ProofLane -Name 'sidecar_reachable' -Passed:$healthProbe.Reachable -Summary $healthProbe.Uri),
    (New-ProofLane -Name 'bridge_proof_reachable' -Passed:$bridgeProbe.Reachable -Summary $bridgeProbe.Uri),
    (New-ProofLane -Name 'live_delivery_proven' -Passed:$liveDeliveryProven -Summary $bridgeStatus),
    (New-ProofLane -Name 'native_hud_bind_ready' -Passed:$nativeHudBindReady -Summary $recommendedHudTarget -Required:$false),
    (New-ProofLane -Name 'visible_delivery_confirmed' -Passed:$visibleDeliveryConfirmed -Summary 'loop proof visible delivery flag'),
    (New-ProofLane -Name 'action_feedback_observed' -Passed:$actionFeedbackObserved -Summary 'loop proof action feedback flag' -Required:$false),
    (New-ProofLane -Name 'native_proof_artifact_present' -Passed:($null -ne $nativeProof) -Summary $nativeProofPath -Required:$false),
    (New-ProofLane -Name 'proof_bundle_present' -Passed:($null -ne $proofBundle) -Summary $proofBundlePath -Required:$false),
    (New-ProofLane -Name 'proof_evidence_fresh' -Passed:$evidenceFreshForLane -Summary ([string]$evidenceFreshness.Summary) -Required:$false)
)

$snapshot = [pscustomobject]@{
    Schema = 'https://palllm.dev/schemas/native-proof-status-v1.schema.json'
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    RepoRoot = $repoRoot
    RuntimeRoot = $runtimeRoot
    BaseUrl = $script:NormalizedBaseUrl
    Overall = $overall
    OverallReason = $overallReason
    DiagnosisCode = $diagnosisCode
    DiagnosisSummary = $diagnosisSummary
    DiagnosisAction = $diagnosisAction
    DiagnosisCommand = $diagnosisCommand
    NextAction = $nextAction
    ProofSource = $proofSource
    BridgeProofStatus = $bridgeStatus
    Summary = $summary
    EvidenceFreshnessStatus = [string]$evidenceFreshness.Status
    EvidenceCapturedAtUtc = $evidenceFreshness.CapturedAtUtc
    EvidenceFreshUntilUtc = $evidenceFreshness.FreshUntilUtc
    EvidenceFreshnessWindowHours = [int]$evidenceFreshness.FreshnessWindowHours
    LiveDeliveryProven = $liveDeliveryProven
    NativeHudBindReady = $nativeHudBindReady
    RecommendedHudTarget = $recommendedHudTarget
    VisibleDeliveryConfirmed = $visibleDeliveryConfirmed
    ActionFeedbackObserved = $actionFeedbackObserved
    CurrentBlockers = @($blockers)
    ReadyEvidence = @($readyEvidence)
    Artifacts = [pscustomobject]@{
        LatestNativeProof = $nativeProofPath
        LatestNativeProofExists = ($null -ne $nativeProof)
        LatestNativeProofHistory = if ($historyNativeProof) { $historyNativeProof.FullName } else { $null }
        LatestProofBundle = $proofBundlePath
        LatestProofBundleExists = ($null -ne $proofBundle)
    }
    Lanes = @($lanes)
}

if ($Json.IsPresent) {
    $snapshot | ConvertTo-Json -Depth 12
} else {
    $color = switch ($overall) {
        'PROVEN' { 'Green' }
        'BLOCKED' { 'Yellow' }
        'STALE PROOF' { 'Yellow' }
        'NEEDS LIVE SESSION' { 'Yellow' }
        default { 'Red' }
    }

    Write-Host ''
    Write-Host 'PalLLM native proof status' -ForegroundColor Cyan
    Write-Host ("  overall : {0}" -f $overall) -ForegroundColor $color
    Write-Host ("  reason  : {0}" -f $overallReason)
    if (-not [string]::IsNullOrWhiteSpace($diagnosisCode)) {
        Write-Host ("  code    : {0}" -f $diagnosisCode)
    }
    if (-not [string]::IsNullOrWhiteSpace($diagnosisAction)) {
        Write-Host ("  action  : {0}" -f $diagnosisAction)
    }
    Write-Host ("  source  : {0}" -f $proofSource)
    Write-Host ("  proof   : {0}" -f $evidenceFreshness.Summary)
    Write-Host ("  next    : {0}" -f $nextAction) -ForegroundColor Yellow
    if (-not [string]::IsNullOrWhiteSpace($diagnosisCommand)) {
        Write-Host ("  command : {0}" -f $diagnosisCommand) -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host 'Proof lanes:' -ForegroundColor White
    foreach ($lane in $lanes) {
        $laneColor = switch ($lane.Status) {
            'PASS' { 'Green' }
            'WARN' { 'Yellow' }
            default { 'Red' }
        }
        Write-Host ("  {0,-30} {1,-5} {2}" -f $lane.Name, $lane.Status, $lane.Summary) -ForegroundColor $laneColor
    }

    if ($blockers.Count -gt 0) {
        Write-Host ''
        Write-Host 'Current blockers:' -ForegroundColor White
        foreach ($blocker in $blockers) {
            Write-Host ("  - {0}" -f $blocker)
        }
    }

    if ($readyEvidence.Count -gt 0) {
        Write-Host ''
        Write-Host 'Ready evidence:' -ForegroundColor White
        foreach ($item in $readyEvidence) {
            Write-Host ("  - {0}" -f $item)
        }
    }

    Write-Host ''
}

$provenForGate = $liveDeliveryProven -and -not $staleDurableProof
if ($RequireProven.IsPresent -and -not $provenForGate) {
    exit 1
}
