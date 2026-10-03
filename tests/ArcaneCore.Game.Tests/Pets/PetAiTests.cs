using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>vmangos PetAI (AI/PetAI.cpp) on the pets, guardians and mini pets.</summary>
public sealed class PetAiTests
{
    private static (PetTestKit Kit, Player Owner, FakeSession Session, Creature Pet) Summoned()
    {
        var kit = new PetTestKit();
        (Player owner, FakeSession session) = kit.AddPlayer(1, 5, 5);
        kit.Cast(owner, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        return (kit, owner, session, pet);
    }

    private static Creature Enemy(PetTestKit kit, float x, float y)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, x, y, 83.5f, 0);

    private static PetAI Ai(Creature pet) => Assert.IsType<PetAI>(pet.AI);

    [Fact]
    public void PetsGuardiansAndMiniPetsRunPetAI_TotemsDoNothing_WildSummonsKeepTheirOwnAI()
    {
        using var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1);
        kit.Cast(owner, PetSpell);
        kit.Cast(owner, GuardianSpell);
        kit.Cast(owner, CritterSpell);
        kit.Cast(owner, FireTotemSpell);
        kit.Cast(owner, WildSpell);

        foreach (Creature summon in kit.Creatures.Creatures)
        {
            // A totem is the shaman lane's TotemSystem creature: it has no pet summon record.
            if (summon.IsTotem)
            {
                Assert.IsNotType<PetAI>(summon.AI);
                continue;
            }

            switch (summon.Summon!.Kind)
            {
                case SummonKind.Pet or SummonKind.Guardian or SummonKind.MiniPet:
                    Assert.IsType<PetAI>(summon.AI);
                    break;
                default:
                    Assert.IsNotType<PetAI>(summon.AI);
                    break;
            }
        }
    }

    [Fact]
    public void TheImpHasNoMeleeAttack_OtherPetsDo()
    {
        using var imp = new PetTestKit();
        (Player warlock, _) = imp.AddPlayer(1);
        imp.Cast(warlock, ImpSpell);
        Creature impPet = Assert.Single(imp.Creatures.Creatures);
        Assert.Equal(PetAI.ImpEntry, impPet.Entry);
        Assert.False(Ai(impPet).MeleeEnabled);

        (PetTestKit kit, _, _, Creature pet) = Summoned();
        using (kit)
        {
            Assert.True(Ai(pet).MeleeEnabled);
        }
    }

    [Fact]
    public void ANewPetFollowsItsOwner()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            kit.Run(200);
            Assert.Equal(MovementGeneratorType.Follow, pet.Motion.CurrentType);
            Assert.Same(owner, pet.Motion.TargetedUnit);

            kit.Run(3_000);
            float follow = owner.BoundingRadius + pet.BoundingRadius + PetConstants.FollowDistance;
            float distance = MathF.Sqrt(((pet.X - owner.X) * (pet.X - owner.X)) + ((pet.Y - owner.Y) * (pet.Y - owner.Y)));
            Assert.InRange(distance, follow - 1.1f, follow + 1.1f);
            Assert.True(pet.Summon!.Charm!.IsFollowing);
            Assert.False(pet.Summon.Charm.IsReturning);
        }
    }

    [Fact]
    public void ADefensivePetDefendsItsOwner_APassivePetDoesNot()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 7, 5);
            kit.Map.Combat.Attack(enemy, owner);
            kit.Map.Combat.DealDamage(enemy, owner, 1, direct: true, meleeDamage: true);

            // OwnerAttackedBy (PetAI.cpp:383-414)
            Assert.Same(enemy, pet.Combat.Victim);
            Assert.Equal(MovementGeneratorType.Chase, pet.Motion.CurrentType);
            Assert.False(pet.Summon!.Charm!.IsReturning);
        }

        (PetTestKit kit2, Player owner2, _, Creature pet2) = Summoned();
        using (kit2)
        {
            pet2.Summon!.Charm!.ReactState = ReactState.Passive;
            Creature enemy = Enemy(kit2, 7, 5);
            kit2.Map.Combat.DealDamage(enemy, owner2, 1, direct: true, meleeDamage: true);
            Assert.Null(pet2.Combat.Victim);
        }
    }

    [Fact]
    public void APetHitByAnEnemyFightsBack_UnlessPassive()
    {
        (PetTestKit kit, _, _, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 7, 5);
            kit.Map.Combat.DealDamage(enemy, pet, 1, direct: true, meleeDamage: true);
            Assert.Same(enemy, pet.Combat.Victim);
        }

        (PetTestKit kit2, _, _, Creature pet2) = Summoned();
        using (kit2)
        {
            pet2.Summon!.Charm!.ReactState = ReactState.Passive;
            Creature enemy = Enemy(kit2, 7, 5);
            kit2.Map.Combat.DealDamage(enemy, pet2, 1, direct: true, meleeDamage: true);
            Assert.Null(pet2.Combat.Victim);
        }
    }

    [Fact]
    public void APassivePetAttacksOnlyWhenCommanded()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            charm.ReactState = ReactState.Passive;
            Creature enemy = Enemy(kit, 8, 5);

            Assert.False(Ai(pet).AttackTarget(enemy));
            Assert.Null(pet.Combat.Victim);

            kit.Controller.HandleCommand(pet, CommandState.Attack, enemy);
            Assert.Same(enemy, pet.Combat.Victim);
            Assert.True(charm.IsCommandAttack);
            Assert.Equal(MovementGeneratorType.Chase, pet.Motion.CurrentType);

            // the fight goes on while the target is valid
            kit.Run(300);
            Assert.Same(enemy, pet.Combat.Victim);
        }
    }

    [Fact]
    public void AfterAKill_ThePetTakesTheNextTargetOfItsOwner_ElseGoesBackToFollowing()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            Creature first = Enemy(kit, 8, 5);
            kit.Controller.HandleCommand(pet, CommandState.Attack, first);
            Assert.Same(first, pet.Combat.Victim);

            // the owner is fighting someone else (PetAI.cpp:520-560 SelectNextTarget)
            Creature second = Enemy(kit, 9, 6);
            kit.Map.Combat.Attack(owner, second);
            kit.Map.Combat.SetInCombatState(owner, 0);
            kit.Map.Combat.SetInCombatState(second, 0);

            kit.Map.Combat.Kill(pet, first);
            Assert.Same(second, pet.Combat.Victim);
            Assert.True(charm.IsCommandAttack); // DoAttack keeps the command flag across targets (PetAI.cpp:699-705)
            Assert.Equal(MovementGeneratorType.Chase, pet.Motion.CurrentType);

            // and when nothing is left it returns to its owner
            kit.Map.Combat.Kill(pet, second);
            kit.Run(300);
            Assert.Null(pet.Combat.Victim);
            Assert.Equal(MovementGeneratorType.Follow, pet.Motion.CurrentType);
            Assert.True(charm.IsReturning || charm.IsFollowing);
        }
    }

    [Fact]
    public void AStayingPetFightsOnlyWhatIsInReachOrWhatItIsToldTo_AndReturnsToItsPlace()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            kit.Run(200);
            kit.Controller.HandleCommand(pet, CommandState.Stay, null);
            (float stayX, float stayY, _) = charm.StayPosition;

            // out of reach: not attacked, even when the owner is hit (PetAI.cpp:792-796)
            Creature far = Enemy(kit, 20, 5);
            Assert.False(Ai(pet).CanAttack(far, charm));
            kit.Map.Combat.DealDamage(far, owner, 1, direct: true, meleeDamage: true);
            Assert.Null(pet.Combat.Victim);

            // commanded: it goes, and after the kill walks back to where it was told to stay
            kit.Controller.HandleCommand(pet, CommandState.Attack, far);
            Assert.Same(far, pet.Combat.Victim);
            kit.Run(1_000);
            kit.Map.Combat.Kill(pet, far);
            kit.Run(8_000);
            Assert.True(charm.IsAtStay);
            Assert.Equal(CommandState.Stay, charm.CommandState);
            Assert.InRange(MathF.Abs(pet.X - stayX) + MathF.Abs(pet.Y - stayY), 0f, 1.5f);
        }
    }

    [Fact]
    public void FollowCommand_BreaksOffTheFight()
    {
        (PetTestKit kit, Player owner, _, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 8, 5);
            kit.Controller.HandleCommand(pet, CommandState.Attack, enemy);
            Assert.Same(enemy, pet.Combat.Victim);

            kit.Controller.HandleCommand(pet, CommandState.Follow, null);
            Assert.Null(pet.Combat.Victim);
            Assert.Equal(MovementGeneratorType.Follow, pet.Motion.CurrentType);

            // while returning on follow the pet ignores new targets (PetAI.cpp:780-783)
            Assert.False(Ai(pet).CanAttack(Enemy(kit, 6, 5), pet.Summon!.Charm!));
        }
    }

    [Fact]
    public void ADisabledPetStopsAttackingAndRefusesTargets()
    {
        (PetTestKit kit, _, _, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 8, 5);
            kit.Controller.HandleCommand(pet, CommandState.Attack, enemy);
            Assert.Same(enemy, pet.Combat.Victim);

            kit.Controller.SetEnabled(pet, false);
            kit.Run(200); // _needToStop: "Stop attacking when player is mounted"
            Assert.Null(pet.Combat.Victim);
            Assert.False(Ai(pet).AttackTarget(enemy));
        }
    }

    [Fact]
    public void Autocast_CastsAHarmfulSpellAtTheVictimWhileFighting_AndTellsTheOwner()
    {
        (PetTestKit kit, Player owner, FakeSession session, Creature pet) = Summoned();
        using (kit)
        {
            CharmInfo charm = pet.Summon!.Charm!;
            charm.LearnSpell(PetBiteSpell, autocast: true);
            Creature enemy = Enemy(kit, 7, 5);
            int bites = 0;
            kit.Spells.System.SpellHit += (caster, target, spell) =>
            {
                if (spell.Id == PetBiteSpell && ReferenceEquals(caster, pet) && ReferenceEquals(target, enemy))
                {
                    bites++;
                }
            };

            // no fight, no autocast
            kit.Run(300);
            Assert.Equal(0, bites);

            kit.Controller.HandleCommand(pet, CommandState.Attack, enemy);
            session.Clear();
            kit.Run(200);

            // the bite goes at the victim and the owner hears the growl or the talk
            Assert.True(bites >= 1);
            Assert.True(Packets(session, WorldOpcode.SmsgAiReaction).Count + Packets(session, WorldOpcode.SmsgPetActionSound).Count >= 1);

            // turned off, nothing more is cast
            charm.SetSpellAutocast(PetBiteSpell, false);
            int before = bites;
            kit.Spells.Advance(5_000);
            kit.Run(500);
            Assert.Equal(before, bites);

            // and a spell that is not harmful is never autocast at the victim
            charm.LearnSpell(PetShieldSpell, autocast: true);
            kit.Spells.Advance(5_000);
            kit.Run(500);
            Assert.Equal(before, bites);
        }
    }

    private static List<byte[]> Packets(FakeSession session, WorldOpcode opcode) => Spells.SpellTestKit.Packets(session, opcode);
}
