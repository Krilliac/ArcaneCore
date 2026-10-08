using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.WorldData.Transports;

namespace ArcaneCore.Data.Content.Transports;

/// <summary>
/// TransportAnimation.dbc of build 5875 (vmangos DBCfmt.h TransportAnimationfmt "diifffx": id, transport entry, time segment, x, y, z,
/// movement id; DBCStructure.h:709-718) into a <see cref="TransportAnimationCatalog"/>.
/// </summary>
public static class TransportAnimationDbcReader
{
    /// <summary>The seven four-byte fields of a row.</summary>
    public const int FieldCount = 7;

    public static TransportAnimationCatalog Load(string path) => Read(DbcFile.Load(path));

    public static TransportAnimationCatalog Read(DbcFile dbc)
    {
        ArgumentNullException.ThrowIfNull(dbc);
        if (dbc.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"TransportAnimation.dbc has {dbc.FieldCount} fields, build 5875 has {FieldCount}");
        }

        var rows = new List<(uint, TransportAnimationNode)>(dbc.RecordCount);
        for (int i = 0; i < dbc.RecordCount; i++)
        {
            rows.Add((dbc.GetUInt32(i, 1), new TransportAnimationNode(dbc.GetUInt32(i, 2), dbc.GetFloat(i, 3), dbc.GetFloat(i, 4), dbc.GetFloat(i, 5))));
        }

        return new TransportAnimationCatalog(rows);
    }
}
