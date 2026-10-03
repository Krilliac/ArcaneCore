using System.Buffers.Binary;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Talents;
using ArcaneCore.Kernel.Talents;
using Xunit;

namespace ArcaneCore.Data.Tests.Talents;

/// <summary>
/// Synthetic build-5875 Talent.dbc / TalentTab.dbc images. Field indexes follow vmangos
/// Database/DBCfmt.h:81-82 ("niiiiiiiixxxxixxixxxi" = 21 fields, "nxxxxxxxxxxxiix" = 15 fields) and
/// DBCStructure.h:660-686.
/// </summary>
public sealed class TalentDbcTests
{
    private static uint[] TalentRow(uint id, uint tab, uint row, uint col, uint[] ranks, uint dependsOn = 0, uint dependsOnRank = 0, uint dependsOnSpell = 0)
    {
        uint[] fields = new uint[21];
        fields[0] = id;
        fields[1] = tab;
        fields[2] = row;
        fields[3] = col;
        for (int i = 0; i < 5; i++)
        {
            fields[4 + i] = ranks[i];
        }

        fields[13] = dependsOn;
        fields[16] = dependsOnRank;
        fields[20] = dependsOnSpell;
        return fields;
    }

    private static uint[] TabRow(uint id, uint classMask, uint order)
    {
        uint[] fields = new uint[15];
        fields[0] = id;
        fields[12] = classMask;
        fields[13] = order;
        return fields;
    }

    [Fact]
    public void Talent_FieldsLandOnTheVmangosIndexes()
    {
        TalentCatalog catalog = TalentDbcReaders.ReadCatalog(
            DbcFile.Parse(Image(21, TalentRow(7, 161, 2, 3, [100, 101, 102, 0, 0], dependsOn: 0, dependsOnRank: 0, dependsOnSpell: 555),
                TalentRow(8, 161, 3, 1, [200, 201, 0, 0, 0], dependsOn: 7, dependsOnRank: 2))),
            DbcFile.Parse(Image(15, TabRow(161, 1, 4))));

        TalentRecord first = catalog.ById(7)!;
        Assert.Equal(161u, first.TabId);
        Assert.Equal(2u, first.Row);
        Assert.Equal(3u, first.Column);
        Assert.Equal<uint>([100, 101, 102, 0, 0], first.RankSpells);
        Assert.Equal(3, first.RankCount);
        Assert.Equal(555u, first.DependsOnSpell);

        TalentRecord second = catalog.ById(8)!;
        Assert.Equal(7u, second.DependsOn);
        Assert.Equal(2u, second.DependsOnRank);
        Assert.Equal(2, second.RankCount);

        TalentTabRecord tab = catalog.Tab(161)!;
        Assert.Equal(1u, tab.ClassMask);
        Assert.Equal(4u, tab.Order);
    }

    [Theory]
    [InlineData(20, 15)]
    [InlineData(22, 15)]
    [InlineData(21, 14)]
    [InlineData(21, 16)]
    public void OtherFieldCounts_AreRejected(int talentFields, int tabFields)
    {
        DbcFile talents = DbcFile.Parse(Image(talentFields));
        DbcFile tabs = DbcFile.Parse(Image(tabFields));
        Assert.Throws<InvalidDataException>(() => TalentDbcReaders.ReadCatalog(talents, tabs));
    }

    [Fact]
    public void ExactFieldCounts_AreAccepted()
    {
        TalentCatalog empty = TalentDbcReaders.ReadCatalog(DbcFile.Parse(Image(21)), DbcFile.Parse(Image(15)));
        Assert.Equal(0, empty.TalentCount);
    }

    [Fact]
    public void LoadFromPaths_ReadsBothFiles()
    {
        string directory = Directory.CreateTempSubdirectory("arcane-talents").FullName;
        try
        {
            string talentPath = Path.Combine(directory, "Talent.dbc");
            string tabPath = Path.Combine(directory, "TalentTab.dbc");
            File.WriteAllBytes(talentPath, Image(21, TalentRow(1, 5, 0, 0, [10, 0, 0, 0, 0])));
            File.WriteAllBytes(tabPath, Image(15, TabRow(5, 2, 0)));
            TalentCatalog catalog = TalentDbcReaders.Load(talentPath, tabPath);
            Assert.Equal(1, catalog.TalentCount);
            Assert.Equal(1, catalog.TabCount);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Real-data oracle: runs only when ARCANECORE_TALENT_DBC_DIR points at a developer-supplied
    /// build-5875 Talent.dbc + TalentTab.dbc (never committed) and ARCANECORE_TALENT_IDS_FILE at a
    /// text file with one talent id per line (the 432 ids of wow_messages cmsg_learn_talent.wowm).
    /// </summary>
    [Fact]
    public void RealDbc_ContainsEveryVanillaTalentId_WhenSupplied()
    {
        string? dir = Environment.GetEnvironmentVariable("ARCANECORE_TALENT_DBC_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        TalentCatalog catalog = TalentDbcReaders.Load(Path.Combine(dir, "Talent.dbc"), Path.Combine(dir, "TalentTab.dbc"));
        string? idsFile = Environment.GetEnvironmentVariable("ARCANECORE_TALENT_IDS_FILE");
        Assert.False(string.IsNullOrEmpty(idsFile), "ARCANECORE_TALENT_IDS_FILE must list the oracle talent ids");
        string[] ids = File.ReadAllLines(idsFile!).Where(l => l.Length > 0).ToArray();
        Assert.NotEmpty(ids);
        foreach (string id in ids)
        {
            Assert.NotNull(catalog.ById(uint.Parse(id)));
        }
    }

    internal static byte[] Image(int fields, params uint[][] records)
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
