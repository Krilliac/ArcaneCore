using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Text;
using ArcaneCore.Atlas.Core;
using ArcaneCore.Game.Maps.Terrain;
using Xunit;

namespace ArcaneCore.Atlas.Tests.Reference.Client.Terrain;

/// <summary>
/// A fact that needs the server-extracted terrain (<c>ARCANECORE_TEST_TERRAIN_DIR</c>) and the extracted 1.12.1 client
/// files (<c>ARCANECORE_TEST_CLIENT_FILES_DIR</c>); it is reported as skipped, with the reason, when either is missing.
/// </summary>
public sealed class ClientTerrainRealFactAttribute : FactAttribute
{
    /// <summary>Skips the fact unless both data roots are present.</summary>
    public ClientTerrainRealFactAttribute()
    {
        if (ClientTerrainRealData.SkipReason() is { } reason)
        {
            Skip = reason;
        }
    }
}

/// <summary>The real-data roots of the client terrain tests, opened once per test run.</summary>
internal static class ClientTerrainRealData
{
    /// <summary>The server-extracted terrain root (maps/, vmaps/, mmaps/).</summary>
    public const string TerrainVariable = "ARCANECORE_TEST_TERRAIN_DIR";

    private static readonly Lazy<DirectoryClientFiles> LazyFiles = new(() => new DirectoryClientFiles(ClientRoot()!), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<DbcMapDirectory> LazyMaps = new(() => DbcMapDirectory.Read(LazyFiles.Value), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The extracted client files.</summary>
    public static DirectoryClientFiles Files => LazyFiles.Value;

    /// <summary>The maps of the extracted client's Map.dbc.</summary>
    public static DbcMapDirectory Maps => LazyMaps.Value;

    /// <summary>The server's <c>maps</c> directory.</summary>
    public static string ServerMapsDirectory => Path.Combine(Environment.GetEnvironmentVariable(TerrainVariable)!, "maps");

    /// <summary>Why the real-data tests cannot run here, or null when they can.</summary>
    public static string? SkipReason()
    {
        string? terrain = Environment.GetEnvironmentVariable(TerrainVariable);
        if (string.IsNullOrWhiteSpace(terrain) || !Directory.Exists(Path.Combine(terrain, "maps")))
        {
            return $"{TerrainVariable} is not set to a server terrain root holding maps/ (vmangos extractor output for 1.12.1).";
        }

        string? client = ClientRoot();
        if (client is null || !File.Exists(Path.Combine(client, "DBFilesClient", "Map.dbc")) || !Directory.Exists(Path.Combine(client, "World", "Maps")))
        {
            return $"{DirectoryClientFiles.TestEnvironmentVariable} is not set to an extracted 1.12.1 client holding DBFilesClient\\Map.dbc and World\\Maps.";
        }

        return null;
    }

    /// <summary>The server's parsed <c>.map</c> for <paramref name="tile"/>, or null when the server has no file for it.</summary>
    public static ServerMapTile? ServerTile(TileKey tile)
    {
        string path = Path.Combine(ServerMapsDirectory, TerrainTile.FileName(tile.MapId, tile.X, tile.Y));
        return File.Exists(path) ? new ServerMapTile(TerrainTile.Parse(File.ReadAllBytes(path))) : null;
    }

    /// <summary>Every server tile of <paramref name="mapId"/> that has a <c>.map</c> file.</summary>
    public static IReadOnlyList<TileKey> ServerTiles(uint mapId)
    {
        var tiles = new List<TileKey>();
        foreach (string file in Directory.EnumerateFiles(ServerMapsDirectory, $"{mapId:D3}????.map"))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            tiles.Add(new TileKey(mapId, int.Parse(name.AsSpan(3, 2), CultureInfo.InvariantCulture), int.Parse(name.AsSpan(5, 2), CultureInfo.InvariantCulture)));
        }

        tiles.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        return tiles;
    }

    /// <summary>Map ids the server has at least one <c>.map</c> file for.</summary>
    public static IReadOnlyList<uint> ServerMapIds()
        => [.. Directory.EnumerateFiles(ServerMapsDirectory, "*.map")
            .Select(f => uint.Parse(Path.GetFileName(f).AsSpan(0, 3), CultureInfo.InvariantCulture))
            .Distinct()
            .Order()];

    private static string? ClientRoot()
    {
        string? root = Environment.GetEnvironmentVariable(DirectoryClientFiles.TestEnvironmentVariable);
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
    }
}

/// <summary>
/// A test-only map directory read straight from the client's <c>DBFilesClient\Map.dbc</c>: id, directory, name and
/// instance type, nothing else. The client terrain module itself gets its directory from the foundation lane.
/// </summary>
internal sealed class DbcMapDirectory : IMapDirectory
{
    // ref: src/ArcaneCore.Data/ClientData/DbcFile.cs:40-64 (WDBC header: magic, records, fields, record size, string block size; 20 bytes)
    // ref: vmangos@0e3ff01 contrib/extractor/dbcfile.cpp:33-59 (the same five header words; records follow, then the string block)
    private const uint WdbcMagic = 0x43424457;
    private const int HeaderBytes = 20;

