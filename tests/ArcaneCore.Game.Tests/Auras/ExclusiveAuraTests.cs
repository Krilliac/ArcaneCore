using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Auras;

/// <summary>
/// The Nostalrius buff stacking rule (vmangos _IsExclusiveSpellAura / Aura::CheckExclusiveWith / ExclusiveAuraCanApply / ExclusiveAuraUnapply,
/// SpellAuras.cpp:8177-8434): of the exclusive auras of one type, misc value and sign only the strongest is applied; the others wait in their
/// holders and the strongest waiting one comes back when the applied one ends.
/// </summary>
public sealed class ExclusiveAuraTests
{
    private const uint SmallStamina = 949_101;
    private const uint BigStamina = 949_102;
    private const uint AllStats = 949_103;
    private const uint PassiveStamina = 949_104;
    private const uint WellFedStamina = 949_105;
    private const uint BattleShout = 949_106;
    private const uint BlessingOfMight = 949_107;
    private const uint OtherAttackPower = 949_108;
    private const uint WeakCurse = 949_109;
    private const uint StrongCurse = 949_110;
    private const int Stamina = 2;
    private const int Strength = 0;

    private static SpellInfo Buff(uint id, AuraType type, int value, int misc = 0) =>
        Spell(id, Effect(SpellEffectName.ApplyAura, value, SpellImplicitTarget.UnitCaster, type, misc: misc)) with
        {
            Duration = new SpellDuration(60_000, 0, 60_000),
            SpellVisual = 1,
            StartRecoveryCategory = 0,
            StartRecoveryTime = 0,
        };

    private static SpellTestKit Kit() => new(
        Buff(SmallStamina, AuraType.ModStat, 10, Stamina),
        Buff(BigStamina, AuraType.ModStat, 25, Stamina),
        Buff(AllStats, AuraType.ModStat, 4, -1),
        Buff(PassiveStamina, AuraType.ModStat, 7, Stamina) with { Attributes = SpellAttributes.Passive, Duration = new SpellDuration(-1, 0, -1) },
        Buff(WellFedStamina, AuraType.ModStat, 6, Stamina) with { AttributesEx2 = (SpellAttributesEx2)0x80000000 },
        Buff(BattleShout, AuraType.ModAttackPower, 50) with { SpellFamilyName = 4, SpellFamilyFlags = 1UL << 16 },
        Buff(BlessingOfMight, AuraType.ModAttackPower, 60) with { SpellFamilyName = 10, SpellFamilyFlags = 1UL << 1 },
        Buff(OtherAttackPower, AuraType.ModAttackPower, 40),
        Buff(WeakCurse, AuraType.ModStat, -5, Strength) with { Attributes = SpellAttributes.AuraIsDebuff },
        Buff(StrongCurse, AuraType.ModStat, -12, Strength) with { Attributes = SpellAttributes.AuraIsDebuff });

    private static void Cast(SpellTestKit kit, Player player, uint spell)
        => Assert.Equal(SpellCastResult.CastOk, kit.System.CastSpell(player, spell, SpellCastTargets.ForSelf(), triggered: true));

    private static int Stat(Unit unit, int stat) => unit.GetInt32(UpdateFields.UnitFieldStat0 + stat);

    private static SpellAura AuraOf(SpellTestKit kit, Player player, uint spell)
        => kit.System.GetAuras(player).Single(h => h.Spell.Id == spell).Auras[0]!;

    [Fact]
    public void TwoStaminaBuffs_OnlyTheStrongerApplies_AndTheWeakerComesBackWhenItEnds()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int baseStamina = Stat(player, Stamina);

        Cast(kit, player, SmallStamina);
        Assert.Equal(baseStamina + 10, Stat(player, Stamina));

        Cast(kit, player, BigStamina);
        Assert.Equal(baseStamina + 25, Stat(player, Stamina));
        Assert.False(AuraOf(kit, player, SmallStamina).IsApplied);
        Assert.True(AuraOf(kit, player, BigStamina).IsApplied);

