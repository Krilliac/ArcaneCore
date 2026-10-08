using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Death;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps.Templates;
using ArcaneCore.Game.Npc;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Protocol;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Scenarios;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Xunit;
using Step = ArcaneCore.World.Playerbots.PlayerbotRecoveryStep;
using Place = ArcaneCore.World.Playerbots.PlayerbotCorpsePlace;
using Healer = ArcaneCore.World.Playerbots.PlayerbotSpiritHealerStep;

namespace ArcaneCore.World.Tests.Playerbots.Dungeon;

/// <summary>
/// The recovery decision tree (<see cref="PlayerbotRecovery.Decide"/>, <see cref="PlayerbotRecovery.DecideSpiritHealer"/>) and its
/// world behaviour: a body on this map is walked to; a body in a dungeon is reached through the entrance trigger; a hostile near
/// the body means waiting; a stalled run takes the spirit healer and nothing throws until that fails as well.
/// </summary>
public sealed class PlayerbotRecoveryDecisionTests
{
    public static TheoryData<bool, string, bool, bool, long, bool, bool, bool, bool, string> Tree => new()
    {
        // ghost, corpse, entrance known, at corpse, reclaim wait, hostile, stalled, fallback taken, revive spot known -> step
        { false, "ThisMap", false, false, 0, false, false, false, false, "Release" },
        { false, "ThisMap", false, false, 0, false, true, false, false, "Fault" },
        { true, "ThisMap", false, false, 0, false, false, false, false, "WalkToCorpse" },
        { true, "ThisMap", false, true, 30, false, false, false, false, "WaitForReclaimDelay" },
        { true, "ThisMap", false, true, 30, true, false, false, false, "WaitForReclaimDelay" },
        { true, "ThisMap", false, true, 0, true, false, false, false, "WaitForHostiles" },
        { true, "ThisMap", false, true, 0, false, false, false, false, "Reclaim" },
        { true, "OtherMap", true, false, 0, false, false, false, false, "WalkToEntrance" },
        { true, "OtherMap", false, false, 0, false, false, false, false, "SpiritHealer" },
        { true, "None", false, false, 0, false, false, false, false, "SpiritHealer" },
        { true, "ThisMap", false, false, 0, false, true, false, false, "SpiritHealer" },
        { true, "ThisMap", false, true, 0, true, true, false, false, "SpiritHealer" },
        { true, "OtherMap", true, false, 0, false, true, false, false, "SpiritHealer" },
        { true, "ThisMap", false, true, 0, false, false, true, false, "SpiritHealer" },
        // A camped revive point with a clear, reachable spot inside the reclaim radius: walk there instead of waiting (and reclaim
        // wherever the ghost stands once nothing camps it; the delay, a stall and the fallback come first, as before).
        { true, "ThisMap", false, true, 0, true, false, false, true, "WalkToReviveSpot" },
        { true, "ThisMap", false, true, 0, false, false, false, true, "Reclaim" },
        { true, "ThisMap", false, true, 30, true, false, false, true, "WaitForReclaimDelay" },
        { true, "ThisMap", false, true, 0, true, true, false, true, "SpiritHealer" },
        { true, "ThisMap", false, true, 0, true, false, true, true, "SpiritHealer" },
        { true, "ThisMap", false, false, 0, true, false, false, true, "WalkToCorpse" },
    };

    [Theory]
    [MemberData(nameof(Tree))]
    public void Decide(bool ghost, string corpse, bool entrance, bool atCorpse, long wait, bool hostile, bool stalled, bool fallback, bool reviveSpot, string expected)
        => Assert.Equal(Enum.Parse<Step>(expected),
            PlayerbotRecovery.Decide(ghost, Enum.Parse<Place>(corpse), entrance, atCorpse, wait, hostile, stalled, fallback, reviveSpot));

