using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PalLLM.Domain;
using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Personality-pack v1 format - manifest record + deterministic
//            validator + canonical content-hash algorithm. Self-contained
//            local-first format: no signature infrastructure, integrity is
//            the SHA-256 of (sorted relative paths, file bytes, sentinel
//            separators). Loaders refuse to load a pack whose declared and
//            actual hashes diverge.
//   surface: PersonalityPackManifest (record), PersonalityPackValidator.Validate,
//            PersonalityPackValidator.ComputeContentHash.
//   gate:    None directly. Sample packs under samples/packs/ are validated
//            via Drift_Path_references.
//   adr:     None.
//   docs:    docs/PACK_AUTHORING.md (deep-dive), docs/PACK_SAMPLES.md (the
//            four reference packs and how to load them),
//            docs/schemas/personality-pack.schema.json (JSON Schema 2020-12).
//   notes:   The PowerShell helper scripts/compute-pack-hash.ps1 mirrors
//            ComputeContentHash exactly; if you change the algorithm here,
//            update the helper in the same commit.
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Packs;

/// <summary>
/// Pass 39 / C6 — personality-pack v1 format.
///
/// <para>A personality pack is a self-contained directory describing
/// a companion persona that PalLLM can load at runtime. The format
/// is deliberately simple and local-first:</para>
///
/// <code>
/// {pack-root}/
///   pack.json              ← manifest (this record, version 1)
///   prompt.md              ← system-prompt fragment (required)
///   voice-hint.md          ← optional TTS voice hint
///   audio/                 ← optional sample clips (ogg, opus)
///   portrait.png           ← optional dashboard portrait
/// </code>
///
/// <para>The validator enforces: schema version, required files,
/// bounded prompt length, allowlisted file extensions under
/// <c>audio/</c>, and the <see cref="PersonalityPackManifest.ContentHash"/>
/// invariant — loaders reject a pack whose on-disk content hash
/// doesn't match the manifest. Pure-local: no signature verification,
/// no download path. Packs are hand-copied into PalLLM's runtime
/// packs directory (by default <c>%LOCALAPPDATA%\Pal\Saved\PalLLM\Packs\</c>)
/// or any other local directory an operator stages before validation.</para>
///
/// <para>The manifest itself carries the <i>declared</i> content
/// hash; the validator computes the actual hash and compares. This
/// lets pack authors ship a manifest + content and have PalLLM
/// refuse to load a tampered pack without a key infrastructure.</para>
/// </summary>
public sealed class PersonalityPackManifest
{
    /// <summary>Schema version, always 1 for now.</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>Stable kebab-case id — becomes the directory name.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Short one-liner for the dashboard picker.</summary>
    public string Tagline { get; init; } = string.Empty;

    /// <summary>Pack author / team name.</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>Semver-style version string (e.g. "1.0.0").</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Long-form description shown on the pack detail page.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Relative path to the system-prompt fragment file.</summary>
    public string PromptPath { get; init; } = "prompt.md";

    /// <summary>Optional relative path to the voice-hint file.</summary>
    public string? VoiceHintPath { get; init; }

    /// <summary>Optional relative path to a local voice reference used by opt-in TTS or voice-clone adapters.</summary>
    public string? VoiceRefPath { get; init; }

    /// <summary>Required consent/provenance category when <see cref="VoiceRefPath"/> is present.</summary>
    public string? VoiceConsent { get; init; }

    /// <summary>Optional short provenance note for the voice reference.</summary>
    public string? VoiceConsentNotes { get; init; }

    /// <summary>Optional relative path to the portrait image.</summary>
    public string? PortraitPath { get; init; }

    /// <summary>Optional relative path to a local LoRA/personality adapter file staged with the pack.</summary>
    public string? LoraAdapterPath { get; init; }

    /// <summary>Optional kebab-case namespace for pack-specific long-term memory or adapter cache identity.</summary>
    public string? MemoryNamespace { get; init; }

