# Easy mode — the absolute-beginner guide

Last audited: `2026-05-23`

If you've never modded a game before, never used PowerShell, and
just want PalLLM working right now — this is the page for you.

Everything below assumes Windows. (Linux + macOS work too, but
Palworld itself is Windows-only, so the player flow is described
in Windows terms here.)

## The 60-second path

1. Download the **PalLLM-vX.Y.Z.zip** from the project's
   GitHub Releases page.
2. **Right-click the zip → Extract All...** to anywhere writable.
   Desktop or Documents is fine.
3. Open the extracted folder. Double-click **`play.bat`**.
4. Wait ~10 seconds. Your browser opens to
   <http://localhost:5088>. Palworld launches.
5. In the dashboard, click any character and type "hi". You'll
   get a reply.

That's it.

### Even easier: skip the dashboard entirely

If the dashboard feels like a spreadsheet to you (it kind of
is — it's for power users), open the friendly version instead:

> <http://localhost:5088/welcome.html>

You'll see a Pal face, one big chat box, and three buttons.
Type or speak (mic button on the left), and your Pal answers.
That's the whole product on one page. Click "Install PalLLM
Companion" in your browser's address bar to add it to your
Start menu like a real app.

## What just happened, in plain English

- `play.bat` checked your computer for Palworld and found it.
- It copied the PalLLM mod into Palworld's mods folder.
- It started a small program (called the "sidecar") that runs
  the AI companion on your computer.
- It opened a webpage where you can chat with your companion.
- It launched the game so you can also chat in-world.

Nothing is sent to the cloud. Your chat history lives in
`%LOCALAPPDATA%\Pal\Saved\PalLLM\` on your computer.

## Common questions

### Do I need an AI account or subscription?

No. PalLLM ships with 19 hand-written reply strategies that work
without any AI model installed. The companion responds even
when nothing is set up.

If you have a powerful GPU and want richer replies, you can
install a free local AI model server (llama.cpp is the default;
vLLM for high-config GPUs) and point it at a GGUF you've
downloaded. PalLLM will use it automatically. See
**docs/QUICKSTART.md** for the 5-minute walkthrough.

### What if Windows says "Windows protected your PC"?

Windows SmartScreen sometimes flags new `.bat` files. Click
**More info** → **Run anyway**. The release zip is signed when
distributed through the official GitHub Releases page; you can
verify by following **SECURITY.md**.

### What if play.bat doesn't find Palworld?

Pass the path manually:

```
play.bat -PalworldPath "D:\SteamLibrary\steamapps\common\Palworld"
```

Replace the path with wherever your Palworld is installed. To
find it: open Steam → right-click Palworld → **Manage** →
**Browse local files**.

### What if I want to remove PalLLM?

Double-click **`uninstall.bat`** in the same folder.

By default this removes the mod but **keeps your chat history**
and any custom companion packs. If you want a complete wipe,
double-click `uninstall.bat /full` (or run it from a terminal
with `/full`). To preview what would happen first, use
`uninstall.bat /preview`.

### Where does my chat history live?

`%LOCALAPPDATA%\Pal\Saved\PalLLM\session.json`. You can copy
this folder to back it up or move it to another machine.

### Something went wrong. What now?

Double-click **`support.bat`**. It bundles every relevant log,
health snapshot, and evidence artifact into a zip under
`%LOCALAPPDATA%\Pal\Saved\PalLLM\SupportEvidence\
latest-support-bundle.zip`. Attach that zip when reporting an
issue — it's the highest-signal thing a maintainer can get.

To self-diagnose, run:

```powershell
pal doctor
```

Reports PASS / WARN / FAIL per check with a suggested fix for
any failure. (No `pwsh ./pal.ps1` — the `pal.bat` wrapper at
the repo root forwards everything for you.)

### Can I see the companion say something right now without
launching the game?

```powershell
pal hello
```

Sends "hi" to the running sidecar and prints the reply. Works
even with inference disabled (deterministic fallback always
answers). If the sidecar isn't running, the command tells you
what to type next.

### How do I pick the right AI model for my GPU?

```powershell
pal models
```

Detects your hardware (CPU only / Hopper / Ada / Ampere /
Blackwell / etc.) and prints the recommended quantization
format with a one-line reason. For deep dives on the trade-offs
read `docs/QUANTIZATION.md`; for copy-pastable vLLM startup
snippets read `docs/BLACKWELL_RECIPES.md`.

### How do I edit the configuration file?

```powershell
pal config
```

Opens the active `appsettings.json` in your default editor.
Tells you where it lives if you need to find it manually. Most
settings can also be set via environment variables — see
`docs/ENV_VARS.md` for the full table.

### How do I reset everything?

`recover.bat` — stops the sidecar, archives stuck messages,
prunes old logs, and restarts cleanly. Doesn't delete your
chat history.

For a full factory reset (no chat history left), use
`uninstall.bat /full` then re-install.

### Can I remove generated junk safely?

```powershell
pal cleanup
```

This previews generated local clutter, mostly old HTML coverage
reports under `artifacts/full-audit/*/coverage`. If the list looks
right, run:

```powershell
pal cleanup -Apply
```

It keeps source files, docs, samples, release evidence, and audit
`RESULTS.md` files. Add `-BuildOutputs` if you also want to remove
`bin` / `obj` folders that `dotnet build` can recreate.

## Three things to know about privacy

1. **Nothing leaves your computer by default.** Inference,
   vision, TTS, telemetry, and remote MCP — all opt-in.
2. **`http://localhost:5088/api/privacy/posture`** lists every
   data-emitting surface and its current status. Open it in your
   browser to audit.
3. **You can run PalLLM completely offline.** Even on an
   air-gapped machine. The deterministic fallback director
   handles every chat without any network access.

## Where to go for more

| If you want to... | Read |
|---|---|
| 5-minute "first model" walkthrough | [`QUICKSTART.md`](QUICKSTART.md) |
| Plain-English tour of what PalLLM does | [`PITCH.md`](PITCH.md) |
| Common first-time questions | [`FAQ.md`](FAQ.md) |
| Connect Claude Desktop / VS Code / etc. | [`MCP_QUICKSTART.md`](MCP_QUICKSTART.md) |
| Cleanly remove PalLLM | [`UNINSTALL.md`](UNINSTALL.md) |
| Privacy details | [`PRIVACY.md`](PRIVACY.md) |
| Troubleshoot a specific issue | [`RUNBOOK.md`](RUNBOOK.md) |
| Tune performance for your hardware | [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) |
| Pick the right model quantization | [`QUANTIZATION.md`](QUANTIZATION.md) |

The **`pal.bat`** wrapper at the repo root means every command
in any of those docs that says `pwsh ./pal.ps1 X` can also be
typed as `pal X`. That includes:

```powershell
pal hello       # check the sidecar is talking
pal models      # what model should I run?
pal config      # edit settings
pal status      # rolling state of the install
pal cleanup     # preview generated clutter
pal doctor      # health check with PASS/WARN/FAIL per item
pal play        # boot sidecar + open dashboard
pal uninstall   # clean removal (preserves chat history)
pal help        # full grouped table of every verb
```

That's the entire surface. Five `.bat` files at the repo root
for the most common one-clicks; one `pal` command with grouped
verbs for everything else.

## Related

- [`../START_HERE.txt`](../START_HERE.txt) - even shorter
  ("just double-click play.bat")
- `PLAYER_README.txt` (generated and shipped only inside release
  zips by `scripts/package-release.ps1`) - the 60-second
  quickstart with troubleshooting
- [`FIRST_HOUR.md`](FIRST_HOUR.md) - the contributor on-ramp
  if you want to dive into the code
- [`UNINSTALL.md`](UNINSTALL.md) - the full uninstall
  walkthrough with all options
