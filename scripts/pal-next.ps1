<#
.SYNOPSIS
    Context-aware "what should I do right now?" advisor. The single
    intuitive lifeline a user can always reach for: type `pal next`,
    get one concrete next action with the exact command to run.

.DESCRIPTION
    PalLLM has many verbs, 60 docs, and a dozen optional surfaces. New
    users (and returning ones who forget what they configured last
    week) hit decision paralysis. `pal next` cuts that:

      1. Probes the current state in ~1 second:
         - sidecar reachable on configured BaseUrl?
         - inference wired in appsettings.json?
         - personality packs loaded?
         - latest audit pass / fail / never run?
         - any open `pal preflight` warnings?
         - hardware tier from /api/hardware (if reachable)?
         - live native proof state from /api/bridge/proof (if reachable)?
      2. Picks the highest-impact gap.
      3. Prints ONE recommended action with the exact command.

    Idempotent / read-only / no network calls beyond the local
    sidecar's /api/health, /api/hardware, and /api/bridge/proof. Pure local probing;
    runs in well under a second.

    The advisor's priority order is opinionated:

       1.  Repo not built / not tested      -> `pal onboard`
       2.  Sidecar offline                  -> `pal play`
       3.  Sidecar up, no inference         -> `pal connect ollama` (also llamacpp / vllm / omni / foundry / openvino / tensorrt / transformers)
       3b. Inference wired but unreachable  -> `pal doctor` (circuit open / probe failed)
       4.  Sidecar up, no packs loaded      -> `pal pack copy companion-warrior`
       5.  Latest audit FAIL                -> `pal fast-audit` to confirm
       6.  Preflight has warnings           -> `pal preflight` to triage
       7.  Native proof missing             -> `pal proof`
       8.  Everything healthy               -> `pal demo` (the "have fun" exit)

    Each step has a one-line rationale so the user knows WHY this
    is the suggested move, not just what to type.

.PARAMETER BaseUrl
    Sidecar URL. Default http://localhost:5088.

.PARAMETER Json
    Emit a structured record (verdict + action + reason) instead
    of pretty text. Useful for piping into the dashboard or a CI
    smoke check.

.EXAMPLE
    pwsh ./scripts/pal-next.ps1
    # Probe state. Print the single best next action.

.EXAMPLE
    pwsh ./scripts/pal-next.ps1 -Json | ConvertFrom-Json
    # Programmatic consumption.

.NOTES
    Verb shortcut:  pal next

    Distinct from `pal preflight` (which lists all 12 readiness
    checks with PASS/WARN/FAIL per row) and `pal welcome` (the
    interactive six-beat guided tour). `pal next` is the
    single-action recommender for users who already know the
    landscape and just want the magic "do this now" answer.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [switch]$Json
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot

# -----------------------------------------------------------------------------
# Probe current state
# -----------------------------------------------------------------------------

# Sidecar reachable? Capture the full health snapshot so we can spot
# downstream failures (circuit open, last inference probe failed) without a
# second round-trip.
$sidecarUp = $false
$healthSnapshot = $null
try {
    $healthSnapshot = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/health" -Method Get -TimeoutSec 2 -ErrorAction Stop
    $sidecarUp = $true
} catch {
    $sidecarUp = $false
}

