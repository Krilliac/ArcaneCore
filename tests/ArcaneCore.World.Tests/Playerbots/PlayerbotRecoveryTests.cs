using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Protocol;
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
                Assert.Equal(before, MathF.Abs(player.X - corpse.X));
                Assert.True(player.Movement.HasFlag(MovementFlags.Forward));
                PlayerbotMotion.ElapseForTests(player, 1000);
                session.ManagedBudget = new ManagedActionBudget(4);
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
                // Within the delay the bot waits, as the client's Resurrect button does (SMSG_CORPSE_RECLAIM_DELAY): nothing is sent.
                Assert.False(recovery.Update(player, 1000));
                Assert.Equal(2, session.ManagedBudget!.Remaining);
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

    /// <summary>
    /// The live fault of Dawnrover and Ironwander (2026-10-07): a ghost standing at its body after a second death within five minutes
    /// waits 60 s (vmangos GetCorpseReclaimDelay). At the live think interval of 100 ms the old per-think attempt cap (360) ran out after
    /// 36 s and threw "playerbot-recovery-stalled", and the fault disabled the bot. Waiting out the delay is not a stall.
    /// </summary>
    [Fact]
    public async Task GhostAtCorpse_WaitsOutAScaledReclaimDelay_AtAShortThinkInterval_ThenReclaims()
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
                MapCombat combat = player.Map!.Combat;
                player.Health = 0;
                combat.KillPlayer(player);
                Assert.True(combat.RepopPlayer(player));
                clock.Seconds += 31;
                Assert.True(combat.TryReclaimCorpse(player));
                clock.Seconds += 5;
                player.Health = 0;
                combat.KillPlayer(player); // the second death in five minutes doubles the delay
                Assert.True(combat.RepopPlayer(player));
                uint delay = combat.GetCorpseReclaimDelay(player, pvp: false);
                Assert.True(delay >= 60, $"delay {delay}");

                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 100, AllowedMaps = [0, 1] });
                int thinks = 0;
                int readyAt = -1;
                while (!player.IsAlive && thinks < (int)(delay + 10) * 10)
                {
                    if (readyAt < 0 && combat.CorpseReclaimWaitSeconds(player) == 0) readyAt = thinks;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery.Update(player, 100);
                    if (++thinks % 10 == 0) clock.Seconds++;
                }

                // vmangos recomputes the delay from the recent deaths left at each try, so the wait ends at 60 s here, well past the
                // 36 s (360 thinks) where the old cap gave up; the bot reclaims on the first think the server allows it.
                Assert.True(player.IsAlive, $"still a ghost after {thinks} thinks");
                Assert.True(readyAt > 360, $"ready at think {readyAt}");
                Assert.Equal(readyAt + 1, thinks);
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
