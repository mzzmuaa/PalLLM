# =============================================================================
# PalLLM - Full-Audit Pipeline
# =============================================================================
#
# A single script that produces a reproducible "is this repo shippable?"
# report. Runs the same checks CI runs (build, tests, drift gates,
# public-copy scan, path-reference scan, dangling-link scan, mojibake
# scan) plus locally-available extras
# (coverage, SBOM), and emits a timestamped `artifacts/full-audit/<ts>/`
# directory with:
#
#   - RESULTS.md  - human-readable summary, one line per step + status
#   - steps/      - per-step stdout/stderr logs
#   - coverage/   - ReportGenerator HTML (when coverage runs)
#   - sbom/       - CycloneDX *.cdx.json (when SBOM runs)
#
# Why: CI failures force a PR to know which gate is red. Running the same
# pipeline locally before pushing catches regressions earlier and the
# timestamped artifact is a self-contained "here's where we stood on
# <date>" snapshot that's useful for audits and for tracking drift over
# time.
#
# Usage (from the repo root):
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_full_audit.ps1
#
# Flags:
#   -SkipCoverage   Don't collect code coverage (faster, no
#                   ReportGenerator tool install required).
#   -SkipSbom       Don't generate CycloneDX SBOMs.
#   -SkipTests      Don't run the NUnit suite (drift gates still run).
#   -SkipPackaging  Don't build and verify a candidate release zip.
#   -FailFast       Stop at the first failing step instead of running
#                   every step and reporting at the end.
#
# Exit code: 0 if every step passed, 1 otherwise. The exit code matches
# the RESULTS.md overall verdict so CI / git hooks / pre-push scripts can
# gate on it.
# =============================================================================

[CmdletBinding()]
param(
    [switch]$SkipCoverage,
    [switch]$SkipSbom,
    [switch]$SkipTests,
    [switch]$SkipPackaging,
    [switch]$FailFast
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

# Constants we need in regex patterns without fighting the PS parser.
$BT = [char]0x60  # backtick, used as literal char in ROADMAP/README regexes.

# Mojibake sentinel: the characteristic 3-byte sequence a UTF-8 em-dash
# (0xE2 0x80 0x94) becomes when accidentally reinterpreted as Windows-1252.
# Built byte-wise so the .ps1 file itself stays ASCII-clean.
$MojibakeSentinel = [Text.Encoding]::UTF8.GetString([byte[]](0xC3, 0xA2, 0xE2, 0x82, 0xAC))

# ---- Resolve repo root regardless of where the caller invoked us from ----
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# ---- Timestamped artifact directory (UTC for determinism across TZs) ----
$timestamp = (Get-Date).ToUniversalTime().ToString("yyyyMMdd-HHmmss")
$auditRoot = Join-Path $repoRoot "artifacts/full-audit/$timestamp"
$stepsDir  = Join-Path $auditRoot "steps"
New-Item -ItemType Directory -Force -Path $stepsDir | Out-Null

$resultsFile = Join-Path $auditRoot "RESULTS.md"
$steps = New-Object System.Collections.Generic.List[object]

function Record-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )

    $safeName = ($Name -replace '[^\w.-]', '_')
    $logPath = Join-Path $stepsDir ("{0:D2}_{1}.log" -f ($steps.Count + 1), $safeName)
    Write-Host ""
    Write-Host "=== [$Name] ===" -ForegroundColor Cyan
    $start = Get-Date
    $stepStatus = "PASS"
    $stepError  = ""
    try {
        $global:LASTEXITCODE = 0
        & $Body *>&1 | Tee-Object -FilePath $logPath
        if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) {
            throw "step exit code $LASTEXITCODE"
        }
    } catch {
        $stepStatus = "FAIL"
        $stepError = $_.Exception.Message
        Write-Host "[$Name] FAIL: $stepError" -ForegroundColor Red
        Add-Content -Path $logPath -Value "`nERROR: $stepError"
    }
    $duration = (Get-Date) - $start

    $null = $steps.Add([pscustomobject]@{
        Name     = $Name
        Status   = $stepStatus
        Seconds  = [math]::Round($duration.TotalSeconds, 1)
        Error    = $stepError
        LogPath  = $logPath
    })

    if ($FailFast -and $stepStatus -eq "FAIL") {
        Write-Host "-FailFast was set; stopping at first failure." -ForegroundColor Yellow
        Write-Report
        exit 1
    }
}