# Inference live? Distinct from "wired in config" -- this asks the sidecar's
# health endpoint whether the configured inference backend is actually
# reachable. Catches the painful "config says enabled, Ollama isn't running"
# case where pal next would otherwise report READY.
$inferenceLive = $true
$inferenceCircuit = $null
if ($sidecarUp -and $healthSnapshot) {
    if ($healthSnapshot.PSObject.Properties['inferenceCircuitState']) {
        $inferenceCircuit = [string]$healthSnapshot.inferenceCircuitState
        if (-not [string]::IsNullOrWhiteSpace($inferenceCircuit) -and `
            $inferenceCircuit -ne 'Closed' -and `
            $inferenceCircuit -ne 'closed' -and `
            $inferenceCircuit -ne 'Disabled') {
            $inferenceLive = $false
        }
    }
    if ($healthSnapshot.PSObject.Properties['inferenceConfigured'] -and `
        $healthSnapshot.inferenceConfigured -eq $false) {
        # Sidecar reports inference is not configured even though config says yes,
        # OR the operator opted out and that's fine -- we use $inferenceWired
        # below to tell the difference.
        $inferenceLive = $false
    }
}

# Inference wired? (config-only check; doesn't require sidecar)
$inferenceWired = $false
$activeConfig = $null
$candidates = @(
    (Join-Path $repoRoot 'sidecar/publish/appsettings.json')
    (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
    (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM/appsettings.json')
)
foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) {
        try {
            $cfg = Get-Content -LiteralPath $c -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
            if ($cfg.PalLLM -and $cfg.PalLLM.Inference -and $cfg.PalLLM.Inference.Enabled) {
                $inferenceWired = $true
                $activeConfig = $c
                break
            }
            if ($null -eq $activeConfig) { $activeConfig = $c }
        } catch { }
    }
}

# Personality packs loaded?
# When the sidecar is up we ask it directly; when offline we fall back to a
# filesystem probe of the configured runtime pack dir so the advisor can still
# warn an operator with no packs even before they boot the sidecar.
$packCount = -1
$packSource = 'unknown'
if ($sidecarUp) {
    try {
        $packs = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/packs" -Method Get -TimeoutSec 2 -ErrorAction Stop
        if ($packs.PSObject.Properties['packs']) {
            $packCount = @($packs.packs).Count
        } elseif ($packs -is [System.Collections.IEnumerable] -and -not ($packs -is [string])) {
            $packCount = @($packs).Count
        } else {
            $packCount = 0
        }
        $packSource = 'sidecar'
    } catch {
        $packCount = -1
    }
}
if ($packCount -lt 0) {
    # Filesystem fallback. Resolves the runtime root from the live
    # appsettings.json (so a custom PalSavedRoot / RuntimeFolderName is
    # honored), then probes <root>/Packs for pack manifests. An operator
    # who never copied a sample over will see zero manifests and the
    # advisor can still make an informed NO PERSONALITY suggestion even
    # before the sidecar is up.
    $palToolingPath = Join-Path $repoRoot 'scripts/PalLLM.Tooling.ps1'
    if (Test-Path -LiteralPath $palToolingPath) {
        . $palToolingPath
        $packDir = Join-Path (Get-PalLlmRuntimeRoot) 'Packs'
    } else {
        # Fallback for callers running the script outside the repo (e.g.
        # from a release zip). Same path the runtime defaults to.
        $packDir = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM/Packs'
    }
    if (Test-Path -LiteralPath $packDir) {
        $manifests = Get-ChildItem -Path $packDir -Recurse -Filter 'pack.json' -ErrorAction SilentlyContinue
        $packCount = @($manifests).Count
        $packSource = 'disk'
    }
}

# Hardware tier?
$hardwareTier = $null
if ($sidecarUp) {
    try {
        $hw = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/hardware" -Method Get -TimeoutSec 2 -ErrorAction Stop
        $hardwareTier = $hw.detectedTier
    } catch { }
}

# Native delivery proof?
$nativeDeliveryProven = $false
$nativeProofStatus = 'missing'
if ($sidecarUp) {
    try {
        $proof = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/bridge/proof" -Method Get -TimeoutSec 2 -ErrorAction Stop
        $nativeProofStatus = [string]$proof.status
        $nativeDeliveryProven = [bool]$proof.liveDeliveryProven -or [string]::Equals($nativeProofStatus, 'delivery_proven', [System.StringComparison]::OrdinalIgnoreCase)
    } catch { }
}
if (-not $nativeDeliveryProven) {
    $runtimeRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM'
    $nativeProofArtifact = Join-Path $runtimeRoot 'ReleaseEvidence/latest-native-proof.json'
    if (Test-Path -LiteralPath $nativeProofArtifact) {
        try {
            $artifact = Get-Content -LiteralPath $nativeProofArtifact -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
            $nativeProofStatus = [string]$artifact.bridgeProofStatus
            $artifactStatus = [string]$artifact.status
            $nativeDeliveryProven = [bool]$artifact.liveDeliveryProven `
                -or [string]::Equals($nativeProofStatus, 'delivery_proven', [System.StringComparison]::OrdinalIgnoreCase) `
                -or [string]::Equals($artifactStatus, 'proven', [System.StringComparison]::OrdinalIgnoreCase)
        } catch { }
    }
}

# Latest audit
$latestAuditStatus = 'never-run'
$latestAuditPath = $null
$auditDir = Join-Path $repoRoot 'artifacts/full-audit'
if (Test-Path -LiteralPath $auditDir) {
    $latest = Get-ChildItem -Path $auditDir -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        $resultsFile = Join-Path $latest.FullName 'RESULTS.md'
        if (Test-Path -LiteralPath $resultsFile) {
            $resultsContent = Get-Content -LiteralPath $resultsFile -Raw -ErrorAction SilentlyContinue
            $latestAuditPath = $resultsFile
            if ($resultsContent -match '(?m)^- Overall:\s*\*\*PASS\*\*') {
                $latestAuditStatus = 'PASS'
            } elseif ($resultsContent -match '(?m)^- Overall:\s*\*\*FAIL\*\*') {
                $latestAuditStatus = 'FAIL'
            } else {
                $latestAuditStatus = 'unknown'
            }
        }
    }
}

