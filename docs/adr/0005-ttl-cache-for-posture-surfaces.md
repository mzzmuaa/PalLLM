# ADR 0005 — TTL-cache pattern for read-heavy posture surfaces

- **Status:** Accepted
- **Date:** 2026-04
- **Tags:** performance, caching, observability
- **Depends on:** [`0002`](0002-portable-adapter-seam.md) (the cached surfaces sit in PalLLM.Domain, so the cache must not introduce ASP.NET dependencies)
- **Supports:** the latency budgets in [`../HOT_PATH.md`](../HOT_PATH.md)

## Context

PalLLM exposes several "posture" surfaces — pure deterministic
snapshots that summarize a slice of the runtime state for an
operator or AI consumer. The notable ones:

| Surface | Endpoint | Cost (cold) |
|---|---|---|
| Hardware profile | `GET /api/hardware` | OS calls + driver-marker probes; ~5-20ms |
| Privacy posture | `GET /api/privacy/posture` | Per-surface inspection; ~1-5ms |
| Resource-budget posture | `GET /api/budgets` | Metric reads + arithmetic; ~1-3ms |
| Air-gap report | `GET /api/airgap/verify` | DNS classification; ~5-50ms |

Each is called repeatedly. The Field Console dashboard polls all
four every few seconds. AI clients fetch them on connect via
`/api/describe`. The `/api/quickstart` route reads three of them
inline. A naive request burst (1 dashboard tab + 1 MCP client
connect) can fire 8 concurrent reads of the same posture in under a
second.

The data underneath changes slowly: hardware ID doesn't change
mid-run; privacy posture only changes when an operator flips a
config flag; budget posture changes with metrics counters. The
**inputs that drive the snapshot are stable enough to cache**, with
a clear invalidation key.

## Decision

Each read-heavy posture builder gets a `*Cached` companion to its
existing `Capture()` / `Verify()` method. The pattern:

```csharp
private static readonly TimeSpan DefaultCacheTtl = TimeSpan.FromSeconds(30);
private static volatile PostureCacheEntry? _cached;

public static Posture CaptureCached(Options options, TimeSpan? cacheTtl = null)
{
    string signature = ComputeSignature(options);
    PostureCacheEntry? snapshot = _cached;
    DateTimeOffset now = DateTimeOffset.UtcNow;
    TimeSpan ttl = cacheTtl ?? DefaultCacheTtl;

    if (snapshot is not null
        && snapshot.Signature == signature
        && now - snapshot.CapturedAt < ttl)
    {
        return snapshot.Posture;
    }

    Posture fresh = Capture(options);
    _cached = new PostureCacheEntry(fresh, signature, now);
    return fresh;
}

public static void InvalidateCache() => _cached = null;
```

Three invariants:

1. **Signature-based invalidation.** The cache compares a stable
   signature derived from the inputs. Any change in the signature
   forces a recompute, regardless of TTL.
2. **Bounded TTL.** Even if the signature is stable, the cache
   expires after a configurable interval — defaults vary per surface
   (5min hardware, 30s privacy/airgap, 15s budget) based on how
   quickly the underlying state can change.
3. **Original API preserved.** `Capture()` / `Verify()` still works
   exactly as before. Tests that need a fresh snapshot don't have to
   know caching exists. The cached variant is a separate method.

Surfaces that have adopted this pattern (as of Pass 48):

| Surface | Cache method | TTL | Signature |
|---|---|---|---|
| `HardwareProfiler` | `CaptureCached(forceTier?, cacheTtl?)` | 5 min | `forceTier` |
| `PrivacyPostureBuilder` | `CaptureCached(options, cacheTtl?)` | 30 s | Inference/Vision/TTS enable+BaseUrl, OTLP env-var |
| `ResourceBudgetPostureBuilder` | `CaptureCached(options, metrics, cacheTtl?)` | 15 s | Options fields + metrics + 75% fallback-share boundary |
| `AirGapVerifier` | `VerifyCached(options, cacheTtl?)` | 30 s | Options outbound fields + OTLP env-var + MCP upstream list |

## Alternatives considered

- **`MemoryCache` / `IMemoryCache`.** Adds a dependency on
  ASP.NET Core abstractions, breaks the portable-domain contract.
  These surfaces sit in `PalLLM.Domain`, which deliberately doesn't
  reference ASP.NET.
- **`Lazy<T>` per process.** Doesn't expire; one-time computation
  that never refreshes is wrong when state changes.
- **HTTP-layer output caching only.** Already in use for
  `/api/describe` and friends. But the posture builders are also
  called from non-HTTP code paths (the `/api/quickstart` route
  composes them inline; MCP tools call the same builders). HTTP
  caching can't cover those.
- **Recompute always.** The cost is small per call but visible in
  flame graphs under polling load. Lower latency is genuinely worth
  the small caching layer.

## Consequences

**Positive:**
- Lower latency under polling load. Repeated reads return in
  microseconds (a single signature comparison + dictionary lookup)
  instead of milliseconds.
- The pattern is uniform across surfaces, which makes it easy to
  apply to the next read-heavy posture builder.
- Tests can call `InvalidateCache()` at fixture setup to guarantee a
  fresh read; this is wired in every test that exercises a `*Cached`
  variant.

**Negative:**
- Static state. `_cached` is a process-level static — shared across
  every caller. That's fine because the underlying inputs are
  process-level too (the `PalLlmOptions` instance is a singleton).
- Stale-by-TTL during a flag flip. If an operator toggles a privacy
  flag, the next caller within the TTL window may see the old
  posture. Acceptable for a 30s window; documented in
  `OPERATIONS.md`.

## Harvest hint

The pattern is documented in
[`DESIGN_PRINCIPLES.md`](../DESIGN_PRINCIPLES.md) § 8. The cleanest
example to copy is `AirGapVerifier.VerifyCached`
(`src/PalLLM.Sidecar/AirGapVerifier.cs`) — small, self-contained,
shows the signature computation and the invalidation API.

## Related

- Code: `src/PalLLM.Domain/Inference/HardwareProfiler.cs`,
  `src/PalLLM.Domain/Runtime/PrivacyPostureBuilder.cs`,
  `src/PalLLM.Domain/Runtime/ResourceBudgetPostureBuilder.cs`,
  `src/PalLLM.Sidecar/AirGapVerifier.cs`
- Docs: [`DESIGN_PRINCIPLES.md`](../DESIGN_PRINCIPLES.md) § 8,
  [`ADVISORS.md`](../ADVISORS.md) (the "Pure / Stateful / Cached"
  column)
