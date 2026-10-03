using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Maps.Templates;

/// <summary>
/// The known maps (vmangos <c>sMapStorage</c>, loaded from <c>map_template</c>). Teleports and
/// area triggers only go to maps listed here (vmangos <c>MapManager::IsValidMapCoord</c> /
/// <c>sMapStorage.LookupEntry</c>). With an empty table the two continents are known, so a
/// fresh database still has a working world.
/// <para>Immutable; safe to read from any thread.</para>
/// </summary>
public sealed class MapRegistry
{
    private readonly Dictionary<uint, MapTemplate> _maps;

    public MapRegistry(IEnumerable<MapTemplate> templates)
    {
        _maps = templates.ToDictionary(t => t.Entry);
        if (_maps.Count == 0)
        {
            foreach (MapTemplate continent in DefaultContinents)
            {
                _maps[continent.Entry] = continent;
            }
        }
    }

    /// <summary>
    /// The continents (vmangos <c>map_template</c> rows 0 "Eastern Kingdoms" and 1 "Kalimdor":
    /// MAP_COMMON, no linked zone, no player limit or reset, ghost entrance −1).
    /// </summary>
    public static IReadOnlyList<MapTemplate> DefaultContinents { get; } =
    [
        new(0, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Eastern Kingdoms", string.Empty),
        new(1, 0, MapType.Common, 0, 0, 0, -1, 0, 0, "Kalimdor", string.Empty),
    ];

    /// <summary>A registry holding only <see cref="DefaultContinents"/>.</summary>
    public static MapRegistry Default { get; } = new([]);

    public int Count => _maps.Count;

    public IEnumerable<MapTemplate> All => _maps.Values.OrderBy(m => m.Entry);

    public MapTemplate? Find(uint mapId) => _maps.GetValueOrDefault(mapId);

    public bool Contains(uint mapId) => _maps.ContainsKey(mapId);
}
