namespace WowWorldMessages.Wrath;

public class EnchantMask(ushort[] auras)
{
    private const int EnchantArraySize = 16;

    public ushort Enchant(int index) => auras[index];

    public void SetEnchant(int index, ushort value) => auras[index] = value;

    internal static async Task<EnchantMask> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var mask = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auras = new ushort[EnchantArraySize];

        for (var i = 0; i < EnchantArraySize; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                auras[i] = await stream.ReadUShort(cancellationToken).ConfigureAwait(false);
            }
        }

        return new EnchantMask(auras);
    }

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        uint mask = 0;
        for (var i = 0; i < EnchantArraySize; i++)
        {
            if (auras[i] != 0)
            {
                mask |= (uint)(1 << i);
            }
        }

        await stream.WriteUInt(mask, cancellationToken).ConfigureAwait(false);

        foreach (var aura in auras)
        {
            if (aura != 0)
            {
                await stream.WriteUShort(aura, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal int Length()
    {
        var size = 4;

        for (var i = 0; i < EnchantArraySize; i++)
        {
            if (auras[i] != 0)
            {
                size += 2;
            }
        }

        return size;
    }
}