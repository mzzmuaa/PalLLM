[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$RunSmoke,
    [switch]$RunDeliveryReplay,
    [string]$BaseUrl = "http://localhost:5088"
)

# One-shot developer loop. Default run is fast and deterministic: build + test.
# Add -RunSmoke / -RunDeliveryReplay to also exercise the HTTP path against a
# running sidecar. This script exists so "am I still green after my edit?" is
# one command instead of three.

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

$repoRoot = Get-PalLlmRepoRoot
$solution = Join-Path $repoRoot "PalLLM.sln"
if (-not (Test-Path -LiteralPath $solution)) {
    throw "PalLLM.sln was not found at $solution"
}

if (-not (Test-CommandAvailable -CommandName "dotnet")) {
    throw "dotnet is not on PATH. Install the .NET 10 SDK before running update-all."
}

$steps = [System.Collections.Generic.List[object]]::new()

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Action
    )
    $start = [DateTime]::UtcNow
    Write-Host ""
    Write-Host ("=== {0} ===" -f $Name) -ForegroundColor Cyan
    try {
        & $Action
        $status = "PASS"
    }
    catch {
        $status = "FAIL"
        Write-Host ("!! {0} failed: {1}" -f $Name, $_.Exception.Message) -ForegroundColor Red
    }
    $elapsed = [int]([DateTime]::UtcNow - $start).TotalMilliseconds
    $steps.Add([pscustomobject]@{ Name = $Name; Status = $status; DurationMs = $elapsed })
    if ($status -eq "FAIL") {
        Write-Host ""
        Write-Host "update-all stopped after a failing step." -ForegroundColor Red
        $steps | Format-Table -AutoSize
        exit 1
    }
}

if (-not $SkipBuild) {
    Invoke-Step -Name "dotnet build" -Action {
        & dotnet build $solution --nologo --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet build exited $LASTEXITCODE" }
    }
}

if (-not $SkipTests) {
    Invoke-Step -Name "dotnet test" -Action {
        & dotnet test $solution --nologo --verbosity minimal --no-build
        if ($LASTEXITCODE -ne 0) { throw "dotnet test exited $LASTEXITCODE" }
    }
}

if ($RunSmoke) {
    $smokeScript = Join-Path $PSScriptRoot "run-sidecar-smoke.ps1"
    Invoke-Step -Name "sidecar smoke" -Action {
        & $smokeScript -BaseUrl $BaseUrl | Out-Null
    }
}

if ($RunDeliveryReplay) {
    $replayScript = Join-Path $PSScriptRoot "run-delivery-replay.ps1"
    Invoke-Step -Name "delivery replay" -Action {
        & $replayScript -BaseUrl $BaseUrl | Out-Null
    }
}

Write-Host ""
Write-Host "update-all summary:" -ForegroundColor Green
$steps | Format-Table -AutoSize
