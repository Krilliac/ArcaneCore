using ArcaneCore.Game.Combat;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.CombatMechanics;

/// <summary>
/// The done and taken bonuses of weapon damage (vmangos SpellCaster::MeleeDamageBonusDone, SpellCaster.cpp:1295-1455; Unit::MeleeDamageBonusTaken,
/// Unit.cpp:5676-5749): a white swing rolls the weapon, adds the creature-type and attack power versus bonuses, multiplies by damage done versus,
/// then the victim's taken modifiers apply (Unit::CalculateMeleeDamage, Unit.cpp:1362-1367). The victim is a player (creature type humanoid,
/// mask 64) with no armor, facing away from the attacker, and the roll is a plain hit, so each expected value is exact.
/// </summary>
public sealed class MeleeDamageBonusTests
{
    private const uint HumanoidFlat10 = 989_900;       // 59: +10 damage against humanoids
    private const uint BeastFlat10 = 989_901;          // 59: +10 damage against beasts
    private const uint HumanoidApVersus140 = 989_902;  // 102: +140 attack power against humanoids
    private const uint MarkedMelee140 = 989_903;       // 165 on the victim: attackers' melee attack power +140
    private const uint HumanoidVersus10 = 989_904;     // 168: +10% damage against humanoids
    private const uint PhysicalTakenMinus10 = 989_905; // 87 on the victim: -10% physical damage taken
    private const uint MeleeTakenPct20 = 989_906;      // 126 on the victim: +20% melee damage taken
    private const uint MeleeTakenFlat5 = 989_907;      // 125 on the victim: +5 melee damage taken
    private const uint PhysicalTakenMinus5 = 989_908;  // 14 on the victim: -5 physical damage taken
    private const uint FireTakenMinus10 = 989_909;     // 87 on the victim, fire only
    private const uint RangedTakenPct50 = 989_910;     // 114 on the victim: +50% ranged damage taken
    private const uint WeaponStrike = 989_911;         // WEAPON_DAMAGE +0, melee class
    private const uint RangedShot = 989_912;           // WEAPON_DAMAGE +0, ranged class
    private const uint Bow = 989_950;
    private const uint Arrow = 989_951;

    private static SpellInfo WeaponSpell(uint id, SpellDamageClass damageClass, SpellAttributes attributes) =>
        Spell(id, Effect(SpellEffectName.WeaponDamage, 0, SpellImplicitTarget.UnitEnemy)) with
        {
            DamageClass = damageClass,
            Attributes = attributes,
            RangeIndex = 4,
            Range = new SpellRange(0, 35),
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        RuleTestSupport.Grant(HumanoidFlat10, AuraType.ModDamageDoneCreature, 10, misc: 64),
        RuleTestSupport.Grant(BeastFlat10, AuraType.ModDamageDoneCreature, 10, misc: 1),
        RuleTestSupport.Grant(HumanoidApVersus140, AuraType.ModMeleeAttackPowerVersus, 140, misc: 64),
        RuleTestSupport.Grant(MarkedMelee140, AuraType.MeleeAttackPowerAttackerBonus, 140),
        RuleTestSupport.Grant(HumanoidVersus10, AuraType.ModDamageDoneVersus, 10, misc: 64),
        RuleTestSupport.Grant(PhysicalTakenMinus10, AuraType.ModDamagePercentTaken, -10, misc: 1),
        RuleTestSupport.Grant(MeleeTakenPct20, AuraType.ModMeleeDamageTakenPct, 20),
        RuleTestSupport.Grant(MeleeTakenFlat5, AuraType.ModMeleeDamageTaken, 5),
        RuleTestSupport.Grant(PhysicalTakenMinus5, AuraType.ModDamageTaken, -5, misc: 1),
        RuleTestSupport.Grant(FireTakenMinus10, AuraType.ModDamagePercentTaken, -10, misc: 1 << (int)SpellSchool.Fire),
        RuleTestSupport.Grant(RangedTakenPct50, AuraType.ModRangedDamageTakenPct, 50),
        WeaponSpell(WeaponStrike, SpellDamageClass.Melee, SpellAttributes.None),
        WeaponSpell(RangedShot, SpellDamageClass.Ranged, SpellAttributes.None));

