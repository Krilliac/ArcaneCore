using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
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
}
