using PalLLM.Domain.Configuration;
using PalLLM.Domain.Portable;

namespace PalLLM.Domain.Integration;

public sealed class BridgeGameAdapter : IGameAdapter
{
    private readonly object _gate = new();
    private readonly SnapshotWorldClock _clock;
    private readonly RuntimePathProvider _paths;
    private readonly AdapterLogger _logger;
    private GameWorldSnapshot _snapshot = new();

    public BridgeGameAdapter(PalLlmOptions options)
    {
        options.EnsureDirectories();
        _paths = new RuntimePathProvider(options);
        _clock = new SnapshotWorldClock(() => Snapshot);
        _logger = new AdapterLogger();
    }

    public string AdapterName => "Palworld (UE4SS bridge)";

    public PalLLM.Domain.Portable.ILogger Logger => _logger;

    public IWorldClock Clock => _clock;

    public IPathProvider Paths => _paths;

    public IEnumerable<ICharacter> Characters
    {
        get
        {
            GameCharacterSnapshot[] characters = Snapshot.Characters.ToArray();
            var wrapped = new ICharacter[characters.Length];
            for (int i = 0; i < characters.Length; i++)
            {
                wrapped[i] = new BridgeGameCharacter(characters[i]);
            }

            return wrapped;
        }
    }

    public bool IsReadyForInference => Snapshot.IsWorldLoaded;

    public GameWorldSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot.CloneDeep();
            }
        }
    }

    public IReadOnlyList<AdapterLogEntry> RecentLogs => _logger.Snapshot();

    public void UpdateSnapshot(GameWorldSnapshot snapshot)
    {
        if (snapshot is null)
        {
            throw new ArgumentNullException(nameof(snapshot));
        }

        lock (_gate)
        {
            _snapshot = snapshot.CloneDeep();
        }
    }
}

public sealed class BridgeGameCharacter : ICharacter
{
    private readonly GameCharacterSnapshot _snapshot;

    public BridgeGameCharacter(GameCharacterSnapshot snapshot)
    {
        _snapshot = snapshot;
    }

    public int Id => _snapshot.Id;

    public string DisplayName => _snapshot.DisplayName;

    public bool IsAlive => _snapshot.IsAlive;

    public bool IsPlayerFaction => _snapshot.IsPlayerFaction;

    public bool IsIncapacitated => _snapshot.IsIncapacitated;

    public Vec3 Position => new(_snapshot.Position.X, _snapshot.Position.Y, _snapshot.Position.Z);

    public int Age => _snapshot.Age;

    public IReadOnlyDictionary<string, int> Skills => _snapshot.Skills;

    public IReadOnlyDictionary<string, float> Needs => _snapshot.Needs;

    public IReadOnlyList<string> Traits => _snapshot.Traits;
}

public sealed class SnapshotWorldClock : IWorldClock
{
    private readonly Func<GameWorldSnapshot> _snapshotAccessor;

    public SnapshotWorldClock(Func<GameWorldSnapshot> snapshotAccessor)
    {
        _snapshotAccessor = snapshotAccessor;
    }

    public long CurrentTick => _snapshotAccessor().CurrentTick;

    public long TicksPerHour => _snapshotAccessor().TicksPerHour;

    public long TicksPerDay => _snapshotAccessor().TicksPerDay;
}

public sealed class RuntimePathProvider : IPathProvider
{
    private readonly PalLlmOptions _options;

    public RuntimePathProvider(PalLlmOptions options)
    {
        _options = options;
    }

    public string ModelsDir => _options.ModelsDir;

    public string DiffusionModelsDir => _options.DiffusionModelsDir;

    public string RuntimeRoot => _options.RuntimeRoot;

    public string TtsDir => _options.TtsDir;

    public string PackDir => _options.PackDir;
}

public sealed class AdapterLogger : PalLLM.Domain.Portable.ILogger
{
    private readonly object _gate = new();
    private readonly Queue<AdapterLogEntry> _entries = new();

    public void Info(string message) => Add("info", message);

    public void Warning(string message) => Add("warning", message);

    public void Error(string message) => Add("error", message);

    public IReadOnlyList<AdapterLogEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    private void Add(string level, string message)
    {
        lock (_gate)
        {
            _entries.Enqueue(new AdapterLogEntry(DateTimeOffset.UtcNow, level, message ?? string.Empty));
            while (_entries.Count > 100)
            {
                _entries.Dequeue();
            }
        }
    }
}

public sealed record AdapterLogEntry(DateTimeOffset TimestampUtc, string Level, string Message);
