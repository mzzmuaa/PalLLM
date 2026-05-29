using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Maps /api/media/* routes: vision describe, TTS synth (when
//            enabled), ASR transcribe, multimodal cache-id lookup,
//            screenshot ingest.
//   surface: PalLlmMediaRoutes.MapMedia(IEndpointRouteBuilder).
//   gate:    tests/PalLLM.Tests/SidecarEndpointTests.cs (media routes) +
//            tests/PalLLM.Tests/SnapshotVisionFallbackTests.cs.
//   adr:     ADR 0006 (opt-in everything by default).
//   docs:    docs/MULTIMODAL_RECIPES.md, docs/API.md (/api/media/*).
// ---------------------------------------------------------------------------

namespace PalLLM.Sidecar;

internal static class PalLlmMediaRoutes
{
    internal static void MapPalLlmVisionRoutes(this RouteGroupBuilder api)
    {
        api.MapPost("/vision/describe", async (
            VisionDescribeRequest request,
            PalLlmRuntime runtime,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await runtime.DescribeImageAsync(request, cancellationToken)))
            .ValidatePalRequest<VisionDescribeRequest>()
            .RequireRateLimiting("vision-heavy")
            .WithName("DescribeImage")
            .WithTags("Vision")
            .WithSummary("Generate a freeform scene description for an image.")
            .Produces<VisionDescribeResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithRequestTimeout("vision-timeout");

        api.MapPost("/vision/world-state", async (
            VisionWorldStateRequest request,
            PalLlmRuntime runtime,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await runtime.ExtractWorldStateAsync(request, cancellationToken)))
            .ValidatePalRequest<VisionWorldStateRequest>()
            .RequireRateLimiting("vision-heavy")
            .WithName("ExtractVisionWorldState")
            .WithTags("Vision")
            .WithSummary("Extract structured world-state data from an image and optionally merge it into the live snapshot.")
            .Produces<VisionWorldStateResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithRequestTimeout("vision-timeout");

        api.MapPost("/vision/screenshots/process", async (
            PalLlmRuntime runtime,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await runtime.ProcessScreenshotsAsync(cancellationToken)))
            .RequireRateLimiting("vision-heavy")
            .WithName("ProcessPendingScreenshots")
            .WithTags("Vision")
            .WithSummary("Process pending screenshots from the bridge screenshot inbox.")
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithRequestTimeout("vision-timeout");
    }

    internal static void MapPalLlmAudioRoutes(this RouteGroupBuilder api)
    {
        api.MapPost("/tts/synthesize", async (
            TtsSynthesizeRequest request,
            PalLlmRuntime runtime,
            CancellationToken cancellationToken) =>
            TypedResults.Ok(await runtime.SynthesizeSpeechAsync(request, cancellationToken)))
            .ValidatePalRequest<TtsSynthesizeRequest>()
            .RequireRateLimiting("tts-heavy")
            .WithName("SynthesizeSpeech")
            .WithTags("Audio")
            .WithSummary("Synthesize speech audio for a text payload.")
            .Produces<TtsSynthesizeResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithRequestTimeout("tts-timeout");

        api.MapPost("/audio/transcribe", async (
            AudioTranscribeRequest request,
            PalLlmRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            AudioTranscribeResponse result = await runtime.TranscribeAudioAsync(request, cancellationToken);
            return TypedResults.Ok(result);
        })
            .ValidatePalRequest<AudioTranscribeRequest>()
            .RequireRateLimiting("tts-heavy")
            .WithName("TranscribeAudio")
            .WithTags("Audio")
            .WithSummary("Transcribe a bounded local audio payload through an opt-in OpenAI-compatible ASR endpoint.")
            .Produces<AudioTranscribeResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithRequestTimeout("tts-timeout");
    }
}
