using System.Text.Json;
using System.Text.Json.Serialization;
using PalLLM.Domain.Configuration;
using PalLLM.Domain.Integration;
using PalLLM.Domain.Memory;
using PalLLM.Domain.Portable;

namespace PalLLM.Domain.Runtime;

/// <summary>
/// On-disk persistence for the two in-memory subsystems that benefit most from
/// surviving a restart: the conversation memory stream and the per-character
/// relationship tracker. One compact JSON file per runtime root. Save is
/// synchronous and explicit; the sidecar wraps it in an autosave worker.
/// </summary>
internal sealed partial class SessionPersistence
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly SessionPersistenceJsonContext JsonContext = new(JsonOptions);

    private readonly PalLlmOptions _options;
    private readonly ConversationMemoryStore _memory;
    private readonly RelationshipTracker _relationships;
    private readonly object _ioGate = new();
    private long _lastSavedMemoryVersion = -1;
    private long _lastSavedRelationshipVersion = -1;
    private DateTimeOffset? _lastSavedAtUtc;

    public SessionPersistence(
        PalLlmOptions options,
        ConversationMemoryStore memory,
        RelationshipTracker relationships)
    {
        _options = options;
        _memory = memory;
        _relationships = relationships;
    }

    public DateTimeOffset? LastSavedAtUtc => _lastSavedAtUtc;

    /// True when either subsystem has mutated since the last successful save.
    public bool IsDirty =>
        _memory.MutationVersion != Interlocked.Read(ref _lastSavedMemoryVersion)
        || _relationships.MutationVersion != Interlocked.Read(ref _lastSavedRelationshipVersion);

    /// Autosave-friendly save: skips the disk write when nothing has changed since
    /// the previous save. The autosave worker calls this every tick so the fast
    /// path is "no mutation → no I/O".
    public SessionPersistenceResult SaveIfDirty()
    {
        if (!IsDirty)
        {
            return new SessionPersistenceResult
            {
                Success = true,
                MemoryEntryCount = _memory.Count,
                RelationshipCount = _relationships.Count,
                SavedAtUtc = _lastSavedAtUtc,
                FilePath = _options.SessionFilePath,
                StatusMessage = "Session clean; skipped disk write.",
            };
        }

        return Save();
    }

    public SessionPersistenceResult Save()
    {
        string path = _options.SessionFilePath;
        // Capture versions BEFORE reading state so a concurrent mutation between
        // version read and Export doesn't make the save look clean on the next call.
        long memoryVersion = _memory.MutationVersion;
        long relationshipVersion = _relationships.MutationVersion;
        IReadOnlyList<ConversationMemoryEntry> entries = _memory.Export();
        IReadOnlyList<CharacterRelationship> relationships = _relationships.Snapshot();
        DateTimeOffset savedAt = DateTimeOffset.UtcNow;
        PersistedConversationMemoryEntry[] persistedEntries = entries
            .Select(PersistedConversationMemoryEntry.FromRuntimeEntry)
            .ToArray();

        var snapshot = new SessionFile
        {
            SavedAtUtc = savedAt,
            SchemaVersion = SessionFile.CurrentSchemaVersion,
            MemoryEntries = persistedEntries,
            Relationships = relationships.ToArray(),
        };

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Write to a temp file and move — avoids partial reads during autosave.
            // On replace, keep the previous file as `.bak` so a corrupted write
            // (e.g. disk full mid-flush) has a last-known-good to recover from.
            string tempPath = path + ".tmp";
            string backupPath = path + ".bak";
            lock (_ioGate)
            {
                using (FileStream stream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, snapshot, JsonContext.SessionFile);
                }

                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, backupPath);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }

            Interlocked.Exchange(ref _lastSavedMemoryVersion, memoryVersion);
            Interlocked.Exchange(ref _lastSavedRelationshipVersion, relationshipVersion);
            _lastSavedAtUtc = savedAt;

            return new SessionPersistenceResult
            {
                Success = true,
                MemoryEntryCount = persistedEntries.Length,
                RelationshipCount = relationships.Count,
                SavedAtUtc = savedAt,
                FilePath = path,
                StatusMessage = "Session saved.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return CreateFailureResult(DescribeSessionSaveFailure(ex));
        }
    }

    public SessionPersistenceResult Load()
    {
        string path = _options.SessionFilePath;
        string backupPath = path + ".bak";

        if (!File.Exists(path) && !File.Exists(backupPath))
        {
            return CreateFailureResult("No session file on disk.");
        }

        // Try primary then fall back to `.bak` so a corrupted write (disk full,
        // killed process mid-flush) doesn't lose the last-known-good state.
        SessionPersistenceResult primary = TryLoadFrom(path, isBackup: false);
        if (primary.Success)
        {
            return primary;
        }

        if (File.Exists(backupPath))
        {
            SessionPersistenceResult fromBackup = TryLoadFrom(backupPath, isBackup: true);
            if (fromBackup.Success)
            {
                return fromBackup;
            }
        }

        return primary;
    }

    private SessionPersistenceResult TryLoadFrom(string path, bool isBackup)
    {
        if (!File.Exists(path))
        {
            return CreateFailureResult("Session file missing.");
        }

        try
        {
            BoundedJsonFileReader.JsonReadResult<SessionFile> readResult;
            lock (_ioGate)
            {
                readResult = TryReadSessionFile(path, _options.Session.MaxPersistedBytes);
            }

            if (!readResult.Succeeded)
            {
                return CreateFailureResult(DescribeSessionLoadFailure(readResult.FailureCode));
            }

            SessionFile? loaded = readResult.Value;
            if (loaded is null)
            {
                return CreateFailureResult("Session file deserialised to null.");
            }

            // Reject files written by a future schema. Missing guard used to mean an
            // unknown-newer file would silently load with whichever fields happened
            // to match the current shape, quietly corrupting state. Fail fast so the
            // operator can downgrade or migrate explicitly.
            if (loaded.SchemaVersion > SessionFile.CurrentSchemaVersion)
            {
                return CreateFailureResult(
                    $"Session file schema version {loaded.SchemaVersion} is newer than supported ({SessionFile.CurrentSchemaVersion}); refusing to load to avoid silent state corruption.");
            }

            PersistedConversationMemoryEntry[] persistedEntries = (loaded.MemoryEntries ?? [])
                .Where(static entry => entry is not null)
                .ToArray();
            CharacterRelationship[] relationships = (loaded.Relationships ?? [])
                .Where(static relationship => relationship is not null)
                .ToArray();

            _memory.Import(persistedEntries.Select(entry => entry.ToRuntimeEntry()));
            _relationships.Import(relationships);

            // Imports bump the mutation versions; record them as the latest saved
            // versions so the first post-load autosave doesn't re-write the file
            // with state identical to what we just loaded.
            Interlocked.Exchange(ref _lastSavedMemoryVersion, _memory.MutationVersion);
            Interlocked.Exchange(ref _lastSavedRelationshipVersion, _relationships.MutationVersion);
            _lastSavedAtUtc = loaded.SavedAtUtc;

            return new SessionPersistenceResult
            {
                Success = true,
                MemoryEntryCount = persistedEntries.Length,
                RelationshipCount = relationships.Length,
                SavedAtUtc = loaded.SavedAtUtc,
                FilePath = path,
                StatusMessage = isBackup ? "Session loaded from .bak fallback." : "Session loaded.",
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return CreateFailureResult(DescribeSessionLoadFailure(ex));
        }
    }

    private static SessionPersistenceResult CreateFailureResult(string statusMessage) =>
        new()
        {
            Success = false,
            FilePath = string.Empty,
            StatusMessage = statusMessage,
        };

    private static string DescribeSessionSaveFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Session save failed: session file is not writable.",
        DirectoryNotFoundException => "Session save failed: session directory is unavailable.",
        JsonException => "Session save failed: session snapshot could not be serialized.",
        IOException => "Session save failed: session file could not be written.",
        _ => "Session save failed.",
    };

    private static string DescribeSessionLoadFailure(BoundedJsonFileReader.JsonReadFailureCode? failureCode) => failureCode switch
    {
        BoundedJsonFileReader.JsonReadFailureCode.Oversized => "Session file exceeds PalLLM:Session:MaxPersistedBytes.",
        BoundedJsonFileReader.JsonReadFailureCode.MalformedJson => "Session file is malformed JSON.",
        BoundedJsonFileReader.JsonReadFailureCode.Unreadable => "Session file is unreadable.",
        _ => "Session file could not be loaded.",
    };

    private static string DescribeSessionLoadFailure(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "Session load failed: session file is unreadable.",
        DirectoryNotFoundException => "Session load failed: session directory is unavailable.",
        JsonException => "Session load failed: session file could not be deserialized.",
        IOException => "Session load failed: session file could not be read.",
        _ => "Session load failed.",
    };

    private static BoundedJsonFileReader.JsonReadResult<SessionFile> TryReadSessionFile(string path, int maxBytes) =>
        BoundedJsonFileReader.TryRead(
            path,
            maxBytes,
            stream => JsonSerializer.Deserialize(stream, JsonContext.SessionFile));

    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(SessionFile))]
    private sealed partial class SessionPersistenceJsonContext : JsonSerializerContext;

    private sealed class SessionFile
    {
        public const int CurrentSchemaVersion = 2;

        public int SchemaVersion { get; init; } = CurrentSchemaVersion;

        public DateTimeOffset SavedAtUtc { get; init; }

        public PersistedConversationMemoryEntry[] MemoryEntries { get; init; } = [];

        public CharacterRelationship[] Relationships { get; init; } = [];
    }

    private sealed class PersistedConversationMemoryEntry
    {
        public Guid Id { get; init; }

        public int? CharacterId { get; init; }

        public string CharacterName { get; init; } = string.Empty;

        public string SpeakerRole { get; init; } = string.Empty;

        public string Content { get; init; } = string.Empty;

        public IReadOnlyList<string> Tags { get; init; } = [];

        public DateTimeOffset CreatedAtUtc { get; init; }

        public float? Importance { get; init; }

        public static PersistedConversationMemoryEntry FromRuntimeEntry(ConversationMemoryEntry entry) =>
            new()
            {
                Id = entry.Id,
                CharacterId = entry.CharacterId,
                CharacterName = entry.CharacterName,
                SpeakerRole = entry.SpeakerRole,
                Content = entry.Content,
                Tags = entry.Tags,
                CreatedAtUtc = entry.CreatedAtUtc,
                Importance = entry.Importance,
            };

        public ConversationMemoryEntry ToRuntimeEntry()
        {
            string normalizedContent = Content?.Trim() ?? string.Empty;
            string[] normalizedTags = Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new ConversationMemoryEntry
            {
                Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
                CharacterId = CharacterId,
                CharacterName = string.IsNullOrWhiteSpace(CharacterName) ? "Unknown" : CharacterName,
                SpeakerRole = string.IsNullOrWhiteSpace(SpeakerRole) ? "system" : SpeakerRole,
                Content = normalizedContent,
                Tags = normalizedTags,
                CreatedAtUtc = CreatedAtUtc == default ? DateTimeOffset.UtcNow : CreatedAtUtc,
                Embedding = SemanticEmbedder.FallbackEmbed(normalizedContent),
                Importance = Importance.HasValue
                    ? Math.Clamp(Importance.Value, 0f, 1f)
                    : MemoryImportance.Derive(normalizedContent, SpeakerRole, normalizedTags),
            };
        }
    }
}
