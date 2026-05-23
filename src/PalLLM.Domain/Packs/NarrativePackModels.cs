using System.Linq;

namespace PalLLM.Domain.Packs;

public sealed class NarrativePackDefinition
{
    public string Name { get; init; } = string.Empty;

    public string Version { get; init; } = "1.0";

    public string Description { get; init; } = string.Empty;

    public string Author { get; init; } = "Unknown";

    public NarrativeScenarioProfile? Scenario { get; init; }

    public NarrativePublishProfile? Publish { get; init; }

    public List<NarrativeCharacterProfile> Characters { get; init; } = [];

    public List<NarrativeRelationshipSeed> Relationships { get; init; } = [];

    public List<NarrativeMemorySeed> MemorySeeds { get; init; } = [];
}

public sealed class NarrativeCharacterProfile
{
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public List<string> Aliases { get; init; } = [];

    public string Role { get; init; } = string.Empty;

    public string Personality { get; init; } = string.Empty;

    public string Backstory { get; init; } = string.Empty;

    public List<string> Traits { get; init; } = [];

    public Dictionary<string, int> Skills { get; init; } = new();
}

public sealed class NarrativeRelationshipSeed
{
    public string CharacterA { get; init; } = string.Empty;

    public string CharacterB { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public int Opinion { get; init; }
}

public sealed class NarrativeMemorySeed
{
    public string CharacterId { get; init; } = string.Empty;

    public string Content { get; init; } = string.Empty;

    public List<string> Tags { get; init; } = [];

    public float Importance { get; init; } = 0.5f;
}

public sealed class NarrativeScenarioProfile
{
    public string Theme { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public List<string> Tags { get; init; } = [];
}

public sealed class NarrativePublishProfile
{
    public string ListingSummary { get; init; } = string.Empty;

    public string Homepage { get; init; } = string.Empty;

    public string SourceUrl { get; init; } = string.Empty;

    public string License { get; init; } = string.Empty;
}

internal static class NarrativePackNormalizer
{
    public static NarrativePackDefinition Normalize(NarrativePackDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new NarrativePackDefinition
        {
            Name = definition.Name ?? string.Empty,
            Version = string.IsNullOrWhiteSpace(definition.Version) ? "1.0" : definition.Version,
            Description = definition.Description ?? string.Empty,
            Author = string.IsNullOrWhiteSpace(definition.Author) ? "Unknown" : definition.Author,
            Scenario = definition.Scenario is null ? null : Normalize(definition.Scenario),
            Publish = definition.Publish is null ? null : Normalize(definition.Publish),
            Characters = definition.Characters?
                .OfType<NarrativeCharacterProfile>()
                .Select(Normalize)
                .ToList() ?? [],
            Relationships = definition.Relationships?
                .OfType<NarrativeRelationshipSeed>()
                .Select(Normalize)
                .ToList() ?? [],
            MemorySeeds = definition.MemorySeeds?
                .OfType<NarrativeMemorySeed>()
                .Select(Normalize)
                .ToList() ?? [],
        };
    }

    private static NarrativeCharacterProfile Normalize(NarrativeCharacterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new NarrativeCharacterProfile
        {
            Id = profile.Id ?? string.Empty,
            Name = profile.Name ?? string.Empty,
            Aliases = CleanStrings(profile.Aliases),
            Role = profile.Role ?? string.Empty,
            Personality = profile.Personality ?? string.Empty,
            Backstory = profile.Backstory ?? string.Empty,
            Traits = CleanStrings(profile.Traits),
            Skills = profile.Skills?
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal),
        };
    }

    private static NarrativeRelationshipSeed Normalize(NarrativeRelationshipSeed relationship)
    {
        ArgumentNullException.ThrowIfNull(relationship);

        return new NarrativeRelationshipSeed
        {
            CharacterA = relationship.CharacterA ?? string.Empty,
            CharacterB = relationship.CharacterB ?? string.Empty,
            Type = relationship.Type ?? string.Empty,
            Opinion = relationship.Opinion,
        };
    }

    private static NarrativeMemorySeed Normalize(NarrativeMemorySeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);

        return new NarrativeMemorySeed
        {
            CharacterId = seed.CharacterId ?? string.Empty,
            Content = seed.Content ?? string.Empty,
            Tags = CleanStrings(seed.Tags),
            Importance = seed.Importance,
        };
    }

    private static NarrativeScenarioProfile Normalize(NarrativeScenarioProfile scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        return new NarrativeScenarioProfile
        {
            Theme = scenario.Theme ?? string.Empty,
            Summary = scenario.Summary ?? string.Empty,
            Tags = CleanStrings(scenario.Tags),
        };
    }

    private static NarrativePublishProfile Normalize(NarrativePublishProfile publish)
    {
        ArgumentNullException.ThrowIfNull(publish);

        return new NarrativePublishProfile
        {
            ListingSummary = publish.ListingSummary ?? string.Empty,
            Homepage = publish.Homepage ?? string.Empty,
            SourceUrl = publish.SourceUrl ?? string.Empty,
            License = publish.License ?? string.Empty,
        };
    }

    private static List<string> CleanStrings(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
}
