namespace WowWorldMessages.Wrath;

public class CacheMask(uint?[] auras)
{
    public const int ArraySize = 32;

    public uint? Value(int index) => auras[index];

    public void SetValue(int index, uint value) => auras[index] = value;

    internal static async Task<CacheMask> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var mask = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

        var auras = new uint?[ArraySize];

        for (var i = 0; i < ArraySize; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                auras[i] = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
            }
        }

        return new CacheMask(auras);
    }

    internal async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
    {
        ulong mask = 0;
        for (var i = 0; i < ArraySize; i++)
        {
            if (auras[i] != null)
            {
                mask |= (uint)(1 << i);
            }
        }

        await stream.WriteULong(mask, cancellationToken).ConfigureAwait(false);

        foreach (var val in auras)
        {
            if (val is { } value)
            {
                await stream.WriteUInt(value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal int Length()
    {
        var size = 4;

        for (var i = 0; i < ArraySize; i++)
        {
            if (auras[i] != null)
            {
                size += 4;
            }
        }

        return size;
    }
}