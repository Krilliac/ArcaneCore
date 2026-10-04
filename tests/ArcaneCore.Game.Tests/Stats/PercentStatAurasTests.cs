using ArcaneCore.Game.Creatures;
using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Items;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using ArcaneCore.Game.Tests.Spells;
using ArcaneCore.Game.Tests.Threat;
using ArcaneCore.Kernel.Items;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Stats;

/// <summary>
/// The percent and flat stat auras (<see cref="PercentStatAuras"/>) through the spell system: each aura is cast, the derived field is compared with a value
/// worked by hand from the reference order <c>((BASE * BASE_PCT) + TOTAL_VALUE) * TOTAL_PCT</c> (mangos zero Unit::HandleStatModifier / StatSystem.cpp),
/// then removed, and the field must return to what the flat contributions alone give. Float products are truncated to integers as the reference does.
/// </summary>
public sealed class PercentStatAurasTests
{
    private const float Tol = 0.01f;

    private const uint TotalStrength10 = 940001;
    private const uint TotalStamina100 = 940002;
    private const uint BaseStrength50 = 940003;
    private const uint FlatStrength20 = 940004;
    private const uint AbilityStamina50 = 940005;
    private const uint HealthFlat100 = 940006;
    private const uint HealthPct10 = 940007;
    private const uint LastStand = 12976;
    private const uint BearForm = 1178;
    private const uint HealthPctMinus100 = 940008;
    private const uint EnergyFlat20 = 940009;
    private const uint EnergyPct50 = 940010;
    private const uint EnergyWrongType = 940011;
    private const uint ArmorPct10 = 940012;
    private const uint ArmorFlat100 = 940013;
    private const uint FirePct10 = 940014;
    private const uint HolyPct10 = 940015;
    private const uint BaseArmorPct20 = 940016;
    private const uint BaseArmorFlat50 = 940017;
    private const uint AttackPowerPct10 = 940018;
    private const uint RangedAttackPowerPct10 = 940019;
    private const uint AttackPowerPctMinus100 = 940020;
    private const uint Dodge3 = 940021;
    private const uint Parry2 = 940022;
    private const uint Block4 = 940023;
    private const uint ParryMinus100 = 940024;
    private const uint ShieldBlockFlat10 = 940025;
    private const uint ShieldBlockPct50 = 940026;
    private const uint TotalAgility100 = 940027;
    private const uint EnemyStrengthMinus50 = 940030;
    private const uint EnemyBaseStrength50 = 940031;
    private const uint EnemyArmorMinus50 = 940032;
    private const uint EnemyHealthPct50 = 940033;
    private const uint EnemyDodge3 = 940034;
    private const uint EnemyBaseArmorPct50 = 940035;
    private const uint EnemyEnergyPct50 = 940036;

    private const uint Chestpiece = 940100;
    private const uint Shield = 940101;

    private static readonly ItemTemplateStore s_items = new(
    [
        new() { Entry = Chestpiece, Class = 4, SubClass = 2, Name = "Test Chest", InventoryType = 5, Armor = 200, MaxDurability = 50 },
        new() { Entry = Shield, Class = 4, SubClass = 6, Name = "Test Shield", InventoryType = 14, Block = 20, Armor = 5, MaxDurability = 40 },
    ], []);

    private static SpellInfo Aura(uint id, int amount, AuraType type, int misc = 0, bool enemy = false, SpellAttributes attributes = SpellAttributes.None)
        => Spell(id, Effect(SpellEffectName.ApplyAura, amount, enemy ? SpellImplicitTarget.UnitEnemy : SpellImplicitTarget.UnitCaster, type, misc: misc)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
            Attributes = (enemy ? SpellAttributes.AuraIsDebuff : SpellAttributes.None) | attributes,
        };

