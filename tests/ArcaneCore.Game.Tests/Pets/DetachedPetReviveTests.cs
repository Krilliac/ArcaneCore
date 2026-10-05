using ArcaneCore.Game;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class DetachedPetReviveTests
{
    [Fact]
    public async Task LivingDetachedPet_RefusesRevivalWithoutPromotion()
    {
        using PetTestKit kit = new();
        (Player owner, var session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        PersistentPetSnapshot detached = new(1, 8811, PetTestKit.PetEntry, 5, 12, 20, 0, 0, 1, [], [], IsCurrent: false);
        kit.Service.LoadCallablePersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(detached);
        await kit.Service.ReadCallablePetAsync(owner);
        Assert.False(kit.Service.SummonDeadPet(owner, 50, kit.Spells.System));
        Assert.Empty(kit.Creatures.Creatures);
        Assert.Equal(new byte[] { 11 }, Assert.Single(session.Sent, packet => packet.Opcode == WorldOpcode.SmsgPetTameFailure).Payload);
        Assert.True(kit.Service.TryGetCachedCurrentPet(owner, out PersistentPetSnapshot unchanged));
        Assert.Equal(detached, unchanged);
    }

    [Fact]
    public async Task DetachedDeadPet_Effect109SpawnsThenPromotesAuthoritativeCurrentSnapshot()
    {
        using PetTestKit kit = new();
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        PersistentPetSnapshot detached = new(1, 8811, PetTestKit.PetEntry, 5, 12, 0, 0, 0, 1, [], [],
            IsCurrent: false, Name: "Rex", NameTimestamp: 7, RenameAllowed: false);
        kit.Service.LoadCallablePersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(detached);
        PersistentPetSnapshot? saved = null;
        kit.Service.SavePersistence = (snapshot, _) => { saved = snapshot; return Task.CompletedTask; };
        Assert.Equal(detached, await kit.Service.ReadCallablePetAsync(owner));

        Assert.True(kit.Service.SummonDeadPet(owner, 50, kit.Spells.System));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(7u, pet.GetUInt32(UpdateFields.UnitFieldPetNameTimestamp));
        Assert.Equal(pet.MaxHealth / 2, pet.Health);
        await kit.Service.FlushCharacterAsync(1);
        Assert.NotNull(saved);
        Assert.True(saved!.IsCurrent);
        Assert.Equal((8811u, "Rex", 7u), (saved.PetNumber, saved.Name, saved.NameTimestamp));
        Assert.Equal(pet.Health, saved.Health);
    }
}
