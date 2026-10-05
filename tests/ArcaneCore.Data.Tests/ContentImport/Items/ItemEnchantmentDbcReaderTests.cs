using System.Buffers.Binary;
using ArcaneCore.Data.Content.Items;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Schema;
using ArcaneCore.Kernel.WorldData.Items;
using Xunit;

namespace ArcaneCore.Data.Tests.ContentImport.Items;

public sealed class ItemEnchantmentDbcReaderTests
{
    [Fact]
    public void Build5875Layout_ReadsTypeAmountAndSpellIdAtDeclaredOffsets()
    {
        byte[] bytes = new byte[20 + (24 * 4)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 49502);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20 + (4 * 4)), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + (10 * 4)), 49501);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + (7 * 4)), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + (21 * 4)), 0xA5A5);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + (22 * 4)), 777);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + (23 * 4)), 0x40);

        IReadOnlyList<ItemEnchantmentDefinition> rows = ItemEnchantmentDbcReader.Read(DbcFile.Parse(bytes));
        Assert.Equal((uint)49502, rows[0].Entry);
        Assert.Equal((uint)1, rows[0].Effects[0].EffectType);
        Assert.Equal((uint)49501, rows[0].Effects[0].SpellId);
        Assert.Equal(100, rows[0].Effects[0].Amount);
        Assert.Equal(uint.MaxValue, rows[0].AmountMax[0]);
        Assert.Equal(0xA5A5u, rows[0].NameFlags);
        Assert.Equal(777u, rows[0].ItemVisualId);
        Assert.Equal(0x40u, rows[0].SlotFlags);
    }

    [Fact]
    public void UnexpectedFieldCountFailsClosed()
    {
        byte[] bytes = new byte[20 + 11 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 11);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 44);
        Assert.Throws<InvalidDataException>(() => ItemEnchantmentDbcReader.Read(DbcFile.Parse(bytes)));
    }

    [Fact]
    public void DuplicateEntryFailsClosed()
    {
        byte[] bytes = new byte[20 + (24 * 4 * 2)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 96);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 9);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20 + 96), 9);
        Assert.Throws<InvalidDataException>(() => ItemEnchantmentDbcReader.Read(DbcFile.Parse(bytes)));
    }

    [Fact]
    public void SyntheticTwoArgumentDefinitionUsesMetadataDefaultsAndClonesImmutably()
    {
        var original = new ItemEnchantmentDefinition(5, [new ItemEnchantmentEffect(1, 2, 3)]);
        ItemEnchantmentDefinition clone = original.WithEffects([new ItemEnchantmentEffect(2, 4, 6)]);
        Assert.Empty(original.AmountMax);
        Assert.Equal(0u, original.NameFlags);
        Assert.Equal((uint)1, original.Effects[0].EffectType);
        Assert.Equal((uint)2, original.Effects[0].SpellId);
        Assert.Equal((uint)4, clone.Effects[0].SpellId);
        Assert.Equal(0u, clone.SlotFlags);
    }
}
