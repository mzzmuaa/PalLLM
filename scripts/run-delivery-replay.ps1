param(
    [string]$BaseUrl = "http://localhost:5088",
    [int]$TimeoutSeconds = 8,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

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

function Find-MatchingEnvelope {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Directories,

        [Parameter(Mandatory = $true)]
        [string]$RequestId
    )

    foreach ($directory in $Directories) {
        if (-not (Test-Path -LiteralPath $directory)) {
            continue
        }

        $candidates = Get-ChildItem -Path $directory -Filter "*.json" -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending

        foreach ($candidate in $candidates) {
            try {
                $parsed = Get-Content -Path $candidate.FullName -Raw | ConvertFrom-Json
            }
            catch {
                continue
            }

            if ($parsed.Payload.RequestId -eq $RequestId) {
                return [pscustomobject]@{
                    Envelope = $parsed
                    Path = $candidate.FullName
                    Location = if ((Split-Path -Leaf (Split-Path -Parent $candidate.FullName)) -ieq "Archive") { "archive" } else { "outbox" }
                }
            }
        }
    }

    return $null
}

function Wait-ForEnvelope {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Directories,

        [Parameter(Mandatory = $true)]
        [string]$RequestId,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds))
    while ([DateTime]::UtcNow -lt $deadline) {
        $match = Find-MatchingEnvelope -Directories $Directories -RequestId $RequestId
        if ($match) {
            return $match
        }

        Start-Sleep -Milliseconds 200
    }

    throw ("No chat_reply envelope matching request id '{0}' was observed in Outbox or Archive within {1}s." -f $RequestId, $TimeoutSeconds)
}

$script:NormalizedBaseUrl = Get-PalLlmNormalizedBaseUrl -BaseUrl $BaseUrl

Write-Host ("Checking PalLLM sidecar at {0} ..." -f $script:NormalizedBaseUrl)
$health = Invoke-PalApi -Method GET -Path "/api/health"

if ([string]::IsNullOrWhiteSpace($health.RuntimeRoot)) {
    throw "Sidecar health payload did not include RuntimeRoot."
}

$runtimeRoot = $health.RuntimeRoot
$bridgeRoot = Join-Path $runtimeRoot "Bridge"
$bridgeOutboxDir = Join-Path $bridgeRoot "Outbox"
$bridgeArchiveDir = Join-Path $bridgeRoot "Archive"

New-Item -ItemType Directory -Force -Path $bridgeOutboxDir | Out-Null
New-Item -ItemType Directory -Force -Path $bridgeArchiveDir | Out-Null

