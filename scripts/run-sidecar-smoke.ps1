param(
    [string]$BaseUrl = "http://localhost:5088",
    [int]$TimeoutSeconds = 6
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

Write-Host ("Checking PalLLM sidecar at {0} ..." -f $script:NormalizedBaseUrl)
$health = Invoke-PalApi -Method GET -Path "/api/health"

if ([string]::IsNullOrWhiteSpace($health.RuntimeRoot)) {
    throw "Sidecar health payload did not include RuntimeRoot."
}

$runtimeRoot = $health.RuntimeRoot
$bridgeInboxDir = Join-Path $runtimeRoot "Bridge\Inbox"
$bridgeOutboxDir = Join-Path $runtimeRoot "Bridge\Outbox"

New-Item -ItemType Directory -Force -Path $bridgeInboxDir | Out-Null
New-Item -ItemType Directory -Force -Path $bridgeOutboxDir | Out-Null

$requestId = "smoke-{0}" -f ([Guid]::NewGuid().ToString("N").Substring(0, 12))
$characterId = 9001
$characterName = "SmokeFox"
$baseId = "SmokeCamp"

Invoke-PalApi -Method POST -Path "/api/snapshot" -Body @{
    IsWorldLoaded = $true
    IsInBase = $true
    WorldName = "Palpagos"
    Characters = @(
        @{
            Id = $characterId
            DisplayName = $characterName
            Species = "CampScout"
        }
    )
} | Out-Null

$bridgeEnvelope = @{
    EventType = "base_discovered"
    Source = "smoke"
    TimestampUtc = [DateTimeOffset]::UtcNow
    Payload = @{
        BaseId = $baseId
        AreaRange = 42.5
    }
}

$bridgeFile = Join-Path $bridgeInboxDir ("smoke-base-{0}.json" -f $requestId)
Set-Content -Path $bridgeFile -Value (ConvertTo-PalLlmJsonBody -InputObject $bridgeEnvelope) -Encoding UTF8

$chatResponse = Invoke-PalApi -Method POST -Path "/api/chat" -Body @{
    CharacterId = $characterId
    RequestId = $requestId
    UserMessage = "How should we set up this camp?"
    TaskTag = "chat_camp"
}

if ([string]::IsNullOrWhiteSpace($chatResponse.AssistantMessage)) {
    throw "Smoke chat returned an empty assistant message."
}

$deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds))
$matchedEnvelope = $null
$matchedFile = $null

while ([DateTime]::UtcNow -lt $deadline -and -not $matchedEnvelope) {
    $candidates = Get-ChildItem -Path $bridgeOutboxDir -Filter "*.json" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending

    foreach ($candidate in $candidates) {
        try {
            $parsed = Get-Content -Path $candidate.FullName -Raw | ConvertFrom-Json
        }
        catch {
            continue
        }

        if ($parsed.Payload.RequestId -eq $requestId) {
            $matchedEnvelope = $parsed
            $matchedFile = $candidate.FullName
            break
        }
    }

    if (-not $matchedEnvelope) {
        Start-Sleep -Milliseconds 200
    }
}

if (-not $matchedEnvelope) {
    throw ("No outbox envelope matching request id '{0}' was observed within {1}s." -f $requestId, $TimeoutSeconds)
}

$payload = $matchedEnvelope.Payload
$actionPayload = Get-PalLlmPropertyValue -InputObject $payload -Name "Action"
$fallbackStrategy = [string](Get-PalLlmPropertyValue -InputObject $payload -Name "FallbackStrategy")
$fallbackPhase = [string](Get-PalLlmPropertyValue -InputObject $payload -Name "FallbackPhase")
$characterNameFromPayload = [string](Get-PalLlmPropertyValue -InputObject $payload -Name "CharacterName")
$responsePath = [string](Get-PalLlmPropertyValue -InputObject $payload -Name "ResponsePath")
$usedFallback = [bool](Get-PalLlmPropertyValue -InputObject $payload -Name "UsedFallback")

