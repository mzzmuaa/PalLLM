# Uninstalling PalLLM

Last audited: `2026-05-21`

PalLLM installs cleanly and uninstalls just as cleanly. Three
audiences are covered below: end users (one-click), operators (with
options), and developers (working from the repo). The full design
rationale, including the 2026 industry patterns and 2030 directions
this implementation follows, is at the bottom.

## TL;DR

```
Double-click uninstall.bat
```

That's it. The mod is removed. Your chat history, custom packs, and
release evidence are preserved by default. Want everything wiped?
Pass `/full`.

## End users — one-click uninstall

The release zip ships an `uninstall.bat` next to `install.bat`.
Double-click it and:

| Default | Behavior |
|---|---|
| Mod files in `<Palworld>/Pal/Binaries/Win64/Mods/PalLLM/` | **Removed** |
| `enabled.txt` next to the mod | **Removed** |
| Sample personality pack at `runtime-root/Packs/chillet-pack.json` | **Removed** |
| `runtime-root/install-manifest.json` | **Removed** |
| `runtime-root/session.json` (chat memory) | **Preserved** |
| `runtime-root/Packs/` (any custom packs you added) | **Preserved** |
| `runtime-root/TTS/` (cached TTS audio) | **Preserved** |
| `runtime-root/ReleaseEvidence/` + `SupportEvidence/` | **Preserved** |
| `runtime-root/Bridge/` (history) | **Preserved** |

Two helpful flags:

```
uninstall.bat /preview     Show what would happen without changing anything.
uninstall.bat /full        Remove everything, including chat history and packs.
```

## Operators — uninstall-mod.ps1

The script `scripts/uninstall-mod.ps1` is what `uninstall.bat`
wraps. It supports `pwsh` and `powershell.exe` and is safe to call
from any directory — it resolves paths relative to the script
itself.

```powershell
# Preview without changing anything
pwsh ./scripts/uninstall-mod.ps1 -DryRun

# Default uninstall (preserves personal data)
pwsh ./scripts/uninstall-mod.ps1

# Full uninstall (wipes runtime-root entirely)
pwsh ./scripts/uninstall-mod.ps1 -Full

# Force a specific Palworld path (when manifest is missing)
pwsh ./scripts/uninstall-mod.ps1 -PalworldPath "D:\\SteamLibrary\\steamapps\\common\\Palworld"

# Verb-driven shortcut (works on any platform with PowerShell)
pwsh ./pal.ps1 uninstall            # default
pwsh ./pal.ps1 uninstall -DryRun    # preview
pwsh ./pal.ps1 uninstall -Full      # full

# Or via Make
make uninstall
```

The script prints a complete plan before doing anything. Each line
shows whether the artifact will be **removed**, **preserved**, or
is already **missing**. The plan is also returned as a structured
PowerShell object so it can be programmatically consumed by smoke
scripts and CI.

## Developers — junction-mode + repo-only data

If you installed with `-InstallMode Junction` (live-editable from
the repo source), uninstall removes the **junction** without
touching the repo source:

```
[REMOVE]   junction      D:\SteamLibrary\steamapps\common\Palworld\Pal\Binaries\Win64\Mods\PalLLM
           reason: manifest
```

PowerShell's `Remove-Item` with `-Recurse` on a junction would
follow the link and delete the target. The uninstaller deliberately
uses non-recursive removal for `Kind == 'junction'` so your repo
stays intact.

## How it works — the install manifest

Every install of PalLLM writes a manifest to
`runtime-root/install-manifest.json`. The manifest schema is
[`schemas/install-manifest.schema.json`](schemas/install-manifest.schema.json),
the producer is [`scripts/PalLLM.InstallManifest.ps1`](../scripts/PalLLM.InstallManifest.ps1),
and the producer is integrated into
[`scripts/install-mod.ps1`](../scripts/install-mod.ps1).

The manifest records every filesystem touchpoint the install
created — the mod directory (or junction), the `enabled.txt`, the
sample pack, plus its own path. Each artifact has a `Kind`
(`directory` / `file` / `junction` / `enabled-file` / `sample-pack`)
and an `AddedAt` timestamp.

When you uninstall:

1. The manifest is read.
2. A removal plan is built and printed.
3. Artifacts are removed in reverse-creation order.
4. The manifest itself is removed last (only if not in `-Full`
   mode, where the entire runtime root goes anyway).

When the manifest is **missing** (older install, manifest deleted,
or sandbox where it never existed), the uninstaller falls back to
"uninstall by convention" — it auto-detects the Palworld install
the same way `install-mod.ps1` does, and removes the canonical
`Mods/PalLLM/` directory there.

## What gets removed vs preserved (decision tree)

