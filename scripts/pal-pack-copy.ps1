<#
.SYNOPSIS
    Copy a ready-made sample pack from samples/packs/ into the runtime
    pack dir so the sidecar picks it up on the next reload.

.DESCRIPTION
    PalLLM ships four polished sample personality packs under
    samples/packs/ (companion-healer, companion-scholar, companion-trickster,
    companion-warrior). The runtime loads packs from the configured
    pack dir (default %LOCALAPPDATA%/Pal/Saved/PalLLM/Packs).

    `pal pack copy <name>` is the obvious one-step bridge between those
    two locations. It replaces the awkward
    `Copy-Item samples\packs\companion-warrior "...AppData...\Packs\" -Recurse`
    line that pal-next previously emitted as the recommended next action
    when no packs are loaded.

    Behavior:
      1. Resolves the source: <repo-root>/samples/packs/<name>/
         (case-insensitive match accepted).
      2. Resolves the destination: <runtime-root>/Packs/<name>/, where
         <runtime-root> is %LOCALAPPDATA%/Pal/Saved/PalLLM unless the
         operator overrides via -RuntimeRoot.
      3. If the destination already exists, refuses unless -Force is
         passed; the sample pack is treated as immutable so accidental
         double-copies don't overwrite operator-customised manifests.
      4. Recursively copies the source tree.
      5. Prints a one-line success summary plus the next-step hint
         (re-probe via `pal pack list` to confirm the sidecar reloaded
         the new pack).

    Pure read/write; no network calls. Runs in well under a second.

.PARAMETER Name
    Sample pack id (folder name under samples/packs/). Case-insensitive.
    Example: companion-warrior

.PARAMETER RuntimeRoot
    Override the runtime root. Default is
    %LOCALAPPDATA%/Pal/Saved/PalLLM/.

.PARAMETER Force
    Overwrite an existing destination directory.

.EXAMPLE
    pwsh ./scripts/pal-pack-copy.ps1 companion-warrior
    # Copies samples/packs/companion-warrior into the default runtime pack dir.

.EXAMPLE
    pwsh ./scripts/pal-pack-copy.ps1 -Name companion-trickster -Force
    # Copies and overwrites an existing companion-trickster on disk.

.NOTES
    Verb shortcut:  pal pack copy
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Name,
    [string]$RuntimeRoot,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# -----------------------------------------------------------------------------
# Resolve source under samples/packs/
# -----------------------------------------------------------------------------

$samplesDir = Join-Path $repoRoot 'samples/packs'
if (-not (Test-Path -LiteralPath $samplesDir)) {
    Write-Host "samples/packs/ not found at $samplesDir" -ForegroundColor Red
    Write-Host "If you cloned a partial tree, refresh from the official release zip." -ForegroundColor Yellow
    exit 1
}

# Case-insensitive match so 'Companion-Warrior' or 'COMPANION-WARRIOR' also work.
$sourceDir = Get-ChildItem -Path $samplesDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ieq $Name } |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $sourceDir) {
    Write-Host "No sample pack named '$Name' under samples/packs/." -ForegroundColor Red
    Write-Host ""
    Write-Host "Available samples:" -ForegroundColor White
    Get-ChildItem -Path $samplesDir -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name |
        ForEach-Object { Write-Host ("  " + $_.Name) -ForegroundColor Cyan }
    Write-Host ""
    Write-Host "Usage: pal pack copy <name>" -ForegroundColor White
    exit 1
}

# -----------------------------------------------------------------------------
# Resolve destination under runtime-root/Packs/
# -----------------------------------------------------------------------------

if (-not $RuntimeRoot) {
    # Honor the active appsettings.json's PalSavedRoot / RuntimeFolderName
    # overrides if the operator changed them, otherwise fall back to the
    # default %LOCALAPPDATA%/Pal/Saved/PalLLM. This is the same helper
    # play-palllm / install-mod / pal-health use, so a custom-rooted
    # deployment stays consistent.
    . (Join-Path $PSScriptRoot 'PalLLM.Tooling.ps1')
    $RuntimeRoot = Get-PalLlmRuntimeRoot
}

$destPacksDir = Join-Path $RuntimeRoot 'Packs'
if (-not (Test-Path -LiteralPath $destPacksDir)) {
    New-Item -ItemType Directory -Path $destPacksDir -Force | Out-Null
}

$destDir = Join-Path $destPacksDir (Split-Path -Leaf $sourceDir)
if ((Test-Path -LiteralPath $destDir) -and -not $Force.IsPresent) {
    Write-Host "Destination already exists: $destDir" -ForegroundColor Yellow
    Write-Host "Pass -Force to overwrite, or pick a different sample." -ForegroundColor Yellow
    exit 1
}

# -----------------------------------------------------------------------------
# Copy
# -----------------------------------------------------------------------------

Copy-Item -LiteralPath $sourceDir -Destination $destDir -Recurse -Force

# Sanity-check: the copied tree must contain a pack.json so the runtime
# can actually load it on the next reload.
$copiedManifest = Join-Path $destDir 'pack.json'
if (-not (Test-Path -LiteralPath $copiedManifest)) {
    Write-Host "Copied tree but no pack.json found at $copiedManifest." -ForegroundColor Red
    Write-Host "The source sample may be malformed; please report this." -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "Copied sample pack" -ForegroundColor Green
Write-Host ("  source : {0}" -f $sourceDir) -ForegroundColor DarkGray
Write-Host ("  dest   : {0}" -f $destDir) -ForegroundColor DarkGray
Write-Host ""
Write-Host "Next:" -ForegroundColor White
Write-Host "  pal pack list           # confirm the sidecar reloaded the new pack" -ForegroundColor Cyan
Write-Host "  pal demo                # 30-second tour with the new personality" -ForegroundColor Cyan
Write-Host ""
