using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Protocol;
using ArcaneCore.World.Features;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Net;
using Microsoft.Extensions.DependencyInjection;
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

    [Theory]
    [InlineData(true, true, PlayerbotSpiritHealerStep.DetourToHealer)]
    [InlineData(true, false, PlayerbotSpiritHealerStep.DetourToHealer)]
    [InlineData(false, true, PlayerbotSpiritHealerStep.ResumeCorpseRun)]
    [InlineData(false, false, PlayerbotSpiritHealerStep.Fault)]
    internal void DecideSpiritHealer_WhenGraveyardWalkStalls_TriesDetourBeforeCorpseOrFault(
        bool detourLeft, bool bodyRunLeft, PlayerbotSpiritHealerStep expected)
        => Assert.Equal(expected, PlayerbotRecovery.DecideSpiritHealer(
            healerKnown: false, healerInReach: false, graveyardKnown: true, stalled: true,
            bodyRunLeft: bodyRunLeft, detourLeft: detourLeft));

    /// <summary>
    /// Live w46 (2026-10-14, Maesgrer in Mulgore at (-3066.7, 10.5, 84.0) map 1): the ghost took the spirit healer instead of an unsafe
    /// body, stalled walking to graveyard 34 (205 yd away, healer out of sight), resumed the corpse run which also stalled in the same
    /// mountain pocket, and then stalled again on the spirit-healer fallback. Instead of faulting immediately with
    /// <see cref="PlayerbotRecovery.SpiritHealerFailed"/> at the trapped coordinates, recovery relocates the ghost once to the graveyard
    /// safe loc, and if the spirit healer is 12.13 yd away (beyond <see cref="PlayerbotRecovery.ServerReachProbeYards"/>) and walking
    /// those final yards also stalls, relocates the ghost directly onto the spirit healer before ever faulting.
    /// </summary>
    [Fact]
    public async Task TrappedGhost_RelocatesToGraveyardAndSpiritHealer_BeforeFaulting()
    {
        await using WorldTestHost host = WorldTestHost.Start(
            configureServices: services => services.AddSingleton<IWorldFeature, ManualClockFeature>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            WorldSafeLoc? graveyard = null;
            Creature? healer = null;
            PlayerbotRecovery? recovery = null;
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor(), pathfinder: new BlockedPathfinder());
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                player.Combat.Corpse!.SetPosition(player.X + 250f, player.Y - 700f, player.Z - 75f, 0);
                graveyard = new WorldSafeLoc(34, player.MapId, player.X + 122f, player.Y - 163f, player.Z - 18f, 0f, "Mulgore");
                recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 200, AllowedMaps = [0, 1] })
                {
                    GraveyardLookup = (_, _) => graveyard,
                };
                recovery.TakeSpiritHealer();
                return true;
            });

            bool resumedCorpseRun = false;
            bool relocatedToGraveyard = false;
            for (int think = 0; think < 200 && !relocatedToGraveyard; think++)
            {
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery!.Update(player, 500);
                    resumedCorpseRun |= recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.ResumeCorpseRun;
                    relocatedToGraveyard = recovery.GraveyardRelocated;
                    return true;
                });
                if (!relocatedToGraveyard)
                    await host.World.AdvanceClockAsync(2_500);
            }

            Assert.True(resumedCorpseRun, "expected the ghost to try resuming the corpse run before relocating");
            Assert.True(relocatedToGraveyard, "expected the trapped ghost to relocate to the graveyard safe loc");
            await host.OnWorldAsync(() =>
            {
                Assert.True(Vector3.Distance(new(player.X, player.Y, player.Z), new(graveyard!.X, graveyard.Y, graveyard.Z)) < 0.5f);
                Assert.False(recovery!.HealerRelocated);
                // Spawn a spirit healer 12.13 yards from the graveyard safe loc (as at Mulgore graveyard 34), visible to the ghost.
                healer = Dungeon.DungeonBotHost.AddCreature(
                    player.Map!, 40565u, graveyard.X - 4.44f, graveyard.Y + 11.21f, graveyard.Z + 1.30f,
                    (uint)NpcFlags.SpiritHealer, host.World.NowMs, faction: 1);
                player.VisibleObjects.Add(healer.Guid);
                return true;
            });

            // Next tick after graveyard relocation: progress was reset so stalled is false and the ghost walks toward the healer.
            await host.World.AdvanceClockAsync(500);
            await host.OnWorldAsync(() =>
            {
                session.ManagedBudget = new ManagedActionBudget(4);
                recovery!.Update(player, 500);
                Assert.Equal(PlayerbotSpiritHealerStep.WalkToHealer, recovery.LastSpiritHealerStep);
                Assert.False(recovery.HealerRelocated);
                return true;
            });

            // When walking the final 12.13 yards to the healer also stalls, the ghost relocates directly onto the healer.
            bool relocatedToHealer = false;
            for (int think = 0; think < 100 && !relocatedToHealer; think++)
            {
                await host.World.AdvanceClockAsync(2_500);
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery!.Update(player, 500);
                    relocatedToHealer = recovery.HealerRelocated;
                    return true;
                });
            }

            Assert.True(relocatedToHealer, "expected the ghost to relocate onto the spirit healer after the 12-yard walk stalled");
            await host.OnWorldAsync(() =>
            {
                Assert.True(Vector3.Distance(new(player.X, player.Y, player.Z), new(healer!.X, healer.Y, healer.Z))
                    <= PlayerbotRecovery.SpiritHealerReachYards);
                // Remove the healer so activation cannot revive the ghost: a further stall must still fault rather than loop forever.
                player.VisibleObjects.Remove(healer.Guid);
                return true;
            });

            InvalidOperationException? fault = null;
            for (int think = 0; think < 100 && fault is null; think++)
            {
                await host.World.AdvanceClockAsync(2_500);
                fault = await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    try
                    {
                        recovery!.Update(player, 500);
                        return null;
                    }
                    catch (InvalidOperationException ex)
                    {
                        return ex;
                    }
                });
            }

            Assert.NotNull(fault);
            Assert.Equal(PlayerbotRecovery.SpiritHealerFailed, fault!.Message);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }

    private sealed class ManualClockFeature : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    private sealed class BlockedPathfinder : IPathfinder
    {
        public bool Enabled => true;
        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null) => PathResult.None(start);
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
