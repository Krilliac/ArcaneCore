using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using Step = ArcaneCore.World.Playerbots.PlayerbotRecoveryStep;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// A body that is unsafe to reclaim is given up for the spirit healer at once (vmangos NPCHandler.cpp:416, SendSpiritResurrect:
/// half health and mana, resurrection sickness, 25% durability) instead of being walked back to, and the home return then takes the
/// bot home. Live 2026-10-11 05:45-06:00 UTC (world.log, build 567dbfeb): the Westfall-homed level-14 Wfbotaz died among the level 20+
/// skeletons and spiders of Raven Hill (attackers 202/531/569, 202/217/569), reclaimed its body there, died again, and went round
/// twice ("death loop: died 3 times in 454s within 60 yd of -10627.8,383.2", then again 441 s later); only the third death of a loop
/// sent it to the spirit healer. The test world is the Deadmines bot content (flat floor, open paths, a spirit healer at the Westfall
/// graveyard); the ghost is released where it died (no graveyard is linked to that place) and sees the healer from its body.
/// </summary>
public sealed class PlayerbotUnsafeBodyRecoveryTests
{
    /// <summary>Where the bot dies: 60 yards west of the spirit healer, which the ghost sees from there.</summary>
    private static readonly Vector3 Body = new(DeadminesTestContent.Graveyard.X - 60f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);

    private static Vector3 Healer => new(DeadminesTestContent.Graveyard.X + 2f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);

