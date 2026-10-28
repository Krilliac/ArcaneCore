using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
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
    /// One set of facts reads at most <see cref="PlayerbotTravel.BlockingLocates"/> tile synchronously on the world thread (counted by
    /// <see cref="PathSearchCounters"/>): the first place that is not the bot's own. The next cold place is only queued for a background read
    /// and counts as joined until it arrives; a new set of facts, which is what a replan builds, places it.
    /// </summary>
    [Fact]
    public async Task BuildFacts_ReadsOnlyTheFirstColdPlaceOnTheWorldThread_AndQueuesTheRest()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, [TravelIslands.Home()], new FloorLineOfSight(),
            unloadedTiles: [TravelIslands.Far(), TravelIslands.Large()]);
        await world.PlaceAsync(TravelIslands.Start);
        await world.Host.OnWorldAsync(() => { world.Nav.UnloadTile(0, 30, 31); world.Nav.UnloadTile(0, 31, 30); return true; });
        Assert.Equal(1, PlayerbotTravel.BlockingLocates);

        (bool first, bool second, long syncReads, double ms) = await world.Host.OnWorldAsync(() =>
        {
            PathSearchTotals before = PathSearchCounters.Current;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TravelFacts facts = PlayerbotTravel.BuildFacts(world.Session, world.Player, TravelIslands.Options());
            TravelPlace here = TravelPlace.Of(world.Player);
            bool onLarge = facts.Walkable!(here, new TravelPlace(0, new Vector3(100f, 700f, 0f))); // the first cold place: read now
            bool onFar = facts.Walkable!(here, new TravelPlace(0, TravelIslands.Goal)); // the second: queued, undecided
            return (onLarge, onFar, (PathSearchCounters.Current - before).SyncTileReads, sw.Elapsed.TotalMilliseconds);
        });

        output.WriteLine($"two cold places: {syncReads} synchronous tile read(s), {ms:F2} ms on the world thread");
        Assert.False(first, "the first cold place is placed on the large island, apart from the bot's");
        Assert.True(second, "the second cold place is still pending and counts as joined");
        Assert.Equal(1, syncReads);

        bool walkable = true;
        for (int attempt = 0; attempt < 200 && walkable; attempt++)
        {
            walkable = await world.Host.OnWorldAsync(() =>
                PlayerbotTravel.BuildFacts(world.Session, world.Player, TravelIslands.Options()).Walkable!(TravelPlace.Of(world.Player), new TravelPlace(0, TravelIslands.Goal)));
            if (walkable) await Task.Delay(20);
        }

        Assert.False(walkable, "a new set of facts places the second place too");
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
