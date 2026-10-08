namespace WowWorldMessages;

public static class UpdateMaskUtils
{
    internal static async Task<IDictionary<ushort, uint>> ReadAsyncValues(Stream stream,
        CancellationToken cancellationToken)
    {
        var amountOfBlocks = await stream.ReadByte(cancellationToken).ConfigureAwait(false);

        var blocks = new uint[amountOfBlocks];
        for (var i = 0; i < amountOfBlocks; i++)
        {
            blocks[i] = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
        }

        var values = new Dictionary<ushort, uint>();
        foreach (var (index, block) in blocks.Select((v, i) => (i, v)))
        {
            for (var bit = 0; bit < 32; bit++)
            {
                if ((block & (1 << bit)) != 0)
                {
                    var value = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
                    var key = index * 32 + bit;
                    values[(ushort)key] = value;
                }
            }
        }

        return values;
    }

    internal static async Task WriteAsync(Stream stream, CancellationToken cancellationToken, IDictionary<ushort, uint> values)
    {
        var highestKey = values.Keys.Max();
        var amountOfBlocks = highestKey / 32;
        if (amountOfBlocks < 32 || amountOfBlocks % 32 != 0)
        {
            amountOfBlocks += 1;
        }

        await stream.WriteByte((byte)amountOfBlocks, cancellationToken).ConfigureAwait(false);

        var blocks = new uint[amountOfBlocks];
        foreach (var (key, value) in values)
        {
            var block = key / 32;
            var index = key % 32;
            blocks[block] |= (uint)(1 << index);
        }

        foreach (var block in blocks)
        {
            await stream.WriteUInt(block, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (_, value) in values)
        {
            await stream.WriteUInt(value, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static int Length(IDictionary<ushort, uint> values)
    {
        var highestKey = values.Keys.Max();
        var amountOfBlocks = highestKey / 32;
        var extra = 0;
        if (highestKey % 32 != 0)
        {
            extra = 1;
        }

        return 1 + (extra + amountOfBlocks + values.Count) * 4;
    }

    internal static void AddUInt(IDictionary<ushort, uint> values, ushort offset, uint value)
    {
        values[offset] = value;
    }

    internal static uint? GetUInt(IDictionary<ushort, uint> values, ushort offset)
    {
        if (values.TryGetValue(offset, out var value))
        {
            return value;
        }

        return null;
    }

    internal static void AddFloat(IDictionary<ushort, uint> values, ushort offset, float value)
    {
        AddUInt(values, offset, BitConverter.ToUInt32(BitConverter.GetBytes(value)));
    }

    internal static float? GetFloat(IDictionary<ushort, uint> values, ushort offset)
    {
        if (GetUInt(values, offset) is { } val)
        {
            return BitConverter.ToSingle(BitConverter.GetBytes(val));
        }

        return null;
    }

    internal static void AddGuid(IDictionary<ushort, uint> values, ushort offset, ulong value)
    {
        AddUInt(values, offset, value.LowerUInt());
        offset += 1;
        AddUInt(values, offset, value.UpperUInt());
    }

    internal static ulong? GetGuid(IDictionary<ushort, uint> values, ushort offset)
    {
        var lower = GetUInt(values, offset);
        offset += 1;
        var upper = GetUInt(values, offset);

        if ((lower, upper) is (null, null))
        {
            return null;
        }

        return ReadUtils.ULongFromUInt(lower ?? 0, upper ?? 0);
    }

    internal static void AddTwoShort(IDictionary<ushort, uint> values, ushort offset, ushort first, ushort second)
    {
        AddUInt(values, offset, ReadUtils.UIntFromUShort(first, second));
    }

    internal static (ushort, ushort)? GetTwoShort(IDictionary<ushort, uint> values, ushort offset)
    {
        if (GetUInt(values, offset) is { } value)
        {
            return ReadUtils.UShortFromUInt(value);
        }

        return null;
    }

    internal static void AddBytes(IDictionary<ushort, uint> values, ushort offset, byte first, byte second, byte third,
        byte fourth)
    {
        AddUInt(values, offset, ReadUtils.UIntFromBytes(first, second, third, fourth));
    }

    internal static (byte, byte, byte, byte)? GetBytes(IDictionary<ushort, uint> values, ushort offset)
    {
        if (GetUInt(values, offset) is { } value)
        {
            return ReadUtils.BytesFromUInt(value);
        }

        return null;
    }
}