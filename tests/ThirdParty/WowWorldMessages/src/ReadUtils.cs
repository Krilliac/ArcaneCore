using System.Text;
using WowWorldMessages.All;

namespace WowWorldMessages;

internal static class ReadUtils
{
    private const uint AchievementArraySentinelValue = 0xFF_FF_FF_FF;

    internal static async Task<byte> ReadByte(this Stream r,
        CancellationToken cancellationToken
    )
    {
        var b = new byte[1];
        await r.ReadExactlyAsync(b, cancellationToken).ConfigureAwait(false);

        return b[0];
    }

    internal static async Task<ushort> ReadUShort(this Stream r,
        CancellationToken cancellationToken
    )
    {
        var b = new byte[2];
        await r.ReadExactlyAsync(b, cancellationToken).ConfigureAwait(false);

        return (ushort)(b[0] | (b[1] << 8));
    }

    internal static async Task<uint> ReadUInt(this Stream r, CancellationToken cancellationToken)
    {
        var b = new byte[4];
        await r.ReadExactlyAsync(b, cancellationToken).ConfigureAwait(false);

        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    internal static async Task<int> ReadInt(this Stream r, CancellationToken cancellationToken) =>
        BitConverter.ToInt32(BitConverter.GetBytes(await r.ReadUInt(cancellationToken).ConfigureAwait(false)));

    internal static async Task<float> ReadFloat(this Stream r, CancellationToken cancellationToken)
    {
        var val = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        return BitConverter.ToSingle(BitConverter.GetBytes(val));
    }

    internal static async Task<ulong> ReadULong(this Stream r, CancellationToken cancellationToken)
    {
        var b = new byte[8];
        await r.ReadExactlyAsync(b, cancellationToken).ConfigureAwait(false);

        return b[0] | ((ulong)b[1] << 8) | ((ulong)b[2] << 16) | ((ulong)b[3] << 24) | ((ulong)b[4] << 32) |
               ((ulong)b[5] << 40) | ((ulong)b[6] << 48) |
               ((ulong)b[7] << 56);
    }

    internal static async Task<ulong> ReadU48(this Stream r, CancellationToken cancellationToken)
    {
        var b = new byte[6];
        await r.ReadExactlyAsync(b, cancellationToken).ConfigureAwait(false);

        return b[0] | ((ulong)b[1] << 8) | ((ulong)b[2] << 16) | ((ulong)b[3] << 24) | ((ulong)b[4] << 32) |
               ((ulong)b[5] << 40);
    }

    internal static async Task<bool> ReadBool8(this Stream r, CancellationToken cancellationToken) =>
        await ReadByte(r, cancellationToken).ConfigureAwait(false) switch
        {
            1 => true,
            0 => false,
            _ => throw new ArgumentOutOfRangeException()
        };

    internal static async Task<bool> ReadBool16(this Stream r, CancellationToken cancellationToken) =>
        await ReadUShort(r, cancellationToken).ConfigureAwait(false) switch
        {
            1 => true,
            0 => false,
            _ => throw new ArgumentOutOfRangeException()
        };

    internal static async Task<bool> ReadBool32(this Stream r, CancellationToken cancellationToken) =>
        await ReadUInt(r, cancellationToken).ConfigureAwait(false) switch
        {
            1 => true,
            0 => false,
            _ => throw new ArgumentOutOfRangeException()
        };

    internal static async Task<string> ReadCString(this Stream r, CancellationToken cancellationToken)
    {
        var s = new StringBuilder();
        var b = await ReadByte(r, cancellationToken).ConfigureAwait(false);

        while (b != 0)
        {
            s.Append((char)b);
            b = await ReadByte(r, cancellationToken).ConfigureAwait(false);
        }

        return s.ToString();
    }


    internal static async Task<string> ReadSizedCString(this Stream r, CancellationToken cancellationToken)
    {
        var length = await r.ReadUInt(cancellationToken).ConfigureAwait(false);
        var array = new byte[length - 1];
        await r.ReadExactlyAsync(array, cancellationToken).ConfigureAwait(false);

        // Null terminator
        _ = await r.ReadByte(cancellationToken).ConfigureAwait(false);

        return Encoding.UTF8.GetString(array);
    }

    internal static async Task<ulong> ReadPackedGuid(this Stream r, CancellationToken cancellationToken)
    {
        var header = await r.ReadByte(cancellationToken).ConfigureAwait(false);
        ulong value = 0;
        for (var i = 0; i < 8; i++)
        {
            if ((header & (1 << i)) != 0)
            {
                var b = await r.ReadByte(cancellationToken).ConfigureAwait(false);
                value |= (ulong)b << (i * 8);
            }
        }

        return value;
    }

    internal static ulong ULongFromUInt(uint lower, uint upper) => ((ulong)upper << 32) | lower;

    internal static uint UIntFromUShort(ushort lower, ushort upper) => ((uint)upper << 16) | lower;

    internal static (ushort, ushort) UShortFromUInt(uint value)
    {
        var lower = (ushort)value;
        var upper = (ushort)(value >> 16);

        return (lower, upper);
    }

    internal static (byte, byte, byte, byte) BytesFromUInt(uint value)
    {
        var bytes = BitConverter.GetBytes(value);

        return (bytes[0], bytes[1], bytes[2], bytes[3]);
    }

    internal static uint UIntFromBytes(byte first, byte second, byte third, byte fourth) =>
        BitConverter.ToUInt32([first, second, third, fourth]);

    internal static int MonsterMoveSplineLength(IList<Vector3d> splines)
    {
        if (splines.Count == 0)
        {
            return 4;
        }

        if (splines.Count == 1)
        {
            return 4 + 12;
        }

        return 4 + 12 + (splines.Count - 1) * 4;
    }

    internal static async Task<List<Vector3d>> ReadMonsterMoveSpline(Stream stream,
        CancellationToken cancellationToken)
    {
        var amountOfSplines = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

        var splines = new List<Vector3d>();
        for (var i = 0; i < amountOfSplines; i++)
        {
            if (i == 0)
            {
                splines.Add(await Vector3d.ReadBodyAsync(stream, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                var packed = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
                splines.Add(Vector3dFromPacked(packed));
            }
        }

        return splines;
    }

    private static Vector3d Vector3dFromPacked(uint p)
    {
        // ReSharper disable once PossibleLossOfFraction
        var x = (float)((p & 0x7FF) / 4);
        // ReSharper disable once PossibleLossOfFraction
        var y = (float)(((p >> 11) & 0x7FF) / 4);
        // ReSharper disable once PossibleLossOfFraction
        var z = (float)(((p >> 22) & 0x3FF) / 4);

        return new Vector3d { X = x, Y = y, Z = z };
    }

    private static uint PackedFromVector3d(Vector3d v)
    {
        uint packed = 0;
        packed |= (uint)(v.X / 0.25f) & 0x7FF;
        packed |= ((uint)(v.Y / 0.25f) & 0x7FF) << 11;
        packed |= ((uint)(v.Z / 0.25f) & 0x3FF) << 22;
        return packed;
    }

    internal static async Task WriteMonsterMoveSpline(Stream stream, IList<Vector3d> splines,
        CancellationToken cancellationToken)
    {
        await stream.WriteUInt((uint)splines.Count, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < splines.Count; i++)
        {
            if (i == 0)
            {
                await splines[i].WriteBodyAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await stream.WriteUInt(PackedFromVector3d(splines[i]), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Wrath-only helpers omitted from the vanilla test build.
#if false
    internal static async Task WriteAchievementDoneArray(IList<AchievementDone> array, Stream w,
        CancellationToken cancellationToken)
    {
        foreach (var a in array)
        {
            await a.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt(AchievementArraySentinelValue, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteAchievementInProgressArray(IList<AchievementInProgress> array, Stream w,
        CancellationToken cancellationToken)
    {
        foreach (var a in array)
        {
            await a.WriteBodyAsync(w, cancellationToken).ConfigureAwait(false);
        }

        await w.WriteUInt(AchievementArraySentinelValue, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<List<AchievementDone>> ReadAchievementDoneArray(Stream stream,
        CancellationToken cancellationToken)
    {
        var first = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

        var array = new List<AchievementDone>();
        while (first != AchievementArraySentinelValue)
        {
            var time = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

            array.Add(new AchievementDone
            {
                Achievement = first,
                Time = time
            });

            first = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
        }

        return array;
    }

    internal static async Task<List<AchievementInProgress>> ReadAchievementInProgressArray(Stream stream,
        CancellationToken cancellationToken)
    {
        var first = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

        var array = new List<AchievementInProgress>();
        while (first != AchievementArraySentinelValue)
        {
            var counter = await stream.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            var player = await stream.ReadPackedGuid(cancellationToken).ConfigureAwait(false);

            var timedCriteriaFailed = await stream.ReadBool32(cancellationToken).ConfigureAwait(false);

            var progressDate = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

            var timeSinceProgress = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

            var timeSinceProgress2 = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);

            array.Add(new AchievementInProgress
            {
                Achievement = first,
                Counter = counter,
                Player = player,
                TimedCriteriaFailed = timedCriteriaFailed,
                ProgressDate = progressDate,
                TimeSinceProgress = timeSinceProgress,
                TimeSinceProgress2 = timeSinceProgress2
            });

            first = await stream.ReadUInt(cancellationToken).ConfigureAwait(false);
        }

        return array;
    }
#endif
}
