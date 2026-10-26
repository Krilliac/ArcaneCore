using System.Numerics;
using ArcaneCore.Game;
using ArcaneCore.Game.Battlegrounds;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using ArcaneCore.World.Playerbots.Battlegrounds;
using ArcaneCore.World.Tests.Playerbots.Scenarios;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ArcaneCore.World.Playerbots.Scenarios;
using Content = ArcaneCore.World.Tests.Playerbots.Battlegrounds.BattlegroundBotTestContent;

namespace ArcaneCore.World.Tests.Playerbots.Battlegrounds;

/// <summary>
/// Issue #1486 (bot-bg/objective-unreachable): the Warsong Gulch midfield the bots walk to is a point the mesh accepted (never the raw
/// midpoint, never kept while tiles are pending), the battlemaster a bot walks to is one it can plan a route to, and the walk's timeout
/// grows with its length.
/// </summary>
public sealed class PlayerbotBattlegroundObjectiveReachTests : IDisposable
{
    private static readonly Vector3 OwnStand = new(1539.5f, 1481.2f, 352.0f);
    private static readonly Vector3 EnemyStand = new(917.4f, 1434.0f, 345.0f);

    private readonly WorldRuntime _world = new(new WorldRuntimeOptions { TickIntervalMs = 5, AutosaveIntervalMs = 0 }, new NullSaveQueue(), NullLogger<WorldRuntime>.Instance);

    public void Dispose() => _world.Dispose();

    // --- (a) the midfield ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ResolveWarsongMidfield_WhenNoPointAroundTheMiddleIsWalkable_NeverReturnsTheOffMeshMiddle()
    {
        var map = _world.GetMap(489);
        Vector3 midpoint = (OwnStand + EnemyStand) / 2f;
        // The unwalkable disc reaches past the ring search (28 yards), so no ring point passes: the answer must still be on the mesh.
        var mesh = new MidfieldMesh(midpoint, discRadius: 60f, floorZ: 308f, tilesPending: false);
        WorldCollision.Of(_world).Install(lineOfSight: mesh, pathfinder: mesh);

        Vector3 resolved = PlayerbotBattlegroundCoordinator.ResolveWarsongMidfield(map, OwnStand, EnemyStand, out bool settled);

        Assert.True(PlayerbotNavigation.OnWalkableMesh(map, resolved), $"the midfield {resolved} is off the walkable mesh");
        Assert.True(Vector2.Distance(new Vector2(resolved.X, resolved.Y), new Vector2(midpoint.X, midpoint.Y)) >= 60f, $"{resolved} is inside the unwalkable disc");
        Assert.False(settled, "the answer is a fallback (the walkable check reads no tile, so a tile not resident answers like a hole): it is asked again, not cached");
    }

    [Fact]
    public void ResolveWarsongMidfield_WhileTilesArePending_IsNotSettled_SoItIsNotKept()
    {
        var map = _world.GetMap(489);
        Vector3 midpoint = (OwnStand + EnemyStand) / 2f;
        var mesh = new MidfieldMesh(midpoint, discRadius: 0f, floorZ: 308f, tilesPending: true);
        WorldCollision.Of(_world).Install(lineOfSight: mesh, pathfinder: mesh);

        Vector3 resolved = PlayerbotBattlegroundCoordinator.ResolveWarsongMidfield(map, OwnStand, EnemyStand, out bool settled);

        Assert.False(settled, "a midfield resolved while a tile is still being read must be asked again, not cached");
        Assert.True(float.IsFinite(resolved.X) && float.IsFinite(resolved.Y) && float.IsFinite(resolved.Z));
    }

    [Fact]
    public void ResolveWarsongMidfield_OnWalkableGround_IsSettled()
    {
        var map = _world.GetMap(489);
        Vector3 midpoint = (OwnStand + EnemyStand) / 2f;
        var mesh = new MidfieldMesh(midpoint, discRadius: 0f, floorZ: 308f, tilesPending: false);
        WorldCollision.Of(_world).Install(lineOfSight: mesh, pathfinder: mesh);

        Vector3 resolved = PlayerbotBattlegroundCoordinator.ResolveWarsongMidfield(map, OwnStand, EnemyStand, out bool settled);

        Assert.True(settled);
        Assert.True(PlayerbotNavigation.OnWalkableMesh(map, resolved));
    }

