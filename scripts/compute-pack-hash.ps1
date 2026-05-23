<#
.SYNOPSIS
    Compute the canonical PalLLM personality-pack ContentHash for a
    pack directory. Mirrors PersonalityPackValidator.ComputeContentHash
    in src/PalLLM.Domain/Packs/PersonalityPack.cs so pack authors can
    embed the value into pack.json without round-tripping through the
    runtime.

.DESCRIPTION
    The hash is SHA-256 of:
        for each tracked file (sorted by relative path, ordinal):
            UTF-8(relativePath) || 0x00 || file-bytes || 0xFF

    Tracked files are: PromptPath, optional VoiceHintPath, optional
    VoiceRefPath, optional PortraitPath, optional LoraAdapterPath,
    and any AudioSamples entries. The manifest itself (pack.json)
    is intentionally NOT hashed - that lets the manifest embed the
    value without a bootstrap cycle.

    Returns the lower-case hex string. Use -Update to also rewrite
    pack.json's ContentHash field in place (with a one-shot .bak
    backup like the connect-* scripts).

.PARAMETER PackRoot
    Directory containing pack.json. Required.

.PARAMETER Update
    Write the computed hash back into pack.json's ContentHash field.

.EXAMPLE
    pwsh ./scripts/compute-pack-hash.ps1 ./samples/packs/companion-warrior
    # Print the canonical content hash for the pack at that path.

.EXAMPLE
    pwsh ./scripts/compute-pack-hash.ps1 ./samples/packs/companion-warrior -Update
    # Compute and embed the hash into pack.json (writing pack.json.bak
    # the first time).

.NOTES
    The C# source of truth is PersonalityPackValidator.ComputeContentHash
    in src/PalLLM.Domain/Packs/PersonalityPack.cs. If the algorithm
    changes there, this script must change in lockstep.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$PackRoot,

    [switch]$Update
)

$ErrorActionPreference = 'Stop'

$PackRoot = [IO.Path]::GetFullPath($PackRoot)
if (-not (Test-Path -LiteralPath $PackRoot -PathType Container)) {
    Write-Error "Pack root not found: $PackRoot"
    exit 1
}

$manifestPath = Join-Path $PackRoot 'pack.json'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    Write-Error "pack.json not found at: $manifestPath"
    exit 1
}

# Read manifest as raw JSON so we can re-serialize it the same way later.
$manifestText = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$manifest = $manifestText | ConvertFrom-Json -ErrorAction Stop

# Collect tracked file relative paths in the same order the C# validator does.
# The C# code sorts ordinal, so we mirror that here.
$tracked = New-Object System.Collections.ArrayList

if ([string]::IsNullOrWhiteSpace($manifest.PromptPath)) {
    Write-Error "PromptPath is required in pack.json."
    exit 1
}
[void]$tracked.Add($manifest.PromptPath)

if ($manifest.PSObject.Properties['VoiceHintPath'] -and -not [string]::IsNullOrWhiteSpace($manifest.VoiceHintPath)) {
    [void]$tracked.Add($manifest.VoiceHintPath)
}
if ($manifest.PSObject.Properties['VoiceRefPath'] -and -not [string]::IsNullOrWhiteSpace($manifest.VoiceRefPath)) {
    [void]$tracked.Add($manifest.VoiceRefPath)
}
if ($manifest.PSObject.Properties['PortraitPath'] -and -not [string]::IsNullOrWhiteSpace($manifest.PortraitPath)) {
    [void]$tracked.Add($manifest.PortraitPath)
}
if ($manifest.PSObject.Properties['LoraAdapterPath'] -and -not [string]::IsNullOrWhiteSpace($manifest.LoraAdapterPath)) {
    [void]$tracked.Add($manifest.LoraAdapterPath)
}
if ($manifest.PSObject.Properties['AudioSamples'] -and $manifest.AudioSamples) {
    foreach ($a in $manifest.AudioSamples) {
        if (-not [string]::IsNullOrWhiteSpace($a)) { [void]$tracked.Add($a) }
    }
}

# Ordinal sort to match StringComparer.Ordinal in C#.
$sorted = @($tracked | Sort-Object -CaseSensitive)

# Build the hash incrementally. Some packs can carry large local adapter
# files, so this intentionally mirrors the C# streaming hash path instead
# of buffering every tracked asset into memory.
$incrementalHash = [System.Security.Cryptography.IncrementalHash]::CreateHash([System.Security.Cryptography.HashAlgorithmName]::SHA256)
try {
    $pathSeparator = [byte[]](0x00)
    $fileSeparator = [byte[]](0xFF)
    $buffer = New-Object byte[] (16 * 1024)
    foreach ($rel in $sorted) {
        $abs = Join-Path $PackRoot $rel
        if (-not (Test-Path -LiteralPath $abs)) {
            # The C# code skips missing files silently. Mirror that
            # so authors can ship a manifest before the assets exist
            # and have the tool succeed; the validator will report
            # the missing file separately.
            continue
        }
        $relBytes = [System.Text.Encoding]::UTF8.GetBytes($rel)
        $incrementalHash.AppendData($relBytes)
        $incrementalHash.AppendData($pathSeparator)

        $fileStream = [System.IO.File]::OpenRead($abs)
        try {
            while (($read = $fileStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $incrementalHash.AppendData($buffer, 0, $read)
            }
        } finally {
            $fileStream.Dispose()
        }

        $incrementalHash.AppendData($fileSeparator)
    }
    $hashBytes = $incrementalHash.GetHashAndReset()
} finally {
    $incrementalHash.Dispose()
}

$hashHex = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })

Write-Host ""
Write-Host "PalLLM personality-pack ContentHash" -ForegroundColor Cyan
Write-Host "  Pack root : $PackRoot"
Write-Host "  Tracked   : $($sorted.Count) file(s)"
foreach ($p in $sorted) { Write-Host "    $p" -ForegroundColor DarkGray }
Write-Host ""
Write-Host "  ContentHash: $hashHex" -ForegroundColor Green
Write-Host ""

if ($Update.IsPresent) {
    if (-not $PSCmdlet.ShouldProcess($manifestPath, "Embed ContentHash $hashHex into pack.json")) {
        return
    }
    if (-not (Test-Path -LiteralPath "$manifestPath.bak")) {
        Copy-Item -LiteralPath $manifestPath -Destination "$manifestPath.bak" -Force
        Write-Host "Backed up to $manifestPath.bak" -ForegroundColor DarkGray
    }
    # Update the ContentHash property and re-serialize. ConvertFrom-Json
    # gives us a PSCustomObject; setting an existing property is enough.
    if ($manifest.PSObject.Properties['ContentHash']) {
        $manifest.ContentHash = $hashHex
    } else {
        $manifest | Add-Member -NotePropertyName 'ContentHash' -NotePropertyValue $hashHex
    }
    $resultJson = $manifest | ConvertTo-Json -Depth 12
    Set-Content -LiteralPath $manifestPath -Value $resultJson -Encoding UTF8
    Write-Host "Wrote updated pack.json with ContentHash = $hashHex" -ForegroundColor Green
    Write-Host ""
}

[pscustomobject]@{
    PackRoot = $PackRoot
    ContentHash = $hashHex
    TrackedCount = $sorted.Count
    Updated = $Update.IsPresent
} | Write-Output
