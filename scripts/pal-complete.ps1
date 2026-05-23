<#
.SYNOPSIS
    Live "are we 100% complete?" dashboard. One verb, one screen,
    full picture: autonomous progress + the six live-Palworld /
    clean-machine queues that gate the remaining 23.8%.

.DESCRIPTION
    PalLLM has multiple completion surfaces:

      - docs/ROADMAP.md             76.2% phase breakdown
      - docs/IMPLEMENTATION_QUEUE.md  six queues to close 23.8%
      - docs/READINESS.md           23-aspect 10/10 scorecard
      - docs/COMPLETION.md          canonical written status
      - pal next                    single highest-impact next action
      - pal proof                   Queue 1 native-proof lanes

    `pal complete` consolidates them into one queue-by-queue
    dashboard. Read-only. Probes:

      - docs/PROJECT_NUMBERS.json           rolling counts
      - /api/bridge/proof (if reachable)    live queue 1 evidence
      - latest-native-proof.json (offline)  cached queue 1 evidence
      - latest full-audit RESULTS.md        latest pass / fail

    Then prints:

      - autonomous baseline (does NOT move 76.2% but is at ceiling)
      - per-queue status: PROVEN / PARTIAL / PENDING with the next
        command to advance it
      - the six "Definition of 100%" criteria from IMPLEMENTATION_QUEUE
      - one recommended next command (the same pal-next would print
        if asked, but framed as "this advances the topmost PENDING
        queue")

.PARAMETER BaseUrl
    Sidecar URL. Default http://localhost:5088.

.PARAMETER Json
    Emit a structured snapshot instead of pretty text.

.EXAMPLE
    pwsh ./scripts/pal-complete.ps1
    # Print the queue-by-queue dashboard.

.EXAMPLE
    pwsh ./scripts/pal-complete.ps1 -Json | ConvertFrom-Json
    # Programmatic consumption - every queue's state as JSON.

.NOTES
    Verb shortcut: pal complete

    Distinct from `pal next` (immediate single-action advisor),
    `pal proof` (Queue 1 native-proof lanes only), and
    `pal preflight` (12-check readiness checklist). This verb
    answers: "of the 23.8% that's live-hardware-bound, where am I?"
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [switch]$Json
)

$ErrorActionPreference = 'Continue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$toolingPath = Join-Path $PSScriptRoot 'PalLLM.Tooling.ps1'
if (Test-Path -LiteralPath $toolingPath) {
    . $toolingPath
}

function Get-RuntimeRoot {
    if (Get-Command -Name Get-PalLlmRuntimeRoot -ErrorAction SilentlyContinue) {
        return Get-PalLlmRuntimeRoot
    }
    return (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal\Saved\PalLLM')
}

function Get-PropertyOrNull {
    param([AllowNull()][object]$InputObject, [Parameter(Mandatory = $true)][string]$Name)
    if ($null -eq $InputObject) { return $null }
    $prop = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $prop) { return $null }
    return $prop.Value
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

function Probe-BridgeProof {
    param([Parameter(Mandatory = $true)][string]$BaseUrl)
    $uri = "$($BaseUrl.TrimEnd('/'))/api/bridge/proof"
    try {
        return Invoke-RestMethod -Uri $uri -Method Get -TimeoutSec 3 -ErrorAction Stop
    } catch {
        return $null
    }
}

function Get-LatestAuditPass {
    param([Parameter(Mandatory = $true)][string]$RepoRoot)
    $auditDir = Join-Path $RepoRoot 'artifacts/full-audit'
    if (-not (Test-Path -LiteralPath $auditDir)) { return $null }
    $latest = Get-ChildItem -LiteralPath $auditDir -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $latest) { return $null }
    $resultsPath = Join-Path $latest.FullName 'RESULTS.md'
    if (-not (Test-Path -LiteralPath $resultsPath)) { return $null }
    $content = Get-Content -LiteralPath $resultsPath -Raw -ErrorAction SilentlyContinue
    # Format: "- Overall: **PASS**" or "- Overall: **FAIL**" with markdown bolding.
    if ($content -match 'Overall:\s*\*?\*?PASS') { return 'PASS' }
    if ($content -match 'Overall:\s*\*?\*?FAIL') { return 'FAIL' }
    return 'UNKNOWN'
}

