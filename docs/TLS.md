# Deploying PalLLM behind TLS

Last audited: `2026-05-07`

PalLLM binds to plain HTTP on localhost by design. When you expose the
sidecar beyond the host machine — LAN, VPN, or public internet — put it
behind a TLS-terminating reverse proxy. This doc walks through the
recommended deployment patterns with Caddy (easiest), nginx, and
Traefik.

> **Security prerequisite before opening any port beyond localhost**:
> set `PalLLM:Auth:ApiKey` to a strong secret. Every `/api/*` and
> `/mcp` request will then require
> `Authorization: Bearer <key>`. See
> [OPERATIONS § Enabling API-key authentication](OPERATIONS.md#enabling-api-key-authentication)
> for the full flow. **Do not expose an unauthenticated PalLLM** —
> chat, memory, and upstream MCP discovery would all be world-reachable.

## Why a reverse proxy (and not Kestrel directly)

Kestrel — PalLLM's underlying HTTP server — can serve TLS directly, but
putting a purpose-built reverse proxy in front gets you:

- **Automatic HTTPS**: Caddy (and Traefik with the appropriate resolver)
  provisions and renews Let's Encrypt certs for you. No manual cert
  rotation runbook.
- **MCP Streamable HTTP compatibility**: Server-Sent Events work through
  every mainstream reverse proxy as long as response buffering is off.
  Example configs below pre-configure this.
- **Operational separation**: PalLLM can bind to `localhost:5088`
  regardless of public exposure, so a misconfigured proxy never
  accidentally leaks the sidecar to the internet.
- **Uniform policy surface**: the proxy is the one place you configure
  TLS ciphers, HSTS, access logs, WAF rules, and rate limiting — not
  scattered across app + infrastructure.

## Caddy (recommended — simplest)

Caddy handles TLS provisioning automatically. Point it at your hostname
and it requests + renews a Let's Encrypt cert without operator
intervention. See the companion example file
[`examples/Caddyfile`](examples/Caddyfile) — copy it, replace
`palllm.example.com` with your real hostname, run
`caddy run --config Caddyfile`.

Key details pre-configured in the example:

- `flush_interval -1` on the `reverse_proxy` block so MCP's SSE
  responses aren't buffered by Caddy.
- `X-Forwarded-For` / `X-Forwarded-Proto` headers so PalLLM's logs +
  OpenTelemetry spans see the real client IP and scheme.
- Baseline security headers: HSTS with `preload` intent, nosniff,
  restrictive Referrer-Policy, removed `Server:` banner.

### Paired with auth

1. Set `PalLLM:Auth:ApiKey` on the sidecar to a strong secret.
2. Clients send `Authorization: Bearer <key>` on every request.
3. Caddy preserves the header unchanged — no proxy-side config needed
   for auth forwarding.

### Testing

```bash
# should return 200 Healthy
curl -I https://palllm.example.com/health/live

# should return 401 (no auth header)
curl -i https://palllm.example.com/api/features

# should return 200
curl -i -H "Authorization: Bearer YOUR-KEY" https://palllm.example.com/api/features
```

## nginx

nginx is the traditional option when Caddy isn't viable (existing
policy, OS package preference, etc.). Manual TLS via certbot or your
org's CA.

```nginx
# /etc/nginx/sites-enabled/palllm.conf
server {
    listen 443 ssl http2;
    server_name palllm.example.com;

    ssl_certificate     /etc/letsencrypt/live/palllm.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/palllm.example.com/privkey.pem;

    # Modern TLS policy. Adjust to your compliance requirements.
    ssl_protocols       TLSv1.2 TLSv1.3;
    ssl_prefer_server_ciphers off;

    # Security headers.
    add_header Strict-Transport-Security "max-age=31536000; includeSubDomains; preload" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "strict-origin-when-cross-origin" always;

    location / {
        proxy_pass         http://127.0.0.1:5088;
        proxy_http_version 1.1;
        proxy_set_header   Host              $host;
        proxy_set_header   X-Forwarded-For   $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;

        # MCP Streamable HTTP uses Server-Sent Events for server->client
        # messages. Disable nginx's response buffering so tokens stream
        # through instead of being held until the stream ends.
        proxy_buffering off;
        proxy_cache      off;
        chunked_transfer_encoding on;

        # Long-running chat streams need enough read timeout to cover
        # slow model responses. Bump this if you see upstream timeouts
        # during long `/api/chat` calls.
        proxy_read_timeout 120s;
    }
}

# Force HTTPS.
server {
    listen 80;
    server_name palllm.example.com;
    return 301 https://$host$request_uri;
}
```