function Write-Report {
    $overall = if ($steps | Where-Object Status -eq "FAIL") { "FAIL" } else { "PASS" }

    $lines = New-Object System.Collections.Generic.List[string]
    $null = $lines.Add("# PalLLM Full-Audit Results")
    $null = $lines.Add("")
    $null = $lines.Add("- Generated: ``$timestamp`` UTC")
    $null = $lines.Add("- Repo root: ``$repoRoot``")
    $null = $lines.Add("- Overall: **$overall**")
    $null = $lines.Add("")
    $null = $lines.Add("| # | Step | Status | Seconds | Log |")
    $null = $lines.Add("| -: | :--- | :---: | --: | :--- |")
    $i = 0
    foreach ($s in $steps) {
        $i++
        $logRel = "steps/" + (Split-Path -Leaf $s.LogPath)
        $null = $lines.Add("| $i | $($s.Name) | **$($s.Status)** | $($s.Seconds) | [log]($logRel) |")
    }
    $null = $lines.Add("")

    $fails = @($steps | Where-Object Status -eq "FAIL")
    if ($fails.Count -gt 0) {
        $null = $lines.Add("## Failures")
        $null = $lines.Add("")
        foreach ($f in $fails) {
            $null = $lines.Add("### $($f.Name)")
            $null = $lines.Add("")
            $null = $lines.Add('```')
            $null = $lines.Add($f.Error)
            $null = $lines.Add('```')
            $null = $lines.Add("")
        }
    }

    $null = $lines.Add("## Environment")
    $null = $lines.Add("")
    $dotnetVer = try { (dotnet --version) } catch { 'not found' }
    $null = $lines.Add("- .NET SDK: ``$dotnetVer``")
    $null = $lines.Add("- PowerShell: ``$($PSVersionTable.PSVersion)``")
    $null = $lines.Add("- Host OS: ``$([System.Environment]::OSVersion.VersionString)``")
    $null = $lines.Add("")

    Set-Content -Path $resultsFile -Value ($lines -join "`n") -Encoding UTF8
    $fullAuditEvidence = Write-FullAuditEvidence -Overall $overall
    Write-Host ""
    Write-Host "==========================================================" -ForegroundColor Yellow
    $color = if ($overall -eq "PASS") { "Green" } else { "Red" }
    Write-Host "Audit complete. Overall: $overall" -ForegroundColor $color
    Write-Host "Report: $resultsFile" -ForegroundColor Yellow
    Write-Host "Latest durable audit artifact: $($fullAuditEvidence.LatestFullAuditArtifact)" -ForegroundColor Yellow
    Write-Host "==========================================================" -ForegroundColor Yellow
}

function Write-FullAuditEvidence {
    param(
        [Parameter(Mandatory)][string]$Overall
    )

    $runtimeRoot = Get-PalLlmRuntimeRoot
    $releaseEvidenceDir = Join-Path $runtimeRoot "ReleaseEvidence"
    $historyDir = Join-Path $releaseEvidenceDir "History"
    New-Item -ItemType Directory -Force -Path $historyDir | Out-Null

    $capturedAtUtc = [DateTimeOffset]::UtcNow
    $historyStamp = $capturedAtUtc.ToString("yyyyMMdd-HHmmss")
    $latestArtifactPath = Join-Path $releaseEvidenceDir "latest-full-audit.json"
    $historyArtifactPath = Join-Path $historyDir ("full-audit-{0}.json" -f $historyStamp)

    $stepNames = [System.Collections.Generic.List[string]]::new()
    $failedSteps = [System.Collections.Generic.List[string]]::new()
    $currentBlockers = [System.Collections.Generic.List[string]]::new()
    $readyEvidence = [System.Collections.Generic.List[string]]::new()

    foreach ($step in $steps) {
        Add-UniqueString -List $stepNames -Value $step.Name
        if ($step.Status -eq "FAIL") {
            Add-UniqueString -List $failedSteps -Value $step.Name
            Add-UniqueString -List $currentBlockers -Value ("{0}: {1}" -f $step.Name, $step.Error)
            continue
        }

        Add-UniqueString -List $readyEvidence -Value ("{0} passed." -f $step.Name)
    }

    if ($Overall -eq "PASS") {
        Add-UniqueString -List $readyEvidence -Value "All recorded full-audit steps passed."
    }
    else {
        Add-UniqueString -List $currentBlockers -Value "One or more full-audit gates failed. Inspect RESULTS.md and per-step logs before trusting this candidate."
    }

    $artifact = [ordered]@{
        Status = if ($Overall -eq "PASS") { "passed" } else { "failed" }
        Summary = if ($Overall -eq "PASS") {
            "PalLLM full audit passed and recorded the current build, test, drift, and packaging posture."
        }
        else {
            "PalLLM full audit recorded failing gates. Inspect RESULTS.md and the step logs before trusting this candidate."
        }
        CapturedAtUtc = $capturedAtUtc
        ArtifactPath = $latestArtifactPath
        HistoryArtifactPath = $historyArtifactPath
        AuditRoot = $auditRoot
        ResultsPath = $resultsFile
        StepsDirectoryPath = $stepsDir
        TestsEnabled = (-not $SkipTests)
        CoverageEnabled = (-not $SkipTests -and -not $SkipCoverage)
        SbomEnabled = (-not $SkipSbom)
        PackagingEnabled = (-not $SkipPackaging)
        TotalStepCount = $steps.Count
        PassedStepCount = @($steps | Where-Object Status -eq "PASS").Count
        FailedStepCount = @($steps | Where-Object Status -eq "FAIL").Count
        StepNames = @($stepNames)
        FailedSteps = @($failedSteps)
        CurrentBlockers = @($currentBlockers)
        ReadyEvidence = @($readyEvidence)
    }

    $json = ConvertTo-PalLlmJsonBody -InputObject $artifact
    Set-Content -LiteralPath $historyArtifactPath -Value $json -Encoding UTF8
    Set-Content -LiteralPath $latestArtifactPath -Value $json -Encoding UTF8

    return [pscustomobject]@{
        LatestFullAuditArtifact = $latestArtifactPath
        FullAuditHistoryArtifact = $historyArtifactPath
    }
}

# -----------------------------------------------------------------------------
# Step 1: Restore + Build
# -----------------------------------------------------------------------------
Record-Step -Name "Build_Release" -Body {
    dotnet restore PalLLM.sln
    dotnet build PalLLM.sln --configuration Release --no-restore --nologo --verbosity minimal
}