    /// <summary>Optional relative paths to sample audio clips (ogg/opus only).</summary>
    public IReadOnlyList<string> AudioSamples { get; init; } = Array.Empty<string>();

    /// <summary>Safety flags the pack declares it touches ("mature-themes" / "combat-heavy" / "family-friendly" / etc.).</summary>
    public IReadOnlyList<string> SafetyFlags { get; init; } = Array.Empty<string>();

    /// <summary>Lower-case hex SHA-256 of the canonical content blob (sorted paths + file bytes).</summary>
    public string ContentHash { get; init; } = string.Empty;
}

/// <summary>
/// Deterministic validator for Pass-39 personality packs. Scans a
/// pack root directory, loads + validates the manifest, recomputes
/// the content hash, and returns a <see cref="PersonalityPackValidationResult"/>
/// with per-check status. Pure function over filesystem + text; no
/// inference call, no network, no mutation.
/// </summary>
public static class PersonalityPackValidator
{
    /// <summary>Hard cap on the pack manifest size (bytes) so local validation never buffers an arbitrarily large pack.json.</summary>
    public const int MaxManifestBytes = 64 * 1024;

    /// <summary>Hard cap on the prompt fragment length (bytes) — keeps PalLLM prompt budgets predictable.</summary>
    public const int MaxPromptBytes = 8_192;

    /// <summary>Hard cap for optional sample clips shipped inside a pack.</summary>
    public const int MaxAudioSampleBytes = 10 * 1024 * 1024;

    /// <summary>Hard cap for optional voice-reference clips shipped inside a pack.</summary>
    public const int MaxVoiceReferenceBytes = 10 * 1024 * 1024;

    /// <summary>Audio samples must be one of these extensions.</summary>
    public static readonly string[] AllowedAudioExtensions = [".ogg", ".opus"];

