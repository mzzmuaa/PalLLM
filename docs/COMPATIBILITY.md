# PalLLM Compatibility Matrix

Last audited: `2026-05-24`

Known-compatible and known-conflicting setups for PalLLM. Consumed by
`scripts/doctor.ps1` via [`scripts/compatibility.json`](../scripts/compatibility.json)
so installed-environment checks stay data-driven.

## Supported Palworld versions

| Palworld version | PalLLM version | UE4SS version | Notes |
|---|---|---|---|
| `0.5.x` (current) | `v1.0.0` | `3.x` | Reference target |
| `0.4.x` | `v1.0.0` | `3.x` | Expected to work; unverified |
| `< 0.4.0` | - | - | Unsupported; upgrade Palworld |

## Supported .NET runtimes

| Runtime | Status | Notes |
|---|---|---|
| .NET 10 LTS | **Supported** | Self-contained `PalLLM.Sidecar.exe` bundles it; no separate install needed |
| .NET 9 | Unsupported | Missing System.Text.Json features PalLLM uses |
| .NET 8 and older | Unsupported | - |

## Operating systems

| OS | Sidecar | UE4SS mod | Dedicated-server host |
|---|---|---|---|
| Windows 10 (22H2+) | **Supported** | **Supported** | **Supported** |
| Windows 11 (all builds) | **Supported** | **Supported** | **Supported** |
| Windows Server 2022 | **Supported** | - | **Supported** |
| Linux (x86_64) | **Supported** (sidecar only) | - | **Supported** (sidecar only) |
| macOS | Sidecar builds, untested | - | - |

The **mod** (`mod/ue4ss/Mods/PalLLM`) is structurally Windows-only -
UE4SS is a Win64 process injector targeting the Palworld executable.
There is no UE4SS port for Linux or macOS. `scripts/install-mod.ps1`
and `scripts/play-palllm.ps1` fail-fast with a friendly message
explaining this if invoked from PowerShell 7+ on a non-Windows host;
they direct the operator at `dotnet run` for the cross-platform
sidecar-only path.

The **sidecar** has no Windows-specific runtime dependency. The
domain assembly is `<IsAotCompatible>true</IsAotCompatible>`.
Hardware probing in `HardwareProfiler.cs` branches per OS
(Windows P/Invoke + registry; Linux `/proc/meminfo` + `/proc/driver/nvidia`;
macOS falls back to `GC.GetGCMemoryInfo()`) so detection is robust on
all three platforms.

## CPU architectures

| Arch | Source builds | Pre-built release | Notes |
|---|---|---|---|
| `x86_64` (Intel / AMD) | **Supported** | **Supported** (`win-x64` zip) | Reference architecture |
| `x86_64` Linux | **Supported** | Build from source: `dotnet publish -r linux-x64 -c Release --self-contained` | Sidecar only; dedicated-server host pattern |
| `x86_64` macOS | **Supported** | Build from source: `dotnet publish -r osx-x64 -c Release --self-contained` | Sidecar only |
| `arm64` Apple Silicon (M1/M2/M3/M4) | **Supported** | Build from source: `dotnet publish -r osx-arm64 -c Release --self-contained` | .NET 10 has native ARM64 support |
| `arm64` Linux (Raspberry Pi 4+, AWS Graviton) | **Supported** | Build from source: `dotnet publish -r linux-arm64 -c Release --self-contained` | Sidecar only; useful for low-power dedicated servers |
| `arm64` Windows on ARM (Snapdragon X) | **Supported** | Build from source: `dotnet publish -r win-arm64 -c Release --self-contained` | Sidecar only; mod still requires x64 Palworld |

The published GitHub release zip is currently `win-x64` only because
the player flow needs the mod, which needs Windows x64. The sidecar
builds and runs on every other RID listed above - the build system
has no architecture gates beyond `<TargetFramework>net10.0</TargetFramework>`.

