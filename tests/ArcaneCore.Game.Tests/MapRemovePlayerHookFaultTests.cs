using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>Map.RemovePlayer finishes removing the player from the world even when an updater's OnPlayerRemoved throws.</summary>
public sealed class MapRemovePlayerHookFaultTests
{
    private sealed class RemoveHookProbeException : Exception;

    private sealed class ThrowingOnRemovedUpdater : IMapUpdater
    {
        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player) => throw new RemoveHookProbeException();
    }

    private sealed class CountingUpdater : IMapUpdater
    {
        public int Removed { get; private set; }

        public void Update(Map map, uint diffMs)
        {
        }

        public void OnPlayerRemoved(Map map, Player player) => Removed++;
    }

    [Fact]
    public void ThrowingOnPlayerRemoved_StillPropagates_ButPlayerLeavesObjectsAndObserverVisibleSet()
    {
        using var world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Player observer = TestWorld.CreatePlayer(1, 10f, 10f, new FakeSession());
        Player leaver = TestWorld.CreatePlayer(2, 11f, 11f, new FakeSession());
        map.AddPlayer(observer);
        map.AddPlayer(leaver);
        Assert.Contains(leaver.Guid, observer.VisibleObjects);
        map.AddUpdater(new ThrowingOnRemovedUpdater());

        Assert.Throws<RemoveHookProbeException>(() => map.RemovePlayer(leaver));

        Assert.Null(map.FindObject(leaver.Guid));
        Assert.DoesNotContain(leaver.Guid, observer.VisibleObjects);
        Assert.Equal(1, map.PlayerCount);
        Assert.False(leaver.NeedsVisibilityUpdate);
    }

    [Fact]
    public void ThrowingOnPlayerRemoved_SkipsLaterUpdaters_ContinueSemanticsUnchanged()
    {
        using var world = TestWorld.CreateRuntime();
        Map map = world.GetMap(0);
        Player leaver = TestWorld.CreatePlayer(3, 10f, 10f, new FakeSession());
        map.AddPlayer(leaver);
        var after = new CountingUpdater();
        map.AddUpdater(new ThrowingOnRemovedUpdater());
        map.AddUpdater(after);

        Assert.Throws<RemoveHookProbeException>(() => map.RemovePlayer(leaver));

        Assert.Equal(0, after.Removed);
        Assert.Null(map.FindObject(leaver.Guid));
    }
}
