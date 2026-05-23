<#
.SYNOPSIS
    Time PalLLM's cold-start path: dotnet run -> /health 200 ->
    first /api/chat with deterministic fallback.

.DESCRIPTION
    Pass 360 (Tier-A #6 from senior-dev review). PalLLM has
    per-method latency budgets in docs/HOT_PATH.md but cold-start
    -- the operator's actual "how long until I can chat?" metric
    -- wasn't measured. This script closes that gap.

    Phases timed:

      1. (optional) `dotnet build PalLLM.sln -c Release` from a
         clean intermediate-output state. Skipped by default;
         enable via -IncludeBuild.

      2. `dotnet run` the sidecar in the background, poll
         /health/live every 100ms until it returns 200, record
         time-to-ready.

      3. POST /api/chat with a synthetic prompt, record
         time-to-first-non-empty-reply. PalLLM's deterministic
         fallback director means this works without an LLM
         configured -- the SLO is the cold-start of the
         **runtime** not the **model**.

    Results write to artifacts/cold-start-benchmark/<ts>.json with
    a human-readable summary printed to stdout. The summary line
    is a single line so it can be pasted into a ticket / readme /
    chat without context.

.PARAMETER IncludeBuild
    Also time the `dotnet build` step before launching. Use for a
    truly-cold-clone baseline (e.g. after `git clean -fxd`).
    Default off because most operators just want runtime cold-start.

.PARAMETER ReadyTimeoutSeconds
    Maximum seconds to wait for /health/live. Default 90.

.PARAMETER ChatTimeoutSeconds
    Maximum seconds to wait for the first chat reply. Default 30.

.PARAMETER OutputDir
    Directory to write the JSON artifact. Defaults to
    artifacts/cold-start-benchmark/ relative to the repo root.

.PARAMETER DryRun
    Skip the actual measurement; write a sentinel JSON with phase
    keys present but all timings = -1. Used by the
    ScriptExecutionTests fixture so the test suite stays fast.

.EXAMPLE
    pwsh ./scripts/pal-benchmark-coldstart.ps1
    # Default: ~10-30 seconds; prints "ColdStart: build=skipped ready=Xs chat=Ys"

.EXAMPLE
    pwsh ./scripts/pal-benchmark-coldstart.ps1 -IncludeBuild
    # ~60-90 seconds; full clone-to-chat path with build timing

.NOTES
    Verb shortcut: pal benchmark cold-start (Pass 360).
    Pairs with docs/HOT_PATH.md "Cold-start" row.
#>
[CmdletBinding()]
param(
    [switch]$IncludeBuild,
    [int]$ReadyTimeoutSeconds = 90,
    [int]$ChatTimeoutSeconds = 30,
    [string]$OutputDir,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot 'artifacts/cold-start-benchmark'
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$artifactPath = Join-Path $OutputDir "$timestamp.json"

function New-StubResult {
    return [ordered]@{
        timestamp        = $timestamp
        dryRun           = $true
        host             = [ordered]@{
            os = [Environment]::OSVersion.VersionString
            arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
            processorCount = [Environment]::ProcessorCount
        }
        phases           = [ordered]@{
            buildSeconds       = -1
            readyTimeSeconds   = -1
            firstChatSeconds   = -1
        }
        summary          = 'DryRun: no measurement performed'
        artifactPath     = $artifactPath
    }
}

if ($DryRun.IsPresent) {
    Write-Host "[DryRun] cold-start benchmark stub" -ForegroundColor Yellow
    $result = New-StubResult
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $artifactPath -Encoding UTF8
    Write-Host "Artifact : $artifactPath"
    Write-Host "ColdStart: build=skipped ready=DryRun chat=DryRun"
    exit 0
}

# ---- Phase 1: optional build ---------------------------------------------

$buildSeconds = -1
if ($IncludeBuild.IsPresent) {
    Write-Host ""
    Write-Host "Phase 1: dotnet build (Release)..." -ForegroundColor Cyan
    $buildStart = Get-Date
    & dotnet build (Join-Path $repoRoot 'PalLLM.sln') --configuration Release --nologo --verbosity quiet 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE. Cold-start benchmark cannot proceed."
    }
    $buildSeconds = ((Get-Date) - $buildStart).TotalSeconds
    Write-Host ("  build time: {0:N2}s" -f $buildSeconds) -ForegroundColor Green
}

# ---- Phase 2: launch sidecar -> /health 200 ------------------------------

Write-Host ""
Write-Host "Phase 2: dotnet run -> /health/live ..." -ForegroundColor Cyan
$projectPath = Join-Path $repoRoot 'src/PalLLM.Sidecar/PalLLM.Sidecar.csproj'

# Use a non-standard port so this benchmark doesn't collide with a
# running player install on 5088. Random in 18000-19000.
$port = Get-Random -Minimum 18000 -Maximum 19000
$baseUrl = "http://127.0.0.1:$port"

$env:ASPNETCORE_URLS = $baseUrl
# Ensure the sidecar boots without auth -- StartupAuthGuard (Pass 354)
# only refuses non-loopback binds without auth, so this is safe and
# matches the dev-mode loopback posture.
$env:PalLLM__Auth__ApiKey = $null

$readyStart = Get-Date
$proc = Start-Process -FilePath 'dotnet' `
    -ArgumentList @('run', '--project', $projectPath, '--configuration', 'Release', '--no-build', '--', '--urls', $baseUrl) `
    -PassThru -NoNewWindow `
    -RedirectStandardOutput (Join-Path $OutputDir "$timestamp.sidecar.out.log") `
    -RedirectStandardError (Join-Path $OutputDir "$timestamp.sidecar.err.log")

$readySeconds = -1
try {
    $deadline = $readyStart.AddSeconds($ReadyTimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 100
        try {
            $resp = Invoke-WebRequest -Uri "$baseUrl/health/live" -UseBasicParsing -TimeoutSec 1 -ErrorAction Stop
            if ($resp.StatusCode -eq 200) {
                $readySeconds = ((Get-Date) - $readyStart).TotalSeconds
                Write-Host ("  ready in: {0:N2}s" -f $readySeconds) -ForegroundColor Green
                break
            }
        } catch {
            # Sidecar not up yet; keep polling.
        }
    }

    if ($readySeconds -lt 0) {
        throw "Sidecar did not reach /health/live within ${ReadyTimeoutSeconds}s. Check $OutputDir/$timestamp.sidecar.err.log."
    }

    # ---- Phase 3: first /api/chat ----------------------------------------

    Write-Host ""
    Write-Host "Phase 3: /api/chat (deterministic fallback) ..." -ForegroundColor Cyan
    $chatStart = Get-Date
    $chatBody = @{
        sender   = 'BenchmarkClient'
        message  = 'Cold-start benchmark ping'
        category = 'global'
    } | ConvertTo-Json
    $chatResp = Invoke-WebRequest -Uri "$baseUrl/api/chat" `
        -Method POST `
        -Body $chatBody `
        -ContentType 'application/json' `
        -UseBasicParsing `
        -TimeoutSec $ChatTimeoutSeconds
    $firstChatSeconds = ((Get-Date) - $chatStart).TotalSeconds

    if ($chatResp.StatusCode -ne 200) {
        throw "/api/chat returned HTTP $($chatResp.StatusCode). Body: $($chatResp.Content)"
    }
    Write-Host ("  first chat: {0:N2}s" -f $firstChatSeconds) -ForegroundColor Green
}
finally {
    if ($proc -and -not $proc.HasExited) {
        try { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue } catch { }
    }
}

# ---- Write artifact + summary --------------------------------------------

$result = [ordered]@{
    timestamp        = $timestamp
    dryRun           = $false
    host             = [ordered]@{
        os = [Environment]::OSVersion.VersionString
        arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        processorCount = [Environment]::ProcessorCount
    }
    phases           = [ordered]@{
        buildSeconds       = if ($buildSeconds -ge 0) { [math]::Round($buildSeconds, 2) } else { $null }
        readyTimeSeconds   = [math]::Round($readySeconds, 2)
        firstChatSeconds   = [math]::Round($firstChatSeconds, 2)
    }
    portUsed         = $port
    sidecarStdoutLog = "$timestamp.sidecar.out.log"
    sidecarStderrLog = "$timestamp.sidecar.err.log"
}

$buildLabel = if ($buildSeconds -ge 0) { ('{0:N2}s' -f $buildSeconds) } else { 'skipped' }
$summary = "ColdStart: build=$buildLabel ready=$([math]::Round($readySeconds, 2))s chat=$([math]::Round($firstChatSeconds, 2))s"
$result.summary = $summary

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $artifactPath -Encoding UTF8

Write-Host ""
Write-Host "Artifact : $artifactPath" -ForegroundColor White
Write-Host $summary -ForegroundColor Green
Write-Host ""
Write-Host "Reference-rig budget (per docs/HOT_PATH.md cold-start row):" -ForegroundColor DarkGray
Write-Host "  ready < 8s, first chat < 10s combined." -ForegroundColor DarkGray
