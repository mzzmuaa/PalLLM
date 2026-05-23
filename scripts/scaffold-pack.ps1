<#
.SYNOPSIS
    Scaffold a new personality pack directory under samples/packs/
    or anywhere else, populated with placeholder pack.json,
    prompt.md, and voice-hint.md and a pre-computed ContentHash.

.DESCRIPTION
    Eliminates the manual schema-following for first-time pack
    authors. Given a kebab-case id (e.g. "companion-stoic") and a
    display name, the script:

      1. Creates <output-root>/<id>/
      2. Writes a pack.json scaffolded with the right schema fields
         (SchemaVersion, Id, DisplayName, Author, Version,
         PromptPath, VoiceHintPath, AudioSamples, SafetyFlags,
         ContentHash placeholder).
      3. Writes a prompt.md that follows the four-section shape
         (Voice / What you talk about / What you do not do /
         A note on quiet moments) used by the four reference packs.
      4. Writes a voice-hint.md with TTS pacing guidance.
      5. Computes the canonical ContentHash via the existing
         compute-pack-hash.ps1 helper and embeds it in pack.json.

    The author then edits the prompt + voice-hint to taste,
    re-runs `compute-pack-hash.ps1 -Update`, and drops the pack
    into runtime-root/Packs/.

    The scaffolder NEVER overwrites an existing directory unless
    -Force is passed, mirroring the safety posture of the parent
    scaffold.ps1.

.PARAMETER Id
    Kebab-case pack id. Becomes the directory name. Required.

.PARAMETER DisplayName
    Human-readable pack name. Required.

.PARAMETER Tagline
    Short one-liner for the pack picker. Optional but recommended.

.PARAMETER Author
    Pack author or team name. Required.

.PARAMETER OutputRoot
    Parent directory to create the pack inside. Default
    samples/packs/.

.PARAMETER Force
    Overwrite an existing pack directory.

.EXAMPLE
    pwsh ./scripts/scaffold-pack.ps1 -Id companion-stoic `
        -DisplayName "Stoic - few words, all weight" `
        -Tagline "Says less, means more." `
        -Author "your-name"
    # Creates samples/packs/companion-stoic/ with starter files
    # and a valid ContentHash. You then edit the prompt / voice
    # to taste.

.NOTES
    Verb shortcut:  pal pack new -Id <id> -DisplayName "..." -Author <name>

    See docs/PACK_AUTHORING.md for the format deep-dive and
    docs/PACK_SAMPLES.md for the four reference voices.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9][a-z0-9-]*[a-z0-9]$')]
    [string]$Id,

    [Parameter(Mandatory = $true)]
    [string]$DisplayName,

    [string]$Tagline = '',

    [Parameter(Mandatory = $true)]
    [string]$Author,

    [string]$OutputRoot,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'samples/packs'
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$packRoot = Join-Path $OutputRoot $Id
if ((Test-Path -LiteralPath $packRoot) -and -not $Force.IsPresent) {
    Write-Error "Pack directory already exists: $packRoot. Pass -Force to overwrite."
    exit 1
}

if (-not $PSCmdlet.ShouldProcess($packRoot, "Scaffold new personality pack '$Id'")) {
    return
}

New-Item -ItemType Directory -Path $packRoot -Force | Out-Null

$packDepth = ($packRoot.TrimEnd('\').Split([IO.Path]::DirectorySeparatorChar)).Count -
             ($repoRoot.TrimEnd('\').Split([IO.Path]::DirectorySeparatorChar)).Count
$schemaPath = ('../' * $packDepth) + 'docs/schemas/personality-pack.schema.json'

# ---------- prompt.md ----------
$promptStarter = @"
# Companion personality fragment - $DisplayName

[Edit this file to define the voice of the $DisplayName persona.
Each section is a constraint PalLLM appends to its base prompt -
not a screenplay.]

## Voice

- [What does this voice sound like? Cadence, register, what it does NOT sound like.]

## What you talk about, in order of priority

1. [Highest-priority thing this voice notices first.]
2. [Second priority.]
3. [Third priority. Two or three is plenty.]

## What you do not do

- [The anti-pattern. What the voice would otherwise default to that you want to suppress.]

## A note on quiet moments

[How does this voice handle silence, exit beats, and after-fight pauses?]
"@
Set-Content -LiteralPath (Join-Path $packRoot 'prompt.md') -Value $promptStarter -Encoding UTF8

# ---------- voice-hint.md ----------
$voiceStarter = @"
# Voice hint - $DisplayName

[Pacing notes for TTS. Pitch, cadence, what the consonant attack
should feel like. Treat punctuation as real pacing cues; the TTS
honors them.]

Pacing notes for TTS:
- [What does each period / em-dash / comma do in this voice?]
- [Where does stress land?]
- [What patterns to avoid (sing-song, breathy, etc.)?]
"@
Set-Content -LiteralPath (Join-Path $packRoot 'voice-hint.md') -Value $voiceStarter -Encoding UTF8

# ---------- pack.json (with placeholder ContentHash) ----------
$manifest = [ordered]@{
    '$schema'      = $schemaPath
    SchemaVersion  = 1
    Id             = $Id
    DisplayName    = $DisplayName
    Tagline        = $Tagline
    Author         = $Author
    Version        = '0.1.0'
    Description    = "TODO: long-form description shown on the pack detail page."
    PromptPath     = 'prompt.md'
    VoiceHintPath  = 'voice-hint.md'
    AudioSamples   = @()
    SafetyFlags    = @('family-friendly')
    ContentHash    = '0000000000000000000000000000000000000000000000000000000000000000'
}
$manifestJson = ($manifest | ConvertTo-Json -Depth 6)
Set-Content -LiteralPath (Join-Path $packRoot 'pack.json') -Value $manifestJson -Encoding UTF8

# ---------- compute the canonical hash ----------
$hashScript = Join-Path $PSScriptRoot 'compute-pack-hash.ps1'
& powershell -NoProfile -ExecutionPolicy Bypass -File $hashScript $packRoot -Update | Out-Null

Write-Host ""
Write-Host "Scaffolded new personality pack" -ForegroundColor Green
Write-Host ("  id          : {0}" -f $Id)
Write-Host ("  displayName : {0}" -f $DisplayName)
Write-Host ("  author      : {0}" -f $Author)
Write-Host ("  pack root   : {0}" -f $packRoot)
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Edit $packRoot\prompt.md to define the voice."
Write-Host "  2. Edit $packRoot\voice-hint.md for TTS pacing."
Write-Host "  3. Recompute the ContentHash:"
Write-Host "       pwsh ./scripts/compute-pack-hash.ps1 $packRoot -Update"
Write-Host "  4. Drop the pack into your runtime root and reload:"
Write-Host "       Invoke-RestMethod http://localhost:5088/api/packs/reload -Method Post"
Write-Host ""
Write-Host "More: docs/PACK_AUTHORING.md  +  docs/PACK_SAMPLES.md" -ForegroundColor DarkGray
Write-Host ""
