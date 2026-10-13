using System.Numerics;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots;

/// <summary>
/// A far goal whose region the bot's does not join (bot QA cluster C3: the same dead-end point to 0.1 yard for 205 bots). The layout of
/// <see cref="PlayerbotRegionLegTests"/> without tile <c>D</c>: the bot's tile <c>A</c> (world X 0-40, Y 0-40) joins <c>C</c> to the north
/// only, and the goal in <c>B</c> (X 40-80, west 30 yards a hole) is in a region no walk reaches.
/// </summary>
public sealed class PlayerbotDeadEndLegTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static readonly Vector3 AtTheEdge = new(35, 5, 0), Far = new(5, 5, 0), Goal = new(75, 5, 0);

    private static readonly PlayerbotOptions Options = new() { MaxRouteYards = 30, MaxPathPoints = 22 };

    private static Task<PlayerbotRoute?> PlanAsync(SyntheticNavWorld world)
        => world.Host.OnWorldAsync(() => PlayerbotNavigation.TryPlanToward(world.Player, Goal, Options, out PlayerbotRoute? planned) ? planned : null);

    /// <summary>
    /// One bot proves the dead end by standing at the nearest point; a second bot, farther back, is then not sent to walk there: before, it was
    /// given a leg to the same edge, and the stall watch set the goal aside minutes later only for the next bot to come back.
    /// </summary>
    [Fact]
    public async Task ABotFarBack_IsNotSentToTheDeadEnd_OnceAnotherProvedIt()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(AtTheEdge);
        Assert.Null(await PlanAsync(world)); // standing at the nearest point: no route closer

        await world.PlaceAsync(Far);
        PlayerbotRoute? route = await PlanAsync(world);
        Assert.True(route is null, $"a leg to {route?.Points[^1]} towards a goal no walk reaches");
    }

    /// <summary>The refusal at the dead end is told to the caller once, and so is the proven pair's refusal for a bot farther back.</summary>
    [Fact]
    public async Task TheRefusalAtTheDeadEnd_IsTold_ToTheCaller()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(AtTheEdge);
        Assert.Null(await PlanAsync(world));
        Assert.True(await world.Host.OnWorldAsync(PlayerbotNavigation.TakeDeadEndRefusal));
        Assert.False(await world.Host.OnWorldAsync(PlayerbotNavigation.TakeDeadEndRefusal)); // told once

        await world.PlaceAsync(Far);
        Assert.Null(await PlanAsync(world));
        Assert.True(await world.Host.OnWorldAsync(PlayerbotNavigation.TakeDeadEndRefusal));
    }

    /// <summary>A goal the walk does reach is no dead end: no refusal is told and the leg is planned.</summary>
    [Fact]
    public async Task AReachableGoal_IsNoDeadEnd()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Layout);
        await world.PlaceAsync(Far);
        Vector3 north = new(20, 70, 0);
        PlayerbotRoute? route = await world.Host.OnWorldAsync(() =>
            PlayerbotNavigation.TryPlanToward(world.Player, north, Options, out PlayerbotRoute? planned) ? planned : null);
        Assert.NotNull(route);
        Assert.False(await world.Host.OnWorldAsync(PlayerbotNavigation.TakeDeadEndRefusal));
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
