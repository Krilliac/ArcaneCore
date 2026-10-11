using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// The guards of the indoor stairs exception (<see cref="PlayerbotNavigation.ClimbReachYards"/>, <see cref="PlayerbotNavigation.ClimbExtraYards"/>
/// and the indoor test) on a repository-authored mesh: one 80-yard tile of 5-yard cells (cell i along world X, j along world Y), steep
/// cells flagged <see cref="NavTerrain.SteepSlopes"/> as the generator flags the Kargath tower's upper stairs, and a stand-in for the
/// collision models that says which places are indoors. Every plan is <see cref="PlayerbotNavigation.TryPlan"/>, as a quest giver's
/// approach is (PlayerbotQuestGoals); the walkable mesh never reaches the destination, only steep polygons do.
/// </summary>
public sealed class PlayerbotClimbIndoorsTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 Start = new(5, 40, 0);

    /// <summary>Walkable to X 50, a 5-yard steep strip (X 50–55), walkable beyond: the destination at X 60 is 10 yards past the walkable end.</summary>
    private static (int, int, CellTile)[] NarrowStairs() =>
    [(10, 10, new CellTile(0, 0, 0, 0, 16, 16, Size: 5) { Flags = (i, _) => i == 10 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground })];

    private static readonly IndoorsWhere Indoors = new(_ => true), Outdoors = new(_ => false);

    private async Task<PlayerbotRoute?> PlanAsync((int, int, CellTile)[] tiles, IndoorsWhere models, Vector3 start, Vector3 destination)
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, tiles, models);
        await world.PlaceAsync(start);
        return await world.Host.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlan(world.Player, destination, new PlayerbotOptions(), out PlayerbotRoute? route) ? route : null);
    }

    /// <summary>The exception itself: an indoor destination 10 yards past the walkable end, over 5 yards of steep stairs, is reached.</summary>
    [Fact]
    public async Task AnIndoorDestination_PastAShortSteepFlight_IsReached()
    {
        Vector3 destination = new(60, 40, 0);
        PlayerbotRoute? route = await PlanAsync(NarrowStairs(), Indoors, Start, destination);

        Assert.NotNull(route);
        Assert.True(Vector3.Distance(route!.Points[^1], destination) < 1f, $"the route ends at {route.Points[^1]}");
    }

    /// <summary>
    /// The indoor guard: the same steep strip with the destination outdoors (a cliff, a slope of the terrain) stays refused. Before the
    /// exception every steep polygon was refused; without the guard the bot would climb any slope near its goal.
    /// </summary>
    [Fact]
    public async Task AnOutdoorDestination_PastSteepPolygons_IsRefused()
        => Assert.Null(await PlanAsync(NarrowStairs(), Outdoors, Start, new Vector3(60, 40, 0)));

    /// <summary>
    /// The reach guard: steep cells from X 50 to 75 and the destination at X 78, 28 yards (flat) past the walkable end, is refused although
    /// the way over them is only 28 yards longer than the walkable one (within <see cref="PlayerbotNavigation.ClimbExtraYards"/>).
    /// </summary>
    [Fact]
    public async Task AnIndoorDestination_FartherThanTheReachFromTheWalkableEnd_IsRefused()
    {
        (int, int, CellTile)[] wide =
            [(10, 10, new CellTile(0, 0, 0, 0, 16, 16, Size: 5) { Flags = (i, _) => i is >= 10 and <= 14 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground })];
        Assert.Null(await PlanAsync(wide, Indoors, Start, new Vector3(78, 40, 0)));
    }

    /// <summary>
    /// The length guard: a wall (no mesh, X 50–55) between the walkable side and the destination, 10 yards past the walkable end, with one
    /// steep gap in it at the far end of the tile (Y 75–80). The way through the gap is about 110 yards longer than the walkable way: refused.
    /// </summary>
    [Fact]
    public async Task AnIndoorClimb_MuchLongerThanTheWalkableWay_IsRefused()
    {
        (int, int, CellTile)[] wall =
        [(10, 10, new CellTile(0, 0, 0, 0, 16, 16, Size: 5)
        {
            Walkable = (i, j) => i != 10 || j == 15,
            Flags = (i, _) => i == 10 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
        })];
        Assert.Null(await PlanAsync(wall, Indoors, new Vector3(5, 5, 0), new Vector3(60, 5, 0)));
    }
}
