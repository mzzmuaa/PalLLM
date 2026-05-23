<#
.SYNOPSIS
    List the personality packs the running sidecar has loaded.
    Closes the "I have packs in samples/ but how do I see what's
    actually loaded?" gap.

.DESCRIPTION
    Hits GET /api/packs against the running sidecar and renders the
    response as a readable table. Each row shows id, display name,
    version, and a one-line tagline (when present in the manifest).

    The sidecar's pack store loads from PalLlmOptions.PackDir
    (default: ${PalSavedRoot}/PalLLM/Packs/personalities/<id>/).
    If you've copied a pack from samples/packs/ into that directory
    and the manifest validates, it appears here.

    Reload-without-restart path:
        Invoke-RestMethod http://localhost:5088/api/packs/reload -Method Post

.PARAMETER BaseUrl
    Sidecar URL. Default http://localhost:5088.

.PARAMETER Json
    Emit the structured /api/packs response instead of a table.

.EXAMPLE
    pwsh ./scripts/pal-pack-list.ps1
    # Pretty table of loaded packs.

.EXAMPLE
    pwsh ./scripts/pal-pack-list.ps1 -Json
    # Pass-through of the API response.

.NOTES
    Verb shortcut:  pal pack list

    Companion to:
      pal pack new   - scaffold a new pack
      docs/PACK_AUTHORING.md / docs/PACK_SAMPLES.md - format deep-dives
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [switch]$Json
)

$ErrorActionPreference = 'Stop'

$url = "$($BaseUrl.TrimEnd('/'))/api/packs"
try {
    $response = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 5 -ErrorAction Stop
} catch {
    Write-Host ""
    Write-Host "Could not reach $url" -ForegroundColor Red
    Write-Host "  Try: pal play   (boot sidecar in a window + open dashboard)" -ForegroundColor Yellow
    Write-Host "  Or:  pal pack new -Id <id> -DisplayName ""..."" -Author <name>   (scaffold a pack first)" -ForegroundColor DarkGray
    Write-Host ""
    exit 1
}

if ($Json.IsPresent) {
    $response | ConvertTo-Json -Depth 6
    return
}

Write-Host ""
Write-Host "PalLLM personality packs (loaded by the running sidecar)" -ForegroundColor Cyan
Write-Host ("  source: {0}" -f $url)
Write-Host ""

$packs = $null
if ($response.PSObject.Properties['packs']) {
    $packs = $response.packs
} elseif ($response -is [System.Collections.IEnumerable] -and -not ($response -is [string])) {
    $packs = $response
}

if (-not $packs -or @($packs).Count -eq 0) {
    Write-Host "  (no packs loaded)" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Get started:" -ForegroundColor White
    Write-Host "  1. Pick a sample voice: docs/PACK_SAMPLES.md" -ForegroundColor DarkGray
    Write-Host "  2. Copy it into the runtime root:" -ForegroundColor DarkGray
    Write-Host "       Copy-Item samples/packs/companion-warrior \"\$env:LOCALAPPDATA\\Pal\\Saved\\PalLLM\\Packs\\personalities\\companion-warrior\" -Recurse" -ForegroundColor DarkGray
    Write-Host "  3. Reload:" -ForegroundColor DarkGray
    Write-Host "       Invoke-RestMethod $($BaseUrl.TrimEnd('/'))/api/packs/reload -Method Post" -ForegroundColor DarkGray
    Write-Host "  4. Re-run:  pal pack list" -ForegroundColor DarkGray
    Write-Host ""
    return
}

$rows = New-Object System.Collections.ArrayList
foreach ($p in $packs) {
    $id = if ($p.PSObject.Properties['id']) { [string]$p.id } else { '' }
    $name = if ($p.PSObject.Properties['displayName']) { [string]$p.displayName } else { '' }
    $version = if ($p.PSObject.Properties['version']) { [string]$p.version } else { '' }
    $tagline = if ($p.PSObject.Properties['tagline']) { [string]$p.tagline } else { '' }
    [void]$rows.Add([pscustomobject]@{ Id = $id; DisplayName = $name; Version = $version; Tagline = $tagline })
}

$maxIdWidth = ($rows | ForEach-Object { $_.Id.Length } | Measure-Object -Maximum).Maximum
$maxNameWidth = ($rows | ForEach-Object { $_.DisplayName.Length } | Measure-Object -Maximum).Maximum
foreach ($r in $rows) {
    $idPad = $r.Id.PadRight($maxIdWidth)
    $namePad = $r.DisplayName.PadRight($maxNameWidth)
    Write-Host ("  {0}  {1}  v{2}" -f $idPad, $namePad, $r.Version) -ForegroundColor White
    if ($r.Tagline) {
        Write-Host ("      {0}" -f $r.Tagline) -ForegroundColor DarkGray
    }
}
Write-Host ""
Write-Host "Reload from disk without restart:" -ForegroundColor DarkGray
Write-Host "  Invoke-RestMethod $($BaseUrl.TrimEnd('/'))/api/packs/reload -Method Post" -ForegroundColor DarkGray
Write-Host ""
