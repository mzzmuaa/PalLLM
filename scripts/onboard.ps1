<#
.SYNOPSIS
    First-time onboarding for PalLLM contributors. One command from a
    fresh clone to a verified working sidecar + open dashboard.

.DESCRIPTION
    Runs the four steps a new contributor (human or agent) takes to
    confirm the repo builds and works on their machine:

      1. dotnet build (Release) - verifies .NET 10 SDK + dependencies
      2. dotnet test  (Release) - confirms 1309 / 1309 tests pass
      3. drift audit (without coverage / SBOM / packaging for speed) -
         confirms 16 / 16 gates green
      4. boots the sidecar in the background and opens the Field
         Console dashboard (http://localhost:5088/) so you can see
         the running runtime

    Stops at the first failure and prints a focused diagnosis. On a
    healthy clone this completes in ~30 s and ends with the dashboard
    in your default browser.

.PARAMETER SkipDashboard
    Build + test + audit only; do not boot the sidecar or open the
    browser. Useful in CI or headless environments.

.PARAMETER SkipAudit
    Skip the drift audit step. Use only for fast iteration when you
    know you have not touched docs.

.PARAMETER Port
    Port the sidecar should bind to. Defaults to 5088.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/onboard.ps1

.EXAMPLE
    # CI / headless variant - no browser, no foreground sidecar
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts/onboard.ps1 -SkipDashboard

.NOTES
    Equivalent to running:
      dotnet build PalLLM.sln --configuration Release --nologo
      dotnet test  PalLLM.sln --configuration Release --nologo --verbosity quiet
      powershell -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging
      powershell -File scripts/play-palllm.ps1
    in sequence. This wrapper exists so a new contributor only has
    to remember one command.

    See docs/HANDOFF.md and AGENTS.md for what to do after onboarding.
#>
[CmdletBinding()]
param(
    [switch]$SkipDashboard,
    [switch]$SkipAudit,
    [int]$Port = 5088
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$slnPath  = Join-Path $repoRoot "PalLLM.sln"

function Write-Step {
    param([string]$Number, [string]$Title)
    Write-Host ""
    Write-Host "===== Step $Number / $script:TotalSteps : $Title =====" -ForegroundColor Cyan
}

function Write-Pass {
    param([string]$Message)
    Write-Host "  PASS - $Message" -ForegroundColor Green
}

function Write-Fail {
    param([string]$Message, [string]$Hint)
    Write-Host "  FAIL - $Message" -ForegroundColor Red
    if ($Hint) {
        Write-Host "  Hint:  $Hint" -ForegroundColor Yellow
    }
    exit 1
}

# Resolve total steps so progress messages stay accurate when flags skip work.
$script:TotalSteps = 3
if (-not $SkipAudit)     { $script:TotalSteps += 1 }
if (-not $SkipDashboard) { $script:TotalSteps += 1 }

Write-Host ""
Write-Host "PalLLM onboarding - one command from clone to working sidecar"  -ForegroundColor White
Write-Host "Repo: $repoRoot"                                                 -ForegroundColor DarkGray
Write-Host ""

# -- Step 1 ---------------------------------------------------------
Write-Step "1" "dotnet --info (sanity check toolchain)"
try {
    $info = & dotnet --info 2>&1
    $sdkLine = ($info | Select-String -Pattern '^\s*Version:').Line
    if (-not $sdkLine) { throw ".NET SDK not detected (dotnet --info returned no Version line)" }
    Write-Pass "Toolchain: $($sdkLine.Trim())"
} catch {
    Write-Fail "dotnet --info failed: $_" `
              "Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0"
}

# -- Step 2 ---------------------------------------------------------
Write-Step "2" "dotnet build (Release)"
$buildOutput = & dotnet build $slnPath --configuration Release --nologo --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host $buildOutput
    Write-Fail "Build failed (exit $LASTEXITCODE)" `
              "Read the output above; common cause: stale obj/ - try 'dotnet clean PalLLM.sln' first."
}
Write-Pass "Build succeeded"

# -- Step 3 ---------------------------------------------------------
Write-Step "3" "dotnet test (Release) - 1309 expected"
$testOutput = & dotnet test $slnPath --configuration Release --nologo --verbosity quiet --no-build 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host $testOutput
    Write-Fail "Tests failed (exit $LASTEXITCODE)" `
              "If a single test failed, the output above names the file. Re-run with --verbosity normal for details."
}
$passLine = $testOutput | Select-String -Pattern 'Passed!\s+-' | Select-Object -First 1
if ($passLine) { Write-Pass $passLine.Line.Trim() } else { Write-Pass "All tests passed" }

# -- Step 4 (optional) ----------------------------------------------
if (-not $SkipAudit) {
    Write-Step "4" "drift audit (16 gates, fast variant)"
    $auditScript = Join-Path $repoRoot "scripts/run_full_audit.ps1"
    $auditOutput = & powershell -NoProfile -ExecutionPolicy Bypass `
        -File $auditScript -SkipCoverage -SkipSbom -SkipPackaging -SkipTests 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host $auditOutput
        Write-Fail "Audit FAILED" `
                  "Open the latest artifacts/full-audit/<timestamp>/RESULTS.md to see which gate fired."
    }
    Write-Pass "Audit PASS (skipping the slow Coverage / SBOM / Packaging steps)"
}

# -- Step 5 (optional) ----------------------------------------------
if (-not $SkipDashboard) {
    $stepNumber = if ($SkipAudit) { "4" } else { "5" }
    Write-Step $stepNumber "boot sidecar + open Field Console"
    $dashboardUrl = "http://localhost:$Port/"
    Write-Host "  Booting sidecar in a new window..." -ForegroundColor DarkGray
    $sidecarProj = Join-Path $repoRoot "src/PalLLM.Sidecar/PalLLM.Sidecar.csproj"
    Start-Process -FilePath "dotnet" -ArgumentList @(
        "run", "--no-build", "--configuration", "Release",
        "--project", $sidecarProj, "--urls", "http://localhost:$Port"
    ) -WorkingDirectory $repoRoot
    Write-Pass "Sidecar starting on port $Port"
    # Give it a few seconds to bind before the browser races to /.
    Start-Sleep -Seconds 4
    Write-Host "  Opening $dashboardUrl ..." -ForegroundColor DarkGray
    Start-Process $dashboardUrl
    Write-Pass "Field Console open"
}

# -- Done -----------------------------------------------------------
Write-Host ""
Write-Host "Onboarding complete." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  - Read docs/HANDOFF.md for the current state and 'what just landed' summary."
Write-Host "  - Read AGENTS.md (or CLAUDE.md) if you are a coding agent picking this up."
Write-Host "  - The dashboard at http://localhost:$Port/ shows live runtime posture."
Write-Host "  - To stop the sidecar window: close it, or Ctrl+C in the sidecar console."
Write-Host ""