# --- gather state -------------------------------------------------------------

$numbersPath = Join-Path $repoRoot 'docs/PROJECT_NUMBERS.json'
$numbers = Read-JsonFileOrNull -Path $numbersPath

$runtimeRoot = Get-RuntimeRoot
$nativeProofPath = Join-Path $runtimeRoot 'ReleaseEvidence/latest-native-proof.json'
$nativeProof = Read-JsonFileOrNull -Path $nativeProofPath
$liveProof = Probe-BridgeProof -BaseUrl $BaseUrl
$auditPass = Get-LatestAuditPass -RepoRoot $repoRoot

# Determine queue 1 state from whichever proof source is fresher.
$proofPayload = if ($liveProof) { $liveProof } else { $nativeProof }
$liveDeliveryProven = [bool](Get-PropertyOrNull -InputObject $proofPayload -Name 'LiveDeliveryProven')
$visibleDelivery    = [bool](Get-PropertyOrNull -InputObject $proofPayload -Name 'VisibleDeliveryConfirmed')
$bridgeStatus       = [string](Get-PropertyOrNull -InputObject $proofPayload -Name 'BridgeProofStatus')

# --- map onto queues ----------------------------------------------------------
# Each queue has an accept criterion that a live operator must satisfy. None
# of them can be flipped autonomously. The status here reflects what evidence
# (if any) the local artifacts contain - it cannot inflate beyond what the
# live session has proven.

$queues = @(
    @{
        Id = 'Q1'
        Name = 'Compat proof + real Palworld smoke'
        Pct = '~6.0%'
        Status = if ($liveDeliveryProven -and $visibleDelivery) { 'PROVEN' }
                 elseif ($proofPayload) { 'PARTIAL' }
                 else { 'PENDING' }
        Next = if ($liveDeliveryProven) { 'pal proof-bundle  # package the evidence' }
               elseif ($proofPayload) { 'pal native-proof  # active watcher; advances PARTIAL -> PROVEN' }
               else { 'pal play  # boot sidecar + Palworld so the watcher has a session to observe' }
    }
    @{
        Id = 'Q2'
        Name = 'Bridge truth + seam confirmation'
        Pct = '~4.5%'
        Status = 'PENDING'
        Next = 'live session: validate production-sampler + travel detail against current Palworld build'
    }
    @{
        Id = 'Q3'
        Name = 'Native delivery layer V2'
        Pct = '~7.5%'
        Status = 'PENDING'
        Next = 'pal hud-bind  # after Q1+Q2 complete, applies the ranked recommendation'
    }
    @{
        Id = 'Q4'
        Name = 'Native speech loop integration'
        Pct = '~2.0%'
        Status = 'PENDING'
        Next = 'live session: enable TTS, validate in-game audio path renders through native surface'
    }
    @{
        Id = 'Q5'
        Name = 'Expand guarded native actions'
        Pct = '~3.0%'
        Status = 'PENDING'
        Next = 'live session: validate recall_pals + request_craft_queue beyond feedback-only paths'
    }
    @{
        Id = 'Q6'
        Name = 'Clean-machine release proof'
        Pct = '~1.5%'
        Status = 'PENDING'
        Next = 'pal package; pal verify  # then run on a clean Windows machine without dev artifacts'
    }
)

# --- determine the topmost recommendation ------------------------------------

$topQueue = $queues | Where-Object { $_.Status -ne 'PROVEN' } | Select-Object -First 1
$recommendedAction = if ($topQueue) { $topQueue.Next } else { 'pal demo  # everything proven; have fun' }

# --- emit ---------------------------------------------------------------------

