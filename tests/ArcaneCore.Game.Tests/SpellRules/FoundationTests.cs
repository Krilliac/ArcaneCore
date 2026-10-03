using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Kernel.WorldData.Creatures;
using ArcaneCore.Game.Tests.Spells;
using Xunit;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>Facts of the spell-rule foundation: mechanic/dispel ids and masks, SpellInfo views, boss-relative levels, option defaults.</summary>
public sealed class FoundationTests
{
    [Theory]
    [InlineData(SpellMechanic.Charm, 1u)]
    [InlineData(SpellMechanic.Disoriented, 2u)]
    [InlineData(SpellMechanic.Disarm, 3u)]
    [InlineData(SpellMechanic.Fear, 5u)]
    [InlineData(SpellMechanic.Root, 7u)]
    [InlineData(SpellMechanic.Pacify, 8u)]
    [InlineData(SpellMechanic.Silence, 9u)]
    [InlineData(SpellMechanic.Sleep, 10u)]
    [InlineData(SpellMechanic.Snare, 11u)]
    [InlineData(SpellMechanic.Stun, 12u)]
    [InlineData(SpellMechanic.Freeze, 13u)]
    [InlineData(SpellMechanic.Knockout, 14u)]
    [InlineData(SpellMechanic.Polymorph, 17u)]
    [InlineData(SpellMechanic.Banish, 18u)]
    [InlineData(SpellMechanic.Horror, 24u)]
    [InlineData(SpellMechanic.Interrupt, 26u)]
    [InlineData(SpellMechanic.ImmuneShield, 29u)]
    [InlineData(SpellMechanic.Sapped, 30u)]
    [InlineData(SpellMechanic.SlowCastSpeed, 31u)]
    public void MechanicIds_MatchVmangosSpellDefines(SpellMechanic mechanic, uint id)
    {
        Assert.Equal(id, (uint)mechanic);
        Assert.Equal(1u << (int)(id - 1), SpellMechanics.Mask(mechanic));
    }

    [Fact]
    public void MechanicMasks_IgnoreNoneAndOutOfRange_AndCompositeMasksMatchVmangos()
    {
        Assert.Equal(0u, SpellMechanics.Mask(0u));
        Assert.Equal(0u, SpellMechanics.Mask(32u));
        Assert.Equal((1u << 6) | (1u << 10), SpellMechanics.RootAndSnareMask);
        Assert.Equal((1u << 6) | (1u << 11), SpellMechanics.RootAndStunMask);
        Assert.Equal((1u << 1) | (1u << 16), SpellMechanics.ConfusedMask);
    }

    [Fact]
    public void DispelMask_ExpandsAllToMagicCurseDiseasePoison_AndOtherTypesToTheirOwnBit()
    {
        Assert.Equal(0b11110u, DispelTypes.GetDispelMask(DispelType.All));
        Assert.Equal(DispelTypes.AllMask, DispelTypes.GetDispelMask(7u));
        Assert.Equal(0b10u, DispelTypes.GetDispelMask(DispelType.Magic));
        Assert.Equal(1u << 9, DispelTypes.GetDispelMask(DispelType.Enrage));
        Assert.Equal(0u, DispelTypes.GetDispelMask(40u));
    }

    [Fact]
    public void AllMechanicMask_CombinesSpellAndEffectMechanics_EvenOnEmptyEffectSlots()
    {
        SpellInfo spell = SpellTestKit.Spell(1, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5)) with { Mechanic = (uint)SpellMechanic.Stun };
        spell = spell with
        {
            Effects =
            [
                spell.Effects[0],
                new SpellEffectInfo { Mechanic = (uint)SpellMechanic.Root },
            ],
        };