    [Theory]
    [InlineData(true, true, false, false, "Activate")]
    [InlineData(true, false, false, false, "WalkToHealer")]
    [InlineData(true, false, true, false, "WalkToHealer")]
    [InlineData(false, false, true, false, "WalkToGraveyard")]
    [InlineData(false, false, false, false, "Fault")]
    [InlineData(true, false, true, true, "Fault")]
    public void DecideSpiritHealer(bool healer, bool inReach, bool graveyard, bool stalled, string expected)
        => Assert.Equal(Enum.Parse<Healer>(expected), PlayerbotRecovery.DecideSpiritHealer(healer, inReach, graveyard, stalled));

    /// <summary>A threat with a same-level aggro radius (20 yards) unless given, on the ground (the height limit applies).</summary>
    private static PlayerbotThreat Threat(float x, float y, float radius = 20f, float z = 0f, bool flyer = false)
        => new(new Vector3(x, y, z), radius, flyer, Radii: 0f);

    private const float SpotClearance = PlayerbotRecovery.CampMarginYards + PlayerbotRecovery.ReviveSpotSlackYards;

    /// <summary>
    /// The way left along a route (the corpse run's second closing measure): to the next corner, along the remaining legs, then
    /// straight on to the goal. A bot on the first leg of a route that bends away from its goal still has less way left as it walks.
    /// </summary>
    [Fact]
    public void RouteLeft_FollowsTheRemainingLegs_ThenStraightOnToTheGoal()
    {
        var route = new PlayerbotRoute([new(0, 0, 0), new(10, 0, 0), new(10, 10, 0)], 20f, navigated: true);
        var goal = new Vector3(10, 20, 0);
        Assert.Equal(25f, PlayerbotRecovery.RouteLeft(new Vector3(5, 0, 0), route, goal), 3);
        Assert.Equal(29f, PlayerbotRecovery.RouteLeft(new Vector3(1, 0, 0), route, goal), 3);
        route.NextPoint = 2;
        Assert.Equal(15f, PlayerbotRecovery.RouteLeft(new Vector3(10, 5, 0), route, goal), 3);
        route.NextPoint = 3; // finished: straight on to the goal
        Assert.Equal(10f, PlayerbotRecovery.RouteLeft(new Vector3(10, 10, 0), route, goal), 3);
    }

    /// <summary>
    /// The revive-spot search, free of world state: rings round the body inside the reclaim radius, nearest to the ghost first, out
    /// of every threat's aggro reach with the margins; the first one the mesh reaches. The body here is surrounded: the only clear
    /// ground is the far rim.
    /// </summary>
    [Fact]
    public void FindReviveSpot_TakesTheNearestReachableCandidateOutOfEveryThreatsReach()
    {
        var body = new Vector3(0, 0, 0);
        var ghost = new Vector3(30, 0, 0);
        PlayerbotThreat[] hostiles = [Threat(0, 0), Threat(20, 0), Threat(5, 15), Threat(5, -15)];
        var asked = new List<Vector3>();
        Vector3? spot = PlayerbotRecovery.FindReviveSpot(ghost, body, hostiles, candidate => { asked.Add(candidate); return candidate; });

        Assert.NotNull(spot);
        Assert.True(Vector3.Distance(spot!.Value, body) <= PlayerbotRecovery.ReviveSpotMaxYards, "outside the reclaim radius");
        Assert.All(hostiles, hostile => Assert.False(hostile.Reaches(spot.Value, SpotClearance),
            $"{Vector3.Distance(spot.Value, hostile.Position):F1} yards from a hostile"));
        Assert.Equal(spot, asked[0]); // nearest first: the first clear candidate the search asked about was reachable
    }

    /// <summary>A body whose whole reclaim disc is inside the camp's reach has no spot: the ghost goes on waiting, as before.</summary>
    [Fact]
    public void FindReviveSpot_FindsNothing_WhenTheCampCoversTheReclaimDisc()
    {
        // Eight same-level hostiles on a circle of 22 yards round the body: every point of the disc is within 25 yards of one.
        PlayerbotThreat[] hostiles = [.. Enumerable.Range(0, 8).Select(i => Threat(22f * MathF.Cos(i * MathF.PI / 4f), 22f * MathF.Sin(i * MathF.PI / 4f)))];
        Assert.Null(PlayerbotRecovery.FindReviveSpot(new Vector3(30, 0, 0), new Vector3(0, 0, 0), hostiles, candidate => candidate));
    }

