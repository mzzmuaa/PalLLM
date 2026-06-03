// ---------------------------------------------------------------------------
// AGENT-CARD:
//   what:    Strongly-typed runtime configuration. Every operator-facing knob
//            (Bridge / Inference / Fallback / Tts / Asr / Vision / Session /
//            Automation / Auth / Http / McpClient) lives here as a nested
//            options class with compiled defaults. Bound from
//            appsettings.json's "PalLLM" section + PalLLM__Section__Key env
//            var overrides.
//   surface: PalLlmOptions (root); BridgeOptions / InferenceOptions /
//            FallbackOptions / TtsOptions / AsrOptions / VisionOptions / SessionOptions /
//            AutomationOptions / AuthOptions / HttpSurfaceOptions /
//            McpClientOptions (nested).
//   gate:    None directly; option validation lives in PalLlmOptionsValidator.
//   adr:     0006-opt-in-everything-by-default.md (every privacy-sensitive opt-in is off
//            by default; the wizard NEVER flips defaults without explicit
//            consent).
//   docs:    docs/ENV_VARS.md (every knob with effects), docs/TUNING.md
//            (too-low / too-high guidance + per-knob test recipes),
//            scripts/pal-config-wizard.ps1 (interactive setup).
// ---------------------------------------------------------------------------

using System.Text.Json.Serialization;

namespace PalLLM.Domain.Configuration;

