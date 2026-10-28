using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Rules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.WorldData.Creatures;
using Xunit;
using static ArcaneCore.Game.Tests.ItemTestData;

namespace ArcaneCore.Game.Tests.SpellRules;

/// <summary>
/// Retail resistance rules: a resistance aura cannot push a school below 0 (Patch 1.9.0), a warlock cannot resist its own
/// Hellfire (Patch 1.9.0), the holy resistance field stays 0, and a creature is created with its template's magic resistances.
/// </summary>
public sealed class RetailResistFixesTests
{
    private const uint CurseOfElements = 900_301;
    private const int FireMask = 1 << (int)SpellSchool.Fire;

    private static (SpellTestKit Kit, Player Caster, Player Target) Cursed(int baseFireResistance)
    {
        var kit = new SpellTestKit(RuleTestKit.Grant(CurseOfElements, AuraType.ModResistance, -75, FireMask));
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        caster.Level = 60;
        target.Level = 60;
        target.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, baseFireResistance);
        RuleTestKit.Apply(kit, target, CurseOfElements);
        return (kit, caster, target);
    }

    private static Creature SpawnCreature(Func<CreatureTemplate, CreatureTemplate> configure)
    {
        CreatureTemplate template = configure(new CreatureTemplate
        {
            Entry = 990_301, Name = "Resistant", MinLevel = 60, MaxLevel = 60, MinLevelHealth = 10_000, MaxLevelHealth = 10_000, DisplayIds = [1], Faction = 35,
        });
        return new Creature(template.Entry, template, null, new CreatureContent([template], [], [], [], []), new Random(1));
    }

    [Fact]
    public void ACurseOnATargetWithNoResistance_GivesNoBonusDamage()
    {
        (SpellTestKit kit, Player caster, Player target) = Cursed(baseFireResistance: 0);
        using (kit)
        {
            Assert.Equal(-75, target.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire)); // the aura is on the field
            var rules = new VanillaSpellCombatRules();

            Assert.Equal(0f, rules.ResistChance(kit.System, caster, target, SpellSchool.Fire, innateResists: true));
            Assert.Equal(0, rules.RollResist(kit.System, caster, target, RuleTestKit.Magic(1, SpellSchool.Fire), 100, periodic: false)); // not -25 (extra damage)
        }
    }

    [Fact]
    public void ACurseOnATargetWithResistance_StillLowersIt()
    {
        (SpellTestKit kit, Player caster, Player target) = Cursed(baseFireResistance: 150);
        using (kit)
        {
            // 150 - 75 = 75: 75 * 0.15 / 60.
            Assert.Equal(0.1875f, new VanillaSpellCombatRules().ResistChance(kit.System, caster, target, SpellSchool.Fire, innateResists: true), 4);
        }
    }

    [Theory]
    [InlineData(-150, -0.5f)]
    [InlineData(-900, -0.75f)]
    public void ACreatureWithANegativeTemplateResistance_IsStillVulnerable(int templateResistance, float expected)
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        Creature mob = SpawnCreature(t => t with { FireRes = templateResistance });

        Assert.Equal(expected, VanillaSpellCombatRules.AverageResistFraction(caster, mob, SpellSchool.Fire), 3);
    }

    [Fact]
    public void ACreatureWithZeroTemplateResistance_CannotBeMadeVulnerableByTheField()
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        Creature mob = SpawnCreature(t => t);
        mob.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, -75); // a curse on the field

        Assert.Equal(0f, VanillaSpellCombatRules.AverageResistFraction(caster, mob, SpellSchool.Fire));
    }

    [Theory]
    [InlineData(1949u)]
    [InlineData(11683u)]
    [InlineData(11684u)]
    public void AWarlock_NeverResistsItsOwnHellfire(uint hellfireTick)
    {
        using var kit = new SpellTestKit();
        (Player warlock, _) = kit.AddPlayer(1);
        warlock.Level = 60;
        warlock.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 300);
        kit.System.Random = new Random(7);
        var rules = new VanillaSpellCombatRules();
        SpellInfo self = RuleTestKit.Magic(hellfireTick, SpellSchool.Fire);

        Assert.Equal(0, rules.RollResist(kit.System, warlock, warlock, self, 100, periodic: false));
        int resisted = 0;
        for (int tick = 0; tick < 200; tick++)
        {
            resisted += rules.RollResist(kit.System, warlock, warlock, self, 100, periodic: true);
        }

        Assert.Equal(0, resisted);
    }

    [Fact]
    public void HellfireEffect_OnAVictim_IsResistedNormally_AndOtherSelfDamageStillIs()
    {
        using var kit = new SpellTestKit();
        (Player warlock, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2);
        warlock.Level = 60;
        victim.Level = 60;
        warlock.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 300);
        victim.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 300);
        var rules = new VanillaSpellCombatRules();

        Assert.Equal(75, rules.RollResist(kit.System, warlock, victim, RuleTestKit.Magic(11682, SpellSchool.Fire), 100, periodic: false)); // Hellfire Effect on a victim
        Assert.Equal(75, rules.RollResist(kit.System, warlock, victim, RuleTestKit.Magic(1949, SpellSchool.Fire), 100, periodic: false)); // the tick id on another unit
        Assert.Equal(75, rules.RollResist(kit.System, warlock, warlock, RuleTestKit.Magic(5857, SpellSchool.Fire), 100, periodic: false)); // any other spell on self
    }

    [Fact]
    public void ItemHolyResistance_DoesNotLandInTheHolyField_ButOtherSchoolsDo()
    {
        (Player player, _) = CreatePlayer();
        int holy = UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy;
        int fire = UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire;
        Item ring = Give(player.Inventory, ResistRing);

        player.Inventory.AutoEquipItem(ring.BagSlot, ring.Slot);

        Assert.Equal(0, player.GetInt32(holy));
        Assert.Equal(7, player.GetInt32(fire));
    }

    [Fact]
    public void ACreature_IsCreatedWithItsTemplateMagicResistances_AndHolyStaysZero()
    {
        Creature mob = SpawnCreature(t => t with { Armor = 500, HolyRes = 40, FireRes = 100, NatureRes = 200, FrostRes = -30, ShadowRes = 400, ArcaneRes = 500 });

        Assert.Equal(500, mob.GetInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(0, mob.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy));
        Assert.Equal(100, mob.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
        Assert.Equal(200, mob.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Nature));
        Assert.Equal(-30, mob.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Frost));
        Assert.Equal(400, mob.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Shadow));
        Assert.Equal(500, mob.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Arcane));
    }
}
