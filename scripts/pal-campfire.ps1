<#
.SYNOPSIS
    Chat with your PalLLM companion outside Palworld - a small REPL
    for "I want a moment with my companion but can't play right now".

.DESCRIPTION
    A clean, low-friction chat loop against a running sidecar. Same
    fallback engine + personality stack as the in-game path, just
    surfaced through a terminal. Works offline against the
    deterministic fallback when the sidecar is reachable but no
    inference endpoint is wired - so the campfire never goes silent.

    The metaphor: you're not at the game right now. You're at the
    campfire. Five minutes of company.

    Designed to feel handcrafted:
      - Time-aware greeting (morning / afternoon / evening / late)
      - Slash commands that read like a small menu, not a manual
      - One closing line on exit, never just a prompt-drop
      - Companion's last reply colored differently so the eye finds
        it without effort

    No persistence: this is a moment, not a session log. Use the
    in-game memory store if you want continuity.

.PARAMETER BaseUrl
    Sidecar URL. Default http://localhost:5088. The campfire still
    runs (in canned mode) if the sidecar is unreachable.

.PARAMETER CharacterId
    Character ID forwarded to /api/chat. Default 1 - the canonical
    "your main companion" id.

.PARAMETER Persona
    Optional pack id (e.g. companion-warrior, companion-scholar).
    Reserved for a future "switch personality mid-campfire" flow;
    currently informational only - the active persona is whichever
    one the sidecar already has loaded.

.PARAMETER NoColor
    Suppress ANSI colour. Pipe-friendly.

.EXAMPLE
    pwsh ./scripts/pal-campfire.ps1
    # Default: chat with the companion at localhost:5088.

.EXAMPLE
    pwsh ./scripts/pal-campfire.ps1 -BaseUrl http://my-host:5088
    # Talk to a remote sidecar (LAN-hosted, dedicated server, etc.).

.NOTES
    Verb shortcut:  pal campfire

    Slash commands inside the REPL:
        /help                  show this help
        /clear                 clear the screen but keep the session
        /whisper               one quiet ambient line
        /fortune               today's date-seeded fortune
        /quest [tier]          a micro-quest suggestion (easy / medium / spicy / quiet)
        /tale [title-prefix]   a 3-4-line campfire story
        /quit                  leave the campfire (also: exit, /exit)

    The campfire is intentionally tiny - it has no memory beyond the
    current shell window, no logging, and no "save this session"
    feature. If you want continuity, use the in-game chat path which
    is wired into the memory store and the bridge.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [int]$CharacterId = 1,
    [string]$Persona,
    [switch]$NoColor
)

$ErrorActionPreference = 'Continue'

function Write-Pal {
    param([string]$Text, [string]$Color = 'Gray')
    if ($NoColor) { Write-Host $Text } else { Write-Host $Text -ForegroundColor $Color }
}

function Get-TimeOfDayBeat {
    $h = (Get-Date).Hour
    if ($h -lt 5)  { return 'Late.  Couldn''t sleep either?' }
    if ($h -lt 11) { return 'Morning.  Fire''s already going if you want it.' }
    if ($h -lt 14) { return 'Midday.  Sit a minute.' }
    if ($h -lt 18) { return 'Afternoon.  No rush.' }
    if ($h -lt 22) { return 'Evening.  Easy one tonight.' }
    return 'Late again.  I''ll keep the kettle warm.'
}

