<#
.SYNOPSIS
    "Show your friend" demo. Prints a self-running tour of the PalLLM
    companion across multiple fallback families in ~30 seconds.

.DESCRIPTION
    Two modes:

      LIVE mode  - default. Probes the running sidecar at the configured
                   BaseUrl. If reachable, sends six representative chat
                   prompts hitting different fallback strategy families
                   and prints both sides of the conversation, plus the
                   diagnostic ResponsePath for each turn.

      CANNED mode - if the sidecar is not reachable (or -Canned is
                    forced), prints a hand-curated transcript that
                    showcases the same range without needing a running
                    sidecar. Designed for "open the README, see the
                    personality" moments.

    Both modes work with deterministic fallback only - no inference
    endpoint required, no model configured. The demo is the fastest way
    to convey what PalLLM actually feels like.

.PARAMETER BaseUrl
    Sidecar URL. Default http://localhost:5088.

.PARAMETER Canned
    Force canned mode even if a sidecar is reachable. Useful for
    docs / README excerpts where you want a stable, reproducible
    output every time.

.PARAMETER NoColor
    Suppress ANSI color output. Useful when piping the demo into a
    text doc.

.EXAMPLE
    pwsh ./scripts/demo-pal.ps1
    # Live demo against localhost:5088 if the sidecar is up,
    # otherwise canned transcript.

.EXAMPLE
    pwsh ./scripts/demo-pal.ps1 -Canned -NoColor > docs/examples/demo-transcript.txt
    # Reproducible transcript for embedding in docs.

.NOTES
    Verb shortcut:  pal demo
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [switch]$Canned,
    [switch]$NoColor
)

$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Color-aware printing
# -----------------------------------------------------------------------------

function Write-Frame {
    param([string]$Text, [string]$Color = 'Cyan')
    if ($NoColor) {
        Write-Host $Text
    }
    else {
        Write-Host $Text -ForegroundColor $Color
    }
}

function Write-Section {
    param([string]$Title)
    $bar = ('-' * $Title.Length)
    Write-Host ""
    Write-Frame ("== $Title ==") 'Cyan'
}

# -----------------------------------------------------------------------------
# The six demo turns - each one targets a different fallback family so the
# demo shows the range. Same set is used for both live and canned modes.
# -----------------------------------------------------------------------------

$turns = @(
    @{
        Scene  = 'First contact at camp'
        Prompt = 'Hey - quiet morning so far. How are you settling in?'
        Family = 'AmbientCamp'
        Emoji  = '*'
    }
    @{
        Scene  = 'Combat just kicked off'
        Prompt = 'Three Bushi just dropped on us, low health, what now?'
        Family = 'EmergencyTriage'
        Emoji  = '!'
    }
    @{
        Scene  = 'Quiet exploration'
        Prompt = 'I see a ridgeline north of base. Worth the detour?'
        Family = 'ExplorationSweep'
        Emoji  = '~'
    }
    @{
        Scene  = 'Late-night base maintenance'
        Prompt = 'The forge is cold and rain is rolling in. Help me prioritize.'
        Family = 'CraftingDiscipline'
        Emoji  = '#'
    }
    @{
        Scene  = 'Capture window'
        Prompt = 'Big alpha cooled to 14% HP - is this the moment?'
        Family = 'CaptureWindow'
        Emoji  = '+'
    }
    @{
        Scene  = 'Returning to base after a hard fight'
        Prompt = 'Lost two pals out there. I just need to sit by the fire for a minute.'
        Family = 'MoraleRally'
        Emoji  = '.'
    }
)

# -----------------------------------------------------------------------------
# Canned transcript - hand-curated to convey the actual flavor of each
# fallback family. Used when the sidecar is unreachable, or when the demo is
# embedded in docs that need a stable output.
# -----------------------------------------------------------------------------

$cannedReplies = @{
    'AmbientCamp'         = @{
        Reply = 'Good - the perimeter held. I caught one of the cooling racks ticking too loud near dawn. Nothing serious, just worth a glance.'
        Path  = 'fallback-after-inference-disabled'
        Strat = 'ambient-camp'
    }
    'EmergencyTriage'     = @{
        Reply = "Three's manageable if we don't bunch up. Pull behind the stone pillar to your left, I'll draw the right flank. Heal between their second and third combo - that's the tell."
        Path  = 'fallback-after-inference-disabled'
        Strat = 'emergency-triage'
    }
    'ExplorationSweep'    = @{
        Reply = 'Worth it before the weather turns. Stay on the ridge spine - there was an updraft cue last time we were near, and the line of sight back to camp stays clear. Mark the start point so we can retrace if it goes long.'
        Path  = 'fallback-after-inference-disabled'
        Strat = 'exploration-sweep'
    }
    'CraftingDiscipline'  = @{
        Reply = 'Fire first - rain steals heat faster than we make it. I will queue charcoal next so the forge has fuel through the storm. Spare ingots can wait until morning; nothing on that table is urgent.'
        Path  = 'fallback-after-inference-disabled'
        Strat = 'crafting-discipline'
    }
    'CaptureWindow'       = @{
        Reply = 'Yes - but break the line of sight first. At 14% it will desperate-cast. If you slip behind the boulder for a beat, the throw lands clean. Spheres ready?'
        Path  = 'fallback-after-inference-disabled'
        Strat = 'capture-window'
    }
    'MoraleRally'         = @{
        Reply = "Sit. The fire is enough. Whatever you didn't get to bring back, the pals you trained are still proof of you. We'll plan again in the morning."
        Path  = 'fallback-after-inference-disabled'
        Strat = 'morale-rally'
    }
}

