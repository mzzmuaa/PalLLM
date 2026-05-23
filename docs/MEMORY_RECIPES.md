# Memory recipes — long-running companion memory (2026)

Last audited: `2026-05-22`

A companion that doesn't remember last week's deaths is the
companion the player mutes. This doc covers the memory patterns
that have settled by mid-2026 and how each maps onto PalLLM's
existing single-tier `ConversationMemoryStore`.

Companion to [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md)
(modality APIs) and [`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md)
(tool-use patterns).

## What PalLLM does today

`ConversationMemoryStore` (see
[`../src/PalLLM.Domain/Memory/ConversationMemoryStore.cs`](../src/PalLLM.Domain/Memory/ConversationMemoryStore.cs))
is a **2000-entry sliding window** (`MaxEntries = 2_000`) persisted via
`Session.EnableAutosave`, with each remembered content body capped at
**4 KiB** (`MaxContentChars = 4 * 1024`) on write and import. It surfaces
the last N turns into the chat hot path so replies stay coherent across
a session without letting old oversized text bloat prompt assembly or
response payloads.

`ConversationMemoryStore.Recall(query, characterId, limit)` adds a
**deterministic semantic recall lane on top of the working store**.
Today's recall is pure-local and pure-deterministic:

1. **Dense lane**: an in-process FNV-1a bag-of-tokens projection of the
   query is scored against the same projection of every stored entry.
   Local-first, zero network, sub-millisecond.
2. **Exact-token rerank**: a tie-breaker pass that boosts entries
   sharing any literal token with the query, so named Palworld events,
   bosses, bases, and raids ("Sage", "the alpha at the lake") survive
   hash-bucket collisions in the bag-of-tokens score.
3. **Character boost**: entries with a matching `CharacterId` get an
   extra additive bonus so per-companion memory dominates cross-talk.
4. **Importance weighting**: `MemoryImportance.Derive(...)` already
   classifies each entry on write; recall multiplies score by importance
   so high-salience moments outrank chatter.

That covers the **working tier** + a **lightweight recall tier**, both
local-first and on the chat hot path with no model server required. Two
more tiers exist in 2026 production patterns and are **not yet wired**:

- **Recall (vector)** — model-quality semantic similarity beyond the
  in-process FNV-1a projection (e.g. `bge-m3` dense embeddings).
- **Archival (graph + KV)** — structured facts about the player and
  long-running pal identities, retrieved by targeted lookup.

This doc explains what each tier does, when each becomes necessary, and
which library to use.

## The three-tier model (Mem0's pattern)

| Tier | Stores | Retrieval | Token cost per turn |
|---|---|---|---|
| **Working** | last 8-12 turns verbatim | always in context | ~1-2 K |
| **Recall** | semantically chunked past sessions | top-K vector similarity | ~2-4 K (top-5 chunks) |
| **Archival** | normalised facts, relationships, preferences | targeted graph / KV lookup | ~0.5-1 K (only relevant facts) |

PalLLM's working tier is solid. The recall + archival tiers are
what a 100-hour-companion needs.

## Library landscape (May 2026)

| Library | Pattern | Good for | Honest take |
|---|---|---|---|
| **Mem0** (48k stars on GitHub) | three-tier user/session/agent + vector + graph + KV | drop-in default for any project | Battle-tested, easy. **Pick this first.** |
| **Letta** (MemGPT successor) | OS-style core / archival / recall blocks | agents that should "feel" like they have a mind | Heavier, more setup, but models the cognitive layer better |
| **Zep** | temporal-graph memory (events have time semantics) | timelines / event-rich agents | 15-pt LongMemEval lead, heaviest of the three. Use when the time dimension matters. |

For a companion mod where "remembers what we did yesterday" is
the killer feature, **Zep's temporal-graph approach is the
strongest match** — but **Mem0 is the cheap-and-fast default**.

## Recipe 1: Drop-in Mem0 integration

The minimum-viable wiring. Mem0 runs as a separate service (or
in-process Python), takes messages, returns retrievable
embeddings.

### Server-side (Mem0 standalone)

```bash
# Run Mem0 as a service (Docker, default port 8000)
docker run -d --name mem0 -p 8765:8765 \
  -v mem0-data:/data \
  mem0ai/mem0-server:latest
```

### PalLLM-side adapter (sketch)

PalLLM's `PalLlmRuntime.ChatAsync` already builds a working-memory
window before calling inference. The adapter would:

1. Before the inference call: `POST http://localhost:8765/search`
   with the user's message and per-character namespace; receive
   top-K relevant past memories.
2. Inject those memories into the prompt (e.g. as a "Recent
   recall" block in the system prompt).
3. After the inference call: `POST http://localhost:8765/add`
   with the new turn so it's available for next time.

A `MemoryRecallClient` that mirrors the existing `InferenceClient`
shape would slot in cleanly.

### Per-character namespacing

Each personality pack should get its own Mem0 namespace so
"warrior remembers warrior conversations, scholar remembers
scholar conversations". Pack metadata extension (proposed):

```json
{
  "Id": "companion-warrior",
  "Mem0Namespace": "warrior-{playerId}",
  "...": "..."
}
```

The `{playerId}` placeholder lets the namespace also be
per-player so two players sharing one PalLLM install get
independent memory.

## Recipe 2: Vector recall without a separate service

If the operator doesn't want a separate Mem0 service, an
in-process vector recall layer works. Approach:

1. Use `Microsoft.SemanticKernel.Memory` (NuGet) or a tiny
   handwritten in-memory vector store.
2. Embed every chat turn with a small embedding model
   (e.g. all-MiniLM-L6-v2 via ONNX Runtime).
3. Persist embeddings + raw text to disk under
   `${RuntimeRoot}/Memory/recall/<character>.bin`.
4. On every chat, retrieve top-K most-similar past turns and
   prepend to the working-memory window.

Trade-off: in-process is simpler to ship, but the embedding cost
is on the chat hot path. ~10ms per turn on CPU for MiniLM-class
models — acceptable but not free.

## Recipe 3: Archival store (the 100-hour-companion feature)

The killer feature: the companion knows that the player named
their first pal "Sage" 80 hours ago. Working memory loses this;
recall vector search may or may not surface it. **Structured
archival** is the right tier.

The pattern: every chat turn, an "archivist advisor" extracts
named-entity facts and writes them to a small structured store.

```text
Archived facts for player "alice":
  - first_pal_name: "Sage"
  - playstyle_preference: "ranged"
  - lost_pal_count: 3
  - favorite_biome: "northern ridge"
  - last_long_session: "2026-05-03T22:00:00Z, 4h 12m"
  - notable_moments:
      - "saved Sage from drowning" (2026-04-15)
      - "first alpha capture - Bushi" (2026-04-22)
```

Each fact is a key-value pair with optional timestamp + provenance.
Retrieval is a targeted lookup (not a vector search) — the
companion asks "did the player ever mention a favorite biome?"
and gets back the answer in O(1).

Implementation sketch: a new `ArchivalStore` advisor with
methods `Add(playerId, key, value, source)`, `Get(playerId, key)`,
and `List(playerId, pattern)`. Persisted as JSON Lines under
`${RuntimeRoot}/Memory/archival/<playerId>.jsonl`.

The "archivist advisor" runs on the *trailing* edge of each chat
turn (not blocking the reply), examines the turn text, decides
whether anything worth archiving was said, writes a fact if
yes. Pure-fallback friendly — no model required for the simple
case (regex + named-entity rules); LLM-extracted for the rich
case.

## Recipe 4: Temporal graph (Zep-style)

For the most ambitious pattern: every memory has a timestamp and
a relationship to other memories. "The player lost Sage" links
to "the player named Sage" via the `same_pal` edge. "The
thunderstorm fight" links to "the alpha encounter that night"
via the `same_session` edge.

This is genuinely the most powerful pattern but also the most
expensive to ship. Recommended approach if PalLLM goes here:

- Use Zep as the backing store.
- Map each pack-namespace to a Zep group.
- Maintain a small pal-character ontology (every Pal has an
  identity that persists across sessions).
- Build a `pal memory timeline` verb that prints the timeline
  in human-readable form for any player or pal.

This is post-1.0 territory. Worth designing the seam now;
implementing later.

## Recommended trajectory for PalLLM

1. **Done today:** working tier (`ConversationMemoryStore`) plus the
   deterministic recall lane described above (FNV-1a dense + exact-token
   rerank + character boost + importance weighting). Local-first, on the
   chat hot path, no model server required.
2. **Next pass with model work:** add an optional **model-quality dense
   lane** (e.g. `bge-m3` via an OpenAI-compatible `/v1/embeddings`
   server) behind a `Memory:ExternalEmbedderEndpoint` flag. The
   deterministic lane stays as the always-on fallback when the external
   lane is off, slow, or breaker-open. This is the natural next step
   from the FUTURE_2035 idea 5c "hybrid-retrieval memory upgrade."
3. **After that:** ship the Mem0 in-process adapter or equivalent
   (Recipe 1 / 2). Useful when the operator wants cross-session
   memory promotion + the Mem0 ecosystem's auto-summarisation.
4. **After that:** archival store (Recipe 3) — the simplest form is
   regex + named-entity extraction; the rich form is LLM-driven.
5. **Future / 2027:** temporal graph (Recipe 4) for full
   100-hour-companion realism.

## Privacy posture for memory tiers

Every memory tier is a new data-emitting surface. The current
`/api/privacy/posture` endpoint enumerates this. Whatever memory
implementation lands must:

- Default to **never-leaves** (in-process / on-disk under the
  runtime root)
- Surface a posture-block with `data-class: chat-history` and
  `default: never-leaves`
- Honor the same opt-in flag as TTS / Vision: only flips to
  `only-with-opt-in` if the operator wires a remote service

Mem0 standalone is fine here (runs locally). Mem0 cloud or
Zep cloud would be `only-with-opt-in`.

## Cross-references

- [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md) — modality
  APIs (vision / audio / realtime)
- [`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md) —
  tool-use patterns
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) — the
  collaboration patterns layer
- [`PRIVACY.md`](PRIVACY.md) — every data-emitting surface,
  default classification
- [`PACK_AUTHORING.md`](PACK_AUTHORING.md) — pack format that
  could carry per-pack memory namespaces
