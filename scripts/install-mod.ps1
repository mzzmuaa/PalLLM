[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$PalworldPath,
    [string]$SourcePath,
    [ValidateSet("Copy", "Junction")]
    [string]$InstallMode = "Copy",
    [switch]$SkipSamplePack
)

$ErrorActionPreference = "Stop"

# Platform guard. UE4SS (and therefore Palworld + this mod) are
# Windows-only. Fail fast with a helpful message instead of letting
# the operator discover the limitation through a cryptic path / API
# error several lines deeper.
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Host ""
    Write-Host "PalLLM mod install is Windows-only." -ForegroundColor Yellow
    Write-Host "  Reason: UE4SS (the script runtime that hosts the Lua bridge) is a"
    Write-Host "  Windows-only injector targeting Palworld's Win64 process. There is"
    Write-Host "  no equivalent for Linux or macOS today."
    Write-Host ""
    Write-Host "  The sidecar (PalLLM.Sidecar) DOES run cross-platform. If you only"
    Write-Host "  want the runtime + dashboard + MCP server (not the in-game mod),"
    Write-Host "  use:"
    Write-Host "    dotnet run --project src/PalLLM.Sidecar/PalLLM.Sidecar.csproj"
    Write-Host ""
    Write-Host "  See docs/COMPATIBILITY.md > 'Operating systems' for the full"
    Write-Host "  per-OS support matrix."
    Write-Host ""
    exit 1
}

. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")
. (Join-Path $PSScriptRoot "PalLLM.InstallManifest.ps1")

$resolvedSource = if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    Get-PalLlmModSourcePath
}
else {
    Resolve-ExistingPath -Path $SourcePath
}

if (-not $resolvedSource) {
    throw "PalLLM mod source was not found. Pass -SourcePath or keep the repo/package structure intact."
}

$entryScript = Join-Path $resolvedSource "Scripts\main.lua"
if (-not (Test-Path -LiteralPath $entryScript)) {
    throw "PalLLM source is missing Scripts\\main.lua: $resolvedSource"
}

$install = Resolve-PalworldInstall -PalworldPath $PalworldPath
$targetRoot = $install.ModRoot
$targetPath = $install.InstalledModPath

$normalizedTargetRoot = [IO.Path]::GetFullPath($targetRoot)
$normalizedTargetPath = [IO.Path]::GetFullPath($targetPath)
if (-not $normalizedTargetPath.StartsWith($normalizedTargetRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install outside the resolved mod root. Target=$normalizedTargetPath Root=$normalizedTargetRoot"
}

$manifest = New-PalLlmInstallManifest `
    -PalworldRoot $install.Root `
    -ModRoot $normalizedTargetRoot `
    -InstalledModPath $normalizedTargetPath `
    -SourcePath $resolvedSource `
    -InstallMode $InstallMode

if ($PSCmdlet.ShouldProcess($normalizedTargetPath, "Install PalLLM mod")) {
    # Track every filesystem mutation so uninstall-mod.ps1 can undo it
    # precisely. If any step throws, the catch block walks the manifest in
    # reverse and removes whatever we already created (atomic install per
    # 2026 transactional-install best practice).
    try {
        if (-not (Test-Path -LiteralPath $normalizedTargetRoot)) {
            New-Item -ItemType Directory -Path $normalizedTargetRoot -Force | Out-Null
            # ModRoot itself is not added to the manifest - it is a Palworld /
            # UE4SS folder we never want to delete on uninstall.
        }

        if (Test-Path -LiteralPath $normalizedTargetPath) {
            Remove-Item -LiteralPath $normalizedTargetPath -Recurse -Force
        }

        if ($InstallMode -eq "Junction") {
            New-Item -ItemType Junction -Path $normalizedTargetPath -Target $resolvedSource | Out-Null
            Add-PalLlmInstallArtifact -Manifest $manifest -Kind 'junction' `
                -Path $normalizedTargetPath -Source $resolvedSource | Out-Null
        }
        else {
            Copy-Item -LiteralPath $resolvedSource -Destination $normalizedTargetRoot -Recurse -Force
            Add-PalLlmInstallArtifact -Manifest $manifest -Kind 'directory' `
                -Path $normalizedTargetPath -Source $resolvedSource | Out-Null
        }

        $enabledFile = Join-Path $normalizedTargetPath "enabled.txt"
        if (-not (Test-Path -LiteralPath $enabledFile)) {
            Set-Content -LiteralPath $enabledFile -Value "1" -Encoding ASCII
            Add-PalLlmInstallArtifact -Manifest $manifest -Kind 'enabled-file' `
                -Path $enabledFile | Out-Null
        }
    }
    catch {
        # Rollback the partial install. We touch only the artifacts we recorded
        # in the manifest, never the surrounding ModRoot.
        Write-Host "[install-mod] Install failed - rolling back $($manifest.Artifacts.Count) recorded artifact(s)..."
        foreach ($entry in @($manifest.Artifacts) | Sort-Object -Descending AddedAt) {
            try {
                if (Test-Path -LiteralPath $entry.Path) {
                    if ($entry.Kind -eq 'directory') {
                        Remove-Item -LiteralPath $entry.Path -Recurse -Force -ErrorAction SilentlyContinue
                    }
                    else {
                        Remove-Item -LiteralPath $entry.Path -Force -ErrorAction SilentlyContinue
                    }
                }
            }
            catch {
                Write-Warning "[install-mod] Rollback failed to remove $($entry.Path): $_"
            }
        }
        throw
    }
}

