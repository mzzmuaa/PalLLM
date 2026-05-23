<#
.SYNOPSIS
    Print and optionally wire a local llama.cpp llama-server lane.

.DESCRIPTION
    llama-server exposes GGUF models through OpenAI-compatible /v1 endpoints
    and can also serve multimodal GGUF lanes through libmtmd and a matching
    mmproj. This helper keeps that path PalLLM-shaped: loopback-first,
    measured-context-first, metrics-enabled for proof, and explicit about
    which flags are proof lanes rather than player defaults.

    The script does not install llama.cpp or download model weights. It prints
    copy-paste setup commands and, with -WriteConfig, updates appsettings.json
    so the next sidecar restart points at the selected local server.

.PARAMETER LlamaCppUrl
    llama-server root. Default http://localhost:8080. PalLLM consumes the
    OpenAI-compatible BaseUrl http://localhost:8080/v1/.

.PARAMETER Model
    API model id / alias PalLLM should send in chat requests. If omitted, the
    script uses the first id from /v1/models when reachable, otherwise it
    suggests and wires the stable alias "pal-llamacpp".

.PARAMETER ModelPath
    Local GGUF model path used in the printed llama-server command.

.PARAMETER HfRepo
    Optional Hugging Face repo spec for llama-server -hf. When set, the printed
    command uses -hf instead of -m. Keep promotion evidence pinned to a
    concrete artifact or recorded model hash before publishing a recipe.

.PARAMETER Mmproj
    Optional multimodal projector GGUF path. Use with -WireVision only after
    proving the model/projector pair on Palworld screenshots.

.PARAMETER ContextSize
    Total context allocated across llama-server slots. With -Parallel 4 and
    -ContextSize 16384, each slot effectively gets about 4096 tokens.

.PARAMETER Parallel
    llama-server parallel slots. Keep 1 for the default single-player lane.

.PARAMETER BatchSize
    Prompt-processing batch size for the printed command.

.PARAMETER UBatchSize
    Physical microbatch size for the printed command.

.PARAMETER GpuLayers
    Number of layers to offload to GPU. Default 99, which is the common
    "offload as much as possible" llama.cpp recipe.

.PARAMETER CacheReuse
    Minimum chunk size for llama-server KV shifting reuse. 0 disables it.

.PARAMETER CacheRamMiB
    Optional llama.cpp cache RAM limit in MiB, emitted as -cram.

.PARAMETER SlotPromptSimilarity
    Slot prompt similarity threshold, emitted as -sps.

.PARAMETER SleepIdleSeconds
    Optional idle sleep setting. 0 leaves it off; positive values emit
    --sleep-idle-seconds and should be tested for wake latency.

.PARAMETER FlashAttn
    Flash Attention setting. Defaults to `auto` (llama-server picks
    based on tensor-core availability). Switched from `on` in Pass 347
    after the April-2026 stream_k_fixup kernel crash report on RTX 5090
    Blackwell (Xid 43 after b8680). Set to `on` to force enable, `off`
    to disable, or leave on `auto`.

.PARAMETER EnableThinking
    Toggle Qwen3 reasoning blocks. Defaults to $false (suppressed) to
    match PalLLM's `PalLLM:Inference:EnableThinking=false` shipping
    config. Emits `--chat-template-kwargs '{"enable_thinking":<bool>}'`.

