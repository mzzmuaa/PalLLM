# =============================================================================
# PalLLM - One-click recovery
# =============================================================================
#
# When something looks stuck (sidecar unresponsive, envelopes backed up,
# dashboard showing Critical), the recovery flow is:
#
#   1. Stop any running sidecar process cleanly, fall back to Stop-Process.
#   2. Clear stuck bridge envelopes (Inbox+Outbox) to an archive folder
#      under the runtime root so nothing is lost — the archive is
#      timestamped and retained for 14 days so an operator can inspect
#      what was stuck.
#   3. Prune durable evidence files older than -RetainEvidenceDays
#      (default 14) so the launch/support/proof history doesn't grow
#      without bound.
#   4. Start the sidecar back up.
#   5. Probe /health/live + /api/describe and report the operator
#      happiness score so the operator knows immediately whether
#      recovery worked.
#
# Every step is best-effort: a sidecar that was already stopped, an
# empty outbox, or a missing evidence folder are all success cases.
#
# Usage (from repo root or extracted release zip):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/recover-palllm.ps1
#
# Flags:
#   -BaseUrl             Sidecar URL (default http://localhost:5088).
#   -RetainEvidenceDays  How many days of durable evidence to keep
#                        (default 14). Set to 0 to skip pruning.
#   -SkipRestart         Stop + clean, but don't start the sidecar again.
#   -BootTimeoutSeconds  How long to wait for the restarted sidecar to
#                        answer (default 25).
#
# Exit code 0 on success, non-zero if the final probe never succeeded.
# =============================================================================

[CmdletBinding()]
param(
    [string]$BaseUrl = "http://localhost:5088",
    [int]$RetainEvidenceDays = 14,
    [switch]$SkipRestart,
    [int]$BootTimeoutSeconds = 25
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

function Stop-SidecarProcesses {
    $stopped = 0
    # Target anything listening on the configured port first (cleanest).
    try {
        $uri = [Uri]$BaseUrl
        $port = if ($uri.Port -gt 0) { $uri.Port } else { 5088 }
        $connections = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
        foreach ($conn in $connections) {
            try {
                Stop-Process -Id $conn.OwningProcess -Force -ErrorAction SilentlyContinue
                $stopped++
            } catch { }
        }
    } catch { }

    # Also sweep anything obviously named like the sidecar process (covers
    # an operator who changed the port after the sidecar booted).
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessName -eq "PalLLM.Sidecar" -or $_.ProcessName -eq "dotnet" -and ($_.Path -like "*PalLLM.Sidecar*") } |
        ForEach-Object {
            try {
                Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                $stopped++
            } catch { }
        }

    return $stopped
}

function Archive-StuckEnvelopes {
    param([Parameter(Mandatory)][object]$Dirs)

    $stamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
    $archiveRoot = Join-Path $Dirs.RuntimeRoot ("Bridge/RecoveryArchive/" + $stamp)
    $archivedCount = 0

    foreach ($kind in @("Inbox", "Outbox")) {
        $source = $Dirs.$kind
        if (-not (Test-Path -LiteralPath $source)) { continue }
        $envelopes = Get-ChildItem -LiteralPath $source -File -ErrorAction SilentlyContinue
        if (-not $envelopes) { continue }
        $dest = Join-Path $archiveRoot $kind
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
        foreach ($envelope in $envelopes) {
            try {
                Move-Item -LiteralPath $envelope.FullName -Destination $dest -Force -ErrorAction SilentlyContinue
                $archivedCount++
            } catch { }
        }
    }

    # 14-day retention on the recovery archive itself — older recoveries
    # are no longer interesting.
    $archiveParent = Join-Path $Dirs.RuntimeRoot "Bridge/RecoveryArchive"
    if (Test-Path -LiteralPath $archiveParent) {
        $cutoff = (Get-Date).AddDays(-14)
        Get-ChildItem -LiteralPath $archiveParent -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt $cutoff } |
            ForEach-Object {
                try { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue } catch { }
            }
    }

    return [pscustomobject]@{
        ArchiveRoot = $archiveRoot
        ArchivedCount = $archivedCount
    }
}

