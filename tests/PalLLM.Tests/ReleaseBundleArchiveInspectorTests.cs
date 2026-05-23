using System.IO.Compression;
using System.Text.Json;
using PalLLM.Domain.Integration;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

// Pass 217 - direct unit tests for the archive-entry safety filter that
// protects /api/release/readiness from "zip-slip"-style untrusted-archive
// inputs. Until this pass the filter was only covered by 2 integration
// tests (`..` segment in IncludedFiles, leading-`/` absolute path); the
// remaining rejection branches (backslash-encoded `..`, drive-qualified
// paths, lone `.` segments, control characters in entry names, empty
// names) had no direct regression coverage. A regression that loosened
// the filter on, say, drive-letter rejection would have shipped silently
// because the existing integration tests do not exercise that branch.
//
// These unit tests reach the internal helper through the Pass-217
// `InternalsVisibleTo` declaration on the Sidecar csproj. Each case
// names exactly one rejection rule so a future failure points at the
// specific branch that broke.
public sealed class ReleaseBundleArchiveInspectorTests
{
    [Test]
    public async Task InspectProofBundle_RejectsArchivedManifestWithContradictoryProofStatus()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "palllm-proof-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            string archivePath = Path.Combine(tempRoot, "latest-proof-bundle.zip");
            DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;

            ReleaseProofBundleEvidenceSnapshot recorded = new()
            {
                Status = "recorded",
                CapturedAtUtc = capturedAtUtc,
                ArchivePath = archivePath,
                ReleasePublicationStatus = "caution",
                BridgeProofStatus = "delivery_proven",
                SmokeEvidenceStatus = "recorded",
                NativeProofEvidenceStatus = "proven",
                TtsEnabled = true,
                TtsCallCount = 4,
                TtsFailureCount = 1,
                TtsSuccessEvidenceCount = 3,
                AsrEnabled = true,
                AsrCallCount = 6,
                AsrFailureCount = 2,
                AsrSuccessEvidenceCount = 4,
                AsrEndpointingReceiptCount = 3,
                AsrBargeInCount = 1,
                AsrEndpointingReviewCount = 1,
                AsrConfidenceReceiptCount = 2,
                AsrConfidenceReviewCount = 1,
                AsrTimingReceiptCount = 2,
                AsrTimingReviewCount = 1,
                AsrQualityReceiptCount = 2,
                AsrQualityReviewCount = 1,
                AsrUpstreamRequestIdReceiptCount = 2,
                AsrUpstreamProcessingReceiptCount = 2,
                AsrUpstreamPhaseTimingReceiptCount = 1,
                NativeHudConfigSource = "mod_override_file",
                NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
                PrivacyRedactionApplied = true,
                PrivacyRedactionCheckedFileCount = 4,
                PrivacyRedactionRedactedFileCount = 0,
                PrivacyRedactionRuleHits = [],
                PublicationScanPassed = true,
                PublicationScanCheckedFileCount = 4,
                PublicationScanViolations = [],
                IncludedFiles = ["bridge-proof.json", "latest-native-proof.json", "proof-bundle.json"],
                MissingOptionalFiles = [],
                CurrentBlockers = ["The product name itself remains scope-coupled to a third-party game."],
                ReadyEvidence = ["HUD target bound", "Delivery rendered"],
            };

            ReleaseProofBundleEvidenceSnapshot archived = new()
            {
                Status = recorded.Status,
                CapturedAtUtc = capturedAtUtc,
                ArchivePath = archivePath,
                ReleasePublicationStatus = recorded.ReleasePublicationStatus,
                BridgeProofStatus = recorded.BridgeProofStatus,
                SmokeEvidenceStatus = recorded.SmokeEvidenceStatus,
                NativeProofEvidenceStatus = "missing",
                TtsEnabled = recorded.TtsEnabled,
                TtsCallCount = recorded.TtsCallCount,
                TtsFailureCount = recorded.TtsFailureCount,
                TtsSuccessEvidenceCount = recorded.TtsSuccessEvidenceCount,
                AsrEnabled = recorded.AsrEnabled,
                AsrCallCount = recorded.AsrCallCount,
                AsrFailureCount = recorded.AsrFailureCount,
                AsrSuccessEvidenceCount = recorded.AsrSuccessEvidenceCount,
                AsrEndpointingReceiptCount = recorded.AsrEndpointingReceiptCount,
                AsrBargeInCount = recorded.AsrBargeInCount,
                AsrEndpointingReviewCount = recorded.AsrEndpointingReviewCount,
                AsrConfidenceReceiptCount = recorded.AsrConfidenceReceiptCount,
                AsrConfidenceReviewCount = recorded.AsrConfidenceReviewCount,
                AsrTimingReceiptCount = recorded.AsrTimingReceiptCount,
                AsrTimingReviewCount = recorded.AsrTimingReviewCount,
                AsrQualityReceiptCount = recorded.AsrQualityReceiptCount,
                AsrQualityReviewCount = recorded.AsrQualityReviewCount,
                AsrUpstreamRequestIdReceiptCount = recorded.AsrUpstreamRequestIdReceiptCount,
                AsrUpstreamProcessingReceiptCount = recorded.AsrUpstreamProcessingReceiptCount,
                AsrUpstreamPhaseTimingReceiptCount = recorded.AsrUpstreamPhaseTimingReceiptCount,
                NativeHudConfigSource = recorded.NativeHudConfigSource,
                NativeHudConfigPath = recorded.NativeHudConfigPath,
                PrivacyRedactionApplied = recorded.PrivacyRedactionApplied,
                PrivacyRedactionCheckedFileCount = recorded.PrivacyRedactionCheckedFileCount,
                PrivacyRedactionRedactedFileCount = recorded.PrivacyRedactionRedactedFileCount,
                PrivacyRedactionRuleHits = recorded.PrivacyRedactionRuleHits,
                PublicationScanPassed = recorded.PublicationScanPassed,
                PublicationScanCheckedFileCount = recorded.PublicationScanCheckedFileCount,
                PublicationScanViolations = recorded.PublicationScanViolations,
                IncludedFiles = recorded.IncludedFiles,
                MissingOptionalFiles = recorded.MissingOptionalFiles,
                CurrentBlockers = recorded.CurrentBlockers,
                ReadyEvidence = recorded.ReadyEvidence,
            };