.PARAMETER SpecType
    Optional llama.cpp speculative decoding proof lane. Defaults to none.
    Net-negative on RTX 3090 + Qwen3.6-35B-A3B (post PR #19493 benchmark);
    net-positive on RTX PRO 6000 / RTX 5090 / Apple M3 Max / Strix Halo.
    Measure cold/warm replay on your own hardware before enabling.
    NOTE (Pass 348): Qwen3-Coder-Next currently errors with "speculative
    decoding not supported by this context" (upstream issue #21886);
    leave at none for that model.

.PARAMETER ModelProfile
    Per-model Unsloth canonical sampler profile. Defaults to qwen36 to
    match PalLLM's shipping config. Values:
      qwen36       - Qwen 3.6 (temp 0.7, top-p 0.8, top-k 20, min-p 0, pp 1.5)
      qwen3-coder  - Qwen 3 Coder Next (temp 0.6, top-p 0.95, top-k 20, pp 0)
      minimax      - MiniMax M2.7 (temp 1.0, top-p 0.95, top-k 40, min-p 0.01)
      gemma        - Gemma 4 family (temp 0.7, top-p 0.95, top-k 20)
      deepseek     - DeepSeek V4 Flash (temp 0.7, top-p 0.95, top-k 40)
      generic      - No sampler override; llama-server defaults apply.

.PARAMETER Threads
    Number of generation threads. Default 0 = llama-server picks. For
    GPU-offload lanes, `1` is the documented optimum (+43% per Ventus
    Servers 2026 tuning).

.PARAMETER ThreadsBatch
    Prompt-processing thread count. Default 0 = match --threads.

.PARAMETER Prio
    Worker-thread priority. 0 = normal, 3 = high. MiniMax M2.7 recipe
    recommends `--prio 3`; everything else stays at 0.

.PARAMETER Mlock
    Emit `--mlock` (lock weights in RAM, no swap). Useful on Apple
    Silicon with memory headroom; thrashes on tight-memory hosts.

.PARAMETER NoMmap
    Emit `--no-mmap` (skip mmap; explicit host cache). Pair with
    `-CacheRamMiB` for the low-latency lane recipe.

.PARAMETER TensorSplit
    Comma-separated multi-GPU VRAM ratios (e.g. "2,1" for a 24 GB +
    12 GB pair). Off by default; single-GPU lanes don't need it.

.PARAMETER SplitMode
    Multi-GPU split mode: layer (default), row, or graph. `graph` is
    tensor parallelism at the GGML-graph level — 3-4x on dual Blackwell
    vs the default layer-split, per upstream multi-gpu.md.

.PARAMETER DraftModelPath
    Optional draft GGUF path for draft-simple speculation. draft-mtp normally
    uses model-native MTP heads; pass this only when the exact llama.cpp build
    and artifact recipe require an external draft model.

.PARAMETER QuantizedKv
    Emit the proof-only -ctk q8_0 -ctv q8_0 KV-cache compression flags.

.PARAMETER WireVision
    Also point PalLLM:Vision at this server. Keep off unless the selected GGUF
    model and mmproj were qualified as a Palworld screenshot lane.

.PARAMETER WriteConfig
    Update appsettings.json's PalLLM:Inference block. Default off.

.PARAMETER ConfigPath
    Override which appsettings.json to update.

.PARAMETER DryRun
    Print the planned config delta without writing.

.EXAMPLE
    pwsh ./scripts/connect-llamacpp.ps1 -ModelPath C:\Models\qwen.gguf

.EXAMPLE
    pwsh ./scripts/connect-llamacpp.ps1 -HfRepo ggml-org/gemma-3-4b-it-GGUF -WireVision

.EXAMPLE
    pwsh ./scripts/connect-llamacpp.ps1 -Model pal-gguf -WriteConfig

.NOTES
    Verb shortcut: pal connect llamacpp
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$LlamaCppUrl = 'http://localhost:8080',

    [string]$Model,

    [string]$ModelPath = '<model.gguf>',

    [string]$HfRepo,

    [string]$Mmproj,

    [int]$ContextSize = 8192,

    [int]$Parallel = 1,

    [int]$BatchSize = 512,

    [int]$UBatchSize = 256,

    [int]$GpuLayers = 99,

    [int]$CacheReuse = 256,

    [int]$CacheRamMiB = 0,

    [double]$SlotPromptSimilarity = 0.10,

    [int]$SleepIdleSeconds = 0,

    [ValidateSet('auto', 'on', 'off')]
    [string]$FlashAttn = 'auto',

    [bool]$EnableThinking = $false,

    [ValidateSet('none', 'draft-simple', 'draft-mtp', 'ngram-cache', 'ngram-simple', 'ngram-map-k', 'ngram-map-k4v', 'ngram-mod', 'draft')]
    [string]$SpecType = 'none',

    [int]$DraftMin = 48,

    [int]$DraftMax = 64,

    [int]$SpecNgramSize = 24,

    [string]$DraftModelPath = '<draft-model.gguf>',

    [switch]$QuantizedKv,

    # Pass 348: per-model sampler profile. Defaults to qwen36 to match
    # PalLLM's shipping Qwen3.6 quality tier. Other profiles match the
    # Unsloth-documented per-model canonical samplers.
    [ValidateSet('qwen36', 'qwen3-coder', 'minimax', 'gemma', 'deepseek', 'generic')]
    [string]$ModelProfile = 'qwen36',

    # Pass 348: thread / priority / lock perf knobs. Defaults are the
    # GPU-offload-friendly values (Ventus Servers 2026 tuning: 1 thread
    # is +43% on GPU lanes vs system-default).
    [int]$Threads = 0,           # 0 = let llama-server pick its default
    [int]$ThreadsBatch = 0,      # 0 = match --threads
    [int]$Prio = 0,              # 0..3 (normal..high); MiniMax recommends 3
    [switch]$Mlock,              # Lock weights in RAM (no swap)
    [switch]$NoMmap,             # Skip mmap (pair with --cache-ram)

    # Pass 348: multi-GPU tensor-split + split-mode-graph.
    [string]$TensorSplit,        # e.g. "2,1" for asymmetric dual-GPU VRAM
    [ValidateSet('layer', 'row', 'graph', 'none')]
    [string]$SplitMode = 'none',

    # Pass 350: MoE partial-CPU offload. When > 0, emits --n-cpu-moe N
    # so deeper layers' expert FFN tensors live in RAM instead of VRAM.
    # Required to run Qwen3.6-35B-A3B / Qwen3-Coder-Next / MiniMax-M2.7
    # on consumer cards (12-16 GB VRAM range). Source: David Sanftenberg
    # Medium guide + Doctor-Shotgun HF blog. Pass 350 install-llama-cpp.ps1
    # computes a sensible default per detected VRAM; this flag is the
    # manual override.
    [int]$NCpuMoe = 0,

    # Pass 350: regex tensor override (--override-tensor / -ot) for
    # finer-grained MoE control than --n-cpu-moe. Example:
    # '\.ffn_.*_exps\.weight=CPU' offloads ALL expert FFN tensors.
    [string]$OverrideTensor,

    [switch]$WireVision,

    [switch]$WriteConfig,

    [string]$ConfigPath,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Get-DefaultConfigPath {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(
        (Join-Path $repoRoot 'sidecar/publish/appsettings.json')
        (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM/appsettings.json')
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }
    return $candidates[2]
}

function ConvertTo-MutableConfig {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($key in $Value.Keys) { $copy[$key] = ConvertTo-MutableConfig $Value[$key] }
        return $copy
    }
    if ($Value -is [psobject] -and $Value.PSObject.Properties.Count -gt 0 -and -not ($Value -is [string])) {
        $copy = [ordered]@{}
        foreach ($prop in $Value.PSObject.Properties) {
            $copy[$prop.Name] = ConvertTo-MutableConfig $prop.Value
        }
        return $copy
    }
    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $list = New-Object System.Collections.ArrayList
        foreach ($item in $Value) { [void]$list.Add((ConvertTo-MutableConfig $item)) }
        return $list.ToArray()
    }
    return $Value
}

