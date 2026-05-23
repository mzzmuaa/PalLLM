# PalLLM Release Checklist

Last audited: `2026-05-23`

This is the maintainers' publication guide: the shortest path from a green
working tree to a release that is traceable, supportable, and less likely to
surprise users after it leaves the repo.

## Release goals

Before tagging a release, make sure the build is:

- technically shippable
- operationally supportable
- publication-safe on the repo front page and support surfaces
- honest about any remaining compatibility or legal blockers

## Required checks

Run these from the repo root and keep the generated reports with the release
notes or validation bundle:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_full_audit.ps1
```

The full audit now includes:

- build + tests
- candidate release packaging + manifest verification
- code/docs drift checks
- committed OpenAPI snapshot drift check
- the public copy audit
- the path reference audit
- dangling markdown-link checks
- optional coverage + SBOM generation

For faster local iteration:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_full_audit.ps1 -SkipPackaging
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/publish-audit.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/aot-readiness.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/audit_public_copy.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/path_reference_audit.ps1
```

`pal publish-audit` is the focused local publication preflight. It does not
build, test, package, or call public registries; it composes the public copy
audit, path-reference audit, workflow action pin audit, and
`THIRD_PARTY_NOTICES.md` package-reference coverage into one timestamped
`artifacts/publish-audit/<timestamp>/RESULTS.md` report. Use it before a
release notes pass when you need the publication-safety answer without waiting
for a full package build.

`pal aot-readiness` is the focused local trim / Native AOT preflight. Its
default mode is static and local-only: it verifies the .NET 10 target
framework, source-generated config binding, the portable domain assembly's
explicit AOT analyzer opt-in, source-generated JSON contexts, common sidecar
source-generated payload shapes, Minimal API host shape, dynamic-code markers,
and dependency review surface. Use `-PublishProbe` only when the machine has
the native compiler prerequisites installed and you intentionally want to run a
full Native AOT publish experiment.

`GET /api/release/readiness` is the runtime's machine-readable release
posture. Check it when automation, release packaging, or external dashboards
need the current shipped surface, canonical audit commands, current
publication blockers, or the latest durable smoke/native-proof/proof-bundle/
support-bundle artifacts without scraping markdown.

`GET /api/bridge/proof` is the runtime's machine-readable Palworld-native proof
surface. Check it when you need to verify that bridge boot, widget-seam
evidence, the active native-hud config source/path, and the live
request/delivery/speech-playback loop are genuinely proven instead of merely
inferred from sidecar health.

`scripts/run-sidecar-smoke.ps1` now persists `Runtime/ReleaseEvidence/latest-smoke.json`
plus a timestamped history artifact. Keep the latest file with the release
validation bundle whenever you are preparing a publishable package.

`scripts/run-native-proof.ps1` persists
`Runtime/ReleaseEvidence/latest-native-proof.json` plus a timestamped history
artifact. Keep that artifact with the same validation bundle whenever you have
captured a real Palworld-native HUD delivery proof on the target build. Local
runs fail fast and persist a blocked artifact when the Palworld process is not
running; use `-SkipPalworldProcessCheck` only for remote-sidecar proof capture
or unusual launchers. Each artifact now includes watcher timing, timeout and
poll settings, poll count, a completion reason, stable `DiagnosisCode` /
`DiagnosisSummary` fields, `DiagnosisAction` / `DiagnosisCommand`
remediation hints, and a compact status-transition trail so a failed run can
be diagnosed and routed from the JSON alone.
`/api/release/readiness` does not trust a native-proof artifact merely because
its status string says `proven`; the same artifact must
also carry `BridgeProofStatus = "delivery_proven"`, `LiveDeliveryProven =
true`, and `NativeHudBindReady = true`, otherwise the evidence is downgraded to
`invalid` and the next command remains `scripts/run-native-proof.ps1`.

