<#
.SYNOPSIS
    One-click PalLLM uninstaller.

.DESCRIPTION
    Removes the PalLLM mod from a Palworld install precisely, using the
    install manifest written by `install-mod.ps1`. Personal data
    (chat history, custom packs) is preserved by default; pass `-Full`
    to remove everything.

    Strategy follows 2026 mod-manager best practice (r2modman /
    Thunderstore Mod Manager pattern): one button, base game stays
    unaffected, dry-run preview before any change. Falls back to
    "uninstall by convention" when the manifest is absent so legacy
    installs from before the manifest existed are still removable.

.PARAMETER PalworldPath
    Optional explicit Palworld install path. When omitted, the mod is
    removed wherever the manifest says it lives, or via the same
    auto-detection install-mod.ps1 uses.

.PARAMETER PreservePersonalData
    Default `true`. When set, the runtime root's `session.json`,
    `Packs/`, `TTS/`, and the `Bridge/` history are kept. Pass
    `-Full` to wipe everything.

.PARAMETER Full
    Remove EVERYTHING - the mod files plus the entire runtime root
    (chat history, packs, TTS cache, evidence files, install
    manifest). Use this for "factory reset" before reinstalling on a
    different Palworld build.

.PARAMETER DryRun
    Print what would be removed without changing anything. Always
    safe to run; produces a list of paths and a per-path action.

.PARAMETER ManifestPath
    Override the manifest location. Defaults to
    `runtime-root/install-manifest.json`. Useful for tests.

.EXAMPLE
    pwsh ./scripts/uninstall-mod.ps1 -DryRun
    # Preview what would be removed; nothing changes.

.EXAMPLE
    pwsh ./scripts/uninstall-mod.ps1
    # Default: remove the mod, keep personal data.

.EXAMPLE
    pwsh ./scripts/uninstall-mod.ps1 -Full
    # Remove everything including runtime root.

.NOTES
    Pairs with `install-mod.ps1` (the manifest writer) and
    `uninstall.bat` (the one-click wrapper). The manifest schema is
    `docs/schemas/install-manifest.schema.json`.

    See `docs/UNINSTALL.md` for the full operator-facing walkthrough.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PalworldPath,
    [bool]$PreservePersonalData = $true,
    [switch]$Full,
    [switch]$DryRun,
    [string]$ManifestPath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")
. (Join-Path $PSScriptRoot "PalLLM.InstallManifest.ps1")

if ($Full.IsPresent) {
    $PreservePersonalData = $false
}

# -----------------------------------------------------------------------------
# Resolve manifest
# -----------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Get-PalLlmInstallManifestPath
}

$manifest = Read-PalLlmInstallManifest -ManifestPath $ManifestPath

# -----------------------------------------------------------------------------
# Build the removal plan
# -----------------------------------------------------------------------------
class UninstallEntry {
    [string]$Path
    [string]$Kind
    [string]$Action       # 'remove' | 'preserve' | 'missing'
    [string]$Source       # 'manifest' | 'fallback' | 'runtime-root'
    [string]$Reason
}

$plan = New-Object System.Collections.ArrayList

function Add-Plan {
    param(
        [string]$Path,
        [string]$Kind,
        [string]$Action,
        [string]$Source,
        [string]$Reason
    )
    $entry = [UninstallEntry]@{
        Path   = $Path
        Kind   = $Kind
        Action = $Action
        Source = $Source
        Reason = $Reason
    }
    [void]$plan.Add($entry)
    return $entry
}

if ($manifest) {
    Write-Host "[uninstall-mod] Using manifest at $ManifestPath (install id: $($manifest.InstallId))"
    foreach ($artifact in $manifest.Artifacts) {
        $exists = Test-Path -LiteralPath $artifact.Path
        $action = if (-not $exists) { 'missing' } else { 'remove' }
        $reason = if ($exists) { 'manifest' } else { 'manifest-but-already-gone' }
        Add-Plan -Path $artifact.Path -Kind $artifact.Kind -Action $action -Source 'manifest' -Reason $reason | Out-Null
    }
}
else {
    # Manifest fallback: best-effort uninstall by convention. Removes the
    # known mod directory under the resolved Palworld install only.
    Write-Host "[uninstall-mod] No manifest at $ManifestPath - falling back to convention-based uninstall."
    try {
        $install = Resolve-PalworldInstall -PalworldPath $PalworldPath
        $convPath = $install.InstalledModPath
        if (Test-Path -LiteralPath $convPath) {
            Add-Plan -Path $convPath -Kind 'directory' -Action 'remove' -Source 'fallback' -Reason 'convention:installedModPath' | Out-Null
        }
    }
    catch {
        Write-Warning "[uninstall-mod] Could not resolve a Palworld install: $_"
    }
}

