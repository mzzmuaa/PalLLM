using System.IO;
using System.Text.Json;
using PalLLM.Domain.Configuration;

namespace PalLLM.Tests;

/// <summary>
/// Pass 358 — pins the SLO contract + the shipping Prometheus alert
/// rules + the reference Grafana dashboard. Without these tests the
/// alert thresholds can silently drift away from the SLO doc, the
/// dashboard panels can lose their threshold colors, and the runbook
/// URLs can break. The senior-dev review's Tier-S #4 item is "no
/// shipping alert rules, no SLI/SLO contract, no reference
/// dashboard"; Pass 358 ships all three and these tests pin them.
/// </summary>
[TestFixture]
public sealed class ObservabilitySloTests
{
    // ---------- The 6 shipping alert rules ----------

    [Test]
    public void AlertsYaml_ShipsAllSixSloAlerts()
    {
        string text = ReadAlertsYaml();

        // Each alert is named so AlertManager routing trees + the
        // SLO doc's runbook section anchors line up.
        string[] requiredAlerts = new[]
        {
            "PalLLMServiceDown",
            "PalLLMChatLatencyHigh_Warning",
            "PalLLMChatLatencyHigh_Critical",
            "PalLLMFallbackRateHigh",
            "PalLLMInboxBacklogGrowing",
            "PalLLMInferenceLaneRed",
        };
        foreach (string name in requiredAlerts)
        {
            Assert.That(text, Does.Contain($"alert: {name}"),
                $"palllm.alerts.yaml must declare alert '{name}'.");
        }

        // Each alert must declare a severity label so a standard
        // Alertmanager routing tree picks it up.
        Assert.That(text, Does.Contain("severity: critical"),
            "palllm.alerts.yaml must use severity: critical for page-grade alerts (ServiceDown + Latency_Critical).");
        Assert.That(text, Does.Contain("severity: warning"),
            "palllm.alerts.yaml must use severity: warning for ticket-grade alerts.");

        // Each alert must declare an SLO label so SLO-burn-rate
        // calculations + dashboard filters know which SLO the alert
        // belongs to.
        foreach (string slo in new[] { "availability", "latency", "quality", "bridge" })
        {
            Assert.That(text, Does.Contain($"slo: {slo}"),
                $"palllm.alerts.yaml must label at least one alert with slo: {slo}.");
        }
    }

    [Test]
    public void AlertsYaml_ThresholdsMatchSloContract()
    {
        string text = ReadAlertsYaml();

        // Standard-tier latency budget from docs/HOT_PATH.md is
        // 2500ms p95. The warning alert threshold is 2.5s (in seconds).
        Assert.That(text, Does.Contain("> 2.5"),
            "palllm.alerts.yaml latency-warning threshold must be 2.5s (Standard tier).");
        // Constrained-tier budget is 4000ms. Sustained p95 above this
        // is severely degraded; pages.
        Assert.That(text, Does.Contain("> 4.0"),
            "palllm.alerts.yaml latency-critical threshold must be 4.0s (Constrained tier ceiling).");
        // Fallback share > 30% indicates inference is structurally
        // degraded.
        Assert.That(text, Does.Contain("> 0.30"),
            "palllm.alerts.yaml fallback-rate threshold must be 0.30 (30%).");
        // Bridge backlog > 50 sustained means drain worker is wedged
        // or production rate exceeds drain capacity.
        Assert.That(text, Does.Contain("palllm_inbox_pending_files > 50"),
            "palllm.alerts.yaml bridge-backlog threshold must be 50 files.");
    }

    [Test]
    public void AlertsYaml_EachAlertReferencesValidPrometheusMetric()
    {
        string text = ReadAlertsYaml();

        // Cross-check against the metrics emitted by
        // PalLLM.Domain.Runtime.PrometheusExporter. Drift here would
        // break the alert evaluator silently (rule fires never).
        string[] referencedMetrics = new[]
        {
            "palllm_chat_duration_seconds_bucket",
            "palllm_chat_duration_seconds_count",
            "palllm_fallback_reply_total",
            "palllm_inbox_pending_files",
            "palllm_inference_lane_status",
        };
        foreach (string metric in referencedMetrics)
        {
            Assert.That(text, Does.Contain(metric),
                $"palllm.alerts.yaml must reference '{metric}' — it's emitted by PrometheusExporter and there must be an alert covering it.");
        }
    }