# .NET SDK present?
$dotnetVersion = $null
try {
    $dotnetVersion = (& dotnet --version 2>$null).Trim()
} catch { }

# Build artifacts present? (heuristic: any *.dll under bin/Release)
$buildArtifactsPresent = $false
$binRoot = Join-Path $repoRoot 'src/PalLLM.Sidecar/bin/Release'
if (Test-Path -LiteralPath $binRoot) {
    $dlls = Get-ChildItem -Path $binRoot -Recurse -Filter '*.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($dlls) { $buildArtifactsPresent = $true }
}

# -----------------------------------------------------------------------------
# Decide the single best next action
# -----------------------------------------------------------------------------

$action = $null
$why = $null
$command = $null
$verdict = $null

# 1. SDK / build not present
if ([string]::IsNullOrWhiteSpace($dotnetVersion)) {
    $action = 'install-dotnet'
    $verdict = 'NOT READY'
    $why = "The .NET SDK isn't on PATH. Without it nothing else builds."
    $command = '(install .NET 10 SDK from https://dotnet.microsoft.com/download)'
}
elseif (-not $buildArtifactsPresent) {
    $action = 'onboard'
    $verdict = 'GETTING STARTED'
    $why = "Repo isn't built yet. `pal onboard` does the SDK check, build, test, audit, and opens the dashboard in one go."
    $command = 'pal onboard'
}
# 2. Sidecar offline -> boot it
elseif (-not $sidecarUp) {
    $action = 'play'
    $verdict = 'SIDECAR OFFLINE'
    $why = "The sidecar isn't reachable at $BaseUrl. `pal play` boots it in a window and opens the dashboard."
    $command = 'pal play'
}
# 3. Sidecar up but inference not wired -> connect a model
elseif (-not $inferenceWired) {
    $action = 'connect-ollama'
    $verdict = 'DETERMINISTIC ONLY'
    $why = 'Sidecar is up but inference is OFF in appsettings.json. The companion still answers via the deterministic-fallback layer; with a model wired, replies get richer.'
    if ($hardwareTier -eq 'Blackwell' -or $hardwareTier -eq 'Generous') {
        $command = 'pal connect vllm   # or: pal connect llamacpp / lmstudio / openvino / foundry / transformers'
    } else {
        $command = 'pal connect ollama   # easiest path; ''pal connect llamacpp'' for raw GGUF; ''pal connect lmstudio'' for desktop local models; ''pal connect openvino'' for Intel GPU/NPU; ''pal connect foundry'' for Windows ML; ''pal connect transformers'' for pinned HF serving; ''pal connect omni'' for multimodal'
    }
}
# 3b. Inference wired in config but the configured backend is unreachable.
# Without this branch, an operator with a dead Ollama / crashed vLLM gets the
# READY verdict because everything else is green. /api/health surfacing
# circuit-state lets us surface the real issue.
elseif (-not $inferenceLive) {
    $action = 'inference-unreachable'
    $verdict = 'INFERENCE UNREACHABLE'
    $why = "Inference is enabled in config but the sidecar reports the circuit as '$inferenceCircuit'. The configured backend at the BaseUrl in appsettings.json isn't responding. Boot it (or pick a different lane with 'pal connect') and the companion swaps back to live replies on the next probe."
    $command = "pal doctor   # diagnose the backend; or pick a lane with pal connect ollama / llamacpp / openvino / foundry"
}
# 4. No packs loaded -> get a voice. Four ready-made samples ship under
# samples\packs\; `pal pack copy <name>` is the dedicated one-step bridge
# into the runtime pack dir.
elseif ($packCount -eq 0) {
    $action = 'pack-copy-sample'
    $verdict = 'NO PERSONALITY'
    $why = "No personality packs loaded. Four ready-made samples ship under samples\packs\ (companion-healer / -scholar / -trickster / -warrior). 'pal pack copy <name>' drops one into your runtime pack dir; the sidecar picks it up on the next reload."
    $command = 'pal pack copy companion-warrior   # or: pal pack new   to scaffold your own'
}
# 5. Latest audit FAIL -> investigate
elseif ($latestAuditStatus -eq 'FAIL') {
    $action = 'fast-audit'
    $verdict = 'AUDIT FAILED'
    $why = "Latest drift audit FAILED at $latestAuditPath. Re-run to see which gate is open."
    $command = 'pal fast-audit'
}
# 6. Audit never run -> run it
elseif ($latestAuditStatus -eq 'never-run') {
    $action = 'fast-audit'
    $verdict = 'NEVER AUDITED'
    $why = "No drift audit has been run. `pal fast-audit` runs the 16 drift gates in ~10 s and produces a results report."
    $command = 'pal fast-audit'
}
# 7. Repo/operator posture is healthy, but live native proof is still missing.
elseif (-not $nativeDeliveryProven) {
    $action = 'proof'
    $verdict = 'NATIVE PROOF MISSING'
    $why = "The repo/operator posture is healthy, but the release blocker is still live Palworld `delivery_proven` evidence. `pal proof` names the current proof lanes and exact next command."
    $command = 'pal proof'
}
# 8. Everything healthy -> have fun
else {
    $action = 'demo'
    $verdict = 'READY'
    $why = "Sidecar up, inference wired ($($activeConfig | Split-Path -Leaf)), $packCount pack(s) loaded, latest audit PASS. You're set. `pal demo` is a 30-second tour, or `pal campfire` for a 5-minute moment with the companion."
    $command = 'pal demo   # or: pal campfire'
}

