---
name: Palworld / UE4SS compatibility report
about: A game patch or UE4SS update changed a hook that PalLLM's Lua layer depends on.
title: "[Compat] "
labels: ["compat", "triage"]
assignees: []
---

<!-- PalLLM's Lua bridge uses `register_hook_safely` so a renamed class in a
game patch logs a line instead of crashing. This template is for reporting
which hook(s) broke so we can update the default target paths. -->

## Versions

- Palworld version:
- UE4SS version:
- PalLLM commit SHA:

## `[PalLLM][Compat]` line from the UE4SS console at mod boot

<!-- Paste the full compat line. Example:
[PalLLM][Compat] PalGameStateInGame=present | PalCharacter=missing | ...
-->

## Which events stopped arriving

<!-- Check `GET /api/health` and `Bridge/Archive` to see which event
types you're still receiving. -->

- [ ] `chat_message`
- [ ] `base_discovered`
- [ ] `combat_start` / `combat_end`
- [ ] `pal_status`
- [ ] `weather_change`
- [ ] `raid`
- [ ] `ui_probe`
- [ ] Travel samples
- [ ] Production sampler
- [ ] Something else (specify):

## Known replacement hook path (if you've found it)

<!-- Many Palworld patches just rename a class. If you've already
discovered the new path via UE4SS Live View, paste it here so we can
update the default. -->

## Additional context
