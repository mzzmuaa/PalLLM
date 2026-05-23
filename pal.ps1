<#
.SYNOPSIS
    PalLLM verb-driven task runner. One memorable command for every
    common operation in this repo.

.DESCRIPTION
    Wraps the most-used scripts and dotnet invocations behind a
    single PowerShell entrypoint. Inspired by `make`, `just`, and
    `npm run`. Saves contributors (human or agent) from memorizing
    the location and flag set of every individual script.

.PARAMETER Verb
    What to do. One of: build, test, audit, fast-audit, cleanup, run, play,
    onboard, doctor, smoke, workflow-pins, publish-audit,
    aot-readiness, openapi, package, recover, list, help.

    Run `pal list` to see the full table with descriptions.

.PARAMETER Args
    Extra arguments forwarded to the underlying script. Only some
    verbs accept them — see `pal help <verb>` or the script source
    for the per-verb forwarding rules.

.EXAMPLE
    pwsh ./pal.ps1 build
    # Equivalent to: dotnet build PalLLM.sln --configuration Release --nologo

.EXAMPLE
    pwsh ./pal.ps1 test
    # Equivalent to: dotnet test  PalLLM.sln --configuration Release --nologo --verbosity quiet

.EXAMPLE
    pwsh ./pal.ps1 fast-audit
    # Equivalent to: scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging

.EXAMPLE
    pwsh ./pal.ps1 audit
    # Full audit: build + tests + every drift gate. Roughly 30 s.

.EXAMPLE
    pwsh ./pal.ps1 onboard
    # First-time contributor flow: SDK check + build + test + audit + dashboard.

.EXAMPLE
    pwsh ./pal.ps1 play
    # Boots the sidecar in a window and opens the Field Console dashboard.

.NOTES
    All verbs run from the repo root regardless of the working
    directory you call this from. The script resolves $PSScriptRoot
    and uses absolute paths internally.

    See `docs/CHEAT_SHEET.md` for the full quick reference.
    See `docs/INDEX.md` for the doc map.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Verb = "help",

    # Renamed off the automatic $Args name so verb functions can read
    # the script-level forwarded args. PowerShell sets a per-function
    # $Args automatically (the function's own args); using a different
    # name keeps that scope rule from masking the script-level value.
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs = @()
)

$ErrorActionPreference = "Stop"

$repoRoot = $PSScriptRoot
$slnPath  = Join-Path $repoRoot "PalLLM.sln"

# -- Verb dispatch table --------------------------------------------
# Each verb is a small function. Keep them tight; deeper logic
# belongs in the per-script file under scripts/.

function Run-Help {
    Write-Host ""
    Write-Host "PalLLM task runner -- one command for every common operation." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  Usage: pwsh ./pal.ps1 <verb> [args]"
    Write-Host ""
    Write-Host "  Brand-new here?  Try:  pwsh ./pal.ps1 onboard" -ForegroundColor Green
    Write-Host "  Player install?  Try:  double-click play.bat" -ForegroundColor Green
    Run-List
    Write-Host "Common combinations:" -ForegroundColor White
    Write-Host "  pwsh ./pal.ps1 onboard           # first-time contributor: SDK + build + test + audit + dashboard"
    Write-Host "  pwsh ./pal.ps1 fast-audit        # quick 16-gate drift check (no rebuild, no tests)"
    Write-Host "  pwsh ./pal.ps1 cleanup           # preview generated artifact cleanup; add -Apply to delete"
    Write-Host "  pwsh ./pal.ps1 audit             # full validation: build + tests + every drift gate"
    Write-Host "  pwsh ./pal.ps1 play              # boot sidecar + open dashboard at http://localhost:5088"
    Write-Host "  pwsh ./pal.ps1 status            # one-line state readout"
    Write-Host "  pwsh ./pal.ps1 uninstall -DryRun # preview a clean uninstall without changing anything"
    Write-Host ""
    Write-Host "All verbs forward extra args to the underlying script - e.g."
    Write-Host "  pwsh ./pal.ps1 play -PalworldPath 'D:\SteamLibrary\steamapps\common\Palworld'"
    Write-Host ""
}

