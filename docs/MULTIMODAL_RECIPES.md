# Multimodal recipes — vision, audio, realtime (2026)

Last audited: `2026-05-22`

The companion to [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md). Where
that doc covers text-only inference on Blackwell, this doc covers
**vision in**, **audio in**, **audio out**, **video in**, and the
**realtime full-duplex** stack as it landed in 2026.

PalLLM's HTTP client surface today already handles single-image
chat (via `VisionClient`) and TTS-out (via `TtsClient`). What's
new in 2026 is:

1. **OpenAI-compatible audio in / audio out** is now the standard
   wire shape - Qwen3-Omni / vLLM-Omni / OpenAI-compatible realtime stacks
   speak it.
2. **Gemma 3n**, **Gemma 4**, and **Qwen3-Omni** expose native
   audio-capable multimodal paths you can light up locally. Qwen3.6 stays
   text/image/video in the normal PalLLM model matrix; use cascaded ASR or an
   Omni lane for speech until the exact Gemma runtime/model artifact is proven.
3. **Realtime WebSocket** has settled as the bidirectional voice
   transport — `/v1/realtime`, server-VAD, sub-100ms TTFA on the
   right model + GPU pair.
4. **Streaming video WebSocket** is emerging in vLLM-Omni as
   `/v1/video/chat/stream`; PalLLM treats it as a proof-only Palworld clip
   lane until frame cadence, audio chunk, reconnect, and fallback evidence
   are recorded.
5. **Video generation** through vLLM-Omni `/v1/videos` is a different async
   diffusion-job surface. It is useful for offline release walkthroughs or
   proof-bundle material, not for companion chat, screenshot understanding, or
   live Palworld HUD rendering.

This doc is recipe-shaped: pick the use case, copy the snippet.

> **Honest scope note.** Most recipes here describe operator
> server-side setup (vLLM-Omni / vLLM-Audio / SGLang). PalLLM's
> own runtime today consumes the standard `/v1/chat/completions`
> path; the seams for audio-in / audio-out / realtime-WS are
> documented + scaffolded but the chat hot path stays text-first
> by design. See "What PalLLM does today vs. what these recipes
> light up" at the bottom for the honest line.

## Index