    private static SpellInfo[] Spells() =>
    [
        Aura(TotalStrength10, 10, AuraType.ModTotalStatPercentage, misc: 0),
        Aura(TotalStamina100, 100, AuraType.ModTotalStatPercentage, misc: 2),
        Aura(TotalAgility100, 100, AuraType.ModTotalStatPercentage, misc: 1),
        Aura(BaseStrength50, 50, AuraType.ModPercentStat, misc: 0),
        Aura(FlatStrength20, 20, AuraType.ModStat, misc: 0),
        Aura(AbilityStamina50, 100, AuraType.ModTotalStatPercentage, misc: 2, attributes: SpellAttributes.IsAbility),
        Aura(HealthFlat100, 100, AuraType.ModIncreaseHealth),
        Aura(HealthPct10, 10, AuraType.ModIncreaseHealthPercent),
        Aura(LastStand, 300, AuraType.ModIncreaseHealth),
        Aura(BearForm, 1000, AuraType.ModIncreaseHealth),
        Aura(HealthPctMinus100, -100, AuraType.ModIncreaseHealthPercent),
        Aura(EnergyFlat20, 20, AuraType.ModIncreaseEnergy, misc: (int)PowerType.Rage),
        Aura(EnergyPct50, 50, AuraType.ModIncreaseEnergyPercent, misc: (int)PowerType.Rage),
        Aura(EnergyWrongType, 50, AuraType.ModIncreaseEnergyPercent, misc: (int)PowerType.Energy),
        Aura(ArmorPct10, 10, AuraType.ModResistancePct, misc: 1),
        Aura(ArmorFlat100, 100, AuraType.ModResistance, misc: 1),
        Aura(FirePct10, 10, AuraType.ModResistancePct, misc: 1 << (int)SpellSchool.Fire),
        Aura(HolyPct10, 10, AuraType.ModResistancePct, misc: 1 << (int)SpellSchool.Holy),
        Aura(BaseArmorPct20, 20, AuraType.ModBaseResistancePct, misc: 1),
        Aura(BaseArmorFlat50, 50, AuraType.ModBaseResistance, misc: 1),
        Aura(AttackPowerPct10, 10, AuraType.ModAttackPowerPct),
        Aura(RangedAttackPowerPct10, 10, AuraType.ModRangedAttackPowerPct),
        Aura(AttackPowerPctMinus100, -100, AuraType.ModAttackPowerPct),
        Aura(Dodge3, 3, AuraType.ModDodgePercent),
        Aura(Parry2, 2, AuraType.ModParryPercent),
        Aura(Block4, 4, AuraType.ModBlockPercent),
        Aura(ParryMinus100, -100, AuraType.ModParryPercent),
        Aura(ShieldBlockFlat10, 10, AuraType.ModShieldBlockvalue),
        Aura(ShieldBlockPct50, 50, AuraType.ModShieldBlockvaluePct),
        Aura(EnemyStrengthMinus50, -50, AuraType.ModTotalStatPercentage, misc: 0, enemy: true),
        Aura(EnemyBaseStrength50, 50, AuraType.ModPercentStat, misc: 0, enemy: true),
        Aura(EnemyArmorMinus50, -50, AuraType.ModResistancePct, misc: 1, enemy: true),
        Aura(EnemyHealthPct50, 50, AuraType.ModIncreaseHealthPercent, enemy: true),
        Aura(EnemyDodge3, 3, AuraType.ModDodgePercent, enemy: true),
        Aura(EnemyBaseArmorPct50, 50, AuraType.ModBaseResistancePct, misc: 1, enemy: true),
        Aura(EnemyEnergyPct50, 50, AuraType.ModIncreaseEnergyPercent, misc: (int)PowerType.Rage, enemy: true),
    ];

    private static SpellTestKit Kit() => new(Spells());

    private static void CastOn(SpellTestKit kit, Unit target, uint spell) => kit.System.CastSpell(target, spell, SpellCastTargets.ForSelf(), triggered: true);

    private static void SetStats(Player player, params uint[] stats)
    {
        for (int i = 0; i < stats.Length; i++)
        {
            player.SetUInt32(UpdateFields.UnitFieldStat0 + i, stats[i]);
        }
    }

    private static int Stat(Unit unit, int stat) => unit.GetInt32(UpdateFields.UnitFieldStat0 + stat);

    private static int Armor(Unit unit) => unit.GetInt32(UpdateFields.UnitFieldResistances);

    private static Item Equip(Player player, uint entry)
    {
        PlayerInventory inventory = player.Inventory;
        ItemTestData.Wire(inventory).Templates = s_items;
        Item item = ItemTestData.Give(inventory, entry);
        inventory.AutoEquipItem(item.BagSlot, item.Slot);
        Assert.Contains(inventory.Equipped, e => ReferenceEquals(e.Item, item));
        return item;
    }

    // --- registration ----------------------------------------------------------------------------