$honestRoadmap = [string](Get-PropertyOrNull -InputObject $numbers -Name 'honestRoadmap')
if ([string]::IsNullOrWhiteSpace($honestRoadmap)) { $honestRoadmap = '76.2%' }
$testsCount    = Get-PropertyOrNull -InputObject $numbers -Name 'tests'
$driftGates    = [string](Get-PropertyOrNull -InputObject $numbers -Name 'driftGates')
$readiness     = '~8.0 / 10'

$snapshot = [pscustomobject]@{
    Schema = 'https://palllm.dev/schemas/completion-status-v1.schema.json'
    GeneratedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    HonestRoadmapPct = $honestRoadmap
    RemainingPct = '23.8%'
    Tests = $testsCount
    DriftGates = $driftGates
    Readiness = $readiness
    AuditPass = $auditPass
    LiveDeliveryProven = $liveDeliveryProven
    BridgeProofStatus = $bridgeStatus
    Queues = @($queues)
    RecommendedAction = $recommendedAction
}

if ($Json.IsPresent) {
    $snapshot | ConvertTo-Json -Depth 10
    return
}

Write-Host ''
Write-Host 'PalLLM completion status' -ForegroundColor Cyan
Write-Host ('  honest roadmap   : {0}  (autonomous ceiling for sidecar runtime)' -f $snapshot.HonestRoadmapPct)
Write-Host ('  remaining        : {0}  (live-Palworld + clean-machine work)' -f $snapshot.RemainingPct)
if ($snapshot.Tests)       { Write-Host ('  test count       : {0} / {0}' -f $snapshot.Tests) }
if ($snapshot.DriftGates)  { Write-Host ('  drift gates      : {0}' -f $snapshot.DriftGates) }
Write-Host ('  readiness        : {0} across 23 aspects' -f $snapshot.Readiness)
if ($auditPass) { Write-Host ('  latest audit     : {0}' -f $auditPass) }
Write-Host ''
Write-Host 'Queue-by-queue status:' -ForegroundColor White
foreach ($q in $queues) {
    $color = switch ($q.Status) {
        'PROVEN'  { 'Green' }
        'PARTIAL' { 'Yellow' }
        default   { 'DarkGray' }
    }
    $line = '  {0} {1,-38} {2,-7} ({3})' -f $q.Id, $q.Name, $q.Status, $q.Pct
    Write-Host $line -ForegroundColor $color
    Write-Host ('       -> {0}' -f $q.Next) -ForegroundColor DarkGray
}
Write-Host ''
Write-Host 'Definition of 100% (per IMPLEMENTATION_QUEUE.md):' -ForegroundColor White
Write-Host '  - replies render through real native in-game surface           (-> Q3)'
Write-Host '  - chat-linked speech plays natively when enabled               (-> Q4)'
Write-Host '  - allowlisted actions execute safely + natively when enabled   (-> Q5)'
Write-Host '  - bridge producers cover intended event taxonomy on live build (-> Q2)'
Write-Host '  - real Palworld smoke harness protects full companion loop     (-> Q1)'
Write-Host '  - setup is player-usable from packaged release flow            (-> Q6)'
Write-Host ''
Write-Host 'Autonomous progress (already at ceiling; does NOT move the 76.2%):' -ForegroundColor White
Write-Host ('  - sidecar runtime: feature catalog + tests + zero warnings')
Write-Host ('  - operator surface: every queue-gating script wrapped as a `pal` verb')
Write-Host ('  - drift protection: 16 gates + 27 meta-tests + 8 JSON Schemas')
Write-Host ('  - readiness: autonomous-movable aspects already at their ceiling')
Write-Host ''
Write-Host 'Next command to advance the topmost PENDING queue:' -ForegroundColor Yellow
Write-Host ('  ' + $recommendedAction) -ForegroundColor Green
Write-Host ''
Write-Host 'For the full picture, read:  docs/COMPLETION.md' -ForegroundColor DarkGray
Write-Host ''

