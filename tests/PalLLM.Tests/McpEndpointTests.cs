using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PalLLM.Domain.Configuration;

namespace PalLLM.Tests;

public sealed class McpEndpointTests
{
    private const string McpAcceptHeader = "application/json, text/event-stream";

    // ---------------------------------------------------------------------
    // MCP lifecycle + discovery
    // ---------------------------------------------------------------------

    [Test]
    public async Task McpEndpoint_Initialize_NegotiatesSpecProtocolVersionAndDeclaresToolsCapability()
    {
        // Every MCP client starts by calling `initialize` to negotiate the
        // protocol version and discover server capabilities. This test pins
        // the three contract guarantees: protocol version matches the spec,
        // the server identifies itself, and the `tools` capability is
        // declared so clients know `tools/list` and `tools/call` are usable.
        await using var fixture = new SidecarTestFixture();

        string requestJson = BuildInitializeRequest(id: 1);
        JsonDocument response = await PostMcpAsync(fixture.Client, requestJson);

        JsonElement result = response.RootElement.GetProperty("result");
        Assert.That(
            result.GetProperty("protocolVersion").GetString(),
            Does.StartWith("2025-"),
            "Protocol version must be a 2025-series spec release (currently 2025-06-18).");

        Assert.That(
            result.GetProperty("capabilities").TryGetProperty("tools", out _), Is.True,
            "Server must advertise the tools capability so MCP clients know tools/list and tools/call are available.");

        Assert.That(
            result.GetProperty("serverInfo").GetProperty("name").GetString(),
            Is.Not.Null.And.Not.Empty,
            "Server must identify itself so debugging + compatibility tracking works.");
    }

    [Test]
    public async Task McpEndpoint_ToolsList_ReturnsEveryPalLlmMcpTool()
    {
        // Pin every MCP tool we expose so an accidental drop of
        // [McpServerTool] is caught before shipping. If you add a new
        // PalLlmMcpTools method, add its name here.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        JsonElement tools = response.RootElement.GetProperty("result").GetProperty("tools");
        HashSet<string> toolNames = new(StringComparer.Ordinal);
        foreach (JsonElement tool in tools.EnumerateArray())
        {
            toolNames.Add(tool.GetProperty("name").GetString() ?? string.Empty);
        }

        string[] expected =
        [
            "pal_world_snapshot",
            "pal_scene_description",
            "pal_chat",
            "pal_recall_memory",
            "pal_list_characters",
            "pal_list_features",
            "pal_list_recent_bridge_events",
            "pal_active_model_tier",
            "pal_model_collaboration",
            "pal_plan_model_collaboration_task",
            "pal_list_upstream_mcp",
            "pal_describe",
            "pal_quickstart",
            "pal_status",
            "pal_health_suggestions",
            "pal_health_score",
            "pal_airgap_verify",
            "pal_self_healing_status",
            "pal_why",
            "pal_model_roles",
            "pal_duo_plan",
            "pal_disagreement_check",
            "pal_proof_packet",
            "pal_promotion_summary",
            "pal_promotion_record",
            "pal_promotion_suggestions",
            "pal_promotion_apply_preview",
            "pal_chat_plan",
        ];

        foreach (string name in expected)
        {
            Assert.That(toolNames, Contains.Item(name),
                $"MCP tool '{name}' must be discoverable. If you renamed or removed it, update this list.");
        }
    }

