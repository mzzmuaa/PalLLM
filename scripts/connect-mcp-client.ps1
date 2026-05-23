<#
.SYNOPSIS
    Wires PalLLM into a desktop MCP client (Claude Desktop, VS Code,
    or Cursor) with one command instead of editing JSON by hand.

.DESCRIPTION
    Idempotently merges the PalLLM MCP server entry into the client's
    existing config file. Reads the file (creating it if missing), adds
    or refreshes the `palllm` entry, writes back. Existing entries from
    other servers are preserved. Comments above the merge note when the
    entry already existed.

    Supports -DryRun to preview without changing anything. Always
    backs up the existing file to <file>.bak before writing.

.PARAMETER Client
    Which client to wire. One of:
      - claude-desktop  (default; Anthropic Claude Desktop app)
      - vscode          (GitHub Copilot Chat agent mode)
      - cursor          (Cursor IDE)

.PARAMETER Url
    PalLLM MCP endpoint URL. Default http://localhost:5088/mcp.

.PARAMETER ApiKey
    Optional. If PalLLM is running with PalLLM:Auth:ApiKey set, pass it
    here and the `Authorization: Bearer ...` header is wired into the
    config (where the client supports headers).

.PARAMETER ConfigPath
    Override the config file location. Defaults to the platform-
    appropriate path for the chosen client.

.PARAMETER Workspace
    For -Client vscode only: write to the workspace .vscode/mcp.json
    instead of the global config. Default: workspace.

.PARAMETER DryRun
    Print the planned merge result without writing or backing up.

.EXAMPLE
    pwsh ./scripts/connect-mcp-client.ps1
    # Default: wires Claude Desktop pointing at localhost:5088/mcp.

.EXAMPLE
    pwsh ./scripts/connect-mcp-client.ps1 -Client vscode -DryRun
    # Preview the workspace .vscode/mcp.json that would be written.

.EXAMPLE
    pwsh ./scripts/connect-mcp-client.ps1 -Client cursor -ApiKey xyz123
    # Wire Cursor with bearer-token auth.

.NOTES
    Verb shortcut:  pal mcp connect <client>

    Authoritative example configs live in docs/examples/. This script
    derives the same shapes programmatically so the source of truth
    stays single.

    See docs/MCP_QUICKSTART.md for the broader walkthrough including
    troubleshooting and upstream MCP proxy setup.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Position = 0)]
    [ValidateSet('claude-desktop', 'vscode', 'cursor')]
    [string]$Client = 'claude-desktop',

    [string]$Url = 'http://localhost:5088/mcp',

    [string]$ApiKey,

    [string]$ConfigPath,

    [switch]$Workspace,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# -----------------------------------------------------------------------------
# Resolve config path per client + platform
# -----------------------------------------------------------------------------

function Get-DefaultConfigPath {
    param([string]$Client, [bool]$Workspace)

    switch ($Client) {
        'claude-desktop' {
            if ($IsWindows -or $env:OS -eq 'Windows_NT') {
                return Join-Path $env:APPDATA 'Claude\claude_desktop_config.json'
            }
            elseif ($IsMacOS) {
                return Join-Path $HOME 'Library/Application Support/Claude/claude_desktop_config.json'
            }
            else {
                return Join-Path $HOME '.config/Claude/claude_desktop_config.json'
            }
        }
        'vscode' {
            if ($Workspace) {
                return (Join-Path (Get-Location).Path '.vscode/mcp.json')
            }
            if ($IsWindows -or $env:OS -eq 'Windows_NT') {
                return Join-Path $env:APPDATA 'Code\User\mcp.json'
            }
            elseif ($IsMacOS) {
                return Join-Path $HOME 'Library/Application Support/Code/User/mcp.json'
            }
            else {
                return Join-Path $HOME '.config/Code/User/mcp.json'
            }
        }
        'cursor' {
            return Join-Path $HOME '.cursor/mcp.json'
        }
    }
}

# Default to workspace mode for vscode unless the operator opted out by
# setting -ConfigPath explicitly.
$resolvedWorkspace = if ($Client -eq 'vscode' -and -not $PSBoundParameters.ContainsKey('Workspace')) { $true } else { [bool]$Workspace }

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath -Client $Client -Workspace $resolvedWorkspace
}

