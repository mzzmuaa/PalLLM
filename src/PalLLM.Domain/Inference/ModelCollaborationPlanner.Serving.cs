using PalLLM.Domain.Configuration;

namespace PalLLM.Domain.Inference;

// Partial of ModelCollaborationPlanner; see ModelCollaborationPlanner.cs for fields + ctor + GetSnapshot.
//
// llama.cpp (llama-server) is PalLLM's ONLY local inference engine. Models that
// cannot run as a local GGUF route through the OpenAI-compatible cloud escape
// path (pal connect cloud) instead. Both speak /v1/chat/completions, so every
// serving hint below targets a single local llama-server or that cloud
// fallback — there is no multi-backend recommendation surface.
public sealed partial class ModelCollaborationPlanner
{

    private static ModelServingProfile BuildServingProfile(
        bool isGguf,
        bool isEmbedding,
        bool isSparseMoe,
        bool supportsVision,
        bool supportsVideo,
        bool supportsAudioInput,
        bool supportsAudioOutput,
        bool isQwen36,
        bool isQwenOmni,
        bool isGemma3n,
        bool isGemma4,
        bool supportsStructuredOutputs,
        bool supportsToolCalls,
        bool supportsSpeculativeDecoding)
    {
        bool multimodal = supportsVision || supportsVideo || supportsAudioInput || supportsAudioOutput;
        bool localGguf = isGguf && !isEmbedding;
        bool cloudEscape = !isGguf && !isEmbedding;
        bool supportsModelNativeMtp = localGguf && (isQwen36 || isGemma4);
        bool isGemmaAudio = supportsAudioInput && (isGemma4 || isGemma3n);
        string gemmaAudioFamily = isGemma4 ? "Gemma 4" : "Gemma 3n";
        string gemmaAudioTokenRate = isGemma4 ? "25" : "6.25";
        string gemmaThirtySecondTokenCost = isGemma4 ? "750" : "188";

        string profileId = isEmbedding ? "embedding-retrieval"
            : supportsAudioOutput ? "omni-realtime-opt-in"
            : multimodal && isGguf ? "gguf-libmtmd-multimodal"
            : isGguf ? "gguf-chat"
            : "cloud-openai-chat";

        string requestProtocol = isEmbedding
            ? "OpenAI-compatible /v1/embeddings"
            : supportsAudioOutput
                ? "OpenAI-compatible /v1/chat/completions for text plus a separate opt-in /v1/audio/speech lane for audio"
                : "OpenAI-compatible /v1/chat/completions";

        string preferredRuntime = isEmbedding
            ? "a local llama.cpp llama-server embedding lane behind the retrieval pipeline"
            : isGguf
                ? "llama.cpp llama-server (the bundled, only local engine)"
                : "an OpenAI-compatible cloud API via pal connect cloud (the below-reference-rig escape path; PalLLM ships no non-GGUF local engine)";

        // ---- StartupHints -------------------------------------------------
        List<string> startupHints = new();
        if (localGguf)
        {
            startupHints.Add("GGUF artifact provenance lane: record source repo or local path, immutable revision or file hash, base model relation, quantizer/build version, license metadata, and matching mmproj hash before promotion or redistribution.");
            startupHints.Add("llama.cpp local lane: --host 127.0.0.1 --port <port> -c <qualified-context> -np <player-slot-count> -b <measured> -ub <measured> -ngl <measured> --flash-attn on --metrics --no-webui.");
            startupHints.Add("llama.cpp prompt-cache lane: keep --cache-prompt enabled; tune --cache-reuse, -sps, and -cram before raising -np; verify /metrics or logs instead of assuming slot reuse.");
            startupHints.Add("llama.cpp state-cache canary lane: for SWA, hybrid, recurrent, or long-context GGUFs, test --swa-full / --slot-save-path / --cache-reuse on the exact server build before enabling host prompt-cache persistence.");
            startupHints.Add("llama.cpp idle-memory proof lane: use --sleep-idle-seconds only after wake latency, cold-after-wake cache behavior, and deterministic fallback behavior are recorded.");
            startupHints.Add("llama.cpp KV-memory proof lane: compare default f16 against -ctk q8_0 -ctv q8_0 before using longer contexts.");
            startupHints.Add("llama.cpp connector lane: use pal connect llamacpp to print the llama-server command, probe /health and /v1/models, and wire PalLLM:Inference with residency hints disabled.");
            startupHints.Add("llama.cpp schema lane: qualify response_format json_schema or json_object through the server's grammar conversion with a PalLLM schema digest receipt before using it for actions, world-state, or proof packets.");
        }

        if (cloudEscape)
        {
            startupHints.Add("Cloud escape lane: use pal connect cloud -Provider <provider> -Model <id> -ApiKey <key> -WriteConfig to point PalLLM:Inference at an OpenAI-compatible cloud endpoint when local llama.cpp cannot serve the needed model on below-reference hardware.");
            startupHints.Add("Cloud privacy lane: prompts leave the machine on the cloud escape path; keep player text, save paths, and secrets out of system prompts and confirm the provider's retention posture before promotion.");
        }

        if (isEmbedding)
        {
            startupHints.Add("Embedding lane: run a local llama-server embedding model behind the retrieval pipeline and expose /v1/embeddings; keep it on loopback and separate from the live companion chat slot.");
        }

        if (localGguf && isQwenOmni)
        {
            startupHints.Add("llama.cpp Qwen Omni speech lane: for GGUF speech proof, launch llama-server with --talker-model <qwen3-omni-talker.gguf> and --code2wav-model <qwen3-omni-code2wav.gguf> to expose /v1/audio/speech, then keep that endpoint separate from the live text lane until p95 chat latency is re-proven.");
        }

        if (localGguf && isGemma3n)
        {
            startupHints.Add("Gemma 3n edge lane: qualify E2B/E4B with PLE caching and conditional parameter loading; bypass vision/audio parameters on text-only companion lanes to reduce memory and latency.");
        }

        if (supportsModelNativeMtp)
        {
            startupHints.Add("llama.cpp draft-MTP proof lane: qualify --spec-type draft-mtp with measured --spec-draft-n-min / --spec-draft-n-max on text-only Qwen3.6 or Gemma 4 replay before any player-facing use.");
            if (multimodal)
            {
                startupHints.Add("MTP/multimodal split-lane guard: keep text MTP and vision/audio profiles on separate server processes or ports until the exact llama-server build proves shared encoder batches do not corrupt slot, KV, or scheduler state.");
            }
        }

        if (localGguf && supportsVision)
        {
            startupHints.Add("--mmproj <matching-mmproj.gguf>");
        }

        if (supportsVideo)
        {
            startupHints.Add("llama.cpp video lane: most GGUF/libmtmd builds understand still images, not video; treat video understanding as cloud-escape-only until the exact llama-server build proves frame ingestion on PalLLM proof clips.");
        }

        if (supportsAudioInput)
        {
            startupHints.Add("Audio ingress normalization lane: downmix and resample player clips to mono 16 kHz, cap clips at 30 seconds, and reject oversized audio before it reaches the model server.");
            if (isGemmaAudio)
            {
                startupHints.Add($"{gemmaAudioFamily} audio-token budget lane: account for {gemmaAudioTokenRate} audio tokens per second, normalized mono 16 kHz float32 input, 30-second default clips, and about {gemmaThirtySecondTokenCost} audio tokens before text/output headroom.");
            }

            if (localGguf)
            {
                startupHints.Add("--mmproj <matching-audio-mmproj.gguf>");
            }
        }

        if (supportsAudioOutput && localGguf)
        {
            startupHints.Add("llama.cpp audio-output lane: expose speech only through the isolated --talker-model / --code2wav-model /v1/audio/speech endpoint; never block the live text reply on the audio stage.");
        }

        if (supportsSpeculativeDecoding && !multimodal && localGguf)
        {
            startupHints.Add("llama.cpp speculation proof lane: --spec-type ngram-simple --spec-draft-n-max 64 for repetitive PalLLM text turns after parser proof.");
            startupHints.Add("llama.cpp speculation alternative: --spec-type ngram-mod --spec-ngram-mod-n-match 24 --spec-ngram-mod-n-min 48 --spec-ngram-mod-n-max 64 when repeated text/code patterns dominate.");
        }

        // ---- RequestHints -------------------------------------------------
        List<string> requestHints =
        [
            "Keep PalLLM text chat on /v1/chat/completions so deterministic fallback remains the hot-path backstop.",
            "Stamp every promotion replay with the PalLLM operation name and latency budget; /api/chat, /api/vision/describe, /api/vision/world-state, audio/ASR, and long proof/docs lanes are separate evidence.",
            "Do not promote modalities, tool calling, speculation, or realtime audio from a family name alone; capture a primary-source capability receipt and a local runtime canary for the exact model artifact.",
            "Before any downloaded model, quant, adapter, mmproj, or drafter becomes a PalLLM default, record the source URL or local path, immutable revision or file hash, model-card license metadata, base-model/adapter relation, runtime version, and trust-remote-code status.",
            "Use PalLLM:Inference:Seed only for endpoint-proven replay lanes; record the seed, served model id, runtime version, and any system_fingerprint-equivalent evidence beside the replay result.",
            "Use PalLLM:Inference:TokenBudgetField=max_completion_tokens only for endpoint-proven reasoning lanes that require the newer OpenAI-compatible token-budget field; record accepted request shape, visible/reasoning token usage when exposed, p95 latency, and fallback counters before promotion.",
            "Use PalLLM:Inference:FrequencyPenalty only for endpoint-proven repetition-control lanes; record repeated-phrase rate, generated tokens, latency, and fallback counters before making it a player-facing default.",
            "Keep PalLLM:Inference:Temperature, TopP, and PresencePenalty inside their startup-validated OpenAI-style ranges before testing any endpoint-specific sampler changes.",
            "Use PalLLM:Inference:TopK, MinP, and RepetitionPenalty only for endpoint-proven local-sampler lanes; record accepted request shape, style/loop deltas, token count, latency, and fallback counters before promotion.",
            "Use PalLLM:Inference:StopSequences only for endpoint-proven delimiter lanes; record before/after token usage and clipped-text review before treating stop as a latency optimization.",
            "Use InferencePrompt.Prediction only for endpoint-proven predicted-output replay lanes, such as stable proof or docs regeneration; ordinary companion chat omits prediction so strict local endpoints stay portable.",
            "Use InferencePrompt.Logprobs / TopLogprobs only for endpoint-proven confidence or evaluator canaries; ordinary companion chat omits logprobs so strict local endpoints stay portable, and PalLLM preserves returned choice logprobs as a raw receipt.",
        ];
        if (supportsStructuredOutputs)
        {
            requestHints.Add("Use InferencePrompt.ResponseFormat to forward response_format json_schema on portable route-specific text proof lanes; use InferencePrompt.StructuredOutputs only for endpoint-specific structured_outputs canaries; use the vision schema hook for world-state, and keep ordinary companion chat field-free until the endpoint proves structured-output support.");
            requestHints.Add("Every schema-bearing replay should carry a schema digest, schema name, PalLLM route class, served model id, request shape, grammar/backend id, temperature, parse result, schema-validation result, token usage, p95 latency, and fallback counter delta.");
        }

        if (supportsToolCalls)
        {
            requestHints.Add("Use prompt-level InferencePrompt.Tools and InferencePrompt.ToolChoice only on strict route canaries; PalLLM forwards tools/tool_choice verbatim and preserves returned tool_calls as a receipt, while ordinary companion chat omits both fields.");
            requestHints.Add("For tool-call proof, set PalLLM:Inference:ParallelToolCalls=false so PalLLM forwards parallel_tool_calls=false on strict directive/action routes until a multi-call fan-out contract is intentionally added and tested.");
        }

        if (localGguf)
        {
            requestHints.Add("On llama.cpp, keep cache_prompt enabled for stable PalLLM prefixes; do not assume reuse across chat-template, context-size, adapter, model, or slot changes.");
            requestHints.Add("Use PalLLM:Inference:LlamaCppCachePrompt, LlamaCppSlotId, and LlamaCppCacheReuseTokens only on endpoint-proven llama-server canaries; ordinary cross-runtime chat omits cache_prompt, id_slot, and n_cache_reuse.");
            requestHints.Add("For llama.cpp host prompt-cache persistence, include the model family, tokenizer metadata, chat template, context size, adapter id, --swa-full state, and server commit in the PalLLM replay receipt.");
            if (supportsModelNativeMtp)
            {
                requestHints.Add("For llama.cpp draft-MTP proof, record the --spec-type draft-mtp command line, --spec-draft-* depths, tokenizer/chat-template identity, served model hash, server commit, accepted/generated tokens, TTFT, ITL, parser result, and fallback counters.");
            }
        }

        if (cloudEscape)
        {
            requestHints.Add("On the cloud escape lane, keep PalLLM on /v1/chat/completions, verify the configured model id via /v1/models before relying on tier orchestration, and keep request hints (cache keys, metadata, safety identifiers) free of player identity, save paths, or secrets.");
        }

        if (supportsSpeculativeDecoding)
        {
            requestHints.Add("Qualify n-gram or suffix speculation first for repetitive PalLLM text turns; use draft-model or model-native MTP configs only after repeated-run schema/tool-call tests pass.");
        }

        if (supportsModelNativeMtp)
        {
            requestHints.Add("Treat Qwen3.6 or Gemma 4 MTP as a separate model-native speculation mode; keep strict JSON, tool-call, judge, and save-replay routes no-spec until route-specific proof exists.");
            if (isQwen36)
            {
                requestHints.Add("For Qwen3.6 low-concurrency latency proof, run MTP-1 with prefix caching disabled and keep the normal prefix-cache lane separate for shared or prompt-heavy traffic.");
            }
            else if (isGemma4)
            {
                requestHints.Add("For Gemma 4 MTP benchmarking, treat assistant checkpoints as Gemma 4 MTP speculators rather than generic draft models; disable prefix caching for the measurement pass, then replay the chosen drafter profile with normal cache settings before promotion.");
            }

            if (multimodal)
            {
                requestHints.Add("Treat text-only MTP proof as text-only proof; screenshot, video, and audio routes need their own no-spec, n-gram, and model-native speculation replay before promotion.");
            }
        }

        if (isGemma3n)
        {
            requestHints.Add("For Gemma 3n, request only the modalities needed for the turn; text-only companion turns should not load audio or vision parameters just because the model can.");
        }

        if (isQwen36)
        {
            requestHints.Add("Qwen3.6 has a 262K default context on official cards; keep ordinary companion turns short, and reserve 128K+ contexts for proof, docs-sync, or deliberate review lanes that can afford the KV cache.");
            requestHints.Add("Do not copy a 1,010,000-token extension, hosted catalog limit, or GGUF context setting across Qwen3.6 lanes; the served model id, runtime context cap, extension flags, and route token budget must be in the promotion receipt.");
        }

        if (supportsAudioInput)
        {
            requestHints.Add("For native audio-in, prefer OpenAI input_audio with base64 wav or mp3; treat audio_url and video_url extensions as operator-proof only because they can fetch remote media.");
            requestHints.Add("Keep ordinary player speech on cascaded ASR-to-text until typed-text fallback, privacy retention, and latency evidence are recorded.");
            if (isGemmaAudio)
            {
                requestHints.Add($"For {gemmaAudioFamily} audio-in, record the normalized duration and {gemmaAudioTokenRate} audio-token-per-second estimate; a 30-second clip spends about {gemmaThirtySecondTokenCost} audio tokens before text, system, and output headroom.");
            }
        }

        if (multimodal)
        {
            requestHints.Add("Use prompt-level InferencePrompt.UserContent only on route-specific multimodal input canaries; ordinary companion chat keeps a plain string user message, while proof lanes may forward text/image_url/input_audio content parts after media-admission checks.");
            requestHints.Add("Send local data URLs or file paths from an explicitly allowed runtime directory; remote URLs are opt-in only.");
        }

        if (supportsAudioOutput)
        {
            requestHints.Add("Use prompt-level InferencePrompt.Modalities and InferencePrompt.Audio only on isolated audio-output canaries; PalLLM forwards modalities/audio, preserves returned message.audio on InferenceResult.AudioJson, and keeps ordinary companion chat field-free.");
            requestHints.Add("Use PalLLM:Tts:RequestFormat=openai_speech and optional PalLLM:Tts:Speed only for endpoint-proven /v1/audio/speech canaries; record the accepted speed, response_format, voice, output MIME, byte count, duration, p95 synthesis latency, and text fallback behavior before promoting speech playback.");
        }

        // ---- CacheHints ---------------------------------------------------
        List<string> cacheHints =
        [
            "Keep the PalLLM system prompt, pack summary, and world-state prefix stable for prefix-cache hits.",
            "Treat every local model cache as a staged artifact store: cache hits are useful only after the cached files have a provenance receipt with license, revision/hash, weight format, and trust boundary recorded.",
        ];
        if (supportsStructuredOutputs)
        {
            cacheHints.Add("Keep schema-bearing replay, cache, and coalescing identities keyed by route, model id, request shape, and canonical schema digest; never reuse a cached structured answer across schema revisions.");
        }
        if (multimodal)
        {
            cacheHints.Add("Use content-hash media ids so identical screenshots avoid repeated multimodal preprocessing.");
            cacheHints.Add("Budget multimodal processor cache memory per server process before increasing media counts.");
            if (supportsModelNativeMtp)
            {
                cacheHints.Add("Keep text MTP KV cache and multimodal encoder/KV cache in separate process or port cache namespaces until same-process MTP plus media replay is proven stable.");
            }
        }

        if (supportsAudioInput)
        {
            cacheHints.Add("Hash normalized 16 kHz mono audio bytes after trimming policy is applied; do not reuse raw clip hashes across different resampling or silence-trimming settings.");
            if (isGemma4)
            {
                cacheHints.Add("Store the Gemma audio normalized duration and audio-token cost estimate beside the clip hash so cache hits cannot hide budget overruns.");
            }
        }

        if (isGemma3n)
        {
            cacheHints.Add("For Gemma 3n, measure PLE cache hit behavior and conditional parameter loading separately from text KV cache before claiming edge-memory wins.");
        }

        if (localGguf)
        {
            cacheHints.Add("On llama.cpp, prompt caching is per server and slot; measure second-turn latency, --cache-reuse hits, -cram pressure, slot similarity, and active KV memory before raising -np.");
            cacheHints.Add("Treat llama.cpp --slot-save-path as local operator-managed persistence; keep saved slot state under the PalLLM runtime root and redact or exclude it from support bundles.");
            cacheHints.Add("Treat llama.cpp host prompt-cache restore as a per-model-family capability, not a RAM-only toggle; SWA, hybrid, recurrent, multimodal, and long-context GGUFs need state-cache canary receipts before promotion.");
        }

        // ---- AdmissionControls --------------------------------------------
        List<string> admissionControls = new();
        if (localGguf)
        {
            admissionControls.Add("For llama.cpp, cap -np to the real player/session count; continuous batching is useful only while p95 companion latency stays inside HOT_PATH.md budgets.");
        }

        if (supportsVision)
        {
            admissionControls.Add("image_count<=1 by default; raise only for explicit proof replay or multi-frame review.");
            admissionControls.Add("image_embeds require a local precomputed source, exact model/projector shape proof, and no player-supplied tensor payloads.");
        }

        if (supportsVideo)
        {
            admissionControls.Add("video_count<=1 and frame_count<=8 by default; distill video findings before feeding the text lanes.");
        }

        if (supportsAudioInput)
        {
            admissionControls.Add("audio_count<=1 and <=30 seconds by default; prefer cascaded ASR for ordinary player speech.");
            if (isGemmaAudio)
            {
                admissionControls.Add($"For {gemmaAudioFamily} audio-in, reject or chunk clips when the {gemmaAudioTokenRate}-tokens-per-second audio estimate plus text prompt exceeds the route token budget.");
            }

            admissionControls.Add("audio_embeds require a trusted encoder lane and stay off the normal player-speech path until malformed-shape isolation is proven.");
        }

        if (supportsAudioOutput)
        {
            admissionControls.Add("Realtime audio runs on a separate opt-in lane and must never block text reply delivery.");
            admissionControls.Add("Speech synthesis speed controls stay proof-only; reject promotion if non-1.0 speed values create artifacts, increase TTS p95, or degrade native playback receipts.");
        }

        if (supportsSpeculativeDecoding && multimodal)
        {
            admissionControls.Add("Keep speculative decoding disabled for screenshot, video, and audio routes until modality-isolated PalLLM replay proves lower p95 latency without worse parse stability or fallback activation.");
        }

        if (supportsModelNativeMtp && multimodal)
        {
            admissionControls.Add("Do not co-schedule model-native MTP with mmproj, libmtmd, image, or audio workloads on one local server unless a same-process PalLLM replay proves no stalls, loops, OOM, or parser regressions.");
        }

        if (isQwen36)
        {
            admissionControls.Add("For Qwen3.6, cap ordinary companion prompts to measured live budgets; 262,144+ local, 1,010,000-token extended, and reduced-context GGUF profiles require separate proof.");
        }

        if (admissionControls.Count == 0)
        {
            admissionControls.Add("Token and request-size budgets stay with the existing PalLLM inference client caps.");
        }

        // ---- SecurityControls ---------------------------------------------
        List<string> securityControls =
        [
            "Bind the local llama-server to loopback or a trusted LAN interface; do not expose raw inference ports publicly.",
            "Do not redistribute downloaded model weights, quant files, adapters, mmproj files, or drafter weights with PalLLM unless their license, base-model lineage, immutable revision/hash, and allowed redistribution terms are recorded in release evidence.",
        ];
        if (localGguf)
        {
            securityControls.Add("Keep llama.cpp --host at 127.0.0.1 by default; if exposed beyond loopback, require --api-key and keep --webui-mcp-proxy, --tools, /props, and /slots behind an admin-only surface.");
        }

        if (cloudEscape)
        {
            securityControls.Add("On the cloud escape lane, store the API key only via PalLLM:Auth/Inference config or an environment variable, never in committed files; treat the cloud provider as outside the air-gap boundary and keep player text minimal.");
        }

        if (multimodal)
        {
            securityControls.Add("Keep the llama-server media-domain allowlist tight: prefer PalLLM-owned local base64 media, disable remote media URLs by default, and reject redirects when remote URLs are enabled.");
            securityControls.Add("Treat remote image_url, audio_url, and video_url as SSRF-sensitive opt-in paths: deny localhost, private ranges, link-local ranges, IP literals, and redirects unless an operator-owned proxy has already sanitized them.");
        }

        if (supportsAudioInput)
        {
            securityControls.Add("Do not let audio_url or video_url fetch arbitrary URLs on the player path; prefer local bytes or an explicit allowed local media path.");
        }

        // ---- VerificationChecks -------------------------------------------
        List<string> verificationChecks =
        [
            "Probe /v1/models and confirm the configured model id is present before promoting the lane.",
            "Replay representative PalLLM chat turns and record p50/p95 latency, TTFT when available, failure rate, and fallback activation before changing defaults.",
            "Replay each PalLLM route class separately before promoting cache, scheduler, or speculation settings: companion chat, vision describe, world-state extraction, screenshot proof loops, audio/ASR, and long proof/docs traffic.",
            "Capture a primary-source capability receipt before promotion: model-card or vendor-doc revision, local /v1/models identity, supported text/image/audio/tool-call/speculation fields, positive canaries for claimed modalities, and negative canaries for unsupported modalities.",
            "Capture a model-artifact provenance receipt before promotion: model card license, base_model or adapter relation, immutable revision/commit or local SHA-256, weight format, runtime and tokenizer revisions, trust_remote_code status, and whether redistribution is allowed.",
        ];

        // ---- PromotionReceipts --------------------------------------------
        List<string> promotionReceipts =
        [
            "Route replay receipt: preserve PalLLM operation and budget labels for companion chat, vision describe, world-state extraction, screenshot proof loops, audio/ASR, and long proof/docs lanes.",
            "Runtime capability handshake receipt: model-card or vendor-doc revision, served model id from /v1/models, runtime version, enabled launch flags, positive canaries for claimed capabilities, and negative canaries for unsupported modalities.",
            "Model artifact provenance receipt: source URL or local path, immutable revision/commit or local SHA-256, model-card license metadata, base-model/adapter relation, weight format, safetensors/pickle and trust_remote_code status, runtime/tokenizer revisions, and redistribution decision.",
            "Publication receipt: confirm model weights, adapters, mmproj files, prompts, voice samples, and pack assets are either excluded from the PalLLM package or have redistribution permission recorded before release.",
        ];

        // ---- MetricReceipts -----------------------------------------------
        List<string> metricReceipts =
        [
            "PalLLM /metrics: palllm_chat_duration_seconds, palllm_inference_recent_window_status, palllm_inference_lane_status, and palllm_fallback_reply_total.",
            "PalLLM route replay receipt: preserve operation/budget labels from palllm_inference_lane_status and paired p50/p95 evidence for chat, vision, audio/ASR, and proof/docs lanes instead of collapsing them into one model score.",
            "Runtime capability handshake receipt: primary-source model-card or vendor-doc revision, served model id from /v1/models, runtime version, enabled launch flags, positive modality/tool/speculation canary ids, and negative unsupported-modality canary ids.",
            "Model artifact provenance receipt: source URL or local path, immutable revision/hash, license metadata, base-model/adapter relation, weight format, safetensors/pickle/trust-remote-code status, runtime version, and redistribution decision.",
        ];

        if (supportsStructuredOutputs)
        {
            promotionReceipts.Add("Structured-output portability receipt: schema name and digest, route class, served model id, request shape (response_format, structured_outputs, or grammar), grammar/backend id, exact parse success, schema-validation success, refusal or empty-output handling, p95 latency, token usage, and fallback counters.");
            metricReceipts.Add("Structured-output proof receipts: schema digest, request-shape id, exact-parse success rate, schema-validation success rate, invalid-output retry count, token usage, p95 latency, and route-labeled fallback counters.");
            verificationChecks.Add("Run a schema-echo portability canary: one required object, one enum, one bounded array, one deliberate violation prompt, and one changed-schema digest; reject promotion if json_object-only mode passes while json_schema validation fails.");
            verificationChecks.Add("Run repeated JSON-schema and tool-call qualification; reject promotion if exact parse success is not stable.");
            verificationChecks.Add("Keep app-side schema validation authoritative even when the server advertises constrained decoding; classify failures as invalid JSON, schema mismatch, unsupported schema keyword, refusal or empty output, semantic mismatch, or timeout.");
        }

        if (supportsToolCalls)
        {
            verificationChecks.Add("For strict tool-call canaries, replay with InferencePrompt.Tools plus InferencePrompt.ToolChoice, confirm the outgoing tools/tool_choice request shape is accepted, archive the returned tool_calls receipt, and require a deterministic fallback path when content is empty or malformed.");
            verificationChecks.Add("Prove PalLLM:Inference:ParallelToolCalls=false forwards parallel_tool_calls=false and yields zero or one action/tool call on PalLLM directive routes before allowing any parallel fan-out experiment.");
        }

        if (localGguf)
        {
            promotionReceipts.Add("GGUF prompt/state-cache receipt: same-slot second-turn replay, slot save/restore timing, tokenizer/chat-template/context/adapters/server-build identity, and no unexpected full prompt re-processing before cache promotion.");
            metricReceipts.Add("llama.cpp /metrics or logs: prompt-cache reuse, slot count, -cram pressure, active KV memory, accepted/generated token statistics, forced full prompt re-processing warnings, and p95 companion latency.");
            metricReceipts.Add("llama.cpp state-cache canary: same-slot second-turn replay, slot save/restore timing, tokenizer/chat-template/context/adapters/server-build receipt, and no unexpected full-prefill log lines.");
            verificationChecks.Add("For llama.cpp GGUF lanes, record /health, /v1/models, /metrics or server logs for prompt-cache reuse, slot count, -cram pressure, active KV memory, forced full prompt re-processing warnings, and p95 latency before raising -np or CacheRamMiB.");
            verificationChecks.Add("Run a llama.cpp state-cache canary before host prompt-cache promotion: same PalLLM prefix twice on the same slot should avoid unexpected full prefill, then a changed chat template, context size, adapter id, or model file should invalidate instead of reusing stale state.");
            verificationChecks.Add("If using llama.cpp -ctk/-ctv quantized KV, --sleep-idle-seconds, or context above the model default, replay PalLLM strict JSON, tool-call, companion, and long-context turns against default f16 KV first.");
        }

        if (cloudEscape)
        {
            promotionReceipts.Add("Cloud escape promotion receipt: provider name, served model id from /v1/models, endpoint region/base URL, API-key storage location, retention posture, p95 latency, and deterministic local-fallback behavior when the network is unavailable.");
            verificationChecks.Add("For the cloud escape lane, prove /v1/models lists the configured model, /v1/chat/completions handles PalLLM replay traffic, and PalLLM falls back to the deterministic reply when the cloud endpoint is unreachable before promotion.");
        }

        if (isGemma3n)
        {
            verificationChecks.Add("For Gemma 3n, compare text-only parameter skipping and PLE cache behavior against full multimodal loading on the same PalLLM turns; record memory, load latency, and fallback behavior.");
        }

        if (isGemma4 && supportsAudioInput)
        {
            verificationChecks.Add("For Gemma 4 audio-in, verify the exact size/runtime accepts input_audio and budget 25 audio tokens per second before routing player speech.");
        }

        if (isQwenOmni)
        {
            verificationChecks.Add("For Qwen Omni, prove text-only, text+audio-in, and text+audio-out requests independently; record whether audio choices include a text mirror and whether speaker configuration is stable.");
        }

        if (supportsSpeculativeDecoding)
        {
            promotionReceipts.Add("Speculation promotion receipt: no-spec baseline, selected speculative mode, accepted/proposed token ratio, TTFT/ITL, exact JSON/tool-call parse success, and fallback counters for each promoted route class.");
            verificationChecks.Add("Compare baseline vs speculative latency, accepted/proposed token ratio, and parse stability with the exact model, quant, and server version; disable speculation if structured-output reliability drops.");
            verificationChecks.Add("Keep strict JSON, tool-call, judge, and save-replay routes no-spec until each route has its own repeated-run proof.");
            if (multimodal)
            {
                verificationChecks.Add("Run modality-isolated speculative replay: text-only chat, screenshot/image, and audio cases each need no-spec and n-gram comparisons before a multimodal lane can be promoted.");
            }

            if (localGguf)
            {
                verificationChecks.Add("For llama.cpp --spec-type ngram-simple, ngram-mod, draft-simple, or draft-mtp lanes, capture accepted/generated token statistics and disable the lane if p95 latency or exact parse success regresses.");
            }
        }

        if (supportsModelNativeMtp)
        {
            verificationChecks.Add("For Qwen3.6 or Gemma 4 MTP, compare the model-native drafter against n-gram or no-spec baselines on the same PalLLM replay set and record acceptance rate, TTFT, ITL, fallback behavior, and JSON/tool-call parse stability before promotion.");
            if (isQwen36)
            {
                verificationChecks.Add("For Qwen3.6 MTP-1 latency mode, include a --no-enable-prefix-caching replay and compare it against the normal prefix-cache lane before changing any player-facing default.");
            }
            else if (isGemma4)
            {
                verificationChecks.Add("For Gemma 4 MTP, reject generic draft_model wiring for assistant checkpoints; record assistant checkpoint id/hash, method=mtp, token depth, acceptance rate, prefix-cache-disabled benchmark results, normal-cache replay, and fallback behavior before promotion.");
            }
        }

        if (isGguf && multimodal)
        {
            verificationChecks.Add("Smoke llama.cpp libmtmd with a matching mmproj through /v1/chat/completions before routing Palworld screenshots to the GGUF lane.");
        }

        if (multimodal)
        {
            promotionReceipts.Add("Multimodal media-admission receipt: local data URL accepted, remote media URL disabled by default, allowed-domain fetch logged, redirect probe blocked, and localhost/private/link-local/IP-literal probes rejected before model execution.");
            verificationChecks.Add("Warm media UUIDs with full local media bytes first, then verify uuid-only replay only after the same server process has a cache hit.");
            verificationChecks.Add("For remote-media profiles, run a negative SSRF replay: localhost, RFC1918, link-local, IP-literal, and redirect-to-private media URLs must be blocked before any PalLLM screenshot or audio URL lane is promoted.");
            verificationChecks.Add("For InferencePrompt.UserContent canaries, replay text-only, image_url, and input_audio content parts on the exact endpoint and record accepted request shape, media byte caps, p95 latency, parse stability, and fallback counters before promoting a multimodal lane.");
        }

        if (supportsAudioInput)
        {
            promotionReceipts.Add("Audio-in promotion receipt: normalized mono 16 kHz clip hash, duration cap, privacy retention decision, native audio-in vs cascaded ASR comparison, and deterministic text fallback behavior.");
            if (isGemmaAudio)
            {
                promotionReceipts.Add($"{gemmaAudioFamily} audio budget receipt: normalized duration, {gemmaAudioTokenRate} audio-token-per-second estimate, route token budget/headroom, cascaded ASR comparison, and deterministic text fallback behavior.");
                verificationChecks.Add("For Gemma audio-token budget proof, replay native audio-in and cascaded ASR with the same 30-second-or-less clip and reject promotion if token headroom, latency, parse stability, or fallback behavior regresses.");
            }

            verificationChecks.Add("For player speech, compare cascaded ASR-to-text latency, transcript quality, and privacy retention against native audio-in before changing the default input lane.");
        }

        if (supportsAudioOutput)
        {
            promotionReceipts.Add("Realtime audio promotion receipt: isolated llama-server talker/code2wav profile, text mirror, speaker/config stability, stalled-audio fallback, and proof that text chat still returns while audio is unhealthy.");
            verificationChecks.Add("For audio-output canaries, replay with and without InferencePrompt.Modalities / Audio on the exact endpoint and archive returned InferenceResult.AudioJson alongside text mirror, latency, response-size, and fallback counters.");
            verificationChecks.Add("Smoke /v1/audio/speech on the llama.cpp talker/code2wav endpoint, verify output MIME/playback receipts, and prove /api/chat still returns while speech synthesis is stalled or unhealthy.");
        }

        return new ModelServingProfile(
            ProfileId: profileId,
            RequestProtocol: requestProtocol,
            PreferredRuntime: preferredRuntime,
            StartupHints: startupHints.Distinct(StringComparer.Ordinal).ToArray(),
            RequestHints: requestHints.ToArray(),
            CacheHints: cacheHints.ToArray(),
            AdmissionControls: admissionControls.ToArray(),
            SecurityControls: securityControls.ToArray(),
            PromotionReceipts: promotionReceipts.Distinct(StringComparer.Ordinal).ToArray(),
            MetricReceipts: metricReceipts.Distinct(StringComparer.Ordinal).ToArray(),
            VerificationChecks: verificationChecks.ToArray());
    }

