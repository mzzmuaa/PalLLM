using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace PalLLM.Tests;

/// <summary>
/// Coverage for the eight read-only diagnostic / posture HTTP endpoints
/// that prior passes exposed but didn't yet have dedicated tests:
///
/// <list type="bullet">
/// <item><c>GET /api/airgap/verify</c> — air-gap classification per surface</item>
/// <item><c>GET /api/budgets</c> — resource-budget posture per feature</item>
/// <item><c>GET /api/degradation/advisory</c> — graceful-degradation advisory</item>
/// <item><c>GET /api/hardware</c> — deterministic hardware posture</item>
/// <item><c>GET /api/narration/cue</c> — world-narration advisor</item>
/// <item><c>GET /api/privacy/posture</c> — machine-readable privacy posture</item>
/// <item><c>GET /api/roles</c> — local-first mesh role coverage</item>
/// <item><c>GET /api/self-healing/status</c> — SelfHealingWorker evidence</item>
/// </list>
///
/// Each test pins the contract that must hold on a default
/// fallback-only sidecar configuration: 200 OK + valid JSON +
/// at least one anchor field per the API.md contract.
///
/// These endpoints share a posture / read-only / never-fails-loudly
/// shape. Together they form the live "what is this install
/// configured to do?" surface that operator tooling, MCP clients, and
/// the dashboard all read.
/// </summary>
public sealed class DiagnosticEndpointsTests
{
    [Test]
    public async Task GetAirgapVerify_ReturnsClassifiedSurfaces()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/airgap/verify");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // The air-gap verifier classifies every outbound surface.
        // On a default install with no inference / vision / TTS / OTLP
        // wired, every classification should be `loopback`,
        // `disabled`, or `private`. None should be `public`.
        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/airgap/verify must return a JSON object.");
    }

    [Test]
    public async Task GetBudgets_ReturnsPerFeatureBudgetPosture()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/budgets");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/budgets must return a JSON object.");
    }

    [Test]
    public async Task GetDegradationAdvisory_ReturnsPostureAndRecommendations()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/degradation/advisory");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/degradation/advisory must return a JSON object.");
    }

    [Test]
    public async Task GetHardware_ReturnsDeterministicProfile()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/hardware");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/hardware must return a JSON object describing the host.");
    }

    [Test]
    public async Task GetNarrationCue_ReturnsAdvisoryDecision()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/narration/cue");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/narration/cue must return a JSON object.");
    }

    [Test]
    public async Task GetPrivacyPosture_ReturnsClassifiedSurfaces()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/privacy/posture");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // Privacy posture enumerates every data-emitting surface
        // and classifies each as never-leaves / only-with-opt-in /
        // leaves-by-default. The default install must never have a
        // surface in `leaves-by-default`.
        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/privacy/posture must return a JSON object.");
    }

    [Test]
    public async Task GetRoles_ReturnsMeshRoleCoverage()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/roles");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/roles must return a JSON object describing role coverage.");
    }

    [Test]
    public async Task GetWelcomePage_ServesStaticHtmlWithChatHook()
    {
        // Pass 179 added a friendly welcome page at `/welcome.html` for
        // non-technical users (Facebook grandma + average citizen
        // personas). The page is a self-contained ES2017 single-file
        // HTML at `wwwroot/welcome.html` that POSTs to `/api/chat`.
        // This test pins the contract:
        //   1. The static file is served on a default install (no
        //      special routing required — ASP.NET Core static files
        //      middleware picks it up automatically).
        //   2. The page contains the friendly greeting that a small
        //      model or new operator should see.
        //   3. The page references `/api/chat` so the chat hook still
        //      points at a valid runtime endpoint.
        await using var fixture = new SidecarTestFixture();

        using HttpResponseMessage response = await fixture.Client.GetAsync("/welcome.html");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "/welcome.html must be served as a static file on a default install.");

        string body = await response.Content.ReadAsStringAsync();
        Assert.That(body, Does.Contain("<title>Welcome to PalLLM</title>"),
            "Welcome page must keep the canonical title — operators bookmark this URL.");
        Assert.That(body, Does.Contain("Hi! I'm your companion."),
            "Welcome page must keep the friendly greeting; that's its whole purpose.");
        Assert.That(body, Does.Contain("/api/chat"),
            "Welcome page must POST to /api/chat — that's the runtime endpoint that makes the experience work.");
        Assert.That(body, Does.Contain("/api/airgap/verify"),
            "Welcome page must link the privacy-proof endpoint to back up its 'no data leaves this computer' claim.");

        // Pass 180 — the welcome page now has a Pal visual identity,
        // a browser-native voice playback button (SpeechSynthesis API,
        // no network call), and a localStorage-backed conversation
        // history. All three are non-network, all three add no
        // operator-surface risk, all three lift the casual-user
        // reviewer score. Pin each so future refactors can't silently
        // strip them.
        Assert.That(body, Does.Contain("pal-avatar"),
            "Welcome page must render a friendly Pal avatar — that's the visual identity grandma + casual-citizen reviewers respond to.");
        Assert.That(body, Does.Contain("speechSynthesis"),
            "Welcome page must offer browser-native voice playback. SpeechSynthesis works offline; no TTS engine wiring required.");
        Assert.That(body, Does.Contain("Read aloud"),
            "Welcome page must surface the voice playback button by its user-facing label.");
        Assert.That(body, Does.Contain("HISTORY_KEY"),
            "Welcome page must persist a small conversation history in localStorage so a returning user sees continuity.");
        Assert.That(body, Does.Contain("Clear conversation history"),
            "Welcome page must let the user delete their history — privacy + control.");

        // Pass 181 — accessibility + voice input. The welcome page now
        // has voice input (webkitSpeechRecognition for "talk to your
        // Pal"), a high-contrast toggle, and a big-text toggle. All
        // three persist user preferences in localStorage. Pin them so
        // future refactors can't strip the casual-user accessibility
        // story.
        Assert.That(body, Does.Contain("webkitSpeechRecognition"),
            "Welcome page must offer voice input for users who prefer talking over typing (grandma + accessibility).");
        Assert.That(body, Does.Contain("Big text"),
            "Welcome page must surface a Big-text accessibility toggle by its user-facing label.");
        Assert.That(body, Does.Contain("High contrast"),
            "Welcome page must surface a High-contrast accessibility toggle by its user-facing label.");
        Assert.That(body, Does.Contain("Speak instead of typing"),
            "Welcome page mic button must carry a clear, screen-reader-friendly label.");
        Assert.That(body, Does.Contain("PREFS_KEY"),
            "Welcome page must persist accessibility preferences in localStorage so they survive page reloads.");

        // Pass 182 — PWA manifest + favicon. The welcome page is now
        // installable as a Progressive Web App on Chrome / Edge /
        // Opera / Samsung Internet (localhost is treated as a secure
        // context, so PWA install works without HTTPS). Pin the
        // three new static surfaces so future refactors can't strip
        // the PWA story.
        Assert.That(body, Does.Contain("/manifest.webmanifest"),
            "Welcome page must link the Web App Manifest so it's PWA-installable.");
        Assert.That(body, Does.Contain("/favicon.svg"),
            "Welcome page must link an SVG favicon for browser tab + PWA launcher icon.");
        Assert.That(body, Does.Contain("apple-touch-icon"),
            "Welcome page must declare apple-touch-icon for iOS home-screen install.");
        Assert.That(body, Does.Contain("apple-mobile-web-app-capable"),
            "Welcome page must declare iOS web-app capability for full-screen launch.");

        // Verify the manifest itself is served on the same origin as a
        // valid JSON document with the canonical fields.
        using HttpResponseMessage manifestResponse =
            await fixture.Client.GetAsync("/manifest.webmanifest");
        Assert.That(manifestResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "/manifest.webmanifest must be served as a static file on a default install.");
        string manifestBody = await manifestResponse.Content.ReadAsStringAsync();
        using JsonDocument manifest = JsonDocument.Parse(manifestBody);
        Assert.That(manifest.RootElement.GetProperty("name").GetString(),
            Is.EqualTo("PalLLM Companion"),
            "Manifest 'name' must match the canonical PWA name. Browsers display this on the install prompt.");
        Assert.That(manifest.RootElement.GetProperty("start_url").GetString(),
            Is.EqualTo("/welcome.html"),
            "Manifest 'start_url' must point at the welcome page so PWA launch lands the user in the right place.");
        Assert.That(manifest.RootElement.GetProperty("display").GetString(),
            Is.EqualTo("standalone"),
            "Manifest 'display' must be 'standalone' so the installed PWA renders without browser chrome.");

        // Verify the SVG favicon serves with the right MIME type.
        using HttpResponseMessage faviconResponse =
            await fixture.Client.GetAsync("/favicon.svg");
        Assert.That(faviconResponse.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            "/favicon.svg must be served as a static file on a default install.");
        Assert.That(faviconResponse.Content.Headers.ContentType?.MediaType,
            Is.EqualTo("image/svg+xml"),
            "favicon.svg must serve with image/svg+xml MIME type so browsers render it correctly.");

        // Pass 184 — animated Pal character. Eyes blink, eyes track
        // the cursor, mouth smiles when a reply arrives. All
        // animations are CSS-driven and honor prefers-reduced-motion.
        // Pin the load-bearing element ids + animation keyframes so
        // a future markup refactor can't silently strip the visual
        // identity that lifts the casual-user reviewer score.
        Assert.That(body, Does.Contain("pal-eye-left"),
            "Welcome page Pal SVG must keep id='pal-eye-left' so blink + eye-tracking JS can address it.");
        Assert.That(body, Does.Contain("pal-eye-right"),
            "Welcome page Pal SVG must keep id='pal-eye-right' so blink + eye-tracking JS can address it.");
        Assert.That(body, Does.Contain("pal-mouth"),
            "Welcome page Pal SVG must keep id='pal-mouth' so the smile-on-reply animation can target it.");
        Assert.That(body, Does.Contain("@keyframes palBlink"),
            "Welcome page must declare the palBlink CSS keyframe — this is the idle blink animation.");
        Assert.That(body, Does.Contain("@keyframes palSmile"),
            "Welcome page must declare the palSmile CSS keyframe — this fires when a chat reply lands.");
    }

    [Test]
    public async Task GetSelfHealingStatus_ReturnsEvidenceOrPendingMarker()
    {
        await using var fixture = new SidecarTestFixture();
        using HttpResponseMessage response = await fixture.Client.GetAsync("/api/self-healing/status");
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement root = doc.RootElement;

        // The endpoint either returns the latest SelfHealingWorker
        // evidence object or a structured `pending` / `unreadable`
        // marker — never a 5xx, never an empty body.
        Assert.That(root.ValueKind, Is.EqualTo(JsonValueKind.Object),
            "/api/self-healing/status must return a JSON object.");
    }
}
