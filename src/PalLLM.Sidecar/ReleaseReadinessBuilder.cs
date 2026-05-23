using Microsoft.AspNetCore.Routing;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class ReleaseReadinessBuilder
{
    private static readonly string[] ConditionalReadPaths =
    [
        "/api/dashboard",
        "/api/features",
        "/api/describe",
        "/api/bridge/proof",
        "/api/inference/performance",
        "/api/mcp/upstream",
        "/api/release/readiness",
    ];

    private static readonly ReleaseSurfaceDescriptor[] FeaturedSurfaces =
    [
        new()
        {
            Id = "dashboard",
            Method = "GET",
            Path = "/",
            Area = "ops",
            Summary = "Static Field Console for high-signal runtime oversight.",
        },
        new()
        {
            Id = "metrics",
            Method = "GET",
            Path = "/metrics",
            Area = "ops",
            Summary = "Prometheus exposition for runtime and orchestration counters.",
        },
        new()
        {
            Id = "health-live",
            Method = "GET",
            Path = "/health/live",
            Area = "ops",
            Summary = "Liveness probe as application/health+json.",
        },
        new()
        {
            Id = "health-ready",
            Method = "GET",
            Path = "/health/ready",
            Area = "ops",
            Summary = "Readiness probe with dependency-oriented diagnostics.",
        },
        new()
        {
            Id = "openapi-json",
            Method = "GET",
            Path = "/openapi/v1.json",
            Area = "ops",
            Summary = "OpenAPI 3.1 JSON contract generated from the live minimal-API surface.",
        },
        new()
        {
            Id = "openapi-yaml",
            Method = "GET",
            Path = "/openapi/v1.yaml",
            Area = "ops",
            Summary = "OpenAPI 3.1 YAML variant of the live HTTP contract.",
        },
        new()
        {
            Id = "release-readiness",
            Method = "GET",
            Path = "/api/release/readiness",
            Area = "inspection",
            Summary = "Machine-readable release posture, audit commands, doc pointers, and publication blockers.",
        },
        new()
        {
            Id = "feature-catalog",
            Method = "GET",
            Path = "/api/features",
            Area = "inspection",
            Summary = "Canonical runtime feature catalog exposed to dashboards and automation.",
        },
        new()
        {
            Id = "bridge-proof",
            Method = "GET",
            Path = "/api/bridge/proof",
            Area = "inspection",
            Summary = "Machine-readable Palworld bridge proof snapshot showing native readiness, widget-seam evidence, and live request/delivery closure.",
        },
        new()
        {
            Id = "inference-performance",
            Method = "GET",
            Path = "/api/inference/performance",
            Area = "inspection",
            Summary = "Recent per-model live inference summary with bounded latency-budget assessment and token trends for the active chat and vision lanes.",
        },
        new()
        {
            Id = "dashboard-json",
            Method = "GET",
            Path = "/api/dashboard",
            Area = "inspection",
            Summary = "Aggregated dashboard snapshot with conditional caching and server-timing metadata.",
        },
        new()
        {
            Id = "mcp-upstream",
            Method = "GET",
            Path = "/api/mcp/upstream",
            Area = "mcp",
            Summary = "Discovered upstream MCP server snapshots for hub-style deployments.",
        },
        new()
        {
            Id = "mcp",
            Method = "POST",
            Path = "/mcp",
            Area = "protocol",
            Summary = "Streamable HTTP MCP endpoint for tools, resources, and prompts.",
        },
    ];

    private static readonly ReleaseAuditDescriptor[] Audits =
    [
        new()
        {
            Id = "full-audit",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run_full_audit.ps1",
            Purpose = "Build, tests, drift gates, public-copy audit, path-reference audit, candidate package verification, and a durable latest-full-audit artifact.",
        },
        new()
        {
            Id = "public-copy-audit",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/audit_public_copy.ps1",
            Purpose = "Release-facing copy neutrality and publication-guidance presence checks.",
        },
        new()
        {
            Id = "path-reference-audit",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/path_reference_audit.ps1",
            Purpose = "Repo-local path reference integrity across docs, scripts, and support files.",
        },
        new()
        {
            Id = "openapi-verify",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-openapi.ps1 -Verify",
            Purpose = "Verify the committed OpenAPI snapshot still matches the live sidecar contract.",
        },
        new()
        {
            Id = "sidecar-smoke",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-sidecar-smoke.ps1",
            Purpose = "Drive a local request -> outbox -> delivery -> feedback loop and persist the latest machine-readable smoke artifact under Runtime/ReleaseEvidence.",
        },
        new()
        {
            Id = "native-proof",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/run-native-proof.ps1",
            Purpose = "Poll the live Palworld bridge until native HUD readiness plus visible in-game delivery are proven, then persist the latest native-proof artifact under Runtime/ReleaseEvidence.",
        },
        new()
        {
            Id = "proof-bundle",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-release-proof-bundle.ps1",
            Purpose = "Capture the current release/readiness snapshot, bridge proof, and latest smoke/native-proof artifacts into a single durable Palworld validation bundle.",
        },
        new()
        {
            Id = "support-bundle",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/export-support-bundle.ps1",
            Purpose = "Capture a portable support bundle with the latest launch evidence, bridge proof, release-readiness snapshot, and proof artifacts for tester handoff.",
        },
        new()
        {
            Id = "package-verify",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-release-package.ps1",
            Purpose = "Validate a candidate PalLLM release zip or expanded package against the embedded manifest and persist the latest package-verification artifact under Runtime/ReleaseEvidence.",
        },
        new()
        {
            Id = "artifact-integrity",
            Command = "powershell -NoProfile -ExecutionPolicy Bypass -File scripts/compute-release-checksums.ps1",
            Purpose = "Compute SHA-256/SHA-512 release digest manifests and persist the latest artifact-integrity evidence under Runtime/ReleaseEvidence.",
        },
    ];

    private static readonly ReleaseDocumentDescriptor[] Documents =
    [
        new()
        {
            Id = "api-reference",
            Path = "docs/API.md",
            Purpose = "Human-readable HTTP contract reference paired with the live OpenAPI document.",
        },
        new()
        {
            Id = "doc-index",
            Path = "docs/INDEX.md",
            Purpose = "Diataxis map for operators, contributors, and integrators.",
        },
        new()
        {
            Id = "release-guide",
            Path = "docs/RELEASE.md",
            Purpose = "Publication checklist, guardrails, and current blockers.",
        },
        new()
        {
            Id = "openapi-snapshot",
            Path = "docs/openapi/palllm-sidecar-v1.json",
            Purpose = "Committed build-time OpenAPI snapshot for drift gates and SDK generation.",
        },
        new()
        {
            Id = "roadmap",
            Path = "docs/ROADMAP.md",
            Purpose = "Current delivery status, audited counts, and remaining work.",
        },
    ];

    private static readonly string[] PublicationBlockers =
    [
        "The product name itself remains scope-coupled to a third-party game.",
        "The shipped mod package, Lua bridge, and compatibility workflow still target Palworld and UE4SS explicitly by design.",
        "Some deeper technical docs remain compatibility-heavy because the current runtime still interoperates with those external surfaces.",
    ];

    public static ReleaseReadinessSnapshot Create(
        PalLlmRuntime runtime,
        EndpointDataSource endpointDataSource,
        PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(endpointDataSource);
        ArgumentNullException.ThrowIfNull(options);

        FeatureDescriptor[] features = runtime.GetFeatures().ToArray();
        int ready = CountFeaturesByStatus(features, "ready");
        int scaffolded = CountFeaturesByStatus(features, "scaffolded");
        int deferred = CountFeaturesByStatus(features, "deferred");
        ReleaseSmokeEvidenceSnapshot smokeEvidence = ReleaseSmokeEvidenceBuilder.ReadLatest(options);
        ReleaseNativeProofEvidenceSnapshot nativeProofEvidence = ReleaseNativeProofEvidenceBuilder.ReadLatest(options);
        ReleaseProofBundleEvidenceSnapshot proofBundleEvidence = ReleaseProofBundleEvidenceBuilder.ReadLatest(options);
        ReleaseSupportBundleEvidenceSnapshot supportBundleEvidence = ReleaseSupportBundleEvidenceBuilder.ReadLatest(options);
        ReleasePackageVerificationEvidenceSnapshot packageVerificationEvidence = ReleasePackageVerificationEvidenceBuilder.ReadLatest(options);
        ReleaseArtifactIntegrityEvidenceSnapshot artifactIntegrityEvidence = ReleaseArtifactIntegrityEvidenceBuilder.ReadLatest(options);
        ReleaseFullAuditEvidenceSnapshot fullAuditEvidence = ReleaseFullAuditEvidenceBuilder.ReadLatest(options);

        return new ReleaseReadinessSnapshot
        {
            Runtime = new ReleaseRuntimeSurfaceSummary
            {
                AdapterName = runtime.Adapter.AdapterName,
                ApiRouteCount = CountDistinctRoutes(endpointDataSource, path => path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)),
                ProtocolRouteCount = CountDistinctRoutes(endpointDataSource, path => path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)),
                FeaturedOperationalSurfaceCount = FeaturedSurfaces.Count(surface => string.Equals(surface.Area, "ops", StringComparison.Ordinal)),
                DashboardPath = "/",
                MetricsPath = "/metrics",
                OpenApiJsonPath = "/openapi/v1.json",
                OpenApiYamlPath = "/openapi/v1.yaml",
                McpPath = "/mcp",
                ConditionalReadPaths = ConditionalReadPaths,
            },
            Features = new ReleaseFeatureCatalogSummary
            {
                Total = features.Length,
                Ready = ready,
                Scaffolded = scaffolded,
                Deferred = deferred,
                Other = Math.Max(0, features.Length - ready - scaffolded - deferred),
            },
            Publication = new ReleasePublicationSummary
            {
                Status = "caution",
                NextRecommendedPass = BuildNextRecommendedPass(smokeEvidence, nativeProofEvidence, proofBundleEvidence, supportBundleEvidence, packageVerificationEvidence, artifactIntegrityEvidence, fullAuditEvidence),
                NextRecommendedCommand = BuildNextRecommendedCommand(smokeEvidence, nativeProofEvidence, proofBundleEvidence, supportBundleEvidence, packageVerificationEvidence, artifactIntegrityEvidence, fullAuditEvidence),
                CurrentBlockers = PublicationBlockers,
            },
            SmokeEvidence = smokeEvidence,
            NativeProofEvidence = nativeProofEvidence,
            ProofBundleEvidence = proofBundleEvidence,
            SupportBundleEvidence = supportBundleEvidence,
            PackageVerificationEvidence = packageVerificationEvidence,
            ArtifactIntegrityEvidence = artifactIntegrityEvidence,
            FullAuditEvidence = fullAuditEvidence,
            Surfaces = FeaturedSurfaces,
            Audits = Audits,
            Documents = Documents,
        };
    }

    private static string BuildNextRecommendedPass(
        ReleaseSmokeEvidenceSnapshot smokeEvidence,
        ReleaseNativeProofEvidenceSnapshot nativeProofEvidence,
        ReleaseProofBundleEvidenceSnapshot proofBundleEvidence,
        ReleaseSupportBundleEvidenceSnapshot supportBundleEvidence,
        ReleasePackageVerificationEvidenceSnapshot packageVerificationEvidence,
        ReleaseArtifactIntegrityEvidenceSnapshot artifactIntegrityEvidence,
        ReleaseFullAuditEvidenceSnapshot fullAuditEvidence)
    {
        ArgumentNullException.ThrowIfNull(smokeEvidence);
        ArgumentNullException.ThrowIfNull(nativeProofEvidence);
        ArgumentNullException.ThrowIfNull(proofBundleEvidence);
        ArgumentNullException.ThrowIfNull(supportBundleEvidence);
        ArgumentNullException.ThrowIfNull(packageVerificationEvidence);
        ArgumentNullException.ThrowIfNull(artifactIntegrityEvidence);
        ArgumentNullException.ThrowIfNull(fullAuditEvidence);

        if (!string.Equals(nativeProofEvidence.Status, "proven", StringComparison.OrdinalIgnoreCase))
        {
            return nativeProofEvidence.Status switch
            {
                "missing" =>
                    "Capture a live Palworld native-proof artifact with scripts/run-native-proof.ps1 while the game is running, then use that evidence to close the HUD-bind and in-game delivery blocker before packaging.",
                "invalid" =>
                    "Repair or recapture the latest native-proof artifact with scripts/run-native-proof.ps1 so release-readiness reflects real Palworld HUD delivery evidence before the next packaging pass.",
                _ =>
                    "Resolve the current live Palworld native-proof blockers, then rerun scripts/run-native-proof.ps1 until the release surface reports proven native HUD delivery.",
            };
        }

        if (string.Equals(nativeProofEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh the live Palworld native-proof artifact with scripts/run-native-proof.ps1 so the HUD-bind proof is current for this release candidate.";
        }

        if (!string.Equals(smokeEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase))
        {
            return smokeEvidence.Status switch
            {
                "missing" =>
                    "Capture a fresh Palworld smoke artifact with scripts/run-sidecar-smoke.ps1, then use that proof to validate the HUD bind and remaining native delivery seams before packaging.",
                "invalid" =>
                    "Repair or recapture the latest Palworld smoke artifact so release-readiness reflects real bridge proof before the next packaging pass.",
                _ =>
                    "Refresh the Palworld smoke artifact with scripts/run-sidecar-smoke.ps1 so release-readiness has a current synthetic bridge-loop proof before packaging.",
            };
        }

        if (string.Equals(smokeEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh the Palworld smoke artifact with scripts/run-sidecar-smoke.ps1 so the synthetic bridge-loop proof is current for this release candidate.";
        }

        if (!string.Equals(proofBundleEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase))
        {
            return proofBundleEvidence.Status switch
            {
                "missing" =>
                    "Export a Palworld release proof bundle with scripts/export-release-proof-bundle.ps1 so the current release/readiness snapshot, bridge proof, smoke artifact, and native-proof artifact travel together with the candidate build.",
                "invalid" =>
                    "Repair or recapture the latest Palworld release proof bundle so release-readiness points at a valid archive before the next packaging pass.",
                _ =>
                    "Refresh the Palworld release proof bundle with scripts/export-release-proof-bundle.ps1 so the current smoke/native-proof evidence and bridge snapshots are archived together before packaging.",
            };
        }

        if (!proofBundleEvidence.PrivacyRedactionApplied)
        {
            return "Recapture the Palworld release proof bundle with scripts/export-release-proof-bundle.ps1 so the portable proof archive is privacy-redacted before packaging or tester sharing.";
        }

        if (!proofBundleEvidence.PublicationScanPassed)
        {
            return "Recapture the Palworld release proof bundle with scripts/export-release-proof-bundle.ps1 so the portable proof archive passes the publication text-surface scan before packaging.";
        }

        if (string.Equals(proofBundleEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh the Palworld release proof bundle with scripts/export-release-proof-bundle.ps1 so the archived readiness snapshot matches the current candidate build.";
        }

        if (!string.Equals(fullAuditEvidence.Status, "passed", StringComparison.OrdinalIgnoreCase))
        {
            return fullAuditEvidence.Status switch
            {
                "missing" =>
                    "Run scripts/run_full_audit.ps1 so build, tests, drift gates, and candidate package verification are captured as a durable full-audit artifact before clean-machine validation.",
                "invalid" =>
                    "Repair or rerun scripts/run_full_audit.ps1 so release-readiness points at a valid latest full-audit artifact before clean-machine validation.",
                _ =>
                    "Resolve the failing full-audit steps and rerun scripts/run_full_audit.ps1 until release-readiness reports a passed full-audit artifact.",
            };
        }

        if (string.Equals(fullAuditEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh the durable full-audit artifact with scripts/run_full_audit.ps1 so build, drift, and package-verification truth are current for this release candidate.";
        }

        if (!string.Equals(packageVerificationEvidence.Status, "verified", StringComparison.OrdinalIgnoreCase))
        {
            return packageVerificationEvidence.Status switch
            {
                "missing" =>
                    "Build or verify a PalLLM release package with scripts/package-release.ps1 or scripts/verify-release-package.ps1 so release-readiness records a structurally verified candidate zip before clean-machine validation.",
                "invalid" =>
                    "Repair or rerun scripts/verify-release-package.ps1 against the latest candidate package so release-readiness points at a valid package-verification artifact before clean-machine validation.",
                _ =>
                    "Rerun scripts/verify-release-package.ps1 against the latest candidate package until release-readiness reports a verified manifest-backed package artifact.",
            };
        }

        if (string.Equals(packageVerificationEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh the package verification artifact with scripts/verify-release-package.ps1 against the current candidate zip so release-readiness reflects a current clean-machine package check.";
        }

        if (!string.Equals(artifactIntegrityEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase))
        {
            return artifactIntegrityEvidence.Status switch
            {
                "missing" =>
                    "Run scripts/compute-release-checksums.ps1 against the current packaging directory so SHA256SUMS, SHA512SUMS, checksums.json, and artifact-integrity evidence travel with the candidate zip.",
                "invalid" =>
                    "Repair or rerun scripts/compute-release-checksums.ps1 so release-readiness points at valid checksum manifests before publication.",
                _ =>
                    "Refresh artifact-integrity evidence with scripts/compute-release-checksums.ps1 so the current candidate package has local digest manifests before publication.",
            };
        }

        if (string.Equals(artifactIntegrityEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh artifact-integrity evidence with scripts/compute-release-checksums.ps1 so SHA256SUMS and checksums.json match the current candidate zip.";
        }

        if (!string.Equals(supportBundleEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase))
        {
            return supportBundleEvidence.Status switch
            {
                "missing" =>
                    "Capture a portable support bundle with scripts/export-support-bundle.ps1 so the current launch evidence, bridge proof, readiness snapshot, and proof artifacts are ready for tester handoff.",
                "invalid" =>
                    "Repair or recapture the latest support bundle with scripts/export-support-bundle.ps1 so release-readiness points at a valid portable support archive.",
                _ =>
                    "Refresh the support bundle with scripts/export-support-bundle.ps1 so the latest readiness and proof artifacts travel together for tester handoff.",
            };
        }

        if (!supportBundleEvidence.PrivacyRedactionApplied)
        {
            return "Recapture the support bundle with scripts/export-support-bundle.ps1 so the portable tester handoff archive is privacy-redacted before sharing.";
        }

        if (!supportBundleEvidence.PublicationScanPassed)
        {
            return "Recapture the support bundle with scripts/export-support-bundle.ps1 so the portable tester handoff archive passes the publication text-surface scan.";
        }

        if (string.Equals(supportBundleEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return "Refresh the support bundle with scripts/export-support-bundle.ps1 so the portable diagnostic archive matches the current candidate build.";
        }

        return "Keep the recorded Palworld proof bundle, support bundle, and verified package artifact alongside the candidate zip, then use them to drive clean-machine validation and the remaining native delivery closure work.";
    }

    private static string BuildNextRecommendedCommand(
        ReleaseSmokeEvidenceSnapshot smokeEvidence,
        ReleaseNativeProofEvidenceSnapshot nativeProofEvidence,
        ReleaseProofBundleEvidenceSnapshot proofBundleEvidence,
        ReleaseSupportBundleEvidenceSnapshot supportBundleEvidence,
        ReleasePackageVerificationEvidenceSnapshot packageVerificationEvidence,
        ReleaseArtifactIntegrityEvidenceSnapshot artifactIntegrityEvidence,
        ReleaseFullAuditEvidenceSnapshot fullAuditEvidence)
    {
        ArgumentNullException.ThrowIfNull(smokeEvidence);
        ArgumentNullException.ThrowIfNull(nativeProofEvidence);
        ArgumentNullException.ThrowIfNull(proofBundleEvidence);
        ArgumentNullException.ThrowIfNull(supportBundleEvidence);
        ArgumentNullException.ThrowIfNull(packageVerificationEvidence);
        ArgumentNullException.ThrowIfNull(artifactIntegrityEvidence);
        ArgumentNullException.ThrowIfNull(fullAuditEvidence);

        if (!string.Equals(nativeProofEvidence.Status, "proven", StringComparison.OrdinalIgnoreCase)
            || string.Equals(nativeProofEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptCommand("scripts/run-native-proof.ps1");
        }

        if (!string.Equals(smokeEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(smokeEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptCommand("scripts/run-sidecar-smoke.ps1");
        }

        if (!string.Equals(proofBundleEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase)
            || !proofBundleEvidence.PrivacyRedactionApplied
            || !proofBundleEvidence.PublicationScanPassed
            || string.Equals(proofBundleEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptCommand("scripts/export-release-proof-bundle.ps1");
        }

        if (!string.Equals(fullAuditEvidence.Status, "passed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fullAuditEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptCommand("scripts/run_full_audit.ps1");
        }

        if (!string.Equals(packageVerificationEvidence.Status, "verified", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(packageVerificationEvidence.Status, "missing", StringComparison.OrdinalIgnoreCase)
                ? ScriptCommand("scripts/package-release.ps1")
                : ScriptCommand("scripts/verify-release-package.ps1");
        }

        if (string.Equals(packageVerificationEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptCommand("scripts/verify-release-package.ps1");
        }

        if (!string.Equals(artifactIntegrityEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase)
            || string.Equals(artifactIntegrityEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptCommand("scripts/compute-release-checksums.ps1");
        }

        if (!string.Equals(supportBundleEvidence.Status, "recorded", StringComparison.OrdinalIgnoreCase)
            || !supportBundleEvidence.PrivacyRedactionApplied
            || !supportBundleEvidence.PublicationScanPassed
            || string.Equals(supportBundleEvidence.FreshnessStatus, "stale", StringComparison.OrdinalIgnoreCase))
        {
            return ScriptCommand("scripts/export-support-bundle.ps1");
        }

        return string.Empty;
    }

    private static string ScriptCommand(string scriptPath) =>
        $"powershell -NoProfile -ExecutionPolicy Bypass -File {scriptPath}";

    private static int CountDistinctRoutes(
        EndpointDataSource endpointDataSource,
        Func<string, bool> predicate)
    {
        return endpointDataSource.Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Where(predicate)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    private static int CountFeaturesByStatus(
        IEnumerable<FeatureDescriptor> features,
        string status) =>
        features.Count(feature => string.Equals(feature.Status, status, StringComparison.OrdinalIgnoreCase));
}
