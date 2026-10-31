using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Spells.Rules.Immunity;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.CreatureTestKit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Creature-type immunities (vmangos Creature::Create, Creature.cpp:425-441): Mechanical creatures are immune to the Heal and HealMaxHealth
/// effects, the PeriodicHeal and PeriodicLeech aura states and the Disease and Poison dispel types; Elemental creatures to the two dispel types only.
/// </summary>
public sealed class CreatureTypeImmunityTests
{
    private const uint Beast = 1;
    private const uint Elemental = 4;
    private const uint Mechanical = 9;

    private const uint PoisonSpell = 932_101;
    private const uint DiseaseSpell = 932_102;
    private const uint HealSpell = 932_103;
    private const uint MaxHealSpell = 932_104;
    private const uint HotSpell = 932_105;
    private const uint LeechSpell = 932_106;

    private static SpellInfo Hostile(uint id, params SpellEffectInfo[] effects) =>
        Spell(id, effects) with
        {
            School = SpellSchool.Nature,
            DamageClass = SpellDamageClass.Magic,
            RangeIndex = 4,
            Range = new SpellRange(0, 30),
            Duration = new SpellDuration(30_000, 0, 30_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellEffectInfo Aura(AuraType type) => Effect(SpellEffectName.ApplyAura, 0, SpellImplicitTarget.UnitEnemy, type);

    private static SpellTestKit Kit() => new(
        Hostile(PoisonSpell, Aura(AuraType.Dummy)) with { Dispel = (uint)DispelType.Poison },
        Hostile(DiseaseSpell, Aura(AuraType.Dummy)) with { Dispel = (uint)DispelType.Disease },
        Hostile(HealSpell, Effect(SpellEffectName.Heal, 100, SpellImplicitTarget.UnitEnemy)),
        Hostile(MaxHealSpell, Effect(SpellEffectName.HealMaxHealth, 100, SpellImplicitTarget.UnitEnemy)),
        Hostile(HotSpell, Aura(AuraType.PeriodicHeal)),
        Hostile(LeechSpell, Aura(AuraType.PeriodicLeech)));

    private static Creature Make(uint creatureType)
    {
        const uint entry = 932_001;
        CreatureTemplate template = Template(entry) with { CreatureType = creatureType };
        CreatureSpawn spawn = Spawn(1, entry, 0, 0);
        var content = new CreatureContent([template], [spawn], [], [], []);
        return new Creature(1, template, spawn, content, new Random(1));
    }

    private static bool SpellImmune(SpellTestKit kit, Creature c, uint spell) =>
        ImmunityRules.IsImmuneToSpell(kit.System, c, kit.Store.Get(spell)!, castOnSelf: false);

    private static bool EffectImmune(SpellTestKit kit, Creature c, uint spell) =>
        ImmunityRules.IsImmuneToSpellEffect(kit.System, c, kit.Store.Get(spell)!, 0, castOnSelf: false);

    [Theory]
    [InlineData(Elemental)]
    [InlineData(Mechanical)]
    public void PoisonAndDiseaseSpells_MissElementalsAndMechanicals(uint type)
    {
        using SpellTestKit kit = Kit();
        Creature c = Make(type);

        Assert.True(SpellImmune(kit, c, PoisonSpell));
        Assert.True(SpellImmune(kit, c, DiseaseSpell));
    }

    [Fact]
    public void PoisonAndDiseaseSpells_LandOnABeast()
    {
        using SpellTestKit kit = Kit();
        Creature c = Make(Beast);

        Assert.False(SpellImmune(kit, c, PoisonSpell));
        Assert.False(SpellImmune(kit, c, DiseaseSpell));
    }

    [Fact]
    public void Heals_MissAMechanical_ButLandOnAnElementalAndABeast()
    {
        using SpellTestKit kit = Kit();

        Creature mech = Make(Mechanical);
        Assert.True(EffectImmune(kit, mech, HealSpell));
        Assert.True(EffectImmune(kit, mech, MaxHealSpell));

        foreach (uint type in new[] { Elemental, Beast })
        {
            Creature c = Make(type);
            Assert.False(EffectImmune(kit, c, HealSpell));
            Assert.False(EffectImmune(kit, c, MaxHealSpell));
        }
    }

    [Fact]
    public void PeriodicHealAndLeech_AreRefusedOnAMechanical_Only()
    {
        using SpellTestKit kit = Kit();

        Creature mech = Make(Mechanical);
        Assert.True(EffectImmune(kit, mech, HotSpell));
        Assert.True(EffectImmune(kit, mech, LeechSpell));

        foreach (uint type in new[] { Elemental, Beast })
        {
            Creature c = Make(type);
            Assert.False(EffectImmune(kit, c, HotSpell));
            Assert.False(EffectImmune(kit, c, LeechSpell));
        }
    }
}
