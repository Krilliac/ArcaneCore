using ArcaneCore.Kernel.WorldData;

namespace ArcaneCore.Data.Content.Spells;

/// <summary>
/// Decode the developer-supplied build-5875 SpellShapeshiftForm.dbc (never downloaded by the daemon). Layout:
/// cmangos-classic DBCStructure.h SpellShapeshiftFormEntry (ID 0, button position 1, eight names 2-9, name flags
/// 10, flags1 11, creatureType 12, attack icon 13): fourteen four-byte fields.
/// </summary>
public static class ShapeshiftFormDbcReader
{
    public const int FieldCount = 14;

    public static ShapeshiftFormCatalog Load(string path) => Read(DbcFile.Load(path));

    public static ShapeshiftFormCatalog Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"build-5875 SpellShapeshiftForm.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}");
        }

        var rows = new List<ShapeshiftFormInfo>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (id == 0)
            {
                throw new InvalidDataException("SpellShapeshiftForm.dbc has a zero form id");
            }

            rows.Add(new ShapeshiftFormInfo(id, file.GetUInt32(row, 11), file.GetInt32(row, 12)));
        }

        try
        {
            return new ShapeshiftFormCatalog(rows);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("duplicate form id in SpellShapeshiftForm.dbc", error);
        }
    }
}
