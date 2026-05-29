# Narrative Pack Authoring

Audience: someone writing character lore and memory seeds for PalLLM, not a C# developer.

Last audited: `2026-05-21`

This is a how-to guide in the [Diátaxis](https://diataxis.fr/) sense. It assumes you already know *what* a pack is — you just want a working one in your runtime without reading the code. For the why, see [`ARCHITECTURE.md`](ARCHITECTURE.md). For the field-level reference, see [`../src/PalLLM.Domain/Packs/NarrativePackModels.cs`](../src/PalLLM.Domain/Packs/NarrativePackModels.cs).

## What a pack is

A pack is a JSON file PalLLM loads on startup to give specific characters authored personality, lore, and seeded memories. The runtime looks up a pack's character by `DisplayName` or `Alias` when building prompts, and splices the matched personality/backstory/traits into the system prompt.

Packs live under `%LOCALAPPDATA%\Pal\Saved\PalLLM\Packs\*.json`. The sidecar rescans them on startup and on `POST /api/packs/reload`.
That reload path uses the same `1,000,000` byte ceiling as the HTTP
validation endpoint: malformed pack files or JSON files above that size are
skipped instead of blocking the whole startup/reload pass.

## Starter pack (ships with the installer)

The repo ships a small, validated starter pack at
[`docs/examples/camp-guardian-pack.json`](examples/camp-guardian-pack.json). By default
`scripts/install-mod.ps1` and `install.bat` copy it into
`%LOCALAPPDATA%\Pal\Saved\PalLLM\Packs\` on first install so a new player
sees authored companion lore immediately instead of only the generic
deterministic fallback replies. Pass `-SkipSamplePack` to the installer to
opt out.

The installer never overwrites an existing `camp-guardian-pack.json` in the
runtime Packs folder, so editing your copy is safe — future installs
will leave your edits alone.

## Minimum viable pack

Save this as `%LOCALAPPDATA%\Pal\Saved\PalLLM\Packs\starter.json`:

```json
{
  "Name": "Starter Pack",
  "Version": "1.0",
  "Author": "You",
  "Description": "A one-character starter so PalLLM has authored lore to work with.",
  "Characters": [
    {
      "Id": "camp-guardian",
      "Name": "Camp Guardian",
      "Aliases": ["Night Watch"],
      "Role": "Camp guardian",
      "Personality": "Calm, observant, quietly protective.",
      "Backstory": "Spent many cold nights watching campfires burn low.",
      "Traits": ["calm", "loyal", "watchful"],
      "Skills": { "Cooling": 3, "Transport": 2 }
    }
  ],
  "Relationships": [],
  "MemorySeeds": []
}
```

Validate it before shipping:

```powershell
curl -X POST http://localhost:5088/api/packs/validate `
  -H "Content-Type: application/json" `
  --data-binary "@$env:LOCALAPPDATA\Pal\Saved\PalLLM\Packs\starter.json"
```

`IsValid: true` means the runtime will accept it. The response also reports `CharacterCount`, `RelationshipCount`, and `MemorySeedCount` for sanity. Invalid packs return `400` with a list of `{ Path, Message }` errors. Malformed JSON stays on a stable location-based contract such as `Pack JSON could not be parsed near line 1, byte 3.` rather than echoing serializer-specific exception prose. Oversized pack-validation bodies return `413 Payload Too Large`; the current cap is `1,000,000` bytes even when the upload is chunked or omits `Content-Length`.
That same `1,000,000` byte limit is what the runtime uses when it rescans pack
files from disk, so keep authored packs below the validation cap if you expect
them to load during startup or `POST /api/packs/reload`.

The validator also runs a deterministic publication-safety scan. It is not a
legal review, but it blocks obvious public-copy mistakes: claims that a pack is
official, endorsed, sponsored, approved, or certified by Palworld, Pocketpair,
Steam, or Valve; unrelated third-party franchise/IP references; third-party
model/runtime/vendor brand references; broad "multi-game platform" language;
and legal-safety, IP-neutrality, or compliance-certainty overclaims. The
franchise/IP and legal-overclaim lists are intentionally aligned with the
release public-copy audit, so a shareable pack cannot pass validation with
terms that the release scanner would reject later.
The unrelated-franchise denylist is deliberately conservative: do not use
well-known third-party games, films, comics, tabletop systems, anime, or
studio names as style shorthand. Describe the original voice, behavior, and
scenario directly instead.
Keep shareable packs original, scoped to PalLLM for Palworld, and clear that
they are unaffiliated user-authored content.

Reload the runtime without restarting:

```powershell
curl -X POST http://localhost:5088/api/packs/reload
curl http://localhost:5088/api/packs
```

`GET /api/packs` reports each loaded manifest through `PackSummary.FilePath`,
but that path is pack-root-relative (for example `companions/alias-pack.json`)
rather than an absolute machine-local path.

## Field reference

### Pack header

| Field | Type | Rule |
|---|---|---|
| `Name` | string | **Required**, non-blank |
| `Version` | string | Optional, defaults to `"1.0"` |
| `Description` | string | Optional |
| `Author` | string | Optional, defaults to `"Unknown"` |
| `Scenario` | object | Optional — `{ Theme, Summary, Tags[] }` |
| `Publish` | object | Optional — `{ ListingSummary, Homepage, SourceUrl, License }` |
| `Characters` | array | **Required**, at least one |
| `Relationships` | array | Optional |
| `MemorySeeds` | array | Optional |

### Character

| Field | Type | Rule |
|---|---|---|
| `Id` | string | **Required**, unique within this pack |
| `Name` | string | **Required** |
| `Aliases` | string[] | Optional; matched alongside `Name` when resolving lore |
| `Role` | string | Surfaced in prompts as "Role: …" |
| `Personality` | string | Surfaced as "Personality: …" |
| `Backstory` | string | Surfaced as "Backstory: …" |
| `Traits` | string[] | Surfaced as "Authored traits: trait1, trait2" |
| `Skills` | `Dictionary<string,int>` | Values must be `[0, 20]` |

### Relationship

| Field | Type | Rule |
|---|---|---|
| `CharacterA` | string | **Required**, must reference a `Character.Id` in this pack |
| `CharacterB` | string | **Required**, must reference a `Character.Id` in this pack |
| `Type` | string | Free-form (e.g. `"allied"`, `"rival"`, `"mentor"`) |
| `Opinion` | int | Must be in `[-100, 100]` |

### MemorySeed

| Field | Type | Rule |
|---|---|---|
| `CharacterId` | string | **Required**, must reference a `Character.Id` in this pack |
| `Content` | string | **Required**, non-blank |
| `Tags` | string[] | Optional |
| `Importance` | float | Must be in `[0, 1]` |

## Common authoring patterns

### Give a Pal a distinct voice

Keep `Personality` short and sensory — 1–2 sentences describing *how* they speak and act, not just what they like. "Quick to crack a joke, but always keeps one eye on the door." is more useful to the model than "friendly and alert".

### Seed a defining memory

A `MemorySeed` with `Importance` ≥ 0.7 surfaces reliably when the player asks related questions:

```json
{
  "CharacterId": "chill-guardian",
  "Content": "The player once brought me in from a storm after I was abandoned. I still remember their jacket smelled like pine.",
  "Tags": ["trust", "rescue", "storm"],
  "Importance": 0.8
}
```

The reflection consolidation pass will treat high-importance seeds as stable anchors across sessions.

### Use relationships for group dynamics

If your pack has three or more characters, add `Relationships` so the prompt builder can mention "the guardian and the scout are long-time friends" rather than treating them as strangers every time.

## Testing your pack

Once validated and reloaded:

1. Post a snapshot that includes your character (matching `DisplayName` or an alias):
   ```powershell
   curl -X POST http://localhost:5088/api/snapshot ...
   ```
2. Post a chat request with `CharacterId` or `CharacterName` pointing at your authored character.
3. Inspect the response's `SystemPrompt`. It should contain the character's `Role`, `Personality`, `Backstory`, and `Traits`.
4. Ask a question related to a high-importance `MemorySeed`. Check `MemoryMatches` in the response — the seed should surface.

If your character's lore isn't being picked up, the usual cause is a mismatch between `DisplayName` in the snapshot and `Name` / `Aliases` in the pack. The lookup is case-insensitive but requires an exact token match.

## Publishing

The `Publish` section is informational today — PalLLM doesn't index or browse remote packs. A future tooling pass may read `Homepage` and `SourceUrl`, so filling them in is cheap and forward-compatible.

## Schema source of truth

The live C# types at [`NarrativePackModels.cs`](../src/PalLLM.Domain/Packs/NarrativePackModels.cs) are the authoritative schema. This doc summarises the rules the validator enforces today; if it ever drifts from the code, the code wins.