    private sealed class Scene : IDisposable
    {
        public Scene()
        {
            Kit = MeleeDamageBonusTests.Kit();
            (Attacker, _) = Kit.AddPlayer(1);
            (Victim, _) = Kit.AddPlayer(2, 2);  // faces away: no dodge, parry or block
            Attacker.Level = Victim.Level = 60;
            Combat = Attacker.Map!.Combat;
            Combat.SpellMitigation = Kit.System;
            Attacker.SetUInt32(UpdateFields.UnitFieldBaseattacktime, 2000);
            Attacker.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2000);
            foreach ((int min, int max) in new[] { (UpdateFields.UnitFieldMindamage, UpdateFields.UnitFieldMaxdamage), (UpdateFields.UnitFieldMinrangeddamage, UpdateFields.UnitFieldMaxrangeddamage) })
            {
                Attacker.SetFloat(min, 100);
                Attacker.SetFloat(max, 100);
            }

            Victim.MaxHealth = Victim.Health = 100_000;
        }

        public SpellTestKit Kit { get; }

        public Player Attacker { get; }

        public Player Victim { get; }

        public MapCombat Combat { get; }

        public void OnAttacker(uint spell) => RuleTestSupport.Apply(Kit, Attacker, spell);

        public void OnVictim(uint spell) => RuleTestSupport.Apply(Kit, Victim, spell);

        /// <summary>Give the attacker a bow and arrows, as a ranged class spell needs, and keep the ranged damage fields at 100.</summary>
        public void EquipBow()
        {
            PlayerInventory inventory = Attacker.Inventory;
            inventory.Templates = new ItemTemplateStore(
            [
                .. ItemTestData.Templates,
                new() { Entry = Bow, Class = 2, SubClass = 2, DisplayId = 300, InventoryType = 15, Delay = 2000, MaxDurability = 40, AmmoType = 2, Damages = [new ItemDamage(20, 30, 0)] },
                new() { Entry = Arrow, Class = 6, SubClass = 2, DisplayId = 5996, InventoryType = 24, Stackable = 200, Damages = [new ItemDamage(1, 1, 0)] },
            ], []);
            inventory.GuidAllocator = new ItemGuidAllocator();
            Item bow = ItemTestData.Give(inventory, Bow);
            Assert.Equal(InventoryResult.Ok, inventory.CanEquipItem(InventorySlots.NullSlot, out byte dest, bow.Template, bow, swap: false));
            inventory.RemoveItem(bow.BagSlot, bow.Slot);
            inventory.EquipItem(dest, bow);
            ItemTestData.Give(inventory, Arrow, 200);
            inventory.SetAmmo(Arrow);
            Attacker.SetUInt32(UpdateFields.UnitFieldRangedattacktime, 2000);
            Attacker.SetFloat(UpdateFields.UnitFieldMinrangeddamage, 100);
            Attacker.SetFloat(UpdateFields.UnitFieldMaxrangeddamage, 100);
        }

        /// <summary>One white swing that hits (past the 5% miss range, no crit): its damage. Fractions round down.</summary>
        public uint Swing()
        {
            var random = new ScriptedRandom { DefaultFraction = 0.999f };
            random.Ints.Enqueue(5000);
            Combat.Random = random;
            MeleeDamageInfo hit = Combat.CalculateMeleeDamage(Attacker, Victim, WeaponAttackType.BaseAttack);
            Assert.Equal(MeleeHitOutcome.Normal, hit.Outcome);
            return hit.TotalDamage;
        }

        /// <summary>Cast a weapon spell at the victim (the spell tests' neutral rules: always hits, never crits) and return the damage it dealt.</summary>
        public uint Hit(uint spell)
        {
            Victim.Health = 100_000;
            Assert.Equal(SpellCastResult.CastOk, Kit.System.CastSpell(Attacker, spell, SpellCastTargets.ForUnit(Victim.Guid), triggered: true));
            return 100_000 - Victim.Health;
        }

