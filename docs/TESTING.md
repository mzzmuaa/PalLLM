# Testing - how to write a test for X

Last audited: `2026-05-22`

PalLLM has 1154 tests in NUnit, organized one file per
subsystem. They run in under 30 s on a Standard-tier dev box and
require zero external dependencies (no model server, no GPU, no
Palworld). This doc names the patterns the suite uses so adding a
new test fits the existing shape.

The test infrastructure is kept on the current stable .NET/NUnit package line.
Before a release hardening pass, run:

```powershell
dotnet list D:\Coding\PalLLM\PalLLM.sln package --outdated
```

That check is intentionally manual because it uses NuGet.org, while the normal
test and audit posture must remain local-first after restore.

## Structure

- **One file per subsystem.** `RuntimeTests.cs` for the chat
  pipeline, `BackendValidationTests.cs` for request validation,
  `SidecarEndpointTests.cs` for HTTP integration, etc.
- **Focused fixtures.** A test class covers one subsystem; an
  individual test covers one behavior. Long fixtures with
  dozens of unrelated tests are an anti-pattern.
- **No external dependencies.** Inference, vision, TTS, MCP
  upstream proxy - all stubbed via in-process fakes (see
  `SidecarTestFixture` for the canonical setup).
- **Deterministic output.** Tests that need a clock use the
  `FakeClock` pattern - never `DateTimeOffset.UtcNow` directly.

## How to write a test for...

### A pure-logic class (advisor / builder / scorer)

```csharp
[TestFixture]
public class MyAdvisorTests
{
    [Test]
    public void Recommend_NormalState_ReturnsBalancedAdvisory()
    {
        // Arrange - concrete inputs, no mocks
        var input = new MyAdvisorInput(...);

        // Act - pure function
        var result = MyAdvisor.Recommend(input);

        // Assert - every meaningful field
        Assert.Multiple(() =>
        {
            Assert.That(result.Posture, Is.EqualTo("balanced"));
            Assert.That(result.Recommendations, Has.Count.EqualTo(3));
            Assert.That(result.CapturedAtUtc, Is.GreaterThan(DateTimeOffset.MinValue));
        });
    }
}
```

Reference: `MoodWeatherAdvisorTests.cs`, `OperatorHealthScorerTests.cs`,
`HardwareProfilerTests.cs`.

### A TTL-cached surface

Always invalidate the static cache in `[SetUp]` so each test
starts from a fresh state.

```csharp
[SetUp]
public void Setup()
{
    PrivacyPostureBuilder.InvalidateCache();
}
```

Then test both the cold and the warm path:

```csharp
[Test]
public void CaptureCached_SecondCall_WithinTtl_ReturnsSameInstance()
{
    var options = new PalLlmOptions();
    var first = PrivacyPostureBuilder.CaptureCached(options);
    var second = PrivacyPostureBuilder.CaptureCached(options);
    Assert.That(second, Is.SameAs(first));
}

[Test]
public void CaptureCached_SignatureChanged_RecomputesEvenWithinTtl()
{
    var options = new PalLlmOptions();
    var first = PrivacyPostureBuilder.CaptureCached(options);
    options.Vision.Enabled = !options.Vision.Enabled;
    var second = PrivacyPostureBuilder.CaptureCached(options);
    Assert.That(second, Is.Not.SameAs(first));
}
```

Reference: `PrivacyPostureBuilderTests.cs`,
`AirGapVerifierTests.cs`.

### An HTTP endpoint

Use `SidecarTestFixture` to spin up a `WebApplicationFactory`
with the same DI graph as production. Hit the route with
`HttpClient`.

```csharp
[Test]
public async Task GetExamplePosture_ReturnsExpectedShape()
{
    using var fixture = new SidecarTestFixture();
    var client = fixture.CreateClient();

    var response = await client.GetAsync("/api/example/posture");

    response.EnsureSuccessStatusCode();
    var body = await response.Content.ReadFromJsonAsync<ExamplePosture>();
    Assert.Multiple(() =>
    {
        Assert.That(body, Is.Not.Null);
        Assert.That(body!.Headline, Is.Not.Empty);
    });
}
```

Reference: `SidecarEndpointTests.cs` (the largest example
fixture; covers ~80 routes).

### An MCP tool

The fixture's MCP test helper sends a JSON-RPC `tools/call`
and parses the response.