    [Test]
    public async Task McpEndpoint_ToolsList_EveryToolHasDescriptionAndInputSchema()
    {
        // MCP clients auto-generate UI from each tool's description +
        // inputSchema. A tool without either is useless — the MCP host has
        // nothing to show the user and can't validate arguments.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""");

        foreach (JsonElement tool in response.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray())
        {
            string name = tool.GetProperty("name").GetString() ?? "<anon>";
            Assert.That(
                string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), Is.False,
                $"Tool '{name}' must have a non-empty description for MCP host UI.");
            Assert.That(
                tool.GetProperty("inputSchema").GetProperty("type").GetString(),
                Is.EqualTo("object"),
                $"Tool '{name}' must declare inputSchema as JSON Schema type object.");
        }
    }

    // ---------------------------------------------------------------------
    // tools/call — exercise the actual runtime
    // ---------------------------------------------------------------------

    [Test]
    public async Task McpEndpoint_ToolsCall_ListFeatures_ReturnsCatalog()
    {
        // pal_list_features is a pure-read tool with no parameters, so this
        // is the cheapest end-to-end test that a tool call actually
        // executes the DI-wired method on the sidecar.
        await using var fixture = new SidecarTestFixture();

        string request = """
            {
              "jsonrpc":"2.0",
              "id":4,
              "method":"tools/call",
              "params":{"name":"pal_list_features","arguments":{}}
            }
            """;

        JsonDocument response = await PostMcpAsync(fixture.Client, request);

        JsonElement content = response.RootElement
            .GetProperty("result")
            .GetProperty("content");
        Assert.That(content.GetArrayLength(), Is.GreaterThan(0),
            "tools/call must always return at least one content block.");

        string text = content[0].GetProperty("text").GetString() ?? string.Empty;
        Assert.That(text, Does.Contain("portable-adapter-surface"),
            "Feature catalog must come through the MCP pipeline untouched.");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_Describe_ReturnsSelfDescriptionManifest()
    {
        // pal_describe mirrors GET /api/describe so AI clients get the same
        // self-description manifest without an HTTP round-trip.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":20,
              "method":"tools/call",
              "params":{"name":"pal_describe","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        Assert.That(text, Does.Contain("\"Product\":\"PalLLM\""));
        Assert.That(text, Does.Contain("\"License\":\"MIT\""));
        Assert.That(text, Does.Contain("\"OperatorHealth\":"),
            "Self-description must carry the operator-health roll-up so AI callers see a single-number signal.");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_Quickstart_ReturnsStateAwareSteps()
    {
        // pal_quickstart returns the same live next-step guidance as GET /api/quickstart.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":21,
              "method":"tools/call",
              "params":{"name":"pal_quickstart","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.TryGetProperty("OverallStatus", out JsonElement status), Is.True);
        string statusValue = status.GetString() ?? string.Empty;
        Assert.That(statusValue, Is.AnyOf("ready", "needs-setup", "needs-attention"));
        Assert.That(doc.RootElement.TryGetProperty("Steps", out _), Is.True);
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_HealthSuggestions_ReturnsActionableArray()
    {
        // pal_health_suggestions exposes the live operator-actionable hints
        // computed by HealthSuggestionBuilder. The test fixture starts with
        // Bridge enabled (booted via fixture init) and Inference disabled, so
        // the builder may emit zero or more entries depending on the test
        // environment's bridge boot count + pack dir state. The contract we
        // pin here: the response is a JSON ARRAY whose entries (if any) all
        // declare a non-empty `Code` and `Message` so dashboard / pal-next
        // consumers can rely on those fields without null-guarding.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":42,
              "method":"tools/call",
              "params":{"name":"pal_health_suggestions","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.ValueKind, Is.EqualTo(JsonValueKind.Array),
            "pal_health_suggestions must return a JSON array (possibly empty).");

        foreach (JsonElement entry in doc.RootElement.EnumerateArray())
        {
            Assert.That(entry.TryGetProperty("Code", out JsonElement code), Is.True,
                "Every suggestion must carry a Code so consumers can match programmatically.");
            Assert.That(string.IsNullOrWhiteSpace(code.GetString()), Is.False);
            Assert.That(entry.TryGetProperty("Message", out JsonElement message), Is.True);
            Assert.That(string.IsNullOrWhiteSpace(message.GetString()), Is.False);
        }
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_Status_ReturnsCompactSummary()
    {
        // pal_status is the agent-facing one-shot summary that pairs with
        // the CLI `pal status` verb. The contract: a single object carrying
        // numeric Score, Grade in the four allowed bands, suggestion counts
        // by severity, the top suggestion code (or null when healthy), and
        // headline configuration flags an agent can quote without parsing
        // the full RuntimeHealth.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":44,
              "method":"tools/call",
              "params":{"name":"pal_status","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.TryGetProperty("Score", out JsonElement score), Is.True,
            "pal_status must carry a numeric Score field.");
        Assert.That(score.GetInt32(), Is.InRange(0, 100));

        Assert.That(doc.RootElement.TryGetProperty("Grade", out JsonElement grade), Is.True);
        Assert.That(grade.GetString(), Is.AnyOf("Excellent", "Good", "Degraded", "Critical"));

        // Severity-bucket counts must sum to total -- catches a future
        // refactor that adds a fourth severity but forgets the bucket.
        Assert.That(doc.RootElement.TryGetProperty("SuggestionsTotal", out JsonElement total), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("SuggestionsUrgent", out JsonElement u), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("SuggestionsWarn", out JsonElement w), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("SuggestionsInfo", out JsonElement i), Is.True);
        Assert.That(u.GetInt32() + w.GetInt32() + i.GetInt32(), Is.EqualTo(total.GetInt32()),
            "Per-severity counts must sum to SuggestionsTotal.");

        // Configuration flags every agent will want for routing decisions.
        Assert.That(doc.RootElement.TryGetProperty("InferenceConfigured", out _), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("VisionEnabled", out _), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("BridgeEnabled", out _), Is.True);
        Assert.That(doc.RootElement.TryGetProperty("LoadedPackCount", out _), Is.True);

        // TopSuggestionCode is null when the runtime is healthy; the
        // property must always be present so consumers don't need to
        // null-guard the missing-key case.
        Assert.That(doc.RootElement.TryGetProperty("TopSuggestionCode", out _), Is.True);
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_HealthScore_ReturnsScoreAndGrade()
    {
        // pal_health_score wraps OperatorHealthScorer for agent consumption.
        // The contract: a 0-100 numeric Score, a Grade matching the four
        // bands (Excellent / Good / Degraded / Critical), a non-empty
        // Summary string, and a TopReasons array. The default test fixture
        // ships with inference disabled so the score will be slightly off
        // 100 (bridge-disabled or status-degraded penalties may apply); the
        // contract asserted here is the SHAPE, not a specific value.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":43,
              "method":"tools/call",
              "params":{"name":"pal_health_score","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.TryGetProperty("Score", out JsonElement scoreElem), Is.True,
            "pal_health_score must carry a numeric Score field.");
        int score = scoreElem.GetInt32();
        Assert.That(score, Is.InRange(0, 100), "Score must be 0-100.");

        Assert.That(doc.RootElement.TryGetProperty("Grade", out JsonElement gradeElem), Is.True);
        string grade = gradeElem.GetString() ?? string.Empty;
        Assert.That(grade, Is.AnyOf("Excellent", "Good", "Degraded", "Critical"));

        Assert.That(doc.RootElement.TryGetProperty("Summary", out JsonElement summaryElem), Is.True);
        Assert.That(string.IsNullOrWhiteSpace(summaryElem.GetString()), Is.False);

        Assert.That(doc.RootElement.TryGetProperty("TopReasons", out _), Is.True,
            "TopReasons array must be present so consumers can render the score's drivers without re-deriving them.");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_AirgapVerify_ClassifiesOutboundSurfaces()
    {
        // pal_airgap_verify mirrors GET /api/airgap/verify so AI callers can
        // prove the instance makes no outbound calls off this machine.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":22,
              "method":"tools/call",
              "params":{"name":"pal_airgap_verify","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument doc = JsonDocument.Parse(text);
        Assert.That(doc.RootElement.TryGetProperty("Verdict", out JsonElement verdict), Is.True);
        string verdictValue = verdict.GetString() ?? string.Empty;
        Assert.That(verdictValue, Is.AnyOf("strict-airgapped", "lan-airgapped", "not-airgapped", "indeterminate"));
        Assert.That(doc.RootElement.TryGetProperty("Surfaces", out _), Is.True);
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_SelfHealingStatus_ReportsPendingOrLatest()
    {
        // On a fresh fixture the worker likely has not ticked yet; in that
        // case we expect the structured pending-marker payload. If a tick
        // has landed, the returned JSON carries CapturedAtUtc.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":23,
              "method":"tools/call",
              "params":{"name":"pal_self_healing_status","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument doc = JsonDocument.Parse(text);
        // Either the worker hasn't ticked yet (status=pending) or we see
        // a real evidence shape (CapturedAtUtc).
        bool hasCapturedAt = doc.RootElement.TryGetProperty("CapturedAtUtc", out _);
        bool hasPendingMarker = doc.RootElement.TryGetProperty("status", out JsonElement s)
            && (s.GetString() == "pending" || s.GetString() == "unreadable");
        Assert.That(hasCapturedAt || hasPendingMarker, Is.True,
            "pal_self_healing_status must return either a real evidence payload or a structured pending marker.");

        using (IServiceScope scope = fixture.Factory.Services.CreateScope())
        {
            PalLlmOptions options = scope.ServiceProvider.GetRequiredService<PalLlmOptions>();
            string evidencePath = Path.Combine(options.RuntimeRoot, "SelfHealingEvidence", "latest-self-healing.json");
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            await File.WriteAllTextAsync(
                evidencePath,
                new string('{', options.Http.LocalArtifactMaxBytes + 64));
        }

        JsonDocument oversizedResponse = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":24,
              "method":"tools/call",
              "params":{"name":"pal_self_healing_status","arguments":{}}
            }
            """);

        string oversizedText = oversizedResponse.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument oversizedDoc = JsonDocument.Parse(oversizedText);
        Assert.That(oversizedDoc.RootElement.GetProperty("status").GetString(), Is.EqualTo("unreadable"));
        Assert.That(oversizedDoc.RootElement.GetProperty("detail").GetString(), Does.Contain("configured size limit"));
        Assert.That(oversizedDoc.RootElement.GetProperty("detail").GetString(), Does.Not.Contain("System."));
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_Why_ReturnsStructuredCausalAnswer()
    {
        // pal_why wraps WhyEngine.Answer so natural-language causal
        // questions always resolve to a structured deterministic answer.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":24,
              "method":"tools/call",
              "params":{"name":"pal_why","arguments":{"question":"why did my reply come from the fallback?"}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument doc = JsonDocument.Parse(text);
        JsonElement root = doc.RootElement;

        Assert.That(root.TryGetProperty("Intent", out JsonElement intent), Is.True);
        Assert.That(intent.GetString(), Is.EqualTo("FallbackTriggered"));
        Assert.That(root.TryGetProperty("PrimaryReason", out _), Is.True);
        Assert.That(root.TryGetProperty("CausalChain", out JsonElement chain), Is.True);
        Assert.That(chain.GetArrayLength(), Is.GreaterThan(0));
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_SceneDescription_UsesSnapshotFallbackWhenWorldNotLoaded()
    {
        // The scene-description tool depends on SnapshotVisionFallback,
        // which returns a sentinel when no world is loaded. Verify the MCP
        // path surfaces that sentinel so clients get a meaningful response
        // instead of a silent empty string.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":5,
              "method":"tools/call",
              "params":{"name":"pal_scene_description","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
        Assert.That(text, Does.Contain("No world loaded"));
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_ActiveModelTier_ReturnsStructuredJson()
    {
        await using var fixture = new SidecarTestFixture();

        string request = """
            {
              "jsonrpc":"2.0",
              "id":6,
              "method":"tools/call",
              "params":{"name":"pal_active_model_tier","arguments":{}}
            }
            """;

        JsonDocument response = await PostMcpAsync(fixture.Client, request);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument body = JsonDocument.Parse(text);
        Assert.That(body.RootElement.TryGetProperty("ActiveModel", out _), Is.True);
        Assert.That(body.RootElement.TryGetProperty("ConfiguredTiers", out JsonElement configuredTiers), Is.True);
        Assert.That(configuredTiers.ValueKind, Is.EqualTo(JsonValueKind.Array));
        Assert.That(body.RootElement.TryGetProperty("Warmup", out JsonElement warmup), Is.True);
        Assert.That(warmup.TryGetProperty("Status", out _), Is.True);
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_ModelCollaboration_ReturnsStructuredJson()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Inference:ModelTiers:0:Id"] = "worker",
            ["PalLLM:Inference:ModelTiers:0:Model"] = "unsloth/Qwen3.6-35B-A3B-GGUF",
            ["PalLLM:Inference:ModelTiers:0:Priority"] = "10",
            ["PalLLM:Inference:ModelTiers:1:Id"] = "judge",
            ["PalLLM:Inference:ModelTiers:1:Model"] = "unsloth/Qwen3.6-27B-GGUF",
            ["PalLLM:Inference:ModelTiers:1:Priority"] = "9",
        });

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":7,
              "method":"tools/call",
              "params":{"name":"pal_model_collaboration","arguments":{}}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        Assert.That(text.StartsWith("{", StringComparison.Ordinal), Is.True, text);
        using JsonDocument body = JsonDocument.Parse(text);
        Assert.That(body.RootElement.GetProperty("Hardware").GetProperty("ClassId").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(body.RootElement.GetProperty("Recipes")[0].GetProperty("Id").GetString(), Is.Not.Null.And.Not.Empty);
        Assert.That(body.RootElement.GetProperty("RoutingPolicies")[0].GetProperty("Id").GetString(), Is.EqualTo("low-risk-fast-lane"));
        Assert.That(body.RootElement.GetProperty("QualificationSuite").GetProperty("Checks")[0].GetProperty("Id").GetString(), Is.EqualTo("exact-json-tool-call"));
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_ModelCollaborationTaskPlanner_ReturnsExecutionPlan()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Inference:ModelTiers:0:Id"] = "worker",
            ["PalLLM:Inference:ModelTiers:0:Model"] = "unsloth/Qwen3.6-35B-A3B-GGUF",
            ["PalLLM:Inference:ModelTiers:0:Priority"] = "10",
            ["PalLLM:Inference:ModelTiers:1:Id"] = "judge",
            ["PalLLM:Inference:ModelTiers:1:Model"] = "unsloth/Qwen3.6-27B-GGUF",
            ["PalLLM:Inference:ModelTiers:1:Priority"] = "9",
        });

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":70,
              "method":"tools/call",
              "params":{
                "name":"pal_plan_model_collaboration_task",
                "arguments":{
                  "task":"Plan a release-facing auth migration with tool-driven edits",
                  "taskClass":"coding",
                  "riskLevel":"high",
                  "toolHeavy":true,
                  "releaseGate":true,
                  "vramGb":48,
                  "ramGb":128,
                  "preferParallel":true
                }
              }
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        using JsonDocument body = JsonDocument.Parse(text);
        Assert.That(body.RootElement.GetProperty("SelectedPolicyId").GetString(), Is.EqualTo("high-risk-deliberate-bookends"));
        Assert.That(body.RootElement.GetProperty("RunMode").GetString(), Is.EqualTo("parallel"));
        Assert.That(body.RootElement.GetProperty("FastLaneModel").GetString(), Does.Contain("35B-A3B"));
        Assert.That(body.RootElement.GetProperty("DeliberateLaneModel").GetString(), Does.Contain("27B"));
        Assert.That(body.RootElement.GetProperty("Validators")[0].GetString(), Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task McpEndpoint_Auth_WhenApiKeyConfigured_RequiresBearerOnMcpToo()
    {
        // The /mcp endpoint must honor the same auth posture as /api/*.
        // If an operator sets PalLLM:Auth:ApiKey, unauthenticated MCP
        // clients must get 401 — otherwise MCP would be a back door.
        var extraConfig = new Dictionary<string, string?>
        {
            ["PalLLM:Auth:ApiKey"] = "mcp-test-key",
        };
        await using var fixture = new SidecarTestFixture(extraConfig);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                BuildInitializeRequest(id: 1),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Accept.ParseAdd(McpAcceptHeader);

        using HttpResponseMessage unauthResponse = await fixture.Client.SendAsync(request);
        Assert.That(unauthResponse.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(unauthResponse.Headers.WwwAuthenticate.ToString(), Does.Contain("Bearer"));
        Assert.That(unauthResponse.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));

        using var authedRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(
                BuildInitializeRequest(id: 1),
                Encoding.UTF8,
                "application/json"),
        };
        authedRequest.Headers.Accept.ParseAdd(McpAcceptHeader);
        authedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "mcp-test-key");

        using HttpResponseMessage authedResponse = await fixture.Client.SendAsync(authedRequest);
        Assert.That(authedResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task McpEndpoint_WhenOriginHeaderIsRemote_Returns403ProblemDetails()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpRequestMessage request = CreateMcpRequest(BuildInitializeRequest(id: 1), origin: "https://evil.example");
        using HttpResponseMessage response = await fixture.Client.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("application/problem+json"));
        Assert.That(body, Does.Contain("Forbidden Origin"));
        Assert.That(body, Does.Contain("traceId"));
    }

    [Test]
    public async Task McpEndpoint_WhenOriginHeaderIsLoopback_AllowsRequest()
    {
        await using var fixture = new SidecarTestFixture();

        using HttpRequestMessage request = CreateMcpRequest(BuildInitializeRequest(id: 1), origin: "http://localhost:3000");
        using HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task McpEndpoint_WhenOriginHeaderMatchesConfiguredAllowlist_AllowsRequest()
    {
        var extraConfig = new Dictionary<string, string?>
        {
            ["PalLLM:Auth:McpAllowedOrigins:0"] = "https://ops.example.com",
        };
        await using var fixture = new SidecarTestFixture(extraConfig);

        using HttpRequestMessage request = CreateMcpRequest(BuildInitializeRequest(id: 1), origin: "https://ops.example.com");
        using HttpResponseMessage response = await fixture.Client.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    // ---------------------------------------------------------------------
    // Resources
    // ---------------------------------------------------------------------

    [Test]
    public async Task McpEndpoint_ResourcesList_ReturnsEveryPalLlmDirectResource()
    {
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """{"jsonrpc":"2.0","id":10,"method":"resources/list"}""");

        HashSet<string> uris = new(StringComparer.Ordinal);
        foreach (JsonElement resource in response.RootElement.GetProperty("result").GetProperty("resources").EnumerateArray())
        {
            uris.Add(resource.GetProperty("uri").GetString() ?? string.Empty);
        }

        string[] expected =
        [
            "palllm://world/snapshot",
            "palllm://features",
            "palllm://runtime/health",
            "palllm://characters",
            "palllm://model/tier/active",
            "palllm://model/collaboration",
        ];
        foreach (string uri in expected)
        {
            Assert.That(uris, Contains.Item(uri),
                $"Resource '{uri}' must be discoverable via resources/list.");
        }
    }

    [Test]
    public async Task McpEndpoint_ResourcesTemplatesList_IncludesPerCharacterTemplate()
    {
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """{"jsonrpc":"2.0","id":11,"method":"resources/templates/list"}""");

        JsonElement templates = response.RootElement.GetProperty("result").GetProperty("resourceTemplates");
        bool found = false;
        foreach (JsonElement template in templates.EnumerateArray())
        {
            if (template.GetProperty("uriTemplate").GetString() == "palllm://character/{characterId}")
            {
                found = true;
                break;
            }
        }
        Assert.That(found, Is.True,
            "Resource template palllm://character/{characterId} must be discoverable via resources/templates/list.");
    }

    [Test]
    public async Task McpEndpoint_ResourcesRead_WorldSnapshot_ReturnsJsonBody()
    {
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":12,
              "method":"resources/read",
              "params":{"uri":"palllm://world/snapshot"}
            }
            """);

        JsonElement contents = response.RootElement.GetProperty("result").GetProperty("contents");
        Assert.That(contents.GetArrayLength(), Is.GreaterThan(0),
            "resources/read must return at least one content entry.");

        JsonElement firstEntry = contents[0];
        Assert.That(firstEntry.GetProperty("uri").GetString(),
            Is.EqualTo("palllm://world/snapshot"));

        // The body is a JSON-serialised world snapshot — verify it parses.
        string text = firstEntry.GetProperty("text").GetString() ?? string.Empty;
        using JsonDocument body = JsonDocument.Parse(text);
        Assert.That(body.RootElement.TryGetProperty("IsWorldLoaded", out _), Is.True,
            "Snapshot resource body must be a serialised world snapshot.");
    }

    [Test]
    public async Task McpEndpoint_ResourcesRead_RuntimeHealth_ReturnsJsonBody()
    {
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":13,
              "method":"resources/read",
              "params":{"uri":"palllm://runtime/health"}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("contents")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
        using JsonDocument body = JsonDocument.Parse(text);
        Assert.That(body.RootElement.TryGetProperty("Status", out _), Is.True);
        Assert.That(body.RootElement.TryGetProperty("BridgeEnabled", out _), Is.True);
        Assert.That(body.RootElement.TryGetProperty("NativeReadiness", out JsonElement nativeReadiness), Is.True);
        Assert.That(nativeReadiness.TryGetProperty("BridgeBootSeen", out _), Is.True);
    }

    [Test]
    public async Task McpEndpoint_ResourcesRead_ModelCollaboration_ReturnsJsonBody()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Inference:ModelTiers:0:Id"] = "worker",
            ["PalLLM:Inference:ModelTiers:0:Model"] = "unsloth/Qwen3.6-35B-A3B-GGUF",
            ["PalLLM:Inference:ModelTiers:0:Priority"] = "10",
            ["PalLLM:Inference:ModelTiers:1:Id"] = "judge",
            ["PalLLM:Inference:ModelTiers:1:Model"] = "unsloth/Qwen3.6-27B-GGUF",
            ["PalLLM:Inference:ModelTiers:1:Priority"] = "9",
        });

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":14,
              "method":"resources/read",
              "params":{"uri":"palllm://model/collaboration"}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("contents")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
        using JsonDocument body = JsonDocument.Parse(text);
        Assert.That(body.RootElement.TryGetProperty("ConfiguredModels", out _), Is.True);
        Assert.That(body.RootElement.TryGetProperty("Recipes", out _), Is.True);
    }

    [Test]
    public async Task McpEndpoint_ResourcesRead_CharacterTemplate_HandlesMissingIdGracefully()
    {
        // Template URIs must work even when the id doesn't match — the
        // resource method returns a structured 'not-found' sentinel
        // rather than throwing, so MCP hosts show a graceful error.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":13,
              "method":"resources/read",
              "params":{"uri":"palllm://character/99999"}
            }
            """);

        string text = response.RootElement
            .GetProperty("result")
            .GetProperty("contents")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
        Assert.That(text, Does.Contain("not found"));
    }

    // ---------------------------------------------------------------------
    // Prompts
    // ---------------------------------------------------------------------

    [Test]
    public async Task McpEndpoint_PromptsList_ReturnsEveryPalLlmPrompt()
    {
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """{"jsonrpc":"2.0","id":20,"method":"prompts/list"}""");

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonElement prompt in response.RootElement.GetProperty("result").GetProperty("prompts").EnumerateArray())
        {
            names.Add(prompt.GetProperty("name").GetString() ?? string.Empty);
        }

        string[] expected =
        [
            "palllm_companion_chat",
            "palllm_threat_analysis",
            "palllm_base_status",
            "palllm_model_collaboration_orchestrator",
        ];
        foreach (string name in expected)
        {
            Assert.That(names, Contains.Item(name),
                $"Prompt '{name}' must be discoverable via prompts/list.");
        }
    }

    [Test]
    public async Task McpEndpoint_PromptsGet_CompanionChat_ReturnsSystemAndUserMessages()
    {
        // prompts/get must return a system message (context + character)
        // plus a user message (what the player said), so the host injects
        // both into the conversation on invocation.
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":21,
              "method":"prompts/get",
              "params":{
                "name":"palllm_companion_chat",
                "arguments":{"userMessage":"what's next?"}
              }
            }
            """);

        JsonElement messages = response.RootElement.GetProperty("result").GetProperty("messages");
        Assert.That(messages.GetArrayLength(), Is.EqualTo(2),
            "Companion-chat prompt must return context + user messages.");

        List<string> roles = new();
        foreach (JsonElement message in messages.EnumerateArray())
        {
            roles.Add(message.GetProperty("role").GetString() ?? string.Empty);
        }
        // MCP spec allows only user/assistant roles. PalLLM encodes the
        // scene-setting context as an assistant opening turn, then the
        // actual player question as the user turn.
        Assert.That(roles, Is.EqualTo(new[] { "assistant", "user" }));

        string userContent = messages[1].GetProperty("content").GetProperty("text").GetString() ?? string.Empty;
        Assert.That(userContent, Does.Contain("what's next?"),
            "User message must echo the supplied userMessage argument.");
    }

    [Test]
    public async Task McpEndpoint_PromptsGet_ModelCollaborationOrchestrator_ReturnsJsonScaffoldPrompt()
    {
        await using var fixture = new SidecarTestFixture(new Dictionary<string, string?>
        {
            ["PalLLM:Inference:ModelTiers:0:Id"] = "worker",
            ["PalLLM:Inference:ModelTiers:0:Model"] = "unsloth/Qwen3.6-35B-A3B-GGUF",
            ["PalLLM:Inference:ModelTiers:0:Priority"] = "10",
            ["PalLLM:Inference:ModelTiers:1:Id"] = "judge",
            ["PalLLM:Inference:ModelTiers:1:Model"] = "unsloth/Qwen3.6-27B-GGUF",
            ["PalLLM:Inference:ModelTiers:1:Priority"] = "9",
        });

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":22,
              "method":"prompts/get",
              "params":{
                "name":"palllm_model_collaboration_orchestrator",
                "arguments":{
                  "task":"Design the safest local duo workflow for a medium-risk refactor.",
                  "hardware":"RTX 4090 24GB + 128GB RAM"
                }
              }
            }
            """);

        JsonElement messages = response.RootElement.GetProperty("result").GetProperty("messages");
        Assert.That(messages.GetArrayLength(), Is.EqualTo(2));

        string assistantContent = messages[0].GetProperty("content").GetProperty("text").GetString() ?? string.Empty;
        string userContent = messages[1].GetProperty("content").GetProperty("text").GetString() ?? string.Empty;

        Assert.That(assistantContent, Does.Contain("Return only JSON using this shape"));
        Assert.That(assistantContent, Does.Contain("\"strategy\""));
        Assert.That(assistantContent, Does.Contain("Hardware playbook"));
        Assert.That(userContent, Does.Contain("RTX 4090 24GB + 128GB RAM"));
        Assert.That(userContent, Does.Contain("medium-risk refactor"));
    }

    [Test]
    public async Task McpEndpoint_PromptsGet_ThreatAnalysis_IncludesSnapshotContextInSystemMessage()
    {
        await using var fixture = new SidecarTestFixture();

        JsonDocument response = await PostMcpAsync(
            fixture.Client,
            """
            {
              "jsonrpc":"2.0",
              "id":22,
              "method":"prompts/get",
              "params":{"name":"palllm_threat_analysis"}
            }
            """);

        JsonElement messages = response.RootElement.GetProperty("result").GetProperty("messages");
        string systemContent = messages[0].GetProperty("content").GetProperty("text").GetString() ?? string.Empty;
        Assert.That(systemContent, Does.Contain("tactical advisor"),
            "Threat-analysis prompt must frame the role correctly.");
        Assert.That(systemContent, Does.Contain("Threat level"),
            "Threat-analysis prompt must inject the live threat level from the snapshot.");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static string BuildInitializeRequest(int id) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method = "initialize",
            @params = new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "pal-llm-test", version = "1.0" },
            },
        });

    // ---------------------------------------------------------------------
    // tools/call — diagnostic / posture coverage (Pass 178)
    //
    // Nine tools with no dedicated test pinning their tools/call shape:
    // pal_degradation_advisory, pal_directives_plan, pal_hardware_profile,
    // pal_mood_weather, pal_narration_cue, pal_privacy_posture,
    // pal_resource_budgets, pal_vision_describe, pal_promotion_apply.
    //
    // Each test verifies the MCP pipeline runs the tool, returns at least
    // one content block, and the content body is non-empty. This is the
    // never-fails-loudly invariant for read-only diagnostic tools — any
    // future regression that breaks a tool's MCP shape fails here loudly
    // with the tool name. pal_promotion_apply is exercised in dry-run
    // mode (the default) so it never mutates state.
    // ---------------------------------------------------------------------

    [Test]
    public async Task McpEndpoint_ToolsCall_DegradationAdvisory_ReturnsContent()
    {
        await AssertReadOnlyToolReturnsContentAsync("pal_degradation_advisory");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_HardwareProfile_ReturnsContent()
    {
        await AssertReadOnlyToolReturnsContentAsync("pal_hardware_profile");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_MoodWeather_ReturnsContent()
    {
        // pal_mood_weather expects a characterId; default 1 is the
        // canonical fixture character.
        await AssertReadOnlyToolReturnsContentAsync(
            "pal_mood_weather",
            arguments: """{"characterId":1}""");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_NarrationCue_ReturnsContent()
    {
        await AssertReadOnlyToolReturnsContentAsync("pal_narration_cue");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_PrivacyPosture_ReturnsContent()
    {
        await AssertReadOnlyToolReturnsContentAsync("pal_privacy_posture");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_ResourceBudgets_ReturnsContent()
    {
        await AssertReadOnlyToolReturnsContentAsync("pal_resource_budgets");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_VisionDescribe_ReturnsContent()
    {
        // pal_vision_describe runs even with vision disabled — it returns a
        // structured "vision-disabled" sentinel rather than throwing.
        await AssertReadOnlyToolReturnsContentAsync(
            "pal_vision_describe",
            arguments: """{"prompt":"describe a sunny biome"}""");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_DirectivesPlan_ReturnsContent()
    {
        // pal_directives_plan translates a player utterance into a
        // PalDirective[] plan. Deterministic, no inference call.
        await AssertReadOnlyToolReturnsContentAsync(
            "pal_directives_plan",
            arguments: """{"utterance":"recall my pals"}""");
    }

    [Test]
    public async Task McpEndpoint_ToolsCall_PromotionApply_ReturnsContent()
    {
        // pal_promotion_apply with no observed task class returns the
        // structured "task-not-observed" / "not-yet-candidate" rejection
        // shape. By default `PalLLM:PromotionApply:AllowApply` is false,
        // so the tool also returns the structured "disabled" sentinel —
        // both are valid responses for this contract test.
        await AssertReadOnlyToolReturnsContentAsync(
            "pal_promotion_apply",
            arguments: """{"taskClass":"unobserved-class"}""");
    }

    /// <summary>
    /// Helper — call any read-only MCP tool and assert the response has
    /// the canonical <c>{ result: { content: [...] } }</c> shape with a
    /// non-empty first content block. Used for the diagnostic tool
    /// coverage tests above.
    /// </summary>
    private static async Task AssertReadOnlyToolReturnsContentAsync(
        string toolName,
        string arguments = "{}")
    {
        await using var fixture = new SidecarTestFixture();

        string request = $$"""
            {
              "jsonrpc":"2.0",
              "id":42,
              "method":"tools/call",
              "params":{"name":"{{toolName}}","arguments":{{arguments}}}
            }
            """;

        JsonDocument response = await PostMcpAsync(fixture.Client, request);

        Assert.That(
            response.RootElement.TryGetProperty("result", out JsonElement result), Is.True,
            $"tools/call for '{toolName}' must return a result, not an error.");

        Assert.That(
            result.TryGetProperty("content", out JsonElement content), Is.True,
            $"tools/call for '{toolName}' result must contain a 'content' array.");

        Assert.That(content.GetArrayLength(), Is.GreaterThan(0),
            $"tools/call for '{toolName}' must return at least one content block.");

        string text = content[0].GetProperty("text").GetString() ?? string.Empty;
        Assert.That(text, Is.Not.Empty,
            $"tools/call for '{toolName}' content text must be non-empty.");
    }

    /// <summary>
    /// POSTs a JSON-RPC 2.0 body to the <c>/mcp</c> endpoint and parses
    /// the Streamable HTTP response. The MCP SDK serves responses as
    /// Server-Sent Events — the body is a single <c>event: message</c> /
    /// <c>data: {...}</c> frame for simple request/response flows. This
    /// helper extracts the JSON payload from that frame so tests can
    /// assert on the parsed document shape.
    /// </summary>
    private static async Task<JsonDocument> PostMcpAsync(HttpClient client, string body)
    {
        using HttpRequestMessage request = CreateMcpRequest(body);
        using HttpResponseMessage response = await client.SendAsync(request);
        string payload = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). Body: {payload}");
        }

        string? jsonLine = payload
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("data:", StringComparison.Ordinal));
        if (jsonLine is not null)
        {
            payload = jsonLine["data:".Length..].TrimStart();
        }

        return JsonDocument.Parse(payload);
    }

    private static HttpRequestMessage CreateMcpRequest(string body, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd(McpAcceptHeader);
        if (!string.IsNullOrWhiteSpace(origin))
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }
}
