using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Progression;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Stats;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// SPELL_AURA_MOD_STAT, MOD_RESISTANCE, MOD_ATTACK_POWER and MOD_RANGED_ATTACK_POWER after vmangos
/// Aura::HandleAuraModStat (SpellAuras.cpp:4641), HandleAuraModResistance (:4551),
/// HandleAuraModAttackPower (:5169) and HandleAuraModRangedAttackPower (:5181). Flat amounts only: the
/// percent variants are other aura types.
/// </summary>
public sealed class StatAuraModuleTests
{
    private const uint AllStats = 930201;
    private const uint Stamina = 930202;
    private const uint Intellect = 930203;
    private const uint Weaken = 930204;
    private const uint Armor = 930205;
    private const uint FireAndHoly = 930206;
    private const uint Sunder = 930207;
    private const uint BattleShout = 930208;
    private const uint DemoShout = 930209;
    private const uint RangedBoost = 930210;
    private const uint BadStat = 930211;
    private const uint FrostArmor = 930212;
    private const uint StatDebuffOnEnemy = 930213;

    [Fact]
    public void ModuleIsDiscovered_AndTheAuraTypesHaveHandlers()
    {
        using var kit = Kit();

        Assert.Contains(typeof(StatAuras), kit.System.Modules);
        Assert.True(kit.System.HasAuraHandler(AuraType.ModStat));
        Assert.True(kit.System.HasAuraHandler(AuraType.ModResistance));
        Assert.True(kit.System.HasAuraHandler(AuraType.ModAttackPower));
        Assert.True(kit.System.HasAuraHandler(AuraType.ModRangedAttackPower));
    }

    [Fact]
    public void ModStat_AllStats_RaisesEveryStatAndTheBuffFields_AndRemovalRestoresThemExactly()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int[] before = Stats(player);

