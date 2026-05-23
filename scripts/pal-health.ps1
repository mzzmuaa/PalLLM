<#
.SYNOPSIS
    Generate one local PalLLM health snapshot as Markdown plus JSON.

.DESCRIPTION
    Composes existing PalLLM evidence into a single timestamped artifact:
    rolling counts, latest audit/publish/AOT artifacts, live sidecar probes
    when available, runtime-root evidence, doc freshness, and the next
    production blocker.

    This command is intentionally read-mostly. It does not build, test,
    audit, launch Palworld, or contact the public internet. The only HTTP
    calls are optional probes against the configured sidecar BaseUrl.

.PARAMETER BaseUrl
    Sidecar URL to probe. Default http://localhost:5088.

.PARAMETER OutputRoot
    Root folder for timestamped health artifacts. Default:
    artifacts/health-snapshot.

.PARAMETER NoWrite
    Build the snapshot in memory and print the planned output path without
    writing files.

.PARAMETER Json
    Print the JSON snapshot to stdout in addition to writing artifacts.

.EXAMPLE
    pwsh ./scripts/pal-health.ps1
    # Writes artifacts/health-snapshot/<timestamp>/HEALTH.md and health.json.

.EXAMPLE
    pwsh ./scripts/pal-health.ps1 -Json
    # Also emits the structured snapshot to stdout.

.NOTES
    Verb shortcut: pal health
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [string]$OutputRoot,
    [switch]$NoWrite,
    [switch]$Json
)

$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolingPath = Join-Path $PSScriptRoot 'PalLLM.Tooling.ps1'
if (Test-Path -LiteralPath $toolingPath) {
    . $toolingPath
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts/health-snapshot'
}

function Read-JsonFileOrNull {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        return Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Get-LatestArtifactSummary {
    param(
        [Parameter(Mandatory = $true)][string]$RelativeRoot,
        [string]$ResultFileName = 'RESULTS.md'
    )

    $root = Join-Path $repoRoot $RelativeRoot
    if (-not (Test-Path -LiteralPath $root)) {
        return [pscustomobject]@{
            Exists = $false
            Name = $null
            Directory = $root
            ResultPath = $null
            Overall = 'MISSING'
            GeneratedUtc = $null
        }
    }

    $latest = Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        return [pscustomobject]@{
            Exists = $false
            Name = $null
            Directory = $root
            ResultPath = $null
            Overall = 'MISSING'
            GeneratedUtc = $null
        }
    }

    $resultPath = Join-Path $latest.FullName $ResultFileName
    $overall = 'UNKNOWN'
    if (Test-Path -LiteralPath $resultPath) {
        $text = Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($text -match '(?m)^- Overall:\s*\*\*(.+?)\*\*') {
            $overall = $Matches[1].Trim()
        } elseif ($text -match '(?im)^Status:\s*(.+?)\s*$') {
            $overall = $Matches[1].Trim()
        } elseif ($text -match '(?i)\bPASS\b') {
            $overall = 'PASS'
        } elseif ($text -match '(?i)\bWARN\b') {
            $overall = 'WARN'
        } elseif ($text -match '(?i)\bFAIL\b') {
            $overall = 'FAIL'
        }
    }

    return [pscustomobject]@{
        Exists = $true
        Name = $latest.Name
        Directory = $latest.FullName
        ResultPath = if (Test-Path -LiteralPath $resultPath) { $resultPath } else { $null }
        Overall = $overall
        GeneratedUtc = $latest.LastWriteTimeUtc.ToString('o')
    }
}

function Invoke-SidecarGet {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$TimeoutSeconds = 3
    )

    $uri = "{0}{1}" -f $BaseUrl.TrimEnd('/'), $Path
    try {
        $data = Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec $TimeoutSeconds -ErrorAction Stop
        return [pscustomobject]@{
            Reachable = $true
            Uri = $uri
            Error = $null
            Data = $data
        }
    } catch {
        return [pscustomobject]@{
            Reachable = $false
            Uri = $uri
            Error = $_.Exception.Message
            Data = $null
        }
    }
}

function New-SkippedSidecarGet {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [pscustomobject]@{
        Reachable = $false
        Uri = "{0}{1}" -f $BaseUrl.TrimEnd('/'), $Path
        Error = 'skipped because /api/health was not reachable'
        Data = $null
    }
}