function Run-List {
    # Verbs are grouped by intent so a first-time reader sees structure
    # rather than a flat alphabetical list. Order within each group is
    # ordered by typical usage frequency.
    $groups = @(
        @{
            Title = 'Set up'
            Tagline = 'Get from a fresh clone or release zip to a working PalLLM.'
            Verbs = @(
                @{ Verb = 'next';      Description = 'the "what should I do right now?" advisor (probes state, recommends ONE action)' }
                @{ Verb = 'complete';  Description = 'queue-by-queue completion dashboard (full Q1-Q6 arc + autonomous baseline)' }
                @{ Verb = 'welcome';   Description = 'first-run guided 60-second tour for "you just installed this; what now?"' }
                @{ Verb = 'onboard';   Description = 'first-time setup -- SDK check + build + test + audit + dashboard' }
                @{ Verb = 'play';      Description = 'one-click: install mod + boot sidecar + open dashboard + launch game' }
                @{ Verb = 'uninstall'; Description = 'cleanly remove the mod (preserves chat history; -Full to wipe everything)' }
            )
        }
        @{
            Title = 'Develop'
            Tagline = 'Day-to-day code + test + audit loop.'
            Verbs = @(
                @{ Verb = 'build';      Description = 'dotnet build (Release)' }
                @{ Verb = 'test';       Description = 'dotnet test  (Release, quiet) -- expects 1313 / 1313' }
                @{ Verb = 'audit';      Description = 'full drift audit -- build + tests + 16 gates (~30 s)' }
                @{ Verb = 'fast-audit'; Description = 'drift gates only -- skip coverage / SBOM / packaging' }
                @{ Verb = 'cleanup';    Description = 'preview/remove generated audit coverage and build outputs (-Apply to delete)' }
                @{ Verb = 'scaffold';   Description = 'scaffold placeholders for a new advisor / builder / validator / feeder / fallback / mcp-tool' }
                @{ Verb = 'run';        Description = 'dotnet run the sidecar (foreground; use play for a windowed sidecar + dashboard)' }
            )
        }
        @{
            Title = 'Operate'
            Tagline = 'Health checks, recovery, and live diagnostics.'
            Verbs = @(
                @{ Verb = 'hello';         Description = 'one-shot probe: send "hi" to a running sidecar and print the reply' }
                @{ Verb = 'demo';          Description = '30-second self-running tour across six fallback families (live or canned)' }
                @{ Verb = 'campfire';      Description = 'chat with your companion outside the game (REPL; works offline; /whisper /fortune /quest /tale inside)' }
                @{ Verb = 'fortune';       Description = 'in-character daily fortune (date-seeded; same all day, different next day)' }
                @{ Verb = 'whisper';       Description = 'one quiet ambient one-liner from the companion (random; no fanfare)' }
                @{ Verb = 'quest';         Description = 'a small ~30-min self-contained challenge (-Tier easy / medium / spicy / quiet)' }
                @{ Verb = 'tale';          Description = 'a 3-4-line campfire story (random or -Title <prefix>)' }
                @{ Verb = 'patrol-report'; Description = 'the night the companion spent watching while you slept (4-6 lines)' }
                @{ Verb = 'pack';          Description = 'manage personality packs (''pal pack list'' / ''pal pack copy <name>'' / ''pal pack new'')' }
                @{ Verb = 'doctor';        Description = 'environment + smoke + delivery-replay diagnostics' }
                @{ Verb = 'smoke';         Description = 'sidecar smoke test against a running sidecar' }
                @{ Verb = 'replay';        Description = 'replay a captured delivery envelope through the live bridge loop' }
                @{ Verb = 'native-proof';  Description = 'live-Palworld watcher that polls /api/bridge/proof until delivery_proven and persists a durable artifact' }
                @{ Verb = 'hud-bind';      Description = 'apply the ranked HUD bind recommendation into config/native-hud.lua' }
                @{ Verb = 'proof-bundle';  Description = 'package the bridge proof + smoke + native-proof + HUD config into one validation bundle' }
                @{ Verb = 'recover';       Description = 'archive runtime root + start clean (last-resort recovery)' }
                @{ Verb = 'status';        Description = 'one-line current-state check (counts + latest audit)' }
                @{ Verb = 'models';        Description = 'recommended model + quantization; ''pal models serving'' policy; ''pal models probe'' endpoint evidence' }
                @{ Verb = 'config';        Description = 'open / show / wizard appsettings.json (try ''pal config wizard'')' }
                @{ Verb = 'benchmark';     Description = 'real-world latency measurement vs HOT_PATH.md per-tier budget' }
                @{ Verb = 'mcp';           Description = 'wire PalLLM into an MCP client (claude-desktop / vscode / cursor)' }
                @{ Verb = 'install-llama-cpp'; Description = 'download + verify the bundled llama-server release into runtime-root/Bundled/llama.cpp/ (the "bundled and default" engine)' }
                @{ Verb = 'connect';       Description = 'wire PalLLM''s inference path to a local engine (llamacpp default / vllm high-config / lmstudio / omni / transformers / tensorrt / openvino / foundry)' }
                @{ Verb = 'support';       Description = 'export an anonymized support bundle (privacy-redacted JSON + zip)' }
                @{ Verb = 'health';        Description = 'write one Markdown + JSON health snapshot from local evidence' }
                @{ Verb = 'proof';         Description = 'summarize live/native proof status and the exact next action' }
                @{ Verb = 'logs';          Description = 'recent activity: launch evidence + native artifacts + latest audit (lighter than support)' }
                @{ Verb = 'preflight';     Description = 'single-command readiness checklist: READY / NEARLY READY / NOT READY verdict' }
                @{ Verb = 'check-updates'; Description = 'check GitHub Releases for a newer PalLLM version (opt-in network call)' }
                @{ Verb = 'news';          Description = 'print the most recent CHANGELOG entry (offline; pairs with check-updates)' }
            )
        }
        @{
            Title = 'Publish'
            Tagline = 'Release packaging + supply-chain checks.'
            Verbs = @(
                @{ Verb = 'package';       Description = 'build the release zip under release/ (PowerShell 7+ recommended)' }
                @{ Verb = 'verify';        Description = 'verify a packaged release zip / dir: required files, hashes, publish flags, publication-surface hygiene' }
                @{ Verb = 'publish-audit'; Description = 'local publication preflight: copy, path, action-pin, and notice coverage checks' }
                @{ Verb = 'aot-readiness'; Description = 'local AOT/trim readiness scan; -PublishProbe runs an explicit native publish experiment' }
                @{ Verb = 'openapi';       Description = 'regenerate the docs/openapi/palllm-sidecar-v1.json snapshot' }
                @{ Verb = 'workflow-pins'; Description = 'verify GitHub Actions use full-SHA action pins' }
            )
        }
        @{
            Title = 'Inspect'
            Tagline = 'Programmatic readouts of the project state. Designed for agent use - every output is structured.'
            Verbs = @(
                @{ Verb = 'context';   Description = 'JSON snapshot for AI agent consumption (counts + ADRs + schemas + freshness)' }
                @{ Verb = 'harvest';   Description = 'list / show harvestable units (capabilities to lift into another project)' }
                @{ Verb = 'explain';   Description = 'structured explanation of a file or directory: kind, surface, deps, related docs/tests' }
                @{ Verb = 'where';     Description = 'natural-language query -> ranked file paths (e.g. ''pal where chat hot path'')' }
                @{ Verb = 'readiness'; Description = 'candid 10/10 scorecard per aspect (matches docs/READINESS.md)' }
                @{ Verb = 'list';      Description = 'this table' }
                @{ Verb = 'help';      Description = 'help text + the table' }
            )
        }
    )

    # Compute a single column width across every group so the descriptions
    # align even between groups.
    $maxVerbWidth = ($groups | ForEach-Object { $_.Verbs } |
        ForEach-Object { $_.Verb.Length } | Measure-Object -Maximum).Maximum

    foreach ($group in $groups) {
        Write-Host ""
        Write-Host ("== {0} ==" -f $group.Title) -ForegroundColor Cyan
        Write-Host ("   {0}" -f $group.Tagline) -ForegroundColor DarkGray
        foreach ($v in $group.Verbs) {
            $padded = $v.Verb.PadRight($maxVerbWidth)
            Write-Host ("   {0}  -- {1}" -f $padded, $v.Description)
        }
    }
    Write-Host ""
}

