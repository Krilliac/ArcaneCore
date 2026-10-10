using System.Buffers.Binary;
using ArcaneCore.Data.Content.Pets;
using ArcaneCore.Data.Content.Spells;
using Xunit;

namespace ArcaneCore.Data.Tests;

public sealed class CreatureFamilyDbcTests
{
    [Fact]
    public void FoodMasks_AreReadFromTheEighthField_ByFamilyId()
    {
        uint[] wolf = new uint[CreatureFamilyDbcReader.FieldCount];
        wolf[0] = 1;
        wolf[CreatureFamilyDbcReader.PetFoodMaskField] = 0x41; // meat | raw meat
        uint[] boar = new uint[CreatureFamilyDbcReader.FieldCount];
        boar[0] = 5;
        boar[CreatureFamilyDbcReader.PetFoodMaskField] = 0xFF;

        IReadOnlyDictionary<uint, uint> masks = CreatureFamilyDbcReader.ReadFoodMasks(DbcFile.Parse(Image(CreatureFamilyDbcReader.FieldCount, wolf, boar)));

        Assert.Equal(0x41u, masks[1]);
        Assert.Equal(0xFFu, masks[5]);
        Assert.Equal(2, masks.Count);
    }

    [Fact]
    public void WrongLayout_IsRefused()
        => Assert.Throws<InvalidDataException>(() => CreatureFamilyDbcReader.ReadFoodMasks(DbcFile.Parse(Image(17, new uint[17]))));

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
