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
            int callerThread = Environment.CurrentManagedThreadId;
            int storeThread = -1;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new ManualResetEventSlim(false);
            kit.Service.SavePersistence = (_, _) =>
            {
                storeThread = Environment.CurrentManagedThreadId;
                entered.SetResult();
                release.Wait(HangBound); // a blocked store (database is locked) must not hold the caller
                return Task.CompletedTask;
            };

            kit.Service.QueueCurrentPetSave(player); // returns while the store is still blocked, or this test hangs on main
            await entered.Task.WaitAsync(HangBound);
            Assert.NotEqual(callerThread, storeThread);
            release.Set();
            await kit.Service.FlushCharacterAsync(1).WaitAsync(HangBound);
        }
    }

    [Fact]
    public async Task QueueSerializedWriteAsync_DoesNotRunTheWriteOnTheCallingThread()
    {
        using PetTestKit kit = new();
        int callerThread = Environment.CurrentManagedThreadId;
        int writeThread = -1;

        int result = await kit.Service.QueueSerializedWriteAsync(1, _ =>
        {
            writeThread = Environment.CurrentManagedThreadId;
            return Task.FromResult(7);
        }).WaitAsync(HangBound);

        Assert.Equal(7, result);
        Assert.NotEqual(-1, writeThread);
        Assert.NotEqual(callerThread, writeThread); // an idle queue used to run the stable's store call inline on the world thread
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