    [Test]
    public void AlertsYaml_EachAlertHasRunbookUrl()
    {
        string text = ReadAlertsYaml();

        // Every alert must carry a runbook_url annotation pointing
        // back to OBSERVABILITY_SLO.md so an operator paging at 3am
        // has somewhere to go without context-switching to the repo.
        Assert.That(text, Does.Contain("OBSERVABILITY_SLO.md"),
            "Every alert must point its runbook_url at docs/OBSERVABILITY_SLO.md.");

        // Count: 6 alerts, 6 runbook_url annotations.
        int runbookCount = System.Text.RegularExpressions.Regex.Matches(text, @"runbook_url:").Count;
        Assert.That(runbookCount, Is.EqualTo(6),
            $"Expected 6 runbook_url annotations (one per alert), found {runbookCount}.");
    }

    // ---------- The Grafana dashboard ----------

    [Test]
    public void GrafanaDashboard_IsValidJson_WithFourSloPanels()
    {
        string text = ReadGrafanaDashboard();
        using JsonDocument doc = JsonDocument.Parse(text);

        Assert.That(doc.RootElement.TryGetProperty("uid", out JsonElement uid), Is.True,
            "Grafana dashboard must declare a uid so runbook deep-links work.");
        Assert.That(uid.GetString(), Is.EqualTo("palllm-slo-overview"),
            "Dashboard uid must be 'palllm-slo-overview' (the SLO doc references this).");

        Assert.That(doc.RootElement.TryGetProperty("panels", out JsonElement panels), Is.True);
        Assert.That(panels.GetArrayLength(), Is.EqualTo(4),
            "Dashboard must have exactly 4 panels — one per SLO category (latency / quality / bridge / inference).");

        // Each panel title must mention a recognized SLI to keep
        // the dashboard semantically clear.
        var titles = new System.Collections.Generic.List<string>();
        foreach (JsonElement panel in panels.EnumerateArray())
        {
            if (panel.TryGetProperty("title", out JsonElement title))
            {
                titles.Add(title.GetString() ?? string.Empty);
            }
        }
        Assert.That(titles.Any(t => t.Contains("latency", System.StringComparison.OrdinalIgnoreCase)),
            "Dashboard must include a latency panel.");
        Assert.That(titles.Any(t => t.Contains("Fallback", System.StringComparison.OrdinalIgnoreCase)),
            "Dashboard must include a fallback-rate panel.");
        Assert.That(titles.Any(t => t.Contains("inbox", System.StringComparison.OrdinalIgnoreCase) || t.Contains("backlog", System.StringComparison.OrdinalIgnoreCase)),
            "Dashboard must include a bridge inbox / backlog panel.");
        Assert.That(titles.Any(t => t.Contains("Inference", System.StringComparison.OrdinalIgnoreCase) && t.Contains("lane", System.StringComparison.OrdinalIgnoreCase)),
            "Dashboard must include an inference-lane-status panel.");
    }

    [Test]
    public void GrafanaDashboard_LatencyPanelCarriesThresholdColours()
    {
        string text = ReadGrafanaDashboard();

        // The latency panel uses threshold-coloured plots so an
        // operator glancing at it can see whether p95 is in budget.
        // Orange = Standard-tier budget (2.5s), red = Constrained-tier
        // ceiling (4.0s). These match the alert thresholds.
        Assert.That(text, Does.Contain("\"value\": 2.5"),
            "Dashboard latency panel must include the 2.5s threshold step (Standard tier).");
        Assert.That(text, Does.Contain("\"value\": 4"),
            "Dashboard latency panel must include the 4.0s threshold step (Constrained tier).");
        Assert.That(text, Does.Contain("\"value\": 0.3"),
            "Dashboard fallback-rate panel must include the 0.30 (30%) threshold step.");
    }

    // ---------- The SLO contract doc ----------

