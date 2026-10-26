using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Protocol;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Npc;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.World.Features;
using ArcaneCore.World.Net;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Tests.Items;
using ArcaneCore.World.Tests.Playerbots.Risk;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The outcome checks of destination picking, the walk home and the town visit: a destination on the wrong map or in an enemy's town,
/// a trip that never arrives, a home that is invalid or never reached, a home teleport refused for a setup fault, and a repair NPC that
/// is not where its spawn says. Each key is proved both ways.
/// </summary>
public sealed class PlayerbotDestinationAnomalyTests
{
    private static readonly FactionTemplateCatalog Factions = new(
    [
        new FactionTemplateRecord(1, 1, 0, OwnMask: 3, FriendlyMask: 2, HostileMask: 12),   // human player
        new FactionTemplateRecord(14, 14, 0, OwnMask: 8, FriendlyMask: 0, HostileMask: 1),  // monster: hostile to the player
        new FactionTemplateRecord(12, 12, 0, OwnMask: 2, FriendlyMask: 1, HostileMask: 0),  // friendly town folk
    ]);

    private static CreatureSpawn Spawn(uint map, float x = 1f, float y = 2f, float z = 3f)
        => new() { Guid = 77, Entry = 5, MapId = map, X = x, Y = y, Z = z };

    // --- destination faults (pure) ------------------------------------------------------------------------------