$deliveryEnvelope = @{
    EventType = "reply_delivery"
    Source = "smoke"
    TimestampUtc = [DateTimeOffset]::UtcNow
    Payload = @{
        RequestId = $requestId
        Speaker = $characterNameFromPayload
        ResponsePath = $responsePath
        StrategyId = $fallbackStrategy
        Phase = $fallbackPhase
        UsedFallback = $usedFallback
        Rendered = $true
        Surface = "smoke_replay"
        CardLabel = "SmokeReplay"
        CardIndex = 1
        CardCount = 1
        Note = "scripted smoke delivery"
    }
}

$deliveryFile = Join-Path $bridgeInboxDir ("smoke-delivery-{0}.json" -f $requestId)
Set-Content -Path $deliveryFile -Value (ConvertTo-PalLlmJsonBody -InputObject $deliveryEnvelope) -Encoding UTF8
Invoke-PalApi -Method POST -Path "/api/bridge/drain" | Out-Null

$actionType = ""
if ($actionPayload) {
    $actionType = [string](Get-PalLlmPropertyValue -InputObject $actionPayload -Name "Type")
}

if (-not [string]::IsNullOrWhiteSpace($actionType)) {
    $strategyTag = [string](Get-PalLlmPropertyValue -InputObject $actionPayload -Name "SourceStrategy")
    if ([string]::IsNullOrWhiteSpace($strategyTag)) {
        $strategyTag = $fallbackStrategy
    }

    switch ($actionType) {
        "waypoint_suggest" {
            $feedbackEnvelope = @{
                EventType = "travel"
                Source = "smoke"
                TimestampUtc = [DateTimeOffset]::UtcNow
                Payload = @{
                    Origin = $baseId
                    Destination = "SmokeWaypoint"
                    Waypoint = "SmokeRoute"
                    Mode = "guided_route"
                    Note = "scripted smoke feedback"
                    RequestId = $requestId
                    SourceStrategy = $strategyTag
                }
            }
        }
        "recall_pals" {
            $feedbackEnvelope = @{
                EventType = "pal_status"
                Source = "smoke"
                TimestampUtc = [DateTimeOffset]::UtcNow
                Payload = @{
                    PalName = $characterName
                    Species = "CampScout"
                    Change = "regrouped"
                    Note = "scripted smoke feedback"
                    RequestId = $requestId
                    SourceStrategy = $strategyTag
                }
            }
        }
        "request_craft_queue" {
            $feedbackEnvelope = @{
                EventType = "production"
                Source = "smoke"
                TimestampUtc = [DateTimeOffset]::UtcNow
                Payload = @{
                    BaseId = $baseId
                    Station = "smoke_station"
                    Item = "berry_bread"
                    Quantity = 1
                    Status = "queued"
                    Note = "scripted smoke feedback"
                    RequestId = $requestId
                    SourceStrategy = $strategyTag
                }
            }
        }
        default {
            $feedbackEnvelope = $null
        }
    }

    if ($feedbackEnvelope) {
        $feedbackFile = Join-Path $bridgeInboxDir ("smoke-feedback-{0}.json" -f $requestId)
        Set-Content -Path $feedbackFile -Value (ConvertTo-PalLlmJsonBody -InputObject $feedbackEnvelope) -Encoding UTF8
        Invoke-PalApi -Method POST -Path "/api/bridge/drain" | Out-Null
    }
}

$loopHealth = Invoke-PalApi -Method GET -Path "/api/health"
if ($null -eq $loopHealth.BridgeLoop) {
    throw "Smoke loop expected BridgeLoop in /api/health, but the runtime did not return it."
}

if (-not [bool]$loopHealth.BridgeLoop.LoopClosed) {
    $loopStatus = [string]$loopHealth.BridgeLoop.Status
    throw ("Smoke loop never closed for request '{0}'. Current bridge loop status: {1}" -f $requestId, $loopStatus)
}

$bridgeProof = Invoke-PalApi -Method GET -Path "/api/bridge/proof"
$releaseEvidenceDir = Join-Path $runtimeRoot "ReleaseEvidence"
$releaseEvidenceHistoryDir = Join-Path $releaseEvidenceDir "History"
New-Item -ItemType Directory -Force -Path $releaseEvidenceHistoryDir | Out-Null

