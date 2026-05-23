using System.Net;
using System.Net.Http;
using System.Text.Json;
using PalLLM.Domain.Inference;

namespace PalLLM.Tests;

/// <summary>
/// Covers the <c>GET /api/quickstart</c> endpoint. Contract pinned here:
///
/// 1. Always returns 200 with a <c>QuickstartGuide</c> payload shaped as
///    {OverallStatus, Headline, OperatorHealth, Steps[]}.
/// 2. <c>OverallStatus</c> is one of "ready" / "needs-setup" /
///    "needs-attention" and is derived from step priorities; the headline
///    is always a single non-empty sentence that matches the status.
/// 3. Every <c>QuickstartStep</c> carries a non-empty label, why, action,
///    and verify field so both humans and AI callers can act on it without
///    further context.
/// 4. Inference-related steps only surface when inference is relevant —
///    a fallback-only operator sees a "recommended" nudge to enable it, not
///    a "critical" failure.
/// </summary>
public sealed class QuickstartGuideEndpointTests
{
    [Test]
    public async Task GetQuickstart_ReturnsStructuredNextStepGuidance()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/quickstart");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // PascalCase because the sidecar pins PropertyNamingPolicy=null.
        Assert.That(root.TryGetProperty("OverallStatus", out JsonElement status), Is.True);
        string statusValue = status.GetString() ?? string.Empty;
        Assert.That(statusValue, Is.AnyOf("ready", "needs-setup", "needs-attention"),
            "OverallStatus must be one of the three documented values.");

        Assert.That(root.TryGetProperty("Headline", out JsonElement headline), Is.True);
        Assert.That(headline.GetString(), Is.Not.Empty,
            "Every response must carry a non-empty one-liner.");

        Assert.That(root.TryGetProperty("OperatorHealth", out JsonElement health), Is.True);
        Assert.That(health.GetProperty("Score").GetInt32(), Is.InRange(0, 100));

        Assert.That(root.TryGetProperty("Steps", out JsonElement steps), Is.True);
        Assert.That(steps.ValueKind, Is.EqualTo(JsonValueKind.Array));

        foreach (JsonElement step in steps.EnumerateArray())
        {
            Assert.That(step.GetProperty("Priority").GetString(),
                Is.AnyOf("critical", "recommended", "optional"));
            Assert.That(step.GetProperty("Label").GetString(), Is.Not.Empty);
            Assert.That(step.GetProperty("Why").GetString(), Is.Not.Empty);
            Assert.That(step.GetProperty("Action").GetString(), Is.Not.Empty);
            Assert.That(step.GetProperty("Verify").GetString(), Is.Not.Empty);
        }
    }

    [Test]
    public async Task GetQuickstart_OnDefaultFixtureSuggestsInferenceAsRecommended()
    {
        // The default test fixture ships with inference disabled. That
        // should surface as a "recommended" step (not critical), because
        // the deterministic fallback is still replying to players.
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/quickstart");
        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        bool foundInferenceStep = false;
        foreach (JsonElement step in doc.RootElement.GetProperty("Steps").EnumerateArray())
        {
            string label = step.GetProperty("Label").GetString() ?? string.Empty;
            if (label.Contains("live inference", StringComparison.OrdinalIgnoreCase))
            {
                foundInferenceStep = true;
                Assert.That(step.GetProperty("Priority").GetString(), Is.EqualTo("recommended"),
                    "Enabling inference is quality-of-life, not a critical failure.");
                break;
            }
        }

        Assert.That(foundInferenceStep, Is.True,
            "A fresh test fixture should nudge operators toward live inference.");
    }

    [TestCase("mi300")]
    [TestCase("mi350")]
    public async Task GetQuickstart_WithAmdInstinctHint_SurfacesMxfp4Guidance(string hint)
    {
        string? hintSaved = Environment.GetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE");
        string? rocrSaved = Environment.GetEnvironmentVariable("ROCR_VISIBLE_DEVICES");

        try
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", hint);
            Environment.SetEnvironmentVariable("ROCR_VISIBLE_DEVICES", "0");
            HardwareProfiler.InvalidateCache();

            await using var fixture = new SidecarTestFixture();

            using HttpResponseMessage response = await fixture.Client.GetAsync("/api/quickstart");
            string body = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(body);

            bool foundMxfp4Step = false;
            foreach (JsonElement step in doc.RootElement.GetProperty("Steps").EnumerateArray())
            {
                string label = step.GetProperty("Label").GetString() ?? string.Empty;
                if (label.Contains("MXFP4", StringComparison.OrdinalIgnoreCase))
                {
                    foundMxfp4Step = true;
                    Assert.That(step.GetProperty("Priority").GetString(), Is.EqualTo("optional"));
                    Assert.That(step.GetProperty("Verify").GetString(), Does.Contain("RecommendedQuantization=mxfp4"));
                    break;
                }
            }

            Assert.That(foundMxfp4Step, Is.True,
                "AMD Instinct hinting should surface the MXFP4 quickstart nudge.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PALLLM_GPU_ARCHITECTURE", hintSaved);
            Environment.SetEnvironmentVariable("ROCR_VISIBLE_DEVICES", rocrSaved);
            HardwareProfiler.InvalidateCache();
        }
    }
}
