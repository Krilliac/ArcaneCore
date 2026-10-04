using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Honor;
using ArcaneCore.Kernel.Honor;
using Xunit;

namespace ArcaneCore.Game.Tests.Honor;

/// <summary>
/// SMSG_PVP_CREDIT and MSG_INSPECT_HONOR_STATS layouts (wow_messages smsg_pvp_credit.wowm,
/// msg_inspect_honor_stats_server.wowm 1.12; vmangos Server/Packets/Misc.cpp:350-401).
/// </summary>
public sealed class HonorPacketsTests
{
    [Fact]
    public void Pvp_credit_is_honor_victim_guid_rank_little_endian()
    {
        byte[] p = HonorPackets.PvpCredit(-100, 0x0102030405060708UL, 19);
        Assert.Equal(16, p.Length);
        Assert.Equal(-100, BinaryPrimitives.ReadInt32LittleEndian(p));
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64LittleEndian(p.AsSpan(4)));
        Assert.Equal(19u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(12)));
    }

    [Fact]
    public void Inspect_honor_stats_is_the_fifty_byte_layout_read_from_the_targets_fields()
    {
        var service = new HonorService(new HonorOptions(), new FixedHonorClock(20_000), () => 19_997);
        Player target = TestWorld.CreatePlayer(5, 0, 0, new FakeSession());
        var rows = new List<HonorCpRecord>
        {
            new(4, 10, 100f, 20_000, 1), new(3, 11, 20f, 20_000, 2), new(4, 13, 30f, 19_999, 1),
        };
        service.Track(target, service.Create(target, new CharacterHonorData(new CharacterHonorState(5500f, 9, 7, 4, 44.5f, 7, 3, 0, false), rows)));

        byte[] p = HonorPackets.InspectHonorStats(target);
        Assert.Equal(50, p.Length);
        Assert.Equal(target.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(p));
        Assert.Equal(9, p[8]);                                                                    // highest rank
        Assert.Equal(1u | (1u << 16), BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(9)));       // today HK | DK << 16
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(13)));                    // yesterday HK
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(15)));
        Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(17)));                    // last week HK
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(19)));
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(21)));                    // this week HK
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(p.AsSpan(23)));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(25)));                   // lifetime HK 7 + 2
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(29)));                   // lifetime DK 3 + 1
        Assert.Equal(30u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(33)));                  // yesterday honor
        Assert.Equal(44u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(37)));                  // last week honor
        Assert.Equal(130u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(41)));                 // this week honor (DK excluded)
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(45)));                   // last week standing
        Assert.Equal(25, p[49]);                                                                   // rank bar
    }
}
