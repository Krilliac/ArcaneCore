using System.Numerics;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using Xunit;

namespace ArcaneCore.Game.Tests.Collision;

/// <summary>
/// The navmesh parameter file exactly as vmangos' own generator writes it: the generator builds
/// Detour with 64-bit polygon references and so stores <c>maxPolys = 0</c> (vmangos
/// contrib/mmap/src/MapBuilder.cpp:462, "Unused if DT_POLYREF64 set"); the server reads the 28
/// bytes raw (src/game/Maps/MoveMap.cpp:86-92).
/// </summary>
public sealed class NavMeshVmangosCompatTests
{
    private const uint MapId = 0;

    private static CellTile Walled() => new(0, 0, 0, 0, 4, 4, Size: 5) { Walkable = (i, j) => !(i == 2 && j < 3) };

    [Fact]
    public void VmangosParams_WithZeroMaxPolys_Parse()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId, maxPolys: 0, maxTiles: 64);
        NavMeshParams parsed = NavMeshParams.Parse(File.ReadAllBytes(fixture.PathOf(NavMeshFormat.ParamsFileName(MapId))));

        Assert.Equal(0, parsed.MaxPolys);
        Assert.Equal(64, parsed.MaxTiles);
        Assert.Equal(533.3333f, parsed.TileWidth);
    }

    [Fact]
    public void VmangosParams_GiveARealPath_NotTheStraightFallback()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId, maxPolys: 0, maxTiles: 64);
        fixture.WriteTile(MapId, 31, 31, Walled());
        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.True(nav.LoadTile(MapId, 31, 31));

        PathResult path = nav.FindPath(MapId, new Vector3(7, 5, 0), new Vector3(17, 5, 0));

        Assert.Equal(PathType.Normal, path.Type);
        Assert.False(path.Type.HasFlag(PathType.NotUsingPath));
        Assert.Equal(4, path.Points.Count);
    }

    [Fact]
    public void CmangosStyleParams_WithPolyBudget_StillLoad()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId, maxPolys: 1 << 16);
        fixture.WriteTile(MapId, 31, 31, Walled());
        var nav = new NavMeshPathfinder(fixture.Directory);

        Assert.True(nav.LoadTile(MapId, 31, 31));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ParamsWithoutTiles_AreRejected(int maxTiles)
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId, maxPolys: 0, maxTiles: maxTiles);

        Assert.Throws<InvalidDataException>(() => NavMeshParams.Parse(File.ReadAllBytes(fixture.PathOf(NavMeshFormat.ParamsFileName(MapId)))));
    }

    [Fact]
    public void ParamsWithNonFiniteOrigin_OrWrongLength_AreRejected()
    {
        var bytes = new byte[NavMeshParams.Size];
        BitConverter.GetBytes(float.NaN).CopyTo(bytes, 0);
        BitConverter.GetBytes(533.3333f).CopyTo(bytes, 12);
        BitConverter.GetBytes(533.3333f).CopyTo(bytes, 16);
        BitConverter.GetBytes(8).CopyTo(bytes, 20);
        Assert.Throws<InvalidDataException>(() => NavMeshParams.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => NavMeshParams.Parse(new byte[NavMeshParams.Size - 4]));
    }

    [Fact]
    public void CmangosVersion8Tile_IsRejectedNamingTheGenerator()
    {
        using var fixture = new NavMeshFixture();
        fixture.WriteParams(MapId, maxPolys: 0);
        fixture.WriteTile(MapId, 31, 31, Walled(), mmapVersion: 8);
        byte[] file = File.ReadAllBytes(fixture.PathOf(NavMeshFormat.TileFileName(MapId, 31, 31)));

        var ex = Assert.Throws<InvalidDataException>(() => NavMeshTile.ParseFile(file));
        Assert.Contains("cMaNGOS", ex.Message);

        var nav = new NavMeshPathfinder(fixture.Directory);
        Assert.False(nav.LoadTile(MapId, 31, 31));
    }
}
