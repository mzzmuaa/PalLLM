using PalLLM.Domain.Configuration;
using PalLLM.Domain.Runtime;

namespace PalLLM.Sidecar;

internal static class PalLlmPromotionRoutes
{
    internal static void MapPalLlmPromotionRoutes(this RouteGroupBuilder api)
    {
        // Promotion-ledger observations: operators + AI callers post one
        // observation per task-class run (success / disagreement-block /
        // validator-fail / human-override) and read back per-task summaries
        // that flag which patterns are stable enough to hard-code into
        // deterministic logic. Deterministic + in-memory + bounded per task.
        api.MapPost("/promotion/record", IResult (
            PromotionRecordRequest request,
            PromotionLedger ledger) =>
        {
            PromotionRecordRequest r = request ?? new PromotionRecordRequest();
            if (string.IsNullOrWhiteSpace(r.TaskClass))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing task class",
                    detail: "A non-empty TaskClass is required before an observation can be recorded.");
            }

            if (string.IsNullOrWhiteSpace(r.PatternId))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing pattern id",
                    detail: "A non-empty PatternId is required before an observation can be recorded.");
            }

            if (string.IsNullOrWhiteSpace(r.Outcome))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing promotion outcome",
                    detail: "A non-empty Outcome is required before an observation can be recorded.");
            }

            if (!PromotionLedger.TryNormalizeOutcome(r.Outcome, out _))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid promotion outcome",
                    detail: $"Outcome must be one of: {string.Join(", ", PromotionLedger.AllowedOutcomeValues)}.");
            }

            PromotionObservation observation = ledger.Record(
                taskClass: r.TaskClass,
                patternId: r.PatternId,
                outcome: r.Outcome,
                note: r.Note);
            return TypedResults.Ok(observation);
        })
            .WithName("PostPromotionObservation")
            .WithTags("Inspection")
            .WithSummary("Record one observation against the hard-code promotion ledger.");

        api.MapGet("/promotion/summary", IResult (PromotionLedger ledger) =>
        {
            PromotionSummary summary = ledger.Snapshot();
            return TypedResults.Ok(summary);
        })
            .WithName("GetPromotionSummary")
            .WithTags("Inspection")
            .WithSummary("Per-task-class promotion summary; flags patterns stable enough to hard-code.");

        // Actionable suggestions for every promotion candidate: concrete target
        // files, one-sentence recipes, rollback paths, and a ProofPacket per
        // suggestion so the recommendation itself has audit provenance.
        api.MapGet("/promotion/suggestions", IResult (PromotionLedger ledger) =>
        {
            PromotionSummary summary = ledger.Snapshot();
            PromotionSuggestionSet suggestions = PromotionSuggestionBuilder.Build(summary);
            return TypedResults.Ok(suggestions);
        })
            .WithName("GetPromotionSuggestions")
            .WithTags("Inspection")
            .WithSummary("Hard-code suggestions for every promotion candidate: target file, suggested change, evidence summary, rollback path, ProofPacket.");

        // Concrete, editor-ready change template for a specific candidate.
        // Request body names a (TaskClass, PatternId) pair; the server looks
        // up the matching candidate in the live ledger and returns a preview
        // with file path, before-context anchors, after-code template,
        // safety warnings, rollback command, and ProofPacket provenance.
        // Deterministic — no file reads, no inference call.
        api.MapPost("/promotion/apply/preview", IResult (
            PromotionApplyPreviewRequest request,
            PromotionLedger ledger) =>
        {
            PromotionApplyPreviewRequest r = request ?? new PromotionApplyPreviewRequest();
            if (string.IsNullOrWhiteSpace(r.TaskClass))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing task class",
                    detail: "A non-empty TaskClass is required so the preview builder can find the matching candidate.");
            }

            PromotionSummary summary = ledger.Snapshot();
            PromotionTaskSummary? task = summary.Tasks.FirstOrDefault(t =>
                string.Equals(t.TaskClass, r.TaskClass, StringComparison.Ordinal));
            if (task is null)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Task class not in ledger",
                    detail: $"No observations recorded against task class '{r.TaskClass}'. Record at least one observation before requesting a preview.");
            }
            if (!task.IsPromotionCandidate)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Task is not a promotion candidate",
                    detail: task.Recommendation);
            }

            // If the caller supplied a specific PatternId, pin it; otherwise
            // use the task's most-common pattern as a reasonable default.
            string patternId = string.IsNullOrWhiteSpace(r.PatternId)
                ? (task.MostCommonPatternId ?? "(unspecified)")
                : r.PatternId.Trim();

            // Build the suggestion once and feed it into the preview builder.
            // Since the suggestion builder's ResolveTarget is keyed on task class,
            // we derive a synthesised task summary with the caller-pinned pattern
            // id so the downstream templates reference the right pattern.
            PromotionTaskSummary pinnedTask = task with { MostCommonPatternId = patternId };
            PromotionSuggestion suggestion = PromotionSuggestionBuilder.BuildForTask(pinnedTask, summary.CapturedAtUtc);
            PromotionApplyPreview preview = PromotionApplyPreviewBuilder.Build(suggestion);
            return TypedResults.Ok(preview);
        })
            .WithName("PostPromotionApplyPreview")
            .WithTags("Inspection")
            .WithSummary("Build an editor-ready change template for a specific promotion candidate.");

        // Pass 24 — "apply" verb for the promotion pipeline. Persists the
        // Pass-14 preview as a durable staging triple (template + rollback +
        // provenance packet) under the configured staging root. NEVER
        // mutates source code. Behind PalLLM:PromotionApply:AllowApply=false
        // by default — flip only in environments where a human reviewer will
        // cherry-pick the staged artifacts into real code. 403 when the flag
        // is off; 404/409 for the same reasons as /apply/preview; 200 + the
        // structured PromotionApplyResult otherwise.
        api.MapPost("/promotion/apply", IResult (
            PromotionApplyRequest request,
            PromotionLedger ledger,
            PalLlmOptions options) =>
        {
            PromotionApplyRequest r = request ?? new PromotionApplyRequest();
            if (string.IsNullOrWhiteSpace(r.TaskClass))
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing task class",
                    detail: "A non-empty TaskClass is required so the apply verb can find the matching candidate.");
            }

            if (!options.PromotionApply.AllowApply)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status403Forbidden,
                    title: "Promotion apply is disabled",
                    detail: "Set PalLLM:PromotionApply:AllowApply=true in configuration to allow the apply verb to persist staging artifacts. Apply never mutates source code in place — it only writes to the configured staging root.");
            }

            PromotionSummary applySummary = ledger.Snapshot();
            PromotionTaskSummary? applyTask = applySummary.Tasks.FirstOrDefault(t =>
                string.Equals(t.TaskClass, r.TaskClass, StringComparison.Ordinal));
            if (applyTask is null)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Task class not in ledger",
                    detail: $"No observations recorded against task class '{r.TaskClass}'.");
            }
            if (!applyTask.IsPromotionCandidate)
            {
                return TypedResults.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Task is not a promotion candidate",
                    detail: applyTask.Recommendation);
            }

            string applyPatternId = string.IsNullOrWhiteSpace(r.PatternId)
                ? (applyTask.MostCommonPatternId ?? "(unspecified)")
                : r.PatternId!.Trim();
            PromotionTaskSummary pinnedApply = applyTask with { MostCommonPatternId = applyPatternId };
            PromotionSuggestion applySuggestion = PromotionSuggestionBuilder.BuildForTask(pinnedApply, applySummary.CapturedAtUtc);
            PromotionApplyPreview applyPreview = PromotionApplyPreviewBuilder.Build(applySuggestion);

            PromotionApplyResult applyResult = PromotionApplier.Apply(applyPreview, options);
            return TypedResults.Ok(applyResult);
        })
            .WithName("PostPromotionApply")
            .WithTags("Inspection")
            .WithSummary("Persist a promotion candidate as a staging-only template + rollback + provenance triple under the configured staging root. Never mutates source code. Gated by PalLLM:PromotionApply:AllowApply.");
    }
}
