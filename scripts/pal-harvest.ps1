<#
.SYNOPSIS
    Discover the harvestable units in this repo (capabilities you can lift
    into another project) and print their file lists + doc references.
    The "where do I copy from if I just want X?" verb.

.DESCRIPTION
    Reads the `harvestableUnits` block from agents.json (the canonical
    machine-readable surface; written in lockstep with HARVEST.md) and
    presents two views:

      - `pal harvest list`            -> table of units (name + doc)
      - `pal harvest show <name>`     -> detailed view (files + doc + adr)

    The harvest verb does NOT copy files. It tells you which files to
    copy and which doc to read first. Lifting a capability into another
    project is intentionally a hand-curated step - autocopy would miss
    the per-codebase namespace renames and dependency choices.

    Pure local read; no network call.

.PARAMETER Action
    Subcommand: list (default) or show.

.PARAMETER Name
    For 'show': the harvestable unit name (case-insensitive prefix
    match against the unit's 'name' field).

.PARAMETER Json
    Emit a structured record instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-harvest.ps1
    # List all harvestable units (table view).

.EXAMPLE
    pwsh ./scripts/pal-harvest.ps1 show "personality pack"
    # Show files + doc references for the personality-pack subsystem.

.NOTES
    Verb shortcut:  pal harvest [list|show <name>]

    Read first when lifting:
      docs/HARVEST.md       walkthrough per capability
      docs/CONVENTIONS.md   advisor / builder / validator / feeder patterns
      docs/CODE_MAP.md      symbol-to-file index
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('list', 'show')]
    [string]$Action = 'list',

    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$NameParts = @(),

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$agentsPath = Join-Path $repoRoot 'agents.json'
if (-not (Test-Path -LiteralPath $agentsPath)) {
    Write-Error "agents.json not found at $agentsPath"
    exit 1
}

$manifest = Get-Content -LiteralPath $agentsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$units = $null
if ($manifest.PSObject.Properties['harvestableUnits']) {
    $units = $manifest.harvestableUnits
}
if (-not $units) {
    Write-Error "agents.json has no harvestableUnits block."
    exit 1
}

$patterns = if ($units.PSObject.Properties['patterns']) { @($units.patterns) } else { @() }
$subsystems = if ($units.PSObject.Properties['subsystems']) { @($units.subsystems) } else { @() }

function Show-List {
    if ($Json.IsPresent) {
        [pscustomobject]@{
            Patterns   = $patterns
            Subsystems = $subsystems
        } | ConvertTo-Json -Depth 6
        return
    }

    Write-Host ""
    Write-Host "PalLLM harvestable units" -ForegroundColor Cyan
    Write-Host "  source: agents.json -> harvestableUnits" -ForegroundColor DarkGray
    Write-Host ""

    if ($patterns.Count -gt 0) {
        Write-Host "Patterns (the small set you'll see throughout the repo):" -ForegroundColor White
        foreach ($p in $patterns) {
            $name = if ($p.PSObject.Properties['name']) { [string]$p.name } else { '(unnamed)' }
            $doc  = if ($p.PSObject.Properties['doc'])  { [string]$p.doc }  else { '' }
            Write-Host ("  - {0,-30}  {1}" -f $name, $doc)
        }
        Write-Host ""
    }

    if ($subsystems.Count -gt 0) {
        Write-Host "Subsystems (independently liftable capabilities):" -ForegroundColor White
        foreach ($s in $subsystems) {
            $name = if ($s.PSObject.Properties['name']) { [string]$s.name } else { '(unnamed)' }
            $doc  = if ($s.PSObject.Properties['doc'])  { [string]$s.doc }  else { '' }
            Write-Host ("  - {0,-40}  {1}" -f $name, $doc)
        }
        Write-Host ""
    }

    Write-Host "Show one in detail:" -ForegroundColor DarkGray
    Write-Host '  pal harvest show "personality pack"' -ForegroundColor DarkGray
    Write-Host '  pal harvest show "deterministic fallback"' -ForegroundColor DarkGray
    Write-Host ""
}

function Show-One {
    $name = ($NameParts -join ' ').Trim()
    if ([string]::IsNullOrWhiteSpace($name)) {
        Write-Error "Pass the unit name. Examples: pal harvest show ""personality pack"""
        exit 1
    }
    $needle = $name.ToLowerInvariant()

    $matches = New-Object System.Collections.ArrayList
    foreach ($p in $patterns)   { if ($p.name -and $p.name.ToLowerInvariant().StartsWith($needle)) { [void]$matches.Add(@{ Kind = 'pattern';   Item = $p }) } }
    foreach ($s in $subsystems) { if ($s.name -and $s.name.ToLowerInvariant().StartsWith($needle)) { [void]$matches.Add(@{ Kind = 'subsystem'; Item = $s }) } }
    foreach ($p in $patterns)   { if ($p.name -and $p.name.ToLowerInvariant().Contains($needle)   -and -not $p.name.ToLowerInvariant().StartsWith($needle)) { [void]$matches.Add(@{ Kind = 'pattern';   Item = $p }) } }
    foreach ($s in $subsystems) { if ($s.name -and $s.name.ToLowerInvariant().Contains($needle)   -and -not $s.name.ToLowerInvariant().StartsWith($needle)) { [void]$matches.Add(@{ Kind = 'subsystem'; Item = $s }) } }

    if ($matches.Count -eq 0) {
        Write-Host ""
        Write-Host "No harvestable unit matched '$name'." -ForegroundColor Yellow
        Write-Host "Try: pal harvest list" -ForegroundColor DarkGray
        Write-Host ""
        exit 1
    }

    $picked = $matches[0]

    if ($Json.IsPresent) {
        [pscustomobject]@{
            Kind = $picked.Kind
            Item = $picked.Item
        } | ConvertTo-Json -Depth 6
        return
    }

    $item = $picked.Item
    $itemName = if ($item.PSObject.Properties['name']) { [string]$item.name } else { '(unnamed)' }
    Write-Host ""
    Write-Host ("Harvestable unit: {0}" -f $itemName) -ForegroundColor Cyan
    Write-Host ("  kind : {0}" -f $picked.Kind)

    if ($item.PSObject.Properties['doc']) {
        Write-Host ("  doc  : {0}" -f $item.doc)
    }
    if ($item.PSObject.Properties['convention']) {
        Write-Host ("  conv : {0}" -f $item.convention)
    }
    if ($item.PSObject.Properties['files']) {
        Write-Host ""
        Write-Host "  files (lift these into your project):" -ForegroundColor White
        foreach ($f in $item.files) {
            Write-Host ("    {0}" -f $f)
        }
    }

    Write-Host ""
    Write-Host "Read first when lifting:" -ForegroundColor DarkGray
    Write-Host "  docs/HARVEST.md       walkthrough per capability" -ForegroundColor DarkGray
    Write-Host "  docs/CONVENTIONS.md   the four patterns this repo uses" -ForegroundColor DarkGray
    Write-Host "  docs/CODE_MAP.md      symbol-to-file index" -ForegroundColor DarkGray
    Write-Host ""
}

switch ($Action) {
    'list' { Show-List }
    'show' { Show-One }
}
