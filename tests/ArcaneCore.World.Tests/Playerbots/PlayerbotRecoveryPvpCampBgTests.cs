using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Tests.Playerbots.Combat;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The battleground driver's side of the PvP corpse wait: it hands its recovery the contested-objective hook once (a method group, no
/// allocation per think), and that hook is false outside an Arathi Basin match. The recovery's own decisions (body run, 2v1 reclaim,
/// 3v1 wait, the 30 s cap) are in <see cref="PlayerbotRecoveryPvpCampTests"/>.
/// </summary>
public sealed class PlayerbotRecoveryPvpCampBgTests
{
    [Fact]
    public async Task TheBattlegroundDriver_WiresTheContestedObjectiveHook_AndItIsFalseWithoutAnArathiBasinMatch()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotPvpTestKit.EnterAsync(host, "BGHOOKBOT", "Bghookbot", PlayerbotPvpTestKit.Human);
        try
        {
            var options = new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] };
            var ai = new PlayerbotBattlegroundAI(session, options);
            Func<Player, Vector3, bool>? hook = ai.Recovery.ContestedObjectiveNear;
            Assert.NotNull(hook);
            Assert.Same(hook, ai.Recovery.ContestedObjectiveNear); // one delegate, not a new one per read

            Player player = await host.OnWorldAsync(() => session.Player!);
            Assert.False(await host.OnWorldAsync(() => hook!(player, new Vector3(player.X, player.Y, player.Z)))); // no match played yet
        }
        finally
        {
            session.Kick();
            await session.ManagedClosed;
        }
    }
}
