<#
.SYNOPSIS
    First-run guided tour. The "you just installed this; what now?"
    surface. Walks a new user through six 10-second beats so they
    leave with a working mental model and a real next thing to try.

.DESCRIPTION
    Six interactive beats:

      1. "Hi. I'm PalLLM. Here's what I am in 30 words."
      2. The first thing to try: pal hello (probe a running sidecar)
      3. The 30-second self-tour: pal demo
      4. Out-of-game moments: pal campfire (with /whisper /fortune /quest /tale)
      5. Wire a real LLM: pal connect ollama (or llamacpp / vllm / foundry)
      6. Where to go next: docs/INDEX.md and docs/HANDOFF.md

    Each beat waits for ENTER before continuing. The user can ABORT
    with Ctrl-C at any point - no state is written, no commitment is
    made. The tour itself never executes verbs; it tells the user
    what to type, so the user stays in control.

    Designed to read like a guided conversation, not a sales pitch.
    Every beat fits in one paragraph; the longest is ~70 words.

.PARAMETER NoColor
    Suppress ANSI color. Useful when piping into a text doc.

.PARAMETER Quick
    Skip the ENTER pauses; print everything at once. Useful for
    "remind me what's here" re-reads.

.EXAMPLE
    pwsh ./scripts/pal-welcome.ps1
    # Six interactive beats; press ENTER to advance each one.

.EXAMPLE
    pwsh ./scripts/pal-welcome.ps1 -Quick
    # Print all six beats at once. Good for re-orientation.

.NOTES
    Verb shortcut:  pal welcome

    A complement to docs/QUICKSTART.md (the technical first-chat
    path). This
    verb is the in-terminal version - meet the user where their
    cursor is.
#>
[CmdletBinding()]
param(
    [switch]$NoColor,
    [switch]$Quick
)

$ErrorActionPreference = 'Continue'

function Write-Pal {
    param([string]$Text, [string]$Color = 'Gray')
    if ($NoColor) { Write-Host $Text } else { Write-Host $Text -ForegroundColor $Color }
}

function Pause-Beat {
    if ($Quick) {
        Write-Host ""
        return
    }
    Write-Host ""
    Write-Pal "  (press ENTER to continue, Ctrl-C to leave the tour)" 'DarkGray'
    [void](Read-Host)
}

# -----------------------------------------------------------------------------
# Banner
# -----------------------------------------------------------------------------

if (-not $Quick) { Clear-Host }
Write-Pal ""
Write-Pal "  ~~~ Welcome to PalLLM ~~~" 'Magenta'
Write-Pal "  Six small beats. About a minute. Press ENTER to advance each one." 'DarkGray'
Pause-Beat

# -----------------------------------------------------------------------------
# Beat 1 - what is this
# -----------------------------------------------------------------------------

Write-Pal ""
Write-Pal "1. What you just installed" 'Cyan'
Write-Pal "   PalLLM is a local-first companion runtime. A small .NET sidecar" 'White'
Write-Pal "   that gives a Palworld pal (or any game / app) a voice. By default" 'White'
Write-Pal "   it makes ZERO outbound traffic - everything runs on your machine." 'White'
Write-Pal "   The companion always answers, even with no model wired, via a" 'White'
Write-Pal "   hand-authored fallback layer." 'White'
Pause-Beat

# -----------------------------------------------------------------------------
# Beat 2 - first probe
# -----------------------------------------------------------------------------

Write-Pal ""
Write-Pal "2. Probe the sidecar:" 'Cyan'
Write-Pal "   Open a second terminal and run:" 'White'
Write-Pal ""
Write-Pal "       pal play          # boots the sidecar and the dashboard" 'Yellow'
Write-Pal ""
Write-Pal "   Then back here:" 'White'
Write-Pal ""
Write-Pal "       pal hello         # sends 'hi' to the sidecar and prints the reply" 'Yellow'
Write-Pal ""
Write-Pal "   You should see a reply with a 'ResponsePath' diagnostic. Even if" 'DarkGray'
Write-Pal "   inference is off, the reply will land via the deterministic fallback." 'DarkGray'
Pause-Beat

# -----------------------------------------------------------------------------
# Beat 3 - demo tour
# -----------------------------------------------------------------------------

