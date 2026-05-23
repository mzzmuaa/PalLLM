# PalLLM Runbook — incident response

Last audited: `2026-05-23`

When something is wrong with a running PalLLM install, this is the
short version of "what to do, in what order." Each section names the
likely cause, the diagnostic that confirms it, and the smallest fix
that gets the runtime back to a working state. The full operator
reference for routine maintenance lives in
[`OPERATIONS.md`](OPERATIONS.md).

## First five minutes — universal triage

Run these three commands in order. They cover ~90% of "something's
wrong":

```powershell
# 1. Health snapshot — what does the runtime think is broken?
curl -s http://localhost:5088/api/health | ConvertFrom-Json | Format-List

# 2. Doctor — environment + smoke + delivery replay
powershell -File scripts/doctor.ps1 -RunSmoke -RunDeliveryReplay

# 3. Latest audit log — is the build/test surface clean?
Get-Content artifacts/full-audit/(Get-ChildItem artifacts/full-audit | Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty Name)/RESULTS.md
```

If `/api/health` returns a non-200 the sidecar isn't running — jump
to [Sidecar won't start](#sidecar-wont-start). Otherwise the
`OperatorHealthScore` and its top reasons name the specific
problem; pick the matching section below.

## Sidecar won't start

**Likely causes (in order of frequency):**

1. **Port 5088 already in use.** Another process is bound to the
   port — old PalLLM instance, another dev sidecar, or an unrelated
   service.
2. **`runtime-root` not writable.** The sidecar can't create
   `Bridge/`, `SessionState/`, or evidence directories.
3. **.NET 10 SDK missing or wrong version.** The release ZIP is
   self-contained but a source build needs SDK 10.0.
4. **Antivirus quarantine.** Some AV products quarantine
   `PalLLM.Sidecar.exe` on first run.

**Diagnose:**

```powershell
# Port conflict
Get-NetTCPConnection -LocalPort 5088 -ErrorAction SilentlyContinue
# If something is bound, the OwningProcess column names the offender.

# Writable runtime root
Test-Path "$env:LOCALAPPDATA\Pal\Saved\PalLLM"
# If False, the sidecar will create it on first run; if True but the
# Bridge/ subdirs don't exist, you have a permissions problem.

# .NET SDK
dotnet --info | Select-String -Pattern 'Version:' | Select-Object -First 1
# Should report 10.0.x or higher.
```

**Fix:**

- Port conflict: stop the offender or run with `--urls
  http://localhost:5089` and update any clients pointing at 5088.
- Permissions: delete the runtime root from your user profile and
  let the sidecar recreate it. The session.json and any packs
  under it will be lost; chat history is recoverable from
  `Bridge/Archive/`.
- SDK missing: install .NET 10 from
  https://dotnet.microsoft.com/download/dotnet/10.0
- AV: add an exclusion for the sidecar binary's directory, or run
  the release ZIP variant that's already digitally signed.

## Chat returns deterministic replies even though I have inference enabled

**Likely causes:**

1. **Inference circuit breaker is open.** Five consecutive failures
   tripped it; subsequent calls skip the HTTP roundtrip.
2. **Per-character rate limit hit.** The character is breaching
   `MaxCharacterRequestsPerMinute`.
3. **Thermal gate fired.** GPU temperature crossed the configured
   threshold.
4. **Inference endpoint is offline or returning errors.**

**Diagnose:**

```powershell
$health = curl -s http://localhost:5088/api/health | ConvertFrom-Json
$health.InferenceCircuitOpen     # True = breaker tripped
$health.InferenceConfigured      # False = inference is off entirely
$health.InferenceActiveModel     # Empty = not configured

# Check ResponsePath on a recent reply — this is the smoking gun
curl -s -X POST http://localhost:5088/api/chat `
    -H "Content-Type: application/json" `
    -d '{"userMessage":"hi","characterId":1}' | ConvertFrom-Json | Format-List ResponsePath
```

The `ResponsePath` value is exact: `fallback-after-breaker-open`,
`fallback-after-rate-limit`, `fallback-after-thermal-gate`,
`fallback-after-inference-disabled`, etc.

**Fix:**

- Breaker open: wait for the configured cooldown (default 30 s),
  or restart the sidecar to reset state. If breaker keeps
  re-opening, the inference endpoint itself is unhealthy — go check
  Ollama / your model server.
- Rate limit: bump `MaxCharacterRequestsPerMinute` or wait for the
  sliding window to free up.
- Thermal: cool the GPU, or temporarily set
  `Inference:ThermalGate:Enabled = false`.
- Endpoint down: hit `Inference:BaseUrl` directly with curl. If
  Ollama isn't responding, restart it. If the model is missing,
  pull it.

## Outbox isn't emptying — Lua bridge sees no replies

**Likely causes:**

1. **Lua bridge polling is broken.** The `main.lua` polling loop
   isn't reading from `Bridge/Outbox/`.
2. **Outbox is disabled in config.** `Bridge:OutboxEnabled =
   false`.
3. **Disk full or runtime root unwritable.** The sidecar's outbox
   write is failing silently to `Bridge/Failed/`.

**Diagnose:**

```powershell
$outbox = "$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bridge\Outbox"
Get-ChildItem $outbox -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object Name, LastWriteTime, Length -First 5

# Failed envelopes (sidecar-side write errors)
$failed = "$env:LOCALAPPDATA\Pal\Saved\PalLLM\Bridge\Failed"
Get-ChildItem $failed -ErrorAction SilentlyContinue
```

If `Outbox/` has fresh files but Lua never picks them up, the bridge
is broken on the Lua side. Check
`mod/ue4ss/Mods/PalLLM/Scripts/main.lua` is loaded (UE4SS console
should log "[PalLLM] mod loaded") and that the `runtime-root` path
in the Lua config matches the sidecar's.

**Fix:**

- Replay envelopes:
  `powershell -File scripts/run-delivery-replay.ps1`
- Restart the Lua bridge: in UE4SS console, type
  `mod restart PalLLM`.
- If `Bridge/Failed/` has entries, read the per-envelope failure
  reason in the JSON; usually a malformed cue plan or a write
  permission issue.

## Drift audit is failing in CI but passes locally

**Likely causes:**

1. **You forgot to regenerate the OpenAPI snapshot** after a route
   change.
2. **One doc count is stale** — README, ROADMAP, ARCHITECTURE,
   API.md, CODE_MAP, HANDOFF must agree.
3. **A doc's `Last audited:` stamp is older than 45 days.**
4. **A markdown link points at a moved or renamed file.**

**Diagnose:**

```powershell
# Run the same gates locally
powershell -File scripts/run_full_audit.ps1 -SkipCoverage -SkipSbom -SkipPackaging
# Open the latest report
$latest = Get-ChildItem artifacts/full-audit | Sort LastWriteTime -Desc | Select -First 1
notepad "$($latest.FullName)/RESULTS.md"
```

The report names every failing gate plus the exact file/line.

**Fix:**

- OpenAPI: `powershell -File scripts/export-openapi.ps1`
- Stale count: open the failing doc, replace the stale number with
  the live one.
- Stale stamp: bump the `Last audited:` date if the doc is still
  accurate, or fix the doc and bump.
- Bad link: fix the link target or the path.

## Memory store is corrupt

**Symptom:** Sidecar starts but every chat says "I don't remember
us meeting" or the relationship tracker resets.

**Likely cause:** `runtime-root/session.json` is malformed (rare —
usually only happens after an interrupted crash during persistence).

**Diagnose:**

```powershell
$session = "$env:LOCALAPPDATA\Pal\Saved\PalLLM\session.json"
Get-Content $session -Raw | ConvertFrom-Json
# If this throws, the file is corrupt.
```

**Fix:**

```powershell
# Quarantine the corrupt file (don't delete — it may be partially
# salvageable for the recovery script)
Move-Item $session "$session.corrupt-$(Get-Date -Format yyyyMMdd-HHmmss)"

# Restart the sidecar — it'll create a fresh session.json
# To recover history from the corrupt file:
powershell -File scripts/recover-palllm.ps1 -SessionFile "$session.corrupt-*"
```

## Inference is using too much GPU memory

**Symptom:** Other GPU-heavy apps (the game itself, video
encoding, etc.) are slow when PalLLM is running.

**Likely cause:** Live inference is using a model that's larger
than your GPU can comfortably co-resident with Palworld.

**Diagnose:**

```powershell
# Hardware tier reading
curl -s http://localhost:5088/api/hardware | ConvertFrom-Json | Format-List
# DetectedTier vs EffectiveTier
```

**Fix:** Switch to a smaller model. The `MODEL_COLLABORATION.md`
doc has a table of recommended models per tier. For a single-GPU
mid-range box, `qwen3.6:0.6b` or `gemma3:4b` co-resides with
Palworld; `qwen3.6:35b-a3b` does not.

## Disk is filling up under runtime-root

**Likely cause:** Bridge retention caps are too high or the
self-healing watchdog is disabled.

**Diagnose:**

```powershell
$root = "$env:LOCALAPPDATA\Pal\Saved\PalLLM"
Get-ChildItem $root -Recurse |
    Group-Object DirectoryName |
    Select-Object @{N='Dir';E={$_.Name}},
                  @{N='Count';E={$_.Count}},
                  @{N='Bytes';E={($_.Group | Measure-Object Length -Sum).Sum}} |
    Sort-Object Bytes -Descending | Select-Object -First 10
```

**Fix:** Lower retention caps in config (`Bridge:OutboxMaxFiles`,
`ArchiveMaxFiles`, `FailedMaxFiles`, `DiagnosticsMaxFiles`) or
manually clear `Bridge/Archive/` (it's history, safe to delete).

## Last-resort recover

If the runtime is in a state nothing else fixes, the human-driven
recovery path resets without losing player data:

```powershell
# Stops the sidecar, archives the current runtime root, fresh start
powershell -File scripts/recover-palllm.ps1
```

This is intentionally NOT something the runtime does automatically
— see [`adr/0001-deterministic-first-reply-pipeline.md`](adr/0001-deterministic-first-reply-pipeline.md)
for why automatic destructive recovery is off the table.

## Workspace clutter / disk pressure

If the repo folder is getting noisy or large, start with a preview:

```powershell
powershell -File scripts/pal-cleanup.ps1
```

The default candidate set is old `artifacts/full-audit/*/coverage`
HTML output. It keeps each audit directory and `RESULTS.md` so
handoff/changelog links remain valid. If the preview looks right:

```powershell
powershell -File scripts/pal-cleanup.ps1 -Apply
```

Add `-BuildOutputs` to include `src/**/bin`, `src/**/obj`,
`tests/**/bin`, and `tests/**/obj`.

## Related

- [`OPERATIONS.md`](OPERATIONS.md) — routine operator reference (not
  incident response)
- `scripts/export-support-bundle.ps1` — packages everything an
  incident reporter needs (logs, latest health, latest evidence,
  redacted session) into a zip
- [`API.md`](API.md) — full HTTP surface for diagnostic curls
- [`OBSERVABILITY.md`](OBSERVABILITY.md) — wire up tracing if the
  health endpoints aren't enough
