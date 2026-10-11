using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.DependencyInjection;
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
    /// The repeat death: inside the home, the bot already died at the body a minute ago, and creatures that would attack the revived
    /// bot still stand in reach of the body (weak ones: not too strong, the repeat alone decides). Reviving there again would only
    /// repeat it (the stall watch's death memory, <see cref="PlayerbotStallWatch.RecordDeath"/>): it takes the spirit healer.
    /// </summary>
    [Fact]
    public async Task ASecondDeathAtTheSameSpot_WithHostilesStillInReach_TakesTheSpiritHealer()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await OnAnOldErrandAsync(host, brain);
        await KillAsync(host, brain);
        // Level 8 against 14: an aggro reach of 5 yards (the floor), so one stands 3 yards from the body.
        Creature[] camp = await CampAsync(host, strong: false, round: Body + new Vector3(12f, 0f, 0f));
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.DiedHere, run.Reason);
        Assert.False(run.Reclaimed, run.Trail);
        // The errand that led there is set aside as after a death loop: the revived bot does not walk straight back.
        await host.OnWorldAsync(() => Assert.True(brain.IsEntrySetAside(ErrandVendor), "the errand that led there was not set aside"));
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// A second death at a spot nothing hostile stands near any more (the killers gone, nothing in reach of the body): a busy
    /// grinding spot cleared is safe, whatever happened there. Live 2026-10-11 08:04-08:10 UTC, 10 of the 80 bodies given up were
    /// "already died there"; each cost resurrection sickness and a quarter of the durability. The body is reclaimed (the errand that led
    /// there is still set aside, as before), and the third death at one spot would be a death loop, which asks for the healer anyway.
    /// </summary>
    [Fact]
    public async Task ASecondDeathAtTheSameSpot_WithNothingHostileInReach_IsReclaimed()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await OnAnOldErrandAsync(host, brain);
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        await host.OnWorldAsync(() => Assert.True(brain.IsEntrySetAside(ErrandVendor), "the errand that led there was not set aside"));
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// No spirit healer to be had (Kalimdor in the test world: no graveyard is linked and no healer stands anywhere): a second death
    /// at the body among monsters too strong for the bot is no reason to give the body up, for the ghost would wait for a healer
    /// until the fallback faulted (<see cref="PlayerbotRecovery.SpiritHealerFailed"/>) and the bot was kicked. vmangos leaves a spirit
    /// with no linked graveyard where it died (Player::RepopAtGraveyard); the body is its way back, and it reclaims it, never faulting.
    /// </summary>
    [Fact]
    public async Task WithoutASpiritHealerToBeHad_ASecondDeathAmongStrongHostiles_StillReclaimsTheBody()
    {
        var clock = new TestDeathClock(1_000);
        var kalimdor = new Vector3(100f, 100f, DeadminesTestContent.OutsideFloor);
        await using DungeonBotHost host = await StartAsync(clock, map: 1, at: kalimdor);
        PlayerbotBrain brain = await BrainAsync(host, new PlayerbotHomeArea(1, kalimdor, 300f, "Kalimdorhome"));
        await DieAndComeBackAsync(host, brain);
        await KillAsync(host, brain);
        Creature[] camp = await CampAsync(host, strong: true);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 300); // a fault throws out of brain.Update

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// A death loop (the third death at one spot) where no spirit healer can be had: the loop asks for the healer
    /// (<see cref="PlayerbotRecovery.TakeSpiritHealer"/>), but taking it would leave the ghost waiting until the fallback faulted
    /// (<see cref="PlayerbotRecovery.SpiritHealerFailed"/>) and the bot was kicked, then disabled. It keeps its corpse run and
    /// reclaims; the loop's errand set-aside and the home return do the rest. Before, the loop forced the healer and faulted.
    /// </summary>
    [Fact]
    public async Task ADeathLoopWithoutASpiritHealerToBeHad_ReclaimsTheBody_AndNeverFaults()
    {
        var clock = new TestDeathClock(1_000);
        var kalimdor = new Vector3(100f, 100f, DeadminesTestContent.OutsideFloor);
        await using DungeonBotHost host = await StartAsync(clock, map: 1, at: kalimdor);
        PlayerbotBrain brain = await BrainAsync(host, new PlayerbotHomeArea(1, kalimdor, 300f, "Kalimdorhome"));
        await DieAndComeBackAsync(host, brain);
        await DieAndComeBackAsync(host, brain);
        await KillAsync(host, brain);
        await host.OnWorldAsync(() =>
        {
            Assert.StartsWith("death loop: died 3 times", brain.StallReport, StringComparison.Ordinal);
            Assert.True(brain.Recovery.SpiritHealerRequested);
        });

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 300); // a fault throws out of brain.Update

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// A body outside the home whose spirit healer would leave the bot only a few yards nearer the home's centre (less than
    /// <see cref="PlayerbotRecovery.OutsideHomeGainYards"/>) is reclaimed: so small a gain is not worth resurrection sickness and a
    /// quarter of the durability.
    /// </summary>
    [Fact]
    public async Task ABodyOutsideTheHome_WithTheHealerHardlyNearer_IsReclaimed()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        // Centre 400 yards north of the body and the healer, radius 100: both outside, the healer 0.2 yards nearer.
        var home = new PlayerbotHomeArea(0, new Vector3(Healer.X - 30f, Healer.Y + 400f, Healer.Z), 100f, "Northhome");
        PlayerbotBrain brain = await BrainAsync(host, home);
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 100);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
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
        await OnAnOldErrandAsync(host, brain);
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 100);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        Assert.True(Vector2.Distance(new(run.RevivedAt.X, run.RevivedAt.Y), new(Body.X, Body.Y)) < 5f, "revived away from the body: " + run.Trail);
        await host.OnWorldAsync(() => Assert.False(brain.IsEntrySetAside(ErrandVendor), "a first death set its errand aside"));
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
    /// A body outside the home, with the spirit healer nearer the home (here inside it), for a bot whose home return cannot teleport
    /// (<see cref="PlayerbotHomeReturnMode.Walk"/>): the healer brings the bot home, the body would leave it outside with a walk back,
    /// so the bot takes the healer even at its first death and with nothing near the body.
    /// </summary>
    [Fact]
    public async Task ABodyOutsideTheHome_WithTheSpiritHealerNearerHome_AndNoHomeTeleport_TakesTheHealer()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotHomeArea home = HomeRoundTheHealer();
        PlayerbotBrain brain = await BrainAsync(host, home, PlayerbotHomeReturnMode.Walk);
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

    /// <summary>
    /// The live case of 2026-10-11 08:07-08:09 UTC (world.log, build b3ce1529): the Westfall-homed level-14 Wfbotfe died beside the Raven
    /// Hill graveyard, among the level-24 Skeletal Horrors that stand round it. Its body was judged unsafe (too strong, then died there),
    /// so it took the spirit healer, and the healer revived it at the same graveyard among the same skeletons: they attacked at once, the
    /// home return could not teleport a bot in combat, it retreated and died, twice more (a death loop), and its fourth recovery faulted
    /// (<see cref="PlayerbotRecovery.SpiritHealerFailed"/>). Here the camp stands round the healer and the body alike (real spawns of the
    /// creature system, which aggro a living bot on sight in the world's own update, vmangos BasicAI::MoveInLineOfSight) and covers the
    /// whole reclaim radius of the body, so the healer is the only way back. The revived bot leaves before the camp can aggro it: the
    /// home teleport in the same think as the resurrection (out of combat). It ends at home, alive, revived once, never faulting.
    /// </summary>
    [Fact]
    public async Task AHealerLandingAmongStrongHostiles_TeleportsTheRevivedBotHome_BeforeTheyAttack()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, at: CampedBody, configure: services =>
            services.AddSingleton<ICreatureDataStore>(new HealerAmongSkeletons(CampOverBodyAndHealer)));
        var home = new PlayerbotHomeArea(0, new Vector3(CampedBody.X + 2000f, CampedBody.Y, CampedBody.Z), 100f, "Farhome");
        PlayerbotBrain brain = await BrainAsync(host, home);
        await KillAsync(host, brain);
        await SeeTheCampAsync(host, CampOverBodyAndHealer.Length);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive && home.Contains(p), thinks: 400);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.TooStrong, run.Reason);
        Assert.True(run.Done, "the bot did not end at home, alive: " + run.Trail);
        Assert.Equal(1, run.Revivals); // one resurrection: it never died again at the landing
        await host.OnWorldAsync(() =>
        {
            Assert.True(brain.Recovery.EscapedLanding, "the revived bot was not taken away from the landing");
            Assert.Equal(1, brain.HomeReturn.Teleports);
            Assert.Equal(PlayerbotHomeTeleportReason.UnsafeLanding, brain.HomeReturn.LastTeleport);
            Assert.False(host.Player.Combat.IsInCombat);
            brain.Stop();
        });
    }

    /// <summary>
    /// The unhomed case (live 2026-10-11, Dawnrover: death loops at Raven Hill at 08:23:32 and 08:27:47, then the spirit-healer fault at
    /// 08:30:25 and quarantine): a bot without a home revived among the same camp has no home to go to. It teleports to its bind point,
    /// where its hearthstone leads (vmangos Player::TeleportToHomebind), in the think of the resurrection, and lives.
    /// </summary>
    [Fact]
    public async Task AnUnhomedBot_RevivedAmongStrongHostiles_TeleportsToItsBindPoint_BeforeTheyAttack()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, at: CampedBody, configure: services =>
            services.AddSingleton<ICreatureDataStore>(new HealerAmongSkeletons(CampOverBodyAndHealer)));
        var bind = new Vector3(CampedBody.X + 1500f, CampedBody.Y, CampedBody.Z);
        await host.OnWorldAsync(() => host.Player.Home = new HomeBind(0, 0, bind.X, bind.Y, bind.Z));
        PlayerbotBrain brain = await BrainAsync(host, home: null);
        await KillAsync(host, brain);
        await SeeTheCampAsync(host, CampOverBodyAndHealer.Length);

        Run run = await RunAsync(host, brain, clock,
            p => p.IsAlive && Vector2.Distance(new(p.X, p.Y), new(bind.X, bind.Y)) < 5f, thinks: 400);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.True(run.Done, "the bot did not end at its bind point, alive: " + run.Trail);
        Assert.Equal(1, run.Revivals);
        await host.OnWorldAsync(() =>
        {
            Assert.True(brain.Recovery.EscapedLanding, "the revived bot was not taken away from the landing");
            Assert.False(host.Player.Combat.IsInCombat);
            brain.Stop();
        });
    }

    /// <summary>
    /// The camp of the live case covers the healer's landing and the body, but not all of the body's reclaim radius: a spot inside it
    /// lies out of every skeleton's reach. The body is then the safer of the two, though it was judged too strong: the bot keeps its
    /// corpse run, revives on that spot (no sickness, no durability loss, out of combat) and goes home as after any death.
    /// </summary>
    [Fact]
    public async Task AnUnsafeBody_WithAClearSpotInReach_AndAnUnsafeLanding_IsReclaimedOnTheClearSpot()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, at: CampedBody, configure: services =>
            services.AddSingleton<ICreatureDataStore>(new HealerAmongSkeletons(CampAtTheGraveyard)));
        var home = new PlayerbotHomeArea(0, new Vector3(CampedBody.X + 2000f, CampedBody.Y, CampedBody.Z), 100f, "Farhome");
        PlayerbotBrain brain = await BrainAsync(host, home);
        await KillAsync(host, brain);
        await SeeTheCampAsync(host, CampAtTheGraveyard.Length);
        bool kept = false;

        Run run = await RunAsync(host, brain, clock, p =>
        {
            kept |= brain.Recovery.BodyKeptOverLanding;
            return p.IsAlive && home.Contains(p);
        }, thinks: 400);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(kept, "the body was not kept over the unsafe landing: " + run.Trail);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        Assert.True(run.Done, "the bot did not end at home, alive: " + run.Trail);
        Assert.Equal(1, run.Revivals);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// The no-fault half of the live case: the fault was no refusal by the server but the motion's loop watch (PlayerbotMotion). The
    /// bot's walks to the same healer at each death and its retreats in between made an A, B, A pattern across deaths; the healer's
    /// place was refused for 60 seconds, the ghost could plan no route to it, and the stuck bound faulted the recovery 10 seconds later.
    /// A new death forgets the loop watch (<see cref="PlayerbotMotion.ForgetLoops"/>): a ghost whose healer was blacklisted by its
    /// living self still walks to it, activates it and lives.
    /// </summary>
    [Fact]
    public async Task AHealerRefusedByTheLoopWatchBeforeTheDeath_IsStillReached_AndNeverFaults()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await host.OnWorldAsync(() =>
        {
            // The living bot's loop: the healer's place, somewhere else, the healer's place again, and again.
            PlayerbotMotion.MotionState state = PlayerbotMotion.Of(host.Player);
            state.World = host.Host.World;
            uint now = host.Host.World.NowMs;
            Vector3 away = new(Healer.X - 40f, Healer.Y + 40f, Healer.Z);
            Assert.Null(state.Loops.OnRoute(Healer, now));
            Assert.Null(state.Loops.OnRoute(away, now));
            Assert.Null(state.Loops.OnRoute(Healer, now));
            Assert.Null(state.Loops.OnRoute(away, now));
            IReadOnlyList<Vector3>? loop = state.Loops.OnRoute(Healer, now);
            Assert.NotNull(loop);
            foreach (Vector3 place in loop!) state.Blacklist.Add((place, now + PlayerbotMotion.LoopBlacklistMs));
            Assert.True(PlayerbotMotion.IsBlacklisted(host.Player, Healer));
            host.Player.Relocate(Body.X, Body.Y, Body.Z, 0, now);
        });
        await KillAsync(host, brain);
        await host.OnWorldAsync(() => brain.Recovery.TakeSpiritHealer()); // as a death loop asks

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 300);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// The same body outside the home with the healer inside it, for a bot whose home return teleports (Auto, the default; no return
    /// teleport in the last five minutes): the body is no danger, and the home return takes the revived bot home for nothing, so it is
    /// reclaimed (no resurrection sickness, no durability loss). Live 2026-10-11 08:04-08:10 UTC, 39 of the 80 bodies given up were
    /// "outside its home", most of them in Westfall a few hundred yards beyond a 550-yard Sentinel Hill home.
    /// </summary>
    [Fact]
    public async Task ABodyOutsideTheHome_WhenTheHomeReturnCanTeleport_IsReclaimed_AndTheBotGoesHome()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock);
        PlayerbotHomeArea home = HomeRoundTheHealer();
        PlayerbotBrain brain = await BrainAsync(host, home);
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive && home.Contains(p), thinks: 400);

        Assert.True(run.Done, "the bot did not end at home, alive: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>Centre 150 yards east of the healer, radius 200: the healer is inside (150 yards), the body outside (212 yards).</summary>
    private static PlayerbotHomeArea HomeRoundTheHealer()
    {
        var home = new PlayerbotHomeArea(0, new Vector3(Healer.X + 150f, Healer.Y, Healer.Z), 200f, "Graveyardhome");
        Assert.False(home.Contains(0, Body.X, Body.Y, Body.Z));
        Assert.True(home.Contains(0, Healer.X, Healer.Y, Healer.Z));
        return home;
    }

    // --- harness -------------------------------------------------------------------------------------------------------

    /// <summary>A home round the body and the healer (radius 300): neither is outside it.</summary>
    private static PlayerbotHomeArea HomeAroundBodyAndHealer() => new(0, (Body + Healer) / 2f, 300f, "Westfallhome");

    /// <summary>The body of the camped-graveyard tests: 10 yards west of the spirit healer (live: Wfbotfe's third death, 10 yards from the graveyard).</summary>
    private static Vector3 CampedBody => new(Healer.X - 10f, Healer.Y, Healer.Z);

    /// <summary>Three skeletons west of the healer within 15 yards: the landing and the body are in their reach, the far side of the reclaim radius is not.</summary>
    private static readonly (float X, float Y)[] CampAtTheGraveyard = [(-15f, 0f), (-10f, 12f), (-10f, -12f)];

    /// <summary>
    /// Seven skeletons over the body (one on it, six round it 20 yards out): every spot within the reclaim radius of the body, and the
    /// healer's landing, lies in one's reach (28 yards for level 24 against 14).
    /// </summary>
    private static readonly (float X, float Y)[] CampOverBodyAndHealer =
        [(-10f, 0f), .. Enumerable.Range(0, 6).Select(i => (-10f + (20f * MathF.Cos(i * MathF.PI / 3f)), 20f * MathF.Sin(i * MathF.PI / 3f)))];

    /// <summary>The Skeletal Horror of <see cref="HealerAmongSkeletons"/>.</summary>
    private const uint SkeletonEntry = 994410;

    /// <summary>The ghost sees every skeleton (they were spawned with the grid).</summary>
    private static async Task SeeTheCampAsync(DungeonBotHost host, int count)
    {
        Creature[] camp = await host.OnWorldAsync(() => host.Player.Map!.FindUpdater<CreatureMapSystem>()!.Creatures
            .Where(c => c.Entry == SkeletonEntry).ToArray());
        Assert.Equal(count, camp.Length);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
    }

    /// <summary>
    /// The test world's spirit healer (as <see cref="DeadminesTestContent"/> spawns it) with level-24 skeletons at <paramref name="offsets"/>
    /// from it: Raven Hill's graveyard. Real spawns of the creature system, so they aggro a living bot on sight.
    /// </summary>
    private sealed class HealerAmongSkeletons((float X, float Y)[] offsets) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default)
        {
            CreatureTemplate healer = new()
            {
                Entry = DeadminesTestContent.SpiritHealerEntry, Name = "Spirit Healer", Faction = 12, NpcFlags = (uint)NpcFlags.SpiritHealer,
                DisplayIds = [5233], MinLevel = 60, MaxLevel = 60, MinLevelHealth = 100, MaxLevelHealth = 100, ExtraFlags = Creature.ExtraFlagNoAggro,
            };
            CreatureTemplate skeleton = new()
            {
                Entry = SkeletonEntry, Name = "Skeletal Horror", CreatureType = 6, Faction = 14, DisplayIds = [200],
                MinLevel = 24, MaxLevel = 24, MinLevelHealth = 1500, MaxLevelHealth = 1500,
                MinMeleeDamage = 40f, MaxMeleeDamage = 60f, MeleeBaseAttackTime = 2000,
            };
            List<CreatureSpawn> spawns = [new CreatureSpawn { Guid = DeadminesTestContent.SpiritHealerSpawn, Entry = healer.Entry, MapId = 0, X = Healer.X, Y = Healer.Y, Z = Healer.Z }];
            spawns.AddRange(offsets.Select((offset, index) => new CreatureSpawn
            {
                Guid = 994410u + (uint)index, Entry = SkeletonEntry, MapId = 0, X = Healer.X + offset.X, Y = Healer.Y + offset.Y, Z = Healer.Z,
            }));
            return Task.FromResult(new CreatureContent([healer, skeleton], [.. spawns], [], [], []));
        }
    }

    private static async Task<DungeonBotHost> StartAsync(TestDeathClock clock, uint map = 0, Vector3? at = null,
        Action<IServiceCollection>? configure = null)
    {
        DungeonBotHost host = await DungeonBotHost.StartAsync(configure);
        await host.OnWorldAsync(() =>
        {
            DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock));
            host.Player.Level = 14;
        });
        Vector3 start = at ?? Body;
        await host.TeleportAsync(map, start.X, start.Y, start.Z);
        return host;
    }

    private static Task<PlayerbotBrain> BrainAsync(DungeonBotHost host, PlayerbotHomeArea? home,
        PlayerbotHomeReturnMode returnMode = PlayerbotHomeReturnMode.Auto)
        => host.OnWorldAsync(() => new PlayerbotBrain(host.Session,
            new PlayerbotOptions { Enabled = true, Home = new PlayerbotHomeOptions { ReturnMode = returnMode } }) { Home = home });

    /// <summary>A vendor the bot was walking to (<see cref="PlayerbotBrain.NoteErrand"/>).</summary>
    private const uint ErrandVendor = 994999;

    /// <summary>
    /// The bot was on its way to <see cref="ErrandVendor"/> 31 seconds ago, longer than <see cref="PlayerbotBrain.DeadlyErrandMs"/>: a
    /// first death does not set that errand aside.
    /// </summary>
    private static async Task OnAnOldErrandAsync(DungeonBotHost host, PlayerbotBrain brain)
    {
        await host.OnWorldAsync(() => brain.NoteErrand(PlayerbotGoalKind.Vendor, ErrandVendor, 0));
        await host.AdvanceAsync(PlayerbotBrain.DeadlyErrandMs + 1_000);
    }

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
        Vector3 spot = await host.OnWorldAsync(() => new Vector3(host.Player.X, host.Player.Y, host.Player.Z));
        await KillAsync(host, brain);
        await host.OnWorldAsync(() =>
        {
            host.Player.Map!.Combat.ResurrectPlayer(host.Player, 0.5f, applySickness: false);
            Assert.True(host.Player.IsAlive);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            brain.Update(1);
        });
        await host.AdvanceAsync(60_000);
        await host.OnWorldAsync(() => host.Player.Relocate(spot.X, spot.Y, spot.Z, 0, host.Host.World.NowMs));
    }

    /// <summary>
    /// Three monsters round the body (where the ghost stands), west of it (60 yards and more from the healer, beyond their reach of it): level 24 against the
    /// level-14 bot with 1500 health and 40-60 damage a swing (<paramref name="strong"/>), as Raven Hill's Skeletal Horrors; else level 8 with
    /// 40 health and 1-2 damage, a camp the bot takes on at half health.
    /// </summary>
    /// <param name="round">Where the camp stands (west of it); the ghost's position (its body) by default.</param>
    private static Task<Creature[]> CampAsync(DungeonBotHost host, bool strong, Vector3? round = null) => host.OnWorldAsync(() =>
    {
        Map map = host.Player.Map!;
        uint now = host.Host.World.NowMs;
        Vector3 body = round ?? new Vector3(host.Player.X, host.Player.Y, host.Player.Z); // the ghost stays at its body here
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
            creature.Relocate(body.X + offset.Item1, body.Y + offset.Item2, body.Z, 0, now);
            map.AddObject(creature);
            return creature;
        }).ToArray();
    });

    private readonly record struct Run(bool Done, bool Healer, bool Reclaimed, Vector3 RevivedAt, PlayerbotUnsafeBody Reason, string Trail,
        int Revivals = 0, string? Fault = null);

    /// <summary>
    /// Think as the feature does (the motion pumped, then the brain) every 500 ms of game time until <paramref name="done"/> holds,
    /// noting whether the recovery took the spirit healer or reclaimed the body, where the bot came back to life, and a trail of
    /// the brain's goals and recovery steps for a failure message.
    /// </summary>
    private static async Task<Run> RunAsync(DungeonBotHost host, PlayerbotBrain brain, TestDeathClock clock, Func<Player, bool> done, int thinks)
    {
        bool healer = false, reclaimed = false, finished = false, delayPassed = false;
        int revivals = 0;
        string? fault = null;
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
                try
                {
                    brain.Update(500);
                }
                catch (InvalidOperationException e)
                {
                    // A recovery fault (the feature would quarantine the bot): the run ends with it.
                    fault = e.Message;
                    trail.Add("FAULT " + e.Message);
                    return true;
                }
                PlayerbotRecovery recovery = brain.Recovery;
                healer |= recovery.UsingSpiritHealer || recovery.SpiritHealerChosen;
                if (reason == PlayerbotUnsafeBody.None) reason = recovery.UnsafeReason;
                reclaimed |= recovery.LastStep == Step.Reclaim && player.IsAlive && !wasAlive;
                if (!wasAlive && player.IsAlive)
                {
                    revivedAt ??= new Vector3(player.X, player.Y, player.Z);
                    revivals++;
                }
                // Past any reclaim delay (30, 60 or 120 s from the release) once released: only the choice keeps a ghost from its body.
                if (!delayPassed && player.Combat.Corpse is not null)
                {
                    clock.Seconds += 125;
                    delayPassed = true;
                }

                string step = $"{brain.Goal}/{(player.IsAlive ? (player.Combat.IsInCombat ? "alive-in-combat" : "alive") : recovery.LastStep + (recovery.LastSpiritHealerStep is { } s ? "." + s : string.Empty))}";
                if (trail.Count == 0 || !trail[^1].StartsWith(step + " ", StringComparison.Ordinal))
                    trail.Add($"{step} ({player.X:F0},{player.Y:F0})");
                return done(player);
            });
            if (!finished) await host.AdvanceAsync(500);
        }

        return new Run(finished && fault is null, healer, reclaimed, revivedAt ?? new Vector3(float.NaN), reason, string.Join(" -> ", trail),
            revivals, fault);
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
