# Sample personality packs — pick a voice in 30 seconds

Last audited: `2026-05-21`

PalLLM ships four reference personality packs under
[`samples/packs/`](../samples/packs/) so a new player has working,
schema-valid examples to load instead of the abstract spec in
[`PACK_AUTHORING.md`](PACK_AUTHORING.md).

Each pack is a complete, validated drop-in: the canonical
`pack.json` manifest, the system-prompt fragment (`prompt.md`),
and a TTS voice-hint file. Pre-computed `ContentHash` so the
runtime accepts them without re-hashing.

Read [`PACK_AUTHORING.md`](PACK_AUTHORING.md) for the format
deep-dive. Read this doc to pick a starting voice and copy it
into your runtime.

## The four sample voices

| Pack | Voice | When you'd want this |
|---|---|---|
| [`companion-warrior`](../samples/packs/companion-warrior/) | Battle-hardened. Direct verbs. Plans, not panic. | Combat-heavy playstyle. You want a steady second pair of eyes that flags risk early without being a doom narrator. |
| [`companion-scholar`](../samples/packs/companion-scholar/) | Quiet noticer. Points at patterns. Comfortable in silence. | Exploration playstyle. You want the companion to feel like a calm second-brain rather than an active tactician. |
| [`companion-healer`](../samples/packs/companion-healer/) | Warm, grounded. Watches the player as a person. | Long sessions. You want someone who notices tiredness and hunger before the HP bar does, and sits with you after a loss. |
| [`companion-trickster`](../samples/packs/companion-trickster/) | Sharp and playful. Funny when funny helps; quiet when it doesn't. | You want the world to feel lived-in and a little ridiculous without the bit overstaying its welcome. |

All four packs are tagged `family-friendly`. The warrior pack is
also tagged `combat-heavy` because the prompt is oriented toward
fights.

## Loading a pack

```powershell
# 1. Copy the pack into your runtime root (default: %LOCALAPPDATA%\Pal\Saved\PalLLM\Packs\)
$dest = Join-Path $env:LOCALAPPDATA 'Pal\Saved\PalLLM\Packs\personalities\companion-warrior'
New-Item -ItemType Directory -Path $dest -Force | Out-Null
Copy-Item -Path samples/packs/companion-warrior/* -Destination $dest -Recurse -Force

# 2. Reload the pack store on the running sidecar
Invoke-RestMethod -Uri 'http://localhost:5088/api/packs/reload' -Method Post

# 3. Verify the pack is loaded
Invoke-RestMethod -Uri 'http://localhost:5088/api/packs' | Select-Object -ExpandProperty packs
```

## Customizing a pack

The fastest path:

1. Copy one of the sample directories to your work area.
2. Edit `prompt.md` (the system-prompt fragment, hard-capped at
   8 KiB).
3. Optionally edit `voice-hint.md` for TTS pacing, or add local
   opt-in assets declared in `pack.json`: `VoiceRefPath` for a
   voice reference, `VoiceConsent` / `VoiceConsentNotes` for voice
   provenance, `LoraAdapterPath` for a hash-pinned local `.safetensors`
   adapter, and `MemoryNamespace` for pack-specific long-term memory
   identity.
4. Bump `Version` in `pack.json`.
5. Recompute the `ContentHash`:

   ```powershell
   pwsh ./scripts/compute-pack-hash.ps1 ./my-pack -Update
   ```

6. Drop the pack into the runtime and reload as above.

The validator will refuse a pack whose declared `ContentHash`
doesn't match the on-disk content — this catches accidental edits
without requiring a key infrastructure. The
`compute-pack-hash.ps1` helper takes a one-shot `.bak` of
`pack.json` on its first overwrite for safety.

## What goes in `prompt.md`

The system-prompt fragment is **not** a screenplay. It's a list
of constraints PalLLM appends to its base prompt:

- **Voice.** Cadence, register, what the voice does *not* sound
  like.
- **What you talk about, in order.** Priorities for the next
  reply. Two or three numbered items, not ten.
- **What you do not do.** Anti-patterns that would otherwise be
  the LLM's natural default.
- **A note on quiet moments.** What "presence without speech"
  looks like in this voice.

Each sample pack follows this shape. The constraints are
hand-tuned so the warrior pack and the trickster pack feel
*different* even when the underlying model is the same.

## Hash details (for tooling integration)

The `ContentHash` is the lower-case hex of:

```
SHA-256( for each tracked file (relative paths sorted ordinal):
    UTF-8(relativePath) || 0x00 || file-bytes || 0xFF )
```

Tracked files are: `PromptPath`, `VoiceHintPath` (if set),
`VoiceRefPath` (if set), `PortraitPath` (if set),
`LoraAdapterPath` (if set), and any `AudioSamples` entries. The
manifest itself (`pack.json`) is *not* hashed — that's why the
hash can be embedded into the manifest without a bootstrap cycle.

Optional voice references must stay local to the pack, stay at or
below `10 MiB`, use `.wav`, `.mp3`, `.flac`, `.ogg`, `.opus`,
`.m4a`, or `.aac`, and declare `VoiceConsent` as one of
`self_recorded`, `licensed`, `synthetic`, or `public_domain`.
`VoiceConsentNotes` can hold a short provenance note for reviewers.
Optional `AudioSamples` entries stay `.ogg` / `.opus` and are also
capped at `10 MiB` each. Optional LoRA/personality adapters must stay
local, use `.safetensors`, and are hash-covered; remote adapter URLs
are rejected. `MemoryNamespace` is a kebab-case identifier only - it
does not create a memory store by itself.

Source of truth: `PersonalityPackValidator.ComputeContentHash`
in [`../src/PalLLM.Domain/Packs/PersonalityPack.cs`](../src/PalLLM.Domain/Packs/PersonalityPack.cs).
The `compute-pack-hash.ps1` helper mirrors that algorithm
exactly so packs authored on Linux / macOS get bit-identical
hashes to packs authored on Windows.

## Validating a pack programmatically

```powershell
# POST the pack root to the validator endpoint and read back the
# structured check list. Useful for CI on a community pack repo.
$body = @{ packRoot = 'D:\my-packs\companion-stoic' } | ConvertTo-Json
Invoke-RestMethod -Uri 'http://localhost:5088/api/packs/validate' `
                  -Method Post -ContentType 'application/json' -Body $body
```

The response includes per-check pass/fail rows so you can wire
this into a pre-commit hook or a CI check on a community-pack
repository.

## Why these four voices?

The four samples cover the four most-asked-for companion modes
across community discussions:

- **Warrior** — tactical / "give me information I can act on".
- **Scholar** — exploration / "tell me what I'm missing".
- **Healer** — long-session / "look out for me as a person".
- **Trickster** — vibe / "make this feel alive".

Three of the four are explicitly low-talk by default (warrior /
scholar / healer); the trickster is the only one that
willingly fills silence, and only when the player is riffing
back. That's deliberate — silence is part of the voice, and a
companion that always-narrates is the companion the player
mutes.

The deterministic-fallback layer ([`PROMPT_CARDS.md`](PROMPT_CARDS.md))
applies on top of any of these voices: even with no LLM wired,
the fallback strategies still respond, just with the pack's
voice constraints layered into the prompt that the fallback
generator consults.
