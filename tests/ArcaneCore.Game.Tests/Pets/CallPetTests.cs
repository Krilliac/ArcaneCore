using ArcaneCore.Game;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

public sealed class CallPetTests
{
    private const uint CallSpell = 991001;

    [Fact]
    public async Task Effect56_LoadsPreloadedCurrentHunterPetAndPreservesStablePetNumber()
    {
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 701, PetTestKit.PetEntry, 5, 0, 50, 0, 0, 1, [1, 2, 3], []));
        await kit.Service.ReadCurrentPetAsync(owner);
        Assert.Equal(SpellCastResult.CastOk, kit.Cast(owner, CallSpell));
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(701u, pet.Summon!.Charm!.PetNumber);
        Assert.Equal(pet.Guid, owner.PetGuid);
    }

    [Fact]
    public async Task Effect56_NoRowOrDeadRowSendsSourceFailureReasons()
    {
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(null);
        await kit.Service.ReadCurrentPetAsync(owner);
        kit.Cast(owner, CallSpell);
        Assert.Equal((byte)PetTameFailureReason.NoPetAvailable,
            Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetTameFailure).Payload[0]);

        session.Clear();
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 702, PetTestKit.PetEntry, 5, 0, 0, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);
        kit.Cast(owner, CallSpell);
        Assert.Equal((byte)PetTameFailureReason.Dead,
            Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetTameFailure).Payload[0]);
    }

    [Fact]
    public async Task Effect56_RefusesWhenAWorldPetAlreadyExists()
    {
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(owner, PetTestKit.PetSpell);
        Creature existing = Assert.Single(kit.Creatures.Creatures);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 703, PetTestKit.PetEntry, 5, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);
        kit.Cast(owner, CallSpell);
        Assert.Same(existing, kit.Creatures.FindCreature(existing.Guid));
    }

    [Fact]
    public async Task Effect56_ForgetCharacterTombstonePreventsCachedResurrection()
    {
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 704, PetTestKit.PetEntry, 5, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);
        kit.Service.ForgetCharacter(1);
        kit.Cast(owner, CallSpell);
        Assert.Empty(kit.Creatures.Creatures);
        Assert.Equal((byte)PetTameFailureReason.NoPetAvailable,
            Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetTameFailure).Payload[0]);
    }

    [Fact]
    public async Task Effect56_NullReloadClearsOlderCachedSnapshot()
    {
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))]);
        (Player owner, FakeSession session) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 705, PetTestKit.PetEntry, 5, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(null);
        await kit.Service.ReadCurrentPetAsync(owner);
        kit.Cast(owner, CallSpell);
        Assert.Empty(kit.Creatures.Creatures);
        Assert.Equal((byte)PetTameFailureReason.NoPetAvailable,
            Assert.Single(session.Sent, p => p.Opcode == WorldOpcode.SmsgPetTameFailure).Payload[0]);
    }
}
