using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Talents;

namespace ArcaneCore.Data.Talents;

/// <summary>
/// Decoders for the developer-supplied build-5875 Talent.dbc and TalentTab.dbc (never downloaded by
/// the daemon, never committed). Layouts: vmangos src/game/Database/DBCfmt.h:81-82 and
/// DBCStructure.h:660-686; mangos-classic src/game/Server/DBCfmt.h:83-84 carries the same strings.
/// Any other field count is refused so a wrong-version file fails at startup.
/// </summary>
public static class TalentDbcReaders
{
    /// <summary>"niiiiiiiixxxxixxixxxi" = 21 four-byte fields.</summary>
    public const int TalentFields = 21;

    /// <summary>"nxxxxxxxxxxxiix" = 15 four-byte fields.</summary>
    public const int TalentTabFields = 15;

    public static TalentCatalog Load(string talentPath, string talentTabPath)
        => ReadCatalog(DbcFile.Load(talentPath), DbcFile.Load(talentTabPath));

    public static TalentCatalog ReadCatalog(DbcFile talents, DbcFile tabs)
    {
        Require(talents, TalentFields, "Talent.dbc");
        Require(tabs, TalentTabFields, "TalentTab.dbc");

        var tabRows = new List<TalentTabRecord>(tabs.RecordCount);
        for (int row = 0; row < tabs.RecordCount; row++)
        {
            tabRows.Add(new TalentTabRecord(tabs.GetUInt32(row, 0), tabs.GetUInt32(row, 12), tabs.GetUInt32(row, 13)));
        }

        var talentRows = new List<TalentRecord>(talents.RecordCount);
        for (int row = 0; row < talents.RecordCount; row++)
        {
            uint[] ranks = new uint[TalentRecord.MaxRanks];
            for (int i = 0; i < ranks.Length; i++)
            {
                ranks[i] = talents.GetUInt32(row, 4 + i);
            }

            talentRows.Add(new TalentRecord(
                talents.GetUInt32(row, 0), talents.GetUInt32(row, 1), talents.GetUInt32(row, 2), talents.GetUInt32(row, 3),
                ranks, talents.GetUInt32(row, 13), talents.GetUInt32(row, 16), talents.GetUInt32(row, 20)));
        }

        return new TalentCatalog(tabRows, talentRows);
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
