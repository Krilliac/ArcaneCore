using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.TestSupport;
using ArcaneCore.World.Playerbots;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Tram;

/// <summary>
/// #1499: a start more than 150 yards from the entrance approach used to be accepted without any check, and the walk then failed with
/// <c>no-way-to-entrance</c> after the trip was committed (Coldridge Valley, indoors in Ironforge). Beyond 150 yards the region graph decides:
/// an approach in another walking component is no way, and the trip is not chosen. On the synthetic mesh, through the code the bots run.
/// </summary>
public sealed class PlayerbotTramFarComponentTests : IDisposable
{
    private readonly NavMeshFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    // The bot's tile chain runs north from Y 0 to 160; the tile holding Unjoined (X 40-80) has a hole in its west and joins nothing.
    private static readonly (int X, int Y, CellTile Tile)[] Tiles =
    [
        (10, 10, new CellTile(0, 0, 0, 0, 8, 8, Size: 5) { LinkedSides = [0, 2] }),
        (10, 11, new CellTile(0, 1, 40, 0, 8, 8, Size: 5) { LinkedSides = [0, 6], Walkable = (i, _) => i >= 6 }),
        (11, 10, new CellTile(1, 0, 0, 40, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (12, 10, new CellTile(2, 0, 0, 80, 8, 8, Size: 5) { LinkedSides = [0, 4, 2] }),
        (13, 10, new CellTile(3, 0, 0, 120, 8, 8, Size: 5) { LinkedSides = [4] }),
    ];

    private static readonly Vector3 FarStart = new(5, 150, 0), JoinedApproach = new(5, 5, 0), UnjoinedApproach = new(75, 5, 0);

    [Fact]
    public async Task AFarStart_WhoseApproachIsInAnotherComponent_DoesNotChooseTheTram_AndOneInTheSameComponentStillDoes()
    {
        await using SyntheticNavWorld world = await SyntheticNavWorld.StartAsync(_fixture, Tiles, new FloorLineOfSight());
        await world.PlaceAsync(FarStart);
        Assert.True(Vector2.Distance(new(FarStart.X, FarStart.Y), new(UnjoinedApproach.X, UnjoinedApproach.Y)) > 150f);

        bool otherComponent = false;
        for (int think = 0; think < 80 && !otherComponent; think++)
        {
            otherComponent = await world.Host.OnWorldAsync(() => PlayerbotNavigation.InOtherWalkingComponent(world.Player, UnjoinedApproach));
            if (!otherComponent) await world.Host.World.AdvanceClockAsync(50);
        }

        Assert.True(otherComponent, "the graph never put the approach in another component");
        Assert.False(await world.Host.OnWorldAsync(() => PlayerbotTram.CanReachEntranceApproach(world.Player, UnjoinedApproach)));
        Assert.False(await world.Host.OnWorldAsync(() => PlayerbotNavigation.InOtherWalkingComponent(world.Player, JoinedApproach)));
        Assert.True(await world.Host.OnWorldAsync(() => PlayerbotTram.CanReachEntranceApproach(world.Player, JoinedApproach)));
    }
}