        kit.System.CastSpell(player, AllStats, SpellCastTargets.ForSelf(), triggered: true);

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(before[i] + 4, player.GetInt32(UpdateFields.UnitFieldStat0 + i));
            Assert.Equal(4, player.GetInt32(UpdateFields.PlayerFieldPosstat0 + i)); // ApplyStatBuffMod, Player.h:1506
            Assert.Equal(0, player.GetInt32(UpdateFields.PlayerFieldNegstat0 + i));
        }

        // An item-like delta between apply and remove survives (the contributions compose).
        player.SetInt32(UpdateFields.UnitFieldStat0, player.GetInt32(UpdateFields.UnitFieldStat0) + 10);
        kit.System.RemoveAuras(player, AllStats);

        Assert.Equal(before[0] + 10, player.GetInt32(UpdateFields.UnitFieldStat0));
        for (int i = 1; i < 5; i++)
        {
            Assert.Equal(before[i], player.GetInt32(UpdateFields.UnitFieldStat0 + i));
        }

        for (int i = 0; i < 5; i++)
        {
            Assert.Equal(0, player.GetInt32(UpdateFields.PlayerFieldPosstat0 + i));
        }
    }

    [Fact]
    public void ModStat_NegativeAmount_UsesTheNegativeBuffField()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);
        int strength = player.GetInt32(UpdateFields.UnitFieldStat0);

        kit.System.CastSpell(enemy, Weaken, SpellCastTargets.ForUnit(player.Guid), triggered: true);

        Assert.Equal(strength - 6, player.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(-6, player.GetInt32(UpdateFields.PlayerFieldNegstat0));
        Assert.Equal(0, player.GetInt32(UpdateFields.PlayerFieldPosstat0));

        kit.System.RemoveAuras(player, Weaken);
        Assert.Equal(strength, player.GetInt32(UpdateFields.UnitFieldStat0));
        Assert.Equal(0, player.GetInt32(UpdateFields.PlayerFieldNegstat0));
    }

    [Fact]
    public void ModStat_Stamina_AddsTheHealthBonusOfThePlayersStaminaCurve()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 2, 15);
        player.MaxHealth = 100;
        player.Health = 100;

        kit.System.CastSpell(player, Stamina, SpellCastTargets.ForSelf(), triggered: true);

        // Player::GetHealthBonusFromStamina: the first 20 stamina give 1 health each, the rest 10 (StatSystem.cpp:134-141):
        // bonus(25) - bonus(15) = (20 + 50) - 15.
        Assert.Equal(25, player.GetInt32(UpdateFields.UnitFieldStat0 + 2));
        Assert.Equal(155u, player.MaxHealth);
        Assert.Equal(100u, player.Health); // a rising maximum does not heal

        kit.System.RemoveAuras(player, Stamina);
        Assert.Equal(100u, player.MaxHealth);
        Assert.Equal(100u, player.Health);
    }

    [Fact]
    public void ModStat_Stamina_RemovalClampsCurrentHealthToTheNewMaximum()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 2, 25);
        player.MaxHealth = 200;
        player.Health = 200;

        kit.System.CastSpell(player, Stamina, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(300u, player.MaxHealth); // bonus(35) - bonus(25) = 170 - 70
        player.Health = player.MaxHealth;
        kit.System.RemoveAuras(player, Stamina);

        Assert.Equal(200u, player.MaxHealth);
        Assert.Equal(200u, player.Health); // Unit::SetMaxHealth lowers current health with the maximum
    }

    [Fact]
    public void ModStat_Intellect_AddsManaForAManaPlayer_AndNothingForARagePlayer()
    {
        using var kit = Kit();
        Player mage = AddCaster(kit, 3, Class.Mage, PowerType.Mana);
        (Player warrior, _) = kit.AddPlayer(1);
        mage.SetInt32(UpdateFields.UnitFieldStat0 + 3, 15);
        mage.SetUInt32(UpdateFields.UnitFieldMaxpower1, 300);
        mage.SetUInt32(UpdateFields.UnitFieldPower1, 300);
        warrior.SetInt32(UpdateFields.UnitFieldStat0 + 3, 15);
        uint warriorMaxMana = warrior.GetUInt32(UpdateFields.UnitFieldMaxpower1);

        kit.System.CastSpell(mage, Intellect, SpellCastTargets.ForSelf(), triggered: true);
        kit.System.CastSpell(warrior, Intellect, SpellCastTargets.ForSelf(), triggered: true);

        // Player::GetManaBonusFromIntellect: the first 20 intellect give 1 mana each, the rest 15 (StatSystem.cpp:143-150):
        // bonus(25) - bonus(15) = (20 + 75) - 15 = 80.
        Assert.Equal(380u, mage.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        Assert.Equal(warriorMaxMana, warrior.GetUInt32(UpdateFields.UnitFieldMaxpower1));

        kit.System.RemoveAuras(mage, Intellect);
        Assert.Equal(300u, mage.GetUInt32(UpdateFields.UnitFieldMaxpower1));
    }

    [Fact]
    public void ModStat_Intellect_UpdatesLatentManaPoolWhenBaseManaExists_RegardlessOfCurrentPowerType()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        player.SetByte(UpdateFields.UnitFieldBytes0, 3, (byte)PowerType.Energy);
        player.SetUInt32(UpdateFields.UnitFieldBaseMana, 1);
        player.SetUInt32(UpdateFields.UnitFieldMaxpower1, 100);
        player.SetUInt32(UpdateFields.UnitFieldPower1, 100);
        player.SetInt32(UpdateFields.UnitFieldStat0 + 3, 20);
        var maintainer = new PlayerStatSystem();
        maintainer.Attach(player);
        maintainer.UpdateAll(player);
        Assert.Equal(120u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1)); // initial intellect-20 bonus

        kit.System.CastSpell(player, Intellect, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(270u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        maintainer.UpdateAll(player); // attached refresh is idempotent
        Assert.Equal(270u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
        kit.System.RemoveAuras(player, Intellect);
        Assert.Equal(120u, player.GetUInt32(UpdateFields.UnitFieldMaxpower1));
    }

    /// <summary>
    /// The stats lane keeps a ledger of the stamina and intellect bonus already inside the maximum fields and
    /// recomputes it from the TOTAL stat on every item and level update (vmangos recomputes max health from the stat
    /// group, StatSystem.cpp:165-192; auras only modify the stat group). An aura that moved the maximum on its own, without
    /// the ledger, was counted twice by the next update and again, negatively, after removal.
    /// </summary>
    [Fact]
    public void ModStat_Stamina_IsCountedOnce_ByTheStatsLanesRecomputation_WhateverUpdatesRunAfterwards()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        var system = new PlayerStatSystem();
        system.Attach(player); // syncs the ledger with the stat fields as they are
        uint baseline = player.MaxHealth;
        int baseStamina = player.GetInt32(UpdateFields.UnitFieldStat0 + 2);
        uint baseBonus = ExperienceFormulas.HealthBonusFromStamina((uint)baseStamina);
        uint Expected(int stamina) => baseline - baseBonus + ExperienceFormulas.HealthBonusFromStamina((uint)stamina);

        kit.System.CastSpell(player, Stamina, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(Expected(baseStamina + 10), player.MaxHealth);
        system.UpdateAll(player);                                  // what an item equip or a level-up runs
        Assert.Equal(Expected(baseStamina + 10), player.MaxHealth);
        system.UpdateAll(player);
        Assert.Equal(Expected(baseStamina + 10), player.MaxHealth);

        // An item-like stamina delta, then the update the item hook runs.
        player.SetInt32(UpdateFields.UnitFieldStat0 + 2, player.GetInt32(UpdateFields.UnitFieldStat0 + 2) + 7);
        system.UpdateAll(player);
        Assert.Equal(Expected(baseStamina + 17), player.MaxHealth);

        kit.System.RemoveAuras(player, Stamina);
        Assert.Equal(Expected(baseStamina + 7), player.MaxHealth);
        system.UpdateAll(player);
        Assert.Equal(Expected(baseStamina + 7), player.MaxHealth);

        // The item comes off: fully back to the baseline.
        player.SetInt32(UpdateFields.UnitFieldStat0 + 2, baseStamina);
        system.UpdateAll(player);
        Assert.Equal(baseline, player.MaxHealth);
    }

    [Fact]
    public void ModStat_Intellect_IsCountedOnce_ByTheStatsLanesRecomputation_WhateverUpdatesRunAfterwards()
    {
        using var kit = Kit();
        Player mage = AddCaster(kit, 3, Class.Mage, PowerType.Mana);
        mage.SetUInt32(UpdateFields.UnitFieldBaseMana, 100);
        mage.SetInt32(UpdateFields.UnitFieldStat0 + 3, 30);
        var system = new PlayerStatSystem();
        system.Attach(mage);
        int manaField = UpdateFields.UnitFieldMaxpower1;
        uint baseline = mage.GetUInt32(manaField);
        uint baseBonus = ExperienceFormulas.ManaBonusFromIntellect(30);
        uint Expected(int intellect) => baseline - baseBonus + ExperienceFormulas.ManaBonusFromIntellect((uint)intellect);

        kit.System.CastSpell(mage, Intellect, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(Expected(40), mage.GetUInt32(manaField));
        system.UpdateAll(mage);
        Assert.Equal(Expected(40), mage.GetUInt32(manaField));

        mage.SetInt32(UpdateFields.UnitFieldStat0 + 3, mage.GetInt32(UpdateFields.UnitFieldStat0 + 3) + 5);
        system.UpdateAll(mage);
        Assert.Equal(Expected(45), mage.GetUInt32(manaField));

        kit.System.RemoveAuras(mage, Intellect);
        Assert.Equal(Expected(35), mage.GetUInt32(manaField));
        system.UpdateAll(mage);
        Assert.Equal(Expected(35), mage.GetUInt32(manaField));

        mage.SetInt32(UpdateFields.UnitFieldStat0 + 3, 30);
        system.UpdateAll(mage);
        Assert.Equal(baseline, mage.GetUInt32(manaField));
    }

    [Fact]
    public void ModStat_MiscValueOutsideMinus2To4_IsIgnored()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int[] before = Stats(player);

        kit.System.CastSpell(player, BadStat, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(before, Stats(player));
        kit.System.RemoveAuras(player, BadStat);
        Assert.Equal(before, Stats(player));
    }

    [Fact]
    public void ModStat_OnAnotherUnit_RemovesWithTheHolderWhenTheHolderIsDispelled()
    {
        using var kit = Kit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        int before = target.GetInt32(UpdateFields.UnitFieldStat0 + 1);

        kit.System.CastSpell(caster, StatDebuffOnEnemy, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        Assert.Equal(before - 3, target.GetInt32(UpdateFields.UnitFieldStat0 + 1));

        // The holder expires: the aura is un-applied through the normal removal path.
        kit.Advance(11_000);
        Assert.Equal(before, target.GetInt32(UpdateFields.UnitFieldStat0 + 1));
    }

    [Fact]
    public void ModResistance_Armor_RaisesTheArmorField_AndTheBuffFieldOfAPlayer()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int armor = player.GetInt32(UpdateFields.UnitFieldResistances);

        kit.System.CastSpell(player, Armor, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(armor + 150, player.GetInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(150, player.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive));

        kit.System.RemoveAuras(player, Armor);
        Assert.Equal(armor, player.GetInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(0, player.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive));
    }

    [Fact]
    public void ModResistance_MaskSchools_HolyHasNoResistanceField_ButTheBuffInfoFollows()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);

        // Misc value is a school MASK: fire (4) and holy (2).
        kit.System.CastSpell(player, FireAndHoly, SpellCastTargets.ForSelf(), triggered: true);

        Assert.Equal(30, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
        // Player::UpdateResistances (StatSystem.cpp:117-126): holy is always 0 in 1.12.
        Assert.Equal(0, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Holy));
        Assert.Equal(30, player.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive + (int)SpellSchool.Fire));
        Assert.Equal(30, player.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive + (int)SpellSchool.Holy));
        Assert.Equal(0, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Frost));

        kit.System.RemoveAuras(player, FireAndHoly);
        Assert.Equal(0, player.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Fire));
        Assert.Equal(0, player.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive + (int)SpellSchool.Holy));
    }

    [Fact]
    public void ModResistance_StackingDebuff_MovesArmorBelowZero_AndRestoresIt()
    {
        using var kit = Kit();
        (Player attacker, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 2);
        target.SetInt32(UpdateFields.UnitFieldResistances, 100);

        for (int i = 0; i < 3; i++)
        {
            kit.System.CastSpell(attacker, Sunder, SpellCastTargets.ForUnit(target.Guid), triggered: true);
        }

        // Sunder Armor-shaped: -90 per stack, three stacks (re-applied as the stack grows), armor may go negative.
        Assert.Equal(100 - 270, target.GetInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(-270, target.GetInt32(UpdateFields.PlayerFieldResistancebuffmodsnegative));

        kit.System.RemoveAuras(target, Sunder);
        Assert.Equal(100, target.GetInt32(UpdateFields.UnitFieldResistances));
        Assert.Equal(0, target.GetInt32(UpdateFields.PlayerFieldResistancebuffmodsnegative));
    }

    [Fact]
    public void ModAttackPower_PositiveBuffGoesToTheLowHalf_NegativeDebuffToTheHighHalf()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        (Player enemy, _) = kit.AddPlayer(2, 2);

        kit.System.CastSpell(player, BattleShout, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal((70, 0), Halves(player, UpdateFields.UnitFieldAttackPowerMods));

        kit.System.CastSpell(enemy, DemoShout, SpellCastTargets.ForUnit(player.Guid), triggered: true);
        Assert.Equal((70, -30), Halves(player, UpdateFields.UnitFieldAttackPowerMods));

        kit.System.RemoveAuras(player, BattleShout);
        Assert.Equal((0, -30), Halves(player, UpdateFields.UnitFieldAttackPowerMods));
        kit.System.RemoveAuras(player, DemoShout);
        Assert.Equal((0, 0), Halves(player, UpdateFields.UnitFieldAttackPowerMods));
    }

    [Fact]
    public void ModRangedAttackPower_WritesTheRangedField_AndWandUsersAreSkipped()
    {
        using var kit = Kit();
        (Player warrior, _) = kit.AddPlayer(1);
        Player mage = AddCaster(kit, 3, Class.Mage, PowerType.Mana);
        Player priest = AddCaster(kit, 4, Class.Priest, PowerType.Mana);
        Player hunter = AddCaster(kit, 5, Class.Hunter, PowerType.Mana);

        foreach (Player p in new[] { warrior, mage, priest, hunter })
        {
            kit.System.CastSpell(p, RangedBoost, SpellCastTargets.ForSelf(), triggered: true);
        }

        Assert.Equal((55, 0), Halves(warrior, UpdateFields.UnitFieldRangedAttackPowerMods));
        Assert.Equal((55, 0), Halves(hunter, UpdateFields.UnitFieldRangedAttackPowerMods));
        // vmangos SpellAuras.cpp:5183: CLASSMASK_WAND_USERS (priest, mage, warlock) ignore ranged attack power.
        Assert.Equal((0, 0), Halves(mage, UpdateFields.UnitFieldRangedAttackPowerMods));
        Assert.Equal((0, 0), Halves(priest, UpdateFields.UnitFieldRangedAttackPowerMods));
        Assert.Equal((0, 0), Halves(warrior, UpdateFields.UnitFieldAttackPowerMods));

        kit.System.RemoveAuras(mage, RangedBoost);
        kit.System.RemoveAuras(warrior, RangedBoost);
        Assert.Equal((0, 0), Halves(mage, UpdateFields.UnitFieldRangedAttackPowerMods));
        Assert.Equal((0, 0), Halves(warrior, UpdateFields.UnitFieldRangedAttackPowerMods));
    }

    [Fact]
    public void ModResistance_ExpiringFrostArmor_UnappliesThroughTheNormalRemovalPath()
    {
        using var kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int armor = player.GetInt32(UpdateFields.UnitFieldResistances);

        kit.System.CastSpell(player, FrostArmor, SpellCastTargets.ForSelf(), triggered: true);
        Assert.Equal(armor + 30, player.GetInt32(UpdateFields.UnitFieldResistances));

        kit.Advance(31_000);

        Assert.Equal(armor, player.GetInt32(UpdateFields.UnitFieldResistances));
        Assert.False(kit.System.HasAura(player, FrostArmor));
    }

    private static int[] Stats(Player player)
        => [.. Enumerable.Range(0, 5).Select(i => player.GetInt32(UpdateFields.UnitFieldStat0 + i))];

    private static (int Positive, int Negative) Halves(Unit unit, int field)
    {
        uint value = unit.GetUInt32(field);
        return ((short)(value & 0xFFFF), (short)(value >> 16));
    }

    private static Player AddCaster(SpellTestKit kit, uint guid, Class cls, PowerType power)
        => TestPlayers.Add(kit, guid, cls, power);

    private static SpellTestKit Kit()
    {
        SpellInfo Buff(uint id, SpellEffectInfo effect, int duration = 30_000, bool debuff = false) => Spell(id, effect) with
        {
            Duration = new SpellDuration(duration, 0, duration),
            SpellVisual = 1,
            Attributes = debuff ? SpellAttributes.AuraIsDebuff : SpellAttributes.None,
        };

        return new SpellTestKit(
            Buff(AllStats, Effect(SpellEffectName.ApplyAura, 4, aura: AuraType.ModStat, misc: -1)),
            Buff(Stamina, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModStat, misc: 2)),
            Buff(Intellect, Effect(SpellEffectName.ApplyAura, 10, aura: AuraType.ModStat, misc: 3)),
            Buff(Weaken, Effect(SpellEffectName.ApplyAura, -6, SpellImplicitTarget.UnitEnemy, AuraType.ModStat, misc: 0), debuff: true),
            Buff(BadStat, Effect(SpellEffectName.ApplyAura, 9, aura: AuraType.ModStat, misc: 7)),
            Buff(StatDebuffOnEnemy, Effect(SpellEffectName.ApplyAura, -3, SpellImplicitTarget.UnitEnemy, AuraType.ModStat, misc: 1), duration: 10_000, debuff: true),
            Buff(Armor, Effect(SpellEffectName.ApplyAura, 150, aura: AuraType.ModResistance, misc: 1)),
            Buff(FrostArmor, Effect(SpellEffectName.ApplyAura, 30, aura: AuraType.ModResistance, misc: 1)),
            Buff(FireAndHoly, Effect(SpellEffectName.ApplyAura, 30, aura: AuraType.ModResistance, misc: (1 << (int)SpellSchool.Fire) | (1 << (int)SpellSchool.Holy))),
            Buff(Sunder, Effect(SpellEffectName.ApplyAura, -90, SpellImplicitTarget.UnitEnemy, AuraType.ModResistance, misc: 1), debuff: true) with { StackAmount = 5 },
            Buff(BattleShout, Effect(SpellEffectName.ApplyAura, 70, aura: AuraType.ModAttackPower)),
            Buff(DemoShout, Effect(SpellEffectName.ApplyAura, -30, SpellImplicitTarget.UnitEnemy, AuraType.ModAttackPower), debuff: true),
            Buff(RangedBoost, Effect(SpellEffectName.ApplyAura, 55, aura: AuraType.ModRangedAttackPower)));
    }
}
