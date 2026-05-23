namespace PalLLM.Domain.Integration;

internal static class SnapshotCloneExtensions
{
    private const int RecentEventsCap = 12;

    public static GameWorldSnapshot CloneDeep(this GameWorldSnapshot snapshot) =>
        new()
        {
            Source = snapshot.Source ?? string.Empty,
            WorldName = snapshot.WorldName ?? string.Empty,
            IsWorldLoaded = snapshot.IsWorldLoaded,
            CurrentTick = snapshot.CurrentTick,
            TicksPerHour = snapshot.TicksPerHour,
            TicksPerDay = snapshot.TicksPerDay,
            CapturedAtUtc = snapshot.CapturedAtUtc,
            Biome = snapshot.Biome ?? string.Empty,
            Weather = snapshot.Weather ?? string.Empty,
            TimeOfDay = snapshot.TimeOfDay ?? string.Empty,
            ThreatLevel = snapshot.ThreatLevel,
            AlertLevel = snapshot.AlertLevel,
            PlayerHealthFraction = snapshot.PlayerHealthFraction,
            PlayerStaminaFraction = snapshot.PlayerStaminaFraction,
            PlayerHungerFraction = snapshot.PlayerHungerFraction,
            CurrentObjective = snapshot.CurrentObjective ?? string.Empty,
            LastTravel = CloneDeep(snapshot.LastTravel),
            LastProduction = CloneDeep(snapshot.LastProduction),
            IsInBase = snapshot.IsInBase,
            ActiveBaseIds = CloneStringList(snapshot.ActiveBaseIds),
            KnownBases = CloneList(snapshot.KnownBases, CloneDeep),
            NearbyHostiles = CloneStringList(snapshot.NearbyHostiles),
            NearbyFriendlies = CloneStringList(snapshot.NearbyFriendlies),
            NearbyResources = CloneStringList(snapshot.NearbyResources),
            RecentEvents = CloneStringList(snapshot.RecentEvents),
            Characters = CloneList(snapshot.Characters, CloneDeep),
        };

    /// Builds a new snapshot based on the current one, merging in a discovered base
    /// and appending the corresponding recent-event marker. Source, timestamp, and
    /// existing fields are preserved where appropriate.
    public static GameWorldSnapshot WithBaseDiscovery(
        this GameWorldSnapshot snapshot,
        string baseId,
        float? areaRange,
        DateTimeOffset discoveredAtUtc,
        string source)
    {
        List<string> activeBaseIds = (snapshot.ActiveBaseIds ?? [])
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!activeBaseIds.Contains(baseId, StringComparer.OrdinalIgnoreCase))
        {
            activeBaseIds.Add(baseId);
        }

        List<GameBaseSnapshot> knownBases = CloneList(snapshot.KnownBases, CloneDeep);
        int existingIndex = knownBases.FindIndex(info =>
            string.Equals(info.BaseId, baseId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            GameBaseSnapshot existing = knownBases[existingIndex];
            knownBases[existingIndex] = new GameBaseSnapshot
            {
                BaseId = existing.BaseId,
                AreaRange = areaRange ?? existing.AreaRange,
                FirstSeenUtc = existing.FirstSeenUtc,
                LastSeenUtc = discoveredAtUtc,
                Source = string.IsNullOrWhiteSpace(source) ? existing.Source : source,
            };
        }
        else
        {
            knownBases.Add(new GameBaseSnapshot
            {
                BaseId = baseId,
                AreaRange = areaRange,
                FirstSeenUtc = discoveredAtUtc,
                LastSeenUtc = discoveredAtUtc,
                Source = string.IsNullOrWhiteSpace(source) ? "bridge" : source,
            });
        }

        return new GameWorldSnapshot
        {
            Source = string.IsNullOrWhiteSpace(source) ? snapshot.Source ?? string.Empty : source,
            WorldName = snapshot.WorldName ?? string.Empty,
            IsWorldLoaded = snapshot.IsWorldLoaded,
            CurrentTick = snapshot.CurrentTick,
            TicksPerHour = snapshot.TicksPerHour,
            TicksPerDay = snapshot.TicksPerDay,
            CapturedAtUtc = discoveredAtUtc,
            Biome = snapshot.Biome ?? string.Empty,
            Weather = snapshot.Weather ?? string.Empty,
            TimeOfDay = snapshot.TimeOfDay ?? string.Empty,
            ThreatLevel = snapshot.ThreatLevel,
            AlertLevel = snapshot.AlertLevel,
            PlayerHealthFraction = snapshot.PlayerHealthFraction,
            PlayerStaminaFraction = snapshot.PlayerStaminaFraction,
            PlayerHungerFraction = snapshot.PlayerHungerFraction,
            CurrentObjective = snapshot.CurrentObjective ?? string.Empty,
            LastTravel = CloneDeep(snapshot.LastTravel),
            LastProduction = CloneDeep(snapshot.LastProduction),
            IsInBase = snapshot.IsInBase,
            ActiveBaseIds = activeBaseIds,
            KnownBases = knownBases,
            NearbyHostiles = CloneStringList(snapshot.NearbyHostiles),
            NearbyFriendlies = CloneStringList(snapshot.NearbyFriendlies),
            NearbyResources = CloneStringList(snapshot.NearbyResources),
            RecentEvents = MergeRecentEvents(snapshot.RecentEvents ?? [], $"base_discovered:{baseId}"),
            Characters = CloneList(snapshot.Characters, CloneDeep),
        };
    }

