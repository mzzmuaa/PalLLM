# Agentic patterns - what's working in 2026

Last audited: `2026-05-22`

A focused cheat-sheet of multi-step tool-use patterns that have
landed in production-grade agent systems by mid-2026. Companion
to [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) (which
covers single-vs-multi model orchestration) and
[`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md) (which covers
modality APIs).

This doc is for: someone who's already shipping tool-use today
and wants to know what's changed since 2024 - and what to ship
in PalLLM's MCP surface (38 tools today) to stay current.

## Index

| Pattern | Section | Who's using it |
|---|---|---|
| Anthropic Tool Search Tool | [Section 1](#1-tool-search-tool) | Claude Skills, Cursor, Cline |
| Programmatic Tool Calling sandbox | [Section 2](#2-programmatic-tool-calling) | Claude (2026), several internal Anthropic deployments |
| Pyramid Mixture-of-Agents | [Section 3](#3-pyramid-mixture-of-agents) | Production multi-agent systems, papers Apr 2026 |
| Speculative-decoded agent loops | [Section 4](#4-speculative-decoded-agent-loops) | vLLM 0.16 + EAGLE-3 default |
| Long-running memory + retrieval | [Section 5](#5-long-running-memory) | Mem0 (48k stars), Letta, Zep |
| Constrained-decoding tool dispatch | [Section 6](#6-constrained-decoding-dispatch) | xgrammar default in vLLM, SGLang |

## 1. Tool Search Tool

**The problem it solves:** an agent with 200 tools (PalLLM has
35 today; some internal Anthropic deployments are at 1000+) wastes
context tokens describing every tool to the model on every turn.
Tool descriptions can dominate the system prompt.

**The pattern:** instead of listing every tool up front, expose
a single `tool_search` meta-tool. The model calls
`tool_search("crafting-related stuff")` and gets back a small
shortlist of relevant tools. Only those expanded definitions
return to the model.

```jsonc
{
  "tools": [
    {
      "name": "tool_search",
      "description": "Find tools by keyword or capability. Returns up to 10 matches with full schemas.",
      "input_schema": {
        "type": "object",
        "properties": {
          "query": {"type": "string"},
          "limit": {"type": "integer", "default": 10}
        },
        "required": ["query"]
      }
    }
  ]
}
```

The agent loop sees only `tool_search` initially. After it picks
the right tool by query, the framework injects that tool's full
schema and the model invokes it.

**For PalLLM specifically:** the MCP surface today exposes 35
tools to the client. A `tool_search` meta-tool would let
companion personalities focus on the 5-6 tools relevant to
"craft a torch" without the full 36-tool list bloating every
prompt.

**Status today:** MCP doesn't yet support tool-search natively
in the spec; the pattern is implementable as a server-side
meta-tool that returns the discovery response shape.

## 2. Programmatic Tool Calling

**The problem it solves:** chained tool calls pollute context.
Each round-trip adds the tool result to history. After 10 rounds
of "list pals -> filter by level -> filter by element -> count"
the context is mostly intermediate JSON.

**The pattern:** the model writes a small script (Python /
TypeScript) that calls tools as functions, runs in a sandbox,
and returns only the *final* result. The intermediate tool
calls and their outputs never enter the chat history.

```python
# Model emits this code:
pals = list_pals()
hot_pals = [p for p in pals if p.element == "fire" and p.level >= 30]
print(f"You have {len(hot_pals)} fire-element pals at level 30+.")
# Sandbox runs it, returns only the print() output.
```

**For PalLLM specifically:** would require a sandboxed runner -
either a JS sandbox (Deno + permission flags) or a constrained
Python interpreter. The 38 MCP tools become callable as
typed-function bindings. The companion can answer
"how many crafting recipes do I have for berry-element pals
that need wood?" without 6 round-trip tool calls.

**Status today:** out of scope for the autonomous loop (sandbox
infrastructure is real work). But documenting the pattern means
when someone picks it up the contract shape is clear.

## 3. Pyramid Mixture-of-Agents

**The problem it solves:** flat MoA (every model votes on every
query) wastes compute on easy queries. The classic 2024 pattern.

**The pattern:** a lightweight router (cheap dense model, ~1B
params) decides per-query whether to:
- Answer directly (90% of queries)
- Escalate to a single-model second pass (8%)
- Escalate to full multi-model parallel (2%)

Result on benchmarks: 93% on GSM8K with **61% lower compute**
vs flat MoA (paper Apr 2026).

```
query
  -> router (0.5B-class, ~5ms)
        -> "easy" -> single small model -> reply
        -> "medium" -> draft + verify -> reply
        -> "hard" -> draft + verify + judge + critic -> reply
```

**For PalLLM specifically:** today's `ModelCollaborationPlanner`
+ `DuoOrchestratorPlanner` define 10 cooperation patterns + 5
hardware playbooks. They are *configuration* - the runtime picks
based on hardware tier + risk level. Adding a Pyramid router
means a small dense model (Gemma 4 E2B / Qwen3-4B) runs first as
the dispatcher. **Mechanical implementation** would be a new
`PyramidRouter` advisor that returns one of {direct, escalate,
escalate-full} given the request.

**Status today:** the patterns ship, the router doesn't. Adding
it requires the small-dense model to be running and a routing
prompt. Worth a future pass.

## 4. Speculative-decoded agent loops

**The problem it solves:** an agent loop with 10 tool-call
round-trips generates a lot of prefilled tokens (the system
prompt + tool definitions + history). Each round-trip pays
prefill cost.

**The pattern:** EAGLE-3 (NeurIPS 2025, default in vLLM 0.16)
draft-and-verify with a tiny draft model. Acceptance rate stays
high in agent loops because the model's output for tool calls
is highly structured (verbatim function names, predictable
JSON keys).

vLLM startup with EAGLE-3:

```bash
docker run --gpus all --rm -p 8000:8000 \
  vllm/vllm-openai:latest \
  --model Qwen/Qwen3-30B-A3B-Instruct \
  --speculative-config '{"method":"eagle3","model":"Qwen/Qwen3-Eagle-Draft","num_speculative_tokens":4}'
```

**For PalLLM specifically:** zero PalLLM-side change. The
operator's vLLM startup gets the flag; PalLLM's client doesn't
care that the upstream is using speculative decoding. **Update
[`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md)** to include the
flag in the recommended startup commands.