function Get-DocFreshnessSummary {
    $thresholdDays = 45
    $docsRoot = Join-Path $repoRoot 'docs'
    $stamped = 0
    $stale = 0
    $newest = $null
    $oldest = $null
    $now = Get-Date

    if (Test-Path -LiteralPath $docsRoot) {
        foreach ($file in Get-ChildItem -LiteralPath $docsRoot -Filter '*.md' -Recurse -File -ErrorAction SilentlyContinue) {
            $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
            if ($text -match 'Last audited:\s+`(\d{4}-\d{2}-\d{2})`') {
                $stamped++
                try {
                    $stamp = [datetime]::ParseExact($Matches[1], 'yyyy-MM-dd', [System.Globalization.CultureInfo]::InvariantCulture)
                    if ($null -eq $newest -or $stamp -gt $newest) { $newest = $stamp }
                    if ($null -eq $oldest -or $stamp -lt $oldest) { $oldest = $stamp }
                    if (($now - $stamp).TotalDays -gt $thresholdDays) { $stale++ }
                } catch {
                }
            }
        }
    }

    return [pscustomobject]@{
        ThresholdDays = $thresholdDays
        StampedDocs = $stamped
        StaleDocs = $stale
        NewestStamp = if ($newest) { $newest.ToString('yyyy-MM-dd') } else { $null }
        OldestStamp = if ($oldest) { $oldest.ToString('yyyy-MM-dd') } else { $null }
    }
}

function Get-RecentFileSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [string[]]$Patterns = @('*.json', '*.md', '*.log', '*.txt'),
        [int]$Count = 5
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return @()
    }

    $files = New-Object System.Collections.ArrayList
    foreach ($pattern in $Patterns) {
        foreach ($file in Get-ChildItem -LiteralPath $Path -Filter $pattern -File -ErrorAction SilentlyContinue) {
            [void]$files.Add($file)
        }
    }

    return @($files |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First $Count |
        ForEach-Object {
            [pscustomobject]@{
                Name = $_.Name
                Path = $_.FullName
                Bytes = $_.Length
                LastWriteUtc = $_.LastWriteTimeUtc.ToString('o')
            }
        })
}

function ConvertTo-HealthMarkdown {
    param([Parameter(Mandatory = $true)]$Snapshot)

    $nl = "`n"
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.Append("# PalLLM Health Snapshot$nl$nl")
    [void]$sb.Append(("Generated: {0}$nl$nl" -f $Snapshot.GeneratedAtLocal))
    [void]$sb.Append("Auto-generated by `scripts/pal-health.ps1`. Do not edit generated artifacts by hand.$nl$nl")

    [void]$sb.Append("## Verdict$nl$nl")
    [void]$sb.Append(("- Overall: **{0}**$nl" -f $Snapshot.Overall))
    [void]$sb.Append(("- Reason: {0}$nl" -f $Snapshot.OverallReason))
    [void]$sb.Append(("- Next action: ``{0}``{1}{1}" -f $Snapshot.NextAction, $nl))

    [void]$sb.Append("## Counts$nl$nl")
    [void]$sb.Append("| Metric | Value |$nl|---|---:|$nl")
    [void]$sb.Append(("| NUnit tests | {0} |$nl" -f $Snapshot.Numbers.tests))
    [void]$sb.Append(("| Drift gates | {0} |$nl" -f $Snapshot.Numbers.driftGates))
    [void]$sb.Append(("| /api routes | {0} |$nl" -f $Snapshot.Numbers.apiRoutes))
    [void]$sb.Append(("| MCP tools | {0} |$nl" -f $Snapshot.Numbers.mcpTools))
    [void]$sb.Append(("| Feature catalog | {0} |$nl" -f $Snapshot.Numbers.featureCatalog))
    [void]$sb.Append(("| Fallback strategies | {0} |$nl" -f $Snapshot.Numbers.fallbackStrategies))
    [void]$sb.Append(("| Docs | {0} |$nl" -f $Snapshot.Numbers.docsCount))
    [void]$sb.Append(("| Honest roadmap | {0} |$nl$nl" -f $Snapshot.Numbers.honestRoadmap))

    [void]$sb.Append("## Latest Evidence$nl$nl")
    [void]$sb.Append("| Lane | Status | Artifact |$nl|---|---|---|$nl")
    foreach ($lane in @($Snapshot.Evidence.FullAudit, $Snapshot.Evidence.PublishAudit, $Snapshot.Evidence.AotReadiness)) {
        $artifact = if ($lane.ResultPath) { $lane.ResultPath } elseif ($lane.Directory) { $lane.Directory } else { '(missing)' }
        [void]$sb.Append(("| {0} | {1} | ``{2}`` |{3}" -f $lane.Label, $lane.Overall, $artifact, $nl))
    }
    [void]$sb.Append("$nl")

    [void]$sb.Append("## Live Sidecar$nl$nl")
    [void]$sb.Append(("- Base URL: ``{0}``{1}" -f $Snapshot.Sidecar.BaseUrl, $nl))
    [void]$sb.Append(("- Health reachable: {0}$nl" -f $Snapshot.Sidecar.HealthReachable))
    [void]$sb.Append(("- Hardware reachable: {0}$nl" -f $Snapshot.Sidecar.HardwareReachable))
    [void]$sb.Append(("- Release readiness reachable: {0}$nl" -f $Snapshot.Sidecar.ReleaseReadinessReachable))
    [void]$sb.Append(("- Bridge proof reachable: {0}$nl$nl" -f $Snapshot.Sidecar.BridgeProofReachable))

    [void]$sb.Append("## Runtime Evidence$nl$nl")
    [void]$sb.Append(("- Runtime root: ``{0}``{1}" -f $Snapshot.Runtime.RuntimeRoot, $nl))
    [void]$sb.Append(("- Runtime root exists: {0}$nl" -f $Snapshot.Runtime.Exists))
    [void]$sb.Append(("- Release evidence files found: {0}$nl" -f $Snapshot.Runtime.ReleaseEvidenceCount))
    [void]$sb.Append(("- Native proof artifact present: {0}$nl$nl" -f $Snapshot.Runtime.NativeProofArtifactPresent))

    [void]$sb.Append("## Documentation$nl$nl")
    [void]$sb.Append(("- Stamped docs: {0}$nl" -f $Snapshot.DocFreshness.StampedDocs))
    [void]$sb.Append(("- Stale docs: {0}$nl" -f $Snapshot.DocFreshness.StaleDocs))
    [void]$sb.Append(("- Newest stamp: {0}$nl" -f $Snapshot.DocFreshness.NewestStamp))
    [void]$sb.Append(("- Oldest stamp: {0}$nl$nl" -f $Snapshot.DocFreshness.OldestStamp))

    [void]$sb.Append("## Useful Commands$nl$nl")
    [void]$sb.Append('```powershell' + $nl)
    [void]$sb.Append("pwsh ./pal.ps1 preflight       # fast player-readiness checklist$nl")
    [void]$sb.Append("pwsh ./pal.ps1 logs            # recent launch/native/audit evidence$nl")
    [void]$sb.Append("pwsh ./pal.ps1 publish-audit   # local publication preflight$nl")
    [void]$sb.Append("pwsh ./pal.ps1 aot-readiness   # trim/AOT readiness scan$nl")
    [void]$sb.Append("pwsh ./pal.ps1 fast-audit      # no-build drift check$nl")
    [void]$sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1$nl")
    [void]$sb.Append('```' + $nl)

    return $sb.ToString()
}

