# Security Policy

Thanks for taking the time to report a security issue. PalLLM is an
unofficial third-party modification with a narrow surface area, and
responsible disclosure helps it stay that way.

## How to report a vulnerability

**Preferred channel — GitHub private vulnerability reporting.**
Open the repository's **Security** tab → **"Report a vulnerability"**.
This opens a private draft advisory only the maintainers and you can
see. GitHub walks you through the report fields; see the
[official GitHub
guide](https://docs.github.com/code-security/security-advisories/guidance-on-reporting-and-writing/privately-reporting-a-security-vulnerability)
for what to include.

If private vulnerability reporting is not available on your fork, open
a minimal issue that says "requesting private disclosure channel" and
the maintainers will reach out; do **not** post the vulnerability
details in a public issue.

## What to include

- Affected component (`PalLLM.Sidecar` HTTP surface, `PalLLM.Domain`
  runtime, the UE4SS Lua bridge, or one of the install / release
  scripts).
- Affected version(s) — either a release tag, a commit SHA, or a
  description of the code path.
- A minimal reproduction. PalLLM's default posture is local-only
  (`localhost:5088`), so please clarify whether the issue requires
  local access, a specific runtime configuration, or the bridge file
  path.
- The impact you observed and any mitigations you tried.

## Response timeline

We aim to:
- Acknowledge a valid report within **5 calendar days**.
- Confirm an initial severity assessment within **14 calendar days**.
- Ship a fix or a documented mitigation before public disclosure,
  typically within **90 days** of the acknowledged report, coordinated
  with the reporter.

If the issue is exploitable with public-internet exposure (e.g. the
operator bound the sidecar to `0.0.0.0` without authentication), we
will prioritize above the default timeline.

## Scope

In scope:
- The PalLLM sidecar HTTP endpoints under `/api/*`, `/metrics`, and
  `/health/*`.
- The UE4SS Lua bridge at `mod/ue4ss/Mods/PalLLM/Scripts/main.lua`.
- The install, doctor, and release-packaging scripts under `scripts/`.
- The Dockerfile and associated build / run posture.
- The GitHub Actions workflows under `.github/workflows/`.

Out of scope:
- Vulnerabilities in upstream components we don't own (UE4SS itself,
  the .NET runtime, any operator-supplied HTTP inference / vision / TTS
  server, NuGet packages pulled via
  `THIRD_PARTY_NOTICES.md`). Report those to their respective
  projects; cross-file a copy to us if PalLLM's default configuration
  meaningfully amplifies the issue.
- Social-engineering risks that depend on the operator explicitly
  flipping a default-off kill switch without reading the docs.
- The in-game companion's replies themselves (the deterministic
  fallback director and any configured LLM). Model content-safety is
  the operator's responsibility; see `docs/OPERATIONS.md` for the
  automation allowlist and rate limiter posture.

## Supported versions

PalLLM is pre-1.0. Until the first tagged release:

- The **latest commit on `main`** is the only supported version.
- Security fixes land on `main` and are referenced in `CHANGELOG.md`
  under a `### Fixed` section.

Once a 1.0 tag exists, we will update this file with the per-branch
support matrix.

## Repository publication hardening

If this repository is hosted publicly on GitHub, keep both of these on:

- **private vulnerability reporting**, so reporters have a built-in
  confidential channel
- **secret scanning push protection**, so obvious credentials are blocked
  before they land in history

Local contributors should also run the repo's `pre-commit` hooks
(`.pre-commit-config.yaml`), which include gitleaks plus the repo's own
public-copy and path-reference audits.

## Safe harbour

We will not pursue legal action against researchers who:
- Act in good faith on a reasonable interpretation of this policy.
- Do not access or modify other users' data.
- Give us reasonable time to remediate before public disclosure.

## Thanks

Reports that lead to a material fix are credited (with reporter
permission) in the `CHANGELOG.md` entry for the release that ships
the fix.

## Authentication when deploying beyond localhost

PalLLM ships with **no HTTP authentication** because the default
posture is localhost-only (the bound port is unreachable from other
machines). The moment you expose the sidecar beyond localhost — via
the shipped Dockerfile, a reverse proxy, or by binding to `0.0.0.0` —
**enable bearer-token auth** before anything else:

> **Pass 354 startup guard.** PalLLM now refuses to boot when it
> detects a non-loopback bind URL combined with a null/empty
> `PalLLM:Auth:ApiKey`. The startup log emits `LogCritical` and the
> process throws `InvalidOperationException`. This closes the
> "operator changed `ASPNETCORE_URLS` to `0.0.0.0` and forgot to
> set the key" footgun. Loopback-only binds with no key still boot
> but log a `LogWarning` documenting the posture so a future
> operator review surfaces it.
>

```jsonc
// appsettings.json
{
  "PalLLM": {
    "Auth": {
      "ApiKey": "<high-entropy secret>"
    }
  }
}
```

Or via environment:

```bash
-e PalLLM__Auth__ApiKey="$(openssl rand -hex 24)"
```

Operational routes (`/metrics`, `/health/*`, `/openapi/v1.json`, the
static dashboard) stay reachable without a credential so monitoring
and SDK tooling keep working. `ProtectMetrics` and `ProtectHealth`
flags tighten that if you're exposing the sidecar to the public
internet; in that scenario, also put a TLS-terminating reverse proxy
in front of the bound port. See
[`docs/OPERATIONS.md`](docs/OPERATIONS.md) § "Enabling API-key
authentication" for the full matrix.

## Supply-chain verification

Every tagged release is digested and attested automatically by the
[release workflow](.github/workflows/release.yml):

- **Digest manifests** (`SHA256SUMS`, `SHA512SUMS`, and
  `checksums.json`) published alongside each release zip.
- **[Sigstore](https://www.sigstore.dev/) keyless provenance** via
  GitHub's OIDC identity - no long-lived private keys, no secrets to
  rotate. The signing certificate records the exact workflow run,
  commit SHA, and repo that produced the artifact.
- **SLSA v1 provenance attestation** bound to the digest-listed artifacts - a
  signed statement describing *how* the binary was built, reviewable
  in the repo's "Attestations" tab.
- **Full-SHA pinned workflow actions** in CI, CodeQL, Lua lint, and release
  workflows. `pal workflow-pins` verifies external `uses:` references do not
  drift back to mutable tags.
- **CycloneDX SBOMs** (`PalLLM.Sidecar.cdx.json`,
  `PalLLM.Domain.cdx.json`) published alongside each release zip.
  Feed them to
  [Dependency-Track](https://dependencytrack.org/),
  [OWASP Grype](https://github.com/anchore/grype), or any other
  SBOM-aware vulnerability scanner.

**Verify a downloaded release** before running it:

```bash
sha256sum -c SHA256SUMS
gh attestation verify PalLLM-<tag>.zip --owner <owner>
```

The command exits non-zero if the zip has been tampered with, the
attestation was forged, or the signing certificate doesn't match the
expected workflow. Consider the install untrusted until this check
passes — especially for `install.bat`, which executes PowerShell
against your Palworld directory.
