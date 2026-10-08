using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Items;
using ArcaneCore.Kernel.Items;
using Xunit;

namespace ArcaneCore.Data.Tests.Items;

/// <summary>
/// Random property content: a synthetic build-5875 ItemRandomProperties.dbc image (vmangos DBCfmt.h "nsiiixxssssssssx", 16 fields) and
/// <c>item_enchantment_template</c> rows in the vmangos (patch range) and cmangos classic-db (no range) dump layouts.
/// </summary>
public sealed class ItemRandomPropertyReaderTests
{
    public static uint[] Row(uint id, uint nameOffset, uint a, uint b, uint c)
    {
        uint[] row = new uint[ItemRandomPropertiesDbcReader.FieldCount];
        (row[0], row[1], row[2], row[3], row[4]) = (id, nameOffset, a, b, c);
        return row;
    }

    public static byte[] Image(int fields, string strings, params uint[][] records)
    {
        byte[] block = Encoding.UTF8.GetBytes(strings);
        byte[] image = new byte[20 + (records.Length * fields * 4) + block.Length];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), (uint)block.Length);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        block.CopyTo(image, 20 + (records.Length * fields * 4));
        return image;
    }

    [Fact]
    public void Dbc_ReadsIdNameAndTheThreeEnchantments()
    {
        byte[] image = Image(ItemRandomPropertiesDbcReader.FieldCount, "\0of the Bear\0of the Eagle\0",
            Row(1001, 1, 74, 75, 0), Row(1002, 13, 76, 0, 0));
        IReadOnlyList<ItemRandomPropertyRecord> rows = ItemRandomPropertiesDbcReader.Build(DbcFile.Parse(image));
        Assert.Equal(2, rows.Count);
        Assert.Equal((1001u, "of the Bear"), (rows[0].Id, rows[0].Name));
        Assert.Equal([74u, 75u, 0u], rows[0].EnchantIds);
        Assert.Equal([76u, 0u, 0u], rows[1].EnchantIds);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(17)]
    public void Dbc_RefusesAnyOtherFieldCount(int fields)
        => Assert.Throws<InvalidDataException>(() => ItemRandomPropertiesDbcReader.Build(DbcFile.Parse(Image(fields, "\0", new uint[fields]))));

    [Fact]
    public void Dbc_RefusesADuplicateId()
        => Assert.Throws<InvalidDataException>(() => ItemRandomPropertiesDbcReader.Build(DbcFile.Parse(
            Image(ItemRandomPropertiesDbcReader.FieldCount, "\0", Row(5, 0, 1, 0, 0), Row(5, 0, 2, 0, 0)))));

    [Fact]
    public void Dump_KeepsThePatchTenRows_OfTheVmangosLayout()
    {
        IReadOnlyList<ItemEnchantmentChance> rows = ItemEnchantmentTemplateDumpReader.Read(new StringReader("""
            CREATE TABLE `item_enchantment_template` (`entry` int, `ench` int, `chance` float, `patch_min` tinyint, `patch_max` tinyint);
            INSERT INTO `item_enchantment_template` VALUES (6,1001,25.5,0,10),(6,1002,74.5,0,10),(6,1003,10,0,9),(7,1004,100,11,12);
            CREATE TABLE `page_text` (`entry` int, `text` text, `next_page` int);
            INSERT INTO `page_text` VALUES (1,'not a row',0);
            """));
        Assert.Equal([new ItemEnchantmentChance(6, 1001, 25.5f), new ItemEnchantmentChance(6, 1002, 74.5f)], rows);
    }

    [Fact]
    public void Dump_KeepsEveryRow_OfTheClassicDbLayout()
    {
        IReadOnlyList<ItemEnchantmentChance> rows = ItemEnchantmentTemplateDumpReader.Read(new StringReader("""
            CREATE TABLE `item_enchantment_template` (`entry` mediumint, `ench` mediumint, `chance` float);
            INSERT INTO `item_enchantment_template` VALUES (6,1001,25),(9,1002,0.5);
            """));
        Assert.Equal([new ItemEnchantmentChance(6, 1001, 25f), new ItemEnchantmentChance(9, 1002, 0.5f)], rows);
    }

    [Fact]
    public void Dump_RefusesANonNumericChance()
        => Assert.Throws<InvalidDataException>(() => ItemEnchantmentTemplateDumpReader.Read(new StringReader(
            "CREATE TABLE `item_enchantment_template` (`entry` int, `ench` int, `chance` float);\nINSERT INTO `item_enchantment_template` VALUES (6,1001,'x');")));

    [Fact]
    public void Catalog_DropsChancesOutsideTheVmangosRange()
    {
        var catalog = new ItemRandomPropertyCatalog([], [new(6, 1, 0f), new(6, 2, 100.5f), new(6, 3, 50f)]);
        Assert.Equal([new ItemEnchantmentChance(6, 3, 50f)], catalog.Group(6));
        Assert.Empty(catalog.Group(7));
    }
}
