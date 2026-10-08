using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// The per-cell player index (vmangos' "world" container of a cell, GridDefines.h AllWorldObjectTypes) behind
/// <see cref="GridContainer.CollectPlayers"/>: the players-only visit of Map::UpdateObjectVisibility and
/// Map::MessageDistBroadcast.
/// </summary>
public sealed class GridPlayerIndexTests
{
    private static GridContainer Create() => new(new MapOptions(), Map.VisibilityRange);

    private static Player NewPlayer(uint guid, float x, float y) => TestWorld.CreatePlayer(guid, x, y, new FakeSession((int)guid));

    private static List<Player> Players(GridContainer grids, float x, float y, float radius)
    {
        var found = new List<Player>();
        grids.CollectPlayers(x, y, radius, found);
        return found;
    }

    [Fact]
    public void CollectPlayers_ReturnsThePlayersOfTheTouchedCells_AndNoOtherObjects()
    {
        GridContainer grids = Create();
        Player near = NewPlayer(1, 10, 10);
        Player far = NewPlayer(2, 900, 900);
        grids.Add(near, active: true);
        grids.Add(far, active: true);
        grids.Add(new TestUnit(1, 12, 12), active: false);

        Assert.Equal(new[] { near }, Players(grids, 0, 0, 50));
        Assert.Equal(new[] { far }, Players(grids, 900, 900, 10));

        // The object query of the same cells still sees both kinds.
        var objects = new List<WorldObject>();
        grids.CollectObjects(0, 0, 50, objects);
        Assert.Equal(2, objects.Count);
    }

    [Fact]
    public void Relocate_MovesThePlayerBetweenCellLists()
    {
        GridContainer grids = Create();
        Player player = NewPlayer(1, 10, 10);
        grids.Add(player, active: true);

        player.SetPosition(2000, 2000, 0, 0); // not in a map: re-file by hand, as Map.OnObjectMoved does
        Assert.True(grids.Relocate(player));

        Assert.Empty(Players(grids, 10, 10, 20));
        Assert.Equal(new[] { player }, Players(grids, 2000, 2000, 20));

        Assert.True(grids.Remove(player));
        Assert.Empty(Players(grids, 2000, 2000, 20));
    }

    [Fact]
    public void CollectPlayers_IncludesUnplacedPlayers_AndWalksEveryPlayerForHugeOrNonFiniteQueries()
    {
        GridContainer grids = Create();
        Player placed = NewPlayer(1, 10, 10);
        Player unplaced = NewPlayer(2, float.NaN, 0);
        grids.Add(placed, active: true);
        grids.Add(unplaced, active: false);
        grids.Add(new TestUnit(1, 11, 11), active: false);

        Assert.Equal(new[] { unplaced }, Players(grids, 5000, 5000, 10));
        Assert.Equal(new[] { placed, unplaced }, Players(grids, 0, 0, 20).OrderBy(p => p.Guid.Value));
        Assert.Equal(2, Players(grids, 0, 0, float.PositiveInfinity).Count);
        Assert.Equal(2, Players(grids, 0, 0, 20000).Count);
        Assert.Equal(2, Players(grids, float.NaN, 0, 10).Count);
    }
}
