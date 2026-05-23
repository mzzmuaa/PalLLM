Set-StrictMode -Version Latest

# -----------------------------------------------------------------------------
# PalLLM install manifest - shared helper.
#
# Records every filesystem touchpoint a PalLLM install creates so that
# `uninstall-mod.ps1` can undo it precisely without guessing. The pattern
# follows 2026 best practice (transactional install with rollback) and
# extends naturally toward 2030 directions like content-addressed storage,
# declarative reconciliation (Apple DDM-style), and snapshot rollback.
#
# Manifest schema:  docs/schemas/install-manifest.schema.json
# Manifest path:    runtime-root/install-manifest.json
#
# Source-of-truth for the schema is the JSON file in docs/schemas/, but
# this PS module is the single producer. Test fixtures in
# tests/PalLLM.Tests/InstallManifestTests.cs validate the shape.
# -----------------------------------------------------------------------------

$Script:PalLlmInstallManifestSchemaVersion = 1

function New-PalLlmInstallManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PalworldRoot,

        [Parameter(Mandatory = $true)]
        [string]$ModRoot,

        [Parameter(Mandatory = $true)]
        [string]$InstalledModPath,

        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [ValidateSet("Copy", "Junction")]
        [string]$InstallMode
    )

    return [pscustomobject]@{
        SchemaVersion = $Script:PalLlmInstallManifestSchemaVersion
        InstallId     = [guid]::NewGuid().ToString()
        InstalledAt   = (Get-Date).ToUniversalTime().ToString('o')
        Producer      = 'install-mod.ps1'
        ProducerVersion = (Get-PalLlmInstallProducerVersion)
        PalworldRoot  = $PalworldRoot
        ModRoot       = $ModRoot
        InstalledModPath = $InstalledModPath
        SourcePath    = $SourcePath
        InstallMode   = $InstallMode
        Artifacts     = New-Object System.Collections.ArrayList
    }
}

function Add-PalLlmInstallArtifact {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Manifest,

        [Parameter(Mandatory = $true)]
        [ValidateSet('directory', 'file', 'junction', 'enabled-file', 'sample-pack')]
        [string]$Kind,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [string]$Source,

        [hashtable]$Metadata
    )

    $entry = [pscustomobject]@{
        Kind = $Kind
        Path = [IO.Path]::GetFullPath($Path)
        Source = $Source
        AddedAt = (Get-Date).ToUniversalTime().ToString('o')
        Metadata = if ($Metadata) { [pscustomobject]$Metadata } else { $null }
    }
    [void]$Manifest.Artifacts.Add($entry)
    return $entry
}

function Save-PalLlmInstallManifest {
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)]
        [object]$Manifest,

        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    if ($PSCmdlet.ShouldProcess($ManifestPath, 'Write PalLLM install manifest')) {
        $parent = Split-Path -Parent $ManifestPath
        if (-not (Test-Path -LiteralPath $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $json = $Manifest | ConvertTo-Json -Depth 10
        Set-Content -LiteralPath $ManifestPath -Value $json -Encoding UTF8
    }
}

function Read-PalLlmInstallManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        return $null
    }

    try {
        $json = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8
        $manifest = $json | ConvertFrom-Json
    }
    catch {
        Write-Warning "Install manifest at $ManifestPath is unreadable: $_"
        return $null
    }

    # Reject manifests with unsupported schema versions so a future v2 manifest
    # cannot be silently mis-uninstalled by an older script.
    if (-not ($manifest.PSObject.Properties['SchemaVersion'])) {
        Write-Warning "Install manifest at $ManifestPath has no SchemaVersion - treating as unreadable."
        return $null
    }

    if ($manifest.SchemaVersion -ne $Script:PalLlmInstallManifestSchemaVersion) {
        Write-Warning "Install manifest at $ManifestPath uses schema version $($manifest.SchemaVersion); this tool understands v$Script:PalLlmInstallManifestSchemaVersion."
        return $null
    }

    return $manifest
}

function Get-PalLlmInstallManifestPath {
    [CmdletBinding()]
    param()
    return (Join-Path (Get-PalLlmRuntimeRoot) 'install-manifest.json')
}

function Get-PalLlmInstallProducerVersion {
    # Best-effort version stamp. The packaged release writes a sidecar exe whose
    # FileVersion we can read; in dev or when the exe is missing we fall back to
    # the assembly version reported by `dotnet --version` so the manifest still
    # carries a recognisable producer fingerprint.
    $exe = Get-PalLlmPackagedSidecarExePath
    if (Test-Path -LiteralPath $exe) {
        try {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
            if (-not [string]::IsNullOrWhiteSpace($info.FileVersion)) {
                return $info.FileVersion
            }
        }
        catch {
            # fall through
        }
    }
    return 'dev'
}

function Get-PalLlmInstallManifestSchemaVersion {
    return $Script:PalLlmInstallManifestSchemaVersion
}
