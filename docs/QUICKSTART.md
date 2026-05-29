# PalLLM Quickstart

Audience: first-time user who wants a working chat reply in under five
minutes. Two paths below - pick the one that matches where you are:

- **Player path** - you have Palworld installed and want PalLLM running
  in-game. Follow [In Palworld](#in-palworld).
- **Developer path** - you cloned the repo and want to see the runtime
  produce a reply without touching the game. Follow
  [Standalone sidecar](#standalone-sidecar).

Last audited: `2026-05-16`

This is a tutorial in the [Diataxis](https://diataxis.fr/) sense - the
goal is learning through doing. For day-to-day operations, open
[`OPERATIONS.md`](OPERATIONS.md). For the big-picture design, read
[`ARCHITECTURE.md`](ARCHITECTURE.md).

## In Palworld

Prerequisites:
- **Palworld** (any recent build).
- **UE4SS v3.x or newer** installed into your Palworld `Win64` folder.
  [UE4SS releases](https://github.com/UE4SS-RE/RE-UE4SS/releases).
- **.NET 10 Runtime** only if you are running a framework-dependent package
  or starting the sidecar from source. Official release zips now bundle a
  self-contained sidecar under `sidecar\publish\` by default.

Steps:

1. **Download the latest release zip** from the project's GitHub
   Releases page. Extract somewhere writable (Documents or Desktop).
2. **Double-click `play.bat`** in the extracted folder. It auto-detects
   your Palworld install, installs or refreshes the mod, starts or reuses
   the sidecar, primes the active inference lane when warmup is enabled,
   runs doctor, writes `Runtime/LaunchEvidence/latest-player-launch.json`
   plus `.md`, opens the dashboard, and launches Palworld.
   If auto-detect fails, re-run from a terminal with the path:
   ```powershell
   play.bat -PalworldPath "D:\SteamLibrary\steamapps\common\Palworld"
   ```
3. **If you need a support bundle**, double-click `support.bat`. It writes
   `Runtime/SupportEvidence/latest-support-bundle.zip` plus `.json` with the
   latest launch, health, bridge-proof, and release-readiness evidence.
4. **If you want the manual path instead**, install then start the sidecar:
   ```powershell
   install.bat
   powershell -File scripts\start-sidecar.ps1
   powershell -File scripts\doctor.ps1 -RunSmoke
   ```
   Official release zips now bundle that sidecar by default; the same manual
   flow also works from the repo.
5. **Watch the UE4SS console.** When Palworld launches it prints:
   ```
   [PalLLM] UE4SS bridge booting
   [PalLLM][Compat] PalGameStateInGame=present | PalCharacter=present | ...
   ```
   The `Compat` line shows which core Palworld classes resolved on your
   current game version. Any `missing` entry means that specific event
   type won't fire until the hook name is updated - the rest of the mod
   still works.
6. **Chat in-game.** Replies render through the UE4SS screen-message
  layer by default. To enable a native HUD widget bind, TTS playback,
  or the guarded action executor, see
  [`OPERATIONS.md`](OPERATIONS.md#opt-in-feature-matrix).

That's the complete player path. The rest of this document is for
developers who want to see the sidecar run without Palworld at all.

## Standalone sidecar

Audience: first-time user who just cloned the repo and wants a chat
reply from the deterministic fallback director without installing
Palworld, a model, or anything else.

What you'll have at the end:

- A running PalLLM sidecar at `http://localhost:5088`.
- A chat reply from the deterministic fallback director (no LLM
  installed yet).
- A `Bridge/Outbox/*.json` envelope the UE4SS mod would render.
- A `session.json` file that proves memory persistence is alive.

Prerequisites:

- .NET 10 SDK (`dotnet --list-sdks` must include a `10.x.x`).
- Windows PowerShell, or any shell on Linux/macOS with `dotnet` on
  `PATH`.
- About 200 MB free disk under `%LOCALAPPDATA%\Pal\Saved\PalLLM` (or
  `~/.local/share/Pal/Saved/PalLLM` on Linux once you set
  `PalLLM:PalSavedRoot`).

No local inference server, no game, and no UE4SS required. The
deterministic fallback director produces a useful reply with zero
external models.

## 1. Build and run

```powershell
cd D:\Coding\PalLLM
dotnet build PalLLM.sln
dotnet run --project src\PalLLM.Sidecar\PalLLM.Sidecar.csproj
```

You should see:

```
Now listening on: http://localhost:5088
Application started. Press Ctrl+C to shut down.
```

Leave that terminal running. Open a second one for the rest of the tutorial.

## 2. Confirm the sidecar is alive

```powershell
curl http://localhost:5088/api/health
```

The response is a `RuntimeHealth` JSON document. Look for:

- `"AdapterName": "Palworld (UE4SS bridge)"`
- `"Status": "PalLLM sidecar is ready."`
- `"InferenceConfigured": false` - expected; no model wired yet.
- `"LoadedPackCount": 0` - expected; no narrative packs shipped yet.

- `"BridgeLoop": { "Status": "idle" }` - expected before the first tracked
  reply is delivered through the bridge.

Bonus: `http://localhost:5088/` serves the read-only `PalLLM Field Console` dashboard. `http://localhost:5088/metrics` returns Prometheus exposition-format counters and gauges. **For non-technical users**, `http://localhost:5088/welcome.html` is a friendly chat surface (Pal avatar, voice in/out, accessibility toggles, installable as a PWA) — same `/api/chat` underneath, no jargon on the surface.

Optional: `curl http://localhost:5088/api/bridge/proof` will report a
pre-bridge state such as `awaiting_bridge_boot` in this standalone path, which
is expected because Palworld and the UE4SS bridge are not attached yet.

## 3. Seed a world snapshot

PalLLM's fallback strategies key off the world snapshot. Feed it a minimal one so the next chat reply has something to ground on.

```powershell
curl -X POST http://localhost:5088/api/snapshot `
  -H "Content-Type: application/json" `
  -d '{
    "IsWorldLoaded": true,
    "WorldName": "Palpagos",
    "IsInBase": true,
    "TimeOfDay": "night",
    "Characters": [
      {
        "Id": 7,
        "DisplayName": "Camp Guardian",
        "Species": "camp-guardian",
        "Traits": ["calm", "loyal"]
      }
    ]
  }'
```

## 4. Ask your first question

```powershell
curl -X POST http://localhost:5088/api/chat `
  -H "Content-Type: application/json" `
  -d '{
    "CharacterId": 7,
    "UserMessage": "How should we prepare this camp for the night?",
    "TaskTag": "chat_camp"
  }'
```

The reply includes:

- `"ResponsePath": "fallback_inference_disabled"` - inference is off, so the deterministic director produced the reply.
- `"FallbackStrategy": "crafting-discipline"` - the keyword match plus the camp-at-night context picked this of the 19 hardcoded strategies.
- `"AssistantMessage"` - a multi-sentence reply.
- `"Presentation"` - paired visual + audio cue metadata the UE4SS side would render.
- `"RequestId"` - a short correlation id that also appears in the outbox envelope and logs.

## 5. Inspect the outbox

Every successful chat writes a JSON envelope to `%LOCALAPPDATA%\Pal\Saved\PalLLM\Bridge\Outbox\`. List them:

```powershell
curl http://localhost:5088/api/bridge/outbox
```

You should see one `chat_reply-*.json` file. The UE4SS Lua bridge would normally consume this, render the assistant message in-game, and archive the file.

## 6. Prove memory persisted

Save the session explicitly:

```powershell
curl -X POST http://localhost:5088/api/session/save
```

Now stop the sidecar (`Ctrl+C`) and start it again:

```powershell
dotnet run --project src\PalLLM.Sidecar\PalLLM.Sidecar.csproj
```

After startup, call memory recall:

```powershell
curl -X POST http://localhost:5088/api/memory/recall `
  -H "Content-Type: application/json" `
  -d '{"CharacterId": 7, "Query": "camp night", "Limit": 5}'
```

Your earlier chat turn and the fallback reply are still in memory. PalLLM auto-loaded `session.json` on startup.

## What just happened

- The sidecar bound its options from `src/PalLLM.Sidecar/appsettings.json`, validated them on startup, and created the `Bridge/`, `Models/`, `Packs/`, `TTS/`, and `Screenshots/` directories under the runtime root.
- Your snapshot post updated the adapter state; the chat endpoint read it when building the system prompt.
- Because inference is disabled, the `FallbackBehaviorEngine` picked a strategy from a deterministic ranking of 19 candidates - no LLM token spent.
- `PresentationCuePlanner` generated audio + visual cue metadata from the chosen strategy.
- `ActionIntentPlanner` checked the automation allowlist (empty by default) and attached nothing.
- The outbox was written, and the memory + relationship stores mutated.
- Session autosave noticed the mutation and will flush `session.json` on its next tick.

## Next steps

- **Turn on live inference**: run any HTTP server implementing the JSON
  chat-completions schema, then set `PalLLM:Inference:Enabled=true` and
  point `BaseUrl`/`Model` at your server. Restart the sidecar.
- **Turn on vision**: set `PalLLM:Vision:Enabled=true` on a server that
  also supports `image_url` content parts, and attach `ImageBase64` to
  a chat request.
- **Turn on TTS**: run any HTTP server that returns audio bytes and set
  `PalLLM:Tts:Enabled=true` plus the server `BaseUrl`. Keep
  `Tts:RequestFormat=simple` for `{ "text", "voice" }` adapters, or set
  `Tts:RequestFormat=openai_speech` for `/v1/audio/speech` servers.
- **Hook into Palworld**: install UE4SS, run `scripts/install-dev-mod.ps1`, and the Lua side takes over chat capture + outbox rendering.

For production concerns - health probes, metrics scraping, tuning retention caps, safely enabling the action executor - read `docs/OPERATIONS.md` next.