    private static string[] BuildServingOptimizations(
        bool supportsStructuredOutputs,
        bool supportsToolCalls,
        bool supportsSpeculativeDecoding,
        bool supportsVision,
        bool supportsVideo,
        bool supportsAudioInput,
        bool supportsAudioOutput)
    {
        List<string> optimizations =
        [
            "Keep the PalLLM system prompt, character profile, and world-state prefix stable so the llama-server prefix cache can hit.",
            "Keep llama.cpp continuous batching (-np) sized to the real player/session count so long Palworld proof or docs-sync prompts do not stall short companion turns.",
        ];

        if (supportsStructuredOutputs)
        {
            optimizations.Add("Use response_format json_schema for world-state, tool-call, and proof-packet JSON whenever the llama-server grammar conversion accepts it.");
            optimizations.Add("Use route/model/schema-digest keyed proof and cache identities so structured-output wins never bleed across PalLLM schemas.");
        }

        if (supportsToolCalls)
        {
            optimizations.Add("Treat tool calls as proposals and disable speculative decoding for strict tool-call or JSON-schema qualification runs.");
        }

        if (supportsSpeculativeDecoding)
        {
            optimizations.Add("Enable speculative decoding only after the exact model, quant, and draft pairing pass PalLLM's repeated-run qualification suite.");
            optimizations.Add("Prefer n-gram or suffix speculation for repetitive low-QPS text turns before adding a separate draft model to the operator footprint.");
        }

        if (supportsVision || supportsVideo || supportsAudioInput)
        {
            optimizations.Add("Use stable media UUIDs for repeated screenshots, proof replays, or audio clips when the llama-server build exposes media caching.");
            optimizations.Add("Cap media-per-prompt counts and prefer local base64 payloads; allow remote media only through explicit server-side allowlists.");
            if (supportsSpeculativeDecoding)
            {
                optimizations.Add("Keep speculative decoding route-scoped: text-only gains do not qualify screenshot, video, or audio lanes until each modality passes PalLLM replay proof.");
            }
        }

        if (supportsAudioOutput)
        {
            optimizations.Add("Keep realtime audio on a separate opt-in lane so text chat and deterministic fallback stay responsive when the voice server stalls.");
        }

        return optimizations.ToArray();
    }

