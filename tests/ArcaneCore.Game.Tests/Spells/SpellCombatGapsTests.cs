using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Protocol;
using Xunit;

namespace ArcaneCore.Game.Tests.Spells;

/// <summary>
/// Y87: SPELL_CUSTOM_IGNORE_ARMOR (vmangos SpellCaster.cpp:1283) and the ranged-slot +500 ms of a spell whose cast time row is the all-zero
/// "instant" row 1 (SpellEntry.cpp:477 looks the row up, :480-481 returns 0 only when it is missing, :511-512 adds 500 otherwise).
/// </summary>
public sealed class SpellCombatGapsTests
{
    private const uint PlainStrike = 989_500;
    private const uint IgnoringStrike = 989_501;
    private const int VictimArmor = 3750;

    private static uint Dealt(uint spellId)
    {
        using var kit = new SpellTestKit(
            RuleTestKit.Magic(PlainStrike) with { School = SpellSchool.Normal, DamageClass = SpellDamageClass.Melee },
            RuleTestKit.Magic(IgnoringStrike) with { School = SpellSchool.Normal, DamageClass = SpellDamageClass.Melee, CustomFlags = SpellInfo.CustomIgnoreArmor });
        (Player attacker, _) = kit.AddPlayer(1);
        (Player victim, _) = kit.AddPlayer(2, 2); // faces away: no block
        attacker.Level = 60;
        victim.Level = 60;
        victim.MaxHealth = 100_000;
        victim.Health = 100_000;
        victim.SetInt32(UpdateFields.UnitFieldResistances, VictimArmor);
        kit.System.CombatRules = new VanillaSpellCombatRules();
        return kit.System.DealDirectDamage(attacker, victim, kit.Store.Get(spellId)!, 100, allowCrit: false).Dealt;
    }

    [Fact]
    public void APhysicalSpell_IsReducedByArmor()
    {
        Assert.Equal(59u, Dealt(PlainStrike)); // 3750 armor at level 60, as ArmorPenetrationTests pins
    }

    [Fact]
    public void APhysicalSpellWithTheIgnoreArmorFlag_SkipsArmor()
    {
        Assert.Equal(100u, Dealt(IgnoringStrike));
    }

    [Fact]
    public void AnAllZeroCastTimeRow_StillTakesTheRangedSlotHalfSecond()
    {
        const SpellAttributes shot = SpellAttributes.UsesRangedSlot | SpellAttributes.IsAbility;
        var withRow = new SpellInfo { Attributes = shot, HasCastTimeEntry = true }; // Throw / Multi-Shot: CastingTimeIndex 1 = 0 ms
        var withoutRow = new SpellInfo { Attributes = shot, HasCastTimeEntry = false };
        var handBuilt = new SpellInfo { Attributes = shot };

        Assert.Equal(500, withRow.GetCastTime(60));
        Assert.Equal(0, withRow.GetCastTime(60, autoRepeat: true));
        Assert.Equal(0, withoutRow.GetCastTime(60));
        Assert.Equal(0, handBuilt.GetCastTime(60));
    }

    [Fact]
    public void AnExplicitlyRowlessSpell_IsInstantWhateverItsCastTimeField_NoFactoryBuildsThatState()
    {
        var spell = new SpellInfo { CastTime = new SpellCastTime(2000, 0, 0), Attributes = SpellAttributes.UsesRangedSlot, HasCastTimeEntry = false };
        Assert.Equal(0, spell.GetCastTime(60));
    }
}
