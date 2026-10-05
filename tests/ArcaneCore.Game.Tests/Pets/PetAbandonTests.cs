using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class PetAbandonTests
{
    [Fact]
    public async Task HunterAbandon_DeletesAfterPriorCurrentSave_AndInvalidatesCachedReads()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        var currentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCurrent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> events = [];
        PersistentPetSnapshot snapshot = kit.Service.CaptureCurrentPet(owner)!;
        kit.Service.SavePersistence = async (_, _) =>
        {
            events.Add("current");
            currentEntered.SetResult();
            await releaseCurrent.Task;
        };
        kit.Service.DeletePersistence = (id, _) =>
        {
            events.Add($"delete:{id}");
            return Task.CompletedTask;
        };
        kit.Service.LoadPersistence = async (_, _) =>
        {
            await readRelease.Task;
            return snapshot;
        };
        kit.Service.LoadCallablePersistence = async (_, _) =>
        {
            await readRelease.Task;
            return snapshot with { IsCurrent = false };
        };
        kit.Service.QueueCurrentPetSave(owner);
        await currentEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<PersistentPetSnapshot?> lateRead = kit.Service.ReadCurrentPetAsync(owner);
        Task<PersistentPetSnapshot?> lateCallableRead = kit.Service.ReadCallablePetAsync(owner);
        kit.Controller.HandleAbandon(owner, pet.Guid);
        Assert.Null(owner.GetPet());
        Assert.False(kit.Service.TryGetCachedCurrentPet(owner, out _));
        releaseCurrent.SetResult();
        readRelease.SetResult();
        await kit.Service.FlushCharacterAsync(1);
        Assert.Equal(["current", "delete:1"], events);
        Assert.Null(await lateRead);
        Assert.Null(await lateCallableRead);
    }

    [Fact]
    public async Task DeadHunterAbandon_DeletesThePetRow()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        kit.Creatures.KillCreature(pet);
        int deletes = 0;
        kit.Service.DeletePersistence = (_, _) => { deletes++; return Task.CompletedTask; };
        kit.Controller.HandleAbandon(owner, pet.Guid);
        await kit.Service.FlushCharacterAsync(1);
        Assert.Equal(1, deletes);
        Assert.Null(owner.GetPet());
    }

    [Fact]
    public async Task Abandon_DeleteWaitsBehindBlockedDetachedSave()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        var detachedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDetached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> events = [];
        kit.Service.SaveDetachedPersistence = async (_, _) =>
        {
            events.Add("detached");
            detachedEntered.SetResult();
            await releaseDetached.Task;
        };
        kit.Service.DeletePersistence = (_, _) => { events.Add("delete"); return Task.CompletedTask; };
        kit.Service.QueueDetachedPetSave(owner);
        await detachedEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        kit.Controller.HandleAbandon(owner, pet.Guid);
        releaseDetached.SetResult();
        await kit.Service.FlushCharacterAsync(1);
        Assert.Equal(["detached", "delete"], events);
    }

    [Fact]
    public async Task Abandon_DeleteFailurePropagatesThroughFlush()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        kit.Service.DeletePersistence = (_, _) => Task.FromException(new InvalidOperationException("delete failed"));
        kit.Controller.HandleAbandon(owner, pet.Guid);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Service.FlushCharacterAsync(1));
    }

    [Fact]
    public async Task GuardianAbandon_OnlyUnsummonsAndNeverDeletesHunterRow()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.GuardianSpell));
        Creature guardian = Assert.Single(kit.Creatures.Creatures);
        int deletes = 0;
        kit.Service.DeletePersistence = (_, _) => { deletes++; return Task.CompletedTask; };
        kit.Controller.HandleAbandon(owner, guardian.Guid);
        await kit.Service.FlushCharacterAsync(1);
        Assert.Equal(0, deletes);
        Assert.Empty(kit.Creatures.Creatures);
    }

    [Fact]
    public async Task StaleOrUnownedAbandon_DoesNotMutateTheCurrentPet()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        (Player other, _) = kit.AddPlayer(2, 4, 0);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        int deletes = 0;
        kit.Service.DeletePersistence = (_, _) => { deletes++; return Task.CompletedTask; };
        kit.Controller.HandleAbandon(other, pet.Guid);
        kit.Controller.HandleAbandon(owner, new ObjectGuid(pet.Guid.Value + 1));
        await kit.Service.FlushCharacterAsync(1);
        Assert.Equal(pet.Guid, owner.GetPet()?.Guid);
        Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(0, deletes);
    }

    [Fact]
    public async Task HeldOrInTransitOwner_CannotAbandonPet()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, PetTestKit.PetSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        int deletes = 0;
        kit.Service.DeletePersistence = (_, _) => { deletes++; return Task.CompletedTask; };
        kit.Spells.System.IsInTransit = _ => true;
        kit.Controller.HandleAbandon(owner, pet.Guid);
        kit.Spells.System.IsInTransit = _ => false;
        Assert.Equal(pet.Guid, owner.GetPet()?.Guid);
        Guid operation = Guid.NewGuid();
        Assert.True(owner.BeginQuestSettlement(operation));
        kit.Controller.HandleAbandon(owner, pet.Guid);
        Assert.True(owner.EndQuestSettlement(operation));
        await kit.Service.FlushCharacterAsync(1);
        Assert.Equal(pet.Guid, owner.GetPet()?.Guid);
        Assert.Equal(0, deletes);
    }
}
