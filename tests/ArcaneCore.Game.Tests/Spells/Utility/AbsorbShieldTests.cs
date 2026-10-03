using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.Spells.Utility;

/// <summary>
/// Absorb shield spell power: vmangos Aura::HandleSchoolAbsorb (SpellAuras.cpp:5750-5810) adds 10 percent of the caster's +damage (Fire Ward,
/// Frost Ward, Shadow Ward) or +healing (Power Word: Shield) to the shield, times CalculateLevelPenalty, truncated to int. Ice Barrier, Mana Shield
/// and the other shields keep their data amount. Spell data values are quoted oracle constants of the test, not read from any reference.
/// </summary>
public sealed class AbsorbShieldTests
{
    private const uint FrostWard = 963_001;
    private const uint FireWard = 963_002;
    private const uint ShadowWard = 963_003;
    private const uint IceBarrierLike = 963_004;
    private const uint PowerWordShield = 963_005;
    private const uint LowLevelFrostWard = 963_006;
    private const uint FrostDamage = 963_101;
    private const uint FireDamage = 963_102;
    private const uint ShadowDamage = 963_103;
    private const uint Healing = 963_104;
    private const uint OddFrostDamage = 963_105;
    private const uint RetailShadowWard = 963_007;
    private const uint SameIconOtherCategory = 963_008;

    private const int FrostMask = 0x10;
    private const int FireMask = 0x04;
    private const int ShadowMask = 0x20;
    private const int HolyMask = 0x02;
    private const int Mage = 3;
    private const int Warlock = 5;
    private const int Priest = 6;

