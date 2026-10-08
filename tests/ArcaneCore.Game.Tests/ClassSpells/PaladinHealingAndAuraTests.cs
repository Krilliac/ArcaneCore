using ArcaneCore.Game.Entities;
using ArcaneCore.Game.Spells;
using ArcaneCore.Game.Spells.Casters.Bonus;
using ArcaneCore.Game.Spells.Paladin;
using ArcaneCore.Game.Spells.Scripts;
using ArcaneCore.Game.Tests.SpellRules;
using ArcaneCore.Game.Tests.Spells;
using Xunit;
using static ArcaneCore.Game.Tests.Spells.SpellTestKit;

namespace ArcaneCore.Game.Tests.ClassSpells;

/// <summary>
/// Holy Light and Flash of Light (script effects casting 19968 / 19993: mangos-classic SpellEffects.cpp:4151-4171, vmangos :4485-4500), Blessing of
/// Light (vmangos Unit.cpp:5364-5378) and SPELL_AURA_MOD_RESISTANCE_EXCLUSIVE (vmangos SpellAuras.cpp:4503-4549). Spell ids, families, flags, icons
/// and the Blessing of Light visual follow the build 5875 rows.
/// </summary>
public sealed class PaladinHealingAndAuraTests : IDisposable
{
    private const uint HolyLight = 635;
    private const uint FlashOfLight = 19750;
    private const uint BlessingOfLight = 19977;
    private const uint ShadowAura1 = 19876;
    private const uint ShadowAura2 = 19895;
    private const uint ShadowAura3 = 19896;

    private static SpellInfo Instant(SpellInfo spell) => spell with { StartRecoveryCategory = 0, StartRecoveryTime = 0, SpellVisual = spell.SpellVisual == 0 ? 1 : spell.SpellVisual };

    private static SpellInfo Resistance(uint id, int amount) => Instant(Spell(id,
        Effect(SpellEffectName.ApplyAura, amount, aura: AuraType.ModResistanceExclusive, misc: 1 << (int)SpellSchool.Shadow)) with
    {
        SpellFamilyName = PaladinSpells.Family,
        Duration = new SpellDuration(-1, 0, -1),
    });

    private readonly SpellTestKit _kit;
    private readonly Player _paladin;
    private readonly Player _friend;

    public PaladinHealingAndAuraTests()
    {
        _kit = new SpellTestKit(
            Instant(Spell(HolyLight, Effect(SpellEffectName.ScriptEffect, 300, SpellImplicitTarget.UnitFriend)) with
            {
                SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 1UL << 31, SpellIconId = HolyLightScript.HolyLightIcon,
                RangeIndex = 4, Range = new SpellRange(0, 40),
            }),
            Instant(Spell(FlashOfLight, Effect(SpellEffectName.ScriptEffect, 100, SpellImplicitTarget.UnitFriend)) with
            {
                SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 1UL << 30, SpellIconId = HolyLightScript.FlashOfLightIcon,
                RangeIndex = 4, Range = new SpellRange(0, 40),
            }),
            Instant(Spell(HolyLightScript.HolyLightHeal, Effect(SpellEffectName.Heal, 0, SpellImplicitTarget.UnitFriend)) with
            {
                SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 1UL << 14, SpellIconId = HolyLightScript.HolyLightIcon,
                DamageClass = SpellDamageClass.Magic, RangeIndex = 4, Range = new SpellRange(0, 40),
            }),
            Instant(Spell(HolyLightScript.FlashOfLightHeal, Effect(SpellEffectName.Heal, 0, SpellImplicitTarget.UnitFriend)) with
            {
                SpellFamilyName = PaladinSpells.Family, SpellFamilyFlags = 1UL << 13, SpellIconId = HolyLightScript.FlashOfLightIcon,
                DamageClass = SpellDamageClass.Magic, RangeIndex = 4, Range = new SpellRange(0, 40),
            }),
            Instant(Spell(BlessingOfLight,
                Effect(SpellEffectName.ApplyAura, 210, SpellImplicitTarget.UnitFriend, AuraType.Dummy),
                Effect(SpellEffectName.ApplyAura, 60, SpellImplicitTarget.UnitFriend, AuraType.Dummy)) with
            {
                Name = "Blessing of Light",
                SpellFamilyName = PaladinSpells.Family,
                SpellFamilyFlags = 0x10000000,
                SpellVisual = BlessingOfLightRules.BlessingOfLightVisual,
                Duration = new SpellDuration(300_000, 0, 300_000),
                RangeIndex = 4,
                Range = new SpellRange(0, 40),
            }),
            Resistance(ShadowAura1, 30),
            Resistance(ShadowAura2, 45),
            Resistance(ShadowAura3, 20));
        (_paladin, _) = _kit.AddPlayer(1, 0, 0);
        (_friend, _) = _kit.AddPlayer(2, 3, 0);
        SpellScriptDispatcher.Install(_kit.System, SpellScriptRegistry.Discover(typeof(SpellScriptRegistry).Assembly));
        _friend.MaxHealth = 10_000;
        _friend.Health = 1_000;
    }

