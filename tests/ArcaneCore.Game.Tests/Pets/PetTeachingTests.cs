using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Kernel.WorldData.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>Hunters learning abilities from their pets (vmangos AddTeachSpell in Pet::InitPetCreateSpells, Pet::CheckLearning).</summary>
public sealed class PetTeachingTests
{
    private const uint BeastEntry = 5251;
    private const uint LearnBite = 924001;
    private const uint Bite = 924002;

    private static PetTestKit Kit() => new(
        extraSpells:
        [
            Spell(LearnBite, Effect(SpellEffectName.LearnPetSpell, 0, trigger: Bite)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(Bite, Effect(SpellEffectName.Dummy, 1)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
        ],
        petContent: new PetContent([], [new PetCreateSpells(BeastEntry, [LearnBite])]),
        extraTemplates:
        [
            CreatureTestSupport.Template(BeastEntry, b =>
            {
                b.MinLevel = 20;
                b.MaxLevel = 20;
                b.MinLevelHealth = 100;
                b.MaxLevelHealth = 100;
                b.Faction = 14;
            }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
        ]);

    private static (Player Hunter, Creature Pet) Tame(PetTestKit kit)
    {
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        return (hunter, Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515)));
    }

    [Fact]
    public void PetUsingItsAbility_TeachesTheOwner_OnASuccessfulRoll_Once()
    {
        using PetTestKit kit = Kit();
        (Player hunter, Creature pet) = Tame(kit);
        Assert.True(pet.Summon!.Charm!.HasSpell(Bite));

        kit.Service.TeachRoll = () => false;
        kit.Cast(pet, Bite);
        Assert.False(kit.Spells.Spellbook.HasSpell(hunter, LearnBite));

        kit.Service.TeachRoll = () => true;
        kit.Cast(pet, Bite);
        Assert.True(kit.Spells.Spellbook.HasSpell(hunter, LearnBite));
        Assert.Empty(pet.Summon.Charm.TeachSpells);
    }

    [Fact]
    public void AnOwnerWhoKnowsTheTeachingSpell_GetsNoTeachSpell()
    {
        using PetTestKit kit = Kit();
        (Player hunter, _) = kit.AddPlayer(1);
        kit.Spells.Spellbook.LearnSpell(hunter, LearnBite);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515));
        Assert.Empty(pet.Summon!.Charm!.TeachSpells);
    }
}