To verify on your platform after a source build:
```powershell
dotnet test PalLLM.sln --configuration Release    # 1315 / 1315 expected
```
Tests use only platform-agnostic APIs (`HttpClient`, `JsonDocument`,
`MemoryStream`); they pass on every supported RID.

## Hardware tier recommendations

Sourced from `HardwareProfiler` at runtime; published for consumers
who want to check before installing. The four tiers are computed
deterministically from `(coreCount, ramGiB, gpuPresent)` - see
`src/PalLLM.Domain/Inference/HardwareProfiler.cs`.

| Tier | Classification rule (live code) | Recommended for | What runs |
|---|---|---|---|
| `Constrained` | no GPU, OR `< 8 cores`, OR `< 16 GiB RAM` | 2016-era CPU-only laptops, basic desktops | Deterministic fallback always; small local model (1-3B) optional via `pal connect ollama` |
| `Standard` | GPU present + `>= 8 cores` + `>= 16 GiB RAM` | 2018-2024 mid-range gaming PCs | All of Constrained, plus 7-13B local models, vision describe, TTS |
| `Generous` | GPU present + `>= 16 cores` + `>= 48 GiB RAM` | 2022+ high-end workstations | All of Standard, plus 30-70B local models, multimodal lanes, Duo mesh |

The three values are the live `DuoHardwareTier` enum
(`src/PalLLM.Domain/Inference/DuoOrchestratorPlanner.cs:400`).
For 2025+ Blackwell hardware (5090 / B-series with NVFP4 + vLLM)
the system still reports `Generous` - Blackwell is a sub-recipe
of Generous, not a fourth enum value. The `pal benchmark`
script defines an additional `Blackwell` budget row for live
latency reporting only (`scripts/pal-benchmark.ps1:108-111`).
See [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md) for the
recipe-specific tuning.

**Anything older than 2016 may still work but isn't tested.** The
deterministic fallback director runs on essentially any system
that can run .NET 10 (which means Windows 10 1607+, kernel
= 4.15 on Linux). Inference quality is bounded by the
local engine, not by PalLLM.

### What works on 10-year-old hardware (2016-era)

Specifically tested or expected to work on a 2016-era reference
profile (Intel i5-6500, 8 GiB DDR4, no discrete GPU, Windows 10):