$scenarios = @(
    [pscustomobject]@{
        Name = "camp"
        ExpectedStrategyId = "crafting-discipline"
        ExpectedFamilyId = "base"
        ExpectedLayoutMode = "operations_panel"
        Snapshot = @{
            IsWorldLoaded = $true
            IsInBase = $true
            WorldName = "Palpagos"
            TimeOfDay = "night"
            Characters = @(
                @{
                    Id = 9101
                    DisplayName = "CampScout"
                    Species = "CampScout"
                }
            )
        }
        Chat = @{
            CharacterId = 9101
            UserMessage = "How should we prepare this camp for the night?"
            TaskTag = "chat_camp"
        }
    },
    [pscustomobject]@{
        Name = "stealth"
        ExpectedStrategyId = "stealth-shadow"
        ExpectedFamilyId = "stealth"
        ExpectedLayoutMode = "stealth_whisper"
        Snapshot = @{
            IsWorldLoaded = $true
            WorldName = "Palpagos"
            NearbyHostiles = @("Syndicate Thug")
            Characters = @(
                @{
                    Id = 9102
                    DisplayName = "Direhowl"
                    Species = "Direhowl"
                }
            )
        }
        Chat = @{
            CharacterId = 9102
            UserMessage = "Give me a stealth route past these guards."
            TaskTag = "chat_stealth"
        }
    },
    [pscustomobject]@{
        Name = "triage"
        ExpectedStrategyId = "emergency-triage"
        ExpectedFamilyId = "combat"
        ExpectedLayoutMode = "combat_alert"
        Snapshot = @{
            IsWorldLoaded = $true
            WorldName = "Palpagos"
            ThreatLevel = 0.9
            AlertLevel = 0.9
            NearbyHostiles = @("Rayhound", "Syndicate Gunner", "Incineram")
            RecentEvents = @("base_raid")
            Characters = @(
                @{
                    Id = 9103
                    DisplayName = "Mammorest"
                    Species = "Mammorest"
                    HealthFraction = 0.35
                    NearbyEnemyCount = 4
                    RecentDamageFraction = 0.6
                }
            )
        }
        Chat = @{
            CharacterId = 9103
            UserMessage = "What do we do right now?"
            TaskTag = "chat_defense"
        }
    },
    [pscustomobject]@{
        Name = "travel"
        ExpectedStrategyId = "safe-travel"
        ExpectedFamilyId = "travel"
        ExpectedLayoutMode = "route_strip"
        Snapshot = @{
            IsWorldLoaded = $true
            WorldName = "Palpagos"
            KnownBases = @(
                @{
                    BaseId = "Verdant Hub"
                    AreaRange = 42.5
                }
            )
            LastTravel = @{
                Origin = "Verdant Hub"
                Destination = "Alpha Tower"
                Waypoint = "Obsidian Outpost"
                Mode = "guided_route"
            }
            Characters = @(
                @{
                    Id = 9104
                    DisplayName = "Nitewing"
                    Species = "Nitewing"
                }
            )
        }
        Chat = @{
            CharacterId = 9104
            UserMessage = "How should we travel to the tower?"
            TaskTag = "chat_travel"
        }
    },
    [pscustomobject]@{
        Name = "base-network"
        ExpectedStrategyId = "base-network"
        ExpectedFamilyId = "base"
        ExpectedLayoutMode = "operations_panel"
        Snapshot = @{
            IsWorldLoaded = $true
            IsInBase = $true
            WorldName = "Palpagos"
            ActiveBaseIds = @("Verdant Hub", "Obsidian Outpost")
            KnownBases = @(
                @{
                    BaseId = "Verdant Hub"
                    AreaRange = 42.5
                },
                @{
                    BaseId = "Obsidian Outpost"
                    AreaRange = 31.0
                }
            )
            LastProduction = @{
                BaseId = "Verdant Hub"
                Station = "assembly_line"
                Item = "advanced_sphere"
                Quantity = 2
                Status = "queued"
            }
            Characters = @(
                @{
                    Id = 9105
                    DisplayName = "Anubis"
                    Species = "Anubis"
                }
            )
        }
        Chat = @{
            CharacterId = 9105
            UserMessage = "How should we split work between our bases?"
            TaskTag = "chat_bases"
        }
    }
)

$results = [System.Collections.Generic.List[object]]::new()

