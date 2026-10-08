using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Dungeon;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using Step = ArcaneCore.World.Playerbots.PlayerbotRecoveryStep;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A bot that keeps dying in one place (<see cref="PlayerbotStallWatch.RecordDeath"/>). Rehearsal 2026-10-08: Dawnrover reclaimed
/// its body 36 yards below a Scourge invasion point, chose a Skeletal Soldier as its next fight, was killed by its Scourge Strike
/// within seconds and did it again, three times in five minutes; between deaths it waited out reclaim delays of up to two minutes
/// as a ghost, and the stall watch said <c>stall=none</c> throughout. Replayed from the rehearsal's characters database on the real
/// terrain, the same loop: four deaths in ten minutes, every one to entry 16422.
/// </summary>
public sealed class PlayerbotDeathLoopTests
{
    private static readonly Vector3 Here = new(-9130f, 305f, 95f);

    [Fact]
    public void ThreeDeathsInOnePlace_AreADeathLoop_ReportedUntilTheBotHasLivedAStallBound()
    {
        var watch = new PlayerbotStallWatch();
        Assert.False(watch.RecordDeath(1_000, 0, Here, [16422]).Loop);
        Assert.False(watch.RecordDeath(120_000, 0, Here + new Vector3(3, 2, 0), [16422]).Loop);
        Assert.Null(watch.Report);

        Assert.True(watch.RecordDeath(250_000, 0, Here + new Vector3(-2, 1, 1), [16422]).Loop);
        string report = Assert.IsType<string>(watch.Report);
        Assert.StartsWith("death loop: died 3 times in 249s", report);
        Assert.Contains("attackers=16422", report);
        Assert.Equal(1, watch.Count);
        Assert.Equal(report, watch.LastReport);

        // The brain resets the living watch on every update while the bot is dead: the death loop stays reported.
        watch.Reset();
        Assert.Equal(report, watch.Report);

        // A fourth death is the same loop, not a new stall.
        Assert.True(watch.RecordDeath(380_000, 0, Here, [16422]).Loop);
        Assert.Equal(1, watch.Count);
    }

    [Fact]
    public void DeathsFarApart_OrOutsideTheWindow_AreNoDeathLoop()
    {
        var spread = new PlayerbotStallWatch();
        spread.RecordDeath(1_000, 0, Here, []);
        spread.RecordDeath(2_000, 0, Here + new Vector3(PlayerbotStallWatch.DeathLoopYards + 10f, 0, 0), []);
        Assert.False(spread.RecordDeath(3_000, 0, Here + new Vector3(0, PlayerbotStallWatch.DeathLoopYards + 10f, 0), []).Loop);
        Assert.False(spread.RecordDeath(4_000, 1, Here, []).Loop); // another map
        Assert.Null(spread.Report);

        var slow = new PlayerbotStallWatch();
        slow.RecordDeath(1_000, 0, Here, []);
        slow.RecordDeath(1_000 + PlayerbotStallWatch.DeathLoopWindowMs, 0, Here, []);
        Assert.False(slow.RecordDeath(2_000 + PlayerbotStallWatch.DeathLoopWindowMs, 0, Here, []).Loop); // the first one expired
        Assert.True(slow.RecordDeath(3_000 + PlayerbotStallWatch.DeathLoopWindowMs, 0, Here, []).Loop);
    }

    [Fact]
    public void SettingTrainingAside_LastsTheSuspension()
    {
        var suspensions = new PlayerbotSuspensions();
        Assert.False(suspensions.IsTrainingSuspended(1_000));
        suspensions.SuspendTraining(1_000);
        Assert.True(suspensions.IsTrainingSuspended(1_000 + PlayerbotSuspensions.SuspendMs - 1));
        Assert.False(suspensions.IsTrainingSuspended(1_000 + PlayerbotSuspensions.SuspendMs));
    }

    [Fact]
    public void ACreatureAmongTheAttackersAtTwoDeaths_IsSetAside()
    {
        var watch = new PlayerbotStallWatch();
        Assert.Empty(watch.RecordDeath(1_000, 0, Here, [16422, 94]).SetAside);
        Assert.Equal([16422u], watch.RecordDeath(2_000, 0, Here + new Vector3(200, 0, 0), [16422, 16423]).SetAside);
        Assert.Equal([16423u], watch.RecordDeath(3_000, 0, Here, [16423]).SetAside);
        Assert.Empty(watch.RecordDeath(4_000 + PlayerbotStallWatch.DeathLoopWindowMs, 0, Here, [94]).SetAside); // the first 94 expired
    }

