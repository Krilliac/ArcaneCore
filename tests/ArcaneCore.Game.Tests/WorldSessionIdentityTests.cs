using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests;

public sealed class WorldSessionIdentityTests
{
    [Fact]
    public void OldSessionCallbacks_CannotRemoveLogoutOrSaveAReplacementPlayer()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves);
        var oldSession = new FakeSession();
        var oldPlayer = TestWorld.CreatePlayer(1, 0, 0, oldSession);
        world.AddPlayer(oldPlayer);
        world.RemovePlayer(oldPlayer);
        saves.Saved.Clear();
        var replacement = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        replacement.Money = 1234;
        world.AddPlayer(replacement);

        world.RemovePlayer(oldPlayer);
        world.LogoutPlayer(oldPlayer);
        world.SavePlayer(oldPlayer);

        Assert.Same(replacement, world.FindOnlinePlayer(replacement.Guid));
        Assert.Same(world.GetMap(0), replacement.Map);
        Assert.Empty(saves.Saved);
        Assert.Equal(0, oldSession.LoggedOutCount);
        world.SavePlayer(replacement);
        Assert.Equal(1234u, Assert.Single(saves.Saved).Money);
    }

    [Fact]
    public void Logout_PublishesOfflineOnlyAfterEnqueuingTheLastSnapshot()
    {
        var saves = new ObservingSaveQueue();
        using var world = new WorldRuntime(new WorldRuntimeOptions(), saves, NullLogger<WorldRuntime>.Instance);
        var player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        saves.Observe = () => Assert.True(world.IsOnline(player.Guid));

        world.RemovePlayer(player);

        Assert.Equal(1, saves.Count);
        Assert.False(world.IsOnline(player.Guid));
    }

    private sealed class ObservingSaveQueue : ICharacterSaveQueue
    {
        public Action? Observe { get; set; }
        public int Count { get; private set; }
        public void Enqueue(CharacterState state)
        {
            Observe?.Invoke();
            Count++;
        }
    }
}
