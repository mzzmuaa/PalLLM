[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$OutputRoot = "",
    [string]$Version = ([DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")),
    # Back-compat alias. Release packaging now includes the sidecar by default,
    # so keeping this switch means older automation still reads naturally.
    [switch]$IncludeSidecarPublish,
    # Opt out of the default bundled sidecar publish for lean internal
    # packages. Player-facing releases should normally keep the bundled
    # sidecar enabled.
    [switch]$SkipSidecarPublish,
    [string]$Configuration = "Release",
    # Back-compat alias. Bundled sidecar publishes are self-contained by
    # default, so this switch simply makes the intent explicit.
    [switch]$SelfContained,
    # Opt out of the default self-contained single-file sidecar and emit a
    # framework-dependent publish instead.
    [switch]$FrameworkDependentSidecar,
    # Target runtime for self-contained publish. win-x64 matches Palworld.
    # Override to linux-x64 or osx-x64 for headless / server builds.
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SkipVerification
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "PalLLM.Tooling.ps1")

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\packaging"
}

if ($IncludeSidecarPublish.IsPresent -and $SkipSidecarPublish.IsPresent) {
    throw "Choose either -IncludeSidecarPublish or -SkipSidecarPublish, not both."
}

if ($SelfContained.IsPresent -and $FrameworkDependentSidecar.IsPresent) {
    throw "Choose either -SelfContained or -FrameworkDependentSidecar, not both."
}

if ($SkipSidecarPublish.IsPresent -and ($SelfContained.IsPresent -or $FrameworkDependentSidecar.IsPresent)) {
    throw "Bundled sidecar mode flags cannot be used together with -SkipSidecarPublish."
}

$shouldIncludeSidecarPublish = -not $SkipSidecarPublish.IsPresent
if ($IncludeSidecarPublish.IsPresent) {
    $shouldIncludeSidecarPublish = $true
}

$shouldSelfContained = $shouldIncludeSidecarPublish
if ($FrameworkDependentSidecar.IsPresent) {
    $shouldSelfContained = $false
}
if ($SelfContained.IsPresent) {
    $shouldSelfContained = $true
}

$repoRoot = Get-PalLlmRepoRoot
$releaseName = "PalLLM-$Version"
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ("palllm-release-" + [guid]::NewGuid().ToString("N"))
$packageRoot = Join-Path $stagingRoot $releaseName
$packageZip = Join-Path $OutputRoot ($releaseName + ".zip")
$verifyScript = Join-Path $PSScriptRoot "verify-release-package.ps1"
$packagedScripts = @(
    "PalLLM.Tooling.ps1",
    "PalLLM.InstallManifest.ps1",
    "install-mod.ps1",
    "install-dev-mod.ps1",
    "uninstall-mod.ps1",
    "play-palllm.ps1",
    "recover-palllm.ps1",
    "export-support-bundle.ps1",
    "doctor.ps1",
    "run-sidecar-smoke.ps1",
    "run-native-proof.ps1",
    "export-release-proof-bundle.ps1",
    "apply-hud-bind-recommendation.ps1",
    "run-delivery-replay.ps1",
    "start-sidecar.ps1",
    "verify-release-package.ps1",
    # Pass 113+ pal.* verb scripts. Cheap to ship - each is small - and
    # bundling them keeps the zip's pal.json verb manifest honest. End
    # users mostly use play.bat / support.bat, but a coding agent
    # picking up the zip can run these directly with PowerShell.
    "pal-next.ps1",
    "pal-health.ps1",
    "pal-proof.ps1",
    "pal-model-serving.ps1",
    "pal-pack-copy.ps1",
    "aot-readiness.ps1"
)
$packagedDocs = @(
    # Player-facing - always include.
    "PITCH.md",
    "FAQ.md",
    "MCP_QUICKSTART.md",
    "TLS.md",
    "QUICKSTART.md",
    "OPERATIONS.md",
    "PRIVACY.md",
    "RELEASE.md",
    "UNINSTALL.md",
    "EASY_MODE.md",
    # Agent + harvester docs. Ship what an LLM coding agent needs to pick
    # up the project from the zip alone, without cloning. The agent reads
    # AGENTS.md / CLAUDE.md from the root + agents.json, then dives into
    # the docs below.
    #
    # NOTE: a small set of docs - HANDOFF, ARCHITECTURE, API, HARVEST,
    # CORE_LIBRARY, PACK_AUTHORING, COMPANION_INTELLIGENCE - is intentionally
    # excluded here because they legitimately discuss the portable adapter
    # surface using phrases the publication scanner reserves for clean
    # release copy [multi-game, cross-game, game-agnostic]. The full doc
    # set lives in the source repo; PLAYER_README points contributors there
    # for the complete reference.
    "INDEX.md",
    "READINESS.md",
    "CODE_MAP.md",
    "ADVISORS.md",
    "CONVENTIONS.md",
    "COOKBOOK.md",
    "ROADMAP.md",
    "RUNBOOK.md",
    "ENV_VARS.md",
    "TUNING.md",
    "DESIGN_PRINCIPLES.md",
    "ANTI_PATTERNS.md",
    "MENTAL_MODEL.md",
    "GLOSSARY.md",
    "REVIEW_CHECKLIST.md",
    "DATAFLOW.md",
    "STATE_MACHINES.md",
    "EXTENSION_POINTS.md",
    "HOT_PATH.md",
    "OBSERVABILITY.md",
    "CHEAT_SHEET.md",
    "QUICKREF.md",
    "EVENTS.md",
    "INVARIANTS.md",
    "TESTING.md",
    "AGENT_NATIVE.md",
    # Companion / texture docs
    "PROMPT_CARDS.md",
    "MOMENTS.md",
    "PACK_SAMPLES.md",
    # 2026 model best-practices recipes - Pass 112.
    "BLACKWELL_RECIPES.md",
    "QUANTIZATION.md",
    "MULTIMODAL_RECIPES.md",
    "AGENTIC_PATTERNS_2026.md",
    "MEMORY_RECIPES.md",
    "MODEL_COLLABORATION.md",
    "RESEARCH_NOTES_2026-05.md",
    "FALLBACK_AI_RESEARCH.md",
    "FIRST_HOUR.md",
    "COMPATIBILITY.md",
    "RELEASE_SIGNING.md",
    "SERVER_OPERATOR.md",
    "UX_PRINCIPLES.md",
    "IMPLEMENTATION_QUEUE.md"
)

function ConvertTo-PackageRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageRoot,

        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $basePath = [IO.Path]::GetFullPath($PackageRoot)
    $targetPath = [IO.Path]::GetFullPath($FilePath)
    if (-not $basePath.EndsWith([IO.Path]::DirectorySeparatorChar.ToString(), [System.StringComparison]::Ordinal)) {
        $basePath += [IO.Path]::DirectorySeparatorChar
    }

    $baseUri = [Uri]$basePath
    $targetUri = [Uri]$targetPath
    return [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($targetUri).ToString()).Replace('\', '/')
}

function New-PackageManifestFileRecord {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageRoot,

        [Parameter(Mandatory = $true)]
        [string]$FilePath
    )

    $item = Get-Item -LiteralPath $FilePath
    return [pscustomobject]@{
        Path = ConvertTo-PackageRelativePath -PackageRoot $PackageRoot -FilePath $FilePath
        SizeBytes = [int64]$item.Length
        Sha256 = (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash
    }
}

function Write-PlayerChangelog {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageRoot,

        [Parameter(Mandatory = $true)]
        [string]$Version
    )

    $releaseNotes = @"
# PalLLM Release Notes

Package version: $Version

This is the player-facing PalLLM release package for Palworld. It keeps
the release notes short on purpose: the source repository changelog contains
developer research notes and internal implementation history that are not
needed to install, troubleshoot, or validate the player build.

## Included

- play.bat for the normal install, sidecar start, doctor, dashboard, and game
  launch path.
- support.bat for a portable support bundle with launch, health, bridge, and
  release-readiness evidence.
- A bundled sidecar publish by default, including a self-contained Windows
  executable when packaging uses the default runtime settings.
- The UE4SS Lua bridge under mod/ue4ss/Mods/PalLLM/.
- Player and operator docs under README.md, PLAYER_README.txt, and docs/.
- RELEASE_PACKAGE_MANIFEST.json with required paths, file sizes, and SHA-256
  records for verification.

## Validate This Package

Run this from the package root:

    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-release-package.ps1

That verification checks the embedded manifest and publication-facing package
surface before the package should be trusted as a release candidate.
"@

    Set-Content -LiteralPath (Join-Path $PackageRoot "CHANGELOG.md") -Value $releaseNotes -Encoding ASCII
}

