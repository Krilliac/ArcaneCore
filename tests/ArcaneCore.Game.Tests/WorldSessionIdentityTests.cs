using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.Game.Tests;

public sealed class WorldSessionIdentityTests
{
    [Fact]
    public void SettlementSaveHoldSkipsSnapshotCreation_AndDisconnectStillRemovesThePlayer()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves);
        var player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);
        Assert.True(player.SetActionButton(0, 123));
        Guid operation = Guid.NewGuid();
        Assert.True(player.BeginQuestSettlement(operation));

        world.SavePlayer(player);
        world.SaveAll();
        world.RemovePlayer(player);

        Assert.Empty(saves.Saved);
        Assert.False(world.IsOnline(player.Guid));
        Assert.Null(player.Map);
        Assert.True(player.EndQuestSettlement(operation));
        CharacterState final = player.CreateSnapshot(world.NowMs);
        Assert.NotNull(final.ActionButtons);
        Assert.Equal(123u, Assert.Single(final.ActionButtons!).Action);
    }

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

    [Fact]
    public void ASaveIsPendingWork_UntilTheQueueSettlesIt()
    {
        var saves = new DeferringSaveQueue();
        using var world = new WorldRuntime(new WorldRuntimeOptions(), saves, NullLogger<WorldRuntime>.Instance);
        var player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);

        world.SavePlayer(player);

        Assert.Equal(1, world.PendingWork.Count);
        Assert.StartsWith("save of character ", Assert.Single(world.PendingWork.Names()), StringComparison.Ordinal);
        saves.SettleAll();
        Assert.Equal(0, world.PendingWork.Count);
    }

    [Fact]
    public void ASaveTheQueueRefusesToTake_DoesNotLeavePendingWorkBehind()
    {
        var saves = new DeferringSaveQueue { ThrowOnEnqueue = true };
        using var world = new WorldRuntime(new WorldRuntimeOptions(), saves, NullLogger<WorldRuntime>.Instance);
        var player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);

        Assert.Throws<InvalidOperationException>(() => world.SavePlayer(player));

        Assert.Equal(0, world.PendingWork.Count);
    }

    [Fact]
    public void AQueueWithoutACompletionSignal_SettlesAtOnce_SoNothingStaysPending()
    {
        var saves = new RecordingSaveQueue();
        using WorldRuntime world = TestWorld.CreateRuntime(saves);
        var player = TestWorld.CreatePlayer(1, 0, 0, new FakeSession());
        world.AddPlayer(player);

        world.SavePlayer(player);

        Assert.Single(saves.Saved);
        Assert.Equal(0, world.PendingWork.Count);
    }

    private sealed class DeferringSaveQueue : ICharacterSaveQueue
    {
        private readonly List<Action> _settle = [];
        public bool ThrowOnEnqueue { get; init; }
        public void Enqueue(CharacterState state) => throw new InvalidOperationException("plain Enqueue is not used by the world any more");
        public void Enqueue(CharacterState state, Action onSettled)
        {
            if (ThrowOnEnqueue) throw new InvalidOperationException("queue closed");
            _settle.Add(onSettled);
        }

        public void SettleAll()
        {
            foreach (Action settle in _settle) settle();
            _settle.Clear();
        }
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
