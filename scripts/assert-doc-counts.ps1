# =============================================================================
# PalLLM - Canonical doc-count drift checks (single source of truth)
# =============================================================================
#
# Pass 424: This script is the ONE implementation of the "do the counts in
# the docs still match the code?" drift checks. Both consumers call it:
#
#   - scripts/run_full_audit.ps1 dot-sources this file and calls the
#     individual Assert-* functions inside its Record-Step wrappers (so the
#     per-gate RESULTS.md granularity is preserved).
#   - .github/workflows/ci.yml runs this file directly (pwsh) in the
#     `doc drift audit` job.
#
# WHY THIS EXISTS: before Pass 424, CI re-implemented these checks in bash,
# parallel to the PowerShell audit. The two drifted SIX times (Passes 369,
# 370, 371, 372, 421, 423) because every structural change (route
# extraction, test additions) had to be mirrored in two places with two
# different shell dialects. Worse, the bash version only checked a SUBSET
# of the mirror docs the PowerShell version checked, so a stale count in
# ARCHITECTURE.md or CODE_MAP.md could pass CI but fail the local audit.
# Collapsing to one PowerShell implementation, run identically on Windows
# (local) and Linux (CI), eliminates that entire class of bug.
#
# Each Assert-* function:
#   - counts the live code surface (routes / features / strategies / tests),
#   - extracts the matching number from every mirror doc,
#   - writes a one-line "code=.. roadmap=.. readme=.." summary,
#   - throws with a descriptive message on any mismatch.
#
# Usage:
#   Direct (CI):        pwsh ./scripts/assert-doc-counts.ps1
#   Dot-source (audit): . "$PSScriptRoot/assert-doc-counts.ps1" -DefineOnly
#                       then call Assert-ApiRouteCount, etc.
#
# Exit code (direct run): 0 if every check passed, 1 on the first failure.
# =============================================================================