    // ref: WoWDBDefs definitions/Map.dbd:151-158 (1.12.1.5875: ID, Directory, InstanceType, MapType, MapName_lang)
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:220-221 (the extractor reads the id from field 0 and the folder name from field 1)
    // ref: vmangos@0e3ff01 src/game/Database/DBCfmt.h:63 (MapEntryfmt "nxix" then the name strings: id, -, instance type, -, name)
    private const int IdField = 0;
    private const int DirectoryField = 1;
    private const int InstanceTypeField = 2;
    private const int NameField = 4;

    private readonly Dictionary<uint, ClientMap> _byId;

    private DbcMapDirectory(List<ClientMap> maps)
    {
        Maps = maps;
        _byId = maps.ToDictionary(m => m.Id);
    }

    /// <inheritdoc />
    public IReadOnlyList<ClientMap> Maps { get; }

    /// <summary>Reads <c>DBFilesClient\Map.dbc</c> from <paramref name="files"/>.</summary>
    public static DbcMapDirectory Read(IClientFileSource files)
    {
        byte[] data = files.Read("DBFilesClient\\Map.dbc") ?? throw new FileNotFoundException("DBFilesClient\\Map.dbc is not in the client files.");
        Assert.True(data.Length >= HeaderBytes && BinaryPrimitives.ReadUInt32LittleEndian(data) == WdbcMagic, "Map.dbc is not a WDBC file.");
        int records = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4)));
        int fields = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)));
        int recordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12)));
        int stringBytes = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16)));
        Assert.Equal(fields * 4, recordSize);
        Assert.Equal(HeaderBytes + (records * recordSize) + stringBytes, data.Length);
        Assert.True(fields > NameField, $"Map.dbc has {fields} fields.");
        int strings = HeaderBytes + (records * recordSize);

        uint Field(int record, int field) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(HeaderBytes + (record * recordSize) + (field * 4)));

        string Text(int record, int field)
        {
            int start = checked(strings + (int)Field(record, field));
            int end = Array.IndexOf(data, (byte)0, start);
            return Encoding.UTF8.GetString(data, start, end - start);
        }

        var maps = new List<ClientMap>(records);
        for (int r = 0; r < records; r++)
        {
            maps.Add(new ClientMap(Field(r, IdField), Text(r, DirectoryField), Text(r, NameField), Field(r, InstanceTypeField)));
        }

        return new DbcMapDirectory(maps);
    }

    /// <inheritdoc />
    public bool TryGetMap(uint mapId, out ClientMap map) => _byId.TryGetValue(mapId, out map!);

    /// <inheritdoc />
    public string? AreaName(uint areaId) => null;
}

/// <summary>
/// The server's own parse of a <c>.map</c> file (<see cref="TerrainTile.Parse"/>), with its height grids and hole
/// words read out of the parsed object. TerrainTile keeps them private, so this reads its fields by name and fails
/// loudly if they are renamed.
/// </summary>
internal sealed class ServerMapTile
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly float[]? _v9;
    private readonly float[]? _v8;
    private readonly ushort[] _holes;

    /// <summary>Wraps a parsed tile.</summary>
    public ServerMapTile(TerrainTile tile)
    {
        Tile = tile;
        Storage = Field<object>("_heightStorage").ToString()!;
        GridHeight = Field<float>("_gridHeight");
        _v9 = FieldOrNull<float[]>("_v9");
        _v8 = FieldOrNull<float[]>("_v8");
        _holes = Field<ushort[]>("_holes");
    }

    /// <summary>The parsed tile, for its public queries.</summary>
    public TerrainTile Tile { get; }

    /// <summary>How the heights are stored: <c>Flat</c>, <c>Float</c> or <c>Packed</c>.</summary>
    public string Storage { get; }

    /// <summary>The flat height (or the packed base) from the height header.</summary>
    public float GridHeight { get; }

    /// <summary>Corner sample (<paramref name="row"/>, <paramref name="column"/>) of the 129 x 129 grid.</summary>
    public float V9(int row, int column) => Storage == "Flat" ? GridHeight : _v9![(row * 129) + column];

    /// <summary>Centre sample (<paramref name="row"/>, <paramref name="column"/>) of the 128 x 128 grid.</summary>
    public float V8(int row, int column) => Storage == "Flat" ? GridHeight : _v8![(row * 128) + column];

    /// <summary>The hole word of chunk (<paramref name="row"/>, <paramref name="column"/>).</summary>
    public ushort Holes(int row, int column) => _holes[(row * 16) + column];

    private T Field<T>(string name)
        => FieldOrNull<T>(name) ?? throw new InvalidOperationException($"TerrainTile.{name} is null.");

    private T? FieldOrNull<T>(string name)
    {
        FieldInfo field = typeof(TerrainTile).GetField(name, Private)
            ?? throw new MissingFieldException(nameof(TerrainTile), name);
        return (T?)field.GetValue(Tile);
    }
}
