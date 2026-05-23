[CmdletBinding()]
param(
    [string]$PalworldPath,
    [string]$BaseUrl = "http://localhost:5088",
    [int]$BootTimeoutSeconds = 25,
    [switch]$SkipInstall,
    [switch]$SkipDoctor,
    [switch]$SkipWarmup,
    [switch]$RunSmoke,
    [switch]$SkipDashboard,
    [switch]$SkipGameLaunch,
    [switch]$RequireWarmup
)

$ErrorActionPreference = "Stop"

# Platform guard. The full play loop installs the UE4SS Lua mod and
# launches Palworld -- both Windows-only. The sidecar itself runs
# cross-platform; on non-Windows hosts the operator should boot the
# sidecar directly with `dotnet run` and use the dashboard / MCP
# without the in-game mod.
if ($PSVersionTable.PSEdition -eq 'Core' -and -not $IsWindows) {
    Write-Host ""
    Write-Host "pal play is Windows-only (it installs the UE4SS mod and launches Palworld)." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  Cross-platform alternatives:"
    Write-Host "    dotnet run --project src/PalLLM.Sidecar/PalLLM.Sidecar.csproj"
    Write-Host "      Boots just the sidecar + dashboard + MCP server."
    Write-Host "    pwsh ./pal.ps1 run"
    Write-Host "      Same thing through the verb table."
    Write-Host ""
    Write-Host "  See docs/COMPATIBILITY.md > 'Operating systems' for the full"
    Write-Host "  per-OS support matrix."
    Write-Host ""
    exit 1
}

. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

function Test-SidecarHealth {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl
    )

    try {
        return Invoke-PalLlmApi -Method GET -BaseUrl $BaseUrl -Path "/api/health" -TimeoutSeconds 2
    }
    catch {
        return $null
    }
}

function Wait-ForSidecarHealth {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).ToUniversalTime().AddSeconds([Math]::Max(1, $TimeoutSeconds))
    while ((Get-Date).ToUniversalTime() -lt $deadline) {
        $health = Test-SidecarHealth -BaseUrl $BaseUrl
        if ($health) {
            return $health
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Invoke-PlayerWarmup {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [object]$HealthPayload,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds,

        [Parameter(Mandatory = $true)]
        [bool]$RequireWarmup
    )

    $inferenceConfigured = [bool](Get-PalLlmPropertyValue -InputObject $HealthPayload -Name "InferenceConfigured")
    $warmupSnapshot = Get-PalLlmPropertyValue -InputObject $HealthPayload -Name "InferenceWarmup"
    $warmupEnabled = [bool](Get-PalLlmPropertyValue -InputObject $warmupSnapshot -Name "Enabled")
    $activeModel = [string](Get-PalLlmPropertyValue -InputObject $HealthPayload -Name "InferenceActiveModel")
    if ([string]::IsNullOrWhiteSpace($activeModel)) {
        $activeModel = [string](Get-PalLlmPropertyValue -InputObject $HealthPayload -Name "InferenceModel")
    }

    if (-not $inferenceConfigured) {
        return [pscustomobject]@{
            Status = "skipped"
            Detail = "Inference disabled; deterministic fallback remains the primary player path."
            Succeeded = $true
        }
    }

    if (-not $warmupEnabled) {
        return [pscustomobject]@{
            Status = "skipped"
            Detail = "Inference is enabled, but warmup is disabled in configuration; first live reply may pay cold-start latency."
            Succeeded = $true
        }
    }

    try {
        $warmup = Invoke-PalLlmApi -Method POST -BaseUrl $BaseUrl -Path "/api/inference/warmup" -TimeoutSeconds ([Math]::Max(5, $TimeoutSeconds))
        $status = [string](Get-PalLlmPropertyValue -InputObject $warmup -Name "Status")
        $statusMessage = [string](Get-PalLlmPropertyValue -InputObject $warmup -Name "StatusMessage")
        if ([string]::IsNullOrWhiteSpace($status)) {
            $status = "unknown"
        }

        $detail = if ([string]::IsNullOrWhiteSpace($statusMessage)) {
            "Warmup status '$status' reported for $activeModel."
        }
        else {
            $statusMessage
        }

        return [pscustomobject]@{
            Status = $status
            Detail = $detail
            Succeeded = $true
        }
    }
    catch {
        $message = "Warmup request failed: $($_.Exception.Message)"
        if ($RequireWarmup) {
            throw $message
        }

        Write-Warning $message
        return [pscustomobject]@{
            Status = "failed"
            Detail = $message
            Succeeded = $false
        }
    }
}

function Invoke-OptionalPalLlmGet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$Path,

        [int]$TimeoutSeconds = 5
    )

    try {
        return Invoke-PalLlmApi -Method GET -BaseUrl $BaseUrl -Path $Path -TimeoutSeconds $TimeoutSeconds
    }
    catch {
        return $null
    }
}

