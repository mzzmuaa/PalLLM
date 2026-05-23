using System.IO;
using NUnit.Framework;
using PalLLM.Domain.Configuration;

namespace PalLLM.Tests;

/// <summary>
/// Regression tests for the <see cref="PalLlmOptions.ModelsDir"/> resolver
/// added in Pass 334. The resolver has two branches:
///
/// <list type="number">
///   <item>When <see cref="PalLlmOptions.ExternalModelsRoot"/> is null /
///         empty / whitespace, the resolved path falls back to the legacy
///         <c>{RuntimeRoot}\Models</c> location.</item>
///   <item>When <see cref="PalLlmOptions.ExternalModelsRoot"/> is a real path,
///         that path wins (trimmed of surrounding whitespace).</item>
/// </list>
///
/// These tests pin both branches so the wiring can't silently regress —
/// without the second branch, the Pass 334 reroute would have no effect
/// at runtime even though the docs claim it does.
/// </summary>
[TestFixture]
public class PalLlmOptionsModelsDirTests
{
    [Test]
    public void ModelsDir_DefaultsToRuntimeRootSubdir_WhenExternalModelsRootIsEmpty()
    {
        PalLlmOptions options = new();
        string expected = Path.Combine(options.RuntimeRoot, "Models");

        Assert.That(options.ExternalModelsRoot, Is.Empty,
            "ExternalModelsRoot must default to empty so fresh installs use the legacy runtime-root layout.");
        Assert.That(options.ModelsDir, Is.EqualTo(expected),
            "Empty ExternalModelsRoot must fall back to {RuntimeRoot}\\Models for back-compat.");
    }

    [Test]
    public void ModelsDir_DefaultsToRuntimeRootSubdir_WhenExternalModelsRootIsNull()
    {
        PalLlmOptions options = new();
        // .NET wouldn't allow null on a string with non-null init, but config
        // binders can produce nulls. Simulate that path defensively.
        options.ExternalModelsRoot = null!;

        string expected = Path.Combine(options.RuntimeRoot, "Models");
        Assert.That(options.ModelsDir, Is.EqualTo(expected),
            "Null ExternalModelsRoot must be treated the same as empty.");
    }

    [Test]
    public void ModelsDir_DefaultsToRuntimeRootSubdir_WhenExternalModelsRootIsWhitespace()
    {
        PalLlmOptions options = new();
        options.ExternalModelsRoot = "   ";

        string expected = Path.Combine(options.RuntimeRoot, "Models");
        Assert.That(options.ModelsDir, Is.EqualTo(expected),
            "Whitespace-only ExternalModelsRoot must be treated as empty so a stray space in config doesn't break the resolver.");
    }

    [Test]
    public void ModelsDir_UsesExternalModelsRoot_WhenSet()
    {
        PalLlmOptions options = new()
        {
            ExternalModelsRoot = @"D:\Models",
        };

        Assert.That(options.ModelsDir, Is.EqualTo(@"D:\Models"),
            "A set ExternalModelsRoot must win over the runtime-root default.");
    }

    [Test]
    public void ModelsDir_TrimsSurroundingWhitespace_FromExternalModelsRoot()
    {
        // Config files sometimes inject leading/trailing whitespace via
        // editor reformatting. The resolver trims defensively.
        PalLlmOptions options = new()
        {
            ExternalModelsRoot = "  D:\\Models  ",
        };

        Assert.That(options.ModelsDir, Is.EqualTo(@"D:\Models"),
            "Surrounding whitespace on a configured path must not change the resolved value.");
    }

    [Test]
    public void ModelsDir_IsIndependentOfRuntimeRoot_WhenExternalModelsRootIsSet()
    {
        // Changing RuntimeFolderName / PalSavedRoot must NOT affect the resolved
        // path when the operator has explicitly chosen an external library.
        PalLlmOptions options = new()
        {
            PalSavedRoot = @"C:\different\saved",
            RuntimeFolderName = "DifferentName",
            ExternalModelsRoot = @"E:\my-curated-models",
        };

        Assert.That(options.ModelsDir, Is.EqualTo(@"E:\my-curated-models"),
            "Operator's curated path must dominate; runtime-root changes are irrelevant once ExternalModelsRoot is set.");
    }

