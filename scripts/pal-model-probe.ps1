<#
.SYNOPSIS
    Probe a running OpenAI-compatible model endpoint and write a local
    model-serving evidence artifact.

.DESCRIPTION
    Checks the three surfaces that matter before a model lane is trusted:
    /health, /v1/models, and /metrics. The probe does not send chat,
    vision, audio, or tool-call content. It stores only endpoint status,
    model ids, and metric names/categories so support bundles can prove
    the serving shape without retaining player prompts or metric samples.

    This complements `pal models serving`, which prints PalLLM's expected
    serving policy from /api/inference/collaboration. This command checks
    whether the model server actually exposes enough evidence to verify
    cache, speculation, queue, and latency behavior.

.PARAMETER BaseUrl
    OpenAI-compatible model base URL. Accepts either the service root
    (http://127.0.0.1:8080) or the /v1 root
    (http://127.0.0.1:8080/v1). Defaults to PalLLM's bundled llama.cpp
    lane at http://127.0.0.1:8080/v1.

.PARAMETER MetricsUrl
    Optional explicit Prometheus metrics URL. Defaults to <service-root>/metrics.

.PARAMETER OutputDir
    Directory where the JSON artifact is written.

.PARAMETER RequireReady
    Exit non-zero when the probe verdict is blocked or partial.

.PARAMETER Json
    Emit the JSON artifact to stdout.

.PARAMETER DryRun
    Write a deterministic no-network sample artifact. Used by tests and
    by operators who want to inspect the artifact shape before probing.

.EXAMPLE
    pwsh ./pal.ps1 models probe

.EXAMPLE
    pwsh ./pal.ps1 models probe -BaseUrl http://127.0.0.1:8000/v1 -Json

.NOTES
    Verb shortcut: pal models probe
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:8080/v1',
    [string]$MetricsUrl,
    [string]$OutputDir = 'artifacts/model-probe',
    [int]$TimeoutSec = 5,
    [switch]$RequireReady,
    [switch]$Json,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Normalize-Url {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) {
        throw "URL cannot be empty."
    }
    return $Url.Trim().TrimEnd('/')
}

function Get-ServiceRoot {
    param([string]$NormalizedBaseUrl)
    if ($NormalizedBaseUrl.EndsWith('/v1', [StringComparison]::OrdinalIgnoreCase)) {
        return $NormalizedBaseUrl.Substring(0, $NormalizedBaseUrl.Length - 3).TrimEnd('/')
    }
    return $NormalizedBaseUrl
}

function Get-ModelsUrl {
    param([string]$NormalizedBaseUrl)
    if ($NormalizedBaseUrl.EndsWith('/v1', [StringComparison]::OrdinalIgnoreCase)) {
        return "$NormalizedBaseUrl/models"
    }
    return "$NormalizedBaseUrl/v1/models"
}

function Invoke-ProbeGet {
    param(
        [string]$Name,
        [string]$Url,
        [int]$TimeoutSeconds
    )

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest -Uri $Url -Method Get -TimeoutSec $TimeoutSeconds -UseBasicParsing -ErrorAction Stop
        $sw.Stop()
        return [pscustomobject]@{
            name = $Name
            url = $Url
            ok = $true
            statusCode = [int]$response.StatusCode
            latencyMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
            contentLength = if ($null -ne $response.Content) { ([string]$response.Content).Length } else { 0 }
            error = $null
            body = [string]$response.Content
        }
    } catch {
        $sw.Stop()
        $statusCode = $null
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        return [pscustomobject]@{
            name = $Name
            url = $Url
            ok = $false
            statusCode = $statusCode
            latencyMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
            contentLength = 0
            error = $_.Exception.Message
            body = ''
        }
    }
}