| Subsystem | Status on 2016 hardware |
|---|---|
| Sidecar runtime | Runs; chat replies via deterministic fallback |
| Field Console dashboard | Renders in any 2017+ browser (ES2017 minimum) |
| `/api/*` (57 routes) | All routes respond; latency in `Constrained` tier budget (1500 ms warm) |
| `/mcp` (38 tools) | All tools respond |
| Local inference | warning: 1-3B model recommended; 7B+ will be slow |
| Vision describe | warning: off by default; CPU-only multimodal is impractical at this tier |
| TTS synthesis | warning: off by default; CPU-only voice synthesis is slow |
| UE4SS Lua mod | depends on whether the system can run Palworld at all (Palworld's own minimum is GTX 1050 Ti / 16 GiB RAM) |

The runtime never refuses to start based on hardware. Worst case
on a Constrained system with no GPU and inference disabled: the
deterministic director answers every chat turn in `< 100 ms` on
warm cache, the dashboard renders, the MCP server is fully
addressable. Every interactive surface remains responsive.

## Browser compatibility (Field Console dashboard)

The dashboard at `http://localhost:5088/` is a vanilla
HTML/CSS/JS bundle in `src/PalLLM.Sidecar/wwwroot/`. No build
step, no framework, no transpiler. It uses ES2017 features
(`<script type="module">`, `AbortController`,
`Intl.RelativeTimeFormat` with graceful fallback).

| Browser | Minimum version | Status |
|---|---|---|
| Chrome / Edge / Opera | 66+ (mid-2018) | Supported |
| Firefox | 57+ (late 2017) | Supported |
| Safari | 11.1+ (early 2018) | Supported |
| Internet Explorer | any | Unsupported (lacks ES modules) |

If you can run a 2018-or-later browser, the dashboard works.

## PowerShell compatibility (operator scripts)

Every `pal.ps1` verb and every `scripts/*.ps1` script targets
**Windows PowerShell 5.1** as the minimum (ships with Windows 10
out of the box) and works on **PowerShell 7+** identically. No
script uses PS-7-only syntax (`??` ternary, `?.` null-conditional,
pipeline-chain operators).

| PowerShell | Minimum | Status |
|---|---|---|
| Windows PowerShell 5.1 | bundled with Windows 10 | Supported |
| PowerShell 7.0+ | install via `winget install Microsoft.PowerShell` | Supported (recommended for cross-platform) |
| PowerShell 6.x | end-of-life | Untested |

Scripts that ship with the release zip use the `pwsh` invocation
when PowerShell 7 is available; the `play.bat` launcher falls
back to `powershell.exe` (the 5.1 native interpreter) if `pwsh`
is missing.

## Known-good UE4SS mod pairs

| Mod | Status | Notes |
|---|---|---|
| `PalKit` | Compatible | No overlap in hook surface |
| `PalDex` | Compatible | No overlap in hook surface |
| `BetterPalworld` | Compatible | Expected to work - verify no double-hook on pal-AI |
| `LiveMap` | Compatible | Different hook surface |

## Known-conflicting UE4SS mod pairs

| Mod | Status | Mitigation |
|---|---|---|
| `NativeLLM` (if it ever exists) | Would conflict | Choose one LLM bridge |
| Other mods that bind the same HUD widgets as `PalLLM:Bridge:NativeHudTargets[]` | May conflict | Set `PalLLM:Bridge:NativeHudTargets` to a non-overlapping target, or uninstall the other mod |

## Known-conflicting system tooling

| Tool | Status | Mitigation |
|---|---|---|
| Aggressive corporate AV / EDR that flags UE4SS DLL injection | Blocks install | Whitelist the PalLLM install directory |
| Windows Defender Exploit Guard with "block DLL injection" | Blocks mod loading | See docs/SERVER_OPERATOR.md - "Graceful antivirus compatibility" |
| AppLocker / WDAC in Enforce mode | Blocks unsigned EXE | Code-sign the release EXE or allowlist the publisher |

## Known-safe inference endpoints

| Provider | Transport | Status | Notes |
|---|---|---|---|
| Ollama (loopback) | `http://127.0.0.1:11434/v1/` | **Reference** | Default in `appsettings.json` |
| Ollama (LAN) | `http://<host>:11434/v1/` | Supported | Set `PalLLM:Inference:BaseUrl` |
| llama.cpp server | `http://<host>:8080/v1/` | Supported | OpenAI-compatible |
| vLLM | `http://<host>:8000/v1/` | Supported | OpenAI-compatible |
| Text Generation Inference | `http://<host>:3000/v1/` | Supported | OpenAI-compatible (messages API) |
| OpenAI direct | `https://api.openai.com/v1/` | Works but not recommended | Leaks chat traffic off-device; `/api/airgap/verify` will mark `public-internet` |

## Reporting a new compatibility entry

Open an issue using the `compat_report.md` template. Attach:

1. `support.bat` output (covers sidecar + runtime + hardware posture)
2. The exact Palworld / UE4SS / PalLLM versions involved
3. Steps to reproduce

Maintainers add validated entries to this doc and to
`scripts/compatibility.json`.

## Where `doctor.ps1` reads this

`scripts/doctor.ps1` consumes `scripts/compatibility.json` (a
machine-readable mirror of this table). At run time it:

1. Detects the installed UE4SS + Palworld versions
2. Cross-references against the JSON matrix
3. Prints a per-check status (supported / conflict / unknown) in the
   doctor summary
4. Suggests the recommended mitigation if a conflict is known

This way a new known issue can be added by patching the JSON - no
code release required to surface the warning to operators.


