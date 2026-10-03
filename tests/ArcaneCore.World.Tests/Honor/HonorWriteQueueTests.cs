using ArcaneCore.Kernel.Honor;
using ArcaneCore.World.Honor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ArcaneCore.World.Tests.Honor;

/// <summary>
/// The honor write queue (<see cref="HonorWriteQueue"/>): ordered, coalesced, retained across failures until durable. Every wait is on
/// a condition with a generous deadline; nothing depends on how long a write takes.
/// </summary>
public sealed class HonorWriteQueueTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);

    private static (HonorWriteQueue Queue, MemoryHonorStore Store, ServiceProvider Provider) Create(bool start = true)
    {
        var store = new MemoryHonorStore();
        ServiceProvider provider = new ServiceCollection().AddSingleton<IHonorStore>(store).BuildServiceProvider();
        var queue = new HonorWriteQueue(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        if (start)
        {
            queue.Start();
        }

        return (queue, store, provider);
    }

    private static HonorCpRecord Row(uint victim, float cp = 10f) => new(4, victim, cp, 100, (byte)HonorKind.Honorable);

    [Fact]
    public async Task Rows_and_state_are_written_in_one_ordered_batch()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create();
        await using ServiceProvider _ = provider;
        queue.AppendCp(1, Row(1));
        queue.AppendCp(1, Row(2));
        queue.SaveState(1, CharacterHonorState.Empty with { RankPoints = 12.34f });
        await queue.FlushAsync().WaitAsync(Budget);

        Assert.Equal([1u, 2u], store.Rows(1).Select(r => r.VictimId));
        Assert.Equal(12.3f, store.State(1).RankPoints);
        Assert.Equal(0, queue.Pending);
        Assert.Empty(queue.RetainedCharacters);
        await queue.StopAsync();
    }

    [Fact]
    public async Task A_reset_runs_before_the_state_and_rows_queued_after_it()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create(start: false);
        await using ServiceProvider _ = provider;
        store.Seed(1, CharacterHonorState.Empty with { RankPoints = 9000f }, Row(7), Row(8));
        queue.AppendCp(1, Row(1));            // discarded by the reset below
        queue.Reset(1);
        queue.AppendCp(1, Row(2));
        queue.SaveState(1, CharacterHonorState.Empty with { RankPoints = 5f });
        queue.Start();
        await queue.FlushAsync().WaitAsync(Budget);

        Assert.Equal([2u], store.Rows(1).Select(r => r.VictimId));
        Assert.Equal(5f, store.State(1).RankPoints);
        Assert.Equal(["reset:1", "state:1", "cp:1:1"], store.Journal);
        await queue.StopAsync();
    }

    [Fact]
    public async Task A_failed_write_is_retained_and_the_login_barrier_refuses_until_storage_recovers()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create();
        await using ServiceProvider _ = provider;
        store.FailWrites = true;
        queue.AppendCp(1, Row(1));
        await queue.FlushAsync().WaitAsync(Budget);
        Assert.True(queue.HasRetainedFailure(1));
        Assert.Equal([1], queue.RetainedCharacters);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.FlushCharacterAsync(1).WaitAsync(Budget));
        Assert.Contains("1", refused.Message);
        await queue.FlushCharacterAsync(2).WaitAsync(Budget); // another character is unaffected
        Assert.Empty(store.Rows(1));

        store.FailWrites = false;
        await queue.FlushCharacterAsync(1).WaitAsync(Budget);
        Assert.False(queue.HasRetainedFailure(1));
        Assert.Single(store.Rows(1));
        await queue.StopAsync();
    }

    [Fact]
    public async Task A_retry_after_the_outage_writes_every_retained_row_once()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create();
        await using ServiceProvider _ = provider;
        store.FailWrites = true;
        queue.AppendCp(1, Row(1));
        queue.AppendCp(1, Row(2));
        queue.AppendCp(1, Row(3));
        await queue.FlushAsync().WaitAsync(Budget);
        store.FailWrites = false;

        queue.RequestRetry(1);
        queue.RequestRetry(2); // nothing retained: a no-op
        await queue.FlushAsync().WaitAsync(Budget);

        Assert.Equal([1u, 2u, 3u], store.Rows(1).Select(r => r.VictimId));
        Assert.False(queue.HasRetainedFailure(1));
        await queue.StopAsync();
    }

    [Fact]
    public async Task Deleting_a_character_discards_retained_rows_and_removes_stored_ones()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create();
        await using ServiceProvider _ = provider;
        store.Seed(1, CharacterHonorState.Empty with { RankPoints = 100f }, Row(99));
        store.FailWrites = true;
        queue.AppendCp(1, Row(1));
        await queue.FlushAsync().WaitAsync(Budget);
        queue.DeleteCharacter(1);                      // discards the retained row and queues the removal
        await queue.FlushAsync().WaitAsync(Budget);
        Assert.True(queue.HasRetainedFailure(1));      // the removal itself is retained while storage is down

        store.FailWrites = false;
        await queue.FlushCharacterAsync(1).WaitAsync(Budget);
        Assert.Empty(store.Rows(1));
        Assert.Equal(CharacterHonorState.Empty, store.State(1));
        Assert.False(queue.HasRetainedFailure(1));
        await queue.StopAsync();
    }

    [Fact]
    public async Task Forgetting_a_character_drops_whatever_was_retained_for_a_reused_id()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create();
        await using ServiceProvider _ = provider;
        store.FailWrites = true;
        queue.AppendCp(1, Row(1));
        await queue.FlushAsync().WaitAsync(Budget);
        queue.ForgetCharacter(1);
        store.FailWrites = false;
        await queue.FlushCharacterAsync(1).WaitAsync(Budget);
        Assert.Empty(store.Rows(1));
        await queue.StopAsync();
    }

    [Fact]
    public async Task Stopping_with_storage_still_down_throws_naming_the_characters_and_a_later_change_is_refused()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create();
        await using ServiceProvider _ = provider;
        store.FailWrites = true;
        queue.AppendCp(3, Row(1));
        await queue.FlushAsync().WaitAsync(Budget);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget));
        Assert.Contains("3", failure.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StopAsync().WaitAsync(Budget)); // idempotent: same outcome

        queue.AppendCp(4, Row(2));
        Assert.True(queue.HasRetainedFailure(4)); // after stop a change cannot be persisted and says so
    }

    [Fact]
    public async Task Stopping_a_healthy_queue_drains_it_and_a_host_without_a_store_keeps_nothing()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create();
        await using ServiceProvider _ = provider;
        queue.AppendCp(1, Row(1));
        await queue.StopAsync().WaitAsync(Budget);
        Assert.Single(store.Rows(1));

        await using ServiceProvider bare = new ServiceCollection().BuildServiceProvider();
        var withoutStore = new HonorWriteQueue(bare.GetRequiredService<IServiceScopeFactory>(), NullLogger.Instance);
        withoutStore.Start();
        withoutStore.AppendCp(1, Row(1));
        await withoutStore.FlushAsync().WaitAsync(Budget);
        Assert.False(withoutStore.HasRetainedFailure(1));
        await withoutStore.StopAsync();
    }

    [Fact]
    public async Task Many_changes_while_a_write_is_pending_coalesce_into_one_queued_write()
    {
        (HonorWriteQueue queue, MemoryHonorStore store, ServiceProvider provider) = Create(start: false);
        await using ServiceProvider _ = provider;
        for (uint i = 0; i < 50; i++)
        {
            queue.AppendCp(1, Row(i));
        }

        Assert.Equal(1, queue.Pending);
        queue.Start();
        await queue.FlushAsync().WaitAsync(Budget);
        Assert.Equal(50, store.Rows(1).Count);
        Assert.Equal(["cp:1:50"], store.Journal);
        await queue.StopAsync();
    }
}