    // --- (b) the battlemaster ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The pick of a battlemaster is by the region graph alone (<see cref="PlayerbotNavigation.IsInOtherWalkingRegion"/>, tested on the
    /// synthetic mesh in <see cref="PlayerbotBattlegroundRegionTests"/>): no path search runs for it, and a master the exact planner would
    /// refuse is still found, because the walk itself goes by legs and a failing walk refuses the master for the zone.
    /// </summary>
    [Fact]
    public async Task FindMaster_RunsNoPathSearch_AndIsNotRefusedByAnExactRoutePlan()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await PlayerbotBattlegroundWorldTests.RunAsync(world, "bg-master-no-search", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmnosrch", 1, characterClass: 1);
            PlayerbotBattlegroundCoordinator coordinator = PlayerbotBattlegroundWorldTests.Coordinator(context);
            await context.ReadAsync(() =>
            {
                Player player = ally.RequirePlayer();
                var counting = new RefusingPathfinder(Content.MasterAt(Content.AllianceWsgMaster));
                WorldCollision.Of(context.World).Install(pathfinder: counting);
                (uint SpawnGuid, Vector3 At)? found = coordinator.FindMasterForTests(player, BattlegroundType.WarsongGulch);
                ScenarioContext.Expect(found is not null, "a battlemaster beside the start is found although no exact route reaches it");
                ScenarioContext.ExpectEqual(0, counting.Searches, "path searches by the pick");
                return true;
            });
        });
    }

    /// <summary>
    /// The Darnassus emissary (14991, game event 20) walks waypoints away from its spawn point: the walk follows the live creature, not the
    /// spawn, so the bot reaches it where it stands.
    /// </summary>
    [Fact]
    public async Task Travel_FollowsABattlemasterThatWalkedAwayFromItsSpawn_AndReachesIt()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await PlayerbotBattlegroundWorldTests.RunAsync(world, "bg-master-walks-away", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmwalks", 1, characterClass: 1);
            Vector3 start = await ally.ReadAsync(p => new Vector3(p.X, p.Y, p.Z));
            await context.PlaceAsync(ally, 0, start.X + 40f, start.Y, start.Z);
            await context.AutonomousAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = PlayerbotBattlegroundWorldTests.Coordinator(context);
            await context.WaitUntilAsync("the bot walks to the battlemaster", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Travel, TimeSpan.FromSeconds(60));
            Vector3 spawn = await context.ReadAsync(() => coordinator.Find(ally.BotId)!.MasterAt);
            Vector3 movedTo = await context.ReadAsync(() =>
            {
                Player player = ally.RequirePlayer();
                var master = player.Map!.FindObject(coordinator.Find(ally.BotId)!.Battlemaster) as Creature;
                ScenarioContext.Expect(master is not null, "the battlemaster is in the world");
                master!.Relocate(master.X, master.Y + 30f, master.Z);
                return new Vector3(master.X, master.Y, master.Z);
            });
            ScenarioContext.Expect(Vector3.Distance(spawn, movedTo) > 20f, "control: the master moved well away from its spawn");
            await context.WaitUntilAsync("the walk's goal follows the creature", () =>
                coordinator.Find(ally.BotId) is not { } t || t.Phase != PlayerbotBattlegroundPhase.Travel || Vector3.Distance(t.MasterAt, movedTo) < 3f, TimeSpan.FromSeconds(30));
            await context.WaitUntilAsync("the bot reaches the moved battlemaster", () => coordinator.Find(ally.BotId)?.Phase is PlayerbotBattlegroundPhase.Join or PlayerbotBattlegroundPhase.Queued,
                TimeSpan.FromSeconds(90));
        });
    }

    private static CreatureSpawn ExtraMaster(uint guid, Vector3 at, byte movementType = 0)
        => new() { Guid = guid, Entry = Content.AllianceWsgMaster, MapId = 0, X = at.X, Y = at.Y, Z = at.Z, Orientation = MathF.PI, MovementType = movementType };

    private static async Task<uint?> PickedAsync(ScenarioTestWorld world, string name, string botName, Func<ScenarioContext, ScenarioBot, Task>? before = null)
    {
        uint? picked = null;
        await PlayerbotBattlegroundWorldTests.RunAsync(world, name, async context =>
        {
            ScenarioBot ally = await context.LoginAsync(botName, 1, characterClass: 1);
            PlayerbotBattlegroundCoordinator coordinator = PlayerbotBattlegroundWorldTests.Coordinator(context);
            if (before is not null) await before(context, ally);
            picked = await context.ReadAsync(() => coordinator.FindMasterForTests(ally.RequirePlayer(), BattlegroundType.WarsongGulch)?.SpawnGuid);
        });
        return picked;
    }

    /// <summary>The control that a spawn of the test content is a living creature in the world (a master not in the world is skipped within sight of its spawn).</summary>
    private static async Task ExpectInWorldAsync(ScenarioContext context, ScenarioBot ally, uint spawnGuid)
        => ScenarioContext.Expect(await context.ReadAsync(() => ally.RequirePlayer().Map!.FindObject(ObjectGuid.WithEntry(HighGuid.Unit, Content.AllianceWsgMaster, spawnGuid)) is Creature { IsAlive: true }),
            $"control: spawn {spawnGuid} is in the world");

    /// <summary>A master that stands still is taken over a nearer one that walks its waypoints (the Darnassus emissary 14991 of game event 20).</summary>
    [Fact]
    public async Task FindMaster_PrefersAStationaryMaster_ToANearerOneThatWalksWaypoints()
    {
        Vector3 start = Content.MasterAt(Content.AllianceWsgMaster) - new Vector3(3f, 0f, 0f);
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartWithSpawnsAsync(
            [ExtraMaster(993901, start + new Vector3(1f, 0f, 0f), (byte)CreatureMovementType.Waypoint)]);
        uint? picked = await PickedAsync(world, "bg-master-prefers-still", "Bgmstill", (context, ally) => ExpectInWorldAsync(context, ally, 993901));
        Assert.Equal(Content.AllianceWsgMaster, picked);
    }

    /// <summary>Two masters as far from the bot: the lower spawn guid is taken, whatever the order of the spawns.</summary>
    [Fact]
    public async Task FindMaster_BreaksATieByTheLowerSpawnGuid()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartWithSpawnsAsync(
            [ExtraMaster(993100, Content.MasterAt(Content.AllianceWsgMaster))]);
        uint? picked = await PickedAsync(world, "bg-master-tie", "Bgmtie", (context, ally) => ExpectInWorldAsync(context, ally, 993100));
        Assert.Equal(993100u, picked);
    }

    /// <summary>
    /// On a real navigation mesh (the synthetic one of <see cref="PlayerbotBattlegroundRegionTests"/>) the master on an island of the
    /// region graph is skipped and the farther one the bot can walk to is taken.
    /// </summary>
    [Fact]
    public async Task FindMaster_SkipsAMasterInAnotherWalkingRegion_OnARealNavMesh()
    {
        Vector3 island = new(75, 5, 0), reachable = new(20, 115, 0), start = new(5, 5, 0);
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartWithSpawnsAsync([ExtraMaster(993910, island), ExtraMaster(993911, reachable)]);
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(0);
        foreach ((int x, int y, CellTile tile) in PlayerbotBattlegroundRegionTests.Tiles)
            fixture.WriteTile(0, x, y, tile);
        var nav = new NavMeshPathfinder(fixture.Directory);
        foreach ((int x, int y, _) in PlayerbotBattlegroundRegionTests.Tiles)
            Assert.True(nav.LoadTile(0, x, y));
        Assert.NotNull(await nav.RegionGraphTask(0, PathMover.Player.IncludeFlags, NavTerrain.SteepSlopes)!);

        uint? picked = await PickedAsync(world, "bg-master-other-region", "Bgmregion", async (context, ally) =>
        {
            await context.ReadAsync(() =>
            {
                WorldCollision.Of(context.World).Install(lineOfSight: new FloorLineOfSight(), pathfinder: nav);
                return true;
            });
            await context.PlaceAsync(ally, 0, start.X, start.Y, start.Z);
            // Asked until the creatures of the content stand in the world and the graph answers: the island is a component of its own.
            await context.WaitUntilAsync("the island is a region of its own", () =>
                PlayerbotNavigation.IsInOtherWalkingRegion(ally.RequirePlayer(), island), TimeSpan.FromSeconds(30));
        });
        Assert.Equal(993911u, picked);
    }

    /// <summary>
    /// The progress watch driven through the facts: a walk that has made no progress for its stall time is set aside, the master is refused
    /// for the zone the walk started in and the zone it stalled in, and the stall is reported as an unreachable objective.
    /// </summary>
    [Fact]
    public async Task AStalledWalk_IsSetAside_AndTheMasterIsRefusedFromTheStartZone()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await PlayerbotBattlegroundWorldTests.RunAsync(world, "bg-master-stalled", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmstalled", 1, characterClass: 1);
            Vector3 start = await ally.ReadAsync(p => new Vector3(p.X, p.Y, p.Z));
            await context.PlaceAsync(ally, 0, start.X + 40f, start.Y, start.Z);
            await context.AutonomousAsync(ally);
            PlayerbotBattlegroundCoordinator coordinator = PlayerbotBattlegroundWorldTests.Coordinator(context);
            await context.WaitUntilAsync("the bot walks to the battlemaster", () => coordinator.Find(ally.BotId)?.Phase == PlayerbotBattlegroundPhase.Travel, TimeSpan.FromSeconds(60));
            (ObjectGuid master, uint zone) = await context.ReadAsync(() =>
            {
                PlayerbotBattlegroundCoordinator.Tracked t = coordinator.Find(ally.BotId)!;
                ScenarioContext.Expect(!coordinator.IsMasterRefused(t.Battlemaster, t.StartZone), "control: not refused before");
                // The watch's mark: a best distance no step can beat by ten yards, reached longer ago than the stall time.
                t.BestYardsToMaster = 0.1f;
                t.BestYardsAtMs = unchecked(t.PhaseSinceMs - PlayerbotBattlegroundQueueMachine.TravelStallFor(t.TravelYards) - 10_000);
                return (t.Battlemaster, t.StartZone);
            });
            await context.WaitUntilAsync("the walk is set aside", () => coordinator.Events.Any(e => e.Contains("could not walk to the WarsongGulch battlemaster", StringComparison.Ordinal)),
                TimeSpan.FromSeconds(30));
            ScenarioContext.Expect(await context.ReadAsync(() => coordinator.IsMasterRefused(master, zone)), "the master is refused from the start zone");
        });
    }

    // --- stubs -------------------------------------------------------------------------------------------------------------

    /// <summary>Open ground at <c>floorZ</c> except an unwalkable disc, and (optionally) every non-trivial path query answered "tiles pending".</summary>
    private sealed class MidfieldMesh(Vector3 discCenter, float discRadius, float floorZ, bool tilesPending) : ILineOfSight, IPathfinder, IMeshPointQuery
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float zSearch, float maxSearchDistance) => floorZ;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float zSearch, out ModelAreaInfo info)
        {
            info = default;
            return false;
        }

        private bool InDisc(Vector3 at) => Vector2.Distance(new Vector2(at.X, at.Y), new Vector2(discCenter.X, discCenter.Y)) < discRadius;

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            if (InDisc(start)) return PathResult.None(start);
            PathType type = tilesPending && start != end ? PathType.Normal | PathType.TilesPending : PathType.Normal;
            return PathResult.StraightLine(start with { Z = floorZ }, end with { Z = floorZ }, type);
        }

        public bool TryGetRandomPointAround(uint mapId, Vector3 center, float radius, Random random, PathOptions? options, out Vector3 point)
        {
            point = center;
            return false;
        }

        public MeshRaycastResult Raycast(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => InDisc(start) ? MeshRaycastResult.Unavailable(end) : new MeshRaycastResult(true, false, 1f, end with { Z = floorZ });
    }

    /// <summary>Open ground, except that no path leads to within two yards of <paramref name="refused"/>.</summary>
    private sealed class RefusingPathfinder(Vector3 refused) : IPathfinder
    {
        public bool Enabled => true;

        /// <summary>The path queries asked so far.</summary>
        public int Searches { get; private set; }

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
        {
            Searches++;
            return Vector3.Distance(end, refused) < 2f ? new PathResult(PathType.NoPath, []) : PathResult.StraightLine(start, end, PathType.Normal);
        }
    }
}

