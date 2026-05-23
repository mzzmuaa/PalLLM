using System.IO;
using System.Text.Json;
using NUnit.Framework;

namespace PalLLM.Tests;

/// <summary>
/// Pins the shipping <c>appsettings.json</c> to the operator-curated
/// <c>D:\Models</c> library. Pass 337 made every model identifier in the
/// shipping config resolve to a file under the operator's curated GGUF
/// library — anything else (an Ollama tag like <c>qwen3.6:35b-a3b</c>, a
/// raw Hugging Face spec like <c>hf.co/unsloth/...</c>, or a placeholder
/// like <c>gemma4:e2b</c>) is regression to a pre-Pass-334 state and must
/// fail loudly.
///
/// <para>The guard is intentionally narrow: it verifies what's required to
/// keep the user's "only D:\Models" guarantee true, no more. Specifically:
/// <c>ExternalModelsRoot</c> must point at the curated path; the inference
/// + vision model identifiers must be present in the curated inventory
/// (verified by name shape, not by filesystem presence — the test must
/// run on machines that don't have D:\Models locally).</para>
/// </summary>
[TestFixture]
public class ShippingAppsettingsCurationTests
{
    private static readonly string[] DisallowedModelIdentifierFragments = new[]
    {
        // Ollama-style tags from the pre-curation defaults.
        "qwen3.6:35b-a3b",
        "qwen3.6:27b",
        "gemma3:4b",
        "gemma3:1b",
        "gemma4:e2b",
        "gemma4:e4b",
        // Raw HF specs — the curation references the local unsloth UD-* name
        // (e.g. "Qwen3.6-35B-A3B-UD-Q8_K_XL"), not the upstream HF repo path.
        "hf.co/unsloth/",
        "hf.co/",
        // Bonsai family — operator-deprioritised per Pass 336.
        "bonsai",
    };

    private static readonly string[] RequiredCuratedSubstrings = new[]
    {
        // The operator-curated unsloth UD-* identifier shape. The shipping
        // ModelTiers + Inference.Model + Vision.Model all reference these.
        "UD-Q",
        "UD-IQ",
    };

    [Test]
    public void ShippingAppsettings_HasExternalModelsRootPointingAtCuratedLibrary()
    {
        string path = LocateShippingAppsettings();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement palLlm = doc.RootElement.GetProperty("PalLLM");

        Assert.That(palLlm.TryGetProperty("ExternalModelsRoot", out JsonElement root), Is.True,
            "shipping appsettings.json must declare PalLLM.ExternalModelsRoot so PalLlmOptions.ModelsDir resolves to the operator's curated library.");

        string? value = root.GetString();
        Assert.That(value, Is.Not.Null.And.Not.Empty,
            "PalLLM.ExternalModelsRoot must be a non-empty path; empty means the resolver falls back to the legacy %LOCALAPPDATA% layout.");
        Assert.That(value, Does.Contain("Models"),
            $"PalLLM.ExternalModelsRoot ('{value}') must reference a curated 'Models' directory.");
    }

    [Test]
    public void ShippingAppsettings_InferenceModel_NoOllamaOrHfTagsLeak()
    {
        AssertNoDisallowedIdentifiersInPath("PalLLM.Inference.Model");
    }

    [Test]
    public void ShippingAppsettings_VisionModel_NoOllamaOrHfTagsLeak()
    {
        AssertNoDisallowedIdentifiersInPath("PalLLM.Vision.Model");
    }

    [Test]
    public void ShippingAppsettings_ModelTiers_NoOllamaOrHfTagsLeak()
    {
        string path = LocateShippingAppsettings();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement tiers = doc.RootElement
            .GetProperty("PalLLM")
            .GetProperty("Inference")
            .GetProperty("ModelTiers");

        Assert.That(tiers.ValueKind, Is.EqualTo(JsonValueKind.Array));
        int index = 0;
        foreach (JsonElement tier in tiers.EnumerateArray())
        {
            string id = tier.TryGetProperty("Id", out JsonElement idElem) ? (idElem.GetString() ?? "?") : "?";
            string model = tier.TryGetProperty("Model", out JsonElement modelElem) ? (modelElem.GetString() ?? string.Empty) : string.Empty;

            foreach (string disallowed in DisallowedModelIdentifierFragments)
            {
                Assert.That(
                    model.Contains(disallowed, System.StringComparison.OrdinalIgnoreCase),
                    Is.False,
                    $"ModelTiers[{index}] (Id='{id}') Model='{model}' contains disallowed fragment '{disallowed}'. " +
                    $"Shipping defaults must reference operator-curated D:\\Models GGUFs only; see docs/LOCAL_MODELS_INVENTORY.md.");
            }

            bool hasCuratedShape = false;
            foreach (string required in RequiredCuratedSubstrings)
            {
                if (model.Contains(required, System.StringComparison.OrdinalIgnoreCase))
                {
                    hasCuratedShape = true;
                    break;
                }
            }
            Assert.That(hasCuratedShape, Is.True,
                $"ModelTiers[{index}] (Id='{id}') Model='{model}' lacks the curated unsloth UD-* identifier shape. " +
                $"Curated identifiers contain 'UD-Q' or 'UD-IQ' (e.g. 'Qwen3.6-35B-A3B-UD-Q8_K_XL').");

            index++;
        }

        Assert.That(index, Is.GreaterThanOrEqualTo(2),
            "ModelTiers must declare at least the fast-start ('small') and quality ('large') tiers.");
    }

    private static void AssertNoDisallowedIdentifiersInPath(string dottedPath)
    {
        string path = LocateShippingAppsettings();
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));

        JsonElement node = doc.RootElement;
        foreach (string segment in dottedPath.Split('.'))
        {
            Assert.That(node.TryGetProperty(segment, out JsonElement child), Is.True,
                $"appsettings.json missing required section '{segment}' in path '{dottedPath}'.");
            node = child;
        }

        string value = node.GetString() ?? string.Empty;
        foreach (string disallowed in DisallowedModelIdentifierFragments)
        {
            Assert.That(
                value.Contains(disallowed, System.StringComparison.OrdinalIgnoreCase),
                Is.False,
                $"{dottedPath} = '{value}' contains disallowed fragment '{disallowed}'. " +
                $"Shipping defaults must reference operator-curated D:\\Models GGUFs only.");
        }

        bool hasCuratedShape = false;
        foreach (string required in RequiredCuratedSubstrings)
        {
            if (value.Contains(required, System.StringComparison.OrdinalIgnoreCase))
            {
                hasCuratedShape = true;
                break;
            }
        }
        Assert.That(hasCuratedShape, Is.True,
            $"{dottedPath} = '{value}' lacks the curated unsloth UD-* identifier shape (looked for 'UD-Q' or 'UD-IQ'). " +
            $"See docs/LOCAL_MODELS_INVENTORY.md for the full curated identifier list.");
    }

    private static string LocateShippingAppsettings()
    {
        string testBin = TestContext.CurrentContext.TestDirectory;
        DirectoryInfo? current = new(testBin);
        while (current is not null)
        {
            string candidate = Path.Combine(
                current.FullName,
                "src",
                "PalLLM.Sidecar",
                "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException(
            "Could not locate shipping appsettings.json by walking up from the test bin directory.");
    }
}
