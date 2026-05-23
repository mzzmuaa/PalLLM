<#
.SYNOPSIS
    Real-world latency benchmark against a running PalLLM sidecar.
    Measures median / p95 / max chat-turn latency across N probes,
    compares to the per-tier budgets in docs/HOT_PATH.md, and names
    the likely bottleneck.

.DESCRIPTION
    The single command operators want when asking "is this fast
    enough?" or "what's actually slowing me down?". Sends N chat
    probes through /api/chat, records wall-clock latency per call,
    and prints:

      - Hardware tier (from /api/hardware)
      - Sample count + actual latency (median / p95 / max)
      - Per-tier budget from HOT_PATH.md (cold + warm)
      - Verdict: under-budget / at-budget / over-budget
      - Likely bottleneck (model size / inference engine / fallback
        path / network) -- inferred from the response surface

    The first probe usually pays cold-load cost. By default the
    script discards it and reports on the warm samples; pass
    -IncludeCold to keep it.

.PARAMETER BaseUrl
    Sidecar URL. Default http://localhost:5088.

.PARAMETER Probes
    Number of chat probes to run. Default 10. Minimum 3.

.PARAMETER CharacterId
    Forwarded to /api/chat. Default 1.

.PARAMETER IncludeCold
    Include the first probe in the latency stats. Default off
    (the first probe is reported separately as the cold figure).

.PARAMETER Json
    Emit a structured record instead of pretty text. Suitable for
    feeding into a dashboard, a CI gate, or a regression harness.

.EXAMPLE
    pwsh ./scripts/pal-benchmark.ps1
    # Default: 10 probes, prints summary against per-tier budgets.

.EXAMPLE
    pwsh ./scripts/pal-benchmark.ps1 -Probes 30 -Json
    # 30 samples, structured output for a dashboard panel.

.NOTES
    Verb shortcut:  pal benchmark

    The probes go through the same /api/chat path the in-game flow
    uses, so the numbers reflect what a player will actually feel.
    Per-tier budgets come from docs/HOT_PATH.md and are honest
    about what's reasonable on each hardware tier.
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',
    [int]$Probes = 10,
    [int]$CharacterId = 1,
    [switch]$IncludeCold,
    [switch]$Json
)

$ErrorActionPreference = 'Stop'
if ($Probes -lt 3) { $Probes = 3 }

$prompts = @(
    'Quick status check.'
    'Anything I should know before we head out?'
    'What''s the perimeter look like?'
    'How are the pals doing?'
    'Talk me through the next move.'
    'How''s the weather?'
    'Where''s the closest threat?'
    'What did we miss yesterday?'
    'Got time for a small detour?'
    'Help me prioritize.'
    'Plan the next ten minutes.'
    'Anything ticking I should look at?'
)

# Probe sidecar reachability up front.
try {
    $null = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/health" -Method Get -TimeoutSec 5 -ErrorAction Stop
} catch {
    Write-Host ""
    Write-Host "Sidecar not reachable at $BaseUrl." -ForegroundColor Red
    Write-Host "  Try: pal play   (boot sidecar in a window + open dashboard)" -ForegroundColor Yellow
    Write-Host ""
    exit 1
}

# Pull the hardware tier so we can compare to the right budget row.
$hw = $null
try {
    $hw = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/hardware" -Method Get -TimeoutSec 5 -ErrorAction Stop
} catch {
    # Non-fatal; we'll fall through to "Standard" tier in the budget table.
}
$tier = if ($hw -and $hw.detectedTier) { $hw.detectedTier } else { 'Standard' }

# Per-tier budget rows (mirrors the warm-path columns in docs/HOT_PATH.md).
# Numbers are warm latency budgets in milliseconds for the chat hot path.
$budgets = @{
    'Constrained' = @{ ColdMs = 4000; WarmMs = 1500; Notes = 'Older laptop / no GPU.' }
    'Standard'    = @{ ColdMs = 2500; WarmMs =  900; Notes = 'Mainstream desktop / mid-range GPU.' }
    'Generous'    = @{ ColdMs = 1500; WarmMs =  600; Notes = '4070-class GPU / 24+ GB VRAM.' }
    'Blackwell'   = @{ ColdMs =  900; WarmMs =  450; Notes = '5090 / B-series with NVFP4.' }
}
$budget = if ($budgets.ContainsKey($tier)) { $budgets[$tier] } else { $budgets['Standard'] }

# -----------------------------------------------------------------------------
# Run the probes
# -----------------------------------------------------------------------------

$samples = New-Object System.Collections.ArrayList
$paths = New-Object System.Collections.Generic.HashSet[string]
$fallbackHits = 0
$liveHits = 0
$errors = 0

Write-Host ""
Write-Host "PalLLM benchmark" -ForegroundColor Cyan
Write-Host ("  target  : {0}/api/chat" -f $BaseUrl.TrimEnd('/'))
Write-Host ("  tier    : {0}" -f $tier)
Write-Host ("  probes  : {0}" -f $Probes)
Write-Host ""