    /// <summary>Voice references must use local, decodable audio container formats.</summary>
    public static readonly string[] AllowedVoiceReferenceExtensions = [".wav", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac"];

    /// <summary>Voice-reference consent/provenance categories accepted by shareable packs.</summary>
    public static readonly string[] AllowedVoiceConsentValues = ["self_recorded", "licensed", "synthetic", "public_domain"];

    /// <summary>Personality adapters must use the safe tensor container expected by local LoRA runtimes.</summary>
    public static readonly string[] AllowedLoraAdapterExtensions = [".safetensors"];

    /// <summary>
    /// Validate the pack at <paramref name="packRoot"/>. The directory
    /// must contain a <c>pack.json</c>; all other files are loaded on
    /// demand. Returns a structured result even when validation fails.
    /// </summary>
    public static PersonalityPackValidationResult Validate(string packRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);

        var issues = new List<string>();
        var checks = new List<PersonalityPackCheck>();

        if (!Directory.Exists(packRoot))
        {
            issues.Add($"Pack root does not exist: {packRoot}");
            return new PersonalityPackValidationResult(
                PackRoot: packRoot,
                Manifest: null,
                IsValid: false,
                Checks: checks,
                Issues: issues,
                ActualContentHash: null);
        }

        string manifestPath = Path.Combine(packRoot, "pack.json");
        checks.Add(Check("manifest-present", File.Exists(manifestPath), manifestPath));
        if (!File.Exists(manifestPath))
        {
            issues.Add("Missing pack.json at the root of the pack directory.");
            return new PersonalityPackValidationResult(
                PackRoot: packRoot,
                Manifest: null,
                IsValid: false,
                Checks: checks,
                Issues: issues,
                ActualContentHash: null);
        }

        BoundedJsonFileReader.JsonReadResult<PersonalityPackManifest> manifestRead = BoundedJsonFileReader.TryRead(
            manifestPath,
            MaxManifestBytes,
            stream => JsonSerializer.Deserialize(stream, ManifestJsonContext.PersonalityPackManifest));
        checks.Add(Check(
            "manifest-within-budget",
            manifestRead.FailureCode is not BoundedJsonFileReader.JsonReadFailureCode.Oversized,
            $"{MaxManifestBytes} bytes"));
        if (manifestRead.FailureCode is BoundedJsonFileReader.JsonReadFailureCode.Oversized)
        {
            issues.Add($"pack.json exceeds the {MaxManifestBytes}-byte manifest cap.");
            return new PersonalityPackValidationResult(
                PackRoot: packRoot,
                Manifest: null,
                IsValid: false,
                Checks: checks,
                Issues: issues,
                ActualContentHash: null);
        }
        if (manifestRead.FailureCode is BoundedJsonFileReader.JsonReadFailureCode.MalformedJson)
        {
            issues.Add("pack.json is not valid JSON.");
            return new PersonalityPackValidationResult(
                PackRoot: packRoot,
                Manifest: null,
                IsValid: false,
                Checks: checks,
                Issues: issues,
                ActualContentHash: null);
        }
        if (manifestRead.FailureCode is BoundedJsonFileReader.JsonReadFailureCode.Unreadable)
        {
            issues.Add("pack.json could not be read.");
            return new PersonalityPackValidationResult(
                PackRoot: packRoot,
                Manifest: null,
                IsValid: false,
                Checks: checks,
                Issues: issues,
                ActualContentHash: null);
        }

        PersonalityPackManifest? manifest = manifestRead.Value;
        if (manifest is null)
        {
            issues.Add("pack.json deserialised to null.");
            return new PersonalityPackValidationResult(
                PackRoot: packRoot,
                Manifest: null,
                IsValid: false,
                Checks: checks,
                Issues: issues,
                ActualContentHash: null);
        }

        string manifestId = manifest.Id ?? string.Empty;
        string displayName = manifest.DisplayName ?? string.Empty;
        string tagline = manifest.Tagline ?? string.Empty;
        string author = manifest.Author ?? string.Empty;
        string version = manifest.Version ?? string.Empty;
        string description = manifest.Description ?? string.Empty;
        string promptRelativePath = manifest.PromptPath ?? string.Empty;
        string contentHash = manifest.ContentHash ?? string.Empty;
        string voiceRefPath = manifest.VoiceRefPath ?? string.Empty;
        string voiceConsent = manifest.VoiceConsent ?? string.Empty;
        string voiceConsentNotes = manifest.VoiceConsentNotes ?? string.Empty;
        string loraAdapterPath = manifest.LoraAdapterPath ?? string.Empty;
        string memoryNamespace = manifest.MemoryNamespace ?? string.Empty;
        IReadOnlyList<string> audioSamples = manifest.AudioSamples ?? Array.Empty<string>();
        IReadOnlyList<string> safetyFlags = manifest.SafetyFlags ?? Array.Empty<string>();
        var publicationFindings = new List<PackPublicationSafetyFinding>(
            PackPublicationSafetyValidator.CollectPersonalityManifestFindings(manifest));

        checks.Add(Check("schema-version-1", manifest.SchemaVersion == 1, manifest.SchemaVersion.ToString()));
        if (manifest.SchemaVersion != 1)
        {
            issues.Add($"Unsupported schema version {manifest.SchemaVersion}; loader only understands v1.");
        }

        checks.Add(Check("id-present", !string.IsNullOrWhiteSpace(manifestId), manifestId));
        if (string.IsNullOrWhiteSpace(manifestId)) { issues.Add("Manifest Id is required."); }
        checks.Add(Check("id-shape", string.IsNullOrWhiteSpace(manifestId) || IdRegex.IsMatch(manifestId), manifestId));
        if (!string.IsNullOrWhiteSpace(manifestId) && !IdRegex.IsMatch(manifestId))
        {
            issues.Add("Manifest Id must be kebab-case and 2-64 characters.");
        }

        checks.Add(Check("display-name-present", !string.IsNullOrWhiteSpace(displayName), displayName));
        if (string.IsNullOrWhiteSpace(displayName)) { issues.Add("DisplayName is required."); }
        checks.Add(Check("display-name-length", displayName.Length <= 80, displayName.Length.ToString()));
        if (displayName.Length > 80) { issues.Add("DisplayName exceeds the 80-character limit."); }

        checks.Add(Check("author-present", !string.IsNullOrWhiteSpace(author), author));
        if (string.IsNullOrWhiteSpace(author)) { issues.Add("Author is required."); }
        checks.Add(Check("author-length", author.Length <= 80, author.Length.ToString()));
        if (author.Length > 80) { issues.Add("Author exceeds the 80-character limit."); }

        checks.Add(Check("version-semver", string.IsNullOrWhiteSpace(version) || VersionRegex.IsMatch(version), version));
        if (!string.IsNullOrWhiteSpace(version) && !VersionRegex.IsMatch(version))
        {
            issues.Add("Version must use semver-style formatting such as 1.0.0.");
        }

        checks.Add(Check("tagline-length", tagline.Length <= 160, tagline.Length.ToString()));
        if (tagline.Length > 160) { issues.Add("Tagline exceeds the 160-character limit."); }

        checks.Add(Check("description-length", description.Length <= 4_096, description.Length.ToString()));
        if (description.Length > 4_096) { issues.Add("Description exceeds the 4096-character limit."); }

        checks.Add(Check("voice-consent-notes-length", voiceConsentNotes.Length <= 1_024, voiceConsentNotes.Length.ToString()));
        if (voiceConsentNotes.Length > 1_024)
        {
            issues.Add("VoiceConsentNotes exceeds the 1024-character limit.");
        }

        checks.Add(Check("audio-samples-unique", audioSamples.Distinct(StringComparer.Ordinal).Count() == audioSamples.Count, audioSamples.Count.ToString()));
        if (audioSamples.Distinct(StringComparer.Ordinal).Count() != audioSamples.Count)
        {
            issues.Add("AudioSamples must not contain duplicate entries.");
        }

        checks.Add(Check("safety-flags-unique", safetyFlags.Distinct(StringComparer.Ordinal).Count() == safetyFlags.Count, safetyFlags.Count.ToString()));
        if (safetyFlags.Distinct(StringComparer.Ordinal).Count() != safetyFlags.Count)
        {
            issues.Add("SafetyFlags must not contain duplicate entries.");
        }

        checks.Add(Check("content-hash-present", !string.IsNullOrWhiteSpace(contentHash), contentHash));
        if (string.IsNullOrWhiteSpace(contentHash))
        {
            issues.Add("ContentHash is required.");
        }

        checks.Add(Check("content-hash-shape", string.IsNullOrWhiteSpace(contentHash) || ContentHashRegex.IsMatch(contentHash), contentHash));
        if (!string.IsNullOrWhiteSpace(contentHash) && !ContentHashRegex.IsMatch(contentHash))
        {
            issues.Add("ContentHash must be 64 lowercase hex characters.");
        }

        ResolvedPackPath promptPath = ResolveTrackedPath(packRoot, promptRelativePath);
        checks.Add(Check("prompt-path-contained", promptPath.IsWithinPackRoot, promptRelativePath));
        if (!promptPath.IsWithinPackRoot)
        {
            issues.Add($"PromptPath escapes the pack root: {promptRelativePath}");
        }

        bool promptExists = promptPath.IsWithinPackRoot && File.Exists(promptPath.AbsolutePath);
        checks.Add(Check("prompt-present", promptExists, promptPath.IsWithinPackRoot ? promptPath.AbsolutePath : promptRelativePath));
        if (promptPath.IsWithinPackRoot && !promptExists)
        {
            issues.Add($"Required prompt fragment missing: {promptRelativePath}");
        }
        else
        {
            long promptBytes = new FileInfo(promptPath.AbsolutePath).Length;
            checks.Add(Check("prompt-within-budget", promptBytes <= MaxPromptBytes, $"{promptBytes} bytes"));
            if (promptBytes > MaxPromptBytes)
            {
                issues.Add($"Prompt fragment exceeds {MaxPromptBytes} bytes ({promptBytes}) — tighten to fit the runtime prompt budget.");
            }
            else
            {
                BoundedTextFileReader.TextReadResult promptRead = BoundedTextFileReader.TryRead(
                    promptPath.AbsolutePath,
                    MaxPromptBytes);
                if (promptRead.Succeeded && promptRead.Text is not null)
                {
                    publicationFindings.AddRange(
                        PackPublicationSafetyValidator.CollectPersonalityPromptFindings(promptRead.Text));
                }
            }
        }

        foreach (string audio in audioSamples)
        {
            string ext = Path.GetExtension(audio).ToLowerInvariant();
            bool extOk = AllowedAudioExtensions.Contains(ext);
            checks.Add(Check("audio-extension-allowed-" + audio, extOk, ext));
            if (!extOk)
            {
                issues.Add($"Audio sample {audio} uses disallowed extension '{ext}'. Allowed: {string.Join(", ", AllowedAudioExtensions)}.");
            }

            ResolvedPackPath audioPath = ResolveTrackedPath(packRoot, audio);
            checks.Add(Check("audio-path-contained-" + audio, audioPath.IsWithinPackRoot, audio));
            if (!audioPath.IsWithinPackRoot)
            {
                issues.Add($"Audio sample path escapes the pack root: {audio}");
            }

            bool audioExists = audioPath.IsWithinPackRoot && File.Exists(audioPath.AbsolutePath);
            checks.Add(Check("audio-file-present-" + audio, audioExists, audioPath.IsWithinPackRoot ? audioPath.AbsolutePath : audio));
            if (audioPath.IsWithinPackRoot && !audioExists)
            {
                issues.Add($"Audio sample missing: {audio}");
            }
            else if (audioExists)
            {
                long audioBytes = new FileInfo(audioPath.AbsolutePath).Length;
                bool withinBudget = audioBytes <= MaxAudioSampleBytes;
                checks.Add(Check("audio-within-budget-" + audio, withinBudget, $"{audioBytes} bytes"));
                if (!withinBudget)
                {
                    issues.Add($"Audio sample {audio} exceeds the {MaxAudioSampleBytes}-byte cap ({audioBytes} bytes).");
                }
            }
        }

        if (manifest.VoiceHintPath is string hint && hint.Length > 0)
        {
            ResolvedPackPath voiceHintPath = ResolveTrackedPath(packRoot, hint);
            checks.Add(Check("voice-hint-path-contained", voiceHintPath.IsWithinPackRoot, hint));
            if (!voiceHintPath.IsWithinPackRoot)
            {
                issues.Add($"VoiceHintPath escapes the pack root: {hint}");
            }

            bool voiceHintExists = voiceHintPath.IsWithinPackRoot && File.Exists(voiceHintPath.AbsolutePath);
            checks.Add(Check("voice-hint-file-present", voiceHintExists, voiceHintPath.IsWithinPackRoot ? voiceHintPath.AbsolutePath : hint));
            if (voiceHintPath.IsWithinPackRoot && !voiceHintExists) { issues.Add($"Voice-hint file missing: {hint}"); }
        }

        if (voiceRefPath.Length > 0)
        {
            bool voiceConsentOk = AllowedVoiceConsentValues.Contains(voiceConsent, StringComparer.OrdinalIgnoreCase);
            checks.Add(Check("voice-ref-consent-declared", voiceConsentOk, voiceConsent));
            if (!voiceConsentOk)
            {
                issues.Add("VoiceConsent is required when VoiceRefPath is set. Allowed: " + string.Join(", ", AllowedVoiceConsentValues) + ".");
            }

            bool isLocalReference = !LooksLikeRemoteReference(voiceRefPath);
            checks.Add(Check("voice-ref-local", isLocalReference, voiceRefPath));
            if (!isLocalReference)
            {
                issues.Add($"VoiceRefPath must be a pack-relative local file, not a URL: {voiceRefPath}");
            }

            string ext = Path.GetExtension(voiceRefPath).ToLowerInvariant();
            bool extOk = AllowedVoiceReferenceExtensions.Contains(ext);
            checks.Add(Check("voice-ref-extension-allowed", extOk, ext));
            if (!extOk)
            {
                issues.Add($"VoiceRefPath {voiceRefPath} uses disallowed extension '{ext}'. Allowed: {string.Join(", ", AllowedVoiceReferenceExtensions)}.");
            }

            if (isLocalReference)
            {
                ResolvedPackPath resolvedVoiceRef = ResolveTrackedPath(packRoot, voiceRefPath);
                checks.Add(Check("voice-ref-path-contained", resolvedVoiceRef.IsWithinPackRoot, voiceRefPath));
                if (!resolvedVoiceRef.IsWithinPackRoot)
                {
                    issues.Add($"VoiceRefPath escapes the pack root: {voiceRefPath}");
                }

                bool voiceRefExists = resolvedVoiceRef.IsWithinPackRoot && File.Exists(resolvedVoiceRef.AbsolutePath);
                checks.Add(Check("voice-ref-file-present", voiceRefExists, resolvedVoiceRef.IsWithinPackRoot ? resolvedVoiceRef.AbsolutePath : voiceRefPath));
                if (resolvedVoiceRef.IsWithinPackRoot && !voiceRefExists)
                {
                    issues.Add($"Voice reference file missing: {voiceRefPath}");
                }
                else if (voiceRefExists)
                {
                    long voiceRefBytes = new FileInfo(resolvedVoiceRef.AbsolutePath).Length;
                    bool withinBudget = voiceRefBytes <= MaxVoiceReferenceBytes;
                    checks.Add(Check("voice-ref-within-budget", withinBudget, $"{voiceRefBytes} bytes"));
                    if (!withinBudget)
                    {
                        issues.Add($"VoiceRefPath {voiceRefPath} exceeds the {MaxVoiceReferenceBytes}-byte cap ({voiceRefBytes} bytes).");
                    }
                }
            }
        }

        if (manifest.PortraitPath is string portrait && portrait.Length > 0)
        {
            ResolvedPackPath portraitPath = ResolveTrackedPath(packRoot, portrait);
            checks.Add(Check("portrait-path-contained", portraitPath.IsWithinPackRoot, portrait));
            if (!portraitPath.IsWithinPackRoot)
            {
                issues.Add($"PortraitPath escapes the pack root: {portrait}");
            }

            bool portraitExists = portraitPath.IsWithinPackRoot && File.Exists(portraitPath.AbsolutePath);
            checks.Add(Check("portrait-file-present", portraitExists, portraitPath.IsWithinPackRoot ? portraitPath.AbsolutePath : portrait));
            if (portraitPath.IsWithinPackRoot && !portraitExists) { issues.Add($"Portrait missing: {portrait}"); }
        }

        if (loraAdapterPath.Length > 0)
        {
            bool isLocalAdapter = !LooksLikeRemoteReference(loraAdapterPath);
            checks.Add(Check("lora-adapter-local", isLocalAdapter, loraAdapterPath));
            if (!isLocalAdapter)
            {
                issues.Add($"LoraAdapterPath must be a pack-relative local file, not a URL: {loraAdapterPath}");
            }

            string ext = Path.GetExtension(loraAdapterPath).ToLowerInvariant();
            bool extOk = AllowedLoraAdapterExtensions.Contains(ext);
            checks.Add(Check("lora-adapter-extension-allowed", extOk, ext));
            if (!extOk)
            {
                issues.Add($"LoraAdapterPath {loraAdapterPath} uses disallowed extension '{ext}'. Allowed: {string.Join(", ", AllowedLoraAdapterExtensions)}.");
            }

            if (isLocalAdapter)
            {
                ResolvedPackPath resolvedAdapter = ResolveTrackedPath(packRoot, loraAdapterPath);
                checks.Add(Check("lora-adapter-path-contained", resolvedAdapter.IsWithinPackRoot, loraAdapterPath));
                if (!resolvedAdapter.IsWithinPackRoot)
                {
                    issues.Add($"LoraAdapterPath escapes the pack root: {loraAdapterPath}");
                }

                bool adapterExists = resolvedAdapter.IsWithinPackRoot && File.Exists(resolvedAdapter.AbsolutePath);
                checks.Add(Check("lora-adapter-file-present", adapterExists, resolvedAdapter.IsWithinPackRoot ? resolvedAdapter.AbsolutePath : loraAdapterPath));
                if (resolvedAdapter.IsWithinPackRoot && !adapterExists)
                {
                    issues.Add($"LoRA adapter file missing: {loraAdapterPath}");
                }
            }
        }

        if (memoryNamespace.Length > 0)
        {
            bool memoryNamespaceShape = IdRegex.IsMatch(memoryNamespace);
            checks.Add(Check("memory-namespace-shape", memoryNamespaceShape, memoryNamespace));
            if (!memoryNamespaceShape)
            {
                issues.Add("MemoryNamespace must be kebab-case and 2-64 characters.");
            }
        }

        string? actualHash = null;
        if (TryComputeContentHash(packRoot, manifest, out string computedHash, out string? contentHashFailure))
        {
            actualHash = computedHash;
        }
        else if (!string.IsNullOrWhiteSpace(contentHashFailure))
        {
            issues.Add(contentHashFailure);
        }

        bool hashMatches = !string.IsNullOrWhiteSpace(contentHash)
            && actualHash is not null
            && string.Equals(contentHash, actualHash, StringComparison.OrdinalIgnoreCase);
        checks.Add(Check("content-hash-matches", hashMatches, actualHash ?? "unavailable"));
        if (!string.IsNullOrWhiteSpace(contentHash) && !hashMatches)
        {
            if (actualHash is not null)
            {
                issues.Add($"ContentHash mismatch — declared {contentHash}, computed {actualHash}. Pack may have been tampered with.");
            }
        }

        checks.Add(Check("publication-safety", publicationFindings.Count == 0, $"{publicationFindings.Count} finding(s)"));
        foreach (PackPublicationSafetyFinding finding in publicationFindings)
        {
            issues.Add($"{finding.Path}: {finding.Message}");
        }

        bool isValid = issues.Count == 0;
        return new PersonalityPackValidationResult(
            PackRoot: packRoot,
            Manifest: manifest,
            IsValid: isValid,
            Checks: checks,
            Issues: issues,
            ActualContentHash: actualHash);
    }