$capturedAtUtc = [DateTimeOffset]::UtcNow
$historyStamp = $capturedAtUtc.ToString("yyyyMMdd-HHmmss")
$historyArtifactPath = Join-Path $releaseEvidenceHistoryDir ("smoke-{0}-{1}.json" -f $historyStamp, $requestId)
$latestArtifactPath = Join-Path $releaseEvidenceDir "latest-smoke.json"
$recommendedHudTarget = ""
$configuredHudTargets = @()
$nativeHudConfigSource = ""
$nativeHudConfigPath = ""

if ($bridgeProof -and $bridgeProof.NativeReadiness) {
    if ($bridgeProof.NativeReadiness.HudBindRecommendation) {
        $recommendedHudTarget = [string]$bridgeProof.NativeReadiness.HudBindRecommendation.RecommendedTarget
    }
    if ($bridgeProof.NativeReadiness.ConfiguredHudTargets) {
        $configuredHudTargets = @($bridgeProof.NativeReadiness.ConfiguredHudTargets)
    }
    $nativeHudConfigSource = [string]$bridgeProof.NativeReadiness.NativeHudConfigSource
    $nativeHudConfigPath = [string]$bridgeProof.NativeReadiness.NativeHudConfigPath
}

$smokeArtifact = [pscustomobject]@{
    Status = "recorded"
    Summary = "Palworld sidecar smoke loop closed and the latest bridge proof snapshot was captured."
    CapturedAtUtc = $capturedAtUtc
    ArtifactPath = $latestArtifactPath
    HistoryArtifactPath = $historyArtifactPath
    BaseUrl = $script:NormalizedBaseUrl
    RequestId = $requestId
    ResponsePath = $chatResponse.ResponsePath
    BridgeProofStatus = [string]$bridgeProof.Status
    BridgeLoopStatus = [string]$loopHealth.BridgeLoop.Status
    LoopClosed = [bool]$loopHealth.BridgeLoop.LoopClosed
    VisibleDeliveryConfirmed = [bool]$loopHealth.BridgeLoop.VisibleDeliveryConfirmed
    ActionFeedbackObserved = [bool]$loopHealth.BridgeLoop.ActionFeedbackObserved
    NativeHudBindReady = [bool]$bridgeProof.NativeHudBindReady
    RecommendedHudTarget = $recommendedHudTarget
    ConfiguredHudTargets = $configuredHudTargets
    NativeHudConfigSource = $nativeHudConfigSource
    NativeHudConfigPath = $nativeHudConfigPath
    DeliverySurface = [string]$loopHealth.BridgeLoop.LastReplyDelivery.Surface
    ActionType = $actionType
    UsedFallback = [bool]$chatResponse.UsedFallback
}

$smokeArtifactJson = ConvertTo-PalLlmJsonBody -InputObject $smokeArtifact
Set-Content -Path $historyArtifactPath -Value $smokeArtifactJson -Encoding UTF8
Set-Content -Path $latestArtifactPath -Value $smokeArtifactJson -Encoding UTF8

$result = [pscustomobject]@{
    BaseUrl = $script:NormalizedBaseUrl
    RuntimeRoot = $runtimeRoot
    RequestId = $requestId
    ResponsePath = $chatResponse.ResponsePath
    AssistantMessage = $chatResponse.AssistantMessage
    OutboxFile = $matchedFile
    DeliveryFile = $deliveryFile
    PresentationSource = $matchedEnvelope.Payload.Presentation.Source
    PresentationSummary = $matchedEnvelope.Payload.Presentation.Summary
    BridgeProofStatus = [string]$bridgeProof.Status
    BridgeLoopStatus = [string]$loopHealth.BridgeLoop.Status
    BridgeLoopClosed = [bool]$loopHealth.BridgeLoop.LoopClosed
    DeliverySurface = [string]$loopHealth.BridgeLoop.LastReplyDelivery.Surface
    ActionType = $actionType
    UsedFallback = [bool]$chatResponse.UsedFallback
    NativeHudConfigSource = $nativeHudConfigSource
    NativeHudConfigPath = $nativeHudConfigPath
    LatestSmokeArtifact = $latestArtifactPath
    SmokeHistoryArtifact = $historyArtifactPath
}

Write-Host "PalLLM smoke loop succeeded."
$result