    /// <summary>
    /// The brain, in the world: a creature that attacks the bot and kills it twice is no longer chosen as a fight (before, the bot
    /// picked the nearest attackable creature of its level again, as Dawnrover picked the Skeletal Soldier that kept killing it);
    /// the third death in the place is a death loop, visible in the stall report while the bot is dead, and that death goes to the
    /// spirit healer instead of the body.
    /// </summary>
    [Fact]
    public async Task ABotKilledThreeTimesInOnePlace_SetsItsKillerAside_ReportsADeathLoop_AndTakesTheSpiritHealer()
    {
        const uint Entry = 993901;
        await using WorldTestHost host = WorldTestHost.Start();
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        var brain = new PlayerbotBrain(session, new PlayerbotOptions { Enabled = true, ThinkIntervalMs = 500 });
        try
        {
            for (uint death = 1; death <= 3; death++)
            {
                Creature killer = await SpawnAsync(host, session, Entry, 100 + death);
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    Assert.True(player.Map!.Combat.Attack(killer, player));
                    player.Map!.Combat.DealDamage(killer, player, 1);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(500); // alive: the attackers are seen
                    player.Health = 0;
                    player.Map!.Combat.KillPlayer(player);
                    session.ManagedBudget = new ManagedActionBudget(4);
                    brain.Update(500); // dead: the death is recorded
                    player.Map!.RemoveObject(killer);
                    Assert.True(death >= 2 == brain.IsEntrySetAside(Entry), $"death {death}: set aside {brain.IsEntrySetAside(Entry)}");
                    Assert.Equal(death == 3, brain.StallReport?.StartsWith("death loop: died 3 times", StringComparison.Ordinal) == true);
                    Assert.Equal(death == 3, brain.Recovery.SpiritHealerChosen);
                    if (death < 3) player.Map!.Combat.ResurrectPlayer(player, 1f, applySickness: false);
                    return true;
                });
                if (death == 3) break;

                // Revived beside another creature of that entry: killed once by one, the bot still picks it as its next fight;
                // killed twice, it leaves it be.
                Creature candidate = await SpawnAsync(host, session, Entry, 200 + death);
                bool chosen = false;
                for (int think = 0; think < 20 && !chosen; think++)
                {
                    chosen = await host.OnWorldAsync(() =>
                    {
                        session.ManagedBudget = new ManagedActionBudget(4);
                        brain.Update(500);
                        return ReferenceEquals(brain.InspectionTarget, candidate) || brain.TargetEntry == Entry;
                    });
                    if (!chosen) await Task.Delay(20);
                }

                Assert.True(chosen == (death == 1), $"after death {death} the creature was {(chosen ? "" : "not ")}chosen");
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    player.Map!.RemoveObject(candidate);
                    if (player.Combat.Victim is not null) player.Map!.Combat.AttackStop(player);
                    return true;
                });
            }

            await host.OnWorldAsync(() =>
            {
                Assert.Contains($"attackers={Entry}", brain.StallReport);
                Assert.Equal(1, brain.StallCount);
                return true;
            });
        }
        finally
        {
            brain.Stop();
            session.Kick();
            await session.ManagedClosed;
        }
    }

    private static async Task<Creature> SpawnAsync(WorldTestHost host, WorldSession session, uint entry, uint low)
    {
        Creature creature = await host.OnWorldAsync(() =>
        {
            Player player = session.Player!;
            var spawned = new Creature(low, new CreatureTemplate
            {
                Entry = entry, Name = "invasion soldier", CreatureType = 1,
                MinLevel = player.Level, MaxLevel = player.Level, MinLevelHealth = 20, MaxLevelHealth = 20,
            }, null, CreatureContent.Empty, new Random((int)low));
            spawned.Relocate(player.X + 8f, player.Y, player.Z, 0, host.World.NowMs);
            player.Map!.AddObject(spawned);
            return spawned;
        });
        await host.WaitForWorldAsync(() => session.Player!.VisibleObjects.Contains(creature.Guid), "the creature in sight");
        return creature;
    }

    /// <summary>
    /// A ghost told to take the spirit healer (a death loop) does so although its body lies in reclaim range: it never reclaims and
    /// is revived at the healer. Without the choice the same ghost waits at its body and reclaims it.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AGhostInADeathLoop_TakesTheSpiritHealer_InsteadOfItsBodyInReach(bool deathLoop)
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        // The reclaim delay follows game time (the manual clock), not the wall clock.
        await host.OnWorldAsync(() => DeathHooks.Register(host.Host.World,
            new DeathHooks(new DeathOptions(), new GameTimeDeathClock(host.Host.World))));
        await host.TeleportAsync(0, DeadminesTestContent.Graveyard.X - 25f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        ObjectGuid healer = await host.OnWorldAsync(() => host.Player.VisibleObjects.Select(g => host.Player.Map!.FindObject(g))
            .OfType<Creature>().Single(c => (c.NpcFlags & (uint)NpcFlags.SpiritHealer) != 0).Guid);

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        if (deathLoop) recovery.TakeSpiritHealer();
        var steps = new HashSet<Step>();
        bool alive = false;
        for (int think = 0; think < 400 && !alive; think++)
        {
            alive = await host.OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(host.Session, host.Player);
                if (host.Player.IsAlive) return true;
                PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs);
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                recovery.Update(host.Player, 500);
                steps.Add(recovery.LastStep);
                return host.Player.IsAlive;
            });
            if (!alive) await host.AdvanceAsync(500);
        }

        Assert.True(alive, "still a ghost after: " + string.Join(", ", steps));
        await host.OnWorldAsync(() =>
        {
            Creature spirit = (Creature)host.Player.Map!.FindObject(healer)!;
            float fromHealer = Vector3.Distance(new(spirit.X, spirit.Y, spirit.Z), new(host.Player.X, host.Player.Y, host.Player.Z));
            if (deathLoop)
            {
                Assert.Contains(Step.SpiritHealer, steps);
                Assert.DoesNotContain(Step.Reclaim, steps);
                Assert.True(fromHealer <= 5f, $"revived {fromHealer:F1} yards from the healer");
            }
            else
            {
                Assert.Contains(Step.Reclaim, steps);
                Assert.DoesNotContain(Step.SpiritHealer, steps);
            }
        });
    }

    private sealed class GameTimeDeathClock(ArcaneCore.Game.Maps.WorldRuntime world) : DeathClock
    {
        public override long UnixSeconds => 1_800_000_000L + (long)world.Uptime.TotalSeconds;
    }
}
