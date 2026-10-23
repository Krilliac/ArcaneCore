using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Maps;

/// <summary>
/// Map.RemovePlayer against an <see cref="IMapUpdater"/> whose OnPlayerRemoved throws: the exception still propagates
/// (unchanged failure behaviour), but the player must not be left in the world (objects, grid, observers' visible sets).
/// </summary>
public sealed class MapRemovePlayerFaultTests
{
    private sealed class ThrowingRemoveUpdater : IMapUpdater
    {
        public int RemovedCalls { get; private set; }

        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
            RemovedCalls++;
            throw new InvalidOperationException("updater fault in OnPlayerRemoved");
        }
    }

    [Fact]
    public void RemovePlayer_UpdaterThrows_ExceptionPropagates_ButPlayerIsStillRemovedFromTheWorld()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var sa = new FakeSession(1);
        var sb = new FakeSession(2);
        Player observer = TestWorld.CreatePlayer(1, 0, 0, sa);
        Player leaving = TestWorld.CreatePlayer(2, 5, 0, sb);
        world.AddPlayer(observer);
        world.AddPlayer(leaving);
        world.RunTick(50);

        Map map = leaving.Map!;
        Assert.Contains(leaving.Guid, observer.VisibleObjects);
        var updater = new ThrowingRemoveUpdater();
        map.AddUpdater(updater);
        sa.Clear();

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => map.RemovePlayer(leaving));

        Assert.Equal("updater fault in OnPlayerRemoved", ex.Message);
        Assert.Equal(1, updater.RemovedCalls);
        Assert.Null(map.FindPlayer(leaving.Guid));
        Assert.Null(map.FindObject(leaving.Guid));
        Assert.DoesNotContain(leaving.Guid, observer.VisibleObjects);
        Assert.Null(leaving.Map);
        Assert.Equal(leaving.Guid.Value, BitConverter.ToUInt64(Assert.Single(sa.Sent, p => p.Opcode == WorldOpcode.SmsgDestroyObject).Payload));
    }
}
