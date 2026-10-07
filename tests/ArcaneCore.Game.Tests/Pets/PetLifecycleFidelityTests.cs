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
    private const uint PetCastSpell = 910301;

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

    [Fact]
    public void Guardian_TakesItsOwnersPlayerControlledAndPvpFlags()
    {
        // vmangos Pet::InitStatsForLevel (Pet.cpp:1472-1479), called by EffectSummonGuardian for every guardian (SpellEffects.cpp:2884):
        // a non-mini pet copies UNIT_FLAG_PLAYER_CONTROLLED and the PvP flag from its owner.
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        owner.UnitFlags |= UnitFlags.PlayerControlled | UnitFlags.Pvp;

        kit.Cast(owner, GuardianSpell);

        Creature guardian = SummonOf(kit, SummonKind.Guardian);
        Assert.Equal(UnitFlags.PlayerControlled, guardian.UnitFlags & UnitFlags.PlayerControlled);
        Assert.Equal(UnitFlags.Pvp, guardian.UnitFlags & UnitFlags.Pvp);

        // and a guardian of an unflagged owner carries neither
        using var other = new PetTestKit();
        (Player calm, _) = other.AddPlayer(2);
        calm.UnitFlags &= ~(UnitFlags.PlayerControlled | UnitFlags.Pvp);
        other.Cast(calm, GuardianSpell);
        Creature calmGuardian = SummonOf(other, SummonKind.Guardian);
        Assert.Equal(UnitFlags.None, calmGuardian.UnitFlags & (UnitFlags.PlayerControlled | UnitFlags.Pvp));
    }

    [Fact]
    public void AMiniPet_LearnsItsCreateSpells()
    {
        // vmangos Spell::EffectSummonCritter: critter->InitPetCreateSpells() (SpellEffects.cpp:5452, "e.g. disgusting oozeling").
        using var kit = new PetTestKit(petContent: new PetContent([], [new PetCreateSpells(MiniPetEntry, [PetPassiveSpell])]));
        (Player owner, _) = kit.AddPlayer(1);

        kit.Cast(owner, CritterSpell);

        Creature mini = SummonOf(kit, SummonKind.MiniPet);
        Assert.True(mini.Summon!.Charm!.HasSpell(PetPassiveSpell));
        Assert.True(kit.Spells.System.HasAura(mini, PetPassiveSpell));
    }

    [Fact]
    public void ADeadSummonedPetOrGuardian_IsUnsummonedAfterFifteenSeconds_AHunterPetAfterAnHour()
    {
        // vmangos Pet::SetDeathState(CORPSE): m_corpseDecayTimer = HUNTER_PET ? 3600000 : 15000 (Pet.cpp:649-653),
        // and Pet::Update unsummons when it runs out (Pet.cpp:677-686).
        using var kit = new PetTestKit();
        (Player warlock, _) = kit.AddPlayer(1);
        kit.Cast(warlock, PetSpell);
        kit.Cast(warlock, GuardianSpell);
        Creature pet = SummonOf(kit, SummonKind.Pet);
        Creature guardian = SummonOf(kit, SummonKind.Guardian);

        kit.Creatures.KillCreature(pet);
        kit.Creatures.KillCreature(guardian);
        kit.Run(14_000);
        Assert.Same(pet, kit.Creatures.FindCreature(pet.Guid));
        Assert.Equal(pet.Guid, warlock.PetGuid);
        Assert.Same(guardian, kit.Creatures.FindCreature(guardian.Guid));

        kit.Run(1_200);
        Assert.Null(kit.Creatures.FindCreature(pet.Guid));
        Assert.True(warlock.PetGuid.IsEmpty);
        Assert.Null(kit.Creatures.FindCreature(guardian.Guid));
        Assert.Empty(kit.Map.Pets!.Summons);

        using var hunterKit = new PetTestKit();
        (Player hunter, _) = hunterKit.AddPlayer(2);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunterKit.Cast(hunter, PetSpell);
        Creature hunterPet = SummonOf(hunterKit, SummonKind.Pet);
        hunterKit.Creatures.KillCreature(hunterPet);
        Assert.Equal(3_600_000u, hunterPet.CorpseDecayMs);
    }

    [Fact]
    public void APetReturningOnAFollowOrder_IsNotArrivedWhileItCannotMove()
    {
        // vmangos FollowMovementGenerator::Update returns before MovementInform while the pet casts (IsNoMovementSpellCasted),
        // and PetAI::MovementInform(FOLLOW_MOTION_TYPE) is the only place that marks the pet as following (PetAI.cpp:684-693).
        SpellInfo castTime = Spell(PetCastSpell, Effect(SpellEffectName.Heal, 5)) with
        {
            CastTime = new SpellCastTime(3_000, 0, 3_000),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        using var kit = new PetTestKit([castTime], creatureSpells: true);
        (Player owner, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(owner, PetSpell);
        Creature pet = SummonOf(kit, SummonKind.Pet);
        kit.Run(3_000);
        CharmInfo charm = pet.Summon!.Charm!;
        Assert.True(charm.IsFollowing);

        owner.SetPosition(30, 5, owner.Z, 0);
        kit.Controller.HandleCommand(pet, CommandState.Follow, null);
        Assert.True(charm.IsReturning && charm.IsCommandFollow);
        Assert.Equal(SpellCastResult.CastOk, kit.Spells.System.CastSpell(pet, PetCastSpell, SpellCastTargets.ForSelf(), triggered: false));

        kit.Run(300);
        Assert.False(pet.IsMoving);
        Assert.False(charm.IsFollowing);
        Assert.True(charm.IsReturning);
        Assert.True(charm.IsCommandFollow);

        // once the cast is over it runs to its owner and only then follows
        kit.Spells.System.CancelCast(pet, 0);
        kit.Run(8_000);
        Assert.True(charm.IsFollowing);
        Assert.False(charm.IsReturning);
        float distance = MathF.Sqrt(((pet.X - owner.X) * (pet.X - owner.X)) + ((pet.Y - owner.Y) * (pet.Y - owner.Y)));
        Assert.True(distance < 6f, $"pet still {distance} yd from its owner");
    }
}
