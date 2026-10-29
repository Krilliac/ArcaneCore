using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Net;

/// <summary>The read-trace test seam of <see cref="PacketReader"/>: one event per primitive, nothing outside a scope.</summary>
public sealed class PacketReadTraceTests
{
    [Fact]
    public void Reads_AreRecorded_WithKindOffsetWidthAndOutcome()
    {
        byte[] data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30];
        var events = new List<PacketReadEvent>();
        using (PacketReadTrace.Begin(events))
        {
            var reader = new PacketReader(data);
            reader.TryReadByte(out _);
            reader.TryReadUInt16(out _);
            reader.TryReadUInt32(out _);
            reader.TryReadInt32(out _);
            reader.TryReadUInt64(out _);
            reader.TrySkip(2);
            reader.TryReadBytes(3, out _);
            reader.TryReadByteCount(255, out _);
            reader.TryReadPackedGuid(out _);
            reader.ReadToEnd();
        }

        Assert.Equal(
            [
                new PacketReadEvent(PacketReadKind.Byte, 0, 1, true),
                new PacketReadEvent(PacketReadKind.UInt16, 1, 2, true),
                new PacketReadEvent(PacketReadKind.UInt32, 3, 4, true),
                new PacketReadEvent(PacketReadKind.UInt32, 7, 4, true), // TryReadInt32 records once, as UInt32
                new PacketReadEvent(PacketReadKind.UInt64, 11, 8, true),
                new PacketReadEvent(PacketReadKind.Skip, 19, 2, true),
                new PacketReadEvent(PacketReadKind.Bytes, 21, 3, true),
                new PacketReadEvent(PacketReadKind.ByteCount, 24, 1, true),
                new PacketReadEvent(PacketReadKind.PackedGuid, 25, 1 + 3, true), // mask 26 = 0b11010
                new PacketReadEvent(PacketReadKind.ToEnd, 29, 1, true),
            ],
            events);
    }

    [Fact]
    public void FailedReads_RecordTheBytesNeeded_AndLeaveTheCursor()
    {
        var events = new List<PacketReadEvent>();
        using (PacketReadTrace.Begin(events))
        {
            var reader = new PacketReader([0x07, 1]);
            Assert.False(reader.TryReadUInt32(out _));
            Assert.False(reader.TryReadPackedGuid(out _)); // mask 7 needs 3 more bytes, 1 present
            Assert.False(reader.TryReadCount(5, out _)); // short
            Assert.False(reader.TryReadBytes(40, out _));
            Assert.False(reader.TrySkip(-1));
            Assert.False(reader.TryReadFiniteCoordinates(out _, out _, out _));
            Assert.Equal(0, reader.Position);
        }

        Assert.Equal(
            [
                new PacketReadEvent(PacketReadKind.UInt32, 0, 4, false),
                new PacketReadEvent(PacketReadKind.PackedGuid, 0, 4, false),
                new PacketReadEvent(PacketReadKind.Count, 0, 4, false),
                new PacketReadEvent(PacketReadKind.Bytes, 0, 40, false),
                new PacketReadEvent(PacketReadKind.Skip, 0, -1, false),
                new PacketReadEvent(PacketReadKind.FiniteSingle, 0, 12, false),
            ],
            events);
    }

    [Fact]
    public void StringsFloatsAndCoordinates_AreRecordedPerPrimitive()
    {
        byte[] floats = new byte[12];
        BitConverter.TryWriteBytes(floats.AsSpan(0, 4), 1f);
        BitConverter.TryWriteBytes(floats.AsSpan(4, 4), 2f);
        BitConverter.TryWriteBytes(floats.AsSpan(8, 4), 3f);
        byte[] data = [.. "ab"u8, 0, .. floats, .. "zz"u8];
        var events = new List<PacketReadEvent>();
        using (PacketReadTrace.Begin(events))
        {
            var reader = new PacketReader(data);
            Assert.True(reader.TryReadCStringBytes(10, out _));
            Assert.True(reader.TryReadFiniteCoordinates(out _, out _, out _));
            Assert.False(reader.TryReadCStringBytes(1, out _)); // "zz" is longer than the bound of 1 and has no terminator
            Assert.True(reader.TryReadCString(2, out _));
        }

        Assert.Equal(
            [
                new PacketReadEvent(PacketReadKind.CString, 0, 3, true), // includes the terminator
                new PacketReadEvent(PacketReadKind.FiniteSingle, 3, 4, true),
                new PacketReadEvent(PacketReadKind.FiniteSingle, 7, 4, true),
                new PacketReadEvent(PacketReadKind.FiniteSingle, 11, 4, true),
                new PacketReadEvent(PacketReadKind.CString, 15, 2, false), // found 2 bytes, bound 1
                new PacketReadEvent(PacketReadKind.CString, 15, 2, true), // no terminator: reads to the end
            ],
            events);
    }

    [Fact]
    public void NonFiniteFloat_RecordsAFailedFiniteSingle()
    {
        var events = new List<PacketReadEvent>();
        using (PacketReadTrace.Begin(events))
        {
            var reader = new PacketReader(BitConverter.GetBytes(float.NaN));
            Assert.False(reader.TryReadFiniteSingle(out _));
        }

        Assert.Equal([new PacketReadEvent(PacketReadKind.FiniteSingle, 0, 4, false)], events);
    }

    [Fact]
    public void WithoutAScope_NothingIsRecorded()
    {
        var events = new List<PacketReadEvent>();
        using (PacketReadTrace.Begin(events))
        {
        }

        var reader = new PacketReader([1, 2, 3]);
        reader.TryReadByte(out _);
        reader.ReadToEnd();
        Assert.Empty(events);
    }

    [Fact]
    public void ReadsOnAnotherThread_AreNotRecorded()
    {
        var events = new List<PacketReadEvent>();
        using (PacketReadTrace.Begin(events))
        {
            Assert.True(PacketReadTrace.Active);
            var other = new Thread(() => new PacketReader([1, 2, 3]).TryReadByte(out _));
            other.Start();
            other.Join();
        }

        Assert.Empty(events);
    }

    [Fact]
    public void NestedScopes_RestoreTheOuterSink()
    {
        var outer = new List<PacketReadEvent>();
        var inner = new List<PacketReadEvent>();
        using (PacketReadTrace.Begin(outer))
        {
            new PacketReader([1, 2]).TryReadByte(out _);
            using (PacketReadTrace.Begin(inner))
            {
                new PacketReader([1, 2]).TryReadUInt16(out _);
            }

            new PacketReader([1, 2]).TryReadByte(out _);
        }

        new PacketReader([1, 2]).TryReadByte(out _);

        Assert.Equal([new PacketReadEvent(PacketReadKind.UInt16, 0, 2, true)], inner);
        Assert.Equal([new PacketReadEvent(PacketReadKind.Byte, 0, 1, true), new PacketReadEvent(PacketReadKind.Byte, 0, 1, true)], outer);
    }
}