# -----------------------------------------------------------------------------
# Step 2: Tests (+optional coverage)
# -----------------------------------------------------------------------------
if (-not $SkipTests) {
    $testArgs = @(
        "test", "PalLLM.sln",
        "--configuration", "Release",
        "--no-build",
        "--nologo",
        "--verbosity", "minimal"
    )
    if (-not $SkipCoverage) {
        $coverageDir = Join-Path $auditRoot "TestResults"
        $testArgs += @(
            '--collect:"XPlat Code Coverage"',
            "--settings", "tests/PalLLM.Tests/coverlet.runsettings",
            "--results-directory", $coverageDir
        )
    }

    Record-Step -Name "Tests" -Body {
        & dotnet @testArgs
    }

    if (-not $SkipCoverage) {
        Record-Step -Name "Coverage_report" -Body {
            $coverageDir = Join-Path $auditRoot "TestResults"
            $reports = Get-ChildItem -Path $coverageDir -Filter "coverage.cobertura.xml" -Recurse -ErrorAction SilentlyContinue
            if (-not $reports) {
                throw "No coverage files produced; coverlet.collector may not be wired up."
            }
            $reportOut = Join-Path $auditRoot "coverage"
            New-Item -ItemType Directory -Force -Path $reportOut | Out-Null
            $tool = Get-Command reportgenerator -ErrorAction SilentlyContinue
            if (-not $tool) {
                dotnet tool install --global dotnet-reportgenerator-globaltool --version 5.5.5
            }
            reportgenerator `
                "-reports:$($reports.FullName -join ';')" `
                "-targetdir:$reportOut" `
                "-reporttypes:HtmlInline;TextSummary" `
                "-assemblyfilters:+PalLLM.Domain;+PalLLM.Sidecar"
            $summary = Join-Path $reportOut "Summary.txt"
            if (Test-Path $summary) { Get-Content $summary | Select-Object -First 25 }
        }
    }
}

