using System.Numerics;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A far goal whose region the bot's does not join (bot QA cluster C3: identical coordinates across bots and goals at several spots, e.g. -1.0,-4188.3 map 1). The layout of
/// <see cref="PlayerbotRegionLegTests"/> without tile <c>D</c>: the bot's tile <c>A</c> (world X 0-40, Y 0-40) joins <c>C</c> to the north
/// only, and the goal in <c>B</c> (X 40-80, west 30 yards a hole) is in a region no walk reaches.
/// </summary>
public sealed class PlayerbotDeadEndLegTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 AtTheEdge = new(35, 5, 0), Far = new(5, 5, 0), Goal = new(75, 5, 0);

    private static readonly PlayerbotOptions Options = new() { MaxRouteYards = 30, MaxPathPoints = 22 };

    private static Task<PlayerbotRoute?> PlanAsync(SyntheticNavWorld world, bool deadEnds = true, bool completedReturn = false, PlayerbotOptions? options = null)
        => world.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(world.Player, Goal, options ?? Options, out PlayerbotRoute? planned,
            completedQuestReturn: completedReturn, deadEnds: deadEnds) ? planned : null);

    private static Task<bool> RefusalTold(SyntheticNavWorld world) => world.Host.OnWorldAsync(PlayerbotNavigation.TakeDeadEndRefusal);

    /// <summary>
    /// Plans like a bot thinking again until the mesh answers: a tile still being read answers "pending" (no verdict, nothing recorded), so
    /// the first thinks may be refused without a dead end told. Returns the last route and whether any think was told a dead end.
    /// </summary>
    private static async Task<(PlayerbotRoute? Route, bool Told)> ThinkAsync(SyntheticNavWorld world, bool deadEnds = true, bool completedReturn = false,
        PlayerbotOptions? options = null)
    {
        PlayerbotRoute? route = null;
        for (int think = 0; think < 40; think++)
        {
            route = await PlanAsync(world, deadEnds, completedReturn, options);
            if (await RefusalTold(world)) return (route, true);
            await world.Host.World.AdvanceClockAsync(50);
        }

        return (route, false);
    }

    /// <summary>
    /// One bot proves the dead end by standing at the nearest point (the mesh's partial path to the goal ends beside it); a second bot,
    /// farther back, is then not sent to walk there: before, it was given a leg to the same edge, and the stall watch set the goal aside
    /// minutes later only for the next bot to come back.
    /// </summary>
    [Fact]
    public async Task ABotFarBack_IsNotSentToTheDeadEnd_OnceAnotherProvedIt()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(AtTheEdge);
        (PlayerbotRoute? first, bool told) = await ThinkAsync(world); // standing at the nearest point: no route closer
        Assert.Null(first);
        Assert.True(told);

        await world.PlaceAsync(Far);
        PlayerbotRoute? route = await PlanAsync(world);
        Assert.True(route is null, $"a leg to {route?.Points[^1]} towards a goal no walk reaches");
        Assert.True(await RefusalTold(world));
    }

    /// <summary>A caller that did not opt in plans as on main, whatever a bot proved.</summary>
    [Fact]
    public async Task ACallerThatDidNotOptIn_IsUnaffected()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(AtTheEdge);
        Assert.True((await ThinkAsync(world)).Told); // recorded

        await world.PlaceAsync(Far);
        Assert.NotNull(await PlanAsync(world, deadEnds: false));
        Assert.False(await RefusalTold(world));
        await world.PlaceAsync(AtTheEdge);
        Assert.Null(await PlanAsync(world, deadEnds: false));
        Assert.False(await RefusalTold(world)); // refused as on main, nothing told
    }

    /// <summary>A refusal that is the bot's own (the map policy keeps it off the map) proves nothing about the pair, at the edge or far from it.</summary>
    [Fact]
    public async Task ABotRefusedByTheMapPolicy_RecordsNothing()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        var offMap = new PlayerbotOptions { MaxRouteYards = 30, MaxPathPoints = 22, AllowedMaps = [1] };
        foreach (Vector3 at in new[] { AtTheEdge, Far })
        {
            await world.PlaceAsync(at);
            (PlayerbotRoute? refused, bool told) = await ThinkAsync(world, options: offMap);
            Assert.Null(refused);
            Assert.False(told);
        }

        await world.PlaceAsync(Far); // nothing was recorded: another bot is still given its leg
        Assert.NotNull(await PlanAsync(world));
        Assert.False(await RefusalTold(world));
    }

    /// <summary>A completed quest's return is no opted-in walk: no refusal told, nothing recorded.</summary>
    [Fact]
    public async Task ACompletedQuestReturn_RecordsNothing()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(AtTheEdge);
        Assert.False((await ThinkAsync(world, completedReturn: true)).Told);
        await world.PlaceAsync(Far);
        Assert.NotNull(await PlanAsync(world));
    }

    /// <summary>A goal the walk does reach is no dead end: no refusal is told and the leg is planned.</summary>
    [Fact]
    public async Task AReachableGoal_IsNoDeadEnd()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(Far);
        Vector3 north = new(20, 70, 0);
        PlayerbotRoute? route = await world.Host.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, north, Options, out PlayerbotRoute? planned, deadEnds: true) ? planned : null);
        Assert.NotNull(route);
        Assert.False(await RefusalTold(world));
    }

    /// <summary>A dead-end set-aside suspends the quest but is no step towards abandoning it (<see cref="PlayerbotSuspensions.MaxReturnSetAsides"/>).</summary>
    [Fact]
    public void ADeadEndSetAside_DoesNotFeedTheAbandonCounter()
    {
        var suspensions = new PlayerbotSuspensions();
        for (int i = 0; i < 2 * PlayerbotSuspensions.MaxReturnSetAsides; i++) suspensions.SuspendQuest(77, 1000, countsAsSetAside: false);
        Assert.True(suspensions.IsQuestSuspended(77, 2000));
        Assert.Equal(0, suspensions.QuestSetAsideCount(77));
        suspensions.SuspendQuest(77, 1000);
        Assert.Equal(1, suspensions.QuestSetAsideCount(77)); // an ordinary set-aside still counts
    }

    [Fact]
    public async Task TheMemory_SurvivesConcurrentRecordAndKnown()
    {
        var memory = new PlayerbotDeadEnds();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                memory.Record(worker, i, 1000 + i);
                memory.Known(worker, i / 2, 1000 + i);
            }
        })));
        Assert.InRange(memory.Count, 1, PlayerbotDeadEnds.MaxPairs);
    }
    [Fact]
    public void TheMemory_IsBounded_AndExpires()
    {
        var memory = new PlayerbotDeadEnds();
        for (int i = 0; i < PlayerbotDeadEnds.MaxPairs + 50; i++) memory.Record(i, i + 1, 1000 + i);
        Assert.Equal(PlayerbotDeadEnds.MaxPairs, memory.Count);
        Assert.True(memory.Known(PlayerbotDeadEnds.MaxPairs + 49, PlayerbotDeadEnds.MaxPairs + 50, 5000));
        Assert.False(memory.Known(0, 1, 5000)); // the earliest went first
        Assert.False(memory.Known(PlayerbotDeadEnds.MaxPairs + 49, PlayerbotDeadEnds.MaxPairs + 50, 5000 + PlayerbotDeadEnds.ExpireMs));
    }

    private static readonly (int X, int Y, CellTile Tile)[] Layout =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [2, 4] }),
    ];
}
