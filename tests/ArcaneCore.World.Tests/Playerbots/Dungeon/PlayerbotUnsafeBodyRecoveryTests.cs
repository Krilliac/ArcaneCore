using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Graveyards;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.WorldData;
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
    /// The graveyard chosen at the body is kept while the ghost crosses cells with no area on its way to it. Live 2026-10-11 12:18-12:32
    /// (w23/w24, world.log; Grogeil, a level-28 Tauren): its body lay in the Barrens (zone 17, area flag 130) at (1437.1, -2804.0) among
    /// creatures too strong for it, so it took the spirit healer; the ghost's position was in zone 17, linked to the Crossroads for the
    /// Horde, so it walked west to that graveyard, out of sight of any healer. 150 yards on, at (1291.8, -2764.4), the terrain's area
    /// cells read 0xFFFF (no area: zone 0, real terrain-5875 and AreaTable.dbc), and the graveyard was looked up again from there at every
    /// think: nothing is linked to zone 0, so the goal vanished, the ghost stood waiting for a healer, and the fallback faulted
    /// ("gives up the spirit healer ...: goal none, n/a yd away, route none") three times, until the bot was disabled. The test world's
    /// band of cells without a graveyard lies between the body and the healer's graveyard, 300 yards east (beyond the ghost's sight).
    /// </summary>
    [Fact]
    public async Task AGhostWalkingToItsGraveyardThroughCellsWithoutAnArea_KeepsItsGoal_AndReachesTheHealer()
    {
        var clock = new TestDeathClock(1_000);
        WorldSafeLoc grave = DeadminesTestContent.Graveyard;
        var body = new Vector3(grave.X - 300f, grave.Y, DeadminesTestContent.OutsideFloor);
        await using DungeonBotHost host = await StartAsync(clock, at: body);
        PlayerbotBrain brain = await BrainAsync(host, new PlayerbotHomeArea(0, (body + Healer) / 2f, 400f, "Barrenshome"));
        // The graveyard is known round the body and round the graveyard; the cells in between have no area (zone 0, nothing linked).
        await host.OnWorldAsync(() => brain.Recovery.GraveyardLookup = (_, at) =>
            Vector2.Distance(new(at.X, at.Y), new(body.X, body.Y)) < 40f || Vector2.Distance(new(at.X, at.Y), new(grave.X, grave.Y)) < 60f
                ? grave : null);
        await KillAsync(host, brain);
        Creature[] camp = await CampAsync(host, strong: true);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
        await host.OnWorldAsync(() => Assert.DoesNotContain(host.Player.VisibleObjects,
            guid => host.Player.Map!.FindObject(guid) is Creature c && (c.NpcFlags & (uint)NpcFlags.SpiritHealer) != 0));

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 400);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.TooStrong, run.Reason);
        Assert.False(run.Reclaimed, run.Trail);
        Assert.Contains("SpiritHealer.WalkToGraveyard", run.Trail, StringComparison.Ordinal);
        Assert.True(Vector2.Distance(new(run.RevivedAt.X, run.RevivedAt.Y), new(Healer.X, Healer.Y)) < 10f, "revived away from the healer: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// The graveyard the ghost set out for is kept on its own (review of ee40ad7a): no graveyard is known at the body, so the body's
    /// graveyard cannot carry the walk; the healer's graveyard is known only round the ghost's starting point, 250 yards west of it
    /// (out of the healer's sight), and the ground in between has no area. The ghost keeps walking to the graveyard it chose, reaches
    /// the healer and is revived there; it never goes back to its body. Before, it lost its goal 40 yards on (the live Grogeil fault).
    /// </summary>
    [Fact]
    public async Task AGhostWhoseBodyHasNoGraveyard_KeepsTheGraveyardItSetOutFor_AndReachesTheHealer()
    {
        var clock = new TestDeathClock(1_000);
        (DungeonBotHost host, PlayerbotBrain brain, _) = await StartForTheKeptGraveyardAsync(clock);
        await using DungeonBotHost owned = host;

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 400);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.False(run.Reclaimed, "the body was reclaimed: " + run.Trail);
        Assert.DoesNotContain("ResumeCorpseRun", run.Trail, StringComparison.Ordinal);
        Assert.True(Vector2.Distance(new(run.RevivedAt.X, run.RevivedAt.Y), new(Healer.X, Healer.Y)) < 10f, "revived away from the healer: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// The kept graveyard is for the ghost's map only: walking to it on map 0 with nothing known round it, the ghost is taken to
    /// Kalimdor (no graveyard, no healer there; its body stays on map 0). It does not walk on to the map-0 graveyard's coordinates on
    /// Kalimdor: it waits for a healer, as it did before a graveyard was kept.
    /// </summary>
    [Fact]
    public async Task AKeptGraveyard_IsDropped_WhenTheGhostIsOnAnotherMap()
    {
        var clock = new TestDeathClock(1_000);
        (DungeonBotHost host, PlayerbotBrain brain, Vector3 start) = await StartForTheKeptGraveyardAsync(clock);
        await using DungeonBotHost owned = host;
        Run walking = await RunAsync(host, brain, clock, p => OnTheKeptGraveyard(brain, p, start), thinks: 200);
        Assert.True(walking.Done, "the ghost never walked on the kept graveyard: " + walking.Trail);

        await host.TeleportAsync(1, 100f, 100f, DeadminesTestContent.OutsideFloor);
        Run after = await RunAsync(host, brain, clock, p => false, thinks: 3);

        await host.OnWorldAsync(() =>
        {
            Assert.Equal(1u, host.Player.MapId);
            Assert.False(host.Player.IsAlive);
            Assert.Equal(Step.SpiritHealer, brain.Recovery.LastStep);
            Assert.True(brain.Recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.WaitForHealer,
                $"{brain.Recovery.LastSpiritHealerStep}: " + after.Trail);
            brain.Stop();
        });
    }

    /// <summary>
    /// A new death is judged afresh even when no think saw the bot alive in between (review of 986e558f): the ghost walking on its
    /// kept graveyard is revived and dies again 400 yards south, where no graveyard is known, before the recovery thinks again. The
    /// new body is no longer the one the recovery released, so it starts afresh: no spirit-healer fallback carried over from the last
    /// death (no kept graveyard, no resumed corpse run, no healer request), the ghost runs to its new body and reclaims it.
    /// </summary>
    [Fact]
    public async Task ANewBody_IsJudgedAfresh_EvenWithoutAThinkWhileAlive()
    {
        var clock = new TestDeathClock(1_000);
        (DungeonBotHost host, PlayerbotBrain brain, Vector3 start) = await StartForTheKeptGraveyardAsync(clock);
        await using DungeonBotHost owned = host;
        Run walking = await RunAsync(host, brain, clock, p => OnTheKeptGraveyard(brain, p, start), thinks: 200);
        Assert.True(walking.Done, "the ghost never walked on the kept graveyard: " + walking.Trail);
        ObjectGuid oldBody = await host.OnWorldAsync(() => host.Player.Combat.Corpse!.Guid);

        await host.OnWorldAsync(() =>
        {
            host.Player.Map!.Combat.ResurrectPlayer(host.Player, 0.5f, applySickness: false);
            Assert.True(host.Player.IsAlive);
        });
        await host.TeleportAsync(0, start.X, start.Y - 400f, start.Z);
        await host.KillAndReleaseAsync();
        await host.OnWorldAsync(() => Assert.NotEqual(oldBody, host.Player.Combat.Corpse!.Guid));

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 60);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.False(run.Healer, "the last death's spirit-healer fallback carried over: " + run.Trail);
        Assert.True(run.Reclaimed, "the new body was not reclaimed: " + run.Trail);
        // No step of the last death's fallback, not even the one that gives it up (ResumeCorpseRun): the new death never entered it.
        Assert.DoesNotContain("SpiritHealer", run.Trail, StringComparison.Ordinal);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// The brain resets the recovery at its first update alive even when pending movement orders hold the rest of that update (review
    /// of 986e558f): the ghost walking on its kept graveyard is revived, a near teleport is pending, and the alive update only
    /// acknowledges it. Before, the reset came after that early return: the recovery kept the last death's spirit-healer fallback, and
    /// the next death went straight to it (PlayerbotRecovery.Update), never released its new body, and faulted as stalled.
    /// </summary>
    [Fact]
    public async Task ARevivalHeldByAPendingTeleport_ResetsTheRecovery_AndTheNextBodyIsReleasedAndReclaimed()
    {
        var clock = new TestDeathClock(1_000);
        (DungeonBotHost host, PlayerbotBrain brain, Vector3 start) = await StartForTheKeptGraveyardAsync(clock);
        await using DungeonBotHost owned = host;
        Run walking = await RunAsync(host, brain, clock, p => OnTheKeptGraveyard(brain, p, start), thinks: 200);
        Assert.True(walking.Done, "the ghost never walked on the kept graveyard: " + walking.Trail);
        var spot = new Vector3(start.X, start.Y - 400f, start.Z);

        await host.OnWorldAsync(() =>
        {
            Assert.True(brain.Recovery.UsingSpiritHealer);
            host.Player.Map!.Combat.ResurrectPlayer(host.Player, 0.5f, applySickness: false);
            ArcaneCore.Game.Teleport.TeleportService teleports =
                host.Host.WorldServices.GetRequiredService<ArcaneCore.World.Teleport.TeleportFeature>().Teleports;
            Assert.True(teleports.TeleportTo(host.Player, 0, spot.X, spot.Y, spot.Z, 0), "teleport refused");
            Assert.NotNull(teleports.StageOf(host.Player)); // pending until the bot acknowledges it
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            brain.Update(500); // alive: the movement control acknowledges the teleport and holds the rest of the update
            Assert.False(brain.Recovery.UsingSpiritHealer, "the alive update held by the pending teleport did not reset the recovery");
        });
        await host.AcknowledgeUntilAsync(p => Vector2.Distance(new(p.X, p.Y), new(spot.X, spot.Y)) < 1f, "the bot arrives");
        await KillAsync(host, brain);

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.StartsWith("Recover/Release", run.Trail, StringComparison.Ordinal);
        Assert.False(run.Healer, "the last death's spirit-healer fallback carried over: " + run.Trail);
        Assert.True(run.Reclaimed, "the new body was not reclaimed: " + run.Trail);
        Assert.DoesNotContain("SpiritHealer", run.Trail, StringComparison.Ordinal);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>The ghost walks to the healer's graveyard where its own cell knows none: the kept graveyard is what it walks on.</summary>
    private static bool OnTheKeptGraveyard(PlayerbotBrain brain, Player player, Vector3 start)
        => !player.IsAlive && brain.Recovery.LastSpiritHealerStep == PlayerbotSpiritHealerStep.WalkToGraveyard
            && Vector2.Distance(new(player.X, player.Y), new(start.X, start.Y)) > 50f;

    /// <summary>
    /// The bot dies and is released at a body 300 yards west of the healer's graveyard, where no graveyard is known; its ghost is taken
    /// 50 yards east (the start: the healer's graveyard is known within 40 yards of it; the healer is out of sight), and a death loop
    /// asks for the healer. Only the start and the graveyard's own surroundings know the graveyard.
    /// </summary>
    private static async Task<(DungeonBotHost Host, PlayerbotBrain Brain, Vector3 Start)> StartForTheKeptGraveyardAsync(TestDeathClock clock)
    {
        WorldSafeLoc grave = DeadminesTestContent.Graveyard;
        var body = new Vector3(grave.X - 300f, grave.Y, DeadminesTestContent.OutsideFloor);
        var start = new Vector3(grave.X - 250f, grave.Y, DeadminesTestContent.OutsideFloor);
        DungeonBotHost host = await StartAsync(clock, at: body);
        PlayerbotBrain brain = await BrainAsync(host, new PlayerbotHomeArea(0, (body + Healer) / 2f, 400f, "Barrenshome"));
        await host.OnWorldAsync(() => brain.Recovery.GraveyardLookup = (_, at) =>
            Vector2.Distance(new(at.X, at.Y), new(start.X, start.Y)) < 40f || Vector2.Distance(new(at.X, at.Y), new(grave.X, grave.Y)) < 60f
                ? grave : null);
        await host.KillAndReleaseAsync();
        await host.TeleportAsync(0, start.X, start.Y, start.Z);
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            Assert.True(Vector2.Distance(new(corpse.X, corpse.Y), new(body.X, body.Y)) < 1f, "the body is not where the bot died");
            brain.Recovery.TakeSpiritHealer(); // as a death loop asks
            Assert.DoesNotContain(host.Player.VisibleObjects,
                guid => host.Player.Map!.FindObject(guid) is Creature c && (c.NpcFlags & (uint)NpcFlags.SpiritHealer) != 0);
        });
        return (host, brain, start);
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
    /// The same unhomed bot in a party (a stored group with an offline partner; the brain still drives it, PlayerbotPartyAI.Drives): the
    /// bind-point teleport is refused as the return teleport home is (in a group), and the bot is not pulled away from its group. Before,
    /// EscapeLanding called TeleportToHomebind, which checks nothing.
    /// </summary>
    [Fact]
    public async Task AGroupedUnhomedBot_RevivedAmongStrongHostiles_IsNotTeleportedToItsBindPoint()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, at: CampedBody, configure: services =>
            services.AddSingleton<ICreatureDataStore>(new HealerAmongSkeletons(CampOverBodyAndHealer)));
        var bind = new Vector3(CampedBody.X + 1500f, CampedBody.Y, CampedBody.Z);
        await host.OnWorldAsync(() =>
        {
            host.Player.Home = new HomeBind(0, 0, bind.X, bind.Y, bind.Z);
            PlayerbotTestGroups.JoinOfflinePartner(host.Session, host.Player);
        });
        PlayerbotBrain brain = await BrainAsync(host, home: null);
        await KillAsync(host, brain);
        await SeeTheCampAsync(host, CampOverBodyAndHealer.Length);

        Run run = await RunAsync(host, brain, clock,
            p => Vector2.Distance(new(p.X, p.Y), new(bind.X, bind.Y)) < 5f, thinks: 200);

        Assert.False(run.Done, "the grouped bot was teleported to its bind point: " + run.Trail);
        await host.OnWorldAsync(() => brain.Stop());
    }

    /// <summary>
    /// The camp of the live case covers the healer's landing and the body. The old five-yard revive margin found a spot just beyond
    /// aggro, but no spot has the required ten-yard clearance. Recovery takes the healer and escapes its unsafe landing.
    /// </summary>
    [Fact]
    public async Task AnUnsafeBody_WithNoTenYardClearance_AndAnUnsafeLanding_TakesTheHealer()
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
        Assert.False(kept, "the body was kept without ten-yard clearance: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.False(run.Reclaimed, run.Trail);
        Assert.True(run.Done, "the bot did not end at home, alive: " + run.Trail);
        Assert.Equal(1, run.Revivals);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// A death loop's request for the healer is never overridden by the landing (review of PR #167): the camp of the live case covers
    /// the landing, and even if a clear spot near the body made an unsafe body worth keeping, the bot has died there three times, and
    /// the healer is the way out of a body death loop. It takes the healer, and leaves the unsafe landing at once (home, alive).
    /// </summary>
    [Fact]
    public async Task ADeathLoop_WithAnUnsafeLandingAndAClearSpot_StillTakesTheHealer_AndEscapes()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, at: CampedBody, configure: services =>
            services.AddSingleton<ICreatureDataStore>(new HealerAmongSkeletons(CampAtTheGraveyard)));
        var home = new PlayerbotHomeArea(0, new Vector3(CampedBody.X + 2000f, CampedBody.Y, CampedBody.Z), 100f, "Farhome");
        PlayerbotBrain brain = await BrainAsync(host, home);
        await KillAsync(host, brain);
        await host.OnWorldAsync(() => brain.Recovery.TakeSpiritHealer()); // the death loop's request (PlayerbotBrain.RecordDeath)
        await SeeTheCampAsync(host, CampAtTheGraveyard.Length);
        bool kept = false;

        Run run = await RunAsync(host, brain, clock, p =>
        {
            kept |= brain.Recovery.BodyKeptOverLanding;
            return p.IsAlive && home.Contains(p);
        }, thinks: 400);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.False(kept, "the body was kept over the death loop's request: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.False(run.Reclaimed, run.Trail);
        Assert.True(run.Done, "the bot did not end at home, alive: " + run.Trail);
        Assert.Equal(1, run.Revivals);
        await host.OnWorldAsync(() =>
        {
            Assert.True(brain.Recovery.EscapedLanding, "the revived bot was not taken away from the landing");
            Assert.Equal(PlayerbotHomeTeleportReason.UnsafeLanding, brain.HomeReturn.LastTeleport);
            brain.Stop();
        });
    }

    /// <summary>
    /// The real landing is the body's graveyard (review of PR #167): when it differs from the ghost's, the resurrection teleports the
    /// revived bot there (vmangos SendSpiritResurrect; GraveyardRepopService.TeleportToCorpseGraveyard), a near teleport pending until
    /// the bot acknowledges it. Here the body lies at graveyard A among the skeletons (out of the healer's sight), the ghost is released
    /// there and walks to the healer at graveyard B, 70 yards east, which is safe. The bot is judged where the resurrection sends it (A),
    /// not where it stood (B), and the escape replaces the pending teleport: it ends at home, alive, never at A.
    /// </summary>
    [Fact]
    public async Task AHealerAtASafeGraveyard_WhoseResurrectionSendsTheBotToAnUnsafeOne_EscapesFromTheRealLanding()
    {
        var clock = new TestDeathClock(1_000);
        Vector3 graveA = new(Healer.X - 70f, Healer.Y, Healer.Z);
        Vector3 body = new(graveA.X - 10f, graveA.Y, graveA.Z);
        (float X, float Y)[] camp = [.. CampOverBodyAndHealer.Select(o => (o.X - 70f, o.Y))]; // over the body and graveyard A
        await using DungeonBotHost host = await StartAsync(clock, at: body, configure: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new HealerAmongSkeletons(camp));
            services.AddSingleton<IGraveyardDataStore>(new TwoGraveyards(graveA));
        });
        var home = new PlayerbotHomeArea(0, new Vector3(body.X + 2000f, body.Y, body.Z), 100f, "Farhome");
        PlayerbotBrain brain = await BrainAsync(host, home);
        // The test terrain knows no zones, so the graveyards are linked at run time (the outdoor PvP seam, IGraveyardLinkSource), and
        // the selector keeps the closest one to a position: A for the body, B for the ghost at the healer. The bot's own prediction
        // of the landing (SpiritHealerLanding) reads only the static links and sees none: it takes the healer in sight, B, for the
        // landing, which is safe; only the server's resurrection knows the bot goes to A.
        await host.OnWorldAsync(() => Assert.IsType<GraveyardRepopService>(DeathSeams.Find(host.Host.World)!.Graveyards)
            .AddLinkSource(new LinkBoth()));
        await KillAsync(host, brain);
        await SeeTheCampAsync(host, camp.Length);
        bool everAtA = false;

        Run run = await RunAsync(host, brain, clock, p =>
        {
            everAtA |= p.IsAlive && Vector2.Distance(new(p.X, p.Y), new(graveA.X, graveA.Y)) < 5f;
            return p.IsAlive && home.Contains(p);
        }, thinks: 400);

        Assert.True(run.Fault is null, "the recovery faulted: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.True(run.Done, "the bot did not end at home, alive: " + run.Trail);
        Assert.Equal(1, run.Revivals);
        Assert.False(everAtA, "the revived bot stood at the unsafe graveyard: " + run.Trail);
        await host.OnWorldAsync(() =>
        {
            Assert.True(brain.Recovery.EscapedLanding, "the revived bot was not taken away from the real landing: " + run.Trail);
            Assert.Equal(PlayerbotHomeTeleportReason.UnsafeLanding, brain.HomeReturn.LastTeleport);
            Assert.False(host.Player.Combat.IsInCombat);
            brain.Stop();
        });
    }

    /// <summary>
    /// Two safe locations (no static links: the test terrain knows no zones): A, where the skeletons stand, and B, the spirit healer's
    /// (<see cref="Healer"/>). <see cref="LinkBoth"/> links them at run time.
    /// </summary>
    private sealed class TwoGraveyards(Vector3 graveA) : IGraveyardDataStore
    {
        public Task<GraveyardContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new GraveyardContent(
            [
                new WorldSafeLoc(9101, 0, graveA.X, graveA.Y, graveA.Z, 0f, "Camped graveyard"),
                new WorldSafeLoc(9102, 0, Healer.X, Healer.Y, Healer.Z, 0f, "Healer's graveyard"),
            ],
            []));
    }

    /// <summary>Both graveyards of <see cref="TwoGraveyards"/> for every position (GraveyardRepopService keeps the closest).</summary>
    private sealed class LinkBoth : IGraveyardLinkSource
    {
        public IEnumerable<uint> ExtraLinks(Player player, uint zoneId, uint areaId) => [9101u, 9102u];
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

    // --- resurrection sickness (analysis bot-xp-analysis.md, 4.1) -----------------------------------------------------------------

    /// <summary>
    /// The live spiral of 2026-10-11 (5,789 spirit-healer revivals of population bots in six hours; 47% of the "too strong" verdicts
    /// against creatures at or below the bot's level): a level-14 bot carrying resurrection sickness from an earlier healer revival dies,
    /// and its body lies among ordinary level-13 creatures, one on it and eight round it, so every spot of the reclaim radius is in a
    /// creature's reach. Judged with its sickened health they are too strong and the body is given up for the healer at once, which
    /// sickens it again. Judged as the corpse run revives it (no new sickness; the old one runs out) they are not: no verdict, no healer;
    /// the ghost waits for the camp at its body.
    /// </summary>
    [Fact]
    public async Task ASickBotsBodyAmongLevelAppropriateCreatures_IsJudgedWithoutItsSickness_AndKeepsTheCorpseRun()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        (uint well, uint sick) = await host.OnWorldAsync(() =>
        {
            GiveLevel14Stamina(host.Player);
            uint before = host.Player.MaxHealth;
            PlayerbotSicknessTestKit.Apply(host.Host.WorldServices, host.Player);
            return (before, host.Player.MaxHealth);
        });
        Assert.True(sick * 3 < well * 2, $"the sickness took too little health: {well} -> {sick}");
        await KillAsync(host, brain);
        Creature[] camp = await CampOverBodyAsync(host, OrdinaryLevel13);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        await host.OnWorldAsync(() =>
        {
            Assert.True(PlayerbotSickness.IsSick(host.Session, host.Player), "the ghost lost its sickness");
            var mods = host.Player.StatState.Mods;
            Assert.True(well == PlayerbotSickness.UnsickenedMaxHealth(host.Session, host.Player),
                $"unsickened {PlayerbotSickness.UnsickenedMaxHealth(host.Session, host.Player)} max {host.Player.MaxHealth} well {well} sick {sick} stam {host.Player.GetInt32(UpdateFields.UnitFieldStat0 + 2)} applied {mods.Applied(ArcaneCore.Game.Stats.UnitMods.StatStamina)} base% {mods.BasePct(ArcaneCore.Game.Stats.UnitMods.StatStamina)} total% {mods.TotalPct(ArcaneCore.Game.Stats.UnitMods.StatStamina)}");
            PlayerbotEngagement? verdict = brain.Risk.TooStrongNear(host.Player, Body);
            Assert.True(verdict is null, $"judged too strong ({verdict}); health well {well}, sick {sick}");
        });

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive || brain.Recovery.SpiritHealerChosen, thinks: 30);

        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, run.Reason);
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(Step.WaitForHostiles, brain.Recovery.LastStep);
            brain.Stop();
        });
    }

    /// <summary>
    /// The same camp, but for a bot that is well: the strength judged is the same either way (the reference for the test above), and
    /// the body is kept as well.
    /// </summary>
    [Fact]
    public async Task AWellBotsBodyAmongTheSameCreatures_IsNotTooStrongEither()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await host.OnWorldAsync(() => GiveLevel14Stamina(host.Player));
        await KillAsync(host, brain);
        Creature[] camp = await CampOverBodyAsync(host, OrdinaryLevel13);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(400u, host.Player.MaxHealth);
            Assert.Null(brain.Risk.TooStrongNear(host.Player, Body));
            brain.Stop();
        });
    }

    /// <summary>
    /// Sickness level 11 (vmangos' default): the level-14 bot of the live repeat death (it already died at its body a minute ago, and
    /// creatures that would attack the revived bot still stand in reach of the body) keeps its corpse run instead of the spirit healer,
    /// which would cost resurrection sickness and a quarter of its durability; it revives on a clear spot near the body. Below the
    /// sickness level the same death takes the healer (<see cref="ASecondDeathAtTheSameSpot_WithHostilesStillInReach_TakesTheSpiritHealer"/>).
    /// </summary>
    [Fact]
    public async Task ALevel14RepeatDeath_KeepsTheCorpseRun_ForTheHealerWouldSickenIt()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await KillAsync(host, brain);
        Creature[] camp = await CampAsync(host, strong: false, round: Body + new Vector3(12f, 0f, 0f));
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
        PlayerbotUnsafeBody kept = PlayerbotUnsafeBody.None;

        Run run = await RunAsync(host, brain, clock, p =>
        {
            if (brain.Recovery.BodyKeptOverSickness != PlayerbotUnsafeBody.None) kept = brain.Recovery.BodyKeptOverSickness;
            return p.IsAlive;
        }, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.DiedHere, kept);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    [Fact]
    public async Task ARepeatDeathCausedByAKillerThreeLevelsAbove_DoesNotKeepTheBodyOverSickness()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await host.OnWorldAsync(() =>
        {
            Player player = host.Player;
            uint now = host.Host.World.NowMs;
            var killer = new Creature(995201, new CreatureTemplate
            {
                Entry = 995201, Name = "higher level killer", CreatureType = 6, Faction = 14,
                MinLevel = 17, MaxLevel = 17, MinLevelHealth = 40, MaxLevelHealth = 40,
                MinMeleeDamage = 2, MaxMeleeDamage = 3, MeleeBaseAttackTime = 2000,
            }, null, CreatureContent.Empty, new Random(995201));
            killer.Relocate(player.X + 8, player.Y, player.Z, 0, now);
            player.Map!.AddObject(killer);
            brain.ObserveKillingBlows(player);
            player.Health = 1;
            player.Map.Combat.DealDamage(killer, player, 2);
            Assert.False(player.IsAlive);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            brain.Update(1);
            player.Map.RemoveObject(killer);
        });
        Creature[] camp = await CampAsync(host, strong: false, round: Body + new Vector3(12f, 0f, 0f));
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);
        Assert.True(run.Done, run.Trail);
        Assert.True(run.Healer, run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.DiedHere, run.Reason);
        Assert.Equal(PlayerbotUnsafeBody.None, brain.Recovery.BodyKeptOverSickness);
        await host.OnWorldAsync(brain.Stop);
    }

    [Fact]
    public async Task ARepeatDeathWhileAlreadySick_DoesNotKeepTheBodyOverSickness()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await host.OnWorldAsync(() => PlayerbotSicknessTestKit.Apply(host.Host.WorldServices, host.Player));
        await KillAsync(host, brain);
        Creature[] camp = await CampAsync(host, strong: false, round: Body + new Vector3(12f, 0f, 0f));
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
        Assert.True(await host.OnWorldAsync(() => PlayerbotSickness.IsSick(host.Session, host.Player)));

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);
        Assert.True(run.Done, run.Trail);
        Assert.True(run.Healer, run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.DiedHere, run.Reason);
        Assert.Equal(PlayerbotUnsafeBody.None, brain.Recovery.BodyKeptOverSickness);
        await host.OnWorldAsync(brain.Stop);
    }

    [Fact]
    public async Task AThirdDeathAtTheSameBody_TakesTheHealerInsteadOfKeepingItOverSickness()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await DieAndComeBackAsync(host, brain);
        await KillAsync(host, brain);
        Assert.True(await host.OnWorldAsync(() => brain.Recovery.SpiritHealerRequested));
        Creature[] camp = await CampAsync(host, strong: false, round: Body + new Vector3(12f, 0f, 0f));
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
        PlayerbotUnsafeBody kept = PlayerbotUnsafeBody.None;

        Run run = await RunAsync(host, brain, clock, p =>
        {
            if (brain.Recovery.BodyKeptOverSickness != PlayerbotUnsafeBody.None) kept = brain.Recovery.BodyKeptOverSickness;
            return p.IsAlive;
        }, thinks: 200);
        Assert.True(run.Done, run.Trail);
        Assert.True(run.Healer, run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.None, kept);
        await host.OnWorldAsync(brain.Stop);
    }

    [Fact]
    public async Task ALevel14RepeatDeath_WithNoClearReclaimSpot_TakesTheSpiritHealer()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await DieAndComeBackAsync(host, brain);
        await KillAsync(host, brain);
        Creature[] camp = await CampOverBodyAsync(host, OrdinaryLevel13);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "no clear reclaim spot, but the healer was not taken: " + run.Trail);
        // WaitForHostiles makes the fallback before JudgeBody records an unsafe-body reason.
        Assert.False(run.Reclaimed, run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// Sickness level 11: a body among creatures really too strong for the level-14 bot (level 24 Skeletal Horrors, judged without any
    /// sickness) whose reclaim radius has a spot out of their reach is kept, and the bot revives there; the healer would sicken it.
    /// Below the sickness level the same body takes the healer (<see cref="ABodyAmongHostilesTooStrongForTheBot_IsGivenUpForTheSpiritHealer"/>).
    /// </summary>
    [Fact]
    public async Task ALevel14BodyAmongStrongCreatures_WithAClearSpot_KeepsTheCorpseRun()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await KillAsync(host, brain);
        Creature[] camp = await CampAsync(host, strong: true);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
        PlayerbotUnsafeBody kept = PlayerbotUnsafeBody.None;

        Run run = await RunAsync(host, brain, clock, p =>
        {
            if (brain.Recovery.BodyKeptOverSickness != PlayerbotUnsafeBody.None) kept = brain.Recovery.BodyKeptOverSickness;
            return p.IsAlive;
        }, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.Equal(PlayerbotUnsafeBody.TooStrong, kept);
        Assert.False(run.Healer, "the spirit healer was taken: " + run.Trail);
        Assert.True(run.Reclaimed, "the body was not reclaimed: " + run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>A real death loop still takes the spirit healer at level 14 with sickness from level 11: the healer is the way out of it.</summary>
    [Fact]
    public async Task ALevel14DeathLoop_StillTakesTheSpiritHealer()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await StartAsync(clock, sicknessLevel: SicknessFrom11);
        PlayerbotBrain brain = await BrainAsync(host, HomeAroundBodyAndHealer());
        await KillAsync(host, brain);
        await host.OnWorldAsync(() => brain.Recovery.TakeSpiritHealer()); // the death loop's request (PlayerbotBrain.RecordDeath)
        Creature[] camp = await CampAsync(host, strong: true);
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");

        Run run = await RunAsync(host, brain, clock, p => p.IsAlive, thinks: 200);

        Assert.True(run.Done, "still dead: " + run.Trail);
        Assert.True(run.Healer, "the spirit healer was not taken: " + run.Trail);
        Assert.False(run.Reclaimed, run.Trail);
        await host.OnWorldAsync(brain.Stop);
    }

    /// <summary>
    /// Level 13 against the level-14 bot, 400 health, 28-37 damage a 2-second swing (16.25 a second): an ordinary same-zone creature.
    /// Against the well bot (400 health, the level-14 damage estimate of 27.2 a second) a fight at full health risks about 0.6 of its
    /// health, under the 0.9 that any pull is ever taken at; against the sick one (190 health) about 1.26, beyond its strength.
    /// </summary>
    private static readonly CreatureTemplate OrdinaryLevel13 = new()
    {
        Entry = 994420, Name = "ordinary level 13", CreatureType = 7, Faction = 14, MinLevel = 13, MaxLevel = 13,
        MinLevelHealth = 400, MaxLevelHealth = 400, MinMeleeDamage = 28f, MaxMeleeDamage = 37f, MeleeBaseAttackTime = 2000,
    };

    /// <summary>
    /// World thread: a level-14 bot's stamina (40, all of it the level base: no item adds any) and the 400 health it gives with a base of
    /// 180 (<c>Player::GetHealthBonusFromStamina</c>: 20 + 20 x 10). The test player is made level 14 by hand, without the stats.
    /// </summary>
    private static void GiveLevel14Stamina(Player player)
    {
        player.SetUInt32(UpdateFields.UnitFieldStat0 + 2, 40);
        player.SetUInt32(UpdateFields.PlayerFieldPosstat0 + 2, 0);
        player.SetUInt32(UpdateFields.PlayerFieldNegstat0 + 2, 0);
        ArcaneCore.Game.Stats.StatBonuses.Update(player); // the stats lane's ledger of the health stamina gives (what an aura moves)
        player.MaxHealth = 400;
        player.Health = 400;
    }

    /// <summary>
    /// <paramref name="template"/> on the body and eight round it 22 yards out: every spot of the reclaim radius lies in one's reach, so
    /// no revive spot is clear; 17 yards apart or more, beyond each other's assistance (10 yards).
    /// </summary>
    private static Task<Creature[]> CampOverBodyAsync(DungeonBotHost host, CreatureTemplate template) => host.OnWorldAsync(() =>
    {
        Map map = host.Player.Map!;
        uint now = host.Host.World.NowMs;
        Vector3 body = new(host.Player.X, host.Player.Y, host.Player.Z);
        (float X, float Y)[] offsets = [(0f, 0f), .. Enumerable.Range(0, 8).Select(i => (22f * MathF.Cos(i * MathF.PI / 4f), 22f * MathF.Sin(i * MathF.PI / 4f)))];
        return offsets.Select((offset, index) =>
        {
            uint low = 994420u + (uint)index;
            var creature = new Creature(low, template with { Entry = low }, null, CreatureContent.Empty, new Random((int)low));
            creature.Relocate(body.X + offset.X, body.Y + offset.Y, body.Z, 0, now);
            map.AddObject(creature);
            return creature;
        }).ToArray();
    });

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

    /// <summary>
    /// <c>Death.SicknessLevel</c> above the level cap: the spirit healer sickens nobody, so a level-14 bot's unsafe body is judged by the
    /// rules of a bot below the sickness level (the tests written before <see cref="PlayerbotRecovery.BodyKeptOverSickness"/>).
    /// </summary>
    private const int NoSickness = 61;

    /// <summary>The default <c>Death.SicknessLevel</c> (vmangos 11): the spirit healer sickens the level-14 bot.</summary>
    private const int SicknessFrom11 = 11;

    private static async Task<DungeonBotHost> StartAsync(TestDeathClock clock, uint map = 0, Vector3? at = null,
        Action<IServiceCollection>? configure = null, int sicknessLevel = NoSickness)
    {
        DungeonBotHost host = await DungeonBotHost.StartAsync(configure);
        await host.OnWorldAsync(() =>
        {
            DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions { SicknessLevel = sicknessLevel }, clock));
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
