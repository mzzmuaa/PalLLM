const DASHBOARD_ENDPOINT = "/api/dashboard";
const FEATURES_ENDPOINT = "/api/features";
const QUICKSTART_ENDPOINT = "/api/quickstart";
const HEALTH_ENDPOINT = "/api/health";
const SELF_HEALING_ENDPOINT = "/api/self-healing/status";
const WHY_ENDPOINT = "/api/why";
const ROLES_ENDPOINT = "/api/roles";
const DUO_PLAN_ENDPOINT = "/api/duo/plan";
const PROMOTION_SUMMARY_ENDPOINT = "/api/promotion/summary";
const PROMOTION_SUGGESTIONS_ENDPOINT = "/api/promotion/suggestions";
const CHAT_ENDPOINT = "/api/chat";
const CHAT_STREAM_ENDPOINT = "/api/chat/stream";
const CHAT_PLAN_ENDPOINT = "/api/chat/plan";

// DuoCooperationPattern enum values — match server-side numeric order
// so we can render a friendly label when the endpoint returns an int.
const DUO_PATTERN_LABELS = {
    0: "Scout → Judge",
    1: "Architect → Implementer → Auditor",
    2: "Fan-out → Synthesis",
    3: "Parallel Disagreement (safety)",
    4: "Branch Tournament",
    5: "Sequential Swap",
    6: "Worker live / Judge background",
    7: "Draft → Finalise",
    8: "Duo Watchdog",
    9: "Dense Appeal Court",
    10: "Single-role fallback",
    11: "Deterministic-only",
};
const ELLIPSIS = "\u2026";
const MID_DOT = "\u00B7";

const featureHighlights = [
    "qwen-inference-defaults",
    "vision-augmentation",
    "session-persistence",
    "outbox-return-channel",
    "advisory-action-intents",
    "expanded-bridge-events",
    "autopilot-port",
];

const state = {
    autoRefresh: true,
    refreshMs: 8000,
    refreshing: false,
    timerId: null,
    controller: null,
    featuresLoaded: false,
    featureRequest: null,
    lastFetchDurationMs: null,
    lastRenderDurationMs: null,
    lastSuccessfulRefreshAt: null,
    lastServerLatencyMs: null,
    dashboardEtag: null,
    latestDashboard: null,
    uiVitals: {
        lcpMs: null,
        cls: 0,
    },
};

const renderCache = new WeakMap();
const numberFormatter = new Intl.NumberFormat();
const timeFormatter = new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
});
const relativeFormatter = typeof Intl.RelativeTimeFormat === "function"
    ? new Intl.RelativeTimeFormat(undefined, { numeric: "auto" })
    : null;

const statusBanner = document.querySelector("#status-banner");
const lastRefresh = document.querySelector("#last-refresh");
const refreshButton = document.querySelector("#refresh-button");
const autoRefreshToggle = document.querySelector("#auto-refresh");
const signalRibbon = document.querySelector("#signal-ribbon");
const metricGrid = document.querySelector("#metric-grid");
const overviewSummary = document.querySelector("#overview-summary");
const inferencePerformance = document.querySelector("#inference-performance");
const worldSummary = document.querySelector("#world-summary");
const bridgeSummary = document.querySelector("#bridge-summary");
const relationshipsBody = document.querySelector("#relationships-body");
const outboxList = document.querySelector("#outbox-list");
const logsList = document.querySelector("#logs-list");
const featuresList = document.querySelector("#features-list");
const featuresSection = document.querySelector("#features");
const navLinks = Array.from(document.querySelectorAll(".section-nav a"));

init();

function init() {
    state.autoRefresh = resolveAutoRefreshPreference();
    autoRefreshToggle.checked = state.autoRefresh;

    refreshButton.addEventListener("click", () => refreshDashboard({ announce: true }));
    autoRefreshToggle.addEventListener("change", () => {
        state.autoRefresh = autoRefreshToggle.checked;
        storeAutoRefreshPreference(state.autoRefresh);
        syncRefreshQueryParam(state.autoRefresh);
        resetRefreshTimer();
        updateMeta("Auto refresh updated.");
    });
    document.addEventListener("visibilitychange", handleVisibilityChange);

    syncRefreshQueryParam(state.autoRefresh);
    observeUiVitals();
    installFeatureLoader();
    installSectionObserver();
    installHashSync();
    void refreshDashboard({ announce: false });
}

async function refreshDashboard({ announce }) {
    if (state.refreshing) {
        return;
    }

    state.refreshing = true;
    refreshButton.disabled = true;
    refreshButton.textContent = `Refreshing${ELLIPSIS}`;
    metricGrid.setAttribute("aria-busy", "true");

    if (state.controller) {
        state.controller.abort();
    }

    const controller = new AbortController();
    state.controller = controller;

    try {
        const fetchStartedAt = performance.now();
        const result = await fetchDashboard(controller.signal);
        const fetchDurationMs = Math.max(1, Math.round(performance.now() - fetchStartedAt));
        if (result.etag) {
            state.dashboardEtag = result.etag;
        }

        if (result.notModified) {
            const renderStartedAt = performance.now();
            if (state.latestDashboard) {
                renderDashboard(state.latestDashboard);
                updateStatusBanner(state.latestDashboard.Health, state.latestDashboard.World);
            }

            const renderDurationMs = Math.max(1, Math.round(performance.now() - renderStartedAt));
            const refreshedAt = new Date();
            state.lastFetchDurationMs = fetchDurationMs;
            state.lastRenderDurationMs = renderDurationMs;
            state.lastSuccessfulRefreshAt = refreshedAt;

            updateMeta(buildRefreshMeta(
                refreshedAt,
                fetchDurationMs,
                renderDurationMs,
                state.lastServerLatencyMs,
                "304 not modified"));

            if (announce) {
                statusBanner.textContent = `${statusBanner.textContent} State unchanged.`;
            }

            return;
        }

        const data = result.data;

        const renderStartedAt = performance.now();
        state.latestDashboard = data;
        renderDashboard(data);
        const renderDurationMs = Math.max(1, Math.round(performance.now() - renderStartedAt));

        const refreshedAt = new Date();
        state.lastFetchDurationMs = fetchDurationMs;
        state.lastRenderDurationMs = renderDurationMs;
        state.lastSuccessfulRefreshAt = refreshedAt;
        state.lastServerLatencyMs = Number(data.ServerLatencyMs) || 0;

        updateStatusBanner(data.Health, data.World);
        updateMeta(buildRefreshMeta(
            refreshedAt,
            fetchDurationMs,
            renderDurationMs,
            state.lastServerLatencyMs));

        if (announce) {
            statusBanner.textContent = `${statusBanner.textContent} Refreshed just now.`;
        }
    } catch (error) {
        if (error.name !== "AbortError") {
            showGlobalError(error);
        }
    } finally {
        state.refreshing = false;
        refreshButton.disabled = false;
        refreshButton.textContent = "Refresh Now";
        metricGrid.setAttribute("aria-busy", "false");
        resetRefreshTimer();
    }
}

async function fetchDashboard(signal) {
    return fetchJson(DASHBOARD_ENDPOINT, signal, {
        cacheMode: "no-store",
        etag: state.dashboardEtag,
    });
}

async function fetchJson(url, signal, options = {}) {
    const headers = { Accept: "application/json" };
    if (options.etag) {
        headers["If-None-Match"] = options.etag;
    }

    const response = await fetch(url, {
        headers,
        cache: options.cacheMode || "no-store",
        signal,
    });

    const etag = response.headers.get("ETag");
    if (response.status === 304) {
        return { notModified: true, etag };
    }

    if (!response.ok) {
        throw new Error(`${url} returned ${response.status}`);
    }

    return {
        notModified: false,
        etag,
        data: await response.json(),
    };
}

function renderDashboard(data) {
    renderSignalRibbon(data.Health, data.InferencePerformance);
    renderMetricGrid(data.Health);
    renderOverviewSummary(data.Health, data.World, data.ServerLatencyMs, data.InferencePerformance);
    renderInferencePerformance(data.InferencePerformance);
    renderWorld(data.World);
    renderBridge(data.World, data.Health);
    renderRelationships(data.Relationships);
    renderOutbox(data.Outbox);
    renderLogs(data.Logs);
}

function renderSignalRibbon(health, performance) {
    if (!health) {
        setMarkup(signalRibbon, `<span class="tone-chip" data-tone="bad">Runtime data unavailable</span>`);
        return;
    }

    const chips = [
        toneChip(health.AdapterReady ? "good" : "warn", health.AdapterReady ? "Adapter Ready" : "Adapter Waiting"),
        toneChip(health.BridgeEnabled ? "good" : "warn", health.BridgeEnabled ? "Bridge Enabled" : "Bridge Disabled"),
        toneChip(
            health.InferenceConfigured ? "good" : "warn",
            health.InferenceConfigured ? `Inference: ${escapeHtml(health.InferenceModel || "Configured")}` : "Inference Disabled"
        ),
        toneChip(
            health.VisionEnabled ? "good" : "neutral",
            health.VisionEnabled ? `Vision: ${escapeHtml(health.VisionModel || "Enabled")}` : "Vision Offline"
        ),
        toneChip(health.TtsEnabled ? "good" : "neutral", health.TtsEnabled ? "TTS Ready" : "TTS Idle"),
        toneChip(health.AsrEnabled ? "good" : "neutral", health.AsrEnabled ? "ASR Ready" : "ASR Idle"),
        toneChip(
            health.AutomationEnabled ? "warn" : "neutral",
            health.AutomationEnabled ? "Automation Advisory On" : "Automation Advisory Off"
        ),
    ];

    if (performance?.Assessment?.Status) {
        chips.push(toneChip(
            assessmentTone(performance.Assessment.Status),
            `Lane Budget ${assessmentLabel(performance.Assessment.Status)}`
        ));
    }

    setMarkup(signalRibbon, chips.join(""));
}

