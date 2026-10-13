using System.Collections.Concurrent;
using ArcaneCore.Game.Maps;
using ArcaneCore.Kernel.Characters;
using ArcaneCore.Kernel.Items;
using ArcaneCore.Kernel.Configuration;
using ArcaneCore.World.Ops.Announce;
using ArcaneCore.World.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        // The earlier snapshot is written (and fails all three attempts) before the newer one exists: snapshots that wait
        // together coalesce, so the retained-failure merge is what carries its optional sections forward.
        kit.Queue.Enqueue(earlier);
        await Assert.ThrowsAsync<IOException>(() => kit.Queue.FlushCharacterAsync(1));
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
    public async Task FailedBarrierCompletesAfterItsInFlightSlotIsCleared()
    {
        await using var kit = new Kit();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingAtCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Queue.BeforeWaitersCompletedForTesting = () => pendingAtCompletion.TrySetResult(kit.Queue.Pending);
        kit.Store.OnSave = async (_, token) =>
        {
            if (kit.Store.Attempts.Count == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }

            throw new IOException("still unavailable");
        };

        kit.Queue.Enqueue(State(1, 33));
        Task barrier = kit.Queue.FlushCharacterAsync(1);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(barrier.IsCompleted);
        release.TrySetResult();
        await Assert.ThrowsAsync<IOException>(() => barrier.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, await pendingAtCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(kit.Store.Saved);

        kit.Store.OnSave = null;
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(33u, Assert.Single(kit.Store.Saved).Money);
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
        // The waiting 12 was superseded by the settlement's 33 (a newer snapshot replaces a waiting one); 11 was already being written.
        Assert.Equal(new uint[] { 11, 33 }, kit.Store.Saved.Select(s => s.Money));
        Assert.True(kit.Queue.IsHeld(1));
        kit.Queue.ResumeCharacter(1);
        kit.Queue.Enqueue(State(1, 55));
        await kit.Queue.FlushCharacterAsync(1);
        Assert.Equal(new uint[] { 11, 33, 55 }, kit.Store.Saved.Select(s => s.Money));
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
        await Assert.ThrowsAsync<IOException>(() => kit.Queue.FlushCharacterAsync(1)); // fails every attempt: retained
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

    [Fact]
    public async Task PendingSnapshotsOfOneCharacterCoalesceLatestWins()
    {
        await using var kit = new Kit();
        var (entered, release) = BlockOn(kit, id: 99);
        kit.Queue.Enqueue(State(99, 1));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        kit.Queue.Enqueue(State(1, 10));
        kit.Queue.Enqueue(State(1, 20));
        kit.Queue.Enqueue(State(1, 30));
        Assert.True(kit.Queue.IsPending(1));
        Assert.Equal(2, kit.Queue.Pending); // characters, not snapshots: 99 in flight, 1 waiting
        release.TrySetResult();
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(30u, Assert.Single(kit.Store.Saved, s => s.Id == 1).Money);
        Assert.Single(kit.Store.Attempts, s => s.Id == 1);
        Assert.False(kit.Queue.IsPending(1));
        Assert.Equal(0, kit.Queue.Pending);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task InFlightSnapshotIsNotReplacedAndTheNewerOneFollowsIt()
    {
        await using var kit = new Kit();
        var (entered, release) = BlockOn(kit, id: 1, money: 11);
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(kit.Queue.IsPending(1)); // being written is not waiting
        kit.Queue.Enqueue(State(1, 22));
        kit.Queue.Enqueue(State(1, 33));
        Assert.True(kit.Queue.IsPending(1));
        Assert.Equal(1, kit.Queue.Pending);
        release.TrySetResult();
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new uint[] { 11, 33 }, kit.Store.Saved.Select(s => s.Money));
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task CoalescedSnapshotKeepsOptionalSectionsOfTheSupersededOne()
    {
        await using var kit = new Kit();
        var (entered, release) = BlockOn(kit, id: 99);
        kit.Queue.Enqueue(State(99, 1));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CharacterState older = State(1, 11) with
        {
            ActionButtons = [new ActionButton(1, 123, 0)],
            Home = new HomeBind(1, 2, 3, 4, 5),
            Inventory = new InventorySnapshot([new InventoryItemData(0, 23, new ItemInstanceData { Guid = 7, Entry = 117, Count = 4 })]),
        };
        kit.Queue.Enqueue(older);
        kit.Queue.Enqueue(State(1, 22) with { Home = new HomeBind(9, 8, 7, 6, 5) });
        release.TrySetResult();
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        CharacterState saved = Assert.Single(kit.Store.Saved, s => s.Id == 1);
        Assert.Equal(22u, saved.Money);
        Assert.Equal(older.ActionButtons, saved.ActionButtons);
        Assert.Equal(new HomeBind(9, 8, 7, 6, 5), saved.Home); // the newer section wins where it has one
        Assert.Equal(4u, Assert.Single(saved.Inventory!.Items).Item.Count);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task InterleavedEnqueueAndDequeueNeverPersistsAnOlderSnapshotAfterANewerOne()
    {
        const int characters = 50;
        const int snapshots = 20_000;
        await using var kit = new Kit();
        var random = new Random(7);
        var delays = new int[256];
        for (int i = 0; i < delays.Length; i++)
        {
            delays[i] = random.Next(0, 2001); // microseconds
        }

        int call = 0;
        kit.Store.OnSave = async (_, _) =>
        {
            await Task.Yield();
            long until = System.Diagnostics.Stopwatch.GetTimestamp()
                + (delays[Interlocked.Increment(ref call) & 255] * System.Diagnostics.Stopwatch.Frequency / 1_000_000);
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until)
            {
                Thread.SpinWait(50);
            }
        };
        kit.Queue.Start();
        var last = new uint[characters + 1];
        var producer = Task.Run(() =>
        {
            for (int n = 1; n <= snapshots; n++)
            {
                int id = 1 + (n % characters);
                last[id] = (uint)n;
                kit.Queue.Enqueue(State(id, (uint)n));
                if (n % 64 == 0)
                {
                    Thread.Yield();
                }
            }
        });
        await producer.WaitAsync(TimeSpan.FromSeconds(60));
        await kit.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(60));
        foreach (IGrouping<int, CharacterState> group in kit.Store.Saved.GroupBy(s => s.Id))
        {
            uint[] money = [.. group.Select(s => s.Money)];
            for (int i = 1; i < money.Length; i++)
            {
                Assert.True(money[i] > money[i - 1], $"character {group.Key}: {money[i - 1]} was persisted before the newer {money[i]} was not newer");
            }

            Assert.Equal(last[group.Key], money[^1]);
        }

        Assert.Equal(characters, kit.Store.Saved.Select(s => s.Id).Distinct().Count());
        Assert.True(kit.Store.Saved.Count < snapshots, "nothing coalesced: the test did not exercise supersession");
        Assert.Equal(0, kit.Queue.Pending);
    }

    [Fact]
    public async Task FailureBetweenWritesRetainsTheNewestSnapshotAndStorageNeverRegresses()
    {
        var durable = new ConcurrentDictionary<int, uint>();
        var history = new ConcurrentQueue<uint>();
        bool down = false;
        Func<CharacterState, CancellationToken, Task> store = (state, _) =>
        {
            if (Volatile.Read(ref down))
            {
                return Task.FromException(new IOException("injected outage"));
            }

            bool known = durable.TryGetValue(state.Id, out uint was);
            Assert.True(!known || state.Money > was, $"durable {was} regressed to {state.Money}");
            durable[state.Id] = state.Money;
            history.Enqueue(state.Money);
            return Task.CompletedTask;
        };

        await using var kit = new Kit();
        kit.Store.OnSave = store;
        kit.Queue.Enqueue(State(1, 1));
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1u, durable[1]);

        // Storage is down for the write of 2 (every attempt, whether or not a barrier is attached) and then recovers: the failed
        // snapshot is retained and the newer 3 is merged over it, so storage moves 1 -> 3 and never back.
        Volatile.Write(ref down, true);
        kit.Queue.Enqueue(State(1, 2));
        await Assert.ThrowsAsync<IOException>(() => kit.Queue.FlushCharacterAsync(1));
        Volatile.Write(ref down, false);
        kit.Queue.Enqueue(State(1, 3));
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3u, durable[1]);

        // A persistent outage with many newer snapshots pending: shutdown reports it; storage still holds 3.
        Volatile.Write(ref down, true);
        for (uint n = 4; n <= 40; n++)
        {
            kit.Queue.Enqueue(State(1, n));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Queue.StopAsync());
        Assert.Equal(3u, durable[1]);

        // The outage ends. A second queue over the same backing store writes the newest snapshot; storage only ever moved forward.
        Volatile.Write(ref down, false);
        await using var second = new Kit();
        second.Store.OnSave = store;
        second.Queue.Enqueue(State(1, 40));
        await second.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(40u, durable[1]);
        Assert.Equal(new uint[] { 1, 3, 40 }, history.ToArray());
        await second.Queue.StopAsync();
    }

    [Fact]
    public async Task SettlementSaveOvertakesADeepQueueOfOtherCharacters()
    {
        await using var kit = new Kit();
        kit.Store.OnSave = async (state, _) =>
        {
            if (state.Id != 1000)
            {
                await Task.Delay(20);
            }
        };
        for (int id = 1; id <= 300; id++)
        {
            kit.Queue.Enqueue(State(id, 5));
        }

        kit.Queue.Start();
        kit.Queue.HoldCharacter(1000);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await kit.Queue.SaveForSettlementAsync(State(1000, 77)).WaitAsync(TimeSpan.FromSeconds(10));
        TimeSpan waited = clock.Elapsed;
        int stillQueued = kit.Queue.Pending;
        Assert.True(waited < TimeSpan.FromMilliseconds(500), $"the settlement waited {waited.TotalMilliseconds:F0} ms behind other characters");
        Assert.True(stillQueued > 150, $"only {stillQueued} other snapshots were still queued: the settlement did not overtake them");
        Assert.Equal(77u, Assert.Single(kit.Store.Saved, s => s.Id == 1000).Money);
        await kit.Queue.StopAsync();
        Assert.Equal(301, kit.Store.Saved.Count);
    }

    [Fact]
    public async Task CancelledSettlementWaitLeavesItsSnapshotQueuedNotFailed()
    {
        var logger = new CaptureLogger();
        await using var kit = new Kit(logger: logger);
        var (entered, release) = BlockOn(kit, id: 99);
        kit.Queue.Enqueue(State(99, 1));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        kit.Queue.HoldCharacter(1);
        using var budget = new CancellationTokenSource();
        Task settlement = kit.Queue.SaveForSettlementAsync(State(1, 42), budget.Token);
        Assert.False(settlement.IsCompleted);
        await budget.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settlement.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(settlement.IsCanceled);
        Assert.True(kit.Queue.IsPending(1)); // still queued, as an ordinary snapshot
        release.TrySetResult();
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(42u, Assert.Single(kit.Store.Saved, s => s.Id == 1).Money);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Equal(0, kit.Queue.Pending);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task PriorityLaneCannotStarveOrdinarySnapshots()
    {
        await using var kit = new Kit();
        var (entered, release) = BlockOn(kit, id: 99);
        kit.Queue.Enqueue(State(99, 1));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        kit.Queue.Enqueue(State(1, 10));
        var settlements = new List<Task>();
        for (int id = 100; id < 130; id++)
        {
            kit.Queue.HoldCharacter(id);
            settlements.Add(kit.Queue.SaveForSettlementAsync(State(id, 7)));
        }

        release.TrySetResult();
        await Task.WhenAll(settlements).WaitAsync(TimeSpan.FromSeconds(10));
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
        int[] order = [.. kit.Store.Saved.Select(s => s.Id).Where(id => id != 99)];
        int position = Array.IndexOf(order, 1);
        Assert.True(position is >= 0 and <= 7, $"the ordinary snapshot was written at position {position} of {order.Length}: the priority lane starved it");
        Assert.Equal(31, order.Length);
        await kit.Queue.StopAsync();
    }

    [Fact]
    public async Task ReportsDepthAndOldestPendingAgeAndWarnsPastThresholds()
    {
        var logger = new CaptureLogger();
        await using var kit = new Kit(new WorldRuntimeOptions { SaveQueueWarnDepth = 3, SaveQueueWarnAgeMs = 0 }, logger);
        var (entered, release) = BlockOn(kit, id: 99);
        kit.Queue.Enqueue(State(99, 1));
        kit.Queue.Start();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int id = 1; id <= 5; id++)
        {
            kit.Queue.Enqueue(State(id, 5));
        }

        Assert.Equal(6, kit.Queue.Pending);
        await Task.Delay(120);
        Assert.True(kit.Queue.OldestPendingAge >= TimeSpan.FromMilliseconds(100), $"oldest age {kit.Queue.OldestPendingAge}");
        await WaitUntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("character save backlog:", StringComparison.Ordinal)),
            () => kit.Queue.Enqueue(State(6, 5)));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("oldest waiting", StringComparison.Ordinal));
        release.TrySetResult();
        await WaitUntilAsync(() => logger.Entries.Any(e => e.Message.Contains("character save backlog cleared", StringComparison.Ordinal)), () => { });
        Assert.Equal(0, kit.Queue.Pending);
        Assert.Equal(TimeSpan.Zero, kit.Queue.OldestPendingAge);
        await kit.Queue.StopAsync();
    }

    // ---- a locked SQLite database (another writer holds characters.db) ----

    private static SqliteException Busy() => new("SQLite Error 5: 'database is locked'.", 5);

    private static async Task<bool> BecomesTrueAsync(Func<bool> condition, int seconds)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(10);
        }

        return true;
    }

    /// <summary>Character <paramref name="lockedId"/> fails with SQLite BUSY until <see cref="Locked.Release"/>; every other character saves.</summary>
    private sealed class Locked
    {
        private int _attempts;
        private int _unlocked;
        public int Attempts => Volatile.Read(ref _attempts);
        public void Release() => Volatile.Write(ref _unlocked, 1);
        public bool IsReleased => Volatile.Read(ref _unlocked) == 1;
        public int Next() => Interlocked.Increment(ref _attempts);
    }

    private sealed class CountingSink : IAnnounceSink
    {
        private readonly ConcurrentQueue<AnnounceDelivery> _sent = new();
        public int Count => _sent.Count;
        public AnnounceDelivery[] Sent => [.. _sent];
        public void Deliver(AnnounceDelivery delivery) => _sent.Enqueue(delivery);
    }

    private sealed class OutcomeLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task ABusyStorm_IsAnnouncedOnTheThirdConsecutiveLockedFailure_Once_AndTheRestAreDuplicatesInTheWindow()
    {
        var sink = new CountingSink();
        var announcerLog = new OutcomeLogger();
        long now = 1_000_000; // frozen: every failure is inside the 120 s duplicate window
        var announcer = new OpsAnnouncer(new AnnounceOptions(), sink, announcerLog, () => Volatile.Read(ref now));
        await using var kit = new Kit(announcer: announcer);
        var @lock = new Locked();
        var sentBeforeAttempt = new ConcurrentDictionary<int, int>();
        kit.Store.OnSave = (state, _) =>
        {
            int attempt = @lock.Next();
            sentBeforeAttempt[attempt] = sink.Count; // what the staff had been told before this attempt ran
            if (!@lock.IsReleased)
            {
                throw Busy();
            }

            return Task.CompletedTask;
        };
        try
        {
            kit.Queue.Enqueue(State(1, 11));
            kit.Queue.Start();
            for (int i = 0; i < 8 && @lock.Attempts < 6; i++)
            {
                _ = kit.Queue.FlushCharacterAsync(1); // a barrier retries the retained snapshot at once; the consumer does too after its pause
                Assert.True(await BecomesTrueAsync(() => @lock.Attempts > i, seconds: 20), $"attempt {i + 1} never ran");
            }

            Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 6, seconds: 20), $"only {@lock.Attempts} attempts ran");
            await Task.Delay(100); // the announcement of the last attempt is made after it throws

            // Streaks 1 and 2 announce nothing; streak 3 announces; streaks 4 and later are duplicates.
            Assert.Equal(0, sentBeforeAttempt[2]);
            Assert.Equal(0, sentBeforeAttempt[3]);
            Assert.Equal(1, sentBeforeAttempt[4]);
            AnnounceDelivery storm = Assert.Single(sink.Sent);
            Assert.Contains("database is busy", storm.StaffChat, StringComparison.Ordinal);
            Assert.Null(storm.WorldChat);

            // Each failure past the third was counted as a duplicate and logged below Warning (a lock storm lasts minutes).
            (LogLevel Level, string Message)[] entries = [.. announcerLog.Entries];
            int duplicates = entries.Count(e => e.Message.Contains("(duplicate)", StringComparison.Ordinal));
            Assert.True(duplicates >= 2, $"expected at least 2 duplicates, saw {duplicates}");
            Assert.All(entries.Where(e => e.Message.Contains("(duplicate)", StringComparison.Ordinal)), e => Assert.Equal(LogLevel.Debug, e.Level));
            Assert.Single(entries, e => e.Message.Contains("(sent)", StringComparison.Ordinal));
        }
        finally
        {
            @lock.Release();
        }
    }

    [Fact]
    public void AQuarantinedCharacter_IsAnnouncedOncePerQuarantine_NotOnEveryPeriodicEnqueue()
    {
        var sink = new CountingSink();
        long now = 1_000_000;
        var announcer = new OpsAnnouncer(new AnnounceOptions { Saves = { DedupSeconds = 0 } }, sink, NullLogger.Instance, () => Volatile.Read(ref now));
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var queue = new CharacterSaveQueue(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<CharacterSaveQueue>.Instance, announcer: announcer);
        queue.QuarantineCharacter(7);

        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(State(7, 1));
        }

        Assert.Equal(1, sink.Count); // the announcer's dedup window is off: the queue itself speaks once

        queue.ResumeCharacter(7);
        queue.QuarantineCharacter(7);
        queue.Enqueue(State(7, 1));
        Assert.Equal(2, sink.Count); // a new quarantine is news again
    }

    [Fact]
    public async Task DatabaseLockedOnOneSave_DoesNotHoldTheConsumer_AndTheSnapshotIsWrittenOnceTheLockIsGone()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (state, token) =>
        {
            if (state.Id != 1)
            {
                return;
            }

            int attempt = @lock.Next();
            if (attempt == 1)
            {
                firstAttempt.TrySetResult();
                await firstEnds.Task.WaitAsync(token); // the driver's lock wait, ended by the test
                throw Busy();
            }

            await laterWait.Task.WaitAsync(token); // a later attempt that finds the lock still held waits again (the incident's 30 s)
            if (!@lock.IsReleased)
            {
                throw Busy();
            }
        };
        try
        {
            kit.Queue.Enqueue(State(1, 11));
            kit.Queue.Enqueue(State(2, 22));
            kit.Queue.Start();
            await firstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstEnds.TrySetResult();

            // Before the fix the consumer retried character 1 inline and sat in that wait, so character 2 never got its turn.
            Assert.True(await BecomesTrueAsync(() => kit.Store.Saved.Any(s => s.Id == 2), seconds: 4),
                "character 2 was not saved while character 1's save was waiting on the database lock: the consumer is held by the retry");
            Assert.DoesNotContain(kit.Store.Saved, s => s.Id == 1);

            @lock.Release();
            laterWait.TrySetResult();
            Assert.True(await BecomesTrueAsync(() => kit.Store.Saved.Any(s => s.Id == 1), seconds: 10), "the retained snapshot of character 1 was never written");
            await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(11u, kit.Store.Saved.Single(s => s.Id == 1).Money);
            Assert.Equal(0, kit.Queue.Pending);
        }
        finally
        {
            @lock.Release();
            laterWait.TrySetResult();
            firstEnds.TrySetResult();
        }
    }

    [Fact]
    public async Task ABarrierWaitsForTheRetainedLockedSnapshot_AndCompletesWhenItIsWritten()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        kit.Store.OnSave = (state, _) =>
        {
            if (state.Id == 1)
            {
                @lock.Next();
                if (!@lock.IsReleased)
                {
                    throw Busy();
                }
            }

            return Task.CompletedTask;
        };
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Start();
        Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 1 && kit.Queue.Pending == 0, seconds: 5));

        Task barrier = kit.Queue.FlushCharacterAsync(1);
        Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 2 && kit.Queue.Pending == 0, seconds: 5), "the barrier did not retry the retained snapshot");
        Assert.False(barrier.IsCompleted, "the barrier completed while the snapshot was still unwritten");
        Assert.Empty(kit.Store.Saved);

        @lock.Release();
        await barrier.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new uint[] { 11 }, kit.Store.Saved.Select(s => s.Money));
        Assert.Equal(0, kit.Queue.Pending);
    }

    [Fact]
    public async Task ASettlementSaveWaitingOnTheLock_IsCancelledByItsToken_AndItsSnapshotStillGetsWritten()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        kit.Store.OnSave = (state, _) =>
        {
            if (state.Id == 1)
            {
                @lock.Next();
                if (!@lock.IsReleased)
                {
                    throw Busy();
                }
            }

            return Task.CompletedTask;
        };
        using var cancel = new CancellationTokenSource();
        Task settlement = kit.Queue.SaveForSettlementAsync(State(1, 11), cancel.Token);
        Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 1 && kit.Queue.Pending == 0, seconds: 5));
        Assert.False(settlement.IsCompleted, "the settlement save reported a result while the database was locked");

        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settlement.WaitAsync(TimeSpan.FromSeconds(5)));

        @lock.Release();
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new uint[] { 11 }, kit.Store.Saved.Select(s => s.Money));
    }

    [Fact]
    public async Task ASettlementWhoseTokenFiresDuringTheLockedWrite_IsCancelled_AndItsSnapshotIsStillRetriedWithoutATrigger()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lockWaitEnds = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (state, token) =>
        {
            if (state.Id != 1)
            {
                return;
            }

            if (@lock.Next() == 1)
            {
                entered.TrySetResult();
                await lockWaitEnds.Task; // the driver's lock wait ignores the token
                Assert.True(token.IsCancellationRequested, "the settlement token was expected to have fired during the lock wait");
            }

            if (!@lock.IsReleased)
            {
                throw Busy();
            }
        };
        using var cancel = new CancellationTokenSource();
        try
        {
            Task settlement = kit.Queue.SaveForSettlementAsync(State(1, 11), cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancel.Cancel();
            lockWaitEnds.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settlement.WaitAsync(TimeSpan.FromSeconds(5)));

            // Not logged as a final failure and not left retained for good: the idle queue retries it once the lock is gone.
            @lock.Release();
            Assert.True(await BecomesTrueAsync(() => kit.Store.Saved.Any(s => s.Id == 1 && s.Money == 11), seconds: 10), "the retained snapshot was never retried");
        }
        finally
        {
            @lock.Release();
            lockWaitEnds.TrySetResult();
        }
    }

    [Fact]
    public async Task ANewerSnapshotSupersedesTheRetainedLockedOne_AndTheOlderIsNeverWrittenAfterIt()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        kit.Store.OnSave = (state, _) =>
        {
            if (state.Id == 1)
            {
                @lock.Next();
                if (!@lock.IsReleased)
                {
                    throw Busy();
                }
            }

            return Task.CompletedTask;
        };
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Start();
        Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 1 && kit.Queue.Pending == 0, seconds: 5));
        kit.Queue.Enqueue(State(1, 22));
        Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 2 && kit.Queue.Pending == 0, seconds: 5));

        @lock.Release();
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new uint[] { 22 }, kit.Store.Saved.Select(s => s.Money));
    }

    [Fact]
    public async Task ShutdownDrainWritesTheRetainedLockedSnapshot_AndRefusesWhileTheLockStays()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        kit.Store.OnSave = (state, _) =>
        {
            if (state.Id == 1)
            {
                @lock.Next();
                if (!@lock.IsReleased)
                {
                    throw Busy();
                }
            }

            return Task.CompletedTask;
        };
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Start();
        Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 1 && kit.Queue.Pending == 0, seconds: 5));

        // Still locked at shutdown: the drain retries inline, then reports the character instead of dropping it silently.
        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("1", refused.Message, StringComparison.Ordinal);
        Assert.Empty(kit.Store.Saved);
    }

    [Fact]
    public async Task ShutdownDrainWritesTheRetainedLockedSnapshotOnceTheLockIsGone()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        kit.Store.OnSave = (state, _) =>
        {
            if (state.Id == 1)
            {
                @lock.Next();
                if (!@lock.IsReleased)
                {
                    throw Busy();
                }
            }

            return Task.CompletedTask;
        };
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Start();
        Assert.True(await BecomesTrueAsync(() => @lock.Attempts >= 1 && kit.Queue.Pending == 0, seconds: 5));

        @lock.Release();
        await kit.Queue.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new uint[] { 11 }, kit.Store.Saved.Select(s => s.Money));
    }

    [Fact]
    public async Task ABusyFailureWrappedByEfIsDeferredToo()
    {
        await using var kit = new Kit();
        var @lock = new Locked();
        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = (state, _) =>
        {
            if (state.Id == 1)
            {
                @lock.Next();
                if (!@lock.IsReleased)
                {
                    firstAttempt.TrySetResult();
                    throw new DbUpdateException("An error occurred while saving the entity changes.", new SqliteException("SQLite Error 6: 'database table is locked'.", 6));
                }
            }

            return Task.CompletedTask;
        };
        kit.Queue.Enqueue(State(1, 11));
        kit.Queue.Enqueue(State(2, 22));
        kit.Queue.Start();
        Assert.True(await BecomesTrueAsync(() => kit.Store.Saved.Any(s => s.Id == 2), seconds: 4));
        Assert.Equal([1, 2], kit.Store.Attempts.Select(a => a.Id).Take(2)); // character 2 came right after the first failure: no inline retry
        @lock.Release();
        await kit.Queue.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(kit.Store.Saved, s => s.Id == 1 && s.Money == 11);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Action poke)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition was not reached in 10 s");
            poke();
            await Task.Delay(100);
        }
    }

    private static (TaskCompletionSource Entered, TaskCompletionSource Release) BlockOn(Kit kit, int id, uint? money = null)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Store.OnSave = async (state, token) =>
        {
            if (state.Id == id && (money is null || state.Money == money))
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        };
        return (entered, release);
    }

    private sealed class CaptureLogger : ILogger<CharacterSaveQueue>
    {
        private readonly Lock _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                {
                    return [.. _entries];
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    private static CharacterState State(int id, uint money) => new(id, 0, 12, 0, 0, 83.5f, 0, 1, 0, Money: money);

    private sealed class Kit : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public Kit(WorldRuntimeOptions? options = null, ILogger<CharacterSaveQueue>? logger = null, IOpsAnnouncer? announcer = null)
        {
            _services = new ServiceCollection().AddSingleton<ICharacterStore>(Store).BuildServiceProvider();
            Queue = new CharacterSaveQueue(_services.GetRequiredService<IServiceScopeFactory>(),
                logger ?? NullLogger<CharacterSaveQueue>.Instance, Options.Create(options ?? new WorldRuntimeOptions()), announcer);
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
