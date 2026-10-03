using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>A bare creature-like unit for grid tests (the creature system lands separately).</summary>
internal sealed class TestUnit : Unit
{
    public TestUnit(uint counter, float x, float y, float z = 0)
        : base(new ObjectGuid(((ulong)HighGuid.Unit << 48) | (1000UL << 24) | counter), Game.TypeId.Unit, TypeMask.Object | TypeMask.Unit, UpdateFields.UnitEnd)
    {
        SetPosition(x, y, z, 0);
    }
}

/// <summary>Cell/grid arithmetic (vmangos/cmangos GridDefines.h).</summary>
public sealed class GridDefinesTests
{
    [Fact]
    public void Origin_IsTheCentreGridAndCell()
    {
        Assert.Equal(new GridCoord(32, 32), GridDefines.ComputeGridCoord(0, 0));
        CellCoord cell = GridDefines.ComputeCellCoord(0, 0);
        Assert.Equal(new CellCoord(512, 512), cell);
        Assert.Equal(new GridCoord(32, 32), cell.Grid);
        Assert.Equal((0, 0), (cell.LocalX, cell.LocalY));
    }

    [Fact]
    public void Cells_Are16PerGrid_AndClampAtTheMapEdge()
    {
        Assert.Equal(GridDefines.SizeOfGrids / 16, GridDefines.SizeOfGridCell);
        Assert.Equal(new CellCoord(513, 512), GridDefines.ComputeCellCoord(GridDefines.SizeOfGridCell, 0));
        Assert.Equal(new GridCoord(63, 0), GridDefines.ComputeGridCoord(1e9f, -1e9f));
        Assert.Equal(new CellCoord(1023, 0), GridDefines.ComputeCellCoord(1e9f, -1e9f));
        Assert.Equal(32 * 64 + 31, new GridCoord(32, 31).Id);
    }

    [Fact]
    public void CellArea_CoversTheBoundingSquare()
    {
        CellArea area = GridDefines.CalculateCellArea(0, 0, 90);
        Assert.Equal(new CellCoord(509, 509), area.Low);
        Assert.Equal(new CellCoord(514, 514), area.High);
        Assert.True(area.Contains(new CellCoord(512, 514)));
        Assert.False(area.Contains(new CellCoord(515, 512)));
        Assert.True(GridDefines.CalculateCellArea(5, 5, 0).IsSingleCell);
    }

    [Theory]
    [InlineData(17066f, 0f, 0f, 0f, true)]
    [InlineData(17067f, 0f, 0f, 0f, false)]
    [InlineData(0f, float.NaN, 0f, 0f, false)]
    [InlineData(0f, 0f, 500000f, 0f, false)]
    [InlineData(0f, 0f, 0f, 13f, false)]
    public void ValidMapCoord_FollowsMaNGOS(float x, float y, float z, float o, bool valid)
        => Assert.Equal(valid, GridDefines.IsValidMapCoord(x, y, z, o));
}

/// <summary>Grid loading, the grid state machine and cell queries (vmangos Map / GridStates).</summary>
public sealed class GridContainerTests
{
    private static GridContainer Create(bool unload = true)
        => new(new MapOptions { GridUnload = unload, GridCleanUpDelayMs = MapOptions.MinGridDelayMs }, Map.VisibilityRange);

    private static void Run(GridContainer grids, long totalMs, long stepMs = 1000)
    {
        for (long t = 0; t < totalMs; t += stepMs)
        {
            grids.Update(stepMs);
        }
    }

    [Fact]
    public void ActiveObject_LoadsItsGridAndTheGridsWithinActivationDistance()
    {
        GridContainer grids = Create();
        var loaded = new List<GridCoord>();
        grids.GridLoaded += g => loaded.Add(g.Coord);

        grids.Add(new TestUnit(1, 0, 0), active: true);

        // (0, 0) sits on the corner of four grids; 100 yards reaches all of them.
        Assert.Equal(4, grids.LoadedGridCount);
        Assert.Equal(4, loaded.Count);
        Assert.All(grids.LoadedGrids, g => Assert.Equal(GridState.Active, g.State));
        Assert.True(grids.IsGridLoaded(new GridCoord(31, 31)));
    }

    [Fact]
    public void PassiveObject_OnlyCreatesItsGrid()
    {
        GridContainer grids = Create();
        var unit = new TestUnit(1, 5000, 5000);
        grids.Add(unit, active: false);

        Grid grid = Assert.Single(grids.LoadedGrids);
        Assert.False(grid.ObjectDataLoaded);
        Assert.Equal(GridState.Idle, grid.State);
        Assert.Equal(GridDefines.ComputeCellCoord(5000, 5000), grids.CellOf(unit));
    }

