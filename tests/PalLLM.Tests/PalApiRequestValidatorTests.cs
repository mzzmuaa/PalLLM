using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

// The validator runs as an ASP.NET endpoint filter and is exercised indirectly
// by SidecarEndpointTests when they POST malformed payloads. These unit tests
// hit the validator directly so each request type's validation branches are
// covered without spinning up the full test host — faster, and easier to pin
// down which exact branch failed.
public sealed class PalApiRequestValidatorTests
{
    [Test]
    public void Validate_ChatRequest_WhenUserMessageMissing_ReportsUserMessageError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest { UserMessage = "   " },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(ChatRequest.UserMessage)));
        Assert.That(errors[nameof(ChatRequest.UserMessage)], Has.Some.Contain("required"));
    }

    [Test]
    public void Validate_ChatRequest_WhenCharacterIdNonPositive_ReportsCharacterIdError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest { UserMessage = "hi", CharacterId = 0 },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(ChatRequest.CharacterId)));
    }

    [Test]
    public void Validate_ChatRequest_WhenTemperatureNegative_ReportsTemperatureError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest { UserMessage = "hi", Temperature = -0.1f },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey("Temperature"));
    }

    [Test]
    public void Validate_ChatRequest_WhenTemperatureNaN_ReportsTemperatureError()
    {
        // NaN and Infinity are the corner cases System.Text.Json won't reject
        // on its own — the validator has to catch them explicitly.
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest { UserMessage = "hi", Temperature = float.NaN },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey("Temperature"));
    }

    [Test]
    public void Validate_ChatRequest_WhenTemperatureInfinity_ReportsTemperatureError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest { UserMessage = "hi", Temperature = float.PositiveInfinity },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey("Temperature"));
    }

    [Test]
    public void Validate_ChatRequest_WhenMaxTokensNonPositive_ReportsMaxTokensError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest { UserMessage = "hi", MaxTokens = 0 },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey("MaxTokens"));
    }

    [Test]
    public void Validate_ChatRequest_WhenRequestIdTooLong_ReportsRequestIdError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest
            {
                UserMessage = "hi",
                RequestId = new string('r', 129),
            },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(ChatRequest.RequestId)));
    }

    [Test]
    public void Validate_ChatRequest_WhenImageExceedsCap_ReportsImagePayloadError()
    {
        var options = new PalLlmOptions();
        options.Vision.MaxImageBytes = 16;
        // Base64 expands bytes 4/3. 25 chars → ~18.75 bytes, over the 16-byte cap.
        string payload = new string('A', 32);

        var errors = PalApiRequestValidator.Validate(
            new ChatRequest
            {
                UserMessage = "hi",
                ImageBase64 = payload,
                ImageMimeType = "image/png",
            },
            options);

        Assert.That(errors, Does.ContainKey("ImageBase64"));
    }

    [Test]
    public void Validate_ChatRequest_WhenImageBase64Malformed_ReportsImagePayloadError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest
            {
                UserMessage = "hi",
                ImageBase64 = "not base64!!!",
                ImageMimeType = "image/png",
            },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey("ImageBase64"));
        Assert.That(errors["ImageBase64"], Has.Some.Contain("valid base64"));
    }

    [Test]
    public void Validate_ChatRequest_WhenImageMimeNotImage_ReportsMimeError()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest
            {
                UserMessage = "hi",
                ImageBase64 = "AAAA",
                ImageMimeType = "text/plain",
            },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey("ImageMimeType"));
    }

    [Test]
    public void Validate_ChatRequest_WhenMinimalValid_ReturnsNoErrors()
    {
        var errors = PalApiRequestValidator.Validate(
            new ChatRequest { UserMessage = "hi" },
            new PalLlmOptions());

        Assert.That(errors, Is.Empty);
    }

    [Test]
    public void Validate_MemoryRecallRequest_MissingQuery_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new MemoryRecallRequest { Query = string.Empty },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(MemoryRecallRequest.Query)));
    }

    [Test]
    public void Validate_MemoryRecallRequest_LimitBelowRange_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new MemoryRecallRequest { Query = "anything", Limit = 0 },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(MemoryRecallRequest.Limit)));
    }

    [Test]
    public void Validate_MemoryRecallRequest_LimitAboveRange_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new MemoryRecallRequest { Query = "anything", Limit = 100 },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(MemoryRecallRequest.Limit)));
    }

    [Test]
    public void Validate_MemoryRecallRequest_CharacterIdNonPositive_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new MemoryRecallRequest { Query = "anything", CharacterId = 0 },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(MemoryRecallRequest.CharacterId)));
    }

    [Test]
    public void Validate_VisionDescribeRequest_MissingImage_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new VisionDescribeRequest { ImageBase64 = string.Empty },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(VisionDescribeRequest.ImageBase64)));
    }

    [Test]
    public void Validate_VisionWorldStateRequest_MissingImage_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new VisionWorldStateRequest { ImageBase64 = string.Empty },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(VisionWorldStateRequest.ImageBase64)));
    }

    [Test]
    public void Validate_TtsRequest_MissingText_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new TtsSynthesizeRequest { Text = "  " },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(TtsSynthesizeRequest.Text)));
    }

    [Test]
    public void Validate_TtsRequest_TextExceedsCap_Errors()
    {
        var options = new PalLlmOptions();
        options.Tts.MaxCharacters = 8;

        var errors = PalApiRequestValidator.Validate(
            new TtsSynthesizeRequest { Text = new string('x', 9) },
            options);

        Assert.That(errors, Does.ContainKey(nameof(TtsSynthesizeRequest.Text)));
    }

    [Test]
    public void Validate_AudioTranscribeRequest_MissingAudio_Errors()
    {
        var errors = PalApiRequestValidator.Validate(
            new AudioTranscribeRequest { AudioBase64 = string.Empty },
            new PalLlmOptions());

        Assert.That(errors, Does.ContainKey(nameof(AudioTranscribeRequest.AudioBase64)));
    }

    [Test]
    public void Validate_AudioTranscribeRequest_OversizedOrUnknownMime_Errors()
    {
        var options = new PalLlmOptions();
        options.Asr.MaxAudioBytes = 2;

        var errors = PalApiRequestValidator.Validate(
            new AudioTranscribeRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                AudioMimeType = "application/octet-stream",
            },
            options);

        Assert.That(errors, Does.ContainKey(nameof(AudioTranscribeRequest.AudioBase64)));
        Assert.That(errors, Does.ContainKey(nameof(AudioTranscribeRequest.AudioMimeType)));
    }

    [Test]
    public void Validate_AudioTranscribeRequest_InvalidEndpointingMetadata_Errors()
    {
        var options = new PalLlmOptions();
        options.Asr.MaxTurnDurationMs = 1_000;

        var errors = PalApiRequestValidator.Validate(
            new AudioTranscribeRequest
            {
                AudioBase64 = Convert.ToBase64String(new byte[] { 1, 2 }),
                Endpointing = new AudioTurnEndpointingInput
                {
                    SpeechMs = -1,
                    LeadingSilenceMs = 1_001,
                    EndpointReason = new string('x', 129),
                },
            },
            options);

        Assert.That(errors, Does.ContainKey("Endpointing.SpeechMs"));
        Assert.That(errors, Does.ContainKey("Endpointing.LeadingSilenceMs"));
        Assert.That(errors, Does.ContainKey("Endpointing.EndpointReason"));
    }

    [Test]
    public void Validate_UnrecognizedRequestType_ReturnsEmptyErrors()
    {
        // The filter is a no-op for request types the validator doesn't know
        // about — that's intentional. Tests that unknown types don't silently
        // error (which would 400 every unknown endpoint).
        var errors = PalApiRequestValidator.Validate(
            new { IAmA = "stranger" },
            new PalLlmOptions());

        Assert.That(errors, Is.Empty);
    }
}