function Get-ModelIds {
    param([string]$Body)
    $ids = New-Object System.Collections.Generic.List[string]
    if ([string]::IsNullOrWhiteSpace($Body)) {
        return @()
    }
    try {
        $json = $Body | ConvertFrom-Json
        if ($json.data) {
            foreach ($item in @($json.data)) {
                if ($item.id) { $ids.Add([string]$item.id) }
            }
        } elseif ($json.models) {
            foreach ($item in @($json.models)) {
                if ($item.id) { $ids.Add([string]$item.id) }
                elseif ($item.name) { $ids.Add([string]$item.name) }
            }
        }
    } catch {
        # Non-JSON /v1/models responses are treated as no catalog ids.
    }
    return @($ids | Sort-Object -Unique)
}

function Get-MetricNames {
    param([string]$Body)
    $names = New-Object System.Collections.Generic.HashSet[string]
    if ([string]::IsNullOrWhiteSpace($Body)) {
        return @()
    }
    foreach ($line in ($Body -split "`n")) {
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith('#')) {
            continue
        }
        $match = [regex]::Match($trimmed, '^(?<name>[A-Za-z_:][A-Za-z0-9_:]*)(?:\{|[\s])')
        if ($match.Success) {
            [void]$names.Add($match.Groups['name'].Value)
        }
    }
    return @($names | Sort-Object)
}

function Select-Metrics {
    param(
        [string[]]$Names,
        [string]$Pattern
    )
    return @($Names | Where-Object { $_ -match $Pattern } | Sort-Object -Unique)
}

function New-MetricSummary {
    param([string[]]$MetricNames)

    [string[]]$vllmPrefix = @(Select-Metrics $MetricNames '^vllm:(prefix_cache|external_prefix_cache)')
    [string[]]$vllmKv = @(Select-Metrics $MetricNames '^vllm:.*kv_cache|^vllm:.*cache_usage|^vllm:gpu_cache_usage_perc|^vllm:cpu_cache_usage_perc')
    [string[]]$vllmQueue = @(Select-Metrics $MetricNames '^vllm:num_requests_(running|waiting|swapped)|^vllm:request_(success|failure|queue)')
    [string[]]$vllmLatency = @(Select-Metrics $MetricNames '^vllm:.*(time_to_first_token|inter_token|e2e_request_latency|request_latency|iteration_tokens)')
    [string[]]$vllmSpec = @(Select-Metrics $MetricNames '^vllm:spec_decode')
    [string[]]$sglang = @(Select-Metrics $MetricNames '^(sglang:|sglang_)')
    [string[]]$llamaCpp = @(Select-Metrics $MetricNames '(?i)(llama|slot|prompt|kv|cache|token)')

    $engineGuess = 'unknown'
    if ($vllmPrefix.Count -gt 0 -or $vllmKv.Count -gt 0 -or $vllmQueue.Count -gt 0) {
        $engineGuess = 'vllm'
    } elseif ($sglang.Count -gt 0) {
        $engineGuess = 'sglang'
    } elseif ($llamaCpp.Count -gt 0) {
        $engineGuess = 'llama.cpp-or-gguf'
    }

    return [pscustomobject]@{
        engineGuess = $engineGuess
        exposedMetricCount = $MetricNames.Count
        families = [pscustomobject]@{
            vllmPrefixCache = [pscustomobject]@{ present = ($vllmPrefix.Count -gt 0); names = $vllmPrefix }
            vllmKvCache = [pscustomobject]@{ present = ($vllmKv.Count -gt 0); names = $vllmKv }
            vllmQueue = [pscustomobject]@{ present = ($vllmQueue.Count -gt 0); names = $vllmQueue }
            vllmLatency = [pscustomobject]@{ present = ($vllmLatency.Count -gt 0); names = $vllmLatency }
            vllmSpeculativeDecoding = [pscustomobject]@{ present = ($vllmSpec.Count -gt 0); names = $vllmSpec }
            sglang = [pscustomobject]@{ present = ($sglang.Count -gt 0); names = $sglang }
            ggufOrLlamaCpp = [pscustomobject]@{ present = ($llamaCpp.Count -gt 0); names = $llamaCpp }
        }
    }
}

