using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using System.Collections.Concurrent;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

/// <remarks>
/// The waits on background persistence work are hang bounds, not timings: the continuations run on the thread pool, which a fully
/// loaded test run can delay for seconds (a 2 s bound timed out a read that had already completed).
/// </remarks>
public sealed class PersistentPetTests
{
    [Fact]
    public async Task DeletedCharacter_LateStoreReadCannotRepopulatePetCache()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        var row = new PersistentPetSnapshot(1, 77, PetTestKit.PetEntry, 5, 0, 0, 0, 0, 1, [], []);
        var result = new TaskCompletionSource<PersistentPetSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Service.LoadPersistence = (_, _) => result.Task;
        Task<PersistentPetSnapshot?> read = kit.Service.ReadCurrentPetAsync(player);
        kit.Service.ForgetCharacter(1);
        result.SetResult(row);

        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(kit.Service.SummonDeadPet(player, 50, kit.Spells.System));
        Assert.True(player.PetGuid.IsEmpty);
    }

    [Fact]
    public async Task DeathSnapshot_SurvivesCorpseRemovalAndSupportsCachedRevival()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        uint number = pet.Summon!.Charm!.PetNumber;
        PersistentPetSnapshot? saved = null;
        kit.Service.SavePersistence = (snapshot, _) => { saved = snapshot; return Task.CompletedTask; };

        kit.Creatures.KillCreature(pet);
        await kit.Service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(saved);
        Assert.Equal(0u, saved.Health);
        kit.Creatures.Despawn(pet);
        player.Map!.Pets!.Update(player.Map!, 1);
        Assert.True(player.PetGuid.IsEmpty);

        Assert.True(kit.Service.SummonDeadPet(player, 50, kit.Spells.System));
        Creature revived = Assert.Single(kit.Creatures.Creatures);
        Assert.NotEqual(pet.Guid, revived.Guid);
        Assert.Equal(number, revived.Summon!.Charm!.PetNumber);
        Assert.Equal(revived.MaxHealth / 2, revived.Health);
    }

    [Fact]
    public void CaptureCurrentPet_RecordsStablePetIdentityAndExcludesNonHunterOwners()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, PetTestKit.PetSpell));
        var snapshot = kit.Service.CaptureCurrentPet(player);
        Assert.NotNull(snapshot);
        Assert.Equal((uint)player.PetGuid.Counter, snapshot!.PetNumber);
        Assert.Equal(PetTestKit.PetEntry, snapshot.Entry);

        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Warlock);
        Assert.Null(kit.Service.CaptureCurrentPet(player));
    }

    [Fact]
    public void CachedRestores_UseFreshWorldGuidsWhileKeepingStablePetNumbers()
    {
        using PetTestKit kit = new();
        (Player first, _) = kit.AddPlayer(1);
        (Player second, _) = kit.AddPlayer(2, 4, 0);
        first.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        second.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        var a = new PersistentPetSnapshot(1, 77, PetTestKit.PetEntry, 5, 0, 50, 0, 0, 1, [], []);
        var b = new PersistentPetSnapshot(2, 77, PetTestKit.PetEntry, 5, 0, 50, 0, 0, 1, [], []);
        var pa = kit.Service.RestoreCurrentPet(first, a);
        var pb = kit.Service.RestoreCurrentPet(second, b);
        Assert.NotNull(pa); Assert.NotNull(pb);
        Assert.NotEqual(pa!.Guid, pb!.Guid);
        Assert.Equal(77u, pa.Summon!.Charm!.PetNumber);
        Assert.Equal(77u, pb.Summon!.Charm!.PetNumber);
    }

    [Fact]
    public async Task QueuedSaves_AreOrderedAndStopDrainsPendingWrites()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(player, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        var firstSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new ConcurrentQueue<PersistentPetSnapshot>();
        kit.Service.SavePersistence = async (snapshot, _) =>
        {
            writes.Enqueue(snapshot);
            if (writes.Count == 1)
            {
                firstSeen.SetResult();
                await release.Task.ConfigureAwait(false);
            }
        };

        pet.Health = 41;
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 501);
        kit.Service.QueueCurrentPetSave(player);
        await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(30));
        pet.Health = 73;
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 902);
        kit.Service.QueueCurrentPetSave(player);
        Task flush = kit.Service.FlushCharacterAsync(1);
        Task stop = kit.Service.StopAsync();
        Assert.False(flush.IsCompleted);
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await Task.WhenAll(flush, stop).WaitAsync(TimeSpan.FromSeconds(30));

        PersistentPetSnapshot[] saved = writes.ToArray();
        Assert.Equal(2, saved.Length);
        Assert.Equal((41u, 501u), (saved[0].Health, saved[0].Happiness));
        Assert.Equal((73u, 902u), (saved[1].Health, saved[1].Happiness));
    }

    [Fact]
    public async Task FailedSave_DoesNotPoisonNewerSnapshot()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        var writes = new ConcurrentQueue<PersistentPetSnapshot>();
        int attempt = 0;
        kit.Service.SavePersistence = (snapshot, _) =>
        {
            writes.Enqueue(snapshot);
            if (Interlocked.Increment(ref attempt) == 1) throw new InvalidOperationException("synthetic failure");
            return Task.CompletedTask;
        };
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        pet.Health = 31;
        kit.Service.QueueCurrentPetSave(player);
        pet.Health = 62;
        kit.Service.QueueCurrentPetSave(player);
        await kit.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(writes, s => s.Health == 62);
    }

    [Fact]
    public async Task FailedQueuedSave_IsRetainedAndTheLoginBarrierWritesIt()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        var writes = new ConcurrentQueue<PersistentPetSnapshot>();
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int attempt = 0;
        kit.Service.SavePersistence = (snapshot, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                failed.SetResult();
                throw new InvalidOperationException("synthetic failure");
            }
            writes.Enqueue(snapshot);
            return Task.CompletedTask;
        };
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        pet.Health = 31;
        kit.Service.QueueCurrentPetSave(player);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The login barrier (PersistentPetFeature.OnPlayerLoadingAsync, CharacterPersistenceBarrier) writes the retained
        // snapshot before the row is read back, so the level, loyalty and health the failed save carried are not lost.
        await kit.Service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(31u, Assert.Single(writes).Health);
        await kit.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(writes);
    }

    [Fact]
    public async Task APetWriteThatFailed_ShowsAsRetainedForTheQueueProbe_UntilTheBarrierWritesIt()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        int attempt = 0;
        kit.Service.SavePersistence = (_, _) => Interlocked.Increment(ref attempt) == 1
            ? throw new InvalidOperationException("synthetic failure")
            : Task.CompletedTask;
        Assert.Empty(kit.Service.RetainedPersistenceCharacters);
        Assert.Equal(0, kit.Service.PendingPersistenceWrites);

        kit.Service.QueueCurrentPetSave(player);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (kit.Service.RetainedPersistenceCharacters.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the failed write was never retained");
            await Task.Delay(5);
        }

        Assert.Equal([1], kit.Service.RetainedPersistenceCharacters);
        Assert.Equal(0, kit.Service.PendingPersistenceWrites); // the attempt is over: retained, not in flight

        await kit.Service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(kit.Service.RetainedPersistenceCharacters);
        await kit.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task FailedLogoutSave_FailsTheBarrierUntilStorageRecoversThenWritesTheRetainedSnapshot()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        var writes = new ConcurrentQueue<PersistentPetSnapshot>();
        int attempts = 0;
        bool storageDown = true;
        kit.Service.SavePersistence = (snapshot, _) =>
        {
            Interlocked.Increment(ref attempts);
            if (Volatile.Read(ref storageDown)) throw new InvalidOperationException("synthetic failure");
            writes.Enqueue(snapshot);
            return Task.CompletedTask;
        };
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        pet.Health = 31;
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.SaveCurrentPetAsync(player)).WaitAsync(TimeSpan.FromSeconds(30));
        pet.Health = 5; // not captured again: the barrier writes the snapshot the logout save took

        // While storage is down the barrier fails closed, and each barrier tries the retained snapshot again.
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.FlushCharacterAsync(1)).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, Volatile.Read(ref attempts));
        Volatile.Write(ref storageDown, false);
        await kit.Service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(3, Volatile.Read(ref attempts));
        Assert.Equal(31u, Assert.Single(writes).Health);
    }

    [Fact]
    public async Task FailedSave_IsRetriedByStop()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        var writes = new ConcurrentQueue<PersistentPetSnapshot>();
        int attempt = 0;
        kit.Service.SavePersistence = (snapshot, _) =>
        {
            if (Interlocked.Increment(ref attempt) == 1) throw new InvalidOperationException("synthetic failure");
            writes.Enqueue(snapshot);
            return Task.CompletedTask;
        };
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        pet.Health = 31;
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.SaveCurrentPetAsync(player)).WaitAsync(TimeSpan.FromSeconds(30));

        await kit.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(31u, Assert.Single(writes).Health);
    }

    [Fact]
    public async Task AbandonDelete_SupersedesARetainedFailedSave()
    {
        using PetTestKit kit = new();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(player, PetTestKit.PetSpell);
        int saves = 0;
        int deletes = 0;
        kit.Service.SavePersistence = (_, _) =>
        {
            Interlocked.Increment(ref saves);
            throw new InvalidOperationException("synthetic failure");
        };
        kit.Service.DeletePersistence = (_, _) => { Interlocked.Increment(ref deletes); return Task.CompletedTask; };
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.SaveCurrentPetAsync(player)).WaitAsync(TimeSpan.FromSeconds(30));

        kit.Service.QueueDeletePet(player);
        await kit.Service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(30));
        await kit.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, Volatile.Read(ref saves)); // the abandoned pet's failed snapshot is never written back
        Assert.Equal(1, Volatile.Read(ref deletes));
    }

    [Fact]
    public void CachedRestore_RehydratesLiveCooldownsAndSkipsUnknownRows()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        var snapshot = new PersistentPetSnapshot(1, 88, PetTestKit.PetEntry, 5, 0, 80, 0, 0, 1, [], [],
            Cooldowns: [new PersistentPetCooldown(0, PetTestKit.PetBiteSpell, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000),
                new PersistentPetCooldown(0, 999_999, 0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60_000)]);
        Creature pet = Assert.IsType<Creature>(kit.Service.RestoreCurrentPet(owner, snapshot));
        Assert.Contains(kit.Spells.System.GetActiveCooldowns(pet), c => c.SpellId == PetTestKit.PetBiteSpell);
        Assert.DoesNotContain(kit.Spells.System.GetActiveCooldowns(pet), c => c.SpellId == 999_999);
    }

    // Pet.cpp:413 (LoadPetFromDB) -> SynchronizeLevelWithOwner (Pet.cpp:2353-2356): a hunter pet saved above its owner loads at the owner's level.
    [Theory]
    [InlineData(25, 20, 20u, 200u, 200u, 50u, 50u, 0u, 12)]
    [InlineData(20, 20, 20u, 200u, 90u, 50u, 7u, 500u, 10)]
    [InlineData(5, 20, 5u, 100u, 90u, 10u, 7u, 500u, 10)]
    public void CachedRestore_ClampsAHunterPetToTheOwnersLevel(byte savedLevel, byte ownerLevel, uint expectedLevel, uint expectedMaxHealth, uint expectedHealth, uint expectedMaxMana, uint expectedMana, uint expectedExperience, int expectedTrainingPoints)
    {
        var content = new PetContent(
            [
                new PetLevelStats(PetTestKit.PetEntry, 1, 50, 5, 5, 1, 2, 3, 4, 5, 6, 7),
                new PetLevelStats(PetTestKit.PetEntry, 5, 100, 10, 5, 1, 2, 3, 4, 5, 6, 7),
                new PetLevelStats(PetTestKit.PetEntry, 20, 200, 50, 5, 1, 2, 3, 4, 5, 6, 7),
                new PetLevelStats(PetTestKit.PetEntry, 25, 300, 60, 5, 1, 2, 3, 4, 5, 6, 7),
            ], []);
        using PetTestKit kit = new(petContent: content);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        owner.Level = ownerLevel;
        var snapshot = new PersistentPetSnapshot(1, 88, PetTestKit.PetEntry, savedLevel, 500, 90, 7, 0, 1, [], [], LoyaltyLevel: 3, TrainingPoints: 10);
        Creature pet = Assert.IsType<Creature>(kit.Service.RestoreCurrentPet(owner, snapshot));
        Assert.Equal(expectedLevel, (uint)pet.Level);
        Assert.Equal(expectedMaxHealth, pet.MaxHealth);
        Assert.Equal(expectedHealth, pet.Health);
        Assert.Equal(expectedMaxMana, pet.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        Assert.Equal(expectedMana, pet.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(expectedExperience, pet.GetUInt32(UpdateFields.UnitFieldPetexperience));
        Assert.Equal(expectedTrainingPoints, pet.Summon!.Charm!.TrainingPoints);
    }
}