function Prune-DurableEvidence {
    param(
        [Parameter(Mandatory)][object]$Dirs,
        [Parameter(Mandatory)][int]$Days
    )

    if ($Days -le 0) { return 0 }

    $cutoff = (Get-Date).AddDays(-$Days)
    $pruned = 0

    # Evidence folders PalLLM maintains under the runtime root.
    $historyRoots = @(
        (Join-Path $Dirs.RuntimeRoot "LaunchEvidence/History"),
        (Join-Path $Dirs.RuntimeRoot "ReleaseEvidence/History"),
        (Join-Path $Dirs.RuntimeRoot "SupportEvidence/History")
    )

    foreach ($root in $historyRoots) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        Get-ChildItem -LiteralPath $root -File -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -lt $cutoff } |
            ForEach-Object {
                try {
                    Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue
                    $pruned++
                } catch { }
            }
    }

    return $pruned
}

function Start-SidecarAgain {
    $startScript = Join-Path $PSScriptRoot "start-sidecar.ps1"
    if (-not (Test-Path -LiteralPath $startScript)) {
        throw "start-sidecar.ps1 not found next to recover-palllm.ps1. Extract the full release zip."
    }

    Start-Process -FilePath "powershell" -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $startScript,
        "-BaseUrl", $BaseUrl
    ) -WindowStyle Hidden | Out-Null
}

function Wait-ForHealthyReboot {
    param([Parameter(Mandatory)][int]$TimeoutSeconds)

    $deadline = (Get-Date).AddSeconds([Math]::Max(1, $TimeoutSeconds))
    while ((Get-Date) -lt $deadline) {
        try {
            $desc = Invoke-PalLlmApi -Method GET -BaseUrl $BaseUrl -Path "/api/describe" -TimeoutSeconds 3
            if ($desc) { return $desc }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host " PalLLM recovery" -ForegroundColor Cyan
Write-Host "=====================================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "Step 1/4 — stop any running sidecar..." -ForegroundColor Yellow
$stoppedCount = Stop-SidecarProcesses
Write-Host "  stopped $stoppedCount process(es)." -ForegroundColor DarkGray
Start-Sleep -Milliseconds 500

Write-Host "Step 2/4 — archive stuck envelopes + prune old evidence..." -ForegroundColor Yellow
$dirs = Get-PalLlmExpectedRuntimeDirectories
$archiveInfo = Archive-StuckEnvelopes -Dirs $dirs
Write-Host ("  archived " + $archiveInfo.ArchivedCount + " envelope(s) to " + $archiveInfo.ArchiveRoot) -ForegroundColor DarkGray
$prunedCount = Prune-DurableEvidence -Dirs $dirs -Days $RetainEvidenceDays
Write-Host ("  pruned " + $prunedCount + " evidence file(s) older than " + $RetainEvidenceDays + " days.") -ForegroundColor DarkGray

if ($SkipRestart) {
    Write-Host ""
    Write-Host "Step 3/4 — restart skipped (-SkipRestart)." -ForegroundColor Yellow
    Write-Host ""
    exit 0
}

Write-Host ""
Write-Host "Step 3/4 — restart sidecar..." -ForegroundColor Yellow
Start-SidecarAgain

Write-Host ("Step 4/4 — wait up to " + $BootTimeoutSeconds + "s for /api/describe...") -ForegroundColor Yellow
$description = Wait-ForHealthyReboot -TimeoutSeconds $BootTimeoutSeconds
if (-not $description) {
    Write-Host ""
    Write-Host "Recovery probe timed out. The sidecar may still be starting — run scripts/doctor.ps1 to inspect." -ForegroundColor Red
    exit 1
}

$operatorHealth = Get-PalLlmPropertyValue -InputObject $description -Name "OperatorHealth"
$scoreValue = if ($operatorHealth) { Get-PalLlmPropertyValue -InputObject $operatorHealth -Name "Score" } else { $null }
$grade = if ($operatorHealth) { Get-PalLlmPropertyValue -InputObject $operatorHealth -Name "Grade" } else { "unknown" }
$summary = if ($operatorHealth) { Get-PalLlmPropertyValue -InputObject $operatorHealth -Name "Summary" } else { "" }

Write-Host ""
Write-Host "=====================================================" -ForegroundColor Green
Write-Host (" Recovery complete. Operator health: " + $grade + " ($scoreValue/100)") -ForegroundColor Green
if ($summary) {
    Write-Host ("  " + $summary) -ForegroundColor DarkGray
}
Write-Host ("  Dashboard: " + $BaseUrl) -ForegroundColor DarkGray
Write-Host "=====================================================" -ForegroundColor Green
Write-Host ""

exit 0
