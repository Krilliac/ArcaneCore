using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Crafting;
using ArcaneCore.Data.Tests.Skills;
using ArcaneCore.Kernel.Crafting;
using Xunit;
using Xunit.Abstractions;

namespace ArcaneCore.Data.Tests.Crafting;

/// <summary>
/// Crafting lane, slice spell-item-enchantment-data: the strict 24-field decoder of SpellItemEnchantment.dbc (vmangos DBCfmt.h:75). The synthetic image
/// is built from the field positions of DBCStructure.h:639-651, not from the reader's own constants.
/// </summary>
public sealed class EnchantDbcReaderTests(ITestOutputHelper output)
{
    private const int Fields = 24;

    /// <summary>One row at the vmangos field positions: [0] id, [1-3] type, [4-6] amount, [7-9] max amount, [10-12] arg, [13-20] names, [21], [22] visual, [23] flags.</summary>
    private static uint[] Row(uint id, (uint Type, int Amount, uint Arg)[] effects, uint nameOffset, uint visual, uint flags)
    {
        uint[] row = new uint[Fields];
        row[0] = id;
        for (int i = 0; i < 3; i++)
        {
            row[1 + i] = effects[i].Type;
            row[4 + i] = unchecked((uint)effects[i].Amount);
            row[7 + i] = unchecked((uint)(effects[i].Amount + 100));   // the unread maximum points: must not leak into the amount
            row[10 + i] = effects[i].Arg;
        }

        row[13] = nameOffset;
        row[22] = visual;
        row[23] = flags;
        return row;
    }

    private static byte[] Image(int fields, string strings, params uint[][] records)
    {
        byte[] block = Encoding.UTF8.GetBytes("\0" + strings + "\0");
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

        block.CopyTo(image.AsSpan(20 + (records.Length * fields * 4)));
        return image;
    }

    [Fact]
    public void ARow_DecodesEveryFieldFromItsVmangosPosition()
    {
        // 1 = "Minor Agility": STAT, +1, arg 3 (ITEM_MOD_AGILITY); two empty effects; visual 24; flags 0.
        EnchantCatalog catalog = EnchantDbcReader.Read(DbcFile.Parse(Image(Fields, "Minor Agility",
            Row(1, [(5, 1, 3), (0, 0, 0), (0, 0, 0)], nameOffset: 1, visual: 24, flags: 0),
            Row(2, [(1, 0, 7), (2, 3, 0), (4, 20, 2)], nameOffset: 0, visual: 7, flags: 0x01))));

        SpellItemEnchantment agility = catalog.Find(1)!;
        Assert.Equal([5u, 0u, 0u], agility.Types);
        Assert.Equal([1, 0, 0], agility.Amounts);
        Assert.Equal([3u, 0u, 0u], agility.Args);
        Assert.Equal("Minor Agility", agility.Name);
        Assert.Equal((24u, 0u), (agility.VisualId, agility.Flags));

        SpellItemEnchantment mixed = catalog.Find(2)!;
        Assert.Equal([1u, 2u, 4u], mixed.Types);
        Assert.Equal([0, 3, 20], mixed.Amounts);
        Assert.Equal([7u, 0u, 2u], mixed.Args);
        Assert.Equal(EnchantCatalog.CanSoulboundFlag, mixed.Flags);
        Assert.Equal(2, catalog.Count);
        Assert.Null(catalog.Find(3));
    }

    [Fact]
    public void ANegativeAmount_IsKeptSigned()
    {
        EnchantCatalog catalog = EnchantDbcReader.Read(DbcFile.Parse(Image(Fields, "", Row(9, [(5, -4, 7), (0, 0, 0), (0, 0, 0)], 0, 0, 0))));

        Assert.Equal(-4, catalog.Find(9)!.Amounts[0]);
    }

    [Theory]
    [InlineData(23)]
    [InlineData(25)]
    public void OtherClientLayouts_AreRejected(int fields)
        => Assert.Throws<InvalidDataException>(() => EnchantDbcReader.Read(DbcFile.Parse(Image(fields, "", new uint[fields]))));

    [Fact]
    public void ADuplicateId_IsRejected()
    {
        uint[] row = Row(5, [(0, 0, 0), (0, 0, 0), (0, 0, 0)], 0, 0, 0);

        Assert.Throws<InvalidDataException>(() => EnchantDbcReader.Read(DbcFile.Parse(Image(Fields, "", row, row))));
    }

    [Fact]
    public void TheEmptyCatalog_MissesEveryLookup()
    {
        Assert.Null(EnchantCatalog.Empty.Find(1));
        Assert.Equal(0, EnchantCatalog.Empty.Count);
    }

    [RealDbcFact]
    public void RealDbcProbe_DecodesTheDevelopersFile_AndKnownEnchantsResolve()
    {
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        DbcFile file = DbcFile.Load(Path.Combine(dir, "SpellItemEnchantment.dbc"));
        EnchantCatalog catalog = EnchantDbcReader.Read(file);
        output.WriteLine($"SpellItemEnchantment {file.FieldCount} fields / {file.RecordCount} rows, {catalog.Count} decoded");

        Assert.Equal(file.RecordCount, catalog.Count);
        // Each row has at most the six display types of vmangos DBCEnums.h:164-170.
        int probed = 0;
        for (uint id = 1; id < 4000; id++)
        {
            if (catalog.Find(id) is not { } row)
            {
                continue;
            }

            Assert.All(row.Types, t => Assert.InRange(t, 0u, (uint)EnchantEffectType.Totem));
            probed++;
        }

        Assert.True(probed > 1000, $"only {probed} rows probed");
        output.WriteLine($"{probed} probes ran / 0 skipped");
    }
}