    private static string[] BuildRuntimeGuards(
        bool supportsVision,
        bool supportsVideo,
        bool supportsAudioInput,
        bool supportsAudioOutput,
        bool supportsStructuredOutputs)
    {
        List<string> guards =
        [
            "Keep deterministic fallback enabled; a model capability must never become a hard dependency for POST /api/chat.",
        ];

        if (supportsStructuredOutputs)
        {
            guards.Add("Validate strict JSON with deterministic parsers before promotion; model confidence is not a validator.");
            guards.Add("Treat constrained decoding as an upstream hint, not the contract; PalLLM validators and deterministic fallback remain the authority when schema output is malformed or semantically wrong.");
        }

        if (supportsVision || supportsVideo)
        {
            guards.Add("Keep Palworld screenshot ingress bounded and local by default; do not let remote media fetches bypass the air-gap posture.");
        }

        if (supportsAudioInput || supportsAudioOutput)
        {
            guards.Add("Keep audio-in and realtime voice opt-in until the operator has explicit latency, privacy, and device-loop proof.");
        }

        return guards.ToArray();
    }

    private static ModelAuthorityProfile BuildAuthorityProfile(bool isSparseMoe) =>
        isSparseMoe
            ? new(
                MayDraftChanges: true,
                MayBePrimaryReviewer: false,
                MayRecommendMerge: false,
                MayExecuteLowRiskToolLoops: true,
                MayDraftHighRiskToolPlans: false,
                MayExecuteHighRiskTools: false)
            : new(
                MayDraftChanges: true,
                MayBePrimaryReviewer: true,
                MayRecommendMerge: true,
                MayExecuteLowRiskToolLoops: false,
                MayDraftHighRiskToolPlans: true,
                MayExecuteHighRiskTools: false);