    [Test]
    public void SloDoc_DeclaresThreeSlosWithSliFormulas()
    {
        string text = ReadSloDoc();

        // The three SLO sections are named so AlertManager labels
        // (slo: availability/latency/quality/bridge) line up with
        // the doc sections.
        Assert.That(text, Does.Contain("Availability SLO"),
            "OBSERVABILITY_SLO.md must declare an Availability SLO.");
        Assert.That(text, Does.Contain("Latency SLO"),
            "OBSERVABILITY_SLO.md must declare a Latency SLO.");
        Assert.That(text, Does.Contain("Quality SLO"),
            "OBSERVABILITY_SLO.md must declare a Quality SLO.");

        // Each SLO must include the SLI formula so the contract is
        // executable, not just narrative.
        Assert.That(text, Does.Contain("histogram_quantile"),
            "OBSERVABILITY_SLO.md must include the histogram_quantile PromQL formula for the latency SLI.");
        Assert.That(text, Does.Contain("palllm_fallback_reply_total"),
            "OBSERVABILITY_SLO.md must include the fallback-rate PromQL formula.");

        // Numeric targets must match the alert thresholds.
        Assert.That(text, Does.Contain("99.5%"),
            "Availability SLO target must be 99.5%.");
        Assert.That(text, Does.Contain("30%"),
            "Quality SLO threshold must be 30% (matches alert).");
        Assert.That(text, Does.Contain("2500 ms").Or.Contain("2.5"),
            "Latency SLO threshold must be 2500 ms / 2.5s (Standard tier).");
    }

    [Test]
    public void SloDoc_LinksToAlertsYaml_AndDashboardJson()
    {
        string text = ReadSloDoc();

        // The SLO doc is the operator's entry point. It must link
        // to both shipping artifacts so they're discoverable.
        Assert.That(text, Does.Contain("palllm.alerts.yaml"),
            "OBSERVABILITY_SLO.md must link to the alert rules file.");
        Assert.That(text, Does.Contain("palllm-grafana-dashboard.json"),
            "OBSERVABILITY_SLO.md must link to the Grafana dashboard JSON.");

        // Import recipes must be concrete (operator can copy-paste).
        Assert.That(text, Does.Contain("/etc/prometheus/rules/").Or.Contain("rule_files"),
            "OBSERVABILITY_SLO.md must show the Prometheus import recipe.");
        Assert.That(text, Does.Contain("Upload JSON"),
            "OBSERVABILITY_SLO.md must show the Grafana import recipe.");
    }

    [Test]
    public void SloDoc_HasRunbookSectionPerAlert()
    {
        string text = ReadSloDoc();

        // Each alert's runbook_url annotation deep-links into this
        // doc. Every alert must have its target section.
        Assert.That(text, Does.Contain("{#palllmservicedown}"),
            "OBSERVABILITY_SLO.md must include the runbook anchor for PalLLMServiceDown.");
        Assert.That(text, Does.Contain("{#palllmchatlatencyhigh}"),
            "OBSERVABILITY_SLO.md must include the runbook anchor for PalLLMChatLatencyHigh.");
        Assert.That(text, Does.Contain("{#palllmfallbackratehigh}"),
            "OBSERVABILITY_SLO.md must include the runbook anchor for PalLLMFallbackRateHigh.");
        Assert.That(text, Does.Contain("{#palllminboxbacklog}"),
            "OBSERVABILITY_SLO.md must include the runbook anchor for PalLLMInboxBacklogGrowing.");
        Assert.That(text, Does.Contain("{#palllminferencelane}"),
            "OBSERVABILITY_SLO.md must include the runbook anchor for PalLLMInferenceLaneRed.");
    }

    // ---------- Helpers ----------

    private static string ReadAlertsYaml() =>
        File.ReadAllText(LocateRepoFile("scripts", "observability", "palllm.alerts.yaml"));

    private static string ReadGrafanaDashboard() =>
        File.ReadAllText(LocateRepoFile("scripts", "observability", "palllm-grafana-dashboard.json"));

    private static string ReadSloDoc() =>
        File.ReadAllText(LocateRepoFile("docs", "OBSERVABILITY_SLO.md"));

    private static string LocateRepoFile(params string[] segments)
    {
        string testBin = TestContext.CurrentContext.TestDirectory;
        DirectoryInfo? current = new(testBin);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PalLLM.sln")))
            {
                string candidate = Path.Combine(new[] { current.FullName }.Concat(segments).ToArray());
                if (File.Exists(candidate))
                {
                    return candidate;
                }
                throw new FileNotFoundException(
                    $"Could not locate {string.Join(Path.DirectorySeparatorChar, segments)} under the repo root at {current.FullName}.");
            }
            current = current.Parent;
        }
        throw new FileNotFoundException("Could not locate the repo root (no PalLLM.sln found).");
    }
}
