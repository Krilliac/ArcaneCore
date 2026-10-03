using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Maps;
using Xunit;

namespace ArcaneCore.Game.Tests;

/// <summary>The world's player lifecycle events (the seam world features subscribe to).</summary>
public sealed class WorldEventsTests
{
    [Fact]
    public void PlayerLoggingOut_IsRaisedOnce_WhileThePlayerIsStillInTheWorld()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(player);

        var seen = new List<(bool Online, bool InMap)>();
        world.PlayerLoggingOut += p => seen.Add((world.IsOnline(p.Guid), p.Map is not null));

        world.RemovePlayer(player);
        world.RemovePlayer(player);

        Assert.Equal([(true, true)], seen);
        Assert.False(world.IsOnline(player.Guid));
    }

    [Fact]
    public void LogoutCompletion_RaisesPlayerLoggingOut()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        var session = new FakeSession(1);
        Player player = TestWorld.CreatePlayer(1, 0, 0, session);
        world.AddPlayer(player);
        int raised = 0;
        world.PlayerLoggingOut += _ => raised++;

        world.LogoutPlayer(player);

        Assert.Equal(1, raised);
        Assert.Equal(1, session.LoggedOutCount);
    }

    [Fact]
    public void AFailingHandler_DoesNotStopTheOthers_OrTheRemoval()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves);
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(player);
        int later = 0;
        world.PlayerLoggingOut += _ => throw new InvalidOperationException("feature bug");
        world.PlayerLoggingOut += _ => later++;

        world.RemovePlayer(player);

        Assert.Equal(1, later);
        Assert.False(world.IsOnline(player.Guid));
        Assert.Single(saves.Saved);
    }

    [Fact]
    public void NotifyLoggedIn_RaisesPlayerLoggedIn()
    {
        using WorldRuntime world = TestWorld.CreateRuntime();
        Player player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession(1));
        world.AddPlayer(player);
        Player? seen = null;
        world.PlayerLoggedIn += _ => throw new InvalidOperationException("feature bug");
        world.PlayerLoggedIn += p => seen = p;

        world.NotifyLoggedIn(player);

        Assert.Same(player, seen);
    }
}
