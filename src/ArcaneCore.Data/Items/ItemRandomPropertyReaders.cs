using System.Globalization;
using ArcaneCore.Data.ClientData;
using ArcaneCore.Data.World.Creatures;
using ArcaneCore.Kernel.Items;

namespace ArcaneCore.Data.Items;

/// <summary>
/// Decoder for the developer-supplied build-5875 ItemRandomProperties.dbc (never downloaded by the daemon). Layout: vmangos DBCStructure.h
/// ItemRandomPropertiesEntry and DBCfmt.h ItemRandomPropertiesfmt "nsiiixxssssssssx", 16 fields: 0 id, 1 internal name, 2-4 enchantment ids,
/// 5-6 unused, 7-14 the locale suffix names, 15 their flags. The field count is strict; a file with another layout is refused.
/// </summary>
public static class ItemRandomPropertiesDbcReader
{
    public const int FieldCount = 16;

    private const int NameField = 1;
    private const int EnchantField = 2;

    public static IReadOnlyList<ItemRandomPropertyRecord> Load(string path) => Build(DbcFile.Load(path));

    public static IReadOnlyList<ItemRandomPropertyRecord> Build(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        file.RequireFields(FieldCount, static table => $"ItemRandomProperties.dbc has {table.FieldCount} fields, expected {FieldCount}");

        var rows = new List<ItemRandomPropertyRecord>(file.RecordCount);
        HashSet<uint> seen = [];
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (!seen.Add(id))
            {
                throw new InvalidDataException($"ItemRandomProperties.dbc lists property {id} twice");
            }

            uint[] enchants = new uint[ItemRandomPropertyRecord.EnchantSlots];
            for (int i = 0; i < enchants.Length; i++)
            {
                enchants[i] = file.GetUInt32(row, EnchantField + i);
            }

            rows.Add(new ItemRandomPropertyRecord(id, file.GetString(row, NameField), enchants));
        }

        return rows;
    }
}

/// <summary>
/// Parses <c>item_enchantment_template</c> (<c>entry</c>, <c>ench</c>, <c>chance</c>) out of a MySQL world dump through
/// <see cref="MySqlDumpReader"/>; every other table is skipped. vmangos dumps carry <c>patch_min</c>/<c>patch_max</c>: only rows whose range holds
/// patch 10 (1.12) are kept, as vmangos LoadRandomEnchantmentsTable does for its configured patch; cmangos classic-db rows have no range and are
/// all kept. The front end of <see cref="ArcaneCore.Data.Content.Import.ItemTextDumpImporter"/>, which writes the rows to the world database (the
/// world no longer reads a dump; <c>ItemRandomProperties:EnchantmentTemplateDumpPath</c> is retired). A value that is not a number refuses the file.
/// </summary>
public static class ItemEnchantmentTemplateDumpReader
{
    public const string Table = "item_enchantment_template";

    /// <summary>vmangos WowPatch for 1.12.1.</summary>
    public const int Patch = CreatureDumpImporter.MaxPatch;

    public static IReadOnlyList<ItemEnchantmentChance> Read(TextReader dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var rows = new List<ItemEnchantmentChance>();
        foreach (object item in new MySqlDumpReader(dump).Read())
        {
            if (item is DumpRow row && Parse(row) is { } chance)
            {
                rows.Add(chance);
            }
        }

        return rows;
    }

    /// <summary>One dump row as a candidate, or null when it belongs to another table or lies outside patch 10.</summary>
    /// <exception cref="InvalidDataException">The row has no entry, ench or chance, or a value that is not a number.</exception>
    public static ItemEnchantmentChance? Parse(DumpRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (!string.Equals(row.Table, Table, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (row.TryGet(out string? min, "patch_min") && min is not null && Integer(min, "patch_min") > Patch)
        {
            return null;
        }

        if (row.TryGet(out string? max, "patch_max") && max is not null && Integer(max, "patch_max") < Patch)
        {
            return null;
        }

        return new ItemEnchantmentChance(UInt(Required(row, "entry"), "entry"), UInt(Required(row, "ench"), "ench"), Float(Required(row, "chance")));
    }

    private static string Required(DumpRow row, string column)
        => row.TryGet(out string? value, column) && value is not null ? value : throw new InvalidDataException($"{Table}: a row has no {column}");

    private static uint UInt(string value, string column)
        => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint number)
            ? number
            : throw new InvalidDataException($"{Table}: {column} '{value}' is not a number");

    private static int Integer(string value, string column)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : throw new InvalidDataException($"{Table}: {column} '{value}' is not a number");

    private static float Float(string value)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number)
            ? number
            : throw new InvalidDataException($"{Table}: chance '{value}' is not a number");
}
