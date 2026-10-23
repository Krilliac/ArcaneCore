using ArcaneCore.Game.Maps.Grid;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>
/// Pins the world-position to grid mapping that <c>Creatures.GridCoord</c>/<c>CreatureMapSystem.ComputeGrid</c> duplicated
/// and that the refactor folds onto <see cref="GridDefines.ComputeGridCoord"/>. The expected pairs are the ones the old
/// CreatureTests.ComputeGrid_MatchesVMangosGridPair asserted (vmangos GridDefines.h ComputeGridPair).
/// </summary>
public sealed class GridCoordComputationCharacterisationTests
{
    [Theory]
    [InlineData(0f, 0f, 32, 32)]
    [InlineData(533.0f, 533.0f, 32, 32)]
    [InlineData(533.4f, -0.1f, 33, 31)]
    // Northshire Abbey: ((-8949.95 - 266.67) / 533.33 + 32.5) = 15.2; y -> 31.7.
    [InlineData(-8949.95f, -132.49f, 15, 31)]
    [InlineData(-1e6f, 1e6f, 0, 63)]
    [InlineData(1e9f, -1e9f, 63, 0)]
    public void ComputeGridCoord_MatchesVMangosGridPair(float x, float y, int gx, int gy)
    {
        Assert.Equal(new GridCoord(gx, gy), GridDefines.ComputeGridCoord(x, y));
    }

    [Fact]
    public void GridConstants_AreTheVMangosValues()
    {
        Assert.Equal(64, GridDefines.MaxNumberOfGrids);
        Assert.Equal(533.33333f, GridDefines.SizeOfGrids);
        Assert.Equal(32, GridDefines.CenterGridId);
        Assert.Equal(533.33333f / 2, GridDefines.CenterGridOffset);
    }

    [Fact]
    public void GridCoord_ToString_IsBracketedPair_AndIdIsXTimes64PlusY()
    {
        Assert.Equal("[33,31]", new GridCoord(33, 31).ToString());
        Assert.Equal((33 * 64) + 31, new GridCoord(33, 31).Id);
    }
}