`scripts/export-release-proof-bundle.ps1` persists
`Runtime/ReleaseEvidence/latest-proof-bundle.json` plus
`Runtime/ReleaseEvidence/latest-proof-bundle.zip`, as well as timestamped
history artifacts. Keep that bundle with the candidate package whenever you
want one durable artifact that contains the current release/readiness snapshot,
bridge proof, inference-performance snapshot, smoke artifact, native-proof
artifact, and HUD config. The manifest summarizes inference health with compact
status, lane, response/fingerprint, finish-reason, and token-receipt counts
without raw prompt or completion text. The exporter privacy-redacts staged
portable text before archiving, then the
manifest reports `PrivacyRedactionApplied`,
`PrivacyRedactionCheckedFileCount`, `PrivacyRedactionRedactedFileCount`,
`PrivacyRedactionRuleHits`, `PublicationScanPassed`,
`PublicationScanCheckedFileCount`, and `PublicationScanViolations` for the
portable bundle text surface.
`/api/release/readiness` also opens the paired zip and rejects the evidence as
`invalid` if it is not a readable archive, does not contain `proof-bundle.json`,
is missing a file listed by the manifest, or contains an archived manifest whose
proof status, native-HUD config, optional-file, blocker, or ready-evidence
fields disagree with the latest manifest. Inference-performance receipt counts
must also agree between the archived manifest and the latest manifest.

`scripts/verify-release-package.ps1` persists
`Runtime/ReleaseEvidence/latest-package-verification.json` plus a timestamped
history artifact. Keep that artifact with the same validation bundle whenever
you want durable proof that the current candidate zip matches its embedded
`RELEASE_PACKAGE_MANIFEST.json` and passes the packaged text-surface
publication scan.

`scripts/compute-release-checksums.ps1` persists
`Runtime/ReleaseEvidence/latest-artifact-integrity.json` plus a timestamped
history artifact after writing `SHA256SUMS`, `SHA512SUMS`, and
`checksums.json` under `artifacts/packaging/`. Keep those digest manifests with
the candidate zip. `/api/release/readiness` reports whether those files still
exist, how many release artifacts were covered, whether SHA-512 was skipped,
and whether detached signature files were present beside the manifests.

Released packages now also carry `play.bat` plus `scripts/play-palllm.ps1` as
the primary player-facing entry path. `scripts/package-release.ps1` now bundles
a self-contained sidecar by default, so the normal release zip is directly
runnable without a separate .NET runtime install. That launcher installs or
refreshes the mod, starts or reuses the bundled sidecar, issues a best-effort
warmup for the active inference lane when warmup is enabled, runs doctor, opens
the dashboard, writes `Runtime/LaunchEvidence/latest-player-launch.json` plus
`.md`, and launches Palworld while preserving the lower-level
`install.bat`, `scripts/start-sidecar.ps1`, and `scripts/doctor.ps1` path for
support. Use `-SkipSidecarPublish` or `-FrameworkDependentSidecar` only for
deliberately lean or internal builds.

`scripts/package-release.ps1` also bundles every root-level `docs\*.md` file
named by the generated `PLAYER_README.txt`. The package writes a compact
player-facing `CHANGELOG.md` instead of copying the source repository's
developer changelog, and the generated readme avoids unnecessary client or
model-provider brand names in favor of protocol/provider-neutral wording.
`MetaTests` parses the generated readme body and `$packagedDocs` list so future
release-copy edits cannot point players at a doc that is absent from the zip or
reintroduce root player-copy branding drift.

`scripts/verify-release-package.ps1` scans the expanded package publication
surface in addition to checking `RELEASE_PACKAGE_MANIFEST.json`. Its durable
verification artifact reports `PublicationScanPassed`,
`PublicationScanCheckedFileCount`, and `PublicationScanViolations`, so a
candidate package can fail before publication even if the file manifest itself
is structurally correct.

