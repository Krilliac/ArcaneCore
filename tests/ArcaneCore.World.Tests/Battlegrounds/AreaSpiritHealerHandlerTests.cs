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
}