function Run-Build {
    & dotnet build $slnPath --configuration Release --nologo @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Test {
    & dotnet test $slnPath --configuration Release --nologo --verbosity quiet @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Audit {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/run_full_audit.ps1") `
        -SkipCoverage -SkipSbom -SkipPackaging @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-FastAudit {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/run_full_audit.ps1") `
        -SkipCoverage -SkipSbom -SkipPackaging -SkipTests @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Cleanup {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/pal-cleanup.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Run {
    $sidecarProj = Join-Path $repoRoot "src/PalLLM.Sidecar/PalLLM.Sidecar.csproj"
    & dotnet run --configuration Release --project $sidecarProj @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Play {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/play-palllm.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Onboard {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/onboard.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Doctor {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/doctor.ps1") -RunSmoke -RunDeliveryReplay @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Smoke {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/run-sidecar-smoke.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-NativeProof {
    # Live-operator companion to `pal proof`. `pal proof` is read-only (summarises
    # current state); `pal native-proof` is the active watcher that polls the
    # live Palworld bridge until delivery_proven and persists a durable
    # `latest-native-proof.json` artifact. Required for IMPLEMENTATION_QUEUE Queue 1.
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/run-native-proof.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Replay {
    # Replays a captured delivery envelope against the running sidecar so an
    # operator can validate the full inbox -> outbox -> delivery -> feedback
    # loop without standing up a live Palworld session. Pairs with `pal smoke`
    # for full Queue 1 loop coverage.
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/run-delivery-replay.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-ProofBundle {
    # Packages the current bridge proof, smoke artifact, native-proof artifact,
    # and HUD config into one durable validation bundle. Required for
    # IMPLEMENTATION_QUEUE Queue 1 (release-friendly evidence export).
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/export-release-proof-bundle.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-HudBind {
    # Writes the ranked HUD bind recommendation into config/native-hud.lua so
    # the installed mod consumes the operator-confirmed widget seam without
    # hand-editing main.lua. Required for IMPLEMENTATION_QUEUE Queue 3 (native
    # delivery layer V2).
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/apply-hud-bind-recommendation.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Verify {
    # Validates a packaged PalLLM release zip / expanded directory: required
    # files, manifest-declared hashes, sidecar publish flags, package shape,
    # publication-surface hygiene. Required for IMPLEMENTATION_QUEUE Queue 6
    # (clean-machine release proof).
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/verify-release-package.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Complete {
    # Live "are we 100% complete?" dashboard. Read-only consolidation of
    # ROADMAP.md, IMPLEMENTATION_QUEUE.md, READINESS.md, and the live bridge
    # proof into one queue-by-queue status board. Distinct from `pal next`
    # (single immediate action) and `pal proof` (Queue 1 lanes only).
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/pal-complete.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-WorkflowPins {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/audit-workflow-action-pins.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-OpenApi {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/export-openapi.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Package {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/package-release.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-PublishAudit {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/publish-audit.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-AotReadiness {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/aot-readiness.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Recover {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/recover-palllm.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Uninstall {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/uninstall-mod.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Context {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/agent-context.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Scaffold {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot "scripts/scaffold.ps1") @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Status {
    # One-line state check. Reads docs/PROJECT_NUMBERS.json (the
    # machine-readable source of truth for rolling counts) and the
    # latest audit report. Designed to be fast (<1 s) and grep-able.
    $numbersPath = Join-Path $repoRoot "docs/PROJECT_NUMBERS.json"
    $auditDir    = Join-Path $repoRoot "artifacts/full-audit"

    if (Test-Path $numbersPath) {
        try {
            $numbers = Get-Content $numbersPath -Raw | ConvertFrom-Json
            Write-Host ""
            Write-Host "PalLLM rolling baseline:" -ForegroundColor Cyan
            Write-Host ("  tests           : {0}" -f $numbers.tests)
            Write-Host ("  drift gates     : {0}" -f $numbers.driftGates)
            Write-Host ("  build warnings  : {0}" -f $numbers.buildWarnings)
            Write-Host ("  /api routes     : {0}" -f $numbers.apiRoutes)
            Write-Host ("  feature catalog : {0} (ready={1} / scaffolded={2} / deferred={3})" -f `
                $numbers.featureCatalog, $numbers.featureReady, $numbers.featureScaffolded, $numbers.featureDeferred)
            Write-Host ("  MCP tools       : {0}" -f $numbers.mcpTools)
            Write-Host ("  fallback strats : {0}" -f $numbers.fallbackStrategies)
            Write-Host ("  ADRs accepted   : {0}" -f $numbers.adrsAccepted)
            Write-Host ("  docs (root+docs): {0}" -f $numbers.docsCount)
            Write-Host ("  honest roadmap  : {0}" -f $numbers.honestRoadmap)
            Write-Host ("  baseline date   : {0}" -f $numbers.baselineDate)
        } catch {
            Write-Host "  (could not parse $numbersPath -- file may be corrupt)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "  (docs/PROJECT_NUMBERS.json missing)" -ForegroundColor Yellow
    }

    if (Test-Path $auditDir) {
        $latest = Get-ChildItem $auditDir -Directory |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($latest) {
            $resultsFile = Join-Path $latest.FullName "RESULTS.md"
            if (Test-Path $resultsFile) {
                $overall = Select-String -Path $resultsFile -Pattern '^- Overall: \*\*(.+?)\*\*' |
                    Select-Object -First 1
                $overallText = if ($overall) { $overall.Matches.Groups[1].Value } else { 'UNKNOWN' }
                Write-Host ""
                Write-Host ("Latest audit: {0} ({1} UTC)" -f $overallText, $latest.Name) -ForegroundColor Cyan
                Write-Host ("  full report : {0}" -f $resultsFile)
            }
        }
    }

    # Live runtime suggestion summary. When the sidecar is reachable, count
    # active hints by severity so the operator sees critical state at a
    # glance without leaving `pal status`. Skipped entirely when the
    # sidecar is offline -- this verb stays fast (<1 s) and grep-able.
    try {
        $statusBaseUrl = if ($env:PALLLM_BASE_URL) { $env:PALLLM_BASE_URL.TrimEnd('/') } else { 'http://localhost:5088' }
        $statusHealth = Invoke-RestMethod -Uri "$statusBaseUrl/api/health" -Method Get -TimeoutSec 1 -ErrorAction Stop
        $statusSuggestions = if ($statusHealth.PSObject.Properties['Suggestions']) {
            @($statusHealth.Suggestions)
        } elseif ($statusHealth.PSObject.Properties['suggestions']) {
            @($statusHealth.suggestions)
        } else {
            @()
        }
        if ($statusSuggestions.Count -gt 0) {
            $statusUrgent = @($statusSuggestions | Where-Object { ($_.Severity -ieq 'urgent') -or ($_.severity -ieq 'urgent') }).Count
            $statusWarn   = @($statusSuggestions | Where-Object { ($_.Severity -ieq 'warn')   -or ($_.severity -ieq 'warn')   }).Count
            $statusInfo   = @($statusSuggestions | Where-Object { ($_.Severity -ieq 'info')   -or ($_.severity -ieq 'info')   }).Count
            $statusColor = if ($statusUrgent -gt 0) { 'Red' } elseif ($statusWarn -gt 0) { 'Yellow' } else { 'Cyan' }
            Write-Host ""
            Write-Host ("Live runtime: {0} suggestion(s) -- {1} urgent, {2} warn, {3} info" -f $statusSuggestions.Count, $statusUrgent, $statusWarn, $statusInfo) -ForegroundColor $statusColor
            Write-Host "  details : pwsh ./pal.ps1 next" -ForegroundColor DarkGray
        } else {
            Write-Host ""
            Write-Host "Live runtime: 0 suggestions (healthy)" -ForegroundColor Green
        }
    } catch {
        # Sidecar offline; nothing to add. The audit/baseline section
        # already gave the operator the static answer.
    }

    Write-Host ""
}

function Run-Demo {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/demo-pal.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Campfire {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-campfire.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Fortune {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-fortune.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Whisper {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-whisper.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Quest {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-quest.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Tale {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-tale.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Benchmark {
    # Pass 360: `pal benchmark cold-start` (and `coldstart`) routes to
    # scripts/pal-benchmark-coldstart.ps1 -- the one-shot operator UX
    # number measured against the docs/HOT_PATH.md "Cold-start" row.
    # `pal benchmark` (no subcommand) keeps its prior behaviour:
    # per-turn latency vs the per-tier latency budgets.
    $subArgs = @($script:ForwardArgs)
    if ($subArgs.Count -gt 0 -and $subArgs[0] -in @('cold-start', 'coldstart', 'cold')) {
        $rest = if ($subArgs.Count -gt 1) { $subArgs[1..($subArgs.Count - 1)] } else { @() }
        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $repoRoot 'scripts/pal-benchmark-coldstart.ps1') @rest
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        return
    }
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-benchmark.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Welcome {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-welcome.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Next {
    # The "intuitive lifeline" verb. Probes current state in ~1 s, picks the
    # single highest-impact gap, and prints ONE recommended action with the
    # exact command. Distinct from preflight (which lists all 12 checks)
    # and welcome (which is the interactive six-beat tour).
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-next.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-News {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-news.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Harvest {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-harvest.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Logs {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-logs.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Health {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-health.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Proof {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-proof.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Preflight {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-preflight.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-PatrolReport {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-patrol-report.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Pack {
    # Subcommand router: pal pack <subcommand>.
    if (-not $script:ForwardArgs -or $script:ForwardArgs.Count -eq 0) {
        Write-Host ""
        Write-Host "pal pack <subcommand> [args]" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Subcommands:"
        Write-Host "  list  list personality packs the running sidecar has loaded"
        Write-Host "  copy  copy a sample pack from samples/packs/ into the runtime pack dir"
        Write-Host "  new   scaffold a new personality pack with valid manifest + ContentHash"
        Write-Host ""
        Write-Host "Examples:" -ForegroundColor White
        Write-Host "  pal pack list"
        Write-Host "  pal pack copy companion-warrior"
        Write-Host "  pal pack new -Id companion-stoic -DisplayName ""Stoic"" -Author ""you"""
        Write-Host ""
        Write-Host "More: docs/PACK_SAMPLES.md  +  docs/PACK_AUTHORING.md" -ForegroundColor DarkGray
        Write-Host ""
        return
    }
    $sub = $script:ForwardArgs[0]
    $rest = if ($script:ForwardArgs.Count -gt 1) { $script:ForwardArgs[1..($script:ForwardArgs.Count - 1)] } else { @() }
    switch ($sub.ToLowerInvariant()) {
        'list' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/pal-pack-list.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'copy' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/pal-pack-copy.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'new' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/scaffold-pack.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        default {
            Write-Host ""
            Write-Host "Unknown 'pal pack' subcommand: $sub" -ForegroundColor Red
            Write-Host "Try: pal pack list   or   pal pack copy <name>   or   pal pack new" -ForegroundColor Yellow
            Write-Host ""
            exit 1
        }
    }
}

function Run-Where {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-where.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Explain {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/pal-explain.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Connect {
    # Subcommand router: pal connect <target>. Today: ollama, llamacpp,
    # vllm, omni, lmstudio, transformers, tensorrt, openvino, foundry.
    # Future: llama-cpp, openai-direct, etc.
    if (-not $script:ForwardArgs -or $script:ForwardArgs.Count -eq 0) {
        Write-Host ""
        Write-Host "pal connect <target> [args]" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Targets:"
        Write-Host "  ollama      probe Ollama, recommend a model for your hardware, write the config"
        Write-Host "  llamacpp    print or wire a raw llama.cpp llama-server GGUF lane"
        Write-Host "  lmstudio    wire LM Studio's local OpenAI-compatible server with TTL proof"
        Write-Host "  vllm        pick a Blackwell / Hopper / Ampere recipe + print the docker command"
        Write-Host "  omni        multimodal-in / audio-out (Gemma 4 / Gemma 3n / Qwen3-Omni) via vLLM-Omni"
        Write-Host "  transformers Hugging Face transformers serve with continuous batching"
        Write-Host "  tensorrt   NVIDIA TensorRT-LLM /v1 lane with health + metrics proof"
        Write-Host "  openvino   OpenVINO Model Server /v3 lane for Intel CPU, GPU, or NPU"
        Write-Host "  foundry     Microsoft Foundry Local / Windows ML REST lane"
        Write-Host ""
        Write-Host "Examples:" -ForegroundColor White
        Write-Host "  pal connect ollama                          # default localhost:11434, auto-recommend"
        Write-Host "  pal connect ollama -DryRun                  # preview the config delta"
        Write-Host "  pal connect ollama -Model qwen3:14b         # force a specific installed model"
        Write-Host "  pal connect llamacpp -ModelPath C:\Models\qwen.gguf # print / wire raw llama-server"
        Write-Host "  pal connect lmstudio -Model <loaded-id>     # print / wire LM Studio localhost:1234"
        Write-Host "  pal connect vllm -UseCase coding            # print the agentic-coding recipe"
        Write-Host "  pal connect vllm -WriteConfig               # also wire appsettings.json at vLLM"
        Write-Host "  pal connect omni                            # default omni-text-out profile"
        Write-Host "  pal connect omni -Profile omni-full         # multimodal in + audio out via realtime WS"
        Write-Host "  pal connect omni -WriteConfig               # wire Vision to vLLM-Omni; add -WireInference only after proof"
        Write-Host "  pal connect transformers -Revision <sha>    # print / wire transformers serve"
        Write-Host "  pal connect tensorrt -ToolCallParser qwen3  # print / wire TensorRT-LLM"
        Write-Host "  pal connect openvino -TargetDevice GPU      # print / wire OpenVINO Model Server"
        Write-Host "  pal connect foundry -FoundryEndpoint <url>  # print / wire Foundry Local"
        Write-Host ""
        Write-Host "More: docs/MULTIMODAL_RECIPES.md  +  docs/BLACKWELL_RECIPES.md" -ForegroundColor DarkGray
        Write-Host ""
        return
    }

    $sub = $script:ForwardArgs[0]
    $rest = if ($script:ForwardArgs.Count -gt 1) { $script:ForwardArgs[1..($script:ForwardArgs.Count - 1)] } else { @() }

    switch ($sub.ToLowerInvariant()) {
        'ollama' {
            Write-Host "  pal connect ollama is no longer supported." -ForegroundColor Yellow
            Write-Host "  Pass 339 removed the Ollama connector (the engine was heavier than llama.cpp"
            Write-Host "  for the same workload). Use the bundled llama.cpp instead:" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "      pwsh ./pal.ps1 install-llama-cpp"
            Write-Host "      pwsh ./pal.ps1 connect llamacpp -ModelPath <gguf> -WriteConfig"
            Write-Host ""
            Write-Host "  See docs/LOCAL_MODELS_INVENTORY.md for the operator-curated GGUF mapping."
            exit 2
        }
        'cloud' {
            # Pass 357: shipping escape path for below-reference hardware.
            # Routes to scripts/connect-cloud.ps1 (OpenAI-compatible
            # cloud-API connector). See docs/MINIMUM_REQUIREMENTS.md
            # § "Escape path #1: cloud API".
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-cloud.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'openai' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-cloud.ps1') -Provider openai @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'llamacpp' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-llamacpp.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'llama-cpp' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-llamacpp.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'llama.cpp' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-llamacpp.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'lmstudio' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-lmstudio.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'lm-studio' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-lmstudio.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'vllm' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-vllm.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'omni' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-vllm-omni.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'transformers' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-transformers.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'hf' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-transformers.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'tensorrt' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-tensorrt.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'trtllm' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-tensorrt.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'trt-llm' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-tensorrt.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'openvino' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-openvino.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'ovms' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-openvino.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'intel' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-openvino.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'foundry' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-foundry.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'foundry-local' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-foundry.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        'ms-foundry' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-foundry.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        default {
            Write-Host ""
            Write-Host "Unknown 'pal connect' target: $sub" -ForegroundColor Red
            Write-Host "Try: pal connect ollama   or   pal connect llamacpp   or   pal connect lmstudio   or   pal connect vllm   or   pal connect tensorrt   or   pal connect openvino   or   pal connect foundry" -ForegroundColor Yellow
            Write-Host ""
            exit 1
        }
    }
}

function Run-Support {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/export-support-bundle.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Mcp {
    # Subcommand router: pal mcp connect <client> [--dry-run | --api-key XYZ | --url ...].
    # Today only 'connect' is wired; future subcommands like 'disconnect',
    # 'list', 'test' could land here without changing the verb surface.
    if (-not $script:ForwardArgs -or $script:ForwardArgs.Count -eq 0) {
        Write-Host ""
        Write-Host "pal mcp <subcommand> [args]" -ForegroundColor Cyan
        Write-Host ""
        Write-Host "Subcommands:"
        Write-Host "  connect <client>   wire PalLLM into an MCP client (claude-desktop / vscode / cursor)"
        Write-Host ""
        Write-Host "Examples:" -ForegroundColor White
        Write-Host "  pal mcp connect                          # default: claude-desktop, localhost:5088/mcp"
        Write-Host "  pal mcp connect vscode                   # wire VS Code workspace .vscode/mcp.json"
        Write-Host "  pal mcp connect cursor -ApiKey xyz123    # wire Cursor with bearer auth"
        Write-Host "  pal mcp connect claude-desktop -DryRun   # preview without writing"
        Write-Host ""
        return
    }

    $sub = $script:ForwardArgs[0]
    $rest = if ($script:ForwardArgs.Count -gt 1) { $script:ForwardArgs[1..($script:ForwardArgs.Count - 1)] } else { @() }

    switch ($sub.ToLowerInvariant()) {
        'connect' {
            & powershell -NoProfile -ExecutionPolicy Bypass `
                -File (Join-Path $repoRoot 'scripts/connect-mcp-client.ps1') @rest
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
        default {
            Write-Host ""
            Write-Host "Unknown 'pal mcp' subcommand: $sub" -ForegroundColor Red
            Write-Host "Try: pal mcp connect" -ForegroundColor Yellow
            Write-Host ""
            exit 1
        }
    }
}

function Run-CheckUpdates {
    & powershell -NoProfile -ExecutionPolicy Bypass `
        -File (Join-Path $repoRoot 'scripts/check-updates.ps1') @script:ForwardArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

function Run-Readiness {
    # Programmatic version of docs/READINESS.md - prints the candid
    # 10/10 scorecard per aspect with the current state. Use to give a
    # quick "is it ready to run?" answer with honest numbers.
    Write-Host ""
    Write-Host "PalLLM readiness scorecard" -ForegroundColor Cyan
    Write-Host "Honest aggregate: ~8.0 / 10 across 23 aspects." -ForegroundColor White
    Write-Host "Full per-aspect breakdown: docs/READINESS.md" -ForegroundColor DarkGray
    Write-Host ""

    $scores = @(
        @{ Aspect = 'Privacy posture';                 Score = '10/10'; Color = 'Green'  }
        @{ Aspect = 'Security / supply chain';         Score = '10/10'; Color = 'Green'  }
        @{ Aspect = 'Performance (Blackwell+vLLM+Omni)'; Score = '9.8/10'; Color = 'Green'  }
        @{ Aspect = 'Uninstall (one-click + manifest)'; Score = '9.5/10'; Color = 'Green'  }
        @{ Aspect = 'Install (one-click play.bat)';    Score = '9.5/10'; Color = 'Green'  }
        @{ Aspect = 'Diagnose / troubleshoot + logs';  Score = '9.7/10'; Color = 'Green'  }
        @{ Aspect = 'Documentation (57 fresh docs)';   Score = '9/10';   Color = 'Green'  }
        @{ Aspect = 'First chat (with inference)';     Score = '9/10';   Color = 'Green'  }
        @{ Aspect = 'MCP client integration';          Score = '9/10';   Color = 'Green'  }
        @{ Aspect = 'Discovery (README + pitch)';      Score = '8/10';   Color = 'Yellow' }
        @{ Aspect = 'Update / re-install + news';      Score = '8.5/10'; Color = 'Green'  }
        @{ Aspect = 'Customize (packs+scaffold+list)';  Score = '8.5/10'; Color = 'Green'  }
        @{ Aspect = 'Fun (campfire+5 ritual catalogs)'; Score = '9.2/10'; Color = 'Green'  }
        @{ Aspect = 'Download / extract / signing';    Score = '7/10';   Color = 'Yellow' }
        @{ Aspect = 'First chat (deterministic only)'; Score = '7/10';   Color = 'Yellow' }
        @{ Aspect = 'Configuration UX (wizard+show)';  Score = '8/10';   Color = 'Yellow' }
        @{ Aspect = 'Performance (typical hardware)';  Score = '7.5/10'; Color = 'Yellow' }
        @{ Aspect = 'Polish (welcome+preflight)';      Score = '7.7/10'; Color = 'Yellow' }
        @{ Aspect = 'Cross-platform mod support';      Score = '6/10';   Color = 'Yellow' }
        @{ Aspect = 'Agent-native discoverability';    Score = '9.9/10'; Color = 'Green'  }
        @{ Aspect = 'In-game native HUD/audio/action'; Score = '5/10';   Color = 'Red'    }
        @{ Aspect = 'Community / share-ability';       Score = '4/10';   Color = 'Red'    }
        @{ Aspect = 'Localization (i18n)';             Score = '3/10';   Color = 'Red'    }
    )

    $maxAspectWidth = ($scores | ForEach-Object { $_.Aspect.Length } | Measure-Object -Maximum).Maximum
    foreach ($s in $scores) {
        $padded = $s.Aspect.PadRight($maxAspectWidth)
        Write-Host ("  {0}  {1}" -f $padded, $s.Score) -ForegroundColor $s.Color
    }

    Write-Host ""
    Write-Host "Per-audience verdict:" -ForegroundColor White
    Write-Host "  Casual Palworld player on average hardware    : 6-7 / 10 today"
    Write-Host "  Player on Blackwell hardware (5090 / B-series): 8 / 10 default, 10 / 10 once vLLM is wired"
    Write-Host "  Operator (sidecar + dashboard, no game)       : 9 / 10 today"
    Write-Host "  Coding agent / harvester                      : 9.5 / 10 today"
    Write-Host "  Linux / macOS user                            : 6 / 10 (sidecar yes, mod no)"
    Write-Host "  MCP client user                               : 8 / 10 today"
    Write-Host ""
    Write-Host "What's blocking 10/10 average:" -ForegroundColor White
    Write-Host "  In-game native HUD/audio/actions = 5/10 - the dominant gap."
    Write-Host "  Closing it requires live Palworld + UE4SS work tracked in"
    Write-Host "  docs/IMPLEMENTATION_QUEUE.md queues 3-5 (~12.5% of the"
    Write-Host "  honest 23.8% remaining roadmap)."
    Write-Host ""
    Write-Host "Closest single move that would lift the average to 9/10:" -ForegroundColor Green
    Write-Host "  Capture one live delivery_proven proof on the bridge proof"
    Write-Host "  endpoint - powershell -File scripts/run-native-proof.ps1"
    Write-Host ""
}

function Run-Hello {
    # One-command "did it actually work?" probe. Calls /api/chat against a
    # running sidecar (default localhost:5088) and prints the reply. No
    # configuration needed - the deterministic fallback always answers, so
    # this works even with inference disabled.
    $baseUrl = if ($script:ForwardArgs -and $script:ForwardArgs.Count -gt 0) { $script:ForwardArgs[0] } else { 'http://localhost:5088' }
    $url = "$($baseUrl.TrimEnd('/'))/api/chat"
    $body = @{ userMessage = 'hi'; characterId = 1 } | ConvertTo-Json -Compress

    Write-Host ""
    Write-Host "PalLLM hello probe -> $url" -ForegroundColor Cyan
    try {
        $response = Invoke-RestMethod -Uri $url -Method Post -ContentType 'application/json' `
            -Body $body -TimeoutSec 15 -ErrorAction Stop
    } catch {
        Write-Host ""
        Write-Host "Could not reach the sidecar:" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)"
        Write-Host ""
        Write-Host "Try one of these:" -ForegroundColor Yellow
        Write-Host "  pal play              # boot sidecar in a window + open dashboard"
        Write-Host "  pal run               # boot sidecar in this window (foreground)"
        Write-Host "  pal hello http://your-host:port  # different bind address"
        exit 1
    }

    Write-Host ""
    Write-Host "Reply:" -ForegroundColor White
    Write-Host "  $($response.assistantMessage)"
    Write-Host ""
    Write-Host "Diagnostics:" -ForegroundColor White
    Write-Host ("  ResponsePath    : {0}" -f $response.responsePath)
    if ($response.usedFallback) {
        Write-Host ("  UsedFallback    : true (strategy = {0})" -f $response.fallbackStrategy)
    } else {
        Write-Host  "  UsedFallback    : false (live inference completed)"
    }
    if ($response.taskKind)      { Write-Host ("  TaskKind        : {0}" -f $response.taskKind) }
    if ($response.characterName) { Write-Host ("  Character       : {0}" -f $response.characterName) }
    Write-Host ""
}

function Run-Models {
    if ($script:ForwardArgs -and $script:ForwardArgs.Count -gt 0) {
        $sub = $script:ForwardArgs[0]
        $rest = if ($script:ForwardArgs.Count -gt 1) { $script:ForwardArgs[1..($script:ForwardArgs.Count - 1)] } else { @() }
        switch ($sub.ToLowerInvariant()) {
            'serving' {
                & powershell -NoProfile -ExecutionPolicy Bypass `
                    -File (Join-Path $repoRoot 'scripts/pal-model-serving.ps1') @rest
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                return
            }
            'profiles' {
                & powershell -NoProfile -ExecutionPolicy Bypass `
                    -File (Join-Path $repoRoot 'scripts/pal-model-serving.ps1') @rest
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                return
            }
            'probe' {
                & powershell -NoProfile -ExecutionPolicy Bypass `
                    -File (Join-Path $repoRoot 'scripts/pal-model-probe.ps1') @rest
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                return
            }
            'proof' {
                & powershell -NoProfile -ExecutionPolicy Bypass `
                    -File (Join-Path $repoRoot 'scripts/pal-model-probe.ps1') @rest
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                return
            }
            default {
                Write-Host ""
                Write-Host "Unknown 'pal models' subcommand: $sub" -ForegroundColor Red
                Write-Host "Try one of:" -ForegroundColor Yellow
                Write-Host "  pal models              # recommended model + quantization"
                Write-Host "  pal models serving      # live serving profile checklist"
                Write-Host "  pal models probe        # probe /health, /v1/models, and /metrics on the model endpoint"
                Write-Host ""
                exit 1
            }
        }
    }

    # Recommended models per detected hardware. Hits /api/hardware on a
    # running sidecar (or boots a local check) and pulls the curated
    # recommendation matrix out of scripts/compatibility.json so the
    # answer matches docs/QUANTIZATION.md.
    $baseUrl = 'http://localhost:5088'

    Write-Host ""
    Write-Host "PalLLM model recommendation" -ForegroundColor Cyan
    Write-Host ""

    $hardware = $null
    try {
        $hardware = Invoke-RestMethod -Uri "$baseUrl/api/hardware" -Method Get `
            -TimeoutSec 5 -ErrorAction Stop
    } catch {
        Write-Host "Sidecar not running on $baseUrl." -ForegroundColor Yellow
        Write-Host "Without it the recommendation is generic; the table below shows the format / tier matrix only." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Boot the sidecar first to get a hardware-specific recommendation:" -ForegroundColor White
        Write-Host "  pal play       # boots the sidecar and opens the dashboard" -ForegroundColor Cyan
        Write-Host "  pal serve      # boots the sidecar without opening the dashboard" -ForegroundColor Cyan
        Write-Host ""
    }

    if ($hardware) {
        Write-Host "Detected hardware:" -ForegroundColor White
        Write-Host ("  OS                      : {0}" -f $hardware.operatingSystem)
        Write-Host ("  Cores / RAM             : {0} / {1} GB" -f $hardware.logicalCoreCount, $hardware.physicalRamGigabytes)
        Write-Host ("  GPU detected            : {0}" -f $hardware.gpuLikelyPresent)
        Write-Host ("  Architecture            : {0}" -f $hardware.gpuArchitecture)
        Write-Host ("  FP4 tensor cores likely : {0}" -f $hardware.fp4TensorCoresLikely)
        Write-Host ("  Detected tier           : {0}" -f $hardware.detectedTier)
        Write-Host ("  RECOMMENDED QUANT       : {0}" -f $hardware.recommendedQuantization) -ForegroundColor Green
        Write-Host ""
        Write-Host "Recommendation reason:" -ForegroundColor White
        Write-Host ("  {0}" -f $hardware.recommendation)
        Write-Host ""
    }

    $compat = Join-Path $repoRoot 'scripts/compatibility.json'
    if (Test-Path $compat) {
        try {
            $data = Get-Content $compat -Raw | ConvertFrom-Json
            Write-Host "Quantization formats (from scripts/compatibility.json):" -ForegroundColor White
            foreach ($q in $data.quantizationFormats) {
                Write-Host ("  {0,-10}  {1,-30}  {2}-bit  hw: {3}" -f `
                    $q.id, $q.displayName, $q.bitsPerWeight, $q.hardwareRequirement)
            }
            Write-Host ""
        } catch {
            Write-Host "Could not parse $compat" -ForegroundColor Yellow
        }
    }

    Write-Host "Full primer + community sentiment + recipes:" -ForegroundColor White
    Write-Host "  docs/QUANTIZATION.md          # vs Q4_K_M / Q8 / FP8 / NVFP4 / MXFP4"
    Write-Host "  docs/BLACKWELL_RECIPES.md     # copy-pastable vLLM startup snippets"
    Write-Host "  docs/MODEL_COLLABORATION.md   # per-tier model pairings"
    Write-Host "  pal models serving            # live per-lane model-server checklist"
    Write-Host "  pal models probe              # live /v1/models + /metrics evidence artifact"
    Write-Host ""
}

function Run-Config {
    # Subcommand router:
    #   pal config           (no args) -> open the file (legacy behaviour)
    #   pal config show      -> print effective config with source annotation
    #   pal config wizard    -> interactive first-time setup
    #
    # The flat (no-args) path is preserved so muscle memory doesn't break.
    if ($script:ForwardArgs -and $script:ForwardArgs.Count -gt 0) {
        $sub = $script:ForwardArgs[0]
        $rest = if ($script:ForwardArgs.Count -gt 1) { $script:ForwardArgs[1..($script:ForwardArgs.Count - 1)] } else { @() }
        switch ($sub.ToLowerInvariant()) {
            'show' {
                & powershell -NoProfile -ExecutionPolicy Bypass `
                    -File (Join-Path $repoRoot 'scripts/pal-config-show.ps1') @rest
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                return
            }
            'wizard' {
                & powershell -NoProfile -ExecutionPolicy Bypass `
                    -File (Join-Path $repoRoot 'scripts/pal-config-wizard.ps1') @rest
                if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                return
            }
            'edit' {
                # Fall through to the open-in-editor flow.
            }
            default {
                Write-Host ""
                Write-Host "Unknown 'pal config' subcommand: $sub" -ForegroundColor Red
                Write-Host "Try one of:" -ForegroundColor Yellow
                Write-Host "  pal config            # open appsettings.json in your editor"
                Write-Host "  pal config show       # print effective config (source-annotated)"
                Write-Host "  pal config wizard     # interactive 5-question setup"
                Write-Host ""
                exit 1
            }
        }
    }

    # No subcommand (or 'edit') -- open the right appsettings.json in the
    # user's default editor. Looks in three places in priority order:
    #   1. release zip path: sidecar/publish/appsettings.json
    #   2. dev path:         src/PalLLM.Sidecar/appsettings.json
    #   3. user-local path:  %LOCALAPPDATA%\Pal\Saved\PalLLM\appsettings.json
    $candidates = @(
        (Join-Path $repoRoot 'sidecar/publish/appsettings.json')
        (Join-Path $repoRoot 'src/PalLLM.Sidecar/appsettings.json')
        (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Pal/Saved/PalLLM/appsettings.json')
    )

    $found = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

    if (-not $found) {
        Write-Host ""
        Write-Host "No appsettings.json found." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Looked in:" -ForegroundColor White
        foreach ($c in $candidates) {
            Write-Host "  $c"
        }
        Write-Host ""
        Write-Host "Tip: most operator settings can be set via environment variable" -ForegroundColor White
        Write-Host "instead of editing JSON. See docs/ENV_VARS.md."
        Write-Host ""
        exit 1
    }

    Write-Host ""
    Write-Host "Opening: $found" -ForegroundColor Cyan
    try {
        if ($IsWindows -or $env:OS -eq 'Windows_NT') {
            Start-Process -FilePath $found
        } elseif ($IsMacOS) {
            & open $found
        } else {
            & xdg-open $found
        }
    } catch {
        Write-Host "Could not auto-open the file. Path:" -ForegroundColor Yellow
        Write-Host "  $found"
    }
    Write-Host ""
    Write-Host "While you're editing, useful references:" -ForegroundColor White
    Write-Host "  docs/ENV_VARS.md          # every config knob with defaults + effects"
    Write-Host "  docs/TUNING.md            # too-low / too-high guidance per parameter"
    Write-Host "  docs/PRIVACY.md           # what each opt-in flag changes about traffic"
    Write-Host "  pal.ps1 doctor            # validate the new config when you save"
    Write-Host ""
}

# -- Friendly error wrapper -----------------------------------------
function Invoke-PalVerb {
    param(
        [string]$VerbName,
        [scriptblock]$Action,
        [string]$ErrorHint = $null
    )
    try {
        & $Action
    } catch {
        Write-Host ""
        Write-Host "[PalLLM] '$VerbName' failed:" -ForegroundColor Red
        Write-Host "  $($_.Exception.Message)"
        if ($ErrorHint) {
            Write-Host ""
            Write-Host "Try this next:" -ForegroundColor Yellow
            Write-Host "  $ErrorHint"
        }
        exit 1
    }
}

# -- Dispatch -------------------------------------------------------
switch ($Verb.ToLowerInvariant()) {
    'build'      { Invoke-PalVerb 'build'      { Run-Build }      'pal doctor    # diagnose missing SDK / restore failures' }
    'test'       { Invoke-PalVerb 'test'       { Run-Test }       'pal build     # confirm the build is green first' }
    'audit'      { Invoke-PalVerb 'audit'      { Run-Audit }      'check the latest artifacts/full-audit/<ts>/RESULTS.md for the failing gate' }
    'fast-audit' { Invoke-PalVerb 'fast-audit' { Run-FastAudit }  'check the latest artifacts/full-audit/<ts>/RESULTS.md for the failing gate' }
    'cleanup'    { Invoke-PalVerb 'cleanup'    { Run-Cleanup }    'pal cleanup    # preview first, then add -Apply when the candidate list looks right' }
    'run'        { Invoke-PalVerb 'run'        { Run-Run }        'pal play       # boot in a window + open dashboard instead' }
    'play'       { Invoke-PalVerb 'play'       { Run-Play }       'pal play -PalworldPath "D:\SteamLibrary\steamapps\common\Palworld"' }
    'onboard'    { Invoke-PalVerb 'onboard'    { Run-Onboard }    'pal doctor     # see which step failed' }
    'doctor'     { Invoke-PalVerb 'doctor'     { Run-Doctor }     'docs/RUNBOOK.md per-symptom incident response' }
    'smoke'      { Invoke-PalVerb 'smoke'      { Run-Smoke }      'pal play       # confirm the sidecar is actually running first' }
    'native-proof'  { Invoke-PalVerb 'native-proof'  { Run-NativeProof }  'pal play       # boot the sidecar so the watcher has something to poll' }
    'replay'        { Invoke-PalVerb 'replay'        { Run-Replay }       'pal play       # boot the sidecar so the replay has somewhere to land' }
    'proof-bundle'  { Invoke-PalVerb 'proof-bundle'  { Run-ProofBundle }  'pal proof      # check what evidence is available before bundling' }
    'hud-bind'      { Invoke-PalVerb 'hud-bind'      { Run-HudBind }      'pal proof      # confirm a ranked HUD recommendation exists before applying' }
    'verify'        { Invoke-PalVerb 'verify'        { Run-Verify }       'pal package    # build a release zip first; verify needs a candidate to inspect' }
    'complete'      { Invoke-PalVerb 'complete'      { Run-Complete }     'pal next       # if you want a single action instead of the full queue dashboard' }
    'workflow-pins' { Invoke-PalVerb 'workflow-pins' { Run-WorkflowPins } 'check .github/workflows/*.yml for any unpinned action references' }
    'publish-audit' { Invoke-PalVerb 'publish-audit' { Run-PublishAudit } 'check artifacts/publish-audit/<ts>/RESULTS.md for the failing publication check' }
    'aot-readiness' { Invoke-PalVerb 'aot-readiness' { Run-AotReadiness } 'check artifacts/aot-readiness/<ts>/RESULTS.md for the failing readiness check' }
    'openapi'    { Invoke-PalVerb 'openapi'    { Run-OpenApi }    'pal build      # confirm Sidecar builds before regenerating' }
    'package'    { Invoke-PalVerb 'package'    { Run-Package }    'docs/RELEASE.md walk-through' }
    'recover'    { Invoke-PalVerb 'recover'    { Run-Recover }    'pal doctor     # see what the recovery left in a bad state' }
    'uninstall'  { Invoke-PalVerb 'uninstall'  { Run-Uninstall }  'pal uninstall -DryRun  # preview without changing anything' }
    'status'     { Run-Status }
    'context'    { Invoke-PalVerb 'context'    { Run-Context }    'pal status     # quick human-readable variant' }
    'scaffold'   { Invoke-PalVerb 'scaffold'   { Run-Scaffold }   'pal scaffold help    # show usage' }
    'hello'         { Run-Hello }
    'demo'          { Run-Demo }
    'campfire'      { Run-Campfire }
    'fortune'       { Run-Fortune }
    'whisper'       { Run-Whisper }
    'quest'         { Run-Quest }
    'tale'          { Run-Tale }
    'pack'          { Run-Pack }
    'benchmark'     { Invoke-PalVerb 'benchmark' { Run-Benchmark } 'pal play       # boot a sidecar so the benchmark has something to probe' }
    'welcome'       { Run-Welcome }
    'next'          { Run-Next }
    'models'        { Run-Models }
    'config'        { Run-Config }
    'mcp'           { Run-Mcp }
    'connect'       { Run-Connect }
    'install-llama-cpp' {
        & powershell -NoProfile -ExecutionPolicy Bypass `
            -File (Join-Path $repoRoot 'scripts/install-llama-cpp.ps1') @rest
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    'support'       { Invoke-PalVerb 'support' { Run-Support } 'pal play       # confirm the sidecar is reachable so the bundle has live evidence' }
    'check-updates' { Run-CheckUpdates }
    'news'          { Run-News }
    'harvest'         { Run-Harvest }
    'health'          { Run-Health }
    'proof'           { Run-Proof }
    'logs'            { Run-Logs }
    'preflight'       { Invoke-PalVerb 'preflight' { Run-Preflight } 'pal play       # boot a sidecar so preflight has more to check' }
    'patrol-report'   { Run-PatrolReport }
    'where'         { Invoke-PalVerb 'where'   { Run-Where }   "pal where 'fallback strategies'   # natural-language query" }
    'explain'       { Invoke-PalVerb 'explain' { Run-Explain } 'pal explain src/PalLLM.Sidecar/Program.cs' }
    'readiness'     { Run-Readiness }
    'list'       { Run-List }
    'help'       { Run-Help }
    default {
        Write-Host ""
        Write-Host "Unknown verb: $Verb" -ForegroundColor Red
        Write-Host ""
        # Suggest closest match by simple Levenshtein-style "starts with" check.
        # Source of truth: the top-level dispatch table above. Pinned by
        # MetaTests.cs `Pal_VerbInventory_AgreesAcross_PalJson_PalPs1Dispatch_RunList`
        # so the suggester never falls behind the real verb set.
        $known = 'build','test','audit','fast-audit','cleanup','run','play','onboard',
                 'doctor','smoke','native-proof','replay','proof-bundle','hud-bind','verify','complete',
                 'workflow-pins','publish-audit','aot-readiness','openapi','package','recover',
                 'uninstall','status','context','scaffold','hello','models',
                 'config','mcp','connect','install-llama-cpp','support','check-updates','readiness',
                 'demo','campfire','fortune','whisper','quest','tale',
                 'patrol-report','pack','benchmark','welcome','next','news',
                 'harvest','health','proof','logs','preflight','where','explain',
                 'list','help'
        $best = $known | Where-Object { $_.StartsWith($Verb.ToLower()) } | Select-Object -First 1
        if ($best) {
            Write-Host "Did you mean: pal $best ?" -ForegroundColor Yellow
            Write-Host ""
        }
        Run-Help
        exit 1
    }
}