function renderMetricGrid(health) {
    if (!health) {
        setMarkup(metricGrid, `<p class="empty-state">Runtime metrics could not be loaded.</p>`);
        return;
    }

    const metrics = [
        {
            label: "Runtime Status",
            value: health.Status || "Unknown",
            note: `Adapter ${health.AdapterName || "unknown"} ${MID_DOT} ${health.AdapterReady ? "ready for inference" : "not ready"}`,
        },
        {
            label: "Character Network",
            value: formatNumber(health.CharacterCount),
            note: `${formatNumber(health.TrackedRelationshipCount)} tracked relationships`,
        },
        {
            label: "Memory Store",
            value: formatNumber(health.RememberedEntries),
            note: `${formatNumber(health.LoadedPackCount)} packs ${MID_DOT} ${formatNumber(health.KnownBaseCount)} known bases`,
        },
        {
            label: "Bridge Events",
            value: formatNumber(health.BridgeEventCount),
            note: `${formatNumber(health.OutboxPendingCount)} pending outbox items`,
        },
        {
            label: "LLM Offload",
            value: formatNumber((health.FallbackReplyCount || 0) + (health.InferenceBypassCount || 0)),
            note: `${formatNumber(health.FallbackReplyCount)} fallback replies ${MID_DOT} ${formatNumber(health.RateLimitedCount)} rate limited`,
        },
        {
            label: "Token Load",
            value: formatNumber(health.TotalInferenceTokens),
            note: `${formatNumber(health.TotalPromptTokens)} prompt ${MID_DOT} ${formatNumber(health.TotalCompletionTokens)} completion`,
        },
    ];

    setMarkup(metricGrid, metrics.map(renderMetricCard).join(""));
}

function renderMetricCard(metric) {
    return `
        <article class="metric">
            <p class="metric-label">${escapeHtml(metric.label)}</p>
            <p class="metric-value">${escapeHtml(metric.value)}</p>
            <p class="metric-note">${escapeHtml(metric.note)}</p>
        </article>
    `;
}

function renderOverviewSummary(health, world, serverLatencyMs, performance) {
    if (!health) {
        setMarkup(overviewSummary, `<p class="empty-state">Operational readout unavailable.</p>`);
        return;
    }

    const snapshot = world?.Snapshot;
    const hasWorldSnapshot = hasLiveWorldSnapshot(snapshot);
    const queuePressure =
        (health.InboxPendingCount || 0)
        + (health.OutboxPendingCount || 0)
        + (health.ScreenshotPendingCount || 0)
        + (health.ArchiveFileCount || 0)
        + (health.FailedFileCount || 0);

    const items = [
        summaryItem(
            "Model Path",
            health.InferenceConfigured
                ? `${health.InferenceModel || "Configured"}${health.VisionEnabled ? ` ${MID_DOT} vision online` : ""}`
                : "Inference disabled"
        ),
        summaryItem(
            "Inference Window",
            performance && Number(performance.SampleCount) > 0
                ? `${formatNumber(performance.SampleCount)} ops ${MID_DOT} ${assessmentLabel(performance.Assessment?.Status)} ${MID_DOT} ${formatPercent(performance.SuccessCount, performance.SampleCount)} success ${MID_DOT} ${formatTargetBudget(performance.Assessment)}`
                : "No recent live inference traffic"
        ),
        summaryItem(
            "Circuit",
            `${health.InferenceCircuitState || "Unknown"}${health.InferenceCircuitFailures ? ` ${MID_DOT} ${formatNumber(health.InferenceCircuitFailures)} recent failures` : ""}`
        ),
        summaryItem(
            "Session",
            health.SessionDirty
                ? `Dirty ${MID_DOT} last saved ${formatRelativeOrTime(health.SessionLastSavedAtUtc)}`
                : `Clean ${MID_DOT} last saved ${formatRelativeOrTime(health.SessionLastSavedAtUtc)}`
        ),
        summaryItem(
            "Queue Pressure",
            `${formatNumber(queuePressure)} tracked files ${MID_DOT} ${formatNumber(health.OutboxPendingCount)} outbox ${MID_DOT} ${formatNumber(health.ScreenshotPendingCount)} screenshots`
        ),
        summaryItem(
            "Dashboard Path",
            `${formatNumber(serverLatencyMs || 0)} ms server aggregation`
        ),
        summaryItem(
            "UI Vitals",
            formatUiVitals()
        ),
        summaryItem(
            "Snapshot Freshness",
            hasWorldSnapshot && snapshot?.CapturedAtUtc
                ? `${formatRelativeOrTime(snapshot.CapturedAtUtc)} ${MID_DOT} ${snapshot.WorldName || "live session"}`
                : "Awaiting first live world snapshot"
        ),
    ];

    setMarkup(overviewSummary, items.join(""));
}

function renderInferencePerformance(performance) {
    if (!performance || !Array.isArray(performance.Lanes) || performance.Lanes.length === 0 || Number(performance.SampleCount) <= 0) {
        setMarkup(inferencePerformance, `<li class="empty-state">No recent live inference or vision calls recorded in the current window.</li>`);
        return;
    }

    const items = [];
    if (performance.Assessment?.Status) {
        items.push(`
            <li class="stack-item">
                <div class="stack-head">
                    <strong>Recent window budget</strong>
                    ${toneChip(assessmentTone(performance.Assessment.Status), assessmentLabel(performance.Assessment.Status))}
                </div>
                <p class="stack-body">${escapeHtml(performance.Assessment.Summary || performance.Summary || "Recent budget assessment unavailable.")}</p>
                <p class="stack-meta">${escapeHtml(buildAssessmentMeta(performance.Assessment))}</p>
            </li>
        `);
    }

    const lanes = performance.Lanes.slice(0, 4);
    items.push(...lanes.map((lane) => {
        const sampleCount = Number(lane.SampleCount) || 0;
        const successCount = Number(lane.SuccessCount) || 0;
        const failureCount = Number(lane.FailureCount) || 0;
        const header = [
            lane.OperationName === "generate_content" ? "Vision" : "Chat",
            lane.Model || lane.RequestModel || "Unknown model",
        ].join(` ${MID_DOT} `);
        const detail = [
            `${formatNumber(sampleCount)} ops`,
            `p95 ${formatDuration(lane.P95LatencyMs)}`,
            `avg ${formatDuration(lane.AverageLatencyMs)}`,
            `${formatPercent(successCount, sampleCount)} success`,
        ].join(` ${MID_DOT} `);
        const lastTokenDetail = Number(lane.LastTotalTokens) > 0
            ? `${formatNumber(lane.LastPromptTokens || 0)} in ${MID_DOT} ${formatNumber(lane.LastCompletionTokens || 0)} out last`
            : "";
        const averageTokenDetail = Number(lane.TotalTokens) > 0
            ? `${formatNumber(lane.AveragePromptTokens || 0)} in ${MID_DOT} ${formatNumber(lane.AverageCompletionTokens || 0)} out avg`
            : "No token totals reported";
        const tokenDetail = lastTokenDetail
            ? `${lastTokenDetail} ${MID_DOT} ${averageTokenDetail}`
            : averageTokenDetail;
        const usageDetailParts = [];
        if (Number(lane.LastCachedPromptTokens) > 0) {
            usageDetailParts.push(`${formatNumber(lane.LastCachedPromptTokens)} cached`);
        }
        if (Number(lane.LastCompletionReasoningTokens) > 0) {
            usageDetailParts.push(`${formatNumber(lane.LastCompletionReasoningTokens)} reasoning`);
        }
        if (Number(lane.LastPromptAudioTokens) > 0 || Number(lane.LastCompletionAudioTokens) > 0) {
            usageDetailParts.push(`${formatNumber((Number(lane.LastPromptAudioTokens) || 0) + (Number(lane.LastCompletionAudioTokens) || 0))} audio`);
        }
        if (Number(lane.LastAcceptedPredictionTokens) > 0 || Number(lane.LastRejectedPredictionTokens) > 0) {
            usageDetailParts.push(`${formatNumber(lane.LastAcceptedPredictionTokens || 0)}/${formatNumber(lane.LastRejectedPredictionTokens || 0)} prediction`);
        }
        const usageDetail = usageDetailParts.length > 0
            ? `${MID_DOT} details ${usageDetailParts.join("/")}`
            : "";
        const finishReasonDetail = Array.isArray(lane.LastFinishReasons) && lane.LastFinishReasons.length > 0
            ? `${MID_DOT} finish ${lane.LastFinishReasons.slice(0, 3).join("/")}`
            : "";
        const upstreamRequestIdDetail = lane.LastUpstreamRequestId
            ? `${MID_DOT} upstream ${lane.LastUpstreamRequestId}`
            : "";
        const upstreamProcessingDetail = lane.LastUpstreamProcessingMs !== null && lane.LastUpstreamProcessingMs !== undefined
            ? `${MID_DOT} upstream ${formatDuration(lane.LastUpstreamProcessingMs)} proc`
            : "";
        const upstreamPhaseParts = [];
        if (lane.LastUpstreamQueueMs !== null && lane.LastUpstreamQueueMs !== undefined) {
            upstreamPhaseParts.push(`q ${formatDuration(lane.LastUpstreamQueueMs)}`);
        }
        if (lane.LastUpstreamTimeToFirstTokenMs !== null && lane.LastUpstreamTimeToFirstTokenMs !== undefined) {
            upstreamPhaseParts.push(`ttft ${formatDuration(lane.LastUpstreamTimeToFirstTokenMs)}`);
        }
        if (lane.LastUpstreamPrefillMs !== null && lane.LastUpstreamPrefillMs !== undefined) {
            upstreamPhaseParts.push(`pre ${formatDuration(lane.LastUpstreamPrefillMs)}`);
        }
        if (lane.LastUpstreamDecodeMs !== null && lane.LastUpstreamDecodeMs !== undefined) {
            upstreamPhaseParts.push(`dec ${formatDuration(lane.LastUpstreamDecodeMs)}`);
        }
        const upstreamPhaseDetail = upstreamPhaseParts.length > 0
            ? `${MID_DOT} phases ${upstreamPhaseParts.join("/")}`
            : "";
        const tail = failureCount > 0 && lane.LastErrorType
            ? `${tokenDetail}${usageDetail}${finishReasonDetail}${upstreamRequestIdDetail}${upstreamProcessingDetail}${upstreamPhaseDetail} ${MID_DOT} last error ${escapeHtml(lane.LastErrorType)}`
            : `${tokenDetail}${usageDetail}${finishReasonDetail}${upstreamRequestIdDetail}${upstreamProcessingDetail}${upstreamPhaseDetail}`;

        return `
            <li class="stack-item">
                <div class="stack-head">
                    <strong>${escapeHtml(header)}</strong>
                    <span class="stack-meta">${escapeHtml(lane.ProviderName || "openai_compatible")}</span>
                    ${lane.Assessment?.Status ? toneChip(assessmentTone(lane.Assessment.Status), assessmentLabel(lane.Assessment.Status)) : ""}
                </div>
                <p class="stack-body">${escapeHtml(detail)}</p>
                <p class="stack-meta">${escapeHtml(tail)} ${MID_DOT} ${escapeHtml(formatTargetBudget(lane.Assessment))} ${MID_DOT} last ${escapeHtml(formatRelativeOrTime(lane.LastObservedAtUtc))}</p>
            </li>
        `;
    }));

    if (performance.Lanes.length > lanes.length) {
        items.push(`
            <li class="stack-item">
                <div class="stack-head">
                    <strong>${escapeHtml(performance.Summary || "Recent model activity summary")}</strong>
                </div>
                <p class="stack-meta">Showing ${formatNumber(lanes.length)} of ${formatNumber(performance.Lanes.length)} recent lanes.</p>
            </li>
        `);
    }

    setMarkup(inferencePerformance, items.join(""));
}