$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

# -----------------------------------------------------------------------------
# Build the entry shape for the chosen client
# -----------------------------------------------------------------------------

function New-PalLlmEntry {
    param([string]$Client, [string]$Url, [string]$ApiKey)

    $headers = $null
    if (-not [string]::IsNullOrWhiteSpace($ApiKey)) {
        $headers = [ordered]@{ Authorization = "Bearer $ApiKey" }
    }

    switch ($Client) {
        'claude-desktop' {
            $entry = [ordered]@{
                url       = $Url
                transport = 'streamable-http'
            }
            if ($headers) { $entry.headers = $headers }
            return [pscustomobject]@{ ContainerKey = 'mcpServers'; ServerName = 'palllm'; Entry = $entry }
        }
        'vscode' {
            $entry = [ordered]@{
                type = 'http'
                url  = $Url
            }
            if ($headers) { $entry.headers = $headers }
            return [pscustomobject]@{ ContainerKey = 'servers'; ServerName = 'palllm'; Entry = $entry }
        }
        'cursor' {
            $entry = [ordered]@{
                url = $Url
            }
            if ($headers) { $entry.headers = $headers }
            return [pscustomobject]@{ ContainerKey = 'mcpServers'; ServerName = 'palllm'; Entry = $entry }
        }
    }
}

$plan = New-PalLlmEntry -Client $Client -Url $Url -ApiKey $ApiKey

# -----------------------------------------------------------------------------
# Read existing config (or start fresh)
# -----------------------------------------------------------------------------

# Helper: convert PSCustomObject -> nested [ordered]@{} so the result can
# be mutated. Works on both Windows PowerShell 5.1 (no -AsHashtable) and
# pwsh 7+. Arrays and primitives pass through unchanged.
function ConvertTo-MutableConfig {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in $Value.Keys) { $copy[$key] = ConvertTo-MutableConfig $Value[$key] }
        return $copy
    }
    if ($Value -is [psobject] -and $Value.PSObject.Properties.Count -gt 0 -and -not ($Value -is [string])) {
        $copy = [ordered]@{}
        foreach ($prop in $Value.PSObject.Properties) {
            $copy[$prop.Name] = ConvertTo-MutableConfig $prop.Value
        }
        return $copy
    }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $list = New-Object System.Collections.ArrayList
        foreach ($item in $Value) { [void]$list.Add((ConvertTo-MutableConfig $item)) }
        return $list.ToArray()
    }
    return $Value
}

# Helper: container types differ between [hashtable] (.ContainsKey) and
# [System.Collections.Specialized.OrderedDictionary] (.Contains). This
# wrapper hides the difference at the call site.
function Test-ConfigKey {
    param($Container, [string]$Key)
    if ($null -eq $Container) { return $false }
    if ($Container -is [System.Collections.IDictionary]) {
        return $Container.Contains($Key)
    }
    return $false
}

$existingConfig = [ordered]@{}
$alreadyHadEntry = $false

if (Test-Path -LiteralPath $ConfigPath) {
    try {
        $raw = Get-Content -LiteralPath $ConfigPath -Raw -Encoding UTF8
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            $parsed = $raw | ConvertFrom-Json -ErrorAction Stop
            $mutable = ConvertTo-MutableConfig $parsed
            if ($mutable -is [System.Collections.IDictionary]) {
                $existingConfig = $mutable
            }
            else {
                Write-Warning "[connect-mcp] Existing config at $ConfigPath has a top-level value that is not an object; replacing with an object containing the PalLLM entry."
                $existingConfig = [ordered]@{}
            }
            if (Test-ConfigKey $existingConfig $plan.ContainerKey) {
                $container = $existingConfig[$plan.ContainerKey]
                if (Test-ConfigKey $container $plan.ServerName) {
                    $alreadyHadEntry = $true
                }
            }
        }
    }
    catch {
        Write-Warning "[connect-mcp] Existing config at $ConfigPath is not valid JSON: $_"
        Write-Warning "[connect-mcp] Refusing to overwrite. Fix the JSON first or pass -ConfigPath to a different file."
        exit 1
    }
}