/// <summary>Issue #1486 (c): the walk to a battlemaster is given time by its length (<see cref="PlayerbotBattlegroundQueueMachine.TravelTimeoutFor"/>).</summary>
public sealed class PlayerbotBattlegroundTravelTimeoutTests
{
    [Fact]
    public void TravelTimeoutFor_IsTheBaseForShortOrUnknownWalks_AndGrowsWithLength_UpToACap()
    {
        uint basis = PlayerbotBattlegroundQueueMachine.TravelTimeoutMs;
        Assert.Equal(basis, PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(0f));
        Assert.Equal(basis, PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(float.NaN));
        Assert.Equal(basis, PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(1_000f));
        uint far = PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(6_000f);
        Assert.True(far > basis, $"a 6,000 yard walk got {far} ms");
        Assert.True(PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(12_000f) > far);
        Assert.Equal(PlayerbotBattlegroundQueueMachine.MaxTravelTimeoutMs, PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(1_000_000f));
    }

    [Fact]
    public void Travel_ALongWalk_IsNotReleasedAtTheBaseTimeout_ButAtItsOwn()
    {
        uint basis = PlayerbotBattlegroundQueueMachine.TravelTimeoutMs;
        const float yards = 6_000f;
        uint own = PlayerbotBattlegroundQueueMachine.TravelTimeoutFor(yards);
        var atBase = new PlayerbotBattlegroundFacts { InPhaseMs = basis + 1, TravelYards = yards };
        var pastOwn = new PlayerbotBattlegroundFacts { InPhaseMs = own + 1, TravelYards = yards };
        Assert.Equal((PlayerbotBattlegroundPhase.Travel, PlayerbotBattlegroundAction.None), PlayerbotBattlegroundQueueMachine.Next(PlayerbotBattlegroundPhase.Travel, atBase));
        Assert.Equal((PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.Release), PlayerbotBattlegroundQueueMachine.Next(PlayerbotBattlegroundPhase.Travel, pastOwn));
    }
}

