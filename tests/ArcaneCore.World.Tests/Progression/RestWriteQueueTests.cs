using ArcaneCore.Data.Characters.Life;
using ArcaneCore.World.Progression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Progression;

/// <summary>The retained-write rules of <see cref="RestWriteQueue"/>: nothing is dropped, only the latest state is written, and shutdown says what is not durable.</summary>
public sealed class RestWriteQueueTests
{
    private static (RestWriteQueue Queue, InMemoryRestStore Store) Create()
    {
        var store = new InMemoryRestStore();
        ServiceProvider services = new ServiceCollection().AddSingleton<ICharacterRestStore>(store).BuildServiceProvider();
        var queue = new RestWriteQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance) { RetryDelay = TimeSpan.Zero };
        queue.Start();
        return (queue, store);
    }

    private static CharacterRestState State(float pool) => new(pool, 1_700_000_000, true);

    [Fact]
    public async Task OnlyTheLatestState_OfACharacterIsWritten_InOrder()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        for (int i = 1; i <= 50; i++)
        {
            queue.Save(1, State(i));
        }

        queue.Save(2, State(7));
        await queue.FlushAsync();

        Assert.Equal(50f, store.Get(1)!.Value.RestBonus);
        Assert.Equal(7f, store.Get(2)!.Value.RestBonus);
        Assert.Equal(0, queue.Pending);
        await queue.StopAsync();
    }

    [Fact]
    public async Task AFailedWrite_IsRetained_NeverDropped_AndTheNextChangeCarriesIt()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        store.FailSaves = true;
        queue.Save(1, State(10));
        await queue.FlushAsync(); // the barrier never throws

        Assert.True(queue.HasRetainedFailure(1));
        Assert.Null(store.Get(1));

        store.FailSaves = false;
        queue.Save(1, State(11));
        await queue.FlushAsync();

        Assert.False(queue.HasRetainedFailure(1));
        Assert.Equal(11f, store.Get(1)!.Value.RestBonus);
        await queue.StopAsync();
    }

    [Fact]
    public async Task TheLoginBarrier_RetriesOnce_AndFaultsWhileTheCharacterIsStillNotDurable()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        store.FailSaves = true;
        queue.Save(1, State(10));
        await queue.FlushAsync();

        InvalidOperationException fault = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(1));
        Assert.Contains("character 1", fault.Message, StringComparison.Ordinal);
        await queue.FlushCharacterAsync(2); // a character with nothing retained passes

        store.FailSaves = false;
        await queue.FlushCharacterAsync(1); // the retry succeeds and the barrier opens
        Assert.Equal(10f, store.Get(1)!.Value.RestBonus);
        Assert.False(queue.HasRetainedFailure(1));
        await queue.StopAsync();
    }

    [Fact]
    public async Task ARequestedRetry_WritesARetainedState_WithoutANewChange()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        store.FailSaves = true;
        queue.Save(1, State(10));
        await queue.FlushAsync();
        store.FailSaves = false;

        queue.RequestRetry(1);
        await queue.FlushAsync();

        Assert.Equal(10f, store.Get(1)!.Value.RestBonus);
        await queue.StopAsync();
    }

    [Fact]
    public async Task Stop_RetriesEveryRetainedCharacterOnce_AndThrowsNamingThoseStillNotDurable()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        store.FailSaves = true;
        queue.Save(3, State(1));
        queue.Save(1, State(2));
        await queue.FlushAsync();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(queue.StopAsync);

        Assert.Contains("1, 3", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.InnerException);
        await Assert.ThrowsAsync<InvalidOperationException>(queue.StopAsync); // idempotent: the same outcome
    }

    [Fact]
    public async Task Stop_SucceedsWhenTheStoreHasRecovered()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        store.FailSaves = true;
        queue.Save(1, State(5));
        await queue.FlushAsync();
        store.FailSaves = false;

        await queue.StopAsync();

        Assert.Equal(5f, store.Get(1)!.Value.RestBonus);
    }

    [Fact]
    public async Task AStateThatArrivesAfterStop_IsNotLostSilently()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        await queue.StopAsync();

        queue.Save(1, State(5));

        Assert.True(queue.HasRetainedFailure(1)); // reported by the next barrier, never written
        Assert.Null(store.Get(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(1));
    }

    [Fact]
    public async Task Forget_DropsWhatWasRetained_ForADeletedCharacter()
    {
        (RestWriteQueue queue, InMemoryRestStore store) = Create();
        store.FailSaves = true;
        queue.Save(1, State(5));
        await queue.FlushAsync();

        queue.Forget(1);
        store.FailSaves = false;

        Assert.False(queue.HasRetainedFailure(1));
        await queue.FlushCharacterAsync(1);
        await queue.StopAsync();
        Assert.Null(store.Get(1));
    }
}
