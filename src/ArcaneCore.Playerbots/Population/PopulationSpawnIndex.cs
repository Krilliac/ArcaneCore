using ArcaneCore.Kernel.WorldData.Creatures;

namespace ArcaneCore.World.Playerbots.Population;

/// <summary>
/// A fixed-spawn grid for population placement checks. Constructed once per creature content object on a background task (<see cref="PopulationPlacementFacts.Build"/>), then only read;
/// template definitions may reload on that same object, so callers resolve entries through <see cref="Content"/> at query time.
/// </summary>
internal sealed class PopulationSpawnIndex
{
    internal const float CellYards = 128f;
    private readonly Dictionary<(uint Map, int X, int Y), CreatureSpawn[]> _cells;
    private readonly Dictionary<uint, float> _maxWanderByMap;

    internal PopulationSpawnIndex(CreatureContent content)
    {
        Content = content ?? throw new ArgumentNullException(nameof(content));
        var buckets = new Dictionary<(uint Map, int X, int Y), List<CreatureSpawn>>();
        _maxWanderByMap = [];
        foreach (uint map in content.MapsWithSpawns)
            foreach (CreatureSpawn spawn in content.GetSpawns(map))
            {
                if (!float.IsFinite(spawn.X) || !float.IsFinite(spawn.Y)) continue;
                var cell = (map, Cell(spawn.X), Cell(spawn.Y));
                if (!buckets.TryGetValue(cell, out List<CreatureSpawn>? list)) buckets[cell] = list = [];
                list.Add(spawn);
                if (float.IsFinite(spawn.WanderDistance))
                    _maxWanderByMap[map] = MathF.Max(_maxWanderByMap.GetValueOrDefault(map), spawn.WanderDistance);
            }

        _cells = buckets.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    internal CreatureContent Content { get; }

    internal float MaxWander(uint map) => _maxWanderByMap.GetValueOrDefault(map);

    internal IReadOnlyList<CreatureSpawn> InCell(uint map, int x, int y)
        => _cells.GetValueOrDefault((map, x, y)) ?? [];

    internal static int Cell(float coordinate) => (int)MathF.Floor(coordinate / CellYards);
}
