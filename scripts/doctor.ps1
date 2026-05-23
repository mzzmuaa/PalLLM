[CmdletBinding()]
param(
    [string]$PalworldPath,
    [string]$BaseUrl = "http://localhost:5088",
    [switch]$RunSmoke,
    [switch]$RunDeliveryReplay,
    [switch]$SkipSidecarBoot,
    [int]$BootTimeoutSeconds = 25
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

function New-DoctorCheck {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [ValidateSet("PASS", "WARN", "FAIL")]
        [string]$Status,

        [Parameter(Mandatory = $true)]
        [string]$Detail,

        [string]$Fix
    )

    return [pscustomobject]@{
        Name = $Name
        Status = $Status
        Detail = $Detail
        Fix = $Fix
    }
}

function Invoke-JsonGet {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Uri,

        [int]$TimeoutSeconds = 3
    )

    $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec $TimeoutSeconds
    return [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Raw = $response.Content
        Json = $response.Content | ConvertFrom-Json
    }
}

function Wait-ForHealth {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl,

        [Parameter(Mandatory = $true)]
        [int]$TimeoutSeconds
    )

    $deadline = (Get-Date).ToUniversalTime().AddSeconds([Math]::Max(1, $TimeoutSeconds))
    while ((Get-Date).ToUniversalTime() -lt $deadline) {
        try {
            return Invoke-JsonGet -Uri ($BaseUrl.TrimEnd("/") + "/api/health") -TimeoutSeconds 2
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    return $null
}

$checks = [System.Collections.Generic.List[object]]::new()
$normalizedBaseUrl = $BaseUrl.TrimEnd("/")
$runtimeInfo = Get-PalLlmExpectedRuntimeDirectories
$sidecarProject = Get-PalLlmSidecarProjectPath
$packagedSidecarLaunch = Resolve-PalLlmPackagedSidecarLaunchTarget
$sourceModPath = Get-PalLlmModSourcePath
$sidecarProcess = $null
$stdoutLog = $null
$stderrLog = $null
$healthPayload = $null
$releaseReadinessPayload = $null

try {
    if (Test-Path -LiteralPath $sourceModPath) {
        $checks.Add((New-DoctorCheck -Name "Source mod" -Status PASS -Detail "Found PalLLM mod source at $sourceModPath"))
    }
    else {
        $checks.Add((New-DoctorCheck -Name "Source mod" -Status FAIL -Detail "PalLLM mod source was not found at $sourceModPath" -Fix "Keep the repo or release package structure intact so mod\\ue4ss\\Mods\\PalLLM exists."))
    }

    $install = $null
    try {
        $install = Resolve-PalworldInstall -PalworldPath $PalworldPath
        $checks.Add((New-DoctorCheck -Name "Palworld install" -Status PASS -Detail "Resolved Palworld at $($install.Root)"))
        $checks.Add((New-DoctorCheck -Name "UE4SS mod root" -Status PASS -Detail "Using mod root $($install.ModRoot)"))

        if (Test-Path -LiteralPath $install.InstalledModPath) {
            $installedMain = Join-Path $install.InstalledModPath "Scripts\main.lua"
            if (Test-Path -LiteralPath $installedMain) {
                $checks.Add((New-DoctorCheck -Name "Installed mod" -Status PASS -Detail "Found installed PalLLM mod at $($install.InstalledModPath)"))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Installed mod" -Status FAIL -Detail "PalLLM is present but Scripts\\main.lua is missing at $($install.InstalledModPath)" -Fix "Reinstall with scripts\\install-mod.ps1."))
            }
        }
        else {
            $checks.Add((New-DoctorCheck -Name "Installed mod" -Status WARN -Detail "PalLLM is not installed under $($install.ModRoot)" -Fix "Run scripts\\install-mod.ps1 to copy the mod into the detected UE4SS mod root."))
        }
    }
    catch {
        $checks.Add((New-DoctorCheck -Name "Palworld install" -Status FAIL -Detail $_.Exception.Message -Fix "Pass -PalworldPath with the Palworld root or the Pal\\Binaries\\Win64 folder."))
    }

    try {
        $healthPayload = Invoke-JsonGet -Uri ($normalizedBaseUrl + "/api/health") -TimeoutSeconds 2
        $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status PASS -Detail "Connected to an already-running sidecar at $normalizedBaseUrl"))
    }
    catch {
        if ($SkipSidecarBoot) {
            $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status FAIL -Detail "Sidecar was unreachable at $normalizedBaseUrl and -SkipSidecarBoot was supplied." -Fix "Start the sidecar manually with dotnet run or remove -SkipSidecarBoot so doctor can attempt a local boot."))
        }
        elseif (-not $packagedSidecarLaunch -and -not (Test-CommandAvailable -CommandName "dotnet")) {
            $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status FAIL -Detail "Sidecar was unreachable at $normalizedBaseUrl and dotnet was not available for a local boot test." -Fix "Install the .NET SDK/runtime, package a self-contained sidecar, or start the sidecar separately before rerunning doctor."))
        }
        else {
            $stdoutLog = Join-Path ([IO.Path]::GetTempPath()) ("palllm-doctor-" + [guid]::NewGuid().ToString("N") + ".out.log")
            $stderrLog = Join-Path ([IO.Path]::GetTempPath()) ("palllm-doctor-" + [guid]::NewGuid().ToString("N") + ".err.log")

            if ($packagedSidecarLaunch) {
                if ($packagedSidecarLaunch.RequiresDotNet) {
                    if (-not (Test-CommandAvailable -CommandName "dotnet")) {
                        $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status FAIL -Detail "A packaged sidecar DLL was found, but dotnet was not available to launch it." -Fix "Install the .NET runtime or ship a self-contained sidecar publish."))
                    }
                    else {
                        $sidecarProcess = Start-Process -FilePath "dotnet" -ArgumentList @($packagedSidecarLaunch.FilePath, "--urls", $normalizedBaseUrl) -WorkingDirectory $packagedSidecarLaunch.WorkingDirectory -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
                    }
                }
                else {
                    $sidecarProcess = Start-Process -FilePath $packagedSidecarLaunch.FilePath -ArgumentList @("--urls", $normalizedBaseUrl) -WorkingDirectory $packagedSidecarLaunch.WorkingDirectory -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
                }

                if ($sidecarProcess) {
                    $healthPayload = Wait-ForHealth -BaseUrl $normalizedBaseUrl -TimeoutSeconds $BootTimeoutSeconds
                }

                if ($healthPayload) {
                    $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status PASS -Detail ("Packaged sidecar (" + $packagedSidecarLaunch.Kind + ") booted successfully at $normalizedBaseUrl for the doctor check.")))
                }
            }
            elseif (Test-Path -LiteralPath $sidecarProject) {
                $sidecarProcess = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", $sidecarProject, "--urls", $normalizedBaseUrl) -WorkingDirectory (Get-PalLlmRepoRoot) -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -PassThru
                $healthPayload = Wait-ForHealth -BaseUrl $normalizedBaseUrl -TimeoutSeconds $BootTimeoutSeconds

                if ($healthPayload) {
                    $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status PASS -Detail "Sidecar booted successfully at $normalizedBaseUrl for the doctor check."))
                }
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status FAIL -Detail "Sidecar was unreachable at $normalizedBaseUrl and neither a packaged sidecar nor the local sidecar project was available." -Fix "Run doctor from the PalLLM repo, create a release package with a published sidecar, or point doctor at an already-running sidecar."))
            }

            if (-not $healthPayload -and -not ($checks | Where-Object { $_.Name -eq "Sidecar health" -and $_.Status -eq "FAIL" })) {
                $stderrText = if ($stderrLog -and (Test-Path -LiteralPath $stderrLog)) { Get-Content -LiteralPath $stderrLog -Raw } else { "" }
                $detail = "Doctor could not boot the sidecar at $normalizedBaseUrl within $BootTimeoutSeconds seconds."
                if (-not [string]::IsNullOrWhiteSpace($stderrText)) {
                    $detail += " stderr: " + $stderrText.Trim()
                }

                $checks.Add((New-DoctorCheck -Name "Sidecar health" -Status FAIL -Detail $detail -Fix "Run dotnet run --project src\\PalLLM.Sidecar\\PalLLM.Sidecar.csproj manually and inspect startup errors."))
            }
        }
    }

    if ($healthPayload) {
        try {
            $releaseReadinessPayload = Invoke-JsonGet -Uri ($normalizedBaseUrl + "/api/release/readiness") -TimeoutSeconds 2
        }
        catch {
            $releaseReadinessPayload = $null
        }

        $reportedRuntimeRoot = $healthPayload.Json.RuntimeRoot
        if ([string]::IsNullOrWhiteSpace($reportedRuntimeRoot)) {
            $checks.Add((New-DoctorCheck -Name "Runtime root" -Status FAIL -Detail "Health payload did not include RuntimeRoot." -Fix "Check sidecar startup and option binding."))
        }
        else {
            $checks.Add((New-DoctorCheck -Name "Runtime root" -Status PASS -Detail "Sidecar reported runtime root $reportedRuntimeRoot"))
        }

        try {
            $dashboard = Invoke-WebRequest -Uri $normalizedBaseUrl -UseBasicParsing -TimeoutSec 3
            if ($dashboard.Content -like "*PalLLM Field Console*") {
                $checks.Add((New-DoctorCheck -Name "Dashboard" -Status PASS -Detail "Dashboard responded at $normalizedBaseUrl/"))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Dashboard" -Status FAIL -Detail "Dashboard responded, but the expected PalLLM Field Console marker was missing." -Fix "Check static file hosting under src\\PalLLM.Sidecar\\wwwroot."))
            }
        }
        catch {
                $checks.Add((New-DoctorCheck -Name "Dashboard" -Status FAIL -Detail ("Dashboard was unreachable at " + $normalizedBaseUrl + "/. " + $_.Exception.Message) -Fix "Confirm the sidecar is listening on the expected URL and static files are enabled."))
        }

        $inferenceWarmup = $healthPayload.Json.InferenceWarmup
        $inferenceConfigured = [bool]$healthPayload.Json.InferenceConfigured
        if ($null -eq $inferenceWarmup) {
            $checks.Add((New-DoctorCheck -Name "Inference warm lane" -Status WARN -Detail "Health payload did not include InferenceWarmup." -Fix "Update the sidecar to a build that exposes warmup status in GET /api/health."))    
        }
        else {
            $warmupEnabled = [bool]$inferenceWarmup.Enabled
            $warmupStatus = [string]$inferenceWarmup.Status
            if ([string]::IsNullOrWhiteSpace($warmupStatus)) {
                $warmupStatus = "unknown"
            }

            $warmupDetail = @(
                ("status=" + $warmupStatus),
                ($(if (-not [string]::IsNullOrWhiteSpace([string]$inferenceWarmup.ActiveModel)) { "active model=" + ([string]$inferenceWarmup.ActiveModel) } else { "active model unavailable" })),
                ($(if (-not [string]::IsNullOrWhiteSpace([string]$inferenceWarmup.WarmupTransport)) { "transport=" + ([string]$inferenceWarmup.WarmupTransport) } else { "transport unavailable" })),
                ($(if (-not [string]::IsNullOrWhiteSpace([string]$inferenceWarmup.ResidencyProvider)) { "residency=" + ([string]$inferenceWarmup.ResidencyProvider) } else { "residency unavailable" }))
            ) -join "; "

            if (-not $inferenceConfigured) {
                $checks.Add((New-DoctorCheck -Name "Inference warm lane" -Status PASS -Detail "Inference is disabled, so no warm lane is required; deterministic fallback remains primary."))
            }
            elseif (-not $warmupEnabled) {
                $checks.Add((New-DoctorCheck -Name "Inference warm lane" -Status WARN -Detail ($warmupDetail + "; warmup disabled by config") -Fix "Enable PalLLM:Inference:EnableWarmup or rerun play.bat after turning it on so the first live turn does not pay a cold load."))    
            }
            elseif ([string]::Equals($warmupStatus, "ready", [System.StringComparison]::OrdinalIgnoreCase)) {
                $checks.Add((New-DoctorCheck -Name "Inference warm lane" -Status PASS -Detail $warmupDetail))
            }
            else {
                $fix = "POST /api/inference/warmup or rerun play.bat so the active inference lane is primed before the first live turn."
                if ([string]::Equals($warmupStatus, "failed", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $fix = "Check the configured inference endpoint, then POST /api/inference/warmup again. Deterministic fallback still keeps PalLLM usable while inference is cold or unavailable."
                }

                $checks.Add((New-DoctorCheck -Name "Inference warm lane" -Status WARN -Detail $warmupDetail -Fix $fix))
            }
        }

        $nativeReadiness = $healthPayload.Json.NativeReadiness
        if ($null -eq $nativeReadiness) {
            $checks.Add((New-DoctorCheck -Name "Native readiness snapshot" -Status WARN -Detail "Health payload did not include NativeReadiness." -Fix "Update the sidecar to a build that exposes native readiness in GET /api/health."))    
        }
        else {
            $compatSummary = [string]$nativeReadiness.CompatSummary
            if ([string]::IsNullOrWhiteSpace($compatSummary)) {
                $compatSummary = "compat summary unavailable"
            }

            if ($nativeReadiness.BridgeBootSeen) {
                $checks.Add((New-DoctorCheck -Name "Bridge boot heartbeat" -Status PASS -Detail ("Observed bridge_boot from version " + ([string]$nativeReadiness.BridgeVersion) + " (" + $compatSummary + ")")))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Bridge boot heartbeat" -Status WARN -Detail "Sidecar is healthy, but no bridge_boot heartbeat has been observed yet." -Fix "Launch Palworld with the UE4SS bridge running and wait for the first bridge_boot event."))
            }

            $hudDetails = @(
                ($(if ($nativeReadiness.NativeHudEnabled) { "native HUD enabled" } else { "native HUD disabled" })),
                ($(if ($nativeReadiness.NativeHudTargetsConfigured) { "targets configured" } else { "no widget targets configured" })),
                ($(if ($nativeReadiness.HasUserWidgetCompat) { "UserWidget compat present" } else { "UserWidget compat missing" })),
                ($(if ($nativeReadiness.HasUiProbeCandidates) { "top ui_probe candidate: " + ([string]$nativeReadiness.TopUiProbeCandidate) } else { "no ui_probe candidates yet" })),
                ($(if (-not [string]::IsNullOrWhiteSpace([string]$nativeReadiness.NativeHudConfigSource)) { "config source: " + ([string]$nativeReadiness.NativeHudConfigSource) } else { "config source unavailable" })),
                ($(if (-not [string]::IsNullOrWhiteSpace([string]$nativeReadiness.NativeHudConfigPath)) { "config path: " + ([string]$nativeReadiness.NativeHudConfigPath) } else { "config path unavailable" }))
            ) -join "; "

            if ($nativeReadiness.HudBindReady) {
                $checks.Add((New-DoctorCheck -Name "Native HUD seam" -Status PASS -Detail $hudDetails))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Native HUD seam" -Status WARN -Detail $hudDetails -Fix "Confirm a real UserWidget via /api/bridge/ui-probe, then run scripts\\apply-hud-bind-recommendation.ps1 or update config\\native-hud.lua and restart Palworld."))
            }

            $hudRecommendation = $nativeReadiness.HudBindRecommendation
            if ($null -ne $hudRecommendation) {
                $recommendedTarget = [string]$hudRecommendation.RecommendedTarget
                $configuredTargets = @($nativeReadiness.ConfiguredHudTargets | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                $shortlist = @($hudRecommendation.Shortlist |
                    Select-Object -First 3 |
                    ForEach-Object {
                        if (-not [string]::IsNullOrWhiteSpace($_.FullName)) { $_.FullName }
                        elseif (-not [string]::IsNullOrWhiteSpace($_.DisplayName)) { $_.DisplayName }
                    } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                $recommendationDetail = @(
                    ("status=" + ([string]$hudRecommendation.Status)),
                    ($(if (-not [string]::IsNullOrWhiteSpace($recommendedTarget)) { "recommended=" + $recommendedTarget } else { "no recommended target yet" })),
                    ($(if ($configuredTargets.Count -gt 0) { "configured=" + ($configuredTargets -join ", ") } else { "configured targets unavailable" })),
                    ($(if ($shortlist.Count -gt 0) { "shortlist=" + ($shortlist -join " | ") } else { "no shortlist yet" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$nativeReadiness.NativeHudConfigPath)) { "config path=" + ([string]$nativeReadiness.NativeHudConfigPath) } else { "config path unavailable" }))
                ) -join "; "

                $recommendationFix = $null
                if ($hudRecommendation.SuggestedNextSteps -and $hudRecommendation.SuggestedNextSteps.Count -gt 0) {
                    $recommendationFix = [string]$hudRecommendation.SuggestedNextSteps[0]
                }
                if ([string]::IsNullOrWhiteSpace($recommendationFix)) {
                    $recommendationFix = "Run scripts\\apply-hud-bind-recommendation.ps1 and restart Palworld so bridge_boot reports the updated HUD target list."
                }

                if ([bool]$hudRecommendation.ConfiguredTargetMatchesRecommendation -or [bool]$nativeReadiness.HudBindReady) {
                    $checks.Add((New-DoctorCheck -Name "HUD target recommendation" -Status PASS -Detail $recommendationDetail))
                }
                else {
                    $checks.Add((New-DoctorCheck -Name "HUD target recommendation" -Status WARN -Detail $recommendationDetail -Fix $recommendationFix))
                }
            }

            $productionDetails = @(
                ($(if ($nativeReadiness.ProductionSamplerEnabled) { "sampler enabled" } else { "sampler disabled" })),
                ($(if ($nativeReadiness.HasPalBaseCampManagerCompat) { "PalBaseCampManager compat present" } else { "PalBaseCampManager compat missing" }))
            ) -join "; "

            if ($nativeReadiness.ProductionSamplerReady) {
                $checks.Add((New-DoctorCheck -Name "Production sampler" -Status PASS -Detail $productionDetails))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Production sampler" -Status WARN -Detail $productionDetails -Fix "Only enable production_sampler_enabled after confirming BaseCampManager hook names on the running Palworld build."))
            }

            $markerDetails = @(
                ($(if ($nativeReadiness.WaypointNativeMarkerEnabled) { "native marker enabled" } else { "native marker disabled" })),
                ($(if ($nativeReadiness.HasPalMapManagerCompat) { "PalMapManager compat present" } else { "PalMapManager compat missing" }))
            ) -join "; "

            if ($nativeReadiness.WaypointMarkerReady) {
                $checks.Add((New-DoctorCheck -Name "Waypoint native marker" -Status PASS -Detail $markerDetails))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Waypoint native marker" -Status WARN -Detail $markerDetails -Fix "Confirm PalMapManager compatibility on the running build before trusting native waypoint placement."))
            }

            if ($nativeReadiness.ActionExecutorEnabled) {
                $checks.Add((New-DoctorCheck -Name "Action executor gate" -Status PASS -Detail "action_executor_enabled is true on the bridge side."))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Action executor gate" -Status WARN -Detail "action_executor_enabled is false on the bridge side." -Fix "Flip the bridge-side action executor gate only after validating the allowlist on the running build."))
            }
        }

        $bridgeLoop = $healthPayload.Json.BridgeLoop
        if ($null -eq $bridgeLoop) {
            $checks.Add((New-DoctorCheck -Name "Reply delivery proof" -Status WARN -Detail "Health payload did not include BridgeLoop." -Fix "Update the sidecar to a build that exposes bridge loop proof in GET /api/health."))
        }
        else {
            $loopStatus = [string]$bridgeLoop.Status
            if ([string]::IsNullOrWhiteSpace($loopStatus)) {
                $loopStatus = "unknown"
            }

            $activeRequest = [string]$bridgeLoop.ActiveRequestId
            $loopDetail = @(
                ("status=" + $loopStatus),
                ($(if (-not [string]::IsNullOrWhiteSpace($activeRequest)) { "request=" + $activeRequest } else { "no active request" })),
                ($(if ($bridgeLoop.OutboxReplyWritten) { "outbox reply seen" } else { "no outbox reply yet" })),
                ($(if ($bridgeLoop.VisibleDeliveryConfirmed) { "delivery confirmed" } else { "delivery not yet confirmed" })),
                ($(if ($bridgeLoop.ActionPlanned) { "action planned" } else { "no action planned" })),
                ($(if ($bridgeLoop.ActionFeedbackObserved) { "feedback observed" } else { "no matched feedback yet" }))
            ) -join "; "

            if ([bool]$bridgeLoop.LoopClosed) {
                $checks.Add((New-DoctorCheck -Name "Reply delivery proof" -Status PASS -Detail $loopDetail))
            }
            else {
                $checks.Add((New-DoctorCheck -Name "Reply delivery proof" -Status WARN -Detail $loopDetail -Fix "Run scripts\\run-sidecar-smoke.ps1 or play one Palworld turn to drive a full request -> outbox -> render -> feedback loop."))    
            }
        }

        if ($releaseReadinessPayload -and $releaseReadinessPayload.Json) {
            $nativeProof = $releaseReadinessPayload.Json.NativeProofEvidence
            if ($null -eq $nativeProof) {
                $checks.Add((New-DoctorCheck -Name "Latest native proof artifact" -Status WARN -Detail "Release-readiness did not include NativeProofEvidence." -Fix "Update the sidecar to a build that exposes live native proof evidence in GET /api/release/readiness."))
            }
            else {
                $nativeProofStatus = [string]$nativeProof.Status
                if ([string]::IsNullOrWhiteSpace($nativeProofStatus)) {
                    $nativeProofStatus = "unknown"
                }

                $nativeProofDetail = @(
                    ("status=" + $nativeProofStatus),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$nativeProof.BridgeProofStatus)) { "bridge=" + ([string]$nativeProof.BridgeProofStatus) } else { "bridge status unavailable" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$nativeProof.DeliverySurface)) { "surface=" + ([string]$nativeProof.DeliverySurface) } else { "surface unavailable" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$nativeProof.NativeHudConfigSource)) { "config source=" + ([string]$nativeProof.NativeHudConfigSource) } else { "config source unavailable" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$nativeProof.ArtifactPath)) { "artifact=" + ([string]$nativeProof.ArtifactPath) } else { "artifact path unavailable" }))
                ) -join "; "

                if ([string]::Equals($nativeProofStatus, "proven", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $checks.Add((New-DoctorCheck -Name "Latest native proof artifact" -Status PASS -Detail $nativeProofDetail))
                }
                else {
                    $nativeProofFix = "Run scripts\\run-native-proof.ps1 while Palworld is live so the release surface records real native HUD delivery evidence."
                    if (-not [string]::IsNullOrWhiteSpace([string]$nativeProof.RecommendedNextStep)) {
                        $nativeProofFix = [string]$nativeProof.RecommendedNextStep
                    }

                    $checks.Add((New-DoctorCheck -Name "Latest native proof artifact" -Status WARN -Detail $nativeProofDetail -Fix $nativeProofFix))
                }
            }

            $proofBundle = $releaseReadinessPayload.Json.ProofBundleEvidence
            if ($null -eq $proofBundle) {
                $checks.Add((New-DoctorCheck -Name "Latest proof bundle artifact" -Status WARN -Detail "Release-readiness did not include ProofBundleEvidence." -Fix "Update the sidecar to a build that exposes release proof bundle evidence in GET /api/release/readiness."))
            }
            else {
                $proofBundleStatus = [string]$proofBundle.Status
                if ([string]::IsNullOrWhiteSpace($proofBundleStatus)) {
                    $proofBundleStatus = "unknown"
                }

                $proofBundleDetail = @(
                    ("status=" + $proofBundleStatus),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$proofBundle.BridgeProofStatus)) { "bridge=" + ([string]$proofBundle.BridgeProofStatus) } else { "bridge status unavailable" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$proofBundle.SmokeEvidenceStatus)) { "smoke=" + ([string]$proofBundle.SmokeEvidenceStatus) } else { "smoke status unavailable" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$proofBundle.NativeProofEvidenceStatus)) { "native=" + ([string]$proofBundle.NativeProofEvidenceStatus) } else { "native proof status unavailable" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$proofBundle.ArchivePath)) { "archive=" + ([string]$proofBundle.ArchivePath) } else { "archive path unavailable" }))
                ) -join "; "

                if ([string]::Equals($proofBundleStatus, "recorded", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $checks.Add((New-DoctorCheck -Name "Latest proof bundle artifact" -Status PASS -Detail $proofBundleDetail))
                }
                else {
                    $proofBundleFix = "Run scripts\\export-release-proof-bundle.ps1 so the current release/readiness snapshot, bridge proof, and latest smoke/native-proof artifacts are archived together."
                    $checks.Add((New-DoctorCheck -Name "Latest proof bundle artifact" -Status WARN -Detail $proofBundleDetail -Fix $proofBundleFix))
                }
            }

            $packageVerification = $releaseReadinessPayload.Json.PackageVerificationEvidence
            if ($null -eq $packageVerification) {
                $checks.Add((New-DoctorCheck -Name "Latest package verification artifact" -Status WARN -Detail "Release-readiness did not include PackageVerificationEvidence." -Fix "Update the sidecar to a build that exposes package verification evidence in GET /api/release/readiness."))
            }
            else {
                $packageVerificationStatus = [string]$packageVerification.Status
                if ([string]::IsNullOrWhiteSpace($packageVerificationStatus)) {
                    $packageVerificationStatus = "unknown"
                }

                $packageVerificationDetail = @(
                    ("status=" + $packageVerificationStatus),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$packageVerification.PackagePath)) { "package=" + ([string]$packageVerification.PackagePath) } else { "package path unavailable" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$packageVerification.PackageKind)) { "kind=" + ([string]$packageVerification.PackageKind) } else { "package kind unavailable" })),
                    ($(if ($packageVerification.IncludesSidecarPublish) { "includes sidecar publish" } else { "no packaged sidecar publish" })),
                    ($(if (-not [string]::IsNullOrWhiteSpace([string]$packageVerification.ArtifactPath)) { "artifact=" + ([string]$packageVerification.ArtifactPath) } else { "artifact path unavailable" }))
                ) -join "; "

                if ([string]::Equals($packageVerificationStatus, "verified", [System.StringComparison]::OrdinalIgnoreCase)) {
                    $checks.Add((New-DoctorCheck -Name "Latest package verification artifact" -Status PASS -Detail $packageVerificationDetail))
                }
                else {
                    $packageVerificationFix = "Run scripts\\package-release.ps1 or scripts\\verify-release-package.ps1 against the current candidate package so release-readiness records a verified package artifact."
                    $checks.Add((New-DoctorCheck -Name "Latest package verification artifact" -Status WARN -Detail $packageVerificationDetail -Fix $packageVerificationFix))
                }
            }
        }
    }

    foreach ($directoryCheck in @(
        @{ Name = "Runtime directory"; Path = $runtimeInfo.RuntimeRoot },
        @{ Name = "Bridge inbox"; Path = $runtimeInfo.Inbox },
        @{ Name = "Bridge outbox"; Path = $runtimeInfo.Outbox },
        @{ Name = "Bridge archive"; Path = $runtimeInfo.Archive },
        @{ Name = "Bridge failed"; Path = $runtimeInfo.Failed },
        @{ Name = "Bridge screenshots"; Path = $runtimeInfo.Screenshots }
    )) {
        if (Test-Path -LiteralPath $directoryCheck.Path) {
            $checks.Add((New-DoctorCheck -Name $directoryCheck.Name -Status PASS -Detail ("Found " + $directoryCheck.Path)))
        }
        else {
            $checks.Add((New-DoctorCheck -Name $directoryCheck.Name -Status WARN -Detail ("Missing " + $directoryCheck.Path) -Fix "Boot the sidecar once so it can create the runtime folders."))
        }
    }

    if ($RunSmoke) {
        $smokeScript = Join-Path $PSScriptRoot "run-sidecar-smoke.ps1"
        if (-not (Test-Path -LiteralPath $smokeScript)) {
            $checks.Add((New-DoctorCheck -Name "Smoke loop" -Status FAIL -Detail "run-sidecar-smoke.ps1 was not found next to doctor.ps1" -Fix "Keep the scripts folder intact or restore the smoke helper."))
        }
        elseif (-not $healthPayload) {
            $checks.Add((New-DoctorCheck -Name "Smoke loop" -Status FAIL -Detail "Smoke loop was requested but the sidecar never became healthy." -Fix "Resolve the sidecar health failure first, then rerun doctor with -RunSmoke."))
        }
        else {
            try {
                & $smokeScript -BaseUrl $normalizedBaseUrl | Out-Null
                $checks.Add((New-DoctorCheck -Name "Smoke loop" -Status PASS -Detail "Bridge -> chat -> outbox smoke loop succeeded against $normalizedBaseUrl"))
            }
            catch {
                $checks.Add((New-DoctorCheck -Name "Smoke loop" -Status FAIL -Detail $_.Exception.Message -Fix "Inspect the sidecar logs and Bridge\\Outbox state, then rerun scripts\\run-sidecar-smoke.ps1 directly for more detail."))
            }
        }
    }

    if ($RunDeliveryReplay) {
        $replayScript = Join-Path $PSScriptRoot "run-delivery-replay.ps1"
        if (-not (Test-Path -LiteralPath $replayScript)) {
            $checks.Add((New-DoctorCheck -Name "Delivery replay" -Status FAIL -Detail "run-delivery-replay.ps1 was not found next to doctor.ps1" -Fix "Keep the scripts folder intact or restore the delivery replay helper."))
        }
        elseif (-not $healthPayload) {
            $checks.Add((New-DoctorCheck -Name "Delivery replay" -Status FAIL -Detail "Delivery replay was requested but the sidecar never became healthy." -Fix "Resolve the sidecar health failure first, then rerun doctor with -RunDeliveryReplay."))
        }
        else {
            try {
                & $replayScript -BaseUrl $normalizedBaseUrl | Out-Null
                $checks.Add((New-DoctorCheck -Name "Delivery replay" -Status PASS -Detail "Five representative delivery scenarios produced valid chat_reply envelopes against $normalizedBaseUrl"))
            }
            catch {
                $checks.Add((New-DoctorCheck -Name "Delivery replay" -Status FAIL -Detail $_.Exception.Message -Fix "Rerun scripts\\run-delivery-replay.ps1 directly and inspect the matching Bridge\\Outbox or Bridge\\Archive envelope."))    
            }
        }
    }
}
finally {
    if ($sidecarProcess -and -not $sidecarProcess.HasExited) {
        Stop-Process -Id $sidecarProcess.Id -Force -ErrorAction SilentlyContinue
    }

    foreach ($logFile in @($stdoutLog, $stderrLog)) {
        if ($logFile -and (Test-Path -LiteralPath $logFile)) {
            Remove-Item -LiteralPath $logFile -Force -ErrorAction SilentlyContinue
        }
    }
}

