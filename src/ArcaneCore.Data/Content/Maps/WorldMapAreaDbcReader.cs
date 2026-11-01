using ArcaneCore.Data.ClientData;

namespace ArcaneCore.Data.Content.Maps;

/// <summary>
/// The world-map rectangle of one WorldMapArea.dbc row: the world coordinates (server axes: X north, Y west) the client maps onto its 0..100 percent
/// frame. <see cref="Left"/> and <see cref="Right"/> bound the Y axis, <see cref="Top"/> and <see cref="Bottom"/> the X axis.
/// </summary>
public readonly record struct WorldMapAreaBounds(float Left, float Right, float Top, float Bottom);

/// <summary>
/// Reads build-5875 WorldMapArea.dbc (8 four-byte fields; vmangos <c>WorldMapAreaEntryfmt</c> "xinxffff", Database/DBCfmt.h:87; struct in DBCStructure.h:737): id, MapID (field 1), AreaID
/// (field 2), the area name (not kept) and LocLeft, LocRight, LocTop, LocBottom (fields 4 to 7). It keeps the rows of the two continents (maps 0 and 1)
/// that name an area (AreaID not 0), keyed by that area id, so a zone id gives its map rectangle for a "x, y" place line. Rows with a bound that is not
/// a finite number, or with an empty rectangle, are skipped; when two rows name the same area the first one wins.
/// </summary>
public static class WorldMapAreaDbcReader
{
    public const int FieldCount = 8;

    private const int FieldMap = 1;
    private const int FieldArea = 2;
    private const int FieldLeft = 4;

    public static IReadOnlyDictionary<uint, WorldMapAreaBounds> Read(string path) => Read(DbcFile.Load(path));

    public static IReadOnlyDictionary<uint, WorldMapAreaBounds> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.RequireFields(FieldCount, static table => $"WorldMapArea.dbc requires {FieldCount} four-byte fields, found {table.FieldCount}/{table.RecordSize}");

        var bounds = new Dictionary<uint, WorldMapAreaBounds>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint map = file.GetUInt32(row, FieldMap);
            uint area = file.GetUInt32(row, FieldArea);
            if (area == 0 || map > 1)
                continue;

            var rectangle = new WorldMapAreaBounds(
                file.GetFloat(row, FieldLeft), file.GetFloat(row, FieldLeft + 1), file.GetFloat(row, FieldLeft + 2), file.GetFloat(row, FieldLeft + 3));
            if (!float.IsFinite(rectangle.Left) || !float.IsFinite(rectangle.Right) || !float.IsFinite(rectangle.Top) || !float.IsFinite(rectangle.Bottom)
                || rectangle.Left == rectangle.Right || rectangle.Top == rectangle.Bottom)
                continue;

            bounds.TryAdd(area, rectangle);
        }

        return bounds;
    }

    /// <summary>
    /// Reads <paramref name="path"/> for the bot defense call text. A blank path is not an error (no coordinates, no warning). A missing, unreadable or
    /// malformed file reports one line to <paramref name="warn"/> and gives an empty table, so calls name the place without coordinates.
    /// </summary>
    public static IReadOnlyDictionary<uint, WorldMapAreaBounds> TryRead(string? path, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(warn);
        if (string.IsNullOrWhiteSpace(path))
            return new Dictionary<uint, WorldMapAreaBounds>();

        try
        {
            return Read(path);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            warn($"WorldMapArea.dbc could not be read ({error.Message}); bot defense calls name the place without map coordinates.");
            return new Dictionary<uint, WorldMapAreaBounds>();
        }
    }
}
