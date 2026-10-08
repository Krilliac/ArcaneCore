using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.SpellCrit;

/// <summary>
/// Shatter (vmangos Unit::GetSpellCritChance, Unit.cpp:5259-5290): an OVERRIDE_CLASS_SCRIPTS aura of misc 849, 910, 911, 912 or 913 adds 10 to 50
/// percent to the magic crit chance of the caster's spells of the aura's family against a frozen target (AURA_STATE_FROZEN, set while a frost
/// stun or root holds the target). Build 5875 is past 1.11.0, so the bonus is not limited to Frost spells.
/// </summary>
public sealed class ShatterCritTests
{
    private const uint ShatterRank1 = 11170;
    private const uint ShatterRank5 = 12985;
    private const uint FrostNovaRoot = 996_001;
    private const uint FrostStun = 996_002;
    private const uint ArcaneRoot = 996_003;
    private const uint Frostbolt = 996_004;
    private const uint Fireball = 996_005;
    private const uint ShadowBolt = 996_006;

    private const uint FamilyMage = 3;
    private const uint FamilyWarlock = 5;

    private static SpellInfo Shatter(uint id, int misc) => RuleTestSupport.Grant(id, AuraType.OverrideClassScripts, 0, misc) with { SpellFamilyName = FamilyMage };

    private static SpellTestKit NewKit(int misc = 913) => new(
        Shatter(ShatterRank5, misc),
        Shatter(ShatterRank1, 849),
        RuleTestSupport.Grant(FrostNovaRoot, AuraType.ModRoot, 0) with { School = SpellSchool.Frost },
        RuleTestSupport.Grant(FrostStun, AuraType.ModStun, 0) with { School = SpellSchool.Frost },
        RuleTestSupport.Grant(ArcaneRoot, AuraType.ModRoot, 0) with { School = SpellSchool.Arcane },
        RuleTestSupport.Magic(Frostbolt, SpellSchool.Frost) with { SpellFamilyName = FamilyMage, SpellFamilyFlags = 0x20 },
        RuleTestSupport.Magic(Fireball, SpellSchool.Fire) with { SpellFamilyName = FamilyMage, SpellFamilyFlags = 0x1 },
        RuleTestSupport.Magic(ShadowBolt, SpellSchool.Shadow) with { SpellFamilyName = FamilyWarlock, SpellFamilyFlags = 0x1 });

    private static float Crit(SpellTestKit kit, Unit caster, Unit target, uint spell)
        => new VanillaSpellCombatRules().CritChance(kit.System, caster, target, kit.Store.Get(spell)!);

    [Theory]
    [InlineData(849, 10f)]
    [InlineData(910, 20f)]
    [InlineData(911, 30f)]
    [InlineData(912, 40f)]
    [InlineData(913, 50f)]
    public void Shatter_AddsItsRankBonus_AgainstAFrozenTarget(int misc, float bonus)
    {
        using SpellTestKit kit = NewKit(misc);
        (Player mage, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5, 0);
        RuleTestSupport.Apply(kit, mage, ShatterRank5);
        float unfrozen = Crit(kit, mage, target, Frostbolt);

        RuleTestSupport.Apply(kit, target, FrostNovaRoot);

        Assert.Equal(unfrozen + bonus, Crit(kit, mage, target, Frostbolt), 3);
    }

    [Fact]
    public void Shatter_CoversEveryMageSpellAfter1_11_ButNotAnotherFamily()
    {
        using SpellTestKit kit = NewKit();
        (Player mage, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5, 0);
        RuleTestSupport.Apply(kit, mage, ShatterRank5);
        float fire = Crit(kit, mage, target, Fireball);
        float shadow = Crit(kit, mage, target, ShadowBolt);

        RuleTestSupport.Apply(kit, target, FrostStun);

        Assert.Equal(fire + 50f, Crit(kit, mage, target, Fireball), 3);
        Assert.Equal(shadow, Crit(kit, mage, target, ShadowBolt), 3);
    }

    [Fact]
    public void Shatter_NeedsTheTargetFrozen_NotMerelyRooted()
    {
        using SpellTestKit kit = NewKit();
        (Player mage, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5, 0);
        RuleTestSupport.Apply(kit, mage, ShatterRank5);
        float unfrozen = Crit(kit, mage, target, Frostbolt);

        RuleTestSupport.Apply(kit, target, ArcaneRoot);
        Assert.Equal(unfrozen, Crit(kit, mage, target, Frostbolt), 3);

        // The client-visible state alone (UNIT_FIELD_AURASTATE bit AURA_STATE_FROZEN - 1) counts too.
        target.SetUInt32(UpdateFields.UnitFieldAurastate, 1u << ((int)AuraState.Frozen - 1));
        Assert.Equal(unfrozen + 50f, Crit(kit, mage, target, Frostbolt), 3);
    }

    [Fact]
    public void Shatter_TheRankOneAura_AddsTenPercent()
    {
        using SpellTestKit kit = NewKit();
        (Player mage, _) = kit.AddPlayer(1);
        (Player target, _) = kit.AddPlayer(2, 5, 0);
        RuleTestSupport.Apply(kit, mage, ShatterRank1);
        float unfrozen = Crit(kit, mage, target, Frostbolt);

        RuleTestSupport.Apply(kit, target, FrostNovaRoot);

        Assert.Equal(unfrozen + 10f, Crit(kit, mage, target, Frostbolt), 3);
    }
}