**Status today:** vLLM-side flag, fully external to PalLLM.

## 5. Long-running memory

**The problem it solves:** a companion that's been with the
player for 100 hours can't fit 100 hours of conversation in
context. Vanilla "last N turns" memory is amnesia by another
name.

**The pattern:** three-tier memory (Mem0's contribution, now
the default):

| Tier | Stores | Retrieval |
|---|---|---|
| **Working** | last 8-12 turns | always in context |
| **Recall (vector)** | semantically-indexed past chunks | top-K embedding similarity |
| **Archival (graph + KV)** | structured facts ("the player named their first pal Sage", "they prefer ranged weapons") | targeted lookup on key |

Mem0's API:

```python
mem0.add(messages, user_id="player-1234")
# later
context = mem0.search("what was the player's first pal?", user_id="player-1234")
```

**For PalLLM specifically:** today's `ConversationMemoryStore`
(2000-entry sliding window, `4 KiB` content cap per entry) is the working tier. The recall
+ archival tiers don't exist yet. Adding them means a vector
index + a graph store. Mem0 is the path-of-least-resistance
implementation; Letta and Zep are stronger but heavier.

**Status today:** documented in
[`MEMORY_RECIPES.md`](MEMORY_RECIPES.md), not yet wired.
PalLLM's working tier is solid; the companion's "100-hour
memory" feeling needs the tier-2/3 work.

## 6. Constrained-decoding dispatch

**The problem it solves:** tool-use models that occasionally
emit malformed JSON, hallucinate tool names, or return arguments
that don't match the schema.

**The pattern:** xgrammar-style constrained decoding enforces
the response shape at the token level. The model literally
cannot emit a malformed tool call because the decoder mask
forbids the wrong tokens.

vLLM startup (xgrammar is usually auto-selected; pin it explicitly
when you want a stable structured-output backend):

```bash
docker run --gpus all --rm -p 8000:8000 \
  vllm/vllm-openai:latest \
  --model Qwen/Qwen3-30B-A3B-Instruct \
  --structured-outputs-config.backend xgrammar
```

Request side:

```jsonc
{
  "model": "...",
  "messages": [...],
  "response_format": {
    "type": "json_schema",
    "json_schema": {
      "name": "tool_call",
      "schema": { /* strict JSON schema */ }
    }
  }
}
```

**For PalLLM specifically:** the existing inference client supports
`response_format` already (the field exists in the request body
shape). Operators should pin xgrammar with
`--structured-outputs-config.backend xgrammar` only when they need a
stable backend; otherwise vLLM's `auto` backend is fine. Per-request
guided-decoding fields from older vLLM examples should be replaced
with `structured_outputs` or `response_format`.

**Status today:** wire shape supported; documentation steering
is the gap.

## Summary table - what to adopt and when

| Pattern | Difficulty to add | Value for a companion mod | Recommended timing |
|---|---|---|---|
| Constrained decoding (xgrammar) | trivial -- vLLM flag or auto backend | high (no more malformed tool calls) | now, doc-only |
| EAGLE-3 speculative decoding | trivial - vLLM flag | medium-high (2-6x faster on Blackwell) | now, doc-only |
| Tool Search Tool | medium - meta-tool + server logic | high once tool count > 50 | when the surface grows |
| Pyramid MoA router | medium - new advisor + small router model | medium (compute savings) | when the operator is on a tight VRAM budget |
| Programmatic Tool Calling | high - sandbox infrastructure | high for complex chained queries | future pass |
| Long-running memory (Mem0) | high - vector + graph store | very high for 100-hour-companion feel | future pass |

## Cross-references

- [`MULTIMODAL_RECIPES.md`](MULTIMODAL_RECIPES.md) - vision /
  audio / realtime / vLLM-Omni
- [`MEMORY_RECIPES.md`](MEMORY_RECIPES.md) - Mem0 / Letta /
  three-tier memory
- [`MODEL_COLLABORATION.md`](MODEL_COLLABORATION.md) - collaboration
  patterns + DuoOrchestratorPlanner
- [`BLACKWELL_RECIPES.md`](BLACKWELL_RECIPES.md) - vLLM startup
  flags (EAGLE-3, xgrammar, prefix caching)
- [`API.md`](API.md) - PalLLM's HTTP + MCP surfaces (the consumer
  side of these patterns)



