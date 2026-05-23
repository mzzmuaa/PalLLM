using System.Text.RegularExpressions;

namespace PalLLM.Domain.Packs;

/// <summary>
/// Deterministic publication-safety scan for shareable pack text.
/// This is a guardrail, not a legal opinion: it catches obvious official-
/// endorsement claims, unrelated third-party IP references, and broad
/// platform-scope language before a pack reaches public-facing surfaces.
/// </summary>
internal static class PackPublicationSafetyValidator
{
    public static IReadOnlyList<PackPublicationSafetyFinding> CollectNarrativeFindings(
        NarrativePackDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var fields = new List<PackPublicationSafetyField>
        {
            Field("Name", definition.Name, checkScope: true),
            Field("Description", definition.Description, checkScope: true),
            Field("Author", definition.Author, checkScope: false),
            Field("Scenario.Theme", definition.Scenario?.Theme, checkScope: true),
            Field("Scenario.Summary", definition.Scenario?.Summary, checkScope: true),
            Field("Publish.ListingSummary", definition.Publish?.ListingSummary, checkScope: true),
            Field("Publish.License", definition.Publish?.License, checkScope: false),
        };

        AddList(fields, "Scenario.Tags", definition.Scenario?.Tags, checkScope: true);

        for (int i = 0; i < definition.Characters.Count; i++)
        {
            NarrativeCharacterProfile character = definition.Characters[i];
            string root = $"Characters[{i}]";
            fields.Add(Field($"{root}.Name", character.Name, checkScope: false));
            fields.Add(Field($"{root}.Role", character.Role, checkScope: false));
            fields.Add(Field($"{root}.Personality", character.Personality, checkScope: false));
            fields.Add(Field($"{root}.Backstory", character.Backstory, checkScope: false));
            AddList(fields, $"{root}.Aliases", character.Aliases, checkScope: false);
            AddList(fields, $"{root}.Traits", character.Traits, checkScope: false);
        }

        for (int i = 0; i < definition.Relationships.Count; i++)
        {
            fields.Add(Field($"Relationships[{i}].Type", definition.Relationships[i].Type, checkScope: false));
        }

        for (int i = 0; i < definition.MemorySeeds.Count; i++)
        {
            NarrativeMemorySeed seed = definition.MemorySeeds[i];
            string root = $"MemorySeeds[{i}]";
            fields.Add(Field($"{root}.Content", seed.Content, checkScope: false));
            AddList(fields, $"{root}.Tags", seed.Tags, checkScope: false);
        }

        return Collect(fields);
    }

    public static IReadOnlyList<PackPublicationSafetyFinding> CollectPersonalityManifestFindings(
        PersonalityPackManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var fields = new List<PackPublicationSafetyField>
        {
            Field("Id", manifest.Id, checkScope: true),
            Field("DisplayName", manifest.DisplayName, checkScope: true),
            Field("Tagline", manifest.Tagline, checkScope: true),
            Field("Author", manifest.Author, checkScope: false),
            Field("Description", manifest.Description, checkScope: true),
            Field("VoiceConsentNotes", manifest.VoiceConsentNotes, checkScope: false),
        };
        AddList(fields, "SafetyFlags", manifest.SafetyFlags, checkScope: true);
        return Collect(fields);
    }

    public static IReadOnlyList<PackPublicationSafetyFinding> CollectPersonalityPromptFindings(
        string promptText) =>
        Collect([Field("prompt.md", promptText, checkScope: false)]);

    private static IReadOnlyList<PackPublicationSafetyFinding> Collect(
        IEnumerable<PackPublicationSafetyField> fields)
    {
        var findings = new List<PackPublicationSafetyFinding>();
        foreach (PackPublicationSafetyField field in fields)
        {
            string text = NormalizeText(field.Value);
            if (text.Length == 0)
            {
                continue;
            }

            AddMatch(
                findings,
                field.Path,
                text,
                OfficialImpersonationRegex,
                "Pack text must not imply official endorsement, sponsorship, or approval.");
            AddMatch(
                findings,
                field.Path,
                text,
                ThirdPartyIpRegex,
                "Pack text should stay original and avoid unrelated third-party IP or franchise references.");
            AddMatch(
                findings,
                field.Path,
                text,
                ThirdPartyRuntimeBrandRegex,
                "Pack text should avoid third-party model, runtime, or vendor brand references.");
            AddMatch(
                findings,
                field.Path,
                text,
                LegalOverclaimRegex,
                "Pack text must not claim legal, IP, or compliance certainty.");

            if (field.CheckScope)
            {
                AddMatch(
                    findings,
                    field.Path,
                    text,
                    BroadScopeRegex,
                    "Pack text should stay scoped to PalLLM for Palworld, not a broader platform or multi-game product.");
            }
        }

        return findings;
    }