function Test-ConfigKey {
    param($Container, [string]$Key)
    if ($null -eq $Container) { return $false }
    if ($Container -is [System.Collections.IDictionary]) {
        return $Container.Contains($Key)
    }
    return $false
}

function Ensure-ObjectKey {
    param(
        [System.Collections.IDictionary]$Container,
        [string]$Key
    )
    if (-not (Test-ConfigKey $Container $Key) -or -not ($Container[$Key] -is [System.Collections.IDictionary])) {
        $Container[$Key] = [ordered]@{}
    }
    return $Container[$Key]
}

function Read-Config {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
    }

    try {
        $raw = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
        }
        return ConvertTo-MutableConfig ($raw | ConvertFrom-Json -ErrorAction Stop)
    } catch {
        Write-Warning "[connect-llamacpp] Existing config at $Path is not valid JSON: $_"
        Write-Warning "[connect-llamacpp] Refusing to overwrite. Fix the JSON first or pass -ConfigPath to a different file."
        exit 1
    }
}

function Normalize-LlamaCppBaseUrl {
    param([string]$Url)
    $trimmed = $Url.Trim().TrimEnd('/')
    if ($trimmed.EndsWith('/v1', [System.StringComparison]::OrdinalIgnoreCase)) {
        return "$trimmed/"
    }
    return "$trimmed/v1/"
}

