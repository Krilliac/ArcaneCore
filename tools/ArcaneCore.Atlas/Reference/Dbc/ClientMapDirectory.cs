using ArcaneCore.Atlas.Core;

namespace ArcaneCore.Atlas.Reference.Dbc;

/// <summary>
/// Map and area names read from the client's <c>Map.dbc</c> and <c>AreaTable.dbc</c>. Immutable once loaded and thread-safe.
/// </summary>
/// <remarks>
/// Column positions are the 1.12.1.5875 layouts of WoWDBDefs (WoWDBDefs definitions/Map.dbd:151-168,
/// AreaTable.dbd:99-112), confirmed by the field counts the loader insists on and by D:\WoW-RE-Research\formats\dbc.md:238-262.
/// A table with another field count is refused rather than read with guessed columns.
/// </remarks>
public sealed class ClientMapDirectory : IMapDirectory
{
    /// <summary>In-archive path of the map table.</summary>
    public const string MapTablePath = "DBFilesClient\\Map.dbc";

    /// <summary>In-archive path of the area table.</summary>
    public const string AreaTablePath = "DBFilesClient\\AreaTable.dbc";

    /// <summary>Cells per Map.dbc row in build 5875: ID, Directory, InstanceType, MapType, 9 name cells, ... Unk4.</summary>
    // ref: WoWDBDefs definitions/Map.dbd:151-168 ; client-dbc-5875 README.txt (Map.dbc 42 fields)
    public const int MapFieldCount = 42;

    /// <summary>Cells per AreaTable.dbc row in build 5875: ID ... AreaName (9 cells), FactionGroupMask, LiquidTypeID[4].</summary>
    // ref: WoWDBDefs definitions/AreaTable.dbd:99-112 ; client-dbc-5875 README.txt (AreaTable.dbc 25 fields)
    public const int AreaFieldCount = 25;

    // Map.dbc columns (ref: Map.dbd:151-157).
    private const int MapId = 0;
    private const int MapDirectoryColumn = 1;
    private const int MapInstanceType = 2;
    private const int MapNameFirstCell = 4;

    // AreaTable.dbc columns (ref: AreaTable.dbd:99-110).
    private const int AreaId = 0;
    private const int AreaNameFirstCell = 11;

    private readonly Dictionary<uint, ClientMap> _byId;
    private readonly Dictionary<uint, string> _areas;

    private ClientMapDirectory(IReadOnlyList<ClientMap> maps, Dictionary<uint, ClientMap> byId, Dictionary<uint, string> areas)
    {
        Maps = maps;
        _byId = byId;
        _areas = areas;
    }

    /// <inheritdoc />
    public IReadOnlyList<ClientMap> Maps { get; }

    /// <summary>Number of areas with a name.</summary>
    public int AreaCount => _areas.Count;

    /// <summary>Reads both tables from <paramref name="files"/>.</summary>
    /// <exception cref="FileNotFoundException">A table is not in the client files.</exception>
    /// <exception cref="InvalidDataException">A table is damaged or not the 1.12.1.5875 layout.</exception>
    public static ClientMapDirectory Load(IClientFileSource files)
    {
        ArgumentNullException.ThrowIfNull(files);
        WdbcFile maps = ReadTable(files, MapTablePath);
        WdbcFile areas = ReadTable(files, AreaTablePath);
        return FromTables(maps, areas);
    }

    /// <summary>Builds the directory from parsed tables.</summary>
    /// <exception cref="InvalidDataException">A table has another field count than build 5875, or repeats an id.</exception>
    public static ClientMapDirectory FromTables(WdbcFile maps, WdbcFile areas)
    {
        ArgumentNullException.ThrowIfNull(maps);
        ArgumentNullException.ThrowIfNull(areas);
        Require(maps, MapFieldCount, MapTablePath);
        Require(areas, AreaFieldCount, AreaTablePath);

        var list = new List<ClientMap>(maps.RecordCount);
        var byId = new Dictionary<uint, ClientMap>(maps.RecordCount);
        for (int r = 0; r < maps.RecordCount; r++)
        {
            uint id = maps.GetUInt32(r, MapId);
            var map = new ClientMap(
                id,
                maps.GetString(r, MapDirectoryColumn),
                maps.GetLocalizedString(r, MapNameFirstCell),
                maps.GetUInt32(r, MapInstanceType));
            if (!byId.TryAdd(id, map))
            {
                throw new InvalidDataException($"{MapTablePath} lists map id {id} twice.");
            }

            list.Add(map);
        }

        list.Sort((a, b) => a.Id.CompareTo(b.Id));

        var names = new Dictionary<uint, string>(areas.RecordCount);
        for (int r = 0; r < areas.RecordCount; r++)
        {
            uint id = areas.GetUInt32(r, AreaId);
            string name = areas.GetLocalizedString(r, AreaNameFirstCell);
            if (!names.TryAdd(id, name))
            {
                throw new InvalidDataException($"{AreaTablePath} lists area id {id} twice.");
            }
        }

        return new ClientMapDirectory(list, byId, names);
    }

    /// <inheritdoc />
    public bool TryGetMap(uint mapId, out ClientMap map) => _byId.TryGetValue(mapId, out map!);

    /// <inheritdoc />
    public string? AreaName(uint areaId) => _areas.TryGetValue(areaId, out string? name) && name.Length > 0 ? name : null;

    private static WdbcFile ReadTable(IClientFileSource files, string path)
    {
        byte[] bytes = files.Read(path) ?? throw new FileNotFoundException($"{path} is not in the client files ({files.Description}).");
        try
        {
            return WdbcFile.Parse(bytes);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{path}: {e.Message}", e);
        }
    }

    private static void Require(WdbcFile table, int fields, string path)
    {
        if (table.FieldCount != fields)
        {
            throw new InvalidDataException(
                $"{path} has {table.FieldCount} fields; the build 5875 (1.12.1) layout has {fields}. Other builds are not supported.");
        }
    }
}