        kit.System.RemoveAuras(player, BigStamina);
        Assert.Equal(baseStamina + 10, Stat(player, Stamina));
        Assert.True(AuraOf(kit, player, SmallStamina).IsApplied);

        kit.System.RemoveAuras(player, SmallStamina);
        Assert.Equal(baseStamina, Stat(player, Stamina));
    }

    [Fact]
    public void AWeakerBuffAfterAStrongerOne_Waits()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int baseStamina = Stat(player, Stamina);

        Cast(kit, player, BigStamina);
        Cast(kit, player, SmallStamina);

        Assert.Equal(baseStamina + 25, Stat(player, Stamina));
        Assert.False(AuraOf(kit, player, SmallStamina).IsApplied);

        kit.System.RemoveAuras(player, SmallStamina); // the waiting one leaves without touching the stat
        Assert.Equal(baseStamina + 25, Stat(player, Stamina));
    }

    [Fact]
    public void AnotherMiscValue_APassiveAndFood_StackWithTheBuff()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int baseStamina = Stat(player, Stamina);

        Cast(kit, player, BigStamina);
        Cast(kit, player, AllStats); // misc -1 (all stats) is another kind than stamina (misc 2)
        Cast(kit, player, PassiveStamina);
        Cast(kit, player, WellFedStamina);

        Assert.Equal(baseStamina + 25 + 4 + 7 + 6, Stat(player, Stamina));
    }

    [Fact]
    public void BattleShoutAndBlessingOfMight_StackWithEverything_OtherAttackPowerBuffsDoNot()
    {
        using SpellTestKit kit = Kit();
        Assert.False(ExclusiveAuras.IsExclusive(kit.System.Store.Get(BattleShout)!, 0, AuraType.ModAttackPower));
        Assert.False(ExclusiveAuras.IsExclusive(kit.System.Store.Get(BlessingOfMight)!, 0, AuraType.ModAttackPower));
        Assert.True(ExclusiveAuras.IsExclusive(kit.System.Store.Get(OtherAttackPower)!, 0, AuraType.ModAttackPower));
    }

    [Fact]
    public void TwoStrengthDebuffs_OnlyTheDeeperApplies()
    {
        using SpellTestKit kit = Kit();
        (Player player, _) = kit.AddPlayer(1);
        int baseStrength = Stat(player, Strength);

        Cast(kit, player, WeakCurse);
        Cast(kit, player, StrongCurse);
        Assert.Equal(baseStrength - 12, Stat(player, Strength));

        kit.System.RemoveAuras(player, StrongCurse);
        Assert.Equal(baseStrength - 5, Stat(player, Strength));
    }

    [Fact]
    public void TheRuleTable_FollowsVmangos()
    {
        SpellInfo slow = Buff(1, AuraType.ModDecreaseSpeed, -50);
        SpellInfo sprint = Buff(2, AuraType.ModDecreaseSpeed, 40);
        SpellInfo mortalStrike = Buff(3, AuraType.ModHealingPct, -50);
        SpellInfo wintersChill = Buff(4, AuraType.ModAttackerSpellCritChance, 2) with { SpellFamilyName = 3 };
        SpellInfo resistance = Buff(5, AuraType.ModResistance, 50);
        SpellInfo songflower = Buff(15366, AuraType.ModStat, 15, -1);

        Assert.True(ExclusiveAuras.IsExclusive(slow, 0, AuraType.ModDecreaseSpeed));
        Assert.False(ExclusiveAuras.IsExclusive(sprint, 0, AuraType.ModDecreaseSpeed));
        Assert.False(ExclusiveAuras.IsExclusive(mortalStrike, 0, AuraType.ModHealingPct));
        Assert.False(ExclusiveAuras.IsExclusive(wintersChill, 0, AuraType.ModAttackerSpellCritChance));
        Assert.False(ExclusiveAuras.IsExclusive(resistance, 0, AuraType.ModResistance));
        Assert.False(ExclusiveAuras.IsExclusive(songflower, 0, AuraType.ModStat));
    }
}