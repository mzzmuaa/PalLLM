using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

/// <summary>
/// Request validation layer. .NET 10 ships built-in Minimal API validation via
/// DataAnnotations, but PalLLM keeps this custom filter because several fields
/// validate against <see cref="PalLlmOptions"/> (image-byte caps, TTS character
/// caps) which can't be expressed as static attributes. Kept here so a single
/// filter uniformly returns <c>Microsoft.AspNetCore.Mvc.ValidationProblemDetails</c>
/// with the shape tests pin.
/// </summary>
public static class PalApiValidation
{
    /// <summary>
    /// Hard cap on user-supplied free-text fields (UserMessage, Query,
    /// directive utterances, etc.). 16 KiB is generous for a companion
    /// chat surface — typical messages are hundreds of bytes; even
    /// long story-style inputs fit. Inputs larger than this are
    /// treated as caller error so a misbehaving client (or a
    /// malicious one) cannot drive the chat path with megabyte-scale
    /// payloads that eat memory + token budget on every turn.
    /// </summary>
    /// <remarks>
    /// The cap is deliberately not user-configurable: it's a safety
    /// rail, not a tunable. Operators who need a different cap can
    /// fork this constant; the choice belongs to whoever owns the
    /// runtime, not to a remote caller filling in a JSON field.
    /// </remarks>
    public const int UserTextMaxLength = ChatRequest.UserMessageMaxLength;

    /// <summary>
    /// Hard cap for short labels, identifiers, risk tags, task tags,
    /// and other low-entropy request metadata.
    /// </summary>
    public const int ShortTextMaxLength = 128;

    /// <summary>
    /// Proof packets are meant to summarize evidence, not carry an
    /// unbounded transcript. This keeps packet hashing, JSON output,
    /// and audit rendering predictable.
    /// </summary>
    public const int ProofEvidenceMaxEntries = 32;

    /// <summary>
    /// Maximum companions a single party-chat request can fan out across. The
    /// route runs a full <see cref="PalLlmRuntime.ChatAsync"/> turn per id, so
    /// a short list keeps the endpoint interactive and prevents one request
    /// from multiplying model work across an unbounded array.
    /// </summary>
    public const int PartyChatMaxCharacters = 8;

    public static RouteHandlerBuilder ValidatePalRequest<TRequest>(this RouteHandlerBuilder builder)
        where TRequest : class
    {
        return builder.AddEndpointFilterFactory((context, next) =>
        {
            int index = Array.FindIndex(
                context.MethodInfo.GetParameters(),
                parameter => parameter.ParameterType == typeof(TRequest));

            if (index < 0)
            {
                return invocationContext => next(invocationContext);
            }

            return async invocationContext =>
            {
                if (invocationContext.Arguments[index] is TRequest request)
                {
                    PalLlmOptions options = invocationContext.HttpContext.RequestServices.GetRequiredService<PalLlmOptions>();
                    Dictionary<string, string[]> errors = PalApiRequestValidator.Validate(request, options);
                    if (errors.Count > 0)
                    {
                        return TypedResults.ValidationProblem(errors);
                    }
                }

                return await next(invocationContext);
            };
        });
    }
}