# -----------------------------------------------------------------------------
# Output
# -----------------------------------------------------------------------------

if ($Json.IsPresent) {
    [pscustomobject]@{
        Verdict          = $verdict
        Action           = $action
        Command          = $command
        Why              = $why
        State            = [pscustomobject]@{
            SidecarUp           = $sidecarUp
            InferenceWired      = $inferenceWired
            InferenceLive       = $inferenceLive
            InferenceCircuit    = $inferenceCircuit
            PackCount           = $packCount
            PackSource          = $packSource
            HardwareTier        = $hardwareTier
            NativeProofStatus   = $nativeProofStatus
            NativeDeliveryProven= $nativeDeliveryProven
            LatestAuditStatus   = $latestAuditStatus
            DotnetVersion       = $dotnetVersion
            BuildArtifacts      = $buildArtifactsPresent
            ActiveConfigPath    = $activeConfig
        }
    } | ConvertTo-Json -Depth 4
    return
}

$verdictColor = switch ($verdict) {
    'READY'                  { 'Green' }
    'NEVER AUDITED'          { 'Yellow' }
    'NO PERSONALITY'         { 'Yellow' }
    'NATIVE PROOF MISSING'   { 'Yellow' }
    'DETERMINISTIC ONLY'     { 'Yellow' }
    'INFERENCE UNREACHABLE'  { 'Red' }
    'AUDIT FAILED'           { 'Red' }
    'SIDECAR OFFLINE'        { 'Red' }
    'GETTING STARTED'        { 'Cyan' }
    'NOT READY'              { 'Red' }
    default                  { 'Gray' }
}

