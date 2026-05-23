using System.Text.Json;
using NUnit.Framework;
using PalLLM.Domain.Packs;

namespace PalLLM.Tests;

/// <summary>
/// Pass 39 / C6 — regression coverage for
/// <see cref="PersonalityPackValidator"/>. Pinned contract:
/// <list type="bullet">
///   <item>Missing pack root → IsValid=false, manifest-present check fails.</item>
///   <item>Missing prompt fragment → IsValid=false with specific issue.</item>
///   <item>Oversized prompt fragment → IsValid=false.</item>
///   <item>Disallowed audio extension → IsValid=false.</item>
///   <item>Voice reference without consent provenance → IsValid=false.</item>
///   <item>Valid pack + matching hash → IsValid=true.</item>
///   <item>Tampered content → hash mismatch → IsValid=false.</item>
/// </list>
/// </summary>
[TestFixture]
public class PersonalityPackValidatorTests
{
    private string _root = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "PalLLM.Tests.Packs", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Test]
    public void Validate_MissingPackRoot_ReturnsInvalid()
    {
        string missing = Path.Combine(_root, "nope");
        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(missing);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Issues.Any(i => i.Contains("does not exist", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Validate_ValidPack_WithMatchingHash_ReturnsValid()
    {
        string packRoot = BuildPack(promptBody: "You are warm and curious.");
        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.True,
            "Pack with complete manifest + matching ContentHash must validate. Issues: "
            + string.Join("; ", result.Issues));
        Assert.That(result.Manifest, Is.Not.Null);
        Assert.That(result.Checks.All(c => c.Status == "pass"), Is.True);
    }

    [Test]
    public void Validate_LocalVoiceAdapterAndMemoryMetadata_WithMatchingHash_ReturnsValid()
    {
        string packRoot = BuildPack(promptBody: "You are warm and curious.");
        Directory.CreateDirectory(Path.Combine(packRoot, "adapters"));
        File.WriteAllBytes(Path.Combine(packRoot, "voice-ref.mp3"), new byte[] { 1, 2, 3, 4 });
        File.WriteAllBytes(Path.Combine(packRoot, "adapters", "warm.safetensors"), new byte[] { 5, 6, 7, 8 });
        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            voiceRefPath: "voice-ref.mp3",
            voiceConsent: "self_recorded",
            voiceConsentNotes: "Recorded by the pack author for this test pack.",
            loraAdapterPath: "adapters/warm.safetensors",
            memoryNamespace: "warm-companion-main"));
        RecomputeAndRewriteManifest(packRoot);

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.True,
            "Voice reference + adapter metadata should validate when paths are local, present, and hash-covered. Issues: "
            + string.Join("; ", result.Issues));
        Assert.That(result.Manifest!.VoiceRefPath, Is.EqualTo("voice-ref.mp3"));
        Assert.That(result.Manifest.VoiceConsent, Is.EqualTo("self_recorded"));
        Assert.That(result.Manifest.LoraAdapterPath, Is.EqualTo("adapters/warm.safetensors"));
        Assert.That(result.Manifest.MemoryNamespace, Is.EqualTo("warm-companion-main"));
        Assert.That(result.Checks, Has.Some.Matches<PersonalityPackCheck>(
            check => check.Id == "voice-ref-consent-declared" && check.Status == "pass"));
        Assert.That(result.Checks, Has.Some.Matches<PersonalityPackCheck>(
            check => check.Id == "voice-ref-file-present" && check.Status == "pass"));
        Assert.That(result.Checks, Has.Some.Matches<PersonalityPackCheck>(
            check => check.Id == "lora-adapter-file-present" && check.Status == "pass"));
        Assert.That(result.Checks, Has.Some.Matches<PersonalityPackCheck>(
            check => check.Id == "memory-namespace-shape" && check.Status == "pass"));
    }

    [Test]
    public void Validate_UnsafeVoiceAdapterAndMemoryMetadata_ReturnsInvalid()
    {
        string packRoot = BuildPack(promptBody: "prompt");
        Directory.CreateDirectory(Path.Combine(packRoot, "audio"));
        File.WriteAllBytes(Path.Combine(packRoot, "voice-ref.exe"), new byte[] { 1, 2, 3 });
        File.WriteAllBytes(Path.Combine(packRoot, "adapter.bin"), new byte[] { 4, 5, 6 });
        using (FileStream stream = File.Create(Path.Combine(packRoot, "audio", "huge.opus")))
        {
            stream.SetLength(PersonalityPackValidator.MaxAudioSampleBytes + 1L);
        }

        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            voiceRefPath: "https://example.invalid/voice-ref.wav",
            loraAdapterPath: "adapter.bin",
            memoryNamespace: "Bad Namespace",
            audioSamples: ["audio/huge.opus"]));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Issues, Has.Some.Contains("VoiceConsent is required when VoiceRefPath is set"));
        Assert.That(result.Issues, Has.Some.Contains("VoiceRefPath must be a pack-relative local file"));
        Assert.That(result.Issues, Has.Some.Contains("LoraAdapterPath adapter.bin uses disallowed extension"));
        Assert.That(result.Issues, Has.Some.Contains("MemoryNamespace must be kebab-case"));
        Assert.That(result.Issues, Has.Some.Contains("Audio sample audio/huge.opus exceeds"));
    }

    [Test]
    public void Validate_PublicationUnsafePackText_ReturnsInvalid()
    {
        string packRoot = BuildPack(
            promptBody: "Official Palworld prompt sponsored by Pocketpair, with Pok\u00E9mon banter, Qwen tuning notes, and a lawyer-proof release claim.");
        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            voiceConsentNotes: "Qwen license note says this voice pack is lawyer-proof."));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Checks, Has.Some.Matches<PersonalityPackCheck>(
            check => check.Id == "publication-safety" && check.Status == "fail"));
        Assert.That(result.Issues, Has.Some.Matches<string>(
            issue => issue.Contains("official endorsement", StringComparison.OrdinalIgnoreCase) &&
                     issue.Contains("Official Palworld", StringComparison.Ordinal)));
        Assert.That(result.Issues, Has.Some.Matches<string>(
            issue => issue.Contains("third-party IP", StringComparison.OrdinalIgnoreCase) &&
                     issue.Contains("Pok\u00E9mon", StringComparison.Ordinal)));
        Assert.That(result.Issues, Has.Some.Matches<string>(
            issue => issue.Contains("third-party model", StringComparison.OrdinalIgnoreCase) &&
                     issue.Contains("Qwen", StringComparison.Ordinal)));
        Assert.That(result.Issues, Has.Some.Matches<string>(
            issue => issue.Contains("legal", StringComparison.OrdinalIgnoreCase) &&
                     issue.Contains("lawyer-proof", StringComparison.OrdinalIgnoreCase)));
        Assert.That(result.Issues, Has.Some.Matches<string>(
            issue => issue.Contains("VoiceConsentNotes", StringComparison.Ordinal) &&
                     issue.Contains("third-party model", StringComparison.OrdinalIgnoreCase)));
    }

    [Test]
    public void Validate_MissingPromptFragment_FailsWithSpecificIssue()
    {
        string packRoot = BuildPack(promptBody: "prompt");
        File.Delete(Path.Combine(packRoot, "prompt.md"));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Issues.Any(i => i.Contains("prompt fragment missing", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Validate_OversizedPromptFragment_FailsBudgetCheck()
    {
        string oversized = new('x', PersonalityPackValidator.MaxPromptBytes + 500);
        string packRoot = BuildPack(promptBody: oversized);

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Issues.Any(i => i.Contains("exceeds", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Validate_DisallowedAudioExtension_Fails()
    {
        string packRoot = BuildPack(
            promptBody: "prompt",
            audioSamples: new[] { "audio/sample.mp3" });
        Directory.CreateDirectory(Path.Combine(packRoot, "audio"));
        File.WriteAllBytes(Path.Combine(packRoot, "audio/sample.mp3"), new byte[] { 1, 2, 3 });
        RecomputeAndRewriteManifest(packRoot);

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Issues.Any(i => i.Contains("disallowed extension", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Validate_TamperedContent_FailsHashCheck()
    {
        string packRoot = BuildPack(promptBody: "Original prompt.");
        // Tamper: change prompt body after manifest hash was computed.
        File.WriteAllText(Path.Combine(packRoot, "prompt.md"), "Tampered prompt body.");

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Issues.Any(i => i.Contains("ContentHash mismatch", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Validate_RejectsEscapedPaths_BlankContentHash_AndOversizedManifest()
    {
        string escapedPack = BuildPack(promptBody: "prompt");
        File.WriteAllText(Path.Combine(_root, "outside.md"), "outside");
        RewriteManifest(escapedPack, manifest => CopyManifest(
            manifest,
            promptPath: Path.Combine("..", "outside.md")));

        PersonalityPackValidationResult escapedResult = PersonalityPackValidator.Validate(escapedPack);

        Assert.That(escapedResult.IsValid, Is.False);
        Assert.That(escapedResult.Issues.Any(i => i.Contains("escapes the pack root", StringComparison.Ordinal)), Is.True);

        string blankHashPack = BuildPack(promptBody: "prompt");
        RewriteManifest(blankHashPack, manifest => CopyManifest(
            manifest,
            contentHash: string.Empty));

        PersonalityPackValidationResult blankHashResult = PersonalityPackValidator.Validate(blankHashPack);

        Assert.That(blankHashResult.IsValid, Is.False);
        Assert.That(blankHashResult.Issues.Any(i => i.Contains("ContentHash is required", StringComparison.Ordinal)), Is.True);

        string oversizedPack = Path.Combine(_root, "oversized-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(oversizedPack);
        File.WriteAllText(Path.Combine(oversizedPack, "prompt.md"), "prompt");
        File.WriteAllText(
            Path.Combine(oversizedPack, "pack.json"),
            JsonSerializer.Serialize(new PersonalityPackManifest
            {
                SchemaVersion = 1,
                Id = "oversized-pack",
                DisplayName = "Oversized Pack",
                Tagline = "Too big on purpose.",
                Author = "test",
                Version = "1.0.0",
                Description = new string('x', PersonalityPackValidator.MaxManifestBytes * 2),
                PromptPath = "prompt.md",
                ContentHash = new string('a', 64),
            }, JsonOptions));

        PersonalityPackValidationResult oversizedResult = PersonalityPackValidator.Validate(oversizedPack);

        Assert.That(oversizedResult.IsValid, Is.False);
        Assert.That(oversizedResult.Issues.Any(i => i.Contains("manifest cap", StringComparison.Ordinal)), Is.True);
    }

    // Pass 215 — path-traversal regression tests for every tracked-path field.
    // Pass 39's `Validate_RejectsEscapedPaths_*` already covers PromptPath; the
    // remaining 5 fields (AudioSamples, VoiceHintPath, VoiceRefPath,
    // PortraitPath, LoraAdapterPath) all run through the same
    // `ResolveTrackedPath` + `IsWithinPackRoot` gate but until this pass had
    // no direct regression test. A regression that removed the containment
    // check on, say, LoraAdapterPath would have shipped silently. Each test
    // below builds a valid pack, writes the bait file outside the pack root,
    // points the tracked field at `..\outside.<ext>`, and asserts the
    // `escapes the pack root` issue surfaces.

    [Test]
    public void Validate_AudioSamples_WithEscapedPath_ReportsContainmentFailure()
    {
        string packRoot = BuildPack(promptBody: "prompt", audioSamples: ["clip.wav"]);
        File.WriteAllText(Path.Combine(packRoot, "clip.wav"), "fake wav body");
        File.WriteAllText(Path.Combine(_root, "outside.wav"), "outside wav");
        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            audioSamples: [Path.Combine("..", "outside.wav")]));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Issues.Any(i => i.Contains("escapes the pack root", StringComparison.Ordinal)),
            Is.True,
            "AudioSamples path-containment regression: "
            + string.Join(" | ", result.Issues));
    }

    [Test]
    public void Validate_VoiceHintPath_WithEscapedPath_ReportsContainmentFailure()
    {
        string packRoot = BuildPack(promptBody: "prompt");
        File.WriteAllText(Path.Combine(_root, "outside-hint.txt"), "voice hint");
        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            voiceHintPath: Path.Combine("..", "outside-hint.txt")));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Issues.Any(i => i.Contains("escapes the pack root", StringComparison.Ordinal)),
            Is.True,
            "VoiceHintPath path-containment regression: "
            + string.Join(" | ", result.Issues));
    }

    [Test]
    public void Validate_VoiceRefPath_WithEscapedPath_ReportsContainmentFailure()
    {
        string packRoot = BuildPack(promptBody: "prompt");
        File.WriteAllText(Path.Combine(_root, "outside-ref.wav"), "voice ref");
        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            voiceRefPath: Path.Combine("..", "outside-ref.wav"),
            voiceConsent: "self_recorded"));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Issues.Any(i => i.Contains("escapes the pack root", StringComparison.Ordinal)),
            Is.True,
            "VoiceRefPath path-containment regression: "
            + string.Join(" | ", result.Issues));
    }

    [Test]
    public void Validate_PortraitPath_WithEscapedPath_ReportsContainmentFailure()
    {
        string packRoot = BuildPack(promptBody: "prompt");
        File.WriteAllText(Path.Combine(_root, "outside-portrait.png"), "portrait body");
        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            portraitPath: Path.Combine("..", "outside-portrait.png")));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Issues.Any(i => i.Contains("escapes the pack root", StringComparison.Ordinal)),
            Is.True,
            "PortraitPath path-containment regression: "
            + string.Join(" | ", result.Issues));
    }

    [Test]
    public void Validate_LoraAdapterPath_WithEscapedPath_ReportsContainmentFailure()
    {
        string packRoot = BuildPack(promptBody: "prompt");
        File.WriteAllText(Path.Combine(_root, "outside-adapter.safetensors"), "adapter body");
        RewriteManifest(packRoot, manifest => CopyManifest(
            manifest,
            loraAdapterPath: Path.Combine("..", "outside-adapter.safetensors")));

        PersonalityPackValidationResult result = PersonalityPackValidator.Validate(packRoot);

        Assert.That(result.IsValid, Is.False);
        Assert.That(
            result.Issues.Any(i => i.Contains("escapes the pack root", StringComparison.Ordinal)),
            Is.True,
            "LoraAdapterPath path-containment regression: "
            + string.Join(" | ", result.Issues));
    }

    private string BuildPack(string promptBody, string[]? audioSamples = null)
    {
        string packRoot = Path.Combine(_root, "warm-companion-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(packRoot);

        File.WriteAllText(Path.Combine(packRoot, "prompt.md"), promptBody);

        var manifest = new PersonalityPackManifest
        {
            SchemaVersion = 1,
            Id = "warm-companion",
            DisplayName = "Warm Companion",
            Tagline = "A test pack.",
            Author = "test",
            Version = "1.0.0",
            Description = "Test pack.",
            PromptPath = "prompt.md",
            AudioSamples = audioSamples ?? Array.Empty<string>(),
        };
        // Write manifest with the correct hash so validation passes.
        string manifestPath = Path.Combine(packRoot, "pack.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        // Now recompute hash and rewrite manifest with it.
        string hash = PersonalityPackValidator.ComputeContentHash(packRoot, manifest);
        var finalManifest = new PersonalityPackManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            Tagline = manifest.Tagline,
            Author = manifest.Author,
            Version = manifest.Version,
            Description = manifest.Description,
            PromptPath = manifest.PromptPath,
            AudioSamples = manifest.AudioSamples,
            ContentHash = hash,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(finalManifest, JsonOptions));
        return packRoot;
    }

    private static void RecomputeAndRewriteManifest(string packRoot)
    {
        string manifestPath = Path.Combine(packRoot, "pack.json");
        PersonalityPackManifest manifest = JsonSerializer.Deserialize<PersonalityPackManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions)!;
        string hash = PersonalityPackValidator.ComputeContentHash(packRoot, manifest);
        var rewritten = new PersonalityPackManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            Tagline = manifest.Tagline,
            Author = manifest.Author,
            Version = manifest.Version,
            Description = manifest.Description,
            PromptPath = manifest.PromptPath,
            VoiceHintPath = manifest.VoiceHintPath,
            VoiceRefPath = manifest.VoiceRefPath,
            VoiceConsent = manifest.VoiceConsent,
            VoiceConsentNotes = manifest.VoiceConsentNotes,
            PortraitPath = manifest.PortraitPath,
            LoraAdapterPath = manifest.LoraAdapterPath,
            MemoryNamespace = manifest.MemoryNamespace,
            AudioSamples = manifest.AudioSamples,
            SafetyFlags = manifest.SafetyFlags,
            ContentHash = hash,
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(rewritten, JsonOptions));
    }

    private static void RewriteManifest(string packRoot, Func<PersonalityPackManifest, PersonalityPackManifest> transform)
    {
        string manifestPath = Path.Combine(packRoot, "pack.json");
        PersonalityPackManifest manifest = JsonSerializer.Deserialize<PersonalityPackManifest>(
            File.ReadAllText(manifestPath),
            JsonOptions)!;
        PersonalityPackManifest rewritten = transform(manifest);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(rewritten, JsonOptions));
    }

    private static PersonalityPackManifest CopyManifest(
        PersonalityPackManifest manifest,
        string? promptPath = null,
        string? voiceHintPath = null,
        string? voiceRefPath = null,
        string? voiceConsent = null,
        string? voiceConsentNotes = null,
        string? portraitPath = null,
        string? loraAdapterPath = null,
        string? memoryNamespace = null,
        IReadOnlyList<string>? audioSamples = null,
        IReadOnlyList<string>? safetyFlags = null,
        string? contentHash = null) =>
        new()
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            DisplayName = manifest.DisplayName,
            Tagline = manifest.Tagline,
            Author = manifest.Author,
            Version = manifest.Version,
            Description = manifest.Description,
            PromptPath = promptPath ?? manifest.PromptPath,
            VoiceHintPath = voiceHintPath ?? manifest.VoiceHintPath,
            VoiceRefPath = voiceRefPath ?? manifest.VoiceRefPath,
            VoiceConsent = voiceConsent ?? manifest.VoiceConsent,
            VoiceConsentNotes = voiceConsentNotes ?? manifest.VoiceConsentNotes,
            PortraitPath = portraitPath ?? manifest.PortraitPath,
            LoraAdapterPath = loraAdapterPath ?? manifest.LoraAdapterPath,
            MemoryNamespace = memoryNamespace ?? manifest.MemoryNamespace,
            AudioSamples = audioSamples ?? manifest.AudioSamples,
            SafetyFlags = safetyFlags ?? manifest.SafetyFlags,
            ContentHash = contentHash ?? manifest.ContentHash,
        };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}

// Extension method polyfill because `with` requires a record.
internal static class PersonalityPackManifestCloneExtensions
{
    // Kept as a reminder — PersonalityPackManifest is a class, so callers
    // build a new instance explicitly in the test helpers above.
}
