using System.Text.Json;
using PalLLM.Domain;

namespace PalLLM.Domain.Packs;

/// <summary>
/// Structured validator for narrative pack JSON. Exposes actionable errors so pack
/// authors can debug without guessing at the schema. Complements the resilient-load
/// path in <see cref="NarrativePackService.Reload"/> by giving an explicit "dry run"
/// endpoint the runtime (and tooling) can call before shipping content.
/// </summary>
public static class NarrativePackValidator
{
    /// <summary>
    /// Shared maximum size for a single narrative-pack JSON payload, whether it
    /// arrives over <c>POST /api/packs/validate</c> or is discovered from disk
    /// during startup/reload. Keeps authoring and runtime ingestion on one
    /// bounded contract.
    /// </summary>
    public const int MaxPackBytes = 1_000_000;

    private static readonly JsonSerializerOptions ValidationJsonOptions = PalLlmDomainJsonOptions.Create(static options =>
    {
        options.PropertyNameCaseInsensitive = true;
        options.AllowTrailingCommas = true;
        options.ReadCommentHandling = JsonCommentHandling.Skip;
    });
    private static readonly PalLlmDomainJsonSerializerContext ValidationJsonContext = new(ValidationJsonOptions);

    public static NarrativePackValidationResult Validate(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("$", "Pack payload is empty.");
        }

        NarrativePackDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize(json, ValidationJsonContext.NarrativePackDefinition);
        }
        catch (JsonException ex)
        {
            return Invalid("$", DescribeParseFailure(ex));
        }

        if (definition is null)
        {
            return Invalid("$", "Pack JSON deserialised to null.");
        }

        definition = NarrativePackNormalizer.Normalize(definition);

        List<NarrativePackValidationError> errors = [];

        if (string.IsNullOrWhiteSpace(definition.Name))
        {
            errors.Add(new("Name", "Pack name is required."));
        }

        if (definition.Characters.Count == 0)
        {
            errors.Add(new("Characters", "At least one character is required."));
        }

        HashSet<string> seenIds = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < definition.Characters.Count; i++)
        {
            NarrativeCharacterProfile character = definition.Characters[i];
            string root = $"Characters[{i}]";

            if (string.IsNullOrWhiteSpace(character.Id))
            {
                errors.Add(new($"{root}.Id", "Character id is required."));
            }
            else if (!seenIds.Add(character.Id.Trim()))
            {
                errors.Add(new($"{root}.Id", $"Duplicate character id '{character.Id}'."));
            }

            if (string.IsNullOrWhiteSpace(character.Name))
            {
                errors.Add(new($"{root}.Name", "Character name is required."));
            }

            foreach (KeyValuePair<string, int> skill in character.Skills)
            {
                if (skill.Value < 0 || skill.Value > 20)
                {
                    errors.Add(new($"{root}.Skills[\"{skill.Key}\"]",
                        "Skill value must be between 0 and 20."));
                }
            }
        }

        for (int i = 0; i < definition.Relationships.Count; i++)
        {
            NarrativeRelationshipSeed rel = definition.Relationships[i];
            string root = $"Relationships[{i}]";

            if (string.IsNullOrWhiteSpace(rel.CharacterA) || string.IsNullOrWhiteSpace(rel.CharacterB))
            {
                errors.Add(new(root, "Both CharacterA and CharacterB are required."));
            }

            if (!string.IsNullOrWhiteSpace(rel.CharacterA) && !seenIds.Contains(rel.CharacterA.Trim()))
            {
                errors.Add(new($"{root}.CharacterA",
                    $"Relationship references unknown character id '{rel.CharacterA}'."));
            }

            if (!string.IsNullOrWhiteSpace(rel.CharacterB) && !seenIds.Contains(rel.CharacterB.Trim()))
            {
                errors.Add(new($"{root}.CharacterB",
                    $"Relationship references unknown character id '{rel.CharacterB}'."));
            }

            if (rel.Opinion < -100 || rel.Opinion > 100)
            {
                errors.Add(new($"{root}.Opinion",
                    "Opinion must be between -100 and 100."));
            }
        }

        for (int i = 0; i < definition.MemorySeeds.Count; i++)
        {
            NarrativeMemorySeed seed = definition.MemorySeeds[i];
            string root = $"MemorySeeds[{i}]";

            if (string.IsNullOrWhiteSpace(seed.CharacterId))
            {
                errors.Add(new($"{root}.CharacterId", "Memory seed must reference a character id."));
            }
            else if (!seenIds.Contains(seed.CharacterId.Trim()))
            {
                errors.Add(new($"{root}.CharacterId",
                    $"Memory seed references unknown character id '{seed.CharacterId}'."));
            }

            if (string.IsNullOrWhiteSpace(seed.Content))
            {
                errors.Add(new($"{root}.Content", "Memory seed content is required."));
            }

            if (seed.Importance < 0f || seed.Importance > 1f)
            {
                errors.Add(new($"{root}.Importance", "Importance must be between 0 and 1."));
            }
        }

        foreach (PackPublicationSafetyFinding finding in PackPublicationSafetyValidator.CollectNarrativeFindings(definition))
        {
            errors.Add(new(finding.Path, finding.Message));
        }

        return new NarrativePackValidationResult
        {
            IsValid = errors.Count == 0,
            Name = definition.Name,
            CharacterCount = definition.Characters.Count,
            RelationshipCount = definition.Relationships.Count,
            MemorySeedCount = definition.MemorySeeds.Count,
            Errors = errors,
        };
    }

    private static NarrativePackValidationResult Invalid(string path, string message) =>
        new()
        {
            IsValid = false,
            Errors = [new NarrativePackValidationError(path, message)],
        };

    private static string DescribeParseFailure(JsonException ex)
    {
        if (ex.LineNumber is long lineNumber &&
            ex.BytePositionInLine is long bytePositionInLine)
        {
            return $"Pack JSON could not be parsed near line {lineNumber + 1}, byte {bytePositionInLine + 1}.";
        }

        if (!string.IsNullOrWhiteSpace(ex.Path))
        {
            return $"Pack JSON could not be parsed near path '{ex.Path}'.";
        }

        return "Pack JSON could not be parsed.";
    }
}

public sealed class NarrativePackValidationResult
{
    public bool IsValid { get; init; }

    public string Name { get; init; } = string.Empty;

    public int CharacterCount { get; init; }

    public int RelationshipCount { get; init; }

    public int MemorySeedCount { get; init; }

    public IReadOnlyList<NarrativePackValidationError> Errors { get; init; } = [];
}

public sealed record NarrativePackValidationError(string Path, string Message);
