using PalLLM.Domain.Configuration;
using PalLLM.Domain.Inference;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmConversationRoutes
{
    internal static void MapPalLlmPartyChatRoute(this RouteGroupBuilder api)
    {
        // Pass 34 / C1 — party chat. Fans out a single utterance across
        // multiple character ids in order. Each per-character turn runs
        // through the existing ChatAsync machinery (so the task-aware
        // execution profile, Pass-8 planner, rate limiting, and deterministic
        // fallback all apply per-turn). Threaded mode seeds each turn with a
        // brief mention of earlier replies so a conversation forms; default
        // off so each character replies independently.
        api.MapPost("/chat/party", async (
            PartyChatRequest request,
            PalLlmRuntime runtime,
            CancellationToken cancellationToken) =>
        {
            PartyChatRequest r = request ?? new PartyChatRequest();
            if (r.CharacterIds is null || r.CharacterIds.Count == 0)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Party chat requires at least one CharacterId",
                    detail: "Send a non-empty CharacterIds array so the dispatcher knows which companions to fan out across.");
            }
            if (string.IsNullOrWhiteSpace(r.UserMessage))
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "UserMessage is required",
                    detail: "Party chat must carry a non-empty UserMessage just like /api/chat.");
            }

            string partyId = "party-" + Guid.NewGuid().ToString("N")[..12];
            var turns = new List<PartyChatTurn>(r.CharacterIds.Count);
            var earlierSummary = new System.Text.StringBuilder();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int fallbackCount = 0;

            for (int i = 0; i < r.CharacterIds.Count; i++)
            {
                int cid = r.CharacterIds[i];
                string? cname = (r.CharacterNames is not null && r.CharacterNames.Count > i)
                    ? r.CharacterNames[i]
                    : null;

                string perTurnMessage = r.UserMessage;
                if (r.Threaded && earlierSummary.Length > 0)
                {
                    perTurnMessage = r.UserMessage
                        + "\n\n[Party context — earlier replies this turn]\n"
                        + earlierSummary;
                }

                var turnReq = new ChatRequest
                {
                    CharacterId = cid,
                    CharacterName = cname,
                    TaskTag = r.TaskTag,
                    UserMessage = perTurnMessage,
                    Temperature = r.Temperature,
                    RequestId = partyId + "-" + i,
                };
                ChatResponse turnResp = await runtime.ChatAsync(turnReq, cancellationToken).ConfigureAwait(false);

                turns.Add(new PartyChatTurn(
                    OrderIndex: i,
                    CharacterId: cid,
                    CharacterName: string.IsNullOrWhiteSpace(cname) ? turnResp.CharacterName : cname,
                    Response: turnResp));

                if (turnResp.UsedFallback) { fallbackCount++; }

                if (r.Threaded)
                {
                    string who = string.IsNullOrWhiteSpace(turnResp.CharacterName) ? $"#{cid}" : turnResp.CharacterName;
                    string reply = turnResp.AssistantMessage ?? string.Empty;
                    if (reply.Length > 240) { reply = reply[..240] + "…"; }
                    earlierSummary.Append(who).Append(": ").AppendLine(reply);
                }
            }

            sw.Stop();

            var response = new PartyChatResponse
            {
                PartyId = partyId,
                Turns = turns,
                Threaded = r.Threaded,
                TotalLatencyMs = sw.ElapsedMilliseconds,
                FallbackTurnCount = fallbackCount,
                CapturedAtUtc = DateTimeOffset.UtcNow,
            };
            return Results.Ok(response);
        })
            .ValidatePalRequest<PartyChatRequest>()
            .RequireRateLimiting("chat-heavy")
            .WithName("PostChatParty")
            .WithTags("Conversation")
            .WithSummary("Pass 34 – fan a single utterance out across multiple character ids. Each per-character reply runs through the full ChatAsync pipeline.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .WithRequestTimeout("chat-timeout");
    }

    internal static void MapPalLlmChatTurnRoutes(this RouteGroupBuilder api)
    {
        api.MapPost("/chat", async (
            ChatRequest request,
            PalLlmRuntime runtime,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            // runtime.ChatAsync is designed to swallow inference / fallback /
            // rate-limit failures and return a ChatResponse with
            // Success=false. Wrap it anyway so an unexpected exception (OOM,
            // JSON binding fault that escapes ValidatePalRequest, domain-level
            // ArgumentException) becomes a structured Problem instead of an
            // opaque ASP.NET 500. Parity with the streaming sibling
            // (/chat/stream) which already catches and emits a structured
            // error payload before closing the SSE stream.
            try
            {
                ChatResponse response = await runtime.ChatAsync(request, cancellationToken);
                return Results.Ok(response);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Client disconnected. ASP.NET handles the response framing;
                // rethrow so the request-completed log line records the cancel
                // rather than a 500.
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat turn aborted before a reply could be returned.");
                return Results.Problem(
                    title: "Chat turn failed.",
                    detail: "The chat orchestration pipeline raised an unexpected internal error. Re-run the request; if the issue persists, check the sidecar log for the matching warning entry.",
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        })
            .ValidatePalRequest<ChatRequest>()
            .RequireRateLimiting("chat-heavy")
            .WithName("PostChatTurn")
            .WithTags("Conversation")
            .WithSummary("Run a single chat turn through the PalLLM orchestration pipeline.")
            .Produces<ChatResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .ProducesProblem(StatusCodes.Status500InternalServerError)
            .WithRequestTimeout("chat-timeout");

        // Streaming variant of /chat. Emits Server-Sent Events so a web UI or
        // AI client sees progress phases (`started` -> `phase` -> ... ->
        // `final`) instead of a silent wait before the final payload arrives.
        // The final event always carries the complete ChatResponse JSON, so a
        // client that only cares about the final answer can still consume this
        // endpoint and ignore the intermediate phases. /api/chat stays
        // unchanged for clients that prefer a single synchronous request.
        api.MapPost("/chat/stream", async (
            ChatRequest request,
            PalLlmRuntime runtime,
            PalLlmOptions options,
            HttpContext context,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers["Content-Type"] = "text/event-stream";
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["X-Accel-Buffering"] = "no"; // Caddy / nginx / reverse-proxy hint.

            string requestId = Guid.NewGuid().ToString("N")[..12];
            await ChatStreamWriter.EmitAsync(
                context.Response,
                "started",
                new ChatStreamStartedPayload(requestId),
                PalLlmJsonSerializerContext.Default.ChatStreamStartedPayload,
                cancellationToken);

            ChatResponse? response = null;
            int timeoutSeconds = Math.Max(1, options.Http.ChatRequestTimeoutSeconds);
            using var streamTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            streamTimeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await ChatStreamWriter.EmitAsync(
                    context.Response,
                    "phase",
                    new ChatStreamPhasePayload("ingress", "received request"),
                    PalLlmJsonSerializerContext.Default.ChatStreamPhasePayload,
                    cancellationToken);
                await ChatStreamWriter.EmitAsync(
                    context.Response,
                    "phase",
                    new ChatStreamPhasePayload("orchestration", "routing through PalLlmRuntime"),
                    PalLlmJsonSerializerContext.Default.ChatStreamPhasePayload,
                    cancellationToken);

                response = await runtime.ChatAsync(request, streamTimeout.Token);

                // Pass 23: emit structured per-channel events BEFORE the final
                // synchronous payload so streaming clients can render reply
                // text, presentation cues, speech, and action intent as they
                // arrive instead of re-parsing the full ChatResponse. Clients
                // that only care about the final answer still receive the
                // complete ChatResponse as the trailing `final` event.
                await ChatStreamWriter.EmitAsync(
                    context.Response,
                    "phase",
                    new ChatStreamFinalPrepPayload(
                        "final-prep",
                        response.UsedFallback
                        ? $"fallback strategy '{response.FallbackStrategy}' produced the reply"
                        : "live inference produced the reply",
                        response.InferredTaskKind,
                        response.CooperationPattern,
                        response.DispatchMode,
                        response.DispatchedRoleChain),
                    PalLlmJsonSerializerContext.Default.ChatStreamFinalPrepPayload,
                    cancellationToken);

                // Token-level event framing. Real upstream-token SSE requires
                // the inference provider to support it; Pass 23 emits word
                // chunks from the completed reply at a cadence fast enough to
                // feel incremental in the dashboard + any MCP-aware client.
                // When the provider itself streams (future pass), the
                // ChatResponse-side API can stay identical while the stream
                // path swaps the chunking source.
                string replyText = response.AssistantMessage ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(replyText))
                {
                    string[] words = replyText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    int emitted = 0;
                    foreach (string word in words)
                    {
                        emitted++;
                        await ChatStreamWriter.EmitAsync(
                            context.Response,
                            "token",
                            new ChatStreamTokenPayload(
                                emitted,
                                words.Length,
                                word + (emitted == words.Length ? string.Empty : " ")),
                            PalLlmJsonSerializerContext.Default.ChatStreamTokenPayload,
                            cancellationToken);
                        // Tiny yield so the event arrives even if the consumer
                        // is reading with no buffering; skipped on
                        // cancellation.
                        if (emitted % 6 == 0 && !cancellationToken.IsCancellationRequested)
                        {
                            await context.Response.Body.FlushAsync(cancellationToken);
                        }
                    }
                }

                if (response.Presentation is not null)
                {
                    await ChatStreamWriter.EmitAsync(
                        context.Response,
                        "presentation",
                        response.Presentation,
                        PalLlmJsonSerializerContext.Default.PresentationCuePlan,
                        cancellationToken);
                }
                if (response.Speech is not null)
                {
                    await ChatStreamWriter.EmitAsync(
                        context.Response,
                        "speech",
                        response.Speech,
                        PalLlmJsonSerializerContext.Default.SpeechArtifact,
                        cancellationToken);
                }
                if (response.Action is not null)
                {
                    await ChatStreamWriter.EmitAsync(
                        context.Response,
                        "action",
                        response.Action,
                        PalLlmJsonSerializerContext.Default.ActionIntent,
                        cancellationToken);
                }

                await ChatStreamWriter.EmitAsync(
                    context.Response,
                    "final",
                    response,
                    PalLlmJsonSerializerContext.Default.ChatResponse,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Client disconnected. Nothing more to emit.
            }
            catch (OperationCanceledException) when (streamTimeout.IsCancellationRequested)
            {
                logger.LogWarning(
                    "Chat stream {RequestId} exceeded PalLLM:Http:ChatRequestTimeoutSeconds ({TimeoutSeconds}s) before the final reply was ready.",
                    requestId,
                    timeoutSeconds);
                await ChatStreamWriter.EmitAsync(
                    context.Response,
                    "error",
                    new ChatStreamErrorPayload(
                        requestId,
                        "Chat stream exceeded its configured timeout before the final reply was ready.",
                        true,
                        "request_timeout"),
                    PalLlmJsonSerializerContext.Default.ChatStreamErrorPayload,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Chat stream {RequestId} aborted before the final reply was ready.", requestId);
                await ChatStreamWriter.EmitAsync(
                    context.Response,
                    "error",
                    new ChatStreamErrorPayload(
                        requestId,
                        "Chat stream aborted before the final reply was ready.",
                        true,
                        "internal_error"),
                    PalLlmJsonSerializerContext.Default.ChatStreamErrorPayload,
                    cancellationToken);
            }
        })
            .ValidatePalRequest<ChatRequest>()
            .RequireRateLimiting("chat-heavy")
            .WithName("PostChatTurnStreaming")
            .WithTags("Conversation")
            .WithSummary("Server-Sent-Events variant of /api/chat. Emits 'started' / 'phase' / 'final' (or 'error') events so clients see progress before the final answer lands.");

        // Advisory: how would the Qwen Duo planner route THIS specific chat
        // request? Infers the DuoTaskKind from the user message (keyword
        // classifier), then calls the Pass-8 planner with the operator's risk
        // + hardware preference. Deterministic, no inference call, no runtime
        // mutation — pure "what pattern would be picked" forecast.
        api.MapPost("/chat/plan", IResult (ChatPlanRequest request, DuoOrchestratorPlanner planner, ModelRoleRegistry registry) =>
        {
            ChatPlanAdvice advice = ChatPlanAdvisor.Advise(request ?? new ChatPlanRequest(), planner, registry);
            return TypedResults.Ok(advice);
        })
            .ValidatePalRequest<ChatPlanRequest>()
            .WithName("PostChatPlanAdvice")
            .WithTags("Inspection")
            .WithSummary("Return the Duo cooperation pattern the planner would pick for a specific chat request, plus the executable role chain that would dispatch today.");
    }
}