foreach ($scenario in $scenarios) {
    $requestId = "replay-{0}-{1}" -f $scenario.Name, ([Guid]::NewGuid().ToString("N").Substring(0, 8))
    $chatBody = @{
        CharacterId = $scenario.Chat.CharacterId
        RequestId = $requestId
        UserMessage = $scenario.Chat.UserMessage
        TaskTag = $scenario.Chat.TaskTag
    }

    Invoke-PalApi -Method POST -Path "/api/snapshot" -Body $scenario.Snapshot | Out-Null
    $chatResponse = Invoke-PalApi -Method POST -Path "/api/chat" -Body $chatBody

    if ([string]::IsNullOrWhiteSpace($chatResponse.AssistantMessage)) {
        throw ("Scenario '{0}' returned an empty assistant message." -f $scenario.Name)
    }

    $match = Wait-ForEnvelope -Directories @($bridgeOutboxDir, $bridgeArchiveDir) -RequestId $requestId -TimeoutSeconds $TimeoutSeconds
    $payload = $match.Envelope.Payload

    if ($match.Envelope.EventType -ne "chat_reply") {
        throw ("Scenario '{0}' produced an unexpected event type '{1}'." -f $scenario.Name, $match.Envelope.EventType)
    }

    if ($payload.Presentation.StrategyId -ne $scenario.ExpectedStrategyId) {
        throw ("Scenario '{0}' produced strategy '{1}', expected '{2}'." -f $scenario.Name, $payload.Presentation.StrategyId, $scenario.ExpectedStrategyId)
    }

    if ($payload.Presentation.Surface.FamilyId -ne $scenario.ExpectedFamilyId) {
        throw ("Scenario '{0}' produced surface family '{1}', expected '{2}'." -f $scenario.Name, $payload.Presentation.Surface.FamilyId, $scenario.ExpectedFamilyId)
    }

    if ($payload.Presentation.Surface.LayoutMode -ne $scenario.ExpectedLayoutMode) {
        throw ("Scenario '{0}' produced layout mode '{1}', expected '{2}'." -f $scenario.Name, $payload.Presentation.Surface.LayoutMode, $scenario.ExpectedLayoutMode)
    }

    if ([string]::IsNullOrWhiteSpace($payload.Presentation.Summary) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Audio.BehaviorId) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Audio.SubtitleStyle) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Visual.HudAccent) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Visual.WorldMarker) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Visual.ScreenTreatment) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.LayoutMode) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.PathBadge) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.FamilyBadge) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.PhaseBadge) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.PrimaryTitle) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.CueTitle) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.ReadoutTitle) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.SupportTitle) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.ActionPreviewTitle) `
        -or [string]::IsNullOrWhiteSpace($payload.Presentation.Surface.ActionFeedbackTitle) `
        -or $payload.Presentation.Surface.FollowupOrder.Count -ne 3 `
        -or $payload.Presentation.Surface.HeaderTokens.Count -le 0 `
        -or $payload.Presentation.Surface.CueTokens.Count -le 0 `
        -or $payload.Presentation.Surface.FocusTokens.Count -le 0 `
        -or $payload.Presentation.Surface.StatusTokens.Count -le 0 `
        -or $payload.Presentation.Surface.StageTokens.Count -le 0 `
        -or $payload.Presentation.Surface.AtmosphereTokens.Count -le 0 `
        -or $payload.Presentation.Surface.FooterTokens.Count -le 0 `
        -or $payload.Presentation.Surface.CardBudget -lt 1 `
        -or $payload.Presentation.Surface.CardBudget -gt 3 `
        -or $payload.Presentation.Surface.PrimaryCueTokenCount -lt 0 `
        -or $payload.Presentation.Surface.PrimaryCueTokenCount -gt 2 `
        -or $payload.Presentation.Surface.PrimaryFocusTokenCount -lt 0 `
        -or $payload.Presentation.Surface.PrimaryFocusTokenCount -gt 2 `
        -or $payload.Presentation.Surface.PrimaryStatusTokenCount -lt 0 `
        -or $payload.Presentation.Surface.PrimaryStatusTokenCount -gt 2 `
        -or $payload.Presentation.Surface.PrimaryStageTokenCount -lt 0 `
        -or $payload.Presentation.Surface.PrimaryStageTokenCount -gt 1 `
        -or $payload.Presentation.Surface.PrimaryAtmosphereTokenCount -lt 0 `
        -or $payload.Presentation.Surface.PrimaryAtmosphereTokenCount -gt 1 `
        -or $payload.Presentation.Surface.WidthChars -le 0 `
        -or $payload.Presentation.Surface.MaxBodyLines -le 0 `
        -or $payload.Presentation.Surface.PrimaryDurationMs -le 0 `
        -or $payload.Presentation.Surface.FollowupDurationMs -le 0) {
        throw ("Scenario '{0}' produced an incomplete presentation payload." -f $scenario.Name)
    }

    $results.Add([pscustomobject]@{
        Scenario = $scenario.Name
        RequestId = $requestId
        EnvelopePath = $match.Path
        EnvelopeLocation = $match.Location
        ResponsePath = $payload.ResponsePath
        UsedFallback = [bool]$payload.UsedFallback
        FallbackStrategy = $payload.FallbackStrategy
        StrategyId = $payload.Presentation.StrategyId
        SurfaceFamily = $payload.Presentation.Surface.FamilyId
        LayoutMode = $payload.Presentation.Surface.LayoutMode
        PathBadge = $payload.Presentation.Surface.PathBadge
        FamilyBadge = $payload.Presentation.Surface.FamilyBadge
        PhaseBadge = $payload.Presentation.Surface.PhaseBadge
        PrimaryTitle = $payload.Presentation.Surface.PrimaryTitle
        CueTitle = $payload.Presentation.Surface.CueTitle
        ReadoutTitle = $payload.Presentation.Surface.ReadoutTitle
        SupportTitle = $payload.Presentation.Surface.SupportTitle
        ActionPreviewTitle = $payload.Presentation.Surface.ActionPreviewTitle
        ActionFeedbackTitle = $payload.Presentation.Surface.ActionFeedbackTitle
        FollowupOrder = @($payload.Presentation.Surface.FollowupOrder)
        HeaderTokens = @($payload.Presentation.Surface.HeaderTokens)
        CueTokens = @($payload.Presentation.Surface.CueTokens)
        FocusTokens = @($payload.Presentation.Surface.FocusTokens)
        StatusTokens = @($payload.Presentation.Surface.StatusTokens)
        StageTokens = @($payload.Presentation.Surface.StageTokens)
        AtmosphereTokens = @($payload.Presentation.Surface.AtmosphereTokens)
        FooterTokens = @($payload.Presentation.Surface.FooterTokens)
        CardBudget = $payload.Presentation.Surface.CardBudget
        PrimaryCueTokenCount = $payload.Presentation.Surface.PrimaryCueTokenCount
        PrimaryFocusTokenCount = $payload.Presentation.Surface.PrimaryFocusTokenCount
        PrimaryStatusTokenCount = $payload.Presentation.Surface.PrimaryStatusTokenCount
        PrimaryStageTokenCount = $payload.Presentation.Surface.PrimaryStageTokenCount
        PrimaryAtmosphereTokenCount = $payload.Presentation.Surface.PrimaryAtmosphereTokenCount
        AudioBehavior = $payload.Presentation.Audio.BehaviorId
        SubtitleStyle = $payload.Presentation.Audio.SubtitleStyle
        VisualBehavior = $payload.Presentation.Visual.BehaviorId
        HudAccent = $payload.Presentation.Visual.HudAccent
        WorldMarker = $payload.Presentation.Visual.WorldMarker
        ScreenTreatment = $payload.Presentation.Visual.ScreenTreatment
        WidthChars = $payload.Presentation.Surface.WidthChars
        MaxBodyLines = $payload.Presentation.Surface.MaxBodyLines
        PrimaryDurationMs = $payload.Presentation.Surface.PrimaryDurationMs
        FollowupDurationMs = $payload.Presentation.Surface.FollowupDurationMs
        HasAction = ($null -ne $payload.Action)
        ActionType = if ($null -ne $payload.Action) { $payload.Action.Type } else { "" }
        HasSpeech = ($null -ne $payload.Speech)
    })
}

if ($PSBoundParameters.ContainsKey("OutputPath") -and -not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }

    Set-Content -Path $OutputPath -Value ($results | ConvertTo-Json -Depth 12) -Encoding UTF8
}

Write-Host "PalLLM delivery replay succeeded."
$results