    /// <summary>
    /// Deterministic content hash over every tracked file in the
    /// pack. Input format:
    /// <c>SHA256(for each sorted relative path: path || 0x00 || file-bytes || 0xFF)</c>.
    /// Excludes the manifest itself so hash can be embedded without
    /// creating a bootstrap cycle.
    /// </summary>
    public static string ComputeContentHash(string packRoot, PersonalityPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (!TryComputeContentHash(packRoot, manifest, out string hash, out string? failure))
        {
            throw new InvalidOperationException(failure ?? "Unable to compute personality-pack content hash.");
        }

        return hash;
    }

    private static bool TryComputeContentHash(
        string packRoot,
        PersonalityPackManifest manifest,
        out string hash,
        out string? failure)
    {
        string promptRelativePath = manifest.PromptPath ?? string.Empty;
        IReadOnlyList<string> audioSamples = manifest.AudioSamples ?? Array.Empty<string>();
        var paths = new List<string>
        {
            promptRelativePath,
        };
        if (!string.IsNullOrWhiteSpace(manifest.VoiceHintPath)) { paths.Add(manifest.VoiceHintPath!); }
        if (!string.IsNullOrWhiteSpace(manifest.VoiceRefPath)) { paths.Add(manifest.VoiceRefPath!); }
        if (!string.IsNullOrWhiteSpace(manifest.PortraitPath)) { paths.Add(manifest.PortraitPath!); }
        if (!string.IsNullOrWhiteSpace(manifest.LoraAdapterPath)) { paths.Add(manifest.LoraAdapterPath!); }
        foreach (string a in audioSamples) { paths.Add(a); }

        paths.Sort(StringComparer.Ordinal);

        using var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);
        try
        {
            foreach (string rel in paths)
            {
                if (string.IsNullOrWhiteSpace(rel))
                {
                    continue;
                }

                ResolvedPackPath resolvedPath = ResolveTrackedPath(packRoot, rel);
                if (!resolvedPath.IsWithinPackRoot)
                {
                    hash = string.Empty;
                    failure = $"Tracked file path escapes the pack root: {rel}";
                    return false;
                }

                if (!File.Exists(resolvedPath.AbsolutePath))
                {
                    continue;
                }

                incrementalHash.AppendData(Encoding.UTF8.GetBytes(rel));
                incrementalHash.AppendData(PathSeparatorSentinel);

                try
                {
                    using FileStream stream = OpenRead(resolvedPath.AbsolutePath);
                    while (true)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read == 0)
                        {
                            break;
                        }

                        incrementalHash.AppendData(buffer, 0, read);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    hash = string.Empty;
                    failure = $"Tracked file could not be read for hashing: {rel}";
                    return false;
                }

                incrementalHash.AppendData(FileSeparatorSentinel);
            }

