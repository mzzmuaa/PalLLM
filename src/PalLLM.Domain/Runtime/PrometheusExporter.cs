using System.Globalization;
using System.Text;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// Renders a <see cref="RuntimeHealth"/> snapshot as Prometheus exposition format.
/// Plain text, no client library, so the sidecar does not pick up a new dependency.
/// Counters and gauges are keyed by metric name; no per-request labels to keep
/// cardinality bounded for a single-tenant companion sidecar.
/// </summary>
public static class PrometheusExporter
{
    private static readonly IFormatProvider Invariant = CultureInfo.InvariantCulture;

    public static string Render(RuntimeHealth health) =>
        Render(health, new InferencePerformanceSnapshot());

    public static string Render(RuntimeHealth health, InferencePerformanceSnapshot inferencePerformance)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(inferencePerformance);
        var builder = new StringBuilder(2_048);

        // Counters — monotonically increasing across the life of the sidecar.
        WriteCounter(builder, "palllm_inference_success_total", "Successful inference calls.", health.InferenceSuccessCount);
        WriteCounter(builder, "palllm_inference_failure_total", "Failed inference calls (non-2xx or network).", health.InferenceFailureCount);
        WriteCounter(builder, "palllm_inference_bypass_total", "Chat requests that bypassed inference via the deterministic fast path.", health.InferenceBypassCount);
        WriteCounter(builder, "palllm_fallback_reply_total", "Chat replies served by the deterministic fallback director.", health.FallbackReplyCount);
        WriteCounter(builder, "palllm_rate_limited_total", "Chat requests diverted to fallback by the per-character rate limiter.", health.RateLimitedCount);
        WriteCounter(builder, "palllm_bridge_event_total", "Bridge events processed since startup.", health.BridgeEventCount);
        WriteCounter(builder, "palllm_bridge_boot_total", "Bridge boot heartbeats seen since startup.", health.BridgeBootCount);
        WriteCounter(builder, "palllm_vision_call_total", "Vision describe/world-state calls.", health.VisionCallCount);
        WriteCounter(builder, "palllm_vision_failure_total", "Vision calls that failed.", health.VisionFailureCount);
        WriteCounter(builder, "palllm_tts_call_total", "TTS synthesis calls (including chat-linked synthesis).", health.TtsCallCount);
        WriteCounter(builder, "palllm_tts_success_total", "TTS calls that returned usable audio.", health.TtsSuccessCount);
        WriteCounter(builder, "palllm_tts_failure_total", "TTS calls that failed when TTS was configured.", health.TtsFailureCount);
        WriteCounter(builder, "palllm_asr_call_total", "ASR transcription calls.", health.AsrCallCount);
        WriteCounter(builder, "palllm_asr_success_total", "ASR calls that returned usable transcript text.", health.AsrSuccessCount);
        WriteCounter(builder, "palllm_asr_failure_total", "ASR calls that failed when ASR was configured.", health.AsrFailureCount);
        WriteCounter(builder, "palllm_asr_endpointing_receipt_total", "ASR requests that carried client VAD / turn-close timing receipts.", health.AsrEndpointingReceiptCount);
        WriteCounter(builder, "palllm_asr_barge_in_total", "ASR endpointing receipts that marked a barge-in interruption.", health.AsrBargeInCount);
        WriteCounter(builder, "palllm_asr_endpointing_review_total", "ASR endpointing receipts whose timing metadata needs operator review.", health.AsrEndpointingReviewCount);
        WriteCounter(builder, "palllm_asr_confidence_receipt_total", "ASR responses that returned content-free logprob confidence receipts.", health.AsrConfidenceReceiptCount);
        WriteCounter(builder, "palllm_asr_confidence_review_total", "ASR confidence receipts that include low-confidence token evidence.", health.AsrConfidenceReviewCount);
        WriteCounter(builder, "palllm_asr_timing_receipt_total", "ASR verbose-json responses that returned content-free timing receipts.", health.AsrTimingReceiptCount);
        WriteCounter(builder, "palllm_asr_timing_review_total", "ASR timing receipts whose metadata needs operator review.", health.AsrTimingReviewCount);
        WriteCounter(builder, "palllm_asr_quality_receipt_total", "ASR verbose-json responses that returned content-free segment quality receipts.", health.AsrQualityReceiptCount);
        WriteCounter(builder, "palllm_asr_quality_review_total", "ASR segment quality receipts whose metadata needs operator review.", health.AsrQualityReviewCount);
        WriteCounter(builder, "palllm_asr_upstream_request_id_receipt_total", "ASR responses that carried a sanitized upstream request/correlation id.", health.AsrUpstreamRequestIdReceiptCount);
        WriteCounter(builder, "palllm_asr_upstream_processing_receipt_total", "ASR responses that carried upstream processing-duration timing.", health.AsrUpstreamProcessingReceiptCount);
        WriteCounter(builder, "palllm_asr_upstream_phase_timing_receipt_total", "ASR responses that carried upstream queue / TTFT / prefill / decode timing.", health.AsrUpstreamPhaseTimingReceiptCount);
        WriteCounter(builder, "palllm_inference_prompt_tokens_total", "Cumulative prompt tokens billed by the inference endpoint.", health.TotalPromptTokens);
        WriteCounter(builder, "palllm_inference_completion_tokens_total", "Cumulative completion tokens billed by the inference endpoint.", health.TotalCompletionTokens);
        WriteCounter(builder, "palllm_inference_total_tokens_total", "Cumulative total tokens billed by the inference endpoint.", health.TotalInferenceTokens);