    private static ModelHardwareProfile BuildHardwareProfile(ModelHardwareHints hints)
    {
        bool cpuOnly = hints.CpuOnly;
        double vramGb = Math.Max(0, hints.VramGb ?? 0);
        double ramGb = Math.Max(0, hints.RamGb ?? 0);
        double unifiedMemoryGb = Math.Max(0, hints.UnifiedMemoryGb ?? 0);

        string classId;
        string summary;
        bool canKeepTwoSpecialistsWarm;
        bool preferSequentialBatonPassing;

        if (cpuOnly)
        {
            classId = "cpu-only";
            summary = "CPU-only or intentionally accelerator-free setup. Favor a single resident model and wake the second only at review checkpoints.";
            canKeepTwoSpecialistsWarm = false;
            preferSequentialBatonPassing = true;
        }
        else if (unifiedMemoryGb >= 128 || vramGb >= 48)
        {
            classId = "workstation";
            summary = "Workstation-class memory budget. Separate fast and deliberate specialists can stay hot and work in parallel.";
            canKeepTwoSpecialistsWarm = true;
            preferSequentialBatonPassing = false;
        }
        else if (unifiedMemoryGb >= 64 || vramGb >= 24)
        {
            classId = "prosumer";
            summary = "Prosumer single-accelerator or mid-size unified-memory setup. Keep the fast model resident and bring the dense reviewer in at key boundaries.";
            canKeepTwoSpecialistsWarm = hints.PreferParallel && (unifiedMemoryGb >= 96 || vramGb >= 32);
            preferSequentialBatonPassing = !canKeepTwoSpecialistsWarm;
        }
        else if (vramGb >= 8 || ramGb >= 64 || unifiedMemoryGb >= 32)
        {
            classId = "hybrid-offload";
            summary = "Hybrid GPU+RAM offload setup. Sequential baton passing is usually better than trying to keep both large models resident.";
            canKeepTwoSpecialistsWarm = false;
            preferSequentialBatonPassing = true;
        }
        else
        {
            classId = "edge";
            summary = "Edge or low-memory setup. Keep the fast scout as the resident lane and only load the deliberate reviewer for narrow audits.";
            canKeepTwoSpecialistsWarm = false;
            preferSequentialBatonPassing = true;
        }

        return new ModelHardwareProfile(
            ClassId: classId,
            Summary: summary,
            VramGb: hints.VramGb,
            RamGb: hints.RamGb,
            UnifiedMemoryGb: hints.UnifiedMemoryGb,
            CpuOnly: hints.CpuOnly,
            PreferParallel: hints.PreferParallel,
            CanKeepTwoSpecialistsWarm: canKeepTwoSpecialistsWarm,
            PreferSequentialBatonPassing: preferSequentialBatonPassing);
    }
}