function renderWorld(world) {
    const snapshot = world?.Snapshot;
    if (!hasLiveWorldSnapshot(snapshot)) {
        setMarkup(worldSummary, `<p class="empty-state">Waiting for the first live world snapshot from the bridge or screenshot pipeline.</p>`);
        return;
    }

    const hostiles = listOrFallback(snapshot.NearbyHostiles, "No visible hostiles");
    const friendlies = listOrFallback(snapshot.NearbyFriendlies, "No visible friendlies");
    const resources = listOrFallback(snapshot.NearbyResources, "No nearby resources");
    const events = listOrFallback(snapshot.RecentEvents, "No recent world events");

    setMarkup(worldSummary, `
        <div class="info-grid">
            ${infoItem("World", snapshot.WorldName || "Unnamed World")}
            ${infoItem("Objective", snapshot.CurrentObjective || "No active objective")}
            ${infoItem("Biome", snapshot.Biome || "Unknown")}
            ${infoItem("Weather", snapshot.Weather || "Unknown")}
            ${infoItem("Time of Day", snapshot.TimeOfDay || "Unknown")}
            ${infoItem("In Base", booleanLabel(snapshot.IsInBase))}
            ${infoItem("Player Health", formatFraction(snapshot.PlayerHealthFraction))}
            ${infoItem("Player Stamina", formatFraction(snapshot.PlayerStaminaFraction))}
            ${infoItem("Player Hunger", formatFraction(snapshot.PlayerHungerFraction))}
            ${infoItem("Hostiles", hostiles)}
            ${infoItem("Friendlies", friendlies)}
            ${infoItem("Resources", resources)}
            ${infoItem("Recent Events", events)}
            ${infoItem("Known Bases", renderBaseLabels(snapshot.KnownBases))}
        </div>
    `);
}

function renderBridge(world, health) {
    const bridge = world?.Bridge;
    if (!bridge) {
        setMarkup(bridgeSummary, `<p class="empty-state">Bridge status unavailable.</p>`);
        return;
    }

    const probe = bridge.LastUiProbe;
    const diagnostics = bridge.UiProbeDiagnostics;
    const probeWidgets = Array.isArray(probe?.Widgets) && probe.Widgets.length > 0
        ? probe.Widgets
            .slice(0, 4)
            .map((entry) => {
                const label = entry.DisplayName || entry.ClassName || entry.FullName || "Unknown widget";
                const active = entry.IsActive ? " active" : "";
                const count = entry.SeenCount > 1 ? ` x${entry.SeenCount}` : "";
                return `${label}${count}${active}`;
            })
            .join(` ${MID_DOT} `)
        : "No widget sample yet";
    const probeCandidates = Array.isArray(diagnostics?.Candidates) && diagnostics.Candidates.length > 0
        ? diagnostics.Candidates
            .slice(0, 3)
            .map((candidate) => {
                const label = candidate.DisplayName || candidate.ClassName || candidate.FullName || "Unknown widget";
                const score = Number.isFinite(candidate.Score) ? ` (${formatNumber(candidate.Score)})` : "";
                return `${label}${score}`;
            })
            .join(` ${MID_DOT} `)
        : "No ranked candidates yet";
    const probeCandidateTitle = Array.isArray(diagnostics?.Candidates) && diagnostics.Candidates.length > 0
        ? diagnostics.Candidates
            .slice(0, 3)
            .map((candidate) => {
                const label = candidate.DisplayName || candidate.ClassName || candidate.FullName || "Unknown widget";
                const rationale = Array.isArray(candidate.Rationale) && candidate.Rationale.length > 0
                    ? ` - ${candidate.Rationale.join("; ")}`
                    : "";
                return `${label}: score ${formatNumber(candidate.Score)}${rationale}`;
            })
            .join(" | ")
        : "No ranked candidates yet";

    setMarkup(bridgeSummary, `
        <div class="info-grid">
            ${infoItem("Last Event", bridge.LastEventType || "None yet")}
            ${infoItem("Last Event Time", formatRelativeOrTime(bridge.LastEventAtUtc))}
            ${infoItem("Last Event Source", bridge.LastEventSource || "Unknown")}
            ${infoItem("Event Count", formatNumber(bridge.EventCount || 0))}
            ${infoItem("Boot Count", formatNumber(bridge.BootCount || 0))}
            ${infoItem("UI Probe", probe?.Summary || "No probe captured yet")}
            ${infoItem("Probe Reason", probe?.Reason || "None yet")}
            ${infoItem("Probe Widgets", probeWidgets)}
            ${infoItem("Probe Dump", compactPath(probe?.DumpPath || "Unavailable"), probe?.DumpPath || "Unavailable")}
            ${infoItem("Probe Dumps", formatNumber(diagnostics?.DumpCount || 0))}
            ${infoItem("Probe Candidates", probeCandidates, probeCandidateTitle)}
            ${infoItem("Probe Candidate Count", formatNumber(diagnostics?.CandidateCount || 0))}
            ${infoItem("Latest Probe Dump", compactPath(diagnostics?.LastDumpPath || "Unavailable"), diagnostics?.LastDumpPath || "Unavailable")}
            ${infoItem("Circuit State", health?.InferenceCircuitState || "Unknown")}
            ${infoItem("Session Dirty", booleanLabel(health?.SessionDirty))}
            ${infoItem("Last Saved", formatRelativeOrTime(health?.SessionLastSavedAtUtc))}
            ${infoItem(
                "Runtime Root",
                compactPath(health?.RuntimeRoot || "Unknown"),
                health?.RuntimeRoot || "Unknown")}
        </div>
    `);
}

function renderRelationships(relationships) {
    if (!Array.isArray(relationships) || relationships.length === 0) {
        setMarkup(relationshipsBody, `<tr><td colspan="5" class="empty-state">No relationships tracked yet.</td></tr>`);
        return;
    }

    setMarkup(relationshipsBody, relationships.map((relationship) => `
        <tr>
            <td data-label="Character">${escapeHtml(relationship.CharacterName || `Character ${relationship.CharacterId}`)}</td>
            <td data-label="Affinity" data-type="number">${formatSignedNumber(relationship.Affinity || 0)}</td>
            <td data-label="Mood"><span class="badge" data-mood="${escapeHtml(relationship.Mood || "Neutral")}">${escapeHtml(relationship.Mood || "Neutral")}</span></td>
            <td data-label="Tone">${escapeHtml(relationship.LastTone || "Neutral")}</td>
            <td data-label="Last Seen">${escapeHtml(formatRelativeOrTime(relationship.LastInteractionUtc))}</td>
        </tr>
    `).join(""));
}

function renderOutbox(outbox) {
    if (!Array.isArray(outbox) || outbox.length === 0) {
        setMarkup(outboxList, `<li class="empty-state">No pending outbox envelopes. The current game-side consumer should be caught up.</li>`);
        return;
    }

    setMarkup(outboxList, outbox.map((item) => `
        <li class="stack-item">
            <strong>${escapeHtml(item.FileName || "chat_reply.json")}</strong>
            <p>${escapeHtml(formatRelativeOrTime(item.WrittenAtUtc))} ${MID_DOT} ${escapeHtml(formatBytes(item.SizeBytes || 0))}</p>
        </li>
    `).join(""));
}

function renderLogs(logs) {
    if (!Array.isArray(logs) || logs.length === 0) {
        setMarkup(logsList, `<li class="empty-state">No adapter log entries yet.</li>`);
        return;
    }

    setMarkup(logsList, logs.map((entry) => `
        <li class="log-item" data-level="${escapeHtml((entry.Level || "info").toLowerCase())}">
            <div class="log-meta">
                <span class="log-level">${escapeHtml(entry.Level || "Info")}</span>
                <span>${escapeHtml(formatRelativeOrTime(entry.TimestampUtc))}</span>
            </div>
            <div>${escapeHtml(entry.Message || "No log message.")}</div>
        </li>
    `).join(""));
}

function renderFeatures(features) {
    featuresList.setAttribute("aria-busy", "false");

    if (!Array.isArray(features) || features.length === 0) {
        setMarkup(featuresList, `<p class="empty-state">Feature catalog unavailable.</p>`);
        return;
    }

    const byPriority = [
        ...featureHighlights
            .map((id) => features.find((feature) => feature.Id === id))
            .filter(Boolean),
        ...features.filter((feature) => feature.Status !== "ready" && !featureHighlights.includes(feature.Id)),
        ...features.filter((feature) => feature.Status === "ready" && !featureHighlights.includes(feature.Id)),
    ];

    const unique = dedupeBy(byPriority, (feature) => feature.Id);

    setMarkup(featuresList, unique.map((feature) => `
        <article class="feature-item">
            <header>
                <h3>${escapeHtml(toTitleCase(feature.Id.replaceAll("-", " ")))}</h3>
                <span class="badge" data-status="${escapeHtml(feature.Status || "ready")}">${escapeHtml(feature.Status || "ready")}</span>
            </header>
            <p>${escapeHtml(feature.Summary || "")}</p>
            <small>${escapeHtml(feature.Notes || "")}</small>
        </article>
    `).join(""));

    state.featuresLoaded = true;
}

function updateStatusBanner(health, world) {
    if (!health) {
        statusBanner.dataset.tone = "bad";
        statusBanner.textContent = "Runtime data unavailable.";
        return;
    }

    const worldName = world?.Snapshot?.WorldName || "live session";
    const deliveryNote = health.OutboxPendingCount > 0
        ? `${formatNumber(health.OutboxPendingCount)} pending delivery`
        : "delivery queue clear";
    const capabilityNote = health.InferenceConfigured
        ? deliveryNote
        : `fallback-only mode, ${deliveryNote}`;

    statusBanner.dataset.tone = health.AdapterReady && health.InferenceConfigured ? "good" : "warn";
    statusBanner.textContent = `${health.Status || "Unknown"} - ${worldName} - ${capabilityNote}`;
}

function updateMeta(message) {
    lastRefresh.textContent = message;
}

function showGlobalError(error) {
    statusBanner.dataset.tone = "bad";
    statusBanner.textContent = `Interface refresh failed: ${error.message}`;
    updateMeta("The dashboard kept its last successful state.");
}

