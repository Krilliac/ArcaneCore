using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.World.Tests.Death;

/// <summary>
/// CMSG_RECLAIM_CORPSE through the real daemon. The body is a u64 corpse GUID. vmangos reads it (<c>recv_data &gt;&gt; guid</c>) before
/// anything else, so a shorter body throws in the read and the packet is dropped without reviving the ghost (bot QA #783/#785).
/// </summary>
public sealed class ReclaimCorpsePacketTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    public async Task AShortReclaimBody_IsDropped_AndAFullOneRevives(int length)
    {
        await using WorldTestHost host = WorldTestHost.Start();
        await using WorldTestClient client = await host.EnterWorldAsync("RECLAIMS" + length, "Reclaimer" + (char)('a' + length));
        string name = "Reclaimer" + (char)('a' + length);
        await host.OnWorldAsync(() =>
        {
            Player player = host.World.FindOnlinePlayer(name)!;
            player.Health = 0;
            player.Map!.Combat.KillPlayer(player);
            Assert.True(player.Map!.Combat.RepopPlayer(player));
            player.Combat.GhostTime -= 3600; // reclaim delay long past; the ghost stands on its corpse
        });
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.Combat.Corpse is not null, "the corpse");

        await client.SendAsync(WorldOpcode.CmsgReclaimCorpse, new byte[length]);
        await client.SendAsync(WorldOpcode.MsgCorpseQuery, []);
        await client.ReadUntilAsync(WorldOpcode.MsgCorpseQuery); // the session has handled the reclaim packet
        Assert.True(await host.OnWorldAsync(() => (host.World.FindOnlinePlayer(name)!.Flags & PlayerFlags.Ghost) != 0));

        await client.SendAsync(WorldOpcode.CmsgReclaimCorpse, new byte[8]);
        await host.WaitForWorldAsync(() => host.World.FindOnlinePlayer(name)!.IsAlive, "the full reclaim to revive");
    }
}
