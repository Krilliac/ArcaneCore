using ArcaneCore.Kernel.WorldData.WorldState;
using ArcaneCore.World.WorldState;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.WorldState;

/// <summary>
/// The four saved-variable writers that blocked the world thread on the characters database (Dragons of Nightmare every 20 s while
/// they count down, Fishing Extravaganza on Riggle's AI, elemental invasion on every invader kill, Silithyst on a turn-in) now go
/// through <see cref="StateSaveQueue{TKey,TState}"/>: the world thread records the value and returns, one drain writes it.
/// </summary>
public sealed class StateSaveQueueTests
{
    [Fact]
    public async Task SaveNeverWaitsForAStuckStoreAndTheNewestValuePerKeyIsWrittenInKeyOrder()
    {
        using var gate = new ManualResetEventSlim(false);
        using var entered = new ManualResetEventSlim(false);
        var written = new List<(int Key, int Value)>();
        var queue = new StateSaveQueue<int, (int Key, int Value)>(state =>
        {
            entered.Set();
            gate.Wait(); // a held SQLite write lock
            lock (written) written.Add(state);
            return Task.CompletedTask;
        }, NullLogger.Instance, "test");

        queue.Save(1, (1, 10)); // taken by the drain, then stuck
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        // The store stays stuck until gate.Set() below, so a Save that waited for it would never return: completing at all is the
        // proof, and the generous bound only turns a hang into a failure (no tight wall-clock bound that load could trip).
        Task saves = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                queue.Save(2, (2, i));
                queue.Save(1, (1, 100 + i));
            }
        });
        try
        {
            await saves.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            gate.Set(); // release the stuck store so the abandoned task cannot leak
            Assert.Fail("Save waited for the stuck store");
        }
        gate.Set();
        await queue.FlushAsync();
        lock (written)
        {
            // The first value, then the newest of each key in the order the keys were queued behind it.
            Assert.Equal((1, 10), written[0]);
            Assert.Equal([(2, 99), (1, 199)], written.Skip(1));
        }

        Assert.Equal(3, queue.Written);
    }

    [Fact]
    public async Task AFailedWriteIsLoggedAndTheNextValueIsStillWritten()
    {
        int calls = 0;
        var saved = new List<int>();
        var queue = new StateSaveQueue<int, int>(value =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("database is locked");
            saved.Add(value);
            return Task.CompletedTask;
        }, NullLogger.Instance, "test");
        queue.Save(0, 1);
        await queue.FlushAsync();
        queue.Save(0, 2);
        await queue.FlushAsync();
        Assert.Equal([2], saved);
    }

    private sealed class BlockingFishingStore : IFishingExtravaganzaStore
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public FishingExtravaganzaState? Saved;
        public int Saves;
        public Task<FishingExtravaganzaState?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
        public Task SaveAsync(FishingExtravaganzaState state, CancellationToken cancellationToken = default)
        {
            Gate.Wait();
            Saved = state;
            Interlocked.Increment(ref Saves);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RiggleResetDoesNotWaitForTheDatabaseAndStopAsyncWritesIt()
    {
        var store = new BlockingFishingStore();
        ServiceProvider provider = new ServiceCollection().AddSingleton<IFishingExtravaganzaStore>(store).BuildServiceProvider();
        var feature = new FishingExtravaganzaFeature(provider.GetRequiredService<IServiceScopeFactory>(),
            new GameEventFeature(provider, NullLogger<GameEventFeature>.Instance));
        feature.PreviousWinTime = feature.NowUnix() - (2 * 86400);

        // The store is gated until store.Gate.Set() below, so a reset that waited for it would never return; the 30 s bound only turns a
        // hang into a failure.
        Task reset = Task.Run(feature.ResetIfStale); // npc_riggle_bassbaitAI's constructor, on the world thread
        try
        {
            await reset.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            store.Gate.Set();
            Assert.Fail("the reset waited for the store");
        }
        Assert.True(feature.AnnounceBegin); // memory is the state at once
        Assert.Null(store.Saved);

        store.Gate.Set();
        await feature.StopAsync(); // shutdown drains what is queued
        Assert.Equal(feature.State, store.Saved);
        Assert.Equal(1, store.Saves);
    }
}
