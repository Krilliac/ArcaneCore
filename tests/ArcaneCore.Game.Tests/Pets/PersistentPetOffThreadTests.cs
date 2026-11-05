using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.Diagnostics;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <remarks>Waits are hang bounds, not timings: the work runs on the thread pool, which a loaded test run can delay.</remarks>
public sealed class PersistentPetOffThreadTests
{
    private static readonly TimeSpan HangBound = TimeSpan.FromSeconds(30);

    private static (PetTestKit Kit, Player Player) HunterWithPet()
    {
        PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, PetTestKit.PetSpell));
        return (kit, player);
    }

    [Fact]
    public void QueueDetachedPetSave_CaptureFailure_ReportsAPersistenceAnomaly_AndWritesNothing()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int writes = 0;
            kit.Service.SaveDetachedPersistence = (_, _) => { Interlocked.Increment(ref writes); return Task.CompletedTask; };
            kit.Service.DetachedCaptureOverride = _ => throw new InvalidOperationException("synthetic capture failure");
            using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Key == "pet/persistence-fault" && n.Subject == "character 1");

            // The capture runs synchronously on the calling thread, before the first await of the queued save.
            kit.Service.QueueDetachedPetSave(player);

            Assert.Equal(1, capture.Count("pet/persistence-fault"));
            Assert.Equal(0, Volatile.Read(ref writes));
        }
    }

    [Fact]
    public async Task QueueCurrentPetSave_DoesNotCallTheStoreOnTheCallingThread()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim(false);
            kit.Service.SavePersistence = (_, _) =>
            {
                entered.SetResult();
                release.Wait(HangBound); // a blocked store (database is locked) must not hold the caller
                return Task.CompletedTask;
            };

            // Comparing thread ids would be flaky: the test itself runs on a pool thread that the queued store can reuse.
            // Run inline, the call below would sit in the blocked store until the HangBound wait ends, then return with the store done and the flush complete.
            kit.Service.QueueCurrentPetSave(player);
            Task flush = kit.Service.FlushCharacterAsync(1);
            Assert.False(flush.IsCompleted, "QueueCurrentPetSave did not return until the blocked store call finished: the save ran on the calling thread");
            await entered.Task.WaitAsync(HangBound);
            Assert.False(flush.IsCompleted);
            release.Set();
            await flush.WaitAsync(HangBound);
        }
    }

    [Fact]
    public async Task QueueSerializedWriteAsync_DoesNotRunTheWriteOnTheCallingThread()
    {
        using PetTestKit kit = new();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);

        // A blocked store call must not hold the caller. (Comparing thread ids would be flaky: the test itself runs on a pool thread.)
        // Inline on an idle queue, the call below would not return until the 5 s wait ends, and the task would already be complete.
        Task<int> write = kit.Service.QueueSerializedWriteAsync(1, _ =>
        {
            entered.SetResult();
            release.Wait(TimeSpan.FromSeconds(5));
            return Task.FromResult(7);
        });

        Assert.False(write.IsCompleted);
        await entered.Task.WaitAsync(HangBound);
        release.Set();
        Assert.Equal(7, await write.WaitAsync(HangBound));
    }

    [Fact]
    public async Task QueueDeletePet_DoesNotCallTheStoreOnTheCallingThread()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int callerThread = Environment.CurrentManagedThreadId;
            int storeThread = -1;
            kit.Service.DeletePersistence = (_, _) =>
            {
                storeThread = Environment.CurrentManagedThreadId;
                return Task.CompletedTask;
            };

            kit.Service.QueueDeletePet(player);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            Assert.NotEqual(-1, storeThread);
            Assert.NotEqual(callerThread, storeThread);
        }
    }

    [Fact]
    public async Task FailedDelete_IsRetriedAfterATransientFailureAndSucceeds()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int attempts = 0;
            kit.Service.DeletePersistence = (_, _) => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new IOException("database is locked"))
                : Task.CompletedTask;

            kit.Service.QueueDeletePet(player);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound); // threw on main: the failed delete was never retried
            Assert.Equal(2, Volatile.Read(ref attempts));
        }
    }

    [Fact]
    public async Task FailedDelete_IsKeptAndRetriedByTheNextBarrierOnceStorageRecovers()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int deleted = 0;
            bool storageDown = true;
            kit.Service.DeletePersistence = (_, _) =>
            {
                if (Volatile.Read(ref storageDown)) return Task.FromException(new IOException("database is locked"));
                Interlocked.Increment(ref deleted);
                return Task.CompletedTask;
            };

            kit.Service.QueueDeletePet(player);
            await Assert.ThrowsAsync<IOException>(() => kit.Service.FlushCharacterAsync(1)).WaitAsync(HangBound);
            Assert.Equal(0, Volatile.Read(ref deleted));

            Volatile.Write(ref storageDown, false);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            Assert.Equal(1, Volatile.Read(ref deleted));
            await kit.Service.StopAsync().WaitAsync(HangBound);
            Assert.Equal(1, Volatile.Read(ref deleted)); // done: not deleted a third time
        }
    }

    [Fact]
    public async Task SaveQueuedAfterAPendingDelete_DoesNotBringTheDeletedPetBack()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            int saves = 0;
            bool storageDown = true;
            kit.Service.SavePersistence = (_, _) => { Interlocked.Increment(ref saves); return Task.CompletedTask; };
            kit.Service.DeletePersistence = (_, _) => Volatile.Read(ref storageDown)
                ? Task.FromException(new IOException("database is locked"))
                : Task.CompletedTask;

            kit.Service.QueueDeletePet(player);
            kit.Service.QueueCurrentPetSave(player); // the abandoned pet is still on the map: a save of it must be dropped
            await Assert.ThrowsAsync<IOException>(() => kit.Service.FlushCharacterAsync(1)).WaitAsync(HangBound);

            Volatile.Write(ref storageDown, false);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
            await kit.Service.StopAsync().WaitAsync(HangBound);
            Assert.Equal(0, Volatile.Read(ref saves));
        }
    }

    /// <summary>One overall deadline for a wedge check; a wedged queue slot never completes, so the wait must fail the test instead of hanging it.</summary>
    private static readonly TimeSpan WedgeDeadline = TimeSpan.FromSeconds(10);

    private sealed class SaveProbeException(string message) : Exception(message);

    [Fact]
    public async Task SaveFailingWithOutOfMemory_DoesNotWedgeTheCharactersQueue()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            kit.Service.SavePersistence = (_, _) => Task.FromException(new OutOfMemoryException("simulated"));
            kit.Service.QueueCurrentPetSave(player);

            // The queue slot completes with the out-of-memory failure; a slot that never completes would hang every later barrier.
            await Assert.ThrowsAsync<OutOfMemoryException>(() => kit.Service.FlushCharacterAsync(1)).WaitAsync(WedgeDeadline);

            int saves = 0;
            kit.Service.SavePersistence = (_, _) => { Interlocked.Increment(ref saves); return Task.CompletedTask; };
            kit.Service.QueueCurrentPetSave(player);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(WedgeDeadline);
            Assert.Equal(1, Volatile.Read(ref saves));
        }
    }

    [Fact]
    public async Task DeleteFailingWithOutOfMemory_DoesNotWedgeTheCharactersQueue()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            kit.Service.DeletePersistence = (_, _) => Task.FromException(new OutOfMemoryException("simulated"));
            kit.Service.QueueDeletePet(player);

            await Assert.ThrowsAsync<OutOfMemoryException>(() => kit.Service.FlushCharacterAsync(1)).WaitAsync(WedgeDeadline);

            // A later delete runs although its predecessor completed with the out-of-memory failure, and a stop drain still finishes.
            int deleted = 0;
            kit.Service.DeletePersistence = (_, _) => { Interlocked.Increment(ref deleted); return Task.CompletedTask; };
            kit.Service.QueueDeletePet(player);
            await kit.Service.FlushCharacterAsync(1).WaitAsync(WedgeDeadline);
            Assert.True(Volatile.Read(ref deleted) >= 1);
            await kit.Service.StopAsync().WaitAsync(WedgeDeadline);
        }
    }

    [Fact]
    public async Task OneQueuedSaveFailure_IsRecordedAndReportedOnce()
    {
        (PetTestKit kit, Player player) = HunterWithPet();
        using (kit)
        {
            string message = "probe " + Guid.NewGuid();
            string probeType = typeof(SaveProbeException).FullName!;
            using AnomalyCapture capture = AnomalyLedger.Shared.Capture(n => n.Subsystem == "pet" && n.Detail == message);
            long faultsBefore = FaultLedger.Shared.Snapshot().Where(s => s.ExceptionType == probeType).Sum(s => s.Count);
            int calls = 0;
            kit.Service.SavePersistence = (_, _) => Interlocked.Increment(ref calls) == 1
                ? Task.FromException(new SaveProbeException(message))
                : Task.CompletedTask;

            kit.Service.QueueCurrentPetSave(player); // fails once; the barrier below then retries the retained snapshot, which succeeds
            await kit.Service.FlushCharacterAsync(1).WaitAsync(WedgeDeadline);
            await kit.Service.StopAsync().WaitAsync(WedgeDeadline);

            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Single(capture.Notices);
            Assert.Equal(1, FaultLedger.Shared.Snapshot().Where(s => s.ExceptionType == probeType).Sum(s => s.Count) - faultsBefore);
        }
    }
}
