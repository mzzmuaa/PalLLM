---
name: Bug report
about: Something in the sidecar, the Lua bridge, or the install flow is misbehaving.
title: "[Bug] "
labels: ["bug", "triage"]
assignees: []
---

<!-- Thanks for taking the time to file a bug. The more of the sections
below you can fill in, the faster we can reproduce. -->

## What happened

<!-- One or two sentences. -->

## What you expected

<!-- One sentence. -->

## How to reproduce

1.
2.
3.

## Environment

- PalLLM version / commit SHA:
- `dotnet --list-runtimes` output (runtime version matters):
- Operating system:
- Palworld version + UE4SS version (if in-game):
- Configured inference / vision / TTS endpoints (model tags + base URLs, redact API keys):

## Evidence

Attach or paste whichever of these apply:

- `GET /api/health` JSON:
- `scripts/doctor.ps1` output:
- Relevant `Bridge/Outbox` or `Bridge/Failed` envelope filename + content (redact any chat text you'd rather not share):
- UE4SS console lines (compat probe line is especially useful):
- Stack trace or relevant sidecar log tail:

## Is this reproducible from the deterministic fallback director?

<!-- If yes, the issue is in the runtime itself. If no, it likely involves the
configured external inference/vision/TTS endpoint, which narrows the search. -->

- [ ] Yes — reproduces with `PalLLM:Inference:Enabled=false`.
- [ ] No — only happens when live inference / vision / TTS is configured.

## Additional context