            hash = Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();
            failure = null;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static PersonalityPackCheck Check(string id, bool pass, string detail)
        => new(id, pass ? "pass" : "fail", detail);

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly PalLlmDomainJsonSerializerContext ManifestJsonContext = new(ManifestOptions);

    private static readonly Regex IdRegex = new(
        "^[a-z0-9][a-z0-9-]*[a-z0-9]$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionRegex = new(
        "^\\d+\\.\\d+\\.\\d+(?:[-+][A-Za-z0-9.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ContentHashRegex = new(
        "^[0-9a-f]{64}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RemoteReferenceRegex = new(
        "^[A-Za-z][A-Za-z0-9+.-]*://",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly byte[] PathSeparatorSentinel = [0x00];

    private static readonly byte[] FileSeparatorSentinel = [0xFF];

    private const int HashBufferSize = 16 * 1024;

    private static ResolvedPackPath ResolveTrackedPath(string packRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return new ResolvedPackPath(relativePath, string.Empty, IsWithinPackRoot: false);
        }

        string fullPackRoot = Path.GetFullPath(packRoot);
        string absolutePath = Path.GetFullPath(Path.Combine(fullPackRoot, relativePath));
        bool isWithinPackRoot = IsPathWithinRoot(fullPackRoot, absolutePath);
        return new ResolvedPackPath(relativePath, absolutePath, isWithinPackRoot);
    }

