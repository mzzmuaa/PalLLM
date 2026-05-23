# `scripts/`

PowerShell automation. The repo-root [`pal.ps1`](../pal.ps1) is
the verb-driven entry point that wraps the most-used scripts
here; reach into this directory directly only when you need a
flag the wrapper doesn't pass through.

## Categories

### Onboarding + day-to-day

| Script | Purpose | `pal.ps1` shortcut |
|---|---|---|
| `onboard.ps1` | First-time setup: SDK + build + test + audit + dashboard | `pal onboard` |
| `play-palllm.ps1` | Boot sidecar in a window + open Field Console | `pal play` |
| `doctor.ps1` | Environment + smoke + delivery-replay | `pal doctor` |
| `recover-palllm.ps1` | Last-resort: archive runtime root + clean start | `pal recover` |

### Drift + audit

| Script | Purpose | `pal.ps1` shortcut |
|---|---|---|
| `run_full_audit.ps1` | The 16 drift gates | `pal audit` / `pal fast-audit` |
| `audit-workflow-action-pins.ps1` | GitHub Actions full-SHA pin audit | `pal workflow-pins` |
| `audit_public_copy.ps1` | Brand-name guard for release-facing copy | (called by `run_full_audit`) |
| `path_reference_audit.ps1` | Repo-relative path reference resolver | (called by `run_full_audit`) |
| `public_copy_policy.ps1` | Public-copy policy data | (called by `audit_public_copy`) |

### Smoke + replay

| Script | Purpose | `pal.ps1` shortcut |
|---|---|---|
| `run-sidecar-smoke.ps1` | Live sidecar smoke test | `pal smoke` |
| `run-delivery-replay.ps1` | Replay outbox envelopes | (called by `doctor`) |

### Release

| Script | Purpose | `pal.ps1` shortcut |
|---|---|---|
| `package-release.ps1` | Build the release zip | `pal package` |
| `compute-release-checksums.ps1` | SHA-256 + sigstore inputs | (called by release pipeline) |
| `export-openapi.ps1` | Regenerate the OpenAPI snapshot | `pal openapi` |
| `export-release-proof-bundle.ps1` | Bundle release evidence | (called by release pipeline) |
| `export-support-bundle.ps1` | Bundle support data for an incident report | n/a |

### Install / mod

| Script | Purpose |
|---|---|
| `install-mod.ps1` | Install the UE4SS mod |
| `install-dev-mod.ps1` | Symlink the mod for dev iteration |
| `apply-hud-bind-recommendation.ps1` | Apply HUD probe recommendation to settings |

### Shared helpers

| File | Purpose |
|---|---|
| `PalLLM.Tooling.ps1` | Shared PowerShell helpers dotted into other scripts |
| `compatibility.json` | Schema-v1 compatibility matrix consumed by `doctor.ps1` |

## Add a script

Match the existing conventions:

- Top-level `[CmdletBinding()]` + `param(...)`.
- Comment-based help block (`.SYNOPSIS`, `.DESCRIPTION`,
  `.PARAMETER`, `.EXAMPLE`).
- Resolve `$PSScriptRoot` and use absolute paths so the script
  works regardless of working directory.
- Source `PalLLM.Tooling.ps1` for shared helpers.

If your script is one of the most-used operations, add a verb
to [`../pal.ps1`](../pal.ps1).
