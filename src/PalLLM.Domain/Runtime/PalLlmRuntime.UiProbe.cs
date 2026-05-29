using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PalLLM.Domain.Integration;

namespace PalLLM.Domain.Runtime;

public sealed partial class PalLlmRuntime
{
    private static readonly TimeSpan UiProbeDiagnosticsSnapshotTtl = TimeSpan.FromSeconds(2);
    private const int UiProbeDumpCacheMaxEntries = 512;
    private static readonly string[] UiProbePositiveKeywords =
    [
        "hud",
        "overlay",
        "subtitle",
        "caption",
        "chat",
        "message",
        "guide",
        "companion",
        "status",
        "notification",
        "ingame",
        "mainhud",
        "playerhud",
        "reticle",
        "crosshair",
        "marker",
        "root",
    ];

    private static readonly string[] UiProbeNegativeKeywords =
    [
        "inventory",
        "map",
        "menu",
        "pause",
        "settings",
        "option",
        "title",
        "popup",
        "dialog",
        "loading",
        "craft",
        "technology",
        "palbox",
        "storage",
        "guild",
        "tutorial",
    ];

    private static readonly UiProbeDumpJsonContext UiProbeDumpJsonContextInstance = new(CreateBridgeJsonOptions());

    private readonly object _uiProbeDiagnosticsGate = new();
    private UiProbeDiagnosticsSnapshot _uiProbeDiagnostics = new();
    private readonly Dictionary<string, UiProbeDumpCacheEntry> _uiProbeDumpCache = new(StringComparer.OrdinalIgnoreCase);
    private long _nextUiProbeDiagnosticsRefreshTick;

    public UiProbeDiagnosticsSnapshot GetUiProbeDiagnostics(int candidateLimit = 8)
    {
        _options.EnsureDirectories();
        long now = Environment.TickCount64;

        lock (_uiProbeDiagnosticsGate)
        {
            if (_nextUiProbeDiagnosticsRefreshTick <= now)
            {
                PruneUiProbeDiagnosticsDirectory();
                _uiProbeDiagnostics = BuildUiProbeDiagnosticsSnapshot();
                _nextUiProbeDiagnosticsRefreshTick = now + (long)UiProbeDiagnosticsSnapshotTtl.TotalMilliseconds;
            }

            return CloneUiProbeDiagnostics(_uiProbeDiagnostics, candidateLimit);
        }
    }

    private static UiProbeSnapshot BuildUiProbeSnapshot(UiProbeEventPayload payload, BridgeEventEnvelope envelope)
    {
        UiProbeWidgetEntry[] widgets = SanitizeUiProbeWidgetEntries(payload.Widgets, 12);

        return new UiProbeSnapshot
        {
            Reason = payload.Reason ?? string.Empty,
            Summary = payload.Summary ?? string.Empty,
            DumpPath = payload.DumpPath ?? string.Empty,
            ObservedWidgetCount = Math.Max(0, payload.ObservedWidgetCount),
            ActiveWidgetCount = Math.Max(0, payload.ActiveWidgetCount),
            Source = envelope.Source ?? string.Empty,
            CapturedAtUtc = envelope.TimestampUtc,
            Widgets = widgets,
        };
    }

    private void InvalidateUiProbeDiagnostics()
    {
        lock (_uiProbeDiagnosticsGate)
        {
            _nextUiProbeDiagnosticsRefreshTick = 0;
        }
    }

    private void PruneUiProbeDiagnosticsDirectory()
    {
        lock (_uiProbeDiagnosticsGate)
        {
            DirectoryRetention.Enforce(
                _options.BridgeDiagnosticsDir,
                _options.Bridge.DiagnosticsMaxFiles,
                _options.Bridge.DiagnosticsMaxAgeHours,
                "*.json");
        }
    }

