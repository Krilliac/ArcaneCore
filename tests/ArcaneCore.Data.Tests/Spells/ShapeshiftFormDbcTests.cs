using System.Buffers.Binary;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Tests.Skills;
using ArcaneCore.Kernel.WorldData;
using Xunit;

namespace ArcaneCore.Data.Tests.Spells;

/// <summary>SpellShapeshiftForm.dbc of build 5875 (cmangos-classic DBCStructure.h SpellShapeshiftFormEntry: flags1 at 11, creatureType at 12).</summary>
public sealed class ShapeshiftFormDbcTests
{
    [Fact]
    public void Record_DecodesIdFlags1AndCreatureType_FromTheirFileOrderFields()
    {
        // Independent file-order vector: field 0 = id, 1 = button, 2-9 = names, 10 = name flags, 11 = flags1, 12 = creatureType, 13 = attack icon.
        uint[] bear = [5, 7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, uint.MaxValue, 0];
        uint[] battle = [17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0];

        ShapeshiftFormCatalog catalog = ShapeshiftFormDbcReader.Read(DbcFile.Parse(Image(14, bear, battle)));

        Assert.Equal(2, catalog.Count);
        Assert.True(catalog.TryGet(5, out ShapeshiftFormInfo? parsedBear));
        Assert.Equal(new ShapeshiftFormInfo(5, 0, -1), parsedBear);   // creatureType is signed
        Assert.True(catalog.TryGet(17, out ShapeshiftFormInfo? parsedBattle));
        Assert.Equal(new ShapeshiftFormInfo(17, 1, 0), parsedBattle);
        Assert.False(catalog.TryGet(18, out _));
    }

    [Theory]
    [InlineData(13)]
    [InlineData(15)]
    public void OtherClientLayouts_AreRejected(int fields)
        => Assert.Throws<InvalidDataException>(() => ShapeshiftFormDbcReader.Read(DbcFile.Parse(Image(fields, new uint[fields]))));

    [Fact]
    public void ZeroAndDuplicateIds_AreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ShapeshiftFormDbcReader.Read(DbcFile.Parse(Image(14, new uint[14]))));
        uint[] row = [3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Throws<InvalidDataException>(() => ShapeshiftFormDbcReader.Read(DbcFile.Parse(Image(14, row, row))));
    }

    [Fact]
    public void WarriorStanceSeed_CoversForms17To19WithTheStanceBit()
    {
        ShapeshiftFormCatalog seed = ShapeshiftFormCatalog.WarriorStances;

        Assert.Equal(3, seed.Count);
        foreach (uint form in new uint[] { 17, 18, 19 })
        {
            Assert.True(seed.TryGet(form, out ShapeshiftFormInfo? info));
            Assert.Equal(1u, info!.Flags1);
        }

        Assert.False(seed.TryGet(1, out _));
        Assert.Equal(0, ShapeshiftFormCatalog.Empty.Count);
    }

    [Fact]
    public void Catalog_RejectsDuplicateForms()
        => Assert.Throws<ArgumentException>(() => new ShapeshiftFormCatalog([new ShapeshiftFormInfo(1, 0, 0), new ShapeshiftFormInfo(1, 1, 0)]));

    [Fact]
    public void ADbcRowOverridesTheBuiltInRetailRow_ByIdOnly()
    {
        // A configured DBC replaces the table wholesale (StanceFeature picks one or the other, never a merge).
        // The DBC row 1 deliberately differs from the built-in Cat row in both columns (Stance instead of 0x70, humanoid).
        ShapeshiftFormCatalog fromDbc = ShapeshiftFormDbcReader.Read(DbcFile.Parse(Image(14, [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x1, 0, 0])));

        Assert.Equal(1, fromDbc.Count);
        Assert.True(fromDbc.TryGet(1, out ShapeshiftFormInfo? cat));
        Assert.Equal(new ShapeshiftFormInfo(1, 0x1, 0), cat);
        Assert.False(fromDbc.TryGet(5, out _));   // no merge: the built-in Bear row does not leak in
        Assert.True(ShapeshiftFormCatalog.Retail.TryGet(1, out ShapeshiftFormInfo? retail));
        Assert.Equal(new ShapeshiftFormInfo(1, 0x70, 1), retail);
        Assert.NotEqual(retail, cat);
    }

    [RealDbcFact]
    public void Retail_MatchesTheDevelopersClientDbc_RowForRow()
    {
        // Reports Skipped (with the variable name) when ARCANECORE_TEST_DBC_DIR is unset, never a silent pass.
        string dir = Environment.GetEnvironmentVariable(RealDbcFactAttribute.Variable)!;
        ShapeshiftFormCatalog client = ShapeshiftFormDbcReader.Load(Path.Combine(dir, "SpellShapeshiftForm.dbc"));

        // The file must be the one the client uses (patch.MPQ overrides dbc.MPQ), not the older base-archive copy.
        Assert.NotEqual(0, client.Count);
        Assert.Equal(client.Count, ShapeshiftFormCatalog.Retail.Count);
        string[] mismatches = client.Forms.OrderBy(row => row.Id)
            .Where(row => !ShapeshiftFormCatalog.Retail.TryGet(row.Id, out ShapeshiftFormInfo? ours) || ours != row)
            .Select(row => $"client {row} vs built-in {(ShapeshiftFormCatalog.Retail.TryGet(row.Id, out ShapeshiftFormInfo? ours) ? ours!.ToString() : "missing")}")
            .ToArray();
        Assert.True(mismatches.Length == 0, string.Join(Environment.NewLine, mismatches));
    }

    private static byte[] Image(int fields, params uint[][] records)
    {
        byte[] image = new byte[20 + (records.Length * fields * 4) + 1];
        "WDBC"u8.CopyTo(image);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4), (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(8), (uint)fields);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(12), (uint)fields * 4);
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(16), 1);
        for (int record = 0; record < records.Length; record++)
        {
            for (int field = 0; field < fields; field++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(20 + (((record * fields) + field) * 4)), records[record][field]);
            }
        }

        return image;
    }
}
