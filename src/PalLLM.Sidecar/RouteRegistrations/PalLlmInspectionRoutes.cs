using System.Text.Json;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Maps /api/inspect/* routes: feature catalog, advisor
//            inventory, fallback strategy enumeration, environment
//            posture, suggestion surface. Read-only introspection of the
//            runtime composition.
//   surface: PalLlmInspectionRoutes.MapInspection(IEndpointRouteBuilder).
//   gate:    tests/PalLLM.Tests/SidecarEndpointTests.cs (inspect routes).
//   adr:     None directly.
//   docs:    docs/API.md (/api/inspect/*), docs/ADVISORS.md.
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmInspectionRoutes
{
    internal static void MapPalLlmInspectionRoutes(this RouteGroupBuilder api)
    {
        // Self-healing watchdog status: the latest evidence artifact written by
        // SelfHealingWorker, or a structured pending marker if the worker has not
        // ticked yet. Used by the dashboard chip + the pal_self_healing_status MCP
        // tool so every consumer sees the same payload contract.
        api.MapGet("/self-healing/status", IResult (
            HttpContext context,
            PalLlmOptions options) =>
        {
            using JsonDocument doc = SelfHealingStatusReader.Read(options);
            // Return the parsed JsonElement so the minimal-API pipeline serialises it
            // with the same PascalCase posture the rest of the sidecar uses.
            return TypedResults.Content(doc.RootElement.GetRawText(), "application/json");
        })
            .WithName("GetSelfHealingStatus")
            .WithTags("Inspection")
            .WithSummary("Latest SelfHealingWorker evidence or a pending marker when the watchdog has not ticked yet.");

        // Publication-facing air-gap posture check. Classifies every outbound surface
        // (inference, vision, TTS, OTLP, MCP upstreams) as loopback/private/public/
        // disabled without opening a TCP connection or emitting a single live
        // request. Answers "will this sidecar make any network call off this
        // machine under the current config?" in one shot.
        api.MapGet("/airgap/verify", IResult (
            HttpContext context,
            PalLlmOptions options) =>
        {
            AirGapReport report = AirGapVerifier.VerifyCached(options);
            return TypedResults.Ok(report);
        })
            .WithName("GetAirGapReport")
            .WithTags("Inspection")
            .WithSummary("Classify every outbound surface so operators + AI can prove air-gap posture.");

        // Local-first mesh role coverage. Reports which of the five roles
        // (Edge / Worker / Judge / Media / Validator) the operator has bound to
        // local endpoints, which are missing, and what a good pairing looks like
        // for the current setup. Metadata-only today: binding a role does not
        // automatically route inference traffic, but it makes the mesh
        // architecture legible to operators and AI clients.
        api.MapGet("/roles", IResult (ModelRoleRegistry registry) =>
        {
            ModelRoleCoverage coverage = registry.GetCoverage();
            return TypedResults.Ok(coverage);
        })
            .WithName("GetModelRoleCoverage")
            .WithTags("Inspection")
            .WithSummary("Coverage of the Edge / Worker / Judge / Media / Validator roles in the local-first AI mesh.");

        // Pass 25 / D1 - machine-readable hardware posture. Inspects the OS,
        // core count, RAM, and GPU markers and derives the recommended
        // DuoHardwareTier. Operators can force a tier via
        // PalLLM:Hardware:ForceTier; the override surfaces on the profile so
        // /api/describe and the dashboard can show both the detected and the
        // forced tier. Deterministic - no inference call, no subprocess, no
        // network. Safe to call on hot paths.
        api.MapGet("/hardware", IResult (PalLlmOptions options) =>
        {
            HardwareProfile profile = HardwareProfiler.CaptureCached(options.Hardware.ForceTier);
            return TypedResults.Ok(profile);
        })
            .WithName("GetHardwareProfile")
            .WithTags("Inspection")
            .WithSummary("Detected hardware posture: CPU cores, RAM, GPU-likelihood, and recommended DuoHardwareTier. Honours PalLLM:Hardware:ForceTier override.");

        // Pass 33 / D2 - graceful-degradation advisory. Inspects the current
        // HardwareProfile + PalLlmOptions and recommends a posture for boxes
        // that cannot comfortably run the full inference / vision / TTS
        // pipeline. Covers "my laptop has no GPU, can I still play?" -
        // deterministic director + small Edge model stay available, vision
        // + TTS are recommended off, and the active model lane is nudged
        // toward the smallest available tier. Pure advisory - the endpoint
        // never mutates options itself.
        api.MapGet("/degradation/advisory", IResult (PalLlmOptions options) =>
        {
            HardwareProfile profile = HardwareProfiler.CaptureCached(options.Hardware.ForceTier);
            DegradationAdvisory advisory = GracefulDegradationAdvisor.Recommend(profile, options);
            return TypedResults.Ok(advisory);
        })
            .WithName("GetDegradationAdvisory")
            .WithTags("Inspection")
            .WithSummary("Advisory posture for the current hardware + options: CPU-only deterministic-first, entry-GPU worker-only, full-mesh no-degradation. Never mutates runtime state.");

        // Pass 35 / D10 - resource budget posture. Enumerates every tracked
        // runtime budget (inference rate, circuit breaker, vision queue, TTS
        // caps, memory window, bridge retention, chat fallback share) and
        // classifies each as ok / review / exhausted with a plain-English
        // recommendation. Pure advisory - never mutates counters.
        api.MapGet("/budgets", IResult (PalLlmOptions options, PalLlmMetrics metrics) =>
        {
            ResourceBudgetMetrics derived = ResourceBudgetMetrics.FromSnapshot(metrics.Snapshot());
            ResourceBudgetPosture posture = ResourceBudgetPostureBuilder.CaptureCached(options, derived);
            return TypedResults.Ok(posture);
        })
            .WithName("GetResourceBudgetPosture")
            .WithTags("Inspection")
            .WithSummary("Resource-budget posture per feature (inference rate, vision queue, TTS caps, memory window, bridge retention, fallback share) with ok / review / exhausted bucketing.");

        // Pass 36 / C2 - world-narration advisor. Deterministic decision on
        // whether the current scene warrants a companion's one-line quip.
        // Triggers on combat-start, threat-spike, night-fall, weather-change,
        // low-health, objective-update. Rate-limited by caller - the advisor
        // returns the minimum gap it expects, so a narrator worker can drop
        // cues that arrive too quickly. Pure function over the world snapshot.
        api.MapGet("/narration/cue", IResult (PalLlmRuntime runtime) =>
        {
            NarrationCue cue = WorldNarrationAdvisor.Advise(runtime.Adapter.Snapshot, lastNarrationUtc: null);
            return TypedResults.Ok(cue);
        })
            .WithName("GetNarrationCue")
            .WithTags("Inspection")
            .WithSummary("Should the companion narrate right now? Deterministic decision from the current world snapshot. Never calls inference.");

        // Pass 38 / C10 - mood weather forecast per character. Blends the
        // RelationshipTracker's CharacterRelationship record with the current
        // world snapshot (threat, player health, time-of-day) to produce a
        // short mood/weather/tone triple the dashboard can render and the
        // chat prompt can include. Deterministic - no inference call.
        // Pass 40 / C8 - lifetime-relationship summary across every
        // observed session. Reads the aggregate persisted under
        // Runtime/LifetimeRelationships/latest.json (or returns an empty
        // aggregate if the file doesn't exist yet) and emits a life-story
        // summary per tracked character. Pure read-only inspection.
        api.MapGet("/relationships/lifetime", IResult (PalLlmRuntime runtime, PalLlmOptions options) =>
        {
            string saveRoot = string.IsNullOrWhiteSpace(options.PalSavedRoot)
                ? AppContext.BaseDirectory
                : options.PalSavedRoot!;
            string path = Path.Combine(saveRoot, "Runtime", "LifetimeRelationships", "latest.json");
            LifetimeRelationshipAggregate aggregate = LifetimeRelationshipAggregator.Empty();
            if (File.Exists(path))
            {
                BoundedJsonFileReader.JsonReadResult<LifetimeRelationshipAggregate> readResult =
                    BoundedJsonFileReader.TryRead(
                        path,
                        options.Http.LocalArtifactMaxBytes,
                        LifetimeRelationshipAggregator.Deserialize);
                if (readResult.Succeeded && readResult.Value is not null)
                {
                    aggregate = readResult.Value;
                }
            }
            var summaries = aggregate.Characters
                .Select(LifetimeRelationshipAggregator.Summarise)
                .ToArray();
            return TypedResults.Ok(new
            {
                Aggregate = aggregate,
                Summaries = summaries,
            });
        })
            .WithName("GetLifetimeRelationships")
            .WithTags("Relationships")
            .WithSummary("Cross-session lifetime summary for every tracked character. Reads the persisted aggregate under Runtime/LifetimeRelationships/latest.json with bounded local JSON ingress.");

        api.MapGet("/characters/{characterId:int}/mood", IResult (int characterId, PalLlmRuntime runtime) =>
        {
            CharacterRelationship? rel = runtime.GetRelationships()
                .FirstOrDefault(r => r.CharacterId == characterId);
            if (rel is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Character not tracked",
                    detail: $"No relationship record for character id {characterId}. Chat at least once to create one.");
            }
            MoodWeather forecast = MoodWeatherAdvisor.Forecast(rel, runtime.Adapter.Snapshot);
            return Results.Ok(forecast);
        })
            .WithName("GetCharacterMoodWeather")
            .WithTags("Relationships")
            .WithSummary("Deterministic mood-weather forecast for a tracked character, blended from relationship + world snapshot.");

        // Pass 27 / E3 - machine-readable privacy posture. Enumerates every
        // data-emitting surface PalLLM ships and classifies each as
        // "never-leaves", "only-with-opt-in", or "leaves-by-default" so
        // operators + AI agents can answer "what does this install actually
        // transmit?" without running a packet capture. Pairs with
        // /api/airgap/verify (network-scope view) to give a complete
        // privacy picture. Deterministic - no inference call.
        api.MapGet("/privacy/posture", IResult (PalLlmOptions options) =>
        {
            PrivacyPosture posture = PrivacyPostureBuilder.CaptureCached(options);
            return TypedResults.Ok(posture);
        })
            .WithName("GetPrivacyPosture")
            .WithTags("Inspection")
            .WithSummary("Enumerate every data-emitting surface and classify it as never-leaves / only-with-opt-in / leaves-by-default. Pairs with /api/airgap/verify.");
    }
}
