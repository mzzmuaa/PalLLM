<#
.SYNOPSIS
    Print the live model-serving checklist from the sidecar's model
    collaboration contract.

.DESCRIPTION
    Reads GET /api/inference/collaboration and projects each configured model
    lane's Capability.ServingProfile into an operator-readable checklist.
    This is the "what do I need to boot/check on the model server?" companion
    to pal connect llamacpp / pal connect lmstudio / pal connect vllm / pal connect omni /
    pal connect transformers / pal connect tensorrt / pal connect openvino /
    pal connect foundry.
    It includes startup, request, cache, admission, security, promotion-receipt,
    metric-receipt, verification, and runtime-guard sections for each lane, including
    model-artifact provenance receipts before promotion or redistribution.

    The command is read-only. It never writes appsettings.json and it does not
    start a model server.

.PARAMETER BaseUrl
    Sidecar base URL. Defaults to http://localhost:5088.

.PARAMETER ModelId
    Optional substring filter for a configured model id or tier id.

.PARAMETER VramGb
    Optional hardware override passed through to the collaboration endpoint.

.PARAMETER RamGb
    Optional hardware override passed through to the collaboration endpoint.

.PARAMETER PreferParallel
    Optional hardware override passed through to the collaboration endpoint.

.PARAMETER Json
    Emit a machine-readable JSON projection instead of text.

.EXAMPLE
    pwsh ./scripts/pal-model-serving.ps1

.EXAMPLE
    pwsh ./scripts/pal-model-serving.ps1 -ModelId qwen -VramGb 48 -RamGb 128 -PreferParallel

.NOTES
    Verb shortcut: pal models serving
#>
[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://localhost:5088',

    [string]$ModelId,

    [double]$VramGb,

    [double]$RamGb,

    [switch]$PreferParallel,

    [switch]$Json
)

$ErrorActionPreference = 'Stop'