`scripts/export-release-proof-bundle.ps1` and
`scripts/export-support-bundle.ps1` first use the same shared privacy
redaction pass on staged portable text, then use the same shared text-surface
scanner before writing their manifests. A proof or support bundle with
sibling-project bleed, endorsement/approval copy, unrelated third-party
franchise references, or broad platform-scope wording is marked `invalid` and
carries the violations in its manifest; stale bundles that predate the
redaction flag are also routed toward recapture by `/api/release/readiness`.

Released packages now also carry `support.bat`, which runs
`scripts/export-support-bundle.ps1` and writes
`Runtime/SupportEvidence/latest-support-bundle.zip` plus `.json`. Use it when a
candidate package works on your machine but you need one portable artifact that
captures the latest launch, health, bridge-proof, and release-readiness state
for support or regression comparison. The support-bundle manifest includes the
same `PrivacyRedaction*` and `PublicationScan*` fields as the proof bundle, and
the release-readiness reader applies the same archive-shape check before
treating the support bundle as recorded. It also rejects support archives whose
embedded manifest disagrees with the latest launch/proof/package/audit status,
native-HUD config, optional-file, blocker, or ready-evidence fields.

`scripts/run_full_audit.ps1` now also persists
`Runtime/ReleaseEvidence/latest-full-audit.json` plus a timestamped history
artifact. Keep that artifact with the same validation bundle whenever you want
durable proof that the current source tree, OpenAPI snapshot, docs, tests, and
candidate-package verification all passed together.

`GET /api/release/readiness` now also marks `SmokeEvidence`,
`NativeProofEvidence`, `ProofBundleEvidence`, `SupportBundleEvidence`,
`PackageVerificationEvidence`, `ArtifactIntegrityEvidence`, and
`FullAuditEvidence` as `fresh`, `stale`, or
`unknown` using the runtime freshness window (`24` hours by default). Treat
stale proof as a release blocker until the relevant capture/export script has
been rerun for the current candidate build. The read-only `pal proof` projection
uses the same release-proof posture for native delivery: stale durable
`latest-native-proof.json` evidence reports `overall: STALE PROOF`, and
`pal proof -RequireProven` fails until a live or fresh proof is available.

## Publication-facing guardrails

`README.md`, `NOTICE.md`, `SECURITY.md`, `docs/INDEX.md`, and this guide are
treated as release-facing copy. On those surfaces:

- prefer neutral protocol/capability language over third-party client names
- prefer "local inference endpoint" over model-host brands
- prefer "MCP-capable client" over naming a specific desktop app unless the
  brand is technically required
- avoid unrelated third-party franchise references as comparison shorthand
- avoid broader platform claims that present PalLLM as more than a focused
  Palworld companion mod
- avoid legal-safety, IP-neutrality, or compliance-certainty overclaims
- keep repo-local links valid so copied instructions still resolve cleanly

If a technical compatibility term is required, keep it in a narrow,
defensive context and make sure `NOTICE.md` still makes the unaffiliated
status obvious.

Release-package roots are checked separately during
`scripts/verify-release-package.ps1`. That scan blocks private sibling-project
research terms, obvious official-endorsement or approval claims, unrelated
third-party IP/franchise references, broad platform-scope wording, and legal
certainty overclaims anywhere in shipped text files. It also keeps root
player-facing copy (`README.md`,
`PLAYER_README.txt`, and the generated package `CHANGELOG.md`) on
protocol/provider-neutral wording and blocks current third-party
model/runtime/vendor brands there. The raw developer `CHANGELOG.md` is
intentionally replaced with a short player-facing package notes file during
packaging so internal research history does not travel in release zips.

## Security and supply chain

For a public GitHub-hosted release, treat the following as mandatory:

- GitHub private vulnerability reporting stays enabled
- secret scanning push protection stays enabled
- CodeQL stays green or every open alert is consciously triaged
- `.github/workflows/*.yml` external actions stay pinned to full commit SHAs
  (`pal workflow-pins`)
