using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Pets;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Bonus;
using ArcaneCore.Protocol;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Pets;

/// <summary>
/// The hunter pet happiness damage factor: 125% happy, 100% content, 75% unhappy (vmangos SpellCaster.cpp:1378-1390 for melee damage,
/// :1663-1675 for spell damage). Applied once, so a white swing is 125%, not the field-and-bonus product.
/// </summary>
public sealed class PetHappinessDamageTests
{
    private const uint BeastEntry = 5241;
    private const uint MeleeSpell = 963_001;
    private const uint MagicSpell = 963_002;

    private static PetTestKit Kit() => new(extraTemplates:
    [
        CreatureTestKit.Template(BeastEntry, b =>
        {
            b.MinLevel = 20;
            b.MaxLevel = 20;
            b.MinLevelHealth = 100;
            b.MaxLevelHealth = 100;
            b.Faction = 14;
        }) with { CreatureType = SummonService.CreatureTypeBeast, TypeFlags = SummonService.TypeFlagTameable, Family = 1 },
    ]);

    private static (Creature Pet, Player Victim) Tame(PetTestKit kit, uint happiness)
    {
        (Player hunter, _) = kit.AddPlayer(1);
        hunter.SetByte(UpdateFields.UnitFieldBytes0, 1, (byte)Class.Hunter);
        hunter.Level = 30;
        Creature beast = kit.Creatures.SpawnTemporary(kit.Content.FindTemplate(BeastEntry)!, 3, 0, 0, 0);
        Creature pet = Assert.IsType<Creature>(kit.Service.TameCreature(hunter, beast, 1515));
        pet.SetUInt32(UpdateFields.UnitFieldPower5, happiness);
        (Player victim, _) = kit.AddPlayer(2, 2, 0);
        return (pet, victim);
    }

    [Theory]
    [InlineData(0u, 75f)]
    [InlineData(350_000u, 100f)]
    [InlineData(700_000u, 125f)]
    public void MeleeDamageBonusDone_ScalesAHunterPetsSwingByHappiness(uint happiness, float expected)
    {
        using PetTestKit kit = Kit();
        (Creature pet, Player victim) = Tame(kit, happiness);

        Assert.Equal(expected, MeleeDamageBonus.Done(kit.Spells.System, pet, victim, 100f, WeaponAttackType.BaseAttack), 3);
    }

    [Theory]
    [InlineData(0u, 75f)]
    [InlineData(700_000u, 125f)]
    public void SpellBonusDone_ScalesAHunterPetsMeleeClassAndMagicSpellsByHappiness(uint happiness, float expected)
    {
        using PetTestKit kit = Kit();
        (Creature pet, Player victim) = Tame(kit, happiness);
        SpellBonusModule bonus = new(kit.Spells.System);
        SpellInfo melee = Spell(MeleeSpell, Effect(SpellEffectName.SchoolDamage, 100)) with { DamageClass = SpellDamageClass.Melee };
        SpellInfo magic = Spell(MagicSpell, Effect(SpellEffectName.SchoolDamage, 100)) with { DamageClass = SpellDamageClass.Magic };

        Assert.Equal(expected, bonus.Modify(SpellAmountStage.DirectDamage, pet, victim, melee, 0, 100f, 1), 3);
        Assert.Equal(expected, bonus.Modify(SpellAmountStage.DamageOverTimeSnapshot, pet, victim, magic, 0, 100f, 1), 3);
    }

    [Fact]
    public void HappinessDamageFactor_IsOneForAPetWithoutCharmInfo()
    {
        using PetTestKit kit = Kit();
        (Creature pet, _) = Tame(kit, 700_000u);
        Assert.Equal(1.25f, SummonService.HappinessDamageFactor(pet));

        pet.Summon!.Charm = null;

        Assert.Equal(1.0f, SummonService.HappinessDamageFactor(pet));
    }
}
