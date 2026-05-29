# What is PalLLM?

Last audited: `2026-05-22`

> **One-sentence answer:** PalLLM gives every companion in *Palworld* its
> own local AI voice - on your own computer, with no cloud account, no
> subscription, and no data leaving your machine by default.

## For players (plain English)

Imagine your Pal actually **reacting** to what you're doing.

- You're low on health mid-raid -> your companion yells "Hang in there!"
  without you asking.
- You lost your crafting queue at 2am -> another companion remembers what you
  were building and nudges you when you log back in.
- You tell a companion "stop mining and help me fight" -> the companion
  understands and a safe, allowlisted action signal reaches the
  game.
- You take a screenshot of a rare biome -> the companion describes what
  she sees and stores it in her memory for next time.

Today's Palworld companions are scripted. PalLLM gives them a live
voice that learns you, remembers what matters, and stays in
character.

## Why is that interesting?

1. **100% local by default.** No OpenAI account, no cloud bill, no
   "we've updated our privacy policy" emails. Your conversations
   stay on your PC. The one exception - you opt in to point PalLLM
   at your own llama.cpp (default) or vLLM (high-config) server -
   is explicit and fully inspectable (`GET /api/airgap/verify`).
2. **Zero-inference fallback.** Even if you never set up an AI
   model, PalLLM still answers. A deterministic "director" picks
   between 19 hand-authored reply strategies so the companion is
   never mute. Cost of a chat reply with no AI configured: zero.
3. **Character continuity across sessions.** Relationships persist.
   If you treated a companion kindly yesterday, that carries over. The
   cross-session "lifetime memory" tracks peak affinity, first-seen
   date, session count, and dominant mood so the companion greets
   you with genuine context.
4. **Scales to your hardware.** CPU-only laptop? PalLLM detects it
   and picks a 1B-class model or falls back to the deterministic
   director. 2-GPU workstation? Full Duo-mesh (two models
   cooperating) unlocks automatically. Hit
   `GET /api/hardware` and the runtime tells you what it's doing.
5. **You can talk to the companion from any chat app.** PalLLM
   exposes an MCP server, so Claude Desktop / ChatGPT Desktop /
   VS Code / other MCP-aware clients can call the same 38 tools
   your in-game companion uses. Ask your desktop AI "what's
   happening in my base right now?" and it actually knows.

## What does it look like in practice?

One-click install:

```
install.bat   <- detects Palworld, installs UE4SS mod, runs doctor
play.bat      <- starts sidecar, warms AI lane, opens game
support.bat   <- exports a zip of health evidence for troubleshooting
recover.bat   <- stops, cleans, restarts
```

Open your browser to `http://localhost:5088` - you get the
**Field Console**: a live dashboard that shows every companion's
mood, affinity trend, the last bridge event, active model tier,
privacy posture, and an inline chat panel you can use to talk to
the companion without opening the game.

## Is it safe?

Three short answers:

- **Network safety:** PalLLM binds to `localhost` by default. Nothing
  leaves your machine unless you flip a specific, documented switch
  (`PalLLM:Inference:Enabled=true` + set an endpoint). See
  [`PRIVACY.md`](PRIVACY.md) for the full inventory of every
  data-emitting surface, classified as *never-leaves* /
  *only-with-opt-in* / *leaves-by-default*.
- **Game safety:** The Lua bridge is **one-way + advisory**. PalLLM
  never reaches into Palworld directly. Anything in-game happens
  through an explicit allowlist of guarded actions (`PalLLM:Automation:AllowedActions`),
  and every action emits a structured proof record the player can
  audit.
- **Upstream safety:** If live inference breaks, the companion
  still answers. Circuit breaker, thermal gate, per-character rate
  limiter, self-healing watchdog, and the deterministic fallback
  form a 5-tier defence in depth so a player is never left with a
  mute companion.

## Who is it for?

- **Players** who want a richer in-game companion without signing up
  for another cloud service.
- **Modders** who want a working example of UE4SS <-> .NET sidecar
  integration with a portable adapter seam they can lift into other
  games.
- **AI hobbyists** who want a local LLM runtime with observability,
  role-aware dispatch, proof packets, and a genuine
  hard-code-promotion loop.
- **Developers** who want to harvest individual capabilities -
  the Duo cooperation-pattern planner, proof-packet provenance
  format, disagreement detector, or privacy-posture inventory -
  into their own projects. See [`HARVEST.md`](HARVEST.md) for
  recipes.

## What's the catch?

- **Palworld + UE4SS is the live target.** The portable adapter
  seam is neutral, but the shipped mod targets Palworld. Other games
  would need their own `IGameAdapter` implementation. The seam is
  documented so this is tractable if you want to port.
- **You provide the inference model.** PalLLM doesn't ship a
  model - it connects to your local llama.cpp (default) or vLLM
  (high-config) install. Deterministic fallback answers when you
  don't have one.
- **The honest roadmap number is `76.2%` today.** The sidecar
  runtime is production-ready; the remaining work is in the
  in-Palworld native HUD binding, native audio playback, and
  action executor coverage. See [`ROADMAP.md`](ROADMAP.md) for
  the full ship-readiness math.

## How do I try it?

1. Download the latest release zip.
2. Unzip anywhere with write access.
3. Double-click `install.bat`. Follow the prompts.
4. Double-click `play.bat`. The companion loads with Palworld.

That's it. [`MCP_QUICKSTART.md`](MCP_QUICKSTART.md) covers the
5-minute path to using PalLLM from a desktop AI client. For the
gritty details, [`QUICKSTART.md`](QUICKSTART.md) walks through
every opt-in toggle.

## Where to go from here

- **Want the technical tour?** [`ARCHITECTURE.md`](ARCHITECTURE.md).
- **Want to contribute?** [`../CONTRIBUTING.md`](../CONTRIBUTING.md).
- **Want to harvest a piece into your own project?** [`HARVEST.md`](HARVEST.md).
- **Are you a coding agent?** [`../AGENTS.md`](../AGENTS.md).
- **Want the full feature inventory?** Hit
  `GET /api/features` on a running sidecar - 121 entries, every
  subsystem, live status.