    [Test]
    public void ModelsDir_FallbackTracksRuntimeRoot_WhenExternalIsEmpty()
    {
        // Without ExternalModelsRoot, the resolver must follow RuntimeRoot
        // changes — that's the legacy behaviour the back-compat branch
        // preserves.
        PalLlmOptions options = new()
        {
            PalSavedRoot = @"C:\different\saved",
            RuntimeFolderName = "DifferentName",
        };

        string expected = Path.Combine(@"C:\different\saved", "DifferentName", "Models");
        Assert.That(options.ModelsDir, Is.EqualTo(expected),
            "Legacy fallback must continue to derive from RuntimeRoot when no external override is configured.");
    }

    // ---- DiffusionModelsDir (Pass 340) -----------------------------------

    [Test]
    public void DiffusionModelsDir_DefaultsToModelsDirSubdir_WhenExternalModelsRootIsEmpty()
    {
        // Pass 340: diffusion weights live alongside the chat models. With
        // no operator override, that's the legacy runtime-root/Models/Diffusion
        // path so back-compat is preserved for fresh installs.
        PalLlmOptions options = new();
        string expected = Path.Combine(options.RuntimeRoot, "Models", "Diffusion");

        Assert.That(options.DiffusionModelsDir, Is.EqualTo(expected),
            "Empty ExternalModelsRoot must place DiffusionModelsDir under the legacy runtime-root/Models/Diffusion path.");
    }

    [Test]
    public void DiffusionModelsDir_TracksExternalModelsRoot_WhenSet()
    {
        // Pass 340: setting ExternalModelsRoot must move the diffusion path
        // too — operators get one knob to relocate every model class, not
        // a separate DiffusionRoot config that can silently drift.
        PalLlmOptions options = new()
        {
            ExternalModelsRoot = @"D:\Models",
        };

        Assert.That(options.DiffusionModelsDir, Is.EqualTo(@"D:\Models\Diffusion"),
            "DiffusionModelsDir must be a Diffusion subdirectory under the resolved ModelsDir, not a separate root.");
    }

    [Test]
    public void DiffusionModelsDir_HonorsWhitespaceTrimming_OnExternalModelsRoot()
    {
        PalLlmOptions options = new()
        {
            ExternalModelsRoot = "  D:\\Models  ",
        };

        Assert.That(options.DiffusionModelsDir, Is.EqualTo(@"D:\Models\Diffusion"),
            "Whitespace trimming on the chat-model root must propagate to the diffusion path.");
    }

    [Test]
    public void DiffusionModelsDir_IsAlwaysSiblingOf_ModelsDir()
    {
        // Pass 340 invariant: regardless of where ModelsDir lands,
        // DiffusionModelsDir must always be its `Diffusion` child. No
        // operator config can break this relationship.
        PalLlmOptions options1 = new();
        Assert.That(
            options1.DiffusionModelsDir,
            Is.EqualTo(Path.Combine(options1.ModelsDir, "Diffusion")));

        PalLlmOptions options2 = new()
        {
            ExternalModelsRoot = @"E:\custom-curation",
        };
        Assert.That(
            options2.DiffusionModelsDir,
            Is.EqualTo(Path.Combine(options2.ModelsDir, "Diffusion")));

        PalLlmOptions options3 = new()
        {
            PalSavedRoot = @"C:\different\saved",
            RuntimeFolderName = "DifferentName",
        };
        Assert.That(
            options3.DiffusionModelsDir,
            Is.EqualTo(Path.Combine(options3.ModelsDir, "Diffusion")));
    }
}