function Get-ServiceRootFromBaseUrl {
    param([string]$BaseUrl)
    $trimmed = $BaseUrl.TrimEnd('/')
    if ($trimmed.EndsWith('/v1', [System.StringComparison]::OrdinalIgnoreCase)) {
        return $trimmed.Substring(0, $trimmed.Length - 3)
    }
    return $trimmed
}

function Get-LlamaCppModelIds {
    param([string]$BaseUrl)
    try {
        $models = Invoke-RestMethod -Uri "$($BaseUrl.TrimEnd('/'))/models" -Method Get -TimeoutSec 5 -ErrorAction Stop
    } catch {
        return $null
    }

    if ($null -eq $models -or $null -eq $models.data) {
        return @()
    }

    return @($models.data | ForEach-Object {
        if ($_.id) { [string]$_.id }
    } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-LlamaCppHealth {
    param([string]$ServiceRoot)
    try {
        $health = Invoke-RestMethod -Uri "$($ServiceRoot.TrimEnd('/'))/health" -Method Get -TimeoutSec 5 -ErrorAction Stop
        if ($health.status) { return [string]$health.status }
        return 'reachable'
    } catch {
        return $null
    }
}

function Format-CommandArgument {
    param([string]$Value)
    if ($Value -match '^[A-Za-z0-9_\-./:=<>]+$') {
        return $Value
    }
    return '"' + ($Value -replace '"', '\"') + '"'
}

function Join-CommandLine {
    param([string[]]$CommandArgs)
    return (($CommandArgs | ForEach-Object { Format-CommandArgument $_ }) -join ' ')
}

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-DefaultConfigPath
}
$ConfigPath = [IO.Path]::GetFullPath($ConfigPath)

$baseUrl = Normalize-LlamaCppBaseUrl $LlamaCppUrl
$serviceRoot = Get-ServiceRootFromBaseUrl $baseUrl
$serviceUri = [Uri]$serviceRoot
$port = if ($serviceUri.IsDefaultPort) { 8080 } else { $serviceUri.Port }
$discoveredModels = Get-LlamaCppModelIds -BaseUrl $baseUrl
$healthStatus = Test-LlamaCppHealth -ServiceRoot $serviceRoot

$stableAlias = 'pal-llamacpp'
$resolvedModel = $Model
if ([string]::IsNullOrWhiteSpace($resolvedModel) -and $null -ne $discoveredModels -and $discoveredModels.Count -gt 0) {
    $resolvedModel = $discoveredModels[0]
}
if ([string]::IsNullOrWhiteSpace($resolvedModel)) {
    $resolvedModel = $stableAlias
}

$commandModelAlias = if ([string]::IsNullOrWhiteSpace($Model)) { $stableAlias } else { $Model }

$serverArgs = @('llama-server')
if (-not [string]::IsNullOrWhiteSpace($HfRepo)) {
    $serverArgs += @('-hf', $HfRepo)
} else {
    $serverArgs += @('-m', $ModelPath)
}
$serverArgs += @('-a', $commandModelAlias)
$serverArgs += @('--host', '127.0.0.1', '--port', [string]$port)
$serverArgs += @('-c', [string]$ContextSize, '-np', [string]([math]::Max(1, $Parallel)))
$serverArgs += @('-b', [string]([math]::Max(1, $BatchSize)), '-ub', [string]([math]::Max(1, $UBatchSize)))
$serverArgs += @('-ngl', [string]([math]::Max(0, $GpuLayers)), '--flash-attn', $FlashAttn)
$serverArgs += @('--cache-prompt', '--cache-reuse', [string]([math]::Max(0, $CacheReuse)))
$serverArgs += @('-sps', $SlotPromptSimilarity.ToString([Globalization.CultureInfo]::InvariantCulture))
$serverArgs += @('--metrics', '--no-webui')
# Pass 347: Unsloth canonical Qwen3.6 thinking-toggle. Emits
# --chat-template-kwargs '{"enable_thinking":false}' by default to match
# PalLLM:Inference:EnableThinking=false in shipping appsettings.json.
# Pass 348: Only emit for Qwen profiles. MiniMax / Gemma / DeepSeek
# don't use the enable_thinking template kwarg.
if ($ModelProfile -in @('qwen36', 'qwen3-coder')) {
    $thinkingValue = if ($EnableThinking) { 'true' } else { 'false' }
    $serverArgs += @('--chat-template-kwargs', "{`"enable_thinking`":$thinkingValue}")
}

# Pass 347/348: Per-model Unsloth canonical sampler. PalLLM's shipping
# appsettings carries the Qwen3.6 profile; the connect script flips to
# the right per-model profile when the operator selects -ModelProfile.
# Sources: unsloth.ai/docs/models/qwen3.6 (Qwen),
#          unsloth.ai/docs/models/tutorials/minimax-m27 (MiniMax),
#          unsloth.ai/docs/models/qwen3-coder-next (Qwen3-Coder-Next).
switch ($ModelProfile) {
    'qwen36' {
        if (-not $EnableThinking) {
            $serverArgs += @('--temp', '0.7', '--top-p', '0.8', '--top-k', '20', '--min-p', '0.0', '--presence-penalty', '1.5')
        } else {
            $serverArgs += @('--temp', '1.0', '--top-p', '0.95', '--top-k', '20', '--min-p', '0.0', '--presence-penalty', '1.5')
        }
    }
    'qwen3-coder' {
        # Unsloth Qwen3-Coder-Next: coding-tuned, lower temp, no presence penalty.
        $serverArgs += @('--temp', '0.6', '--top-p', '0.95', '--top-k', '20', '--min-p', '0.0', '--presence-penalty', '0.0')
    }
    'minimax' {
        # Unsloth MiniMax-M2.7: higher temp, top-k 40, min-p 0.01.
        $serverArgs += @('--temp', '1.0', '--top-p', '0.95', '--top-k', '40', '--min-p', '0.01')
    }
    'gemma' {
        # Gemma family typically uses OpenAI-ish defaults; PalLLM's
        # shipping config has the Qwen profile, so when on Gemma the
        # operator usually wants to override.
        $serverArgs += @('--temp', '0.7', '--top-p', '0.95', '--top-k', '20', '--min-p', '0.0')
    }
    'deepseek' {
        $serverArgs += @('--temp', '0.7', '--top-p', '0.95', '--top-k', '40', '--min-p', '0.0')
    }
    'generic' {
        # No sampler override; llama-server defaults apply.
    }
}

# Pass 348: thread / priority / mlock / no-mmap perf knobs.
if ($Threads -gt 0) {
    $serverArgs += @('--threads', [string]$Threads)
}
if ($ThreadsBatch -gt 0) {
    $serverArgs += @('--threads-batch', [string]$ThreadsBatch)
}
if ($Prio -gt 0) {
    $serverArgs += @('--prio', [string]$Prio)
}
if ($Mlock.IsPresent) {
    $serverArgs += '--mlock'
}
if ($NoMmap.IsPresent) {
    $serverArgs += '--no-mmap'
}

# Pass 348: multi-GPU tensor split + split-mode.
if (-not [string]::IsNullOrWhiteSpace($TensorSplit)) {
    $serverArgs += @('--tensor-split', $TensorSplit)
}
if ($SplitMode -ne 'none') {
    $serverArgs += @('--split-mode', $SplitMode)
}

# Pass 350: MoE partial offload. The deepest N layers' expert FFN
# tensors are placed on CPU/RAM instead of VRAM. Pairs naturally with
# the curated MoE families (Qwen3.6-35B-A3B, Qwen3-Coder-Next, MiniMax)
# to run on consumer GPUs.
if ($NCpuMoe -gt 0) {
    $serverArgs += @('--n-cpu-moe', [string]$NCpuMoe)
}
if (-not [string]::IsNullOrWhiteSpace($OverrideTensor)) {
    $serverArgs += @('--override-tensor', $OverrideTensor)
}
if ($CacheRamMiB -gt 0) {
    $serverArgs += @('-cram', [string]$CacheRamMiB)
}
if ($SleepIdleSeconds -gt 0) {
    $serverArgs += @('--sleep-idle-seconds', [string]$SleepIdleSeconds)
}
if (-not [string]::IsNullOrWhiteSpace($Mmproj)) {
    $serverArgs += @('--mmproj', $Mmproj)
}
if ($QuantizedKv.IsPresent) {
    $serverArgs += @('-ctk', 'q8_0', '-ctv', 'q8_0')
}
if ($SpecType -ne 'none') {
    $effectiveSpecType = if ($SpecType -eq 'draft') { 'draft-simple' } else { $SpecType }
    $serverArgs += @('--spec-type', $effectiveSpecType)
    if ($effectiveSpecType -eq 'draft-simple') {
        $serverArgs += @('--spec-draft-model', $DraftModelPath, '--spec-draft-n-min', [string]$DraftMin, '--spec-draft-n-max', [string]$DraftMax)
    } elseif ($effectiveSpecType -eq 'draft-mtp') {
        if ($PSBoundParameters.ContainsKey('DraftModelPath')) {
            $serverArgs += @('--spec-draft-model', $DraftModelPath)
        }
        $serverArgs += @('--spec-draft-n-min', [string]$DraftMin, '--spec-draft-n-max', [string]$DraftMax)
    } elseif ($effectiveSpecType -eq 'ngram-mod') {
        $serverArgs += @('--spec-ngram-mod-n-match', [string]$SpecNgramSize, '--spec-ngram-mod-n-min', [string]$DraftMin, '--spec-ngram-mod-n-max', [string]$DraftMax)
    } else {
        $serverArgs += @('--spec-draft-n-min', [string]$DraftMin, '--spec-draft-n-max', [string]$DraftMax)
        if ($effectiveSpecType -eq 'ngram-simple') {
            $serverArgs += @('--spec-ngram-simple-size-n', [string]$SpecNgramSize)
        } elseif ($effectiveSpecType -eq 'ngram-map-k') {
            $serverArgs += @('--spec-ngram-map-k-size-n', [string]$SpecNgramSize)
        } elseif ($effectiveSpecType -eq 'ngram-map-k4v') {
            $serverArgs += @('--spec-ngram-map-k4v-size-n', [string]$SpecNgramSize)
        }
    }
}

$setupCommands = @(
    (Join-CommandLine $serverArgs)
)

Write-Host ""
Write-Host "PalLLM <- llama.cpp llama-server" -ForegroundColor Cyan
Write-Host ""
Write-Host ("Server root   : {0}" -f $serviceRoot) -ForegroundColor Green
Write-Host ("BaseUrl       : {0}" -f $baseUrl)
Write-Host ("Model / alias : {0}" -f $resolvedModel)
Write-Host ("Wire vision   : {0}" -f $WireVision.IsPresent)
if ($null -eq $healthStatus) {
    Write-Host "Health        : /health unreachable right now" -ForegroundColor Yellow
} else {
    Write-Host ("Health        : {0}" -f $healthStatus)
}
if ($null -eq $discoveredModels) {
    Write-Host "Probe         : /v1/models unreachable right now" -ForegroundColor Yellow
} elseif ($discoveredModels.Count -eq 0) {
    Write-Host "Probe         : /v1/models reachable but no model ids were reported" -ForegroundColor Yellow
} else {
    Write-Host ("Probe         : {0} model(s): {1}" -f $discoveredModels.Count, ($discoveredModels -join ', '))
}
Write-Host ""
Write-Host "Copy-paste setup:" -ForegroundColor White
Write-Host ""
foreach ($command in $setupCommands) {
    Write-Host $command
}
Write-Host ""
Write-Host "After it boots, verify:" -ForegroundColor White
Write-Host ("  curl {0}/health" -f $serviceRoot)
Write-Host ("  curl {0}/v1/models" -f $serviceRoot)
Write-Host ("  curl {0}/metrics" -f $serviceRoot)
Write-Host ("  curl {0}/v1/chat/completions -H ""Content-Type: application/json"" -d '{{""model"":""{1}"",""messages"":[{{""role"":""user"",""content"":""hi""}}],""max_tokens"":32}}'" -f $serviceRoot, $resolvedModel)
Write-Host ""
Write-Host "Promotion guardrails:" -ForegroundColor White
Write-Host "  - Keep --host 127.0.0.1 for the default player lane; add --api-key and a trusted proxy before exposing it."
Write-Host "  - Record /health, /v1/models, /metrics, slot state or logs, p50/p95, exact JSON/tool-call parse success, and fallback proof."
Write-Host "  - Treat -ctk/-ctv, --spec-type, --spec-draft-*, --sleep-idle-seconds, router mode, and mmproj vision as separate proof lanes."
Write-Host "  - Review model licenses before redistributing any model files; PalLLM should ship config, not weights."
Write-Host ""

if (-not $WriteConfig.IsPresent) {
    Write-Host "(Run again with -Model <api-id> -WriteConfig to wire PalLLM's appsettings.json.)" -ForegroundColor DarkGray
    Write-Host ""
    [pscustomobject]@{
        DryRun = $DryRun.IsPresent
        WroteConfig = $false
        Model = $resolvedModel
        BaseUrl = $baseUrl
        WireVision = $WireVision.IsPresent
        ProbeReachable = $null -ne $discoveredModels
        DiscoveredModels = $discoveredModels
        HealthStatus = $healthStatus
        SetupCommands = $setupCommands
    } | Write-Output
    return
}

$config = Read-Config -Path $ConfigPath
if (-not ($config -is [System.Collections.IDictionary])) {
    $config = [ordered]@{ PalLLM = [ordered]@{ Inference = [ordered]@{} } }
}

$pal = Ensure-ObjectKey -Container $config -Key 'PalLLM'
$inference = Ensure-ObjectKey -Container $pal -Key 'Inference'

$priorBaseUrl = if (Test-ConfigKey $inference 'BaseUrl') { [string]$inference['BaseUrl'] } else { '' }
$priorModel = if (Test-ConfigKey $inference 'Model') { [string]$inference['Model'] } else { '' }
$priorEnabled = if (Test-ConfigKey $inference 'Enabled') { [bool]$inference['Enabled'] } else { $false }
$priorResidencyProvider = if (Test-ConfigKey $inference 'ResidencyProvider') { [string]$inference['ResidencyProvider'] } else { '' }
$priorResidencyTtl = if (Test-ConfigKey $inference 'ResidencyTtlSeconds') { [int]$inference['ResidencyTtlSeconds'] } else { 0 }

$inference['BaseUrl'] = $baseUrl
$inference['Model'] = $resolvedModel
$inference['Enabled'] = $true
$inference['ResidencyProvider'] = 'Disabled'
$inference['ResidencyTtlSeconds'] = 0

$delta = @()
if ($priorBaseUrl -ne $baseUrl) { $delta += "  Inference.BaseUrl             : $priorBaseUrl -> $baseUrl" }
if ($priorModel -ne $resolvedModel) { $delta += "  Inference.Model               : $priorModel -> $resolvedModel" }
if ($priorEnabled -ne $true) { $delta += "  Inference.Enabled             : $priorEnabled -> True" }
if ($priorResidencyProvider -ne 'Disabled') { $delta += "  Inference.ResidencyProvider   : $priorResidencyProvider -> Disabled" }
if ($priorResidencyTtl -ne 0) { $delta += "  Inference.ResidencyTtlSeconds : $priorResidencyTtl -> 0" }

# Pass 352: Per-family Unsloth canonical sampler now propagates into
# PalLLM.Inference's Temperature/TopP/TopK/MinP/PresencePenalty when
# -ModelProfile is explicitly set. Without this, PalLLM keeps sending
# Qwen3.6 sampler values even when the loaded model is MiniMax / Gemma
# / DeepSeek -- PalLLM's per-request sampler overrides llama-server's
# defaults, so the wrong-family sampler silently mis-samples the
# response. Profiles match Get-SamplerFlags in install-llama-cpp.ps1.
if ($PSBoundParameters.ContainsKey('ModelProfile')) {
    $samplerSnapshot = switch ($ModelProfile) {
        'qwen36'      { @{ Temperature = 0.7; TopP = 0.8;  TopK = 20; MinP = 0.0;  PresencePenalty = 1.5 } }
        'qwen3-coder' { @{ Temperature = 0.6; TopP = 0.95; TopK = 20; MinP = 0.0;  PresencePenalty = 0.0 } }
        'minimax'     { @{ Temperature = 1.0; TopP = 0.95; TopK = 40; MinP = 0.01; PresencePenalty = $null } }
        'gemma'       { @{ Temperature = 0.7; TopP = 0.95; TopK = 20; MinP = 0.0;  PresencePenalty = $null } }
        'deepseek'    { @{ Temperature = 0.7; TopP = 0.95; TopK = 40; MinP = 0.0;  PresencePenalty = $null } }
        default       { $null }
    }
    if ($null -ne $samplerSnapshot) {
        foreach ($field in 'Temperature','TopP','TopK','MinP','PresencePenalty') {
            $prior = if (Test-ConfigKey $inference $field) { $inference[$field] } else { $null }
            $next = $samplerSnapshot[$field]
            if ($null -ne $next -and $prior -ne $next) {
                $inference[$field] = $next
                $delta += "  Inference.$field".PadRight(36) + ": $prior -> $next"
            }
        }
    }
}

if ($WireVision.IsPresent) {
    $vision = Ensure-ObjectKey -Container $pal -Key 'Vision'
    $priorVisionBaseUrl = if (Test-ConfigKey $vision 'BaseUrl') { [string]$vision['BaseUrl'] } else { '' }
    $priorVisionModel = if (Test-ConfigKey $vision 'Model') { [string]$vision['Model'] } else { '' }
    $priorVisionEnabled = if (Test-ConfigKey $vision 'Enabled') { [bool]$vision['Enabled'] } else { $false }

    $vision['BaseUrl'] = $baseUrl
    $vision['Model'] = $resolvedModel
    $vision['Enabled'] = $true

    if ($priorVisionBaseUrl -ne $baseUrl) { $delta += "  Vision.BaseUrl                : $priorVisionBaseUrl -> $baseUrl" }
    if ($priorVisionModel -ne $resolvedModel) { $delta += "  Vision.Model                  : $priorVisionModel -> $resolvedModel" }
    if ($priorVisionEnabled -ne $true) { $delta += "  Vision.Enabled                : $priorVisionEnabled -> True" }
}

Write-Host "Config target : $ConfigPath" -ForegroundColor White
if ($delta.Count -eq 0) {
    Write-Host "  (no changes - config already matches the recipe)" -ForegroundColor DarkGray
} else {
    Write-Host "Planned changes:" -ForegroundColor White
    $delta | ForEach-Object { Write-Host $_ }
}
Write-Host ""

if ($DryRun.IsPresent) {
    Write-Host "[DryRun] No file changes." -ForegroundColor Yellow
    Write-Host ($config | ConvertTo-Json -Depth 12)
    return
}

if ($delta.Count -eq 0) {
    Write-Host "Config unchanged. No write needed." -ForegroundColor Green
    return
}

if (-not $PSCmdlet.ShouldProcess($ConfigPath, "Wire PalLLM inference -> llama.cpp ($resolvedModel)")) {
    return
}

$parent = Split-Path -Parent $ConfigPath
if (-not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
if ((Test-Path -LiteralPath $ConfigPath) -and -not (Test-Path -LiteralPath "$ConfigPath.bak")) {
    Copy-Item -LiteralPath $ConfigPath -Destination "$ConfigPath.bak" -Force
    Write-Host "Backed up existing config to $ConfigPath.bak" -ForegroundColor DarkGray
}

$config | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8

Write-Host "Wrote $ConfigPath" -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:" -ForegroundColor White
Write-Host "  1. Start llama-server with one of the commands above."
Write-Host "  2. Confirm /health and /v1/models list $resolvedModel."
Write-Host "  3. Restart PalLLM: pal play"
Write-Host "  4. Probe chat: pal hello"
Write-Host ""

[pscustomobject]@{
    DryRun = $false
    WroteConfig = $true
    Model = $resolvedModel
    BaseUrl = $baseUrl
    WireVision = $WireVision.IsPresent
    ResidencyProvider = 'Disabled'
    Backup = "$ConfigPath.bak"
} | Write-Output