function Get-Verdict {
    param(
        [bool]$ModelsOk,
        [int]$ModelCount,
        [bool]$MetricsOk,
        [int]$MetricCount
    )
    if (-not $ModelsOk) { return 'blocked' }
    if ($ModelCount -eq 0) { return 'partial' }
    if (-not $MetricsOk -or $MetricCount -eq 0) { return 'partial' }
    return 'ready'
}

function New-NextActions {
    param(
        [string]$Verdict,
        [bool]$ModelsOk,
        [int]$ModelCount,
        [bool]$MetricsOk,
        [int]$MetricCount,
        [object]$MetricSummary
    )

    $actions = New-Object System.Collections.Generic.List[string]
    if (-not $ModelsOk) {
        $actions.Add('Fix the OpenAI-compatible model catalog endpoint first: /v1/models must respond before PalLLM can trust the lane.')
    } elseif ($ModelCount -eq 0) {
        $actions.Add('The model catalog responded but returned no model ids; load or alias the served model before wiring PalLLM.')
    }

    if (-not $MetricsOk) {
        $actions.Add('Expose Prometheus metrics on the model server. For llama.cpp use --metrics; for vLLM/SGLang keep /metrics loopback or auth-protected.')
    } elseif ($MetricCount -eq 0) {
        $actions.Add('The metrics endpoint returned no metric samples; send a short replay or check server metrics configuration.')
    }

    if ($MetricSummary.engineGuess -eq 'vllm') {
        if (-not $MetricSummary.families.vllmPrefixCache.present) {
            $actions.Add('vLLM metrics are present but prefix-cache metrics were not seen; run repeated-prefix replay before claiming cache reuse.')
        }
        if (-not $MetricSummary.families.vllmKvCache.present) {
            $actions.Add('vLLM metrics are present but KV-cache pressure metrics were not seen; confirm the server version and /metrics configuration.')
        }
    }

    if ($actions.Count -eq 0 -and $Verdict -eq 'ready') {
        $actions.Add('Archive this JSON beside latency replay evidence before promoting cache, speculation, or multimodal settings.')
    }
    return @($actions)
}

$normalizedBaseUrl = Normalize-Url $BaseUrl
$serviceRoot = Get-ServiceRoot $normalizedBaseUrl
$modelsUrl = Get-ModelsUrl $normalizedBaseUrl
if ([string]::IsNullOrWhiteSpace($MetricsUrl)) {
    $MetricsUrl = "$serviceRoot/metrics"
} else {
    $MetricsUrl = Normalize-Url $MetricsUrl
}
$healthUrl = "$serviceRoot/health"

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$artifactPath = Join-Path $OutputDir "model-probe-$stamp.json"

