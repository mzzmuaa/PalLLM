// ---------------------------------------------------------------
// AGENT-CARD:
//   what:    pure function that maps a Palworld species name to a personality-pack id
//            using the operator-configured PalLlmOptions.Packs.DefaultBySpecies map,
//            with a per-call fallbackPackId chain. Lets same-species companions share a default
//            personality without having to author one pack per character id.
//   surface: SpeciesPersonalityResolver.Resolve(species, defaultBySpecies, fallbackPackId?),
//            SpeciesPersonalityResolution record (PackId / Source / Species).
//   gate:    Drift_Feature_catalog_count (one new entry), Drift_Test_count_docs (new tests).
//   adr:     follows the deterministic-first advisor pattern (adr/0001).
//   docs:    docs/ADVISORS.md row, docs/CHANGELOG.md, docs/HANDOFF.md.
// ---------------------------------------------------------------

namespace PalLLM.Domain.Packs;

/// <summary>
/// Resolves the preferred personality-pack id for a Palworld character based on
/// its species. Pure, deterministic, side-effect-free — safe to call from any
/// layer and from tests without fixture setup. The dispatch order is:
///
/// <list type="number">
///   <item>Operator-configured <c>PalLlmOptions:Packs:DefaultBySpecies</c>
///         entry whose key matches the species (case-insensitive, trimmed).</item>
///   <item>Caller-supplied <c>fallbackPackId</c> (e.g. the
///         per-character pack id when one is set).</item>
///   <item><c>null</c> — no pack should be auto-applied.</item>
/// </list>
///
/// <para>The point of this layer is to let an operator say "all species-alpha
/// companions are timid; all species-beta companions are aloof" once in config, instead of authoring one
/// per-character pack for every tamed pal. The PersonalityPack format itself
/// stays per-character; this advisor is the lookup table that decides which
/// pack to *use* when the character does not have an explicit assignment.</para>
///
/// <para>Both inputs are sanitised (null / empty / whitespace handled), and the
/// resolver never throws — it returns a structured
/// <see cref="SpeciesPersonalityResolution"/> with <see cref="ResolutionSource"/>
/// so callers can log which path fired without re-running the logic.</para>
/// </summary>
public static class SpeciesPersonalityResolver
{
    /// <summary>
    /// Decide which personality-pack id should apply to a character of the given species.
    /// </summary>
    /// <param name="species">The character's species (e.g. <c>"species-alpha"</c>, <c>"species-beta"</c>).
    /// Null / empty / whitespace returns the fallback chain without consulting the species map.</param>
    /// <param name="defaultBySpecies">The operator-configured species->packId map. Typically
    /// <c>PalLlmOptions.Packs.DefaultBySpecies</c>. Null or empty disables the species lane.</param>
    /// <param name="fallbackPackId">Optional caller-supplied default — usually the per-character
    /// pack id when one exists. Returned when the species lookup misses or species is missing.</param>
    public static SpeciesPersonalityResolution Resolve(
        string? species,
        IReadOnlyDictionary<string, string>? defaultBySpecies,
        string? fallbackPackId = null)
    {
        string normalisedSpecies = (species ?? string.Empty).Trim();
        string normalisedFallback = (fallbackPackId ?? string.Empty).Trim();

        // Step 1: species lookup.
        if (!string.IsNullOrEmpty(normalisedSpecies) && defaultBySpecies is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> pair in defaultBySpecies)
            {
                string key = (pair.Key ?? string.Empty).Trim();
                string value = (pair.Value ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(value))
                {
                    continue;
                }
                if (string.Equals(key, normalisedSpecies, StringComparison.OrdinalIgnoreCase))
                {
                    return new SpeciesPersonalityResolution(
                        PackId: value,
                        Source: ResolutionSource.SpeciesDefault,
                        Species: normalisedSpecies);
                }
            }
        }

        // Step 2: caller-supplied fallback.
        if (!string.IsNullOrEmpty(normalisedFallback))
        {
            return new SpeciesPersonalityResolution(
                PackId: normalisedFallback,
                Source: ResolutionSource.Fallback,
                Species: normalisedSpecies);
        }

        // Step 3: nothing to apply.
        return new SpeciesPersonalityResolution(
            PackId: null,
            Source: ResolutionSource.None,
            Species: normalisedSpecies);
    }
}

/// <summary>
/// Structured return value from <see cref="SpeciesPersonalityResolver.Resolve"/>. The
/// <see cref="Source"/> field is the diagnostic — callers can log which lane fired
/// without re-running the logic.
/// </summary>
/// <param name="PackId">The resolved pack id, or <c>null</c> if no pack applies.</param>
/// <param name="Source">Which dispatch lane produced the answer.</param>
/// <param name="Species">The trimmed species the lookup ran against
/// (empty string if the caller passed null / blank). Echoed for traceability.</param>
public sealed record SpeciesPersonalityResolution(
    string? PackId,
    ResolutionSource Source,
    string Species);

/// <summary>Dispatch lane the resolver took to produce its answer.</summary>
public enum ResolutionSource
{
    /// <summary>No species match, no fallback. <c>PackId</c> is null.</summary>
    None,

    /// <summary>The operator-configured species map produced the answer.</summary>
    SpeciesDefault,

    /// <summary>The caller-supplied fallback produced the answer (typically the per-character pack id).</summary>
    Fallback,
}