try {
    $sourceModPath = Get-PalLlmModSourcePath
    if (-not (Test-Path -LiteralPath $sourceModPath)) {
        throw "PalLLM mod source was not found at $sourceModPath"
    }

    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

    $packageScriptsRoot = Join-Path $packageRoot "scripts"
    $packageModRoot = Join-Path $packageRoot "mod\ue4ss\Mods"
    New-Item -ItemType Directory -Path $packageScriptsRoot -Force | Out-Null
    New-Item -ItemType Directory -Path $packageModRoot -Force | Out-Null

    Copy-Item -LiteralPath $sourceModPath -Destination $packageModRoot -Recurse -Force
    foreach ($scriptName in $packagedScripts) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $scriptName) -Destination $packageScriptsRoot -Force
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $packageRoot -Force
    foreach ($rootFile in @("install.bat", "uninstall.bat", "support.bat", "recover.bat", "START_HERE.txt", "LICENSE", "NOTICE.md", "THIRD_PARTY_NOTICES.md")) {
        $rootFilePath = Join-Path $repoRoot $rootFile
        if (Test-Path -LiteralPath $rootFilePath) {
            Copy-Item -LiteralPath $rootFilePath -Destination $packageRoot -Force
        }
    }
    Write-PlayerChangelog -PackageRoot $packageRoot -Version $Version
    $playBatPath = Join-Path $repoRoot "play.bat"
    if (Test-Path -LiteralPath $playBatPath) {
        Copy-Item -LiteralPath $playBatPath -Destination $packageRoot -Force
    }

    # Ship the agent-facing root briefings + machine-readable manifests so
    # any AI coding agent can pick the project up from the zip alone. The
    # agent's "you just landed here, what now?" reading order is:
    #   AGENTS.md -> agents.json -> docs/HANDOFF.md -> docs/CODE_MAP.md
    # All of those files are now in the zip if the source repo has them.
    $agentRootFiles = @(
        "AGENTS.md",
        "CLAUDE.md",
        "agents.json",
        "pal.json"
    )
    foreach ($agentRootFile in $agentRootFiles) {
        $sourcePath = Join-Path $repoRoot $agentRootFile
        if (Test-Path -LiteralPath $sourcePath) {
            Copy-Item -LiteralPath $sourcePath -Destination $packageRoot -Force
        }
    }

    # Ship the four reference personality packs + the five hand-curated
    # ritual catalogs (fortunes / whispers / quests / tales / patrols) so
    # an end user can pick a voice in 30 seconds and a contributing agent
    # has the canonical pack format on hand. Each pack ships pack.json
    # (manifest) + prompt.md + voice-hint.md with pre-computed ContentHash.
    $samplesSource = Join-Path $repoRoot "samples"
    if (Test-Path -LiteralPath $samplesSource) {
        $packageSamplesRoot = Join-Path $packageRoot "samples"
        New-Item -ItemType Directory -Path $packageSamplesRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $samplesSource "*") -Destination $packageSamplesRoot -Recurse -Force
    }

    # Ship the schemas directory so JSON validators (Test-Json, ajv,
    # jsonschema, etc.) can verify pack.json + agents.json + bridge
    # envelopes locally without cloning the repo.
    $schemasSource = Join-Path $repoRoot "docs\schemas"
    if (Test-Path -LiteralPath $schemasSource) {
        $packageSchemasRoot = Join-Path $packageRoot "docs\schemas"
        New-Item -ItemType Directory -Path $packageSchemasRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $schemasSource "*") -Destination $packageSchemasRoot -Recurse -Force
    }

    # Ship the ADRs (Architecture Decision Records) so a contributing
    # agent can read the load-bearing decisions before refactoring.
    $adrSource = Join-Path $repoRoot "docs\adr"
    if (Test-Path -LiteralPath $adrSource) {
        $packageAdrRoot = Join-Path $packageRoot "docs\adr"
        New-Item -ItemType Directory -Path $packageAdrRoot -Force | Out-Null
        Copy-Item -Path (Join-Path $adrSource "*") -Destination $packageAdrRoot -Recurse -Force
    }

    # Ship the starter narrative pack so install.bat can seed runtime\Packs\
    # with authored companion lore on first run. Kept under docs\examples\ in
    # the release layout so the path the installer reads in the repo and in
    # the zip are identical.
    # Ship every example file under docs/examples/ so the release zip has
    # the starter narrative pack, MCP host configs, the Docker Compose stack,
    # and anything else authors drop in that directory. Keeping this copy as a directory sweep means new
    # examples become shipping assets automatically â€” no script edit per file.
    $packageExamplesRoot = Join-Path $packageRoot "docs\examples"
    $examplesSource = Join-Path $repoRoot "docs\examples"
    if (Test-Path -LiteralPath $examplesSource) {
        New-Item -ItemType Directory -Path $packageExamplesRoot -Force | Out-Null
        # -Path (not -LiteralPath) is required because we're globbing child
        # files. -LiteralPath treats the trailing * as a literal file name.
        Copy-Item -Path (Join-Path $examplesSource "*") -Destination $packageExamplesRoot -Recurse -Force
    }

    # Ship the high-value deployment docs alongside the release so end
    # users don't have to clone the repo to follow them. Cross-links
    # between these docs resolve inside the ZIP. Keep the list tight:
    # these are the docs named by PLAYER_README.txt plus the release/TLS
    # references needed for first-run support.
    $packageDocsRoot = Join-Path $packageRoot "docs"
    foreach ($docFile in $packagedDocs) {
        $docSource = Join-Path $repoRoot "docs\$docFile"
        if (Test-Path -LiteralPath $docSource) {
            New-Item -ItemType Directory -Path $packageDocsRoot -Force | Out-Null
            Copy-Item -LiteralPath $docSource -Destination $packageDocsRoot -Force
        }
    }

    $sidecarPublishRoot = $null
    if ($shouldIncludeSidecarPublish) {
        if (-not (Test-CommandAvailable -CommandName "dotnet")) {
            throw "dotnet was not found on PATH, so the sidecar publish could not be included."
        }

        $sidecarProject = Get-PalLlmSidecarProjectPath
        if (-not (Test-Path -LiteralPath $sidecarProject)) {
            throw "The sidecar project was not found at $sidecarProject"
        }

        $sidecarPublishRoot = Join-Path $packageRoot "sidecar\publish"
        New-Item -ItemType Directory -Path $sidecarPublishRoot -Force | Out-Null

        $publishArgs = @('publish', $sidecarProject, '-c', $Configuration, '-o', $sidecarPublishRoot, '--nologo')
        if ($shouldSelfContained) {
            # Self-contained single-file: end user just double-clicks the
            # resulting .exe. No runtime install needed. Compression keeps
            # the binary size reasonable (~90-110MB for ASP.NET Core + deps
            # with R2R). Not trimmed - ASP.NET + JSON reflection + the MCP
            # SDK need the full BCL.
            #
            # ReadyToRun (R2R) ahead-of-time-compiles the managed code to
            # native so the JIT has less work on startup. Cold-start time
            # drops 50-70% (from ~1-2s to ~300-500ms on typical hardware)
            # at the cost of ~30-40MB extra binary size. On a sidecar the
            # operator starts once per session, faster startup is a direct
            # win on perceived latency for the first chat.
            #
            # IncludeAllContentForSelfExtract is required for R2R + single-
            # file to correctly bundle native images.
            $publishArgs += @(
                '-r', $RuntimeIdentifier,
                '--self-contained=true',
                '/p:PublishSingleFile=true',
                '/p:IncludeNativeLibrariesForSelfExtract=true',
                '/p:IncludeAllContentForSelfExtract=true',
                '/p:EnableCompressionInSingleFile=true',
                '/p:PublishReadyToRun=true',
                '/p:PublishReadyToRunComposite=true',
                '/p:DebugType=embedded'
            )
        }
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed while building the packaged sidecar."
        }
    }

    $dotnetPrereqLine = "- .NET 10 Runtime (required to run the sidecar from source)."
    if ($shouldSelfContained) {
        $dotnetPrereqLine = "- .NET 10 Runtime is NOT required - the bundled sidecar\publish\PalLLM.Sidecar.exe is a self-contained single-file binary for $RuntimeIdentifier. Unzip and run."
    }
    elseif ($shouldIncludeSidecarPublish) {
        $dotnetPrereqLine = "- .NET 10 Runtime (the bundled sidecar\publish is framework-dependent)."
    }

    $sidecarLaunchLine = "3. Start the sidecar: powershell -File scripts\start-sidecar.ps1"
    if ($shouldSelfContained) {
        $sidecarLaunchLine = "3. Start the sidecar: sidecar\publish\PalLLM.Sidecar.exe   (no install needed)"
    }
    $sidecarContentsLine = if ($shouldIncludeSidecarPublish) {
        if ($shouldSelfContained) {
            "sidecar\publish\         - self-contained PalLLM.Sidecar.exe (no .NET install)"
        }
        else {
            "sidecar\publish\         - bundled framework-dependent sidecar publish"
        }
    }
    else {
        "sidecar\publish\         - not included in this lean package"
    }

    $playerReadme = @"
