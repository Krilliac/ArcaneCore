using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Kernel.Npc;

namespace ArcaneCore.Data.Npc;

/// <summary>
/// Decoders for the developer-supplied build-5875 DBC files the NPC services use (never
/// downloaded by the daemon). Field counts follow vmangos/core Database/DBCfmt.h:
/// TaxiPathNodeEntryfmt "diiifffii", SkillLineAbilityfmt "niiiixxiiiiixxi" (fifteen fields, the last is reqtrainpoints:
/// vmangos Database/DBCfmt.h:68, DBCStructure.h:540-556; mangos-classic Server/DBCfmt.h:70; a fourteen-field image is still read),
/// DurabilityCostsfmt (id + 29 multipliers), DurabilityQualityfmt "nf", BankBagSlotPricesEntryfmt "ni".
/// </summary>
public static class NpcServiceDbcReaders
{
    /// <summary>vmangos Database/DBCfmt.h:83, TaxiNodesEntryfmt "nifffssssssssxii".</summary>
    public static IReadOnlyList<TaxiNode> LoadTaxiNodes(string path) => ReadTaxiNodes(DbcFile.Load(path));

    public static IReadOnlyList<TaxiNode> ReadTaxiNodes(DbcFile file)
    {
        Require(file, 16, "TaxiNodes.dbc");
        var rows = new List<TaxiNode>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new TaxiNode
            {
                Id = file.GetUInt32(row, 0), MapId = file.GetUInt32(row, 1),
                X = Finite(file.GetFloat(row, 2)), Y = Finite(file.GetFloat(row, 3)), Z = Finite(file.GetFloat(row, 4)),
                Name = file.GetString(row, 5), MountHorde = file.GetUInt32(row, 14), MountAlliance = file.GetUInt32(row, 15),
            });
        }

        return rows;
    }

    /// <summary>vmangos Database/DBCfmt.h:84, TaxiPathEntryfmt "niii".</summary>
    public static IReadOnlyList<TaxiPath> LoadTaxiPaths(string path) => ReadTaxiPaths(DbcFile.Load(path));

    public static IReadOnlyList<TaxiPath> ReadTaxiPaths(DbcFile file)
    {
        Require(file, 4, "TaxiPath.dbc");
        var rows = new List<TaxiPath>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new TaxiPath
            {
                Id = file.GetUInt32(row, 0), FromNode = file.GetUInt32(row, 1),
                ToNode = file.GetUInt32(row, 2), Price = file.GetUInt32(row, 3),
            });
        }

        return rows;
    }

    /// <summary>Canonical SkillLineAbility.dbc width (vmangos DBCfmt.h:68 "niiiixxiiiiixxi").</summary>
    public const int SkillLineAbilityFields = 15;

    /// <summary>The narrower layout without reqtrainpoints this reader has always accepted (the columns read are identical).</summary>
    public const int SkillLineAbilityFieldsWithoutTrainPoints = 14;

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
        => new(ReadSkillLineAbilityRecords(file), hasTrainingPoints: file.FieldCount == SkillLineAbilityFields);

    /// <summary>The raw SkillLineAbility.dbc rows in file order (the skill catalog needs them by skill as well as by spell).</summary>
    public static IReadOnlyList<SkillLineAbilityRecord> ReadSkillLineAbilityRecords(DbcFile file)
    {
        RequireOneOf(file, "SkillLineAbility.dbc", SkillLineAbilityFields, SkillLineAbilityFieldsWithoutTrainPoints);
        var rows = new List<SkillLineAbilityRecord>(file.RecordCount);
        for (int row = 0; row < file.RecordCount; row++)
        {
            rows.Add(new SkillLineAbilityRecord(
                file.GetUInt32(row, 0), file.GetUInt32(row, 1), file.GetUInt32(row, 2), file.GetUInt32(row, 3), file.GetUInt32(row, 4),
                file.GetUInt32(row, 7), file.GetUInt32(row, 8), file.GetUInt32(row, 9), file.GetUInt32(row, 10), file.GetUInt32(row, 11),
                file.FieldCount == SkillLineAbilityFields ? file.GetUInt32(row, 14) : 0));
        }

        return rows;
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

    private static void RequireOneOf(DbcFile file, string name, int first, int second)
    {
        ArgumentNullException.ThrowIfNull(file);
        if ((file.FieldCount != first && file.FieldCount != second) || file.RecordSize != file.FieldCount * 4)
        {
            throw new InvalidDataException($"build-5875 {name} requires {first} (or {second}) four-byte fields");
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