    private static SpellInfo Shield(uint id, int amount, int mask, uint family, int flagBit, uint level = 0, uint icon = 0, uint category = 0) => Spell(
        id, Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.SchoolAbsorb, misc: mask)) with
    {
        Duration = new SpellDuration(30_000, 0, 30_000),
        SpellVisual = 1,
        SpellFamilyName = family,
        SpellFamilyFlags = 1UL << flagBit,
        SpellLevel = level,
        School = SchoolOf(mask),
        SpellIconId = icon,
        Category = category,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellInfo Bonus(uint id, AuraType type, int amount, int mask) => Spell(id, Effect(SpellEffectName.ApplyAura, amount, aura: type, misc: mask)) with
    {
        Duration = new SpellDuration(60_000, 0, 60_000),
        SpellVisual = 1,
        StartRecoveryCategory = 0,
        StartRecoveryTime = 0,
    };

    private static SpellSchool SchoolOf(int mask) => (SpellSchool)System.Numerics.BitOperations.Log2((uint)mask);

    private static SpellTestKit NewKit(bool install = true)
    {
        var kit = new SpellTestKit(
            Shield(FrostWard, 164, FrostMask, Mage, 8),
            Shield(FireWard, 164, FireMask, Mage, 3),
            Shield(ShadowWard, 290, ShadowMask, Warlock, 20, icon: 207, category: 56),
            // The real classic-db z2815 rows of Shadow Ward (6229, 11739, 11740, 28610): SpellFamilyName 0, icon 207, category 56.
            Shield(RetailShadowWard, 290, ShadowMask, 0, 0, icon: 207, category: 56) with { SpellFamilyFlags = 0 },
            Shield(SameIconOtherCategory, 290, ShadowMask, 0, 0, icon: 207, category: 0) with { SpellFamilyFlags = 0 },
            Shield(IceBarrierLike, 438, FrostMask, Mage, 10),
            Shield(PowerWordShield, 44, HolyMask, Priest, 0),
            Shield(LowLevelFrostWard, 164, FrostMask, Mage, 8, level: 12),
            Bonus(FrostDamage, AuraType.ModDamageDone, 200, FrostMask),
            Bonus(FireDamage, AuraType.ModDamageDone, 100, FireMask),
            Bonus(ShadowDamage, AuraType.ModDamageDone, 150, ShadowMask),
            Bonus(Healing, AuraType.ModHealingDone, 110, 0x7F),
            Bonus(OddFrostDamage, AuraType.ModDamageDone, 205, FrostMask));
        if (install)
        {
            CasterSpellModules.Register(kit.System, null);
        }

        return kit;
    }

    private static int ShieldAmount(SpellTestKit kit, Unit unit, uint spell)
        => kit.System.GetAuras(unit).Single(h => h.Spell.Id == spell).Auras.OfType<SpellAura>().Single().Amount;

    private static void Cast(SpellTestKit kit, Unit caster, uint spell)
        => kit.System.CastSpell(caster, spell, SpellCastTargets.ForSelf(), triggered: true);

    [Fact]
    public void FrostWard_GetsTenPercentOfFrostSpellDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, FrostDamage);
        Cast(kit, caster, FireDamage); // another school: does not count

        Cast(kit, caster, FrostWard);

        Assert.Equal(184, ShieldAmount(kit, caster, FrostWard)); // 164 + 0.1 * 200
    }

    [Fact]
    public void FireWard_UsesFireDamageOnly()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, FrostDamage);
        Cast(kit, caster, FireDamage);

        Cast(kit, caster, FireWard);

        Assert.Equal(174, ShieldAmount(kit, caster, FireWard)); // 164 + 0.1 * 100
    }

    [Fact]
    public void ShadowWard_IsRecognisedByFamilyIconAndCategory_AndUsesShadowDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, ShadowDamage);

        Cast(kit, caster, ShadowWard);

        Assert.Equal(305, ShieldAmount(kit, caster, ShadowWard)); // 290 + 0.1 * 150
    }

    [Fact]
    public void ShadowWard_WithTheRealDataRow_FamilyZero_StillGetsTheSpellPower()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, ShadowDamage);

        Cast(kit, caster, RetailShadowWard);

        Assert.Equal(305, ShieldAmount(kit, caster, RetailShadowWard)); // 290 + 0.1 * 150
    }

    [Fact]
    public void ShadowWardRule_DoesNotMatchAShieldWithTheIconButNotTheCategory()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, ShadowDamage);

        Cast(kit, caster, SameIconOtherCategory);

        Assert.Equal(290, ShieldAmount(kit, caster, SameIconOtherCategory));
    }

    [Fact]
    public void PowerWordShield_GetsTenPercentOfHealingNotDamage()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, Healing);
        Cast(kit, caster, FrostDamage);

        Cast(kit, caster, PowerWordShield);

        Assert.Equal(55, ShieldAmount(kit, caster, PowerWordShield)); // 44 + 0.1 * 110
    }

    [Fact]
    public void OtherShields_KeepTheirDataAmount()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, FrostDamage);

        Cast(kit, caster, IceBarrierLike);

        Assert.Equal(438, ShieldAmount(kit, caster, IceBarrierLike));
    }

    [Fact]
    public void TheLevelPenaltyScalesTheBonus()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, FrostDamage);

        Cast(kit, caster, LowLevelFrostWard);

        // spell level 12: 1 - (20 - 12) * 0.0375 = 0.7; 164 + 0.1 * 200 * 0.7 = 178.
        Assert.Equal(178, ShieldAmount(kit, caster, LowLevelFrostWard));
    }

    [Fact]
    public void TheSumIsTruncated_NotRounded()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, OddFrostDamage);

        Cast(kit, caster, FrostWard);

        Assert.Equal(184, ShieldAmount(kit, caster, FrostWard)); // 164 + 20.5 = 184.5 -> int
    }

    [Fact]
    public void WithoutTheSpellPowerModule_TheShieldIsUnchanged()
    {
        using SpellTestKit kit = NewKit(install: false);
        (Player caster, _) = kit.AddPlayer(1);
        Cast(kit, caster, FrostDamage);

        Cast(kit, caster, FrostWard);

        Assert.Equal(164, ShieldAmount(kit, caster, FrostWard));
    }

    [Fact]
    public void TheBonusShield_AbsorbsItsFullAmount_ThenBreaks()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player attacker, _) = kit.AddPlayer(2, 10, 0);
        Cast(kit, caster, FrostDamage);
        Cast(kit, caster, FrostWard);

        uint absorbed = kit.System.AbsorbDamage(attacker, caster, FrostMask, 300, null);

        Assert.Equal(184u, absorbed);
        Assert.False(kit.System.HasAura(caster, FrostWard)); // the shield breaks
        Assert.Equal(0u, kit.System.AbsorbDamage(attacker, caster, FrostMask, 50, null)); // nothing left to absorb
    }

    [Fact]
    public void AShieldOfAnotherSchool_DoesNotAbsorb()
    {
        using SpellTestKit kit = NewKit();
        (Player caster, _) = kit.AddPlayer(1);
        (Player attacker, _) = kit.AddPlayer(2, 10, 0);
        Cast(kit, caster, FrostWard);

        Assert.Equal(0u, kit.System.AbsorbDamage(attacker, caster, FireMask, 100, null));
        Assert.True(kit.System.HasAura(caster, FrostWard));
    }
}