    private static void AddList(
        List<PackPublicationSafetyField> fields,
        string path,
        IReadOnlyList<string>? values,
        bool checkScope)
    {
        if (values is null)
        {
            return;
        }

        for (int i = 0; i < values.Count; i++)
        {
            fields.Add(Field($"{path}[{i}]", values[i], checkScope));
        }
    }

    private static PackPublicationSafetyField Field(string path, string? value, bool checkScope) =>
        new(path, value ?? string.Empty, checkScope);

    private static void AddMatch(
        List<PackPublicationSafetyFinding> findings,
        string path,
        string text,
        Regex regex,
        string messagePrefix)
    {
        Match match = regex.Match(text);
        if (!match.Success)
        {
            return;
        }

        findings.Add(new PackPublicationSafetyFinding(path, $"{messagePrefix} Found: {match.Value}."));
    }

    private static string NormalizeText(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : WhitespaceRegex.Replace(value.Trim(), " ");

    private static readonly Regex WhitespaceRegex = new(
        "\\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex OfficialImpersonationRegex = new(
        "\\b(?:official|endorsed|sponsored|approved|authorized|certified)\\b.{0,60}\\b(?:Palworld|Pocketpair|Steam|Valve)\\b|\\b(?:Palworld|Pocketpair|Steam|Valve)\\b.{0,60}\\b(?:official|endorsed|sponsored|approved|authorized|certified)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ThirdPartyIpRegex = new(
        "\\b(?:Pok(?:e|\\u00E9)mon|Pikachu|Nintendo|Mario|Zelda|Star\\s+Wars|Jedi|Sith|Marvel|Avengers|DC(?:\\s+Comics)?|Batman|Superman|Wonder\\s+Woman|Disney|Minecraft|Fortnite|Roblox|RimWorld|Skyrim|Fallout|Cyberpunk\\s+2077|Harry\\s+Potter|Hogwarts|Warhammer|Mass\\s+Effect|Dragon\\s+Age|The\\s+Witcher|League\\s+of\\s+Legends|Dungeons\\s*(?:&|and)\\s*Dragons|D\\s*&\\s*D|DnD|Baldur.?s\\s+Gate|Elden\\s+Ring|Dark\\s+Souls|Monster\\s+Hunter|Final\\s+Fantasy|World\\s+of\\s+Warcraft|Warcraft|One\\s+Piece|Dragon\\s+Ball|Naruto|Gundam|Studio\\s+Ghibli|Ghibli|ARK\\s*:?\\s*Survival)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ThirdPartyRuntimeBrandRegex = new(
        "\\b(?:OpenAI|ChatGPT|GPT-4|Claude|Anthropic|Gemini|Google|Microsoft|Ollama|LM\\s*Studio|vLLM|SGLang|llama\\.cpp|Mistral|Llama|Meta|DashScope|Qwen[0-9A-Za-z:._-]*|Gemma[0-9A-Za-z:._-]*|NVIDIA|TensorRT(?:-LLM)?|Hugging\\s+Face|OpenVINO|Foundry\\s+Local|DeepSeek|Grok)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BroadScopeRegex = new(
        "\\b(?:generic\\s+AI\\s+platform|multi[-\\s]?game|cross[-\\s]?game|universal\\s+game\\s+agent|all\\s+games|browser\\s+agent|computer\\s+use)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex LegalOverclaimRegex = new(
        "\\b(?:lawyer[-\\s]?proof|legal[-\\s]?risk[-\\s]?free|no\\s+legal\\s+risk|guaranteed\\s+legal|fully\\s+IP[-\\s]?neutral|100%\\s+IP[-\\s]?neutral|compliance[-\\s]?certified)\\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly record struct PackPublicationSafetyField(
        string Path,
        string Value,
        bool CheckScope);
}

internal readonly record struct PackPublicationSafetyFinding(
    string Path,
    string Message);