public sealed class PalLlmOptions
{
    public string PalSavedRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Pal",
        "Saved");

    public string RuntimeFolderName { get; set; } = "PalLLM";

    /// <summary>
    /// Optional absolute path that overrides where <see cref="ModelsDir"/> resolves
    /// to. Empty (default) keeps the runtime-root-anchored layout
    /// (<c>%LOCALAPPDATA%\Pal\Saved\PalLLM\Models</c>). Set to an operator-curated
    /// model library (e.g. <c>D:\Models</c>) to share weights across PalLLM
    /// installs and other inference tooling without duplicating GB-class files.
    /// PalLLM never writes to this directory automatically — it's an
    /// informational hint for the connector scripts and for any future advisor
    /// that needs to enumerate locally-available GGUFs.
    /// </summary>
    public string ExternalModelsRoot { get; set; } = string.Empty;

    public BridgeOptions Bridge { get; set; } = new();

    public InferenceOptions Inference { get; set; } = new();

    /// <summary>
    /// Background self-healing watchdog settings. When enabled, a
    /// conservative worker sweeps for orphan outbox envelopes on a cadence
    /// and writes a durable audit artifact under
    /// <c>Runtime/SelfHealingEvidence/latest-self-healing.json</c> so
    /// operators can see exactly what was automated. Defaults to on with
    /// safe thresholds; flip <see cref="SelfHealingOptions.Enabled"/> to
    /// false to disable the worker entirely.
    /// </summary>
    public SelfHealingOptions SelfHealing { get; set; } = new();

    /// <summary>
    /// Auto-feeder that converts <c>PalLlmMetrics</c> deltas into
    /// <c>PromotionLedger</c> observations on a cadence. Opt-in but
    /// defaults ON — the feeder is pure observer (reads metric counters,
    /// writes to the bounded in-memory ledger) so leaving it on is safe.
    /// Disable by flipping <see cref="PromotionFeederOptions.Enabled"/>
    /// if an operator would rather feed the ledger manually.
    /// </summary>
    public PromotionFeederOptions PromotionFeeder { get; set; } = new();

    /// <summary>
    /// Promotion-apply flow config (Pass 24). Controls whether
    /// <c>POST /api/promotion/apply</c> is allowed to write durable
    /// staging artifacts (template + rollback marker + audit packet)
    /// for a candidate promotion. Default is <see cref="PromotionApplyOptions.AllowApply"/>=false —
    /// the promotion pipeline is observation-only out of the box. The
    /// apply verb never mutates source code; it writes to
    /// <see cref="PromotionApplyOptions.StagingRoot"/> so a human
    /// reviewer can cherry-pick the change and commit it. Rollback is
    /// deletion of the staging artifacts.
    /// </summary>
    public PromotionApplyOptions PromotionApply { get; set; } = new();

    /// <summary>
    /// Pass 25 / D1 — hardware profile override. When
    /// <see cref="HardwareOptions.ForceTier"/> names a valid
    /// <c>DuoHardwareTier</c> enum value, the /api/hardware surface
    /// reports that tier as the effective tier regardless of
    /// detection. Empty or unparsable values are ignored and the
    /// detected tier wins.
    /// </summary>
    public HardwareOptions Hardware { get; set; } = new();

    /// <summary>
    /// Declarative role bindings for the local-first AI mesh. Each entry
    /// maps one of the five <c>ModelRole</c> values (Edge / Worker / Judge
    /// / Media / Validator) to a named endpoint + model pair. Used by
    /// <c>ModelRoleRegistry</c> to compute coverage, by <c>/api/roles</c>
    /// to report the configured mesh, and by <c>/api/quickstart</c> to
    /// nudge operators toward a stronger pairing.
    ///
    /// <para>The list is metadata-only today: binding a role does not
    /// automatically route inference traffic to that endpoint. It records
    /// intent so the mesh is legible to operators, AI clients, and
    /// validators. Future passes can add role-aware routing on top
    /// without changing the operator-facing shape.</para>
    /// </summary>
    public List<ModelRoleBinding> ModelRoles { get; set; } = new();

    public FallbackOptions Fallback { get; set; } = new();

    public VisionOptions Vision { get; set; } = new();

    public SessionOptions Session { get; set; } = new();

    public TtsOptions Tts { get; set; } = new();

    public AsrOptions Asr { get; set; } = new();

    public AutomationOptions Automation { get; set; } = new();

    public HttpSurfaceOptions Http { get; set; } = new();

    public AuthOptions Auth { get; set; } = new();

    public McpClientOptions McpClient { get; set; } = new();

    /// <summary>Pack resolution policy — currently the per-species personality-pack
    /// default map, so operators can pin one pack per Palworld species without
    /// authoring one pack per character id. See <see cref="PacksOptions"/>.</summary>
    public PacksOptions Packs { get; set; } = new();

    public string RuntimeRoot => Path.Combine(PalSavedRoot, RuntimeFolderName);

    /// <summary>
    /// Resolves the directory PalLLM treats as the canonical model library.
    /// When <see cref="ExternalModelsRoot"/> is set, that absolute path wins;
    /// otherwise the legacy runtime-root-anchored <c>Models</c> subdirectory is
    /// used. PalLLM never writes to this directory automatically — it's read by
    /// connector scripts and by any future "where do local GGUFs live?" advisor.
    /// </summary>
    public string ModelsDir => string.IsNullOrWhiteSpace(ExternalModelsRoot)
        ? Path.Combine(RuntimeRoot, "Models")
        : ExternalModelsRoot.Trim();

    /// <summary>
    /// Resolves the directory PalLLM treats as the canonical diffusion-model
    /// library (Stable Diffusion / Flux / Hunyuan / etc. weights for the
    /// future portrait-variant + scene-narration lane described in
    /// <c>docs/FUTURE_2035.md</c> idea #15). Always a <c>Diffusion</c>
    /// subdirectory of <see cref="ModelsDir"/> so it automatically tracks any
    /// <see cref="ExternalModelsRoot"/> override the operator sets — no
    /// separate config knob to keep in sync. Like <see cref="ModelsDir"/>,
    /// PalLLM never writes to this directory automatically; the diffusion
    /// endpoint owns its own weights file lifecycle.
    /// </summary>
    public string DiffusionModelsDir => Path.Combine(ModelsDir, "Diffusion");

    public string PackDir => Path.Combine(RuntimeRoot, "Packs");

    public string TtsDir => Path.Combine(RuntimeRoot, "TTS");

    public string BridgeRoot => Path.Combine(RuntimeRoot, "Bridge");

    public string BridgeInboxDir => Path.Combine(BridgeRoot, "Inbox");

    public string BridgeArchiveDir => Path.Combine(BridgeRoot, "Archive");

    public string BridgeFailedDir => Path.Combine(BridgeRoot, "Failed");

    public string BridgeOutboxDir => Path.Combine(BridgeRoot, "Outbox");

    public string BridgeScreenshotsDir => Path.Combine(BridgeRoot, "Screenshots");

    public string BridgeDiagnosticsDir => Path.Combine(BridgeRoot, "Diagnostics");

    public string ReleaseEvidenceDir => Path.Combine(RuntimeRoot, "ReleaseEvidence");

    public string ReleaseEvidenceHistoryDir => Path.Combine(ReleaseEvidenceDir, "History");

    public string SupportEvidenceDir => Path.Combine(RuntimeRoot, "SupportEvidence");

    public string SupportEvidenceHistoryDir => Path.Combine(SupportEvidenceDir, "History");

    public string LatestSmokeEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-smoke.json");

    public string LatestNativeProofEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-native-proof.json");

    public string LatestProofBundleEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-proof-bundle.json");

    public string LatestProofBundleArchivePath => Path.Combine(ReleaseEvidenceDir, "latest-proof-bundle.zip");

    public string LatestPackageVerificationEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-package-verification.json");

    public string LatestArtifactIntegrityEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-artifact-integrity.json");

    public string LatestFullAuditEvidencePath => Path.Combine(ReleaseEvidenceDir, "latest-full-audit.json");

    public string LatestSupportBundleEvidencePath => Path.Combine(SupportEvidenceDir, "latest-support-bundle.json");

    public string LatestSupportBundleArchivePath => Path.Combine(SupportEvidenceDir, "latest-support-bundle.zip");

    /// <summary>
    /// Maximum age, in hours, before release proof artifacts should be treated as
    /// stale and refreshed before a candidate package is trusted.
    /// </summary>
    public int ReleaseEvidenceFreshnessHours { get; set; } = 24;

    public string SessionFilePath => Path.Combine(RuntimeRoot, "session.json");

    private int _directoriesEnsured;

    public void EnsureDirectories()
    {
        // First call creates every runtime-owned directory. Subsequent calls are
        // free — the hot paths (every chat, every bridge drain, every screenshot
        // tick) all call this, and the directories outlive the sidecar once
        // they exist. Write sites still handle DirectoryNotFoundException, so a
        // user who deletes a directory mid-run just sees the next write retry
        // via the write site's own error handling.
        //
        // ModelsDir is intentionally NOT created here — PalLLM talks to
        // HTTP-reachable inference endpoints, so there is no local weights file
        // to store. It remains on the IPathProvider surface for portable
        // adapter-library compatibility; a consumer that actually downloads
        // models can create the directory lazily at write time.
        if (Interlocked.CompareExchange(ref _directoriesEnsured, 1, 0) != 0)
        {
            return;
        }

        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(PackDir);
        Directory.CreateDirectory(TtsDir);
        Directory.CreateDirectory(BridgeInboxDir);
        Directory.CreateDirectory(BridgeArchiveDir);
        Directory.CreateDirectory(BridgeFailedDir);
        Directory.CreateDirectory(BridgeOutboxDir);
        Directory.CreateDirectory(BridgeScreenshotsDir);
        Directory.CreateDirectory(BridgeDiagnosticsDir);
        Directory.CreateDirectory(ReleaseEvidenceHistoryDir);
        Directory.CreateDirectory(SupportEvidenceHistoryDir);
    }

    /// <summary>Forces the next <see cref="EnsureDirectories"/> call to re-create
    /// all runtime directories. Tests use this so each fixture's tmp root gets a
    /// fresh layout; production code should not call it during normal operation.</summary>
    public void ResetDirectoryCache() => Interlocked.Exchange(ref _directoriesEnsured, 0);
}

