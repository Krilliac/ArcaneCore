using System.Buffers.Binary;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Npc;
using ArcaneCore.Kernel.Npc;
using Xunit;

namespace ArcaneCore.Data.Tests.Npc;

/// <summary>Synthetic build-5875 images for the NPC service DBC readers (vmangos DBCfmt.h layouts).</summary>
public sealed class NpcServiceDbcTests
{
    private static uint F(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void TaxiNodesAndPaths_ReadVanillaColumnsAndRejectWrongWidths()
    {
        // vmangos Database/DBCfmt.h:83-85: 16 fields in TaxiNodes, four in TaxiPath.
        IReadOnlyList<TaxiNode> nodes = NpcServiceDbcReaders.ReadTaxiNodes(DbcFile.Parse(Image(16,
            [1, 0, F(2), F(3), F(4), 0, 0, 0, 0, 0, 0, 0, 0, 0, 2224, 3837])));
        Assert.Equal((1u, 0u, 2f, 3f, 4f, 2224u, 3837u),
            (nodes[0].Id, nodes[0].MapId, nodes[0].X, nodes[0].Y, nodes[0].Z, nodes[0].MountHorde, nodes[0].MountAlliance));
        TaxiPath path = Assert.Single(NpcServiceDbcReaders.ReadTaxiPaths(DbcFile.Parse(Image(4, [10, 1, 2, 73]))));
        Assert.Equal((10u, 1u, 2u, 73u), (path.Id, path.FromNode, path.ToNode, path.Price));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadTaxiNodes(DbcFile.Parse(Image(15, new uint[15]))));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadTaxiPaths(DbcFile.Parse(Image(5, new uint[5]))));
    }

    [Fact]
    public void TaxiPathNode_GroupsByPathInIndexOrder()
    {
        TaxiPathNodeCatalog catalog = NpcServiceDbcReaders.ReadTaxiPathNodes(DbcFile.Parse(Image(9,
            [2, 10, 1, 0, F(32), F(0), F(90), 0, 0],
            [1, 10, 0, 0, F(0), F(0), F(83.5f), 0, 0],
            [3, 11, 0, 1, F(5), F(6), F(7), 2, 30])));
        Assert.Equal(2, catalog.PathCount);
        Assert.Equal(
        [
            new TaxiPathNodeRecord(1, 10, 0, 0, 0, 0, 83.5f, 0, 0),
            new TaxiPathNodeRecord(2, 10, 1, 0, 32, 0, 90, 0, 0),
        ], catalog.Nodes(10));
        Assert.Equal(new TaxiPathNodeRecord(3, 11, 0, 1, 5, 6, 7, 2, 30), Assert.Single(catalog.Nodes(11)));
        Assert.Empty(catalog.Nodes(12));
    }

    [Fact]
    public void TaxiPathNode_RejectsOtherLayoutsAndNonFinitePositions()
    {
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadTaxiPathNodes(DbcFile.Parse(Image(8, new uint[8]))));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadTaxiPathNodes(DbcFile.Parse(Image(9,
            [1, 10, 0, 0, F(float.NaN), 0, 0, 0, 0]))));
    }

    [Fact]
    public void SkillLineAbility_ReadsTheVanillaFieldsAndBuildsRankChains()
    {
        // id, skill, spell, racemask, classmask, (2 unused), req value, forward spell, learn on get, max, min, (2 unused)
        SkillLineAbilityCatalog catalog = NpcServiceDbcReaders.ReadSkillLineAbilities(DbcFile.Parse(Image(14,
            [1, 26, 100, 0, 1, 99, 99, 0, 102, 0, 0, 0, 99, 99],
            [2, 26, 102, 0, 1, 0, 0, 5, 0, 1, 300, 150, 0, 0])));
        Assert.Equal(2, catalog.Count);
        Assert.Equal(new SkillLineAbilityRecord(2, 26, 102, 0, 1, 5, 0, 1, 300, 150), Assert.Single(catalog.Abilities(102)));
        Assert.Equal(100u, catalog.PreviousRank(102));
        Assert.True(catalog.FitsClassAndRace(100, 1, 1));
        Assert.False(catalog.FitsClassAndRace(100, 1, 1u << 7));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadSkillLineAbilities(DbcFile.Parse(Image(13, new uint[13]))));
    }

    [Fact]
    public void SkillLineAbility_AcceptsTheCanonical15FieldLayout()
    {
        // vmangos DBCfmt.h:68 SkillLineAbilityfmt "niiiixxiiiiixxi" and mangos-classic Server/DBCfmt.h:70 are
        // fifteen fields wide (the last is reqtrainpoints, DBCStructure.h:555); a 14-field image is still read.
        SkillLineAbilityCatalog catalog = NpcServiceDbcReaders.ReadSkillLineAbilities(DbcFile.Parse(Image(15,
            [1, 26, 100, 0, 1, 99, 99, 0, 102, 0, 0, 0, 99, 99, 0],
            [2, 26, 102, 0, 1, 0, 0, 5, 0, 1, 300, 150, 0, 0, 7])));
        Assert.Equal(2, catalog.Count);
        Assert.Equal(new SkillLineAbilityRecord(2, 26, 102, 0, 1, 5, 0, 1, 300, 150, 7), Assert.Single(catalog.Abilities(102)));
        Assert.Equal(100u, catalog.PreviousRank(102));
        Assert.True(catalog.HasTrainingPoints);
        Assert.Equal(7u, catalog.TrainingPoints(102));
        Assert.Equal(0u, catalog.TrainingPoints(100));
        Assert.Equal(0u, catalog.TrainingPoints(999));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadSkillLineAbilities(DbcFile.Parse(Image(16, new uint[16]))));
    }

    [Fact]
    public void SkillLineAbility_FourteenFieldImage_HasNoTrainingPoints()
    {
        SkillLineAbilityCatalog catalog = NpcServiceDbcReaders.ReadSkillLineAbilities(DbcFile.Parse(Image(14,
            [2, 26, 102, 0, 1, 0, 0, 5, 0, 1, 300, 150, 0, 7])));
        Assert.False(catalog.HasTrainingPoints);
        Assert.Equal(0u, catalog.TrainingPoints(102));
        Assert.Equal(102u, catalog.FirstInChain(102));
    }

    [Fact]
    public void DurabilityCostsAndQuality_PriceRepairs()
    {
        uint[] costRow = new uint[30];
        costRow[0] = 10;
        costRow[1 + 7] = 3;        // one-handed swords
        costRow[1 + 21 + 4] = 5;   // plate
        RepairCostTable table = NpcServiceDbcReaders.ReadRepairCosts(
            DbcFile.Parse(Image(30, costRow)),
            DbcFile.Parse(Image(2, [6, F(1.0f)], [8, F(1.25f)])));
        Assert.False(table.IsEmpty);
        Assert.True(table.TryGetCost(2, 7, 10, 2, 10, out uint sword));
        Assert.Equal(30u, sword);
        Assert.True(table.TryGetCost(4, 4, 10, 3, 8, out uint plate));
        Assert.Equal(50u, plate);
        Assert.False(table.TryGetCost(2, 7, 11, 2, 10, out _));  // no item level row
        Assert.False(table.TryGetCost(2, 7, 10, 0, 10, out _));  // no quality row

        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadRepairCosts(
            DbcFile.Parse(Image(29, new uint[29])), DbcFile.Parse(Image(2, [6, F(1)]))));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadRepairCosts(
            DbcFile.Parse(Image(30, costRow)), DbcFile.Parse(Image(2, [6, F(-1)]))));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadRepairCosts(
            DbcFile.Parse(Image(30, costRow, costRow)), DbcFile.Parse(Image(2, [6, F(1)]))));
    }

    [Fact]
    public void BankBagSlotPrices_MapSlotsToCopper()
    {
        BankBagSlotPriceTable table = NpcServiceDbcReaders.ReadBankBagSlotPrices(DbcFile.Parse(Image(2, [1, 1000], [2, 7500])));
        Assert.Equal(1000u, table.Price(1));
        Assert.Equal(7500u, table.Price(2));
        Assert.Null(table.Price(3));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadBankBagSlotPrices(DbcFile.Parse(Image(2, [1, 1], [1, 2]))));
        Assert.Throws<InvalidDataException>(() => NpcServiceDbcReaders.ReadBankBagSlotPrices(DbcFile.Parse(Image(3, new uint[3]))));
    }

    private static byte[] Image(int fields, params uint[][] records)
    {
        byte[] image = new byte[20 + records.Length * fields * 4 + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (record * fields + field) * 4), records[record][field]);
            }
        }

        return image;
    }
}
