using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests.GridTerrain;

public sealed class HeartbeatSnapshotTests
{
    [Fact]
    public void StableMembership_DoesNotRescanMapObjects_EveryTick()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        for (uint i = 1; i <= 1000; i++) map.AddObject(new TestUnit(i, 0, 0));
        world.RunTick(50);
        long scanned = map.HeartbeatSnapshotObjectsVisited;
        for (int i = 0; i < 100; i++) world.RunTick(50);
        Assert.Equal(scanned, map.HeartbeatSnapshotObjectsVisited);
    }

    [Fact]
    public void HeartbeatMutation_SkipsRemovedUnit_AndStartsNewUnitOnNextTick()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var first = new TestUnit(1, 0, 0);
        var removed = new TestUnit(2, 0, 0);
        var added = new TestUnit(3, 0, 0);
        int firstBeats = 0, removedBeats = 0, addedBeats = 0;
        first.Heartbeat += _ =>
        {
            if (++firstBeats == 1)
            {
                map.RemoveObject(removed);
                map.AddObject(added);
            }
        };
        removed.Heartbeat += _ => removedBeats++;
        added.Heartbeat += _ => addedBeats++;
        map.AddObject(first); map.AddObject(removed);
        world.RunTick(Unit.HeartbeatIntervalMs);
        Assert.Equal(1, firstBeats);
        Assert.Equal(0, removedBeats);
        Assert.Equal(0, addedBeats);
        world.RunTick(Unit.HeartbeatIntervalMs - 1);
        Assert.Equal(0, addedBeats);
        world.RunTick(1);
        Assert.Equal(2, firstBeats);
        Assert.Equal(1, addedBeats);
        Assert.Equal(0, removedBeats);
        map.RemoveObject(first);
        map.RemoveObject(added);
        world.RunTick(Unit.HeartbeatIntervalMs);
        Assert.Equal(2, firstBeats);
        Assert.Equal(1, addedBeats);
    }

    [Fact]
    public void PlayerLeaveAndRejoin_UsesCurrentSnapshot_AndPreservesHeartbeatTimer()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        int beats = 0;
        player.Heartbeat += _ => beats++;
        world.AddPlayer(player);
        world.RunTick(Unit.HeartbeatIntervalMs - 1);
        map.RemovePlayer(player);
        world.RunTick(Unit.HeartbeatIntervalMs);
        Assert.Equal(0, beats);
        map.AddPlayer(player);
        world.RunTick(1);
        Assert.Equal(1, beats);
    }
}