/// <summary>
/// Pack resolution policy. Currently exposes a single map: Palworld species name
/// (case-insensitive) -> personality-pack id. Lets operators pin one pack per
/// species (e.g. all same-species companions share the same timid-helper voice) without having
/// to author one pack per character id. Consumed by
/// <c>SpeciesPersonalityResolver.Resolve</c>; missing / empty entries are silently
/// skipped and the resolver falls through to the caller's fallback chain.
/// </summary>
public sealed class PacksOptions
{
    /// <summary>
    /// Species -> packId default map. Both keys and values are trimmed and
    /// empty/whitespace entries are ignored at resolve-time. Keys are matched
    /// case-insensitively (so <c>"species-alpha"</c> and <c>"Species-Alpha"</c> work the
    /// same). Values reference pack ids under
    /// <c>runtime-root/Packs/personalities/&lt;id&gt;/</c>. Empty dictionary
    /// disables the species-default lane entirely (default).
    /// </summary>
    public Dictionary<string, string> DefaultBySpecies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class BridgeOptions
{
    public bool Enabled { get; set; } = true;

    public int PollIntervalMs { get; set; } = 1_000;

    /// Upper bound on how many inbox events the background worker processes in one
    /// poll. Keeps long-running sessions responsive even if a producer dumps a large
    /// backlog into Bridge/Inbox; manual drains can still process the full queue.
    public int MaxEventsPerPoll { get; set; } = 32;

    /// Hard cap on a single Bridge/Inbox JSON envelope. Prevents a malformed or
    /// hostile local producer from forcing the bridge drain to deserialize
    /// arbitrarily large event files into memory on the hot filesystem ingest path.
    public int MaxInboxEventBytes { get; set; } = 65_536;

    public bool ArchiveProcessedEvents { get; set; } = true;

    /// Enables the reply return-channel. When true, every successful chat response is
    /// persisted as a JSON envelope in the Bridge/Outbox directory so UE4SS (or any
    /// other game-side consumer) can render the assistant message and presentation
    /// cues in-game without calling back into the sidecar.
    public bool OutboxEnabled { get; set; } = true;

    /// Retention cap for the outbox directory. Prevents unbounded growth when a
    /// game-side consumer isn't running. On write, the oldest files beyond this cap
    /// are deleted so the outbox never exceeds the configured size.
    public int OutboxMaxFiles { get; set; } = 100;

    /// Max age (hours) for outbox files. Files older than this are pruned on write.
    public int OutboxMaxAgeHours { get; set; } = 24;

    /// Retention cap for the archive directory. Bridge events and processed
    /// screenshots both archive here, so the cap is higher than the outbox's.
    public int ArchiveMaxFiles { get; set; } = 500;

    public int ArchiveMaxAgeHours { get; set; } = 72;

    /// Retention cap for the failed directory. Failures should be rare; keeping the
    /// last few hundred is enough for diagnostic pullback without letting a runaway
    /// producer pack the disk.
    public int FailedMaxFiles { get; set; } = 200;

    public int FailedMaxAgeHours { get; set; } = 168;

    /// Retention cap for widget-probe diagnostics. These dumps are useful for
    /// HUD discovery, but should stay bounded so repeated probe sessions do not
    /// silently accumulate under Bridge/Diagnostics forever.
    public int DiagnosticsMaxFiles { get; set; } = 128;

    public int DiagnosticsMaxAgeHours { get; set; } = 168;
}
