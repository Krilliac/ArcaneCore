using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Game.Maps.Terrain;

/// <summary>
/// The area of a WMO group, from the client's WMOAreaTable.dbc (world DB <c>wmo_area_template</c>): vmangos
/// <c>GetWMOAreaTableEntryByTripple</c> over <c>sWMOAreaInfoByTripple</c> (DBCStores.cpp:479-486; the table is filled with `insert` at :415), keyed by the (root id, ADT name-set id,
/// group id) a model query returns (<see cref="Collision.ModelAreaInfo"/>). The key is exact: vmangos has no fallback to a
/// (root, name set, -1) row, and neither has mangos-zero (a 1.12 core: GridMap.cpp:273) or AzerothCore (a 3.3.5 core, read only as a cross-check: Map.cpp:1303).
/// <para>
/// The DBC holds ten triples twice; vmangos inserts into a <c>std::map</c> in row order, so the first (lowest id) row of a triple wins,
/// including when its area is 0 (a row with no area still shadows the second).
/// </para>
/// <para>Immutable; safe to read from any thread.</para>
/// </summary>
public sealed class WmoAreaTable
{
    private readonly Dictionary<Key, uint> _areaByGroup;

    public WmoAreaTable(IEnumerable<WmoAreaTemplate> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        _areaByGroup = [];
        foreach (WmoAreaTemplate row in rows.OrderBy(r => r.Id))
        {
            _areaByGroup.TryAdd(new Key(row.RootId, row.AdtId, row.GroupId), row.AreaId);
        }
    }

    public static WmoAreaTable Empty { get; } = new([]);

    /// <summary>Distinct (root, name set, group) keys held.</summary>
    public int Count => _areaByGroup.Count;

    /// <summary>
    /// The <c>AreaTable</c> id of a group; false when no row names the group, or its row has no area (0), so that the caller reads the
    /// terrain's area instead (vmangos <c>TerrainInfo::GetAreaFlag</c>, GridMap.cpp:922-928).
    /// </summary>
    public bool TryGetAreaId(int rootId, int adtId, int groupId, out uint areaId)
        => _areaByGroup.TryGetValue(new Key(rootId, adtId, groupId), out areaId) && areaId != 0;

    private readonly record struct Key(int RootId, int AdtId, int GroupId);
}