$summary = $checks | Sort-Object Name
$summary | Format-Table -AutoSize

$failures = @($summary | Where-Object { $_.Status -eq "FAIL" })
$warnings = @($summary | Where-Object { $_.Status -eq "WARN" })

# Surface live runtime suggestions from /api/health when the sidecar is up.
# These are the operator-actionable hints computed by HealthSuggestionBuilder
# (no-packs-loaded / inference-circuit-open / inference-only-failures /
# bridge-idle / bridge-failed-files-accumulating / bridge-inbox-backlog /
# vision-only-failures / tts-only-failures / outbox-backlog /
# automation-allowlist-empty-but-enabled / screenshots-pending-but-vision-disabled).
# They complement doctor's static checks with finer-grained signals --
# e.g. doctor confirms the sidecar is reachable + the bridge config is sane,
# the suggestions tell you the bridge has zero events because UE4SS isn't
# attached yet. Advisory by default; urgent severity escalates to a
# non-zero exit at the bottom of this script for CI parity.
$healthSuggestions = $null
if ($healthPayload -and $healthPayload.PSObject.Properties['Json'] -and $healthPayload.Json) {
    $healthJson = $healthPayload.Json
    if ($healthJson.PSObject.Properties['Suggestions']) {
        $healthSuggestions = @($healthJson.Suggestions)
    } elseif ($healthJson.PSObject.Properties['suggestions']) {
        $healthSuggestions = @($healthJson.suggestions)
    }
}
if ($healthSuggestions -and $healthSuggestions.Count -gt 0) {
    Write-Host ""
    Write-Host "Runtime suggestions (from /api/health):" -ForegroundColor White
    foreach ($suggestion in $healthSuggestions) {
        $suggestionCode = if ($suggestion.PSObject.Properties['code']) { $suggestion.code } else { $suggestion.Code }
        $suggestionMessage = if ($suggestion.PSObject.Properties['message']) { $suggestion.message } else { $suggestion.Message }
        $suggestionCommand = if ($suggestion.PSObject.Properties['command']) { $suggestion.command } else { $suggestion.Command }
        $suggestionSeverity = if ($suggestion.PSObject.Properties['severity']) { $suggestion.severity } else { $suggestion.Severity }
        # Severity-aware coloring matches the dashboard + pal next: red
        # for urgent (active failure breaking chat), yellow for warn
        # (mildly off, look at it within a session), cyan for info
        # (common operator state). Builder is the source of truth so
        # new hint codes pick up the right colour without an edit here.
        $entryColor = switch ($suggestionSeverity) {
            'urgent' { 'Red' }
            'warn'   { 'Yellow' }
            'info'   { 'Cyan' }
            default  { 'Yellow' }
        }
        Write-Host ("- [{0}] {1}" -f $suggestionCode, $suggestionMessage) -ForegroundColor $entryColor
        if (-not [string]::IsNullOrWhiteSpace($suggestionCommand)) {
            Write-Host ("  Try: {0}" -f $suggestionCommand) -ForegroundColor Green
        }
    }
}