function Join-QueryString {
    $pairs = @()
    if ($PSBoundParameters.ContainsKey('VramGb')) {
        $pairs += 'vramGb={0}' -f $VramGb.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    if ($PSBoundParameters.ContainsKey('RamGb')) {
        $pairs += 'ramGb={0}' -f $RamGb.ToString([Globalization.CultureInfo]::InvariantCulture)
    }
    if ($PreferParallel.IsPresent) {
        $pairs += 'preferParallel=true'
    }
    if ($pairs.Count -eq 0) {
        return ''
    }
    return '?' + ($pairs -join '&')
}

function Format-ListValue {
    param($Value)
    $items = @($Value) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    if ($items.Count -eq 0) {
        return '(none)'
    }
    return ($items -join ', ')
}

function Write-Section {
    param(
        [string]$Title,
        $Items
    )
    $values = @($Items) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
    if ($values.Count -eq 0) {
        return
    }
    Write-Host ("  {0}:" -f $Title) -ForegroundColor White
    foreach ($item in $values) {
        Write-Host ("    - {0}" -f $item)
    }
}

$base = $BaseUrl.TrimEnd('/')
$url = "$base/api/inference/collaboration$(Join-QueryString)"

try {
    $snapshot = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec 10 -ErrorAction Stop
} catch {
    if ($Json.IsPresent) {
        [pscustomobject]@{
            ok = $false
            url = $url
            error = 'sidecar_unreachable'
            message = 'Could not read /api/inference/collaboration.'
            next = 'Start the sidecar with pal play or pal run, then rerun pal models serving.'
        } | ConvertTo-Json -Depth 8
    } else {
        Write-Host ""
        Write-Host "PalLLM model serving profiles" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Could not reach $url" -ForegroundColor Red
        Write-Host ""
        Write-Host "Try this next:" -ForegroundColor Yellow
        Write-Host "  pal play       # boot sidecar + dashboard"
        Write-Host "  pal run        # boot sidecar in this terminal"
        Write-Host ""
    }
    exit 1
}

$models = @($snapshot.ConfiguredModels)
if (-not [string]::IsNullOrWhiteSpace($ModelId)) {
    $needle = $ModelId.ToLowerInvariant()
    $models = @($models | Where-Object {
        ([string]$_.ModelId).ToLowerInvariant().Contains($needle) -or
        ([string]$_.TierId).ToLowerInvariant().Contains($needle)
    })
}

if ($models.Count -eq 0) {
    if ($Json.IsPresent) {
        [pscustomobject]@{
            ok = $false
            url = $url
            error = 'model_not_found'
            filter = $ModelId
            availableModels = @($snapshot.ConfiguredModels | ForEach-Object { $_.ModelId })
        } | ConvertTo-Json -Depth 8
    } else {
        Write-Host ""
        Write-Host "No configured model matched '$ModelId'." -ForegroundColor Yellow
        Write-Host "Available models:" -ForegroundColor White
        foreach ($model in @($snapshot.ConfiguredModels)) {
            Write-Host ("  - {0}" -f $model.ModelId)
        }
        Write-Host ""
    }
    exit 2
}

$projection = [pscustomobject]@{
    ok = $true
    generatedAtUtc = $snapshot.GeneratedAtUtc
    source = $url
    hardware = $snapshot.Hardware
    activeModel = $snapshot.ActiveModel
    models = @($models | ForEach-Object {
        $capability = $_.Capability
        $serving = $capability.ServingProfile
        [pscustomobject]@{
            modelId = $_.ModelId
            tierId = $_.TierId
            priority = $_.Priority
            isActive = $_.IsActive
            family = $capability.Family
            recommendedBackend = $capability.RecommendedBackend
            inputModalities = $capability.InputModalities
            outputModalities = $capability.OutputModalities
            supportsVisionInput = $capability.SupportsVisionInput
            supportsVideoInput = $capability.SupportsVideoInput
            supportsAudioInput = $capability.SupportsAudioInput
            supportsAudioOutput = $capability.SupportsAudioOutput
            supportsStructuredOutputs = $capability.SupportsStructuredOutputs
            supportsToolCalls = $capability.SupportsToolCalls
            supportsSpeculativeDecoding = $capability.SupportsSpeculativeDecoding
            speculation = $capability.Speculation
            servingProfile = $serving
            servingOptimizations = $capability.ServingOptimizations
            runtimeGuards = $capability.RuntimeGuards
            promotionReceipts = $serving.PromotionReceipts
            metricReceipts = $serving.MetricReceipts
            verificationChecks = $serving.VerificationChecks
        }
    })
}

if ($Json.IsPresent) {
    $projection | ConvertTo-Json -Depth 12
    return
}

Write-Host ""
Write-Host "PalLLM model serving profiles" -ForegroundColor Cyan
Write-Host ""
Write-Host ("Source       : {0}" -f $url)
if ($snapshot.Hardware) {
    Write-Host ("Hardware     : {0} VRAM GB / {1} RAM GB / preferParallel={2}" -f `
        $snapshot.Hardware.VramGb,
        $snapshot.Hardware.RamGb,
        $snapshot.Hardware.PreferParallel)
}
Write-Host ("Active model : {0}" -f $snapshot.ActiveModel)
Write-Host ""

foreach ($model in $projection.models) {
    $serving = $model.servingProfile
    $active = if ($model.isActive) { 'active' } else { 'standby' }
    $tier = if ([string]::IsNullOrWhiteSpace($model.tierId)) { 'no-tier' } else { $model.tierId }

    Write-Host ("[{0}] {1} ({2})" -f $active, $model.modelId, $tier) -ForegroundColor Green
    Write-Host ("  Family/runtime : {0} via {1}" -f $model.family, $serving.PreferredRuntime)
    Write-Host ("  Backend/proto  : {0}; {1}" -f $model.recommendedBackend, $serving.RequestProtocol)
    Write-Host ("  Profile id     : {0}" -f $serving.ProfileId)
    Write-Host ("  Modalities     : in=[{0}] out=[{1}]" -f `
        (Format-ListValue $model.inputModalities),
        (Format-ListValue $model.outputModalities))
    Write-Host ("  Fit flags      : vision={0} video={1} audioIn={2} audioOut={3} structured={4} tools={5} speculative={6}" -f `
        $model.supportsVisionInput,
        $model.supportsVideoInput,
        $model.supportsAudioInput,
        $model.supportsAudioOutput,
        $model.supportsStructuredOutputs,
        $model.supportsToolCalls,
        $model.supportsSpeculativeDecoding)
    if ($model.speculation) {
        Write-Host ("  Speculation    : first={0}; ngram={1}; draft={2}; mtp={3}; isolatedProof={4}" -f `
            $model.speculation.RecommendedFirstMode,
            $model.speculation.SupportsNgramSpeculation,
            $model.speculation.SupportsDraftModelSpeculation,
            $model.speculation.SupportsModelNativeMtp,
            $model.speculation.RequiresModalityIsolatedProof)
    }

    Write-Section 'Startup hints' $serving.StartupHints
    Write-Section 'Request hints' $serving.RequestHints
    Write-Section 'Cache hints' $serving.CacheHints
    Write-Section 'Admission controls' $serving.AdmissionControls
    Write-Section 'Security controls' $serving.SecurityControls
    Write-Section 'Promotion receipts' $model.promotionReceipts
    Write-Section 'Metric receipts' $model.metricReceipts
    Write-Section 'Verification checks' $model.verificationChecks
    Write-Section 'Runtime guards' $model.runtimeGuards
    Write-Host ""
}

Write-Host "Related commands:" -ForegroundColor White
Write-Host "  pal connect vllm      # print and optionally wire a text/vision vLLM recipe"
Write-Host "  pal connect llamacpp  # print and optionally wire a raw llama.cpp GGUF recipe"
Write-Host "  pal connect lmstudio  # print and optionally wire a local LM Studio recipe"
Write-Host "  pal connect omni      # print and optionally wire the multimodal-in recipe"
Write-Host "  pal connect transformers # print and optionally wire a transformers serve recipe"
Write-Host "  pal connect tensorrt  # print and optionally wire a TensorRT-LLM /v1 recipe"
Write-Host "  pal connect openvino  # print and optionally wire an OpenVINO Model Server /v3 recipe"
Write-Host "  pal connect foundry   # print and optionally wire a Foundry Local / Windows ML recipe"
Write-Host "  pal models serving -Json | ConvertFrom-Json  # feed this to another tool"
Write-Host ""