| Use case | Section |
|---|---|
| Vision-in (single image, any VLM) | [§1](#1-vision-in-single-image) |
| Vision-in (multi-image, Qwen3-VL / Gemma 3n / Gemma 4) | [§2](#2-vision-in-multi-image) |
| Audio-in (Whisper-class transcription) | [§3](#3-audio-in-whisper-class) |
| Audio-in (native multimodal - Gemma 3n / Gemma 4 / Qwen3-Omni) | [§4](#4-audio-in-native-multimodal) |
| Audio-out (text -> voice, Maya1 / F5-TTS / IndexTTS2) | [§5](#5-audio-out-tts) |
| Voice cloning (10-second sample to NPC voice) | [§6](#6-voice-cloning) |
| Realtime full-duplex (`/v1/realtime` WebSocket) | [§7](#7-realtime-full-duplex) |
| Streaming video proof (`/v1/video/chat/stream`) | [§8](#8-streaming-video-proof) |
| Cascaded vs full-duplex — when to pick which | [§9](#9-cascaded-vs-full-duplex) |
| Vision-agent "look at the screen" (game capture -> VLM) | [§10](#10-vision-agent-screen-capture) |
| Per-NPC voice + LoRA bundle (a 2026 idea worth shipping) | [§11](#11-per-npc-voice--lora-bundle) |

Audio-in Gemma 4 references are proof-gated by exact model artifact and
runtime canary. Budget audio at `25` tokens/sec for Gemma 4 and `6.25`
tokens/sec for Gemma 3n.

If the same host also runs a Qwen3.6 or Gemma 4 model-native MTP lane, keep
that text-MTP endpoint separate from multimodal/mmproj/audio/video endpoints
until the exact server build passes a same-process negative canary. Text-MTP
wins do not qualify `image_url`, `video_url`, `input_audio`, or `audio_url`
routes; those stay no-spec or separately proven.

`pal connect omni -WriteConfig` now follows that split-lane posture: it wires
`PalLLM:Vision` to the vLLM-Omni endpoint by default and leaves the existing
text `PalLLM:Inference` lane alone. Add `-WireInference` only after the exact
same endpoint has replay proof for text-only chat, screenshot/image, audio,
strict JSON/tool-call, latency, fallback counters, and stall behavior.

## 1. Vision-in (single image)

The OpenAI-compatible wire shape (works on vLLM, Ollama, llama.cpp,
SGLang):

```json
{
  "model": "google/gemma-4-31b-it",
  "messages": [
    { "role": "user",
      "content": [
        {"type": "text", "text": "What's in this picture?"},
        {"type": "image_url",
         "image_url": {"url": "data:image/png;base64,iVBOR..."}}
      ]
    }
  ]
}
```

PalLLM's `VisionClient` already speaks this. No changes required
on PalLLM's side — point the `VisionOptions:Model` at any
single-image VLM (Gemma 4 31B, Qwen2-VL, LLaVA-Next, etc.) and
the existing `/api/vision/describe` endpoint works.

vLLM can cache repeated media processing when the caller supplies stable media
UUIDs. For a future PalLLM screen-capture loop, use a deterministic id such as
`sha256(frame-bytes)` for identical screenshots, or
`ui-probe:<dump-id>:<frame-index>` when replaying a captured proof sequence.
Send the media on cache misses; only skip the payload when that UUID is known
to be warm in the same vLLM process.

For heavier repeated-media loops, current vLLM v1 can also be paired with
LMCache's encoder-cache connector. Treat this as a separate cache from text KV
reuse: set an explicit CPU or disk budget, choose a local path if persistence is
desired, and prove cold vs warm media TTFT plus cache-hit/log evidence before
claiming a win. This is useful for repeated Palworld proof screenshots,
longer video clips, or repeated audio clips where the encoder dominates
latency; it is usually overkill for one-off single-frame asks.

## 2. Vision-in (multi-image)

Qwen3-VL and Gemma 4 31B accept multiple images per request.
Same `image_url` blocks, just more of them — the OpenAI wire
shape supports this natively. vLLM enforces a per-prompt cap via
`--limit-mm-per-prompt image=4`.

PalLLM today caps `VisionRequest` to one image. To accept multiple,
the `VisionOptions:MaxImagesPerRequest` knob (planned, see
`PalLlmOptions.cs`) is the seam — set it to N and pass an array
of images. The internal `VisionRequest` record would carry a
list rather than a single image.

vLLM serve flag for batched-image use:

```bash
docker run --gpus all --rm -p 8000:8000 \
  -e VLLM_MEDIA_URL_ALLOW_REDIRECTS=0 \
  vllm/vllm-openai:latest \
  --model google/gemma-4-31b-it \
  --limit-mm-per-prompt image=4
```

PalLLM's current vision path sends base64 data URLs, so vLLM does not need to
fetch arbitrary remote media. If an operator adds remote image/video URLs,
start vLLM with an explicit `--allowed-media-domains` allowlist and keep
redirects disabled so a media URL cannot bounce into a private network. Before
promoting any remote-media profile, run a negative replay with localhost,
private-range, link-local, IP-literal, and redirect-to-private URLs and confirm
they fail before model execution. Keep the normal player path on local bytes.

## 3. Audio-in (Whisper-class)

The cascaded-pipeline default for "the player is talking to me":
record audio -> Whisper -> text -> chat. Mature, low-risk.

```bash
docker run --gpus all --rm -p 9000:9000 \
  ghcr.io/openai/whisper:latest \
  --model whisper-large-v3-turbo
```

PalLLM doesn't currently capture mic input. The seam for an
operator who wants to wire it: a small `pal-listen.ps1` (or
similar) that captures, posts to Whisper, and then forwards the
text to PalLLM's `/api/chat`. **Out of scope for the autonomous
loop today** but the wire shape is plain HTTP POST.

## 4. Audio-in (native multimodal)

Gemma 3n, Gemma 4, and Qwen3-Omni accept audio directly in
chat-completions-style requests. Do not promote audio-in from the family name
alone; require the exact model artifact and local runtime canary. Keep clips
local, mono 16 kHz, and 30 seconds or shorter until longer clips have proof:

```json
{
  "model": "Qwen/Qwen3-Omni-30B-A3B-Instruct",
  "messages": [
    { "role": "user",
      "content": [
        {"type": "input_audio",
         "input_audio": {"data": "<base64-WAV>", "format": "wav"}},
        {"type": "text",
         "text": "Transcribe and respond."}
      ]
    }
  ]
}
```

Local startup with audio extras:

```bash
python -m pip install --upgrade "transformers[serving]"
transformers serve google/gemma-3n-E4B-it@<revision-sha> \
  --host localhost \
  --port 8002 \
  --continuous-batching \
  --dtype bfloat16

vllm serve Qwen/Qwen3-Omni-30B-A3B-Instruct --omni --port 8091
```

Docker-shaped vLLM example for Gemma 4 E4B text-out with native audio-in:

```bash
docker run --gpus all --rm -p 8000:8000 \
  vllm/vllm-openai:latest \
  --model google/gemma-4-E4B-it \
  --limit-mm-per-prompt image=2,audio=1
```

For Gemma proof lanes, record the normalized clip duration and token budget
beside the clip hash. Current Gemma audio guidance budgets Gemma 4 at `25`
audio tokens/sec, so a 30-second clip costs about `750` audio tokens before
text, system, and output headroom. Gemma 3n remains `6.25` audio tokens/sec,
or about `188` tokens for a 30-second clip. Reject or chunk clips when that
estimate plus the prompt exceeds the route budget.

**When to pick native-audio over cascaded:** the native path
preserves prosody, hesitation cues, and emotional content that
ASR-then-text strips. Cascaded is still easier to debug and cheaper for
ordinary player speech; native wins when "how the player said it" matters.
Gemma 3n gets a separate edge-memory proof: measure PLE caching and
conditional parameter loading separately from text KV cache before claiming a
laptop-friendly win.

## 5. Audio-out (TTS)

Three production-grade options as of May 2026:

| Engine | First audio | Voice clone? | Emotion control? | Cost |
|---|---|---|---|---|
| **Maya1** | <100 ms | yes (5s ref) | no | OSS, single-GPU |
| **F5-TTS** | 2-3 s | yes (10s ref) | partial | OSS |
| **XTTS-v2** | 5-8 s | yes (6s ref) | no | OSS |
| **IndexTTS2** | 1-2 s | yes (10s ref) | yes (separate identity + emotion vector) | OSS |
| **Chatterbox (Resemble)** | <500 ms | yes | yes (exaggeration knob) | OSS |
| **Cartesia Sonic-3** | <100 ms | yes | yes + 40 langs + laughter | paid |

PalLLM's `TtsClient` defaults to POSTing `{ "text", "voice" }` to a
configured endpoint and treating the response body as audio bytes. For
current OpenAI-compatible speech APIs, set
`PalLLM:Tts:RequestFormat=openai_speech`; PalLLM then posts `input`,
`voice`, optional `model`, and `response_format` to a route such as
vLLM-Omni `/v1/audio/speech`.

The recommended local-first default for a companion mod: **Maya1
or IndexTTS2**, paired with per-pack voice references (see §11).

## 6. Voice cloning

10-second WAV reference + F5-TTS / IndexTTS2 / Chatterbox = a new
NPC voice in seconds. Workflow:

```bash
# 1. Capture 10 seconds of clean audio from any source.
# 2. Save as samples/packs/<id>/voice-ref.wav
# 3. Declare VoiceConsent in pack.json before any TTS adapter uses it.
# 4. Point the TTS adapter at that file when synthesising for that pack.

# IndexTTS2 example (server-side synthesis):
curl -X POST http://localhost:5002/synthesize \
  -H "Content-Type: application/json" \
  -d '{
    "text": "Take it easy. I will bank the fire.",
    "voice_ref": "/voices/companion-warrior-ref.wav",
    "emotion": "calm"
  }'
```

Pair this with the personality-pack format
(see [`PACK_SAMPLES.md`](PACK_SAMPLES.md)): a pack can ship a
`voice-ref.wav` next to `prompt.md` and declare it through
`VoiceRefPath`. The validator keeps that path local to the pack,
allows only local audio container extensions (`.wav`, `.mp3`,
`.flac`, `.ogg`, `.opus`, `.m4a`, `.aac`), enforces a `10 MiB`
cap, requires `VoiceConsent` (`self_recorded`, `licensed`,
`synthetic`, or `public_domain`), and includes the file in
`ContentHash`.

## 7. Realtime full-duplex

The `/v1/realtime` WebSocket has converged across OpenAI, Azure,
and vLLM-Omni. Wire shape:

- Client opens a WS to `wss://<host>/v1/realtime?model=<model>`.
- Server accepts; sends `session.created` event.
- Client sends `session.update` with `modalities: ["text", "audio"]`,
  voice, system prompt, tools, etc.
- Client streams 20-100 ms PCM16 mono @ 16 kHz frames as
  `input_audio_buffer.append`.
- Server emits `input_audio_buffer.speech_started` (server VAD),
  then `response.audio.delta` (incremental PCM16 frames) +
  `response.audio_transcript.delta` (text mirror).
- Either side can `response.cancel` for barge-in.

vLLM-Omni snippet:

```bash
docker run --gpus all --rm -p 8000:8000 \
  -e VLLM_MEDIA_URL_ALLOW_REDIRECTS=0 \
  -v ${HF_HOME:-~/.cache/huggingface}:/root/.cache/huggingface \
  vllm/vllm-omni:latest \
  --model Qwen/Qwen3-Omni-30B-A3B-Instruct \
  --omni \
  --enable-prefix-caching
```

Current vLLM-Omni Qwen3-Omni docs call out one important proof caveat:
`/v1/realtime` is unsupported while the deploy config has `async_chunk`
enabled. For PalLLM, a realtime voice proof therefore needs a recorded
`async_chunk`-disabled deploy config plus `session.created`,
`response.audio.delta`, transcript-delta, reconnect/stall, and text-chat
fallback evidence. If that receipt is missing, use `/v1/chat/completions`
audio canaries or `/v1/audio/speech` instead of promoting realtime voice.

PalLLM today does not host a `/v1/realtime` endpoint. The seam:
a thin proxy that forwards player audio to the upstream realtime
WS, multiplexes server events back into the existing
`/api/chat` event stream + a new `/api/audio/stream` SSE / WS
endpoint. **Not in the chat hot path** — text chat stays
deterministic-fallback-grade. Audio-realtime is opt-in.

## 8. Streaming video proof

vLLM-Omni's streaming-video route uses a separate WebSocket,
`/v1/video/chat/stream`. The client sends `session.config`, base64 JPEG/PNG
`video.frame` messages, optional base64 PCM16 16 kHz mono `audio.chunk`
messages, and `video.query` prompts over the buffered stream.

PalLLM treats this as a Palworld proof-clip lane only:

- cap frame cadence and clip duration per profile
- bound optional PCM16 audio chunks and total buffered media bytes
- shed the stream before it can queue ahead of `/api/chat`
- record reconnect/stall behavior and fallback to still-image or world-state
- prove ordinary text chat still returns while the stream is unhealthy

This is useful for future "what just happened on screen?" clips, but it is not
the normal companion hot path.

vLLM-Omni also exposes `/v1/videos` and `/v1/videos/sync` for diffusion video
generation. Keep that off PalLLM's player-facing path: prove async
create/poll/content/delete, storage cleanup, cancellation, prompt-publication
hygiene, and no interference with `/api/chat` or `/api/vision` before using it
as release-walkthrough evidence.

## 9. Cascaded vs full-duplex

| Pipeline | Latency (warm) | Quality | Reliability | Hardware |
|---|---|---|---|---|
| **Cascaded** (Whisper-small + Gemma 4 E2B + Maya1) | ~400 ms end-to-end | High (uses biggest LLM you can fit) | High | Any 8GB+ GPU |
| **Full-duplex** (Moshi / Qwen3-Omni native) | <200 ms TTFA | Medium (smaller-context omni model) | Lower (stateful WS, harder to recover from drops) | Larger GPU, more memory |

**Picking guidance for a companion mod:**
- *Default:* cascaded — easier to debug, better text quality.
- *Special cases:* full-duplex when prosody-aware response is the
  point (the player gasps and the companion *immediately* asks
  what's wrong before the gasp finishes ASR). Reserve for
  hand-tuned moments.

## 10. Vision-agent screen capture

A 2026 idea that's mechanically simple but underexplored: the
companion sees the game by capturing a frame on demand and
sending it through the existing vision endpoint.

```powershell
# Pseudo-flow inside a future pal verb:
$frame = Capture-WindowFrame -ProcessName "Palworld"
$base64 = [Convert]::ToBase64String($frame)
Invoke-RestMethod -Uri http://localhost:5088/api/vision/describe `
  -Method POST -ContentType "application/json" `
  -Body (@{ imageBase64 = $base64; prompt = "What's the player looking at right now?" } | ConvertTo-Json)
```

Window capture options on Windows:

- **GraphicsCaptureItem (Win32 Capture API)** — native, low overhead
- **OBS virtual camera** — simple, works
- **NDI** — lowest latency, native on Windows

Pair with a `pal vision look` verb (future) that fires the capture
+ describe round-trip on demand. The companion can then say "I
see the alpha pal you're tracking, the line of sight to it is
blocked by the rock; flank east first."

PalLLM doesn't ship a window-capture verb today but the existing
`/api/vision/describe` endpoint accepts any base64 image.

For repeated "look at the current game screen" calls, prefer stable media
UUIDs over rehashing every captured frame in the model server. A future
`pal vision look` verb should capture the frame, compute a SHA-256 media id
locally, send the image with that UUID on first use, and reuse the UUID for
proof replay or repeated static screens while falling back to full media
payloads on cache misses. If an operator enables LMCache encoder cache, the
same verb should record cold/warm TTFT and expose whether the cache hit came
from text prefix reuse, media UUID reuse, or encoder-cache reuse so debugging
does not blur three different cache layers. If `PalLLM:Inference:PrefixCacheSalt`
is configured for a shared vLLM server, repeated screenshot proof should also
record whether matching salts reuse cache while different trust-domain salts do
not. If multiple replicas sit behind a router, prove sticky or KV cache-aware
routing keeps repeated media on the cache-warm worker before using that pool for
live companion turns. If the same vLLM lane also uses sleep mode for idle VRAM
reclaim, treat wake-up as a cold-cache boundary and remeasure those cache claims
before trusting the warm path.

vLLM can also accept precomputed multimodal embeddings through
`--enable-mm-embeds`. Treat that as a future trusted-tool lane, not a player
media path: PalLLM must own the encoder, model-family tensor shape, projector
metadata, and malformed-shape isolation proof before it sends `image_embeds`,
`audio_embeds`, or video embeddings. Operators can then compare VRAM and
latency against ordinary local media bytes; until that proof exists, the safe
default remains local bytes plus stable media UUIDs.

This remains an operator-server optimization. `GET /api/inference/collaboration`
now exposes the same idea in each model lane's `Capability.ServingProfile`
(`RequestHints`, `CacheHints`, `AdmissionControls`, `SecurityControls`,
`PromotionReceipts`, and `VerificationChecks`) so operator tools can check
runtime flags, media guardrails, vLLM sleep/wake admin isolation, trusted-only
embedding lanes, local LoRA adapter guardrails, and promotion proof without
scraping this prose.
The default PalLLM security posture stays local base64 media plus bounded
request bodies.

## 11. Per-NPC voice + LoRA bundle

A genuinely-novel 2026 idea: ship a four-tuple per personality
pack — `(prompt.md, voice-ref.wav, lora-adapter.safetensors,
memory-namespace)`. vLLM supports multi-LoRA serving, so an
opt-in runtime lane can select a local adapter per pack at request time.

Pack format extension (schema implemented; runtime adapter selection stays
operator-approved and opt-in):

```json
{
  "Id": "companion-warrior",
  "PromptPath": "prompt.md",
  "VoiceRefPath": "voice-ref.wav",
  "VoiceConsent": "self_recorded",
  "VoiceConsentNotes": "Recorded by the pack author for this local pack.",
  "LoraAdapterPath": "lora-adapter.safetensors",
  "MemoryNamespace": "warrior-namespace",
  "ContentHash": "..."
}
```

The companion who's been training for 100 hours on the player's
specific play style speaks in their cloned voice and remembers
their last 50 sessions — all with one drop-in pack swap. Each
piece is mechanically simple in 2026; the integration is the
work.

**Status today:** `PromptPath`, `VoiceRefPath`, `LoraAdapterPath`,
and `MemoryNamespace` are now in the personality-pack schema. The
validator keeps voice references and adapters local to the pack,
requires a voice-consent category when `VoiceRefPath` is set, caps
voice/audio clip files at `10 MiB`, requires `.safetensors` for local
adapter files, rejects remote URLs, and includes the declared files in
`ContentHash`. Runtime selection of
the adapter is still opt-in operator work: `GET /api/inference/collaboration`
and `pal models serving` expose the serving guardrails (`--enable-lora
--max-loras 1`, local hash-pinned paths, no remote adapter loads,
dynamic load/unload only on loopback admin surfaces, per-adapter
cache-identity proof, and deterministic fallback on adapter load failure).

## What PalLLM does today vs. what these recipes light up

| Feature | Today | Recipe lights up |
|---|---|---|
| Single-image vision-in | yes (`VisionClient`) | doc only — already works |
| Multi-image vision-in | no (1-image cap) | needs `MaxImagesPerRequest` knob + multi-content-block in `VisionRequest` |
| Audio-in (cascaded) | no (no mic capture) | doc only — operator wires Whisper themselves |
| Audio-in (native) | no | needs `AudioOptions` block + new audio-in endpoint |
| Audio-out (TTS) | yes (`TtsClient`) | doc only — already works |
| Voice cloning | pack voice reference metadata + consent category exists | needs a TTS adapter that consumes `VoiceRefPath` |
| Realtime WS | no | needs new `/api/audio/stream` endpoint + WS bridge |
| Streaming video WS | proof guidance only | needs bounded proxy, fallback proof, and live Palworld receipts |
| Vision-agent screen | no | doc only — uses existing `/api/vision/describe` |
| Per-NPC LoRA bundle | pack schema + guarded serving guidance | needs runtime adapter selection + operator-approved adapter staging |

## Cross-references

- [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md) — the text-only
  Blackwell + NVFP4 / MXFP4 recipes
- [`MEMORY_RECIPES.md`](MEMORY_RECIPES.md) — Mem0 / Letta /
  long-term memory patterns
- [`AGENTIC_PATTERNS_2026.md`](AGENTIC_PATTERNS_2026.md) — Tool
  Search / Programmatic Tool Calling / Pyramid MoA
- [`PACK_AUTHORING.md`](PACK_AUTHORING.md) — the pack format
  this doc proposes extending
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) — the
  collaboration patterns layer