    private UiProbeDiagnosticsSnapshot BuildUiProbeDiagnosticsSnapshot()
    {
        string[] files = GetSortedFiles(_options.BridgeDiagnosticsDir, "*.json");
        if (files.Length == 0)
        {
            _uiProbeDumpCache.Clear();
            return new UiProbeDiagnosticsSnapshot();
        }

        var candidates = new Dictionary<string, UiProbeCandidateAccumulator>(StringComparer.OrdinalIgnoreCase);
        var retainedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int dumpCount = 0;
        long cacheTick = Environment.TickCount64;
        DateTimeOffset? lastDumpAtUtc = null;
        string lastDumpPath = string.Empty;
        string lastReason = string.Empty;
        string lastSummary = string.Empty;

        foreach (string file in files)
        {
            retainedFiles.Add(file);
            UiProbeDumpDocument? dump = TryReadCachedUiProbeDump(file, _options.Http.LocalArtifactMaxBytes, cacheTick);
            if (dump is null)
            {
                continue;
            }

            dumpCount++;
            DateTimeOffset seenAtUtc = ResolveUiProbeDumpTimestamp(file, dump.GeneratedAtUtc);
            if (!lastDumpAtUtc.HasValue || seenAtUtc >= lastDumpAtUtc.Value)
            {
                lastDumpAtUtc = seenAtUtc;
                lastDumpPath = file;
                lastReason = dump.Reason ?? string.Empty;
                lastSummary = dump.Summary ?? string.Empty;
            }

            foreach (UiProbeWidgetEntry widget in SanitizeUiProbeWidgetEntries(dump.Widgets, 24))
            {
                string key = BuildUiProbeCandidateKey(widget);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!candidates.TryGetValue(key, out UiProbeCandidateAccumulator? accumulator))
                {
                    accumulator = new UiProbeCandidateAccumulator(key);
                    candidates[key] = accumulator;
                }

                accumulator.Observe(widget, seenAtUtc);
            }
        }

        PruneUiProbeDumpCache(retainedFiles);