# Optionally seed the runtime Packs folder with the shipped starter pack so a
# first-run install shows authored companion lore instead of only generic
# fallback replies. Skip with -SkipSamplePack if the operator already has their
# own Packs folder populated.
$samplePackInstalled = $false
$samplePackDestination = $null
if (-not $SkipSamplePack.IsPresent) {
    $repoRoot = Get-PalLlmRepoRoot
    $samplePackSource = Join-Path $repoRoot "docs\examples\camp-guardian-pack.json"
    if (Test-Path -LiteralPath $samplePackSource) {
        $runtimeRoot = Get-PalLlmRuntimeRoot
        $packsDir = Join-Path $runtimeRoot "Packs"
        $samplePackDestination = Join-Path $packsDir "camp-guardian-pack.json"
        if (-not (Test-Path -LiteralPath $samplePackDestination)) {
            if ($PSCmdlet.ShouldProcess($samplePackDestination, "Install PalLLM sample pack")) {
                New-Item -ItemType Directory -Path $packsDir -Force | Out-Null
                Copy-Item -LiteralPath $samplePackSource -Destination $samplePackDestination -Force
                $samplePackInstalled = $true
                Add-PalLlmInstallArtifact -Manifest $manifest -Kind 'sample-pack' `
                    -Path $samplePackDestination -Source $samplePackSource | Out-Null
            }
        }
    }
}

# Persist the manifest only after every step succeeded. Uninstall reads from
# this single file and never has to guess what was touched.
$manifestPath = Get-PalLlmInstallManifestPath
if ($PSCmdlet.ShouldProcess($manifestPath, "Write PalLLM install manifest")) {
    Save-PalLlmInstallManifest -Manifest $manifest -ManifestPath $manifestPath
}

[pscustomobject]@{
    PalworldRoot = $install.Root
    Win64Path = $install.Win64Path
    ModRoot = $normalizedTargetRoot
    TargetPath = $normalizedTargetPath
    SourcePath = $resolvedSource
    InstallMode = $InstallMode
    EnabledFile = Join-Path $normalizedTargetPath "enabled.txt"
    SamplePackInstalled = $samplePackInstalled
    SamplePackPath = $samplePackDestination
    InstallManifestPath = $manifestPath
    InstallId = $manifest.InstallId
}