Test + reload:

```bash
sudo nginx -t && sudo systemctl reload nginx
```

## Traefik

Traefik fits best in container orchestration scenarios (Docker Compose,
Kubernetes). Labels on the PalLLM service declare the route; Traefik
provisions certs via its built-in ACME resolver.

Add to [`examples/compose.yaml`](examples/compose.yaml) as a new
service:

```yaml
  traefik:
    image: traefik:v3
    ports:
      - "80:80"
      - "443:443"
    command:
      - "--providers.docker=true"
      - "--providers.docker.exposedbydefault=false"
      - "--entrypoints.web.address=:80"
      - "--entrypoints.websecure.address=:443"
      - "--certificatesresolvers.le.acme.httpchallenge=true"
      - "--certificatesresolvers.le.acme.httpchallenge.entrypoint=web"
      - "--certificatesresolvers.le.acme.email=you@example.com"
      - "--certificatesresolvers.le.acme.storage=/letsencrypt/acme.json"
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
      - letsencrypt:/letsencrypt
    restart: unless-stopped
```

Then label the `palllm` service:

```yaml
  palllm:
    # ... existing config ...
    labels:
      - "traefik.enable=true"
      - "traefik.http.routers.palllm.rule=Host(`palllm.example.com`)"
      - "traefik.http.routers.palllm.entrypoints=websecure"
      - "traefik.http.routers.palllm.tls.certresolver=le"
      - "traefik.http.services.palllm.loadbalancer.server.port=5088"
```

Add `letsencrypt:` under the top-level `volumes:` block.

## Hardening checklist once TLS is on

- [ ] `PalLLM:Auth:ApiKey` set to a strong secret (≥ 32 random bytes).
- [ ] `PalLLM:Auth:ProtectMetrics=true` if Prometheus isn't on the
      trusted side of the proxy.
- [ ] `PalLLM:Auth:ProtectHealth=true` if container orchestrators are
      off-host (otherwise leave probes open for locality).
- [ ] OTel collector, if used, reachable via an authenticated OTLP
      endpoint — the `OTEL_EXPORTER_OTLP_HEADERS` env var forwards a
      bearer token.
- [ ] Proxy access logs shipped to your log aggregator.
- [ ] Firewall / security group permits only ports `80` + `443`
      ingress; `5088` stays bound to localhost.
- [ ] Dependency monitoring (Dependabot / CodeQL) enabled on the repo
      for timely CVE response.

## Troubleshooting

- **`curl` returns "connection reset" but browser works** — you're
  hitting HTTP and the proxy is 301-redirecting to HTTPS; add `-L` or
  use `https://` directly.
- **MCP tools appear but no responses** — response buffering isn't
  disabled. Caddy: `flush_interval -1`. nginx: `proxy_buffering off`.
- **Browsers warn about mixed content from the dashboard** — the
  Field Console dashboard at `/` uses relative URLs, so proxy-rewritten
  hosts work fine. If you see mixed content, check that the proxy is
  setting `X-Forwarded-Proto: https` so ASP.NET Core generates HTTPS
  absolute URLs.
- **`/mcp` works via curl but Claude Desktop fails to connect** —
  Claude Desktop stores a cached negotiation; fully **Quit → Relaunch**
  (not just close-window) after changing the `url` field in
  `claude_desktop_config.json`.
- **HSTS headers stuck after you move hosts** — browsers cache HSTS
  policy for the `max-age` you configured. Trim `max-age=31536000` to
  `max-age=60` during initial rollout + increase only once everything
  is working.

## See also

- [OPERATIONS § Enabling API-key authentication](OPERATIONS.md#enabling-api-key-authentication)
- [OPERATIONS § Exposing PalLLM via MCP](OPERATIONS.md#exposing-palllm-via-mcp)
- [OPERATIONS § Container deployment](OPERATIONS.md#container-deployment)
- [SECURITY.md](../SECURITY.md) — vulnerability disclosure + supply-chain verification