        UiProbeCandidateSummary[] rankedCandidates = candidates.Values
            .Select(BuildUiProbeCandidateSummary)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.ActiveRatio)
            .ThenByDescending(candidate => candidate.DumpCount)
            .ThenByDescending(candidate => candidate.PeakSeenCount)
            .ThenByDescending(candidate => candidate.LastSeenAtUtc)
            .ThenBy(candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new UiProbeDiagnosticsSnapshot
        {
            DumpCount = dumpCount,
            CandidateCount = rankedCandidates.Length,
            LastDumpAtUtc = lastDumpAtUtc,
            LastDumpPath = lastDumpPath,
            LastReason = lastReason,
            LastSummary = lastSummary,
            Candidates = rankedCandidates,
        };
    }

    private UiProbeDumpDocument? TryReadCachedUiProbeDump(string file, int maxBytes, long cacheTick)
    {
        if (!TryGetUiProbeDumpMetadata(file, out UiProbeDumpMetadata metadata))
        {
            _uiProbeDumpCache.Remove(file);
            return null;
        }

        if (_uiProbeDumpCache.TryGetValue(file, out UiProbeDumpCacheEntry? cached)
            && cached.Metadata.Equals(metadata))
        {
            cached.LastUsedTick = cacheTick;
            return CloneUiProbeDumpDocument(cached.Dump);
        }

        UiProbeDumpDocument? dump = TryReadUiProbeDump(file, maxBytes);
        if (dump is null)
        {
            _uiProbeDumpCache.Remove(file);
            return null;
        }

        _uiProbeDumpCache[file] = new UiProbeDumpCacheEntry(
            metadata,
            CloneUiProbeDumpDocument(dump),
            cacheTick);
        return dump;
    }

    private void PruneUiProbeDumpCache(HashSet<string> retainedFiles)
    {
        foreach (string path in _uiProbeDumpCache.Keys.ToArray())
        {
            if (!retainedFiles.Contains(path))
            {
                _uiProbeDumpCache.Remove(path);
            }
        }

        int limit = Math.Min(
            UiProbeDumpCacheMaxEntries,
            Math.Max(0, _options.Bridge.DiagnosticsMaxFiles));
        if (limit <= 0)
        {
            _uiProbeDumpCache.Clear();
            return;
        }

        if (_uiProbeDumpCache.Count <= limit)
        {
            return;
        }

        foreach (string path in _uiProbeDumpCache
            .OrderBy(entry => entry.Value.LastUsedTick)
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Take(_uiProbeDumpCache.Count - limit)
            .Select(entry => entry.Key)
            .ToArray())
        {
            _uiProbeDumpCache.Remove(path);
        }
    }

    private static bool TryGetUiProbeDumpMetadata(string file, out UiProbeDumpMetadata metadata)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                metadata = default;
                return false;
            }

            metadata = new UiProbeDumpMetadata(info.Length, info.LastWriteTimeUtc.Ticks);
            return true;
        }
        catch (IOException)
        {
            metadata = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            metadata = default;
            return false;
        }
    }

    private static UiProbeDumpDocument? TryReadUiProbeDump(string file, int maxBytes)
    {
        BoundedJsonFileReader.JsonReadResult<UiProbeDumpDocument> readResult =
            BoundedJsonFileReader.TryRead(
                file,
                maxBytes,
                stream => JsonSerializer.Deserialize(stream, UiProbeDumpJsonContextInstance.UiProbeDumpDocument));
        if (!readResult.Succeeded || readResult.Value is null)
        {
            return null;
        }

        UiProbeDumpDocument dump = readResult.Value;
        return new UiProbeDumpDocument
        {
            GeneratedAtUtc = dump.GeneratedAtUtc,
            Reason = dump.Reason ?? string.Empty,
            Summary = dump.Summary ?? string.Empty,
            ObservedWidgetCount = Math.Max(0, dump.ObservedWidgetCount),
            ActiveWidgetCount = Math.Max(0, dump.ActiveWidgetCount),
            Widgets = SanitizeUiProbeWidgetEntries(dump.Widgets, 24),
        };
    }

    private static DateTimeOffset ResolveUiProbeDumpTimestamp(string file, DateTimeOffset? generatedAtUtc)
    {
        if (generatedAtUtc.HasValue && generatedAtUtc.Value != default)
        {
            return generatedAtUtc.Value;
        }

        try
        {
            return new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
        }
        catch (IOException)
        {
            return DateTimeOffset.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static UiProbeCandidateSummary BuildUiProbeCandidateSummary(UiProbeCandidateAccumulator candidate)
    {
        string searchText = BuildUiProbeSearchText(candidate.DisplayName, candidate.ClassName, candidate.FullName);
        string[] positiveHits = FindUiProbeKeywordHits(searchText, UiProbePositiveKeywords);
        string[] negativeHits = FindUiProbeKeywordHits(searchText, UiProbeNegativeKeywords);
        double activeRatio = candidate.DumpCount <= 0
            ? 0
            : Math.Round((double)candidate.ActiveObservationCount / candidate.DumpCount, 2, MidpointRounding.AwayFromZero);

        var rationale = new List<string>();
        int score = candidate.DumpCount * 8;
        score += candidate.ActiveObservationCount * 6;
        score += Math.Min(candidate.PeakSeenCount, 12) * 2;

        if (candidate.DumpCount >= 3)
        {
            score += 12;
            rationale.Add($"recurs across {candidate.DumpCount} dumps");
        }
        else if (candidate.DumpCount == 2)
        {
            score += 5;
            rationale.Add("recurs across multiple dumps");
        }

        if (candidate.ActiveObservationCount > 0)
        {
            rationale.Add($"active in {candidate.ActiveObservationCount}/{candidate.DumpCount} dumps");
            if (activeRatio >= 0.75d)
            {
                score += 10;
            }
            else if (activeRatio >= 0.5d)
            {
                score += 5;
            }
            else
            {
                score += 2;
            }
        }

        if (candidate.PeakSeenCount >= 4)
        {
            rationale.Add($"peaks at x{candidate.PeakSeenCount} lifecycle hits");
        }

        if (positiveHits.Length > 0)
        {
            score += positiveHits.Length * 6;
            rationale.Add($"name suggests HUD usage: {string.Join("/", positiveHits.Take(2))}");
        }

        if (negativeHits.Length > 0)
        {
            score -= negativeHits.Length * 7;
            rationale.Add($"penalized for menu-like naming: {string.Join("/", negativeHits.Take(2))}");
        }

        if (searchText.Contains("root", StringComparison.OrdinalIgnoreCase) && positiveHits.Length > 0)
        {
            score += 4;
            rationale.Add("looks like a root-level surface");
        }

        if (string.Equals(candidate.LastLifecycle, "construct", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }

        return new UiProbeCandidateSummary
        {
            DisplayName = candidate.DisplayName,
            FullName = candidate.FullName,
            ClassName = candidate.ClassName,
            DumpCount = candidate.DumpCount,
            ActiveObservationCount = candidate.ActiveObservationCount,
            PeakSeenCount = candidate.PeakSeenCount,
            ActiveRatio = activeRatio,
            Score = Math.Max(0, score),
            LastLifecycle = candidate.LastLifecycle,
            LastSeenAtUtc = candidate.LastSeenAtUtc,
            Rationale = rationale.ToArray(),
        };
    }

    private static string BuildUiProbeCandidateKey(UiProbeWidgetEntry entry) =>
        TakeFirstNonBlank(entry.FullName, entry.ClassName, entry.DisplayName);

    private static UiProbeWidgetEntry[] SanitizeUiProbeWidgetEntries(
        IEnumerable<UiProbeWidgetEntry>? entries,
        int limit)
    {
        return (entries ?? Array.Empty<UiProbeWidgetEntry>())
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.DisplayName)
                || !string.IsNullOrWhiteSpace(entry.FullName)
                || !string.IsNullOrWhiteSpace(entry.ClassName))
            .Take(Math.Max(0, limit))
            .Select(CloneUiProbeWidget)
            .ToArray();
    }

    private static string BuildUiProbeSearchText(params string?[] values) =>
        string.Join(" | ", values.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string[] FindUiProbeKeywordHits(string text, IEnumerable<string> keywords) =>
        keywords
            .Where(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static UiProbeSnapshot? CloneUiProbe(UiProbeSnapshot? probe)
    {
        if (probe is null)
        {
            return null;
        }

        UiProbeWidgetEntry[] widgets = (probe.Widgets ?? Array.Empty<UiProbeWidgetEntry>())
            .Select(CloneUiProbeWidget)
            .ToArray();

        return new UiProbeSnapshot
        {
            Reason = probe.Reason,
            Summary = probe.Summary,
            DumpPath = probe.DumpPath,
            ObservedWidgetCount = probe.ObservedWidgetCount,
            ActiveWidgetCount = probe.ActiveWidgetCount,
            Source = probe.Source,
            CapturedAtUtc = probe.CapturedAtUtc,
            Widgets = widgets,
        };
    }

    private static UiProbeDiagnosticsSnapshot CloneUiProbeDiagnostics(
        UiProbeDiagnosticsSnapshot? diagnostics,
        int candidateLimit = int.MaxValue)
    {
        if (diagnostics is null)
        {
            return new UiProbeDiagnosticsSnapshot();
        }

        UiProbeCandidateSummary[] candidates = (diagnostics.Candidates ?? Array.Empty<UiProbeCandidateSummary>())
            .Take(ClampPositiveBudget(candidateLimit))
            .Select(CloneUiProbeCandidate)
            .ToArray();

        return new UiProbeDiagnosticsSnapshot
        {
            DumpCount = diagnostics.DumpCount,
            CandidateCount = diagnostics.CandidateCount,
            LastDumpAtUtc = diagnostics.LastDumpAtUtc,
            LastDumpPath = diagnostics.LastDumpPath ?? string.Empty,
            LastReason = diagnostics.LastReason ?? string.Empty,
            LastSummary = diagnostics.LastSummary ?? string.Empty,
            Candidates = candidates,
        };
    }

    private static UiProbeCandidateSummary CloneUiProbeCandidate(UiProbeCandidateSummary candidate) =>
        new()
        {
            DisplayName = candidate.DisplayName ?? string.Empty,
            FullName = candidate.FullName ?? string.Empty,
            ClassName = candidate.ClassName ?? string.Empty,
            DumpCount = Math.Max(0, candidate.DumpCount),
            ActiveObservationCount = Math.Max(0, candidate.ActiveObservationCount),
            PeakSeenCount = Math.Max(0, candidate.PeakSeenCount),
            ActiveRatio = candidate.ActiveRatio < 0 ? 0 : candidate.ActiveRatio,
            Score = Math.Max(0, candidate.Score),
            LastLifecycle = candidate.LastLifecycle ?? string.Empty,
            LastSeenAtUtc = candidate.LastSeenAtUtc,
            Rationale = (candidate.Rationale ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray(),
        };

    private static UiProbeWidgetEntry CloneUiProbeWidget(UiProbeWidgetEntry entry) =>
        new()
        {
            DisplayName = entry.DisplayName ?? string.Empty,
            FullName = entry.FullName ?? string.Empty,
            ClassName = entry.ClassName ?? string.Empty,
            SeenCount = Math.Max(0, entry.SeenCount),
            IsActive = entry.IsActive,
            LastLifecycle = entry.LastLifecycle ?? string.Empty,
        };

    private static UiProbeDumpDocument CloneUiProbeDumpDocument(UiProbeDumpDocument dump) =>
        new()
        {
            GeneratedAtUtc = dump.GeneratedAtUtc,
            Reason = dump.Reason ?? string.Empty,
            Summary = dump.Summary ?? string.Empty,
            ObservedWidgetCount = Math.Max(0, dump.ObservedWidgetCount),
            ActiveWidgetCount = Math.Max(0, dump.ActiveWidgetCount),
            Widgets = SanitizeUiProbeWidgetEntries(dump.Widgets, 24),
        };

    private readonly record struct UiProbeDumpMetadata(long Length, long LastWriteUtcTicks);

    private sealed class UiProbeDumpCacheEntry
    {
        public UiProbeDumpCacheEntry(UiProbeDumpMetadata metadata, UiProbeDumpDocument dump, long lastUsedTick)
        {
            Metadata = metadata;
            Dump = dump;
            LastUsedTick = lastUsedTick;
        }

        public UiProbeDumpMetadata Metadata { get; }

        public UiProbeDumpDocument Dump { get; }

        public long LastUsedTick { get; set; }
    }

    private sealed class UiProbeDumpDocument
    {
        public DateTimeOffset? GeneratedAtUtc { get; init; }

        public string Reason { get; init; } = string.Empty;

        public string Summary { get; init; } = string.Empty;

        public int ObservedWidgetCount { get; init; }

        public int ActiveWidgetCount { get; init; }

        public IReadOnlyList<UiProbeWidgetEntry> Widgets { get; init; } =
            Array.Empty<UiProbeWidgetEntry>();
    }

    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNameCaseInsensitive = true)]
    [JsonSerializable(typeof(UiProbeDumpDocument))]
    private sealed partial class UiProbeDumpJsonContext : JsonSerializerContext;

    private sealed class UiProbeCandidateAccumulator
    {
        public UiProbeCandidateAccumulator(string key)
        {
            Key = key;
        }

        public string Key { get; }

        public string DisplayName { get; private set; } = string.Empty;

        public string FullName { get; private set; } = string.Empty;

        public string ClassName { get; private set; } = string.Empty;

        public int DumpCount { get; private set; }

        public int ActiveObservationCount { get; private set; }

        public int PeakSeenCount { get; private set; }

        public string LastLifecycle { get; private set; } = string.Empty;

        public DateTimeOffset? LastSeenAtUtc { get; private set; }

        public void Observe(UiProbeWidgetEntry widget, DateTimeOffset seenAtUtc)
        {
            DisplayName = TakeFirstNonBlank(widget.DisplayName, DisplayName);
            FullName = TakeFirstNonBlank(widget.FullName, FullName);
            ClassName = TakeFirstNonBlank(widget.ClassName, ClassName);
            DumpCount++;
            if (widget.IsActive)
            {
                ActiveObservationCount++;
            }

            PeakSeenCount = Math.Max(PeakSeenCount, Math.Max(0, widget.SeenCount));
            LastLifecycle = TakeFirstNonBlank(widget.LastLifecycle, LastLifecycle);
            if (!LastSeenAtUtc.HasValue || seenAtUtc >= LastSeenAtUtc.Value)
            {
                LastSeenAtUtc = seenAtUtc;
            }
        }
    }

    private sealed class DirectoryActivitySnapshot
    {
        public int OutboxPendingCount { get; init; }

        public int InboxPendingCount { get; init; }

        public int ScreenshotPendingCount { get; init; }

        public int ArchiveFileCount { get; init; }

        public int FailedFileCount { get; init; }
    }
}
