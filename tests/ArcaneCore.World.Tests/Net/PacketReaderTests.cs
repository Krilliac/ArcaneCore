using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>
/// Every read of <see cref="PacketReader"/> is bounds-checked: the throwing surface throws exactly
/// <see cref="ArgumentOutOfRangeException"/> (the <see cref="MalformedPacket"/> outcome the sessions
/// catch) and the Try surface returns false with the cursor left in place. Neither surface ever
/// reads past the payload, and the Try surface allocates nothing.
/// </summary>
public sealed class PacketReaderTests
{
    private static ArgumentOutOfRangeException Malformed(Action action)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(action); // exact type, never a derived or different one
        Assert.Equal(MalformedPacket.ParamName, ex.ParamName);
        return ex;
    }

    [Fact]
    public void ReadByte_PastTheEnd_IsTheControlledOutcome_NotAnIndexException()
    {
        // Before this lane ReadByte indexed the span directly and a 0-byte payload threw
        // IndexOutOfRangeException, which the sessions did not catch (and the world thread saw).
        Malformed(() => new PacketReader([]).ReadByte());
        Malformed(() =>
        {
            var reader = new PacketReader([1]);
            reader.ReadByte();
            reader.ReadByte();
        });
    }

    [Fact]
    public void EveryThrowingRead_PastTheEnd_IsTheControlledOutcome()
    {
        byte[] three = [1, 2, 3];
        Malformed(() => new PacketReader(three).ReadUInt32());
        Malformed(() => new PacketReader(three).ReadInt32());
        Malformed(() => new PacketReader(three).ReadSingle());
        Malformed(() => new PacketReader(three).ReadUInt64());
        Malformed(() => new PacketReader([1]).ReadUInt16());
        Malformed(() => new PacketReader(three).ReadBytes(4));
        Malformed(() => new PacketReader(three).ReadBytes(-1));
        Malformed(() => new PacketReader(three).Skip(4));
        Malformed(() => new PacketReader(three).Skip(-1));
        Malformed(() => new PacketReader(three).ReadCount(10));
        Malformed(() => new PacketReader([0xFF, 0, 0, 0]).ReadCount(254));
        Malformed(() => new PacketReader([0x07, 1, 2]).ReadPackedGuid()); // mask promises 3 bytes, 2 present
        Malformed(() => new PacketReader([0xFF]).ReadPackedGuid());      // mask promises 8 bytes, none present
        Malformed(() => new PacketReader([0x41, 0x42, 0x43]).ReadCString(2));
        Malformed(() => new PacketReader([0x41, 0x42, 0x43]).ReadCStringBytes(2));
    }

    [Fact]
    public void PackedGuid_WithEnoughBytes_Reads_AndWithAZeroMaskReadsOneByte()
    {
        var reader = new PacketReader([0x03, 0x34, 0x12, 0x00, 0xAA]);
        Assert.Equal(0x1234UL, reader.ReadPackedGuid());
        Assert.Equal(0UL, reader.ReadPackedGuid());
        Assert.Equal(1, reader.Remaining);
    }

    [Fact]
    public void TryReads_ReturnFalse_AndLeaveTheCursorInPlace()
    {
        var reader = new PacketReader([1, 2, 3]);
        reader.ReadByte();
        int at = reader.Position;

        Assert.False(reader.TryReadUInt32(out _));
        Assert.False(reader.TryReadUInt64(out _));
        Assert.False(reader.TryReadSingle(out _));
        Assert.False(reader.TryReadBytes(3, out _));
        Assert.False(reader.TryReadBytes(-1, out _));
        Assert.False(reader.TrySkip(3));
        Assert.False(reader.TryReadCount(1, out _));
        Assert.False(reader.TryReadCString(1, out string s));
        Assert.Equal(string.Empty, s);
        Assert.False(reader.TryReadCStringBytes(1, out _));
        Assert.Equal(at, reader.Position);

        Assert.True(reader.TryReadUInt16(out ushort u16));
        Assert.Equal(0x0302, u16);
        Assert.False(reader.TryReadByte(out _));
        Assert.Equal(3, reader.Position);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void TryReadPackedGuid_Short_LeavesTheMaskUnread()
    {
        var reader = new PacketReader([0x07, 1, 2]);
        Assert.False(reader.TryReadPackedGuid(out _));
        Assert.Equal(0, reader.Position);
        Assert.Equal(0x07, reader.ReadByte());
    }

    [Fact]
    public void CString_Bounds_TerminatorAndMissingTerminator()
    {
        byte[] data = [0x41, 0x42, 0x00, 0x43, 0x44];
        var reader = new PacketReader(data);
        Assert.Equal("AB", reader.ReadCString());
        Assert.Equal(3, reader.Position);
        Assert.Equal("CD", reader.ReadCString()); // no terminator: reads to the end
        Assert.Equal(0, reader.Remaining);

        var exact = new PacketReader(data);
        Assert.True(exact.TryReadCString(2, out string ab));
        Assert.Equal("AB", ab);
        Assert.False(exact.TryReadCString(1, out _));
        Assert.Equal(3, exact.Position);

        var empty = new PacketReader([0x00, 0x01]);
        Assert.Equal(string.Empty, empty.ReadCString());
        Assert.Equal(1, empty.Position);

        Assert.True(new PacketReader([]).TryReadCStringBytes(0, out ReadOnlySpan<byte> nothing));
        Assert.Equal(0, nothing.Length);
    }

    [Fact]
    public void CString_DefaultBound_IsTheLargestClientFrame()
    {
        byte[] oversized = new byte[PacketReader.MaxCStringBytes + 1];
        Array.Fill(oversized, (byte)0x41);
        Malformed(() => new PacketReader(oversized).ReadCString());

        byte[] atTheBound = new byte[PacketReader.MaxCStringBytes];
        Array.Fill(atTheBound, (byte)0x41);
        Assert.Equal(PacketReader.MaxCStringBytes, new PacketReader(atTheBound).ReadCString().Length);
    }

    [Fact]
    public void InvalidUtf8_BecomesReplacementCharacters_NeverAnException()
    {
        var reader = new PacketReader([0xC3, 0x28, 0xFF, 0x00]);
        string text = reader.ReadCString();
        Assert.Contains('�', text);
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Counts_AreBounded_BeforeAnythingIsAllocated()
    {
        var reader = new PacketReader([5, 0, 0, 0, 7]);
        Assert.Equal(5, reader.ReadCount(5));
        Assert.True(reader.TryReadByteCount(7, out int seven));
        Assert.Equal(7, seven);

        var tooMany = new PacketReader([6, 0, 0, 0, 8]);
        Assert.False(tooMany.TryReadCount(5, out _));
        Assert.Equal(0, tooMany.Position);
        tooMany.Skip(4);
        Assert.False(tooMany.TryReadByteCount(7, out _));
        Assert.Equal(4, tooMany.Position);
        Assert.False(new PacketReader([0xFF, 0xFF, 0xFF, 0xFF]).TryReadCount(int.MaxValue, out _)); // never negative as an int
    }

    [Fact]
    public void TrySurface_DoesNotAllocate()
    {
        byte[] data = new byte[256];
        new Random(3).NextBytes(data);
        data[40] = 0;
        long total = 0;
        for (int round = 0; round < 2; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 20_000; i++)
            {
                var reader = new PacketReader(data);
                reader.TryReadUInt32(out _);
                reader.TryReadUInt64(out _);
                reader.TryReadSingle(out _);
                reader.TryReadPackedGuid(out _);
                reader.TryReadCStringBytes(PacketReader.MaxCStringBytes, out _);
                reader.TryReadBytes(8, out _);
                reader.TryReadCount(1000, out _);
                reader.TryReadUInt16(out _);
                MovementInfo.TryRead(ref reader, out _);
                total += reader.Position;
            }

            if (round == 1)
            {
                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            }
        }

        Assert.True(total > 0);
    }

    [Fact]
    public void MovementInfo_TryRead_FailsWithoutMovingTheCursor_AndMatchesRead()
    {
        var full = new MovementInfo
        {
            Flags = MovementFlags.Forward | MovementFlags.Jumping | MovementFlags.Swimming | MovementFlags.SplineElevation | MovementFlags.OnTransport,
            Time = 5, X = 1, Y = 2, Z = 3, Orientation = 4, TransportGuid = 9, TransportX = 1, Pitch = 0.5f, FallTime = 7,
            JumpZSpeed = 1, JumpCosAngle = 2, JumpSinAngle = 3, JumpXySpeed = 4, SplineElevation = 8,
        };
        var writer = new PacketWriter();
        full.Write(writer);
        byte[] bytes = writer.ToArray();

        var ok = new PacketReader(bytes);
        Assert.True(MovementInfo.TryRead(ref ok, out MovementInfo parsed));
        Assert.Equal(bytes.Length, ok.Position);
        Assert.Equal(full.Flags, parsed.Flags);
        Assert.Equal(full.SplineElevation, parsed.SplineElevation);
        Assert.Equal(full.TransportGuid, parsed.TransportGuid);

        for (int cut = 0; cut < bytes.Length; cut++)
        {
            var truncated = new PacketReader(bytes.AsSpan(0, cut));
            Assert.False(MovementInfo.TryRead(ref truncated, out MovementInfo none));
            Assert.Equal(0, truncated.Position);
            Assert.Equal(default, none);
            byte[] slice = bytes[..cut];
            Malformed(() =>
            {
                var reader = new PacketReader(slice);
                MovementInfo.Read(ref reader);
            });
        }
    }
}