            await WriteArchiveAsync(archivePath, "proof-bundle.json", archived, recorded.IncludedFiles);

            ReleaseBundleArchiveInspection result =
                ReleaseBundleArchiveInspector.InspectProofBundle(recorded, maxManifestBytes: 1_000_000);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Summary, Does.Contain("stale or mismatched 'proof-bundle.json'"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public async Task InspectSupportBundle_RejectsArchivedManifestWithContradictoryAuditStatus()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "palllm-support-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            string archivePath = Path.Combine(tempRoot, "latest-support-bundle.zip");
            DateTimeOffset capturedAtUtc = DateTimeOffset.UtcNow;

            ReleaseSupportBundleEvidenceSnapshot recorded = new()
            {
                Status = "recorded",
                CapturedAtUtc = capturedAtUtc,
                ArchivePath = archivePath,
                RuntimeRoot = @"D:\PalLLM\Runtime",
                LaunchEvidenceStatus = "recorded",
                SmokeEvidenceStatus = "recorded",
                NativeProofEvidenceStatus = "proven",
                ProofBundleEvidenceStatus = "recorded",
                PackageVerificationEvidenceStatus = "verified",
                FullAuditEvidenceStatus = "passed",
                NativeHudConfigPath = @"D:\Games\Palworld\Pal\Binaries\Win64\ue4ss\Mods\PalLLM\config\native-hud.lua",
                PrivacyRedactionApplied = true,
                PrivacyRedactionCheckedFileCount = 5,
                PrivacyRedactionRedactedFileCount = 1,
                PrivacyRedactionRuleHits = ["windows-user-path"],
                PublicationScanPassed = true,
                PublicationScanCheckedFileCount = 5,
                PublicationScanViolations = [],
                IncludedFiles = ["health.json", "release-readiness.json", "support-bundle.json"],
                MissingOptionalFiles = [],
                CurrentBlockers = [],
                ReadyEvidence = ["Support bundle captured"],
            };

            ReleaseSupportBundleEvidenceSnapshot archived = new()
            {
                Status = recorded.Status,
                CapturedAtUtc = capturedAtUtc,
                ArchivePath = archivePath,
                RuntimeRoot = recorded.RuntimeRoot,
                LaunchEvidenceStatus = recorded.LaunchEvidenceStatus,
                SmokeEvidenceStatus = recorded.SmokeEvidenceStatus,
                NativeProofEvidenceStatus = recorded.NativeProofEvidenceStatus,
                ProofBundleEvidenceStatus = recorded.ProofBundleEvidenceStatus,
                PackageVerificationEvidenceStatus = recorded.PackageVerificationEvidenceStatus,
                FullAuditEvidenceStatus = "missing",
                NativeHudConfigPath = recorded.NativeHudConfigPath,
                PrivacyRedactionApplied = recorded.PrivacyRedactionApplied,
                PrivacyRedactionCheckedFileCount = recorded.PrivacyRedactionCheckedFileCount,
                PrivacyRedactionRedactedFileCount = recorded.PrivacyRedactionRedactedFileCount,
                PrivacyRedactionRuleHits = recorded.PrivacyRedactionRuleHits,
                PublicationScanPassed = recorded.PublicationScanPassed,
                PublicationScanCheckedFileCount = recorded.PublicationScanCheckedFileCount,
                PublicationScanViolations = recorded.PublicationScanViolations,
                IncludedFiles = recorded.IncludedFiles,
                MissingOptionalFiles = recorded.MissingOptionalFiles,
                CurrentBlockers = recorded.CurrentBlockers,
                ReadyEvidence = recorded.ReadyEvidence,
            };

            await WriteArchiveAsync(archivePath, "support-bundle.json", archived, recorded.IncludedFiles);

            ReleaseBundleArchiveInspection result =
                ReleaseBundleArchiveInspector.InspectSupportBundle(recorded, maxManifestBytes: 1_000_000);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Summary, Does.Contain("stale or mismatched 'support-bundle.json'"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    // ---------- Accepted entry names ----------

    [TestCase("manifest.json")]
    [TestCase("logs/run-001.log")]
    [TestCase("nested/dir/file.txt")]
    [TestCase("a")]
    [TestCase("dir/.hidden")]
    [TestCase("dir/..hidden")]
    public void IsSafeArchiveEntryName_AcceptsCanonicalRelativePaths(string entryName)
    {
        bool accepted = ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName);

        Assert.That(accepted, Is.True, $"Expected '{entryName}' to be accepted as a safe archive entry name.");
    }

    // ---------- Empty / whitespace ----------

    [TestCase("")]
    [TestCase("   ")]
    public void IsSafeArchiveEntryName_RejectsEmptyOrWhitespaceNames(string entryName)
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName), Is.False);
    }

    // ---------- Absolute / drive-qualified ----------

    [TestCase("/etc/passwd")]
    [TestCase("/absolute-support-log.json")]
    public void IsSafeArchiveEntryName_RejectsLeadingSlash(string entryName)
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName), Is.False);
    }

    [TestCase("C:/Windows/system32/evil.dll")]
    [TestCase("D:windows/file.txt")]
    [TestCase("zone:Identifier")]
    public void IsSafeArchiveEntryName_RejectsColonInEntryName(string entryName)
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName), Is.False);
    }

    // ---------- Path-traversal ----------

    [TestCase("../outside.txt")]
    [TestCase("dir/../../escape.txt")]
    [TestCase("..")]
    public void IsSafeArchiveEntryName_RejectsDoubleDotSegmentForwardSlash(string entryName)
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName), Is.False);
    }

    [TestCase(@"..\outside.txt")]
    [TestCase(@"dir\..\..\escape.txt")]
    public void IsSafeArchiveEntryName_RejectsDoubleDotSegmentBackslash(string entryName)
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName), Is.False);
    }

    // ---------- Lone-dot segments ----------

    [TestCase("./file.txt")]
    [TestCase("dir/./file.txt")]
    [TestCase(".")]
    public void IsSafeArchiveEntryName_RejectsSingleDotSegment(string entryName)
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName), Is.False);
    }

    // ---------- Control characters ----------
    // Each [TestCase] string uses C# source-level escape sequences so the
    // file stays pure ASCII text on disk. The C# compiler interprets `\t`,
    // `\n`, `\0`, `\a` as the corresponding control characters at runtime,
    // which is what `IsSafeArchiveEntryName` rejects via `char.IsControl`.

    [Test]
    public void IsSafeArchiveEntryName_RejectsTabCharacter()
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName("logs/file\twith\ttab.txt"), Is.False);
    }

    [Test]
    public void IsSafeArchiveEntryName_RejectsNewlineCharacter()
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName("logs/file\nnewline.txt"), Is.False);
    }

    [Test]
    public void IsSafeArchiveEntryName_RejectsNulCharacter()
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName("logs/file\0nul.txt"), Is.False);
    }

    [Test]
    public void IsSafeArchiveEntryName_RejectsBellCharacter()
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName("logs/\abel.txt"), Is.False);
    }

    // ---------- Empty path segment (consecutive slashes) ----------

    [TestCase("dir//file.txt")]
    [TestCase("dir///file.txt")]
    public void IsSafeArchiveEntryName_RejectsEmptyPathSegments(string entryName)
    {
        Assert.That(ReleaseBundleArchiveInspector.IsSafeArchiveEntryName(entryName), Is.False);
    }

    private static async Task WriteArchiveAsync<TManifest>(
        string archivePath,
        string manifestEntryName,
        TManifest manifest,
        IEnumerable<string> includedFiles)
    {
        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        string[] entries = includedFiles
            .Append(manifestEntryName)
            .Where(entry => !string.IsNullOrWhiteSpace(entry))
            .Select(entry => entry.Replace('\\', '/').Trim().TrimStart('/'))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (string entryName in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(entryName);
            await using Stream stream = entry.Open();
            await using StreamWriter writer = new(stream);
            string content = string.Equals(entryName, manifestEntryName, StringComparison.Ordinal)
                ? JsonSerializer.Serialize(manifest)
                : "{}";
            await writer.WriteAsync(content);
        }
    }
}