# -----------------------------------------------------------------------------
# Live mode - probe the sidecar
# -----------------------------------------------------------------------------

function Test-SidecarReachable {
    try {
        $null = Invoke-RestMethod -Uri "$BaseUrl/api/health" -Method Get -TimeoutSec 3 -ErrorAction Stop
        return $true
    }
    catch {
        return $false
    }
}

function Get-LiveReply {
    param([string]$Prompt, [int]$CharacterId = 1)
    $body = @{ userMessage = $Prompt; characterId = $CharacterId } | ConvertTo-Json -Compress
    $response = Invoke-RestMethod -Uri "$BaseUrl/api/chat" -Method Post `
        -ContentType 'application/json' -Body $body -TimeoutSec 10 -ErrorAction Stop
    return [pscustomobject]@{
        Reply = $response.assistantMessage
        Path = $response.responsePath
        Strat = $response.fallbackStrategy
    }
}

# -----------------------------------------------------------------------------
# Render the demo
# -----------------------------------------------------------------------------

$mode = if ($Canned) { 'canned' } else { 'auto' }
$liveReachable = $false
if ($mode -eq 'auto') {
    $liveReachable = Test-SidecarReachable
}

Write-Host ""
Write-Frame "PalLLM companion demo - 30-second tour" 'Magenta'
if ($Canned -or -not $liveReachable) {
    Write-Frame "  Mode: CANNED (sidecar not reachable, or -Canned forced)" 'DarkGray'
    Write-Frame "  Boot the sidecar with 'pal play' to run the same demo live." 'DarkGray'
}
else {
    Write-Frame "  Mode: LIVE against $BaseUrl" 'DarkGray'
    Write-Frame "  Each turn hits the deterministic fallback director - no model required." 'DarkGray'
}
Write-Host ""

$idx = 1
foreach ($turn in $turns) {
    Write-Section "$idx. $($turn.Scene)"
    Write-Frame ("[player]    " + $turn.Prompt) 'White'

    if ($liveReachable -and -not $Canned) {
        try {
            $live = Get-LiveReply -Prompt $turn.Prompt
            Write-Frame ("[companion] " + $live.Reply) 'Green'
            Write-Frame ("            -> path: $($live.Path)" + $(if ($live.Strat) { "  strategy: $($live.Strat)" })) 'DarkGray'
        }
        catch {
            # NOTE: must NOT use the variable name $canned - it collides
            # case-insensitively with the script's [switch]$Canned parameter
            # and PowerShell will reject assigning a hashtable to it.
            $fallbackTurn = $cannedReplies[$turn.Family]
            Write-Frame ("[companion] " + $fallbackTurn.Reply) 'Yellow'
            Write-Frame "            -> live call failed; falling back to canned: $($_.Exception.Message)" 'DarkGray'
        }
    }
    else {
        $fallbackTurn = $cannedReplies[$turn.Family]
        Write-Frame ("[companion] " + $fallbackTurn.Reply) 'Green'
        Write-Frame ("            -> family: $($turn.Family) / strategy: $($fallbackTurn.Strat)") 'DarkGray'
    }

    $idx += 1
}

Write-Host ""
Write-Frame "What just happened" 'Cyan'
Write-Host "  Each turn picked a different deterministic fallback family. No"
Write-Host "  inference call was made; the companion still responded with"
Write-Host "  scene-aware, in-character lines. Wire up an LLM endpoint and"
Write-Host "  these baseline replies become the floor, not the ceiling."
Write-Host ""
Write-Frame "Try it yourself" 'Cyan'
Write-Host "  pal hello                      # one-shot probe with diagnostics"
Write-Host "  pal models                     # what model fits your hardware"
Write-Host "  pal mcp connect <client>       # wire into Claude Desktop / VS Code / Cursor"
Write-Host "  docs/PROMPT_CARDS.md           # 19 curated demo prompts by scenario"
Write-Host ""