function Write-PlayerLaunchArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [object]$InstallResult,

        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [string]$SidecarStatus,

        [Parameter(Mandatory = $true)]
        [object]$WarmupResult,

        [Parameter(Mandatory = $true)]
        [bool]$DoctorRan,

        [Parameter(Mandatory = $true)]
        [bool]$SmokeRan,

        [Parameter(Mandatory = $true)]
        [bool]$DashboardOpened,

        [Parameter(Mandatory = $true)]
        [bool]$GameLaunched,

        [string]$GameExePath,

        [AllowNull()]
        [object]$HealthPayload,

        [AllowNull()]
        [object]$ReleaseReadinessPayload,

        [AllowNull()]
        [object]$BridgeProofPayload
    )

    $runtimeRoot = Get-PalLlmRuntimeRoot
    $launchEvidenceDir = Join-Path $runtimeRoot "LaunchEvidence"
    $historyDir = Join-Path $launchEvidenceDir "History"
    New-Item -ItemType Directory -Force -Path $historyDir | Out-Null

    $historyStamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $historyJsonPath = Join-Path $historyDir ("player-launch-{0}.json" -f $historyStamp)
    $historyMarkdownPath = Join-Path $historyDir ("player-launch-{0}.md" -f $historyStamp)
    $latestJsonPath = Join-Path $launchEvidenceDir "latest-player-launch.json"
    $latestMarkdownPath = Join-Path $launchEvidenceDir "latest-player-launch.md"

    $nativeReadiness = Get-PalLlmPropertyValue -InputObject $HealthPayload -Name "NativeReadiness"
    $releaseNativeProof = Get-PalLlmPropertyValue -InputObject $ReleaseReadinessPayload -Name "NativeProofEvidence"
    $bridgeProofStatus = [string](Get-PalLlmPropertyValue -InputObject $BridgeProofPayload -Name "Status")
    $nativeProofStatus = [string](Get-PalLlmPropertyValue -InputObject $releaseNativeProof -Name "Status")
    $hudRecommendation = Get-PalLlmPropertyValue -InputObject $nativeReadiness -Name "HudBindRecommendation"
    $recommendedHudTarget = [string](Get-PalLlmPropertyValue -InputObject $hudRecommendation -Name "RecommendedTarget")

    $nextSteps = [System.Collections.Generic.List[string]]::new()
    if (-not $DoctorRan) {
        $nextSteps.Add("Run scripts\\doctor.ps1 -RunSmoke so the packaged install gets the full support baseline.")
    }
    if ([string]::Equals([string]$WarmupResult.Status, "failed", [System.StringComparison]::OrdinalIgnoreCase)) {
        $nextSteps.Add("POST /api/inference/warmup or rerun play.bat -RequireWarmup so the active live lane is hot before the first turn.")
    }
    if (-not [string]::Equals($nativeProofStatus, "proven", [System.StringComparison]::OrdinalIgnoreCase)) {
        $nextSteps.Add("Run scripts\\run-native-proof.ps1 during a live Palworld session so release/readiness records real native delivery evidence.")
    }
    if (-not [string]::IsNullOrWhiteSpace($recommendedHudTarget) -and -not [bool](Get-PalLlmPropertyValue -InputObject $nativeReadiness -Name "HudBindReady")) {
        $nextSteps.Add("Apply scripts\\apply-hud-bind-recommendation.ps1 if you want the current recommended native HUD target written into config/native-hud.lua.")
    }

    $artifact = [pscustomobject]@{
        generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
        launcher = [pscustomobject]@{
            mode = "one_click_player_launcher"
            base_url = $BaseUrl
            sidecar_status = $SidecarStatus
            doctor_ran = $DoctorRan
            smoke_ran = $SmokeRan
            dashboard_opened = $DashboardOpened
            game_launched = $GameLaunched
            game_executable = $GameExePath
        }
        install = [pscustomobject]@{
            palworld_root = $InstallResult.PalworldRoot
            win64_path = $InstallResult.Win64Path
            mod_root = $InstallResult.ModRoot
            mod_target_path = $InstallResult.TargetPath
            install_mode = $InstallResult.InstallMode
            enabled_file = $InstallResult.EnabledFile
            sample_pack_installed = $InstallResult.SamplePackInstalled
            sample_pack_path = $InstallResult.SamplePackPath
        }
        warmup = $WarmupResult
        runtime = [pscustomobject]@{
            runtime_root = $runtimeRoot
            launch_evidence_dir = $launchEvidenceDir
        }
        health = $HealthPayload
        release_readiness = $ReleaseReadinessPayload
        bridge_proof = $BridgeProofPayload
        next_steps = @($nextSteps | Select-Object -Unique)
    }

    $jsonText = (ConvertTo-PalLlmJsonValue -InputObject $artifact | ConvertTo-Json -Depth 12)
    Set-Content -LiteralPath $historyJsonPath -Value $jsonText -Encoding utf8
    Copy-Item -LiteralPath $historyJsonPath -Destination $latestJsonPath -Force

    $markdown = New-Object System.Text.StringBuilder
    [void]$markdown.AppendLine("# PalLLM Player Launch")
    [void]$markdown.AppendLine("")
    [void]$markdown.AppendLine("- Generated (UTC): $($artifact.generated_at_utc)")
    [void]$markdown.AppendLine("- Base URL: $BaseUrl")
    [void]$markdown.AppendLine("- Sidecar: $SidecarStatus")
    [void]$markdown.AppendLine("- Doctor ran: $DoctorRan")
    [void]$markdown.AppendLine("- Warmup: $($WarmupResult.Detail)")
    [void]$markdown.AppendLine("- Game launched: $GameLaunched")
    if (-not [string]::IsNullOrWhiteSpace($GameExePath)) {
        [void]$markdown.AppendLine("- Game executable: $GameExePath")
    }
    [void]$markdown.AppendLine("- Palworld root: $($InstallResult.PalworldRoot)")
    [void]$markdown.AppendLine("- Installed mod target: $($InstallResult.TargetPath)")
    [void]$markdown.AppendLine("- Runtime root: $runtimeRoot")
    if (-not [string]::IsNullOrWhiteSpace($bridgeProofStatus)) {
        [void]$markdown.AppendLine("- Bridge proof status: $bridgeProofStatus")
    }
    if (-not [string]::IsNullOrWhiteSpace($nativeProofStatus)) {
        [void]$markdown.AppendLine("- Native proof status: $nativeProofStatus")
    }
    if (-not [string]::IsNullOrWhiteSpace($recommendedHudTarget)) {
        [void]$markdown.AppendLine("- Recommended HUD target: $recommendedHudTarget")
    }
    [void]$markdown.AppendLine("")
    [void]$markdown.AppendLine("## Next Steps")
    [void]$markdown.AppendLine("")
    if ($artifact.next_steps.Count -eq 0) {
        [void]$markdown.AppendLine("- No follow-up action suggested from the current launcher snapshot.")
    }
    else {
        foreach ($step in $artifact.next_steps) {
            [void]$markdown.AppendLine("- $step")
        }
    }

    Set-Content -LiteralPath $historyMarkdownPath -Value $markdown.ToString() -Encoding utf8
    Copy-Item -LiteralPath $historyMarkdownPath -Destination $latestMarkdownPath -Force

    return [pscustomobject]@{
        LatestJsonPath = $latestJsonPath
        LatestMarkdownPath = $latestMarkdownPath
        HistoryJsonPath = $historyJsonPath
        HistoryMarkdownPath = $historyMarkdownPath
    }
}

