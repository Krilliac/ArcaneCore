using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Map.RemovePlayer must take the player out of the world even when an updater's OnPlayerRemoved throws (the exception still propagates).</summary>
public sealed class MapRemovePlayerUpdaterFaultTests
{
    private sealed class RemoveHookProbeException : Exception;

    private sealed class ThrowingOnRemovedUpdater : IMapUpdater
    {
        public int RemovedCalls { get; private set; }

        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player)
        {
            RemovedCalls++;
            throw new RemoveHookProbeException();
        }
    }

    [Fact]
    public void AThrowingOnPlayerRemoved_StillRemovesThePlayerFromTheWorld_AndPropagates()
    {
        using var world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        var updater = new ThrowingOnRemovedUpdater();
        map.AddUpdater(updater);
        Player player = TestWorld.CreatePlayer(991_501, 10f, 10f, new FakeSession());
        world.AddPlayer(player);
        Assert.Same(player, map.FindObject(player.Guid));

        Assert.Throws<RemoveHookProbeException>(() => map.RemovePlayer(player));

        Assert.Equal(1, updater.RemovedCalls);
        Assert.Null(map.FindObject(player.Guid));
    }
}
