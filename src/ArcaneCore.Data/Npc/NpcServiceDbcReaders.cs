using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Data.Npc;

/// <summary>
/// Decoders for the developer-supplied build-5875 DBC files the NPC services use (never
/// downloaded by the daemon). Field counts follow vmangos/core Database/DBCfmt.h:
/// TaxiPathNodeEntryfmt "diiifffii", SkillLineAbilityfmt "niiiixxiiiiixx",
/// DurabilityCostsfmt (id + 29 multipliers), DurabilityQualityfmt "nf", BankBagSlotPricesEntryfmt "ni".
/// </summary>
public static class NpcServiceDbcReaders
{
    public static TaxiPathNodeCatalog LoadTaxiPathNodes(string path) => ReadTaxiPathNodes(DbcFile.Load(path));

    public static TaxiPathNodeCatalog ReadTaxiPathNodes(DbcFile file)
    {
        Require(file, 9, "TaxiPathNode.dbc");
        var rows = new List<TaxiPathNodeRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new TaxiPathNodeRecord(
                file.GetUInt32(row, 0), file.GetUInt32(row, 1), file.GetUInt32(row, 2), file.GetUInt32(row, 3),
                Finite(file.GetFloat(row, 4)), Finite(file.GetFloat(row, 5)), Finite(file.GetFloat(row, 6)),
                file.GetUInt32(row, 7), file.GetUInt32(row, 8)));
        }

        return new TaxiPathNodeCatalog(rows);
    }

    public static SkillLineAbilityCatalog LoadSkillLineAbilities(string path) => ReadSkillLineAbilities(DbcFile.Load(path));

    public static SkillLineAbilityCatalog ReadSkillLineAbilities(DbcFile file)
    {
        Require(file, 14, "SkillLineAbility.dbc");
        var rows = new List<SkillLineAbilityRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new SkillLineAbilityRecord(
                file.GetUInt32(row, 0), file.GetUInt32(row, 1), file.GetUInt32(row, 2), file.GetUInt32(row, 3), file.GetUInt32(row, 4),
                file.GetUInt32(row, 7), file.GetUInt32(row, 8), file.GetUInt32(row, 9), file.GetUInt32(row, 10), file.GetUInt32(row, 11)));
        }

        return new SkillLineAbilityCatalog(rows);
    }

    public static RepairCostTable LoadRepairCosts(string costsPath, string qualityPath)
        => ReadRepairCosts(DbcFile.Load(costsPath), DbcFile.Load(qualityPath));

    public static RepairCostTable ReadRepairCosts(DbcFile costs, DbcFile quality)
    {
        Require(costs, 1 + RepairCostTable.MultiplierCount, "DurabilityCosts.dbc");
        Require(quality, 2, "DurabilityQuality.dbc");
        var costRows = new List<(uint, uint[])>(costs.RecordCount);
        for (int row = 0; row < costs.RecordCount; row++)
        {
            uint[] multipliers = new uint[RepairCostTable.MultiplierCount];
            for (int i = 0; i < multipliers.Length; i++)
            {
                multipliers[i] = costs.GetUInt32(row, i + 1);
            }

            costRows.Add((costs.GetUInt32(row, 0), multipliers));
        }

        var qualityRows = new List<(uint, float)>(quality.RecordCount);
        for (int row = 0; row < quality.RecordCount; row++)
        {
            float factor = quality.GetFloat(row, 1);
            if (!float.IsFinite(factor) || factor < 0)
            {
                throw new InvalidDataException("DurabilityQuality.dbc factors must be finite and non-negative");
            }

            qualityRows.Add((quality.GetUInt32(row, 0), factor));
        }

        try
        {
            return new RepairCostTable(costRows, qualityRows);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("duplicate DurabilityCosts/DurabilityQuality id", error);
        }
    }

    public static BankBagSlotPriceTable LoadBankBagSlotPrices(string path) => ReadBankBagSlotPrices(DbcFile.Load(path));

    public static BankBagSlotPriceTable ReadBankBagSlotPrices(DbcFile file)
    {
        Require(file, 2, "BankBagSlotPrices.dbc");
        var rows = new List<(uint, uint)>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add((file.GetUInt32(row, 0), file.GetUInt32(row, 1)));
        }

        try
        {
            return new BankBagSlotPriceTable(rows);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("duplicate BankBagSlotPrices.dbc id", error);
        }
    }

    private static void Require(DbcFile file, int fields, string name)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.FieldCount != fields || file.RecordSize != fields * 4)
        {
            throw new InvalidDataException($"build-5875 {name} requires {fields} four-byte fields");
        }
    }

    private static float Finite(float value)
        => float.IsFinite(value) ? value : throw new InvalidDataException("TaxiPathNode.dbc positions must be finite");
}
