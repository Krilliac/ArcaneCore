using System.Buffers.Binary;
using System.Text;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Reputation;
using ArcaneCore.Kernel.Reputation;
using Xunit;

namespace ArcaneCore.Data.Tests.Reputation;

/// <summary>Faction.dbc decoding on the shared WDBC reader (build 5875: 37 four-byte fields).</summary>
public sealed class FactionDbcTests
{
    [Fact]
    public void SyntheticVanillaRecord_DecodesEveryUsedField_InFileOrder()
    {
        // Independent file-order vector from the pinned FactionEntryfmt, not an exported catalog.
        uint[] row = new uint[37];
        row[0] = 72;                       // id
        row[1] = 7;                        // reputation list index
        row[2] = 77; row[3] = 178;         // race masks (alliance, horde)
        row[6] = 0; row[7] = 0x400;        // class masks (any, druid)
        row[10] = 3000; row[11] = unchecked((uint)-42000);
        row[14] = 0x11; row[15] = 0x0E;    // base flags
        row[18] = 469;                     // parent faction
        row[19] = 1;                       // enUS name offset
        FactionCatalog catalog = FactionDbcReader.Read(DbcFile.Parse(Image(37, "\0Stormwind\0", row)));

        FactionRecord faction = Assert.IsType<FactionRecord>(catalog.Find(72));
        Assert.Equal(7, faction.ReputationListId);
        Assert.Equal([77u, 178u, 0u, 0u], faction.RaceMasks);
        Assert.Equal([0u, 0x400u, 0u, 0u], faction.ClassMasks);
        Assert.Equal([3000, -42000, 0, 0], faction.BaseValues);
        Assert.Equal([0x11u, 0x0Eu, 0u, 0u], faction.BaseFlags);
        Assert.Equal(469u, faction.ParentFactionId);
        Assert.Equal("Stormwind", faction.Name);
        Assert.Same(faction, catalog.FindByListId(7));
        // Human warrior uses slot 0; a tauren druid matches slot 1 (race and class).
        Assert.Equal((3000, 0x11u), (faction.BaseReputation(1, 1), faction.DefaultFlags(1, 1)));
        Assert.Equal((-42000, 0x0Eu), (faction.BaseReputation(1u << 5, 0x400), faction.DefaultFlags(1u << 5, 0x400)));
        // A tauren warrior fits neither slot 0 (race) nor slot 1 (class); slots 2/3 are zero-masked wildcards.
        Assert.Equal(2, faction.BaseSlotFor(1u << 5, 1));
    }

    [Fact]
    public void NoReputationFactions_AreKept_ButNotListed()
    {
        uint[] mob = new uint[37];
        mob[0] = 15;
        mob[1] = uint.MaxValue; // -1: no reputation
        FactionCatalog catalog = FactionDbcReader.Read(DbcFile.Parse(Image(37, "\0", mob)));
        Assert.False(catalog.Find(15)!.CanHaveReputation);
        Assert.Empty(catalog.ReputationFactions);
    }

    [Theory]
    [InlineData(36)]
    [InlineData(38)]
    [InlineData(14)]
    public void OtherLayouts_AreRejected(int fields)
        => Assert.Throws<InvalidDataException>(() => FactionDbcReader.Read(DbcFile.Parse(Image(fields, "\0", new uint[fields]))));

    [Fact]
    public void ZeroDuplicateAndOutOfRangeSlots_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => FactionDbcReader.Read(DbcFile.Parse(Image(37, "\0", new uint[37]))));
        uint[] a = new uint[37];
        a[0] = 1;
        a[1] = 3;
        uint[] b = (uint[])a.Clone();
        b[0] = 2;
        Assert.Throws<InvalidDataException>(() => FactionDbcReader.Read(DbcFile.Parse(Image(37, "\0", a, a))));
        Assert.Throws<InvalidDataException>(() => FactionDbcReader.Read(DbcFile.Parse(Image(37, "\0", a, b))));
        uint[] wide = (uint[])a.Clone();
        wide[1] = FactionCatalog.ReputationListSize;
        Assert.Throws<InvalidDataException>(() => FactionDbcReader.Read(DbcFile.Parse(Image(37, "\0", wide))));
    }

    private static byte[] Image(int fields, string strings, params uint[][] records)
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

        block.CopyTo(image.AsSpan(20 + (records.Length * fields * 4)));
        return image;
    }
}