    [Fact]
    public void ASpawnOnAnotherMap_IsFaulted_OnTheSameMap_NotFaulted()
    {
        Assert.Equal("wrong-map", PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(1), 12, Factions, wantsFriendly: true));
        Assert.Null(PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(0), 12, Factions, wantsFriendly: true));
    }

    [Fact]
    public void ASpawnWithNoPlace_IsFaulted()
    {
        Assert.Equal("non-finite", PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(0, x: float.NaN), 12, Factions, wantsFriendly: false));
    }

    [Fact]
    public void AnEnemyNpc_IsFaulted_ForATrip_ButNotWhenHunting_AndNotAFriendlyOne()
    {
        Assert.Equal("hostile-faction", PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(0), 14, Factions, wantsFriendly: true));
        Assert.Null(PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(0), 14, Factions, wantsFriendly: false)); // a kill target is hostile by design
        Assert.Null(PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(0), 12, Factions, wantsFriendly: true));
        Assert.Null(PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(0), 14, null, wantsFriendly: true)); // no catalog: no verdict
        Assert.Null(PlayerbotNavigationAnomalies.DestinationFault(0, 1, Spawn(0), 0, Factions, wantsFriendly: true)); // unresolved template: no verdict
    }

    [Fact]
    public async Task AFaultedDestination_IsReported_UnderTheDestinationArea()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            PlayerbotNavigationAnomalies.ReportDestination(world.Player, "wrong-map", 5, Spawn(1));
            Assert.True(cap.Any(PlayerbotAnomalies.Key("destination", "wrong-map")), cap.Describe());
            return true;
        });
    }

    // --- destination/not-reached --------------------------------------------------------------------------------

    private static readonly string NotReached = PlayerbotAnomalies.Key("destination", "not-reached");

    [Fact]
    public async Task ATripPastItsBudget_IsReportedOnce()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            var trip = new PlayerbotNavigationAnomalies.TripWatch();
            trip.Note(world.Player, 5, 77, 1_000);
            trip.Note(world.Player, 5, 77, 1_000 + PlayerbotNavigationAnomalies.TripBudgetMs - 1);
            Assert.False(cap.Any(NotReached), cap.Describe());
            trip.Note(world.Player, 5, 77, 1_000 + PlayerbotNavigationAnomalies.TripBudgetMs);
            trip.Note(world.Player, 5, 77, 1_000 + PlayerbotNavigationAnomalies.TripBudgetMs + 60_000);
            Assert.Equal(1, cap.Count(NotReached));
            return true;
        });
    }

    [Fact]
    public async Task ATripThatChangesDestination_OrEnds_StartsItsBudgetAgain()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            var trip = new PlayerbotNavigationAnomalies.TripWatch();
            uint budget = PlayerbotNavigationAnomalies.TripBudgetMs;
            trip.Note(world.Player, 5, 77, 0);
            trip.Note(world.Player, 6, 78, budget - 1_000); // another destination: a new trip
            trip.Note(world.Player, 6, 78, budget + 500);   // 1.5 s into it
            trip.Clear();                                   // arrived
            trip.Note(world.Player, 6, 78, budget + 600_000);
            trip.Note(world.Player, 6, 78, budget + 600_000 + budget - 1);
            Assert.False(cap.Any(NotReached), cap.Describe());
            return true;
        });
    }

    // --- home ---------------------------------------------------------------------------------------------------

    private static string HomeKey(string code) => PlayerbotAnomalies.Key("home", code);

    private static PlayerbotHomeArea Home(uint map, Vector3 centre, float radius) => new(map, centre, radius, "test home");

    [Theory]
    [InlineData(float.NaN, 100f)]
    [InlineData(10f, 0f)]
    [InlineData(10f, float.NaN)]
    public async Task AnInvalidHome_IsReportedOnce(float centreX, float radius)
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            var homeReturn = new PlayerbotHomeReturn();
            PlayerbotHomeArea area = Home(world.Player.MapId, new Vector3(centreX, 10f, 10f), radius);
            homeReturn.Evaluate(world.Player, area, 1_000);
            homeReturn.Evaluate(world.Player, area, 2_000);
            Assert.Equal(1, cap.Count(HomeKey("invalid")));
            return true;
        });
    }

    [Fact]
    public async Task AValidHome_IsNotReported()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Player player = world.Player;
            new PlayerbotHomeReturn().Evaluate(player, Home(player.MapId, new Vector3(player.X + 500f, player.Y, player.Z), 100f), 1_000);
            Assert.False(cap.Any(HomeKey("invalid")), cap.Describe());
            return true;
        });
    }

    [Fact]
    public async Task AReturnThatNeverArrives_IsReportedOnceAfterItsBudget()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Player player = world.Player;
            var homeReturn = new PlayerbotHomeReturn();
            PlayerbotHomeArea area = Home(player.MapId, new Vector3(player.X + 600f, player.Y, player.Z), 100f);
            Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, area, 1_000));
            homeReturn.Evaluate(player, area, 1_000 + PlayerbotHomeReturn.ReturnBudgetMs);
            Assert.False(cap.Any(HomeKey("return-timeout")), cap.Describe());
            homeReturn.Evaluate(player, area, 1_001 + PlayerbotHomeReturn.ReturnBudgetMs);
            homeReturn.Evaluate(player, area, 90_000 + PlayerbotHomeReturn.ReturnBudgetMs);
            Assert.Equal(1, cap.Count(HomeKey("return-timeout")));
            return true;
        });
    }

    [Fact]
    public async Task AReturnThatArrives_OrIsOffMap_IsNeverTimedOut()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Player player = world.Player;
            var homeReturn = new PlayerbotHomeReturn();
            PlayerbotHomeArea far = Home(player.MapId, new Vector3(player.X + 600f, player.Y, player.Z), 100f);
            homeReturn.Evaluate(player, far, 1_000);
            // Home reached, long after the walk began: arrival ends the episode, a later walk home starts its own clock.
            PlayerbotHomeArea here = Home(player.MapId, new Vector3(player.X, player.Y, player.Z), 100f);
            Assert.Equal(PlayerbotHomeState.Inside, homeReturn.Evaluate(player, here, 1_000 + PlayerbotHomeReturn.ReturnBudgetMs + 5_000));
            homeReturn.Evaluate(player, far, 2_000 + PlayerbotHomeReturn.ReturnBudgetMs + 5_000);
            // A home on another map is the off-map state, which waits for an operator, not a walk.
            PlayerbotHomeArea other = Home(player.MapId + 1, new Vector3(0, 0, 0), 100f);
            Assert.Equal(PlayerbotHomeState.OffMap, homeReturn.Evaluate(player, other, 3_000 + 3 * PlayerbotHomeReturn.ReturnBudgetMs));
            Assert.False(cap.Any(HomeKey("return-timeout")), cap.Describe());
            return true;
        });
    }

    /// <summary>
    /// Bot QA #818: a town home with <c>TownProgressionTeleport</c> off is walked from up to 20000 yards, so a bot 3000 yards away
    /// (Stormwind, home Kharanos) is inside the walk range and Auto keeps choosing a walk. Fifteen minutes later it is still there.
    /// Past <see cref="PlayerbotHomeReturn.ReturnBudgetMs"/> that walk is given up: Auto teleports (the same reason as a blocked walk).
    /// Walk mode never teleports. The timeout is reported once.
    /// </summary>
    [Fact]
    public async Task ATownReturnPastItsBudget_IsGivenUpForATeleport_AndReportedOnce()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Player player = world.Player;
            var homeReturn = new PlayerbotHomeReturn();
            var area = new PlayerbotHomeArea(player.MapId, new Vector3(player.X + 3_000f, player.Y, player.Z), 100f,
                "town:kharanos;m=stormwind;s=kharanos")
            {
                WalkRangeYards = 20_000f,
                TownTeleports = false,
            };
            var auto = new PlayerbotHomeOptions { ReturnMode = PlayerbotHomeReturnMode.Auto, TeleportDistanceYards = 1_500f };
            var walk = new PlayerbotHomeOptions { ReturnMode = PlayerbotHomeReturnMode.Walk, TeleportDistanceYards = 1_500f };
            Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, area, 1_000));
            Assert.Equal(PlayerbotHomeTeleportReason.None, homeReturn.Judge(player, area, auto));
            homeReturn.Evaluate(player, area, 1_000 + PlayerbotHomeReturn.ReturnBudgetMs);
            Assert.Equal(PlayerbotHomeTeleportReason.None, homeReturn.Judge(player, area, auto));
            Assert.False(cap.Any(HomeKey("return-timeout")), cap.Describe());

            Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, area, 1_001 + PlayerbotHomeReturn.ReturnBudgetMs));
            Assert.Equal(PlayerbotHomeTeleportReason.WalkBlocked, homeReturn.Judge(player, area, auto));
            Assert.Equal(PlayerbotHomeTeleportReason.None, homeReturn.Judge(player, area, walk));
            homeReturn.Evaluate(player, area, 90_000 + PlayerbotHomeReturn.ReturnBudgetMs);
            homeReturn.Judge(player, area, auto);
            Assert.Equal(1, cap.Count(HomeKey("return-timeout")));
            return true;
        });
    }

    [Fact]
    public async Task AReturnThatMakesDistanceProgress_OrIsDeferredByErrands_IsNotTimedOut()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(world.Player);
            Player player = world.Player;
            var homeReturn = new PlayerbotHomeReturn();
            var area = new PlayerbotHomeArea(player.MapId, new Vector3(player.X + 4_000f, player.Y, player.Z), 100f, "zone:elwynn")
            {
                WalkRangeYards = 20_000f,
            };

            // 1. While Evaluate is ticked at think cadence without Update (errandsFirst deferring the walk home),
            //    return-timeout does not fire even past ReturnBudgetMs.
            uint now = 1_000;
            for (uint step = 0; step <= PlayerbotHomeReturn.ReturnBudgetMs + 60_000; step += 1_000)
            {
                now = 1_000 + step;
                Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, area, now));
            }
            Assert.False(cap.Any(HomeKey("return-timeout")), cap.Describe());

            // 2. Once the bot begins actively returning (Judge/Update called) and closes >= 25 yards every few minutes,
            //    a multi-budget cross-zone walk does not time out until progress actually stalls for ReturnBudgetMs.
            float startX = player.X;
            var walk = new PlayerbotHomeOptions { ReturnMode = PlayerbotHomeReturnMode.Walk };
            for (int leg = 1; leg <= 10; leg++)
            {
                now += 200_000;
                player.SetPosition(startX + (leg * 50f), player.Y, player.Z, player.Orientation);
                Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, area, now));
                homeReturn.Judge(player, area, walk);
            }
            Assert.False(cap.Any(HomeKey("return-timeout")), cap.Describe());

            // Stalling in place past ReturnBudgetMs after Update was active still reports return-timeout once.
            now += PlayerbotHomeReturn.ReturnBudgetMs + 1_000;
            Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, area, now));
            Assert.Equal(1, cap.Count(HomeKey("return-timeout")));
            return true;
        });
    }

    [Fact]
    public async Task AReturnDeferredAfterOneActiveStep_OrBlockedOnTownWalk_DoesNotReportAnomaly()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            WorldCollision.Of(world.Host.World).Install(pathfinder: new Canned((start, _) => PathResult.None(start)));
            using var cap = PlayerbotAnomalies.CaptureFor(player);
            var homeReturn = new PlayerbotHomeReturn();
            var townArea = new PlayerbotHomeArea(player.MapId, new Vector3(player.X + 2_000f, player.Y, player.Z), 100f, "town:goldshire")
            {
                WalkRangeYards = 20_000f,
            };
            var auto = new PlayerbotHomeOptions { ReturnMode = PlayerbotHomeReturnMode.Auto, TeleportDistanceYards = 1_500f };

            // 1. One active step (Judge called) followed by 20 minutes of think-cadence Evaluate without Judge/Update
            //    (bot doing errands/quests outside home) does not fire return-timeout.
            uint now = 1_000;
            Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, townArea, now));
            homeReturn.Judge(player, townArea, auto);
            for (uint step = 500; step <= PlayerbotHomeReturn.ReturnBudgetMs + 120_000; step += 500)
            {
                now = 1_000 + step;
                Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, townArea, now));
            }
            Assert.False(cap.Any(HomeKey("return-timeout")), cap.Describe());

            // 2. Three refused path plans on a town home (WalkRangeYards > 0) transition to Blocked + WalkBlocked
            //    so Auto teleports home, without reporting bot-home/walk-blocked.
            for (int i = 0; i < PlayerbotHomeReturn.MaxFailures; i++)
            {
                now += 500;
                homeReturn.Evaluate(player, townArea, now);
                homeReturn.Update(world.Session, player, townArea, world.Options, 500, now);
            }
            Assert.Equal(PlayerbotHomeState.Blocked, homeReturn.State);
            Assert.True(homeReturn.WalkBlocked);
            Assert.Equal(PlayerbotHomeTeleportReason.WalkBlocked, homeReturn.Judge(player, townArea, auto));
            Assert.False(cap.Any(HomeKey("walk-blocked")), cap.Describe());
            return true;
        });
    }

    private sealed class Canned(Func<Vector3, Vector3, PathResult> answer) : IPathfinder
    {
        public bool Enabled => true;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => start == end ? PathResult.None(start) : answer(start, end);
    }

    [Fact]
    public async Task AWalkHomeThatFailsRepeatedly_IsReportedWhenSetAside_NotBefore()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            WorldCollision.Of(world.Host.World).Install(pathfinder: new Canned((start, _) => PathResult.None(start)));
            using var cap = PlayerbotAnomalies.CaptureFor(player);
            var homeReturn = new PlayerbotHomeReturn();
            PlayerbotHomeArea area = Home(player.MapId, new Vector3(player.X + 500f, player.Y, player.Z), 100f);
            Assert.Equal(PlayerbotHomeState.Returning, homeReturn.Evaluate(player, area, 1_000));
            for (int i = 0; i < PlayerbotHomeReturn.MaxFailures - 1; i++)
                homeReturn.Update(world.Session, player, area, world.Options, 500, 1_000 + (uint)(i * 500));
            Assert.False(cap.Any(HomeKey("walk-blocked")), cap.Describe());
            homeReturn.Update(world.Session, player, area, world.Options, 500, 3_000);
            Assert.Equal(PlayerbotHomeState.Blocked, homeReturn.State);
            Assert.True(cap.Any(HomeKey("walk-blocked")), cap.Describe());
            return true;
        });
    }

    [Fact]
    public async Task ATeleportHomeRefusedForTheSetup_IsReported_ButNotOneRefusedForTheBotsState()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            using var cap = PlayerbotAnomalies.CaptureFor(player);

            // In a fight: told no for a good reason, nothing to report.
            Creature mob = world.Spawn(RiskTestWorld.Template(9101), 5f, 0f);
            world.Engage(mob);
            Assert.True(player.Combat.IsInCombat);
            var inFight = new PlayerbotHomeReturn();
            PlayerbotHomeArea unknownMap = Home(999, new Vector3(10f, 10f, 10f), 100f);
            inFight.Evaluate(player, unknownMap, 1_000);
            Assert.False(inFight.TryTeleport(world.Session, player, unknownMap, world.Options, PlayerbotHomeTeleportReason.Distance, 1_000));
            Assert.False(cap.Any(HomeKey("teleport-refused")), cap.Describe());
            return true;
        });
    }

    [Fact]
    public async Task ATeleportHomeToAMapTheServerDoesNotKnow_IsReported()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            using var cap = PlayerbotAnomalies.CaptureFor(player);
            var homeReturn = new PlayerbotHomeReturn();
            PlayerbotHomeArea unknownMap = Home(999, new Vector3(10f, 10f, 10f), 100f);
            homeReturn.Evaluate(player, unknownMap, 1_000);
            Assert.False(homeReturn.TryTeleport(world.Session, player, unknownMap, world.Options, PlayerbotHomeTeleportReason.Distance, 1_000));
            Assert.True(cap.Any(HomeKey("teleport-refused")), cap.Describe());
            return true;
        });
    }

    // --- town/service-npc-missing -------------------------------------------------------------------------------

    private static readonly string NpcMissing = PlayerbotAnomalies.Key("town", "service-npc-missing");

    private sealed class FlatLos : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 83.53f;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }
    }

    private sealed class ManualClock : IWorldFeature
    {
        public void Attach(WorldRuntime world) => world.UseManualClock();
    }

    /// <summary>A bot with broken boots and money, ten repair NPCs far east (<see cref="ManyRepairFixture"/>) all reachable; <paramref name="body"/> runs on the world thread.</summary>
    private static async Task RepairTrip(Action<Player, Func<Player>, Action> body, Action<PlayerbotTownGoals>? configure = null)
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 9212, Name = "boots", Class = (uint)ItemClass.Armor, SubClass = ItemSubClasses.ArmorMisc,
            InventoryType = (uint)InventoryType.Feet, Armor = 10, MaxDurability = 20, ItemLevel = 10, Quality = 1, Stackable = 1,
            AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
        });
        using IDisposable scope = items.Use();
        ManyRepairServices.Current.Value = new ManyRepairFixture();
        try
        {
            await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            {
                services.AddSingleton(new RepairCostTable([(10u, Enumerable.Repeat(1u, RepairCostTable.MultiplierCount).ToArray())], [(4u, 1f)]));
                services.AddSingleton<IWorldFeature, ManualClock>();
            });
            WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
            var goals = new PlayerbotTownGoals(session, new PlayerbotOptions { Enabled = true });
            configure?.Invoke(goals);
            try
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    WorldCollision.Of(host.World).Install(lineOfSight: new FlatLos(), pathfinder: new ScriptedPathfinder { StartOnMesh = true, Reaches = _ => true });
                    PlayerbotEquipmentTests.EquipFromBags(player, 9212);
                    player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.Feet)!.Durability = 0;
                    player.Money = 100;
                    body(player, () => player, () =>
                    {
                        goals.BeginThink();
                        PlayerbotMotion.ElapseForTests(player, 500);
                        session.ManagedBudget = new ManagedActionBudget(1);
                        goals.Update(player, 500);
                    });
                    return true;
                });
            }
            finally { session.Kick(); await session.ManagedClosed; }
        }
        finally { ManyRepairServices.Current.Value = null; }
    }

    [Fact]
    public async Task ArrivingAtARepairSpawnWithNoNpcInSight_IsReported_OnceForTheTrip()
    {
        await RepairTrip((player, _, think) =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(player);
            think(); // plans the walk to the nearest spawn, 130 yards east
            Assert.False(cap.Any(NpcMissing), cap.Describe());
            player.Relocate(ScriptedPathfinder.SpawnX(0), player.Y, player.Z, player.Orientation, 0);
            think();
            think();
            Assert.Equal(1, cap.Count(NpcMissing));
        });
    }

    /// <summary>
    /// The entry of the NPC a bot found missing is set aside for that bot (<see cref="PlayerbotSuspensions.SuspendEntryEscalating"/>), so
    /// it does not walk back to the same empty spawn when the trip rests and its blocked spawns are forgotten (Timbermaw Hold, issues
    /// #1478 and #1479: 13 service-npc-missing lines for vendors 11557 and 11555 in a day).
    /// </summary>
    [Fact]
    public async Task ArrivingAtARepairSpawnWithNoNpcInSight_SetsTheEntryAside()
    {
        var suspensions = new PlayerbotSuspensions();
        await RepairTrip((player, _, think) =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(player);
            think();
            Assert.False(suspensions.IsEntrySuspended(950, 0));
            player.Relocate(ScriptedPathfinder.SpawnX(0), player.Y, player.Z, player.Orientation, 0);
            think();
            Assert.Equal(1, cap.Count(NpcMissing));
            Assert.True(suspensions.IsEntrySuspended(950, 0), "the entry of the missing NPC was not set aside");
        }, goals => goals.Suspensions = suspensions);
    }

    [Fact]
    public async Task WalkingToARepairSpawn_StillFarFromIt_IsNotReported()
    {
        await RepairTrip((player, _, think) =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(player);
            think();
            player.Relocate(ScriptedPathfinder.SpawnX(0) - 40f, player.Y, player.Z, player.Orientation, 0);
            think();
            think();
            Assert.False(cap.Any(NpcMissing), cap.Describe());
        });
    }

    [Fact]
    public async Task ArrivingAtARepairSpawn_WhenLiveCreatureExistsOnMap_DoesNotReportServiceNpcMissing()
    {
        await RepairTrip((player, _, think) =>
        {
            using var cap = PlayerbotAnomalies.CaptureFor(player);
            think();
            float spawnX = ScriptedPathfinder.SpawnX(0);
            Creature live = Dungeon.DungeonBotHost.AddCreature(
                player.Map!, 950u, spawnX, player.Y, player.Z,
                (uint)ArcaneCore.Game.Npc.NpcFlags.Repair, 0, faction: 1);
            player.VisibleObjects.Add(live.Guid);
            player.Relocate(spawnX, player.Y, player.Z, player.Orientation, 0);
            think();
            think();
            Assert.False(cap.Any(NpcMissing), cap.Describe());
        });
    }

    [Fact]
    public async Task BrokenGear_WhenRepairTripIsBackedOff_DoesNotReportBrokenUnrepaired()
    {
        var items = new ItemTestContent();
        items.Templates.Templates.Add(new ItemTemplate
        {
            Entry = 9213, Name = "sword", Class = (uint)ItemClass.Weapon, SubClass = 7,
            InventoryType = (uint)InventoryType.Weapon, MaxDurability = 20, ItemLevel = 5, Quality = 1, Stackable = 1,
            AllowableClass = uint.MaxValue, AllowableRace = uint.MaxValue,
        });
        using IDisposable scope = items.Use();
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
            services.AddSingleton<IWorldFeature, ManualClock>());
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            bool tripDue = false;
            var equipment = new PlayerbotEquipment(session) { RepairTripDue = _ => tripDue };
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                PlayerbotEquipmentTests.EquipFromBags(player, 9213);
                player.Inventory.GetItem(InventorySlots.Bag0, InventorySlots.MainHand)!.Durability = 0;
                equipment.Update(player);
                return true;
            });
            await host.World.AdvanceClockAsync(PlayerbotEquipment.BrokenGearMs + 60_000);
            await host.OnWorldAsync(() =>
            {
                Player player = session.Player!;
                using var cap = PlayerbotAnomalies.CaptureFor(player);
                equipment.Update(player);
                Assert.False(cap.Any(PlayerbotAnomalies.Key("gear", "broken-unrepaired")), cap.Describe());
                return true;
            });
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    [Fact]
    public async Task GiveUpStalledGoal_OnExploreAndCombatTargets_SuspendsEntryAndClearsTargetEntry()
    {
        await using RiskTestWorld world = await RiskTestWorld.StartAsync();
        await world.OnWorldAsync(() =>
        {
            Player player = world.Player;
            PlayerbotBrain brain = world.Brain;

            // 1. Stall on Explore with TargetEntry = 1397
            brain.HoldGoalForTests(PlayerbotGoalKind.Explore, 1397, 0);
            brain.GiveUpStalledGoal(player);
            Assert.True(brain.IsEntrySetAside(1397));
            Assert.Equal(0u, brain.TargetEntry);

            // 2. Stall on Combat with TargetEntry = 199
            brain.HoldGoalForTests(PlayerbotGoalKind.Combat, 199, 0);
            brain.GiveUpStalledGoal(player);
            Assert.True(brain.IsEntrySetAside(199));
            Assert.Equal(0u, brain.TargetEntry);
            return true;
        });
    }

    [Fact]
    public async Task HuntTrip_WithFourSecondReplanGaps_MaintainsNotClosingClock_AndSuspendsSingleSpawnEntry()
    {
        const float startX = -8949.95f;
        var content = new CreatureContent(
            [new CreatureTemplate { Entry = 3100, Name = "boar", DisplayIds = [49] }],
            [new CreatureSpawn { Guid = 588, Entry = 3100, MapId = 0, X = startX + 200f, Y = -132.49f, Z = 83.53f }], [], [], []);
        await using WorldTestHost host = WorldTestHost.Start(configureServices: services =>
        {
            services.AddSingleton<ICreatureDataStore>(new SingleSpawnContent(content));
            services.AddSingleton<IWorldFeature, ManualClock>();
        });
        WorldSession session = await PlayerbotMovementControlTests.EnterAsync(host);
        try
        {
            var suspensions = new PlayerbotSuspensions();
            var destinations = await host.OnWorldAsync(() =>
            {
                WorldCollision.Of(host.World).Install(
                    lineOfSight: new FlatLos(),
                    pathfinder: new Canned((start, _) => PathResult.StraightLine(start, start + new Vector3(0f, 1f, 0f), PathType.Normal)));
                return new PlayerbotWorldDestinations(session, new PlayerbotOptions { Enabled = true }) { Suspensions = suspensions };
            });

            // Step in 4,000 ms increments (> old 3,000 ms BackoffMs + 1,000 reset window)
            const uint step = 4_000;
            for (uint elapsed = 0; elapsed <= PlayerbotWorldDestinations.SpawnNotClosingMs + step; elapsed += step)
            {
                await host.OnWorldAsync(() =>
                {
                    Player player = session.Player!;
                    session.ManagedBudget = new ManagedActionBudget(1);
                    destinations.Update(player, 3100, step);
                    PlayerbotMotion.ElapseForTests(player, step);
                    return true;
                });
                await host.World.AdvanceClockAsync(step);
            }

            bool suspended = await host.OnWorldAsync(() => suspensions.IsEntrySuspended(3100, host.World.NowMs));
            Assert.True(suspended, "Single-spawn hunt target that never closed across 4s replan gaps should be suspended");
        }
        finally { session.Kick(); await session.ManagedClosed; }
    }

    private sealed class SingleSpawnContent(CreatureContent content) : ICreatureDataStore
    {
        public Task<CreatureContent> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(content);
    }
}
