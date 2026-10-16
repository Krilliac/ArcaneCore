using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Pets.PetTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// vmangos Unit::Update combat timer (Objects/Unit.cpp:256-287) with Unit::UsesPvPCombatTimer (:368-385): a player's pet holds no threat list,
/// so the 1.2 s combat check is what ends its combat once no creature holds it on a threat list. Live: player-bot pets
/// (combat/combat-never-left on Pet GUIDs) stayed flagged in combat for minutes after their fight ended.
/// </summary>
public sealed class PetCombatTimerTests
{
    private static (PetTestKit Kit, Player Owner, Creature Pet) Summoned()
    {
        var kit = new PetTestKit();
        (Player owner, _) = kit.AddPlayer(1, 5, 5);
        kit.Cast(owner, PetSpell);
        Creature pet = Assert.Single(kit.Creatures.Creatures);
        return (kit, owner, pet);
    }

    private static Creature Enemy(PetTestKit kit, float x, float y)
        => kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(NpcCasterEntry)!, x, y, 83.5f, 0);

    [Fact]
    public void APetWhoseVictimDied_LeavesCombatOnTheCombatTimer()
    {
        (PetTestKit kit, _, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 8, 5);
            kit.Controller.HandleCommand(pet, CommandState.Attack, enemy);
            Assert.Same(enemy, pet.Combat.Victim);
            Assert.True(pet.Combat.IsInCombat);

            kit.Map.Combat.Kill(pet, enemy);
            Assert.False(enemy.IsAlive);
            Assert.Empty(pet.Combat.ThreatenedBy);

            kit.Run(3_000); // two 1.2 s combat checks
            Assert.Null(pet.Combat.Victim);
            Assert.False(pet.Combat.IsInCombat);
            Assert.True(MapCombat.UsesPvpCombatTimer(pet));
        }
    }

    [Fact]
    public void APetPutInCombatWithNothingToFight_LeavesCombatOnTheCombatTimer()
    {
        (PetTestKit kit, _, Creature pet) = Summoned();
        using (kit)
        {
            // An imp buffing its fighting owner (SpellSystem.Threat: a positive spell on a unit in combat puts the caster in combat) — the
            // live combat/combat-never-left reports were on such pets (entry 416, the imp).
            kit.Map.Combat.SetInCombatState(pet, 0);
            Assert.True(pet.Combat.IsInCombat);

            kit.Run(3_000);
            Assert.False(pet.Combat.IsInCombat);
        }
    }

    [Fact]
    public void APetCalledBackWhoseTargetTheOwnerKilled_LeavesCombatOnTheCombatTimer()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            Creature enemy = Enemy(kit, 8, 5);
            kit.Controller.HandleCommand(pet, CommandState.Attack, enemy);
            Assert.True(pet.Combat.IsInCombat);
            kit.Controller.HandleCommand(pet, CommandState.Follow, null);
            kit.Map.Combat.Kill(owner, enemy);

            kit.Run(3_000);
            Assert.Null(pet.Combat.Victim);
            Assert.False(pet.Combat.IsInCombat);
        }
    }

    [Fact]
    public void APetHeldOnALivingCreaturesThreatList_StaysInCombat_AndKeepsItsOwnerInCombat()
    {
        (PetTestKit kit, Player owner, Creature pet) = Summoned();
        using (kit)
        {
            pet.Summon!.Charm!.ReactState = ReactState.Passive; // the pet does not fight back, so the creature keeps it on its list
            pet.MaxHealth = 1_000_000;
            pet.Health = 1_000_000;
            Creature enemy = Enemy(kit, 8, 5);
            Assert.True(kit.Creatures.AttackStart(enemy, pet));
            kit.Map.Combat.SetInCombatState(owner, 0);
            Assert.Contains(enemy, pet.Combat.ThreatenedBy);

            kit.Run(3_000);
            Assert.True(enemy.IsAlive);
            Assert.True(pet.Combat.IsInCombat);
            Assert.True(owner.Combat.IsInCombat); // vmangos Unit.cpp:264-266: the owner waits for its pet
        }
    }
}