# -----------------------------------------------------------------------------
# Release package verification
# -----------------------------------------------------------------------------
if (-not $SkipPackaging) {
    Record-Step -Name "Release_Package_verification" -Body {
        $packagingRoot = Join-Path $auditRoot "packaging"
        $packageVersion = "$timestamp-audit"
        powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/package-release.ps1" `
            -OutputRoot $packagingRoot `
            -Version $packageVersion
    }
}

# -----------------------------------------------------------------------------
# Drift gates
# -----------------------------------------------------------------------------
Record-Step -Name "Drift_Mojibake" -Body {
    # Windows PowerShell 5.1 defaults Get-Content to the ANSI code page,
    # which would itself convert UTF-8 em-dashes into fake mojibake. Read
    # explicit bytes and decode only where we need to so the detector is
    # actually checking the on-disk payload, not a decoder artifact.
    $files = @(
        Get-ChildItem -Recurse -Path "src","mod","docs" -Include *.cs,*.lua,*.md -ErrorAction SilentlyContinue
        Get-Item "README.md","CHANGELOG.md","CONTRIBUTING.md","SECURITY.md","NOTICE.md","THIRD_PARTY_NOTICES.md" -ErrorAction SilentlyContinue
    ) | Where-Object { $_ }
    $hits = @()
    # Pattern on disk: 0xC3 0xA2 (U+00E2 "a-circumflex" in UTF-8) followed by
    # 0xE2 0x82 0xAC (U+20AC "euro" in UTF-8). That byte sequence only occurs
    # when a UTF-8 em-dash got re-encoded through Windows-1252 and saved
    # back as UTF-8 — which is the regression this gate exists to catch.
    $needle = [byte[]](0xC3, 0xA2, 0xE2, 0x82, 0xAC)
    foreach ($f in $files) {
        $bytes = [System.IO.File]::ReadAllBytes($f.FullName)
        if ($bytes.Length -lt $needle.Length) { continue }
        $max = $bytes.Length - $needle.Length
        for ($i = 0; $i -le $max; $i++) {
            $match = $true
            for ($j = 0; $j -lt $needle.Length; $j++) {
                if ($bytes[$i + $j] -ne $needle[$j]) { $match = $false; break }
            }
            if ($match) { $hits += $f.FullName; break }
        }
    }
    if ($hits.Count -gt 0) {
        $hits | ForEach-Object { Write-Output $_ }
        throw "mojibake detected in $($hits.Count) file(s)"
    }
    Write-Output "No mojibake byte sequences found in source or docs surfaces."
}

Record-Step -Name "Drift_Api_route_count" -Body {
    $codeRoutes = (Select-String -Path "src/PalLLM.Sidecar/Program.cs" -Pattern 'api\.Map(Get|Post|Put|Delete|Patch)').Count
    $roadmapPattern = "$BT(\d+)$BT $BT/api$BT routes"
    $readmePattern  = "\*\*(\d+) $BT/api$BT routes\*\*"
    $architecturePattern = '/api routes \((\d+) total\)'
    $codeMapPattern = '\((\d+) /api/\* routes\)'
    $handoffPattern = "$BT(\d+)$BT $BT/api$BT routes"
    $roadmapRoutes = [int]([regex]::Match((Get-Content -Raw "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    $readmeRoutes  = [int]([regex]::Match((Get-Content -Raw "README.md"), $readmePattern).Groups[1].Value)
    $architectureRoutes = [int]([regex]::Match((Get-Content -Raw "docs/ARCHITECTURE.md"), $architecturePattern).Groups[1].Value)
    $codeMapRoutes = [int]([regex]::Match((Get-Content -Raw "docs/CODE_MAP.md"), $codeMapPattern).Groups[1].Value)
    $handoffRoutes = [int]([regex]::Match((Get-Content -Raw "docs/HANDOFF.md"), $handoffPattern).Groups[1].Value)
    Write-Output "code=$codeRoutes roadmap=$roadmapRoutes readme=$readmeRoutes architecture=$architectureRoutes codemap=$codeMapRoutes handoff=$handoffRoutes"
    if ($codeRoutes -ne $roadmapRoutes -or $codeRoutes -ne $readmeRoutes -or $codeRoutes -ne $architectureRoutes -or $codeRoutes -ne $codeMapRoutes -or $codeRoutes -ne $handoffRoutes) {
        throw "route count drift (code=$codeRoutes roadmap=$roadmapRoutes readme=$readmeRoutes architecture=$architectureRoutes codemap=$codeMapRoutes handoff=$handoffRoutes)"
    }
}

Record-Step -Name "Drift_Api_reference_surface" -Body {
    $programPath = "src/PalLLM.Sidecar/Program.cs"
    $codeApiRoutes = @(Select-String -Path $programPath -Pattern 'api\.Map(Get|Post|Put|Delete|Patch)').Count
    $codeOperationalRoutes = @(
        Select-String -Path $programPath -Pattern 'app\.MapGet\("/metrics"|app\.MapGet\("/"|app\.MapHealthChecks\(|app\.MapOpenApi'
    ).Count

    $codeProtocolRoutes = @(Select-String -Path $programPath -Pattern 'app\.MapMcp\("/mcp"\)').Count

    $apiText = Get-Content -Raw "docs/API.md"
    $roadmapText = Get-Content -Raw "docs/ROADMAP.md"

    $apiRouteMatch = [regex]::Match($apiText, 'Total `/api` routes: (\d+)')
    $apiOpsMatch = [regex]::Match($apiText, 'Operational routes outside `/api`: (\d+)')
    $apiProtocolMatch = [regex]::Match($apiText, 'Separate protocol route: (\d+)')
    $roadmapOpsMatch = [regex]::Match($roadmapText, "$BT(\d+)$BT operational routes outside $BT/api$BT")
    $roadmapProtocolMatch = [regex]::Match($roadmapText, "$BT(\d+)$BT separate protocol route")
    $handoffText = Get-Content -Raw "docs/HANDOFF.md"
    $handoffOpsMatch = [regex]::Match($handoffText, "$BT(\d+)$BT operational routes outside $BT/api$BT")
    $handoffProtocolMatch = [regex]::Match($handoffText, "$BT(\d+)$BT separate $BT/mcp$BT protocol route")

    if (-not $apiRouteMatch.Success -or -not $apiOpsMatch.Success -or -not $apiProtocolMatch.Success `
        -or -not $roadmapOpsMatch.Success -or -not $roadmapProtocolMatch.Success `
        -or -not $handoffOpsMatch.Success -or -not $handoffProtocolMatch.Success)
    {
        throw "could not extract API/ROADMAP/HANDOFF surface counts; check docs formatting"
    }

    $apiRoutes = [int]$apiRouteMatch.Groups[1].Value
    $apiOperationalRoutes = [int]$apiOpsMatch.Groups[1].Value
    $apiProtocolRoutes = [int]$apiProtocolMatch.Groups[1].Value
    $roadmapOperationalRoutes = [int]$roadmapOpsMatch.Groups[1].Value
    $roadmapProtocolRoutes = [int]$roadmapProtocolMatch.Groups[1].Value
    $handoffOperationalRoutes = [int]$handoffOpsMatch.Groups[1].Value
    $handoffProtocolRoutes = [int]$handoffProtocolMatch.Groups[1].Value

    Write-Output "code_api=$codeApiRoutes api_doc=$apiRoutes code_ops=$codeOperationalRoutes api_doc_ops=$apiOperationalRoutes roadmap_ops=$roadmapOperationalRoutes handoff_ops=$handoffOperationalRoutes code_protocol=$codeProtocolRoutes api_doc_protocol=$apiProtocolRoutes roadmap_protocol=$roadmapProtocolRoutes handoff_protocol=$handoffProtocolRoutes"

    if ($codeApiRoutes -ne $apiRoutes) {
        throw "API.md /api route drift (code=$codeApiRoutes api_doc=$apiRoutes)"
    }

    if ($codeOperationalRoutes -ne $apiOperationalRoutes -or $codeOperationalRoutes -ne $roadmapOperationalRoutes -or $codeOperationalRoutes -ne $handoffOperationalRoutes) {
        throw "operational route drift (code=$codeOperationalRoutes api_doc=$apiOperationalRoutes roadmap=$roadmapOperationalRoutes handoff=$handoffOperationalRoutes)"
    }

    if ($codeProtocolRoutes -ne $apiProtocolRoutes -or $codeProtocolRoutes -ne $roadmapProtocolRoutes -or $codeProtocolRoutes -ne $handoffProtocolRoutes) {
        throw "protocol route drift (code=$codeProtocolRoutes api_doc=$apiProtocolRoutes roadmap=$roadmapProtocolRoutes handoff=$handoffProtocolRoutes)"
    }

    foreach ($required in @('/openapi/v1.json', '/openapi/v1.yaml', '/api/release/readiness', 'SmokeEvidence', 'POST /mcp', 'application/health+json')) {
        if ($apiText -notmatch [regex]::Escape($required)) {
            throw "docs/API.md is missing required contract marker '$required'"
        }
    }
}

Record-Step -Name "Drift_OpenApi_snapshot" -Body {
    powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/export-openapi.ps1" -Verify -Configuration Release
}

Record-Step -Name "Drift_Feature_catalog_count" -Body {
    $codeFeatures = (Select-String -Path "src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs" -Pattern 'Id = "[a-z0-9-]+"').Count
    $roadmapPattern = "$BT(\d+)$BT feature-catalog entries"
    $readmePattern = '`(\d+)`\s*\r?\n> feature-catalog entries'
    $architecturePattern = 'contains `(\d+)` entries'
    $apiPattern = 'Feature catalog \(`(\d+)` entries'
    $codeMapPattern = 'All (\d+) entries'
    $handoffPattern = "$BT(\d+)$BT feature-catalog entries"
    $roadmapFeatures = [int]([regex]::Match((Get-Content -Raw "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    $readmeFeatures = [int]([regex]::Match((Get-Content -Raw "README.md"), $readmePattern).Groups[1].Value)
    $architectureFeatures = [int]([regex]::Match((Get-Content -Raw "docs/ARCHITECTURE.md"), $architecturePattern).Groups[1].Value)
    $apiFeatures = [int]([regex]::Match((Get-Content -Raw "docs/API.md"), $apiPattern).Groups[1].Value)
    $codeMapFeatures = [int]([regex]::Match((Get-Content -Raw "docs/CODE_MAP.md"), $codeMapPattern).Groups[1].Value)
    $handoffFeatures = [int]([regex]::Match((Get-Content -Raw "docs/HANDOFF.md"), $handoffPattern).Groups[1].Value)
    Write-Output "code=$codeFeatures roadmap=$roadmapFeatures readme=$readmeFeatures architecture=$architectureFeatures api=$apiFeatures codemap=$codeMapFeatures handoff=$handoffFeatures"
    if ($codeFeatures -ne $roadmapFeatures -or $codeFeatures -ne $readmeFeatures -or $codeFeatures -ne $architectureFeatures -or $codeFeatures -ne $apiFeatures -or $codeFeatures -ne $codeMapFeatures -or $codeFeatures -ne $handoffFeatures) {
        throw "feature count drift (code=$codeFeatures roadmap=$roadmapFeatures readme=$readmeFeatures architecture=$architectureFeatures api=$apiFeatures codemap=$codeMapFeatures handoff=$handoffFeatures)"
    }
}

Record-Step -Name "Drift_Feature_status_split" -Body {
    $featureText = Get-Content -Raw "src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs"
    $codeReady = ([regex]::Matches($featureText, 'Status = "ready"')).Count
    $codeScaffolded = ([regex]::Matches($featureText, 'Status = "scaffolded"')).Count
    $codeDeferred = ([regex]::Matches($featureText, 'Status = "deferred"')).Count

    $readmeText = Get-Content -Raw "README.md"
    $roadmapText = Get-Content -Raw "docs/ROADMAP.md"
    $architectureText = Get-Content -Raw "docs/ARCHITECTURE.md"
    $apiText = Get-Content -Raw "docs/API.md"
    $handoffText = Get-Content -Raw "docs/HANDOFF.md"

    $readmeMatch = [regex]::Match($readmeText, '`(\d+) ready / (\d+) scaffolded / (\d+) deferred`')
    $roadmapMatch = [regex]::Match($roadmapText, 'feature status split: `(\d+) ready`, `(\d+) scaffolded`, `(\d+) deferred`')
    $architectureMatch = [regex]::Match($architectureText, 'contains `\d+` entries \(`(\d+) ready`, `(\d+) scaffolded`, `(\d+) deferred`\)')
    $apiMatch = [regex]::Match($apiText, 'Feature catalog \(`\d+` entries: `(\d+) ready`, `(\d+) scaffolded`, `(\d+) deferred`\)')
    $handoffMatch = [regex]::Match($handoffText, 'feature split: `(\d+) ready`, `(\d+) scaffolded`, `(\d+) deferred`')

    if (-not $readmeMatch.Success -or -not $roadmapMatch.Success -or -not $architectureMatch.Success -or -not $apiMatch.Success -or -not $handoffMatch.Success) {
        throw "could not extract feature status split from docs; check formatting"
    }

    $readmeReady = [int]$readmeMatch.Groups[1].Value
    $readmeScaffolded = [int]$readmeMatch.Groups[2].Value
    $readmeDeferred = [int]$readmeMatch.Groups[3].Value
    $roadmapReady = [int]$roadmapMatch.Groups[1].Value
    $roadmapScaffolded = [int]$roadmapMatch.Groups[2].Value
    $roadmapDeferred = [int]$roadmapMatch.Groups[3].Value
    $architectureReady = [int]$architectureMatch.Groups[1].Value
    $architectureScaffolded = [int]$architectureMatch.Groups[2].Value
    $architectureDeferred = [int]$architectureMatch.Groups[3].Value
    $apiReady = [int]$apiMatch.Groups[1].Value
    $apiScaffolded = [int]$apiMatch.Groups[2].Value
    $apiDeferred = [int]$apiMatch.Groups[3].Value
    $handoffReady = [int]$handoffMatch.Groups[1].Value
    $handoffScaffolded = [int]$handoffMatch.Groups[2].Value
    $handoffDeferred = [int]$handoffMatch.Groups[3].Value

    Write-Output "code=$codeReady/$codeScaffolded/$codeDeferred readme=$readmeReady/$readmeScaffolded/$readmeDeferred roadmap=$roadmapReady/$roadmapScaffolded/$roadmapDeferred architecture=$architectureReady/$architectureScaffolded/$architectureDeferred api=$apiReady/$apiScaffolded/$apiDeferred handoff=$handoffReady/$handoffScaffolded/$handoffDeferred"

    if ($codeReady -ne $readmeReady -or $codeReady -ne $roadmapReady -or $codeReady -ne $architectureReady -or $codeReady -ne $apiReady `
        -or $codeReady -ne $handoffReady `
        -or $codeScaffolded -ne $readmeScaffolded -or $codeScaffolded -ne $roadmapScaffolded -or $codeScaffolded -ne $architectureScaffolded -or $codeScaffolded -ne $apiScaffolded `
        -or $codeScaffolded -ne $handoffScaffolded `
        -or $codeDeferred -ne $readmeDeferred -or $codeDeferred -ne $roadmapDeferred -or $codeDeferred -ne $architectureDeferred -or $codeDeferred -ne $apiDeferred `
        -or $codeDeferred -ne $handoffDeferred)
    {
        throw "feature status split drift detected"
    }
}

