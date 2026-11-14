using ArcaneCore.Atlas.Core;
using ArcaneCore.Atlas.Reference.Dbc;

namespace ArcaneCore.Atlas.Extract;

/// <summary>
/// AreaTable.dbc id to the explore flag a <c>.map</c> file stores per chunk, as the reference map extractor reads it.
/// Immutable once loaded and thread-safe.
/// </summary>
public sealed class AreaExploreFlags
{
    /// <summary>The value stored for a chunk whose area id is zero, past the table, or has no row.</summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:241 (memset 0xff) and :347 (area_flags[i][j] = 0xffff)
    public const ushort None = 0xFFFF;

    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:244 (areas[getUInt(0)] = getUInt(3): id in column 0, explore flag in column 3)
    // ref: src/ArcaneCore.Data/ClientData/ClientDbcLayouts.Dbd.g.cs (AreaTable "AreaBit" at byte 12 = column 3, build 5875)
    private const int IdColumn = 0;
    private const int ExploreFlagColumn = 3;

    private readonly ushort[] _flags;

    private AreaExploreFlags(ushort[] flags)
    {
        _flags = flags;
    }

    /// <summary>The largest area id in the table (ids above it have no flag).</summary>
    public uint MaxId => (uint)(_flags.Length - 1);

    /// <summary>Reads <c>DBFilesClient\AreaTable.dbc</c> from <paramref name="files"/>.</summary>
    /// <exception cref="FileNotFoundException">The table is not in the client files.</exception>
    /// <exception cref="InvalidDataException">The table is damaged or not the build 5875 layout.</exception>
    public static AreaExploreFlags Load(IClientFileSource files)
    {
        ArgumentNullException.ThrowIfNull(files);
        byte[] bytes = files.Read(ClientMapDirectory.AreaTablePath)
            ?? throw new FileNotFoundException($"{ClientMapDirectory.AreaTablePath} is not in the client files ({files.Description}).");
        WdbcFile table;
        try
        {
            table = WdbcFile.Parse(bytes);
        }
        catch (InvalidDataException e)
        {
            throw new InvalidDataException($"{ClientMapDirectory.AreaTablePath}: {e.Message}", e);
        }

        return FromTable(table);
    }

    /// <summary>Builds the lookup from a parsed AreaTable.dbc.</summary>
    /// <exception cref="InvalidDataException">The table has another field count than build 5875.</exception>
    public static AreaExploreFlags FromTable(WdbcFile table)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (table.FieldCount != ClientMapDirectory.AreaFieldCount)
        {
            throw new InvalidDataException(
                $"{ClientMapDirectory.AreaTablePath} has {table.FieldCount} fields; the build 5875 (1.12.1) layout has {ClientMapDirectory.AreaFieldCount}. Other builds are not supported.");
        }

        uint maxId = 0;
        for (int r = 0; r < table.RecordCount; r++)
        {
            maxId = Math.Max(maxId, table.GetUInt32(r, IdColumn));
        }

        // The reference allocates maxId + 1 entries; an id that large would be a damaged table, not a 1.12.1 one.
        if (maxId > ushort.MaxValue)
        {
            throw new InvalidDataException($"{ClientMapDirectory.AreaTablePath} holds area id {maxId}; build 5875 ids fit in 16 bits.");
        }

        // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:239-244 (maxid + 1 entries of 0xffff; rows in file order, the value cut to 16 bits)
        var flags = new ushort[maxId + 1];
        Array.Fill(flags, None);
        for (int r = 0; r < table.RecordCount; r++)
        {
            flags[table.GetUInt32(r, IdColumn)] = unchecked((ushort)table.GetUInt32(r, ExploreFlagColumn));
        }

        return new AreaExploreFlags(flags);
    }

    /// <summary>
    /// The explore flag stored for a chunk with <paramref name="areaId"/>: <see cref="None"/> for id 0, an id past the
    /// table, or an id without a row.
    /// </summary>
    // ref: vmangos@0e3ff01 contrib/extractor/System.cpp:337-347
    public ushort FlagOf(uint areaId) => areaId != 0 && areaId <= MaxId ? _flags[areaId] : None;
}
