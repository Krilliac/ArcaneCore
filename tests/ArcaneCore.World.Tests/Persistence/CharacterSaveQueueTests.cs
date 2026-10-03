using System.Collections.Concurrent;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.World.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

public sealed class CharacterSaveQueueTests
{
    [Fact]
    public async Task BarrierWaitsForEarlierSnapshotsAndRetainsTheirOrder()
    {
        await using var kit = new Kit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (state, token) =>
        {
            if (state.Money == 11)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task barrier = kit.Queue.FlushCharacterAsync(1);
        Assert.False(barrier.IsCompleted);
        kit.Queue.Enqueue(State(1, 22));
        release.TrySetResult();
        await barrier.WaitAsync(TimeSpan.FromSeconds(5));
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new uint[] { 11, 22 }, kit.Store.Saved.Select(s => s.Money));
        Assert.Equal(0, kit.Queue.Pending);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task FailedOptionalDataIsMergedIntoTheNewerSnapshotBeforeSaving()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = (_, _) => kit.Store.Attempts.Count <= 3
            ? Task.FromException(new IOException("injected character failure")) : Task.CompletedTask;
        CharacterState earlier = State(1, 11) with
        {
            ActionButtons = [new ActionButton(1, 123, 0)],
            Home = new HomeBind(1, 2, 3, 4, 5),
            Inventory = new InventorySnapshot([new InventoryItemData(0, 23,
                new ItemInstanceData { Guid = 7, Entry = 117, Count = 4 })]),
        };
        kit.Queue.Enqueue(earlier);
        kit.Queue.Enqueue(State(1, 22));
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        CharacterState saved = Assert.Single(kit.Store.Saved);
        Assert.Equal(22u, saved.Money);
        Assert.Equal(earlier.ActionButtons, saved.ActionButtons);
        Assert.Equal(earlier.Home, saved.Home);
        Assert.Equal(4u, saved.Inventory!.Items[0].Item.Count);
        Assert.Equal(4, kit.Store.Attempts.Count);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task BarrierPropagatesUnresolvedFailureAndNextBarrierRetriesRetainedState()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = (_, _) => Task.FromException(new IOException("still unavailable"));
        kit.Queue.Enqueue(State(1, 33));
        await Assert.ThrowsAsync<IOException>(() => kit.Queue.FlushCharacterAsync(1));
        Assert.Empty(kit.Store.Saved);
        Assert.Equal(0, kit.Queue.Pending);
        kit.Store.OnSave = null;
        await kit.Queue.FlushCharacterAsync(1);
        Assert.Equal(33u, Assert.Single(kit.Store.Saved).Money);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task QuarantineSuppressesQueuedSnapshotsAndBarrierCannotResumeTheId()
    {
        await using var kit = new Kit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (state, token) =>
        {
            if (state.Id == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        kit.Queue.Enqueue(State(2, 10));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.QuarantineCharacter(1);
        kit.Queue.Enqueue(State(1, 22));
        Task drain = kit.Queue.FlushCharacterAsync(1);
        release.TrySetResult();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(kit.Queue.IsQuarantined(1));
        Assert.DoesNotContain(kit.Store.Attempts, s => s.Id == 1);
        Assert.Equal(0, kit.Queue.Pending);
        kit.Queue.ResumeCharacter(1);
        Assert.False(kit.Queue.IsQuarantined(1));
        kit.Queue.Enqueue(State(1, 55));
        await kit.Queue.FlushCharacterAsync(1);
        Assert.Equal(55u, Assert.Single(kit.Store.Saved, s => s.Id == 1).Money);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task CancellationDoesNotLoseRetainedState()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = (_, _) => Task.FromException(new IOException("unavailable"));
        kit.Queue.Enqueue(State(1, 44));
        await Assert.ThrowsAsync<IOException>(() => kit.Queue.FlushCharacterAsync(1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => kit.Queue.FlushCharacterAsync(1, canceled.Token));
        kit.Store.OnSave = null;
        await kit.Queue.FlushCharacterAsync(1);
        Assert.Equal(44u, Assert.Single(kit.Store.Saved).Money);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task ShutdownRetriesAndPropagatesRetainedFailures()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = (_, _) => Task.FromException(new IOException("persistent outage"));
        kit.Queue.Enqueue(State(1, 66));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Queue.StopAsync());
        Assert.IsType<IOException>(error.InnerException);
        Assert.Contains("1", error.Message);
        Assert.Equal(6, kit.Store.Attempts.Count);
        Assert.Equal(0, kit.Queue.Pending);
    }

    [Fact]
    public async Task EnqueuedOptionalBuffersAreOwnedByTheQueue()
    {
        await using var kit = new Kit();
        var buttons = new List<ActionButton> { new(1, 123, 0) };
        int[] charges = [2];
        var rows = new List<InventoryItemData> { new(0, 23,
            new ItemInstanceData { Guid = 7, Entry = 117, Charges = charges }) };
        kit.Queue.Enqueue(State(1, 77) with { ActionButtons = buttons, Inventory = new InventorySnapshot(rows) });
        buttons.Clear();
        charges[0] = 9;
        rows.Clear();
        await kit.Queue.FlushCharacterAsync(1);
        CharacterState saved = Assert.Single(kit.Store.Saved);
        Assert.Single(saved.ActionButtons!);
        Assert.Equal(2, Assert.Single(saved.Inventory!.Items).Item.Charges[0]);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task HoldDrainsEarlierSnapshotsAndTrustedSettlementBypassesOnlyTheHold()
    {
        await using var kit = new Kit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (state, token) =>
        {
            if (state.Money == 11)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        kit.Queue.Enqueue(State(1, 12));
        kit.Queue.HoldCharacter(1);
        kit.Queue.Enqueue(State(1, 22));
        Task trusted = kit.Queue.SaveForSettlementAsync(State(1, 33));
        kit.Queue.Enqueue(State(1, 44));
        Assert.False(trusted.IsCompleted);
        release.TrySetResult();
        await trusted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new uint[] { 11, 12, 33 }, kit.Store.Saved.Select(s => s.Money));
        Assert.True(kit.Queue.IsHeld(1));
        kit.Queue.ResumeCharacter(1);
        kit.Queue.Enqueue(State(1, 55));
        await kit.Queue.FlushCharacterAsync(1);
        Assert.Equal(new uint[] { 11, 12, 33, 55 }, kit.Store.Saved.Select(s => s.Money));
        Assert.False(kit.Queue.IsHeld(1));
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task TrustedSettlementFullyObservesAStoreThatIgnoresCancellation()
    {
        await using var kit = new Kit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        using var canceled = new CancellationTokenSource();
        kit.Queue.HoldCharacter(1);
        Task trusted = kit.Queue.SaveForSettlementAsync(State(1, 77), canceled.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel();
        Assert.False(trusted.IsCompleted);
        Assert.Empty(kit.Store.Saved);
        Task shutdown = kit.Queue.StopAsync();
        Assert.False(shutdown.IsCompleted);
        release.TrySetResult();
        await trusted.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(77u, Assert.Single(kit.Store.Saved).Money);
        Assert.True(kit.Queue.IsHeld(1));
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task QuarantineRejectsTrustedQueuedSnapshotsWithoutStrandingTheirCompletion()
    {
        await using var kit = new Kit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (state, token) =>
        {
            if (state.Id == 2)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        kit.Queue.Enqueue(State(2, 10));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        kit.Queue.HoldCharacter(1);
        Task trusted = kit.Queue.SaveForSettlementAsync(State(1, 33));
        kit.Queue.QuarantineCharacter(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Queue.SaveForSettlementAsync(State(1, 44)));
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => trusted.WaitAsync(TimeSpan.FromSeconds(5)));
        await kit.Queue.FlushCharacterAsync(1);
        Assert.DoesNotContain(kit.Store.Attempts, state => state.Id == 1);
        Assert.True(kit.Queue.IsHeld(1));
        Assert.True(kit.Queue.IsQuarantined(1));
        Assert.Equal(0, kit.Queue.Pending);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task TrustedSettlementCarriesRetainedOptionalDataIntoItsOrderedSave()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = (_, _) => kit.Store.Attempts.Count <= 3
            ? Task.FromException(new IOException("injected character failure")) : Task.CompletedTask;
        CharacterState earlier = State(1, 11) with
        {
            ActionButtons = [new ActionButton(1, 123, 0)],
            Home = new HomeBind(1, 2, 3, 4, 5),
        };
        kit.Queue.Enqueue(earlier);
        kit.Queue.HoldCharacter(1);
        await kit.Queue.SaveForSettlementAsync(State(1, 22)).WaitAsync(TimeSpan.FromSeconds(5));
        CharacterState saved = Assert.Single(kit.Store.Saved);
        Assert.Equal(22u, saved.Money);
        Assert.Equal(earlier.ActionButtons, saved.ActionButtons);
        Assert.Equal(earlier.Home, saved.Home);
        Assert.True(kit.Queue.IsHeld(1));
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task FailedTrustedBeforeSnapshotRetainsConsumedDirtyFieldsWhileDisconnectSavesAreHeld()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = (_, _) => Task.FromException(new IOException("pre-settlement save failed"));
        CharacterState captured = State(1, 11) with
        {
            ActionButtons = [new ActionButton(1, 123, 0)],
            Home = new HomeBind(1, 2, 3, 4, 5),
            Inventory = new InventorySnapshot([new InventoryItemData(0, 23,
                new ItemInstanceData { Guid = 7, Entry = 117, Count = 4 })]),
        };
        kit.Queue.HoldCharacter(1);
        await Assert.ThrowsAsync<IOException>(() => kit.Queue.SaveForSettlementAsync(captured));
        // CreateSnapshot's dirty flags have been consumed. The disconnect snapshot must
        // neither replace the trusted captured state nor lose its optional changes.
        kit.Queue.Enqueue(State(1, 22));
        Assert.Empty(kit.Store.Saved);
        kit.Store.OnSave = null;
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        CharacterState recovered = Assert.Single(kit.Store.Saved);
        Assert.Equal(11u, recovered.Money);
        Assert.Equal(captured.ActionButtons, recovered.ActionButtons);
        Assert.Equal(captured.Home, recovered.Home);
        Assert.Equal(4u, Assert.Single(recovered.Inventory!.Items).Item.Count);
        Assert.True(kit.Queue.IsHeld(1));
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task AuthoritativeResumeDiscardsFailuresFromTheQuarantinedOldSession()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = (_, _) => Task.FromException(new IOException("old session failure"));
        kit.Queue.Enqueue(State(1, 11) with
        {
            Inventory = new InventorySnapshot([new InventoryItemData(0, 23,
                new ItemInstanceData { Guid = 7, Entry = 117, Count = 4 })]),
        });
        await Assert.ThrowsAsync<IOException>(() => kit.Queue.FlushCharacterAsync(1));
        kit.Queue.HoldCharacter(1);
        kit.Queue.QuarantineCharacter(1);
        await kit.Queue.FlushCharacterAsync(1);
        kit.Store.OnSave = null;
        kit.Queue.ResumeCharacter(1);
        kit.Queue.Enqueue(State(1, 99));
        await kit.Queue.FlushCharacterAsync(1);
        CharacterState saved = Assert.Single(kit.Store.Saved);
        Assert.Equal(99u, saved.Money);
        Assert.Null(saved.Inventory);
        Assert.False(kit.Queue.IsHeld(1));
        Assert.False(kit.Queue.IsQuarantined(1));
        await kit.Queue.StopAsync();
    }

    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 83.5f, 0, 1, 0, Money: money);

    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public Kit()
        {
            _services = new ServiceCollection().AddSingleton<ICharacterStore>(Store).BuildServiceProvider();
            Queue = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance);
        }

        public RecordingStore Store { get; } = new();
        public CharacterSaveQueue Queue { get; }
        public async ValueTask DisposeAsync()
        {
            Store.OnSave = null;
            try
            {
                await Queue.StopAsync();
            }
            catch (InvalidOperationException)
            {
                // The shutdown-failure test has already asserted this completed worker failure.
            }

            await _services.DisposeAsync();
        }
    }

    private sealed class RecordingStore : ICharacterStore
    {
        private readonly InMemoryCharacterStore _inner = new();
        private Func<CharacterState, CancellationToken, Task>? _onSave;
        public Func<CharacterState, CancellationToken, Task>? OnSave
        {
            get => Volatile.Read(ref _onSave);
            set => Volatile.Write(ref _onSave, value);
        }

        public ConcurrentQueue<CharacterState> Attempts { get; } = new();
        public ConcurrentQueue<CharacterState> Saved { get; } = new();
        public async Task SaveStateAsync(CharacterState state, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts.Enqueue(state);
            if (OnSave is { } handler)
            {
                await handler(state, cancellationToken);
            }

            Saved.Enqueue(state);
        }

        public Task<IReadOnlyList<CharacterRecord>> GetByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.GetByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord?> GetByIdAsync(int id, CancellationToken cancellationToken = default) => _inner.GetByIdAsync(id, cancellationToken);
        public Task<bool> IsNameTakenAsync(string name, CancellationToken cancellationToken = default) => _inner.IsNameTakenAsync(name, cancellationToken);
        public Task<int> CountByAccountAsync(int accountId, CancellationToken cancellationToken = default) => _inner.CountByAccountAsync(accountId, cancellationToken);
        public Task<CharacterRecord> CreateAsync(CharacterRecord character, CancellationToken cancellationToken = default) => _inner.CreateAsync(character, cancellationToken);
        public Task<bool> DeleteAsync(int id, int accountId, CancellationToken cancellationToken = default) => _inner.DeleteAsync(id, accountId, cancellationToken);
        public Task<IReadOnlyList<ActionButton>> GetActionButtonsAsync(int characterId, CancellationToken cancellationToken = default) => _inner.GetActionButtonsAsync(characterId, cancellationToken);
        public Task<IReadOnlyList<CharacterIdentity>> GetAllIdentitiesAsync(CancellationToken cancellationToken = default) => _inner.GetAllIdentitiesAsync(cancellationToken);
    }
}
