using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Kernel.Characters.Pets;
using ArcaneCore.Kernel.WorldData.Pets;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// vmangos Pet::InitStatsForLevel reads pet_levelstats with creatureId = HUNTER_PET ? 1 : cinfo->entry (Pet.cpp:1289). Unproven until the integration gate.
/// </summary>
public sealed class HunterPetStatsRowTests
{
    private const uint CallSpell = 991201;
    private const uint SummonImpSpell = 688;
    private const uint FireboltRank1 = 3110;

    private static PetLevelStats StatsRow(uint entry, uint health)
        => new(entry, 1, health, 0, 0, 0, 0, 1, 1, 1, 1, 1);

    [Theory]
    [InlineData(true, 700u)]   // shared row present: creature id 1 row
    [InlineData(false, 300u)]  // no shared row: own entry
    public async Task HunterPetStats_UseCreatureId1WhenPresent_AndTheOwnEntryOtherwise(bool withSharedRow, uint expectedHealth)
    {
        PetLevelStats[] rows = withSharedRow ? [StatsRow(1, 700), StatsRow(PetTestKit.PetEntry, 300)] : [StatsRow(PetTestKit.PetEntry, 300)];
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))], new PetContent(rows, []));
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);

        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 804, PetTestKit.PetEntry, 1, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        kit.Cast(owner, CallSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(expectedHealth, pet.MaxHealth);
    }

    [Fact]
    public async Task HunterPet_TakesNoManaFromTheCreatureId1Row_AndItsDamageIsTheLevelFormula()
    {
        const uint rowMana = 77;
        PetLevelStats shared = new(1, 1, 700, rowMana, 9, 40, 50, 1, 2, 3, 4, 5);
        PetLevelStats own = new(PetTestKit.PetEntry, 1, 300, 5, 0, 0, 0, 1, 1, 1, 1, 1);
        using PetTestKit kit = new([Spell(CallSpell, Effect(SpellEffectName.SummonPet, 0, misc: 0))], new PetContent([shared, own], []));
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        kit.Service.LoadPersistence = (_, _) => Task.FromResult<PersistentPetSnapshot?>(
            new(1, 804, PetTestKit.PetEntry, 1, 0, 50, 0, 0, 1, [], []));
        await kit.Service.ReadCurrentPetAsync(owner);

        kit.Cast(owner, CallSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures);
        Assert.Equal(700u, pet.MaxHealth);
        Assert.Equal(9u, pet.GetUInt32(UpdateFields.UnitFieldResistances));
        Assert.NotEqual(rowMana, pet.GetUInt32(UpdateFields.UnitFieldBaseMana));
        Assert.NotEqual(rowMana, pet.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        Assert.NotEqual(rowMana, pet.GetUInt32(UpdateFields.UnitFieldPower1));
        Assert.Equal(PowerType.Focus, pet.PowerType);

        // Pet.cpp:1409-1414: the row's damage (40..50) is ignored for a hunter pet; level * 1.15 * 1.05 * attackTime / 2000 and 1.45.
        float attackTime = pet.GetUInt32(UpdateFields.UnitFieldBaseattacktime);
        Assert.Equal((float)(pet.Level * 1.15 * 1.05 * attackTime / 2000), pet.GetFloat(UpdateFields.UnitFieldMindamage), 3);
        Assert.Equal((float)(pet.Level * 1.45 * 1.05 * attackTime / 2000), pet.GetFloat(UpdateFields.UnitFieldMaxdamage), 3);
    }

    [Fact]
    public void AHuntersSummonEffectPetWithAnExplicitEntry_IsNotAHunterPet()
    {
        PetContent content = new([StatsRow(1, 700), StatsRow(PetTestKit.ImpEntry, 300)], [new PetCreateSpells(PetTestKit.ImpEntry, [FireboltRank1])]);
        IEnumerable<SpellInfo> spells =
        [
            Spell(SummonImpSpell, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)PetTestKit.ImpEntry)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(FireboltRank1, Effect(SpellEffectName.SchoolDamage, 8, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
        ];
        using PetTestKit kit = new(spells, content);
        kit.Service.InstallDemons(kit.Spells.System);
        (Player owner, _) = kit.AddPlayer(1);
        owner.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        owner.Level = 10;

        kit.Cast(owner, SummonImpSpell);

        Creature pet = Assert.Single(kit.Creatures.Creatures, c => c.IsPet);
        Assert.False(PetPower.IsHunterPet(pet));
        Assert.Equal(300u, pet.MaxHealth); // its own pet_levelstats row, not the hunter rows of creature id 1
    }

    [Fact]
    public void AWarlockPet_IgnoresTheHunterRowsOfCreatureId1()
    {
        PetContent content = new([StatsRow(1, 700), StatsRow(PetTestKit.ImpEntry, 300)], [new PetCreateSpells(PetTestKit.ImpEntry, [FireboltRank1])]);
        IEnumerable<SpellInfo> spells =
        [
            Spell(SummonImpSpell, Effect(SpellEffectName.SummonPet, 0, (SpellImplicitTarget)32, misc: (int)PetTestKit.ImpEntry)) with { StartRecoveryCategory = 0, StartRecoveryTime = 0 },
            Spell(FireboltRank1, Effect(SpellEffectName.SchoolDamage, 8, SpellImplicitTarget.UnitEnemy)) with { RangeIndex = 4, Range = new SpellRange(0, 30) },
        ];
        using PetTestKit kit = new(spells, content);
        kit.Service.InstallDemons(kit.Spells.System);
        (Player owner, _) = kit.AddPlayer(1);
        owner.Level = 10;

        kit.Cast(owner, SummonImpSpell);

        Assert.Equal(300u, Assert.Single(kit.Creatures.Creatures, c => c.IsPet).MaxHealth);
    }
}
