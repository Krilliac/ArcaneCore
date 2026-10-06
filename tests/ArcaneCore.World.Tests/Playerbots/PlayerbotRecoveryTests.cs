using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Net;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

public sealed class PlayerbotRecoveryTests
{
    [Fact]
    public async Task GhostOutsideCorpseRadius_PlansWithoutDirectResurrection()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                Corpse corpse = player.Combat.Corpse!;
                corpse.SetPosition(player.X + 50, player.Y, player.Z, 0);
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 100, AllowedMaps = [0, 1] });
                session.ManagedBudget = new ManagedActionBudget(4);
                float before = MathF.Abs(player.X - corpse.X);
                Assert.True(recovery.Update(player, 1000));
                Assert.True(MathF.Abs(player.X - corpse.X) < before);
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                Assert.NotNull(player.Combat.Corpse);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task GhostAtCorpseAfterDelay_ReclaimsThroughOrdinaryHandler()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var clock = new TestDeathClock(1_000);
            await host.OnWorldAsync(() =>
            {
                DeathHooks.Register(host.World, new DeathHooks(new DeathOptions(), clock));
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                session.ManagedBudget = new ManagedActionBudget(2);
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] });
                Assert.True(recovery.Update(player, 1000)); // Dispatch is admitted; the ordinary delay still refuses resurrection.
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                clock.Seconds += 31;
                Assert.True(recovery.Update(player, 1000));
                Assert.True(player.IsAlive);
                Assert.Null(player.Combat.Corpse);
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task RecoveryRespectsManagedActionBudget()
    {
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                session.ManagedBudget = new ManagedActionBudget(0);
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, AllowedMaps = [0, 1] });
                Assert.False(recovery.Update(player, 1000));
                Assert.True(player.Flags.HasFlag(PlayerFlags.Ghost));
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }

    private sealed class FlatFloor : ILineOfSight
    {
        public bool Enabled => true;
        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;
        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        { hit = to; return false; }
        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;
        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        { info = default; return false; }
    }
}