        // Gauges — current snapshots.
        WriteGauge(builder, "palllm_character_count", "Characters currently exposed by the adapter.", health.CharacterCount);
        WriteGauge(builder, "palllm_memory_entries", "Conversation memory entries in the store.", health.RememberedEntries);
        WriteGauge(builder, "palllm_loaded_pack_count", "Narrative packs loaded from disk.", health.LoadedPackCount);
        WriteGauge(builder, "palllm_known_base_count", "Bases promoted into live world state.", health.KnownBaseCount);
        WriteGauge(builder, "palllm_tracked_relationship_count", "Characters with an active relationship record.", health.TrackedRelationshipCount);
        WriteGauge(builder, "palllm_outbox_pending_files", "Outbox files waiting for a game-side consumer.", health.OutboxPendingCount);
        WriteGauge(builder, "palllm_inbox_pending_files", "Bridge inbox files waiting for drain.", health.InboxPendingCount);
        WriteGauge(builder, "palllm_screenshot_pending_files", "Screenshots waiting for vision world-state extraction.", health.ScreenshotPendingCount);
        WriteGauge(builder, "palllm_archive_files", "Files currently in the bridge archive directory.", health.ArchiveFileCount);
        WriteGauge(builder, "palllm_failed_files", "Files currently in the bridge failed directory.", health.FailedFileCount);
        WriteGauge(builder, "palllm_session_dirty", "1 when session state has unsaved mutations, else 0.", health.SessionDirty ? 1 : 0);
        WriteGauge(builder, "palllm_inference_circuit_failures", "Consecutive inference failures driving the circuit breaker.", health.InferenceCircuitFailures);
        WriteGauge(builder, "palllm_inference_recent_window_sample_count", "Recent-window live inference/vision samples retained for assessment.", inferencePerformance.SampleCount);
        WriteGauge(builder, "palllm_inference_recent_window_success_ratio_percent", "Recent-window inference success ratio percent.", inferencePerformance.Assessment.SuccessRatioPercent);
        WriteGauge(builder, "palllm_inference_recent_window_target_hit_ratio_percent", "Recent-window inference target-hit ratio percent.", inferencePerformance.Assessment.TargetHitRatioPercent);
        WriteGauge(builder, "palllm_inference_recent_window_ceiling_hit_ratio_percent", "Recent-window inference ceiling-hit ratio percent.", inferencePerformance.Assessment.CeilingHitRatioPercent);
        WriteGauge(
            builder,
            "palllm_inference_recent_window_degraded_lanes",
            "Recent-window live inference lanes currently assessed as degraded.",
            inferencePerformance.Lanes.Count(lane =>
                string.Equals(lane.Assessment.Status, InferencePerformanceStatuses.Degraded, StringComparison.Ordinal)));
        WriteGauge(
            builder,
            "palllm_inference_recent_window_critical_lanes",
            "Recent-window live inference lanes currently assessed as critical.",
            inferencePerformance.Lanes.Count(lane =>
                string.Equals(lane.Assessment.Status, InferencePerformanceStatuses.Critical, StringComparison.Ordinal)));

        builder.AppendLine("# HELP palllm_inference_recent_window_status Current recent-window inference readiness state. Exactly one series is 1.");
        builder.AppendLine("# TYPE palllm_inference_recent_window_status gauge");
        string recentBudget = string.IsNullOrWhiteSpace(inferencePerformance.Assessment.BudgetName)
            ? "recent_window"
            : inferencePerformance.Assessment.BudgetName;
        foreach (string status in InferencePerformanceStatuses.All)
        {
            builder.Append("palllm_inference_recent_window_status{status=\"")
                .Append(EscapeLabel(status))
                .Append("\",budget=\"")
                .Append(EscapeLabel(recentBudget))
                .Append("\"} ")
                .AppendLine(string.Equals(inferencePerformance.Assessment.Status, status, StringComparison.Ordinal) ? "1" : "0");
        }

        // Labeled counters — per-strategy fallback usage and per-pair tier
        // transitions. Both have bounded cardinality: the 19-entry strategy
        // catalog and the (typically 2-3) tier catalog. Prometheus happily
        // serves thousands of series per metric, but we stay well under.
        if (health.FallbackStrategyCounts.Count > 0)
        {
            builder.AppendLine("# HELP palllm_fallback_strategy_total Per-strategy count of chat replies served by the deterministic fallback director.");
            builder.AppendLine("# TYPE palllm_fallback_strategy_total counter");
            foreach (FallbackStrategyCount entry in health.FallbackStrategyCounts)
            {
                builder.Append("palllm_fallback_strategy_total{strategy=\"")
                    .Append(EscapeLabel(entry.StrategyId))
                    .Append("\"} ")
                    .AppendLine(entry.Count.ToString(Invariant));
            }
        }