[CmdletBinding()]
param(
    # When set, only define the functions and the helpers; do not run the
    # checks. run_full_audit.ps1 dot-sources with this switch so it can call
    # the functions one at a time inside its own Record-Step wrappers.
    [switch]$DefineOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Backtick as a literal char — avoids escaping headaches inside the
# ROADMAP/README/HANDOFF regexes that match Markdown `code spans`.
$script:BT = [char]0x60

# Resolve the repo root (this script lives in <repo>/scripts/) so the
# relative doc/source paths below resolve regardless of caller CWD.
$script:RepoRoot = Split-Path -Parent $PSScriptRoot

function Get-SidecarRouteSourcePaths {
    # Program.cs plus every RouteRegistrations/*.cs companion (Codex's
    # Pass 387-402 extraction moved the /api routes out of Program.cs).
    $paths = New-Object System.Collections.Generic.List[string]
    $paths.Add((Join-Path $script:RepoRoot "src/PalLLM.Sidecar/Program.cs"))

    $routeRegistrationDir = Join-Path $script:RepoRoot "src/PalLLM.Sidecar/RouteRegistrations"
    if (Test-Path $routeRegistrationDir) {
        Get-ChildItem -Path $routeRegistrationDir -Filter "*.cs" -File |
            Sort-Object FullName |
            ForEach-Object { $paths.Add($_.FullName) }
    }

    return $paths.ToArray()
}

function Get-RepoFile {
    param([Parameter(Mandatory)][string]$Relative)
    return (Get-Content -Raw (Join-Path $script:RepoRoot $Relative))
}

function Assert-ApiRouteCount {
    $routeSourcePaths = Get-SidecarRouteSourcePaths
    $codeRoutes = (Select-String -Path $routeSourcePaths -Pattern 'api\.Map(Get|Post|Put|Delete|Patch)').Count
    $roadmapPattern = "$script:BT(\d+)$script:BT $script:BT/api$script:BT routes"
    $readmePattern  = "\*\*(\d+) $script:BT/api$script:BT routes\*\*"
    $architecturePattern = '/api routes \((\d+) total\)'
    $codeMapPattern = '\((\d+) /api/\* routes\)'
    $handoffPattern = "$script:BT(\d+)$script:BT $script:BT/api$script:BT routes"
    $roadmapRoutes = [int]([regex]::Match((Get-RepoFile "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    $readmeRoutes  = [int]([regex]::Match((Get-RepoFile "README.md"), $readmePattern).Groups[1].Value)
    $architectureRoutes = [int]([regex]::Match((Get-RepoFile "docs/ARCHITECTURE.md"), $architecturePattern).Groups[1].Value)
    $codeMapRoutes = [int]([regex]::Match((Get-RepoFile "docs/CODE_MAP.md"), $codeMapPattern).Groups[1].Value)
    $handoffRoutes = [int]([regex]::Match((Get-RepoFile "docs/HANDOFF.md"), $handoffPattern).Groups[1].Value)
    Write-Output "code=$codeRoutes roadmap=$roadmapRoutes readme=$readmeRoutes architecture=$architectureRoutes codemap=$codeMapRoutes handoff=$handoffRoutes"
    if ($codeRoutes -ne $roadmapRoutes -or $codeRoutes -ne $readmeRoutes -or $codeRoutes -ne $architectureRoutes -or $codeRoutes -ne $codeMapRoutes -or $codeRoutes -ne $handoffRoutes) {
        throw "route count drift (code=$codeRoutes roadmap=$roadmapRoutes readme=$readmeRoutes architecture=$architectureRoutes codemap=$codeMapRoutes handoff=$handoffRoutes)"
    }
}

function Assert-ApiReferenceSurface {
    $routeSourcePaths = Get-SidecarRouteSourcePaths
    $programPath = Join-Path $script:RepoRoot "src/PalLLM.Sidecar/Program.cs"
    $codeApiRoutes = @(Select-String -Path $routeSourcePaths -Pattern 'api\.Map(Get|Post|Put|Delete|Patch)').Count
    $codeOperationalRoutes = @(
        Select-String -Path $routeSourcePaths -Pattern 'app\.MapGet\("/metrics"|app\.MapGet\("/"|app\.MapHealthChecks\(|app\.MapOpenApi'
    ).Count

    $codeProtocolRoutes = @(Select-String -Path $programPath -Pattern 'app\.MapMcp\("/mcp"\)').Count

    $apiText = Get-RepoFile "docs/API.md"
    $roadmapText = Get-RepoFile "docs/ROADMAP.md"

    $apiRouteMatch = [regex]::Match($apiText, 'Total `/api` routes: (\d+)')
    $apiOpsMatch = [regex]::Match($apiText, 'Operational routes outside `/api`: (\d+)')
    $apiProtocolMatch = [regex]::Match($apiText, 'Separate protocol route: (\d+)')
    $roadmapOpsMatch = [regex]::Match($roadmapText, "$script:BT(\d+)$script:BT operational routes outside $script:BT/api$script:BT")
    $roadmapProtocolMatch = [regex]::Match($roadmapText, "$script:BT(\d+)$script:BT separate protocol route")
    $handoffText = Get-RepoFile "docs/HANDOFF.md"
    $handoffOpsMatch = [regex]::Match($handoffText, "$script:BT(\d+)$script:BT operational routes outside $script:BT/api$script:BT")
    $handoffProtocolMatch = [regex]::Match($handoffText, "$script:BT(\d+)$script:BT separate $script:BT/mcp$script:BT protocol route")

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

function Assert-FeatureCatalogCount {
    $codeFeatures = (Select-String -Path (Join-Path $script:RepoRoot "src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs") -Pattern 'Id = "[a-z0-9-]+"').Count
    $roadmapPattern = "$script:BT(\d+)$script:BT feature-catalog entries"
    $readmePattern = '`(\d+)`\s*\r?\n> feature-catalog entries'
    $architecturePattern = 'contains `(\d+)` entries'
    $apiPattern = 'Feature catalog \(`(\d+)` entries'
    $codeMapPattern = 'All (\d+) entries'
    $handoffPattern = "$script:BT(\d+)$script:BT feature-catalog entries"
    $roadmapFeatures = [int]([regex]::Match((Get-RepoFile "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    $readmeFeatures = [int]([regex]::Match((Get-RepoFile "README.md"), $readmePattern).Groups[1].Value)
    $architectureFeatures = [int]([regex]::Match((Get-RepoFile "docs/ARCHITECTURE.md"), $architecturePattern).Groups[1].Value)
    $apiFeatures = [int]([regex]::Match((Get-RepoFile "docs/API.md"), $apiPattern).Groups[1].Value)
    $codeMapFeatures = [int]([regex]::Match((Get-RepoFile "docs/CODE_MAP.md"), $codeMapPattern).Groups[1].Value)
    $handoffFeatures = [int]([regex]::Match((Get-RepoFile "docs/HANDOFF.md"), $handoffPattern).Groups[1].Value)
    Write-Output "code=$codeFeatures roadmap=$roadmapFeatures readme=$readmeFeatures architecture=$architectureFeatures api=$apiFeatures codemap=$codeMapFeatures handoff=$handoffFeatures"
    if ($codeFeatures -ne $roadmapFeatures -or $codeFeatures -ne $readmeFeatures -or $codeFeatures -ne $architectureFeatures -or $codeFeatures -ne $apiFeatures -or $codeFeatures -ne $codeMapFeatures -or $codeFeatures -ne $handoffFeatures) {
        throw "feature count drift (code=$codeFeatures roadmap=$roadmapFeatures readme=$readmeFeatures architecture=$architectureFeatures api=$apiFeatures codemap=$codeMapFeatures handoff=$handoffFeatures)"
    }
}

function Assert-FeatureStatusSplit {
    $featureText = Get-RepoFile "src/PalLLM.Domain/Runtime/PalLlmFeatureCatalog.cs"
    $codeReady = ([regex]::Matches($featureText, 'Status = "ready"')).Count
    $codeScaffolded = ([regex]::Matches($featureText, 'Status = "scaffolded"')).Count
    $codeDeferred = ([regex]::Matches($featureText, 'Status = "deferred"')).Count

    $readmeText = Get-RepoFile "README.md"
    $roadmapText = Get-RepoFile "docs/ROADMAP.md"
    $architectureText = Get-RepoFile "docs/ARCHITECTURE.md"
    $apiText = Get-RepoFile "docs/API.md"
    $handoffText = Get-RepoFile "docs/HANDOFF.md"

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

function Assert-FallbackStrategyCount {
    $codeStrategies = (Select-String -Path (Join-Path $script:RepoRoot "src/PalLLM.Domain/Runtime/FallbackBehaviorEngine.cs") -Pattern '^\s*private static FallbackBehaviorDecision (Try[A-Z][A-Za-z]+|CreateGeneralDirector)').Count
    $roadmapPattern = "$script:BT(\d+)$script:BT deterministic fallback strategies"
    $roadmapStrategies = [int]([regex]::Match((Get-RepoFile "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    Write-Output "code=$codeStrategies roadmap=$roadmapStrategies"
    if ($codeStrategies -ne $roadmapStrategies) {
        throw "strategy count drift (code=$codeStrategies roadmap=$roadmapStrategies)"
    }
}

function Assert-TestCountDocs {
    $codeTests = (Get-ChildItem -Recurse -Path (Join-Path $script:RepoRoot "tests/PalLLM.Tests") -Filter "*.cs" | Select-String -Pattern '^\s*\[(?:TestCase|Test)(?:\(|\])').Count
    $roadmapPattern = "$script:BT(\d+)$script:BT passing NUnit tests"
    $architecturePattern = '`(\d+)` tests passed on'
    $codeMapPattern = 'NUnit, (\d+) tests'
    $handoffPattern = "$script:BT(\d+)$script:BT passing tests"
    $roadmapTests = [int]([regex]::Match((Get-RepoFile "docs/ROADMAP.md"), $roadmapPattern).Groups[1].Value)
    $readmeTests  = [int]([regex]::Match((Get-RepoFile "README.md"), 'Passed: (\d+)').Groups[1].Value)
    $architectureTests = [int]([regex]::Match((Get-RepoFile "docs/ARCHITECTURE.md"), $architecturePattern).Groups[1].Value)
    $codeMapTests = [int]([regex]::Match((Get-RepoFile "docs/CODE_MAP.md"), $codeMapPattern).Groups[1].Value)
    $handoffTests = [int]([regex]::Match((Get-RepoFile "docs/HANDOFF.md"), $handoffPattern).Groups[1].Value)
    Write-Output "code=$codeTests roadmap=$roadmapTests readme=$readmeTests architecture=$architectureTests codemap=$codeMapTests handoff=$handoffTests"
    if ($roadmapTests -ne $readmeTests -or $codeTests -ne $roadmapTests -or $codeTests -ne $architectureTests -or $codeTests -ne $codeMapTests -or $codeTests -ne $handoffTests) {
        throw "test count drift (code=$codeTests roadmap=$roadmapTests readme=$readmeTests architecture=$architectureTests codemap=$codeMapTests handoff=$handoffTests)"
    }
}

# When run directly (not dot-sourced with -DefineOnly), execute every check.
# Any throw propagates and, with $ErrorActionPreference=Stop, exits non-zero.
if (-not $DefineOnly) {
    $checks = @(
        @{ Name = "Api route count";        Fn = { Assert-ApiRouteCount } },
        @{ Name = "Api reference surface";  Fn = { Assert-ApiReferenceSurface } },
        @{ Name = "Feature catalog count";  Fn = { Assert-FeatureCatalogCount } },
        @{ Name = "Feature status split";   Fn = { Assert-FeatureStatusSplit } },
        @{ Name = "Fallback strategy count";Fn = { Assert-FallbackStrategyCount } },
        @{ Name = "Test count docs";        Fn = { Assert-TestCountDocs } }
    )
    foreach ($check in $checks) {
        Write-Host "[$($check.Name)] " -NoNewline
        & $check.Fn
    }
    Write-Host "All doc-count drift checks passed."
}
