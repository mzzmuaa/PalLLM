using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;
using PalLLM.Sidecar;

namespace PalLLM.Tests;

public sealed class BackendValidationTests
{
    [Test]
    public void OptionsValidator_WhenInferenceEnabledWithoutModel_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Model = " ",
                PrefixCacheSalt = new string('x', 129),
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:Model"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:PrefixCacheSalt"));
    }

    [Test]
    public void OptionsValidator_WhenHttpAndBridgeIngressValuesAreInvalid_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Bridge = new BridgeOptions
            {
                MaxInboxEventBytes = 0,
            },
            Session = new SessionOptions
            {
                MaxPersistedBytes = 0,
            },
            Http = new HttpSurfaceOptions
            {
                SelfDescriptionCacheSeconds = -1,
                LocalArtifactMaxBytes = 0,
                ApiRequestBodyMaxBytes = 0,
                ChatRequestTimeoutSeconds = 0,
                VisionRequestTimeoutSeconds = -1,
                TtsRequestTimeoutSeconds = 0,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Bridge:MaxInboxEventBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Session:MaxPersistedBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Http:SelfDescriptionCacheSeconds"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Http:LocalArtifactMaxBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Http:ApiRequestBodyMaxBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Http:ChatRequestTimeoutSeconds"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Http:VisionRequestTimeoutSeconds"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Http:TtsRequestTimeoutSeconds"));
    }

    [Test]
    public void OptionsValidator_WhenMcpMetadataCapsAreNonPositive_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            McpClient = new McpClientOptions
            {
                MaxToolsPerServer = 0,
                MaxResourcesPerServer = 0,
                MaxPromptsPerServer = 0,
                MaxMetadataEntryLength = 0,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:McpClient:MaxToolsPerServer"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:McpClient:MaxResourcesPerServer"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:McpClient:MaxPromptsPerServer"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:McpClient:MaxMetadataEntryLength"));
    }

    [Test]
    public void OptionsValidator_WhenTierProbeIntervalIsNonPositive_FailsStartupValidation()
    {
        // The tier-probe worker re-checks the inference endpoint on this cadence; a
        // zero or negative value would either freeze the upgrade path entirely or
        // burn a hot loop, so startup must reject it before the worker kicks off.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = false,
                TierProbeIntervalSeconds = 0,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:TierProbeIntervalSeconds"));
    }

    [Test]
    public void OptionsValidator_WhenOptionalInferenceHintsAreInvalid_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = false,
                Temperature = float.PositiveInfinity,
                TopP = -0.1f,
                PresencePenalty = -2.5f,
                ReasoningEffort = "turbo",
                TokenBudgetField = "max_new_tokens",
                FrequencyPenalty = 2.5f,
                TopK = 0,
                MinP = 1.5f,
                RepetitionPenalty = float.NaN,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:Temperature"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:TopP"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:PresencePenalty"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:ReasoningEffort"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:TokenBudgetField"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:FrequencyPenalty"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:TopK"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:MinP"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:RepetitionPenalty"));
    }

    [Test]
    public void OptionsValidator_WhenReasoningEffortIsKnown_SucceedsValidation()
    {
        var validator = new PalLlmOptionsValidator();
        foreach (string allowed in InferenceReasoningEfforts.Allowed.Select(v => v.ToUpperInvariant()))
        {
            PalLlmOptions options = new()
            {
                Inference = new InferenceOptions
                {
                    Enabled = false,
                    Temperature = 2.0f,
                    TopP = 1.0f,
                    PresencePenalty = 2.0f,
                    ReasoningEffort = allowed,
                    FrequencyPenalty = -2.0f,
                    TopK = 1,
                    MinP = 0.0f,
                    RepetitionPenalty = 2.0f,
                },
            };
            ValidateOptionsResult result = validator.Validate(name: null, options);
            Assert.That(result.Succeeded, Is.True,
                $"ReasoningEffort='{allowed}' should validate. Failures: " +
                string.Join("; ", result.Failures ?? Array.Empty<string>()));
        }
    }

    [Test]
    public void OptionsValidator_WhenVisionSamplingValuesAreInvalid_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Vision = new VisionOptions
            {
                Enabled = false,
                Temperature = float.NaN,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Vision:Temperature"));
    }

    [Test]
    public void OptionsValidator_WhenTokenBudgetFieldIsKnown_SucceedsValidation()
    {
        var validator = new PalLlmOptionsValidator();
        foreach (string allowed in InferenceTokenBudgetFields.Allowed.Select(v => v.ToUpperInvariant()))
        {
            PalLlmOptions options = new()
            {
                Inference = new InferenceOptions
                {
                    Enabled = false,
                    TokenBudgetField = allowed,
                },
            };
            ValidateOptionsResult result = validator.Validate(name: null, options);
            Assert.That(result.Succeeded, Is.True,
                $"TokenBudgetField='{allowed}' should validate. Failures: " +
                string.Join("; ", result.Failures ?? Array.Empty<string>()));
        }
    }

    [Test]
    public void OptionsValidator_WhenStopSequencesAreInvalid_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = false,
                StopSequences =
                [
                    "</one>",
                    " ",
                    new string('x', 129),
                    " duplicate ",
                    "duplicate",
                ],
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:StopSequences must contain 4 entries or fewer."));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:StopSequences[1]"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:StopSequences[2]"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:StopSequences[4]"));
    }

    [Test]
    public void OptionsValidator_WhenModelTierEntriesAreBlank_FailsStartupValidation()
    {
        // Empty ModelTiers list is fine (it disables tier orchestration), but every
        // entry that IS configured must name an Id and a Model tag so the orchestrator
        // can probe the endpoint and surface a meaningful tier name in traces.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = false,
                ModelTiers = new List<ModelTierOptions>
                {
                    new() { Id = string.Empty, Model = "gemma3:4b", Priority = 1 },
                    new() { Id = "large", Model = "   ", Priority = 10 },
                },
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:ModelTiers[0]:Id"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:ModelTiers[1]:Model"));
    }

    [Test]
    public void OptionsValidator_WhenThermalGateBandsAreInverted_FailsStartupValidation()
    {
        // The warn band must trip before the reject band; if WarnAboveC >= RejectAboveC
        // the operator-visible "warm" indicator never fires before the gate rejects,
        // which silently breaks the operator UX without a runtime warning.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = false,
                ThermalGate = new ThermalGateOptions
                {
                    Enabled = true,
                    WarnAboveC = 90.0,
                    RejectAboveC = 85.0,
                    CacheTtlSeconds = 5,
                },
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:ThermalGate:WarnAboveC"));
    }

    [Test]
    public void OptionsValidator_WhenThermalGateCacheTtlIsNonPositive_FailsStartupValidation()
    {
        // CacheTtlSeconds is validated even when ThermalGate is disabled because
        // flipping Enabled=true at runtime should not require re-validation.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = false,
                ThermalGate = new ThermalGateOptions
                {
                    Enabled = false,
                    CacheTtlSeconds = 0,
                },
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:ThermalGate:CacheTtlSeconds"));
    }

    [Test]
    public void OptionsValidator_WhenThermalGateDisabledWithDefaults_SucceedsValidation()
    {
        // Default ThermalGateOptions (Enabled=false, defaults for everything else) must
        // pass cleanly so users who never touch the thermal gate config never hit a
        // surprise startup failure.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new();

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Succeeded, Is.True,
            "Default options must pass validation. Failures: " + string.Join("; ", result.Failures ?? Array.Empty<string>()));
    }

    [Test]
    public void OptionsValidator_WhenResponseByteCapsAreNonPositive_FailsStartupValidation()
    {
        // Response-byte caps gate every chat / vision / tts upstream read. Setting
        // any of them to 0 or negative would make the runtime floor to 1024 bytes
        // and silently truncate every reply -- a much worse failure mode than a
        // clean startup error.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = false,
                MaxResponseBytes = 0,
                ModelCatalogMaxResponseBytes = -1,
            },
            Vision = new VisionOptions
            {
                Enabled = false,
                MaxResponseBytes = 0,
            },
            Tts = new TtsOptions
            {
                Enabled = false,
                MaxResponseBytes = 0,
            },
            Asr = new AsrOptions
            {
                Enabled = false,
                MaxResponseBytes = 0,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:MaxResponseBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Inference:ModelCatalogMaxResponseBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Vision:MaxResponseBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Tts:MaxResponseBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:MaxResponseBytes"));
    }

    [Test]
    public void OptionsValidator_WhenAsrValuesAreInvalid_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Asr = new AsrOptions
            {
                Enabled = true,
                BaseUrl = "not a uri",
                Model = new string('a', 257),
                TimeoutSeconds = 0,
                ResponseFormat = "xml",
                TimestampGranularities = ["segment", "phoneme"],
                ChunkingStrategy = "semantic_vad",
                MaxAudioBytes = 0,
                MaxResponseBytes = 0,
                MaxTranscriptCharacters = 0,
                MaxTurnDurationMs = 0,
                PreSpeechPaddingMs = -1,
                EndpointSilenceMs = 0,
                Temperature = 2.0f,
                LowConfidenceLogprobThreshold = 1.0f,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:BaseUrl"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:Model"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:TimeoutSeconds"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:ResponseFormat"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:TimestampGranularities"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:ChunkingStrategy"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:MaxAudioBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:MaxResponseBytes"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:MaxTranscriptCharacters"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:MaxTurnDurationMs"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:PreSpeechPaddingMs"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:EndpointSilenceMs"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:Temperature"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Asr:LowConfidenceLogprobThreshold"));
    }

    [Test]
    public void OptionsValidator_WhenTtsSpeechFormatHasUnknownValues_FailsStartupValidation()
    {
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Tts = new TtsOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:8091/v1/audio/speech",
                DefaultVoice = "default",
                RequestFormat = "openai-audio",
                ResponseFormat = "wav24",
                Model = new string('m', 257),
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Tts:RequestFormat"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Tts:ResponseFormat"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Tts:Model"));
    }

    [Test]
    public void OptionsValidator_WhenReleaseEvidenceFreshnessHoursIsNonPositive_FailsStartupValidation()
    {
        // ReleaseEvidenceFreshnessHours gates whether evidence artifacts (audit /
        // health / native-proof bundles) are still trusted. A 0 or negative value
        // would mark every artifact perpetually stale with no obvious cause.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            ReleaseEvidenceFreshnessHours = 0,
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:ReleaseEvidenceFreshnessHours"));
    }

    [Test]
    public void OptionsValidator_WhenHardwareForceTierIsUnknown_FailsStartupValidation()
    {
        // Empty / null is fine (auto-detect). A typo like "Generus" would
        // otherwise be silently ignored by the runtime, which is operator-hostile
        // because their override has no effect and they have to read the C#
        // source to find the enum members.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            Hardware = new HardwareOptions
            {
                ForceTier = "Generus", // typo for Generous
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:Hardware:ForceTier"));
    }

    [Test]
    public void OptionsValidator_WhenHardwareForceTierIsKnown_SucceedsValidation()
    {
        var validator = new PalLlmOptionsValidator();
        foreach (string allowed in new[] { "Constrained", "Standard", "Generous", "constrained", "GENEROUS" })
        {
            PalLlmOptions options = new()
            {
                Hardware = new HardwareOptions
                {
                    ForceTier = allowed,
                },
            };
            ValidateOptionsResult result = validator.Validate(name: null, options);
            Assert.That(result.Succeeded, Is.True,
                $"ForceTier='{allowed}' should validate. Failures: " +
                string.Join("; ", result.Failures ?? Array.Empty<string>()));
        }
    }

    [Test]
    public void OptionsValidator_WhenSelfHealingIntervalsAreNonPositive_FailsStartupValidation()
    {
        // The watchdog floors negative values at runtime, but startup validation
        // is the right place to catch operator confusion -- a clean error
        // pointing at the exact field beats silently-floored behavior.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            SelfHealing = new SelfHealingOptions
            {
                CheckIntervalSeconds = 0,
                OrphanEnvelopeAgeSeconds = -1,
                UnhealthyScoreFloor = -1,
                HistoryRetention = -1,
            },
            PromotionFeeder = new PromotionFeederOptions
            {
                CheckIntervalSeconds = 0,
                MaxObservationsPerStrategyPerTick = 0,
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:SelfHealing:CheckIntervalSeconds"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:SelfHealing:OrphanEnvelopeAgeSeconds"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:PromotionFeeder:CheckIntervalSeconds"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:PromotionFeeder:MaxObservationsPerStrategyPerTick"));
    }

    [Test]
    public void OptionsValidator_WhenMcpUpstreamServerEntriesAreBlank_FailsStartupValidation()
    {
        // Empty UpstreamServers list is valid (MCP-client discovery off). When the
        // list IS populated, every entry must declare a non-empty Id and Url so the
        // discovery pool can record and probe it. Malformed URLs (typos, wrong
        // schemes) are intentionally NOT rejected here — they surface at probe time
        // as ErrorCode="invalid_endpoint" snapshots in /api/mcp/upstream, which is
        // pinned by McpUpstreamClientTests.UpstreamPool_WhenConfiguredServerHasInvalidUrl_*.
        var validator = new PalLlmOptionsValidator();
        PalLlmOptions options = new()
        {
            McpClient = new McpClientOptions
            {
                UpstreamServers = new List<McpUpstreamServer>
                {
                    new() { Id = string.Empty, Url = "http://localhost:3001/mcp" },
                    new() { Id = "missing-url", Url = string.Empty },
                },
            },
        };

        ValidateOptionsResult result = validator.Validate(name: null, options);

        Assert.That(result.Failed, Is.True);
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:McpClient:UpstreamServers[0]:Id"));
        Assert.That(result.Failures, Has.Some.Contains("PalLLM:McpClient:UpstreamServers[1]:Url"));
    }

    [Test]
    public void RequestValidator_BlankChatMessage_ReturnsValidationProblem()
    {
        PalLlmOptions options = new();

        Dictionary<string, string[]> errors = PalApiRequestValidator.Validate(new ChatRequest
        {
            UserMessage = "   ",
        }, options);

        Assert.That(errors, Contains.Key(nameof(ChatRequest.UserMessage)));
    }

    [Test]
    public void RequestValidator_TtsTextOverConfiguredCap_ReturnsValidationProblem()
    {
        PalLlmOptions options = new()
        {
            Tts = new TtsOptions
            {
                MaxCharacters = 8,
            },
        };

        Dictionary<string, string[]> errors = PalApiRequestValidator.Validate(new TtsSynthesizeRequest
        {
            Text = "This is longer than eight chars.",
        }, options);

        Assert.That(errors, Contains.Key(nameof(TtsSynthesizeRequest.Text)));
    }

    [Test]
    public void ShippedAppSettings_BindsCleanlyIncludingAutomationBlock()
    {
        // appsettings.json previously shipped without an Automation section, so the
        // nested config would silently default even when operators were editing it
        // by hand. The shipped file now also carries the HTTP surface-tuning
        // block because the sidecar exposes an OpenAPI cache + protective
        // concurrency limits that operators may need to tune. Verify the file at
        // the expected repo path round-trips into PalLlmOptions with both blocks
        // present and Validate-clean.
        string appsettingsPath = LocateAppsettings();
        Assert.That(File.Exists(appsettingsPath), $"appsettings.json not found at {appsettingsPath}.");

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonFile(appsettingsPath, optional: false)
            .Build();

        PalLlmOptions options = config.GetSection("PalLLM").Get<PalLlmOptions>()
            ?? throw new AssertionException("PalLLM section did not bind.");

        Assert.That(options.Automation, Is.Not.Null,
            "AutomationOptions must always be reachable so runtime code can safely read it.");
        Assert.That(options.Http, Is.Not.Null,
            "HttpSurfaceOptions must always be reachable so runtime code can safely read the HTTP cache/admission-control settings.");

        // The shipped file SHOULD declare the Automation block explicitly so operators
        // see the knobs instead of discovering them by grepping the C# source.
        string rawJson = File.ReadAllText(appsettingsPath);
        Assert.That(rawJson, Does.Contain("\"Automation\""),
            "appsettings.json should document the Automation block so operators can see the kill switch and allowlist.");
        Assert.That(rawJson, Does.Contain("\"Http\""),
            "appsettings.json should document the Http block so operators can see the OpenAPI cache + concurrency limits without reading Program.cs.");
        Assert.That(rawJson, Does.Contain("\"ApiRequestBodyMaxBytes\""),
            "appsettings.json should document the API/MCP request-body byte cap so operators can tune ingress hardening without reading Program.cs.");
        Assert.That(rawJson, Does.Contain("\"MaxInboxEventBytes\""),
            "appsettings.json should document the bridge inbox byte cap so operators can tune local ingress hardening without reading PalLlmRuntime.");
        Assert.That(rawJson, Does.Contain("\"MaxPersistedBytes\""),
            "appsettings.json should document the session byte cap so operators can tune startup persistence hardening without reading SessionPersistence.");
        Assert.That(rawJson, Does.Contain("\"ChatRequestTimeoutSeconds\""),
            "appsettings.json should document the chat request timeout so operators can tune stuck-request hardening without reading Program.cs.");
        Assert.That(rawJson, Does.Contain("\"VisionRequestTimeoutSeconds\""),
            "appsettings.json should document the vision request timeout so operators can tune stuck-request hardening without reading Program.cs.");
        Assert.That(rawJson, Does.Contain("\"TtsRequestTimeoutSeconds\""),
            "appsettings.json should document the TTS request timeout so operators can tune stuck-request hardening without reading Program.cs.");
        Assert.That(rawJson, Does.Contain("\"Asr\""),
            "appsettings.json should document the ASR block so operators can qualify audio transcription without reading PalLlmOptions.");
        Assert.That(rawJson, Does.Contain("\"MaxTranscriptCharacters\""),
            "appsettings.json should document the ASR transcript cap so audio proof lanes stay bounded.");
        Assert.That(rawJson, Does.Contain("\"EndpointSilenceMs\""),
            "appsettings.json should document the ASR endpointing target so voice-turn proof receipts stay reproducible.");
        Assert.That(rawJson, Does.Contain("\"RequestLogprobs\""),
            "appsettings.json should document the ASR confidence receipt opt-in so token text is not needed for proof.");
        Assert.That(rawJson, Does.Contain("\"ResponseFormat\""),
            "appsettings.json should document the ASR response_format knob so verbose transcription canaries are explicit.");
        Assert.That(rawJson, Does.Contain("\"TimestampGranularities\""),
            "appsettings.json should document the ASR timestamp-granularity opt-in so verbose timing receipts stay explicit.");
        Assert.That(rawJson, Does.Contain("\"ChunkingStrategy\""),
            "appsettings.json should document the ASR chunking_strategy opt-in so VAD chunking canaries stay explicit.");
        Assert.That(rawJson, Does.Contain("\"RequestPriority\""),
            "appsettings.json should document the opt-in request-priority pass-through so vLLM operators can tune foreground scheduling without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"Temperature\""),
            "appsettings.json should document baseline sampler bounds so operators can tune chat and vision without reading PalLlmOptions.");
        Assert.That(rawJson, Does.Contain("\"TopP\""),
            "appsettings.json should document the baseline top-p sampler so operators can tune live chat without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"PresencePenalty\""),
            "appsettings.json should document the baseline presence-penalty sampler so operators can tune live chat without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"TokenBudgetField\""),
            "appsettings.json should document the token-budget field selector so reasoning-model lanes can qualify max_completion_tokens without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"FrequencyPenalty\""),
            "appsettings.json should document the opt-in frequency-penalty pass-through so operators can tune repetition control without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"TopK\""),
            "appsettings.json should document the opt-in top-k pass-through so local-runtime operators can qualify sampler controls without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"MinP\""),
            "appsettings.json should document the opt-in min-p pass-through so local-runtime operators can qualify sampler controls without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"RepetitionPenalty\""),
            "appsettings.json should document the opt-in repetition-penalty pass-through so local-runtime operators can tune repetition control without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"ParallelToolCalls\""),
            "appsettings.json should document the opt-in parallel-tool-call pass-through so strict tool routes can be qualified without reading InferenceClient.");
        Assert.That(rawJson, Does.Contain("\"StopSequences\""),
            "appsettings.json should document the opt-in stop-sequence pass-through so delimiter canaries can be qualified without reading InferenceClient.");

        // Per-startup validation should accept the shipped defaults.
        var validator = new PalLlmOptionsValidator();
        ValidateOptionsResult validation = validator.Validate(name: null, options);
        Assert.That(validation.Succeeded, Is.True,
            "Shipped appsettings.json must pass startup validation. Failures: "
                + string.Join("; ", validation.Failures ?? Array.Empty<string>()));
    }

    private static string LocateAppsettings()
    {
        // Walk up from the test bin directory to the repo root, then down to the
        // sidecar project — robust whether tests run from bin/Debug or the repo root.
        string current = AppContext.BaseDirectory;
        for (int depth = 0; depth < 8; depth++)
        {
            string candidate = Path.Combine(current, "src", "PalLLM.Sidecar", "appsettings.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || parent == current)
            {
                break;
            }

            current = parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    }

    [Test]
    public async Task InferenceClient_WhenEndpointReturnsMalformedJson_ReturnsFailedWithoutThrowing()
    {
        // A 200 with a non-JSON body used to propagate JsonException up through the
        // chat handler as a 500. The catch now converts it to a clean Failed result
        // so the chat path can fall through to the deterministic director.
        using var httpClient = new HttpClient(new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not json</html>", System.Text.Encoding.UTF8, "text/html"),
        }));
        PalLlmOptions options = new()
        {
            Inference = new InferenceOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Model = "qwen3.6:35b-a3b",
                MaxTransientRetries = 0,
            },
        };
        var client = new HttpJsonInferenceClient(httpClient, options);

        InferenceResult result = await client.CompleteAsync(new InferencePrompt
        {
            SystemPrompt = "sys",
            UserPrompt = "user",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("malformed JSON").IgnoreCase);
    }

    [Test]
    public async Task VisionClient_WhenEndpointReturnsMalformedJson_ReturnsFailedWithoutThrowing()
    {
        using var httpClient = new HttpClient(new FixedResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"choices\": [ broken", System.Text.Encoding.UTF8, "application/json"),
        }));
        PalLlmOptions options = new()
        {
            Vision = new VisionOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Model = "gemma4:e2b",
            },
        };
        var client = new HttpVisionClient(httpClient, options);

        VisionResult result = await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("malformed JSON").IgnoreCase);
    }

    [Test]
    public void ChatRateLimiter_WhenDisabled_AlwaysAcquires()
    {
        var limiter = new ChatRateLimiter { MaxPerMinute = 0 };
        for (int i = 0; i < 50; i++)
        {
            Assert.That(limiter.TryAcquire("anyone"), Is.True,
                "MaxPerMinute=0 means the limiter is a no-op; every acquire must succeed.");
        }

        Assert.That(limiter.IsEnabled, Is.False);
    }

    [Test]
    public void ChatRateLimiter_IsolatesBucketsPerKey()
    {
        var limiter = new ChatRateLimiter { MaxPerMinute = 2 };
        Assert.That(limiter.TryAcquire("alice"), Is.True);
        Assert.That(limiter.TryAcquire("alice"), Is.True);
        Assert.That(limiter.TryAcquire("alice"), Is.False,
            "alice has exhausted her budget within the one-minute window.");

        // bob has his own window; alice's exhaustion must not leak across buckets.
        Assert.That(limiter.TryAcquire("bob"), Is.True);
        Assert.That(limiter.TryAcquire("bob"), Is.True);
        Assert.That(limiter.TryAcquire("bob"), Is.False);

        Assert.That(limiter.BucketCount, Is.GreaterThanOrEqualTo(2));
    }

    private sealed class FixedResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public FixedResponseHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }

    [Test]
    public async Task VisionClient_WhenStructuredOutputsEnabled_SendsResponseFormatSchema()
    {
        // World-state extraction must forward the json_schema response_format so
        // endpoints that support structured outputs (OpenAI, Ollama ≥ 0.5, LM
        // Studio, vLLM) constrain the model. Endpoints that don't recognise the
        // field silently ignore it — backwards compatible either way.
        using var handler = new BodyCapturingHandler();
        using var httpClient = new HttpClient(handler);
        PalLlmOptions options = new()
        {
            Vision = new VisionOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Model = "gemma4:e2b",
                UseStructuredOutputs = true,
            },
        };
        var client = new HttpVisionClient(httpClient, options);

        // Pass the orchestrator-shaped request — ResponseFormat is set here the
        // same way VisionOrchestrator.ExtractWorldStateAsync sets it.
        using JsonDocument schema = JsonDocument.Parse(
            """{"type":"json_schema","json_schema":{"name":"palllm_world_state","schema":{"type":"object"}}}""");
        await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
            SystemPrompt = "schema",
            UserPrompt = "extract",
            ResponseFormat = schema.RootElement.Clone(),
        }, CancellationToken.None);

        Assert.That(handler.LastRequestBody, Is.Not.Empty);
        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody);
        Assert.That(body.RootElement.TryGetProperty("response_format", out JsonElement rf), Is.True,
            "Chat-completions body must carry response_format when the orchestrator opts in.");
        Assert.That(rf.GetProperty("type").GetString(), Is.EqualTo("json_schema"));
        Assert.That(rf.GetProperty("json_schema").GetProperty("name").GetString(), Is.EqualTo("palllm_world_state"));
    }

    [Test]
    public async Task VisionClient_WhenStructuredOutputsOmitted_DoesNotIncludeResponseFormat()
    {
        // Operators who set UseStructuredOutputs=false — or any caller who does
        // not set ResponseFormat — must see a clean body with no response_format
        // field, so older endpoints that error on unknown fields keep working.
        using var handler = new BodyCapturingHandler();
        using var httpClient = new HttpClient(handler);
        PalLlmOptions options = new()
        {
            Vision = new VisionOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Model = "gemma4:e2b",
                UseStructuredOutputs = false,
            },
        };
        var client = new HttpVisionClient(httpClient, options);

        await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
            UserPrompt = "freeform describe",
        }, CancellationToken.None);

        using JsonDocument body = JsonDocument.Parse(handler.LastRequestBody);
        Assert.That(body.RootElement.TryGetProperty("response_format", out _), Is.False,
            "When no ResponseFormat is supplied the request body must NOT carry one.");
    }

    private sealed class BodyCapturingHandler : HttpMessageHandler
    {
        public string LastRequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"{}\"}}]}",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    [Test]
    public async Task VisionClient_WhenEndpointIsUnreachable_ReturnsFailedResult()
    {
        using var httpClient = new HttpClient(new ThrowingHttpMessageHandler(new HttpRequestException("socket closed")));
        PalLlmOptions options = new()
        {
            Vision = new VisionOptions
            {
                Enabled = true,
                BaseUrl = "http://127.0.0.1:11434/v1/",
                Model = "gemma4:e2b",
            },
        };
        var client = new HttpVisionClient(httpClient, options);

        VisionResult result = await client.DescribeAsync(new VisionRequest
        {
            ImageBase64 = "Zm9v",
        }, CancellationToken.None);

        Assert.That(result.Success, Is.False);
        Assert.That(result.StatusMessage, Does.Contain("unreachable").IgnoreCase);
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _exception switch
            {
                HttpRequestException http => Task.FromException<HttpResponseMessage>(http),
                OperationCanceledException canceled => Task.FromException<HttpResponseMessage>(canceled),
                _ => Task.FromException<HttpResponseMessage>(new InvalidOperationException("Unexpected test exception type.")),
            };
    }
}
