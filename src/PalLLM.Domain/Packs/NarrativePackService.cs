using System.Text.Json;
using PalLLM.Domain;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Loads + validates personality packs + narrative packs from
//            PackDir, caches their summaries, exposes them to the runtime
//            and to the /api/packs HTTP route. Reload-without-restart path
//            via /api/packs/reload re-enters this service.
//   surface: NarrativePackService.GetSummaries / Reload / TryGetPersonality.
//            Wired to the GET /api/packs and POST /api/packs/reload
//            endpoints in Program.cs.
//   gate:    Drift_Api_route_count + Drift_OpenApi_snapshot via the
//            registered routes; pack-format gates live inline in
//            PersonalityPack.cs (already AGENT-CARD'd).
//   adr:     None.
//   docs:    docs/PACK_AUTHORING.md, docs/PACK_SAMPLES.md (the four sample
//            personalities), docs/MOMENTS.md (per-pack moments overlays).
// ---------------------------------------------------------------------------

namespace PalLLM.Domain.Packs;

public sealed class NarrativePackService
{
    private static readonly JsonSerializerOptions JsonOptions = PalLlmDomainJsonOptions.Create(static options =>
    {
        options.PropertyNameCaseInsensitive = true;
    });
    private static readonly PalLlmDomainJsonSerializerContext JsonContext = new(JsonOptions);

    private readonly PalLlmOptions _options;
    private readonly object _gate = new();
    private readonly List<LoadedNarrativePack> _packs = [];
    private readonly Dictionary<string, NarrativeCharacterProfile> _characterLoreIndex =
        new(StringComparer.OrdinalIgnoreCase);

    public NarrativePackService(PalLlmOptions options)
    {
        _options = options;
        _options.EnsureDirectories();
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _packs.Count;
            }
        }
    }

    public IReadOnlyList<PackSummary> GetSummaries()
    {
        lock (_gate)
        {
            return _packs
                .Select(pack => new PackSummary
                {
                    Name = pack.Definition.Name,
                    Author = pack.Definition.Author,
                    CharacterCount = pack.Definition.Characters.Count,
                    FilePath = BuildPackDisplayPath(pack.FilePath),
                })
                .ToArray();
        }
    }

    private string BuildPackDisplayPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(_options.PackDir, filePath);
        }
        catch (ArgumentException)
        {
            relativePath = Path.GetFileName(filePath);
        }

        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath == "."
            || Path.IsPathRooted(relativePath)
            || relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            relativePath = Path.GetFileName(filePath);
        }

        return relativePath.Replace('\\', '/');
    }

    public void Reload()
    {
        _options.EnsureDirectories();
        var loaded = new List<LoadedNarrativePack>();
        var loreIndex = new Dictionary<string, NarrativeCharacterProfile>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (string file in Directory.EnumerateFiles(
                         _options.PackDir,
                         "*.json",
                         new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                         }))
            {
                if (TryLoadPack(file, out LoadedNarrativePack? pack))
                {
                    loaded.Add(pack);
                    IndexPackCharacters(pack, loreIndex);
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            // Pack roots are runtime-owned and normally pre-created, but a missing
            // directory during reload should degrade to "no packs loaded", not tear
            // down startup or the HTTP-triggered reload route.
        }
        catch (IOException)
        {
            // Same intent as DirectoryNotFoundException above: a transient filesystem
            // error (locked file, antivirus contention, sleeping device) during a
            // best-effort reload should leave the existing pack list untouched, not
            // throw out of an HTTP-triggered reload or background warm-up.
        }
        catch (UnauthorizedAccessException)
        {
            // Same intent as DirectoryNotFoundException above: an ACL surprise on the
            // pack root must not crash the sidecar. Operators see "no packs loaded"
            // and can fix permissions out-of-band; deterministic chat keeps working.
        }

        lock (_gate)
        {
            _packs.Clear();
            _packs.AddRange(loaded);
            _characterLoreIndex.Clear();
            foreach ((string key, NarrativeCharacterProfile value) in loreIndex)
            {
                _characterLoreIndex[key] = value;
            }
        }
    }

    public NarrativeCharacterProfile? FindCharacterLore(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        lock (_gate)
        {
            return _characterLoreIndex.TryGetValue(displayName.Trim(), out NarrativeCharacterProfile? match)
                ? match
                : null;
        }
    }

    private static bool TryLoadPack(string file, out LoadedNarrativePack pack)
    {
        pack = null!;
        BoundedJsonFileReader.JsonReadResult<NarrativePackDefinition> read = BoundedJsonFileReader.TryRead(
            file,
            NarrativePackValidator.MaxPackBytes,
            stream => JsonSerializer.Deserialize(stream, JsonContext.NarrativePackDefinition));
        if (!read.Succeeded || read.Value is null)
        {
            return false;
        }

        // One malformed, unreadable, or oversized pack must not tear down the
        // whole reload pass. The explicit validator endpoint remains the
        // author-facing place to inspect why a specific file failed.
        pack = new LoadedNarrativePack(file, NarrativePackNormalizer.Normalize(read.Value));
        return true;
    }

    private static void IndexPackCharacters(
        LoadedNarrativePack pack,
        Dictionary<string, NarrativeCharacterProfile> loreIndex)
    {
        foreach (NarrativeCharacterProfile character in pack.Definition.Characters)
        {
            AddLoreKey(loreIndex, character.Name, character);
            foreach (string alias in character.Aliases)
            {
                AddLoreKey(loreIndex, alias, character);
            }
        }
    }

    private static void AddLoreKey(
        Dictionary<string, NarrativeCharacterProfile> loreIndex,
        string? key,
        NarrativeCharacterProfile character)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        string normalized = key.Trim();
        if (loreIndex.ContainsKey(normalized))
        {
            return;
        }

        loreIndex[normalized] = character;
    }
}

public sealed record LoadedNarrativePack(string FilePath, NarrativePackDefinition Definition);
