using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Pets;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// Owner death, pet corpses, guardian flags, mini pet create spells, follow arrival, attack orders of a pacified owner and the
/// possessed-pet leash (vmangos Player::SetDeathState, Pet::SetDeathState, Pet::Update, Pet::InitStatsForLevel,
/// Spell::EffectSummonCritter, PetAI::MovementInform, Unit::HandlePetCommand).
/// </summary>
public sealed class PetLifecycleFidelityTests
{

    private static Creature Enemy(PetTestKit kit, float x, float y)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, x, y, 83.5f, 0);

    private static Creature SummonOf(PetTestKit kit, SummonKind kind)
        => Assert.Single(kit.Creatures.Creatures, c => c.Summon?.Kind == kind);

    [Fact]
    public void PlayerDeath_UnsummonsItsPetAndMiniPetAtOnce_EvenInCombat_ButAGuardianInCombatStays()
    {
        // vmangos Player::SetDeathState(JUST_DIED): RemovePet(PET_SAVE_REAGENTS) and RemoveMiniPet() (Player.cpp:1527-1531);
        // a guardian only goes through Pet::Update's "owner dead and out of combat" rule (Pet.cpp:696-701).
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(owner, PetSpell);
        kit.Cast(owner, CritterSpell);
        kit.Cast(owner, GuardianSpell);
        Creature pet = SummonOf(kit, SummonKind.Pet);
        Creature mini = SummonOf(kit, SummonKind.MiniPet);
        Creature guardian = SummonOf(kit, SummonKind.Guardian);

        Creature enemy = Enemy(kit, 8, 5);
        Assert.True(kit.Map.Combat.Attack(pet, enemy));
        Assert.True(kit.Map.Combat.Attack(guardian, enemy));

        kit.Map.Combat.Kill(enemy, owner);
        kit.Run(100);

        Assert.Null(kit.Creatures.FindCreature(pet.Guid));
        Assert.True(owner.PetGuid.IsEmpty);
        Assert.Null(kit.Creatures.FindCreature(mini.Guid));
        Assert.Same(guardian, kit.Creatures.FindCreature(guardian.Guid));
    }

    [Fact]
    public void ThePetLeash_DoesNotApplyToAPetItsOwnerIsPossessing()
    {
        // vmangos Pet::Update: ... && !IsWithinDistInMap(owner, 120) && !(owner->GetCharmGuid() == GetObjectGuid()) (Pet.cpp:668-673).
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, PetSpell);
        Creature pet = SummonOf(kit, SummonKind.Pet);
        owner.SetUInt64(UpdateFields.UnitFieldCharm, pet.Guid.Value);

        owner.SetPosition(125, 0, owner.Z, 0);
        kit.Run(100);

        Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid));
        Assert.Equal(pet.Guid, owner.PetGuid);
    }

    [Fact]
    public void TheDismissCommand_LeavesAHunterPet_WhichGoesThroughTheDismissPetSpell()
    {
        // vmangos Unit::HandlePetCommand COMMAND_DISMISS: "Hunter pets are dismissed with a spell with a cast time",
        // so only a non-hunter pet is unsummoned by the command.
        using var kit = new PetTestKit();
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Cast(hunter, PetSpell);
        Creature hunterPet = SummonOf(kit, SummonKind.Pet);

        kit.Controller.HandleCommand(hunterPet, CommandState.Dismiss, null);
        Assert.Same(hunterPet, kit.Creatures.FindCreature(hunterPet.Guid));
        Assert.Equal(hunterPet.Guid, hunter.PetGuid);

        (Player warlock, _) = kit.AddPlayer(2);
        kit.Cast(warlock, PetSpell);
        Creature demon = Assert.Single(kit.Creatures.Creatures, c => c.OwnerGuid == warlock.Guid);
        kit.Controller.HandleCommand(demon, CommandState.Dismiss, null);
        Assert.Null(kit.Creatures.FindCreature(demon.Guid));
        Assert.True(warlock.PetGuid.IsEmpty);
    }
}
