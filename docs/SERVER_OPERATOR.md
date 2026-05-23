# PalLLM for Dedicated Server Operators

Last audited: `2026-05-07`

This guide is for operators running a **Palworld dedicated server**
who want PalLLM's companion chat, memory, and action-intent surface
available to every connected player without each player running their
own sidecar.

If you're a single-player or small-LAN operator, start with
[`QUICKSTART.md`](QUICKSTART.md) instead — this doc assumes you're
hosting multiple players and care about uptime, rate limiting, and
backup.

## Deployment shape

Two supported topologies:

### Topology A — sidecar on the server box (recommended)

```
   ┌─────────────────────────────────────────────────┐
   │ Dedicated Palworld host (Windows or Linux)      │
   │                                                 │
   │  ┌───────────────┐      ┌────────────────────┐  │
   │  │ PalWorld.exe  │◀────▶│ PalLLM.Sidecar.exe │  │
   │  │ + UE4SS mod   │ loop │ (self-contained)   │  │
   │  └───────────────┘      └─────────┬──────────┘  │
   │                                   │             │
   │                                   ▼ loopback    │
   │                              ┌──────────────┐  │
   │                              │ llama-server │  │
   │                              └──────────────┘  │
   └─────────────────────────────────────────────────┘
            ▲                                ▲
            │                                │
            │ game protocol (15777)          │ optional Prometheus / OTLP
            │                                │ (no public exposure)
          players                        observability sink
```

**Pros:** single box, zero network hops between mod and sidecar, minimal
latency. **Cons:** sidecar competes with game server for GPU/CPU.

### Topology B — sidecar on a separate AI box

```
   ┌──────────────────────┐              ┌───────────────────────┐
   │ Palworld host        │              │ AI host               │
   │ + UE4SS mod          │◀──── LAN ────│ PalLLM.Sidecar + GPU  │
   │                      │   HTTP/JSON  │ + llama-server / vLLM │
   └──────────────────────┘              └───────────────────────┘
```

Configure the UE4SS mod to POST bridge events to the AI host's
`/api/bridge/drain` endpoint instead of localhost. See
[`docs/OPERATIONS.md`](OPERATIONS.md) "Remote bridge loop" for the
exact config-file layout. Secure with either a shared-secret
`PalLLM:Auth:ApiKey` or a Caddy/nginx reverse proxy with TLS (see
[`TLS.md`](TLS.md)).

## Install checklist

On the **server** box:

- [ ] Install Palworld dedicated server
- [ ] Install UE4SS (follow the Palworld Mod Wiki)
- [ ] Extract the PalLLM release zip into a writable directory
- [ ] Run `install.bat` (Windows) or `install-mod.sh` (Linux — requires
      Wine for the mod-copy step; the sidecar runs natively under
      Linux .NET 10)
- [ ] Edit `sidecar/publish/appsettings.json` and your launch environment:
      - Set `ASPNETCORE_URLS=http://0.0.0.0:5088` only if using
        Topology B; keep the default localhost bind for Topology A
      - Set `PalLLM:Auth:ApiKey` to a strong value if reachable from
        the LAN
      - Configure `PalLLM:Inference:BaseUrl` to point at your local
        llama-server (default `:8080/v1/`) or vLLM (`:8000/v1/`) endpoint
      - Raise `PalLLM:Fallback:MaxCharacterRequestsPerMinute` to suit
        your concurrent-player count (default is per-character)
- [ ] Launch the sidecar: `sidecar\publish\PalLLM.Sidecar.exe` or
      `systemctl start palllm.service` (see systemd unit below)
- [ ] Verify `curl http://localhost:5088/health/ready` returns `200`
- [ ] Start the Palworld server
- [ ] Watch `Runtime/LaunchEvidence/latest-player-launch.md` for the
      first player-bridge boot record

## Systemd unit for Linux

`/etc/systemd/system/palllm.service`:

```ini
[Unit]
Description=PalLLM sidecar (Palworld companion runtime)
After=network.target llama-server.service

[Service]
Type=simple
User=palllm
WorkingDirectory=/opt/palllm/sidecar/publish
ExecStart=/opt/palllm/sidecar/publish/PalLLM.Sidecar
Restart=on-failure
RestartSec=10

# Privacy: restrict filesystem writes to the configured Runtime/ root.
ProtectSystem=strict
ReadWritePaths=/opt/palllm/runtime
PrivateTmp=true
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
```

