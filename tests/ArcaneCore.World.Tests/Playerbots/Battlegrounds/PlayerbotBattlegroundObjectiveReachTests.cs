using System.Numerics;
using ArcaneCore.Game.Battlegrounds;
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
        Assert.True(settled, "every query was answered, so the answer is settled");
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

    [Fact]
    public async Task FindMaster_SkipsABattlemasterNoExactRouteReaches()
    {
        await using ScenarioTestWorld world = await BattlegroundBotTestWorld.StartAsync();
        await PlayerbotBattlegroundWorldTests.RunAsync(world, "bg-master-unplannable", async context =>
        {
            ScenarioBot ally = await context.LoginAsync("Bgmasterally", 1, characterClass: 1);
            PlayerbotBattlegroundCoordinator coordinator = PlayerbotBattlegroundWorldTests.Coordinator(context);
            var find = typeof(PlayerbotBattlegroundCoordinator).GetMethod("FindMaster", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await context.ReadAsync(() =>
            {
                Player player = ally.RequirePlayer();
                object? control = find.Invoke(coordinator, [player, BattlegroundType.WarsongGulch]);
                ScenarioContext.Expect(control is not null, "control: with open ground the Alliance Warsong Gulch battlemaster beside the start is found");

                // The same world, but no route leads to that battlemaster (a master on an island or behind a wall the region graph does not show).
                WorldCollision.Of(context.World).Install(pathfinder: new RefusingPathfinder(Content.MasterAt(Content.AllianceWsgMaster)));
                object? refused = find.Invoke(coordinator, [player, BattlegroundType.WarsongGulch]);
                ScenarioContext.Expect(refused is null, "a battlemaster no exact route reaches must not be picked");
                return true;
            });
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

        public PathResult FindPath(uint mapId, Vector3 start, Vector3 end, PathOptions? options = null)
            => Vector3.Distance(end, refused) < 2f ? new PathResult(PathType.NoPath, []) : PathResult.StraightLine(start, end, PathType.Normal);
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
