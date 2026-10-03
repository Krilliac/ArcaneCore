using System.Buffers.Binary;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Game.Maps.Grid;
using ArcaneCore.Game.Maps.Terrain;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

/// <summary>The grid-backed <see cref="Map"/>: non-player objects, the observer index, grid lifetime.</summary>
public sealed class GridMapTests
{
    /// <summary>Long enough for an abandoned grid to pass Active → Idle → Removal at the default 5-minute delay, and for the terrain clean-up.</summary>
    private static void RunFor(WorldRuntime world, int totalMs)
    {
        for (int t = 0; t < totalMs; t += 1000)
        {
            world.RunTick(1000);
        }
    }

    [Fact]
    public void AddObject_IsSeenByPlayersInRange_AtTheNextTick()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var near = new FakeSession(1);
        var far = new FakeSession(2);
        Player a = TestWorld.CreatePlayer(1, 0, 0, near);
        Player b = TestWorld.CreatePlayer(2, 1000, 0, far);
        world.AddPlayer(a);
        world.AddPlayer(b);
        world.RunTick(50);
        near.Clear();
        far.Clear();

        Map map = world.GetMap(0);
        var unit = new TestUnit(1, 20, 0);
        map.AddObject(unit);
        Assert.Same(unit, map.FindObject(unit.Guid));
        Assert.Equal(3, map.ObjectCount);
        world.RunTick(50);

        (WorldOpcode op, byte[] payload) = near.Next();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);
        Assert.Equal((byte)ObjectUpdateType.CreateObject, payload[5]);
        Assert.Contains(unit.Guid, a.VisibleObjects);
        Assert.Contains(a, map.ObserversOf(unit));
        Assert.True(far.Sent.IsEmpty);
        Assert.DoesNotContain(b, map.ObserversOf(unit));
    }

    [Fact]
    public void MovingObject_EntersAndLeavesSight()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var session = new FakeSession(1);
        Player a = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(a);
        Map map = world.GetMap(0);
        var unit = new TestUnit(1, 300, 0);
        map.AddObject(unit);
        world.RunTick(50);
        session.Clear();
        Assert.DoesNotContain(unit.Guid, a.VisibleObjects);

        unit.SetPosition(50, 0, 0, 0);
        world.RunTick(50);
        Assert.Contains(unit.Guid, a.VisibleObjects);
        session.Clear();

        unit.SetPosition(100.5f, 0, 0, 0); // inside the grey zone: still visible
        world.RunTick(50);
        Assert.Contains(unit.Guid, a.VisibleObjects);

        unit.SetPosition(400, 0, 0, 0);
        world.RunTick(50);
        Assert.DoesNotContain(unit.Guid, a.VisibleObjects);
        Assert.Empty(map.ObserversOf(unit));
        (WorldOpcode op, byte[] update) = session.Sent.Last();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, op);
        Assert.Equal((byte)ObjectUpdateType.OutOfRangeObjects, update[5]);
    }

    [Fact]
    public void RemoveObject_DestroysItForObservers()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var session = new FakeSession(1);
        Player a = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(a);
        Map map = world.GetMap(0);
        var unit = new TestUnit(1, 5, 0);
        map.AddObject(unit);
        world.RunTick(50);
        session.Clear();

        map.RemoveObject(unit);

        (WorldOpcode op, byte[] payload) = session.Next();
        Assert.Equal(WorldOpcode.SmsgDestroyObject, op);
        Assert.Equal(unit.Guid.Value, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        Assert.Null(unit.Map);
        Assert.DoesNotContain(unit.Guid, a.VisibleObjects);
        Assert.Throws<ArgumentException>(() => map.AddObject(TestWorld.CreatePlayer(9, 0, 0, new FakeSession(9))));
    }

    [Fact]
    public void Grids_FollowThePlayers_AndHoldTheirTerrainTiles()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player a = TestWorld.CreatePlayer(1, 100, 100, new FakeSession(1));
        world.AddPlayer(a);
        Map map = world.GetMap(0);
        GridCoord grid = GridDefines.ComputeGridCoord(100, 100);
        (int tx, int ty) = TerrainTile.TileOf(grid);

        Assert.True(map.Grids.IsGridLoaded(grid));
        Assert.Equal(1, map.Terrain.RefCount(tx, ty));

        world.RemovePlayer(a);
        RunFor(world, 400_000);

        Assert.Equal(0, map.Grids.LoadedGridCount);
        Assert.Equal(0, map.Terrain.RefCount(tx, ty));
        Assert.False(map.Terrain.IsTileLoaded(tx, ty));
    }

    [Fact]
    public void RemovingObjectBeforeJoiningObserversFirstTick_SendsCreateBeforeDestroy()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var unit = new TestUnit(1, 5, 0);
        map.AddObject(unit);
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        session.Clear(); // self create has flushed; the nearby object's create is pending

        map.RemoveObject(unit);

        (WorldOpcode opcode, byte[] create) = session.Next();
        Assert.Equal(WorldOpcode.SmsgUpdateObject, opcode);
        Assert.Equal((byte)ObjectUpdateType.CreateObject, create[5]);
        Assert.Equal(WorldOpcode.SmsgDestroyObject, session.Next().Opcode);
        world.RunTick(50);
        Assert.True(session.Sent.IsEmpty);
        Assert.DoesNotContain(unit.Guid, player.VisibleObjects);
    }

    [Fact]
    public void NonPlayerObjects_InUnloadedGrids_AreRemovedWithTheGrid()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var unit = new TestUnit(1, 5000, 5000);
        map.AddObject(unit);
        RunFor(world, 400_000);

        Assert.Null(unit.Map);
        Assert.Null(map.FindObject(unit.Guid));
    }

    [Fact]
    public void ActiveObject_KeepsItsGridsLoaded()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var unit = new TestUnit(1, 5000, 5000);
        map.AddObject(unit, active: true);
        RunFor(world, 400_000);

        Assert.Same(map, unit.Map);
        map.SetActive(unit, false);
        RunFor(world, 400_000);

        Assert.Null(unit.Map);
    }
}
