using System.Buffers.Binary;
using ArcaneCore.Data.Content.Spells;
using ArcaneCore.Data.Content.Transports;
using ArcaneCore.Kernel.WorldData.Transports;
using Xunit;

namespace ArcaneCore.Data.Tests;

/// <summary>TransportAnimation.dbc of build 5875 (vmangos TransportAnimationfmt "diifffx", TransportMgr::AddPathNodeToTransport).</summary>
public sealed class TransportAnimationDbcTests
{
    private const uint Elevator = 4170;
    private const uint Tram = 176080;

    [Fact]
    public void TheAnimationRows_ReadFromTheDbc_SetTheCycleToTheLastTimeSegment()
    {
        // TransportAnimation.dbc: id, transport entry, time segment, x, y, z, movement id.
        uint[][] rows = [[1, Elevator, 0, 0, 0, 0, 0], [2, Elevator, 8000, 0, 0, 0, 0], [3, Elevator, 4000, 0, 0, Bits(12.5f), 0], [4, Tram, 30000, 0, 0, 0, 0]];
        byte[] data = new byte[20 + (rows.Length * 28) + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)rows.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 28);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1);
        for (int r = 0; r < rows.Length; r++)
        {
            for (int f = 0; f < 7; f++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20 + (r * 28) + (f * 4)), rows[r][f]);
            }
        }

        TransportAnimationCatalog catalog = TransportAnimationDbcReader.Read(DbcFile.Parse(data));

        Assert.Equal(2, catalog.Count);
        Assert.Equal(8000u, catalog.TotalTime(Elevator));
        Assert.Equal(30000u, catalog.TotalTime(Tram));
        Assert.Equal([0u, 4000u, 8000u], catalog.Nodes(Elevator).Select(n => n.TimeSeg));
        Assert.Equal(6.25f, catalog.Offset(Elevator, 2000)!.Value.Z, 0.001f);
        Assert.Null(catalog.Offset(Elevator, 0)); // lower_bound(0) is the first node: no node before it
        Assert.Equal(0u, catalog.TotalTime(1));
    }

    private static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    [Fact]
    public void ADbcWithTheWrongFieldCount_IsRefused()
    {
        byte[] data = new byte[20 + 24 + 1];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x43424457);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 1);
        Assert.Throws<InvalidDataException>(() => TransportAnimationDbcReader.Read(DbcFile.Parse(data)));
    }
}