# Append the manifest itself (always last to remove). It lives in the runtime
# root, so when -Full is set the runtime-root removal will also catch it; when
# -PreservePersonalData is set we still want the manifest gone since it points
# at a no-longer-installed mod.
if (Test-Path -LiteralPath $ManifestPath) {
    Add-Plan -Path $ManifestPath -Kind 'file' -Action 'remove' -Source 'manifest' -Reason 'manifest-self' | Out-Null
}

# Personal-data handling. Runtime root contents are preserved unless -Full.
$runtimeRoot = Get-PalLlmRuntimeRoot
if ($Full.IsPresent) {
    if (Test-Path -LiteralPath $runtimeRoot) {
        Add-Plan -Path $runtimeRoot -Kind 'directory' -Action 'remove' -Source 'runtime-root' -Reason 'full-uninstall' | Out-Null
    }
}
else {
    if (Test-Path -LiteralPath $runtimeRoot) {
        Add-Plan -Path $runtimeRoot -Kind 'directory' -Action 'preserve' -Source 'runtime-root' -Reason 'preserves-chat-history-and-packs' | Out-Null
    }
}

# -----------------------------------------------------------------------------
# Print + execute
# -----------------------------------------------------------------------------
function Format-Plan {
    param([object[]]$Entries)
    $lines = @()
    foreach ($e in $Entries) {
        $marker = switch ($e.Action) {
            'remove'   { '[REMOVE]   ' }
            'preserve' { '[PRESERVE] ' }
            'missing'  { '[MISSING]  ' }
            default    { '[?]        ' }
        }
        $lines += ("{0,-12}{1,-14}{2}" -f $marker, $e.Kind, $e.Path)
        if ($e.Reason) {
            $lines += ("{0,-12}{1}" -f '', "        reason: $($e.Reason)")
        }
    }
    return ($lines -join "`n")
}

Write-Host ""
Write-Host "PalLLM uninstall plan:" -ForegroundColor Cyan
Write-Host ""
Write-Host (Format-Plan $plan)
Write-Host ""

if ($DryRun.IsPresent) {
    Write-Host "[uninstall-mod] -DryRun set; nothing changed." -ForegroundColor Yellow
    [pscustomobject]@{
        DryRun = $true
        ManifestPath = $ManifestPath
        ManifestPresent = $null -ne $manifest
        Plan = @($plan)
        RemovedCount = 0
        PreservedCount = ($plan | Where-Object { $_.Action -eq 'preserve' }).Count
        MissingCount = ($plan | Where-Object { $_.Action -eq 'missing' }).Count
    } | Write-Output
    return
}

$removed = 0
$failed = New-Object System.Collections.ArrayList
foreach ($entry in $plan) {
    if ($entry.Action -ne 'remove') { continue }
    if (-not (Test-Path -LiteralPath $entry.Path)) { continue }

    if (-not $PSCmdlet.ShouldProcess($entry.Path, "Remove $($entry.Kind)")) {
        continue
    }

    try {
        switch ($entry.Kind) {
            'directory' {
                Remove-Item -LiteralPath $entry.Path -Recurse -Force -ErrorAction Stop
            }
            'junction' {
                # Junction removal: don't recurse into the target, just remove
                # the link itself. PowerShell's Remove-Item with -Recurse on a
                # junction will follow into the source directory which we
                # never want to touch.
                Remove-Item -LiteralPath $entry.Path -Force -ErrorAction Stop
            }
            default {
                Remove-Item -LiteralPath $entry.Path -Force -ErrorAction Stop
            }
        }
        $removed += 1
    }
    catch {
        [void]$failed.Add([pscustomobject]@{ Path = $entry.Path; Error = $_.Exception.Message })
    }
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "[uninstall-mod] Removed $removed artifact(s)." -ForegroundColor Green
}
else {
    Write-Host "[uninstall-mod] Removed $removed artifact(s); $($failed.Count) failed." -ForegroundColor Yellow
    foreach ($f in $failed) {
        Write-Host "  - $($f.Path): $($f.Error)" -ForegroundColor Yellow
    }
}

[pscustomobject]@{
    DryRun = $false
    ManifestPath = $ManifestPath
    ManifestPresent = $null -ne $manifest
    Plan = @($plan)
    RemovedCount = $removed
    FailedCount = $failed.Count
    Failures = @($failed)
    PreservedCount = ($plan | Where-Object { $_.Action -eq 'preserve' }).Count
    MissingCount = ($plan | Where-Object { $_.Action -eq 'missing' }).Count
}
