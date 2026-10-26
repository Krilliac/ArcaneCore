using ArcaneCore.Data.Characters.Life;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.World.Features;
using ArcaneCore.World.Persistence;
using ArcaneCore.World.Progression;
using ArcaneCore.World.WorldState;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ArcaneCore.World.Tests.Persistence;

/// <summary>The feature write queues share one stop-drain lane: once one of them has stalled it, the others make no attempt, so the stall does not add up across queues.</summary>
public sealed class WorldFeaturesStopDrainTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private sealed class SlowLockedStore : ICharacterRestStore, IExploredZonesStore
    {
        private int _attempts;

        public volatile bool Fail;
        public volatile bool Locked;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<CharacterRestState?> LoadAsync(int characterId, CancellationToken cancellationToken = default) => Task.FromResult<CharacterRestState?>(null);

        public Task SaveAsync(int characterId, CharacterRestState state, CancellationToken cancellationToken = default) => Step();

        public Task DeleteAsync(int characterId, CancellationToken cancellationToken = default) => Step();

        Task<uint[]?> IExploredZonesStore.LoadAsync(int characterId, CancellationToken cancellationToken) => Task.FromResult<uint[]?>(null);

        public Task SaveAsync(int characterId, IReadOnlyList<uint> words, CancellationToken cancellationToken = default) => Step();

        private async Task Step()
        {
            Interlocked.Increment(ref _attempts);
            if (Locked)
            {
                await Task.Delay(400).ConfigureAwait(false);
                throw new SqliteException("SQLite Error 5: 'database is locked'.", 5);
            }

            if (Fail)
            {
                throw new IOException("store down");
            }
        }
    }

    [Fact]
    public async Task StopWorldFeatures_SharesOneLaneAcrossFeatureQueues()
    {
        var drain = new ShutdownDrain { Features = new StopDrainBudget { StallBudget = TimeSpan.FromMilliseconds(300) } };
        var store = new SlowLockedStore();
        await using ServiceProvider services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(drain)
            .AddSingleton<ICharacterRestStore>(store)
            .AddSingleton<IExploredZonesStore>(store)
            .AddSingleton<RestFeature>()
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<RestFeature>())
            .AddSingleton<ExploredZonesPersistence>()
            .AddSingleton<IWorldFeature>(sp => sp.GetRequiredService<ExploredZonesPersistence>())
            .BuildServiceProvider();
        RestFeature rest = services.GetRequiredService<RestFeature>();
        ExploredZonesPersistence explored = services.GetRequiredService<ExploredZonesPersistence>();
        rest.Writes.Start(); // the features start their queues when they attach to a world; these are driven without one
        explored.Writes.Start();
        store.Fail = true;
        rest.Writes.Save(1, new CharacterRestState(50f, 1000, true));
        explored.Writes.Save(1, new uint[ExploredZonesText.WordCount]);
        await rest.Writes.FlushAsync().WaitAsync(Wait);
        await explored.Writes.FlushAsync().WaitAsync(Wait);
        Assert.True(rest.Writes.HasRetainedFailure(1));
        Assert.True(explored.Writes.HasRetainedFailure(1));
        int before = store.Attempts;
        store.Fail = false;
        store.Locked = true;

        await Assert.ThrowsAsync<AggregateException>(services.StopWorldFeaturesAsync);

        Assert.True(drain.Features.Refused > 0, drain.Features.Describe());
        Assert.Equal(1, store.Attempts - before); // the first queue's one attempt stalled the shared lane; the second queue made none
        Assert.False(drain.Saves.Started); // the save queue's lane is begun by the host, not by the feature queues
    }
}