    /// <summary>
    /// The same camp of low-level creatures (an aggro radius of 8 yards: vmangos GetAttackDistance shrinks it by the level
    /// difference) leaves room: with the flat 25 yards every creature counted alike, it did not.
    /// </summary>
    [Fact]
    public void FindReviveSpot_UsesEachThreatsOwnAggroRadius()
    {
        PlayerbotThreat[] hostiles = [.. Enumerable.Range(0, 8).Select(i => Threat(22f * MathF.Cos(i * MathF.PI / 4f), 22f * MathF.Sin(i * MathF.PI / 4f), radius: 8f))];
        Vector3? spot = PlayerbotRecovery.FindReviveSpot(new Vector3(30, 0, 0), new Vector3(0, 0, 0), hostiles, candidate => candidate);
        Assert.NotNull(spot);
        Assert.All(hostiles, hostile => Assert.False(hostile.Reaches(spot!.Value, SpotClearance)));
    }

    /// <summary>
    /// A threat's reach (CreatureMapSystem.IsInAggroReach): strictly inside radius plus margin, in 3D; a creature that cannot fly
    /// does not aggro a unit more than 3 yards above or below it (bounding radii taken off), a flyer does.
    /// </summary>
    [Fact]
    public void AThreatReachesOnlyInsideItsRadiusAndHeightLimit()
    {
        Assert.True(Threat(0, 0, radius: 5f).Reaches(new Vector3(7, 0, 0), PlayerbotRecovery.CampMarginYards));
        Assert.False(Threat(0, 0, radius: 5f).Reaches(new Vector3(8, 0, 0), PlayerbotRecovery.CampMarginYards));
        Assert.False(Threat(0, 0, z: 5f).Reaches(new Vector3(2, 0, 0), PlayerbotRecovery.CampMarginYards));
        Assert.True(Threat(0, 0, z: 5f, flyer: true).Reaches(new Vector3(2, 0, 0), PlayerbotRecovery.CampMarginYards));
        Assert.True(new PlayerbotThreat(new Vector3(0, 0, 5), 20f, false, Radii: 2.5f).Reaches(new Vector3(2, 0, 0), 0f));
    }

    /// <summary>
    /// A candidate the mesh cannot reach is skipped for the next, and a reachable end that lies outside the radius or inside a
    /// threat's reach (the mesh ends short of an off-mesh candidate) is no spot; the number of asked candidates is bounded.
    /// </summary>
    [Fact]
    public void FindReviveSpot_SkipsUnreachableCandidates_AndChecksTheEndTheMeshReturns()
    {
        var body = new Vector3(0, 0, 0);
        var ghost = new Vector3(30, 0, 0);
        PlayerbotThreat[] hostiles = [Threat(30, 10)];
        int asked = 0;
        Assert.Null(PlayerbotRecovery.FindReviveSpot(ghost, body, hostiles, _ => { asked++; return null; }));
        Assert.Equal(PlayerbotRecovery.ReviveSpotMaxQueries, asked);

        // The mesh answers with the hostile's own place for every candidate: never a spot.
        Assert.Null(PlayerbotRecovery.FindReviveSpot(ghost, body, hostiles, _ => hostiles[0].Position));
        // ... and with a place beyond the radius: never a spot either.
        Assert.Null(PlayerbotRecovery.FindReviveSpot(ghost, body, hostiles, _ => new Vector3(60, 0, 0)));
        // The first candidate fails, the second is reached.
        int calls = 0;
        Vector3? spot = PlayerbotRecovery.FindReviveSpot(ghost, body, hostiles, candidate => ++calls == 1 ? null : candidate);
        Assert.NotNull(spot);
        Assert.Equal(2, calls);
    }

