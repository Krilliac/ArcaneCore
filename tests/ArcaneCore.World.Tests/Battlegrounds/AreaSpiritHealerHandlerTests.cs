using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using ArcaneCore.World.Spells;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Battlegrounds;

/// <summary>
/// CMSG_AREA_SPIRIT_HEALER_QUERY / _QUEUE (vmangos BattleGroundHandler.cpp:579-612): both need the player in a battleground and a spirit
/// service creature on its map; anything else is ignored.
/// </summary>
public sealed class AreaSpiritHealerHandlerTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// A map id no other test queues for. The anomaly ledger is process-wide and every test host numbers its first character 1, so
    /// subsystem, code and subject cannot tell this test's notices from a concurrent test's; the map id in the detail can.
    /// </summary>
    private const uint UnclaimedMap = 4_242_424u;
    private const string UnclaimedMapDetail = "map 4242424:";

    [Fact]
    public async Task OutsideABattleground_QueryAndQueue_AreIgnored()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("ASHOUT", "Ashout");
        await client.CollectAsync(Quiet);
        byte[] guid = BitConverter.GetBytes(ObjectGuid.Player(1).Value);

        await client.SendAsync(WorldOpcode.CmsgAreaSpiritHealerQuery, guid);
        await client.SendAsync(WorldOpcode.CmsgAreaSpiritHealerQueue, guid);
        await client.SendAsync(WorldOpcode.CmsgAreaSpiritHealerQuery, [1, 2]);

        Assert.DoesNotContain(await client.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgAreaSpiritHealerTime);
        bool waiting = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<SpellFeature>().System
            .HasAura(host.World.FindOnlinePlayer("Ashout")!, BattlegroundConstants.SpellWaitingToResurrect));
        Assert.False(waiting);
    }

    [Fact]
    public async Task BattlefieldJoin_IsRefused_NothingIsQueued()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("ASHJOIN", "Ashjoin");
        await client.CollectAsync(Quiet);

        await client.SendAsync(WorldOpcode.CmsgBattlefieldJoin, BitConverter.GetBytes(489u)); // Warsong Gulch
        await client.SendAsync(WorldOpcode.CmsgBattlefieldJoin, BitConverter.GetBytes(0u));

        Assert.DoesNotContain(await client.CollectAsync(Quiet), p => p.Opcode == WorldOpcode.SmsgBattlefieldStatus);
    }

    [Fact]
    public async Task BattlefieldJoin_Flood_LogsAtMostOneLinePerWindow_AndScoresEveryPacket()
    {
        var logs = new ArcaneCore.World.Tests.Characters.Creation.LogCapture();
        await using WorldTestHost host = WorldTestHost.Start(sessionLogger: logs.CreateLogger("session"));
        await using WorldTestClient client = await host.EnterWorldAsync("ASHFLOOD", "Ashflood");
        await client.CollectAsync(Quiet);
        using ArcaneCore.Kernel.Diagnostics.AnomalyCapture anomalies = ArcaneCore.Kernel.Diagnostics.AnomalyLedger.Shared.Capture(
            n => n.Subsystem == "anticheat" && n.Code == "battlefield-join" && n.Detail.Contains(UnclaimedMapDetail, StringComparison.Ordinal));

        const int Packets = 25;
        for (int i = 0; i < Packets; i++)
        {
            await client.SendAsync(WorldOpcode.CmsgBattlefieldJoin, BitConverter.GetBytes(UnclaimedMap));
        }

        await client.CollectAsync(Quiet);
        await host.OnWorldAsync(() => true);

        Assert.Single(logs.Lines, l => l.Contains("CMSG_BATTLEFIELD_JOIN", StringComparison.Ordinal));
        uint low = await host.OnWorldAsync(() => host.World.FindOnlinePlayer("Ashflood")!.Guid.Low);
        Assert.Equal(Packets, anomalies.Notices.Count);
        Assert.All(anomalies.Notices, n => Assert.Equal($"character {low}", n.Subject)); // the report names the offending character
    }

    [Fact]
    public async Task SpiritGuideGossipTimer_IsWiredOnTheTick()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("ASHGOS", "Ashgos");
        await host.WaitForWorldAsync(() => host.WorldServices.GetRequiredService<ArcaneCore.World.Npc.QuestNpcFeature>().Services.SpiritGuideNextResurrectMs is not null,
            "the spirit guide timer hook");
        uint none = await host.OnWorldAsync(() => host.WorldServices.GetRequiredService<ArcaneCore.World.Npc.QuestNpcFeature>().Services.SpiritGuideNextResurrectMs!(ObjectGuid.Player(99)));
        Assert.Equal(0u, none);
    }
}