function resetRefreshTimer() {
    if (state.timerId) {
        window.clearTimeout(state.timerId);
        state.timerId = null;
    }

    if (!state.autoRefresh || document.hidden) {
        return;
    }

    state.timerId = window.setTimeout(() => {
        void refreshDashboard({ announce: false });
    }, state.refreshMs);
}

function resolveAutoRefreshPreference() {
    const params = new URLSearchParams(window.location.search);
    if (params.get("refresh") === "off") {
        return false;
    }

    if (params.get("refresh") === "on") {
        return true;
    }

    const stored = window.localStorage.getItem("palllm:autoRefresh");
    if (stored) {
        return stored !== "off";
    }

    return !window.navigator.connection?.saveData;
}

function storeAutoRefreshPreference(enabled) {
    window.localStorage.setItem("palllm:autoRefresh", enabled ? "on" : "off");
}

function syncRefreshQueryParam(enabled) {
    const url = new URL(window.location.href);
    url.searchParams.set("refresh", enabled ? "on" : "off");
    window.history.replaceState({}, "", url);
}

function installFeatureLoader() {
    if ("IntersectionObserver" in window) {
        const observer = new IntersectionObserver((entries) => {
            const visible = entries.some((entry) => entry.isIntersecting);
            if (!visible) {
                return;
            }

            observer.disconnect();
            void loadFeatures();
        }, { rootMargin: "180px 0px" });

        observer.observe(featuresSection);
    } else {
        window.setTimeout(() => {
            void loadFeatures();
        }, 1500);
    }

    if ("requestIdleCallback" in window) {
        window.requestIdleCallback(() => {
            void loadFeatures();
        }, { timeout: 2200 });
    }
}