/// <summary>Issue #1486 (c): a long timeout is no licence to stand still - the progress watch of the walk.</summary>
public sealed class PlayerbotBattlegroundTravelProgressTests
{
    private const uint Stall = PlayerbotBattlegroundQueueMachine.TravelStallMs;

    [Fact]
    public void TravelStalled_IsTrueOnlyWhenNoProgressIsMadeForTheStallTime()
    {
        float best = -1f;
        uint at = 0;
        Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(500f, false, Stall, ref best, ref at, 1_000));
        Assert.Equal((500f, 1_000u), (best, at));

        // 5 yards closer is no progress (under TravelProgressYards): the clock of the 500-yard mark runs on.
        Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(495f, false, Stall, ref best, ref at, 1_000 + Stall));
        Assert.True(PlayerbotBattlegroundQueueMachine.TravelStalled(495f, false, Stall, ref best, ref at, 1_001 + Stall));

        // More than 10 yards closer is progress: the mark and the clock move.
        Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(489f, false, Stall, ref best, ref at, 1_001 + Stall));
        Assert.Equal((489f, 1_001 + Stall), (best, at));
        Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(900f, false, Stall, ref best, ref at, 1_001 + Stall + 10_000));

        // A distance that is not a number says nothing.
        Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(float.NaN, false, Stall, ref best, ref at, uint.MaxValue));
    }

    [Fact]
    public void AFightOnTheRoad_StopsTheClock_SoItIsNeverAStall()
    {
        float best = -1f;
        uint at = 0;
        Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(500f, false, Stall, ref best, ref at, 0));
        // Fighting for ten minutes: each fighting tick restarts the clock.
        for (uint now = 30_000; now <= 600_000; now += 30_000)
            Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(500f, true, Stall, ref best, ref at, now));
        Assert.Equal(600_000u, at);
        // Out of the fight the stall time counts from its end.
        Assert.False(PlayerbotBattlegroundQueueMachine.TravelStalled(500f, false, Stall, ref best, ref at, 600_000 + Stall));
        Assert.True(PlayerbotBattlegroundQueueMachine.TravelStalled(500f, false, Stall, ref best, ref at, 600_001 + Stall));
    }

    [Fact]
    public void WatchTravel_ADriveThatFightsIsNotStalled_AndOneThatStandsStillIs()
    {
        var fighter = new PlayerbotBattlegroundCoordinator.Tracked(Guid.NewGuid(), "Fighter") { MasterAt = new Vector3(100, 0, 0), TravelYards = 150f };
        var idler = new PlayerbotBattlegroundCoordinator.Tracked(Guid.NewGuid(), "Idler") { MasterAt = new Vector3(100, 0, 0), TravelYards = 150f };
        Vector3 at = Vector3.Zero;
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(fighter, at, true, 0));
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(idler, at, false, 0));
        uint late = PlayerbotBattlegroundQueueMachine.TravelStallFor(150f) + 1_000;
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(fighter, at, true, late));
        Assert.True(PlayerbotBattlegroundCoordinator.WatchTravel(idler, at, false, late));
    }

    [Fact]
    public void TheStallTime_GrowsWithTheLengthOfTheWay_SoAWindingRoadIsNotAStall()
    {
        Assert.Equal(Stall, PlayerbotBattlegroundQueueMachine.TravelStallFor(0f));
        Assert.Equal(Stall, PlayerbotBattlegroundQueueMachine.TravelStallFor(float.NaN));
        uint longWay = PlayerbotBattlegroundQueueMachine.TravelStallFor(9_000f);
        Assert.True(longWay > Stall + 600_000, $"a 9,000 yard way got {longWay} ms");
        Assert.True(longWay <= PlayerbotBattlegroundQueueMachine.MaxTravelTimeoutMs);

        // A road that leads 400 yards away from the master first: no stall in a long walk, a stall in a short one.
        var longWalk = new PlayerbotBattlegroundCoordinator.Tracked(Guid.NewGuid(), "Long") { MasterAt = new Vector3(3_000, 0, 0), TravelYards = 4_500f };
        var shortWalk = new PlayerbotBattlegroundCoordinator.Tracked(Guid.NewGuid(), "Short") { MasterAt = new Vector3(3_000, 0, 0), TravelYards = 0f };
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(longWalk, Vector3.Zero, false, 0));
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(shortWalk, Vector3.Zero, false, 0));
        uint afterThree = Stall + 1_000;
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(longWalk, new Vector3(-400, 0, 0), false, afterThree));
        Assert.True(PlayerbotBattlegroundCoordinator.WatchTravel(shortWalk, new Vector3(-400, 0, 0), false, afterThree));
    }

    [Fact]
    public void AGoalThatMovesAwayFromTheBot_ResetsTheWatch_OneThatMovesCloserDoesNot()
    {
        var t = new PlayerbotBattlegroundCoordinator.Tracked(Guid.NewGuid(), "Follower") { MasterAt = new Vector3(100, 0, 0), TravelYards = 150f };
        Vector3 at = Vector3.Zero;
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(t, at, false, 1_000));
        Assert.Equal((100f, 1_000u), (t.BestYardsToMaster, t.BestYardsAtMs));

        // Closer by 20 yards: the goal follows, the mark stays (the bot's progress so far still counts).
        PlayerbotBattlegroundCoordinator.FollowMaster(t, at, new Vector3(80, 0, 0), 50_000);
        Assert.Equal(new Vector3(80, 0, 0), t.MasterAt);
        Assert.Equal((100f, 1_000u), (t.BestYardsToMaster, t.BestYardsAtMs));

        // Farther by 60 yards: the distance to cover grew, so the best distance could never be beaten; the watch starts over.
        PlayerbotBattlegroundCoordinator.FollowMaster(t, at, new Vector3(140, 0, 0), 60_000);
        Assert.Equal(new Vector3(140, 0, 0), t.MasterAt);
        Assert.Equal((-1f, 60_000u), (t.BestYardsToMaster, t.BestYardsAtMs));
        Assert.False(PlayerbotBattlegroundCoordinator.WatchTravel(t, at, false, 60_500));
        Assert.Equal((140f, 60_500u), (t.BestYardsToMaster, t.BestYardsAtMs));
    }

    [Fact]
    public void TheGoalFollowsALiveMasterNearTheBotByHalfAYard_AndAFarOneByThree()
    {
        var t = new PlayerbotBattlegroundCoordinator.Tracked(Guid.NewGuid(), "Near") { MasterAt = new Vector3(8, 0, 0) };
        // Near: a 1-yard step is followed, so a stop within the interaction distance is a stop within reach of the creature.
        PlayerbotBattlegroundCoordinator.FollowMaster(t, Vector3.Zero, new Vector3(9, 0, 0), 0);
        Assert.Equal(new Vector3(9, 0, 0), t.MasterAt);
        PlayerbotBattlegroundCoordinator.FollowMaster(t, Vector3.Zero, new Vector3(9.3f, 0, 0), 0);
        Assert.Equal(new Vector3(9, 0, 0), t.MasterAt);

        // Far: a 1-yard step is not (the walk is not replanned at every step).
        var far = new PlayerbotBattlegroundCoordinator.Tracked(Guid.NewGuid(), "Far") { MasterAt = new Vector3(100, 0, 0) };
        PlayerbotBattlegroundCoordinator.FollowMaster(far, Vector3.Zero, new Vector3(101, 0, 0), 0);
        Assert.Equal(new Vector3(100, 0, 0), far.MasterAt);
        PlayerbotBattlegroundCoordinator.FollowMaster(far, Vector3.Zero, new Vector3(104, 0, 0), 0);
        Assert.Equal(new Vector3(104, 0, 0), far.MasterAt);
    }

    [Fact]
    public void ALongWalk_WithTheWatchTripped_IsSetAsideLikeARefusedRoute()
    {
        var stalled = new PlayerbotBattlegroundFacts { InPhaseMs = 20 * 60_000, TravelYards = 9_000f, TravelRefused = true };
        Assert.Equal((PlayerbotBattlegroundPhase.Idle, PlayerbotBattlegroundAction.SetAside), PlayerbotBattlegroundQueueMachine.Next(PlayerbotBattlegroundPhase.Travel, stalled));
    }
}

