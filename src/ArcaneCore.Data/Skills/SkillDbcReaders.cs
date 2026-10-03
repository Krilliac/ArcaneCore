using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Npc;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.Skills;

namespace ArcaneCore.Data.Skills;

/// <summary>
/// Decoders for the developer-supplied build-5875 skill DBC files (never downloaded by the daemon). Field
/// counts are strict and follow vmangos Database/DBCfmt.h:67-70: SkillLine 22 (<c>"nixssssssssxxxxxxxxxxi"</c>),
/// SkillLineAbility 15 (see <see cref="NpcServiceDbcReaders"/>), SkillRaceClassInfo 8 (<c>"diiiiiix"</c>),
/// SkillTiers 33 (<c>"niiiiiiiiiiiiiiiiiiiiiiiiiiiiiiii"</c>).
/// </summary>
public static class SkillDbcReaders
{
    public const int SkillLineFields = 22;
    public const int SkillRaceClassInfoFields = 8;
    public const int SkillTiersFields = 1 + (2 * SkillTierRecord.StepCount);

    /// <summary>SkillLine.dbc field 3 is the enUS name string (DBCStructure.h:533 <c>name[8]</c> at 3-10).</summary>
    private const int SkillLineNameField = 3;

    private const int SkillLineIconField = 21;

    /// <summary>Load the four files and assemble the catalog (spell-derived tables are supplied by the caller).</summary>
    public static SkillCatalog Load(
        string skillLinePath,
        string skillRaceClassInfoPath,
        string skillTiersPath,
        string skillLineAbilityPath,
        SpellLearnSkillTable? learnSkills = null,
        Func<uint, bool>? spellExists = null)
        => Build(
            DbcFile.Load(skillLinePath),
            DbcFile.Load(skillRaceClassInfoPath),
            DbcFile.Load(skillTiersPath),
            DbcFile.Load(skillLineAbilityPath),
            learnSkills,
            spellExists);

    public static SkillCatalog Build(
        DbcFile skillLine,
        DbcFile skillRaceClassInfo,
        DbcFile skillTiers,
        DbcFile skillLineAbility,
        SpellLearnSkillTable? learnSkills = null,
        Func<uint, bool>? spellExists = null)
        => new(
            ReadSkillLines(skillLine),
            ReadSkillRaceClassInfos(skillRaceClassInfo),
            ReadSkillTiers(skillTiers),
            NpcServiceDbcReaders.ReadSkillLineAbilityRecords(skillLineAbility),
            learnSkills,
            spellExists);

    public static IReadOnlyList<SkillLineRecord> ReadSkillLines(DbcFile file)
    {
        Require(file, SkillLineFields, "SkillLine.dbc");
        var rows = new List<SkillLineRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new SkillLineRecord(
                file.GetUInt32(row, 0), file.GetInt32(row, 1), file.GetString(row, SkillLineNameField), file.GetUInt32(row, SkillLineIconField)));
        }

        return rows;
    }

    public static IReadOnlyList<SkillRaceClassInfoRecord> ReadSkillRaceClassInfos(DbcFile file)
    {
        Require(file, SkillRaceClassInfoFields, "SkillRaceClassInfo.dbc");
        var rows = new List<SkillRaceClassInfoRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new SkillRaceClassInfoRecord(
                file.GetUInt32(row, 1), file.GetUInt32(row, 2), file.GetUInt32(row, 3),
                file.GetUInt32(row, 4), file.GetUInt32(row, 5), file.GetUInt32(row, 6)));
        }

        return rows;
    }

    public static IReadOnlyList<SkillTierRecord> ReadSkillTiers(DbcFile file)
    {
        Require(file, SkillTiersFields, "SkillTiers.dbc");
        var rows = new List<SkillTierRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            uint[] cost = new uint[SkillTierRecord.StepCount];
            uint[] max = new uint[SkillTierRecord.StepCount];
            for (int step = 0; step < SkillTierRecord.StepCount; step++)
            {
                cost[step] = file.GetUInt32(row, 1 + step);
                max[step] = file.GetUInt32(row, 1 + SkillTierRecord.StepCount + step);
            }

            rows.Add(new SkillTierRecord(file.GetUInt32(row, 0), cost, max));
        }

        return rows;
    }

    private static void Require(DbcFile file, int fields, string name)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != fields || file.RecordSize != fields * 4)
        {
            throw new InvalidDataException($"build-5875 {name} requires {fields} four-byte fields");
        }
    }
}