        public void Dispose() => Kit.Dispose();
    }

    [Fact]
    public void TheDamageBonusAuraTypes_HaveHandlers_AndTheirSupportRowsSayHandler()
    {
        using SpellTestKit kit = Kit();
        AuraType[] types =
        [
            AuraType.ModDamageTaken, AuraType.ModDamageDoneCreature, AuraType.ModDamagePercentTaken, AuraType.ModMeleeAttackPowerVersus,
            AuraType.ModRangedDamageTaken, AuraType.ModRangedDamageTakenPct, AuraType.ModMeleeDamageTaken, AuraType.ModMeleeDamageTakenPct,
            AuraType.RangedAttackPowerAttackerBonus, AuraType.ModRangedAttackPowerVersus, AuraType.MeleeAttackPowerAttackerBonus, AuraType.ModDamageDoneVersus,
        ];

        Assert.All(types, type =>
        {
            Assert.True(kit.System.HasAuraHandler(type), type.ToString());
            Assert.Equal(AuraSupportLevel.Handler, AuraSupport.Get(type).Level);
        });
    }

    [Fact]
    public void AWhiteSwing_WithoutBonuses_DealsTheWeaponRoll()
    {
        using var scene = new Scene();

        Assert.Equal(100u, scene.Swing());
    }

    [Fact]
    public void DamageDoneCreature_AddsItsAmountAgainstTheMatchingCreatureTypeOnly()
    {
        using var scene = new Scene();
        scene.OnAttacker(BeastFlat10);
        Assert.Equal(100u, scene.Swing());

        scene.OnAttacker(HumanoidFlat10);
        Assert.Equal(110u, scene.Swing());
    }

    [Fact]
    public void AttackPowerVersus_AndTheVictimsMeleeAttackerBonus_AddAttackPowerOverFourteenAtTheWeaponSpeed()
    {
        using var scene = new Scene();
        scene.OnAttacker(HumanoidApVersus140);
        Assert.Equal(120u, scene.Swing());          // 140 / 14 * 2.0

        scene.OnVictim(MarkedMelee140);
        Assert.Equal(140u, scene.Swing());
    }

    [Fact]
    public void DamageDoneVersus_MultipliesTheSwing()
    {
        using var scene = new Scene();
        scene.OnAttacker(HumanoidFlat10);
        scene.OnAttacker(HumanoidVersus10);

        Assert.Equal(121u, scene.Swing());          // (100 + 10) * 1.1
    }

    [Fact]
    public void TheVictimsTakenModifiers_ApplyFlatThenPercent()
    {
        using var scene = new Scene();
        scene.OnVictim(PhysicalTakenMinus10);
        Assert.Equal(90u, scene.Swing());

        scene.OnVictim(FireTakenMinus10);
        Assert.Equal(90u, scene.Swing());           // another school: no effect

        scene.OnVictim(MeleeTakenPct20);
        Assert.Equal(108u, scene.Swing());          // 100 * 0.9 * 1.2

        scene.OnVictim(MeleeTakenFlat5);
        scene.OnVictim(PhysicalTakenMinus5);
        Assert.Equal(108u, scene.Swing());          // (100 + 5 - 5) * 0.9 * 1.2

        scene.OnVictim(RangedTakenPct50);
        Assert.Equal(108u, scene.Swing());          // a ranged taken modifier leaves melee alone
    }

    [Fact]
    public void AWeaponSpell_TakesTheVersusAndTakenBonuses()
    {
        using var scene = new Scene();
        Assert.Equal(100u, scene.Hit(WeaponStrike));

        scene.OnAttacker(HumanoidFlat10);
        scene.OnAttacker(HumanoidVersus10);
        Assert.Equal(121u, scene.Hit(WeaponStrike));

        scene.OnVictim(MeleeTakenPct20);
        Assert.Equal(145u, scene.Hit(WeaponStrike)); // 121 * 1.2 = 145.2
    }

    [Fact]
    public void ARangedWeaponSpell_TakesTheRangedTakenPercent()
    {
        using var scene = new Scene();
        scene.EquipBow();
        Assert.Equal(100u, scene.Hit(RangedShot));

        scene.OnVictim(RangedTakenPct50);
        scene.OnVictim(MeleeTakenPct20);
        Assert.Equal(150u, scene.Hit(RangedShot));
    }
}
