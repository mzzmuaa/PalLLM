using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Tests;

/// <summary>
/// Pinning tests for the operator-actionable Suggestions[] surfaced in
/// /api/health. The builder is pure and consumed by the dashboard, the
/// `pal next` advisor, `pal doctor`, and any MCP client polling runtime
/// health, so each hint code + severity bucket + ordering is treated as
/// part of the contract.
/// </summary>
public sealed class HealthSuggestionBuilderTests
{
    /// <summary>Default-healthy inputs. Tests start from this and flip
    /// individual fields to drive specific hint codes — keeps each test
    /// focused on one signal without copying 17 default values.</summary>
    private static HealthSuggestionInputs Healthy() =>
        new(
            LoadedPackCount: 4,
            InferenceConfigured: true,
            InferenceCircuitState: "Closed",
            InferenceSuccessCount: 100,
            InferenceFailureCount: 0,
            VisionEnabled: false,
            VisionCallCount: 0,
            VisionFailureCount: 0,
            TtsEnabled: false,
            TtsCallCount: 0,
            TtsFailureCount: 0,
            BridgeEnabled: true,
            BridgeBootCount: 1,
            BridgeEventCount: 50,
            InboxPendingCount: 2,
            OutboxPendingCount: 0,
            FailedFileCount: 0,
            ScreenshotPendingCount: 0,
            AutomationEnabled: false,
            AutomationAllowedActionCount: 0);

    [Test]
    public void Build_WhenEverythingIsHealthy_ReturnsEmptyList()
    {
        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(Healthy());

        Assert.That(suggestions, Is.Empty,
            "A healthy snapshot must surface no suggestions so the dashboard hides the panel.");
    }