- `THIRD_PARTY_NOTICES.md` names every current NuGet `PackageReference` using
  SPDX license-expression language (`pal publish-audit`)
- release artifacts still carry SBOMs and attestations
- contributors can run the local `pre-commit` hooks in
  `.pre-commit-config.yaml` before they push

See `SECURITY.md` for the vulnerability-reporting policy and the local auth
posture when the sidecar is exposed beyond localhost.

## Publication blockers

This repo is closer to publication-ready than it was, but public naming and
shipping scope are still tied to external compatibility surfaces. Current
publication blockers still include:

- the product name itself remains scope-coupled to a third-party game
- the runtime seam is now neutralized, but the shipped mod package, Lua bridge,
  and operator flow still target Palworld and UE4SS explicitly
- some deeper technical docs remain intentionally compatibility-heavy because
  the current runtime still interoperates with those external surfaces

Do not use legal-safety overclaims or brand-neutral positioning until those
items are removed or isolated behind a deliberate rebrand pass.

## Final release sanity check

Before tagging:

1. Run `scripts/run_full_audit.ps1`.
2. Run `pal publish-audit` and read the generated
   `artifacts/publish-audit/<timestamp>/RESULTS.md`.
3. Read the generated public copy audit and path reference audit reports.
4. Verify `/api/release/readiness` reports the expected route counts, audit
   commands, canonical doc pointers, and publication blockers, verify
   `/api/bridge/proof` reports the expected native-readiness and loop-proof
   state for the target build, verify `SmokeEvidence.Status` is `recorded`
   with the expected latest artifact path and native-hud config source/path,
   verify `NativeProofEvidence.Status` is `proven` when a live Palworld proof
   run is expected for the release candidate, verify
   `ProofBundleEvidence.Status` is `recorded` with the expected latest bundle
   manifest/archive paths, verify `SupportBundleEvidence.Status` is `recorded`
   with the expected latest support-bundle manifest/archive paths, verify
   `ProofBundleEvidence.PrivacyRedactionApplied=true` and
   `SupportBundleEvidence.PrivacyRedactionApplied=true`, verify
   `ProofBundleEvidence.PublicationScanPassed=true` and
   `SupportBundleEvidence.PublicationScanPassed=true` (which also means the
   paired zips were readable, contained their manifest-listed entries, and had
   archived manifests matching the latest status fields), verify
   `ProofBundleEvidence.InferencePerformanceStatus` and its
   sample/lane/upstream-request-id/processing/phase-timing/token receipt
   counts match the expected model-serving proof window, verify
   `ProofBundleEvidence.TtsCallCount`, `TtsFailureCount`, and
   `TtsSuccessEvidenceCount` match the expected audio proof window when TTS is
   enabled, verify
   `PackageVerificationEvidence.Status` is `verified` with the expected
   candidate package path and `PublicationScanPassed=true`, verify
   `ArtifactIntegrityEvidence.Status` is `recorded` with the expected
   `SHA256SUMS`, `SHA512SUMS`, and `checksums.json` paths, verify
   `FullAuditEvidence.Status` is `passed` with the expected latest audit
   bundle/report paths, and verify that
   `docs/openapi/palllm-sidecar-v1.json` matches `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-openapi.ps1 -Verify`.
5. Verify `NOTICE.md`, `THIRD_PARTY_NOTICES.md`, and `SECURITY.md` still match
   the current release posture.
6. Verify the packaged release still exposes `play.bat` as the primary
   player-facing entry point, bundles the expected sidecar publish under
   `sidecar\publish\`, and that `PLAYER_README.txt` matches that flow and only
   names docs that are present in the zip. Also verify the package-root
   `CHANGELOG.md` is the compact player-facing version generated by
   `Write-PlayerChangelog`, not the source repository changelog.
7. Only then cut the tag and publish the zip.
