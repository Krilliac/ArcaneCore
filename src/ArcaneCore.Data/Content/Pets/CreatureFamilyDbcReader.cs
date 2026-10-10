using ArcaneCore.Data.Content.Spells;

namespace ArcaneCore.Data.Content.Pets;

/// <summary>
/// Decode the developer-supplied build-5875 CreatureFamily.dbc into pet food masks (vmangos CreatureFamilyEntry.petFoodMask,
/// read by Pet::HaveInDiet) and first skill lines (skillLine[0], Pet::CanLearnPetSpell) by family id. Layout (ClientDbcLayouts CreatureFamily.dbc): ID 0, min/max scale and levels 1-4, two skill lines 5-6,
/// petFoodMask 7, name 8-16, icon 17: eighteen four-byte fields.
/// </summary>
public static class CreatureFamilyDbcReader
{
    public const int FieldCount = 18;
    public const int PetFoodMaskField = 7;

    /// <summary>
    /// The first of the two skill lines (vmangos DBCStructure.h:250 skillLine[2], fields 5-6): the family's own ability line, the one
    /// Pet::CanLearnPetSpell checks (field 6 is SKILL_PET_TALENTS 270 for hunter families, 0 for demons).
    /// </summary>
    public const int SkillLineField = 5;

    public static IReadOnlyDictionary<uint, uint> LoadFoodMasks(string path) => ReadFoodMasks(DbcFile.Load(path));

    public static IReadOnlyDictionary<uint, uint> ReadFoodMasks(DbcFile file) => ReadColumn(file, PetFoodMaskField);

    /// <summary>Family id to skillLine[0] (the family's ability skill line).</summary>
    public static IReadOnlyDictionary<uint, uint> LoadSkillLines(string path) => ReadSkillLines(DbcFile.Load(path));

    public static IReadOnlyDictionary<uint, uint> ReadSkillLines(DbcFile file) => ReadColumn(file, SkillLineField);

    private static Dictionary<uint, uint> ReadColumn(DbcFile file, int field)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != FieldCount)
        {
            throw new InvalidDataException($"build-5875 CreatureFamily.dbc requires {FieldCount} four-byte fields, found {file.FieldCount}");
        }

        var values = new Dictionary<uint, uint>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint id = file.GetUInt32(row, 0);
            if (id == 0 || !values.TryAdd(id, file.GetUInt32(row, field)))
            {
                throw new InvalidDataException($"CreatureFamily.dbc has a zero or duplicate family id {id}");
            }
        }

        return values;
    }
}