    [Test]
    public void Build_WhenLoadedPackCountIsZero_SuggestsPalPackCopy_AsInfo()
    {
        HealthSuggestionInputs inputs = Healthy() with { LoadedPackCount = 0 };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion noPacks = suggestions.First(s => s.Code == "no-packs-loaded");
        Assert.That(noPacks.Command, Does.Contain("pal pack copy"),
            "No-packs hint must hand the operator a copy-paste command.");
        Assert.That(noPacks.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Info),
            "No-packs is informational -- the runtime still works without packs.");
    }

    [Test]
    public void Build_WhenInferenceCircuitOpen_SuggestsDoctor_AsUrgent()
    {
        HealthSuggestionInputs inputs = Healthy() with
        {
            InferenceCircuitState = "Open",
            InferenceSuccessCount = 0,
            InferenceFailureCount = 5,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion circuit = suggestions.First(s => s.Code == "inference-circuit-open");
        Assert.That(circuit.Command, Is.EqualTo("pal doctor"));
        Assert.That(circuit.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Urgent),
            "Inference circuit open is an active failure -- urgent.");
    }

    [Test]
    public void Build_WhenInferenceCircuitOpenButInferenceNotConfigured_DoesNotSuggest()
    {
        // A fallback-only deployment must not be pestered about an inference
        // circuit it explicitly opted out of.
        HealthSuggestionInputs inputs = Healthy() with
        {
            InferenceConfigured = false,
            InferenceCircuitState = "Open",
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        Assert.That(suggestions.Any(s => s.Code == "inference-circuit-open"), Is.False);
    }

    [Test]
    public void Build_WhenInferenceConfiguredAndAllRequestsFailing_SuggestsDoctor_AsUrgent()
    {
        HealthSuggestionInputs inputs = Healthy() with
        {
            InferenceCircuitState = "Closed", // breaker hasn't tripped yet
            InferenceSuccessCount = 0,
            InferenceFailureCount = 3,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion failures = suggestions.First(s => s.Code == "inference-only-failures");
        Assert.That(failures.Message, Does.Contain("3 failure"));
        Assert.That(failures.Command, Is.EqualTo("pal doctor"));
        Assert.That(failures.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Urgent));
    }

    [Test]
    public void Build_WhenVisionConfiguredAndAllRequestsFailing_SuggestsDoctor_AsWarn()
    {
        // Vision failures are warn (not urgent) because the chat path keeps
        // working without screenshot description.
        HealthSuggestionInputs inputs = Healthy() with
        {
            VisionEnabled = true,
            VisionCallCount = 4,
            VisionFailureCount = 4,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion vision = suggestions.First(s => s.Code == "vision-only-failures");
        Assert.That(vision.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Warn));
        Assert.That(vision.Message, Does.Contain("4 failure"));
    }

    [Test]
    public void Build_WhenVisionDisabled_DoesNotSuggestVisionFailures()
    {
        // An operator who never enabled vision must not be nagged about
        // vision lane failures even if stale failure counts somehow exist.
        HealthSuggestionInputs inputs = Healthy() with
        {
            VisionEnabled = false,
            VisionFailureCount = 99,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        Assert.That(suggestions.Any(s => s.Code == "vision-only-failures"), Is.False);
    }

    [Test]
    public void Build_WhenTtsConfiguredAndAllRequestsFailing_SuggestsDoctor_AsWarn()
    {
        HealthSuggestionInputs inputs = Healthy() with
        {
            TtsEnabled = true,
            TtsCallCount = 2,
            TtsFailureCount = 2,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion tts = suggestions.First(s => s.Code == "tts-only-failures");
        Assert.That(tts.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Warn));
        Assert.That(tts.Message, Does.Contain("2 failure"));
    }

    [Test]
    public void Build_WhenBridgeEnabledButIdle_SuggestsDoctor_AsInfo()
    {
        HealthSuggestionInputs inputs = Healthy() with
        {
            BridgeBootCount = 0,
            BridgeEventCount = 0,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion idle = suggestions.First(s => s.Code == "bridge-idle");
        Assert.That(idle.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Info),
            "Bridge-idle is informational -- the operator may simply not have launched Palworld yet.");
    }

    [Test]
    public void Build_WhenBridgeBootedButCurrentlyIdle_DoesNotSuggest()
    {
        // A bridge that has booted at least once but is currently idle is
        // fine -- the player just isn't talking to the companion right now.
        HealthSuggestionInputs inputs = Healthy() with
        {
            BridgeBootCount = 1,
            BridgeEventCount = 0,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        Assert.That(suggestions.Any(s => s.Code == "bridge-idle"), Is.False);
    }

    [Test]
    public void Build_WhenOutboxBacklogIsHigh_SuggestsCheck_AsWarn()
    {
        HealthSuggestionInputs inputs = Healthy() with { OutboxPendingCount = 100 };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion backlog = suggestions.First(s => s.Code == "outbox-backlog");
        Assert.That(backlog.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Warn));
        Assert.That(backlog.Message, Does.Contain("100"));
    }

    [Test]
    public void Build_WhenFailedFileCountIsHigh_SuggestsInspectingFailedFolder()
    {
        HealthSuggestionInputs inputs = Healthy() with { FailedFileCount = 100 };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion failed = suggestions.First(s => s.Code == "bridge-failed-files-accumulating");
        Assert.That(failed.Message, Does.Contain("100"));
        Assert.That(failed.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Warn));
    }

    [Test]
    public void Build_WhenInboxBacklogIsHigh_SuggestsRaisingPollSettings()
    {
        HealthSuggestionInputs inputs = Healthy() with { InboxPendingCount = 500 };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion backlog = suggestions.First(s => s.Code == "bridge-inbox-backlog");
        Assert.That(backlog.Message, Does.Contain("MaxEventsPerPoll").Or.Contain("PollIntervalMs"));
        Assert.That(backlog.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Warn));
    }

    [Test]
    public void Build_WhenAutomationEnabledButAllowlistEmpty_SuggestsConfigCheck_AsWarn()
    {
        // Real footgun: operator flips Automation.Enabled=true and forgets
        // to add action ids. The runtime correctly emits zero action
        // intents and the operator wonders why nothing happens.
        HealthSuggestionInputs inputs = Healthy() with
        {
            AutomationEnabled = true,
            AutomationAllowedActionCount = 0,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion automation = suggestions.First(s => s.Code == "automation-allowlist-empty-but-enabled");
        Assert.That(automation.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Warn));
        Assert.That(automation.Message, Does.Contain("AllowedActions").Or.Contain("allowlist"));
    }

    [Test]
    public void Build_WhenAutomationEnabledWithAllowlist_DoesNotSuggest()
    {
        HealthSuggestionInputs inputs = Healthy() with
        {
            AutomationEnabled = true,
            AutomationAllowedActionCount = 3,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        Assert.That(suggestions.Any(s => s.Code == "automation-allowlist-empty-but-enabled"), Is.False);
    }

    [Test]
    public void Build_WhenScreenshotsPendingButVisionDisabled_SuggestsToggle_AsInfo()
    {
        // Screenshots queueing with no consumer -- either enable vision or
        // turn the watcher off; otherwise the queue grows until the cap.
        HealthSuggestionInputs inputs = Healthy() with
        {
            ScreenshotPendingCount = 5,
            VisionEnabled = false,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        HealthSuggestion screenshots = suggestions.First(s => s.Code == "screenshots-pending-but-vision-disabled");
        Assert.That(screenshots.Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Info));
        Assert.That(screenshots.Message, Does.Contain("5"));
    }

    [Test]
    public void Build_WhenScreenshotsPendingAndVisionEnabled_DoesNotSuggest()
    {
        // Vision will consume them; nothing to flag.
        HealthSuggestionInputs inputs = Healthy() with
        {
            ScreenshotPendingCount = 5,
            VisionEnabled = true,
            VisionCallCount = 0,
            VisionFailureCount = 0,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        Assert.That(suggestions.Any(s => s.Code == "screenshots-pending-but-vision-disabled"), Is.False);
    }

    [Test]
    public void Build_OrdersBySeverityUrgentFirst()
    {
        // The most pressing entry must be at index 0 so a UI that only has
        // room for one card surfaces the right thing. Trip one hint per
        // severity bucket and assert urgent < warn < info.
        HealthSuggestionInputs inputs = Healthy() with
        {
            // info: bridge enabled but never booted
            BridgeBootCount = 0,
            BridgeEventCount = 0,
            // warn: outbox backlog
            OutboxPendingCount = 100,
            // urgent: inference circuit open
            InferenceCircuitState = "Open",
            InferenceSuccessCount = 0,
            InferenceFailureCount = 5,
        };

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        Assert.That(suggestions, Has.Count.GreaterThanOrEqualTo(3));
        // Map each entry to its severity index for the assertion.
        int[] severities = suggestions.Select(s => s.Severity switch
        {
            "urgent" => 0,
            "warn" => 1,
            "info" => 2,
            _ => 3,
        }).ToArray();

        for (int i = 1; i < severities.Length; i++)
        {
            Assert.That(severities[i], Is.GreaterThanOrEqualTo(severities[i - 1]),
                "Suggestions must be ordered urgent -> warn -> info; index " + i +
                " breaks the contract: " + string.Join(",", suggestions.Select(s => $"{s.Code}({s.Severity})")));
        }

        Assert.That(suggestions[0].Severity, Is.EqualTo(HealthSuggestionBuilder.Severity.Urgent),
            "Index 0 must be urgent when an urgent hint is present, so 'Suggestions[0]' is the right one-card pick.");
    }

    [Test]
    public void Build_OrderIsStableForSameInputs()
    {
        // Dashboards poll repeatedly; if the order flapped between identical
        // inputs the Suggestions[0] highlighted card would jitter and be useless.
        HealthSuggestionInputs inputs = Healthy() with
        {
            LoadedPackCount = 0,
            InferenceCircuitState = "Open",
            InferenceSuccessCount = 0,
            InferenceFailureCount = 5,
            BridgeBootCount = 0,
            BridgeEventCount = 0,
        };

        IReadOnlyList<HealthSuggestion> a = HealthSuggestionBuilder.Build(inputs);
        IReadOnlyList<HealthSuggestion> b = HealthSuggestionBuilder.Build(inputs);

        Assert.That(b.Select(s => s.Code), Is.EqualTo(a.Select(s => s.Code)));
    }

    [Test]
    public void Build_EveryHintCarriesNonEmptyCodeMessageAndSeverity()
    {
        // Trip every hint code at once and verify the basic contract --
        // every entry has the three required fields populated. Note we
        // can't trip 'screenshots-pending-but-vision-disabled' alongside
        // 'vision-only-failures' because they're mutually exclusive
        // (Vision either Enabled or not), so the smoke test covers
        // VisionEnabled=true and lets the screenshot-disabled hint run
        // in its own test above.
        HealthSuggestionInputs inputs = new(
            LoadedPackCount: 0,
            InferenceConfigured: true,
            InferenceCircuitState: "Open",
            InferenceSuccessCount: 0,
            InferenceFailureCount: 5,
            VisionEnabled: true,
            VisionCallCount: 3,
            VisionFailureCount: 3,
            TtsEnabled: true,
            TtsCallCount: 2,
            TtsFailureCount: 2,
            BridgeEnabled: true,
            BridgeBootCount: 0,
            BridgeEventCount: 0,
            InboxPendingCount: 500,
            OutboxPendingCount: 100,
            FailedFileCount: 100,
            ScreenshotPendingCount: 0,
            AutomationEnabled: true,
            AutomationAllowedActionCount: 0);

        IReadOnlyList<HealthSuggestion> suggestions = HealthSuggestionBuilder.Build(inputs);

        Assert.That(suggestions, Is.Not.Empty);
        foreach (HealthSuggestion s in suggestions)
        {
            Assert.That(s.Code, Is.Not.Null.And.Not.Empty);
            Assert.That(s.Message, Is.Not.Null.And.Not.Empty);
            Assert.That(s.Severity, Is.AnyOf(
                HealthSuggestionBuilder.Severity.Info,
                HealthSuggestionBuilder.Severity.Warn,
                HealthSuggestionBuilder.Severity.Urgent),
                $"Hint '{s.Code}' has unexpected severity '{s.Severity}'.");
        }
    }
}