Write-Host ""
Write-Host "PalLLM next-step advisor" -ForegroundColor Cyan
Write-Host ("  Verdict : {0}" -f $verdict) -ForegroundColor $verdictColor
Write-Host ""
Write-Host "Why:" -ForegroundColor White
foreach ($line in ($why -split "\. ")) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $clean = $line.TrimEnd('.').Trim()
    Write-Host ("  " + $clean + ".")
}
Write-Host ""
Write-Host "Try this:" -ForegroundColor White
Write-Host ("  " + $command) -ForegroundColor Yellow
Write-Host ""
Write-Host "State summary:" -ForegroundColor DarkGray
Write-Host ("  sidecar reachable     : {0}" -f $sidecarUp) -ForegroundColor DarkGray
Write-Host ("  inference wired       : {0}" -f $inferenceWired) -ForegroundColor DarkGray
if ($sidecarUp -and $inferenceWired) {
    Write-Host ("  inference circuit     : {0} (live={1})" -f $inferenceCircuit, $inferenceLive) -ForegroundColor DarkGray
}
if ($packCount -ge 0) {
    Write-Host ("  packs loaded          : {0} (source={1})" -f $packCount, $packSource) -ForegroundColor DarkGray
}
if ($hardwareTier) {
    Write-Host ("  hardware tier         : {0}" -f $hardwareTier) -ForegroundColor DarkGray
}
Write-Host ("  native proof          : {0}" -f $nativeProofStatus) -ForegroundColor DarkGray
Write-Host ("  latest audit          : {0}" -f $latestAuditStatus) -ForegroundColor DarkGray
if ($dotnetVersion) {
    Write-Host ("  .NET SDK              : {0}" -f $dotnetVersion) -ForegroundColor DarkGray
}

# Surface live runtime suggestions from /api/health when the sidecar is up.
# These are the operator-actionable hints computed by HealthSuggestionBuilder
# (no-packs-loaded / inference-circuit-open / inference-only-failures /
# bridge-idle / bridge-failed-files-accumulating / bridge-inbox-backlog).
# They complement the verdict above with finer-grained signals the verdict
# itself doesn't surface.
if ($healthSnapshot -and ($healthSnapshot.PSObject.Properties['suggestions'] -or $healthSnapshot.PSObject.Properties['Suggestions'])) {
    $runtimeSuggestions = if ($healthSnapshot.PSObject.Properties['suggestions']) {
        @($healthSnapshot.suggestions)
    } else {
        @($healthSnapshot.Suggestions)
    }
    if ($runtimeSuggestions.Count -gt 0) {
        Write-Host ""
        Write-Host "Runtime suggestions (from /api/health):" -ForegroundColor White
        foreach ($suggestion in $runtimeSuggestions) {
            $sCode = if ($suggestion.PSObject.Properties['code']) { $suggestion.code } else { $suggestion.Code }
            $sMessage = if ($suggestion.PSObject.Properties['message']) { $suggestion.message } else { $suggestion.Message }
            $sCommand = if ($suggestion.PSObject.Properties['command']) { $suggestion.command } else { $suggestion.Command }
            $sSeverity = if ($suggestion.PSObject.Properties['severity']) { $suggestion.severity } else { $suggestion.Severity }
            # Severity-aware coloring matches the dashboard cards: red for
            # urgent (active failure breaking chat), yellow for warn (mildly
            # off, look at it within a session), cyan for info (common
            # operator state, not a problem). The builder is the source of
            # truth, so a new hint code automatically picks up the right
            # colour without an edit here.
            $headerColor = switch ($sSeverity) {
                'urgent' { 'Red' }
                'warn'   { 'Yellow' }
                'info'   { 'Cyan' }
                default  { 'Yellow' }
            }
            Write-Host ("  - [{0}] {1}" -f $sCode, $sMessage) -ForegroundColor $headerColor
            if ($sCommand) {
                Write-Host ("      try: {0}" -f $sCommand) -ForegroundColor Green
            }
        }
    }
}
Write-Host ""
Write-Host "Other lifelines:" -ForegroundColor DarkGray
Write-Host "  pal welcome   # six-beat guided tour"     -ForegroundColor DarkGray
Write-Host "  pal proof     # native proof lanes + next action" -ForegroundColor DarkGray
Write-Host "  pal preflight # full 12-check readiness list" -ForegroundColor DarkGray
Write-Host "  pal status    # one-line current state"    -ForegroundColor DarkGray
Write-Host "  pal list      # every verb"                -ForegroundColor DarkGray
Write-Host ""