    [Fact]
    public void EveryAuraTypeOfTheLane_HasAHandler_AndItsSupportRowIsHandler()
    {
        using SpellTestKit kit = Kit();
        AuraType[] types =
        [
            AuraType.ModIncreaseHealth, AuraType.ModIncreaseEnergy, AuraType.ModParryPercent, AuraType.ModDodgePercent, AuraType.ModBlockPercent,
            AuraType.ModPercentStat, AuraType.ModBaseResistance, AuraType.ModResistancePct, AuraType.ModIncreaseEnergyPercent,
            AuraType.ModIncreaseHealthPercent, AuraType.ModTotalStatPercentage, AuraType.ModBaseResistancePct, AuraType.ModShieldBlockvaluePct,
            AuraType.ModShieldBlockvalue, AuraType.ModAttackPowerPct, AuraType.ModRangedAttackPowerPct,
        ];

        Assert.Contains(typeof(PercentStatAuras), kit.System.Modules);
        Assert.All(types, type =>
        {
            Assert.True(kit.System.HasAuraHandler(type), type.ToString());
            Assert.Equal(AuraSupportLevel.Handler, AuraSupport.Get(type).Level);
        });
    }

    // --- stats -----------------------------------------------------------------------------------

    [Fact]
    public void TotalStatPercent_ScalesTheFlatSum_InEitherOrderOfApplication()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        SetStats(player, 100);

        CastOn(kit, player, TotalStrength10);
        Assert.Equal(110, Stat(player, 0));
        CastOn(kit, player, FlatStrength20);
        Assert.Equal(132, Stat(player, 0));            // (100 + 20) * 1.1: flat first, then percent (10% of the base alone would give 130)
        Assert.Equal(20, player.GetInt32(UpdateFields.PlayerFieldPosstat0));   // the buff counter holds the flat amount only

        kit.System.RemoveAuras(player, TotalStrength10);
        Assert.Equal(120, Stat(player, 0));
        kit.System.RemoveAuras(player, FlatStrength20);
        Assert.Equal(100, Stat(player, 0));