/// <summary>
/// Issue #1486 (b): the region-graph rule for a battlemaster's place (<see cref="PlayerbotNavigation.IsInOtherWalkingRegion"/>) on the
/// synthetic mesh of <see cref="PlayerbotUnreachableGoalTests"/>: an outdoor island is skipped, an indoor one is let through (the indoor
/// climb may reach it), a reachable place is no skip.
/// </summary>
public sealed class PlayerbotBattlegroundRegionTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 Start = new(5, 5, 0), Island = new(75, 5, 0), Reachable = new(20, 115, 0);

    // Tile A (the bot's) joins C, E, F to the north; tile B (X 40-80), holding Island, has a hole in its west and joins nothing.
    internal static readonly (int X, int Y, CellTile Tile)[] Tiles =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (12, 10, new CellTile(2, 0, 0, 80, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (13, 10, new CellTile(3, 0, 0, 120, 8, 8, Size: 5) { LinkedSides = [4] }),
    ];

    /// <summary>Open floor, but every place east of X 40 is inside a building (a WMO group that is not exterior).</summary>
    private sealed class IndoorEastLineOfSight : ILineOfSight
    {
        public bool Enabled => true;

        public bool IsInLineOfSight(uint mapId, Vector3 from, Vector3 to, bool ignoreM2 = true) => true;

        public bool TryGetObjectHit(uint mapId, Vector3 from, Vector3 to, float modifyDistance, out Vector3 hit)
        {
            hit = to;
            return false;
        }

        public float? GetModelHeight(uint mapId, float x, float y, float z, float maxSearchDistance) => 0f;

        public bool TryGetAreaInfo(uint mapId, float x, float y, float z, out ModelAreaInfo info)
        {
            info = x > 40f ? new ModelAreaInfo(ModelAreaInfo.MogpInterior, 0, 1, 1, 0f) : default;
            return x > 40f;
        }
    }

    private async Task<bool> AskAsync(ILineOfSight lineOfSight, Vector3 goal)
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Tiles, lineOfSight);
        await world.PlaceAsync(Start);
        // Asked like a think until the graph and the tiles are ready: the island is a component of its own.
        bool ready = false;
        for (int think = 0; think < 80 && !ready; think++)
        {
            ready = await world.Host.OnWorldAsync(() =>
            {
                PlayerbotNavigation.IsUnreachableOnFoot(world.Player, Island, out int from, out int to);
                return from >= 0 && to >= 0 && from != to;
            });
            if (!ready) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(ready, "the island is never told a component of its own");
        return await world.Host.OnWorldAsync(() => PlayerbotNavigation.IsInOtherWalkingRegion(world.Player, goal));
    }

    [Fact]
    public async Task AnOutdoorIslandMaster_IsInAnotherWalkingRegion()
        => Assert.True(await AskAsync(new FloorLineOfSight(), Island));

    [Fact]
    public async Task AReachablePlace_IsNotInAnotherWalkingRegion()
        => Assert.False(await AskAsync(new FloorLineOfSight(), Reachable));

    [Fact]
    public async Task AnIndoorIslandMaster_IsLetThrough_ForTheIndoorClimb()
        => Assert.False(await AskAsync(new IndoorEastLineOfSight(), Island));
}
