using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.Pets;

/// <summary>
/// Decode the developer-supplied build-5875 CreatureFamily.dbc into pet food masks by family id (vmangos CreatureFamilyEntry.petFoodMask,
/// read by Pet::HaveInDiet). Layout (ClientDbcLayouts CreatureFamily.dbc): ID 0, min/max scale and levels 1-4, two skill lines 5-6,
/// petFoodMask 7, name 8-16, icon 17: eighteen four-byte fields.
/// </summary>
public static class CreatureFamilyDbcReader
{
    public const int FieldCount = 18;
    public const int PetFoodMaskField = 7;

    public static IReadOnlyDictionary<uint, uint> LoadFoodMasks(string path) => ReadFoodMasks(DbcFile.Load(path));

    public static IReadOnlyDictionary<uint, uint> ReadFoodMasks(DbcFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"build-5875 CreatureFamily.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}");
        }

        var masks = new Dictionary<uint, uint>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (id == 0 || !masks.TryAdd(id, file.GetUInt32(row, PetFoodMaskField)))
            {
                throw new InvalidDataException($"CreatureFamily.dbc has a zero or duplicate family id {id}");
            }
        }

        return masks;
    }
}
