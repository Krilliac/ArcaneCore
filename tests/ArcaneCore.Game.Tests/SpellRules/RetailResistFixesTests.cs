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
/// Retail resistance rules (the numbers come from the vmangos formula, not from measured retail): a resistance aura cannot push a school below 0 (Patch 1.9.0), a warlock cannot resist its own
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

    /// <summary>The shape that marks Hellfire (warlock family flag, icon 937, visual 5423; SpellEntry.cpp:817-823).</summary>
    private static SpellInfo AsHellfire(SpellInfo spell) => spell with { SpellFamilyName = 5, SpellFamilyFlags = 1UL << 6, SpellIconId = 937, SpellVisual = 5423 };

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
        SpellInfo self = AsHellfire(RuleTestKit.Magic(hellfireTick, SpellSchool.Fire));

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
        Assert.Equal(75, rules.RollResist(kit.System, warlock, victim, AsHellfire(RuleTestKit.Magic(1949, SpellSchool.Fire)), 100, periodic: false)); // the tick on another unit
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

    private static SpellInfo SelfTick(uint id, bool hellfire)
    {
        SpellInfo tick = SpellTestKit.Spell(id, SpellTestKit.Effect(SpellEffectName.ApplyAura, 100, SpellImplicitTarget.UnitCaster, AuraType.PeriodicDamage, amplitude: 1000)) with
        {
            School = SpellSchool.Fire,
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };
        return hellfire ? AsHellfire(tick) : tick;
    }

    private static uint HealthLostToSelfTicks(uint spellId, bool hellfire)
    {
        using var kit = new SpellTestKit(SelfTick(spellId, hellfire));
        (Player warlock, _) = kit.AddPlayer(1);
        warlock.Level = 60;
        warlock.MaxHealth = 1_000_000;
        warlock.Health = 1_000_000;
        warlock.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 300);
        kit.System.CombatRules = new VanillaSpellCombatRules();
        kit.System.Damage = new MapCombatDamageSink();
        kit.System.Random = new Random(11);
        kit.System.CastSpell(warlock, spellId, SpellCastTargets.ForSelf(), triggered: true);
        kit.Advance(40_500);
        return 1_000_000 - warlock.Health;
    }

    [Fact]
    public void HellfireTicksThroughTheRealPeriodicPath_AreNeverResisted()
    {
        uint hellfire = HealthLostToSelfTicks(1949, hellfire: true);

        Assert.Equal(0u, hellfire % 100u);
        Assert.True(hellfire >= 3_900u, $"{hellfire}"); // every tick landed at full damage (about 40 of them)
        Assert.True(HealthLostToSelfTicks(900_302, hellfire: false) < hellfire); // control: the same self-tick that is not Hellfire is resisted
    }

    [Theory]
    [InlineData(-20, true)]
    [InlineData(0, false)]
    public void APercentResistanceAura_UnderAFlatCurse_FollowsTheSameFloorAsTheResistChance(int templateResistance, bool vulnerable)
    {
        using var kit = new SpellTestKit();
        (Player caster, _) = kit.AddPlayer(1);
        caster.Level = 60;
        int fire = UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire;
        Creature mob = SpawnCreature(t => t with { FireRes = templateResistance });
        mob.SetInt32(fire, mob.GetInt32(fire) - 75); // Curse of the Elements: a flat -75
        ArcaneCore.Game.Stats.UnitModLedger.For(mob).Apply((ArcaneCore.Game.Stats.UnitMods)((int)ArcaneCore.Game.Stats.UnitMods.Armor + (int)SpellSchool.Fire),
            ArcaneCore.Game.Stats.UnitModifierType.TotalPct, 10, true); // plus a +10% resistance aura

        PercentStatAuras.RefreshResistance(mob, (int)SpellSchool.Fire);

        int total = mob.GetInt32(fire);
        float chance = VanillaSpellCombatRules.AverageResistFraction(caster, mob, SpellSchool.Fire);
        if (vulnerable)
        {
            Assert.True(total < -75, $"{total}"); // no floor: the field keeps the vulnerability, it is not clamped back to the template's -20
            Assert.Equal(Math.Max(total / 300f, -0.75f), chance, 4); // and the chance reads the very same total
        }
        else
        {
            Assert.Equal(0, total); // floored at 0
            Assert.Equal(0f, chance);
        }
    }

    [Fact]
    public void AResistanceAuraOnACreature_SurvivesAReinitialisationWithANewTemplate_AndComesOffCleanly()
    {
        int fire = UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire;
        Creature mob = SpawnCreature(t => t with { FireRes = 100 });
        Assert.Equal(100, mob.GetInt32(fire));

        mob.SetInt32(fire, mob.GetInt32(fire) - 75); // Curse of the Elements: the aura handler moves the field by its amount
        CreatureTemplate next = mob.Template with { FireRes = 40 };
        mob.ChangeTemplate(next);
        mob.InitializeFields(); // UpdateEntry / a game event override re-initialise a live creature

        Assert.Equal(40 - 75, mob.GetInt32(fire)); // the curse is still on top of the new template
        Assert.Equal(40, mob.CreateResistance((int)SpellSchool.Fire));

        mob.SetInt32(fire, mob.GetInt32(fire) + 75); // the curse expires
        Assert.Equal(40, mob.GetInt32(fire));
    }

    [Fact]
    public void ACursedCreature_ThatIsKilledAndRespawned_HasItsTemplateResistanceBack()
    {
        int fire = UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire;
        Creature mob = SpawnCreature(t => t with { FireRes = 100 });
        mob.SetInt32(fire, mob.GetInt32(fire) - 75); // cursed

        mob.SetInt32(fire, mob.GetInt32(fire) + 75); // death removes the aura: its handler takes the delta back
        mob.InitializeFields();                      // the respawn re-initialises the fields

        Assert.Equal(100, mob.GetInt32(fire));
    }
}
