# `mod/`

Game-side bridge. Currently Palworld + UE4SS only — see
[`../docs/adr/0003-one-way-advisory-bridge.md`](../docs/adr/0003-one-way-advisory-bridge.md)
for why the bridge is the way it is.

## Layout

```
mod/
└── ue4ss/Mods/PalLLM/
    ├── Scripts/
    │   ├── main.lua          ← event producer + outbox consumer + action executor
    │   └── helpers/...       ← shared Lua utilities
    ├── enabled.txt           ← UE4SS load marker
    └── ...
```

## What `main.lua` does

1. **Event producers** — write JSON envelopes into
   `runtime-root/Bridge/Inbox/` for each in-game event the
   sidecar should hear about (player chat, world snapshot,
   combat outcome, milestone, screenshot ready, widget probe).
2. **Outbox consumer** — polls `runtime-root/Bridge/Outbox/`
   for new chat-reply envelopes and renders them in-game
   (HUD widget, in-world text, optional speech audio).
3. **Action executor** — when an envelope contains an
   `Action` intent and the action type is on the Lua-side
   allowlist, executes the action. Default allowlist is empty
   — the operator opts in per action type.

The bridge is **filesystem-only and one-way at any moment**.
The Lua side never makes HTTP calls to the sidecar; the
sidecar never injects code into Palworld. See
[`../docs/adr/0003-one-way-advisory-bridge.md`](../docs/adr/0003-one-way-advisory-bridge.md).

## Wire schemas

- Inbound (Lua → sidecar):
  [`../docs/schemas/bridge-event-envelope.schema.json`](../docs/schemas/bridge-event-envelope.schema.json)
- Outbound (sidecar → Lua):
  [`../docs/schemas/outbox-envelope.schema.json`](../docs/schemas/outbox-envelope.schema.json)

## Install

The release ZIP's `install.bat` symlinks this directory into
the player's UE4SS Mods folder. For dev iteration:

```powershell
pwsh ../scripts/install-dev-mod.ps1
```

## Constraints

- **Lua 5.4** (UE4SS's current public-release runtime). The
  `.luacheckrc` and the `lua.yml` workflow are pinned to 5.4.
- **`pcall`-guarded engine calls.** A bad cast in a UE4SS API
  call shouldn't crash the bridge.
- **No global writes.** Local-scoped helpers only.
- **`snake_case`** names matching existing convention.

When the bridge protocol changes (new event type, new action
type), the schemas above + the corresponding C# code in
`src/PalLLM.Domain/Integration/Contracts.cs` must update in
lockstep. The audit doesn't enforce Lua-C# sync directly —
that's a contributor responsibility called out in
[`../CONTRIBUTING.md`](../CONTRIBUTING.md) § "Changing runtime
contracts".