    /// <summary>
    /// The live case: a level-14 bot has died twice at the same spot, its body lying among level-24 monsters (Raven Hill's Skeletal
    /// Horrors). This time it does not walk back to the body: it takes the spirit healer, and the home return (Auto, its home 2000
    /// yards away) teleports it home. Before, the second death was no death loop yet (three are needed), so the ghost went back to
    /// the body and reclaimed it among the same monsters.
    /// </summary>
    [Fact]
    public async Task ABotThatDiedTwiceAtItsBodyAmongStrongHostiles_TakesTheSpiritHealer_AndEndsAtHome()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        var home = new PlayerbotHomeArea(0, new Vector3(Body.X + 2000f, Body.Y, Body.Z), 100f, "Farhome");
        PlayerbotBrain brain = await BrainAsync(host, home);
        await DieAndComeBackAsync(host, brain); // the first death here
        await KillAsync(host, brain);           // the second
        Creature[] camp = await CampAsync(host, strong: true);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive && home.Contains(p), thinks: 400);

        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.DiedHere, run.Reason); // judged first; the camp alone would send it too (the next test)
        Assert.False(run.Reclaimed, "the body was reclaimed among the monsters: " + run.Trail);
        Assert.True(run.Done, "the bot did not end at home: " + run.Trail);
        Assert.True(Vector2.Distance(new(run.RevivedAt.X, run.RevivedAt.Y), new(Healer.X, Healer.Y)) <= 5f, "revived away from the healer: " + run.Trail);
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(1, brain.HomeReturn.Teleports);
            Assert.Null(host.Player.Combat.Corpse);
            brain.Stop();
        });
    }

    /// <summary>
    /// The repeat death alone: no creature near the body, inside the home, but the bot already died there a minute ago. Reviving
    /// there again would only repeat it (the stall watch's death memory, <see cref="PlayerbotStallWatch.RecordDeath"/>): it takes the
    /// spirit healer.
    /// </summary>
    [Fact]
    public async Task ASecondDeathAtTheSameSpot_TakesTheSpiritHealer()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.DiedHere, run.Reason);
        Assert.False(run.Reclaimed, run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// The risk model alone, at the first death: the body lies among monsters the bot could not take even at full health
    /// (<see cref="PlayerbotRiskModel.Assess"/>: avoid). Reclaiming there revives it inside their reach; it takes the healer.
    /// </summary>
    [Fact]
    public async Task ABodyAmongHostilesTooStrongForTheBot_IsGivenUpForTheSpiritHealer()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await KillAsync(host, brain);
        Creature[] camp = await CampAsync(host, strong: true);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.TooStrong, run.Reason);
        Assert.False(run.Reclaimed, run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// No regression: a first death in a safe spot inside the home (no creature near the body) is reclaimed as before, where the ghost
    /// stands, and the spirit healer is not taken.
    /// </summary>
    [Fact]
    public async Task AFirstDeathInASafeSpotInsideTheHome_StillReclaimsTheBody()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 100);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        Assert.True(Vector2.Distance(new(run.RevivedAt.X, run.RevivedAt.Y), new(Body.X, Body.Y)) < 5f, "revived away from the body: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// The risk estimate is a matter of strength, not of presence: three weak monsters round the body (level 8 against the level-14
    /// bot, a fight the model takes even at the half health a reclaim gives) do not make the body unsafe, and it is reclaimed.
    /// </summary>
    [Fact]
    public async Task AWeakCampRoundTheBody_IsNoReasonToGiveItUp()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await KillAsync(host, brain);
        Creature[] camp = await CampAsync(host, strong: false);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// A body outside the home, with the spirit healer nearer the home (here inside it): the healer brings the bot home, the body
    /// would leave it outside, so the bot takes the healer even at its first death and with nothing near the body.
    /// </summary>
    [Fact]
    public async Task ABodyOutsideTheHome_WithTheSpiritHealerNearerHome_TakesTheHealer()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        // Centre 150 yards east of the healer, radius 200: the healer is inside (150 yards), the body outside (212 yards).
        var home = new PlayerbotHomeArea(0, new Vector3(Healer.X + 150f, Healer.Y, Healer.Z), 200f, "Graveyardhome");
        Assert.False(home.Contains(0, Body.X, Body.Y, Body.Z));
        Assert.True(home.Contains(0, Healer.X, Healer.Y, Healer.Z));
        PlayerbotBrain brain = await BrainAsync(host, home);
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.OutsideHome, run.Reason);
        Assert.False(run.Reclaimed, run.Trail);
        await host.OnWorldAsync(() =>
        {
            Assert.True(home.Contains(host.Player), "the bot did not end inside its home");
            brain.Stop();
        });
    }

    // --- harness -------------------------------------------------------------------------------------------------------

    /// <summary>A home round the body and the healer (radius 300): neither is outside it.</summary>
    private static PlayerbotHomeArea HomeAroundBodyAndHealer() => new(0, (Body + Healer) / 2f, 300f, "Westfallhome");

    private static async Task<DungeonBotHost> StartAsync(TestDeathClock clock)
    {
        DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() =>
        {
            DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock));
            host.Player.Level = 14;
        });
        await host.TeleportAsync(0, Body.X, Body.Y, Body.Z);
        return host;
    }

    private static Task<PlayerbotBrain> BrainAsync(DungeonBotHost host, PlayerbotHomeArea home)
        => host.OnWorldAsync(() => new PlayerbotBrain(host.Session, new PlayerbotOptions { Enabled = true }) { Home = home });

    /// <summary>The bot dies where it stands; the brain sees the death at once (its death memory, the risk's danger memory).</summary>
    private static Task KillAsync(DungeonBotHost host, PlayerbotBrain brain) => host.OnWorldAsync(() =>
    {
        host.Player.Health = 0;
        host.Player.Map!.Combat.KillPlayer(host.Player);
        host.Session.ManagedBudget = new ManagedActionBudget(4);
        brain.Update(1); // records the death; the recovery thinks at the next full interval
        Assert.False(host.Player.IsAlive);
    });

    /// <summary>
    /// An earlier death at the body's spot, a minute before: the bot dies, the brain records it, and it is back alive there (as after
    /// a reclaim) and lives a minute before it dies again.
    /// </summary>
    private static async Task DieAndComeBackAsync(DungeonBotHost host, PlayerbotBrain brain)
    {
        await KillAsync(host, brain);
        await host.OnWorldAsync(() =>
        {
            host.Player.Map!.Combat.ResurrectPlayer(host.Player, 0.5f, applySickness: false);
            Assert.True(host.Player.IsAlive);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            brain.Update(1);
        });
        await host.AdvanceAsync(60_000);
        await host.OnWorldAsync(() => host.Player.Relocate(Body.X, Body.Y, Body.Z, 0, host.Host.World.NowMs));
    }

    /// <summary>
    /// Three monsters round the body, west of it (60 yards and more from the healer, beyond their reach of it): level 24 against the
    /// level-14 bot with 1500 health and 40-60 damage a swing (<paramref name="strong"/>), as Raven Hill's Skeletal Horrors; else level 8 with
    /// 40 health and 1-2 damage, a camp the bot takes on at half health.
    /// </summary>
    private static Task<Creature[]> CampAsync(DungeonBotHost host, bool strong) => host.OnWorldAsync(() =>
    {
        Map map = host.Player.Map!;
        uint now = host.Host.World.NowMs;
        return new[] { (-15f, 0f), (-10f, 12f), (-10f, -12f) }.Select((offset, index) =>
        {
            uint low = 994400u + (uint)index;
            var creature = new Creature(low, new CreatureTemplate
            {
                Entry = low, Name = "skeletal horror " + low, CreatureType = 6, Faction = 14,
                MinLevel = strong ? (byte)24 : (byte)8, MaxLevel = strong ? (byte)24 : (byte)8,
                MinLevelHealth = strong ? 1500u : 40u, MaxLevelHealth = strong ? 1500u : 40u,
                MinMeleeDamage = strong ? 40f : 1f, MaxMeleeDamage = strong ? 60f : 2f, MeleeBaseAttackTime = 2000,
            }, null, CreatureContent.Empty, new Random((int)low));
            creature.Relocate(Body.X + offset.Item1, Body.Y + offset.Item2, Body.Z, 0, now);
            map.AddObject(creature);
            return creature;
        }).ToArray();
    });

    private readonly record struct Run(bool Done, bool Healer, bool Reclaimed, Vector3 RevivedAt, PlayerbotUnsafeBody Reason, string Trail);

    /// <summary>
    /// Think as the feature does (the motion pumped, then the brain) every 500 ms of game time until <paramref name="done"/> holds,
    /// noting whether the recovery took the spirit healer or reclaimed the body, where the bot came back to life, and a trail of
    /// the brain's goals and recovery steps for a failure message.
    /// </summary>
    private static async Task<Run> RunAsync(DungeonBotHost host, PlayerbotBrain brain, TestDeathClock clock, Func<Player, bool> done, int thinks)
    {
        bool healer = false, reclaimed = false, finished = false, delayPassed = false;
        PlayerbotUnsafeBody reason = PlayerbotUnsafeBody.None;
        Vector3? revivedAt = null;
        var trail = new List<string>();
        for (int think = 0; think < thinks && !finished; think++)
        {
            finished = await host.OnWorldAsync(() =>
            {
                Player player = host.Player;
                PlayerbotMotion.Pump(host.Session, player, host.Host.World.NowMs);
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                bool wasAlive = player.IsAlive;
                brain.Update(500);
                PlayerbotRecovery recovery = brain.Recovery;
                healer |= recovery.UsingSpiritHealer || recovery.SpiritHealerChosen;
                if (reason == PlayerbotUnsafeBody.None) reason = recovery.UnsafeReason;
                reclaimed |= recovery.LastStep == Step.Reclaim && player.IsAlive && !wasAlive;
                if (!wasAlive && player.IsAlive) revivedAt ??= new Vector3(player.X, player.Y, player.Z);
                // Past any reclaim delay (30, 60 or 120 s from the release) once released: only the choice keeps a ghost from its body.
                if (!delayPassed && player.Combat.Corpse is not null)
                {
                    clock.Seconds += 125;
                    delayPassed = true;
                }

                string step = $"{brain.Goal}/{(player.IsAlive ? "alive" : recovery.LastStep + (recovery.LastSpiritHealerStep is { } s ? "." + s : string.Empty))}";
                if (trail.Count == 0 || !trail[^1].StartsWith(step + " ", StringComparison.Ordinal))
                    trail.Add($"{step} ({player.X:F0},{player.Y:F0})");
                return done(player);
            });
            if (!finished) await host.AdvanceAsync(500);
        }

        return new Run(finished, healer, reclaimed, revivedAt ?? new Vector3(float.NaN), reason, string.Join(" -> ", trail));
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