        if (health.ModelTierTransitionCounts.Count > 0)
        {
            builder.AppendLine("# HELP palllm_model_tier_transition_total Count of model-tier graduations, keyed by source and destination tier id.");
            builder.AppendLine("# TYPE palllm_model_tier_transition_total counter");
            foreach (ModelTierTransitionCount entry in health.ModelTierTransitionCounts)
            {
                builder.Append("palllm_model_tier_transition_total{from=\"")
                    .Append(EscapeLabel(entry.From))
                    .Append("\",to=\"")
                    .Append(EscapeLabel(entry.To))
                    .Append("\"} ")
                    .AppendLine(entry.Count.ToString(Invariant));
            }
        }

        // Chat-latency histogram. Prometheus histograms carry cumulative
        // bucket counts plus _count and _sum suffixes — Grafana's
        // histogram_quantile() reads exactly this shape.
        if (health.ChatLatency.Count > 0)
        {
            builder.AppendLine("# HELP palllm_chat_duration_seconds End-to-end latency of /api/chat turns in seconds.");
            builder.AppendLine("# TYPE palllm_chat_duration_seconds histogram");
            foreach (LatencyHistogramBucket bucket in health.ChatLatency.Buckets)
            {
                builder.Append("palllm_chat_duration_seconds_bucket{le=\"")
                    .Append(bucket.UpperBoundSeconds.ToString("0.###", Invariant))
                    .Append("\"} ")
                    .AppendLine(bucket.CumulativeCount.ToString(Invariant));
            }
            // +Inf bucket equals the total count by definition.
            builder.Append("palllm_chat_duration_seconds_bucket{le=\"+Inf\"} ")
                .AppendLine(health.ChatLatency.Count.ToString(Invariant));
            builder.Append("palllm_chat_duration_seconds_sum ")
                .AppendLine(health.ChatLatency.SumSeconds.ToString("0.######", Invariant));
            builder.Append("palllm_chat_duration_seconds_count ")
                .AppendLine(health.ChatLatency.Count.ToString(Invariant));
        }

        if (inferencePerformance.Lanes.Count > 0)
        {
            builder.AppendLine("# HELP palllm_inference_lane_status Current per-lane recent-window inference readiness state. Exactly one series per lane is 1.");
            builder.AppendLine("# TYPE palllm_inference_lane_status gauge");
            foreach (InferencePerformanceLaneSnapshot lane in inferencePerformance.Lanes)
            {
                string laneBudget = string.IsNullOrWhiteSpace(lane.Assessment.BudgetName)
                    ? "recent_window"
                    : lane.Assessment.BudgetName;
                foreach (string status in InferencePerformanceStatuses.All)
                {
                    builder.Append("palllm_inference_lane_status{operation=\"")
                        .Append(EscapeLabel(lane.OperationName))
                        .Append("\",provider=\"")
                        .Append(EscapeLabel(lane.ProviderName))
                        .Append("\",model=\"")
                        .Append(EscapeLabel(lane.Model))
                        .Append("\",budget=\"")
                        .Append(EscapeLabel(laneBudget))
                        .Append("\",status=\"")
                        .Append(EscapeLabel(status))
                        .Append("\"} ")
                        .AppendLine(string.Equals(lane.Assessment.Status, status, StringComparison.Ordinal) ? "1" : "0");
                }
            }

            builder.AppendLine("# HELP palllm_inference_lane_sample_count Recent-window sample count for an active inference lane.");
            builder.AppendLine("# TYPE palllm_inference_lane_sample_count gauge");
            foreach (InferencePerformanceLaneSnapshot lane in inferencePerformance.Lanes)
            {
                string laneBudget = string.IsNullOrWhiteSpace(lane.Assessment.BudgetName)
                    ? "recent_window"
                    : lane.Assessment.BudgetName;
                builder.Append("palllm_inference_lane_sample_count{operation=\"")
                    .Append(EscapeLabel(lane.OperationName))
                    .Append("\",provider=\"")
                    .Append(EscapeLabel(lane.ProviderName))
                    .Append("\",model=\"")
                    .Append(EscapeLabel(lane.Model))
                    .Append("\",budget=\"")
                    .Append(EscapeLabel(laneBudget))
                    .Append("\"} ")
                    .AppendLine(lane.SampleCount.ToString(Invariant));
            }
        }

        return builder.ToString();
    }

    /// <summary>Prometheus label values must escape backslash, double quote, and newline.</summary>
    private static string EscapeLabel(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static void WriteCounter(StringBuilder builder, string name, string help, long value)
    {
        builder.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        builder.Append("# TYPE ").Append(name).AppendLine(" counter");
        builder.Append(name).Append(' ').AppendLine(value.ToString(Invariant));
    }

    private static void WriteGauge(StringBuilder builder, string name, string help, long value)
    {
        builder.Append("# HELP ").Append(name).Append(' ').AppendLine(help);
        builder.Append("# TYPE ").Append(name).AppendLine(" gauge");
        builder.Append(name).Append(' ').AppendLine(value.ToString(Invariant));
    }
}
