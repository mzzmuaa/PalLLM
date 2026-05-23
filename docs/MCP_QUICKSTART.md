# MCP Quickstart - PalLLM in 5 Minutes

Last audited: `2026-05-22`

This walks you from **downloaded PalLLM release ZIP -> working companion chat
inside an MCP-capable desktop host** using the Model Context Protocol. Roughly
five minutes on a machine that already has llama-server running; about ten
minutes if you also need to download a fresh GGUF.

> Already running PalLLM? Skip to [Step 3](#step-3---point-claude-desktop-at-palllm).
>
> Already familiar with MCP? The TL;DR: PalLLM's MCP endpoint is
> `http://localhost:5088/mcp` over Streamable HTTP. Paste the
> [example config](examples/claude-desktop-config.json) into your Claude
> Desktop config and restart.

## Prerequisites

- [Claude Desktop](https://claude.ai/download) (Mac or Windows).
- [llama.cpp](https://github.com/ggml-org/llama.cpp) - any recent build of
  `llama-server`.
- 5 GB of disk headroom for the small fast-start model (`gemma-4-E4B`).

## Step 1 - download the small GGUF first

PalLLM's tier orchestrator graduates from a fast-start model
(`gemma-4-E4B-it-UD-Q4_K_XL`, 5 GB unsloth UD-Q4_K_XL Gemma 4 E4B) to a larger
quality model once the larger one is loaded by llama-server. Pull the small
one from Hugging Face into your `D:\Models\Gemma` directory (or wherever your
curated library lives — see `docs/LOCAL_MODELS_INVENTORY.md`):

```bash
huggingface-cli download unsloth/gemma-4-E4B-it-GGUF \
    gemma-4-E4B-it-UD-Q4_K_XL.gguf --local-dir D:\Models\Gemma
```

Optional - schedule the quality tier (`Qwen3.6-35B-A3B-UD-Q8_K_XL`, 39 GB) to
download in the background:

```bash
huggingface-cli download unsloth/Qwen3.6-35B-A3B-Instruct-GGUF \
    Qwen3.6-35B-A3B-UD-Q8_K_XL.gguf --local-dir D:\Models\Qwen
```

The sidecar will auto-graduate to the 35B-A3B MoE once llama-server reports it
loaded, without any restart or config edit on your part (see
[OPERATIONS section "Configuring tiered model loading"](OPERATIONS.md#configuring-tiered-model-loading)).

## Step 2 - start the PalLLM sidecar

If you downloaded the release ZIP, unzip it somewhere writable (Desktop or
Documents) and launch the bundled self-contained binary:

```bash
sidecar\publish\PalLLM.Sidecar.exe
```

Or if you built from source:

```bash
dotnet run --project src\PalLLM.Sidecar\PalLLM.Sidecar.csproj
```

Or via Docker Compose (llama.cpp + PalLLM together — Pass 339 dropped
Ollama support; see `docs/examples/compose.yaml`):

```bash
docker compose -f docs/examples/compose.yaml up -d
```

Verify the sidecar is alive:

```bash
curl http://localhost:5088/health/live         # -> Healthy
curl http://localhost:5088/api/features | ...  # -> 121 feature catalog entries
```

## Step 3 - point Claude Desktop at PalLLM

Open your Claude Desktop config file:

- **Windows**: `%APPDATA%\Claude\claude_desktop_config.json`
- **macOS**: `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Linux**: `~/.config/Claude/claude_desktop_config.json`

Merge the following `mcpServers` entry into it - see
[`examples/claude-desktop-config.json`](examples/claude-desktop-config.json)
for the ready-to-paste version:

```json
{
  "mcpServers": {
    "palllm": {
      "url": "http://localhost:5088/mcp",
      "transport": "streamable-http"
    }
  }
}
```

Save, then fully quit and restart Claude Desktop (not just close the window -
use **Quit** from the menu). The PalLLM tools appear under the plug icon in the
chat input.

> **If PalLLM is running with auth on** (`PalLLM:Auth:ApiKey` set), add a
> `headers` block under the server entry:
>
> ```json
> "headers": { "Authorization": "Bearer YOUR-KEY-HERE" }
> ```
>
> Browser-based MCP hosts that send `Origin` also need to run from a loopback
> origin or be explicitly allowlisted via `PalLLM:Auth:McpAllowedOrigins[]`.

## Step 4 - use it

In a fresh Claude Desktop chat, the plug menu now shows PalLLM's tools. Try
natural prompts like:

- *"What's the current Palworld scene?"* -> your host calls
  `pal_scene_description` and summarises.
- *"Which Pal companions are nearby?"* -> `pal_list_characters`.
- *"What features does PalLLM ship with?"* -> `pal_list_features` -> your host
  summarises the `121` catalog entries.
- *"Is the runtime healthy?"* -> `pal_status` returns one structured
  payload (Score / Grade / suggestion counts by severity / top suggestion
  code / headline configuration flags). For finer detail call
  `pal_health_score` (just the numeric verdict + reasons) or
  `pal_health_suggestions` (the operator-actionable hint list with
  copy-paste commands).

You can also:

- **Attach a resource**: click the paperclip, browse "Resources", and attach
  `palllm://world/snapshot` as a context card.
- **Use a slash command**: type `/palllm_companion_chat` (or the host's
  equivalent prompt UI) to start a fully-contextualised companion
  conversation - PalLLM injects the live scene and character profile for you.

## Step 5 - optional: VS Code, Cursor, other MCP hosts

The same `/mcp` endpoint works with every MCP-aware tool. See:

- [`examples/vscode-mcp.json`](examples/vscode-mcp.json) - paste into
  `.vscode/mcp.json` for GitHub Copilot Chat MCP support.
- **Cursor**: `Settings -> MCP -> Add new server -> URL: http://localhost:5088/mcp`.
- **Custom clients**: any implementation of the MCP `2025-06-18` Streamable
  HTTP transport works against this endpoint. See
  [OPERATIONS section "Exposing PalLLM via MCP"](OPERATIONS.md#exposing-palllm-via-mcp)
  for the JSON-RPC request shapes.

## Full MCP surface reference

Complete list of what PalLLM exposes via MCP:

- **38 tools** across companion chat, world and roster inspection, memory and
  feature lookup, hardware/privacy/readiness posture, bridge and proof
  inspection, directives and promotion planning, and upstream MCP discovery.
  Use your host's tool browser or send `tools/list` to inspect the live full
  list.
- **6 direct resources + 1 templated resource**:
  `palllm://world/snapshot`, `palllm://features`, `palllm://runtime/health`,
  `palllm://characters`, `palllm://model/tier/active`,
  `palllm://model/collaboration`, `palllm://character/{characterId}`.
- **4 prompts**: `palllm_companion_chat`, `palllm_threat_analysis`,
  `palllm_base_status`, `palllm_model_collaboration_orchestrator`.

## Troubleshooting

- **Tools don't appear in Claude Desktop** -> Check that you did
  **Quit -> Reopen**, not just close the window. Claude Desktop loads MCP
  servers once at startup.
- **Sidecar logs `401 Unauthorized` for MCP requests** -> You have
  `PalLLM:Auth:ApiKey` set. Add the bearer header to your Claude Desktop
  config (see Step 3 note).
- **Browser-based MCP host gets `403 Forbidden` from `/mcp`** -> Its
  `Origin` is not loopback and is not listed in `PalLLM:Auth:McpAllowedOrigins[]`.
- **Companion replies feel generic or non-contextual** -> Vision and inference
  are off by default. See
  [OPERATIONS section "Opt-in feature matrix"](OPERATIONS.md#opt-in-feature-matrix)
  to turn on live inference against your llama-server endpoint.
- **Large model isn't being used** -> Run
  `curl http://localhost:5088/api/mcp/upstream` or attach the
  `palllm://model/tier/active` resource - it shows which tier is active, which
  models were last seen as available, and whether the active lane has been
  warmed yet.

## Where to go from here

- [`OPERATIONS.md`](OPERATIONS.md) - opt-in feature matrix, tier
  orchestration, auth, OTel, fallback coverage matrix.
- [`TLS.md`](TLS.md) - deploying PalLLM behind Caddy, nginx, or Traefik with
  auto-HTTPS when you want Claude Desktop on one machine to reach a PalLLM on
  another. Ships with an [`examples/Caddyfile`](examples/Caddyfile) you can
  adapt in minutes.
- [`TUNING.md`](TUNING.md) - consolidated tuning reference for every
  configurable parameter.
- [`API.md`](API.md) - full REST API reference if you prefer raw HTTP over MCP.
- [`ARCHITECTURE.md`](ARCHITECTURE.md) - why PalLLM is shaped the way it is.



