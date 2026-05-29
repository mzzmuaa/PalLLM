using NUnit.Framework;
using PalLLM.Domain.Packs;

namespace PalLLM.Tests;

/// <summary>
/// Direct unit tests for <see cref="SpeciesPersonalityResolver.Resolve"/>. Pure
/// function so every branch is testable without fixture setup. The three lanes
/// (species default / caller fallback / none) plus all the input-sanitisation
/// edge cases (null, whitespace, mixed case, blank values inside the dictionary)
/// pin the behaviour the per-species personality feature relies on.
/// </summary>
[TestFixture]
public class SpeciesPersonalityResolverTests
{
    [Test]
    public void Resolve_NullSpecies_NullMap_NoFallback_ReturnsNoneWithEmptySpecies()
    {
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(null, null);
        Assert.Multiple(() =>
        {
            Assert.That(result.PackId, Is.Null);
            Assert.That(result.Source, Is.EqualTo(ResolutionSource.None));
            Assert.That(result.Species, Is.EqualTo(string.Empty));
        });
    }

    [Test]
    public void Resolve_EmptySpecies_EmptyMap_NoFallback_ReturnsNone()
    {
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            string.Empty,
            new Dictionary<string, string>());
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.None));
        Assert.That(result.PackId, Is.Null);
    }

    [Test]
    public void Resolve_SpeciesMatchesMap_ReturnsMappedPackIdFromSpeciesDefault()
    {
        Dictionary<string, string> map = new()
        {
            ["SpeciesAlpha"] = "species-alpha-timid",
            ["CampGuardian"] = "camp-guardian-aloof",
        };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve("SpeciesAlpha", map);
        Assert.Multiple(() =>
        {
            Assert.That(result.PackId, Is.EqualTo("species-alpha-timid"));
            Assert.That(result.Source, Is.EqualTo(ResolutionSource.SpeciesDefault));
            Assert.That(result.Species, Is.EqualTo("SpeciesAlpha"));
        });
    }

    [Test]
    public void Resolve_SpeciesMatchesMap_CaseInsensitive()
    {
        Dictionary<string, string> map = new() { ["SpeciesAlpha"] = "species-alpha-timid" };
        SpeciesPersonalityResolution lower = SpeciesPersonalityResolver.Resolve("speciesalpha", map);
        SpeciesPersonalityResolution upper = SpeciesPersonalityResolver.Resolve("SPECIESALPHA", map);
        SpeciesPersonalityResolution mixed = SpeciesPersonalityResolver.Resolve("SpeciesAlpha", map);
        Assert.Multiple(() =>
        {
            Assert.That(lower.PackId, Is.EqualTo("species-alpha-timid"));
            Assert.That(upper.PackId, Is.EqualTo("species-alpha-timid"));
            Assert.That(mixed.PackId, Is.EqualTo("species-alpha-timid"));
            Assert.That(lower.Source, Is.EqualTo(ResolutionSource.SpeciesDefault));
        });
    }

    [Test]
    public void Resolve_SpeciesMatchesMap_TrimmedSpeciesInput()
    {
        Dictionary<string, string> map = new() { ["SpeciesAlpha"] = "species-alpha-timid" };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve("  SpeciesAlpha  ", map);
        Assert.That(result.PackId, Is.EqualTo("species-alpha-timid"));
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.SpeciesDefault));
        // Species echoed back trimmed, not raw.
        Assert.That(result.Species, Is.EqualTo("SpeciesAlpha"));
    }

    [Test]
    public void Resolve_SpeciesMatchesMap_TrimmedMapKey()
    {
        Dictionary<string, string> map = new() { ["  SpeciesAlpha  "] = "species-alpha-timid" };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve("SpeciesAlpha", map);
        Assert.That(result.PackId, Is.EqualTo("species-alpha-timid"));
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.SpeciesDefault));
    }

    [Test]
    public void Resolve_SpeciesMatchesMap_TrimmedMapValue()
    {
        Dictionary<string, string> map = new() { ["SpeciesAlpha"] = "  species-alpha-timid  " };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve("SpeciesAlpha", map);
        Assert.That(result.PackId, Is.EqualTo("species-alpha-timid"));
    }

    [Test]
    public void Resolve_SpeciesMissFromMap_NoFallback_ReturnsNone()
    {
        Dictionary<string, string> map = new() { ["SpeciesAlpha"] = "species-alpha-timid" };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve("CampGuardian", map);
        Assert.Multiple(() =>
        {
            Assert.That(result.PackId, Is.Null);
            Assert.That(result.Source, Is.EqualTo(ResolutionSource.None));
            Assert.That(result.Species, Is.EqualTo("CampGuardian"));
        });
    }

    [Test]
    public void Resolve_SpeciesMissFromMap_WithFallback_ReturnsFallback()
    {
        Dictionary<string, string> map = new() { ["SpeciesAlpha"] = "species-alpha-timid" };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "CampGuardian",
            map,
            fallbackPackId: "camp-guardian-fallback");
        Assert.Multiple(() =>
        {
            Assert.That(result.PackId, Is.EqualTo("camp-guardian-fallback"));
            Assert.That(result.Source, Is.EqualTo(ResolutionSource.Fallback));
            Assert.That(result.Species, Is.EqualTo("CampGuardian"));
        });
    }

    [Test]
    public void Resolve_SpeciesMatchesMap_WithFallback_SpeciesDefaultWins()
    {
        // Species lookup takes priority over caller fallback - that's the
        // whole point of the operator-configured map.
        Dictionary<string, string> map = new() { ["SpeciesAlpha"] = "species-alpha-timid" };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "SpeciesAlpha",
            map,
            fallbackPackId: "per-character-override");
        Assert.That(result.PackId, Is.EqualTo("species-alpha-timid"));
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.SpeciesDefault));
    }

    [Test]
    public void Resolve_NullMap_WithFallback_ReturnsFallback()
    {
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "SpeciesAlpha",
            null,
            fallbackPackId: "manual-pack");
        Assert.That(result.PackId, Is.EqualTo("manual-pack"));
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.Fallback));
    }

    [Test]
    public void Resolve_EmptyMap_WithFallback_ReturnsFallback()
    {
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "SpeciesAlpha",
            new Dictionary<string, string>(),
            fallbackPackId: "manual-pack");
        Assert.That(result.PackId, Is.EqualTo("manual-pack"));
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.Fallback));
    }

    [Test]
    public void Resolve_BlankSpecies_WithFallback_ReturnsFallbackWithoutConsultingMap()
    {
        // Whitespace-only species treated as missing - resolver doesn't try the map.
        Dictionary<string, string> map = new() { ["SpeciesAlpha"] = "species-alpha-timid" };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "   ",
            map,
            fallbackPackId: "fallback");
        Assert.That(result.PackId, Is.EqualTo("fallback"));
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.Fallback));
        Assert.That(result.Species, Is.EqualTo(string.Empty));
    }

    [Test]
    public void Resolve_MapEntryWithBlankKey_SkippedNotMatched()
    {
        Dictionary<string, string> map = new()
        {
            ["   "] = "bogus-pack",
            ["SpeciesAlpha"] = "species-alpha-timid",
        };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve("SpeciesAlpha", map);
        Assert.That(result.PackId, Is.EqualTo("species-alpha-timid"));
    }

    [Test]
    public void Resolve_MapEntryWithBlankValue_SkippedFallthroughToFallback()
    {
        Dictionary<string, string> map = new()
        {
            ["SpeciesAlpha"] = "   ",
        };
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "SpeciesAlpha",
            map,
            fallbackPackId: "fallback");
        Assert.That(result.PackId, Is.EqualTo("fallback"));
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.Fallback));
    }

    [Test]
    public void Resolve_FallbackPackId_Trimmed()
    {
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "Unknown",
            new Dictionary<string, string>(),
            fallbackPackId: "  trimmed-fallback  ");
        Assert.That(result.PackId, Is.EqualTo("trimmed-fallback"));
    }

    [Test]
    public void Resolve_BlankFallback_TreatedAsNoFallback()
    {
        SpeciesPersonalityResolution result = SpeciesPersonalityResolver.Resolve(
            "Unknown",
            new Dictionary<string, string>(),
            fallbackPackId: "   ");
        Assert.That(result.PackId, Is.Null);
        Assert.That(result.Source, Is.EqualTo(ResolutionSource.None));
    }

    [Test]
    public void Resolve_MultipleMapEntries_PickRightOne()
    {
        // Sanity check that iteration finds the correct match in a populated map.
        Dictionary<string, string> map = new()
        {
            ["SpeciesAlpha"] = "species-alpha-timid",
            ["CampGuardian"] = "camp-guardian-aloof",
            ["CampScout"] = "camp-scout-fiery",
            ["SpeciesBeta"] = "species-beta-greedy",
        };
        Assert.Multiple(() =>
        {
            Assert.That(SpeciesPersonalityResolver.Resolve("CampGuardian", map).PackId, Is.EqualTo("camp-guardian-aloof"));
            Assert.That(SpeciesPersonalityResolver.Resolve("CampScout", map).PackId, Is.EqualTo("camp-scout-fiery"));
            Assert.That(SpeciesPersonalityResolver.Resolve("SpeciesBeta", map).PackId, Is.EqualTo("species-beta-greedy"));
            Assert.That(SpeciesPersonalityResolver.Resolve("SpeciesAlpha", map).PackId, Is.EqualTo("species-alpha-timid"));
        });
    }

    [Test]
    public void Resolve_DoesNotThrow_OnAnyInputShape()
    {
        // Exhaustive non-throw verification - the resolver is meant to be safe
        // to call from any layer including hot-path prompt assembly.
        Assert.DoesNotThrow(() => SpeciesPersonalityResolver.Resolve(null, null, null));
        Assert.DoesNotThrow(() => SpeciesPersonalityResolver.Resolve(string.Empty, null, null));
        Assert.DoesNotThrow(() => SpeciesPersonalityResolver.Resolve("SpeciesAlpha", null, null));
        Assert.DoesNotThrow(() => SpeciesPersonalityResolver.Resolve(null, new Dictionary<string, string>(), null));
        Assert.DoesNotThrow(() => SpeciesPersonalityResolver.Resolve(null, null, string.Empty));
        Assert.DoesNotThrow(() => SpeciesPersonalityResolver.Resolve("X", new Dictionary<string, string> { [string.Empty] = string.Empty }, null));
    }
}
