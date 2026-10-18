using System.Numerics;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Collision;
using ArcaneCore.Game.Maps.Collision.MMaps;
using ArcaneCore.Game.Maps.Collision.VMaps;
using ArcaneCore.Game.Tests.Collision;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Playerbots;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Elevator;

/// <summary>
/// The collision tiles <see cref="PlayerbotElevator.LandingBeside"/> needs at a lift stop no grid covers (a lift far from the bot): the
/// stop's model tile (floor heights, line of sight) and navigation tile (mesh floor, walkable mesh). They are loaded for the question,
/// not as if a grid stood on them: the grid hooks (<see cref="ICollisionTileLifecycle"/>) are the grids' own, and a tile loaded through
/// them stays loaded until a grid on it unloads, outside every bound on tiles loaded for queries. One row of synthetic tiles on map 0, no
/// grid loaded.
/// </summary>
public sealed class PlayerbotElevatorStopTileTests : IDisposable
{
    private const uint MapId = 0;
    private const int TileRow = 30;

    /// <summary>More stops, each on its own tile, than either service keeps loaded for queries on one map.</summary>
    private const int Tiles = 64;

    private static readonly PathOptions Loading = new() { LoadTiles = true, AllowPartial = true };

    private readonly NavMeshFixture _mesh = new();
    private readonly VMapFixture _models = new();
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
    private readonly WorldRuntime _world;
    private readonly NavMeshPathfinder _nav;
    private readonly VMapManager _vmaps;

    public PlayerbotElevatorStopTileTests()
    {
        _mesh.WriteParams(MapId);
        for (int x = 0; x < Tiles; x++)
        {
            _mesh.WriteTile(MapId, x, TileRow, new CellTile(x, 0, 0, 0, 2, 2, Size: 5));
        }

        // A wall beside the first stop gives the map its model tree; every other stop's tile is a model tile with nothing in it.
        _models.Place("wall", VMapFixture.Box(new Vector3(-1, -10, 0), new Vector3(1, 10, 10)), StopOn(0) + new Vector3(20, 0, 0));
        _models.Write(MapId);
        for (int x = 1; x < Tiles; x++)
        {
            _models.WriteTile(MapId, x, TileRow, []);
        }

        _nav = new NavMeshPathfinder(_mesh.Directory);
        _vmaps = new VMapManager(_models.Directory);
        var saves = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        _world = new WorldRuntime(new WorldRuntimeOptions { AutosaveIntervalMs = 0 }, saves, NullLogger<WorldRuntime>.Instance);
        WorldCollision.Of(_world).Install(lineOfSight: _vmaps, pathfinder: _nav);
    }

    public void Dispose()
    {
        _world.Dispose();
        _services.Dispose();
        _models.Dispose();
        _mesh.Dispose();
    }

    /// <summary>The centre of terrain tile (<paramref name="tileX"/>, <see cref="TileRow"/>).</summary>
    private static Vector3 StopOn(int tileX) => new((32f - tileX - 0.5f) * 533.33333f, (32f - TileRow - 0.5f) * 533.33333f, 0f);

    private Map Map => _world.GetMap(MapId);

    [Fact]
    public void TheStopsNavigationTile_IsReleasedLikeAnyTileReadForAQuery()
    {
        NavMesh mesh = _nav.GetNavMesh(MapId)!;
        PlayerbotElevator.LandingBeside(Map, StopOn(0), StopOn(1));
        Assert.True(mesh.IsTerrainTileLoaded(0, TileRow), "the landing search did not have the stop's tile");

        // A bot's path queries read the tiles they cross that no grid holds (PathOptions.LoadTiles); once they need more than the bound,
        // the tile used longest ago, the stop's, makes room.
        for (int x = 1; x <= NavMeshPathfinder.MaxOnDemandTiles; x++)
        {
            _nav.FindPath(MapId, StopOn(x), StopOn(x), Loading);
        }

        Assert.False(mesh.IsTerrainTileLoaded(0, TileRow), "the stop's navigation tile is held as if a grid stood on it");
        Assert.True(mesh.IsTerrainTileLoaded(NavMeshPathfinder.MaxOnDemandTiles, TileRow));
        Assert.Equal(NavMeshPathfinder.MaxOnDemandTiles, _nav.OnDemandTileCount);
    }

    [Fact]
    public void LandingSearchesAtStopsNoGridCovers_KeepABoundedNumberOfTiles()
    {
        for (int x = 0; x < Tiles; x++)
        {
            PlayerbotElevator.LandingBeside(Map, StopOn(x), StopOn((x + 1) % Tiles));
        }

        VMapTree tree = _vmaps.GetTree(MapId)!;
        NavMesh mesh = _nav.GetNavMesh(MapId)!;
        Assert.True(tree.IsTileLoaded(Tiles - 1, TileRow), "the last landing search did not have the stop's model tile");
        Assert.True(mesh.IsTerrainTileLoaded(Tiles - 1, TileRow), "the last landing search did not have the stop's navigation tile");
        Assert.False(tree.IsTileLoaded(0, TileRow), "every stop's model tile stays loaded");
        Assert.False(mesh.IsTerrainTileLoaded(0, TileRow), "every stop's navigation tile stays loaded");
        Assert.InRange(tree.LoadedTiles.Count, 1, Tiles - 1);
        Assert.InRange(_nav.OnDemandTileCount, 1, NavMeshPathfinder.MaxOnDemandTiles);
    }
}
