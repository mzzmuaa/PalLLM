# `tests/`

NUnit test suite. **1310 tests** at last audit, organized one
fixture per subsystem.

## Run

From the repo root:

```powershell
pwsh ../pal.ps1 test          # quiet, ~25 s
pwsh ../pal.ps1 audit         # build + test + 16 drift gates
```

## Layout

```
tests/PalLLM.Tests/
├── *.cs                     ← one fixture per subsystem
├── SidecarTestFixture.cs    ← canonical fixture wiring (read this first)
├── coverlet.runsettings     ← coverage settings (excludes generated code)
└── PalLLM.Tests.csproj
```

## Add a test

See [`../docs/TESTING.md`](../docs/TESTING.md) for the cookbook.
Every test pattern (pure-logic, TTL-cached surface, HTTP
endpoint, MCP tool, streaming endpoint, bridge event handler,
fallback strategy) has a working reference test in this directory
to copy.

## Constraints

- **No external dependencies.** No live model server, no GPU,
  no Palworld instance. The fixture wires everything in-process.
- **Deterministic.** No `Thread.Sleep`, no
  `DateTimeOffset.UtcNow` in assertions, no unseeded `Random`.
- **One file per subsystem.** Long fixtures with dozens of
  unrelated tests are an anti-pattern. Reference:
  `MoodWeatherAdvisorTests.cs` (focused, single-subsystem).

The drift gate `Drift_Test_count_docs` enforces that the
`[Test]` count in this directory matches the count quoted in
README, ROADMAP, ARCHITECTURE, HANDOFF, and CODE_MAP. Add a
test → bump the docs.