        Assert.Equal(SpellMechanics.Mask(SpellMechanic.Stun) | SpellMechanics.Mask(SpellMechanic.Root), spell.AllMechanicMask());
        Assert.True(spell.HasMechanic(SpellMechanic.Root));
        Assert.False(spell.HasMechanic(SpellMechanic.Fear));
        Assert.Equal((uint)SpellMechanic.Stun, spell.EffectMechanic(0)); // falls back to the spell mechanic
        Assert.Equal((uint)SpellMechanic.Root, spell.EffectMechanic(1));
    }

    [Fact]
    public void AreaEffect_FollowsVmangosAreaTargets_AndSchoolMaskIsABit()
    {
        SpellInfo single = SpellTestKit.Spell(1, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy));
        SpellInfo aoe = SpellTestKit.Spell(2, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.UnitEnemy, targetB: SpellImplicitTarget.EnumUnitsEnemyAoeAtDestLoc));
        SpellInfo cone54 = SpellTestKit.Spell(3, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.EnumUnitsEnemyInCone54));
        SpellInfo cone24 = SpellTestKit.Spell(4, SpellTestKit.Effect(SpellEffectName.SchoolDamage, 5, SpellImplicitTarget.EnumUnitsEnemyInCone24));

        Assert.False(single.IsAreaEffect());
        Assert.True(aoe.IsAreaEffect());
        Assert.False(cone54.IsAreaEffect());
        Assert.True(cone24.IsAreaEffect());
        Assert.Equal(1u << 4, (single with { School = SpellSchool.Frost }).SchoolMask());
        Assert.Equal(SpellSchool.Fire, SpellSchoolMasks.FirstSchoolIn(0b1100));
        Assert.Equal(SpellSchool.Normal, SpellSchoolMasks.FirstSchoolIn(0));
    }

    [Fact]
    public void AttributeBits_ReadTheRawColumns()
    {
        SpellInfo spell = SpellTestKit.Spell(1);
        Assert.False(spell.IsAlwaysHit());
        Assert.True((spell with { AttributesEx3 = 0x40000 }).IsAlwaysHit());
        Assert.True((spell with { AttributesEx4 = 1 }).IgnoresResistances());
        Assert.True((spell with { Attributes = (SpellAttributes)0x20000000 }).IgnoresImmunities());
        Assert.False(spell.IgnoresImmunities());
    }

    [Fact]
    public void WorldBoss_CountsAsTheTargetsLevelPlusTheConfiguredDifference()
    {
        Creature boss = MakeCreature(rank: 3, level: 60);
        Creature elite = MakeCreature(rank: 1, level: 60);
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;

        Assert.True(boss.IsWorldBoss());
        Assert.False(elite.IsWorldBoss());
        Assert.Equal(63, boss.EffectiveLevelAgainst(player, 3));
        Assert.Equal(62, boss.EffectiveLevelAgainst(player, 2));
        Assert.Equal(60, elite.EffectiveLevelAgainst(player, 3));
        Assert.Equal(60, player.EffectiveLevelAgainst(boss, 3));
        player.Level = 255;
        Assert.Equal(255, boss.EffectiveLevelAgainst(player, 3)); // clamped
        Assert.Equal(60, boss.EffectiveLevelAgainst(null, 3));
    }

    [Fact]
    public void CreatureTypeMask_IsTheCreatureTypeBit_AndPlayersAreHumanoid()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        Creature beast = MakeCreature(rank: 0, level: 5); // test template: type 1 (beast)

        Assert.Equal(1u, beast.CreatureTypeMask());
        Assert.Equal(1u << 6, player.CreatureTypeMask());
        Assert.True(player.IsLikePlayer());
        Assert.False(beast.IsLikePlayer());
    }

    [Fact]
    public void Options_DefaultToRetail()
    {
        var options = new SpellRuleOptions();
        Assert.Equal(22.0f, options.MagicHitFloorPercent);
        Assert.Equal(3, options.WorldBossLevelDiff);
        Assert.False(options.CreatureSpellCrit);
        Assert.Equal("SpellRules", SpellRuleOptions.SectionName);
    }

    [Fact]
    public void NoModifiers_IsTheIdentity_AndFlatCritSourceOnlyGivesPlayersACrit()
    {
        using var kit = new SpellTestKit();
        (Player player, _) = kit.AddPlayer(1);
        Creature beast = MakeCreature(rank: 0, level: 5);
        SpellInfo spell = SpellTestKit.Spell(1);

        Assert.Equal(7.5f, ISpellModifiers.None.Apply(player, spell, SpellModOp.CriticalChance, 7.5f));
        ISpellCritSource flat = ISpellCritSource.Flat(5f);
        Assert.Equal(5f, flat.SpellCritPercent(player, SpellSchool.Fire));
        Assert.Equal(0f, flat.SpellCritPercent(beast, SpellSchool.Fire));
    }

    internal static Creature MakeCreature(uint rank, byte level)
    {
        CreatureTemplate template = CreatureTestSupport.Template(CreatureTestSupport.WolfEntry, b =>
        {
            b.Rank = rank;
            b.MinLevel = level;
            b.MaxLevel = level;
        });
        var creature = new Creature(1, template, null, CreatureTestSupport.Content([template], []), new Random(1));
        creature.Level = level;
        return creature;
    }
}