Record-Step -Name "Drift_Fallback_strategy_count" -Body {
    $codeStrategies = (Select-String -Path "src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs" -Pattern '^\s*private static FallbackBehaviorDecision (Try[A-Z][A-Za-z]+|CreateGeneralDirector)').Count
    $roadmapPattern = "$BT(\d+)$BT deterministic fallback strategies"
    $roadmapStrategies = [int]([regex]::Match((Get-Content -Raw "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    Write-Output "code=$codeStrategies roadmap=$roadmapStrategies"
    if ($codeStrategies -ne $roadmapStrategies) {
        throw "strategy count drift (code=$codeStrategies roadmap=$roadmapStrategies)"
    }
}

Record-Step -Name "Drift_Test_count_docs" -Body {
    $codeTests = (Get-ChildItem -Recurse -Path "tests/PalLLM.Tests" -Filter "*.cs" | Select-String -Pattern '^\s*\[(?:TestCase|Test)(?:\(|\])').Count
    $roadmapPattern = "$BT(\d+)$BT passing NUnit tests"
    $architecturePattern = '`(\d+)` tests passed on'
    $codeMapPattern = 'NUnit, (\d+) tests'
    $handoffPattern = "$BT(\d+)$BT passing tests"
    $roadmapTests = [int]([regex]::Match((Get-Content -Raw "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    $readmeTests  = [int]([regex]::Match((Get-Content -Raw "README.md"), 'Passed: (\d+)').Groups[1].Value)
    $architectureTests = [int]([regex]::Match((Get-Content -Raw "docs/ARCHITECTURE.md"), $architecturePattern).Groups[1].Value)
    $codeMapTests = [int]([regex]::Match((Get-Content -Raw "docs/CODE_MAP.md"), $codeMapPattern).Groups[1].Value)
    $handoffTests = [int]([regex]::Match((Get-Content -Raw "docs/HANDOFF.md"), $handoffPattern).Groups[1].Value)
    Write-Output "code=$codeTests roadmap=$roadmapTests readme=$readmeTests architecture=$architectureTests codemap=$codeMapTests handoff=$handoffTests"
    if ($roadmapTests -ne $readmeTests -or $codeTests -ne $roadmapTests -or $codeTests -ne $architectureTests -or $codeTests -ne $codeMapTests -or $codeTests -ne $handoffTests) {
        throw "test count drift (code=$codeTests roadmap=$roadmapTests readme=$readmeTests architecture=$architectureTests codemap=$codeMapTests handoff=$handoffTests)"
    }
}

Record-Step -Name "Drift_Public_copy" -Body {
    $reportPath = Join-Path $auditRoot "public-copy-audit.md"
    powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/audit_public_copy.ps1" `
        -RepoRoot $repoRoot `
        -WriteReportPath $reportPath
}

Record-Step -Name "Drift_Path_references" -Body {
    $reportPath = Join-Path $auditRoot "path-reference-audit.json"
    powershell -NoProfile -ExecutionPolicy Bypass -File "scripts/path_reference_audit.ps1" `
        -RepoRoot $repoRoot `
        -WriteReportPath $reportPath
}

Record-Step -Name "Drift_Agents_manifest" -Body {
    # Pass 110 - validate that agents.json conforms to its schema. The
    # full JSON Schema 2020-12 contract lives at docs/schemas/agents.schema.json
    # but Windows PowerShell 5.1 doesn't ship Test-Json - so we do the
    # required-keys + types check inline. Catches "I added a field but
    # forgot to update the schema" and "the manifest got corrupted".
    $manifestPath = Join-Path $repoRoot "agents.json"
    $schemaPath   = Join-Path $repoRoot "docs/schemas/agents.schema.json"

    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "agents.json not found at $manifestPath"
    }
    if (-not (Test-Path -LiteralPath $schemaPath)) {
        throw "agents.schema.json not found at $schemaPath"
    }

    # Both files must be valid JSON.
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "agents.json is not valid JSON: $($_.Exception.Message)"
    }
    try {
        $null = Get-Content -LiteralPath $schemaPath -Raw -Encoding UTF8 | ConvertFrom-Json -ErrorAction Stop
    } catch {
        throw "agents.schema.json is not valid JSON: $($_.Exception.Message)"
    }

    # Required top-level keys (mirror the schema's `required` list).
    $required = @("schemaVersion", "lastAudited", "project", "rollingState", "capabilities", "validationGates", "hardRules")
    foreach ($key in $required) {
        if (-not $manifest.PSObject.Properties[$key]) {
            throw "agents.json is missing required key: $key"
        }
    }

    # Type / shape checks.
    if ($manifest.schemaVersion -ne 1) {
        throw "agents.json schemaVersion must be 1; found $($manifest.schemaVersion)"
    }
    if ($manifest.lastAudited -notmatch '^\d{4}-\d{2}-\d{2}$') {
        throw "agents.json lastAudited must be yyyy-MM-dd; found '$($manifest.lastAudited)'"
    }
    if ($manifest.project -isnot [psobject] -or
        -not $manifest.project.PSObject.Properties['name'] -or
        -not $manifest.project.PSObject.Properties['kind'] -or
        -not $manifest.project.PSObject.Properties['license'] -or
        -not $manifest.project.PSObject.Properties['rootEntryPoints']) {
        throw "agents.json project block must have name, kind, license, rootEntryPoints"
    }
    $entryPoints = $manifest.project.rootEntryPoints
    if (-not ($entryPoints -is [System.Collections.IEnumerable]) -or @($entryPoints).Count -lt 1) {
        throw "agents.json project.rootEntryPoints must be a non-empty array"
    }
    foreach ($ep in $entryPoints) {
        foreach ($prop in @('kind', 'path', 'audience')) {
            if (-not $ep.PSObject.Properties[$prop]) {
                throw "agents.json rootEntryPoints entry missing required field '$prop'"
            }
        }
    }

    # Hard rules must be a non-empty string array.
    if (-not ($manifest.hardRules -is [System.Collections.IEnumerable]) -or @($manifest.hardRules).Count -lt 1) {
        throw "agents.json hardRules must be a non-empty array"
    }

    # Validation gates must reference fullAuditCommand.
    if (-not $manifest.validationGates.PSObject.Properties['fullAuditCommand']) {
        throw "agents.json validationGates must include fullAuditCommand"
    }

    Write-Output "agents.json conforms to required-fields contract; $(@($entryPoints).Count) root entry points; $(@($manifest.hardRules).Count) hard rules."
}

Record-Step -Name "Drift_Doc_freshness" -Body {
    # Pass 32 / E8 — ensure every doc's "Last audited" stamp is within
    # a rolling N-day window. Stale dates mean docs drift silently from
    # code — the rest of the audit pipeline can't catch "the docs say
    # 2026-04-01 but code has moved 3 weeks". This gate catches it.
    #
    # Threshold: 45 days. Generous enough that a mature doc doesn't
    # need a touch every week, strict enough that truly-stale content
    # surfaces before it misleads a reader.
    $maxAgeDays = 45
    $now = (Get-Date).ToUniversalTime().Date
    $threshold = $now.AddDays(-$maxAgeDays)
    $files = @()
    $files += Get-ChildItem -Path "." -Filter "*.md" -File
    $files += Get-ChildItem -Path "docs" -Filter "*.md" -File -ErrorAction SilentlyContinue
    $stalePattern = '^Last audited: `([0-9]{4}-[0-9]{2}-[0-9]{2})`'
    $checked = 0
    $stale = @()
    $missing = @()
    foreach ($f in $files) {
        # Skip auto-generated / artifact markdown.
        if ($f.FullName -like "*artifacts*") { continue }
        if ($f.FullName -like "*obj*") { continue }
        if ($f.FullName -like "*bin*") { continue }
        # Support-export, bug-report etc. issue templates don't carry a
        # Last audited stamp by design.
        if ($f.Name -eq "PULL_REQUEST_TEMPLATE.md") { continue }
        if ($f.Name -like "*_TEMPLATE.md") { continue }

        $content = Get-Content -Raw $f.FullName
        $match = [regex]::Match($content, $stalePattern, 'Multiline')
        if (-not $match.Success) {
            # Some docs intentionally don't carry the stamp (NOTICE,
            # SECURITY, LICENSE, CONTRIBUTING, CHANGELOG, top-level
            # README — which has its own "Last audit" field in a diff
            # format). We only gate docs that DO carry the canonical
            # stamp.
            continue
        }
        $checked++
        try {
            $date = [DateTime]::ParseExact($match.Groups[1].Value, 'yyyy-MM-dd', $null)
        } catch {
            $missing += "$($f.Name): unparsable date '$($match.Groups[1].Value)'"
            continue
        }
        if ($date -lt $threshold) {
            $age = ($now - $date).Days
            $stale += "$($f.Name): last audited $($match.Groups[1].Value) ($age days ago, >$maxAgeDays)"
        }
    }
    Write-Output "Checked $checked docs with 'Last audited' stamps; threshold=$maxAgeDays days."
    if ($missing.Count -gt 0) {
        foreach ($m in $missing) { Write-Output "  malformed: $m" }
    }
    if ($stale.Count -gt 0) {
        foreach ($s in $stale) { Write-Output "  stale: $s" }
        throw "$($stale.Count) doc(s) exceeded the $maxAgeDays-day freshness window. Update 'Last audited' stamps or set a new anchor."
    }
    Write-Output "All audited docs are fresh."
}

Record-Step -Name "Drift_Hot_file_line_count" -Body {
    # Pass 311 - pin hot-file line counts vs the approximate numbers
    # mirrored in cross-file prose references. Catches the Pass 310 root
    # drift: PalLlmRuntime.cs grew ~700 lines but 5 docs still said
    # 4028. Tolerance is 5% per file so small commits pass cleanly but
    # >5% drift (~250 lines on a 5000-line file) requires the docs to
    # update in lockstep.
    #
    # The gate scans an explicit list of doc files (not glob) so
    # CHANGELOG.md historical entries with old pinned numbers don't
    # generate false positives. Adding a new doc file that pins a hot
    # file's line count means adding it to $docFiles below.
    $tolerancePct = 5.0
    $hotFiles = @(
        @{ Path = "src/PalLLM.Domain/Runtime/PalLlmRuntime.cs"; Tag = "PalLlmRuntime\.cs" },
        @{ Path = "src/PalLLM.Sidecar/Program.cs"; Tag = "Program\.cs" },
        @{ Path = "src/PalLLM.Domain/Runtime/PresentationCuePlanner.cs"; Tag = "PresentationCuePlanner\.cs" }
    )
    $docFiles = @(
        "CLAUDE.md",
        "docs/CHEAT_SHEET.md",
        "docs/CODE_MAP.md",
        ".github/copilot-instructions.md",
        "docs/ANTI_PATTERNS.md",
        "docs/HARVEST.md"
    )

    $issues = @()
    $checked = 0
    foreach ($file in $hotFiles) {
        if (-not (Test-Path -LiteralPath $file.Path)) {
            $issues += "Hot file not found: $($file.Path)"
            continue
        }
        $actualLines = (Get-Content -LiteralPath $file.Path).Count
        $maxDrift = [int][Math]::Ceiling($actualLines * ($tolerancePct / 100.0))
        $minOk = $actualLines - $maxDrift
        $maxOk = $actualLines + $maxDrift

        foreach ($docFile in $docFiles) {
            if (-not (Test-Path -LiteralPath $docFile)) { continue }
            $content = Get-Content -LiteralPath $docFile -Raw

            # Match: filename, then up to 80 non-digit chars, then optional
            # ~, then a 3-5 digit number, then optional "-" or whitespace,
            # then "line" (case-insensitive). Non-digit gap ensures we don't
            # skip past one number to a later one for the same filename.
            $pattern = "$($file.Tag)[^\d]{0,80}~?(\d{3,5})\s*-?\s*line"
            $matches = [regex]::Matches($content, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            foreach ($m in $matches) {
                $checked++
                $claimed = [int]$m.Groups[1].Value
                if ($claimed -lt $minOk -or $claimed -gt $maxOk) {
                    $shortTag = ($file.Tag -replace '\\','')
                    $issues += "$docFile -> $shortTag claim=$claimed actual=$actualLines tol=${maxDrift} (${tolerancePct}%)"
                }
            }

            if ($docFile -eq "docs/CODE_MAP.md") {
                # CODE_MAP keeps the same information in a markdown table:
                # `| path/to/PalLlmRuntime.cs | `4729` | ... |`
                # It does not repeat the word "lines" per row, so the prose
                # pattern above cannot see it.
                $tablePattern = "\|\s*``?[^|]*$($file.Tag)``?\s*\|\s*``?~?(\d{3,5})``?\s*\|"
                $tableMatches = [regex]::Matches($content, $tablePattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
                foreach ($m in $tableMatches) {
                    $checked++
                    $claimed = [int]$m.Groups[1].Value
                    if ($claimed -lt $minOk -or $claimed -gt $maxOk) {
                        $shortTag = ($file.Tag -replace '\\','')
                        $issues += "$docFile -> $shortTag table-claim=$claimed actual=$actualLines tol=${maxDrift} (${tolerancePct}%)"
                    }
                }
            }
        }
    }
    Write-Output "checked $checked hot-file line-count claim(s) across $($docFiles.Count) doc files; issues=$($issues.Count); tolerance=${tolerancePct}%"
    if ($issues.Count -gt 0) {
        foreach ($issue in $issues) { Write-Output "  - $issue" }
        throw "Found $($issues.Count) hot-file line-count drift(s) outside the ${tolerancePct}% tolerance window"
    }
}

Record-Step -Name "Drift_Dangling_markdown_links" -Body {
    $pattern = '\]\(([^)]+\.(?:md|ps1|cs|lua|json|bat|sln|yml|txt))[^)]*\)'
    $files = @()
    $files += Get-ChildItem -Path "." -Filter "*.md" -File
    $files += Get-ChildItem -Path "docs" -Filter "*.md" -File -ErrorAction SilentlyContinue
    $links = @{}
    foreach ($f in $files) {
        foreach ($match in [regex]::Matches((Get-Content -Raw $f.FullName), $pattern)) {
            $target = ($match.Groups[1].Value -split '#')[0]
            if (-not $target) { continue }
            if ($target -match '^(https?|mailto):') { continue }
            $links[$target] = $true
        }
    }
    $missing = 0
    foreach ($link in $links.Keys) {
        $found = $false
        foreach ($base in @(".", "docs")) {
            if (Test-Path (Join-Path $base $link)) { $found = $true; break }
        }
        if (-not $found) {
            Write-Output "Dangling: $link"
            $missing++
        }
    }
    Write-Output "Checked $($links.Count) unique link targets; missing=$missing"
    if ($missing -gt 0) {
        throw "$missing dangling link target(s)"
    }
}

# -----------------------------------------------------------------------------
# Optional: CycloneDX SBOMs
# -----------------------------------------------------------------------------
if (-not $SkipSbom) {
    Record-Step -Name "SBOM_CycloneDX" -Body {
        $sbomDir = Join-Path $auditRoot "sbom"
        New-Item -ItemType Directory -Force -Path $sbomDir | Out-Null
        $tool = Get-Command "dotnet-CycloneDX" -ErrorAction SilentlyContinue
        if (-not $tool) {
            dotnet tool install --global CycloneDX --version 5.0.0 2>$null
        }
        dotnet CycloneDX src/PalLLM.Sidecar/PalLLM.Sidecar.csproj --output $sbomDir --filename PalLLM.Sidecar.cdx.json --json
        dotnet CycloneDX src/PalLLM.Domain/PalLLM.Domain.csproj  --output $sbomDir --filename PalLLM.Domain.cdx.json  --json
        Get-ChildItem -Path $sbomDir | ForEach-Object { Write-Output "SBOM: $($_.Name) ($($_.Length) bytes)" }
    }
}

# -----------------------------------------------------------------------------
# Render report + propagate exit code
# -----------------------------------------------------------------------------
Write-Report
$failCount = @($steps | Where-Object Status -eq "FAIL").Count
if ($failCount -gt 0) { exit 1 } else { exit 0 }