    [Fact]
    public void Grids_UnloadAfterTheLastActiveObjectLeaves()
    {
        GridContainer grids = Create();
        var unloaded = new List<GridCoord>();
        grids.GridUnloaded += unloaded.Add;
        var player = new TestUnit(1, 0, 0);
        grids.Add(player, active: true);

        Run(grids, 3 * MapOptions.MinGridDelayMs);
        Assert.Equal(4, grids.LoadedGridCount); // kept alive while the active object is there

        grids.Remove(player);
        Run(grids, MapOptions.MinGridDelayMs / 10); // Active → Idle at the next expiry/10 check
        Assert.All(grids.LoadedGrids, g => Assert.NotEqual(GridState.Active, g.State));
        Run(grids, MapOptions.MinGridDelayMs + 2000); // Idle → Removal → unloaded after the full delay

        Assert.Equal(0, grids.LoadedGridCount);
        Assert.Equal(4, unloaded.Count);
    }

    [Fact]
    public void Unloading_EvictsTheObjectsLeftInTheGrid()
    {
        GridContainer grids = Create();
        var evicted = new List<WorldObject>();
        grids.EvictObject = evicted.Add;
        var unit = new TestUnit(1, 5000, 5000);
        grids.Add(unit, active: false);

        Run(grids, 2 * MapOptions.MinGridDelayMs + 2000);

        Assert.Same(unit, Assert.Single(evicted));
        Assert.False(grids.Contains(unit));
        Assert.Equal(0, grids.LoadedGridCount);
    }

    [Fact]
    public void ActiveObjectNearby_KeepsANeighbouringGridLoaded()
    {
        GridContainer grids = Create();
        GridCoord farGrid = GridDefines.ComputeGridCoord(5000, 5000);
        grids.Add(new TestUnit(1, 5000, 5000), active: false);

        var guard = new TestUnit(2, 5000, 5000);
        grids.Add(guard, active: true);
        Run(grids, 3 * MapOptions.MinGridDelayMs);
        Assert.NotNull(grids.GetGrid(farGrid));
        Assert.False(grids.UnloadGrid(farGrid, force: false));
        Assert.True(grids.UnloadGrid(farGrid, force: true));
    }

    [Fact]
    public void GridUnloadOff_LocksGridsInMemory()
    {
        GridContainer grids = Create(unload: false);
        var unit = new TestUnit(1, 5000, 5000);
        grids.Add(unit, active: false);

        Run(grids, 5 * MapOptions.MinGridDelayMs);

        Assert.True(grids.Contains(unit));
        Assert.Equal(1, grids.LoadedGridCount);
    }

    [Fact]
    public void CollectObjects_VisitsOnlyNearbyCells_ButAlwaysUnplacedObjects()
    {
        GridContainer grids = Create();
        var near = new TestUnit(1, 10, 10);
        var far = new TestUnit(2, 400, 400);
        var lost = new TestUnit(3, float.NaN, 0);
        grids.Add(near, active: true);
        grids.Add(far, active: true);
        grids.Add(lost, active: false);

        var found = new List<WorldObject>();
        grids.CollectObjects(0, 0, 50, found);

        Assert.Contains(near, found);
        Assert.Contains(lost, found);
        Assert.DoesNotContain(far, found);

        found.Clear();
        grids.CollectObjects(0, 0, float.PositiveInfinity, found);
        Assert.Equal(3, found.Distinct().Count());
    }

    [Fact]
    public void Relocate_MovesTheObjectBetweenCellsAndLoadsAheadOfActiveObjects()
    {
        GridContainer grids = Create();
        var player = new TestUnit(1, 0, 0);
        grids.Add(player, active: true);

        player.SetPosition(0.5f, 0, 0, 0);
        Assert.False(grids.Relocate(player)); // same cell

        player.SetPosition(3000, 0, 0, 0);
        Assert.True(grids.Relocate(player));
        Assert.Equal(GridDefines.ComputeCellCoord(3000, 0), grids.CellOf(player));
        Assert.True(grids.IsGridLoaded(GridDefines.ComputeGridCoord(3000, 0)));
        Assert.Equal(1, grids.GetGrid(GridDefines.ComputeGridCoord(3000, 0))!.ActiveObjectCount);
    }
}
