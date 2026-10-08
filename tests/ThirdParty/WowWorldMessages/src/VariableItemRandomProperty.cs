namespace WowWorldMessages;

public class VariableItemRandomProperty
{
    public VariableItemRandomProperty()
    {
    }

    public VariableItemRandomProperty(uint itemRandomPropertyId, uint itemSuffixFactor)
    {
        if (itemRandomPropertyId == 0 && itemSuffixFactor != 0)
        {
            throw new ArgumentException(
                $"itemRandomPropertyId can not be 0 while itemSuffixFactor is not zero (instead {itemSuffixFactor})");
        }

        ItemRandomPropertyId = itemRandomPropertyId;
        ItemSuffixFactor = itemSuffixFactor;
    }

    public uint ItemRandomPropertyId { get; }
    public uint ItemSuffixFactor { get; }

    internal static async Task<VariableItemRandomProperty> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var itemRandomPropertyId = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
        if (itemRandomPropertyId == 0)
        {
            return new VariableItemRandomProperty();
        }

        var itemSuffixFactor = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
        return new VariableItemRandomProperty(itemRandomPropertyId, itemSuffixFactor);
    }

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        await stream.WriteUInt(ItemRandomPropertyId, cancellationToken).ConfigureAwait(false);

        if (ItemRandomPropertyId == 0)
        {
            return;
        }

        await stream.WriteUInt(ItemSuffixFactor, cancellationToken).ConfigureAwait(false);
    }

    internal int Length()
    {
        return ItemRandomPropertyId == 0 ? 4 : 8;
    }
}