    public void Dispose() => _kit.Dispose();

    private SpellCastResult Cast(Unit caster, uint spell, Unit target)
        => _kit.System.CastSpell(caster, spell, SpellCastTargets.ForUnit(target.Guid), triggered: true);

    [Fact]
    public void HolyLight_HealsThroughItsHealSpell()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, HolyLight, _friend));

        Assert.Equal(1_300u, _friend.Health);
    }

    [Fact]
    public void FlashOfLight_HealsThroughItsHealSpell()
    {
        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, FlashOfLight, _friend));

        Assert.Equal(1_100u, _friend.Health);
    }

    [Fact]
    public void BlessingOfLight_AddsItsFirstAmountToHolyLight_AndItsSecondToFlashOfLight_ThroughTheCoefficient()
    {
        // The real bonus module: a 1.5 s instant-cast stand-in gets the minimum coefficient; record the bonus both ways.
        _kit.System.AmountModifier = new SpellBonusModule(_kit.System);
        Cast(_paladin, HolyLight, _friend);
        uint holyLightAlone = _friend.Health - 1_000;
        _friend.Health = 1_000;
        Cast(_paladin, FlashOfLight, _friend);
        uint flashAlone = _friend.Health - 1_000;
        _friend.Health = 1_000;

        Assert.Equal(SpellCastResult.CastOk, Cast(_paladin, BlessingOfLight, _friend));
        Cast(_paladin, HolyLight, _friend);
        uint holyLightBlessed = _friend.Health - 1_000;
        _friend.Health = 1_000;
        Cast(_paladin, FlashOfLight, _friend);
        uint flashBlessed = _friend.Health - 1_000;

        Assert.True(holyLightBlessed > holyLightAlone, $"{holyLightBlessed} <= {holyLightAlone}");
        Assert.True(flashBlessed > flashAlone, $"{flashBlessed} <= {flashAlone}");
        Assert.True(holyLightBlessed - holyLightAlone > flashBlessed - flashAlone); // 210 versus 60 through the same coefficient
        Assert.Equal(210, BlessingOfLightRules.TakenBonus(_kit.System, _friend, _kit.Store.Get(HolyLightScript.HolyLightHeal)!));
        Assert.Equal(60, BlessingOfLightRules.TakenBonus(_kit.System, _friend, _kit.Store.Get(HolyLightScript.FlashOfLightHeal)!));
    }

    private int Shadow => _friend.GetInt32(UpdateFields.UnitFieldResistances + (int)SpellSchool.Shadow);

    [Fact]
    public void ExclusiveResistance_OnlyTheStrongestCounts_WhateverTheOrder()
    {
        int before = Shadow;
        RuleTestSupport.Apply(_kit, _friend, ShadowAura1);
        Assert.Equal(before + 30, Shadow);

        RuleTestSupport.Apply(_kit, _friend, ShadowAura2);          // stronger: 45 replaces 30
        Assert.Equal(before + 45, Shadow);

        RuleTestSupport.Apply(_kit, _friend, ShadowAura3);          // weaker: nothing changes
        Assert.Equal(before + 45, Shadow);

        _kit.System.RemoveAuras(_friend, ShadowAura2);              // the best goes: the next best counts
        Assert.Equal(before + 30, Shadow);

        _kit.System.RemoveAuras(_friend, ShadowAura1);
        Assert.Equal(before + 20, Shadow);

        _kit.System.RemoveAuras(_friend, ShadowAura3);
        Assert.Equal(before, Shadow);
        Assert.Equal(0, _friend.GetInt32(UpdateFields.PlayerFieldResistancebuffmodspositive + (int)SpellSchool.Shadow));
    }
}