$numbersPath = Join-Path $repoRoot 'docs/PROJECT_NUMBERS.json'
$numbers = Read-JsonFileOrNull -Path $numbersPath
if ($null -eq $numbers) {
    $numbers = [pscustomobject]@{
        tests = 0
        driftGates = 'unknown'
        apiRoutes = 0
        mcpTools = 0
        featureCatalog = 0
        fallbackStrategies = 0
        docsCount = 0
        honestRoadmap = 'unknown'
    }
}

$fullAudit = Get-LatestArtifactSummary -RelativeRoot 'artifacts/full-audit'
$publishAudit = Get-LatestArtifactSummary -RelativeRoot 'artifacts/publish-audit'
$aotReadiness = Get-LatestArtifactSummary -RelativeRoot 'artifacts/aot-readiness'
$fullAudit | Add-Member -NotePropertyName Label -NotePropertyValue 'Full audit' -Force
$publishAudit | Add-Member -NotePropertyName Label -NotePropertyValue 'Publish audit' -Force
$aotReadiness | Add-Member -NotePropertyName Label -NotePropertyValue 'AOT readiness' -Force

$healthProbe = Invoke-SidecarGet -Path '/api/health'
if ($healthProbe.Reachable) {
    $hardwareProbe = Invoke-SidecarGet -Path '/api/hardware'
    $releaseProbe = Invoke-SidecarGet -Path '/api/release/readiness'
    $bridgeProbe = Invoke-SidecarGet -Path '/api/bridge/proof'
} else {
    $hardwareProbe = New-SkippedSidecarGet -Path '/api/hardware'
    $releaseProbe = New-SkippedSidecarGet -Path '/api/release/readiness'
    $bridgeProbe = New-SkippedSidecarGet -Path '/api/bridge/proof'
}