Write-Host ""
Write-Host ("PalLLM doctor summary: {0} pass, {1} warn, {2} fail" -f (@($summary | Where-Object { $_.Status -eq "PASS" }).Count), $warnings.Count, $failures.Count)

if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "Warnings:"
    foreach ($warning in $warnings) {
        Write-Host ("- {0}: {1}" -f $warning.Name, $warning.Detail)
        if (-not [string]::IsNullOrWhiteSpace($warning.Fix)) {
            Write-Host ("  Fix: {0}" -f $warning.Fix)
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "Failures:"
    foreach ($failure in $failures) {
        Write-Host ("- {0}: {1}" -f $failure.Name, $failure.Detail)
        if (-not [string]::IsNullOrWhiteSpace($failure.Fix)) {
            Write-Host ("  Fix: {0}" -f $failure.Fix)
        }
    }

    exit 1
}

# Treat any urgent runtime suggestion as a non-zero exit so CI / VSCode
# tasks / pre-commit hooks that run `pal doctor` catch active failures
# (inference circuit open, every-request-failing, etc.) even when the
# static checks above all pass. Warn / info severity stays advisory --
# the operator can run `pal doctor` interactively without it failing for
# common in-progress states (no packs loaded yet, bridge enabled but
# never booted).
if ($healthSuggestions -and $healthSuggestions.Count -gt 0) {
    $urgentCount = @($healthSuggestions | Where-Object {
        $sev = if ($_.PSObject.Properties['severity']) { $_.severity } else { $_.Severity }
        $sev -ieq 'urgent'
    }).Count
    if ($urgentCount -gt 0) {
        Write-Host ""
        Write-Host ("Doctor sees {0} urgent runtime suggestion(s). Treating as failure for CI parity." -f $urgentCount) -ForegroundColor Red
        Write-Host "  Run 'pal next' for the prioritized one-action recommendation." -ForegroundColor DarkGray
        exit 1
    }
}

# Clean run: surface the optional next steps so operators know what to enable next
# without having to chase through multiple docs. These are intentionally advisory
# only, not failures, because a player who just wants a working baseline should
# not be nagged into flipping on every optional subsystem.
if ($failures.Count -eq 0 -and $warnings.Count -eq 0) {
    Write-Host ""
    Write-Host "All checks passed. Optional next steps:"
    Write-Host "- Enable live inference: set PalLLM:Inference:Enabled=true and point BaseUrl/Model at your chat-completions server."
    Write-Host "- Enable vision: set PalLLM:Vision:Enabled=true and point at a multimodal chat-completions server."
    Write-Host "- Enable TTS: set PalLLM:Tts:Enabled=true and run a speech server that returns audio bytes. Use Tts:RequestFormat=simple for POST { text, voice }, or openai_speech for /v1/audio/speech."
    Write-Host "- Enable guarded automation: set PalLLM:Automation:Enabled=true and add an AllowedActions entry."
    Write-Host "- Target a native HUD: read GET /api/bridge/ui-probe and populate native_hud_widget_targets in main.lua."
    Write-Host "- Re-run doctor with -RunSmoke -RunDeliveryReplay for the stronger end-to-end check."
}