    private static List<string> MergeRecentEvents(IEnumerable<string> existing, string nextEvent)
    {
        List<string> events = existing
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(nextEvent))
        {
            events.RemoveAll(value => string.Equals(value, nextEvent, StringComparison.OrdinalIgnoreCase));
            events.Insert(0, nextEvent);
        }

        if (events.Count > RecentEventsCap)
        {
            events.RemoveRange(RecentEventsCap, events.Count - RecentEventsCap);
        }

        return events;
    }

    private static GameBaseSnapshot CloneDeep(GameBaseSnapshot snapshot) =>
        new()
        {
            BaseId = snapshot.BaseId ?? string.Empty,
            AreaRange = snapshot.AreaRange,
            FirstSeenUtc = snapshot.FirstSeenUtc,
            LastSeenUtc = snapshot.LastSeenUtc,
            Source = snapshot.Source ?? string.Empty,
        };

    private static ProductionStatusSnapshot? CloneDeep(ProductionStatusSnapshot? snapshot) =>
        snapshot is null
            ? null
            : new ProductionStatusSnapshot
            {
                BaseId = snapshot.BaseId ?? string.Empty,
                Station = snapshot.Station ?? string.Empty,
                Item = snapshot.Item ?? string.Empty,
                Quantity = snapshot.Quantity,
                Status = snapshot.Status ?? string.Empty,
                Note = snapshot.Note ?? string.Empty,
                RequestId = snapshot.RequestId ?? string.Empty,
                SourceStrategy = snapshot.SourceStrategy ?? string.Empty,
                Source = snapshot.Source ?? string.Empty,
                CapturedAtUtc = snapshot.CapturedAtUtc,
            };

    private static TravelStatusSnapshot? CloneDeep(TravelStatusSnapshot? snapshot) =>
        snapshot is null
            ? null
            : new TravelStatusSnapshot
            {
                Origin = snapshot.Origin ?? string.Empty,
                Destination = snapshot.Destination ?? string.Empty,
                Waypoint = snapshot.Waypoint ?? string.Empty,
                Mode = snapshot.Mode ?? string.Empty,
                Note = snapshot.Note ?? string.Empty,
                RequestId = snapshot.RequestId ?? string.Empty,
                SourceStrategy = snapshot.SourceStrategy ?? string.Empty,
                Source = snapshot.Source ?? string.Empty,
                CapturedAtUtc = snapshot.CapturedAtUtc,
            };

    private static GameCharacterSnapshot CloneDeep(GameCharacterSnapshot snapshot) =>
        new()
        {
            Id = snapshot.Id,
            DisplayName = snapshot.DisplayName ?? string.Empty,
            Species = snapshot.Species ?? string.Empty,
            IsAlive = snapshot.IsAlive,
            IsPlayerFaction = snapshot.IsPlayerFaction,
            IsIncapacitated = snapshot.IsIncapacitated,
            Age = snapshot.Age,
            Position = CloneDeep(snapshot.Position ?? new Vector3Snapshot()),
            Skills = CloneDictionary(snapshot.Skills),
            Needs = CloneDictionary(snapshot.Needs),
            Role = snapshot.Role ?? string.Empty,
            CurrentTask = snapshot.CurrentTask ?? string.Empty,
            HealthFraction = snapshot.HealthFraction,
            StaminaFraction = snapshot.StaminaFraction,
            HungerFraction = snapshot.HungerFraction,
            Morale = snapshot.Morale,
            Loyalty = snapshot.Loyalty,
            RecentDamageFraction = snapshot.RecentDamageFraction,
            NearbyEnemyCount = snapshot.NearbyEnemyCount,
            NearbyAllyCount = snapshot.NearbyAllyCount,
            Loadout = CloneStringList(snapshot.Loadout),
            RecentEvents = CloneStringList(snapshot.RecentEvents),
            Traits = CloneStringList(snapshot.Traits),
            Tags = CloneStringList(snapshot.Tags),
        };

    private static Vector3Snapshot CloneDeep(Vector3Snapshot snapshot) =>
        new()
        {
            X = snapshot.X,
            Y = snapshot.Y,
            Z = snapshot.Z,
        };

    private static List<TTarget> CloneList<TSource, TTarget>(
        IEnumerable<TSource>? source,
        Func<TSource, TTarget> clone)
        where TSource : class
    {
        if (source is null)
        {
            return [];
        }

        List<TTarget> items = [];
        foreach (TSource? item in source)
        {
            if (item is null)
            {
                continue;
            }

            items.Add(clone(item));
        }

        return items;
    }

    private static List<string> CloneStringList(IEnumerable<string>? source)
    {
        if (source is null)
        {
            return [];
        }

        List<string> items = [];
        foreach (string? item in source)
        {
            items.Add(item ?? string.Empty);
        }

        return items;
    }

    private static Dictionary<string, TValue> CloneDictionary<TValue>(IDictionary<string, TValue>? source)
    {
        if (source is null)
        {
            return new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, TValue> clone = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, TValue value) in source)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            clone[key] = value;
        }

        return clone;
    }
}
