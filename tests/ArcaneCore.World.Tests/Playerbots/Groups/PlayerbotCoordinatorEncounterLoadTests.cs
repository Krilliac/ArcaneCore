using ArcaneCore.Data.World.Instances;
using ArcaneCore.Kernel.WorldData;
using ArcaneCore.World.Bots;
using ArcaneCore.World.Playerbots.Groups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Playerbots.Groups;

/// <summary>
/// The coordinator's <c>instance_encounter</c> read: its quiesce stays pending while the read is open, a first read never overwrites the
/// rows a <c>.reload instance_encounter</c> put in force, and the world's stop cancels the read and waits for it.
/// </summary>
public sealed class PlayerbotCoordinatorEncounterLoadTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private static IReadOnlyList<InstanceEncounter> Rows(string name) => [new InstanceEncounter(36, 0, 167, 0, 1234, true, name)];

    private sealed class GatedStore(Task gate, IReadOnlyList<InstanceEncounter> rows) : IInstanceEncounterStore
    {
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await gate.ConfigureAwait(false);
            return rows;
        }
    }

    /// <summary>A read that ends only when its token fires.</summary>
    private sealed class TokenOnlyStore : IInstanceEncounterStore
    {
        public readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _ended;
        public bool Ended => Volatile.Read(ref _ended) == 1;

        public async Task<IReadOnlyList<InstanceEncounter>> LoadAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                Entered.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                return [];
            }
            finally
            {
                Volatile.Write(ref _ended, 1);
            }
        }
    }

    private static PlayerbotGroupCoordinator Coordinator(IInstanceEncounterStore store, InstanceEncounterCatalog catalog)
    {
        IServiceProvider provider = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(catalog)
            .BuildServiceProvider();
        return new PlayerbotGroupCoordinator(provider, NullLogger<PlayerbotGroupCoordinator>.Instance);
    }

    [Fact]
    public async Task QuiesceAsync_StaysPendingWhileTheReadIsOpen_UntilTheReadEndsOrTheTokenFires()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null, true, null);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new GatedStore(gate.Task, Rows("Quiesce Boss"));
        PlayerbotGroupCoordinator coordinator = Coordinator(store, new InstanceEncounterCatalog());
        coordinator.Attach(world.World.Host.World);
        await store.Entered.Task.WaitAsync(Wait);

        using var cancel = new CancellationTokenSource();
        Task quiescing = coordinator.QuiesceAsync(cancel.Token);
        Assert.False(quiescing.IsCompleted); // the read is held on the gate
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => quiescing.WaitAsync(Wait));

        Task again = coordinator.QuiesceAsync(CancellationToken.None);
        Assert.False(again.IsCompleted);
        gate.SetResult(true);
        await again.WaitAsync(Wait);
        Assert.Single(coordinator.Encounters);
    }

    [Fact]
    public async Task AFirstRead_NeverOverwritesRowsAReloadPutInForce_InEitherOrder()
    {
        for (int round = 0; round < 200; round++)
        {
            var catalog = new InstanceEncounterCatalog();
            IReadOnlyList<InstanceEncounter> reloaded = Rows("Reloaded");
            IReadOnlyList<InstanceEncounter> first = Rows("First read");
            using var start = new ManualResetEventSlim(false);
            Task reload = Task.Run(() => { start.Wait(); catalog.Replace(reloaded); });
            Task read = Task.Run(() => { start.Wait(); catalog.ReplaceIfNotLoaded(first); });
            start.Set();
            await Task.WhenAll(reload, read).WaitAsync(Wait);

            Assert.Same(reloaded, catalog.Current); // the reload wins whether the read ran before it or after it
            Assert.True(catalog.IsLoaded);
        }

        var single = new InstanceEncounterCatalog();
        Assert.True(single.ReplaceIfNotLoaded(Rows("a")));
        Assert.False(single.ReplaceIfNotLoaded(Rows("b")));
        Assert.Equal("a", Assert.Single(single.Current).Name);
    }

    [Fact]
    public async Task StopAsync_CancelsAndWaitsForTheRead_SoItHasEndedWhenTheStopReturns()
    {
        await using GroupTestWorld world = await GroupTestWorld.StartAsync([], null, true, null);
        var store = new TokenOnlyStore();
        var catalog = new InstanceEncounterCatalog();
        PlayerbotGroupCoordinator coordinator = Coordinator(store, catalog);
        coordinator.Attach(world.World.Host.World);
        await store.Entered.Task.WaitAsync(Wait);
        Assert.False(store.Ended);

        await coordinator.StopAsync().WaitAsync(Wait);

        Assert.True(store.Ended);
        Assert.True(coordinator.EncounterLoading.IsCompleted);
        Assert.False(catalog.IsLoaded); // a cut-short read puts nothing in force
    }
}