```
Always removed (the mod itself):
  - <Palworld>/Pal/Binaries/Win64/Mods/PalLLM/   (or its junction)
  - <Palworld>/Pal/Binaries/Win64/Mods/PalLLM/enabled.txt

Always removed (the manifest itself):
  - runtime-root/install-manifest.json

Preserved by default (-PreservePersonalData $true):
  - runtime-root/session.json     (chat history + relationships)
  - runtime-root/Packs/           (custom personality + narrative packs)
  - runtime-root/TTS/             (cached TTS audio)
  - runtime-root/Bridge/          (event archive)
  - runtime-root/ReleaseEvidence/ (audit / smoke / native-proof / package
                                   verification + history)
  - runtime-root/SupportEvidence/ (support bundle outputs + history)

Removed only with -Full:
  - The entire runtime root above is wiped.
```

`runtime-root` defaults to
`%LOCALAPPDATA%\Pal\Saved\PalLLM` and is configurable via
`PalLLM:PalSavedRoot` + `PalLLM:RuntimeFolderName`.

## Backing up before uninstalling

If you might want to come back later, the simplest safety net is to
copy the runtime root before running `-Full`:

```powershell
$src = "$env:LOCALAPPDATA\Pal\Saved\PalLLM"
$dst = "$env:USERPROFILE\Desktop\PalLLM-backup-$(Get-Date -Format yyyyMMdd-HHmmss)"
Copy-Item -LiteralPath $src -Destination $dst -Recurse
```

Restoring is just the reverse: copy that folder back over the
runtime root after re-installing.

## Reinstalling on the same machine

After uninstall, just double-click `install.bat` again. If you kept
personal data (the default), your chat history and packs come back
automatically — the runtime detects them on first boot.

## Reinstalling on a different Palworld build

Run a `-Full` uninstall first, then install. This guarantees the
new install starts from a clean manifest and there's no cross-
contamination from a previous Palworld version's hook surface.

## What this implementation is built on (research basis)

The pattern is informed by current industry practice:

### 2026 mod-manager parity

- **r2modman** (v3.2.15, March 2026) and **Thunderstore Mod
  Manager** (v1.98.3, October 2025) both let users
  *"uninstall mods with a single click while keeping the base
  game unaffected"*. The PalLLM uninstaller delivers the same
  experience for Palworld.
- **Vortex (Nexus Mods)** uses an internal manifest of installed
  files plus an undo journal. PalLLM's manifest follows the same
  shape but in plain JSON instead of a vendor format, so any
  third-party tool can read and validate it.

### 2026 transactional-install best practice

- **Helm `--atomic` flag** (Kubernetes ecosystem, current best
  practice 2026): if any step fails, automatically roll back all
  changes made during the operation. PalLLM's `install-mod.ps1`
  now has the same posture — every filesystem mutation is
  recorded as it happens, and a `try/catch` rolls back on any
  failure.
- **openSUSE transactional-update** (Linux atomic-update
  reference): "only if all updates could be applied successfully,
  then the system will switch into that new updated state. If any
  error occurred the updated system will just be discarded
  again."

### 2030+ directions this leaves room for

The manifest format and uninstall script are designed to extend
naturally into:

- **Snapshot rollback at the filesystem level** — Btrfs / ZFS /
  Windows shadow copies. The manifest's `InstalledAt` and
  `Artifacts[].AddedAt` fields make a snapshot point obvious.
- **Content-addressed storage** — every installed file stored by
  SHA-256 with refcounted GC. Add a `Sha256` field to the
  `Artifact` schema and the uninstaller becomes a refcount-
  decrement.
- **Declarative reconciliation (Apple DDM-style)** — the manifest
  is the desired state; a future `pal sync` verb could re-converge
  any drift from it without imperative steps.
- **Cryptographic provenance per file** — PalLLM already ships
  SLSA provenance + SHA256SUMS + sigstore signing for the release
  zip. Extending that to the install manifest itself (signing the
  manifest at install time, verifying at uninstall time) closes
  the supply-chain loop locally.
- **Sandbox / virtual-filesystem install** — Sandboxie-style
  install where every install lives in a virtual layer; uninstall
  is dropping the layer. Useful in shared machines and player
  trial deployments. Out of scope for v1 but the manifest is
  forward-compatible.

## Related

- [`schemas/install-manifest.schema.json`](schemas/install-manifest.schema.json)
  — formal JSON Schema for the manifest
- [`../scripts/PalLLM.InstallManifest.ps1`](../scripts/PalLLM.InstallManifest.ps1)
  — the manifest producer (shared between install + uninstall)
- [`../scripts/install-mod.ps1`](../scripts/install-mod.ps1) +
  [`../install.bat`](../install.bat) — the install path
- [`../scripts/uninstall-mod.ps1`](../scripts/uninstall-mod.ps1) +
  [`../uninstall.bat`](../uninstall.bat) — the uninstall path
- [`RUNBOOK.md`](RUNBOOK.md) — incident response, including
  "I uninstalled but want my data back" recovery steps
- [`OPERATIONS.md`](OPERATIONS.md) — long-running operations
  reference
- [`SECURITY.md`](../SECURITY.md) — supply-chain attestation
  context for the install manifest