        CastOn(kit, player, FlatStrength20);
        Assert.Equal(120, Stat(player, 0));
        CastOn(kit, player, TotalStrength10);
        Assert.Equal(132, Stat(player, 0));            // the same result when the percent comes second
        kit.System.RemoveAuras(player, FlatStrength20);
        Assert.Equal(110, Stat(player, 0));
        kit.System.RemoveAuras(player, TotalStrength10);
        Assert.Equal(100, Stat(player, 0));
    }

    [Fact]
    public void BasePercentStat_ScalesTheLevelBaseOnly_AndTotalPercentThenScalesEverything()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        SetStats(player, 110);
        player.SetInt32(UpdateFields.PlayerFieldPosstat0, 10);   // 10 of the 110 came from an item, as EquipmentStatsApplier writes it

        CastOn(kit, player, BaseStrength50);
        Assert.Equal(160, Stat(player, 0));            // 100 * 1.5 + 10: the item is not scaled by BASE_PCT
        CastOn(kit, player, TotalStrength10);
        Assert.Equal(176, Stat(player, 0));            // (100 * 1.5 + 10) * 1.1
        kit.System.RemoveAuras(player, BaseStrength50);
        Assert.Equal(121, Stat(player, 0));            // (100 + 10) * 1.1
        kit.System.RemoveAuras(player, TotalStrength10);
        Assert.Equal(110, Stat(player, 0));
    }

    [Fact]
    public void StaminaPercent_MovesTheMaximumHealthByTheStatBonusCurve_AndRemovalGivesItBack()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        SetStats(player, 0, 0, 15);
        player.MaxHealth = 100;
        player.Health = 100;

        CastOn(kit, player, TotalStamina100);

        Assert.Equal(30, Stat(player, 2));
        Assert.Equal(205u, player.MaxHealth);          // bonus(30) - bonus(15) = (20 + 10 * 10) - 15
        Assert.Equal(100u, player.Health);             // a rising maximum does not heal
        kit.System.RemoveAuras(player, TotalStamina100);
        Assert.Equal(15, Stat(player, 2));
        Assert.Equal(100u, player.MaxHealth);
    }

    [Fact]
    public void AbilityStaminaPercent_KeepsTheHealthRatio_AndAnOrdinaryOneDoesNot()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        SetStats(player, 0, 0, 15);
        player.MaxHealth = 200;
        player.Health = 100;

        CastOn(kit, player, AbilityStamina50);

        Assert.Equal(305u, player.MaxHealth);
        Assert.Equal(152u, player.Health);             // 305 * 100 / 200 in integers (HandleModTotalPercentStat)
    }

    [Fact]
    public void StatPercent_UnderAStatSystem_IsCountedOnce_WhateverUpdatesFollow_AndFollowsAnItemDelta()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        SetStats(player, 100, 50, 20);
        var system = new PlayerStatSystem();
        system.Attach(player);
        uint baseline = player.MaxHealth;
        uint Expected(int stamina) => baseline - ExperienceFormulas.HealthBonusFromStamina(20) + ExperienceFormulas.HealthBonusFromStamina((uint)stamina);

        CastOn(kit, player, TotalStamina100);
        Assert.Equal(Expected(40), player.MaxHealth);
        system.UpdateAll(player);
        system.UpdateAll(player);
        Assert.Equal(40, Stat(player, 2));
        Assert.Equal(Expected(40), player.MaxHealth);

        // An item-like +7 stamina, then the update the item hook runs: the percent scales it too.
        player.SetInt32(UpdateFields.UnitFieldStat0 + 2, Stat(player, 2) + 7);
        system.UpdateAll(player);
        Assert.Equal(54, Stat(player, 2));             // (20 + 7) * 2
        Assert.Equal(Expected(54), player.MaxHealth);

        kit.System.RemoveAuras(player, TotalStamina100);
        Assert.Equal(27, Stat(player, 2));
        Assert.Equal(Expected(27), player.MaxHealth);
        system.UpdateAll(player);
        Assert.Equal(Expected(27), player.MaxHealth);
    }

    [Fact]
    public void AgilityPercent_ReachesTheAgilityArmor_ThroughTheStatSystem()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        SetStats(player, 100, 50, 20);
        new PlayerStatSystem().Attach(player);
        Assert.Equal(100, Armor(player));              // 2 armor per agility

        CastOn(kit, player, TotalAgility100);

        Assert.Equal(100, Stat(player, 1));
        Assert.Equal(200, Armor(player));
        kit.System.RemoveAuras(player, TotalAgility100);
        Assert.Equal(50, Stat(player, 1));
        Assert.Equal(100, Armor(player));
    }

    // --- health and power ------------------------------------------------------------------------

    [Fact]
    public void IncreaseHealth_FlatThenPercent_InEitherOrder_AndTheCurrentHealthFollowsALoweredMaximum()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.Health = 1000;

        CastOn(kit, player, HealthFlat100);
        Assert.Equal(1100u, player.MaxHealth);
        CastOn(kit, player, HealthPct10);
        Assert.Equal(1210u, player.MaxHealth);         // (1000 + 100) * 1.1
        player.Health = player.MaxHealth;
        kit.System.RemoveAuras(player, HealthFlat100);
        Assert.Equal(1100u, player.MaxHealth);         // 1000 * 1.1
        Assert.Equal(1100u, player.Health);
        kit.System.RemoveAuras(player, HealthPct10);
        Assert.Equal(1000u, player.MaxHealth);
        Assert.Equal(1000u, player.Health);

        CastOn(kit, player, HealthPct10);
        Assert.Equal(1100u, player.MaxHealth);
        CastOn(kit, player, HealthFlat100);
        Assert.Equal(1210u, player.MaxHealth);         // the same when the percent comes first
        kit.System.RemoveAuras(player, HealthPct10);
        Assert.Equal(1100u, player.MaxHealth);
        kit.System.RemoveAuras(player, HealthFlat100);
        Assert.Equal(1000u, player.MaxHealth);
    }

    [Fact]
    public void LastStand_AddsTheAmountToTheCurrentHealthToo_AndRemovalNeverKillsTheUnit()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.Health = 500;

        CastOn(kit, player, LastStand);
        Assert.Equal(1300u, player.MaxHealth);
        Assert.Equal(800u, player.Health);
        kit.System.RemoveAuras(player, LastStand);
        Assert.Equal(1000u, player.MaxHealth);
        Assert.Equal(500u, player.Health);

        CastOn(kit, player, LastStand);
        player.Health = 200;                           // below the amount: the removal leaves 1 instead of killing
        kit.System.RemoveAuras(player, LastStand);
        Assert.Equal(1000u, player.MaxHealth);
        Assert.Equal(1u, player.Health);
    }

    [Fact]
    public void BearFormPassive_KeepsTheHealthPercentage_BothWays()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.Health = 500;

        CastOn(kit, player, BearForm);
        Assert.Equal(2000u, player.MaxHealth);
        Assert.Equal(1000u, player.Health);            // still 50%
        kit.System.RemoveAuras(player, BearForm);
        Assert.Equal(1000u, player.MaxHealth);
        Assert.Equal(500u, player.Health);
    }

    [Fact]
    public void MinusOneHundredPercentHealth_LeavesOneHealthAndRemovalRestoresTheMaximum()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.MaxHealth = 1000;
        player.Health = 1000;

        CastOn(kit, player, HealthPctMinus100);          // the reference stores -100% as -200% and a non-positive total reads 0

        Assert.Equal(1u, player.MaxHealth);
        Assert.Equal(1u, player.Health);
        kit.System.RemoveAuras(player, HealthPctMinus100);
        Assert.Equal(1000u, player.MaxHealth);
    }

    [Fact]
    public void IncreaseEnergy_AppliesToTheUnitsOwnPowerTypeOnly_AndRemovalUsesTheRecordedGroup()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int maxField = UpdateFields.UnitFieldMaxpower1 + (int)PowerType.Rage;
        uint baseline = player.GetUInt32(maxField);

        CastOn(kit, player, EnergyWrongType);
        Assert.Equal(baseline, player.GetUInt32(maxField));          // the aura names energy, the unit uses rage

        CastOn(kit, player, EnergyFlat20);
        Assert.Equal(baseline + 20, player.GetUInt32(maxField));
        CastOn(kit, player, EnergyPct50);
        Assert.Equal((uint)((baseline + 20) * 1.5f), player.GetUInt32(maxField));
        player.SetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage, player.GetUInt32(maxField));

        // The unit changes power type (a druid shifting); the removal still takes the rage group the apply recorded.
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Energy);
        kit.System.RemoveAuras(player, EnergyPct50);
        Assert.Equal(baseline + 20, player.GetUInt32(maxField));
        Assert.Equal(baseline + 20, player.GetUInt32(UpdateFields.UnitFieldPower1 + (int)PowerType.Rage));   // current power follows the lowered maximum
        kit.System.RemoveAuras(player, EnergyFlat20);
        Assert.Equal(baseline, player.GetUInt32(maxField));
        kit.System.RemoveAuras(player, EnergyWrongType);
        Assert.Equal(baseline, player.GetUInt32(maxField));
    }

    // --- resistances and armor -------------------------------------------------------------------

    [Fact]
    public void ResistancePercent_ScalesArmorAfterTheFlatAura_AndAFireMaskScalesFireOnly()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldResistances, 1000);
        player.SetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire, 200);

        CastOn(kit, player, ArmorPct10);
        Assert.Equal(1100, Armor(player));
        CastOn(kit, player, ArmorFlat100);
        Assert.Equal(1210, Armor(player));             // (1000 + 100) * 1.1
        Assert.Equal(100, player.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive));
        kit.System.RemoveAuras(player, ArmorPct10);
        Assert.Equal(1100, Armor(player));
        kit.System.RemoveAuras(player, ArmorFlat100);
        Assert.Equal(1000, Armor(player));

        CastOn(kit, player, FirePct10);
        Assert.Equal(220, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
        Assert.Equal(1000, Armor(player));
        CastOn(kit, player, HolyPct10);                // holy has no field in 1.12: nothing to scale, nothing to break
        Assert.Equal(0, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy));
        kit.System.RemoveAuras(player, FirePct10);
        kit.System.RemoveAuras(player, HolyPct10);
        Assert.Equal(200, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
    }

    [Fact]
    public void BaseResistancePercent_ScalesTheWornItemsArmorOnly_AndFollowsEquipmentChanges()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        var system = new PlayerStatSystem();
        system.Attach(player);
        Item chest = Equip(player, Chestpiece);
        Assert.Equal(200, Armor(player));

        CastOn(kit, player, ArmorFlat100);
        Assert.Equal(300, Armor(player));
        CastOn(kit, player, BaseArmorPct20);
        Assert.Equal(340, Armor(player));              // 200 * 1.2 + 100: the aura armor is not scaled by BASE_PCT
        CastOn(kit, player, BaseArmorFlat50);
        Assert.Equal(390, Armor(player));              // + 50 flat from MOD_BASE_RESISTANCE (TOTAL_VALUE)
        Assert.Equal(100, player.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive));   // which, unlike MOD_RESISTANCE, moves no buff counter

        // The item comes off: the hook subtracts its 200, the update rederives the percent from the new base (none).
        player.Inventory.SwapItem(chest.BagSlot, chest.Slot, InventorySlots.Bag0, InventorySlots.ItemStart);   // out of the equipment slots, into the first backpack slot
        Assert.DoesNotContain(player.Inventory.Equipped, e => ReferenceEquals(e.Item, chest));
        Assert.Equal(150, Armor(player));

        kit.System.RemoveAuras(player, BaseArmorPct20);
        kit.System.RemoveAuras(player, BaseArmorFlat50);
        kit.System.RemoveAuras(player, ArmorFlat100);
        Assert.Equal(0, Armor(player));
    }

    [Fact]
    public void ArmorPercent_ComposesWithAnItemEquippedWhileItIsActive()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        new PlayerStatSystem().Attach(player);
        CastOn(kit, player, ArmorPct10);

        Equip(player, Chestpiece);                     // the item hook adds 200 to the flat sum, the update scales it again

        Assert.Equal(220, Armor(player));
        kit.System.RemoveAuras(player, ArmorPct10);
        Assert.Equal(200, Armor(player));
    }

    // --- attack power ----------------------------------------------------------------------------

    [Fact]
    public void AttackPowerPercent_WritesTheMultiplier_AndTheDamageFieldsFollowIt()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        (Player twin, _) = kit.AddPlayer(2, 2);
        foreach (Player p in new[] { player, twin })
        {
            p.Level = 60;
            SetStats(p, 120, 80, 100, 20, 30);
            new PlayerStatSystem().Attach(p);
        }

        float minBefore = player.GetFloat(UpdateFields.UnitFieldMindamage);
        Assert.Equal(400, player.GetInt32(UpdateFields.UnitFieldAttackPower));

        CastOn(kit, player, AttackPowerPct10);

        Assert.Equal(0.1f, player.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier), 0.0001f);
        Assert.Equal(0f, player.GetFloat(UpdateFields.UnitFieldRangedAttackPowerMultiplier));
        // 400 * 1.1 = 440 total attack power: the same damage as a twin with +40 flat attack power.
        twin.SetUInt32(UpdateFields.UnitFieldAttackPowerMods, 40);
        new PlayerStatSystem().UpdateAttackPowerAndDamage(twin, ranged: false);
        Assert.True(player.GetFloat(UpdateFields.UnitFieldMindamage) > minBefore);
        Assert.Equal(twin.GetFloat(UpdateFields.UnitFieldMindamage), player.GetFloat(UpdateFields.UnitFieldMindamage), Tol);
        Assert.Equal(twin.GetFloat(UpdateFields.UnitFieldMaxdamage), player.GetFloat(UpdateFields.UnitFieldMaxdamage), Tol);

        kit.System.RemoveAuras(player, AttackPowerPct10);
        Assert.Equal(0f, player.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier));
        Assert.Equal(minBefore, player.GetFloat(UpdateFields.UnitFieldMindamage), Tol);
    }

    [Fact]
    public void AttackPowerPercent_StacksMultiplicatively_AndMinusOneHundredPercentLeavesNoAttackPower()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        SetStats(player, 120, 80, 100, 20, 30);
        new PlayerStatSystem().Attach(player);

        CastOn(kit, player, AttackPowerPct10);
        CastOn(kit, player, AttackPowerPct10);          // a second cast of the same spell refreshes it: still one aura
        Assert.Equal(0.1f, player.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier), 0.0001f);

        CastOn(kit, player, AttackPowerPctMinus100);    // stored as -200%: the multiplier is 1.1 * -1, read as 0 -> field -1
        Assert.Equal(-1f, player.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier));
        Assert.Equal(0f, StatFormulas.TotalAttackPower(400, 0, 0, player.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier)));

        kit.System.RemoveAuras(player, AttackPowerPctMinus100);
        kit.System.RemoveAuras(player, AttackPowerPct10);
        Assert.Equal(0f, player.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier));
    }

    [Fact]
    public void RangedAttackPowerPercent_WritesTheRangedMultiplier_AndWandUsersAreSkipped()
    {
        using SpellTestKit kit = Kit();
        (Player warrior, _) = kit.AddPlayer(1);
        Player mage = TestPlayers.Add(kit, 3, Class.Mage, PowerType.Mana);

        CastOn(kit, warrior, RangedAttackPowerPct10);
        CastOn(kit, mage, RangedAttackPowerPct10);

        Assert.Equal(0.1f, warrior.GetFloat(UpdateFields.UnitFieldRangedAttackPowerMultiplier), 0.0001f);
        Assert.Equal(0f, warrior.GetFloat(UpdateFields.UnitFieldAttackPowerMultiplier));
        Assert.Equal(0f, mage.GetFloat(UpdateFields.UnitFieldRangedAttackPowerMultiplier));   // CLASSMASK_WAND_USERS
        kit.System.RemoveAuras(warrior, RangedAttackPowerPct10);
        kit.System.RemoveAuras(mage, RangedAttackPowerPct10);
        Assert.Equal(0f, warrior.GetFloat(UpdateFields.UnitFieldRangedAttackPowerMultiplier));
    }

    // --- dodge, parry, block, shield block value ---------------------------------------------------

    [Fact]
    public void DodgeParryBlockPercent_AddToThePercentagesOfAStatSystem_AndRemovalTakesThemBack()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        player.StatState.SetCanParry(true);
        player.StatState.SetCanBlock(true);
        new PlayerStatSystem().Attach(player);
        float dodge = player.GetFloat(UpdateFields.PlayerDodgePercentage);
        float parry = player.GetFloat(UpdateFields.PlayerParryPercentage);
        float block = player.GetFloat(UpdateFields.PlayerBlockPercentage);

        CastOn(kit, player, Dodge3);
        CastOn(kit, player, Parry2);
        CastOn(kit, player, Block4);

        Assert.Equal(dodge + 3, player.GetFloat(UpdateFields.PlayerDodgePercentage), Tol);
        Assert.Equal(parry + 2, player.GetFloat(UpdateFields.PlayerParryPercentage), Tol);
        Assert.Equal(block + 4, player.GetFloat(UpdateFields.PlayerBlockPercentage), Tol);

        CastOn(kit, player, ParryMinus100);              // a debuff cannot take the percentage below 0
        Assert.Equal(0f, player.GetFloat(UpdateFields.PlayerParryPercentage));

        kit.System.RemoveAuras(player, ParryMinus100);
        kit.System.RemoveAuras(player, Dodge3);
        kit.System.RemoveAuras(player, Parry2);
        kit.System.RemoveAuras(player, Block4);
        Assert.Equal(dodge, player.GetFloat(UpdateFields.PlayerDodgePercentage), Tol);
        Assert.Equal(parry, player.GetFloat(UpdateFields.PlayerParryPercentage), Tol);
        Assert.Equal(block, player.GetFloat(UpdateFields.PlayerBlockPercentage), Tol);
    }

    [Fact]
    public void DodgePercentApplied_BeforeTheStatSystemAttaches_IsIncludedByTheFirstRecompute()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        var system = new PlayerStatSystem();
        system.Attach(player);
        float dodge = player.GetFloat(UpdateFields.PlayerDodgePercentage);
        (Player other, _) = kit.AddPlayer(2, 2);
        other.Level = 60;
        CastOn(kit, other, Dodge3);
        Assert.Equal(3f, other.StatState.DodgeAuraBonus);
        Assert.Equal(0f, other.GetFloat(UpdateFields.PlayerDodgePercentage));    // no stat system, no field

        system.Attach(other);

        Assert.Equal(dodge + 3, other.GetFloat(UpdateFields.PlayerDodgePercentage), Tol);
    }

    [Fact]
    public void ShieldBlockValue_AddsTheFlatAuraBeforeTheMultiplier()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.Level = 60;
        SetStats(player, 120);
        var system = new PlayerStatSystem();
        system.Attach(player);
        Equip(player, Shield);
        Assert.Equal(25u, system.ShieldBlockValue(player));   // 20 (shield) + 120 / 20 - 1

        CastOn(kit, player, ShieldBlockFlat10);
        Assert.Equal(35u, system.ShieldBlockValue(player));
        CastOn(kit, player, ShieldBlockPct50);
        Assert.Equal(52u, system.ShieldBlockValue(player));   // 35 * 1.5 = 52.5, truncated
        kit.System.RemoveAuras(player, ShieldBlockPct50);
        Assert.Equal(35u, system.ShieldBlockValue(player));
        kit.System.RemoveAuras(player, ShieldBlockFlat10);
        Assert.Equal(25u, system.ShieldBlockValue(player));
    }

    // --- other units -------------------------------------------------------------------------------

    [Fact]
    public void ACreature_TakesTheTotalPercentAuras_AndIgnoresThePlayerOnlyOnes()
    {
        using var arena = new ThreatArena(Spells());
        Creature wolf = arena.Wolf;
        wolf.SetInt32(UpdateFields.UnitFieldStat0, 100);
        wolf.SetInt32(UpdateFields.UnitFieldResistances, 800);
        uint health = wolf.MaxHealth;

        arena.Cast(arena.Tank, wolf, EnemyStrengthMinus50);
        arena.Cast(arena.Tank, wolf, EnemyArmorMinus50);
        arena.Cast(arena.Tank, wolf, EnemyHealthPct50);
        arena.Cast(arena.Tank, wolf, EnemyBaseStrength50);       // BASE_PCT stat and base resistance are player-only auras
        arena.Cast(arena.Tank, wolf, EnemyBaseArmorPct50);
        arena.Cast(arena.Tank, wolf, EnemyDodge3);
        arena.Cast(arena.Tank, wolf, EnemyEnergyPct50);          // the wolf has no rage pool of its own: nothing to scale

        Assert.Equal(50, Stat(wolf, 0));
        Assert.Equal(400, Armor(wolf));
        Assert.Equal((uint)(health * 1.5f), wolf.MaxHealth);

        foreach (uint spell in new[] { EnemyStrengthMinus50, EnemyArmorMinus50, EnemyHealthPct50, EnemyBaseStrength50, EnemyBaseArmorPct50, EnemyDodge3, EnemyEnergyPct50 })
        {
            arena.Kit.System.RemoveAuras(wolf, spell);
        }

        Assert.Equal(100, Stat(wolf, 0));
        Assert.Equal(800, Armor(wolf));
        Assert.Equal(health, wolf.MaxHealth);
    }

    // --- the ledger itself -------------------------------------------------------------------------

    [Fact]
    public void PercentFactor_ReturnsToExactlyOne_AfterAnyNumberOfApplyAndRemoveCycles()
    {
        var factor = new PercentFactor();
        Assert.Equal(1.0f, factor.Value);

        float[] amounts = [7f, 13f, -9f, 33f, -41f, 3f];
        for (int cycle = 0; cycle < 500; cycle++)
        {
            foreach (float amount in amounts)
            {
                factor.Apply(amount, apply: true);
            }

            foreach (float amount in amounts.Reverse())
            {
                factor.Apply(amount, apply: false);
            }
        }

        Assert.Equal(1.0f, factor.Value);              // not 1.0000x: the product is reset when the last modifier goes
        Assert.Equal(0, factor.Count);

        factor.Apply(10, apply: false);                // a removal with nothing applied is ignored
        Assert.Equal(1.0f, factor.Value);
    }

    [Fact]
    public void PercentFactor_StoresAtOrBelowMinusOneHundredAsMinusTwoHundred()
    {
        Assert.Equal(-1.0f, PercentFactor.Multiplier(-100));
        Assert.Equal(-1.0f, PercentFactor.Multiplier(-250));
        Assert.Equal(0.5f, PercentFactor.Multiplier(-50));
        Assert.Equal(1.1f, PercentFactor.Multiplier(10));

        var factor = new PercentFactor();
        factor.Apply(10, apply: true);
        factor.Apply(10, apply: true);
        Assert.Equal(1.1f * 1.1f, factor.Value, 0.00001f);
    }

    [Fact]
    public void TheLedger_KeepsOnlyThePercentSlots_AndTheOffHandDamageDefaultsToHalf()
    {
        var ledger = new UnitModLedger();
        Assert.True(ledger.IsNeutral(UnitMods.Armor));
        Assert.Equal(1.0f, ledger.BasePct(UnitMods.Armor));
        Assert.Equal(0.5f, ledger.TotalPct(UnitMods.DamageOffHand));    // vmangos Unit.cpp:126-127

        ledger.Apply(UnitMods.Armor, UnitModifierType.TotalPct, 25, apply: true);
        Assert.Equal(1.25f, ledger.TotalPct(UnitMods.Armor));
        Assert.False(ledger.IsNeutral(UnitMods.Armor));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Apply(UnitMods.Armor, UnitModifierType.TotalValue, 5, apply: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Apply(UnitMods.End, UnitModifierType.TotalPct, 5, apply: true));
    }
}