function Test-SidecarReachable {
    param([string]$Url)
    try {
        $null = Invoke-RestMethod -Uri "$($Url.TrimEnd('/'))/api/health" -Method Get -TimeoutSec 3 -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

function Get-ChatReply {
    param([string]$Url, [string]$Message, [int]$CharacterId)
    $body = @{ userMessage = $Message; characterId = $CharacterId } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$($Url.TrimEnd('/'))/api/chat" -Method Post `
        -ContentType 'application/json' -Body $body -TimeoutSec 30 -ErrorAction Stop
    return [pscustomobject]@{
        Reply       = $response.assistantMessage
        Path        = $response.responsePath
        UsedFallback= [bool]$response.usedFallback
        Strategy    = $response.fallbackStrategy
    }
}

# A tiny canned-reply set so the campfire still has tone if the sidecar
# isn't running. Mirrors the demo-pal voice.
$cannedReplies = @(
    'Same as it always is. Wind in the trees and pals at the perimeter.',
    'Slow night. Easy company. Keep talking.',
    'I''d sit with you whether you said anything or not. The sitting''s the part.',
    'No fights. No fires. No alarms. That counts as a good evening.',
    'Tell me what kind of day you''d call it. I''ll just listen.',
    'Wood''s dry, fire''s good, you''re still in one piece. Three for three.',
    'I was up. I''m always up when you''re up. Don''t worry about that.'
)

function Get-CannedReply {
    return $cannedReplies | Get-Random
}

# -----------------------------------------------------------------------------
# Open the campfire
# -----------------------------------------------------------------------------

Clear-Host
Write-Pal ""
Write-Pal "  ~~~ PalLLM campfire ~~~" 'Magenta'
Write-Pal "  $(Get-TimeOfDayBeat)" 'DarkYellow'
Write-Pal ""

$reachable = Test-SidecarReachable -Url $BaseUrl
if ($reachable) {
    Write-Pal "  (sidecar at $BaseUrl)" 'DarkGray'
} else {
    Write-Pal "  (sidecar offline; canned voice mode - tone is real, not LLM)" 'DarkGray'
    Write-Pal "  (boot the sidecar with 'pal play' for the live companion)" 'DarkGray'
}
Write-Pal "  type a message, or one of:  /help  /whisper  /fortune  /quest  /tale  /patrol  /clear  /quit" 'DarkGray'
Write-Pal ""

$keepGoing = $true
while ($keepGoing) {
    Write-Host "you  > " -ForegroundColor White -NoNewline
    $line = Read-Host
    if ($null -eq $line) { $line = '' }
    $trimmed = $line.Trim()
    if ([string]::IsNullOrEmpty($trimmed)) { continue }

    switch -Regex ($trimmed) {
        '^(/quit|/exit|exit|quit|q)$' {
            Write-Pal ""
            Write-Pal "pal  > Take it easy.  I'll bank the fire." 'Cyan'
            Write-Pal ""
            $keepGoing = $false
            break
        }
        '^/help$' {
            Write-Pal ""
            Write-Pal "  /help                   this menu" 'DarkGray'
            Write-Pal "  /whisper                one quiet ambient line" 'DarkGray'
            Write-Pal "  /fortune                today's date-seeded fortune" 'DarkGray'
            Write-Pal "  /quest [tier]           micro-quest (easy / medium / spicy / quiet)" 'DarkGray'
            Write-Pal "  /tale [title-prefix]    3-4-line campfire story" 'DarkGray'
            Write-Pal "  /patrol [prefix]        the night the companion spent watching" 'DarkGray'
            Write-Pal "  /clear                  clear the screen" 'DarkGray'
            Write-Pal "  /quit                   leave the campfire" 'DarkGray'
            Write-Pal "  anything else           you talk, the companion replies" 'DarkGray'
            Write-Pal ""
            break
        }
        '^/whisper$' {
            $whisperScript = Join-Path $PSScriptRoot 'pal-whisper.ps1'
            & powershell -NoProfile -ExecutionPolicy Bypass -File $whisperScript
            break
        }
        '^/fortune$' {
            $fortuneScript = Join-Path $PSScriptRoot 'pal-fortune.ps1'
            & powershell -NoProfile -ExecutionPolicy Bypass -File $fortuneScript
            break
        }
        '^/quest(\s+(easy|medium|spicy|quiet|any))?$' {
            $tier = if ($matches[2]) { $matches[2] } else { 'any' }
            $questScript = Join-Path $PSScriptRoot 'pal-quest.ps1'
            & powershell -NoProfile -ExecutionPolicy Bypass -File $questScript -Tier $tier
            break
        }
        '^/tale(\s+(.+))?$' {
            $taleScript = Join-Path $PSScriptRoot 'pal-tale.ps1'
            if ($matches[2]) {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $taleScript -Title $matches[2]
            } else {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $taleScript
            }
            break
        }
        '^/patrol(\s+(.+))?$' {
            $patrolScript = Join-Path $PSScriptRoot 'pal-patrol-report.ps1'
            if ($matches[2]) {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $patrolScript -Title $matches[2]
            } else {
                & powershell -NoProfile -ExecutionPolicy Bypass -File $patrolScript
            }
            break
        }
        '^/clear$' {
            Clear-Host
            Write-Pal ""
            Write-Pal "  ~~~ PalLLM campfire ~~~" 'Magenta'
            Write-Pal ""
            break
        }
        default {
            Write-Pal ""
            if ($reachable) {
                try {
                    $r = Get-ChatReply -Url $BaseUrl -Message $trimmed -CharacterId $CharacterId
                    Write-Pal ("pal  > " + $r.Reply) 'Cyan'
                    if ($r.UsedFallback -and $r.Strategy) {
                        Write-Pal ("       (fallback: " + $r.Strategy + ")") 'DarkGray'
                    }
                } catch {
                    Write-Pal ("pal  > " + (Get-CannedReply)) 'Yellow'
                    Write-Pal ("       (live call failed: " + $_.Exception.Message + ")") 'DarkGray'
                }
            } else {
                Write-Pal ("pal  > " + (Get-CannedReply)) 'Yellow'
            }
            Write-Pal ""
            break
        }
    }
}