```csharp
[Test]
public async Task PalMyTool_ValidArgs_ReturnsExpectedResult()
{
    using var fixture = new SidecarTestFixture();
    var client = fixture.CreateClient();
    var result = await McpTestHelper.CallToolAsync(
        client,
        toolName: "pal_my_tool",
        args: new { ... });
    Assert.That(result.IsSuccess, Is.True);
    // Assert on result.Structured...
}
```

Reference: `McpEndpointTests.cs`.

### A streaming endpoint (SSE)

Use `HttpClient.GetStreamAsync` and parse line-prefixed events.
The fixture has a `ReadSseAsync` helper.

Reference: `ChatStreamEndpointTests.cs`.

### A bridge event handler

Write a fixture envelope, drop it in a temp `Bridge/Inbox/`
directory, point the runtime at the temp directory, drain.

```csharp
[Test]
public async Task DrainInbox_NewEventType_RoutesCorrectly()
{
    using var fixture = new SidecarTestFixture();
    var inboxPath = fixture.GetBridgeInboxPath();
    File.WriteAllText(
        Path.Combine(inboxPath, "test-event.json"),
        JsonSerializer.Serialize(new { ... }));

    await fixture.Runtime.DrainInboxAsync();

    // Assert side-effects
}
```

Reference: `RuntimeTests.cs` (multiple `DrainInbox_*` tests).

### A new fallback strategy

Drive `ChatAsync` with an input that should match the strategy.
Assert on `ResponsePath` and `FallbackStrategy` in the response.

```csharp
[Test]
public async Task ChatAsync_NewMatchingInput_FiresMyStrategy()
{
    using var fixture = new SidecarTestFixture();
    fixture.Options.Inference.Enabled = false;  // force fallback

    var response = await fixture.Runtime.ChatAsync(new ChatRequest
    {
        UserMessage = "input that should match my new strategy",
        CharacterId = 1,
    });

    Assert.That(response.FallbackStrategy, Is.EqualTo("my-new-strategy"));
    Assert.That(response.ResponsePath, Does.Contain("my-new-strategy"));
}
```

Reference: `RuntimeTests.cs` (every existing `Try_*` strategy
has at least one such test).

## Conventions

- **`Assert.Multiple`** for grouped assertions - failures
  report all violations, not just the first.
- **No `Assert.Pass`** as an escape hatch. If a test isn't
  testing something, delete it.
- **No `Thread.Sleep`** to wait for async work. Use `await`,
  task completion sources, or polling with a timeout.
- **No literal `DateTimeOffset.UtcNow` in assertions**. Either
  capture once and compare to a window, or use the
  `FakeClock` pattern.
- **No `Random` without a seed**. The runtime is deterministic;
  tests should be too.

## Coverage

Coverage runs on every CI invocation via
`coverlet.collector` and renders into the Actions step
summary. The `runsettings` file
(`tests/PalLLM.Tests/coverlet.runsettings`) excludes generated
code. Current baseline: `~88%` line / `~75%` branch on
`PalLLM.Domain` + `PalLLM.Sidecar`.

Inspect locally:

```powershell
dotnet test PalLLM.sln --collect:"XPlat Code Coverage" `
    --settings tests/PalLLM.Tests/coverlet.runsettings `
    --results-directory ./TestResults
dotnet tool install --global dotnet-reportgenerator-globaltool
reportgenerator "-reports:TestResults/**/coverage.cobertura.xml" `
    "-targetdir:CoverageReport" `
    "-reporttypes:HtmlInline"
Start-Process CoverageReport/index.html
```

## Drift gates that affect tests

- `Drift_Test_count_docs` - `[Test]` attribute count must
  agree with counts in README, ROADMAP, ARCHITECTURE,
  HANDOFF, CODE_MAP. Add a test -> bump the docs.

## Related

- [`COOKBOOK.md`](COOKBOOK.md) - recipes for adding
  endpoints / advisors / etc., each with a "tests to add"
  step
- [`EXTENSION_POINTS.md`](EXTENSION_POINTS.md) - "where do I
  add the test?" map
- [`CONTRIBUTING.md`](../CONTRIBUTING.md) - pre-flight
  checklist for PRs
- `tests/PalLLM.Tests/SidecarTestFixture.cs` - the
  canonical fixture wiring; read this once and you'll know
  how every HTTP / MCP test is structured