if ($DryRun.IsPresent) {
    $metricSummary = New-MetricSummary @(
        'vllm:prefix_cache_queries',
        'vllm:prefix_cache_hits',
        'vllm:kv_cache_usage_perc',
        'vllm:num_requests_running',
        'vllm:num_requests_waiting',
        'vllm:spec_decode_draft_acceptance_rate'
    )
    $artifact = [pscustomobject]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
        dryRun = $true
        baseUrl = $normalizedBaseUrl
        serviceRoot = $serviceRoot
        urls = [pscustomobject]@{ health = $healthUrl; models = $modelsUrl; metrics = $MetricsUrl }
        endpoints = [pscustomobject]@{
            health = [pscustomobject]@{ ok = $true; statusCode = 200; latencyMs = 0; contentLength = 0; error = $null }
            models = [pscustomobject]@{ ok = $true; statusCode = 200; latencyMs = 0; contentLength = 64; error = $null }
            metrics = [pscustomobject]@{ ok = $true; statusCode = 200; latencyMs = 0; contentLength = 256; error = $null }
        }
        modelCatalog = [pscustomobject]@{ count = 1; ids = @('dry-run-model') }
        metrics = $metricSummary
        verdict = 'dry-run'
        nextActions = @('DryRun only: rerun without -DryRun against the model endpoint to collect real evidence.')
        privacy = 'No chat, image, audio, tool-call, or player payload content was sent or stored.'
    }
} else {
    $healthProbe = Invoke-ProbeGet -Name 'health' -Url $healthUrl -TimeoutSeconds $TimeoutSec
    $modelsProbe = Invoke-ProbeGet -Name 'models' -Url $modelsUrl -TimeoutSeconds $TimeoutSec
    $metricsProbe = Invoke-ProbeGet -Name 'metrics' -Url $MetricsUrl -TimeoutSeconds $TimeoutSec

    $modelIds = Get-ModelIds $modelsProbe.body
    $metricNames = Get-MetricNames $metricsProbe.body
    $metricSummary = New-MetricSummary $metricNames
    $verdict = Get-Verdict -ModelsOk $modelsProbe.ok -ModelCount $modelIds.Count -MetricsOk $metricsProbe.ok -MetricCount $metricNames.Count
    $nextActions = New-NextActions `
        -Verdict $verdict `
        -ModelsOk $modelsProbe.ok `
        -ModelCount $modelIds.Count `
        -MetricsOk $metricsProbe.ok `
        -MetricCount $metricNames.Count `
        -MetricSummary $metricSummary

    $artifact = [pscustomobject]@{
        schemaVersion = 1
        generatedAtUtc = (Get-Date).ToUniversalTime().ToString('O')
        dryRun = $false
        baseUrl = $normalizedBaseUrl
        serviceRoot = $serviceRoot
        urls = [pscustomobject]@{ health = $healthUrl; models = $modelsUrl; metrics = $MetricsUrl }
        endpoints = [pscustomobject]@{
            health = [pscustomobject]@{ ok = $healthProbe.ok; statusCode = $healthProbe.statusCode; latencyMs = $healthProbe.latencyMs; contentLength = $healthProbe.contentLength; error = $healthProbe.error }
            models = [pscustomobject]@{ ok = $modelsProbe.ok; statusCode = $modelsProbe.statusCode; latencyMs = $modelsProbe.latencyMs; contentLength = $modelsProbe.contentLength; error = $modelsProbe.error }
            metrics = [pscustomobject]@{ ok = $metricsProbe.ok; statusCode = $metricsProbe.statusCode; latencyMs = $metricsProbe.latencyMs; contentLength = $metricsProbe.contentLength; error = $metricsProbe.error }
        }
        modelCatalog = [pscustomobject]@{ count = $modelIds.Count; ids = @($modelIds | Select-Object -First 20) }
        metrics = $metricSummary
        verdict = $verdict
        nextActions = $nextActions
        privacy = 'No chat, image, audio, tool-call, or player payload content was sent or stored.'
    }
}

$jsonText = $artifact | ConvertTo-Json -Depth 12
Set-Content -LiteralPath $artifactPath -Value $jsonText -Encoding UTF8

if ($Json.IsPresent) {
    $jsonText
} else {
    Write-Host ""
    Write-Host "PalLLM model endpoint probe" -ForegroundColor Cyan
    Write-Host ""
    Write-Host ("Base URL  : {0}" -f $artifact.baseUrl)
    Write-Host ("Models    : {0} ({1})" -f $artifact.endpoints.models.ok, $artifact.modelCatalog.count)
    Write-Host ("Metrics   : {0} ({1} names)" -f $artifact.endpoints.metrics.ok, $artifact.metrics.exposedMetricCount)
    Write-Host ("Engine    : {0}" -f $artifact.metrics.engineGuess)
    Write-Host ("Verdict   : {0}" -f $artifact.verdict)
    Write-Host ("Artifact  : {0}" -f $artifactPath)
    Write-Host ""
    Write-Host "Next:" -ForegroundColor White
    foreach ($action in @($artifact.nextActions)) {
        Write-Host ("  - {0}" -f $action)
    }
    Write-Host ""
}

if ($RequireReady.IsPresent -and $artifact.verdict -ne 'ready') {
    exit 2
}
