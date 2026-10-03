using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The path contract numbers and defaults follow vmangos: PathFinder.h:39-57 (types, 256 points),
/// PathFinder.cpp:657-693 (<c>createFilter</c>), MoveMap.cpp:350 (2048-node query), GridMap.cpp:875-878 (outdoor WMO).
/// </summary>
public sealed class PathContractsTests
{
    private const uint MapId = 0;

    [Fact]
    public void PathType_HasVmangosValues()
    {
        Assert.Equal(0x00, (int)PathType.Blank);
        Assert.Equal(0x01, (int)PathType.Normal);
        Assert.Equal(0x02, (int)PathType.Shortcut);
        Assert.Equal(0x04, (int)PathType.Incomplete);
        Assert.Equal(0x08, (int)PathType.NoPath);
        Assert.Equal(0x10, (int)PathType.NotUsingPath);
        Assert.Equal(0x20, (int)PathType.DestForced);
        Assert.Equal(0x40, (int)PathType.FlyPath);
        Assert.Equal(0x80, (int)PathType.Underwater);
        Assert.Equal(0x100, (int)PathType.Caster);
        Assert.Equal(0x200, (int)PathType.Short); // ArcaneCore extension, clear of vmangos' bits
    }

    [Fact]
    public void DefaultOptions_ExcludeNothing_AndUseTheVmangosLimits()
    {
        var options = new PathOptions();
        Assert.Equal(NavTerrain.Empty, options.ExcludeFlags);
        Assert.Equal(256, options.MaxPoints);
        Assert.Equal(2048, options.MaxSearchNodes);
    }

    [Theory]
    [InlineData(true, false, false, NavTerrain.Ground)]
    [InlineData(true, true, true, NavTerrain.Ground | NavTerrain.Water)]
    [InlineData(true, true, false, NavTerrain.Ground | NavTerrain.Water | NavTerrain.Magma | NavTerrain.Slime)]
    [InlineData(false, true, false, NavTerrain.Water | NavTerrain.Magma | NavTerrain.Slime)]
    [InlineData(false, false, false, NavTerrain.Empty)]
    public void Mover_DerivesTheFilterLikeCreateFilter(bool walk, bool swim, bool player, NavTerrain expected)
    {
        var mover = new PathMover(walk, swim, CanFly: false, player);
        Assert.Equal(expected, mover.IncludeFlags);
        Assert.Equal(expected, new PathOptions { Mover = mover }.EffectiveIncludeFlags);
    }

    [Fact]
    public void WithoutAMover_TheExplicitIncludeFlagsApply()
    {
        Assert.Equal(NavTerrain.Ground | NavTerrain.Water | NavTerrain.Magma | NavTerrain.Slime, new PathOptions().EffectiveIncludeFlags);
        Assert.Equal(NavTerrain.Ground, new PathOptions { IncludeFlags = NavTerrain.Ground }.EffectiveIncludeFlags);
    }

    [Fact]
    public void OutdoorWmoGroup_IsFlagged0x8000_NotTheOldBit()
    {
        Assert.True(new ModelAreaInfo(0x8000, 0, 0, 0, 0).IsOutdoors);
        Assert.False(new ModelAreaInfo(0x8, 0, 0, 0, 0).IsOutdoors);
        Assert.False(new ModelAreaInfo(ModelAreaInfo.MogpInterior, 0, 0, 0, 0).IsOutdoors);
    }

    /// <summary>4×4 cells of 5 yd; the column i == 2 (Y 0-15) is steep slope terrain, the rest ground.</summary>
    private static CellTile SteepWall() => new(0, 0, 0, 0, 4, 4, Size: 5)
    {
        Flags = (i, j) => i == 2 && j < 3 ? NavTerrain.Ground | NavTerrain.SteepSlopes : NavTerrain.Ground,
    };

    [Fact]
    public void SteepSlopes_AreWalkedByDefault_AndAvoidedWhenExcluded()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        fixture.WriteTile(MapId, 31, 31, SteepWall());
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        var start = new Vector3(7, 5, 0);
        var end = new Vector3(17, 5, 0);

        Assert.Equal(2, nav.FindPath(MapId, start, end).Points.Count);
        Assert.Equal(4, nav.FindPath(MapId, start, end, new PathOptions { ExcludeFlags = NavTerrain.SteepSlopes }).Points.Count);
    }

    [Fact]
    public void WalkerMover_DoesNotCrossWater_ButASwimmerDoes()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId);
        var lake = new CellTile(0, 0, 0, 0, 4, 4, Size: 5)
        {
            Flags = (i, j) => i == 2 && j < 3 ? NavTerrain.Water : NavTerrain.Ground,
        };
        fixture.WriteTile(MapId, 31, 31, lake);
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));
        var start = new Vector3(7, 5, 0);
        var end = new Vector3(17, 5, 0);

        PathResult walker = nav.FindPath(MapId, start, end, new PathOptions { Mover = new PathMover(true, false, false, true) });
        PathResult swimmer = nav.FindPath(MapId, start, end, new PathOptions { Mover = PathMover.Player });

        Assert.Equal(4, walker.Points.Count); // around the lake
        Assert.Equal(2, swimmer.Points.Count); // straight through it
    }
}
