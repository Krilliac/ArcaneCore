using System.Numerics;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The outcome checks of <see cref="PlayerbotBrain"/> (think clock, overrun, stall, death loop) and <see cref="PlayerbotRecovery"/>
/// (teleports, loop, spirit-healer failure): each fires on its failing scenario and stays silent on the correct one.
/// </summary>
public sealed class PlayerbotBrainAnomalyTests
{
    private static string BrainKey(string code) => PlayerbotAnomalies.Key("brain", code);

    private static string RecoveryKey(string code) => PlayerbotAnomalies.Key("recovery", code);

    [Fact]
    public async Task ABotThatDoesNotThinkForThreeMinutes_IsReportedOnce_AndAThinkingBotIsNot()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.CheckThinkClock(player, 1000);
                brain.CheckThinkClock(player, 1000 + PlayerbotBrain.ThinkStarvedMs - 1);
                Assert.False(cap.Any(BrainKey("think-starved")));
                brain.CheckThinkClock(player, 1000 + PlayerbotBrain.ThinkStarvedMs + 1);
                brain.CheckThinkClock(player, 1000 + PlayerbotBrain.ThinkStarvedMs + 5000);
                return true;
            });
            Assert.Equal(1, cap.Count(BrainKey("think-starved")));
            Assert.Equal(AnomalySeverity.Warning, cap.Notices.Single(n => n.Key == BrainKey("think-starved")).Severity);

            // A bot that really thinks, for 200 s of world time, is never starved.
            var thinker = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
            using AnomalyCapture thinking = H1AnomalyKit.CaptureFor(session.Player!);
            for (int second = 0; second < 200; second++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); thinker.Update(1000); return true; });
                await host.World.AdvanceClockAsync(1000);
            }

            thinker.Stop();
            Assert.False(thinking.Any(BrainKey("think-starved")), thinking.Describe());
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ABotThatAGroupOrBattlegroundDroveForMinutes_IsNotStarved_WhenTheBrainTakesItBack()
    {
        // Live 2026-10-17: a bot queued for Warsong Gulch, was driven through the match and handed back: the brain's first think reported
        // "no think for 929 s" (bot-brain/think-starved) because its clock stayed armed across the hand-off.
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.CheckThinkClock(player, 1000);                 // the brain thought, the clock is armed
                brain.ResumeAfterGroup();                             // the AI that drove it for 15 minutes lets it go
                brain.CheckThinkClock(player, 1000 + 900_000);        // its first think back
                brain.CheckThinkClock(player, 1000 + 900_000 + PlayerbotBrain.ThinkStarvedMs - 1);
                return true;
            });
            Assert.False(cap.Any(BrainKey("think-starved")), cap.Describe());

            // The clock is armed again by the first think back: a brain that then goes silent is still reported.
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.CheckThinkClock(player, 2_000_000);
                brain.CheckThinkClock(player, 2_000_000 + PlayerbotBrain.ThinkStarvedMs + 1);
                return true;
            });
            Assert.Equal(1, cap.Count(BrainKey("think-starved")));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AThinkThatTakesHalfASecond_IsReportedOverran_AndAFastOneIsNot()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                brain.CheckThinkOverran(player, 40, 1000);
                brain.CheckThinkOverran(player, PlayerbotBrain.ThinkOverranMs - 1, 1000);
                Assert.False(cap.Any(BrainKey("think-overran")));
                brain.CheckThinkOverran(player, 900, 2000);
                brain.CheckThinkOverran(player, 900, 3000); // same episode
                return true;
            });
            Assert.Equal(1, cap.Count(BrainKey("think-overran")));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AThinkLongOnlyForACollectorPause_IsNotReportedOverran_BotQa1483()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true });
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                // 504 ms of which the collector held the process 480: 24 ms of the bot's own work.
                brain.CheckThinkOverran(player, 504, 1000, gcPauseMs: 480);
                brain.CheckThinkOverran(player, 900, 2000, gcPauseMs: 900);
                Assert.False(cap.Any(BrainKey("think-overran")));
                // Control: a pause that leaves the bot's own time over the line still reports.
                brain.CheckThinkOverran(player, 900, 3000, gcPauseMs: 100);
                return true;
            });
            Assert.Equal(1, cap.Count(BrainKey("think-overran")));
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AStall_IsReported_AndWhenGivingTheGoalUpDoesNotFreeTheBot_ItIsReportedUnresolved()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, StallSeconds = 10 });
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            // No terrain: every route is refused and nothing changes.
            for (int think = 0; think < 80; think++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return true; });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.False(cap.Any(BrainKey("stall")), "8 s: not stalled yet\n" + cap.Describe());
            for (int think = 0; think < 270; think++)
            {
                await host.OnWorldAsync(() => { session.ManagedBudget = new ManagedActionBudget(4); brain.Update(100); return true; });
                await host.World.AdvanceClockAsync(100);
            }

            Assert.Equal(1, cap.Count(BrainKey("stall")));
            Assert.True(cap.Any(BrainKey("stall-unresolved")), cap.Describe());
            Assert.True(cap.Any(BrainKey("dead-end-pin")), cap.Describe());
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ABotKilledThreeTimesInOnePlace_ReportsADeathLoop_OnTheThirdDeathOnly()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            for (int death = 1; death <= 3; death++)
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(500);
                    player.Health = 0;
                    player.Map!.Combat.KillPlayer(player);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(500); // the death is recorded
                    if (death < 3) player.Map!.Combat.ResurrectPlayer(player, 1f, applySickness: false);
                    return true;
                });
                Assert.Equal(death == 3 ? 1 : 0, cap.Count(RecoveryKey("death-loop")));
                await host.World.AdvanceClockAsync(1000);
            }
        }
        finally { brain.Stop(); session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task ARecoveryThatRelocatesThreeTimes_IsALoop_AndRelocationsFarApartAreNot()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true });
                recovery.NoteRelocation(player, 1_000, "first");
                recovery.NoteRelocation(player, 60_000, "second");
                Assert.Equal(2, cap.Count(RecoveryKey("teleport-used")));
                Assert.False(cap.Any(RecoveryKey("loop")));
                recovery.NoteRelocation(player, 120_000, "third");
                Assert.Equal(1, cap.Count(RecoveryKey("loop")));
                var spaced = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true });
                spaced.NoteRelocation(player, 1_000, "a");
                spaced.NoteRelocation(player, 1_000 + PlayerbotRecovery.RelocationLoopWindowMs + 1, "b");
                spaced.NoteRelocation(player, 1_000 + (2 * (PlayerbotRecovery.RelocationLoopWindowMs + 1)), "c");
                Assert.Equal(1, cap.Count(RecoveryKey("loop")));
                return true;
            });
            Assert.Equal(AnomalySeverity.Info, cap.Notices.First(n => n.Key == RecoveryKey("teleport-used")).Severity);
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    /// <summary>
    /// The trapped-ghost scenario of PlayerbotRecoveryTests: the recovery relocates the ghost to the graveyard and onto the healer
    /// (teleports used, Info), then the spirit-healer fallback fails when the healer is gone (reported once; nothing is thrown).
    /// </summary>
    [Fact]
    public async Task ATrappedGhost_ReportsItsRelocations_AndTheFailureOfTheSpiritHealerFallback()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            Player player = session.Player!;
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(player);
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

            Assert.False(cap.Any(RecoveryKey("teleport-used")));
            bool relocated = false;
            for (int think = 0; think < 200 && !relocated; think++)
            {
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery!.Update(player, 500);
                    relocated = recovery.GraveyardRelocated;
                    return true;
                });
                if (!relocated) await host.World.AdvanceClockAsync(2_500);
            }

            Assert.True(relocated);
            Assert.Equal(1, cap.Count(RecoveryKey("teleport-used")));
            await host.OnWorldAsync(() =>
            {
                healer = Dungeon.DungeonBotHost.AddCreature(player.Map!, 40565u, graveyard!.X - 4.44f, graveyard.Y + 11.21f, graveyard.Z + 1.30f,
                    (uint)NpcFlags.SpiritHealer, host.World.NowMs, faction: 1);
                player.VisibleObjects.Add(healer.Guid);
                return true;
            });
            await host.World.AdvanceClockAsync(500);
            bool onHealer = false;
            for (int think = 0; think < 100 && !onHealer; think++)
            {
                await host.World.AdvanceClockAsync(2_500);
                await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    recovery!.Update(player, 500);
                    onHealer = recovery.HealerRelocated;
                    return true;
                });
            }

            Assert.True(onHealer);
            Assert.Equal(2, cap.Count(RecoveryKey("teleport-used")));
            await host.OnWorldAsync(() => { player.VisibleObjects.Remove(healer!.Guid); return true; });
            bool threw = false;
            for (int think = 0; think < 100 && !threw; think++)
            {
                await host.World.AdvanceClockAsync(2_500);
                threw = await host.OnWorldAsync(() =>
                {
                    session.ManagedBudget = new ManagedActionBudget(4);
                    try { recovery!.Update(player, 500); return false; }
                    catch (InvalidOperationException) { return true; }
                });
            }

            // Reported once, never thrown (a thrown fault quarantined the bot and then disabled it).
            Assert.False(threw);
            Assert.Equal(1, cap.Count(RecoveryKey("spirit-healer-failed")));
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task AnOrdinaryGhostWalk_ReportsNoRecoveryAnomaly()
    {
        await using WorldTestHost host = WorldTestHost.Start(configureServices: s => s.AddSingleton<IWorldFeature, H1AnomalyKit.ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            using AnomalyCapture cap = H1AnomalyKit.CaptureFor(session.Player!);
            await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(lineOfSight: new FlatFloor());
                Player player = session.Player!;
                player.Health = 0;
                player.Map!.Combat.KillPlayer(player);
                Assert.True(player.Map!.Combat.RepopPlayer(player));
                player.Combat.Corpse!.SetPosition(player.X + 50, player.Y, player.Z, 0);
                var recovery = new PlayerbotRecovery(session, new PlayerbotOptions { Enabled = true, MaxRouteYards = 100, AllowedMaps = [0, 1] });
                session.ManagedBudget = new ManagedActionBudget(4);
                recovery.Update(player, 1000);
                return true;
            });
            Assert.Empty(cap.OfSubsystem("bot-recovery"));
        }
        finally { session.Kick(); await session.ManagedClosed; }
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