## Monitoring

PalLLM exposes Prometheus metrics at `GET /metrics` and optional OTLP
via `OTEL_EXPORTER_OTLP_ENDPOINT`. Useful dashboards:

- `palllm_chat_total`, `palllm_chat_fallback_total` — fallback rate
- `palllm_inference_recent_window_*_ratio_percent` — live-lane budget
  pressure
- `palllm_chat_rate_limit_bypasses_total` — players hitting the limiter
- `palllm_self_healing_*` — watchdog ticks + orphan archival

See [`OPERATIONS.md`](OPERATIONS.md) for the full metric inventory.

## Per-player rate limiting

Default `PalLLM:Fallback:MaxCharacterRequestsPerMinute = 20` is tuned
for single-player. For a 32-slot server, drop to `6–8` per character
to keep one player from monopolising the live lane.

The per-character rate limiter bypasses the live inference path but
**does not** bypass the deterministic director — so a rate-limited
player still gets a reply, just from the fallback tier. The
deterministic-first reply pipeline (ADR 0001) and the layered fallback
stack documented in [`ARCHITECTURE.md`](ARCHITECTURE.md) keep every
reply flowing.

## Backup and rollback

PalLLM persists state under `Runtime/`:

- `Runtime/session.json` — memory, relationships, reflections
- `Runtime/ReleaseEvidence/` — proof packets + audit artifacts
- `Runtime/SupportEvidence/` — support bundles (generated on demand)
- `Runtime/PromotionStaging/` — Pass-24 apply staging (if enabled)
- `Runtime/LaunchEvidence/` — per-boot snapshots

Back up `Runtime/` weekly. Rollback is simply replacing the directory —
no DB migration step, no external dependency.

For a faster operator response path, run `support.bat`
(`scripts/export-support-bundle.ps1` on Linux) before filing a GitHub
issue. See [`.github/ISSUE_TEMPLATE/support_export.md`](../.github/ISSUE_TEMPLATE/support_export.md).

## Upgrading

1. Stop the sidecar (`systemctl stop palllm.service` or Task Manager)
2. Extract the new release zip alongside the old one
3. Copy your edited `appsettings.json` into the new `sidecar/publish/`
4. Copy the `Runtime/` directory into the new install (or point
   `PalSavedRoot` at the old location)
5. Start the sidecar
6. Verify with
   `curl http://localhost:5088/api/release/readiness | jq .ReleaseReady`

Release-readiness surfaces every audit lane (smoke, native proof,
proof bundle, support bundle, package verification, full audit) so
"is this upgrade complete?" has a machine-readable answer.

## Privacy posture for server operators

Per [`PRIVACY.md`](PRIVACY.md):

- Inference traffic is loopback-only by default. Enabling a remote
  inference endpoint means your server's chat traffic crosses the
  network — disclose this to players up front.
- No telemetry, no analytics, no crash reports leave the box unless
  you explicitly configure an OTLP endpoint.
- The local `/metrics` and `/health/*` surfaces are localhost-bound by
  default; opening them to the LAN for monitoring is an explicit
  operator choice.
- Every player's conversation memory is stored under
  `Runtime/session.json` on your server — treat it like any other
  player data under your game-server privacy policy.

## Known limitations

- UE4SS mods run on the **client** (the native Palworld client
  connecting to your server), not on the dedicated server itself. So
  the PalLLM mod needs to be installed on every player who wants the
  companion experience. The dedicated server itself does not need the
  mod — it's a pure game-protocol host.
- This means Topology A + B both assume the **sidecar** is shared
  (optionally), but the **Lua mod** is per-client.
- For server operators who want to offer "PalLLM companion" as a
  service to their players, ship the release zip link in your server's
  Discord / welcome message and let players install locally. The
  sidecar-side state lives per-player anyway (keyed on CharacterId).

## Where to go from here

- [`OPERATIONS.md`](OPERATIONS.md) — full tuning reference
- [`TLS.md`](TLS.md) — reverse proxy with auto-HTTPS for Topology B
- [`PRIVACY.md`](PRIVACY.md) — privacy posture inventory
- [`MCP_QUICKSTART.md`](MCP_QUICKSTART.md) — expose PalLLM via MCP to
  external AI clients (Claude Desktop, VS Code, Cursor)