for ($i = 0; $i -lt $Probes; $i++) {
    $message = $prompts[$i % $prompts.Count]
    $body = @{ userMessage = $message; characterId = $CharacterId } | ConvertTo-Json -Compress
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/api/chat" -Method Post `
            -ContentType 'application/json' -Body $body -TimeoutSec 30 -ErrorAction Stop
        $watch.Stop()
        $latencyMs = [int]$watch.Elapsed.TotalMilliseconds
        if ($response.usedFallback) { $fallbackHits++ } else { $liveHits++ }
        if ($response.responsePath) { [void]$paths.Add([string]$response.responsePath) }
        [void]$samples.Add([pscustomobject]@{
            Index       = $i
            LatencyMs   = $latencyMs
            UsedFallback= [bool]$response.usedFallback
            Path        = [string]$response.responsePath
            Strategy    = [string]$response.fallbackStrategy
        })
        $tag = if ($response.usedFallback) { 'fallback' } else { 'live' }
        Write-Host ("  [{0,2}/{1}] {2,5} ms   {3,-9}  {4}" -f ($i + 1), $Probes, $latencyMs, $tag, $response.responsePath)
    } catch {
        $watch.Stop()
        $errors++
        [void]$samples.Add([pscustomobject]@{
            Index = $i
            LatencyMs = $null
            UsedFallback = $null
            Path = $null
            Strategy = $null
            Error = $_.Exception.Message
        })
        Write-Host ("  [{0,2}/{1}] FAILED        {2}" -f ($i + 1), $Probes, $_.Exception.Message) -ForegroundColor Red
    }
}

# -----------------------------------------------------------------------------
# Summarise
# -----------------------------------------------------------------------------

$validLatencies = $samples | Where-Object { $_.LatencyMs -ne $null } | Select-Object -ExpandProperty LatencyMs
$cold = $null
$warm = $validLatencies
if (-not $IncludeCold.IsPresent -and $validLatencies.Count -ge 2) {
    $cold = $validLatencies[0]
    $warm = $validLatencies[1..($validLatencies.Count - 1)]
}

function Get-Percentile {
    param([int[]]$Values, [double]$P)
    if (-not $Values -or $Values.Count -eq 0) { return 0 }
    $sorted = ($Values | Sort-Object)
    $idx = [int][math]::Ceiling($P * $sorted.Count) - 1
    if ($idx -lt 0) { $idx = 0 }
    if ($idx -ge $sorted.Count) { $idx = $sorted.Count - 1 }
    return $sorted[$idx]
}

$median = Get-Percentile -Values $warm -P 0.50
$p95    = Get-Percentile -Values $warm -P 0.95
$max    = if ($warm) { ($warm | Measure-Object -Maximum).Maximum } else { 0 }

# Verdict against budget
$verdict = if ($median -le $budget.WarmMs) {
    'UNDER budget (warm)'
} elseif ($median -le ($budget.WarmMs * 1.5)) {
    'AT budget (warm; tight)'
} else {
    'OVER budget (warm)'
}

# Bottleneck heuristic
$bottleneck = ''
if ($errors -gt 0) {
    $bottleneck = 'request failures - check sidecar logs first'
} elseif ($fallbackHits -gt 0 -and $liveHits -eq 0) {
    $bottleneck = 'fully on deterministic fallback - no live inference wired (run `pal connect ollama`, `pal connect llamacpp`, `pal connect lmstudio`, `pal connect vllm`, `pal connect foundry`, or `pal connect transformers`)'
} elseif ($median -gt $budget.WarmMs * 2) {
    $bottleneck = 'model latency dominates - try a smaller model or NVFP4 quant if hardware supports'
} elseif ($median -gt $budget.WarmMs * 1.2 -and $tier -eq 'Standard') {
    $bottleneck = 'standard tier near ceiling - consider dropping to a smaller model'
} else {
    $bottleneck = 'within tier budget - no obvious bottleneck'
}

if ($Json.IsPresent) {
    [pscustomobject]@{
        BaseUrl       = $BaseUrl
        Tier          = $tier
        ProbesAttempted = $Probes
        ProbesValid   = $validLatencies.Count
        FallbackHits  = $fallbackHits
        LiveHits      = $liveHits
        Errors        = $errors
        ColdMs        = $cold
        MedianMs      = $median
        P95Ms         = $p95
        MaxMs         = $max
        Budget        = $budget
        Verdict       = $verdict
        Bottleneck    = $bottleneck
        Paths         = ([string[]]$paths)
        Samples       = $samples
    } | ConvertTo-Json -Depth 6
    return
}

Write-Host ""
Write-Host "Summary" -ForegroundColor Cyan
if ($null -ne $cold) {
    Write-Host ("  cold       : {0} ms" -f $cold)
}
Write-Host ("  median     : {0} ms" -f $median)
Write-Host ("  p95        : {0} ms" -f $p95)
Write-Host ("  max        : {0} ms" -f $max)
Write-Host ""
Write-Host "Budget for tier $tier (from docs/HOT_PATH.md):" -ForegroundColor White
Write-Host ("  cold cap   : {0} ms" -f $budget.ColdMs)
Write-Host ("  warm cap   : {0} ms" -f $budget.WarmMs)
Write-Host ("  notes      : {0}" -f $budget.Notes)
Write-Host ""
$verdictColor = if ($verdict -like 'UNDER*') { 'Green' } elseif ($verdict -like 'AT*') { 'Yellow' } else { 'Red' }
Write-Host "Verdict   : $verdict" -ForegroundColor $verdictColor
Write-Host "Bottleneck: $bottleneck" -ForegroundColor DarkGray
Write-Host ""
Write-Host ("Response paths seen ({0}):" -f $paths.Count) -ForegroundColor DarkGray
foreach ($p in $paths) { Write-Host "  $p" -ForegroundColor DarkGray }
Write-Host ""
