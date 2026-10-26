using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// Area and zone lookup from the area flags terrain files store (vmangos <c>AreaEntry</c>
/// statics over <c>sAreaStorage</c>, Map.h, and <c>TerrainManager::GetZoneAndAreaIdByAreaFlag</c>).
/// <para>Immutable; safe to read from any thread.</para>
/// </summary>
public sealed class AreaTable
{
    private readonly AreaTemplate[] _byEntry;
    private readonly Dictionary<uint, AreaTemplate> _byId;

    // GetByAreaFlagAndMap's answers, built once in entry order: the first entry of each (flag, map) and the last entry of each flag
    // (the zone updater and the bot probes ask per player per second; a scan of every area each time was the cost).
    private readonly Dictionary<(uint Flag, uint Map), AreaTemplate> _firstByFlagAndMap = [];
    private readonly Dictionary<uint, AreaTemplate> _lastByFlag = [];
    private readonly MapRegistry _maps;

    public AreaTable(IEnumerable<AreaTemplate> areas, MapRegistry maps)
    {
        // sAreaStorage iterates in entry order.
        _byEntry = areas.OrderBy(a => a.Entry).ToArray();
        _byId = _byEntry.ToDictionary(a => a.Entry);
        _maps = maps;
        foreach (AreaTemplate area in _byEntry)
        {
            if (area.ExploreFlag == 0)
            {
                continue; // a flag of 0 matches nothing (GetByAreaFlagAndMap)
            }

            _firstByFlagAndMap.TryAdd((area.ExploreFlag, area.MapId), area);
            _lastByFlag[area.ExploreFlag] = area;
        }
    }

    public static AreaTable Empty { get; } = new([], MapRegistry.Default);

    public int Count => _byEntry.Length;

    /// <summary>Every area in entry order (GM lookups).</summary>
    public IReadOnlyList<AreaTemplate> All => _byEntry;

    public AreaTemplate? GetById(uint id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// vmangos <c>AreaEntry::GetByAreaFlagAndMap</c>: "1.12.1 areatable have duplicates for
    /// areaflag", so the first entry with the flag on this map wins, else the last entry with
    /// the flag on any map, else the map's <c>linked_zone</c> area. A flag of 0 matches nothing
    /// and goes straight to the linked zone (instances without terrain files).
    /// </summary>
    /// <remarks>
    /// Two lookups built at load time stand for vmangos' scan in entry order: the first entry of the flag on the map, else the last entry
    /// of the flag anywhere (when no entry of the flag is on the map, the scan's last match is the last entry of the flag). No allocation.
    /// </remarks>
    public AreaTemplate? GetByAreaFlagAndMap(uint areaFlag, uint mapId)
    {
        if (areaFlag != 0)
        {
            if (_firstByFlagAndMap.TryGetValue((areaFlag, mapId), out AreaTemplate? onMap))
            {
                return onMap;
            }

            if (_lastByFlag.TryGetValue(areaFlag, out AreaTemplate? anyMap))
            {
                return anyMap;
            }
        }

        return _maps.Find(mapId) is { } map ? GetById(map.LinkedZone) : null;
    }

    /// <summary>
    /// vmangos <c>TerrainManager::GetZoneAndAreaIdByAreaFlag</c>: area = the entry's id, zone =
    /// the entry itself when it is a zone, else its parent zone; (0, 0) when nothing matches.
    /// </summary>
    public (uint ZoneId, uint AreaId) GetZoneAndAreaId(uint areaFlag, uint mapId)
    {
        AreaTemplate? entry = GetByAreaFlagAndMap(areaFlag, mapId);
        return entry is null ? (0, 0) : (entry.IsZone ? entry.Entry : entry.ZoneId, entry.Entry);
    }
}
