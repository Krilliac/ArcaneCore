using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.WorldData.Items;

namespace ArcaneCore.Data.Content.Items;

/// <summary>Reader for the build-5875 SpellItemEnchantment.dbc layout.</summary>
public static class ItemEnchantmentDbcReader
{
    private const int ExpectedFields = 24; // ID, type[3], amount[3], amount2[3], spell[3], names[8], flags, aura, slot

    public static IReadOnlyList<ItemEnchantmentDefinition> Read(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != ExpectedFields)
            throw new InvalidDataException($"SpellItemEnchantment.dbc expected {ExpectedFields} fields, got {file.FieldCount}");

        var result = new List<ItemEnchantmentDefinition>(file.RecordCount);
        var seen = new HashSet<uint>();
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint entry = file.GetUInt32(row, 0);
            if (!seen.Add(entry)) throw new InvalidDataException($"duplicate SpellItemEnchantment.dbc entry {entry}");
            var effects = new ItemEnchantmentEffect[3];
            var amountMax = new uint[3];
            for (int i = 0; i < effects.Length; i++)
            {
                effects[i] = new(file.GetUInt32(row, 1 + i), file.GetUInt32(row, 10 + i), file.GetInt32(row, 4 + i));
                amountMax[i] = file.GetUInt32(row, 7 + i);
            }
            result.Add(new ItemEnchantmentDefinition(entry, effects, amountMax,
                file.GetUInt32(row, 21), file.GetUInt32(row, 22), file.GetUInt32(row, 23)));
        }
        return result;
    }

    public static IReadOnlyList<ItemEnchantmentDefinition> Load(string path)
        => Read(DbcFile.Load(path));
}
