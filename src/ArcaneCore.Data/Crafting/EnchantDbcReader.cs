using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Crafting;

namespace ArcaneCore.Data.Crafting;

/// <summary>
/// Decoder for the developer-supplied build-5875 SpellItemEnchantment.dbc (never downloaded, never copied into the repository). Layout from
/// vmangos Database/DBCfmt.h:75 <c>"niiiiiixxxiiissssssssxii"</c> (24 four-byte fields) and DBCStructure.h:639-651: id 0, effect types 1-3, amounts
/// 4-6 (minimum points; 7-9 are the unread maximum points), effect args 10-12, name strings 13-20 (enUS first), 21 the string flags, visual id 22,
/// flags 23. The field count is strict: another client layout is refused rather than misread.
/// </summary>
public static class EnchantDbcReader
{
    public const int Fields = 24;

    private const int TypeField = 1;
    private const int AmountField = 4;
    private const int ArgField = 10;
    private const int NameField = 13;
    private const int VisualField = 22;
    private const int FlagsField = 23;

    /// <summary>Load and decode the file at <paramref name="path"/>.</summary>
    public static EnchantCatalog Load(string path) => Read(DbcFile.Load(path));

    /// <summary>Decode <paramref name="file"/>. A duplicate id throws <see cref="InvalidDataException"/> (the catalog refuses it).</summary>
    public static EnchantCatalog Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != Fields || file.RecordSize != Fields * 4)
        {
            throw new InvalidDataException($"build-5875 SpellItemEnchantment.dbc requires {Fields} four-byte fields (found {file.FieldCount})");
        }

        var rows = new List<SpellItemEnchantment>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint[] types = new uint[EnchantCatalog.EffectCount];
            int[] amounts = new int[EnchantCatalog.EffectCount];
            uint[] args = new uint[EnchantCatalog.EffectCount];
            for (int i = 0; i < EnchantCatalog.EffectCount; i++)
            {
                types[i] = file.GetUInt32(row, TypeField + i);
                amounts[i] = file.GetInt32(row, AmountField + i);
                args[i] = file.GetUInt32(row, ArgField + i);
            }

            rows.Add(new SpellItemEnchantment(
                file.GetUInt32(row, 0), types, amounts, args, file.GetString(row, NameField), file.GetUInt32(row, VisualField), file.GetUInt32(row, FlagsField)));
        }

        return new EnchantCatalog(rows);
    }
}