# -----------------------------------------------------------------------------
# Merge in the PalLLM entry
# -----------------------------------------------------------------------------

if (-not (Test-ConfigKey $existingConfig $plan.ContainerKey)) {
    $existingConfig[$plan.ContainerKey] = [ordered]@{}
}

$container = $existingConfig[$plan.ContainerKey]
if ($container -isnot [System.Collections.IDictionary]) {
    Write-Warning "[connect-mcp] Existing $($plan.ContainerKey) is not an object; replacing."
    $existingConfig[$plan.ContainerKey] = [ordered]@{}
    $container = $existingConfig[$plan.ContainerKey]
}

$container[$plan.ServerName] = $plan.Entry

# -----------------------------------------------------------------------------
# Print or write
# -----------------------------------------------------------------------------

$resultJson = $existingConfig | ConvertTo-Json -Depth 10

Write-Host ""
Write-Host "PalLLM MCP wiring plan" -ForegroundColor Cyan
Write-Host "  Client       : $Client"
Write-Host "  Config file  : $ConfigPath"
Write-Host "  PalLLM URL   : $Url"
if ($ApiKey) {
    Write-Host "  Authorization: Bearer ********** ($($ApiKey.Length) chars)"
} else {
    Write-Host "  Authorization: (none - assumes PalLLM:Auth:ApiKey unset)"
}
Write-Host "  Container key: $($plan.ContainerKey)"
Write-Host "  Server name  : $($plan.ServerName)"
if ($alreadyHadEntry) {
    Write-Host "  Action       : refresh (entry already existed)" -ForegroundColor Yellow
} else {
    Write-Host "  Action       : add (new entry)" -ForegroundColor Green
}
Write-Host ""

if ($DryRun.IsPresent) {
    Write-Host "Result that would be written (DryRun):" -ForegroundColor Yellow
    Write-Host $resultJson
    Write-Host ""
    Write-Host "[DryRun] No file changes." -ForegroundColor Yellow
    [pscustomobject]@{
        DryRun = $true
        ConfigPath = $ConfigPath
        Client = $Client
        AlreadyHadEntry = $alreadyHadEntry
        ResultJson = $resultJson
    } | Write-Output
    return
}

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM into $Client MCP config")) {
    return
}

# Ensure parent directory exists.
$parent = Split-Path -Parent $ConfigPath
if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}

# Backup any existing file once (so re-runs do not flatten the original).
if ((Test-Path -LiteralPath $ConfigPath) -and -not (Test-Path -LiteralPath "$ConfigPath.bak")) {
    Copy-Item -LiteralPath $ConfigPath -Destination "$ConfigPath.bak" -Force
    Write-Host "Backed up existing config to $ConfigPath.bak" -ForegroundColor DarkGray
}

Set-Content -LiteralPath $ConfigPath -Value $resultJson -Encoding UTF8

Write-Host "Wrote $ConfigPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
switch ($Client) {
    'claude-desktop' {
        Write-Host "  1. Quit and reopen Claude Desktop."
        Write-Host "  2. Click the slider icon in the chat composer; PalLLM should appear."
        Write-Host "  3. If it doesn't, check Claude Desktop's MCP server logs."
    }
    'vscode' {
        Write-Host "  1. Reload the VS Code window (Ctrl+Shift+P -> Developer: Reload Window)."
        Write-Host "  2. Open Copilot Chat in agent mode; PalLLM tools appear under the agent."
        Write-Host "  3. If they don't, check the Output channel: GitHub Copilot Chat - MCP."
    }
    'cursor' {
        Write-Host "  1. Quit and reopen Cursor."
        Write-Host "  2. Open the chat composer; PalLLM tools should be available."
        Write-Host "  3. If they don't appear, check Cursor's MCP server status panel."
    }
}
Write-Host ""
Write-Host "Verify the sidecar is reachable first:" -ForegroundColor White
Write-Host "  pal hello"
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    ConfigPath = $ConfigPath
    Client = $Client
    AlreadyHadEntry = $alreadyHadEntry
    Backup = "$ConfigPath.bak"
} | Write-Output
