# GitHub Copilot instructions

This file is the GitHub Copilot convention for repo-level
guidance. PalLLM uses the universal `AGENTS.md` convention as
its source of truth — Copilot reads this file, but everything
substantive lives there.

**→ Read [`../AGENTS.md`](../AGENTS.md) for the full briefing.**

## TL;DR for Copilot

- Test count: `1313` (verify via `pal.ps1 test`)
- Drift gates: `16 / 16` (run via `pal.ps1 audit`)
- Build warnings: `0` (enforced by `Directory.Build.props`)
- Hot files: `PalLlmRuntime.cs` (~4729 lines, every method
  inline-documented — don't mass-reformat),
  `Program.cs` (every HTTP route),
  `PalLlmFeatureCatalog.cs` (every feature entry),
  `FallbackBehaviorEngine.cs` (the 19 deterministic strategies)

## When you suggest code in this repo

1. Match the existing pattern. The advisor / builder /
   validator / feeder shapes are documented in
   `docs/CONVENTIONS.md`; new code should fit one of them.
2. Don't add features that are on by default if they emit
   network traffic — see ADR 0006 in `docs/adr/`.
3. Bump every documentation count when you change the
   underlying code; the drift gates will fail otherwise.
4. Pair every fallback strategy with a presentation cue —
   ADR 0001 makes the cue mandatory.
5. Read `docs/ANTI_PATTERNS.md` before proposing a "wouldn't
   it be cleaner if..." refactor.

## Run-loop the user expects

```powershell
pwsh ./pal.ps1 build
pwsh ./pal.ps1 test
pwsh ./pal.ps1 audit
```

Suggestions that pass these three are most likely to land.
