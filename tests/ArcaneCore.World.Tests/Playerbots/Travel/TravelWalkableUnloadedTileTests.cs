using System.Numerics;
using ArcaneCore.Game.Entities;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Travel;

/// <summary>
/// Cause 1 of the Teldrassil cluster: the planner's walkable test (<see cref="TravelFacts.Walkable"/>) read a place on a navigation tile
/// that has not been read yet as "joined" to everything, so a far goal behind a tile the walk never loaded was planned as one straight
/// Walk leg (5709 and 13200 yards left in the live log) and the replan could only fail. The layout is <see cref="TravelIslands"/>: the
/// bot is on a small island, the goal on another one whose tile is on disk but not resident.
/// </summary>
public sealed class TravelWalkableUnloadedTileTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 SamePlace = new(80f, 90f, 0f), AtSea = new(5000f, 5000f, 0f);

    private async Task<SyntheticNavWorld> StartAsync()
    {
        SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, [TravelIslands.Home(), TravelIslands.Large()], new FloorLineOfSight(),
            unloadedTiles: [TravelIslands.Far()]);
        await world.PlaceAsync(TravelIslands.Start);
        await world.Host.OnWorldAsync(() => world.Nav.UnloadTile(0, 30, 31)); // make sure the goal's tile is not resident
        return world;
    }

    private static Task<bool> WalkableAsync(SyntheticNavWorld world, Vector3 to)
        => world.Host.OnWorldAsync(() =>
        {
            TravelFacts facts = PlayerbotTravel.BuildFacts(world.Session, world.Player, TravelIslands.Options());
            return facts.Walkable!(TravelPlace.Of(world.Player), new TravelPlace(0, to));
        });

    /// <summary>
    /// The cost of the blocking tile reads (<see cref="PlayerbotTravel.BlockingLocates"/> places, read synchronously on the world thread):
    /// <c>BuildFacts</c> with the goal's tile not resident, timed on the world thread, then again with every tile resident. The bound is
    /// generous (a failure means a read that blocks for seconds); the measured times are in the assertion messages and the output.
    /// </summary>
    [Fact]
    public async Task BuildFacts_WithAColdGoalTile_ReadsItOnceAndStaysCheap()
    {
        await using SyntheticNavWorld world = await StartAsync();
        const double BudgetMs = 1000.0;
        (double cold, double warm) = await world.Host.OnWorldAsync(() =>
        {
            Player player = world.Player;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TravelFacts coldFacts = PlayerbotTravel.BuildFacts(world.Session, player, TravelIslands.Options());
            _ = coldFacts.Walkable!(TravelPlace.Of(player), new TravelPlace(0, TravelIslands.Goal));
            double coldMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            TravelFacts warmFacts = PlayerbotTravel.BuildFacts(world.Session, player, TravelIslands.Options());
            _ = warmFacts.Walkable!(TravelPlace.Of(player), new TravelPlace(0, TravelIslands.Goal));
            return (coldMs, sw.Elapsed.TotalMilliseconds);
        });

        output.WriteLine($"BuildFacts + walkable test, cold goal tile {cold:F2} ms, warm {warm:F2} ms");
        Assert.True(cold < BudgetMs, $"cold BuildFacts took {cold:F2} ms (warm {warm:F2} ms)");
        Assert.True(warm < BudgetMs, $"warm BuildFacts took {warm:F2} ms (cold {cold:F2} ms)");
    }

    [Fact]
    public async Task AGoalOnATileNotReadYet_BehindAnIslandTheWalkCannotCross_IsNotWalkable()
    {
        await using SyntheticNavWorld world = await StartAsync();
        Assert.False(await WalkableAsync(world, TravelIslands.Goal));
    }

    /// <summary>Once the tile is read both ends are placed: the islands are seen as separate (what the failing replan saw). Layout check; holds before and after the fix.</summary>
    [Fact]
    public async Task OnceTheGoalsTileIsRead_TheIslandsAreSeenAsSeparate()
    {
        await using SyntheticNavWorld world = await StartAsync();
        bool walkable = true;
        for (int attempt = 0; attempt < 200 && walkable; attempt++)
        {
            walkable = await WalkableAsync(world, TravelIslands.Goal);
            if (walkable) await Task.Delay(20);
        }

        Assert.False(walkable, "the goal's island is never placed in another component");
    }

    /// <summary>The leniency that must stay: a place on the bot's own island, and a place with no navmesh at all (at sea), count as joined.</summary>
    [Fact]
    public async Task APlaceOnTheBotsOwnIsland_AndAPlaceWithNoNavmesh_StillCountAsJoined()
    {
        await using SyntheticNavWorld world = await StartAsync();
        Assert.True(await WalkableAsync(world, SamePlace));
        Assert.True(await WalkableAsync(world, AtSea));
    }
}