$runtimeRoot = if (Get-Command -Name Get-PalLlmRuntimeRoot -ErrorAction SilentlyContinue) {
    Get-PalLlmRuntimeRoot
} else {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal\Saved\PalLLM'
}
$releaseEvidenceDir = Join-Path $runtimeRoot 'ReleaseEvidence'
$nativeReadinessDir = Join-Path $runtimeRoot 'NativeReadiness'
$releaseEvidenceFiles = Get-RecentFileSummary -Path $releaseEvidenceDir -Count 10
$nativeFiles = Get-RecentFileSummary -Path $nativeReadinessDir -Count 10
$allEvidenceFiles = @($releaseEvidenceFiles) + @($nativeFiles)
$nativeProofFiles = @($allEvidenceFiles | Where-Object {
    $_.Name -match 'native'
})

$docFreshness = Get-DocFreshnessSummary

$repoHealthPass = [string]::Equals($fullAudit.Overall, 'PASS', [System.StringComparison]::OrdinalIgnoreCase)
$publishHealthPass = [string]::Equals($publishAudit.Overall, 'PASS', [System.StringComparison]::OrdinalIgnoreCase)
$nativeProofPresent = (@($nativeProofFiles).Count -gt 0)

$overall = 'REVIEW REQUIRED'
$overallReason = 'Latest repo evidence is usable, but live native Palworld proof is still the main blocker.'
if (-not $repoHealthPass) {
    $overall = 'FAIL'
    $overallReason = 'Latest full audit is not PASS or has not been generated.'
} elseif (-not $publishHealthPass) {
    $overall = 'FAIL'
    $overallReason = 'Latest publication audit is not PASS or has not been generated.'
} elseif ($nativeProofPresent) {
    $overall = 'PASS WITH LIVE EVIDENCE'
    $overallReason = 'Repo, publication, and native proof artifacts are all present.'
}

$timestamp = (Get-Date).ToString('yyyyMMdd-HHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
$outputDir = Join-Path $OutputRoot $timestamp
$markdownPath = Join-Path $outputDir 'HEALTH.md'
$jsonPath = Join-Path $outputDir 'health.json'

$snapshot = [pscustomobject]@{
    Schema = 'https://palllm.dev/schemas/health-snapshot-v1.json'
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    GeneratedAtLocal = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz')
    RepoRoot = $repoRoot
    OutputDir = $outputDir
    Overall = $overall
    OverallReason = $overallReason
    NextAction = 'powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1'
    Numbers = $numbers
    Evidence = [pscustomobject]@{
        FullAudit = $fullAudit
        PublishAudit = $publishAudit
        AotReadiness = $aotReadiness
    }
    Sidecar = [pscustomobject]@{
        BaseUrl = $BaseUrl
        HealthReachable = $healthProbe.Reachable
        HardwareReachable = $hardwareProbe.Reachable
        ReleaseReadinessReachable = $releaseProbe.Reachable
        BridgeProofReachable = $bridgeProbe.Reachable
        Health = $healthProbe.Data
        Hardware = $hardwareProbe.Data
        ReleaseReadiness = $releaseProbe.Data
        BridgeProof = $bridgeProbe.Data
    }
    Runtime = [pscustomobject]@{
        RuntimeRoot = $runtimeRoot
        Exists = (Test-Path -LiteralPath $runtimeRoot)
        ReleaseEvidenceDir = $releaseEvidenceDir
        ReleaseEvidenceCount = $releaseEvidenceFiles.Count
        RecentReleaseEvidence = $releaseEvidenceFiles
        NativeReadinessDir = $nativeReadinessDir
        RecentNativeReadiness = $nativeFiles
        NativeProofArtifactPresent = $nativeProofPresent
    }
    DocFreshness = $docFreshness
}

$markdown = ConvertTo-HealthMarkdown -Snapshot $snapshot
$jsonText = $snapshot | ConvertTo-Json -Depth 20

if (-not $NoWrite.IsPresent) {
    if (-not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText($markdownPath, $markdown, $utf8NoBom)
    [System.IO.File]::WriteAllText($jsonPath, $jsonText, $utf8NoBom)
}

Write-Host ''
Write-Host 'PalLLM health snapshot' -ForegroundColor Cyan
Write-Host ("  overall : {0}" -f $overall)
Write-Host ("  reason  : {0}" -f $overallReason)
if ($NoWrite.IsPresent) {
    Write-Host ("  planned : {0}" -f $outputDir)
} else {
    Write-Host ("  markdown: {0}" -f $markdownPath)
    Write-Host ("  json    : {0}" -f $jsonPath)
}
Write-Host ("  next    : {0}" -f $snapshot.NextAction)
Write-Host ''

if ($Json.IsPresent) {
    $jsonText
}