function installSectionObserver() {
    if (!("IntersectionObserver" in window) || navLinks.length === 0) {
        return;
    }

    const sections = navLinks
        .map((link) => document.querySelector(link.getAttribute("href")))
        .filter(Boolean);

    if (sections.length === 0) {
        return;
    }

    const observer = new IntersectionObserver((entries) => {
        const active = entries
            .filter((entry) => entry.isIntersecting)
            .sort((left, right) => right.intersectionRatio - left.intersectionRatio)[0];

        if (!active?.target?.id) {
            return;
        }

        setActiveSection(active.target.id);
    }, {
        rootMargin: "-18% 0px -58% 0px",
        threshold: [0.15, 0.35, 0.55, 0.75],
    });

    sections.forEach((section) => observer.observe(section));
    const requestedSectionId = window.location.hash.replace(/^#/, "");
    const initialSectionId = sections.some((section) => section.id === requestedSectionId)
        ? requestedSectionId
        : sections[0].id;
    setActiveSection(initialSectionId);
}

function setActiveSection(sectionId) {
    for (const link of navLinks) {
        if (link.getAttribute("href") === `#${sectionId}`) {
            link.setAttribute("aria-current", "location");
        } else {
            link.removeAttribute("aria-current");
        }
    }
}

function installHashSync() {
    window.addEventListener("hashchange", handleHashChange);
    handleHashChange();
}

function handleHashChange() {
    const sectionId = window.location.hash.replace(/^#/, "");
    if (!sectionId) {
        return;
    }

    if (!navLinks.some((link) => link.getAttribute("href") === `#${sectionId}`)) {
        return;
    }

    setActiveSection(sectionId);
}

async function loadFeatures() {
    if (state.featuresLoaded) {
        return;
    }

    featuresList.setAttribute("aria-busy", "true");

    if (!state.featureRequest) {
        state.featureRequest = fetchJson(FEATURES_ENDPOINT, undefined, { cacheMode: "default" })
            .then((result) => {
                const features = Array.isArray(result.data) ? result.data : [];
                renderFeatures(features);
                return features;
            })
            .catch((error) => {
                featuresList.setAttribute("aria-busy", "false");
                setMarkup(featuresList, `<p class="empty-state">Feature catalog unavailable: ${escapeHtml(error.message)}</p>`);
                throw error;
            });
    }

    try {
        await state.featureRequest;
    } finally {
        if (!state.featuresLoaded) {
            state.featureRequest = null;
        }
    }
}

function infoItem(term, value, title = value) {
    return `
        <article class="info-item">
            <p class="info-term">${escapeHtml(term)}</p>
            <p class="info-value" title="${escapeHtml(title)}">${escapeHtml(value)}</p>
        </article>
    `;
}

function summaryItem(term, value) {
    return `
        <article class="summary-item">
            <span class="summary-term">${escapeHtml(term)}</span>
            <p class="summary-value">${escapeHtml(value)}</p>
        </article>
    `;
}

function toneChip(tone, label) {
    return `<span class="tone-chip" data-tone="${escapeHtml(tone)}">${label}</span>`;
}

function renderBaseLabels(bases) {
    if (!Array.isArray(bases) || bases.length === 0) {
        return "No promoted base state";
    }

    return bases
        .slice(0, 4)
        .map((base) => base.BaseId || "Unknown Base")
        .join(", ");
}

function booleanLabel(value) {
    if (value === true) {
        return "Yes";
    }

    if (value === false) {
        return "No";
    }

    return "Unknown";
}

function hasLiveWorldSnapshot(snapshot) {
    if (!snapshot) {
        return false;
    }

    return Boolean(
        snapshot.IsWorldLoaded
        || snapshot.WorldName
        || snapshot.CurrentObjective
        || snapshot.Biome
        || snapshot.Weather
        || snapshot.TimeOfDay
        || (Array.isArray(snapshot.ActiveBaseIds) && snapshot.ActiveBaseIds.length > 0)
        || (Array.isArray(snapshot.KnownBases) && snapshot.KnownBases.length > 0)
        || (Array.isArray(snapshot.NearbyHostiles) && snapshot.NearbyHostiles.length > 0)
        || (Array.isArray(snapshot.NearbyFriendlies) && snapshot.NearbyFriendlies.length > 0)
        || (Array.isArray(snapshot.NearbyResources) && snapshot.NearbyResources.length > 0)
        || (Array.isArray(snapshot.RecentEvents) && snapshot.RecentEvents.length > 0)
    );
}

function listOrFallback(values, fallback) {
    if (!Array.isArray(values) || values.length === 0) {
        return fallback;
    }

    return values.slice(0, 4).join(", ");
}

function formatRelativeOrTime(value) {
    if (!value) {
        return "Not available";
    }

    const date = new Date(value);
    if (Number.isNaN(date.getTime())) {
        return "Invalid time";
    }

    if (!relativeFormatter) {
        return timeFormatter.format(date);
    }

    const seconds = Math.round((date.getTime() - Date.now()) / 1000);
    const absSeconds = Math.abs(seconds);

    if (absSeconds < 60) {
        return relativeFormatter.format(seconds, "second");
    }

    const minutes = Math.round(seconds / 60);
    if (Math.abs(minutes) < 60) {
        return relativeFormatter.format(minutes, "minute");
    }

    const hours = Math.round(minutes / 60);
    if (Math.abs(hours) < 24) {
        return relativeFormatter.format(hours, "hour");
    }

    const days = Math.round(hours / 24);
    return relativeFormatter.format(days, "day");
}

function formatSignedNumber(value) {
    const numeric = Number(value) || 0;
    return numeric > 0 ? `+${numberFormatter.format(numeric)}` : numberFormatter.format(numeric);
}

function formatNumber(value) {
    return numberFormatter.format(Number(value) || 0);
}

function formatFraction(value) {
    const numeric = Number(value);
    if (Number.isNaN(numeric) || !Number.isFinite(numeric)) {
        return "Unknown";
    }

    return `${Math.round(Math.max(0, Math.min(1, numeric)) * 100)}%`;
}

function formatBytes(value) {
    const bytes = Number(value) || 0;
    if (bytes < 1024) {
        return `${bytes} B`;
    }

    if (bytes < 1024 * 1024) {
        return `${(bytes / 1024).toFixed(1)} KB`;
    }

    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

function formatUiVitals() {
    const parts = [];

    if (Number.isFinite(state.uiVitals.lcpMs) && state.uiVitals.lcpMs > 0) {
        parts.push(`LCP ${formatDuration(state.uiVitals.lcpMs)}`);
    } else {
        parts.push("LCP pending");
    }

    parts.push(`CLS ${state.uiVitals.cls.toFixed(3)}`);
    return parts.join(` ${MID_DOT} `);
}

function formatDuration(value) {
    const numeric = Number(value);
    if (!Number.isFinite(numeric) || numeric < 0) {
        return "Unknown";
    }

    if (numeric === 0) {
        return "0 ms";
    }

    if (numeric < 1000) {
        return `${Math.round(numeric)} ms`;
    }

    return `${(numeric / 1000).toFixed(1)} s`;
}

function formatPercent(numerator, denominator) {
    const top = Number(numerator) || 0;
    const bottom = Number(denominator) || 0;
    if (bottom <= 0) {
        return "0%";
    }

    return `${Math.round((top / bottom) * 100)}%`;
}

function assessmentTone(status) {
    switch (String(status || "").toLowerCase()) {
        case "healthy":
            return "good";
        case "degraded":
            return "warn";
        case "critical":
            return "bad";
        case "insufficient_data":
            return "info";
        default:
            return "neutral";
    }
}

function assessmentLabel(status) {
    switch (String(status || "").toLowerCase()) {
        case "healthy":
            return "Healthy";
        case "degraded":
            return "Degraded";
        case "critical":
            return "Critical";
        case "insufficient_data":
            return "Warming";
        default:
            return "No Data";
    }
}

function formatTargetBudget(assessment) {
    if (!assessment || !assessment.Status) {
        return "no budget data";
    }

    const target = Number(assessment.LatencyTargetMs);
    const targetLabel = Number.isFinite(target) && target > 0
        ? formatDuration(target)
        : "per-lane target";
    const ratio = Number.isFinite(Number(assessment.TargetHitRatioPercent))
        ? `${formatNumber(assessment.TargetHitRatioPercent)}%`
        : "0%";
    return `${ratio} within ${targetLabel}`;
}

function buildAssessmentMeta(assessment) {
    if (!assessment || !assessment.Status) {
        return "No recent budget data.";
    }

    const success = `${formatNumber(assessment.SuccessRatioPercent || 0)}% success`;
    const target = formatTargetBudget(assessment);
    const ceiling = Number.isFinite(Number(assessment.CeilingHitRatioPercent))
        ? `${formatNumber(assessment.CeilingHitRatioPercent)}% inside ${Number.isFinite(Number(assessment.LatencyCeilingMs)) && Number(assessment.LatencyCeilingMs) > 0 ? formatDuration(assessment.LatencyCeilingMs) : "per-lane ceiling"}`
        : "No ceiling data";
    const minimum = Number.isFinite(Number(assessment.MinimumSampleCount))
        ? `${formatNumber(assessment.MinimumSampleCount)}-sample confidence floor`
        : "confidence floor unavailable";

    return [success, target, ceiling, minimum].join(` ${MID_DOT} `);
}

function compactPath(value) {
    const text = String(value ?? "");
    if (!text || text === "Unknown") {
        return text || "Unknown";
    }

    const segments = text.split(/[\\/]+/).filter(Boolean);
    if (segments.length <= 4) {
        return text;
    }

    return `...\\${segments.slice(-4).join("\\")}`;
}

function escapeHtml(value) {
    return String(value ?? "")
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll('"', "&quot;")
        .replaceAll("'", "&#39;");
}

function dedupeBy(items, keySelector) {
    const seen = new Set();
    return items.filter((item) => {
        const key = keySelector(item);
        if (seen.has(key)) {
            return false;
        }

        seen.add(key);
        return true;
    });
}

function toTitleCase(value) {
    return value.replace(/\b\w/g, (character) => character.toUpperCase());
}

function buildRefreshMeta(refreshedAt, fetchDurationMs, renderDurationMs, serverLatencyMs, freshnessNote = null) {
    const modeNote = state.autoRefresh
        ? "Live refresh pauses while the tab is hidden."
        : "Manual refresh mode.";
    const freshnessSegment = freshnessNote ? ` ${freshnessNote}.` : "";
    return `Updated ${timeFormatter.format(refreshedAt)} in ${formatNumber(fetchDurationMs)} ms fetch / ${formatNumber(renderDurationMs)} ms paint / ${formatNumber(serverLatencyMs || 0)} ms server.${freshnessSegment} ${modeNote}`;
}

function observeUiVitals() {
    if (typeof PerformanceObserver !== "function") {
        return;
    }

    const supportedEntryTypes = PerformanceObserver.supportedEntryTypes || [];

    if (supportedEntryTypes.includes("largest-contentful-paint")) {
        const lcpObserver = new PerformanceObserver((list) => {
            const entries = list.getEntries();
            const lastEntry = entries[entries.length - 1];
            if (!lastEntry) {
                return;
            }

            state.uiVitals.lcpMs = Math.round(lastEntry.startTime);
            renderOverviewSummaryFromState();
        });

        lcpObserver.observe({ type: "largest-contentful-paint", buffered: true });

        document.addEventListener("visibilitychange", () => {
            if (document.visibilityState === "hidden") {
                lcpObserver.disconnect();
            }
        }, { once: true });
    }

    if (supportedEntryTypes.includes("layout-shift")) {
        const clsObserver = new PerformanceObserver((list) => {
            let changed = false;
            for (const entry of list.getEntries()) {
                if (entry.hadRecentInput) {
                    continue;
                }

                state.uiVitals.cls += entry.value;
                changed = true;
            }

            if (changed) {
                renderOverviewSummaryFromState();
            }
        });

        clsObserver.observe({ type: "layout-shift", buffered: true });
    }
}

function handleVisibilityChange() {
    if (document.hidden) {
        if (state.timerId) {
            window.clearTimeout(state.timerId);
            state.timerId = null;
        }

        updateMeta("Live refresh paused while this tab is hidden.");
        return;
    }

    if (state.lastSuccessfulRefreshAt && state.lastFetchDurationMs && state.lastRenderDurationMs) {
        updateMeta(buildRefreshMeta(
            state.lastSuccessfulRefreshAt,
            state.lastFetchDurationMs,
            state.lastRenderDurationMs,
            state.lastServerLatencyMs));
    }

    if (state.autoRefresh && !state.refreshing) {
        void refreshDashboard({ announce: false });
    }
}

function setMarkup(element, html) {
    if (renderCache.get(element) === html) {
        return;
    }

    element.innerHTML = html;
    renderCache.set(element, html);
}

function renderOverviewSummaryFromState() {
    if (!state.latestDashboard) {
        return;
    }

    renderOverviewSummary(
        state.latestDashboard.Health,
        state.latestDashboard.World,
        state.latestDashboard.ServerLatencyMs,
        state.latestDashboard.InferencePerformance);
}

// --------------------------------------------------------------------
// Quickstart panel
// --------------------------------------------------------------------
// Reads /api/quickstart on every refresh and renders the structured
// "what should I do next?" guidance. Same data that AI / MCP callers see;
// humans get a visual card treatment so new operators know immediately
// whether the sidecar is ready or needs setup, without reading any docs.
// --------------------------------------------------------------------

async function refreshQuickstart() {
    const panel = document.getElementById("quickstart");
    const headline = document.getElementById("quickstart-headline");
    const list = document.getElementById("quickstart-steps");
    if (!panel || !headline || !list) {
        return;
    }

    try {
        const response = await fetch(QUICKSTART_ENDPOINT, {
            headers: { Accept: "application/json" },
            cache: "no-store",
        });
        if (!response.ok) {
            panel.setAttribute("data-state", "error");
            headline.textContent = `Quickstart guidance unavailable (HTTP ${response.status}).`;
            list.innerHTML = "";
            return;
        }
        const guide = await response.json();
        renderQuickstartGuide(guide, panel, headline, list);
    } catch (err) {
        panel.setAttribute("data-state", "error");
        headline.textContent = "Quickstart guidance unavailable — see the server log.";
        list.innerHTML = "";
    }
}

function renderQuickstartGuide(guide, panel, headline, list) {
    const status = (guide.OverallStatus || "ready").toLowerCase();
    panel.setAttribute("data-state", status);
    headline.textContent = guide.Headline || "PalLLM is operational.";

    const steps = Array.isArray(guide.Steps) ? guide.Steps : [];
    if (steps.length === 0) {
        list.innerHTML = `<li class="quickstart-step quickstart-step--ready"><span class="quickstart-priority">ready</span><div class="quickstart-step-body"><p class="quickstart-label">Nothing required.</p><p class="quickstart-why">All critical and recommended surfaces are live. Optional upgrades would only be quality-of-life.</p></div></li>`;
        return;
    }

    const fragments = steps.map((step) => {
        const priority = escapeHtml(String(step.Priority || "optional").toLowerCase());
        const label = escapeHtml(step.Label || "");
        const why = escapeHtml(step.Why || "");
        const action = escapeHtml(step.Action || "");
        const verify = escapeHtml(step.Verify || "");
        return `
            <li class="quickstart-step quickstart-step--${priority}">
                <span class="quickstart-priority">${priority}</span>
                <div class="quickstart-step-body">
                    <p class="quickstart-label">${label}</p>
                    <p class="quickstart-why">${why}</p>
                    <dl class="quickstart-instructions">
                        <dt>Do</dt><dd>${action}</dd>
                        <dt>Verify</dt><dd>${verify}</dd>
                    </dl>
                </div>
            </li>`;
    });

    list.innerHTML = fragments.join("");
}

function escapeHtml(text) {
    return String(text)
        .replaceAll("&", "&amp;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;")
        .replaceAll("\"", "&quot;")
        .replaceAll("'", "&#39;");
}

// --------------------------------------------------------------------
// Live runtime suggestions panel
// --------------------------------------------------------------------
// Reads /api/health.suggestions[] on every refresh. Each entry carries
// a stable Code (e.g. 'no-packs-loaded', 'inference-circuit-open'), a
// human Message, and an optional copy-paste Command. Hidden via the
// .is-empty CSS modifier when the runtime is healthy. Same surface as
// `pal next`, `pal doctor`, and the `pal_health_suggestions` MCP tool;
// this just gives every dashboard viewer the at-a-glance signal too.
// --------------------------------------------------------------------

// Severity buckets recognised by the CSS. Anything outside this set
// falls through to "warn" so a future builder addition still lights up
// reasonably until consumers update.
const SUGGESTION_SEVERITY_VALUES = new Set(["info", "warn", "urgent"]);

async function refreshSuggestions() {
    const panel = document.getElementById("suggestions");
    const headline = document.getElementById("suggestions-headline");
    const list = document.getElementById("suggestions-list");
    if (!panel || !headline || !list) {
        return;
    }

    try {
        const response = await fetch(HEALTH_ENDPOINT, {
            headers: { Accept: "application/json" },
            cache: "no-store",
        });
        if (!response.ok) {
            // Degrade quietly: the rest of the dashboard already surfaces
            // hard sidecar errors; the suggestions panel just hides.
            panel.classList.add("is-empty");
            return;
        }
        const health = await response.json();
        const suggestions = Array.isArray(health.Suggestions)
            ? health.Suggestions
            : Array.isArray(health.suggestions)
                ? health.suggestions
                : [];
        renderSuggestions(suggestions, panel, headline, list);
    } catch (err) {
        // Same quiet-degrade story for transport errors.
        panel.classList.add("is-empty");
    }
}

function renderSuggestions(suggestions, panel, headline, list) {
    if (!suggestions || suggestions.length === 0) {
        panel.classList.add("is-empty");
        panel.setAttribute("data-state", "ready");
        list.innerHTML = "";
        renderSuggestionsBadge([]);
        return;
    }

    panel.classList.remove("is-empty");
    panel.setAttribute("data-state", "active");
    headline.textContent = suggestions.length === 1
        ? "One live signal — the runtime spotted something worth your attention."
        : `${suggestions.length} live signals — the runtime spotted things worth your attention.`;
    renderSuggestionsBadge(suggestions);

    const fragments = suggestions.map((entry, index) => {
        const code = entry.Code || entry.code || "unknown";
        const message = entry.Message || entry.message || "";
        const command = entry.Command || entry.command || "";
        // Severity now comes from the builder itself (Pass-135) -- the
        // server is the single source of truth, so the dashboard never
        // drifts when new hint codes land. Unknown values fall back to
        // "warn" so we still render a visible card.
        const rawSeverity = entry.Severity || entry.severity || "warn";
        const severity = SUGGESTION_SEVERITY_VALUES.has(rawSeverity) ? rawSeverity : "warn";
        const codeHtml = escapeHtml(code);
        const messageHtml = escapeHtml(message);
        // One-click Copy affordance per command. We render the command
        // in a flex row alongside a button rather than triple-click-and-
        // Ctrl-C; the click handler is wired by event delegation below.
        // The data-copy-text attribute carries the unescaped command so
        // the click handler can hand it directly to navigator.clipboard
        // without re-decoding the rendered HTML.
        const commandHtml = command
            ? `
                <div class="suggestions-command-row">
                    <pre class="suggestions-command"><code>${escapeHtml(command)}</code></pre>
                    <button type="button"
                            class="suggestions-copy-button"
                            data-copy-text="${escapeHtml(command)}"
                            data-copy-index="${index}"
                            aria-label="Copy command to clipboard"
                            title="Copy this command">Copy</button>
                </div>`
            : "";
        return `
            <li class="suggestions-card" data-severity="${escapeHtml(severity)}">
                <span class="suggestions-code">${codeHtml}</span>
                <div class="suggestions-body">
                    <p class="suggestions-message">${messageHtml}</p>
                    ${commandHtml}
                </div>
            </li>`;
    });

    list.innerHTML = fragments.join("");
}

// One-click clipboard handler for Suggestion-card Copy buttons. Wired
// once at module load via event delegation so we don't have to re-bind
// every time renderSuggestions() rewrites the list. Falls back to a
// document.execCommand path on older browsers / file:// schemes where
// the modern Clipboard API is gated.
function wireSuggestionsCopyHandler() {
    const list = document.getElementById("suggestions-list");
    if (!list) {
        return;
    }
    list.addEventListener("click", async (ev) => {
        const button = ev.target.closest(".suggestions-copy-button");
        if (!button) {
            return;
        }
        ev.preventDefault();
        const text = button.getAttribute("data-copy-text") || "";
        if (!text) {
            return;
        }
        let copied = false;
        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                copied = true;
            }
        } catch (err) {
            copied = false;
        }
        if (!copied) {
            // Older browsers + non-HTTPS local pages: fall back to a
            // hidden textarea + execCommand. Best-effort -- if both
            // paths fail the operator can still triple-click the
            // command text.
            try {
                const ta = document.createElement("textarea");
                ta.value = text;
                ta.setAttribute("readonly", "");
                ta.style.position = "absolute";
                ta.style.left = "-9999px";
                document.body.appendChild(ta);
                ta.select();
                copied = document.execCommand("copy");
                document.body.removeChild(ta);
            } catch (err) {
                copied = false;
            }
        }
        // Visual feedback: swap the label to "Copied!" (or "Copy failed")
        // for ~1.5 s, then restore. The class toggle drives the CSS
        // colour swap.
        const original = button.textContent;
        button.textContent = copied ? "Copied!" : "Copy failed";
        button.classList.toggle("is-copied", copied);
        button.classList.toggle("is-copy-failed", !copied);
        button.disabled = true;
        setTimeout(() => {
            button.textContent = original;
            button.classList.remove("is-copied");
            button.classList.remove("is-copy-failed");
            button.disabled = false;
        }, 1500);
    });
}

// Wire the copy handler exactly once at script-load time.
wireSuggestionsCopyHandler();

// Topbar suggestions badge. Visible from any scroll position; colour
// matches the highest severity present (urgent > warn > info). Hidden
// (via .is-empty) when the runtime is healthy. The badge text uses the
// total count and colour the most-severe bucket so a glance at the topbar
// answers "is anything wrong right now?" without scrolling.
function renderSuggestionsBadge(suggestions) {
    const badge = document.getElementById("suggestions-badge");
    const text = document.getElementById("suggestions-badge-text");
    const icon = document.getElementById("suggestions-badge-icon");
    if (!badge || !text || !icon) {
        return;
    }
    if (!suggestions || suggestions.length === 0) {
        badge.classList.add("is-empty");
        badge.setAttribute("data-severity", "info");
        return;
    }
    let urgent = 0;
    let warn = 0;
    let info = 0;
    for (const entry of suggestions) {
        const sev = entry.Severity || entry.severity || "warn";
        if (sev === "urgent") urgent += 1;
        else if (sev === "warn") warn += 1;
        else if (sev === "info") info += 1;
    }
    const dominant = urgent > 0 ? "urgent" : (warn > 0 ? "warn" : "info");
    badge.classList.remove("is-empty");
    badge.setAttribute("data-severity", dominant);
    icon.textContent = dominant === "urgent" ? "!" : (dominant === "warn" ? "!" : "i");
    const total = suggestions.length;
    text.textContent = total === 1
        ? `1 ${dominant} signal`
        : `${total} signals (${urgent} urgent, ${warn} warn, ${info} info)`;
}

// Hook quickstart + suggestions refresh into the same cadence as the
// main dashboard refresh. We wrap the existing refreshDashboard entry
// point so both panels stay in sync with the operator's manual refreshes
// + auto-refresh timer without needing their own polling loops.
const __originalRefreshDashboard = refreshDashboard;
refreshDashboard = async function wrappedRefresh(opts) {
    try {
        await __originalRefreshDashboard(opts);
    } finally {
        // Fire-and-forget — quickstart / suggestions failures never block
        // the main dashboard render.
        void refreshQuickstart();
        void refreshSuggestions();
    }
};

// Kick off an initial quickstart + suggestions fetch so the panels
// aren't blank before the first refresh tick lands.
void refreshQuickstart();
void refreshSuggestions();

// --------------------------------------------------------------------
// Self-healing watchdog chip
// --------------------------------------------------------------------
// Reads /api/self-healing/status and shows a compact status pill in the
// top ribbon so operators can see the background janitor is alive. Shares
// the dashboard refresh cadence via the wrappedRefresh hook above.
// --------------------------------------------------------------------

async function refreshSelfHealingChip() {
    const chip = document.getElementById("self-healing-chip");
    if (!chip) {
        return;
    }

    try {
        const response = await fetch(SELF_HEALING_ENDPOINT, {
            headers: { Accept: "application/json" },
            cache: "no-store",
        });
        if (!response.ok) {
            chip.setAttribute("data-state", "error");
            chip.textContent = `Watchdog: HTTP ${response.status}`;
            return;
        }
        const payload = await response.json();
        renderSelfHealingChip(payload, chip);
    } catch (_err) {
        chip.setAttribute("data-state", "error");
        chip.textContent = "Watchdog: unreachable";
    }
}

function renderSelfHealingChip(payload, chip) {
    // Pending marker -> watchdog has not ticked yet.
    if (payload && typeof payload.status === "string") {
        if (payload.status === "pending") {
            chip.setAttribute("data-state", "pending");
            chip.textContent = "Watchdog: pending first tick";
            chip.title = payload.detail || "";
            return;
        }
        if (payload.status === "unreadable") {
            chip.setAttribute("data-state", "error");
            chip.textContent = "Watchdog: unreadable evidence";
            chip.title = payload.detail || "";
            return;
        }
    }

    // Real evidence payload.
    const capturedAt = payload.CapturedAtUtc ? new Date(payload.CapturedAtUtc) : null;
    const archived = Number(payload.OrphanEnvelopesArchived || 0);
    const score = payload.OperatorHealth?.Score;
    const grade = payload.OperatorHealth?.Grade;

    let ageLabel = "just now";
    if (capturedAt) {
        const seconds = Math.max(0, Math.round((Date.now() - capturedAt.getTime()) / 1000));
        if (seconds < 60) {
            ageLabel = `${seconds}s ago`;
        } else if (seconds < 3600) {
            ageLabel = `${Math.round(seconds / 60)}m ago`;
        } else {
            ageLabel = `${Math.round(seconds / 3600)}h ago`;
        }
    }

    chip.setAttribute("data-state", archived > 0 ? "active" : "quiet");
    const archivedLabel = archived > 0 ? `archived ${archived}` : "no orphans";
    chip.textContent = `Watchdog: ${ageLabel} · ${archivedLabel}`;
    chip.title = score !== undefined
        ? `Last tick saw operator-health ${score}/100 (${grade}).`
        : "";
}

// Also ride the wrapped refresh. wrappedRefresh already re-fires
// refreshQuickstart; add the chip alongside it.
const __originalWrappedRefresh = refreshDashboard;
refreshDashboard = async function wrappedRefreshWithChip(opts) {
    try {
        await __originalWrappedRefresh(opts);
    } finally {
        void refreshSelfHealingChip();
    }
};

void refreshSelfHealingChip();

// --------------------------------------------------------------------
// Why panel
// --------------------------------------------------------------------
// Natural-language causal questions -> POST /api/why -> render the
// structured WhyAnswer as a numbered causal chain. Deterministic-first:
// the backend never calls out to live inference, so this panel always
// answers even when every external dependency is down.
// --------------------------------------------------------------------

function setupWhyPanel() {
    const form = document.getElementById("why-form");
    const input = document.getElementById("why-input");
    const answer = document.getElementById("why-answer");
    const suggestions = document.getElementById("why-suggestions");
    if (!form || !input || !answer) {
        return;
    }

    form.addEventListener("submit", async (ev) => {
        ev.preventDefault();
        const question = input.value.trim();
        if (!question) {
            return;
        }
        await submitWhyQuestion(question, input, answer);
    });

    if (suggestions) {
        suggestions.addEventListener("click", async (ev) => {
            const target = ev.target instanceof HTMLElement ? ev.target : null;
            if (!target || target.tagName !== "BUTTON") {
                return;
            }
            const preset = target.getAttribute("data-why-preset");
            if (!preset) {
                return;
            }
            input.value = preset;
            await submitWhyQuestion(preset, input, answer);
        });
    }
}

async function submitWhyQuestion(question, input, answer) {
    answer.setAttribute("data-state", "loading");
    answer.innerHTML = `<p class="why-placeholder">Thinking&hellip;</p>`;
    input.disabled = true;

    try {
        const response = await fetch(WHY_ENDPOINT, {
            method: "POST",
            headers: {
                Accept: "application/json",
                "Content-Type": "application/json",
            },
            cache: "no-store",
            body: JSON.stringify({ Question: question }),
        });
        if (!response.ok) {
            answer.setAttribute("data-state", "error");
            answer.innerHTML = `<p class="why-error">The runtime returned HTTP ${response.status}. Try a shorter or different question.</p>`;
            return;
        }
        const payload = await response.json();
        renderWhyAnswer(payload, answer);
    } catch (_err) {
        answer.setAttribute("data-state", "error");
        answer.innerHTML = `<p class="why-error">Could not reach /api/why. Is the sidecar still running?</p>`;
    } finally {
        input.disabled = false;
        input.focus();
    }
}

function renderWhyAnswer(payload, answer) {
    const intent = escapeHtml(payload.Intent || "Unknown");
    const primary = escapeHtml(payload.PrimaryReason || "");
    const confidence = escapeHtml(payload.Confidence || "");
    const chain = Array.isArray(payload.CausalChain) ? payload.CausalChain : [];
    const evidence = Array.isArray(payload.EvidenceReferences) ? payload.EvidenceReferences : [];

    const chainHtml = chain.length === 0
        ? ""
        : `<ol class="why-chain">${chain.map((line) => `<li>${escapeHtml(line)}</li>`).join("")}</ol>`;

    const evidenceHtml = evidence.length === 0
        ? ""
        : `<p class="why-evidence"><strong>Evidence</strong> ${evidence.map((e) => `<code>${escapeHtml(e)}</code>`).join(" &middot; ")}</p>`;

    answer.setAttribute("data-state", "ready");
    answer.setAttribute("data-intent", intent.toLowerCase());
    answer.innerHTML = `
        <header class="why-answer-header">
            <span class="why-intent">${intent}</span>
            <span class="why-confidence">confidence: ${confidence}</span>
        </header>
        <p class="why-primary">${primary}</p>
        ${chainHtml}
        ${evidenceHtml}
    `;
}

setupWhyPanel();

// --------------------------------------------------------------------
// Local-first mesh role coverage panel
// --------------------------------------------------------------------
// Reads /api/roles on every refresh and renders a 5-slot grid showing
// which of the Edge / Worker / Judge / Media / Validator roles are
// active, configured-but-disabled, or missing. Pairing pattern becomes
// the surface-copy headline so operators see at a glance what their
// mesh topology looks like.
// --------------------------------------------------------------------

async function refreshMeshRoles() {
    const panel = document.getElementById("mesh");
    const pairing = document.getElementById("mesh-pairing");
    const list = document.getElementById("mesh-slots");
    if (!panel || !pairing || !list) {
        return;
    }
    try {
        const response = await fetch(ROLES_ENDPOINT, {
            headers: { Accept: "application/json" },
            cache: "no-store",
        });
        if (!response.ok) {
            panel.setAttribute("data-state", "error");
            pairing.textContent = `Mesh role coverage unavailable (HTTP ${response.status}).`;
            list.innerHTML = "";
            return;
        }
        const coverage = await response.json();
        renderMeshCoverage(coverage, panel, pairing, list);
        // Fire-and-forget: fetch the default Qwen Duo cooperation plan
        // and append it below the pairing text so operators see what
        // pattern the orchestrator would pick for a standard
        // ImplementDraft task. Failure never blocks the main render.
        void appendDuoPlanRecommendation(pairing);
    } catch (_err) {
        panel.setAttribute("data-state", "error");
        pairing.textContent = "Mesh role coverage unavailable — see the server log.";
        list.innerHTML = "";
    }
}

async function appendDuoPlanRecommendation(pairing) {
    try {
        const response = await fetch(DUO_PLAN_ENDPOINT, {
            method: "POST",
            headers: { Accept: "application/json", "Content-Type": "application/json" },
            cache: "no-store",
            body: JSON.stringify({}),
        });
        if (!response.ok) {
            return;
        }
        const plan = await response.json();
        const patternLabel = DUO_PATTERN_LABELS[plan.Pattern] ?? `Pattern ${plan.Pattern}`;
        const pairingHtml = pairing.innerHTML;
        pairing.innerHTML = `${pairingHtml}<br><small class="mesh-duo-hint">
            <strong>Default Duo pattern:</strong> ${escapeHtml(patternLabel)}
            &mdash; <em>${escapeHtml(plan.Why || "")}</em>
        </small>`;
    } catch (_err) {
        // Duo plan is optional polish; if it fails we silently skip it.
    }
}

function renderMeshCoverage(coverage, panel, pairing, list) {
    const gaps = Array.isArray(coverage.CriticalGaps) ? coverage.CriticalGaps : [];
    const state = gaps.length > 0 ? "gaps" : (coverage.ActiveBindings > 0 ? "ready" : "empty");
    panel.setAttribute("data-state", state);
    pairing.textContent = coverage.PairingPattern || "";

    const slots = Array.isArray(coverage.Slots) ? coverage.Slots : [];
    if (slots.length === 0) {
        list.innerHTML = "";
        return;
    }

    list.innerHTML = slots.map((slot) => {
        const role = escapeHtml(slot.Role || "");
        const description = escapeHtml(slot.Description || "");
        const recommendation = escapeHtml(slot.Recommendation || "");
        const isActive = Boolean(slot.IsActive);
        const isConfigured = Boolean(slot.IsConfigured);
        const slotState = isActive ? "active" : (isConfigured ? "configured" : "missing");
        const bindings = Array.isArray(slot.Bindings) ? slot.Bindings : [];
        const activeBinding = bindings.find((b) => b && b.Enabled);
        const label = activeBinding
            ? `${escapeHtml(activeBinding.Id || "(unnamed)")} <small>${escapeHtml(activeBinding.ModelId || "")}</small>`
            : isConfigured
                ? `<em>${bindings.length} binding(s) declared, none enabled</em>`
                : `<em>No binding declared</em>`;
        return `
            <li class="mesh-slot mesh-slot--${slotState}">
                <div class="mesh-slot-head">
                    <span class="mesh-role">${role}</span>
                    <span class="mesh-state">${slotState}</span>
                </div>
                <p class="mesh-binding">${label}</p>
                <p class="mesh-description">${description}</p>
                ${!isActive ? `<p class="mesh-recommendation"><strong>Recommendation:</strong> ${recommendation}</p>` : ""}
            </li>`;
    }).join("");
}

// Ride the refresh cadence so mesh coverage updates when operators
// change appsettings.json + restart, without adding a new polling loop.
const __originalWrappedWithChipAndMesh = refreshDashboard;
refreshDashboard = async function wrappedRefreshWithMesh(opts) {
    try {
        await __originalWrappedWithChipAndMesh(opts);
    } finally {
        void refreshMeshRoles();
    }
};

void refreshMeshRoles();

// --------------------------------------------------------------------
// Hard-code promotion panel
// --------------------------------------------------------------------
// Reads /api/promotion/summary on every dashboard refresh and renders
// per-task cards that flag which AI-assisted patterns are stable
// enough to promote into deterministic product logic. No separate
// polling loop — rides the existing dashboard cadence.
// --------------------------------------------------------------------

async function refreshPromotionPanel() {
    const panel = document.getElementById("promotion");
    const headline = document.getElementById("promotion-headline");
    const list = document.getElementById("promotion-tasks");
    if (!panel || !headline || !list) {
        return;
    }

    try {
        // Fetch summary + suggestions in parallel. Suggestions are a
        // pure derivation of summary candidates, so if the summary is
        // empty the suggestions endpoint returns an empty set too.
        const [summaryResponse, suggestionsResponse] = await Promise.all([
            fetch(PROMOTION_SUMMARY_ENDPOINT, {
                headers: { Accept: "application/json" },
                cache: "no-store",
            }),
            fetch(PROMOTION_SUGGESTIONS_ENDPOINT, {
                headers: { Accept: "application/json" },
                cache: "no-store",
            }),
        ]);
        if (!summaryResponse.ok) {
            panel.setAttribute("data-state", "error");
            headline.textContent = `Promotion ledger unavailable (HTTP ${summaryResponse.status}).`;
            list.innerHTML = "";
            return;
        }
        const summary = await summaryResponse.json();
        // Suggestions is optional polish; if it fails we still render.
        let suggestionsByTask = {};
        if (suggestionsResponse.ok) {
            const set = await suggestionsResponse.json();
            if (Array.isArray(set.Suggestions)) {
                for (const suggestion of set.Suggestions) {
                    if (suggestion && suggestion.TaskClass) {
                        suggestionsByTask[suggestion.TaskClass] = suggestion;
                    }
                }
            }
        }
        renderPromotionSummary(summary, suggestionsByTask, panel, headline, list);
    } catch (_err) {
        panel.setAttribute("data-state", "error");
        headline.textContent = "Promotion ledger unavailable — see the server log.";
        list.innerHTML = "";
    }
}

function renderPromotionSummary(summary, suggestionsByTask, panel, headline, list) {
    const tasks = Array.isArray(summary.Tasks) ? summary.Tasks : [];
    const candidateCount = Number(summary.PromotionCandidateCount || 0);

    if (tasks.length === 0) {
        panel.setAttribute("data-state", "empty");
        headline.textContent = "No observations yet. The feeder records one ledger entry per fallback-strategy fire; promote patterns once they pass the stability gate.";
        list.innerHTML = `<li class="promotion-task promotion-task--empty"><p class="promotion-empty">Nothing to review right now.</p></li>`;
        return;
    }

    const state = candidateCount > 0 ? "candidates" : "collecting";
    panel.setAttribute("data-state", state);
    headline.textContent = candidateCount > 0
        ? `${candidateCount} pattern${candidateCount === 1 ? "" : "s"} stable enough to hard-code into deterministic logic.`
        : `Collecting observations across ${tasks.length} task class${tasks.length === 1 ? "" : "es"}; none have crossed the stability gate yet.`;

    // Sort: candidates first, then by total observations descending.
    const sorted = [...tasks].sort((a, b) => {
        if (a.IsPromotionCandidate !== b.IsPromotionCandidate) {
            return a.IsPromotionCandidate ? -1 : 1;
        }
        return Number(b.TotalObservations || 0) - Number(a.TotalObservations || 0);
    });

    list.innerHTML = sorted.map((task) => {
        const taskState = task.IsPromotionCandidate
            ? "candidate"
            : (Number(task.DisagreementBlockCount || 0) + Number(task.HumanOverrideCount || 0) > 0 ? "blocked" : "collecting");
        const taskClass = escapeHtml(task.TaskClass || "");
        const mostCommon = escapeHtml(task.MostCommonPatternId || "");
        const recommendation = escapeHtml(task.Recommendation || "");
        const successRate = Math.round(Number(task.SuccessRate || 0) * 100);
        const total = Number(task.TotalObservations || 0);
        const successCount = Number(task.SuccessCount || 0);
        const disagreement = Number(task.DisagreementBlockCount || 0);
        const validatorFail = Number(task.ValidatorFailCount || 0);
        const humanOverride = Number(task.HumanOverrideCount || 0);

        // If this task is a candidate AND the suggestions endpoint
        // returned a matching entry, inline the suggestion details so
        // operators see exactly what to change without an extra click.
        let suggestionBlock = "";
        const suggestion = task.IsPromotionCandidate && suggestionsByTask ? suggestionsByTask[task.TaskClass] : null;
        if (suggestion) {
            suggestionBlock = `
                <details class="promotion-suggestion" open>
                    <summary>Suggested hard-code</summary>
                    <dl class="promotion-suggestion-body">
                        <dt>Target</dt><dd><code>${escapeHtml(suggestion.TargetFile || "")}</code></dd>
                        <dt>Change</dt><dd>${escapeHtml(suggestion.SuggestedChange || "")}</dd>
                        <dt>Evidence</dt><dd>${escapeHtml(suggestion.EvidenceSummary || "")}</dd>
                        <dt>Rollback</dt><dd>${escapeHtml(suggestion.RollbackPath || "")}</dd>
                    </dl>
                </details>`;
        }

        return `
            <li class="promotion-task promotion-task--${taskState}">
                <div class="promotion-task-head">
                    <span class="promotion-task-class">${taskClass}</span>
                    <span class="promotion-state">${taskState}</span>
                </div>
                <p class="promotion-metrics">
                    <strong>${successCount}</strong> / ${total} success · ${successRate}% rate
                    ${disagreement > 0 ? `&middot; <span class="promotion-neg">${disagreement} block</span>` : ""}
                    ${validatorFail > 0 ? `&middot; <span class="promotion-neg">${validatorFail} validator-fail</span>` : ""}
                    ${humanOverride > 0 ? `&middot; <span class="promotion-neg">${humanOverride} override</span>` : ""}
                </p>
                ${mostCommon ? `<p class="promotion-pattern">Most common: <code>${mostCommon}</code></p>` : ""}
                <p class="promotion-reco">${recommendation}</p>
                ${suggestionBlock}
            </li>`;
    }).join("");
}

const __originalWithPromotion = refreshDashboard;
refreshDashboard = async function wrappedRefreshWithPromotion(opts) {
    try {
        await __originalWithPromotion(opts);
    } finally {
        void refreshPromotionPanel();
    }
};

void refreshPromotionPanel();

// --------------------------------------------------------------------
// Chat panel — interactive companion from the dashboard
// --------------------------------------------------------------------
// Lets operators actually TRY the companion without leaving the
// browser. Defaults to POST /api/chat; flipping the Stream toggle
// switches to POST /api/chat/stream so phase events render live.
// Each turn runs a parallel /api/chat/plan call so the inferred Duo
// task kind + recommended cooperation pattern surface as chips
// alongside the reply.
// --------------------------------------------------------------------

function setupChatPanel() {
    const form = document.getElementById("chat-form");
    const input = document.getElementById("chat-input");
    const streamToggle = document.getElementById("chat-stream-toggle");
    const characterField = document.getElementById("chat-character");
    const history = document.getElementById("chat-history");
    if (!form || !input || !history) {
        return;
    }

    form.addEventListener("submit", async (ev) => {
        ev.preventDefault();
        const message = (input.value || "").trim();
        if (!message) { return; }
        const streaming = !!(streamToggle && streamToggle.checked);
        const characterId = characterField && characterField.value.trim() ? characterField.value.trim() : null;

        const turnNode = appendChatTurn(history, message, streaming);
        input.value = "";
        input.focus();

        // Fire the advisory in parallel so we can show the chip even
        // if the actual chat takes longer (or fails).
        void fetchChatPlan(message).then((planAdvice) => {
            if (planAdvice) { renderChatPlanChip(turnNode, planAdvice); }
        });

        try {
            if (streaming) {
                await runChatStreaming(turnNode, message, characterId);
            } else {
                await runChatSync(turnNode, message, characterId);
            }
        } catch (err) {
            setChatTurnError(turnNode, `Chat failed: ${err && err.message ? err.message : "network error"}`);
        }
    });
}

async function fetchChatPlan(userMessage) {
    try {
        const response = await fetch(CHAT_PLAN_ENDPOINT, {
            method: "POST",
            headers: { Accept: "application/json", "Content-Type": "application/json" },
            cache: "no-store",
            body: JSON.stringify({ UserMessage: userMessage }),
        });
        if (!response.ok) { return null; }
        return await response.json();
    } catch (_err) {
        return null;
    }
}

async function runChatSync(turnNode, userMessage, characterId) {
    const payload = { UserMessage: userMessage };
    if (characterId) { payload.CharacterId = characterId; }
    const response = await fetch(CHAT_ENDPOINT, {
        method: "POST",
        headers: { Accept: "application/json", "Content-Type": "application/json" },
        cache: "no-store",
        body: JSON.stringify(payload),
    });
    if (!response.ok) {
        setChatTurnError(turnNode, `HTTP ${response.status}`);
        return;
    }
    const body = await response.json();
    renderChatReply(turnNode, body);
}

async function runChatStreaming(turnNode, userMessage, characterId) {
    const payload = { UserMessage: userMessage };
    if (characterId) { payload.CharacterId = characterId; }
    const response = await fetch(CHAT_STREAM_ENDPOINT, {
        method: "POST",
        headers: { Accept: "text/event-stream", "Content-Type": "application/json" },
        cache: "no-store",
        body: JSON.stringify(payload),
    });
    if (!response.ok || !response.body) {
        setChatTurnError(turnNode, `HTTP ${response.status}`);
        return;
    }

    const phaseList = turnNode.querySelector(".chat-phases");
    const reader = response.body.getReader();
    const decoder = new TextDecoder("utf-8");
    let buffer = "";
    let finalBody = null;

    for (;;) {
        const { value, done } = await reader.read();
        if (done) { break; }
        buffer += decoder.decode(value, { stream: true });

        // Parse SSE blocks: `event: <name>\ndata: <json>\n\n`
        let idx;
        while ((idx = buffer.indexOf("\n\n")) >= 0) {
            const block = buffer.substring(0, idx);
            buffer = buffer.substring(idx + 2);
            const eventMatch = block.match(/^event:\s*(\S+)/m);
            const dataMatch = block.match(/^data:\s*(.*)$/m);
            if (!eventMatch || !dataMatch) { continue; }
            const eventName = eventMatch[1];
            const dataJson = dataMatch[1];
            let data = null;
            try { data = JSON.parse(dataJson); } catch (_e) { data = dataJson; }

            if (eventName === "phase" && phaseList && data && data.name) {
                const li = document.createElement("li");
                li.className = "chat-phase-item";
                li.textContent = `${data.name}: ${data.detail || ""}`;
                phaseList.appendChild(li);
            } else if (eventName === "token" && data && typeof data.text === "string") {
                // Pass 23: render incremental reply text as each chunk
                // arrives. Client sees words appear live instead of
                // waiting for the final ChatResponse payload.
                let bubble = turnNode.querySelector(".chat-live-reply");
                if (!bubble) {
                    const assistantBlock = document.createElement("div");
                    assistantBlock.className = "chat-turn-assistant chat-turn-live";
                    assistantBlock.innerHTML = '<span class="chat-role">companion</span><p class="chat-live-reply chat-message"></p>';
                    turnNode.appendChild(assistantBlock);
                    bubble = assistantBlock.querySelector(".chat-live-reply");
                }
                bubble.textContent += data.text;
            } else if (eventName === "final") {
                finalBody = data;
            } else if (eventName === "error") {
                setChatTurnError(turnNode, data && data.message ? data.message : "stream error");
                return;
            }
        }
    }

    if (finalBody) {
        // Clear the incremental live-reply bubble now that the
        // structured ChatResponse will render the full assistant
        // block (phases, cues, speech, action).
        const liveBubble = turnNode.querySelector(".chat-turn-live");
        if (liveBubble) { liveBubble.remove(); }
        renderChatReply(turnNode, finalBody);
    }
}

function appendChatTurn(history, userMessage, streaming) {
    const li = document.createElement("li");
    li.className = "chat-turn" + (streaming ? " chat-turn--streaming" : "");
    li.innerHTML = `
        <div class="chat-turn-user">
            <span class="chat-role">you</span>
            <p class="chat-message">${escapeHtml(userMessage)}</p>
        </div>
        <div class="chat-turn-plan" aria-label="Inferred Duo plan">
            <span class="chat-chip chat-chip--pending" data-role="plan">planning&hellip;</span>
        </div>
        ${streaming ? `<ul class="chat-phases" aria-label="Streaming phases"></ul>` : ""}
        <div class="chat-turn-reply" aria-busy="true">
            <span class="chat-role">companion</span>
            <p class="chat-message chat-message--pending">Waiting for reply&hellip;</p>
        </div>`;
    history.appendChild(li);
    li.scrollIntoView({ behavior: "smooth", block: "end" });
    return li;
}

function renderChatPlanChip(turnNode, planAdvice) {
    const chip = turnNode.querySelector('[data-role="plan"]');
    if (!chip) { return; }
    const kind = escapeHtml(planAdvice.InferredTaskKind || "Unknown");
    const pattern = escapeHtml(
        planAdvice.Plan && DUO_PATTERN_LABELS[planAdvice.Plan.Pattern]
            ? DUO_PATTERN_LABELS[planAdvice.Plan.Pattern]
            : (planAdvice.Plan ? `Pattern ${planAdvice.Plan.Pattern}` : "")
    );
    chip.className = "chat-chip";
    chip.textContent = pattern ? `${kind} · ${pattern}` : kind;
    chip.title = planAdvice.Plan && planAdvice.Plan.Why ? planAdvice.Plan.Why : "";
}

function renderChatReply(turnNode, chatResponse) {
    const replyEl = turnNode.querySelector(".chat-turn-reply");
    if (!replyEl) { return; }
    replyEl.setAttribute("aria-busy", "false");

    const message = escapeHtml(chatResponse.AssistantMessage || "(empty reply)");
    const strategy = chatResponse.UsedFallback
        ? escapeHtml(chatResponse.FallbackStrategy || "fallback")
        : null;
    const path = escapeHtml(chatResponse.ResponsePath || "");
    const latency = chatResponse.LatencyMs !== undefined
        ? `${Number(chatResponse.LatencyMs).toFixed(0)}ms`
        : "";

    const cues = Array.isArray(chatResponse.PresentationCues) ? chatResponse.PresentationCues : [];
    const cueBlock = cues.length === 0 ? "" : `
        <div class="chat-cues" aria-label="Presentation cues">
            ${cues.slice(0, 6).map((cue) => `<span class="chat-cue">${escapeHtml((cue.Name || "") + (cue.Intensity ? ` (${cue.Intensity})` : ""))}</span>`).join("")}
        </div>`;

    replyEl.innerHTML = `
        <span class="chat-role">companion</span>
        <p class="chat-message">${message}</p>
        <p class="chat-meta">
            ${strategy ? `<span class="chat-chip chat-chip--fallback">fallback · ${strategy}</span>` : `<span class="chat-chip chat-chip--live">live inference</span>`}
            ${path ? `<span class="chat-chip chat-chip--path">${path}</span>` : ""}
            ${latency ? `<span class="chat-chip chat-chip--latency">${latency}</span>` : ""}
        </p>
        ${cueBlock}`;
}

function setChatTurnError(turnNode, detail) {
    const replyEl = turnNode.querySelector(".chat-turn-reply");
    if (!replyEl) { return; }
    replyEl.setAttribute("aria-busy", "false");
    replyEl.innerHTML = `
        <span class="chat-role">companion</span>
        <p class="chat-message chat-message--error">${escapeHtml(detail)}</p>`;
}

setupChatPanel();