public static class PalApiRequestValidator
{
    public static Dictionary<string, string[]> Validate(object request, PalLlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        Dictionary<string, List<string>> errors = new(StringComparer.Ordinal);

        switch (request)
        {
            case ChatRequest chat:
                ValidateChatRequest(chat, options, errors);
                break;
            case PartyChatRequest partyChat:
                ValidatePartyChatRequest(partyChat, errors);
                break;
            case MemoryRecallRequest recall:
                ValidateMemoryRecallRequest(recall, errors);
                break;
            case VisionDescribeRequest describe:
                ValidateVisionDescribeRequest(describe, options, errors);
                break;
            case VisionWorldStateRequest worldState:
                ValidateVisionWorldStateRequest(worldState, options, errors);
                break;
            case TtsSynthesizeRequest tts:
                ValidateTtsRequest(tts, options, errors);
                break;
            case AudioTranscribeRequest audioTranscribe:
                ValidateAudioTranscribeRequest(audioTranscribe, options, errors);
                break;
            case ModelCollaborationDecisionRequest collaborationDecision:
                ValidateModelCollaborationDecisionRequest(collaborationDecision, errors);
                break;
            case ChatPlanRequest chatPlan:
                ValidateChatPlanRequest(chatPlan, errors);
                break;
            case WhyRequest why:
                ValidateWhyRequest(why, errors);
                break;
            case DirectivePlanRequest directivePlan:
                ValidateDirectivePlanRequest(directivePlan, errors);
                break;
            case DuoPlanRequest duoPlan:
                ValidateDuoPlanRequest(duoPlan, errors);
                break;
            case DisagreementCheckRequest disagreement:
                ValidateDisagreementCheckRequest(disagreement, errors);
                break;
            case ProofPacketRequest proofPacket:
                ValidateProofPacketRequest(proofPacket, errors);
                break;
        }

        return errors.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }

    private static void ValidateChatRequest(
        ChatRequest request,
        PalLlmOptions options,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(request.UserMessage))
        {
            AddError(errors, nameof(request.UserMessage), "UserMessage is required.");
        }
        else if (request.UserMessage.Length > PalApiValidation.UserTextMaxLength)
        {
            // Hard input cap — see PalApiValidation.UserTextMaxLength docstring. A 5 MB
            // userMessage previously rendered into a ~1 MB reply and
            // burned both memory and token budget; this rejection
            // closes that vector cleanly with a structured 400.
            AddError(
                errors,
                nameof(request.UserMessage),
                $"UserMessage must be {PalApiValidation.UserTextMaxLength} characters or fewer (received {request.UserMessage.Length}).");
        }

        if (request.CharacterId is <= 0)
        {
            AddError(errors, nameof(request.CharacterId), "CharacterId must be greater than 0 when supplied.");
        }

        ValidateOptionalSampling(request.Temperature, request.MaxTokens, errors);
        ValidateOptionalImagePayload(request.ImageBase64, request.ImageMimeType, options, errors);