PalLLM - you are 60 seconds from a working local AI companion
==============================================================

What you just downloaded
------------------------
A local-first AI companion runtime for Palworld. Your companion replies
run on your own computer. No cloud account. No subscription. No data
leaves your machine by default.

Quick start (60 seconds)
------------------------
1. Extract this entire folder somewhere writable - Desktop or Documents
   both work fine.
2. Double-click play.bat. It auto-detects Palworld, installs or refreshes
   the mod, starts the sidecar, opens the dashboard at
   http://localhost:5088, and launches the game.
3. Done. Open your browser to http://localhost:5088 and chat with your
   companion from the dashboard if you want to try it before loading
   into the game.

That's it. If it worked, you can skip the rest of this file.

If something went wrong
------------------------
Double-click support.bat. It bundles every relevant log, health snapshot,
and evidence artifact into
Runtime\SupportEvidence\latest-support-bundle.zip. Attach that zip when
reporting an issue - it's the highest-signal thing a maintainer can get.

To sanity-check the install:
  powershell -File scripts\doctor.ps1 -RunSmoke
Reports PASS / WARN / FAIL per check with a suggested fix for any
failure.

Manual install (if play.bat won't run)
--------------------------------------
1. Double-click install.bat - this just installs the mod.
   $sidecarLaunchLine

Connect PalLLM to an MCP-capable client
----------------------------------------
PalLLM exposes a Model Context Protocol (MCP) server at
http://localhost:5088/mcp. Any MCP-aware client can use its 38 tools.
Follow docs\MCP_QUICKSTART.md for setup patterns, or adapt the
configuration examples under docs\examples\ for your host's config format.

Prerequisites
-------------
- Palworld (any recent build; mod logs which hooks resolved at startup).
- UE4SS v3.x or newer, installed into the Palworld Win64 folder.
  Get it from: https://github.com/UE4SS-RE/RE-UE4SS/releases
$dotnetPrereqLine

Troubleshooting
---------------
- Palworld not detected: pass the path explicitly.
  install.bat -PalworldPath "D:\SteamLibrary\steamapps\common\Palworld"
- Sidecar didn't start: check docs\OPERATIONS.md for health probes.
- Companion replies feel generic: by default PalLLM runs with live
  inference OFF and answers via a deterministic director. Point
  PalLLM:Inference at a local HTTP chat-completions endpoint and set
  PalLLM:Inference:Enabled=true in sidecar\publish\appsettings.json for
  richer replies. See docs\QUICKSTART.md for the full opt-in walk-through.
- Stuck envelopes / lingering state: double-click recover.bat. Stops the
  sidecar, archives stuck messages, prunes old evidence, restarts.
- Want to remove PalLLM cleanly: double-click uninstall.bat. Your chat
  history and any custom packs are preserved by default - pass /full
  to wipe everything, or /preview to see what would happen first.
  Full walk-through: docs\UNINSTALL.md.

What's in this zip
------------------
END-USER ENTRY POINTS:
START_HERE.txt           - 5-line "open this first" pointer
install.bat              - one-click installer (mod only)
play.bat                 - one-click install + sidecar + game launcher
support.bat              - one-click support bundle export
recover.bat              - one-click reset when something is stuck
uninstall.bat            - one-click uninstaller (preserves chat history; pass /full to wipe)
scripts\                 - install / doctor / smoke / recovery / start helpers
$sidecarContentsLine
mod\ue4ss\Mods\PalLLM\   - the UE4SS Lua bridge

CODING-AGENT ENTRY POINTS (ALSO IN THIS ZIP):
AGENTS.md                - root briefing for any coding agent (read first)
CLAUDE.md                - Claude-Code-specific shortcut
agents.json              - machine-readable capability manifest
pal.json                 - machine-readable verb manifest

DOCS (in this zip):
docs\INDEX.md            - the doc map (40+ docs, Diataxis-organised)
docs\READINESS.md        - candid 10/10 scorecard per aspect
docs\CHEAT_SHEET.md      - one-page summary of everything
docs\QUICKREF.md         - sortable / grep-able alphabetical surface table
docs\PITCH.md            - plain-English tour
docs\QUICKSTART.md       - first-chat tutorial
docs\OPERATIONS.md       - tuning, health probes, troubleshooting
docs\PRIVACY.md          - inventory of every data-emitting surface
docs\BLACKWELL_RECIPES.md       - GPU serving recipes for low-latency local inference
docs\MULTIMODAL_RECIPES.md      - vision / audio / realtime WS recipes (2026)
docs\AGENTIC_PATTERNS_2026.md   - Tool Search / Programmatic Tool Calling / Pyramid MoA
docs\MEMORY_RECIPES.md          - Mem0 / Letta / Zep three-tier patterns
docs\PROMPT_CARDS.md     - 19 deterministic-fallback strategies as scenario cards
docs\PACK_SAMPLES.md     - the four reference personality packs
docs\MOMENTS.md          - the five hand-curated ritual catalogs
docs\COOKBOOK.md         - "how do I add X?" recipes
docs\schemas\            - JSON Schema 2020-12 contracts
docs\adr\                - 6 Architecture Decision Records
docs\examples\           - starter narrative pack, MCP configs, Docker compose
samples\packs\           - 4 reference personality packs (Warrior / Scholar / Healer / Trickster)
samples\moments\         - 5 ritual catalogs (fortunes / whispers / quests / tales / patrols)
(Source-repo-only docs: HANDOFF, ARCHITECTURE, API, HARVEST, CORE_LIBRARY,
 PACK_AUTHORING, COMPANION_INTELLIGENCE -- clone the repo for the full set)

ROOT FILES:
README.md                - project overview
CHANGELOG.md             - player-facing package notes
LICENSE                  - MIT License
NOTICE.md                - third-party disclaimers
THIRD_PARTY_NOTICES.md   - SPDX license-expressions per dependency
RELEASE_PACKAGE_MANIFEST.json - required-file manifest + SHA-256 records

Privacy posture (short version)
-------------------------------
Default install is fully local. Nothing leaves your machine unless you
explicitly configure a live inference / vision / TTS endpoint in
sidecar\publish\appsettings.json. You can verify this yourself by
running the sidecar and calling http://localhost:5088/api/privacy/posture
- it prints every data-emitting surface with never-leaves /
only-with-opt-in / leaves-by-default status.

Full disclosure in docs\PRIVACY.md.
"@
    Set-Content -LiteralPath (Join-Path $packageRoot "PLAYER_README.txt") -Value $playerReadme -Encoding ASCII

    $requiredPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($requiredRootFile in @(
        "README.md",
        "CHANGELOG.md",
        "LICENSE",
        "NOTICE.md",
        "THIRD_PARTY_NOTICES.md",
        "install.bat",
        "uninstall.bat",
        "play.bat",
        "support.bat",
        "recover.bat",
        "START_HERE.txt",
        "PLAYER_README.txt",
        # Agent-facing root briefings + machine-readable manifests
        "AGENTS.md",
        "CLAUDE.md",
        "agents.json",
        "pal.json")) {
        Add-UniqueString -List $requiredPaths -Value $requiredRootFile
    }

    foreach ($scriptName in $packagedScripts) {
        Add-UniqueString -List $requiredPaths -Value ("scripts/" + $scriptName)
    }

    foreach ($docFile in $packagedDocs) {
        $docPath = Join-Path $packageDocsRoot $docFile
        if (Test-Path -LiteralPath $docPath) {
            Add-UniqueString -List $requiredPaths -Value ("docs/" + $docFile)
        }
    }

    Add-UniqueString -List $requiredPaths -Value "mod/ue4ss/Mods/PalLLM/Scripts/main.lua"

    if ($shouldIncludeSidecarPublish) {
        $sidecarEntryPath = if ($shouldSelfContained) {
            "sidecar/publish/PalLLM.Sidecar.exe"
        }
        else {
            "sidecar/publish/PalLLM.Sidecar.dll"
        }

        Add-UniqueString -List $requiredPaths -Value $sidecarEntryPath
    }

    $manifestPath = Join-Path $packageRoot "RELEASE_PACKAGE_MANIFEST.json"
    $manifestFiles = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
        Where-Object { $_.FullName -ne $manifestPath } |
        Sort-Object FullName |
        ForEach-Object {
            New-PackageManifestFileRecord -PackageRoot $packageRoot -FilePath $_.FullName
        }

    $manifest = [ordered]@{
        SchemaVersion = 1
        ReleaseName = $releaseName
        Version = $Version
        CreatedAtUtc = [DateTimeOffset]::UtcNow
        IncludesSidecarPublish = $shouldIncludeSidecarPublish
        SelfContained = $shouldSelfContained
        RuntimeIdentifier = $RuntimeIdentifier
        ManifestRelativePath = "RELEASE_PACKAGE_MANIFEST.json"
        RequiredPaths = @($requiredPaths | Sort-Object -Unique)
        Files = @($manifestFiles)
    }
    Set-Content -LiteralPath $manifestPath -Value (ConvertTo-PalLlmJsonBody -InputObject $manifest) -Encoding UTF8

    if (Test-Path -LiteralPath $packageZip) {
        Remove-Item -LiteralPath $packageZip -Force
    }

    if ($PSCmdlet.ShouldProcess($packageZip, "Create PalLLM release package")) {
        Compress-Archive -LiteralPath $packageRoot -DestinationPath $packageZip -CompressionLevel Optimal
    }

    $packageVerification = $null
    if (-not $SkipVerification -and (Test-Path -LiteralPath $packageZip)) {
        if (-not (Test-Path -LiteralPath $verifyScript)) {
            throw "verify-release-package.ps1 was not found at $verifyScript"
        }

        $packageVerification = & $verifyScript -PackagePath $packageZip
    }

    [pscustomobject]@{
        ReleaseName = $releaseName
        PackageZip = $packageZip
        PackageRoot = $packageRoot
        SourceModPath = $sourceModPath
        IncludedSidecarPublish = $shouldIncludeSidecarPublish
        SidecarPublishRoot = $sidecarPublishRoot
        PackageManifestPath = $manifestPath
        PackageVerificationArtifact = if ($packageVerification) { [string]$packageVerification.LatestPackageVerificationArtifact } else { "" }
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