    private static bool IsPathWithinRoot(string root, string candidate)
    {
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string normalizedRoot = AppendDirectorySeparator(root);
        return candidate.StartsWith(normalizedRoot, comparison)
            || string.Equals(candidate, root, comparison);
    }

    private static string AppendDirectorySeparator(string value) =>
        Path.EndsInDirectorySeparator(value)
            ? value
            : value + Path.DirectorySeparatorChar;

    private static bool LooksLikeRemoteReference(string relativePath) =>
        RemoteReferenceRegex.IsMatch(relativePath);

    private static FileStream OpenRead(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.SequentialScan,
        });

    private readonly record struct ResolvedPackPath(
        string RelativePath,
        string AbsolutePath,
        bool IsWithinPackRoot);
}

/// <summary>
/// One check result inside a <see cref="PersonalityPackValidationResult"/>.
/// </summary>
public sealed record PersonalityPackCheck(
    string Id,
    string Status,
    string Detail);

/// <summary>
/// Outcome of <see cref="PersonalityPackValidator.Validate"/>.
/// </summary>
public sealed record PersonalityPackValidationResult(
    string PackRoot,
    PersonalityPackManifest? Manifest,
    bool IsValid,
    IReadOnlyList<PersonalityPackCheck> Checks,
    IReadOnlyList<string> Issues,
    string? ActualContentHash);
