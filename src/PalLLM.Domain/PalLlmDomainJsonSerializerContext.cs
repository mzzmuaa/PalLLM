using System.Text.Json;
using System.Text.Json.Serialization;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Packs;
using PalLLM.Domain.Runtime;

namespace PalLLM.Domain;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(BridgeEventEnvelope))]
[JsonSerializable(typeof(BridgeBootPayload))]
[JsonSerializable(typeof(BridgeBootCompatSignal))]
[JsonSerializable(typeof(BridgeBootCompatSignal[]))]
[JsonSerializable(typeof(ChatHookPayload))]
[JsonSerializable(typeof(BaseDiscoveredPayload))]
[JsonSerializable(typeof(CombatEventPayload))]
[JsonSerializable(typeof(PalStatusEventPayload))]
[JsonSerializable(typeof(ProductionEventPayload))]
[JsonSerializable(typeof(TravelEventPayload))]
[JsonSerializable(typeof(WeatherEventPayload))]
[JsonSerializable(typeof(RaidEventPayload))]
[JsonSerializable(typeof(UiProbeEventPayload))]
[JsonSerializable(typeof(ReplyDeliveryEventPayload))]
[JsonSerializable(typeof(SpeechPlaybackEventPayload))]
[JsonSerializable(typeof(GameWorldSnapshot))]
[JsonSerializable(typeof(OutboxEnvelope))]
[JsonSerializable(typeof(VisionWorldStateSnapshot))]
[JsonSerializable(typeof(NarrativePackDefinition))]
[JsonSerializable(typeof(PersonalityPackManifest))]
[JsonSerializable(typeof(LifetimeRelationshipAggregate))]
[JsonSerializable(typeof(ProofPacket))]
[JsonSerializable(typeof(ProofPacketArtifact))]
[JsonSerializable(typeof(ProofPacketValidator))]
[JsonSerializable(typeof(InferenceChatCompletionsRequestBody))]
[JsonSerializable(typeof(InferenceChatMessage))]
[JsonSerializable(typeof(InferenceChatTemplateKwargs))]
// Pass 346: OllamaWarmupRequestBody removed (runtime now warms every engine
// via the generic OpenAI-compatible chat-completions path).
[JsonSerializable(typeof(VisionChatCompletionsRequestBody))]
[JsonSerializable(typeof(VisionChatMessage))]
[JsonSerializable(typeof(VisionContentPart))]
[JsonSerializable(typeof(VisionContentPart[]))]
[JsonSerializable(typeof(VisionImageUrl))]
[JsonSerializable(typeof(TtsHttpRequestBody))]
[JsonSerializable(typeof(OpenAiSpeechTtsHttpRequestBody))]
[JsonSerializable(typeof(AudioTranscriptionRequest))]
[JsonSerializable(typeof(AudioTranscriptionResult))]
[JsonSerializable(typeof(AudioTranscriptionConfidenceReceipt))]
[JsonSerializable(typeof(AudioTranscriptionTimingReceipt))]
[JsonSerializable(typeof(AudioTranscriptionQualityReceipt))]
[JsonSerializable(typeof(AudioTurnEndpointingInput))]
[JsonSerializable(typeof(AudioTurnEndpointingReceipt))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(List<NarrativeCharacterProfile>))]
[JsonSerializable(typeof(List<NarrativeRelationshipSeed>))]
[JsonSerializable(typeof(List<NarrativeMemorySeed>))]
internal partial class PalLlmDomainJsonSerializerContext : JsonSerializerContext;

internal static class PalLlmDomainJsonOptions
{
    public static void AddSourceGeneration(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.TypeInfoResolverChain.Contains(PalLlmDomainJsonSerializerContext.Default))
        {
            options.TypeInfoResolverChain.Insert(0, PalLlmDomainJsonSerializerContext.Default);
        }
    }

    public static JsonSerializerOptions Create(Action<JsonSerializerOptions>? configure = null)
    {
        JsonSerializerOptions options = new();
        AddSourceGeneration(options);
        configure?.Invoke(options);
        return options;
    }
}
