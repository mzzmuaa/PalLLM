[CmdletBinding()]
param(
    [string]$PalworldPath,
    [ValidateSet("Copy", "Junction")]
    [string]$InstallMode = "Junction"
)

$ErrorActionPreference = "Stop"

$installScript = Join-Path $PSScriptRoot "install-mod.ps1"
if (-not (Test-Path -LiteralPath $installScript)) {
    throw "install-mod.ps1 was not found next to install-dev-mod.ps1"
}

& $installScript -PalworldPath $PalworldPath -InstallMode $InstallMode