$normalizedBaseUrl = Get-PalLlmNormalizedBaseUrl -BaseUrl $BaseUrl
$repoRoot = Get-PalLlmRepoRoot
$installScript = Join-Path $PSScriptRoot "install-mod.ps1"
$doctorScript = Join-Path $PSScriptRoot "doctor.ps1"
$startScript = Join-Path $PSScriptRoot "start-sidecar.ps1"

if (-not (Test-Path -LiteralPath $installScript)) {
    throw "play-palllm.ps1 expected install-mod.ps1 next to itself, but the file was missing."
}
if (-not (Test-Path -LiteralPath $doctorScript)) {
    throw "play-palllm.ps1 expected doctor.ps1 next to itself, but the file was missing."
}
if (-not (Test-Path -LiteralPath $startScript)) {
    throw "play-palllm.ps1 expected start-sidecar.ps1 next to itself, but the file was missing."
}

$installResult = if ($SkipInstall) {
    $resolvedInstall = Resolve-PalworldInstall -PalworldPath $PalworldPath
    [pscustomobject]@{
        PalworldRoot = $resolvedInstall.Root
        Win64Path = $resolvedInstall.Win64Path
        ModRoot = $resolvedInstall.ModRoot
        TargetPath = $resolvedInstall.InstalledModPath
        SourcePath = $null
        InstallMode = "Skipped"
        EnabledFile = Join-Path $resolvedInstall.InstalledModPath "enabled.txt"
        SamplePackInstalled = $false
        SamplePackPath = $null
    }
}
else {
    & $installScript -PalworldPath $PalworldPath
}