Write-Pal ""
Write-Pal "3. The 30-second self-tour:" 'Cyan'
Write-Pal "   Run:" 'White'
Write-Pal ""
Write-Pal "       pal demo          # six scenes across six fallback families" 'Yellow'
Write-Pal ""
Write-Pal "   This shows the range of voices the deterministic fallback covers:" 'White'
Write-Pal "   first contact at camp, mid-combat triage, exploration, late-night" 'White'
Write-Pal "   base maintenance, capture window, returning after a loss." 'White'
Pause-Beat

# -----------------------------------------------------------------------------
# Beat 4 - campfire
# -----------------------------------------------------------------------------

Write-Pal ""
Write-Pal "4. Five minutes with the companion outside the game:" 'Cyan'
Write-Pal ""
Write-Pal "       pal campfire      # a clean REPL; works offline against fallback" 'Yellow'
Write-Pal ""
Write-Pal "   Inside the campfire, four hand-curated ritual surfaces are one" 'White'
Write-Pal "   slash command away:" 'White'
Write-Pal "       /whisper          quiet ambient one-liner" 'DarkGray'
Write-Pal "       /fortune          today's date-seeded fortune" 'DarkGray'
Write-Pal "       /quest [tier]     small ~30-min self-contained challenge" 'DarkGray'
Write-Pal "       /tale [prefix]    3-4-line in-character campfire story" 'DarkGray'
Pause-Beat

# -----------------------------------------------------------------------------
# Beat 5 - wire a model
# -----------------------------------------------------------------------------

Write-Pal ""
Write-Pal "5. Wire a real LLM (optional):" 'Cyan'
Write-Pal "   Pick the path that matches your local model server:" 'White'
Write-Pal ""
Write-Pal "       pal connect ollama   # local engine, any hardware" 'Yellow'
Write-Pal "       pal connect llamacpp -ModelPath <model.gguf>  # raw GGUF llama-server lane" 'Yellow'
Write-Pal "       pal connect lmstudio -Model <loaded-id>  # LM Studio desktop lane" 'Yellow'
Write-Pal "       pal connect vllm     # Blackwell / Hopper / Ampere recipe" 'Yellow'
Write-Pal "       pal connect openvino -TargetDevice GPU  # Intel CPU / GPU / NPU lane" 'Yellow'
Write-Pal "       pal connect foundry -FoundryEndpoint <url>  # Windows ML lane" 'Yellow'
Write-Pal "       pal connect transformers -Revision <sha>  # pinned HF serving" 'Yellow'
Write-Pal ""
Write-Pal "   Each can write a coherent appsettings.json with a -DryRun preview" 'White'
Write-Pal "   and a .bak backup. Or run the wizard for everything:" 'White'
Write-Pal ""
Write-Pal "       pal config wizard    # 5-question first-time setup" 'Yellow'
Pause-Beat

# -----------------------------------------------------------------------------
# Beat 6 - where to go next
# -----------------------------------------------------------------------------

Write-Pal ""
Write-Pal "6. Where to go next:" 'Cyan'
Write-Pal "       pal list             # the full verb table" 'Yellow'
Write-Pal "       pal readiness        # candid scorecard ('is this ready for me?')" 'Yellow'
Write-Pal "       pal status           # one-line current-state check" 'Yellow'
Write-Pal "       pal benchmark        # real-world latency vs per-tier budget" 'Yellow'
Write-Pal "       pal uninstall -DryRun  # preview a clean uninstall (nothing changes)" 'Yellow'
Write-Pal ""
Write-Pal "   Reading order, in priority:" 'White'
Write-Pal "       docs/QUICKSTART.md   technical first-chat path" 'DarkGray'
Write-Pal "       docs/INDEX.md        full doc map" 'DarkGray'
Write-Pal "       docs/READINESS.md    honest 10/10 scorecard per aspect" 'DarkGray'
Write-Pal "       docs/PRIVACY.md      what does and doesn't leave your machine" 'DarkGray'
Pause-Beat

Write-Pal ""
Write-Pal "  ~~~ Welcome tour complete ~~~" 'Magenta'
Write-Pal ""
Write-Pal "  Always-available lifeline:  pal next" 'White'
Write-Pal "  (probes your current state and recommends ONE action)" 'DarkGray'
Write-Pal ""
Write-Pal "  If anything's broken: pal doctor" 'DarkGray'
Write-Pal ""
