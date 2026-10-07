using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using System.Collections.Concurrent;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

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

        Assert.Null(await read.WaitAsync(TimeSpan.FromSeconds(2)));
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
        await kit.Service.FlushCharacterAsync(1).WaitAsync(TimeSpan.FromSeconds(2));
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
        await firstSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
        pet.Health = 73;
        pet.SetUInt32(UpdateFields.UnitFieldPower5, 902);
        kit.Service.QueueCurrentPetSave(player);
        Task flush = kit.Service.FlushCharacterAsync(1);
        Task stop = kit.Service.StopAsync();
        Assert.False(flush.IsCompleted);
        Assert.False(stop.IsCompleted);
        release.SetResult();
        await Task.WhenAll(flush, stop).WaitAsync(TimeSpan.FromSeconds(2));

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
        await kit.Service.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(writes, s => s.Health == 62);
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
}