    /// <summary>A ghost whose body lies on its own map walks to it (the ordinary corpse run).</summary>
    [Fact]
    public async Task ABodyOnThisMap_IsWalkedTo()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(1, 100f, 100f, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(host.Player.X + 60, host.Player.Y, host.Player.Z, 0);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WalkToCorpse, recovery.LastStep);
            Assert.True(host.Player.Movement.HasFlag(MovementFlags.Forward));
        });
    }

    /// <summary>
    /// A bot killed in The Deadmines is released at the graveyard outside; its recovery walks to entrance trigger 78 (the trigger
    /// whose teleport leads to the body's map), walks in as a ghost and the server revives it at the entrance inside. Before, a body
    /// on another map made the recovery wait without doing anything until it threw "playerbot-recovery-stalled".
    /// </summary>
    [Fact]
    public async Task ABodyInADungeon_IsReachedThroughItsEntrance_AndTheGhostIsRevivedInside()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => host.Player.Level = 10);
        await host.TeleportAsync(DungeonEntryScenario.Deadmines, -16.4f, -383.07f, 61.78f, 1.9f);
        await host.KillAndReleaseAsync(p => p.MapId == 0);
        AreaTriggerTemplate entrance = await host.OnWorldAsync(() => WorldMaps.Of(host.Host.World).FindAreaTrigger(DungeonEntryScenario.EntranceTrigger)!);
        await host.OnWorldAsync(() =>
        {
            Assert.Equal(DeadminesTestContent.Graveyard.X, host.Player.X, 1);
            Assert.Equal(DungeonEntryScenario.Deadmines, host.Player.Combat.Corpse!.MapId);
        });

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        float first = await host.OnWorldAsync(() =>
        {
            Assert.Same(entrance, recovery.FindEntrance(host.Player, DungeonEntryScenario.Deadmines));
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WalkToEntrance, recovery.LastStep);
            Assert.Equal(new Vector3(entrance.X, entrance.Y, entrance.Z).X, recovery.RouteEnd!.Value.X, 1);
            return Vector2.Distance(new(host.Player.X, host.Player.Y), new(entrance.X, entrance.Y));
        });

        bool revived = false;
        for (int think = 0; think < 200 && !revived; think++)
        {
            await host.AdvanceAsync(500);
            revived = await host.OnWorldAsync(() =>
            {
                if (PlayerbotMovementControl.Update(host.Session, host.Player)) return false;
                if (host.Player.IsAlive) return true;
                PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs);
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                recovery.Update(host.Player, 500);
                Assert.False(recovery.UsingSpiritHealer, "the ghost gave up on its body");
                return false;
            });
        }

        Assert.True(revived, "the ghost was not revived");
        await host.AcknowledgeUntilAsync(p => p.MapId == DungeonEntryScenario.Deadmines, "the ghost lands inside");
        await host.OnWorldAsync(() =>
        {
            Assert.True(host.Player.IsAlive);
            Assert.Null(host.Player.Combat.Corpse);
            Assert.True(Vector2.Distance(new(host.Player.X, host.Player.Y), new(-16.4f, -383.07f)) < 1f);
        });
        Assert.True(first > 20f, $"started {first} yards from the entrance");
    }

    /// <summary>
    /// At its body, after the reclaim delay, a ghost does not reclaim while a hostile creature stands near it; once the camp has
    /// left, the ghost reclaims (mangoszero ReviveFromCorpseAction). The camp here is eight level-1 monsters (aggro radius 18 against
    /// the level-1 bot) on a circle of 15 yards round the body: every point inside the reclaim radius is within their reach and the
    /// margins, so there is no revive spot to walk to and the ghost waits, as it always did. (With one hostile only, it now walks
    /// to clear ground: the next test.) The circle was 22 yards under the flat 25-yard rule; with the creatures' own 18-yard radius
    /// a ghost 22 yards from them is no longer camped, so the camp moved in.
    /// </summary>
    [Fact]
    public async Task ACampOverTheWholeReclaimRadius_MeansWaiting_ThenTheGhostReclaimsWhenItLeaves()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await host.KillAndReleaseAsync();
        Creature[] camp = await host.OnWorldAsync(() => Enumerable.Range(0, 8).Select(i => DungeonBotHost.AddCreature(host.Player.Map!, 994310u + (uint)i,
            host.Player.X + (15f * MathF.Cos(i * MathF.PI / 4f)), host.Player.Y + (15f * MathF.Sin(i * MathF.PI / 4f)), host.Player.Z, 0,
            host.Host.World.NowMs, faction: 14)).ToArray()); // monsters
        await host.AcknowledgeUntilAsync(p => camp.All(c => p.VisibleObjects.Contains(c.Guid)), "the ghost sees the camp");
        clock.Seconds += 31;

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.False(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WaitForHostiles, recovery.LastStep);
            Assert.Null(recovery.ReviveSpot);
            Assert.Equal(4, host.Session.ManagedBudget.Remaining);
            Assert.False(host.Player.IsAlive);

            foreach (Creature wolf in camp) wolf.Relocate(host.Player.X + 45, host.Player.Y, host.Player.Z, 0, host.Host.World.NowMs);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.Reclaim, recovery.LastStep);
            Assert.True(host.Player.IsAlive);
        });
    }

    /// <summary>
    /// A hostile camps the ghost's revive point, but the reclaim radius is 39 yards and the rest of it is clear: the ghost walks to
    /// ground out of the hostile's aggro reach (its own radius, 18 yards for a level-1 monster against the level-1 bot, plus the
    /// margins; it was a flat 25 yards) and inside the radius, and reclaims there. Before, it stood still until the
    /// hostile left, and took the spirit healer after a minute (Mirthblade, two Frostmane Troll Whelps near its body).
    /// </summary>
    [Fact]
    public async Task AHostileNearTheBody_WithClearGroundInsideTheReclaimRadius_SendsTheGhostThere()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await host.KillAndReleaseAsync();
        Creature wolf = await host.OnWorldAsync(() => DungeonBotHost.AddCreature(host.Player.Map!, 994301,
            host.Player.X + 10, host.Player.Y, host.Player.Z, 0, host.Host.World.NowMs, faction: 14)); // a monster
        Vector3 body = await host.OnWorldAsync(() => new Vector3(host.Player.Combat.Corpse!.X, host.Player.Combat.Corpse.Y, host.Player.Combat.Corpse.Z));
        await host.AcknowledgeUntilAsync(p => p.VisibleObjects.Contains(wolf.Guid), "the ghost sees the creature");
        clock.Seconds += 31;

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        PlayerbotThreat threat = await host.OnWorldAsync(() => PlayerbotRecovery.Threats(host.Player).Single());
        Assert.Equal(18f, threat.Radius); // vmangos GetAttackDistance: detection 18, same level
        Vector3 spot = await host.OnWorldAsync(() =>
        {
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WalkToReviveSpot, recovery.LastStep);
            Assert.False(host.Player.IsAlive);
            return recovery.ReviveSpot!.Value;
        });
        Assert.False(threat.Reaches(spot, PlayerbotRecovery.CampMarginYards + PlayerbotRecovery.ReviveSpotSlackYards), "the spot is camped too");
        Assert.True(Vector3.Distance(spot, body) <= PlayerbotRecovery.ReviveSpotMaxYards, "the spot is outside the reclaim radius");

        bool revived = false;
        for (int think = 0; think < 100 && !revived; think++)
        {
            await host.AdvanceAsync(500);
            revived = await host.OnWorldAsync(() =>
            {
                if (PlayerbotMovementControl.Update(host.Session, host.Player)) return false;
                if (host.Player.IsAlive) return true;
                PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs);
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                recovery.Update(host.Player, 500);
                Assert.False(recovery.UsingSpiritHealer, "the ghost gave up on its body");
                return host.Player.IsAlive;
            });
        }

        Assert.True(revived, "the ghost never revived");
        await host.OnWorldAsync(() =>
        {
            var at = new Vector3(host.Player.X, host.Player.Y, host.Player.Z);
            Assert.False(threat.Reaches(at, PlayerbotRecovery.CampMarginYards), "revived inside the hostile's reach");
            Assert.True(Vector3.Distance(at, body) < CombatConstants.CorpseReclaimRadius, "revived outside the reclaim radius");
            Assert.Null(host.Player.Combat.Corpse);
        });
    }

    /// <summary>
    /// Only creatures that would attack the revived bot camp its body. A level-20 bot beside a level-1 monster 10 yards away (aggro
    /// radius 5: 18 less the 19-level difference, never under 5) and a same-level monster flagged NO_AGGRO 4 yards away (react
    /// state defensive: it never attacks on sight) reclaims where it stands. Under the flat 25-yard rule both camped it: it waited
    /// a minute and took the spirit healer.
    /// </summary>
    [Fact]
    public async Task CreaturesThatWouldNotAggroTheRevivedBot_DoNotCampItsBody()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() =>
        {
            DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock));
            host.Player.Level = 20;
        });
        await host.KillAndReleaseAsync();
        (Creature low, Creature passive) = await host.OnWorldAsync(() =>
        {
            Creature lowLevel = DungeonBotHost.AddCreature(host.Player.Map!, 994320, host.Player.X + 10, host.Player.Y, host.Player.Z, 0,
                host.Host.World.NowMs, faction: 14);
            var noAggro = new Creature(994321, new CreatureTemplate
            {
                Entry = 994321, Name = "no-aggro monster", CreatureType = 1, Faction = 14, MinLevel = 20, MaxLevel = 20,
                MinLevelHealth = 20, MaxLevelHealth = 20,
            }, null, CreatureContent.Empty, new Random(994321));
            noAggro.ReactState = CreatureReactState.Defensive; // what NO_AGGRO gives it (vmangos Creature::InitializeReactState)
            noAggro.Relocate(host.Player.X - 4, host.Player.Y, host.Player.Z, 0, host.Host.World.NowMs);
            host.Player.Map!.AddObject(noAggro);
            return (lowLevel, noAggro);
        });
        await host.AcknowledgeUntilAsync(p => p.VisibleObjects.Contains(low.Guid) && p.VisibleObjects.Contains(passive.Guid), "the ghost sees both");
        clock.Seconds += 31;

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            PlayerbotThreat only = Assert.Single(PlayerbotRecovery.Threats(host.Player)); // the NO_AGGRO one is no threat
            Assert.Equal(5f, only.Radius);
            Assert.False(PlayerbotRecovery.Camped(host.Player));
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.Reclaim, recovery.LastStep);
            Assert.True(host.Player.IsAlive);
        });
    }

    /// <summary>
    /// The hostile check is made where the ghost stands, because CMSG_RECLAIM_CORPSE revives the player there (vmangos
    /// MiscHandler.cpp:599, no relocation), and the walk stops as soon as the body is in reclaim range (about 39 yards). With the body
    /// 30 yards away: a creature 15 yards from the ghost (45 from the body) means waiting; one beside the body (35 yards from the
    /// ghost) does not. Before, both were measured from the body, the other way round. The creature 15 yards from the ghost now sends
    /// the ghost walking to clear ground instead of waiting (<see cref="Step.WalkToReviveSpot"/>); what the check pins is that it
    /// is not reclaimed from there.
    /// </summary>
    [Fact]
    public async Task TheHostileCheck_IsMadeAroundTheGhost_WhereTheReclaimRevivesIt()
    {
        var clock = new TestDeathClock(1_000);
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.OnWorldAsync(() => DeathHooks.Register(host.Host.World, new DeathHooks(new DeathOptions(), clock)));
        await host.KillAndReleaseAsync();
        Vector3 ghost = await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(host.Player.X + 30, host.Player.Y, host.Player.Z, 0);
            return new Vector3(host.Player.X, host.Player.Y, host.Player.Z);
        });
        Creature wolf = await host.OnWorldAsync(() => DungeonBotHost.AddCreature(host.Player.Map!, 994302,
            ghost.X - 15, ghost.Y, ghost.Z, 0, host.Host.World.NowMs, faction: 14)); // a monster behind the ghost
        await host.AcknowledgeUntilAsync(p => p.VisibleObjects.Contains(wolf.Guid), "the ghost sees the creature");
        clock.Seconds += 31;

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            recovery.Update(host.Player, 500);
            Assert.Equal(Step.WalkToReviveSpot, recovery.LastStep); // not Reclaim: the hostile is near the ghost
            Assert.False(host.Player.IsAlive);

            wolf.Relocate(ghost.X + 35, ghost.Y, ghost.Z, 0, host.Host.World.NowMs); // beside the body
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.Reclaim, recovery.LastStep);
            Assert.True(host.Player.IsAlive);
            Assert.True(Vector2.Distance(new(host.Player.X, host.Player.Y), new(ghost.X, ghost.Y)) < 1f, "revived away from where the ghost stood");
        });
    }

    /// <summary>
    /// A body that was never released (no ghost) and whose recovery counts as stalled faults with <see cref="PlayerbotRecovery.Stalled"/>
    /// (<see cref="Step.Fault"/>); the spirit-healer fallback is for ghosts only. Here the bot walked as a ghost and lost the ghost
    /// flag, so the stuck bound of the walk (<see cref="PlayerbotRecovery.StuckMs"/>) runs out well before the release bound
    /// (<see cref="PlayerbotRecovery.NoProgressMs"/>). Before, the Fault step fell through to the spirit-healer fallback.
    /// </summary>
    [Fact]
    public async Task AStalledBodyThatIsNoGhost_FaultsStalled_AndNeverTakesTheSpiritHealer()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(1, 100f, 100f, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(host.Player.X + 60, host.Player.Y, host.Player.Z, 0);
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            Assert.True(recovery.Update(host.Player, 500));
            Assert.Equal(Step.WalkToCorpse, recovery.LastStep);
            PlayerbotMovementControl.Stop(host.Session, host.Player);
        });
        await host.AdvanceAsync((uint)PlayerbotRecovery.StuckMs + 1_000);

        InvalidOperationException fault = await host.OnWorldAsync(() =>
        {
            host.Player.Flags &= ~PlayerFlags.Ghost;
            host.Session.ManagedBudget = new ManagedActionBudget(4);
            return Assert.Throws<InvalidOperationException>(() => recovery.Update(host.Player, 500));
        });

        Assert.Equal(PlayerbotRecovery.Stalled, fault.Message);
        Assert.Equal(Step.Fault, recovery.LastStep);
        Assert.False(recovery.UsingSpiritHealer);
    }

    /// <summary>
    /// A corpse run that cannot get anywhere (the body lies far below the ground) is given up after <see cref="PlayerbotRecovery.StuckMs"/>
    /// for the spirit healer in sight: the ghost walks to it and activates it through the ordinary handler. Nothing throws. Before,
    /// the stuck walk threw "playerbot-recovery-stuck" and the fault quarantined the bot.
    /// </summary>
    [Fact]
    public async Task AStalledCorpseRun_TakesTheSpiritHealer_AndNothingThrows()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        await host.TeleportAsync(0, DeadminesTestContent.Graveyard.X - 25f, DeadminesTestContent.Graveyard.Y, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        ObjectGuid healer = await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(corpse.X, corpse.Y, corpse.Z - 500f, 0);
            return host.Player.VisibleObjects.Select(g => host.Player.Map!.FindObject(g)).OfType<Creature>()
                .Single(c => (c.NpcFlags & (uint)NpcFlags.SpiritHealer) != 0).Guid;
        });

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        bool alive = false;
        long stuckAt = -1;
        var trail = new List<string>(); // the steps taken, for a failure message
        for (int think = 0; think < 200 && !alive; think++)
        {
            alive = await host.OnWorldAsync(() =>
            {
                PlayerbotMovementControl.Update(host.Session, host.Player);
                if (host.Player.IsAlive) return true;
                PlayerbotMotion.Pump(host.Session, host.Player, host.Host.World.NowMs);
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                try { recovery.Update(host.Player, 500); }
                catch (InvalidOperationException error) { trail.Add(error.Message); throw new InvalidOperationException(string.Join(" -> ", trail), error); }
                string step = $"{recovery.LastStep}/{recovery.LastSpiritHealerStep} at ({host.Player.X:F1}, {host.Player.Y:F1}, {host.Player.Z:F1})";
                if (trail.Count == 0 || !trail[^1].StartsWith($"{recovery.LastStep}/{recovery.LastSpiritHealerStep} ", StringComparison.Ordinal))
                    trail.Add(step);
                if (stuckAt < 0 && recovery.UsingSpiritHealer) stuckAt = (long)host.Host.World.Uptime.TotalMilliseconds;
                return host.Player.IsAlive;
            });
            if (!alive) await host.AdvanceAsync(500);
        }

        Assert.True(alive, "still a ghost: " + string.Join(" -> ", trail));
        Assert.True(stuckAt > 0, "the spirit healer was never chosen");
        await host.OnWorldAsync(() =>
        {
            Creature spirit = (Creature)host.Player.Map!.FindObject(healer)!;
            Assert.True(Vector3.Distance(new(spirit.X, spirit.Y, spirit.Z), new(host.Player.X, host.Player.Y, host.Player.Z)) <= 5f);
            Assert.Null(host.Player.Combat.Corpse);
        });
    }

    /// <summary>
    /// A ghost with no way back and no spirit healer or graveyard anywhere faults — but only once the fallback has failed too, with
    /// its own code; before the stuck bound it keeps trying.
    /// </summary>
    [Fact]
    public async Task WithoutAnySpiritHealer_TheRecoveryFaultsOnlyWhenTheFallbackFails()
    {
        await using DungeonBotHost host = await DungeonBotHost.StartAsync();
        // Kalimdor: no graveyard, no healer, no trigger.
        await host.TeleportAsync(1, 100f, 100f, DeadminesTestContent.OutsideFloor);
        await host.KillAndReleaseAsync();
        await host.OnWorldAsync(() =>
        {
            Corpse corpse = host.Player.Combat.Corpse!;
            corpse.SetPosition(corpse.X, corpse.Y, corpse.Z - 500f, 0);
        });

        var recovery = new PlayerbotRecovery(host.Session, new PlayerbotOptions { Enabled = true });
        InvalidOperationException? fault = null;
        long faultAt = 0, start = (long)host.Host.World.Uptime.TotalMilliseconds;
        for (int think = 0; think < 60 && fault is null; think++)
        {
            fault = await host.OnWorldAsync(() =>
            {
                host.Session.ManagedBudget = new ManagedActionBudget(4);
                try { recovery.Update(host.Player, 500); return null; }
                catch (InvalidOperationException error) { return error; }
            });
            faultAt = (long)host.Host.World.Uptime.TotalMilliseconds;
            if (fault is null) await host.AdvanceAsync(500);
        }

        Assert.NotNull(fault);
        Assert.Equal(PlayerbotRecovery.SpiritHealerFailed, fault!.Message);
        Assert.True(faultAt - start > PlayerbotRecovery.StuckMs, $"faulted after {faultAt - start} ms");
    }

    private sealed class TestDeathClock(long seconds) : DeathClock
    {
        public long Seconds { get; set; } = seconds;
        public override long UnixSeconds => Seconds;
    }
}