        if (!string.IsNullOrWhiteSpace(request.RequestId) && request.RequestId.Length > 128)
        {
            AddError(errors, nameof(request.RequestId), "RequestId must be 128 characters or fewer.");
        }
    }

    private static void ValidatePartyChatRequest(
        PartyChatRequest request,
        Dictionary<string, List<string>> errors)
    {
        if (request.CharacterIds is null || request.CharacterIds.Count == 0)
        {
            AddError(errors, nameof(request.CharacterIds), "At least one CharacterId is required.");
        }
        else
        {
            if (request.CharacterIds.Count > PalApiValidation.PartyChatMaxCharacters)
            {
                AddError(
                    errors,
                    nameof(request.CharacterIds),
                    $"CharacterIds can contain at most {PalApiValidation.PartyChatMaxCharacters} entries.");
            }

            if (request.CharacterIds.Any(id => id <= 0))
            {
                AddError(errors, nameof(request.CharacterIds), "Every CharacterId must be greater than 0.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.UserMessage))
        {
            AddError(errors, nameof(request.UserMessage), "UserMessage is required.");
        }
        else if (request.UserMessage.Length > PalApiValidation.UserTextMaxLength)
        {
            AddError(
                errors,
                nameof(request.UserMessage),
                $"UserMessage must be {PalApiValidation.UserTextMaxLength} characters or fewer (received {request.UserMessage.Length}).");
        }

        if (request.Temperature.HasValue &&
            (float.IsNaN(request.Temperature.Value) || float.IsInfinity(request.Temperature.Value) ||
             request.Temperature.Value < 0f || request.Temperature.Value > 2f))
        {
            AddError(errors, nameof(request.Temperature), "Temperature must be between 0 and 2.");
        }
    }

    private static void ValidateMemoryRecallRequest(
        MemoryRecallRequest request,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            AddError(errors, nameof(request.Query), "Query is required.");
        }
        else if (request.Query.Length > PalApiValidation.UserTextMaxLength)
        {
            AddError(
                errors,
                nameof(request.Query),
                $"Query must be {PalApiValidation.UserTextMaxLength} characters or fewer (received {request.Query.Length}).");
        }

        if (request.CharacterId is <= 0)
        {
            AddError(errors, nameof(request.CharacterId), "CharacterId must be greater than 0 when supplied.");
        }

        if (request.Limit is < 1 or > 25)
        {
            AddError(errors, nameof(request.Limit), "Limit must be between 1 and 25.");
        }
    }

    private static void ValidateVisionDescribeRequest(
        VisionDescribeRequest request,
        PalLlmOptions options,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            AddError(errors, nameof(request.ImageBase64), "ImageBase64 is required.");
        }

        ValidateOptionalImagePayload(request.ImageBase64, request.ImageMimeType, options, errors);
        ValidateOptionalSampling(request.Temperature, request.MaxTokens, errors);
    }

    private static void ValidateVisionWorldStateRequest(
        VisionWorldStateRequest request,
        PalLlmOptions options,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(request.ImageBase64))
        {
            AddError(errors, nameof(request.ImageBase64), "ImageBase64 is required.");
        }

        ValidateOptionalImagePayload(request.ImageBase64, request.ImageMimeType, options, errors);
    }

    private static void ValidateTtsRequest(
        TtsSynthesizeRequest request,
        PalLlmOptions options,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            AddError(errors, nameof(request.Text), "Text is required.");
            return;
        }

        if (request.Text.Length > options.Tts.MaxCharacters)
        {
            AddError(
                errors,
                nameof(request.Text),
                $"Text exceeds the configured cap of {options.Tts.MaxCharacters} characters.");
        }
    }

    private static void ValidateAudioTranscribeRequest(
        AudioTranscribeRequest request,
        PalLlmOptions options,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(request.AudioBase64))
        {
            AddError(errors, nameof(request.AudioBase64), "AudioBase64 is required.");
        }
        else
        {
            Base64PayloadInspection inspection = Base64PayloadInspector.Inspect(
                request.AudioBase64,
                options.Asr.MaxAudioBytes);
            if (!inspection.Accepted)
            {
                AddError(
                    errors,
                    nameof(request.AudioBase64),
                    Base64PayloadInspector.BuildAudioFailureMessage(
                        inspection,
                        options.Asr.MaxAudioBytes));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AudioMimeType) &&
            !AsrAudioMimeTypes.IsAllowed(request.AudioMimeType.Trim()))
        {
            AddError(
                errors,
                nameof(request.AudioMimeType),
                "AudioMimeType must be one of: " + string.Join(", ", AsrAudioMimeTypes.Allowed) + ".");
        }

        ValidateOptionalTextLength(
            request.Language,
            nameof(request.Language),
            PalApiValidation.ShortTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.Prompt,
            nameof(request.Prompt),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateAudioEndpointing(request.Endpointing, options, errors);
    }

    private static void ValidateAudioEndpointing(
        AudioTurnEndpointingInput? endpointing,
        PalLlmOptions options,
        Dictionary<string, List<string>> errors)
    {
        if (endpointing is null)
        {
            return;
        }

        ValidateOptionalMilliseconds(
            endpointing.SpeechMs,
            $"{nameof(AudioTranscribeRequest.Endpointing)}.{nameof(endpointing.SpeechMs)}",
            options.Asr.MaxTurnDurationMs,
            errors);
        ValidateOptionalMilliseconds(
            endpointing.LeadingSilenceMs,
            $"{nameof(AudioTranscribeRequest.Endpointing)}.{nameof(endpointing.LeadingSilenceMs)}",
            options.Asr.MaxTurnDurationMs,
            errors);
        ValidateOptionalMilliseconds(
            endpointing.TrailingSilenceMs,
            $"{nameof(AudioTranscribeRequest.Endpointing)}.{nameof(endpointing.TrailingSilenceMs)}",
            options.Asr.MaxTurnDurationMs,
            errors);
        ValidateOptionalTextLength(
            endpointing.EndpointReason,
            $"{nameof(AudioTranscribeRequest.Endpointing)}.{nameof(endpointing.EndpointReason)}",
            PalApiValidation.ShortTextMaxLength,
            errors);
    }

    private static void ValidateOptionalMilliseconds(
        int? value,
        string key,
        int maxMilliseconds,
        Dictionary<string, List<string>> errors)
    {
        if (value is null)
        {
            return;
        }

        if (value.Value < 0)
        {
            AddError(errors, key, $"{key} must be 0 or greater.");
            return;
        }

        if (value.Value > maxMilliseconds)
        {
            AddError(errors, key, $"{key} must be {maxMilliseconds} ms or less.");
        }
    }

    private static void ValidateModelCollaborationDecisionRequest(
        ModelCollaborationDecisionRequest request,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(request.Task))
        {
            AddError(errors, nameof(request.Task), "Task is required.");
        }
        else
        {
            ValidateOptionalTextLength(
                request.Task,
                nameof(request.Task),
                PalApiValidation.UserTextMaxLength,
                errors);
        }

        ValidateOptionalTextLength(
            request.TaskClass,
            nameof(request.TaskClass),
            PalApiValidation.ShortTextMaxLength,
            errors);

        if (!string.IsNullOrWhiteSpace(request.RiskLevel) &&
            request.RiskLevel is not ("low" or "medium" or "high"))
        {
            AddError(errors, nameof(request.RiskLevel), "RiskLevel must be one of: low, medium, high.");
        }
        else
        {
            ValidateOptionalTextLength(
                request.RiskLevel,
                nameof(request.RiskLevel),
                PalApiValidation.ShortTextMaxLength,
                errors);
        }

        if (request.VramGb is < 0)
        {
            AddError(errors, nameof(request.VramGb), "VramGb must be greater than or equal to 0.");
        }

        if (request.RamGb is < 0)
        {
            AddError(errors, nameof(request.RamGb), "RamGb must be greater than or equal to 0.");
        }

        if (request.UnifiedMemoryGb is < 0)
        {
            AddError(errors, nameof(request.UnifiedMemoryGb), "UnifiedMemoryGb must be greater than or equal to 0.");
        }

        ValidateOptionalTextLength(
            request.AvailableQuants,
            nameof(request.AvailableQuants),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.ContextBudget,
            nameof(request.ContextBudget),
            PalApiValidation.ShortTextMaxLength,
            errors);
    }

    private static void ValidateChatPlanRequest(
        ChatPlanRequest request,
        Dictionary<string, List<string>> errors)
    {
        ValidateOptionalTextLength(
            request.UserMessage,
            nameof(request.UserMessage),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.TaskTag,
            nameof(request.TaskTag),
            PalApiValidation.ShortTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.Risk,
            nameof(request.Risk),
            PalApiValidation.ShortTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.Hardware,
            nameof(request.Hardware),
            PalApiValidation.ShortTextMaxLength,
            errors);
    }

    private static void ValidateWhyRequest(
        WhyRequest request,
        Dictionary<string, List<string>> errors)
    {
        ValidateOptionalTextLength(
            request.Question,
            nameof(request.Question),
            PalApiValidation.UserTextMaxLength,
            errors);
    }

    private static void ValidateDirectivePlanRequest(
        DirectivePlanRequest request,
        Dictionary<string, List<string>> errors)
    {
        ValidateOptionalTextLength(
            request.Utterance,
            nameof(request.Utterance),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.AddressedPal,
            nameof(request.AddressedPal),
            PalApiValidation.ShortTextMaxLength,
            errors);
    }

    private static void ValidateDuoPlanRequest(
        DuoPlanRequest request,
        Dictionary<string, List<string>> errors)
    {
        ValidateOptionalTextLength(
            request.Note,
            nameof(request.Note),
            PalApiValidation.UserTextMaxLength,
            errors);
    }

    private static void ValidateDisagreementCheckRequest(
        DisagreementCheckRequest request,
        Dictionary<string, List<string>> errors)
    {
        ValidateOptionalTextLength(
            request.WorkerOutput,
            nameof(request.WorkerOutput),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.JudgeOutput,
            nameof(request.JudgeOutput),
            PalApiValidation.UserTextMaxLength,
            errors);
    }

    private static void ValidateProofPacketRequest(
        ProofPacketRequest request,
        Dictionary<string, List<string>> errors)
    {
        ValidateOptionalTextLength(
            request.Subsystem,
            nameof(request.Subsystem),
            PalApiValidation.ShortTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.Decision,
            nameof(request.Decision),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.PrimaryReason,
            nameof(request.PrimaryReason),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.RollbackPath,
            nameof(request.RollbackPath),
            PalApiValidation.UserTextMaxLength,
            errors);
        ValidateOptionalTextLength(
            request.Confidence,
            nameof(request.Confidence),
            PalApiValidation.ShortTextMaxLength,
            errors);

        if (request.Evidence is null)
        {
            return;
        }

        if (request.Evidence.Count > PalApiValidation.ProofEvidenceMaxEntries)
        {
            AddError(
                errors,
                nameof(request.Evidence),
                $"Evidence can contain at most {PalApiValidation.ProofEvidenceMaxEntries} entries.");
        }

        for (int i = 0; i < request.Evidence.Count; i++)
        {
            ValidateOptionalTextLength(
                request.Evidence[i],
                $"{nameof(request.Evidence)}[{i}]",
                PalApiValidation.UserTextMaxLength,
                errors);
        }
    }

    private static void ValidateOptionalSampling(
        float? temperature,
        int? maxTokens,
        Dictionary<string, List<string>> errors)
    {
        if (temperature.HasValue &&
            (float.IsNaN(temperature.Value) || float.IsInfinity(temperature.Value) || temperature.Value < 0f || temperature.Value > 2f))
        {
            AddError(errors, "Temperature", "Temperature must be between 0 and 2.");
        }

        if (maxTokens.HasValue && maxTokens.Value <= 0)
        {
            AddError(errors, "MaxTokens", "MaxTokens must be greater than 0 when supplied.");
        }
    }

    private static void ValidateOptionalImagePayload(
        string? imageBase64,
        string? imageMimeType,
        PalLlmOptions options,
        Dictionary<string, List<string>> errors)
    {
        if (string.IsNullOrWhiteSpace(imageBase64))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(imageMimeType) &&
            !imageMimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            AddError(errors, "ImageMimeType", "ImageMimeType must be an image/* media type.");
        }

        Base64PayloadInspection inspection = Base64PayloadInspector.Inspect(
            imageBase64,
            options.Vision.MaxImageBytes);
        if (!inspection.Accepted)
        {
            AddError(
                errors,
                "ImageBase64",
                Base64PayloadInspector.BuildImageFailureMessage(
                    inspection,
                    options.Vision.MaxImageBytes));
        }
    }

    private static void ValidateOptionalTextLength(
        string? value,
        string key,
        int maxLength,
        Dictionary<string, List<string>> errors)
    {
        if (!string.IsNullOrEmpty(value) && value.Length > maxLength)
        {
            AddError(
                errors,
                key,
                $"{key} must be {maxLength} characters or fewer (received {value.Length}).");
        }
    }

    private static void AddError(
        Dictionary<string, List<string>> errors,
        string key,
        string message)
    {
        if (!errors.TryGetValue(key, out List<string>? messages))
        {
            messages = [];
            errors[key] = messages;
        }

        messages.Add(message);
    }
}