$healthPayload = Test-SidecarHealth -BaseUrl $normalizedBaseUrl
$sidecarStatus = "reused"
$warmupResult = [pscustomobject]@{
    Status = "skipped"
    Detail = "Warmup was skipped by launcher option."
    Succeeded = $true
}
if (-not $healthPayload) {
    $sidecarStatus = "started"
    Start-Process -FilePath "powershell" -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $startScript,
        "-BaseUrl",
        $normalizedBaseUrl
    ) -WorkingDirectory $repoRoot | Out-Null

    $healthPayload = Wait-ForSidecarHealth -BaseUrl $normalizedBaseUrl -TimeoutSeconds $BootTimeoutSeconds
    if (-not $healthPayload) {
        throw "PalLLM sidecar did not become healthy at $normalizedBaseUrl within $BootTimeoutSeconds seconds."
    }
}

if (-not $SkipDoctor) {
    $doctorArgs = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $doctorScript,
        "-PalworldPath",
        $installResult.PalworldRoot,
        "-BaseUrl",
        $normalizedBaseUrl,
        "-SkipSidecarBoot"
    )
    if ($RunSmoke) {
        $doctorArgs += "-RunSmoke"
    }

    & powershell @doctorArgs
    if ($LASTEXITCODE -ne 0) {
        throw "PalLLM doctor reported failures. Fix those issues before starting Palworld."
    }
}

if (-not $SkipWarmup) {
    $warmupResult = Invoke-PlayerWarmup `
        -BaseUrl $normalizedBaseUrl `
        -HealthPayload $healthPayload `
        -TimeoutSeconds $BootTimeoutSeconds `
        -RequireWarmup:$RequireWarmup
}

$latestHealthPayload = Test-SidecarHealth -BaseUrl $normalizedBaseUrl
if ($latestHealthPayload) {
    $healthPayload = $latestHealthPayload
}

$releaseReadinessPayload = Invoke-OptionalPalLlmGet -BaseUrl $normalizedBaseUrl -Path "/api/release/readiness"
$bridgeProofPayload = Invoke-OptionalPalLlmGet -BaseUrl $normalizedBaseUrl -Path "/api/bridge/proof"

if (-not $SkipDashboard) {
    Start-Process $normalizedBaseUrl | Out-Null
}

$launchedGame = $false
$gameExePath = $null
if (-not $SkipGameLaunch) {
    $gameExePath = Get-PalworldExecutablePath -PalworldRoot $installResult.PalworldRoot
    Start-Process -FilePath $gameExePath -WorkingDirectory (Split-Path -Parent $gameExePath) | Out-Null
    $launchedGame = $true
}

$launchArtifact = Write-PlayerLaunchArtifact `
    -InstallResult $installResult `
    -BaseUrl $normalizedBaseUrl `
    -SidecarStatus $sidecarStatus `
    -WarmupResult $warmupResult `
    -DoctorRan:(-not $SkipDoctor) `
    -SmokeRan:$RunSmoke.IsPresent `
    -DashboardOpened:(-not $SkipDashboard) `
    -GameLaunched:$launchedGame `
    -GameExePath $gameExePath `
    -HealthPayload $healthPayload `
    -ReleaseReadinessPayload $releaseReadinessPayload `
    -BridgeProofPayload $bridgeProofPayload

Write-Host ""
Write-Host "PalLLM player launch completed."
Write-Host ("- Mod target: " + $installResult.TargetPath)
Write-Host ("- Sidecar: " + $sidecarStatus + " at " + $normalizedBaseUrl)
if (-not $SkipDoctor) {
    Write-Host "- Doctor: clean"
}
if ($warmupResult) {
    Write-Host ("- Warmup: " + $warmupResult.Detail)
}
if (-not $SkipDashboard) {
    Write-Host ("- Dashboard: " + $normalizedBaseUrl)
}
Write-Host ("- Launch artifact: " + $launchArtifact.LatestJsonPath)
if ($launchedGame) {
    Write-Host ("- Game launched: " + $gameExePath)
}
else {
    Write-Host "- Game launch skipped"
}

[pscustomobject]@{
    PalworldRoot = $installResult.PalworldRoot
    ModTargetPath = $installResult.TargetPath
    SidecarStatus = $sidecarStatus
    BaseUrl = $normalizedBaseUrl
    WarmupStatus = $warmupResult.Status
    WarmupDetail = $warmupResult.Detail
    LaunchArtifactPath = $launchArtifact.LatestJsonPath
    LaunchArtifactMarkdownPath = $launchArtifact.LatestMarkdownPath
    GameExePath = $gameExePath
    GameLaunched = $launchedGame
    DashboardOpened = (-not $SkipDashboard)
    DoctorRan = (-not $SkipDoctor)
    SmokeRan = $RunSmoke.IsPresent
}
