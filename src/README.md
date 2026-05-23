# `src/`

Two .NET projects:

- **`PalLLM.Domain/`** — portable runtime. NO ASP.NET, NO UE4SS,
  NO Palworld-specific references. Compiles to a self-contained
  DLL that any host can consume by implementing the five
  interfaces in `Portable/PortableAdapterContracts.cs`. See
  [`../docs/adr/0002-portable-adapter-seam.md`](../docs/adr/0002-portable-adapter-seam.md).
- **`PalLLM.Sidecar/`** — ASP.NET Core minimal-API host. Wraps
  the domain runtime, exposes `/api/*` (56 routes) + `/mcp` +
  `/metrics` + the Field Console dashboard, runs background
  workers (bridge inbox drain, self-healing watchdog,
  screenshot watcher).

## Where things live

The full symbol-to-file map is in
[`../docs/CODE_MAP.md`](../docs/CODE_MAP.md). The "where do I add
X?" map is in
[`../docs/EXTENSION_POINTS.md`](../docs/EXTENSION_POINTS.md).

## Build / test

From the repo root:

```powershell
pwsh ../pal.ps1 build
pwsh ../pal.ps1 test
```

The repo-root [`Directory.Build.props`](../Directory.Build.props)
applies to both projects (XML-doc generation on, CS1591
suppressed repo-wide because positional records are
self-documenting).
